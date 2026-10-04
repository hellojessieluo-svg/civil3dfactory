using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        const string LpRegApp = "C3DF_ANNO";


        static double[] LpDoubles(JsonObject a, string key, double[] dflt)
        {
            var arr = a[key] as JsonArray;
            if (arr == null || arr.Count == 0) return dflt;
            var r = new double[arr.Count];
            for (int i = 0; i < arr.Count; i++) r[i] = arr[i].GetValue<double>();
            return r;
        }

        static string[] LpStrings(JsonObject a, string key, string[] dflt)
        {
            var arr = a[key] as JsonArray;
            if (arr == null || arr.Count == 0) return dflt;
            var r = new List<string>();
            foreach (JsonNode n in arr) if (n != null) r.Add(n.ToString());
            return r.ToArray();
        }

        static Func<string, bool> LpWild(string patterns)
        {
            if (string.IsNullOrWhiteSpace(patterns)) return s => false;
            var parts = new List<Regex>();
            foreach (string p in patterns.Split(','))
            {
                string t = p.Trim(); if (t.Length == 0) continue;
                parts.Add(new Regex("^" + Regex.Escape(t).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase));
            }
            return s => { foreach (var r in parts) if (r.IsMatch(s ?? "")) return true; return false; };
        }

        static JsonArray LpXY(double x, double y) { return new JsonArray(Math.Round(x, 3), Math.Round(y, 3)); }


        static BlockTableRecord LpSpace(Transaction tr, Database db, string space, OpenMode mode)
        {
            if (string.IsNullOrEmpty(space) || string.Equals(space, "Model", StringComparison.OrdinalIgnoreCase))
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                return (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], mode);
            }
            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            if (!dict.Contains(space)) throw new InvalidOperationException("Layout '" + space + "'.");
            var lay = (Layout)tr.GetObject(dict.GetAt(space), OpenMode.ForRead);
            return (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, mode);
        }

        internal sealed class LpView
        {
            public ObjectId Id; public string Handle;
            public double Cx, Cy, Hw, Hh, Vx, Vy, S;
            public LBox Frame { get { return new LBox(Cx - Hw, Cy - Hh, Cx + Hw, Cy + Hh); } }
            public LPt ToPaper(double x, double y) { return new LPt(Cx + (x - Vx) * S, Cy + (y - Vy) * S); }
            public Point2d ToModel(LPt p) { return new Point2d((p.X - Cx) / S + Vx, (p.Y - Cy) / S + Vy); }
            public void ModelWindow(out double x0, out double y0, out double x1, out double y1)
            { x0 = Vx - Hw / S; y0 = Vy - Hh / S; x1 = Vx + Hw / S; y1 = Vy + Hh / S; }
        }

        static LpView LpReadView(Transaction tr, Database db, string handle, ObjectId layoutBtrId)
        {
            ObjectId id = ResolveHandle(db, handle);
            var vp = tr.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Viewport;
            if (vp == null) throw new InvalidOperationException("Handle " + handle + " is not a viewport.");
            if (!layoutBtrId.IsNull && vp.OwnerId != layoutBtrId) throw new InvalidOperationException("Viewport " + handle + " is not in the specified layout.");
            return LpFromViewport(vp);
        }

        static LpView LpFromViewport(Autodesk.AutoCAD.DatabaseServices.Viewport vp)
        {
            if (Math.Abs(vp.TwistAngle) > 1e-9) throw new InvalidOperationException("Viewport " + vp.Handle + " has a twist angle; only untwisted plan views are supported.");
            if (!vp.ViewDirection.IsParallelTo(Vector3d.ZAxis)) throw new InvalidOperationException("Viewport " + vp.Handle + " is not a plan view.");
            if (vp.CustomScale <= 0) throw new InvalidOperationException("Viewport " + vp.Handle + " has zero scale.");
            return new LpView
            {
                Id = vp.ObjectId, Handle = vp.Handle.ToString(),
                Cx = vp.CenterPoint.X, Cy = vp.CenterPoint.Y, Hw = vp.Width / 2, Hh = vp.Height / 2,
                Vx = vp.ViewCenter.X + vp.ViewTarget.X, Vy = vp.ViewCenter.Y + vp.ViewTarget.Y, S = vp.CustomScale
            };
        }

        static List<LpView> LpLayoutViews(Transaction tr, BlockTableRecord layoutBtr)
        {
            var r = new List<LpView>();
            foreach (ObjectId id in layoutBtr)
            {
                var vp = tr.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Viewport;
                if (vp == null || vp.Number == 1 || vp.Width < 1e-6) continue;
                try { r.Add(LpFromViewport(vp)); } catch { }
            }
            r.Sort((p, q) => p.Cx.CompareTo(q.Cx));
            return r;
        }


        static List<Point2d> LpCurvePts(Curve c)
        {
            var pts = new List<Point2d>();
            var pl = c as Polyline;
            if (pl != null)
            {
                int n = pl.NumberOfVertices; int segs = pl.Closed ? n : n - 1;
                if (n == 0) return pts;
                pts.Add(pl.GetPoint2dAt(0));
                for (int i = 0; i < segs; i++)
                {
                    double b = pl.GetBulgeAt(i);
                    Point2d a0 = pl.GetPoint2dAt(i), a1 = pl.GetPoint2dAt((i + 1) % n);
                    if (Math.Abs(b) > 1e-9)
                        foreach (var q in LabelGeo.ArcPoints(new LPt(a0.X, a0.Y), new LPt(a1.X, a1.Y), b)) pts.Add(new Point2d(q.X, q.Y));
                    pts.Add(a1);
                }
                return pts;
            }
            var ln = c as Line;
            if (ln != null) { pts.Add(new Point2d(ln.StartPoint.X, ln.StartPoint.Y)); pts.Add(new Point2d(ln.EndPoint.X, ln.EndPoint.Y)); return pts; }
            double L;
            try { L = c.GetDistanceAtParameter(c.EndParam) - c.GetDistanceAtParameter(c.StartParam); } catch { return pts; }
            if (L <= 1e-9) return pts;
            for (int i = 0; i <= 64; i++)
            {
                try { Point3d q = c.GetPointAtDist(Math.Min(L, L * i / 64)); pts.Add(new Point2d(q.X, q.Y)); } catch { }
            }
            return pts;
        }

        static bool LpTextBox(Entity e, out Extents3d box, out string text, out bool isMText)
        {
            box = new Extents3d(); text = null; isMText = false;
            var t = e as DBText; var m = e as MText; var ml = e as MLeader;
            try
            {
                if (t != null) { text = t.TextString; box = t.GeometricExtents; return true; }
                if (m != null) { text = m.Text; isMText = true; box = m.GeometricExtents; return true; }
                if (ml != null && ml.ContentType == ContentType.MTextContent && ml.MText != null)
                {
                    var mt = ml.MText; text = mt.Text; var loc = ml.TextLocation;
                    box = new Extents3d(new Point3d(loc.X, loc.Y - mt.ActualHeight, 0), new Point3d(loc.X + mt.ActualWidth, loc.Y, 0));
                    return true;
                }
            }
            catch { }
            return false;
        }

        static List<List<Point2d>> LpMLeaderLines(MLeader ml)
        {
            var r = new List<List<Point2d>>();
            try
            {
                foreach (int li in ml.GetLeaderIndexes())
                    foreach (int lli in ml.GetLeaderLineIndexes(li))
                    {
                        var seg = new List<Point2d>(); int nv = ml.VerticesCount(lli);
                        for (int vi = 0; vi < nv; vi++) { var v = ml.GetVertex(lli, vi); seg.Add(new Point2d(v.X, v.Y)); }
                        if (seg.Count >= 2) r.Add(seg);
                    }
            }
            catch { }
            return r;
        }

        static bool LpHasTag(Entity e, string tag)
        {
            ResultBuffer rb = null;
            try { rb = e.GetXDataForApplication(LpRegApp); } catch { }
            if (rb == null) return false;
            foreach (TypedValue tv in rb.AsArray())
                if (tv.TypeCode == (int)DxfCode.ExtendedDataAsciiString) return tag == null || string.Equals(tv.Value as string, tag, StringComparison.Ordinal);
            return false;
        }

        internal sealed class LpItem
        {
            public string Layer;
            public int Kind;
            public List<Point2d> Pts;
            public double X0, Y0, X1, Y1;
            public string Text;
            public bool IsMText, Closed;
        }

        /// <summary>
        /// </summary>
        static List<LpItem> LpCache(Transaction tr, IEnumerable<ObjectId> ids, string skipTag)
        {
            var r = new List<LpItem>();
            foreach (ObjectId id in ids)
            {
                Entity e; try { e = tr.GetObject(id, OpenMode.ForRead) as Entity; } catch { continue; }
                if (e == null || e.IsErased || e is Autodesk.AutoCAD.DatabaseServices.Viewport) continue;
                if (skipTag != null && LpHasTag(e, skipTag)) continue;
                string ly = e.Layer;
                Extents3d ex; string text; bool isM;
                if (LpTextBox(e, out ex, out text, out isM))
                {
                    r.Add(new LpItem { Layer = ly, Kind = 0, X0 = ex.MinPoint.X, Y0 = ex.MinPoint.Y, X1 = ex.MaxPoint.X, Y1 = ex.MaxPoint.Y, Text = text, IsMText = isM });
                    var ml = e as MLeader;
                    if (ml != null) foreach (var seg in LpMLeaderLines(ml)) r.Add(new LpItem { Layer = ly, Kind = 3, Pts = seg });
                    continue;
                }
                var br = e as BlockReference;
                if (br != null)
                {
                    try { var bex = br.GeometricExtents; r.Add(new LpItem { Layer = ly, Kind = 2, X0 = bex.MinPoint.X, Y0 = bex.MinPoint.Y, X1 = bex.MaxPoint.X, Y1 = bex.MaxPoint.Y, Text = br.Name }); }
                    catch { }
                    continue;
                }
                var c = e as Curve;
                if (c == null) continue;
                List<Point2d> pts; try { pts = LpCurvePts(c); } catch { continue; }
                if (pts.Count < 2) continue;
                bool closed = false; try { closed = c.Closed; } catch { }
                r.Add(new LpItem { Layer = ly, Kind = 1, Pts = pts, Closed = closed });
            }
            return r;
        }

        /// <summary>
        /// </summary>
        internal sealed class LpSceneOpts
        {
            public Func<string, bool> Major = s => false, Minor = null, Ignore = s => false, TextLayer = null, Region = s => false;
            public double Clearance = 0;
            public double MaxBlockMm = 100;
            public bool Blocks = true;
            public bool ShrinkMText = true;
            public double RegionMinArea = 0;
        }

        static double LpArea(List<Point2d> p)
        {
            double s = 0; int n = p.Count;
            for (int i = 0, j = n - 1; i < n; j = i++) s += p[j].X * p[i].Y - p[i].X * p[j].Y;
            return Math.Abs(s) / 2;
        }

        static LBox LpBox(Func<double, double, LPt> toP, double x0, double y0, double x1, double y1)
        {
            LPt a = toP(x0, y0), b = toP(x1, y1);
            return new LBox(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        static bool LpTouch(LBox b, LBox clip) { return b.X1 >= clip.X0 && b.X0 <= clip.X1 && b.Y1 >= clip.Y0 && b.Y0 <= clip.Y1; }

        static void LpScene(List<LpItem> cache, Func<double, double, LPt> toP, LBox clip, LpSceneOpts o, LabelScene sc)
        {
            foreach (var it in cache)
            {
                if (o.Ignore(it.Layer)) continue;
                if (it.Kind == 0)
                {
                    if (o.TextLayer != null && !o.TextLayer(it.Layer)) continue;
                    var bx = LpBox(toP, it.X0, it.Y0, it.X1, it.Y1);
                    if (!LpTouch(bx, clip)) continue;
                    if (it.IsMText && o.ShrinkMText) bx = LabelGeo.ShrinkMTextBox(bx, (it.Text ?? "").Length);
                    if (o.Clearance > 0) bx = new LBox(bx.X0 - o.Clearance, bx.Y0 - o.Clearance, bx.X1 + o.Clearance, bx.Y1 + o.Clearance);
                    sc.Texts.Add(bx);
                }
                else if (it.Kind == 2)
                {
                    if (!o.Blocks) continue;
                    var bx = LpBox(toP, it.X0, it.Y0, it.X1, it.Y1);
                    if (bx.W < o.MaxBlockMm && bx.H < o.MaxBlockMm && LpTouch(bx, clip)) sc.Texts.Add(bx);
                }
                else if (it.Kind == 3) LpAddPolyline(it.Pts, false, toP, clip, sc.Major, sc.All);
                else
                {
                    if (o.Region(it.Layer) && it.Closed && (o.RegionMinArea <= 0 || LpArea(it.Pts) >= o.RegionMinArea))
                    {
                        var ring = new List<LPt>(); foreach (var q in it.Pts) ring.Add(toP(q.X, q.Y));
                        sc.Regions.Add(ring);
                    }
                    List<LSeg> cls = o.Major(it.Layer) ? sc.Major : ((o.Minor == null || o.Minor(it.Layer)) ? sc.Minor : null);
                    LpAddPolyline(it.Pts, false, toP, clip, cls, sc.All);
                }
            }
        }

        static void LpAddPolyline(List<Point2d> pts, bool closed, Func<double, double, LPt> toP, LBox clip, List<LSeg> cls, List<LSeg> all)
        {
            int n = pts.Count; int segs = closed ? n : n - 1;
            for (int i = 0; i < segs; i++)
            {
                LPt a = toP(pts[i].X, pts[i].Y), b = toP(pts[(i + 1) % n].X, pts[(i + 1) % n].Y);
                if (Math.Max(a.X, b.X) < clip.X0 || Math.Min(a.X, b.X) > clip.X1 || Math.Max(a.Y, b.Y) < clip.Y0 || Math.Min(a.Y, b.Y) > clip.Y1) continue;
                var s = new LSeg(a, b);
                all.Add(s);
                if (cls != null) cls.Add(s);
            }
        }


        static ObjectId LpEnsureLayer(Transaction tr, Database db, string name, short color)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return lt[name];
            lt.UpgradeOpen();
            var rec = new LayerTableRecord { Name = name, Color = Color.FromColorIndex(ColorMethod.ByAci, color) };
            ObjectId id = lt.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
            lt.DowngradeOpen();
            return id;
        }

        static ObjectId LpTextStyle(Transaction tr, Database db, string name)
        {
            var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            if (!tst.Has(name))
                throw new InvalidOperationException("Text style not found in drawing: '" + name + "'; import the required text style from your drawing template.");
            return tst[name];
        }

        static double LpStyleWf(Transaction tr, ObjectId styleId)
        {
            if (styleId.IsNull) return 1.0;
            try { var st = (TextStyleTableRecord)tr.GetObject(styleId, OpenMode.ForRead); return st.XScale > 0 ? st.XScale : 1.0; }
            catch { return 1.0; }
        }

        static int LpClearTagged(Transaction tr, BlockTableRecord btr, string tag)
        {
            var kill = new List<ObjectId>();
            foreach (ObjectId id in btr)
            {
                Entity e; try { e = tr.GetObject(id, OpenMode.ForRead) as Entity; } catch { continue; }
                if (e != null && !e.IsErased && LpHasTag(e, tag)) kill.Add(id);
            }
            foreach (ObjectId id in kill) ((Entity)tr.GetObject(id, OpenMode.ForWrite)).Erase();
            return kill.Count;
        }

        internal sealed class LpPen
        {
            readonly Transaction _tr; readonly Database _db; readonly BlockTableRecord _btr; readonly ObjectId _layer, _style; readonly string _tag;
            public readonly ObjectIdCollection Ids = new ObjectIdCollection();
            public readonly double StyleWf = 1.0;
            public LpPen(Transaction tr, Database db, BlockTableRecord btr, ObjectId layerId, ObjectId styleId, string tag)
            {
                _tr = tr; _db = db; _btr = btr; _layer = layerId; _style = styleId; _tag = tag;
                EnsureRegApp(tr, db, LpRegApp);
                StyleWf = LpStyleWf(tr, styleId);
            }
            public int Count { get { return Ids.Count; } }

            public Entity Put(Entity e, short color = 256)
            {
                e.SetDatabaseDefaults();
                e.LayerId = _layer;
                if (color != 256) e.Color = Color.FromColorIndex(ColorMethod.ByAci, color);
                _btr.AppendEntity(e);
                _tr.AddNewlyCreatedDBObject(e, true);
                e.XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, LpRegApp),
                                           new TypedValue((int)DxfCode.ExtendedDataAsciiString, _tag));
                Ids.Add(e.ObjectId);
                return e;
            }

            public void Line(double x1, double y1, double x2, double y2, short color = 256)
            { Put(new Line(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0)), color); }

            /// <summary>
            /// </summary>
            public double Text(double x, double y, string s, double h, short color = 256, int align = 0)
            {
                var t = new DBText();
                Put(t, color);
                t.TextString = s;
                if (!_style.IsNull)
                {
                    t.TextStyleId = _style;
                    if (StyleWf > 0) t.WidthFactor = StyleWf;
                }
                t.Height = h;
                if (align == 0) t.Position = new Point3d(x, y, 0);
                else
                {
                    t.HorizontalMode = align == 1 ? TextHorizontalMode.TextCenter : TextHorizontalMode.TextRight;
                    t.VerticalMode = TextVerticalMode.TextBase;
                    t.AlignmentPoint = new Point3d(x, y, 0);
                    try { t.AdjustAlignment(_db); } catch { }
                }
                try { var ex = t.GeometricExtents; return ex.MaxPoint.X - ex.MinPoint.X; } catch { return -1; }
            }

            public void Poly(IList<Point2d> pts, bool closed, short color = 256)
            {
                var pl = new Polyline();
                for (int i = 0; i < pts.Count; i++) pl.AddVertexAt(i, pts[i], 0, 0, 0);
                pl.Closed = closed;
                Put(pl, color);
            }

            public void Solid(IList<Point2d> ring, IList<double> bulges, short color = 256)
            {
                var h = new Hatch();
                Put(h, color);
                h.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
                var pc = new Point2dCollection(); var bc = new DoubleCollection();
                for (int i = 0; i < ring.Count; i++) { pc.Add(ring[i]); bc.Add(bulges == null ? 0 : bulges[i]); }
                pc.Add(ring[0]); bc.Add(0);
                h.AppendLoop(HatchLoopTypes.Default, pc, bc);
                h.EvaluateHatch(true);
            }

            public void Dot(double x, double y, double r, short color = 256)
            {
                Solid(new[] { new Point2d(x - r, y), new Point2d(x + r, y) }, new[] { 1.0, 1.0 }, color);
            }

            public void Wipe(double x0, double y0, double x1, double y1)
            {
                var wo = new Wipeout();
                wo.SetDatabaseDefaults();
                wo.SetFrom(new Point2dCollection { new Point2d(x0, y0), new Point2d(x1, y0), new Point2d(x1, y1), new Point2d(x0, y1), new Point2d(x0, y0) }, Vector3d.ZAxis);
                Put(wo);
            }

            public string MoveToTop()
            {
                if (Ids.Count == 0) return null;
                try { var dot = (DrawOrderTable)_tr.GetObject(_btr.DrawOrderTableId, OpenMode.ForWrite); dot.MoveToTop(Ids); return null; }
                catch (System.Exception ex) { return "Bring-to-front failed: " + ex.Message; }
            }
        }

        // ============================ label_place ============================

        static readonly double[] LpDefaultLengthsH = { 2, 3, 4, 5, 6, 8, 10, 12, 15, 18 };

        /// <summary>
        /// </summary>
        static void LpDrawNormal(LpPen pen, LabelRequest rq, LabelResult res, double scale)
        {
            double H = rq.H, ex = res.End.X * scale, ey = res.End.Y * scale, maxw = 0;
            for (int j = 0; j < rq.Lines.Length; j++)
            {
                double x = (res.Right ? res.End.X + 0.3 * H : res.End.X - 0.3 * H) * scale;
                double w = pen.Text(x, res.LinePos[j].Y * scale, rq.Lines[j], H * scale, 256, res.Right ? 0 : 2);
                if (w < 0) w = LabelGeo.TextWidth(rq.Lines[j], H, rq.Wf) * scale;
                maxw = Math.Max(maxw, w);
            }
            double len = maxw + 0.6 * H * scale;
            pen.Line(rq.Anchor.X * scale, rq.Anchor.Y * scale, ex, ey);
            pen.Line(ex, ey, res.Right ? ex + len : ex - len, ey);
        }

        static void LpDrawBoundary(LpPen pen, LabelRequest rq, LabelPlacer.BoundaryPlaced bp, bool right, double scale)
        {
            double H = rq.H, col = right ? bp.BaseA.X : bp.BaseB.X, maxw = 0;
            for (int j = 0; j < rq.Lines.Length; j++)
            {
                double w = pen.Text((right ? col + 0.3 : col - 0.3) * scale, bp.LinePos[j].Y * scale, rq.Lines[j], H * scale, 256, right ? 0 : 2);
                if (w < 0) w = LabelGeo.TextWidth(rq.Lines[j], H, rq.Wf) * scale;
                maxw = Math.Max(maxw, w);
            }
            double y = bp.BaseA.Y * scale, len = maxw + 0.6 * scale;
            pen.Line(bp.Anchor.X * scale, bp.Anchor.Y * scale, bp.LeaderEnd.X * scale, bp.LeaderEnd.Y * scale);
            pen.Line(col * scale, y, (right ? col + len / scale : col - len / scale) * scale, y);
        }

        static JsonNode RunNodeLabelPlace(JsonObject a, Document doc)
        {
            var labels = a["labels"] as JsonArray;
            if (labels == null || labels.Count == 0) throw new InvalidOperationException("Specify labels:[{anchor:[x,y], text}].");
            string space = GetString(a, "space", "Model");
            string mode = GetString(a, "mode", "normal");
            if (mode != "normal" && mode != "boundary") throw new InvalidOperationException("mode must be normal or boundary.");
            double scale = GetDouble(a, "scale", 1.0);
            if (scale <= 0) throw new InvalidOperationException("scale must be positive (drawing units per paper mm; 1 in layouts).");
            double H = GetDouble(a, "text_height", 3.5);
            string layer = GetString(a, "layer", "C-ANNO-TEXT");
            string styleName = GetString(a, "text_style", "txt1");
            string tag = GetString(a, "tag", "label_place");
            bool clear = GetBool(a, "clear", true), dry = GetBool(a, "dry_run", false);
            var w = LabelWeights.PlanDefaults().Apply(a["weights"] as JsonObject);
            var o = new LpSceneOpts
            {
                Major = LpWild(GetString(a, "major_layers", null)),
                Minor = a["minor_layers"] == null ? null : LpWild(GetString(a, "minor_layers", null)),
                Ignore = LpWild(GetString(a, "ignore_layers", null)),
                TextLayer = a["text_layers"] == null ? null : LpWild(GetString(a, "text_layers", null)),
                Region = LpWild(GetString(a, "region_layers", null)),
                Clearance = GetDouble(a, "clearance", 0), Blocks = GetBool(a, "block_obstacles", true),
                RegionMinArea = GetDouble(a, "region_min_area", 0)
            };
            double k = 1.0 / scale;
            Func<double, double, LPt> toP = (x, y) => new LPt(x * k, y * k);

            var reqs = new List<LabelRequest>();
            foreach (JsonNode n in labels)
            {
                var lo = n as JsonObject; var an = lo?["anchor"] as JsonArray;
                if (an == null || an.Count < 2) throw new InvalidOperationException("Each label requires anchor:[x,y].");
                string txt = Need(lo, "text");
                reqs.Add(new LabelRequest { Anchor = toP(an[0].GetValue<double>(), an[1].GetValue<double>()), Lines = txt.Split('\n'), H = H });
            }
            double[] lens = LpDoubles(a, "lengths", null);
            if (lens == null) { lens = new double[LpDefaultLengthsH.Length]; for (int i = 0; i < lens.Length; i++) lens[i] = LpDefaultLengthsH[i] * H; }
            double reach = 0; foreach (var L in lens) reach = Math.Max(reach, L);
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var r in reqs) { x0 = Math.Min(x0, r.Anchor.X); y0 = Math.Min(y0, r.Anchor.Y); x1 = Math.Max(x1, r.Anchor.X); y1 = Math.Max(y1, r.Anchor.Y); }
            double pad = reach + 40 * H;
            var clip = new LBox(x0 - pad, y0 - pad, x1 + pad, y1 + pad);
            if (mode == "boundary")
            {
                double cx = GetDouble(a, "column_x", double.NaN);
                if (double.IsNaN(cx)) throw new InvalidOperationException("boundary mode requires column_x (column origin in drawing units).");
                clip = new LBox(Math.Min(clip.X0, cx * k - pad), clip.Y0, Math.Max(clip.X1, cx * k + pad), clip.Y1);
            }

            Database db = doc.Database;
            int cleared = 0; var items = new JsonArray(); var withdrawn = new JsonArray(); int crossings = 0; string note = null; int made = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var btr = LpSpace(tr, db, space, OpenMode.ForWrite);
                ObjectId styleId = LpTextStyle(tr, db, styleName);
                double swf = LpStyleWf(tr, styleId);
                foreach (var rq0 in reqs) rq0.Wf = swf;
                if (clear && !dry) cleared = LpClearTagged(tr, btr, tag);
                var sc = new LabelScene();
                var win = a["window"] as JsonObject;
                if (win != null)
                    sc.Frame = new LBox(GetDouble(win, "minx", 0) * k, GetDouble(win, "miny", 0) * k, GetDouble(win, "maxx", 0) * k, GetDouble(win, "maxy", 0) * k);
                sc.FrameMargin = GetDouble(a, "frame_margin", 3.0);
                var ids = new List<ObjectId>(); foreach (ObjectId id in btr) ids.Add(id);
                LpScene(LpCache(tr, ids, clear ? tag : null), toP, clip, o, sc);

                LpPen pen = null;
                if (!dry) pen = new LpPen(tr, db, btr, LpEnsureLayer(tr, db, layer, 3), styleId, tag);
                if (mode == "normal")
                {
                    string[] dirs = LpStrings(a, "directions", LabelPlacer.DirOrder);
                    double exempt = GetDouble(a, "anchor_exempt", 1.5);
                    var placedBoxes = new List<LBox>(); var placedLeaders = new List<LSeg>();
                    for (int i = 0; i < reqs.Count; i++)
                    {
                        var rq = reqs[i];
                        var res = LabelPlacer.PlaceNormal(sc, rq, w, lens, dirs, placedBoxes, placedLeaders, exempt);
                        if (!res.Placed)
                        {
                            var why = new JsonObject(); foreach (var kv in res.Why) why[kv.Key] = kv.Value;
                            withdrawn.Add(new JsonObject
                            {
                                ["index"] = i, ["text"] = string.Join("\\n", rq.Lines), ["anchor"] = LpXY(rq.Anchor.X * scale, rq.Anchor.Y * scale),
                                ["best_cost"] = res.Dir == null ? null : (JsonNode)Math.Round(res.Cost, 2),
                                ["reasons"] = why
                            });
                            continue;
                        }
                        placedBoxes.Add(res.Box); placedLeaders.Add(new LSeg(rq.Anchor, res.End));
                        items.Add(new JsonObject
                        {
                            ["index"] = i, ["text"] = string.Join("\\n", rq.Lines), ["anchor"] = LpXY(rq.Anchor.X * scale, rq.Anchor.Y * scale),
                            ["end"] = LpXY(res.End.X * scale, res.End.Y * scale), ["dir"] = res.Dir, ["leader_mm"] = Math.Round(res.LeaderMm, 2),
                            ["cost"] = Math.Round(res.Cost, 2), ["box"] = res.Box.ToJson(scale)
                        });
                        if (pen != null)
                        {
                            LpDrawNormal(pen, rq, res, scale);
                        }
                    }
                }
                else
                {
                    double colX = GetDouble(a, "column_x", 0) * k;
                    bool right = GetString(a, "side", "right") != "left";
                    double yMin = GetDouble(a, "y_min", double.NegativeInfinity) * k, yMax = GetDouble(a, "y_max", double.PositiveInfinity) * k;
                    var rows = new List<LabelPlacer.BoundaryRow>();
                    for (int i = 0; i < reqs.Count; i++)
                        rows.Add(new LabelPlacer.BoundaryRow { Index = i, Anchor = reqs[i].Anchor, Lines = reqs[i].Lines, RowH = 5.5 / 3.5 * H + (reqs[i].Lines.Length - 1) * 1.6 * H });
                    var placed = LabelPlacer.PlaceBoundary(rows, colX, right, H, yMin, yMax, out crossings, swf);
                    foreach (var bp in placed)
                    {
                        var rq = reqs[bp.Index];
                        items.Add(new JsonObject
                        {
                            ["index"] = bp.Index, ["text"] = string.Join("\\n", rq.Lines), ["anchor"] = LpXY(bp.Anchor.X * scale, bp.Anchor.Y * scale),
                            ["end"] = LpXY(bp.LeaderEnd.X * scale, bp.LeaderEnd.Y * scale)
                        });
                        if (pen != null)
                        {
                            LpDrawBoundary(pen, rq, bp, right, scale);
                        }
                    }
                }
                if (pen != null) { note = pen.MoveToTop(); made = pen.Count; }
                tr.Commit();
            }
            return new JsonObject
            {
                ["mode"] = mode, ["space"] = space, ["dry_run"] = dry,
                ["placed"] = items.Count, ["withdrawn"] = withdrawn, ["items"] = items,
                ["crossings"] = crossings, ["cleared"] = cleared, ["entities"] = made, ["weights"] = w.ToJson(),
                ["note"] = note ?? "Memory only; call save_dwg to persist."
            };
        }
    }
}
