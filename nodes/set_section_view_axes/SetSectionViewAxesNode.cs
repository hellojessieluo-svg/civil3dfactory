using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivSampleLine = Autodesk.Civil.DatabaseServices.SampleLine;
using CivSampleLineGroup = Autodesk.Civil.DatabaseServices.SampleLineGroup;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivSectionViewStyle = Autodesk.Civil.DatabaseServices.Styles.SectionViewStyle;
using CivAxisStyle = Autodesk.Civil.DatabaseServices.Styles.AxisStyle;
using CivAxisTickStyle = Autodesk.Civil.DatabaseServices.Styles.AxisTickStyle;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        /// <summary>
        ///
        /// </summary>
        static JsonNode RunNodeSetSectionViewAxes(JsonObject a, Document doc)
        {
            string styleName = GetString(a, "style", null);
            bool dry = GetBool(a, "dry_run", true);
            var axesEdit = a["axes"] as JsonObject;
            if (!dry && (string.IsNullOrEmpty(styleName) || axesEdit == null))
                throw new InvalidOperationException("Editing (dry_run:false) requires style and axes.");

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var styles = new JsonArray();
            var applied = new JsonArray();
            bool found = false;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var usage = new Dictionary<ObjectId, int>();
                foreach (ObjectId alId in ModelSpace(db, tr))
                {
                    CivAlignment al;
                    try { al = tr.GetObject(alId, OpenMode.ForRead) as CivAlignment; } catch { continue; }
                    if (al == null) continue;
                    foreach (ObjectId gid in al.GetSampleLineGroupIds())
                    {
                        var g = (CivSampleLineGroup)tr.GetObject(gid, OpenMode.ForRead);
                        foreach (ObjectId slId in g.GetSampleLineIds())
                        {
                            var sl = (CivSampleLine)tr.GetObject(slId, OpenMode.ForRead);
                            foreach (ObjectId svId in sl.GetSectionViewIds())
                            {
                                var sv = tr.GetObject(svId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.SectionView;
                                if (sv == null) continue;
                                usage[sv.StyleId] = (usage.TryGetValue(sv.StyleId, out int n) ? n : 0) + 1;
                            }
                        }
                    }
                }

                foreach (ObjectId sid in civ.Styles.SectionViewStyles)
                {
                    var st = tr.GetObject(sid, OpenMode.ForRead) as CivSectionViewStyle;
                    if (st == null) continue;
                    var info = new JsonObject
                    {
                        ["name"] = st.Name,
                        ["used_by_views"] = usage.TryGetValue(sid, out int u) ? u : 0,
                        ["axes"] = AxesInfo(st)
                    };
                    if (!string.IsNullOrEmpty(styleName) && st.Name == styleName)
                    {
                        found = true;
                        if (!dry)
                        {
                            st.UpgradeOpen();
                            foreach (var kv in axesEdit)
                            {
                                var ax = AxisOf(st, kv.Key);
                                var e = kv.Value as JsonObject;
                                if (ax == null || e == null) throw new InvalidOperationException("axes keys must be bottom/top/left/right/center: " + kv.Key);
                                var log = new JsonObject { ["axis"] = kv.Key };
                                if (e["title"] is JsonObject t)
                                {
                                    if (t["offset_x"] != null) { ax.TitleStyle.OffsetX = t["offset_x"].GetValue<double>(); log["title.offset_x"] = ax.TitleStyle.OffsetX; }
                                    if (t["offset_y"] != null) { ax.TitleStyle.OffsetY = t["offset_y"].GetValue<double>(); log["title.offset_y"] = ax.TitleStyle.OffsetY; }
                                    if (t["text_height"] != null) { ax.TitleStyle.TextHeight = t["text_height"].GetValue<double>(); log["title.text_height"] = ax.TitleStyle.TextHeight; }
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
                            info["axes_after"] = AxesInfo(st);
                        }
                    }
                    styles.Add(info);
                }
                tr.Commit();
            }
            if (!string.IsNullOrEmpty(styleName) && !found)
                throw new InvalidOperationException("Section view style '" + styleName + "'.");
            return new JsonObject { ["dry_run"] = dry, ["styles"] = styles, ["applied"] = applied };
        }

        static CivAxisStyle AxisOf(CivSectionViewStyle st, string key)
        {
            switch ((key ?? "").ToLowerInvariant())
            {
                case "bottom": return st.BottomAxis;
                case "top": return st.TopAxis;
                case "left": return st.LeftAxis;
                case "right": return st.RightAxis;
                case "center": return st.CenterAxis;
                default: return null;
            }
        }

        static JsonObject AxesInfo(CivSectionViewStyle st)
        {
            var o = new JsonObject();
            foreach (var k in new[] { "bottom", "top", "left", "right", "center" })
            {
                CivAxisStyle ax;
                try { ax = AxisOf(st, k); } catch { continue; }
                if (ax == null) continue;
                var axisInfo = new JsonObject();
                try { axisInfo["show"] = ax.ShowTickAndLabel; } catch { }
                try { axisInfo["title"] = new JsonObject { ["text"] = ax.TitleStyle.Text, ["text_height"] = ax.TitleStyle.TextHeight, ["offset_x"] = ax.TitleStyle.OffsetX, ["offset_y"] = ax.TitleStyle.OffsetY, ["location"] = ax.TitleStyle.Location.ToString() }; } catch { }
                try { axisInfo["major"] = TickInfo(ax.MajorTickStyle); } catch { }
                try { axisInfo["minor"] = TickInfo(ax.MinorTickStyle); } catch { }
                o[k] = axisInfo;
            }
            return o;
        }

        static JsonObject TickInfo(CivAxisTickStyle t) => new JsonObject
        {
            ["text_height"] = t.TextHeight, ["interval"] = t.Interval, ["size"] = t.Size,
            ["offset_x"] = t.OffsetX, ["offset_y"] = t.OffsetY, ["label"] = t.LabelText
        };
    }
}
