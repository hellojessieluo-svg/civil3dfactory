using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Civil3DFactory.Geometry;

namespace Civil3DFactory
{
    /// <summary>
    /// make_parcels: extent lines + smoothed centerlines -> closed terrace boundaries (terrace building).
    ///
    /// Same algorithm core (MakeParcelsCore.cs) as C3DF-MakeParcels/CT in products\waterbox.
    /// Multiple outer rings are grouped automatically: containment depth decides outer ring (0) / island hole (1) / ignored (>=2); holes attach to the
    /// smallest outer ring containing them; centerlines are grouped by which outer ring their midpoint falls in; each group builds terraces independently.
    /// Idempotent: clear_previous (default on) erases existing polylines on out_layer before drawing.
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeMakeParcels(JsonObject a, Document doc)
        {
            string boundaryLayer = GetString(a, "boundary_layer", null);
            string centerLayer = GetString(a, "centerline_layer", null);
            var boundaryHandles = CollectHandles(a["boundary_handles"] as JsonArray);
            var centerHandles = CollectHandles(a["centerline_handles"] as JsonArray);
            double halfW = GetDouble(a, "half_width", 15.0);
            bool widthsFromOffsets = GetBool(a, "widths_from_offsets", true);
            double filletR = GetDouble(a, "fillet_r", 20.0);
            double minDefl = GetDouble(a, "min_defl_deg", 8.0);   // 8 deg = GY noise floor: obtuse corners are rounded too, every terrace corner must be round (owner's rule 08-22)
            double minArea = GetDouble(a, "min_area", 500.0);
            string outLayer = GetString(a, "out_layer", "C3DF-TERRACE-BOUNDARY");
            string chanLayer = GetString(a, "channel_layer", "C3DF-CHANNEL-EXTENT");
            bool clearPrev = GetBool(a, "clear_previous", true);

            if (string.IsNullOrWhiteSpace(boundaryLayer) && boundaryHandles.Count == 0)
                throw new InvalidOperationException("Give at least one of boundary_layer or boundary_handles.");
            if (string.IsNullOrWhiteSpace(centerLayer) && centerHandles.Count == 0)
                throw new InvalidOperationException("Give at least one of centerline_layer or centerline_handles.");

            Database db = doc.Database;
            var warnings = new JsonArray();
            var smallOnes = new JsonArray();
            var perOuter = new JsonArray();
            int cleared = 0, parcelTotal = 0, extendedTotal = 0, channelLoopTotal = 0;
            double channelAreaTotal = 0;
            var ignoredBounds = new JsonArray();
            var unassignedCls = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // ---- Collect extent lines and centerlines (in-memory clones, z zeroed) ----
                var bounds = new List<(string handle, Polyline pl)>();
                var centers = new List<(string handle, Polyline pl)>();
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not Polyline pl) continue;
                    string h = pl.Handle.ToString();
                    bool wantB = boundaryHandles.Count > 0 ? boundaryHandles.Contains(h)
                               : string.Equals(pl.Layer, boundaryLayer, StringComparison.OrdinalIgnoreCase);
                    bool wantC = centerHandles.Count > 0 ? centerHandles.Contains(h)
                               : string.Equals(pl.Layer, centerLayer, StringComparison.OrdinalIgnoreCase);
                    if (wantB && (pl.Closed || OffsetConeCore.IsSnapClosed(pl)))
                    {
                        var c = (Polyline)pl.Clone(); c.Elevation = 0; c.Closed = true;
                        bounds.Add((h, c));
                    }
                    else if (wantC && pl.NumberOfVertices >= 2 && !pl.Closed)
                    {
                        var c = (Polyline)pl.Clone(); c.Elevation = 0;
                        centers.Add((h, c));
                    }
                    else if (wantB || wantC)
                        warnings.Add((JsonNode)$"Entity {h} does not qualify (extent lines must be closed / centerlines open), ignored");
                }
                if (bounds.Count == 0) throw new InvalidOperationException("No closed extent line found.");
                if (centers.Count == 0) throw new InvalidOperationException("No open centerline found.");

                // ---- Grouping by containment depth (point-in-polygon via the factory's GridSamplePolygon/GridPointInPolygon) ----
                var rings = bounds.Select(b => GridSamplePolygon(b.pl, 2.0)).ToList();
                int n = bounds.Count;
                var depth = new int[n];
                var containers = new List<List<int>>();
                for (int i = 0; i < n; i++)
                {
                    var inSet = new List<int>();
                    Point2d probe = bounds[i].pl.GetPoint2dAt(0);
                    for (int j = 0; j < n; j++)
                        if (j != i && GridPointInPolygon(probe, rings[j])) inSet.Add(j);
                    depth[i] = inSet.Count;
                    containers.Add(inSet);
                }
                var outers = new List<int>();
                var holesOf = new Dictionary<int, List<int>>();
                for (int i = 0; i < n; i++)
                    if (depth[i] == 0) { outers.Add(i); holesOf[i] = new List<int>(); }
                for (int i = 0; i < n; i++)
                {
                    if (depth[i] == 1)
                    {
                        int host = containers[i].OrderBy(j => Math.Abs(bounds[j].pl.Area)).First();
                        if (holesOf.ContainsKey(host)) holesOf[host].Add(i);
                        else ignoredBounds.Add((JsonNode)$"{bounds[i].handle} (host is not an outer ring)");
                    }
                    else if (depth[i] >= 2)
                        ignoredBounds.Add((JsonNode)$"{bounds[i].handle} (containment depth {depth[i]})");
                }
                if (outers.Count == 0) throw new InvalidOperationException("No outer ring identified.");

                // ---- Per-channel width: match centerline polyline <-> main alignment by start point + length, take |NominalOffset| of its offset child
                //      (that is what the half-width panel edits -- terraces follow whichever width was changed; unmatched falls back to the global half_width) ----
                var widthLut = new List<(Point2d start, double len, double w)>();
                if (widthsFromOffsets)
                {
                    try
                    {
                        var civW = Civ(db);
                        var wByParent = new Dictionary<ObjectId, double>();
                        foreach (ObjectId aid in civW.GetAlignmentIds())
                        {
                            if (tr.GetObject(aid, OpenMode.ForRead)
                                is not Autodesk.Civil.DatabaseServices.Alignment seg) continue;
                            Autodesk.Civil.DatabaseServices.OffsetAlignmentInfo oi;
                            try { oi = seg.OffsetAlignmentInfo; } catch { continue; }
                            if (oi == null) continue;
                            double wv = Math.Abs(oi.NominalOffset);
                            if (!wByParent.TryGetValue(oi.ParentAlignmentId, out double cur) || wv > cur)
                                wByParent[oi.ParentAlignmentId] = wv;
                        }
                        foreach (var kv in wByParent)
                        {
                            if (tr.GetObject(kv.Key, OpenMode.ForRead)
                                is not Autodesk.Civil.DatabaseServices.Alignment host) continue;
                            double e0 = 0, n0 = 0;
                            host.PointLocation(host.StartingStation, 0, ref e0, ref n0);
                            widthLut.Add((new Point2d(e0, n0), host.Length, kv.Value));
                        }
                    }
                    catch { }
                }
                double WidthOf(Polyline cl)
                {
                    foreach (var (s, l, w) in widthLut)
                        if (Math.Abs(l - cl.Length) < 0.02 && s.GetDistanceTo(cl.GetPoint2dAt(0)) < 0.02)
                            return w;
                    return halfW;
                }

                // ---- Group centerlines by midpoint ----
                var clsOf = outers.ToDictionary(o => o, o => new List<Polyline>());
                var wsOf = outers.ToDictionary(o => o, o => new List<double>());
                foreach (var (h, cl) in centers)
                {
                    Point3d mid;
                    try { mid = cl.GetPointAtDist(cl.Length / 2); }
                    catch { unassignedCls.Add((JsonNode)h); continue; }
                    int home = -1;
                    foreach (int o in outers)
                        if (GridPointInPolygon(new Point2d(mid.X, mid.Y), rings[o])) { home = o; break; }
                    if (home < 0) { unassignedCls.Add((JsonNode)h); continue; }
                    clsOf[home].Add(cl);
                    wsOf[home].Add(WidthOf(cl));
                }

                // ---- Idempotent clean-up of old output ----
                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                if (clearPrev)
                {
                    foreach (ObjectId id in ModelSpace(db, tr))
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is Polyline p &&
                            (string.Equals(p.Layer, outLayer, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(p.Layer, chanLayer, StringComparison.OrdinalIgnoreCase)))
                        { p.UpgradeOpen(); p.Erase(); cleared++; }
                    }
                }
                MpEnsureLayer(tr, db, outLayer, 3);
                MpEnsureLayer(tr, db, chanLayer, 4);   // cyan

                // ---- Build terraces per outer ring ----
                foreach (int o in outers)
                {
                    if (clsOf[o].Count == 0)
                    {
                        warnings.Add((JsonNode)$"Outer ring {bounds[o].handle} has no centerline assigned, skipped");
                        perOuter.Add(new JsonObject
                        { ["outer"] = bounds[o].handle, ["parcels"] = 0, ["skipped"] = true });
                        continue;
                    }
                    var holes = holesOf[o].Select(i => bounds[i].pl).ToList();
                    var res = MakeParcelsCore.GenerateOne(bounds[o].pl, holes, clsOf[o],
                        halfW, filletR, minDefl, widthsFromOffsets ? wsOf[o] : null);
                    extendedTotal += res.Extended;
                    foreach (string w in res.Warnings) warnings.Add((JsonNode)$"[{bounds[o].handle}] {w}");

                    int cnt = 0;
                    foreach (Polyline parcel in res.Parcels)
                    {
                        parcel.Layer = outLayer;
                        space.AppendEntity(parcel);
                        tr.AddNewlyCreatedDBObject(parcel, true);
                        cnt++; parcelTotal++;
                        double area = Math.Abs(parcel.Area);
                        if (area < minArea)
                            smallOnes.Add(new JsonObject
                            { ["handle"] = parcel.Handle.ToString(), ["area"] = Round(area, 1) });
                    }
                    int chanCnt = 0;
                    foreach (Polyline loop in res.ChannelLoops)
                    {
                        loop.Layer = chanLayer;
                        space.AppendEntity(loop);
                        tr.AddNewlyCreatedDBObject(loop, true);
                        chanCnt++; channelLoopTotal++;
                    }
                    channelAreaTotal += res.ChannelArea;

                    var wArr = new JsonArray();
                    foreach (double wv in wsOf[o]) wArr.Add(Round(wv, 2));
                    perOuter.Add(new JsonObject
                    {
                        ["outer"] = bounds[o].handle,
                        ["holes"] = holesOf[o].Count,
                        ["centerlines"] = clsOf[o].Count,
                        ["half_widths"] = wArr,
                        ["parcels"] = cnt,
                        ["channel_loops"] = chanCnt,
                        ["channel_area"] = Round(res.ChannelArea, 1)
                    });
                }

                foreach (var (_, pl) in bounds) if (!pl.IsDisposed) pl.Dispose();
                foreach (var (_, pl) in centers) if (!pl.IsDisposed) pl.Dispose();
                tr.Commit();
            }

            // Assert: no terrace at all = failure (silent success breaks reuse)
            if (parcelTotal == 0)
                throw new InvalidOperationException("The boolean produced no terrace at all -- check extent-line closure and centerline assignment.");

            return new JsonObject
            {
                ["parcels"] = parcelTotal,
                ["out_layer"] = outLayer,
                ["channel_loops"] = channelLoopTotal,
                ["channel_layer"] = chanLayer,
                ["channel_area_total"] = Round(channelAreaTotal, 1),
                ["cleared_previous"] = cleared,
                ["centerlines_extended"] = extendedTotal,
                ["per_outer"] = perOuter,
                ["small_parcels"] = smallOnes,
                ["ignored_boundaries"] = ignoredBounds,
                ["unassigned_centerlines"] = unassignedCls,
                ["warnings"] = warnings
            };
        }

        static HashSet<string> CollectHandles(JsonArray arr)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (arr != null)
                foreach (JsonNode h in arr) if (h != null) set.Add(h.ToString().Trim());
            return set;
        }

        static void MpEnsureLayer(Transaction tr, Database db, string name, short colorIndex)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return;
            lt.UpgradeOpen();
            var rec = new LayerTableRecord
            {
                Name = name,
                Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex),
            };
            lt.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }
    }
}
