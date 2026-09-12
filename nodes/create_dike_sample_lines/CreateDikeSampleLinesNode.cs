using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        /// <summary>
        /// Dike batch processing: convert every centerline on the given layers into an alignment (direction set by the K-station texts along the line),
        /// locate sample lines and their real endpoints from the section lines already drawn (section-layer entities crossing the centerline),
        /// falling back to the station texts along the line for dikes without section lines. The original centerline polylines are left untouched.
        ///
        /// This node only "computes the endpoints of each sample line"; creating the group/sample lines, setting styles and marking sample sources
        /// is delegated to create_sample_lines (lines explicit-endpoint mode); that path is proven,
        /// and includes the "set style per line in write mode" step: without it sections come out as empty shells
        /// (SectionPoints=0, no ground line in the section view, and no API can recompute it afterwards; burned on 2026-08-11).
        /// </summary>
        static JsonNode RunNodeCreateDikeSampleLines(JsonObject args, Document doc)
        {
            var layersArr = args["centerline_layers"] as JsonArray;
            if (layersArr == null || layersArr.Count == 0)
                throw new ArgumentException("centerline_layers must contain at least one centerline layer name.");
            var centerLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonNode n in layersArr) centerLayers.Add(n.GetValue<string>());

            string surfaceName = Need(args, "surface");
            string numberLayer = GetString(args, "number_layer", "DIKE-NO");
            string stationLayer = GetString(args, "station_layer", "0");
            var sectionLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var secArr = args["section_layers"] as JsonArray;
            if (secArr != null)
                foreach (JsonNode n in secArr) sectionLayers.Add(n.GetValue<string>());
            else
                sectionLayers.Add(GetString(args, "section_layer", "SECTION-LINE"));
            double numberMaxDist = GetDouble(args, "number_max_dist", 150);
            double stationMaxDist = GetDouble(args, "station_max_dist", 60);
            double sectionMargin = GetDouble(args, "section_margin", 120);
            double fallbackSwath = GetDouble(args, "fallback_swath", 50);
            // rebuild_only: the alignment already exists (not rebuilt, direction unchanged); only the sample line group is rebuilt.
            // Used after the corridor is built: sample sources must be marked in the same transaction as group creation, so sampling corridor/road surfaces requires rebuilding the group.
            bool rebuildOnly = GetBool(args, "rebuild_only", false);
            string corridorSuffix = GetString(args, "corridor_suffix", "_Corridor");
            string roadSurfaceSuffix = GetString(args, "road_surface_suffix", "_Design");

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var stationRegex = new Regex(@"^K(\d+)\+(\d+(?:\.\d+)?)");

            // Phase 1 (read-only transaction): collect inputs and make every decision based on the original drawing geometry.
            var lineIds = new List<ObjectId>();
            var lineInfo = new List<JsonObject>();      // name/layer/length/reversed/station_texts_used
            var lineReversed = new List<bool>();
            var lineLabelStations = new List<List<double>>();   // fallback: section stations given by the station texts along the line
            var sectionIds = new List<(ObjectId id, Extents3d ext)>();
            var warnings = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (FindSurfaceId(tr, civ, surfaceName).IsNull)
                    throw new InvalidOperationException("Surface '" + surfaceName + "' not in the drawing; run import_surface first.");

                var centerlines = new List<Polyline>();
                var numberTexts = new List<(Point3d pos, string text)>();
                var stationTexts = new List<(Point3d pos, double station)>();

                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    Entity ent;
                    try { ent = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                    catch { continue; }
                    if (ent == null) continue;
                    string layer = ent.Layer;

                    if (centerLayers.Contains(layer) && ent is Polyline cpl && cpl.Length > 1)
                        centerlines.Add(cpl);
                    else if (string.Equals(layer, numberLayer, StringComparison.OrdinalIgnoreCase))
                    {
                        if (ent is MText mt && !string.IsNullOrWhiteSpace(mt.Text))
                            numberTexts.Add((mt.Location, mt.Text.Trim()));
                        else if (ent is DBText dt && !string.IsNullOrWhiteSpace(dt.TextString))
                            numberTexts.Add((dt.Position, dt.TextString.Trim()));
                    }
                    else if (string.Equals(layer, stationLayer, StringComparison.OrdinalIgnoreCase) && ent is DBText st)
                    {
                        Match m = stationRegex.Match(st.TextString.Trim());
                        if (m.Success)
                        {
                            double station = double.Parse(m.Groups[1].Value) * 1000
                                           + double.Parse(m.Groups[2].Value);
                            stationTexts.Add((st.Position, station));
                        }
                    }
                    else if (sectionLayers.Contains(layer) && (ent is Line || ent is Polyline))
                    {
                        try { sectionIds.Add((id, ent.GeometricExtents)); }
                        catch { }
                    }
                }
                if (centerlines.Count == 0)
                    throw new InvalidOperationException("No centerline polyline found on the given layers.");

                // number <-> centerline: global greedy pairing by distance (stable even with fewer numbers than centerlines)
                var pairs = new List<(int li, int ti, double d)>();
                for (int li = 0; li < centerlines.Count; li++)
                    for (int ti = 0; ti < numberTexts.Count; ti++)
                    {
                        double d = centerlines[li]
                            .GetClosestPointTo(numberTexts[ti].pos, false)
                            .DistanceTo(numberTexts[ti].pos);
                        if (d <= numberMaxDist) pairs.Add((li, ti, d));
                    }
                pairs.Sort((a, b) => a.d.CompareTo(b.d));
                var lineName = new string[centerlines.Count];
                var textUsed = new bool[numberTexts.Count];
                foreach (var p in pairs)
                {
                    if (lineName[p.li] != null || textUsed[p.ti]) continue;
                    lineName[p.li] = numberTexts[p.ti].text;
                    textUsed[p.ti] = true;
                }
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int li = 0; li < centerlines.Count; li++)
                {
                    Polyline pl = centerlines[li];

                    string nm = lineName[li];
                    if (nm == null)
                    {
                        nm = "Dike_" + pl.Handle;
                        warnings.Add("No usable number text near centerline " + pl.Handle + ", named " + nm);
                    }
                    string uniq = nm;
                    int k = 2;
                    while (!usedNames.Add(uniq)) uniq = nm + "_" + k++;

                    // Direction: correlation of (distance along line, labelled station) of the K-station texts; negative = reverse
                    var samples = new List<(double geom, double label)>();
                    foreach (var stx in stationTexts)
                    {
                        Point3d cp = pl.GetClosestPointTo(stx.pos, false);
                        if (cp.DistanceTo(stx.pos) > stationMaxDist) continue;
                        if (stx.station > pl.Length + 50) continue;   // crosstalk from long-station labels of a neighbouring line
                        samples.Add((pl.GetDistAtPoint(cp), stx.station));
                    }
                    bool reversed = false;
                    if (samples.Count >= 2)
                    {
                        double mg = 0, ml = 0;
                        foreach (var s in samples) { mg += s.geom; ml += s.label; }
                        mg /= samples.Count; ml /= samples.Count;
                        double cov = 0;
                        foreach (var s in samples) cov += (s.geom - mg) * (s.label - ml);
                        reversed = cov < 0;
                    }
                    else
                        warnings.Add(uniq + ": not enough station texts along the line (" + samples.Count + "); drawing direction used as alignment direction");

                    var labelStations = new List<double>();
                    foreach (var s in samples)
                    {
                        bool dupSt = false;
                        foreach (double v in labelStations)
                            if (Math.Abs(v - s.label) < 0.05) { dupSt = true; break; }
                        if (!dupSt) labelStations.Add(s.label);
                    }
                    labelStations.Sort();

                    lineIds.Add(pl.ObjectId);
                    lineLabelStations.Add(labelStations);
                    lineReversed.Add(reversed);
                    lineInfo.Add(new JsonObject
                    {
                        ["name"] = uniq,
                        ["source_handle"] = pl.Handle.ToString(),
                        ["layer"] = pl.Layer,
                        ["length"] = Math.Round(pl.Length, 2),
                        ["reversed"] = reversed,
                        ["station_texts_used"] = samples.Count
                    });
                }
                tr.Commit();
            }

            // Phase 2: per dike: transaction A creates the alignment; a read-only transaction computes sample line endpoints;
            // creation is delegated to create_sample_lines (lines mode).
            var dikes = new JsonArray();
            int totalSampleLines = 0;
            for (int li = 0; li < lineIds.Count; li++)
            {
                JsonObject dike = lineInfo[li];
                string name = dike["name"].GetValue<string>();
                bool reversed = lineReversed[li];

                // Transaction A: create the alignment only and commit (creating alignment and sample line group in one transaction once produced empty sections, see note above).
                // With rebuild_only the alignment must already exist; just look it up.
                ObjectId alignId;
                if (rebuildOnly)
                {
                    using (Transaction tf = db.TransactionManager.StartTransaction())
                    {
                        CivAlignment existing = FindAlignment(tf, civ, name);
                        if (existing == null)
                        {
                            warnings.Add(name + ": rebuild_only but the alignment is not in the drawing, skipped");
                            dike["sample_lines"] = 0;
                            dike["mode"] = "skipped";
                            dikes.Add(dike);
                            tf.Commit();
                            continue;
                        }
                        alignId = existing.ObjectId;
                        tf.Commit();
                    }
                }
                else
                using (Transaction t2 = db.TransactionManager.StartTransaction())
                {
                    var pl = (Polyline)t2.GetObject(lineIds[li], OpenMode.ForRead);

                    // Overwrite the alignment with the same name
                    EraseAlignments(t2, civ, name);

                    // Always build the alignment from a temporary copy of the centerline (EraseExistingEntities consumes the copy); the original is kept
                    var bt = (BlockTable)t2.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)t2.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    var tmp = (Polyline)pl.Clone();
                    if (reversed) tmp.ReverseCurve();
                    ms.AppendEntity(tmp);
                    t2.AddNewlyCreatedDBObject(tmp, true);

                    alignId = CreateAlignmentFromEntity(
                        t2, civ, name, ObjectId.Null, tmp.ObjectId, db.Clayer,
                        FindStyleId(t2, civ.Styles.AlignmentStyles, null),
                        FindStyleId(t2, civ.Styles.LabelSetStyles.AlignmentLabelSetStyles, null),
                        eraseSource: true, addCurves: false);
                    t2.Commit();
                }

                // Read-only transaction: compute the endpoints of each sample line (section lines first, station texts as fallback).
                var lineSpecs = new JsonArray();
                string mode = "sections";
                int skippedDup = 0;
                using (Transaction t2 = db.TransactionManager.StartTransaction())
                {
                    var pl = (Polyline)t2.GetObject(lineIds[li], OpenMode.ForRead);
                    var al = (CivAlignment)t2.GetObject(alignId, OpenMode.ForRead);

                    Extents3d ext = pl.GeometricExtents;
                    var min = new Point2d(ext.MinPoint.X - sectionMargin, ext.MinPoint.Y - sectionMargin);
                    var max = new Point2d(ext.MaxPoint.X + sectionMargin, ext.MaxPoint.Y + sectionMargin);

                    // 2D segments of the centerline (ignore Z: section lines often carry ground elevation, and 3D IntersectWith would miss them all)
                    var plSegs = new List<(Point2d a, Point2d b)>();
                    for (int vi = 0; vi < pl.NumberOfVertices - 1; vi++)
                        plSegs.Add((pl.GetPoint2dAt(vi), pl.GetPoint2dAt(vi + 1)));

                    var stationsMade = new List<double>();
                    foreach (var se in sectionIds)
                    {
                        if (se.ext.MinPoint.X > max.X || se.ext.MaxPoint.X < min.X ||
                            se.ext.MinPoint.Y > max.Y || se.ext.MaxPoint.Y < min.Y) continue;

                        Entity sent;
                        try { sent = (Entity)t2.GetObject(se.id, OpenMode.ForRead); }
                        catch { continue; }

                        var secSegs = new List<(Point2d a, Point2d b)>();
                        if (sent is Line sln)
                            secSegs.Add((new Point2d(sln.StartPoint.X, sln.StartPoint.Y),
                                         new Point2d(sln.EndPoint.X, sln.EndPoint.Y)));
                        else if (sent is Polyline sp)
                            for (int vi = 0; vi < sp.NumberOfVertices - 1; vi++)
                                secSegs.Add((sp.GetPoint2dAt(vi), sp.GetPoint2dAt(vi + 1)));

                        bool hitFound = false;
                        Point2d hit = Point2d.Origin;
                        foreach (var ps in plSegs)
                        {
                            foreach (var ss in secSegs)
                            {
                                double d1x = ps.b.X - ps.a.X, d1y = ps.b.Y - ps.a.Y;
                                double d2x = ss.b.X - ss.a.X, d2y = ss.b.Y - ss.a.Y;
                                double den = d1x * d2y - d1y * d2x;
                                if (Math.Abs(den) < 1e-12) continue;
                                double t = ((ss.a.X - ps.a.X) * d2y - (ss.a.Y - ps.a.Y) * d2x) / den;
                                double u = ((ss.a.X - ps.a.X) * d1y - (ss.a.Y - ps.a.Y) * d1x) / den;
                                if (t < -0.001 || t > 1.001 || u < -0.001 || u > 1.001) continue;
                                hit = new Point2d(ps.a.X + t * d1x, ps.a.Y + t * d1y);
                                hitFound = true;
                                break;
                            }
                            if (hitFound) break;
                        }
                        if (!hitFound) continue;

                        double station = 0, offset = 0;
                        try { al.StationOffset(hit.X, hit.Y, ref station, ref offset); }
                        catch { continue; }
                        if (station < al.StartingStation - 0.01 || station > al.EndingStation + 0.01) continue;

                        bool dup = false;
                        foreach (double s in stationsMade)
                            if (Math.Abs(s - station) < 0.05) { dup = true; break; }
                        if (dup) { skippedDup++; continue; }

                        var pts = new JsonArray();
                        if (sent is Line ln2)
                        {
                            pts.Add(new JsonArray { ln2.StartPoint.X, ln2.StartPoint.Y });
                            pts.Add(new JsonArray { ln2.EndPoint.X, ln2.EndPoint.Y });
                        }
                        else if (sent is Polyline spl)
                        {
                            for (int vi = 0; vi < spl.NumberOfVertices; vi++)
                            {
                                Point2d v = spl.GetPoint2dAt(vi);
                                pts.Add(new JsonArray { v.X, v.Y });
                            }
                        }
                        if (pts.Count < 2) continue;

                        lineSpecs.Add(new JsonObject
                        {
                            ["name"] = name + "_SL-" + station.ToString("F1"),
                            ["points"] = pts
                        });
                        stationsMade.Add(station);
                    }

                    // Fallback: when no section line crosses the centerline, emit perpendicular sample lines at the stations of the station texts
                    if (lineSpecs.Count == 0 && lineLabelStations[li].Count > 0)
                    {
                        mode = "label_stations";
                        foreach (double rawSt in lineLabelStations[li])
                        {
                            double st = Math.Min(Math.Max(rawSt, al.StartingStation), al.EndingStation);
                            bool dup = false;
                            foreach (double s in stationsMade)
                                if (Math.Abs(s - st) < 0.05) { dup = true; break; }
                            if (dup) continue;
                            double xL = 0, yL = 0, xR = 0, yR = 0;
                            try
                            {
                                al.PointLocation(st, -fallbackSwath, ref xL, ref yL);
                                al.PointLocation(st, fallbackSwath, ref xR, ref yR);
                                lineSpecs.Add(new JsonObject
                                {
                                    ["name"] = name + "_SL-" + st.ToString("F1"),
                                    ["points"] = new JsonArray
                                    {
                                        new JsonArray { xL, yL },
                                        new JsonArray { xR, yR }
                                    }
                                });
                                stationsMade.Add(st);
                            }
                            catch (System.Exception ex)
                            {
                                warnings.Add(name + " fallback station " + st.ToString("F1") + " could not be located: " + ex.Message);
                            }
                        }
                        warnings.Add(name + ": no section line crosses the centerline; " + lineSpecs.Count
                            + " station texts used as fallback for sample lines (" + fallbackSwath + " per side)");
                    }

                    dike["alignment_start"] = Math.Round(al.StartingStation, 2);
                    dike["alignment_end"] = Math.Round(al.EndingStation, 2);
                    t2.Commit();
                }

                // Creation: delegated to the proven create_sample_lines (lines explicit-endpoint mode)
                int made = 0;
                if (lineSpecs.Count > 0)
                {
                    JsonNode slRes = CreateSampleLines(new JsonObject
                    {
                        ["alignment"] = name,
                        ["surface"] = surfaceName,
                        ["corridor"] = name + corridorSuffix,
                        ["road_surface"] = name + roadSurfaceSuffix,
                        ["lines"] = lineSpecs
                    }, doc);
                    made = slRes["sample_lines"].GetValue<int>();
                    dike["sampled_sources"] = slRes["sampled_sources"].DeepClone();
                }
                else
                {
                    dike["sampled_sources"] = new JsonArray();
                    warnings.Add(name + ": neither crossing section lines nor station texts; sample line group is empty");
                }

                dike["sample_lines"] = made;
                dike["mode"] = mode;
                dike["dup_sections_skipped"] = skippedDup;
                totalSampleLines += made;
                dikes.Add(dike);
            }

            return new JsonObject
            {
                ["surface"] = surfaceName,
                ["dike_count"] = dikes.Count,
                ["total_sample_lines"] = totalSampleLines,
                ["dikes"] = dikes,
                ["warnings"] = warnings
            };
        }
    }
}
