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
    /// Parameter dialog generated on the fly from the node.json contract:
    /// inputs get dropdowns (names listed from the current drawing), parameters get an editor matching their type.
    /// Previously entered values are stored per node under %LOCALAPPDATA%\Civil3DFactory\ui\ and restored next time.
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
            /// <summary>Semantic parse of the contract type; decides which control is built.</summary>
            public NodeArgType Type;
            public Control Editor;
            /// <summary>How the value is read: bool | number | integer | json | text | path.</summary>
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

        // ───────────────────────── UI ─────────────────────────

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
            if (_node.Effect == "persist") desc = "[Writes files] " + desc;
            else if (_node.Effect == "modify") desc = "[Modifies the current drawing] " + desc;

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
            // AutoSize + percentage columns grow with content and push the third column out of view, so cap at the container width
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
                    Text = "This node has no parameters; just run it.",
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
                // flow: entries are only pipeline data dependencies, not read at run time; do not show them
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

            // Convention: when a node has both x and y numeric parameters, the x row gets a pick button that fills both at once.
            // Insertion points almost always come as such a pair; a dedicated contract field is not worth it.
            if (_hasXyPair && string.Equals(r.Def.Key, "x", StringComparison.OrdinalIgnoreCase))
                return WithSideButton(r, "Pick in drawing", delegate { PickXy(); });

            if (r.Kind == "path")
                return WithSideButton(r, "Browse...", delegate { Browse(r); });

            r.Editor.Dock = DockStyle.Fill;
            return r.Editor;
        }

        /// <summary>Attaches an action button (browse / pick) to the right of the editor; for multi-line editors it sits at the top right.</summary>
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
                // multi-line box: the button does not stretch, it hugs the top-right corner
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
                case "points": return "Pick points";
                case "entities": return "Select objects";
                case "distance": return "Measure distance";
            }
            return "Pick in drawing";
        }

        // ───────────────────────── Pick in drawing ─────────────────────────

        void PickInto(ArgRow r)
        {
            JsonNode value = EntityPicker.Pick(this, _doc, r.Type, r.Def.Key);
            if (value == null) return;
            SetEditorText(r, value is JsonValue ? value.ToString() : value.ToJsonString());
        }

        /// <summary>Picks one point and fills both the x and y rows.</summary>
        void PickXy()
        {
            JsonNode value = EntityPicker.PickPoint(this, _doc, "insertion point");
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

        /// <summary>Whether the node has a paired x / y numeric parameter.</summary>
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
                        // the value set is fixed by the contract: read-only dropdown, nothing outside the contract can be chosen
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

                        // multi-select (e.g. "which layers are centrelines") gets a checked list; the ticks become the array directly
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
                            // if no candidates can be listed from the drawing (e.g. layers not created yet), fall back to hand-edited JSON; never block the user
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

                        // candidates are listed live from the current drawing; typing is still allowed, a failed listing must not block execution
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
                        // the shape of the picked value decides how it is stored: handles are strings, distances numbers, points/multi-selects JSON
                        if (t.PickKind == "entity") r.Kind = "text";
                        else if (t.PickKind == "distance") r.Kind = "number";
                        else r.Kind = "json";
                        return r;
                    }

                case "bool":
                    r.Editor = new CheckBox { Text = "Enabled", Height = 24, AutoSize = false };
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
                    // array / structured parameters: edit JSON directly instead of building a control per structure
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
            // enums with a fixed value in the contract (e.g. selection:"all") are filled in as the default
            if (!string.Equals(type, "string", StringComparison.OrdinalIgnoreCase)) r.LiteralDefault = type;
            return r;
        }

        Control BuildFooter()
        {
            var panel = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(12, 8, 12, 8) };

            var run = new Button { Text = "Run", Width = 90, Height = 30, Dock = DockStyle.Right };
            var cancel = new Button { Text = "Cancel", Width = 90, Height = 30, Dock = DockStyle.Right };
            var spacer = new Panel { Width = 8, Dock = DockStyle.Right };
            var copy = new Button { Text = "Copy work-order JSON", Width = 130, Height = 30, Dock = DockStyle.Left };
            var reset = new Button { Text = "Clear", Width = 70, Height = 30, Dock = DockStyle.Left };

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

        // ───────────────────────── Reading values ─────────────────────────

        void OnRun()
        {
            JsonObject args;
            List<string> missing;
            if (!TryCollect(out args, out missing)) return;

            if (missing.Count > 0)
            {
                DialogResult ans = MessageBox.Show(
                    "The following required parameters are empty:\r\n  " + string.Join("\r\n  ", missing.ToArray())
                    + "\r\n\r\nRun anyway? (The node may have its own internal defaults.)",
                    "Incomplete parameters", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
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
                                Complain(r, "must be a number");
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
                                Complain(r, "must be an integer");
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
                                Complain(r, "is not valid JSON: " + ex.Message);
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

        /// <summary>Numeric parameters with {min..max} in the contract are caught here, instead of failing halfway through the node.</summary>
        bool InRange(ArgRow r, double v)
        {
            NodeArgType t = r.Type;
            if (t == null) return true;
            if (t.Min.HasValue && v < t.Min.Value)
            {
                Complain(r, "must not be less than " + t.Min.Value.ToString("0.###", CultureInfo.InvariantCulture));
                return false;
            }
            if (t.Max.HasValue && v > t.Max.Value)
            {
                Complain(r, "must not be greater than " + t.Max.Value.ToString("0.###", CultureInfo.InvariantCulture));
                return false;
            }
            return true;
        }

        void Complain(ArgRow r, string message)
        {
            MessageBox.Show("Parameter " + r.Def.Key + " " + message, "Invalid parameter",
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
                // setting Text on a read-only dropdown has no effect; match by item, and leave it unselected if nothing matches
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
                MessageBox.Show("Copied; paste it straight into the ops array of a work order.", "Copy work-order JSON",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show("Copy failed: " + ex.Message, "Copy work-order JSON",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void ResetValues()
        {
            foreach (ArgRow r in _rows)
            {
                // an optional parameter's =value only shows the implementation default to the user (in the type hint) and is not pre-filled:
                // leaving it blank means "let the node use its own default"; pre-filling would hard-code the value into the work order.
                // a required parameter's =value is still pre-filled; that is the starting value the contract asserts.
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

        // ───────────────────────── Remembering last values ─────────────────────────

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
        /// Whether a stored value is dirty data left by an old plugin build.
        ///
        /// When the contract gains a new type string (e.g. list:dwg.layer), an old plugin that does not know it takes the fallback branch
        /// and fills the whole type string into the box as a "literal default"; one click on Run saves it to the history file,
        /// and even after the plugin is updated and the dropdown works again, that dirty history keeps overriding it.
        /// Any value read back that equals the type string itself, or carries a new-syntax prefix, is discarded.
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

        /// <summary>With no history, seed from the sample parameters in the node's own test.json.</summary>
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

        /// <summary>Type hint shown on the right of the UI.</summary>
        static string TypeHint(NodeArgDef arg)
        {
            if (arg.Parsed != null) return arg.Parsed.Hint;
            return arg.Type + (arg.Optional ? "?" : "");
        }
    }
}
