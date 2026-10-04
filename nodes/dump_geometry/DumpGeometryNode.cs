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
        static JsonNode RunNodeDumpGeometry(JsonObject a, Document doc)
        {
            var names = new List<string>(); var arr = a["surfaces"] as JsonArray;
            if (arr != null) foreach (JsonNode n in arr) names.Add(n.ToString());
            string prefix = GetString(a, "surface_prefix", null);
            var layers = new HashSet<string>(); var lar = a["layers"] as JsonArray;
            if (lar != null) foreach (JsonNode n in lar) layers.Add(n.ToString());
            string outPath = GetString(a, "out", null);
            double arcTol = GetDouble(a, "arc_tol", 0.5);

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var surfaces = new JsonArray(); var plines = new JsonArray(); var hatches = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civ.GetSurfaceIds())
                {
                    var s = tr.GetObject(id, OpenMode.ForRead) as CivTinSurface;
                    if (s == null) continue;
                    bool hit = names.Contains(s.Name) || (!string.IsNullOrEmpty(prefix) && s.Name.StartsWith(prefix, StringComparison.Ordinal));
                    if (!hit) continue;
                    s.UpgradeOpen();
                    var rings = new JsonArray();
                    try
                    {
                        ObjectIdCollection ids = s.ExtractBorder(SurfaceExtractionSettingsType.Model);
                        foreach (ObjectId eid in ids)
                        {
                            var ent = tr.GetObject(eid, OpenMode.ForWrite) as Entity;
                            if (ent == null) continue;
                            var ring = new JsonArray();
                            var pl = ent as Polyline; var p3 = ent as Polyline3d;
                            if (pl != null) for (int k = 0; k < pl.NumberOfVertices; k++) { var p = pl.GetPoint3dAt(k); ring.Add(new JsonArray(Math.Round(p.X, 4), Math.Round(p.Y, 4), Math.Round(p.Z, 4))); }
                            else if (p3 != null) foreach (ObjectId vid in p3) { var v = tr.GetObject(vid, OpenMode.ForRead) as PolylineVertex3d; if (v != null) ring.Add(new JsonArray(Math.Round(v.Position.X, 4), Math.Round(v.Position.Y, 4), Math.Round(v.Position.Z, 4))); }
                            ent.Erase();
                            if (ring.Count >= 3) rings.Add(ring);
                        }
                    }
                    catch (System.Exception ex) { rings.Add(JsonValue.Create("ExtractBorder failed: " + ex.Message)); }
                    surfaces.Add(new JsonObject { ["name"] = s.Name, ["rings"] = rings });
                }
                if (layers.Count > 0)
                {
                    foreach (ObjectId eid in ModelSpace(db, tr))
                    {
                        var ent = tr.GetObject(eid, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        bool layerHit = layers.Contains(ent.Layer) || layers.Contains("*");
                        if (!layerHit) foreach (string l in layers) if (l.EndsWith("*") && ent.Layer.StartsWith(l.TrimEnd('*'), StringComparison.Ordinal)) { layerHit = true; break; }
                        if (!layerHit) continue;
                        var pl = ent as Polyline; var p3 = ent as Polyline3d;
                        if (pl != null)
                        {
                            var vs = new JsonArray();
                            for (int k = 0; k < pl.NumberOfVertices; k++) { var p = pl.GetPoint2dAt(k); vs.Add(new JsonArray(Math.Round(p.X, 4), Math.Round(p.Y, 4), Math.Round(pl.GetBulgeAt(k), 6))); }
                            plines.Add(new JsonObject { ["handle"] = pl.Handle.ToString(), ["layer"] = pl.Layer, ["closed"] = pl.Closed, ["elevation"] = pl.Elevation, ["vertices"] = vs });
                        }
                        else if (p3 != null)
                        {
                            var vs = new JsonArray();
                            foreach (ObjectId vid in p3) { var v = tr.GetObject(vid, OpenMode.ForRead) as PolylineVertex3d; if (v != null) vs.Add(new JsonArray(Math.Round(v.Position.X, 4), Math.Round(v.Position.Y, 4), Math.Round(v.Position.Z, 4))); }
                            plines.Add(new JsonObject { ["handle"] = p3.Handle.ToString(), ["layer"] = p3.Layer, ["closed"] = p3.Closed, ["is3d"] = true, ["vertices"] = vs });
                        }
                        else if (ent is Hatch h)
                        {
                            var loops = new JsonArray();
                            for (int li = 0; li < h.NumberOfLoops; li++)
                            {
                                HatchLoop loop;
                                try { loop = h.GetLoopAt(li); } catch (System.Exception ex) { loops.Add(new JsonObject { ["error"] = ex.Message }); continue; }
                                var vs = new JsonArray();
                                foreach (var p in HatchLoopPoints(loop, arcTol)) vs.Add(new JsonArray(Math.Round(p.X, 4), Math.Round(p.Y, 4)));
                                loops.Add(new JsonObject { ["type"] = loop.LoopType.ToString(), ["is_polyline"] = loop.IsPolyline, ["vertices"] = vs });
                            }
                            double area = 0; try { area = h.Area; } catch { }
                            hatches.Add(new JsonObject { ["handle"] = h.Handle.ToString(), ["layer"] = h.Layer, ["pattern"] = h.PatternName, ["area"] = Math.Round(area, 3), ["loops"] = loops });
                        }
                    }
                }
                tr.Commit();
            }
            var rep = new JsonObject { ["surfaces"] = surfaces, ["polylines"] = plines, ["hatches"] = hatches };
            if (!string.IsNullOrEmpty(outPath))
            {
                System.IO.File.WriteAllText(outPath, rep.ToJsonString(), new System.Text.UTF8Encoding(false));
                return new JsonObject { ["out"] = outPath, ["surfaces"] = surfaces.Count, ["polylines"] = plines.Count, ["hatches"] = hatches.Count };
            }
            return rep;
        }

        static List<Point2d> HatchLoopPoints(HatchLoop loop, double arcTol)
        {
            var pts = new List<Point2d>();
            if (arcTol <= 0) arcTol = 0.5;
            if (loop.IsPolyline)
            {
                BulgeVertexCollection bvc = loop.Polyline;
                int n = bvc.Count;
                for (int k = 0; k < n; k++)
                {
                    Point2d p = bvc[k].Vertex; double b = bvc[k].Bulge;
                    pts.Add(p);
                    if (Math.Abs(b) < 1e-9) continue;
                    Point2d q = bvc[(k + 1) % n].Vertex;
                    double dx = q.X - p.X, dy = q.Y - p.Y, d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < 1e-9) continue;
                    double theta = 4 * Math.Atan(b);
                    double r = d / (2 * Math.Sin(Math.Abs(theta) / 2));
                    double mx = (p.X + q.X) / 2, my = (p.Y + q.Y) / 2;
                    double nx = -dy / d, ny = dx / d;
                    double off = (d / 2) * (1 - b * b) / (2 * b);
                    double cx = mx + nx * off, cy = my + ny * off;
                    double a0 = Math.Atan2(p.Y - cy, p.X - cx);
                    int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(theta) * r / arcTol));
                    for (int i = 1; i < steps; i++)
                    {
                        double a = a0 + theta * i / steps;
                        pts.Add(new Point2d(cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
                    }
                }
            }
            else
            {
                foreach (Curve2d c in loop.Curves)
                {
                    int ns;
                    if (c is LineSegment2d) ns = 2;
                    else if (c is CircularArc2d arc) ns = Math.Max(3, (int)Math.Ceiling(Math.Abs(arc.EndAngle - arc.StartAngle) * arc.Radius / arcTol) + 1);
                    else ns = 17;
                    Point2d[] sp;
                    try { sp = c.GetSamplePoints(ns); } catch { sp = new[] { c.StartPoint, c.EndPoint }; }
                    for (int i = 0; i < sp.Length - 1; i++) pts.Add(sp[i]);
                }
            }
            if (pts.Count > 1 && pts[0].GetDistanceTo(pts[pts.Count - 1]) < 1e-6) pts.RemoveAt(pts.Count - 1);
            return pts;
        }
    }
}
