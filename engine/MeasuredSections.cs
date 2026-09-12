using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory
{
    /// <summary>
    /// Measured-section reader: surveyors deliver plain CAD drawings with many sections tiled on one sheet;
    /// using layer conventions plus tick-text calibration, each section is restored to a ground line in engineering coordinates (offset, elevation).
    ///
    /// Origin: the 2026-06 embankment-demolition plugin V1 (Civil3D-002-DLL / C3DFSection, three interactive commands
    /// C3DF-ExtractSections / C3DF-GenDesignLine / C3DF-CalcVolume). When moved into the factory,
    /// "window-select entities" became "scan model space by layer"; calibration and geometry were copied line by line.
    /// That algorithm was validated on real Project B drawings; do not change it on intuition.
    ///
    /// Three nodes share this file: extract_measured_sections / generate_demolition_design_lines /
    /// compute_embankment_demolition. This file does geometry only and reads no parameter keys (each node reads its own).
    /// </summary>
    public sealed class MeasuredSectionOptions
    {
        /// <summary>Layer of the ground lines, * wildcard supported (survey convention dmx*).</summary>
        public string GroundLayer = "dmx*";
        /// <summary>Layer of the chainage/elevation tick texts (convention zdmt*). The two are told apart by format:
        /// -0+012.5 style is an offset tick, a plain number is an elevation tick.</summary>
        public string ColumnLayer = "zdmt*";
        /// <summary>Layer of the section title texts (convention 1-Title*).</summary>
        public string TitleLayer = "1-Title*";
        /// <summary>Title regex; must contain the named groups ln (line name), km (kilometres), m (metres).</summary>
        public string TitleRegex = @"^(?<ln>.+?)-K(?<km>\d+)\+(?<m>\d+(?:\.\d+)?)Section$";
        /// <summary>Maximum offset calibration residual (m); above it the calibration fails.</summary>
        public double OffsetTolerance = 0.06;
        /// <summary>Maximum elevation calibration residual (m); above it the calibration fails.</summary>
        public double ElevTolerance = 0.02;
        /// <summary>How far below the ground-line bounding box to look for tick texts (drawing units).</summary>
        public double TableDepth = 80.0;
        /// <summary>X tolerance for pairing tick texts with ground-line vertices (drawing units).</summary>
        public double PairTolX = 3.0;
    }

    /// <summary>One section: calibration result + ground line in engineering coordinates.</summary>
    public sealed class MeasuredSection
    {
        public string Title = "";
        public string LineName = "";
        public double StakeM;
        /// <summary>Engineering value = k * drawing coordinate + b (one pair per axis).</summary>
        public double Kx, Bx, Ky, By;
        /// <summary>(offset, elevation) ascending, de-duplicated at 1 mm.</summary>
        public List<Point2d> Ground = new List<Point2d>();
        public bool Valid;
        public string Report = "";
        public double OffsetResidual = double.NaN;
        public double ElevResidual = double.NaN;
        public string GroundHandle = "";
        public int OffsetMarks, ElevMarks;

        public Point2d ToPaper(Point2d eng) => new Point2d((eng.X - Bx) / Kx, (eng.Y - By) / Ky);

        public Point2d ToEng(Point2d paper) => new Point2d(Kx * paper.X + Bx, Ky * paper.Y + By);

        public Point3d ToPaper3(Point2d eng)
        {
            Point2d p = ToPaper(eng);
            return new Point3d(p.X, p.Y, 0);
        }

        public double ElevAt(double x) => Sections.Interp(Ground, x);

        /// <summary>Station string, e.g. K0+250.0 of A2-K0+250.</summary>
        public string Stake
        {
            get
            {
                int km = (int)Math.Floor(StakeM / 1000.0);
                double rem = StakeM - km * 1000.0;
                return "K" + km.ToString(CultureInfo.InvariantCulture) + "+" +
                       rem.ToString("000.0", CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>Section calibration and section geometry algorithms (stripping / excavation / slopes / areas).</summary>
    public static class Sections
    {
        // ---------- Parameter validation and filtering (shared by three nodes; each node reads its own keys so contracts stay aligned) ----------

        public static void Check(MeasuredSectionOptions o)
        {
            if (string.IsNullOrWhiteSpace(o.GroundLayer))
                throw new InvalidOperationException("ground_layer must not be empty.");
            if (o.OffsetTolerance <= 0 || o.ElevTolerance <= 0)
                throw new InvalidOperationException("offset_tolerance / elev_tolerance must be greater than 0.");
            if (o.TableDepth <= 0) throw new InvalidOperationException("table_depth must be greater than 0.");
            if (o.PairTolX <= 0) throw new InvalidOperationException("pair_tol_x must be greater than 0.");
        }

        /// <summary>line_filter -> regex; empty means no filtering.</summary>
        public static Regex CompileFilter(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return null;
            try { return new Regex(pattern); }
            catch (ArgumentException ex)
            { throw new InvalidOperationException("line_filter is not a valid regex: " + ex.Message); }
        }

        public static bool Matches(MeasuredSection s, Regex filter)
            => filter == null || filter.IsMatch(s.LineName ?? "") || filter.IsMatch(s.Title ?? "");

        /// <summary>NaN/infinity always become null; never write sentinel values into the report as numbers.</summary>
        public static JsonNode Num(double v, int decimals)
            => double.IsNaN(v) || double.IsInfinity(v) ? null : (JsonNode)Math.Round(v, decimals);

        public static string F(double v, int decimals)
            => v.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        // ---------- Reading and calibration ----------

        public static bool LayerMatch(string layer, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            return Regex.IsMatch(layer ?? "",
                "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$",
                RegexOptions.IgnoreCase);
        }

        /// <summary>Scans model space and restores all sections by layer convention (including failed calibrations, which the caller reports).</summary>
        public static List<MeasuredSection> Read(Database db, Transaction tr, MeasuredSectionOptions o)
        {
            var grounds = new List<Polyline>();
            var colTexts = new List<DBText>();
            var titles = new List<DBText>();

            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                var pl = obj as Polyline;
                if (pl != null)
                {
                    if (LayerMatch(pl.Layer, o.GroundLayer) && pl.NumberOfVertices >= 2) grounds.Add(pl);
                    continue;
                }
                var tx = obj as DBText;
                if (tx == null) continue;
                if (LayerMatch(tx.Layer, o.ColumnLayer)) colTexts.Add(tx);
                if (LayerMatch(tx.Layer, o.TitleLayer) && tx.TextString.Contains("Section")) titles.Add(tx);
            }

            var offsetRe = new Regex(@"^(-?)(\d+)\+(\d+(?:\.\d+)?)$");
            Regex titleRe;
            try { titleRe = new Regex(o.TitleRegex); }
            catch (ArgumentException ex)
            { throw new InvalidOperationException("title_regex is not a valid regex: " + ex.Message); }

            var list = new List<MeasuredSection>();
            foreach (Polyline g in grounds)
            {
                var s = new MeasuredSection();
                s.GroundHandle = g.Handle.ToString();
                Extents3d ext = g.GeometricExtents;
                double x0 = ext.MinPoint.X - 10, x1 = ext.MaxPoint.X + 10, yTop = ext.MinPoint.Y;

                var verts = Enumerable.Range(0, g.NumberOfVertices)
                    .Select(i => g.GetPoint2dAt(i)).OrderBy(p => p.X).ToList();

                var offs = new List<(double vx, double val)>();
                var els = new List<(double vy, double val)>();
                foreach (DBText t in colTexts.Where(t => t.Position.X >= x0 && t.Position.X <= x1
                                                      && t.Position.Y < yTop
                                                      && t.Position.Y > yTop - o.TableDepth))
                {
                    Point2d v = verts.OrderBy(p => Math.Abs(p.X - t.Position.X)).First();
                    if (Math.Abs(v.X - t.Position.X) > o.PairTolX) continue;
                    string str = t.TextString.Trim();
                    Match m = offsetRe.Match(str);
                    if (m.Success)
                        offs.Add((v.X, (m.Groups[1].Value == "-" ? -1 : 1)
                            * (double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 1000
                               + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture))));
                    else if (double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out double e))
                        els.Add((v.Y, e));
                }

                DBText title = titles
                    .Where(t => t.Position.X >= x0 && t.Position.X <= x1 && t.Position.Y < yTop)
                    .OrderByDescending(t => t.Position.Y).FirstOrDefault();
                s.Title = title == null ? "(no title)" : title.TextString.Trim();
                Match tm = titleRe.Match(s.Title);
                if (tm.Success)
                {
                    s.LineName = tm.Groups["ln"].Value;
                    s.StakeM = double.Parse(tm.Groups["km"].Value, CultureInfo.InvariantCulture) * 1000
                             + double.Parse(tm.Groups["m"].Value, CultureInfo.InvariantCulture);
                }

                s.OffsetMarks = offs.Count;
                s.ElevMarks = els.Count;
                if (offs.Count < 2 || els.Count < 2)
                {
                    s.Report = "Not enough pairs (" + offs.Count + " offset ticks, " + els.Count + " elevation ticks; need >=2 of each)";
                    list.Add(s);
                    continue;
                }

                (s.Kx, s.Bx) = Fit(offs.Select(t => (t.vx, t.val)).ToList());
                (s.Ky, s.By) = Fit(els.Select(t => (t.vy, t.val)).ToList());

                s.OffsetResidual = offs.Max(t => Math.Abs(s.Kx * t.vx + s.Bx - t.val));
                s.ElevResidual = els.Max(t => Math.Abs(s.Ky * t.vy + s.By - t.val));
                s.Valid = s.OffsetResidual <= o.OffsetTolerance && s.ElevResidual <= o.ElevTolerance;
                s.Report = string.Format(CultureInfo.InvariantCulture,
                    "H 1:{0:F1} V 1:{1:F1} offset residual {2:F3} elev residual {3:F3}",
                    s.Kx * 1000, s.Ky * 1000, s.OffsetResidual, s.ElevResidual);

                foreach (Point2d v in verts)   // convert to engineering coordinates; consecutive duplicates removed at 1 mm
                {
                    var p = new Point2d(s.Kx * v.X + s.Bx, s.Ky * v.Y + s.By);
                    if (s.Ground.Count == 0
                        || Math.Abs(p.X - s.Ground[s.Ground.Count - 1].X) > 0.001
                        || Math.Abs(p.Y - s.Ground[s.Ground.Count - 1].Y) > 0.001)
                        s.Ground.Add(p);
                }
                list.Add(s);
            }
            return list.OrderBy(x => x.LineName).ThenBy(x => x.StakeM).ToList();
        }

        /// <summary>Least-squares line fit y = kx + b.</summary>
        static (double k, double b) Fit(List<(double x, double y)> p)
        {
            double n = p.Count, sx = p.Sum(t => t.x), sy = p.Sum(t => t.y);
            double sxx = p.Sum(t => t.x * t.x), sxy = p.Sum(t => t.x * t.y);
            double k = (n * sxy - sx * sy) / (n * sxx - sx * sx);
            return (k, (sy - k * sx) / n);
        }

        // ---------- Polyline basics ----------

        public static double Interp(List<Point2d> pts, double x)
        {
            if (pts == null || pts.Count == 0) return double.NaN;
            if (x <= pts[0].X) return pts[0].Y;
            for (int i = 0; i < pts.Count - 1; i++)
                if (x <= pts[i + 1].X)
                {
                    double dx = pts[i + 1].X - pts[i].X;
                    return dx < 1e-9 ? pts[i + 1].Y
                         : pts[i].Y + (x - pts[i].X) / dx * (pts[i + 1].Y - pts[i].Y);
                }
            return pts[pts.Count - 1].Y;
        }

        /// <summary>All maximal intervals of the point list lying above level.</summary>
        public static List<(double a, double b)> RegionsAbove(List<Point2d> pts, double level)
        {
            var res = new List<(double a, double b)>();
            if (pts == null || pts.Count < 2) return res;

            var xs = new List<double> { pts[0].X, pts[pts.Count - 1].X };
            for (int i = 0; i < pts.Count - 1; i++)
            {
                double z1 = pts[i].Y - level, z2 = pts[i + 1].Y - level;
                double dx = pts[i + 1].X - pts[i].X;
                if (z1 * z2 < 0)
                    xs.Add(dx > 1e-9 ? pts[i].X + z1 * dx / (z1 - z2) : pts[i].X);
            }
            xs = xs.Distinct().OrderBy(x => x).ToList();
            for (int i = 0; i < xs.Count - 1; i++)
                if (xs[i + 1] - xs[i] > 1e-6 && Interp(pts, (xs[i] + xs[i + 1]) / 2) > level)
                {
                    if (res.Count > 0 && Math.Abs(res[res.Count - 1].b - xs[i]) < 1e-9)
                        res[res.Count - 1] = (res[res.Count - 1].a, xs[i + 1]);
                    else res.Add((xs[i], xs[i + 1]));
                }
            return res;
        }

        /// <summary>Stripping intervals: offset ranges where ground elevation > threshold.</summary>
        public static List<(double a, double b)> StripIntervals(MeasuredSection s, double thr)
            => RegionsAbove(s.Ground, thr).Where(r => r.b - r.a > 0.01).ToList();

        /// <summary>Stripping line: ground offset down by t, plus horizontal extensions at elevation (thr-t) on both ends until they meet the ground line.</summary>
        public static List<Point2d> StripLine(MeasuredSection s, double a, double b, double thr, double t)
        {
            List<Point2d> g = s.Ground;
            double lvl = thr - t;
            var pts = new List<Point2d>();

            double A = FindCross(g, a, -1, lvl, thr);
            if (double.IsNaN(A))                    // fallback: cannot reach the ground -> close vertically
            {
                if (a > g[0].X + 1e-9) pts.Add(new Point2d(a, s.ElevAt(a)));
                pts.Add(new Point2d(a, s.ElevAt(a) - t));
            }
            else { pts.Add(new Point2d(A, lvl)); pts.Add(new Point2d(a, lvl)); }

            foreach (Point2d v in g.Where(p => p.X > a + 1e-6 && p.X < b - 1e-6))
                pts.Add(new Point2d(v.X, v.Y - t));

            double B = FindCross(g, b, +1, lvl, thr);
            if (double.IsNaN(B))
            {
                pts.Add(new Point2d(b, s.ElevAt(b) - t));
                if (b < g[g.Count - 1].X - 1e-9) pts.Add(new Point2d(b, s.ElevAt(b)));
            }
            else { pts.Add(new Point2d(b, lvl)); pts.Add(new Point2d(B, lvl)); }
            return pts;
        }

        /// <summary>From x0 along dir, finds the first intersection of the ground line with elevation lvl;
        /// returns NaN if the ground rises back above stopAbove on the way (entering an adjacent stripping zone).</summary>
        public static double FindCross(List<Point2d> g, double x0, int dir, double lvl, double stopAbove)
        {
            if (dir > 0)
            {
                for (int i = 0; i < g.Count - 1; i++)
                {
                    if (g[i + 1].X <= x0 + 1e-9) continue;
                    double x1 = Math.Max(g[i].X, x0), x2 = g[i + 1].X;
                    double z1 = Interp(g, x1), z2 = g[i + 1].Y;
                    if ((z1 - lvl) * (z2 - lvl) <= 0 && Math.Abs(z1 - z2) > 1e-12)
                        return x1 + (z1 - lvl) * (x2 - x1) / (z1 - z2);
                    if (z2 >= stopAbove) return double.NaN;
                }
            }
            else
            {
                for (int i = g.Count - 1; i > 0; i--)
                {
                    if (g[i - 1].X >= x0 - 1e-9) continue;
                    double x1 = Math.Min(g[i].X, x0), x2 = g[i - 1].X;
                    double z1 = Interp(g, x1), z2 = g[i - 1].Y;
                    if ((z1 - lvl) * (z2 - lvl) <= 0 && Math.Abs(z1 - z2) > 1e-12)
                        return x1 + (z1 - lvl) * (x2 - x1) / (z1 - z2);
                    if (z2 >= stopAbove) return double.NaN;
                }
            }
            return double.NaN;
        }

        /// <summary>Projects a 1:m slope outward from (sgn*W, H0) until it meets the stripped surface; appends a note to warn if it never touches down.</summary>
        public static List<Point2d> SlopeOut(List<Point2d> S, int sgn, double H0, double W,
                                            double m, ref string warn)
        {
            var prof = new List<Point2d> { new Point2d(0, Interp(S, 0)) };
            if (sgn > 0) prof.AddRange(S.Where(p => p.X > 1e-9));
            else prof.AddRange(S.Where(p => p.X < -1e-9)
                                .Select(p => new Point2d(-p.X, p.Y)).Reverse());

            var res = new List<Point2d> { new Point2d(W, H0) };
            bool hit = false;
            for (int i = 0; i < prof.Count - 1 && !hit; i++)
            {
                if (prof[i + 1].X <= W + 1e-9) continue;
                double d1 = Math.Max(prof[i].X, W), d2 = prof[i + 1].X;
                if (d2 - d1 < 1e-9) continue;
                double f1 = Interp(prof, d1) - (H0 + (d1 - W) / m);
                double f2 = prof[i + 1].Y - (H0 + (d2 - W) / m);
                if (f1 > 0 && f2 <= 0)
                {
                    double di = d1 + f1 * (d2 - d1) / (f1 - f2);
                    res.Add(new Point2d(di, H0 + (di - W) / m));
                    hit = true;
                }
            }
            if (!hit) { warn += (sgn > 0 ? "right" : "left") + " slope did not reach ground;"; res.Add(prof[prof.Count - 1]); }
            var pts = res.Select(p => new Point2d(sgn * p.X, p.Y)).ToList();
            if (sgn < 0) pts.Reverse();
            return pts;
        }

        /// <summary>Removes the vertical closures at both ends of the stripping line, keeping only the offset segment.</summary>
        public static List<Point2d> Trimmed(List<Point2d> pts)
        {
            var r = new List<Point2d>(pts);
            while (r.Count > 1 && Math.Abs(r[0].X - r[1].X) < 1e-6) r.RemoveAt(0);
            while (r.Count > 1 && Math.Abs(r[r.Count - 1].X - r[r.Count - 2].X) < 1e-6)
                r.RemoveAt(r.Count - 1);
            return r;
        }

        /// <summary>Stripped surface = ground line with stripping ranges replaced by the offset segment of the stripping line (with vertical steps).</summary>
        public static List<Point2d> ComposeSurface(MeasuredSection s, List<List<Point2d>> strips)
        {
            var segs = (strips ?? new List<List<Point2d>>())
                .Select(Trimmed).Where(o => o.Count >= 2).OrderBy(o => o[0].X).ToList();
            if (segs.Count == 0) return new List<Point2d>(s.Ground);

            var res = new List<Point2d>();
            double x = s.Ground[0].X - 1;
            foreach (List<Point2d> o in segs)
            {
                double a = o[0].X, b = o[o.Count - 1].X;
                res.AddRange(s.Ground.Where(p => p.X > x + 1e-9 && p.X < a - 1e-9));
                res.Add(new Point2d(a, s.ElevAt(a)));
                res.AddRange(o);
                res.Add(new Point2d(b, s.ElevAt(b)));
                x = b;
            }
            res.AddRange(s.Ground.Where(p => p.X > x + 1e-9));
            return res;
        }

        /// <summary>Area between two x-monotone polylines (over the span of lower, exact trapezoidal integration).</summary>
        public static double AreaBetween(List<Point2d> upper, List<Point2d> lower)
        {
            if (lower == null || lower.Count < 2 || upper == null || upper.Count < 2) return 0;
            double a = lower[0].X, b = lower[lower.Count - 1].X;
            var xs = upper.Select(p => p.X).Concat(lower.Select(p => p.X))
                          .Where(x => x >= a - 1e-9 && x <= b + 1e-9)
                          .Distinct().OrderBy(x => x).ToList();
            double area = 0;
            for (int i = 0; i < xs.Count - 1; i++)
            {
                double dx = xs[i + 1] - xs[i];
                if (dx < 1e-9) continue;
                double f1 = Interp(upper, xs[i]) - Interp(lower, xs[i]);
                double f2 = Interp(upper, xs[i + 1]) - Interp(lower, xs[i + 1]);
                area += (f1 + f2) / 2 * dx;
            }
            return area;
        }

        // ---------- Drawing <-> engineering coordinates ----------

        /// <summary>Drawing polyline -> engineering-coordinate points (X ascending, duplicates removed).</summary>
        public static List<Point2d> EngPoints(Polyline pl, MeasuredSection s)
        {
            var res = new List<Point2d>();
            for (int i = 0; i < pl.NumberOfVertices; i++)
            {
                Point2d e = s.ToEng(pl.GetPoint2dAt(i));
                if (res.Count == 0 || Math.Abs(e.X - res[res.Count - 1].X) > 1e-6
                                   || Math.Abs(e.Y - res[res.Count - 1].Y) > 1e-6)
                    res.Add(e);
            }
            if (res.Count > 1 && res[0].X > res[res.Count - 1].X) res.Reverse();
            return res;
        }

        /// <summary>Ownership test: the line's bounding-box centre lies inside the section's ground-line drawing bounding box (expanded by margin).</summary>
        public static List<Polyline> OwnedBy(List<Polyline> pls, MeasuredSection s, double margin)
        {
            var pts = s.Ground.Select(p => s.ToPaper(p)).ToList();
            double x0 = pts.Min(p => p.X) - margin, x1 = pts.Max(p => p.X) + margin;
            double y0 = pts.Min(p => p.Y) - margin, y1 = pts.Max(p => p.Y) + margin;
            return pls.Where(p =>
            {
                Extents3d c = p.GeometricExtents;
                double cx = (c.MinPoint.X + c.MaxPoint.X) / 2;
                double cy = (c.MinPoint.Y + c.MaxPoint.Y) / 2;
                return cx >= x0 && cx <= x1 && cy >= y0 && cy <= y1;
            }).ToList();
        }

        /// <summary>Engineering-coordinate points -> drawing polyline on the given layer.</summary>
        public static ObjectId Draw(BlockTableRecord space, Transaction tr,
                                    List<Point2d> eng, MeasuredSection s, ObjectId layer)
        {
            if (eng == null || eng.Count < 2) return ObjectId.Null;
            var pl = new Polyline();
            for (int i = 0; i < eng.Count; i++)
                pl.AddVertexAt(i, s.ToPaper(eng[i]), 0, 0, 0);
            pl.LayerId = layer;
            ObjectId id = space.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
            return id;
        }
    }
}
