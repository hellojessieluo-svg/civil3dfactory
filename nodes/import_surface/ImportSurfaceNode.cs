using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        /// <summary>
        ///
        /// </summary>
        static JsonNode RunNodeImportSurface(JsonObject args, Document doc)
        {
            string path = Need(args, "dwg");
            string name = Need(args, "name");
            string mode = (GetString(args, "mode", "clone") ?? "clone").Trim().ToLowerInvariant();
            string asName = GetString(args, "as", null);
            if (string.IsNullOrWhiteSpace(asName)) asName = name;
            string layer = GetString(args, "layer", null);
            if (!File.Exists(path))
                throw new InvalidOperationException("Surface source drawing not found: " + path);
            if (mode != "clone" && mode != "snapshot" && mode != "rebuild")
                throw new InvalidOperationException("mode must be clone, snapshot or rebuild; received: " + mode);

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            bool replace = GetBool(args, "replace", false);
            bool replaced = false;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId oldId = FindSurfaceId(tr, civ, asName);
                if (!oldId.IsNull)
                {
                    if (!replace)
                {
                    tr.Commit();
                    return new JsonObject
                    {
                            ["surface"] = asName,
                        ["imported"] = false,
                        ["reason"] = "a surface of this name already exists in the current drawing; using it"
                    };
                }
                    var old = (CivSurface)tr.GetObject(oldId, OpenMode.ForWrite);
                    old.Erase();
                    replaced = true;
                }
                tr.Commit();
            }

            var res = new JsonObject { ["surface"] = asName, ["source_name"] = name, ["mode"] = mode, ["from"] = path, ["replaced"] = replaced };
            using (var srcDb = new Database(false, true))
            {
                srcDb.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
                ObjectId srcId = ObjectId.Null;
                using (Transaction str = srcDb.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)str.GetObject(srcDb.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)str.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        CivTinSurface s;
                        try { s = str.GetObject(id, OpenMode.ForRead) as CivTinSurface; }
                        catch { continue; }
                        if (s != null && s.Name == name) { srcId = id; break; }
                    }
                    str.Commit();
                }
                if (srcId.IsNull)
                    throw new InvalidOperationException(
                        "The source drawing has no TIN surface named '" + name + "': " + path);

                if (GetBool(args, "probe", false))
                {
                    using (Transaction str = srcDb.TransactionManager.StartTransaction())
                    {
                        var s = (CivTinSurface)str.GetObject(srcId, OpenMode.ForRead);
                        res["probe"] = ProbeSurfaceDefinition(s);
                        str.Commit();
                    }
                    res["imported"] = false;
                    return res;
                }

                if (mode == "rebuild")
                {
                    RebuildSurfaceFromSource(srcDb, srcId, db, civ, asName, layer, res,
                        GetBool(args, "boundaries", true), GetBool(args, "copy_build_options", true));
                }
                else
                {
                    if (mode == "snapshot")
                    {
                        using (Transaction str = srcDb.TransactionManager.StartTransaction())
                        {
                            var s = (CivTinSurface)str.GetObject(srcId, OpenMode.ForWrite);
                            s.CreateSnapshot();
                            str.Commit();
                        }
                        res["snapshot_taken"] = true;
                    }
                var ids = new ObjectIdCollection { srcId };
                var map = new IdMapping();
                ObjectId targetMs;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    targetMs = bt[BlockTableRecord.ModelSpace];
                    tr.Commit();
                }
                srcDb.WblockCloneObjects(ids, targetMs, map, DuplicateRecordCloning.Ignore, false);
            }
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId sid = FindSurfaceId(tr, civ, mode == "rebuild" ? asName : name);
                if (sid.IsNull)
                    throw new InvalidOperationException("Surface '" + name + "' still not found after WblockClone; import failed.");
                var surf = (CivSurface)tr.GetObject(sid, OpenMode.ForWrite);
                if (mode != "rebuild" && asName != name) surf.Name = asName;
                if (!string.IsNullOrEmpty(layer))
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (!lt.Has(layer))
                    {
                        lt.UpgradeOpen();
                        var ltr = new LayerTableRecord { Name = layer };
                        lt.Add(ltr); tr.AddNewlyCreatedDBObject(ltr, true);
                    }
                    surf.Layer = layer;
                }
                var props = surf.GetGeneralProperties();
                res["imported"] = true;
                res["elev_min"] = Math.Round(props.MinimumElevation, 3);
                res["elev_max"] = Math.Round(props.MaximumElevation, 3);
                res["points"] = props.NumberOfPoints;
                res["layer"] = surf.Layer;
                tr.Commit();
                return res;
            }
        }

        static JsonObject ProbeSurfaceDefinition(CivTinSurface s)
        {
            var o = new JsonObject();
            try { var bo = s.BuildOptions; o["use_max_triangle_length"] = bo.UseMaximumTriangleLength; o["max_triangle_length"] = bo.MaximumTriangleLength; }
            catch (System.Exception ex) { o["build_options_error"] = ex.Message; }
            try { o["boundaries"] = s.BoundariesDefinition.Count; } catch { }
            try { var bl = new JsonArray(); for (int i = 0; i < s.BoundariesDefinition.Count; i++) { string ty = "?"; try { ty = s.BoundariesDefinition[i].BoundaryType.ToString(); } catch { } bl.Add(ty); } o["boundary_types"] = bl; } catch { }
            try { o["breaklines"] = s.BreaklinesDefinition.Count; } catch { }
            try { o["contours"] = s.ContoursDefinition.Count; } catch { }
            try { o["drawing_objects"] = s.DrawingObjectsDefinition.Count; } catch { }
            try { o["point_files"] = s.PointFilesDefinition.Count; } catch { }
            try { o["point_groups"] = s.PointGroupsDefinition.Count; } catch { }
            try { o["operations"] = s.Operations.Count; } catch { }
            try { var ops = new JsonArray(); for (int i = 0; i < s.Operations.Count; i++) ops.Add(s.Operations[i].GetType().Name); o["operation_types"] = ops; } catch { }
            try { var bl = new JsonArray(); for (int i = 0; i < s.Operations.Count; i++) { var ab = s.Operations[i] as Autodesk.Civil.DatabaseServices.SurfaceOperationAddBreakline; if (ab == null) continue; int verts = 0; foreach (Autodesk.Civil.DatabaseServices.SurfaceBreakline b in ab) verts += b.Vertices.Count; bl.Add(new JsonObject { ["type"] = ab.BreaklineType.ToString(), ["count"] = ab.Count, ["vertices"] = verts, ["mid_ordinate"] = ab.MidOrdinateDistance, ["max_distance"] = ab.MaximumDistance, ["weed_distance"] = ab.WeedingDistance, ["weed_angle"] = ab.WeedingAngle, ["description"] = ab.Description }); } o["breakline_ops"] = bl; } catch (System.Exception ex) { o["breakline_ops_error"] = ex.Message; }
            try { var tp = s.GetTinProperties(); o["triangles"] = tp.NumberOfTriangles; o["max_edge"] = Math.Round(tp.MaximumTriangleLength, 3); } catch { }
            return o;
        }

        static void RebuildSurfaceFromSource(Database srcDb, ObjectId srcId, Database db, CivDoc civ,
                                             string asName, string layer, JsonObject res,
                                             bool addBoundaries = true, bool copyBuildOptions = true)
        {
            var pts = new Point3dCollection();
            var rings = new List<List<Point3d>>();
            double srcMin = 0, srcMax = 0; int srcPts = 0;
            bool useMaxLen = false; double maxLen = 0;
            using (Transaction str = srcDb.TransactionManager.StartTransaction())
            {
                var s = (CivTinSurface)str.GetObject(srcId, OpenMode.ForWrite);
                var gp = s.GetGeneralProperties();
                srcMin = gp.MinimumElevation; srcMax = gp.MaximumElevation; srcPts = gp.NumberOfPoints;
                try { useMaxLen = s.BuildOptions.UseMaximumTriangleLength; maxLen = s.BuildOptions.MaximumTriangleLength; } catch { }
                res["source_probe"] = ProbeSurfaceDefinition(s);
                foreach (var v in s.Vertices) pts.Add(v.Location);
                if (addBoundaries) try
                {
                    ObjectIdCollection ids = s.ExtractBorder(SurfaceExtractionSettingsType.Model);
                    foreach (ObjectId eid in ids)
                    {
                        var ent = str.GetObject(eid, OpenMode.ForWrite) as Entity;
                        if (ent == null) continue;
                        var ring = new List<Point3d>();
                        var pl = ent as Polyline; var p3 = ent as Polyline3d;
                        if (pl != null) for (int k = 0; k < pl.NumberOfVertices; k++) ring.Add(pl.GetPoint3dAt(k));
                        else if (p3 != null)
                            foreach (ObjectId vid in p3)
                            {
                                var v = str.GetObject(vid, OpenMode.ForRead) as PolylineVertex3d;
                                if (v != null) ring.Add(v.Position);
                            }
                        ent.Erase();
                        if (ring.Count >= 3) rings.Add(ring);
    }
}
                catch (System.Exception ex) { res["border_warning"] = "ExtractBorder failed; rebuilding without boundaries: " + ex.Message; }
                str.Commit();
            }
            if (pts.Count < 3) throw new InvalidOperationException("Source surface has fewer than 3 vertices; cannot rebuild.");

            int outerIdx = -1; double best = -1;
            for (int i = 0; i < rings.Count; i++)
            {
                double a = 0; var r = rings[i];
                for (int k = 0; k < r.Count; k++)
                {
                    var p = r[k]; var q = r[(k + 1) % r.Count];
                    a += p.X * q.Y - q.X * p.Y;
                }
                a = Math.Abs(a) / 2;
                if (a > best) { best = a; outerIdx = i; }
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId sid = CivTinSurface.Create(db, asName);
                var tin = (CivTinSurface)tr.GetObject(sid, OpenMode.ForWrite);
                if (copyBuildOptions && useMaxLen)
                {
                    try { tin.BuildOptions.UseMaximumTriangleLength = true; tin.BuildOptions.MaximumTriangleLength = maxLen; res["max_triangle_length_copied"] = maxLen; }
                    catch (System.Exception ex) { res["build_options_warning"] = ex.Message; }
                }
                tin.AddVertices(pts);
                int bOk = 0; var bWarn = new JsonArray();
                for (int i = 0; i < rings.Count; i++)
                {
                    var bp = new Point3dCollection();
                    foreach (var p in rings[i]) bp.Add(p);
                    try
                    {
                        tin.BoundariesDefinition.AddBoundaries(bp, 1.0,
                            i == outerIdx ? SurfaceBoundaryType.Outer : SurfaceBoundaryType.Hide, false);
                        bOk++;
                    }
                    catch (System.Exception ex) { bWarn.Add((i == outerIdx ? "Outer boundary" : "Hole boundary") + "Failed: " + ex.Message); }
                }
                bool outdatedBefore = false; try { outdatedBefore = tin.IsOutOfDate; } catch { }
                string rebuildErr = null;
                try { tin.Rebuild(); } catch (System.Exception ex) { rebuildErr = ex.GetType().Name + ": " + ex.Message; }
                bool outdatedAfter = false; try { outdatedAfter = tin.IsOutOfDate; } catch { }
                tr.Commit();
                res["out_of_date_before"] = outdatedBefore;
                res["out_of_date_after"] = outdatedAfter;
                if (rebuildErr != null) res["rebuild_error"] = rebuildErr;
                res["source_points"] = srcPts;
                res["source_elev_min"] = Math.Round(srcMin, 3);
                res["source_elev_max"] = Math.Round(srcMax, 3);
                res["vertices_copied"] = pts.Count;
                res["rings_found"] = rings.Count;
                res["boundaries_added"] = bOk;
                if (bWarn.Count > 0) res["boundary_warnings"] = bWarn;
            }
        }
    }
}
