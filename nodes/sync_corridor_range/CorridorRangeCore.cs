#nullable disable   // This file is compiled by both Civil3DFactory (nullable off) and WaterBox (nullable on); treat as off

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
    /// Core for aligning corridor regions to the alignment start/end (after a centerline swap the corridor matches the alignment ends).
    /// For each baseline take its alignment's [start station, end station]: pull the first region's start to the alignment start,
    /// the last region's end to the alignment end, clamp the middle region boundaries into range, then Rebuild the corridor.
    /// Single source of truth: compiled by both Civil3DFactory (node sync_corridor_range) and products\waterbox
    /// (C3DF-SyncCorridorRange/QZ).
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

        /// <summary>corridor must already be open ForWrite.</summary>
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
                    sync.AlignmentName = "(baseline alignment unavailable)";
                    sync.Warnings.Add("Baseline has no alignment object, skipped.");
                    continue;
                }
                sync.AlignmentName = al.Name;
                double s = al.StartingStation, e = al.EndingStation;
                sync.AlignStart = s;
                sync.AlignEnd = e;
                if (e - s < Tol)
                {
                    sync.Warnings.Add("Alignment length is zero, skipped.");
                    continue;
                }

                // Sort regions by start station, then adjust
                var regions = new List<CivRegion>();
                foreach (CivRegion rg in bl.BaselineRegions) regions.Add(rg);
                sync.Regions = regions.Count;
                if (regions.Count == 0)
                {
                    sync.Warnings.Add("Baseline has no region, skipped.");
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
                        sync.Warnings.Add("Region " + (i + 1) + " would have zero length after adjustment (was "
                            + rs.ToString("0.###") + "~" + re.ToString("0.###") + "), untouched.");
                        continue;
                    }
                    if (Math.Abs(ns - rs) < Tol && Math.Abs(ne - re) < Tol) continue;
                    try
                    {
                        // When shrinking, move the end that stays in range first to avoid a momentary start>end
                        if (ns <= re) { SetStart(rg, ns); SetEnd(rg, ne); }
                        else { SetEnd(rg, ne); SetStart(rg, ns); }
                        sync.RegionsAdjusted++;
                    }
                    catch (System.Exception ex)
                    {
                        sync.Warnings.Add("Region " + (i + 1) + " station write failed: " + ex.Message);
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
