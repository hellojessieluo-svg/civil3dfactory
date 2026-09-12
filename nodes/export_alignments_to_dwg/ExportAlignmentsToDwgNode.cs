using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlign = Autodesk.Civil.DatabaseServices.Alignment;
using CivSub = Autodesk.Civil.DatabaseServices.AlignmentSubEntity;
using CivSubArc = Autodesk.Civil.DatabaseServices.AlignmentSubEntityArc;
using CivSubType = Autodesk.Civil.DatabaseServices.AlignmentSubEntityType;
using CivilDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// export_alignments_to_dwg：把全部（或指定的）路线抽成纯多段线，写进一张**全新的空白 DWG**。
    ///
    /// 用途：把设计意图从 Civil 3D 对象里搬到多段线上——以后中心线/边线多段线是输入参数，
    /// Civil 模型是下游产物。产出文件里没有任何 Civil 3D 对象、样式或代理图形。
    ///
    /// 通道归属不靠名字猜：偏移路线用 OffsetAlignmentInfo.ParentAlignmentId 拿父通道、
    /// Side 拿左右、NominalOffset 拿半宽（图里那些 "路线(8)-左-35.000" 的名字看不出是 B1）。
    ///
    /// 图层名是这份输入文件的契约，用模板参数控制：
    ///   center_layer 默认 "CL-{channel}"
    ///   edge_layer   默认 "EDGE-{channel}-{side}"
    /// 占位符：{channel} 通道名、{side} 左/右、{offset} 半宽。
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeExportAlignmentsToDwg(JsonObject args, Document doc)
            => ExportAlignmentsToDwg(args, doc);

        sealed class EadItem
        {
            public string Name;
            public string Channel;
            public string Side;          // "" = 中心线
            public double Offset;
            public double Length;
            public List<Point2d> Verts = new List<Point2d>();
            public List<double> Bulges = new List<double>();
            public int Lines, Arcs, Spirals, Sampled;
        }

        public static JsonNode ExportAlignmentsToDwg(JsonObject a, Document doc)
        {
            string outPath = Need(a, "out");
            if (!Path.IsPathRooted(outPath))
                throw new InvalidOperationException("out 必须是绝对路径：" + outPath);
            bool overwrite = GetBool(a, "overwrite", false);
            if (File.Exists(outPath) && !overwrite)
                throw new InvalidOperationException("文件已存在，拒绝覆盖：" + outPath + "（确需覆盖传 overwrite:true）");

            string centerTpl = GetString(a, "center_layer", "CL-{channel}");
            string edgeTpl = GetString(a, "edge_layer", "EDGE-{channel}-{side}");
            short centerColor = (short)GetDouble(a, "center_color", 3);      // 绿
            short edgeColor = (short)GetDouble(a, "edge_color", 4);          // 青
            string centerLt = GetString(a, "center_linetype", "CENTER2");
            string edgeLt = GetString(a, "edge_linetype", "Continuous");
            double ltScale = GetDouble(a, "linetype_scale", 5.0);
            double spiralStep = GetDouble(a, "spiral_step", 5.0);
            if (spiralStep <= 1e-6) spiralStep = 5.0;
            bool centersOnly = GetBool(a, "centers_only", false);

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            JsonArray only = a["alignments"] as JsonArray;
            if (only != null)
                foreach (JsonNode n in only) if (n != null) wanted.Add(n.ToString());

            Database db = doc.Database;
            var items = new List<EadItem>();
            var skipped = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivilDoc civ = Civ(db);
                // 先建 ObjectId → 名字 的表，供偏移路线找父通道
                var nameById = new Dictionary<ObjectId, string>();
                foreach (ObjectId id in civ.GetAlignmentIds())
                {
                    var x = tr.GetObject(id, OpenMode.ForRead) as CivAlign;
                    if (x != null) nameById[id] = x.Name;
                }

                foreach (ObjectId id in civ.GetAlignmentIds())
                {
                    var al = tr.GetObject(id, OpenMode.ForRead) as CivAlign;
                    if (al == null) continue;
                    if (wanted.Count > 0 && !wanted.Contains(al.Name)) continue;

                    var it = new EadItem { Name = al.Name, Length = al.Length, Channel = al.Name, Side = "" };

                    bool isOffset = false;
                    try { isOffset = al.IsOffsetAlignment; } catch (System.Exception) { }
                    if (isOffset)
                    {
                        if (centersOnly) { skipped.Add(al.Name + "（边线，centers_only）"); continue; }
                        try
                        {
                            var info = al.OffsetAlignmentInfo;
                            string parent;
                            if (nameById.TryGetValue(info.ParentAlignmentId, out parent)) it.Channel = parent;
                            it.Side = info.Side.ToString().IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0
                                ? "左" : "右";
                            it.Offset = Math.Abs(info.NominalOffset);
                        }
                        catch (System.Exception ex)
                        {
                            skipped.Add(al.Name + "（读父路线失败：" + ex.GetType().Name + "）");
                            continue;
                        }
                    }

                    if (!EadExtract(al, spiralStep, it))
                    {
                        skipped.Add(al.Name + "（没有可转换的几何）");
                        continue;
                    }
                    items.Add(it);
                }
                tr.Commit();
            }

            if (items.Count == 0)
                throw new InvalidOperationException("没有可导出的路线。");

            // ---- 写进全新空白图 ----
            var layerCounts = new JsonObject();
            using (var nd = new Database(true, false))
            {
                nd.Insunits = UnitsValue.Meters;
                using (Transaction tr = nd.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(nd.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    foreach (EadItem it in items)
                    {
                        bool isCenter = string.IsNullOrEmpty(it.Side);
                        string layer = (isCenter ? centerTpl : edgeTpl)
                            .Replace("{channel}", it.Channel)
                            .Replace("{side}", it.Side)
                            .Replace("{offset}", it.Offset.ToString("0.###"));
                        string lt = isCenter ? centerLt : edgeLt;
                        short color = isCenter ? centerColor : edgeColor;

                        ObjectId ltId = A2PResolveLinetype(tr, nd, lt, "acadiso.lin");
                        ObjectId layerId = EadEnsureLayer(tr, nd, layer, color, ltId);

                        // ⚠ 顺序不能反：new Polyline() 默认绑在宿主图的 WorkingDatabase 上，
                        // 未入库就设 LayerId（取自新图的图层表）会抛 eWrongDatabase。
                        // 必须先 AppendEntity 让它归属新图，再设一切带 ObjectId 的属性。
                        var pl = new Polyline(it.Verts.Count);
                        // 必须显式传 nd：无参重载用 WorkingDatabase（宿主图），
                        // 后面赋新库的 LayerId 会抛 eWrongDatabase（2026-08-16 项目C实跑修复）
                        pl.SetDatabaseDefaults(nd);
                        for (int i = 0; i < it.Verts.Count; i++)
                            pl.AddVertexAt(i, it.Verts[i], it.Bulges[i], 0.0, 0.0);
                        pl.Elevation = 0.0;
                        pl.Closed = false;
                        ms.AppendEntity(pl);
                        tr.AddNewlyCreatedDBObject(pl, true);

                        pl.LayerId = layerId;
                        pl.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                            Autodesk.AutoCAD.Colors.ColorMethod.ByAci, color);
                        if (!ltId.IsNull) pl.LinetypeId = ltId;
                        pl.LinetypeScale = ltScale;
                        // 冗余身份标记：图层名是人机契约，XData 是机器兜底
                        // （图层被改名/线被搬图层后仍认得出归属）
                        EadSetXData(tr, nd, pl, it.Channel, isCenter ? "CENTER" : (it.Side == "左" ? "LEFT" : "RIGHT"),
                                    it.Offset, it.Name);

                        double plLen = 0.0;
                        try { plLen = pl.Length; } catch (System.Exception) { }
                        layerCounts[layer] = new JsonObject
                        {
                            ["source_alignment"] = it.Name,
                            ["channel"] = it.Channel,
                            ["side"] = isCenter ? "中心线" : it.Side,
                            ["offset"] = Round(it.Offset, 3),
                            ["alignment_length"] = Round(it.Length, 4),
                            ["polyline_length"] = Round(plLen, 4),
                            ["length_delta"] = Round(plLen - it.Length, 4),
                            ["vertices"] = it.Verts.Count,
                            ["line_seg"] = it.Lines,
                            ["arc_seg"] = it.Arcs,
                            ["spiral_seg"] = it.Spirals
                        };
                    }
                    tr.Commit();
                }

                string dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                nd.SaveAs(outPath, DwgVersion.Current);
            }

            long bytes = 0;
            try { bytes = new FileInfo(outPath).Length; } catch (System.Exception) { }

            return new JsonObject
            {
                ["output"] = outPath,
                ["bytes"] = bytes,
                ["exported"] = items.Count,
                ["skipped"] = skipped,
                ["center_layer_template"] = centerTpl,
                ["edge_layer_template"] = edgeTpl,
                ["items"] = layerCounts
            };
        }

        /// <summary>路线 → 顶点/bulge 列表。直线与圆弧精确，缓和曲线按步长采样。
        /// 与 alignment_to_polyline 共用同一份几何逻辑。</summary>
        static bool EadExtract(CivAlign al, double spiralStep, EadItem it)
        {
            Point2d tail = new Point2d(0.0, 0.0);
            bool hasTail = false;
            var ents = al.Entities;
            int n = ents.Count;
            if (n <= 0) return false;

            for (int i = 0; i < n; i++)
            {
                var ent = ents.GetEntityByOrder(i);
                for (int j = 0; j < ent.SubEntityCount; j++)
                {
                    CivSub sub = ent[j];
                    if (sub.SubEntityType == CivSubType.Arc)
                    {
                        var arc = sub as CivSubArc;
                        double bulge = 0.0;
                        if (arc != null)
                        {
                            bulge = Math.Tan(Math.Abs(arc.Delta) / 4.0);
                            if (arc.Clockwise) bulge = -bulge;
                        }
                        it.Verts.Add(sub.StartPoint); it.Bulges.Add(bulge); it.Arcs++;
                    }
                    else if (sub.SubEntityType == CivSubType.Spiral)
                    {
                        double s0 = sub.StartStation, s1 = sub.EndStation, span = s1 - s0;
                        int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(span) / spiralStep));
                        for (int k = 0; k < steps; k++)
                        {
                            double east = 0.0, north = 0.0;
                            al.PointLocation(s0 + span * k / steps, 0.0, ref east, ref north);
                            it.Verts.Add(new Point2d(east, north)); it.Bulges.Add(0.0); it.Sampled++;
                        }
                        it.Spirals++;
                    }
                    else
                    {
                        it.Verts.Add(sub.StartPoint); it.Bulges.Add(0.0); it.Lines++;
                    }
                    tail = sub.EndPoint; hasTail = true;
                }
            }
            if (it.Verts.Count == 0) return false;
            if (hasTail) { it.Verts.Add(tail); it.Bulges.Add(0.0); }
            return true;
        }

        const string EadAppName = "C3DF_CHANNEL";

        /// <summary>写身份 XData：通道名 / 角色(CENTER|LEFT|RIGHT) / 半宽 / 源路线名。</summary>
        static void EadSetXData(Transaction tr, Database db, Entity ent,
            string channel, string role, double offset, string source)
        {
            try
            {
                var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
                if (!rat.Has(EadAppName))
                {
                    rat.UpgradeOpen();
                    var rec = new RegAppTableRecord { Name = EadAppName };
                    rat.Add(rec);
                    tr.AddNewlyCreatedDBObject(rec, true);
                }
                ent.XData = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, EadAppName),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, channel ?? ""),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, role ?? ""),
                    new TypedValue((int)DxfCode.ExtendedDataReal, offset),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, source ?? ""));
            }
            catch (System.Exception) { }
        }

        static ObjectId EadEnsureLayer(Transaction tr, Database db, string name, short color, ObjectId ltId)
        {
            LayerTable table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
            var rec = new LayerTableRecord
            {
                Name = name,
                Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByAci, color)
            };
            if (!ltId.IsNull) rec.LinetypeObjectId = ltId;
            ObjectId id = table.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
            return id;
        }
    }
}
