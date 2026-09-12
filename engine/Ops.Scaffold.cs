using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DFactory
{
    /// <summary>
    /// 通用绘图脚手架（2026-08-20 重复合并收口）：纸张尺寸、XData 可重跑标记。
    /// 全仓唯一真源——新节点直接调这里，别再复制 switch/RegApp 样板。
    /// </summary>
    public static partial class Ops
    {
        /// <summary>A0–A4 纸张尺寸（mm，横放）。改图幅口径只改这里。</summary>
        internal static void PaperSizeMm(string paper, out double wMm, out double hMm)
        {
            switch ((paper ?? "A3").Trim().ToUpperInvariant())
            {
                case "A0": wMm = 1189; hMm = 841; break;
                case "A1": wMm = 841; hMm = 594; break;
                case "A2": wMm = 594; hMm = 420; break;
                case "A3": wMm = 420; hMm = 297; break;
                case "A4": wMm = 297; hMm = 210; break;
                default: throw new InvalidOperationException("不认识的纸张 '" + paper + "'，可用 A0/A1/A2/A3/A4。");
            }
        }

        /// <summary>注册 XData 应用名（幂等）。</summary>
        internal static void EnsureRegApp(Transaction tr, Database db, string appName)
        {
            var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (rat.Has(appName)) return;
            rat.UpgradeOpen();
            var rec = new RegAppTableRecord { Name = appName };
            rat.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }

        /// <summary>删模型空间里带指定 XData 应用名标记的实体（可重跑清场），返回删除数。</summary>
        internal static int EraseTaggedEntities(Transaction tr, Database db, string appName)
        {
            var kill = new List<ObjectId>();
            foreach (ObjectId id in ModelSpace(db, tr))
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;
                ResultBuffer rb = null;
                try { rb = ent.GetXDataForApplication(appName); }
                catch (System.Exception) { }
                if (rb == null) continue;
                rb.Dispose();
                kill.Add(id);
            }
            foreach (ObjectId id in kill)
                ((Entity)tr.GetObject(id, OpenMode.ForWrite)).Erase();
            return kill.Count;
        }
    }
}
