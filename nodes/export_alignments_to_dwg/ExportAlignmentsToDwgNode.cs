using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlign = Autodesk.Civil.DatabaseServices.Alignment;
using CivSub = Autodesk.Civil.DatabaseServices.AlignmentSubEntity;
using CivSubArc = Autodesk.Civil.DatabaseServices.AlignmentSubEntityArc;
using CivSubType = Autodesk.Civil.DatabaseServices.AlignmentSubEntityType;
using CivilDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// export_alignments_to_dwg: extract all (or selected) alignments as plain polylines and write them into a brand-new empty DWG.
    ///
    /// Purpose: move the design intent from Civil 3D objects onto polylines -- from then on the centerline / edge polylines are the input,
    /// and the Civil model is a downstream product. The output file contains no Civil 3D objects, styles or proxy graphics.
    ///
    /// Channel ownership is not guessed from names: offset alignments use OffsetAlignmentInfo.ParentAlignmentId for the parent channel,
    /// Side for left/right and NominalOffset for the half width (names like "Alignment(8)-L-35.000" do not reveal that it belongs to B1).
    ///
    /// Layer names are the contract of this input file, controlled by template parameters:
    ///   center_layer default "CL-{channel}"
    ///   edge_layer   default "EDGE-{channel}-{side}"
    /// Placeholders: {channel} channel name, {side} L/R, {offset} half width.
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeExportAlignmentsToDwg(JsonObject args, Document doc)
            => ExportAlignmentsToDwg(args, doc);

        sealed class EadItem
        {
            public string Name;
            public string Channel;
            public string Side;          // "" = centerline
            public double Offset;
            public double Length;
            public List<Point2d> Verts = new List<Point2d>();
            public List<double> Bulges = new List<double>();
            public int Lines, Arcs, Spirals, Sampled;
        }

        public static JsonNode ExportAlignmentsToDwg(JsonObject a, Document doc)
        {
            string outPath = Need(a, "out");
            if (!Path.IsPathRooted(outPath))
                throw new InvalidOperationException("out must be an absolute path: " + outPath);
            bool overwrite = GetBool(a, "overwrite", false);
            if (File.Exists(outPath) && !overwrite)
                throw new InvalidOperationException("File already exists, refusing to overwrite: " + outPath + " (pass overwrite:true to overwrite)");

            string centerTpl = GetString(a, "center_layer", "CL-{channel}");
            string edgeTpl = GetString(a, "edge_layer", "EDGE-{channel}-{side}");
            short centerColor = (short)GetDouble(a, "center_color", 3);      // green
            short edgeColor = (short)GetDouble(a, "edge_color", 4);          // cyan
            string centerLt = GetString(a, "center_linetype", "CENTER2");
            string edgeLt = GetString(a, "edge_linetype", "Continuous");
            double ltScale = GetDouble(a, "linetype_scale", 5.0);
            double spiralStep = GetDouble(a, "spiral_step", 5.0);
            if (spiralStep <= 1e-6) spiralStep = 5.0;
            bool centersOnly = GetBool(a, "centers_only", false);

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            JsonArray only = a["alignments"] as JsonArray;
            if (only != null)
                foreach (JsonNode n in only) if (n != null) wanted.Add(n.ToString());

            Database db = doc.Database;
            var items = new List<EadItem>();
            var skipped = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivilDoc civ = Civ(db);
                // Build the ObjectId -> name table first, so offset alignments can find their parent channel
                var nameById = new Dictionary<ObjectId, string>();
                foreach (ObjectId id in civ.GetAlignmentIds())
                {
                    var x = tr.GetObject(id, OpenMode.ForRead) as CivAlign;
                    if (x != null) nameById[id] = x.Name;
                }

                foreach (ObjectId id in civ.GetAlignmentIds())
                {
                    var al = tr.GetObject(id, OpenMode.ForRead) as CivAlign;
                    if (al == null) continue;
                    if (wanted.Count > 0 && !wanted.Contains(al.Name)) continue;

                    var it = new EadItem { Name = al.Name, Length = al.Length, Channel = al.Name, Side = "" };

                    bool isOffset = false;
                    try { isOffset = al.IsOffsetAlignment; } catch (System.Exception) { }
                    if (isOffset)
                    {
                        if (centersOnly) { skipped.Add(al.Name + " (edge, centers_only)"); continue; }
                        try
                        {
                            var info = al.OffsetAlignmentInfo;
                            string parent;
                            if (nameById.TryGetValue(info.ParentAlignmentId, out parent)) it.Channel = parent;
                            it.Side = info.Side.ToString().IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0
                                ? "L" : "R";
                            it.Offset = Math.Abs(info.NominalOffset);
                        }
                        catch (System.Exception ex)
                        {
                            skipped.Add(al.Name + " (failed to read parent alignment: " + ex.GetType().Name + ")");
                            continue;
                        }
                    }

                    if (!EadExtract(al, spiralStep, it))
                    {
                        skipped.Add(al.Name + " (no convertible geometry)");
                        continue;
                    }
                    items.Add(it);
                }
                tr.Commit();
            }

            if (items.Count == 0)
                throw new InvalidOperationException("No alignments to export.");

            // ---- Write into a brand-new empty drawing ----
            var layerCounts = new JsonObject();
            using (var nd = new Database(true, false))
            {
                nd.Insunits = UnitsValue.Meters;
                using (Transaction tr = nd.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(nd.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    foreach (EadItem it in items)
                    {
                        bool isCenter = string.IsNullOrEmpty(it.Side);
                        string layer = (isCenter ? centerTpl : edgeTpl)
                            .Replace("{channel}", it.Channel)
                            .Replace("{side}", it.Side)
                            .Replace("{offset}", it.Offset.ToString("0.###"));
                        string lt = isCenter ? centerLt : edgeLt;
                        short color = isCenter ? centerColor : edgeColor;

                        ObjectId ltId = A2PResolveLinetype(tr, nd, lt, "acadiso.lin");
                        ObjectId layerId = EadEnsureLayer(tr, nd, layer, color, ltId);

                        // Order matters: new Polyline() is bound to the host drawing's WorkingDatabase by default;
                        // setting LayerId (taken from the new drawing's layer table) before it is appended throws eWrongDatabase.
                        // AppendEntity first so it belongs to the new drawing, then set every ObjectId-bearing property.
                        var pl = new Polyline(it.Verts.Count);
                        // nd must be passed explicitly: the parameterless overload uses WorkingDatabase (host drawing),
                        // and assigning the new database's LayerId afterwards throws eWrongDatabase (fixed 2026-08-16 on a live project run)
                        pl.SetDatabaseDefaults(nd);
                        for (int i = 0; i < it.Verts.Count; i++)
                            pl.AddVertexAt(i, it.Verts[i], it.Bulges[i], 0.0, 0.0);
                        pl.Elevation = 0.0;
                        pl.Closed = false;
                        ms.AppendEntity(pl);
                        tr.AddNewlyCreatedDBObject(pl, true);

                        pl.LayerId = layerId;
                        pl.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                            Autodesk.AutoCAD.Colors.ColorMethod.ByAci, color);
                        if (!ltId.IsNull) pl.LinetypeId = ltId;
                        pl.LinetypeScale = ltScale;
                        // Redundant identity marker: the layer name is the human/machine contract, XData is the machine fallback
                        // (ownership stays recognisable after the layer is renamed or the line is moved to another layer)
                        EadSetXData(tr, nd, pl, it.Channel, isCenter ? "CENTER" : (it.Side == "L" ? "LEFT" : "RIGHT"),
                                    it.Offset, it.Name);

                        double plLen = 0.0;
                        try { plLen = pl.Length; } catch (System.Exception) { }
                        layerCounts[layer] = new JsonObject
                        {
                            ["source_alignment"] = it.Name,
                            ["channel"] = it.Channel,
                            ["side"] = isCenter ? "CL" : it.Side,
                            ["offset"] = Round(it.Offset, 3),
                            ["alignment_length"] = Round(it.Length, 4),
                            ["polyline_length"] = Round(plLen, 4),
                            ["length_delta"] = Round(plLen - it.Length, 4),
                            ["vertices"] = it.Verts.Count,
                            ["line_seg"] = it.Lines,
                            ["arc_seg"] = it.Arcs,
                            ["spiral_seg"] = it.Spirals
                        };
                    }
                    tr.Commit();
                }

                string dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                nd.SaveAs(outPath, DwgVersion.Current);
            }

            long bytes = 0;
            try { bytes = new FileInfo(outPath).Length; } catch (System.Exception) { }

            return new JsonObject
            {
                ["output"] = outPath,
                ["bytes"] = bytes,
                ["exported"] = items.Count,
                ["skipped"] = skipped,
                ["center_layer_template"] = centerTpl,
                ["edge_layer_template"] = edgeTpl,
                ["items"] = layerCounts
            };
        }

        /// <summary>Alignment -> vertex/bulge list. Lines and arcs are exact; spirals are sampled by step.
        /// Shares the geometry logic with alignment_to_polyline.</summary>
        static bool EadExtract(CivAlign al, double spiralStep, EadItem it)
        {
            Point2d tail = new Point2d(0.0, 0.0);
            bool hasTail = false;
            var ents = al.Entities;
            int n = ents.Count;
            if (n <= 0) return false;

            for (int i = 0; i < n; i++)
            {
                var ent = ents.GetEntityByOrder(i);
                for (int j = 0; j < ent.SubEntityCount; j++)
                {
                    CivSub sub = ent[j];
                    if (sub.SubEntityType == CivSubType.Arc)
                    {
                        var arc = sub as CivSubArc;
                        double bulge = 0.0;
                        if (arc != null)
                        {
                            bulge = Math.Tan(Math.Abs(arc.Delta) / 4.0);
                            if (arc.Clockwise) bulge = -bulge;
                        }
                        it.Verts.Add(sub.StartPoint); it.Bulges.Add(bulge); it.Arcs++;
                    }
                    else if (sub.SubEntityType == CivSubType.Spiral)
                    {
                        double s0 = sub.StartStation, s1 = sub.EndStation, span = s1 - s0;
                        int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(span) / spiralStep));
                        for (int k = 0; k < steps; k++)
                        {
                            double east = 0.0, north = 0.0;
                            al.PointLocation(s0 + span * k / steps, 0.0, ref east, ref north);
                            it.Verts.Add(new Point2d(east, north)); it.Bulges.Add(0.0); it.Sampled++;
                        }
                        it.Spirals++;
                    }
                    else
                    {
                        it.Verts.Add(sub.StartPoint); it.Bulges.Add(0.0); it.Lines++;
                    }
                    tail = sub.EndPoint; hasTail = true;
                }
            }
            if (it.Verts.Count == 0) return false;
            if (hasTail) { it.Verts.Add(tail); it.Bulges.Add(0.0); }
            return true;
        }

        const string EadAppName = "C3DF_CHANNEL";

        /// <summary>Write identity XData: channel name / role (CENTER|LEFT|RIGHT) / half width / source alignment name.</summary>
        static void EadSetXData(Transaction tr, Database db, Entity ent,
            string channel, string role, double offset, string source)
        {
            try
            {
                var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
                if (!rat.Has(EadAppName))
                {
                    rat.UpgradeOpen();
                    var rec = new RegAppTableRecord { Name = EadAppName };
                    rat.Add(rec);
                    tr.AddNewlyCreatedDBObject(rec, true);
                }
                ent.XData = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, EadAppName),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, channel ?? ""),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, role ?? ""),
                    new TypedValue((int)DxfCode.ExtendedDataReal, offset),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, source ?? ""));
            }
            catch (System.Exception) { }
        }

        static ObjectId EadEnsureLayer(Transaction tr, Database db, string name, short color, ObjectId ltId)
        {
            LayerTable table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
            var rec = new LayerTableRecord
            {
                Name = name,
                Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByAci, color)
            };
            if (!ltId.IsNull) rec.LinetypeObjectId = ltId;
            ObjectId id = table.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
            return id;
        }
    }
}
