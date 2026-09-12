using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlign = Autodesk.Civil.DatabaseServices.Alignment;
using CivCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using CivBaseline = Autodesk.Civil.DatabaseServices.Baseline;
using CivFlColl = Autodesk.Civil.DatabaseServices.FeatureLineCollection;
using CivFl = Autodesk.Civil.DatabaseServices.CorridorFeatureLine;
using CivFlPoint = Autodesk.Civil.DatabaseServices.FeatureLinePoint;
using CivilDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// export_corridor_feature_lines：把走廊要素线（按点码）抽成三维多段线，
    /// 写进一张**全新的空白 DWG**——逐点保留高程，产出文件里没有任何 Civil 3D 对象。
    ///
    /// 用途：疏浚走廊的设计边界线（daylight 开口线、toe 坡脚线等）是断面法模型的
    /// 三维骨架，交付/汇总时按点码整层导出。点码沿用厂里 PKT 码表
    /// （origin / controlpoint / daylight / mp / toe + -left/-right，见业务总纲 §3）。
    ///
    /// 要素线在区域断开处（FeatureLinePoint.IsBreak，如交叉口段换工况）按段拆成多条
    /// 多段线，不硬连。图层名是这份输出文件的契约，用模板参数控制：
    ///   layer 默认 "FL-{corridor}-{code}"，占位符 {corridor} 走廊名、
    ///   {baseline} 基准线路线名、{code} 点码。
    /// 只导主基准线要素线（偏移基准线本项目未用，遇到记入 skipped）。
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeExportCorridorFeatureLines(JsonObject args, Document doc)
            => ExportCorridorFeatureLines(args, doc);

        public static JsonNode ExportCorridorFeatureLines(JsonObject a, Document doc)
        {
            string outPath = Need(a, "out");
            if (!Path.IsPathRooted(outPath))
                throw new InvalidOperationException("out 必须是绝对路径：" + outPath);
            bool overwrite = GetBool(a, "overwrite", false);
            if (File.Exists(outPath) && !overwrite)
                throw new InvalidOperationException("文件已存在，拒绝覆盖：" + outPath + "（确需覆盖传 overwrite:true）");

            string layerTpl = GetString(a, "layer", "FL-{corridor}-{code}");
            short color = (short)GetDouble(a, "color", 2);          // 黄
            int minPoints = (int)GetDouble(a, "min_points", 2);

            var wantCorridors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            JsonArray onlyCor = a["corridors"] as JsonArray;
            if (onlyCor != null)
                foreach (JsonNode n in onlyCor) if (n != null) wantCorridors.Add(n.ToString());
            var wantCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            JsonArray onlyCodes = a["codes"] as JsonArray;
            if (onlyCodes != null)
                foreach (JsonNode n in onlyCodes) if (n != null) wantCodes.Add(n.ToString());

            Database db = doc.Database;
            var items = new List<JsonObject>();
            var lines = new List<KeyValuePair<string, List<Point3d>>>();  // layer -> pts
            var skipped = new JsonArray();
            int corridorsSeen = 0, breaks = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivilDoc civ = Civ(db);
                foreach (ObjectId cid in civ.CorridorCollection)
                {
                    var cor = tr.GetObject(cid, OpenMode.ForRead) as CivCorridor;
                    if (cor == null) continue;
                    if (wantCorridors.Count > 0 && !wantCorridors.Contains(cor.Name)) continue;
                    corridorsSeen++;

                    foreach (CivBaseline bl in cor.Baselines)
                    {
                        string blName = "";
                        try
                        {
                            var al = tr.GetObject(bl.AlignmentId, OpenMode.ForRead) as CivAlign;
                            if (al != null) blName = al.Name;
                        }
                        catch (System.Exception) { }

                        foreach (CivFlColl coll in bl.MainBaselineFeatureLines.FeatureLineCollectionMap)
                        {
                            int idx = 0;
                            foreach (CivFl fl in coll)
                            {
                                idx++;
                                string code = "";
                                try { code = fl.CodeName; } catch (System.Exception) { }
                                if (wantCodes.Count > 0 && !wantCodes.Contains(code)) continue;

                                string layer = layerTpl
                                    .Replace("{corridor}", cor.Name)
                                    .Replace("{baseline}", blName)
                                    .Replace("{code}", code);

                                // 按 IsBreak 分段：断开处（换工况/区域间隙）不硬连
                                var segs = new List<List<Point3d>>();
                                var cur = new List<Point3d>();
                                try
                                {
                                    foreach (CivFlPoint p in fl.FeatureLinePoints)
                                    {
                                        cur.Add(p.XYZ);
                                        if (p.IsBreak)
                                        {
                                            segs.Add(cur); cur = new List<Point3d>(); breaks++;
                                        }
                                    }
                                }
                                catch (System.Exception ex)
                                {
                                    skipped.Add(cor.Name + "/" + code + "#" + idx + "（取点失败：" + ex.GetType().Name + "）");
                                    continue;
                                }
                                if (cur.Count > 0) segs.Add(cur);

                                int seg = 0;
                                foreach (var pts in segs)
                                {
                                    seg++;
                                    if (pts.Count < minPoints)
                                    {
                                        skipped.Add(cor.Name + "/" + code + "#" + idx + "." + seg + "（点数不足 " + minPoints + "）");
                                        continue;
                                    }
                                    lines.Add(new KeyValuePair<string, List<Point3d>>(layer, pts));

                                    double zmin = double.MaxValue, zmax = double.MinValue, len = 0.0;
                                    for (int i = 0; i < pts.Count; i++)
                                    {
                                        double z = pts[i].Z;
                                        if (z < zmin) zmin = z;
                                        if (z > zmax) zmax = z;
                                        if (i > 0) len += pts[i].DistanceTo(pts[i - 1]);
                                    }
                                    items.Add(new JsonObject
                                    {
                                        ["corridor"] = cor.Name,
                                        ["baseline"] = blName,
                                        ["code"] = code,
                                        ["layer"] = layer,
                                        ["segment"] = seg,
                                        ["points"] = pts.Count,
                                        ["length3d"] = Round(len, 3),
                                        ["z_min"] = Round(zmin, 3),
                                        ["z_max"] = Round(zmax, 3)
                                    });
                                }
                            }
                        }
                        // 偏移基准线不处理，如实报告
                        try
                        {
                            if (bl.OffsetBaselineFeatureLinesCol != null && bl.OffsetBaselineFeatureLinesCol.Count > 0)
                                skipped.Add(cor.Name + "（含 " + bl.OffsetBaselineFeatureLinesCol.Count + " 组偏移基准线要素线，未导出）");
                        }
                        catch (System.Exception) { }
                    }
                }
                tr.Commit();
            }

            if (lines.Count == 0)
                throw new InvalidOperationException("没有可导出的走廊要素线（走廊 " + corridorsSeen + " 个；核对 corridors/codes 过滤条件）。");

            // ---- 写进全新空白图 ----
            using (var nd = new Database(true, false))
            {
                nd.Insunits = UnitsValue.Meters;
                using (Transaction tr = nd.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(nd.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    ObjectId ltId = A2PResolveLinetype(tr, nd, "Continuous", "acadiso.lin");

                    foreach (var kv in lines)
                    {
                        ObjectId layerId = EadEnsureLayer(tr, nd, kv.Key, color, ltId);
                        var pl = new Polyline3d();
                        pl.SetDatabaseDefaults(nd);   // 见 ExportAlignmentsToDwgNode：必须显式传 nd
                        ms.AppendEntity(pl);
                        tr.AddNewlyCreatedDBObject(pl, true);
                        foreach (Point3d p in kv.Value)
                        {
                            var v = new PolylineVertex3d(p);
                            pl.AppendVertex(v);
                            tr.AddNewlyCreatedDBObject(v, true);
                        }
                        pl.LayerId = layerId;
                        pl.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                            Autodesk.AutoCAD.Colors.ColorMethod.ByAci, color);
                    }
                    tr.Commit();
                }
                string dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                nd.SaveAs(outPath, DwgVersion.Current);
            }

            long bytes = 0;
            try { bytes = new FileInfo(outPath).Length; } catch (System.Exception) { }

            var arr = new JsonArray();
            foreach (var it in items) arr.Add(it);
            return new JsonObject
            {
                ["output"] = outPath,
                ["bytes"] = bytes,
                ["corridors"] = corridorsSeen,
                ["exported"] = lines.Count,
                ["break_splits"] = breaks,
                ["layer_template"] = layerTpl,
                ["skipped"] = skipped,
                ["items"] = arr
            };
        }
    }
}
