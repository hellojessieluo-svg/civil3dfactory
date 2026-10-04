using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivSampleLine = Autodesk.Civil.DatabaseServices.SampleLine;
using CivSampleLineGroup = Autodesk.Civil.DatabaseServices.SampleLineGroup;
using CivSection = Autodesk.Civil.DatabaseServices.Section;
using CivSectionView = Autodesk.Civil.DatabaseServices.SectionView;
using CivGradeBreakGroup = Autodesk.Civil.DatabaseServices.SectionGradeBreakLabelGroup;
using CivOffsetGroup = Autodesk.Civil.DatabaseServices.SectionOffsetLabelGroup;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeAddSectionLabels(JsonObject a, Document doc)
        {
            var prefixes = new List<string>(); var pa = a["source_prefixes"] as JsonArray;
            if (pa != null) foreach (JsonNode n in pa) prefixes.Add(n.ToString());
            if (prefixes.Count == 0) throw new InvalidOperationException("source_prefixes is required, e.g. [\"Channel-\",\"Intersection-\"]");
            string labelType = GetString(a, "label_type", "grade_break").ToLowerInvariant();
            bool major = labelType == "major_offset";
            string singleStyle = GetString(a, "style", null);
            string tpl = GetString(a, "style_template", "@C3DF-AdjacentSurface-{name}");
            if (major && string.IsNullOrEmpty(singleStyle)) throw new InvalidOperationException("label_type=major_offset requires style (Major Offset label style name).");
            string fallback = GetString(a, "fallback_style", null);
            double rangeLen = GetDouble(a, "range_len", 0.5);
            double increment = GetDouble(a, "increment", 20);
            bool clear = GetBool(a, "clear", true);
            var only = new HashSet<string>(); var al0 = a["alignments"] as JsonArray;
            if (al0 != null) foreach (JsonNode n in al0) only.Add(n.ToString());

            if (major && !(GetDouble(a, "end_margin", 2.0) < 0 && !GetBool(a, "avoid", true)))
                return RunSectionNamesSoft(a, doc, prefixes, singleStyle, increment, clear, only);

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            int created = 0, skippedEmpty = 0, noStyle = 0, cleared = 0;
            var perSurface = new Dictionary<string, int>();
            var styleMiss = new HashSet<string>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                object gbColl = ResolveStyleCollection(civ, major ? "LabelStyles.SectionLabelStyles.MajorOffsetLabelStyles" : "LabelStyles.SectionLabelStyles.GradeBreakLabelStyles");
                var styleIds = new Dictionary<string, ObjectId>();
                var en = gbColl as System.Collections.IEnumerable;
                if (en != null) foreach (object it in en) { if (!(it is ObjectId)) continue; var o = tr.GetObject((ObjectId)it, OpenMode.ForRead); string nm = TryGetName(o); if (nm != null) styleIds[nm] = (ObjectId)it; }

                foreach (ObjectId aid in civ.GetAlignmentIds())
                {
                    var al = tr.GetObject(aid, OpenMode.ForRead) as CivAlignment;
                    if (al == null || (only.Count > 0 && !only.Contains(al.Name))) continue;
                    foreach (ObjectId gid in al.GetSampleLineGroupIds())
                    {
                        var slg = (CivSampleLineGroup)tr.GetObject(gid, OpenMode.ForWrite);
                        foreach (ObjectId slId in slg.GetSampleLineIds())
                        {
                            var sl = (CivSampleLine)tr.GetObject(slId, OpenMode.ForRead);
                            var viewIds = sl.GetSectionViewIds();
                            if (viewIds.Count == 0) continue;
                            ObjectId viewId = viewIds[0];
                            var sv = (CivSectionView)tr.GetObject(viewId, OpenMode.ForWrite);
                            if (clear)
                            {
                                var oldIds = new List<ObjectId>();
                                try { foreach (ObjectId lg in sv.GetSectionGradeBreakLabelGroupIds()) oldIds.Add(lg); } catch { }
                                try { foreach (ObjectId lg in sv.GetSectionOffsetLabelGroupIds()) oldIds.Add(lg); } catch { }
                                foreach (ObjectId lg in oldIds)
                                { try { var g = tr.GetObject(lg, OpenMode.ForWrite); g.Erase(); cleared++; } catch { } }
                            }
                            foreach (ObjectId secId in sl.GetSectionIds())
                            {
                                var sec = tr.GetObject(secId, OpenMode.ForRead) as CivSection;
                                if (sec == null) continue;
                                string src = null; try { src = sec.SourceName; } catch { }
                                if (src == null) continue;
                                bool hit = false; foreach (string p in prefixes) if (src.StartsWith(p, StringComparison.Ordinal)) { hit = true; break; }
                                if (!hit) continue;
                                double lo, hi;
                                try { lo = sec.LeftOffset; hi = sec.RightOffset; } catch { skippedEmpty++; continue; }
                                string styleName = major ? singleStyle : tpl.Replace("{name}", src);
                                ObjectId stId;
                                if (!styleIds.TryGetValue(styleName, out stId))
                                {
                                    styleMiss.Add(styleName);
                                    if (fallback == null || !styleIds.TryGetValue(fallback, out stId)) { noStyle++; continue; }
                                }
                                try
                                {
                                    if (major)
                                    {
                                        CivOffsetGroup.CreateMajor(viewId, secId, increment, stId);
                                    }
                                    else
                                    {
                                        ObjectId lgId = CivGradeBreakGroup.Create(viewId, secId, stId);
                                        var lg = (CivGradeBreakGroup)tr.GetObject(lgId, OpenMode.ForWrite);
                                        try { lg.RangeStartFromFeature = false; lg.RangeEndFromFeature = false; lg.SetRange(lo, Math.Min(hi, lo + rangeLen)); } catch { }
                                    }
                                    created++;
                                    int c; perSurface.TryGetValue(src, out c); perSurface[src] = c + 1;
                                }
                                catch (System.Exception ex) { skippedEmpty++; if (created == 0 && skippedEmpty < 3) styleMiss.Add("Create failed: " + ex.Message); }
                            }
                        }
                    }
                }
                tr.Commit();
            }
            var ps = new JsonObject(); foreach (var kv in perSurface) ps[kv.Key] = kv.Value;
            var miss = new JsonArray(); foreach (string s in styleMiss) miss.Add(s);
            return new JsonObject { ["created"] = created, ["cleared_old_groups"] = cleared, ["skipped_empty_or_failed"] = skippedEmpty, ["no_style"] = noStyle, ["per_surface"] = ps, ["style_missing"] = miss };
        }

        sealed class SnGroup
        {
            public ObjectId View, Section, Group; public string Surface, Alignment; public double Station;
            public int Index; public List<Point3d> Locs = new List<Point3d>();
        }

        static JsonNode RunSectionNamesSoft(JsonObject a, Document doc, List<string> prefixes, string styleName, double increment, bool clear, HashSet<string> only)
        {
            double endMarginMm = GetDouble(a, "end_margin", 2.0);
            bool avoid = GetBool(a, "avoid", true);
            string pick = GetString(a, "pick", "all").ToLowerInvariant();
            if (pick != "all" && pick != "best") throw new InvalidOperationException("pick must be all or best.");
            bool dryRun = GetBool(a, "dry_run", false);
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            double scale = GetDouble(a, "scale", double.NaN);
            if (double.IsNaN(scale) || scale <= 0)
            { try { var cs = db.Cannoscale; scale = cs.PaperUnits > 0 ? cs.DrawingUnits / cs.PaperUnits : 1; } catch { scale = 1; } }
            double stepMm = GetDouble(a, "step_mm", 4.0);
            double step = stepMm * scale;
            if (step <= 0) throw new InvalidOperationException("step_mm must be positive.");
            if (increment <= 0) throw new InvalidOperationException("increment must be positive.");
            var w = LabelWeights.PlanDefaults().Apply(a["weights"] as JsonObject);
            double textHmm = GetDouble(a, "text_height_mm", 3.5);
            var boxOverride = new Dictionary<string, double[]>();
            var tb = a["text_box"] as JsonObject;
            if (tb != null) foreach (var kv in tb) { var arr = kv.Value as JsonArray; if (arr != null && arr.Count >= 2) boxOverride[kv.Key] = new[] { (double)arr[0], (double)arr[1] }; }
            string obsDwg = GetString(a, "obstacles_dwg", null);
            bool live = GetBool(a, "live_obstacles", true);
            string ignoreText = GetString(a, "ignore_text", null);
            var ignoreRe = string.IsNullOrEmpty(ignoreText) ? null : new System.Text.RegularExpressions.Regex(ignoreText);
            Func<string, bool> skipText = t => ignoreRe != null && t != null && ignoreRe.IsMatch(t.Trim());

            var dwgTexts = new List<KeyValuePair<Extents3d, string>>();
            if (!string.IsNullOrEmpty(obsDwg))
            {
                if (!Path.IsPathRooted(obsDwg) || !File.Exists(obsDwg)) throw new InvalidOperationException("obstacles_dwg must be an existing absolute path: " + obsDwg);
                using (var odb = new Database(false, true))
                {
                    odb.ReadDwgFile(obsDwg, FileOpenMode.OpenForReadAndAllShare, true, null);
                    odb.CloseInput(true);
                    using (var otr = odb.TransactionManager.StartTransaction())
                    {
                        var obt = (BlockTable)otr.GetObject(odb.BlockTableId, OpenMode.ForRead);
                        var oms = (BlockTableRecord)otr.GetObject(obt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                        foreach (ObjectId id in oms)
                        {
                            var e = otr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (e == null) continue;
                            Extents3d bx; string txt; bool isM;
                            if (LpTextBox(e, out bx, out txt, out isM) && txt != null) dwgTexts.Add(new KeyValuePair<Extents3d, string>(bx, txt.Trim()));
                        }
                        otr.Commit();
                    }
                }
            }

            var groups = new List<SnGroup>();
            int cleared = 0, skippedEmpty = 0;
            ObjectId styleIdKeep = ObjectId.Null;
            var styleMiss = new HashSet<string>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                object coll = ResolveStyleCollection(civ, "LabelStyles.SectionLabelStyles.MajorOffsetLabelStyles");
                ObjectId stId = ObjectId.Null;
                var en = coll as System.Collections.IEnumerable;
                if (en != null) foreach (object it in en) { if (!(it is ObjectId)) continue; var o = tr.GetObject((ObjectId)it, OpenMode.ForRead); if (TryGetName(o) == styleName) { stId = (ObjectId)it; break; } }
                if (stId.IsNull) throw new InvalidOperationException("Major Offset label style not found: '" + styleName + "'.");
                styleIdKeep = stId;
                foreach (ObjectId aid in civ.GetAlignmentIds())
                {
                    var al = tr.GetObject(aid, OpenMode.ForRead) as CivAlignment;
                    if (al == null || (only.Count > 0 && !only.Contains(al.Name))) continue;
                    foreach (ObjectId gid in al.GetSampleLineGroupIds())
                    {
                        var slg = (CivSampleLineGroup)tr.GetObject(gid, OpenMode.ForRead);
                        foreach (ObjectId slId in slg.GetSampleLineIds())
                        {
                            var sl = (CivSampleLine)tr.GetObject(slId, OpenMode.ForRead);
                            var viewIds = sl.GetSectionViewIds();
                            if (viewIds.Count == 0) continue;
                            ObjectId viewId = viewIds[0];
                            if (clear && !dryRun)
                            {
                                var sv = (CivSectionView)tr.GetObject(viewId, OpenMode.ForWrite);
                                var oldIds = new List<ObjectId>();
                                try { foreach (ObjectId lg in sv.GetSectionGradeBreakLabelGroupIds()) oldIds.Add(lg); } catch { }
                                try { foreach (ObjectId lg in sv.GetSectionOffsetLabelGroupIds()) oldIds.Add(lg); } catch { }
                                foreach (ObjectId lg in oldIds) { try { tr.GetObject(lg, OpenMode.ForWrite).Erase(); cleared++; } catch { } }
                            }
                            foreach (ObjectId secId in sl.GetSectionIds())
                            {
                                var sec = tr.GetObject(secId, OpenMode.ForRead) as CivSection;
                                if (sec == null) continue;
                                string src = null; try { src = sec.SourceName; } catch { }
                                if (src == null) continue;
                                bool hit = false; foreach (string p in prefixes) if (src.StartsWith(p, StringComparison.Ordinal)) { hit = true; break; }
                                if (!hit) continue;
                                try { double lo0 = sec.LeftOffset; } catch { skippedEmpty++; continue; }
                                try
                                {
                                    ObjectId g = CivOffsetGroup.CreateMajor(viewId, secId, step, stId);
                                    groups.Add(new SnGroup { View = viewId, Section = secId, Group = g, Surface = src, Alignment = al.Name, Station = sl.Station, Index = groups.Count });
                                }
                                catch (System.Exception ex) { skippedEmpty++; if (groups.Count == 0 && skippedEmpty < 3) styleMiss.Add("Create failed: " + ex.Message); }
                            }
                        }
                    }
                }
                tr.Commit();
            }

            int subsTotal = 0, shown = 0, hidden = 0, erasedEmpty = 0, visFail = 0, measured = 0, liveGroups = 0, liveTexts = 0, liveEmpty = 0, dwgUsed = 0, pointsBad = 0, locOutside = 0, fallbackOld = 0;
            var withdrawn = new JsonArray();
            var perSurface = new Dictionary<string, int>();
            var sizeSrc = new Dictionary<string, string>();
            var sizes = new Dictionary<string, double[]>();
            var placedRows = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var mw = new Dictionary<string, List<double[]>>();
                foreach (var g in groups)
                {
                    var grp = tr.GetObject(g.Group, OpenMode.ForRead) as CivOffsetGroup;
                    if (grp == null) continue;
                    try { foreach (var s in grp.SubEntities) g.Locs.Add(s.LabelLocation); } catch { }
                    subsTotal += g.Locs.Count;
                    if (g.Locs.Count == 0) continue;
                    try
                    {
                        Extents3d ex = grp.GeometricExtents;
                        double ax0 = double.MaxValue, ax1 = double.MinValue, ay0 = double.MaxValue, ay1 = double.MinValue;
                        foreach (var p in g.Locs) { ax0 = Math.Min(ax0, p.X); ax1 = Math.Max(ax1, p.X); ay0 = Math.Min(ay0, p.Y); ay1 = Math.Max(ay1, p.Y); }
                        double ww = (ex.MaxPoint.X - ex.MinPoint.X) - (ax1 - ax0), hh = (ex.MaxPoint.Y - ex.MinPoint.Y) - (ay1 - ay0);
                        if (ww > 0.5 * scale && ww < 300 * scale && hh > 0.5 * scale && hh < 40 * scale)
                        {
                            List<double[]> lst; if (!mw.TryGetValue(g.Surface, out lst)) mw[g.Surface] = lst = new List<double[]>();
                            lst.Add(new[] { ww, hh, (ex.MinPoint.X + ex.MaxPoint.X) / 2 - (ax0 + ax1) / 2, (ex.MinPoint.Y + ex.MaxPoint.Y) / 2 - (ay0 + ay1) / 2 });
                        }
                    }
                    catch { }
                }
                var names = new HashSet<string>(); foreach (var g in groups) names.Add(g.Surface);
                foreach (string nm in names)
                {
                    double[] ov;
                    if (boxOverride.TryGetValue(nm, out ov)) { sizes[nm] = new[] { ov[0] * scale, ov[1] * scale, 0, 0 }; sizeSrc[nm] = "text_box"; continue; }
                    List<double[]> lst;
                    if (mw.TryGetValue(nm, out lst) && lst.Count > 0)
                    {
                        Func<int, double> med = k => { var v = new List<double>(); foreach (var r in lst) v.Add(r[k]); v.Sort(); return v[v.Count / 2]; };
                        sizes[nm] = new[] { med(0), med(1), med(2), med(3) }; sizeSrc[nm] = "Measured label-group extents (" + lst.Count + ")"; measured++; continue;
                    }
                    var dw = new List<double>(); var dh = new List<double>();
                    foreach (var kv in dwgTexts) if (kv.Value == nm) { dw.Add(kv.Key.MaxPoint.X - kv.Key.MinPoint.X); dh.Add(kv.Key.MaxPoint.Y - kv.Key.MinPoint.Y); }
                    if (dw.Count > 0) { dw.Sort(); dh.Sort(); sizes[nm] = new[] { dw[dw.Count / 2], dh[dh.Count / 2], 0, 0 }; sizeSrc[nm] = "Measured matching text in obstacles_dwg (" + dw.Count + ")"; continue; }
                    sizes[nm] = new[] { LabelGeo.TextWidth(nm, textHmm, 1.0) * scale, textHmm * 1.3 * scale, 0, 0 };
                    sizeSrc[nm] = "Estimated from character widths and text height " + textHmm + "; wide fonts may be underestimated: supply text_box or obstacles_dwg)";
                }

                var byView = new Dictionary<ObjectId, List<SnGroup>>();
                foreach (var g in groups) { List<SnGroup> l; if (!byView.TryGetValue(g.View, out l)) byView[g.View] = l = new List<SnGroup>(); l.Add(g); }
                var mine = new HashSet<ObjectId>(); foreach (var g in groups) mine.Add(g.Group);
                foreach (var kv in byView)
                {
                    var sv = (CivSectionView)tr.GetObject(kv.Key, OpenMode.ForRead);
                    Func<double, double, LPt> toP = (x, y) => new LPt(x / scale, y / scale);
                    Extents3d vext; try { vext = sv.GeometricExtents; } catch { continue; }
                    double pad = 60 * scale;
                    var vbox = new LBox(vext.MinPoint.X - pad, vext.MinPoint.Y - pad, vext.MaxPoint.X + pad, vext.MaxPoint.Y + pad);
                    var texts = new List<LBox>();
                    foreach (var t in dwgTexts)
                    {
                        if (names.Contains(t.Value) || skipText(t.Value)) continue;
                        var b = t.Key;
                        if (b.MaxPoint.X < vbox.X0 || b.MinPoint.X > vbox.X1 || b.MaxPoint.Y < vbox.Y0 || b.MinPoint.Y > vbox.Y1) continue;
                        LPt p0 = toP(b.MinPoint.X, b.MinPoint.Y), p1 = toP(b.MaxPoint.X, b.MaxPoint.Y);
                        texts.Add(new LBox(p0.X, p0.Y, p1.X, p1.Y)); dwgUsed++;
                    }
                    if (live)
                    {
                        var lg = new List<ObjectId>();
                        try { foreach (ObjectId id in CivilCompat.CorridorPointLabelIds(sv)) lg.Add(id); } catch { }
                        try { foreach (ObjectId id in sv.GetSectionGradeBreakLabelGroupIds()) lg.Add(id); } catch { }
                        try { foreach (ObjectId id in sv.GetSectionSegmentLabelGroupIds()) lg.Add(id); } catch { }
                        try { foreach (ObjectId id in sv.GetSectionOffsetLabelGroupIds()) if (!mine.Contains(id)) lg.Add(id); } catch { }
                        try { foreach (ObjectId id in sv.GetSectionMinorOffsetLabelGroupIds()) lg.Add(id); } catch { }
                        foreach (ObjectId id in lg)
                        {
                            liveGroups++;
                            int got = SnExplodeTexts(tr, id, toP, texts, skipText);
                            if (got == 0) liveEmpty++; liveTexts += got;
                        }
                    }
                    var segs = new List<LSeg>();
                    var ownBySec = new Dictionary<ObjectId, List<LSeg>>();
                    var rangeBySec = new Dictionary<ObjectId, double[]>();
                    ObjectId slId0 = ObjectId.Null; try { slId0 = sv.SampleLineId; } catch { }
                    if (!slId0.IsNull)
                    {
                        var sl0 = (CivSampleLine)tr.GetObject(slId0, OpenMode.ForRead);
                        foreach (ObjectId sid in sl0.GetSectionIds())
                        {
                            var sec = tr.GetObject(sid, OpenMode.ForRead) as CivSection;
                            if (sec == null) continue;
                            double lo, hi;
                            try { lo = sec.LeftOffset; hi = sec.RightOffset; } catch { continue; }
                            double xl = 0, xh = 0, yy = 0;
                            try { sv.FindXYAtOffsetAndElevation(lo, sv.ElevationMin, ref xl, ref yy); sv.FindXYAtOffsetAndElevation(hi, sv.ElevationMin, ref xh, ref yy); rangeBySec[sid] = new[] { Math.Min(xl, xh) / scale, Math.Max(xl, xh) / scale }; } catch { }
                            var own = new List<LSeg>(); LPt? prev = null; bool sane = true;
                            try
                            {
                                foreach (var sp in sec.SectionPoints)
                                {
                                    if (sp.Location.X < lo - 1 || sp.Location.X > hi + 1) { sane = false; break; }
                                    double x = 0, y = 0; sv.FindXYAtOffsetAndElevation(sp.Location.X, sp.Location.Y, ref x, ref y);
                                    var q = toP(x, y);
                                    if (prev.HasValue) own.Add(new LSeg(prev.Value, q));
                                    prev = q;
                                }
                            }
                            catch { sane = false; }
                            if (!sane) { own.Clear(); pointsBad++; }
                            segs.AddRange(own); ownBySec[sid] = own;
                        }
                    }
                    double x0m = 0, y0m = 0;
                    try { sv.FindXYAtOffsetAndElevation(0, sv.ElevationMin, ref x0m, ref y0m); } catch { }
                    var req = new List<LabelPlacer.NameLine>();
                    foreach (var g in kv.Value)
                    {
                        if (g.Locs.Count == 0) continue;
                        foreach (var p in g.Locs) if (p.X < vext.MinPoint.X - scale || p.X > vext.MaxPoint.X + scale || p.Y < vext.MinPoint.Y - scale || p.Y > vext.MaxPoint.Y + scale) locOutside++;
                        var sz = sizes[g.Surface];
                        double[] rg;
                        if (!rangeBySec.TryGetValue(g.Section, out rg))
                        {
                            double a0 = double.MaxValue, a1 = double.MinValue;
                            foreach (var p in g.Locs) { a0 = Math.Min(a0, p.X / scale); a1 = Math.Max(a1, p.X / scale); }
                            rg = new[] { a0, a1 };
                        }
                        var L = new LabelPlacer.NameLine { Name = g.Surface, Tag = g.Index, Lo = rg[0], Hi = rg[1], W = sz[0] / scale, H = sz[1] / scale, Dy = sz[3] / scale };
                        List<LSeg> own; if (ownBySec.TryGetValue(g.Section, out own)) L.Own = own;
                        foreach (var p in g.Locs) L.Cands.Add(toP(p.X + sz[2], p.Y));
                        req.Add(L);
                    }
                    var prm = new LabelPlacer.NamePlaceParams { X0 = x0m / scale, IncMm = increment / scale, EndGapMm = endMarginMm, Avoid = avoid, PickBest = pick == "best" };
                    var res = LabelPlacer.PlaceSurfaceNames(req, texts, segs, prm, w);
                    var keep = new Dictionary<int, HashSet<int>>();
                    foreach (var r in res)
                    {
                        var g = groups[r.Line.Tag];
                        if (!r.Placed)
                        {
                            withdrawn.Add(new JsonObject { ["alignment"] = g.Alignment, ["station"] = Math.Round(g.Station, 3), ["surface"] = g.Surface, ["why"] = r.Why, ["target_offset"] = Math.Round(r.TargetX * scale - x0m, 2) });
                            continue;
                        }
                        HashSet<int> ks; if (!keep.TryGetValue(g.Index, out ks)) keep[g.Index] = ks = new HashSet<int>();
                        ks.Add(r.Cand);
                        if (placedRows.Count < 2000)
                            placedRows.Add(new JsonObject { ["alignment"] = g.Alignment, ["station"] = Math.Round(g.Station, 3), ["surface"] = g.Surface, ["offset"] = Math.Round(g.Locs[r.Cand].X - x0m, 2), ["cost"] = Math.Round(r.Cost, 2) });
                    }
                    foreach (var g in kv.Value)
                    {
                        HashSet<int> ks; keep.TryGetValue(g.Index, out ks);
                        if (dryRun) { if (ks != null) { shown += ks.Count; int c0; perSurface.TryGetValue(g.Surface, out c0); perSurface[g.Surface] = c0 + ks.Count; } continue; }
                        var grp = (CivOffsetGroup)tr.GetObject(g.Group, OpenMode.ForWrite);
                        if (ks == null || ks.Count == 0) { grp.Erase(); erasedEmpty++; continue; }
                        int i = 0, shown0 = 0, hidden0 = 0; bool failed = false;
                        foreach (var s in grp.SubEntities)
                        {
                            bool on = ks.Contains(i++);
                            try { if (s.Visibility != on) s.Visibility = on; if (s.Visibility != on) throw new InvalidOperationException("Could not set Visibility"); if (on) shown0++; else hidden0++; }
                            catch { failed = true; break; }
                        }
                        if (failed)
                        {
                            visFail++;
                            try { grp.Erase(); CivOffsetGroup.CreateMajor(g.View, g.Section, increment, styleIdKeep); fallbackOld++; } catch { }
                            continue;
                        }
                        shown += shown0; hidden += hidden0;
                        int c; perSurface.TryGetValue(g.Surface, out c); perSurface[g.Surface] = c + ks.Count;
                    }
                }
                if (dryRun)
                {
                    foreach (var g in groups) { try { tr.GetObject(g.Group, OpenMode.ForWrite).Erase(); } catch { } }
                }
                tr.Commit();
            }

            var ps = new JsonObject(); foreach (var kv in perSurface) ps[kv.Key] = kv.Value;
            var sz2 = new JsonObject();
            foreach (var kv in sizes) sz2[kv.Key] = new JsonObject { ["w_mm"] = Math.Round(kv.Value[0] / scale, 2), ["h_mm"] = Math.Round(kv.Value[1] / scale, 2), ["dx_mm"] = Math.Round(kv.Value[2] / scale, 2), ["dy_mm"] = Math.Round(kv.Value[3] / scale, 2), ["source"] = sizeSrc[kv.Key] };
            var miss = new JsonArray(); foreach (string s in styleMiss) miss.Add(s);
            string obsNote = (dwgUsed == 0 && liveTexts == 0)
                ? "No existing text could be read from obstacles_dwg or exploded label groups. Only endpoint clearance and surface-name conflicts were checked. Supply obstacles_dwg to avoid other labels."
                : "Existing text: exported drawing " + dwgUsed + "; exploded label groups " + liveTexts + " items";
            return new JsonObject
            {
                ["mode"] = "major_offset weighted placement" + (dryRun ? "; dry_run left the drawing unchanged" : ""),
                ["created"] = shown,
                ["groups"] = groups.Count, ["candidates"] = subsTotal, ["shown"] = shown, ["hidden"] = hidden,
                ["withdrawn_count"] = withdrawn.Count, ["erased_empty_groups"] = erasedEmpty, ["visibility_failed"] = visFail, ["fallback_old_groups"] = fallbackOld,
                ["cleared_old_groups"] = cleared, ["skipped_empty_or_failed"] = skippedEmpty, ["section_points_unusable"] = pointsBad, ["candidates_outside_view"] = locOutside,
                ["per_surface"] = ps, ["text_box"] = sz2, ["text_box_measured"] = measured, ["scale"] = scale, ["step"] = Math.Round(step, 4), ["increment"] = increment,
                ["end_margin_mm"] = endMarginMm, ["avoid"] = avoid, ["pick"] = pick, ["weights"] = w.ToJson(),
                ["obstacles"] = new JsonObject { ["dwg_texts"] = dwgUsed, ["live_groups"] = liveGroups, ["live_texts"] = liveTexts, ["live_groups_without_text"] = liveEmpty, ["note"] = obsNote },
                ["withdrawn"] = withdrawn, ["placed"] = placedRows, ["style_missing"] = miss,
                ["note"] = dryRun ? "dry_run: candidate groups created and removed; drawing unchanged" : "Memory only; call save_dwg to persist. Rerun after section geometry changes: hidden sublabels are tracked by index."
            };
        }

        static int SnExplodeTexts(Transaction tr, ObjectId id, Func<double, double, LPt> toP, List<LBox> texts, Func<string, bool> skip)
        {
            int n = 0;
            Entity e; try { e = tr.GetObject(id, OpenMode.ForRead) as Entity; } catch { return 0; }
            if (e == null) return 0;
            var parts = new DBObjectCollection();
            try { e.Explode(parts); } catch { return 0; }
            var queue = new List<DBObject>(); foreach (DBObject o in parts) queue.Add(o);
            for (int depth = 0; depth < 3 && queue.Count > 0; depth++)
            {
                var next = new List<DBObject>();
                foreach (DBObject o in queue)
                {
                    try
                    {
                        var t = o as DBText; var m = o as MText; var br = o as BlockReference;
                        if (t != null && !string.IsNullOrWhiteSpace(t.TextString) && !skip(t.TextString))
                        {
                            var b = t.GeometricExtents; LPt p0 = toP(b.MinPoint.X, b.MinPoint.Y), p1 = toP(b.MaxPoint.X, b.MaxPoint.Y);
                            texts.Add(new LBox(p0.X, p0.Y, p1.X, p1.Y)); n++;
                        }
                        else if (m != null && !string.IsNullOrWhiteSpace(m.Text) && !skip(m.Text))
                        {
                            double ww = m.ActualWidth, hh = m.ActualHeight, x = m.Location.X, y = m.Location.Y, rot = m.Rotation;
                            int at = (int)m.Attachment; int col = (at - 1) % 3, row = (at - 1) / 3;
                            double lx0 = col == 0 ? 0 : col == 1 ? -ww / 2 : -ww;
                            double ly1 = row == 0 ? 0 : row == 1 ? hh / 2 : hh;
                            double c = Math.Cos(rot), sn = Math.Sin(rot);
                            double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue;
                            foreach (var q in new[] { new[] { lx0, ly1 - hh }, new[] { lx0 + ww, ly1 - hh }, new[] { lx0, ly1 }, new[] { lx0 + ww, ly1 } })
                            {
                                double qx = x + q[0] * c - q[1] * sn, qy = y + q[0] * sn + q[1] * c;
                                bx0 = Math.Min(bx0, qx); by0 = Math.Min(by0, qy); bx1 = Math.Max(bx1, qx); by1 = Math.Max(by1, qy);
                            }
                            LPt p0 = toP(bx0, by0), p1 = toP(bx1, by1);
                            if (ww > 0 && hh > 0) { texts.Add(new LBox(p0.X, p0.Y, p1.X, p1.Y)); n++; }
                        }
                        else if (br != null)
                        {
                            var sub = new DBObjectCollection();
                            try { br.Explode(sub); foreach (DBObject s in sub) next.Add(s); } catch { }
                        }
                    }
                    catch { }
                    finally { o.Dispose(); }
                }
                queue = next;
            }
            foreach (DBObject o in queue) o.Dispose();
            return n;
        }
    }
}
