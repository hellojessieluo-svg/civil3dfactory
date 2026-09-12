using System;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Civil3DFactory.Geometry;
using CivCorr = Autodesk.Civil.DatabaseServices.Corridor;

namespace Civil3DFactory
{
    /// <summary>
    /// 节点 sync_corridor_range：走廊区间对齐路线起终点（换中线后配套）。
    /// 算法核心在同目录 CorridorRangeCore.cs（与 products\waterbox 的
    /// C3DF-SyncCorridorRange/QZ 同核）。
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
                    throw new InvalidOperationException("找不到走廊 '" + name + "'。");
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

                // 断言：没有任何基线可同步（全部拿不到路线/没区间）是配置问题，不许安静成功
                if (r.Baselines.Count == 0)
                    throw new InvalidOperationException("走廊 '" + name + "' 没有基线。");
                if (!r.Rebuilt)
                    throw new InvalidOperationException("走廊重建失败：" + r.RebuildError);

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
