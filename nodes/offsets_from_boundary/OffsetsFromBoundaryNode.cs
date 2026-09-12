using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlign = Autodesk.Civil.DatabaseServices.Alignment;
using CivilDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// offsets_from_boundary (S02): split a closed boundary polyline into left/right offset alignments for the corridor to use as width targets.
    ///
    /// Approach: project every boundary vertex onto the centerline via StationOffset as (station, offset), bucket by interval,
    /// most negative offset in a bucket = left half-width, most positive = right half-width, then project back with PointLocation(station, +/-halfwidth).
    /// This also solves two problems: end-cap vertices drop out naturally (they are never the extreme at any station);
    /// the hundreds of tiny vertices of a TIN outline get thinned into a regular line at the given interval.
    ///
    /// Naming follows the create_corridor convention: {channel}_L / {channel}_R (it finds targets by name prefix, not by offset distance).
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeOffsetsFromBoundary(JsonObject args, Document doc)
            => OffsetsFromBoundary(args, doc);

        public static JsonNode OffsetsFromBoundary(JsonObject a, Document doc)
        {
            string bPrefix = GetString(a, "boundary_prefix", "BOUNDARY-");
            double interval = GetDouble(a, "interval", 25.0);
            if (interval <= 1e-6) interval = 25.0;
            string onlyCh = GetString(a, "channel", null);
            string style = GetString(a, "style", null);
            string labelSet = GetString(a, "label_set", null);
            bool eraseBoundary = GetBool(a, "erase_boundary", false);
            bool replaceExisting = GetBool(a, "replace_existing", true);
            double minArea = GetDouble(a, "min_boundary_area", 100.0);

            Database db = doc.Database;
            var made = new JsonArray();
            var notes = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivilDoc civ = Civ(db);
                ObjectId styleId = FindStyleId(tr, civ.Styles.AlignmentStyles, style);
                ObjectId labelId = FindStyleId(tr,
                    civ.Styles.LabelSetStyles.AlignmentLabelSetStyles, labelSet);
                if (labelId.IsNull)
                    throw new InvalidOperationException("The drawing has no alignment label set style; cannot create offset alignments.");

                var byName = new Dictionary<string, CivAlign>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId aid in civ.GetAlignmentIds())
                {
                    var x = tr.GetObject(aid, OpenMode.ForRead) as CivAlign;
                    if (x != null) byName[x.Name] = x;
                }

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // Collect boundary polylines
                var bounds = new List<KeyValuePair<string, Polyline>>();
                foreach (ObjectId id in ms)
                {
                    var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (pl == null) continue;
                    string layer = pl.Layer ?? "";
                    if (!layer.StartsWith(bPrefix, StringComparison.Ordinal)) continue;
                    string ch = layer.Substring(bPrefix.Length);
                    if (string.IsNullOrWhiteSpace(ch)) continue;
                    if (!string.IsNullOrWhiteSpace(onlyCh) &&
                        !string.Equals(ch, onlyCh, StringComparison.OrdinalIgnoreCase)) continue;
                    double area = 0.0;
                    try { area = Math.Abs(pl.Area); } catch (System.Exception) { }
                    if (area < minArea)
                    { notes.Add("Discarded fragment boundary " + layer + " (area " + Math.Round(area, 2) + ")"); continue; }
                    bounds.Add(new KeyValuePair<string, Polyline>(ch, pl));
                }
                if (bounds.Count == 0)
                    throw new InvalidOperationException("No boundary polyline found (prefix \"" + bPrefix + "\").");

                foreach (var kv in bounds)
                {
                    string ch = kv.Key;
                    Polyline bpl = kv.Value;
                    CivAlign center;
                    if (!byName.TryGetValue(ch, out center))
                    { notes.Add("Boundary " + ch + ": no centerline alignment with the same name, skipped"); continue; }

                    double s0 = center.StartingStation, s1 = center.EndingStation;
                    int nv = bpl.NumberOfVertices;

                    // Probe half-length: largest boundary-vertex offset plus a margin, so the perpendicular always crosses both sides
                    double probeHalf = 0.0;
                    for (int i = 0; i < nv; i++)
                    {
                        Point2d p = bpl.GetPoint2dAt(i);
                        double st = 0, off = 0; bool oor = false;
                        try { center.StationOffsetAcceptOutOfRange(p.X, p.Y, ref st, ref off, ref oor); }
                        catch (System.Exception) { continue; }
                        if (Math.Abs(off) > probeHalf) probeHalf = Math.Abs(off);
                    }
                    probeHalf = probeHalf * 1.5 + 10.0;

                    // WARNING: do not use "bucket vertices by station and take extremes": TIN outline vertices are very unevenly spread along the station,
                    // some buckets miss the outermost vertex and the line gets pinched (measured: B1 left side pinched to 18.6m, actual 35m).
                    // Use geometric intersection instead: at each station cast a perpendicular across the boundary; the intersections are the true left/right edges.
                    var lp = new List<Point2d>();
                    var rp = new List<Point2d>();
                    double lMin = double.MaxValue, lMax = 0, rMin = double.MaxValue, rMax = 0;
                    int nStep = Math.Max(2, (int)Math.Ceiling((s1 - s0) / interval));
                    double eps = Math.Min(0.05, (s1 - s0) * 1e-4);   // stay clear of the end caps
                    for (int i = 0; i <= nStep; i++)
                    {
                        double st = s0 + (s1 - s0) * i / (double)nStep;
                        if (i == 0) st += eps;
                        if (i == nStep) st -= eps;

                        double ax = 0, ay = 0, bx = 0, by = 0;
                        try
                        {
                            center.PointLocation(st, -probeHalf, ref ax, ref ay);
                            center.PointLocation(st, probeHalf, ref bx, ref by);
                        }
                        catch (System.Exception) { continue; }

                        double lo = 0.0, hi = 0.0;
                        bool okL = false, okR = false;
                        using (var probe = new Line(new Point3d(ax, ay, 0.0), new Point3d(bx, by, 0.0)))
                        {
                            var xs = new Point3dCollection();
                            try { probe.IntersectWith(bpl, Intersect.OnBothOperands, xs, IntPtr.Zero, IntPtr.Zero); }
                            catch (System.Exception) { continue; }
                            foreach (Point3d x in xs)
                            {
                                double xst = 0, xoff = 0; bool oor = false;
                                try { center.StationOffsetAcceptOutOfRange(x.X, x.Y, ref xst, ref xoff, ref oor); }
                                catch (System.Exception) { continue; }
                                if (xoff < 0) { if (!okL || -xoff > lo) { lo = -xoff; okL = true; } }
                                else { if (!okR || xoff > hi) { hi = xoff; okR = true; } }
                            }
                        }
                        if (!okL || !okR || lo <= 1e-9 || hi <= 1e-9) continue;

                        double e = 0, n = 0;
                        try { center.PointLocation(st, -lo, ref e, ref n); lp.Add(new Point2d(e, n)); }
                        catch (System.Exception) { continue; }
                        try { center.PointLocation(st, hi, ref e, ref n); rp.Add(new Point2d(e, n)); }
                        catch (System.Exception) { continue; }
                        if (lo < lMin) lMin = lo; if (lo > lMax) lMax = lo;
                        if (hi < rMin) rMin = hi; if (hi > rMax) rMax = hi;
                    }
                    if (lp.Count < 2 || rp.Count < 2)
                    { notes.Add("Boundary " + ch + ": not enough valid samples (left " + lp.Count + " right " + rp.Count + "), skipped"); continue; }

                    string lName = ch + "_L", rName = ch + "_R";
                    ObjectId lId = OfbMakeAlignment(tr, civ, ms, db, lp, lName, styleId, labelId,
                                                    byName, replaceExisting, notes);
                    ObjectId rId = OfbMakeAlignment(tr, civ, ms, db, rp, rName, styleId, labelId,
                                                    byName, replaceExisting, notes);

                    var row = new JsonObject
                    {
                        ["channel"] = ch,
                        ["boundary_vertices"] = nv,
                        ["samples"] = lp.Count,
                        ["left_name"] = lName,
                        ["right_name"] = rName,
                        ["left_offset_min"] = Round(lMin, 3),
                        ["left_offset_max"] = Round(lMax, 3),
                        ["right_offset_min"] = Round(rMin, 3),
                        ["right_offset_max"] = Round(rMax, 3),
                        ["width_min"] = Round(lMin + rMin, 3),
                        ["width_max"] = Round(lMax + rMax, 3)
                    };
                    if (!lId.IsNull)
                    {
                        var al = tr.GetObject(lId, OpenMode.ForRead) as CivAlign;
                        if (al != null) row["left_length"] = Round(al.Length, 3);
                    }
                    if (!rId.IsNull)
                    {
                        var al = tr.GetObject(rId, OpenMode.ForRead) as CivAlign;
                        if (al != null) row["right_length"] = Round(al.Length, 3);
                    }
                    made.Add(row);

                    if (eraseBoundary)
                    { try { bpl.UpgradeOpen(); bpl.Erase(); } catch (System.Exception) { } }
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["channels"] = made.Count,
                ["interval"] = interval,
                ["items"] = made,
                ["notes"] = notes
            };
        }

        /// <summary>Point list -> temporary polyline -> alignment. The temporary line is removed by CreateAlignmentFromEntity's eraseSource.</summary>
        static ObjectId OfbMakeAlignment(Transaction tr, CivilDoc civ, BlockTableRecord ms, Database db,
            List<Point2d> pts, string name, ObjectId styleId, ObjectId labelId,
            Dictionary<string, CivAlign> byName, bool replaceExisting, JsonArray notes)
        {
            // Same name exists: rename it out of the way and delete only after the new one succeeds (never delete first; a failure would lose the original)
            CivAlign old = null;
            string parked = null;
            if (byName.ContainsKey(name))
            {
                if (!replaceExisting)
                { notes.Add("Offset alignment " + name + " already exists, not replaced"); return ObjectId.Null; }
                old = byName[name];
                try
                {
                    old.UpgradeOpen();
                    parked = name + "_old_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                    old.Name = parked;
                }
                catch (System.Exception ex)
                { notes.Add("Offset alignment " + name + " could not be renamed out of the way: " + ex.Message); return ObjectId.Null; }
            }

            var pl = new Polyline(pts.Count);
            pl.SetDatabaseDefaults(db);
            for (int i = 0; i < pts.Count; i++) pl.AddVertexAt(i, pts[i], 0.0, 0.0, 0.0);
            pl.Closed = false;
            ms.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);

            ObjectId id;
            try
            {
                id = CreateAlignmentFromEntity(tr, civ, name, ObjectId.Null, pl.ObjectId,
                    db.Clayer, styleId, labelId, true, false);
            }
            catch (System.Exception ex)
            {
                try { pl.Erase(); } catch (System.Exception) { }
                if (old != null)
                {
                    try { old.Name = name; }
                    catch (System.Exception) { notes.Add("WARNING: old alignment could not be renamed back, now named " + parked); }
                }
                notes.Add("Failed to create offset alignment " + name + ": " + ex.Message);
                return ObjectId.Null;
            }

            if (old != null)
            {
                try { old.Erase(); }
                catch (System.Exception ex)
                { notes.Add("WARNING: " + name + " new one created but the old one could not be deleted (now named " + parked + "): " + ex.Message); }
            }
            return id;
        }
    }
}
