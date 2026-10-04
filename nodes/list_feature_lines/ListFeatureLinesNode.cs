using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivSite = Autodesk.Civil.DatabaseServices.Site;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeListFeatureLines(JsonObject a, Document doc)
        {
            string layerFilter = GetString(a, "layer", null);
            Database db = doc.Database;
            var arr = new JsonArray();
            var byLayer = new JsonObject();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    CivFeatureLine fl; try { fl = tr.GetObject(id, OpenMode.ForRead) as CivFeatureLine; } catch { continue; }
                    if (fl == null) continue;
                    if (!string.IsNullOrEmpty(layerFilter) && !string.Equals(fl.Layer, layerFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    var rec = new JsonObject { ["handle"] = fl.Handle.ToString(), ["layer"] = fl.Layer, ["name"] = fl.Name ?? "" };
                    try { rec["closed"] = fl.Closed; } catch { }
                    try { rec["length_2d"] = Math.Round(fl.Length2D, 2); } catch { }
                    try { rec["site"] = fl.SiteId.IsNull ? "" : ((CivSite)tr.GetObject(fl.SiteId, OpenMode.ForRead)).Name; } catch { }
                    try { rec["style"] = fl.StyleName; } catch { }
                    try
                    {
                        var pts = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.AllPoints);
                        double zmin = double.MaxValue, zmax = double.MinValue, xmin = double.MaxValue, xmax = double.MinValue, ymin = double.MaxValue, ymax = double.MinValue;
                        foreach (Point3d p in pts) { zmin = Math.Min(zmin, p.Z); zmax = Math.Max(zmax, p.Z); xmin = Math.Min(xmin, p.X); xmax = Math.Max(xmax, p.X); ymin = Math.Min(ymin, p.Y); ymax = Math.Max(ymax, p.Y); }
                        rec["points"] = pts.Count; rec["z_min"] = Math.Round(zmin, 3); rec["z_max"] = Math.Round(zmax, 3);
                        rec["bbox"] = new JsonArray(Math.Round(xmin, 2), Math.Round(ymin, 2), Math.Round(xmax, 2), Math.Round(ymax, 2));
                    }
                    catch { }
                    arr.Add(rec);
                    string L = fl.Layer;
                    byLayer[L] = (byLayer[L] == null ? 0 : (int)byLayer[L]) + 1;
                }
                tr.Commit();
            }
            var gr = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    DBObject o; try { o = tr.GetObject(id, OpenMode.ForRead); } catch { continue; }
                    if (o.GetType().Name != "Grading") continue;
                    var e = o as Entity;
                    gr.Add(new JsonObject { ["handle"] = o.Handle.ToString(), ["layer"] = e != null ? e.Layer : "" });
                }
                tr.Commit();
            }
            return new JsonObject { ["count"] = arr.Count, ["by_layer"] = byLayer, ["feature_lines"] = arr, ["gradings"] = gr };
        }
    }
}
