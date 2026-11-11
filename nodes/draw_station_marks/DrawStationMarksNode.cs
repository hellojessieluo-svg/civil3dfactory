using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory
{
    /// <summary>
    /// Node draw_station_marks: pure-CAD station ticks and labels on a plan (for drawings without
    /// a Civil alignment, e.g. terrain sheets handed to another firm). Two sources:
    ///   stations [{station, x, y, ux, uy}]  — e.g. locate_sheet_stations output; (ux,uy) points left;
    ///   points [[x,y]..] + interval         — chainage along a polyline from its first vertex
    ///                                         (+ start_station), end station added when > 5 m left.
    /// A tick of 2*tick along the left normal, label on the left reading outward, flipped 180°
    /// when it would read upside down. Memory only; save_dwg to persist.
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeDrawStationMarks(JsonObject a, Document doc)
        {
            string prefix = GetString(a, "prefix", "");
            string layer = GetString(a, "layer", "C-ANNO-STATION");
            string styleName = GetString(a, "text_style", "txt1");
            double tick = GetDouble(a, "tick", 6), gap = GetDouble(a, "gap", 2);
            double h = GetDouble(a, "text_height", 3), wf = GetDouble(a, "width_factor", 0.8);

            var marks = new List<(double st, Point2d p, Vector2d n)>();
            if (a["stations"] is JsonArray sa)
            {
                foreach (JsonNode s in sa)
                    marks.Add((s["station"].GetValue<double>(),
                               new Point2d(s["x"].GetValue<double>(), s["y"].GetValue<double>()),
                               new Vector2d(s["ux"].GetValue<double>(), s["uy"].GetValue<double>()).GetNormal()));
            }
            else if (a["points"] is JsonArray pa)
            {
                double interval = GetDouble(a, "interval", 50), start = GetDouble(a, "start_station", 0);
                var pts = new List<Point2d>();
                foreach (JsonNode p in pa) pts.Add(new Point2d(p[0].GetValue<double>(), p[1].GetValue<double>()));
                var pl = new Polyline();
                for (int i = 0; i < pts.Count; i++) pl.AddVertexAt(i, pts[i], 0, 0, 0);
                double len = pl.Length;
                var chain = new List<double>();
                for (int i = 0; i * interval <= len + 1e-9; i++) chain.Add(i * interval);
                if (len - chain[chain.Count - 1] > 5) chain.Add(len);
                foreach (double s in chain)
                {
                    Point3d p = pl.GetPointAtDist(s);
                    Point3d q0 = pl.GetPointAtDist(Math.Max(0, s - 1)), q1 = pl.GetPointAtDist(Math.Min(len, s + 1));
                    var t = new Vector2d(q1.X - q0.X, q1.Y - q0.Y).GetNormal();
                    marks.Add((start + s, new Point2d(p.X, p.Y), new Vector2d(-t.Y, t.X)));
                }
                pl.Dispose();
            }
            else throw new InvalidOperationException("draw_station_marks needs stations[] or points[] (+interval).");

            Database db = doc.Database;
            int made = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EnsureLayer(db, tr, layer, 7);
                var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                if (!tst.Has(styleName)) throw new InvalidOperationException("Text style '" + styleName + "' not in drawing.");
                ObjectId styleId = tst[styleName];
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                foreach (var m in marks)
                {
                    var n = m.n;
                    var tickLine = new Line(new Point3d(m.p.X - n.X * tick, m.p.Y - n.Y * tick, 0),
                                            new Point3d(m.p.X + n.X * tick, m.p.Y + n.Y * tick, 0)) { Layer = layer };
                    ms.AppendEntity(tickLine); tr.AddNewlyCreatedDBObject(tickLine, true);

                    string label = StationName(prefix, m.st);
                    double ang = Math.Atan2(n.Y, n.X) * 180 / Math.PI;
                    double d0 = tick + gap;
                    if (ang > 90 || ang <= -90)
                    {
                        ang += 180;
                        d0 += label.Length * h * wf * 0.95;
                    }
                    var txt = new DBText
                    {
                        Position = new Point3d(m.p.X + n.X * d0, m.p.Y + n.Y * d0, 0),
                        TextString = label, Height = h, WidthFactor = wf,
                        Rotation = ang * Math.PI / 180, TextStyleId = styleId, Layer = layer
                    };
                    ms.AppendEntity(txt); tr.AddNewlyCreatedDBObject(txt, true);
                    made++;
                }
                tr.Commit();
            }
            return new JsonObject { ["marks"] = made, ["layer"] = layer, ["first"] = marks.Count > 0 ? StationName(prefix, marks[0].st) : null,
                                    ["last"] = marks.Count > 0 ? StationName(prefix, marks[marks.Count - 1].st) : null };
        }
    }
}
