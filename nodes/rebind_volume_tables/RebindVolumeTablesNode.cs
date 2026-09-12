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
    /// Node rebind_volume_tables: rebind orphaned volume tables in place to the current material list of their alignment.
    ///
    /// Root cause: recomputing with compute_quantities clears and rebuilds the material list (new Guid); volume tables
    /// already placed in the drawing still hold the old Guid -- the data is fine but the table shows all zeros. Position and style are untouched;
    /// only MaterialListGuid is swapped and the materials re-selected.
    /// Ownership: a dead table is matched to the nearest "alignment with a material list" by bounding-box center; the distance is reported,
    /// so check manually when two alignments are suspiciously close (that is what the distance field is for).
    ///
    /// WARNING - scope limit (verified on project C drawings, 2026-08-16): this node can only fix
    /// SectionViewQuantityTakeoffTable (section view QTO tables). "Total volume tables" inserted from the GUI via AddTotalVolumeTable
    /// are wrapped in the managed API as the **base class Table with zero members** -- the binding cannot be read,
    /// changed, or extracted (and it does not derive from the ACAD Table either). Those tables can only be deleted and re-inserted in the GUI:
    /// Analyze -> Volumes and Materials -> Total Volume Table -> pick the new material list.
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
                // Registry of live material lists: guid -> (alignment name, list, alignment bounding-box center)
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
                            break;   // one list per alignment: the first
                        }
                    }
                }
                if (byAlign.Count == 0)
                    throw new InvalidOperationException("The drawing has no material list; run compute_quantities first.");

                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    CivQtoTable t;
                    try { t = tr.GetObject(id, OpenMode.ForRead) as CivQtoTable; }
                    catch { continue; }
                    if (t == null) continue;

                    Guid cur = t.MaterialListGuid;
                    if (live.ContainsKey(cur)) { healthy++; continue; }
                    dead++;

                    // Dead table: find the nearest alignment with a list by bounding-box center
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
                    // The old selected materials are all dead Guids; clear them and select every material of the new list
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
