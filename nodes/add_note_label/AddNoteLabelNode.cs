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
        /// Batch-place General Note Labels: given a list of points, place one label per point with the specified style.
        /// This is how coordinate labelling is automated: the style contains X=&lt;[Northing]&gt; / Y=&lt;[Easting]&gt;,
        /// so the label picks up the values automatically when placed and follows when dragged; no manual copying of coordinates.
        ///
        /// Why this node exists (2026-09-02 phase-5 template coordinate label tuning):
        /// the anchor position / dragged-state display of a label style is not exposed in the .NET API; the only way to check it is to actually place a label and look at the rendering;
        /// so "batch coordinate labels at given points" was made a formal entry point along the way.
        ///
        /// Each point may carry dx/dy (drawing units): if given, the label is dragged into its dragged state (with leader), used to inspect/output the dragged-state layout.
        /// Changes stay in memory; call save_dwg explicitly to persist.
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
                    var p = pn as JsonObject;
                    if (p == null) continue;
                    double x = GetDouble(p, "x", double.NaN), y = GetDouble(p, "y", double.NaN);
                    if (double.IsNaN(x) || double.IsNaN(y))
                        throw new InvalidOperationException("Every points item must have x, y.");
                    var loc = new Point3d(x, y, 0);
                    ObjectId lid = CivNoteLabel.Create(db, loc);
                    var lbl = (CivNoteLabel)tr.GetObject(lid, OpenMode.ForWrite);
                    lbl.StyleId = styleId;
                    if (!string.IsNullOrEmpty(layer)) lbl.Layer = layer;
                    var rec = new JsonObject
                    {
                        ["handle"] = lbl.Handle.ToString(),
                        ["x"] = x,
                        ["y"] = y
                    };
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
                ["labels"] = placed
            };
        }
    }
}
