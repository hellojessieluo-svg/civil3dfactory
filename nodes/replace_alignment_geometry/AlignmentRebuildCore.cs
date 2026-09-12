#nullable disable   // 本文件被 Civil3DFactory（可空关）与 WaterBox（可空开）两个工程共同编译，按关处理

using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlign = Autodesk.Civil.DatabaseServices.Alignment;
using CivAlignEnts = Autodesk.Civil.DatabaseServices.AlignmentEntityCollection;

namespace Civil3DFactory.Geometry
{
    /// <summary>
    /// 原位重写路线几何的算法核心：清空实体集，按多段线逐段重建。
    /// 路线的 ObjectId / 名字 / 样式 / 站号参考都不动，因此走廊、偏移路线、纵断面、
    /// 采样线组等按 ObjectId 挂接的依赖对象全部保留（动态依赖会按新几何自行重建）。
    /// 代价：原有 PI/约束布线被替换成全 Fixed 实体（固定直线/固定圆弧）。
    ///
    /// 唯一真源：Civil3DFactory（节点 replace_alignment_geometry）与 products\waterbox
    /// （C3DF-ReplaceCenterline/HZX 命令）共同编译本文件，改算法只改这里。
    ///
    /// 几何映射与 alignment_to_polyline 互逆：
    ///   bulge=0 → AddFixedLine；bulge≠0 → AddFixedCurve(两端点+半径+顺逆)，
    ///   半径 = 弦长/(2·sin(θ/2))，θ = 4·atan|bulge|，bulge&lt;0 为顺时针。
    ///   两点+半径只能表达劣弧，θ≥180° 的段先从弧中点劈半再各自成弧。
    /// </summary>
    public static class AlignmentRebuildCore
    {
        /// <summary>重建结果统计。</summary>
        public sealed class RebuildResult
        {
            public double OldLength;
            public double NewLength;
            public double PolylineLength;
            public int OldEntities;
            public int NewEntities;
            public int Lines;
            public int Arcs;
            public int ArcSplits;
            /// <summary>多段线画向与原路线桩号方向相反，已自动反向重建。</summary>
            public bool Reversed;
        }

        /// <summary>
        /// 用多段线几何原位重写路线。al 必须已 ForWrite 打开；pl 只读。
        /// 桩号方向跟原路线不跟画线手势：新线哪端贴近原路线起点，哪端就当起点
        /// （两种取向的端点距离和判定，路线整体挪位也适用），判定要反则顶点倒序、
        /// bulge 取负后重建，Reversed 置 true。
        /// 多段线闭合 / 顶点不足 / 全部重合时抛 InvalidOperationException。
        /// </summary>
        public static RebuildResult RebuildFromPolyline(CivAlign al, Polyline pl)
        {
            if (pl.Closed)
                throw new InvalidOperationException("闭合多段线不能作为路线几何。");
            int nv = pl.NumberOfVertices;
            if (nv < 2)
                throw new InvalidOperationException("多段线顶点少于 2 个。");

            // 顶点表先取出来，方向判定后可能整体反向
            var pts = new Point2d[nv];
            var bulges = new double[nv];
            for (int i = 0; i < nv; i++)
            {
                pts[i] = pl.GetPoint2dAt(i);
                bulges[i] = pl.GetBulgeAt(i);
            }

            var r = new RebuildResult
            {
                OldLength = al.Length,
                PolylineLength = pl.Length,
                Reversed = ShouldReverse(al, pts)
            };

            if (r.Reversed)
            {
                var rp = new Point2d[nv];
                var rb = new double[nv];
                for (int i = 0; i < nv; i++) rp[i] = pts[nv - 1 - i];
                for (int j = 0; j < nv - 1; j++) rb[j] = -bulges[nv - 2 - j];   // 段倒走，弧向取负
                rb[nv - 1] = 0;
                pts = rp;
                bulges = rb;
            }

            CivAlignEnts ents = al.Entities;
            r.OldEntities = ents.Count;
            ents.Clear();

            for (int i = 0; i < nv - 1; i++)
            {
                if (pts[i].GetDistanceTo(pts[i + 1]) < 1e-6) continue;   // 重合点，跳过
                AddSegment(ents, pts[i], pts[i + 1], bulges[i], r, 0);
            }
            if (ents.Count == 0)
                throw new InvalidOperationException("多段线没有有效线段（顶点全部重合？）。");

            r.NewEntities = ents.Count;
            r.NewLength = al.Length;
            return r;
        }

        /// <summary>新线取向判定：保持取向与反向取向，哪个的（起-起 + 终-终）距离和小用哪个。
        /// 原路线几何取不到（零长等）时按不反向处理。</summary>
        static bool ShouldReverse(CivAlign al, Point2d[] pts)
        {
            try
            {
                double e0 = 0, n0 = 0, e1 = 0, n1 = 0;
                al.PointLocation(al.StartingStation, 0.0, ref e0, ref n0);
                al.PointLocation(al.EndingStation, 0.0, ref e1, ref n1);
                var os = new Point2d(e0, n0);
                var oe = new Point2d(e1, n1);
                Point2d ps = pts[0], pe = pts[pts.Length - 1];
                double keep = ps.GetDistanceTo(os) + pe.GetDistanceTo(oe);
                double flip = pe.GetDistanceTo(os) + ps.GetDistanceTo(oe);
                return flip < keep;
            }
            catch
            {
                return false;
            }
        }

        static void AddSegment(CivAlignEnts ents, Point2d s, Point2d e, double bulge,
            RebuildResult r, int depth)
        {
            if (Math.Abs(bulge) < 1e-9)
            {
                ents.AddFixedLine(new Point3d(s.X, s.Y, 0.0), new Point3d(e.X, e.Y, 0.0));
                r.Lines++;
                return;
            }
            double theta = 4.0 * Math.Atan(Math.Abs(bulge));
            if (theta > Math.PI * 0.999 && depth < 8)
            {
                // 两点+半径的 AddFixedCurve 只能取劣弧，≥180° 的段从弧中点劈半
                Point2d mid = BulgeMidPoint(s, e, bulge);
                double half = Math.Tan(theta / 8.0) * Math.Sign(bulge);
                r.ArcSplits++;
                AddSegment(ents, s, mid, half, r, depth + 1);
                AddSegment(ents, mid, e, half, r, depth + 1);
                return;
            }
            double chord = s.GetDistanceTo(e);
            double radius = chord / (2.0 * Math.Sin(theta / 2.0));
            ents.AddFixedCurve(new Point3d(s.X, s.Y, 0.0), new Point3d(e.X, e.Y, 0.0),
                radius, bulge < 0.0);
            r.Arcs++;
        }

        /// <summary>bulge 段的弧中点：弦中点 − 左法向 × (bulge·弦长/2)。
        /// 验证例：(0,0)→(1,0) bulge=1（半圆，逆时针）中点应为 (0.5,−0.5)。</summary>
        static Point2d BulgeMidPoint(Point2d s, Point2d e, double b)
        {
            double dx = e.X - s.X, dy = e.Y - s.Y;
            double chord = Math.Sqrt(dx * dx + dy * dy);
            double ux = dx / chord, uy = dy / chord;
            double mx = (s.X + e.X) / 2.0, my = (s.Y + e.Y) / 2.0;
            double sag = b * chord / 2.0;
            return new Point2d(mx + uy * sag, my - ux * sag);
        }
    }
}
