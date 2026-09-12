using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Aec.PropertyData.DatabaseServices;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;
using CivFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivFlPointType = Autodesk.Civil.FeatureLinePointType;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// Feature-line workflow (settled in the Project B L4 refactor, 2026-08-24; memory feedback-design-input-draggable-lines):
    /// design truth = feature lines carrying classification properties (drawn by people); automation = chain into a ring + distance-field grading + build TIN (consume the lines).
    /// create_feature_lines upgrades curves to FeatureLines (three elevation modes); dredge_from_feature_lines
    /// chains a classified set of lines into a ring and produces the design surface directly, reusing the surface-building code of create_dredge_grading with zero algorithm duplication.
    /// v1 rule: arcs are densified into polylines by densify_step when lines are created (an R459 arc with 10 m chords has 2.7 cm sagitta, geometrically negligible).
    /// </summary>
    public static partial class Ops
    {
        // ==================== create_feature_lines ====================

        static JsonNode CreateFeatureLines(JsonObject a, Document doc)
        {
            var items = a["items"] as JsonArray;
            if (items == null || items.Count == 0)
                throw new InvalidOperationException("items is required: [{handle,name,z_mode,...}].");
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var made = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                foreach (JsonNode n in items)
                {
                    var it = (JsonObject)n;
                    string h = Need(it, "handle");
                    string name = Need(it, "name");
                    string zMode = GetString(it, "z_mode", "keep").ToLowerInvariant();
                    double zConst = GetDouble(it, "z", 0.0);
                    string surfName = GetString(it, "surface", null);
                    double step = GetDouble(it, "densify_step", 10.0);
                    string layer = GetString(it, "layer", null);
                    bool eraseSource = GetBool(it, "erase_source", true);

                    var src = tr.GetObject(ResolveHandle(db, h), OpenMode.ForRead) as Curve;
                    if (src == null) throw new InvalidOperationException(h + " is not a curve.");

                    // Sampling: vertices always kept, arcs densified by densify_step (v1: arcs in feature lines = densified polylines)
                    var pts = new Point3dCollection();
                    var pl = src as Polyline;
                    if (pl != null)
                    {
                        int nv = pl.NumberOfVertices;
                        for (int i = 0; i < nv; i++)
                        {
                            Point3d vp = pl.GetPoint3dAt(i);
                            pts.Add(vp);
                            if (i == nv - 1 && !pl.Closed) break;
                            double bulge = pl.GetBulgeAt(i);
                            if (Math.Abs(bulge) < 1e-9) continue;
                            double d0 = pl.GetDistanceAtParameter(i);
                            double d1 = (i == nv - 1)
                                ? pl.GetDistanceAtParameter(pl.EndParam)
                                : pl.GetDistanceAtParameter(i + 1);
                            int nseg = Math.Max(2, (int)Math.Ceiling((d1 - d0) / Math.Max(step, 0.5)));
                            for (int k = 1; k < nseg; k++)
                                pts.Add(pl.GetPointAtDist(d0 + (d1 - d0) * k / nseg));
                        }
                    }
                    else
                    {
                        // Other curves (3D polylines etc.): sample at equal spacing, step densify_step
                        double L = src.GetDistanceAtParameter(src.EndParam);
                        int nseg = Math.Max(1, (int)Math.Ceiling(L / Math.Max(step, 0.5)));
                        for (int k = 0; k <= nseg; k++)
                            pts.Add(src.GetPointAtDist(Math.Min(L, L * k / nseg)));
                    }

                    // Elevation
                    var pts2 = new Point3dCollection();
                    foreach (Point3d p in pts)
                    {
                        double z = zMode == "const" ? zConst
                                 : zMode == "surface" ? 0.0
                                 : p.Z;
                        pts2.Add(new Point3d(p.X, p.Y, z));
                    }

                    var tmp = new Polyline3d(Poly3dType.SimplePoly, pts2, false);
                    btr.AppendEntity(tmp);
                    tr.AddNewlyCreatedDBObject(tmp, true);
                    ObjectId flId = CivFeatureLine.Create(name, tmp.ObjectId);
                    if (!tmp.IsErased) { tmp.UpgradeOpen(); tmp.Erase(); }   // no need to erase if Create already consumed the source

                    var fl = (CivFeatureLine)tr.GetObject(flId, OpenMode.ForWrite);
                    if (!string.IsNullOrEmpty(layer))
                    {
                        fl.LayerId = GridEnsureLayer(tr, db, layer, 4);
                    }
                    if (zMode == "surface" || zMode == "cap")
                    {
                        if (string.IsNullOrEmpty(surfName))
                            throw new InvalidOperationException(name + ": z_mode=" + zMode + " requires surface.");
                        ObjectId sfId = FindSurfaceId(tr, civ, surfName);
                        if (sfId.IsNull) throw new InvalidOperationException("Surface '" + surfName + "' not found.");
                        fl.AssignElevationsFromSurface(sfId, true);
                        if (zMode == "cap")
                        {
                            // Cap mode: min(terrain, z), the rule for crest lines meeting terraces
                            var allp = fl.GetPoints(CivFlPointType.AllPoints);
                            for (int i = 0; i < allp.Count; i++)
                                if (allp[i].Z > zConst)
                                    try { fl.SetPointElevation(i, zConst); } catch { }
                        }
                    }
                    if (eraseSource)
                    {
                        var s2 = tr.GetObject(src.ObjectId, OpenMode.ForWrite);
                        if (!s2.IsErased) s2.Erase();
                    }
                    // Attach classification on creation: props={set,values} (the set definition must exist; run property_sets define first)
                    var props = it["props"] as JsonObject;
                    if (props != null)
                    {
                        string psName = GetString(props, "set", "DredgeFeatures");
                        var dict = new DictionaryPropertySetDefinitions(db);
                        if (!dict.Has(psName, tr))
                            throw new InvalidOperationException("Property set '" + psName + "' does not exist; create it with property_sets define first.");
                        ObjectId psdId = dict.GetAt(psName);
                        PropertyDataServices.AddPropertySet(fl, psdId);
                        ObjectId psId = PropertyDataServices.GetPropertySet(fl, psdId);
                        var ps = (PropertySet)tr.GetObject(psId, OpenMode.ForWrite);
                        var vals = props["values"] as JsonObject;
                        if (vals != null)
                            foreach (var kv in vals)
                            {
                                int pid = ps.PropertyNameToId(kv.Key);
                                var jv = kv.Value as JsonValue;
                                double dv;
                                if (jv != null && jv.TryGetValue(out dv)) ps.SetAt(pid, dv);
                                else ps.SetAt(pid, kv.Value == null ? "" : kv.Value.ToString());
                            }
                    }
                    made.Add(new JsonObject
                    {
                        ["name"] = name,
                        ["handle"] = fl.Handle.ToString(),
                        ["points"] = fl.PointsCount,
                        ["z_min"] = Math.Round(fl.MinElevation, 3),
                        ["z_max"] = Math.Round(fl.MaxElevation, 3),
                        ["length_2d"] = Math.Round(fl.Length2D, 2)
                    });
                }
                tr.Commit();
            }
            return new JsonObject { ["created"] = made };
        }

        // ==================== dredge_from_feature_lines ====================

        static string FlPsText(Transaction tr, Database db, DBObject obj, string setName, string field)
        {
            try
            {
                var dict = new DictionaryPropertySetDefinitions(db);
                if (!dict.Has(setName, tr)) return null;
                ObjectId psId = PropertyDataServices.GetPropertySet(obj, dict.GetAt(setName));
                var ps = (PropertySet)tr.GetObject(psId, OpenMode.ForRead);
                object v = ps.GetAt(ps.PropertyNameToId(field));
                return v == null ? null : Convert.ToString(v).Trim();
            }
            catch { return null; }
        }

        static double FlPsReal(Transaction tr, Database db, DBObject obj, string setName, string field)
        {
            try
            {
                var dict = new DictionaryPropertySetDefinitions(db);
                if (!dict.Has(setName, tr)) return double.NaN;
                ObjectId psId = PropertyDataServices.GetPropertySet(obj, dict.GetAt(setName));
                var ps = (PropertySet)tr.GetObject(psId, OpenMode.ForRead);
                return Convert.ToDouble(ps.GetAt(ps.PropertyNameToId(field)));
            }
            catch { return double.NaN; }
        }

        /// <summary>Resamples the feature-line polyline (AllPoints, linear interpolation) at the given step, keeping z linear.</summary>
        static List<Point3d> DredgeResample(List<Point3d> src, double step)
        {
            var outp = new List<Point3d> { src[0] };
            for (int i = 1; i < src.Count; i++)
            {
                Point3d a = src[i - 1], b = src[i];
                double L = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                int nseg = Math.Max(1, (int)Math.Ceiling(L / Math.Max(step, 0.2)));
                for (int k = 1; k <= nseg; k++)
                {
                    double t = (double)k / nseg;
                    outp.Add(new Point3d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t,
                                         a.Z + (b.Z - a.Z) * t));
                }
            }
            return outp;
        }

        static JsonNode DredgeFromFeatureLines(JsonObject a, Document doc)
        {
            string setName = GetString(a, "set", "DredgeFeatures");
            string region = GetString(a, "region", null);
            var lineHandles = a["lines"] as JsonArray;
            string terrName = GetString(a, "surface", null);
            double sampleStep = GetDouble(a, "sample_step", 2.0);
            double slopeStep = GetDouble(a, "slope_step", 2.0);
            double flatStep = GetDouble(a, "flat_step", 20.0);
            double chainTol = GetDouble(a, "chain_tol", 0.1);
            bool drawToe = GetBool(a, "draw_toe", true);
            bool drawCrest = GetBool(a, "draw_crest", false);   // the crest line is the feature line itself; not drawn separately by default
            string surfLayer = GetString(a, "surface_layer", "C3DF-DREDGE-SURFACE");
            string crestLayer = GetString(a, "crest_layer", "C3DF-DREDGE-TOP");
            string toeLayer = GetString(a, "toe_layer", "C3DF-DREDGE-TOE");

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var res = new JsonObject();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // ---- Collect lines (handle whitelist, or scan by the Region classification) ----
                var fls = new List<CivFeatureLine>();
                if (lineHandles != null && lineHandles.Count > 0)
                {
                    foreach (JsonNode hn in lineHandles)
                    {
                        var fl = tr.GetObject(ResolveHandle(db, hn.GetValue<string>()), OpenMode.ForRead) as CivFeatureLine;
                        if (fl == null) throw new InvalidOperationException(hn + " is not a feature line.");
                        fls.Add(fl);
                    }
                }
                else if (!string.IsNullOrEmpty(region))
                {
                    foreach (ObjectId id in ModelSpace(db, tr))
                    {
                        var fl = tr.GetObject(id, OpenMode.ForRead) as CivFeatureLine;
                        if (fl == null) continue;
                        if (string.Equals(FlPsText(tr, db, fl, setName, "Region"), region,
                                          StringComparison.OrdinalIgnoreCase)) fls.Add(fl);
                    }
                }
                else throw new InvalidOperationException("Give at least one of lines or region.");
                if (fls.Count < 2)
                    throw new InvalidOperationException("Cannot form a ring: only " + fls.Count + " feature line(s) found.");

                // Terrain: when the parameter is absent, read the <TerrainSurface> field from the lines (only follow-terrain lines need it)
                if (string.IsNullOrEmpty(terrName))
                    foreach (var fl in fls)
                    {
                        string t = FlPsText(tr, db, fl, setName, "TerrainSurface");
                        if (!string.IsNullOrEmpty(t)) { terrName = t; break; }
                    }
                CivSurface terr = null;
                if (!string.IsNullOrEmpty(terrName))
                {
                    ObjectId sfId = FindSurfaceId(tr, civ, terrName);
                    if (sfId.IsNull) throw new InvalidOperationException("Surface '" + terrName + "' not found.");
                    terr = (CivSurface)tr.GetObject(sfId, OpenMode.ForRead);
                }

                // ---- Read classification and sample each line ----
                var segs = new List<(CivFeatureLine Fl, string Role, string Mode, double M, List<Point3d> Pts)>();
                var lineRows = new JsonArray();
                foreach (var fl in fls)
                {
                    string role = FlPsText(tr, db, fl, setName, "Role") ?? "";
                    string mode = FlPsText(tr, db, fl, setName, "ZMode") ?? "Fixed";
                    double m = FlPsReal(tr, db, fl, setName, "SlopeM");
                    if (double.IsNaN(m) || m < 0.5 || m > 50)
                        throw new InvalidOperationException("Feature line '" + fl.Name + "': <SlopeM> missing or out of range (0.5~50).");
                    // Follow-terrain / cap lines: refresh the stored elevation snapshot on rebuild so the displayed z, breakline z and engine-sampled z all agree
                    if ((mode.Contains("Follow") || mode.StartsWith("Cap")) && terr != null)
                    {
                        try
                        {
                            var flW = (CivFeatureLine)tr.GetObject(fl.ObjectId, OpenMode.ForWrite);
                            flW.AssignElevationsFromSurface(terr.ObjectId, true);
                            double capv;
                            if (mode.StartsWith("Cap") &&
                                double.TryParse(mode.Substring(3).Trim(), out capv))
                            {
                                var ap = flW.GetPoints(CivFlPointType.AllPoints);
                                for (int i = 0; i < ap.Count; i++)
                                    if (ap[i].Z > capv)
                                        try { flW.SetPointElevation(i, capv); } catch { }
                            }
                        }
                        catch { }
                    }
                    var raw = new List<Point3d>();
                    foreach (Point3d p in fl.GetPoints(CivFlPointType.AllPoints)) raw.Add(p);
                    if (raw.Count < 2)
                        throw new InvalidOperationException("Feature line '" + fl.Name + "' has too few points.");
                    segs.Add((fl, role, mode, m, DredgeResample(raw, sampleStep)));
                    lineRows.Add(new JsonObject
                    {
                        ["name"] = fl.Name, ["role"] = role, ["z_mode"] = mode, ["slope_m"] = m,
                        ["points"] = raw.Count, ["z_min"] = Math.Round(fl.MinElevation, 3),
                        ["z_max"] = Math.Round(fl.MaxElevation, 3)
                    });
                }

                // ---- Chain into a ring (nearest endpoints, reversing where needed). Distances are always planar:
                //      an elevation jump at a joint is by design (crest line at 5.0 meeting a follow-terrain line), not a gap ----
                double D2(Point3d p, Point3d q)
                {
                    double dx = p.X - q.X, dy = p.Y - q.Y;
                    return Math.Sqrt(dx * dx + dy * dy);
                }
                var order = new List<(int Idx, bool Rev)> { (0, false) };
                var used = new HashSet<int> { 0 };
                var gaps = new List<double>();
                while (order.Count < segs.Count)
                {
                    var last = order[order.Count - 1];
                    var lp = last.Rev ? segs[last.Idx].Pts[0] : segs[last.Idx].Pts[segs[last.Idx].Pts.Count - 1];
                    int best = -1; bool bestRev = false; double bestD = double.MaxValue;
                    for (int i = 0; i < segs.Count; i++)
                    {
                        if (used.Contains(i)) continue;
                        double dh = D2(lp, segs[i].Pts[0]);
                        double dt = D2(lp, segs[i].Pts[segs[i].Pts.Count - 1]);
                        if (dh < bestD) { bestD = dh; best = i; bestRev = false; }
                        if (dt < bestD) { bestD = dt; best = i; bestRev = true; }
                    }
                    if (bestD > chainTol)
                        throw new InvalidOperationException(
                            "Ring chain broken: after '" + segs[order[order.Count - 1].Idx].Fl.Name + "' the nearest endpoint is " +
                            bestD.ToString("0.###") + " m away (tolerance " + chainTol + ").");
                    gaps.Add(bestD);
                    order.Add((best, bestRev));
                    used.Add(best);
                }
                {
                    var first = segs[order[0].Idx].Pts[0];
                    var lastSeg = order[order.Count - 1];
                    var lastP = lastSeg.Rev ? segs[lastSeg.Idx].Pts[0]
                                            : segs[lastSeg.Idx].Pts[segs[lastSeg.Idx].Pts.Count - 1];
                    double dClose = D2(lastP, first);
                    if (dClose > chainTol)
                        throw new InvalidOperationException("Ring not closed: last point is " + dClose.ToString("0.###") + " m from the first.");
                    gaps.Add(dClose);
                }

                // ---- Bottom elevation: parameter > <BottomElev> field on the lines (error if they disagree) > lowest point of the Mouth line ----
                double bottom;
                if (a["bottom_elev"] != null) bottom = GetDouble(a, "bottom_elev", 0.0);
                else
                {
                    var fieldVals = new List<double>();
                    foreach (var fl in fls)
                    {
                        double b = FlPsReal(tr, db, fl, setName, "BottomElev");
                        if (!double.IsNaN(b) && Math.Abs(b) > 1e-9 &&
                            !fieldVals.Exists(v => Math.Abs(v - b) < 1e-6)) fieldVals.Add(b);
                    }
                    if (fieldVals.Count > 1)
                        throw new InvalidOperationException("<BottomElev> differs between lines: " +
                            string.Join("/", fieldVals) + ". Make them consistent first.");
                    if (fieldVals.Count == 1) bottom = fieldVals[0];
                    else
                    {
                        double mn = double.MaxValue;
                        foreach (var s in segs)
                            if (s.Role.Contains("Mouth"))
                                foreach (Point3d p in s.Pts) if (p.Z < mn) mn = p.Z;
                        if (mn == double.MaxValue)
                            throw new InvalidOperationException(
                                "No bottom elevation from any of the three sources: bottom_elev not given, no <BottomElev> on the lines, and no line whose <Role> contains 'Mouth'.");
                        bottom = mn;
                    }
                }

                // ---- Assemble the ring (Z: sampled live from terrain; M: per line) ----
                var ring = new DredgeRing();
                int followed = 0;
                foreach (var (idx, rev) in order)
                {
                    var s = segs[idx];
                    var ptsSeg = new List<Point3d>(s.Pts);
                    if (rev) ptsSeg.Reverse();
                    bool follow = s.Mode.Contains("Follow");
                    // "CapX": terrace-meeting rule, top = min(terrain, X), sampled and clamped live on rebuild
                    double cap = double.NaN;
                    if (s.Mode.StartsWith("Cap"))
                    {
                        if (!double.TryParse(s.Mode.Substring(3).Trim(), out cap))
                            throw new InvalidOperationException(
                                "Feature line '" + s.Fl.Name + "': elevation mode '" + s.Mode + "' cannot be parsed (expected form: Cap5.0).");
                        follow = true;
                    }
                    if (follow && terr == null)
                        throw new InvalidOperationException(
                            "Feature line '" + s.Fl.Name + "' is in " + s.Mode + " mode but there is no terrain surface to sample.");
                    for (int i = 0; i < ptsSeg.Count - 1; i++)   // drop each segment's last point (= next segment's first)
                    {
                        Point3d p = ptsSeg[i];
                        double z = p.Z;
                        if (follow && terr != null)
                        {
                            double zs;
                            if (GridTrySample(terr, new Point3d(p.X, p.Y, 0), out zs)) { z = zs; followed++; }
                        }
                        if (!double.IsNaN(cap) && z > cap) z = cap;
                        if (z < bottom) z = bottom;
                        ring.P.Add(new Point2d(p.X, p.Y));
                        ring.Z.Add(z);
                        ring.M.Add(s.M);
                    }
                }
                if (ring.Count < 16)
                    throw new InvalidOperationException("Too few ring sample points (" + ring.Count + ").");
                ring.Recalc();
                double perim = 0;
                for (int i = 0; i < ring.Count; i++)
                {
                    var q = ring.P[(i + 1) % ring.Count];
                    perim += Math.Sqrt((q.X - ring.P[i].X) * (q.X - ring.P[i].X) +
                                       (q.Y - ring.P[i].Y) * (q.Y - ring.P[i].Y));
                }
                ring.TotalLen = perim;

                // Clear this region's old crest/toe lines (C3DF_DREDGE tag + first vertex inside the ring) so rebuilds do not pile up
                int erasedOld = 0;
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var p3 = tr.GetObject(id, OpenMode.ForRead) as Polyline3d;
                    if (p3 == null || p3.IsErased || !DredgeIsTagged(p3)) continue;
                    Point3d fp;
                    try { fp = p3.GetPointAtParameter(p3.StartParam); } catch { continue; }
                    if (!GridPointInPolygon(new Point2d(fp.X, fp.Y), ring.P)) continue;
                    p3.UpgradeOpen();
                    p3.Erase();
                    erasedOld++;
                }

                string sName = GetString(a, "name", "DredgeDesign-" + (region ?? segs[0].Fl.Name));
                ObjectId lyS = GridEnsureLayer(tr, db, surfLayer, 7);
                ObjectId lyC = GridEnsureLayer(tr, db, crestLayer, 1);
                ObjectId lyT = GridEnsureLayer(tr, db, toeLayer, 3);
                var btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                EnsureRegApp(tr, db, DredgeAppName);

                DredgeSurfaceOut so = DredgeBuildSurface(tr, db, civ, btr, ring, bottom, sName,
                    lyS, lyC, lyT, drawCrest, drawToe, slopeStep, flatStep);

                // Attach the feature lines to the surface definition (Definition -> Breaklines): self-describing definition + edges forced to fit.
                // Note: interior slope points are Edits; do not run native REBUILD by hand in the GUI (it pairs new lines with old points). Always rebuild through the linkage/op.
                int breaklines = 0;
                try
                {
                    ObjectId sid2 = FindSurfaceId(tr, civ, so.Surface);
                    var ts2 = (CivTinSurface)tr.GetObject(sid2, OpenMode.ForWrite);
                    var bIds = new ObjectIdCollection();
                    foreach (var fl in fls) bIds.Add(fl.ObjectId);
                    ts2.BreaklinesDefinition.AddStandardBreaklines(bIds, 1.0, 0.0, 0.0, 0.0);
                    breaklines = bIds.Count;
                }
                catch (System.Exception ex)
                { so.Notes.Add("Failed to attach feature lines as breaklines (surface itself unaffected): " + ex.Message); }

                double zMin = double.MaxValue, zMax = double.MinValue;
                foreach (double z in ring.Z) { if (z < zMin) zMin = z; if (z > zMax) zMax = z; }
                double gMax = 0; foreach (double g in gaps) if (g > gMax) gMax = g;

                res["region"] = region;
                res["surface"] = so.Surface;
                res["bottom_elev"] = bottom;
                res["lines"] = lineRows;
                res["chain_gap_max"] = Math.Round(gMax, 4);
                res["ring_points"] = ring.Count;
                res["z_top_min"] = Math.Round(zMin, 3);
                res["z_top_max"] = Math.Round(zMax, 3);
                res["band_max"] = Math.Round(so.BandMax, 2);
                res["terrain"] = terrName;
                res["breaklines"] = breaklines;
                res["erased_old_lines"] = erasedOld;
                res["terrain_followed_points"] = followed;
                res["toe_points"] = so.ToePoints;
                res["grid_points"] = so.GridPoints;
                res["vertices"] = so.Vertices;
                var notes = new JsonArray();
                foreach (string nte in so.Notes) notes.Add((JsonNode)nte);
                res["warnings"] = notes;
                tr.Commit();
            }
            return res;
        }
    }
}
