using System;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeCreateFilletAlignment(JsonObject args, Document doc)
            => CreateFilletAlignment(args, doc);

        // Properly constrained corner alignment: fixed line + free arc (by radius, tangent to both neighbours) + fixed line.
        // Unlike a "three-point fixed arc", tangency is a constraint stored in the object: changing the radius in the geometry editor re-solves and keeps tangency,
        // and dragging a fixed line re-tangents the arc. This is the correct internal structure the user asked for on 2026-08-23.
        static JsonNode CreateFilletAlignment(JsonObject a, Document doc)
        {
            string name = Need(a, "name");
            double radius = GetDouble(a, "radius", 20);
            bool big = GetBool(a, "greater_than_180", false);
            string style = GetString(a, "style", null);
            string labelSet = GetString(a, "label_set", null);

            Point3d Pt(string key, int idx)
            {
                var arr = a[key] as JsonArray;
                if (arr == null || arr.Count < 2)
                    throw new InvalidOperationException(key + ":[[x,y],[x,y]] is required");
                var p = (JsonArray)arr[idx];
                return new Point3d(p[0].GetValue<double>(), p[1].GetValue<double>(), 0);
            }

            Database db = doc.Database;
            var civ = Civ(db);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EraseAlignments(tr, civ, name);
                tr.Commit();
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId styleId = FindStyleId(tr, civ.Styles.AlignmentStyles, style);
                ObjectId labelId = FindStyleId(tr, civ.Styles.LabelSetStyles.AlignmentLabelSetStyles, labelSet);
                ObjectId alId = CivAlignment.Create(civ, name, ObjectId.Null, db.Clayer, styleId, labelId);
                var al = (CivAlignment)tr.GetObject(alId, OpenMode.ForWrite);
                var ents = al.Entities;
                var l1 = ents.AddFixedLine(Pt("line1", 0), Pt("line1", 1));
                var l2 = ents.AddFixedLine(Pt("line2", 0), Pt("line2", 1));
                var arc = ents.AddFreeCurve(l1.EntityId, l2.EntityId, radius,
                    CurveParamType.Radius, big, CurveType.Compound);

                double len = al.Length;
                double rBack = arc.Radius;
                if (len <= 0 || Math.Abs(rBack - radius) > 0.01)
                    throw new InvalidOperationException(
                        name + " free arc solve anomaly: len=" + Math.Round(len, 2) + " R=" + Math.Round(rBack, 3));

                var res = new JsonObject
                {
                    ["alignment"] = al.Name,
                    ["handle"] = al.Handle.ToString(),
                    ["length"] = Math.Round(len, 3),
                    ["entities"] = ents.Count,
                    ["radius"] = Math.Round(rBack, 3),
                    ["arc_length"] = Math.Round(arc.Length, 3),
                    ["constraint"] = "fixed line + free arc (tangent) + fixed line"
                };
                tr.Commit();
                return res;
            }
        }
    }
}
