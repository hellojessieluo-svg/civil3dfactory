using System;
using System.Collections.Generic;
using System.Globalization;

namespace Civil3DFactory
{
    /// <summary>
    /// node.json 参数类型串的解析结果。
    ///
    /// 契约层原本只有 string/number/integer/boolean 这几个「值类型」，
    /// 表单层拿不到语义，只能一律退回文本框——这是参数难填的根因。
    /// 这里给契约补一层「语义类型」词汇，表单照着长控件：
    ///
    ///   enum[a|b|c]              只读下拉
    ///   list:dwg.layer           可编辑下拉，候选从当前图纸现列（见 Ops.UiListNames）
    ///   civil.alignment          等价 list:civil.alignment，保留旧写法
    ///   pick.point               图中拾取一点        → [x, y]
    ///   pick.points              图中连续拾取        → [[x,y], …]
    ///   pick.entity&lt;polyline&gt;   图中选一个对象      → 句柄字符串
    ///   pick.entities&lt;polyline&gt;  图中框选            → 句柄数组
    ///   pick.distance            图中量距离          → number
    ///   number{0..1}             带范围校验
    ///   number{unit:m}           带单位后缀
    ///   number{0..1,unit:%}      两者可组合
    ///
    /// 修饰符可叠加：尾部 ? 表示可选，尾部 [] 表示数组（[2] 表示定长 2）。
    /// 不认识的串一律退回 text，旧契约一个字不改也能继续跑。
    /// </summary>
    public sealed class NodeArgType
    {
        /// <summary>node.json 里的原始串，去掉可选问号。</summary>
        public string Raw;
        /// <summary>控件类别：bool | number | integer | path | enum | list | pick | json | text。</summary>
        public string Kind = "text";
        public bool Optional;
        public bool IsArray;
        /// <summary>定长数组的长度，如 path[2] 为 2；不定长为 0。</summary>
        public int ArrayLength;

        /// <summary>Kind == "enum" 时的候选值。</summary>
        public List<string> EnumValues;
        /// <summary>Kind == "list" 时传给 Ops.UiListNames 的源键，如 dwg.layer、civil.alignment。</summary>
        public string ListSource;

        /// <summary>Kind == "pick" 时的拾取动作：point | points | entity | entities | distance。</summary>
        public string PickKind;
        /// <summary>实体拾取的类型过滤，如 polyline、alignment；空表示不限。</summary>
        public string PickFilter;

        public double? Min;
        public double? Max;
        public string Unit;
        /// <summary>
        /// 契约里用 =值 写的默认值。
        /// 必填参数（`number=50`）会预填进框；可选参数（`number?=50`）只在类型提示里
        /// 显示"默认 50"而不预填——留空正是让节点走自己的内部缺省。
        /// </summary>
        public string Default;
        /// <summary>
        /// `flow:` 前缀：这一项只是流水线的数据依赖声明，节点执行时并不读它，
        /// 参数对话框不显示。写它是为了让流水线知道上下游怎么接，不是为了让人填。
        /// </summary>
        public bool FlowOnly;

        /// <summary>界面右侧那列的类型提示文字。</summary>
        public string Hint
        {
            get
            {
                string text;
                switch (Kind)
                {
                    case "enum": text = "选项"; break;
                    case "list": text = ListLabel(ListSource); break;
                    case "pick": text = PickLabel(PickKind, PickFilter); break;
                    default: text = Raw; break;
                }
                if (IsArray && Kind != "pick") text += ArrayLength > 0 ? " ×" + ArrayLength : " 多个";
                if (!string.IsNullOrEmpty(Unit)) text += " " + Unit;
                if (Min.HasValue || Max.HasValue)
                    text += " " + Bound(Min) + "~" + Bound(Max);

                // 可选参数的默认值不预填，但要让人看见留空会得到什么
                if (Optional && !string.IsNullOrEmpty(Default)) return text + " 默认 " + Default;
                return text + (Optional ? " ?" : "");
            }
        }

        static string Bound(double? v)
        {
            return v.HasValue ? v.Value.ToString("0.###", CultureInfo.InvariantCulture) : "";
        }

        static string ListLabel(string source)
        {
            if (string.IsNullOrEmpty(source)) return "列表";
            if (source.StartsWith("dwg.", StringComparison.OrdinalIgnoreCase))
            {
                switch (source.ToLowerInvariant())
                {
                    case "dwg.layer": return "图层";
                    case "dwg.blockname": return "块名";
                    case "dwg.textstyle": return "文字样式";
                    case "dwg.dimstyle": return "标注样式";
                    case "dwg.linetype": return "线型";
                    case "dwg.ctb": return "打印样式表";
                    case "dwg.layout": return "布局";
                }
            }
            if (source.StartsWith("civil.style.", StringComparison.OrdinalIgnoreCase)) return "样式";
            if (source.StartsWith("civil.labelset.", StringComparison.OrdinalIgnoreCase)) return "标注集";
            if (source.StartsWith("civil.bandset.", StringComparison.OrdinalIgnoreCase)) return "带状集";
            if (source.StartsWith("civil.codeset", StringComparison.OrdinalIgnoreCase)) return "代码集";
            return "图中对象";
        }

        static string PickLabel(string kind, string filter)
        {
            switch (kind)
            {
                case "point": return "图中一点";
                case "points": return "图中多点";
                case "distance": return "图中距离";
                case "entity": return "图中对象" + (string.IsNullOrEmpty(filter) ? "" : "·" + filter);
                case "entities": return "图中多个" + (string.IsNullOrEmpty(filter) ? "" : "·" + filter);
            }
            return "图中拾取";
        }

        // ───────────────────────── 解析 ─────────────────────────

        public static NodeArgType Parse(string raw)
        {
            var t = new NodeArgType();
            string s = (raw ?? "string").Trim();

            // flow: 前缀最先剥：它只说明"这一项不进表单"，剩下的照常解析
            if (s.StartsWith("flow:", StringComparison.OrdinalIgnoreCase))
            {
                t.FlowOnly = true;
                s = s.Substring(5).Trim();
            }

            // =值 是默认值。取第一个「不在括号里」的等号：括号内的属于 enum 候选或数值修饰，
            // 而默认值本身可能带花括号（如图层名模板 CL-{channel}），所以不能简单地从右往左找。
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
                // 老写法把问号夹在类型名和结构体中间：array?<{…}>、object?{…}
                int q = s.IndexOf('?');
                if (q > 0 && q < s.Length - 1 && (s[q + 1] == '<' || s[q + 1] == '{'))
                {
                    t.Optional = true;
                    s = s.Substring(0, q) + s.Substring(q + 1);
                }
            }

            // 尾部 {…} 是数值修饰，先摘下来再判数组，避免 number{0..1} 被误当结构体
            string modifiers = null;
            if (s.EndsWith("}", StringComparison.Ordinal))
            {
                int brace = s.LastIndexOf('{');
                // 只有 { 之前还有类型名时才当修饰符；"{a:1}" 这种整串是结构体，留给 json
                if (brace > 0)
                {
                    modifiers = s.Substring(brace + 1, s.Length - brace - 2);
                    s = s.Substring(0, brace).Trim();
                }
            }

            // 数组后缀 [] / [2]，但 enum[a|b] 的方括号是候选值，不能当数组
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

            // 旧写法：civil.* 直接就是「从图里列名字」
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
                // 标量套了数组（string[]、number[]）就只能编 JSON；
                // path[] 例外，表单那边给的是多行框加浏览按钮
                if (t.IsArray && t.Kind != "path") t.Kind = "json";
                return t;
            }

            // 结构化 / 未知：交给 JSON 编辑框，行为与改造前一致
            if (t.IsArray || lower.Contains("<") || lower.Contains("{") || lower.StartsWith("object", StringComparison.Ordinal)
                || lower.StartsWith("array", StringComparison.Ordinal))
            {
                t.Kind = "json";
                return t;
            }

            t.Kind = "text";
            return t;
        }

        /// <summary>找出第一个不在 [] / {} / &lt;&gt; 内部的等号；没有返回 -1。</summary>
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

        /// <summary>解析 {0..1,unit:m} 这类修饰。</summary>
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
