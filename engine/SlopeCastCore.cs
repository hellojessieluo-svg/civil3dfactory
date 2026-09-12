#nullable disable   // This file is link-compiled into WaterBox (nullable on); shared when Civil3DFactory nodes adopt it, so keep nullable off here

using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory.Geometry
{
    /// <summary>
    /// Slope-casting core (pure geometry, no DB or interaction): along a chain of elevated sample points,
    /// casts a 1:n slope to the target elevation z1 on the given side, producing toe-line vertices + alternating long/short slope hatch lines.
    /// The offset varies per point (= n*|z-z1|); vertices follow the angle bisector with a miter factor; the toe line gets
    /// self-intersection loop removal and collinear weeding. Single source of truth: the C3DF-SlopeToElev command in products\waterbox
    /// link-compiles this file; future Civil3DFactory nodes link it too, so algorithm changes go here only.
    /// </summary>
    public static class SlopeCastCore
    {
        const double ZeroDz = 1e-4;        // height differences below this count as zero-width points (line exactly at target elevation)
        const double MinCombWidth = 0.05;  // no slope hatch line when the slope width is below this
        const double WeedTol = 0.01;       // collinear weeding tolerance for the toe line
        const int MaxLoops = 200;          // max self-intersection loop removals (guards against infinite loops on pathological input)
        const int MaxCombs = 20000;        // max number of slope hatch lines (guards against a mistakenly tiny spacing)

        /// <summary>One slope hatch line: Top is always on the high side, End points to the low side (the long stroke is drawn from high to low).</summary>
        public sealed class Comb
        {
            public Point3d Top;
            public Point3d End;
            public bool Full;   // true = long line (to the toe), false = short line (half width)
        }

        public sealed class CastResult
        {
            public List<Point3d> Toe = new List<Point3d>();   // toe-line vertices, Z all at the target elevation
            public List<Comb> Combs = new List<Comb>();
            public int LoopsRemoved;                          // number of self-intersection loops removed
            public int CombsSkipped;                          // hatch lines skipped because they fell in a cleaned self-intersection zone
            public double MinWidth = double.MaxValue;         // horizontal width range of the slope band
            public double MaxWidth;
            public int ZeroPoints;                            // zero-width sample points (where the line crosses the target elevation)
            public double CutLength;                          // length along the line above the target elevation (cut)
            public double FillLength;                         // length along the line below the target elevation (fill)
            public bool AllZero;                              // whole line at the target elevation, no slope to cast
            public bool TooShort;                             // fewer than 2 valid samples
            public int Direction;                             // +1 = slope down (source line above target); -1 = slope up
            public bool Mixed;                                // this segment has samples both above and below the target
        }

        /// <summary>
        /// samples: ordered sample points along the line (with elevation; must already be dense enough, vertices included).
        /// closed: whether it is a closed ring (first and last point may or may not coincide).
        /// side: +1 = cast to the left of the direction of travel, -1 = right.
        /// slopeN: the n of slope 1:n. z1: target elevation. combSpacing: spacing of hatch lines along the line.
        /// </summary>
        public static CastResult Cast(IList<Point3d> samples, bool closed, int side,
            double slopeN, double z1, double combSpacing)
        {
            var res = new CastResult();

            // ---- 0. De-duplicate in plan projection ----
            var pts = new List<Point3d>(samples.Count);
            foreach (var p in samples)
                if (pts.Count == 0 || Dist2d(pts[pts.Count - 1], p) > 1e-6) pts.Add(p);
            if (closed && pts.Count > 1 && Dist2d(pts[0], pts[pts.Count - 1]) < 1e-6)
                pts.RemoveAt(pts.Count - 1);
            int n = pts.Count;
            if (n < 2) { res.TooShort = true; return res; }
            int segCount = closed ? n : n - 1;

            // ---- 1. Segment direction / length / chainage ----
            var segDir = new Point2d[segCount];   // unit direction vectors (stored in Point2d)
            var segLen = new double[segCount];
            var cumV = new double[segCount + 1];
            for (int k = 0; k < segCount; k++)
            {
                var a = pts[k]; var b = pts[(k + 1) % n];
                double vx = b.X - a.X, vy = b.Y - a.Y;
                segLen[k] = Math.Sqrt(vx * vx + vy * vy);
                segDir[k] = new Point2d(vx / segLen[k], vy / segLen[k]);
                cumV[k + 1] = cumV[k] + segLen[k];
            }
            double L = cumV[segCount];

            // ---- 2. Vertex offset direction (angle bisector) and miter factor ----
            var offX = new double[n]; var offY = new double[n]; var miter = new double[n];
            for (int i = 0; i < n; i++)
            {
                int kPrev = closed ? (i - 1 + segCount) % segCount : i - 1;
                int kNext = closed ? i : i;
                bool hasPrev = closed || i > 0;
                bool hasNext = closed || i < n - 1;
                double sc = 1.0, nx, ny;
                double pnx = 0, pny = 0, nnx = 0, nny = 0;
                if (hasPrev) { pnx = -segDir[kPrev].Y * side; pny = segDir[kPrev].X * side; }
                if (hasNext) { nnx = -segDir[kNext].Y * side; nny = segDir[kNext].X * side; }
                if (hasPrev && hasNext)
                {
                    double sx = pnx + nnx, sy = pny + nny;
                    double sl = Math.Sqrt(sx * sx + sy * sy);
                    if (sl < 1e-6) { nx = nnx; ny = nny; }   // 180-degree reversal: degrade to the next segment's normal
                    else
                    {
                        nx = sx / sl; ny = sy / sl;
                        double cosHalf = nx * nnx + ny * nny;
                        sc = Math.Min(1.0 / Math.Max(cosHalf, 0.5), 2.0);   // miter, capped at 2x
                    }
                }
                else if (hasNext) { nx = nnx; ny = nny; }
                else { nx = pnx; ny = pny; }
                offX[i] = nx; offY[i] = ny; miter[i] = sc;
            }

            // ---- 3. Per-point width and raw toe points ----
            var rawToe = new List<Point2d>(n);
            var orig = new List<int>(n);   // sample index of each raw point; -1 = point inserted by self-intersection cleanup
            for (int i = 0; i < n; i++)
            {
                double dz = pts[i].Z - z1;
                double w = slopeN * Math.Abs(dz);
                if (Math.Abs(dz) < ZeroDz) res.ZeroPoints++;
                if (w < res.MinWidth) res.MinWidth = w;
                if (w > res.MaxWidth) res.MaxWidth = w;
                rawToe.Add(new Point2d(pts[i].X + offX[i] * w * miter[i],
                                       pts[i].Y + offY[i] * w * miter[i]));
                orig.Add(i);
            }
            if (res.MaxWidth < MinCombWidth) { res.AllZero = true; return res; }

            for (int k = 0; k < segCount; k++)
            {
                double dzAvg = (pts[k].Z + pts[(k + 1) % n].Z) * 0.5 - z1;
                if (dzAvg > ZeroDz) res.CutLength += segLen[k];
                else if (dzAvg < -ZeroDz) res.FillLength += segLen[k];
            }
            res.Direction = res.FillLength > res.CutLength ? -1 : 1;
            res.Mixed = res.CutLength > 0 && res.FillLength > 0;

            // ---- 4. Hatch lines (from the raw toe, alternating long/short; bridge indices recorded for filtering after cleanup) ----
            double spacing = Math.Max(combSpacing, L / MaxCombs);
            var combsRaw = new List<Comb>();
            var brackets = new List<int[]>();
            double sEnd = closed ? L - spacing * 0.5 : L + 1e-6;
            int k2 = 0, idx = 0;
            for (double s = 0; s <= sEnd; s += spacing, idx++)
            {
                double sc2 = Math.Min(s, L - 1e-9);
                while (k2 < segCount - 1 && sc2 > cumV[k2 + 1]) k2++;
                double f = segLen[k2] > 1e-12 ? (sc2 - cumV[k2]) / segLen[k2] : 0;
                if (f < 0) f = 0; if (f > 1) f = 1;
                int iA = k2, iB = (k2 + 1) % n;
                var top = Lerp3(pts[iA], pts[iB], f);
                var t2 = Lerp2(rawToe[iA], rawToe[iB], f);
                var toeEnd = new Point3d(t2.X, t2.Y, z1);
                double w = Math.Sqrt((toeEnd.X - top.X) * (toeEnd.X - top.X)
                                   + (toeEnd.Y - top.Y) * (toeEnd.Y - top.Y));
                if (w < MinCombWidth) continue;   // not drawn in zero-width zones, not counted as skipped
                bool full = idx % 2 == 0;
                var mid = new Point3d((top.X + toeEnd.X) / 2, (top.Y + toeEnd.Y) / 2, (top.Z + z1) / 2);
                // the long stroke always points from high to low: high side is the source line when it is above target, otherwise the generated (crest) line
                bool srcHigh = top.Z - z1 > 0;
                var hi = srcHigh ? top : toeEnd;
                var lo = srcHigh ? toeEnd : top;
                combsRaw.Add(new Comb { Top = hi, End = full ? lo : mid, Full = full });
                brackets.Add(new[] { iA, iB });
            }

            // ---- 5. Toe-line self-intersection loop removal (cut out knots from variable offset on the concave side) ----
            var dead = new HashSet<int>();
            bool again = true;
            while (again && res.LoopsRemoved < MaxLoops)
            {
                again = false;
                int m = rawToe.Count;
                for (int a = 0; a + 1 < m && !again; a++)
                    for (int b = a + 2; b + 1 < m && !again; b++)
                    {
                        if (!SegInt(rawToe[a], rawToe[a + 1], rawToe[b], rawToe[b + 1], out Point2d x))
                            continue;
                        for (int t = a + 1; t <= b; t++) if (orig[t] >= 0) dead.Add(orig[t]);
                        rawToe.RemoveRange(a + 1, b - a);
                        orig.RemoveRange(a + 1, b - a);
                        rawToe.Insert(a + 1, x);
                        orig.Insert(a + 1, -1);
                        res.LoopsRemoved++;
                        again = true;
                    }
            }

            // ---- 6. Finalise hatch lines: keep those whose bridging sample point survived ----
            for (int i = 0; i < combsRaw.Count; i++)
            {
                if (dead.Contains(brackets[i][0]) || dead.Contains(brackets[i][1]))
                { res.CombsSkipped++; continue; }
                res.Combs.Add(combsRaw[i]);
            }

            // ---- 7. Output the toe line after collinear weeding ----
            foreach (int i in Weed(rawToe, WeedTol))
                res.Toe.Add(new Point3d(rawToe[i].X, rawToe[i].Y, z1));
            if (res.MinWidth == double.MaxValue) res.MinWidth = 0;
            return res;
        }

        /// <summary>
        /// Splits the line into same-direction segments by "source above/below target elevation" and casts each. Returns results in line order;
        /// each segment's Direction: +1 = slope down (source on top, product is a toe line),
        /// -1 = slope up (source below, product is a crest line).
        /// Where the line crosses the target elevation a zero-width crossing point is inserted, and both adjacent segments converge onto the source line there.
        /// A line entirely on one side returns a single result and keeps closure; a line entirely level returns a single AllZero.
        /// </summary>
        public static List<CastResult> CastSplit(IList<Point3d> samples, bool closed, int side,
            double slopeN, double z1, double combSpacing)
        {
            var outList = new List<CastResult>();

            // same plan de-duplication as Cast, so the sign array matches the geometry one to one
            var pts = new List<Point3d>(samples.Count);
            foreach (var p in samples)
                if (pts.Count == 0 || Dist2d(pts[pts.Count - 1], p) > 1e-6) pts.Add(p);
            if (closed && pts.Count > 1 && Dist2d(pts[0], pts[pts.Count - 1]) < 1e-6)
                pts.RemoveAt(pts.Count - 1);
            int n = pts.Count;
            if (n < 2) { outList.Add(new CastResult { TooShort = true }); return outList; }

            var sgn = new int[n];
            bool hasUp = false, hasDown = false;
            for (int i = 0; i < n; i++)
            {
                double dz = pts[i].Z - z1;
                sgn[i] = dz > ZeroDz ? 1 : (dz < -ZeroDz ? -1 : 0);
                if (sgn[i] > 0) hasDown = true;
                else if (sgn[i] < 0) hasUp = true;
            }
            if (!hasUp && !hasDown) { outList.Add(new CastResult { AllZero = true }); return outList; }

            // entire line on one side: cast once, closure preserved
            if (!(hasUp && hasDown))
            {
                var one = Cast(pts, closed, side, slopeN, z1, combSpacing);
                one.Direction = hasDown ? 1 : -1;
                if (!one.TooShort && !one.AllZero) outList.Add(one);
                return outList;
            }

            // mixed: rotate a closed line to a sign-change boundary first, then always split as an open line
            var order = new List<int>(n + 1);
            if (closed)
            {
                int b = 0;
                for (int i = 0; i < n; i++)
                {
                    int prev = (i - 1 + n) % n;
                    if (sgn[i] != 0 && sgn[prev] != 0 && sgn[i] != sgn[prev]) { b = i; break; }
                }
                for (int i = 0; i < n; i++) order.Add((b + i) % n);
                order.Add(b);   // wrap back to the start so the closed line's last segment is not lost
            }
            else for (int i = 0; i < n; i++) order.Add(i);

            var run = new List<Point3d>();
            int runSign = 0, prevIdx = -1;
            for (int t = 0; t < order.Count; t++)
            {
                int i = order[t];
                int s = sgn[i];
                if (s == 0) { run.Add(pts[i]); prevIdx = i; continue; }
                if (runSign == 0 || s == runSign)
                {
                    runSign = s;
                    run.Add(pts[i]);
                    prevIdx = i;
                    continue;
                }
                // sign change: insert the point with elevation == z1 as the shared zero-width end of both segments
                var cross = CrossAtZ(pts[prevIdx], pts[i], z1);
                run.Add(cross);
                FlushRun(outList, run, runSign, side, slopeN, z1, combSpacing);
                run = new List<Point3d> { cross, pts[i] };
                runSign = s;
                prevIdx = i;
            }
            FlushRun(outList, run, runSign, side, slopeN, z1, combSpacing);
            return outList;
        }

        static void FlushRun(List<CastResult> outList, List<Point3d> run, int runSign, int side,
            double slopeN, double z1, double combSpacing)
        {
            if (run.Count < 2 || runSign == 0) return;
            var r = Cast(run, false, side, slopeN, z1, combSpacing);
            if (r.TooShort || r.AllZero) return;
            r.Direction = runSign;
            outList.Add(r);
        }

        /// <summary>Linearly interpolates the point with z==z1 between two points; on degenerate height difference uses the plan position of the first point.</summary>
        static Point3d CrossAtZ(Point3d a, Point3d b, double z1)
        {
            double dz = b.Z - a.Z;
            if (Math.Abs(dz) < 1e-12) return new Point3d(a.X, a.Y, z1);
            double f = (z1 - a.Z) / dz;
            if (f < 0) f = 0;
            if (f > 1) f = 1;
            return new Point3d(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, z1);
        }

        // ---- Geometry helpers ----

        static double Dist2d(Point3d a, Point3d b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        static Point3d Lerp3(Point3d a, Point3d b, double f)
            => new Point3d(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, a.Z + (b.Z - a.Z) * f);

        static Point2d Lerp2(Point2d a, Point2d b, double f)
            => new Point2d(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);

        /// <summary>Proper segment intersection (touching endpoints do not count); the intersection is solved from the parameters.</summary>
        static bool SegInt(Point2d a, Point2d b, Point2d c, Point2d d, out Point2d x)
        {
            x = default;
            double rx = b.X - a.X, ry = b.Y - a.Y;
            double sx = d.X - c.X, sy = d.Y - c.Y;
            double denom = rx * sy - ry * sx;
            if (Math.Abs(denom) < 1e-12) return false;
            double qx = c.X - a.X, qy = c.Y - a.Y;
            double t = (qx * sy - qy * sx) / denom;
            double u = (qx * ry - qy * rx) / denom;
            const double e = 1e-9;
            if (t <= e || t >= 1 - e || u <= e || u >= 1 - e) return false;
            x = new Point2d(a.X + t * rx, a.Y + t * ry);
            return true;
        }

        /// <summary>Sequential weeding: intermediate points are dropped when all of them deviate from the anchor-candidate chord by no more than the tolerance. Returns the indices kept.</summary>
        static List<int> Weed(List<Point2d> p, double tol)
        {
            var keep = new List<int> { 0 };
            int a = 0;
            for (int c = a + 2; c < p.Count; c++)
            {
                double maxd = 0;
                for (int t = a + 1; t < c; t++)
                {
                    double d = PerpDist(p[a], p[c], p[t]);
                    if (d > maxd) maxd = d;
                }
                if (maxd > tol) { keep.Add(c - 1); a = c - 1; c = a + 1; }
            }
            if (p.Count > 1 && keep[keep.Count - 1] != p.Count - 1) keep.Add(p.Count - 1);
            return keep;
        }

        static double PerpDist(Point2d a, Point2d b, Point2d p)
        {
            double vx = b.X - a.X, vy = b.Y - a.Y;
            double len = Math.Sqrt(vx * vx + vy * vy);
            if (len < 1e-12)
            {
                double dx = p.X - a.X, dy = p.Y - a.Y;
                return Math.Sqrt(dx * dx + dy * dy);
            }
            return Math.Abs(vx * (p.Y - a.Y) - vy * (p.X - a.X)) / len;
        }
    }
}
