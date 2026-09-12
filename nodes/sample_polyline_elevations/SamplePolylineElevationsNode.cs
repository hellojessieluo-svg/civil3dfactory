using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// sample_polyline_elevations: sample surface elevations along polylines (read-only).
    ///
    /// For every polyline on the layer (or in the handle whitelist), sample the given surface at each vertex and report
    /// min/max/mean/median + the number of points off the surface; with step, extra points are added evenly between vertices.
    /// **Open polylines are accepted** -- island crest lines and dike crest lines are often unclosed; that is the split with
    /// survey_dredge_regions (closed boundaries only, aggregate values only).
    ///
    /// Typical use: given a ring of design edge lines, check the existing ground under it before fixing the crest elevation and computing fill.
    /// Quantities are not produced here; use calculate_surface_volume.
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeSamplePolylineElevations(JsonObject a, Document doc)
        {
            string layer = GetString(a, "layer", null);
            string sfName = Need(a, "surface");
            var handles = a["handles"] as JsonArray;
            var names = a["names"] as JsonObject;      // {handle: name}; when given the name is carried into the result
            double step = GetDouble(a, "step", 0.0);   // 0 = vertices only
            double inStep = GetDouble(a, "interior_step", 0.0);  // >0 = also grid-sample the interior
            bool includeOpen = GetBool(a, "include_open", true);
            double minLen = GetDouble(a, "min_length", 0.0);
            bool withPoints = GetBool(a, "points", false);
            bool exportExcel = GetBool(a, "export_excel", false);
            string excelFormat = GetString(a, "excel_format", "xlsx");

            if (string.IsNullOrWhiteSpace(layer) && (handles == null || handles.Count == 0))
                throw new InvalidOperationException("Give at least one of layer and handles.");

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (handles != null)
                foreach (JsonNode h in handles)
                    if (h != null) wanted.Add(h.ToString().Trim());

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            var perLine = new JsonArray();
            var warnings = new JsonArray();
            var excelRows = new List<object[]>();
            var pointRows = new List<object[]>();
            int totalSamples = 0, totalOff = 0;
            double gMin = double.MaxValue, gMax = double.MinValue;
            string usedSf = null;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId sfId = FindSurfaceId(tr, civ, sfName);
                if (sfId.IsNull) throw new InvalidOperationException("Surface '" + sfName + "' not found.");
                var sf = (CivSurface)tr.GetObject(sfId, OpenMode.ForRead);
                usedSf = sf.Name;

                int seq = 0;
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    Polyline pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (pl == null) continue;
                    string handle = pl.Handle.ToString();
                    if (wanted.Count > 0 && !wanted.Contains(handle)) continue;
                    if (wanted.Count == 0 && !string.IsNullOrWhiteSpace(layer) &&
                        !string.Equals(pl.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!includeOpen && !pl.Closed) continue;

                    double len = 0.0;
                    try { len = pl.Length; } catch (System.Exception) { }
                    if (len < minLen) continue;

                    seq++;
                    string label = null;
                    if (names != null && names[handle] != null) label = names[handle].ToString();
                    if (string.IsNullOrEmpty(label)) label = "P" + seq.ToString("00", CultureInfo.InvariantCulture);

                    List<Point2d> pts = SampleAlongPolyline(pl, step);
                    var zs = new List<double>();
                    int off = 0;
                    var ptArr = withPoints ? new JsonArray() : null;

                    foreach (Point2d p in pts)
                    {
                        object z = null;
                        try { z = Math.Round(sf.FindElevationAtXY(p.X, p.Y), 3); zs.Add((double)z); }
                        catch (System.Exception) { off++; }   // point off the surface
                        if (ptArr != null)
                            ptArr.Add(new JsonArray { Round(p.X, 3), Round(p.Y, 3),
                                                      z == null ? null : (JsonNode)(double)z });
                        if (exportExcel)
                            pointRows.Add(new object[] { label, handle, pointRows.Count + 1,
                                                         Round2(p.X), Round2(p.Y), z });
                    }

                    totalSamples += pts.Count;
                    totalOff += off;

                    double zMin = 0, zMax = 0, zMean = 0, zMed = 0;
                    if (zs.Count > 0)
                    {
                        zs.Sort();
                        zMin = zs[0]; zMax = zs[zs.Count - 1];
                        double sum = 0; for (int i = 0; i < zs.Count; i++) sum += zs[i];
                        zMean = sum / zs.Count;
                        zMed = zs.Count % 2 == 1 ? zs[zs.Count / 2]
                                                 : (zs[zs.Count / 2 - 1] + zs[zs.Count / 2]) / 2.0;
                        if (zMin < gMin) gMin = zMin;
                        if (zMax > gMax) gMax = zMax;
                    }
                    else
                    {
                        warnings.Add((JsonNode)(label + " (" + handle + "): all sample points are off the surface, no elevation."));
                    }

                    double area = 0.0;
                    try { area = pl.Area; } catch (System.Exception) { }

                    // ---- Interior grid sampling (the "existing elevation" of an island/parcel comes from this, not the edge ring) ----
                    var zin = new List<double>();
                    int inTotal = 0, inOff = 0;
                    if (inStep > 0)
                    {
                        // Ring building and point-in-polygon reuse the existing functions from create_dredge_grading / annotate_grid_elevations,
                        // consistent with the grading chain (a home-made vertex ring scrambles on lines with arc segments; do not rewrite it)
                        DredgeRing ring = DredgeBuildRing(pl, Math.Min(inStep, 2.0));
                        foreach (Point2d p in GridInsideRing(ring, inStep))
                        {
                            inTotal++;
                            try { zin.Add(Math.Round(sf.FindElevationAtXY(p.X, p.Y), 3)); }
                            catch (System.Exception) { inOff++; }
                        }
                        if (inTotal == 0)
                            warnings.Add((JsonNode)(label + " (" + handle + "): not a single grid point landed inside, "
                                                    + "interior_step is too coarse for this area."));
                    }

                    double[] stIn = Stats(zin);

                    var row = new JsonObject
                    {
                        ["id"] = label,
                        ["handle"] = handle,
                        ["layer"] = pl.Layer,
                        ["closed"] = pl.Closed,
                        ["vertices"] = pl.NumberOfVertices,
                        ["length"] = Round(len, 3),
                        ["area"] = Round(area, 2),
                        ["samples"] = pts.Count,
                        ["on_surface"] = zs.Count,
                        ["off_surface"] = off,
                        ["z_min"] = zs.Count > 0 ? (JsonNode)Round(zMin, 3) : null,
                        ["z_max"] = zs.Count > 0 ? (JsonNode)Round(zMax, 3) : null,
                        ["z_mean"] = zs.Count > 0 ? (JsonNode)Round(zMean, 3) : null,
                        ["z_median"] = zs.Count > 0 ? (JsonNode)Round(zMed, 3) : null
                    };
                    if (inStep > 0)
                    {
                        row["interior_samples"] = inTotal;
                        row["interior_off_surface"] = inOff;
                        row["z_in_min"] = stIn == null ? null : (JsonNode)Round(stIn[0], 3);
                        row["z_in_max"] = stIn == null ? null : (JsonNode)Round(stIn[1], 3);
                        row["z_in_mean"] = stIn == null ? null : (JsonNode)Round(stIn[2], 3);
                        row["z_in_median"] = stIn == null ? null : (JsonNode)Round(stIn[3], 3);
                    }
                    if (ptArr != null) row["points"] = ptArr;
                    perLine.Add(row);

                    if (exportExcel)
                    {
                        var cells = new List<object> {
                            label, handle, pl.Layer, pl.Closed ? "closed" : "open",
                            pl.NumberOfVertices, Round2(len), Round2(area),
                            pts.Count, off,
                            zs.Count > 0 ? (object)Round(zMin, 3) : null,
                            zs.Count > 0 ? (object)Round(zMax, 3) : null,
                            zs.Count > 0 ? (object)Round(zMean, 3) : null,
                            zs.Count > 0 ? (object)Round(zMed, 3) : null };
                        if (inStep > 0)
                        {
                            cells.Add(inTotal);
                            cells.Add(stIn == null ? null : (object)Round(stIn[0], 3));
                            cells.Add(stIn == null ? null : (object)Round(stIn[1], 3));
                            cells.Add(stIn == null ? null : (object)Round(stIn[2], 3));
                            cells.Add(stIn == null ? null : (object)Round(stIn[3], 3));
                        }
                        excelRows.Add(cells.ToArray());
                    }
                }
                tr.Commit();
            }

            if (perLine.Count == 0)
                throw new InvalidOperationException(
                    "No matching polyline found (layer='" + (layer ?? "") + "', handle whitelist " + wanted.Count + ").");

            var files = new JsonArray();
            string outdir = null;
            if (exportExcel)
            {
                outdir = ResolveOutDir(a, doc);
                string baseName = "EdgeGroundElevation_" + Sanitize(usedSf) +
                                  (string.IsNullOrWhiteSpace(layer) ? "" : "_" + Sanitize(layer));
                var head = new List<string>(HeadersPlElevSummary);
                if (inStep > 0)
                    head.AddRange(new[] { "InteriorPoints", "InteriorMin_m", "InteriorMax_m", "InteriorMean_m", "InteriorMedian_m" });
                foreach (string p in Excel.Write(outdir, baseName, head.ToArray(), excelRows, excelFormat))
                    files.Add(p);
                foreach (string p in Excel.Write(outdir, baseName + "_Points", HeadersPlElevPoints, pointRows, excelFormat))
                    files.Add(p);
            }

            return new JsonObject
            {
                ["surface"] = usedSf,
                ["layer"] = layer,
                ["step"] = step > 0 ? (JsonNode)step : null,
                ["interior_step"] = inStep > 0 ? (JsonNode)inStep : null,
                ["polylines"] = perLine.Count,
                ["samples"] = totalSamples,
                ["off_surface"] = totalOff,
                ["z_min"] = gMin < double.MaxValue ? (JsonNode)Round(gMin, 3) : null,
                ["z_max"] = gMax > double.MinValue ? (JsonNode)Round(gMax, 3) : null,
                ["per_polyline"] = perLine,
                ["excel_files"] = files,
                ["outdir"] = outdir,
                ["warnings"] = warnings
            };
        }

        static readonly string[] HeadersPlElevSummary = {
            "Id", "Handle", "Layer", "Closed", "Vertices", "Length_m", "Area_m2",
            "Samples", "OffSurface", "MinElev_m", "MaxElev_m", "MeanElev_m", "MedianElev_m" };

        static readonly string[] HeadersPlElevPoints = {
            "Id", "Handle", "Seq", "X", "Y", "GroundElev_m" };

        /// <summary>Returns [min, max, mean, median]; null for an empty set.</summary>
        static double[] Stats(List<double> v)
        {
            if (v == null || v.Count == 0) return null;
            var s = new List<double>(v);
            s.Sort();
            double sum = 0; for (int i = 0; i < s.Count; i++) sum += s[i];
            double med = s.Count % 2 == 1 ? s[s.Count / 2]
                                          : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0;
            return new[] { s[0], s[s.Count - 1], sum / s.Count, med };
        }

        /// <summary>Grid the ring's bounding box at step, keeping only points inside the ring. The grid is aligned to the bounding box so reruns on the same line are identical.</summary>
        static IEnumerable<Point2d> GridInsideRing(DredgeRing ring, double step)
        {
            if (ring == null || ring.Count < 3) yield break;
            for (double y = ring.MinY + step / 2; y < ring.MaxY; y += step)
                for (double x = ring.MinX + step / 2; x < ring.MaxX; x += step)
                {
                    var p = new Point2d(x, y);
                    if (GridPointInPolygon(p, ring.P)) yield return p;
                }
        }

        /// <summary>
        /// step&lt;=0 takes the original vertices; step&gt;0 walks the whole line at equal steps (same ring convention as survey_dredge_regions,
        /// arcs unrolled by true arc length). Do not hand-roll "GetDistAtPoint per segment then interpolate" -- on lines with arcs it duplicates points and scrambles the ring.
        /// </summary>
        static List<Point2d> SampleAlongPolyline(Polyline pl, double step)
        {
            var pts = new List<Point2d>();
            if (step > 0)
            {
                DredgeRing r = DredgeBuildRing(pl, step);
                pts.AddRange(r.P);
                if (!pl.Closed && r.Count > 0)
                    try
                    {
                        Point3d e = pl.GetPointAtDist(pl.GetDistanceAtParameter(pl.EndParam));
                        pts.Add(new Point2d(e.X, e.Y));
                    }
                    catch (System.Exception) { }
                return pts;
            }
            int nv = pl.NumberOfVertices;
            for (int i = 0; i < nv; i++) pts.Add(pl.GetPoint2dAt(i));
            return pts;
        }
    }
}
