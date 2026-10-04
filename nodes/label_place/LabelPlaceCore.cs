using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Civil3DFactory
{
    // =====================================================================================
    //
    // =====================================================================================

    internal struct LPt
    {
        public double X, Y;
        public LPt(double x, double y) { X = x; Y = y; }
        public static LPt operator +(LPt a, LPt b) { return new LPt(a.X + b.X, a.Y + b.Y); }
        public static LPt operator -(LPt a, LPt b) { return new LPt(a.X - b.X, a.Y - b.Y); }
        public static LPt operator *(LPt a, double k) { return new LPt(a.X * k, a.Y * k); }
        public double Len { get { return Math.Sqrt(X * X + Y * Y); } }
    }

    internal struct LBox
    {
        public double X0, Y0, X1, Y1;
        public LBox(double x0, double y0, double x1, double y1) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
        public double W { get { return X1 - X0; } }
        public double H { get { return Y1 - Y0; } }
        public LPt Center { get { return new LPt((X0 + X1) / 2, (Y0 + Y1) / 2); } }
        public JsonArray ToJson(double k = 1.0)
        { return new JsonArray(Math.Round(X0 * k, 3), Math.Round(Y0 * k, 3), Math.Round(X1 * k, 3), Math.Round(Y1 * k, 3)); }
    }

    internal struct LSeg
    {
        public LPt A, B;
        public LSeg(LPt a, LPt b) { A = a; B = b; }
    }

    internal static class LabelGeo
    {
        public static bool Overlap(LBox a, LBox b, double tol = 0.2)
        {
            return a.X0 < b.X1 - tol && b.X0 < a.X1 - tol && a.Y0 < b.Y1 - tol && b.Y0 < a.Y1 - tol;
        }

        public static bool SegBox(LPt p, LPt q, LBox b)
        {
            double dx = q.X - p.X, dy = q.Y - p.Y, t0 = 0.0, t1 = 1.0;
            double[] pp = { -dx, dx, -dy, dy };
            double[] qq = { p.X - b.X0, b.X1 - p.X, p.Y - b.Y0, b.Y1 - p.Y };
            for (int i = 0; i < 4; i++)
            {
                if (Math.Abs(pp[i]) < 1e-12) { if (qq[i] < 0) return false; }
                else
                {
                    double t = qq[i] / pp[i];
                    if (pp[i] < 0) t0 = Math.Max(t0, t); else t1 = Math.Min(t1, t);
                    if (t0 > t1) return false;
                }
            }
            return true;
        }

        static double Cross(LPt o, LPt u, LPt v) { return (u.X - o.X) * (v.Y - o.Y) - (u.Y - o.Y) * (v.X - o.X); }

        public static bool SegSeg(LPt p, LPt q, LPt a, LPt b)
        {
            double d1 = Cross(a, b, p), d2 = Cross(a, b, q), d3 = Cross(p, q, a), d4 = Cross(p, q, b);
            return d1 * d2 < 0 && d3 * d4 < 0;
        }

        public static double? RayHit(LPt P, LPt n, LPt a, LPt b)
        {
            double ex = b.X - a.X, ey = b.Y - a.Y, den = n.X * ey - n.Y * ex;
            if (Math.Abs(den) < 1e-9) return null;
            double t = ((a.X - P.X) * ey - (a.Y - P.Y) * ex) / den;
            double u = ((a.X - P.X) * n.Y - (a.Y - P.Y) * n.X) / den;
            return (u >= 0 && u <= 1) ? (double?)t : null;
        }

        public static bool InPoly(LPt p, List<LPt> poly)
        {
            bool ins = false; int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                LPt a = poly[i], b = poly[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) ins = !ins;
            }
            return ins;
        }

        public static int CountCross(LPt p, LPt q, List<LSeg> segs)
        {
            int n = 0;
            foreach (var s in segs) if (SegSeg(p, q, s.A, s.B)) n++;
            return n;
        }

        public static int CountSegsInBox(LBox b, List<LSeg> segs)
        {
            int n = 0;
            foreach (var s in segs) if (SegBox(s.A, s.B, b)) n++;
            return n;
        }

        static readonly double[] AsciiW = {
            0.438, 0.558, 0.558, 0.725, 0.558, 0.342, 0.558, 0.558, 0.308, 0.308, 0.558, 0.558, 0.267, 0.558, 0.225, 0.558,
            0.558, 0.392, 0.566, 0.562, 0.642, 0.562, 0.568, 0.558, 0.558, 0.570, 0.225, 0.267, 0.558, 0.558, 0.558, 0.558,
            0.558, 0.725, 0.642, 0.562, 0.646, 0.558, 0.558, 0.646, 0.642, 0.225, 0.478, 0.646, 0.558, 0.725, 0.642, 0.642,
            0.642, 0.725, 0.642, 0.648, 0.642, 0.642, 0.725, 0.892, 0.725, 0.725, 0.642, 0.308, 0.558, 0.308, 0.558, 0.917,
            0.558, 0.563, 0.561, 0.478, 0.561, 0.561, 0.475, 0.564, 0.561, 0.225, 0.395, 0.558, 0.310, 0.728, 0.561, 0.558,
            0.561, 0.561, 0.475, 0.565, 0.475, 0.561, 0.558, 0.725, 0.558, 0.558, 0.558, 0.558, 0.558, 0.558, 0.558 };
        const string FullWidthPunct = "\uff08\uff09\uff1a\uff0c\u3002\uff1b\uff01\uff1f\u3001\u3010\u3011\u300a\u300b\u201c\u201d\u2018\u2019\u00b7";
        public const double SideBearing = 0.15;

        public static double GlyphW(char c)
        {
            if (c >= 32 && c <= 126) return AsciiW[c - 32];
            if (c == '²') return 0.480;
            if (c == '³') return 0.392;
            if (FullWidthPunct.IndexOf(c) >= 0) return 0.67;
            return c > 255 ? 0.716 : 0.558;
        }

        /// <summary>
        /// </summary>
        public static double TextWidth(string s, double h, double wf = 0.8)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            bool underline = false;
            if (s.IndexOf("%%u", StringComparison.OrdinalIgnoreCase) >= 0) { underline = true; s = s.Replace("%%u", "").Replace("%%U", ""); }
            double w = 0;
            foreach (char c in s) w += GlyphW(c);
            if (!underline && w > SideBearing) w -= SideBearing;
            return w * h * wf;
        }

        /// <summary>
        /// </summary>
        public static List<LPt> ArcPoints(LPt p0, LPt p1, double bulge, double maxStepDeg = 10)
        {
            var r = new List<LPt>();
            double dx = p1.X - p0.X, dy = p1.Y - p0.Y, c = Math.Sqrt(dx * dx + dy * dy);
            if (c < 1e-9 || Math.Abs(bulge) < 1e-9) return r;
            double theta = 4 * Math.Atan(bulge);
            double dist = c * (1 - bulge * bulge) / (4 * bulge);
            double mx = (p0.X + p1.X) / 2, my = (p0.Y + p1.Y) / 2, nx = -dy / c, ny = dx / c;
            double cx = mx + nx * dist, cy = my + ny * dist;
            double rad = Math.Sqrt((p0.X - cx) * (p0.X - cx) + (p0.Y - cy) * (p0.Y - cy));
            double a0 = Math.Atan2(p0.Y - cy, p0.X - cx);
            int k = Math.Max(2, (int)Math.Ceiling(Math.Abs(theta) / (maxStepDeg * Math.PI / 180)));
            for (int j = 1; j < k; j++)
            {
                double a = a0 + theta * j / k;
                r.Add(new LPt(cx + rad * Math.Cos(a), cy + rad * Math.Sin(a)));
            }
            return r;
        }

        /// <summary>
        /// </summary>
        public static LBox ShrinkMTextBox(LBox b, int nChars)
        {
            int n = Math.Max(1, nChars);
            if (b.H > 12 || b.W > n * 8)
            {
                double ch = Math.Min(b.H, 6.0);
                return new LBox(b.X0, b.Y1 - ch, Math.Min(b.X1, b.X0 + n * ch), b.Y1);
            }
            return b;
        }

        public static bool Inside(LBox frame, LPt p, double margin)
        {
            return p.X >= frame.X0 + margin && p.X <= frame.X1 - margin && p.Y >= frame.Y0 + margin && p.Y <= frame.Y1 - margin;
        }

        public static bool Inside(LBox frame, LBox b, double margin)
        {
            return b.X0 >= frame.X0 + margin && b.X1 <= frame.X1 - margin && b.Y0 >= frame.Y0 + margin && b.Y1 <= frame.Y1 - margin;
        }
    }

    /// <summary>
    /// </summary>
    internal sealed class LabelWeights
    {
        public double LeaderMm = 0.5;
        public double CrossMajor = 30;
        public double CrossMinor = 5;
        public double TextOnLine = 40;
        public double LeaderThroughText = 100;
        public double Dogleg = 10;
        public double CenterMm = 0.3;
        public double Direction = 0;
        public double NearSame = 0;
        public double Region = 1000;
        public double MaxCost = double.PositiveInfinity;
        public double GoodEnough = 5;

        public static LabelWeights PlanDefaults() { return new LabelWeights(); }

        public static LabelWeights CoordsDefaults()
        {
            return new LabelWeights
            {
                LeaderMm = 0.3, CrossMajor = 1000, CrossMinor = 2, TextOnLine = 0, LeaderThroughText = 1000,
                Dogleg = 0, CenterMm = 0, Direction = 0.15, NearSame = 0, Region = 1000, MaxCost = 500, GoodEnough = 0
            };
        }

        public LabelWeights Apply(JsonObject w)
        {
            if (w == null) return this;
            Func<string, double, double> g = (k, d) =>
            {
                var n = w[k]; if (n == null) return d;
                try { return n.GetValue<double>(); } catch { double v; return double.TryParse(n.ToString(), out v) ? v : d; }
            };
            LeaderMm = g("leader_mm", LeaderMm); CrossMajor = g("cross_major", CrossMajor); CrossMinor = g("cross_minor", CrossMinor);
            TextOnLine = g("text_on_line", TextOnLine); LeaderThroughText = g("leader_through_text", LeaderThroughText);
            Dogleg = g("dogleg", Dogleg); CenterMm = g("center_mm", CenterMm); Direction = g("direction", Direction);
            NearSame = g("near_same", NearSame); Region = g("region", Region); MaxCost = g("max_cost", MaxCost);
            GoodEnough = g("good_enough", GoodEnough);
            return this;
        }

        public JsonObject ToJson()
        {
            return new JsonObject
            {
                ["leader_mm"] = LeaderMm, ["cross_major"] = CrossMajor, ["cross_minor"] = CrossMinor,
                ["text_on_line"] = TextOnLine, ["leader_through_text"] = LeaderThroughText, ["dogleg"] = Dogleg,
                ["center_mm"] = CenterMm, ["direction"] = Direction, ["near_same"] = NearSame, ["region"] = Region,
                ["max_cost"] = double.IsInfinity(MaxCost) ? null : (JsonNode)MaxCost, ["good_enough"] = GoodEnough
            };
        }
    }

    internal sealed class LabelScene
    {
        public List<LBox> Texts = new List<LBox>();
        public List<LSeg> Major = new List<LSeg>();
        public List<LSeg> Minor = new List<LSeg>();
        public List<LSeg> All = new List<LSeg>();
        public List<List<LPt>> Regions = new List<List<LPt>>();
        public LBox? Frame;
        public double FrameMargin = 3.0;
        public double OverlapTol = 0.2;
    }

    internal sealed class LabelRequest
    {
        public LPt Anchor;
        public string[] Lines;
        public double H = 3.5;
        public double Wf = 0.8;
    }

    internal sealed class LabelResult
    {
        public bool Placed;
        public double Cost;
        public string Dir;
        public double LeaderMm;
        public LPt End;
        public LPt BaseEnd;
        public bool Right;
        public LBox Box;
        public List<LPt> LinePos = new List<LPt>();
        public Dictionary<string, int> Why = new Dictionary<string, int>();
    }

    internal static class LabelPlacer
    {
        public static readonly string[] DirOrder = { "NE", "E", "SE", "NW", "W", "SW", "N", "S" };

        static LPt DirVec(string d)
        {
            switch (d)
            {
                case "NE": return new LPt(1, 1);
                case "E": return new LPt(1.414, 0);
                case "SE": return new LPt(1, -1);
                case "NW": return new LPt(-1, 1);
                case "W": return new LPt(-1.414, 0);
                case "SW": return new LPt(-1, -1);
                case "N": return new LPt(0, 1.414);
                case "S": return new LPt(0, -1.414);
                default: throw new InvalidOperationException("Unknown direction '" + d + "'; use NE/E/SE/NW/W/SW/N/S.");
            }
        }

        static void Bump(Dictionary<string, int> why, string k) { int v; why.TryGetValue(k, out v); why[k] = v + 1; }

        /// <summary>
        /// </summary>
        public static LabelResult PlaceNormal(LabelScene sc, LabelRequest rq, LabelWeights w, double[] lengthsMm, string[] dirs,
                                              List<LBox> placedBoxes, List<LSeg> placedLeaders, double anchorExemptMm)
        {
            var best = new LabelResult();
            double H = rq.H;
            double textW = 0; foreach (var s in rq.Lines) textW = Math.Max(textW, LabelGeo.TextWidth(s, H, rq.Wf));
            double boxW = 0.6 * H + textW;
            int nl = Math.Max(1, rq.Lines.Length);
            double top = 1.3 * H, bottom = Math.Min(0.0, 0.3 * H - (nl - 1) * 1.6 * H);
            double bestCost = double.PositiveInfinity;
            for (int li = 0; li < lengthsMm.Length; li++)
            {
                double L = lengthsMm[li];
                for (int di = 0; di < dirs.Length; di++)
                {
                    LPt v = DirVec(dirs[di]);
                    LPt end = rq.Anchor + v * (L / Math.Sqrt(2));
                    bool right = v.X >= 0;
                    var box = right ? new LBox(end.X, end.Y + bottom, end.X + boxW, end.Y + top)
                                    : new LBox(end.X - boxW, end.Y + bottom, end.X, end.Y + top);
                    if (sc.Frame.HasValue && !LabelGeo.Inside(sc.Frame.Value, box, sc.FrameMargin)) { Bump(best.Why, "outside frame"); continue; }
                    bool hit = false;
                    foreach (var b in sc.Texts) if (LabelGeo.Overlap(box, b, sc.OverlapTol)) { hit = true; break; }
                    if (!hit) foreach (var b in placedBoxes) if (LabelGeo.Overlap(box, b, sc.OverlapTol)) { hit = true; break; }
                    if (hit) { Bump(best.Why, "text overlap"); continue; }

                    double f0 = Math.Min(0.5, anchorExemptMm / Math.Max(L, 1e-9));
                    LPt p2 = rq.Anchor + (end - rq.Anchor) * f0;
                    double cost = w.LeaderMm * L + w.Direction * di;
                    cost += w.CrossMajor * (LabelGeo.CountCross(p2, end, sc.Major) + LabelGeo.CountSegsInBox(box, sc.Major));
                    cost += w.CrossMinor * (LabelGeo.CountCross(p2, end, sc.Minor) + LabelGeo.CountSegsInBox(box, sc.Minor));
                    cost += w.TextOnLine * LabelGeo.CountSegsInBox(box, sc.All);
                    foreach (var b in sc.Texts) if (LabelGeo.SegBox(rq.Anchor, end, b)) cost += w.LeaderThroughText;
                    foreach (var b in placedBoxes) if (LabelGeo.SegBox(rq.Anchor, end, b)) cost += w.LeaderThroughText;
                    foreach (var s in placedLeaders)
                        if (LabelGeo.SegSeg(rq.Anchor, end, s.A, s.B) || LabelGeo.SegBox(s.A, s.B, box)) cost += w.LeaderThroughText;
                    if (w.Region > 0 && sc.Regions.Count > 0)
                    {
                        var corners = new[] { new LPt(box.X0, box.Y0), new LPt(box.X1, box.Y0), new LPt(box.X0, box.Y1), new LPt(box.X1, box.Y1), box.Center };
                        foreach (var poly in sc.Regions)
                        {
                            bool bad = false;
                            foreach (var c in corners) if (LabelGeo.InPoly(c, poly)) { bad = true; break; }
                            if (!bad) bad = PolyCrossSeg(poly, p2, end) || PolyCrossBox(poly, box);
                            if (bad) cost += w.Region;
                        }
                    }
                    if (w.NearSame > 0)
                        foreach (var b in placedBoxes)
                            if (LabelGeo.Overlap(new LBox(box.X0 - H, box.Y0 - H, box.X1 + H, box.Y1 + H), b, 0)) cost += w.NearSame;
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best.Cost = cost; best.Dir = dirs[di]; best.LeaderMm = L; best.End = end; best.Right = right; best.Box = box;
                        best.BaseEnd = right ? new LPt(end.X + boxW, end.Y) : new LPt(end.X - boxW, end.Y);
                        best.LinePos.Clear();
                        for (int i = 0; i < nl; i++)
                        {
                            double yb = end.Y + 0.3 * H - i * 1.6 * H;
                            best.LinePos.Add(new LPt(right ? end.X + 0.3 * H : end.X - boxW + 0.3 * H, yb));
                        }
                    }
                }
                if (bestCost < w.GoodEnough) break;
            }
            best.Placed = !double.IsInfinity(bestCost) && bestCost <= w.MaxCost;
            if (!double.IsInfinity(bestCost) && !best.Placed) Bump(best.Why, "cost limit exceeded");
            return best;
        }

        static bool PolyCrossSeg(List<LPt> poly, LPt p, LPt q)
        {
            for (int i = 0; i < poly.Count; i++)
                if (LabelGeo.SegSeg(p, q, poly[i], poly[(i + 1) % poly.Count])) return true;
            return false;
        }

        static bool PolyCrossBox(List<LPt> poly, LBox b)
        {
            var c = new[] { new LPt(b.X0, b.Y0), new LPt(b.X1, b.Y0), new LPt(b.X1, b.Y1), new LPt(b.X0, b.Y1) };
            for (int i = 0; i < 4; i++) if (PolyCrossSeg(poly, c[i], c[(i + 1) % 4])) return true;
            return false;
        }


        internal sealed class BoundaryRow
        {
            public int Index;
            public LPt Anchor;
            public string[] Lines;
            public double RowH;
        }

        internal sealed class BoundaryPlaced
        {
            public int Index;
            public LPt Anchor, LeaderEnd, BaseA, BaseB;
            public List<LPt> LinePos = new List<LPt>();
        }

        /// <summary>
        /// </summary>
        public static List<BoundaryPlaced> PlaceBoundary(List<BoundaryRow> rows, double colX, bool rightSide, double H,
                                                         double yMin, double yMax, out int crossings, double wf = 0.8)
        {
            crossings = 0;
            var result = new List<BoundaryPlaced>();
            if (rows.Count == 0) return result;
            const double gap = 0.8;
            double total = 0; foreach (var r in rows) total += r.RowH;
            total += (rows.Count - 1) * gap;
            double ymx = double.MinValue, ymn = double.MaxValue;
            foreach (var r in rows) { ymx = Math.Max(ymx, r.Anchor.Y); ymn = Math.Min(ymn, r.Anchor.Y); }
            double ctr = (ymx + ymn) / 2;
            double top = Math.Min(Math.Max(ctr + total / 2, yMin + total), yMax);

            Func<List<BoundaryRow>, List<LSeg>> layout = order =>
            {
                var segs = new List<LSeg>(); double yy = top;
                foreach (var r in order)
                {
                    double bse = yy - r.RowH + 1.0;
                    segs.Add(new LSeg(r.Anchor, new LPt(colX, bse)));
                    yy -= r.RowH + gap;
                }
                return segs;
            };
            Func<List<LSeg>, double> score = segs =>
            {
                int c = 0; double len = 0;
                for (int i = 0; i < segs.Count; i++)
                {
                    len += (segs[i].B - segs[i].A).Len;
                    for (int j = i + 1; j < segs.Count; j++) if (LabelGeo.SegSeg(segs[i].A, segs[i].B, segs[j].A, segs[j].B)) c++;
                }
                return c * 100 + len;
            };

            List<BoundaryRow> bestOrder;
            if (rows.Count <= 6)
            {
                bestOrder = null; double bestS = double.PositiveInfinity;
                foreach (var perm in Permutations(rows))
                {
                    double s = score(layout(perm));
                    if (s < bestS) { bestS = s; bestOrder = perm; }
                }
            }
            else
            {
                bestOrder = new List<BoundaryRow>(rows);
                bestOrder.Sort((a, b) => b.Anchor.Y.CompareTo(a.Anchor.Y));
            }

            var fin = layout(bestOrder);
            for (int i = 0; i < fin.Count; i++)
                for (int j = i + 1; j < fin.Count; j++) if (LabelGeo.SegSeg(fin[i].A, fin[i].B, fin[j].A, fin[j].B)) crossings++;

            double y = top;
            foreach (var r in bestOrder)
            {
                double bse = y - r.RowH + 1.0;
                double wd = 0; foreach (var s in r.Lines) wd = Math.Max(wd, LabelGeo.TextWidth(s, H, wf));
                var bp = new BoundaryPlaced { Index = r.Index, Anchor = r.Anchor, LeaderEnd = new LPt(colX, bse) };
                if (rightSide) { bp.BaseA = new LPt(colX, bse); bp.BaseB = new LPt(colX + wd + 0.6, bse); }
                else { bp.BaseA = new LPt(colX - wd - 0.6, bse); bp.BaseB = new LPt(colX, bse); }
                for (int i = 0; i < r.Lines.Length; i++)
                    bp.LinePos.Add(new LPt(bp.BaseA.X + 0.3, bse + 0.6 + (r.Lines.Length - 1 - i) * 1.6 * H));
                result.Add(bp);
                y -= r.RowH + gap;
            }
            return result;
        }

        static IEnumerable<List<T>> Permutations<T>(List<T> items)
        {
            if (items.Count <= 1) { yield return new List<T>(items); yield break; }
            for (int i = 0; i < items.Count; i++)
            {
                var rest = new List<T>(items); rest.RemoveAt(i);
                foreach (var p in Permutations(rest)) { p.Insert(0, items[i]); yield return p; }
            }
        }


        internal sealed class CrossCutScene
        {
            public LBox Frame;
            public List<LSeg> Top = new List<LSeg>();
            public List<LSeg> Cntr = new List<LSeg>();
            public List<LSeg> Samp = new List<LSeg>();
            public List<LSeg> Other = new List<LSeg>();
            public List<LSeg> All = new List<LSeg>();
            public List<LBox> Texts = new List<LBox>();
        }

        internal sealed class CrossCutParams
        {
            public string[] Names = { "Dredging top edge", "Dredging centerline", "Dredging top edge" };
            public double H = 3.5, Pitch = 5.5;
            public double Wf = 0.8;
            public bool Underline = true;
            public double Step = 5.0;
            public double Inset = 30;
            public double Edge = 6;
            public double TextEdge = 4;
            public double MaxHalf = 25;
            public double NearGap = 2.5;
            public double[] Ext = { 8.0, 14.0, 20.0 };
            public double[] Offs = { 0, 8, -8, 14, -14, 20, -20 };
            public int MaxCands = 400;
        }

        internal sealed class CrossCutResult
        {
            public bool Placed;
            public double Cost;
            public LPt Near, Far, Anc, A, P, B;
            public double K, E;
            public List<LBox> TextBoxes = new List<LBox>();
            public Dictionary<string, int> Why = new Dictionary<string, int>();
        }

        /// <summary>
        /// </summary>
        public static CrossCutResult CrossCut(CrossCutScene sc, CrossCutParams p, LabelWeights w)
        {
            var res = new CrossCutResult();
            var cands = new List<KeyValuePair<LPt, LPt>>();
            LPt center = sc.Frame.Center;
            foreach (var s in sc.Cntr)
            {
                LPt d = s.B - s.A; double L = d.Len;
                if (L < 1e-6) continue;
                d = d * (1.0 / L);
                for (double k = 0; k <= L; k += p.Step)
                {
                    LPt P = s.A + d * k;
                    if (LabelGeo.Inside(sc.Frame, P, p.Inset)) cands.Add(new KeyValuePair<LPt, LPt>(P, d));
                }
            }
            res.Why["candidate"] = cands.Count;
            cands.Sort((x, y) => (x.Key - center).Len.CompareTo((y.Key - center).Len));
            if (cands.Count > p.MaxCands) cands.RemoveRange(p.MaxCands, cands.Count - p.MaxCands);

            double bestCost = double.PositiveInfinity;
            foreach (var c in cands)
            {
                LPt P = c.Key, d = c.Value, n = new LPt(-d.Y, d.X);
                double tA = double.NegativeInfinity, tB = double.PositiveInfinity; bool hasA = false, hasB = false;
                foreach (var s in sc.Top)
                {
                    double? t = LabelGeo.RayHit(P, n, s.A, s.B);
                    if (!t.HasValue) continue;
                    if (t.Value < -0.3 && t.Value > tA) { tA = t.Value; hasA = true; }
                    if (t.Value > 0.3 && t.Value < tB) { tB = t.Value; hasB = true; }
                }
                if (!hasA || !hasB) { Bump(res.Why, "no top-edge lines found on both sides"); continue; }
                if (-tA > p.MaxHalf || tB > p.MaxHalf) { Bump(res.Why, "channel too wide"); continue; }

                foreach (double E in p.Ext)
                    foreach (double K in p.Offs)
                        for (int sg = 1; sg >= -1; sg -= 2)
                        {
                            double tOut = sg > 0 ? tB : tA;
                            LPt far = P + n * (tOut + E * sg);
                            LPt near = sg > 0 ? P + n * (tA - p.NearGap) : P + n * (tB + p.NearGap);
                            if (!LabelGeo.Inside(sc.Frame, far, p.Edge)) { Bump(res.Why, "outer endpoint outside frame"); continue; }
                            double cost = w.CrossMajor * LabelGeo.CountCross(near, far, sc.Other)
                                        + w.CrossMinor * LabelGeo.CountCross(near, far, sc.Samp);
                            foreach (var b in sc.Texts) if (LabelGeo.SegBox(near, far, b)) cost += w.LeaderThroughText;
                            LPt anc = far + d * K;
                            bool right = anc.X >= (P + n * tOut).X;
                            var tb = new List<LBox>(); bool ok = true;
                            for (int j = 0; j < p.Names.Length; j++)
                            {
                                double tw = LabelGeo.TextWidth((p.Underline ? "%%u" : "") + p.Names[j], p.H, p.Wf);
                                double yb = anc.Y + p.Pitch * ((p.Names.Length - 1) / 2.0 - j) - p.H / 2;
                                var bx = right ? new LBox(anc.X + 1.0, yb, anc.X + 1.0 + tw, yb + p.H)
                                               : new LBox(anc.X - 1.0 - tw, yb, anc.X - 1.0, yb + p.H);
                                if (!LabelGeo.Inside(sc.Frame, bx, p.TextEdge)) { Bump(res.Why, "text outside frame"); ok = false; break; }
                                bool hit = false;
                                foreach (var b in sc.Texts) if (LabelGeo.Overlap(bx, b)) { hit = true; break; }
                                if (!hit) foreach (var b in tb) if (LabelGeo.Overlap(bx, b)) { hit = true; break; }
                                if (hit) { Bump(res.Why, "text overlaps text"); ok = false; break; }
                                cost += w.TextOnLine * LabelGeo.CountSegsInBox(bx, sc.All);
                                tb.Add(bx);
                            }
                            if (!ok) continue;
                            if (K != 0)
                            {
                                cost += w.Dogleg + w.CrossMajor * (LabelGeo.CountCross(far, anc, sc.Other) + LabelGeo.CountCross(far, anc, sc.Samp));
                                foreach (var b in sc.Texts) if (LabelGeo.SegBox(far, anc, b)) cost += w.LeaderThroughText;
                            }
                            cost += w.CenterMm * (P - center).Len + w.LeaderMm * E;
                            if (cost < bestCost)
                            {
                                bestCost = cost;
                                res.Cost = cost; res.Near = near; res.Far = far; res.Anc = anc; res.K = K; res.E = E;
                                res.A = P + n * tA; res.P = P; res.B = P + n * tB; res.TextBoxes = tb;
                            }
                        }
                if (bestCost < w.GoodEnough) break;
            }
            res.Placed = !double.IsInfinity(bestCost) && bestCost <= w.MaxCost;
            if (!double.IsInfinity(bestCost) && !res.Placed) Bump(res.Why, "cost limit exceeded");
            return res;
        }


        /// <summary>
        /// </summary>
        public static string PickIndexCorner(LBox frame, double w, double h, double margin, List<LPt> ownPts, List<LBox> texts,
                                             List<LSeg> segs, List<LBox> blockers, out LBox chosen, out double density)
        {
            var order = new[] { "LL", "UL", "LR", "UR" };
            string pick = null; chosen = default(LBox); density = double.PositiveInfinity;
            foreach (var k in order)
            {
                double x0 = k[1] == 'L' ? frame.X0 + margin : frame.X1 - margin - w;
                double y0 = k[0] == 'L' ? frame.Y0 + margin : frame.Y1 - margin - h;
                var b = new LBox(x0, y0, x0 + w, y0 + h);
                double dn = 0;
                foreach (var p in ownPts) if (p.X >= b.X0 && p.X <= b.X1 && p.Y >= b.Y0 && p.Y <= b.Y1) dn += 3;
                foreach (var t in texts) if (LabelGeo.Overlap(t, b)) dn += 3;
                dn += LabelGeo.CountSegsInBox(b, segs);
                foreach (var t in blockers) if (LabelGeo.Overlap(t, b)) dn += 1000;
                if (dn < density) { density = dn; pick = k; chosen = b; }
            }
            return pick;
        }


        internal sealed class NameLine
        {
            public string Name;
            public int Tag;
            public double Lo, Hi;
            public List<LPt> Cands = new List<LPt>();
            public List<LSeg> Own = new List<LSeg>();
            public double W, H;
            public double Dy;
        }

        internal sealed class NamePlaceParams
        {
            public double X0;
            public double IncMm;
            public double EndGapMm = 2;
            public bool Avoid = true;
            public bool PickBest = false;
            public double Tol = 0.2;
        }

        internal sealed class NamePlaced
        {
            public NameLine Line;
            public bool Placed;
            public int Cand = -1;
            public LBox Box;
            public double Cost;
            public double TargetX;
            public string Why;
        }

        /// <summary>
        /// </summary>
        public static List<NamePlaced> PlaceSurfaceNames(List<NameLine> lines, List<LBox> texts, List<LSeg> segs,
                                                         NamePlaceParams p, LabelWeights w)
        {
            var res = new List<NamePlaced>();
            var placed = new List<LBox>();
            var order = new List<NameLine>(lines);
            order.Sort((a, b) => b.W != a.W ? b.W.CompareTo(a.W) : a.Lo.CompareTo(b.Lo));
            double g = p.EndGapMm;
            foreach (var L in order)
            {
                Func<LPt, LBox> boxOf = c => new LBox(c.X - L.W / 2, c.Y + L.Dy - L.H / 2, c.X + L.W / 2, c.Y + L.Dy + L.H / 2);
                Func<LBox, bool> endOk = b => g < 0 || (b.X0 >= L.Lo + g - 1e-9 && b.X1 <= L.Hi - g + 1e-9);

                var targets = new List<double>();
                if (p.IncMm > 1e-9)
                {
                    long k0 = (long)Math.Ceiling((L.Lo - p.X0) / p.IncMm - 1e-9), k1 = (long)Math.Floor((L.Hi - p.X0) / p.IncMm + 1e-9);
                    for (long k = k0; k <= k1; k++)
                    {
                        double tx = p.X0 + k * p.IncMm;
                        if (g < 0 || endOk(new LBox(tx - L.W / 2, 0, tx + L.W / 2, 0))) targets.Add(tx);
                    }
                    if (g < 0) { if (targets.Count == 0 || Math.Abs(targets[0] - L.Lo) > 1e-6) targets.Insert(0, L.Lo); if (Math.Abs(targets[targets.Count - 1] - L.Hi) > 1e-6) targets.Add(L.Hi); }
                }
                if (targets.Count == 0)
                {
                    if (g >= 0 && L.Hi - L.Lo < L.W + 2 * g)
                    { res.Add(new NamePlaced { Line = L, Placed = false, TargetX = (L.Lo + L.Hi) / 2, Why = "endpoint clearance: surface line shorter than text" }); continue; }
                    targets.Add((L.Lo + L.Hi) / 2);
                }

                var used = new HashSet<int>();
                var groups = new List<List<double>>();
                if (p.PickBest) groups.Add(targets); else foreach (var t in targets) groups.Add(new List<double> { t });
                foreach (var tg in groups)
                {
                    int bestI = -1; double bestCost = double.PositiveInfinity, bestDist = double.PositiveInfinity, bestT = tg[0];
                    int nEnd = 0, nText = 0, nName = 0, nWin = 0;
                    for (int i = 0; i < L.Cands.Count; i++)
                    {
                        if (used.Contains(i)) continue;
                        var c = L.Cands[i];
                        double dist = double.PositiveInfinity, tNear = tg[0];
                        foreach (var t in tg) { double d = Math.Abs(c.X - t); if (d < dist) { dist = d; tNear = t; } }
                        if (!p.PickBest && p.IncMm > 1e-9 && dist > p.IncMm / 2 + 1e-6) continue;
                        nWin++;
                        var b = boxOf(c);
                        if (!endOk(b)) { nEnd++; continue; }
                        bool hitName = false;
                        foreach (var q in placed) if (LabelGeo.Overlap(b, q, p.Tol)) { hitName = true; break; }
                        if (hitName && p.Avoid) { nName++; continue; }
                        if (p.Avoid)
                        {
                            bool hitText = false;
                            foreach (var q in texts) if (LabelGeo.Overlap(b, q, p.Tol)) { hitText = true; break; }
                            if (hitText) { nText++; continue; }
                        }
                        double cost = w.CenterMm * dist;
                        if (p.Avoid) cost += w.TextOnLine * Math.Max(0, LabelGeo.CountSegsInBox(b, segs) - LabelGeo.CountSegsInBox(b, L.Own));
                        if (cost < bestCost - 1e-9 || (Math.Abs(cost - bestCost) <= 1e-9 && dist < bestDist))
                        { bestCost = cost; bestDist = dist; bestI = i; bestT = tNear; }
                    }
                    if (bestI < 0 || bestCost > w.MaxCost)
                    {
                        string why = bestI >= 0 ? "cost limit exceeded" : nWin == 0 ? "no candidates near target" : nEnd == nWin ? "endpoint clearance" : (nText >= nName ? "text overlap (no feasible move)" : "surface-name overlap (no feasible move)");
                        res.Add(new NamePlaced { Line = L, Placed = false, TargetX = tg[0], Why = why, Cost = bestI >= 0 ? bestCost : double.NaN });
                        continue;
                    }
                    used.Add(bestI);
                    var bb = boxOf(L.Cands[bestI]);
                    placed.Add(bb);
                    res.Add(new NamePlaced { Line = L, Placed = true, Cand = bestI, Box = bb, Cost = bestCost, TargetX = bestT });
                }
            }
            return res;
        }
    }
}
