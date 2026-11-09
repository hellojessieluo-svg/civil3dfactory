using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodePlaceElevationPoints(JsonObject a, Document doc)
        {
            var raw = new List<Point3d>();
            if (a["points"] is JsonArray pa) foreach (JsonNode p in pa) raw.Add(new Point3d(p[0].GetValue<double>(), p[1].GetValue<double>(), p[2].GetValue<double>()));
            string file = GetString(a, "points_file", null);
            if (!string.IsNullOrEmpty(file))
            {
                string text = File.ReadAllText(file);
                if (text.TrimStart().StartsWith("["))
                    foreach (JsonNode p in (JsonArray)JsonNode.Parse(text)) raw.Add(new Point3d(p[0].GetValue<double>(), p[1].GetValue<double>(), p[2].GetValue<double>()));
                else
                    foreach (string line in File.ReadAllLines(file))
                    {
                        var f = line.Split(new[] { ',', '\t', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
                        if (f.Length < 3) continue;
                        if (double.TryParse(f[f.Length - 3], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                            && double.TryParse(f[f.Length - 2], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                            && double.TryParse(f[f.Length - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                            raw.Add(new Point3d(x, y, z));
                    }
            }
            if (raw.Count == 0) throw new InvalidOperationException("no points: give points or points_file");

            string layer = GetString(a, "layer", "V-SPOT");
            string labelLayer = GetString(a, "label_layer", layer + "-TEXT");
            string blockName = GetString(a, "block", null);
            double blockScale = GetDouble(a, "block_scale", 1.0);
            bool label = GetBool(a, "label", true);
            bool placePoints = GetBool(a, "points_only", true);
            double h = GetDouble(a, "text_height", 1.0);
            int dec = (int)GetDouble(a, "decimals", 2);
            double dx = GetDouble(a, "dx", 0.6 * h);
            double dy = GetDouble(a, "dy", -0.5 * h);
            string styleName = GetString(a, "text_style", "txt1");
            bool clear = GetBool(a, "clear", true);
            short color = (short)GetDouble(a, "color", 256);
            string fmt = "F" + dec;

            Database db = doc.Database;
            var warn = new JsonArray();
            int nPts = 0, nTxt = 0, cleared = 0;
            string pointKind = "none";
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                EnsureLayer(db, tr, layer, 7);
                if (label) EnsureLayer(db, tr, labelLayer, 7);
                if (clear)
                {
                    foreach (ObjectId id in ms)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        bool hit = string.Equals(ent.Layer, layer, StringComparison.OrdinalIgnoreCase) && placePoints
                                   || label && string.Equals(ent.Layer, labelLayer, StringComparison.OrdinalIgnoreCase);
                        if (hit) { ent.UpgradeOpen(); ent.Erase(); cleared++; }
                    }
                }
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                ObjectId blockId = ObjectId.Null;
                if (placePoints && !string.IsNullOrEmpty(blockName))
                {
                    if (bt.Has(blockName)) blockId = bt[blockName];
                    else warn.Add("block not in drawing: " + blockName + "; placing point objects instead");
                }
                if (placePoints) pointKind = blockId.IsNull ? "DBPoint" : "block:" + blockName;
                ObjectId styleId = ObjectId.Null;
                var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                if (label)
                {
                    if (tst.Has(styleName)) styleId = tst[styleName];
                    else warn.Add("text style not in drawing: " + styleName + "; using the current style");
                }
                foreach (var p in raw)
                {
                    if (placePoints)
                    {
                        Entity e;
                        if (!blockId.IsNull) e = new BlockReference(p, blockId) { ScaleFactors = new Scale3d(blockScale) };
                        else e = new DBPoint(p);
                        e.Layer = layer;
                        if (color != 256) e.ColorIndex = color;
                        ms.AppendEntity(e); tr.AddNewlyCreatedDBObject(e, true); nPts++;
                    }
                    if (label)
                    {
                        var t = new DBText();
                        t.SetDatabaseDefaults();
                        t.Layer = labelLayer;
                        if (!styleId.IsNull) t.TextStyleId = styleId;
                        t.Height = h;
                        t.TextString = p.Z.ToString(fmt, CultureInfo.InvariantCulture);
                        t.Position = new Point3d(p.X + dx, p.Y + dy, p.Z);
                        if (color != 256) t.ColorIndex = color;
                        ms.AppendEntity(t); tr.AddNewlyCreatedDBObject(t, true); nTxt++;
                    }
                }
                tr.Commit();
            }
            return new JsonObject
            {
                ["input_points"] = raw.Count,
                ["points_placed"] = nPts,
                ["labels_placed"] = nTxt,
                ["point_kind"] = pointKind,
                ["layer"] = layer,
                ["label_layer"] = label ? labelLayer : null,
                ["cleared"] = cleared,
                ["warnings"] = warn
            };
        }
    }
}
