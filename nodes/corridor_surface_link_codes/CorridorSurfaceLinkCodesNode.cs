using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using CivCorridorSurface = Autodesk.Civil.DatabaseServices.CorridorSurface;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        /// <summary>
        ///
        /// </summary>
        static JsonNode RunNodeCorridorSurfaceLinkCodes(JsonObject a, Document doc)
        {
            var corridors = new HashSet<string>();
            if (a["corridors"] is JsonArray ca) foreach (JsonNode n in ca) if (n != null) corridors.Add(n.ToString());
            var surfaces = new HashSet<string>();
            if (a["surfaces"] is JsonArray sa) foreach (JsonNode n in sa) if (n != null) surfaces.Add(n.ToString());
            var remove = new List<string>();
            if (a["remove"] is JsonArray ra) foreach (JsonNode n in ra) if (n != null) remove.Add(n.ToString());
            bool dry = GetBool(a, "dry_run", true);
            bool rebuild = GetBool(a, "rebuild", true);

            Database db = doc.Database;
            var rows = new JsonArray();
            int removedTotal = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId eid in ModelSpace(db, tr))
                {
                    var cor = tr.GetObject(eid, OpenMode.ForRead) as CivCorridor;
                    if (cor == null) continue;
                    if (corridors.Count > 0 && !corridors.Contains(cor.Name)) continue;
                    bool changed = false;
                    var surfRows = new JsonArray();
                    foreach (CivCorridorSurface cs in cor.CorridorSurfaces)
                    {
                        if (surfaces.Count > 0 && !surfaces.Contains(cs.Name)) continue;
                        var before = new List<string>(cs.LinkCodes());
                        var removed = new JsonArray();
                        foreach (string code in remove)
                        {
                            if (!before.Contains(code)) continue;
                            if (!dry)
                            {
                                if (!changed) cor.UpgradeOpen();
                                cs.RemoveLinkCode(code);
                                changed = true;
                            }
                            removed.Add(code);
                            removedTotal++;
                        }
                        var row = new JsonObject
                        {
                            ["surface"] = cs.Name,
                            ["link_codes"] = LinkCodeArr(before),
                            ["removed"] = removed
                        };
                        if (!dry && removed.Count > 0) row["link_codes_after"] = LinkCodeArr(new List<string>(cs.LinkCodes()));
                        surfRows.Add(row);
                    }
                    if (changed && rebuild) cor.Rebuild();
                    rows.Add(new JsonObject { ["corridor"] = cor.Name, ["rebuilt"] = changed && rebuild, ["surfaces"] = surfRows });
                }
                tr.Commit();
            }
            return new JsonObject
            {
                ["dry_run"] = dry,
                ["remove"] = LinkCodeArr(remove),
                ["removed_total"] = removedTotal,
                ["corridors"] = rows
            };
        }

        static JsonArray LinkCodeArr(IEnumerable<string> xs)
        {
            var arr = new JsonArray();
            foreach (string x in xs) arr.Add(x);
            return arr;
        }
    }
}
