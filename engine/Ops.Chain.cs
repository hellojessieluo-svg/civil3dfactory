using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivAlignmentType = Autodesk.Civil.DatabaseServices.AlignmentType;
using CivAssembly = Autodesk.Civil.DatabaseServices.Assembly;
using CivCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using CivCorridorSurface = Autodesk.Civil.DatabaseServices.CorridorSurface;
using CivMaterialItemType = Autodesk.Civil.DatabaseServices.MaterialItemType;
using CivProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivProfileType = Autodesk.Civil.DatabaseServices.ProfileType;
using CivQtoCriteria = Autodesk.Civil.DatabaseServices.Styles.QuantityTakeoffCriteria;
using CivQtoMapping = Autodesk.Civil.DatabaseServices.QTOCriteriaNameMapping;
using CivQtoMaterialList = Autodesk.Civil.DatabaseServices.QTOMaterialList;
using CivQtoSectionalResult = Autodesk.Civil.DatabaseServices.QTOSectionalResult;
using CivSampleLine = Autodesk.Civil.DatabaseServices.SampleLine;
using CivSampleLineGroup = Autodesk.Civil.DatabaseServices.SampleLineGroup;
using CivSectionSource = Autodesk.Civil.DatabaseServices.SectionSource;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivTargetInfo = Autodesk.Civil.DatabaseServices.SubassemblyTargetInfo;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;
using CivVolumeMethod = Autodesk.Civil.DatabaseServices.MaterialVolumeCalculationMethodType;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivConnectedParams = Autodesk.Civil.DatabaseServices.ConnectedAlignmentParams;
using CivCurveGroupType = Autodesk.Civil.CurbReturnCurveGroupType;

namespace Civil3DFactory
{
    /// <summary>
    /// The whole chain: "a straight line -> alignment -> offsets -> profiles -> corridor -> corridor surface -> sample lines -> quantities".
    ///
    /// Every op edits the **in-memory** drawing database passed via /i; the original file on disk is untouched.
    /// To keep results you must explicitly run save_dwg (defaults to saving as a new file; pass apply:true to write back to the original).
    ///
    /// The computation logic was ported from the field-tested RiverQto (Civil3D-009), keeping the pitfalls it hit:
    ///   - clear all old sample line groups on the alignment before creating a new one, otherwise material computation reports "should have been sampled";
    ///   - the sampled-source flag must be set in the same transaction that creates the group; setting it after commit has no effect;
    ///   - criteria surface slot names must be read from the criteria itself, never hard-coded (hard-coding fails silently -> all quantities 0).
    /// </summary>
    public static partial class Ops
    {
        // ===================== 1. Existing ground surface (build a terrain so the chain is self-contained) =====================

        static JsonNode CreateSurfaceGrid(JsonObject a, Document doc)
        {
            string name = Need(a, "name");
            double minx = GetDouble(a, "minx", 0), miny = GetDouble(a, "miny", 0);
            double maxx = GetDouble(a, "maxx", 0), maxy = GetDouble(a, "maxy", 0);
            if (maxx <= minx || maxy <= miny)
                throw new InvalidOperationException("minx/miny/maxx/maxy are required, and max must be greater than min.");
            double step = GetDouble(a, "step", 20);
            if (step <= 0) throw new InvalidOperationException("step must be greater than 0.");
            double elev = GetDouble(a, "elev", 0);
            double slopeX = GetDouble(a, "slope_x", 0);   // elevation change per metre
            double slopeY = GetDouble(a, "slope_y", 0);
            double undAmp = GetDouble(a, "undulation_amp", 0);
            double undLen = GetDouble(a, "undulation_len", 200);
            if (undLen <= 0) undLen = 200;
            string style = GetString(a, "style", null);

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            // Overwrite/rebuild: delete the surface with the same name first (committed in its own transaction, so the name is released before re-creating it)
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId old = FindSurfaceId(tr, civ, name);
                if (!old.IsNull)
                {
                    var s = (CivSurface)tr.GetObject(old, OpenMode.ForWrite);
                    s.Erase();
                }
                tr.Commit();
            }

            int nx = 0, ny = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId styleId = FindStyleId(tr, civ.Styles.SurfaceStyles, style);
                ObjectId sid = CivTinSurface.Create(db, name);
                var ts = (CivTinSurface)tr.GetObject(sid, OpenMode.ForWrite);
                if (!styleId.IsNull) ts.StyleId = styleId;

                var pts = new Point3dCollection();
                for (double x = minx; x <= maxx + 1e-9; x += step)
                {
                    nx++;
                    ny = 0;
                    for (double y = miny; y <= maxy + 1e-9; y += step)
                    {
                        ny++;
                        double z = elev + slopeX * (x - minx) + slopeY * (y - miny);
                        if (undAmp != 0)
                            z += undAmp * Math.Sin(2 * Math.PI * (x - minx) / undLen) * Math.Cos(2 * Math.PI * (y - miny) / undLen);
                        pts.Add(new Point3d(x, y, z));
                    }
                }
                ts.AddVertices(pts);
                tr.Commit();

                return new JsonObject
                {
                    ["surface"] = name,
                    ["vertices"] = nx * ny,
                    ["grid"] = nx + " x " + ny,
                    ["extent"] = string.Format("({0},{1}) - ({2},{3})", minx, miny, maxx, maxy),
                    ["elev_range"] = Math.Round(elev, 3) + " ~ " +
                                     Math.Round(elev + slopeX * (maxx - minx) + slopeY * (maxy - miny), 3)
                };
            }
        }

        // ===================== 2. Draw a line -> define as alignment =====================

        static JsonNode CreateAlignment(JsonObject a, Document doc)
        {
            string name = Need(a, "name");
            string handle = GetString(a, "handle", null);
            var ptsArr = a["points"] as JsonArray;
            if (string.IsNullOrEmpty(handle) && (ptsArr == null || ptsArr.Count < 2))
                throw new InvalidOperationException("Give either points:[[x,y],[x,y],...] (draw a new line) or handle (an existing line in the drawing).");

            string layer = GetString(a, "layer", "0");
            string style = GetString(a, "style", null);
            string labelSet = GetString(a, "label_set", null);
            string site = GetString(a, "site", null);
            bool eraseSource = GetBool(a, "erase_source", true);      // erase the helper line once it becomes an alignment
            bool addCurves = GetBool(a, "add_curves", false);

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            // Overwrite/rebuild: delete the alignment with the same name first
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EraseAlignments(tr, civ, name);
                tr.Commit();
            }

            string drawnFrom;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId plId;
                if (!string.IsNullOrEmpty(handle))
                {
                    plId = ResolveHandle(db, handle);
                    drawnFrom = "existing object " + handle;
                }
                else
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    var pl = new Polyline();
                    int i = 0;
                    foreach (JsonNode pn in ptsArr)
                    {
                        var pair = pn as JsonArray;
                        if (pair == null || pair.Count < 2)
                            throw new InvalidOperationException("Every item in points must be [x, y].");
                        pl.AddVertexAt(i++, new Point2d(
                            pair[0].GetValue<double>(), pair[1].GetValue<double>()), 0, 0, 0);
                    }
                    ms.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                    if (!string.IsNullOrEmpty(layer) && layer != "0")
                    {
                        EnsureLayers(db, tr, new JsonArray { new JsonObject { ["name"] = layer } });   // a missing layer is created, not eKeyNotFound
                        pl.Layer = layer;   // layer can only be set after the entity is in the database
                    }
                    plId = pl.ObjectId;
                    drawnFrom = "new polyline, " + i + " points, length " + Math.Round(pl.Length, 3) + " m";
                }

                ObjectId siteId = ObjectId.Null;
                if (!string.IsNullOrEmpty(site))
                {
                    foreach (ObjectId s in civ.GetSiteIds())
                        if (TryGetName(tr.GetObject(s, OpenMode.ForRead)) == site) { siteId = s; break; }
                    if (siteId.IsNull) throw new InvalidOperationException("Site '" + site + "' not found.");
                }

                ObjectId styleId = FindStyleId(tr, civ.Styles.AlignmentStyles, style);
                ObjectId labelId = FindStyleId(tr, civ.Styles.LabelSetStyles.AlignmentLabelSetStyles, labelSet);

                ObjectId layerId = db.Clayer;
                if (!string.IsNullOrEmpty(layer) && layer != "0")
                {
                    EnsureLayers(db, tr, new JsonArray { new JsonObject { ["name"] = layer } });
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (lt.Has(layer)) layerId = lt[layer];
                }
                ObjectId alId = CreateAlignmentFromEntity(
                    tr, civ, name, siteId, plId, layerId, styleId, labelId, eraseSource, addCurves);

                var al = (CivAlignment)tr.GetObject(alId, OpenMode.ForRead);
                var res = new JsonObject
                {
                    ["alignment"] = al.Name,
                    ["from"] = drawnFrom,
                    ["length"] = Math.Round(al.Length, 3),
                    ["start_station"] = Math.Round(al.StartingStation, 3),
                    ["end_station"] = Math.Round(al.EndingStation, 3),
                    ["handle"] = al.Handle.ToString(),
                    ["source_erased"] = eraseSource
                };
                tr.Commit();
                return res;
            }
        }

        // ===================== 3. Offset alignments =====================

        static JsonNode OffsetAlignment(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            double dist = GetDouble(a, "distance", 15);
            string style = GetString(a, "style", null);

            // If offsets is given explicitly, use it; otherwise create one on each side at +/-distance (names carry the _L/_R prefix;
            // the corridor step finds its offset targets by that prefix, do not change it).
            // Each item may be a number (offset the whole alignment) or an object {distance, start_station, end_station, name?}
            // -- a parent-alignment station range makes it a "partial offset", leaving the intersection range to a connected alignment (create_connected_alignment).
            var pairs = new List<(string Name, double Dist, double? S0, double? S1)>();
            var arr = a["offsets"] as JsonArray;
            if (arr != null && arr.Count > 0)
            {
                foreach (JsonNode n in arr)
                {
                    if (n is JsonObject o)
                    {
                        JsonNode dn = o["distance"];
                        if (dn == null) throw new InvalidOperationException("A partial-offset object must give distance.");
                        double d = dn.GetValue<double>();
                        double? s0 = o["start_station"] != null ? o["start_station"].GetValue<double>() : (double?)null;
                        double? s1 = o["end_station"] != null ? o["end_station"].GetValue<double>() : (double?)null;
                        if (s0.HasValue != s1.HasValue)
                            throw new InvalidOperationException("start_station and end_station must be given together or not at all.");
                        string side = d < 0 ? "L" : "R";
                        string nm = GetString(o, "name", null);
                        if (string.IsNullOrEmpty(nm))
                            nm = alName + "_" + side + Math.Abs(d) + "m"
                               + (s0.HasValue ? "_" + Math.Round(s0.Value) + "-" + Math.Round(s1.Value) : "");
                        pairs.Add((nm, d, s0, s1));
                    }
                    else
                    {
                        double d = n.GetValue<double>();
                        string side = d < 0 ? "L" : "R";
                        pairs.Add((alName + "_" + side + Math.Abs(d) + "m", d, null, null));
                    }
                }
            }
            else
            {
                if (dist <= 0) throw new InvalidOperationException("distance must be greater than 0.");
                pairs.Add((alName + "_L" + dist + "m", -dist, null, null));
                pairs.Add((alName + "_R" + dist + "m", dist, null, null));
            }

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (FindAlignment(tr, civ, alName) == null)
                    throw new InvalidOperationException("Alignment '" + alName + "' not found.");
                var names = new List<string>();
                foreach (var kv in pairs) names.Add(kv.Name);
                EraseAlignments(tr, civ, names.ToArray());
                tr.Commit();
            }

            var made = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment parent = FindAlignment(tr, civ, alName);
                ObjectId styleId = FindStyleId(tr, civ.Styles.AlignmentStyles, style);
                foreach (var kv in pairs)
                {
                    if (kv.S0.HasValue)
                        CivAlignment.CreateOffsetAlignment(kv.Name, parent.ObjectId, kv.Dist, styleId,
                                                           kv.S0.Value, kv.S1.Value);
                    else
                        CivAlignment.CreateOffsetAlignment(kv.Name, parent.ObjectId, kv.Dist, styleId);
                    var item = new JsonObject { ["name"] = kv.Name, ["offset"] = kv.Dist };
                    if (kv.S0.HasValue) { item["start_station"] = kv.S0.Value; item["end_station"] = kv.S1.Value; }
                    made.Add(item);
                }
                tr.Commit();
            }
            return new JsonObject { ["parent"] = alName, ["created"] = made };
        }

        // ===================== 3b. Connected alignment: radius turn between two alignments (intersection) =====================
        //
        // Native CreateConnectedAlignment: one connection station on each of the incoming/outgoing alignments + a radius produce a dynamic connected alignment;
        // the turn follows automatically when the parents (including dynamic offset alignments) change. CurveGroupType is fixed to Arc (single arc).
        // The gap that partial offsets (offset_alignment start/end_station) leave at the intersection is exactly what it connects.

        static JsonNode CreateConnectedAlignmentOp(JsonObject a, Document doc)
        {
            string name = Need(a, "name");
            string inName = Need(a, "in_alignment");
            string outName = Need(a, "out_alignment");
            JsonNode inNode = a["in_station"], outNode = a["out_station"];
            if (inNode == null || outNode == null)
                throw new InvalidOperationException("in_station / out_station are required (connection stations on the two alignments).");
            double inSta = inNode.GetValue<double>();
            double outSta = outNode.GetValue<double>();
            double radius = GetDouble(a, "radius", 20);
            if (radius <= 0) throw new InvalidOperationException("radius must be greater than 0.");
            string style = GetString(a, "style", null);
            string labelSet = GetString(a, "label_set", null);
            bool big = GetBool(a, "greater_than_180", false);

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EraseAlignments(tr, civ, name);
                tr.Commit();
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment ain = FindAlignment(tr, civ, inName);
                if (ain == null) throw new InvalidOperationException("Incoming alignment '" + inName + "' not found.");
                CivAlignment aout = FindAlignment(tr, civ, outName);
                if (aout == null) throw new InvalidOperationException("Outgoing alignment '" + outName + "' not found.");
                ObjectId styleId = FindStyleId(tr, civ.Styles.AlignmentStyles, style);
                ObjectId labelId = FindStyleId(tr, civ.Styles.LabelSetStyles.AlignmentLabelSetStyles, labelSet);

                var p = new CivConnectedParams
                {
                    IncomingParentAlignmentId = ain.ObjectId,
                    IncomingParentAlignmentStation = inSta,
                    OutgoingParentAlignmentId = aout.ObjectId,
                    OutgoingParentAlignmentStation = outSta,
                    CurveRadius = radius,
                    CurveGroupType = CivCurveGroupType.Arc,
                    GreaterThan180 = big,
                    OffsetIn = GetDouble(a, "offset_in", 0),
                    OffsetOut = GetDouble(a, "offset_out", 0),
                    // The API forces >0: the connected alignment "rides" a short tangent along each parent before the arc starts. Default to the minimum,
                    // so the connected alignment is essentially the turning arc itself.
                    ConnectionOverlapLengthIn = GetDouble(a, "overlap_in", 0.01),
                    ConnectionOverlapLengthOut = GetDouble(a, "overlap_out", 0.01)
                };

                // Solver pitfalls (nailed down on project B, 2026-08-23):
                //  (1) the in/out order is picky; only some flow directions are solvable at each corner;
                //  (2) the sign of OffsetIn/Out flips with the travel direction the solver picks, so parameters alone cannot pin the quadrant --
                //     in one batch +15 sometimes lands on the right, sometimes on the left, and the arc may even jump to another corner.
                // The only reliable approach: try all 8 combinations (order x side signs), then verify the geometry with StationOffset after creation
                // (both end points must truly lie on the two parents' **intended** side at +/-15 and near the hinted stations); delete and try the next on failure.
                double offIn = p.OffsetIn, offOut = p.OffsetOut;
                double tol = GetDouble(a, "verify_tol", 0.5);
                double staTol = GetDouble(a, "verify_station_window", 80);
                // Land-side test (pitfall 6): around the intersection of one pair of offset lines there are 4 feasible tangent arcs; side + station cannot pin the convexity,
                // so the arc may bulge into the channel. A tangent point must also be >= W away from the **other** parent (on the platform side) to count.
                double landMin = GetDouble(a, "land_min",
                    Math.Min(Math.Abs(offIn), Math.Abs(offOut)) - 0.5);

                bool VerifyEnd(Point3d pt, CivAlignment parent, double wantOff, double hintSta)
                {
                    double sta = 0, off = 0;
                    try { parent.StationOffset(pt.X, pt.Y, ref sta, ref off); }
                    catch { return false; }
                    return Math.Abs(off - wantOff) <= tol && Math.Abs(sta - hintSta) <= staTol;
                }

                bool LandSide(Point3d pt, CivAlignment other)
                {
                    double sta = 0, off = 0;
                    try { other.StationOffset(pt.X, pt.Y, ref sta, ref off); }
                    catch { return true; }   // beyond the other alignment's station range = far away, naturally on the land side
                    return Math.Abs(off) >= landMin;
                }

                // Bulge-direction test (pitfall 7): between one pair of tangent points there are two arcs (bulging either way) with identical end points,
                // so the end-point test cannot tell them apart -- the arc midpoint must also be on the land side (>= landMin from the offset parent).
                bool MidLand(CivAlignment cand)
                {
                    if (landMin <= 0) return true;
                    double ms = (cand.StartingStation + cand.EndingStation) / 2;
                    double e = 0, n = 0;
                    cand.PointLocation(ms, 0, ref e, ref n);
                    var mid = new Point3d(e, n, 0);
                    if (Math.Abs(offIn) >= 1 && !LandSide(mid, ain)) return false;
                    if (Math.Abs(offOut) >= 1 && !LandSide(mid, aout)) return false;
                    return true;
                }

                ObjectId id = ObjectId.Null;
                string combo = null;
                var attempts = new JsonArray();
                foreach (bool swap in new[] { false, true })
                foreach (double sIn in new[] { offIn, -offIn })
                foreach (double sOut in new[] { offOut, -offOut })
                {
                    CivAlignment pin = swap ? aout : ain, pout = swap ? ain : aout;
                    p.IncomingParentAlignmentId = pin.ObjectId;
                    p.OutgoingParentAlignmentId = pout.ObjectId;
                    p.IncomingParentAlignmentStation = swap ? outSta : inSta;
                    p.OutgoingParentAlignmentStation = swap ? inSta : outSta;
                    p.OffsetIn = swap ? sOut : sIn;
                    p.OffsetOut = swap ? sIn : sOut;
                    string tag = (swap ? "swapped" : "original") + $" in{p.OffsetIn:+0;-0} out{p.OffsetOut:+0;-0}";
                    ObjectId tryId;
                    try
                    {
                        tryId = CivAlignment.CreateConnectedAlignment(
                            name, ObjectId.Null, db.Clayer, styleId, labelId, p);
                    }
                    catch (System.Exception ex)
                    {
                        attempts.Add(tag + " create failed:" + ex.Message);
                        continue;
                    }
                    var cand = (CivAlignment)tr.GetObject(tryId, OpenMode.ForRead);
                    double e0 = 0, n0 = 0, e1 = 0, n1 = 0;
                    cand.PointLocation(cand.StartingStation, 0, ref e0, ref n0);
                    cand.PointLocation(cand.EndingStation, 0, ref e1, ref n1);
                    var p0 = new Point3d(e0, n0, 0);
                    var p1 = new Point3d(e1, n1, 0);
                    // Intent: one end tangent to ain on the offIn side, the other tangent to aout on the offOut side (either end assignment is accepted),
                    // and both tangent points on the land side (>= landMin from the other parent; the arc must not bulge into the channel).
                    bool okGeom =
                        ((VerifyEnd(p0, ain, offIn, inSta) && VerifyEnd(p1, aout, offOut, outSta)
                          && LandSide(p0, aout) && LandSide(p1, ain)) ||
                         (VerifyEnd(p1, ain, offIn, inSta) && VerifyEnd(p0, aout, offOut, outSta)
                          && LandSide(p1, aout) && LandSide(p0, ain)))
                        && MidLand(cand);
                    if (okGeom && cand.Length > 0)
                    {
                        id = tryId;
                        combo = tag;
                        break;
                    }
                    attempts.Add(tag + $" created but geometry check failed(len={Math.Round(cand.Length, 1)})");
                    var kill = (CivAlignment)tr.GetObject(tryId, OpenMode.ForWrite);
                    kill.Erase();
                }
                if (id.IsNull)
                    throw new InvalidOperationException(
                        "Connected alignment '" + name + "': all 8 combinations failed or did not pass the geometry check: " + attempts.ToJsonString());

                var al = (CivAlignment)tr.GetObject(id, OpenMode.ForRead);

                // Report the measured tangent points: StationOffset of each end point against each parent (Civil signed: negative = left).
                // Downstream uses this as ground truth to re-split the offsets -- more reliable than guessing polyline direction in DXF.
                JsonObject Measure(double sta0)
                {
                    double e = 0, n = 0;
                    al.PointLocation(sta0, 0, ref e, ref n);
                    var m = new JsonObject();
                    foreach (var (tag, parent) in new[] { ("on_in", ain), ("on_out", aout) })
                    {
                        double s = 0, off = 0;
                        try
                        {
                            parent.StationOffset(e, n, ref s, ref off);
                            m[tag] = new JsonObject { ["station"] = Math.Round(s, 3), ["offset"] = Math.Round(off, 3) };
                        }
                        catch { m[tag] = null; }
                    }
                    return m;
                }

                var res = new JsonObject
                {
                    ["alignment"] = al.Name,
                    ["handle"] = al.Handle.ToString(),
                    ["length"] = Math.Round(al.Length, 3),
                    ["radius"] = radius,
                    ["in"] = inName,
                    ["in_station"] = inSta,
                    ["out"] = outName,
                    ["out_station"] = outSta,
                    ["combo"] = combo,
                    ["attempts_before_success"] = attempts.Count,
                    ["geometry_verified"] = true,
                    ["start_end_measurements"] = new JsonObject
                    {
                        ["start"] = Measure(al.StartingStation),
                        ["end"] = Measure(al.EndingStation)
                    }
                };
                tr.Commit();
                return res;
            }
        }

        // ===================== 4. Profiles: existing ground + flat design line =====================

        static JsonNode CreateProfiles(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string sfName = Need(a, "surface");
            double designElev = GetDouble(a, "design_elev", 0);
            string groundStyle = GetString(a, "ground_style", null);
            string designStyle = GetString(a, "design_style", null);
            string labelSet = GetString(a, "label_set", null);

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            string groundName = alName + "_EG";
            string designName = alName + "_FG";

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EraseProfiles(tr, civ, groundName, designName);
                tr.Commit();
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = FindAlignment(tr, civ, alName);
                if (al == null) throw new InvalidOperationException("Alignment '" + alName + "' not found.");
                ObjectId sfId = FindSurfaceId(tr, civ, sfName);
                if (sfId.IsNull) throw new InvalidOperationException("Surface '" + sfName + "' not found.");

                ObjectId gStyle = FindStyleId(tr, civ.Styles.ProfileStyles, groundStyle);
                ObjectId dStyle = FindStyleId(tr, civ.Styles.ProfileStyles, designStyle);
                ObjectId labelId = FindStyleId(tr, civ.Styles.LabelSetStyles.ProfileLabelSetStyles, labelSet);

                CivProfile.CreateFromSurface(groundName, al.ObjectId, sfId, db.Clayer, gStyle, labelId);

                ObjectId dId = CivProfile.CreateByLayout(designName, al.ObjectId, db.Clayer, dStyle, labelId);
                var design = (CivProfile)tr.GetObject(dId, OpenMode.ForWrite);
                design.PVIs.AddPVI(al.StartingStation, designElev);
                design.PVIs.AddPVI(al.EndingStation, designElev);

                var res = new JsonObject
                {
                    ["ground_profile"] = groundName,
                    ["design_profile"] = designName,
                    ["design_elev"] = designElev,
                    ["from_surface"] = sfName
                };
                tr.Commit();
                return res;
            }
        }

        // ===================== 5. Corridor (create + set targets) =====================

        static JsonNode CreateCorridor(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string asmName = Need(a, "assembly");
            string sfName = Need(a, "surface");
            string baseline = GetString(a, "baseline", "Baseline");
            string region = GetString(a, "region", "Region1");
            string corridorName = GetString(a, "name", alName + "_Corridor");

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EraseCorridors(tr, db, corridorName);
                tr.Commit();
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = FindAlignment(tr, civ, alName);
                if (al == null) throw new InvalidOperationException("Alignment '" + alName + "' not found.");

                ObjectId fgId = ObjectId.Null;
                foreach (ObjectId pid in al.GetProfileIds())
                {
                    var p = (CivProfile)tr.GetObject(pid, OpenMode.ForRead);
                    if (p.ProfileType == CivProfileType.FG) { fgId = pid; break; }
                }
                if (fgId.IsNull)
                    throw new InvalidOperationException("Alignment '" + alName + "' has no design profile; run create_profiles first.");

                ObjectId sfId = FindSurfaceId(tr, civ, sfName);
                if (sfId.IsNull) throw new InvalidOperationException("Surface '" + sfName + "' not found.");

                ObjectId asmId = ObjectId.Null;
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var asm = tr.GetObject(id, OpenMode.ForRead) as CivAssembly;
                    if (asm != null && asm.Name == asmName) { asmId = id; break; }
                }
                if (asmId.IsNull) throw new InvalidOperationException("Assembly '" + asmName + "' not found in the drawing (use civil_env to list names).");

                // Left/right offset alignments: found by name prefix, independent of the offset distance
                ObjectId leftId = ObjectId.Null, rightId = ObjectId.Null;
                foreach (ObjectId aid in civ.GetAlignmentIds())
                {
                    var x = (CivAlignment)tr.GetObject(aid, OpenMode.ForRead);
                    if (x.Name.StartsWith(alName + "_L")) leftId = aid;
                    else if (x.Name.StartsWith(alName + "_R")) rightId = aid;
                }

                ObjectId corridorId = civ.CorridorCollection.Add(
                    corridorName, baseline, al.ObjectId, fgId, region, asmId);
                var corridor = (CivCorridor)tr.GetObject(corridorId, OpenMode.ForWrite);
                corridor.Rebuild();

                // Set targets: surface slots -> existing ground; offset slots -> left/right offset alignments
                var targets = corridor.GetTargets();
                var sfIds = new ObjectIdCollection { sfId };
                var offIds = new ObjectIdCollection();
                if (!leftId.IsNull) offIds.Add(leftId);
                if (!rightId.IsNull) offIds.Add(rightId);

                int sCount = 0, oCount = 0;
                var slots = new JsonArray();
                foreach (CivTargetInfo t in targets)
                {
                    string tt = t.TargetType.ToString();
                    slots.Add(t.DisplayName + " [" + tt + "]");
                    if (tt == "Surface") { t.TargetIds = sfIds; sCount++; }
                    else if (tt == "Offset" && offIds.Count > 0) {
                        t.TargetIds = offIds; oCount++;
                        // same-side pick (left piece -> left line): property exists from 2025.x on, set by reflection so 2022-2024 still compile
                        try { var pSame = t.GetType().GetProperty("UseSameSideTarget"); if (pSame != null && pSame.CanWrite) pSame.SetValue(t, true, null); } catch { }
                    }
                }
                corridor.SetTargets(targets);
                corridor.Rebuild();

                var codes = new JsonArray();
                foreach (string c in corridor.GetLinkCodes()) codes.Add(c);

                var res = new JsonObject
                {
                    ["corridor"] = corridorName,
                    ["assembly"] = asmName,
                    ["surface_targets_set"] = sCount,
                    ["offset_targets_set"] = oCount,
                    ["offset_alignments"] = offIds.Count,
                    ["target_slots"] = slots,
                    ["link_codes"] = codes
                };
                tr.Commit();
                return res;
            }
        }

        // ===================== 6. Corridor surface =====================

        static JsonNode CreateCorridorSurface(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string corridorName = GetString(a, "corridor", alName + "_Corridor");
            string surfName = GetString(a, "name", alName + "_Design");
            bool boundary = GetBool(a, "boundary", true);

            var wanted = new List<string>();
            var arr = a["link_codes"] as JsonArray;
            if (arr != null) foreach (JsonNode n in arr) wanted.Add(n.GetValue<string>());

            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivCorridor corridor = FindCorridor(tr, db, corridorName);
                if (corridor == null)
                    throw new InvalidOperationException("Corridor '" + corridorName + "' not found; run create_corridor first.");

                var valid = new List<string>(corridor.GetLinkCodes());

                foreach (CivCorridorSurface ex in corridor.CorridorSurfaces)
                    if (ex.Name == surfName) { corridor.CorridorSurfaces.Remove(ex); break; }

                var cs = corridor.CorridorSurfaces.Add(surfName);
                var added = new JsonArray();
                var skipped = new JsonArray();

                if (valid.Count > 0)
                {
                    if (wanted.Count == 0)
                    {
                        foreach (string c in valid)
                            if (c.IndexOf("Top", StringComparison.OrdinalIgnoreCase) >= 0) wanted.Add(c);
                        if (wanted.Count == 0) wanted.AddRange(valid);
                    }

                    foreach (string code in wanted)
                    {
                        if (valid.Contains(code)) { cs.AddLinkCode(code, true); added.Add(code); }
                        else skipped.Add(code);
                    }
                }
                if (valid.Count > 0 && added.Count == 0)
                {
                    var avail = new JsonArray();
                    foreach (string c in valid) avail.Add(c);
                    throw new InvalidOperationException(
                        "No link code could be added. Codes available on this corridor: " + avail.ToJsonString());
                }

                if (boundary) cs.Boundaries.AddCorridorExtentsBoundary(surfName + "_OuterBoundary");
                corridor.Rebuild();

                var res = new JsonObject
                {
                    ["corridor_surface"] = surfName,
                    ["corridor"] = corridorName,
                    ["link_codes_added"] = added,
                    ["link_codes_skipped"] = skipped,
                    ["boundary"] = boundary
                };
                tr.Commit();
                return res;
            }
        }

        // ===================== 7. Sample line group =====================

        static void TraceSampleLineStage(string stage, string detail = "")
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("C3DF_AUDIT_LOG");
                if (string.IsNullOrWhiteSpace(path)) return;
                var row = new JsonObject
                {
                    ["at"] = DateTimeOffset.Now.ToString("O"),
                    ["event"] = "create_sample_lines_stage",
                    ["stage"] = stage,
                    ["detail"] = detail
                };
                File.AppendAllText(path, row.ToJsonString() + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        static JsonNode CreateSampleLines(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string sfName = Need(a, "surface");
            double interval = GetDouble(a, "interval", 50);
            double swath = GetDouble(a, "swath", 50);
            string style = GetString(a, "style", null);
            string corridorName = GetString(a, "corridor", alName + "_Corridor");
            string roadSurf = GetString(a, "road_surface", alName + "_Design");
            bool clearExisting = GetBool(a, "clear_existing", true);

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            string groupName = alName + "_SampleLines";
            JsonObject res;

            // Clear **all** old sample line groups on this alignment before creating: leftover groups make material computation pick the wrong group,
            // reporting "mappedSurface should have been sampled" (RiverQto pitfall #3, cost a long detour)
            int erased = 0;
            if (clearExisting)
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = FindAlignment(tr, civ, alName);
                if (al == null) throw new InvalidOperationException("Alignment '" + alName + "' not found.");
                foreach (ObjectId gid in al.GetSampleLineGroupIds())
                {
                    var g = (CivSampleLineGroup)tr.GetObject(gid, OpenMode.ForWrite);
                    g.Erase();
                    erased++;
                }
                tr.Commit();
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = FindAlignment(tr, civ, alName);
                if (al == null) throw new InvalidOperationException("Alignment '" + alName + "' not found.");

                ObjectId gid2 = CivSampleLineGroup.Create(groupName, al.ObjectId);
                var group = (CivSampleLineGroup)tr.GetObject(gid2, OpenMode.ForWrite);
                TraceSampleLineStage("group_created", groupName);

                var sampled = new JsonArray();
                var notSampled = new JsonArray();

                // Two sources, pick one: explicit lines (end-point coordinates per line, for cases like dykes where lines follow section lines in the drawing),
                // or the default equally spaced generation by interval/swath. Creation, style and sampled-source marking share one path.
                var linesArr = a["lines"] as JsonArray;
                int createdCount = 0;
                string creationMode;
                if (linesArr != null && linesArr.Count > 0)
                {
                    creationMode = "explicit_lines";
                    foreach (JsonNode ln in linesArr)
                    {
                        var lo = ln as JsonObject;
                        var ptsA = lo == null ? null : lo["points"] as JsonArray;
                        if (ptsA == null || ptsA.Count < 2)
                            throw new InvalidOperationException("Every item in lines must contain points:[[x,y],[x,y],...] (at least two points).");
                        var coll = new Point2dCollection();
                        foreach (JsonNode p in ptsA)
                        {
                            var pair = p as JsonArray;
                            if (pair == null || pair.Count < 2)
                                throw new InvalidOperationException("Every item in lines.points must be [x, y].");
                            coll.Add(new Point2d(pair[0].GetValue<double>(), pair[1].GetValue<double>()));
                        }
                        string slName = GetString(lo, "name", alName + "_SL" + (createdCount + 1));
                        TraceSampleLineStage("polyline_create_begin", slName);
                        CivSampleLine.Create(slName, gid2, coll);
                        TraceSampleLineStage("polyline_create_end", slName);
                        createdCount++;
                    }
                }
                else
                {
                    creationMode = "interval";
                    double start = al.StartingStation, end = al.EndingStation;
                    var stations = new List<double>();
                    for (double st = start; st < end - 0.001; st += interval) stations.Add(st);
                    if (stations.Count == 0 || end - stations[stations.Count - 1] > 0.5) stations.Add(end);

                    // Follow the path verified on the old console: create all sample lines from left/right end points first,
                    // then set the sampled sources; keep command defaults, do not set Dynamic, no extra Rebuild.
                    foreach (double st in stations)
                    {
                        double xL = 0, yL = 0, xR = 0, yR = 0;
                        al.PointLocation(st, -swath, ref xL, ref yL);
                        al.PointLocation(st, swath, ref xR, ref yR);
                        TraceSampleLineStage("polyline_create_begin", st.ToString("F3"));
                        CivSampleLine.Create(alName + "_SL-" + st.ToString("F0"), gid2,
                            new Point2dCollection
                            {
                                new Point2d(xL, yL),
                                new Point2d(xR, yR)
                            });
                        TraceSampleLineStage("polyline_create_end", st.ToString("F3"));
                        createdCount++;
                    }
                }

                ObjectId slStyle = FindStyleId(tr, civ.Styles.SampleLineStyles, style);
                if (!slStyle.IsNull)
                    foreach (ObjectId sid in group.GetSampleLineIds())
                        ((CivSampleLine)tr.GetObject(sid, OpenMode.ForWrite)).StyleId = slStyle;

                // Same as the old console: after the lines are created, only set IsSampled.
                foreach (CivSectionSource src in group.GetSectionSources())
                {
                    string st = "";
                    try { st = src.SourceType.ToString(); } catch { }
                    bool isCorridorSurf =
                        st.IndexOf("CorridorSurface", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool minesToo = src.SourceNameOf().StartsWith(corridorName + " ")
                                 || src.SourceNameOf().Contains(roadSurf);
                    bool keep = src.SourceNameOf() == sfName
                             || src.SourceNameOf() == corridorName
                             || (isCorridorSurf && minesToo);
                    src.IsSampled = keep;
                    string tag = src.SourceNameOf() + "  [" + st + "]";
                    if (keep) sampled.Add(tag); else notSampled.Add(tag);
                }

                res = new JsonObject
                {
                    ["group"] = groupName,
                    ["old_groups_erased"] = erased,
                    ["clear_existing"] = clearExisting,
                    ["sample_lines"] = createdCount,
                    ["interval"] = interval,
                    ["swath"] = swath,
                    ["creation_mode"] = creationMode,
                    ["sampled_sources"] = sampled,
                    ["ignored_sources"] = notSampled
                };
                TraceSampleLineStage("first_transaction_commit_begin");
                tr.Commit();
                TraceSampleLineStage("first_transaction_commit_end");
            }

            res["corridor_rebuilt_after_sampling"] = false;
            return res;
        }

        // ===================== 8. Compute quantities (material list) =====================

        static JsonNode ComputeQuantities(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string sfName = Need(a, "surface");
            string critName = Need(a, "criteria");
            string roadSlot = GetString(a, "road_surface_slot", null);
            // Without an explicit slot name the criteria's surface slots are classified by name
            // (EG/existing/ground -> terrain, Datum/FG/design/proposed/road/top -> corridor surface).
            bool autoSurfaceMapping = string.IsNullOrEmpty(roadSlot)
                || string.Equals(GetString(a, "surface_mapping", null), "auto", StringComparison.OrdinalIgnoreCase);
            string roadSurf = GetString(a, "road_surface", alName + "_Design");
            string corridorName = GetString(a, "corridor", alName + "_Corridor");

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = FindAlignment(tr, civ, alName);
                if (al == null) throw new InvalidOperationException("Alignment '" + alName + "' not found.");

                ObjectId slgId = ObjectId.Null;
                foreach (ObjectId gid in al.GetSampleLineGroupIds()) { slgId = gid; break; }
                if (slgId.IsNull)
                    throw new InvalidOperationException("Alignment '" + alName + "' has no sample line group; run create_sample_lines first.");

                ObjectId critId = FindQtoCriteria(tr, civ, critName);
                if (critId.IsNull) throw new InvalidOperationException("Quantity takeoff criteria '" + critName + "' not found.");

                ObjectId sfId = FindSurfaceId(tr, civ, sfName);
                if (sfId.IsNull) throw new InvalidOperationException("Surface '" + sfName + "' not found.");

                ObjectId roadId = FindCorridorSurfaceId(tr, db, corridorName, roadSurf);
                if (roadId.IsNull)
                    throw new InvalidOperationException("Corridor surface '" + roadSurf + "' not found; run create_corridor_surface first.");

                var slg = (CivSampleLineGroup)tr.GetObject(slgId, OpenMode.ForWrite);

                // Pre-check: both surfaces to be mapped must already be sampled in the group.
                // Unflagged ones are flagged automatically -- after the corridor surface is rebuilt under the same name (new ObjectId), the new source in the group
                // defaults to IsSampled=false, which is the last link in the "surface fixed but volume still 0" chain.
                bool egOk = false, roadOk = false;
                int marked = 0;
                foreach (CivSectionSource src in slg.GetSectionSources())
                {
                    bool isEg = src.SourceNameOf() == sfName;
                    bool isRoad = src.SourceNameOf().Contains(roadSurf);
                    if ((isEg || isRoad) && !src.IsSampled)
                    {
                        try { src.IsSampled = true; marked++; } catch { }
                    }
                    if (!src.IsSampled) continue;
                    if (isEg) egOk = true;
                    else if (isRoad) roadOk = true;
                }
                if (!egOk || !roadOk)
                    throw new InvalidOperationException("Sample line group is missing sampled sources (existing ground=" + egOk + ", corridor surface=" + roadOk +
                                                        "). Re-run create_sample_lines and compute again.");

                slg.MaterialLists.VolumeCalculationMethodType = CivVolumeMethod.AverageEndArea;

                // Overwrite/rebuild: clear all material lists on the group (names are auto-generated, cannot match by name)
                var toRemove = new List<Guid>();
                foreach (CivQtoMaterialList ex in slg.MaterialLists) toRemove.Add(ex.Guid);
                foreach (Guid g in toRemove) slg.MaterialLists.Remove(g);

                var slotLog = new JsonArray();
                using (var mapping = new CivQtoMapping(critId, slgId))
                {
                    // Read the real slot names from the criteria itself, never hard-code -- hard-coding fails silently, uses the wrong default surface -> all quantities 0
                    var crit = (CivQtoCriteria)tr.GetObject(critId, OpenMode.ForRead);
                    var surfaceSlots = new List<string>();
                    for (int i = 0; i < crit.Count; i++)
                    {
                        var item = crit[i];
                        for (int j = 0; j < item.Count; j++)
                        {
                            var data = item[j];
                            if (data.ItemType != CivMaterialItemType.Surface) continue;
                            if (!surfaceSlots.Contains(data.Name)) surfaceSlots.Add(data.Name);
                        }
                    }

                    var slotKinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (autoSurfaceMapping)
                    {
                        foreach (string slot in surfaceSlots)
                        {
                            string lower = slot.ToLowerInvariant();
                            bool ground = lower == "eg" || lower.StartsWith("eg ") || lower.Contains("ground") || lower.Contains("existing")
                                || lower.Contains("terrain") || lower.Contains("natural");
                            bool road = lower == "fg" || lower.Contains("datum") || lower.Contains("design") || lower.Contains("proposed")
                                || lower.Contains("road") || lower.Contains("finish") || lower == "top" || lower.Contains("corridor");
                            if (ground == road) slotKinds[slot] = "unknown";
                            else slotKinds[slot] = road ? "road" : "ground";
                        }
                        if (surfaceSlots.Count == 2)
                        {
                            string a0 = surfaceSlots[0], a1 = surfaceSlots[1];
                            if (slotKinds[a0] == "unknown" && slotKinds[a1] == "ground") slotKinds[a0] = "road";
                            if (slotKinds[a1] == "unknown" && slotKinds[a0] == "ground") slotKinds[a1] = "road";
                            if (slotKinds[a0] == "unknown" && slotKinds[a1] == "road") slotKinds[a0] = "ground";
                            if (slotKinds[a1] == "unknown" && slotKinds[a0] == "road") slotKinds[a1] = "ground";
                        }
                        var unknown = new List<string>();
                        foreach (string slot in surfaceSlots)
                            if (slotKinds[slot] == "unknown") unknown.Add(slot);
                        if (unknown.Count > 0)
                            throw new InvalidOperationException(
                                "Ambiguous automatic mapping of quantity criteria surface slots: " + string.Join(", ", unknown));
                    }

                    foreach (string slot in surfaceSlots)
                    {
                        bool isRoad = autoSurfaceMapping
                            ? slotKinds[slot] == "road"
                            : slot == roadSlot;
                        mapping.MapSurface(slot, isRoad ? roadId : sfId);
                        slotLog.Add(slot + " -> " + (isRoad ? roadSurf : sfName));
                    }
                    if (!mapping.isMappingCompleted)
                        throw new InvalidOperationException("Criteria mapping incomplete; mapped slots: " + slotLog.ToJsonString());

                    var ml = slg.MaterialLists.ImportCriteria(mapping);
                    var result = slg.GetTotalVolumeResultDataForMaterialList(ml.Guid);
                    var sections = result.GetResultsAlongSampleLines();

                    double cut = 0, fill = 0;
                    foreach (CivQtoSectionalResult sec in sections)
                    { cut = sec.VolumeResult.CumulativeCutVolume; fill = sec.VolumeResult.CumulativeFillVolume; }

                    var res = new JsonObject
                    {
                        ["alignment"] = alName,
                        ["criteria"] = critName,
                        ["sections"] = sections.Length,
                        ["mapped_slots"] = slotLog,
                        ["total_cut_m3"] = Math.Round(cut, 2),
                        ["total_fill_m3"] = Math.Round(fill, 2)
                    };
                    tr.Commit();
                    return res;
                }
            }
        }

        // ===================== 9. Export quantities =====================

        static JsonNode ExportQuantities(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string outdir = ResolveOutDir(a, doc);
            string format = GetString(a, "format", "both");

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var rows = new List<object[]>();
            double cut = 0, fill = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = FindAlignment(tr, civ, alName);
                if (al == null) throw new InvalidOperationException("Alignment '" + alName + "' not found.");

                ObjectId slgId = ObjectId.Null;
                foreach (ObjectId gid in al.GetSampleLineGroupIds()) { slgId = gid; break; }
                if (slgId.IsNull) throw new InvalidOperationException("Alignment '" + alName + "' has no sample line group.");

                var slg = (CivSampleLineGroup)tr.GetObject(slgId, OpenMode.ForRead);
                Guid mlGuid = Guid.Empty;
                bool found = false;
                foreach (CivQtoMaterialList ml in slg.MaterialLists) { mlGuid = ml.Guid; found = true; break; }
                if (!found) throw new InvalidOperationException("The sample line group has no material list; run compute_quantities first.");

                var result = slg.GetTotalVolumeResultDataForMaterialList(mlGuid);
                int i = 0;
                foreach (CivQtoSectionalResult sec in result.GetResultsAlongSampleLines())
                {
                    var v = sec.VolumeResult;
                    rows.Add(new object[]
                    {
                        ++i, Station(sec.Station),
                        Math.Round(v.CumulativeCutVolume, 3), Math.Round(v.CumulativeFillVolume, 3),
                        Math.Round(v.IncrementalCutVolume, 3), Math.Round(v.IncrementalFillVolume, 3)
                    });
                    cut = v.CumulativeCutVolume; fill = v.CumulativeFillVolume;
                }
                rows.Add(new object[] { "Total", "", Math.Round(cut, 3), Math.Round(fill, 3), "", "" });
                tr.Commit();
            }

            var files = new JsonArray();
            foreach (string p in Excel.Write(outdir, "Quantities_" + Sanitize(alName), HeadersQto, rows, format))
                files.Add(p);

            return new JsonObject
            {
                ["alignment"] = alName,
                ["sections"] = rows.Count - 1,
                ["total_cut_m3"] = Math.Round(cut, 2),
                ["total_fill_m3"] = Math.Round(fill, 2),
                ["files"] = files,
                ["outdir"] = outdir
            };
        }

        static readonly string[] HeadersQto =
            { "No.", "Station", "Cumulative cut(m³)", "Cumulative fill(m³)", "Incremental cut(m³)", "Incremental fill(m³)" };

        // ===================== 9.5 Drawing output: profile views / section views (model space) =====================
        // Ported from RiverQto's plotting module (field-tested). Placement follows the final scheme there:
        // let Civil 3D create the draft, then TransformBy each view sorted by station onto a custom grid
        // -- Civil 3D's own draft layout wraps/overlaps and cannot be controlled. Anchor = midpoint of the view's bottom edge = sv.Location.

        static JsonNode CreateProfileView(JsonObject a, Document doc)
        {
            double segmentLength = GetDouble(a, "segment_length", 0);
            if (segmentLength > 0 && !GetBool(a, "_single_segment", false))
                return CreateSegmentedProfileViews(a, doc, segmentLength);

            string alName = Need(a, "alignment");
            string style = GetString(a, "style", null);
            string bandSet = GetString(a, "band_set", null);
            string name = GetString(a, "name", alName + "_ProfileView");
            bool eraseExisting = GetBool(a, "erase_existing", true);
            double stationStart = GetDouble(a, "station_start", double.NaN);
            double stationEnd = GetDouble(a, "station_end", double.NaN);
            double elevMin = GetDouble(a, "elev_min", double.NaN);
            double elevMax = GetDouble(a, "elev_max", double.NaN);
            string groundProfileName = GetString(a, "ground_profile", null);
            string designProfileName = GetString(a, "design_profile", null);
            string groundLabelSet = GetString(a, "ground_label_set", null);
            string designLabelSet = GetString(a, "design_label_set", null);
            // Giving a "major station label style" directly (e.g. @EG / @DesignElevation) is more precise than a label set:
            // when given, the label group is built from it and the label-set path is skipped.
            string groundLabelStyle = GetString(a, "ground_label_style", null);
            string designLabelStyle = GetString(a, "design_label_style", null);
            double labelIncrement = GetDouble(a, "label_increment", 50);
            // ProfileView.Create brings in a "line label group" from the default label set
            // (Vertical Alignment Line Label Group, style Standard); it clutters the drawing, cleared by default
            bool dropLineLabels = GetBool(a, "drop_line_labels", true);

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            // Overwrite/rebuild: delete existing profile views on this alignment
            int erased = 0;
            if (eraseExisting)
            {
                using (var trDel = db.TransactionManager.StartTransaction())
                {
                    CivAlignment al0 = FindAlignment(trDel, civ, alName);
                    if (al0 == null) throw new InvalidOperationException("Alignment '" + alName + "' not found.");
                    foreach (ObjectId pvId in al0.GetProfileViewIds())
                    {
                        var pv = (Autodesk.Civil.DatabaseServices.ProfileView)trDel.GetObject(pvId, OpenMode.ForWrite);
                        pv.Erase();
                        erased++;
                    }
                    trDel.Commit();
                }
            }

            using (var tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = FindAlignment(tr, civ, alName);
                if (al == null) throw new InvalidOperationException("Alignment '" + alName + "' not found.");

                // Placement: defaults to 200 m directly below the alignment start, clear of the alignment itself
                double x = GetDouble(a, "x", double.NaN), y = GetDouble(a, "y", double.NaN);
                if (double.IsNaN(x) || double.IsNaN(y))
                {
                    double e = 0, n = 0;
                    al.PointLocation(al.StartingStation, 0, ref e, ref n);
                    if (double.IsNaN(x)) x = e;
                    if (double.IsNaN(y)) y = n - 200;
                }
                var origin = new Point3d(x, y, 0);

                ObjectId styleId = FindStyleId(tr, civ.Styles.ProfileViewStyles, style);
                ObjectId bandId = FindStyleId(tr, civ.Styles.ProfileViewBandSetStyles, bandSet);

                // When the drawing has no band set style, bandId is Null and the bandSet overload throws
                // "An ObjectId of ProfileViewBandSetStyle is excepted" -- so use the two-argument overload
                // and set name and style after creation.
                ObjectId pvId2 = bandId.IsNull
                    ? Autodesk.Civil.DatabaseServices.ProfileView.Create(al.ObjectId, origin)
                    : Autodesk.Civil.DatabaseServices.ProfileView.Create(
                          al.ObjectId, origin, name, bandId, styleId);

                var pv2 = (Autodesk.Civil.DatabaseServices.ProfileView)tr.GetObject(pvId2, OpenMode.ForWrite);
                try { pv2.Name = name; } catch { }
                if (!styleId.IsNull) { try { pv2.StyleId = styleId; } catch { } }
                if (!double.IsNaN(stationStart) || !double.IsNaN(stationEnd))
                {
                    double s0 = double.IsNaN(stationStart) ? al.StartingStation : stationStart;
                    double s1 = double.IsNaN(stationEnd) ? al.EndingStation : stationEnd;
                    s0 = Math.Max(al.StartingStation, s0);
                    s1 = Math.Min(al.EndingStation, s1);
                    if (s1 <= s0)
                        throw new InvalidOperationException(
                            "Invalid profile station range: " + Station(s0) + " ~ " + Station(s1) + ".");
                    pv2.StationRangeMode = Autodesk.Civil.DatabaseServices.StationRangeType.UserSpecified;
                    // Extend/shrink the end first, then set the start, so no intermediate state has start > end.
                    pv2.StationEnd = s1;
                    pv2.StationStart = s0;
                }
                if (!double.IsNaN(elevMin) || !double.IsNaN(elevMax))
                {
                    if (double.IsNaN(elevMin) || double.IsNaN(elevMax)
                        || elevMax <= elevMin)
                        throw new InvalidOperationException(
                            "Invalid profile elevation range; elev_min / elev_max must both be given and max > min.");
                    pv2.ElevationRangeMode =
                        Autodesk.Civil.DatabaseServices.ElevationRangeType.UserSpecified;
                    // Raise the upper bound first, then lower the lower bound, so no intermediate state has min > max.
                    pv2.ElevationMax = elevMax;
                    pv2.ElevationMin = elevMin;
                }

                var profiles = new JsonArray();
                ObjectId groundProfileId = ObjectId.Null;
                ObjectId designProfileId = ObjectId.Null;
                foreach (ObjectId pid in al.GetProfileIds())
                {
                    var p = (CivProfile)tr.GetObject(pid, OpenMode.ForRead);
                    profiles.Add(p.Name);
                    if (!string.IsNullOrEmpty(groundProfileName) && p.Name == groundProfileName)
                        groundProfileId = pid;
                    if (!string.IsNullOrEmpty(designProfileName) && p.Name == designProfileName)
                        designProfileId = pid;
                    string pt = p.ProfileType.ToString();
                    if (groundProfileId.IsNull && string.IsNullOrEmpty(groundProfileName)
                        && (pt.Equals("EG", StringComparison.OrdinalIgnoreCase)
                            || pt.IndexOf("Surface", StringComparison.OrdinalIgnoreCase) >= 0))
                        groundProfileId = pid;
                    if (designProfileId.IsNull && string.IsNullOrEmpty(designProfileName)
                        && (pt.Equals("FG", StringComparison.OrdinalIgnoreCase)
                            || pt.IndexOf("Layout", StringComparison.OrdinalIgnoreCase) >= 0))
                        designProfileId = pid;
                }
                if (groundProfileId.IsNull || designProfileId.IsNull)
                    throw new InvalidOperationException(
                        "Alignment '" + alName + "' cannot identify the existing ground / design profiles;"
                        + " pass ground_profile / design_profile explicitly.");

                // Band data sources: Profile1 = existing ground, Profile2 = design profile.
                int bandSourcesSet = 0;
                var topBands = pv2.Bands.GetTopBandItems();
                foreach (Autodesk.Civil.DatabaseServices.ProfileViewBandItem item in topBands)
                {
                    item.Profile1Id = groundProfileId;
                    item.Profile2Id = designProfileId;
                    bandSourcesSet++;
                }
                pv2.Bands.SetTopBandItems(topBands);
                var bottomBands = pv2.Bands.GetBottomBandItems();
                foreach (Autodesk.Civil.DatabaseServices.ProfileViewBandItem item in bottomBands)
                {
                    item.Profile1Id = groundProfileId;
                    item.Profile2Id = designProfileId;
                    bandSourcesSet++;
                }
                pv2.Bands.SetBottomBandItems(bottomBands);

                // ProfileView.Create brings in several ProfileLabelGroups from the old/default label set.
                // Clear them all, then create from the two @ label sets separately, so old and new labels do not stack.
                int oldProfileLabelGroupsErased = 0;
                foreach (ObjectId lid in pv2.GetLabelIds())
                {
                    var lg = tr.GetObject(lid, OpenMode.ForRead, false)
                        as Autodesk.Civil.DatabaseServices.ProfileLabelGroup;
                    if (lg == null) continue;
                    lg.UpgradeOpen();
                    lg.Erase();
                    oldProfileLabelGroupsErased++;
                }

                int groundLabels, designLabels;
                // Passing "none" = hang no labels on this profile (project B profiles, 2026-08-26:
                // the user rejected station/elevation labels and PVI grade labels, none wanted)
                bool groundNone = string.Equals(groundLabelSet, "none", StringComparison.OrdinalIgnoreCase);
                bool designNone = string.Equals(designLabelSet, "none", StringComparison.OrdinalIgnoreCase);
                if (groundNone)
                {
                    groundLabels = 0;
                    groundLabelSet = "(no labels)";
                }
                else if (!string.IsNullOrWhiteSpace(groundLabelStyle))
                {
                    ObjectId sid = NeedProfileLabelStyle(tr, civ, groundLabelStyle);
                    Autodesk.Civil.DatabaseServices.ProfileStationLabelGroup.CreateMajor(
                        pvId2, groundProfileId, sid, labelIncrement);
                    groundLabels = 1;
                    groundLabelSet = "(style used directly: " + TryGetName(tr.GetObject(sid, OpenMode.ForRead)) + ")";
                }
                else
                {
                    ObjectId groundLabelSetId = FindStyleId(
                        tr, civ.Styles.LabelSetStyles.ProfileLabelSetStyles, groundLabelSet);
                    if (groundLabelSetId.IsNull)
                        throw new InvalidOperationException(
                            "Existing ground label set not found and ground_label_style not given.");
                    groundLabelSet = TryGetName(tr.GetObject(groundLabelSetId, OpenMode.ForRead));
                    groundLabels = ApplyProfileLabelSet(tr, pvId2, groundProfileId, groundLabelSetId);
                }

                if (designNone)
                {
                    designLabels = 0;
                    designLabelSet = "(no labels)";
                }
                else if (!string.IsNullOrWhiteSpace(designLabelStyle))
                {
                    ObjectId sid = NeedProfileLabelStyle(tr, civ, designLabelStyle);
                    Autodesk.Civil.DatabaseServices.ProfileStationLabelGroup.CreateMajor(
                        pvId2, designProfileId, sid, labelIncrement);
                    designLabels = 1;
                    designLabelSet = "(style used directly: " + TryGetName(tr.GetObject(sid, OpenMode.ForRead)) + ")";
                }
                else
                {
                    ObjectId designLabelSetId = FindStyleId(
                        tr, civ.Styles.LabelSetStyles.ProfileLabelSetStyles, designLabelSet);
                    if (designLabelSetId.IsNull)
                        throw new InvalidOperationException(
                            "Design profile label set not found and design_label_style not given.");
                    designLabelSet = TryGetName(tr.GetObject(designLabelSetId, OpenMode.ForRead));
                    designLabels = ApplyProfileLabelSet(tr, pvId2, designProfileId, designLabelSetId);
                }

                // "none" must also clear the label groups hanging on the Profile objects themselves (PVI station/elevation, tangent grade, ...) --
                // they do not belong to the view; neither pv2.GetLabelIds() nor the view-side label set reaches them, and they render again after the view is rebuilt
                // (project B profiles, 2026-08-26: label set "none" left the drawing unchanged -- this batch was the cause).
                int profileLabelGroupsErased = 0;
                {
                    var killP = new List<ObjectId>();
                    if (groundNone) CollectProfileLabelGroupsInDb(tr, db, groundProfileId, killP);
                    if (designNone) CollectProfileLabelGroupsInDb(tr, db, designProfileId, killP);
                    // Both "none" = the profile view of this alignment gets no profile labels at all.
                    // Must scan **all** Profiles of the alignment: the PVI/grade label groups on the old design profile
                    // (referenced by the corridor, untouched by the repair step) still render into the new view -- clearing only the two newly sampled lines does nothing
                    // (project B, 2026-08-26: two rounds with no visible change, this was why).
                    if (groundNone && designNone)
                    {
                        foreach (ObjectId pid in al.GetProfileIds())
                            CollectProfileLabelGroupsInDb(tr, db, pid, killP);
                    }
                    foreach (ObjectId lid in killP)
                    {
                        try
                        {
                            var o = tr.GetObject(lid, OpenMode.ForWrite, false);
                            if (o != null && !o.IsErased) { o.Erase(); profileLabelGroupsErased++; }
                        }
                        catch { }
                    }
                }

                // Clear the line label group: it hangs on the profile (not among the view's labels),
                // so the pv2.GetLabelIds() sweep above misses it; handled separately here.
                int lineLabelGroupsErased = 0;
                if (dropLineLabels)
                {
                    var kill = new List<ObjectId>();
                    CollectLineLabelGroups(tr, pv2.GetLabelIds(), kill);
                    // The line label group hangs on the profile and may not be in the view's label list;
                    // scan model space once more, collecting only those whose ProfileViewId points to this view, so other views are untouched
                    CollectLineLabelGroupsInDb(tr, db, pvId2, kill);
                    foreach (ObjectId lid in kill)
                    {
                        try
                        {
                            var o = tr.GetObject(lid, OpenMode.ForWrite, false);
                            if (o != null && !o.IsErased) { o.Erase(); lineLabelGroupsErased++; }
                        }
                        catch { }
                    }
                }

                var res = new JsonObject
                {
                    ["profile_view"] = name,
                    ["alignment"] = alName,
                    ["old_erased"] = erased,
                    ["origin"] = Math.Round(x, 3) + ", " + Math.Round(y, 3),
                    ["station_start"] = Math.Round(pv2.StationStart, 3),
                    ["station_end"] = Math.Round(pv2.StationEnd, 3),
                    ["elev_range"] = pv2.ElevationRangeMode ==
                        Autodesk.Civil.DatabaseServices.ElevationRangeType.UserSpecified
                        ? Math.Round(pv2.ElevationMin, 3) + " ~ "
                          + Math.Round(pv2.ElevationMax, 3)
                        : "auto",
                    ["profiles_shown"] = profiles,
                    ["band_profile1"] = TryGetName(tr.GetObject(groundProfileId, OpenMode.ForRead)),
                    ["band_profile2"] = TryGetName(tr.GetObject(designProfileId, OpenMode.ForRead)),
                    ["band_items_sources_set"] = bandSourcesSet,
                    ["old_profile_label_groups_erased"] = oldProfileLabelGroupsErased,
                    ["ground_label_set"] = groundLabelSet,
                    ["ground_label_groups_created"] = groundLabels,
                    ["design_label_set"] = designLabelSet,
                    ["design_label_groups_created"] = designLabels,
                    ["line_label_groups_erased"] = lineLabelGroupsErased
                };
                tr.Commit();
                return res;
            }
        }

        static JsonNode CreateSegmentedProfileViews(
            JsonObject a, Document doc, double segmentLength)
        {
            if (segmentLength <= 0)
                throw new InvalidOperationException("segment_length must be greater than 0.");

            string alName = Need(a, "alignment");
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            double routeStart, routeEnd, baseX, baseY;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = FindAlignment(tr, civ, alName);
                if (al == null)
                    throw new InvalidOperationException("Alignment '" + alName + "' not found.");
                routeStart = al.StartingStation;
                routeEnd = al.EndingStation;
                baseX = GetDouble(a, "x", double.NaN);
                baseY = GetDouble(a, "y", double.NaN);
                if (double.IsNaN(baseX) || double.IsNaN(baseY))
                {
                    double e = 0, n = 0;
                    al.PointLocation(routeStart, 0, ref e, ref n);
                    if (double.IsNaN(baseX)) baseX = e;
                    if (double.IsNaN(baseY)) baseY = n - 200;
                }
                tr.Commit();
            }

            int count = Math.Max(1,
                (int)Math.Ceiling((routeEnd - routeStart) / segmentLength));
            int cols = Math.Max(1, (int)GetDouble(a, "segment_cols", 3));
            double gapX = GetDouble(a, "segment_spacing_x", segmentLength + 50);
            double gapY = GetDouble(a, "segment_spacing_y", 220);
            string baseName = GetString(a, "name", alName + "_ProfileView");
            var views = new JsonArray();
            int erased = 0;

            for (int i = 0; i < count; i++)
            {
                double s0 = routeStart + i * segmentLength;
                double s1 = Math.Min(routeEnd, s0 + segmentLength);
                int col = i % cols, row = i / cols;
                var child = JsonNode.Parse(a.ToJsonString()) as JsonObject;
                child["segment_length"] = 0;
                child["_single_segment"] = true;
                child["erase_existing"] = i == 0 && GetBool(a, "erase_existing", true);
                child["station_start"] = s0;
                child["station_end"] = s1;
                child["x"] = baseX + col * gapX;
                child["y"] = baseY - row * gapY;
                child["name"] = count == 1
                    ? baseName
                    : baseName + "-" + (i + 1).ToString("00");
                JsonObject result = CreateProfileView(child, doc) as JsonObject;
                if (result == null)
                    throw new InvalidOperationException("The segmented profile node returned no object.");
                erased += result["old_erased"] == null
                    ? 0 : result["old_erased"].GetValue<int>();
                views.Add(result);
            }

            var first = views[0] as JsonObject;
            return new JsonObject
            {
                ["profile_view"] = first == null || first["profile_view"] == null
                    ? null : first["profile_view"].DeepClone(),
                ["profile_views"] = views,
                ["count"] = views.Count,
                ["alignment"] = alName,
                ["segment_length"] = segmentLength,
                ["station_start"] = routeStart,
                ["station_end"] = routeEnd,
                ["old_erased"] = erased,
                ["layout"] = new JsonObject
                {
                    ["cols"] = cols,
                    ["spacing_x"] = gapX,
                    ["spacing_y"] = gapY
                }
            };
        }

        static ObjectId FindExactStyleId(Transaction tr, object collection, string name, string category)
        {
            var en = collection as System.Collections.IEnumerable;
            if (en != null)
                foreach (object item in en)
                {
                    if (!(item is ObjectId)) continue;
                    ObjectId id = (ObjectId)item;
                    if (TryGetName(tr.GetObject(id, OpenMode.ForRead)) == name) return id;
                }
            throw new InvalidOperationException(category + " '" + name + "' not found.");
        }

        /// <summary>Pick the "line label group" (Vertical Alignment Line Label Group) -- identified by type name;
        /// station/curve/PVI groups are left alone.</summary>
        static void CollectLineLabelGroups(Transaction tr, ObjectIdCollection ids, List<ObjectId> outIds)
        {
            if (ids == null) return;
            foreach (ObjectId id in ids)
            {
                if (id.IsNull || outIds.Contains(id)) continue;
                try
                {
                    DBObject o = tr.GetObject(id, OpenMode.ForRead, false);
                    if (o == null) continue;
                    string t = o.GetType().Name;
                    if (t.IndexOf("Line", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        t.IndexOf("LabelGroup", StringComparison.OrdinalIgnoreCase) >= 0)
                        outIds.Add(id);
                }
                catch { }
            }
        }

        /// <summary>Find this profile view's line label groups in model space: identified by type name, ownership compared via ProfileViewId reflection;
        /// anything whose owner cannot be determined is left alone (better to keep it than delete another view's labels).</summary>
        /// <summary>Collect all label group entities hanging on the given profile (Profile object):
        /// PVI station/elevation, tangent grade, etc.; type name contains LabelGroup and ProfileId points to it.
        /// These labels belong to no view; the view-side label set cannot reach them, so "no labels" must delete them from the database by name.</summary>
        static void CollectProfileLabelGroupsInDb(Transaction tr, Database db,
            ObjectId profileId, List<ObjectId> outIds)
        {
            if (profileId.IsNull) return;
            foreach (ObjectId id in ModelSpace(db, tr))
            {
                if (id.IsNull || outIds.Contains(id)) continue;
                try
                {
                    DBObject o = tr.GetObject(id, OpenMode.ForRead, false);
                    if (o == null) continue;
                    if (o.GetType().Name.IndexOf("LabelGroup", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var p = o.GetType().GetProperty("ProfileId");
                    if (p == null) continue;
                    object v = p.GetValue(o, null);
                    if (v is ObjectId && (ObjectId)v == profileId) outIds.Add(id);
                }
                catch { }
            }
        }

        static void CollectLineLabelGroupsInDb(Transaction tr, Database db,
            ObjectId profileViewId, List<ObjectId> outIds)
        {
            foreach (ObjectId id in ModelSpace(db, tr))
            {
                if (id.IsNull || outIds.Contains(id)) continue;
                try
                {
                    DBObject o = tr.GetObject(id, OpenMode.ForRead, false);
                    if (o == null) continue;
                    string t = o.GetType().Name;
                    if (t.IndexOf("Line", StringComparison.OrdinalIgnoreCase) < 0 ||
                        t.IndexOf("LabelGroup", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var p = o.GetType().GetProperty("ProfileViewId");
                    if (p == null) continue;
                    object v = p.GetValue(o, null);
                    if (v is ObjectId && (ObjectId)v == profileViewId) outIds.Add(id);
                }
                catch { }
            }
        }

        /// <summary>Find a profile major station label style by name (e.g. @EG); throw and list the available ones when not found,
        /// never fall back to "first in collection" -- a wrong label style is harder to spot than none.</summary>
        static ObjectId NeedProfileLabelStyle(Transaction tr, CivDoc civ, string name)
        {
            object coll = civ.Styles.LabelStyles.ProfileLabelStyles.MajorStationLabelStyles;
            var seen = new List<string>();
            ObjectId exact = ObjectId.Null, ci = ObjectId.Null;
            var en = coll as System.Collections.IEnumerable;
            if (en != null)
                foreach (object item in en)
                {
                    if (!(item is ObjectId)) continue;
                    ObjectId id = (ObjectId)item;
                    string n = null;
                    try { n = TryGetName(tr.GetObject(id, OpenMode.ForRead)); }
                    catch { }
                    if (string.IsNullOrEmpty(n)) continue;
                    seen.Add(n);
                    if (n == name) { exact = id; break; }
                    if (ci.IsNull && string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ci = id;
                }
            if (!exact.IsNull) return exact;
            if (!ci.IsNull) return ci;
            throw new InvalidOperationException(
                "Profile major station label style '" + name + "' not found. Available in the drawing: " +
                (seen.Count == 0 ? "(none)" : string.Join(", ", seen)));
        }

        static int ApplyProfileLabelSet(Transaction tr, ObjectId profileViewId,
                                        ObjectId profileId, ObjectId labelSetId)
        {
            var set = (Autodesk.Civil.DatabaseServices.Styles.ProfileLabelSetStyle)
                tr.GetObject(labelSetId, OpenMode.ForRead);
            int created = 0;
            foreach (Autodesk.Civil.DatabaseServices.Styles.ProfileLabelSetItem item in set)
            {
                string kind = item.LabelStyleType.ToString();
                double increment = 50;
                try { if (item.Increment > 0) increment = item.Increment; } catch { }
                // Label set item types map onto their label group classes; kinds without a managed Create (grade breaks, curves, minor stations)
                // are skipped rather than failing the whole profile view.
                if (kind.Equals("ProfileMajorStation", StringComparison.OrdinalIgnoreCase))
                    Autodesk.Civil.DatabaseServices.ProfileStationLabelGroup.CreateMajor(profileViewId, profileId, item.LabelStyleId, increment);
                else if (kind.Equals("ProfileLine", StringComparison.OrdinalIgnoreCase))
                    Autodesk.Civil.DatabaseServices.ProfileLineLabelGroup.Create(profileViewId, profileId, item.LabelStyleId);
                else if (kind.Equals("ProfilePVI", StringComparison.OrdinalIgnoreCase))
                    Autodesk.Civil.DatabaseServices.ProfilePVILabelGroup.Create(profileViewId, profileId, item.LabelStyleId);
                else if (kind.Equals("ProfileHorizontalGeometryPoint", StringComparison.OrdinalIgnoreCase))
                    Autodesk.Civil.DatabaseServices.ProfileHorizontalGeometryPointLabelGroup.Create(profileViewId, profileId, item.LabelStyleId);
                else continue;
                created++;
            }
            return created;
        }

        static JsonNode CreateSectionViews(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string style = GetString(a, "style", null);
            string codeSet = GetString(a, "code_set", null);
            double elevMin = GetDouble(a, "elev_min", 0);
            double elevMax = GetDouble(a, "elev_max", 0);      // min>=max -> automatic elevation range
            double offLeft = GetDouble(a, "offset_left", 50);
            double offRight = GetDouble(a, "offset_right", 50);
            int rows = (int)GetDouble(a, "rows", 2);
            int cols = (int)GetDouble(a, "cols", 2);
            double colSpacing = GetDouble(a, "col_spacing", 130);
            double rowSpacing = GetDouble(a, "row_spacing", 45);
            // Extra vertical clearance appended between rows x cols page groups, so consecutive section groups fall strictly into adjacent sheet frames.
            double groupSpacing = GetDouble(a, "group_spacing", 0);
            // Volume tables default to off: they can be created, but leave unclosed write handles and save_dwg then always throws eWasOpenForWrite
            // (opening the section view ForRead to build the table crashes the process outright). If you need tables, replace save_dwg with another way to save,
            // or add them in the GUI. See item 8 of the pitfalls notes from the 2026-07-26 task.
            bool wantVolTable = GetBool(a, "volume_table", false);
            string corridorName = GetString(a, "corridor", alName + "_Corridor");
            string placement = (GetString(a, "placement", "draft") ?? "draft").Trim().ToLowerInvariant();
            string templateFile = GetString(a, "template", null);
            string layoutName = GetString(a, "layout", null);
            string groupPlotStyle = GetString(a, "group_plot_style", null);

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            // Group name resolution: use group when given; otherwise look for the old convention <alignment>_SampleLines,
            // and if not found and the alignment has exactly one group use it (GUI-built groups have arbitrary names); with several groups one must be named.
            string groupName = GetString(a, "group", null);
            using (Transaction trg = db.TransactionManager.StartTransaction())
            {
                CivAlignment alg = FindAlignment(trg, civ, alName);
                if (alg == null)
                    throw new InvalidOperationException("Alignment '" + alName + "' not found.");
                var names = new List<string>();
                foreach (ObjectId gid in alg.GetSampleLineGroupIds())
                    names.Add(((CivSampleLineGroup)trg.GetObject(gid, OpenMode.ForRead)).Name);
                if (groupName == null)
                {
                    string conv = alName + "_SampleLines";
                    if (names.Contains(conv)) groupName = conv;
                    else if (names.Count == 1) groupName = names[0];
                    else if (names.Count == 0)
                        throw new InvalidOperationException(
                            "Alignment '" + alName + "' has no sample line group; run create_sample_lines first (or refresh_sample_lines after changing lines).");
                    else
                        throw new InvalidOperationException(
                            "Alignment '" + alName + "' has several sample line groups (" + string.Join(", ", names) + "); pick one with the group parameter.");
                }
                else if (!names.Contains(groupName))
                {
                    throw new InvalidOperationException(
                        "Alignment '" + alName + "' has no sample line group named '" + groupName + "' (existing: " +
                        (names.Count == 0 ? "none" : string.Join(", ", names)) + ").");
                }

                // Corridor name resolution (same disease, same cure as the group name): use corridor when given;
                // use the old convention <alignment>_Corridor if it exists; otherwise, if exactly one corridor has its baseline on this alignment, use it
                // (GUI-built corridors have names like "Road[Z1](2)"); several must be named explicitly, none at all is an error --
                // otherwise the guard only finds out after creation that the corridor body has zero sections, wasting a pile of views and blocking the layout.
                if (a["corridor"] == null)
                {
                    string convCorr = alName + "_Corridor";
                    bool convExists = false;
                    var mine = new List<string>();
                    foreach (ObjectId id in ModelSpace(db, trg))
                    {
                        CivCorridor cor;
                        try { cor = trg.GetObject(id, OpenMode.ForRead) as CivCorridor; }
                        catch { continue; }
                        if (cor == null) continue;
                        if (string.Equals(cor.Name, convCorr, StringComparison.OrdinalIgnoreCase))
                            convExists = true;
                        try
                        {
                            foreach (Autodesk.Civil.DatabaseServices.Baseline bl in cor.Baselines)
                            {
                                CivAlignment bal;
                                try { bal = trg.GetObject(bl.AlignmentId, OpenMode.ForRead) as CivAlignment; }
                                catch { continue; }
                                if (bal != null && string.Equals(bal.Name, alName, StringComparison.OrdinalIgnoreCase))
                                {
                                    mine.Add(cor.Name);
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                    if (convExists) corridorName = convCorr;
                    else if (mine.Count == 1) corridorName = mine[0];
                    else if (mine.Count > 1)
                        throw new InvalidOperationException(
                            "Alignment '" + alName + "' has several corridors (" + string.Join(", ", mine) +
                            "); pick one with the corridor parameter.");
                    else
                        throw new InvalidOperationException(
                            "Alignment '" + alName + "' has no corridor: '" + convCorr +
                            "' not found, and no corridor has its baseline on this alignment. Build a corridor before creating sections.");
                }
                trg.Commit();
            }

            // Project-specific SACs often produce new link codes, while the style library's code set only has point codes.
            // Source, Draw and code set name all look fine, but when not a single link code is mapped, corridor sections stay blank.
            JsonObject codeMappingResult = null;
            var codeMappings = a["code_mappings"] as JsonArray;
            if (codeMappings != null && codeMappings.Count > 0)
            {
                if (string.IsNullOrEmpty(codeSet))
                    throw new InvalidOperationException("code_set must be given when code_mappings is provided.");

                // Adding code items and setting labels cannot share one transaction: Civil 3D throws eInvalidInput
                // when LabelStyleId is written on a freshly added CodeSetStyleItem.
                // Commit the link styles first, then attach labels in a second transaction.
                var styleItems = new JsonArray();
                var labelItems = new JsonArray();
                foreach (JsonNode n in codeMappings)
                {
                    var item = n as JsonObject;
                    if (item == null || item["code"] == null) continue;
                    if (item["style"] != null)
                        styleItems.Add(new JsonObject
                        {
                            ["code"] = item["code"].DeepClone(),
                            ["style"] = item["style"].DeepClone(),
                            ["style_type"] = item["style_type"] == null
                                ? "link" : item["style_type"].DeepClone()
                        });
                    if (item["label_style"] != null)
                        labelItems.Add(new JsonObject
                        {
                            ["code"] = item["code"].DeepClone(),
                            ["label_style"] = item["label_style"].DeepClone(),
                            ["style_type"] = item["style_type"] == null
                                ? "link" : item["style_type"].DeepClone()
                        });
                }

                var styleArgs = new JsonObject
                {
                    ["name"] = codeSet,
                    ["items"] = styleItems,
                    ["dry_run"] = false
                };
                JsonObject styleResult = CodeSetEdit(styleArgs, doc) as JsonObject;
                var styleFailed = styleResult == null ? null : styleResult["failed"] as JsonArray;
                if (styleResult == null || (styleFailed != null && styleFailed.Count > 0))
                    throw new InvalidOperationException(
                        "Corridor code set link style mapping failed: " +
                        (styleResult == null ? "(no result)" : styleResult.ToJsonString()));

                JsonObject labelResult = null;
                if (labelItems.Count > 0)
                {
                    var labelArgs = new JsonObject
                    {
                        ["name"] = codeSet,
                        ["items"] = labelItems,
                        ["dry_run"] = false
                    };
                    labelResult = CodeSetEdit(labelArgs, doc) as JsonObject;
                    var labelFailed = labelResult == null
                        ? null : labelResult["failed"] as JsonArray;
                    if (labelResult == null || (labelFailed != null && labelFailed.Count > 0))
                        throw new InvalidOperationException(
                            "Corridor code set link label mapping failed: " +
                            (labelResult == null ? "(no result)" : labelResult.ToJsonString()));
                }
                codeMappingResult = new JsonObject
                {
                    ["style_pass"] = styleResult,
                    ["label_pass"] = labelResult
                };
            }

            // Base point: defaults to 400 m below the alignment start (below the profile view, no overlap)
            double bx = GetDouble(a, "x", double.NaN), by = GetDouble(a, "y", double.NaN);
            using (var tr0 = db.TransactionManager.StartTransaction())
            {
                CivAlignment al0 = FindAlignment(tr0, civ, alName);
                if (al0 == null) throw new InvalidOperationException("Alignment '" + alName + "' not found.");
                if (double.IsNaN(bx) || double.IsNaN(by))
                {
                    double e = 0, n = 0;
                    al0.PointLocation(al0.StartingStation, 0, ref e, ref n);
                    if (double.IsNaN(bx)) bx = e;
                    if (double.IsNaN(by)) by = n - 400;
                }
                tr0.Commit();
            }
            var basePoint = new Point3d(bx, by, 0);

            // Overwrite/rebuild: delete existing section views of this group (volume tables attached to the views go with them)
            int erased = 0;
            using (var trDel = db.TransactionManager.StartTransaction())
            {
                CivSampleLineGroup g = FindSampleLineGroup(trDel, civ, alName, groupName);
                if (g == null)
                    throw new InvalidOperationException("Sample line group '" + groupName + "' not found; run create_sample_lines first.");
                foreach (ObjectId slId in g.GetSampleLineIds())
                {
                    var sl = (CivSampleLine)trDel.GetObject(slId, OpenMode.ForRead);
                    foreach (ObjectId svId in sl.GetSectionViewIds())
                    {
                        ((Autodesk.Civil.DatabaseServices.SectionView)trDel.GetObject(svId, OpenMode.ForWrite)).Erase();
                        erased++;
                    }
                }
                trDel.Commit();
            }

            int count = 0;
            int secStyled = 0;
            int surfStyled = 0;   // number of surface (ground line) sections that got a style
            int corridorBodySections = 0;
            int corridorSurfaceSections = 0;
            int corridorDisplayOverrides = 0;
            string creationMode = "";
            int materialShapeStylesSet = 0;
            int materialSectionsStyled = 0;
            string creationNote = "";
            string corridorCodeSetBefore = "";
            bool corridorCodeSetSet = false;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                CivSampleLineGroup slg = FindSampleLineGroup(tr, civ, alName, groupName);
                slg.UpgradeOpen();

                ObjectId codeSetId = FindStyleId(
                    tr, civ.Styles.CodeSetStyles, codeSet);

                // * The **primary** place for the code set: the corridor's own CodeSetStyleId.
                // How sections render in the drawing (link lines, point markers and the labels attached to them) reads this,
                // not the section source's StyleId nor Section.StyleId.
                // Previously only the latter two were set, so the corridor kept its old code set (on project B: river-dregde-1-500[recommend],
                // only 10 mappings, 10 codes uncovered) and point labels never appeared.
                // (Confirmed 2026-07-28; the user said from the start that "the code set hangs on the corridor itself", it took a long detour to follow that)
                if (!codeSetId.IsNull)
                {
                    foreach (ObjectId eid in ModelSpace(db, tr))
                    {
                        var cor = tr.GetObject(eid, OpenMode.ForRead)
                                  as Autodesk.Civil.DatabaseServices.Corridor;
                        if (cor == null || cor.Name != corridorName) continue;
                        try
                        {
                            corridorCodeSetBefore = cor.CodeSetStyleName;
                            cor.UpgradeOpen();
                            cor.CodeSetStyleId = codeSetId;
                            corridorCodeSetSet = true;
                        }
                        catch (System.Exception ex)
                        { creationNote += " setting corridor code set failed: " + ex.GetType().Name + ": " + Truncate(ex.Message, 80) + ";"; }
                        break;
                    }
                }

                // Code set fallback (1): source level (the Style slot of the corridor source)
                if (!codeSetId.IsNull)
                    foreach (CivSectionSource src in slg.GetSectionSources())
                        if (src.SourceNameOf() == corridorName)
                            try { src.StyleId = codeSetId; } catch { }

                var rangeOpts = new Autodesk.Civil.DatabaseServices.SectionViewGroupCreationRangeOptions(slg.ObjectId);
                rangeOpts.SetOffsetRange(-offLeft, offRight);
                rangeOpts.UseUserSpecifiedOffset = true;

                // Placement: "draft" = grid in model space (default); "production" = the native "Create Multiple Section Views"
                // sheet path: a drawing template with a layout + viewport decides how many views fit one sheet, and the group plot
                // style (GroupPlotStyles) decides how they are arranged. Elevation range stays automatic per view (the wizard only
                // offers height + anchor, absolute min/max are applied per view afterwards, same as the draft path).
                var placeOpts = new Autodesk.Civil.DatabaseServices.SectionViewGroupCreationPlacementOptions();
                if (placement == "production")
                {
                    if (string.IsNullOrEmpty(templateFile) || !File.Exists(templateFile))
                        throw new InvalidOperationException("placement:production needs template (an existing .dwt/.dwg with a layout that holds a viewport); got '" + templateFile + "'.");
                    if (string.IsNullOrEmpty(layoutName))
                    {
                        var available = new List<string>();
                        try { foreach (var n in placeOpts.GetAvailableLayoutNames(templateFile)) available.Add(n); } catch { }
                        if (available.Count == 0) throw new InvalidOperationException("No layouts found in template '" + templateFile + "'.");
                        layoutName = available[0];
                    }
                    placeOpts.UseProductionPlacement(templateFile, layoutName);
                }
                else if (placement == "draft") placeOpts.UseDraftPlacement();
                else throw new InvalidOperationException("placement must be draft or production; got '" + placement + "'.");
                ObjectId plotStyleId = ObjectId.Null;
                if (!string.IsNullOrEmpty(groupPlotStyle))
                {
                    plotStyleId = FindStyleIdStrict(tr, civ.Styles.GroupPlotStyles, groupPlotStyle);
                    if (plotStyleId.IsNull) throw new InvalidOperationException("group_plot_style '" + groupPlotStyle + "' not found in GroupPlotStyles.");
                }

                CivAlignment al = FindAlignment(tr, civ, alName);
                ObjectId svStyleId = FindStyleId(tr, civ.Styles.SectionViewStyles, style);
                // Section style for surface (ground line) sections is specified when the group is created, not patched afterwards
                ObjectId secStyleId = FindStyleId(tr, civ.Styles.SectionStyles,
                                                  GetString(a, "section_style", null));
                // Material sections (cut etc.) render through MaterialSection.StyleId, which may be a ShapeStyle (hatch) or a
                // SectionStyle; look the name up in ShapeStyles first, then SectionStyles. Omitted = same as the surface sections.
                ObjectId matStyleId = ObjectId.Null;
                string matStyleName = GetString(a, "material_style", null);
                if (!string.IsNullOrEmpty(matStyleName))
                {
                    matStyleId = FindStyleIdStrict(tr, civ.Styles.ShapeStyles, matStyleName);
                    if (matStyleId.IsNull) matStyleId = FindStyleIdStrict(tr, civ.Styles.SectionStyles, matStyleName);
                    if (matStyleId.IsNull) throw new InvalidOperationException("material_style '" + matStyleName + "' not found in ShapeStyles or SectionStyles.");
                    // A shape style also goes onto QTOMaterial.ShapeStyleId: that is what draws the material hatch in the views
                    // (the stock material list starts at "Basic" = solid fill); same as restyle_section_views material_shape_style.
                    ObjectId matShapeId = FindStyleIdStrict(tr, civ.Styles.ShapeStyles, matStyleName);
                    if (!matShapeId.IsNull)
                    {
                        foreach (CivQtoMaterialList ml in slg.MaterialLists)
                            foreach (object item in (System.Collections.IEnumerable)ml)
                            {
                                var pShape = item?.GetType().GetProperty("ShapeStyleId");
                                if (pShape == null || !pShape.CanWrite) continue;
                                try { pShape.SetValue(item, matShapeId); materialShapeStylesSet++; } catch { }
                            }
                    }
                }

                // Follow the path verified on the old console: five-argument draft creation.
                slg.SectionViewGroups.Add(basePoint, al.StartingStation, al.EndingStation,
                                          rangeOpts, placeOpts);
                creationMode = placement == "production"
                    ? "production placement (template " + Path.GetFileName(templateFile) + ", layout " + layoutName + ")"
                    : "draft placement (5 args)";
                if (!plotStyleId.IsNull)
                {
                    Autodesk.Civil.DatabaseServices.SectionViewGroup newest = null;
                    foreach (Autodesk.Civil.DatabaseServices.SectionViewGroup g in slg.SectionViewGroups) newest = g;
                    if (newest != null)
                    {
                        try { newest.PlotStyleId = plotStyleId; creationNote += " group plot style " + groupPlotStyle + ";"; }
                        catch (System.Exception ex) { creationNote += " group plot style not set: " + ex.GetType().Name + ": " + Truncate(ex.Message, 80) + ";"; }
                    }
                }

                bool manualElev = elevMin < elevMax;
                foreach (ObjectId slId in slg.GetSampleLineIds())
                {
                    var sl = (CivSampleLine)tr.GetObject(slId, OpenMode.ForRead);
                    foreach (ObjectId svId in sl.GetSectionViewIds())
                    {
                        var sv = (Autodesk.Civil.DatabaseServices.SectionView)tr.GetObject(svId, OpenMode.ForWrite);
                        if (!svStyleId.IsNull) sv.StyleId = svStyleId;
                        if (manualElev)
                        {
                            sv.IsElevationRangeAutomatic = false;
                            sv.ElevationMin = elevMin;
                            sv.ElevationMax = elevMax;
                        }
                        else sv.IsElevationRangeAutomatic = true;
                        count++;
                    }
                }

                // The section's own style follows different rules for the two sources (confirmed 2026-07-28 by comparing old and new sections property by property):
                //   corridor section -> StyleId is the **code set style** (by Civil's design)
                //   surface section (ground line) -> StyleId is a **section style** (e.g. Existing Ground / @C3DF-GroundLine)
                // Previously only the former was set; the latter stayed at Standard, and the plot showed the "wrong style".
                // secStyleId was resolved before the group was built; this is a safety net afterwards (still applied when the 8-arg path fails and falls back to 5 args)
                foreach (CivSectionSource src in slg.GetSectionSources())
                {
                    bool isCorridor = src.SourceNameOf() == corridorName;
                    string sourceType = "";
                    try { sourceType = src.SourceType.ToString(); } catch { }
                    // Three sources, three styles: corridor section = code set style (Civil's design),
                    // material section (cut) = material_style, other surface sections = section_style
                    bool isMaterial = sourceType.IndexOf("Material", StringComparison.OrdinalIgnoreCase) >= 0;
                    int sourceSectionCount = 0;
                    ObjectId want = isCorridor ? codeSetId
                                  : isMaterial && !matStyleId.IsNull ? matStyleId
                                  : secStyleId;
                    foreach (ObjectId secId in src.GetSectionIds())
                    {
                        sourceSectionCount++;
                        try
                        {
                            var sec = (Autodesk.Civil.DatabaseServices.Section)
                                tr.GetObject(secId, OpenMode.ForWrite);
                            if (!want.IsNull)
                            {
                                sec.StyleId = want;
                                if (isCorridor) secStyled++; else surfStyled++;
                            }
                        }
                        catch { }
                    }
                    if (isCorridor) corridorBodySections += sourceSectionCount;
                    else if (sourceType.IndexOf("CorridorSurface",
                             StringComparison.OrdinalIgnoreCase) >= 0)
                        corridorSurfaceSections += sourceSectionCount;
                }
                // Material sections (cut/fill hatch) are not listed under the group's section sources: reach them through each
                // sample line's own sections and set their style slot, which is what the views render from (same as restyle_section_views;
                // without this the hatch stays at the default style and shows as a solid fill).
                if (!matStyleId.IsNull)
                {
                    foreach (ObjectId slId in slg.GetSampleLineIds())
                    {
                        CivSampleLine sl;
                        try { sl = (CivSampleLine)tr.GetObject(slId, OpenMode.ForRead); } catch { continue; }
                        ObjectIdCollection secIds = null;
                        try { secIds = sl.GetSectionIds(); } catch { }
                        if (secIds == null) continue;
                        foreach (ObjectId secId in secIds)
                        {
                            try
                            {
                                DBObject so = tr.GetObject(secId, OpenMode.ForRead);
                                if (so.GetType().Name != "MaterialSection") continue;
                                so.UpgradeOpen();
                                ((Autodesk.Civil.DatabaseServices.Section)so).StyleId = matStyleId;
                                materialSectionsStyled++;
                            }
                            catch { }
                        }
                    }
                }
                tr.Commit();
            }
            if (count == 0) throw new InvalidOperationException("No section views were generated.");
            if (corridorBodySections == 0)
                throw new InvalidOperationException(
                    "The sample line group was created but no corridor body sections were generated; stopping, to avoid a false success that only draws the corridor surface.");

            // * Refresh the section view group in a second transaction.
            // Reason: when "create views / import label set / update layout" are crammed into one transaction,
            // Civil 3D has not finished updating object dependencies and labels do not render. Commit first, then start another transaction.
            // (2026-07-28, from the "recommended stable plotting order" section of the user's troubleshooting notes)
            int layoutUpdated = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                CivSampleLineGroup slg2 = FindSampleLineGroup(tr, civ, alName, groupName);
                foreach (Autodesk.Civil.DatabaseServices.SectionViewGroup g in slg2.SectionViewGroups)
                {
                    try
                    {
                        var m = g.GetType().GetMethod("UpdateLayout", Type.EmptyTypes);
                        if (m != null) { m.Invoke(g, null); layoutUpdated++; }
                    }
                    catch (System.Exception ex)
                    { creationNote += " UpdateLayout failed: " + ex.GetType().Name + ": " + Truncate(ex.Message, 80) + ";"; }
                }
                tr.Commit();
            }

            // * Explicitly create "corridor point label groups".
            // Section views have a global switch eSectionViewCorridorPointLabelOption (corridor point code labelling method:
            // Section Label Set / Code Set Style); programmatic creation leaves it at Section Label Set,
            // which **suppresses only the Point code set labels, not the Links** -- exactly why "link labels present, point labels all missing".
            // The enum has no readable/writable member in the managed API, so it cannot be set; but it can be bypassed:
            // create a SectionCorridorPointLabelGroup directly for "every section view x every section",
            // equivalent to selecting the section in the UI -> right-click Edit labels -> add corridor point labels.
            // (2026-07-28, verified along this line after the user proposed the switch hypothesis)
            // **Not created** by default: in practice it takes the Label Set branch and can only use styles from CorridorPointLabelStyles
            // (this drawing has only Standard), producing generic "Subassembly Point Elevation/Offset" text,
            // not the code set labels. The Code Set branch is what we want. Kept as an option, enable explicitly when needed.
            int pointLabelGroups = 0;
            if (GetBool(a, "corridor_point_labels", false))
            using (var tr = db.TransactionManager.StartTransaction())
            {
                CivSampleLineGroup slg3 = FindSampleLineGroup(tr, civ, alName, groupName);
                foreach (ObjectId slId in slg3.GetSampleLineIds())
                {
                    var sl = (CivSampleLine)tr.GetObject(slId, OpenMode.ForRead);
                    foreach (ObjectId svId in sl.GetSectionViewIds())
                        foreach (ObjectId secId in sl.GetSectionIds())
                        {
                            try
                            {
                                // Create directly, without pre-filtering with GetAvailableLabelGroupIds --
                                // that method returns **existing** groups, not "creatable" ones,
                                // so using it as a guard blocks every Create (measured 0, and no exception).
#if NET472
                                throw new NotSupportedException("SectionCorridorPointLabelGroup needs Civil 3D 2023 or newer.");
#else
                                Autodesk.Civil.DatabaseServices.SectionCorridorPointLabelGroup
                                    .Create(svId, secId);
                                pointLabelGroups++;
#endif
                            }
                            catch (System.Exception ex)
                            {
                                if (creationNote.Length < 300)
                                    creationNote += " creating corridor point label group failed: " + ex.GetType().Name + ": "
                                                  + Truncate(ex.Message, 60) + ";";
                            }
                        }
                }
                tr.Commit();
            }

            // Grid placement: sorted by station, column-major, groups continue downwards
            using (var tr = db.TransactionManager.StartTransaction())
            {
                CivSampleLineGroup slg = FindSampleLineGroup(tr, civ, alName, groupName);
                var views = new List<KeyValuePair<double, ObjectId>>();
                foreach (ObjectId slId in slg.GetSampleLineIds())
                {
                    var sl = (CivSampleLine)tr.GetObject(slId, OpenMode.ForRead);
                    foreach (ObjectId svId in sl.GetSectionViewIds())
                        views.Add(new KeyValuePair<double, ObjectId>(sl.Station, svId));
                }
                views.Sort((p, q) => p.Key.CompareTo(q.Key));

                if (rows < 1) rows = 1;
                if (cols < 1) cols = 1;
                int perGroup = rows * cols;
                for (int i = 0; i < views.Count; i++)
                {
                    int grp = i / perGroup, g = i % perGroup;
                    int col = g / rows;
                    int row = grp * rows + (g % rows);
                    var target = new Point3d(
                        basePoint.X + col * colSpacing,
                        basePoint.Y - row * rowSpacing - grp * groupSpacing,
                        0);
                    var sv = (Autodesk.Civil.DatabaseServices.SectionView)tr.GetObject(views[i].Value, OpenMode.ForWrite);
                    sv.TransformBy(Matrix3d.Displacement(target - sv.Location));
                }
                tr.Commit();
            }

            // Volume tables (one per view; requires a computed material list, skipped if missing without affecting the views)
            int tables = 0;
            string tableNote = "not requested";
            if (wantVolTable)
            {
                // ! Volume tables must be **one view per transaction**, and the VolumeTables wrapper object must be released after use.
                // Cramming them all into one transaction works, but leaves unclosed write handles,
                // and save_dwg then throws eWasOpenForWrite (measured: disabling volume tables makes saving work).
                Guid mlGuid = Guid.Empty;
                var svIds = new List<ObjectId>();
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    CivSampleLineGroup slg = FindSampleLineGroup(tr, civ, alName, groupName);
                    foreach (CivQtoMaterialList ml in slg.MaterialLists) { mlGuid = ml.Guid; break; }
                    foreach (ObjectId slId in slg.GetSampleLineIds())
                    {
                        var sl = (CivSampleLine)tr.GetObject(slId, OpenMode.ForRead);
                        foreach (ObjectId svId in sl.GetSectionViewIds()) svIds.Add(svId);
                    }
                    tr.Commit();
                }

                if (mlGuid == Guid.Empty) tableNote = "the group has no material list, skipped (run compute_quantities first)";
                else
                {
                    string firstErr = null;
                    foreach (ObjectId svId in svIds)
                    {
                        using (var tr = db.TransactionManager.StartTransaction())
                        {
                            try
                            {
                                var sv = (Autodesk.Civil.DatabaseServices.SectionView)
                                         tr.GetObject(svId, OpenMode.ForWrite);   // ForRead crashes the process outright
                                var vt = sv.VolumeTables;
                                vt.SectionViewAnchorType =
                                    Autodesk.Civil.DatabaseServices.SectionViewVolumeTableAnchorType.TopRight;
                                vt.TableAnchorType =
                                    Autodesk.Civil.DatabaseServices.SectionViewVolumeTableAnchorType.TopLeft;
                                vt.OffsetX = 5;
                                vt.OffsetY = 0;
                                // CreateVolumeTable returns the ObjectId of the new table: it comes back **open for write**
                                // and stays that way unless closed explicitly -- save_dwg throws eWasOpenForWrite,
                                // and the saved DWG opens with ErrorStatus=434 (measured).
                                // One view per transaction does not solve this, because the handle is not owned by this transaction.
                                ObjectId vtId = vt.CreateVolumeTable(
                                    Autodesk.Civil.DatabaseServices.VolumeTableType.TotalVolume, mlGuid);
                                if (!vtId.IsNull)
                                {
                                    try
                                    {
                                        DBObject tbl = vtId.Open(OpenMode.ForWrite);
                                        tbl.Close();          // close the write handle Civil handed back
                                    }
                                    catch (System.Exception) { }
                                }
                                tables++;
                                tr.Commit();
                            }
                            catch (System.Exception ex)
                            {
                                if (firstErr == null) firstErr = ex.Message;
                                tr.Abort();
                            }
                        }
                    }
                    tableNote = firstErr == null ? "all succeeded" : ("first failure: " + Truncate(firstErr, 80));

                    // ! After the volume tables are built, close the section views' write handles one by one.
                    // The sv.VolumeTables wrapper leaves the SectionView **still open for write** after the transaction commits;
                    // without closing, save_dwg throws eWasOpenForWrite and the saved DWG opens with ErrorStatus=434.
                    // "One view per transaction" alone does not stop it -- the handle is not owned by the transaction, it must be reclaimed with old-style Open/Close.
                    int reclaimed = 0;
                    foreach (ObjectId svId in svIds)
                    {
                        try
                        {
                            DBObject o = svId.Open(OpenMode.ForWrite);
                            o.Close();
                            reclaimed++;
                        }
                        catch (System.Exception) { }
                    }
                    tableNote += "; reclaimed write handles " + reclaimed + "/" + svIds.Count;
                }
            }

            return new JsonObject
            {
                ["section_views"] = count,
                ["old_erased"] = erased,
                ["grid"] = rows + " rows x " + cols + " cols/group, col spacing " + colSpacing
                         + " row spacing " + rowSpacing + " group spacing " + groupSpacing,
                ["base_point"] = Math.Round(bx, 3) + ", " + Math.Round(by, 3),
                ["elev_range"] = elevMin < elevMax ? (elevMin + " ~ " + elevMax) : "auto",
                ["offset_range"] = "L " + offLeft + " / R " + offRight,
                ["code_set_applied_sections"] = secStyled,
                ["corridor_body_sections"] = corridorBodySections,
                ["corridor_geometry_check"] = "generated per old console baseline; not judged by Section envelope values",
                ["code_set_mapping"] = codeMappingResult,
                ["corridor_surface_sections"] = corridorSurfaceSections,
                ["corridor_display_overrides"] = corridorDisplayOverrides,
                ["surface_sections_styled"] = surfStyled,
                ["creation_mode"] = creationMode,
                ["placement"] = placement,
                ["material_shape_styles_set"] = materialShapeStylesSet,
                ["material_sections_styled"] = materialSectionsStyled,
                ["template"] = templateFile,
                ["layout"] = layoutName,
                ["group_plot_style"] = groupPlotStyle,
                ["layouts_in_drawing"] = CountLayouts(db),
                ["creation_note"] = creationNote,
                ["layout_updated_groups"] = layoutUpdated,
                ["corridor_code_set_before"] = corridorCodeSetBefore,
                ["corridor_code_set_applied"] = corridorCodeSetSet,
                ["corridor_point_label_groups"] = pointLabelGroups,
                ["volume_tables"] = tables,
                ["volume_table_note"] = tableNote
            };
        }

        static int CountLayouts(Database db)
        {
            int n = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry e in dict) if (e.Key != "Model") n++;
                tr.Commit();
            }
            return n;
        }

        static CivSampleLineGroup FindSampleLineGroup(Transaction tr, CivDoc civ, string alName, string groupName)
        {
            CivAlignment al = FindAlignment(tr, civ, alName);
            if (al == null) return null;
            foreach (ObjectId gid in al.GetSampleLineGroupIds())
            {
                var g = (CivSampleLineGroup)tr.GetObject(gid, OpenMode.ForRead);
                if (g.Name == groupName) return g;
            }
            return null;
        }

        // ===================== Plot prerequisite: model space scale =====================
        // Civil 3D's "model space scale" is the annotation scale CANNOSCALE: labels, symbols and sheet frame sizes all follow it.
        // Pin it before plotting (e.g. 1:500); frame sizes and section view grid spacing later depend on it.

        static JsonNode SetScale(JsonObject a, Document doc)
        {
            double drawingUnits = GetDouble(a, "scale", 0);      // pass 500 for 1:500
            string scaleName = GetString(a, "name", null);
            if (drawingUnits <= 0 && string.IsNullOrEmpty(scaleName))
                throw new InvalidOperationException("Give scale (500 for 1:500) or name (scale name, e.g. \"1:500\").");
            if (string.IsNullOrEmpty(scaleName)) scaleName = "1:" + drawingUnits.ToString("0.###");

            Database db = doc.Database;
            var ocm = db.ObjectContextManager;
            var coll = ocm.GetContextCollection("ACDB_ANNOTATIONSCALES");
            if (coll == null) throw new InvalidOperationException("The drawing has no annotation scale collection.");

            var available = new JsonArray();
            foreach (Autodesk.AutoCAD.DatabaseServices.ObjectContext c in coll)
                available.Add(c.Name);

            bool created = false;
            var ctx = coll.GetContext(scaleName) as AnnotationScale;
            if (ctx == null)
            {
                if (drawingUnits <= 0)
                    throw new InvalidOperationException("The drawing has no scale '" + scaleName + "'; to create it also give scale.");
                var sc = new AnnotationScale
                {
                    Name = scaleName,
                    PaperUnits = 1.0,
                    DrawingUnits = drawingUnits
                };
                coll.AddContext(sc);
                created = true;
                ctx = coll.GetContext(scaleName) as AnnotationScale;
                available.Add(scaleName);
            }
            if (ctx == null) throw new InvalidOperationException("Scale '" + scaleName + "' was created but cannot be retrieved.");

            db.Cannoscale = ctx;

            // Civil 3D labels (labels and bands of section/profile views) **ignore CANNOSCALE**;
            // they scale by "Drawing Settings -> Units and Zone -> Scale". Setting only CANNOSCALE leaves text sizes unchanged
            // -- measured with four variants (unset / scale:500 / name half-width / name full-width, including setting before creating section views):
            // text sizes identical, only the scale annotation follows CANNOSCALE. So both must be set.
            JsonNode civilBefore = null, civilAfter = null;
            string civilNote = null;
            try
            {
                CivDoc civ = Civ(db);
                var uz = civ.Settings.DrawingSettings.UnitZoneSettings;
                civilBefore = uz.DrawingScale;
                // Metric sheets: annotation scale 1:500 is stored as "paper 1 : drawing 0.5", and Civil's drawing scale also wants 0.5 (not 500).
                // Measured on project A's recipe: 0.5 -> annotation "H 1:500"; 500 -> annotation "1:500000" and every label text x1000
                // (regression on 2026-09-05 hit all three drawing types). Default to the same ratio as the annotation scale; pass drawing_scale explicitly for anything else.
                double want = ctx.PaperUnits > 0 ? ctx.DrawingUnits / ctx.PaperUnits : 0;
                double target = GetDouble(a, "drawing_scale", want);
                if (target > 0)
                {
                    uz.DrawingScale = target;
                    civilAfter = uz.DrawingScale;
                }
                else civilNote = "cannot derive the Civil drawing scale; pass drawing_scale explicitly";
            }
            catch (System.Exception ex)
            { civilNote = "Civil drawing scale not set: " + ex.GetType().Name + ": " + Truncate(ex.Message, 120); }

            return new JsonObject
            {
                ["scale"] = ctx.Name,
                ["paper_units"] = ctx.PaperUnits,
                ["drawing_units"] = ctx.DrawingUnits,
                ["created"] = created,
                ["civil_drawing_scale_before"] = civilBefore,
                ["civil_drawing_scale_after"] = civilAfter,
                ["civil_note"] = civilNote,
                ["available"] = available
            };
        }

        // ===================== Plot post-step: export Civil objects to plain CAD =====================
        // Civil 3D objects (alignments/section views/surfaces) are proxies on other machines and in other software, and plot unreliably.
        // `-EXPORTTOAUTOCAD` explodes them into plain CAD entities and saves as a new file (the original is untouched).
        // Note: the year-suffixed AECEXPORTTOAUTOCAD20xx exists only in the full GUI; in acc the hyphenated command must be used.

        static JsonNode ExportToAutocad(JsonObject a, Document doc)
        {
            string outPath = GetString(a, "out", null);
            if (string.IsNullOrEmpty(outPath))
                throw new InvalidOperationException("Missing parameter out (path of the exported plain CAD dwg).");
            if (!Path.IsPathRooted(outPath))
                throw new InvalidOperationException("out must be an absolute path: " + outPath);
            if (File.Exists(outPath) && !GetBool(a, "overwrite", false))
                throw new InvalidOperationException("File already exists, refusing to overwrite: " + outPath + " (pass overwrite:true to overwrite)");

            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(outPath)) File.Delete(outPath);   // the command does not overwrite by itself, clear first

            string version = GetString(a, "version", "2018");   // 2010 turns unexploded AEC objects into proxies with a warning
            var ed = doc.Editor;

            // Some AEC objects (confirmed: section QTO volume tables) make -EXPORTTOAUTOCAD fail internally with
            // erase failed (eLockViolation) and abort entirely. For plots with tables: after save_dwg has written the file
            // (keeping live tables in the base drawing), explode those classes in place into plain entities within the same session, then export.
            // explode_classes takes an array of DXF class names (e.g. AECC_SECTION_VIEW_QUANTITY_TAKEOFF_TABLE).
            int exploded = 0, explodeFailed = 0;
            var explodeClasses = a["explode_classes"] as JsonArray;
            if (explodeClasses != null && explodeClasses.Count > 0)
            {
                Database dbx = doc.Database;
                var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var n in explodeClasses) want.Add(n.ToString());
                var targets = new List<ObjectId>();
                using (var tr = dbx.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(dbx.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                        if (want.Contains(id.ObjectClass.DxfName)) targets.Add(id);
                    tr.Commit();
                }
                foreach (ObjectId tid in targets)
                {
                    using (var tr = dbx.TransactionManager.StartTransaction())
                    {
                        try
                        {
                            var ent = (Entity)tr.GetObject(tid, OpenMode.ForWrite);
                            var parts = new DBObjectCollection();
                            ent.Explode(parts);
                            var bt2 = (BlockTable)tr.GetObject(dbx.BlockTableId, OpenMode.ForRead);
                            var msr = (BlockTableRecord)tr.GetObject(bt2[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                            foreach (DBObject p in parts)
                            {
                                var pe = p as Entity;
                                if (pe == null) { p.Dispose(); continue; }
                                msr.AppendEntity(pe);
                                tr.AddNewlyCreatedDBObject(pe, true);
                            }
                            ent.Erase();
                            exploded++;
                            tr.Commit();
                        }
                        catch { explodeFailed++; tr.Abort(); }
                    }
                }
            }

            // The command-line path must use forward slashes; backslashes are treated as escapes in the command stream.
            // Prompt sequence (recorded 2026-07-21):
            //   Export options [Format\Bind\...] <Enter for filename>:   <- this step needs an empty Enter to reach the file name
            //   Export drawing name <default>:
            // Without that empty Enter the command treats the path as an invalid option and keeps re-prompting -> headless hangs forever (hit in this run).
            string cmdPath = outPath.Replace('\\', '/');
            if (string.Equals(version, "2018", StringComparison.OrdinalIgnoreCase))
                ed.Command("_-EXPORTTOAUTOCAD", "", cmdPath);          // default format, Enter straight to the file name
            else
                ed.Command("_-EXPORTTOAUTOCAD", "_F", version, "", cmdPath);

            if (!File.Exists(outPath))
                throw new InvalidOperationException("The command finished but no file was produced: " + outPath +
                    " (the prompt sequence may differ from expected; run -EXPORTTOAUTOCAD manually in acc once to see the prompts)");

            return new JsonObject
            {
                ["exported"] = outPath,
                ["bytes"] = new FileInfo(outPath).Length,
                ["version"] = version,
                ["pre_exploded"] = exploded,
                ["pre_explode_failed"] = explodeFailed,
                ["source"] = SafeFile(doc.Database)
            };
        }

        // Attach material volume tables to **existing** section views (without rebuilding the views). Why a separate node:
        // (1) when tables are attached at view creation and arrange then moves the views, headless anchoring does not follow, and tables stay off-page at the old spot;
        //    the correct order = arrange first, attach tables after; this node is that "attach after" step.
        // (2) create_section_views deletes and rebuilds, destroying the layout / point labels; this node touches tables only.
        // clear_all=true first deletes every section QTO table in model space (pass once on the first alignment of a chain, to avoid stale tables).
        static JsonNode RunNodeAddVolumeTables(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string groupName = GetString(a, "group", null);
            bool clearAll = GetBool(a, "clear_all", false);
            bool create = GetBool(a, "create", true);      // false = clear only, no tables (the unload entry when AEC tables cannot be plotted)
            double offX = GetDouble(a, "offset_x", 5);
            double offY = GetDouble(a, "offset_y", 0);

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            int cleared = 0;
            if (clearAll)
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    var kill = new List<ObjectId>();
                    foreach (ObjectId id in ms)
                        if (id.ObjectClass.DxfName == "AECC_SECTION_VIEW_QUANTITY_TAKEOFF_TABLE")
                            kill.Add(id);
                    foreach (ObjectId id in kill)
                    {
                        try
                        {
                            var o = tr.GetObject(id, OpenMode.ForWrite);
                            o.Erase();
                            cleared++;
                        }
                        catch { }
                    }
                    tr.Commit();
                }
            }

            Guid mlGuid = Guid.Empty;
            var svIds = new List<ObjectId>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                // Group name convention same as create_section_views: default <alignment>_SampleLines,
                // and if not found and the alignment has exactly one group, use it.
                CivSampleLineGroup slg = FindSampleLineGroup(
                    tr, civ, alName, string.IsNullOrEmpty(groupName) ? alName + "_SampleLines" : groupName);
                if (slg == null && string.IsNullOrEmpty(groupName))
                {
                    CivAlignment al0 = FindAlignment(tr, civ, alName);
                    if (al0 != null)
                    {
                        var gids = al0.GetSampleLineGroupIds();
                        if (gids.Count == 1)
                            slg = (CivSampleLineGroup)tr.GetObject(gids[0], OpenMode.ForRead);
                    }
                }
                if (slg == null)
                    throw new InvalidOperationException(
                        "Alignment '" + alName + "' has no sample line group"
                        + (string.IsNullOrEmpty(groupName) ? " (default name " + alName + "_SampleLines)" : " '" + groupName + "'")
                        + ".");
                foreach (CivQtoMaterialList ml in slg.MaterialLists) { mlGuid = ml.Guid; break; }
                foreach (ObjectId slId in slg.GetSampleLineIds())
                {
                    var sl = (CivSampleLine)tr.GetObject(slId, OpenMode.ForRead);
                    foreach (ObjectId svId in sl.GetSectionViewIds()) svIds.Add(svId);
                }
                tr.Commit();
            }
            if (mlGuid == Guid.Empty)
                throw new InvalidOperationException(
                    "The sample line group of alignment '" + alName + "' has no material list; run compute_quantities first.");
            if (!create)
                return new JsonObject
                {
                    ["alignment"] = alName,
                    ["tables_created"] = 0,
                    ["section_views"] = svIds.Count,
                    ["cleared_old_tables"] = cleared,
                    ["note"] = "clear only (create=false)"
                };

            int made = 0;
            string firstErr = null;
            foreach (ObjectId svId in svIds)
            {
                // ! One view per transaction; ForRead crashes the process outright (lesson from create_section_views)
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    try
                    {
                        var sv = (Autodesk.Civil.DatabaseServices.SectionView)
                                 tr.GetObject(svId, OpenMode.ForWrite);
                        var vt = sv.VolumeTables;
                        vt.SectionViewAnchorType =
                            Autodesk.Civil.DatabaseServices.SectionViewVolumeTableAnchorType.TopRight;
                        vt.TableAnchorType =
                            Autodesk.Civil.DatabaseServices.SectionViewVolumeTableAnchorType.TopLeft;
                        vt.OffsetX = offX;
                        vt.OffsetY = offY;
                        vt.CreateVolumeTable(
                            Autodesk.Civil.DatabaseServices.VolumeTableType.TotalVolume, mlGuid);
                        made++;
                        tr.Commit();
                    }
                    catch (System.Exception ex)
                    {
                        if (firstErr == null) firstErr = ex.Message;
                        tr.Abort();
                    }
                }
            }

            return new JsonObject
            {
                ["alignment"] = alName,
                ["tables_created"] = made,
                ["section_views"] = svIds.Count,
                ["cleared_old_tables"] = cleared,
                ["note"] = firstErr == null ? "all succeeded" : ("first failure: " + Truncate(firstErr, 80))
            };
        }

        // Self-drawn section volume tables (plain CAD lines + text, not AEC tables). Why not Civil's QTO tables:
        // AECC section QTO tables make -EXPORTTOAUTOCAD fail internally with erase failed (eLockViolation) and abort entirely,
        // and in accore the managed Explode returns an empty set while native EXPLODE refuses -- they simply cannot leave a headless chain.
        // The dyke demolition chain already labels volumes on sections (compute_embankment_demolition); this node uses the same idea.
        // Data source same as export_quantities: incremental/cumulative cut per station from the material list.
        // Entities carry XData (C3DF_SVT) for identification; clear=true removes old tables first on re-run, idempotent.
        const string SvtRegApp = "C3DF_SVT";

        static JsonNode RunNodeDrawSectionVolumeTables(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string groupName = GetString(a, "group", null);
            double scale = GetDouble(a, "scale", 500);
            double k = scale / 1000.0;                       // mm -> model metres
            double textH = GetDouble(a, "text_mm", 2.5) * k;
            double rowH = GetDouble(a, "row_mm", 5.0) * k;
            double col1 = GetDouble(a, "col1_mm", GetDouble(a, "label_col_mm", 16.0)) * k;   // item column
            double col2 = GetDouble(a, "col2_mm", 30.0) * k;                                  // section area column
            double col3 = GetDouble(a, "col3_mm", 26.0) * k;                                  // cut volume column
            double offX = GetDouble(a, "offset_x_mm", 2.0) * k;
            double offY = GetDouble(a, "offset_y_mm", 0.0) * k;
            bool clear = GetBool(a, "clear", true);
            string layerName = GetString(a, "layer", "C3DF-VolumeTable");
            string targetDwg = GetString(a, "target_dwg", null);
            string textStyleName = GetString(a, "text_style", null);
            string stationPrefix = GetString(a, "station_prefix", "Sta ");
            string headArea = GetString(a, "header_area", "Section area (m²)");
            string headVol = GetString(a, "header_volume", "Cut volume (m³)");
            string headItem = GetString(a, "header_item", "Item");
            string rowLabel = GetString(a, "row_label", "Cut");
            int limit = (int)GetDouble(a, "limit", 0);
            bool dryRun = GetBool(a, "dry_run", false);

            if (!string.IsNullOrEmpty(targetDwg))
            {
                if (!Path.IsPathRooted(targetDwg))
                    throw new InvalidOperationException("target_dwg must be an absolute path: " + targetDwg);
                if (!File.Exists(targetDwg))
                    throw new InvalidOperationException("Target drawing not found: " + targetDwg);
            }

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            // ---------- Phase A: read data and positions from the Civil drawing (read-only) ----------
            // station -> { cut section area m², incremental cut m³ }
            var dataByStation = new List<KeyValuePair<double, double[]>>();
            var views = new List<KeyValuePair<double, ObjectId>>();
            bool areaFromApi = true;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                CivSampleLineGroup slg = FindSampleLineGroup(
                    tr, civ, alName, string.IsNullOrEmpty(groupName) ? alName + "_SampleLines" : groupName);
                if (slg == null && string.IsNullOrEmpty(groupName))
                {
                    CivAlignment al0 = FindAlignment(tr, civ, alName);
                    if (al0 != null)
                    {
                        var gids = al0.GetSampleLineGroupIds();
                        if (gids.Count == 1)
                            slg = (CivSampleLineGroup)tr.GetObject(gids[0], OpenMode.ForRead);
                    }
                }
                if (slg == null)
                    throw new InvalidOperationException("Alignment '" + alName + "' has no sample line group.");
                Guid mlGuid = Guid.Empty;
                foreach (CivQtoMaterialList ml in slg.MaterialLists) { mlGuid = ml.Guid; break; }
                if (mlGuid == Guid.Empty)
                    throw new InvalidOperationException(
                        "The sample line group of alignment '" + alName + "' has no material list; run compute_quantities first.");

                var result = slg.GetTotalVolumeResultDataForMaterialList(mlGuid);
                foreach (CivQtoSectionalResult sec in result.GetResultsAlongSampleLines())
                {
                    double area = double.NaN;
                    try { area = sec.AreaResult.CutArea; }
                    catch { areaFromApi = false; }
                    dataByStation.Add(new KeyValuePair<double, double[]>(
                        sec.Station, new[] { area, sec.VolumeResult.IncrementalCutVolume }));
                }
                foreach (ObjectId slId in slg.GetSampleLineIds())
                {
                    var sl = (CivSampleLine)tr.GetObject(slId, OpenMode.ForRead);
                    foreach (ObjectId svId in sl.GetSectionViewIds())
                        views.Add(new KeyValuePair<double, ObjectId>(sl.Station, svId));
                }
                tr.Commit();
            }

            // One row per table: { table left x, table top y, station, area, volume }
            var tables = new List<double[]>();
            int noData = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var kv in views)
                {
                    double station = kv.Key;
                    double area = double.NaN, vol = double.NaN;
                    foreach (var vv in dataByStation)
                        if (Math.Abs(vv.Key - station) < 0.01) { area = vv.Value[0]; vol = vv.Value[1]; break; }
                    if (double.IsNaN(vol)) { noData++; continue; }
                    var sv = (Autodesk.Civil.DatabaseServices.SectionView)
                             tr.GetObject(kv.Value, OpenMode.ForRead);
                    Extents3d ext = sv.GeometricExtents;
                    tables.Add(new[] { ext.MaxPoint.X + offX, ext.MaxPoint.Y + offY, station, area, vol });
                }
                tr.Commit();
            }
            tables.Sort((p, q) => p[2].CompareTo(q[2]));
            int found = tables.Count;
            if (limit > 0 && tables.Count > limit) tables = tables.GetRange(0, limit);

            var sample = new JsonArray();
            for (int i = 0; i < tables.Count && i < 3; i++)
                sample.Add(new JsonObject
                {
                    ["station"] = tables[i][2],
                    ["x"] = Math.Round(tables[i][0], 3),
                    ["y_top"] = Math.Round(tables[i][1], 3),
                    ["area"] = double.IsNaN(tables[i][3]) ? (JsonNode)null : Math.Round(tables[i][3], 2),
                    ["volume"] = Math.Round(tables[i][4], 2)
                });

            if (dryRun)
                return new JsonObject
                {
                    ["alignment"] = alName,
                    ["mode"] = "dry_run (nothing drawn)",
                    ["section_views"] = views.Count,
                    ["tables_planned"] = tables.Count,
                    ["views_without_data"] = noData,
                    ["area_from_api"] = areaFromApi,
                    ["table_w"] = Math.Round(col1 + col2 + col3, 3),
                    ["table_h"] = Math.Round(3 * rowH, 3),
                    ["first"] = sample
                };

            // ---------- Phase B: draw ----------
            int made = 0, cleared = 0;
            string writtenTo, note = null;
            if (string.IsNullOrEmpty(targetDwg))
            {
                made = PaintSvtTables(db, tables, clear, layerName, textStyleName,
                                      textH, rowH, col1, col2, col3,
                                      stationPrefix, headItem, headArea, headVol, rowLabel, out cleared);
                writtenTo = "(current drawing in memory; save with save_dwg)";
            }
            else
            {
                using (Database tdb = new Database(false, true))
                {
                    tdb.ReadDwgFile(targetDwg, FileOpenMode.OpenForReadAndAllShare, true, null);
                    tdb.CloseInput(true);
                    made = PaintSvtTables(tdb, tables, clear, layerName, textStyleName,
                                          textH, rowH, col1, col2, col3,
                                          stationPrefix, headItem, headArea, headVol, rowLabel, out cleared);
                    writtenTo = targetDwg;
                    try
                    {
                        tdb.SaveAs(targetDwg, DwgVersion.Current);
                    }
                    catch (System.Exception ex)
                    {
                        // House rule: when the target is locked, only one "-locked-pending-replace" file may be left next to the original
                        string alt = Path.Combine(
                            Path.GetDirectoryName(targetDwg),
                            Path.GetFileNameWithoutExtension(targetDwg) + "-locked-pending-replace.dwg");
                        tdb.SaveAs(alt, DwgVersion.Current);
                        writtenTo = alt;
                        note = "Could not write the original drawing (" + Truncate(ex.Message, 60) + "); result saved to the -locked-pending-replace file, swap it in after closing the drawing.";
                    }
                }
            }

            return new JsonObject
            {
                ["alignment"] = alName,
                ["tables_drawn"] = made,
                ["tables_found"] = found,
                ["cleared"] = cleared,
                ["views_without_data"] = noData,
                ["area_from_api"] = areaFromApi,
                ["layer"] = layerName,
                ["written_to"] = writtenTo,
                ["first"] = sample,
                ["note"] = note ?? "all succeeded"
            };
        }

        // Draw the tables into model space of the given Database (current drawing or an external finished drawing both go through here).
        // Table layout (project B preliminary design, sheet 1201 final): three rows, first row spans all columns with the station, second is the header, third the data.
        //   ┌──────────────────────────────┐
        //   │          Sta 0+000.00        │
        //   ├──────┬────────────┬──────────┤
        //   │ Item │ Area (m²)  │ Cut (m³) │
        //   ├──────┼────────────┼──────────┤
        //   │ Cut  │    9.57    │   0.00   │
        //   └──────┴────────────┴──────────┘
        static int PaintSvtTables(Database tdb, List<double[]> tables, bool clear,
                                  string layerName, string textStyleName,
                                  double textH, double rowH, double col1, double col2, double col3,
                                  string stationPrefix, string headItem, string headArea, string headVol,
                                  string rowLabel, out int cleared)
        {
            // To create entities in an external Database, WorkingDatabase must be switched to it,
            // otherwise SetDatabaseDefaults takes the current drawing's defaults and AppendEntity throws eWrongDatabase.
            Database prevWorking = HostApplicationServices.WorkingDatabase;
            bool switched = !ReferenceEquals(prevWorking, tdb);
            if (switched) HostApplicationServices.WorkingDatabase = tdb;
            try
            {
                return PaintSvtTablesCore(tdb, tables, clear, layerName, textStyleName,
                                          textH, rowH, col1, col2, col3,
                                          stationPrefix, headItem, headArea, headVol, rowLabel, out cleared);
            }
            finally
            {
                if (switched) HostApplicationServices.WorkingDatabase = prevWorking;
            }
        }

        static int PaintSvtTablesCore(Database tdb, List<double[]> tables, bool clear,
                                  string layerName, string textStyleName,
                                  double textH, double rowH, double col1, double col2, double col3,
                                  string stationPrefix, string headItem, string headArea, string headVol,
                                  string rowLabel, out int cleared)
        {
            cleared = 0;

            // Layer + RegApp
            using (var tr = tdb.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(tdb.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(layerName))
                {
                    lt.UpgradeOpen();
                    var ltr = new LayerTableRecord { Name = layerName };
                    lt.Add(ltr);
                    tr.AddNewlyCreatedDBObject(ltr, true);
                }
                var rat = (RegAppTable)tr.GetObject(tdb.RegAppTableId, OpenMode.ForRead);
                if (!rat.Has(SvtRegApp))
                {
                    rat.UpgradeOpen();
                    var r = new RegAppTableRecord { Name = SvtRegApp };
                    rat.Add(r);
                    tr.AddNewlyCreatedDBObject(r, true);
                }
                tr.Commit();
            }

            if (clear)
            {
                using (var tr = tdb.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(tdb.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        Entity ent0;
                        try { ent0 = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                        catch { continue; }
                        if (ent0 == null) continue;
                        // Two identification rules: on this layer (the layer is dedicated to this node) or carrying C3DF_SVT XData.
                        // XData alone is not enough -- empty XData with only a RegApp name is dropped on save, and old tables could no longer be cleared.
                        bool mine = string.Equals(ent0.Layer, layerName, StringComparison.OrdinalIgnoreCase);
                        if (!mine && ent0.GetXDataForApplication(SvtRegApp) == null) continue;
                        ent0.UpgradeOpen();
                        ent0.Erase();
                        cleared++;
                    }
                    tr.Commit();
                }
            }

            int made = 0;
            double w = col1 + col2 + col3, h = 3 * rowH;
            using (var tr = tdb.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(tdb.BlockTableId, OpenMode.ForRead);
                var msr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var lt = (LayerTable)tr.GetObject(tdb.LayerTableId, OpenMode.ForRead);
                ObjectId layerId = lt[layerName];

                // Text style: the parameter when given (e.g. the -SimHei style left by the font normalisation chain), otherwise the current default
                ObjectId styleId = ObjectId.Null;
                var tst = (TextStyleTable)tr.GetObject(tdb.TextStyleTableId, OpenMode.ForRead);
                if (!string.IsNullOrEmpty(textStyleName))
                {
                    if (!tst.Has(textStyleName))
                        throw new InvalidOperationException("The drawing has no text style '" + textStyleName + "'.");
                    styleId = tst[textStyleName];
                }

                var xdata = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, SvtRegApp),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, "SectionVolumeTable"));

                Action<Entity> put = e =>
                {
                    e.SetDatabaseDefaults();
                    e.LayerId = layerId;
                    msr.AppendEntity(e);
                    tr.AddNewlyCreatedDBObject(e, true);
                    e.XData = xdata;
                };
                Action<string, double, double> txtMid = (s, cx, cy) =>
                {
                    var t = new DBText
                    {
                        TextString = s,
                        Height = textH,
                        Position = new Point3d(cx, cy, 0)
                    };
                    if (!styleId.IsNull) t.TextStyleId = styleId;
                    t.HorizontalMode = TextHorizontalMode.TextCenter;
                    t.VerticalMode = TextVerticalMode.TextVerticalMid;
                    t.AlignmentPoint = new Point3d(cx, cy, 0);
                    put(t);
                };

                foreach (var row in tables)
                {
                    double x0 = row[0], yTop = row[1], station = row[2], area = row[3], vol = row[4];
                    double y0 = yTop - h;
                    double yR1 = yTop - rowH;          // bottom of first row
                    double yR2 = yTop - 2 * rowH;      // bottom of header row

                    var pl = new Polyline(4) { Closed = true };
                    pl.AddVertexAt(0, new Point2d(x0, y0), 0, 0, 0);
                    pl.AddVertexAt(1, new Point2d(x0 + w, y0), 0, 0, 0);
                    pl.AddVertexAt(2, new Point2d(x0 + w, yTop), 0, 0, 0);
                    pl.AddVertexAt(3, new Point2d(x0, yTop), 0, 0, 0);
                    put(pl);
                    put(new Line(new Point3d(x0, yR1, 0), new Point3d(x0 + w, yR1, 0)));
                    put(new Line(new Point3d(x0, yR2, 0), new Point3d(x0 + w, yR2, 0)));
                    // Vertical lines span only the lower two rows; the first row is a merged cell
                    put(new Line(new Point3d(x0 + col1, y0, 0), new Point3d(x0 + col1, yR1, 0)));
                    put(new Line(new Point3d(x0 + col1 + col2, y0, 0), new Point3d(x0 + col1 + col2, yR1, 0)));

                    int km = (int)Math.Floor(station / 1000.0);
                    string stTxt = km.ToString() + "+" + (station - km * 1000.0).ToString("000.00");
                    double cx1 = x0 + col1 / 2;
                    double cx2 = x0 + col1 + col2 / 2;
                    double cx3 = x0 + col1 + col2 + col3 / 2;
                    txtMid(stationPrefix + stTxt, x0 + w / 2, yTop - rowH / 2);
                    txtMid(headItem, cx1, yR1 - rowH / 2);
                    txtMid(headArea, cx2, yR1 - rowH / 2);
                    txtMid(headVol, cx3, yR1 - rowH / 2);
                    txtMid(rowLabel, cx1, yR2 - rowH / 2);
                    txtMid(double.IsNaN(area) ? "—" : area.ToString("0.00"), cx2, yR2 - rowH / 2);
                    txtMid(vol.ToString("0.00"), cx3, yR2 - rowH / 2);
                    made++;
                }
                tr.Commit();
            }
            return made;
        }

        // ===================== Plot: batch insert sheet frames by scale =====================
        // Frame size in model space = paper size (mm) x scale denominator / 1000 (metres).
        // A3 landscape 420x297 at 1:500 -> 210 m x 148.5 m per sheet. So set_scale must run before inserting frames.
        // Idempotent: frames inserted by this op carry an XData tag; re-running deletes the previous ones instead of piling up.

        const string TitleBlockXdataApp = "C3DF_TITLEBLOCK";

        static JsonNode InsertTitleBlocks(JsonObject a, Document doc)
        {
            string blockName = Need(a, "block");
            string fromDwg = GetString(a, "from_dwg", null);
            int count = (int)GetDouble(a, "count", 1);
            if (count < 1) throw new InvalidOperationException("count must be >= 1.");
            int cols = (int)GetDouble(a, "cols", 1);
            if (cols < 1) cols = 1;
            double x0 = GetDouble(a, "x", 0), y0 = GetDouble(a, "y", 0);
            string layer = GetString(a, "layer", null);

            Database db = doc.Database;

            // Scale: defaults to the drawing's current annotation scale (the one set by set_scale)
            double scale = GetDouble(a, "scale", 0);
            string scaleFrom = "parameter";
            if (scale <= 0)
            {
                try
                {
                    var cs = db.Cannoscale;
                    if (cs != null && cs.PaperUnits > 0) { scale = cs.DrawingUnits / cs.PaperUnits; scaleFrom = "current drawing scale " + cs.Name; }
                }
                catch { }
            }
            if (scale <= 0) throw new InvalidOperationException("No scale available; run set_scale first or pass scale (500 for 1:500).");

            // Paper (mm): presets + custom
            double pw = GetDouble(a, "paper_w", 0), ph = GetDouble(a, "paper_h", 0);
            string paper = GetString(a, "paper", "A3");
            if (pw <= 0 || ph <= 0) PaperSizeMm(paper, out pw, out ph);   // unknown preset throws; custom sizes go through paper_w/paper_h
            // If the block is drawn in mm, the insertion scale is scale/1000 (drawing units are metres)
            double blockScale = GetDouble(a, "block_scale", scale / 1000.0);
            double frameW = pw * scale / 1000.0;
            double frameH = ph * scale / 1000.0;
            double gapX = GetDouble(a, "gap_x", 0), gapY = GetDouble(a, "gap_y", 0);

            var attrs = a["attributes"] as JsonObject;
            // Attribute width factors {tag:factor}: the usual trick for squeezing long text into narrow cells; overly long values get compressed
            var widthFactors = a["width_factors"] as JsonObject;
            JsonArray placed = null;
            // Classic AdjustAlignment pitfall: in a headless session, when WorkingDatabase is not this drawing,
            // non-left-aligned attributes are placed against the wrong base -- the "title/sheet number drifts right" bug. Pin it throughout, restore at the end.
            Database prevWdb = HostApplicationServices.WorkingDatabase;
            HostApplicationServices.WorkingDatabase = db;
            try {
            // at mode: explicit per-point positions (usually from arrange_section_sheets sheets[].origin_x/y),
            // ignoring the count/cols grid; after insertion the block's bounding-box lower-left corner is aligned to the given point (base point location does not matter).
            // Each point may carry its own attributes, overriding global ones of the same name -- that is how per-frame titles/page numbers are filled.
            var atArr = a["at"] as JsonArray;
            if (atArr != null && atArr.Count == 0)
                throw new InvalidOperationException("at was given but is an empty array; omit at to use grid mode.");
            int erased = 0, inserted = 0, attrsFilled = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                // Block definition: if missing from the drawing, clone one from the block library file
                if (!bt.Has(blockName))
                {
                    if (string.IsNullOrEmpty(fromDwg))
                        throw new InvalidOperationException("Block '" + blockName + "' not in the drawing; give from_dwg to import it from the block library (run list_blocks on the library first to find the name).");
                    if (!File.Exists(fromDwg)) throw new InvalidOperationException("Block library file not found: " + fromDwg);
                    using (var src = new Database(false, true))
                    {
                        src.ReadDwgFile(fromDwg, FileOpenMode.OpenForReadAndAllShare, true, null);
                        src.CloseInput(true);
                        using (var stx = src.TransactionManager.StartTransaction())
                        {
                            var sbt = (BlockTable)stx.GetObject(src.BlockTableId, OpenMode.ForRead);
                            if (!sbt.Has(blockName))
                                throw new InvalidOperationException("Block '" + blockName + "' not in the block library: " + fromDwg);
                            var ids = new ObjectIdCollection { sbt[blockName] };
                            var map = new IdMapping();
                            db.WblockCloneObjects(ids, db.BlockTableId, map, DuplicateRecordCloning.Replace, false);
                            stx.Commit();
                        }
                    }
                    bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    if (!bt.Has(blockName))
                        throw new InvalidOperationException("Block '" + blockName + "' still not found after cloning from the block library.");
                }
                ObjectId btrId = bt[blockName];

                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                EnsureRegApp(tr, db, TitleBlockXdataApp);

                // Idempotent: delete the frames inserted by the previous run of this op.
                // Two legs: XData tag + same-name block fallback (erase_same_block, on by default).
                // XData alone is not enough -- XData with only a RegAppName and no payload is gone after save and reopen,
                // old frames cannot be found and pile up (confirmed 2026-08-16: three runs on project B produced 174 frames).
                bool eraseSameName = GetBool(a, "erase_same_block", true);
                foreach (ObjectId id in ms)
                {
                    var old = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (old == null) continue;
                    bool marked = false;
                    var rb = old.GetXDataForApplication(TitleBlockXdataApp);
                    if (rb != null) { rb.Dispose(); marked = true; }
                    if (!marked && eraseSameName)
                    {
                        string bn = null;
                        try
                        {
                            var obtr = (BlockTableRecord)tr.GetObject(
                                old.DynamicBlockTableRecord.IsNull ? old.BlockTableRecord : old.DynamicBlockTableRecord,
                                OpenMode.ForRead);
                            bn = obtr.Name;
                        }
                        catch { }
                        if (!string.Equals(bn, blockName, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    else if (!marked) continue;
                    old.UpgradeOpen();
                    old.Erase();
                    erased++;
                }

                placed = new JsonArray();
                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                int total = atArr != null ? atArr.Count : count;

                // clone_from: deep-clone the original block references from a finished drawing (including instance-level hand-tuned attribute geometry),
                // copy per point, then erase the prototype. Attributes built from the definition are not identical (confirmed 2026-08-16 by comparing project B sheet 1301).
                var cloneFrom = a["clone_from"] as JsonObject;
                if (cloneFrom != null && atArr != null)
                {
                    string srcPath = cloneFrom["dwg"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath))
                        throw new InvalidOperationException("clone_from.dwg does not exist: " + srcPath);
                    ObjectId protoId;
                    using (var src = new Database(false, true))
                    {
                        src.ReadDwgFile(srcPath, FileOpenMode.OpenForReadAndAllShare, true, null);
                        src.CloseInput(true);
                        ObjectId found = ObjectId.Null;
                        using (var stx = src.TransactionManager.StartTransaction())
                        {
                            var sbt = (BlockTable)stx.GetObject(src.BlockTableId, OpenMode.ForRead);
                            foreach (ObjectId recId in sbt)
                            {
                                var rec = (BlockTableRecord)stx.GetObject(recId, OpenMode.ForRead);
                                if (!rec.IsLayout) continue;
                                foreach (ObjectId eid in rec)
                                {
                                    var brf = stx.GetObject(eid, OpenMode.ForRead) as BlockReference;
                                    if (brf == null) continue;
                                    string bn2 = null;
                                    try
                                    {
                                        var obtr2 = (BlockTableRecord)stx.GetObject(
                                            brf.DynamicBlockTableRecord.IsNull ? brf.BlockTableRecord : brf.DynamicBlockTableRecord,
                                            OpenMode.ForRead);
                                        bn2 = obtr2.Name;
                                    }
                                    catch { }
                                    if (string.Equals(bn2, blockName, StringComparison.OrdinalIgnoreCase)) { found = eid; break; }
                                }
                                if (!found.IsNull) break;
                            }
                            stx.Commit();
                        }
                        if (found.IsNull)
                            throw new InvalidOperationException("No reference of block '" + blockName + "' found in the clone_from drawing.");
                        var cids = new ObjectIdCollection { found };
                        var cmap = new IdMapping();
                        src.WblockCloneObjects(cids, ms.ObjectId, cmap, DuplicateRecordCloning.Replace, false);
                        protoId = cmap[found].Value;
                    }
                    var protoBr = (BlockReference)tr.GetObject(protoId, OpenMode.ForRead);
                    double srcScale = protoBr.ScaleFactors.X;
                    for (int i = 0; i < total; i++)
                    {
                        var it2 = atArr[i] as JsonObject;
                        var pos2 = new Point3d(it2["x"].GetValue<double>(), it2["y"].GetValue<double>(), 0);
                        var m2 = new IdMapping();
                        db.DeepCloneObjects(new ObjectIdCollection { protoId }, ms.ObjectId, m2, false);
                        var brC = (BlockReference)tr.GetObject(m2[protoId].Value, OpenMode.ForWrite);
                        double k = srcScale > 1e-12 ? blockScale / srcScale : blockScale;
                        brC.TransformBy(Matrix3d.Scaling(k, brC.Position));
                        Extents3d extC = brC.GeometricExtents;
                        brC.TransformBy(Matrix3d.Displacement(
                            new Vector3d(pos2.X - extC.MinPoint.X, pos2.Y - extC.MinPoint.Y, 0)));
                        brC.XData = new ResultBuffer(
                            new TypedValue((int)DxfCode.ExtendedDataRegAppName, TitleBlockXdataApp),
                            new TypedValue((int)DxfCode.ExtendedDataAsciiString, "C3DF"));
                        placed.Add(Math.Round(pos2.X, 3) + ", " + Math.Round(pos2.Y, 3));
                        inserted++;
                    }
                    ((Entity)tr.GetObject(protoId, OpenMode.ForWrite)).Erase();
                }
                else
                for (int i = 0; i < total; i++)
                {
                    Point3d pos;
                    JsonObject itemAttrs = null;
                    if (atArr != null)
                    {
                        var it = atArr[i] as JsonObject;
                        if (it == null || it["x"] == null || it["y"] == null)
                            throw new InvalidOperationException("at[" + i + "] is missing x/y.");
                        pos = new Point3d(it["x"].GetValue<double>(), it["y"].GetValue<double>(), 0);
                        itemAttrs = it["attributes"] as JsonObject;
                    }
                    else
                    {
                        int col = i % cols, row = i / cols;
                        pos = new Point3d(x0 + col * (frameW + gapX), y0 - row * (frameH + gapY), 0);
                    }
                    var br = new BlockReference(pos, btrId) { ScaleFactors = new Scale3d(blockScale) };
                    ms.AppendEntity(br);
                    tr.AddNewlyCreatedDBObject(br, true);
                    if (!string.IsNullOrEmpty(layer)) br.Layer = layer;
                    // The payload is mandatory: XData with only a RegAppName cannot be read back after save and reopen, and idempotent deletion goes blind
                    br.XData = new ResultBuffer(
                        new TypedValue((int)DxfCode.ExtendedDataRegAppName, TitleBlockXdataApp),
                        new TypedValue((int)DxfCode.ExtendedDataAsciiString, "C3DF"));

                    if (atArr != null)
                    {
                        // Alignment: given point = lower-left corner of the block's bounding box (measure geometry first, then move; attributes are not attached yet so do not interfere)
                        try
                        {
                            Extents3d ext = br.GeometricExtents;
                            var d = new Vector3d(pos.X - ext.MinPoint.X, pos.Y - ext.MinPoint.Y, 0);
                            if (d.Length > 1e-9) br.Position = br.Position + d;
                        }
                        catch { }
                    }

                    if (btr.HasAttributeDefinitions)
                    {
                        foreach (ObjectId eid in btr)
                        {
                            var ad = tr.GetObject(eid, OpenMode.ForRead) as AttributeDefinition;
                            if (ad == null || ad.Constant) continue;
                            var ar = new AttributeReference();
                            ar.SetAttributeFromBlock(ad, br.BlockTransform);
                            JsonNode v = itemAttrs != null && itemAttrs[ad.Tag] != null
                                ? itemAttrs[ad.Tag]
                                : (attrs != null ? attrs[ad.Tag] : null);
                            if (v != null)
                            {
                                // {n} in a value is replaced with the sheet number (starting at 1)
                                ar.TextString = v.ToString().Replace("{n}", (i + 1).ToString());
                                attrsFilled++;
                            }
                            if (widthFactors != null && widthFactors[ad.Tag] != null)
                            {
                                double wf = widthFactors[ad.Tag].GetValue<double>();
                                if (wf > 0) ar.WidthFactor = wf;
                                // Re-placement is needed only when the width changed; in a headless session, calling AdjustAlignment
                                // on an untouched centred attribute shifts it right by the wrong text width (the "right drift" culprit on project B, 2026-08-16)
                                try { ar.AdjustAlignment(db); } catch { }
                            }
                            br.AttributeCollection.AppendAttribute(ar);
                            tr.AddNewlyCreatedDBObject(ar, true);
                        }
                    }
                    placed.Add(Math.Round(br.Position.X, 3) + ", " + Math.Round(br.Position.Y, 3));
                    inserted++;
                }
                tr.Commit();
            }

            // ATTSYNC (API version): reset each attribute's position/format from the block definition (= ATTSYNC in the UI),
            // keeping filled values, then re-apply width_factors at the end -- sync resets widths to the definition values.
            // The _.ATTSYNC command is not used: its prompt sequence is unstable in a headless session, measured throwing eInvalidInput.
            bool wantAttsync = GetBool(a, "attsync", true);
            int widthReapplied = 0, attrsSynced = 0;
            if (wantAttsync && inserted > 0)
            {
                using (Transaction tr2 = db.TransactionManager.StartTransaction())
                {
                    var bt2 = (BlockTable)tr2.GetObject(db.BlockTableId, OpenMode.ForRead);
                    if (bt2.Has(blockName))
                    {
                        var defs = new Dictionary<string, AttributeDefinition>(StringComparer.OrdinalIgnoreCase);
                        var btr2 = (BlockTableRecord)tr2.GetObject(bt2[blockName], OpenMode.ForRead);
                        foreach (ObjectId eid in btr2)
                        {
                            var ad = tr2.GetObject(eid, OpenMode.ForRead) as AttributeDefinition;
                            if (ad != null && !ad.Constant) defs[ad.Tag] = ad;
                        }
                        var ms2 = (BlockTableRecord)tr2.GetObject(bt2[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                        foreach (ObjectId id in ms2)
                        {
                            var br2 = tr2.GetObject(id, OpenMode.ForRead) as BlockReference;
                            if (br2 == null) continue;
                            string bn = null;
                            try
                            {
                                var obtr = (BlockTableRecord)tr2.GetObject(
                                    br2.DynamicBlockTableRecord.IsNull ? br2.BlockTableRecord : br2.DynamicBlockTableRecord,
                                    OpenMode.ForRead);
                                bn = obtr.Name;
                            }
                            catch { }
                            if (!string.Equals(bn, blockName, StringComparison.OrdinalIgnoreCase)) continue;
                            foreach (ObjectId attId in br2.AttributeCollection)
                            {
                                var ar2 = tr2.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                                if (ar2 == null) continue;
                                AttributeDefinition ad2;
                                if (!defs.TryGetValue(ar2.Tag, out ad2)) continue;
                                ar2.UpgradeOpen();
                                string keep = ar2.TextString;
                                try
                                {
                                    ar2.SetAttributeFromBlock(ad2, br2.BlockTransform);
                                    ar2.TextString = keep;
                                    attrsSynced++;
                                }
                                catch { }
                                JsonNode wfNode = widthFactors != null ? widthFactors[ar2.Tag] : null;
                                if (wfNode != null)
                                {
                                    double wf2 = wfNode.GetValue<double>();
                                    if (wf2 > 0) { ar2.WidthFactor = wf2; widthReapplied++; }
                                }
                                try { ar2.AdjustAlignment(db); } catch { }
                            }
                        }
                    }
                    tr2.Commit();
                }
            }

            {

                return new JsonObject
                {
                    ["block"] = blockName,
                    ["mode"] = atArr != null ? "at (per point, aligned to bounding-box lower-left)" : "grid",
                    ["inserted"] = inserted,
                    ["old_erased"] = erased,
                    ["scale"] = "1:" + scale.ToString("0.###") + " (source: " + scaleFrom + ")",
                    ["paper"] = paper + " " + pw + "x" + ph + " mm",
                    ["frame_size_model"] = Math.Round(frameW, 3) + " x " + Math.Round(frameH, 3) + " m",
                    ["block_scale"] = blockScale,
                    ["attributes_filled"] = attrsFilled,
                    ["attsync"] = wantAttsync,
                    ["attsync_attrs"] = attrsSynced,
                    ["attsync_width_reapplied"] = widthReapplied,
                    ["positions"] = placed
                };
            }
            } finally { HostApplicationServices.WorkingDatabase = prevWdb; }
        }

        // ===================== Diagnostics: what is in the drawing =====================

        static JsonNode EntityStats(JsonObject a, Document doc)
        {
            string byWhat = GetString(a, "by", "type");     // type | layer
            int top = (int)GetDouble(a, "top", 30);
            Database db = doc.Database;
            var counts = new Dictionary<string, int>();
            int total = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    DBObject o;
                    try { o = tr.GetObject(id, OpenMode.ForRead); } catch { continue; }
                    total++;
                    string key;
                    if (byWhat == "layer") key = SafeLayer(o);
                    else key = o.GetType().Name;
                    int c;
                    counts.TryGetValue(key, out c);
                    counts[key] = c + 1;
                }
                tr.Commit();
            }

            var list = new List<KeyValuePair<string, int>>(counts);
            list.Sort((p, q) => q.Value.CompareTo(p.Value));
            var arr = new JsonArray();
            for (int i = 0; i < list.Count && i < top; i++)
                arr.Add(new JsonObject { [byWhat] = list[i].Key, ["count"] = list[i].Value });

            return new JsonObject { ["total_entities"] = total, ["group_by"] = byWhat, ["top"] = arr };
        }

        // ===================== Plot helper: turn all layers on =====================
        // The most common "blank sheet" in headless plotting: the objects' layer is off/frozen.
        // New Civil objects land on the project's own layers by LayerKey, and those layers may have been off in the original drawing.

        static JsonNode LayersOff(JsonObject a, Document doc)
        {
            var names = new List<string>();
            var arr = a["names"] as JsonArray;
            if (arr != null) foreach (JsonNode n in arr) names.Add(n.GetValue<string>());
            string one = GetString(a, "name", null);
            if (!string.IsNullOrEmpty(one)) names.Add(one);
            if (names.Count == 0) throw new InvalidOperationException("Give name or names[] (layer names).");

            Database db = doc.Database;
            var done = new JsonArray();
            var missing = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (string nm in names)
                {
                    if (!lt.Has(nm)) { missing.Add(nm); continue; }
                    ObjectId id = lt[nm];
                    if (id == db.Clayer) { missing.Add(nm + "(current layer, skipped)"); continue; }
                    var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite);
                    ltr.IsOff = true;
                    done.Add(nm);
                }
                tr.Commit();
            }
            return new JsonObject { ["turned_off"] = done, ["not_found"] = missing };
        }

        static JsonNode LayersOn(JsonObject a, Document doc)
        {
            Database db = doc.Database;
            var turnedOn = new JsonArray();
            var thawed = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in lt)
                {
                    var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    bool needOn = ltr.IsOff, needThaw = ltr.IsFrozen;
                    if (!needOn && !needThaw) continue;
                    // The current layer cannot be frozen; skip it to avoid an exception
                    if (needThaw && id == db.Clayer) needThaw = false;
                    ltr.UpgradeOpen();
                    if (needOn) { ltr.IsOff = false; turnedOn.Add(ltr.Name); }
                    if (needThaw) { ltr.IsFrozen = false; thawed.Add(ltr.Name); }
                }
                tr.Commit();
            }
            return new JsonObject
            {
                ["turned_on"] = turnedOn,
                ["thawed"] = thawed,
                ["note"] = "only the in-memory drawing is changed; nothing is written to disk without save_dwg"
            };
        }

        // ===================== 10. Save =====================

        // Some Civil APIs (confirmed: SectionViewVolumeTableGroup.CreateVolumeTable) hand back new objects
        // open for write and outside the caller's transaction; committing does not close them, SaveAs throws
        // eWasOpenForWrite and the written file is corrupt (ErrorStatus=434). Before saving, scan the whole database and
        // DowngradeOpen every object still open for write, so any handle a node forgot to close is caught here.
        // ! Must use a plain StartTransaction: OpenCloseTransaction throws eWasOpenForWrite outright on objects
        // already open for write elsewhere (the count is always 0); a plain transaction can attach to already-open objects.
        static int ReclaimWriteOpen(Database db)
        {
            int reclaimed = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId btrId in bt)
                {
                    BlockTableRecord btr;
                    try { btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead); }
                    catch { continue; }
                    foreach (ObjectId id in btr)
                    {
                        try
                        {
                            DBObject o = tr.GetObject(id, OpenMode.ForRead, false, true);
                            if (o != null && o.IsWriteEnabled) { o.DowngradeOpen(); reclaimed++; }
                        }
                        catch { }
                    }
                }
                tr.Commit();
            }
            return reclaimed;
        }


        static JsonNode SaveDwg(JsonObject a, Document doc)
        {
            Database db = doc.Database;
            int reclaimed = ReclaimWriteOpen(db);
            string host = SafeFile(db);
            bool apply = GetBool(a, "apply", false);
            string outPath = GetString(a, "out", null);

            if (apply) outPath = host;
            else if (string.IsNullOrEmpty(outPath))
            {
                string dir = Path.GetDirectoryName(host);
                string stem = Path.GetFileNameWithoutExtension(host);
                outPath = Path.Combine(dir, stem + "_out_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".dwg");
            }
            if (!Path.IsPathRooted(outPath))
                throw new InvalidOperationException("out must be an absolute path: " + outPath);
            if (!apply && string.Equals(Path.GetFullPath(outPath), Path.GetFullPath(host),
                                        StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("out points to the host drawing itself; pass apply:true explicitly to write back to the original.");
            if (File.Exists(outPath) && !apply && !GetBool(a, "overwrite", false))
                throw new InvalidOperationException("File already exists, refusing to overwrite: " + outPath + " (pass overwrite:true to overwrite)");

            string backup = null;
            if (apply && GetBool(a, "backup", true) && File.Exists(host))
            {
                backup = Path.Combine(Path.GetDirectoryName(host),
                    Path.GetFileNameWithoutExtension(host) + "_backup_" +
                    DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".dwg");
                File.Copy(host, backup, false);   // throw if the backup fails; do not proceed wounded
            }

            string dir2 = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir2) && !Directory.Exists(dir2)) Directory.CreateDirectory(dir2);
            bool replaceExisting = !apply && File.Exists(outPath) && GetBool(a, "overwrite", false);
            string requestedOutPath = outPath;
            bool fallbackBecauseLocked = false;
            string actualSavePath = outPath;
            if (replaceExisting)
            {
                actualSavePath = Path.Combine(
                    dir2,
                    Path.GetFileNameWithoutExtension(outPath) + ".__c3df_tmp_" +
                    Guid.NewGuid().ToString("N").Substring(0, 8) + ".dwg");
            }

            try
            {
                db.SaveAs(actualSavePath, DwgVersion.Current);
                if (replaceExisting)
                {
                    try
                    {
                        File.Replace(actualSavePath, outPath, null, true);
                    }
                    catch (IOException)
                    {
                        string fallback = Path.Combine(
                            dir2,
                            Path.GetFileNameWithoutExtension(outPath) + "_run_" +
                            DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".dwg");
                        File.Move(actualSavePath, fallback);
                        actualSavePath = fallback;
                        outPath = fallback;
                        fallbackBecauseLocked = true;
                    }
                }
            }
            finally
            {
                if (!string.Equals(actualSavePath, outPath, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(actualSavePath))
                {
                    try { File.Delete(actualSavePath); } catch { }
                }
            }

            return new JsonObject
            {
                ["mode"] = apply ? "written back to the host drawing" : "saved as a new file (host drawing on disk untouched)",
                ["output"] = outPath,
                ["requested_output"] = requestedOutPath,
                ["replaced_existing"] = replaceExisting,
                ["fallback_because_target_locked"] = fallbackBecauseLocked,
                ["backup"] = backup ?? "(no backup needed)",
                ["bytes"] = File.Exists(outPath) ? new FileInfo(outPath).Length : 0,
                ["write_handles_reclaimed"] = reclaimed
            };
        }

        // ===================== Diagnostics: does the surface/corridor actually have geometry =====================
        // The most common cause of "result is 0" is not a wrong computation but an upstream surface/corridor that is simply empty.
        // These two ops turn "is there anything there" into visible numbers.

        static JsonNode SurfaceStats(JsonObject a, Document doc)
        {
            string want = GetString(a, "name", null);
            Database db = doc.Database;
            var arr = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var s = tr.GetObject(id, OpenMode.ForRead) as CivSurface;
                    if (s == null) continue;
                    if (!string.IsNullOrEmpty(want) && s.Name != want) continue;

                    // ! Do not iterate Vertices/Triangles: headless it **crashes** the process (AccessViolation,
                    // uncatchable in .NET; the result file stops at ops:[] with nothing). Statistics always go through the property interface.
                    var o = new JsonObject { ["name"] = s.Name, ["type"] = s.GetType().Name };
                    o["general"] = InvokeAndDump(s, "GetGeneralProperties");
                    if (s is CivTinSurface) o["tin"] = InvokeAndDump(s, "GetTinProperties");
                    arr.Add(o);
                }
                tr.Commit();
            }
            if (arr.Count == 0) throw new InvalidOperationException("No matching surface.");
            return arr;
        }

        static JsonNode CorridorStats(JsonObject a, Document doc)
        {
            string want = GetString(a, "name", null);
            Database db = doc.Database;
            var arr = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var c = tr.GetObject(id, OpenMode.ForRead) as CivCorridor;
                    if (c == null) continue;
                    if (!string.IsNullOrEmpty(want) && c.Name != want) continue;

                    var o = new JsonObject { ["name"] = c.Name };
                    var codes = new JsonArray();
                    try { foreach (string s in c.GetLinkCodes()) codes.Add(s); } catch { }
                    o["link_codes"] = codes;

                    var pcodes = new JsonArray();
                    try { foreach (string s in c.GetPointCodes()) pcodes.Add(s); } catch { }
                    o["point_codes"] = pcodes;

                    var surfs = new JsonArray();
                    try
                    {
                        foreach (CivCorridorSurface cs in c.CorridorSurfaces)
                        {
                            var one = new JsonObject { ["name"] = cs.Name };
                            try
                            {
                                var ts = tr.GetObject(cs.SurfaceId, OpenMode.ForRead) as CivSurface;
                                if (ts != null) one["general"] = InvokeAndDump(ts, "GetGeneralProperties");
                            }
                            catch (System.Exception ex) { one["error"] = ex.GetType().Name; }
                            surfs.Add(one);
                        }
                    }
                    catch (System.Exception ex) { o["surface_error"] = ex.GetType().Name + ": " + ex.Message; }
                    o["corridor_surfaces"] = surfs;

                    // Baselines/regions require iterating managed wrapper objects, which risks a hard crash headless; not touched by default
                    if (GetBool(a, "deep", false))
                    {
                        var bls = new JsonArray();
                        try
                        {
                            foreach (Autodesk.Civil.DatabaseServices.Baseline b in c.Baselines)
                            {
                                var regions = new JsonArray();
                                foreach (Autodesk.Civil.DatabaseServices.BaselineRegion r in b.BaselineRegions)
                                    regions.Add(new JsonObject
                                    {
                                        ["name"] = r.Name,
                                        ["start"] = Math.Round(r.StartStation, 3),
                                        ["end"] = Math.Round(r.EndStation, 3)
                                    });
                                bls.Add(new JsonObject
                                {
                                    ["name"] = b.Name,
                                    ["start"] = SafeStr(() => Math.Round(b.StartStation, 3).ToString()),
                                    ["end"] = SafeStr(() => Math.Round(b.EndStation, 3).ToString()),
                                    ["regions"] = regions
                                });
                            }
                        }
                        catch (System.Exception ex) { o["baseline_error"] = ex.GetType().Name + ": " + ex.Message; }
                        o["baselines"] = bls;
                    }
                    arr.Add(o);
                }
                tr.Commit();
            }
            if (arr.Count == 0) throw new InvalidOperationException("No matching corridor.");
            return arr;
        }

        /// <summary>Read-only inventory of corridor targets: corridor -> baseline -> region -> the object each target slot points to.
        /// Curve targets (polylines/feature lines etc.) are also sampled along their length to measure the offset distribution relative to the baseline alignment --
        /// small spread = constant-offset segment (can be replaced by an offset alignment), large spread = widening segment (promote the line to an alignment before using it as a target).</summary>
        static JsonNode CorridorTargets(JsonObject a, Document doc)
        {
            string want = GetString(a, "name", null);
            double step = GetDouble(a, "sample_step", 10.0);
            int maxSamples = (int)GetDouble(a, "max_samples", 300);
            Database db = doc.Database;
            var arr = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var c = tr.GetObject(id, OpenMode.ForRead) as CivCorridor;
                    if (c == null) continue;
                    if (!string.IsNullOrEmpty(want) && c.Name != want) continue;
                    var co = new JsonObject { ["corridor"] = c.Name };
                    var bls = new JsonArray();
                    try
                    {
                        foreach (Autodesk.Civil.DatabaseServices.Baseline b in c.Baselines)
                        {
                            CivAlignment mainAl = null;
                            try { mainAl = tr.GetObject(b.AlignmentId, OpenMode.ForRead) as CivAlignment; } catch { }
                            var bo = new JsonObject
                            {
                                ["baseline"] = b.Name,
                                ["alignment"] = mainAl == null ? null : (JsonNode)mainAl.Name
                            };
                            var regs = new JsonArray();
                            foreach (Autodesk.Civil.DatabaseServices.BaselineRegion r in b.BaselineRegions)
                            {
                                var ro = new JsonObject
                                {
                                    ["region"] = r.Name,
                                    ["start"] = Math.Round(r.StartStation, 3),
                                    ["end"] = Math.Round(r.EndStation, 3)
                                };
                                var slots = new JsonArray();
                                try
                                {
                                    double rlo = Math.Min(r.StartStation, r.EndStation) - 1.0;
                                    double rhi = Math.Max(r.StartStation, r.EndStation) + 1.0;
                                    foreach (CivTargetInfo t in r.GetTargets())
                                        slots.Add(DumpTargetSlot(tr, t, mainAl, rlo, rhi, step, maxSamples));
                                }
                                catch (System.Exception ex) { ro["targets_error"] = ex.GetType().Name + ": " + ex.Message; }
                                ro["slots"] = slots;
                                regs.Add(ro);
                            }
                            bo["regions"] = regs;
                            bls.Add(bo);
                        }
                    }
                    catch (System.Exception ex) { co["baseline_error"] = ex.GetType().Name + ": " + ex.Message; }
                    co["baselines"] = bls;

                    // Corridor-level targets (create_corridor sets targets at this level; fallback for what the region level cannot read)
                    var cslots = new JsonArray();
                    try
                    {
                        CivAlignment sampleAl = null;
                        foreach (Autodesk.Civil.DatabaseServices.Baseline b in c.Baselines)
                        {
                            try { sampleAl = tr.GetObject(b.AlignmentId, OpenMode.ForRead) as CivAlignment; } catch { }
                            break;
                        }
                        double clo = double.MinValue, chi = double.MaxValue;
                        if (sampleAl != null)
                        {
                            clo = Math.Min(sampleAl.StartingStation, sampleAl.EndingStation) - 1.0;
                            chi = Math.Max(sampleAl.StartingStation, sampleAl.EndingStation) + 1.0;
                        }
                        foreach (CivTargetInfo t in c.GetTargets())
                            cslots.Add(DumpTargetSlot(tr, t, sampleAl, clo, chi, step, maxSamples));
                    }
                    catch (System.Exception ex) { co["corridor_targets_error"] = ex.GetType().Name + ": " + ex.Message; }
                    co["corridor_slots"] = cslots;
                    arr.Add(co);
                }
                tr.Commit();
            }
            if (arr.Count == 0) throw new InvalidOperationException("No matching corridor.");
            return arr;
        }

        /// <summary>Batch rename alignments (objects and handles untouched; offset/corridor references all preserved).</summary>
        static JsonNode RenameAlignments(JsonObject a, Document doc)
        {
            var items = a["items"] as JsonArray;
            if (items == null || items.Count == 0)
                throw new InvalidOperationException("items is required: [{from,to}].");
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var done = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (JsonNode n in items)
                {
                    var o = (JsonObject)n;
                    string from = Need(o, "from"), to = Need(o, "to");
                    var al = FindAlignment(tr, civ, from);
                    if (al == null) throw new InvalidOperationException("Alignment '" + from + "' not found.");
                    if (FindAlignment(tr, civ, to) != null)
                        throw new InvalidOperationException("Target name '" + to + "' already exists.");
                    al.UpgradeOpen();
                    al.Name = to;
                    done.Add(new JsonObject { ["from"] = from, ["to"] = to, ["handle"] = al.Handle.ToString() });
                }
                tr.Commit();
            }
            return new JsonObject { ["renamed"] = done };
        }

        /// <summary>Resolve one target slot: display name/type/subassembly, and for each target object its class name/handle/layer;
        /// alignment targets report offset alignment info, plain curves are sampled along their length to measure the offset distribution relative to the reference alignment.</summary>
        static JsonObject DumpTargetSlot(Transaction tr, CivTargetInfo t, CivAlignment refAl, double lo, double hi, double step, int maxSamples)
        {
            var so = new JsonObject
            {
                ["slot"] = t.DisplayName,
                ["type"] = t.TargetType.ToString(),
                ["subassembly"] = SafeStr(() => t.SubassemblyName)
            };
            var objs = new JsonArray();
            foreach (ObjectId tid in t.TargetIds)
            {
                var eo = new JsonObject();
                try
                {
                    var ent = tr.GetObject(tid, OpenMode.ForRead);
                    eo["class"] = ent.GetType().Name;
                    eo["handle"] = ent.Handle.ToString();
                    var e2 = ent as Entity;
                    if (e2 != null) eo["layer"] = e2.Layer;
                    var tal = ent as CivAlignment;
                    if (tal != null)
                    {
                        eo["name"] = tal.Name;
                        bool isOff = false;
                        try { isOff = tal.IsOffsetAlignment; } catch { }
                        eo["is_offset_alignment"] = isOff;
                        if (isOff)
                        {
                            try
                            {
                                var info = tal.OffsetAlignmentInfo;
                                var po = tr.GetObject(info.ParentAlignmentId, OpenMode.ForRead) as CivAlignment;
                                eo["offset_parent"] = po == null ? null : (JsonNode)po.Name;
                                eo["nominal_offset"] = Math.Round(info.NominalOffset, 3);
                            }
                            catch (System.Exception ex) { eo["offset_info_error"] = ex.GetType().Name; }
                        }
                    }
                    else if (refAl != null)
                    {
                        var cur = ent as Curve;
                        if (cur != null)
                            eo["offset_profile"] = SampleCurveOffsets(cur, refAl, lo, hi, step, maxSamples);
                    }
                }
                catch (System.Exception ex) { eo["error"] = ex.GetType().Name + ": " + ex.Message; }
                objs.Add(eo);
            }
            so["objects"] = objs;
            return so;
        }

        /// <summary>Sample a curve at equal spacing and compute station/offset relative to the alignment per point; only samples inside the [lo,hi] station window are counted.</summary>
        static JsonNode SampleCurveOffsets(Curve cur, CivAlignment al, double lo, double hi, double step, int maxSamples)
        {
            double len;
            try { len = cur.GetDistanceAtParameter(cur.EndParam); }
            catch (System.Exception ex) { return "(cannot get curve length: " + ex.GetType().Name + ")"; }
            if (len <= 0) return "(zero-length curve)";
            int n = Math.Min(Math.Max(2, maxSamples), Math.Max(2, (int)Math.Ceiling(len / Math.Max(0.5, step)) + 1));
            double dstep = len / (n - 1);
            var offs = new List<double>();
            double staMin = double.MaxValue, staMax = double.MinValue;
            int outside = 0, failed = 0;
            for (int i = 0; i < n; i++)
            {
                Point3d p;
                try { p = cur.GetPointAtDist(Math.Min(len, i * dstep)); } catch { failed++; continue; }
                double sta = 0, off = 0;
                try { al.StationOffset(p.X, p.Y, ref sta, ref off); } catch { failed++; continue; }
                if (sta < lo || sta > hi) { outside++; continue; }
                offs.Add(off);
                if (sta < staMin) staMin = sta;
                if (sta > staMax) staMax = sta;
            }
            var o = new JsonObject
            {
                ["curve_length"] = Math.Round(len, 3),
                ["samples_in_region"] = offs.Count,
                ["samples_outside_region"] = outside,
                ["samples_failed"] = failed
            };
            if (offs.Count > 0)
            {
                double mn = double.MaxValue, mx = double.MinValue, sum = 0;
                foreach (double v in offs) { if (v < mn) mn = v; if (v > mx) mx = v; sum += v; }
                o["offset_min"] = Math.Round(mn, 3);
                o["offset_max"] = Math.Round(mx, 3);
                o["offset_mean"] = Math.Round(sum / offs.Count, 3);
                o["offset_spread"] = Math.Round(mx - mn, 3);
                o["station_min"] = Math.Round(staMin, 3);
                o["station_max"] = Math.Round(staMax, 3);
            }
            return o;
        }

        /// <summary>Call a parameterless method on an object and dump all readable properties of the return value as JSON.
        /// No need to guess the struct member names returned by statistics interfaces (GetGeneralProperties / GetTinProperties).</summary>
        static JsonNode InvokeAndDump(object target, string methodName)
        {
            try
            {
                var m = target.GetType().GetMethod(methodName, Type.EmptyTypes);
                if (m == null) return "(no " + methodName + " method)";
                object v = m.Invoke(target, null);
                if (v == null) return "(returned null)";
                var o = new JsonObject();
                foreach (var p in v.GetType().GetProperties())
                {
                    if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                    try
                    {
                        object pv = p.GetValue(v, null);
                        if (pv == null) o[p.Name] = null;
                        else if (pv is double) o[p.Name] = Math.Round((double)pv, 4);
                        else if (pv is int) o[p.Name] = (int)pv;
                        else if (pv is bool) o[p.Name] = (bool)pv;
                        else o[p.Name] = pv.ToString();
                    }
                    catch (System.Exception ex) { o[p.Name] = "(read failed: " + ex.GetType().Name + ")"; }
                }
                return o;
            }
            catch (System.Exception ex) { return "(" + ex.GetType().Name + ": " + Truncate(ex.Message, 100) + ")"; }
        }

        /// <summary>Rebuild a corridor (diagnostic: what remains of a GUI-built corridor after a headless rebuild
        /// tells whether the subassembly code actually runs inside accoreconsole).</summary>
        static JsonNode RebuildCorridor(JsonObject a, Document doc)
        {
            string name = Need(a, "name");
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivCorridor c = FindCorridor(tr, db, name);
                if (c == null) throw new InvalidOperationException("Corridor '" + name + "' not found.");
                var before = new JsonArray();
                foreach (string s in c.GetLinkCodes()) before.Add(s);
                c.Rebuild();
                var after = new JsonArray();
                foreach (string s in c.GetLinkCodes()) after.Add(s);
                var res = new JsonObject
                {
                    ["corridor"] = name,
                    ["link_codes_before"] = before,
                    ["link_codes_after"] = after
                };
                tr.Commit();
                return res;
            }
        }

        static string SafeStr(Func<string> f)
        {
            try { return f() ?? ""; } catch (System.Exception ex) { return "(" + ex.GetType().Name + ")"; }
        }

        // ===================== Look up real API signatures =====================
        // AeccDbMgd is a mixed-mode assembly; it cannot be reflected out of process (MetadataLoadContext is tedious too),
        // but inside the acc process it is already loaded -- reflecting directly is easiest. Use it to check signatures before writing new ops instead of guessing.

        static JsonNode ApiSignatures(JsonObject a, Document doc)
        {
            string typeName = Need(a, "type");
            string filter = GetString(a, "member", null);
            bool staticsOnly = GetBool(a, "statics_only", false);
            int max = (int)GetDouble(a, "max", 120);

            // Try the full name first (GetType does not fail wholesale because some types in the assembly cannot load),
            // then fall back to scanning simple names -- GetTypes() may throw ReflectionTypeLoadException while scanning,
            // and its Types still holds the usable part, do not discard it entirely.
            // AutoCAD loads AeccDbMgd etc. in a custom AssemblyLoadContext, and
            // AppDomain.CurrentDomain.GetAssemblies() **cannot see them** (the first version fell into this).
            // So work back from types this plugin already references to their assemblies, and use those as search seeds.
            var pool = new List<System.Reflection.Assembly>
            {
                typeof(CivAlignment).Assembly,      // AeccDbMgd
                typeof(CivDoc).Assembly,            // AeccDbMgd / AeccApplicationMgd
                typeof(Database).Assembly,          // acdbmgd
                typeof(Document).Assembly           // acmgd
            };
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (!pool.Contains(asm)) pool.Add(asm);

            var hits = new List<Type>();
            foreach (var asm in pool)
            {
                try
                {
                    Type t = asm.GetType(typeName, false, false);
                    if (t != null && !hits.Contains(t)) hits.Add(t);
                }
                catch { }
            }
            if (hits.Count == 0)
            {
                foreach (var asm in pool)
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (System.Reflection.ReflectionTypeLoadException rex) { types = rex.Types; }
                    catch { continue; }
                    foreach (Type t in types)
                    {
                        if (t == null) continue;
                        if (t.FullName == typeName || t.Name == typeName) hits.Add(t);
                    }
                    if (hits.Count > 12) break;
                }
            }
            if (hits.Count == 0) throw new InvalidOperationException("Type '" + typeName + "' not found.");

            var arr = new JsonArray();
            foreach (Type t in hits)
            {
                var members = new JsonArray();
                var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
                          | System.Reflection.BindingFlags.DeclaredOnly;
                if (!staticsOnly) flags |= System.Reflection.BindingFlags.Instance;

                foreach (var m in t.GetMethods(flags))
                {
                    if (m.IsSpecialName) continue;
                    if (!string.IsNullOrEmpty(filter) &&
                        m.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (members.Count >= max) { members.Add("...(more)"); break; }
                    var ps = new List<string>();
                    foreach (var p in m.GetParameters()) ps.Add(Short(p.ParameterType) + " " + p.Name);
                    members.Add((m.IsStatic ? "static " : "") + Short(m.ReturnType) + " " +
                                m.Name + "(" + string.Join(", ", ps) + ")");
                }

                if (string.IsNullOrEmpty(filter))
                {
                    foreach (var p in t.GetProperties(flags))
                    {
                        if (members.Count >= max) break;
                        members.Add("prop " + Short(p.PropertyType) + " " + p.Name +
                                    (p.CanWrite ? " {get;set;}" : " {get;}"));
                    }
                }

                arr.Add(new JsonObject
                {
                    ["type"] = t.FullName,
                    ["assembly"] = t.Assembly.GetName().Name,
                    ["base"] = t.BaseType == null ? "" : t.BaseType.FullName,
                    ["members"] = members
                });
            }
            return arr;
        }

        /// <summary>Convert a line/polyline into an alignment.
        /// This version has **no** CreateFromPolyline (the snippet copied from the web does not compile on 2025);
        /// the real name is Alignment.Create(CivilDocument, PolylineOptions, ...),
        /// with the polyline, erase-source and add-spirals flags all packed into PolylineOptions (found with the api op).</summary>
        static ObjectId CreateAlignmentFromEntity(Transaction tr, CivDoc civ, string name,
            ObjectId siteId, ObjectId entId, ObjectId layerId, ObjectId styleId, ObjectId labelId,
            bool eraseSource, bool addCurves)
        {
            var opts = new Autodesk.Civil.DatabaseServices.PolylineOptions
            {
                PlineId = entId,
                EraseExistingEntities = eraseSource,
                AddCurvesBetweenTangents = addCurves
            };
            return CivAlignment.Create(civ, opts, name, siteId, layerId, styleId, labelId);
        }

        static string Short(Type t)
        {
            if (t == null) return "?";
            string n = t.Name;
            if (t.IsGenericType)
            {
                var args = new List<string>();
                foreach (Type g in t.GetGenericArguments()) args.Add(Short(g));
                int tick = n.IndexOf('`');
                if (tick > 0) n = n.Substring(0, tick);
                return n + "<" + string.Join(",", args) + ">";
            }
            return n;
        }

        // ===================== Shared helpers =====================

        static CivDoc Civ(Database db)
        {
            CivDoc c = CivDoc.GetCivilDocument(db);
            if (c == null) throw new InvalidOperationException("Cannot get the CivilDocument (this drawing may not be a Civil 3D drawing).");
            return c;
        }

        static string Need(JsonObject a, string key)
        {
            string v = GetString(a, key, null);
            if (string.IsNullOrEmpty(v)) throw new InvalidOperationException("Missing parameter " + key + ".");
            return v;
        }

        static ObjectId ResolveHandle(Database db, string handle)
        {
            long h = Convert.ToInt64(handle.Trim(), 16);
            ObjectId id;
            if (!db.TryGetObjectId(new Handle(h), out id) || id.IsNull)
                throw new InvalidOperationException("No object with handle " + handle + " in the drawing.");
            return id;
        }

        /// <summary>Find a style by name; when name is empty or not found, fall back to the first in the collection (Null when the collection is empty).</summary>
        static ObjectId FindStyleId(Transaction tr, object collection, string name)
        {
            // Match order: exact > case-insensitive > unique prefix > unique substring > first in collection (old fallback).
            // Prefix/substring exist for libraries whose "style names carry a version suffix": the drawing has @C3DF-SimpleGrid[Defalt],
            // @C3DF-river-dregde[Default-v2.0], while the caller only remembers the short name. The old behaviour of falling straight back to
            // the first entry on an exact miss silently sets the wrong style, worse than none.
            ObjectId first = ObjectId.Null;
            var en = collection as System.Collections.IEnumerable;
            if (en == null) return ObjectId.Null;

            var all = new List<KeyValuePair<ObjectId, string>>();
            foreach (object item in en)
            {
                if (!(item is ObjectId)) continue;
                ObjectId id = (ObjectId)item;
                if (first.IsNull) first = id;
                if (string.IsNullOrEmpty(name)) continue;
                string n = null;
                try { n = TryGetName(tr.GetObject(id, OpenMode.ForRead)); }
                catch { }
                if (string.IsNullOrEmpty(n)) continue;
                if (n == name) return id;                    // exact hit, done
                all.Add(new KeyValuePair<ObjectId, string>(id, n));
            }
            if (string.IsNullOrEmpty(name)) return first;

            var ci = new List<KeyValuePair<ObjectId, string>>();
            foreach (var kv in all)
                if (string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase)) ci.Add(kv);
            if (ci.Count > 0) return PickNewestStyle(ci);

            var pre = new List<KeyValuePair<ObjectId, string>>();
            foreach (var kv in all)
                if (kv.Value.StartsWith(name, StringComparison.OrdinalIgnoreCase)) pre.Add(kv);
            if (pre.Count > 0) return PickNewestStyle(pre);

            var con = new List<KeyValuePair<ObjectId, string>>();
            foreach (var kv in all)
                if (kv.Value.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) con.Add(kv);
            if (con.Count > 0) return PickNewestStyle(con);

            return first;
        }

        /// <summary>When one short name matches several styles take the highest version: the style library writes versions into names
        /// (@C3DF-river-dregde[Default] / [Default-v2.0]); taking the first in the collection is a random pick.</summary>
        static ObjectId PickNewestStyle(List<KeyValuePair<ObjectId, string>> cands)
        {
            ObjectId best = cands[0].Key;
            double bestVer = StyleVersion(cands[0].Value);
            string bestName = cands[0].Value;
            for (int i = 1; i < cands.Count; i++)
            {
                double v = StyleVersion(cands[i].Value);
                if (v > bestVer ||
                    (v == bestVer && string.Compare(cands[i].Value, bestName, StringComparison.OrdinalIgnoreCase) > 0))
                {
                    best = cands[i].Key;
                    bestVer = v;
                    bestName = cands[i].Value;
                }
            }
            return best;
        }

        /// <summary>Extract the vN[.N] version number from a style name; 0 when absent.</summary>
        static double StyleVersion(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            double best = 0;
            for (int i = 0; i < name.Length - 1; i++)
            {
                if (name[i] != 'v' && name[i] != 'V') continue;
                if (i > 0 && char.IsLetterOrDigit(name[i - 1])) continue;   // v must be preceded by a separator
                int j = i + 1;
                while (j < name.Length && (char.IsDigit(name[j]) || name[j] == '.')) j++;
                if (j == i + 1) continue;
                double val;
                if (double.TryParse(name.Substring(i + 1, j - i - 1),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out val) && val > best)
                    best = val;
            }
            return best;
        }

        static CivAlignment FindAlignment(Transaction tr, CivDoc civ, string name)
        {
            foreach (ObjectId id in civ.GetAlignmentIds())
            {
                var a = (CivAlignment)tr.GetObject(id, OpenMode.ForRead);
                if (a.Name == name) return a;
            }
            return null;
        }

        static ObjectId FindSurfaceId(Transaction tr, CivDoc civ, string name)
        {
            foreach (ObjectId id in civ.GetSurfaceIds())
            {
                var s = (CivSurface)tr.GetObject(id, OpenMode.ForRead);
                if (s.Name == name) return id;
            }
            return ObjectId.Null;
        }

        static void EraseAlignments(Transaction tr, CivDoc civ, params string[] names)
        {
            var set = new HashSet<string>(names);
            foreach (ObjectId id in civ.GetAlignmentIds())
            {
                var a = (CivAlignment)tr.GetObject(id, OpenMode.ForRead);
                if (!set.Contains(a.Name)) continue;
                a.UpgradeOpen();
                a.Erase();
            }
        }

        static void EraseProfiles(Transaction tr, CivDoc civ, params string[] names)
        {
            var set = new HashSet<string>(names);
            foreach (ObjectId aid in civ.GetAlignmentIds())
            {
                var a = (CivAlignment)tr.GetObject(aid, OpenMode.ForRead);
                foreach (ObjectId pid in a.GetProfileIds())
                {
                    var p = (CivProfile)tr.GetObject(pid, OpenMode.ForRead);
                    if (!set.Contains(p.Name)) continue;
                    p.UpgradeOpen();
                    p.Erase();
                }
            }
        }

        static void EraseCorridors(Transaction tr, Database db, params string[] names)
        {
            var set = new HashSet<string>(names);
            foreach (ObjectId id in ModelSpace(db, tr))
            {
                var c = tr.GetObject(id, OpenMode.ForRead) as CivCorridor;
                if (c == null || !set.Contains(c.Name)) continue;
                c.UpgradeOpen();
                c.Erase();
            }
        }

        static CivCorridor FindCorridor(Transaction tr, Database db, string name)
        {
            foreach (ObjectId id in ModelSpace(db, tr))
            {
                var c = tr.GetObject(id, OpenMode.ForRead) as CivCorridor;
                if (c != null && c.Name == name) return (CivCorridor)tr.GetObject(id, OpenMode.ForWrite);
            }
            return null;
        }

        static ObjectId FindCorridorSurfaceId(Transaction tr, Database db, string corridorName, string surfaceName)
        {
            CivCorridor c = FindCorridor(tr, db, corridorName);
            if (c == null) return ObjectId.Null;
            foreach (CivCorridorSurface cs in c.CorridorSurfaces)
                if (cs.Name == surfaceName) return cs.SurfaceId;
            return ObjectId.Null;
        }

        static ObjectId FindQtoCriteria(Transaction tr, CivDoc civ, string name)
        {
            foreach (ObjectId id in civ.Styles.QuantityTakeoffCriterias)
                if (TryGetName(tr.GetObject(id, OpenMode.ForRead)) == name) return id;
            return ObjectId.Null;
        }

        // ===================== Export all tables in the DWG drawing =====================

        public class ExtractedTableData
        {
            public int Id { get; set; }
            public string Title { get; set; } = "";
            public string Space { get; set; } = "";
            public string Type { get; set; } = "";
            public List<List<string>> Rows { get; set; } = new List<List<string>>();
        }

        public static JsonNode ExportAllDwgTables(JsonObject a, Document doc)
        {
            string targetPath = GetString(a, "target_dwg", null);
            string outdir = ResolveOutDir(a, doc);
            string excelOutPath = GetString(a, "excel_out", null);

            Database dbToRead = doc.Database;
            bool ownDb = false;

            if (!string.IsNullOrEmpty(targetPath) && File.Exists(targetPath))
            {
                dbToRead = new Database(false, true);
                dbToRead.ReadDwgFile(targetPath, FileOpenMode.OpenForReadAndAllShare, true, null);
                ownDb = true;
            }

            try
            {
                var allExtractedTables = new List<ExtractedTableData>();
                int tableCount = 0;

                using (Transaction tr = dbToRead.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(dbToRead.BlockTableId, OpenMode.ForRead);

                    foreach (ObjectId btrId in bt)
                    {
                        BlockTableRecord btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                        if (btr.IsLayout || btr.Name.Equals(BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (ObjectId entId in btr)
                            {
                                Entity ent = tr.GetObject(entId, OpenMode.ForRead) as Entity;
                                if (ent is Table tbl)
                                {
                                    tableCount++;
                                    ExtractedTableData tableData = ExtractAcadTable(tbl, tableCount, btr.Name);
                                    if (tableData != null && tableData.Rows.Count > 0)
                                    {
                                        allExtractedTables.Add(tableData);
                                    }
                                }
                            }
                        }
                    }

                    try
                    {
                        CivDoc civ = Civ(dbToRead);
                        if (civ != null)
                        {
                            foreach (ObjectId alId in civ.GetAlignmentIds())
                            {
                                CivAlignment al = tr.GetObject(alId, OpenMode.ForRead) as CivAlignment;
                                if (al == null) continue;

                                foreach (ObjectId slgId in al.GetSampleLineGroupIds())
                                {
                                    CivSampleLineGroup slg = tr.GetObject(slgId, OpenMode.ForRead) as CivSampleLineGroup;
                                    if (slg == null) continue;

                                    foreach (CivQtoMaterialList ml in slg.MaterialLists)
                                    {
                                        var civTables = ExtractCivMaterialList(slg, ml, ref tableCount, al.Name);
                                        if (civTables != null && civTables.Count > 0)
                                        {
                                            allExtractedTables.AddRange(civTables);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { }

                    tr.Commit();
                }

                var tablesJson = new JsonArray();
                foreach (var t in allExtractedTables)
                {
                    var tj = new JsonObject
                    {
                        ["id"] = t.Id,
                        ["title"] = t.Title,
                        ["space"] = t.Space,
                        ["type"] = t.Type,
                        ["rows_count"] = t.Rows.Count,
                        ["cols_count"] = t.Rows.Count > 0 ? t.Rows[0].Count : 0
                    };
                    var rowsJ = new JsonArray();
                    foreach (var r in t.Rows)
                    {
                        var rowJ = new JsonArray();
                        foreach (var c in r) rowJ.Add(c ?? "");
                        rowsJ.Add(rowJ);
                    }
                    tj["rows"] = rowsJ;
                    tablesJson.Add(tj);
                }

                string jsonExportPath = Path.Combine(outdir, "extracted_dwg_tables.json");
                File.WriteAllText(jsonExportPath, tablesJson.ToJsonString(), System.Text.Encoding.UTF8);

                return new JsonObject
                {
                    ["tables_found"] = allExtractedTables.Count,
                    ["json_export_path"] = jsonExportPath,
                    ["tables"] = tablesJson
                };
            }
            finally
            {
                if (ownDb) dbToRead.Dispose();
            }
        }

        static ExtractedTableData ExtractAcadTable(Table tbl, int index, string spaceName)
        {
            var data = new ExtractedTableData
            {
                Id = index,
                Space = spaceName,
                Type = "AcadTable",
                Title = "Table_" + index
            };

            int numRows = tbl.Rows.Count;
            int numCols = tbl.Columns.Count;

            for (int r = 0; r < numRows; r++)
            {
                var rowData = new List<string>();
                for (int c = 0; c < numCols; c++)
                {
                    string txt = "";
                    try { txt = tbl.Cells[r, c].TextString; } catch { }
                    txt = CleanMText(txt);
                    rowData.Add(txt);
                }

                if (r == 0)
                {
                    string joined = string.Join(" ", rowData).Trim();
                    if (!string.IsNullOrEmpty(joined))
                    {
                        data.Title = joined;
                    }
                }
                data.Rows.Add(rowData);
            }

            return data;
        }

        static List<ExtractedTableData> ExtractCivMaterialList(CivSampleLineGroup slg, CivQtoMaterialList ml, ref int index, string alignmentName)
        {
            var list = new List<ExtractedTableData>();

            try
            {
                var result = slg.GetTotalVolumeResultDataForMaterialList(ml.Guid);
                var dataTotal = new ExtractedTableData
                {
                    Id = ++index,
                    Space = "Civil3D_QTO",
                    Type = "CivQtoMaterialList_Total",
                    Title = "MaterialVolumeTable_" + alignmentName + "_" + ml.Name
                };
                dataTotal.Rows.Add(new List<string> { "No.", "Station", "Cumulative cut(m³)", "Cumulative fill(m³)", "Incremental cut(m³)", "Incremental fill(m³)" });

                int i = 0;
                double cut = 0, fill = 0;
                foreach (CivQtoSectionalResult sec in result.GetResultsAlongSampleLines())
                {
                    var v = sec.VolumeResult;
                    dataTotal.Rows.Add(new List<string>
                    {
                        (++i).ToString(),
                        Station(sec.Station),
                        Math.Round(v.CumulativeCutVolume, 3).ToString(),
                        Math.Round(v.CumulativeFillVolume, 3).ToString(),
                        Math.Round(v.IncrementalCutVolume, 3).ToString(),
                        Math.Round(v.IncrementalFillVolume, 3).ToString()
                    });
                    cut = v.CumulativeCutVolume;
                    fill = v.CumulativeFillVolume;
                }
                dataTotal.Rows.Add(new List<string> { "Total", "", Math.Round(cut, 3).ToString(), Math.Round(fill, 3).ToString(), "", "" });
                if (dataTotal.Rows.Count > 1) list.Add(dataTotal);
            }
            catch { }

            return list;
        }

        static string CleanMText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string t = raw.Replace("\\P", "\n").Replace("\\p", "\n");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\\[fFhHwWtTqQaAcC][^;]*;", "");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"[\{\}]", "");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\\L|\\l|\\O|\\o|\\K|\\k|\\~", "");
            return t.Trim();
        }
    }
}
