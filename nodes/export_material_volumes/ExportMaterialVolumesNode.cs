using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivQtoMaterialList = Autodesk.Civil.DatabaseServices.QTOMaterialList;
using CivQtoSectionalResult = Autodesk.Civil.DatabaseServices.QTOSectionalResult;
using CivSampleLineGroup = Autodesk.Civil.DatabaseServices.SampleLineGroup;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// 节点 export_material_volumes：一键导出全部路线的材质体积表到一本 xlsx——
    /// 首个 sheet「汇总」（序号 / 道路中线名称 / 长度(m) / 总体积(m³)，总体积=累计挖方），
    /// 之后一条路线一个 sheet（逐桩号累计/增量挖填方，与 export_quantities 同列）。
    /// 没有材质列表的路线跳过并入 skipped，一条都没有则抛错（不安静成功）。
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeExportMaterialVolumes(JsonObject a, Document doc)
            => ExportMaterialVolumes(a, doc);

        static readonly string[] HeadersVolSummary =
            { "序号", "道路中线名称", "长度(m)", "总体积(m³)" };

        public static JsonNode ExportMaterialVolumes(JsonObject a, Document doc)
        {
            string outPath = GetString(a, "out", null);
            if (string.IsNullOrEmpty(outPath))
                outPath = Path.Combine(ResolveOutDir(a, doc), "材质体积表汇总.xlsx");

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            var sheets = new List<(string Name, string[] Headers, List<object[]> Rows)>();
            var summary = new List<object[]>();
            var perAl = new JsonArray();
            var skipped = new JsonArray();
            double sumLen = 0, sumVol = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // 路线按堤埝编号顺序（X-2 在 X-10 前）
                var als = new List<CivAlignment>();
                foreach (ObjectId alId in civ.GetAlignmentIds())
                {
                    var al = tr.GetObject(alId, OpenMode.ForRead) as CivAlignment;
                    if (al != null) als.Add(al);
                }
                als.Sort((x, y) => CompareDikeName(x.Name, y.Name));

                foreach (CivAlignment al in als)
                {
                    // 找第一个带材质列表的采样线组
                    CivSampleLineGroup slg = null;
                    Guid mlGuid = Guid.Empty;
                    foreach (ObjectId gid in al.GetSampleLineGroupIds())
                    {
                        var g = (CivSampleLineGroup)tr.GetObject(gid, OpenMode.ForRead);
                        foreach (CivQtoMaterialList ml in g.MaterialLists)
                        {
                            slg = g;
                            mlGuid = ml.Guid;
                            break;
                        }
                        if (slg != null) break;
                    }
                    if (slg == null)
                    {
                        skipped.Add(al.Name);
                        continue;
                    }

                    var rows = new List<object[]>();
                    double cut = 0, fill = 0;
                    var result = slg.GetTotalVolumeResultDataForMaterialList(mlGuid);
                    int i = 0;
                    foreach (CivQtoSectionalResult sec in result.GetResultsAlongSampleLines())
                    {
                        var v = sec.VolumeResult;
                        rows.Add(new object[]
                        {
                            ++i, Station(sec.Station),
                            Math.Round(v.CumulativeCutVolume, 3), Math.Round(v.CumulativeFillVolume, 3),
                            Math.Round(v.IncrementalCutVolume, 3), Math.Round(v.IncrementalFillVolume, 3)
                        });
                        cut = v.CumulativeCutVolume;
                        fill = v.CumulativeFillVolume;
                    }
                    rows.Add(new object[] { "合计", "", Math.Round(cut, 3), Math.Round(fill, 3), "", "" });

                    double len = al.Length;
                    sheets.Add((al.Name, HeadersQto, rows));
                    summary.Add(new object[]
                        { summary.Count + 1, al.Name, Math.Round(len, 3), Math.Round(cut, 3) });
                    sumLen += len;
                    sumVol += cut;

                    perAl.Add(new JsonObject
                    {
                        ["alignment"] = al.Name,
                        ["group"] = slg.Name,
                        ["length"] = Round(len, 3),
                        ["sections"] = i,
                        ["total_cut_m3"] = Round(cut, 3),
                        ["total_fill_m3"] = Round(fill, 3)
                    });
                }
                tr.Commit();
            }

            if (sheets.Count == 0)
                throw new InvalidOperationException(
                    "没有任何路线带材质列表（共 " + skipped.Count + " 条路线都没有），先跑 compute_quantities。");

            summary.Add(new object[] { "合计", "", Math.Round(sumLen, 3), Math.Round(sumVol, 3) });
            sheets.Insert(0, ("汇总", HeadersVolSummary, summary));

            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            Excel.WriteWorkbook(outPath, sheets);

            return new JsonObject
            {
                ["file"] = outPath,
                ["alignments"] = sheets.Count - 1,
                ["skipped"] = skipped,
                ["total_length_m"] = Round(sumLen, 3),
                ["total_volume_m3"] = Round(sumVol, 3),
                ["per_alignment"] = perAl
            };
        }
    }
}
