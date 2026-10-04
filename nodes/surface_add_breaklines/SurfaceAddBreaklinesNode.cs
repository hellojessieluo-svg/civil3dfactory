using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;
using CivFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeSurfaceAddBreaklines(JsonObject a, Document doc)
        {
            string sfName = Need(a, "surface");
            bool allFl = GetBool(a, "feature_lines", false);
            double midOrd = GetDouble(a, "mid_ordinate", 0.01);
            double maxDist = GetDouble(a, "max_distance", 0);
            double weedDist = GetDouble(a, "weed_distance", 0);
            double weedAng = GetDouble(a, "weed_angle", 0);
            string desc = GetString(a, "description", "Breakline set1");
            string style = GetString(a, "style", null);
            string layer = GetString(a, "layer", null);
            bool rebuild = GetBool(a, "rebuild", true);
            bool dryRun = GetBool(a, "dry_run", false);
            string within = GetString(a, "within_surface", null);
            double minInside = GetDouble(a, "min_inside_ratio", 0.5);
            int index = a["index"] != null ? (int)a["index"].GetValue<double>() : -1;
            var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (a["layers"] is JsonArray la) foreach (JsonNode n in la) if (n != null) layers.Add(n.ToString());
            var handles = new List<string>();
            if (a["handles"] is JsonArray ha) foreach (JsonNode n in ha) if (n != null) handles.Add(n.ToString());

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var res = new JsonObject { ["surface"] = sfName, ["dry_run"] = dryRun };
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var ids = new ObjectIdCollection();
                var seen = new HashSet<ObjectId>();
                var byType = new JsonObject();
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                void Take(ObjectId id, Entity e)
                {
                    if (!seen.Add(id)) return;
                    ids.Add(id);
                    string tn = e.GetType().Name;
                    byType[tn] = (byType[tn] == null ? 0 : (int)byType[tn]) + 1;
                }
                foreach (ObjectId id in ms)
                {
                    Entity e; try { e = tr.GetObject(id, OpenMode.ForRead) as Entity; } catch { continue; }
                    if (e == null) continue;
                    bool isFl = e is CivFeatureLine;
                    bool isPl = e is Polyline || e is Polyline3d || e is Polyline2d;
                    if (allFl && isFl) { Take(id, e); continue; }
                    if (layers.Count > 0 && layers.Contains(e.Layer) && (isFl || isPl)) { Take(id, e); continue; }
                }
                foreach (string h in handles)
                {
                    ObjectId id = ResolveHandle(db, h);
                    var e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (e == null) throw new InvalidOperationException("Handle " + h + " is not an entity");
                    Take(id, e);
                }
                if (!string.IsNullOrEmpty(within))
                {
                    ObjectId wid = FindSurfaceId(tr, civ, within);
                    if (wid.IsNull) throw new InvalidOperationException("within_surface not found: '" + within + "'");
                    var wt = tr.GetObject(wid, OpenMode.ForRead) as CivTinSurface;
                    if (wt == null) throw new InvalidOperationException("within_surface is not a TIN surface");
                    var kept = new ObjectIdCollection(); var keptType = new JsonObject(); var dropped = 0;
                    foreach (ObjectId id in ids)
                    {
                        var e = (Entity)tr.GetObject(id, OpenMode.ForRead);
                        var pts = EntityVertices(e);
                        int inside = 0;
                        foreach (var pt in pts) { try { wt.FindElevationAtXY(pt.X, pt.Y); inside++; } catch { } }
                        if (pts.Count > 0 && inside >= minInside * pts.Count)
                        {
                            kept.Add(id);
                            string tn = e.GetType().Name;
                            keptType[tn] = (keptType[tn] == null ? 0 : (int)keptType[tn]) + 1;
                        }
                        else dropped++;
                    }
                    res["candidates"] = ids.Count; res["outside_dropped"] = dropped;
                    ids = kept; byType = keptType;
                }
                res["entities"] = ids.Count; res["by_type"] = byType;
                if (ids.Count == 0) throw new InvalidOperationException("No entities selected; specify feature_lines, layers or handles");
                if (dryRun) { tr.Abort(); return res; }

                ObjectId sfId = FindSurfaceId(tr, civ, sfName);
                CivTinSurface tin;
                if (sfId.IsNull)
                {
                    ObjectId styleId = FindStyleId(tr, civ.Styles.SurfaceStyles, style);
                    sfId = styleId.IsNull ? CivTinSurface.Create(db, sfName) : CivTinSurface.Create(sfName, styleId);
                    tin = (CivTinSurface)tr.GetObject(sfId, OpenMode.ForWrite);
                    res["created"] = true;
                }
                else
                {
                    tin = tr.GetObject(sfId, OpenMode.ForWrite) as CivTinSurface;
                    if (tin == null) throw new InvalidOperationException("'" + sfName + "' is not a TIN surface");
                    res["created"] = false;
                }
                if (!string.IsNullOrEmpty(layer))
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (!lt.Has(layer)) { lt.UpgradeOpen(); var ltr = new LayerTableRecord { Name = layer }; lt.Add(ltr); tr.AddNewlyCreatedDBObject(ltr, true); }
                    tin.Layer = layer;
                }
                if (GetBool(a, "clear_existing", false))
                {
                    int cleared = 0;
                    for (int i = tin.Operations.Count - 1; i >= 0; i--)
                        if (tin.Operations[i] is Autodesk.Civil.DatabaseServices.SurfaceOperationAddBreakline) { tin.Operations.RemoveAt(i); cleared++; }
                    res["cleared_breakline_ops"] = cleared;
                }
                var op = tin.BreaklinesDefinition.AddStandardBreaklines(ids, midOrd, maxDist, weedDist, weedAng);
                try { op.Description = desc; } catch { }
                res["breaklines_added"] = op.Count;
                if (index >= 0)
                {
                    int last = tin.Operations.Count - 1; int target = Math.Min(index, last);
                    for (int i = last; i > target; i--) tin.Operations.SwapAt(i, i - 1);
                    res["moved_to_index"] = target;
                }
                int verts = 0; try { foreach (Autodesk.Civil.DatabaseServices.SurfaceBreakline b in op) verts += b.Vertices.Count; } catch { }
                res["vertices"] = verts;
                if (rebuild) { try { tin.Rebuild(); res["rebuilt"] = true; } catch (Exception ex) { res["rebuilt"] = false; res["rebuild_error"] = ex.Message; } }
                try { var gp = tin.GetGeneralProperties(); res["points"] = gp.NumberOfPoints; res["elev_min"] = Math.Round(gp.MinimumElevation, 3); res["elev_max"] = Math.Round(gp.MaximumElevation, 3); } catch (Exception ex) { res["props_error"] = ex.Message; }
                try { res["triangles"] = tin.GetTinProperties().NumberOfTriangles; } catch (Exception ex) { res["tin_error"] = ex.Message; }
                res["handle"] = tin.Handle.ToString();
                tr.Commit();
            }
            return res;
        }

        static List<Autodesk.AutoCAD.Geometry.Point3d> EntityVertices(Entity e)
        {
            var pts = new List<Autodesk.AutoCAD.Geometry.Point3d>();
            if (e is CivFeatureLine fl)
            {
                try { foreach (Autodesk.AutoCAD.Geometry.Point3d p in fl.GetPoints(Autodesk.Civil.FeatureLinePointType.AllPoints)) pts.Add(p); } catch { }
            }
            else if (e is Polyline pl)
            {
                for (int i = 0; i < pl.NumberOfVertices; i++) pts.Add(pl.GetPoint3dAt(i));
            }
            else if (e is Polyline3d p3)
            {
                using (Transaction t = e.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId vid in p3) { var v = (PolylineVertex3d)t.GetObject(vid, OpenMode.ForRead); pts.Add(v.Position); }
                    t.Commit();
                }
            }
            else if (e is Curve c)
            {
                try { pts.Add(c.StartPoint); pts.Add(c.EndPoint); pts.Add(c.GetPointAtDist(c.GetDistanceAtParameter(c.EndParam) / 2)); } catch { }
            }
            return pts;
        }
    }
}
