using System;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Civil3DFactory.Geometry;
using CivCorr = Autodesk.Civil.DatabaseServices.Corridor;

namespace Civil3DFactory
{
    /// <summary>
    /// Node sync_corridor_range: align corridor regions to the alignment start/end (companion to replacing the centerline).
    /// The algorithm core lives in CorridorRangeCore.cs in this folder (shared with
    /// C3DF-SyncCorridorRange/QZ in products\waterbox).
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeSyncCorridorRange(JsonObject a, Document doc)
            => SyncCorridorRange(a, doc);

        public static JsonNode SyncCorridorRange(JsonObject a, Document doc)
        {
            string name = Need(a, "corridor");
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivCorr corr = FindCorridor(tr, db, name);
                if (corr == null)
                    throw new InvalidOperationException("Corridor '" + name + "' not found.");
                corr.UpgradeOpen();

                CorridorRangeCore.SyncResult r = CorridorRangeCore.SyncToAlignments(corr, tr);

                int adjusted = 0;
                var blArr = new JsonArray();
                foreach (CorridorRangeCore.BaselineSync b in r.Baselines)
                {
                    adjusted += b.RegionsAdjusted;
                    var warns = new JsonArray();
                    foreach (string w in b.Warnings) warns.Add(w);
                    blArr.Add(new JsonObject
                    {
                        ["alignment"] = b.AlignmentName,
                        ["align_start"] = Round(b.AlignStart, 4),
                        ["align_end"] = Round(b.AlignEnd, 4),
                        ["old_start"] = Round(b.OldStart, 4),
                        ["old_end"] = Round(b.OldEnd, 4),
                        ["regions"] = b.Regions,
                        ["regions_adjusted"] = b.RegionsAdjusted,
                        ["warnings"] = warns
                    });
                }

                // Assert: no baseline to sync (none has an alignment / regions) is a configuration problem; no silent success
                if (r.Baselines.Count == 0)
                    throw new InvalidOperationException("Corridor '" + name + "' has no baseline.");
                if (!r.Rebuilt)
                    throw new InvalidOperationException("Corridor rebuild failed: " + r.RebuildError);

                tr.Commit();
                return new JsonObject
                {
                    ["corridor"] = r.CorridorName,
                    ["baselines"] = blArr,
                    ["regions_adjusted"] = adjusted,
                    ["rebuilt"] = r.Rebuilt
                };
            }
        }
    }
}
