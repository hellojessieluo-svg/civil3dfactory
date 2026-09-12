#nullable disable   // 本文件被 WaterBox（可空开）链接编译，将来 Civil3DFactory 节点化时共用，按关处理

using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory.Geometry
{
    /// <summary>
    /// 放坡核心（纯几何，不依赖 DB 与交互）：沿一串带高程的采样点，向指定一侧
    /// 按坡比 1:n 放坡到目标高程 z1，产出坡脚线顶点序列 + 长短相间示坡线。
    /// 偏移量逐点变（= n·|z-z1|），顶点走角平分线并带斜接系数；坡脚线做自交环
    /// 消除与共线抽稀。唯一真源：products\waterbox 的 C3DF-SlopeToElev 命令链接
    /// 编译本文件；将来 Civil3DFactory 节点同样链接，改算法只改这里。
    /// </summary>
    public static class SlopeCastCore
    {
        const double ZeroDz = 1e-4;        // 高差小于此视为零宽点（线正好在目标高程上）
        const double MinCombWidth = 0.05;  // 坡宽小于此不画示坡线
        const double WeedTol = 0.01;       // 坡脚线共线抽稀容差
        const int MaxLoops = 200;          // 自交环消除次数上限（防病态输入死循环）
        const int MaxCombs = 20000;        // 示坡线根数上限（防间距误输过小）

        /// <summary>一根示坡线：Top 恒在坡的高侧，End 指向低侧（长划从高处画向低处）。</summary>
        public sealed class Comb
        {
            public Point3d Top;
            public Point3d End;
            public bool Full;   // true=长线（到坡脚），false=短线（半宽）
        }

        public sealed class CastResult
        {
            public List<Point3d> Toe = new List<Point3d>();   // 坡脚线顶点，Z 全为目标高程
            public List<Comb> Combs = new List<Comb>();
            public int LoopsRemoved;                          // 消除的自交环数
            public int CombsSkipped;                          // 落在自交清理区被跳过的示坡线
            public double MinWidth = double.MaxValue;         // 坡带水平宽度范围
            public double MaxWidth;
            public int ZeroPoints;                            // 零宽采样点数（线穿过目标高程处）
            public double CutLength;                          // 线高于目标高程的沿线长度（挖）
            public double FillLength;                         // 线低于目标高程的沿线长度（填）
            public bool AllZero;                              // 整条线都在目标高程，无坡可放
            public bool TooShort;                             // 有效采样不足 2 点
            public int Direction;                             // +1 = 向下放坡（源线高于目标）；-1 = 向上放坡
            public bool Mixed;                                // 本段内同时存在高于/低于目标的采样
        }

        /// <summary>
        /// samples：沿线有序采样点（含高程，需已按足够密度采样，顶点必采）。
        /// closed：是否闭合环（首尾点可重合也可不重合）。
        /// side：+1 = 前进方向左侧放坡，-1 = 右侧。
        /// slopeN：坡比 1:n 的 n。z1：目标高程。combSpacing：示坡线沿线间距。
        /// </summary>
        public static CastResult Cast(IList<Point3d> samples, bool closed, int side,
            double slopeN, double z1, double combSpacing)
        {
            var res = new CastResult();

            // ---- 0. 水平投影去重 ----
            var pts = new List<Point3d>(samples.Count);
            foreach (var p in samples)
                if (pts.Count == 0 || Dist2d(pts[pts.Count - 1], p) > 1e-6) pts.Add(p);
            if (closed && pts.Count > 1 && Dist2d(pts[0], pts[pts.Count - 1]) < 1e-6)
                pts.RemoveAt(pts.Count - 1);
            int n = pts.Count;
            if (n < 2) { res.TooShort = true; return res; }
            int segCount = closed ? n : n - 1;

            // ---- 1. 分段方向 / 长度 / 桩号 ----
            var segDir = new Point2d[segCount];   // 单位方向向量（借 Point2d 存）
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

            // ---- 2. 顶点偏移方向（角平分）与斜接系数 ----
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
                    if (sl < 1e-6) { nx = nnx; ny = nny; }   // 180° 折返，退化取后段法向
                    else
                    {
                        nx = sx / sl; ny = sy / sl;
                        double cosHalf = nx * nnx + ny * nny;
                        sc = Math.Min(1.0 / Math.Max(cosHalf, 0.5), 2.0);   // 斜接，封顶 2 倍
                    }
                }
                else if (hasNext) { nx = nnx; ny = nny; }
                else { nx = pnx; ny = pny; }
                offX[i] = nx; offY[i] = ny; miter[i] = sc;
            }

            // ---- 3. 逐点宽度与生坯坡脚点 ----
            var rawToe = new List<Point2d>(n);
            var orig = new List<int>(n);   // 生坯点对应采样序号；-1 = 自交清理插入点
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

            // ---- 4. 示坡线（基于生坯坡脚，长短相间；记桥接序号供清理后筛选） ----
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
                if (w < MinCombWidth) continue;   // 零宽区不画，不计跳过
                bool full = idx % 2 == 0;
                var mid = new Point3d((top.X + toeEnd.X) / 2, (top.Y + toeEnd.Y) / 2, (top.Z + z1) / 2);
                // 长划恒从高处指向低处：源线高于目标时高侧在源线，反之高侧在生成线（坡顶线）
                bool srcHigh = top.Z - z1 > 0;
                var hi = srcHigh ? top : toeEnd;
                var lo = srcHigh ? toeEnd : top;
                combsRaw.Add(new Comb { Top = hi, End = full ? lo : mid, Full = full });
                brackets.Add(new[] { iA, iB });
            }

            // ---- 5. 坡脚线自交环消除（凹侧变距偏移的打结段裁掉） ----
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

            // ---- 6. 示坡线定稿：桥接采样点仍在世的保留 ----
            for (int i = 0; i < combsRaw.Count; i++)
            {
                if (dead.Contains(brackets[i][0]) || dead.Contains(brackets[i][1]))
                { res.CombsSkipped++; continue; }
                res.Combs.Add(combsRaw[i]);
            }

            // ---- 7. 共线抽稀后输出坡脚线 ----
            foreach (int i in Weed(rawToe, WeedTol))
                res.Toe.Add(new Point3d(rawToe[i].X, rawToe[i].Y, z1));
            if (res.MinWidth == double.MaxValue) res.MinWidth = 0;
            return res;
        }

        /// <summary>
        /// 按"源线高于/低于目标高程"把线切成同向段，逐段放坡。返回沿线顺序的成果，
        /// 每段 Direction：+1 = 向下放坡（源线在上，产物是坡脚线），
        /// -1 = 向上放坡（源线在下，产物是坡顶线）。
        /// 线穿过目标高程处插入零宽交点，两侧段各自在此收敛到源线上。
        /// 全线同向时只返回一条并保留闭合性；全线持平返回单条 AllZero。
        /// </summary>
        public static List<CastResult> CastSplit(IList<Point3d> samples, bool closed, int side,
            double slopeN, double z1, double combSpacing)
        {
            var outList = new List<CastResult>();

            // 与 Cast 同款水平去重，保证符号数组与几何一一对应
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

            // 全线同向：一次放坡，闭合性保留
            if (!(hasUp && hasDown))
            {
                var one = Cast(pts, closed, side, slopeN, z1, combSpacing);
                one.Direction = hasDown ? 1 : -1;
                if (!one.TooShort && !one.AllZero) outList.Add(one);
                return outList;
            }

            // 混向：闭合线先旋转到一个变号边界，之后一律按开线切段
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
                order.Add(b);   // 绕回起点，闭合线末段不丢
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
                // 变号：插高程 == z1 的交点，作两段共用的零宽端
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

        /// <summary>两点间按高程线性插值出 z==z1 的点；高差退化时取前一点的平面位置。</summary>
        static Point3d CrossAtZ(Point3d a, Point3d b, double z1)
        {
            double dz = b.Z - a.Z;
            if (Math.Abs(dz) < 1e-12) return new Point3d(a.X, a.Y, z1);
            double f = (z1 - a.Z) / dz;
            if (f < 0) f = 0;
            if (f > 1) f = 1;
            return new Point3d(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, z1);
        }

        // ---- 几何小件 ----

        static double Dist2d(Point3d a, Point3d b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        static Point3d Lerp3(Point3d a, Point3d b, double f)
            => new Point3d(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, a.Z + (b.Z - a.Z) * f);

        static Point2d Lerp2(Point2d a, Point2d b, double f)
            => new Point2d(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);

        /// <summary>线段真交（端点相触不算），交点从参数解出。</summary>
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

        /// <summary>顺序抽稀：锚点到候选弦内所有中间点偏差不超容差则删。返回保留序号。</summary>
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
