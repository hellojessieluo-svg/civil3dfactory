using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivBaseline = Autodesk.Civil.DatabaseServices.Baseline;
using CivCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using CivProfile = Autodesk.Civil.DatabaseServices.Profile;

namespace Civil3DFactory
{
    /// <summary>
    /// Node attach_baseline_profile: re-attach the profile reference of corridor baselines.
    ///
    /// Root cause: the design profile was deleted (typically a window-select delete of the profile view took the profile with it),
    /// leaving the baseline's profile reference dangling: regions and link codes look intact but the geometry is dead,
    /// corridor surfaces cannot be evaluated and rebuild_corridor does not help. A recreated design profile is not
    /// re-attached automatically; BaseBaseline.SetAlignmentAndProfile must be called explicitly, then rebuild.
    /// (API signature verified in-process with the api node: BaseBaseline.SetAlignmentAndProfile(ObjectId, ObjectId))
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeAttachBaselineProfile(JsonObject a, Document doc)
            => AttachBaselineProfile(a, doc);

        public static JsonNode AttachBaselineProfile(JsonObject a, Document doc)
        {
            string corridorName = Need(a, "corridor");
            string onlyAlignment = GetString(a, "alignment", null);
            string profileName = GetString(a, "profile", null);
            bool rebuild = GetBool(a, "rebuild", true);

            Database db = doc.Database;
            var attached = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivCorridor corridor = FindCorridor(tr, db, corridorName);
                if (corridor == null)
                    throw new InvalidOperationException("Corridor '" + corridorName + "' not found.");
                corridor.UpgradeOpen();

                foreach (CivBaseline bl in corridor.Baselines)
                {
                    CivAlignment al = null;
                    try { al = tr.GetObject(bl.AlignmentId, OpenMode.ForRead) as CivAlignment; }
                    catch { }
                    if (al == null) continue;
                    if (!string.IsNullOrWhiteSpace(onlyAlignment) &&
                        !string.Equals(al.Name, onlyAlignment, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Pick the profile: explicit name first; otherwise the same design-profile rule as create_profile_view (FG/Layout)
                    ObjectId profId = ObjectId.Null;
                    string profPicked = null;
                    var candidates = new List<string>();
                    foreach (ObjectId pid in al.GetProfileIds())
                    {
                        var p = tr.GetObject(pid, OpenMode.ForRead) as CivProfile;
                        if (p == null) continue;
                        candidates.Add(p.Name);
                        if (!string.IsNullOrWhiteSpace(profileName))
                        {
                            if (string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase))
                            { profId = pid; profPicked = p.Name; break; }
                        }
                        else
                        {
                            string pt = SafeStr(delegate { return p.ProfileType.ToString(); });
                            if (pt.Equals("FG", StringComparison.OrdinalIgnoreCase) ||
                                pt.IndexOf("Layout", StringComparison.OrdinalIgnoreCase) >= 0)
                            { profId = pid; profPicked = p.Name; break; }
                        }
                    }
                    if (profId.IsNull)
                        throw new InvalidOperationException(
                            "Baseline alignment '" + al.Name + "' has no " +
                            (string.IsNullOrWhiteSpace(profileName) ? "design profile (FG/Layout)" : "profile '" + profileName + "'") +
                            ". Existing: " + (candidates.Count == 0 ? "none" : string.Join(", ", candidates)));

                    bl.SetAlignmentAndProfile(bl.AlignmentId, profId);
                    attached.Add(new JsonObject
                    {
                        ["baseline_alignment"] = al.Name,
                        ["profile"] = profPicked
                    });
                }

                if (attached.Count == 0)
                    throw new InvalidOperationException(
                        "Corridor '" + corridorName + "' has no matching baseline" +
                        (string.IsNullOrWhiteSpace(onlyAlignment) ? "" : " (filter: " + onlyAlignment + ")") + ".");

                bool rebuilt = false;
                string rebuildError = null;
                if (rebuild)
                {
                    try { corridor.Rebuild(); rebuilt = true; }
                    catch (System.Exception ex) { rebuildError = ex.Message; }
                }
                tr.Commit();

                var res = new JsonObject
                {
                    ["corridor"] = corridorName,
                    ["baselines_attached"] = attached.Count,
                    ["attached"] = attached,
                    ["rebuilt"] = rebuilt
                };
                if (rebuildError != null) res["rebuild_error"] = rebuildError;
                return res;
            }
        }
    }
}
