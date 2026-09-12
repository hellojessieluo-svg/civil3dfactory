using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Civil3DFactory
{
    /// <summary>节点的一个入参：来自 node.json 的 inputs 或 parameters。</summary>
    public sealed class NodeArgDef
    {
        public string Key;
        /// <summary>node.json 里的原始类型串，已去掉表示可选的问号。</summary>
        public string Type;
        public bool Optional;
        /// <summary>true = 来自 inputs（图中已有对象，界面上给下拉选择）。</summary>
        public bool IsInput;
        /// <summary>类型串的语义解析结果，参数对话框据此决定用哪种控件。</summary>
        public NodeArgType Parsed;
    }

    /// <summary>node.json 里的一条节点契约。</summary>
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

        /// <summary>该节点在插件里是否有执行入口（外部 PowerShell 节点也登记在 Ops 注册表里）。</summary>
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
    /// nodes/node.json 读取器。契约仍是唯一真源：功能区面板、参数对话框、命令名全部由它生成，
    /// 新增节点只要写进 node.json 并在 Ops 注册表登记执行入口即可自动出现。
    /// </summary>
    public static class NodeCatalog
    {
        const string DefaultPanel = "其他";

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

        /// <summary>按 ui.panel 分组，面板与面板内节点都按 ui.order 排序。</summary>
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
                throw new InvalidOperationException("找不到节点契约: " + path);

            JsonNode root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
            JsonArray arr = root as JsonArray;
            if (arr == null)
                throw new InvalidOperationException("nodes/node.json 根元素必须是数组。");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<NodeDef>();
            int index = 0;
            foreach (JsonNode item in arr)
            {
                index++;
                JsonObject o = item as JsonObject;
                if (o == null) throw new InvalidOperationException("nodes/node.json 第 " + index + " 条不是对象。");

                string id = Str(o, "id");
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidOperationException("nodes/node.json 存在缺少 id 的记录。");
                if (!seen.Add(id))
                    throw new InvalidOperationException("nodes/node.json 存在重复 id: " + id);

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
