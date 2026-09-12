using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        /// <summary>
        /// 拆堤工程量（平均断面法）：逐断面量清表面积与底土开挖面积，按测线相邻桩号平均断面法出方量表。
        /// 搬自拆堤插件 V1 的 C3DF-CalcVolume。
        ///
        /// 关键性质（别改）：面积算的是**图上实际的** C3DF-STRIP-LINE / C3DF-CUT-LINE，
        /// 不是 generate_demolition_design_lines 的内存结果——边坡等特殊断面人工修过边界后，
        /// 重跑本节点就是按修过的线算。所以本节点必须在设计线画完（并修完）之后跑。
        /// </summary>
        static JsonNode RunNodeComputeEmbankmentDemolition(JsonObject a, Document doc)
            => ComputeEmbankmentDemolition(a, doc);

        public static JsonNode ComputeEmbankmentDemolition(JsonObject a, Document doc)
        {
            var opt = new MeasuredSectionOptions();
            opt.GroundLayer = GetString(a, "ground_layer", opt.GroundLayer);
            opt.ColumnLayer = GetString(a, "column_layer", opt.ColumnLayer);
            opt.TitleLayer = GetString(a, "title_layer", opt.TitleLayer);
            opt.TitleRegex = GetString(a, "title_regex", opt.TitleRegex);
            opt.OffsetTolerance = GetDouble(a, "offset_tolerance", opt.OffsetTolerance);
            opt.ElevTolerance = GetDouble(a, "elev_tolerance", opt.ElevTolerance);
            opt.TableDepth = GetDouble(a, "table_depth", opt.TableDepth);
            opt.PairTolX = GetDouble(a, "pair_tol_x", opt.PairTolX);
            Sections.Check(opt);

            Regex filter = Sections.CompileFilter(GetString(a, "line_filter", null));

            string layStrip = GetString(a, "strip_line_layer", "C3DF-STRIP-LINE");
            string layExc = GetString(a, "excavation_line_layer", "C3DF-CUT-LINE");
            double ownMargin = GetDouble(a, "own_margin", 30.0);
            if (ownMargin <= 0) throw new InvalidOperationException("own_margin 必须大于 0（图纸单位）。");

            bool annotate = GetBool(a, "annotate_sections", true);
            string layNote = GetString(a, "annotation_layer", "C3DF-QTY-LABEL");
            double textHeight = GetDouble(a, "text_height", 2.5);
            string textStyle = GetString(a, "text_style", null);
            bool clearExisting = GetBool(a, "clear_existing", true);
            if (textHeight <= 0) throw new InvalidOperationException("text_height 必须大于 0。");

            bool exportExcel = GetBool(a, "export_excel", true);
            string excelFormat = GetString(a, "excel_format", "xlsx");   // 归一化/校验在 Excel.Write

            Database db = doc.Database;
            var rows = new List<DikeQtyRow>();
            var warnings = new JsonArray();
            int labels = 0, cleared = 0, unassignedStrip = 0, unassignedExc = 0, noLine = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord space = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                if (clearExisting && annotate)
                    cleared = DikeEraseOnLayers(tr, space, new[] { layNote });

                var stripPls = new List<Polyline>();
                var excPls = new List<Polyline>();
                foreach (ObjectId id in space)
                {
                    Polyline pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (pl == null) continue;
                    if (string.Equals(pl.Layer, layStrip, StringComparison.OrdinalIgnoreCase)) stripPls.Add(pl);
                    else if (string.Equals(pl.Layer, layExc, StringComparison.OrdinalIgnoreCase)) excPls.Add(pl);
                }
                if (stripPls.Count == 0 && excPls.Count == 0)
                    throw new InvalidOperationException(
                        "图层 '" + layStrip + "' / '" + layExc + "' 上没有设计线；" +
                        "先跑 generate_demolition_design_lines，或核对图层名。");

                List<MeasuredSection> all = Sections.Read(db, tr, opt);
                List<MeasuredSection> picked = all
                    .Where(s => s.Valid && Sections.Matches(s, filter)).ToList();
                if (picked.Count == 0)
                    throw new InvalidOperationException(
                        "共读到 " + all.Count + " 个断面，但没有标定通过且匹配 line_filter 的；" +
                        "先跑 extract_measured_sections 看标定情况。");

                ObjectId idNote = annotate ? GridEnsureLayer(tr, db, layNote, 3) : ObjectId.Null;
                ObjectId styleId = annotate ? DikeTextStyle(tr, db, textStyle) : ObjectId.Null;
                var usedStrip = new HashSet<string>(StringComparer.Ordinal);
                var usedExc = new HashSet<string>(StringComparer.Ordinal);

                foreach (MeasuredSection s in picked)
                {
                    List<Polyline> mineStrip = Sections.OwnedBy(stripPls, s, ownMargin);
                    List<Polyline> mineExc = Sections.OwnedBy(excPls, s, ownMargin);
                    foreach (Polyline p in mineStrip) usedStrip.Add(p.Handle.ToString());
                    foreach (Polyline p in mineExc) usedExc.Add(p.Handle.ToString());

                    var stripEng = mineStrip.Select(p => Sections.EngPoints(p, s)).ToList();
                    var excEng = mineExc.Select(p => Sections.EngPoints(p, s)).ToList();

                    List<Point2d> surf = Sections.ComposeSurface(s, stripEng);
                    double aStrip = stripEng.Sum(o => Sections.AreaBetween(s.Ground, Sections.Trimmed(o)));
                    double aExc = excEng.Sum(e => Sections.AreaBetween(surf, e));

                    if (mineStrip.Count == 0 && mineExc.Count == 0)
                    {
                        noLine++;
                        warnings.Add(s.Title + "：断面范围内没有设计线，面积按 0 计入");
                    }

                    rows.Add(new DikeQtyRow
                    {
                        Title = s.Title,
                        LineName = string.IsNullOrEmpty(s.LineName) ? "(图名未解析)" : s.LineName,
                        Stake = string.IsNullOrEmpty(s.LineName) ? s.Title : s.Stake,
                        StakeM = s.StakeM,
                        StripArea = aStrip,
                        ExcArea = aExc,
                        StripLines = mineStrip.Count,
                        ExcLines = mineExc.Count,
                        Parsed = !string.IsNullOrEmpty(s.LineName)
                    });

                    if (annotate)
                    {
                        var txt = new DBText();
                        txt.TextString = "清表 " + Sections.F(aStrip, 2) + " m²  底土 " + Sections.F(aExc, 2) + " m²";
                        txt.Position = s.ToPaper3(new Point2d(s.Ground[0].X, s.Ground.Max(p => p.Y) + 1.2));
                        txt.Height = textHeight;
                        txt.LayerId = idNote;
                        if (!styleId.IsNull) txt.TextStyleId = styleId;
                        space.AppendEntity(txt);
                        tr.AddNewlyCreatedDBObject(txt, true);
                        labels++;
                    }
                }

                unassignedStrip = stripPls.Count(p => !usedStrip.Contains(p.Handle.ToString()));
                unassignedExc = excPls.Count(p => !usedExc.Contains(p.Handle.ToString()));
                if (unassignedStrip + unassignedExc > 0)
                    warnings.Add("有 " + unassignedStrip + " 条清表线、" + unassignedExc +
                                 " 条开挖线没归到任何断面（未计入方量）；own_margin 可能太小，或这些线画在断面范围外");

                tr.Commit();
            }

            // ---- 平均断面法：同测线相邻桩号，方量 = 平均面积 × 间距；首断面不成体积 ----
            var perSection = new JsonArray();
            var perLine = new JsonArray();
            var tableRows = new List<object[]>();
            double sumStrip = 0, sumExc = 0;
            bool unparsed = false;

            foreach (var grp in rows.GroupBy(r => r.LineName).OrderBy(g => g.Key))
            {
                List<DikeQtyRow> ord = grp.OrderBy(r => r.StakeM).ToList();
                double lineStrip = 0, lineExc = 0;
                foreach (DikeQtyRow r in ord) if (!r.Parsed) unparsed = true;

                for (int i = 0; i < ord.Count; i++)
                {
                    DikeQtyRow r = ord[i];
                    // 图名没解析出桩号的断面无法定间距，只报面积、不参与方量
                    double d = (i == 0 || !r.Parsed || !ord[i - 1].Parsed) ? 0 : r.StakeM - ord[i - 1].StakeM;
                    double vS = d <= 0 ? 0 : (ord[i - 1].StripArea + r.StripArea) / 2 * d;
                    double vE = d <= 0 ? 0 : (ord[i - 1].ExcArea + r.ExcArea) / 2 * d;
                    lineStrip += vS; lineExc += vE;

                    perSection.Add(new JsonObject
                    {
                        ["title"] = r.Title,
                        ["line_name"] = r.LineName,
                        ["stake"] = r.Stake,
                        ["stake_m"] = Math.Round(r.StakeM, 3),
                        ["strip_area"] = Math.Round(r.StripArea, 3),
                        ["excavation_area"] = Math.Round(r.ExcArea, 3),
                        ["spacing"] = Math.Round(d, 3),
                        ["strip_volume"] = Math.Round(vS, 3),
                        ["excavation_volume"] = Math.Round(vE, 3),
                        ["strip_lines"] = r.StripLines,
                        ["excavation_lines"] = r.ExcLines
                    });
                    tableRows.Add(new object[]
                    {
                        r.LineName, r.Stake,
                        Math.Round(r.StripArea, 2), Math.Round(r.ExcArea, 2),
                        Math.Round(d, 1),
                        Math.Round(vS, 1), Math.Round(vE, 1), Math.Round(vS + vE, 1)
                    });
                }

                sumStrip += lineStrip; sumExc += lineExc;
                perLine.Add(new JsonObject
                {
                    ["line_name"] = grp.Key,
                    ["sections"] = ord.Count,
                    ["stake_from"] = Math.Round(ord[0].StakeM, 3),
                    ["stake_to"] = Math.Round(ord[ord.Count - 1].StakeM, 3),
                    ["strip_volume"] = Math.Round(lineStrip, 3),
                    ["excavation_volume"] = Math.Round(lineExc, 3),
                    ["total_volume"] = Math.Round(lineStrip + lineExc, 3)
                });
            }
            if (unparsed)
                warnings.Add("有断面的图名不匹配 title_regex，桩号未知：只报面积、不计入方量。核对 title_regex 或图名写法");

            var excelFiles = new JsonArray();
            var excelPaths = new List<string>();
            if (exportExcel)
            {
                tableRows.Add(new object[]
                {
                    "总计", "", null, null, null,
                    Math.Round(sumStrip, 1), Math.Round(sumExc, 1), Math.Round(sumStrip + sumExc, 1)
                });
                string[] headers =
                {
                    "测线", "桩号", "清表面积(m²)", "底土面积(m²)",
                    "间距(m)", "清表方量(m³)", "底土方量(m³)", "小计(m³)"
                };
                // outdir 显式读一次：契约申报了就要在节点实现里出现，契约对账才对得上
                string outdirParam = GetString(a, "outdir", null);
                string outdir = string.IsNullOrWhiteSpace(outdirParam)
                    ? ResolveOutDir(a, doc)
                    : Path.GetFullPath(outdirParam);
                string excelOutPath = GetString(a, "excel_out_path", null);
                string baseName = string.IsNullOrWhiteSpace(excelOutPath)
                    ? Sanitize(Path.GetFileNameWithoutExtension(SafeFile(db))) + "_拆堤工程量"
                    : Path.GetFileNameWithoutExtension(excelOutPath);
                string targetDir = string.IsNullOrWhiteSpace(excelOutPath)
                    ? outdir
                    : (Path.GetDirectoryName(excelOutPath) ?? outdir);
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                excelPaths = Excel.Write(targetDir, baseName, headers, tableRows, excelFormat);
                foreach (string p in excelPaths) excelFiles.Add(p);
            }

            return new JsonObject
            {
                ["sections"] = rows.Count,
                ["sections_without_lines"] = noLine,
                ["unassigned_strip_lines"] = unassignedStrip,
                ["unassigned_excavation_lines"] = unassignedExc,
                ["strip_volume"] = Math.Round(sumStrip, 3),
                ["excavation_volume"] = Math.Round(sumExc, 3),
                ["total_volume"] = Math.Round(sumStrip + sumExc, 3),
                ["labels"] = labels,
                ["cleared_old"] = cleared,
                ["per_section"] = perSection,
                ["per_line"] = perLine,
                ["excel_files"] = excelFiles,
                ["warnings"] = warnings
            };
        }

        sealed class DikeQtyRow
        {
            public string Title, LineName, Stake;
            public double StakeM, StripArea, ExcArea;
            public int StripLines, ExcLines;
            public bool Parsed;
        }
    }
}
