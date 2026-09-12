using System;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using Civil3DFactory.Geometry;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeRebuildFilletNetwork(JsonObject args, Document doc)
            => RebuildFilletNetwork(args, doc);

        // Rebuild the fillet network (point-to-point contract): re-solve each corner from the pairing table (FilletSolveCore is the single source of truth),
        // rebuild entities in place (create the corner if missing), write C3DF_FILLET XData for lineage (parent handles + anchor stations + radius,
        // so dragging a parent on a machine with WaterBox updates it automatically), and snap the region ends of both parent segments exactly to the outer leg ends.
        // The report carries the measured gap at every joint -- the reconciliation sheet for "segment end == corner start".
        static JsonNode RebuildFilletNetwork(JsonObject a, Document doc)
        {
            var arr = a["corners"] as JsonArray;
            if (arr == null || arr.Count == 0)
                throw new InvalidOperationException("Requires corners:[{corner,a,b},...]");
            double radius = GetDouble(a, "radius", 20);
            double leg = GetDouble(a, "leg", 0.1);

            Database db = doc.Database;
            var civ = Civ(db);
            var report = new JsonArray();
            int done = 0, created = 0, failed = 0;
            double worstGap = 0;
            string worstAt = "";

            // First erase the named leftovers (user test residue etc.)
            if (a["erase"] is JsonArray er)
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (JsonNode n in er)
                    {
                        string nm = n.GetValue<string>();
                        try { EraseAlignments(tr, civ, nm); report.Add("Erased " + nm); }
                        catch (System.Exception ex) { report.Add("Erase " + nm + " failed: " + ex.Message); }
                    }
                    tr.Commit();
                }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // Register the C3DF_FILLET application (XData prerequisite)
                var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
                if (!rat.Has("C3DF_FILLET"))
                {
                    rat.UpgradeOpen();
                    var rec = new RegAppTableRecord { Name = "C3DF_FILLET" };
                    rat.Add(rec);
                    tr.AddNewlyCreatedDBObject(rec, true);
                }

                Point3d PtAt(CivAlignment al, double sta)
                {
                    double e = 0, n = 0;
                    al.PointLocation(sta, 0, ref e, ref n);
                    return new Point3d(e, n, 0);
                }
                double DistTo(CivAlignment al, Point3d p)
                {
                    try { return (al.GetClosestPointTo(p, false) - p).Length; }
                    catch { return double.MaxValue; }
                }
                // Fallback pick when the corner is missing: the point on A closest to B (mouths/intersections are naturally the closest approach)
                Point3d NearestOn(CivAlignment src, CivAlignment other)
                {
                    double len = src.EndingStation - src.StartingStation;
                    int steps = Math.Max(10, (int)(len / 2));
                    double bestD = double.MaxValue;
                    Point3d bestP = default;
                    for (int i = 0; i <= steps; i++)
                    {
                        var p = PtAt(src, src.StartingStation + len * i / steps);
                        double d = DistTo(other, p);
                        if (d < bestD) { bestD = d; bestP = p; }
                    }
                    return bestP;
                }

                foreach (JsonNode n in arr)
                {
                    var o = (JsonObject)n;
                    string cname = Need(o, "corner");
                    var A = FindAlignment(tr, civ, Need(o, "a"));
                    var B = FindAlignment(tr, civ, Need(o, "b"));
                    if (A == null || B == null)
                    { report.Add(cname + ": parent alignment missing, skipped"); failed++; continue; }
                    double r0 = GetDouble(o, "radius", radius);

                    var corner = FindAlignment(tr, civ, cname);
                    Point3d pickA, pickB;
                    Point3d? PickArg(string key)
                    {
                        if (o[key] is not JsonArray pa || pa.Count < 2) return null;
                        return new Point3d(pa[0].GetValue<double>(), pa[1].GetValue<double>(), 0);
                    }
                    var expA = PickArg("pick_a");
                    var expB = PickArg("pick_b");
                    if (expA.HasValue && expB.HasValue)
                    { pickA = expA.Value; pickB = expB.Value; }
                    else if (corner != null)
                    {
                        // The existing corner's start/end points are the best pick hints; assign by which parent each is closer to
                        var ps = PtAt(corner, corner.StartingStation);
                        var pe = PtAt(corner, corner.EndingStation);
                        if (DistTo(A, ps) + DistTo(B, pe) <= DistTo(B, ps) + DistTo(A, pe))
                        { pickA = ps; pickB = pe; }
                        else
                        { pickA = pe; pickB = ps; }
                    }
                    else
                    {
                        pickA = NearestOn(A, B);
                        try { pickB = B.GetClosestPointTo(pickA, false); }
                        catch { report.Add(cname + ": fallback pick failed, skipped"); failed++; continue; }
                    }

                    // Solve + fixed-point iteration: Solve treats the neighbourhood of the pick as straight; when a parent (especially a boundary loop)
                    // bends within the tangent span, the first solution's tangent points float off -- move the picks to the current tangent points and re-solve until they sit on the line.
                    var sv = FilletSolveCore.Solve(A, pickA, B, pickB, r0, leg);
                    for (int it = 0; it < 5 && sv.Error == null; it++)
                    {
                        double offT1 = DistTo(A, sv.Line1End), offT2 = DistTo(B, sv.Line2Start);
                        if (offT1 < 0.002 && offT2 < 0.002) break;
                        Point3d nA = pickA, nB = pickB;
                        try { if (offT1 >= 0.002) nA = A.GetClosestPointTo(sv.Line1End, false); } catch { }
                        try { if (offT2 >= 0.002) nB = B.GetClosestPointTo(sv.Line2Start, false); } catch { }
                        if ((nA - pickA).Length < 0.001 && (nB - pickB).Length < 0.001) break;
                        pickA = nA; pickB = nB;
                        var sv2 = FilletSolveCore.Solve(A, pickA, B, pickB, r0, leg);
                        if (sv2.Error != null) break;   // if the iteration goes bad keep the previous solution
                        sv = sv2;
                    }
                    if (sv.Error != null)
                    { report.Add(cname + ": " + sv.Error); failed++; continue; }

                    CivAlignment fw;
                    if (corner == null)
                    {
                        ObjectId styleId = FindStyleId(tr, civ.Styles.AlignmentStyles, null);
                        ObjectId labelId = FindStyleId(tr, civ.Styles.LabelSetStyles.AlignmentLabelSetStyles, null);
                        ObjectId alId = CivAlignment.Create(civ, cname, ObjectId.Null, db.Clayer, styleId, labelId);
                        fw = (CivAlignment)tr.GetObject(alId, OpenMode.ForWrite);
                        created++;
                    }
                    else
                        fw = (CivAlignment)tr.GetObject(corner.ObjectId, OpenMode.ForWrite);

                    var ents = fw.Entities;
                    while (ents.Count > 0) ents.Remove(ents[0]);
                    var l1 = ents.AddFixedLine(sv.Line1Start, sv.Line1End);
                    var l2 = ents.AddFixedLine(sv.Line2Start, sv.Line2End);
                    var arc = ents.AddFreeCurve(l1.EntityId, l2.EntityId, r0,
                        CurveParamType.Radius, false, CurveType.Compound);
                    if (fw.Length <= 0 || Math.Abs(arc.Radius - r0) > 0.01)
                    { report.Add(cname + $": free-curve solve abnormal len={fw.Length:0.00} R={arc.Radius:0.###}"); failed++; continue; }

                    fw.XData = new ResultBuffer(
                        new TypedValue((int)DxfCode.ExtendedDataRegAppName, "C3DF_FILLET"),
                        new TypedValue((int)DxfCode.ExtendedDataHandle, A.Handle),
                        new TypedValue((int)DxfCode.ExtendedDataReal, sv.StationA >= 0 ? sv.StationA : 0),
                        new TypedValue((int)DxfCode.ExtendedDataHandle, B.Handle),
                        new TypedValue((int)DxfCode.ExtendedDataReal, sv.StationB >= 0 ? sv.StationB : 0),
                        new TypedValue((int)DxfCode.ExtendedDataReal, r0));

                    var Aw = (CivAlignment)tr.GetObject(A.ObjectId, OpenMode.ForWrite);
                    var Bw = (CivAlignment)tr.GetObject(B.ObjectId, OpenMode.ForWrite);
                    string snapA = FilletSolveCore.SnapOffsetEnd(tr, Aw, sv.Line1Start, sv.Corner);
                    string snapB = FilletSolveCore.SnapOffsetEnd(tr, Bw, sv.Line2End, sv.Corner);

                    // Reconciliation: for offset segments measure the point distance "segment end to outer leg end" (gaps and overlaps both show);
                    // for non-offset parents (boundary loops, T-contact, not trimmed) measure the perpendicular distance from the outer leg end to the line.
                    double EndGap(CivAlignment seg, Point3d legOuter)
                    {
                        bool isOffset;
                        try { isOffset = seg.OffsetAlignmentInfo != null; } catch { isOffset = false; }
                        if (!isOffset) return DistTo(seg, legOuter);
                        double d1 = (PtAt(seg, seg.StartingStation) - legOuter).Length;
                        double d2 = (PtAt(seg, seg.EndingStation) - legOuter).Length;
                        return Math.Min(d1, d2);
                    }
                    double gapA = EndGap(Aw, sv.Line1Start), gapB = EndGap(Bw, sv.Line2End);
                    double g = Math.Max(gapA, gapB);
                    if (g > worstGap) { worstGap = g; worstAt = cname; }
                    done++;
                    report.Add($"{cname}{(corner == null ? "(new)" : "")} R{r0:0.#} angle{sv.CornerAngleDeg:0.0}deg "
                        + $"gapA={gapA * 1000:0.0}mm gapB={gapB * 1000:0.0}mm | {snapA} | {snapB}");
                }
                tr.Commit();
            }
            return new JsonObject
            {
                ["corners_input"] = arr.Count,
                ["rebuilt"] = done,
                ["created"] = created,
                ["failed"] = failed,
                ["worst_gap_mm"] = Math.Round(worstGap * 1000, 2),
                ["worst_gap_at"] = worstAt,
                ["details"] = report
            };
        }
    }
}
