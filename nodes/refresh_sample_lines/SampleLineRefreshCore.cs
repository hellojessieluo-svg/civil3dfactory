#nullable disable   // This file is compiled by both Civil3DFactory (nullable off) and WaterBox (nullable on); treat as off

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
    /// Sample line refresh core (after a centerline swap the sample lines follow the new geometry; the group stays).
    /// Group object, group name and section source settings are all kept -- only the old sample lines in the group are erased and
    /// perpendicular sample lines rebuilt at the interval along the new geometry; an end line is added when the remainder is under half a metre (same rule as create_sample_lines).
    /// Single source of truth: compiled by both Civil3DFactory (node refresh_sample_lines) and products\waterbox
    /// (C3DF-RefreshSampleLines/CYX).
    /// </summary>
    public static class SampleLineRefreshCore
    {
        public sealed class GroupRefresh
        {
            public string GroupName;
            public int OldLines;
            public int NewLines;
        }

        /// <summary>al may be read-only; groups are opened ForWrite internally. Throws when there is no group.</summary>
        public static List<GroupRefresh> Refresh(CivAlign al, Transaction tr,
            double interval, double swath)
        {
            if (interval <= 0) throw new InvalidOperationException("interval must be greater than 0.");
            if (swath <= 0) throw new InvalidOperationException("swath must be greater than 0.");

            ObjectIdCollection gids = al.GetSampleLineGroupIds();
            if (gids.Count == 0)
                throw new InvalidOperationException("Alignment '" + al.Name + "' has no sample line group.");

            // Station sequence: interval grid + end station (a last segment under 0.5 merges into the end line)
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
        /// Estimate interval/swath as defaults from the first group's existing sample lines (only safe APIs: line count and bounding box).
        /// Returns false when it cannot estimate (empty group etc.); the caller uses its own defaults.
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

                // swath ~= half the diagonal of the first line's bounding box (line is perpendicular to the alignment, diagonal ~= full width)
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
