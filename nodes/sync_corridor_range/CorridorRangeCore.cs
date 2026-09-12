#nullable disable   // 本文件被 Civil3DFactory（可空关）与 WaterBox（可空开）两个工程共同编译，按关处理

using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using CivAlign = Autodesk.Civil.DatabaseServices.Alignment;
using CivBaseline = Autodesk.Civil.DatabaseServices.Baseline;
using CivCorr = Autodesk.Civil.DatabaseServices.Corridor;
using CivRegion = Autodesk.Civil.DatabaseServices.BaselineRegion;

namespace Civil3DFactory.Geometry
{
    /// <summary>
    /// 走廊区间对齐路线起终点的算法核心（换中线后道路与路线起终点一致）。
    /// 逐基线取其路线的 [起点站号, 终点站号]：首区间起点拉到路线起点、
    /// 末区间终点拉到路线终点、中间区间边界收进范围内，然后 Rebuild 走廊。
    /// 唯一真源：Civil3DFactory（节点 sync_corridor_range）与 products\waterbox
    /// （C3DF-SyncCorridorRange/QZ）共同编译本文件。
    /// </summary>
    public static class CorridorRangeCore
    {
        public sealed class BaselineSync
        {
            public string AlignmentName;
            public double AlignStart;
            public double AlignEnd;
            public double OldStart;
            public double OldEnd;
            public int Regions;
            public int RegionsAdjusted;
            public List<string> Warnings = new List<string>();
        }

        public sealed class SyncResult
        {
            public string CorridorName;
            public List<BaselineSync> Baselines = new List<BaselineSync>();
            public bool Rebuilt;
            public string RebuildError;
        }

        const double Tol = 1e-6;

        /// <summary>corridor 必须已 ForWrite 打开。</summary>
        public static SyncResult SyncToAlignments(CivCorr corridor, Transaction tr)
        {
            var result = new SyncResult { CorridorName = corridor.Name };

            foreach (CivBaseline bl in corridor.Baselines)
            {
                var sync = new BaselineSync();
                result.Baselines.Add(sync);

                CivAlign al = null;
                try { al = tr.GetObject(bl.AlignmentId, OpenMode.ForRead) as CivAlign; }
                catch { }
                if (al == null)
                {
                    sync.AlignmentName = "(取不到基线路线)";
                    sync.Warnings.Add("基线拿不到路线对象，跳过。");
                    continue;
                }
                sync.AlignmentName = al.Name;
                double s = al.StartingStation, e = al.EndingStation;
                sync.AlignStart = s;
                sync.AlignEnd = e;
                if (e - s < Tol)
                {
                    sync.Warnings.Add("路线长度为零，跳过。");
                    continue;
                }

                // 区间按起点站号排序后调整
                var regions = new List<CivRegion>();
                foreach (CivRegion rg in bl.BaselineRegions) regions.Add(rg);
                sync.Regions = regions.Count;
                if (regions.Count == 0)
                {
                    sync.Warnings.Add("基线没有区间（Region），跳过。");
                    continue;
                }
                regions.Sort((x, y) => x.StartStation.CompareTo(y.StartStation));
                sync.OldStart = regions[0].StartStation;
                sync.OldEnd = regions[regions.Count - 1].EndStation;

                for (int i = 0; i < regions.Count; i++)
                {
                    CivRegion rg = regions[i];
                    double rs = rg.StartStation, re = rg.EndStation;
                    double ns = i == 0 ? s : Math.Min(Math.Max(rs, s), e);
                    double ne = i == regions.Count - 1 ? e : Math.Min(Math.Max(re, s), e);
                    if (ne - ns < Tol)
                    {
                        sync.Warnings.Add("区间 " + (i + 1) + " 调整后长度为零（原 "
                            + rs.ToString("0.###") + "~" + re.ToString("0.###") + "），未动。");
                        continue;
                    }
                    if (Math.Abs(ns - rs) < Tol && Math.Abs(ne - re) < Tol) continue;
                    try
                    {
                        // 收缩时先动不越界的那端，避免瞬时 start>end
                        if (ns <= re) { SetStart(rg, ns); SetEnd(rg, ne); }
                        else { SetEnd(rg, ne); SetStart(rg, ns); }
                        sync.RegionsAdjusted++;
                    }
                    catch (System.Exception ex)
                    {
                        sync.Warnings.Add("区间 " + (i + 1) + " 站号写入失败：" + ex.Message);
                    }
                }
            }

            try
            {
                corridor.Rebuild();
                result.Rebuilt = true;
            }
            catch (System.Exception ex)
            {
                result.RebuildError = ex.Message;
            }
            return result;
        }

        static void SetStart(CivRegion rg, double v)
        {
            if (Math.Abs(rg.StartStation - v) > Tol) rg.StartStation = v;
        }

        static void SetEnd(CivRegion rg, double v)
        {
            if (Math.Abs(rg.EndStation - v) > Tol) rg.EndStation = v;
        }
    }
}
