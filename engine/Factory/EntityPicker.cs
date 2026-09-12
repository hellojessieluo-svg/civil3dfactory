using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DFactory
{
    /// <summary>
    /// "Pick in drawing" for the parameter dialog: click the button, the dialog steps aside, you pick/select in the drawing, then the dialog returns with the value filled in.
    ///
    /// The parameter form is a modal window from Application.ShowModalDialog; while modal, the CAD main window takes no input,
    /// so before picking we must Hide() and hand focus back to the main window, then Show() afterwards.
    /// This is the standard way to pick from a modal dialog in AutoCAD, and how Civil 3D's own dialogs feel.
    ///
    /// Every method returns JSON that can go straight into a work order:
    /// null when the user cancels with ESC / plain Enter, in which case the caller keeps the original value.
    /// </summary>
    public static class EntityPicker
    {
        /// <summary>Coordinates keep 6 decimals so the JSON does not contain floating-point noise like 1e-15.</summary>
        const int Decimals = 6;

        public static JsonNode Pick(Form owner, Document doc, NodeArgType type, string label)
        {
            if (doc == null || type == null) return null;
            switch (type.PickKind)
            {
                case "point": return PickPoint(owner, doc, label);
                case "points": return PickPoints(owner, doc, label);
                case "distance": return PickDistance(owner, doc, label);
                case "entity": return PickEntity(owner, doc, type.PickFilter, label);
                case "entities": return PickEntities(owner, doc, type.PickFilter, label);
            }
            return null;
        }

        // ───────────────────────── Points ─────────────────────────

        public static JsonNode PickPoint(Form owner, Document doc, string label)
        {
            return Interact(owner, delegate
            {
                Editor ed = doc.Editor;
                var opts = new PromptPointOptions("\n[C3DF] Specify " + Label(label, "point") + " (ESC to cancel): ");
                opts.AllowNone = true;
                PromptPointResult res = ed.GetPoint(opts);
                if (res.Status != PromptStatus.OK) return null;
                return Xy(res.Value);
            });
        }

        public static JsonNode PickPoints(Form owner, Document doc, string label)
        {
            return Interact(owner, delegate
            {
                Editor ed = doc.Editor;
                var points = new List<Point3d>();

                while (true)
                {
                    string prompt = points.Count == 0
                        ? "\n[C3DF] Specify " + Label(label, "start point") + " (ESC to cancel): "
                        : "\n[C3DF] Specify next point [" + points.Count + " picked] (Enter to finish): ";
                    var opts = new PromptPointOptions(prompt);
                    opts.AllowNone = true;
                    if (points.Count > 0)
                    {
                        opts.UseBasePoint = true;
                        opts.BasePoint = points[points.Count - 1];
                    }

                    PromptPointResult res = ed.GetPoint(opts);
                    if (res.Status == PromptStatus.None) break;          // Enter = done
                    if (res.Status != PromptStatus.OK) return null;      // ESC = cancel everything
                    points.Add(res.Value);
                }

                if (points.Count == 0) return null;
                var arr = new JsonArray();
                foreach (Point3d p in points) arr.Add(Xy(p));
                ed.WriteMessage("\n[C3DF] " + points.Count + " point(s) picked.\n");
                return arr;
            });
        }

        public static JsonNode PickDistance(Form owner, Document doc, string label)
        {
            return Interact(owner, delegate
            {
                Editor ed = doc.Editor;
                var opts = new PromptDistanceOptions("\n[C3DF] Specify " + Label(label, "distance") + " (ESC to cancel): ");
                opts.AllowNone = true;
                opts.AllowNegative = false;
                PromptDoubleResult res = ed.GetDistance(opts);
                if (res.Status != PromptStatus.OK) return null;
                return JsonValue.Create(Math.Round(res.Value, Decimals));
            });
        }

        // ───────────────────────── Objects ─────────────────────────

        public static JsonNode PickEntity(Form owner, Document doc, string filter, string label)
        {
            return Interact(owner, delegate
            {
                Editor ed = doc.Editor;
                var opts = new PromptEntityOptions("\n[C3DF] Select " + Label(label, FilterName(filter)) + " (ESC to cancel): ");
                opts.AllowNone = true;
                ApplyClassFilter(opts, filter);
                PromptEntityResult res = ed.GetEntity(opts);
                if (res.Status != PromptStatus.OK) return null;
                return JsonValue.Create(HandleOf(doc, res.ObjectId));
            });
        }

        public static JsonNode PickEntities(Form owner, Document doc, string filter, string label)
        {
            return Interact(owner, delegate
            {
                Editor ed = doc.Editor;
                var opts = new PromptSelectionOptions();
                opts.MessageForAdding = "\n[C3DF] Select " + Label(label, FilterName(filter)) + " (Enter to finish, ESC to cancel)";
                SelectionFilter sf = BuildSelectionFilter(filter);

                PromptSelectionResult res = sf != null ? ed.GetSelection(opts, sf) : ed.GetSelection(opts);
                if (res.Status != PromptStatus.OK || res.Value == null || res.Value.Count == 0) return null;

                var arr = new JsonArray();
                foreach (SelectedObject so in res.Value)
                {
                    if (so == null) continue;
                    string h = HandleOf(doc, so.ObjectId);
                    if (!string.IsNullOrEmpty(h)) arr.Add(JsonValue.Create(h));
                }
                if (arr.Count == 0) return null;
                ed.WriteMessage("\n[C3DF] " + arr.Count + " object(s) selected.\n");
                return arr;
            });
        }

        // ───────────────────────── Step aside / restore ─────────────────────────

        /// <summary>Hide the form -> give focus back to the CAD main window -> pick -> show again. The form comes back on both exceptions and cancellation.</summary>
        static JsonNode Interact(Form owner, Func<JsonNode> body)
        {
            bool hidden = false;
            try
            {
                if (owner != null && owner.Visible)
                {
                    owner.Hide();
                    hidden = true;
                }
                try { AcadApp.MainWindow.Focus(); }
                catch { }

                return body();
            }
            catch (System.Exception ex)
            {
                MessageBox.Show("Pick failed: " + ex.Message, "Civil3DFactory",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            finally
            {
                if (hidden && owner != null)
                {
                    try
                    {
                        owner.Show();
                        owner.Activate();
                        owner.BringToFront();
                    }
                    catch { }
                }
            }
        }

        // ───────────────────────── Filters and conversion ─────────────────────────

        /// <summary>Contract filter name -> optional RXClass whitelist. Unknown names mean no restriction; never lock people out.</summary>
        static void ApplyClassFilter(PromptEntityOptions opts, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return;
            var types = new List<Type>();
            switch (filter.Trim().ToLowerInvariant())
            {
                case "polyline":
                    types.Add(typeof(Polyline));
                    types.Add(typeof(Polyline2d));
                    types.Add(typeof(Polyline3d));
                    break;
                case "line":
                    types.Add(typeof(Line));
                    break;
                case "curve":
                    types.Add(typeof(Curve));
                    break;
                case "text":
                    types.Add(typeof(DBText));
                    types.Add(typeof(MText));
                    break;
                case "blockref":
                    types.Add(typeof(BlockReference));
                    break;
                case "alignment":
                    types.Add(typeof(Autodesk.Civil.DatabaseServices.Alignment));
                    break;
                default:
                    return;
            }

            foreach (Type t in types)
            {
                try { opts.AddAllowedClass(t, false); }
                catch { }
            }
            opts.SetRejectMessage("\n[C3DF] Please select a " + FilterName(filter) + ".");
        }

        static SelectionFilter BuildSelectionFilter(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return null;
            string dxf;
            switch (filter.Trim().ToLowerInvariant())
            {
                case "polyline": dxf = "LWPOLYLINE,POLYLINE"; break;
                case "line": dxf = "LINE"; break;
                case "text": dxf = "TEXT,MTEXT"; break;
                case "blockref": dxf = "INSERT"; break;
                case "alignment": dxf = "AECC_ALIGNMENT"; break;
                default: return null;
            }
            return new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, dxf) });
        }

        static string FilterName(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return "object";
            switch (filter.Trim().ToLowerInvariant())
            {
                case "polyline": return "polyline";
                case "line": return "line";
                case "curve": return "curve";
                case "text": return "text";
                case "blockref": return "block reference";
                case "alignment": return "alignment";
            }
            return filter;
        }

        /// <summary>Handles are hex strings, matching the existing handle parameters in work orders.</summary>
        static string HandleOf(Document doc, ObjectId id)
        {
            if (id.IsNull) return null;
            try
            {
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    DBObject o = tr.GetObject(id, OpenMode.ForRead);
                    string h = o.Handle.ToString();
                    tr.Commit();
                    return h;
                }
            }
            catch { return id.Handle.ToString(); }
        }

        static JsonArray Xy(Point3d p)
        {
            return new JsonArray
            {
                JsonValue.Create(Math.Round(p.X, Decimals)),
                JsonValue.Create(Math.Round(p.Y, Decimals))
            };
        }

        static string Label(string label, string fallback)
        {
            return string.IsNullOrWhiteSpace(label) ? fallback : label;
        }
    }
}
