using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(Civil3DFactory.FactoryCommands))]

namespace Civil3DFactory
{
    /// <summary>
    /// 工厂在 Civil 3D 里的内置命令（功能区 Civil3DFactory.Ui 已于 2026-09-05 拆除）：
    ///   C3DF-NODE   执行一个节点（功能区按钮走的也是这条路）
    ///   C3DF-NODES  在命令行列出全部节点及其命令名
    /// 另外每个节点会动态注册一个 C3DF_&lt;节点ID&gt; 命令，可直接敲。
    /// </summary>
    public class FactoryCommands
    {
        static string _pendingNodeId;
        static readonly HashSet<string> Registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>功能区按钮先排队再发命令，避免把节点 ID 当命令行输入解析。</summary>
        public static void Queue(string nodeId)
        {
            _pendingNodeId = nodeId;
        }

        [CommandMethod("C3DF-NODE", CommandFlags.Modal)]
        public void RunNode()
        {
            string id = _pendingNodeId;
            _pendingNodeId = null;

            if (string.IsNullOrEmpty(id))
            {
                Editor ed = Ed();
                if (ed == null) return;
                var opts = new PromptStringOptions("\n节点 ID（C3DF-NODES 可列出全部）")
                {
                    AllowSpaces = false
                };
                PromptResult res = ed.GetString(opts);
                if (res.Status != PromptStatus.OK) return;
                id = (res.StringResult ?? "").Trim();
                if (id.Length == 0) return;
            }

            NodeRunner.RunInteractive(id);
        }

        [CommandMethod("C3DF-NODES", CommandFlags.Modal)]
        public void ListNodes()
        {
            Editor ed = Ed();
            if (ed == null) return;
            try
            {
                ed.WriteMessage("\n[C3DF] nodes/node.json（" + FactoryPaths.RequireRoot() + "）\n");
                foreach (KeyValuePair<string, List<NodeDef>> panel in NodeCatalog.Panels())
                {
                    ed.WriteMessage("── " + panel.Key + "\n");
                    foreach (NodeDef n in panel.Value)
                    {
                        ed.WriteMessage("   " + n.CommandName.PadRight(38)
                            + n.Title + (n.Runnable ? "" : "  (插件内无执行入口)") + "\n");
                    }
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n[C3DF] 读取节点契约失败：" + ex.Message + "\n");
            }
        }

        /// <summary>
        /// 给每个节点动态注册 C3DF_&lt;节点ID&gt; 命令。用的是 Internal.Utils，
        /// 注册失败不影响功能区和 C3DF-NODE，所以整体吞掉异常。
        /// </summary>
        public static void RegisterNodeCommands()
        {
            IList<NodeDef> nodes;
            try { nodes = NodeCatalog.All(); }
            catch { return; }

            foreach (NodeDef node in nodes)
            {
                string id = node.Id;
                string command = node.CommandName;
                if (!Registered.Add(command)) continue;
                try
                {
                    Autodesk.AutoCAD.Internal.Utils.AddCommand(
                        "C3DF_FACTORY", command, command, CommandFlags.Modal,
                        delegate { NodeRunner.RunInteractive(id); });
                }
                catch
                {
                    Registered.Remove(command);
                }
            }
        }

        static Editor Ed()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            return doc != null ? doc.Editor : null;
        }
    }
}
