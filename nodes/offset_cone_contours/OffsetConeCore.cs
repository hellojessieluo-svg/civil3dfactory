#nullable disable   // 本文件被 Civil3DFactory（可空关）与 WaterBox（可空开）两个工程共同编译，按关处理

using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory.Geometry
{
    /// <summary>
    /// 偏移圆台 + 线形工具链的算法核心（纯几何/DB，不含任何交互与 Editor 依赖）。
    /// 唯一真源：Civil3DFactory（节点 offset_cone_contours）与 products\waterbox（YT/NH/GY/GZ 命令）
    /// 共同编译本文件，改算法只改这里。算法沿自 Civil3D-007-DLL-初步偏移圆台 v0.7（2026-07）。
    /// </summary>
    public static class OffsetConeCore
    {
        const double Tol = 1e-9;
        const double MinRingArea = 1e-6;        // 偏移结果总面积小于此视为塌缩
        const double MinDeflectionDeg = 8.0;    // 顶点偏转角小于此不归圆（与共线剔除阈值一致）
        const double CollinearDeg = 8.0;        // 预简化：偏转角小于此的直线顶点视为共线噪点
        const double LMinFactor = 0.5;          // 预简化：短于 半径×此系数 的直线段视为碎段

        /// <summary>GenerateCone 单条边界的结果。</summary>
        public sealed class ConeResult
        {
            public double Z0;            // 边界原高程
            public double Reached;       // 实际偏到的高程
            public int Rings;            // 生成圈数
            public bool Collapsed;       // 未达目标即塌缩
            public bool SameElevation;   // 目标高程 = 边界高程，什么都没做
            public int SteppedRings;     // 走了「从上一圈再偏一步」退路的圈数（含累积误差）
        }

        // ============================================================
        //  偏移圆台：闭合边界批量内偏生成等高线
        //  管线：边界平滑副本(Smooth) → 逐圈偏移 → 圈级补圆 → 赋高程
        //  emit：每生成一圈回调一次（Elevation/Layer 已设好），由调用方入库。
        //  调用方保证 src 闭合（或 IsSnapClosed）。
        // ============================================================
        public static ConeResult GenerateCone(Polyline src, double z1, double n, double dz,
            double rFillet, Action<Polyline> emit, bool outward = false)
        {
            double z0 = src.Elevation;
            var result = new ConeResult { Z0 = z0, Reached = z0 };
            if (Math.Abs(z1 - z0) < Tol) { result.SameElevation = true; return result; }
            // 向上=岛收顶，向下=坑收底；默认偏移向内。
            // outward=true 改为向外：边界当顶高程线，往外摊到 z1（岛屿外放坡到水底就是这个）。
            int dir = z1 > z0 ? 1 : -1;

            // 先在内存里做一份平滑副本作为偏移基准（源边界一根线不动）
            // r=0 时也要做闭合规范化副本，处理"捕捉画闭"的假开放线
            Polyline work = rFillet > Tol ? Smooth(src, rFillet)
                          : src.Closed ? src
                          : NormalizeClosedCopy(src);

            // 高程序列：间隔格点 + 目标高程（必含）
            var zList = new List<double>();
            for (double z = z0 + dir * dz; dir * (z1 - z) > Tol; z += dir * dz) zList.Add(z);
            zList.Add(z1);

            double sign = 0;      // 偏移方向的符号，第一圈判定后沿用
            var prev = new List<Polyline>();   // 上一圈的副本，只在基准偏移失败时当退路基准
            foreach (double z in zList)
            {
                double d = Math.Abs(z - z0) * n;   // 每圈都从平滑基准起算总距离
                DBObjectCollection ring;

                if (sign == 0)
                {
                    // 方向判定：两侧都偏 d，面积小的那侧是向内、大的那侧是向外
                    var plus = TryOffset(work, d);
                    var minus = TryOffset(work, -d);
                    double aPlus = TotalArea(plus), aMinus = TotalArea(minus);

                    // 向外时不会塌，塌了说明两侧都偏不出来（源线有问题）
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
                        // 从基准一次性偏总距离失败：凹弧半径小于 d 时 GetOffsetCurves 直接抛。
                        // 退路是从上一圈再偏一个增量——累积误差换能不能出得来。
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

                // 凹边界偏移可能分裂成多环：全部保留，赋同一高程
                DisposeAll2(prev); prev.Clear();
                foreach (DBObject o in ring)
                {
                    if (!(o is Polyline p)) { o.Dispose(); continue; }
                    Polyline final = p;
                    if (rFillet > Tol)
                    {
                        // 圈级补圆：内偏导致弧收缩消失后重新出现的尖角
                        final = Smooth(p, rFillet);
                        p.Dispose();
                    }
                    final.Elevation = z;
                    final.Layer = src.Layer;
                    prev.Add((Polyline)final.Clone());   // 留一份给退路当基准
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
        //  Smooth（线归圆内核）：碎段合并 → 共线剔除 → 全角相切圆角
        //  始终返回新 Polyline，不修改入参。
        //  圆角为弧感知——直线-直线 / 直线-弧 / 弧-弧交接统一
        //  用相切小弧过渡；原弧段只被裁短，圆心、半径、方向不变。
        // ============================================================
        public static Polyline Smooth(Polyline src, double r)
        {
            Polyline simplified = SimplifyVertices(src, r);
            Polyline result = FilletCorners(simplified, r);
            if (!ReferenceEquals(result, simplified)) simplified.Dispose();
            return result;
        }

        // ============================================================
        //  FitArcs（弧拟合简化）：碎直线段描线 → 少量直段+弧段
        //  贪心容差拟合：直线和圆弧竞争延伸，谁覆盖的顶点多用谁；
        //  弧 = 首/中/末三点定圆，窗口内所有顶点偏差 ≤ 容差且沿弧单调。
        //  只处理纯直线段多段线（含弧的先归直）。退化（顶点过少）返回 null。
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

        // 贪心拟合：把点列切成若干段，每段用直线或圆弧覆盖尽量多的点
        static List<(Point2d pt, double bulge)> FitSequence(List<Point2d> p, double tol, bool loop)
        {
            var seq = new List<Point2d>(p);
            if (loop) seq.Add(p[0]);           // 闭合：末尾补回起点走完全程
            var res = new List<(Point2d pt, double bulge)>();
            int last = seq.Count - 1;
            int i = 0;
            while (i < last)
            {
                // 闭合线第一段不许一口吞完，保证至少两个输出顶点
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

        // 窗口 [i..j] 能否用一段圆弧覆盖：三点定圆 + 全点偏差 + 沿弧单调
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
            if (Math.Abs(d) < 1e-12) return false;   // 三点共线
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
        //  UnSmooth（线归直）：弧段还原为尖角直线段
        //  Smooth 的逆操作：圆心角 ≤150° 的弧换成两端切线交点（原始尖角）；
        //  更大的弧还原尖角会出长刺，改用"弧中点 + 两段弦"保形。
        //  切点等共线残留顶点自动消失。始终返回新 Polyline。
        // ============================================================
        public static Polyline UnSmooth(Polyline src)
        {
            bool closed = src.Closed;
            var pts = new List<(Point2d pt, double bulge)>();
            for (int i = 0; i < src.NumberOfVertices; i++)
                pts.Add((src.GetPoint2dAt(i), src.GetBulgeAt(i)));
            NormalizeClosure(pts, ref closed);

            int n = pts.Count;
            double maxBulge = Math.Tan(150.0 / 4 * Math.PI / 180.0);   // 圆心角 150° 对应 bulge≈0.767
            var outPts = new List<Point2d>();

            for (int k = 0; k < n; k++)
            {
                double bi = closed ? pts[(k + n - 1) % n].bulge : (k == 0 ? 0 : pts[k - 1].bulge);
                bool hasOut = closed || k < n - 1;
                double bo = hasOut ? pts[k].bulge : 0;
                bool endVertex = !closed && (k == 0 || k == n - 1);

                // 普通直线顶点保留；弧的端点被吸收；开放线首末端点必须保留
                if ((Math.Abs(bo) < Tol && Math.Abs(bi) < Tol) || endVertex)
                    outPts.Add(pts[k].pt);

                if (hasOut && Math.Abs(bo) > Tol)
                {
                    Point2d p1 = pts[k].pt, p2 = pts[(k + 1) % n].pt;
                    Vector2d c = p2 - p1;
                    if (c.Length < Tol) continue;

                    if (Math.Abs(bo) <= maxBulge)
                    {
                        // 正 bulge = 逆时针（左转）弧：起点切向 = 弦向转 −θ/2（与 FilletCorners 的
                        // bulge 符号约定互为逆运算，已用圆角输出反推核对）
                        double half = 2 * Math.Atan(bo);               // 圆心角的一半（带符号）
                        Vector2d u = c.GetNormal().RotateBy(-half);    // 弧起点切向
                        double t = c.Length / (2 * Math.Cos(half));    // 端点到切线交点的距离
                        outPts.Add(p1 + u * t);                        // 还原的尖角
                    }
                    else
                    {
                        outPts.Add(ArcMidpoint(p1, p2, bo));           // 大弧：弦+弧中点保形
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
            // 矢高 h = bulge × 弦长 / 2，方向为弦的右法向（正 bulge=左转弧，弧鼓向行进方向右侧）
            return mid + c.GetNormal().RotateBy(-Math.PI / 2) * (bulge * c.Length / 2);
        }

        // ---- 预简化：把碎顶点清掉，让分摊掉的转角重新集中 ----
        // 碎直边收拢（SimplifyVertices 第 1 步的独立版，阈值由调用方给）：
        // 短于 lMin 的直线段收拢成一个顶点；弧段端点与开放线端点不动。
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
                    if (Math.Abs(pts[i].bulge) > Tol) continue;                 // 本段是弧
                    if (pts[i].pt.GetDistanceTo(pts[j].pt) >= lMin) continue;   // 不碎
                    bool aFixed = (!closed && i == 0) || Math.Abs(pts[(i + nv2 - 1) % nv2].bulge) > Tol;
                    bool bFixed = (!closed && j == nv2 - 1) || Math.Abs(pts[j].bulge) > Tol;
                    if (aFixed && bFixed) continue;
                    Point2d np = aFixed ? pts[i].pt
                               : bFixed ? pts[j].pt
                               : new Point2d((pts[i].pt.X + pts[j].pt.X) / 2, (pts[i].pt.Y + pts[j].pt.Y) / 2);
                    // 位移护栏（2026-08-24 项目B）：碎边夹角大时（如圈的折点旁一小截真实边），
                    // 收拢会把顶点横向顶走 1~2m、长边整体倾斜。被合掉的两个原顶点到
                    // 新几何（prev→np / np→next）的垂距超 5cm 就不收——共线碎屑才收得动。
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

        // ---- 角区清障：真角（偏转 ≥ minDefl）两侧 1.3×R·tan(δ/2)+2（封顶 3R）范围内，
        // 删掉 <minDefl 的小折角顶点、把扫角 ≤15° 的弧摊成弦；碰到别的真角或大弧就停。
        // 累计转角超 8° 也停（防止一串小折角其实是条缓弯被拉直）。基于弦向判角，够用。
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

            // 每个真角向两侧扫描，标记要摊平的弧段与要删的顶点
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
                    int seg = dir > 0 ? v : (v + n0 - 1) % n0;   // 起步段
                    for (int step = 0; step < n0; step++)
                    {
                        // 矢高=弦长×|bulge|/2。"扫角≤15°"判不出弧的大小——大半径缓弧扫角 9°
                        // 却有米级矢高（项目B圈弧 316m 被摊成弦、台田贴圈边整段漂移 1.5m 的元凶）。
                        // 真枝节矢高必然厘米级：矢高超 5cm 的按大弧对待，不动、停。
                        double sag = SegLen(seg) * Math.Abs(pts[seg % N()].bulge) / 2;
                        if (SegSweep(seg) > sweepMax || sag > 0.05) break;   // 大弧/长缓弧：不动，停
                        if (SegSweep(seg) > Tol) flatten.Add(seg % n0);
                        acc += SegLen(seg);
                        if (acc >= zone) break;
                        int nxtV = dir > 0 ? (seg + 1) % n0 : seg;             // 走到的顶点
                        if (!closed && (nxtV == 0 || nxtV == n0 - 1)) break;   // 开放线端点
                        double dv = Defl(nxtV);
                        if (dv >= minDeflRad) break;              // 别的真角：它自己倒角
                        cum += dv;
                        if (cum > cumMax) break;                  // 缓弯保护
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
                // 只删两侧皆直段的（摊平后基本都直了；仍挨着弧的不删，保切点）；
                // 位移护栏（2026-08-24 项目B）：<minDefl 的折点夹在两条长边之间时是真实缓弯，
                // 删它=两长边并弦、几何抬走米级（圈边 2°折点×13m/333m 实测漂 1.6m）。
                // 顶点到两邻点连线垂距 >5cm 的不删——真枝节垂距必然厘米级。
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

        // 共线顶点剔除（SimplifyVertices 第 2 步的独立版，阈值由调用方给）：
        // 两侧皆直线且偏转角 < 阈值的顶点删掉。开放线端点不动。
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
                        // 位移护栏（2026-08-24 项目B）：角度阈值不看尺度——0.4° 折点两边各两百米，
                        // 删掉=1.4m 位移。顶点到两邻点连线垂距 >5cm 不删。
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

            // 闭合规范化：捕捉画闭的线 Closed=false 但首末点重合——按闭合处理并去掉重复点，
            // 否则首末顶点被当开放线端点保护，收口角永远不会被归圆
            NormalizeClosure(pts, ref closed);

            double lMin = r * LMinFactor;
            double colTol = CollinearDeg * Math.PI / 180.0;
            int minKeep = closed ? 4 : 3;

            // 1) 碎段合并：短于 lMin 的直线段收拢成一个顶点（弧段和开放线端点不动）
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
                    if (Math.Abs(pts[i].bulge) > Tol) continue;                 // 本段是弧
                    if (pts[i].pt.GetDistanceTo(pts[j].pt) >= lMin) continue;   // 不碎

                    bool aFixed = (!closed && i == 0) || Math.Abs(pts[(i + nv - 1) % nv].bulge) > Tol;
                    bool bFixed = (!closed && j == nv - 1) || Math.Abs(pts[j].bulge) > Tol;
                    if (aFixed && bFixed) continue;                             // 两端都动不得

                    Point2d np = aFixed ? pts[i].pt
                               : bFixed ? pts[j].pt
                               : new Point2d((pts[i].pt.X + pts[j].pt.X) / 2, (pts[i].pt.Y + pts[j].pt.Y) / 2);
                    pts[i] = (np, pts[j].bulge);   // 合并顶点接管 j 的出边 bulge
                    pts.RemoveAt(j);
                    changed = true;
                    break;
                }
            }

            // 2) 共线剔除：两侧皆直线、偏转角 < 阈值的顶点是噪点
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
                        // 位移护栏（2026-08-24 项目B）：角度阈值不看尺度——0.4° 折点两边各两百米，
                        // 删掉=1.4m 位移。顶点到两邻点连线垂距 >5cm 不删。
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

        // ---- 尖角归圆（弧感知）：直线-直线 / 直线-弧 / 弧-弧交接统一处理 ----
        // 固定半径 r = 恒定曲率；过渡小弧与两侧曲线相切，原弧段只裁短不改形。
        // 半径放不下时自动折半重试（最多 6 次），仍不行保留原折角。
        // 每个交接从相邻段消耗的长度 ≤ 该段的 45%。开放线首末顶点不处理。
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

            // 逐顶点求过渡弧（顶点 i 连接 segs[i-1] 与 segs[i]）
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
                double delta = Math.Acos(dot);            // 切向偏转角
                if (delta < minDefl || delta > Math.PI - 0.01) continue;

                double rr = r;
                for (int a = 0; a < 6; a++, rr /= 2)
                    if (TrySolveJunction(s1, s2, v, rr, out var jj)) { junc[i] = jj; break; }
            }

            // 重建：每段输出（可能被裁短的）起点，交接处插入过渡弧顶点
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
        //  RoundSharpCorners（尖角甄别归圆，台田边角用）：
        //  与 FilletCorners 的区别——①不做任何预简化，源线的自然缓弯、
        //  密集小折角原样保留；②只对偏转角 ≥ 阈值的"真尖角"倒圆；
        //  ③半径放不下折半重试 6 次，仍放不下保留尖角并计数。
        //  始终返回新 Polyline，不修改入参。
        // ============================================================
        public sealed class RoundStats
        {
            public int Rounded;      // 归圆的尖角数
            public int KeptGentle;   // 明显折角（≥8°）但小于阈值，判为自然弯保留
            public int CantFit;      // 该归圆但半径折半 6 次仍放不下，保留尖角
            public int Downgraded;   // 标准半径放不下、按现场塞得下的最大半径倒（二分上探）
            public double MinUsedR;  // 降级角里用到的最小半径（没降级=0）
            public int Spanned;      // 跨边吞角：短边撑不下标准半径，一段 R 弧跨过短边连角一并吞掉
        }

        public static Polyline RoundSharpCorners(Polyline src, double r, double minDeflDeg, RoundStats stats)
        {
            int nv = src.NumberOfVertices;
            if (nv < 3) return (Polyline)src.Clone();
            bool closed = src.Closed;
            double minDefl = minDeflDeg * Math.PI / 180.0;
            double noiseDefl = 8.0 * Math.PI / 180.0;   // 小于 8° 视为线形噪声，不计入"保留"统计

            var pts = new List<(Point2d pt, double bulge)>(nv);
            for (int i = 0; i < nv; i++)
                pts.Add((src.GetPoint2dAt(i), src.GetBulgeAt(i)));
            NormalizeClosure(pts, ref closed);

            // 微碎边收拢（阈值 r/4 封顶 5m，仍远小于 Smooth 的 r/2）：布尔求差常在
            // 交叉口和斜插端头留下米级碎直边，把相邻角的圆角空间顶死（每段只让 45%）。
            // 只收直边，弧端不动。
            CollapseShortStraights(pts, closed, Math.Max(0.05, Math.Min(r / 4, 5.0)));

            // 共线碎节合并（0.5°，比 Smooth 的 8° 严得多，范围线自然弯不受影响）：
            // 布尔会把笔直长边切成多段共线短节，求圆角只看紧邻一节，节短就被迫
            // 折半降级——先并回完整长边，大角才吃得到足额半径。
            RemoveCollinearVertices(pts, closed, 0.5);

            // 角区清障：真角两侧一个切线长范围内的微枝节（<8°小折角顶点、扫角≤15°的
            // 短弧）拉直摊平——人倒角时就是把两侧当完整直边看的。范围外几何一概不动，
            // 与水道边界的贴合只在本来就要变成圆角的区域内让步。
            ClearCornerZones(pts, closed, r, minDefl);

            // 跨边吞角：边短到撑不起两头切距时，用一段足额 R 弧跨过短边直切两侧
            // 长边，把中间的小边小角一并吞掉（人工倒角遇到角簇也是这么并的）。
            SwallowShortEdges(pts, closed, r, minDefl, stats);

            int n = pts.Count;
            int segCount = closed ? n : n - 1;
            if (segCount < 2) return (Polyline)src.Clone();

            var segs = new Seg[segCount];
            for (int k = 0; k < segCount; k++)
                segs[k] = BuildSeg(pts[k].pt, pts[(k + 1) % n].pt, pts[k].bulge);

            // 第一遍：算每个顶点的切向偏转角
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

            // 第二遍：求过渡弧。邻段另一头没有竞争圆角（不是真角）时消耗上限放到 0.9
            var junc = new Junction?[n];
            for (int i = vStart; i < vEnd; i++)
            {
                double delta = deltaArr[i];
                if (delta <= 0 || delta > Math.PI - 0.01) continue;  // 折返角/退化不处理
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
                    // 折半只给 R/2 台阶——能吃 R18 的角不该只拿 R10。
                    // 在 [rr, min(R, 2rr)) 二分上探，取现场塞得下的最大半径。
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
        //  SwallowShortEdges（跨边吞角）：角在全额 R 下解不出＝相邻边太短撑不起
        //  切距。此时不缩半径，而是选一个包含该角的顶点窗口（2~3 个同向转弯的
        //  顶点），求一段半径 R 的弧同时切到窗口两侧的外边上，把窗口里的短边
        //  短角整体替换成这段弧。护栏：S 形反弯不吞（单弧无解）；被吞边总长
        //  ≤1.6R；被吞顶点必须都在弧外侧且到弧的退距 ≤R（防出鬼解）。
        //  每吞一处重扫全环，吞到没得吞为止。
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
                else if (far < 1 || far > n - 2) return 0.9;   // 开放线端头无竞争
                return delta[far] >= minDefl ? 0.45 : 0.9;
            }

            Rebuild();

            // 闭合环把起点转到最平直的顶点上，吞角窗口就不用跨列表接缝
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
                    // 全额半径放得下的角轮不到吞
                    if (TrySolveJunction(segs[i - 1], segs[i], pts[i].pt, r, CapAt(i - 1), CapAt(i + 1), out _))
                        continue;

                    // 候选窗口按被吞边总长从短到长试
                    var wins = new List<(int a, int b, double len)>();
                    foreach (var (a, b) in new[] { (i, i + 1), (i - 1, i), (i, i + 2), (i - 1, i + 1), (i - 2, i) })
                    {
                        if (a < 1 || b > n - 1) continue;
                        bool ok = true; double swLen = 0;
                        for (int k = a; k <= b && ok; k++)
                        {
                            if (delta[k] > Math.PI - 0.01) ok = false;                       // 折返角不碰
                            else if (delta[k] >= noiseDefl && side[k] != side[i]) ok = false; // S 形反弯无单弧解
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

                        // 鬼解拒收：被吞顶点须都在弧外侧、退距 ≤R
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
        //  Refillet（重倒角）：手拉修改过的台田边界，拆掉旧圆角弧、按新半径重倒。
        //  甄别口径：半径 ≤ unroundRMax 的弧视为旧圆角，还原成尖角（UnSmooth 同款
        //  切线交点公式，只依赖弧自身端点切向，邻段不动）；更大的弧是设计弧
        //  （通道弯、范围线弧），原样保留。之后走 RoundSharpCorners 全流程重倒。
        //  始终返回新 Polyline，不修改入参。unrounded 报拆掉的旧圆角数。
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
            double maxBulge = Math.Tan(150.0 / 4 * Math.PI / 180.0);   // 超过 150° 的弧不敢拆

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
            // 闭合环回绕：末段是小弧时它吸收的是顶点 0，得在循环前标掉
            if (closed && SmallArc(n - 1) && !SmallArc(0)) absorbed[0] = true;
            for (int k = 0; k < n; k++)
            {
                if (absorbed[k]) continue;
                // 相邻两段都是小弧时只拆第一段（第二段当保留弧），避免连锁吸收
                if (k < segCount && SmallArc(k) && !SmallArc((k + 1) % n))
                {
                    Point2d p1 = pts[k].pt, p2 = pts[(k + 1) % n].pt;
                    Vector2d c = p2 - p1;
                    double bo = pts[k].bulge;
                    double half = 2 * Math.Atan(bo);
                    Vector2d u = c.GetNormal().RotateBy(-half);
                    double t = c.Length / (2 * Math.Cos(half));
                    outPts.Add((p1 + u * t, pts[(k + 1) % n].bulge));   // 尖角接管旧弧终点的出边
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

        // ---- 交接求解：与 seg1 末端、seg2 首端同时相切、半径 rr 的过渡弧 ----
        struct Junction
        {
            public Point2d Tp1, Tp2;   // seg1 上的切点、seg2 上的切点
            public double Bulge;       // 过渡弧 bulge
        }

        static bool TrySolveJunction(Seg s1, Seg s2, Point2d v, double rr, out Junction j)
            => TrySolveJunction(s1, s2, v, rr, 0.45, 0.45, out j);

        // cap1/cap2：允许从 seg1 尾部 / seg2 头部消耗的比例上限。
        // 默认 0.45（两头都可能有圆角，各让一半留余量）；调用方确认该段另一头
        // 没有竞争圆角时可放宽（RoundSharpCorners 放到 0.9）。
        static bool TrySolveJunction(Seg s1, Seg s2, Point2d v, double rr, double cap1, double cap2, out Junction j)
        {
            Vector2d t1 = TangentAt(s1, v), t2 = TangentAt(s2, v);
            int side = (t1.X * t2.Y - t1.Y * t2.X) >= 0 ? 1 : -1;   // 转弯侧：+1 左转
            return SolveJunctionCore(s1, s2, v, side, rr, cap1, cap2, out j);
        }

        // 跨边吞角版：s1、s2 不共享顶点（中间隔着被吞的短边），转弯侧按 s1 末端
        // 与 s2 首端的切向定；vref（被吞区中点）只用于多解取近。
        static bool TrySolveJunctionAcross(Seg s1, Seg s2, Point2d vref, int side,
            double rr, double cap1, double cap2, out Junction j)
            => SolveJunctionCore(s1, s2, vref, side, rr, cap1, cap2, out j);

        static bool SolveJunctionCore(Seg s1, Seg s2, Point2d v, int side,
            double rr, double cap1, double cap2, out Junction j)
        {
            j = default;

            // 过渡弧圆心轨迹：直线→向转弯侧平移 rr；圆弧→同心圆 R − side·W·rr
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
                if (u1 < 1 - cap1 || u1 > 1 - 1e-9) continue;   // seg1 尾部消耗 ≤cap1 且切点在段内
                if (u2 > cap2 || u2 < 1e-9) continue;           // seg2 头部消耗 ≤cap2

                double sweep = SweepBetween(tp1 - C, tp2 - C, side);
                if (sweep > Math.PI + 0.01) continue;        // 过渡弧不该绕大圈

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
                lp = s.A + ld.RotateBy(Math.PI / 2) * (side * rr);   // 向转弯侧平移
                return true;
            }
            lo = s.O;
            lr = s.R - side * s.W * rr;   // 转弯侧朝圆心则收半径，背圆心则放半径
            return lr > Tol;
        }

        // ---- 段几何（直线或圆弧，由 bulge 决定） ----
        struct Seg
        {
            public bool IsArc;
            public Point2d A, B;    // 起终点（按行进方向）
            public Point2d O;       // 圆心
            public double R;        // 半径
            public int W;           // 行进方向：+1 逆时针 / -1 顺时针
            public double Sweep;    // 圆心角（带符号 = 4·atan(bulge)）
        }

        static Seg BuildSeg(Point2d a, Point2d b, double bulge)
        {
            var s = new Seg { A = a, B = b };
            Vector2d c = b - a;
            if (Math.Abs(bulge) < Tol || c.Length < Tol) return s;   // 直线
            double d = c.Length * (1 + bulge * bulge) / (4 * bulge); // 圆心带符号距离
            s.IsArc = true;
            s.O = a + c.GetNormal().RotateBy(Math.PI / 2 - 2 * Math.Atan(bulge)) * d;
            s.R = Math.Abs(d);
            s.W = Math.Sign(bulge);
            s.Sweep = 4 * Math.Atan(bulge);
            return s;
        }

        // 行进方向上点 P 处的单位切向
        static Vector2d TangentAt(Seg s, Point2d p)
            => s.IsArc ? (p - s.O).GetNormal().RotateBy(s.W * Math.PI / 2)
                       : (s.B - s.A).GetNormal();

        // 点在段上的行进参数 0..1（越界返回超出 [0,1] 的值，供校验拒绝）
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

        // 从 from 转到 to 沿 dir(+1 逆/-1 顺)扫过的角度，[0, 2π)
        static double SweepBetween(Vector2d from, Vector2d to, int dir)
        {
            double a = (Math.Atan2(to.Y, to.X) - Math.Atan2(from.Y, from.X)) * dir;
            while (a < 0) a += 2 * Math.PI;
            while (a >= 2 * Math.PI) a -= 2 * Math.PI;
            return a;
        }

        // 段裁剪到新端点 p→q 后的 bulge
        static double BulgeOf(Seg s, Point2d p, Point2d q)
        {
            if (!s.IsArc) return 0;
            return s.W * Math.Tan(SweepBetween(p - s.O, q - s.O, s.W) / 4);
        }

        // 圆心 C 在段上的相切垂足
        static Point2d FootOn(Seg s, Point2d c)
        {
            if (s.IsArc)
            {
                Vector2d w = c - s.O;
                if (w.Length < Tol) return s.A;   // 退化：交由参数校验拒绝
                return s.O + w.GetNormal() * s.R;
            }
            Vector2d d = (s.B - s.A).GetNormal();
            return s.A + d * (c - s.A).DotProduct(d);
        }

        // ---- 轨迹交点 ----
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

        // ---- 闭合规范化 ----
        const double SnapTol = 1e-6;   // 首末点视为重合的距离

        static void NormalizeClosure(List<(Point2d pt, double bulge)> pts, ref bool closed)
        {
            if (!closed && pts.Count >= 4 &&
                pts[0].pt.GetDistanceTo(pts[pts.Count - 1].pt) < SnapTol)
            {
                pts.RemoveAt(pts.Count - 1);   // 假开放：去重复末点，转闭合
                closed = true;
            }
            else if (closed && pts.Count >= 2 &&
                     pts[0].pt.GetDistanceTo(pts[pts.Count - 1].pt) < SnapTol)
            {
                pts.RemoveAt(pts.Count - 1);   // 真闭合但带重复末点：零长收口段，去重
            }
        }

        /// <summary>捕捉画闭的假开放线：Closed=false 但首末点重合。</summary>
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

        // ---- 小工具 ----
        /// <summary>退路：从上一圈的每条线各偏一个增量，汇成新一圈。</summary>
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
        //  ChainSegments（散线串链）：一把散直线段 → 有序开链/闭环多段线
        //  端点按容差聚类成节点；度=2 节点串链通过，度=1（自由端）与
        //  度≥3（三岔口）处断链。接头处两端点不重合时优先取两线延长
        //  交点（同时吃掉搭不齐与画过头两种手抖），近平行退回两端点中点。
        //  输出纯直段多段线，归圆由 Smooth 接手。
        // ============================================================
        public sealed class SegChain
        {
            public Polyline Pl;           // 串好的多段线（纯直段，未归圆）
            public List<int> SegIndices;  // 参与的输入线段下标（按链序）
            public int FittedJoints;      // 端点不重合、按交点/中点续上的接头数
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

            // 1) 端点聚类成节点（代表点取成员均值；段数少，O(n²) 足够）
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

            // 2) 邻接表（零长/自环段丢弃）
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

            // 段的行进端点：rev=false 段沿 A→B 走
            Point2d Head(int s, bool rev) => rev ? segs[s].B : segs[s].A;
            Point2d Tail(int s, bool rev) => rev ? segs[s].A : segs[s].B;

            // 接头点：前段行进终点 e1 与后段行进起点 p2
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
                // 近平行时交点会飞远——离节点太远就退回中点
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

            // 3) 先从度≠2 的节点出发扫开链，剩下没用过的段必属纯闭环
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

        /// <summary>多段线里 bulge 非零的段数。</summary>
        public static int CountArcs(Polyline pl)
        {
            int c = 0;
            for (int i = 0; i < pl.NumberOfVertices; i++)
                if (Math.Abs(pl.GetBulgeAt(i)) > Tol) c++;
            return c;
        }
    }
}
