using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(Civil3DFactory.Dispatcher))]

namespace Civil3DFactory
{
    /// <summary>
    /// Console dispatcher: one accoreconsole launch runs every op listed in a task file.
    /// Convention (everything via environment variables, no command-line interaction):
    ///   C3DF_TASK   = task JSON path (required)
    ///   C3DF_RESULT = result JSON path (optional; defaults to result.json next to the task file)
    /// Task format: { "ops": [ {"op":"list_alignments"}, {"op":"export_stations","interval":50} ] }
    /// </summary>
    public class Dispatcher
    {
        [CommandMethod("C3DF-Run")]
        public void Run()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            string taskPath = Environment.GetEnvironmentVariable("C3DF_TASK");
            string resultPath = Environment.GetEnvironmentVariable("C3DF_RESULT");
            if (string.IsNullOrEmpty(resultPath) && !string.IsNullOrEmpty(taskPath))
                resultPath = Path.Combine(Path.GetDirectoryName(taskPath), "result.json");
            RunTask(() => LoadTaskFile(taskPath), taskPath, resultPath, doc, true);
        }

        static JsonNode LoadTaskFile(string taskPath)
        {
            if (string.IsNullOrEmpty(taskPath) || !File.Exists(taskPath))
                throw new InvalidOperationException("Task file not found; set the C3DF_TASK environment variable. Current value: " + (taskPath ?? "(empty)"));
            return JsonNode.Parse(File.ReadAllText(taskPath, Encoding.UTF8));
        }

        /// <summary>
        /// Runs one task and returns the result object. Cold start (C3DF-Run) and resident mode (C3DF-Serve) share this code:
        /// audit events, result structure and error handling must be identical on both paths, otherwise
        /// resident-mode results will not reconcile with the work-order ledger.
        /// <paramref name="taskPath"/> is only used for audit fields; resident mode receives tasks from the pipe and passes null.
        /// When <paramref name="resultPath"/> is empty nothing is written to disk; the result is only returned to the caller (resident mode sends it back over the pipe).
        /// </summary>
        internal static JsonObject RunTask(
            Func<JsonNode> loadTask, string taskPath, string resultPath, Document doc, bool echoToEditor)
        {
            var sw = Stopwatch.StartNew();
            DateTimeOffset runStartedAt = DateTimeOffset.Now;

            var result = new JsonObject();
            var opResults = new JsonArray();
            result["ok"] = false;
            result["ops"] = opResults;
            result["timing"] = new JsonObject
            {
                ["started_at"] = runStartedAt.ToString("o", CultureInfo.InvariantCulture)
            };
            Audit(new JsonObject
            {
                ["event"] = "run_start",
                ["runner"] = Environment.GetEnvironmentVariable("C3DF_RUNNER") ?? "unknown",
                ["dwg"] = SafeName(doc),
                ["task_path"] = taskPath,
                ["result_path"] = resultPath,
                ["plugin_version"] = Environment.GetEnvironmentVariable("C3DF_PLUGIN_VERSION"),
                ["plugin_commit"] = Environment.GetEnvironmentVariable("C3DF_PLUGIN_COMMIT")
            });

            try
            {
                JsonNode task = loadTask();
                if (task == null) throw new InvalidOperationException("Task content is empty.");
                JsonObject pipeline = task["pipeline"] as JsonObject;
                bool pipelineTask = pipeline != null;
                bool stopOnError = pipelineTask
                    && pipeline["stop_on_error"] != null
                    && pipeline["stop_on_error"].GetValue<bool>();
                string pipelineId = pipelineTask && pipeline["id"] != null
                    ? pipeline["id"].GetValue<string>()
                    : null;
                if (pipelineTask && string.IsNullOrWhiteSpace(pipelineId))
                    throw new InvalidOperationException("pipeline.id must not be empty.");

                JsonArray ops = task["ops"] as JsonArray;
                if (ops == null) throw new InvalidOperationException("Task file has no ops array.");

                result["dwg"] = SafeName(doc);
                if (pipelineTask) result["pipeline"] = pipeline.DeepClone();
                JsonObject resolved = Ops.ResolveFactoryTask(task as JsonObject, doc);
                if (resolved.Count > 0) result["resolved"] = resolved;
                result["total_ops"] = ops.Count;
                Dump(result, resultPath, null);   // Write once up front: evidence survives even a hard crash later

                bool allOk = true;
                int opIndex = 0;
                foreach (JsonNode opNode in ops)
                {
                    opIndex++;
                    var o = opNode as JsonObject;
                    var r = new JsonObject();
                    string opName = o != null && o["node"] != null
                        ? o["node"].GetValue<string>()
                        : o != null && o["op"] != null
                            ? o["op"].GetValue<string>()
                            : null;
                    string step = StationLabel(o, opIndex);
                    if (pipelineTask) NodeRegistry.EnsureRegistered(opName);
                    r["op"] = opName ?? "(missing)";
                    r["step"] = step;
                    Audit(new JsonObject
                    {
                        ["event"] = "op_start",
                        ["pipeline_id"] = pipelineId,
                        ["step"] = step,
                        ["op"] = opName ?? "(missing)",
                        ["op_index"] = opIndex,
                        ["total_ops"] = ops.Count
                    });
                    DateTimeOffset opStartedAt = DateTimeOffset.Now;
                    var opSw = Stopwatch.StartNew();
                    bool opOk = false;
                    string opError = null;
                    string opErrorType = null;
                    string opStack = null;
                    try
                    {
                        if (string.IsNullOrEmpty(opName))
                            throw new InvalidOperationException("Operation has no op field.");
                        JsonNode data = Ops.Execute(opName, o, doc);
                        r["ok"] = true;
                        r["data"] = data;
                        opOk = true;
                    }
                    catch (System.Exception ex)
                    {
                        allOk = false;
                        r["ok"] = false;
                        r["error"] = ex.Message;
                        r["type"] = ex.GetType().Name;
                        // Include the stack: AutoCAD exception messages are often a single eXxx word, impossible to locate without it
                        r["stack"] = FirstFrames(ex, 6);
                        opError = ex.Message;
                        opErrorType = ex.GetType().Name;
                        opStack = FirstFrames(ex, 6);
                    }
                    opSw.Stop();
                    DateTimeOffset opCompletedAt = DateTimeOffset.Now;
                    r["ms"] = opSw.ElapsedMilliseconds;
                    r["timing"] = new JsonObject
                    {
                        ["started_at"] = opStartedAt.ToString("o", CultureInfo.InvariantCulture),
                        ["completed_at"] = opCompletedAt.ToString("o", CultureInfo.InvariantCulture),
                        ["elapsed_ms"] = opSw.ElapsedMilliseconds
                    };
                    var completed = new JsonObject
                    {
                        ["event"] = "op_complete",
                        ["pipeline_id"] = pipelineId,
                        ["step"] = step,
                        ["op"] = opName ?? "(missing)",
                        ["op_index"] = opIndex,
                        ["ok"] = opOk,
                        ["ms"] = opSw.ElapsedMilliseconds
                    };
                    if (!opOk)
                    {
                        completed["error"] = opError;
                        completed["type"] = opErrorType;
                        completed["stack"] = opStack;
                    }
                    Audit(completed);
                    opResults.Add(r);
                    Dump(result, resultPath, null);   // Update the result file after every op
                    if (!opOk && stopOnError) break;
                }
                result["ok"] = allOk;
                result["completed_ops"] = opResults.Count;
            }
            catch (System.Exception ex)
            {
                result["ok"] = false;
                result["error"] = ex.Message;
                result["type"] = ex.GetType().Name;
                result["stack"] = FirstFrames(ex, 6);
                Audit(new JsonObject
                {
                    ["event"] = "run_error",
                    ["error"] = ex.Message,
                    ["type"] = ex.GetType().Name,
                    ["stack"] = FirstFrames(ex, 6)
                });
                // Cold start without a result path falls back to My Documents so at least some evidence remains;
                // resident mode returns results over the pipe and needs no fallback file.
                if (string.IsNullOrEmpty(resultPath) && !string.IsNullOrEmpty(taskPath))
                    resultPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "civil3dfactory.result.json");
            }

            sw.Stop();
            DateTimeOffset runCompletedAt = DateTimeOffset.Now;
            result["ms"] = sw.ElapsedMilliseconds;
            result["timing"] = new JsonObject
            {
                ["started_at"] = runStartedAt.ToString("o", CultureInfo.InvariantCulture),
                ["completed_at"] = runCompletedAt.ToString("o", CultureInfo.InvariantCulture),
                ["elapsed_ms"] = sw.ElapsedMilliseconds
            };
            Audit(new JsonObject
            {
                ["event"] = "run_complete",
                ["ok"] = result["ok"] != null && result["ok"].GetValue<bool>(),
                ["ms"] = sw.ElapsedMilliseconds,
                ["result_path"] = resultPath
            });
            Dump(result, resultPath, echoToEditor ? doc : null);
            return result;
        }

        [CommandMethod("C3DF-RunFile")]
        public void RunFile()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            var taskOpt = new Autodesk.AutoCAD.EditorInput.PromptStringOptions(
                "\nTask JSON path: ") { AllowSpaces = true };
            var task = doc.Editor.GetString(taskOpt);
            if (task.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return;

            var resultOpt = new Autodesk.AutoCAD.EditorInput.PromptStringOptions(
                "\nResult JSON path: ") { AllowSpaces = true };
            var result = doc.Editor.GetString(resultOpt);
            if (result.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return;

            string taskPath = (task.StringResult ?? "").Trim().Trim('"');
            string resultPath = (result.StringResult ?? "").Trim().Trim('"');
            Environment.SetEnvironmentVariable("C3DF_TASK", taskPath);
            Environment.SetEnvironmentVariable("C3DF_RESULT", resultPath);
            Run();
        }

        /// <summary>
        /// Gets the station label (S01/S02...).
        /// The key `step` is shared: Dispatcher treats it as the station label, but some ops also have
        /// their own `step` parameter (e.g. the grid step of create_surface_grid). So **only strings count**;
        /// numbers are ignored as the op's own parameter and we fall back to sequential numbering.
        /// An unconditional GetValue&lt;string&gt;() once let a single numeric step make the whole work order throw during parsing:
        /// "An element of type 'Number' cannot be converted to a 'System.String'",
        /// completed_ops=0.
        /// </summary>
        static string StationLabel(JsonObject o, int opIndex)
        {
            string fallback = "S" + opIndex.ToString("00", CultureInfo.InvariantCulture);
            if (o == null) return fallback;
            JsonNode n = o["step"];
            if (n == null) return fallback;
            string s;
            try
            {
                if (n.GetValueKind() != JsonValueKind.String) return fallback;  // number/bool -> not a label
                s = n.GetValue<string>();
            }
            catch { return fallback; }
            return string.IsNullOrWhiteSpace(s) ? fallback : s;
        }

        /// <summary>Takes the first n frames of the exception stack (only this plugin's frames, for quick localisation).</summary>
        static string FirstFrames(System.Exception ex, int n)
        {
            try
            {
                string st = ex.StackTrace;
                if (string.IsNullOrEmpty(st)) return "(no stack)";
                var lines = st.Split('\n');
                var sb = new StringBuilder();
                int taken = 0;
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    sb.Append(line);
                    if (++taken >= n) break;
                    sb.Append(" | ");
                }
                return sb.ToString();
            }
            catch { return "(stack unavailable)"; }
        }

        // Reflection-based serialization is off by default in the AutoCAD 2025 host; custom JsonSerializerOptions
        // must set TypeInfoResolver explicitly or it fails with "must specify a TypeInfoResolver".
        internal static JsonSerializerOptions NewOpts()
        {
            return new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,  // keep non-ASCII text unescaped so people can read it directly
                TypeInfoResolver = new DefaultJsonTypeInfoResolver()
            };
        }

        /// <summary>Three-level fallback serialization: readable options -> default options -> plain text, so there is always output.</summary>
        internal static string ToJson(JsonNode node)
        {
            try { return node.ToJsonString(NewOpts()); } catch { }
            try { return node.ToJsonString(); } catch (System.Exception ex)
            {
                return "{\"ok\":false,\"error\":\"Serialization failed: " +
                       ex.Message.Replace("\"", "'").Replace("\\", "/") + "\"}";
            }
        }

        internal static void Audit(JsonObject entry)
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("C3DF_AUDIT_LOG");
                if (string.IsNullOrWhiteSpace(path)) return;
                string parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                entry["timestamp"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
                entry["run_id"] = Environment.GetEnvironmentVariable("C3DF_RUN_ID") ?? "unknown";
                var opts = NewOpts();
                opts.WriteIndented = false;
                string line = entry.ToJsonString(opts);
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>
        /// Write to disk + optional print. Serialization itself must be crash-proof: any failure degrades to one line of plain text;
        /// the "write result" step must never take the whole command down (a serialization exception once left successful ops with no result at all).
        /// </summary>
        static void Dump(JsonNode result, string path, Document doc)
        {
            if (string.IsNullOrEmpty(path) && doc == null) return;   // resident mode without a result file: skip pointless serialization
            string json = ToJson(result);
            try
            {
                if (!string.IsNullOrEmpty(path))
                    File.WriteAllText(path, json, new UTF8Encoding(false));
            }
            catch { }
            if (doc != null)
            {
                try { doc.Editor.WriteMessage("\n" + json + "\n"); } catch { }
            }
        }

        /// <summary>Write the op catalog (registry + node contracts) to C3DF_RESULT; backs `civil3dfactory.ps1 -Help`.</summary>
        [CommandMethod("C3DF-Ops")]
        public void ListOps()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            string json = ToJson(Ops.BuildCatalog(null));
            string outPath = Environment.GetEnvironmentVariable("C3DF_RESULT");
            if (string.IsNullOrEmpty(outPath))
                outPath = Path.Combine(Path.GetTempPath(), "civil3dfactory.ops.json");
            try { File.WriteAllText(outPath, json, new UTF8Encoding(false)); } catch { }
            if (doc != null) { try { doc.Editor.WriteMessage("\nop catalog written: " + outPath + "\n"); } catch { } }
        }

        static string SafeName(Document doc)
        {
            try { return doc == null ? "(none)" : doc.Database.Filename; }
            catch { return "(unknown)"; }
        }
    }
}
