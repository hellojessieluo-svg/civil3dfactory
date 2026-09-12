using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;
using CivTinVolumeSurface = Autodesk.Civil.DatabaseServices.TinVolumeSurface;
using TinVertex = Autodesk.Civil.DatabaseServices.TinSurfaceVertex;
using ExtractType = Autodesk.Civil.SurfaceExtractionSettingsType;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// 工后曲面合成节点（设计∧现状取低）。
    ///
    /// 疏浚只挖不填：设计面低于现状的地方挖到设计面，现状本来就低于设计面的地方保持现状——
    ///     z(P) = min(z设计(P), z现状(P))
    ///
    /// 做法（零差线裁剪法）：
    ///   1. 设计面轮廓 ExtractBorder 当工后面的范围（外边界裁剪，洞按 Hide 处理）；
    ///   2. 建临时高差 TIN（z = 设计 − 现状，顶点取两面顶点并集），在挖侧 ε 处提零差等高线——
    ///      这就是挖/不挖的分界线（折痕），落图 crease_layer 当挖区边界线，同时进工后面当断裂线；
    ///   3. 工后面顶点 = 设计面顶点∧现状 + 范围内现状顶点∧设计 + 零差线点，建 TIN；
    ///   4. verify：现状 vs 工后 GetVolumeProperties——min() 恒不高于现状，填方必须≈0，
    ///      不为零说明合成有问题，安静地成功=没成功。
    /// </summary>
    public static partial class Ops
    {
        const string PostAppName = "C3DF_POSTDREDGE";

        static JsonNode RunNodeMergePostDredgeSurface(JsonObject a, Document doc)
        {
            string designName = Need(a, "design_surface");
            string existName = Need(a, "existing_surface");
            if (string.Equals(designName, existName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("design_surface 与 existing_surface 不能是同一个曲面。");

            string outName = GetString(a, "out_surface", null);
            if (string.IsNullOrEmpty(outName)) outName = "工后-" + designName;
            if (string.Equals(outName, designName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(outName, existName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("out_surface 不能与输入曲面同名（会把输入覆盖掉）。");

            string surfLayer = GetString(a, "surface_layer", "C3DF-POST-SURFACE");
            string creaseLayer = GetString(a, "crease_layer", "C3DF-POST-ZERO-LINE");
            bool drawCrease = GetBool(a, "draw_crease_lines", true);
            double minCrease = GetDouble(a, "min_crease_length", 1.0);
            double eps = Math.Abs(GetDouble(a, "contour_epsilon", 0.001));
            if (eps <= 0) eps = 0.001;
            double borderStep = GetDouble(a, "border_step", 2.0);
            if (borderStep <= 0) throw new InvalidOperationException("border_step 必须大于 0。");
            double gridStep = GetDouble(a, "edge_step", 2.0);
            bool clearExisting = GetBool(a, "clear_existing", true);
            bool verify = GetBool(a, "verify", true);

            string tmpDiffName = "_C3DF_POST_TMPDIFF-" + Sanitize(outName);

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            var warnings = new JsonArray();
            int clearedOld = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId designId = FindSurfaceId(tr, civ, designName);
                if (designId.IsNull) throw new InvalidOperationException("找不到设计曲面 '" + designName + "'。");
                ObjectId existId = FindSurfaceId(tr, civ, existName);
                if (existId.IsNull) throw new InvalidOperationException("找不到现状地形曲面 '" + existName + "'。");

                var designTin = tr.GetObject(designId, OpenMode.ForRead) as CivTinSurface;
                if (designTin == null)
                    throw new InvalidOperationException("设计曲面 '" + designName + "' 不是 TIN 曲面（本节点要读顶点）。");
                var existTin = tr.GetObject(existId, OpenMode.ForRead) as CivTinSurface;
                if (existTin == null)
                    throw new InvalidOperationException("现状曲面 '" + existName + "' 不是 TIN 曲面（本节点要读顶点）。");

                // ---- 幂等清场：同名工后面、本节点画的零差线、上次没收干净的临时面 ----
                if (clearExisting)
                    clearedOld = PostClearOld(tr, db, civ, outName, tmpDiffName);

                EnsureRegApp(tr, db, PostAppName);
                ObjectId lySurf = GridEnsureLayer(tr, db, surfLayer, 4);
                ObjectId lyCrease = GridEnsureLayer(tr, db, creaseLayer, 6);
                var btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                // ---- 1. 设计面轮廓 → 点环（外环 + 洞环） ----
                var rings = new List<List<Point2d>>();
                var borderEnts = new ObjectIdCollection();
                try { borderEnts = designTin.ExtractBorder(ExtractType.Plan); }
                catch (System.Exception ex)
                {
                    warnings.Add((JsonNode)("设计面轮廓提取失败，工后面不做外边界裁剪：" + ex.Message));
                }
                foreach (ObjectId bid in borderEnts)
                {
                    var cv = tr.GetObject(bid, OpenMode.ForRead) as Curve;
                    if (cv != null)
                    {
                        List<Point2d> ring = PostRingFromCurve(cv, borderStep);
                        if (ring.Count >= 3) rings.Add(ring);
                    }
                    var ent = tr.GetObject(bid, OpenMode.ForWrite);
                    ent.Erase();   // 提取物只借形状，不留图上
                }
                int outerIdx = -1;
                double outerArea = 0.0;
                for (int i = 0; i < rings.Count; i++)
                {
                    double s = Math.Abs(DredgeSignedArea(rings[i]));
                    if (s > outerArea) { outerArea = s; outerIdx = i; }
                }
                if (rings.Count > 1)
                    warnings.Add((JsonNode)("设计面轮廓有 " + rings.Count + " 个环，最大环当外边界，其余按 Hide 洞处理。"));

                // ---- 2. 收点：设计顶点∧现状 + 范围内现状顶点∧设计 + 边界环点 ----
                var postPts = new Point3dCollection();
                var diffPts = new Point3dCollection();
                var offExistingPts = new List<Point3d>();
                int designVerts = 0, existVertsUsed = 0, offExisting = 0, offDesign = 0;

                foreach (TinVertex v in designTin.Vertices)
                {
                    Point3d p = v.Location;
                    designVerts++;
                    double zE;
                    if (GridTrySample(existTin, p, out zE))
                    {
                        postPts.Add(new Point3d(p.X, p.Y, Math.Min(p.Z, zE)));
                        diffPts.Add(new Point3d(p.X, p.Y, p.Z - zE));
                    }
                    else
                    {
                        offExisting++;
                        offExistingPts.Add(p);
                        postPts.Add(p);   // 现状采不到：设计面原样进（无从取低）
                    }
                }

                Extents3d dext = designTin.GeometricExtents;
                foreach (TinVertex v in existTin.Vertices)
                {
                    Point3d p = v.Location;
                    if (p.X < dext.MinPoint.X || p.X > dext.MaxPoint.X ||
                        p.Y < dext.MinPoint.Y || p.Y > dext.MaxPoint.Y) continue;
                    if (rings.Count > 0 && !PostInsideRings(new Point2d(p.X, p.Y), rings)) continue;

                    double zD;
                    if (GridTrySample(designTin, p, out zD))
                    {
                        postPts.Add(new Point3d(p.X, p.Y, Math.Min(p.Z, zD)));
                        diffPts.Add(new Point3d(p.X, p.Y, zD - p.Z));
                        existVertsUsed++;
                    }
                    else offDesign++;   // 设计面的洞里：工后面也不该覆盖，不进点
                }

                // 棱上加密：TIN 三角形面内是平面，任何重三角化在面内都精确，误差只出在
                // 工后面三角形跨过原 TIN 棱的地方（弦浮在凸坎肩上）。所以顺着两张 TIN
                // 自己的棱按步长细分采点（z 仍取 min），比盲网格贴合——盲网格斜跨陡坎
                // 反而放大噪声（SY1 实测：无网格 0.56%、5m 盲网格 1.10%、2m 盲网格 0.47%）。
                int edgePts = 0;
                if (gridStep > 0)
                {
                    edgePts += PostSampleTinEdges(designTin, designTin, existTin, gridStep, dext, rings, postPts, diffPts);
                    edgePts += PostSampleTinEdges(existTin, designTin, existTin, gridStep, dext, rings, postPts, diffPts);
                }

                foreach (List<Point2d> ring in rings)
                {
                    for (int i = 0; i < ring.Count; i++)
                    {
                        var p3 = new Point3d(ring[i].X, ring[i].Y, 0);
                        double zD, zE;
                        bool hasD = GridTrySample(designTin, p3, out zD);
                        bool hasE = GridTrySample(existTin, p3, out zE);
                        if (!hasD && !hasE) continue;
                        double z = hasD && hasE ? Math.Min(zD, zE) : (hasD ? zD : zE);
                        postPts.Add(new Point3d(p3.X, p3.Y, z));
                        if (hasD && hasE) diffPts.Add(new Point3d(p3.X, p3.Y, zD - zE));
                    }
                }

                if (postPts.Count < 3)
                    throw new InvalidOperationException("凑不出 3 个工后面顶点——两个曲面在平面上是不是根本不重叠？");
                if (existVertsUsed == 0)
                    warnings.Add((JsonNode)"设计面范围内一个现状顶点都没捞到（现状曲面在这一片没有加密点？），工后面细节全靠设计顶点与零差线。");

                // ---- 3. 临时高差 TIN → 零差等高线（挖侧 ε 处） ----
                var creaseIds = new ObjectIdCollection();
                int creasePts = 0, creaseDropped = 0;
                ObjectId diffId = ObjectId.Null;
                try
                {
                    diffId = CivTinSurface.Create(db, tmpDiffName);
                    var diffTin = (CivTinSurface)tr.GetObject(diffId, OpenMode.ForWrite);
                    diffTin.AddVertices(diffPts);
                    if (outerIdx >= 0)
                    {
                        var bp = new Point3dCollection();
                        foreach (Point2d q in rings[outerIdx]) bp.Add(new Point3d(q.X, q.Y, 0));
                        try { diffTin.BoundariesDefinition.AddBoundaries(bp, 1.0, SurfaceBoundaryType.Outer, false); }
                        catch (System.Exception) { }
                    }

                    ObjectIdCollection contourIds = diffTin.ExtractContoursAt(-eps);
                    foreach (ObjectId cid in contourIds)
                    {
                        var cv = tr.GetObject(cid, OpenMode.ForRead) as Curve;
                        if (cv == null) { tr.GetObject(cid, OpenMode.ForWrite).Erase(); continue; }

                        double len = 0.0;
                        try { len = cv.GetDistanceAtParameter(cv.EndParam); } catch (System.Exception) { }
                        if (len < minCrease)
                        {
                            creaseDropped++;
                            tr.GetObject(cid, OpenMode.ForWrite).Erase();
                            continue;
                        }

                        bool closed = cv.Closed;
                        List<Point2d> cpts = PostCurvePoints(cv, 2.0);
                        var c3 = new Point3dCollection();
                        foreach (Point2d q in cpts)
                        {
                            var p3 = new Point3d(q.X, q.Y, 0);
                            double z;
                            if (GridTrySample(existTin, p3, out z) || GridTrySample(designTin, p3, out z))
                            {
                                c3.Add(new Point3d(q.X, q.Y, z));
                                postPts.Add(new Point3d(q.X, q.Y, z));
                                creasePts++;
                            }
                        }
                        tr.GetObject(cid, OpenMode.ForWrite).Erase();

                        if (drawCrease && c3.Count >= 2)
                        {
                            var pl3 = new Polyline3d(Poly3dType.SimplePoly, c3, closed);
                            pl3.LayerId = lyCrease;
                            btr.AppendEntity(pl3);
                            tr.AddNewlyCreatedDBObject(pl3, true);
                            PostTag(pl3, "CREASE|" + outName);
                            creaseIds.Add(pl3.ObjectId);
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    warnings.Add((JsonNode)("零差线提取失败，工后面退化为纯顶点取低（折痕处无断裂线，体积断言把关）：" + ex.Message));
                }
                finally
                {
                    if (!diffId.IsNull)
                    {
                        try { tr.GetObject(diffId, OpenMode.ForWrite).Erase(); }
                        catch (System.Exception) { warnings.Add((JsonNode)("临时高差曲面 '" + tmpDiffName + "' 没删掉，请手动清理。")); }
                    }
                }
                if (creaseIds.Count == 0 && creasePts == 0)
                    warnings.Add((JsonNode)"一条零差线都没提出来：设计面要么整片都在现状之下（全挖），要么整片之上（全保持现状）——对照 verify 的量判断是哪种。");

                // ---- 4. 建工后 TIN ----
                ObjectId oldOut = FindSurfaceId(tr, civ, outName);
                if (!oldOut.IsNull) tr.GetObject(oldOut, OpenMode.ForWrite).Erase();

                ObjectId postId = CivTinSurface.Create(db, outName);
                var postTin = (CivTinSurface)tr.GetObject(postId, OpenMode.ForWrite);
                postTin.LayerId = lySurf;
                postTin.AddVertices(postPts);

                if (creaseIds.Count > 0)
                {
                    try { postTin.BreaklinesDefinition.AddStandardBreaklines(creaseIds, 1.0, 0.0, 0.0, 0.0); }
                    catch (System.Exception ex)
                    {
                        warnings.Add((JsonNode)("零差线没能当断裂线加入（顶点已含零差线点，折痕精度略降）：" + ex.Message));
                    }
                }

                for (int i = 0; i < rings.Count; i++)
                {
                    var bp = new Point3dCollection();
                    foreach (Point2d q in rings[i]) bp.Add(new Point3d(q.X, q.Y, 0));
                    try
                    {
                        postTin.BoundariesDefinition.AddBoundaries(bp, 1.0,
                            i == outerIdx ? SurfaceBoundaryType.Outer : SurfaceBoundaryType.Hide, false);
                    }
                    catch (System.Exception ex)
                    {
                        warnings.Add((JsonNode)((i == outerIdx ? "外边界" : "洞边界") + "裁剪失败：" + ex.Message));
                    }
                }

                try { postTin.Rebuild(); } catch (System.Exception) { }

                // ---- 5. verify：现状 vs 工后（填方必须≈0），另报 现状 vs 设计 对照 ----
                double postCut = 0, postFill = 0, designCut = 0, designFill = 0;
                double fillRatio = 0;
                bool verified = false;
                var fillHotspots = new JsonArray();
                if (verify)
                {
                    verified = PostTryVolume(tr, civ, "_C3DF_POST_VERIFY-" + Sanitize(outName), existId, postId,
                        out postCut, out postFill, warnings, "现状 vs 工后");
                    PostTryVolume(tr, civ, "_C3DF_POST_COMPARE-" + Sanitize(outName), existId, designId,
                        out designCut, out designFill, warnings, "现状 vs 设计");

                    // 残余填方定位：错开半格采样（格点本身在两面上恒无填方），正差按 50m 格聚合
                    if (verified && postFill > 1.0 && gridStep > 0)
                    {
                        const double cell = 50.0;
                        var cells = new Dictionary<(int, int), double>();
                        var cellMax = new Dictionary<(int, int), double>();
                        var cellDiag = new Dictionary<(int, int), double[]>();
                        double halfShift = gridStep * 0.5;
                        for (double x = dext.MinPoint.X + halfShift; x <= dext.MaxPoint.X; x += gridStep)
                            for (double y = dext.MinPoint.Y + halfShift; y <= dext.MaxPoint.Y; y += gridStep)
                            {
                                var p3 = new Point3d(x, y, 0);
                                double zP, zE2;
                                if (!GridTrySample(postTin, p3, out zP) || !GridTrySample(existTin, p3, out zE2)) continue;
                                double d = zP - zE2;
                                if (d <= 0.001) continue;
                                var key = ((int)Math.Floor(x / cell), (int)Math.Floor(y / cell));
                                double v;
                                cells.TryGetValue(key, out v);
                                cells[key] = v + d * gridStep * gridStep;
                                double mx;
                                cellMax.TryGetValue(key, out mx);
                                if (d > mx)
                                {
                                    cellMax[key] = d;
                                    double zDD;
                                    cellDiag[key] = new double[]
                                    {
                                        x, y, zP, zE2,
                                        GridTrySample(designTin, p3, out zDD) ? zDD : double.NaN
                                    };
                                }
                            }
                        var top = new List<KeyValuePair<(int, int), double>>(cells);
                        top.Sort((u, w) => w.Value.CompareTo(u.Value));
                        for (int i = 0; i < Math.Min(10, top.Count); i++)
                        {
                            var k = top[i].Key;
                            double[] dg = cellDiag[k];
                            double nearestOff = double.NaN;
                            foreach (Point3d op2 in offExistingPts)
                            {
                                double dd = Math.Sqrt((op2.X - dg[0]) * (op2.X - dg[0]) + (op2.Y - dg[1]) * (op2.Y - dg[1]));
                                if (double.IsNaN(nearestOff) || dd < nearestOff) nearestOff = dd;
                            }
                            fillHotspots.Add(new JsonObject
                            {
                                ["x"] = Math.Round((k.Item1 + 0.5) * cell, 0),
                                ["y"] = Math.Round((k.Item2 + 0.5) * cell, 0),
                                ["fill_m3"] = Math.Round(top[i].Value, 1),
                                ["max_dz"] = Math.Round(cellMax[k], 3),
                                ["z_post"] = Math.Round(dg[2], 3),
                                ["z_exist"] = Math.Round(dg[3], 3),
                                ["z_design"] = double.IsNaN(dg[4]) ? null : (JsonNode)Math.Round(dg[4], 3),
                                ["dist_to_offexist_vert"] = double.IsNaN(nearestOff) ? null : (JsonNode)Math.Round(nearestOff, 1)
                            });
                        }
                    }

                    if (verified)
                    {
                        fillRatio = postFill / Math.Max(postCut, 1e-9);
                        if (postCut <= 0)
                            warnings.Add((JsonNode)"⚠ 现状 vs 工后挖方为 0——工后面怕是没合成对（安静地成功=没成功）。");
                        if (fillRatio > 0.005)
                            warnings.Add((JsonNode)("⚠ 工后面高出现状的残余填方 " + postFill.ToString("F1", CultureInfo.InvariantCulture)
                                + " m³（占挖方 " + (fillRatio * 100).ToString("F2", CultureInfo.InvariantCulture)
                                + "%），超过 0.5% 阈值——取低合成有问题，别急着用。"));
                        if (designCut > 1.0)
                        {
                            double cutDev = Math.Abs(postCut - designCut) / designCut;
                            if (cutDev > 0.01)
                                warnings.Add((JsonNode)("⚠ 工后面挖方与真值偏差 " + (cutDev * 100).ToString("F2", CultureInfo.InvariantCulture)
                                    + "%（>1%）。对外工程量一律以 design_cut_volume（现状 vs 设计的挖侧，TIN 叠加精确值）为准，"
                                    + "工后面只当地形用；要收敛可把 edge_step 调小。"));
                        }
                    }
                }

                tr.Commit();

                return new JsonObject
                {
                    ["out_surface"] = outName,
                    ["vertices"] = postPts.Count,
                    ["design_vertices"] = designVerts,
                    ["existing_vertices_used"] = existVertsUsed,
                    ["edge_points"] = edgePts,
                    ["off_existing"] = offExisting,
                    ["off_design"] = offDesign,
                    ["border_rings"] = rings.Count,
                    ["crease_lines"] = creaseIds.Count,
                    ["crease_points"] = creasePts,
                    ["crease_dropped_short"] = creaseDropped,
                    ["cleared_old"] = clearedOld,
                    ["verified"] = verified,
                    ["cut_volume"] = Math.Round(postCut, 3),
                    ["fill_residual"] = Math.Round(postFill, 3),
                    ["fill_ratio"] = Math.Round(fillRatio, 6),
                    ["design_cut_volume"] = Math.Round(designCut, 3),
                    ["design_fake_fill"] = Math.Round(designFill, 3),
                    ["fill_hotspots"] = fillHotspots,
                    ["warnings"] = warnings
                };
            }
        }

        /// <summary>建临时体积曲面读挖填方，读完即删。失败进 warnings 不抛。</summary>
        static bool PostTryVolume(Transaction tr, CivDoc civ, string tmpName,
            ObjectId baseId, ObjectId compId, out double cut, out double fill,
            JsonArray warnings, string what)
        {
            cut = 0; fill = 0;
            ObjectId volId = ObjectId.Null;
            try
            {
                ObjectId old = FindSurfaceId(tr, civ, tmpName);
                if (!old.IsNull) tr.GetObject(old, OpenMode.ForWrite).Erase();
                volId = CivTinVolumeSurface.Create(tmpName, baseId, compId);
                var vs = (CivTinVolumeSurface)tr.GetObject(volId, OpenMode.ForRead);
                var props = vs.GetVolumeProperties();
                cut = props.UnadjustedCutVolume;
                fill = props.UnadjustedFillVolume;
                return true;
            }
            catch (System.Exception ex)
            {
                warnings.Add((JsonNode)("verify（" + what + "）体积计算失败：" + ex.Message));
                return false;
            }
            finally
            {
                if (!volId.IsNull)
                {
                    try { tr.GetObject(volId, OpenMode.ForWrite).Erase(); }
                    catch (System.Exception) { }
                }
            }
        }

        /// <summary>
        /// 沿一张 TIN 的全部棱按步长细分采样（去重后每条棱只走一遍），
        /// 每个细分点 z = min(设计, 现状)，同时给高差 TIN 喂 zD−zE。
        /// 只采范围盒相交且（有环时）至少一端在环内的棱；两面有一面采不到的点跳过。
        /// </summary>
        static int PostSampleTinEdges(CivTinSurface src, CivTinSurface designTin, CivTinSurface existTin,
            double step, Extents3d dext, List<List<Point2d>> rings,
            Point3dCollection postPts, Point3dCollection diffPts)
        {
            int added = 0;
            var seen = new HashSet<(long, long, long, long)>();
            foreach (Autodesk.Civil.DatabaseServices.TinSurfaceTriangle t in src.Triangles)
            {
                Point3d a = t.Vertex1.Location, b = t.Vertex2.Location, c = t.Vertex3.Location;
                added += PostSampleEdge(a, b, designTin, existTin, step, dext, rings, seen, postPts, diffPts);
                added += PostSampleEdge(b, c, designTin, existTin, step, dext, rings, seen, postPts, diffPts);
                added += PostSampleEdge(c, a, designTin, existTin, step, dext, rings, seen, postPts, diffPts);
            }
            return added;
        }

        static int PostSampleEdge(Point3d a, Point3d b, CivTinSurface designTin, CivTinSurface existTin,
            double step, Extents3d dext, List<List<Point2d>> rings,
            HashSet<(long, long, long, long)> seen,
            Point3dCollection postPts, Point3dCollection diffPts)
        {
            long ax = (long)Math.Round(a.X * 1000), ay = (long)Math.Round(a.Y * 1000);
            long bx = (long)Math.Round(b.X * 1000), by = (long)Math.Round(b.Y * 1000);
            var key = ax < bx || (ax == bx && ay <= by) ? (ax, ay, bx, by) : (bx, by, ax, ay);
            if (!seen.Add(key)) return 0;

            if (Math.Max(a.X, b.X) < dext.MinPoint.X || Math.Min(a.X, b.X) > dext.MaxPoint.X ||
                Math.Max(a.Y, b.Y) < dext.MinPoint.Y || Math.Min(a.Y, b.Y) > dext.MaxPoint.Y) return 0;

            double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            int n = (int)Math.Floor(len / Math.Max(step, 0.1));
            if (n < 1) return 0;   // 棱比步长短：两端本来就是顶点，不用加

            if (rings.Count > 0 &&
                !PostInsideRings(new Point2d(a.X, a.Y), rings) &&
                !PostInsideRings(new Point2d(b.X, b.Y), rings)) return 0;

            int added = 0;
            for (int i = 1; i <= n; i++)
            {
                double tpar = (double)i / (n + 1);
                var p3 = new Point3d(a.X + (b.X - a.X) * tpar, a.Y + (b.Y - a.Y) * tpar, 0);
                double zD, zE;
                if (!GridTrySample(designTin, p3, out zD) || !GridTrySample(existTin, p3, out zE)) continue;
                postPts.Add(new Point3d(p3.X, p3.Y, Math.Min(zD, zE)));
                diffPts.Add(new Point3d(p3.X, p3.Y, zD - zE));
                added++;
            }
            return added;
        }

        /// <summary>任意曲线按步长展成 2D 点环（多段线直接取顶点，其余等距采样）。</summary>
        static List<Point2d> PostRingFromCurve(Curve cv, double step)
        {
            var pts = new List<Point2d>();
            double L;
            try { L = cv.GetDistanceAtParameter(cv.EndParam); }
            catch (System.Exception) { return pts; }
            if (L <= 1e-9) return pts;
            int n = Math.Max(8, (int)Math.Ceiling(L / Math.Max(step, 1e-6)));
            for (int i = 0; i < n; i++)
            {
                try
                {
                    Point3d p = cv.GetPointAtDist(L * i / n);
                    pts.Add(new Point2d(p.X, p.Y));
                }
                catch (System.Exception) { }
            }
            return pts;
        }

        /// <summary>取曲线的折点序列：多段线用真顶点（零差线本来就是折线），其余按 maxSeg 采样。</summary>
        static List<Point2d> PostCurvePoints(Curve cv, double maxSeg)
        {
            var pts = new List<Point2d>();
            var pl = cv as Polyline;
            if (pl != null)
            {
                for (int i = 0; i < pl.NumberOfVertices; i++)
                    pts.Add(pl.GetPoint2dAt(i));
                return pts;
            }
            var p2 = cv as Polyline2d;
            var p3 = cv as Polyline3d;
            if (p2 != null || p3 != null)
            {
                double L;
                try { L = cv.GetDistanceAtParameter(cv.EndParam); }
                catch (System.Exception) { return pts; }
                int n = Math.Max(2, (int)Math.Ceiling(L / Math.Max(maxSeg, 0.1)));
                for (int i = 0; i <= n; i++)
                {
                    try
                    {
                        Point3d p = cv.GetPointAtDist(Math.Min(L, L * i / n));
                        pts.Add(new Point2d(p.X, p.Y));
                    }
                    catch (System.Exception) { }
                }
            }
            return pts;
        }

        /// <summary>多环奇偶判内外（外环+洞环一把算：命中奇数个环=在面内）。</summary>
        static bool PostInsideRings(Point2d p, List<List<Point2d>> rings)
        {
            int hits = 0;
            for (int i = 0; i < rings.Count; i++)
                if (GridPointInPolygon(p, rings[i])) hits++;
            return (hits & 1) == 1;
        }

        static void PostTag(DBObject obj, string kind)
        {
            obj.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, PostAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, kind));
        }

        /// <summary>清上次产物：同名工后面、挂本节点 XData 且属于该工后面的零差线、残留临时面。</summary>
        static int PostClearOld(Transaction tr, Database db, CivDoc civ, string outName, string tmpDiffName)
        {
            int n = 0;
            string sane = Sanitize(outName);
            var doomedSurf = new List<string> { outName, tmpDiffName, "_C3DF_POST_VERIFY-" + sane, "_C3DF_POST_COMPARE-" + sane };
            foreach (ObjectId sid in civ.GetSurfaceIds())
            {
                var s = tr.GetObject(sid, OpenMode.ForRead) as CivSurface;
                if (s == null) continue;
                bool hit = false;
                foreach (string dn in doomedSurf)
                    if (string.Equals(s.Name, dn, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
                if (!hit) continue;
                tr.GetObject(sid, OpenMode.ForWrite).Erase();
                n++;
            }

            string want = "CREASE|" + outName;
            foreach (ObjectId id in ModelSpace(db, tr))
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || ent.IsErased) continue;
                if (!(ent is Polyline3d)) continue;
                ResultBuffer rb = ent.GetXDataForApplication(PostAppName);
                if (rb == null) continue;
                bool mine = false;
                foreach (TypedValue tv in rb)
                    if (tv.TypeCode == (int)DxfCode.ExtendedDataAsciiString &&
                        string.Equals((string)tv.Value, want, StringComparison.OrdinalIgnoreCase)) { mine = true; break; }
                rb.Dispose();
                if (!mine) continue;
                ent.UpgradeOpen();
                ent.Erase();
                n++;
            }
            return n;
        }
    }
}
