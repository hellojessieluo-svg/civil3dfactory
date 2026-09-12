using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Civil3DFactory.Geometry;

namespace Civil3DFactory
{
    /// <summary>
    /// 节点 offset_cone_contours：闭合边界批量内偏生成等高线（偏移圆台）。
    /// 算法核心在同目录 OffsetConeCore.cs（与 products\waterbox 的 C3DF-OffsetCone/YT 同核）。
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeOffsetConeContours(JsonObject a, Document doc)
            => OffsetConeContours(a, doc);

        public static JsonNode OffsetConeContours(JsonObject a, Document doc)
        {
            double z1 = GetDouble(a, "target_z", double.NaN);
            if (double.IsNaN(z1)) throw new InvalidOperationException("缺少必需参数 target_z（目标设计高程）。");
            double n = GetDouble(a, "slope_n", double.NaN);
            if (double.IsNaN(n) || n <= 0) throw new InvalidOperationException("slope_n 必须大于 0（坡比 1:n 的 n）。");
            double dz = GetDouble(a, "step_dz", 0.5);
            if (dz <= 0) throw new InvalidOperationException("step_dz 必须大于 0。");
            double rFillet = GetDouble(a, "fillet_r", Math.Round(2 * n * dz, 3));
            if (rFillet < 0) throw new InvalidOperationException("fillet_r 不能为负（0 = 不平滑不归圆）。");

            bool outward = GetBool(a, "outward", false);
            string outLayer = GetString(a, "out_layer", null);   // 缺省跟源边界同层
            bool clearExisting = GetBool(a, "clear_existing", true);
            string layer = GetString(a, "layer", null);
            if (!string.IsNullOrWhiteSpace(outLayer) &&
                string.Equals(outLayer, layer, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("out_layer 不能与源边界图层同名，否则重跑会连源边界一起清掉。");
            JsonArray handles = a["boundaries"] as JsonArray;
            if (string.IsNullOrEmpty(layer) && (handles == null || handles.Count == 0))
                throw new InvalidOperationException("boundaries（句柄数组）与 layer（图层名）至少给一个。");

            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // 收集边界：句柄优先，其次整图层扫闭合 LWPOLYLINE
                var sources = new List<Polyline>();
                if (handles != null && handles.Count > 0)
                {
                    foreach (JsonNode h in handles)
                    {
                        string hs = h != null ? h.ToString() : null;
                        ObjectId id = ResolveHandle(db, hs);
                        Polyline pl = id.IsNull ? null : tr.GetObject(id, OpenMode.ForRead) as Polyline;
                        if (pl == null)
                            throw new InvalidOperationException("句柄 '" + hs + "' 不是多段线或不存在。");
                        sources.Add(pl);
                    }
                }
                else
                {
                    foreach (ObjectId id in ModelSpace(db, tr))
                    {
                        var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                        if (pl != null && string.Equals(pl.Layer, layer, StringComparison.OrdinalIgnoreCase))
                            sources.Add(pl);
                    }
                    if (sources.Count == 0)
                        throw new InvalidOperationException("图层 '" + layer + "' 上没有多段线。");
                }

                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                // 重跑前先清掉上一轮生成的圈，否则等高线会一层层叠上去
                int erased = 0;
                if (clearExisting && !string.IsNullOrWhiteSpace(outLayer))
                {
                    var srcIds = new HashSet<ObjectId>();
                    foreach (Polyline s in sources) srcIds.Add(s.ObjectId);
                    foreach (ObjectId id in ModelSpace(db, tr))
                    {
                        if (srcIds.Contains(id)) continue;
                        var old = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                        if (old == null) continue;
                        if (!string.Equals(old.Layer, outLayer, StringComparison.OrdinalIgnoreCase)) continue;
                        old.UpgradeOpen();
                        old.Erase();
                        erased++;
                    }
                }
                if (!string.IsNullOrWhiteSpace(outLayer))
                    PolyAreaEnsureLayer(tr, db, outLayer, (short)GetDouble(a, "out_color", 4));
                var perBoundary = new JsonArray();
                var ringHandles = new JsonArray();
                int ringsTotal = 0, collapsed = 0, skippedOpen = 0, skippedSameZ = 0;

                foreach (Polyline src in sources)
                {
                    if (!src.Closed && !OffsetConeCore.IsSnapClosed(src)) { skippedOpen++; continue; }

                    var res = OffsetConeCore.GenerateCone(src, z1, n, dz, rFillet, final =>
                    {
                        if (!string.IsNullOrWhiteSpace(outLayer)) final.Layer = outLayer;
                        space.AppendEntity(final);
                        tr.AddNewlyCreatedDBObject(final, true);
                        ringHandles.Add(final.Handle.ToString());
                    }, outward);

                    if (res.SameElevation) { skippedSameZ++; continue; }
                    if (res.Collapsed) collapsed++;
                    ringsTotal += res.Rings;

                    perBoundary.Add(new JsonObject
                    {
                        ["handle"] = src.Handle.ToString(),
                        ["z0"] = res.Z0,
                        ["reached"] = res.Reached,
                        ["rings"] = res.Rings,
                        ["stepped_rings"] = res.SteppedRings,
                        ["collapsed"] = res.Collapsed
                    });
                }

                // 断言：安静地成功=没成功。一圈都没生成说明参数或边界不对，必须报错而不是 ok:true。
                if (ringsTotal == 0)
                    throw new InvalidOperationException(
                        "没有生成任何等高线圈（边界 " + sources.Count + " 条，未闭合 " + skippedOpen
                        + "，高程与目标相同 " + skippedSameZ + "）。检查 target_z 与边界高程。");

                tr.Commit();

                return new JsonObject
                {
                    ["boundaries"] = sources.Count,
                    ["outward"] = outward,
                    ["out_layer"] = outLayer,
                    ["erased_previous"] = erased,
                    ["rings_total"] = ringsTotal,
                    ["collapsed"] = collapsed,
                    ["skipped_open"] = skippedOpen,
                    ["skipped_same_elevation"] = skippedSameZ,
                    ["ring_handles"] = ringHandles,
                    ["per_boundary"] = perBoundary
                };
            }
        }
    }
}
