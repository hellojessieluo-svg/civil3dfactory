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
        ///
        ///
        /// </summary>
        static JsonNode RunNodeSetLabelStyle(JsonObject a, Document doc)
        {
            string styleName = GetString(a, "style", null);
            if (string.IsNullOrEmpty(styleName))
                throw new InvalidOperationException("style (label style name) is required.");
            // components: [{name, visible?, x_offset?, y_offset?, angle_deg?, length?, contents?, height?, block_height?}]
            var compEdits = a["components"] as JsonArray;
            if (compEdits == null || compEdits.Count == 0)
                throw new InvalidOperationException("components array is required (each item needs name + at least one change).");

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var applied = new JsonArray();
            int stylesFound = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
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
                        string compName = GetString(e, "name", null);   // null/"*" = all components
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
                                    if (e["attachment"] != null &&
                                        SetGroupProp(txt, "Text", "Attachment", e["attachment"].GetValue<string>()))
                                    { log["attachment"] = e["attachment"].GetValue<string>(); touched = true; }
                                }
                                var blk = c as Autodesk.Civil.DatabaseServices.Styles.LabelStyleBlockComponent;
                                if (blk != null && e["block_height"] != null)
                                {
                                    log["block_height_before"] = blk.Block.BlockHeight.Value;
                                    blk.Block.BlockHeight.Value = e["block_height"].GetValue<double>();
                                    log["block_height"] = blk.Block.BlockHeight.Value; touched = true;
                                }
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

                    var dsEdits = a["dragged_state"] as JsonObject;
                    if (dsEdits != null && dsEdits.Count > 0)
                    {
                        object ds = GetGroupProp(st, "Properties", "DraggedStateComponents", false);
                        if (ds == null) throw new InvalidOperationException("Properties.DraggedStateComponents is not readable on this style.");
                        var log = new JsonObject { ["style"] = styleName, ["component"] = "(dragged_state)" };
                        bool touched = false;
                        foreach (var kv in dsEdits)
                        {
                            object v = JsonScalar(kv.Value);
                            if (!SetPropValue(ds, kv.Key, v))
                                throw new InvalidOperationException("dragged_state." + kv.Key + " could not be set (wrong property name or value type; check key names with dump_label_styles).");
                            log[kv.Key] = kv.Value?.ToString();
                            touched = true;
                        }
                        if (touched) applied.Add(log);
                    }
                }
                tr.Commit();
            }

            if (stylesFound == 0)
                throw new InvalidOperationException("Label style '" + styleName + "'.");
            return new JsonObject
            {
                ["style"] = styleName,
                ["styles_found"] = stylesFound,
                ["edits_applied"] = applied.Count,
                ["applied"] = applied
            };
        }

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

        static JsonObject ShallowPropertyValues(object holder)
        {
            if (holder == null) return null;
            var o = new JsonObject();
            foreach (PropertyInfo p in holder.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                string tn = p.PropertyType.Name;
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
