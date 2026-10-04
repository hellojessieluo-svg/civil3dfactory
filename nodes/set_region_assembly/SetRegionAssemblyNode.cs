using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using CivAssembly = Autodesk.Civil.DatabaseServices.Assembly;
using CivSubassembly = Autodesk.Civil.DatabaseServices.Subassembly;
using CivBaseline = Autodesk.Civil.DatabaseServices.Baseline;
using CivBaselineRegion = Autodesk.Civil.DatabaseServices.BaselineRegion;
using CivTargetInfo = Autodesk.Civil.DatabaseServices.SubassemblyTargetInfo;

namespace Civil3DFactory
{
    /// <summary>
    ///
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeSetRegionAssembly(JsonObject a, Document doc)
        {
            string corName = Need(a, "corridor");
            string asmName = Need(a, "assembly");
            string blFilter = GetString(a, "baseline", null);
            bool dryRun = GetBool(a, "dry_run", true);
            bool rebuild = GetBool(a, "rebuild", true);
            HashSet<string> want = null;
            var rArr = a["regions"] as JsonArray;
            if (rArr != null)
            {
                want = new HashSet<string>();
                foreach (JsonNode n in rArr) want.Add(n.ToString());
                if (want.Count == 0) throw new InvalidOperationException("regions is empty; omit regions to select all regions.");
            }

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var rep = new JsonObject { ["corridor"] = corName, ["assembly"] = asmName, ["dry_run"] = dryRun };
            var details = new JsonArray();
            var targetsAfter = new JsonArray();
            var notFound = new JsonArray();
            int changed = 0, already = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivCorridor cor = null;
                var corNames = new List<string>();
                foreach (ObjectId cid in civ.CorridorCollection)
                {
                    var c = tr.GetObject(cid, OpenMode.ForRead) as CivCorridor;
                    if (c == null) continue;
                    corNames.Add(c.Name);
                    if (c.Name == corName) cor = c;
                }
                if (cor == null) throw new InvalidOperationException("Corridor '" + corName + "'; drawing contains: " + string.Join(", ", corNames));

                ObjectId asmId = ObjectId.Null;
                var asmNames = new List<string>();
                foreach (ObjectId id in civ.AssemblyCollection)
                {
                    var asm = tr.GetObject(id, OpenMode.ForRead) as CivAssembly;
                    if (asm == null) continue;
                    asmNames.Add(asm.Name);
                    if (asm.Name == asmName) asmId = id;
                }
                if (asmId.IsNull) throw new InvalidOperationException("Assembly not found: '" + asmName + "'; drawing contains: " + string.Join(", ", asmNames));

                var newAsm = (CivAssembly)tr.GetObject(asmId, OpenMode.ForRead);
                var subs = new JsonArray();
                var broken = new List<string>();
                foreach (CivSubassembly sa in ComposerSubassemblies(newAsm, tr))
                {
                    string st = SafeStr(() => sa.StatusOf()) ?? "?";
                    subs.Add(new JsonObject { ["name"] = sa.Name, ["status"] = st });
                    if (!string.Equals(st, "UpToDate", StringComparison.OrdinalIgnoreCase)) broken.Add(sa.Name + "=" + st);
                }
                rep["assembly_subassemblies"] = subs;
                if (broken.Count > 0)
                    throw new InvalidOperationException("Assembly '" + asmName + "' has SAC subassemblies that are not ready (" + string.Join("; ", broken) +
                        "); switching would create empty regions and zero quantities. Repair using create_assembly (embed:true) or check_sac_paths first.");

                var seen = new HashSet<string>();
                if (!dryRun) cor.UpgradeOpen();
                foreach (CivBaseline b in cor.Baselines)
                {
                    if (blFilter != null && b.Name != blFilter) continue;
                    foreach (CivBaselineRegion r in b.BaselineRegions)
                    {
                        if (want != null && !want.Contains(r.Name)) continue;
                        seen.Add(r.Name);
                        string before = DcpNameOf(tr, SafeRegionAsm(r));
                        var d = new JsonObject
                        {
                            ["baseline"] = b.Name,
                            ["region"] = r.Name,
                            ["start"] = Round(r.StartStation, 3),
                            ["end"] = Round(r.EndStation, 3),
                            ["before"] = before
                        };
                        if (before == asmName) { already++; d["after"] = before; d["note"] = "Already uses this assembly; unchanged"; details.Add(d); continue; }
                        if (dryRun) { d["after"] = asmName; d["note"] = "dry_run: not written"; details.Add(d); changed++; continue; }
                        r.AssemblyId = asmId;
                        d["after"] = asmName;
                        d["after_readback"] = DcpNameOf(tr, SafeRegionAsm(r));
                        details.Add(d);
                        changed++;
                    }
                }
                if (want != null)
                    foreach (string w in want) if (!seen.Contains(w)) notFound.Add(w);

                if (!dryRun && changed > 0 && rebuild)
                {
                    try { cor.Rebuild(); rep["rebuilt"] = true; }
                    catch (System.Exception ex) { rep["rebuilt"] = false; rep["rebuild_error"] = ex.Message; }
                }

                if (!dryRun && changed > 0)
                    foreach (CivBaseline b in cor.Baselines)
                    {
                        if (blFilter != null && b.Name != blFilter) continue;
                        foreach (CivBaselineRegion r in b.BaselineRegions)
                        {
                            if (want != null && !want.Contains(r.Name)) continue;
                            var slots = new JsonArray();
                            try
                            {
                                foreach (CivTargetInfo t in r.GetTargets())
                                    slots.Add(new JsonObject
                                    {
                                        ["slot"] = t.DisplayName,
                                        ["subassembly"] = SafeStr(() => t.SubassemblyName) ?? "",
                                        ["type"] = t.TargetType.ToString(),
                                        ["objects"] = t.TargetIds == null ? 0 : t.TargetIds.Count
                                    });
                            }
                            catch (System.Exception ex) { slots.Add(new JsonObject { ["error"] = ex.Message }); }
                            targetsAfter.Add(new JsonObject { ["region"] = r.Name, ["slots"] = slots });
                        }
                    }
                tr.Commit();
            }

            rep["regions_changed"] = changed;
            rep["regions_already"] = already;
            rep["regions_not_found"] = notFound;
            rep["details"] = details;
            rep["targets_after"] = targetsAfter;
            if (notFound.Count > 0) rep["warning"] = "Some region names were not found; inspect regions_not_found.";
            if (!dryRun && changed > 0) rep["next"] = "New subassembly target slots are empty. Use set_corridor_targets to restore surface and alignment targets, then compute_quantities. Old assembly retained.";
            return rep;
        }

        static ObjectId SafeRegionAsm(CivBaselineRegion r)
        {
            try { return r.AssemblyId; } catch { return ObjectId.Null; }
        }
    }
}
