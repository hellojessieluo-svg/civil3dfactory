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
    /// 节点 refresh_sample_lines：采样线组不动、组内采样线沿新几何重生
    /// （换中线后配套；与 create_sample_lines 的区别是不删组、不改采样源设置）。
    /// 算法核心在同目录 SampleLineRefreshCore.cs（与 products\waterbox 的
    /// C3DF-RefreshSampleLines/CYX 同核）。
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
                    throw new InvalidOperationException("找不到路线 '" + alName + "'。");

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
                    throw new InvalidOperationException("没有生成任何采样线（间距大于路线全长？）。");

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
