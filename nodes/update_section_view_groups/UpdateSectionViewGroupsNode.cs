using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivSampleLineGroup = Autodesk.Civil.DatabaseServices.SampleLineGroup;
using CivSectionViewGroup = Autodesk.Civil.DatabaseServices.SectionViewGroup;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeUpdateSectionViewGroups(JsonObject a, Document doc)
        {
            var only = new HashSet<string>();
            var arr = a["alignments"] as JsonArray;
            if (arr != null) foreach (JsonNode n in arr) only.Add(n.ToString());
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var rep = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId aid in civ.GetAlignmentIds())
                {
                    var al = tr.GetObject(aid, OpenMode.ForRead) as CivAlignment;
                    if (al == null) continue;
                    if (only.Count > 0 && !only.Contains(al.Name)) continue;
                    foreach (ObjectId gid in al.GetSampleLineGroupIds())
                    {
                        var slg = (CivSampleLineGroup)tr.GetObject(gid, OpenMode.ForWrite);
                        foreach (CivSectionViewGroup g in slg.SectionViewGroups)
                        {
                            var before = Fingerprint(tr, g);
                            string err = null;
                            try { CivilCompat.UpdateSectionGroupLayout(g); } catch (System.Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
                            var after = Fingerprint(tr, g);
                            rep.Add(new JsonObject
                            {
                                ["alignment"] = al.Name, ["group"] = slg.Name, ["section_view_group"] = g.Name,
                                ["views"] = before["views"].GetValue<int>(),
                                ["before"] = before, ["after"] = after, ["changed"] = before.ToJsonString() != after.ToJsonString(),
                                ["error"] = err
                            });
                        }
                    }
                }
                tr.Commit();
            }
            return new JsonObject { ["groups"] = rep };
        }

        static JsonObject Fingerprint(Transaction tr, CivSectionViewGroup g)
        {
            var xs = new SortedSet<double>(); var ys = new SortedSet<double>(); int n = 0;
            foreach (ObjectId vid in g.GetSectionViewIds())
            {
                var sv = tr.GetObject(vid, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.SectionView;
                if (sv == null) continue;
                n++;
                xs.Add(Math.Round(sv.Location.X, 1)); ys.Add(Math.Round(sv.Location.Y, 1));
            }
            var xl = new List<double>(xs); var yl = new List<double>(ys);
            return new JsonObject
            {
                ["views"] = n, ["columns"] = xs.Count, ["rows"] = ys.Count,
                ["col_pitch"] = xl.Count > 1 ? Math.Round(xl[1] - xl[0], 1) : 0,
                ["row_pitch"] = yl.Count > 1 ? Math.Round(yl[1] - yl[0], 1) : 0
            };
        }
    }
}
