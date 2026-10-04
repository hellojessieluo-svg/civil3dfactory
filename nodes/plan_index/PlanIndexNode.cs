using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodePlanIndex(JsonObject a, Document doc)
        {
            string[] layouts = LpStrings(a, "layouts", null);
            if (layouts == null) throw new InvalidOperationException("Specify layouts:[layout_name] to define the viewport sheet set.");
            string[] only = LpStrings(a, "viewports", null);
            var codes = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            if (a["items"] is JsonArray ia)
                foreach (JsonNode n in ia) { var io = n as JsonObject; if (io != null) codes[Need(io, "viewport")] = io; }
            string tpl = GetString(a, "layer_template", "C-CHNL-{code}-{kind}");
            string bndLayer = GetString(a, "boundary_layer", null);
            var skeleton = LpWild(GetString(a, "skeleton_layers", "C-CHNL-TOP,C-CHNL-*-TOP"));
            double W = GetDouble(a, "width", 84), Hb = GetDouble(a, "height", 60), M = GetDouble(a, "margin", 6);
            double band = GetDouble(a, "title_band", 8), th = GetDouble(a, "text_height", 3.5);
            string title = GetString(a, "title", "Plan index (schematic)");
            string caption = GetString(a, "caption", "This sheet: {name}");
            bool mask = GetBool(a, "mask", true);
            string layer = GetString(a, "layer", "C-ANNO-INDX");
            string styleName = GetString(a, "text_style", "txt1");
            string tag = GetString(a, "tag", "plan_index");
            string crossTag = GetString(a, "avoid_tag", "annotate_cross_cut");
            bool clear = GetBool(a, "clear", true), dry = GetBool(a, "dry_run", false);

            Database db = doc.Database;
            var outItems = new JsonArray(); int cleared = 0, made = 0; var notes = new List<string>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId styleId = LpTextStyle(tr, db, styleName);
                var sheets = new List<KeyValuePair<BlockTableRecord, LpView>>();
                foreach (string ln in layouts)
                {
                    var btr = LpSpace(tr, db, ln, OpenMode.ForWrite);
                    if (btr.Name.Equals(BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("layouts must contain layout names.");
                    foreach (var v in LpLayoutViews(tr, btr))
                        if (only == null || Array.IndexOf(only, v.Handle) >= 0) sheets.Add(new KeyValuePair<BlockTableRecord, LpView>(btr, v));
                }
                if (sheets.Count == 0) throw new InvalidOperationException("No matching viewports in these layouts.");

                var msCache = LpCache(tr, ModelSpace(db, tr), null);
                var bnd = new List<List<Point2d>>(); var skel = new List<List<Point2d>>();
                foreach (var it in msCache)
                {
                    if (it.Kind != 1) continue;
                    if (!string.IsNullOrEmpty(bndLayer) && string.Equals(it.Layer, bndLayer, StringComparison.OrdinalIgnoreCase)) bnd.Add(it.Pts);
                    else if (skeleton(it.Layer)) skel.Add(it.Pts);
                }
                if (a["boundary_points"] is JsonArray bp && bp.Count >= 3)
                {
                    var ring = new List<Point2d>(); foreach (JsonNode n in bp) { var q = (JsonArray)n; ring.Add(new Point2d(q[0].GetValue<double>(), q[1].GetValue<double>())); }
                    ring.Add(ring[0]); bnd.Add(ring);
                }
                double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue;
                foreach (var ring in (bnd.Count > 0 ? bnd : skel)) foreach (var q in ring) { bx0 = Math.Min(bx0, q.X); by0 = Math.Min(by0, q.Y); bx1 = Math.Max(bx1, q.X); by1 = Math.Max(by1, q.Y); }
                if (bx0 >= bx1 || by0 >= by1) throw new InvalidOperationException("Empty skeleton: no lines from boundary_layer, boundary_points or skeleton_layers.");

                var cleanedLayouts = new HashSet<ObjectId>();
                var pens = new Dictionary<ObjectId, LpPen>();
                ObjectId layerId = dry ? ObjectId.Null : LpEnsureLayer(tr, db, layer, 7);
                foreach (var sv in sheets)
                {
                    var btr = sv.Key; var v = sv.Value;
                    if (clear && !dry && cleanedLayouts.Add(btr.ObjectId)) cleared += LpClearTagged(tr, btr, tag);
                    LpPen pen = null;
                    if (!dry && !pens.TryGetValue(btr.ObjectId, out pen)) { pen = new LpPen(tr, db, btr, layerId, styleId, tag); pens[btr.ObjectId] = pen; }

                    var frame = v.Frame;
                    var sc = new LabelScene();
                    LpScene(msCache, v.ToPaper, frame, new LpSceneOpts { Blocks = false }, sc);
                    var ownPts = new List<LPt>();
                    JsonObject ci; codes.TryGetValue(v.Handle, out ci);
                    string code = ci == null ? null : GetString(ci, "code", null);
                    if (code != null)
                    {
                        var own = LpWild(tpl.Replace("{code}", code).Replace("{kind}", "*"));
                        Func<string, string> lay = kd => tpl.Replace("{code}", code).Replace("{kind}", kd);
                        var ownLine = LpWild(lay("TOP") + "," + lay("CNTR") + "," + lay("SAMP"));
                        foreach (var it in msCache)
                        {
                            if (!own(it.Layer)) continue;
                            if (it.Kind == 1 && ownLine(it.Layer)) foreach (var q in it.Pts) { var pp = v.ToPaper(q.X, q.Y); if (LabelGeo.Inside(frame, pp, 0)) ownPts.Add(pp); }
                            else if (it.Kind == 0) { var pp = v.ToPaper((it.X0 + it.X1) / 2, (it.Y0 + it.Y1) / 2); if (LabelGeo.Inside(frame, pp, 0)) ownPts.Add(pp); }
                        }
                    }
                    var blockers = new List<LBox>();
                    foreach (ObjectId id in btr)
                    {
                        var e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (e == null || e.IsErased) continue;
                        bool isCross = LpHasTag(e, crossTag), isBlk = e is BlockReference;
                        if (!isCross && !isBlk) continue;
                        try
                        {
                            var ex = e.GeometricExtents;
                            var b = new LBox(ex.MinPoint.X, ex.MinPoint.Y, ex.MaxPoint.X, ex.MaxPoint.Y);
                            if (isBlk && !isCross && b.W >= 300) continue;
                            blockers.Add(b);
                        }
                        catch { }
                    }
                    LBox box; double dens;
                    string corner = LabelPlacer.PickIndexCorner(frame, W, Hb, M, ownPts, sc.Texts, sc.All, blockers, out box, out dens);
                    string name = ci == null ? null : GetString(ci, "name", code);
                    string cap = string.IsNullOrEmpty(caption) || name == null ? null : caption.Replace("{name}", name).Replace("{code}", code ?? "");
                    outItems.Add(new JsonObject
                    {
                        ["viewport"] = v.Handle, ["corner"] = corner, ["density"] = dens, ["box"] = box.ToJson(),
                        ["caption"] = cap
                    });
                    if (pen == null) continue;

                    double x0 = box.X0, y0 = box.Y0, x1 = box.X1, y1 = box.Y1;
                    if (mask) pen.Wipe(x0, y0, x1, y1);
                    pen.Poly(new[] { new Point2d(x0, y0), new Point2d(x1, y0), new Point2d(x1, y1), new Point2d(x0, y1) }, true);
                    if (!string.IsNullOrEmpty(title))
                        pen.Text(x0 + W / 2, y1 - band + 2.0, title, th, 256, 1);
                    pen.Line(x0, y1 - band, x1, y1 - band);
                    double capBand = cap == null ? 0 : band - 2;
                    if (cap != null)
                    {
                        pen.Line(x0, y0 + capBand, x1, y0 + capBand);
                        pen.Text(x0 + W / 2, y0 + (capBand - th) / 2 + 0.25, cap, th, 256, 1);
                    }
                    double aw = W - 4, ah = Hb - band - capBand - 4;
                    double mpm = Math.Min(aw / (bx1 - bx0), ah / (by1 - by0));
                    double mx = (bx0 + bx1) / 2, my = (by0 + by1) / 2, kx = x0 + 2 + aw / 2, ky = y0 + 2 + capBand + ah / 2;
                    Func<double, double, Point2d> tp = (x, y) => new Point2d(kx + (x - mx) * mpm, ky + (y - my) * mpm);
                    foreach (var ring in bnd) { var pts = new List<Point2d>(); foreach (var q in ring) pts.Add(tp(q.X, q.Y)); pen.Poly(pts, false, 8); }
                    foreach (var ln in skel) { var pts = new List<Point2d>(); foreach (var q in ln) pts.Add(tp(q.X, q.Y)); pen.Poly(pts, false, 8); }
                    foreach (var su in sheets)
                    {
                        double wx0, wy0, wx1, wy1; su.Value.ModelWindow(out wx0, out wy0, out wx1, out wy1);
                        var r4 = new[] { tp(wx0, wy0), tp(wx1, wy0), tp(wx1, wy1), tp(wx0, wy1) };
                        if (su.Value.Id == v.Id) { pen.Solid(r4, null, 8); pen.Poly(r4, true, 7); }
                        else pen.Poly(r4, true, 8);
                    }
                }
                foreach (var pn in pens.Values) { string nt = pn.MoveToTop(); if (nt != null) notes.Add(nt); made += pn.Count; }
                tr.Commit();
            }
            return new JsonObject
            {
                ["dry_run"] = dry, ["indexes"] = outItems.Count, ["items"] = outItems,
                ["cleared"] = cleared, ["entities"] = made, ["note"] = notes.Count > 0 ? string.Join("; ", notes) : "Memory only; call save_dwg to persist."
            };
        }
    }
}
