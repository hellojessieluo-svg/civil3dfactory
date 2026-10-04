using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivProfileType = Autodesk.Civil.DatabaseServices.ProfileType;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeRelinkGroundProfiles(JsonObject a, Document doc)
        {
            string sfName = Need(a, "surface");
            bool dryRun = GetBool(a, "dry_run", false);
            string styleOverride = GetString(a, "style", null);
            string labelSet = GetString(a, "label_set", null);
            var only = new HashSet<string>(StringComparer.Ordinal);
            if (a["alignments"] is JsonArray arr) foreach (JsonNode n in arr) if (n != null) only.Add(n.ToString());

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var result = new JsonArray();
            int relinked = 0, skipped = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId sfId = FindSurfaceId(tr, civ, sfName);
                if (sfId.IsNull) throw new InvalidOperationException("Surface '" + sfName + "'; run list_surfaces first");
                ObjectId labelId = string.IsNullOrEmpty(labelSet) ? ObjectId.Null
                    : FindStyleId(tr, civ.Styles.LabelSetStyles.ProfileLabelSetStyles, labelSet);

                foreach (ObjectId aid in civ.GetAlignmentIds())
                {
                    var al = (CivAlignment)tr.GetObject(aid, OpenMode.ForRead);
                    if (only.Count > 0 && !only.Contains(al.Name)) continue;

                    var egs = new List<CivProfile>();
                    foreach (ObjectId pid in al.GetProfileIds())
                    {
                        var p = tr.GetObject(pid, OpenMode.ForRead) as CivProfile;
                        if (p != null && p.ProfileType == CivProfileType.EG) egs.Add(p);
                    }
                    if (egs.Count == 0) continue;

                    foreach (CivProfile old in egs)
                    {
                        string oldSource = "";
                        try { oldSource = old.DataSourceName ?? ""; } catch { }
                        bool alreadyOnTarget = false;
                        try { alreadyOnTarget = old.DataSourceId == sfId; } catch { }
                        string mode = "";
                        try { mode = old.UpdateMode.ToString(); } catch { }

                        var rec = new JsonObject
                        {
                            ["alignment"] = al.Name,
                            ["old_name"] = old.Name,
                            ["old_handle"] = old.Handle.ToString(),
                            ["old_source"] = oldSource,
                            ["old_update_mode"] = mode,
                            ["old_start_elev"] = Math.Round(SafeElev(old, al.StartingStation), 3),
                            ["old_end_elev"] = Math.Round(SafeElev(old, al.EndingStation), 3),
                            ["profile_views"] = al.GetProfileViewIds().Count
                        };
                        if (alreadyOnTarget && !GetBool(a, "force", false))
                        {
                            rec["action"] = "skip (already linked to target surface)";
                            skipped++; result.Add(rec); continue;
                        }
                        if (dryRun)
                        {
                            rec["action"] = "would_relink";
                            result.Add(rec); continue;
                        }

                        string name = old.Name;
                        ObjectId styleId = old.StyleId;
                        ObjectId layerId = old.LayerId;
                        if (!string.IsNullOrEmpty(styleOverride))
                            styleId = FindStyleId(tr, civ.Styles.ProfileStyles, styleOverride);

                        old.UpgradeOpen();
                        old.Erase();

                        ObjectId nid = CivProfile.CreateFromSurface(name, al.ObjectId, sfId, layerId, styleId, labelId);
                        var np = (CivProfile)tr.GetObject(nid, OpenMode.ForRead);
                        rec["action"] = "relinked";
                        rec["new_handle"] = np.Handle.ToString();
                        rec["new_name"] = np.Name;
                        try { rec["new_source"] = np.DataSourceName; } catch { }
                        try { rec["new_update_mode"] = np.UpdateMode.ToString(); } catch { }
                        rec["new_start_elev"] = Math.Round(SafeElev(np, al.StartingStation), 3);
                        rec["new_end_elev"] = Math.Round(SafeElev(np, al.EndingStation), 3);
                        relinked++;
                        result.Add(rec);
                    }
                }
                if (dryRun) tr.Abort(); else tr.Commit();
            }
            return new JsonObject
            {
                ["surface"] = sfName,
                ["dry_run"] = dryRun,
                ["relinked"] = relinked,
                ["skipped"] = skipped,
                ["profiles"] = result
            };
        }

        static double SafeElev(CivProfile p, double station)
        {
            try { return p.ElevationAt(station); } catch { return double.NaN; }
        }
    }
}
