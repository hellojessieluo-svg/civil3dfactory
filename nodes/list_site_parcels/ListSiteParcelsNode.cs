using System;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeListSiteParcels(JsonObject args, Document doc)
            => ListSiteParcels(args, doc);

        // 宗地清点（只读）：逐站点报宗地数+逐宗名称/面积，供台田成环与 polygonize 基准对账。
        // 注：路线挪站点托管 API 没有（MoveToSite 只在 FeatureLine 上）——路线进站点
        // 走 Prospector 多选右键"移动到站点"，是人的动作；本 op 负责之后的对账。
        static JsonNode ListSiteParcels(JsonObject a, Document doc)
        {
            string only = GetString(a, "site", null);
            Database db = doc.Database;
            var civ = Civ(db);
            var sites = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId sid in civ.GetSiteIds())
                {
                    if (tr.GetObject(sid, OpenMode.ForRead) is not Site s) continue;
                    if (only != null && s.Name != only) continue;
                    var parcels = new JsonArray();
                    double areaSum = 0;
                    foreach (ObjectId pid in s.GetParcelIds())
                    {
                        if (tr.GetObject(pid, OpenMode.ForRead) is not Parcel p) continue;
                        double area = 0;
                        try { area = p.Area; } catch { }
                        areaSum += area;
                        parcels.Add(new JsonObject { ["name"] = p.Name, ["area"] = Math.Round(area, 1) });
                    }
                    sites.Add(new JsonObject
                    {
                        ["site"] = s.Name,
                        ["parcels"] = parcels.Count,
                        ["area_sum"] = Math.Round(areaSum, 1),
                        ["parcel_list"] = parcels
                    });
                }
                tr.Commit();
            }
            return new JsonObject { ["sites"] = sites };
        }
    }
}
