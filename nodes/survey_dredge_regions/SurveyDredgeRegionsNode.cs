using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// 疏浚区边界盘点与参数试算（只读）。
    ///
    /// 疏浚放坡链路的第一步：把边界图层上的闭合多段线逐个盘出来——句柄、面积、周长、
    /// 形心、落在区内的文字（自动认区名，如 CR1 / DK1#地块）、边界处原地形高程统计。
    ///
    /// 给了 bottom_elev + slope_ratio_m 还会**按 create_dredge_grading 同一个锥面模型**
    /// 网格试算方量：不建曲面、不改图，先看这套参数出来的量对不对，再决定拿哪套参数去建面。
    /// 「参数从数据推」的那一步就在这里；产出直接就是 create_dredge_grading 的 regions 参数骨架。
    ///
    /// 试算用 grid_step 网格积分，比下游 TIN 三角网体积法略糙（默认 5m 网格约 1% 量级），
    /// 只用来定参数，不作为出量口径——出量走 calculate_surface_volume。
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeSurveyDredgeRegions(JsonObject a, Document doc)
        {
            string bndLayer = Need(a, "boundary_layer");
            string surfName = GetString(a, "surface", null);
            string labelLayer = GetString(a, "label_layer", null);

            string startSurfName = GetString(a, "start_surface", null);

            bool hasBottom = a["bottom_elev"] != null;
            double gBottom = GetDouble(a, "bottom_elev", 0.0);
            double gSlope = GetDouble(a, "slope_ratio_m", 5.0);
            if (gSlope <= 0) throw new InvalidOperationException("slope_ratio_m 必须大于 0（1:m 的 m）。");
            bool hasStart = a["start_elev"] != null;

            var regionSpecs = a["regions"] as JsonArray;

            double sampleStep = GetDouble(a, "sample_step", 2.0);
            double gridStep = GetDouble(a, "grid_step", 5.0);
            if (sampleStep <= 0) throw new InvalidOperationException("sample_step 必须大于 0。");
            if (gridStep <= 0) throw new InvalidOperationException("grid_step 必须大于 0。");

            var interfaceLayers = a["interface_layers"] as JsonArray;

            bool exportExcel = GetBool(a, "export_excel", false);
            string excelOutPath = GetString(a, "excel_out_path", null);
            string excelFormat = GetString(a, "excel_format", "xlsx");

            var handles = a["boundaries"] as JsonArray;

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            var perRegion = new JsonArray();
            var warnings = new JsonArray();
            var excelFiles = new List<string>();
            double totalArea = 0, totalCut = 0, totalFill = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivSurface ground = null;
                if (!string.IsNullOrEmpty(surfName))
                {
                    ObjectId gsId = FindSurfaceId(tr, civ, surfName);
                    if (gsId.IsNull) throw new InvalidOperationException("找不到原地形曲面 '" + surfName + "'。");
                    ground = (CivSurface)tr.GetObject(gsId, OpenMode.ForRead);
                }
                if (ground == null && !hasStart)
                    warnings.Add((JsonNode)"没给 surface 也没给 start_elev：只报几何，不报高程与试算方量。");

                CivSurface startSurface = null;
                if (!string.IsNullOrEmpty(startSurfName))
                {
                    ObjectId ssId = FindSurfaceId(tr, civ, startSurfName);
                    if (ssId.IsNull) throw new InvalidOperationException("找不到起坡高程曲面 '" + startSurfName + "'。");
                    startSurface = (CivSurface)tr.GetObject(ssId, OpenMode.ForRead);
                }

                List<ObjectId> bndIds = DredgeCollectBoundaries(tr, db, bndLayer, handles);
                if (bndIds.Count == 0)
                    throw new InvalidOperationException(
                        "图层 '" + bndLayer + "' 上没有闭合多段线" +
                        (handles != null && handles.Count > 0 ? "（或句柄白名单一个都没命中）" : "") + "。");

                List<DredgeText> texts = DredgeCollectTexts(tr, db, labelLayer);
                List<Curve> interfaces = DredgeCollectInterfaces(tr, db, interfaceLayers);
                if (interfaceLayers != null && interfaceLayers.Count > 0 && interfaces.Count == 0)
                    warnings.Add((JsonNode)"interface_layers 给了但那些图层上一条曲线都没有，分界线口径没生效。");

                int seq = 0;
                foreach (ObjectId bid in bndIds)
                {
                    seq++;
                    var pl = (Polyline)tr.GetObject(bid, OpenMode.ForRead);
                    string handle = pl.Handle.ToString();

                    DredgeRing ring = DredgeBuildRing(pl, sampleStep);
                    if (ring.Count < 3)
                    {
                        warnings.Add((JsonNode)("边界 " + handle + " 采样点不足，跳过。"));
                        continue;
                    }

                    var labels = new JsonArray();
                    foreach (DredgeText t in texts)
                        if (GridPointInPolygon(t.P, ring.P)) labels.Add((JsonNode)t.S);
                    string label = labels.Count > 0 ? labels[0].ToString() : null;
                    JsonObject rspec = DredgeFindSpec(regionSpecs, handle, label);
                    string id = rspec != null ? GetString(rspec, "id", null) : null;
                    if (string.IsNullOrEmpty(id)) id = label;
                    if (string.IsNullOrEmpty(id)) id = "R" + seq.ToString("00", CultureInfo.InvariantCulture);

                    // 与 create_dredge_grading 共用的参数合并口径：盘点试算用的就是建面要用的那套
                    DredgeZSpec zspec = DredgeMergeZSpec(a, rspec, gBottom, gSlope, startSurface, interfaces);
                    if (!hasBottom) zspec.Bottom = double.MinValue;
                    double bottom = zspec.Bottom;
                    double slopeM = zspec.SlopeM;
                    if (slopeM <= 0)
                        throw new InvalidOperationException("分区 '" + id + "' 的 slope_ratio_m 必须大于 0。");

                    double cx = 0, cy = 0;
                    for (int i = 0; i < ring.Count; i++) { cx += ring.P[i].X; cy += ring.P[i].Y; }
                    cx /= ring.Count; cy /= ring.Count;

                    var row = new JsonObject
                    {
                        ["id"] = id,
                        ["handle"] = handle,
                        ["layer"] = pl.Layer,
                        ["labels"] = labels,
                        ["area"] = Math.Round(pl.Area, 2),
                        ["perimeter"] = Math.Round(pl.Length, 2),
                        ["vertices"] = pl.NumberOfVertices,
                        ["centroid"] = new JsonArray { Math.Round(cx, 3), Math.Round(cy, 3) },
                        ["bbox"] = new JsonArray {
                            Math.Round(ring.MinX, 3), Math.Round(ring.MinY, 3),
                            Math.Round(ring.MaxX, 3), Math.Round(ring.MaxY, 3) },
                        ["winding"] = DredgeSignedArea(ring.P) > 0 ? "ccw" : "cw",
                        // 本区实际生效的参数（全局 + regions 覆盖后），直接可抄进 create_dredge_grading
                        ["bottom_elev"] = hasBottom ? (JsonNode)bottom : null,
                        ["slope_ratio_m"] = slopeM,
                        ["start_elev_mode"] = zspec.Mode,
                        ["start_elev"] = zspec.HasStart ? (JsonNode)zspec.Start : null,
                        ["start_elev_cap"] = zspec.HasCap ? (JsonNode)zspec.Cap : null
                    };
                    totalArea += pl.Area;

                    // ---- 边界处起坡高程统计 ----
                    double zMax = double.MinValue;
                    if (ground != null || hasStart)
                    {
                        int off, onIface;
                        DredgeFillRingZ(ring, ground, zspec, out off, out onIface);

                        double zSum = 0, zMin = double.MaxValue;
                        zMax = double.MinValue;
                        for (int i = 0; i < ring.Count; i++)
                        {
                            double z = ring.Z[i];
                            zSum += z;
                            if (z < zMin) zMin = z;
                            if (z > zMax) zMax = z;
                        }
                        row["z_top_min"] = Math.Round(zMin, 3);
                        row["z_top_max"] = Math.Round(zMax, 3);
                        row["z_top_mean"] = Math.Round(zSum / ring.Count, 3);
                        row["ring_points"] = ring.Count;
                        row["off_surface_points"] = off;
                        row["interface_points"] = onIface;
                        if (off > 0)
                            warnings.Add((JsonNode)("[" + id + "] " + off + "/" + ring.Count +
                                                    " 个边界采样点落在原地形曲面外。"));
                    }

                    // ---- 按锥面模型网格试算 ----
                    if (hasBottom && ground != null)
                    {
                        double bandMax = (zMax - bottom) * slopeM;
                        if (bandMax < 0) bandMax = 0;
                        ring.BuildIndex(Math.Max(bandMax * 1.05, Math.Max(gridStep, 5.0)));

                        double cell = gridStep * gridStep;
                        double cut = 0, fill = 0, wet = 0, deepSum = 0;
                        int cells = 0, offCells = 0;
                        int gx0 = (int)Math.Floor(ring.MinX / gridStep);
                        int gx1 = (int)Math.Ceiling(ring.MaxX / gridStep);
                        int gy0 = (int)Math.Floor(ring.MinY / gridStep);
                        int gy1 = (int)Math.Ceiling(ring.MaxY / gridStep);
                        for (int ix = gx0; ix <= gx1; ix++)
                        {
                            double x = ix * gridStep;
                            for (int iy = gy0; iy <= gy1; iy++)
                            {
                                double y = iy * gridStep;
                                if (!GridPointInPolygon(new Point2d(x, y), ring.P)) continue;
                                cells++;

                                double d, zTop, zDes;
                                if (!ring.Nearest(x, y, out d, out zTop)) zDes = bottom;
                                else { zDes = zTop - d / slopeM; if (zDes < bottom) zDes = bottom; }

                                double zg;
                                if (!GridTrySample(ground, new Point3d(x, y, 0), out zg)) { offCells++; continue; }
                                double h = zg - zDes;
                                if (h > 0) { cut += h * cell; wet += cell; deepSum += h; }
                                else fill += -h * cell;
                            }
                        }
                        row["band_max"] = Math.Round(bandMax, 2);
                        row["est_grid_cells"] = cells;
                        row["est_area"] = Math.Round(cells * cell, 2);
                        row["est_cut_volume"] = Math.Round(cut, 2);
                        row["est_fill_volume"] = Math.Round(fill, 2);
                        row["est_mean_depth"] = wet > 0 ? Math.Round(deepSum * cell / wet, 3) : 0.0;
                        row["est_off_surface_cells"] = offCells;
                        totalCut += cut;
                        totalFill += fill;
                        if (offCells > 0)
                            warnings.Add((JsonNode)("[" + id + "] 试算时 " + offCells + "/" + cells +
                                                    " 个网格点在原地形曲面外，已跳过（方量偏小）。"));
                    }

                    perRegion.Add(row);
                }

                // ---- 参数表骨架导出 ----
                if (exportExcel)
                {
                    string baseName = string.IsNullOrEmpty(excelOutPath)
                        ? "疏浚区参数表_" + Sanitize(bndLayer)
                        : System.IO.Path.GetFileNameWithoutExtension(excelOutPath);
                    string dir = string.IsNullOrEmpty(excelOutPath)
                        ? ResolveOutDir(a, doc)
                        : (System.IO.Path.GetDirectoryName(excelOutPath) ?? ResolveOutDir(a, doc));

                    string[] headers = {
                        "序号", "编号", "句柄", "面积(m²)", "周长(m)",
                        "疏浚底高程(m)", "边坡(1:m)", "起坡高程min(m)", "起坡高程max(m)",
                        "放坡带宽max(m)", "试算挖方(m³)", "试算填方(m³)", "试算平均挖深(m)", "备注"
                    };
                    var rows = new List<object[]>();
                    int i2 = 0;
                    foreach (JsonNode n in perRegion)
                    {
                        var r = (JsonObject)n;
                        i2++;
                        rows.Add(new object[] {
                            i2,
                            r["id"] != null ? r["id"].ToString() : "",
                            r["handle"] != null ? r["handle"].ToString() : "",
                            r["area"] != null ? r["area"].ToString() : "",
                            r["perimeter"] != null ? r["perimeter"].ToString() : "",
                            r["bottom_elev"] != null ? r["bottom_elev"].ToString() : "",
                            r["slope_ratio_m"] != null ? r["slope_ratio_m"].ToString() : "",
                            r["z_top_min"] != null ? r["z_top_min"].ToString() : "",
                            r["z_top_max"] != null ? r["z_top_max"].ToString() : "",
                            r["band_max"] != null ? r["band_max"].ToString() : "",
                            r["est_cut_volume"] != null ? r["est_cut_volume"].ToString() : "",
                            r["est_fill_volume"] != null ? r["est_fill_volume"].ToString() : "",
                            r["est_mean_depth"] != null ? r["est_mean_depth"].ToString() : "",
                            ""
                        });
                    }
                    rows.Add(new object[] {
                        "合计", "", "", Math.Round(totalArea, 2), "", "", "", "", "", "",
                        Math.Round(totalCut, 2), Math.Round(totalFill, 2), "", ""
                    });
                    excelFiles = Excel.Write(dir, baseName, headers, rows, excelFormat);
                }

                // 只读节点：不 Commit，图纸一点不动
            }

            return new JsonObject
            {
                ["regions"] = perRegion.Count,
                ["total_area"] = Math.Round(totalArea, 2),
                ["total_est_cut_volume"] = Math.Round(totalCut, 2),
                ["total_est_fill_volume"] = Math.Round(totalFill, 2),
                ["per_region"] = perRegion,
                ["excel_files"] = DredgeToJsonArray(excelFiles),
                ["warnings"] = warnings
            };
        }

        static JsonArray DredgeToJsonArray(List<string> items)
        {
            var arr = new JsonArray();
            foreach (string s in items) arr.Add((JsonNode)s);
            return arr;
        }
    }
}
