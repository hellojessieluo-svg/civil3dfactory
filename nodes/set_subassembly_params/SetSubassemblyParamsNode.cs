using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivAssembly = Autodesk.Civil.DatabaseServices.Assembly;
using CivSubassembly = Autodesk.Civil.DatabaseServices.Subassembly;

namespace Civil3DFactory
{
    /// <summary>
    ///
    ///
    ///
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeSetSubassemblyParams(JsonObject a, Document doc)
        {
            string asmName = Need(a, "assembly");
            string filter = GetString(a, "subassembly", null);
            var pd = a["params_double"] as JsonObject;
            var pl = a["params_long"] as JsonObject;
            var pstr = a["params_string"] as JsonObject;
            if (pd == null && pl == null && pstr == null)
                throw new InvalidOperationException("Specify at least one of params_double, params_long or params_string.");
            bool dry = GetBool(a, "dry_run", false);

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var rep = new JsonArray();
            int changed = 0, unchanged = 0, notFound = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAssembly asm = null;
                var have = new List<string>();
                foreach (ObjectId id in civ.AssemblyCollection)
                {
                    var x = tr.GetObject(id, OpenMode.ForRead) as CivAssembly;
                    if (x == null) continue;
                    have.Add(x.Name);
                    if (asm == null && string.Equals(x.Name, asmName, StringComparison.OrdinalIgnoreCase)) asm = x;
                }
                if (asm == null)
                    throw new InvalidOperationException(
                        "Assembly not found: '" + asmName + "'; drawing contains: " + string.Join(", ", have.ToArray()));

                var subs = new List<CivSubassembly>();
                var allNames = new List<string>();
                foreach (Autodesk.Civil.DatabaseServices.AssemblyGroup g in asm.Groups)
                    foreach (ObjectId sid in g.GetSubassemblyIds())
                    {
                        var sa = tr.GetObject(sid, dry ? OpenMode.ForRead : OpenMode.ForWrite) as CivSubassembly;
                        if (sa == null) continue;
                        allNames.Add(sa.Name);
                        if (!string.IsNullOrWhiteSpace(filter) &&
                            sa.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        subs.Add(sa);
                    }
                if (subs.Count == 0)
                    throw new InvalidOperationException("Assembly '" + asmName + "' contains no subassembly matching '" + filter +
                        "'; available subassemblies: " + string.Join(", ", allNames.ToArray()));

                foreach (CivSubassembly sa in subs)
                {
                    var log = new JsonArray();
                    ApplyOneParamSet(sa, pd, "double", dry, log, ref changed, ref unchanged, ref notFound);
                    ApplyOneParamSet(sa, pl, "long", dry, log, ref changed, ref unchanged, ref notFound);
                    ApplyOneParamSet(sa, pstr, "string", dry, log, ref changed, ref unchanged, ref notFound);
                    rep.Add(new JsonObject
                    {
                        ["subassembly"] = sa.Name,
                        ["handle"] = sa.Handle.ToString(),
                        ["params"] = log
                    });
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["assembly"] = asmName,
                ["subassembly_filter"] = filter,
                ["dry_run"] = dry,
                ["subassemblies_touched"] = rep.Count,
                ["params_changed"] = changed,
                ["params_already_equal"] = unchanged,
                ["params_not_found"] = notFound,
                ["details"] = rep,
                ["next"] = "Call save_dwg to persist, rebuild_corridor to update corridors, and compute_quantities to update volumes."
            };
        }

        static void ApplyOneParamSet(CivSubassembly sub, JsonObject prm, string kind, bool dry,
                                     JsonArray log, ref int changed, ref int unchanged, ref int notFound)
        {
            if (prm == null) return;
            System.Collections.IEnumerable coll;
            try
            {
                coll = kind == "string" ? (System.Collections.IEnumerable)sub.ParamsString
                     : kind == "double" ? (System.Collections.IEnumerable)sub.ParamsDouble
                     : (System.Collections.IEnumerable)sub.ParamsLong;
            }
            catch (System.Exception ex)
            {
                log.Add(new JsonObject { ["kind"] = kind, ["ok"] = false, ["why"] = "Cannot read parameter collection: " + ex.Message });
                return;
            }

            var names = new List<string>();
            foreach (object p in coll)
            {
                string dn = ReadProperty(p, "DisplayName") as string;
                if (dn != null && !names.Contains(dn)) names.Add(dn);
            }

            foreach (var kv in prm)
            {
                object target = null;
                foreach (object p in coll)
                {
                    string dn = ReadProperty(p, "DisplayName") as string;
                    if (dn != null && string.Equals(dn, kv.Key, StringComparison.OrdinalIgnoreCase)) { target = p; break; }
                }
                if (target == null)
                {
                    notFound++;
                    log.Add(new JsonObject
                    {
                        ["param"] = kv.Key,
                        ["kind"] = kind,
                        ["ok"] = false,
                        ["why"] = "Parameter not found; available on this subassembly (" + kind + "): " + string.Join(", ", names.ToArray())
                    });
                    continue;
                }

                var vp = target.GetType().GetProperty("Value");
                object before = null;
                try { before = vp.GetValue(target, null); } catch { }
                object want = kind == "string" ? (object)kv.Value.ToString()
                            : kind == "double" ? (object)kv.Value.GetValue<double>()
                            : (object)(long)kv.Value.GetValue<double>();

                var row = new JsonObject
                {
                    ["param"] = kv.Key,
                    ["kind"] = kind,
                    ["before"] = before == null ? null : before.ToString(),
                    ["after"] = Convert.ToString(want)
                };
                bool same = before != null && string.Equals(before.ToString(), Convert.ToString(want),
                                                            StringComparison.OrdinalIgnoreCase);
                if (same)
                {
                    unchanged++; row["ok"] = true; row["note"] = "Already has this value; unchanged";
                    log.Add(row); continue;
                }
                if (dry)
                {
                    row["ok"] = true; row["note"] = "dry_run: not written";
                    log.Add(row); continue;
                }
                try
                {
                    vp.SetValue(target, Convert.ChangeType(want, vp.PropertyType), null);
                    object after = null;
                    try { after = vp.GetValue(target, null); } catch { }
                    row["after_readback"] = after == null ? null : after.ToString();
                    row["ok"] = true;
                    changed++;
                }
                catch (System.Exception ex)
                {
                    row["ok"] = false; row["why"] = ex.GetType().Name + ": " + ex.Message;
                }
                log.Add(row);
            }
        }
    }
}
