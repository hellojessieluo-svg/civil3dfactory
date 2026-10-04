using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeCreateFlatSurface(JsonObject a, Document doc)
        {
            var items = a["items"] as JsonArray;
            if (items == null || items.Count == 0) throw new InvalidOperationException("items is required");
            double step = GetDouble(a, "step", 20);
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var rep = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (JsonNode n in items)
                {
                    var o = n as JsonObject; if (o == null) continue;
                    string name = o["name"].ToString(); double z = o["elev"].GetValue<double>();
                    var outer = o["outer"] as JsonArray; if (outer == null || outer.Count < 3) throw new InvalidOperationException(name + " outer requires at least 3 points");
                    bool replace = o["replace"] == null ? true : o["replace"].GetValue<bool>();
                    string styleName = o["style"] == null ? null : o["style"].ToString();
                    string layer = o["layer"] == null ? null : o["layer"].ToString();
                    foreach (ObjectId sid0 in civ.GetSurfaceIds())
                    {
                        var s0 = tr.GetObject(sid0, OpenMode.ForRead) as CivSurface;
                        if (s0 != null && s0.Name == name) { if (!replace) throw new InvalidOperationException("Already exists: " + name); s0.UpgradeOpen(); s0.Erase(); break; }
                    }
                    var pts = new Point3dCollection();
                    var outerPts = new Point3dCollection();
                    double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
                    var poly = new List<Point2d>();
                    foreach (JsonNode p in outer) { double x = p[0].GetValue<double>(), y = p[1].GetValue<double>(); outerPts.Add(new Point3d(x, y, z)); pts.Add(new Point3d(x, y, z)); poly.Add(new Point2d(x, y)); minx = Math.Min(minx, x); miny = Math.Min(miny, y); maxx = Math.Max(maxx, x); maxy = Math.Max(maxy, y); }
                    var holes = new List<Point3dCollection>();
                    var hArr = o["holes"] as JsonArray;
                    if (hArr != null) foreach (JsonNode h in hArr)
                    {
                        var hp = new Point3dCollection();
                        foreach (JsonNode p in (JsonArray)h) { double x = p[0].GetValue<double>(), y = p[1].GetValue<double>(); hp.Add(new Point3d(x, y, z)); pts.Add(new Point3d(x, y, z)); }
                        holes.Add(hp);
                    }
                    int inside = 0;
                    for (double x = minx + step / 2; x < maxx; x += step)
                        for (double y = miny + step / 2; y < maxy; y += step)
                            if (PointInPoly(poly, x, y)) { pts.Add(new Point3d(x, y, z)); inside++; }

                    ObjectId sid = CivTinSurface.Create(db, name);
                    var tin = (CivTinSurface)tr.GetObject(sid, OpenMode.ForWrite);
                    tin.AddVertices(pts);
                    var warn = new JsonArray();
                    try { tin.BoundariesDefinition.AddBoundaries(outerPts, 1.0, SurfaceBoundaryType.Outer, false); } catch (System.Exception ex) { warn.Add("Outer boundary failed: " + ex.Message); }
                    foreach (var hp in holes) { try { tin.BoundariesDefinition.AddBoundaries(hp, 1.0, SurfaceBoundaryType.Hide, false); } catch (System.Exception ex) { warn.Add("Hole failed: " + ex.Message); } }
                    if (!string.IsNullOrEmpty(styleName)) { ObjectId st = FindStyleId(tr, civ.Styles.SurfaceStyles, styleName); if (!st.IsNull) tin.StyleId = st; else warn.Add("Style not found: " + styleName); }
                    if (!string.IsNullOrEmpty(layer)) { EnsureLayer(db, tr, layer, 4); tin.Layer = layer; }
                    try { tin.Rebuild(); } catch (System.Exception ex) { warn.Add("Rebuild: " + ex.Message); }
                    int tri = 0; try { tri = tin.GetTinProperties().NumberOfTriangles; } catch { }
                    rep.Add(new JsonObject { ["name"] = name, ["handle"] = sid.Handle.ToString(), ["vertices"] = pts.Count, ["grid_points"] = inside, ["holes"] = holes.Count, ["triangles"] = tri, ["warnings"] = warn });
                }
                tr.Commit();
            }
            return new JsonObject { ["created"] = rep };
        }

        static bool PointInPoly(List<Point2d> poly, double x, double y)
        {
            bool c = false; int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                if (((poly[i].Y > y) != (poly[j].Y > y)) && (x < (poly[j].X - poly[i].X) * (y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)) c = !c;
            }
            return c;
        }
    }
}
