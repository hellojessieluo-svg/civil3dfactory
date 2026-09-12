using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DFactory
{
    /// <summary>
    /// 功能区按钮 / C3DF_ 命令的执行路径：弹参数框 → 在当前图纸里跑同一个 Ops 实现 → 结果落盘 + 命令行回报。
    /// 与 Dispatcher 的工单执行共用 Ops.Execute，保证界面跑出来的结果和流水线一致。
    /// </summary>
    public static class NodeRunner
    {
        public static void RunInteractive(string nodeId)
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("请先打开一张图纸。", "Civil3DFactory", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Editor ed = doc.Editor;

            NodeDef node;
            try
            {
                node = NodeCatalog.Find(nodeId);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nCivil3DFactory：读取节点契约失败：" + ex.Message + "\n");
                return;
            }

            if (node == null)
            {
                ed.WriteMessage("\nCivil3DFactory：nodes/node.json 里没有节点 '" + nodeId + "'。\n");
                return;
            }
            if (!node.Runnable)
            {
                ed.WriteMessage("\nCivil3DFactory：节点 '" + node.Id + "' 尚未在插件内登记执行入口（Ops.Registry）。\n");
                return;
            }

            JsonObject args;
            using (var form = new NodeParamsForm(node, doc))
            {
                if (AcadApp.ShowModalDialog(form) != DialogResult.OK) return;
                args = form.Args;
            }
            if (args == null) return;

            ed.WriteMessage("\n[C3DF] 开始执行节点 " + node.Id + " · " + node.Title + "\n");

            var sw = Stopwatch.StartNew();
            JsonNode data = null;
            System.Exception failure = null;
            try
            {
                using (DocumentLock dl = doc.LockDocument())
                {
                    data = Ops.Execute(node.Id, args, doc);
                }
            }
            catch (System.Exception ex)
            {
                failure = ex;
            }
            sw.Stop();

            var result = new JsonObject
            {
                ["node"] = node.Id,
                ["runner"] = "ribbon",
                ["dwg"] = SafeDocName(doc),
                ["args"] = args.DeepClone(),
                ["ok"] = failure == null,
                ["ms"] = sw.ElapsedMilliseconds,
                ["completed_at"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture)
            };
            if (failure == null) result["data"] = data;
            else
            {
                result["error"] = failure.Message;
                result["type"] = failure.GetType().Name;
                result["stack"] = FirstFrames(failure, 6);
            }

            string resultPath = WriteResult(node.Id, result);

            if (failure == null)
            {
                ed.WriteMessage("[C3DF] ✓ " + node.Id + " 完成，用时 " + sw.ElapsedMilliseconds + " ms\n");
                string summary = Summarize(data);
                if (!string.IsNullOrEmpty(summary)) ed.WriteMessage("[C3DF] " + summary + "\n");
            }
            else
            {
                ed.WriteMessage("[C3DF] ✗ " + node.Id + " 失败：" + failure.Message + "\n");
                ed.WriteMessage("[C3DF] " + failure.GetType().Name + " @ " + FirstFrames(failure, 3) + "\n");
            }
            if (!string.IsNullOrEmpty(resultPath))
                ed.WriteMessage("[C3DF] 结果：" + resultPath + "\n");
        }

        /// <summary>结果统一写到用户目录，避免污染工程目录和工厂仓库。</summary>
        static string WriteResult(string nodeId, JsonObject result)
        {
            try
            {
                string dir = Path.Combine(FactoryPaths.UserDir(), "runs",
                    DateTime.Now.ToString("yyyyMM", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir,
                    DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + nodeId + ".json");
                File.WriteAllText(path, result.ToJsonString(Opts()), new UTF8Encoding(false));
                return path;
            }
            catch { return null; }
        }

        /// <summary>命令行只回报一行：标量字段直接列，数组列个数。</summary>
        static string Summarize(JsonNode data)
        {
            JsonObject o = data as JsonObject;
            if (o == null) return data == null ? "" : Clip(data.ToJsonString(), 300);

            var sb = new StringBuilder();
            int shown = 0;
            foreach (System.Collections.Generic.KeyValuePair<string, JsonNode> kv in o)
            {
                if (shown >= 8) { sb.Append(" …"); break; }
                string text;
                if (kv.Value is JsonArray) text = "[" + ((JsonArray)kv.Value).Count + " 项]";
                else if (kv.Value is JsonObject) text = "{…}";
                else text = kv.Value == null ? "null" : kv.Value.ToString();
                if (sb.Length > 0) sb.Append("  ");
                sb.Append(kv.Key).Append('=').Append(Clip(text, 60));
                shown++;
            }
            return sb.ToString();
        }

        static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", "").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        static string FirstFrames(System.Exception ex, int count)
        {
            try
            {
                if (ex.StackTrace == null) return "(无堆栈)";
                string[] lines = ex.StackTrace.Split('\n');
                var sb = new StringBuilder();
                for (int i = 0; i < lines.Length && i < count; i++)
                {
                    if (sb.Length > 0) sb.Append(" | ");
                    sb.Append(lines[i].Trim());
                }
                return sb.ToString();
            }
            catch { return "(无堆栈)"; }
        }

        static string SafeDocName(Document doc)
        {
            try { return doc != null ? doc.Name : null; }
            catch { return null; }
        }

        static JsonSerializerOptions Opts()
        {
            return new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
        }
    }
}
