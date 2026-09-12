#nullable disable   // 与 OffsetConeCore 同口径：Civil3DFactory（可空关）与 WaterBox（可空开）共同编译

using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory.Geometry
{
    /// <summary>
    /// 成田算法核心（纯 DB/几何，无交互）。唯一真源：
    /// Civil3DFactory（节点 make_parcels）与 products\waterbox（C3DF-MakeParcels/CT）共同编译本文件。
    /// 链路：中心线端头贴边外延 → 双侧偏移+平头封口成带 → 带并集 →
    /// （外圈−岛洞）−带 = 台田 → 尖角甄别归圆（OffsetConeCore.RoundSharpCorners）。
    /// 入参多段线由调用方保证：内存克隆、Elevation=0、边界已 Closed。
    /// </summary>
    public static class MakeParcelsCore
    {
        const double JoinTol = 1e-6;

        public sealed class ParcelResult
        {
            public List<Polyline> Parcels = new List<Polyline>();      // 已归圆，调用方入库并设图层
            public List<Polyline> ChannelLoops = new List<Polyline>(); // 水道范围闭合环 = 范围−圆角台田（外环+岛环，可直接 HATCH）
            public double ChannelArea;                                 // 水道面积（Region 真值）
            public List<string> Warnings = new List<string>();
            public int Extended;                                       // 端头外延的中心线条数
        }

        /// <summary>处理一个外圈：holes 是它的岛洞，centerlines 是归属它的中心线。
        /// halfWs 给了就逐中心线取宽（与 centerlines 对齐；null=全用 halfW）。
        /// 任一条中心线成带失败抛 InvalidOperationException（宁可不出不出错图）。</summary>
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
                        throw new InvalidOperationException($"中心线 #{i + 1} 成带失败：{err}");
                    bandLoops.Add(band);
                }

                if (bandLoops.Count == 0)
                    throw new InvalidOperationException("这一片没有任何中心线成带。");

                for (int i = 0; i < bandLoops.Count; i++)
                {
                    try
                    {
                        if (bandsAll == null) bandsAll = ToRegion(bandLoops[i]);
                        else using (var r2 = ToRegion(bandLoops[i]))
                                bandsAll.BooleanOperation(BooleanOperationType.BoolUnite, r2);
                    }
                    catch (System.Exception ex)
                    { throw new InvalidOperationException($"带 #{i + 1} 成域失败：{ex.Message}；{DumpPl(bandLoops[i])}"); }
                }

                try { baseRegion = ToRegion(outer); }
                catch (System.Exception ex)
                { throw new InvalidOperationException($"外圈成域失败：{ex.Message}"); }
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
                        result.Warnings.Add($"台田 #{idx} 有 {st.Spanned} 处短边撑不下 R{filletR:0.###}，弧已跨过短边连角一并吞掉（足额半径）");
                    if (st.Downgraded > 0)
                        result.Warnings.Add($"台田 #{idx} 有 {st.Downgraded} 处角放不下 R{filletR:0.###}，已按现场最大半径倒（最小 R{st.MinUsedR:0.#}）");
                    if (st.CantFit > 0)
                        result.Warnings.Add($"台田 #{idx} 有 {st.CantFit} 处角连 R{filletR / 32:0.##} 都放不下，保留尖角");
                }

                // 水道范围 = 范围 −（圆角后的台田）：闭合、贴圆角，HATCH/算面积用
                using (var chan = (Region)baseRegion.Clone())
                {
                    foreach (Polyline p in result.Parcels)
                        using (var pr = ToRegion(p))
                            chan.BooleanOperation(BooleanOperationType.BoolSubtract, pr);
                    result.ChannelArea = chan.Area;
                    result.ChannelLoops = RegionLoops(chan);
                    // 碎环过滤：布尔把圈弧折线化后，台田贴圈边与真弧之间会剩发丝月牙
                    // （数米长、毫米宽、面积个位数 m²，2026-08-24 项目B实测 7 条）——丢弃并报账
                    for (int i = result.ChannelLoops.Count - 1; i >= 0; i--)
                    {
                        double a2 = 0;
                        try { a2 = Math.Abs(result.ChannelLoops[i].Area); } catch { }
                        if (a2 < 25.0)
                        {
                            result.Warnings.Add($"丢弃水道碎环（面积 {a2:0.0}m²，圈弧折线化月牙屑）");
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

        // ---- 端头外延量：端点离任一范围环 ≤ 2×半宽 才外延（贴边的捅穿，深居域内的死端不动） ----
        public static double EndExtension(Point2d end, List<Polyline> boundaries, double halfW)
            => EndExtension(end, default, boundaries, halfW);

        /// <summary>端头外延量。dir=中心线在该端的外向向量（给了就按斜角精确算：
        /// 斜口时带子远角要多伸 halfW·tan(斜角)，不够就在口部与圈之间剩小楔子/飘弧——
        /// 2026-08-24 项目B口部实测病）。不给 dir 退回老口径（距离+半宽）。</summary>
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
            if (best > halfW * 4) return 0;                    // 内部端（接别的通道），不外延
            if (hit == null || dir.Length < 1e-9)
                return best + halfW;                           // 老口径兜底
            try
            {
                var t3 = hit.GetFirstDerivative(q);            // 圈局部切向
                var t2 = new Vector2d(t3.X, t3.Y);
                if (t2.Length < 1e-9) return best + halfW;
                t2 = t2.GetNormal();
                var u = dir.GetNormal();
                var n = new Vector2d(-t2.Y, t2.X);             // 圈局部法向
                double nu = Math.Abs(u.DotProduct(n));
                if (nu < 0.1) return best + halfW * 6;         // 近平行擦边，给大余量截断
                var p = new Vector2d(-u.Y, u.X);               // 带宽方向
                double np = Math.Abs(p.DotProduct(n));
                return best / nu + halfW * np / nu + 2.0;      // 沿线到圈 + 斜角补偿 + 余量
            }
            catch { return best + halfW; }
        }

        // ---- 端头沿末段弦向外延（外延段在范围线外，形状会被布尔裁掉，弦向足够） ----
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

        // ---- 中心线 → 封闭带：左右各偏半宽，首尾平头封口拼成一个闭环 ----
        // 偏移自算（不走 GetOffsetCurves——accore 对开放带弧多段线返回空集，实测坑）：
        // GY 产物 G1 相切连续，直段平移、弧段同心变半径、bulge 不变，端点解析重合；
        // 未归圆的小折角处两侧点都保留（微型倒棱），布尔无感。
        public static Polyline BuildBand(Polyline cl, double halfW, out string err)
        {
            err = null;
            Polyline left = ManualOffset(cl, halfW, out string dl), right = ManualOffset(cl, -halfW, out string dr);
            if (left == null || right == null)
            {
                left?.Dispose(); right?.Dispose();
                err = $"偏移失败（左侧:{dl ?? "ok"}；右侧:{dr ?? "ok"}；顶点 {cl.NumberOfVertices}）";
                return null;
            }
            var band = new Polyline(left.NumberOfVertices + right.NumberOfVertices);
            int vi = 0;
            for (int i = 0; i < left.NumberOfVertices; i++)
            {
                double b = i < left.NumberOfVertices - 1 ? left.GetBulgeAt(i) : 0;   // 末点接封口直线
                band.AddVertexAt(vi++, left.GetPoint2dAt(i), b, 0, 0);
            }
            for (int i = right.NumberOfVertices - 1; i >= 0; i--)
            {
                double b = i > 0 ? -right.GetBulgeAt(i - 1) : 0;                     // 反向段 bulge 取负；首点闭合封口
                band.AddVertexAt(vi++, right.GetPoint2dAt(i), b, 0, 0);
            }
            band.Closed = true;
            left.Dispose(); right.Dispose();
            if (Math.Abs(band.Area) < 1e-6) { band.Dispose(); err = "带面积为零"; return null; }
            return band;
        }

        // ---- 解析偏移：d>0 向行进方向左侧偏。要求开放多段线；弧段半径 ≤ |d| 时报错。
        // 相切接头（GY 圆角产物）端点解析重合；未归圆的直-直小折角走斜接（miter，
        // 两条偏移线求交），避免倒棱在折角内侧造出微小自交、Region 不收 ----
        public static Polyline ManualOffset(Polyline src, double d, out string diag)
        {
            diag = null;
            int n = src.NumberOfVertices;
            if (n < 2) { diag = "顶点不足"; return null; }

            // 每段的偏移原语：端点、bulge、是否弧
            // 碎段阈值取工程尺度：毫米级碎段的方向是画图噪声，偏移后会在带上
            // 造出微型乱折让 Region 拒收（实测 7mm 碎段翻车），跳过后由接头斜接补上
            double minSeg = Math.Max(0.01, Math.Abs(d) / 1000);
            var segs = new List<(Point2d q1, Point2d q2, double bulge, bool arc)>();
            for (int i = 0; i < n - 1; i++)
            {
                Point2d p1 = src.GetPoint2dAt(i), p2 = src.GetPoint2dAt(i + 1);
                double b = src.GetBulgeAt(i);
                Vector2d chord = p2 - p1;
                double ch = chord.Length;
                if (ch < minSeg) continue;                     // 碎段跳过
                var nl = new Vector2d(-chord.Y / ch, chord.X / ch);   // 行进左法向

                if (Math.Abs(b) < 1e-9)
                    segs.Add((p1 + nl * d, p2 + nl * d, 0, false));
                else
                {
                    double r = ch * (1 + b * b) / (4 * Math.Abs(b));
                    double rNew = r - Math.Sign(b) * d;        // 左偏：CCW 弧向心收，CW 弧离心放
                    if (rNew < 1e-6) { diag = $"第 {i + 1} 段弧半径 {r:0.##} 容不下偏移 {Math.Abs(d):0.##}"; return null; }
                    var m = new Point2d((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
                    // 圆心在弦中点左法向上的带号距离。符号用标准四分之一圆核过：
                    // p1=(1,0)→p2=(0,1) b=tan22.5° 的 CCW 弧圆心必须是 (0,0)——
                    // 正 bulge（CCW）圆心在行进左侧、弧顶在右侧，别再记反。
                    double t = (ch / 4) * (1 / b - b);
                    Point2d o = m + nl * t;
                    double k = rNew / r;
                    // 同心缩放，扫角不变 → bulge 不变
                    segs.Add((o + (p1 - o) * k, o + (p2 - o) * k, b, true));
                }
            }
            if (segs.Count == 0) { diag = "偏移后没有有效段"; return null; }

            // 接头处理：相切→共点；直-直折角→斜接；带弧折角→中点凑合（GY 后不该出现）
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

            // 接头两侧已严格共点：顶点 = 首段起点 + 各段终点，bulge 属于起始顶点
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
            if (r == null) throw new InvalidOperationException("Region 创建失败（环自相交？）");
            return r;
        }

        // ---- Region → 闭合多段线环：递归炸开收集边曲线，按端点串环，弧段转 bulge ----
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
                            // ACIS 布尔后圆弧边常以"椭圆弧"炸出——老代码不是 Arc 就默默拉弦，
                            // 把大半径长弧削掉一条矢高（项目B台田贴圈边实测 1~2m 漂移）。
                            // 注意不能用密集直段兜底：下游归圆的共线合并(0.5°)会把微折角
                            // 密集直段重新并成长弦，白修。先三点定圆拟合出真 bulge 弧，
                            // 拟合不上（真样条）才退密集采样。
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
                                        // 容差 5cm：ACIS 有时用样条近似圆弧，3mm 会拒真弧
                                        // →退密集直段→又被归圆共线合并并回长弦（矢高级错误）。
                                        // 5cm 的"圆弧化误差"远小于弦化的米级矢高。
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
                                // 整段不是单圆（ACIS 会把相切的弧+直+弧并成一条复合样条边）——
                                // 逐 ~5m 分段三点定弧出带微 bulge 的小弧串。不能出纯直段：
                                // 下游归圆的共线合并(0.5°)会把密集直段并回长弦（bulge≠0 才受保护）。
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
                                        // 中点到弦的带号距离 h → bulge = 2h/chord 的一阶弧近似的精确式：
                                        // R=(c²/4+h²)/(2h), sweep=4·atan(2h/c)… 直接用 bulge=2h/c 对小段弧即为精确
                                        double hx = (B2.X - A2.X) / chord, hy = (B2.Y - A2.Y) / chord;
                                        double h = (M2.X - A2.X) * (-hy) + (M2.Y - A2.Y) * hx;   // 中点在弦左侧的带号距离
                                        bulge2 = 2 * h / chord;   // tan(sweep/4)=2h/c，三点定弧精确式
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
                    // 整圆独立成环：两个半圆 bulge=1
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
