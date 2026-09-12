using System;
using System.Collections.Generic;
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
        /// Embankment-removal section design lines: stripping line / excavation line / slopes / hatch / leader labels.
        /// Ported line by line from C3DF-GenDesignLine of the embankment-removal plugin V1 (single-section acceptance passed 2026-06),
        /// replacing "window selection + command-line prompts" with "scan model space by layer + parameters from the contract".
        ///
        /// The drawn lines may be fine-tuned by hand -- the downstream compute_embankment_demolition reads the actual lines in the drawing,
        /// not this node's in-memory result. Special sections such as side slopes still need manual boundary edits, as in the old workflow.
        /// </summary>
        static JsonNode RunNodeGenerateDemolitionDesignLines(JsonObject a, Document doc)
            => GenerateDemolitionDesignLines(a, doc);

        public static JsonNode GenerateDemolitionDesignLines(JsonObject a, Document doc)
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

            double thr = GetDouble(a, "strip_threshold_elev", 6.5);
            double tTh = GetDouble(a, "strip_thickness", 0.3);
            double h0Default = GetDouble(a, "bottom_elev", 5.0);
            double slopeM = GetDouble(a, "slope_ratio_m", 3.0);
            double halfW = GetDouble(a, "excavation_half_width", 8.0);
            JsonObject h0By = a["bottom_elev_by_section"] as JsonObject;

            if (tTh < 0) throw new InvalidOperationException("strip_thickness must not be negative.");
            if (slopeM <= 0) throw new InvalidOperationException("slope_ratio_m must be greater than 0 (the m of 1:m).");
            if (halfW <= 0) throw new InvalidOperationException("excavation_half_width must be greater than 0.");

            string layStrip = GetString(a, "strip_line_layer", "C3DF-STRIP-LINE");
            string layExc = GetString(a, "excavation_line_layer", "C3DF-CUT-LINE");
            string layHatch = GetString(a, "hatch_layer", "C3DF-HATCH");
            string layNote = GetString(a, "annotation_layer", "C3DF-LABEL");
            string layCl = GetString(a, "centerline_layer", "C3DF-CL");

            double hatchScale = GetDouble(a, "hatch_scale", 15.0);
            double textHeight = GetDouble(a, "text_height", 2.5);
            string textStyle = GetString(a, "text_style", null);
            bool drawHatch = GetBool(a, "draw_hatch", true);
            bool drawLabels = GetBool(a, "draw_labels", true);
            bool clearExisting = GetBool(a, "clear_existing", true);
            if (hatchScale <= 0) throw new InvalidOperationException("hatch_scale must be greater than 0.");
            if (textHeight <= 0) throw new InvalidOperationException("text_height must be greater than 0.");

            Database db = doc.Database;
            int processed = 0, skipped = 0, zeroCut = 0, stripCount = 0, excCount = 0;
            int hatchCount = 0, labelCount = 0, cleared = 0;
            var warnings = new JsonArray();
            var perSection = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord space = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                if (clearExisting)
                    cleared = DikeEraseOnLayers(tr, space,
                        new[] { layStrip, layExc, layHatch, layNote, layCl });

                ObjectId idStrip = GridEnsureLayer(tr, db, layStrip, 4);    // cyan
                ObjectId idExc = GridEnsureLayer(tr, db, layExc, 2);        // yellow
                ObjectId idHatch = GridEnsureLayer(tr, db, layHatch, 8);    // grey
                ObjectId idNote = GridEnsureLayer(tr, db, layNote, 3);      // green
                ObjectId idCl = GridEnsureLayer(tr, db, layCl, 6);          // magenta
                ObjectId styleId = DikeTextStyle(tr, db, textStyle);

                List<MeasuredSection> all = Sections.Read(db, tr, opt);
                if (all.Count == 0)
                    throw new InvalidOperationException(
                        "No ground-line polyline found in model space on layer '" + opt.GroundLayer + "'; check ground_layer first.");

                List<MeasuredSection> picked = all.Where(s => Sections.Matches(s, filter)).ToList();
                if (picked.Count == 0)
                    throw new InvalidOperationException(
                        "Read " + all.Count + " sections, but none matches line_filter.");

                foreach (MeasuredSection s in picked)
                {
                    if (!s.Valid)
                    {
                        skipped++;
                        warnings.Add(s.Title + ": calibration failed (" + s.Report + "), skipped");
                        perSection.Add(new JsonObject
                        {
                            ["title"] = s.Title,
                            ["line_name"] = s.LineName,
                            ["stake_m"] = Math.Round(s.StakeM, 3),
                            ["skipped"] = true,
                            ["report"] = s.Report
                        });
                        continue;
                    }

                    double h0 = DikeBottomElev(h0By, s, h0Default);
                    string warn = "";

                    // ---- Stripping: segments where the ground is above the threshold are lowered by tTh as a whole ----
                    List<(double a, double b)> strips = Sections.StripIntervals(s, thr);
                    var stripLines = new List<List<Point2d>>();
                    var stripRanges = new JsonArray();
                    foreach (var iv in strips)
                    {
                        List<Point2d> sl = Sections.StripLine(s, iv.a, iv.b, thr, tTh);
                        stripLines.Add(sl);
                        if (Sections.Draw(space, tr, sl, s, idStrip) != ObjectId.Null) stripCount++;
                        if (drawHatch && DikeHatch(space, tr, sl, s.Ground, s, idHatch, "ANSI31", hatchScale))
                            hatchCount++;
                        stripRanges.Add(new JsonArray { Math.Round(iv.a, 2), Math.Round(iv.b, 2) });
                    }

                    List<Point2d> surf = Sections.ComposeSurface(s, stripLines);

                    // ---- Excavation: segments where the stripped surface is above the bottom elevation, limited to +-W, with a slope on the truncated end ----
                    List<(double a, double b)> regions = Sections.RegionsAbove(surf, h0)
                        .Where(r => r.b > -halfW && r.a < halfW && r.b - r.a > 0.05).ToList();

                    var excRanges = new JsonArray();
                    double excL = double.MaxValue, excR = double.MinValue;
                    foreach (var r in regions)
                    {
                        var pts = new List<Point2d>();
                        bool slL = r.a < -halfW - 1e-9, slR = r.b > halfW + 1e-9;
                        if (slL) pts.AddRange(Sections.SlopeOut(surf, -1, h0, halfW, slopeM, ref warn));
                        else pts.Add(new Point2d(r.a, h0));
                        if (slR) pts.AddRange(Sections.SlopeOut(surf, +1, h0, halfW, slopeM, ref warn));
                        else pts.Add(new Point2d(r.b, h0));

                        if (pts[0].X < excL) excL = pts[0].X;
                        if (pts[pts.Count - 1].X > excR) excR = pts[pts.Count - 1].X;
                        if (Sections.Draw(space, tr, pts, s, idExc) != ObjectId.Null) excCount++;
                        if (drawHatch && DikeHatch(space, tr, pts, surf, s, idHatch, "ANSI37", hatchScale))
                            hatchCount++;
                        excRanges.Add(new JsonArray
                        {
                            Math.Round(pts[0].X, 2), Math.Round(pts[pts.Count - 1].X, 2),
                            (slL ? "slope L" : "") + (slR ? "slope R" : "")
                        });
                    }
                    if (regions.Count == 0) zeroCut++;

                    if (drawLabels)
                        labelCount += DikeLabels(space, tr, s, idNote, idCl, thr, tTh, h0,
                                                 strips, excL, excR, textHeight, styleId);

                    processed++;
                    if (warn.Length > 0) warnings.Add(s.Title + ": " + warn);
                    perSection.Add(new JsonObject
                    {
                        ["title"] = s.Title,
                        ["line_name"] = s.LineName,
                        ["stake_m"] = Math.Round(s.StakeM, 3),
                        ["skipped"] = false,
                        ["bottom_elev"] = Math.Round(h0, 3),
                        ["crest_elev"] = Math.Round(s.Ground.Max(p => p.Y), 3),
                        ["strip_intervals"] = stripRanges,
                        ["excavation_intervals"] = excRanges,
                        ["warning"] = warn.Length == 0 ? null : warn
                    });
                }

                tr.Commit();
            }

            return new JsonObject
            {
                ["sections_processed"] = processed,
                ["sections_skipped"] = skipped,
                ["sections_zero_excavation"] = zeroCut,
                ["strip_lines"] = stripCount,
                ["excavation_lines"] = excCount,
                ["hatches"] = hatchCount,
                ["labels"] = labelCount,
                ["cleared_old"] = cleared,
                ["warnings"] = warnings,
                ["per_section"] = perSection
            };
        }

        /// <summary>Bottom elevation per section: look up by full sheet name first, then by survey line name, else use the global value.</summary>
        static double DikeBottomElev(JsonObject map, MeasuredSection s, double dflt)
        {
            if (map == null) return dflt;
            foreach (string key in new[] { s.Title, s.LineName })
            {
                if (string.IsNullOrEmpty(key)) continue;
                JsonNode v = map[key];
                if (v == null) continue;
                try { return v.GetValue<double>(); }
                catch
                {
                    double d;
                    if (double.TryParse(v.ToString(), System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out d)) return d;
                    throw new InvalidOperationException(
                        "bottom_elev_by_section['" + key + "'] is not a number: " + v.ToString());
                }
            }
            return dflt;
        }

        /// <summary>Clean-up before a re-run: erase only the types this node draws (polyline / hatch / text) on the target layers; leave other objects alone.</summary>
        static int DikeEraseOnLayers(Transaction tr, BlockTableRecord space, string[] layers)
        {
            var set = new HashSet<string>(layers.Where(l => !string.IsNullOrWhiteSpace(l)),
                                          StringComparer.OrdinalIgnoreCase);
            if (set.Count == 0) return 0;
            var victims = new List<ObjectId>();
            foreach (ObjectId id in space)
            {
                Entity e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (e == null || !set.Contains(e.Layer)) continue;
                if (e is Polyline || e is Hatch || e is DBText) victims.Add(id);
            }
            foreach (ObjectId id in victims)
                ((Entity)tr.GetObject(id, OpenMode.ForWrite)).Erase();
            return victims.Count;
        }

        /// <summary>Closed hatch between lower boundary and upper boundary; the boundary polyline exists only to create the hatch and is erased afterwards.</summary>
        static bool DikeHatch(BlockTableRecord space, Transaction tr,
                              List<Point2d> lower, List<Point2d> upper,
                              MeasuredSection s, ObjectId layer, string pattern, double scale)
        {
            if (lower == null || lower.Count < 2) return false;
            double a = lower[0].X, b = lower[lower.Count - 1].X;
            var loop = new List<Point2d>(lower);                    // left -> right along the lower boundary
            loop.AddRange(upper.Where(p => p.X > a + 1e-9 && p.X < b - 1e-9).Reverse());
            if (loop.Count < 3) return false;

            var pl = new Polyline();
            pl.Closed = true;
            for (int i = 0; i < loop.Count; i++)
                pl.AddVertexAt(i, s.ToPaper(loop[i]), 0, 0, 0);
            pl.LayerId = layer;
            ObjectId plId = space.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);

            try
            {
                var hat = new Hatch();
                hat.LayerId = layer;
                space.AppendEntity(hat);
                tr.AddNewlyCreatedDBObject(hat, true);
                hat.PatternScale = scale;
                hat.SetHatchPattern(HatchPatternType.PreDefined, pattern);
                hat.Associative = false;
                var ids = new ObjectIdCollection();
                ids.Add(plId);
                hat.AppendLoop(HatchLoopTypes.Default, ids);
                hat.EvaluateHatch(true);
                pl.Erase();
                return true;
            }
            catch
            {
                pl.Erase();   // a failed hatch does not stop the batch; the design lines are already drawn
                return false;
            }
        }

        /// <summary>
        /// Text style: if not given, use the drawing's current style. The default STANDARD is usually paired with txt.shx, where CJK text shows as ????
        /// (one of the leftovers of the embankment-removal plugin V1), so this hook points to an existing CJK-capable style in the drawing,
        /// or run normalize_textstyles first.
        /// </summary>
        static ObjectId DikeTextStyle(Transaction tr, Database db, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return db.Textstyle;
            var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            if (!tst.Has(name))
                throw new InvalidOperationException(
                    "The drawing has no text style '" + name + "'; change text_style or run normalize_textstyles first.");
            return tst[name];
        }

        /// <summary>Design labels: removal centerline, original ground line, topsoil stripping, subsoil excavation, design elevation.</summary>
        static int DikeLabels(BlockTableRecord space, Transaction tr, MeasuredSection s,
                              ObjectId layNote, ObjectId layCl,
                              double thr, double t, double h0,
                              List<(double a, double b)> strips,
                              double excL, double excR, double textHeight, ObjectId styleId)
        {
            int n = 0;
            double crestZ = s.Ground.Max(p => p.Y);
            double crestX = s.Ground.First(p => Math.Abs(p.Y - crestZ) < 1e-9).X;

            var cl = new Polyline();                                  // vertical centerline at offset 0
            cl.AddVertexAt(0, s.ToPaper(new Point2d(0, h0 - 1.0)), 0, 0, 0);
            cl.AddVertexAt(1, s.ToPaper(new Point2d(0, crestZ + 2.0)), 0, 0, 0);
            cl.LayerId = layCl;
            space.AppendEntity(cl);
            tr.AddNewlyCreatedDBObject(cl, true);
            n++;

            n += DikeLeader(space, tr, s, layNote, new Point2d(0, crestZ + 2.0),
                            new Point2d(0.8, crestZ + 3.2), "Removal CL", textHeight, styleId);

            double gx = crestX - 3.0;                                 // original ground line: slope left of the crest
            n += DikeLeader(space, tr, s, layNote, new Point2d(gx, s.ElevAt(gx)),
                            new Point2d(gx - 6.0, crestZ + 1.8), "Original ground", textHeight, styleId);

            if (strips.Count > 0)                                     // topsoil stripping: right slope of the last stripping segment
            {
                double bx = strips[strips.Count - 1].b;
                n += DikeLeader(space, tr, s, layNote, new Point2d(bx, s.ElevAt(bx) - t),
                                new Point2d(bx + 3.5, crestZ + 1.8),
                                "Topsoil stripping (above El. " + Sections.F(thr, 1) + " m)", textHeight, styleId);
            }

            if (excR > excL)
            {
                double ax = excL + (excR - excL) * 0.75;              // subsoil excavation: right-middle of the excavation zone
                n += DikeLeader(space, tr, s, layNote, new Point2d(ax, h0 + 0.8),
                                new Point2d(excR + 2.0, (crestZ + h0) / 2), "Subsoil excavation", textHeight, styleId);

                var mark = new DBText();
                mark.TextString = "▽" + Sections.F(h0, 1) + " (design removal elevation)";
                mark.Position = s.ToPaper3(new Point2d(excR + 0.8, h0 + 0.15));
                mark.Height = textHeight;
                mark.LayerId = layNote;
                if (!styleId.IsNull) mark.TextStyleId = styleId;
                space.AppendEntity(mark);
                tr.AddNewlyCreatedDBObject(mark, true);
                n++;
            }
            return n;
        }

        /// <summary>Leader label: anchor -> slanted line -> underline + text.</summary>
        static int DikeLeader(BlockTableRecord space, Transaction tr, MeasuredSection s,
                              ObjectId layer, Point2d anchor, Point2d txtAt,
                              string text, double textHeight, ObjectId styleId)
        {
            double w = text.Length * textHeight * 1.04 * s.Kx;   // underline length ~ text width (converted to engineering coordinates)
            var pl = new Polyline();
            pl.AddVertexAt(0, s.ToPaper(anchor), 0, 0, 0);
            pl.AddVertexAt(1, s.ToPaper(txtAt), 0, 0, 0);
            pl.AddVertexAt(2, s.ToPaper(new Point2d(txtAt.X + w, txtAt.Y)), 0, 0, 0);
            pl.LayerId = layer;
            space.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);

            var txt = new DBText();
            txt.TextString = text;
            txt.Position = s.ToPaper3(new Point2d(txtAt.X + 0.3, txtAt.Y + 0.15));
            txt.Height = textHeight;
            txt.LayerId = layer;
            if (!styleId.IsNull) txt.TextStyleId = styleId;
            space.AppendEntity(txt);
            tr.AddNewlyCreatedDBObject(txt, true);
            return 2;
        }
    }
}
