using System;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// Node list_profiles: per alignment, inventory the profiles (EG / FG), profile views and sample line groups.
    ///
    /// Health check before creating profile views: has_ground / has_design use the SAME recognition rules
    /// as create_profile_view (ground = EG/Surface, design = FG/Layout),
    /// so an alignment with either column false will certainly fail there with "cannot recognise existing ground / design profile" --
    /// no need to wait for the sheet run to fail.
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeListProfiles(JsonObject a, Document doc)
            => ListProfiles(a, doc);

        public static JsonNode ListProfiles(JsonObject a, Document doc)
        {
            string only = GetString(a, "alignment", null);
            bool skipOffsets = GetBool(a, "skip_offset_alignments", true);

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var arr = new JsonArray();
            int noGround = 0, noDesign = 0, noView = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId alId in civ.GetAlignmentIds())
                {
                    var al = tr.GetObject(alId, OpenMode.ForRead) as CivAlignment;
                    if (al == null) continue;
                    if (!string.IsNullOrWhiteSpace(only) &&
                        !string.Equals(al.Name, only, StringComparison.OrdinalIgnoreCase)) continue;
                    // Offset alignments (like Alignment - (n)-Left-25.000) are hidden by default; too noisy
                    if (skipOffsets && string.IsNullOrWhiteSpace(only) &&
                        al.Name.StartsWith("Alignment -", StringComparison.OrdinalIgnoreCase)) continue;

                    var ps = new JsonArray();
                    bool hasGround = false, hasDesign = false;
                    foreach (ObjectId pid in al.GetProfileIds())
                    {
                        var p = tr.GetObject(pid, OpenMode.ForRead) as CivProfile;
                        if (p == null) continue;
                        string pt = SafeStr(delegate { return p.ProfileType.ToString(); });
                        // Recognition rules identical to create_profile_view
                        bool isGround = pt.Equals("EG", StringComparison.OrdinalIgnoreCase)
                                     || pt.IndexOf("Surface", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool isDesign = pt.Equals("FG", StringComparison.OrdinalIgnoreCase)
                                     || pt.IndexOf("Layout", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (isGround) hasGround = true;
                        if (isDesign) hasDesign = true;

                        var one = new JsonObject
                        {
                            ["name"] = p.Name,
                            ["type"] = pt,
                            ["role"] = isGround ? "EG" : isDesign ? "FG" : "(unrecognised)",
                            ["handle"] = p.Handle.ToString(),
                            ["style"] = SafeStr(delegate { return p.StyleName; })
                        };
                        try
                        {
                            one["start_station"] = Round(p.StartingStation, 3);
                            one["end_station"] = Round(p.EndingStation, 3);
                            one["start_elev"] = Round(p.ElevationAt(p.StartingStation), 3);
                            one["end_elev"] = Round(p.ElevationAt(p.EndingStation), 3);
                        }
                        catch { }
                        ps.Add(one);
                    }

                    int views = 0;
                    try { views = al.GetProfileViewIds().Count; } catch { }
                    int slg = 0;
                    try { slg = al.GetSampleLineGroupIds().Count; } catch { }

                    if (!hasGround) noGround++;
                    if (!hasDesign) noDesign++;
                    if (views == 0) noView++;

                    arr.Add(new JsonObject
                    {
                        ["alignment"] = al.Name,
                        ["handle"] = al.Handle.ToString(),
                        ["length"] = Round(al.Length, 3),
                        ["profile_count"] = ps.Count,
                        ["has_ground"] = hasGround,
                        ["has_design"] = hasDesign,
                        ["profile_views"] = views,
                        ["sample_line_groups"] = slg,
                        ["can_make_profile_view"] = hasGround && hasDesign,
                        ["profiles"] = ps
                    });
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["alignments"] = arr.Count,
                ["missing_ground"] = noGround,
                ["missing_design"] = noDesign,
                ["without_profile_view"] = noView,
                ["data"] = arr
            };
        }
    }
}
