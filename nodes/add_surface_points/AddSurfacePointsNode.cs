using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeAddSurfacePoints(JsonObject a, Document doc)
        {
            var items = a["items"] as JsonArray;
            if (items == null || items.Count == 0) throw new InvalidOperationException("items is required");
            bool rebuild = GetBool(a, "rebuild", true);
            bool nonDestructive = GetBool(a, "non_destructive", true);
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var rep = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (JsonNode n in items)
                {
                    var o = n as JsonObject; if (o == null) continue;
                    string name = o["surface"].ToString();
                    CivTinSurface tin = null;
                    foreach (ObjectId sid0 in civ.GetSurfaceIds())
                    {
                        var s0 = tr.GetObject(sid0, OpenMode.ForRead) as CivTinSurface;
                        if (s0 != null && s0.Name == name) { s0.UpgradeOpen(); tin = s0; break; }
                    }
                    if (tin == null) throw new InvalidOperationException("TIN surface not found: " + name);
                    var warn = new JsonArray();
                    double? elev = o["elev"] == null ? (double?)null : o["elev"].GetValue<double>();
                    JsonArray ptsArr = o["points"] as JsonArray;
                    if (ptsArr == null && o["points_file"] != null)
                        ptsArr = JsonNode.Parse(File.ReadAllText(o["points_file"].ToString())) as JsonArray;
                    var pts = new Point3dCollection();
                    if (ptsArr != null) foreach (JsonNode p in ptsArr)
                    {
                        double x = p[0].GetValue<double>(), y = p[1].GetValue<double>();
                        double z = p.AsArray().Count > 2 ? p[2].GetValue<double>() : (elev ?? 0);
                        pts.Add(new Point3d(x, y, z));
                    }
                    int trisBefore = 0; try { trisBefore = tin.GetTinProperties().NumberOfTriangles; } catch { }
                    var bdBefore = new JsonArray();
                    try { var bd0 = tin.BoundariesDefinition; for (int i = 0; i < bd0.Count; i++) { string ty = "?"; try { ty = bd0[i].BoundaryType.ToString(); } catch { } bdBefore.Add(ty); } } catch { }
                    int removedOuter = 0;
                    var outer = o["outer"] as JsonArray;
                    bool replaceOuter = outer != null && outer.Count >= 3;
                    var holes = o["holes"] as JsonArray;
                    bool clearAll = GetBool(a, "clear_boundaries", holes != null);
                    if (replaceOuter)
                    {
                        var bd = tin.BoundariesDefinition;
                        for (int i = bd.Count - 1; i >= 0; i--)
                        {
                            try { if (clearAll || bd[i].BoundaryType == SurfaceBoundaryType.Outer) { bd.RemoveAt(i); removedOuter++; } }
                            catch (System.Exception ex) { warn.Add("Remove boundary " + i + " failed: " + ex.Message); }
                        }
                    }
                    if (pts.Count > 0)
                    {
                        try { tin.AddVertices(pts); } catch (System.Exception ex) { warn.Add("AddVertices: " + ex.Message); }
                    }
                    if (replaceOuter)
                    {
                        var op = new Point3dCollection();
                        foreach (JsonNode p in outer) op.Add(new Point3d(p[0].GetValue<double>(), p[1].GetValue<double>(), elev ?? 0));
                        try { tin.BoundariesDefinition.AddBoundaries(op, 1.0, SurfaceBoundaryType.Outer, nonDestructive); }
                        catch (System.Exception ex) { warn.Add("New outer boundary failed: " + ex.Message); }
                    }
                    int holesAdded = 0;
                    bool holesNd = GetBool(a, "holes_non_destructive", true);
                    if (holes != null)
                        foreach (JsonNode hn in holes)
                        {
                            var hr = hn as JsonArray; if (hr == null || hr.Count < 3) continue;
                            var hp = new Point3dCollection();
                            foreach (JsonNode p in hr) hp.Add(new Point3d(p[0].GetValue<double>(), p[1].GetValue<double>(), elev ?? 0));
                            try { tin.BoundariesDefinition.AddBoundaries(hp, 1.0, SurfaceBoundaryType.Hide, holesNd); holesAdded++; }
                            catch (System.Exception ex) { warn.Add("Hide boundary failed: " + ex.Message); }
                        }
                    if (rebuild) { try { tin.Rebuild(); } catch (System.Exception ex) { warn.Add("Rebuild: " + ex.Message); } }
                    int tri = 0; try { tri = tin.GetTinProperties().NumberOfTriangles; } catch { }
                    var bdAfter = new JsonArray();
                    try { var bd1 = tin.BoundariesDefinition; for (int i = 0; i < bd1.Count; i++) { string ty = "?"; try { ty = bd1[i].BoundaryType.ToString(); } catch { } bdAfter.Add(ty); } } catch { }
                    rep.Add(new JsonObject { ["surface"] = name, ["points_added"] = pts.Count, ["outer_removed"] = removedOuter, ["outer_replaced"] = replaceOuter, ["non_destructive"] = nonDestructive, ["holes_added"] = holesAdded, ["boundaries_cleared"] = clearAll, ["triangles_before"] = trisBefore, ["triangles_after"] = tri, ["boundaries_before"] = bdBefore, ["boundaries_after"] = bdAfter, ["warnings"] = warn });
                }
                tr.Commit();
            }
            return new JsonObject { ["items"] = rep };
        }
    }
}
