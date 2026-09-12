using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory
{
    /// <summary>
    /// create_block_definition: define (or redefine) a block with plain geometry plus attribute definitions,
    /// and optionally insert references with attribute values. Used to build title blocks from JSON, e.g.
    /// examples/title-block-demo.dwg. Plain AutoCAD objects only, so the result plots headlessly.
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeCreateBlockDefinition(JsonObject a, Document doc)
        {
            string name = Need(a, "name");
            bool replace = GetBool(a, "replace", true);
            string layer = GetString(a, "layer", "0");
            var entities = a["entities"] as JsonObject;
            var attrs = a["attributes"] as JsonArray;
            var inserts = a["inserts"] as JsonArray;

            Database db = doc.Database;
            var drawn = new JsonArray();
            int attdefs = 0, refs = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
                EnsureLayers(db, tr, a["layers"] as JsonArray);
                if (!string.IsNullOrEmpty(layer) && layer != "0")
                    EnsureLayers(db, tr, new JsonArray { new JsonObject { ["name"] = layer } });

                BlockTableRecord btr;
                if (bt.Has(name))
                {
                    if (!replace) throw new InvalidOperationException("Block '" + name + "' already exists (pass replace:true to redefine it).");
                    btr = (BlockTableRecord)tr.GetObject(bt[name], OpenMode.ForWrite);
                    foreach (ObjectId id in btr)
                        ((Entity)tr.GetObject(id, OpenMode.ForWrite)).Erase();
                }
                else
                {
                    btr = new BlockTableRecord { Name = name };
                    bt.Add(btr);
                    tr.AddNewlyCreatedDBObject(btr, true);
                }
                btr.Origin = new Point3d(GetDouble(a, "base_x", 0), GetDouble(a, "base_y", 0), 0);

                if (entities != null) AddEntities(db, tr, btr, entities, layer, drawn);

                if (attrs != null)
                {
                    var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                    foreach (JsonNode n in attrs)
                    {
                        var s = n as JsonObject;
                        if (s == null) continue;
                        string tag = Need(s, "tag").Replace(" ", "_");
                        var ad = new AttributeDefinition(
                            new Point3d(GetDouble(s, "x", 0), GetDouble(s, "y", 0), 0),
                            GetString(s, "default", ""), tag, GetString(s, "prompt", tag), ObjectId.Null);
                        ad.Height = GetDouble(s, "height", 2.5);
                        ad.Rotation = GetDouble(s, "rotation", 0) * Math.PI / 180.0;
                        ad.WidthFactor = GetDouble(s, "width_factor", 1.0);
                        string style = GetString(s, "style", null);
                        if (!string.IsNullOrEmpty(style) && tst.Has(style)) ad.TextStyleId = tst[style];
                        string justify = (GetString(s, "justify", "left") ?? "left").ToLowerInvariant();
                        if (justify == "center") { ad.Justify = AttachmentPoint.MiddleCenter; ad.AlignmentPoint = ad.Position; }
                        else if (justify == "right") { ad.Justify = AttachmentPoint.MiddleRight; ad.AlignmentPoint = ad.Position; }
                        else if (justify == "middle-left") { ad.Justify = AttachmentPoint.MiddleLeft; ad.AlignmentPoint = ad.Position; }
                        bool mtext = GetBool(s, "mtext", false);
                        if (mtext)
                        {
                            ad.IsMTextAttributeDefinition = true;
                            var mt = ad.MTextAttributeDefinition;
                            if (mt != null)
                            {
                                mt.Width = GetDouble(s, "width", 100);
                                mt.Attachment = AttachmentPoint.TopLeft;
                                mt.Location = ad.Position;
                                mt.Contents = GetString(s, "default", "");
                                ad.MTextAttributeDefinition = mt;
                            }
                        }
                        btr.AppendEntity(ad);
                        tr.AddNewlyCreatedDBObject(ad, true);
                        string lay = GetString(s, "layer", layer);
                        if (!string.IsNullOrEmpty(lay) && lay != "0") ad.Layer = lay;
                        attdefs++;
                    }
                }

                if (inserts != null)
                {
                    var lm = LayoutManager.Current;
                    foreach (JsonNode n in inserts)
                    {
                        var s = n as JsonObject;
                        if (s == null) continue;
                        string space = GetString(s, "space", "Model");
                        BlockTableRecord target;
                        if (string.Equals(space, "Model", StringComparison.OrdinalIgnoreCase))
                            target = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                        else
                        {
                            ObjectId lid = lm.GetLayoutId(space);
                            if (lid.IsNull) throw new InvalidOperationException("Layout not found: " + space);
                            var layout = (Layout)tr.GetObject(lid, OpenMode.ForRead);
                            target = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
                        }
                        var br = new BlockReference(new Point3d(GetDouble(s, "x", 0), GetDouble(s, "y", 0), 0), btr.ObjectId)
                        {
                            ScaleFactors = new Scale3d(GetDouble(s, "scale", 1.0)),
                            Rotation = GetDouble(s, "rotation", 0) * Math.PI / 180.0
                        };
                        target.AppendEntity(br);
                        tr.AddNewlyCreatedDBObject(br, true);
                        string lay = GetString(s, "layer", layer);
                        if (!string.IsNullOrEmpty(lay) && lay != "0") br.Layer = lay;
                        var values = s["attributes"] as JsonObject;
                        foreach (ObjectId id in btr)
                        {
                            var ad = tr.GetObject(id, OpenMode.ForRead) as AttributeDefinition;
                            if (ad == null || ad.Constant) continue;
                            var ar = new AttributeReference();
                            ar.SetAttributeFromBlock(ad, br.BlockTransform);
                            if (values != null && values[ad.Tag] != null)
                                ar.TextString = values[ad.Tag].ToString();
                            br.AttributeCollection.AppendAttribute(ar);
                            tr.AddNewlyCreatedDBObject(ar, true);
                        }
                        refs++;
                    }
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["block"] = name,
                ["entities"] = drawn.Count,
                ["attribute_definitions"] = attdefs,
                ["inserted"] = refs
            };
        }
    }
}
