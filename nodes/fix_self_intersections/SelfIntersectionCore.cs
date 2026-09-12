#nullable disable   // 本文件被 Civil3DFactory（可空关）与 WaterBox（可空开）两个工程共同编译，按关处理

using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory.Geometry
{
    /// <summary>
    /// 自相交多段线的检测与修复核心（纯几何/DB，无交互依赖）。
    ///
    /// 自交线是下游的头号杀手：进 TIN 建面、GetOffsetCurves 偏移、算面积、打 Hatch
    /// 全会失败或悄悄给错数。修法是「去回环」——在两段的交点处把线剪断，
    /// 丢掉小的那个环、保留大的那个：
    ///   闭合线：两个候选都是环，比**面积**（含弧段的弓形修正），保面积大的；
    ///   开放线：套索状回环一律丢，保主干。
    /// 丢弃比例超过 max_drop_ratio 就**停手不改**并报「需人工确认」——
    /// 8 字形、大幅画错这类不是小毛刺，替用户拿主意会毁掉设计意图。
    ///
    /// 唯一真源：Civil3DFactory（节点 fix_self_intersections）与 products\waterbox
    /// （C3DF-FixSelfIntersect/ZJ）共同编译本文件。
    /// </summary>
    public static class SelfIntersectionCore
    {
        const double Tol = 1e-9;
        const double DupTol = 1e-6;      // 顶点重合判定
        const int MaxPasses = 200;       // 逐个回环消，防病态图形转不停

        /// <summary>一处自交：第 SegA 段与第 SegB 段交于 Point。</summary>
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
            public double AreaBefore;        // 闭合线才有意义（带符号面积的绝对值）
            public double AreaAfter;
            public double LengthBefore;
            public double LengthAfter;
            public int DuplicateVerticesRemoved;
            public int HitsFound;            // 修复前检出的交点数
            public int LoopsRemoved;
            public List<double> RemovedLoopAreas = new List<double>();
            public List<double> RemovedLoopLengths = new List<double>();
            public List<Point2d> Intersections = new List<Point2d>();   // 修复前的交点位置
            public bool Changed;             // 几何真的改了
            public bool Clean;               // 收工时不再自交
            public bool NeedsManual;         // 被 max_drop_ratio 拦下，未改动
            public string ManualReason;
        }

        // ============================================================
        //  检测：全部段两两求交，相邻段（含闭合线首末段）只排除共享顶点那个交点，
        //  不排除整对——相邻弧段能在第二点真相交，相邻直线段能整段回折重叠，
        //  这两类 Civil 加曲面边界都拒收，2026-09-01 前的版本整对跳过全漏判。
        //  直线×直线自己算（快且精确，平行时再查共线重叠），
        //  涉及弧段走 CurveCurveIntersector2d。
        // ============================================================
        public static List<Hit> Detect(Polyline pl)
        {
            int dup;
            return Detect(pl, out dup);
        }

        /// <summary>检测 + 报告有几个重复顶点（零长段）。重复顶点不产生 Hit，
        /// 但 Civil 加边界同样拒收，调用方要把 duplicateVertices>0 当「脏」处理。</summary>
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
                    // 相邻段共享一个顶点：交在共享点不算自交，其余交点都算
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

            // 含弧段：用 AutoCAD 的曲线求交器，失败就退回弦线近似（宁可漏报也不误判）
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
            // CircularArc2d 按逆时针从 start 到 end；顺时针弧调换端点
            return bulge > 0 ? new CircularArc2d(o, r, sa, sb, Vector2d.XAxis, false)
                             : new CircularArc2d(o, r, sb, sa, Vector2d.XAxis, false);
        }

        /// <summary>线段求交（含端点，排除共线重叠——共线重叠交给去重顶点那步）。</summary>
        static bool LineLine(Point2d p1, Point2d p2, Point2d p3, Point2d p4, out Point2d hit)
        {
            hit = default;
            Vector2d r = p2 - p1, s = p4 - p3;
            double den = r.X * s.Y - r.Y * s.X;
            if (Math.Abs(den) < 1e-12) return false;      // 平行或共线
            Vector2d w = p3 - p1;
            double t = (w.X * s.Y - w.Y * s.X) / den;
            double u = (w.X * r.Y - w.Y * r.X) / den;
            if (t < -1e-9 || t > 1 + 1e-9 || u < -1e-9 || u > 1 + 1e-9) return false;
            hit = p1 + r * t;
            return true;
        }

        /// <summary>共线（或近平行）线段的重叠检测：LineLine 对平行/共线一律 return false，
        /// 但「边上折返」「毛刺回折」这类整段重叠正是 Civil 加边界报错的常客。
        /// 判定：两端点到对方所在直线距离都小于容差、且参数区间重叠长度超过容差。
        /// 命中报重叠区间的中点，交给去回环机器当普通交点处理（回环面积≈0，必被丢弃）。</summary>
        static bool CollinearOverlap(Point2d a1, Point2d a2, Point2d b1, Point2d b2, out Point2d hit)
        {
            hit = default;
            Vector2d r = a2 - a1;
            double len = r.Length;
            if (len < DupTol) return false;
            double d1 = Math.Abs((b1 - a1).X * r.Y - (b1 - a1).Y * r.X) / len;
            double d2 = Math.Abs((b2 - a1).X * r.Y - (b2 - a1).Y * r.X) / len;
            if (d1 > DupTol || d2 > DupTol) return false;         // 平行但不共线
            double len2 = len * len;
            double t3 = ((b1 - a1).X * r.X + (b1 - a1).Y * r.Y) / len2;
            double t4 = ((b2 - a1).X * r.X + (b2 - a1).Y * r.Y) / len2;
            double lo = Math.Max(0, Math.Min(t3, t4));
            double hi = Math.Min(1, Math.Max(t3, t4));
            if ((hi - lo) * len < DupTol) return false;           // 只在端点碰一下，不算重叠
            // 交点优先取「落在对方段内部的端点」——修复的刀口正好切在折返顶点上，
            // 一轮收敛；两段完全互相覆盖时才退回重叠区间中点。
            double margin = DupTol / len;
            if (t3 > margin && t3 < 1 - margin) hit = b1;
            else if (t4 > margin && t4 < 1 - margin) hit = b2;
            else hit = a1 + r * ((lo + hi) / 2);
            return true;
        }

        // ============================================================
        //  修复：逐个回环消，直到不再自交
        //  返回新 Polyline（调用方入库、删旧线）；本来就干净或没敢动时返回 null。
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
                if (dupRemoved == 0) return null;        // 完全没事可做
            }

            // 被丢弃量的上限：闭合按面积、开放按长度
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

                // 候选 A：丢掉中间那圈 —— v0..vi, P, vj+1..
                var ptsA = new List<Point2d>();
                var bulA = new List<double>();
                for (int k = 0; k <= i; k++) { ptsA.Add(pts[k]); bulA.Add(k == i ? bi1 : bulges[k]); }
                ptsA.Add(P); bulA.Add(bj2);
                for (int k = j + 1; k < n; k++) { ptsA.Add(pts[k]); bulA.Add(bulges[k]); }

                // 候选 B：那圈本身 —— P, vi+1..vj, 回到 P（必闭合）
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
                    keepA = true;                                   // 开放线的回环是套索，一律丢
                    dropAmount = Math.Abs(SignedArea(ptsB, bulB, true));
                    if (dropAmount < Tol) dropAmount = TotalLength(ptsB, bulB, true);
                }

                if (dropped + dropAmount > budget)
                {
                    rep.NeedsManual = true;
                    rep.ManualReason = "第 " + (rep.LoopsRemoved + 1) + " 个回环要丢弃 "
                        + dropAmount.ToString("0.###") + (closed ? " m²" : "")
                        + "，超过允许比例（上限 " + budget.ToString("0.###")
                        + "），已停手未改动——请人工确认是回环还是设计本意。";
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
                    rep.ManualReason = "消到顶点不足，线本身可能是退化图形，已停手未改动。";
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

        // ---- 顶点表读取 + 闭合规范化 + 去重复点 ----
        static int Load(Polyline pl, List<Point2d> pts, List<double> bulges, ref bool closed)
        {
            for (int k = 0; k < pl.NumberOfVertices; k++)
            {
                pts.Add(pl.GetPoint2dAt(k));
                bulges.Add(pl.GetBulgeAt(k));
            }
            // 捕捉画闭：Closed=false 但首末重合，按闭合处理（否则收口段会被当自交漏判）
            if (!closed && pts.Count >= 4 &&
                pts[0].GetDistanceTo(pts[pts.Count - 1]) < DupTol)
            {
                pts.RemoveAt(pts.Count - 1);
                bulges.RemoveAt(bulges.Count - 1);
                closed = true;
            }
            return RemoveDuplicates(pts, bulges, closed);
        }

        /// <summary>相邻重合顶点会造出零长段，检测时全是假阳性，先清掉。</summary>
        static int RemoveDuplicates(List<Point2d> pts, List<double> bulges, bool closed)
        {
            int removed = 0;
            for (int k = pts.Count - 1; k > 0; k--)
            {
                if (pts[k].GetDistanceTo(pts[k - 1]) >= DupTol) continue;
                if (Math.Abs(bulges[k - 1]) > Tol) continue;      // 整圆弧段，留着
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

        // ---- 面积（含弧段弓形修正）：逆时针为正 ----
        static double SignedArea(List<Point2d> pts, List<double> bulges, bool closed)
        {
            int n = pts.Count;
            // 闭合 2 顶点带弧是合法的透镜/弓形，面积全在弧段修正里；按 n<3 归零会把
            // 这种回环的丢弃量记成 0，max_drop_ratio 护栏被穿透（2026-09-01 实测）
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
                double seg = r * r / 2 * (theta - Math.Sin(theta));   // 弓形面积
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

        // ---- 弧段在 P 处劈开后的两个 bulge（圆心公式与 OffsetConeCore.BuildSeg 一致）----
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
            double d = c.Length * (1 + bulge * bulge) / (4 * bulge);   // 带符号
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
