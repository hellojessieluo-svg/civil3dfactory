using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    /// <summary>
    /// 由 node.json 契约现场生成的参数对话框：
    /// inputs 给下拉（从当前图纸列名字），parameters 按类型给相应编辑器。
    /// 上次填过的值按节点存到 %LOCALAPPDATA%\Civil3DFactory\ui\，下次直接带出来。
    /// </summary>
    public sealed class NodeParamsForm : Form
    {
        readonly NodeDef _node;
        readonly Document _doc;
        readonly List<ArgRow> _rows = new List<ArgRow>();
        Action _fitTable;
        bool _hasXyPair;

        public JsonObject Args { get; private set; }

        sealed class ArgRow
        {
            public NodeArgDef Def;
            /// <summary>契约类型的语义解析结果，决定长什么控件。</summary>
            public NodeArgType Type;
            public Control Editor;
            /// <summary>取值方式：bool | number | integer | json | text | path。</summary>
            public string Kind;
            public string LiteralDefault;
        }

        public NodeParamsForm(NodeDef node, Document doc)
        {
            _node = node;
            _doc = doc;

            Text = "Civil3DFactory · " + node.Title;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = true;
            ShowInTaskbar = false;
            ClientSize = new Size(760, 580);
            MinimumSize = new Size(560, 320);
            Font = new Font("Microsoft YaHei UI", 9F);

            _hasXyPair = DetectXyPair();
            Controls.Add(BuildBody());
            Controls.Add(BuildHeader());
            Controls.Add(BuildFooter());

            LoadSavedValues();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_fitTable != null) _fitTable();
        }

        // ───────────────────────── 界面 ─────────────────────────

        Control BuildHeader()
        {
            var panel = new Panel { Dock = DockStyle.Top, Height = 76, Padding = new Padding(12, 10, 12, 6) };

            var title = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 24,
                Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
                Text = _node.Title + "   [" + _node.Id + "]"
            };

            OpDef op;
            string desc = Ops.Registry.TryGetValue(_node.Id, out op) && op != null ? op.Description : "";
            if (_node.Effect == "persist") desc = "【会落盘/写文件】" + desc;
            else if (_node.Effect == "modify") desc = "【会修改当前图纸】" + desc;

            var hint = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                ForeColor = Color.FromArgb(90, 90, 90),
                Text = desc
            };

            panel.Controls.Add(hint);
            panel.Controls.Add(title);
            return panel;
        }

        Control BuildBody()
        {
            var host = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12, 4, 12, 4) };

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 3,
                GrowStyle = TableLayoutPanelGrowStyle.AddRows
            };
            // AutoSize + 百分比列会按内容撑宽把第三列挤出可视区，所以按容器宽度封顶
            _fitTable = delegate
            {
                int width = host.ClientSize.Width - host.Padding.Horizontal;
                if (width > 0) table.MaximumSize = new Size(width, 0);
            };
            host.Resize += delegate { _fitTable(); };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));

            if (_node.Args.Count == 0)
            {
                table.Controls.Add(new Label
                {
                    Text = "该节点没有参数，直接执行即可。",
                    AutoSize = true,
                    Margin = new Padding(3, 8, 3, 8)
                }, 0, 0);
                table.SetColumnSpan(table.GetControlFromPosition(0, 0), 3);
                host.Controls.Add(table);
                return host;
            }

            int row = 0;
            foreach (NodeArgDef arg in _node.Args)
            {
                // flow: 标记的只是流水线数据依赖，节点执行时不读，别摆出来占位置
                if (arg.Parsed != null && arg.Parsed.FlowOnly) continue;

                ArgRow r = BuildRow(arg);
                _rows.Add(r);

                var label = new Label
                {
                    Text = arg.Key + (arg.Optional ? "" : " *"),
                    AutoSize = false,
                    Height = 24,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Anchor = AnchorStyles.Left | AnchorStyles.Top,
                    Width = 164,
                    ForeColor = arg.Optional ? Color.FromArgb(60, 60, 60) : Color.FromArgb(150, 20, 20)
                };

                var typeHint = new Label
                {
                    Text = TypeHint(arg),
                    AutoSize = false,
                    Height = 24,
                    Width = 116,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = Color.FromArgb(120, 120, 120)
                };

                table.Controls.Add(label, 0, row);
                table.Controls.Add(WrapEditor(r), 1, row);
                table.Controls.Add(typeHint, 2, row);
                row++;
            }

            host.Controls.Add(table);
            return host;
        }

        Control WrapEditor(ArgRow r)
        {
            r.Editor.Margin = new Padding(3, 2, 3, 6);

            if (r.Type != null && r.Type.Kind == "pick")
                return WithSideButton(r, PickButtonText(r.Type), delegate { PickInto(r); });

            // 约定：节点同时有 x / y 两个数值参数时，在 x 那行给一个拾取按钮，一次填两格。
            // 插入点几乎都是这么成对出现的，为此单开契约字段不值得。
            if (_hasXyPair && string.Equals(r.Def.Key, "x", StringComparison.OrdinalIgnoreCase))
                return WithSideButton(r, "图中拾取", delegate { PickXy(); });

            if (r.Kind == "path")
                return WithSideButton(r, "浏览…", delegate { Browse(r); });

            r.Editor.Dock = DockStyle.Fill;
            return r.Editor;
        }

        /// <summary>编辑器右侧挂一个动作按钮（浏览 / 拾取），多行编辑器按钮顶在右上。</summary>
        Control WithSideButton(ArgRow r, string text, EventHandler onClick)
        {
            int height = Math.Max(r.Editor.Height, 24);
            var holder = new Panel
            {
                Height = height + 2,
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 2, 3, 6)
            };
            var button = new Button
            {
                Text = text,
                Dock = DockStyle.Right,
                Width = 84,
                Height = 24
            };
            if (height > 26)
            {
                // 多行框：按钮不跟着拉长，贴右上角
                button.Dock = DockStyle.None;
                button.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                button.Left = holder.Width - button.Width;
                holder.Resize += delegate { button.Left = holder.ClientSize.Width - button.Width; };
                r.Editor.Dock = DockStyle.None;
                r.Editor.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                r.Editor.Left = 0;
                r.Editor.Top = 0;
                holder.Resize += delegate
                {
                    r.Editor.Width = Math.Max(40, holder.ClientSize.Width - button.Width - 6);
                };
            }
            else
            {
                r.Editor.Dock = DockStyle.Fill;
            }
            button.Click += onClick;

            holder.Controls.Add(r.Editor);
            holder.Controls.Add(button);
            return holder;
        }

        static string PickButtonText(NodeArgType t)
        {
            switch (t.PickKind)
            {
                case "points": return "图中连点";
                case "entities": return "图中框选";
                case "distance": return "图中量取";
            }
            return "图中拾取";
        }

        // ───────────────────────── 图中拾取 ─────────────────────────

        void PickInto(ArgRow r)
        {
            JsonNode value = EntityPicker.Pick(this, _doc, r.Type, r.Def.Key);
            if (value == null) return;
            SetEditorText(r, value is JsonValue ? value.ToString() : value.ToJsonString());
        }

        /// <summary>拾取一点，同时填进 x 和 y 两行。</summary>
        void PickXy()
        {
            JsonNode value = EntityPicker.PickPoint(this, _doc, "插入点");
            JsonArray xy = value as JsonArray;
            if (xy == null || xy.Count < 2) return;

            foreach (ArgRow row in _rows)
            {
                if (string.Equals(row.Def.Key, "x", StringComparison.OrdinalIgnoreCase))
                    SetEditorText(row, xy[0].ToString());
                else if (string.Equals(row.Def.Key, "y", StringComparison.OrdinalIgnoreCase))
                    SetEditorText(row, xy[1].ToString());
            }
        }

        /// <summary>节点是否有成对的 x / y 数值参数。</summary>
        bool DetectXyPair()
        {
            bool hasX = false, hasY = false;
            foreach (NodeArgDef a in _node.Args)
            {
                NodeArgType t = a.Parsed;
                if (t == null || (t.Kind != "number" && t.Kind != "integer")) continue;
                if (string.Equals(a.Key, "x", StringComparison.OrdinalIgnoreCase)) hasX = true;
                else if (string.Equals(a.Key, "y", StringComparison.OrdinalIgnoreCase)) hasY = true;
            }
            return hasX && hasY;
        }

        ArgRow BuildRow(NodeArgDef arg)
        {
            string type = (arg.Type ?? "string").Trim();
            NodeArgType t = arg.Parsed ?? NodeArgType.Parse(type);
            var r = new ArgRow { Def = arg, Type = t };

            switch (t.Kind)
            {
                case "enum":
                    {
                        // 值域是契约写死的，只读下拉，选不出契约外的东西
                        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Height = 24 };
                        if (t.Optional) combo.Items.Add("");
                        if (t.EnumValues != null)
                            foreach (string v in t.EnumValues) combo.Items.Add(v);
                        r.Editor = combo;
                        r.Kind = "text";
                        return r;
                    }

                case "list":
                    {
                        List<string> candidates = Ops.UiListNames(t.ListSource, _doc);

                        // 要选多个（如「哪几个图层是中心线」）就给复选清单，勾完直接成数组
                        if (t.IsArray)
                        {
                            if (candidates.Count > 0)
                            {
                                var checks = new CheckedListBox
                                {
                                    Height = 96,
                                    CheckOnClick = true,
                                    IntegralHeight = false
                                };
                                foreach (string n in candidates) checks.Items.Add(n);
                                r.Editor = checks;
                                r.Kind = "json";
                                return r;
                            }
                            // 图里列不出候选（比如还没建图层）就退回手编 JSON，不能把人挡死
                            r.Editor = new TextBox
                            {
                                Multiline = true,
                                ScrollBars = ScrollBars.Vertical,
                                Height = 64,
                                WordWrap = false
                            };
                            r.Kind = "json";
                            return r;
                        }

                        // 候选从当前图纸现列；列不出来也允许手打，不能因为列不出就挡住执行
                        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Height = 24 };
                        foreach (string n in candidates) combo.Items.Add(n);
                        if (combo.Items.Count > 0)
                        {
                            combo.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
                            combo.AutoCompleteSource = AutoCompleteSource.ListItems;
                        }
                        r.Editor = combo;
                        r.Kind = "text";
                        return r;
                    }

                case "pick":
                    {
                        r.Editor = new TextBox
                        {
                            Height = 24,
                            Multiline = t.IsArray,
                            ScrollBars = t.IsArray ? ScrollBars.Vertical : ScrollBars.None,
                            WordWrap = false
                        };
                        if (t.IsArray) r.Editor.Height = 48;
                        // 拾取回来的形态决定怎么收：句柄是字符串，距离是数字，点/多选是 JSON
                        if (t.PickKind == "entity") r.Kind = "text";
                        else if (t.PickKind == "distance") r.Kind = "number";
                        else r.Kind = "json";
                        return r;
                    }

                case "bool":
                    r.Editor = new CheckBox { Text = "启用", Height = 24, AutoSize = false };
                    r.Kind = "bool";
                    return r;

                case "path":
                    r.Editor = t.IsArray
                        ? (Control)new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 48, WordWrap = false }
                        : new TextBox { Height = 24 };
                    r.Kind = t.IsArray ? "json" : "path";
                    return r;

                case "number":
                case "integer":
                    r.Editor = new TextBox { Height = 24 };
                    r.Kind = t.Kind;
                    return r;

                case "json":
                    // 数组 / 结构化参数：直接编 JSON，避免为每种结构做专用控件
                    r.Editor = new TextBox
                    {
                        Multiline = true,
                        ScrollBars = ScrollBars.Vertical,
                        Height = 64,
                        WordWrap = false
                    };
                    r.Kind = "json";
                    return r;
            }

            r.Editor = new TextBox { Height = 24 };
            r.Kind = "text";
            // 契约里写死取值的枚举（如 selection:"all"）直接当默认值填上
            if (!string.Equals(type, "string", StringComparison.OrdinalIgnoreCase)) r.LiteralDefault = type;
            return r;
        }

        Control BuildFooter()
        {
            var panel = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(12, 8, 12, 8) };

            var run = new Button { Text = "执行", Width = 90, Height = 30, Dock = DockStyle.Right };
            var cancel = new Button { Text = "取消", Width = 90, Height = 30, Dock = DockStyle.Right };
            var spacer = new Panel { Width = 8, Dock = DockStyle.Right };
            var copy = new Button { Text = "复制工单 JSON", Width = 130, Height = 30, Dock = DockStyle.Left };
            var reset = new Button { Text = "清空", Width = 70, Height = 30, Dock = DockStyle.Left };

            run.Click += delegate { OnRun(); };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            copy.Click += delegate { CopyJson(); };
            reset.Click += delegate { ResetValues(); };

            panel.Controls.Add(run);
            panel.Controls.Add(spacer);
            panel.Controls.Add(cancel);
            panel.Controls.Add(new Panel { Width = 8, Dock = DockStyle.Left });
            panel.Controls.Add(reset);
            panel.Controls.Add(copy);

            AcceptButton = run;
            CancelButton = cancel;
            return panel;
        }

        // ───────────────────────── 取值 ─────────────────────────

        void OnRun()
        {
            JsonObject args;
            List<string> missing;
            if (!TryCollect(out args, out missing)) return;

            if (missing.Count > 0)
            {
                DialogResult ans = MessageBox.Show(
                    "以下必填参数为空：\r\n  " + string.Join("\r\n  ", missing.ToArray())
                    + "\r\n\r\n仍然执行吗？（节点内部可能有自己的缺省值）",
                    "参数不完整", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (ans != DialogResult.Yes) return;
            }

            Args = args;
            SaveValues(args);
            DialogResult = DialogResult.OK;
            Close();
        }

        bool TryCollect(out JsonObject args, out List<string> missing)
        {
            args = new JsonObject { ["node"] = _node.Id };
            missing = new List<string>();

            foreach (ArgRow r in _rows)
            {
                if (r.Kind == "bool")
                {
                    args[r.Def.Key] = JsonValue.Create(((CheckBox)r.Editor).Checked);
                    continue;
                }

                string text = EditorText(r).Trim();
                if (text.Length == 0)
                {
                    if (!r.Def.Optional) missing.Add(r.Def.Key);
                    continue;
                }

                switch (r.Kind)
                {
                    case "number":
                        {
                            double d;
                            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                            {
                                Complain(r, "需要一个数字");
                                return false;
                            }
                            if (!InRange(r, d)) return false;
                            args[r.Def.Key] = JsonValue.Create(d);
                            break;
                        }
                    case "integer":
                        {
                            int i;
                            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                            {
                                Complain(r, "需要一个整数");
                                return false;
                            }
                            if (!InRange(r, i)) return false;
                            args[r.Def.Key] = JsonValue.Create(i);
                            break;
                        }
                    case "json":
                        {
                            JsonNode node;
                            try { node = JsonNode.Parse(text); }
                            catch (System.Exception ex)
                            {
                                Complain(r, "不是合法 JSON：" + ex.Message);
                                return false;
                            }
                            args[r.Def.Key] = node;
                            break;
                        }
                    default:
                        args[r.Def.Key] = JsonValue.Create(text);
                        break;
                }
            }
            return true;
        }

        /// <summary>契约里写了 {min..max} 的数值参数，在这里就拦住，不用等节点跑一半才报错。</summary>
        bool InRange(ArgRow r, double v)
        {
            NodeArgType t = r.Type;
            if (t == null) return true;
            if (t.Min.HasValue && v < t.Min.Value)
            {
                Complain(r, "不能小于 " + t.Min.Value.ToString("0.###", CultureInfo.InvariantCulture));
                return false;
            }
            if (t.Max.HasValue && v > t.Max.Value)
            {
                Complain(r, "不能大于 " + t.Max.Value.ToString("0.###", CultureInfo.InvariantCulture));
                return false;
            }
            return true;
        }

        void Complain(ArgRow r, string message)
        {
            MessageBox.Show("参数 " + r.Def.Key + " " + message, "参数有误",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            r.Editor.Focus();
        }

        string EditorText(ArgRow r)
        {
            var checks = r.Editor as CheckedListBox;
            if (checks != null)
            {
                if (checks.CheckedItems.Count == 0) return "";
                var arr = new JsonArray();
                foreach (object item in checks.CheckedItems)
                    arr.Add(JsonValue.Create(Convert.ToString(item)));
                return arr.ToJsonString();
            }
            var combo = r.Editor as ComboBox;
            if (combo != null) return combo.Text ?? "";
            var box = r.Editor as TextBox;
            if (box != null) return box.Text ?? "";
            return "";
        }

        void SetEditorText(ArgRow r, string text)
        {
            var checks = r.Editor as CheckedListBox;
            if (checks != null)
            {
                var wanted = new List<string>();
                try
                {
                    JsonArray arr = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonArray;
                    if (arr != null)
                        foreach (JsonNode n in arr)
                            if (n != null) wanted.Add(n.ToString());
                }
                catch { }

                for (int i = 0; i < checks.Items.Count; i++)
                    checks.SetItemChecked(i, wanted.Contains(Convert.ToString(checks.Items[i])));
                return;
            }

            var combo = r.Editor as ComboBox;
            if (combo != null)
            {
                // 只读下拉设 Text 不生效，得按项匹配；匹配不上就留空不选
                if (combo.DropDownStyle == ComboBoxStyle.DropDownList)
                {
                    int index = -1;
                    for (int i = 0; i < combo.Items.Count; i++)
                    {
                        if (string.Equals(Convert.ToString(combo.Items[i]), text ?? "", StringComparison.Ordinal))
                        {
                            index = i;
                            break;
                        }
                    }
                    combo.SelectedIndex = index;
                    return;
                }
                combo.Text = text;
                return;
            }
            var box = r.Editor as TextBox;
            if (box != null) box.Text = text;
        }

        void Browse(ArgRow r)
        {
            string key = r.Def.Key.ToLowerInvariant();
            if (key.Contains("dir") || key.Contains("folder"))
            {
                using (var dlg = new FolderBrowserDialog())
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK) SetEditorText(r, dlg.SelectedPath);
                }
                return;
            }
            using (var dlg = new OpenFileDialog { CheckFileExists = false, Title = r.Def.Key })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) SetEditorText(r, dlg.FileName);
            }
        }

        void CopyJson()
        {
            JsonObject args;
            List<string> missing;
            if (!TryCollect(out args, out missing)) return;
            try
            {
                Clipboard.SetText(args.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }));
                MessageBox.Show("已复制，可直接粘进工单的 ops 数组。", "复制工单 JSON",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show("复制失败：" + ex.Message, "复制工单 JSON",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void ResetValues()
        {
            foreach (ArgRow r in _rows)
            {
                // 可选参数的 =值 只是把实现里的缺省抄给人看（显示在类型提示里），不预填：
                // 留空才是"让节点走自己的默认"，预填反而把值写死进工单。
                // 必填参数的 =值 照旧预填，那是契约主张的起始值。
                string preset = r.Type != null && !r.Type.Optional ? r.Type.Default : null;
                if (preset == null) preset = r.LiteralDefault;

                if (r.Kind == "bool")
                {
                    ((CheckBox)r.Editor).Checked = preset != null
                        && (string.Equals(preset, "true", StringComparison.OrdinalIgnoreCase)
                            || preset == "1");
                    continue;
                }

                SetEditorText(r, preset ?? "");
            }
        }

        // ───────────────────────── 记忆上次的值 ─────────────────────────

        string StorePath()
        {
            string dir = Path.Combine(FactoryPaths.UserDir(), "ui");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, _node.Id + ".json");
        }

        void LoadSavedValues()
        {
            ResetValues();

            JsonObject saved = ReadJsonObject(StorePath());
            if (saved == null) saved = SeedFromNodeTest();
            if (saved == null) return;

            foreach (ArgRow r in _rows)
            {
                JsonNode v = saved[r.Def.Key];
                if (v == null) continue;
                if (LooksLikeTypeString(r, v)) continue;
                try
                {
                    if (r.Kind == "bool")
                    {
                        ((CheckBox)r.Editor).Checked = v.GetValue<bool>();
                        continue;
                    }
                    if (r.Kind == "json" || v is JsonArray || v is JsonObject)
                    {
                        SetEditorText(r, v.ToJsonString());
                        continue;
                    }
                    SetEditorText(r, v.ToString());
                }
                catch { }
            }
        }

        /// <summary>
        /// 历史值是不是旧插件留下的脏数据。
        ///
        /// 契约加新类型串（如 list:dwg.layer）时，还没换的旧插件不认识它，会走兜底分支
        /// 把整个类型串当"字面默认值"填进框；人一点执行就跟着存进了历史文件，
        /// 之后即便插件换新、下拉恢复正常，也会被这份脏历史顶回去。
        /// 只要读回来的值就是类型串本身、或带着新语法的前缀，一律丢弃。
        /// </summary>
        static bool LooksLikeTypeString(ArgRow r, JsonNode v)
        {
            if (!(v is JsonValue)) return false;
            string text = v.ToString();
            if (string.IsNullOrEmpty(text)) return false;

            if (string.Equals(text, r.Def.Type, StringComparison.Ordinal)) return true;
            return text.StartsWith("list:", StringComparison.Ordinal)
                || text.StartsWith("pick.", StringComparison.Ordinal)
                || text.StartsWith("flow:", StringComparison.Ordinal)
                || text.StartsWith("enum[", StringComparison.Ordinal);
        }

        /// <summary>没有历史值时，用节点自己的 test.json 里的示例参数打底。</summary>
        JsonObject SeedFromNodeTest()
        {
            try
            {
                string root = FactoryPaths.Root;
                if (string.IsNullOrEmpty(root)) return null;
                JsonObject test = ReadJsonObject(Path.Combine(root, "nodes", _node.Id, "test.json"));
                if (test == null) return null;
                return test["parameters"] as JsonObject;
            }
            catch { return null; }
        }

        void SaveValues(JsonObject args)
        {
            try
            {
                var copy = args.DeepClone() as JsonObject;
                if (copy != null) copy.Remove("node");
                File.WriteAllText(StorePath(),
                    copy != null ? copy.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : "{}",
                    new UTF8Encoding(false));
            }
            catch { }
        }

        static JsonObject ReadJsonObject(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                return JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
            }
            catch { return null; }
        }

        /// <summary>界面右侧显示的类型提示。</summary>
        static string TypeHint(NodeArgDef arg)
        {
            if (arg.Parsed != null) return arg.Parsed.Hint;
            return arg.Type + (arg.Optional ? "?" : "");
        }
    }
}
