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
        static JsonNode RunNodeAnnotateCrossCut(JsonObject a, Document doc)
        {
            string layoutName = Need(a, "layout");
            var items = a["items"] as JsonArray;
            if (items == null || items.Count == 0) throw new InvalidOperationException("Specify items:[{viewport:handle, code:channel_code}].");
            string tpl = GetString(a, "layer_template", "C-CHNL-{code}-{kind}");
            if (tpl.IndexOf("{code}", StringComparison.Ordinal) < 0 || tpl.IndexOf("{kind}", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("layer_template must contain {code} and {kind}, e.g. C-CHNL-{code}-{kind}.");
            var other = LpWild(GetString(a, "other_layers", "C-CHNL-*"));
            var ignore = LpWild(GetString(a, "ignore_layers", null));
            var p = new LabelPlacer.CrossCutParams
            {
                Names = LpStrings(a, "labels", new[] { "Dredging top edge", "Dredging centerline", "Dredging top edge" }),
                H = GetDouble(a, "text_height", 3.5), Pitch = GetDouble(a, "pitch", 5.5),
                Step = GetDouble(a, "step", 5.0), MaxHalf = GetDouble(a, "max_half_width", 25),
                Ext = LpDoubles(a, "extensions", new[] { 8.0, 14.0, 20.0 }),
                Offs = LpDoubles(a, "offsets", new[] { 0.0, 8, -8, 14, -14, 20, -20 })
            };
            double dotR = GetDouble(a, "dot_radius", 0.6);
            bool underline = GetBool(a, "underline", true);
            string layer = GetString(a, "layer", "C-ANNO-TEXT");
            string styleName = GetString(a, "text_style", "txt1");
            string tag = GetString(a, "tag", "annotate_cross_cut");
            bool clear = GetBool(a, "clear", true), dry = GetBool(a, "dry_run", false);
            var w = LabelWeights.PlanDefaults().Apply(a["weights"] as JsonObject);

            Database db = doc.Database;
            var outItems = new JsonArray(); var withdrawn = new JsonArray();
            int cleared = 0, made = 0; string note = null;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var paper = LpSpace(tr, db, layoutName, OpenMode.ForWrite);
                if (string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("layout is required; cross-cut annotations are drawn in paper space.");
                ObjectId styleId = LpTextStyle(tr, db, styleName);
                p.Wf = LpStyleWf(tr, styleId); p.Underline = underline;
                if (clear && !dry) cleared = LpClearTagged(tr, paper, tag);
                var msCache = LpCache(tr, ModelSpace(db, tr), clear ? tag : null);
                var psIds = new List<ObjectId>(); foreach (ObjectId id in paper) psIds.Add(id);
                var psCache = LpCache(tr, psIds, clear ? tag : null);
                bool blocks = GetBool(a, "block_obstacles", true);
                LpPen pen = dry ? null : new LpPen(tr, db, paper, LpEnsureLayer(tr, db, layer, 3), styleId, tag);

                foreach (JsonNode it in items)
                {
                    var io = it as JsonObject; if (io == null) continue;
                    string vh = Need(io, "viewport"); string code = Need(io, "code");
                    LpView v = LpReadView(tr, db, vh, paper.ObjectId);
                    Func<string, string> lay = kind => tpl.Replace("{code}", code).Replace("{kind}", kind);
                    var own = LpWild(lay("*"));
                    Func<string, bool> wTop = LpWild(lay("TOP")), wCntr = LpWild(lay("CNTR")), wSamp = LpWild(lay("SAMP"));

                    var frame = v.Frame;
                    var ls = new LabelScene();
                    LpScene(msCache, v.ToPaper, frame, new LpSceneOpts { Ignore = ignore, Blocks = blocks }, ls);
                    LpScene(psCache, (x, y) => new LPt(x, y), frame, new LpSceneOpts { Ignore = ignore, Blocks = blocks, MaxBlockMm = 300 }, ls);
                    var sc = new LabelPlacer.CrossCutScene { Frame = frame, Texts = ls.Texts, All = ls.All };
                    var scratch = new List<LSeg>();
                    foreach (var mi in msCache)
                    {
                        if (mi.Kind != 1 || ignore(mi.Layer)) continue;
                        string ly = mi.Layer;
                        List<LSeg> dst = null;
                        if (wTop(ly)) dst = sc.Top;
                        else if (wCntr(ly)) dst = sc.Cntr;
                        else if (wSamp(ly)) dst = sc.Samp;
                        else if (other(ly) && !own(ly)) dst = sc.Other;
                        if (dst != null) LpAddPolyline(mi.Pts, false, v.ToPaper, frame, dst, scratch);
                    }

                    var res = LabelPlacer.CrossCut(sc, p, w);
                    var why = new JsonObject(); foreach (var kv in res.Why) why[kv.Key] = kv.Value;
                    if (!res.Placed)
                    {
                        withdrawn.Add(new JsonObject
                        {
                            ["viewport"] = vh, ["code"] = code, ["reasons"] = why,
                            ["center"] = LpXY(frame.Center.X, frame.Center.Y),
                            ["counts"] = new JsonObject { ["top"] = sc.Top.Count, ["cntr"] = sc.Cntr.Count, ["samp"] = sc.Samp.Count, ["other"] = sc.Other.Count, ["texts"] = sc.Texts.Count }
                        });
                        continue;
                    }
                    outItems.Add(new JsonObject
                    {
                        ["viewport"] = vh, ["code"] = code, ["cost"] = Math.Round(res.Cost, 2),
                        ["near"] = LpXY(res.Near.X, res.Near.Y), ["far"] = LpXY(res.Far.X, res.Far.Y), ["anchor"] = LpXY(res.Anc.X, res.Anc.Y),
                        ["extension"] = res.E, ["offset"] = res.K,
                        ["hits"] = new JsonArray(LpXY(res.A.X, res.A.Y), LpXY(res.P.X, res.P.Y), LpXY(res.B.X, res.B.Y)),
                        ["model_point"] = new JsonArray(Math.Round(v.ToModel(res.P).X, 3), Math.Round(v.ToModel(res.P).Y, 3)),
                        ["reasons"] = why
                    });
                    if (pen == null) continue;
                    foreach (var q in new[] { res.A, res.P, res.B }) pen.Dot(q.X, q.Y, dotR);
                    pen.Line(res.Near.X, res.Near.Y, res.Far.X, res.Far.Y);
                    if (res.K != 0) pen.Line(res.Far.X, res.Far.Y, res.Anc.X, res.Anc.Y);
                    for (int j = 0; j < res.TextBoxes.Count && j < p.Names.Length; j++)
                    {
                        bool rightSide = res.TextBoxes[j].X0 >= res.Anc.X;
                        pen.Text(rightSide ? res.TextBoxes[j].X0 : res.TextBoxes[j].X1, res.TextBoxes[j].Y0, (underline ? "%%u" : "") + p.Names[j], p.H, 256, rightSide ? 0 : 2);
                    }
                }
                if (pen != null) { note = pen.MoveToTop(); made = pen.Count; }
                tr.Commit();
            }
            return new JsonObject
            {
                ["layout"] = layoutName, ["dry_run"] = dry, ["placed"] = outItems.Count, ["withdrawn"] = withdrawn,
                ["items"] = outItems, ["cleared"] = cleared, ["entities"] = made, ["weights"] = w.ToJson(),
                ["note"] = note ?? "Memory only; call save_dwg to persist."
            };
        }
    }
}
