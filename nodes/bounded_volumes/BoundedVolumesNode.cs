using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// bounded_volumes: report cut/fill of a volume surface per closed boundary (read-only).
    ///
    /// Uses Civil's <c>Surface.GetBoundedVolumes(polygon)</c>, i.e. the numbers you get in the volumes panel
    /// by "adding a bounded volume boundary": cut/fill/net inside the boundary.
    ///
    /// **Do not use calculate_surface_volume for this**: it goes through GetVolumeProperties(),
    /// which returns the whole volume surface; boundary_polyline only ends up in the report text,
    /// so N boundaries give N identical numbers (silently wrong).
    ///
    /// Convention: Cut = existing above design, to be removed; Fill = existing below design, to be filled (relative to the base surface).
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeBoundedVolumes(JsonObject a, Document doc)
        {
            string volName = GetString(a, "volume_surface", null);
            string baseName = GetString(a, "base_surface", null);
            string compName = GetString(a, "comparison_surface", null);
            if (string.IsNullOrWhiteSpace(volName) &&
                (string.IsNullOrWhiteSpace(baseName) || string.IsNullOrWhiteSpace(compName)))
                throw new InvalidOperationException(
                    "Give volume_surface (existing volume surface name), or both base_surface and comparison_surface.");

            string layer = GetString(a, "layer", null);
            var handles = a["handles"] as JsonArray;
            var names = a["names"] as JsonObject;
            double step = GetDouble(a, "sample_step", 1.0);   // boundaries may contain arcs; expand into a point ring by step
            double inset = GetDouble(a, "inset", 0.0);        // shrink the boundary inward to avoid the surface edge
            bool closeRing = GetBool(a, "close_ring", true);  // append the first point at the end
            double cutFactor = GetDouble(a, "cut_factor", 1.0);
            double fillFactor = GetDouble(a, "fill_factor", 1.0);
            bool hasDatum = a["datum_elevation"] != null;
            double datum = GetDouble(a, "datum_elevation", 0.0);
            bool exportExcel = GetBool(a, "export_excel", false);
            string excelFormat = GetString(a, "excel_format", "xlsx");

            if (step <= 0) throw new InvalidOperationException("sample_step must be greater than 0.");
            if (string.IsNullOrWhiteSpace(layer) && (handles == null || handles.Count == 0))
                throw new InvalidOperationException("Give at least one of layer or handles to select boundaries.");

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (handles != null)
                foreach (JsonNode h in handles) if (h != null) wanted.Add(h.ToString().Trim());

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            var per = new JsonArray();
            var warnings = new JsonArray();
            var rows = new List<object[]>();
            double tArea = 0, tCut = 0, tFill = 0;
            string usedVol = null;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId volId = ObjectId.Null;
                if (!string.IsNullOrWhiteSpace(volName))
                {
                    volId = FindSurfaceId(tr, civ, volName);
                    if (volId.IsNull) throw new InvalidOperationException("Volume surface '" + volName + "' not found.");
                }
                else
                {
                    ObjectId bId = FindSurfaceId(tr, civ, baseName);
                    if (bId.IsNull) throw new InvalidOperationException("Base surface '" + baseName + "' not found.");
                    ObjectId cId = FindSurfaceId(tr, civ, compName);
                    if (cId.IsNull) throw new InvalidOperationException("Comparison surface '" + compName + "' not found.");
                    volName = "Vol_" + Sanitize(baseName) + "_" + Sanitize(compName);
                    ObjectId old = FindSurfaceId(tr, civ, volName);
                    if (!old.IsNull) volId = old;
                    else volId = Autodesk.Civil.DatabaseServices.TinVolumeSurface.Create(volName, bId, cId);
                }
                var vol = (CivSurface)tr.GetObject(volId, OpenMode.ForRead);
                usedVol = vol.Name;
                if (!(vol is Autodesk.Civil.DatabaseServices.TinVolumeSurface))
                    warnings.Add((JsonNode)("'" + usedVol + "' is not a volume surface; the numbers are not cut/fill."));

                int seq = 0;
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (pl == null) continue;
                    string handle = pl.Handle.ToString();
                    if (wanted.Count > 0) { if (!wanted.Contains(handle)) continue; }
                    else if (!string.Equals(pl.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;

                    seq++;
                    string label = null;
                    if (names != null && names[handle] != null) label = names[handle].ToString();
                    if (string.IsNullOrEmpty(label)) label = "B" + seq.ToString("00");

                    // Expand the boundary into a point ring: shares DredgeBuildRing with the grading chain; arcs expanded by true arc length
                    DredgeRing ring = DredgeBuildRing(pl, step);
                    if (ring.Count < 3)
                    {
                        warnings.Add((JsonNode)(label + " (" + handle + ") cannot form a ring, skipped."));
                        continue;
                    }
                    // When the boundary sits on the design surface edge Civil reports "illegal bounding polygon";
                    // inset shrinks it slightly toward the centroid to move it inside the surface (default 0 = no shrink)
                    double cx = 0, cy = 0;
                    for (int i = 0; i < ring.Count; i++) { cx += ring.P[i].X; cy += ring.P[i].Y; }
                    cx /= ring.Count; cy /= ring.Count;

                    var pts = new Point3dCollection();
                    for (int i = 0; i < ring.Count; i++)
                    {
                        double px = ring.P[i].X, py = ring.P[i].Y;
                        if (inset > 0)
                        {
                            double dx = px - cx, dy = py - cy, len = Math.Sqrt(dx * dx + dy * dy);
                            if (len > inset) { px -= dx / len * inset; py -= dy / len * inset; }
                        }
                        pts.Add(new Point3d(px, py, 0.0));
                    }
                    if (closeRing && pts.Count > 0) pts.Add(pts[0]);   // append the first point at the end, explicit close

                    double cut = 0, fill = 0, net = 0;
                    string err = null;
                    try
                    {
                        var info = hasDatum ? vol.GetBoundedVolumes(pts, datum)
                                            : vol.GetBoundedVolumes(pts);
                        cut = info.Cut; fill = info.Fill; net = info.Net;
                    }
                    catch (System.Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }

                    double area = 0;
                    try { area = pl.Area; } catch (System.Exception) { }

                    if (err != null)
                    {
                        warnings.Add((JsonNode)(label + " (" + handle + ") volume query failed: " + err));
                    }
                    else
                    {
                        tArea += area; tCut += cut; tFill += fill;
                    }

                    var row = new JsonObject
                    {
                        ["id"] = label,
                        ["handle"] = handle,
                        ["layer"] = pl.Layer,
                        ["area"] = Round(area, 2),
                        ["cut"] = err == null ? (JsonNode)Round(cut, 2) : null,
                        ["fill"] = err == null ? (JsonNode)Round(fill, 2) : null,
                        ["net"] = err == null ? (JsonNode)Round(net, 2) : null,
                        ["cut_adjusted"] = err == null ? (JsonNode)Round(cut * cutFactor, 2) : null,
                        ["fill_adjusted"] = err == null ? (JsonNode)Round(fill * fillFactor, 2) : null,
                        ["ring_points"] = ring.Count,
                        ["error"] = err
                    };
                    per.Add(row);
                    if (exportExcel)
                        rows.Add(new object[] { label, handle, Round(area, 2),
                            err == null ? (object)Round(cut, 2) : null,
                            err == null ? (object)Round(fill, 2) : null,
                            err == null ? (object)Round(net, 2) : null,
                            cutFactor, fillFactor,
                            err == null ? (object)Round(cut * cutFactor, 2) : null,
                            err == null ? (object)Round(fill * fillFactor, 2) : null });
                }
                tr.Commit();
            }

            // Assert: no boundary computed = failure
            if (per.Count == 0)
                throw new InvalidOperationException(
                    "No boundary matched (layer='" + (layer ?? "") + "', " + wanted.Count + " named).");
            bool anyOk = false;
            foreach (JsonNode r in per) if (r["cut"] != null) { anyOk = true; break; }
            if (!anyOk)
            {
                // Carry the reason in the error; do not make the caller dig through warnings
                string first = warnings.Count > 0 ? warnings[0].ToString() : "(no warning; the failure is elsewhere)";
                throw new InvalidOperationException(
                    "Volume query failed for every boundary (" + per.Count + " total). First reason: " + first);
            }

            var files = new JsonArray();
            string outdir = null;
            if (exportExcel)
            {
                outdir = ResolveOutDir(a, doc);
                foreach (string p in Excel.Write(outdir, "BoundedVolumes_" + Sanitize(usedVol),
                                                 HeadersBoundedVol, rows, excelFormat))
                    files.Add(p);
            }

            return new JsonObject
            {
                ["volume_surface"] = usedVol,
                ["boundaries"] = per.Count,
                ["total_area"] = Round(tArea, 2),
                ["total_cut"] = Round(tCut, 2),
                ["total_fill"] = Round(tFill, 2),
                ["total_net"] = Round(tFill - tCut, 2),
                ["cut_factor"] = cutFactor,
                ["fill_factor"] = fillFactor,
                ["total_cut_adjusted"] = Round(tCut * cutFactor, 2),
                ["total_fill_adjusted"] = Round(tFill * fillFactor, 2),
                ["per_boundary"] = per,
                ["excel_files"] = files,
                ["outdir"] = outdir,
                ["warnings"] = warnings
            };
        }

        static readonly string[] HeadersBoundedVol = {
            "No.", "Handle", "Boundary area m2", "Cut m3", "Fill m3", "Net m3",
            "Cut factor", "Fill factor", "Adjusted cut m3", "Adjusted fill m3" };
    }
}
