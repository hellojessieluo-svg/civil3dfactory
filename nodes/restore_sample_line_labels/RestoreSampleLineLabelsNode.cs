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
    /// Node restore_sample_line_labels: rebuild sample line labels (station names) that were erased.
    ///
    /// Why this part exists (2026-08-27, project B preliminary design, main channel calc dwg): the sample lines and
    /// their groups were still there, but all 11 SampleLineLabelGroup entities were gone -- same cause as project C's
    /// "ERASE the sample line family to clean the sheet before export" (see memory c3d-sample-lines-traps).
    /// Key API fact: **SampleLine has no LabelStyleId**, only the StyleId that controls the line's own look;
    /// labels are separate SampleLineLabelGroup entities attached to the group, rebuilt via
    /// SampleLineLabelGroup.Create(groupId, labelStyleId), one per group.
    /// So "re-assign a label style to each line" is a dead end; do not try it again.
    ///
    /// Acceptance criteria are reported and asserted on the spot: label groups after rebuild = sample line groups,
    /// and the sum of SubEntityCount = total sample lines. If short, throw and do not commit,
    /// to prevent a "silent success" (ok=true but still no text in the drawing).
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
                        "Sample line label style '" + styleName + "' not found in the drawing (case sensitive). "
                        + "Use list_styles to see what is under LabelStyles.SampleLineLabelStyles.LabelStyles.");

                // Inventory all sample line groups (model space scan, same as entity_stats, independent of alignment linkage)
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
                    throw new InvalidOperationException("No sample line group found"
                        + (onlyAlignment == null ? "." : " (alignment '" + onlyAlignment + "')."));

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

                // ---- On-the-spot acceptance: do not commit if the counts are off ----
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
                    throw new InvalidOperationException("Only " + labelGroups + " label groups were created"
                        + ", expected " + groups.Count + " -- not committing.");
                if (subEntities != expectedLines)
                    throw new InvalidOperationException("Label entries " + subEntities
                        + " != sample lines " + expectedLines + " -- not committing.");

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
