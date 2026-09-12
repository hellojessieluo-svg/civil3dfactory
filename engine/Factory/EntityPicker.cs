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
    /// 参数对话框的「图中拾取」：点一下按钮，对话框让位，你在图上点/选，选完对话框弹回并回填。
    ///
    /// 参数框是 Application.ShowModalDialog 弹出来的模态窗，模态期间 CAD 主窗不收输入，
    /// 所以拾取前必须 Hide() 并把焦点交回主窗，拾取后再 Show() ——
    /// 这是 AutoCAD 里从模态对话框拾取的标准做法，也是 Civil 3D 自己那些对话框的手感。
    ///
    /// 全部方法的返回值都是可以直接塞进工单的 JSON：
    /// 用户按 ESC / 直接回车放弃时返回 null，此时调用方保持原值不动。
    /// </summary>
    public static class EntityPicker
    {
        /// <summary>坐标留 6 位小数，免得 JSON 里出现 1e-15 这种浮点噪音。</summary>
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

        // ───────────────────────── 点 ─────────────────────────

        public static JsonNode PickPoint(Form owner, Document doc, string label)
        {
            return Interact(owner, delegate
            {
                Editor ed = doc.Editor;
                var opts = new PromptPointOptions("\n[C3DF] 指定" + Label(label, "点") + "（ESC 放弃）: ");
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
                        ? "\n[C3DF] 指定" + Label(label, "起点") + "（ESC 放弃）: "
                        : "\n[C3DF] 指定下一点 [已取 " + points.Count + " 点]（回车结束）: ";
                    var opts = new PromptPointOptions(prompt);
                    opts.AllowNone = true;
                    if (points.Count > 0)
                    {
                        opts.UseBasePoint = true;
                        opts.BasePoint = points[points.Count - 1];
                    }

                    PromptPointResult res = ed.GetPoint(opts);
                    if (res.Status == PromptStatus.None) break;          // 回车 = 收工
                    if (res.Status != PromptStatus.OK) return null;      // ESC = 整个放弃
                    points.Add(res.Value);
                }

                if (points.Count == 0) return null;
                var arr = new JsonArray();
                foreach (Point3d p in points) arr.Add(Xy(p));
                ed.WriteMessage("\n[C3DF] 已取 " + points.Count + " 个点。\n");
                return arr;
            });
        }

        public static JsonNode PickDistance(Form owner, Document doc, string label)
        {
            return Interact(owner, delegate
            {
                Editor ed = doc.Editor;
                var opts = new PromptDistanceOptions("\n[C3DF] 指定" + Label(label, "距离") + "（ESC 放弃）: ");
                opts.AllowNone = true;
                opts.AllowNegative = false;
                PromptDoubleResult res = ed.GetDistance(opts);
                if (res.Status != PromptStatus.OK) return null;
                return JsonValue.Create(Math.Round(res.Value, Decimals));
            });
        }

        // ───────────────────────── 对象 ─────────────────────────

        public static JsonNode PickEntity(Form owner, Document doc, string filter, string label)
        {
            return Interact(owner, delegate
            {
                Editor ed = doc.Editor;
                var opts = new PromptEntityOptions("\n[C3DF] 选择" + Label(label, FilterName(filter)) + "（ESC 放弃）: ");
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
                opts.MessageForAdding = "\n[C3DF] 选择" + Label(label, FilterName(filter)) + "（回车结束，ESC 放弃）";
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
                ed.WriteMessage("\n[C3DF] 已选 " + arr.Count + " 个对象。\n");
                return arr;
            });
        }

        // ───────────────────────── 让位 / 复位 ─────────────────────────

        /// <summary>藏窗 → 把焦点交回 CAD 主窗 → 拾取 → 弹回。异常和取消都保证窗能回来。</summary>
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
                MessageBox.Show("拾取失败：" + ex.Message, "Civil3DFactory",
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

        // ───────────────────────── 过滤与转换 ─────────────────────────

        /// <summary>契约里的过滤名 → 可选的 RXClass 白名单。写不出来的名字就不限制，别把人挡在外面。</summary>
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
            opts.SetRejectMessage("\n[C3DF] 需要选择 " + FilterName(filter) + "。");
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
            if (string.IsNullOrWhiteSpace(filter)) return "对象";
            switch (filter.Trim().ToLowerInvariant())
            {
                case "polyline": return "多段线";
                case "line": return "直线";
                case "curve": return "曲线";
                case "text": return "文字";
                case "blockref": return "块参照";
                case "alignment": return "路线";
            }
            return filter;
        }

        /// <summary>句柄用十六进制串，与工单里既有的 handle 参数写法一致。</summary>
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
