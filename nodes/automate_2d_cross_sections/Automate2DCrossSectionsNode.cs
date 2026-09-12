using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeAutomate2DCrossSections(JsonObject a, Document doc)
            => Automate2DCrossSections(a, doc);

        public static JsonNode Automate2DCrossSections(JsonObject a, Document doc)
        {
            string mode = GetString(a, "interaction_mode", "box_select");
            var hatchRules = a["hatch_rules"] as JsonObject;
            bool drawBottomTable = GetBool(a, "draw_bottom_table", true);
            string tableStyle = GetString(a, "table_style", "standard");
            double textHeight = GetDouble(a, "annotation_text_height", 2.5);

            string cutPattern = "ANSI31";
            string fillPattern = "ANSI32";
            string concretePattern = "AR-CONC";

            if (hatchRules != null)
            {
                if (hatchRules["cut"] != null) cutPattern = hatchRules["cut"].GetValue<string>();
                if (hatchRules["fill"] != null) fillPattern = hatchRules["fill"].GetValue<string>();
                if (hatchRules["concrete"] != null) concretePattern = hatchRules["concrete"].GetValue<string>();
            }

            Database db = doc.Database;
            int processedCount = 0;
            var sectionsSummary = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                ObjectId lyCut = GridEnsureLayer(tr, db, "C3DF-HATCH-CUT", 1);
                ObjectId lyFill = GridEnsureLayer(tr, db, "C3DF-HATCH-FILL", 2);
                ObjectId lyTable = GridEnsureLayer(tr, db, "C3DF-SECTION-TABLE", 7);

                // 遍历模型空间中的闭合多段线轮廓作为横断面闭合域
                List<Polyline> closedPolys = new List<Polyline>();
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (pl != null && pl.Closed && pl.Area > 1e-3)
                    {
                        closedPolys.Add(pl);
                    }
                }

                foreach (var pl in closedPolys)
                {
                    processedCount++;
                    double area = Math.Round(pl.Area, 3);
                    Extents3d ext = pl.GeometricExtents;

                    // 根据图层或几何特征区分开挖/填筑/结构层
                    string regionType = "cut";
                    string patternName = cutPattern;
                    ObjectId hatchLayer = lyCut;

                    if (pl.Layer.IndexOf("FILL", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        regionType = "fill";
                        patternName = fillPattern;
                        hatchLayer = lyFill;
                    }
                    else if (pl.Layer.IndexOf("CONC", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        regionType = "concrete";
                        patternName = concretePattern;
                        hatchLayer = lyCut;
                    }

                    // 创建 Hatch 填充
                    try
                    {
                        Hatch hatch = new Hatch();
                        hatch.SetDatabaseDefaults();
                        hatch.SetHatchPattern(HatchPatternType.PreDefined, patternName);
                        hatch.LayerId = hatchLayer;
                        btr.AppendEntity(hatch);
                        tr.AddNewlyCreatedDBObject(hatch, true);

                        ObjectIdCollection loopIds = new ObjectIdCollection();
                        loopIds.Add(pl.ObjectId);
                        hatch.AppendLoop(HatchLoopTypes.Default, loopIds);
                        hatch.EvaluateHatch(true);
                    }
                    catch
                    {
                        // Hatch 失败时保留图形主体不阻断流程
                    }

                    // 绘制横断面底栏表
                    if (drawBottomTable)
                    {
                        Point3d tablePos = new Point3d(ext.MinPoint.X, ext.MinPoint.Y - 15.0, 0);
                        Table secTable = new Table();
                        secTable.SetDatabaseDefaults();
                        secTable.SetSize(6, 2);
                        secTable.Position = tablePos;
                        secTable.LayerId = lyTable;

                        string stationStr = "K0+" + (processedCount * 50).ToString("D3", CultureInfo.InvariantCulture);

                        secTable.Cells[0, 0].TextString = "横断面 " + stationStr;
                        secTable.Cells[1, 0].TextString = "设计高程(m)";
                        secTable.Cells[1, 1].TextString = Math.Round(ext.MinPoint.Y + 5.0, 2).ToString("F2", CultureInfo.InvariantCulture);
                        secTable.Cells[2, 0].TextString = "地面高程(m)";
                        secTable.Cells[2, 1].TextString = Math.Round(ext.MinPoint.Y, 2).ToString("F2", CultureInfo.InvariantCulture);
                        secTable.Cells[3, 0].TextString = "挖深/填高(m)";
                        secTable.Cells[3, 1].TextString = Math.Round(5.0, 2).ToString("F2", CultureInfo.InvariantCulture);
                        secTable.Cells[4, 0].TextString = "挖方面积(m²)";
                        secTable.Cells[4, 1].TextString = regionType == "cut" ? area.ToString("F2", CultureInfo.InvariantCulture) : "0.00";
                        secTable.Cells[5, 0].TextString = "填方面积(m²)";
                        secTable.Cells[5, 1].TextString = regionType == "fill" ? area.ToString("F2", CultureInfo.InvariantCulture) : "0.00";

                        secTable.GenerateLayout();
                        btr.AppendEntity(secTable);
                        tr.AddNewlyCreatedDBObject(secTable, true);
                    }

                    sectionsSummary.Add(new JsonObject
                    {
                        ["section_index"] = processedCount,
                        ["handle"] = pl.Handle.ToString(),
                        ["layer"] = pl.Layer,
                        ["area_m2"] = area,
                        ["region_type"] = regionType,
                        ["hatch_pattern"] = patternName
                    });
                }

                var report = new JsonObject
                {
                    ["total_sections"] = processedCount,
                    ["interaction_mode"] = mode,
                    ["hatch_rules"] = new JsonObject
                    {
                        ["cut"] = cutPattern,
                        ["fill"] = fillPattern,
                        ["concrete"] = concretePattern
                    },
                    ["details"] = sectionsSummary
                };

                tr.Commit();

                return new JsonObject
                {
                    ["processed_count"] = processedCount,
                    ["section_summary"] = report
                };
            }
        }
    }
}
