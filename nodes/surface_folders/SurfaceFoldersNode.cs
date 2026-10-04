using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;
using CivFolder = Autodesk.Civil.DatabaseServices.Folder;
using CivFolderUtil = Autodesk.Civil.DatabaseServices.FolderUtil;

namespace Civil3DFactory
{
    /// <summary>
    ///
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeSurfaceFolders(JsonObject a, Document doc)
        {
            var fArr = a["folders"] as JsonArray;
            if (fArr == null || fArr.Count == 0) throw new InvalidOperationException("folders is required: [{name,surfaces?[],prefix?}]");
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var rep = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var byName = new Dictionary<string, ObjectId>();
                foreach (ObjectId sid in civ.GetSurfaceIds())
                { var s = tr.GetObject(sid, OpenMode.ForRead) as CivSurface; if (s != null && !byName.ContainsKey(s.Name)) byName[s.Name] = sid; }

                ObjectId rootId = CivFolderUtil.GetNonAlignmentRootFolder(RXClass.GetClass(typeof(CivSurface)), db);
                var root = (CivFolder)tr.GetObject(rootId, OpenMode.ForWrite);
                var existing = new Dictionary<string, ObjectId>();
                foreach (ObjectId fid in root.GetSubFolders())
                { var f = tr.GetObject(fid, OpenMode.ForRead) as CivFolder; if (f != null) existing[System.Linq.Enumerable.Last(f.GetPath().Split('/', '\\'))] = fid; }

                foreach (JsonNode n in fArr)
                {
                    var o = n as JsonObject; if (o == null) continue;
                    string name = o["name"] == null ? null : o["name"].ToString();
                    if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("Each folder requires name");
                    ObjectId fid;
                    bool created = false;
                    if (!existing.TryGetValue(name, out fid))
                    {
                        try { fid = root.CreateFolder(name); created = true; existing[name] = fid; }
                        catch (System.Exception ex) { rep.Add(new JsonObject { ["folder"] = name, ["error"] = "Cannot create folder: " + ex.Message }); continue; }
                    }
                    var folder = (CivFolder)tr.GetObject(fid, OpenMode.ForWrite);
                    var wanted = new List<string>();
                    var sArr = o["surfaces"] as JsonArray;
                    if (sArr != null) foreach (JsonNode s in sArr) wanted.Add(s.ToString());
                    string prefix = o["prefix"] == null ? null : o["prefix"].ToString();
                    if (!string.IsNullOrEmpty(prefix)) foreach (var kv in byName) if (kv.Key.StartsWith(prefix, StringComparison.Ordinal) && !wanted.Contains(kv.Key)) wanted.Add(kv.Key);
                    var moved = new JsonArray(); var missing = new JsonArray(); var failed = new JsonArray();
                    foreach (string sn in wanted)
                    {
                        ObjectId sid;
                        if (!byName.TryGetValue(sn, out sid)) { missing.Add(sn); continue; }
                        try { folder.AddEntity(sid); moved.Add(sn); }
                        catch (System.Exception ex) { failed.Add(new JsonObject { ["surface"] = sn, ["why"] = ex.Message }); }
                    }
                    rep.Add(new JsonObject { ["folder"] = name, ["created"] = created, ["moved"] = moved, ["missing"] = missing, ["failed"] = failed });
                }
                tr.Commit();
            }
            return new JsonObject { ["folders"] = rep };
        }
    }
}
