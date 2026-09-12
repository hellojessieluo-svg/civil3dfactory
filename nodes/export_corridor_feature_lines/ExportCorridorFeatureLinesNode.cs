using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlign = Autodesk.Civil.DatabaseServices.Alignment;
using CivCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using CivBaseline = Autodesk.Civil.DatabaseServices.Baseline;
using CivFlColl = Autodesk.Civil.DatabaseServices.FeatureLineCollection;
using CivFl = Autodesk.Civil.DatabaseServices.CorridorFeatureLine;
using CivFlPoint = Autodesk.Civil.DatabaseServices.FeatureLinePoint;
using CivilDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// export_corridor_feature_lines: extract corridor feature lines (by point code) as 3D polylines and
    /// write them into a brand-new empty DWG -- elevations kept per point, no Civil 3D objects in the output file.
    ///
    /// Purpose: the design boundary lines of a dredge corridor (daylight lines, toe lines, ...) are the 3D
    /// skeleton of the section-method model; on delivery / summary they are exported per code as whole layers.
    /// Codes follow the factory PKT code table (origin / controlpoint / daylight / mp / toe + -left/-right, see business master doc section 3).
    ///
    /// Where a feature line breaks between regions (FeatureLinePoint.IsBreak, e.g. a condition change at an intersection)
    /// it is split into several polylines rather than force-joined. Layer names are the contract of this output file, controlled by template:
    ///   layer default "FL-{corridor}-{code}", placeholders {corridor} corridor name,
    ///   {baseline} baseline alignment name, {code} point code.
    /// Only main-baseline feature lines are exported (offset baselines are unused in this project; when met they go into skipped).
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeExportCorridorFeatureLines(JsonObject args, Document doc)
            => ExportCorridorFeatureLines(args, doc);

        public static JsonNode ExportCorridorFeatureLines(JsonObject a, Document doc)
        {
            string outPath = Need(a, "out");
            if (!Path.IsPathRooted(outPath))
                throw new InvalidOperationException("out must be an absolute path: " + outPath);
            bool overwrite = GetBool(a, "overwrite", false);
            if (File.Exists(outPath) && !overwrite)
                throw new InvalidOperationException("File already exists, refusing to overwrite: " + outPath + " (pass overwrite:true to overwrite)");

            string layerTpl = GetString(a, "layer", "FL-{corridor}-{code}");
            short color = (short)GetDouble(a, "color", 2);          // yellow
            int minPoints = (int)GetDouble(a, "min_points", 2);

            var wantCorridors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            JsonArray onlyCor = a["corridors"] as JsonArray;
            if (onlyCor != null)
                foreach (JsonNode n in onlyCor) if (n != null) wantCorridors.Add(n.ToString());
            var wantCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            JsonArray onlyCodes = a["codes"] as JsonArray;
            if (onlyCodes != null)
                foreach (JsonNode n in onlyCodes) if (n != null) wantCodes.Add(n.ToString());

            Database db = doc.Database;
            var items = new List<JsonObject>();
            var lines = new List<KeyValuePair<string, List<Point3d>>>();  // layer -> pts
            var skipped = new JsonArray();
            int corridorsSeen = 0, breaks = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivilDoc civ = Civ(db);
                foreach (ObjectId cid in civ.CorridorCollection)
                {
                    var cor = tr.GetObject(cid, OpenMode.ForRead) as CivCorridor;
                    if (cor == null) continue;
                    if (wantCorridors.Count > 0 && !wantCorridors.Contains(cor.Name)) continue;
                    corridorsSeen++;

                    foreach (CivBaseline bl in cor.Baselines)
                    {
                        string blName = "";
                        try
                        {
                            var al = tr.GetObject(bl.AlignmentId, OpenMode.ForRead) as CivAlign;
                            if (al != null) blName = al.Name;
                        }
                        catch (System.Exception) { }

                        foreach (CivFlColl coll in bl.MainBaselineFeatureLines.FeatureLineCollectionMap)
                        {
                            int idx = 0;
                            foreach (CivFl fl in coll)
                            {
                                idx++;
                                string code = "";
                                try { code = fl.CodeName; } catch (System.Exception) { }
                                if (wantCodes.Count > 0 && !wantCodes.Contains(code)) continue;

                                string layer = layerTpl
                                    .Replace("{corridor}", cor.Name)
                                    .Replace("{baseline}", blName)
                                    .Replace("{code}", code);

                                // Split by IsBreak: breaks (condition change / region gap) are not force-joined
                                var segs = new List<List<Point3d>>();
                                var cur = new List<Point3d>();
                                try
                                {
                                    foreach (CivFlPoint p in fl.FeatureLinePoints)
                                    {
                                        cur.Add(p.XYZ);
                                        if (p.IsBreak)
                                        {
                                            segs.Add(cur); cur = new List<Point3d>(); breaks++;
                                        }
                                    }
                                }
                                catch (System.Exception ex)
                                {
                                    skipped.Add(cor.Name + "/" + code + "#" + idx + " (failed to get points: " + ex.GetType().Name + ")");
                                    continue;
                                }
                                if (cur.Count > 0) segs.Add(cur);

                                int seg = 0;
                                foreach (var pts in segs)
                                {
                                    seg++;
                                    if (pts.Count < minPoints)
                                    {
                                        skipped.Add(cor.Name + "/" + code + "#" + idx + "." + seg + " (fewer than " + minPoints + " points)");
                                        continue;
                                    }
                                    lines.Add(new KeyValuePair<string, List<Point3d>>(layer, pts));

                                    double zmin = double.MaxValue, zmax = double.MinValue, len = 0.0;
                                    for (int i = 0; i < pts.Count; i++)
                                    {
                                        double z = pts[i].Z;
                                        if (z < zmin) zmin = z;
                                        if (z > zmax) zmax = z;
                                        if (i > 0) len += pts[i].DistanceTo(pts[i - 1]);
                                    }
                                    items.Add(new JsonObject
                                    {
                                        ["corridor"] = cor.Name,
                                        ["baseline"] = blName,
                                        ["code"] = code,
                                        ["layer"] = layer,
                                        ["segment"] = seg,
                                        ["points"] = pts.Count,
                                        ["length3d"] = Round(len, 3),
                                        ["z_min"] = Round(zmin, 3),
                                        ["z_max"] = Round(zmax, 3)
                                    });
                                }
                            }
                        }
                        // Offset baselines are not handled; report them as-is
                        try
                        {
                            if (bl.OffsetBaselineFeatureLinesCol != null && bl.OffsetBaselineFeatureLinesCol.Count > 0)
                                skipped.Add(cor.Name + " (has " + bl.OffsetBaselineFeatureLinesCol.Count + " offset-baseline feature line groups, not exported)");
                        }
                        catch (System.Exception) { }
                    }
                }
                tr.Commit();
            }

            if (lines.Count == 0)
                throw new InvalidOperationException("No corridor feature lines to export (" + corridorsSeen + " corridors; check the corridors/codes filters).");

            // ---- Write into a brand-new empty drawing ----
            using (var nd = new Database(true, false))
            {
                nd.Insunits = UnitsValue.Meters;
                using (Transaction tr = nd.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(nd.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    ObjectId ltId = A2PResolveLinetype(tr, nd, "Continuous", "acadiso.lin");

                    foreach (var kv in lines)
                    {
                        ObjectId layerId = EadEnsureLayer(tr, nd, kv.Key, color, ltId);
                        var pl = new Polyline3d();
                        pl.SetDatabaseDefaults(nd);   // see ExportAlignmentsToDwgNode: nd must be passed explicitly
                        ms.AppendEntity(pl);
                        tr.AddNewlyCreatedDBObject(pl, true);
                        foreach (Point3d p in kv.Value)
                        {
                            var v = new PolylineVertex3d(p);
                            pl.AppendVertex(v);
                            tr.AddNewlyCreatedDBObject(v, true);
                        }
                        pl.LayerId = layerId;
                        pl.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                            Autodesk.AutoCAD.Colors.ColorMethod.ByAci, color);
                    }
                    tr.Commit();
                }
                string dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                nd.SaveAs(outPath, DwgVersion.Current);
            }

            long bytes = 0;
            try { bytes = new FileInfo(outPath).Length; } catch (System.Exception) { }

            var arr = new JsonArray();
            foreach (var it in items) arr.Add(it);
            return new JsonObject
            {
                ["output"] = outPath,
                ["bytes"] = bytes,
                ["corridors"] = corridorsSeen,
                ["exported"] = lines.Count,
                ["break_splits"] = breaks,
                ["layer_template"] = layerTpl,
                ["skipped"] = skipped,
                ["items"] = arr
            };
        }
    }
}
