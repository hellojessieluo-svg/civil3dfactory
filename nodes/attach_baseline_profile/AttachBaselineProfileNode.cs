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
    /// 节点 attach_baseline_profile：给走廊基线重挂纵断面引用。
    ///
    /// 病根：设计纵断面被删（典型：框选删纵断面图时把剖面线一起删了），
    /// 走廊基线的 profile 引用悬空——走廊区间、链接码看着都在，但几何是死的，
    /// 道路曲面评估不出来，rebuild_corridor 也救不活。后补的设计线不会自动
    /// 挂回基线，必须用 BaseBaseline.SetAlignmentAndProfile 显式重挂再重建。
    /// （API 签名用 api 零件在进程内查实：BaseBaseline.SetAlignmentAndProfile(ObjectId, ObjectId)）
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
                    throw new InvalidOperationException("找不到走廊 '" + corridorName + "'。");
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

                    // 选 profile：点名优先；否则按 create_profile_view 同一套设计线识别规则（FG/Layout）
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
                            "基线路线 '" + al.Name + "' 上找不到" +
                            (string.IsNullOrWhiteSpace(profileName) ? "设计纵断面（FG/Layout）" : "剖面线 '" + profileName + "'") +
                            "。现有：" + (candidates.Count == 0 ? "无" : string.Join("、", candidates)));

                    bl.SetAlignmentAndProfile(bl.AlignmentId, profId);
                    attached.Add(new JsonObject
                    {
                        ["baseline_alignment"] = al.Name,
                        ["profile"] = profPicked
                    });
                }

                if (attached.Count == 0)
                    throw new InvalidOperationException(
                        "走廊 '" + corridorName + "' 没有匹配的基线" +
                        (string.IsNullOrWhiteSpace(onlyAlignment) ? "" : "（筛选：" + onlyAlignment + "）") + "。");

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
