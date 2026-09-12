using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeParcelGradingSlope(JsonObject a, Document doc)
            => ParcelGradingSlope(a, doc);

        public static JsonNode ParcelGradingSlope(JsonObject a, Document doc)
        {
            string bndStr = Need(a, "parcel_boundary");
            string targetSurfName = GetString(a, "target_surface", null);

            string direction = GetString(a, "direction", "outward"); // inward | outward
            string targetType = GetString(a, "target_type", "surface"); // surface | elevation | relative_height
            double targetValue = GetDouble(a, "target_value", 0.0);

            double cutSlope = GetDouble(a, "cut_slope", 1.5);
            double fillSlope = GetDouble(a, "fill_slope", 1.5);
            double benchHeight = GetDouble(a, "bench_height", 0.0);
            double benchWidth = GetDouble(a, "bench_width", 0.0);

            bool drawCombLines = GetBool(a, "draw_comb_lines", true);
            double combSpacing = GetDouble(a, "comb_spacing", 2.0);

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // 找边界多段线
                Polyline poly = null;
                ObjectId polyId = ResolveHandle(db, bndStr);
                if (!polyId.IsNull)
                {
                    poly = tr.GetObject(polyId, OpenMode.ForRead) as Polyline;
                }
                if (poly == null)
                {
                    // 按图层查找第一个匹配的多段线
                    foreach (ObjectId id in ModelSpace(db, tr))
                    {
                        var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                        if (pl != null && string.Equals(pl.Layer, bndStr, StringComparison.OrdinalIgnoreCase))
                        {
                            poly = pl;
                            break;
                        }
                    }
                }
                if (poly == null)
                    throw new InvalidOperationException("找不到地块边界多段线 '" + bndStr + "'。");

                // 找目标曲面（若 target_type == "surface"）
                CivSurface targetSurf = null;
                if (targetType.Equals("surface", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(targetSurfName))
                {
                    ObjectId targetSurfId = FindSurfaceId(tr, civ, targetSurfName);
                    if (!targetSurfId.IsNull)
                        targetSurf = tr.GetObject(targetSurfId, OpenMode.ForRead) as CivSurface;
                }

                // 准备模型空间容器与图层
                var btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                ObjectId lyComb = GridEnsureLayer(tr, db, "C3DF-SLOPE-MARK", 2);
                ObjectId lyDaylight = GridEnsureLayer(tr, db, "C3DF-TOP-TOE", 1);

                // 计算多段线面积与顺逆时针（用于导向法线）
                double area = poly.Area;
                bool isCcw = area >= 0; // CAD 默认 CW/CCW

                // 沿边界线采样顶点并计算坡顶/坡脚 daylight 点
                List<Point3d> bndPts = new List<Point3d>();
                List<Point3d> daylightPts = new List<Point3d>();
                List<Line> combLinesList = new List<Line>();

                double length = poly.Length;
                int samples = Math.Max(10, (int)Math.Ceiling(length / Math.Max(0.5, combSpacing)));

                Point3dCollection surfPts = new Point3dCollection();

                for (int i = 0; i <= samples; i++)
                {
                    double dist = Math.Min(length, i * length / samples);
                    Point3d ptOnPoly = poly.GetPointAtDist(dist);
                    Vector3d deriv = poly.GetFirstDerivative(poly.GetParameterAtDistance(dist));
                    Vector2d dir2d = new Vector2d(deriv.X, deriv.Y).GetNormal();

                    // 法向量: 左法线 (-y, x), 右法线 (y, -x)
                    Vector2d normal2d = isCcw ? new Vector2d(-dir2d.Y, dir2d.X) : new Vector2d(dir2d.Y, -dir2d.X);
                    if (direction.Equals("inward", StringComparison.OrdinalIgnoreCase))
                        normal2d = normal2d.Negate();

                    double zBase = ptOnPoly.Z;
                    if (zBase == 0 && targetSurf != null)
                    {
                        GridTrySample(targetSurf, ptOnPoly, out zBase);
                    }

                    double zTarget = zBase;
                    if (targetType.Equals("elevation", StringComparison.OrdinalIgnoreCase))
                        zTarget = targetValue;
                    else if (targetType.Equals("relative_height", StringComparison.OrdinalIgnoreCase))
                        zTarget = zBase + targetValue;
                    else if (targetSurf != null)
                    {
                        // 预估延伸点并采样曲面高程
                        Point3d estPt = ptOnPoly + new Vector3d(normal2d.X * 5.0, normal2d.Y * 5.0, 0);
                        double zSample;
                        if (GridTrySample(targetSurf, estPt, out zSample))
                            zTarget = zSample;
                        else
                            zTarget = zBase + 2.0;
                    }
                    else
                    {
                        zTarget = zBase + targetValue;
                    }

                    double hDiff = zTarget - zBase;
                    double slope = hDiff >= 0 ? fillSlope : cutSlope;
                    double horizontalDist = Math.Abs(hDiff) * slope;

                    if (benchHeight > 0 && Math.Abs(hDiff) > benchHeight)
                    {
                        double steps = Math.Floor(Math.Abs(hDiff) / benchHeight);
                        horizontalDist += steps * benchWidth;
                    }

                    Point3d ptDaylight = new Point3d(
                        ptOnPoly.X + normal2d.X * horizontalDist,
                        ptOnPoly.Y + normal2d.Y * horizontalDist,
                        zTarget);

                    bndPts.Add(ptOnPoly);
                    daylightPts.Add(ptDaylight);

                    surfPts.Add(ptOnPoly);
                    surfPts.Add(ptDaylight);

                    // 绘制示坡线（梳齿线：长短线交替）
                    if (drawCombLines && i % 1 == 0 && i < samples)
                    {
                        bool isLong = (i % 2 == 0);
                        double factor = isLong ? 1.0 : 0.4;
                        Point3d combEnd = new Point3d(
                            ptOnPoly.X + normal2d.X * horizontalDist * factor,
                            ptOnPoly.Y + normal2d.Y * horizontalDist * factor,
                            ptOnPoly.Z + hDiff * factor);

                        var combLn = new Line(ptOnPoly, combEnd);
                        combLn.LayerId = lyComb;
                        btr.AppendEntity(combLn);
                        tr.AddNewlyCreatedDBObject(combLn, true);
                        combLinesList.Add(combLn);
                    }
                }

                // 生成 Daylight Polyline 实体
                Polyline daylightPoly = new Polyline();
                daylightPoly.LayerId = lyDaylight;
                for (int k = 0; k < daylightPts.Count; k++)
                {
                    daylightPoly.AddVertexAt(k, new Point2d(daylightPts[k].X, daylightPts[k].Y), 0, 0, 0);
                }
                btr.AppendEntity(daylightPoly);
                tr.AddNewlyCreatedDBObject(daylightPoly, true);

                // 创建放坡 TIN 曲面
                string gradingSurfName = "GradingSurface_" + poly.Handle.ToString();
                ObjectId oldSurf = FindSurfaceId(tr, civ, gradingSurfName);
                if (!oldSurf.IsNull)
                {
                    var oldObj = tr.GetObject(oldSurf, OpenMode.ForWrite) as DBObject;
                    if (oldObj != null) oldObj.Erase();
                }

                ObjectId surfId = CivTinSurface.Create(db, gradingSurfName);
                var gradingSurf = (CivTinSurface)tr.GetObject(surfId, OpenMode.ForWrite);
                gradingSurf.AddVertices(surfPts);

                var res = new JsonObject
                {
                    ["grading_surface"] = gradingSurfName,
                    ["daylight_line"] = daylightPoly.Handle.ToString(),
                    ["comb_lines_count"] = combLinesList.Count,
                    ["points_count"] = surfPts.Count
                };

                tr.Commit();
                return res;
            }
        }
    }
}
