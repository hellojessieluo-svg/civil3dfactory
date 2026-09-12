#nullable disable   // 本文件被 Civil3DFactory（可空关）与 WaterBox（可空开）两个工程共同编译，按关处理

using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlign = Autodesk.Civil.DatabaseServices.Alignment;
using CivSampleLine = Autodesk.Civil.DatabaseServices.SampleLine;
using CivSampleLineGroup = Autodesk.Civil.DatabaseServices.SampleLineGroup;

namespace Civil3DFactory.Geometry
{
    /// <summary>
    /// 采样线刷新算法核心（换中线后采样线跟新几何走，采样线组不动）。
    /// 组对象、组名、采样源设置全保留——只把组里的旧采样线删光，沿路线新几何
    /// 按间距重建垂直采样线；末端不足半米补终点线（与 create_sample_lines 同规则）。
    /// 唯一真源：Civil3DFactory（节点 refresh_sample_lines）与 products\waterbox
    /// （C3DF-RefreshSampleLines/CYX）共同编译本文件。
    /// </summary>
    public static class SampleLineRefreshCore
    {
        public sealed class GroupRefresh
        {
            public string GroupName;
            public int OldLines;
            public int NewLines;
        }

        /// <summary>al 只读即可；组会在内部 ForWrite 打开。组数为零抛异常。</summary>
        public static List<GroupRefresh> Refresh(CivAlign al, Transaction tr,
            double interval, double swath)
        {
            if (interval <= 0) throw new InvalidOperationException("间距必须大于 0。");
            if (swath <= 0) throw new InvalidOperationException("采样宽度必须大于 0。");

            ObjectIdCollection gids = al.GetSampleLineGroupIds();
            if (gids.Count == 0)
                throw new InvalidOperationException("路线 '" + al.Name + "' 没有采样线组。");

            // 站号序列：间距格点 + 终点（末段不足 0.5 并入终点线）
            double start = al.StartingStation, end = al.EndingStation;
            var stations = new List<double>();
            for (double st = start; st < end - 0.001; st += interval) stations.Add(st);
            if (stations.Count == 0 || end - stations[stations.Count - 1] > 0.5) stations.Add(end);

            var results = new List<GroupRefresh>();
            foreach (ObjectId gid in gids)
            {
                var group = (CivSampleLineGroup)tr.GetObject(gid, OpenMode.ForWrite);
                var r = new GroupRefresh { GroupName = group.Name };

                foreach (ObjectId sid in group.GetSampleLineIds())
                {
                    var sl = (CivSampleLine)tr.GetObject(sid, OpenMode.ForWrite);
                    sl.Erase();
                    r.OldLines++;
                }

                foreach (double st in stations)
                {
                    double xL = 0, yL = 0, xR = 0, yR = 0;
                    al.PointLocation(st, -swath, ref xL, ref yL);
                    al.PointLocation(st, swath, ref xR, ref yR);
                    CivSampleLine.Create(al.Name + "_SL-" + st.ToString("F0"), gid,
                        new Point2dCollection
                        {
                            new Point2d(xL, yL),
                            new Point2d(xR, yR)
                        });
                    r.NewLines++;
                }
                results.Add(r);
            }
            return results;
        }

        /// <summary>
        /// 从第一组现有采样线反推间距/采样宽度当默认值（只用保底 API：线数与包围盒）。
        /// 反推不出（组空等）返回 false，调用方用自己的缺省。
        /// </summary>
        public static bool Estimate(CivAlign al, Transaction tr,
            out double interval, out double swath)
        {
            interval = 50;
            swath = 50;
            try
            {
                ObjectIdCollection gids = al.GetSampleLineGroupIds();
                if (gids.Count == 0) return false;
                var group = (CivSampleLineGroup)tr.GetObject(gids[0], OpenMode.ForRead);
                ObjectIdCollection sids = group.GetSampleLineIds();
                if (sids.Count < 2) return false;

                double len = al.EndingStation - al.StartingStation;
                interval = Math.Round(len / (sids.Count - 1), 1);

                // 采样宽度 ≈ 首条线包围盒对角线的一半（线垂直于路线，对角线≈全宽）
                var first = (Entity)tr.GetObject(sids[0], OpenMode.ForRead);
                Extents3d ext = first.GeometricExtents;
                double dx = ext.MaxPoint.X - ext.MinPoint.X;
                double dy = ext.MaxPoint.Y - ext.MinPoint.Y;
                swath = Math.Round(Math.Sqrt(dx * dx + dy * dy) / 2, 1);
                return interval > 0 && swath > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
