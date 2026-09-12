using System;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeDrawPolylines(JsonObject args, Document doc)
            => DrawPolylines(args, doc);

        // Batch-draw polylines into the host drawing (bulge arcs supported): for derived output such as parcel boundaries.
        // clear_layers first removes old polylines on the given layers (idempotent rerun); missing layers are created.
        static JsonNode DrawPolylines(JsonObject a, Document doc)
        {
            var arr = a["polylines"] as JsonArray;
            if (arr == null || arr.Count == 0)
                throw new InvalidOperationException("polylines:[{layer?,closed?,name?,vertices:[[x,y,bulge?],...]},...] is required");
            string defLayer = GetString(a, "layer", "0");
            short defColor = (short)GetDouble(a, "color", 7);

            Database db = doc.Database;
            var report = new JsonArray();
            int drawn = 0, cleared = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                ObjectId EnsureLayer(string name, short color)
                {
                    if (lt.Has(name)) return lt[name];
                    lt.UpgradeOpen();
                    var rec = new LayerTableRecord
                    { Name = name, Color = Color.FromColorIndex(ColorMethod.ByAci, color) };
                    var id = lt.Add(rec);
                    tr.AddNewlyCreatedDBObject(rec, true);
                    lt.DowngradeOpen();
                    return id;
                }

                var ms = (BlockTableRecord)tr.GetObject(
                    ((BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead))[BlockTableRecord.ModelSpace],
                    OpenMode.ForWrite);

                if (a["clear_layers"] is JsonArray cl)
                {
                    var wanted = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (JsonNode n in cl) wanted.Add(n.GetValue<string>());
                    foreach (ObjectId id in ms)
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is not Polyline pl) continue;
                        if (!wanted.Contains(pl.Layer)) continue;
                        pl.UpgradeOpen();
                        pl.Erase();
                        cleared++;
                    }
                }

                foreach (JsonNode n in arr)
                {
                    var o = (JsonObject)n;
                    var verts = o["vertices"] as JsonArray;
                    if (verts == null || verts.Count < 2) { report.Add("Too few vertices, one polyline skipped"); continue; }
                    string layer = GetString(o, "layer", defLayer);
                    short color = (short)GetDouble(o, "color", defColor);
                    ObjectId layerId = EnsureLayer(layer, color);

                    var pl = new Polyline(verts.Count);
                    pl.SetDatabaseDefaults(db);
                    for (int i = 0; i < verts.Count; i++)
                    {
                        var v = (JsonArray)verts[i];
                        double b = v.Count > 2 ? v[2].GetValue<double>() : 0.0;
                        pl.AddVertexAt(i, new Point2d(v[0].GetValue<double>(), v[1].GetValue<double>()), b, 0, 0);
                    }
                    pl.Closed = GetBool(o, "closed", true);
                    pl.Elevation = 0;
                    pl.LayerId = layerId;
                    ms.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                    drawn++;
                    report.Add(new JsonObject
                    {
                        ["name"] = GetString(o, "name", "pl" + drawn),
                        ["handle"] = pl.Handle.ToString(),
                        ["vertices"] = verts.Count,
                        ["area"] = Math.Round(SafeArea(pl), 1)
                    });
                }
                tr.Commit();
            }
            return new JsonObject { ["drawn"] = drawn, ["cleared"] = cleared, ["items"] = report };
        }

        static double SafeArea(Polyline pl)
        {
            try { return pl.Area; } catch { return 0; }
        }
    }
}
