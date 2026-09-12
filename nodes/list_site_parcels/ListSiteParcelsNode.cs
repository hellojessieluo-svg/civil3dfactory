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

        // Parcel inventory (read-only): per site, report the parcel count plus each parcel's name/area, for reconciling terrace ring-closure and polygonize baselines.
        // Note: there is no API to move an alignment into a site (MoveToSite exists only on FeatureLine) -- moving alignments into a site
        // is done by hand in Prospector (multi-select, right-click "Move to Site"); this op handles the reconciliation afterwards.
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
