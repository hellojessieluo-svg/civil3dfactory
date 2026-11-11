using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory
{
    /// <summary>
    /// Node locate_sheet_stations (read-only): recover a construction drawing's stations from
    /// the plan sheet itself. Each station text (regex with km and m groups) is paired with the
    /// nearest section line (LINE, layer/colour filter) within `search` that crosses a centreline
    /// (polylines on `centre_layer`); the crossing is the station point and the section line the
    /// cross direction, flipped to the left of increasing station. Use it when the sheet's
    /// 78 m apart). Optional half_width also returns ready sample lines and station equations.
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeLocateSheetStations(JsonObject a, Document doc)
        {
            string pattern = Need(a, "pattern");
            string centreLayer = Need(a, "centre_layer");
            string textLayer = GetString(a, "text_layer", null);
            int textColor = (int)GetDouble(a, "text_color", -1);
            string lineLayer = GetString(a, "line_layer", null);
            int lineColor = (int)GetDouble(a, "line_color", -1);
            double search = GetDouble(a, "search", 15);
            double half = GetDouble(a, "half_width", 0);
            string namePrefix = GetString(a, "name_prefix", "");
            var rx = new Regex("^" + pattern + "$");

            Database db = doc.Database;
            var centres = new List<Curve>();
            var lines = new List<Line>();
            var texts = new List<(double st, string txt, Point3d at)>();
            var unmatched = new JsonArray();
            var rows = new List<(double st, Point3d p, Vector3d u)>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    var e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (e == null) continue;
                    if (e is Curve c && !(e is Line) && string.Equals(e.Layer, centreLayer, StringComparison.OrdinalIgnoreCase))
                        centres.Add(c);
                    else if (e is Line ln
                             && (lineLayer == null || string.Equals(ln.Layer, lineLayer, StringComparison.OrdinalIgnoreCase))
                             && (lineColor < 0 || ln.ColorIndex == lineColor))
                        lines.Add(ln);
                    else if (e is DBText t
                             && (textLayer == null || string.Equals(t.Layer, textLayer, StringComparison.OrdinalIgnoreCase))
                             && (textColor < 0 || t.ColorIndex == textColor))
                    {
                        var m = rx.Match((t.TextString ?? "").Trim());
                        if (!m.Success) continue;
                        double st = double.Parse(m.Groups["km"].Value, CultureInfo.InvariantCulture) * 1000
                                  + double.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
                        texts.Add((st, t.TextString.Trim(), t.Position));
                    }
                }
                if (centres.Count == 0) throw new InvalidOperationException("No centreline curves on layer '" + centreLayer + "'.");

                // Plan geometry only: sheets mix elevations (texts, lines and polylines at
                // different Z), so distances and crossings are taken in the XY plane.
                var xy = new Plane(Point3d.Origin, Vector3d.ZAxis);
                var seen = new HashSet<double>();
                foreach (var t in texts)
                {
                    if (!seen.Add(Math.Round(t.st, 3))) continue;
                    double bestD = double.MaxValue; Line best = null; Point3d bestP = Point3d.Origin;
                    var at2 = new Point2d(t.at.X, t.at.Y);
                    foreach (Line ln in lines)
                    {
                        var seg = new LineSegment2d(new Point2d(ln.StartPoint.X, ln.StartPoint.Y), new Point2d(ln.EndPoint.X, ln.EndPoint.Y));
                        double d = seg.GetDistanceTo(at2);
                        if (d > search || d >= bestD) continue;
                        foreach (Curve c in centres)
                        {
                            var pts = new Point3dCollection();
                            ln.IntersectWith(c, Intersect.OnBothOperands, xy, pts, IntPtr.Zero, IntPtr.Zero);
                            if (pts.Count == 0) continue;
                            bestD = d; best = ln; bestP = new Point3d(pts[0].X, pts[0].Y, 0);
                            break;
                        }
                    }
                    if (best == null) { unmatched.Add(t.txt); continue; }
                    Vector3d u = (best.EndPoint - best.StartPoint); u = new Vector3d(u.X, u.Y, 0).GetNormal();
                    rows.Add((t.st, bestP, u));
                }
                tr.Commit();
            }
            if (unmatched.Count > 0 && GetBool(a, "strict", true))
                throw new InvalidOperationException("No section line crossing the centreline within " + search + " m of: " + unmatched.ToJsonString());
            rows.Sort((p, q) => p.st.CompareTo(q.st));

            var stations = new JsonArray();
            var sampleLines = new JsonArray();
            var equations = new JsonArray();
            for (int k = 0; k < rows.Count; k++)
            {
                var r = rows[k];
                Point3d pa = rows[Math.Max(0, k - 1)].p, pb = rows[Math.Min(rows.Count - 1, k + 1)].p;
                Vector3d tan = pb - pa;                         // increasing-station direction
                Vector3d u = r.u;
                if (u.X * -tan.Y + u.Y * tan.X < 0) u = -u;     // section direction to the left
                string name = StationName(namePrefix, r.st);
                stations.Add(new JsonObject
                {
                    ["station"] = r.st, ["name"] = name,
                    ["x"] = Math.Round(r.p.X, 4), ["y"] = Math.Round(r.p.Y, 4),
                    ["ux"] = Math.Round(u.X, 6), ["uy"] = Math.Round(u.Y, 6)
                });
                equations.Add(new JsonObject { ["x"] = Math.Round(r.p.X, 4), ["y"] = Math.Round(r.p.Y, 4), ["ahead"] = r.st });
                if (half > 0)
                    sampleLines.Add(new JsonObject
                    {
                        ["name"] = name,
                        ["points"] = new JsonArray(
                            new JsonArray(Math.Round(r.p.X + u.X * half, 3), Math.Round(r.p.Y + u.Y * half, 3)),
                            new JsonArray(Math.Round(r.p.X - u.X * half, 3), Math.Round(r.p.Y - u.Y * half, 3)))
                    });
            }
            var res = new JsonObject
            {
                ["texts_matched"] = texts.Count,
                ["stations_found"] = rows.Count,
                ["unmatched"] = unmatched,
                ["stations"] = stations,
                ["equations"] = equations
            };
            if (half > 0) res["sample_lines"] = sampleLines;
            return res;
        }

        /// <summary>"B2K0+045", "B2K3+581.69": whole metres as +000, otherwise two decimals.</summary>
        static string StationName(string prefix, double s)
        {
            int km = (int)Math.Floor(s / 1000 + 1e-9);
            double m = s - km * 1000;
            if (Math.Abs(m - Math.Round(m)) < 1e-6)
                return prefix + "K" + km + "+" + ((int)Math.Round(m)).ToString("000", CultureInfo.InvariantCulture);
            return prefix + "K" + km + "+" + m.ToString("000.00", CultureInfo.InvariantCulture);
        }
    }
}
