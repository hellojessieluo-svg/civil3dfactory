using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivProfileView = Autodesk.Civil.DatabaseServices.ProfileView;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        // ---------------- create_surface_profile ----------------
        static JsonNode CreateSurfaceProfile(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string sfName = Need(a, "surface");
            string name = GetString(a, "name", alName + "_" + sfName);
            string style = GetString(a, "style", null);
            string labelSet = GetString(a, "label_set", null);
            string layer = GetString(a, "layer", null);
            bool replace = GetBool(a, "replace", true);

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = FindAlignment(tr, civ, alName);
                if (al == null) throw new InvalidOperationException("Alignment '" + alName + "'.");
                ObjectId sfId = FindSurfaceId(tr, civ, sfName);
                if (sfId.IsNull) throw new InvalidOperationException("Surface '" + sfName + "'.");

                int erased = 0;
                if (replace)
                {
                    foreach (ObjectId pid in al.GetProfileIds())
                    {
                        var p = tr.GetObject(pid, OpenMode.ForRead) as CivProfile;
                        if (p != null && p.Name == name)
                        {
                            p.UpgradeOpen(); p.Erase(); erased++;
                        }
                    }
                }

                ObjectId layerId = db.Clayer;
                if (!string.IsNullOrEmpty(layer))
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (lt.Has(layer)) layerId = lt[layer];
                    else
                    {
                        lt.UpgradeOpen();
                        var ltr = new LayerTableRecord { Name = layer };
                        layerId = lt.Add(ltr);
                        tr.AddNewlyCreatedDBObject(ltr, true);
                    }
                }
                ObjectId styleId = FindStyleId(tr, civ.Styles.ProfileStyles, style);
                ObjectId labelId = FindStyleId(tr, civ.Styles.LabelSetStyles.ProfileLabelSetStyles, labelSet);

                ObjectId pidNew;
                try
                {
                    pidNew = CivProfile.CreateFromSurface(name, al.ObjectId, sfId, layerId, styleId, labelId);
                }
                catch (Exception ex)
                {
                    tr.Commit();
                    return new JsonObject
                    {
                        ["alignment"] = alName, ["surface"] = sfName, ["created"] = false,
                        ["reason"] = ex.GetType().Name + ": " + ex.Message
                    };
                }
                var prof = (CivProfile)tr.GetObject(pidNew, OpenMode.ForRead);
                double s0 = double.NaN, s1 = double.NaN, e0 = double.NaN, e1 = double.NaN;
                try { s0 = prof.StartingStation; s1 = prof.EndingStation; } catch { }
                try { e0 = prof.ElevationMin; e1 = prof.ElevationMax; } catch { }
                double minLen = GetDouble(a, "min_length", 1.0);
                bool empty = prof.PVIs == null || double.IsNaN(s0) || s1 - s0 < minLen;
                if (empty && GetBool(a, "drop_empty", true))
                {
                    prof.UpgradeOpen(); prof.Erase();
                    tr.Commit();
                    return new JsonObject
                    {
                        ["alignment"] = alName, ["surface"] = sfName, ["created"] = false,
                        ["reason"] = "Surface does not intersect alignment; empty profile removed", ["replaced"] = erased
                    };
                }

                int viewsAdded = -1; var viewErr = new JsonArray();
                tr.Commit();
                return new JsonObject
                {
                    ["alignment"] = alName, ["surface"] = sfName, ["profile"] = name,
                    ["created"] = true, ["replaced"] = erased, ["empty"] = empty,
                    ["start_station"] = s0, ["end_station"] = s1,
                    ["elev_min"] = e0, ["elev_max"] = e1,
                    ["views_draw_set"] = viewsAdded, ["view_errors"] = viewErr
                };
            }
        }

        // ---------------- set_profile_view_range ----------------
        static JsonNode SetProfileViewRange(JsonObject a, Document doc)
        {
            string alName = GetString(a, "alignment", null);
            string viewName = GetString(a, "name", null);
            string contains = GetString(a, "contains", null);
            bool lastOnly = GetBool(a, "last_only", false);
            double stationStart = GetDouble(a, "station_start", double.NaN);
            double stationEnd = GetDouble(a, "station_end", double.NaN);
            bool endToAlignment = GetBool(a, "end_to_alignment", false);
            bool dryRun = GetBool(a, "dry_run", false);

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var changed = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var alIds = new List<ObjectId>();
                if (!string.IsNullOrEmpty(alName))
                {
                    CivAlignment al = FindAlignment(tr, civ, alName);
                    if (al == null) throw new InvalidOperationException("Alignment '" + alName + "'.");
                    alIds.Add(al.ObjectId);
                }
                else foreach (ObjectId id in civ.GetAlignmentIds()) alIds.Add(id);

                foreach (ObjectId alId in alIds)
                {
                    var al = (CivAlignment)tr.GetObject(alId, OpenMode.ForRead);
                    var views = new List<CivProfileView>();
                    foreach (ObjectId pvId in al.GetProfileViewIds())
                    {
                        var pv = (CivProfileView)tr.GetObject(pvId, OpenMode.ForRead);
                        if (!string.IsNullOrEmpty(viewName) && pv.Name != viewName) continue;
                        if (!string.IsNullOrEmpty(contains) && pv.Name.IndexOf(contains, StringComparison.Ordinal) < 0) continue;
                        views.Add(pv);
                    }
                    if (lastOnly && views.Count > 0)
                    {
                        CivProfileView last = views[0];
                        foreach (var v in views) if (v.StationStart > last.StationStart) last = v;
                        views = new List<CivProfileView> { last };
                    }
                    foreach (var pvRead in views)
                    {
                        var pv = (CivProfileView)tr.GetObject(pvRead.ObjectId, OpenMode.ForWrite);
                        double oldS0 = pv.StationStart, oldS1 = pv.StationEnd;
                        double s0 = double.IsNaN(stationStart) ? oldS0 : stationStart;
                        double s1 = endToAlignment ? al.EndingStation : (double.IsNaN(stationEnd) ? oldS1 : stationEnd);
                        s0 = Math.Max(al.StartingStation, s0);
                        s1 = Math.Min(al.EndingStation, s1);
                        var rec = new JsonObject
                        {
                            ["alignment"] = al.Name, ["view"] = pv.Name,
                            ["before"] = new JsonObject { ["start"] = oldS0, ["end"] = oldS1 },
                            ["after"] = new JsonObject { ["start"] = s0, ["end"] = s1 }
                        };
                        if (s1 <= s0) { rec["skipped"] = "Invalid range"; changed.Add(rec); continue; }
                        if (Math.Abs(s0 - oldS0) < 1e-6 && Math.Abs(s1 - oldS1) < 1e-6) { rec["skipped"] = "Range already matches"; changed.Add(rec); continue; }
                        if (!dryRun)
                        {
                            pv.StationRangeMode = Autodesk.Civil.DatabaseServices.StationRangeType.UserSpecified;
                            if (s1 > oldS1) { pv.StationEnd = s1; pv.StationStart = s0; }
                            else { pv.StationStart = s0; pv.StationEnd = s1; }
                        }
                        changed.Add(rec);
                    }
                }
                tr.Commit();
            }
            return new JsonObject { ["dry_run"] = dryRun, ["views"] = changed, ["count"] = changed.Count };
        }

        static bool IsAnnotative(Entity e)
        {
            try { var xd = e.GetXDataForApplication("AcadAnnotative"); return xd != null; }
            catch { return false; }
        }

        // ---------------- mleader_text_replace ----------------
        static JsonNode MLeaderTextReplace(JsonObject a, Document doc)
        {
            string find = Need(a, "find"); string repl = GetString(a, "replace", "");
            string where = GetString(a, "where", "model");
            bool dryRun = GetBool(a, "dry_run", false);
            var onlyHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var arrH = a["handles"] as JsonArray; if (arrH != null) foreach (var x in arrH) onlyHandles.Add(x.ToString());
            Database db = doc.Database; var hits = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (var host in TextHosts(db, tr, where))
                    foreach (ObjectId id in host.Value)
                    {
                        MLeader ml; try { ml = tr.GetObject(id, OpenMode.ForRead) as MLeader; } catch { continue; }
                        if (ml == null || ml.ContentType != ContentType.MTextContent) continue;
                        if (onlyHandles.Count > 0 && !onlyHandles.Contains(ml.Handle.ToString())) continue;
                        MText mt; try { mt = ml.MText; } catch { continue; }
                        if (mt == null || mt.Contents == null || mt.Contents.IndexOf(find, StringComparison.Ordinal) < 0) continue;
                        string nv = mt.Contents.Replace(find, repl);
                        hits.Add(new JsonObject { ["handle"] = ml.Handle.ToString(), ["layer"] = ml.Layer, ["from"] = mt.Contents, ["to"] = nv });
                        if (dryRun) continue;
                        ml.UpgradeOpen(); mt.Contents = nv; ml.MText = mt;
                    }
                tr.Commit();
            }
            return new JsonObject { ["dry_run"] = dryRun, ["replaced"] = hits.Count, ["items"] = hits, ["note"] = "Memory only; call save_dwg to persist." };
        }

        // ---------------- move_entities ----------------
        static JsonNode MoveEntities(JsonObject a, Document doc)
        {
            var arr = a["handles"] as JsonArray;
            var perItem = a["items"] as JsonArray;
            bool textOnly = GetBool(a, "text_only", false);
            if ((arr == null || arr.Count == 0) && (perItem == null || perItem.Count == 0)) throw new InvalidOperationException("Specify handles[] or items[].");
            double dx = GetDouble(a, "dx", 0), dy = GetDouble(a, "dy", 0);
            Database db = doc.Database; int n = 0; var missing = new JsonArray();
            if (perItem != null)
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (JsonNode it in perItem)
                    {
                        var io = it as JsonObject; if (io == null) continue;
                        string h = Need(io, "handle"); double ddx = GetDouble(io, "dx", 0), ddy = GetDouble(io, "dy", 0);
                        ObjectId id;
                        try { id = db.GetObjectId(false, new Handle(Convert.ToInt64(h, 16)), 0); } catch { missing.Add(h); continue; }
                        Entity e; try { e = tr.GetObject(id, OpenMode.ForWrite) as Entity; } catch { missing.Add(h); continue; }
                        if (e == null) { missing.Add(h); continue; }
                        var mlx = e as MLeader;
                        if (textOnly && mlx != null) { var p = mlx.TextLocation; mlx.TextLocation = new Point3d(p.X + ddx, p.Y + ddy, p.Z); }
                        else e.TransformBy(Matrix3d.Displacement(new Vector3d(ddx, ddy, 0)));
                        n++;
                    }
                    tr.Commit();
                }
                return new JsonObject { ["moved"] = n, ["missing"] = missing, ["note"] = "Memory only; call save_dwg to persist." };
            }
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (var h in arr)
                {
                    ObjectId id;
                    try { id = db.GetObjectId(false, new Handle(Convert.ToInt64(h.ToString(), 16)), 0); } catch { missing.Add(h.ToString()); continue; }
                    Entity e; try { e = tr.GetObject(id, OpenMode.ForWrite) as Entity; } catch { missing.Add(h.ToString()); continue; }
                    if (e == null) { missing.Add(h.ToString()); continue; }
                    e.TransformBy(Matrix3d.Displacement(new Vector3d(dx, dy, 0))); n++;
                }
                tr.Commit();
            }
            return new JsonObject { ["moved"] = n, ["dx"] = dx, ["dy"] = dy, ["missing"] = missing, ["note"] = "Memory only; call save_dwg to persist." };
        }

        // ---------------- import_entities ----------------
        static JsonNode ImportEntities(JsonObject a, Document doc)
        {
            string src = Need(a, "dwg");
            if (!File.Exists(src)) throw new InvalidOperationException("Source drawing does not exist: " + src);
            var wantHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var arrH = a["handles"] as JsonArray; if (arrH != null) foreach (var x in arrH) wantHandles.Add(x.ToString());
            var wantLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var arrL = a["layers"] as JsonArray; if (arrL != null) foreach (var x in arrL) wantLayers.Add(x.ToString());
            var types = new List<string>();
            var arrT = a["types"] as JsonArray; if (arrT != null) foreach (var x in arrT) types.Add(x.ToString());
            if (wantHandles.Count == 0 && wantLayers.Count == 0) throw new InvalidOperationException("Specify handles[] or layers[].");
            string toLayer = GetString(a, "to_layer", null);
            double annoScale = GetDouble(a, "anno_scale", 1.0);
            bool unanno = GetBool(a, "unannotative", false);
            bool dryRun = GetBool(a, "dry_run", false);
            Database db = doc.Database;
            var report = new JsonArray(); int pickedCount = 0;
            using (var sdb = new Database(false, true))
            {
                sdb.ReadDwgFile(src, FileOpenMode.OpenForReadAndAllShare, true, null);
                sdb.CloseInput(true);
                var picked = new ObjectIdCollection();
                using (Transaction str = sdb.TransactionManager.StartTransaction())
                {
                    var sbt = (BlockTable)str.GetObject(sdb.BlockTableId, OpenMode.ForRead);
                    var sms = (BlockTableRecord)str.GetObject(sbt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in sms)
                    {
                        Entity e; try { e = str.GetObject(id, OpenMode.ForRead) as Entity; } catch { continue; }
                        if (e == null) continue;
                        bool ok = wantHandles.Contains(e.Handle.ToString()) || wantLayers.Contains(e.Layer);
                        if (ok && types.Count > 0)
                        {
                            string tn = e.GetType().Name; bool hit = false;
                            foreach (var t in types) if (tn.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) hit = true;
                            if (!hit) ok = false;
                        }
                        if (ok) picked.Add(id);
                    }
                    str.Commit();
                }
                pickedCount = picked.Count;
                if (dryRun || picked.Count == 0)
                    return new JsonObject { ["dry_run"] = dryRun, ["picked"] = picked.Count, ["imported"] = 0 };
                ObjectId msId;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    msId = bt[BlockTableRecord.ModelSpace];
                    tr.Commit();
                }
                var map = new IdMapping();
                sdb.WblockCloneObjects(picked, msId, map, DuplicateRecordCloning.Ignore, false);
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (!string.IsNullOrEmpty(toLayer) && !lt.Has(toLayer))
                    {
                        lt.UpgradeOpen(); var ltr = new LayerTableRecord { Name = toLayer }; lt.Add(ltr); tr.AddNewlyCreatedDBObject(ltr, true);
                    }
                    foreach (ObjectId sid in picked)
                    {
                        var pair = map[sid]; if (!pair.IsCloned) continue;
                        Entity ne; try { ne = tr.GetObject(pair.Value, OpenMode.ForWrite) as Entity; } catch { continue; }
                        if (ne == null) continue;
                        if (!string.IsNullOrEmpty(toLayer)) ne.Layer = toLayer;
                        var o = new JsonObject { ["from"] = sid.Handle.ToString(), ["handle"] = ne.Handle.ToString(), ["type"] = ne.GetType().Name, ["layer"] = ne.Layer };
                        var mt = ne as MText; var dt = ne as DBText; var ml = ne as MLeader;
                        if (unanno)
                        {
                            try
                            {
                                if (mt != null) mt.Annotative = AnnotativeStates.False;
                                if (dt != null) dt.Annotative = AnnotativeStates.False;
                                if (ml != null) ml.Annotative = AnnotativeStates.False;
                                o["unannotative"] = true;
                            }
                            catch (System.Exception ex) { o["unanno_error"] = ex.Message; }
                        }
                        if (Math.Abs(annoScale - 1.0) > 1e-9)
                        {
                            try
                            {
                                if (mt != null) mt.TextHeight = mt.TextHeight * annoScale;
                                if (dt != null) dt.Height = dt.Height * annoScale;
                                if (ml != null)
                                {
                                    var mlt = ml.MText; if (mlt != null) { mlt.TextHeight = mlt.TextHeight * annoScale; ml.MText = mlt; }
                                    try { ml.ArrowSize = ml.ArrowSize * annoScale; } catch { }
                                    try { ml.DoglegLength = ml.DoglegLength * annoScale; } catch { }
                                    try { ml.LandingGap = ml.LandingGap * annoScale; } catch { }
                                }
                            }
                            catch (System.Exception ex) { o["scale_error"] = ex.Message; }
                        }
                        try
                        {
                            string text = null;
                            if (mt != null) text = mt.Text; if (dt != null) text = dt.TextString;
                            if (ml != null && ml.ContentType == ContentType.MTextContent && ml.MText != null) text = ml.MText.Text;
                            if (text != null) o["text"] = Regex.Replace(text, @"\\[A-Za-z][^;]*;|\{|\}", "");
                        }
                        catch { }
                        report.Add(o);
                    }
                    tr.Commit();
                }
            }
            return new JsonObject { ["picked"] = pickedCount, ["imported"] = report.Count, ["anno_scale"] = annoScale, ["items"] = report, ["note"] = "Memory only; call save_dwg to persist." };
        }

        // ---------------- mleader_styles / create_mleaders ----------------
        static ObjectId FindMLeaderStyle(Database db, Transaction tr, string name)
        {
            var dict = (DBDictionary)tr.GetObject(db.MLeaderStyleDictionaryId, OpenMode.ForRead);
            return dict.Contains(name) ? dict.GetAt(name) : ObjectId.Null;
        }
        static List<string> MLeaderStyleNames(Database db, Transaction tr)
        {
            var dict = (DBDictionary)tr.GetObject(db.MLeaderStyleDictionaryId, OpenMode.ForRead);
            var names = new List<string>(); foreach (DBDictionaryEntry e in dict) names.Add(e.Key); return names;
        }
        static JsonNode MLeaderStyles(JsonObject a, Document doc)
        {
            var o = new JsonObject(); Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction()) { o["host"] = new JsonArray(MLeaderStyleNames(db, tr).Select(n => (JsonNode)n).ToArray()); tr.Commit(); }
            string src = GetString(a, "dwg", null);
            if (!string.IsNullOrEmpty(src) && File.Exists(src))
                using (var sdb = new Database(false, true))
                {
                    sdb.ReadDwgFile(src, FileOpenMode.OpenForReadAndAllShare, true, null); sdb.CloseInput(true);
                    using (Transaction st = sdb.TransactionManager.StartTransaction()) { o["dwg"] = new JsonArray(MLeaderStyleNames(sdb, st).Select(n => (JsonNode)n).ToArray()); st.Commit(); }
                }
            return o;
        }
        static JsonNode CreateMLeaders(JsonObject a, Document doc)
        {
            var items = a["items"] as JsonArray; if (items == null || items.Count == 0) throw new InvalidOperationException("Specify items[].");
            string style = GetString(a, "style", null); string styleFrom = GetString(a, "style_from_dwg", null);
            string space = GetString(a, "space", "Model"); string layer = GetString(a, "layer", null);
            double th = GetDouble(a, "text_height", 0); bool dryRun = GetBool(a, "dry_run", false); bool unanno = GetBool(a, "unannotative", true);
            Database db = doc.Database; var report = new JsonArray(); string styleMethod = "none";
            ObjectId styleId = ObjectId.Null;
            if (!string.IsNullOrEmpty(style))
            {
                using (Transaction tr = db.TransactionManager.StartTransaction()) { styleId = FindMLeaderStyle(db, tr, style); tr.Commit(); }
                styleMethod = styleId.IsNull ? "missing" : "host";
                if (styleId.IsNull && !string.IsNullOrEmpty(styleFrom) && File.Exists(styleFrom))
                {
                    using (var sdb = new Database(false, true))
                    {
                        sdb.ReadDwgFile(styleFrom, FileOpenMode.OpenForReadAndAllShare, true, null); sdb.CloseInput(true);
                        ObjectId sid; using (Transaction st = sdb.TransactionManager.StartTransaction()) { sid = FindMLeaderStyle(sdb, st, style); st.Commit(); }
                        if (sid.IsNull) throw new InvalidOperationException("Source drawing also lacks multileader style " + style + ": " + styleFrom);
                        var ids = new ObjectIdCollection { sid }; var map = new IdMapping();
                        sdb.WblockCloneObjects(ids, db.MLeaderStyleDictionaryId, map, DuplicateRecordCloning.Ignore, false);
                        using (Transaction tr = db.TransactionManager.StartTransaction()) { styleId = FindMLeaderStyle(db, tr, style); tr.Commit(); }
                        styleMethod = styleId.IsNull ? "clone_failed" : "cloned";
                    }
                }
                if (styleId.IsNull) throw new InvalidOperationException("Multileader style does not exist: " + style + " (method=" + styleMethod + ")");
            }
            if (dryRun) return new JsonObject { ["dry_run"] = true, ["style_method"] = styleMethod, ["items"] = items.Count };
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                ObjectId hostId;
                if (space.Equals("Model", StringComparison.OrdinalIgnoreCase)) hostId = bt[BlockTableRecord.ModelSpace];
                else
                {
                    var ld = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                    if (!ld.Contains(space)) throw new InvalidOperationException("Layout not found: " + space);
                    var lay = (Layout)tr.GetObject(ld.GetAt(space), OpenMode.ForRead); hostId = lay.BlockTableRecordId;
                }
                var host = (BlockTableRecord)tr.GetObject(hostId, OpenMode.ForWrite);
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (JsonNode it in items)
                {
                    var io = it as JsonObject; if (io == null) continue;
                    var pts = io["points"] as JsonArray; if (pts == null || pts.Count < 2) throw new InvalidOperationException("Each points array needs at least two points (arrow to text).");
                    string text = Need(io, "text"); string ly = GetString(io, "layer", layer); double h = GetDouble(io, "text_height", th);
                    var ml = new MLeader(); ml.SetDatabaseDefaults();
                    if (!styleId.IsNull) ml.MLeaderStyle = styleId;
                    ml.ContentType = ContentType.MTextContent;
                    var mt = new MText(); mt.SetDatabaseDefaults(); mt.Contents = text;
                    if (!styleId.IsNull)
                    {
                        try { var ms = (MLeaderStyle)tr.GetObject(styleId, OpenMode.ForRead); if (!ms.TextStyleId.IsNull) mt.TextStyleId = ms.TextStyleId; } catch { }
                    }
                    if (h > 0) mt.TextHeight = h;
                    double wd = GetDouble(io, "width", GetDouble(a, "width", 0)); if (wd > 0) mt.Width = wd;
                    var last = pts[pts.Count - 1] as JsonArray;
                    mt.Location = new Point3d((double)last[0], (double)last[1], 0);
                    ml.MText = mt;
                    int li = ml.AddLeaderLine(new Point3d((double)((JsonArray)pts[0])[0], (double)((JsonArray)pts[0])[1], 0));
                    for (int k = 1; k < pts.Count - 1; k++) { var q = pts[k] as JsonArray; ml.AddLastVertex(li, new Point3d((double)q[0], (double)q[1], 0)); }
                    var ta = io["text_at"] as JsonArray;
                    if (ta != null)
                    {
                        var q = pts[pts.Count - 1] as JsonArray; ml.AddLastVertex(li, new Point3d((double)q[0], (double)q[1], 0));
                        ml.TextLocation = new Point3d((double)ta[0], (double)ta[1], 0);
                    }
                    else ml.TextLocation = new Point3d((double)last[0], (double)last[1], 0);
                    if (unanno) { try { ml.Annotative = AnnotativeStates.False; } catch { } }
                    if (h > 0) { try { ml.TextHeight = h; } catch { } }
                    if (!string.IsNullOrEmpty(ly) && lt.Has(ly)) ml.Layer = ly;
                    host.AppendEntity(ml); tr.AddNewlyCreatedDBObject(ml, true);
                    report.Add(new JsonObject { ["handle"] = ml.Handle.ToString(), ["text"] = text });
                }
                tr.Commit();
            }
            return new JsonObject { ["created"] = report.Count, ["style_method"] = styleMethod, ["items"] = report, ["note"] = "Memory only; call save_dwg to persist." };
        }

        // ---------------- add_profile_view_labels ----------------
        static JsonNode AddProfileViewLabels(JsonObject a, Document doc)
        {
            var items = a["items"] as JsonArray; if (items == null || items.Count == 0) throw new InvalidOperationException("Specify items[].");
            string layer = GetString(a, "layer", null);
            Database db = doc.Database; var civ = Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(db);
            var report = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var pvMap = new Dictionary<string, ObjectId>();
                var btA = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var msA = (BlockTableRecord)tr.GetObject(btA[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId vid in msA)
                {
                    if (vid.ObjectClass.Name != "AeccDbGraphProfile") continue;
                    var pv = tr.GetObject(vid, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.ProfileView;
                    if (pv != null) pvMap[pv.Name] = vid;
                }
                ObjectId markerId = ObjectId.Null;
                try { foreach (ObjectId mid in civ.Styles.MarkerStyles) { markerId = mid; break; } } catch { }
                if (!string.IsNullOrEmpty(layer))
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (!lt.Has(layer)) { lt.UpgradeOpen(); var rec = new LayerTableRecord { Name = layer }; lt.Add(rec); tr.AddNewlyCreatedDBObject(rec, true); }
                }
                int idx = -1;
                foreach (JsonNode it in items)
                {
                    idx++;
                    var io = it as JsonObject; if (io == null) continue;
                    try {
                    string pvName = Need(io, "profile_view"); string type = GetString(io, "type", "station_elevation"); string style = Need(io, "style");
                    if (!pvMap.ContainsKey(pvName)) throw new InvalidOperationException("Profile view not found: " + pvName);
                    ObjectId pvId = pvMap[pvName];
                    double st = GetDouble(io, "station", double.NaN), el = GetDouble(io, "elevation", double.NaN);
                    ObjectId lid; ObjectId styleId = ObjectId.Null;
                    if (type == "depth")
                    {
                        double st2 = GetDouble(io, "station2", double.NaN), el2 = GetDouble(io, "elevation2", double.NaN);
                        try { styleId = civ.Styles.LabelStyles.ProfileViewLabelStyles.DepthLabelStyles[style]; } catch { }
                        if (styleId.IsNull) throw new InvalidOperationException("Two-point slope label style not found: " + style);
                        var pvd = (Autodesk.Civil.DatabaseServices.ProfileView)tr.GetObject(pvId, OpenMode.ForRead);
                        double ax = 0, ay = 0, bx = 0, by = 0; pvd.FindXYAtStationAndElevation(st, el, ref ax, ref ay); pvd.FindXYAtStationAndElevation(st2, el2, ref bx, ref by);
                        lid = Autodesk.Civil.DatabaseServices.ProfileViewDepthLabel.Create(pvId, styleId, new Point2d(ax, ay), new Point2d(bx, by));
                    }
                    else
                    {
                        try { styleId = civ.Styles.LabelStyles.ProfileViewLabelStyles.StationElevationLabelStyles[style]; } catch { }
                        if (styleId.IsNull) throw new InvalidOperationException("Station-elevation label style not found: " + style);
                        lid = Autodesk.Civil.DatabaseServices.StationElevationLabel.Create(pvId, styleId, markerId, st, el);
                    }
                    var lbl = (Autodesk.Civil.DatabaseServices.Label)tr.GetObject(lid, OpenMode.ForWrite);
                    if (!string.IsNullOrEmpty(layer)) lbl.Layer = layer;
                    string txt = GetString(io, "text", null);
                    var o = new JsonObject { ["handle"] = lbl.Handle.ToString(), ["type"] = type, ["profile_view"] = pvName };
                    if (!string.IsNullOrEmpty(txt))
                    {
                        var ls = (Autodesk.Civil.DatabaseServices.Styles.LabelStyle)tr.GetObject(styleId, OpenMode.ForRead);
                        var comps = ls.GetComponents(Autodesk.Civil.DatabaseServices.Styles.LabelStyleComponentType.Text);
                        int n = 0; foreach (ObjectId cid in comps) { try { lbl.SetTextComponentOverride(cid, txt); n++; } catch (System.Exception ex) { o["override_error"] = ex.Message; } }
                        o["text_overridden"] = n;
                    }
                    double dx = GetDouble(io, "dx", 0), dy = GetDouble(io, "dy", 0);
                    if (dx != 0 || dy != 0)
                    {
                        try { lbl.DraggedOffset = new Vector3d(dx, dy, 0); o["dragged"] = true; } catch (System.Exception ex) { o["drag_error"] = ex.Message; }
                    }
                    report.Add(o);
                    } catch (System.Exception ex) { report.Add(new JsonObject { ["index"] = idx, ["error"] = ex.GetType().Name + ": " + ex.Message, ["stack"] = (ex.StackTrace ?? "").Substring(0, Math.Min(300, (ex.StackTrace ?? "").Length)) }); }
                }
                tr.Commit();
            }
            return new JsonObject { ["created"] = report.Count, ["items"] = report, ["note"] = "Memory only; call save_dwg to persist." };
        }

        // ---------------- layouts_rename ----------------
        static JsonNode LayoutsRename(JsonObject a, Document doc)
        {
            var items = a["rename"] as JsonArray;
            if (items == null || items.Count == 0) throw new InvalidOperationException("Specify rename:[{from,to}].");
            Database db = doc.Database;
            var done = new JsonArray(); var failed = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (JsonNode n in items)
                {
                    var it = n as JsonObject; if (it == null) continue;
                    string from = Need(it, "from"), to = Need(it, "to");
                    if (!dict.Contains(from)) { failed.Add(new JsonObject { ["from"] = from, ["why"] = "Layout does not exist" }); continue; }
                    if (dict.Contains(to)) { failed.Add(new JsonObject { ["from"] = from, ["why"] = "Target name already exists: " + to }); continue; }
                    try
                    {
                        LayoutManager.Current.RenameLayout(from, to);
                        done.Add(from + " → " + to);
                    }
                    catch (System.Exception ex) { failed.Add(new JsonObject { ["from"] = from, ["why"] = ex.Message }); }
                }
                tr.Commit();
            }
            return new JsonObject { ["renamed"] = done, ["failed"] = failed, ["note"] = "Memory only; call save_dwg to persist." };
        }

        // ---------------- entities_locate ----------------
        static JsonNode EntitiesLocate(JsonObject a, Document doc)
        {
            string layer = GetString(a, "layer", null);
            string type = GetString(a, "type", null);
            string where = GetString(a, "where", "model");
            int max = (int)GetDouble(a, "max", 5000);
            Database db = doc.Database;
            var arr = new JsonArray();
            int scanned = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (var host in TextHosts(db, tr, where))
                {
                    foreach (ObjectId id in host.Value)
                    {
                        Entity e; try { e = tr.GetObject(id, OpenMode.ForRead) as Entity; } catch { continue; }
                        if (e == null) continue;
                        scanned++;
                        if (!string.IsNullOrEmpty(layer) && !string.Equals(e.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
                        string tn = e.GetType().Name;
                        if (!string.IsNullOrEmpty(type) && tn.IndexOf(type, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        string text = null;
                        var t = e as DBText; if (t != null) text = t.TextString;
                        var m = e as MText; if (m != null) text = m.Text;
                        var ml = e as MLeader;
                        if (ml != null)
                        {
                            try { if (ml.ContentType == ContentType.MTextContent && ml.MText != null) text = ml.MText.Text; } catch { }
                            if (text == null) text = "<" + ml.ContentType + ">";
                        }
                        JsonObject mlInfo = null;
                        if (ml != null)
                        {
                            mlInfo = new JsonObject();
                            try
                            {
                                if (ml.ContentType == ContentType.MTextContent && ml.MText != null)
                                {
                                    var mt = ml.MText; var loc = ml.TextLocation;
                                    mlInfo["text_x"] = loc.X; mlInfo["text_y"] = loc.Y;
                                    mlInfo["text_w"] = mt.ActualWidth; mlInfo["text_h"] = mt.ActualHeight;
                                    mlInfo["text_height"] = mt.TextHeight;
                                }
                            }
                            catch { }
                            try
                            {
                                var lines = new JsonArray();
                                foreach (int li in ml.GetLeaderIndexes())
                                    foreach (int lli in ml.GetLeaderLineIndexes(li))
                                    {
                                        var seg = new JsonArray(); int nv = ml.VerticesCount(lli);
                                        for (int vi = 0; vi < nv; vi++) { var v = ml.GetVertex(lli, vi); seg.Add(new JsonArray { v.X, v.Y }); }
                                        try { var cp = ml.GetLastVertex(lli); } catch { }
                                        lines.Add(seg);
                                    }
                                mlInfo["leader_lines"] = lines;
                            }
                            catch { }
                        }
                        if (text != null) text = Regex.Replace(text, @"\\[A-Za-z][^;]*;|\{|\}", "");
                        double minx = 0, miny = 0, maxx = 0, maxy = 0; bool hasExt = false;
                        try { var ex = e.GeometricExtents; minx = ex.MinPoint.X; miny = ex.MinPoint.Y; maxx = ex.MaxPoint.X; maxy = ex.MaxPoint.Y; hasExt = true; } catch { }
                        var o = new JsonObject
                        {
                            ["host"] = host.Key, ["type"] = tn, ["handle"] = e.Handle.ToString(), ["layer"] = e.Layer,
                            ["color"] = e.Color.IsByLayer ? "ByLayer" : e.Color.ColorIndex.ToString(),
                            ["lineweight"] = e.LineWeight.ToString()
                        };
                        if (hasExt) { o["minx"] = minx; o["miny"] = miny; o["maxx"] = maxx; o["maxy"] = maxy; }
                        if (text != null) o["text"] = text;
                        o["annotative"] = IsAnnotative(e);
                        if (mlInfo != null) foreach (var kv in mlInfo) o[kv.Key] = kv.Value != null ? JsonNode.Parse(kv.Value.ToJsonString()) : null;
                        var pl = e as Polyline; if (pl != null) { o["closed"] = pl.Closed; o["length"] = Math.Round(pl.Length, 3); o["vertices"] = pl.NumberOfVertices;
                            if (GetBool(a, "geometry", false)) { var va = new JsonArray(); for (int vi = 0; vi < pl.NumberOfVertices; vi++) { var q = pl.GetPoint2dAt(vi); va.Add(new JsonArray(Math.Round(q.X, 4), Math.Round(q.Y, 4))); } o["points"] = va; } }
                        var ln = e as Line; if (ln != null) { o["x1"] = Math.Round(ln.StartPoint.X, 4); o["y1"] = Math.Round(ln.StartPoint.Y, 4); o["x2"] = Math.Round(ln.EndPoint.X, 4); o["y2"] = Math.Round(ln.EndPoint.Y, 4); }
                        var hz = e as Hatch;
                        if (hz != null)
                        {
                            o["pattern"] = hz.PatternName;
                            try { o["area"] = Math.Round(hz.Area, 2); }
                            catch
                            {
                                try
                                {
                                    double outer = 0, inner = 0;
                                    for (int li = 0; li < hz.NumberOfLoops; li++)
                                    {
                                        var loop = hz.GetLoopAt(li); double s = 0;
                                        if (loop.IsPolyline)
                                        {
                                            var bv = loop.Polyline; int n = bv.Count;
                                            for (int i = 0; i < n; i++) { var p = bv[i].Vertex; var q = bv[(i + 1) % n].Vertex; s += p.X * q.Y - q.X * p.Y; }
                                        }
                                        else
                                        {
                                            var pts = new List<Point2d>();
                                            foreach (Curve2d cv in loop.Curves) { var sp = cv.GetSamplePoints(16); foreach (var sp1 in sp) pts.Add(sp1); }
                                            for (int i = 0; i < pts.Count; i++) { var p = pts[i]; var q = pts[(i + 1) % pts.Count]; s += p.X * q.Y - q.X * p.Y; }
                                        }
                                        s = Math.Abs(s) / 2;
                                        if ((loop.LoopType & HatchLoopTypes.Outermost) != 0 || li == 0) outer += s; else inner += s;
                                    }
                                    o["area"] = Math.Round(outer - inner, 2); o["area_from"] = "loops";
                                }
                                catch (System.Exception ex2) { o["area_error"] = ex2.Message; }
                            }
                        }
                        arr.Add(o);
                        if (arr.Count >= max) break;
                    }
                    if (arr.Count >= max) break;
                }
                tr.Commit();
            }
            return new JsonObject { ["scanned"] = scanned, ["matched"] = arr.Count, ["items"] = arr };
        }

        // ---------------- texts_locate ----------------
        static JsonNode TextsLocate(JsonObject a, Document doc)
        {
            string contains = GetString(a, "contains", null);
            string pattern = GetString(a, "regex", null);
            string where = GetString(a, "where", "all");
            int max = (int)GetDouble(a, "max", 5000);
            Regex re = string.IsNullOrEmpty(pattern) ? null : new Regex(pattern);
            Database db = doc.Database;
            var arr = new JsonArray();
            int scanned = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (var host in TextHosts(db, tr, where))
                {
                    foreach (ObjectId id in host.Value)
                    {
                        DBObject o; try { o = tr.GetObject(id, OpenMode.ForRead); } catch { continue; }
                        string text = null; Point3d pt = Point3d.Origin; double h = 0; string kind = null;
                        var t = o as DBText;
                        if (t != null) { text = t.TextString; pt = t.Position; h = t.Height; kind = "DBText"; }
                        var m = o as MText;
                        if (m != null) { text = m.Text; pt = m.Location; h = m.TextHeight; kind = "MText"; }
                        if (text == null) continue;
                        scanned++;
                        string plain = Regex.Replace(text, @"\\[A-Za-z][^;]*;|\{|\}", "");
                        if (!string.IsNullOrEmpty(contains) && plain.IndexOf(contains, StringComparison.Ordinal) < 0) continue;
                        if (re != null && !re.IsMatch(plain)) continue;
                        Extents3d ex; double minx = pt.X, miny = pt.Y, maxx = pt.X, maxy = pt.Y;
                        try { ex = ((Entity)o).GeometricExtents; minx = ex.MinPoint.X; miny = ex.MinPoint.Y; maxx = ex.MaxPoint.X; maxy = ex.MaxPoint.Y; } catch { }
                        arr.Add(new JsonObject
                        {
                            ["host"] = host.Key, ["type"] = kind, ["handle"] = o.Handle.ToString(),
                            ["layer"] = ((Entity)o).Layer, ["text"] = plain, ["height"] = h,
                            ["x"] = pt.X, ["y"] = pt.Y,
                            ["minx"] = minx, ["miny"] = miny, ["maxx"] = maxx, ["maxy"] = maxy
                        });
                        if (arr.Count >= max) break;
                    }
                    if (arr.Count >= max) break;
                }
                tr.Commit();
            }
            return new JsonObject { ["scanned"] = scanned, ["matched"] = arr.Count, ["items"] = arr };
        }

        // ---------------- paste_window_to_layout ----------------
        static JsonNode PasteWindowToLayout(JsonObject a, Document doc)
        {
            string sourcePath = Need(a, "dwg");
            string layoutName = GetString(a, "layout", "Layout1");
            var items = a["items"] as JsonArray;
            if (items == null || items.Count == 0) throw new InvalidOperationException("items[] is required.");
            string keepBlockContains = GetString(a, "keep_block_contains", "TITLE");
            string pickDefault = GetString(a, "pick", null);
            string clearPickDefault = GetString(a, "clear_pick", "center").ToLowerInvariant();
            bool keepEnclosingDefault = GetBool(a, "keep_enclosing", true);
            var excludedLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var exArr = a["exclude_layers"] as JsonArray;
            if (exArr != null) foreach (var n in exArr) if (n != null) excludedLayers.Add(n.ToString());
            if (!File.Exists(sourcePath)) throw new InvalidOperationException("Source DWG not found: " + sourcePath);

            Database db = doc.Database;
            var lm = LayoutManager.Current;
            ObjectId layoutId = lm.GetLayoutId(layoutName);
            if (layoutId.IsNull) throw new InvalidOperationException("Layout '" + layoutName + "'.");

            var report = new JsonArray();
            int totalCloned = 0, totalErased = 0, eraseFailed = 0, transformFailed = 0; string firstErr = null;
            using (var src = new Database(false, true))
            {
                src.ReadDwgFile(sourcePath, FileOpenMode.OpenForReadAndAllShare, true, null);
                src.CloseInput(true);
                using (Transaction tr = db.TransactionManager.StartTransaction())
                using (Transaction stx = src.TransactionManager.StartTransaction())
                {
                    var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                    var paper = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
                    var sbt = (BlockTable)stx.GetObject(src.BlockTableId, OpenMode.ForRead);
                    var sms = (BlockTableRecord)stx.GetObject(sbt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    var srcExt = new List<KeyValuePair<ObjectId, Extents3d>>();
                    foreach (ObjectId id in sms)
                    {
                        var ent = stx.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null || ent.IsErased) continue;
                        if (excludedLayers.Contains(ent.Layer)) continue;
                        try { srcExt.Add(new KeyValuePair<ObjectId, Extents3d>(id, ent.GeometricExtents)); } catch { }
                    }

                    foreach (JsonNode n in items)
                    {
                        var it = n as JsonObject; if (it == null) continue;
                        var w = it["window"] as JsonObject;
                        if (w == null) throw new InvalidOperationException("Each items[] entry requires window.");
                        double wx0 = GetDouble(w, "minx", 0), wy0 = GetDouble(w, "miny", 0);
                        double wx1 = GetDouble(w, "maxx", 0), wy1 = GetDouble(w, "maxy", 0);
                        bool crossing = GetBool(it, "crossing", false);
                        string pick = (GetString(it, "pick", pickDefault) ?? (crossing ? "crossing" : "inside")).ToLowerInvariant();
                        if (pick != "inside" && pick != "center" && pick != "crossing") throw new InvalidOperationException("pick must be inside, center or crossing.");
                        string clearPick = GetString(it, "clear_pick", clearPickDefault).ToLowerInvariant();
                        if (clearPick != "inside" && clearPick != "center" && clearPick != "crossing") throw new InvalidOperationException("clear_pick must be inside, center or crossing.");
                        bool keepEnclosing = GetBool(it, "keep_enclosing", keepEnclosingDefault);
                        double scale = GetDouble(it, "scale", 1.0);
                        var to = it["to"] as JsonObject;
                        if (to == null) throw new InvalidOperationException("Each items[] entry requires to{x,y}.");
                        double tx = GetDouble(to, "x", 0), ty = GetDouble(to, "y", 0);
                        string tag = GetString(it, "tag", "");

                        int erased = 0, keptEnclosing = 0, clearStraddleKept = 0;
                        var clr = it["clear"] as JsonObject;
                        if (clr != null)
                        {
                            double cx0 = GetDouble(clr, "minx", 0), cy0 = GetDouble(clr, "miny", 0);
                            double cx1 = GetDouble(clr, "maxx", 0), cy1 = GetDouble(clr, "maxy", 0);
                            var toErase = new List<ObjectId>();
                            foreach (ObjectId id in paper)
                            {
                                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                                if (ent == null || ent.IsErased) continue;
                                if (ent is Autodesk.AutoCAD.DatabaseServices.Viewport) continue;
                                var br = ent as BlockReference;
                                if (br != null)
                                {
                                    string bn = "";
                                    try { bn = ((BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead)).Name; } catch { }
                                    if (!string.IsNullOrEmpty(keepBlockContains) && bn.IndexOf(keepBlockContains, StringComparison.Ordinal) >= 0) continue;
                                }
                                Extents3d ex;
                                try { ex = ent.GeometricExtents; } catch { continue; }
                                double ex0 = ex.MinPoint.X, ey0 = ex.MinPoint.Y, ex1 = ex.MaxPoint.X, ey1 = ex.MaxPoint.Y;
                                bool touches = !(ex1 < cx0 || ex0 > cx1 || ey1 < cy0 || ey0 > cy1);
                                if (!touches) continue;
                                if (keepEnclosing && ex0 <= cx0 && ey0 <= cy0 && ex1 >= cx1 && ey1 >= cy1) { keptEnclosing++; continue; }
                                bool allIn = ex0 >= cx0 && ex1 <= cx1 && ey0 >= cy0 && ey1 <= cy1;
                                double cx = (ex0 + ex1) / 2, cy = (ey0 + ey1) / 2;
                                bool centerIn = cx >= cx0 && cx <= cx1 && cy >= cy0 && cy <= cy1;
                                bool take = clearPick == "inside" ? allIn : clearPick == "crossing" ? true : centerIn;
                                if (take) toErase.Add(id); else clearStraddleKept++;
                            }
                            foreach (ObjectId id in toErase)
                            {
                                try
                                {
                                    var ent = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                                    ent.Erase(); erased++;
                                }
                                catch (Exception ex) { eraseFailed++; if (firstErr == null) firstErr = "erase " + id.ObjectClass.Name + ": " + ex.Message; }
                            }
                        }

                        var ids = new ObjectIdCollection();
                        int straddling = 0; var straddleSample = new JsonArray();
                        foreach (var kv in srcExt)
                        {
                            Extents3d ex = kv.Value;
                            bool inside = ex.MinPoint.X >= wx0 && ex.MaxPoint.X <= wx1 && ex.MinPoint.Y >= wy0 && ex.MaxPoint.Y <= wy1;
                            bool intersects = !(ex.MaxPoint.X < wx0 || ex.MinPoint.X > wx1 || ex.MaxPoint.Y < wy0 || ex.MinPoint.Y > wy1);
                            double mx = (ex.MinPoint.X + ex.MaxPoint.X) / 2, my = (ex.MinPoint.Y + ex.MaxPoint.Y) / 2;
                            bool centerIn = mx >= wx0 && mx <= wx1 && my >= wy0 && my <= wy1;
                            bool take = pick == "crossing" ? intersects : pick == "center" ? (inside || (intersects && centerIn)) : inside;
                            if (take) ids.Add(kv.Key);
                            else if (intersects)
                            {
                                straddling++;
                                if (straddleSample.Count < 8)
                                {
                                    var se = stx.GetObject(kv.Key, OpenMode.ForRead) as Entity;
                                    string txt = se is DBText ? ((DBText)se).TextString : se is MText ? ((MText)se).Text : null;
                                    straddleSample.Add(new JsonObject { ["handle"] = kv.Key.Handle.ToString(), ["type"] = se == null ? "" : se.GetType().Name, ["layer"] = se == null ? "" : se.Layer, ["text"] = txt == null ? null : (txt.Length > 20 ? txt.Substring(0, 20) : txt), ["center_in"] = centerIn });
                                }
                            }
                        }
                        int cloned = 0;
                        if (ids.Count > 0)
                        {
                            var map = new IdMapping();
                            src.WblockCloneObjects(ids, paper.ObjectId, map, DuplicateRecordCloning.Ignore, false);
                            var sourceOrigin = new Point3d(wx0, wy0, 0);
                            var targetOrigin = new Point3d(tx, ty, 0);
                            foreach (IdPair pair in map)
                            {
                                if (!pair.IsCloned || pair.Value.IsNull) continue;
                                var ent = tr.GetObject(pair.Value, OpenMode.ForWrite, false) as Entity;
                                if (ent == null || ent.IsErased) continue;
                                try
                                {
                                    ent.TransformBy(Matrix3d.Displacement(Point3d.Origin - sourceOrigin));
                                    ent.TransformBy(Matrix3d.Scaling(scale, Point3d.Origin));
                                    ent.TransformBy(Matrix3d.Displacement(targetOrigin - Point3d.Origin));
                                    cloned++;
                                }
                                catch (Exception ex)
                                {
                                    transformFailed++; if (firstErr == null) firstErr = "transform " + ent.GetType().Name + ": " + ex.Message;
                                    try { ent.Erase(); } catch { }
                                }
                            }
                        }
                        totalCloned += cloned; totalErased += erased;
                        report.Add(new JsonObject
                        {
                            ["tag"] = tag, ["source_entities"] = ids.Count, ["cloned"] = cloned, ["erased"] = erased,
                            ["pick"] = pick, ["straddling"] = straddling, ["straddling_sample"] = straddleSample,
                            ["clear_pick"] = clr == null ? null : clearPick, ["kept_enclosing"] = keptEnclosing, ["clear_straddling_kept"] = clearStraddleKept,
                            ["scale"] = scale, ["to"] = new JsonObject { ["x"] = tx, ["y"] = ty }
                        });
                    }
                    stx.Commit();
                    tr.Commit();
                }
            }
            return new JsonObject
            {
                ["dwg"] = sourcePath, ["layout"] = layoutName, ["items"] = report,
                ["total_cloned"] = totalCloned, ["total_erased"] = totalErased, ["erase_failed"] = eraseFailed, ["transform_failed"] = transformFailed, ["first_error"] = firstErr,
                ["note"] = "Memory only; call save_dwg to persist."
            };
        }
    }
}
