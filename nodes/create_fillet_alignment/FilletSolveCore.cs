#nullable disable
using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace Civil3DFactory.Geometry
{
    /// <summary>
    /// 转角切弧解析核（唯一真源）：两条路线各给一个拾取点 → 固定腿+相切弧的全部几何。
    /// 拾取点承担两件事：定连接侧（消象限歧义——2026-08-23 全天的教训）、定腿的朝向。
    /// 纯几何闭式解，不经原生连接路线求解器。
    /// </summary>
    public static class FilletSolveCore
    {
        public class Result
        {
            public Point3d Line1Start, Line1End;   // 腿1：外端 → 切点T1（沿行进方向）
            public Point3d Line2Start, Line2End;   // 腿2：切点T2 → 外端
            public double StationA, StationB;      // 两切点在各自母线上的桩号（取不到=-1）
            public bool CornerBeyondA, CornerBeyondB; // 角点在切点的桩号增方向侧？（收口判端用）
            public Point3d Corner;                 // 两切线交点（尖角点）
            public double ArcLength, CornerAngleDeg;
            public string Error;                   // 非空=失败原因
        }

        /// <param name="mirrorB">镜像：B 侧腿在角点处反向（弧解到补角象限，另一侧同尺寸切弧）。
        /// 不是"同切点大弧"——那是气球圈，2026-08-23 深夜用户实图否掉。</param>
        public static Result Solve(CivAlignment a, Point3d pickA,
                                   CivAlignment b, Point3d pickB,
                                   double radius, double leg, bool mirrorB = false)
        {
            var r = new Result();
            try
            {
                // 拾取点未必垂足在路线桩号范围内（点在端头外侧 StationOffset 抛
                // PointNotOnEntity）——先把点吸到线上，再不行就粗扫细化。
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
                    if (v.Length < 1e-9) { r.Error = "取向失败：拾取点太靠路线端部。"; return default; }
                    return v / v.Length;
                }

                Point3d P1 = Pt(a, sa), P2 = Pt(b, sb);
                Vector3d u1 = Dir(a, sa), u2 = Dir(b, sb);
                if (r.Error != null) return r;

                // 两条局部直线求交（视拾取点附近为直线段——转角处的常态）
                double det = u1.X * (-u2.Y) - u1.Y * (-u2.X);
                if (Math.Abs(det) < 1e-9) { r.Error = "两条路线在拾取处近乎平行，倒不了角。"; return r; }
                double dx = P2.X - P1.X, dy = P2.Y - P1.Y;
                double t1 = (dx * (-u2.Y) - dy * (-u2.X)) / det;
                Point3d C = new Point3d(P1.X + t1 * u1.X, P1.Y + t1 * u1.Y, 0);

                Vector3d d1 = P1 - C, d2 = P2 - C;   // 角点指向各自拾取点＝腿的朝向
                if (d1.Length < 0.5 || d2.Length < 0.5)
                { r.Error = "拾取点离交点太近（<0.5m），请在离角远一点的位置点取。"; return r; }
                d1 /= d1.Length; d2 /= d2.Length;
                if (mirrorB) d2 = -d2;   // 镜像：B 侧腿反向，弧落到另一侧（补角象限）

                double cos = Math.Max(-1, Math.Min(1, d1.DotProduct(d2)));
                double theta = Math.Acos(cos);
                r.CornerAngleDeg = theta * 180 / Math.PI;
                if (theta < 0.09 || theta > Math.PI - 0.09)
                { r.Error = $"夹角退化（{r.CornerAngleDeg:0.0}°），倒不了角。"; return r; }

                double t = radius / Math.Tan(theta / 2);
                r.Corner = C;
                Point3d T1 = C + d1 * t, T2 = C + d2 * t;
                r.Line1Start = T1 + d1 * leg; r.Line1End = T1;
                r.Line2Start = T2; r.Line2End = T2 + d2 * leg;
                r.ArcLength = radius * (Math.PI - theta);

                double sta = 0, off = 0;
                try { a.StationOffset(T1.X, T1.Y, ref sta, ref off); r.StationA = sta; }
                catch { r.StationA = -1; }   // 切点在母线端头外（腿伸出去的情形）
                try { b.StationOffset(T2.X, T2.Y, ref sta, ref off); r.StationB = sta; }
                catch { r.StationB = -1; }
                // 角在切点的哪一头：切点处桩号增方向 与 切点→角点 的点积（几何判定，零歧义）
                if (r.StationA >= 0)
                    r.CornerBeyondA = Dir(a, r.StationA).DotProduct(C - T1) > 0;
                if (r.StationB >= 0)
                    r.CornerBeyondB = Dir(b, r.StationB).DotProduct(C - T2) > 0;
            }
            catch (System.Exception ex)
            {
                r.Error = "解算异常：" + ex.Message;
            }
            return r;
        }

        /// <summary>
        /// 收口（点对点契约，2026-08-23 定）：偏移段的临角区间端**精确**落到转角路线的
        /// 腿外端（Result.Line1Start / Line2End），段尾点 == 角首点，零搭接零缝。
        /// 传坐标不传桩号——偏移路线自家桩号与母线帧在弯道上差几米（老妖怪），
        /// 内部一律用母线 StationOffset 换算到母线帧后再改区间端。seg 须已 ForWrite。
        /// 返回报账文本；非偏移路线/会吃光区间时不动并说明。
        /// </summary>
        public static string SnapOffsetEnd(Transaction tr, CivAlignment seg,
                                           Point3d legOuterPt, Point3d cornerPt)
        {
            Autodesk.Civil.DatabaseServices.OffsetAlignmentInfo info;
            try { info = seg.OffsetAlignmentInfo; } catch { info = null; }
            if (info == null) return seg.Name + "：非偏移路线，端头未收";

            CivAlignment parent;
            try { parent = (CivAlignment)tr.GetObject(info.ParentAlignmentId, OpenMode.ForRead); }
            catch { return seg.Name + "：取不到母线，端头未收"; }

            double psta = 0, poff = 0;
            try { parent.StationOffset(legOuterPt.X, legOuterPt.Y, ref psta, ref poff); }
            catch { return seg.Name + "：腿外端超出母线范围，端头未收"; }

            // 判端基准＝角点桩号（不是腿外端）：镜像后腿外端会跑到角另一侧，
            // 用"角在切点哪一头"的点积判端会判反——离角最近的那个区间端才是要动的端。
            double pstaRef = psta;
            try
            {
                double pc = 0, po = 0;
                parent.StationOffset(cornerPt.X, cornerPt.Y, ref pc, ref po);
                pstaRef = pc;
            }
            catch { }   // 角点超出母线范围就退回用腿外端当基准

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
            if (best < 0) return seg.Name + "：无区间，端头未收";
            var reg = regions[best];
            bool moveStart = Math.Abs(pstaRef - reg.StartStation) <= Math.Abs(pstaRef - reg.EndStation);
            if (moveStart)
            {
                if (psta > reg.EndStation - 1) return seg.Name + "：收口会吃光区间，未收";
                double old = reg.StartStation;
                reg.StartStation = psta;
                return $"{seg.Name} 起点 {old:0.00}→{psta:0.00}";
            }
            else
            {
                if (psta < reg.StartStation + 1) return seg.Name + "：收口会吃光区间，未收";
                double old = reg.EndStation;
                reg.EndStation = psta;
                return $"{seg.Name} 终点 {old:0.00}→{psta:0.00}";
            }
        }
    }
}
