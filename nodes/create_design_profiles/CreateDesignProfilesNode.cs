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
    /// create_design_profiles（S03）：给每条通道建地面线 + 设计线。
    ///
    /// 设计线不是纯平坡：端部地面高于设计底高程时，按 end_slope（默认 1:10）放坡上去接现状地形，
    /// 坡长 = 高差 × end_slope。这条规律是从既有模型的 PVI 反算出来的：
    ///   1(E) 起点 3.7793 / 坡长 7.793、1(W) 3.6441 / 6.441、B1 4.2847 / 12.847、
    ///   1(E) 终点 4.7178 / 17.178 —— 四处全是 1:10。
    /// 端部地面本来就在底高程附近的（3#、Y2、Y3、4#），自动退化成全线平坡。
    ///
    /// 端部地面高程从原地形曲面取（FindElevationAtXY），不写死。
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
            double tol = GetDouble(a, "ramp_tolerance", 0.05);      // 高差小于它就不放坡
            string gTpl = GetString(a, "ground_name", "{channel}-地面");
            string dTpl = GetString(a, "design_name", "{channel}-设计");
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
                if (sfId.IsNull) throw new InvalidOperationException("找不到曲面 '" + surfName + "'。");
                var tin = tr.GetObject(sfId, OpenMode.ForRead) as CivTin;
                if (tin == null) throw new InvalidOperationException("'" + surfName + "' 不是 TIN 曲面。");

                ObjectId gStyleId = FindStyleId(tr, civ.Styles.ProfileStyles, gStyle);
                ObjectId dStyleId = FindStyleId(tr, civ.Styles.ProfileStyles, dStyle);
                ObjectId labelId = FindStyleId(tr,
                    civ.Styles.LabelSetStyles.ProfileLabelSetStyles, labelSet);

                // 认中心线的办法：它得有配套的 {名}_左 / {名}_右 宽度目标（S02 的产物）。
                // 不能只靠"没有 _左/_右 后缀"排除 —— 图里可能残留旧模型的偏移路线
                // （如 路线(3)-左-25.000、1(E)#-Left-25.000），它们不带这个后缀，
                // 会被误当中心线，白建一堆纵断面（实测多建了 12 条）。
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
                    if (al.Name.EndsWith("_左", StringComparison.Ordinal) ||
                        al.Name.EndsWith("_右", StringComparison.Ordinal)) continue;
                    if (!allNames.Contains(al.Name + "_左") && !allNames.Contains(al.Name + "_右"))
                    { notes.Add("跳过 " + al.Name + "：没有配套的 _左/_右 宽度目标，不认为是中心线"); continue; }
                    targets.Add(al);
                }

                foreach (CivAlign al in targets)
                {
                    string ch = al.Name;
                    string gName = gTpl.Replace("{channel}", ch);
                    string dName = dTpl.Replace("{channel}", ch);
                    double s0 = al.StartingStation, s1 = al.EndingStation;

                    // 已有同名纵断面：先删（纵断面没有"改名腾位再删"的必要，
                    // 它是本节点自己的产物，重跑即重建）
                    foreach (ObjectId pid in al.GetProfileIds())
                    {
                        var p = tr.GetObject(pid, OpenMode.ForRead) as CivProfile;
                        if (p == null) continue;
                        if (p.Name != gName && p.Name != dName) continue;
                        if (!replaceExisting)
                        { notes.Add(ch + " 已有纵断面 " + p.Name + "，未替换"); continue; }
                        try { ((CivProfile)tr.GetObject(pid, OpenMode.ForWrite)).Erase(); }
                        catch (System.Exception ex) { notes.Add(ch + " 删旧纵断面失败：" + ex.Message); }
                    }

                    try { CivProfile.CreateFromSurface(gName, al.ObjectId, sfId, db.Clayer, gStyleId, labelId); }
                    catch (System.Exception ex)
                    { notes.Add(ch + " 建地面线失败：" + ex.Message); }

                    // 两端地面高程
                    double gStart = CdpGround(tin, al, s0);
                    double gEnd = CdpGround(tin, al, s1);

                    ObjectId dId;
                    try { dId = CivProfile.CreateByLayout(dName, al.ObjectId, db.Clayer, dStyleId, labelId); }
                    catch (System.Exception ex)
                    { notes.Add(ch + " 建设计线失败：" + ex.Message); continue; }

                    var design = tr.GetObject(dId, OpenMode.ForWrite) as CivProfile;
                    if (design == null) { notes.Add(ch + " 设计线取不到对象"); continue; }

                    // 放不放坡是**逐端的设计决定**，不能由地形推。
                    // 实测既有模型：1(E) 两端放、1(W) 与 B1 只放起点、3#/Y2/Y3/4# 两端都不放——
                    // 而后四条两端地面也在 3.9~5.0m，照地形推会全放坡，与设计不符。
                    // 所以默认不放，由 ramps 参数逐条指定，例：{"1(E)":["start","end"],"B1":["start"]}
                    bool wantS = false, wantE = false;
                    JsonObject ramps = a["ramps"] as JsonObject;
                    if (ramps != null && ramps.ContainsKey(ch))
                    {
                        var arr = ramps[ch] as JsonArray;
                        if (arr != null)
                            foreach (JsonNode n in arr)
                            {
                                string v = n == null ? "" : n.ToString().Trim().ToLowerInvariant();
                                if (v == "start" || v == "起点") wantS = true;
                                else if (v == "end" || v == "终点") wantE = true;
                                else if (v == "both" || v == "两端") { wantS = true; wantE = true; }
                            }
                    }

                    double rampS = 0.0, rampE = 0.0;
                    bool hasS = wantS && !double.IsNaN(gStart) && gStart - designElev > tol;
                    bool hasE = wantE && !double.IsNaN(gEnd) && gEnd - designElev > tol;
                    if (wantS && !hasS) notes.Add(ch + " 起点要求放坡，但地面 " +
                        (double.IsNaN(gStart) ? "采不到" : Math.Round(gStart, 3) + " 高出不足 " + tol + "m") + "，按平接处理");
                    if (wantE && !hasE) notes.Add(ch + " 终点要求放坡，但地面 " +
                        (double.IsNaN(gEnd) ? "采不到" : Math.Round(gEnd, 3) + " 高出不足 " + tol + "m") + "，按平接处理");
                    if (hasS) rampS = (gStart - designElev) * endSlope;
                    if (hasE) rampE = (gEnd - designElev) * endSlope;
                    // 两端坡长加起来超过路线全长就放弃放坡，退回平坡
                    if (rampS + rampE >= (s1 - s0))
                    {
                        notes.Add(ch + " 端部坡长合计 " + Math.Round(rampS + rampE, 2) +
                                  "m 超过路线全长，退回平坡");
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
                    { notes.Add(ch + " 写 PVI 失败：" + ex.Message); continue; }

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

        /// <summary>取路线某桩号中心点处的曲面高程；采不到返回 NaN。</summary>
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
