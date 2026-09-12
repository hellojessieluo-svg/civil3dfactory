using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Civil3DFactory
{
    /// <summary>One node argument, from the inputs or parameters of node.json.</summary>
    public sealed class NodeArgDef
    {
        public string Key;
        /// <summary>Raw type string from node.json, with the optional question mark already removed.</summary>
        public string Type;
        public bool Optional;
        /// <summary>true = from inputs (objects already in the drawing, offered in a dropdown).</summary>
        public bool IsInput;
        /// <summary>Semantic parse of the type string; the parameter dialog picks its control from it.</summary>
        public NodeArgType Parsed;
    }

    /// <summary>One node contract from node.json.</summary>
    public sealed class NodeDef
    {
        public string Id;
        public string Name;
        public string Kind;
        public string Effect;
        public string Panel;
        public int Order;
        public List<NodeArgDef> Args = new List<NodeArgDef>();

        public string Title
        {
            get { return string.IsNullOrEmpty(Name) ? Id : Name; }
        }

        /// <summary>Whether the node has an execution entry in the plugin (external PowerShell nodes are also registered in the Ops registry).</summary>
        public bool Runnable
        {
            get { return Ops.Registry.ContainsKey(Id); }
        }

        public string CommandName
        {
            get { return "C3DF-" + Id.ToUpperInvariant(); }
        }
    }

    /// <summary>
    /// nodes/node.json reader. The contract remains the single source of truth: ribbon panels, parameter dialogs and command names are all generated from it;
    /// a new node appears automatically once it is written into node.json and its execution entry is registered in the Ops registry.
    /// </summary>
    public static class NodeCatalog
    {
        const string DefaultPanel = "Other";

        static List<NodeDef> _nodes;
        static readonly object Gate = new object();

        public static void Invalidate()
        {
            lock (Gate) { _nodes = null; }
            FactoryPaths.Invalidate();
        }

        public static IList<NodeDef> All()
        {
            lock (Gate)
            {
                if (_nodes == null) _nodes = Load();
                return _nodes;
            }
        }

        public static NodeDef Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            foreach (NodeDef n in All())
                if (string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase)) return n;
            return null;
        }

        public static HashSet<string> Ids()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (NodeDef n in All()) ids.Add(n.Id);
            return ids;
        }

        /// <summary>Groups by ui.panel; panels and the nodes within them are both sorted by ui.order.</summary>
        public static List<KeyValuePair<string, List<NodeDef>>> Panels()
        {
            var order = new Dictionary<string, int>(StringComparer.Ordinal);
            var buckets = new Dictionary<string, List<NodeDef>>(StringComparer.Ordinal);

            foreach (NodeDef n in All())
            {
                string panel = string.IsNullOrEmpty(n.Panel) ? DefaultPanel : n.Panel;
                List<NodeDef> list;
                if (!buckets.TryGetValue(panel, out list))
                {
                    list = new List<NodeDef>();
                    buckets[panel] = list;
                    order[panel] = n.Order;
                }
                else if (n.Order < order[panel])
                {
                    order[panel] = n.Order;
                }
                list.Add(n);
            }

            var result = new List<KeyValuePair<string, List<NodeDef>>>();
            foreach (KeyValuePair<string, List<NodeDef>> kv in buckets)
            {
                kv.Value.Sort(delegate (NodeDef a, NodeDef b)
                {
                    int c = a.Order.CompareTo(b.Order);
                    return c != 0 ? c : string.Compare(a.Id, b.Id, StringComparison.Ordinal);
                });
                result.Add(kv);
            }
            result.Sort(delegate (KeyValuePair<string, List<NodeDef>> a, KeyValuePair<string, List<NodeDef>> b)
            {
                int c = order[a.Key].CompareTo(order[b.Key]);
                return c != 0 ? c : string.Compare(a.Key, b.Key, StringComparison.Ordinal);
            });
            return result;
        }

        static List<NodeDef> Load()
        {
            string path = FactoryPaths.NodeJsonPath();
            if (!File.Exists(path))
                throw new InvalidOperationException("Node contract not found: " + path);

            JsonNode root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
            JsonArray arr = root as JsonArray;
            if (arr == null)
                throw new InvalidOperationException("The root element of nodes/node.json must be an array.");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<NodeDef>();
            int index = 0;
            foreach (JsonNode item in arr)
            {
                index++;
                JsonObject o = item as JsonObject;
                if (o == null) throw new InvalidOperationException("Entry " + index + " of nodes/node.json is not an object.");

                string id = Str(o, "id");
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidOperationException("nodes/node.json contains a record without an id.");
                if (!seen.Add(id))
                    throw new InvalidOperationException("nodes/node.json contains a duplicate id: " + id);

                var def = new NodeDef
                {
                    Id = id,
                    Name = Str(o, "name"),
                    Kind = Str(o, "kind"),
                    Effect = Str(o, "effect"),
                    Panel = DefaultPanel,
                    Order = index * 1000
                };

                JsonObject ui = o["ui"] as JsonObject;
                if (ui != null)
                {
                    string panel = Str(ui, "panel");
                    if (!string.IsNullOrWhiteSpace(panel)) def.Panel = panel;
                    if (ui["order"] != null)
                    {
                        try { def.Order = ui["order"].GetValue<int>(); }
                        catch { }
                    }
                }

                AddArgs(def, o["inputs"] as JsonObject, true);
                AddArgs(def, o["parameters"] as JsonObject, false);
                list.Add(def);
            }
            return list;
        }

        static void AddArgs(NodeDef def, JsonObject map, bool isInput)
        {
            if (map == null) return;
            foreach (KeyValuePair<string, JsonNode> kv in map)
            {
                string raw = kv.Value != null ? kv.Value.ToString() : "string";
                NodeArgType parsed = NodeArgType.Parse(raw);
                string type = raw.Trim();
                if (type.EndsWith("?", StringComparison.Ordinal))
                    type = type.Substring(0, type.Length - 1);
                def.Args.Add(new NodeArgDef
                {
                    Key = kv.Key,
                    Type = type,
                    Optional = parsed.Optional,
                    IsInput = isInput,
                    Parsed = parsed
                });
            }
        }

        static string Str(JsonObject o, string key)
        {
            if (o == null || o[key] == null) return null;
            try { return o[key].GetValue<string>(); }
            catch { return o[key].ToString(); }
        }
    }
}
