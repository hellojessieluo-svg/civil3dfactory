using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlign = Autodesk.Civil.DatabaseServices.Alignment;
using CivCorr = Autodesk.Civil.DatabaseServices.Corridor;
using CivBaseline = Autodesk.Civil.DatabaseServices.Baseline;
using CivTin = Autodesk.Civil.DatabaseServices.TinSurface;

namespace Civil3DFactory
{
    /// <summary>
    /// measure_channel_width: measure the actual dredge width of each channel along its length.
    ///
    /// Method: take every vertex of the corridor surface's outer boundary, project it with the centerline's StationOffset into (station, offset),
    /// bucket by interval; within a bucket the largest |negative offset| = left half width, the largest positive offset = right half width.
    /// No geometric intersection, just projection statistics: robust and fast.
    ///
    /// Why: channels have VARIABLE width; the "average top width 100 m" in the quantity table is a mean, not the design value.
    /// A closed boundary can express variable width, a single number cannot -- this table translates the boundary into width along the length.
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeMeasureChannelWidth(JsonObject args, Document doc)
            => MeasureChannelWidth(args, doc);

        public static JsonNode MeasureChannelWidth(JsonObject a, Document doc)
        {
            double interval = GetDouble(a, "interval", 50.0);
            if (interval <= 1e-6) interval = 50.0;
            double minArea = GetDouble(a, "min_area", 100.0);   // boundaries smaller than this area are treated as fragments and dropped
            string onlyCh = GetString(a, "channel", null);

            Database db = doc.Database;
            var channels = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var corrs = new List<CivCorr>();
                var tins = new List<CivTin>();
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    DBObject o;
                    try { o = tr.GetObject(id, OpenMode.ForRead); } catch (System.Exception) { continue; }
                    if (o is CivCorr) corrs.Add((CivCorr)o);
                    else if (o is CivTin) tins.Add((CivTin)o);
                }

                // corridor name -> centerline alignment
                var corridorAlign = new Dictionary<string, CivAlign>();
                foreach (CivCorr c in corrs)
                {
                    try
                    {
                        string cn = c.Name;
                        foreach (CivBaseline bl in c.Baselines)
                        {
                            var al = tr.GetObject(bl.AlignmentId, OpenMode.ForRead) as CivAlign;
                            if (al != null && !corridorAlign.ContainsKey(cn)) corridorAlign[cn] = al;
                        }
                    }
                    catch (System.Exception) { }
                }

                foreach (CivTin s in tins)
                {
                    string sname;
                    try { sname = s.Name; } catch (System.Exception) { continue; }
                    CivAlign center = null;
                    foreach (var kv in corridorAlign)
                        if (sname.IndexOf(kv.Key, StringComparison.Ordinal) >= 0) { center = kv.Value; break; }
                    if (center == null) continue;
                    if (!string.IsNullOrWhiteSpace(onlyCh) &&
                        !string.Equals(center.Name, onlyCh, StringComparison.OrdinalIgnoreCase)) continue;

                    var pts = new List<Point2d>();
                    try
                    {
                        ObjectIdCollection ids = s.ExtractBorder(
                            Autodesk.Civil.SurfaceExtractionSettingsType.Model);
                        foreach (ObjectId eid in ids)
                        {
                            var ent = tr.GetObject(eid, OpenMode.ForWrite) as Entity;
                            if (ent == null) continue;
                            var local = new List<Point2d>();
                            var pl = ent as Polyline;
                            var p3 = ent as Polyline3d;
                            if (pl != null)
                                for (int k = 0; k < pl.NumberOfVertices; k++) local.Add(pl.GetPoint2dAt(k));
                            else if (p3 != null)
                                foreach (ObjectId vid in p3)
                                {
                                    var v = tr.GetObject(vid, OpenMode.ForRead) as PolylineVertex3d;
                                    if (v != null) local.Add(new Point2d(v.Position.X, v.Position.Y));
                                }
                            double area = McwArea(local);
                            ent.Erase();
                            if (area < minArea) continue;      // fragment
                            pts.AddRange(local);
                        }
                    }
                    catch (System.Exception) { }
                    if (pts.Count < 3) continue;

                    // project and bucket
                    double len = center.Length;
                    int nb = Math.Max(1, (int)Math.Ceiling(len / interval));
                    var L = new double[nb];
                    var R = new double[nb];
                    var hit = new int[nb];
                    int outside = 0;
                    foreach (Point2d p in pts)
                    {
                        double st = 0, off = 0; bool oor = false;
                        try { center.StationOffsetAcceptOutOfRange(p.X, p.Y, ref st, ref off, ref oor); }
                        catch (System.Exception) { continue; }
                        if (oor) { outside++; continue; }
                        int bi = (int)((st - center.StartingStation) / interval);
                        if (bi < 0) bi = 0; if (bi >= nb) bi = nb - 1;
                        hit[bi]++;
                        if (off < 0) { if (-off > L[bi]) L[bi] = -off; }
                        else { if (off > R[bi]) R[bi] = off; }
                    }

                    var rows = new JsonArray();
                    double wMin = double.MaxValue, wMax = 0, wSum = 0; int wN = 0;
                    for (int i = 0; i < nb; i++)
                    {
                        if (hit[i] == 0) continue;
                        double w = L[i] + R[i];
                        rows.Add(new JsonObject
                        {
                            ["station"] = Round(center.StartingStation + i * interval, 1),
                            ["left"] = Round(L[i], 2),
                            ["right"] = Round(R[i], 2),
                            ["width"] = Round(w, 2),
                            ["points"] = hit[i]
                        });
                        if (w < wMin) wMin = w;
                        if (w > wMax) wMax = w;
                        wSum += w; wN++;
                    }

                    channels.Add(new JsonObject
                    {
                        ["channel"] = center.Name,
                        ["surface"] = sname,
                        ["length"] = Round(len, 3),
                        ["interval"] = interval,
                        ["buckets"] = rows.Count,
                        ["width_min"] = wN > 0 ? Round(wMin, 2) : 0,
                        ["width_max"] = Round(wMax, 2),
                        ["width_mean"] = wN > 0 ? Round(wSum / wN, 2) : 0,
                        ["points_out_of_range"] = outside,
                        ["profile"] = rows
                    });
                }
                tr.Commit();
            }
            return new JsonObject { ["count"] = channels.Count, ["channels"] = channels };
        }

        static double McwArea(List<Point2d> p)
        {
            if (p.Count < 3) return 0.0;
            double s = 0.0;
            for (int i = 0; i < p.Count; i++)
            {
                Point2d A = p[i], B = p[(i + 1) % p.Count];
                s += A.X * B.Y - B.X * A.Y;
            }
            return Math.Abs(s) / 2.0;
        }
    }
}
