using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        // 自画数据表（纯 CAD 线+文字），模型空间或指定布局都能画。
        // 用途：工程量表上图。OLE 表无头贴不进去也改不了，自画表 accore 能打印、导纯 CAD 不掉、改数只需重跑。
        // 实体带 XData(C3DF_TBL:<name>) 认亲，同名重跑先清旧表；也认专用图层上的实体。
        const string TblRegApp = "C3DF_TBL";

        static JsonNode RunNodeDrawTable(JsonObject a, Document doc)
        {
            var rows = a["rows"] as JsonArray;
            if (rows == null || rows.Count == 0)
                throw new InvalidOperationException("需要 rows:[[单元格,...],...]（每行一个数组，空字符串=空格）");
            string name = GetString(a, "name", "表");
            string space = GetString(a, "space", "Model");
            double x0 = GetDouble(a, "x", double.NaN), yTop = GetDouble(a, "y", double.NaN);
            if (double.IsNaN(x0) || double.IsNaN(yTop))
                throw new InvalidOperationException("需要 x,y（表左上角坐标；布局里是图纸 mm，模型空间是图形单位）");
            double rowH = GetDouble(a, "row_height", 5.0);
            double textH = GetDouble(a, "text_height", 2.5);
            double totalW = GetDouble(a, "width", 0.0);
            string layerName = GetString(a, "layer", "C3DF-TABLE");
            short color = (short)GetDouble(a, "color", 7);
            string textStyleName = GetString(a, "text_style", null);
            bool clear = GetBool(a, "clear", true);
            int headerRows = (int)GetDouble(a, "header_rows", 1);
            double headerH = GetDouble(a, "header_row_height", rowH);
            bool mask = GetBool(a, "mask", false);                 // 表底下垫 Wipeout，遮住视口里的模型内容（OLE 的不透明底）
            double lwMm = GetDouble(a, "lineweight", 0.25);          // 图层线宽 mm；0 = 不设

            // 列数 = 最长行
            int nCols = 0;
            foreach (JsonNode r in rows) nCols = Math.Max(nCols, ((JsonArray)r).Count);
            if (nCols == 0) throw new InvalidOperationException("rows 里没有单元格");

            // 列宽：col_widths 显式给；否则按各列最长文本估宽（中文按 1.0 字高、ASCII 0.6 字高 + 2 字高留白），再按 width 等比缩放
            var colW = new double[nCols];
            if (a["col_widths"] is JsonArray cw && cw.Count == nCols)
            {
                for (int c = 0; c < nCols; c++) colW[c] = cw[c].GetValue<double>();
            }
            else
            {
                for (int c = 0; c < nCols; c++) colW[c] = 3 * textH;
                foreach (JsonNode r in rows)
                {
                    var ra = (JsonArray)r;
                    for (int c = 0; c < ra.Count; c++)
                    {
                        string s = CellText(ra[c]);
                        double est = EstTextWidth(s, textH) + 2 * textH;
                        if (est > colW[c]) colW[c] = est;
                    }
                }
            }
            double sumW = 0; foreach (double w in colW) sumW += w;
            if (totalW > 0 && sumW > 0)
            {
                double f = totalW / sumW;
                for (int c = 0; c < nCols; c++) colW[c] *= f;
                sumW = totalW;
            }
            double totalH = 0;
            for (int r = 0; r < rows.Count; r++) totalH += r < headerRows ? headerH : rowH;

            Database db = doc.Database;
            int cleared = 0, made = 0;
            string drawOrderNote = null;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                // 目标空间
                BlockTableRecord btr;
                if (string.Equals(space, "Model", StringComparison.OrdinalIgnoreCase))
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                }
                else
                {
                    var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                    if (!dict.Contains(space))
                        throw new InvalidOperationException("图里没有布局 '" + space + "'。");
                    var layout = (Layout)tr.GetObject(dict.GetAt(space), OpenMode.ForRead);
                    btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
                }

                // 图层 + RegApp
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                ObjectId layerId;
                if (lt.Has(layerName)) layerId = lt[layerName];
                else
                {
                    lt.UpgradeOpen();
                    var ltr = new LayerTableRecord { Name = layerName, Color = Color.FromColorIndex(ColorMethod.ByAci, color) };
                    layerId = lt.Add(ltr);
                    tr.AddNewlyCreatedDBObject(ltr, true);
                    lt.DowngradeOpen();
                }
                if (lwMm > 0)
                {
                    int lwv = (int)Math.Round(lwMm * 100);
                    if (Enum.IsDefined(typeof(LineWeight), lwv))
                    {
                        var ltrW = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForWrite);
                        ltrW.LineWeight = (LineWeight)lwv;
                    }
                }
                var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
                if (!rat.Has(TblRegApp))
                {
                    rat.UpgradeOpen();
                    var ra0 = new RegAppTableRecord { Name = TblRegApp };
                    rat.Add(ra0);
                    tr.AddNewlyCreatedDBObject(ra0, true);
                    rat.DowngradeOpen();
                }

                // 清旧表：同空间里带 C3DF_TBL XData 且同名的实体
                if (clear)
                {
                    foreach (ObjectId id in btr)
                    {
                        Entity e0;
                        try { e0 = tr.GetObject(id, OpenMode.ForRead) as Entity; } catch { continue; }
                        if (e0 == null) continue;
                        var xd = e0.GetXDataForApplication(TblRegApp);
                        if (xd == null) continue;
                        string tag = null;
                        foreach (TypedValue tv in xd.AsArray())
                            if (tv.TypeCode == (int)DxfCode.ExtendedDataAsciiString) { tag = tv.Value as string; break; }
                        if (!string.Equals(tag, name, StringComparison.Ordinal)) continue;
                        e0.UpgradeOpen();
                        e0.Erase();
                        cleared++;
                    }
                }

                // 文字样式：参数指定 > -黑体 > 图默认
                ObjectId styleId = ObjectId.Null;
                var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                if (!string.IsNullOrEmpty(textStyleName))
                {
                    if (!tst.Has(textStyleName))
                        throw new InvalidOperationException("图里没有文字样式 '" + textStyleName + "'。");
                    styleId = tst[textStyleName];
                }

                var xdata = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, TblRegApp),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, name));
                var newIds = new ObjectIdCollection();
                Action<Entity> put = e =>
                {
                    e.SetDatabaseDefaults();
                    e.LayerId = layerId;
                    btr.AppendEntity(e);
                    tr.AddNewlyCreatedDBObject(e, true);
                    e.XData = xdata;
                    newIds.Add(e.ObjectId);
                    made++;
                };

                // 白底遮罩（先画，压在最底）：Wipeout 按表外框，WIPEOUTFRAME 归 0 不出框线
                if (mask)
                {
                    var wo = new Wipeout();
                    var pts = new Point2dCollection
                    {
                        new Point2d(x0, yTop), new Point2d(x0 + sumW, yTop),
                        new Point2d(x0 + sumW, yTop - totalH), new Point2d(x0, yTop - totalH), new Point2d(x0, yTop)
                    };
                    wo.SetDatabaseDefaults();
                    wo.SetFrom(pts, Vector3d.ZAxis);
                    put(wo);
                    try { Application.SetSystemVariable("WIPEOUTFRAME", 0); } catch { }
                }

                // 横线
                double yb = yTop;
                var rowYs = new List<double> { yTop };
                for (int r = 0; r < rows.Count; r++) { yb -= r < headerRows ? headerH : rowH; rowYs.Add(yb); }
                foreach (double y in rowYs)
                    put(new Line(new Point3d(x0, y, 0), new Point3d(x0 + sumW, y, 0)));
                // 竖线
                double xc = x0;
                var colXs = new List<double> { x0 };
                for (int c = 0; c < nCols; c++) { xc += colW[c]; colXs.Add(xc); }
                foreach (double x in colXs)
                    put(new Line(new Point3d(x, yTop, 0), new Point3d(x, yTop - totalH, 0)));
                // 文字（居中）
                for (int r = 0; r < rows.Count; r++)
                {
                    var ra = (JsonArray)rows[r];
                    double cy = (rowYs[r] + rowYs[r + 1]) / 2.0;
                    for (int c = 0; c < ra.Count; c++)
                    {
                        string s = CellText(ra[c]);
                        if (string.IsNullOrWhiteSpace(s)) continue;
                        double cx = (colXs[c] + colXs[c + 1]) / 2.0;
                        var t = new DBText { TextString = s, Height = textH, Position = new Point3d(cx, cy, 0) };
                        if (!styleId.IsNull) t.TextStyleId = styleId;
                        t.HorizontalMode = TextHorizontalMode.TextCenter;
                        t.VerticalMode = TextVerticalMode.TextVerticalMid;
                        t.AlignmentPoint = new Point3d(cx, cy, 0);
                        // 文字比格子宽就压宽度因子，不溢出
                        double avail = colW[c] - 0.6 * textH;
                        double est = EstTextWidth(s, textH);
                        if (est > avail && est > 0) t.WidthFactor = Math.Max(0.5, avail / est);
                        put(t);
                    }
                }
                // 置顶：布局里视口常被人 draworder 到最前，新画的表会被视口里的模型内容压住
                try
                {
                    var dot = (DrawOrderTable)tr.GetObject(btr.DrawOrderTableId, OpenMode.ForWrite);
                    dot.MoveToTop(newIds);
                }
                catch (System.Exception ex) { drawOrderNote = "置顶失败：" + ex.Message; }
                tr.Commit();
            }

            return new JsonObject
            {
                ["name"] = name,
                ["space"] = space,
                ["rows"] = rows.Count,
                ["cols"] = nCols,
                ["width"] = Math.Round(sumW, 3),
                ["height"] = Math.Round(totalH, 3),
                ["bbox"] = new JsonArray(Math.Round(x0, 3), Math.Round(yTop - totalH, 3), Math.Round(x0 + sumW, 3), Math.Round(yTop, 3)),
                ["col_widths"] = ToJsonArray(colW),
                ["entities"] = made,
                ["cleared"] = cleared,
                ["layer"] = layerName,
                ["mask"] = mask,
                ["lineweight_mm"] = lwMm,
                ["note"] = drawOrderNote ?? "已置顶"
            };
        }

        static string CellText(JsonNode n)
        {
            if (n == null) return "";
            if (n is JsonValue v)
            {
                if (v.TryGetValue<string>(out string s)) return s;
                if (v.TryGetValue<double>(out double d)) return d.ToString("0.##");
                if (v.TryGetValue<bool>(out bool b)) return b ? "是" : "否";
            }
            return n.ToJsonString().Trim('"');
        }

        // 粗估文字宽度：CJK 按 1.0 字高，其余按 0.6 字高（中文字宽=字高×宽度因子×0.7 的经验值留了余量）
        static double EstTextWidth(string s, double h)
        {
            double w = 0;
            foreach (char ch in s) w += ch > 0x2E7F ? 1.0 * h : 0.6 * h;
            return w;
        }

        static JsonArray ToJsonArray(double[] arr)
        {
            var ja = new JsonArray();
            foreach (double d in arr) ja.Add(Math.Round(d, 3));
            return ja;
        }
    }
}
