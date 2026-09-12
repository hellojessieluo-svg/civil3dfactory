using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Civil3DFactory.Geometry;
using CivAlign = Autodesk.Civil.DatabaseServices.Alignment;
using CivilDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// replace_alignment_geometry：原位重写路线几何——JSON 薄壳。
    /// 算法核心在同目录 AlignmentRebuildCore.cs（与 products\waterbox 的
    /// C3DF-ReplaceCenterline/HZX 同核），几何映射与劈弧规则见核心文件注释。
    ///
    /// list_polylines：按图层列出模型空间多段线（句柄/长度/端点/圆弧段数），
    /// 给上面这个节点找源线句柄用。
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeReplaceAlignmentGeometry(JsonObject args, Document doc)
            => ReplaceAlignmentGeometry(args, doc);

        static JsonNode RunNodeListPolylines(JsonObject args, Document doc)
            => ListPolylines(args, doc);

        public static JsonNode ListPolylines(JsonObject a, Document doc)
        {
            string layer = GetString(a, "layer", null);
            double minLen = GetDouble(a, "min_length", 0.0);
            int max = (int)GetDouble(a, "max", 200);

            Database db = doc.Database;
            var arr = new JsonArray();
            int total = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    Polyline pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (pl == null) continue;
                    if (!string.IsNullOrWhiteSpace(layer) &&
                        !string.Equals(pl.Layer, layer, StringComparison.OrdinalIgnoreCase))
                        continue;
                    double len = 0.0;
                    try { len = pl.Length; } catch (System.Exception) { }
                    if (len < minLen) continue;
                    total++;
                    if (arr.Count >= max) continue;

                    int nv = pl.NumberOfVertices;
                    int arcs = 0;
                    for (int i = 0; i < nv - 1; i++)
                        if (Math.Abs(pl.GetBulgeAt(i)) > 1e-9) arcs++;
                    Point2d sp = pl.GetPoint2dAt(0);
                    Point2d ep = pl.GetPoint2dAt(nv - 1);
                    // 包围盒：闭合图框拿它就能直接喂 plot_pdf 的 window 参数逐张打印
                    double bx0 = double.MaxValue, by0 = double.MaxValue;
                    double bx1 = double.MinValue, by1 = double.MinValue;
                    for (int i = 0; i < nv; i++)
                    {
                        Point2d v = pl.GetPoint2dAt(i);
                        if (v.X < bx0) bx0 = v.X; if (v.X > bx1) bx1 = v.X;
                        if (v.Y < by0) by0 = v.Y; if (v.Y > by1) by1 = v.Y;
                    }
                    arr.Add(new JsonObject
                    {
                        ["bbox"] = new JsonArray { Round(bx0, 4), Round(by0, 4), Round(bx1, 4), Round(by1, 4) },
                        ["handle"] = pl.Handle.ToString(),
                        ["layer"] = pl.Layer,
                        ["color"] = pl.Color.ToString(),
                        ["linetype"] = pl.Linetype,
                        ["linetype_scale"] = Round2(pl.LinetypeScale),
                        ["length"] = Round(len, 3),
                        ["closed"] = pl.Closed,
                        ["vertices"] = nv,
                        ["arc_segments"] = arcs,
                        ["start"] = new JsonArray { Round(sp.X, 3), Round(sp.Y, 3) },
                        ["end"] = new JsonArray { Round(ep.X, 3), Round(ep.Y, 3) }
                    });
                }
                tr.Commit();
            }
            return new JsonObject { ["count"] = total, ["polylines"] = arr };
        }

        public static JsonNode ReplaceAlignmentGeometry(JsonObject a, Document doc)
        {
            string alName = GetString(a, "alignment", null);
            string alHandle = GetString(a, "alignment_handle", null);
            string plHandle = Need(a, "polyline");
            bool erasePl = GetBool(a, "erase_polyline", false);

            Database db = doc.Database;
            var res = new JsonObject();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivilDoc civ = Civ(db);
                CivAlign al;
                if (!string.IsNullOrWhiteSpace(alHandle))
                {
                    al = tr.GetObject(ResolveHandle(db, alHandle), OpenMode.ForRead) as CivAlign;
                    if (al == null)
                        throw new InvalidOperationException("句柄 " + alHandle + " 不是路线对象。");
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(alName))
                        throw new InvalidOperationException("需要 alignment（路线名）或 alignment_handle。");
                    al = FindAlignment(tr, civ, alName);
                    if (al == null)
                        throw new InvalidOperationException("图中没有名为 " + alName + " 的路线。");
                }

                Polyline pl = tr.GetObject(ResolveHandle(db, plHandle), OpenMode.ForRead) as Polyline;
                if (pl == null)
                    throw new InvalidOperationException("句柄 " + plHandle + " 不是多段线（LWPOLYLINE）。");

                al.UpgradeOpen();
                AlignmentRebuildCore.RebuildResult r = AlignmentRebuildCore.RebuildFromPolyline(al, pl);

                bool plErased = false;
                if (erasePl)
                {
                    pl.UpgradeOpen();
                    pl.Erase();
                    plErased = true;
                }

                res["alignment"] = al.Name;
                res["alignment_handle"] = al.Handle.ToString();
                res["old_length"] = Round(r.OldLength, 4);
                res["polyline_length"] = Round(r.PolylineLength, 4);
                res["new_length"] = Round(r.NewLength, 4);
                res["length_delta_vs_polyline"] = Round(r.NewLength - r.PolylineLength, 4);
                res["old_entities"] = r.OldEntities;
                res["new_entities"] = r.NewEntities;
                res["reversed"] = r.Reversed;
                res["segments"] = new JsonObject
                {
                    ["line"] = r.Lines,
                    ["arc"] = r.Arcs,
                    ["arc_splits"] = r.ArcSplits
                };
                res["start_station"] = Round(al.StartingStation, 4);
                res["end_station"] = Round(al.EndingStation, 4);
                res["polyline_erased"] = plErased;

                tr.Commit();
            }
            return res;
        }
    }
}
