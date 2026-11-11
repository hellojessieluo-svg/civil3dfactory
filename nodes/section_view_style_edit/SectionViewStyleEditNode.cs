using System;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace Civil3DFactory
{
    /// <summary>
    /// Node section_view_style_edit: read or change a section view style's graph settings
    /// (vertical exaggeration). Pair with copy_styles to derive a project variant instead of
    /// editing a standard style in place. Memory only; save_dwg to persist.
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeSectionViewStyleEdit(JsonObject a, Document doc)
        {
            string name = Need(a, "name");
            bool dry = GetBool(a, "dry_run", true);
            Database db = doc.Database;
            var civ = Civ(db);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId id = FindStyleId(tr, civ.Styles.SectionViewStyles, name);
                if (id.IsNull) throw new InvalidOperationException("Section view style '" + name + "' not found.");
                var st = (Autodesk.Civil.DatabaseServices.Styles.SectionViewStyle)tr.GetObject(id, dry ? OpenMode.ForRead : OpenMode.ForWrite);
                double before = st.GraphStyle.VerticalExaggeration;
                double after = before;
                if (a["vertical_exaggeration"] != null)
                {
                    double v = a["vertical_exaggeration"].GetValue<double>();
                    if (v <= 0) throw new InvalidOperationException("vertical_exaggeration must be > 0.");
                    if (!dry) st.GraphStyle.VerticalExaggeration = v;
                    after = v;
                }
                // Title template (fields like <[...]> kept verbatim). title_text replaces it all;
                // title_replace [{from,to}] edits substrings, e.g. a fixed vertical scale.
                var ts = st.GraphStyle.TitleStyle;
                string titleBefore = ts.Text, titleAfter = titleBefore;
                if (a["title_text"] != null) titleAfter = a["title_text"].GetValue<string>();
                var misses = new JsonArray();
                if (a["title_replace"] is JsonArray reps)
                    foreach (JsonNode r in reps)
                    {
                        string f = r?["from"]?.GetValue<string>(), t = r?["to"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(f) || t == null) throw new InvalidOperationException("title_replace items need from and to.");
                        if (titleAfter.IndexOf(f, StringComparison.Ordinal) < 0) { misses.Add(f); continue; }
                        titleAfter = titleAfter.Replace(f, t);
                    }
                if (misses.Count > 0)
                    throw new InvalidOperationException("title_replace: not found in title template: " + misses.ToJsonString() + " (template: " + titleBefore + ")");
                if (!dry && titleAfter != titleBefore) ts.Text = titleAfter;
                if (dry) tr.Abort(); else tr.Commit();
                return new JsonObject
                {
                    ["style"] = name,
                    ["dry_run"] = dry,
                    ["vertical_exaggeration_before"] = before,
                    ["vertical_exaggeration_after"] = after,
                    ["title_text_before"] = titleBefore,
                    ["title_text_after"] = titleAfter
                };
            }
        }
    }
}
