using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        static JsonNode ViewportAnnoScale(JsonObject a, Document doc)
        {
            bool dry = GetBool(a, "dry_run", true);
            string mode = (GetString(a, "mode", "from_custom_scale") ?? "from_custom_scale")
                .ToLowerInvariant();
            double modelUnitMm = GetDouble(a, "model_unit_mm", 1000.0);
            var wantLayouts = StringSet(a, "layouts");
            bool hasMinW = a["min_width_mm"] != null;
            double minW = GetDouble(a, "min_width_mm", 0);
            bool hasMaxW = a["max_width_mm"] != null;
            double maxW = GetDouble(a, "max_width_mm", 0);
            double fixedScale = GetDouble(a, "scale", 0);
            string fixedName = GetString(a, "name", null);
            if (mode == "fixed" && fixedScale <= 0 && string.IsNullOrEmpty(fixedName))
                throw new InvalidOperationException(
                    "mode:fixed requires scale (5 for metric 1:5000) or name.");

            Database db = doc.Database;
            var rows = new JsonArray();
            var failed = new JsonArray();
            var createdScales = new JsonArray();
            int touched = 0, skipped = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var coll = db.ObjectContextManager.GetContextCollection("ACDB_ANNOTATIONSCALES");
                if (coll == null) throw new InvalidOperationException("The drawing has no annotation scale collection.");

                var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry e in layouts)
                {
                    if (string.Equals(e.Key, "Model", StringComparison.OrdinalIgnoreCase)) continue;
                    if (wantLayouts != null && !wantLayouts.Contains(e.Key)) continue;
                    var lay = (Layout)tr.GetObject(e.Value, OpenMode.ForRead);
                    var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);

                    foreach (ObjectId id in btr)
                    {
                        var vp = tr.GetObject(id, OpenMode.ForRead) as Viewport;
                        if (vp == null) continue;
                        if (vp.Number == 1) continue;

                        double w = Math.Round(vp.Width, 2), h = Math.Round(vp.Height, 2);
                        if (hasMinW && w < minW) { skipped++; continue; }
                        if (hasMaxW && w > maxW) { skipped++; continue; }

                        string annoBefore = null;
                        try { var s = vp.AnnotationScale; annoBefore = s == null ? null : s.Name; }
                        catch { }

                        double drawingUnits;
                        string scaleName;
                        if (mode == "fixed")
                        {
                            drawingUnits = fixedScale;
                            scaleName = fixedName ?? ("1:" + Math.Round(fixedScale * modelUnitMm)
                                .ToString("0.###"));
                            if (drawingUnits <= 0) drawingUnits = 0;
                        }
                        else
                        {
                            double cs = vp.CustomScale;
                            if (cs <= 0)
                            {
                                failed.Add(new JsonObject
                                {
                                    ["layout"] = e.Key,
                                    ["handle"] = vp.Handle.ToString(),
                                    ["why"] = "CustomScale is unavailable or nonpositive; viewport has no fixed scale"
                                });
                                continue;
                            }
                            drawingUnits = 1.0 / cs;
                            scaleName = "1:" + Math.Round(drawingUnits * modelUnitMm).ToString("0.###");
                        }

                        var row = new JsonObject
                        {
                            ["layout"] = e.Key,
                            ["handle"] = vp.Handle.ToString(),
                            ["vp_number"] = vp.Number,
                            ["width_mm"] = w,
                            ["height_mm"] = h,
                            ["custom_scale"] = Math.Round(vp.CustomScale, 8),
                            ["anno_before"] = annoBefore,
                            ["anno_after"] = scaleName,
                            ["locked"] = vp.Locked
                        };

                        if (!dry)
                        {
                            try
                            {
                                var ctx = coll.GetContext(scaleName) as AnnotationScale;
                                if (ctx == null)
                                {
                                    if (drawingUnits <= 0)
                                        throw new InvalidOperationException(
                                            "The drawing has no scale '" + scaleName + "'; unable to derive a new scale");
                                    var sc = new AnnotationScale
                                    {
                                        Name = scaleName,
                                        PaperUnits = 1.0,
                                        DrawingUnits = drawingUnits
                                    };
                                    coll.AddContext(sc);
                                    createdScales.Add(scaleName);
                                    ctx = coll.GetContext(scaleName) as AnnotationScale;
                                }
                                if (ctx == null)
                                    throw new InvalidOperationException(
                                        "Scale '" + scaleName + "' was created but could not be retrieved");
                                vp.UpgradeOpen();
                                bool relock = false;
                                try { vp.AnnotationScale = ctx; }
                                catch
                                {
                                    if (vp.Locked) { vp.Locked = false; relock = true; }
                                    vp.AnnotationScale = ctx;
                                }
                                if (relock) vp.Locked = true;
                                row["applied"] = true;
                                touched++;
                            }
                            catch (System.Exception ex)
                            {
                                row["applied"] = false;
                                failed.Add(new JsonObject
                                {
                                    ["layout"] = e.Key,
                                    ["handle"] = vp.Handle.ToString(),
                                    ["why"] = ex.GetType().Name + ": " + Truncate(ex.Message, 120)
                                });
                            }
                        }
                        else row["applied"] = false;

                        rows.Add(row);
                    }
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["dry_run"] = dry,
                ["mode"] = mode,
                ["model_unit_mm"] = modelUnitMm,
                ["viewports"] = rows,
                ["applied"] = touched,
                ["skipped_by_width"] = skipped,
                ["created_scales"] = createdScales,
                ["failed"] = failed,
                ["note"] = "Only viewport annotation scale changes; CustomScale and view center remain unchanged. Call save_dwg to persist."
            };
        }
    }
}
