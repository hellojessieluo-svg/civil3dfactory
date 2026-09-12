using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;
using CivTinVolumeSurface = Autodesk.Civil.DatabaseServices.TinVolumeSurface;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeCalculateSurfaceVolume(JsonObject a, Document doc)
            => CalculateSurfaceVolume(a, doc);

        public static JsonNode CalculateSurfaceVolume(JsonObject a, Document doc)
        {
            string baseName = Need(a, "base_surface");
            string compName = Need(a, "comparison_surface");
            // 2026-08-20 裁掉 boundary_polyline：主路径 GetVolumeProperties 无视它（只进报表文字），
            // 只有 TIN 建面失败的退化采样路径才认——同一参数两条路两个语义，给 N 条边界
            // 主路径会返回 N 个一样的数。按闭合边界分块算量走 bounded_volumes。
            if (a["boundary_polyline"] != null)
                throw new InvalidOperationException(
                    "boundary_polyline 已裁掉：本节点算两曲面全范围的量，边界在主路径从不参与计算。"
                    + "按闭合边界分块算量请走 bounded_volumes。");

            double cutFactor = GetDouble(a, "cut_factor", 1.0);
            double fillFactor = GetDouble(a, "fill_factor", 1.0);
            bool exportExcel = GetBool(a, "export_excel", true);
            string excelOutPath = GetString(a, "excel_out_path", null);
            bool drawDwgTable = GetBool(a, "draw_dwg_table", true);

            var tblPtArr = a["table_insertion_point"] as JsonArray;
            Point3d tablePt = new Point3d(0, 0, 0);
            if (tblPtArr != null && tblPtArr.Count >= 2)
            {
                tablePt = new Point3d(
                    tblPtArr[0].GetValue<double>(),
                    tblPtArr[1].GetValue<double>(),
                    tblPtArr.Count >= 3 ? tblPtArr[2].GetValue<double>() : 0.0);
            }

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            double cutVolume = 0.0;
            double fillVolume = 0.0;
            double boundaryArea = 0.0;
            string boundaryInfo = "无（全图范围）";
            string volSurfName = "Vol_" + Sanitize(baseName) + "_" + Sanitize(compName);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId baseId = FindSurfaceId(tr, civ, baseName);
                if (baseId.IsNull) throw new InvalidOperationException("找不到基准曲面 '" + baseName + "'。");
                ObjectId compId = FindSurfaceId(tr, civ, compName);
                if (compId.IsNull) throw new InvalidOperationException("找不到对比曲面 '" + compName + "'。");

                var baseSurf = (CivSurface)tr.GetObject(baseId, OpenMode.ForRead);
                var compSurf = (CivSurface)tr.GetObject(compId, OpenMode.ForRead);

                // 尝试建 TIN Volume Surface
                ObjectId volId = ObjectId.Null;
                try
                {
                    // 覆盖重建：先清理同名曲面
                    ObjectId oldVol = FindSurfaceId(tr, civ, volSurfName);
                    if (!oldVol.IsNull)
                    {
                        var oldS = tr.GetObject(oldVol, OpenMode.ForWrite) as DBObject;
                        if (oldS != null) oldS.Erase();
                    }

                    volId = CivTinVolumeSurface.Create(volSurfName, baseId, compId);
                }
                catch
                {
                    // 若 API 创建异常，降级通过两曲面网格采样近似计算挖填体积
                }

                if (!volId.IsNull)
                {
                    var volSurf = (CivTinVolumeSurface)tr.GetObject(volId, OpenMode.ForRead);
                    try
                    {
                        var props = volSurf.GetVolumeProperties();
                        cutVolume = props.UnadjustedCutVolume;
                        fillVolume = props.UnadjustedFillVolume;
                    }
                    catch
                    {
                        // 若无法读取 volume properties，退回采样计算
                        SampleGridVolume(baseSurf, compSurf, out cutVolume, out fillVolume);
                    }
                }
                else
                {
                    SampleGridVolume(baseSurf, compSurf, out cutVolume, out fillVolume);
                }

                double finalCut = cutVolume * cutFactor;
                double finalFill = fillVolume * fillFactor;
                double finalNet = finalFill - finalCut;

                // 导出 Excel 报表
                List<string> excelFiles = new List<string>();
                string outdir = ResolveOutDir(a, doc);
                if (exportExcel)
                {
                    string excelBaseName = string.IsNullOrEmpty(excelOutPath)
                        ? string.Format("曲面体积计算表_{0}_{1}", Sanitize(baseName), Sanitize(compName))
                        : Path.GetFileNameWithoutExtension(excelOutPath);
                    string targetDir = string.IsNullOrEmpty(excelOutPath)
                        ? outdir
                        : (Path.GetDirectoryName(excelOutPath) ?? outdir);

                    string[] headers = new string[]
                    {
                        "序号", "基准曲面", "对比曲面", "边界范围", "挖方系数", "填方系数",
                        "开挖体积(m³)", "填方体积(m³)", "净体积(m³)"
                    };
                    List<object[]> rows = new List<object[]>
                    {
                        new object[]
                        {
                            1, baseName, compName, boundaryInfo, cutFactor, fillFactor,
                            Math.Round(finalCut, 3), Math.Round(finalFill, 3), Math.Round(finalNet, 3)
                        }
                    };
                    excelFiles = Excel.Write(targetDir, excelBaseName, headers, rows, "xlsx");
                }

                // 绘制 DWG 统计表格
                if (drawDwgTable)
                {
                    CreateVolumeDwgTable(tr, db, tablePt, baseName, compName, finalCut, finalFill, finalNet);
                }

                var reportJson = new JsonObject
                {
                    ["base_surface"] = baseName,
                    ["comparison_surface"] = compName,
                    ["boundary"] = boundaryInfo,
                    ["boundary_area_m2"] = Math.Round(boundaryArea, 2),
                    ["cut_factor"] = cutFactor,
                    ["fill_factor"] = fillFactor,
                    ["raw_cut_volume"] = Math.Round(cutVolume, 3),
                    ["raw_fill_volume"] = Math.Round(fillVolume, 3),
                    ["final_cut_volume"] = Math.Round(finalCut, 3),
                    ["final_fill_volume"] = Math.Round(finalFill, 3),
                    ["final_net_volume"] = Math.Round(finalNet, 3)
                };

                tr.Commit();

                return new JsonObject
                {
                    ["cut_volume"] = Math.Round(finalCut, 3),
                    ["fill_volume"] = Math.Round(finalFill, 3),
                    ["net_volume"] = Math.Round(finalNet, 3),
                    ["volume_report_json"] = reportJson,
                    ["excel_file"] = excelFiles.Count > 0 ? excelFiles[0] : null
                };
            }
        }

        static void SampleGridVolume(CivSurface baseSurf, CivSurface compSurf,
            out double cutVol, out double fillVol)
        {
            cutVol = 0.0;
            fillVol = 0.0;

            Extents3d ext = baseSurf.GeometricExtents;
            double step = 2.0; // 2m 采样网格
            double cellArea = step * step;

            for (double x = ext.MinPoint.X; x <= ext.MaxPoint.X; x += step)
            {
                for (double y = ext.MinPoint.Y; y <= ext.MaxPoint.Y; y += step)
                {
                    double zBase, zComp;
                    if (GridTrySample(baseSurf, new Point3d(x, y, 0), out zBase) &&
                        GridTrySample(compSurf, new Point3d(x, y, 0), out zComp))
                    {
                        double diff = zComp - zBase; // 疏浚开挖: comp < base -> diff < 0
                        if (diff < 0)
                            cutVol += Math.Abs(diff) * cellArea;
                        else
                            fillVol += diff * cellArea;
                    }
                }
            }
        }

        static void CreateVolumeDwgTable(Transaction tr, Database db, Point3d pt,
            string baseName, string compName, double cut, double fill, double net)
        {
            var btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            var tbl = new Table();
            tbl.SetDatabaseDefaults();
            tbl.SetSize(3, 5);
            tbl.Position = pt;

            tbl.Cells[0, 0].TextString = "曲面体积计算与疏浚统计表";
            tbl.Cells[1, 0].TextString = "基准曲面";
            tbl.Cells[1, 1].TextString = "对比曲面";
            tbl.Cells[1, 2].TextString = "挖方(m³)";
            tbl.Cells[1, 3].TextString = "填方(m³)";
            tbl.Cells[1, 4].TextString = "净体积(m³)";

            tbl.Cells[2, 0].TextString = baseName;
            tbl.Cells[2, 1].TextString = compName;
            tbl.Cells[2, 2].TextString = cut.ToString("F2", CultureInfo.InvariantCulture);
            tbl.Cells[2, 3].TextString = fill.ToString("F2", CultureInfo.InvariantCulture);
            tbl.Cells[2, 4].TextString = net.ToString("F2", CultureInfo.InvariantCulture);

            tbl.GenerateLayout();
            btr.AppendEntity(tbl);
            tr.AddNewlyCreatedDBObject(tbl, true);
        }
    }
}
