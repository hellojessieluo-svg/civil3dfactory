using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// Dredge grading design surface node (closed boundary -> bottom elevation + 1:m side slope).
    ///
    /// Model: the boundary polyline is the **top-of-slope line (crest)**, not the toe line.
    /// Per region, the area enclosed by the boundary is cut to bottom_elev, sloping inward and downward from the boundary at 1:m:
    ///
    ///     z(P) = max(bottom_elev, z_top(nearest boundary point) - dist(P, boundary) / m)
    ///
    /// Using a "distance-to-boundary field" instead of per-point normal offsets means concave corners and narrow strips never self-intersect,
    /// and the toe line is automatically the inward offset envelope of the boundary. Slope band width band = (z_top - bottom) x m;
    /// dense sampling inside the band (slope_step), flat bottom with sparse sampling outside (flat_step).
    ///
    /// Top-of-slope elevation z_top, one of three:
    ///   start_elev given      -> fixed around the whole ring (e.g. adjoining an already-dredged area, start at 4.0)
    ///   start_elev_cap given  -> min(existing ground, cap), only clamps the high side
    ///   neither               -> existing ground surface (default, the crest meets the ground naturally)
    ///
    /// Global parameters + per-region overrides in regions = parametric model; survey_dredge_regions produces the parameter skeleton,
    /// and the regions batch mode of calculate_surface_volume computes the volumes.
    /// </summary>
    public static partial class Ops
    {
        const string DredgeAppName = "C3DF_DREDGE";

        // ==================== Shared geometry helpers (also used by survey_dredge_regions) ====================

        /// <summary>Boundary ring: equal-step sample points + top-of-slope elevation per point + segment spatial index.</summary>
        sealed class DredgeRing
        {
            public List<Point2d> P = new List<Point2d>();
            public List<double> Z = new List<double>();   // top-of-slope elevation per point (fill before BuildIndex)
            public List<double> M = new List<double>();   // m of slope 1:m per point (z_segments may give it per segment; default = region value)
            public double TotalLen;                       // total ring length (points sampled at equal arc length; arc length of point i = TotalLen*i/Count)
            public double MinX, MinY, MaxX, MaxY;

            double _cell;
            int _nx, _ny;
            List<int>[] _cells;

            public int Count { get { return P.Count; } }

            public void Recalc()
            {
                MinX = MinY = double.MaxValue;
                MaxX = MaxY = double.MinValue;
                for (int i = 0; i < P.Count; i++)
                {
                    if (P[i].X < MinX) MinX = P[i].X;
                    if (P[i].Y < MinY) MinY = P[i].Y;
                    if (P[i].X > MaxX) MaxX = P[i].X;
                    if (P[i].Y > MaxY) MaxY = P[i].Y;
                }
            }

            /// <summary>Build the segment index. cell must be >= the maximum slope band width, otherwise the 3x3 neighbourhood of Nearest misses segments.</summary>
            public void BuildIndex(double cell)
            {
                _cell = Math.Max(cell, 1.0);
                SizeCells();
                while ((long)_nx * _ny > 2000000L) { _cell *= 2.0; SizeCells(); }

                _cells = new List<int>[_nx * _ny];
                int n = P.Count;
                for (int i = 0; i < n; i++)
                {
                    Point2d a = P[i], b = P[(i + 1) % n];
                    int ix0 = CX(Math.Min(a.X, b.X)), ix1 = CX(Math.Max(a.X, b.X));
                    int iy0 = CY(Math.Min(a.Y, b.Y)), iy1 = CY(Math.Max(a.Y, b.Y));
                    for (int ix = ix0; ix <= ix1; ix++)
                        for (int iy = iy0; iy <= iy1; iy++)
                        {
                            int k = iy * _nx + ix;
                            if (_cells[k] == null) _cells[k] = new List<int>(4);
                            _cells[k].Add(i);
                        }
                }
            }

            void SizeCells()
            {
                _nx = Math.Max(1, (int)Math.Ceiling((MaxX - MinX) / _cell) + 1);
                _ny = Math.Max(1, (int)Math.Ceiling((MaxY - MinY) / _cell) + 1);
            }

            int CX(double x) { int i = (int)Math.Floor((x - MinX) / _cell); return i < 0 ? 0 : (i >= _nx ? _nx - 1 : i); }
            int CY(double y) { int i = (int)Math.Floor((y - MinY) / _cell); return i < 0 ? 0 : (i >= _ny ? _ny - 1 : i); }

            /// <summary>Nearest boundary distance and the top-of-slope elevation there. Returns false when no segment is in the 3x3 neighbourhood (= farther from the boundary than cell).</summary>
            public bool Nearest(double px, double py, out double dist, out double zTop)
            {
                double m;
                return NearestZM(px, py, out dist, out zTop, out m);
            }

            /// <summary>Same as Nearest, also returning the slope m at the nearest point (0 when M is not filled).</summary>
            public bool NearestZM(double px, double py, out double dist, out double zTop, out double mLoc)
            {
                dist = double.MaxValue; zTop = 0.0; mLoc = 0.0;
                bool hit = false;
                int cx = CX(px), cy = CY(py);
                for (int ix = cx - 1; ix <= cx + 1; ix++)
                {
                    if (ix < 0 || ix >= _nx) continue;
                    for (int iy = cy - 1; iy <= cy + 1; iy++)
                    {
                        if (iy < 0 || iy >= _ny) continue;
                        List<int> lst = _cells[iy * _nx + ix];
                        if (lst == null) continue;
                        for (int t = 0; t < lst.Count; t++)
                        {
                            double d, z, m;
                            SegDist(lst[t], px, py, out d, out z, out m);
                            if (d < dist) { dist = d; zTop = z; mLoc = m; hit = true; }
                        }
                    }
                }
                return hit;
            }

            void SegDist(int i, double px, double py, out double d, out double z, out double m)
            {
                int n = P.Count, j = (i + 1) % n;
                Point2d a = P[i], b = P[j];
                double vx = b.X - a.X, vy = b.Y - a.Y;
                double L2 = vx * vx + vy * vy;
                double t = L2 <= 1e-12 ? 0.0 : ((px - a.X) * vx + (py - a.Y) * vy) / L2;
                if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;
                double qx = a.X + t * vx, qy = a.Y + t * vy;
                d = Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
                z = Z[i] + t * (Z[j] - Z[i]);
                m = (M.Count == P.Count) ? M[i] + t * (M[j] - M[i]) : 0.0;
            }
        }

        /// <summary>Sample a closed polyline (arcs included) into a point ring by step.</summary>
        static DredgeRing DredgeBuildRing(Polyline pl, double step)
        {
            var r = new DredgeRing();
            double L;
            try { L = pl.GetDistanceAtParameter(pl.EndParam); }
            catch (System.Exception) { L = 0.0; }
            if (L <= 1e-9) return r;

            int n = Math.Max(16, (int)Math.Ceiling(L / Math.Max(step, 1e-6)));
            for (int i = 0; i < n; i++)
            {
                try
                {
                    Point3d p = pl.GetPointAtDist(L * i / n);
                    r.P.Add(new Point2d(p.X, p.Y));
                }
                catch (System.Exception) { }
            }
            r.TotalLen = L;
            r.Recalc();
            return r;
        }

        /// <summary>Signed area: positive = counter-clockwise. CAD's Polyline.Area is absolute and cannot tell the winding.</summary>
        static double DredgeSignedArea(List<Point2d> p)
        {
            double s = 0.0;
            int n = p.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
                s += p[j].X * p[i].Y - p[i].X * p[j].Y;
            return s / 2.0;
        }

        /// <summary>
        /// Top-of-slope elevation rules: not every segment of a boundary is the same.
        ///   Bottom              design bottom elevation
        ///   HasStart/Start      fixed top-of-slope elevation around the whole ring
        ///   HasCap/Cap          top-of-slope cap min(ground, cap): for adjoining already-dredged areas (e.g. lotus dredging zones)
        ///   Interfaces/Tol/Elev interface lines: boundary segments within Tol of these lines get their top elevation clamped to Elev (default = Bottom,
        ///                       i.e. no slope on that segment): the segments where an intersection meets a channel, whose slope comes from the channel itself
        /// </summary>
        sealed class DredgeZSpec
        {
            public double Bottom;
            public double SlopeM;
            public bool HasStart; public double Start;
            public bool HasCap; public double Cap;
            public CivSurface StartSurface;          // top-of-slope reference surface (e.g. the designed channel surface); default existing ground
            public List<Curve> Interfaces;
            public double InterfaceTol;
            public double InterfaceElev;
            public string Mode;                      // for reporting: top-of-slope elevation rule
            public List<DredgeZSeg> Segs;            // per-segment top elevation mode (ring arc length); if given it is authoritative and overrides the interface rule
        }

        /// <summary>Top elevation segment: within ring arc length [S0,S1] the top is taken by Mode.
        /// Modes: ground = follow existing ground; cap = min(ground, Value); fixed = Value; bottom = bottom elevation, no slope.</summary>
        sealed class DredgeZSeg
        {
            public double S0, S1;
            public string Mode;
            public double Value;
            public double M;    // m of slope 1:m for this segment; 0 = use the region value
        }

        static List<DredgeZSeg> DredgeParseZSegs(JsonArray arr)
        {
            if (arr == null || arr.Count == 0) return null;
            var list = new List<DredgeZSeg>();
            foreach (JsonNode n in arr)
            {
                var o = n as JsonObject;
                if (o == null) continue;
                string mode = GetString(o, "mode", "").Trim().ToLowerInvariant();
                switch (mode)
                {
                    case "Ground": case "ground": mode = "ground"; break;
                    case "Cap": case "cap": mode = "cap"; break;
                    case "Fixed": case "fixed": mode = "fixed"; break;
                    case "Bottom": case "bottom": mode = "bottom"; break;
                    default:
                        throw new InvalidOperationException(
                            "Unknown z_segments.mode '" + mode + "' (supported: ground/cap/fixed/bottom).");
                }
                if ((mode == "cap" || mode == "fixed") && o["value"] == null)
                    throw new InvalidOperationException("z_segments mode '" + mode + "' requires value.");
                double sm = GetDouble(o, "m", 0.0);
                if (sm < 0 || sm > 50)
                    throw new InvalidOperationException("z_segments.m out of range (0~50).");
                list.Add(new DredgeZSeg
                {
                    S0 = GetDouble(o, "s0", 0.0),
                    S1 = GetDouble(o, "s1", 0.0),
                    Mode = mode,
                    Value = GetDouble(o, "value", 0.0),
                    M = sm
                });
            }
            return list.Count > 0 ? list : null;
        }

        /// <summary>
        /// Merge global parameters + per-region spec into the region's effective parameters. survey_dredge_regions and create_dredge_grading
        /// share this single merge rule, so the parameters trialled during the survey are exactly the ones used to build the surface; no drift.
        /// </summary>
        static DredgeZSpec DredgeMergeZSpec(JsonObject a, JsonObject spec, double gBottom, double gSlope,
            CivSurface startSurface, List<Curve> interfaces)
        {
            double bottom = spec != null ? GetDouble(spec, "bottom_elev", gBottom) : gBottom;
            double m = spec != null ? GetDouble(spec, "slope_ratio_m", gSlope) : gSlope;

            bool hasStart = a["start_elev"] != null;
            double start = GetDouble(a, "start_elev", 0.0);
            bool hasCap = a["start_elev_cap"] != null;
            double cap = GetDouble(a, "start_elev_cap", 0.0);
            bool hasIface = a["interface_start_elev"] != null;
            double iface = GetDouble(a, "interface_start_elev", bottom);
            if (spec != null)
            {
                if (spec["start_elev"] != null) { hasStart = true; start = GetDouble(spec, "start_elev", start); }
                if (spec["start_elev_cap"] != null) { hasCap = true; cap = GetDouble(spec, "start_elev_cap", cap); }
                if (spec["interface_start_elev"] != null) { hasIface = true; iface = GetDouble(spec, "interface_start_elev", iface); }
            }

            string mode = hasStart ? "fixed"
                        : (startSurface != null ? (hasCap ? "surface_capped" : "surface")
                                                : (hasCap ? "ground_capped" : "ground"));

            return new DredgeZSpec
            {
                Bottom = bottom,
                SlopeM = m,
                HasStart = hasStart, Start = start,
                HasCap = hasCap, Cap = cap,
                StartSurface = startSurface,
                Interfaces = interfaces,
                InterfaceTol = GetDouble(a, "interface_tolerance", 2.0),
                InterfaceElev = hasIface ? iface : bottom,
                Mode = mode,
                Segs = spec != null ? DredgeParseZSegs(spec["z_segments"] as JsonArray) : null
            };
        }

        /// <summary>
        /// Fill ring.Z (top-of-slope elevation) per spec. Order of precedence:
        /// fixed value -> start_surface -> existing ground -> bottom elevation if neither samples; then apply cap, then interface lines, finally never below bottom.
        /// offSurface = number of points neither surface could sample; onInterface = number of points clamped by interface lines.
        /// </summary>
        static void DredgeFillRingZ(DredgeRing ring, CivSurface ground, DredgeZSpec spec,
            out int offSurface, out int onInterface)
        {
            offSurface = 0;
            onInterface = 0;
            ring.Z.Clear();
            ring.M.Clear();
            for (int i = 0; i < ring.Count; i++)
            {
                var p3 = new Point3d(ring.P[i].X, ring.P[i].Y, 0);
                double z;

                // Per-segment top elevation mode (ring arc length): a hit is authoritative; global rule and interface lines are skipped
                DredgeZSeg seg = null;
                if (spec.Segs != null)
                {
                    double s = ring.TotalLen * i / ring.Count;
                    foreach (DredgeZSeg zg in spec.Segs)
                        if (s >= zg.S0 - 1e-9 && s <= zg.S1 + 1e-9) { seg = zg; break; }
                }
                if (seg != null)
                {
                    double zs2;
                    switch (seg.Mode)
                    {
                        case "bottom": z = spec.Bottom; onInterface++; break;
                        case "fixed": z = seg.Value; break;
                        case "cap":
                            if (GroundAt(ground, spec, p3, out zs2)) z = Math.Min(zs2, seg.Value);
                            else { offSurface++; z = spec.Bottom; }
                            break;
                        default:   // ground
                            if (GroundAt(ground, spec, p3, out zs2)) z = zs2;
                            else { offSurface++; z = spec.Bottom; }
                            break;
                    }
                    if (z < spec.Bottom) z = spec.Bottom;
                    ring.Z.Add(z);
                    ring.M.Add(seg.M > 0 ? seg.M : spec.SlopeM);
                    continue;
                }

                if (spec.HasStart) z = spec.Start;
                else
                {
                    double zs;
                    if (spec.StartSurface != null && GridTrySample(spec.StartSurface, p3, out zs)) z = zs;
                    else if (ground != null && GridTrySample(ground, p3, out zs)) z = zs;
                    else { offSurface++; z = spec.Bottom; }   // unsampled = no slope; better to under-cut than cut wildly
                    if (spec.HasCap && z > spec.Cap) z = spec.Cap;
                }

                if (spec.Interfaces != null && spec.Interfaces.Count > 0 &&
                    DredgeNearAnyCurve(spec.Interfaces, ring.P[i], spec.InterfaceTol))
                {
                    z = spec.InterfaceElev;
                    onInterface++;
                }

                if (z < spec.Bottom) z = spec.Bottom;   // ground already below design bottom: no slope here
                ring.Z.Add(z);
                ring.M.Add(spec.SlopeM);
            }
        }

        /// <summary>Sample the top-of-slope reference: start_surface first, fall back to existing ground.</summary>
        static bool GroundAt(CivSurface ground, DredgeZSpec spec, Point3d p3, out double z)
        {
            if (spec.StartSurface != null && GridTrySample(spec.StartSurface, p3, out z)) return true;
            if (ground != null && GridTrySample(ground, p3, out z)) return true;
            z = 0;
            return false;
        }

        static bool DredgeNearAnyCurve(List<Curve> curves, Point2d p, double tol)
        {
            var p3 = new Point3d(p.X, p.Y, 0);
            for (int i = 0; i < curves.Count; i++)
            {
                try
                {
                    Point3d q = curves[i].GetClosestPointTo(p3, Vector3d.ZAxis, false);
                    double dx = q.X - p.X, dy = q.Y - p.Y;
                    if (dx * dx + dy * dy <= tol * tol) return true;
                }
                catch (System.Exception) { }
            }
            return false;
        }

        /// <summary>Collect interface lines: every curve on the given layers (polylines/lines/arcs).</summary>
        static List<Curve> DredgeCollectInterfaces(Transaction tr, Database db, JsonArray layers)
        {
            var list = new List<Curve>();
            if (layers == null || layers.Count == 0) return list;
            var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonNode n in layers) if (n != null) want.Add(n.ToString().Trim());

            foreach (ObjectId id in ModelSpace(db, tr))
            {
                var c = tr.GetObject(id, OpenMode.ForRead) as Curve;
                if (c == null) continue;
                if (!want.Contains(c.Layer)) continue;
                list.Add(c);
            }
            return list;
        }

        struct DredgeText { public Point2d P; public string S; public string Layer; }

        /// <summary>Collect single/multi-line texts in the drawing, to auto-name regions by "text inside boundary".</summary>
        static List<DredgeText> DredgeCollectTexts(Transaction tr, Database db, string layerFilter)
        {
            var list = new List<DredgeText>();
            bool all = string.IsNullOrEmpty(layerFilter);
            foreach (ObjectId id in ModelSpace(db, tr))
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;

                string s = null;
                Point3d pos = Point3d.Origin;
                var t = ent as DBText;
                if (t != null) { s = t.TextString; pos = t.Position; }
                else
                {
                    var mt = ent as MText;
                    if (mt != null) { s = mt.Text; pos = mt.Location; }
                }
                if (string.IsNullOrEmpty(s)) continue;

                if (all)
                {
                    // Default rule: C3DF-* layers are the output of this factory's annotation nodes (grid elevations etc.), not region names
                    if (ent.Layer.StartsWith("C3DF-", StringComparison.OrdinalIgnoreCase)) continue;
                }
                else if (!string.Equals(ent.Layer, layerFilter, StringComparison.OrdinalIgnoreCase)) continue;

                list.Add(new DredgeText { P = new Point2d(pos.X, pos.Y), S = s.Trim(), Layer = ent.Layer });
            }
            return list;
        }

        /// <summary>Collect closed polylines (boundaries) by layer + optional handle whitelist.</summary>
        static List<ObjectId> DredgeCollectBoundaries(Transaction tr, Database db, string layer, JsonArray handleWhitelist)
        {
            HashSet<string> want = null;
            if (handleWhitelist != null && handleWhitelist.Count > 0)
            {
                want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonNode h in handleWhitelist)
                    if (h != null) want.Add(h.ToString().Trim());
            }

            var ids = new List<ObjectId>();
            foreach (ObjectId id in ModelSpace(db, tr))
            {
                var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                if (pl == null) continue;
                if (!string.IsNullOrEmpty(layer) &&
                    !string.Equals(pl.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
                if (!pl.Closed) continue;
                if (want != null && !want.Contains(pl.Handle.ToString())) continue;
                ids.Add(id);
            }
            return ids;
        }

        static void DredgeTag(DBObject obj, string kind)
        {
            obj.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, DredgeAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, kind));
        }

        /// <summary>Required numeric parameter: report the name when missing instead of silently using 0.</summary>
        static double DredgeNeedDouble(JsonObject a, string key)
        {
            if (a[key] == null)
                throw new InvalidOperationException("Missing required parameter '" + key + "'.");
            return GetDouble(a, key, 0.0);
        }

        static bool DredgeIsTagged(DBObject obj)
        {
            ResultBuffer rb = obj.GetXDataForApplication(DredgeAppName);
            if (rb == null) return false;
            rb.Dispose();
            return true;
        }

        // ==================== Node body ====================

        static JsonNode RunNodeCreateDredgeGrading(JsonObject a, Document doc)
        {
            string bndLayer = Need(a, "boundary_layer");
            string surfName = Need(a, "surface");
            double gBottom = DredgeNeedDouble(a, "bottom_elev");
            double gSlope = DredgeNeedDouble(a, "slope_ratio_m");
            if (gSlope <= 0) throw new InvalidOperationException("slope_ratio_m must be greater than 0 (the m of 1:m).");

            string startSurfName = GetString(a, "start_surface", null);

            double sampleStep = GetDouble(a, "sample_step", 2.0);
            double slopeStep = GetDouble(a, "slope_step", 2.0);
            double flatStep = GetDouble(a, "flat_step", 20.0);
            if (sampleStep <= 0) throw new InvalidOperationException("sample_step must be greater than 0.");
            if (slopeStep <= 0) throw new InvalidOperationException("slope_step must be greater than 0.");
            if (flatStep < slopeStep) flatStep = slopeStep;

            string prefix = GetString(a, "surface_prefix", "DredgeDesign-");
            string surfLayer = GetString(a, "surface_layer", "C3DF-DREDGE-SURFACE");
            string crestLayer = GetString(a, "crest_layer", "C3DF-DREDGE-TOP");
            string toeLayer = GetString(a, "toe_layer", "C3DF-DREDGE-TOE");
            bool drawCrest = GetBool(a, "draw_crest_line", true);
            bool drawToe = GetBool(a, "draw_toe_line", true);
            bool clearExisting = GetBool(a, "clear_existing", true);
            string labelLayer = GetString(a, "label_layer", null);

            var interfaceLayers = a["interface_layers"] as JsonArray;
            var regionSpecs = a["regions"] as JsonArray;
            var handles = a["boundaries"] as JsonArray;

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            var warnings = new JsonArray();
            var perRegion = new JsonArray();
            int madeSurfaces = 0, madeCrest = 0, madeToe = 0, cleared = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId gsId = FindSurfaceId(tr, civ, surfName);
                if (gsId.IsNull) throw new InvalidOperationException("Existing ground surface '" + surfName + "' not found.");
                var ground = (CivSurface)tr.GetObject(gsId, OpenMode.ForRead);

                CivSurface startSurface = null;
                if (!string.IsNullOrEmpty(startSurfName))
                {
                    ObjectId ssId = FindSurfaceId(tr, civ, startSurfName);
                    if (ssId.IsNull) throw new InvalidOperationException("Top-of-slope surface '" + startSurfName + "' not found.");
                    startSurface = (CivSurface)tr.GetObject(ssId, OpenMode.ForRead);
                }

                List<ObjectId> bndIds = DredgeCollectBoundaries(tr, db, bndLayer, handles);
                if (bndIds.Count == 0)
                    throw new InvalidOperationException(
                        "No closed polyline on layer '" + bndLayer + "'" +
                        (handles != null && handles.Count > 0 ? " (or none of the whitelisted handles matched)" : "") + ".");

                List<DredgeText> texts = DredgeCollectTexts(tr, db, labelLayer);
                List<Curve> interfaces = DredgeCollectInterfaces(tr, db, interfaceLayers);
                if (interfaceLayers != null && interfaceLayers.Count > 0 && interfaces.Count == 0)
                    warnings.Add((JsonNode)"interface_layers given but those layers contain no curve; the interface rule had no effect.");

                if (clearExisting) cleared = DredgeClearOld(tr, db, civ, prefix);

                EnsureRegApp(tr, db, DredgeAppName);
                ObjectId lyCrest = GridEnsureLayer(tr, db, crestLayer, 1);
                ObjectId lyToe = GridEnsureLayer(tr, db, toeLayer, 3);
                ObjectId lySurf = GridEnsureLayer(tr, db, surfLayer, 7);
                var btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int seq = 0;

                foreach (ObjectId bid in bndIds)
                {
                    seq++;
                    var pl = (Polyline)tr.GetObject(bid, OpenMode.ForRead);
                    string handle = pl.Handle.ToString();
                    var rw = new JsonArray();

                    DredgeRing ring = DredgeBuildRing(pl, sampleStep);
                    if (ring.Count < 3)
                    {
                        warnings.Add((JsonNode)("Boundary " + handle + " has too few sample points, skipped."));
                        continue;
                    }

                    // Region name: text inside the boundary first, else the index
                    string label = null;
                    foreach (DredgeText t in texts)
                        if (GridPointInPolygon(t.P, ring.P)) { label = t.S; break; }

                    // Per-region parameters: match by handle first, then region name, else global
                    JsonObject spec = DredgeFindSpec(regionSpecs, handle, label);
                    string id = spec != null ? GetString(spec, "id", null) : null;
                    if (string.IsNullOrEmpty(id)) id = label;
                    if (string.IsNullOrEmpty(id)) id = "R" + seq.ToString("00", CultureInfo.InvariantCulture);

                    DredgeZSpec zspec = DredgeMergeZSpec(a, spec, gBottom, gSlope, startSurface, interfaces);
                    double bottom = zspec.Bottom;
                    double m = zspec.SlopeM;
                    if (m <= 0)
                        throw new InvalidOperationException("slope_ratio_m of region '" + id + "' must be greater than 0.");

                    // ---- Top-of-slope elevation ----
                    int offSurface, onInterface;
                    DredgeFillRingZ(ring, ground, zspec, out offSurface, out onInterface);

                    double zSum = 0, zMin = double.MaxValue, zMax = double.MinValue;
                    for (int i = 0; i < ring.Count; i++)
                    {
                        double z = ring.Z[i];
                        zSum += z;
                        if (z < zMin) zMin = z;
                        if (z > zMax) zMax = z;
                    }
                    if (offSurface > 0)
                        rw.Add((JsonNode)(offSurface + "/" + ring.Count + " boundary sample points fall outside the existing ground surface; treated as design bottom (no slope there)"));

                    string sName = prefix + id;
                    string uniq = sName;
                    int dup = 1;
                    while (usedNames.Contains(uniq)) { dup++; uniq = sName + "-" + dup.ToString(CultureInfo.InvariantCulture); }
                    sName = uniq;
                    usedNames.Add(sName);

                    DredgeSurfaceOut so = DredgeBuildSurface(tr, db, civ, btr, ring, bottom, sName,
                        lySurf, lyCrest, lyToe, drawCrest, drawToe, slopeStep, flatStep);
                    foreach (string note in so.Notes) rw.Add((JsonNode)note);
                    madeSurfaces++;
                    madeCrest += so.CrestMade;
                    madeToe += so.ToeMade;
                    double bandMax = so.BandMax;

                    var row = new JsonObject
                    {
                        ["id"] = id,
                        ["label"] = label,
                        ["handle"] = handle,
                        ["area"] = Math.Round(pl.Area, 2),
                        ["perimeter"] = Math.Round(pl.Length, 2),
                        ["bottom_elev"] = bottom,
                        ["slope_ratio_m"] = m,
                        ["start_elev_mode"] = zspec.Mode,
                        ["start_elev"] = zspec.HasStart ? (JsonNode)zspec.Start : null,
                        ["start_elev_cap"] = zspec.HasCap ? (JsonNode)zspec.Cap : null,
                        ["interface_points"] = onInterface,
                        ["interface_start_elev"] = interfaces.Count > 0 ? (JsonNode)zspec.InterfaceElev : null,
                        ["z_top_min"] = Math.Round(zMin, 3),
                        ["z_top_max"] = Math.Round(zMax, 3),
                        ["z_top_mean"] = Math.Round(zSum / ring.Count, 3),
                        ["band_max"] = Math.Round(bandMax, 2),
                        ["surface"] = sName,
                        ["ring_points"] = ring.Count,
                        ["toe_points"] = so.ToePoints,
                        ["grid_points"] = so.GridPoints,
                        ["vertices"] = so.Vertices,
                        ["warnings"] = rw
                    };
                    perRegion.Add(row);

                    for (int i = 0; i < rw.Count; i++)
                        warnings.Add((JsonNode)("[" + id + "] " + rw[i].ToString()));

                    if (spec != null)
                    {
                        string channel = GetString(spec, "channel", null);
                        string note = GetString(spec, "note", null);
                        if (!string.IsNullOrEmpty(channel)) row["channel"] = channel;
                        if (!string.IsNullOrEmpty(note)) row["note"] = note;
                    }
                }

                // Regions named in regions but not found in the drawing are reported explicitly, never silently
                if (regionSpecs != null)
                {
                    foreach (JsonNode n in regionSpecs)
                    {
                        var s = n as JsonObject;
                        if (s == null) continue;
                        string key = GetString(s, "handle", null);
                        if (string.IsNullOrEmpty(key)) key = GetString(s, "label", null);
                        if (string.IsNullOrEmpty(key)) key = GetString(s, "id", null);
                        if (string.IsNullOrEmpty(key)) continue;
                        bool used = false;
                        foreach (JsonNode r in perRegion)
                        {
                            var ro = (JsonObject)r;
                            if (string.Equals(ro["handle"].ToString(), key, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(ro["id"].ToString(), key, StringComparison.OrdinalIgnoreCase) ||
                                (ro["label"] != null && string.Equals(ro["label"].ToString(), key, StringComparison.OrdinalIgnoreCase)))
                            { used = true; break; }
                        }
                        if (!used) warnings.Add((JsonNode)("'" + key + "' in regions has no matching boundary in the drawing, ignored."));
                    }
                }

                tr.Commit();
            }

            return new JsonObject
            {
                ["regions"] = perRegion.Count,
                ["surfaces"] = madeSurfaces,
                ["crest_lines"] = madeCrest,
                ["toe_lines"] = madeToe,
                ["cleared_old"] = cleared,
                ["warnings"] = warnings,
                ["per_region"] = perRegion
            };
        }

        sealed class DredgeSurfaceOut
        {
            public string Surface;
            public double BandMax;
            public int ToePoints, GridPoints, Vertices, CrestMade, ToeMade;
            public List<string> Notes = new List<string>();
        }

        /// <summary>Shared surface-building stage (used by create_dredge_grading and dredge_from_feature_lines):
        /// ring (Z/M filled) -> slope band index -> ring points + toe points + grid points -> TIN + outer boundary clip -> crest/toe lines.
        /// An old surface with the same name is deleted and rebuilt in place.</summary>
        static DredgeSurfaceOut DredgeBuildSurface(Transaction tr, Database db, CivDoc civ, BlockTableRecord btr,
            DredgeRing ring, double bottom, string sName,
            ObjectId lySurf, ObjectId lyCrest, ObjectId lyToe, bool drawCrest, bool drawToe,
            double slopeStep, double flatStep)
        {
            var outp = new DredgeSurfaceOut { Surface = sName };

            double bandMax = 0.0;
            for (int i = 0; i < ring.Count; i++)
            {
                double bi = (ring.Z[i] - bottom) * ring.M[i];
                if (bi > bandMax) bandMax = bi;
            }
            outp.BandMax = bandMax;
            if (bandMax <= 1e-6)
                outp.Notes.Add("Top-of-slope elevation is nowhere above the design bottom; this region is a pure flat bottom (no slope band)");

            ring.BuildIndex(Math.Max(bandMax * 1.05, Math.Max(flatStep, 5.0)));

            // ---- Surface points: boundary ring + toe ring + dense slope band + sparse flat bottom ----
            var pts = new Point3dCollection();
            for (int i = 0; i < ring.Count; i++)
                pts.Add(new Point3d(ring.P[i].X, ring.P[i].Y, ring.Z[i]));

            var toePts = new List<Point3d>();
            double[] nx, ny;
            DredgeInwardNormals(ring, out nx, out ny);
            for (int i = 0; i < ring.Count; i++)
            {
                double band = (ring.Z[i] - bottom) * ring.M[i];
                if (band <= 1e-6) continue;
                double px = ring.P[i].X + nx[i] * band;
                double py = ring.P[i].Y + ny[i] * band;
                // Check: a true toe point must be inside the region and its distance to the boundary must equal the local band width.
                // At concave corners a normal offset runs outside or onto the opposite side; this step removes those.
                if (!GridPointInPolygon(new Point2d(px, py), ring.P)) continue;
                double d, zt;
                if (!ring.Nearest(px, py, out d, out zt)) continue;
                if (d < band * 0.9 - 1e-9) continue;
                toePts.Add(new Point3d(px, py, bottom));
                pts.Add(new Point3d(px, py, bottom));
            }

            int coarse = Math.Max(1, (int)Math.Round(flatStep / slopeStep));
            int gx0 = (int)Math.Floor(ring.MinX / slopeStep) + 1;
            int gx1 = (int)Math.Ceiling(ring.MaxX / slopeStep) - 1;
            int gy0 = (int)Math.Floor(ring.MinY / slopeStep) + 1;
            int gy1 = (int)Math.Ceiling(ring.MaxY / slopeStep) - 1;
            int gridPts = 0;
            for (int ix = gx0; ix <= gx1; ix++)
            {
                double x = ix * slopeStep;
                for (int iy = gy0; iy <= gy1; iy++)
                {
                    double y = iy * slopeStep;
                    if (!GridPointInPolygon(new Point2d(x, y), ring.P)) continue;

                    double d, zTop, mLoc;
                    double z;
                    if (!ring.NearestZM(x, y, out d, out zTop, out mLoc)) z = bottom;   // farther from the boundary than the index cell = flat bottom
                    else
                    {
                        z = zTop - d / (mLoc > 0 ? mLoc : 5.0);
                        if (z < bottom) z = bottom;
                    }
                    // keep only sparse grid points on the flat bottom, all points inside the slope band
                    if (z <= bottom + 1e-9 && (ix % coarse != 0 || iy % coarse != 0)) continue;
                    pts.Add(new Point3d(x, y, z));
                    gridPts++;
                }
            }

            // ---- Build TIN + clip by outer boundary ----
            ObjectId oldId = FindSurfaceId(tr, civ, sName);
            if (!oldId.IsNull)
            {
                var oldObj = tr.GetObject(oldId, OpenMode.ForWrite);
                oldObj.Erase();
            }

            ObjectId sid = CivTinSurface.Create(db, sName);
            var ts = (CivTinSurface)tr.GetObject(sid, OpenMode.ForWrite);
            ts.LayerId = lySurf;
            ts.AddVertices(pts);

            var bpts = new Point3dCollection();
            for (int i = 0; i < ring.Count; i++)
                bpts.Add(new Point3d(ring.P[i].X, ring.P[i].Y, ring.Z[i]));
            try
            {
                ts.BoundariesDefinition.AddBoundaries(bpts, 1.0, SurfaceBoundaryType.Outer, false);
            }
            catch (System.Exception ex)
            {
                outp.Notes.Add("Outer boundary clip failed; surface triangulated by convex hull: " + ex.Message);
            }

            // ---- Crest line (3D version of the boundary) and toe line ----
            if (drawCrest)
            {
                var cp = new Polyline3d(Poly3dType.SimplePoly, bpts, true);
                cp.LayerId = lyCrest;
                btr.AppendEntity(cp);
                tr.AddNewlyCreatedDBObject(cp, true);
                DredgeTag(cp, "CREST");
                outp.CrestMade++;
            }
            if (drawToe && toePts.Count >= 3)
            {
                var tp3 = new Point3dCollection();
                foreach (Point3d p in toePts) tp3.Add(p);
                var tp = new Polyline3d(Poly3dType.SimplePoly, tp3, true);
                tp.LayerId = lyToe;
                btr.AppendEntity(tp);
                tr.AddNewlyCreatedDBObject(tp, true);
                DredgeTag(tp, "TOE");
                outp.ToeMade++;
            }
            else if (drawToe)
            {
                outp.Notes.Add("Fewer than 3 toe points; toe line not drawn (slope band too narrow or whole region flat)");
            }

            outp.ToePoints = toePts.Count;
            outp.GridPoints = gridPts;
            outp.Vertices = pts.Count;
            return outp;
        }

        /// <summary>Per-region parameter lookup: exact handle match first, then region name (text).</summary>
        static JsonObject DredgeFindSpec(JsonArray specs, string handle, string label)
        {
            if (specs == null) return null;
            foreach (JsonNode n in specs)
            {
                var s = n as JsonObject;
                if (s == null) continue;
                string h = GetString(s, "handle", null);
                if (!string.IsNullOrEmpty(h) && string.Equals(h, handle, StringComparison.OrdinalIgnoreCase)) return s;
            }
            if (string.IsNullOrEmpty(label)) return null;
            foreach (JsonNode n in specs)
            {
                var s = n as JsonObject;
                if (s == null) continue;
                if (!string.IsNullOrEmpty(GetString(s, "handle", null))) continue;
                string l = GetString(s, "label", null);
                if (string.IsNullOrEmpty(l)) l = GetString(s, "id", null);
                if (!string.IsNullOrEmpty(l) && string.Equals(l, label, StringComparison.OrdinalIgnoreCase)) return s;
            }
            return null;
        }

        /// <summary>Inward normal (unit vector) per point; winding determined by signed area.</summary>
        static void DredgeInwardNormals(DredgeRing ring, out double[] nx, out double[] ny)
        {
            int n = ring.Count;
            nx = new double[n];
            ny = new double[n];
            bool ccw = DredgeSignedArea(ring.P) > 0;
            for (int i = 0; i < n; i++)
            {
                Point2d a = ring.P[(i - 1 + n) % n], b = ring.P[(i + 1) % n];
                double vx = b.X - a.X, vy = b.Y - a.Y;
                double len = Math.Sqrt(vx * vx + vy * vy);
                if (len <= 1e-12) { nx[i] = 0; ny[i] = 0; continue; }
                vx /= len; vy /= len;
                // for a counter-clockwise ring the inside is the left normal (-vy, vx)
                nx[i] = ccw ? -vy : vy;
                ny[i] = ccw ? vx : -vx;
            }
        }

        /// <summary>Clean previous output: surfaces with this node's prefix + lines carrying C3DF_DREDGE XData.</summary>
        static int DredgeClearOld(Transaction tr, Database db, CivDoc civ, string prefix)
        {
            int n = 0;
            if (!string.IsNullOrEmpty(prefix))
            {
                foreach (ObjectId sid in civ.GetSurfaceIds())
                {
                    var s = tr.GetObject(sid, OpenMode.ForRead) as CivSurface;
                    if (s == null) continue;
                    if (!s.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    tr.GetObject(sid, OpenMode.ForWrite).Erase();
                    n++;
                }
            }
            foreach (ObjectId id in ModelSpace(db, tr))
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || ent.IsErased) continue;
                if (!(ent is Polyline3d) && !(ent is Polyline) && !(ent is Line)) continue;
                if (!DredgeIsTagged(ent)) continue;
                ent.UpgradeOpen();
                ent.Erase();
                n++;
            }
            return n;
        }
    }
}
