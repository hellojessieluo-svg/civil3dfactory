using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeTrimOffsetsToCorners(JsonObject args, Document doc)
            => TrimOffsetsToCorners(args, doc);

        // Terrace corner trim: when a connected alignment is created the parent segments must extend past the corner to intersect (leaving small tails);
        // this part shrinks the region ends of the offset segments on both sides of each corner **in place** to margin beyond the arc tangent point
        // (AlignmentRegion.Start/EndStation are writable; no delete/rebuild, the corner stays alive).
        // Station frame: region ends use the parent centerline frame (StationOffset measured against the centerline), avoiding drift of the offset's own arc-length frame.
        static JsonNode TrimOffsetsToCorners(JsonObject a, Document doc)
        {
            var arr = a["corners"] as JsonArray;
            if (arr == null || arr.Count == 0)
                throw new InvalidOperationException("Requires corners:[{corner,in_alignment,out_alignment},...]");
            double margin = GetDouble(a, "margin", 0.05);

            Database db = doc.Database;
            var civ = Civ(db);
            var report = new JsonArray();
            int trimmed = 0, skipped = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (JsonNode n in arr)
                {
                    var o = (JsonObject)n;
                    string cname = Need(o, "corner");
                    var corner = FindAlignment(tr, civ, cname);
                    var segIn = FindAlignment(tr, civ, Need(o, "in_alignment"));
                    var segOut = FindAlignment(tr, civ, Need(o, "out_alignment"));
                    if (corner == null || segIn == null || segOut == null)
                    {
                        report.Add(cname + ": object missing, skipped");
                        skipped++;
                        continue;
                    }

                    foreach (double sta in new[] { corner.StartingStation, corner.EndingStation })
                    {
                        double e = 0, nn = 0;
                        corner.PointLocation(sta, 0, ref e, ref nn);
                        // Which segment owns this end: first measure offset ~= 0 against the segment; if unmeasurable (tangent point outside the segment range) fall back to
                        // measuring against the segment's parent -- |offset - nominal| ~= 0 also counts as a hit (region ends store parent-frame stations anyway).
                        CivAlignment seg = null;
                        foreach (var cand in new[] { segIn, segOut })
                        {
                            double s2 = 0, off2 = 0;
                            try { cand.StationOffset(e, nn, ref s2, ref off2); } catch { continue; }
                            if (Math.Abs(off2) < 0.05) { seg = cand; break; }
                        }
                        if (seg == null)
                        {
                            foreach (var cand in new[] { segIn, segOut })
                            {
                                Autodesk.Civil.DatabaseServices.OffsetAlignmentInfo oi;
                                try { oi = cand.OffsetAlignmentInfo; } catch { continue; }
                                if (oi == null) continue;
                                CivAlignment par;
                                try { par = (CivAlignment)tr.GetObject(oi.ParentAlignmentId, OpenMode.ForRead); }
                                catch { continue; }
                                double s3 = 0, off3 = 0;
                                try { par.StationOffset(e, nn, ref s3, ref off3); } catch { continue; }
                                if (Math.Abs(Math.Abs(off3) - Math.Abs(oi.NominalOffset)) < 0.1) { seg = cand; break; }
                            }
                        }
                        if (seg == null) { report.Add(cname + ": an end point lies on no segment"); continue; }

                        Autodesk.Civil.DatabaseServices.OffsetAlignmentInfo info;
                        try { info = seg.OffsetAlignmentInfo; }
                        catch { info = null; }   // non-offset alignments (e.g. boundary centerline) throw on this property instead of returning null
                        if (info == null) { report.Add(seg.Name + ": not an offset alignment, end skipped"); continue; }
                        var parent = (CivAlignment)tr.GetObject(info.ParentAlignmentId, OpenMode.ForRead);
                        double psta = 0, poff = 0;
                        try { parent.StationOffset(e, nn, ref psta, ref poff); }
                        catch { report.Add(seg.Name + ": tangent point outside the parent range"); continue; }

                        var segW = (CivAlignment)tr.GetObject(seg.ObjectId, OpenMode.ForWrite);
                        var regions = segW.OffsetAlignmentInfo.Regions;
                        // Find the region covering / nearest to this station
                        int best = -1;
                        double bestD = double.MaxValue;
                        for (int i = 0; i < regions.Count; i++)
                        {
                            var r = regions[i];
                            double d = (psta >= r.StartStation && psta <= r.EndStation) ? 0
                                : Math.Min(Math.Abs(psta - r.StartStation), Math.Abs(psta - r.EndStation));
                            if (d < bestD) { bestD = d; best = i; }
                        }
                        if (best < 0) { report.Add(seg.Name + ": no region"); continue; }
                        var reg = regions[best];
                        bool nearStart = Math.Abs(psta - reg.StartStation) < Math.Abs(psta - reg.EndStation);
                        double before, after;
                        if (nearStart)
                        {
                            before = reg.StartStation;
                            after = psta - margin;
                            if (after > reg.EndStation - 1) { report.Add(seg.Name + ": trim would consume the region, skipped"); continue; }
                            reg.StartStation = after;
                        }
                        else
                        {
                            before = reg.EndStation;
                            after = psta + margin;
                            if (after < reg.StartStation + 1) { report.Add(seg.Name + ": trim would consume the region, skipped"); continue; }
                            reg.EndStation = after;
                        }
                        trimmed++;
                        report.Add(seg.Name + (nearStart ? " start " : " end ")
                            + Math.Round(before, 2) + " -> " + Math.Round(after, 2));
                    }
                }
                tr.Commit();
            }
            return new JsonObject
            {
                ["corners_input"] = arr.Count,
                ["ends_trimmed"] = trimmed,
                ["skipped"] = skipped,
                ["details"] = report
            };
        }
    }
}
