#nullable disable   // Same as OffsetConeCore: compiled by both Civil3DFactory (nullable off) and WaterBox (nullable on)

using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory.Geometry
{
    /// <summary>
    /// Terrace-building algorithm core (pure DB/geometry, no interaction). Single source of truth:
    /// Civil3DFactory (node make_parcels) and products\waterbox (C3DF-MakeParcels/CT) both compile this file.
    /// Chain: extend centerline ends to the boundary -> offset both sides + square caps into a band -> union of bands ->
    /// (outer ring - island holes) - bands = terraces -> sharp-corner detection and rounding (OffsetConeCore.RoundSharpCorners).
    /// Input polylines are guaranteed by the caller: in-memory clones, Elevation=0, boundaries already Closed.
    /// </summary>
    public static class MakeParcelsCore
    {
        const double JoinTol = 1e-6;

        public sealed class ParcelResult
        {
            public List<Polyline> Parcels = new List<Polyline>();      // already rounded; caller appends them and sets the layer
            public List<Polyline> ChannelLoops = new List<Polyline>(); // closed channel-extent rings = extent - rounded terraces (outer ring + island rings, ready for HATCH)
            public double ChannelArea;                                 // channel area (Region truth)
            public List<string> Warnings = new List<string>();
            public int Extended;                                       // number of centerlines whose ends were extended
        }

        /// <summary>Process one outer ring: holes are its island holes, centerlines are the centerlines belonging to it.
        /// If halfWs is given, the width is taken per centerline (aligned with centerlines; null = halfW for all).
        /// Throws InvalidOperationException if any centerline fails to form a band (better no output than a wrong drawing).</summary>
        public static ParcelResult GenerateOne(Polyline outer, List<Polyline> holes,
            List<Polyline> centerlines, double halfW, double filletR, double minDeflDeg,
            IList<double> halfWs = null)
        {
            var result = new ParcelResult();
            var allBounds = new List<Polyline> { outer };
            allBounds.AddRange(holes);

            Region bandsAll = null, baseRegion = null, parcels = null;
            var bandLoops = new List<Polyline>();
            try
            {
                for (int i = 0; i < centerlines.Count; i++)
                {
                    Polyline cl = centerlines[i];
                    double w = (halfWs != null && i < halfWs.Count && halfWs[i] > 0) ? halfWs[i] : halfW;
                    int nv = cl.NumberOfVertices;
                    Vector2d dirS = cl.GetPoint2dAt(0) - cl.GetPoint2dAt(1);
                    Vector2d dirE = cl.GetPoint2dAt(nv - 1) - cl.GetPoint2dAt(nv - 2);
                    double extS = EndExtension(cl.GetPoint2dAt(0), dirS, allBounds, w);
                    double extE = EndExtension(cl.GetPoint2dAt(nv - 1), dirE, allBounds, w);
                    if (extS > 0 || extE > 0) result.Extended++;
                    Polyline work = Extend(cl, extS, extE);
                    Polyline band = BuildBand(work, w, out string err);
                    if (!ReferenceEquals(work, cl)) work.Dispose();
                    if (band == null)
                        throw new InvalidOperationException($"Centerline #{i + 1} failed to form a band: {err}");
                    bandLoops.Add(band);
                }

                if (bandLoops.Count == 0)
                    throw new InvalidOperationException("No centerline in this area formed a band.");

                for (int i = 0; i < bandLoops.Count; i++)
                {
                    try
                    {
                        if (bandsAll == null) bandsAll = ToRegion(bandLoops[i]);
                        else using (var r2 = ToRegion(bandLoops[i]))
                                bandsAll.BooleanOperation(BooleanOperationType.BoolUnite, r2);
                    }
                    catch (System.Exception ex)
                    { throw new InvalidOperationException($"Band #{i + 1} failed to form a region: {ex.Message}; {DumpPl(bandLoops[i])}"); }
                }

                try { baseRegion = ToRegion(outer); }
                catch (System.Exception ex)
                { throw new InvalidOperationException($"Outer ring failed to form a region: {ex.Message}"); }
                foreach (var h in holes)
                    using (var hr = ToRegion(h))
                        baseRegion.BooleanOperation(BooleanOperationType.BoolSubtract, hr);

                parcels = (Region)baseRegion.Clone();
                using (var bandsCopy = (Region)bandsAll.Clone())
                    parcels.BooleanOperation(BooleanOperationType.BoolSubtract, bandsCopy);

                int idx = 0;
                foreach (Polyline loop in RegionLoops(parcels))
                {
                    idx++;
                    var st = new OffsetConeCore.RoundStats();
                    Polyline rounded = OffsetConeCore.RoundSharpCorners(loop, filletR, minDeflDeg, st);
                    loop.Dispose();
                    result.Parcels.Add(rounded);
                    if (st.Spanned > 0)
                        result.Warnings.Add($"Terrace #{idx}: {st.Spanned} short edges cannot hold R{filletR:0.###}; the arc spans the short edge and swallows the adjacent corner (full radius)");
                    if (st.Downgraded > 0)
                        result.Warnings.Add($"Terrace #{idx}: {st.Downgraded} corners cannot hold R{filletR:0.###}; rounded with the largest local radius (min R{st.MinUsedR:0.#})");
                    if (st.CantFit > 0)
                        result.Warnings.Add($"Terrace #{idx}: {st.CantFit} corners cannot even hold R{filletR / 32:0.##}; sharp corner kept");
                }

                // Channel extent = extent - (rounded terraces): closed, hugging the fillets, for HATCH / area computation
                using (var chan = (Region)baseRegion.Clone())
                {
                    foreach (Polyline p in result.Parcels)
                        using (var pr = ToRegion(p))
                            chan.BooleanOperation(BooleanOperationType.BoolSubtract, pr);
                    result.ChannelArea = chan.Area;
                    result.ChannelLoops = RegionLoops(chan);
                    // Sliver filter: after the boolean turns ring arcs into chords, hairline crescents remain between the terrace edge
                    // and the true arc (metres long, millimetres wide, single-digit m² area; 7 observed on project B 2026-08-24) -- drop and report
                    for (int i = result.ChannelLoops.Count - 1; i >= 0; i--)
                    {
                        double a2 = 0;
                        try { a2 = Math.Abs(result.ChannelLoops[i].Area); } catch { }
                        if (a2 < 25.0)
                        {
                            result.Warnings.Add($"Dropped channel sliver ring (area {a2:0.0} m², crescent from ring-arc chording)");
                            result.ChannelLoops[i].Dispose();
                            result.ChannelLoops.RemoveAt(i);
                        }
                    }
                }
            }
            finally
            {
                bandsAll?.Dispose(); baseRegion?.Dispose(); parcels?.Dispose();
                foreach (var b in bandLoops) if (!b.IsDisposed) b.Dispose();
            }
            return result;
        }

        // ---- End extension amount: extend only if the end is within 2 x half width of any extent ring (edge-hugging ends punch through; dead ends deep inside stay) ----
        public static double EndExtension(Point2d end, List<Polyline> boundaries, double halfW)
            => EndExtension(end, default, boundaries, halfW);

        /// <summary>End extension amount. dir = outward vector of the centerline at that end (if given, computed exactly for the skew angle:
        /// at a skewed mouth the far corner of the band must extend an extra halfW*tan(skew), otherwise a small wedge / floating arc remains between
        /// the mouth and the ring -- observed at the mouths on project B 2026-08-24). Without dir, fall back to the old rule (distance + half width).</summary>
        public static double EndExtension(Point2d end, Vector2d dir, List<Polyline> boundaries, double halfW)
        {
            double best = double.MaxValue;
            Polyline hit = null;
            Point3d q = default;
            var p3 = new Point3d(end.X, end.Y, 0);
            foreach (var b in boundaries)
            {
                try
                {
                    var c = b.GetClosestPointTo(p3, false);
                    double d = c.DistanceTo(p3);
                    if (d < best) { best = d; hit = b; q = c; }
                }
                catch { }
            }
            if (best > halfW * 4) return 0;                    // interior end (joins another channel), no extension
            if (hit == null || dir.Length < 1e-9)
                return best + halfW;                           // old-rule fallback
            try
            {
                var t3 = hit.GetFirstDerivative(q);            // local tangent of the ring
                var t2 = new Vector2d(t3.X, t3.Y);
                if (t2.Length < 1e-9) return best + halfW;
                t2 = t2.GetNormal();
                var u = dir.GetNormal();
                var n = new Vector2d(-t2.Y, t2.X);             // local normal of the ring
                double nu = Math.Abs(u.DotProduct(n));
                if (nu < 0.1) return best + halfW * 6;         // nearly parallel graze; give a large margin, gets clipped
                var p = new Vector2d(-u.Y, u.X);               // band-width direction
                double np = Math.Abs(p.DotProduct(n));
                return best / nu + halfW * np / nu + 2.0;      // along the line to the ring + skew compensation + margin
            }
            catch { return best + halfW; }
        }

        // ---- Extend ends along the chord of the last segment (the extension lies outside the extent line and is clipped by the boolean; chord direction suffices) ----
        public static Polyline Extend(Polyline src, double extS, double extE)
        {
            if (extS <= 0 && extE <= 0) return src;
            var pl = (Polyline)src.Clone();
            int n = pl.NumberOfVertices;
            if (extS > 0)
            {
                Vector2d d = (pl.GetPoint2dAt(0) - pl.GetPoint2dAt(1));
                if (d.Length > 1e-9)
                    pl.AddVertexAt(0, pl.GetPoint2dAt(0) + d.GetNormal() * extS, 0, 0, 0);
            }
            n = pl.NumberOfVertices;
            if (extE > 0)
            {
                Vector2d d = (pl.GetPoint2dAt(n - 1) - pl.GetPoint2dAt(n - 2));
                if (d.Length > 1e-9)
                    pl.AddVertexAt(n, pl.GetPoint2dAt(n - 1) + d.GetNormal() * extE, 0, 0, 0);
            }
            return pl;
        }

        // ---- Centerline -> closed band: offset half width on each side, square caps at both ends joined into one closed loop ----
        // Offsets are computed here (not GetOffsetCurves -- accore returns an empty set for open polylines with arcs, observed):
        // GY output is G1 tangent-continuous; straight segments translate, arcs stay concentric with a new radius, bulge unchanged, endpoints coincide analytically;
        // at small un-rounded kinks both side points are kept (micro chamfer), invisible to the boolean.
        public static Polyline BuildBand(Polyline cl, double halfW, out string err)
        {
            err = null;
            Polyline left = ManualOffset(cl, halfW, out string dl), right = ManualOffset(cl, -halfW, out string dr);
            if (left == null || right == null)
            {
                left?.Dispose(); right?.Dispose();
                err = $"Offset failed (left:{dl ?? "ok"}; right:{dr ?? "ok"}; vertices {cl.NumberOfVertices})";
                return null;
            }
            var band = new Polyline(left.NumberOfVertices + right.NumberOfVertices);
            int vi = 0;
            for (int i = 0; i < left.NumberOfVertices; i++)
            {
                double b = i < left.NumberOfVertices - 1 ? left.GetBulgeAt(i) : 0;   // last point joins the straight cap
                band.AddVertexAt(vi++, left.GetPoint2dAt(i), b, 0, 0);
            }
            for (int i = right.NumberOfVertices - 1; i >= 0; i--)
            {
                double b = i > 0 ? -right.GetBulgeAt(i - 1) : 0;                     // reversed segment: negate bulge; first point closes the cap
                band.AddVertexAt(vi++, right.GetPoint2dAt(i), b, 0, 0);
            }
            band.Closed = true;
            left.Dispose(); right.Dispose();
            if (Math.Abs(band.Area) < 1e-6) { band.Dispose(); err = "band area is zero"; return null; }
            return band;
        }

        // ---- Analytic offset: d>0 offsets to the left of the travel direction. Requires an open polyline; errors when an arc radius <= |d|.
        // Tangent joints (GY fillet output) coincide analytically; small un-rounded straight-straight kinks are mitred
        // (intersect the two offset lines) so the chamfer cannot create a tiny self-intersection inside the kink that Region rejects ----
        public static Polyline ManualOffset(Polyline src, double d, out string diag)
        {
            diag = null;
            int n = src.NumberOfVertices;
            if (n < 2) { diag = "too few vertices"; return null; }

            // Offset primitive per segment: endpoints, bulge, is-arc
            // Tiny-segment threshold at engineering scale: the direction of millimetre-size fragments is drawing noise; after offsetting they
            // create micro zigzags on the band that Region rejects (a 7 mm fragment failed in practice); skipped, and the joint mitre fills the gap
            double minSeg = Math.Max(0.01, Math.Abs(d) / 1000);
            var segs = new List<(Point2d q1, Point2d q2, double bulge, bool arc)>();
            for (int i = 0; i < n - 1; i++)
            {
                Point2d p1 = src.GetPoint2dAt(i), p2 = src.GetPoint2dAt(i + 1);
                double b = src.GetBulgeAt(i);
                Vector2d chord = p2 - p1;
                double ch = chord.Length;
                if (ch < minSeg) continue;                     // skip tiny segments
                var nl = new Vector2d(-chord.Y / ch, chord.X / ch);   // left normal of travel

                if (Math.Abs(b) < 1e-9)
                    segs.Add((p1 + nl * d, p2 + nl * d, 0, false));
                else
                {
                    double r = ch * (1 + b * b) / (4 * Math.Abs(b));
                    double rNew = r - Math.Sign(b) * d;        // left offset: CCW arc shrinks toward the centre, CW arc grows away
                    if (rNew < 1e-6) { diag = $"Segment {i + 1}: arc radius {r:0.##} cannot hold offset {Math.Abs(d):0.##}"; return null; }
                    var m = new Point2d((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
                    // Signed distance of the centre along the left normal at the chord midpoint. Sign verified with a standard quarter circle:
                    // the CCW arc p1=(1,0)->p2=(0,1) with b=tan22.5° must have its centre at (0,0) --
                    // positive bulge (CCW) has its centre on the travel-left side and its apex on the right; do not get it backwards again.
                    double t = (ch / 4) * (1 / b - b);
                    Point2d o = m + nl * t;
                    double k = rNew / r;
                    // concentric scaling, sweep unchanged -> bulge unchanged
                    segs.Add((o + (p1 - o) * k, o + (p2 - o) * k, b, true));
                }
            }
            if (segs.Count == 0) { diag = "no valid segments after offsetting"; return null; }

            // Joints: tangent -> shared point; straight-straight kink -> mitre; kink with arcs -> midpoint compromise (should not occur after GY)
            for (int i = 1; i < segs.Count; i++)
            {
                var a = segs[i - 1]; var c = segs[i];
                double gap = a.q2.GetDistanceTo(c.q1);
                if (gap <= 1e-6) { segs[i] = (a.q2, c.q2, c.bulge, c.arc); continue; }

                Point2d j;
                var mid = new Point2d((a.q2.X + c.q1.X) / 2, (a.q2.Y + c.q1.Y) / 2);
                if (!a.arc && !c.arc &&
                    TryIntersectLines(a.q1, a.q2, c.q1, c.q2, out j) &&
                    j.GetDistanceTo(mid) <= Math.Abs(d) * 10)
                { }
                else j = mid;
                segs[i - 1] = (a.q1, j, a.bulge, a.arc);
                segs[i] = (j, c.q2, c.bulge, c.arc);
            }

            // Both sides of each joint now coincide exactly: vertices = start of first segment + end of each segment; bulge belongs to the start vertex
            var pl = new Polyline(segs.Count + 1);
            pl.AddVertexAt(0, segs[0].q1, segs[0].bulge, 0, 0);
            for (int i = 0; i < segs.Count; i++)
                pl.AddVertexAt(i + 1, segs[i].q2, i + 1 < segs.Count ? segs[i + 1].bulge : 0, 0, 0);
            return pl;
        }

        static string DumpPl(Polyline pl)
        {
            var sb = new System.Text.StringBuilder($"closed={pl.Closed} nv={pl.NumberOfVertices}:");
            for (int i = 0; i < pl.NumberOfVertices; i++)
            {
                var p = pl.GetPoint2dAt(i);
                sb.Append($" [{i}]({p.X:0.###},{p.Y:0.###},b={pl.GetBulgeAt(i):0.####})");
            }
            return sb.ToString();
        }

        static bool TryIntersectLines(Point2d a1, Point2d a2, Point2d b1, Point2d b2, out Point2d j)
        {
            j = default;
            Vector2d d1 = a2 - a1, d2 = b2 - b1;
            double cross = d1.X * d2.Y - d1.Y * d2.X;
            if (Math.Abs(cross) < 1e-12 * Math.Max(1.0, d1.Length * d2.Length)) return false;
            double t = ((b1.X - a1.X) * d2.Y - (b1.Y - a1.Y) * d2.X) / cross;
            j = new Point2d(a1.X + d1.X * t, a1.Y + d1.Y * t);
            return true;
        }

        public static Region ToRegion(Polyline loop)
        {
            var col = new DBObjectCollection { loop };
            var regs = Region.CreateFromCurves(col);
            Region r = null;
            foreach (DBObject o in regs)
            {
                if (r == null && o is Region rg) r = rg;
                else o.Dispose();
            }
            if (r == null) throw new InvalidOperationException("Region creation failed (self-intersecting ring?)");
            return r;
        }

        // ---- Region -> closed polyline rings: explode recursively to collect edge curves, chain them by endpoints, convert arcs to bulges ----
        public static List<Polyline> RegionLoops(Region reg)
        {
            var curves = new List<Curve>();
            var loops = new List<Polyline>();
            CollectCurves(reg, curves, loops);

            var used = new bool[curves.Count];
            for (int s = 0; s < curves.Count; s++)
            {
                if (used[s]) continue;
                var order = new List<(Curve c, bool rev)>();
                used[s] = true;
                order.Add((curves[s], false));
                Point3d start = curves[s].StartPoint, cur = curves[s].EndPoint;
                int guard = 0;
                while (cur.DistanceTo(start) > JoinTol && guard++ < curves.Count + 2)
                {
                    int found = -1; bool rev = false;
                    for (int j = 0; j < curves.Count; j++)
                    {
                        if (used[j]) continue;
                        if (curves[j].StartPoint.DistanceTo(cur) <= JoinTol) { found = j; rev = false; break; }
                        if (curves[j].EndPoint.DistanceTo(cur) <= JoinTol) { found = j; rev = true; break; }
                    }
                    if (found < 0) break;
                    used[found] = true;
                    order.Add((curves[found], rev));
                    cur = rev ? curves[found].StartPoint : curves[found].EndPoint;
                }
                if (cur.DistanceTo(start) <= JoinTol)
                {
                    var pl = new Polyline(order.Count);
                    int vi = 0;
                    foreach (var (c, rev) in order)
                    {
                        Point3d p = rev ? c.EndPoint : c.StartPoint;
                        if (c is Arc a)
                        {
                            double sweep = a.EndAngle - a.StartAngle;
                            while (sweep <= 0) sweep += Math.PI * 2;
                            double bulge = Math.Tan(sweep / 4) * (rev ? -1 : 1);
                            pl.AddVertexAt(vi++, new Point2d(p.X, p.Y), bulge, 0, 0);
                        }
                        else if (c is Line)
                            pl.AddVertexAt(vi++, new Point2d(p.X, p.Y), 0, 0, 0);
                        else
                        {
                            // After an ACIS boolean, arc edges often explode as "elliptical arcs" -- the old code silently chorded anything that was not an Arc,
                            // shaving a full sagitta off long large-radius arcs (1~2 m drift observed on project B terrace edges hugging the ring).
                            // Dense straight segments are not an acceptable fallback either: the downstream rounding's collinear merge (0.5°) would merge
                            // the dense micro-kinked segments back into a long chord, undoing the fix. First fit a true bulge arc through three points;
                            // only if the fit fails (true spline) fall back to dense sampling.
                            double len = 0;
                            try { len = c.GetDistanceAtParameter(c.EndParam); } catch { }
                            bool asArc = false;
                            if (len > 1e-6)
                                try
                                {
                                    Point3d pS = rev ? c.EndPoint : c.StartPoint;
                                    Point3d pE = rev ? c.StartPoint : c.EndPoint;
                                    Point3d pM = c.GetPointAtDist(len * 0.5);
                                    double det = 2 * (pS.X * (pM.Y - pE.Y) + pM.X * (pE.Y - pS.Y) + pE.X * (pS.Y - pM.Y));
                                    if (Math.Abs(det) > 1e-9)
                                    {
                                        double s2 = pS.X * pS.X + pS.Y * pS.Y;
                                        double m2 = pM.X * pM.X + pM.Y * pM.Y;
                                        double e2 = pE.X * pE.X + pE.Y * pE.Y;
                                        double ux = (s2 * (pM.Y - pE.Y) + m2 * (pE.Y - pS.Y) + e2 * (pS.Y - pM.Y)) / det;
                                        double uy = (s2 * (pE.X - pM.X) + m2 * (pS.X - pE.X) + e2 * (pM.X - pS.X)) / det;
                                        double R = Math.Sqrt((pS.X - ux) * (pS.X - ux) + (pS.Y - uy) * (pS.Y - uy));
                                        // 5 cm tolerance: ACIS sometimes approximates arcs with splines, and 3 mm would reject true arcs
                                        // -> fall back to dense segments -> merged back into a long chord by the collinear merge (sagitta-level error).
                                        // A 5 cm "arc-fitting error" is far smaller than the metre-level sagitta of chording.
                                        bool fits = true;
                                        for (int k = 1; k <= 6 && fits; k++)
                                        {
                                            Point3d q2 = c.GetPointAtDist(len * k / 7.0);
                                            double rr = Math.Sqrt((q2.X - ux) * (q2.X - ux) + (q2.Y - uy) * (q2.Y - uy));
                                            if (Math.Abs(rr - R) > 0.05) fits = false;
                                        }
                                        if (fits && R > 0.01)
                                        {
                                            double a0 = Math.Atan2(pS.Y - uy, pS.X - ux);
                                            double a1 = Math.Atan2(pE.Y - uy, pE.X - ux);
                                            double cross = (pM.X - pS.X) * (pE.Y - pM.Y) - (pM.Y - pS.Y) * (pE.X - pM.X);
                                            double sweep = a1 - a0;
                                            if (cross > 0) { while (sweep <= 0) sweep += Math.PI * 2; }
                                            else { while (sweep >= 0) sweep -= Math.PI * 2; }
                                            pl.AddVertexAt(vi++, new Point2d(pS.X, pS.Y), Math.Tan(sweep / 4), 0, 0);
                                            asArc = true;
                                        }
                                    }
                                }
                                catch { }
                            if (!asArc)
                            {
                                // The whole edge is not a single circle (ACIS merges tangent arc+line+arc into one composite spline edge) --
                                // fit three-point arcs every ~5 m into a string of small arcs with a tiny bulge. No pure straight segments allowed:
                                // the downstream rounding's collinear merge (0.5°) would merge dense straight segments back into a long chord (only bulge!=0 is protected).
                                int nSeg = Math.Max(1, (int)Math.Ceiling(len / 5.0));
                                for (int k = 0; k < nSeg; k++)
                                {
                                    double d0 = len * (rev ? (nSeg - k) : k) / nSeg;
                                    double d1 = len * (rev ? (nSeg - k - 1) : k + 1) / nSeg;
                                    double dm = (d0 + d1) / 2;
                                    Point3d A2, B2, M2;
                                    try
                                    {
                                        A2 = c.GetPointAtDist(Math.Min(Math.Max(d0, 0), len * (1 - 1e-12)));
                                        B2 = c.GetPointAtDist(Math.Min(Math.Max(d1, 0), len * (1 - 1e-12)));
                                        M2 = c.GetPointAtDist(Math.Min(Math.Max(dm, 0), len * (1 - 1e-12)));
                                    }
                                    catch { break; }
                                    double chord = Math.Sqrt((B2.X - A2.X) * (B2.X - A2.X) + (B2.Y - A2.Y) * (B2.Y - A2.Y));
                                    double bulge2 = 0;
                                    if (chord > 1e-6)
                                    {
                                        // Signed distance h from the midpoint to the chord -> the exact form of the first-order arc approximation bulge = 2h/chord:
                                        // R=(c²/4+h²)/(2h), sweep=4*atan(2h/c)... using bulge=2h/c directly is exact for small arcs
                                        double hx = (B2.X - A2.X) / chord, hy = (B2.Y - A2.Y) / chord;
                                        double h = (M2.X - A2.X) * (-hy) + (M2.Y - A2.Y) * hx;   // signed distance of the midpoint left of the chord
                                        bulge2 = 2 * h / chord;   // tan(sweep/4)=2h/c, exact three-point arc form
                                    }
                                    pl.AddVertexAt(vi++, new Point2d(A2.X, A2.Y), bulge2, 0, 0);
                                }
                            }
                        }
                    }
                    pl.Closed = true;
                    if (Math.Abs(pl.Area) > 1e-6) loops.Add(pl); else pl.Dispose();
                }
            }
            foreach (var c in curves) c.Dispose();
            return loops;
        }

        static void CollectCurves(Region reg, List<Curve> curves, List<Polyline> loops)
        {
            var col = new DBObjectCollection();
            try { reg.Explode(col); } catch { return; }
            foreach (DBObject o in col)
            {
                if (o is Region sub) { CollectCurves(sub, curves, loops); sub.Dispose(); }
                else if (o is Circle ci)
                {
                    // Full circle as its own ring: two semicircles with bulge=1
                    var pl = new Polyline(2);
                    pl.AddVertexAt(0, new Point2d(ci.Center.X - ci.Radius, ci.Center.Y), 1, 0, 0);
                    pl.AddVertexAt(1, new Point2d(ci.Center.X + ci.Radius, ci.Center.Y), 1, 0, 0);
                    pl.Closed = true;
                    loops.Add(pl);
                    ci.Dispose();
                }
                else if (o is Curve cv && cv.StartPoint.DistanceTo(cv.EndPoint) > JoinTol) curves.Add(cv);
                else o.Dispose();
            }
        }
    }
}
