using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivLabelStyle = Autodesk.Civil.DatabaseServices.Styles.LabelStyle;
using CivLabelStyleComponent = Autodesk.Civil.DatabaseServices.Styles.LabelStyleComponent;
using CivLabelStyleComponentType = Autodesk.Civil.DatabaseServices.Styles.LabelStyleComponentType;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        /// <summary>
        /// 只读诊断：列出标签样式（LabelStyle）及其文本组件的内容与位置参数。
        ///
        /// 为什么有这个零件：标签在图上显示的固定文字（如「疏浚控制线」）藏在
        /// LabelStyle 的文本组件里，DXFOUT 落不到明文（Civil 自定义对象走 proxy），
        /// 只能靠 API 读。
        ///
        /// ⚠ 历史教训（2026-08-26 三连崩）：**不许用反射深爬样式对象**——
        /// Flatten 式反射会摸到 Database/Document 等属性，accoreconsole 原生崩溃
        /// （非 .NET 异常，catch 不住）。本版只走强类型文档路径：
        /// LabelStyle.GetComponents(Text) → LabelStyleTextComponent.Text.Contents/XOffset/YOffset。
        /// </summary>
        static JsonNode RunNodeDumpLabelStyles(JsonObject a, Document doc)
        {
            // 只报文本内容含这个串的样式；不给就全报
            string contains = GetString(a, "contains", null);
            int maxStyles = (int)GetDouble(a, "max_styles", 800);

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var styles = new JsonArray();
            int scanned = 0, matched = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // 用属性游走只**收集 ObjectId**（这一步 08-26 实测安全，崩的是后面的反射展开）。
                var ids = new List<ObjectId>();
                var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
                object root = null;
                try { root = civ.Styles.GetType().GetProperty("LabelStyles").GetValue(civ.Styles); }
                catch { }
                CollectIds(root ?? (object)civ.Styles, ids, seen, 0, 7);

                // Code Set Style 支：走廊断面的 Point/Link 标签（「疏浚控制线」这类固定文字）
                // 挂在 CodeSetStyle 每个 item 的 LabelStyleId 上，不在 LabelStyles 根下。
                // 同时记下「标签样式 → 挂在哪个 code set 的哪个 code」，查重复标签要用。
                var styleOwners = new Dictionary<ObjectId, List<string>>();
                try
                {
                    foreach (ObjectId csId in civ.Styles.CodeSetStyles)
                    {
                        var cs = tr.GetObject(csId, OpenMode.ForRead)
                            as Autodesk.Civil.DatabaseServices.Styles.CodeSetStyle;
                        if (cs == null) continue;
                        foreach (Autodesk.Civil.DatabaseServices.Styles.CodeSetStyleItem item in cs)
                        {
                            ObjectId lid = ObjectId.Null;
                            try { lid = item.LabelStyleId; } catch { }
                            if (lid.IsNull) continue;
                            ids.Add(lid);
                            string codeName = null;
                            try { codeName = item.Code; } catch { }
                            if (codeName == null)
                            {
                                // 属性名各版本不一（Code / CodeName / Description），反射只取一个字符串，不深爬
                                foreach (string pn in new[] { "CodeName", "Description" })
                                {
                                    try
                                    {
                                        var pi = item.GetType().GetProperty(pn);
                                        if (pi != null) { codeName = pi.GetValue(item) as string; }
                                    }
                                    catch { }
                                    if (codeName != null) break;
                                }
                            }
                            List<string> owners;
                            if (!styleOwners.TryGetValue(lid, out owners))
                                styleOwners[lid] = owners = new List<string>();
                            owners.Add(cs.Name + " / " + (codeName ?? "?"));
                        }
                    }
                }
                catch { }

                var done = new HashSet<ObjectId>();
                foreach (ObjectId id in ids)
                {
                    if (id.IsNull || !done.Add(id)) continue;
                    if (scanned >= maxStyles) break;
                    CivLabelStyle st;
                    try { st = tr.GetObject(id, OpenMode.ForRead) as CivLabelStyle; }
                    catch { continue; }
                    if (st == null) continue;
                    scanned++;

                    var comps = new JsonArray();
                    bool hit = false;
                    // 文本组件：内容 + 偏移。全走强类型，不反射。
                    foreach (CivLabelStyleComponentType ct in new CivLabelStyleComponentType[]
                    {
                        CivLabelStyleComponentType.Text,
                        CivLabelStyleComponentType.Line,
                        CivLabelStyleComponentType.Block
                    })
                    {
                        ObjectIdCollection cids;
                        try { cids = st.GetComponents(ct); } catch { continue; }
                        foreach (ObjectId cid in cids)
                        {
                            CivLabelStyleComponent c;
                            try { c = tr.GetObject(cid, OpenMode.ForRead) as CivLabelStyleComponent; }
                            catch { continue; }
                            if (c == null) continue;
                            var jc = new JsonObject
                            {
                                ["kind"] = ct.ToString(),
                                ["name"] = c.Name,
                                ["type"] = c.GetType().Name
                            };
                            var txt = c as Autodesk.Civil.DatabaseServices.Styles.LabelStyleTextComponent;
                            if (txt != null)
                            {
                                string contents = null;
                                try { contents = txt.Text.Contents.Value; } catch { }
                                jc["contents"] = contents;
                                try { jc["x_offset"] = txt.Text.XOffset.Value; } catch { }
                                try { jc["y_offset"] = txt.Text.YOffset.Value; } catch { }
                                try { jc["height"] = txt.Text.Height.Value; } catch { }
                                try { jc["attachment"] = txt.Text.Attachment.Value.ToString(); } catch { }
                                try { jc["angle"] = txt.Text.Angle.Value; } catch { }
                                if (contains != null && contents != null &&
                                    contents.IndexOf(contains, StringComparison.Ordinal) >= 0)
                                    hit = true;
                            }
                            // 锚点两项在 General 组：AnchorComponent（挂在哪个组件/要素上）、AnchorLocation（挂在它的哪个点）。
                            // 组件之间的上下距离就是由「锚点 + 附着点 + 偏移」决定的，只报偏移看不出间距来源。
                            {
                                object ac = GetGroupProp(c, "General", "AnchorComponent");
                                if (ac != null) jc["anchor_component"] = ac.ToString();
                                object al = GetGroupProp(c, "General", "AnchorLocation") ?? GetGroupProp(c, "General", "AnchorPoint");
                                if (al != null) jc["anchor_location"] = al.ToString();
                                if (GetBool(a, "debug_props", false))
                                {
                                    JsonObject gen = ShallowPropertyValues(c.GetType().GetProperty("General")?.GetValue(c));
                                    if (gen != null) jc["general"] = gen;
                                }
                            }
                            comps.Add(jc);
                        }
                    }

                    if (contains != null && !hit) continue;
                    matched++;
                    var entry = new JsonObject
                    {
                        ["style_type"] = st.GetType().Name,
                        ["name"] = st.Name,
                        ["handle"] = st.Handle.ToString(),
                        ["components"] = comps
                    };
                    // 拖曳状态（Dragged State 页）与引线（Leader 页）：只读 Property* 包装器的 Value，
                    // 名单化一层，不深爬（避开 08-26 那种原生崩）。
                    JsonObject ds = ShallowPropertyValues(GetGroupProp(st, "Properties", "DraggedStateComponents", false));
                    if (ds != null && ds.Count > 0) entry["dragged_state"] = ds;
                    JsonObject ld = ShallowPropertyValues(GetGroupProp(st, "Properties", "Leader", false));
                    if (ld != null && ld.Count > 0) entry["leader"] = ld;
                    List<string> own;
                    if (styleOwners.TryGetValue(id, out own))
                    {
                        var ja = new JsonArray();
                        foreach (string o2 in own) ja.Add(o2);
                        entry["used_by_codes"] = ja;
                    }
                    styles.Add(entry);
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["contains"] = contains,
                ["styles_scanned"] = scanned,
                ["styles_matched"] = matched,
                ["styles"] = styles
            };
        }

        /// <summary>递归收集对象树里的 ObjectId（样式集合大多是 IEnumerable&lt;ObjectId&gt;）。
        /// 只收 id、不打开对象——这一步实测安全。</summary>
        static void CollectIds(object root, List<ObjectId> outIds, HashSet<object> seen, int depth, int maxDepth)
        {
            if (root == null || depth > maxDepth) return;
            if (root is ObjectId oid) { if (!oid.IsNull) outIds.Add(oid); return; }
            if (root is string || root.GetType().IsPrimitive) return;
            if (!root.GetType().IsValueType && !seen.Add(root)) return;

            if (root is IEnumerable en && !(root is string))
            {
                IEnumerator it;
                try { it = en.GetEnumerator(); } catch { return; }
                while (true)
                {
                    object cur;
                    try { if (!it.MoveNext()) break; cur = it.Current; }
                    catch { break; }
                    CollectIds(cur, outIds, seen, depth + 1, maxDepth);
                }
                return;
            }

            foreach (PropertyInfo p in root.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                // 只钻样式树容器，不碰引擎对象
                string tn = p.PropertyType.Name;
                bool container = tn.IndexOf("Styles", StringComparison.OrdinalIgnoreCase) >= 0
                              || tn.IndexOf("Collection", StringComparison.OrdinalIgnoreCase) >= 0
                              || tn.IndexOf("Root", StringComparison.OrdinalIgnoreCase) >= 0
                              || p.PropertyType == typeof(ObjectId);
                if (!container) continue;
                object v;
                try { v = p.GetValue(root); } catch { continue; }
                CollectIds(v, outIds, seen, depth + 1, maxDepth);
            }
        }

        sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            bool IEqualityComparer<object>.Equals(object x, object y) { return ReferenceEquals(x, y); }
            int IEqualityComparer<object>.GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
