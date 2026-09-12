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
    /// Dredge region boundary inventory and parameter trial (read-only).
    ///
    /// First step of the dredge grading chain: inventory every closed polyline on the boundary layer -- handle, area, perimeter,
    /// centroid, text inside the region (auto-detected region names such as CR1 / DK1#Parcel), existing ground elevation stats along the boundary.
    ///
    /// With bottom_elev + slope_ratio_m it also runs a grid trial of the volume **using the same cone model as create_dredge_grading**:
    /// no surface built, drawing untouched; check the quantity from this parameter set before deciding which set to build the surface with.
    /// This is the "derive parameters from data" step; the output is directly the regions parameter skeleton for create_dredge_grading.
    ///
    /// The trial integrates on a grid_step grid, slightly coarser than the downstream TIN volume method (about 1% at the default 5m grid);
    /// use it only to fix parameters, not as the reported quantity -- quantities come from calculate_surface_volume.
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeSurveyDredgeRegions(JsonObject a, Document doc)
        {
            string bndLayer = Need(a, "boundary_layer");
            string surfName = GetString(a, "surface", null);
            string labelLayer = GetString(a, "label_layer", null);

            string startSurfName = GetString(a, "start_surface", null);

            bool hasBottom = a["bottom_elev"] != null;
            double gBottom = GetDouble(a, "bottom_elev", 0.0);
            double gSlope = GetDouble(a, "slope_ratio_m", 5.0);
            if (gSlope <= 0) throw new InvalidOperationException("slope_ratio_m must be greater than 0 (the m in 1:m).");
            bool hasStart = a["start_elev"] != null;

            var regionSpecs = a["regions"] as JsonArray;

            double sampleStep = GetDouble(a, "sample_step", 2.0);
            double gridStep = GetDouble(a, "grid_step", 5.0);
            if (sampleStep <= 0) throw new InvalidOperationException("sample_step must be greater than 0.");
            if (gridStep <= 0) throw new InvalidOperationException("grid_step must be greater than 0.");

            var interfaceLayers = a["interface_layers"] as JsonArray;

            bool exportExcel = GetBool(a, "export_excel", false);
            string excelOutPath = GetString(a, "excel_out_path", null);
            string excelFormat = GetString(a, "excel_format", "xlsx");

            var handles = a["boundaries"] as JsonArray;

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            var perRegion = new JsonArray();
            var warnings = new JsonArray();
            var excelFiles = new List<string>();
            double totalArea = 0, totalCut = 0, totalFill = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivSurface ground = null;
                if (!string.IsNullOrEmpty(surfName))
                {
                    ObjectId gsId = FindSurfaceId(tr, civ, surfName);
                    if (gsId.IsNull) throw new InvalidOperationException("Existing ground surface '" + surfName + "' not found.");
                    ground = (CivSurface)tr.GetObject(gsId, OpenMode.ForRead);
                }
                if (ground == null && !hasStart)
                    warnings.Add((JsonNode)"Neither surface nor start_elev given: reporting geometry only, no elevations or trial volumes.");

                CivSurface startSurface = null;
                if (!string.IsNullOrEmpty(startSurfName))
                {
                    ObjectId ssId = FindSurfaceId(tr, civ, startSurfName);
                    if (ssId.IsNull) throw new InvalidOperationException("Start elevation surface '" + startSurfName + "' not found.");
                    startSurface = (CivSurface)tr.GetObject(ssId, OpenMode.ForRead);
                }

                List<ObjectId> bndIds = DredgeCollectBoundaries(tr, db, bndLayer, handles);
                if (bndIds.Count == 0)
                    throw new InvalidOperationException(
                        "No closed polyline on layer '" + bndLayer + "'" +
                        (handles != null && handles.Count > 0 ? " (or none of the handle whitelist matched)" : "") + ".");

                List<DredgeText> texts = DredgeCollectTexts(tr, db, labelLayer);
                List<Curve> interfaces = DredgeCollectInterfaces(tr, db, interfaceLayers);
                if (interfaceLayers != null && interfaceLayers.Count > 0 && interfaces.Count == 0)
                    warnings.Add((JsonNode)"interface_layers was given but those layers hold no curve; the interface rule had no effect.");

                int seq = 0;
                foreach (ObjectId bid in bndIds)
                {
                    seq++;
                    var pl = (Polyline)tr.GetObject(bid, OpenMode.ForRead);
                    string handle = pl.Handle.ToString();

                    DredgeRing ring = DredgeBuildRing(pl, sampleStep);
                    if (ring.Count < 3)
                    {
                        warnings.Add((JsonNode)("Boundary " + handle + ": not enough sample points, skipped."));
                        continue;
                    }

                    var labels = new JsonArray();
                    foreach (DredgeText t in texts)
                        if (GridPointInPolygon(t.P, ring.P)) labels.Add((JsonNode)t.S);
                    string label = labels.Count > 0 ? labels[0].ToString() : null;
                    JsonObject rspec = DredgeFindSpec(regionSpecs, handle, label);
                    string id = rspec != null ? GetString(rspec, "id", null) : null;
                    if (string.IsNullOrEmpty(id)) id = label;
                    if (string.IsNullOrEmpty(id)) id = "R" + seq.ToString("00", CultureInfo.InvariantCulture);

                    // Parameter merge shared with create_dredge_grading: the trial uses exactly the set the surface build will use
                    DredgeZSpec zspec = DredgeMergeZSpec(a, rspec, gBottom, gSlope, startSurface, interfaces);
                    if (!hasBottom) zspec.Bottom = double.MinValue;
                    double bottom = zspec.Bottom;
                    double slopeM = zspec.SlopeM;
                    if (slopeM <= 0)
                        throw new InvalidOperationException("slope_ratio_m of region '" + id + "' must be greater than 0.");

                    double cx = 0, cy = 0;
                    for (int i = 0; i < ring.Count; i++) { cx += ring.P[i].X; cy += ring.P[i].Y; }
                    cx /= ring.Count; cy /= ring.Count;

                    var row = new JsonObject
                    {
                        ["id"] = id,
                        ["handle"] = handle,
                        ["layer"] = pl.Layer,
                        ["labels"] = labels,
                        ["area"] = Math.Round(pl.Area, 2),
                        ["perimeter"] = Math.Round(pl.Length, 2),
                        ["vertices"] = pl.NumberOfVertices,
                        ["centroid"] = new JsonArray { Math.Round(cx, 3), Math.Round(cy, 3) },
                        ["bbox"] = new JsonArray {
                            Math.Round(ring.MinX, 3), Math.Round(ring.MinY, 3),
                            Math.Round(ring.MaxX, 3), Math.Round(ring.MaxY, 3) },
                        ["winding"] = DredgeSignedArea(ring.P) > 0 ? "ccw" : "cw",
                        // Effective parameters for this region (global + regions override), can be copied straight into create_dredge_grading
                        ["bottom_elev"] = hasBottom ? (JsonNode)bottom : null,
                        ["slope_ratio_m"] = slopeM,
                        ["start_elev_mode"] = zspec.Mode,
                        ["start_elev"] = zspec.HasStart ? (JsonNode)zspec.Start : null,
                        ["start_elev_cap"] = zspec.HasCap ? (JsonNode)zspec.Cap : null
                    };
                    totalArea += pl.Area;

                    // ---- Start elevation stats along the boundary ----
                    double zMax = double.MinValue;
                    if (ground != null || hasStart)
                    {
                        int off, onIface;
                        DredgeFillRingZ(ring, ground, zspec, out off, out onIface);

                        double zSum = 0, zMin = double.MaxValue;
                        zMax = double.MinValue;
                        for (int i = 0; i < ring.Count; i++)
                        {
                            double z = ring.Z[i];
                            zSum += z;
                            if (z < zMin) zMin = z;
                            if (z > zMax) zMax = z;
                        }
                        row["z_top_min"] = Math.Round(zMin, 3);
                        row["z_top_max"] = Math.Round(zMax, 3);
                        row["z_top_mean"] = Math.Round(zSum / ring.Count, 3);
                        row["ring_points"] = ring.Count;
                        row["off_surface_points"] = off;
                        row["interface_points"] = onIface;
                        if (off > 0)
                            warnings.Add((JsonNode)("[" + id + "] " + off + "/" + ring.Count +
                                                    " boundary sample points are off the existing ground surface."));
                    }

                    // ---- Grid trial with the cone model ----
                    if (hasBottom && ground != null)
                    {
                        double bandMax = (zMax - bottom) * slopeM;
                        if (bandMax < 0) bandMax = 0;
                        ring.BuildIndex(Math.Max(bandMax * 1.05, Math.Max(gridStep, 5.0)));

                        double cell = gridStep * gridStep;
                        double cut = 0, fill = 0, wet = 0, deepSum = 0;
                        int cells = 0, offCells = 0;
                        int gx0 = (int)Math.Floor(ring.MinX / gridStep);
                        int gx1 = (int)Math.Ceiling(ring.MaxX / gridStep);
                        int gy0 = (int)Math.Floor(ring.MinY / gridStep);
                        int gy1 = (int)Math.Ceiling(ring.MaxY / gridStep);
                        for (int ix = gx0; ix <= gx1; ix++)
                        {
                            double x = ix * gridStep;
                            for (int iy = gy0; iy <= gy1; iy++)
                            {
                                double y = iy * gridStep;
                                if (!GridPointInPolygon(new Point2d(x, y), ring.P)) continue;
                                cells++;

                                double d, zTop, zDes;
                                if (!ring.Nearest(x, y, out d, out zTop)) zDes = bottom;
                                else { zDes = zTop - d / slopeM; if (zDes < bottom) zDes = bottom; }

                                double zg;
                                if (!GridTrySample(ground, new Point3d(x, y, 0), out zg)) { offCells++; continue; }
                                double h = zg - zDes;
                                if (h > 0) { cut += h * cell; wet += cell; deepSum += h; }
                                else fill += -h * cell;
                            }
                        }
                        row["band_max"] = Math.Round(bandMax, 2);
                        row["est_grid_cells"] = cells;
                        row["est_area"] = Math.Round(cells * cell, 2);
                        row["est_cut_volume"] = Math.Round(cut, 2);
                        row["est_fill_volume"] = Math.Round(fill, 2);
                        row["est_mean_depth"] = wet > 0 ? Math.Round(deepSum * cell / wet, 3) : 0.0;
                        row["est_off_surface_cells"] = offCells;
                        totalCut += cut;
                        totalFill += fill;
                        if (offCells > 0)
                            warnings.Add((JsonNode)("[" + id + "] during the trial " + offCells + "/" + cells +
                                                    " grid points were off the existing ground surface and skipped (volume underestimated)."));
                    }

                    perRegion.Add(row);
                }

                // ---- Export the parameter table skeleton ----
                if (exportExcel)
                {
                    string baseName = string.IsNullOrEmpty(excelOutPath)
                        ? "DredgeRegionParams_" + Sanitize(bndLayer)
                        : System.IO.Path.GetFileNameWithoutExtension(excelOutPath);
                    string dir = string.IsNullOrEmpty(excelOutPath)
                        ? ResolveOutDir(a, doc)
                        : (System.IO.Path.GetDirectoryName(excelOutPath) ?? ResolveOutDir(a, doc));

                    string[] headers = {
                        "Seq", "Id", "Handle", "Area(m2)", "Perimeter(m)",
                        "BottomElev(m)", "Slope(1:m)", "StartElevMin(m)", "StartElevMax(m)",
                        "BandWidthMax(m)", "EstCut(m3)", "EstFill(m3)", "EstMeanDepth(m)", "Notes"
                    };
                    var rows = new List<object[]>();
                    int i2 = 0;
                    foreach (JsonNode n in perRegion)
                    {
                        var r = (JsonObject)n;
                        i2++;
                        rows.Add(new object[] {
                            i2,
                            r["id"] != null ? r["id"].ToString() : "",
                            r["handle"] != null ? r["handle"].ToString() : "",
                            r["area"] != null ? r["area"].ToString() : "",
                            r["perimeter"] != null ? r["perimeter"].ToString() : "",
                            r["bottom_elev"] != null ? r["bottom_elev"].ToString() : "",
                            r["slope_ratio_m"] != null ? r["slope_ratio_m"].ToString() : "",
                            r["z_top_min"] != null ? r["z_top_min"].ToString() : "",
                            r["z_top_max"] != null ? r["z_top_max"].ToString() : "",
                            r["band_max"] != null ? r["band_max"].ToString() : "",
                            r["est_cut_volume"] != null ? r["est_cut_volume"].ToString() : "",
                            r["est_fill_volume"] != null ? r["est_fill_volume"].ToString() : "",
                            r["est_mean_depth"] != null ? r["est_mean_depth"].ToString() : "",
                            ""
                        });
                    }
                    rows.Add(new object[] {
                        "Total", "", "", Math.Round(totalArea, 2), "", "", "", "", "", "",
                        Math.Round(totalCut, 2), Math.Round(totalFill, 2), "", ""
                    });
                    excelFiles = Excel.Write(dir, baseName, headers, rows, excelFormat);
                }

                // Read-only node: no Commit, the drawing is untouched
            }

            return new JsonObject
            {
                ["regions"] = perRegion.Count,
                ["total_area"] = Math.Round(totalArea, 2),
                ["total_est_cut_volume"] = Math.Round(totalCut, 2),
                ["total_est_fill_volume"] = Math.Round(totalFill, 2),
                ["per_region"] = perRegion,
                ["excel_files"] = DredgeToJsonArray(excelFiles),
                ["warnings"] = warnings
            };
        }

        static JsonArray DredgeToJsonArray(List<string> items)
        {
            var arr = new JsonArray();
            foreach (string s in items) arr.Add((JsonNode)s);
            return arr;
        }
    }
}
