using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivSampleLine = Autodesk.Civil.DatabaseServices.SampleLine;
using CivSampleLineGroup = Autodesk.Civil.DatabaseServices.SampleLineGroup;
using CivSampleLineLabelGroup = Autodesk.Civil.DatabaseServices.SampleLineLabelGroup;
using CivLabelGroup = Autodesk.Civil.DatabaseServices.LabelGroup;

namespace Civil3DFactory
{
    /// <summary>
    /// 节点 restore_sample_line_labels：把被删光的采样线标签（桩号名字）重建回来。
    ///
    /// 为什么有这个零件（2026-08-27 项目B初设 2-主通道计算.dwg）：图面上采样线还在、
    /// 采样线组还在，但 11 个 SampleLineLabelGroup 实体一个不剩——跟项目C那次
    /// 「导出前清图面 ERASE 采样线族」同源（见记忆 c3d-sample-lines-traps）。
    /// 关键 API 事实：**SampleLine 上没有 LabelStyleId**，只有控制线本身外观的 StyleId；
    /// 标签是挂在组上的独立实体 SampleLineLabelGroup，靠
    /// SampleLineLabelGroup.Create(组Id, 标签样式Id) 重建，一组一个。
    /// 所以「给每条线重新赋标签样式」这条路走不通，别再试。
    ///
    /// 验收判据写进回执并当场断言：重建后标签组数 = 采样线组数，
    /// 且各组 SubEntityCount 合计 = 采样线总数。不到数就抛异常不提交，
    /// 防「安静地成功」（ok=true 但图上还是没字）。
    /// </summary>
    public static partial class Ops
    {
        static readonly string[] SampleLineLabelCats = {
            "LabelStyles.SampleLineLabelStyles.LabelStyles" };

        static JsonNode RunNodeRestoreSampleLineLabels(JsonObject a, Document doc)
        {
            string styleName = GetString(a, "style", "@C3DF-AlignmentStation");
            string onlyAlignment = GetString(a, "alignment", null);
            bool skipExisting = GetBool(a, "skip_existing", true);
            bool dry = GetBool(a, "dry_run", false);

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId styleId = FindStyleAnywhere(tr, civ, styleName, SampleLineLabelCats);
                if (styleId.IsNull)
                    throw new InvalidOperationException(
                        "图里没有采样线标签样式 '" + styleName + "'（大小写敏感）。"
                        + "先用 list_styles 看 LabelStyles.SampleLineLabelStyles.LabelStyles 有哪些。");

                // 盘点全部采样线组（模型空间扫，与 entity_stats 同源，不依赖路线挂接）
                var groups = new List<ObjectId>();
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var g = tr.GetObject(id, OpenMode.ForRead) as CivSampleLineGroup;
                    if (g == null) continue;
                    if (!string.IsNullOrEmpty(onlyAlignment))
                    {
                        string alName = null;
                        try
                        {
                            alName = TryGetName(tr.GetObject(g.ParentAlignmentId, OpenMode.ForRead));
                        }
                        catch { }
                        if (alName != onlyAlignment) continue;
                    }
                    groups.Add(id);
                }
                if (groups.Count == 0)
                    throw new InvalidOperationException("没找到采样线组"
                        + (onlyAlignment == null ? "。" : "（路线 '" + onlyAlignment + "'）。"));

                var rows = new JsonArray();
                int created = 0, skipped = 0, expectedLines = 0;

                foreach (ObjectId gid in groups)
                {
                    var g = (CivSampleLineGroup)tr.GetObject(gid, OpenMode.ForRead);
                    int lines = g.GetSampleLineIds().Count;
                    expectedLines += lines;

                    int had = CountLiveLabelGroups(tr, gid);
                    var row = new JsonObject
                    {
                        ["group"] = g.Name,
                        ["sample_lines"] = lines,
                        ["label_groups_before"] = had
                    };

                    if (had > 0 && skipExisting)
                    {
                        row["action"] = "skip";
                        skipped++;
                    }
                    else if (dry)
                    {
                        row["action"] = "would_create";
                        created++;
                    }
                    else
                    {
                        CivSampleLineLabelGroup.Create(gid, styleId);
                        row["action"] = "create";
                        created++;
                    }
                    rows.Add(row);
                }

                var res = new JsonObject
                {
                    ["style"] = styleName,
                    ["groups"] = rows,
                    ["groups_total"] = groups.Count,
                    ["created"] = created,
                    ["skipped"] = skipped,
                    ["sample_lines_total"] = expectedLines
                };

                if (dry)
                {
                    res["dry_run"] = true;
                    return res;
                }

                // ---- 当场验收：数不对就不提交 ----
                int labelGroups = 0;
                long subEntities = 0;
                foreach (ObjectId gid in groups)
                {
                    foreach (ObjectId lid in LiveLabelGroupIds(tr, gid))
                    {
                        labelGroups++;
                        var lg = tr.GetObject(lid, OpenMode.ForRead) as CivLabelGroup;
                        if (lg != null) subEntities += lg.SubEntityCount;
                    }
                }
                res["label_groups_after"] = labelGroups;
                res["sub_entities_after"] = subEntities;

                if (labelGroups < groups.Count)
                    throw new InvalidOperationException("标签组只建出 " + labelGroups
                        + " 个，应有 " + groups.Count + " 个——不提交。");
                if (subEntities != expectedLines)
                    throw new InvalidOperationException("标签条目 " + subEntities
                        + " 条 ≠ 采样线 " + expectedLines + " 条——不提交。");

                tr.Commit();
                return res;
            }
        }

        static List<ObjectId> LiveLabelGroupIds(Transaction tr, ObjectId sampleLineGroupId)
        {
            var live = new List<ObjectId>();
            ObjectIdCollection ids;
            try { ids = CivSampleLineLabelGroup.GetAvailableLabelGroupIds(sampleLineGroupId); }
            catch { return live; }
            foreach (ObjectId id in ids)
            {
                if (id.IsNull || id.IsErased) continue;
                live.Add(id);
            }
            return live;
        }

        static int CountLiveLabelGroups(Transaction tr, ObjectId sampleLineGroupId)
            => LiveLabelGroupIds(tr, sampleLineGroupId).Count;
    }
}
