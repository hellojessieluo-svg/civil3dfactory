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

        // 约束正确的转角路线：固定直线 + 自由圆弧(按半径、与前后实体相切) + 固定直线。
        // 与"三点固定弧"的区别：相切是写进对象的约束——几何编辑器里改半径自动重解保切，
        // 拖固定线时弧也跟着重切。这是 2026-08-23 用户点名的正确内构。
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
                    throw new InvalidOperationException("需要 " + key + ":[[x,y],[x,y]]");
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
                        name + " 自由弧解算异常：len=" + Math.Round(len, 2) + " R=" + Math.Round(rBack, 3));

                var res = new JsonObject
                {
                    ["alignment"] = al.Name,
                    ["handle"] = al.Handle.ToString(),
                    ["length"] = Math.Round(len, 3),
                    ["entities"] = ents.Count,
                    ["radius"] = Math.Round(rBack, 3),
                    ["arc_length"] = Math.Round(arc.Length, 3),
                    ["constraint"] = "固定线+自由弧(相切)+固定线"
                };
                tr.Commit();
                return res;
            }
        }
    }
}
