using System;
using System.Collections.Generic;
using System.Globalization;
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
        static readonly double[] CoordLengthsMm = { 6, 9, 12, 16, 20, 25, 30, 35, 40, 45, 50, 60, 70, 80, 95, 110 };   // gen_coords2 Lmm

        static JsonNode RunNodeAnnotateCoords(JsonObject a, Document doc)
        {
            string space = GetString(a, "space", "Model");
            string boundaryLayer = GetString(a, "boundary_layer", null);
            var handles = a["handles"] as JsonArray;
            var points = a["points"] as JsonArray;
            if (string.IsNullOrEmpty(boundaryLayer) && (handles == null || handles.Count == 0) && (points == null || points.Count == 0))
                throw new InvalidOperationException("Specify an anchor source: boundary_layer (closed polylines), handles, or points.");
            double scale = GetDouble(a, "scale", 1.0);
            if (scale <= 0) throw new InvalidOperationException("scale must be positive (drawing units per paper mm; use 15 for 1:15000 in meters).");
            double H = GetDouble(a, "text_height", 2.5);
            int dec = (int)GetDouble(a, "decimals", 3);
            string px = GetString(a, "prefix_x", "X="), py = GetString(a, "prefix_y", "Y=");
            double minGap = GetDouble(a, "min_gap", (points != null && points.Count > 0) ? 0 : 15);
            string layer = GetString(a, "layer", "C-ANNO-LABL-CORD");
            string styleName = GetString(a, "text_style", "txt1");
            string tag = GetString(a, "tag", "annotate_coords");
            bool clear = GetBool(a, "clear", true), dry = GetBool(a, "dry_run", false);
            var w = LabelWeights.CoordsDefaults().Apply(a["weights"] as JsonObject);
            string regionLayers = GetString(a, "region_layers", boundaryLayer);
            var o = new LpSceneOpts
            {
                Major = LpWild(GetString(a, "major_layers", "C-CHNL-TOP,C-CHNL-*-TOP")),
                Minor = LpWild(GetString(a, "minor_layers", "V-WATR-*")),
                Ignore = LpWild(GetString(a, "ignore_layers", null)),
                TextLayer = a["text_layers"] == null ? null : LpWild(GetString(a, "text_layers", null)),
                Region = LpWild(regionLayers),
                Clearance = GetDouble(a, "clearance", 0.67), Blocks = GetBool(a, "block_obstacles", true),
                ShrinkMText = GetBool(a, "shrink_mtext", false),
                RegionMinArea = GetDouble(a, "region_min_area", 0)
            };
            double[] lens = LpDoubles(a, "lengths", CoordLengthsMm);
            string[] dirs = LpStrings(a, "directions", LabelPlacer.DirOrder);
            double exempt = GetDouble(a, "anchor_exempt", 1.7);
            double k = 1.0 / scale;
            Func<double, double, LPt> toP = (x, y) => new LPt(x * k, y * k);
            string fmt = "0." + new string('0', Math.Max(0, dec));
            if (dec <= 0) fmt = "0";

            Database db = doc.Database;
            var items = new JsonArray(); var withdrawn = new JsonArray(); var dropped = new JsonArray();
            int cleared = 0, made = 0, nAnchors = 0; string note = null;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var btr = LpSpace(tr, db, space, OpenMode.ForWrite);
                ObjectId styleId = LpTextStyle(tr, db, styleName);
                double swf = LpStyleWf(tr, styleId);
                if (clear && !dry) cleared = LpClearTagged(tr, btr, tag);

                var raw = new List<Point2d>();
                if (points != null && points.Count > 0)
                {
                    foreach (JsonNode n in points)
                    {
                        var pa = n as JsonArray;
                        if (pa == null || pa.Count < 2) throw new InvalidOperationException("Each points entry must be [x,y].");
                        raw.Add(new Point2d(pa[0].GetValue<double>(), pa[1].GetValue<double>()));
                    }
                }
                else
                {
                    var src = new List<Polyline>();
                    if (handles != null && handles.Count > 0)
                    {
                        foreach (JsonNode hn in handles)
                        {
                            var pl = tr.GetObject(ResolveHandle(db, hn.ToString()), OpenMode.ForRead) as Polyline;
                            if (pl == null) throw new InvalidOperationException("Handle " + hn + " is not a polyline.");
                            src.Add(pl);
                        }
                    }
                    else
                    {
                        foreach (ObjectId id in btr)
                        {
                            var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                            if (pl != null && pl.Closed && string.Equals(pl.Layer, boundaryLayer, StringComparison.OrdinalIgnoreCase)) src.Add(pl);
                        }
                        if (src.Count == 0) throw new InvalidOperationException("Layer '" + boundaryLayer + "' contains no closed polylines.");
                    }
                    foreach (var pl in src)
                        for (int i = 0; i < pl.NumberOfVertices; i++) raw.Add(pl.GetPoint2dAt(i));
                }
                var anchors = new List<Point2d>();
                foreach (var q in raw)
                {
                    bool near = false;
                    foreach (var kpt in anchors) if (kpt.GetDistanceTo(q) < minGap * scale) { near = true; break; }
                    if (near) dropped.Add(LpXY(q.X, q.Y)); else anchors.Add(q);
                }
                nAnchors = anchors.Count;

                double reach = 0; foreach (var L in lens) reach = Math.Max(reach, L);
                double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
                foreach (var q in anchors) { x0 = Math.Min(x0, q.X * k); y0 = Math.Min(y0, q.Y * k); x1 = Math.Max(x1, q.X * k); y1 = Math.Max(y1, q.Y * k); }
                double pad = reach + 60 * H;
                var clip = new LBox(x0 - pad, y0 - pad, x1 + pad, y1 + pad);
                var sc = new LabelScene { OverlapTol = 0 };
                var win = a["window"] as JsonObject;
                if (win != null)
                    sc.Frame = new LBox(GetDouble(win, "minx", 0) * k, GetDouble(win, "miny", 0) * k, GetDouble(win, "maxx", 0) * k, GetDouble(win, "maxy", 0) * k);
                var ids = new List<ObjectId>(); foreach (ObjectId id in btr) ids.Add(id);
                LpScene(LpCache(tr, ids, clear ? tag : null), toP, clip, o, sc);

                LpPen pen = dry ? null : new LpPen(tr, db, btr, LpEnsureLayer(tr, db, layer, 3), styleId, tag);
                var placedBoxes = new List<LBox>(); var placedLeaders = new List<LSeg>();
                for (int i = 0; i < anchors.Count; i++)
                {
                    var q = anchors[i];
                    string lx = px + q.Y.ToString(fmt, CultureInfo.InvariantCulture);
                    string lyy = py + q.X.ToString(fmt, CultureInfo.InvariantCulture);
                    var rq = new LabelRequest { Anchor = toP(q.X, q.Y), Lines = new[] { lx, lyy }, H = H, Wf = swf };
                    var res = LabelPlacer.PlaceNormal(sc, rq, w, lens, dirs, placedBoxes, placedLeaders, exempt);
                    if (!res.Placed)
                    {
                        var why = new JsonObject(); foreach (var kv in res.Why) why[kv.Key] = kv.Value;
                        withdrawn.Add(new JsonObject
                        {
                            ["index"] = i, ["anchor"] = LpXY(q.X, q.Y), ["text"] = lx + " " + lyy,
                            ["best_cost"] = res.Dir == null ? null : (JsonNode)Math.Round(res.Cost, 2), ["reasons"] = why
                        });
                        continue;
                    }
                    placedBoxes.Add(res.Box); placedLeaders.Add(new LSeg(rq.Anchor, res.End));
                    items.Add(new JsonObject
                    {
                        ["index"] = i, ["anchor"] = LpXY(q.X, q.Y), ["end"] = LpXY(res.End.X * scale, res.End.Y * scale),
                        ["dir"] = res.Dir, ["leader_mm"] = res.LeaderMm, ["cost"] = Math.Round(res.Cost, 2), ["x"] = lx, ["y"] = lyy
                    });
                    if (pen == null) continue;
                    LpDrawNormal(pen, rq, res, scale);
                }
                if (pen != null) { note = pen.MoveToTop(); made = pen.Count; }
                tr.Commit();
            }
            return new JsonObject
            {
                ["space"] = space, ["dry_run"] = dry, ["anchors"] = nAnchors, ["dropped_by_gap"] = dropped,
                ["placed"] = items.Count, ["withdrawn"] = withdrawn, ["items"] = items,
                ["cleared"] = cleared, ["entities"] = made, ["weights"] = w.ToJson(),
                ["note"] = note ?? "Memory only; call save_dwg to persist."
            };
        }
    }
}
