using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivSampleLine = Autodesk.Civil.DatabaseServices.SampleLine;
using CivSampleLineGroup = Autodesk.Civil.DatabaseServices.SampleLineGroup;
using CivSectionView = Autodesk.Civil.DatabaseServices.SectionView;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        const string SectionRowLabelLayer = "C3DF-SECTION-ROW-LABEL";
        const string SectionSheetFrameLayer = "C3DF-SECTION-SHEET-FRAME";

        /// <summary>
        /// Single source of truth for section-view layout: move the generated section views onto the empty area right of all alignments.
        ///
        /// Two layouts:
        ///   rows   (default) one alignment per row, frames laid out along +X, alignment name at the row head; easiest for browsing and checking;
        ///   sheets           frames arranged in a grid by sheets_per_row, rows x cols views per page.
        /// Frame size in model space = paper mm x plot scale / 1000 (A3@1:200 = 84 x 59.4 m, which fits only one
        /// +/-30 m wide section; 4 per page needs 1:500 or coarser).
        ///
        /// Positioning is done by writing Graph.Location; section views are not rebuilt, so styles, labels and volume tables are all kept.
        /// A view that does not fit its cell is not silently ignored: it is recorded in oversize and counted in the result.
        /// </summary>
        static JsonNode RunNodeArrangeSectionSheets(JsonObject args, Document doc)
        {
            string layout = GetString(args, "layout", "rows").ToLowerInvariant();
            if (layout != "rows" && layout != "sheets")
                throw new ArgumentException("layout must be rows or sheets.");

            double scale = GetDouble(args, "scale", 200);
            if (scale <= 0) throw new ArgumentException("scale must be positive (pass 200 for 1:200).");
            string paper = GetString(args, "paper", "A3");
            double paperW = GetDouble(args, "paper_w", 0);
            double paperH = GetDouble(args, "paper_h", 0);
            if (paperW <= 0 || paperH <= 0) PaperSizeMm(paper, out paperW, out paperH);   // unknown preset throws; custom sizes go through paper_w/paper_h
            int rows = (int)GetDouble(args, "rows", 1);
            int cols = (int)GetDouble(args, "cols", 1);
            if (rows < 1 || cols < 1) throw new ArgumentException("rows/cols must be >= 1.");
            int sheetsPerRow = (int)GetDouble(args, "sheets_per_row", 10);
            if (sheetsPerRow < 1) sheetsPerRow = 1;
            double margin = GetDouble(args, "margin", 200);
            double sheetGap = GetDouble(args, "sheet_gap", 0);
            double rowGap = GetDouble(args, "row_gap", 0);
            double innerRatio = GetDouble(args, "inner_margin_ratio", 0.05);
            bool perAlignmentNewSheet = GetBool(args, "per_alignment_new_sheet", true);
            var onlyAlignments = args["alignments"] as JsonArray;
            // Section view display width: narrow it here if given (OffsetLeft/Right are writable; no need to regenerate views).
            // Sample width is usually taken from the widest dike, so views overflow the cell at plot time; this is the only place to trim it.
            double offLeft = GetDouble(args, "offset_left", 0);
            double offRight = GetDouble(args, "offset_right", 0);

            double sheetW = paperW * scale / 1000.0;
            double sheetH = paperH * scale / 1000.0;
            double inner = Math.Min(paperW, paperH) * innerRatio * scale / 1000.0;
            double cellW = (sheetW - 2 * inner) / cols;
            double cellH = (sheetH - 2 * inner) / rows;
            // Row/column pitch given directly (model units, = distance between adjacent view centres); 0 = old behaviour, evenly divide the frame.
            // Section views are often shorter than an evenly divided cell, leaving a lot of white; this is the only knob to tighten row spacing.
            double rowPitch = GetDouble(args, "row_pitch", 0);
            double colPitch = GetDouble(args, "col_pitch", 0);
            if (rowPitch < 0 || colPitch < 0) throw new ArgumentException("row_pitch/col_pitch cannot be negative.");
            double effH = rowPitch > 0 ? rowPitch : cellH;
            double effW = colPitch > 0 ? colPitch : cellW;

            // Frame helper rectangles: page edges become visible, and DWGTitleblockPlotter can batch-plot by "closed polylines on a layer"
            bool wantFrames = GetBool(args, "draw_frames", true);
            bool wantLabel = GetBool(args, "row_label", true) && layout == "rows";
            double labelHeight = GetDouble(args, "label_height", sheetH * 0.05);
            double labelGap = GetDouble(args, "label_gap", labelHeight * 3);

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (onlyAlignments != null)
                foreach (JsonNode n in onlyAlignments) wanted.Add(n.GetValue<string>());

            // -- Layout origin: right of the bounding box of all alignments (alignments only, no section views) --
            double x0 = GetDouble(args, "x", double.NaN);
            double y0 = GetDouble(args, "y", double.NaN);
            var alignNames = new List<string>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                double maxX = double.MinValue, maxY = double.MinValue;
                bool any = false;
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    CivAlignment al;
                    try { al = tr.GetObject(id, OpenMode.ForRead) as CivAlignment; }
                    catch { continue; }
                    if (al == null) continue;
                    alignNames.Add(al.Name);
                    try
                    {
                        Extents3d e = al.GeometricExtents;
                        if (e.MaxPoint.X > maxX) maxX = e.MaxPoint.X;
                        if (e.MaxPoint.Y > maxY) maxY = e.MaxPoint.Y;
                        any = true;
                    }
                    catch { }
                }
                if (!any) throw new InvalidOperationException("No alignment in the drawing; cannot locate the layout origin (pass x/y explicitly).");
                if (double.IsNaN(x0)) x0 = maxX + margin;
                if (double.IsNaN(y0)) y0 = maxY;
                tr.Commit();
            }
            alignNames.Sort(CompareDikeName);

            // On rerun, first remove last run's row-head texts and frames so old ones do not overlap the new layout
            int labelsErased = 0, framesErased = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                bool hasLabelLayer = lt.Has(SectionRowLabelLayer);
                bool hasFrameLayer = lt.Has(SectionSheetFrameLayer);
                if (hasLabelLayer || hasFrameLayer)
                {
                    foreach (ObjectId id in ModelSpace(db, tr))
                    {
                        Entity ent;
                        try { ent = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                        catch { continue; }
                        if (ent == null) continue;
                        if (wantLabel && ent is DBText && ent.Layer == SectionRowLabelLayer)
                        {
                            ent.UpgradeOpen(); ent.Erase(); labelsErased++;
                        }
                        else if (wantFrames && ent is Polyline && ent.Layer == SectionSheetFrameLayer)
                        {
                            ent.UpgradeOpen(); ent.Erase(); framesErased++;
                        }
                    }
                }
                tr.Commit();
            }

            var pages = new JsonArray();
            var perAlign = new JsonArray();
            var oversize = new JsonArray();
            int perPage = rows * cols;
            int globalSlot = 0, movedTotal = 0, labelsMade = 0;
            double curRowTop = y0;          // rows layout: top edge of the current row
            int rowIndex = 0;
            double maxRowRight = x0;

            foreach (string alName in alignNames)
            {
                if (wanted.Count > 0 && !wanted.Contains(alName)) continue;

                // Collect all section views of this alignment, sorted by station
                var items = new List<(double station, ObjectId svId)>();
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    CivAlignment al = FindAlignment(tr, civ, alName);
                    if (al == null) { tr.Commit(); continue; }
                    foreach (ObjectId gid in al.GetSampleLineGroupIds())
                    {
                        var g = (CivSampleLineGroup)tr.GetObject(gid, OpenMode.ForRead);
                        foreach (ObjectId slId in g.GetSampleLineIds())
                        {
                            var sl = (CivSampleLine)tr.GetObject(slId, OpenMode.ForRead);
                            double st = 0;
                            try { st = sl.Station; } catch { }
                            foreach (ObjectId svId in sl.GetSectionViewIds())
                                items.Add((st, svId));
                        }
                    }
                    tr.Commit();
                }
                if (items.Count == 0) continue;
                items.Sort((p, q) => p.station.CompareTo(q.station));

                int startPage = 0, moved = 0;
                int sheetsUsed = (items.Count + perPage - 1) / perPage;

                if (layout == "sheets")
                {
                    // When each alignment gets its own booklet, pad to the start of the next page so two dikes never share a frame
                    if (perAlignmentNewSheet && globalSlot % perPage != 0)
                        globalSlot += perPage - (globalSlot % perPage);
                    startPage = globalSlot / perPage;
                }

                for (int i = 0; i < items.Count; i++)
                {
                    double sheetLeft, sheetTop;
                    int slot;
                    if (layout == "rows")
                    {
                        // One alignment per row: frames laid out along +X, frame index i/perPage within the row
                        int sheetIdx = i / perPage;
                        slot = i % perPage;
                        sheetLeft = x0 + sheetIdx * (sheetW + sheetGap);
                        sheetTop = curRowTop;
                    }
                    else
                    {
                        int pageIndex = globalSlot / perPage;
                        slot = globalSlot % perPage;
                        globalSlot++;
                        int pCol = pageIndex % sheetsPerRow;
                        int pRow = pageIndex / sheetsPerRow;
                        sheetLeft = x0 + pCol * (sheetW + sheetGap);
                        sheetTop = y0 - pRow * (sheetH + sheetGap);
                    }

                    int r = slot / cols, c = slot % cols;
                    double cellCx = sheetLeft + inner + c * effW + effW / 2;
                    double cellCy = sheetTop - inner - r * effH - effH / 2;

                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        var sv = (CivSectionView)tr.GetObject(items[i].svId, OpenMode.ForWrite);
                        if (offLeft > 0 || offRight > 0)
                        {
                            // Trim the range before measuring the bounding box, otherwise positioning by the old width drifts
                            try
                            {
                                sv.IsOffsetRangeAutomatic = false;
                                if (offLeft > 0) sv.OffsetLeft = -Math.Abs(offLeft);
                                if (offRight > 0) sv.OffsetRight = Math.Abs(offRight);
                            }
                            catch { }
                        }
                        Point3d loc = sv.Location;
                        Extents3d ext;
                        try { ext = sv.GeometricExtents; }
                        catch { tr.Commit(); continue; }

                        double w = ext.MaxPoint.X - ext.MinPoint.X;
                        double h = ext.MaxPoint.Y - ext.MinPoint.Y;
                        if (w > effW + 1e-6 || h > effH + 1e-6)
                            oversize.Add(new JsonObject
                            {
                                ["alignment"] = alName,
                                ["station"] = Math.Round(items[i].station, 2),
                                ["view_w"] = Math.Round(w, 2),
                                ["view_h"] = Math.Round(h, 2),
                                ["cell_w"] = Math.Round(effW, 2),
                                ["cell_h"] = Math.Round(effH, 2)
                            });

                        // Location is not necessarily the graph's lower-left; translating "current centre -> target centre" is the most robust
                        double curCx = (ext.MinPoint.X + ext.MaxPoint.X) / 2;
                        double curCy = (ext.MinPoint.Y + ext.MaxPoint.Y) / 2;
                        sv.Location = new Point3d(
                            loc.X + (cellCx - curCx),
                            loc.Y + (cellCy - curCy),
                            loc.Z);
                        moved++;
                        tr.Commit();
                    }
                }

                if (layout == "rows")
                {
                    if (wantLabel)
                    {
                        AddRowLabel(db, alName, x0 - labelGap, curRowTop - sheetH / 2, labelHeight);
                        labelsMade++;
                    }
                    double rowRight = x0 + sheetsUsed * (sheetW + sheetGap);
                    if (rowRight > maxRowRight) maxRowRight = rowRight;
                    perAlign.Add(new JsonObject
                    {
                        ["alignment"] = alName,
                        ["section_views"] = items.Count,
                        ["sheets"] = sheetsUsed,
                        ["row"] = rowIndex + 1,
                        ["row_top_y"] = Math.Round(curRowTop, 3)
                    });
                    for (int s = 0; s < sheetsUsed; s++)
                        pages.Add(new JsonObject
                        {
                            ["alignment"] = alName,
                            ["sheet"] = s + 1,
                            ["origin_x"] = Math.Round(x0 + s * (sheetW + sheetGap), 3),
                            ["origin_y"] = Math.Round(curRowTop - sheetH, 3)
                        });
                    curRowTop -= (sheetH + rowGap);
                    rowIndex++;
                }
                else
                {
                    int endPage = (globalSlot - 1) / perPage;
                    perAlign.Add(new JsonObject
                    {
                        ["alignment"] = alName,
                        ["section_views"] = items.Count,
                        ["sheets"] = endPage - startPage + 1,
                        ["first_sheet"] = startPage + 1
                    });
                }
                movedTotal += moved;
            }

            if (layout == "sheets")
            {
                int totalPages = (globalSlot + perPage - 1) / perPage;
                for (int p = 0; p < totalPages; p++)
                {
                    int pCol = p % sheetsPerRow, pRow = p / sheetsPerRow;
                    pages.Add(new JsonObject
                    {
                        ["sheet"] = p + 1,
                        ["origin_x"] = Math.Round(x0 + pCol * (sheetW + sheetGap), 3),
                        ["origin_y"] = Math.Round(y0 - pRow * (sheetH + sheetGap) - sheetH, 3)
                    });
                }
            }

            // Draw frame helper rectangles (closed polyline, one per page)
            int framesDrawn = 0;
            if (wantFrames && pages.Count > 0)
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ObjectId layerId = EnsureFrameLayer(tr, db);
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    foreach (JsonNode pn in pages)
                    {
                        var p = pn as JsonObject;
                        if (p == null) continue;
                        double fx = p["origin_x"].GetValue<double>();
                        double fy = p["origin_y"].GetValue<double>();
                        var pl = new Polyline();
                        pl.SetDatabaseDefaults();
                        pl.AddVertexAt(0, new Point2d(fx, fy), 0, 0, 0);
                        pl.AddVertexAt(1, new Point2d(fx + sheetW, fy), 0, 0, 0);
                        pl.AddVertexAt(2, new Point2d(fx + sheetW, fy + sheetH), 0, 0, 0);
                        pl.AddVertexAt(3, new Point2d(fx, fy + sheetH), 0, 0, 0);
                        pl.Closed = true;
                        ms.AppendEntity(pl);
                        tr.AddNewlyCreatedDBObject(pl, true);
                        pl.LayerId = layerId;    // layer can only be set after appending to the database
                        framesDrawn++;
                    }
                    tr.Commit();
                }
            }

            var res = new JsonObject
            {
                ["layout"] = layout,
                ["frames_drawn"] = framesDrawn,
                ["frames_erased"] = framesErased,
                ["frame_layer"] = SectionSheetFrameLayer,
                ["scale"] = scale,
                ["paper"] = paper,
                ["sheet_size_model"] = Math.Round(sheetW, 2) + " × " + Math.Round(sheetH, 2) + " m",
                ["cell_size_model"] = Math.Round(effW, 2) + " × " + Math.Round(effH, 2) + " m",
                ["row_pitch"] = Math.Round(effH, 3),
                ["col_pitch"] = Math.Round(effW, 3),
                ["grid_per_sheet"] = rows + " rows x " + cols + " cols",
                ["layout_origin"] = Math.Round(x0, 3) + ", " + Math.Round(y0, 3),
                ["alignments_arranged"] = perAlign.Count,
                ["section_views_moved"] = movedTotal,
                ["total_sheets"] = pages.Count,
                ["oversize_count"] = oversize.Count,
                ["oversize"] = oversize,
                ["per_alignment"] = perAlign,
                ["sheets"] = pages
            };
            if (layout == "rows")
            {
                res["rows_used"] = rowIndex;
                res["row_labels"] = labelsMade;
                res["row_labels_erased"] = labelsErased;
                res["row_label_layer"] = SectionRowLabelLayer;
                res["extent"] = "X " + Math.Round(x0, 1) + " ~ " + Math.Round(maxRowRight, 1)
                              + " / Y " + Math.Round(curRowTop, 1) + " ~ " + Math.Round(y0, 1);
            }
            return res;
        }

        static ObjectId EnsureFrameLayer(Transaction tr, Database db)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(SectionSheetFrameLayer)) return lt[SectionSheetFrameLayer];
            lt.UpgradeOpen();
            var ltr = new LayerTableRecord();
            ltr.Name = SectionSheetFrameLayer;
            ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 8);   // grey, does not compete with the section views
            ObjectId id = lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
            return id;
        }

        /// <summary>Row-head alignment name: right-aligned against the first frame, vertically centred on the row.
        /// Same pattern as annotate_grid_elevations: once an alignment mode is set you must give AlignmentPoint and call AdjustAlignment, otherwise it has no effect.</summary>
        static void AddRowLabel(Database db, string text, double x, double y, double height)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                ObjectId layerId;
                if (lt.Has(SectionRowLabelLayer)) layerId = lt[SectionRowLabelLayer];
                else
                {
                    lt.UpgradeOpen();
                    var ltr = new LayerTableRecord();
                    ltr.Name = SectionRowLabelLayer;
                    ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                        Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 2);
                    layerId = lt.Add(ltr);
                    tr.AddNewlyCreatedDBObject(ltr, true);
                }

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var t = new DBText();
                t.SetDatabaseDefaults();
                t.LayerId = layerId;
                t.Height = height;
                t.TextString = text;
                var pt = new Point3d(x, y, 0);
                t.Position = pt;
                t.HorizontalMode = TextHorizontalMode.TextRight;
                t.VerticalMode = TextVerticalMode.TextVerticalMid;
                t.AlignmentPoint = pt;
                ms.AppendEntity(t);
                tr.AddNewlyCreatedDBObject(t, true);
                t.AdjustAlignment(db);
                tr.Commit();
            }
        }

        /// <summary>Dike number ordering: X-2 sorts before X-10 (plain string ordering would reverse them).</summary>
        static int CompareDikeName(string a, string b)
        {
            int na = DikeNumber(a), nb = DikeNumber(b);
            if (na >= 0 && nb >= 0 && na != nb) return na.CompareTo(nb);
            if (na >= 0 && nb < 0) return -1;
            if (na < 0 && nb >= 0) return 1;
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        static int DikeNumber(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            int dash = name.IndexOf('-');
            if (dash < 0 || dash + 1 >= name.Length) return -1;
            int end = dash + 1;
            while (end < name.Length && char.IsDigit(name[end])) end++;
            if (end == dash + 1) return -1;
            int val;
            return int.TryParse(name.Substring(dash + 1, end - dash - 1), out val) ? val : -1;
        }
    }
}
