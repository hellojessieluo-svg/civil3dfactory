using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

// Dispatcher.cs already declares the assembly-level CommandClass. Once that attribute exists, AutoCAD
// scans **only** the explicitly listed classes; CommandMethods in an unlisted class are never registered (it shows up as
// "Unknown command C3DF-SERVE" with no error at all). Every new command class must be added here.
[assembly: CommandClass(typeof(Civil3DFactory.ResidentServer))]

namespace Civil3DFactory
{
    /// <summary>
    /// Resident service: accoreconsole opens the drawing once and keeps it in memory; later commands arrive over a named pipe.
    ///
    /// Why: the accoreconsole cold start (process launch + Civil 3D kernel init + opening the drawing) is the pipeline's
    /// largest fixed cost; a 10-node pipeline pays it 9 times for nothing. Resident mode pays it once.
    ///
    /// Threading model (key point): the whole service loop runs on the C3DF-Serve command's own thread, i.e. the document thread.
    /// The document is already locked while a command runs, so every request from the pipe executes synchronously in a valid command context,
    /// **exactly** the same environment as C3DF-Run: no DocumentLock, no cross-thread dispatch,
    /// hence none of the Civil 3D API cross-thread crashes. The price is serving one request at a time,
    /// which is exactly what we want: one drawing must not be modified concurrently.
    ///
    /// Save semantics (same as cold start; deliberately no auto-save): in-memory changes are **never** written back to the host drawing on their own;
    /// persisting requires an explicit save_dwg. On exit the resident runs QUIT + _Y (discard) and the changes are simply gone.
    /// The dirty field in the status is the caller's warning: there are unsaved changes, do not close yet.
    /// </summary>
    public class ResidentServer
    {
        const int DefaultIdleSec = 60;
        const int MinIdleSec = 5;
        const int MaxIdleSec = 7200;
        const int ReadTimeoutSec = 30;
        const int BufSize = 64 * 1024;
        const long MaxRequestBytes = 32L * 1024 * 1024;

        [CommandMethod("C3DF-Serve")]
        public void Serve()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            string pipeName = Environment.GetEnvironmentVariable("C3DF_PIPE");
            string statePath = Environment.GetEnvironmentVariable("C3DF_RESIDENT_STATE");
            string logPath = Environment.GetEnvironmentVariable("C3DF_RESIDENT_LOG");
            int idleSec = Clamp(ParseInt(Environment.GetEnvironmentVariable("C3DF_IDLE_SEC"), DefaultIdleSec),
                                MinIdleSec, MaxIdleSec);

            if (string.IsNullOrWhiteSpace(pipeName))
            {
                LogTo(logPath, "Refusing to start: C3DF_PIPE environment variable is missing.");
                return;
            }

            string dwg = SafeName(doc);
            int pid = Process.GetCurrentProcess().Id;
            DateTimeOffset startedAt = DateTimeOffset.Now;
            var uptime = Stopwatch.StartNew();
            bool dirty = false;
            long served = 0;
            long failed = 0;
            string stopReason = "idle_timeout";
            DateTimeOffset lastAt = startedAt;
            string lastCmd = "(none)";

            JsonObject Status(string state)
            {
                return new JsonObject
                {
                    ["schema_version"] = 1,
                    ["state"] = state,
                    ["pid"] = pid,
                    ["dwg"] = dwg,
                    ["pipe"] = pipeName,
                    ["dirty"] = dirty,
                    ["idle_sec"] = idleSec,
                    ["served"] = served,
                    ["failed"] = failed,
                    ["last_cmd"] = lastCmd,
                    ["last_at"] = lastAt.ToString("o", CultureInfo.InvariantCulture),
                    ["started_at"] = startedAt.ToString("o", CultureInfo.InvariantCulture),
                    ["uptime_ms"] = uptime.ElapsedMilliseconds,
                    ["plugin_version"] = Environment.GetEnvironmentVariable("C3DF_PLUGIN_VERSION"),
                    ["plugin_commit"] = Environment.GetEnvironmentVariable("C3DF_PLUGIN_COMMIT")
                };
            }

            NamedPipeServerStream pipe;
            try
            {
                // maxNumberOfServerInstances = 1: only one resident per drawing. Whoever fails to grab the pipe name
                // exits and yields: two accoreconsoles holding the same drawing would overwrite each other on save.
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, BufSize, BufSize);
            }
            catch (System.Exception ex)
            {
                LogTo(logPath, "Pipe " + pipeName + " is already taken; yielding and exiting: " + ex.Message);
                return;
            }

            Environment.SetEnvironmentVariable("C3DF_RUNNER", "resident");
            LogTo(logPath, "Resident started pid=" + pid + " pipe=" + pipeName + " idle=" + idleSec + "s dwg=" + dwg);
            Dispatcher.Audit(new JsonObject
            {
                ["event"] = "resident_start",
                ["pid"] = pid,
                ["pipe"] = pipeName,
                ["dwg"] = dwg,
                ["idle_sec"] = idleSec
            });

            try
            {
                WriteState(statePath, Status("idle"));
                while (true)
                {
                    if (!WaitForClient(pipe, idleSec * 1000)) { stopReason = "idle_timeout"; break; }

                    bool stop = false;
                    var sw = Stopwatch.StartNew();
                    string cmd = "(unparsed)";
                    try
                    {
                        string line = ReadLine(pipe, ReadTimeoutSec * 1000);
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            LogTo(logPath, "Empty request (client probe or disconnect), ignored.");
                        }
                        else
                        {
                            JsonObject req;
                            try
                            {
                                req = JsonNode.Parse(line) as JsonObject;
                            }
                            catch (System.Exception pex)
                            {
                                // Include the head of what was received in the error: requests are framed by \n, so once a client
                                // sends JSON containing newlines verbatim, the server reads only a fragment, and "invalid JSON" alone gives no clue.
                                throw new InvalidOperationException(
                                    "Request JSON parse failed (received " + line.Length + " bytes, starting with: "
                                    + line.Substring(0, Math.Min(200, line.Length)) + "): " + pex.Message);
                            }
                            if (req == null) throw new InvalidOperationException("Request is not a JSON object.");
                            cmd = (GetString(req, "cmd", "ping") ?? "ping").Trim().ToLowerInvariant();
                            lastCmd = cmd;
                            lastAt = DateTimeOffset.Now;
                            served++;
                            WriteState(statePath, Status("busy"));

                            JsonObject resp;
                            switch (cmd)
                            {
                                case "ping":
                                case "status":
                                    resp = Ok(cmd);
                                    break;

                                case "keepalive":
                                    idleSec = Clamp(ParseInt(GetString(req, "idle_sec", null), idleSec),
                                                    MinIdleSec, MaxIdleSec);
                                    resp = Ok(cmd);
                                    break;

                                case "close":
                                case "stop":
                                case "shutdown":
                                    stop = true;
                                    resp = Ok(cmd);
                                    break;

                                case "ops":
                                case "run":
                                    resp = RunOps(req, doc, ref dirty);
                                    break;

                                default:
                                    throw new InvalidOperationException(
                                        "Unknown command '" + cmd + "'. Available: ping/status/ops/keepalive/close");
                            }

                            // Report idle rather than busy: by the time the client reads this snapshot the request is already done
                            // and the server is about to wait for the next connection. Reporting busy would make status show "busy" forever.
                            resp["resident"] = Status(stop ? "closing" : "idle");
                            resp["ms"] = sw.ElapsedMilliseconds;
                            if (resp["ok"] == null || !resp["ok"].GetValue<bool>()) failed++;
                            WriteLine(pipe, resp);
                            try { pipe.WaitForPipeDrain(); } catch { }
                            LogTo(logPath, "cmd=" + cmd + " ms=" + sw.ElapsedMilliseconds + " dirty=" + dirty);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        failed++;
                        LogTo(logPath, "Request failed cmd=" + cmd + ": " + ex.GetType().Name + " " + ex.Message);
                        try
                        {
                            var err = new JsonObject
                            {
                                ["ok"] = false,
                                ["cmd"] = cmd,
                                ["error"] = ex.Message,
                                ["type"] = ex.GetType().Name,
                                ["ms"] = sw.ElapsedMilliseconds
                            };
                            err["resident"] = Status("busy");
                            WriteLine(pipe, err);
                            try { pipe.WaitForPipeDrain(); } catch { }
                        }
                        catch { }
                    }
                    finally
                    {
                        // One connection per request: after disconnect, back to WaitForConnection. The pipe instance itself is not destroyed;
                        // the name stays under \\.\pipe\, so a client connecting while the server is busy simply
                        // waits (ERROR_PIPE_BUSY is retried by NamedPipeClientStream.Connect itself)
                        // instead of hitting "pipe does not exist" and wrongly concluding the resident is dead.
                        try { if (pipe.IsConnected) pipe.Disconnect(); } catch { }
                        WriteState(statePath, Status("idle"));
                    }

                    if (stop) { stopReason = "close"; break; }
                }
            }
            catch (System.Exception ex)
            {
                stopReason = "error";
                LogTo(logPath, "Service loop exited with exception: " + ex);
            }
            finally
            {
                try { pipe.Dispose(); } catch { }
                DeleteState(statePath);
                LogTo(logPath, "Resident exiting reason=" + stopReason + " served=" + served
                             + " failed=" + failed + " dirty=" + dirty
                             + " uptime=" + (uptime.ElapsedMilliseconds / 1000) + "s");
                Dispatcher.Audit(new JsonObject
                {
                    ["event"] = "resident_stop",
                    ["pid"] = pid,
                    ["pipe"] = pipeName,
                    ["reason"] = stopReason,
                    ["served"] = served,
                    ["failed"] = failed,
                    ["dirty"] = dirty,
                    ["uptime_ms"] = uptime.ElapsedMilliseconds
                });
            }

            JsonObject Ok(string c)
            {
                return new JsonObject { ["ok"] = true, ["cmd"] = c };
            }
        }

        /// <summary>
        /// Runs one task. The audit context (run_id / audit log path) travels with **each request** and must not reuse the
        /// environment variables from process start: the resident outlives a single run, otherwise a whole day of requests would be logged under the first run.
        /// </summary>
        static JsonObject RunOps(JsonObject req, Document doc, ref bool dirty)
        {
            JsonNode task = req["task"];
            if (task == null) throw new InvalidOperationException("ops request has no task field.");
            string resultPath = GetString(req, "result_path", null);

            Environment.SetEnvironmentVariable("C3DF_RUN_ID", GetString(req, "run_id", null));
            Environment.SetEnvironmentVariable("C3DF_AUDIT_LOG", GetString(req, "audit_log", null));

            JsonObject result = Dispatcher.RunTask(() => task, null, resultPath, doc, false);
            if (TouchedDrawing(result)) dirty = true;

            return new JsonObject
            {
                ["ok"] = result["ok"] != null && result["ok"].GetValue<bool>(),
                ["cmd"] = "ops",
                ["result_path"] = resultPath,
                ["result"] = result
            };
        }

        /// <summary>Once any WritesDrawing op has succeeded, the in-memory drawing is considered dirty.</summary>
        static bool TouchedDrawing(JsonObject result)
        {
            try
            {
                JsonArray ops = result != null ? result["ops"] as JsonArray : null;
                if (ops == null) return false;
                foreach (JsonNode n in ops)
                {
                    JsonObject o = n as JsonObject;
                    if (o == null || o["ok"] == null || !o["ok"].GetValue<bool>()) continue;
                    if (o["op"] == null) continue;
                    OpDef def;
                    if (Ops.Registry.TryGetValue(o["op"].GetValue<string>(), out def) && def.WritesDrawing)
                        return true;
                }
            }
            catch { }
            return false;
        }

        // ===================== Pipe I/O =====================

        /// <summary>
        /// Waits for one client; returns false on timeout (= idle limit reached, time to exit).
        /// Uses WaitForConnectionAsync + CancellationToken instead of the synchronous WaitForConnection:
        /// the synchronous version has no timeout, and an idle resident would hang forever.
        /// </summary>
        static bool WaitForClient(NamedPipeServerStream pipe, int timeoutMs)
        {
            using (var cts = new CancellationTokenSource(timeoutMs))
            {
                try
                {
                    pipe.WaitForConnectionAsync(cts.Token).GetAwaiter().GetResult();
                    return true;
                }
                catch (OperationCanceledException) { return false; }
                catch (System.Exception) { return false; }
            }
        }

        /// <summary>Reads one line of UTF-8 JSON (\n framed). Returns null on read timeout so a silent client cannot freeze the resident.</summary>
        static string ReadLine(PipeStream pipe, int timeoutMs)
        {
            var buf = new byte[8192];
            using (var acc = new MemoryStream())
            using (var cts = new CancellationTokenSource(timeoutMs))
            {
                while (true)
                {
                    int n;
                    try { n = pipe.ReadAsync(buf, 0, buf.Length, cts.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { return null; }
                    if (n <= 0) break;                       // client disconnected
                    for (int i = 0; i < n; i++)
                    {
                        if (buf[i] != (byte)'\n') continue;
                        acc.Write(buf, 0, i);
                        return Utf8().GetString(acc.ToArray()).TrimEnd('\r');
                    }
                    acc.Write(buf, 0, n);
                    if (acc.Length > MaxRequestBytes)
                        throw new InvalidOperationException("Request exceeds " + MaxRequestBytes + " bytes; refused.");
                }
                return acc.Length > 0 ? Utf8().GetString(acc.ToArray()).TrimEnd('\r') : null;
            }
        }

        /// <summary>Writes one line of UTF-8 JSON. Must be compact (no indentation): indentation adds newlines and breaks the framing.</summary>
        static void WriteLine(PipeStream pipe, JsonNode node)
        {
            byte[] bytes = Utf8().GetBytes(ToLine(node) + "\n");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
        }

        static string ToLine(JsonNode node)
        {
            try
            {
                JsonSerializerOptions opts = Dispatcher.NewOpts();
                opts.WriteIndented = false;
                return node.ToJsonString(opts);
            }
            catch { }
            try { return node.ToJsonString(); }
            catch (System.Exception ex)
            {
                return "{\"ok\":false,\"error\":\"Serialization failed: "
                     + ex.Message.Replace("\"", "'").Replace("\\", "/").Replace("\n", " ") + "\"}";
            }
        }

        // ===================== Status and logging =====================

        /// <summary>
        /// The status file is the client's liveness check: file present + pid alive = resident is up. Deleted on exit;
        /// a file left behind by a hard crash is cleaned up by the client once it sees the pid is dead.
        /// </summary>
        static void WriteState(string path, JsonObject state)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, ToLine(state), new UTF8Encoding(false));
            }
            catch { }
        }

        static void DeleteState(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        static void LogTo(string path, string message)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(path,
                    DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        // ===================== Helpers =====================

        static UTF8Encoding Utf8() { return new UTF8Encoding(false); }

        static string GetString(JsonObject o, string key, string fallback)
        {
            try
            {
                if (o == null || o[key] == null) return fallback;
                JsonNode n = o[key];
                return n.GetValueKind() == JsonValueKind.String
                    ? n.GetValue<string>()
                    : n.ToJsonString().Trim('"');
            }
            catch { return fallback; }
        }

        static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        static string SafeName(Document doc)
        {
            try { return doc == null ? "(none)" : doc.Database.Filename; }
            catch { return "(unknown)"; }
        }
    }
}
