using System;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        /// <summary>从外部 DWG 把指定名称的曲面 WblockClone 进当前图纸；已存在同名曲面则跳过。</summary>
        static JsonNode RunNodeImportSurface(JsonObject args, Document doc)
        {
            string path = Need(args, "dwg");
            string name = Need(args, "name");
            if (!File.Exists(path))
                throw new InvalidOperationException("找不到曲面来源图纸: " + path);

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (!FindSurfaceId(tr, civ, name).IsNull)
                {
                    tr.Commit();
                    return new JsonObject
                    {
                        ["surface"] = name,
                        ["imported"] = false,
                        ["reason"] = "当前图纸已存在同名曲面，直接使用"
                    };
                }
                tr.Commit();
            }

            using (var srcDb = new Database(false, true))
            {
                srcDb.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
                ObjectId srcId = ObjectId.Null;
                using (Transaction str = srcDb.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)str.GetObject(srcDb.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)str.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        CivTinSurface s;
                        try { s = str.GetObject(id, OpenMode.ForRead) as CivTinSurface; }
                        catch { continue; }
                        if (s != null && s.Name == name) { srcId = id; break; }
                    }
                    str.Commit();
                }
                if (srcId.IsNull)
                    throw new InvalidOperationException(
                        "来源图纸里没有名为 '" + name + "' 的 TIN 曲面: " + path);

                var ids = new ObjectIdCollection { srcId };
                var map = new IdMapping();
                ObjectId targetMs;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    targetMs = bt[BlockTableRecord.ModelSpace];
                    tr.Commit();
                }
                srcDb.WblockCloneObjects(ids, targetMs, map, DuplicateRecordCloning.Ignore, false);
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId sid = FindSurfaceId(tr, civ, name);
                if (sid.IsNull)
                    throw new InvalidOperationException("WblockClone 后仍找不到曲面 '" + name + "'，导入失败。");
                var surf = (CivSurface)tr.GetObject(sid, OpenMode.ForRead);
                var props = surf.GetGeneralProperties();
                var res = new JsonObject
                {
                    ["surface"] = name,
                    ["imported"] = true,
                    ["from"] = path,
                    ["elev_min"] = Math.Round(props.MinimumElevation, 3),
                    ["elev_max"] = Math.Round(props.MaximumElevation, 3)
                };
                tr.Commit();
                return res;
            }
        }
    }
}
