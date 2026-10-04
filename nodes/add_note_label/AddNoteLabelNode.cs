using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivNoteLabel = Autodesk.Civil.DatabaseServices.NoteLabel;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        /// <summary>
        ///
        ///
        /// </summary>
        static JsonNode RunNodeAddNoteLabel(JsonObject a, Document doc)
        {
            string styleName = GetString(a, "style", null);
            if (string.IsNullOrEmpty(styleName))
                throw new InvalidOperationException("style (General Note Label style name) is required.");
            var pts = a["points"] as JsonArray;
            if (pts == null || pts.Count == 0)
                throw new InvalidOperationException("points is required: [{x,y,dx?,dy?}, ...]; dx/dy are drag offsets (drawing units).");
            string layer = GetString(a, "layer", null);

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var placed = new JsonArray();
            var handles = new JsonArray();
            int index = -1;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId styleId = ObjectId.Null;
                try { styleId = civ.Styles.LabelStyles.GeneralNoteLabelStyles[styleName]; }
                catch { }
                if (styleId.IsNull)
                    throw new InvalidOperationException("General Note Label style '" + styleName + "' not found (case-sensitive; see GeneralNoteLabelStyles in list_styles).");

                if (!string.IsNullOrEmpty(layer))
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (!lt.Has(layer))
                    {
                        lt.UpgradeOpen();
                        var rec = new LayerTableRecord { Name = layer };
                        lt.Add(rec);
                        tr.AddNewlyCreatedDBObject(rec, true);
                    }
                }

                foreach (JsonNode pn in pts)
                {
                    index++;
                    var p = pn as JsonObject;
                    if (p == null) continue;
                    double x = GetDouble(p, "x", double.NaN), y = GetDouble(p, "y", double.NaN);
                    string pvName = GetString(p, "profile_view", null);
                    if (!string.IsNullOrEmpty(pvName))
                    {
                        double st = GetDouble(p, "station", double.NaN), el = GetDouble(p, "elevation", double.NaN);
                        if (double.IsNaN(st) || double.IsNaN(el)) throw new InvalidOperationException("profile_view requires station and elevation.");
                        ObjectId pvId = ObjectId.Null;
                        var btN = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        var msN = (BlockTableRecord)tr.GetObject(btN[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                        foreach (ObjectId vid in msN)
                        {
                            if (vid.ObjectClass.Name != "AeccDbGraphProfile") continue;
                            var pvv = tr.GetObject(vid, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.ProfileView;
                            if (pvv != null && pvv.Name == pvName) { pvId = vid; break; }
                        }
                        if (pvId.IsNull) throw new InvalidOperationException("Profile view not found: " + pvName);
                        var pv = (Autodesk.Civil.DatabaseServices.ProfileView)tr.GetObject(pvId, OpenMode.ForRead);
                        double xx = 0, yy = 0; pv.FindXYAtStationAndElevation(st, el, ref xx, ref yy); x = xx; y = yy;
                    }
                    if (double.IsNaN(x) || double.IsNaN(y))
                        throw new InvalidOperationException("Every points item must have x, y.");
                    var loc = new Point3d(x, y, 0);
                    ObjectId lid = CivNoteLabel.Create(db, loc);
                    var lbl = (CivNoteLabel)tr.GetObject(lid, OpenMode.ForWrite);
                    lbl.StyleId = styleId;
                    if (!string.IsNullOrEmpty(layer)) lbl.Layer = layer;
                    string txt = GetString(p, "text", null);
                    if (!string.IsNullOrEmpty(txt))
                    {
                        var ls = (Autodesk.Civil.DatabaseServices.Styles.LabelStyle)tr.GetObject(styleId, OpenMode.ForRead);
                        var comps = ls.GetComponents(Autodesk.Civil.DatabaseServices.Styles.LabelStyleComponentType.Text);
                        if (comps.Count == 0) throw new InvalidOperationException("Style " + styleName + " has no text component to override.");
                        foreach (ObjectId cid in comps) lbl.SetTextComponentOverride(cid, txt);
                    }
                    var rec = new JsonObject
                    {
                        ["index"] = index,
                        ["handle"] = lbl.Handle.ToString(),
                        ["x"] = x,
                        ["y"] = y
                    };
                    if (!string.IsNullOrEmpty(pvName))
                    {
                        rec["profile_view"] = pvName;
                        rec["station"] = GetDouble(p, "station", double.NaN);
                        rec["elevation"] = GetDouble(p, "elevation", double.NaN);
                    }
                    if (!string.IsNullOrEmpty(txt)) rec["text"] = txt;
                    rec["layer"] = lbl.Layer;
                    handles.Add(lbl.Handle.ToString());
                    double dx = GetDouble(p, "dx", 0), dy = GetDouble(p, "dy", 0);
                    if (dx != 0 || dy != 0)
                    {
                        lbl.DraggedOffset = new Vector3d(dx, dy, 0);
                        rec["dragged"] = true;
                        rec["dx"] = dx; rec["dy"] = dy;
                    }
                    placed.Add(rec);
                }
                tr.Commit();
            }

            if (placed.Count == 0)
                throw new InvalidOperationException("No label was placed (points contains no valid item).");
            return new JsonObject
            {
                ["style"] = styleName,
                ["placed"] = placed.Count,
                ["handles"] = handles,
                ["labels"] = placed
            };
        }
    }
}
