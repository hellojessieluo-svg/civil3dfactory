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
    /// 节点 list_profiles：逐路线盘点剖面线（地面线/设计线）、纵断面图、采样线组。
    ///
    /// 出纵断面图前的体检表：has_ground / has_design 用的是 create_profile_view
    /// **同一套识别规则**（地面线看 EG/Surface，设计线看 FG/Layout），
    /// 所以这两列为 false 的路线，出图必然报「无法识别现状地形/设计纵断面」——
    /// 不用等出图失败才知道。
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
                    // 偏移路线（Alignment - (n)-Left-25.000 这类）默认不列，太吵
                    if (skipOffsets && string.IsNullOrWhiteSpace(only) &&
                        al.Name.StartsWith("Alignment -", StringComparison.OrdinalIgnoreCase)) continue;

                    var ps = new JsonArray();
                    bool hasGround = false, hasDesign = false;
                    foreach (ObjectId pid in al.GetProfileIds())
                    {
                        var p = tr.GetObject(pid, OpenMode.ForRead) as CivProfile;
                        if (p == null) continue;
                        string pt = SafeStr(delegate { return p.ProfileType.ToString(); });
                        // 与 create_profile_view 完全一致的识别规则
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
                            ["role"] = isGround ? "地面线" : isDesign ? "设计线" : "(识别不出)",
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
