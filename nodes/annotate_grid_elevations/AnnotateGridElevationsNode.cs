using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DFactory
{
    /// <summary>
    /// 网格角点高程标注节点。
    ///
    /// 参数化输入边界图层，图层上**全部闭合多段线**逐个执行：在边界内打方格网(Line)，
    /// 每个落在边界内的交叉点周围放 3 个已填好值的单行文字：
    ///     左上 = 差值(设计−现有)   右上 = 设计高程   右下 = 现有高程   左下 = 空
    ///
    /// 与既有交互插件 GridElev(C3DF-GridGen) 同一算法、同一 XData 约定（AppName C3DF_GRIDELEV），
    /// 因此本节点的产物仍可用 C3DF-ElevFromSurface / C3DF-CalcDiff 手工维护。
    /// 区别：交互版一次选一条边界，本节点按图层批量；曲面按名字取，全程不碰
    /// CivilApplication.ActiveDocument（accoreconsole 里不可用）。
    /// </summary>
    public static partial class Ops
    {
        const string GridAppName = "C3DF_GRIDELEV";
        const string GridTypeDesign = "DESIGN";   // 设计高程(右上)
        const string GridTypeExist = "EXIST";     // 现有高程(右下)
        const string GridTypeDiff = "DIFF";       // 差值   (左上)
        const string GridTypeLine = "GRID";       // 网格线
        const string GridNaText = "—";            // 点在曲面外/无值
        const double GridNodeTol = 1e-3;          // 节点去重容差(米)

        static JsonNode RunNodeAnnotateGridElevations(JsonObject a, Document doc)
        {
            string bndLayer = Need(a, "boundary_layer");
            string designName = Need(a, "design_surface");
            string existName = Need(a, "existing_surface");

            double spacing = GetDouble(a, "spacing", 50.0);
            if (spacing <= 1e-6) throw new InvalidOperationException("spacing 必须大于 0。");
            double h = GetDouble(a, "text_height", 2.5);
            if (h <= 1e-6) throw new InvalidOperationException("text_height 必须大于 0。");
            int decimals = (int)GetDouble(a, "decimals", 2);
            if (decimals < 0 || decimals > 6) decimals = 2;
            double offsetFactor = GetDouble(a, "offset_factor", 0.4);
            bool closedOnly = GetBool(a, "closed_only", true);
            bool drawGrid = GetBool(a, "draw_grid", true);
            bool clearExisting = GetBool(a, "clear_existing", true);

            string lyGridName = GetString(a, "grid_layer", "C3DF-网格");
            string lyDesignName = GetString(a, "design_layer", "C3DF-DesignElevation");
            string lyExistName = GetString(a, "exist_layer", "C3DF-现有高程");
            string lyDiffName = GetString(a, "diff_layer", "C3DF-差值");

            string fmt = "F" + decimals.ToString(CultureInfo.InvariantCulture);
            double m = offsetFactor * h;

            Database db = doc.Database;
            var civ = Civ(db);

            var perBoundary = new JsonArray();
            int cleared = 0, boundaries = 0, skippedOpen = 0;
            int nodesTotal = 0, segsTotal = 0, dOutTotal = 0, eOutTotal = 0, dupNodes = 0;
            JsonObject sample = null;   // 首个节点实际写出的三个值，供验收核对

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId dId = FindSurfaceId(tr, civ, designName);
                if (dId.IsNull) throw new InvalidOperationException("找不到设计曲面 '" + designName + "'。");
                ObjectId eId = FindSurfaceId(tr, civ, existName);
                if (eId.IsNull) throw new InvalidOperationException("找不到原地形曲面 '" + existName + "'。");
                var dSurf = (CivSurface)tr.GetObject(dId, OpenMode.ForRead);
                var eSurf = (CivSurface)tr.GetObject(eId, OpenMode.ForRead);

                // 上次产物清理：只动带本节点 XData 的对象，用户自画的一律不碰
                if (clearExisting) cleared = EraseTaggedEntities(tr, db, GridAppName);

                // 收集边界图层上的多段线
                var bndIds = new List<ObjectId>();
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (pl == null) continue;
                    if (!string.Equals(pl.Layer, bndLayer, StringComparison.OrdinalIgnoreCase)) continue;
                    if (closedOnly && !pl.Closed) { skippedOpen++; continue; }
                    bndIds.Add(id);
                }
                if (bndIds.Count == 0)
                    throw new InvalidOperationException(
                        "图层 '" + bndLayer + "' 上没有可用的" + (closedOnly ? "闭合" : "") + "多段线（LWPOLYLINE）。");

                EnsureRegApp(tr, db, GridAppName);
                ObjectId lyGrid = GridEnsureLayer(tr, db, lyGridName, 4);
                ObjectId lyD = GridEnsureLayer(tr, db, lyDesignName, 2);
                ObjectId lyE = GridEnsureLayer(tr, db, lyExistName, 3);
                ObjectId lyF = GridEnsureLayer(tr, db, lyDiffName, 1);
                var btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                // 多个地块共用边界时，同一节点只标一次（全局按坐标去重）
                var seen = new HashSet<string>();

                foreach (ObjectId bid in bndIds)
                {
                    var bnd = (Polyline)tr.GetObject(bid, OpenMode.ForRead);
                    Extents3d ext;
                    try { ext = bnd.GeometricExtents; }
                    catch (System.Exception) { continue; }

                    List<double> xs = GridCoords(ext.MinPoint.X, ext.MaxPoint.X, spacing);
                    List<double> ys = GridCoords(ext.MinPoint.Y, ext.MaxPoint.Y, spacing);
                    List<Point2d> poly = GridSamplePolygon(bnd, spacing / 10.0);
                    if (poly.Count < 3) continue;

                    int segs = 0;
                    if (drawGrid)
                    {
                        double yLo = ext.MinPoint.Y - spacing, yHi = ext.MaxPoint.Y + spacing;
                        double xLo = ext.MinPoint.X - spacing, xHi = ext.MaxPoint.X + spacing;
                        for (int i = 0; i < xs.Count; i++)
                            segs += GridDrawClipped(tr, btr, lyGrid, bnd, poly,
                                new Point3d(xs[i], yLo, 0), new Point3d(xs[i], yHi, 0));
                        for (int i = 0; i < ys.Count; i++)
                            segs += GridDrawClipped(tr, btr, lyGrid, bnd, poly,
                                new Point3d(xLo, ys[i], 0), new Point3d(xHi, ys[i], 0));
                    }

                    int nodes = 0, dOut = 0, eOut = 0, dup = 0;
                    for (int iy = 0; iy < ys.Count; iy++)
                        for (int ix = 0; ix < xs.Count; ix++)
                        {
                            double x = xs[ix], y = ys[iy];
                            if (!GridPointInPolygon(new Point2d(x, y), poly)) continue;
                            var node = new Point3d(x, y, 0);
                            if (!seen.Add(GridNodeKey(node))) { dup++; continue; }

                            double dz, ez;
                            bool okD = GridTrySample(dSurf, node, out dz);
                            bool okE = GridTrySample(eSurf, node, out ez);
                            if (!okD) dOut++;
                            if (!okE) eOut++;
                            string sD = okD ? dz.ToString(fmt, CultureInfo.InvariantCulture) : GridNaText;
                            string sE = okE ? ez.ToString(fmt, CultureInfo.InvariantCulture) : GridNaText;
                            string sF = (okD && okE)
                                ? (dz - ez).ToString(fmt, CultureInfo.InvariantCulture) : GridNaText;

                            GridAddText(tr, db, btr, lyD, sD, h, new Point3d(x + m, y + m, 0),
                                TextHorizontalMode.TextLeft, TextVerticalMode.TextBottom, GridTypeDesign, node);
                            GridAddText(tr, db, btr, lyE, sE, h, new Point3d(x + m, y - m, 0),
                                TextHorizontalMode.TextLeft, TextVerticalMode.TextTop, GridTypeExist, node);
                            GridAddText(tr, db, btr, lyF, sF, h, new Point3d(x - m, y + m, 0),
                                TextHorizontalMode.TextRight, TextVerticalMode.TextBottom, GridTypeDiff, node);
                            nodes++;

                            if (sample == null)
                                sample = new JsonObject
                                {
                                    ["node"] = new JsonArray { Round(x, 3), Round(y, 3) },
                                    ["design"] = sD,
                                    ["exist"] = sE,
                                    ["diff"] = sF
                                };
                        }

                    boundaries++;
                    nodesTotal += nodes; segsTotal += segs;
                    dOutTotal += dOut; eOutTotal += eOut; dupNodes += dup;

                    perBoundary.Add(new JsonObject
                    {
                        ["handle"] = bnd.Handle.ToString(),
                        ["closed"] = bnd.Closed,
                        ["nodes"] = nodes,
                        ["grid_segments"] = segs,
                        ["design_outside"] = dOut,
                        ["exist_outside"] = eOut,
                        ["duplicate_nodes_skipped"] = dup,
                        ["extents"] = new JsonArray
                        {
                            Round(ext.MinPoint.X, 3), Round(ext.MinPoint.Y, 3),
                            Round(ext.MaxPoint.X, 3), Round(ext.MaxPoint.Y, 3)
                        }
                    });
                }

                var res = new JsonObject
                {
                    ["boundary_layer"] = bndLayer,
                    ["boundaries"] = boundaries,
                    ["open_polylines_skipped"] = skippedOpen,
                    ["nodes"] = nodesTotal,
                    ["texts"] = nodesTotal * 3,
                    ["grid_segments"] = segsTotal,
                    ["design_surface"] = designName,
                    ["existing_surface"] = existName,
                    ["design_outside"] = dOutTotal,
                    ["exist_outside"] = eOutTotal,
                    ["duplicate_nodes_skipped"] = dupNodes,
                    ["spacing"] = spacing,
                    ["text_height"] = h,
                    ["decimals"] = decimals,
                    ["cleared_old"] = cleared,
                    ["layers"] = new JsonObject
                    {
                        ["grid"] = lyGridName,
                        ["design"] = lyDesignName,
                        ["exist"] = lyExistName,
                        ["diff"] = lyDiffName
                    },
                    ["first_node_sample"] = sample,
                    ["per_boundary"] = perBoundary
                };
                tr.Commit();
                return res;
            }
        }

        // ---------- 清理上次产物：只删带 C3DF_GRIDELEV XData 的对象 ----------
        // ---------- 网格坐标 / 裁剪 / 内部判断（与 GridElev 同算法） ----------

        /// <summary>lo..hi 之间对齐到 step 整数倍的坐标序列，相邻地块网格能对齐。</summary>
        static List<double> GridCoords(double lo, double hi, double step)
        {
            var res = new List<double>();
            double first = Math.Ceiling(lo / step - 1e-9) * step;
            for (double v = first; v <= hi + 1e-9; v += step) res.Add(v);
            return res;
        }

        /// <summary>一条直线用边界裁剪，只画落在边界内的分段；返回画出的段数。</summary>
        static int GridDrawClipped(Transaction tr, BlockTableRecord btr, ObjectId layer,
            Entity boundary, List<Point2d> poly, Point3d a, Point3d b)
        {
            var pts = new Point3dCollection();
            using (var gl = new Line(a, b))
                boundary.IntersectWith(gl, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero);
            if (pts.Count < 2) return 0;

            var ordered = new List<Point3d>();
            foreach (Point3d p in pts) ordered.Add(p);
            ordered.Sort(delegate (Point3d p, Point3d q) { return a.DistanceTo(p).CompareTo(a.DistanceTo(q)); });

            int drawn = 0;
            for (int i = 0; i + 1 < ordered.Count; i++)
            {
                var mid = new Point2d((ordered[i].X + ordered[i + 1].X) / 2.0,
                                      (ordered[i].Y + ordered[i + 1].Y) / 2.0);
                if (!GridPointInPolygon(mid, poly)) continue;
                var ln = new Line(ordered[i], ordered[i + 1]);
                ln.LayerId = layer;
                btr.AppendEntity(ln);
                tr.AddNewlyCreatedDBObject(ln, true);
                GridSetXData(ln, GridTypeLine, ordered[i]);
                drawn++;
            }
            return drawn;
        }

        /// <summary>沿边界按步长采样成点环（弧段也能采到），供点在多边形内判断。</summary>
        static List<Point2d> GridSamplePolygon(Curve c, double step)
        {
            var pts = new List<Point2d>();
            double L;
            try { L = c.GetDistanceAtParameter(c.EndParam); }
            catch (System.Exception) { L = 0; }
            if (L <= 1e-9) return pts;
            int n = Math.Max(16, (int)Math.Ceiling(L / Math.Max(step, 1e-6)));
            for (int i = 0; i < n; i++)
            {
                try { Point3d p = c.GetPointAtDist(L * i / n); pts.Add(new Point2d(p.X, p.Y)); }
                catch (System.Exception) { }
            }
            return pts;
        }

        static bool GridPointInPolygon(Point2d p, List<Point2d> poly)
        {
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Point2d pi = poly[i], pj = poly[j];
                if (((pi.Y > p.Y) != (pj.Y > p.Y)) &&
                    (p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y) + pi.X))
                    inside = !inside;
            }
            return inside;
        }

        static bool GridTrySample(CivSurface s, Point3d p, out double z)
        {
            try { z = s.FindElevationAtXY(p.X, p.Y); return true; }
            catch (System.Exception) { z = 0; return false; }
        }

        // ---------- XData / 实体 / 图层 ----------

        static string GridNodeKey(Point3d p)
        {
            return Math.Round(p.X / GridNodeTol).ToString(CultureInfo.InvariantCulture) + "_" +
                   Math.Round(p.Y / GridNodeTol).ToString(CultureInfo.InvariantCulture);
        }

        static void GridSetXData(DBObject obj, string type, Point3d node)
        {
            obj.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, GridAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, type),
                new TypedValue((int)DxfCode.ExtendedDataWorldXCoordinate, node));
        }

        static void GridAddText(Transaction tr, Database db, BlockTableRecord btr, ObjectId layer,
            string content, double height, Point3d pt, TextHorizontalMode hMode, TextVerticalMode vMode,
            string type, Point3d node)
        {
            var t = new DBText();
            t.SetDatabaseDefaults();
            t.LayerId = layer;
            t.Height = height;
            t.TextString = content;
            t.Position = pt;
            t.HorizontalMode = hMode;
            t.VerticalMode = vMode;
            if (hMode != TextHorizontalMode.TextLeft || vMode != TextVerticalMode.TextBase)
                t.AlignmentPoint = pt;

            btr.AppendEntity(t);
            tr.AddNewlyCreatedDBObject(t, true);
            t.AdjustAlignment(db);
            GridSetXData(t, type, node);
        }

        static ObjectId GridEnsureLayer(Transaction tr, Database db, string name, short colorIndex)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return lt[name];
            lt.UpgradeOpen();
            var ltr = new LayerTableRecord();
            ltr.Name = name;
            ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIndex);
            ObjectId id = lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
            return id;
        }
    }
}
