using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DFactory
{
    /// <summary>
    /// erase_entities：按句柄删实体。
    ///
    /// 删之前先把每个对象**长什么样**报出来（类型/图层/高程/顶点数/长度/面积），
    /// 删完这份清单还在结果里——不可逆的事，至少要留得下证据。
    /// dry_run:true 只报不删，用来核对句柄有没有点错。
    ///
    /// 只按句柄删，不提供按图层/按类型批量删的入口：那种口子太容易一次清掉一整层。
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeEraseEntities(JsonObject a, Document doc)
        {
            var handles = a["handles"] as JsonArray;
            if (handles == null || handles.Count == 0)
                throw new InvalidOperationException("handles 必需，且只能按句柄删（不支持按图层批量删）。");
            bool dryRun = GetBool(a, "dry_run", false);

            var wanted = new List<string>();
            foreach (JsonNode h in handles)
                if (h != null && !string.IsNullOrWhiteSpace(h.ToString())) wanted.Add(h.ToString().Trim());

            Database db = doc.Database;
            var done = new JsonArray();
            var missing = new JsonArray();
            int erased = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (string hs in wanted)
                {
                    ObjectId id = ResolveHandle(db, hs);
                    if (id.IsNull || id.IsErased) { missing.Add((JsonNode)hs); continue; }

                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) { missing.Add((JsonNode)hs); continue; }

                    var info = new JsonObject
                    {
                        ["handle"] = hs,
                        ["type"] = ent.GetType().Name,
                        ["layer"] = ent.Layer
                    };
                    var pl = ent as Polyline;
                    if (pl != null)
                    {
                        info["vertices"] = pl.NumberOfVertices;
                        info["closed"] = pl.Closed;
                        info["elevation"] = Round(pl.Elevation, 3);
                        try { info["length"] = Round(pl.Length, 3); } catch (System.Exception) { }
                        try { info["area"] = Round(pl.Area, 3); } catch (System.Exception) { }
                    }
                    var ln = ent as Line;
                    if (ln != null)
                    {
                        info["length"] = Round(ln.Length, 3);
                        info["start"] = new JsonArray { Round(ln.StartPoint.X, 3), Round(ln.StartPoint.Y, 3), Round(ln.StartPoint.Z, 3) };
                        info["end"] = new JsonArray { Round(ln.EndPoint.X, 3), Round(ln.EndPoint.Y, 3), Round(ln.EndPoint.Z, 3) };
                    }
                    var txt = ent as DBText;
                    if (txt != null) info["text"] = txt.TextString;

                    if (!dryRun)
                    {
                        ent.UpgradeOpen();
                        try
                        {
                            ent.Erase();
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception ex)
                        {
                            // 站点拓扑护着的要素线（eNotApplicable）：摘出站点再删
                            if (ent is Autodesk.Civil.DatabaseServices.FeatureLine)
                            {
                                Autodesk.Civil.DatabaseServices.FeatureLine.MoveToNoneSite(ent.ObjectId);
                                ent.Erase();
                                info["note"] = "站点要素线，已摘出站点后删除";
                            }
                            else throw new InvalidOperationException(
                                ent.GetType().Name + " " + ent.Handle + " 拒删：" + ex.ErrorStatus);
                        }
                        erased++;
                    }
                    done.Add(info);
                }
                tr.Commit();
            }

            // 断言：点名了却一个都没找到 = 句柄给错了，不能安静地报成功
            if (done.Count == 0)
                throw new InvalidOperationException(
                    "点名的 " + wanted.Count + " 个句柄一个都没在图上找到，什么都没删。");

            return new JsonObject
            {
                ["dry_run"] = dryRun,
                ["erased"] = erased,
                ["not_found"] = missing,
                ["details"] = done
            };
        }
    }
}
