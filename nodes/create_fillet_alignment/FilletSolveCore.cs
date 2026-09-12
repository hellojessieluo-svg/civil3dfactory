#nullable disable
using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace Civil3DFactory.Geometry
{
    /// <summary>
    /// Corner fillet solver core (single source of truth): one pick point on each of two alignments -> full geometry of fixed legs + tangent arc.
    /// The pick points do two things: fix the connection side (removes quadrant ambiguity, the lesson of 2026-08-23) and fix the leg direction.
    /// Pure closed-form geometry; does not use the native connected-alignment solver.
    /// </summary>
    public static class FilletSolveCore
    {
        public class Result
        {
            public Point3d Line1Start, Line1End;   // leg 1: outer end -> tangent point T1 (in travel direction)
            public Point3d Line2Start, Line2End;   // leg 2: tangent point T2 -> outer end
            public double StationA, StationB;      // stations of the two tangent points on their parent alignments (-1 if unavailable)
            public bool CornerBeyondA, CornerBeyondB; // is the corner on the increasing-station side of the tangent point? (used to pick the end when trimming)
            public Point3d Corner;                 // intersection of the two tangents (sharp corner)
            public double ArcLength, CornerAngleDeg;
            public string Error;                   // non-empty = failure reason
        }

        /// <param name="mirrorB">Mirror: reverse the B-side leg at the corner (arc solved in the supplementary quadrant, same-size tangent arc on the other side).
        /// Not the "large arc through the same tangent points": that is a balloon loop, rejected on real drawings late on 2026-08-23.</param>
        public static Result Solve(CivAlignment a, Point3d pickA,
                                   CivAlignment b, Point3d pickB,
                                   double radius, double leg, bool mirrorB = false)
        {
            var r = new Result();
            try
            {
                // The pick point's foot may fall outside the alignment station range (StationOffset throws
                // PointNotOnEntity beyond the ends): snap the point onto the line first, else coarse scan then refine.
                double StationOf(CivAlignment al, Point3d pick)
                {
                    try
                    {
                        var cp = al.GetClosestPointTo(pick, false);
                        double s = 0, o = 0;
                        al.StationOffset(cp.X, cp.Y, ref s, ref o);
                        return s;
                    }
                    catch
                    {
                        double best = al.StartingStation, bd = double.MaxValue;
                        double len = al.EndingStation - al.StartingStation;
                        for (int i = 0; i <= 400; i++)
                        {
                            double s = al.StartingStation + len * i / 400.0;
                            double e = 0, n = 0;
                            al.PointLocation(s, 0, ref e, ref n);
                            double d = (new Point3d(e, n, 0) - pick).Length;
                            if (d < bd) { bd = d; best = s; }
                        }
                        return best;
                    }
                }
                double sa = StationOf(a, pickA);
                double sb = StationOf(b, pickB);

                Point3d Pt(CivAlignment al, double s)
                {
                    double e = 0, n = 0;
                    al.PointLocation(s, 0, ref e, ref n);
                    return new Point3d(e, n, 0);
                }
                Vector3d Dir(CivAlignment al, double s)
                {
                    double s0 = Math.Max(al.StartingStation, s - 0.05);
                    double s1 = Math.Min(al.EndingStation, s + 0.05);
                    var v = Pt(al, s1) - Pt(al, s0);
                    if (v.Length < 1e-9) { r.Error = "Orientation failed: pick point too close to the alignment end."; return default; }
                    return v / v.Length;
                }

                Point3d P1 = Pt(a, sa), P2 = Pt(b, sb);
                Vector3d u1 = Dir(a, sa), u2 = Dir(b, sb);
                if (r.Error != null) return r;

                // Intersect the two local lines (the neighbourhood of a pick point is treated as straight, the norm at corners)
                double det = u1.X * (-u2.Y) - u1.Y * (-u2.X);
                if (Math.Abs(det) < 1e-9) { r.Error = "The two alignments are nearly parallel at the pick points; cannot fillet."; return r; }
                double dx = P2.X - P1.X, dy = P2.Y - P1.Y;
                double t1 = (dx * (-u2.Y) - dy * (-u2.X)) / det;
                Point3d C = new Point3d(P1.X + t1 * u1.X, P1.Y + t1 * u1.Y, 0);

                Vector3d d1 = P1 - C, d2 = P2 - C;   // corner -> pick point = leg direction
                if (d1.Length < 0.5 || d2.Length < 0.5)
                { r.Error = "Pick point too close to the intersection (<0.5 m); pick further from the corner."; return r; }
                d1 /= d1.Length; d2 /= d2.Length;
                if (mirrorB) d2 = -d2;   // mirror: reverse the B-side leg, arc lands on the other side (supplementary quadrant)

                double cos = Math.Max(-1, Math.Min(1, d1.DotProduct(d2)));
                double theta = Math.Acos(cos);
                r.CornerAngleDeg = theta * 180 / Math.PI;
                if (theta < 0.09 || theta > Math.PI - 0.09)
                { r.Error = $"Degenerate angle ({r.CornerAngleDeg:0.0} deg); cannot fillet."; return r; }

                double t = radius / Math.Tan(theta / 2);
                r.Corner = C;
                Point3d T1 = C + d1 * t, T2 = C + d2 * t;
                r.Line1Start = T1 + d1 * leg; r.Line1End = T1;
                r.Line2Start = T2; r.Line2End = T2 + d2 * leg;
                r.ArcLength = radius * (Math.PI - theta);

                double sta = 0, off = 0;
                try { a.StationOffset(T1.X, T1.Y, ref sta, ref off); r.StationA = sta; }
                catch { r.StationA = -1; }   // tangent point beyond the parent alignment end (leg sticks out)
                try { b.StationOffset(T2.X, T2.Y, ref sta, ref off); r.StationB = sta; }
                catch { r.StationB = -1; }
                // Which side of the tangent point the corner is on: dot product of the increasing-station direction at the tangent point and tangent point -> corner (geometric, unambiguous)
                if (r.StationA >= 0)
                    r.CornerBeyondA = Dir(a, r.StationA).DotProduct(C - T1) > 0;
                if (r.StationB >= 0)
                    r.CornerBeyondB = Dir(b, r.StationB).DotProduct(C - T2) > 0;
            }
            catch (System.Exception ex)
            {
                r.Error = "Solve error: " + ex.Message;
            }
            return r;
        }

        /// <summary>
        /// Trim (point-to-point contract, fixed 2026-08-23): the corner-side region end of the offset segment lands **exactly** on the
        /// outer leg end of the corner alignment (Result.Line1Start / Line2End); segment end == corner start, zero overlap, zero gap.
        /// Pass coordinates, not stations: an offset alignment's own stations differ from the parent frame by metres on curves (an old demon),
        /// so internally everything is converted to the parent frame via the parent's StationOffset before the region end is changed. seg must already be ForWrite.
        /// Returns a report string; leaves the region alone and says so for non-offset alignments or when trimming would consume the region.
        /// </summary>
        public static string SnapOffsetEnd(Transaction tr, CivAlignment seg,
                                           Point3d legOuterPt, Point3d cornerPt)
        {
            Autodesk.Civil.DatabaseServices.OffsetAlignmentInfo info;
            try { info = seg.OffsetAlignmentInfo; } catch { info = null; }
            if (info == null) return seg.Name + ": not an offset alignment, end not trimmed";

            CivAlignment parent;
            try { parent = (CivAlignment)tr.GetObject(info.ParentAlignmentId, OpenMode.ForRead); }
            catch { return seg.Name + ": parent alignment unavailable, end not trimmed"; }

            double psta = 0, poff = 0;
            try { parent.StationOffset(legOuterPt.X, legOuterPt.Y, ref psta, ref poff); }
            catch { return seg.Name + ": outer leg end beyond parent range, end not trimmed"; }

            // End selection reference = corner station (not the outer leg end): after mirroring the outer leg end moves to the other side of the corner,
            // so the "which side of the tangent point" dot product would pick the wrong end; the region end nearest the corner is the one to move.
            double pstaRef = psta;
            try
            {
                double pc = 0, po = 0;
                parent.StationOffset(cornerPt.X, cornerPt.Y, ref pc, ref po);
                pstaRef = pc;
            }
            catch { }   // corner beyond parent range: fall back to the outer leg end as reference

            var regions = info.Regions;
            int best = -1;
            double bd = double.MaxValue;
            for (int i = 0; i < regions.Count; i++)
            {
                var g = regions[i];
                double d = (pstaRef >= g.StartStation && pstaRef <= g.EndStation) ? 0
                    : Math.Min(Math.Abs(pstaRef - g.StartStation), Math.Abs(pstaRef - g.EndStation));
                if (d < bd) { bd = d; best = i; }
            }
            if (best < 0) return seg.Name + ": no region, end not trimmed";
            var reg = regions[best];
            bool moveStart = Math.Abs(pstaRef - reg.StartStation) <= Math.Abs(pstaRef - reg.EndStation);
            if (moveStart)
            {
                if (psta > reg.EndStation - 1) return seg.Name + ": trimming would consume the region, not trimmed";
                double old = reg.StartStation;
                reg.StartStation = psta;
                return $"{seg.Name} start {old:0.00}->{psta:0.00}";
            }
            else
            {
                if (psta < reg.StartStation + 1) return seg.Name + ": trimming would consume the region, not trimmed";
                double old = reg.EndStation;
                reg.EndStation = psta;
                return $"{seg.Name} end {old:0.00}->{psta:0.00}";
            }
        }
    }
}
