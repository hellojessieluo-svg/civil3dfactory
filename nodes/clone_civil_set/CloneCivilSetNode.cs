using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;
using CivFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivGrading = Autodesk.Civil.DatabaseServices.Grading;
using CivSite = Autodesk.Civil.DatabaseServices.Site;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeCloneCivilSet(JsonObject a, Document doc)
        {
            string path = Need(a, "dwg");
            if (!System.IO.File.Exists(path)) throw new InvalidOperationException("Source drawing does not exist: " + path);
            var wantSurfaces = new HashSet<string>(StringComparer.Ordinal);
            if (a["surfaces"] is JsonArray sa) foreach (JsonNode n in sa) if (n != null) wantSurfaces.Add(n.ToString());
            bool allSurfaces = GetBool(a, "all_surfaces", false);
            bool inclFl = GetBool(a, "feature_lines", true);
            bool inclGr = GetBool(a, "gradings", true);
            bool inclSites = GetBool(a, "sites", true);
            bool dryRun = GetBool(a, "dry_run", false);
            var extraLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (a["layers"] is JsonArray la) foreach (JsonNode n in la) if (n != null) extraLayers.Add(n.ToString());

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var res = new JsonObject { ["from"] = path, ["dry_run"] = dryRun };

            var beforeNames = new HashSet<string>(StringComparer.Ordinal);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civ.GetSurfaceIds())
                {
                    var s = tr.GetObject(id, OpenMode.ForRead) as CivSurface;
                    if (s != null) beforeNames.Add(s.Name);
                }
                tr.Commit();
            }

            var counts = new JsonObject();
            var picked = new JsonArray();
            using (var srcDb = new Database(false, true))
            {
                srcDb.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
                var ids = new ObjectIdCollection();
                var siteIds = new HashSet<ObjectId>();
                int nFl = 0, nGr = 0, nSf = 0, nEx = 0, nSite = 0;
                using (Transaction str = srcDb.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)str.GetObject(srcDb.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)str.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        DBObject o;
                        try { o = str.GetObject(id, OpenMode.ForRead); } catch { continue; }
                        var ent = o as Entity;
                        if (o is CivTinSurface ts)
                        {
                            if (allSurfaces || wantSurfaces.Contains(ts.Name))
                            {
                                ids.Add(id); nSf++;
                                picked.Add(new JsonObject { ["type"] = "TinSurface", ["name"] = ts.Name, ["handle"] = id.Handle.ToString() });
                            }
                            continue;
                        }
                        if (o is CivFeatureLine fl)
                        {
                            if (inclFl) { ids.Add(id); nFl++; try { if (!fl.SiteId.IsNull) siteIds.Add(fl.SiteId); } catch { } }
                            continue;
                        }
                        if (o is CivGrading gr)
                        {
                            if (inclGr) { ids.Add(id); nGr++; }
                            continue;
                        }
                        if (ent != null && extraLayers.Count > 0 && extraLayers.Contains(ent.Layer))
                        {
                            ids.Add(id); nEx++;
                        }
                    }
                    if (inclSites)
                    {
                        try
                        {
                            CivDoc srcCiv = CivDoc.GetCivilDocument(srcDb);
                            foreach (ObjectId sid in srcCiv.GetSiteIds()) siteIds.Add(sid);
                        }
                        catch (System.Exception ex) { res["site_scan_error"] = ex.Message; }
                        foreach (ObjectId sid in siteIds)
                        {
                            string sname = "?";
                            try { var site = (CivSite)str.GetObject(sid, OpenMode.ForRead); sname = site.Name; } catch { }
                            ids.Add(sid); nSite++;
                            picked.Add(new JsonObject { ["type"] = "Site", ["name"] = sname, ["handle"] = sid.Handle.ToString() });
                        }
                    }
                    str.Commit();
                }
                counts["surfaces"] = nSf; counts["feature_lines"] = nFl; counts["gradings"] = nGr; counts["sites"] = nSite; counts["extra_layer_entities"] = nEx;
                res["picked"] = counts;
                res["picked_named"] = picked;
                if (wantSurfaces.Count > 0 && nSf < wantSurfaces.Count)
                {
                    var missing = new JsonArray();
                    var got = new HashSet<string>();
                    foreach (JsonNode p in picked) if (p["type"].ToString() == "TinSurface") got.Add(p["name"].ToString());
                    foreach (string w in wantSurfaces) if (!got.Contains(w)) missing.Add(w);
                    res["missing_surfaces"] = missing;
                    throw new InvalidOperationException("Surface not found in source drawing: " + string.Join(", ", missing));
                }
                if (dryRun || ids.Count == 0) { res["cloned"] = 0; return res; }

                ObjectId targetMs;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    targetMs = bt[BlockTableRecord.ModelSpace];
                    tr.Commit();
                }
                var map = new IdMapping();
                srcDb.WblockCloneObjects(ids, targetMs, map, DuplicateRecordCloning.Ignore, false);
                int cloned = 0;
                var clonedIds = new List<ObjectId>();
                foreach (IdPair p in map) if (p.IsCloned && p.IsPrimary) { cloned++; clonedIds.Add(p.Value); }
                res["cloned"] = cloned;
                try
                {
                    var ch = new JsonArray();
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        foreach (ObjectId cid in clonedIds)
                        {
                            DBObject o; try { o = tr.GetObject(cid, OpenMode.ForRead); } catch { continue; }
                            var e = o as Entity;
                            if (e == null || o is CivSurface) continue;
                            ch.Add(new JsonObject { ["handle"] = cid.Handle.ToString(), ["type"] = o.GetType().Name, ["layer"] = e.Layer });
                        }
                        tr.Commit();
                    }
                    res["cloned_entities"] = ch;
                }
                catch { }
                if (GetBool(a, "drop_cloned_entities", false))
                {
                    int dropped = 0; var droppedTypes = new JsonObject();
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        foreach (ObjectId cid in clonedIds)
                        {
                            DBObject o; try { o = tr.GetObject(cid, OpenMode.ForRead); } catch { continue; }
                            if (!(o is Entity) || o is CivSurface || o is CivSite) continue;
                            string tn = o.GetType().Name;
                            o.UpgradeOpen(); o.Erase(); dropped++;
                            droppedTypes[tn] = (droppedTypes[tn] == null ? 0 : (int)droppedTypes[tn]) + 1;
                        }
                        tr.Commit();
                    }
                    res["dropped_entities"] = dropped; res["dropped_types"] = droppedTypes;
                }
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var after = new JsonArray();
                int fl = 0, gr = 0;
                foreach (ObjectId id in civ.GetSurfaceIds())
                {
                    var s = tr.GetObject(id, OpenMode.ForRead) as CivSurface;
                    if (s == null || beforeNames.Contains(s.Name)) continue;
                    var rec = new JsonObject { ["name"] = s.Name, ["handle"] = s.Handle.ToString(), ["layer"] = s.Layer };
                    try { rec["is_reference"] = s.IsReferenceObject; } catch { }
                    try { var gp = s.GetGeneralProperties(); rec["points"] = gp.NumberOfPoints; rec["elev_min"] = Math.Round(gp.MinimumElevation, 3); rec["elev_max"] = Math.Round(gp.MaximumElevation, 3); } catch (System.Exception ex) { rec["props_error"] = ex.Message; }
                    after.Add(rec);
                }
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    DBObject o; try { o = tr.GetObject(id, OpenMode.ForRead); } catch { continue; }
                    if (o is CivFeatureLine) fl++; else if (o is CivGrading) gr++;
                }
                res["surfaces_new"] = after;
                res["host_feature_lines_total"] = fl;
                res["host_gradings_total"] = gr;
                var sites = new JsonArray();
                try { foreach (ObjectId sid in civ.GetSiteIds()) { var st = (CivSite)tr.GetObject(sid, OpenMode.ForRead); sites.Add(st.Name); } } catch { }
                res["host_sites"] = sites;
                tr.Commit();
            }
            return res;
        }
    }
}
