#nullable disable   // This file is compiled by both Civil3DFactory (nullable off) and WaterBox (nullable on); treat as off

using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory.Geometry
{
    /// <summary>
    /// Detection and repair core for self-intersecting polylines (pure geometry/DB, no interaction dependencies).
    ///
    /// Self-intersecting lines are the number-one downstream killer: TIN surface building, GetOffsetCurves offsets, area
    /// computation and hatching all fail or silently give wrong numbers. The fix is loop removal -- cut the line at the
    /// intersection of the two segments, drop the smaller loop and keep the larger one:
    ///   closed line: both candidates are loops, compare AREA (with segment correction for arcs), keep the larger one;
    ///   open line: lasso-shaped loops are always dropped, the trunk is kept.
    /// If the dropped share exceeds max_drop_ratio the line is left UNTOUCHED and reported as needing manual confirmation --
    /// figure-eights and grossly misdrawn shapes are not small burrs; deciding for the user would destroy the design intent.
    ///
    /// Single source of truth: Civil3DFactory (node fix_self_intersections) and products\waterbox
    /// (C3DF-FixSelfIntersect/ZJ) both compile this file.
    /// </summary>
    public static class SelfIntersectionCore
    {
        const double Tol = 1e-9;
        const double DupTol = 1e-6;      // vertex coincidence tolerance
        const int MaxPasses = 200;       // loops are removed one by one; guard against pathological shapes looping forever

        /// <summary>One self-intersection: segment SegA and segment SegB cross at Point.</summary>
        public sealed class Hit
        {
            public int SegA;
            public int SegB;
            public Point2d Point;
        }

        public sealed class RepairReport
        {
            public int VerticesBefore;
            public int VerticesAfter;
            public double AreaBefore;        // meaningful only for closed lines (absolute value of signed area)
            public double AreaAfter;
            public double LengthBefore;
            public double LengthAfter;
            public int DuplicateVerticesRemoved;
            public int HitsFound;            // intersections detected before repair
            public int LoopsRemoved;
            public List<double> RemovedLoopAreas = new List<double>();
            public List<double> RemovedLoopLengths = new List<double>();
            public List<Point2d> Intersections = new List<Point2d>();   // intersection positions before repair
            public bool Changed;             // geometry actually changed
            public bool Clean;               // no self-intersection left at the end
            public bool NeedsManual;         // blocked by max_drop_ratio, unchanged
            public string ManualReason;
        }

        // ============================================================
        //  Detection: intersect every pair of segments; for adjacent segments (including first/last of a closed line) only the
        //  intersection at the shared vertex is excluded, not the whole pair -- adjacent arcs can truly cross at a second point,
        //  and adjacent straight segments can fold back and overlap entirely; Civil refuses both as surface boundaries,
        //  and versions before 2026-09-01 skipped whole pairs and missed them all.
        //  Line x line is computed here (fast and exact; collinear overlap checked when parallel),
        //  anything involving arcs goes through CurveCurveIntersector2d.
        // ============================================================
        public static List<Hit> Detect(Polyline pl)
        {
            int dup;
            return Detect(pl, out dup);
        }

        /// <summary>Detect + report the number of duplicate vertices (zero-length segments). Duplicate vertices produce no Hit,
        /// but Civil refuses them as boundaries just the same; callers must treat duplicateVertices>0 as dirty.</summary>
        public static List<Hit> Detect(Polyline pl, out int duplicateVertices)
        {
            var pts = new List<Point2d>();
            var bulges = new List<double>();
            bool closed = pl.Closed;
            duplicateVertices = Load(pl, pts, bulges, ref closed);
            return Detect(pts, bulges, closed);
        }

        static List<Hit> Detect(List<Point2d> pts, List<double> bulges, bool closed)
        {
            var hits = new List<Hit>();
            int n = pts.Count;
            int segCount = closed ? n : n - 1;
            if (segCount < 2) return hits;

            for (int i = 0; i < segCount; i++)
            {
                for (int j = i + 1; j < segCount; j++)
                {
                    // Adjacent segments share one vertex: crossing at the shared point is not a self-intersection, every other crossing is
                    bool adjacent = (j == i + 1) || (closed && i == 0 && j == segCount - 1);
                    Point2d shared = default;
                    if (adjacent)
                        shared = (j == i + 1) ? pts[(i + 1) % n] : pts[0];

                    foreach (Point2d p in SegmentIntersections(pts, bulges, n, i, j))
                    {
                        if (adjacent && p.GetDistanceTo(shared) < DupTol) continue;
                        hits.Add(new Hit { SegA = i, SegB = j, Point = p });
                    }
                }
            }
            return hits;
        }

        static IEnumerable<Point2d> SegmentIntersections(
            List<Point2d> pts, List<double> bulges, int n, int i, int j)
        {
            Point2d a1 = pts[i], a2 = pts[(i + 1) % n];
            Point2d b1 = pts[j], b2 = pts[(j + 1) % n];
            double ba = bulges[i], bb = bulges[j];
            var found = new List<Point2d>();

            if (Math.Abs(ba) < Tol && Math.Abs(bb) < Tol)
            {
                if (LineLine(a1, a2, b1, b2, out Point2d p)) found.Add(p);
                else if (CollinearOverlap(a1, a2, b1, b2, out Point2d q2)) found.Add(q2);
                return found;
            }

            // With arcs: use AutoCAD's curve intersector; on failure fall back to chord approximation (better to miss than to misjudge)
            try
            {
                Curve2d c1 = MakeCurve(a1, a2, ba);
                Curve2d c2 = MakeCurve(b1, b2, bb);
                if (c1 != null && c2 != null)
                {
                    var x = new CurveCurveIntersector2d(c1, c2, Tolerance.Global);
                    for (int k = 0; k < x.NumberOfIntersectionPoints; k++)
                        found.Add(x.GetIntersectionPoint(k));
                    return found;
                }
            }
            catch { }

            if (LineLine(a1, a2, b1, b2, out Point2d q)) found.Add(q);
            return found;
        }

        static Curve2d MakeCurve(Point2d a, Point2d b, double bulge)
        {
            if (a.GetDistanceTo(b) < DupTol) return null;
            if (Math.Abs(bulge) < Tol) return new LineSegment2d(a, b);
            Center(a, b, bulge, out Point2d o, out double r);
            double sa = (a - o).Angle, sb = (b - o).Angle;
            // CircularArc2d runs counter-clockwise from start to end; swap the endpoints for clockwise arcs
            return bulge > 0 ? new CircularArc2d(o, r, sa, sb, Vector2d.XAxis, false)
                             : new CircularArc2d(o, r, sb, sa, Vector2d.XAxis, false);
        }

        /// <summary>Segment intersection (endpoints included, collinear overlap excluded -- that is handed to the duplicate-vertex step).</summary>
        static bool LineLine(Point2d p1, Point2d p2, Point2d p3, Point2d p4, out Point2d hit)
        {
            hit = default;
            Vector2d r = p2 - p1, s = p4 - p3;
            double den = r.X * s.Y - r.Y * s.X;
            if (Math.Abs(den) < 1e-12) return false;      // parallel or collinear
            Vector2d w = p3 - p1;
            double t = (w.X * s.Y - w.Y * s.X) / den;
            double u = (w.X * r.Y - w.Y * r.X) / den;
            if (t < -1e-9 || t > 1 + 1e-9 || u < -1e-9 || u > 1 + 1e-9) return false;
            hit = p1 + r * t;
            return true;
        }

        /// <summary>Overlap test for collinear (or nearly parallel) segments: LineLine always returns false for parallel/collinear,
        /// yet full-segment overlaps such as a fold-back along an edge or a burr are exactly what Civil's boundary errors complain about.
        /// Criterion: both endpoints lie within tolerance of the other segment's line, and the parameter intervals overlap by more than the tolerance.
        /// A hit reports the midpoint of the overlap, handed to the loop remover as an ordinary intersection (loop area ~ 0, always dropped).</summary>
        static bool CollinearOverlap(Point2d a1, Point2d a2, Point2d b1, Point2d b2, out Point2d hit)
        {
            hit = default;
            Vector2d r = a2 - a1;
            double len = r.Length;
            if (len < DupTol) return false;
            double d1 = Math.Abs((b1 - a1).X * r.Y - (b1 - a1).Y * r.X) / len;
            double d2 = Math.Abs((b2 - a1).X * r.Y - (b2 - a1).Y * r.X) / len;
            if (d1 > DupTol || d2 > DupTol) return false;         // parallel but not collinear
            double len2 = len * len;
            double t3 = ((b1 - a1).X * r.X + (b1 - a1).Y * r.Y) / len2;
            double t4 = ((b2 - a1).X * r.X + (b2 - a1).Y * r.Y) / len2;
            double lo = Math.Max(0, Math.Min(t3, t4));
            double hi = Math.Min(1, Math.Max(t3, t4));
            if ((hi - lo) * len < DupTol) return false;           // touching only at an endpoint is not an overlap
            // Prefer the endpoint that lies inside the other segment as the intersection -- the repair cut then lands exactly on the
            // fold-back vertex and converges in one pass; fall back to the overlap midpoint only when the two segments cover each other fully.
            double margin = DupTol / len;
            if (t3 > margin && t3 < 1 - margin) hit = b1;
            else if (t4 > margin && t4 < 1 - margin) hit = b2;
            else hit = a1 + r * ((lo + hi) / 2);
            return true;
        }

        // ============================================================
        //  Repair: remove loops one at a time until no self-intersection remains
        //  Returns a new Polyline (caller appends it and erases the old one); returns null when already clean or when not daring to change.
        // ============================================================
        public static Polyline Repair(Polyline src, double maxDropRatio, out RepairReport rep)
        {
            rep = new RepairReport();
            var pts = new List<Point2d>();
            var bulges = new List<double>();
            bool closed = src.Closed;

            rep.VerticesBefore = src.NumberOfVertices;
            int dupRemoved = Load(src, pts, bulges, ref closed);
            rep.DuplicateVerticesRemoved = dupRemoved;
            rep.AreaBefore = Math.Abs(SignedArea(pts, bulges, closed));
            rep.LengthBefore = TotalLength(pts, bulges, closed);

            List<Hit> hits = Detect(pts, bulges, closed);
            rep.HitsFound = hits.Count;
            foreach (Hit h in hits) rep.Intersections.Add(h.Point);

            if (hits.Count == 0)
            {
                rep.Clean = true;
                if (dupRemoved == 0) return null;        // nothing to do at all
            }

            // Upper bound on the dropped amount: by area for closed, by length for open
            double budget = (closed ? rep.AreaBefore : rep.LengthBefore) * maxDropRatio;
            double dropped = 0;

            for (int pass = 0; pass < MaxPasses; pass++)
            {
                List<Hit> cur = Detect(pts, bulges, closed);
                if (cur.Count == 0) { rep.Clean = true; break; }

                Hit h = cur[0];
                int i = h.SegA, j = h.SegB, n = pts.Count;
                Point2d P = h.Point;

                SplitBulge(pts[i], pts[(i + 1) % n], bulges[i], P, out double bi1, out double bi2);
                SplitBulge(pts[j], pts[(j + 1) % n], bulges[j], P, out double bj1, out double bj2);

                // Candidate A: drop the inner loop -- v0..vi, P, vj+1..
                var ptsA = new List<Point2d>();
                var bulA = new List<double>();
                for (int k = 0; k <= i; k++) { ptsA.Add(pts[k]); bulA.Add(k == i ? bi1 : bulges[k]); }
                ptsA.Add(P); bulA.Add(bj2);
                for (int k = j + 1; k < n; k++) { ptsA.Add(pts[k]); bulA.Add(bulges[k]); }

                // Candidate B: the loop itself -- P, vi+1..vj, back to P (always closed)
                var ptsB = new List<Point2d>();
                var bulB = new List<double>();
                ptsB.Add(P); bulB.Add(bi2);
                for (int k = i + 1; k <= j; k++) { ptsB.Add(pts[k]); bulB.Add(k == j ? bj1 : bulges[k]); }

                bool keepA;
                double dropAmount;
                if (closed)
                {
                    double areaA = Math.Abs(SignedArea(ptsA, bulA, true));
                    double areaB = Math.Abs(SignedArea(ptsB, bulB, true));
                    keepA = areaA >= areaB;
                    dropAmount = keepA ? areaB : areaA;
                }
                else
                {
                    keepA = true;                                   // a loop on an open line is a lasso, always dropped
                    dropAmount = Math.Abs(SignedArea(ptsB, bulB, true));
                    if (dropAmount < Tol) dropAmount = TotalLength(ptsB, bulB, true);
                }

                if (dropped + dropAmount > budget)
                {
                    rep.NeedsManual = true;
                    rep.ManualReason = "Loop " + (rep.LoopsRemoved + 1) + " would drop "
                        + dropAmount.ToString("0.###") + (closed ? " m²" : "")
                        + ", exceeding the allowed ratio (limit " + budget.ToString("0.###")
                        + "); stopped without changes -- please confirm manually whether it is a loop or design intent.";
                    rep.Changed = false;
                    return null;
                }

                dropped += dropAmount;
                rep.LoopsRemoved++;
                if (closed) rep.RemovedLoopAreas.Add(Math.Round(dropAmount, 4));
                else rep.RemovedLoopLengths.Add(Math.Round(TotalLength(ptsB, bulB, true), 4));

                if (keepA) { pts = ptsA; bulges = bulA; }
                else { pts = ptsB; bulges = bulB; closed = true; }

                RemoveDuplicates(pts, bulges, closed);
                if (pts.Count < (closed ? 3 : 2))
                {
                    rep.NeedsManual = true;
                    rep.ManualReason = "Too few vertices left after removal; the line itself may be degenerate. Stopped without changes.";
                    rep.Changed = false;
                    return null;
                }
            }

            rep.VerticesAfter = pts.Count;
            rep.AreaAfter = Math.Abs(SignedArea(pts, bulges, closed));
            rep.LengthAfter = TotalLength(pts, bulges, closed);
            rep.Changed = true;

            var res = new Polyline(pts.Count);
            for (int k = 0; k < pts.Count; k++)
                res.AddVertexAt(k, pts[k], bulges[k], 0, 0);
            res.Closed = closed;
            res.SetPropertiesFrom(src);
            res.Normal = src.Normal;
            res.Elevation = src.Elevation;
            return res;
        }

        // ---- Read vertex table + normalise closure + remove duplicate points ----
        static int Load(Polyline pl, List<Point2d> pts, List<double> bulges, ref bool closed)
        {
            for (int k = 0; k < pl.NumberOfVertices; k++)
            {
                pts.Add(pl.GetPoint2dAt(k));
                bulges.Add(pl.GetBulgeAt(k));
            }
            // Snap-closed: Closed=false but first and last coincide; treat as closed (otherwise the closing segment escapes self-intersection detection)
            if (!closed && pts.Count >= 4 &&
                pts[0].GetDistanceTo(pts[pts.Count - 1]) < DupTol)
            {
                pts.RemoveAt(pts.Count - 1);
                bulges.RemoveAt(bulges.Count - 1);
                closed = true;
            }
            return RemoveDuplicates(pts, bulges, closed);
        }

        /// <summary>Adjacent coincident vertices create zero-length segments, all false positives in detection; clear them first.</summary>
        static int RemoveDuplicates(List<Point2d> pts, List<double> bulges, bool closed)
        {
            int removed = 0;
            for (int k = pts.Count - 1; k > 0; k--)
            {
                if (pts[k].GetDistanceTo(pts[k - 1]) >= DupTol) continue;
                if (Math.Abs(bulges[k - 1]) > Tol) continue;      // full-circle arc segment, keep it
                pts.RemoveAt(k);
                bulges.RemoveAt(k);
                removed++;
            }
            if (closed && pts.Count >= 2 &&
                pts[0].GetDistanceTo(pts[pts.Count - 1]) < DupTol &&
                Math.Abs(bulges[pts.Count - 1]) < Tol)
            {
                pts.RemoveAt(pts.Count - 1);
                bulges.RemoveAt(bulges.Count - 1);
                removed++;
            }
            return removed;
        }

        // ---- Area (with circular-segment correction for arcs): counter-clockwise positive ----
        static double SignedArea(List<Point2d> pts, List<double> bulges, bool closed)
        {
            int n = pts.Count;
            // A closed 2-vertex line with arcs is a legal lens / circular segment whose area lies entirely in the arc correction; zeroing
            // it for n<3 would record the drop amount of such a loop as 0 and pierce the max_drop_ratio guard (observed 2026-09-01)
            if (n < 2 || (!closed && n < 3)) return 0;
            double a = 0;
            for (int k = 0; k < n; k++)
            {
                Point2d p = pts[k], q = pts[(k + 1) % n];
                if (!closed && k == n - 1) break;
                a += p.X * q.Y - q.X * p.Y;
            }
            a /= 2;

            int segCount = closed ? n : n - 1;
            for (int k = 0; k < segCount; k++)
            {
                double b = bulges[k];
                if (Math.Abs(b) < Tol) continue;
                Point2d p = pts[k], q = pts[(k + 1) % n];
                double c = p.GetDistanceTo(q);
                if (c < Tol) continue;
                double theta = 4 * Math.Atan(Math.Abs(b));
                double r = c * (1 + b * b) / (4 * Math.Abs(b));
                double seg = r * r / 2 * (theta - Math.Sin(theta));   // circular segment area
                a += Math.Sign(b) * seg;
            }
            return a;
        }

        static double TotalLength(List<Point2d> pts, List<double> bulges, bool closed)
        {
            int n = pts.Count;
            if (n < 2) return 0;
            int segCount = closed ? n : n - 1;
            double len = 0;
            for (int k = 0; k < segCount; k++)
            {
                Point2d p = pts[k], q = pts[(k + 1) % n];
                double c = p.GetDistanceTo(q);
                double b = bulges[k];
                if (Math.Abs(b) < Tol) { len += c; continue; }
                double theta = 4 * Math.Atan(Math.Abs(b));
                double r = c * (1 + b * b) / (4 * Math.Abs(b));
                len += r * theta;
            }
            return len;
        }

        // ---- The two bulges of an arc segment split at P (center formula matches OffsetConeCore.BuildSeg) ----
        static void SplitBulge(Point2d a, Point2d b, double bulge, Point2d p,
            out double b1, out double b2)
        {
            b1 = 0; b2 = 0;
            if (Math.Abs(bulge) < Tol) return;
            Center(a, b, bulge, out Point2d o, out double r);
            int w = Math.Sign(bulge);
            double total = Math.Abs(4 * Math.Atan(bulge));
            double s1 = SweepBetween(a - o, p - o, w);
            if (s1 > total) s1 = total;
            b1 = w * Math.Tan(s1 / 4);
            b2 = w * Math.Tan((total - s1) / 4);
        }

        static void Center(Point2d a, Point2d b, double bulge, out Point2d o, out double r)
        {
            Vector2d c = b - a;
            double d = c.Length * (1 + bulge * bulge) / (4 * bulge);   // signed
            o = a + c.GetNormal().RotateBy(Math.PI / 2 - 2 * Math.Atan(bulge)) * d;
            r = Math.Abs(d);
        }

        static double SweepBetween(Vector2d from, Vector2d to, int dir)
        {
            double a = (Math.Atan2(to.Y, to.X) - Math.Atan2(from.Y, from.X)) * dir;
            while (a < 0) a += 2 * Math.PI;
            while (a >= 2 * Math.PI) a -= 2 * Math.PI;
            return a;
        }
    }
}
