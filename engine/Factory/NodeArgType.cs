using System;
using System.Collections.Generic;
using System.Globalization;

namespace Civil3DFactory
{
    /// <summary>
    /// Parsed form of a node.json parameter type string.
    ///
    /// The contract layer originally had only the "value types" string/number/integer/boolean;
    /// the form layer got no semantics and fell back to a text box for everything, which is the root cause of hard-to-fill parameters.
    /// This adds a "semantic type" vocabulary to the contract, and the form builds controls from it:
    ///
    ///   enum[a|b|c]              read-only dropdown
    ///   list:dwg.layer           editable dropdown, candidates listed live from the current drawing (see Ops.UiListNames)
    ///   civil.alignment          same as list:civil.alignment, legacy spelling kept
    ///   pick.point               pick one point in the drawing   -> [x, y]
    ///   pick.points              pick several points             -> [[x,y], ...]
    ///   pick.entity&lt;polyline&gt;   select one object               -> handle string
    ///   pick.entities&lt;polyline&gt;  window-select objects           -> handle array
    ///   pick.distance            measure a distance              -> number
    ///   number{0..1}             with range validation
    ///   number{unit:m}           with unit suffix
    ///   number{0..1,unit:%}      both combined
    ///
    /// Modifiers stack: trailing ? means optional, trailing [] means array ([2] means fixed length 2).
    /// Unknown strings fall back to text, so old contracts keep working without a single edit.
    /// </summary>
    public sealed class NodeArgType
    {
        /// <summary>The raw string from node.json, with the optional question mark removed.</summary>
        public string Raw;
        /// <summary>Control kind: bool | number | integer | path | enum | list | pick | json | text.</summary>
        public string Kind = "text";
        public bool Optional;
        public bool IsArray;
        /// <summary>Length of a fixed-length array, e.g. 2 for path[2]; 0 when unbounded.</summary>
        public int ArrayLength;

        /// <summary>Candidate values when Kind == "enum".</summary>
        public List<string> EnumValues;
        /// <summary>Source key passed to Ops.UiListNames when Kind == "list", e.g. dwg.layer, civil.alignment.</summary>
        public string ListSource;

        /// <summary>Pick action when Kind == "pick": point | points | entity | entities | distance.</summary>
        public string PickKind;
        /// <summary>Type filter for entity picks, e.g. polyline, alignment; empty means unrestricted.</summary>
        public string PickFilter;

        public double? Min;
        public double? Max;
        public string Unit;
        /// <summary>
        /// Default value written as =value in the contract.
        /// Required parameters (`number=50`) are pre-filled; optional ones (`number?=50`) only show
        /// "default 50" in the type hint without pre-filling, since leaving it blank is how the node uses its own internal default.
        /// </summary>
        public string Default;
        /// <summary>
        /// `flow:` prefix: this item is only a pipeline data-dependency declaration; the node does not read it at run time
        /// and the parameter dialog hides it. It exists so the pipeline knows how stages connect, not for people to fill in.
        /// </summary>
        public bool FlowOnly;

        /// <summary>Type hint text for the right-hand column of the UI.</summary>
        public string Hint
        {
            get
            {
                string text;
                switch (Kind)
                {
                    case "enum": text = "Options"; break;
                    case "list": text = ListLabel(ListSource); break;
                    case "pick": text = PickLabel(PickKind, PickFilter); break;
                    default: text = Raw; break;
                }
                if (IsArray && Kind != "pick") text += ArrayLength > 0 ? " ×" + ArrayLength : " multiple";
                if (!string.IsNullOrEmpty(Unit)) text += " " + Unit;
                if (Min.HasValue || Max.HasValue)
                    text += " " + Bound(Min) + "~" + Bound(Max);

                // optional defaults are not pre-filled, but people should see what leaving it blank gives
                if (Optional && !string.IsNullOrEmpty(Default)) return text + " default " + Default;
                return text + (Optional ? " ?" : "");
            }
        }

        static string Bound(double? v)
        {
            return v.HasValue ? v.Value.ToString("0.###", CultureInfo.InvariantCulture) : "";
        }

        static string ListLabel(string source)
        {
            if (string.IsNullOrEmpty(source)) return "List";
            if (source.StartsWith("dwg.", StringComparison.OrdinalIgnoreCase))
            {
                switch (source.ToLowerInvariant())
                {
                    case "dwg.layer": return "Layer";
                    case "dwg.blockname": return "Block name";
                    case "dwg.textstyle": return "Text style";
                    case "dwg.dimstyle": return "Dim style";
                    case "dwg.linetype": return "Linetype";
                    case "dwg.ctb": return "Plot style table";
                    case "dwg.layout": return "Layout";
                }
            }
            if (source.StartsWith("civil.style.", StringComparison.OrdinalIgnoreCase)) return "Style";
            if (source.StartsWith("civil.labelset.", StringComparison.OrdinalIgnoreCase)) return "Label set";
            if (source.StartsWith("civil.bandset.", StringComparison.OrdinalIgnoreCase)) return "Band set";
            if (source.StartsWith("civil.codeset", StringComparison.OrdinalIgnoreCase)) return "Code set";
            return "Drawing object";
        }

        static string PickLabel(string kind, string filter)
        {
            switch (kind)
            {
                case "point": return "Point in drawing";
                case "points": return "Points in drawing";
                case "distance": return "Distance in drawing";
                case "entity": return "Drawing object" + (string.IsNullOrEmpty(filter) ? "" : "·" + filter);
                case "entities": return "Drawing objects" + (string.IsNullOrEmpty(filter) ? "" : "·" + filter);
            }
            return "Pick in drawing";
        }

        // ───────────────────────── Parsing ─────────────────────────

        public static NodeArgType Parse(string raw)
        {
            var t = new NodeArgType();
            string s = (raw ?? "string").Trim();

            // strip the flow: prefix first: it only says "this item stays out of the form"; the rest parses as usual
            if (s.StartsWith("flow:", StringComparison.OrdinalIgnoreCase))
            {
                t.FlowOnly = true;
                s = s.Substring(5).Trim();
            }

            // =value is the default. Take the first equals sign that is not inside brackets: bracketed ones belong to enum candidates or numeric modifiers,
            // and the default itself may contain braces (e.g. the layer name template CL-{channel}), so a simple right-to-left search will not do.
            int eq = TopLevelEquals(s);
            if (eq > 0)
            {
                t.Default = s.Substring(eq + 1).Trim();
                s = s.Substring(0, eq).Trim();
            }

            if (s.EndsWith("?", StringComparison.Ordinal))
            {
                t.Optional = true;
                s = s.Substring(0, s.Length - 1).Trim();
            }
            else
            {
                // legacy spelling puts the question mark between the type name and the structure: array?<{...}>, object?{...}
                int q = s.IndexOf('?');
                if (q > 0 && q < s.Length - 1 && (s[q + 1] == '<' || s[q + 1] == '{'))
                {
                    t.Optional = true;
                    s = s.Substring(0, q) + s.Substring(q + 1);
                }
            }

            // a trailing {...} is a numeric modifier; strip it before the array check so number{0..1} is not mistaken for a structure
            string modifiers = null;
            if (s.EndsWith("}", StringComparison.Ordinal))
            {
                int brace = s.LastIndexOf('{');
                // only treat it as a modifier when a type name precedes the {; a whole string like "{a:1}" is a structure, left to json
                if (brace > 0)
                {
                    modifiers = s.Substring(brace + 1, s.Length - brace - 2);
                    s = s.Substring(0, brace).Trim();
                }
            }

            // array suffix [] / [2]; but the brackets of enum[a|b] hold candidates and are not an array
            if (s.EndsWith("]", StringComparison.Ordinal)
                && !s.StartsWith("enum[", StringComparison.OrdinalIgnoreCase))
            {
                int bracket = s.LastIndexOf('[');
                if (bracket >= 0)
                {
                    string inner = s.Substring(bracket + 1, s.Length - bracket - 2).Trim();
                    int len;
                    if (inner.Length == 0 || int.TryParse(inner, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out len))
                    {
                        t.IsArray = true;
                        if (inner.Length > 0) int.TryParse(inner, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out t.ArrayLength);
                        s = s.Substring(0, bracket).Trim();
                    }
                }
            }

            t.Raw = s + (t.IsArray ? (t.ArrayLength > 0 ? "[" + t.ArrayLength + "]" : "[]") : "");
            ApplyModifiers(t, modifiers);

            string lower = s.ToLowerInvariant();

            if (lower.StartsWith("enum[", StringComparison.Ordinal) && s.EndsWith("]", StringComparison.Ordinal))
            {
                t.Kind = "enum";
                t.EnumValues = new List<string>();
                string body = s.Substring(5, s.Length - 6);
                foreach (string part in body.Split('|'))
                {
                    string v = part.Trim();
                    if (v.Length > 0) t.EnumValues.Add(v);
                }
                return t;
            }

            if (lower.StartsWith("pick.", StringComparison.Ordinal))
            {
                t.Kind = "pick";
                string rest = s.Substring(5).Trim();
                int lt = rest.IndexOf('<');
                if (lt > 0 && rest.EndsWith(">", StringComparison.Ordinal))
                {
                    t.PickFilter = rest.Substring(lt + 1, rest.Length - lt - 2).Trim();
                    rest = rest.Substring(0, lt).Trim();
                }
                t.PickKind = rest.ToLowerInvariant();
                if (t.PickKind == "points" || t.PickKind == "entities") t.IsArray = true;
                return t;
            }

            if (lower.StartsWith("list:", StringComparison.Ordinal))
            {
                t.Kind = "list";
                t.ListSource = s.Substring(5).Trim();
                return t;
            }

            // legacy spelling: civil.* directly means "list names from the drawing"
            if (lower.StartsWith("civil.", StringComparison.Ordinal))
            {
                t.Kind = "list";
                t.ListSource = s;
                return t;
            }

            switch (lower)
            {
                case "boolean": t.Kind = "bool"; break;
                case "number": t.Kind = "number"; break;
                case "integer": t.Kind = "integer"; break;
                case "path": t.Kind = "path"; break;
                case "string": t.Kind = "text"; break;
                default: t.Kind = null; break;
            }
            if (t.Kind != null)
            {
                // scalars wrapped in arrays (string[], number[]) can only be edited as JSON;
                // path[] is the exception: the form gives a multi-line box plus a browse button
                if (t.IsArray && t.Kind != "path") t.Kind = "json";
                return t;
            }

            // structured / unknown: hand over to the JSON editor, same behaviour as before the rework
            if (t.IsArray || lower.Contains("<") || lower.Contains("{") || lower.StartsWith("object", StringComparison.Ordinal)
                || lower.StartsWith("array", StringComparison.Ordinal))
            {
                t.Kind = "json";
                return t;
            }

            t.Kind = "text";
            return t;
        }

        /// <summary>Finds the first equals sign not inside [] / {} / &lt;&gt;; returns -1 if none.</summary>
        static int TopLevelEquals(string s)
        {
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '[' || c == '{' || c == '<') depth++;
                else if (c == ']' || c == '}' || c == '>') depth--;
                else if (c == '=' && depth <= 0) return i;
            }
            return -1;
        }

        /// <summary>Parses modifiers like {0..1,unit:m}.</summary>
        static void ApplyModifiers(NodeArgType t, string modifiers)
        {
            if (string.IsNullOrWhiteSpace(modifiers)) return;
            foreach (string chunk in modifiers.Split(','))
            {
                string part = chunk.Trim();
                if (part.Length == 0) continue;

                int range = part.IndexOf("..", StringComparison.Ordinal);
                if (range > 0)
                {
                    double lo, hi;
                    if (double.TryParse(part.Substring(0, range).Trim(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out lo)) t.Min = lo;
                    if (double.TryParse(part.Substring(range + 2).Trim(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out hi)) t.Max = hi;
                    continue;
                }

                int colon = part.IndexOf(':');
                if (colon > 0 && string.Equals(part.Substring(0, colon).Trim(), "unit",
                        StringComparison.OrdinalIgnoreCase))
                    t.Unit = part.Substring(colon + 1).Trim();
            }
        }
    }
}
