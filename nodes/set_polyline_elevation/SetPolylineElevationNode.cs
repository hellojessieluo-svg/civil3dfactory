using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DFactory
{
    /// <summary>
    /// set_polyline_elevation：给多段线设高程（Z）。
    ///
    /// 平面上画好的设计线常常整层躺在 0 高程上，要进 TIN / 要拿去放坡之前
    /// 得先把它抬到自己的设计高程。elevation 一刀切，elevation_by_handle 逐条给。
    ///
    /// 只改 Elevation，不动几何、不动图层。
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeSetPolylineElevation(JsonObject a, Document doc)
        {
            string layer = GetString(a, "layer", null);
            var handles = a["handles"] as JsonArray;
            var byHandle = a["elevation_by_handle"] as JsonObject;
            bool hasGlobal = a["elevation"] != null;
            double gz = GetDouble(a, "elevation", 0.0);

            if (!hasGlobal && (byHandle == null || byHandle.Count == 0))
                throw new InvalidOperationException("elevation 与 elevation_by_handle 至少给一个。");
            if (string.IsNullOrWhiteSpace(layer) && (handles == null || handles.Count == 0)
                && (byHandle == null || byHandle.Count == 0))
                throw new InvalidOperationException("layer、handles、elevation_by_handle 至少给一个来圈定对象。");

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (handles != null)
                foreach (JsonNode h in handles) if (h != null) wanted.Add(h.ToString().Trim());
            if (byHandle != null)
                foreach (var kv in byHandle) wanted.Add(kv.Key.Trim());

            Database db = doc.Database;
            var changed = new JsonArray();
            var missing = new JsonArray();
            int touched = 0, same = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (pl == null) continue;
                    string handle = pl.Handle.ToString();
                    if (wanted.Count > 0)
                    {
                        if (!wanted.Contains(handle)) continue;
                    }
                    else if (!string.Equals(pl.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;

                    double z = gz;
                    if (byHandle != null && byHandle[handle] != null)
                        z = byHandle[handle].GetValue<double>();
                    else if (!hasGlobal) continue;   // 逐条模式下没点名的不动

                    seen.Add(handle);
                    double z0 = pl.Elevation;
                    if (Math.Abs(z0 - z) < 1e-9) { same++; continue; }

                    pl.UpgradeOpen();
                    pl.Elevation = z;
                    touched++;
                    changed.Add(new JsonObject
                    {
                        ["handle"] = handle,
                        ["layer"] = pl.Layer,
                        ["from"] = Round(z0, 3),
                        ["to"] = Round(z, 3)
                    });
                }

                foreach (string h in wanted)
                    if (!seen.Contains(h)) missing.Add((JsonNode)h);

                tr.Commit();
            }

            // 断言：一条都没动 = 没成功。点名了却一条都没找到，必须报错而不是 ok:true。
            if (touched == 0 && same == 0)
                throw new InvalidOperationException(
                    "一条多段线都没匹配上（layer='" + (layer ?? "") + "'，点名 " + wanted.Count + " 条）。");

            return new JsonObject
            {
                ["changed"] = touched,
                ["already_at_elevation"] = same,
                ["not_found"] = missing,
                ["details"] = changed
            };
        }
    }
}
