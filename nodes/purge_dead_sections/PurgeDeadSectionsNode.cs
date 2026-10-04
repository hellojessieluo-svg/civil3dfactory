using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivSampleLineGroup = Autodesk.Civil.DatabaseServices.SampleLineGroup;
using CivSampleLine = Autodesk.Civil.DatabaseServices.SampleLine;
using CivSection = Autodesk.Civil.DatabaseServices.Section;

namespace Civil3DFactory
{
    /// <summary>
    ///
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodePurgeDeadSections(JsonObject a, Document doc)
        {
            string alName = GetString(a, "alignment", null);
            bool dryRun = GetBool(a, "dry_run", true);

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var groupsOut = new JsonArray();
            int totalDead = 0, totalSections = 0, totalErased = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var alIds = new List<ObjectId>();
                if (!string.IsNullOrEmpty(alName))
                {
                    CivAlignment al = FindAlignment(tr, civ, alName);
                    if (al == null) throw new InvalidOperationException("Alignment '" + alName + "'.");
                    alIds.Add(al.ObjectId);
                }
                else
                {
                    foreach (ObjectId id in civ.GetAlignmentIds()) alIds.Add(id);
                }

                foreach (ObjectId alId in alIds)
                {
                    var al = (CivAlignment)tr.GetObject(alId, OpenMode.ForRead);
                    foreach (ObjectId gid in al.GetSampleLineGroupIds())
                    {
                        var slg = (CivSampleLineGroup)tr.GetObject(gid, OpenMode.ForWrite);
                        int lines = 0, sections = 0, dead = 0, erased = 0, unreadable = 0;
                        var deadByType = new Dictionary<string, int>();
                        var samples = new JsonArray();
                        foreach (ObjectId slId in slg.GetSampleLineIds())
                        {
                            lines++;
                            var sl = (CivSampleLine)tr.GetObject(slId, OpenMode.ForRead);
                            foreach (ObjectId secId in sl.GetSectionIds())
                            {
                                sections++;
                                CivSection sec;
                                try { sec = (CivSection)tr.GetObject(secId, OpenMode.ForRead); }
                                catch { unreadable++; continue; }

                                bool isDead = false;
                                string why = "";
                                string st = "";
                                try { st = sec.SourceType.ToString(); } catch { st = "?"; }
                                ObjectId srcId = ObjectId.Null;
                                try { srcId = sec.SourceId; } catch (System.Exception ex) { isDead = true; why = "Cannot read SourceId: " + ex.GetType().Name; }
                                if (!isDead)
                                {
                                    if (srcId.IsNull) { isDead = true; why = "SourceId is null"; }
                                    else if (srcId.IsErased || !srcId.IsValid) { isDead = true; why = "Source object was erased"; }
                                    else
                                    {
                                        try { tr.GetObject(srcId, OpenMode.ForRead); }
                                        catch (System.Exception ex) { isDead = true; why = "Cannot open source object: " + ex.GetType().Name; }
                                    }
                                }
                                if (!isDead) continue;

                                dead++;
                                deadByType[st] = deadByType.TryGetValue(st, out int c) ? c + 1 : 1;
                                if (samples.Count < 8)
                                    samples.Add(new JsonObject
                                    {
                                        ["sample_line"] = sl.Name,
                                        ["section"] = secId.Handle.ToString(),
                                        ["source_type"] = st,
                                        ["why"] = why
                                    });
                                if (!dryRun)
                                {
                                    try
                                    {
                                        sec.UpgradeOpen();
                                        sec.Erase();
                                        erased++;
                                    }
                                    catch (System.Exception ex)
                                    {
                                        if (samples.Count < 12)
                                            samples.Add(new JsonObject { ["section"] = secId.Handle.ToString(), ["erase_failed"] = ex.GetType().Name + ": " + Truncate(ex.Message, 80) });
                                    }
                                }
                            }
                        }
                        var byType = new JsonObject();
                        foreach (var kv in deadByType) byType[kv.Key] = kv.Value;
                        groupsOut.Add(new JsonObject
                        {
                            ["alignment"] = al.Name,
                            ["group"] = slg.Name,
                            ["sample_lines"] = lines,
                            ["sections"] = sections,
                            ["unreadable"] = unreadable,
                            ["dead"] = dead,
                            ["dead_by_source_type"] = byType,
                            ["erased"] = erased,
                            ["samples"] = samples
                        });
                        totalDead += dead; totalSections += sections; totalErased += erased;
                    }
                }
                if (dryRun) tr.Abort(); else tr.Commit();
            }

            return new JsonObject
            {
                ["dry_run"] = dryRun,
                ["sections_total"] = totalSections,
                ["dead_total"] = totalDead,
                ["erased_total"] = totalErased,
                ["groups"] = groupsOut
            };
        }
    }
}
