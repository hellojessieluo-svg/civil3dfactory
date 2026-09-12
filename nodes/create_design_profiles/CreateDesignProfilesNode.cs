using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivAlign = Autodesk.Civil.DatabaseServices.Alignment;
using CivProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivProfileType = Autodesk.Civil.DatabaseServices.ProfileType;
using CivTin = Autodesk.Civil.DatabaseServices.TinSurface;
using CivilDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// create_design_profiles (S03): create the existing ground profile + design profile for every channel.
    ///
    /// The design profile is not purely flat: where the ground at an end is above the design bottom elevation, it ramps up at end_slope (default 1:10) to meet existing ground;
    /// ramp length = height difference x end_slope. This rule was back-calculated from the PVIs of the existing model:
    ///   1(E) start 3.7793 / ramp 7.793, 1(W) 3.6441 / 6.441, B1 4.2847 / 12.847,
    ///   1(E) end 4.7178 / 17.178: all four are 1:10.
    /// Channels whose end ground is already near the bottom elevation (3#, Y2, Y3, 4#) degrade automatically to a flat profile.
    ///
    /// End ground elevations are taken from the existing ground surface (FindElevationAtXY), not hard-coded.
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeCreateDesignProfiles(JsonObject args, Document doc)
            => CreateDesignProfiles(args, doc);

        public static JsonNode CreateDesignProfiles(JsonObject a, Document doc)
        {
            string surfName = Need(a, "surface");
            double designElev = GetDouble(a, "design_elev", 3.0);
            double endSlope = GetDouble(a, "end_slope", 10.0);      // 1:endSlope
            if (endSlope <= 1e-6) endSlope = 10.0;
            double tol = GetDouble(a, "ramp_tolerance", 0.05);      // no ramp when the height difference is below this
            string gTpl = GetString(a, "ground_name", "{channel}-EG");
            string dTpl = GetString(a, "design_name", "{channel}-FG");
            string gStyle = GetString(a, "ground_style", null);
            string dStyle = GetString(a, "design_style", null);
            string labelSet = GetString(a, "label_set", null);
            bool replaceExisting = GetBool(a, "replace_existing", true);

            var only = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            JsonArray sel = a["channels"] as JsonArray;
            if (sel != null) foreach (JsonNode n in sel) if (n != null) only.Add(n.ToString());

            Database db = doc.Database;
            var made = new JsonArray();
            var notes = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivilDoc civ = Civ(db);
                ObjectId sfId = FindSurfaceId(tr, civ, surfName);
                if (sfId.IsNull) throw new InvalidOperationException("Surface '" + surfName + "' not found.");
                var tin = tr.GetObject(sfId, OpenMode.ForRead) as CivTin;
                if (tin == null) throw new InvalidOperationException("'" + surfName + "' is not a TIN surface.");

                ObjectId gStyleId = FindStyleId(tr, civ.Styles.ProfileStyles, gStyle);
                ObjectId dStyleId = FindStyleId(tr, civ.Styles.ProfileStyles, dStyle);
                ObjectId labelId = FindStyleId(tr,
                    civ.Styles.LabelSetStyles.ProfileLabelSetStyles, labelSet);

                // How a centerline is recognised: it must have matching {name}_L / {name}_R width targets (output of S02).
                // Excluding by "no _L/_R suffix" alone is not enough: offset alignments of an old model may linger in the drawing
                // (e.g. Alignment(3)-Left-25.000, 1(E)#-Left-25.000); they lack the suffix,
                // would be mistaken for centerlines and produce a pile of useless profiles (12 extra in practice).
                var allNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var byId = new List<CivAlign>();
                foreach (ObjectId aid in civ.GetAlignmentIds())
                {
                    var al = tr.GetObject(aid, OpenMode.ForRead) as CivAlign;
                    if (al == null) continue;
                    allNames.Add(al.Name);
                    byId.Add(al);
                }
                var targets = new List<CivAlign>();
                foreach (CivAlign al in byId)
                {
                    if (only.Count > 0)
                    {
                        if (only.Contains(al.Name)) targets.Add(al);
                        continue;
                    }
                    if (al.Name.EndsWith("_L", StringComparison.Ordinal) ||
                        al.Name.EndsWith("_R", StringComparison.Ordinal)) continue;
                    if (!allNames.Contains(al.Name + "_L") && !allNames.Contains(al.Name + "_R"))
                    { notes.Add("Skipped " + al.Name + ": no matching _L/_R width targets, not treated as a centerline"); continue; }
                    targets.Add(al);
                }

                foreach (CivAlign al in targets)
                {
                    string ch = al.Name;
                    string gName = gTpl.Replace("{channel}", ch);
                    string dName = dTpl.Replace("{channel}", ch);
                    double s0 = al.StartingStation, s1 = al.EndingStation;

                    // Existing profile with the same name: delete first (no need for the "rename, then delete" dance;
                    // it is this node's own output, rerun = rebuild)
                    foreach (ObjectId pid in al.GetProfileIds())
                    {
                        var p = tr.GetObject(pid, OpenMode.ForRead) as CivProfile;
                        if (p == null) continue;
                        if (p.Name != gName && p.Name != dName) continue;
                        if (!replaceExisting)
                        { notes.Add(ch + " already has profile " + p.Name + ", not replaced"); continue; }
                        try { ((CivProfile)tr.GetObject(pid, OpenMode.ForWrite)).Erase(); }
                        catch (System.Exception ex) { notes.Add(ch + " failed to delete old profile: " + ex.Message); }
                    }

                    try { CivProfile.CreateFromSurface(gName, al.ObjectId, sfId, db.Clayer, gStyleId, labelId); }
                    catch (System.Exception ex)
                    { notes.Add(ch + " failed to create ground profile: " + ex.Message); }

                    // Ground elevation at both ends
                    double gStart = CdpGround(tin, al, s0);
                    double gEnd = CdpGround(tin, al, s1);

                    ObjectId dId;
                    try { dId = CivProfile.CreateByLayout(dName, al.ObjectId, db.Clayer, dStyleId, labelId); }
                    catch (System.Exception ex)
                    { notes.Add(ch + " failed to create design profile: " + ex.Message); continue; }

                    var design = tr.GetObject(dId, OpenMode.ForWrite) as CivProfile;
                    if (design == null) { notes.Add(ch + " design profile object cannot be retrieved"); continue; }

                    // Whether to ramp is a **per-end design decision**, not inferred from terrain.
                    // Existing model: 1(E) ramps at both ends, 1(W) and B1 only at the start, 3#/Y2/Y3/4# at neither;
                    // yet the last four also have end ground at 3.9~5.0 m, so terrain inference would ramp them all, contrary to the design.
                    // So default is no ramp; the ramps parameter specifies per channel, e.g. {"1(E)":["start","end"],"B1":["start"]}
                    bool wantS = false, wantE = false;
                    JsonObject ramps = a["ramps"] as JsonObject;
                    if (ramps != null && ramps.ContainsKey(ch))
                    {
                        var arr = ramps[ch] as JsonArray;
                        if (arr != null)
                            foreach (JsonNode n in arr)
                            {
                                string v = n == null ? "" : n.ToString().Trim().ToLowerInvariant();
                                if (v == "start" || v == "Start") wantS = true;
                                else if (v == "end" || v == "End") wantE = true;
                                else if (v == "both" || v == "Both") { wantS = true; wantE = true; }
                            }
                    }

                    double rampS = 0.0, rampE = 0.0;
                    bool hasS = wantS && !double.IsNaN(gStart) && gStart - designElev > tol;
                    bool hasE = wantE && !double.IsNaN(gEnd) && gEnd - designElev > tol;
                    if (wantS && !hasS) notes.Add(ch + " start ramp requested, but ground " +
                        (double.IsNaN(gStart) ? "could not be sampled" : Math.Round(gStart, 3) + " is less than " + tol + " m above") + "; treated as flat");
                    if (wantE && !hasE) notes.Add(ch + " end ramp requested, but ground " +
                        (double.IsNaN(gEnd) ? "could not be sampled" : Math.Round(gEnd, 3) + " is less than " + tol + " m above") + "; treated as flat");
                    if (hasS) rampS = (gStart - designElev) * endSlope;
                    if (hasE) rampE = (gEnd - designElev) * endSlope;
                    // If the two ramps together exceed the alignment length, give up ramping and fall back to flat
                    if (rampS + rampE >= (s1 - s0))
                    {
                        notes.Add(ch + " total end ramp length " + Math.Round(rampS + rampE, 2) +
                                  " m exceeds the alignment length, fell back to flat");
                        hasS = hasE = false; rampS = rampE = 0.0;
                    }

                    try
                    {
                        if (hasS)
                        {
                            design.PVIs.AddPVI(s0, gStart);
                            design.PVIs.AddPVI(s0 + rampS, designElev);
                        }
                        else design.PVIs.AddPVI(s0, designElev);

                        if (hasE)
                        {
                            design.PVIs.AddPVI(s1 - rampE, designElev);
                            design.PVIs.AddPVI(s1, gEnd);
                        }
                        else design.PVIs.AddPVI(s1, designElev);
                    }
                    catch (System.Exception ex)
                    { notes.Add(ch + " failed to write PVIs: " + ex.Message); continue; }

                    made.Add(new JsonObject
                    {
                        ["channel"] = ch,
                        ["ground_profile"] = gName,
                        ["design_profile"] = dName,
                        ["design_elev"] = Round(designElev, 3),
                        ["ground_start"] = double.IsNaN(gStart) ? 0.0 : Round(gStart, 4),
                        ["ground_end"] = double.IsNaN(gEnd) ? 0.0 : Round(gEnd, 4),
                        ["ramp_start_len"] = Round(rampS, 3),
                        ["ramp_end_len"] = Round(rampE, 3),
                        ["end_slope"] = "1:" + endSlope.ToString("0.##"),
                        ["pvi_count"] = design.PVIs.Count,
                        ["flat"] = !hasS && !hasE
                    });
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["profiles"] = made.Count,
                ["items"] = made,
                ["notes"] = notes
            };
        }

        /// <summary>Surface elevation at the alignment centre point of a station; NaN if it cannot be sampled.</summary>
        static double CdpGround(CivTin tin, CivAlign al, double station)
        {
            double e = 0, n = 0;
            try { al.PointLocation(station, 0.0, ref e, ref n); }
            catch (System.Exception) { return double.NaN; }
            try { return tin.FindElevationAtXY(e, n); }
            catch (System.Exception) { return double.NaN; }
        }
    }
}
