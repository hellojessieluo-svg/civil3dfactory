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
        /// Embankment demolition quantities (average end area method): measure stripping and subsoil excavation area per section, then tabulate volumes between adjacent stations of each survey line.
        /// Ported from C3DF-CalcVolume of the demolition plugin V1.
        ///
        /// Key property (do not change): areas are computed from the **actual** C3DF-STRIP-LINE / C3DF-CUT-LINE in the drawing,
        /// not from the in-memory result of generate_demolition_design_lines; after special sections (slopes etc.) are edited by hand,
        /// rerunning this node uses the edited lines. So this node must run after the design lines are drawn (and edited).
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
            if (ownMargin <= 0) throw new InvalidOperationException("own_margin must be greater than 0 (drawing units).");

            bool annotate = GetBool(a, "annotate_sections", true);
            string layNote = GetString(a, "annotation_layer", "C3DF-QTY-LABEL");
            double textHeight = GetDouble(a, "text_height", 2.5);
            string textStyle = GetString(a, "text_style", null);
            bool clearExisting = GetBool(a, "clear_existing", true);
            if (textHeight <= 0) throw new InvalidOperationException("text_height must be greater than 0.");

            bool exportExcel = GetBool(a, "export_excel", true);
            string excelFormat = GetString(a, "excel_format", "xlsx");   // normalised/validated in Excel.Write

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
                        "No design lines on layer '" + layStrip + "' / '" + layExc + "'; " +
                        "run generate_demolition_design_lines first, or check the layer names.");

                List<MeasuredSection> all = Sections.Read(db, tr, opt);
                List<MeasuredSection> picked = all
                    .Where(s => s.Valid && Sections.Matches(s, filter)).ToList();
                if (picked.Count == 0)
                    throw new InvalidOperationException(
                        "Read " + all.Count + " sections, but none is calibrated and matches line_filter; " +
                        "run extract_measured_sections first to inspect calibration.");

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
                        warnings.Add(s.Title + ": no design line inside the section extent, area counted as 0");
                    }

                    rows.Add(new DikeQtyRow
                    {
                        Title = s.Title,
                        LineName = string.IsNullOrEmpty(s.LineName) ? "(title not parsed)" : s.LineName,
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
                        txt.TextString = "Strip " + Sections.F(aStrip, 2) + " m2  Subsoil " + Sections.F(aExc, 2) + " m2";
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
                    warnings.Add(unassignedStrip + " strip line(s) and " + unassignedExc +
                                 " excavation line(s) were not assigned to any section (excluded from volumes); own_margin may be too small, or the lines lie outside the section extents");

                tr.Commit();
            }

            // ---- Average end area: adjacent stations on the same survey line, volume = mean area x spacing; the first section yields no volume ----
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
                    // Sections whose title yields no station have no spacing: report area only, no volume
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
                warnings.Add("Some section titles do not match title_regex, station unknown: area only, excluded from volumes. Check title_regex or the title format");

            var excelFiles = new JsonArray();
            var excelPaths = new List<string>();
            if (exportExcel)
            {
                tableRows.Add(new object[]
                {
                    "Total", "", null, null, null,
                    Math.Round(sumStrip, 1), Math.Round(sumExc, 1), Math.Round(sumStrip + sumExc, 1)
                });
                string[] headers =
                {
                    "Survey line", "Station", "Strip area (m2)", "Subsoil area (m2)",
                    "Spacing (m)", "Strip volume (m3)", "Subsoil volume (m3)", "Subtotal (m3)"
                };
                // Read outdir explicitly once: what the contract declares must appear in the node implementation for the contract audit to match
                string outdirParam = GetString(a, "outdir", null);
                string outdir = string.IsNullOrWhiteSpace(outdirParam)
                    ? ResolveOutDir(a, doc)
                    : Path.GetFullPath(outdirParam);
                string excelOutPath = GetString(a, "excel_out_path", null);
                string baseName = string.IsNullOrWhiteSpace(excelOutPath)
                    ? Sanitize(Path.GetFileNameWithoutExtension(SafeFile(db))) + "_DemolitionQuantities"
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
