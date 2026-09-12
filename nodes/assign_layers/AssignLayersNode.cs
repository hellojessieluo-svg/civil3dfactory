using System;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeAssignLayers(JsonObject args, Document doc)
            => AssignLayers(args, doc);

        // 批量归层：按分组把路线（按名）或任意实体（按句柄）挪到指定图层，图层不存在就建。
        // 只动 Layer 一个属性，几何/样式/挂接一概不碰。
        static JsonNode AssignLayers(JsonObject a, Document doc)
        {
            var groups = a["groups"] as JsonArray;
            if (groups == null || groups.Count == 0)
                throw new InvalidOperationException("需要 groups:[{layer,color?,alignments?[],handles?[]},...]");

            Database db = doc.Database;
            var civ = Civ(db);
            var report = new JsonArray();
            int movedTotal = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (JsonNode gn in groups)
                {
                    var g = (JsonObject)gn;
                    string layer = Need(g, "layer");
                    short color = (short)GetDouble(g, "color", 7);

                    ObjectId layerId;
                    if (lt.Has(layer)) layerId = lt[layer];
                    else
                    {
                        lt.UpgradeOpen();
                        var rec = new LayerTableRecord
                        {
                            Name = layer,
                            Color = Color.FromColorIndex(ColorMethod.ByAci, color)
                        };
                        layerId = lt.Add(rec);
                        tr.AddNewlyCreatedDBObject(rec, true);
                        lt.DowngradeOpen();
                    }

                    int moved = 0;
                    var missing = new JsonArray();
                    if (g["alignments"] is JsonArray als)
                        foreach (JsonNode n in als)
                        {
                            var al = FindAlignment(tr, civ, n.GetValue<string>());
                            if (al == null) { missing.Add(n.GetValue<string>()); continue; }
                            var w = (CivAlignment)tr.GetObject(al.ObjectId, OpenMode.ForWrite);
                            if (w.LayerId != layerId) { w.LayerId = layerId; moved++; }
                        }
                    if (g["handles"] is JsonArray hs)
                        foreach (JsonNode n in hs)
                        {
                            string h = n.GetValue<string>();
                            if (!db.TryGetObjectId(new Handle(Convert.ToInt64(h, 16)), out ObjectId id))
                            { missing.Add(h); continue; }
                            if (tr.GetObject(id, OpenMode.ForWrite) is not Entity e)
                            { missing.Add(h); continue; }
                            if (e.LayerId != layerId) { e.LayerId = layerId; moved++; }
                        }
                    movedTotal += moved;
                    var line = new JsonObject { ["layer"] = layer, ["moved"] = moved };
                    if (missing.Count > 0) line["missing"] = missing;
                    report.Add(line);
                }
                tr.Commit();
            }
            return new JsonObject { ["groups"] = report, ["moved_total"] = movedTotal };
        }
    }
}
