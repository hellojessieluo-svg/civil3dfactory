using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Civil3DFactory.Geometry;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// Node refresh_sample_lines: keep the sample line group, regenerate its sample lines along the new geometry
    /// (companion to replacing the centerline; unlike create_sample_lines it neither deletes the group nor changes the section source settings).
    /// The algorithm core lives in SampleLineRefreshCore.cs in this folder (shared with
    /// C3DF-RefreshSampleLines/CYX in products\waterbox).
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeRefreshSampleLines(JsonObject a, Document doc)
            => RefreshSampleLines(a, doc);

        public static JsonNode RefreshSampleLines(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            Database db = doc.Database;
            CivDoc civ = Civ(db);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = FindAlignment(tr, civ, alName);
                if (al == null)
                    throw new InvalidOperationException("Alignment '" + alName + "' not found.");

                double estInterval, estSwath;
                SampleLineRefreshCore.Estimate(al, tr, out estInterval, out estSwath);
                double interval = GetDouble(a, "interval", estInterval);
                double swath = GetDouble(a, "swath", estSwath);

                List<SampleLineRefreshCore.GroupRefresh> groups =
                    SampleLineRefreshCore.Refresh(al, tr, interval, swath);

                int created = 0;
                var arr = new JsonArray();
                foreach (SampleLineRefreshCore.GroupRefresh g in groups)
                {
                    created += g.NewLines;
                    arr.Add(new JsonObject
                    {
                        ["group"] = g.GroupName,
                        ["old_lines"] = g.OldLines,
                        ["new_lines"] = g.NewLines
                    });
                }
                if (created == 0)
                    throw new InvalidOperationException("No sample line was generated (interval longer than the alignment?).");

                tr.Commit();
                return new JsonObject
                {
                    ["alignment"] = alName,
                    ["interval"] = interval,
                    ["swath"] = swath,
                    ["groups"] = arr,
                    ["sample_lines"] = created
                };
            }
        }
    }
}
