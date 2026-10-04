using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivProfileView = Autodesk.Civil.DatabaseServices.ProfileView;
using CivProfileViewStyle = Autodesk.Civil.DatabaseServices.Styles.ProfileViewStyle;
using CivAxisStyle = Autodesk.Civil.DatabaseServices.Styles.AxisStyle;
using CivAxisTickStyle = Autodesk.Civil.DatabaseServices.Styles.AxisTickStyle;
using CivGraphTitleStyle = Autodesk.Civil.DatabaseServices.Styles.GraphTitleStyle;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        /// <summary>
        ///
        /// </summary>
        static JsonNode RunNodeSetProfileViewAxes(JsonObject a, Document doc)
        {
            string styleName = GetString(a, "style", null);
            bool dry = GetBool(a, "dry_run", true);
            var axesEdit = a["axes"] as JsonObject;
            var titleEdit = a["title"] as JsonObject;
            if (!dry && (string.IsNullOrEmpty(styleName) || (axesEdit == null && titleEdit == null)))
                throw new InvalidOperationException("Editing (dry_run:false) requires style and at least one of axes or title.");

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var styles = new JsonArray();
            var applied = new JsonArray();
            bool found = false;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var usage = new Dictionary<ObjectId, int>();
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    CivProfileView pv;
                    try { pv = tr.GetObject(id, OpenMode.ForRead) as CivProfileView; } catch { continue; }
                    if (pv == null) continue;
                    usage[pv.StyleId] = (usage.TryGetValue(pv.StyleId, out int n) ? n : 0) + 1;
                }

                foreach (ObjectId sid in civ.Styles.ProfileViewStyles)
                {
                    var st = tr.GetObject(sid, OpenMode.ForRead) as CivProfileViewStyle;
                    if (st == null) continue;
                    var info = new JsonObject
                    {
                        ["name"] = st.Name,
                        ["used_by_views"] = usage.TryGetValue(sid, out int u) ? u : 0,
                        ["title"] = TitleInfo(st),
                        ["axes"] = ProfileAxesInfo(st)
                    };
                    if (!string.IsNullOrEmpty(styleName) && st.Name == styleName)
                    {
                        found = true;
                        if (!dry)
                        {
                            st.UpgradeOpen();
                            if (titleEdit != null)
                            {
                                CivGraphTitleStyle ts = st.GraphStyle.TitleStyle;
                                var log = new JsonObject { ["title"] = true };
                                if (titleEdit["text"] != null) { ts.Text = titleEdit["text"].GetValue<string>(); log["text"] = ts.Text; }
                                if (titleEdit["text_height"] != null) { ts.TextHeight = titleEdit["text_height"].GetValue<double>(); log["text_height"] = ts.TextHeight; }
                                if (titleEdit["offset_x"] != null) { ts.OffsetX = titleEdit["offset_x"].GetValue<double>(); log["offset_x"] = ts.OffsetX; }
                                if (titleEdit["offset_y"] != null) { ts.OffsetY = titleEdit["offset_y"].GetValue<double>(); log["offset_y"] = ts.OffsetY; }
                                applied.Add(log);
                            }
                            if (axesEdit != null)
                            {
                                foreach (var kv in axesEdit)
                                {
                                    var ax = ProfileAxisOf(st, kv.Key);
                                    var e = kv.Value as JsonObject;
                                    if (ax == null || e == null) throw new InvalidOperationException("axes keys must be bottom/top/left/right: " + kv.Key);
                                    var log = new JsonObject { ["axis"] = kv.Key };
                                    if (e["title"] is JsonObject t)
                                    {
                                        if (t["offset_x"] != null) { ax.TitleStyle.OffsetX = t["offset_x"].GetValue<double>(); log["title.offset_x"] = ax.TitleStyle.OffsetX; }
                                        if (t["offset_y"] != null) { ax.TitleStyle.OffsetY = t["offset_y"].GetValue<double>(); log["title.offset_y"] = ax.TitleStyle.OffsetY; }
                                        if (t["text_height"] != null) { ax.TitleStyle.TextHeight = t["text_height"].GetValue<double>(); log["title.text_height"] = ax.TitleStyle.TextHeight; }
                                        if (t["text"] != null) { ax.TitleStyle.Text = t["text"].GetValue<string>(); log["title.text"] = ax.TitleStyle.Text; }
                                    }
                                    foreach (var tick in new[] { "major", "minor" })
                                    {
                                        if (!(e[tick] is JsonObject m)) continue;
                                        CivAxisTickStyle ts = tick == "major" ? ax.MajorTickStyle : ax.MinorTickStyle;
                                        if (m["text_height"] != null) { ts.TextHeight = m["text_height"].GetValue<double>(); log[tick + ".text_height"] = ts.TextHeight; }
                                        if (m["interval"] != null) { ts.Interval = m["interval"].GetValue<double>(); log[tick + ".interval"] = ts.Interval; }
                                        if (m["offset_x"] != null) { ts.OffsetX = m["offset_x"].GetValue<double>(); log[tick + ".offset_x"] = ts.OffsetX; }
                                        if (m["offset_y"] != null) { ts.OffsetY = m["offset_y"].GetValue<double>(); log[tick + ".offset_y"] = ts.OffsetY; }
                                        if (m["size"] != null) { ts.Size = m["size"].GetValue<double>(); log[tick + ".size"] = ts.Size; }
                                    }
                                    applied.Add(log);
                                }
                            }
                            info["title_after"] = TitleInfo(st);
                            info["axes_after"] = ProfileAxesInfo(st);
                        }
                    }
                    styles.Add(info);
                }
                tr.Commit();
            }
            if (!string.IsNullOrEmpty(styleName) && !found)
                throw new InvalidOperationException("Profile view style not found: '" + styleName + "'.");
            return new JsonObject { ["dry_run"] = dry, ["styles"] = styles, ["applied"] = applied };
        }

        static CivAxisStyle ProfileAxisOf(CivProfileViewStyle st, string key)
        {
            switch ((key ?? "").ToLowerInvariant())
            {
                case "bottom": return st.BottomAxis;
                case "top": return st.TopAxis;
                case "left": return st.LeftAxis;
                case "right": return st.RightAxis;
                default: return null;
            }
        }

        static JsonObject TitleInfo(CivProfileViewStyle st)
        {
            var o = new JsonObject();
            try
            {
                CivGraphTitleStyle ts = st.GraphStyle.TitleStyle;
                o["text"] = ts.Text; o["text_height"] = ts.TextHeight;
                o["offset_x"] = ts.OffsetX; o["offset_y"] = ts.OffsetY;
                try { o["location"] = ts.Location.ToString(); } catch { }
                try { o["justification"] = ts.Justification.ToString(); } catch { }
            }
            catch (Exception ex) { o["error"] = ex.Message; }
            return o;
        }

        static JsonObject ProfileAxesInfo(CivProfileViewStyle st)
        {
            var o = new JsonObject();
            foreach (var k in new[] { "bottom", "top", "left", "right" })
            {
                CivAxisStyle ax;
                try { ax = ProfileAxisOf(st, k); } catch { continue; }
                if (ax == null) continue;
                var axisInfo = new JsonObject();
                try { axisInfo["show"] = ax.ShowTickAndLabel; } catch { }
                try { axisInfo["title"] = new JsonObject { ["text"] = ax.TitleStyle.Text, ["text_height"] = ax.TitleStyle.TextHeight, ["offset_x"] = ax.TitleStyle.OffsetX, ["offset_y"] = ax.TitleStyle.OffsetY }; } catch { }
                try { axisInfo["major"] = TickInfo(ax.MajorTickStyle); } catch { }
                try { axisInfo["minor"] = TickInfo(ax.MinorTickStyle); } catch { }
                o[k] = axisInfo;
            }
            return o;
        }
    }
}
