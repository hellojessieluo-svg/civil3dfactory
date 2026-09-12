using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Civil3DFactory.Geometry;

namespace Civil3DFactory
{
    /// <summary>
    /// 节点 fix_self_intersections：检测并修复自相交多段线（去回环）。
    /// 算法核心在同目录 SelfIntersectionCore.cs（与 products\waterbox 的
    /// C3DF-FixSelfIntersect/ZJ 同核）。report_only 只查不改。
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeFixSelfIntersections(JsonObject a, Document doc)
            => FixSelfIntersections(a, doc);

        public static JsonNode FixSelfIntersections(JsonObject a, Document doc)
        {
            string layer = GetString(a, "layer", null);
            JsonArray handles = a["handles"] as JsonArray;
            if (string.IsNullOrEmpty(layer) && (handles == null || handles.Count == 0))
                throw new InvalidOperationException("handles（句柄数组）与 layer（图层名）至少给一个。");

            double maxDrop = GetDouble(a, "max_drop_ratio", 0.25);
            if (maxDrop <= 0 || maxDrop > 1)
                throw new InvalidOperationException("max_drop_ratio 必须在 (0,1]（0.25 = 最多丢掉四分之一）。");
            bool reportOnly = GetBool(a, "report_only", false);

            Database db = doc.Database;
            var per = new JsonArray();
            var needsManual = new JsonArray();
            int checkedCount = 0, dirty = 0, repaired = 0, loops = 0, hitsTotal = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // 收集目标：句柄优先，否则整图层扫
                var targets = new List<Polyline>();
                if (handles != null && handles.Count > 0)
                {
                    foreach (JsonNode h in handles)
                    {
                        string hs = h != null ? h.ToString() : null;
                        ObjectId id = ResolveHandle(db, hs);
                        Polyline pl = id.IsNull ? null : tr.GetObject(id, OpenMode.ForRead) as Polyline;
                        if (pl == null)
                            throw new InvalidOperationException("句柄 '" + hs + "' 不是多段线或不存在。");
                        targets.Add(pl);
                    }
                }
                else
                {
                    foreach (ObjectId id in ModelSpace(db, tr))
                    {
                        var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                        if (pl != null && string.Equals(pl.Layer, layer, StringComparison.OrdinalIgnoreCase))
                            targets.Add(pl);
                    }
                    if (targets.Count == 0)
                        throw new InvalidOperationException("图层 '" + layer + "' 上没有多段线。");
                }

                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                foreach (Polyline src in targets)
                {
                    checkedCount++;
                    string handle = src.Handle.ToString();

                    if (reportOnly)
                    {
                        int dupVerts;
                        List<SelfIntersectionCore.Hit> hits = SelfIntersectionCore.Detect(src, out dupVerts);
                        // 重复顶点（零长段）没有交点但 Civil 加边界照样拒收，也算脏
                        if (hits.Count == 0 && dupVerts == 0) continue;
                        dirty++;
                        hitsTotal += hits.Count;
                        var ptsJ = new JsonArray();
                        foreach (SelfIntersectionCore.Hit h in hits)
                            ptsJ.Add(new JsonArray { Round(h.Point.X, 4), Round(h.Point.Y, 4) });
                        per.Add(new JsonObject
                        {
                            ["handle"] = handle,
                            ["layer"] = src.Layer,
                            ["closed"] = src.Closed,
                            ["intersections"] = hits.Count,
                            ["duplicate_vertices"] = dupVerts,
                            ["points"] = ptsJ,
                            ["repaired"] = false
                        });
                        continue;
                    }

                    SelfIntersectionCore.RepairReport rep;
                    Polyline fixedPl = SelfIntersectionCore.Repair(src, maxDrop, out rep);
                    if (rep.HitsFound > 0 || rep.DuplicateVerticesRemoved > 0)
                    { dirty++; hitsTotal += rep.HitsFound; }

                    if (rep.NeedsManual)
                    {
                        var ptsJ = new JsonArray();
                        foreach (Point2d p in rep.Intersections)
                            ptsJ.Add(new JsonArray { Round(p.X, 4), Round(p.Y, 4) });
                        needsManual.Add(new JsonObject
                        {
                            ["handle"] = handle,
                            ["layer"] = src.Layer,
                            ["intersections"] = rep.HitsFound,
                            ["reason"] = rep.ManualReason,
                            ["points"] = ptsJ
                        });
                        continue;
                    }
                    if (fixedPl == null) continue;      // 本来就干净

                    src.UpgradeOpen();
                    src.Erase();
                    space.AppendEntity(fixedPl);
                    tr.AddNewlyCreatedDBObject(fixedPl, true);

                    repaired++;
                    loops += rep.LoopsRemoved;
                    var loopsJ = new JsonArray();
                    foreach (double v in rep.RemovedLoopAreas) loopsJ.Add(v);
                    foreach (double v in rep.RemovedLoopLengths) loopsJ.Add(v);
                    per.Add(new JsonObject
                    {
                        ["handle"] = handle,
                        ["new_handle"] = fixedPl.Handle.ToString(),
                        ["layer"] = fixedPl.Layer,
                        ["closed"] = fixedPl.Closed,
                        ["intersections"] = rep.HitsFound,
                        ["loops_removed"] = rep.LoopsRemoved,
                        ["removed_loops"] = loopsJ,
                        ["duplicate_vertices_removed"] = rep.DuplicateVerticesRemoved,
                        ["vertices"] = rep.VerticesBefore + " → " + rep.VerticesAfter,
                        ["area_before"] = Round(rep.AreaBefore, 4),
                        ["area_after"] = Round(rep.AreaAfter, 4),
                        ["length_before"] = Round(rep.LengthBefore, 4),
                        ["length_after"] = Round(rep.LengthAfter, 4),
                        ["clean"] = rep.Clean,
                        ["repaired"] = true
                    });
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["checked"] = checkedCount,
                ["self_intersecting"] = dirty,
                ["intersections_total"] = hitsTotal,
                ["repaired"] = repaired,
                ["loops_removed"] = loops,
                ["needs_manual"] = needsManual,
                ["report_only"] = reportOnly,
                ["max_drop_ratio"] = maxDrop,
                ["per_polyline"] = per
            };
        }
    }
}
