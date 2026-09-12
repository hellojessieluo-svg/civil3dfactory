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

// Dispatcher.cs 已经声明了 assembly 级 CommandClass。只要存在这个特性，AutoCAD 就
// **只**扫描被显式列出的类，漏登记的类里的 CommandMethod 一个都不会注册（表现为
// "Unknown command C3DF-SERVE"，且没有任何报错）。新增命令类必须在这里补一行。
[assembly: CommandClass(typeof(Civil3DFactory.ResidentServer))]

namespace Civil3DFactory
{
    /// <summary>
    /// 常驻服务：让 accoreconsole 开一次图纸后留在内存里，后续命令走命名管道发进来。
    ///
    /// 为什么：accoreconsole 冷启动（进程启动 + Civil 3D 内核初始化 + 打开图纸）是流水线
    /// 最大的固定开销，一条 10 个节点的流水线要白付 9 次。常驻之后只付一次。
    ///
    /// 线程模型（关键）：整个服务循环就跑在 C3DF-Serve 这条命令自己的线程上，也就是文档线程。
    /// 命令执行期间文档本来就是锁住的，所以管道收到的每条请求都在合法的命令上下文里同步执行，
    /// 和 C3DF-Run 的执行环境**逐字相同**——不需要 DocumentLock、不需要跨线程调度，
    /// 也就没有 Civil 3D API 跨线程调用那一类崩溃。代价是同一时刻只服务一个请求，
    /// 这正是我们要的：一张图纸不允许被并发改。
    ///
    /// 存盘语义（和冷启动一致，故意不做自动保存）：内存里的改动**永远不会**自己写回宿主图纸，
    /// 要落盘必须显式跑 save_dwg。常驻退出时走 QUIT + _Y（丢弃），改动直接没。
    /// 状态里的 dirty 字段就是给调用方看的警告：还有没存的改动，别急着关。
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
                LogTo(logPath, "拒绝启动：没有 C3DF_PIPE 环境变量。");
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
                // maxNumberOfServerInstances = 1：同一张图纸只允许一个常驻。抢不到管道名的那个
                // 直接退出让位——两个 accoreconsole 同时按住一张图纸，写盘一定互相覆盖。
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, BufSize, BufSize);
            }
            catch (System.Exception ex)
            {
                LogTo(logPath, "管道 " + pipeName + " 已被占用，让位退出：" + ex.Message);
                return;
            }

            Environment.SetEnvironmentVariable("C3DF_RUNNER", "resident");
            LogTo(logPath, "常驻启动 pid=" + pid + " pipe=" + pipeName + " idle=" + idleSec + "s dwg=" + dwg);
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
                            LogTo(logPath, "空请求（客户端探活或断开），忽略。");
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
                                // 把收到的开头贴进错误里：请求按 \n 分帧，客户端一旦把带换行的
                                // JSON 原样塞进来，服务端只会读到半截，光看 "invalid JSON" 定不了位。
                                throw new InvalidOperationException(
                                    "请求 JSON 解析失败（收到 " + line.Length + " 字节，开头: "
                                    + line.Substring(0, Math.Min(200, line.Length)) + "）：" + pex.Message);
                            }
                            if (req == null) throw new InvalidOperationException("请求不是 JSON 对象。");
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
                                        "未知命令 '" + cmd + "'。可用: ping/status/ops/keepalive/close");
                            }

                            // 报 idle 而不是 busy：客户端读到这份快照时，这条请求已经处理完了，
                            // 服务端正要回到等连接的状态。报 busy 会让 status 永远显示"忙"。
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
                        LogTo(logPath, "请求失败 cmd=" + cmd + "：" + ex.GetType().Name + " " + ex.Message);
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
                        // 一条请求一次连接：断开后回到 WaitForConnection。管道实例本身不销毁，
                        // 名字始终挂在 \\.\pipe\ 下，所以客户端在服务端处理期间连进来只会
                        // 等（ERROR_PIPE_BUSY 由 NamedPipeClientStream.Connect 自己重试），
                        // 不会撞上"管道不存在"而误判常驻已死。
                        try { if (pipe.IsConnected) pipe.Disconnect(); } catch { }
                        WriteState(statePath, Status("idle"));
                    }

                    if (stop) { stopReason = "close"; break; }
                }
            }
            catch (System.Exception ex)
            {
                stopReason = "error";
                LogTo(logPath, "服务循环异常退出：" + ex);
            }
            finally
            {
                try { pipe.Dispose(); } catch { }
                DeleteState(statePath);
                LogTo(logPath, "常驻退出 reason=" + stopReason + " served=" + served
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
        /// 跑一份任务。审计上下文（run_id / 审计日志路径）跟着**每条请求**走，不能沿用进程启动时的
        /// 环境变量——常驻进程活得比单次运行久，否则一整天的请求全记进第一次启动的那个 run 里。
        /// </summary>
        static JsonObject RunOps(JsonObject req, Document doc, ref bool dirty)
        {
            JsonNode task = req["task"];
            if (task == null) throw new InvalidOperationException("ops 请求缺少 task 字段。");
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

        /// <summary>成功跑过任何一个 WritesDrawing 操作，就认为内存里的图脏了。</summary>
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

        // ===================== 管道读写 =====================

        /// <summary>
        /// 等一个客户端，超时就返回 false（= 闲置到点，该退了）。
        /// 用 WaitForConnectionAsync + CancellationToken 而不是同步 WaitForConnection：
        /// 同步版没有超时，闲置的常驻会永远挂着不退。
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

        /// <summary>读一行 UTF-8 JSON（\n 分帧）。读超时返回 null，防一个不发数据的客户端把常驻卡死。</summary>
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
                    if (n <= 0) break;                       // 客户端断开
                    for (int i = 0; i < n; i++)
                    {
                        if (buf[i] != (byte)'\n') continue;
                        acc.Write(buf, 0, i);
                        return Utf8().GetString(acc.ToArray()).TrimEnd('\r');
                    }
                    acc.Write(buf, 0, n);
                    if (acc.Length > MaxRequestBytes)
                        throw new InvalidOperationException("请求超过 " + MaxRequestBytes + " 字节，拒绝处理。");
                }
                return acc.Length > 0 ? Utf8().GetString(acc.ToArray()).TrimEnd('\r') : null;
            }
        }

        /// <summary>回一行 UTF-8 JSON。必须紧凑（不缩进）——缩进会带换行，把分帧打烂。</summary>
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
                return "{\"ok\":false,\"error\":\"序列化失败: "
                     + ex.Message.Replace("\"", "'").Replace("\\", "/").Replace("\n", " ") + "\"}";
            }
        }

        // ===================== 状态与日志 =====================

        /// <summary>
        /// 状态文件是客户端的探活依据：有文件 + pid 活着 = 常驻在。退出时删掉，
        /// 硬崩时残留的文件由客户端按 pid 判死后清理。
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

        // ===================== 小工具 =====================

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
