using System;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeSetOffsetWidth(JsonObject args, Document doc)
            => SetOffsetWidth(args, doc);

        // 改通道半宽（无头版）：主线全部偏移子线 NominalOffset=±width。
        // 报账带改后回读值——专防"快照属性赋值不落库"的安静失败。
        static JsonNode SetOffsetWidth(JsonObject a, Document doc)
        {
            string mainName = Need(a, "alignment");
            double width = GetDouble(a, "width", 15);

            Database db = doc.Database;
            var civ = Civ(db);
            var report = new JsonArray();
            int moved = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var host = FindAlignment(tr, civ, mainName);
                if (host == null) throw new InvalidOperationException("找不到主线 " + mainName);

                foreach (ObjectId aid in civ.GetAlignmentIds())
                {
                    if (tr.GetObject(aid, OpenMode.ForRead) is not CivAlignment seg) continue;
                    Autodesk.Civil.DatabaseServices.OffsetAlignmentInfo oi;
                    try { oi = seg.OffsetAlignmentInfo; } catch { continue; }
                    if (oi == null || oi.ParentAlignmentId != host.ObjectId) continue;
                    double cur = oi.NominalOffset;
                    double tgt = Math.Sign(cur) * width;
                    var segW = (CivAlignment)tr.GetObject(aid, OpenMode.ForWrite);
                    segW.OffsetAlignmentInfo.NominalOffset = tgt;
                    double back = double.NaN;
                    try { back = segW.OffsetAlignmentInfo.NominalOffset; } catch { }
                    moved++;
                    report.Add(new JsonObject
                    {
                        ["segment"] = seg.Name,
                        ["before"] = Math.Round(cur, 3),
                        ["set_to"] = Math.Round(tgt, 3),
                        ["read_back"] = Math.Round(back, 3),
                        ["persisted"] = Math.Abs(back - tgt) < 1e-6
                    });
                }
                tr.Commit();
            }
            return new JsonObject { ["alignment"] = mainName, ["width"] = width, ["segments"] = moved, ["details"] = report };
        }

        static JsonNode RunNodeListOffsetWidths(JsonObject args, Document doc)
            => ListOffsetWidths(doc);

        // 偏移宽度清单（只读）：每条偏移路线的父线名/自名/NominalOffset/区间数。
        static JsonNode ListOffsetWidths(Document doc)
        {
            Database db = doc.Database;
            var civ = Civ(db);
            var rows = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var nameById = new System.Collections.Generic.Dictionary<ObjectId, string>();
                foreach (ObjectId id in civ.GetAlignmentIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is CivAlignment x) nameById[id] = x.Name;
                foreach (ObjectId id in civ.GetAlignmentIds())
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not CivAlignment al) continue;
                    Autodesk.Civil.DatabaseServices.OffsetAlignmentInfo oi;
                    try { oi = al.OffsetAlignmentInfo; } catch { continue; }
                    if (oi == null) continue;
                    nameById.TryGetValue(oi.ParentAlignmentId, out string parent);
                    int regions = 0;
                    try { regions = oi.Regions.Count; } catch { }
                    rows.Add(new JsonObject
                    {
                        ["offset"] = al.Name,
                        ["handle"] = al.Handle.ToString(),
                        ["parent"] = parent ?? "?",
                        ["nominal_offset"] = Math.Round(oi.NominalOffset, 3),
                        ["regions"] = regions
                    });
                }
                tr.Commit();
            }
            return new JsonObject { ["offsets"] = rows.Count, ["details"] = rows };
        }
    }
}
