using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivQtoMaterial = Autodesk.Civil.DatabaseServices.QTOMaterial;
using CivQtoMaterialList = Autodesk.Civil.DatabaseServices.QTOMaterialList;
using CivQtoTable = Autodesk.Civil.DatabaseServices.SectionViewQuantityTakeoffTable;
using CivSampleLineGroup = Autodesk.Civil.DatabaseServices.SampleLineGroup;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// 节点 rebind_volume_tables：把绑定失效的体积表原位重绑到所属路线的现材质列表。
    ///
    /// 病根：compute_quantities 重算会清掉旧材质列表重建（新 Guid），图上已插的
    /// 体积表还攥着旧 Guid——数据是好的、表格全显 0。表格位置不动、样式不动，
    /// 只换 MaterialListGuid 并重选材质。
    /// 归属判定：死表按包围盒中心找最近的「带材质列表的路线」，结果里报距离，
    /// 两条路线都近得可疑时人工核对（distance 字段就是干这个的）。
    ///
    /// ⚠ 能力边界（2026-08-16 项目C实图查实）：本节点只治得了
    /// SectionViewQuantityTakeoffTable（断面图 QTO 表）。GUI 用 AddTotalVolumeTable
    /// 插的「总体积表」在托管 API 里被包装成**基类 Table，零成员**——绑定读不到、
    /// 改不了、内容抽不出（也不派生自 ACAD Table）。那种表只能在界面里删掉重插：
    /// Analyze → Volumes and Materials → Total Volume Table → 选新材质列表。
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeRebindVolumeTables(JsonObject a, Document doc)
            => RebindVolumeTables(a, doc);

        public static JsonNode RebindVolumeTables(JsonObject a, Document doc)
        {
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var rebound = new JsonArray();
            int healthy = 0, dead = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // 活材质列表登记：guid → (路线名, 列表, 路线包围盒中心)
                var live = new Dictionary<Guid, string>();
                var byAlign = new List<(string Name, Point3d Center, CivQtoMaterialList Ml)>();
                foreach (ObjectId alId in civ.GetAlignmentIds())
                {
                    var al = tr.GetObject(alId, OpenMode.ForRead) as CivAlignment;
                    if (al == null) continue;
                    foreach (ObjectId gid in al.GetSampleLineGroupIds())
                    {
                        var g = (CivSampleLineGroup)tr.GetObject(gid, OpenMode.ForRead);
                        foreach (CivQtoMaterialList ml in g.MaterialLists)
                        {
                            live[ml.Guid] = al.Name;
                            Point3d c;
                            try
                            {
                                Extents3d e = al.GeometricExtents;
                                c = new Point3d((e.MinPoint.X + e.MaxPoint.X) / 2,
                                                (e.MinPoint.Y + e.MaxPoint.Y) / 2, 0);
                            }
                            catch { c = Point3d.Origin; }
                            byAlign.Add((al.Name, c, ml));
                            break;   // 一条路线取第一张列表
                        }
                    }
                }
                if (byAlign.Count == 0)
                    throw new InvalidOperationException("图里没有任何材质列表，先跑 compute_quantities。");

                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    CivQtoTable t;
                    try { t = tr.GetObject(id, OpenMode.ForRead) as CivQtoTable; }
                    catch { continue; }
                    if (t == null) continue;

                    Guid cur = t.MaterialListGuid;
                    if (live.ContainsKey(cur)) { healthy++; continue; }
                    dead++;

                    // 死表：按包围盒中心找最近的带列表路线
                    Point3d tc;
                    try
                    {
                        Extents3d e = t.GeometricExtents;
                        tc = new Point3d((e.MinPoint.X + e.MaxPoint.X) / 2,
                                         (e.MinPoint.Y + e.MaxPoint.Y) / 2, 0);
                    }
                    catch { tc = Point3d.Origin; }

                    int best = 0;
                    double bestD = double.MaxValue;
                    for (int i = 0; i < byAlign.Count; i++)
                    {
                        double dd = tc.DistanceTo(byAlign[i].Center);
                        if (dd < bestD) { bestD = dd; best = i; }
                    }
                    var target = byAlign[best];

                    t.UpgradeOpen();
                    t.MaterialListGuid = target.Ml.Guid;
                    // 旧选中材质全是死 Guid，清掉换成新列表的全部材质
                    int matAdded = 0;
                    try
                    {
                        foreach (Guid mg in t.GetSelectedMaterials())
                            try { t.RemoveSelectedMaterial(mg); } catch { }
                        foreach (CivQtoMaterial m in target.Ml)
                            if (t.AddSelectedMaterial(m.Guid)) matAdded++;
                    }
                    catch { }

                    rebound.Add(new JsonObject
                    {
                        ["table_handle"] = t.Handle.ToString(),
                        ["old_guid"] = cur.ToString(),
                        ["alignment"] = target.Name,
                        ["new_guid"] = target.Ml.Guid.ToString(),
                        ["materials_selected"] = matAdded,
                        ["distance"] = Round(bestD, 1)
                    });
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["tables_healthy"] = healthy,
                ["tables_rebound"] = dead,
                ["rebound"] = rebound
            };
        }
    }
}
