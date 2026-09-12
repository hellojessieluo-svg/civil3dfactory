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
    /// 操作台派发器：一次 acc 启动可执行任务文件里的多个操作。
    /// 约定（全部走环境变量，避免命令行交互）：
    ///   C3DF_TASK   = 任务 JSON 路径（必需）
    ///   C3DF_RESULT = 结果 JSON 路径（可选，缺省为任务文件同目录 result.json）
    /// 任务格式： { "ops": [ {"op":"list_alignments"}, {"op":"export_stations","interval":50} ] }
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
                throw new InvalidOperationException("找不到任务文件，请设置环境变量 C3DF_TASK。当前值: " + (taskPath ?? "(空)"));
            return JsonNode.Parse(File.ReadAllText(taskPath, Encoding.UTF8));
        }

        /// <summary>
        /// 执行一份任务，返回结果对象。冷启动（C3DF-Run）和常驻（C3DF-Serve）共用这一段——
        /// 两条路径的审计事件、结果结构、错误处理必须完全一致，否则常驻跑出来的结果
        /// 和工单台账对不上账。
        /// <paramref name="taskPath"/> 只用于审计字段；常驻模式任务直接从管道来，传 null。
        /// <paramref name="resultPath"/> 为空时不落盘，只把结果返回给调用方（常驻走管道回传）。
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
                if (task == null) throw new InvalidOperationException("任务内容为空。");
                JsonObject pipeline = task["pipeline"] as JsonObject;
                bool pipelineTask = pipeline != null;
                bool stopOnError = pipelineTask
                    && pipeline["stop_on_error"] != null
                    && pipeline["stop_on_error"].GetValue<bool>();
                string pipelineId = pipelineTask && pipeline["id"] != null
                    ? pipeline["id"].GetValue<string>()
                    : null;
                if (pipelineTask && string.IsNullOrWhiteSpace(pipelineId))
                    throw new InvalidOperationException("pipeline.id 不能为空。");

                JsonArray ops = task["ops"] as JsonArray;
                if (ops == null) throw new InvalidOperationException("任务文件缺少 ops 数组。");

                result["dwg"] = SafeName(doc);
                if (pipelineTask) result["pipeline"] = pipeline.DeepClone();
                JsonObject resolved = Ops.ResolveFactoryTask(task as JsonObject, doc);
                if (resolved.Count > 0) result["resolved"] = resolved;
                result["total_ops"] = ops.Count;
                Dump(result, resultPath, null);   // 先落一次盘：即使后面硬崩也留证据

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
                            throw new InvalidOperationException("操作缺少 op 字段。");
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
                        // 带上堆栈：AutoCAD 的异常消息常常只有 eXxx 一个词，没有堆栈根本定不了位
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
                    Dump(result, resultPath, null);   // 每个操作跑完就更新结果文件
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
                // 冷启动没给结果路径时兜底到「我的文档」，至少留下证据；
                // 常驻模式结果走管道回传，不需要兜底文件。
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
        /// 取工位标签（S01/S02…）。
        /// `step` 这个键名是共享的：Dispatcher 拿它当工位标签，而某些 op 自己也有叫
        /// `step` 的参数（如 create_surface_grid 的网格步长）。所以**只认字符串**，
        /// 数值一律当成 op 自己的参数忽略掉，回退到按序号编号。
        /// 曾经无条件 GetValue&lt;string&gt;()，一个数值 step 就让整张工单在解析阶段抛
        /// "An element of type 'Number' cannot be converted to a 'System.String'"，
        /// completed_ops=0。
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
                if (n.GetValueKind() != JsonValueKind.String) return fallback;  // 数值/布尔 → 不是标签
                s = n.GetValue<string>();
            }
            catch { return fallback; }
            return string.IsNullOrWhiteSpace(s) ? fallback : s;
        }

        /// <summary>取异常堆栈的前 n 帧（只留本插件的帧，方便一眼定位）。</summary>
        static string FirstFrames(System.Exception ex, int n)
        {
            try
            {
                string st = ex.StackTrace;
                if (string.IsNullOrEmpty(st)) return "(无堆栈)";
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
            catch { return "(堆栈读取失败)"; }
        }

        // AutoCAD 2025 宿主里反射式序列化默认关闭，自定义 JsonSerializerOptions
        // 必须显式给 TypeInfoResolver，否则报 "must specify a TypeInfoResolver"。
        internal static JsonSerializerOptions NewOpts()
        {
            return new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,  // 中文不转义，人能直接读
                TypeInfoResolver = new DefaultJsonTypeInfoResolver()
            };
        }

        /// <summary>三级降级序列化：带中文友好选项 → 默认选项 → 纯文本，保证一定有输出。</summary>
        internal static string ToJson(JsonNode node)
        {
            try { return node.ToJsonString(NewOpts()); } catch { }
            try { return node.ToJsonString(); } catch (System.Exception ex)
            {
                return "{\"ok\":false,\"error\":\"序列化失败: " +
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
        /// 落盘 + 可选打印。序列化本身也要防崩：任何一步失败都退化成一行纯文本，
        /// 绝不让"写结果"这步把整条命令带崩（曾因序列化异常导致 ops 已成功却无任何结果）。
        /// </summary>
        static void Dump(JsonNode result, string path, Document doc)
        {
            if (string.IsNullOrEmpty(path) && doc == null) return;   // 常驻模式无结果文件时不白序列化
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
