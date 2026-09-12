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
    /// create_corridor_regions（S04）：建**多区域**走廊，每段用各自的装配。
    ///
    /// 既有 create_corridor 只能建单区域单装配，而本项目一条通道要分 1~7 段，
    /// 每段按左右工况（常规/荷花/交叉口）挑不同装配 —— B1 就有 7 段。
    ///
    /// 建法：CorridorCollection.Add(名) 建空壳 → Baselines.Add(基线, 路线, 设计纵断面)
    ///       → BaselineRegions.Add(区域名, 装配, 起桩号, 止桩号) 逐段加。
    /// 目标：曲面槽 → 原地形；偏移槽 → {通道}_左 / {通道}_右（S02 的产物）。
    ///
    /// 桩号会按路线实际范围裁剪：参数表里的桩号可能来自旧线位
    /// （B1 改线后从 3308.655 缩到 3285.119），超出的段会被裁掉或丢弃并在 notes 里报出来。
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeCreateCorridorRegions(JsonObject args, Document doc)
            => CreateCorridorRegions(args, doc);

        public static JsonNode CreateCorridorRegions(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string sfName = Need(a, "surface");
            string corridorName = GetString(a, "name", alName + "_走廊");
            string baselineName = GetString(a, "baseline", alName + "_基线");
            JsonArray regions = a["regions"] as JsonArray;
            if (regions == null || regions.Count == 0)
                throw new InvalidOperationException("regions 不能为空：需要 [{assembly,start,end,name?}, ...]");

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
                if (al == null) throw new InvalidOperationException("找不到路线 '" + alName + "'。");
                double s0 = al.StartingStation, s1 = al.EndingStation;

                ObjectId fgId = ObjectId.Null;
                foreach (ObjectId pid in al.GetProfileIds())
                {
                    var p = tr.GetObject(pid, OpenMode.ForRead) as CivProfile;
                    if (p != null && p.ProfileType == CivProfileType.FG) { fgId = pid; break; }
                }
                if (fgId.IsNull)
                    throw new InvalidOperationException("路线 '" + alName + "' 没有设计纵断面，先跑 create_design_profiles。");

                ObjectId sfId = FindSurfaceId(tr, civ, sfName);
                if (sfId.IsNull) throw new InvalidOperationException("找不到曲面 '" + sfName + "'。");

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
                    if (x.Name == alName + "_左") leftId = aid;
                    else if (x.Name == alName + "_右") rightId = aid;
                }
                if (leftId.IsNull || rightId.IsNull)
                    notes.Add("缺少宽度目标（" + alName + "_左/" + alName + "_右），走廊将只按装配自身放坡");

                ObjectId corridorId = civ.CorridorCollection.Add(corridorName);
                var corridor = tr.GetObject(corridorId, OpenMode.ForWrite) as CivCorridor;
                if (corridor == null) throw new InvalidOperationException("走廊建出来但取不到对象。");

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
                    { notes.Add("第 " + idx + " 段没写 assembly，跳过"); skipped++; continue; }
                    if (!asmByName.ContainsKey(asmName))
                    { notes.Add("第 " + idx + " 段装配 '" + asmName + "' 图里没有，跳过"); skipped++; continue; }

                    double st = GetDouble(r, "start", double.NaN);
                    double en = GetDouble(r, "end", double.NaN);
                    if (double.IsNaN(st)) st = s0;
                    if (double.IsNaN(en)) en = s1;
                    double rawSt = st, rawEn = en;
                    if (st < s0) st = s0;
                    if (en > s1) en = s1;
                    if (en - st < 1e-6)
                    {
                        notes.Add("第 " + idx + " 段 " + Math.Round(rawSt, 3) + "~" + Math.Round(rawEn, 3) +
                                  " 完全落在路线范围 [" + Math.Round(s0, 3) + "," + Math.Round(s1, 3) + "] 之外，丢弃");
                        skipped++; continue;
                    }
                    // 阈值取 1mm：路线端点本身有亚毫米浮点差，用 1e-6 会把无害的裁剪全报出来
                    if (Math.Abs(rawSt - st) > 0.001 || Math.Abs(rawEn - en) > 0.001)
                        notes.Add("第 " + idx + " 段桩号被裁剪：" + Math.Round(rawSt, 3) + "~" + Math.Round(rawEn, 3) +
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
                    { notes.Add("第 " + idx + " 段建区域失败：" + ex.Message); skipped++; }
                }

                if (added.Count == 0)
                    throw new InvalidOperationException("一个区域都没建成，走廊无效。");

                corridor.Rebuild();

                // 设目标
                var targets = corridor.GetTargets();
                var sfIds = new ObjectIdCollection { sfId };
                var offIds = new ObjectIdCollection();
                if (!leftId.IsNull) offIds.Add(leftId);
                if (!rightId.IsNull) offIds.Add(rightId);
                int sCount = 0, oCount = 0;
                foreach (CivTargetInfo t in targets)
                {
                    string tt = t.TargetType.ToString();
                    if (tt == "Surface") { t.TargetIds = sfIds; sCount++; }
                    else if (tt == "Offset" && offIds.Count > 0) { t.TargetIds = offIds; oCount++; }
                }
                corridor.SetTargets(targets);
                corridor.Rebuild();

                var codes = new JsonArray();
                try { foreach (string c in corridor.GetLinkCodes()) codes.Add(c); }
                catch (System.Exception) { }

                // 「安静地成功 = 没成功」护栏（2026-08-13 项目A实图教训）：
                // 放坡子装配（RiverSlope PKT）执行了才会声明曲面目标槽并产出 slope-*/bottom-* 链接码；
                // 一个曲面目标都没有 = 子装配没跑（典型：PKT 未嵌入图纸 UseEmbeddedProject=False、外部 .pkt 找不到），
                // 走廊只剩 ZCD 退化面，后面 62 步全 ok=true 而方量全 0。这里直接拦下。
                if (sCount == 0 && !GetBool(a, "allow_no_surface_targets", false))
                    throw new InvalidOperationException(
                        "走廊 '" + corridorName + "' 建成后没有任何曲面目标槽（surface_targets_set=0），链接码只有 [" +
                        string.Join(",", codes.Select(c => c?.ToString())) + "]。放坡子装配没有执行——先在 Civil 3D 界面里核对装配的子装配状态" +
                        "（Status=FileNotFound / UseEmbeddedProject=False 就是它），重导 PKT 并嵌入图纸后再跑；" +
                        "确认装配本来就不搜地面时传 allow_no_surface_targets:true 放行。");

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
