using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        /// <summary>
        /// 堤埝批处理：把指定图层上的每条中线转成路线（起点按沿线 K 桩号文字定向），
        /// 按图上已画断面线（与中线相交的断面线图层实体）确定采样线位置和真实端点，
        /// 没有断面线的堤埝按沿线桩号文字兜底。原中线多段线保留不动。
        ///
        /// 本节点只负责“算出每条采样线的端点”，创建采样线组/采样线/设样式/标记采样源
        /// 统一委托给 create_sample_lines（lines 显式端点模式）——那条路径已实测，
        /// 且包含“逐条写模式设样式”这一步：漏掉它断面只会生成空壳
        /// （SectionPoints=0，断面图无地面线，事后无 API 可补算；2026-08-11 踩坑）。
        /// </summary>
        static JsonNode RunNodeCreateDikeSampleLines(JsonObject args, Document doc)
        {
            var layersArr = args["centerline_layers"] as JsonArray;
            if (layersArr == null || layersArr.Count == 0)
                throw new ArgumentException("centerline_layers 必须给至少一个中线图层名。");
            var centerLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonNode n in layersArr) centerLayers.Add(n.GetValue<string>());

            string surfaceName = Need(args, "surface");
            string numberLayer = GetString(args, "number_layer", "6堤埝编号");
            string stationLayer = GetString(args, "station_layer", "0");
            var sectionLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var secArr = args["section_layers"] as JsonArray;
            if (secArr != null)
                foreach (JsonNode n in secArr) sectionLayers.Add(n.GetValue<string>());
            else
                sectionLayers.Add(GetString(args, "section_layer", "2横断面线"));
            double numberMaxDist = GetDouble(args, "number_max_dist", 150);
            double stationMaxDist = GetDouble(args, "station_max_dist", 60);
            double sectionMargin = GetDouble(args, "section_margin", 120);
            double fallbackSwath = GetDouble(args, "fallback_swath", 50);
            // rebuild_only：路线已存在（不重建、不改向），只重建采样线组。
            // 用在走廊建成之后——采样源必须与建组同事务标记，走廊/道路曲面要采样就必须重建组。
            bool rebuildOnly = GetBool(args, "rebuild_only", false);
            string corridorSuffix = GetString(args, "corridor_suffix", "_走廊");
            string roadSurfaceSuffix = GetString(args, "road_surface_suffix", "_道路曲面");

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var stationRegex = new Regex(@"^K(\d+)\+(\d+(?:\.\d+)?)");

            // 第一阶段（只读事务）：收集素材并完成所有基于原图几何的判定。
            var lineIds = new List<ObjectId>();
            var lineInfo = new List<JsonObject>();      // name/layer/length/reversed/station_texts_used
            var lineReversed = new List<bool>();
            var lineLabelStations = new List<List<double>>();   // 兜底：沿线桩号文字给出的断面里程
            var sectionIds = new List<(ObjectId id, Extents3d ext)>();
            var warnings = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (FindSurfaceId(tr, civ, surfaceName).IsNull)
                    throw new InvalidOperationException("图中没有曲面 '" + surfaceName + "'，先跑 import_surface。");

                var centerlines = new List<Polyline>();
                var numberTexts = new List<(Point3d pos, string text)>();
                var stationTexts = new List<(Point3d pos, double station)>();

                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    Entity ent;
                    try { ent = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                    catch { continue; }
                    if (ent == null) continue;
                    string layer = ent.Layer;

                    if (centerLayers.Contains(layer) && ent is Polyline cpl && cpl.Length > 1)
                        centerlines.Add(cpl);
                    else if (string.Equals(layer, numberLayer, StringComparison.OrdinalIgnoreCase))
                    {
                        if (ent is MText mt && !string.IsNullOrWhiteSpace(mt.Text))
                            numberTexts.Add((mt.Location, mt.Text.Trim()));
                        else if (ent is DBText dt && !string.IsNullOrWhiteSpace(dt.TextString))
                            numberTexts.Add((dt.Position, dt.TextString.Trim()));
                    }
                    else if (string.Equals(layer, stationLayer, StringComparison.OrdinalIgnoreCase) && ent is DBText st)
                    {
                        Match m = stationRegex.Match(st.TextString.Trim());
                        if (m.Success)
                        {
                            double station = double.Parse(m.Groups[1].Value) * 1000
                                           + double.Parse(m.Groups[2].Value);
                            stationTexts.Add((st.Position, station));
                        }
                    }
                    else if (sectionLayers.Contains(layer) && (ent is Line || ent is Polyline))
                    {
                        try { sectionIds.Add((id, ent.GeometricExtents)); }
                        catch { }
                    }
                }
                if (centerlines.Count == 0)
                    throw new InvalidOperationException("指定图层上没有找到中线多段线。");

                // 编号 ↔ 中线：按距离全局贪心配对（编号数少于中线数也能稳）
                var pairs = new List<(int li, int ti, double d)>();
                for (int li = 0; li < centerlines.Count; li++)
                    for (int ti = 0; ti < numberTexts.Count; ti++)
                    {
                        double d = centerlines[li]
                            .GetClosestPointTo(numberTexts[ti].pos, false)
                            .DistanceTo(numberTexts[ti].pos);
                        if (d <= numberMaxDist) pairs.Add((li, ti, d));
                    }
                pairs.Sort((a, b) => a.d.CompareTo(b.d));
                var lineName = new string[centerlines.Count];
                var textUsed = new bool[numberTexts.Count];
                foreach (var p in pairs)
                {
                    if (lineName[p.li] != null || textUsed[p.ti]) continue;
                    lineName[p.li] = numberTexts[p.ti].text;
                    textUsed[p.ti] = true;
                }
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int li = 0; li < centerlines.Count; li++)
                {
                    Polyline pl = centerlines[li];

                    string nm = lineName[li];
                    if (nm == null)
                    {
                        nm = "堤埝_" + pl.Handle;
                        warnings.Add("中线 " + pl.Handle + " 附近没有可用编号文字，命名为 " + nm);
                    }
                    string uniq = nm;
                    int k = 2;
                    while (!usedNames.Add(uniq)) uniq = nm + "_" + k++;

                    // 方向：沿线 K 桩号文字的（线上里程, 标注里程）相关性，负相关=要反向
                    var samples = new List<(double geom, double label)>();
                    foreach (var stx in stationTexts)
                    {
                        Point3d cp = pl.GetClosestPointTo(stx.pos, false);
                        if (cp.DistanceTo(stx.pos) > stationMaxDist) continue;
                        if (stx.station > pl.Length + 50) continue;   // 邻线长里程标注串扰
                        samples.Add((pl.GetDistAtPoint(cp), stx.station));
                    }
                    bool reversed = false;
                    if (samples.Count >= 2)
                    {
                        double mg = 0, ml = 0;
                        foreach (var s in samples) { mg += s.geom; ml += s.label; }
                        mg /= samples.Count; ml /= samples.Count;
                        double cov = 0;
                        foreach (var s in samples) cov += (s.geom - mg) * (s.label - ml);
                        reversed = cov < 0;
                    }
                    else
                        warnings.Add(uniq + ": 沿线桩号文字不足(" + samples.Count + ")，按画线方向作为路线方向");

                    var labelStations = new List<double>();
                    foreach (var s in samples)
                    {
                        bool dupSt = false;
                        foreach (double v in labelStations)
                            if (Math.Abs(v - s.label) < 0.05) { dupSt = true; break; }
                        if (!dupSt) labelStations.Add(s.label);
                    }
                    labelStations.Sort();

                    lineIds.Add(pl.ObjectId);
                    lineLabelStations.Add(labelStations);
                    lineReversed.Add(reversed);
                    lineInfo.Add(new JsonObject
                    {
                        ["name"] = uniq,
                        ["source_handle"] = pl.Handle.ToString(),
                        ["layer"] = pl.Layer,
                        ["length"] = Math.Round(pl.Length, 2),
                        ["reversed"] = reversed,
                        ["station_texts_used"] = samples.Count
                    });
                }
                tr.Commit();
            }

            // 第二阶段：逐条堤埝——事务 A 建路线；只读事务算采样线端点；
            // 创建统一委托 create_sample_lines（lines 模式）。
            var dikes = new JsonArray();
            int totalSampleLines = 0;
            for (int li = 0; li < lineIds.Count; li++)
            {
                JsonObject dike = lineInfo[li];
                string name = dike["name"].GetValue<string>();
                bool reversed = lineReversed[li];

                // 事务 A：只建路线并提交（路线和采样线组同事务创建曾致断面空壳，见上方注释）。
                // rebuild_only 时路线必须已存在，直接找。
                ObjectId alignId;
                if (rebuildOnly)
                {
                    using (Transaction tf = db.TransactionManager.StartTransaction())
                    {
                        CivAlignment existing = FindAlignment(tf, civ, name);
                        if (existing == null)
                        {
                            warnings.Add(name + ": rebuild_only 但图中没有该路线，跳过");
                            dike["sample_lines"] = 0;
                            dike["mode"] = "skipped";
                            dikes.Add(dike);
                            tf.Commit();
                            continue;
                        }
                        alignId = existing.ObjectId;
                        tf.Commit();
                    }
                }
                else
                using (Transaction t2 = db.TransactionManager.StartTransaction())
                {
                    var pl = (Polyline)t2.GetObject(lineIds[li], OpenMode.ForRead);

                    // 覆盖重建同名路线
                    EraseAlignments(t2, civ, name);

                    // 始终用中线的临时副本建路线（EraseExistingEntities 吃掉的是副本），原中线保留
                    var bt = (BlockTable)t2.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)t2.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    var tmp = (Polyline)pl.Clone();
                    if (reversed) tmp.ReverseCurve();
                    ms.AppendEntity(tmp);
                    t2.AddNewlyCreatedDBObject(tmp, true);

                    alignId = CreateAlignmentFromEntity(
                        t2, civ, name, ObjectId.Null, tmp.ObjectId, db.Clayer,
                        FindStyleId(t2, civ.Styles.AlignmentStyles, null),
                        FindStyleId(t2, civ.Styles.LabelSetStyles.AlignmentLabelSetStyles, null),
                        eraseSource: true, addCurves: false);
                    t2.Commit();
                }

                // 只读事务：算每条采样线的端点（优先图上断面线，兜底桩号文字）。
                var lineSpecs = new JsonArray();
                string mode = "sections";
                int skippedDup = 0;
                using (Transaction t2 = db.TransactionManager.StartTransaction())
                {
                    var pl = (Polyline)t2.GetObject(lineIds[li], OpenMode.ForRead);
                    var al = (CivAlignment)t2.GetObject(alignId, OpenMode.ForRead);

                    Extents3d ext = pl.GeometricExtents;
                    var min = new Point2d(ext.MinPoint.X - sectionMargin, ext.MinPoint.Y - sectionMargin);
                    var max = new Point2d(ext.MaxPoint.X + sectionMargin, ext.MaxPoint.Y + sectionMargin);

                    // 中线二维分段（忽略 Z——断面线常带地面高程，3D IntersectWith 会全部漏判）
                    var plSegs = new List<(Point2d a, Point2d b)>();
                    for (int vi = 0; vi < pl.NumberOfVertices - 1; vi++)
                        plSegs.Add((pl.GetPoint2dAt(vi), pl.GetPoint2dAt(vi + 1)));

                    var stationsMade = new List<double>();
                    foreach (var se in sectionIds)
                    {
                        if (se.ext.MinPoint.X > max.X || se.ext.MaxPoint.X < min.X ||
                            se.ext.MinPoint.Y > max.Y || se.ext.MaxPoint.Y < min.Y) continue;

                        Entity sent;
                        try { sent = (Entity)t2.GetObject(se.id, OpenMode.ForRead); }
                        catch { continue; }

                        var secSegs = new List<(Point2d a, Point2d b)>();
                        if (sent is Line sln)
                            secSegs.Add((new Point2d(sln.StartPoint.X, sln.StartPoint.Y),
                                         new Point2d(sln.EndPoint.X, sln.EndPoint.Y)));
                        else if (sent is Polyline sp)
                            for (int vi = 0; vi < sp.NumberOfVertices - 1; vi++)
                                secSegs.Add((sp.GetPoint2dAt(vi), sp.GetPoint2dAt(vi + 1)));

                        bool hitFound = false;
                        Point2d hit = Point2d.Origin;
                        foreach (var ps in plSegs)
                        {
                            foreach (var ss in secSegs)
                            {
                                double d1x = ps.b.X - ps.a.X, d1y = ps.b.Y - ps.a.Y;
                                double d2x = ss.b.X - ss.a.X, d2y = ss.b.Y - ss.a.Y;
                                double den = d1x * d2y - d1y * d2x;
                                if (Math.Abs(den) < 1e-12) continue;
                                double t = ((ss.a.X - ps.a.X) * d2y - (ss.a.Y - ps.a.Y) * d2x) / den;
                                double u = ((ss.a.X - ps.a.X) * d1y - (ss.a.Y - ps.a.Y) * d1x) / den;
                                if (t < -0.001 || t > 1.001 || u < -0.001 || u > 1.001) continue;
                                hit = new Point2d(ps.a.X + t * d1x, ps.a.Y + t * d1y);
                                hitFound = true;
                                break;
                            }
                            if (hitFound) break;
                        }
                        if (!hitFound) continue;

                        double station = 0, offset = 0;
                        try { al.StationOffset(hit.X, hit.Y, ref station, ref offset); }
                        catch { continue; }
                        if (station < al.StartingStation - 0.01 || station > al.EndingStation + 0.01) continue;

                        bool dup = false;
                        foreach (double s in stationsMade)
                            if (Math.Abs(s - station) < 0.05) { dup = true; break; }
                        if (dup) { skippedDup++; continue; }

                        var pts = new JsonArray();
                        if (sent is Line ln2)
                        {
                            pts.Add(new JsonArray { ln2.StartPoint.X, ln2.StartPoint.Y });
                            pts.Add(new JsonArray { ln2.EndPoint.X, ln2.EndPoint.Y });
                        }
                        else if (sent is Polyline spl)
                        {
                            for (int vi = 0; vi < spl.NumberOfVertices; vi++)
                            {
                                Point2d v = spl.GetPoint2dAt(vi);
                                pts.Add(new JsonArray { v.X, v.Y });
                            }
                        }
                        if (pts.Count < 2) continue;

                        lineSpecs.Add(new JsonObject
                        {
                            ["name"] = name + "_SL-" + station.ToString("F1"),
                            ["points"] = pts
                        });
                        stationsMade.Add(station);
                    }

                    // 兜底：没有任何断面线与中线相交时，按沿线桩号文字的里程出垂直采样线
                    if (lineSpecs.Count == 0 && lineLabelStations[li].Count > 0)
                    {
                        mode = "label_stations";
                        foreach (double rawSt in lineLabelStations[li])
                        {
                            double st = Math.Min(Math.Max(rawSt, al.StartingStation), al.EndingStation);
                            bool dup = false;
                            foreach (double s in stationsMade)
                                if (Math.Abs(s - st) < 0.05) { dup = true; break; }
                            if (dup) continue;
                            double xL = 0, yL = 0, xR = 0, yR = 0;
                            try
                            {
                                al.PointLocation(st, -fallbackSwath, ref xL, ref yL);
                                al.PointLocation(st, fallbackSwath, ref xR, ref yR);
                                lineSpecs.Add(new JsonObject
                                {
                                    ["name"] = name + "_SL-" + st.ToString("F1"),
                                    ["points"] = new JsonArray
                                    {
                                        new JsonArray { xL, yL },
                                        new JsonArray { xR, yR }
                                    }
                                });
                                stationsMade.Add(st);
                            }
                            catch (System.Exception ex)
                            {
                                warnings.Add(name + " 兜底桩号 " + st.ToString("F1") + " 定位失败: " + ex.Message);
                            }
                        }
                        warnings.Add(name + ": 没有断面线与中线相交，已按 " + lineSpecs.Count
                            + " 个桩号文字兜底出采样线（每侧 " + fallbackSwath + "）");
                    }

                    dike["alignment_start"] = Math.Round(al.StartingStation, 2);
                    dike["alignment_end"] = Math.Round(al.EndingStation, 2);
                    t2.Commit();
                }

                // 创建：统一委托已实测的 create_sample_lines（lines 显式端点模式）
                int made = 0;
                if (lineSpecs.Count > 0)
                {
                    JsonNode slRes = CreateSampleLines(new JsonObject
                    {
                        ["alignment"] = name,
                        ["surface"] = surfaceName,
                        ["corridor"] = name + corridorSuffix,
                        ["road_surface"] = name + roadSurfaceSuffix,
                        ["lines"] = lineSpecs
                    }, doc);
                    made = slRes["sample_lines"].GetValue<int>();
                    dike["sampled_sources"] = slRes["sampled_sources"].DeepClone();
                }
                else
                {
                    dike["sampled_sources"] = new JsonArray();
                    warnings.Add(name + ": 既无相交断面线也无桩号文字，采样线组为空");
                }

                dike["sample_lines"] = made;
                dike["mode"] = mode;
                dike["dup_sections_skipped"] = skippedDup;
                totalSampleLines += made;
                dikes.Add(dike);
            }

            return new JsonObject
            {
                ["surface"] = surfaceName,
                ["dike_count"] = dikes.Count,
                ["total_sample_lines"] = totalSampleLines,
                ["dikes"] = dikes,
                ["warnings"] = warnings
            };
        }
    }
}
