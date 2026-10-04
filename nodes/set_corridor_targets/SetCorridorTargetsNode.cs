using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
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
        sealed class SctSpec
        {
            public string Slot;
            public string SaFilter;
            public HashSet<string> Regions;
            public ObjectIdCollection Ids;
            public string Desc;
        }

        static JsonNode RunNodeSetCorridorTargets(JsonObject a, Document doc)
        {
            string corName = Need(a, "corridor");
            var tArr = a["targets"] as JsonArray;
            if (tArr == null || tArr.Count == 0) throw new InvalidOperationException("targets is required: [{slot,subassembly_contains?,regions?,surfaces?,alignments?,handles?,clear?}]");
            bool rebuild = GetBool(a, "rebuild", true);

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var rep = new JsonObject { ["corridor"] = corName };
            var applied = new JsonArray();
            var unmatched = new JsonArray();
            var regionsNotFound = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivCorridor cor = null;
                foreach (ObjectId cid in civ.CorridorCollection)
                {
                    var c = tr.GetObject(cid, OpenMode.ForRead) as CivCorridor;
                    if (c != null && c.Name == corName) { cor = c; break; }
                }
                if (cor == null) throw new InvalidOperationException("Corridor '" + corName + "'.");
                cor.UpgradeOpen();

                var surfByName = new Dictionary<string, ObjectId>();
                foreach (ObjectId sid in civ.GetSurfaceIds())
                { var s = tr.GetObject(sid, OpenMode.ForRead) as CivSurface; if (s != null && !surfByName.ContainsKey(s.Name)) surfByName[s.Name] = sid; }
                var alByName = new Dictionary<string, ObjectId>();
                foreach (ObjectId aid in civ.GetAlignmentIds())
                { var al = tr.GetObject(aid, OpenMode.ForRead) as CivAlignment; if (al != null && !alByName.ContainsKey(al.Name)) alByName[al.Name] = aid; }

                var allRegions = new HashSet<string>();
                foreach (CivBaseline b in cor.Baselines)
                    foreach (CivBaselineRegion r in b.BaselineRegions) allRegions.Add(r.Name);

                var specs = new List<SctSpec>();
                foreach (JsonNode n in tArr)
                {
                    var o = n as JsonObject; if (o == null) continue;
                    string slot = o["slot"] == null ? null : o["slot"].ToString();
                    if (slot == null) throw new InvalidOperationException("Each target requires slot.");
                    string saf = o["subassembly_contains"] == null ? null : o["subassembly_contains"].ToString();
                    bool clear = GetBool(o, "clear", false);
                    var ids = new ObjectIdCollection();
                    var desc = new List<string>();
                    var sArr = o["surfaces"] as JsonArray;
                    if (sArr != null) foreach (JsonNode s in sArr)
                    {
                        ObjectId id; string nm = s.ToString();
                        if (!surfByName.TryGetValue(nm, out id)) throw new InvalidOperationException("Surface '" + nm + "'.");
                        ids.Add(id); desc.Add("surface:" + nm);
                    }
                    var aArr = o["alignments"] as JsonArray;
                    if (aArr != null) foreach (JsonNode s in aArr)
                    {
                        ObjectId id; string nm = s.ToString();
                        if (!alByName.TryGetValue(nm, out id)) throw new InvalidOperationException("Alignment '" + nm + "'.");
                        ids.Add(id); desc.Add("alignment:" + nm);
                    }
                    var hArr = o["handles"] as JsonArray;
                    if (hArr != null) foreach (JsonNode s in hArr)
                    {
                        string hs = s.ToString();
                        ObjectId id = db.GetObjectId(false, new Handle(Convert.ToInt64(hs, 16)), 0);
                        if (id.IsNull || id.IsErased) throw new InvalidOperationException("Handle is missing or erased: " + hs);
                        ids.Add(id); desc.Add("handle:" + hs);
                    }
                    if (ids.Count == 0 && !clear)
                        throw new InvalidOperationException("Slot '" + slot + "' has no surfaces/alignments/handles; set clear:true explicitly to empty it.");
                    if (ids.Count > 0 && clear)
                        throw new InvalidOperationException("Slot '" + slot + "' specifies both objects and clear:true; choose one.");
                    if (clear) desc.Add("clear");

                    HashSet<string> regionFilter = null;
                    var rArr = o["regions"] as JsonArray;
                    if (rArr != null)
                    {
                        regionFilter = new HashSet<string>();
                        foreach (JsonNode rn in rArr)
                        {
                            string rname = rn.ToString();
                            regionFilter.Add(rname);
                            if (!allRegions.Contains(rname))
                                regionsNotFound.Add(new JsonObject { ["slot"] = slot, ["subassembly_contains"] = saf, ["region"] = rname });
                        }
                        if (regionFilter.Count == 0) throw new InvalidOperationException("Slot '" + slot + "' has an empty regions array; omit regions to select all regions.");
                    }
                    specs.Add(new SctSpec { Slot = slot, SaFilter = saf, Regions = regionFilter, Ids = ids, Desc = string.Join(", ", desc.ToArray()) });
                }

                int regionHits = 0;
                foreach (CivBaseline b in cor.Baselines)
                {
                    foreach (CivBaselineRegion r in b.BaselineRegions)
                    {
                        var targets = r.GetTargets();
                        int hit = 0;
                        foreach (var sp in specs)
                        {
                            if (sp.Regions != null && !sp.Regions.Contains(r.Name)) continue;
                            foreach (CivTargetInfo t in targets)
                            {
                                if (!string.Equals(t.DisplayName, sp.Slot, StringComparison.OrdinalIgnoreCase)) continue;
                                string san = SafeStr(() => t.SubassemblyName) ?? "";
                                if (sp.SaFilter != null && san.IndexOf(sp.SaFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                                t.TargetIds = sp.Ids;
                                hit++;
                                applied.Add(new JsonObject { ["baseline"] = b.Name, ["region"] = r.Name, ["slot"] = t.DisplayName, ["subassembly"] = san, ["type"] = t.TargetType.ToString(), ["set"] = sp.Desc });
                            }
                        }
                        if (hit > 0) { r.SetTargets(targets); regionHits += hit; }
                    }
                }
                bool anyRegionFilter = specs.Any(s => s.Regions != null);
                if (regionHits == 0 && !anyRegionFilter)
                {
                    var targets = cor.GetTargets();
                    int hit = 0;
                    foreach (var sp in specs)
                        foreach (CivTargetInfo t in targets)
                        {
                            if (!string.Equals(t.DisplayName, sp.Slot, StringComparison.OrdinalIgnoreCase)) continue;
                            string san = SafeStr(() => t.SubassemblyName) ?? "";
                            if (sp.SaFilter != null && san.IndexOf(sp.SaFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            t.TargetIds = sp.Ids; hit++;
                            applied.Add(new JsonObject { ["level"] = "corridor", ["slot"] = t.DisplayName, ["subassembly"] = san, ["set"] = sp.Desc });
                        }
                    if (hit > 0) cor.SetTargets(targets);
                    rep["level"] = "corridor";
                }
                else rep["level"] = "region";

                foreach (var sp in specs)
                {
                    Func<JsonNode, bool> sameSlot = ap =>
                        ap["slot"].ToString().Equals(sp.Slot, StringComparison.OrdinalIgnoreCase)
                        && (sp.SaFilter == null || ap["subassembly"].ToString().IndexOf(sp.SaFilter, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (sp.Regions == null)
                    {
                        if (!applied.Any(ap => sameSlot(ap)))
                            unmatched.Add(new JsonObject { ["slot"] = sp.Slot, ["subassembly_contains"] = sp.SaFilter });
                    }
                    else
                    {
                        foreach (string rn in sp.Regions)
                            if (allRegions.Contains(rn) && !applied.Any(ap => sameSlot(ap) && ap["region"] != null && ap["region"].ToString() == rn))
                                unmatched.Add(new JsonObject { ["slot"] = sp.Slot, ["subassembly_contains"] = sp.SaFilter, ["region"] = rn });
                    }
                }

                if (rebuild) { try { cor.Rebuild(); rep["rebuilt"] = true; } catch (System.Exception ex) { rep["rebuilt"] = false; rep["rebuild_error"] = ex.Message; } }
                tr.Commit();
            }
            rep["applied"] = applied;
            rep["unmatched"] = unmatched;
            rep["regions_not_found"] = regionsNotFound;
            if (unmatched.Count > 0 || regionsNotFound.Count > 0)
                rep["warning"] = "Some target slots did not match. Check unmatched and regions_not_found.";
            return rep;
        }
    }
}
