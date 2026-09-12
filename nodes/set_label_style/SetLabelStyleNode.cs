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
using CivLabelStyleTextComponent = Autodesk.Civil.DatabaseServices.Styles.LabelStyleTextComponent;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        /// <summary>
        /// 改标签样式组件：可见性 / 文本偏移 / 线角度长度。
        ///
        /// 为什么有这个零件（2026-08-26 项目B初设横断面）：断面中心线 origin 点同时被
        /// code set 分支（@C3DF-centerline）和 marker 分支（CL_Elev_WL_Label_1-400）各画一遍，
        /// 655 个标签完全重合成「字体重叠」；且「疏浚控制线」离常水位标注只有 0.42m 必然挤。
        /// GUI 里改样式无头链带不动，所以做成 op。
        ///
        /// ⚠ 只走强类型/名单化属性访问，禁止反射深爬（会原生崩 accoreconsole，08-26 三连崩）。
        /// </summary>
        static JsonNode RunNodeSetLabelStyle(JsonObject a, Document doc)
        {
            string styleName = GetString(a, "style", null);
            if (string.IsNullOrEmpty(styleName))
                throw new InvalidOperationException("style（标签样式名）必填。");
            // components: [{name, visible?, x_offset?, y_offset?, angle_deg?, length?, contents?}]
            var compEdits = a["components"] as JsonArray;
            if (compEdits == null || compEdits.Count == 0)
                throw new InvalidOperationException("components 数组必填（每项至少给 name + 一个改动）。");

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var applied = new JsonArray();
            int stylesFound = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // 找样式：LabelStyles 根 + CodeSetStyles 支，与 dump_label_styles 同源
                var ids = new List<ObjectId>();
                var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
                object root = null;
                try { root = civ.Styles.GetType().GetProperty("LabelStyles").GetValue(civ.Styles); }
                catch { }
                CollectIds(root ?? (object)civ.Styles, ids, seen, 0, 7);
                try
                {
                    foreach (ObjectId csId in civ.Styles.CodeSetStyles)
                    {
                        var cs = tr.GetObject(csId, OpenMode.ForRead)
                            as Autodesk.Civil.DatabaseServices.Styles.CodeSetStyle;
                        if (cs == null) continue;
                        foreach (Autodesk.Civil.DatabaseServices.Styles.CodeSetStyleItem item in cs)
                        {
                            try { if (!item.LabelStyleId.IsNull) ids.Add(item.LabelStyleId); } catch { }
                        }
                    }
                }
                catch { }

                var done = new HashSet<ObjectId>();
                foreach (ObjectId id in ids)
                {
                    if (id.IsNull || !done.Add(id)) continue;
                    CivLabelStyle st;
                    try { st = tr.GetObject(id, OpenMode.ForRead) as CivLabelStyle; }
                    catch { continue; }
                    if (st == null || st.Name != styleName) continue;
                    stylesFound++;
                    st.UpgradeOpen();

                    foreach (JsonNode en in compEdits)
                    {
                        var e = en as JsonObject;
                        if (e == null) continue;
                        string compName = GetString(e, "name", null);   // null/"*" = 全部组件
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
                                try { c = tr.GetObject(cid, OpenMode.ForWrite) as CivLabelStyleComponent; }
                                catch { continue; }
                                if (c == null) continue;
                                if (!string.IsNullOrEmpty(compName) && compName != "*" && c.Name != compName)
                                    continue;

                                var log = new JsonObject
                                {
                                    ["style"] = styleName,
                                    ["component"] = c.Name,
                                    ["kind"] = ct.ToString()
                                };
                                bool touched = false;

                                if (e["visible"] != null)
                                {
                                    bool vis = e["visible"].GetValue<bool>();
                                    // 可见性在 General.Visible（PropertyBoolean）。名单化访问，不深爬。
                                    if (SetGroupProp(c, "General", "Visible", vis)) { log["visible"] = vis; touched = true; }
                                }
                                var txt = c as CivLabelStyleTextComponent;
                                if (txt != null)
                                {
                                    if (e["x_offset"] != null)
                                    { txt.Text.XOffset.Value = e["x_offset"].GetValue<double>(); log["x_offset"] = txt.Text.XOffset.Value; touched = true; }
                                    if (e["y_offset"] != null)
                                    { txt.Text.YOffset.Value = e["y_offset"].GetValue<double>(); log["y_offset"] = txt.Text.YOffset.Value; touched = true; }
                                    if (e["contents"] != null)
                                    { txt.Text.Contents.Value = e["contents"].GetValue<string>(); log["contents"] = "set"; touched = true; }
                                    if (e["angle_deg"] != null)
                                    { txt.Text.Angle.Value = e["angle_deg"].GetValue<double>() * Math.PI / 180.0; log["angle_deg"] = e["angle_deg"].GetValue<double>(); touched = true; }
                                    if (e["height"] != null)
                                    { txt.Text.Height.Value = e["height"].GetValue<double>(); log["height"] = txt.Text.Height.Value; touched = true; }
                                    // 附着点（Attachment）：文字自身哪个点贴到锚点上，枚举名如 TopCenter / MiddleCenter / BottomCenter
                                    if (e["attachment"] != null &&
                                        SetGroupProp(txt, "Text", "Attachment", e["attachment"].GetValue<string>()))
                                    { log["attachment"] = e["attachment"].GetValue<string>(); touched = true; }
                                }
                                // 锚点两项对文本/直线/块通用：anchor_component=组件名（<Feature> 表示挂要素本体），
                                // anchor_location=锚点位置枚举名（TopCenter/BottomCenter/…）。上下两行的间距就靠这两项＋附着点定。
                                if (e["anchor_component"] != null &&
                                    SetGroupProp(c, "General", "AnchorComponent", e["anchor_component"].GetValue<string>()))
                                { log["anchor_component"] = e["anchor_component"].GetValue<string>(); touched = true; }
                                if (e["anchor_location"] != null &&
                                    SetGroupProp(c, "General", "AnchorLocation", e["anchor_location"].GetValue<string>()))
                                { log["anchor_location"] = e["anchor_location"].GetValue<string>(); touched = true; }
                                else if (ct == CivLabelStyleComponentType.Line)
                                {
                                    if (e["angle_deg"] != null &&
                                        SetGroupProp(c, "Line", "Angle", e["angle_deg"].GetValue<double>() * Math.PI / 180.0))
                                    { log["angle_deg"] = e["angle_deg"].GetValue<double>(); touched = true; }
                                    if (e["length"] != null &&
                                        SetGroupProp(c, "Line", "Length", e["length"].GetValue<double>()))
                                    { log["length"] = e["length"].GetValue<double>(); touched = true; }
                                }

                                if (touched) applied.Add(log);
                            }
                        }
                    }

                    // 拖曳状态（Dragged State 页）：dragged_state 是 {属性名: 值} 字典，属性名照 dump_label_styles
                    // 报出来的 dragged_state 键（如 DisplayType / TextHeight / LeaderType / LeaderAttachment），
                    // 枚举给名字串。只设 Property* 包装器的 Value，不深爬。
                    var dsEdits = a["dragged_state"] as JsonObject;
                    if (dsEdits != null && dsEdits.Count > 0)
                    {
                        object ds = GetGroupProp(st, "Properties", "DraggedStateComponents", false);
                        if (ds == null) throw new InvalidOperationException("这个样式读不到 Properties.DraggedStateComponents。");
                        var log = new JsonObject { ["style"] = styleName, ["component"] = "(dragged_state)" };
                        bool touched = false;
                        foreach (var kv in dsEdits)
                        {
                            object v = JsonScalar(kv.Value);
                            if (!SetPropValue(ds, kv.Key, v))
                                throw new InvalidOperationException("dragged_state." + kv.Key + " 设不进去（属性名或值类型不对，先用 dump_label_styles 看键名）。");
                            log[kv.Key] = kv.Value?.ToString();
                            touched = true;
                        }
                        if (touched) applied.Add(log);
                    }
                }
                tr.Commit();
            }

            if (stylesFound == 0)
                throw new InvalidOperationException("找不到标签样式 '" + styleName + "'。");
            return new JsonObject
            {
                ["style"] = styleName,
                ["styles_found"] = stylesFound,
                ["edits_applied"] = applied.Count,
                ["applied"] = applied
            };
        }

        /// <summary>名单化访问 obj.<group>.<prop>.Value = v。只碰指定两级，不深爬。</summary>
        static bool SetGroupProp(object obj, string group, string prop, object v)
        {
            try
            {
                PropertyInfo gp = obj.GetType().GetProperty(group);
                if (gp == null) return false;
                object g = gp.GetValue(obj);
                if (g == null) return false;
                return SetPropValue(g, prop, v);
            }
            catch { return false; }
        }

        /// <summary>holder.<prop>.Value = v（一层）。枚举收名字串，数值走 ChangeType。</summary>
        static bool SetPropValue(object holder, string prop, object v)
        {
            try
            {
                PropertyInfo pp = holder.GetType().GetProperty(prop);
                if (pp == null) return false;
                object wrapper = pp.GetValue(holder);
                if (wrapper == null) return false;
                PropertyInfo vp = null;
                foreach (PropertyInfo cand in wrapper.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (cand.Name == "Value" && cand.GetIndexParameters().Length == 0 && cand.CanWrite)
                    { vp = cand; break; }
                }
                if (vp == null) return false;
                Type t = vp.PropertyType;
                object val;
                if (t.IsEnum) val = Enum.Parse(t, v.ToString(), true);
                else if (t == typeof(bool)) val = System.Convert.ToBoolean(v);
                else if (t == typeof(string)) val = v?.ToString();
                else val = System.Convert.ChangeType(v, t);
                vp.SetValue(wrapper, val);
                return true;
            }
            catch { return false; }
        }

        /// <summary>读 obj.<group>.<prop>：unwrap=true 时取包装器的 Value，否则返回属性对象本身。名单化两级，不深爬。</summary>
        static object GetGroupProp(object obj, string group, string prop, bool unwrap = true)
        {
            try
            {
                PropertyInfo gp = obj.GetType().GetProperty(group);
                if (gp == null) return null;
                object g = gp.GetValue(obj);
                if (g == null) return null;
                PropertyInfo pp = g.GetType().GetProperty(prop);
                if (pp == null) return null;
                object wrapper = pp.GetValue(g);
                if (wrapper == null || !unwrap) return wrapper;
                PropertyInfo vp = wrapper.GetType().GetProperty("Value");
                return vp == null ? wrapper : vp.GetValue(wrapper);
            }
            catch { return null; }
        }

        /// <summary>把一个设置持有者（如 DraggedStateComponents）的 Property* 包装器们摊成 {名: Value}。
        /// 只碰类型名以 Property 开头的属性，别的（可能摸到 Database）一律不读。</summary>
        static JsonObject ShallowPropertyValues(object holder)
        {
            if (holder == null) return null;
            var o = new JsonObject();
            foreach (PropertyInfo p in holder.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                string tn = p.PropertyType.Name;
                // 只碰 Civil 的 Property* 包装器（含 PropertyEnum`1 之类泛型）；Database/Document/ObjectId 类一律不读
                if (tn.IndexOf("Property", StringComparison.Ordinal) < 0)
                { o["?" + p.Name] = tn; continue; }
                try
                {
                    object wrapper = p.GetValue(holder);
                    if (wrapper == null) continue;
                    PropertyInfo vp = wrapper.GetType().GetProperty("Value");
                    if (vp == null) { o["?" + p.Name] = tn + "(noValue)"; continue; }
                    object v = vp.GetValue(wrapper);
                    if (v == null) o[p.Name] = null;
                    else if (v is double d) o[p.Name] = d;
                    else if (v is int i) o[p.Name] = i;
                    else if (v is bool b) o[p.Name] = b;
                    else o[p.Name] = v.ToString();
                }
                catch { }
            }
            return o;
        }

        static object JsonScalar(JsonNode n)
        {
            if (n == null) return null;
            var jv = n as JsonValue;
            if (jv == null) return n.ToString();
            if (jv.TryGetValue<bool>(out bool b)) return b;
            if (jv.TryGetValue<double>(out double d)) return d;
            if (jv.TryGetValue<string>(out string s)) return s;
            return n.ToString();
        }
    }
}
