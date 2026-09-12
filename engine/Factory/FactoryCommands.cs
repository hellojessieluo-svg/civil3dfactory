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
    /// The factory's built-in commands inside Civil 3D (the Civil3DFactory.Ui ribbon was removed on 2026-09-05):
    ///   C3DF-NODE   runs one node (the ribbon buttons went through this path too)
    ///   C3DF-NODES  lists every node and its command name on the command line
    /// Additionally each node registers a dynamic C3DF_&lt;nodeId&gt; command that can be typed directly.
    /// </summary>
    public class FactoryCommands
    {
        static string _pendingNodeId;
        static readonly HashSet<string> Registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Ribbon buttons queue first and then send the command, so the node ID is not parsed as command-line input.</summary>
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
                var opts = new PromptStringOptions("\nNode ID (C3DF-NODES lists them all)")
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
                ed.WriteMessage("\n[C3DF] nodes/node.json (" + FactoryPaths.RequireRoot() + ")\n");
                foreach (KeyValuePair<string, List<NodeDef>> panel in NodeCatalog.Panels())
                {
                    ed.WriteMessage("── " + panel.Key + "\n");
                    foreach (NodeDef n in panel.Value)
                    {
                        ed.WriteMessage("   " + n.CommandName.PadRight(38)
                            + n.Title + (n.Runnable ? "" : "  (no execution entry in the plugin)") + "\n");
                    }
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n[C3DF] Failed to read node contracts: " + ex.Message + "\n");
            }
        }

        /// <summary>
        /// Registers a dynamic C3DF_&lt;nodeId&gt; command for every node. Uses Internal.Utils;
        /// a registration failure does not affect the ribbon or C3DF-NODE, so exceptions are swallowed as a whole.
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
