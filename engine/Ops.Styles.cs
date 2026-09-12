using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// Style construction ops (the "styles" skill): create a style in any collection, shape a section /
    /// profile view style (axes, grid spacing, title, exaggeration), and set shape-style hatching.
    /// Display components (colour, layer, linetype, lineweight, visibility) go through style_display.
    /// </summary>
    public static partial class Ops
    {
        internal static void RegisterStyleOps()
        {
            Registry["create_style"] = new OpDef
            {
                Description = "Create an empty style (default settings) in any style collection, e.g. SectionViewStyles, SectionStyles, ShapeStyles, LinkStyles, CodeSetStyles. Existing style of that name is left as is",
                Parameters = "cat(required, collection path as in list_styles) name(required) exists_ok?(default true)",
                WritesDrawing = true,
                Run = CreateStyle
            };
            Registry["section_view_style"] = new OpDef
            {
                Description = "Read or shape a section view style (or a profile view style with cat=ProfileViewStyles): grid spacing via axis tick intervals, "
                            + "axis labels and titles, view title text/location, vertical exaggeration, grid padding; optional display component overrides. "
                            + "Without set-parameters it only reports the current settings",
                Parameters = "name(required) cat?(default SectionViewStyles) create?(default false; create when missing) "
                           + "grid?{major_h,minor_h(offset direction: bottom+top axis intervals),major_v,minor_v(elevation direction: left+right axis intervals)} "
                           + "axes?{bottom|top|left|right:{show?,major_interval?,minor_interval?,major_label?,minor_label?,text_height?(plotted mm),title?{text,location?,text_height?(mm)}}} "
                           + "title?{text?,location?(Top|Bottom|Left|Right),text_height?(plotted mm),justification?,border?,offset_x?,offset_y?(plotted mm)} "
                           + "graph?{vertical_exaggeration?,direction?(LeftToRight|RightToLeft)} padding?{above,below,left,right} "
                           + "display?[{component,set:{color,layer,linetype,lineweight,visible},view?}](forwarded to style_display, applied immediately)",
                WritesDrawing = true,
                Run = SectionViewStyleOp
            };
            Registry["shape_style"] = new OpDef
            {
                Description = "Read or set a shape style's section hatch (pattern, angle, scale) and, through display overrides, its fill colour - e.g. cut red / fill blue",
                Parameters = "name(required) create?(default false) hatch?{type?(pattern=PreDefined|solid=SolidFill|user|custom),pattern?(acad.pat name, e.g. ANSI31),angle?(degrees),scale?,spacing?} "
                           + "display?[{component,set:{color,layer,visible,...},view?(default Section)}](forwarded to style_display)",
                WritesDrawing = true,
                Run = ShapeStyleOp
            };
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        static ObjectId FindStyleExact(Transaction tr, object collection, string name)
        {
            var en = collection as System.Collections.IEnumerable;
            if (en == null) return ObjectId.Null;
            foreach (object item in en)
            {
                if (!(item is ObjectId)) continue;
                ObjectId oid = (ObjectId)item;
                if (oid.IsErased) continue;
                DBObject o;
                try { o = tr.GetObject(oid, OpenMode.ForRead); } catch { continue; }
                if (string.Equals(TryGetName(o), name, StringComparison.Ordinal)) return oid;
            }
            return ObjectId.Null;
        }

        static ObjectId AddStyle(object collection, string name)
        {
            MethodInfo add = collection.GetType().GetMethod("Add", new[] { typeof(string) });
            if (add == null) throw new InvalidOperationException("This style collection has no Add(name) method: " + collection.GetType().Name);
            try { return (ObjectId)add.Invoke(collection, new object[] { name }); }
            catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
        }

        /// <summary>Plotted text height: the API keeps it in drawing units (metres for metric drawings), users think in millimetres.</summary>
        static double Mm(double millimetres) { return millimetres / 1000.0; }

        static object ParseEnumLoose(Type t, string s)
        {
            if (string.IsNullOrWhiteSpace(s)) throw new InvalidOperationException("Empty value for " + t.Name);
            string[] names = Enum.GetNames(t);
            foreach (string n in names) if (string.Equals(n, s, StringComparison.OrdinalIgnoreCase)) return Enum.Parse(t, n);
            foreach (string n in names) if (n.StartsWith(s, StringComparison.OrdinalIgnoreCase)) return Enum.Parse(t, n);
            foreach (string n in names) if (n.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return Enum.Parse(t, n);
            throw new InvalidOperationException("Unknown " + t.Name + " value '" + s + "'. Choose from: " + string.Join(", ", names));
        }

        static object FindStyleOrCreate(Transaction tr, CivDoc civ, string cat, string name, bool create, out bool created)
        {
            created = false;
            object coll = ResolveStyleCollection(civ, cat);
            if (coll == null) throw new InvalidOperationException("Unknown style collection: " + cat + " (use list_styles for the category paths)");
            ObjectId id = FindStyleExact(tr, coll, name);
            if (id.IsNull)
            {
                if (!create) throw new InvalidOperationException("Style '" + name + "' not found in " + cat + " (pass create:true to create it)");
                id = AddStyle(coll, name);
                created = true;
            }
            return tr.GetObject(id, OpenMode.ForWrite);
        }

        static void ApplyDisplayOverrides(JsonObject a, string cat, string name, string defaultView, Document doc, JsonObject res)
        {
            var display = a["display"] as JsonArray;
            if (display == null) return;
            var applied = new JsonArray();
            foreach (JsonNode n in display)
            {
                var d = n as JsonObject; if (d == null) continue;
                var args = new JsonObject
                {
                    ["cat"] = cat,
                    ["name"] = name,
                    ["view"] = GetString(d, "view", defaultView),
                    ["component"] = GetString(d, "component", null),
                    ["dry_run"] = false
                };
                if (d["set"] != null) args["set"] = d["set"].DeepClone();
                applied.Add(Execute("style_display", args, doc));
            }
            res["display"] = applied;
        }

        // ------------------------------------------------------------------
        // create_style
        // ------------------------------------------------------------------

        static JsonNode CreateStyle(JsonObject a, Document doc)
        {
            string cat = Need(a, "cat");
            string name = Need(a, "name");
            bool existsOk = GetBool(a, "exists_ok", true);
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                object coll = ResolveStyleCollection(civ, cat);
                if (coll == null) throw new InvalidOperationException("Unknown style collection: " + cat);
                ObjectId id = FindStyleExact(tr, coll, name);
                bool created = false;
                if (id.IsNull) { id = AddStyle(coll, name); created = true; }
                else if (!existsOk) throw new InvalidOperationException("Style '" + name + "' already exists in " + cat);
                DBObject o = tr.GetObject(id, OpenMode.ForRead);
                tr.Commit();
                return new JsonObject { ["cat"] = cat, ["name"] = name, ["created"] = created, ["type"] = o.GetType().Name, ["handle"] = o.Handle.ToString() };
            }
        }

        // ------------------------------------------------------------------
        // section_view_style / profile view style
        // ------------------------------------------------------------------

        static AxisStyle AxisOf(object style, string prop)
        {
            PropertyInfo p = style.GetType().GetProperty(prop);
            return p == null ? null : p.GetValue(style, null) as AxisStyle;
        }

        static void ApplyAxis(AxisStyle ax, JsonObject spec, JsonObject log, string key)
        {
            if (ax == null || spec == null) return;
            var changed = new JsonArray();
            if (spec["show"] != null)
            {
                // Not every axis accepts this setter (the host throws InvalidOperationException); report instead of failing the op.
                try { ax.ShowTickAndLabel = spec["show"].GetValue<bool>(); changed.Add("show"); }
                catch (System.Exception ex) { changed.Add("show: not settable on this axis (" + ex.GetType().Name + ")"); }
            }
            if (spec["major_interval"] != null) { ax.MajorTickStyle.Interval = GetDouble(spec, "major_interval", 10); changed.Add("major_interval"); }
            if (spec["minor_interval"] != null) { ax.MinorTickStyle.Interval = GetDouble(spec, "minor_interval", 5); changed.Add("minor_interval"); }
            if (spec["major_label"] != null) { ax.MajorTickStyle.LabelText = spec["major_label"].ToString(); changed.Add("major_label"); }
            if (spec["minor_label"] != null) { ax.MinorTickStyle.LabelText = spec["minor_label"].ToString(); changed.Add("minor_label"); }
            if (spec["text_height"] != null) { ax.MajorTickStyle.TextHeight = Mm(GetDouble(spec, "text_height", 2.5)); changed.Add("text_height"); }
            if (spec["major_size"] != null) { ax.MajorTickStyle.Size = GetDouble(spec, "major_size", 2); changed.Add("major_size"); }
            if (spec["minor_size"] != null) { ax.MinorTickStyle.Size = GetDouble(spec, "minor_size", 1); changed.Add("minor_size"); }
            var title = spec["title"] as JsonObject;
            if (title != null)
            {
                if (title["text"] != null) { ax.TitleStyle.Text = title["text"].ToString(); changed.Add("title.text"); }
                if (title["location"] != null) { ax.TitleStyle.Location = (AxisTitleLocationType)ParseEnumLoose(typeof(AxisTitleLocationType), title["location"].ToString()); changed.Add("title.location"); }
                if (title["text_height"] != null) { ax.TitleStyle.TextHeight = Mm(GetDouble(title, "text_height", 3)); changed.Add("title.text_height"); }
                if (title["offset_x"] != null) { ax.TitleStyle.OffsetX = Mm(GetDouble(title, "offset_x", 0)); changed.Add("title.offset_x"); }
                if (title["offset_y"] != null) { ax.TitleStyle.OffsetY = Mm(GetDouble(title, "offset_y", 0)); changed.Add("title.offset_y"); }
                if (title["rotation"] != null) { ax.TitleStyle.Rotation = GetDouble(title, "rotation", 0) * Math.PI / 180.0; changed.Add("title.rotation"); }
            }
            if (changed.Count > 0) log[key] = changed;
        }

        static JsonObject DumpAxis(AxisStyle ax)
        {
            if (ax == null) return null;
            var o = new JsonObject();
            try { o["show"] = ax.ShowTickAndLabel; } catch { }
            try { o["major_interval"] = ax.MajorTickStyle.Interval; o["major_label"] = ax.MajorTickStyle.LabelText; o["text_height_mm"] = Math.Round(ax.MajorTickStyle.TextHeight * 1000, 3); } catch { }
            try { o["minor_interval"] = ax.MinorTickStyle.Interval; o["minor_label"] = ax.MinorTickStyle.LabelText; } catch { }
            try { o["title"] = new JsonObject { ["text"] = ax.TitleStyle.Text, ["location"] = ax.TitleStyle.Location.ToString(), ["text_height_mm"] = Math.Round(ax.TitleStyle.TextHeight * 1000, 3) }; } catch { }
            return o;
        }

        static JsonNode SectionViewStyleOp(JsonObject a, Document doc)
        {
            string name = Need(a, "name");
            string cat = GetString(a, "cat", "SectionViewStyles");
            bool create = GetBool(a, "create", false);
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var res = new JsonObject { ["cat"] = cat, ["name"] = name };
            var changed = new JsonObject();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                bool created;
                object style = FindStyleOrCreate(tr, civ, cat, name, create, out created);
                res["created"] = created;
                res["type"] = style.GetType().Name;

                // grid spacing = tick intervals of the paired axes
                var grid = a["grid"] as JsonObject;
                if (grid != null)
                {
                    foreach (string axName in new[] { "BottomAxis", "TopAxis" })
                    {
                        AxisStyle ax = AxisOf(style, axName);
                        if (ax == null) continue;
                        if (grid["major_h"] != null) ax.MajorTickStyle.Interval = GetDouble(grid, "major_h", 10);
                        if (grid["minor_h"] != null) ax.MinorTickStyle.Interval = GetDouble(grid, "minor_h", 5);
                    }
                    foreach (string axName in new[] { "LeftAxis", "RightAxis" })
                    {
                        AxisStyle ax = AxisOf(style, axName);
                        if (ax == null) continue;
                        if (grid["major_v"] != null) ax.MajorTickStyle.Interval = GetDouble(grid, "major_v", 5);
                        if (grid["minor_v"] != null) ax.MinorTickStyle.Interval = GetDouble(grid, "minor_v", 1);
                    }
                    changed["grid"] = grid.DeepClone();
                }

                var axes = a["axes"] as JsonObject;
                if (axes != null)
                {
                    ApplyAxis(AxisOf(style, "BottomAxis"), axes["bottom"] as JsonObject, changed, "axes.bottom");
                    ApplyAxis(AxisOf(style, "TopAxis"), axes["top"] as JsonObject, changed, "axes.top");
                    ApplyAxis(AxisOf(style, "LeftAxis"), axes["left"] as JsonObject, changed, "axes.left");
                    ApplyAxis(AxisOf(style, "RightAxis"), axes["right"] as JsonObject, changed, "axes.right");
                }

                var graphStyle = style.GetType().GetProperty("GraphStyle").GetValue(style, null) as GraphStyle;
                var title = a["title"] as JsonObject;
                if (title != null && graphStyle != null)
                {
                    GraphTitleStyle ts = graphStyle.TitleStyle;
                    var c = new JsonArray();
                    if (title["text"] != null) { ts.Text = title["text"].ToString(); c.Add("text"); }
                    if (title["location"] != null) { ts.Location = (GraphTitleLocationType)ParseEnumLoose(typeof(GraphTitleLocationType), title["location"].ToString()); c.Add("location"); }
                    if (title["text_height"] != null) { ts.TextHeight = Mm(GetDouble(title, "text_height", 4)); c.Add("text_height"); }
                    if (title["justification"] != null) { ts.Justification = (GraphTitleJustificationType)ParseEnumLoose(typeof(GraphTitleJustificationType), title["justification"].ToString()); c.Add("justification"); }
                    if (title["border"] != null) { ts.Border = title["border"].GetValue<bool>(); c.Add("border"); }
                    if (title["offset_x"] != null) { ts.OffsetX = Mm(GetDouble(title, "offset_x", 0)); c.Add("offset_x"); }
                    if (title["offset_y"] != null) { ts.OffsetY = Mm(GetDouble(title, "offset_y", 0)); c.Add("offset_y"); }
                    if (title["text_style"] != null) { ts.TextStyle = title["text_style"].ToString(); c.Add("text_style"); }
                    changed["title"] = c;
                }
                var graph = a["graph"] as JsonObject;
                if (graph != null && graphStyle != null)
                {
                    var c = new JsonArray();
                    if (graph["vertical_exaggeration"] != null) { graphStyle.VerticalExaggeration = GetDouble(graph, "vertical_exaggeration", 1); c.Add("vertical_exaggeration"); }
                    if (graph["direction"] != null) { graphStyle.Direction = (GraphDirectionType)ParseEnumLoose(typeof(GraphDirectionType), graph["direction"].ToString()); c.Add("direction"); }
                    changed["graph"] = c;
                }
                var padding = a["padding"] as JsonObject;
                var gridStyle = style.GetType().GetProperty("GridStyle").GetValue(style, null) as GridStyle;
                if (padding != null && gridStyle != null)
                {
                    if (padding["above"] != null) gridStyle.GridPaddingAbove = GetDouble(padding, "above", 0);
                    if (padding["below"] != null) gridStyle.GridPaddingBottom = GetDouble(padding, "below", 0);
                    if (padding["left"] != null) gridStyle.GridPaddingLeft = GetDouble(padding, "left", 0);
                    if (padding["right"] != null) gridStyle.GridPaddingRight = GetDouble(padding, "right", 0);
                    changed["padding"] = padding.DeepClone();
                }

                // report current state
                var dump = new JsonObject();
                foreach (string axName in new[] { "BottomAxis", "TopAxis", "LeftAxis", "RightAxis" })
                {
                    JsonObject d = DumpAxis(AxisOf(style, axName));
                    if (d != null) dump[axName] = d;
                }
                if (graphStyle != null)
                {
                    try
                    {
                        dump["title"] = new JsonObject
                        {
                            ["text"] = graphStyle.TitleStyle.Text,
                            ["location"] = graphStyle.TitleStyle.Location.ToString(),
                            ["text_height_mm"] = Math.Round(graphStyle.TitleStyle.TextHeight * 1000, 3),
                            ["justification"] = graphStyle.TitleStyle.Justification.ToString(),
                            ["border"] = graphStyle.TitleStyle.Border
                        };
                        dump["graph"] = new JsonObject { ["vertical_exaggeration"] = graphStyle.VerticalExaggeration, ["direction"] = graphStyle.Direction.ToString() };
                    }
                    catch { }
                }
                if (gridStyle != null)
                {
                    try { dump["padding"] = new JsonObject { ["above"] = gridStyle.GridPaddingAbove, ["below"] = gridStyle.GridPaddingBottom, ["left"] = gridStyle.GridPaddingLeft, ["right"] = gridStyle.GridPaddingRight }; } catch { }
                }
                res["style"] = dump;
                res["changed"] = changed;
                res["handle"] = ((DBObject)style).Handle.ToString();
                tr.Commit();
            }

            ApplyDisplayOverrides(a, cat, name, "Plan", doc, res);
            // components list (visibility / colour) after everything was applied
            try
            {
                res["components"] = Execute("style_display", new JsonObject { ["cat"] = cat, ["name"] = name, ["view"] = "Plan", ["dry_run"] = true }, doc);
            }
            catch (System.Exception ex) { res["components_error"] = ex.Message; }
            return res;
        }

        // ------------------------------------------------------------------
        // shape_style
        // ------------------------------------------------------------------

        static JsonNode ShapeStyleOp(JsonObject a, Document doc)
        {
            string name = Need(a, "name");
            const string cat = "ShapeStyles";
            bool create = GetBool(a, "create", false);
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var res = new JsonObject { ["cat"] = cat, ["name"] = name };

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                bool created;
                var style = (ShapeStyle)FindStyleOrCreate(tr, civ, cat, name, create, out created);
                res["created"] = created;
                var hatch = a["hatch"] as JsonObject;
                HatchDisplayStyle hs = style.GetHatchDisplayStyleSection();
                if (hatch != null && hs != null)
                {
                    var c = new JsonArray();
                    if (hatch["type"] != null)
                    {
                        // friendly aliases: pattern -> PreDefined (acad.pat), solid -> SolidFill, user -> UserDefined, custom -> CustomDefined
                        string t = hatch["type"].ToString().Trim().ToLowerInvariant();
                        if (t == "pattern" || t == "predefined") t = "PreDefined";
                        else if (t == "solid" || t == "solidfill") t = "SolidFill";
                        else if (t == "user" || t == "userdefined") t = "UserDefined";
                        else if (t == "custom" || t == "customdefined") t = "CustomDefined";
                        hs.HatchType = (HatchType)ParseEnumLoose(typeof(HatchType), t); c.Add("type");
                    }
                    if (hatch["pattern"] != null) { hs.Pattern = hatch["pattern"].ToString(); c.Add("pattern"); }
                    if (hatch["angle"] != null) { hs.Angle = GetDouble(hatch, "angle", 45) * Math.PI / 180.0; c.Add("angle"); }
                    if (hatch["scale"] != null) { hs.ScaleFactor = GetDouble(hatch, "scale", 1); c.Add("scale"); }
                    if (hatch["spacing"] != null) { hs.Spacing = GetDouble(hatch, "spacing", 1); c.Add("spacing"); }
                    res["changed"] = c;
                }
                if (hs != null)
                {
                    try
                    {
                        res["hatch"] = new JsonObject
                        {
                            ["type"] = hs.HatchType.ToString(),
                            ["pattern"] = hs.Pattern,
                            ["angle_deg"] = Math.Round(hs.Angle * 180.0 / Math.PI, 3),
                            ["scale"] = hs.ScaleFactor,
                            ["spacing"] = hs.Spacing
                        };
                    }
                    catch { }
                }
                res["handle"] = style.Handle.ToString();
                tr.Commit();
            }
            ApplyDisplayOverrides(a, cat, name, "Section", doc, res);
            try
            {
                res["components"] = Execute("style_display", new JsonObject { ["cat"] = cat, ["name"] = name, ["view"] = "Section", ["dry_run"] = true }, doc);
            }
            catch (System.Exception ex) { res["components_error"] = ex.Message; }
            return res;
        }
    }
}
