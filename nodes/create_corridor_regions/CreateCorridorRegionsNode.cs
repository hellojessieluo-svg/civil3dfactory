using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivAlign = Autodesk.Civil.DatabaseServices.Alignment;
using CivAssembly = Autodesk.Civil.DatabaseServices.Assembly;
using CivCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using CivBaseline = Autodesk.Civil.DatabaseServices.Baseline;
using CivProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivProfileType = Autodesk.Civil.DatabaseServices.ProfileType;
using CivTargetInfo = Autodesk.Civil.DatabaseServices.SubassemblyTargetInfo;
using CivilDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    ///
    ///
    ///
    /// </summary>
    public static partial class Ops
    {
        /// <summary>
        /// </summary>
        internal static bool IsAdjSwitchSlot(string displayName)
            => displayName != null && displayName.StartsWith("Adj_", StringComparison.OrdinalIgnoreCase);

        static JsonNode RunNodeCreateCorridorRegions(JsonObject args, Document doc)
            => CreateCorridorRegions(args, doc);

        public static JsonNode CreateCorridorRegions(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string sfName = Need(a, "surface");
            string corridorName = GetString(a, "name", alName + "_Corridor");
            string baselineName = GetString(a, "baseline", alName + "_Baseline");
            JsonArray regions = a["regions"] as JsonArray;
            if (regions == null || regions.Count == 0)
                throw new InvalidOperationException("regions cannot be empty: [{assembly,start,end,name?}, ...] is required");

            Database db = doc.Database;
            CivilDoc civ = Civ(db);
            var notes = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EraseCorridors(tr, db, corridorName);
                tr.Commit();
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlign al = FindAlignment(tr, civ, alName);
                if (al == null) throw new InvalidOperationException("Alignment '" + alName + "'.");
                double s0 = al.StartingStation, s1 = al.EndingStation;

                ObjectId fgId = ObjectId.Null;
                foreach (ObjectId pid in al.GetProfileIds())
                {
                    var p = tr.GetObject(pid, OpenMode.ForRead) as CivProfile;
                    if (p != null && p.ProfileType == CivProfileType.FG) { fgId = pid; break; }
                }
                if (fgId.IsNull)
                    throw new InvalidOperationException("Alignment '" + alName + "' has no design profile; run create_design_profiles first.");

                ObjectId sfId = FindSurfaceId(tr, civ, sfName);
                if (sfId.IsNull) throw new InvalidOperationException("Surface '" + sfName + "'.");

                var asmByName = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var asm = tr.GetObject(id, OpenMode.ForRead) as CivAssembly;
                    if (asm != null && !asmByName.ContainsKey(asm.Name)) asmByName[asm.Name] = id;
                }

                ObjectId leftId = ObjectId.Null, rightId = ObjectId.Null;
                foreach (ObjectId aid in civ.GetAlignmentIds())
                {
                    var x = tr.GetObject(aid, OpenMode.ForRead) as CivAlign;
                    if (x == null) continue;
                    if (x.Name == alName + "_L") leftId = aid;
                    else if (x.Name == alName + "_R") rightId = aid;
                }
                if (leftId.IsNull || rightId.IsNull)
                    notes.Add("Width targets missing (" + alName + "_L/" + alName + "_R); the corridor will grade by the assembly alone");

                ObjectId corridorId = civ.CorridorCollection.Add(corridorName);
                var corridor = tr.GetObject(corridorId, OpenMode.ForWrite) as CivCorridor;
                if (corridor == null) throw new InvalidOperationException("Corridor created but the object cannot be retrieved.");

                CivBaseline bl = corridor.Baselines.Add(baselineName, al.ObjectId, fgId);

                var added = new JsonArray();
                int idx = 0, skipped = 0;
                foreach (JsonNode rn in regions)
                {
                    idx++;
                    var r = rn as JsonObject;
                    if (r == null) continue;
                    string asmName = GetString(r, "assembly", null);
                    if (string.IsNullOrWhiteSpace(asmName))
                    { notes.Add("Item " + idx + " has no assembly, skipped"); skipped++; continue; }
                    if (!asmByName.ContainsKey(asmName))
                    { notes.Add("Item " + idx + ": assembly '" + asmName + "' not in the drawing, skipped"); skipped++; continue; }

                    double st = GetDouble(r, "start", double.NaN);
                    double en = GetDouble(r, "end", double.NaN);
                    if (double.IsNaN(st)) st = s0;
                    if (double.IsNaN(en)) en = s1;
                    double rawSt = st, rawEn = en;
                    if (st < s0) st = s0;
                    if (en > s1) en = s1;
                    if (en - st < 1e-6)
                    {
                        notes.Add("Item " + idx + " " + Math.Round(rawSt, 3) + "~" + Math.Round(rawEn, 3) +
                                  " lies entirely outside the alignment range [" + Math.Round(s0, 3) + "," + Math.Round(s1, 3) + "], dropped");
                        skipped++; continue;
                    }
                    if (Math.Abs(rawSt - st) > 0.001 || Math.Abs(rawEn - en) > 0.001)
                        notes.Add("Item " + idx + " stations clipped: " + Math.Round(rawSt, 3) + "~" + Math.Round(rawEn, 3) +
                                  " → " + Math.Round(st, 3) + "~" + Math.Round(en, 3));

                    string rName = GetString(r, "name", "RG-" + idx.ToString("00") + "-" + asmName);
                    try
                    {
                        bl.BaselineRegions.Add(rName, asmByName[asmName], st, en);
                        added.Add(new JsonObject
                        {
                            ["index"] = idx,
                            ["name"] = rName,
                            ["assembly"] = asmName,
                            ["start"] = Round(st, 3),
                            ["end"] = Round(en, 3),
                            ["length"] = Round(en - st, 3)
                        });
                    }
                    catch (System.Exception ex)
                    { notes.Add("Item " + idx + " region creation failed: " + ex.Message); skipped++; }
                }

                if (added.Count == 0)
                    throw new InvalidOperationException("No region was created; the corridor is invalid.");

                corridor.Rebuild();

                var targets = corridor.GetTargets();
                var sfIds = new ObjectIdCollection { sfId };
                var offIds = new ObjectIdCollection();
                if (!leftId.IsNull) offIds.Add(leftId);
                if (!rightId.IsNull) offIds.Add(rightId);
                int sCount = 0, oCount = 0, sSkipped = 0;
                foreach (CivTargetInfo t in targets)
                {
                    string tt = t.TargetType.ToString();
                    if (tt == "Surface" && IsAdjSwitchSlot(t.DisplayName)) sSkipped++;
                    else if (tt == "Surface") { t.TargetIds = sfIds; sCount++; }
                    else if (tt == "Offset" && offIds.Count > 0) { t.TargetIds = offIds; oCount++; }
                }
                corridor.SetTargets(targets);
                corridor.Rebuild();

                var codes = new JsonArray();
                try { foreach (string c in corridor.GetLinkCodes()) codes.Add(c); }
                catch (System.Exception) { }

                if (sCount == 0 && !GetBool(a, "allow_no_surface_targets", false))
                    throw new InvalidOperationException(
                        "Corridor '" + corridorName + "' has no surface target slot after creation (surface_targets_set=0); link codes are only [" +
                        string.Join(",", codes.Select(c => c?.ToString())) + "]. The grading subassembly did not run: check the subassembly status of the assembly in the Civil 3D UI first" +
                        " (Status=FileNotFound / UseEmbeddedProject=False is the symptom), re-import the PKT and embed it in the drawing, then rerun; " +
                        "pass allow_no_surface_targets:true to proceed if the assembly is known not to target a surface.");

                var res = new JsonObject
                {
                    ["corridor"] = corridorName,
                    ["alignment"] = alName,
                    ["alignment_range"] = Round(s0, 3) + " ~ " + Round(s1, 3),
                    ["baseline"] = baselineName,
                    ["regions_requested"] = regions.Count,
                    ["regions_added"] = added.Count,
                    ["regions_skipped"] = skipped,
                    ["surface_targets_set"] = sCount,
                    ["surface_slots_skipped"] = sSkipped,
                    ["offset_targets_set"] = oCount,
                    ["regions"] = added,
                    ["link_codes"] = codes,
                    ["notes"] = notes
                };
                tr.Commit();
                return res;
            }
        }
    }
}
