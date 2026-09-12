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

        // Set the channel half-width (headless): every offset child of the main alignment gets NominalOffset=+/-width.
        // The report carries the read-back value -- guards against the silent failure of "snapshot property assignment not persisted".
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
                if (host == null) throw new InvalidOperationException("Main alignment not found: " + mainName);

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

        // Offset width list (read-only): parent name / own name / NominalOffset / region count of every offset alignment.
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
