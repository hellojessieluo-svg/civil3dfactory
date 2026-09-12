using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DFactory
{
    /// <summary>
    /// set_polyline_elevation: set the elevation (Z) of polylines.
    ///
    /// Design lines drawn in plan often sit at elevation 0 for the whole layer; before going into a TIN / grading
    /// they must be lifted to their design elevation. elevation applies to all, elevation_by_handle per polyline.
    ///
    /// Only Elevation changes; geometry and layer are untouched.
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
                throw new InvalidOperationException("Give at least one of elevation and elevation_by_handle.");
            if (string.IsNullOrWhiteSpace(layer) && (handles == null || handles.Count == 0)
                && (byHandle == null || byHandle.Count == 0))
                throw new InvalidOperationException("Give at least one of layer, handles, elevation_by_handle to select the objects.");

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
                    else if (!hasGlobal) continue;   // in per-handle mode, unnamed ones are untouched

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

            // Assert: nothing touched = not a success. Named handles with no match must error rather than ok:true.
            if (touched == 0 && same == 0)
                throw new InvalidOperationException(
                    "No polyline matched (layer='" + (layer ?? "") + "', " + wanted.Count + " named).");

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
