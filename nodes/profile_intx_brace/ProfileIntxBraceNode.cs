using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivProfileView = Autodesk.Civil.DatabaseServices.ProfileView;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        sealed class IntxHit { public string Name; public double S0, S1, DMin; public string Side; public string Handle; }

        static JsonNode RunNodeProfileIntxBrace(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string bndLayer = GetString(a, "boundary_layer", "C-CHNL-INTX-BNDY");
            string fromDwg = GetString(a, "from_dwg", null);
            var names = a["names"] as JsonObject;
            string nameLayer = GetString(a, "name_layer", null);
            string[] viewNames = LpStrings(a, "profile_views", null);
            double axisStep = GetDouble(a, "axis_step", 2.0);
            if (axisStep <= 0) throw new InvalidOperationException("axis_step must be positive.");
            double maxDist = GetDouble(a, "max_dist", 90), band = GetDouble(a, "near_band", 15);
            double minSpan = GetDouble(a, "min_span", 20), minVis = GetDouble(a, "min_visible", 30), extend = GetDouble(a, "extend", 80);
            double levelGiven = GetDouble(a, "level", double.NaN), levelStep = GetDouble(a, "level_step", 1.5);
            double H = GetDouble(a, "text_height", 3.5);
            string spanFmt = GetString(a, "label_format", "{name} intersection ({side}, {s0}~{s1})");
            string pointFmt = GetString(a, "point_format", "{name} intersection");
            string layer = GetString(a, "layer", "C-PROF-LABL");
            string styleName = GetString(a, "text_style", "txt1");
            string tag = GetString(a, "tag", "profile_intx_brace:" + alName);
            bool clear = GetBool(a, "clear", true), dry = GetBool(a, "dry_run", false);

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            double scale = GetDouble(a, "scale", double.NaN);
            if (double.IsNaN(scale))
            {
                try { var cs = db.Cannoscale; scale = cs.PaperUnits > 0 ? cs.DrawingUnits / cs.PaperUnits : 1; } catch { scale = 1; }
            }
            if (scale <= 0) throw new InvalidOperationException("scale must be positive.");

            var hits = new List<IntxHit>(); var viewsOut = new JsonArray(); var items = new JsonArray();
            int cleared = 0, made = 0; string note = null;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = FindAlignment(tr, civ, alName);
                if (al == null) throw new InvalidOperationException("Alignment '" + alName + "'.");
                double sa = al.StartingStation, sb = al.EndingStation;
                var ax = new List<double[]>();
                for (double s = sa; ; s += axisStep)
                {
                    double st = Math.Min(s, sb), x = 0, y = 0;
                    al.PointLocation(st, 0, ref x, ref y);
                    ax.Add(new[] { st, x, y });
                    if (st >= sb) break;
                }
                if (ax.Count < 2) throw new InvalidOperationException("Alignment too short to sample its axis.");
                double smax = sb;
                Func<Point2d, double[]> station = P =>
                {
                    double bestD = double.MaxValue, bestS = 0, side = 0;
                    for (int i = 0; i < ax.Count - 1; i++)
                    {
                        double s0 = ax[i][0], x0 = ax[i][1], y0 = ax[i][2], s1 = ax[i + 1][0], x1 = ax[i + 1][1], y1 = ax[i + 1][2];
                        double dx = x1 - x0, dy = y1 - y0, L2 = dx * dx + dy * dy; if (L2 < 1e-12) continue;
                        double t = ((P.X - x0) * dx + (P.Y - y0) * dy) / L2, tc = Math.Max(0, Math.Min(1, t));
                        double qx = x0 + tc * dx, qy = y0 + tc * dy, d = Math.Sqrt((P.X - qx) * (P.X - qx) + (P.Y - qy) * (P.Y - qy));
                        if (d < bestD) { bestD = d; bestS = s0 + t * (s1 - s0); side = dx * (P.Y - y0) - dy * (P.X - x0); }
                    }
                    return new[] { bestS, bestD, side };
                };

                var polys = new List<KeyValuePair<string, List<Point2d>>>();
                var verts = new Dictionary<string, List<Point2d>>();
                var nameTexts = new List<KeyValuePair<Point2d, string>>();
                Action<Database, Transaction> readPolys = (sdb, str) =>
                {
                    var bt = (BlockTable)str.GetObject(sdb.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)str.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        var e = str.GetObject(id, OpenMode.ForRead) as Entity; if (e == null) continue;
                        var c = e as Curve;
                        if (c != null && string.Equals(e.Layer, bndLayer, StringComparison.OrdinalIgnoreCase))
                        {
                            bool closed = false; try { closed = c.Closed; } catch { }
                            if (closed)
                            {
                                polys.Add(new KeyValuePair<string, List<Point2d>>(e.Handle.ToString(), LpCurvePts(c)));
                                var plv = c as Polyline;
                                if (plv != null) { var vl = new List<Point2d>(); for (int vi = 0; vi < plv.NumberOfVertices; vi++) vl.Add(plv.GetPoint2dAt(vi)); verts[e.Handle.ToString()] = vl; }
                            }
                        }
                        if (!string.IsNullOrEmpty(nameLayer) && string.Equals(e.Layer, nameLayer, StringComparison.OrdinalIgnoreCase))
                        {
                            var t = e as DBText; var m = e as MText;
                            if (t != null) nameTexts.Add(new KeyValuePair<Point2d, string>(new Point2d(t.Position.X, t.Position.Y), t.TextString.Trim()));
                            else if (m != null) nameTexts.Add(new KeyValuePair<Point2d, string>(new Point2d(m.Location.X, m.Location.Y), m.Text.Trim()));
                        }
                    }
                };
                if (string.IsNullOrEmpty(fromDwg)) readPolys(db, tr);
                else
                {
                    if (!File.Exists(fromDwg)) throw new InvalidOperationException("from_dwg does not exist: " + fromDwg);
                    using (var sdb = new Database(false, true))
                    {
                        sdb.ReadDwgFile(fromDwg, FileOpenMode.OpenForReadAndAllShare, true, null); sdb.CloseInput(true);
                        using (var str = sdb.TransactionManager.StartTransaction()) { readPolys(sdb, str); str.Commit(); }
                    }
                }
                if (polys.Count == 0) throw new InvalidOperationException("Layer '" + bndLayer + "' contains no closed intersection boundaries.");

                int auto = 0;
                foreach (var kv in polys)
                {
                    var ring = kv.Value; if (ring.Count < 3) continue;
                    var st = new List<double[]>(); foreach (var q in ring) st.Add(station(q));
                    double dmin = double.MaxValue; foreach (var r in st) dmin = Math.Min(dmin, r[1]);
                    if (dmin > maxDist) continue;
                    var ss = new List<double>();
                    foreach (var r in st) if (r[1] <= dmin + band && r[0] >= sa - extend && r[0] <= smax + extend) ss.Add(r[0]);
                    if (ss.Count == 0) continue;
                    double s0 = Math.Max(IntxMin(ss), sa), s1 = Math.Min(IntxMax(ss), smax);
                    if (s1 - s0 < minSpan && dmin > 10) continue;
                    List<Point2d> vv; if (!verts.TryGetValue(kv.Key, out vv) || vv.Count == 0) vv = ring;
                    double cx = 0, cy = 0; foreach (var q in vv) { cx += q.X; cy += q.Y; } cx /= vv.Count; cy /= vv.Count;
                    string nm = null;
                    if (names != null && names[kv.Key] != null) nm = names[kv.Key].ToString();
                    if (nm == null && nameTexts.Count > 0)
                    {
                        var lp = new List<LPt>(); foreach (var q in ring) lp.Add(new LPt(q.X, q.Y));
                        foreach (var nt in nameTexts) if (LabelGeo.InPoly(new LPt(nt.Key.X, nt.Key.Y), lp)) { nm = nt.Value; break; }
                    }
                    if (nm == null) nm = "Intersection" + (++auto);
                    hits.Add(new IntxHit { Name = nm, S0 = s0, S1 = s1, DMin = dmin, Side = station(new Point2d(cx, cy))[2] > 0 ? "Left side" : "Right side", Handle = kv.Key });
                }
                hits.Sort((p, q) => p.S0.CompareTo(q.S0));

                var views = new List<CivProfileView>();
                foreach (ObjectId pvId in al.GetProfileViewIds())
                {
                    var pv = (CivProfileView)tr.GetObject(pvId, OpenMode.ForRead);
                    if (viewNames != null && Array.IndexOf(viewNames, pv.Name) < 0) continue;
                    views.Add(pv);
                }
                if (views.Count == 0) throw new InvalidOperationException("Alignment '" + alName + "' has no matching profile views.");

                var ms0 = LpSpace(tr, db, "Model", OpenMode.ForWrite);
                ObjectId styleId = LpTextStyle(tr, db, styleName);
                if (clear && !dry) cleared = LpClearTagged(tr, ms0, tag);
                LpPen pen = dry ? null : new LpPen(tr, db, ms0, LpEnsureLayer(tr, db, layer, 3), styleId, tag);
                double h = H * scale;
                foreach (var pv in views)
                {
                    double a0 = pv.StationStart, a1 = pv.StationEnd;
                    double lv0 = double.IsNaN(levelGiven) ? pv.ElevationMax + 0.6 : levelGiven;
                    double e0 = pv.ElevationMin, e1 = pv.ElevationMax;
                    if (!(a1 > a0) || !(e1 > e0)) throw new InvalidOperationException("Profile view " + pv.Name + " has an empty station or elevation range.");
                    double qx0 = 0, qy0 = 0, qx1 = 0, qy1 = 0;
                    pv.FindXYAtStationAndElevation(a0, e0, ref qx0, ref qy0);
                    pv.FindXYAtStationAndElevation(a1, e1, ref qx1, ref qy1);
                    double kx = (qx1 - qx0) / (a1 - a0), ky = (qy1 - qy0) / (e1 - e0);
                    Func<double, double, Point2d> XY = (s, el) => new Point2d(qx0 + (s - a0) * kx, qy0 + (el - e0) * ky);
                    int lvl = 0, n = 0;
                    foreach (var ht in hits)
                    {
                        string k0 = IntxK(ht.S0), k1 = IntxK(ht.S1);
                        if (ht.S1 - ht.S0 < minSpan)
                        {
                            if (!(a0 - 15 <= ht.S0 && ht.S0 <= a1 + 15)) continue;
                            var pb = XY(ht.S0, lv0); var pl = XY(ht.S0, lv0 - 0.5); var pu = XY(ht.S0, lv0 + 0.5);
                            string label = IntxFmt(pointFmt, ht, k0, k1);
                            bool atStart = ht.S0 <= a0 + 15;
                            items.Add(new JsonObject { ["view"] = pv.Name, ["name"] = ht.Name, ["kind"] = "point", ["station"] = Math.Round(ht.S0, 2), ["text"] = label });
                            if (pen != null) { pen.Line(pl.X, pl.Y, pu.X, pu.Y); pen.Text(atStart ? pb.X + 1.5 * scale : pb.X - 1.5 * scale, pb.Y - h / 2, label, h, 256, atStart ? 0 : 2); }
                            n++;
                        }
                        else
                        {
                            double c0 = Math.Max(ht.S0, a0), c1 = Math.Min(ht.S1, a1);
                            if (c1 - c0 < minVis) continue;
                            double el = lv0 + levelStep * lvl; lvl++;
                            var pa = XY(c0, el); var pb = XY(c1, el);
                            string label = IntxFmt(spanFmt, ht, k0, k1);
                            items.Add(new JsonObject { ["view"] = pv.Name, ["name"] = ht.Name, ["kind"] = "span", ["from"] = Math.Round(c0, 2), ["to"] = Math.Round(c1, 2), ["elevation"] = Math.Round(el, 2), ["text"] = label });
                            if (pen != null)
                            {
                                pen.Line(pa.X, pa.Y, pb.X, pb.Y);
                                pen.Line(pa.X, pa.Y - 1.2 * scale, pa.X, pa.Y + 1.2 * scale);
                                pen.Line(pb.X, pb.Y - 1.2 * scale, pb.X, pb.Y + 1.2 * scale);
                                pen.Text((pa.X + pb.X) / 2, pa.Y + 1.0 * scale, label, h, 256, 1);
                            }
                            n++;
                        }
                    }
                    viewsOut.Add(new JsonObject { ["view"] = pv.Name, ["station_start"] = Math.Round(a0, 2), ["station_end"] = Math.Round(a1, 2), ["level"] = Math.Round(lv0, 2), ["marks"] = n });
                }
                if (pen != null) made = pen.Count;
                tr.Commit();
            }
            var ix = new JsonArray();
            foreach (var ht in hits)
                ix.Add(new JsonObject { ["name"] = ht.Name, ["handle"] = ht.Handle, ["from"] = Math.Round(ht.S0, 1), ["to"] = Math.Round(ht.S1, 1), ["side"] = ht.Side, ["dist_min"] = Math.Round(ht.DMin, 1) });
            return new JsonObject
            {
                ["alignment"] = alName, ["dry_run"] = dry, ["scale"] = scale, ["intersections"] = ix, ["views"] = viewsOut,
                ["items"] = items, ["cleared"] = cleared, ["entities"] = made, ["note"] = note ?? "Memory only; call save_dwg to persist."
            };
        }

        static double IntxMin(List<double> v) { double m = double.MaxValue; foreach (var x in v) m = Math.Min(m, x); return m; }
        static double IntxMax(List<double> v) { double m = double.MinValue; foreach (var x in v) m = Math.Max(m, x); return m; }

        static string IntxK(double s)
        {
            int km = (int)Math.Floor(s / 1000.0);
            return "K" + km.ToString(CultureInfo.InvariantCulture) + "+" + (s - km * 1000.0).ToString("000.00", CultureInfo.InvariantCulture);
        }

        static string IntxFmt(string f, IntxHit h, string k0, string k1)
        {
            return f.Replace("{name}", h.Name).Replace("{side}", h.Side).Replace("{s0}", k0).Replace("{s1}", k1);
        }
    }
}
