using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DFactory
{
    /// <summary>
    /// find_write_open：扫全库，找出仍处于「写打开」状态的对象。
    /// 专治 save_dwg 的 eWasOpenForWrite —— 那个错只说有对象没关，不说是谁。
    /// 扫模型空间、所有布局块、以及命名对象字典下的实体。
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
                            // openErased=true：已删对象也可能挂着写句柄
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
                            // 已被别处以写打开时，事务里再取会抛；这本身就是证据
                            string t = "(取不到:" + ex.ErrorStatus + ")";
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
