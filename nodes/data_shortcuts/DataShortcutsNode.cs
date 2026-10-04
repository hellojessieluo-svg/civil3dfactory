using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using DataShortcuts = Civil3DFactory.ShortcutApi;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        static System.Reflection.MethodInfo FindMethod(object o, string contains)
        {
            foreach (var m in o.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                if (m.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0) return m;
            return null;
        }
        static IEnumerable<string> MethodNames(object o)
        {
            foreach (var m in o.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                yield return m.Name + "(" + m.GetParameters().Length + ")";
        }

        static JsonNode RunNodeDataShortcuts(JsonObject a, Document doc)
        {
            string action = (GetString(a, "action", "list") ?? "list").Trim().ToLowerInvariant();
            var res = new JsonObject { ["action"] = action };
            string wf = GetString(a, "working_folder", null);
            string project = GetString(a, "project", null);

            if (!string.IsNullOrEmpty(wf))
            {
                Directory.CreateDirectory(wf);
                DataShortcuts.SetWorkingFolder(wf);
            }
            if (!string.IsNullOrEmpty(project))
            {
                string cur = DataShortcuts.GetWorkingFolder();
                string projDir = Path.Combine(cur ?? "", project);
                if (!Directory.Exists(Path.Combine(projDir, "_Shortcuts")))
                {
                    DataShortcuts.CreateProjectFolder(project, GetString(a, "description", "civil3dfactory data_shortcuts"), null, true);
                    res["project_created"] = true;
                }
                else DataShortcuts.SetCurrentProjectFolder(project);
            }
            try { res["working_folder"] = DataShortcuts.GetWorkingFolder(); } catch (System.Exception ex) { res["working_folder_error"] = ex.Message; }
            try { res["current_project"] = DataShortcuts.GetCurrentProjectFolder(); } catch (System.Exception ex) { res["current_project_error"] = ex.Message; }
            if (action == "setup")
            {
                if (GetBool(a, "reflect", false))
                {
                    var sm = new JsonArray();
                    foreach (var m in DataShortcuts.HostType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
                    { var ps = new List<string>(); foreach (var pp in m.GetParameters()) ps.Add(pp.ParameterType.Name + " " + pp.Name); sm.Add(m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", ps) + ")"); }
                    res["static_methods"] = sm;
                    var ty = new JsonArray();
                    foreach (var t in DataShortcuts.HostType.Assembly.GetExportedTypes()) ty.Add(t.FullName);
                    res["assembly_types"] = ty;
                    var nested = new JsonArray();
                    foreach (var t in DataShortcuts.HostType.GetNestedTypes()) { var ms = new List<string>(); foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)) { var ps = new List<string>(); foreach (var pp in m.GetParameters()) ps.Add(pp.ParameterType.Name + " " + pp.Name); ms.Add(m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", ps) + ")"); } nested.Add(new JsonObject { ["type"] = t.Name, ["methods"] = string.Join(" | ", ms) }); }
                    res["nested_types"] = nested;
                }
                return res;
            }

            if (action == "repair")
            {
                string target = GetString(a, "target", null);
                if (string.IsNullOrEmpty(target) || !Path.IsPathRooted(target) || !File.Exists(target))
                    throw new InvalidOperationException("repair requires target: an existing absolute source drawing path; received: " + (target ?? "(empty)"));
                var want = new List<string>();
                if (a["surfaces"] is JsonArray sa) foreach (JsonNode n in sa) if (n != null) want.Add(n.ToString());
                if (want.Count == 0) throw new InvalidOperationException("repair requires surfaces: reference surface names to repair");
                bool autoOther = GetBool(a, "auto_repair_other", false);
                Database db = doc.Database;
                CivDoc civ = CivDoc.GetCivilDocument(db);
                var ids = new List<KeyValuePair<string, ObjectId>>();
                var repaired = new JsonArray(); var failed = new JsonArray();
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (string nm in want)
                    {
                        ObjectId id = FindSurfaceId(tr, civ, nm);
                        if (id.IsNull) failed.Add(new JsonObject { ["name"] = nm, ["why"] = "Surface not found" });
                        else ids.Add(new KeyValuePair<string, ObjectId>(nm, id));
                    }
                    tr.Commit();
                }
                foreach (var kv in ids)
                {
                    try
                    {
                        if (DataShortcuts.RepairBrokenDRef(kv.Value, target, autoOther)) repaired.Add(kv.Key);
                        else failed.Add(new JsonObject { ["name"] = kv.Key, ["why"] = "RepairBrokenDRef returned false (reference is not broken or target lacks a matching object)" });
                    }
                    catch (System.Exception ex) { failed.Add(new JsonObject { ["name"] = kv.Key, ["why"] = ex.GetType().Name + ": " + ex.Message }); }
                }
                res["target"] = target; res["repaired"] = repaired; res["failed"] = failed;
                if (failed.Count > 0) throw new InvalidOperationException("repair: " + failed.Count + " references could not be repaired: " + failed.ToJsonString());
                return res;
            }

            var names = new List<string>(); var arr = a["surfaces"] as JsonArray;
            if (arr != null) foreach (JsonNode n in arr) names.Add(n.ToString());

            bool valid = false;
            using (DataShortcuts.DataShortcutManager mgr = DataShortcuts.CreateDataShortcutManager(ref valid))
            {
                res["manager_valid"] = valid;
                if (!valid) throw new InvalidOperationException("CreateDataShortcutManager is invalid; check working folder and project");

                if (action == "list")
                {
                    var exp = new JsonArray();
                    int ne = mgr.GetExportableItemsCount();
                    for (int i = 0; i < ne; i++) { var it = mgr.GetExportableItemAt(i); exp.Add(new JsonObject { ["index"] = i, ["name"] = it.Name, ["type"] = it.DSEntityType.ToString() }); }
                    var pub = new JsonArray();
                    try
                    {
                        int np = mgr.GetPublishedItemsCount();
                        for (int i = 0; i < np; i++) { var it = mgr.GetPublishedItemAt(i); pub.Add(new JsonObject { ["index"] = i, ["name"] = it.Name, ["type"] = it.DSEntityType.ToString(), ["broken"] = it.IsBroken, ["source"] = it.SourceLocation }); }
                    }
                    catch (System.Exception ex) { res["published_error"] = ex.Message; }
                    res["exportable"] = exp; res["published"] = pub;
                    var mn = new JsonArray(); foreach (string m in MethodNames(mgr.Value)) mn.Add(m); res["manager_methods"] = mn;
                    return res;
                }
                if (action == "publish")
                {
                    if (string.IsNullOrEmpty(doc.Name) || !File.Exists(doc.Name)) throw new InvalidOperationException("Save the current drawing before publishing: " + doc.Name);
                    var picked = new JsonArray(); var missing = new JsonArray();
                    int ne = mgr.GetExportableItemsCount();
                    var seen = new HashSet<string>();
                    for (int i = 0; i < ne; i++)
                    {
                        var it = mgr.GetExportableItemAt(i);
                        bool isSurf = it.DSEntityType == "Surface";
                        bool hit = isSurf && (names.Count == 0 || names.Contains(it.Name));
                        mgr.SetSelectItemAtIndex(i, hit);
                        if (hit) { picked.Add(it.Name); seen.Add(it.Name); }
                    }
                    foreach (string n in names) if (!seen.Contains(n)) missing.Add(n);
                    if (picked.Count == 0) throw new InvalidOperationException("No publishable surfaces selected; check surface names");
                    var mref = mgr;
                    bool saved = DataShortcuts.SaveDataShortcutManager(ref mref);
                    res["saved"] = saved;
                    res["published"] = picked; res["missing"] = missing;
                    string shortcutsDir = Path.Combine(DataShortcuts.GetWorkingFolder(), DataShortcuts.GetCurrentProjectFolder(), "_Shortcuts", "Surfaces");
                    if (Directory.Exists(shortcutsDir)) { var files = new JsonArray(); foreach (string f in Directory.GetFiles(shortcutsDir, "*.xml")) files.Add(Path.GetFileName(f)); res["xml_files"] = files; }
                    return res;
                }
                if (action == "reference")
                {
                    if (names.Count == 0) throw new InvalidOperationException("reference requires surfaces[name]");
                    Database db = doc.Database;
                    CivDoc civ = Civ(db);
                    var done = new JsonArray(); var missing = new JsonArray();
                    string srcDwg = GetString(a, "source_dwg", null);
                    int np = 0; try { np = mgr.GetPublishedItemsCount(); } catch (System.Exception ex) { res["published_error"] = ex.Message; }
                    foreach (string n in names)
                    {
                        ObjectIdCollection rids;
                        if (!string.IsNullOrEmpty(srcDwg))
                        {
                            if (!File.Exists(srcDwg)) throw new InvalidOperationException("source_dwg does not exist: " + srcDwg);
                            try { rids = DataShortcuts.CreateReference(db, srcDwg, n, "Surface"); }
                            catch (System.Exception ex) { done.Add(new JsonObject { ["name"] = n, ["error"] = ex.GetType().Name + ": " + ex.Message }); continue; }
                        }
                        else
                        {
                            int idx = -1;
                            for (int i = 0; i < np; i++) { var it = mgr.GetPublishedItemAt(i); if (it.DSEntityType == "Surface" && it.Name == n) { idx = i; break; } }
                            if (idx < 0) { missing.Add(n); continue; }
                            rids = mgr.CreateReference(idx, db);
                        }
                        ObjectId rid = rids != null && rids.Count > 0 ? rids[0] : ObjectId.Null;
                        var o = new JsonObject { ["name"] = n, ["ids_created"] = rids == null ? 0 : rids.Count, ["handle"] = rid.IsNull ? null : rid.Handle.ToString() };
                        if (!rid.IsNull)
                        {
                            using (Transaction tr = db.TransactionManager.StartTransaction())
                            {
                                var s = tr.GetObject(rid, OpenMode.ForRead) as CivSurface;
                                if (s != null)
                                {
                                    try { var gp = s.GetGeneralProperties(); o["points"] = gp.NumberOfPoints; o["elev_min"] = Math.Round(gp.MinimumElevation, 3); o["elev_max"] = Math.Round(gp.MaximumElevation, 3); } catch (System.Exception ex) { o["props_error"] = ex.Message; }
                                    o["surface_name"] = s.Name; o["layer"] = s.Layer;
                                    try { o["is_reference"] = s.IsReferenceObject; } catch { }
                                }
                                tr.Commit();
                            }
                        }
                        done.Add(o);
                    }
                    res["referenced"] = done; res["missing"] = missing;
                    return res;
                }
                throw new InvalidOperationException("action must be setup, list, publish, reference or repair; received: " + action);
            }
        }
    }
}
