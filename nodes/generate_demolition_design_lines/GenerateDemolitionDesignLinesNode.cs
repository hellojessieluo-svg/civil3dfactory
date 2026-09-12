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
        /// 拆堤断面设计线：清表线 / 开挖线 / 放坡 / 填充 / 引出线标注。
        /// 逐行搬自拆堤插件 V1 的 C3DF-GenDesignLine（2026-06 单断面验收通过），
        /// 把「窗选 + 命令行问参数」换成「按图层扫模型空间 + 契约给参数」。
        ///
        /// 画完的线是可以手工微调的——下游 compute_embankment_demolition 读的是图上实际的线，
        /// 不是本节点的内存结果。边坡等特殊断面按老流程仍需人工修边界。
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

            if (tTh < 0) throw new InvalidOperationException("strip_thickness 不能为负。");
            if (slopeM <= 0) throw new InvalidOperationException("slope_ratio_m 必须大于 0（1:m 的 m）。");
            if (halfW <= 0) throw new InvalidOperationException("excavation_half_width 必须大于 0。");

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
            if (hatchScale <= 0) throw new InvalidOperationException("hatch_scale 必须大于 0。");
            if (textHeight <= 0) throw new InvalidOperationException("text_height 必须大于 0。");

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

                ObjectId idStrip = GridEnsureLayer(tr, db, layStrip, 4);    // 青
                ObjectId idExc = GridEnsureLayer(tr, db, layExc, 2);        // 黄
                ObjectId idHatch = GridEnsureLayer(tr, db, layHatch, 8);    // 灰
                ObjectId idNote = GridEnsureLayer(tr, db, layNote, 3);      // 绿
                ObjectId idCl = GridEnsureLayer(tr, db, layCl, 6);          // 品红
                ObjectId styleId = DikeTextStyle(tr, db, textStyle);

                List<MeasuredSection> all = Sections.Read(db, tr, opt);
                if (all.Count == 0)
                    throw new InvalidOperationException(
                        "模型空间在图层 '" + opt.GroundLayer + "' 上找不到地面线多段线；先确认 ground_layer。");

                List<MeasuredSection> picked = all.Where(s => Sections.Matches(s, filter)).ToList();
                if (picked.Count == 0)
                    throw new InvalidOperationException(
                        "共读到 " + all.Count + " 个断面，但没有一个匹配 line_filter。");

                foreach (MeasuredSection s in picked)
                {
                    if (!s.Valid)
                    {
                        skipped++;
                        warnings.Add(s.Title + "：标定未通过（" + s.Report + "），跳过");
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

                    // ---- 清表：地面高于阈值的段整体下移 tTh ----
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

                    // ---- 开挖：清表后表面高于底高程的段，限宽 ±W，被截断的一端放坡 ----
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
                            (slL ? "左坡" : "") + (slR ? "右坡" : "")
                        });
                    }
                    if (regions.Count == 0) zeroCut++;

                    if (drawLabels)
                        labelCount += DikeLabels(space, tr, s, idNote, idCl, thr, tTh, h0,
                                                 strips, excL, excR, textHeight, styleId);

                    processed++;
                    if (warn.Length > 0) warnings.Add(s.Title + "：" + warn);
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

        /// <summary>逐断面底高程：先按图名全名找，再按测线名找，都没有就用全局值。</summary>
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
                        "bottom_elev_by_section['" + key + "'] 不是数字：" + v.ToString());
                }
            }
            return dflt;
        }

        /// <summary>重跑前清场：只删目标图层上本节点会画的类型（多段线/填充/文字），别碰其他对象。</summary>
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

        /// <summary>下边界 lower、上边界 upper 之间闭合打填充；边界线只为生成填充，打完删掉。</summary>
        static bool DikeHatch(BlockTableRecord space, Transaction tr,
                              List<Point2d> lower, List<Point2d> upper,
                              MeasuredSection s, ObjectId layer, string pattern, double scale)
        {
            if (lower == null || lower.Count < 2) return false;
            double a = lower[0].X, b = lower[lower.Count - 1].X;
            var loop = new List<Point2d>(lower);                    // 左→右沿下边界
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
                pl.Erase();   // 填充失败不阻断整批，设计线已经画出去了
                return false;
            }
        }

        /// <summary>
        /// 文字样式：不给就用图纸当前样式。默认 STANDARD 常配 txt.shx，中文会显示成 ????
        /// （拆堤插件 V1 的遗留问题之一），所以留这个口子指向图里已有的中文样式，
        /// 或先跑 normalize_textstyles 归化。
        /// </summary>
        static ObjectId DikeTextStyle(Transaction tr, Database db, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return db.Textstyle;
            var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            if (!tst.Has(name))
                throw new InvalidOperationException(
                    "图纸里没有文字样式 '" + name + "'；改 text_style 或先跑 normalize_textstyles。");
            return tst[name];
        }

        /// <summary>设计标注：拆堤中心线、原地面线、表土清理、底土开挖、设计高程。</summary>
        static int DikeLabels(BlockTableRecord space, Transaction tr, MeasuredSection s,
                              ObjectId layNote, ObjectId layCl,
                              double thr, double t, double h0,
                              List<(double a, double b)> strips,
                              double excL, double excR, double textHeight, ObjectId styleId)
        {
            int n = 0;
            double crestZ = s.Ground.Max(p => p.Y);
            double crestX = s.Ground.First(p => Math.Abs(p.Y - crestZ) < 1e-9).X;

            var cl = new Polyline();                                  // 偏距 0 的竖直中心线
            cl.AddVertexAt(0, s.ToPaper(new Point2d(0, h0 - 1.0)), 0, 0, 0);
            cl.AddVertexAt(1, s.ToPaper(new Point2d(0, crestZ + 2.0)), 0, 0, 0);
            cl.LayerId = layCl;
            space.AppendEntity(cl);
            tr.AddNewlyCreatedDBObject(cl, true);
            n++;

            n += DikeLeader(space, tr, s, layNote, new Point2d(0, crestZ + 2.0),
                            new Point2d(0.8, crestZ + 3.2), "拆堤中心线", textHeight, styleId);

            double gx = crestX - 3.0;                                 // 原地面线：堤顶左侧斜坡
            n += DikeLeader(space, tr, s, layNote, new Point2d(gx, s.ElevAt(gx)),
                            new Point2d(gx - 6.0, crestZ + 1.8), "原地面线", textHeight, styleId);

            if (strips.Count > 0)                                     // 表土清理：最后一个清表段右坡
            {
                double bx = strips[strips.Count - 1].b;
                n += DikeLeader(space, tr, s, layNote, new Point2d(bx, s.ElevAt(bx) - t),
                                new Point2d(bx + 3.5, crestZ + 1.8),
                                "表土清理（高程" + Sections.F(thr, 1) + "m以上）", textHeight, styleId);
            }

            if (excR > excL)
            {
                double ax = excL + (excR - excL) * 0.75;              // 底土开挖：开挖区右侧中部
                n += DikeLeader(space, tr, s, layNote, new Point2d(ax, h0 + 0.8),
                                new Point2d(excR + 2.0, (crestZ + h0) / 2), "底土开挖", textHeight, styleId);

                var mark = new DBText();
                mark.TextString = "▽" + Sections.F(h0, 1) + "（设计拆堤高程）";
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

        /// <summary>引出线标注：锚点 → 斜线 → 文字底横线 + 文字。</summary>
        static int DikeLeader(BlockTableRecord space, Transaction tr, MeasuredSection s,
                              ObjectId layer, Point2d anchor, Point2d txtAt,
                              string text, double textHeight, ObjectId styleId)
        {
            double w = text.Length * textHeight * 1.04 * s.Kx;   // 底横线长 ≈ 文字宽（换算到工程坐标）
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
