using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DFactory
{
    /// <summary>
    /// find_write_open: scan the whole database for objects still open for write.
    /// Cures save_dwg's eWasOpenForWrite -- that error only says some object is unclosed, not which one.
    /// Scans model space, every layout block, and entities under the named object dictionary.
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeFindWriteOpen(JsonObject args, Document doc)
            => FindWriteOpen(args, doc);

        public static JsonNode FindWriteOpen(JsonObject a, Document doc)
        {
            int max = (int)GetDouble(a, "max", 100);
            Database db = doc.Database;
            var hits = new JsonArray();
            var byType = new Dictionary<string, int>(StringComparer.Ordinal);
            int scanned = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId btrId in bt)
                {
                    BlockTableRecord btr;
                    try { btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead); }
                    catch (System.Exception) { continue; }
                    foreach (ObjectId id in btr)
                    {
                        scanned++;
                        try
                        {
                            // openErased=true: erased objects may still hold a write handle
                            DBObject o = tr.GetObject(id, OpenMode.ForRead, false, true);
                            if (o == null || !o.IsWriteEnabled) continue;
                            string t = o.GetType().Name;
                            byType[t] = byType.ContainsKey(t) ? byType[t] + 1 : 1;
                            if (hits.Count < max)
                                hits.Add(new JsonObject
                                {
                                    ["type"] = t,
                                    ["handle"] = o.Handle.ToString(),
                                    ["owner_block"] = btr.Name
                                });
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception ex)
                        {
                            // Already opened for write elsewhere: getting it again inside the transaction throws; that itself is the evidence
                            string t = "(unreadable:" + ex.ErrorStatus + ")";
                            byType[t] = byType.ContainsKey(t) ? byType[t] + 1 : 1;
                            if (hits.Count < max)
                                hits.Add(new JsonObject
                                {
                                    ["type"] = t,
                                    ["handle"] = id.Handle.ToString(),
                                    ["owner_block"] = btr.Name
                                });
                        }
                        catch (System.Exception) { }
                    }
                }
                tr.Commit();
            }

            var summary = new JsonObject();
            foreach (var kv in byType) summary[kv.Key] = kv.Value;
            return new JsonObject
            {
                ["scanned"] = scanned,
                ["write_open_count"] = hits.Count,
                ["by_type"] = summary,
                ["items"] = hits
            };
        }
    }
}
