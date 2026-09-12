using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Civil3DFactory.Geometry;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// grid_earthwork_balance：网格土方平衡（体积曲面上撒格 → 格间运输问题 → 调配箭头+运距直方图）。
    ///
    /// 量的真值走 GetBoundedVolumes（TIN），网格积分先对真值做闭合差检查再配平——
    /// 格距只影响运距分辨率，不影响量。算法核心在同目录 GridBalanceCore.cs（WaterBox C3DF-GridBalance 同源）。
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeGridEarthworkBalance(JsonObject a, Document doc)
        {
            // ---- 曲面参数（与 bounded_volumes 同口径）----
            string volName = GetString(a, "volume_surface", null);
            string baseName = GetString(a, "base_surface", null);
            string compName = GetString(a, "comparison_surface", null);
            if (string.IsNullOrWhiteSpace(volName) &&
                (string.IsNullOrWhiteSpace(baseName) || string.IsNullOrWhiteSpace(compName)))
                throw new InvalidOperationException(
                    "给 volume_surface（已有体积曲面名），或者 base_surface + comparison_surface 两个都给。");

            string layer = GetString(a, "layer", null);
            var handles = a["handles"] as JsonArray;
            if (string.IsNullOrWhiteSpace(layer) && (handles == null || handles.Count == 0))
                throw new InvalidOperationException("layer 与 handles 至少给一个来圈定边界。");

            double step = GetDouble(a, "step", 5.0);
            double ringStep = GetDouble(a, "sample_step", 1.0);
            int maxNodes = (int)GetDouble(a, "solver_max_nodes", 900);
            double closureWarn = GetDouble(a, "closure_warn_pct", 2.0);
            double closureFail = GetDouble(a, "closure_fail_pct", 10.0);
            double factor = GetDouble(a, "volume_factor", 1.0);       // 报表口径系数（如 1.06），几何方另列
            bool drawCells = GetBool(a, "draw_cells", true);
            bool drawArrows = GetBool(a, "draw_arrows", true);
            int maxArrows = (int)GetDouble(a, "max_arrows", 60);
            bool clearPrevious = GetBool(a, "clear_previous", true);
            bool exportExcel = GetBool(a, "export_excel", true);
            string excelFormat = GetString(a, "excel_format", "xlsx");
            double[] bands = ParseBands(a["bands"] as JsonArray, new double[] { 30, 50, 100, 200, 300, 500 });

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (handles != null)
                foreach (JsonNode h in handles) if (h != null) wanted.Add(h.ToString().Trim());

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var warnings = new JsonArray();
            var perBoundary = new JsonArray();
            var excelRows = new List<object[]>();
            var histRowsAll = new List<object[]>();
            string usedVol = null;
            int totalArrowsDrawn = 0, totalCellsDrawn = 0, erased = 0;
            double gTinCut = 0, gTinFill = 0, gMoved = 0, gVolDist = 0, gShort = 0, gSurp = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // 体积曲面：有名取名，没有就 base+comp 现建（命名与 bounded_volumes 一致，可复用）
                ObjectId volId;
                if (!string.IsNullOrWhiteSpace(volName))
                {
                    volId = FindSurfaceId(tr, civ, volName);
                    if (volId.IsNull) throw new InvalidOperationException("找不到体积曲面 '" + volName + "'。");
                }
                else
                {
                    ObjectId bId = FindSurfaceId(tr, civ, baseName);
                    if (bId.IsNull) throw new InvalidOperationException("找不到基准曲面 '" + baseName + "'。");
                    ObjectId cId = FindSurfaceId(tr, civ, compName);
                    if (cId.IsNull) throw new InvalidOperationException("找不到对比曲面 '" + compName + "'。");
                    volName = "Vol_" + Sanitize(baseName) + "_" + Sanitize(compName);
                    ObjectId old = FindSurfaceId(tr, civ, volName);
                    volId = old.IsNull
                        ? Autodesk.Civil.DatabaseServices.TinVolumeSurface.Create(volName, bId, cId)
                        : old;
                }
                var vol = (CivSurface)tr.GetObject(volId, OpenMode.ForRead);
                usedVol = vol.Name;
                if (!(vol is Autodesk.Civil.DatabaseServices.TinVolumeSurface))
                    warnings.Add((JsonNode)("'" + usedVol + "' 不是体积曲面，dz 不是挖填深。"));

                // 图层与清场
                GridBalanceCore.EnsureLayer(tr, db, GridBalanceCore.LayerCut, 11, 55);
                GridBalanceCore.EnsureLayer(tr, db, GridBalanceCore.LayerFill, 73, 55);
                GridBalanceCore.EnsureLayer(tr, db, GridBalanceCore.LayerArrow, 4, 0);
                GridBalanceCore.EnsureLayer(tr, db, GridBalanceCore.LayerText, 2, 0);
                if (clearPrevious) erased = GridBalanceCore.ClearOwnLayers(tr, db);

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var space = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                int seq = 0;
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (pl == null) continue;
                    string handle = pl.Handle.ToString();
                    if (wanted.Count > 0) { if (!wanted.Contains(handle)) continue; }
                    else if (!string.Equals(pl.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;

                    seq++;
                    string label = "B" + seq.ToString("00");
                    var namesObj = a["names"] as JsonObject;
                    if (namesObj != null && namesObj[handle] != null) label = namesObj[handle].ToString();

                    DredgeRing ring = DredgeBuildRing(pl, ringStep);
                    if (ring.Count < 3)
                    { warnings.Add((JsonNode)(label + "（" + handle + "）成不了环，跳过。")); continue; }

                    var ringPts = new List<Point2d>(ring.Count);
                    var boundedPts = new Point3dCollection();
                    for (int i = 0; i < ring.Count; i++)
                    {
                        ringPts.Add(new Point2d(ring.P[i].X, ring.P[i].Y));
                        boundedPts.Add(new Point3d(ring.P[i].X, ring.P[i].Y, 0));
                    }
                    if (boundedPts.Count > 0) boundedPts.Add(boundedPts[0]);

                    // TIN 真值
                    double tinCut, tinFill;
                    try
                    {
                        var info = vol.GetBoundedVolumes(boundedPts);
                        tinCut = info.Cut; tinFill = info.Fill;
                    }
                    catch (System.Exception ex)
                    { warnings.Add((JsonNode)(label + "（" + handle + "）GetBoundedVolumes 失败：" + ex.Message)); continue; }

                    // 撒格 + 配平 + 求解
                    var res = GridBalanceCore.BuildCells(ringPts, step,
                        (x, y) =>
                        {
                            try { return vol.FindElevationAtXY(x, y); }
                            catch (System.Exception) { return double.NaN; }
                        });
                    if (res.CutCells.Count + res.FillCells.Count == 0)
                    { warnings.Add((JsonNode)(label + "：边界内一格有效挖填都没有（曲面外 " + res.CellsOffSurface + " 格）。")); continue; }

                    GridBalanceCore.Reconcile(res, tinCut, tinFill);
                    if (res.ClosureCutPct > closureFail || res.ClosureFillPct > closureFail)
                        throw new InvalidOperationException(label + " 闭合差超限：挖 " + res.ClosureCutPct.ToString("0.0")
                            + "% / 填 " + res.ClosureFillPct.ToString("0.0") + "%（阈值 " + closureFail
                            + "%）——网格积分对不上 TIN，多半是格距太粗或边界压曲面外沿。");
                    if (res.ClosureCutPct > closureWarn || res.ClosureFillPct > closureWarn)
                        warnings.Add((JsonNode)(label + " 闭合差偏大：挖 " + res.ClosureCutPct.ToString("0.0")
                            + "% / 填 " + res.ClosureFillPct.ToString("0.0") + "%（已配平到 TIN 真值）。"));

                    List<GridBalanceCore.Cell> sCut, sFill;
                    res.SolverStep = GridBalanceCore.Coarsen(res, step, maxNodes, out sCut, out sFill);
                    GridBalanceCore.Solve(res, sCut, sFill);
                    GridBalanceCore.Histogram(res, bands);

                    // 画
                    int cellsDrawn = 0, arrowsDrawn = 0;
                    if (drawCells)
                    {
                        cellsDrawn += GridBalanceCore.DrawCells(tr, space, res.CutCells, step, GridBalanceCore.LayerCut);
                        cellsDrawn += GridBalanceCore.DrawCells(tr, space, res.FillCells, step, GridBalanceCore.LayerFill);
                    }
                    if (drawArrows && res.Flows.Count > 0)
                    {
                        var arrows = GridBalanceCore.AggregateArrows(res, res.SolverStep * 4, maxArrows);
                        double maxV = 0; foreach (var f in arrows) if (f.Vol > maxV) maxV = f.Vol;
                        arrowsDrawn = GridBalanceCore.DrawArrows(tr, space, arrows, maxV,
                            res.SolverStep * 0.25, res.SolverStep * 1.2);
                        // 前 10 支标方量
                        for (int i = 0; i < arrows.Count && i < 10; i++)
                        {
                            var f = arrows[i];
                            GridBalanceCore.DrawText(tr, space, (f.SX + f.TX) / 2, (f.SY + f.TY) / 2,
                                (f.Vol >= 10000 ? (f.Vol / 10000).ToString("0.00") + "万" : f.Vol.ToString("0")),
                                res.SolverStep * 0.9);
                        }
                        GridBalanceCore.DrawText(tr, space,
                            (pl.GeometricExtents.MinPoint.X + pl.GeometricExtents.MaxPoint.X) / 2,
                            pl.GeometricExtents.MaxPoint.Y + res.SolverStep * 3,
                            label + " 内调 " + (res.InternalMoved / 10000).ToString("0.00") + "万m³ 均距 "
                            + res.AvgDist.ToString("0") + "m", res.SolverStep * 1.4);
                    }
                    totalCellsDrawn += cellsDrawn; totalArrowsDrawn += arrowsDrawn;

                    gTinCut += tinCut; gTinFill += tinFill; gMoved += res.InternalMoved;
                    gVolDist += res.ObjVolDist; gShort += res.Shortfall; gSurp += res.Surplus;

                    var bandJson = new JsonArray();
                    foreach (string s in GridBalanceCore.HistogramLines(res)) bandJson.Add((JsonNode)s);

                    perBoundary.Add(new JsonObject
                    {
                        ["id"] = label,
                        ["handle"] = handle,
                        ["area"] = Round(pl.Area, 1),
                        ["tin_cut"] = Round(tinCut, 1),
                        ["tin_fill"] = Round(tinFill, 1),
                        ["grid_cut_raw"] = Round(res.GridCut, 1),
                        ["grid_fill_raw"] = Round(res.GridFill, 1),
                        ["closure_cut_pct"] = Round(res.ClosureCutPct, 2),
                        ["closure_fill_pct"] = Round(res.ClosureFillPct, 2),
                        ["cells_inside"] = res.CellsInside,
                        ["cells_off_surface"] = res.CellsOffSurface,
                        ["cut_cells"] = res.CutCells.Count,
                        ["fill_cells"] = res.FillCells.Count,
                        ["step"] = step,
                        ["solver_step"] = Round(res.SolverStep, 2),
                        ["internal_moved"] = Round(res.InternalMoved, 1),
                        ["internal_moved_adjusted"] = Round(res.InternalMoved * factor, 1),
                        ["avg_dist_m"] = Round(res.AvgDist, 1),
                        ["shortfall_borrow"] = Round(res.Shortfall, 1),
                        ["surplus_export"] = Round(res.Surplus, 1),
                        ["flows"] = res.Flows.Count,
                        ["arrows_drawn"] = arrowsDrawn,
                        ["solve_ms"] = res.SolveMs,
                        ["histogram"] = bandJson,
                    });

                    excelRows.Add(new object[] { label, handle, Round(pl.Area, 1),
                        Round(tinCut, 1), Round(tinFill, 1),
                        Round(res.ClosureCutPct, 2), Round(res.ClosureFillPct, 2),
                        step, Round(res.SolverStep, 2),
                        Round(res.InternalMoved, 1), Round(res.AvgDist, 1),
                        Round(res.Shortfall, 1), Round(res.Surplus, 1),
                        factor, Round(res.InternalMoved * factor, 1) });
                    for (int b = 0; b <= bands.Length; b++)
                    {
                        if (res.BandVols[b] < 0.005) continue;
                        string bl = b == 0 ? "≤" + bands[0]
                            : b == bands.Length ? ">" + bands[bands.Length - 1]
                            : bands[b - 1] + "~" + bands[b];
                        histRowsAll.Add(new object[] { label, bl, Round(res.BandVols[b], 1),
                            Round(res.BandVols[b] * factor, 1),
                            Round(res.InternalMoved > 1e-9 ? res.BandVols[b] / res.InternalMoved * 100 : 0, 1) });
                    }
                }
                tr.Commit();
            }

            if (perBoundary.Count == 0)
                throw new InvalidOperationException("一条边界都没算成（layer='" + (layer ?? "") + "'，点名 "
                    + wanted.Count + " 条）。" + (warnings.Count > 0 ? "第一条原因：" + warnings[0] : ""));

            var files = new JsonArray();
            string outdir = null;
            if (exportExcel)
            {
                outdir = ResolveOutDir(a, doc);
                foreach (string p in Excel.Write(outdir, "网格土方平衡_" + Sanitize(usedVol),
                                                 HeadersGridBalance, excelRows, excelFormat))
                    files.Add(p);
                foreach (string p in Excel.Write(outdir, "运距分档_" + Sanitize(usedVol),
                                                 HeadersGridBalanceHist, histRowsAll, excelFormat))
                    files.Add(p);
            }

            return new JsonObject
            {
                ["volume_surface"] = usedVol,
                ["boundaries"] = perBoundary.Count,
                ["total_tin_cut"] = Round(gTinCut, 1),
                ["total_tin_fill"] = Round(gTinFill, 1),
                ["total_internal_moved"] = Round(gMoved, 1),
                ["total_internal_moved_adjusted"] = Round(gMoved * factor, 1),
                ["volume_factor"] = factor,
                ["avg_dist_m"] = Round(gMoved > 1e-9 ? gVolDist / gMoved : 0, 1),
                ["total_shortfall_borrow"] = Round(gShort, 1),
                ["total_surplus_export"] = Round(gSurp, 1),
                ["cells_drawn"] = totalCellsDrawn,
                ["arrows_drawn"] = totalArrowsDrawn,
                ["cleared_old"] = erased,
                ["per_boundary"] = perBoundary,
                ["excel_files"] = files,
                ["outdir"] = outdir,
                ["warnings"] = warnings,
            };
        }

        static double[] ParseBands(JsonArray arr, double[] dflt)
        {
            if (arr == null || arr.Count == 0) return dflt;
            var list = new List<double>();
            foreach (JsonNode n in arr) if (n != null) list.Add(double.Parse(n.ToString()));
            list.Sort();
            return list.ToArray();
        }

        static readonly string[] HeadersGridBalance = {
            "编号", "句柄", "边界面积m2", "TIN挖方m3", "TIN填方m3",
            "闭合差挖%", "闭合差填%", "统计格距m", "求解格距m",
            "内部调配m3", "加权平均运距m", "缺口借方m3", "富余弃方m3", "系数", "调配×系数m3" };

        static readonly string[] HeadersGridBalanceHist = {
            "边界", "运距档m", "方量m3", "方量×系数m3", "占比%" };
    }
}
