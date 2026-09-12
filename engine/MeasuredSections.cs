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
    /// 测量断面读取引擎——测量单位给的「一张图平铺多个断面」纯 CAD 图纸，
    /// 靠图层约定 + 刻度文字标定，把每个断面还原成工程坐标(偏距,高程)地面线。
    ///
    /// 来源：2026-06 的拆堤插件 V1（Civil3D-002-DLL / C3DFSection，三条交互命令
    /// C3DF-ExtractSections / C3DF-GenDesignLine / C3DF-CalcVolume）。搬进工厂时把
    /// 「窗选实体」换成「按图层扫模型空间」，其余标定与几何算法逐行照搬——
    /// 那套算法在项目B实图上验过，不要凭直觉改。
    ///
    /// 三个节点共用本文件：extract_measured_sections / generate_demolition_design_lines /
    /// compute_embankment_demolition。本文件只做几何，不读任何参数键（参数在各节点自己读）。
    /// </summary>
    public sealed class MeasuredSectionOptions
    {
        /// <summary>地面线所在图层，支持 * 通配（测量图惯例 dmx*）。</summary>
        public string GroundLayer = "dmx*";
        /// <summary>里程/高程刻度文字所在图层（惯例 zdmt*）。两者靠格式区分：
        /// 形如 -0+012.5 的是偏距刻度，纯数字的是高程刻度。</summary>
        public string ColumnLayer = "zdmt*";
        /// <summary>断面图名文字所在图层（惯例 1-图名*）。</summary>
        public string TitleLayer = "1-图名*";
        /// <summary>图名解析式，须含命名组 ln(测线名) km(公里) m(米)。</summary>
        public string TitleRegex = @"^(?<ln>.+?)-K(?<km>\d+)\+(?<m>\d+(?:\.\d+)?)断面$";
        /// <summary>偏距标定最大残差(m)，超了判标定不通过。</summary>
        public double OffsetTolerance = 0.06;
        /// <summary>高程标定最大残差(m)，超了判标定不通过。</summary>
        public double ElevTolerance = 0.02;
        /// <summary>自地面线包围盒下沿向下找刻度文字的深度（图纸单位）。</summary>
        public double TableDepth = 80.0;
        /// <summary>刻度文字与地面线顶点的 X 配对容差（图纸单位）。</summary>
        public double PairTolX = 3.0;
    }

    /// <summary>一个断面：标定结果 + 工程坐标地面线。</summary>
    public sealed class MeasuredSection
    {
        public string Title = "";
        public string LineName = "";
        public double StakeM;
        /// <summary>工程值 = k×图纸坐标 + b（横纵各一组）。</summary>
        public double Kx, Bx, Ky, By;
        /// <summary>(偏距,高程) 升序、已按 1mm 去重。</summary>
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

        /// <summary>桩号串，如 A2-K0+250 的 K0+250.0。</summary>
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

    /// <summary>断面标定与断面几何（清表/开挖/放坡/面积）算法集合。</summary>
    public static class Sections
    {
        // ---------- 参数校验与筛选（三节点共用；参数键各节点自己读，契约才对得上）----------

        public static void Check(MeasuredSectionOptions o)
        {
            if (string.IsNullOrWhiteSpace(o.GroundLayer))
                throw new InvalidOperationException("ground_layer 不能为空。");
            if (o.OffsetTolerance <= 0 || o.ElevTolerance <= 0)
                throw new InvalidOperationException("offset_tolerance / elev_tolerance 必须大于 0。");
            if (o.TableDepth <= 0) throw new InvalidOperationException("table_depth 必须大于 0。");
            if (o.PairTolX <= 0) throw new InvalidOperationException("pair_tol_x 必须大于 0。");
        }

        /// <summary>line_filter → 正则；空则不筛。</summary>
        public static Regex CompileFilter(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return null;
            try { return new Regex(pattern); }
            catch (ArgumentException ex)
            { throw new InvalidOperationException("line_filter 不是合法正则：" + ex.Message); }
        }

        public static bool Matches(MeasuredSection s, Regex filter)
            => filter == null || filter.IsMatch(s.LineName ?? "") || filter.IsMatch(s.Title ?? "");

        /// <summary>NaN/∞ 一律出 null，别把哨兵值当数字写进汇报。</summary>
        public static JsonNode Num(double v, int decimals)
            => double.IsNaN(v) || double.IsInfinity(v) ? null : (JsonNode)Math.Round(v, decimals);

        public static string F(double v, int decimals)
            => v.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        // ---------- 读图与标定 ----------

        public static bool LayerMatch(string layer, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            return Regex.IsMatch(layer ?? "",
                "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$",
                RegexOptions.IgnoreCase);
        }

        /// <summary>扫模型空间，按图层约定还原全部断面（含标定未通过的，交由调用方汇报）。</summary>
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
                if (LayerMatch(tx.Layer, o.TitleLayer) && tx.TextString.Contains("断面")) titles.Add(tx);
            }

            var offsetRe = new Regex(@"^(-?)(\d+)\+(\d+(?:\.\d+)?)$");
            Regex titleRe;
            try { titleRe = new Regex(o.TitleRegex); }
            catch (ArgumentException ex)
            { throw new InvalidOperationException("title_regex 不是合法正则：" + ex.Message); }

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
                s.Title = title == null ? "(无图名)" : title.TextString.Trim();
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
                    s.Report = "配对不足（偏距刻度 " + offs.Count + " 个、高程刻度 " + els.Count + " 个，各需 ≥2）";
                    list.Add(s);
                    continue;
                }

                (s.Kx, s.Bx) = Fit(offs.Select(t => (t.vx, t.val)).ToList());
                (s.Ky, s.By) = Fit(els.Select(t => (t.vy, t.val)).ToList());

                s.OffsetResidual = offs.Max(t => Math.Abs(s.Kx * t.vx + s.Bx - t.val));
                s.ElevResidual = els.Max(t => Math.Abs(s.Ky * t.vy + s.By - t.val));
                s.Valid = s.OffsetResidual <= o.OffsetTolerance && s.ElevResidual <= o.ElevTolerance;
                s.Report = string.Format(CultureInfo.InvariantCulture,
                    "横1:{0:F1} 纵1:{1:F1} 里程残差{2:F3} 高程残差{3:F3}",
                    s.Kx * 1000, s.Ky * 1000, s.OffsetResidual, s.ElevResidual);

                foreach (Point2d v in verts)   // 转工程坐标，连续重复点按 1mm 去重
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

        /// <summary>最小二乘直线拟合 y = kx + b。</summary>
        static (double k, double b) Fit(List<(double x, double y)> p)
        {
            double n = p.Count, sx = p.Sum(t => t.x), sy = p.Sum(t => t.y);
            double sxx = p.Sum(t => t.x * t.x), sxy = p.Sum(t => t.x * t.y);
            double k = (n * sxy - sx * sy) / (n * sxx - sx * sx);
            return (k, (sy - k * sx) / n);
        }

        // ---------- 折线基础 ----------

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

        /// <summary>点列上高于 level 的全部极大区间。</summary>
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

        /// <summary>清表区间：地面高程 > 阈值的偏距段。</summary>
        public static List<(double a, double b)> StripIntervals(MeasuredSection s, double thr)
            => RegionsAbove(s.Ground, thr).Where(r => r.b - r.a > 0.01).ToList();

        /// <summary>清表线：地面下移 t 的偏移段 + 两端在高程(thr-t)处水平延伸至与地面线相接。</summary>
        public static List<Point2d> StripLine(MeasuredSection s, double a, double b, double thr, double t)
        {
            List<Point2d> g = s.Ground;
            double lvl = thr - t;
            var pts = new List<Point2d>();

            double A = FindCross(g, a, -1, lvl, thr);
            if (double.IsNaN(A))                    // 兜底：接不到地面 → 竖直收口
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

        /// <summary>从 x0 沿 dir 找地面线与高程 lvl 的第一个交点；
        /// 途中地面又升回 stopAbove 以上（进入相邻清表区）则返回 NaN。</summary>
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

        /// <summary>从 (sgn·W, H0) 沿 1:m 向外放坡至与清表后表面相交；未接地时 warn 累加提示。</summary>
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
            if (!hit) { warn += (sgn > 0 ? "右" : "左") + "坡未接地;"; res.Add(prof[prof.Count - 1]); }
            var pts = res.Select(p => new Point2d(sgn * p.X, p.Y)).ToList();
            if (sgn < 0) pts.Reverse();
            return pts;
        }

        /// <summary>去掉清表线两端的竖直收口，只留偏移段。</summary>
        public static List<Point2d> Trimmed(List<Point2d> pts)
        {
            var r = new List<Point2d>(pts);
            while (r.Count > 1 && Math.Abs(r[0].X - r[1].X) < 1e-6) r.RemoveAt(0);
            while (r.Count > 1 && Math.Abs(r[r.Count - 1].X - r[r.Count - 2].X) < 1e-6)
                r.RemoveAt(r.Count - 1);
            return r;
        }

        /// <summary>清表后表面 = 地面线，清表段替换为清表线的偏移段（带竖直台阶）。</summary>
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

        /// <summary>两条 x 单调折线之间的面积（按 lower 的跨度，梯形精确积分）。</summary>
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

        // ---------- 图纸 ↔ 工程坐标 ----------

        /// <summary>图纸多段线 → 工程坐标点列（X 升序，去重复点）。</summary>
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

        /// <summary>归属判断：线包围盒中心落在该断面地面线图纸包围盒（外扩 margin）内。</summary>
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

        /// <summary>工程坐标点列 → 图纸多段线，画进指定图层。</summary>
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
