using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeRenameSurfaces(JsonObject a, Document doc)
        {
            var items = a["items"] as JsonArray;
            if (items == null || items.Count == 0) throw new InvalidOperationException("items is required, e.g. [{\"from\":\"Surface1\",\"to\":\"Channel-1\"}]");
            bool dryRun = GetBool(a, "dry_run", false);
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var plan = new List<KeyValuePair<string, string>>();
            foreach (JsonNode n in items)
            {
                var o = n as JsonObject;
                if (o == null) throw new InvalidOperationException("Each items entry must be {from,to}");
                string f = Need(o, "from"), t = Need(o, "to");
                if (f == t) continue;
                plan.Add(new KeyValuePair<string, string>(f, t));
            }
            var done = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var byName = new Dictionary<string, CivSurface>(StringComparer.Ordinal);
                foreach (ObjectId id in civ.GetSurfaceIds())
                {
                    var s = tr.GetObject(id, OpenMode.ForRead) as CivSurface;
                    if (s != null && !byName.ContainsKey(s.Name)) byName[s.Name] = s;
                }
                var targets = new HashSet<string>(StringComparer.Ordinal);
                foreach (var kv in plan)
                {
                    if (!byName.ContainsKey(kv.Key)) throw new InvalidOperationException("Surface '" + kv.Key + "'; run list_surfaces first");
                    bool fromAlsoRenamed = false;
                    foreach (var kv2 in plan) if (kv2.Key == kv.Value) fromAlsoRenamed = true;
                    if (byName.ContainsKey(kv.Value) && !fromAlsoRenamed) throw new InvalidOperationException("Target name '" + kv.Value + "' already exists; not overwritten");
                    if (!targets.Add(kv.Value)) throw new InvalidOperationException("Duplicate target name: '" + kv.Value + "'");
                }
                foreach (var kv in plan)
                {
                    var s = byName[kv.Key];
                    var rec = new JsonObject { ["from"] = kv.Key, ["to"] = kv.Value, ["handle"] = s.Handle.ToString() };
                    try { rec["is_reference"] = s.IsReferenceObject; } catch { }
                    if (!dryRun)
                    {
                        s.UpgradeOpen();
                        s.Name = kv.Value;
                    }
                    done.Add(rec);
                }
                if (dryRun) tr.Abort(); else tr.Commit();
            }
            return new JsonObject { ["dry_run"] = dryRun, ["renamed"] = done, ["count"] = done.Count };
        }
    }
}
