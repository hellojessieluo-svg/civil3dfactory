using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlign = Autodesk.Civil.DatabaseServices.Alignment;
using CivCorr = Autodesk.Civil.DatabaseServices.Corridor;
using CivBaseline = Autodesk.Civil.DatabaseServices.Baseline;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivTin = Autodesk.Civil.DatabaseServices.TinSurface;
using CivSubE = Autodesk.Civil.DatabaseServices.AlignmentSubEntity;
using CivSubArc = Autodesk.Civil.DatabaseServices.AlignmentSubEntityArc;
using CivSubType = Autodesk.Civil.DatabaseServices.AlignmentSubEntityType;

namespace Civil3DFactory
{
    /// <summary>
    /// export_design_lines: move the design intent from Civil 3D objects onto polylines, written into a brand-new empty DWG.
    /// The three kinds of lines produced are the input parameters for all future modelling; the model degrades to a downstream product.
    ///
    ///   CL-{channel}             the alignment used by the corridor baseline, open
    ///   EDGE-{channel}-{L|R}     the remaining alignments, assigned to a channel by geometry and given a side, open
    ///   BOUNDARY-{channel}       the outer boundary of the corridor surface, closed
    ///
    /// Three things are measured, not guessed:
    /// - Alignments are enumerated from ModelSpace, not CivilDocument.GetAlignmentIds()
    ///   -- the latter misses alignments inside sites (both B1 edges of this drawing sit in a site, giving 19 vs 21).
    /// - Channel ownership and side are measured with StationOffset against the parent centerline (positive = right, negative = left),
    ///   not taken from OffsetAlignmentInfo.NominalOffset (this drawing read 1(E) left 75 right 25, contradicting the name's 25).
    /// - The centerline set = alignments referenced by corridor baselines, no naming rules.
    ///
    /// Geometry is exact: lines and arcs are kept as-is (arc bulge = tan(delta/4), negative when clockwise);
    /// only spirals are sampled by spiral_step. Every line reports length_delta as a self-check.
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeExportDesignLines(JsonObject args, Document doc)
            => ExportDesignLines(args, doc);

        sealed class EdlLine
        {
            public string Source;              // source object name
            public string Channel;
            public string Role;                // CENTER / LEFT / RIGHT / BOUNDARY
            public double SrcLength;
            public bool Closed;
            public double OffMin, OffMax, OffMean;
            public List<Point2d> V = new List<Point2d>();
            public List<double> B = new List<double>();
            public int Lines, Arcs, Spirals;
        }

        public static JsonNode ExportDesignLines(JsonObject a, Document doc)
        {
            string outPath = Need(a, "out");
            if (!Path.IsPathRooted(outPath))
                throw new InvalidOperationException("out must be an absolute path: " + outPath);
            bool overwrite = GetBool(a, "overwrite", false);
            if (File.Exists(outPath) && !overwrite)
                throw new InvalidOperationException("File already exists, refusing to overwrite: " + outPath + " (pass overwrite:true to overwrite)");

            string tplCenter = GetString(a, "center_layer", "CL-{channel}");
            string tplEdge = GetString(a, "edge_layer", "EDGE-{channel}-{side}");
            string tplBound = GetString(a, "boundary_layer", "BOUNDARY-{channel}");
            short cColor = (short)GetDouble(a, "center_color", 3);
            short eColor = (short)GetDouble(a, "edge_color", 4);
            short bColor = (short)GetDouble(a, "boundary_color", 2);
            string cLt = GetString(a, "center_linetype", "CENTER2");
            string eLt = GetString(a, "edge_linetype", "Continuous");
            string bLt = GetString(a, "boundary_linetype", "Continuous");
            double ltScale = GetDouble(a, "linetype_scale", 5.0);
            double spiralStep = GetDouble(a, "spiral_step", 5.0);
            if (spiralStep <= 1e-6) spiralStep = 5.0;
            int probes = (int)GetDouble(a, "probe_points", 20);
            if (probes < 3) probes = 3;
            double offTol = GetDouble(a, "offset_tolerance", 200.0);
            bool withBoundaries = GetBool(a, "boundaries", true);

            Database db = doc.Database;
            var lines = new List<EdlLine>();
            var notes = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // ---- 1 Collect all alignments, corridors and surfaces from ModelSpace ----
                var aligns = new List<CivAlign>();
                var corrs = new List<CivCorr>();
                var tins = new List<CivTin>();
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    DBObject o;
                    try { o = tr.GetObject(id, OpenMode.ForRead); } catch (System.Exception) { continue; }
                    if (o is CivAlign) aligns.Add((CivAlign)o);
                    else if (o is CivCorr) corrs.Add((CivCorr)o);
                    else if (o is CivTin) tins.Add((CivTin)o);
                }

                // ---- 2 Centerlines = alignments referenced by corridor baselines ----
                var centerIds = new HashSet<ObjectId>();
                var corridorChannel = new Dictionary<string, string>();   // corridor name -> channel name
                foreach (CivCorr c in corrs)
                {
                    string cname = "";
                    try { cname = c.Name; } catch (System.Exception) { }
                    try
                    {
                        foreach (CivBaseline bl in c.Baselines)
                        {
                            ObjectId aid = bl.AlignmentId;
                            if (aid.IsNull) continue;
                            centerIds.Add(aid);
                            var al = tr.GetObject(aid, OpenMode.ForRead) as CivAlign;
                            if (al != null && !corridorChannel.ContainsKey(cname))
                                corridorChannel[cname] = al.Name;
                        }
                    }
                    catch (System.Exception ex) { notes.Add("Corridor " + cname + " failed to read baselines: " + ex.Message); }
                }
                if (centerIds.Count == 0)
                    throw new InvalidOperationException("No corridor baselines in the drawing; cannot determine which alignments are centerlines.");

                var centers = new List<CivAlign>();
                var edges = new List<CivAlign>();
                foreach (CivAlign al in aligns)
                    (centerIds.Contains(al.ObjectId) ? centers : edges).Add(al);

                var diagC = new JsonArray();
                foreach (CivAlign x in centers) diagC.Add(x.Name);
                var diagE = new JsonArray();
                foreach (CivAlign x in edges) diagE.Add(x.Name);
                notes.Add("Diagnostics: ModelSpace has " + aligns.Count +
                          " alignments; " + centers.Count + " classified as centerlines " + diagC.ToJsonString() +
                          "; " + edges.Count + " classified as edges " + diagE.ToJsonString());

                // ---- 3 Centerlines ----
                foreach (CivAlign al in centers)
                {
                    var it = new EdlLine { Source = al.Name, Channel = al.Name, Role = "CENTER", SrcLength = al.Length };
                    if (EdlExtract(al, spiralStep, it)) lines.Add(it);
                    else notes.Add("Centerline " + al.Name + " has no convertible geometry, skipped");
                }

                // ---- 4 Edges: assign to a channel by measured offset and determine the side ----
                foreach (CivAlign al in edges)
                {
                    var it = new EdlLine { Source = al.Name, SrcLength = al.Length };
                    if (!EdlExtract(al, spiralStep, it))
                    { notes.Add("Edge " + al.Name + " has no convertible geometry, skipped"); continue; }

                    CivAlign best = null;
                    double bestAbs = double.MaxValue, bMin = 0, bMax = 0, bMean = 0;
                    foreach (CivAlign c in centers)
                    {
                        double mn, mx, mean;
                        int hit = EdlMeasure(c, al, probes, out mn, out mx, out mean);
                        if (hit < probes / 2) continue;                 // most probe points fall outside this centerline's station range
                        double mag = Math.Abs(mean);
                        if (mag > offTol) continue;                     // too far away, not an edge of this channel
                        if (mag < bestAbs) { bestAbs = mag; best = c; bMin = mn; bMax = mx; bMean = mean; }
                    }

                    if (best == null)
                    {
                        // Report what each centerline measured instead of just saying "not found"
                        var why = new JsonArray();
                        foreach (CivAlign c in centers)
                        {
                            double m1, m2, m3;
                            int h = EdlMeasure(c, al, probes, out m1, out m2, out m3);
                            why.Add(c.Name + ": hits " + h + "/" + probes +
                                    " mean offset " + Math.Round(m3, 2));
                        }
                        notes.Add("Edge " + al.Name + " has no parent centerline (needs hits >= " + (probes / 2) +
                                  " and |mean offset| <= " + offTol.ToString("0.#") + " m). Per-centerline measurements: " +
                                  why.ToJsonString());
                        continue;
                    }
                    it.Channel = best.Name;
                    it.Role = bMean >= 0 ? "RIGHT" : "LEFT";            // Civil convention: positive = right, negative = left
                    it.OffMin = Math.Abs(bMin); it.OffMax = Math.Abs(bMax); it.OffMean = Math.Abs(bMean);
                    lines.Add(it);
                }

                // ---- 5 Road boundaries: outer boundary of the corridor surface (closed) ----
                if (withBoundaries)
                {
                    foreach (CivTin s in tins)
                    {
                        string sname = "";
                        try { sname = s.Name; } catch (System.Exception) { }
                        string channel = null;
                        foreach (var kv in corridorChannel)
                            if (sname.IndexOf(kv.Key, StringComparison.Ordinal) >= 0) { channel = kv.Value; break; }
                        if (channel == null) continue;                  // non-corridor surfaces such as existing ground

                        int got = 0;
                        try
                        {
                            var bdefs = s.BoundariesDefinition;
                            for (int bi = 0; bi < bdefs.Count; bi++)
                            {
                                var op = bdefs[bi];
                                foreach (Autodesk.Civil.DatabaseServices.SurfaceBoundary bd in op)
                                {
                                    var it = new EdlLine
                                    { Source = sname, Channel = channel, Role = "BOUNDARY", Closed = true };
                                    foreach (Point3d p in bd.Vertices)
                                    { it.V.Add(new Point2d(p.X, p.Y)); it.B.Add(0.0); it.Lines++; }
                                    if (it.V.Count < 3) continue;
                                    // If first and last points coincide, drop the last one and rely on the Closed flag
                                    if (it.V[0].GetDistanceTo(it.V[it.V.Count - 1]) < 1e-6)
                                    { it.V.RemoveAt(it.V.Count - 1); it.B.RemoveAt(it.B.Count - 1); }
                                    lines.Add(it); got++;
                                }
                            }
                        }
                        catch (System.Exception ex)
                        { notes.Add("Surface " + sname + " failed to read boundaries: " + ex.Message); }
                        // When the definition boundary is empty, fall back to ExtractBorder: the surface's actual outline.
                        // It creates entities in the host drawing; they are erased right after reading (the host drawing is never saved anyway).
                        if (got == 0)
                        {
                            try
                            {
                                ObjectIdCollection ids = s.ExtractBorder(
                                    Autodesk.Civil.SurfaceExtractionSettingsType.Model);
                                foreach (ObjectId eid in ids)
                                {
                                    var ent = tr.GetObject(eid, OpenMode.ForWrite) as Entity;
                                    if (ent == null) continue;
                                    var it = new EdlLine
                                    { Source = sname + "(ExtractBorder)", Channel = channel, Role = "BOUNDARY", Closed = true };
                                    var pl2 = ent as Polyline;
                                    var p3 = ent as Polyline3d;
                                    if (pl2 != null)
                                    {
                                        for (int k = 0; k < pl2.NumberOfVertices; k++)
                                        { it.V.Add(pl2.GetPoint2dAt(k)); it.B.Add(pl2.GetBulgeAt(k)); }
                                    }
                                    else if (p3 != null)
                                    {
                                        foreach (ObjectId vid in p3)
                                        {
                                            var v = tr.GetObject(vid, OpenMode.ForRead) as PolylineVertex3d;
                                            if (v == null) continue;
                                            it.V.Add(new Point2d(v.Position.X, v.Position.Y)); it.B.Add(0.0);
                                        }
                                    }
                                    ent.Erase();                       // erase right after reading, do not leave it in the host drawing
                                    if (it.V.Count < 3) continue;
                                    if (it.V[0].GetDistanceTo(it.V[it.V.Count - 1]) < 1e-6)
                                    { it.V.RemoveAt(it.V.Count - 1); it.B.RemoveAt(it.B.Count - 1); }
                                    it.Lines = it.V.Count;
                                    lines.Add(it); got++;
                                }
                                if (got > 0)
                                    notes.Add("Surface " + sname + " has no definition boundary; outline taken with ExtractBorder (" + got + " curves)");
                            }
                            catch (System.Exception ex)
                            { notes.Add("Surface " + sname + " ExtractBorder failed: " + ex.Message); }
                        }
                        if (got == 0) notes.Add("Surface " + sname + " has no outer boundary at all");
                    }
                }
                tr.Commit();
            }

            if (lines.Count == 0)
                throw new InvalidOperationException("No lines to export.");

            // ---- 6 Write into a brand-new empty drawing ----
            var items = new JsonArray();
            using (var nd = new Database(true, false))
            {
                nd.Insunits = UnitsValue.Meters;
                using (Transaction tr = nd.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(nd.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    foreach (EdlLine it in lines)
                    {
                        string side = it.Role == "LEFT" ? "L" : it.Role == "RIGHT" ? "R" : "";
                        string tpl = it.Role == "CENTER" ? tplCenter
                                   : it.Role == "BOUNDARY" ? tplBound : tplEdge;
                        string layer = tpl.Replace("{channel}", it.Channel).Replace("{side}", side);
                        string lt = it.Role == "CENTER" ? cLt : it.Role == "BOUNDARY" ? bLt : eLt;
                        short color = it.Role == "CENTER" ? cColor : it.Role == "BOUNDARY" ? bColor : eColor;

                        ObjectId ltId = A2PResolveLinetype(tr, nd, lt, "acadiso.lin");
                        ObjectId layerId = EadEnsureLayer(tr, nd, layer, color, ltId);

                        // Append before setting properties: an un-appended entity is still bound to the host drawing,
                        // and setting LayerId (taken from the new drawing) directly throws eWrongDatabase.
                        var pl = new Polyline(it.V.Count);
                        pl.SetDatabaseDefaults(nd);
                        for (int i = 0; i < it.V.Count; i++)
                            pl.AddVertexAt(i, it.V[i], it.B[i], 0.0, 0.0);
                        pl.Elevation = 0.0;
                        pl.Closed = it.Closed;
                        ms.AppendEntity(pl);
                        tr.AddNewlyCreatedDBObject(pl, true);

                        pl.LayerId = layerId;
                        pl.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                            Autodesk.AutoCAD.Colors.ColorMethod.ByAci, color);
                        if (!ltId.IsNull) pl.LinetypeId = ltId;
                        pl.LinetypeScale = ltScale;
                        EadSetXData(tr, nd, pl, it.Channel, it.Role, it.OffMean, it.Source);

                        double plLen = 0.0, plArea = 0.0;
                        try { plLen = pl.Length; } catch (System.Exception) { }
                        try { if (it.Closed) plArea = Math.Abs(pl.Area); } catch (System.Exception) { }

                        var row = new JsonObject
                        {
                            ["layer"] = layer,
                            ["channel"] = it.Channel,
                            ["role"] = it.Role,
                            ["source"] = it.Source,
                            ["closed"] = it.Closed,
                            ["vertices"] = it.V.Count,
                            ["arc_seg"] = it.Arcs,
                            ["spiral_seg"] = it.Spirals,
                            ["polyline_length"] = Round(plLen, 4)
                        };
                        if (it.Role == "BOUNDARY") row["area"] = Round(plArea, 3);
                        else
                        {
                            row["source_length"] = Round(it.SrcLength, 4);
                            row["length_delta"] = Round(plLen - it.SrcLength, 4);
                        }
                        if (it.Role == "LEFT" || it.Role == "RIGHT")
                        {
                            row["offset_mean"] = Round(it.OffMean, 3);
                            row["offset_min"] = Round(it.OffMin, 3);
                            row["offset_max"] = Round(it.OffMax, 3);
                        }
                        items.Add(row);
                    }
                    tr.Commit();
                }

                string dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                nd.SaveAs(outPath, DwgVersion.Current);
            }

            long bytes = 0;
            try { bytes = new FileInfo(outPath).Length; } catch (System.Exception) { }

            int nc = 0, ne = 0, nb = 0;
            foreach (EdlLine l in lines)
            { if (l.Role == "CENTER") nc++; else if (l.Role == "BOUNDARY") nb++; else ne++; }

            return new JsonObject
            {
                ["output"] = outPath,
                ["bytes"] = bytes,
                ["centerlines"] = nc,
                ["edge_lines"] = ne,
                ["boundaries"] = nb,
                ["total"] = lines.Count,
                ["notes"] = notes,
                ["items"] = items
            };
        }

        /// <summary>Sample along edge and measure its station offset relative to center. Returns the number of points inside the station range.
        /// Civil convention: positive offset = right of the centerline, negative = left.</summary>
        static int EdlMeasure(CivAlign center, CivAlign edge, int probes,
            out double min, out double max, out double mean)
        {
            min = 0; max = 0; mean = 0;
            double lo = double.MaxValue, hi = double.MinValue, sum = 0;
            int hit = 0;
            double s0 = edge.StartingStation, s1 = edge.EndingStation;
            for (int i = 0; i < probes; i++)
            {
                double st = s0 + (s1 - s0) * i / (probes - 1.0);
                double e = 0, n = 0;
                try { edge.PointLocation(st, 0.0, ref e, ref n); }
                catch (System.Exception) { continue; }
                double station = 0, offset = 0;
                bool oor = false;
                try { center.StationOffsetAcceptOutOfRange(e, n, ref station, ref offset, ref oor); }
                catch (System.Exception) { continue; }
                if (oor) continue;
                hit++;
                if (offset < lo) lo = offset;
                if (offset > hi) hi = offset;
                sum += offset;
            }
            if (hit == 0) return 0;
            min = lo; max = hi; mean = sum / hit;
            return hit;
        }

        /// <summary>Alignment -> vertex/bulge. Lines and arcs are exact; spirals are sampled by step.</summary>
        static bool EdlExtract(CivAlign al, double spiralStep, EdlLine it)
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
                    CivSubE sub = ent[j];
                    if (sub.SubEntityType == CivSubType.Arc)
                    {
                        var arc = sub as CivSubArc;
                        double bulge = 0.0;
                        if (arc != null)
                        {
                            bulge = Math.Tan(Math.Abs(arc.Delta) / 4.0);
                            if (arc.Clockwise) bulge = -bulge;
                        }
                        it.V.Add(sub.StartPoint); it.B.Add(bulge); it.Arcs++;
                    }
                    else if (sub.SubEntityType == CivSubType.Spiral)
                    {
                        double a0 = sub.StartStation, a1 = sub.EndStation, span = a1 - a0;
                        int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(span) / spiralStep));
                        for (int k = 0; k < steps; k++)
                        {
                            double e = 0, nn = 0;
                            al.PointLocation(a0 + span * k / steps, 0.0, ref e, ref nn);
                            it.V.Add(new Point2d(e, nn)); it.B.Add(0.0);
                        }
                        it.Spirals++;
                    }
                    else { it.V.Add(sub.StartPoint); it.B.Add(0.0); it.Lines++; }
                    tail = sub.EndPoint; hasTail = true;
                }
            }
            if (it.V.Count == 0) return false;
            if (hasTail) { it.V.Add(tail); it.B.Add(0.0); }
            return true;
        }
    }
}
