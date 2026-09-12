using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlign = Autodesk.Civil.DatabaseServices.Alignment;
using CivilDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// import_design_lines：把设计线 DWG（export_design_lines 的产物，或人手画的）搬进当前图，
    /// 中心线转成路线，闭合边界留作后续宽度目标的源。这是「多段线是输入参数」这条流水线的 S01。
    ///
    /// 身份靠图层名，不靠对象名——多段线没有名字：
    ///   中心线-{通道}   → 建名为 {通道} 的路线
    ///   边界-{通道}     → 原样搬进来，落在 boundary_layer 上，供 offsets_from_boundary 用
    ///
    /// 装配 / 样式 / QTO 准则 / 原地形曲面这四样造不出来，必须已在当前图（模板）里。
    /// 本节点只负责把设计意图搬进去，不碰它们。
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeImportDesignLines(JsonObject args, Document doc)
            => ImportDesignLines(args, doc);

        public static JsonNode ImportDesignLines(JsonObject a, Document doc)
        {
            string from = Need(a, "dwg");
            if (!File.Exists(from))
                throw new InvalidOperationException("找不到设计线图纸：" + from);

            string cPrefix = GetString(a, "center_prefix", "CL-");
            string bPrefix = GetString(a, "boundary_prefix", "BOUNDARY-");
            string style = GetString(a, "style", null);
            string labelSet = GetString(a, "label_set", null);
            string alLayer = GetString(a, "alignment_layer", null);
            bool addCurves = GetBool(a, "add_curves", false);
            bool replaceExisting = GetBool(a, "replace_existing", true);
            double minBoundaryArea = GetDouble(a, "min_boundary_area", 100.0);

            Database db = doc.Database;
            var created = new JsonArray();
            var boundaries = new JsonArray();
            var notes = new JsonArray();

            // ---- 1 从设计线图里挑出要搬的多段线 ----
            var srcIds = new ObjectIdCollection();
            var roleByHandle = new Dictionary<string, string>();   // 源句柄 → "C:通道" / "B:通道"
            using (var srcDb = new Database(false, true))
            {
                srcDb.ReadDwgFile(from, FileOpenMode.OpenForReadAndAllShare, true, null);
                srcDb.CloseInput(true);
                using (Transaction str = srcDb.TransactionManager.StartTransaction())
                {
                    BlockTable sbt = (BlockTable)str.GetObject(srcDb.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord sms = (BlockTableRecord)str.GetObject(
                        sbt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in sms)
                    {
                        var pl = str.GetObject(id, OpenMode.ForRead) as Polyline;
                        if (pl == null) continue;
                        string layer = pl.Layer ?? "";
                        string role = null, channel = null;
                        if (layer.StartsWith(cPrefix, StringComparison.Ordinal))
                        { role = "C"; channel = layer.Substring(cPrefix.Length); }
                        else if (layer.StartsWith(bPrefix, StringComparison.Ordinal))
                        { role = "B"; channel = layer.Substring(bPrefix.Length); }
                        else continue;
                        if (string.IsNullOrWhiteSpace(channel)) continue;

                        if (role == "B")
                        {
                            double area = 0.0;
                            try { area = Math.Abs(pl.Area); } catch (System.Exception) { }
                            if (area < minBoundaryArea)
                            { notes.Add("丢弃碎片边界 " + layer + "（面积 " + Math.Round(area, 2) + " < " + minBoundaryArea + "）"); continue; }
                        }
                        srcIds.Add(id);
                        roleByHandle[pl.Handle.ToString()] = role + ":" + channel;
                    }
                    str.Commit();
                }

                if (srcIds.Count == 0)
                    throw new InvalidOperationException(
                        "设计线图里没有匹配的图层（中心线前缀 \"" + cPrefix + "\"、边界前缀 \"" + bPrefix + "\"）。");

                // ---- 2 搬进当前图的模型空间 ----
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    ObjectId msId = bt[BlockTableRecord.ModelSpace];
                    var map = new IdMapping();
                    srcDb.WblockCloneObjects(srcIds, msId, map, DuplicateRecordCloning.Ignore, false);
                    tr.Commit();
                }
            }

            // ---- 3 在当前图里按图层认领，中心线转路线 ----
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivilDoc civ = Civ(db);
                // 无条件调用：FindStyleId 在名字为空时回退到该集合第一条。
                // Alignment.Create 的 labelSetId 不接受 ObjectId.Null（抛
                // "Value cannot be null. (Parameter 'labelSetId')"），必须给个真样式。
                ObjectId styleId = FindStyleId(tr, civ.Styles.AlignmentStyles, style);
                ObjectId labelId = FindStyleId(tr,
                    civ.Styles.LabelSetStyles.AlignmentLabelSetStyles, labelSet);
                if (labelId.IsNull)
                    throw new InvalidOperationException("当前图里没有任何路线标签集样式，无法建路线。");
                ObjectId layerId = db.Clayer;
                if (!string.IsNullOrWhiteSpace(alLayer))
                    layerId = A2PEnsureLayer(tr, db, alLayer, 7, "Continuous", "acadiso.lin");

                // 已有同名路线：按需先删，否则 Create 会因重名失败
                var existing = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId aid in civ.GetAlignmentIds())
                {
                    var x = tr.GetObject(aid, OpenMode.ForRead) as CivAlign;
                    if (x != null) existing[x.Name] = aid;
                }

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                // 只处理刚搬进来的：按图层前缀 + 尚未登记的方式识别
                var pending = new List<KeyValuePair<ObjectId, string>>();   // 实体 → "C:通道"/"B:通道"
                foreach (ObjectId id in ms)
                {
                    var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (pl == null) continue;
                    string layer = pl.Layer ?? "";
                    if (layer.StartsWith(cPrefix, StringComparison.Ordinal))
                        pending.Add(new KeyValuePair<ObjectId, string>(id, "C:" + layer.Substring(cPrefix.Length)));
                    else if (layer.StartsWith(bPrefix, StringComparison.Ordinal))
                        pending.Add(new KeyValuePair<ObjectId, string>(id, "B:" + layer.Substring(bPrefix.Length)));
                }

                foreach (var kv in pending)
                {
                    string role = kv.Value.Substring(0, 1);
                    string channel = kv.Value.Substring(2);
                    if (role == "B")
                    {
                        var pl = tr.GetObject(kv.Key, OpenMode.ForRead) as Polyline;
                        double area = 0.0;
                        try { area = Math.Abs(pl.Area); } catch (System.Exception) { }
                        boundaries.Add(new JsonObject
                        {
                            ["channel"] = channel,
                            ["handle"] = pl.Handle.ToString(),
                            ["layer"] = pl.Layer,
                            ["closed"] = pl.Closed,
                            ["vertices"] = pl.NumberOfVertices,
                            ["area"] = Round(area, 3)
                        });
                        continue;
                    }

                    // ⚠ 先改名腾位，建成功才删旧的。
                    // 曾经写成「先删旧 → 再建新」，建失败时旧路线已经没了 —— 一次失败赔掉原始数据。
                    CivAlign old = null;
                    string parked = null;
                    if (existing.ContainsKey(channel))
                    {
                        if (!replaceExisting)
                        { notes.Add("路线 " + channel + " 已存在，未替换（replace_existing=false）"); continue; }
                        old = tr.GetObject(existing[channel], OpenMode.ForWrite) as CivAlign;
                        if (old != null)
                        {
                            parked = channel + "_旧_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                            try { old.Name = parked; }
                            catch (System.Exception ex)
                            { notes.Add("路线 " + channel + " 无法改名腾位，跳过：" + ex.Message); continue; }
                        }
                    }

                    ObjectId newId;
                    try
                    {
                        newId = CreateAlignmentFromEntity(tr, civ, channel, ObjectId.Null, kv.Key,
                            layerId, styleId, labelId, true, addCurves);
                    }
                    catch (System.Exception ex)
                    {
                        if (old != null)
                        {
                            try { old.Name = channel; notes.Add("已把旧路线 " + channel + " 改回原名"); }
                            catch (System.Exception) { notes.Add("⚠ 旧路线改不回原名，现名 " + parked); }
                        }
                        notes.Add("中心线 " + channel + " 转路线失败：" + ex.Message);
                        continue;
                    }

                    if (old != null)
                    {
                        try { old.Erase(); notes.Add("路线 " + channel + " 已按设计线重建，旧的已删"); }
                        catch (System.Exception ex)
                        { notes.Add("⚠ 新路线 " + channel + " 已建，但旧的删不掉（现名 " + parked + "）：" + ex.Message); }
                    }

                    var al = tr.GetObject(newId, OpenMode.ForRead) as CivAlign;
                    created.Add(new JsonObject
                    {
                        ["channel"] = channel,
                        ["alignment"] = al == null ? channel : al.Name,
                        ["handle"] = al == null ? "" : al.Handle.ToString(),
                        ["length"] = al == null ? 0.0 : Round(al.Length, 4),
                        ["start_station"] = al == null ? 0.0 : Round(al.StartingStation, 4),
                        ["end_station"] = al == null ? 0.0 : Round(al.EndingStation, 4)
                    });
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["source"] = from,
                ["alignments_created"] = created.Count,
                ["boundaries_imported"] = boundaries.Count,
                ["alignments"] = created,
                ["boundaries"] = boundaries,
                ["notes"] = notes
            };
        }
    }
}
