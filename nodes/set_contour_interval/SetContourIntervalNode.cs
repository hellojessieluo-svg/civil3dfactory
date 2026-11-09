using System;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivSurfaceStyle = Autodesk.Civil.DatabaseServices.Styles.SurfaceStyle;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeSetContourInterval(JsonObject a, Document doc)
        {
            var names = new System.Collections.Generic.List<string>();
            if (a["styles"] is JsonArray arr) foreach (JsonNode n in arr) names.Add(n.ToString());
            string one = GetString(a, "style", null);
            if (!string.IsNullOrEmpty(one)) names.Add(one);
            if (names.Count == 0) throw new InvalidOperationException("style or styles is required");
            double minor = GetDouble(a, "minor", 0.5);
            double major = GetDouble(a, "major", 2.5);
            if (minor <= 0 || major < minor) throw new InvalidOperationException("invalid interval: 0 < minor <= major is required");
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var rep = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (string name in names)
                {
                    ObjectId sid = FindStyleId(tr, civ.Styles.SurfaceStyles, name);
                    if (sid.IsNull) throw new InvalidOperationException("surface style not found: " + name);
                    var st = (CivSurfaceStyle)tr.GetObject(sid, OpenMode.ForWrite);
                    var cs = st.ContourStyle;
                    double minor0 = cs.MinorContourInterval, major0 = cs.MajorContourInterval;
                    cs.MajorContourInterval = major;
                    cs.MinorContourInterval = minor;
                    rep.Add(new JsonObject
                    {
                        ["style"] = name,
                        ["minor_before"] = minor0, ["major_before"] = major0,
                        ["minor_after"] = cs.MinorContourInterval, ["major_after"] = cs.MajorContourInterval
                    });
                }
                tr.Commit();
            }
            return new JsonObject { ["styles"] = rep };
        }
    }
}
