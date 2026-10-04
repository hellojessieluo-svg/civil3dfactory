using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        static JsonNode ViewportLayerOverrides(JsonObject a, Document doc)
        {
            var items = a["items"] as JsonArray;
            if (items == null || items.Count == 0) throw new InvalidOperationException("items[] is required.");
            bool dryRun = GetBool(a, "dry_run", false);
            Database db = doc.Database;
            var report = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (JsonNode n in items)
                {
                    var it = n as JsonObject; if (it == null) continue;
                    string h = Need(it, "viewport");
                    ObjectId vpId;
                    try { vpId = db.GetObjectId(false, new Handle(Convert.ToInt64(h, 16)), 0); }
                    catch { throw new InvalidOperationException("Invalid viewport handle: " + h); }
                    var vp = tr.GetObject(vpId, OpenMode.ForWrite) as Autodesk.AutoCAD.DatabaseServices.Viewport;
                    if (vp == null) throw new InvalidOperationException("Handle " + h + " is not a viewport.");
                    short ci = (short)GetDouble(it, "color_index", 8);
                    int colored = 0, frozen = 0, thawed = 0, cleared = 0; var missing = new JsonArray();

                    var thawIds = new List<ObjectId>();
                    var arrThaw = it["thaw"] as JsonArray;
                    if (arrThaw != null) foreach (var x in arrThaw) { string ln = x.ToString(); if (lt.Has(ln)) thawIds.Add(lt[ln]); else missing.Add(ln); }
                    if (thawIds.Count > 0 && !dryRun) { vp.ThawLayersInViewport(thawIds.GetEnumerator()); thawed = thawIds.Count; }

                    var freezeIds = new List<ObjectId>();
                    var arrFrz = it["freeze"] as JsonArray;
                    if (arrFrz != null) foreach (var x in arrFrz) { string ln = x.ToString(); if (lt.Has(ln)) freezeIds.Add(lt[ln]); else missing.Add(ln); }
                    if (freezeIds.Count > 0 && !dryRun) { vp.FreezeLayersInViewport(freezeIds.GetEnumerator()); frozen = freezeIds.Count; }

                    var arrCol = it["color"] as JsonArray;
                    if (arrCol != null)
                        foreach (var x in arrCol)
                        {
                            string ln = x.ToString();
                            if (!lt.Has(ln)) { missing.Add(ln); continue; }
                            if (dryRun) { colored++; continue; }
                            var ltr = (LayerTableRecord)tr.GetObject(lt[ln], OpenMode.ForWrite);
                            var props = ltr.GetViewportOverrides(vpId);
                            props.Color = Color.FromColorIndex(ColorMethod.ByAci, ci);
                            colored++;
                        }
                    var arrClr = it["clear"] as JsonArray;
                    if (arrClr != null)
                        foreach (var x in arrClr)
                        {
                            string ln = x.ToString();
                            if (!lt.Has(ln)) { missing.Add(ln); continue; }
                            if (dryRun) { cleared++; continue; }
                            var ltr = (LayerTableRecord)tr.GetObject(lt[ln], OpenMode.ForWrite);
                            if (ltr.HasViewportOverrides(vpId)) { ltr.GetViewportOverrides(vpId).RemoveOverrides(); cleared++; }
                        }
                    report.Add(new JsonObject
                    {
                        ["viewport"] = h, ["color_index"] = ci, ["colored"] = colored, ["frozen"] = frozen,
                        ["thawed"] = thawed, ["cleared"] = cleared, ["missing_layers"] = missing
                    });
                }
                tr.Commit();
            }
            return new JsonObject { ["dry_run"] = dryRun, ["items"] = report, ["note"] = "Memory only; call save_dwg to persist." };
        }

        static JsonNode ViewportLayerState(JsonObject a, Document doc)
        {
            Database db = doc.Database;
            var arr = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId bid in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
                    if (!btr.IsLayout || btr.Name.Equals(BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (ObjectId id in btr)
                    {
                        var vp = tr.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Viewport;
                        if (vp == null || vp.Number == 1) continue;
                        var over = new JsonArray(); var frz = new JsonArray();
                        foreach (ObjectId lid in lt)
                        {
                            var ltr = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                            if (ltr.HasViewportOverrides(id))
                            {
                                var p = ltr.GetViewportOverrides(id);
                                over.Add(new JsonObject { ["layer"] = ltr.Name, ["color"] = p.IsColorOverridden ? p.Color.ColorIndex.ToString() : null });
                            }
                            if (vp.IsLayerFrozenInViewport(lid)) frz.Add(ltr.Name);
                        }
                        arr.Add(new JsonObject
                        {
                            ["layout"] = btr.Name, ["handle"] = vp.Handle.ToString(), ["number"] = vp.Number,
                            ["center_x"] = vp.CenterPoint.X, ["center_y"] = vp.CenterPoint.Y, ["width"] = vp.Width, ["height"] = vp.Height,
                            ["view_center_x"] = vp.ViewCenter.X, ["view_center_y"] = vp.ViewCenter.Y, ["custom_scale"] = vp.CustomScale,
                            ["target_x"] = vp.ViewTarget.X, ["target_y"] = vp.ViewTarget.Y, ["twist"] = vp.TwistAngle,
                            ["overrides"] = over, ["frozen"] = frz
                        });
                    }
                }
                tr.Commit();
            }
            return new JsonObject { ["viewports"] = arr, ["count"] = arr.Count };
        }
    }
}
