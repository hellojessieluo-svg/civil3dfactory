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
        /// 批量放通用标签（General Note Label）：给一串点位，每点按指定样式放一个标签。
        /// 坐标标注的自动化就是它——样式里写 X=&lt;[Northing]&gt; / Y=&lt;[Easting]&gt;，
        /// 标签一落在点上数值自动取，拖动也跟着变，不用人手抄坐标。
        ///
        /// 为什么有这个零件（2026-09-02 五期模板坐标标签优化）：
        /// 标签样式的锚点位置 / 拖曳显示方式在 .NET API 里没暴露，只能靠真放一个标签渲染出来看；
        /// 顺手把「按点位批量标坐标」做成正式入口。
        ///
        /// 每个点可带 dx/dy（图形单位）：给了就把标签拖成拖曳状态（带引线），用来看/出拖曳态的排版。
        /// 内存改动不落盘，要成果显式 save_dwg。
        /// </summary>
        static JsonNode RunNodeAddNoteLabel(JsonObject a, Document doc)
        {
            string styleName = GetString(a, "style", null);
            if (string.IsNullOrEmpty(styleName))
                throw new InvalidOperationException("style（通用标签样式名）必填。");
            var pts = a["points"] as JsonArray;
            if (pts == null || pts.Count == 0)
                throw new InvalidOperationException("points 必填：[{x,y,dx?,dy?}, …]，dx/dy 为拖曳偏移（图形单位）。");
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
                    throw new InvalidOperationException("找不到通用标签样式 '" + styleName + "'（大小写敏感，list_styles 里看 GeneralNoteLabelStyles）。");

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
                        throw new InvalidOperationException("points 每项必须有 x, y。");
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
                throw new InvalidOperationException("一个标签都没放出来（points 里没有合法项）。");
            return new JsonObject
            {
                ["style"] = styleName,
                ["placed"] = placed.Count,
                ["labels"] = placed
            };
        }
    }
}
