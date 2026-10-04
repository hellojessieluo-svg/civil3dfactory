using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeSetSurfaceStyle(JsonObject a, Document doc)
        {
            string styleName = Need(a, "style");
            string layer = GetString(a, "layer", null);
            string prefix = GetString(a, "prefix", null);
            var names = new List<string>();
            var arr = a["surfaces"] as JsonArray;
            if (arr != null) foreach (JsonNode n in arr) names.Add(n.ToString());
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var done = new JsonArray(); var missing = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId sid = FindStyleId(tr, civ.Styles.SurfaceStyles, styleName);
                if (sid.IsNull) throw new InvalidOperationException("Surface style not found: '" + styleName + "'");
                if (layer != null) EnsureLayer(db, tr, layer, 5);
                var seen = new HashSet<string>();
                foreach (ObjectId id in civ.GetSurfaceIds())
                {
                    var s = tr.GetObject(id, OpenMode.ForRead) as CivSurface;
                    if (s == null) continue;
                    bool hit = names.Contains(s.Name) || (!string.IsNullOrEmpty(prefix) && s.Name.StartsWith(prefix, StringComparison.Ordinal));
                    if (!hit) continue;
                    seen.Add(s.Name);
                    s.UpgradeOpen();
                    s.StyleId = sid;
                    if (layer != null) s.Layer = layer;
                    done.Add(s.Name);
                }
                foreach (string n in names) if (!seen.Contains(n)) missing.Add(n);
                tr.Commit();
            }
            return new JsonObject { ["style"] = styleName, ["done"] = done, ["missing"] = missing };
        }
    }
}
