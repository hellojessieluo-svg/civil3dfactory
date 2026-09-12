#nullable disable   // This file is compiled by both Civil3DFactory (nullable off) and WaterBox (nullable on); treat as off

using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory.Geometry
{
    /// <summary>
    /// Algorithm core of the offset cone + line-shaping toolchain (pure geometry/DB, no interaction or Editor dependencies).
    /// Single source of truth: Civil3DFactory (node offset_cone_contours) and products\waterbox (YT/NH/GY/GZ commands)
    /// both compile this file; algorithm changes go here only. Derived from Civil3D-007-DLL preliminary offset cone v0.7 (2026-07).
    /// </summary>
    public static class OffsetConeCore
    {
        const double Tol = 1e-9;
        const double MinRingArea = 1e-6;        // total offset area below this counts as collapsed
        const double MinDeflectionDeg = 8.0;    // vertices deflecting less than this are not rounded (same as the collinear-removal threshold)
        const double CollinearDeg = 8.0;        // pre-simplification: straight vertices deflecting less than this are collinear noise
        const double LMinFactor = 0.5;          // pre-simplification: straight segments shorter than radius x this factor are fragments

        /// <summary>GenerateCone result for one boundary.</summary>
        public sealed class ConeResult
        {
            public double Z0;            // original boundary elevation
            public double Reached;       // elevation actually reached
            public int Rings;            // rings generated
            public bool Collapsed;       // collapsed before reaching the target
            public bool SameElevation;   // target elevation = boundary elevation, nothing done
            public int SteppedRings;     // rings that took the "offset one more step from the previous ring" fallback (accumulated error)
        }

        // ============================================================
        //  Offset cone: batch inward offsets of a closed boundary to generate contours
        //  Pipeline: smoothed copy of the boundary (Smooth) -> offset ring by ring -> per-ring re-rounding -> assign elevation
        //  emit: called once per generated ring (Elevation/Layer already set); the caller appends it to the database.
        //  The caller guarantees src is closed (or IsSnapClosed).
        // ============================================================
        public static ConeResult GenerateCone(Polyline src, double z1, double n, double dz,
            double rFillet, Action<Polyline> emit, bool outward = false)
        {
            double z0 = src.Elevation;
            var result = new ConeResult { Z0 = z0, Reached = z0 };
            if (Math.Abs(z1 - z0) < Tol) { result.SameElevation = true; return result; }
            // Upward = island closing to a top, downward = pit closing to a bottom; offset inward by default.
            // outward=true offsets outward: the boundary is the top contour, spread outward down to z1 (island side slope down to the water bed).
            int dir = z1 > z0 ? 1 : -1;

            // First make an in-memory smoothed copy as the offset base (the source boundary itself is untouched)
            // Even with r=0 make a closure-normalised copy, to handle snap-closed fake-open lines
            Polyline work = rFillet > Tol ? Smooth(src, rFillet)
                          : src.Closed ? src
                          : NormalizeClosedCopy(src);

            // Elevation sequence: interval grid + target elevation (always included)
            var zList = new List<double>();
            for (double z = z0 + dir * dz; dir * (z1 - z) > Tol; z += dir * dz) zList.Add(z);
            zList.Add(z1);

            double sign = 0;      // sign of the offset direction, decided on the first ring and reused
            var prev = new List<Polyline>();   // copies of the previous ring; only used as the fallback base when offsetting from the base fails
            foreach (double z in zList)
            {
                double d = Math.Abs(z - z0) * n;   // every ring measures the total distance from the smoothed base
                DBObjectCollection ring;

                if (sign == 0)
                {
                    // Direction test: offset d on both sides; the side with the smaller area is inward, the larger is outward
                    var plus = TryOffset(work, d);
                    var minus = TryOffset(work, -d);
                    double aPlus = TotalArea(plus), aMinus = TotalArea(minus);

                    // Outward never collapses; a collapse means neither side could be offset (source line is broken)
                    if ((!outward && (aPlus < MinRingArea || aMinus < MinRingArea)) ||
                        (outward && aPlus < MinRingArea && aMinus < MinRingArea))
                    {
                        DisposeAll(plus); DisposeAll(minus);
                        result.Collapsed = true; break;
                    }
                    bool takePlus = outward ? aPlus > aMinus : aPlus < aMinus;
                    if (takePlus) { sign = 1; ring = plus; DisposeAll(minus); }
                    else { sign = -1; ring = minus; DisposeAll(plus); }
                }
                else
                {
                    ring = TryOffset(work, sign * d);
                    if (TotalArea(ring) < MinRingArea)
                    {
                        // Offsetting the total distance from the base in one go failed: GetOffsetCurves throws when a concave arc radius is below d.
                        // Fallback: offset one more increment from the previous ring -- trading accumulated error for getting a result at all.
                        DisposeAll(ring);
                        ring = StepFromPrevious(prev, sign * n * dz);
                        if (TotalArea(ring) < MinRingArea)
                        {
                            DisposeAll(ring);
                            result.Collapsed = true; break;
                        }
                        result.SteppedRings++;
                    }
                }

                // A concave boundary may split into several rings when offset: keep them all, same elevation
                DisposeAll2(prev); prev.Clear();
                foreach (DBObject o in ring)
                {
                    if (!(o is Polyline p)) { o.Dispose(); continue; }
                    Polyline final = p;
                    if (rFillet > Tol)
                    {
                        // Per-ring re-rounding: sharp corners reappear where inward offset shrank arcs away
                        final = Smooth(p, rFillet);
                        p.Dispose();
                    }
                    final.Elevation = z;
                    final.Layer = src.Layer;
                    prev.Add((Polyline)final.Clone());   // keep a copy as the fallback base
                    emit(final);
                }
                result.Rings++;
                result.Reached = z;
            }

            DisposeAll2(prev);
            if (!ReferenceEquals(work, src)) work.Dispose();
            return result;
        }

        // ============================================================
        //  Smooth (line rounding kernel): merge fragments -> remove collinear vertices -> tangent fillets at every corner
        //  Always returns a new Polyline; the input is not modified.
        //  Fillets are arc-aware -- line-line / line-arc / arc-arc joints are all handled
        //  with a small tangent transition arc; original arcs are only trimmed, centre / radius / direction unchanged.
        // ============================================================
        public static Polyline Smooth(Polyline src, double r)
        {
            Polyline simplified = SimplifyVertices(src, r);
            Polyline result = FilletCorners(simplified, r);
            if (!ReferenceEquals(result, simplified)) simplified.Dispose();
            return result;
        }

        // ============================================================
        //  FitArcs (arc-fitting simplification): traced dense straight segments -> a few straight + arc segments
        //  Greedy tolerance fit: line and arc compete to extend; whichever covers more vertices wins;
        //  arc = circle through first / middle / last point, all vertices in the window within tolerance and monotonic along the arc.
        //  Only handles straight-only polylines (un-arc ones with arcs first). Degenerate (too few vertices) returns null.
        // ============================================================
        public static Polyline FitArcs(Polyline src, double tol)
        {
            var raw = new List<(Point2d pt, double bulge)>();
            for (int i = 0; i < src.NumberOfVertices; i++)
                raw.Add((src.GetPoint2dAt(i), 0.0));
            bool closed = src.Closed;
            NormalizeClosure(raw, ref closed);
            if (raw.Count < 4) return null;

            var points = new List<Point2d>(raw.Count);
            foreach (var t in raw) points.Add(t.pt);

            var fit = FitSequence(points, tol, closed);
            if (fit.Count < (closed ? 3 : 2)) return null;

            var res = new Polyline(fit.Count);
            for (int i = 0; i < fit.Count; i++)
                res.AddVertexAt(i, fit[i].pt, fit[i].bulge, 0, 0);
            res.Closed = closed;
            res.SetPropertiesFrom(src);
            res.Normal = src.Normal;
            res.Elevation = src.Elevation;
            return res;
        }

        // Greedy fit: cut the point list into runs, each covered by a line or an arc with as many points as possible
        static List<(Point2d pt, double bulge)> FitSequence(List<Point2d> p, double tol, bool loop)
        {
            var seq = new List<Point2d>(p);
            if (loop) seq.Add(p[0]);           // closed: append the start point so the full loop is walked
            var res = new List<(Point2d pt, double bulge)>();
            int last = seq.Count - 1;
            int i = 0;
            while (i < last)
            {
                // The first run of a closed line may not swallow everything, so at least two output vertices remain
                int cap = (loop && i == 0) ? last - 1 : last;
                if (cap <= i) cap = i + 1;
                int endLine = ExtendLine(seq, i, tol, cap);
                int jA = ExtendArc(seq, i, tol, cap, out double bulge);
                if (jA > endLine) { res.Add((seq[i], bulge)); i = jA; }
                else { res.Add((seq[i], 0)); i = endLine; }
            }
            if (!loop) res.Add((seq[last], 0));
            return res;
        }

        static int ExtendLine(List<Point2d> s, int i, double tol, int cap)
        {
            int best = Math.Min(i + 1, cap);
            for (int j = i + 2; j <= cap; j++)
            {
                if (LineDeviation(s, i, j) > tol) break;
                best = j;
            }
            return best;
        }

        static double LineDeviation(List<Point2d> s, int i, int j)
        {
            Vector2d d = s[j] - s[i];
            if (d.Length < Tol) return double.MaxValue;
            Vector2d u = d.GetNormal();
            double max = 0;
            for (int t = i + 1; t < j; t++)
            {
                Vector2d w = s[t] - s[i];
                double dev = Math.Abs(w.X * u.Y - w.Y * u.X);
                if (dev > max) max = dev;
            }
            return max;
        }

        static int ExtendArc(List<Point2d> s, int i, double tol, int cap, out double bulge)
        {
            bulge = 0;
            int best = -1;
            for (int j = i + 2; j <= cap; j++)
            {
                if (!ArcWindowOk(s, i, j, tol, out double b)) break;
                best = j;
                bulge = b;
            }
            return best < 0 ? Math.Min(i + 1, cap) : best;
        }

        // Can window [i..j] be covered by one arc: three-point circle + all-point deviation + monotonic along the arc
        static bool ArcWindowOk(List<Point2d> s, int i, int j, double tol, out double bulge)
        {
            bulge = 0;
            int mid = (i + j) / 2;
            if (mid == i || mid == j) return false;
            if (!Circumcircle(s[i], s[mid], s[j], out Point2d o, out double r)) return false;

            Vector2d v1 = s[mid] - s[i], v2 = s[j] - s[mid];
            double cr = v1.X * v2.Y - v1.Y * v2.X;
            if (Math.Abs(cr) < 1e-12) return false;
            int w = cr > 0 ? 1 : -1;

            double total = SweepBetween(s[i] - o, s[j] - o, w);
            if (total < 1e-6 || total > 2 * Math.PI - 0.1) return false;

            double prev = 0;
            for (int t = i + 1; t < j; t++)
            {
                if (Math.Abs(s[t].GetDistanceTo(o) - r) > tol) return false;
                double ang = SweepBetween(s[i] - o, s[t] - o, w);
                if (ang < prev - 1e-9 || ang > total + 1e-9) return false;
                prev = ang;
            }
            bulge = w * Math.Tan(total / 4);
            return true;
        }

        static bool Circumcircle(Point2d a, Point2d b, Point2d c, out Point2d o, out double r)
        {
            o = default; r = 0;
            double d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
            if (Math.Abs(d) < 1e-12) return false;   // three points collinear
            double a2 = a.X * a.X + a.Y * a.Y;
            double b2 = b.X * b.X + b.Y * b.Y;
            double c2 = c.X * c.X + c.Y * c.Y;
            double ux = (a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d;
            double uy = (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d;
            o = new Point2d(ux, uy);
            r = o.GetDistanceTo(a);
            return true;
        }

        // ============================================================
        //  UnSmooth (line un-rounding): arcs restored to sharp straight corners
        //  Inverse of Smooth: arcs with sweep <= 150 deg are replaced by the intersection of the end tangents (original sharp corner);
        //  restoring larger arcs would produce long spikes, so "arc midpoint + two chords" keeps the shape instead.
        //  Leftover collinear vertices such as tangent points disappear automatically. Always returns a new Polyline.
        // ============================================================
        public static Polyline UnSmooth(Polyline src)
        {
            bool closed = src.Closed;
            var pts = new List<(Point2d pt, double bulge)>();
            for (int i = 0; i < src.NumberOfVertices; i++)
                pts.Add((src.GetPoint2dAt(i), src.GetBulgeAt(i)));
            NormalizeClosure(pts, ref closed);

            int n = pts.Count;
            double maxBulge = Math.Tan(150.0 / 4 * Math.PI / 180.0);   // sweep 150 deg corresponds to bulge ~ 0.767
            var outPts = new List<Point2d>();

            for (int k = 0; k < n; k++)
            {
                double bi = closed ? pts[(k + n - 1) % n].bulge : (k == 0 ? 0 : pts[k - 1].bulge);
                bool hasOut = closed || k < n - 1;
                double bo = hasOut ? pts[k].bulge : 0;
                bool endVertex = !closed && (k == 0 || k == n - 1);

                // Ordinary straight vertices are kept; arc endpoints are absorbed; the ends of an open line must be kept
                if ((Math.Abs(bo) < Tol && Math.Abs(bi) < Tol) || endVertex)
                    outPts.Add(pts[k].pt);

                if (hasOut && Math.Abs(bo) > Tol)
                {
                    Point2d p1 = pts[k].pt, p2 = pts[(k + 1) % n].pt;
                    Vector2d c = p2 - p1;
                    if (c.Length < Tol) continue;

                    if (Math.Abs(bo) <= maxBulge)
                    {
                        // Positive bulge = counter-clockwise (left-turning) arc: start tangent = chord direction rotated by -theta/2 (inverse of
                        // the bulge sign convention in FilletCorners, verified by back-computing fillet output)
                        double half = 2 * Math.Atan(bo);               // half the sweep (signed)
                        Vector2d u = c.GetNormal().RotateBy(-half);    // tangent at the arc start
                        double t = c.Length / (2 * Math.Cos(half));    // distance from endpoint to the tangent intersection
                        outPts.Add(p1 + u * t);                        // restored sharp corner
                    }
                    else
                    {
                        outPts.Add(ArcMidpoint(p1, p2, bo));           // large arc: chord + arc midpoint keeps the shape
                    }
                }
            }

            var res = new Polyline(outPts.Count);
            for (int i = 0; i < outPts.Count; i++)
                res.AddVertexAt(i, outPts[i], 0, 0, 0);
            res.Closed = closed;
            res.SetPropertiesFrom(src);
            res.Normal = src.Normal;
            res.Elevation = src.Elevation;
            return res;
        }

        static Point2d ArcMidpoint(Point2d p1, Point2d p2, double bulge)
        {
            Vector2d c = p2 - p1;
            var mid = new Point2d((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
            // sagitta h = bulge x chord / 2, along the chord's right normal (positive bulge = left-turning arc bulging to the right of travel)
            return mid + c.GetNormal().RotateBy(-Math.PI / 2) * (bulge * c.Length / 2);
        }

        // ---- Pre-simplification: clear fragment vertices so that the spread-out corner angle concentrates again ----
        // Fragment-edge collapse (standalone version of SimplifyVertices step 1, threshold supplied by the caller):
        // straight segments shorter than lMin collapse into one vertex; arc endpoints and open-line ends stay.
        static void CollapseShortStraights(List<(Point2d pt, double bulge)> pts, bool closed, double lMin)
        {
            int minKeep = closed ? 4 : 3;
            int guard = 0;
            bool changed = true;
            while (changed && pts.Count > minKeep && guard++ < 5000)
            {
                changed = false;
                int nv2 = pts.Count;
                int segCount = closed ? nv2 : nv2 - 1;
                for (int i = 0; i < segCount; i++)
                {
                    int j = (i + 1) % nv2;
                    if (Math.Abs(pts[i].bulge) > Tol) continue;                 // this segment is an arc
                    if (pts[i].pt.GetDistanceTo(pts[j].pt) >= lMin) continue;   // not a fragment
                    bool aFixed = (!closed && i == 0) || Math.Abs(pts[(i + nv2 - 1) % nv2].bulge) > Tol;
                    bool bFixed = (!closed && j == nv2 - 1) || Math.Abs(pts[j].bulge) > Tol;
                    if (aFixed && bFixed) continue;
                    Point2d np = aFixed ? pts[i].pt
                               : bFixed ? pts[j].pt
                               : new Point2d((pts[i].pt.X + pts[j].pt.X) / 2, (pts[i].pt.Y + pts[j].pt.Y) / 2);
                    // Displacement guard (project B, 2026-08-24): when the fragment edge meets at a large angle (e.g. a short real edge next to a ring kink),
                    // collapsing pushes the vertex 1~2 m sideways and tilts the whole long edge. If either merged original vertex is more than
                    // 5 cm off the new geometry (prev->np / np->next), do not collapse -- only collinear crumbs may be collapsed.
                    {
                        Point2d prevPt = pts[(i + nv2 - 1) % nv2].pt;
                        Point2d nextPt = pts[(j + 1) % nv2].pt;
                        double DevTo(Point2d q, Point2d a2, Point2d b2)
                        {
                            var ab = b2 - a2;
                            if (ab.Length < Tol) return 0;
                            return Math.Abs((q.X - a2.X) * (-ab.Y) + (q.Y - a2.Y) * ab.X) / ab.Length;
                        }
                        double dev = Math.Max(DevTo(pts[i].pt, prevPt, np), DevTo(pts[j].pt, np, nextPt));
                        if (dev > 0.05) continue;
                    }
                    pts[i] = (np, pts[j].bulge);
                    pts.RemoveAt(j);
                    changed = true;
                    break;
                }
            }
        }

        // ---- Corner-zone clearing: within 1.3 x R*tan(delta/2)+2 (capped at 3R) on both sides of a true corner (deflection >= minDefl),
        // delete small kink vertices (< minDefl) and flatten arcs with sweep <= 15 deg into chords; stop at another true corner or a large arc.
        // Also stop when the accumulated turn exceeds 8 deg (so a string of small kinks that is really a gentle bend is not straightened). Chord-based angles suffice.
        static void ClearCornerZones(List<(Point2d pt, double bulge)> pts, bool closed, double r, double minDeflRad)
        {
            double sweepMax = 15.0 * Math.PI / 180;
            double cumMax = 8.0 * Math.PI / 180;

            int N() => pts.Count;
            Vector2d ChordDir(int s)
            {
                var a = pts[s % N()].pt; var b = pts[(s + 1) % N()].pt;
                var v = b - a;
                return v.Length < Tol ? new Vector2d(1, 0) : v.GetNormal();
            }
            double Defl(int v)
            {
                int n = N();
                if (!closed && (v <= 0 || v >= n - 1)) return 0;
                double dot = Math.Max(-1.0, Math.Min(1.0,
                    ChordDir((v + n - 1) % n).DotProduct(ChordDir(v % n))));
                return Math.Acos(dot);
            }
            double SegSweep(int s) => 4 * Math.Atan(Math.Abs(pts[s % N()].bulge));
            double SegLen(int s) => pts[s % N()].pt.GetDistanceTo(pts[(s + 1) % N()].pt);

            // From every true corner scan both sides, marking arcs to flatten and vertices to delete
            var flatten = new HashSet<int>();
            var remove = new HashSet<int>();
            int n0 = N();
            int vStart0 = closed ? 0 : 1, vEnd0 = closed ? n0 : n0 - 1;
            for (int v = vStart0; v < vEnd0; v++)
            {
                double delta = Defl(v);
                if (delta < minDeflRad || delta > Math.PI - 0.01) continue;
                double zone = Math.Min(1.3 * r * Math.Tan(Math.Min(delta, 2.8) / 2) + 2, 3 * r);

                foreach (int dir in new[] { +1, -1 })
                {
                    double acc = 0, cum = 0;
                    int seg = dir > 0 ? v : (v + n0 - 1) % n0;   // starting segment
                    for (int step = 0; step < n0; step++)
                    {
                        // sagitta = chord x |bulge| / 2. "sweep <= 15 deg" cannot tell the arc size -- a large-radius gentle arc sweeping 9 deg
                        // still has a metre-level sagitta (the culprit when a 316 m ring arc on project B was flattened into a chord and the terrace edge drifted 1.5 m).
                        // A real stub has a centimetre-level sagitta: anything above 5 cm is treated as a large arc -- leave it and stop.
                        double sag = SegLen(seg) * Math.Abs(pts[seg % N()].bulge) / 2;
                        if (SegSweep(seg) > sweepMax || sag > 0.05) break;   // large arc / long gentle arc: leave it, stop
                        if (SegSweep(seg) > Tol) flatten.Add(seg % n0);
                        acc += SegLen(seg);
                        if (acc >= zone) break;
                        int nxtV = dir > 0 ? (seg + 1) % n0 : seg;             // vertex reached
                        if (!closed && (nxtV == 0 || nxtV == n0 - 1)) break;   // end of an open line
                        double dv = Defl(nxtV);
                        if (dv >= minDeflRad) break;              // another true corner: it gets its own fillet
                        cum += dv;
                        if (cum > cumMax) break;                  // gentle-bend protection
                        remove.Add(nxtV);
                        seg = dir > 0 ? (seg + 1) % n0 : (seg + n0 - 1) % n0;
                    }
                }
            }
            if (flatten.Count == 0 && remove.Count == 0) return;

            foreach (int s in flatten) pts[s] = (pts[s].pt, 0);
            var keep = new List<(Point2d pt, double bulge)>(pts.Count);
            for (int i = 0; i < pts.Count; i++)
            {
                // Delete only vertices with straight segments on both sides (mostly straight after flattening; those still next to an arc stay, keeping tangent points);
                // displacement guard (project B, 2026-08-24): a kink < minDefl between two long edges is a real gentle bend;
                // deleting it merges both long edges into a chord and lifts the geometry by metres (a 2 deg kink x 13 m / 333 m on a ring edge drifted 1.6 m).
                // A vertex more than 5 cm off the line joining its neighbours is not deleted -- a real stub is always centimetre-level.
                if (remove.Contains(i) &&
                    Math.Abs(pts[i].bulge) <= Tol &&
                    Math.Abs(pts[(i + pts.Count - 1) % pts.Count].bulge) <= Tol)
                {
                    var pa = pts[(i + pts.Count - 1) % pts.Count].pt;
                    var pb = pts[(i + 1) % pts.Count].pt;
                    var ab = pb - pa;
                    double dev = ab.Length < Tol ? 0
                        : Math.Abs((pts[i].pt.X - pa.X) * (-ab.Y) + (pts[i].pt.Y - pa.Y) * ab.X) / ab.Length;
                    if (dev <= 0.05) continue;
                }
                keep.Add(pts[i]);
            }
            if (keep.Count >= (closed ? 4 : 3)) { pts.Clear(); pts.AddRange(keep); }
        }

        // Collinear vertex removal (standalone version of SimplifyVertices step 2, threshold supplied by the caller):
        // vertices with straight segments on both sides and deflection < threshold are deleted. Open-line ends stay.
        static void RemoveCollinearVertices(List<(Point2d pt, double bulge)> pts, bool closed, double maxDeflDeg)
        {
            double colTol = maxDeflDeg * Math.PI / 180.0;
            int minKeep = closed ? 4 : 3;
            int guard = 0;
            bool changed = true;
            while (changed && pts.Count > minKeep && guard++ < 5000)
            {
                changed = false;
                int nv2 = pts.Count;
                int start = closed ? 0 : 1, end = closed ? nv2 : nv2 - 1;
                for (int k = start; k < end; k++)
                {
                    int prev = (k + nv2 - 1) % nv2, next = (k + 1) % nv2;
                    if (Math.Abs(pts[prev].bulge) > Tol || Math.Abs(pts[k].bulge) > Tol) continue;
                    Vector2d vin = pts[k].pt - pts[prev].pt, vout = pts[next].pt - pts[k].pt;
                    if (vin.Length < Tol || vout.Length < Tol) continue;
                    double dot = Math.Max(-1.0, Math.Min(1.0, vin.GetNormal().DotProduct(vout.GetNormal())));
                    if (Math.Acos(dot) < colTol)
                    {
                        // Displacement guard (project B, 2026-08-24): the angle threshold ignores scale -- a 0.4 deg kink with 200 m on each side
                        // means 1.4 m displacement when deleted. A vertex more than 5 cm off the line joining its neighbours is not deleted.
                        var abg = pts[next].pt - pts[prev].pt;
                        double devg = abg.Length < Tol ? 0
                            : Math.Abs((pts[k].pt.X - pts[prev].pt.X) * (-abg.Y)
                                     + (pts[k].pt.Y - pts[prev].pt.Y) * abg.X) / abg.Length;
                        if (devg > 0.05) continue;
                        pts.RemoveAt(k);
                        changed = true;
                        break;
                    }
                }
            }
        }

        static Polyline SimplifyVertices(Polyline src, double r)
        {
            bool closed = src.Closed;
            var pts = new List<(Point2d pt, double bulge)>();
            for (int i = 0; i < src.NumberOfVertices; i++)
                pts.Add((src.GetPoint2dAt(i), src.GetBulgeAt(i)));

            // Closure normalisation: a snap-closed line has Closed=false but coincident ends -- treat as closed and drop the duplicate point,
            // otherwise the first/last vertices are protected as open-line ends and the closing corner never gets rounded
            NormalizeClosure(pts, ref closed);

            double lMin = r * LMinFactor;
            double colTol = CollinearDeg * Math.PI / 180.0;
            int minKeep = closed ? 4 : 3;

            // 1) Merge fragments: straight segments shorter than lMin collapse into one vertex (arcs and open-line ends stay)
            int guard = 0;
            bool changed = true;
            while (changed && pts.Count > minKeep && guard++ < 5000)
            {
                changed = false;
                int nv = pts.Count;
                int segCount = closed ? nv : nv - 1;
                for (int i = 0; i < segCount; i++)
                {
                    int j = (i + 1) % nv;
                    if (Math.Abs(pts[i].bulge) > Tol) continue;                 // this segment is an arc
                    if (pts[i].pt.GetDistanceTo(pts[j].pt) >= lMin) continue;   // not a fragment

                    bool aFixed = (!closed && i == 0) || Math.Abs(pts[(i + nv - 1) % nv].bulge) > Tol;
                    bool bFixed = (!closed && j == nv - 1) || Math.Abs(pts[j].bulge) > Tol;
                    if (aFixed && bFixed) continue;                             // neither end may move

                    Point2d np = aFixed ? pts[i].pt
                               : bFixed ? pts[j].pt
                               : new Point2d((pts[i].pt.X + pts[j].pt.X) / 2, (pts[i].pt.Y + pts[j].pt.Y) / 2);
                    pts[i] = (np, pts[j].bulge);   // the merged vertex takes over j's outgoing bulge
                    pts.RemoveAt(j);
                    changed = true;
                    break;
                }
            }

            // 2) Collinear removal: vertices with straight segments on both sides and deflection < threshold are noise
            guard = 0;
            changed = true;
            while (changed && pts.Count > minKeep && guard++ < 5000)
            {
                changed = false;
                int nv = pts.Count;
                int start = closed ? 0 : 1, end = closed ? nv : nv - 1;
                for (int k = start; k < end; k++)
                {
                    int prev = (k + nv - 1) % nv, next = (k + 1) % nv;
                    if (Math.Abs(pts[prev].bulge) > Tol || Math.Abs(pts[k].bulge) > Tol) continue;
                    Vector2d vin = pts[k].pt - pts[prev].pt, vout = pts[next].pt - pts[k].pt;
                    if (vin.Length < Tol || vout.Length < Tol) continue;
                    double dot = Math.Max(-1.0, Math.Min(1.0, vin.GetNormal().DotProduct(vout.GetNormal())));
                    if (Math.Acos(dot) < colTol)
                    {
                        // Displacement guard (project B, 2026-08-24): the angle threshold ignores scale -- a 0.4 deg kink with 200 m on each side
                        // means 1.4 m displacement when deleted. A vertex more than 5 cm off the line joining its neighbours is not deleted.
                        var abg = pts[next].pt - pts[prev].pt;
                        double devg = abg.Length < Tol ? 0
                            : Math.Abs((pts[k].pt.X - pts[prev].pt.X) * (-abg.Y)
                                     + (pts[k].pt.Y - pts[prev].pt.Y) * abg.X) / abg.Length;
                        if (devg > 0.05) continue;
                        pts.RemoveAt(k);
                        changed = true;
                        break;
                    }
                }
            }

            var res = new Polyline(pts.Count);
            for (int i = 0; i < pts.Count; i++)
                res.AddVertexAt(i, pts[i].pt, pts[i].bulge, 0, 0);
            res.Closed = closed;
            res.SetPropertiesFrom(src);
            res.Normal = src.Normal;
            res.Elevation = src.Elevation;
            return res;
        }

        // ---- Sharp-corner rounding (arc-aware): line-line / line-arc / arc-arc joints handled uniformly ----
        // Fixed radius r = constant curvature; the transition arc is tangent to both sides, original arcs are only trimmed, not reshaped.
        // When the radius does not fit, halve and retry automatically (up to 6 times); if still not, keep the original corner.
        // Each joint consumes <= 45% of the length of each adjacent segment. The first/last vertices of an open line are not handled.
        static Polyline FilletCorners(Polyline src, double r)
        {
            int nv = src.NumberOfVertices;
            if (nv < 3) return src;
            bool closed = src.Closed;
            double minDefl = MinDeflectionDeg * Math.PI / 180.0;

            var pts = new List<(Point2d pt, double bulge)>(nv);
            for (int i = 0; i < nv; i++)
                pts.Add((src.GetPoint2dAt(i), src.GetBulgeAt(i)));

            int n = pts.Count;
            int segCount = closed ? n : n - 1;
            if (segCount < 2) return src;

            var segs = new Seg[segCount];
            for (int k = 0; k < segCount; k++)
                segs[k] = BuildSeg(pts[k].pt, pts[(k + 1) % n].pt, pts[k].bulge);

            // Solve the transition arc per vertex (vertex i joins segs[i-1] and segs[i])
            var junc = new Junction?[n];
            int vStart = closed ? 0 : 1, vEnd = closed ? n : n - 1;
            for (int i = vStart; i < vEnd; i++)
            {
                Seg s1 = segs[(i + segCount - 1) % segCount];
                Seg s2 = segs[i % segCount];
                if (s1.A.GetDistanceTo(s1.B) < Tol || s2.A.GetDistanceTo(s2.B) < Tol) continue;

                Point2d v = pts[i].pt;
                Vector2d t1 = TangentAt(s1, v), t2 = TangentAt(s2, v);
                double dot = Math.Max(-1.0, Math.Min(1.0, t1.DotProduct(t2)));
                double delta = Math.Acos(dot);            // tangent deflection angle
                if (delta < minDefl || delta > Math.PI - 0.01) continue;

                double rr = r;
                for (int a = 0; a < 6; a++, rr /= 2)
                    if (TrySolveJunction(s1, s2, v, rr, out var jj)) { junc[i] = jj; break; }
            }

            // Rebuild: output the (possibly trimmed) start point of each segment, inserting transition-arc vertices at the joints
            var outPts = new List<(Point2d pt, double bulge)>(n * 2);
            for (int k = 0; k < segCount; k++)
            {
                int vi = k, vj = (k + 1) % n;
                Point2d sp = junc[vi] is Junction ja ? ja.Tp2 : pts[vi].pt;
                Point2d ep = junc[vj] is Junction jb0 ? jb0.Tp1 : pts[vj].pt;
                double b = segs[k].IsArc ? BulgeOf(segs[k], sp, ep) : 0;
                outPts.Add((sp, b));
                if (junc[vj] is Junction jb)
                    outPts.Add((jb.Tp1, jb.Bulge));
            }
            if (!closed) outPts.Add((pts[n - 1].pt, 0));

            var res = new Polyline(outPts.Count);
            for (int i = 0; i < outPts.Count; i++)
                res.AddVertexAt(i, outPts[i].pt, outPts[i].bulge, 0, 0);
            res.Closed = closed;
            res.SetPropertiesFrom(src);
            res.Normal = src.Normal;
            res.Elevation = src.Elevation;
            return res;
        }

        // ============================================================
        //  RoundSharpCorners (sharp-corner detection and rounding, for terrace corners):
        //  Differences from FilletCorners -- (1) no pre-simplification at all; the source line's natural gentle bends and
        //  dense small kinks are kept as-is; (2) only "true sharp corners" with deflection >= threshold are rounded;
        //  (3) when the radius does not fit, halve and retry 6 times; if still not, keep the sharp corner and count it.
        //  Always returns a new Polyline; the input is not modified.
        // ============================================================
        public sealed class RoundStats
        {
            public int Rounded;      // sharp corners rounded
            public int KeptGentle;   // clear kinks (>= 8 deg) below the threshold, judged natural bends and kept
            public int CantFit;      // should be rounded but the radius did not fit after 6 halvings; sharp corner kept
            public int Downgraded;   // standard radius did not fit; rounded with the largest radius that fits locally (binary search upward)
            public double MinUsedR;  // smallest radius used among downgraded corners (0 if none downgraded)
            public int Spanned;      // spanned corners: a short edge cannot hold the standard radius, so one R arc spans the short edge and swallows the adjacent corner
        }

        public static Polyline RoundSharpCorners(Polyline src, double r, double minDeflDeg, RoundStats stats)
        {
            int nv = src.NumberOfVertices;
            if (nv < 3) return (Polyline)src.Clone();
            bool closed = src.Closed;
            double minDefl = minDeflDeg * Math.PI / 180.0;
            double noiseDefl = 8.0 * Math.PI / 180.0;   // below 8 deg counts as line-shape noise, not counted as "kept"

            var pts = new List<(Point2d pt, double bulge)>(nv);
            for (int i = 0; i < nv; i++)
                pts.Add((src.GetPoint2dAt(i), src.GetBulgeAt(i)));
            NormalizeClosure(pts, ref closed);

            // Micro-fragment collapse (threshold r/4 capped at 5 m, still far below Smooth's r/2): boolean subtraction often leaves
            // metre-level straight fragments at intersections and skewed ends that squeeze out the fillet room of adjacent corners (each segment yields only 45%).
            // Only straight edges are collapsed; arc ends stay.
            CollapseShortStraights(pts, closed, Math.Max(0.05, Math.Min(r / 4, 5.0)));

            // Collinear fragment merge (0.5 deg, far stricter than Smooth's 8 deg; natural bends of the extent line are unaffected):
            // the boolean cuts long straight edges into several collinear short pieces; the fillet solver only looks at the adjacent piece, and a short piece
            // forces a halving downgrade -- merge the full long edge back first so large corners get the full radius.
            RemoveCollinearVertices(pts, closed, 0.5);

            // Corner-zone clearing: micro stubs within one tangent length on both sides of a true corner (small kinks < 8 deg, short arcs
            // with sweep <= 15 deg) are straightened and flattened -- a person filleting treats both sides as full straight edges. Geometry outside
            // that zone is untouched; fit to the channel boundary yields only inside the zone that becomes the fillet anyway.
            ClearCornerZones(pts, closed, r, minDefl);

            // Spanned corners: when an edge is too short to hold both tangent distances, one full-R arc spans the short edge and is tangent to
            // the long edges on both sides, swallowing the small edge and corners in between (manual filleting merges corner clusters the same way).
            SwallowShortEdges(pts, closed, r, minDefl, stats);

            int n = pts.Count;
            int segCount = closed ? n : n - 1;
            if (segCount < 2) return (Polyline)src.Clone();

            var segs = new Seg[segCount];
            for (int k = 0; k < segCount; k++)
                segs[k] = BuildSeg(pts[k].pt, pts[(k + 1) % n].pt, pts[k].bulge);

            // First pass: tangent deflection angle at every vertex
            var deltaArr = new double[n];
            int vStart = closed ? 0 : 1, vEnd = closed ? n : n - 1;
            for (int i = vStart; i < vEnd; i++)
            {
                Seg s1 = segs[(i + segCount - 1) % segCount];
                Seg s2 = segs[i % segCount];
                if (s1.A.GetDistanceTo(s1.B) < Tol || s2.A.GetDistanceTo(s2.B) < Tol) continue;
                Point2d v = pts[i].pt;
                double dot = Math.Max(-1.0, Math.Min(1.0, TangentAt(s1, v).DotProduct(TangentAt(s2, v))));
                deltaArr[i] = Math.Acos(dot);
            }

            // Second pass: solve transition arcs. When the far end of the adjacent segment has no competing fillet (not a true corner), the consumption cap rises to 0.9
            var junc = new Junction?[n];
            for (int i = vStart; i < vEnd; i++)
            {
                double delta = deltaArr[i];
                if (delta <= 0 || delta > Math.PI - 0.01) continue;  // fold-back / degenerate corners are not handled
                if (delta < minDefl)
                { if (delta >= noiseDefl) stats.KeptGentle++; continue; }

                Seg s1 = segs[(i + segCount - 1) % segCount];
                Seg s2 = segs[i % segCount];
                Point2d v = pts[i].pt;
                int far1 = (i + n - 1) % n, far2 = (i + 1) % n;
                double cap1 = deltaArr[far1] >= minDefl ? 0.45 : 0.9;
                double cap2 = deltaArr[far2] >= minDefl ? 0.45 : 0.9;

                bool solved = false;
                double rr = r;
                for (int a = 0; a < 6; a++, rr /= 2)
                    if (TrySolveJunction(s1, s2, v, rr, cap1, cap2, out var jj)) { junc[i] = jj; solved = true; break; }
                if (solved && rr < r)
                {
                    // Halving only offers R/2 steps -- a corner that can take R18 should not get just R10.
                    // Binary search upward within [rr, min(R, 2rr)) for the largest radius that fits locally.
                    double lo = rr, hi = Math.Min(r, rr * 2);
                    for (int a = 0; a < 6; a++)
                    {
                        double mid = (lo + hi) / 2;
                        if (TrySolveJunction(s1, s2, v, mid, cap1, cap2, out var jm)) { junc[i] = jm; lo = mid; }
                        else hi = mid;
                    }
                    stats.Downgraded++;
                    if (stats.MinUsedR <= 0 || lo < stats.MinUsedR) stats.MinUsedR = lo;
                }
                if (solved) stats.Rounded++; else stats.CantFit++;
            }

            var outPts = new List<(Point2d pt, double bulge)>(n * 2);
            for (int k = 0; k < segCount; k++)
            {
                int vi = k, vj = (k + 1) % n;
                Point2d sp = junc[vi] is Junction ja ? ja.Tp2 : pts[vi].pt;
                Point2d ep = junc[vj] is Junction jb0 ? jb0.Tp1 : pts[vj].pt;
                double b = segs[k].IsArc ? BulgeOf(segs[k], sp, ep) : pts[k].bulge;
                outPts.Add((sp, b));
                if (junc[vj] is Junction jb)
                    outPts.Add((jb.Tp1, jb.Bulge));
            }
            if (!closed) outPts.Add((pts[n - 1].pt, 0));

            var res = new Polyline(outPts.Count);
            for (int i = 0; i < outPts.Count; i++)
                res.AddVertexAt(i, outPts[i].pt, outPts[i].bulge, 0, 0);
            res.Closed = closed;
            res.SetPropertiesFrom(src);
            res.Normal = src.Normal;
            res.Elevation = src.Elevation;
            return res;
        }

        // ============================================================
        //  SwallowShortEdges (spanned corners): a corner unsolvable at full R = the adjacent edge is too short for the
        //  tangent distance. Instead of shrinking the radius, pick a vertex window containing the corner (2~3 vertices turning
        //  the same way), solve one arc of radius R tangent to the outer edges on both sides of the window, and replace the short
        //  edges and corners inside the window with that arc. Guards: S-shaped reverse bends are not swallowed (no single-arc solution);
        //  swallowed edge total <= 1.6R; swallowed vertices must all lie outside the arc within R of it (against ghost solutions).
        //  After each swallow the whole ring is rescanned until nothing is left to swallow.
        // ============================================================
        static void SwallowShortEdges(List<(Point2d pt, double bulge)> pts, bool closed,
            double r, double minDefl, RoundStats stats)
        {
            if (pts.Count < 4) return;
            double noiseDefl = 8.0 * Math.PI / 180.0;

            Seg[] segs = null; double[] delta = null; int[] side = null;
            int n = 0, segCount = 0;

            void Rebuild()
            {
                n = pts.Count;
                segCount = closed ? n : n - 1;
                segs = new Seg[segCount];
                for (int k = 0; k < segCount; k++)
                    segs[k] = BuildSeg(pts[k].pt, pts[(k + 1) % n].pt, pts[k].bulge);
                delta = new double[n]; side = new int[n];
                int vs = closed ? 0 : 1, ve = closed ? n : n - 1;
                for (int i = vs; i < ve; i++)
                {
                    Seg s1 = segs[(i + segCount - 1) % segCount], s2 = segs[i % segCount];
                    if (s1.A.GetDistanceTo(s1.B) < Tol || s2.A.GetDistanceTo(s2.B) < Tol) continue;
                    Vector2d t1 = TangentAt(s1, pts[i].pt), t2 = TangentAt(s2, pts[i].pt);
                    delta[i] = Math.Acos(Math.Max(-1.0, Math.Min(1.0, t1.DotProduct(t2))));
                    side[i] = (t1.X * t2.Y - t1.Y * t2.X) >= 0 ? 1 : -1;
                }
            }

            double LenOf(Seg s) => s.IsArc ? s.R * Math.Abs(s.Sweep) : s.A.GetDistanceTo(s.B);

            double CapAt(int far)
            {
                if (closed) far = (far + n) % n;
                else if (far < 1 || far > n - 2) return 0.9;   // open-line end has no competition
                return delta[far] >= minDefl ? 0.45 : 0.9;
            }

            Rebuild();

            // Rotate a closed ring so it starts at the straightest vertex; swallow windows then never straddle the list seam
            if (closed)
            {
                int flat = 0; double best = double.MaxValue;
                for (int i = 0; i < n; i++) if (delta[i] < best) { best = delta[i]; flat = i; }
                if (flat > 0)
                {
                    var rot = new List<(Point2d pt, double bulge)>(n);
                    for (int i = 0; i < n; i++) rot.Add(pts[(flat + i) % n]);
                    pts.Clear(); pts.AddRange(rot);
                    Rebuild();
                }
            }

            for (int guard = 0; guard < 32; guard++)
            {
                bool changed = false;
                for (int i = 1; i < n - 1 && !changed; i++)
                {
                    if (delta[i] < minDefl || delta[i] > Math.PI - 0.01) continue;
                    // Corners that fit the full radius are never swallowed
                    if (TrySolveJunction(segs[i - 1], segs[i], pts[i].pt, r, CapAt(i - 1), CapAt(i + 1), out _))
                        continue;

                    // Try candidate windows from the shortest swallowed edge total to the longest
                    var wins = new List<(int a, int b, double len)>();
                    foreach (var (a, b) in new[] { (i, i + 1), (i - 1, i), (i, i + 2), (i - 1, i + 1), (i - 2, i) })
                    {
                        if (a < 1 || b > n - 1) continue;
                        bool ok = true; double swLen = 0;
                        for (int k = a; k <= b && ok; k++)
                        {
                            if (delta[k] > Math.PI - 0.01) ok = false;                       // fold-back corners are not touched
                            else if (delta[k] >= noiseDefl && side[k] != side[i]) ok = false; // S-shaped reverse bend has no single-arc solution
                        }
                        for (int k = a; k < b && ok; k++)
                        { swLen += LenOf(segs[k]); if (swLen > 1.6 * r) ok = false; }
                        if (ok) wins.Add((a, b, swLen));
                    }
                    wins.Sort((x, y) => x.len.CompareTo(y.len));

                    foreach (var (a, b, _) in wins)
                    {
                        Seg so1 = segs[a - 1], so2 = segs[b];
                        if (LenOf(so1) < Tol || LenOf(so2) < Tol) continue;
                        Vector2d te = TangentAt(so1, so1.B), ts = TangentAt(so2, so2.A);
                        int sd = (te.X * ts.Y - te.Y * ts.X) >= 0 ? 1 : -1;
                        if (sd != side[i]) continue;
                        Point2d vref = pts[a].pt + (pts[b].pt - pts[a].pt) * 0.5;
                        if (!TrySolveJunctionAcross(so1, so2, vref, sd, r, CapAt(a - 1), CapAt(b + 1), out var jj))
                            continue;

                        // Reject ghost solutions: swallowed vertices must all lie outside the arc within R of it
                        Point2d C = jj.Tp1 + TangentAt(so1, jj.Tp1).RotateBy(sd * Math.PI / 2) * r;
                        bool sane = true;
                        for (int k = a; k <= b && sane; k++)
                        {
                            double dv = pts[k].pt.GetDistanceTo(C);
                            if (dv < r - 0.05 || dv > 2 * r) sane = false;
                        }
                        if (!sane) continue;

                        double upB = so1.IsArc ? BulgeOf(so1, pts[a - 1].pt, jj.Tp1) : 0;
                        double dnB = so2.IsArc ? BulgeOf(so2, jj.Tp2, pts[(b + 1) % n].pt) : 0;
                        pts[a - 1] = (pts[a - 1].pt, upB);
                        pts.RemoveRange(a, b - a + 1);
                        pts.Insert(a, (jj.Tp1, jj.Bulge));
                        pts.Insert(a + 1, (jj.Tp2, dnB));
                        stats.Spanned++; stats.Rounded++;
                        changed = true;
                        break;
                    }
                }
                if (!changed) break;
                Rebuild();
            }
        }

        // ============================================================
        //  Refillet: for a hand-edited terrace boundary, strip the old fillet arcs and re-fillet with a new radius.
        //  Criterion: arcs with radius <= unroundRMax are old fillets and are restored to sharp corners (same tangent-intersection
        //  formula as UnSmooth, depending only on the arc's own end tangents; neighbours untouched); larger arcs are design arcs
        //  (channel bends, extent-line arcs) and are kept as-is. Then the full RoundSharpCorners pipeline re-fillets.
        //  Always returns a new Polyline; the input is not modified. unrounded reports the number of old fillets removed.
        // ============================================================
        public static Polyline Refillet(Polyline src, double r, double minDeflDeg,
            double unroundRMax, RoundStats stats, out int unrounded)
        {
            unrounded = 0;
            bool closed = src.Closed;
            var pts = new List<(Point2d pt, double bulge)>();
            for (int i = 0; i < src.NumberOfVertices; i++)
                pts.Add((src.GetPoint2dAt(i), src.GetBulgeAt(i)));
            NormalizeClosure(pts, ref closed);
            int n = pts.Count;
            double maxBulge = Math.Tan(150.0 / 4 * Math.PI / 180.0);   // arcs over 150 deg are not stripped

            bool SmallArc(int s)
            {
                if (!closed && s >= n - 1) return false;
                double b = pts[s % n].bulge;
                if (Math.Abs(b) < Tol || Math.Abs(b) > maxBulge) return false;
                double ch = pts[s % n].pt.GetDistanceTo(pts[(s + 1) % n].pt);
                if (ch < Tol) return false;
                return ch * (1 + b * b) / (4 * Math.Abs(b)) <= unroundRMax;
            }

            var outPts = new List<(Point2d pt, double bulge)>(n);
            var absorbed = new bool[n];
            int segCount = closed ? n : n - 1;
            // Closed-ring wrap-around: when the last segment is a small arc it absorbs vertex 0, which must be marked before the loop
            if (closed && SmallArc(n - 1) && !SmallArc(0)) absorbed[0] = true;
            for (int k = 0; k < n; k++)
            {
                if (absorbed[k]) continue;
                // When two adjacent segments are both small arcs, strip only the first (the second counts as a kept arc), avoiding chain absorption
                if (k < segCount && SmallArc(k) && !SmallArc((k + 1) % n))
                {
                    Point2d p1 = pts[k].pt, p2 = pts[(k + 1) % n].pt;
                    Vector2d c = p2 - p1;
                    double bo = pts[k].bulge;
                    double half = 2 * Math.Atan(bo);
                    Vector2d u = c.GetNormal().RotateBy(-half);
                    double t = c.Length / (2 * Math.Cos(half));
                    outPts.Add((p1 + u * t, pts[(k + 1) % n].bulge));   // the sharp corner takes over the outgoing edge of the old arc's end point
                    if (closed || k + 1 < n) absorbed[(k + 1) % n] = true;
                    unrounded++;
                }
                else outPts.Add((pts[k].pt, pts[k].bulge));
            }

            var skeleton = new Polyline(outPts.Count);
            for (int i = 0; i < outPts.Count; i++)
                skeleton.AddVertexAt(i, outPts[i].pt, outPts[i].bulge, 0, 0);
            skeleton.Closed = closed;
            skeleton.SetPropertiesFrom(src);
            skeleton.Normal = src.Normal;
            skeleton.Elevation = src.Elevation;

            Polyline result = RoundSharpCorners(skeleton, r, minDeflDeg, stats);
            skeleton.Dispose();
            return result;
        }

        // ---- Joint solver: transition arc of radius rr tangent to both the end of seg1 and the start of seg2 ----
        struct Junction
        {
            public Point2d Tp1, Tp2;   // tangent point on seg1, tangent point on seg2
            public double Bulge;       // transition arc bulge
        }

        static bool TrySolveJunction(Seg s1, Seg s2, Point2d v, double rr, out Junction j)
            => TrySolveJunction(s1, s2, v, rr, 0.45, 0.45, out j);

        // cap1/cap2: upper bound on the share that may be consumed from the tail of seg1 / head of seg2.
        // Default 0.45 (both ends may carry a fillet, each yields half with margin); the caller may relax it when
        // the segment's far end has no competing fillet (RoundSharpCorners uses 0.9).
        static bool TrySolveJunction(Seg s1, Seg s2, Point2d v, double rr, double cap1, double cap2, out Junction j)
        {
            Vector2d t1 = TangentAt(s1, v), t2 = TangentAt(s2, v);
            int side = (t1.X * t2.Y - t1.Y * t2.X) >= 0 ? 1 : -1;   // turning side: +1 left turn
            return SolveJunctionCore(s1, s2, v, side, rr, cap1, cap2, out j);
        }

        // Spanned-corner variant: s1 and s2 do not share a vertex (swallowed short edges lie between); the turning side is taken from
        // s1's end tangent and s2's start tangent; vref (midpoint of the swallowed zone) only picks the nearest of multiple solutions.
        static bool TrySolveJunctionAcross(Seg s1, Seg s2, Point2d vref, int side,
            double rr, double cap1, double cap2, out Junction j)
            => SolveJunctionCore(s1, s2, vref, side, rr, cap1, cap2, out j);

        static bool SolveJunctionCore(Seg s1, Seg s2, Point2d v, int side,
            double rr, double cap1, double cap2, out Junction j)
        {
            j = default;

            // Locus of the transition arc centre: line -> shifted rr toward the turning side; arc -> concentric circle R - side*W*rr
            if (!LocusOf(s1, side, rr, out bool c1, out Point2d p1, out Vector2d d1, out Point2d o1, out double r1)) return false;
            if (!LocusOf(s2, side, rr, out bool c2, out Point2d p2, out Vector2d d2, out Point2d o2, out double r2)) return false;

            var cands = new List<Point2d>();
            if (!c1 && !c2) IntersectLL(p1, d1, p2, d2, cands);
            else if (c1 && !c2) IntersectLC(p2, d2, o1, r1, cands);
            else if (!c1 && c2) IntersectLC(p1, d1, o2, r2, cands);
            else IntersectCC(o1, r1, o2, r2, cands);
            if (cands.Count == 0) return false;

            cands.Sort((a, b) => a.GetDistanceTo(v).CompareTo(b.GetDistanceTo(v)));
            foreach (Point2d C in cands)
            {
                Point2d tp1 = FootOn(s1, C), tp2 = FootOn(s2, C);
                double u1 = ParamOf(s1, tp1), u2 = ParamOf(s2, tp2);
                if (u1 < 1 - cap1 || u1 > 1 - 1e-9) continue;   // seg1 tail consumption <= cap1 and tangent point inside the segment
                if (u2 > cap2 || u2 < 1e-9) continue;           // seg2 head consumption <= cap2

                double sweep = SweepBetween(tp1 - C, tp2 - C, side);
                if (sweep > Math.PI + 0.01) continue;        // the transition arc must not loop the long way round

                j = new Junction { Tp1 = tp1, Tp2 = tp2, Bulge = side * Math.Tan(sweep / 4) };
                return true;
            }
            return false;
        }

        static bool LocusOf(Seg s, int side, double rr,
            out bool isCircle, out Point2d lp, out Vector2d ld, out Point2d lo, out double lr)
        {
            isCircle = s.IsArc; lp = default; ld = default; lo = default; lr = 0;
            if (!s.IsArc)
            {
                ld = (s.B - s.A).GetNormal();
                lp = s.A + ld.RotateBy(Math.PI / 2) * (side * rr);   // shift toward the turning side
                return true;
            }
            lo = s.O;
            lr = s.R - side * s.W * rr;   // turning toward the centre shrinks the radius, away from it grows it
            return lr > Tol;
        }

        // ---- Segment geometry (line or arc, decided by bulge) ----
        struct Seg
        {
            public bool IsArc;
            public Point2d A, B;    // start / end (in travel direction)
            public Point2d O;       // centre
            public double R;        // radius
            public int W;           // travel direction: +1 counter-clockwise / -1 clockwise
            public double Sweep;    // sweep angle (signed = 4*atan(bulge))
        }

        static Seg BuildSeg(Point2d a, Point2d b, double bulge)
        {
            var s = new Seg { A = a, B = b };
            Vector2d c = b - a;
            if (Math.Abs(bulge) < Tol || c.Length < Tol) return s;   // straight
            double d = c.Length * (1 + bulge * bulge) / (4 * bulge); // signed distance of the centre
            s.IsArc = true;
            s.O = a + c.GetNormal().RotateBy(Math.PI / 2 - 2 * Math.Atan(bulge)) * d;
            s.R = Math.Abs(d);
            s.W = Math.Sign(bulge);
            s.Sweep = 4 * Math.Atan(bulge);
            return s;
        }

        // Unit tangent at point P in the travel direction
        static Vector2d TangentAt(Seg s, Point2d p)
            => s.IsArc ? (p - s.O).GetNormal().RotateBy(s.W * Math.PI / 2)
                       : (s.B - s.A).GetNormal();

        // Travel parameter 0..1 of a point on the segment (out of range returns a value outside [0,1] for validation to reject)
        static double ParamOf(Seg s, Point2d p)
        {
            if (!s.IsArc)
            {
                Vector2d ab = s.B - s.A;
                return (p - s.A).DotProduct(ab) / (ab.Length * ab.Length);
            }
            double full = Math.Abs(s.Sweep);
            if (full < Tol) return 0;
            return SweepBetween(s.A - s.O, p - s.O, s.W) / full;
        }

        // Angle swept from from to to along dir (+1 ccw / -1 cw), [0, 2pi)
        static double SweepBetween(Vector2d from, Vector2d to, int dir)
        {
            double a = (Math.Atan2(to.Y, to.X) - Math.Atan2(from.Y, from.X)) * dir;
            while (a < 0) a += 2 * Math.PI;
            while (a >= 2 * Math.PI) a -= 2 * Math.PI;
            return a;
        }

        // Bulge of the segment after trimming to new endpoints p->q
        static double BulgeOf(Seg s, Point2d p, Point2d q)
        {
            if (!s.IsArc) return 0;
            return s.W * Math.Tan(SweepBetween(p - s.O, q - s.O, s.W) / 4);
        }

        // Tangent foot of centre C on the segment
        static Point2d FootOn(Seg s, Point2d c)
        {
            if (s.IsArc)
            {
                Vector2d w = c - s.O;
                if (w.Length < Tol) return s.A;   // degenerate: left for the parameter validation to reject
                return s.O + w.GetNormal() * s.R;
            }
            Vector2d d = (s.B - s.A).GetNormal();
            return s.A + d * (c - s.A).DotProduct(d);
        }

        // ---- Locus intersections ----
        static void IntersectLL(Point2d p1, Vector2d d1, Point2d p2, Vector2d d2, List<Point2d> outPts)
        {
            double den = d1.X * d2.Y - d1.Y * d2.X;
            if (Math.Abs(den) < 1e-12) return;
            Vector2d w = p2 - p1;
            double t = (w.X * d2.Y - w.Y * d2.X) / den;
            outPts.Add(p1 + d1 * t);
        }

        static void IntersectLC(Point2d lp, Vector2d ld, Point2d o, double r, List<Point2d> outPts)
        {
            Point2d foot = lp + ld * (o - lp).DotProduct(ld);
            double h2 = r * r - foot.GetDistanceTo(o) * foot.GetDistanceTo(o);
            if (h2 < 0) return;
            double h = Math.Sqrt(h2);
            outPts.Add(foot + ld * h);
            if (h > Tol) outPts.Add(foot + ld * (-h));
        }

        static void IntersectCC(Point2d o1, double r1, Point2d o2, double r2, List<Point2d> outPts)
        {
            Vector2d w = o2 - o1;
            double d = w.Length;
            if (d < Tol || d > r1 + r2 + Tol || d < Math.Abs(r1 - r2) - Tol) return;
            double a = (r1 * r1 - r2 * r2 + d * d) / (2 * d);
            double h2 = Math.Max(0, r1 * r1 - a * a);
            double h = Math.Sqrt(h2);
            Vector2d u = w.GetNormal();
            Point2d m = o1 + u * a;
            Vector2d nrm = u.RotateBy(Math.PI / 2);
            outPts.Add(m + nrm * h);
            if (h > Tol) outPts.Add(m + nrm * (-h));
        }

        // ---- Closure normalisation ----
        const double SnapTol = 1e-6;   // distance below which first and last points count as coincident

        static void NormalizeClosure(List<(Point2d pt, double bulge)> pts, ref bool closed)
        {
            if (!closed && pts.Count >= 4 &&
                pts[0].pt.GetDistanceTo(pts[pts.Count - 1].pt) < SnapTol)
            {
                pts.RemoveAt(pts.Count - 1);   // fake open: drop the duplicate last point, make closed
                closed = true;
            }
            else if (closed && pts.Count >= 2 &&
                     pts[0].pt.GetDistanceTo(pts[pts.Count - 1].pt) < SnapTol)
            {
                pts.RemoveAt(pts.Count - 1);   // truly closed but with a duplicate last point: zero-length closing segment, drop it
            }
        }

        /// <summary>Snap-closed fake-open line: Closed=false but first and last points coincide.</summary>
        public static bool IsSnapClosed(Polyline pl)
        {
            int nv = pl.NumberOfVertices;
            if (pl.Closed || nv < 4) return false;
            return pl.GetPoint2dAt(0).GetDistanceTo(pl.GetPoint2dAt(nv - 1)) < SnapTol;
        }

        static Polyline NormalizeClosedCopy(Polyline src)
        {
            var pts = new List<(Point2d pt, double bulge)>();
            for (int i = 0; i < src.NumberOfVertices; i++)
                pts.Add((src.GetPoint2dAt(i), src.GetBulgeAt(i)));
            bool closed = src.Closed;
            NormalizeClosure(pts, ref closed);

            var res = new Polyline(pts.Count);
            for (int i = 0; i < pts.Count; i++)
                res.AddVertexAt(i, pts[i].pt, pts[i].bulge, 0, 0);
            res.Closed = closed;
            res.SetPropertiesFrom(src);
            res.Normal = src.Normal;
            res.Elevation = src.Elevation;
            return res;
        }

        // ---- Small helpers ----
        /// <summary>Fallback: offset each line of the previous ring by one increment and gather them into the new ring.</summary>
        static DBObjectCollection StepFromPrevious(List<Polyline> prev, double step)
        {
            var acc = new DBObjectCollection();
            if (prev == null) return acc;
            foreach (Polyline p in prev)
                foreach (DBObject o in TryOffset(p, step))
                    acc.Add(o);
            return acc;
        }

        static void DisposeAll2(List<Polyline> list)
        {
            if (list == null) return;
            foreach (Polyline p in list) if (p != null && !p.IsDisposed) p.Dispose();
        }

        static DBObjectCollection TryOffset(Curve src, double d)
        {
            try { return src.GetOffsetCurves(d); }
            catch { return new DBObjectCollection(); }
        }

        static double TotalArea(DBObjectCollection col)
        {
            double a = 0;
            foreach (DBObject o in col)
                if (o is Polyline pl) { try { a += Math.Abs(pl.Area); } catch { } }
            return a;
        }

        static void DisposeAll(DBObjectCollection col)
        {
            foreach (DBObject o in col) o.Dispose();
        }

        // ============================================================
        //  ChainSegments (chaining loose segments): a pile of loose straight segments -> ordered open-chain / closed-ring polylines
        //  Endpoints are clustered into nodes by tolerance; degree-2 nodes pass the chain through, degree-1 (free end) and
        //  degree>=3 (junction) nodes break it. Where the two endpoints of a joint do not coincide, the intersection of the two
        //  extended lines is preferred (fixing both under- and over-shoot), falling back to the endpoint midpoint when nearly parallel.
        //  Outputs straight-only polylines; rounding is left to Smooth.
        // ============================================================
        public sealed class SegChain
        {
            public Polyline Pl;           // chained polyline (straight only, not rounded)
            public List<int> SegIndices;  // indices of the input segments used (in chain order)
            public int FittedJoints;      // joints whose endpoints did not coincide and were continued via intersection / midpoint
            public bool Closed;
        }

        public static List<SegChain> ChainSegments(List<(Point2d A, Point2d B)> segs, double tol,
            out int junctionNodes)
        {
            junctionNodes = 0;
            var chains = new List<SegChain>();
            int ns = segs.Count;
            if (ns == 0) return chains;
            if (tol < Tol) tol = Tol;

            // 1) Cluster endpoints into nodes (representative = mean of members; few segments, O(n²) is fine)
            var nodePts = new List<Point2d>();
            var nodeCnt = new List<int>();
            int[,] nodeOf = new int[ns, 2];
            for (int s = 0; s < ns; s++)
                for (int e = 0; e < 2; e++)
                {
                    Point2d p = e == 0 ? segs[s].A : segs[s].B;
                    int hit = -1;
                    for (int k = 0; k < nodePts.Count; k++)
                        if (nodePts[k].GetDistanceTo(p) <= tol) { hit = k; break; }
                    if (hit < 0) { nodePts.Add(p); nodeCnt.Add(1); hit = nodePts.Count - 1; }
                    else
                    {
                        int c = nodeCnt[hit];
                        nodePts[hit] = new Point2d((nodePts[hit].X * c + p.X) / (c + 1),
                                                   (nodePts[hit].Y * c + p.Y) / (c + 1));
                        nodeCnt[hit] = c + 1;
                    }
                    nodeOf[s, e] = hit;
                }

            // 2) Adjacency (zero-length / self-loop segments dropped)
            int nn = nodePts.Count;
            var adj = new List<(int seg, int other)>[nn];
            for (int k = 0; k < nn; k++) adj[k] = new List<(int, int)>();
            for (int s = 0; s < ns; s++)
            {
                int a = nodeOf[s, 0], b = nodeOf[s, 1];
                if (a == b) continue;
                adj[a].Add((s, b));
                adj[b].Add((s, a));
            }
            for (int k = 0; k < nn; k++) if (adj[k].Count >= 3) junctionNodes++;

            var used = new bool[ns];

            // Travel endpoints of a segment: rev=false walks A->B
            Point2d Head(int s, bool rev) => rev ? segs[s].B : segs[s].A;
            Point2d Tail(int s, bool rev) => rev ? segs[s].A : segs[s].B;

            // Joint point: travel end e1 of the previous segment and travel start p2 of the next
            (Point2d pt, bool fitted) Joint(int s1, bool rev1, int s2, bool rev2, int node)
            {
                Point2d e1 = Tail(s1, rev1), p2 = Head(s2, rev2);
                if (e1.GetDistanceTo(p2) <= 1e-6) return (e1, false);
                Point2d a1 = Head(s1, rev1);
                Vector2d d1 = e1 - a1, d2 = Tail(s2, rev2) - p2;
                Point2d mid = new Point2d((e1.X + p2.X) / 2, (e1.Y + p2.Y) / 2);
                double cross = d1.X * d2.Y - d1.Y * d2.X;
                if (Math.Abs(cross) < Tol * Math.Max(d1.Length * d2.Length, 1.0)) return (mid, true);
                double t = ((p2.X - a1.X) * d2.Y - (p2.Y - a1.Y) * d2.X) / cross;
                var ix = new Point2d(a1.X + d1.X * t, a1.Y + d1.Y * t);
                // When nearly parallel the intersection flies away -- fall back to the midpoint if too far from the node
                return ix.GetDistanceTo(nodePts[node]) <= tol * 3 ? (ix, true) : (mid, true);
            }

            SegChain Walk(int startNode, int firstSeg)
            {
                var order = new List<(int seg, bool rev)>();
                int cur = startNode, s = firstSeg;
                bool closed = false;
                while (true)
                {
                    used[s] = true;
                    bool rev = nodeOf[s, 0] != cur;
                    order.Add((s, rev));
                    int nxt = rev ? nodeOf[s, 0] : nodeOf[s, 1];
                    if (nxt == startNode) { closed = order.Count > 1; break; }
                    if (adj[nxt].Count != 2) break;
                    int s2 = -1;
                    foreach (var (cand, _) in adj[nxt])
                        if (cand != s && !used[cand]) { s2 = cand; break; }
                    if (s2 < 0) break;
                    cur = nxt; s = s2;
                }

                int fitted = 0;
                var verts = new List<Point2d>();
                var (s0, r0) = order[0];
                if (closed)
                {
                    var (sl, rl) = order[order.Count - 1];
                    var (jp, jf) = Joint(sl, rl, s0, r0, startNode);
                    verts.Add(jp);
                    if (jf) fitted++;
                }
                else verts.Add(Head(s0, r0));

                for (int i = 0; i + 1 < order.Count; i++)
                {
                    var (sa, ra) = order[i];
                    var (sb, rb) = order[i + 1];
                    int node = ra ? nodeOf[sa, 0] : nodeOf[sa, 1];
                    var (jp, jf) = Joint(sa, ra, sb, rb, node);
                    verts.Add(jp);
                    if (jf) fitted++;
                }
                var (se, re) = order[order.Count - 1];
                if (!closed) verts.Add(Tail(se, re));

                var pl = new Polyline(verts.Count);
                int vi = 0;
                foreach (var p in verts)
                {
                    if (vi > 0 && pl.GetPoint2dAt(vi - 1).GetDistanceTo(p) <= 1e-9) continue;
                    pl.AddVertexAt(vi++, p, 0, 0, 0);
                }
                pl.Closed = closed;

                var idxs = new List<int>(order.Count);
                foreach (var (seg, _) in order) idxs.Add(seg);
                return new SegChain { Pl = pl, SegIndices = idxs, FittedJoints = fitted, Closed = closed };
            }

            // 3) Scan open chains starting from nodes with degree != 2; the unused segments left over must be pure closed rings
            for (int k = 0; k < nn; k++)
            {
                if (adj[k].Count == 2) continue;
                foreach (var (seg, _) in adj[k])
                    if (!used[seg]) chains.Add(Walk(k, seg));
            }
            for (int s = 0; s < ns; s++)
            {
                if (used[s]) continue;
                if (nodeOf[s, 0] == nodeOf[s, 1]) continue;
                chains.Add(Walk(nodeOf[s, 0], s));
            }
            return chains;
        }

        /// <summary>Number of segments with non-zero bulge in a polyline.</summary>
        public static int CountArcs(Polyline pl)
        {
            int c = 0;
            for (int i = 0; i < pl.NumberOfVertices; i++)
                if (Math.Abs(pl.GetBulgeAt(i)) > Tol) c++;
            return c;
        }
    }
}
