using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        /// <summary>
        /// 测量断面标定与地面线提取（只读）。拆堤链路第一步：先看哪些断面标定得住、
        /// 桩号间距是多少、堤顶多高，再决定 generate_demolition_design_lines 的参数怎么给。
        /// 标定与几何算法在 Civil3DFactory/MeasuredSections.cs（三节点共用，源自拆堤插件 V1）。
        /// </summary>
        static JsonNode RunNodeExtractMeasuredSections(JsonObject a, Document doc)
            => ExtractMeasuredSections(a, doc);

        public static JsonNode ExtractMeasuredSections(JsonObject a, Document doc)
        {
            var opt = new MeasuredSectionOptions();
            opt.GroundLayer = GetString(a, "ground_layer", opt.GroundLayer);
            opt.ColumnLayer = GetString(a, "column_layer", opt.ColumnLayer);
            opt.TitleLayer = GetString(a, "title_layer", opt.TitleLayer);
            opt.TitleRegex = GetString(a, "title_regex", opt.TitleRegex);
            opt.OffsetTolerance = GetDouble(a, "offset_tolerance", opt.OffsetTolerance);
            opt.ElevTolerance = GetDouble(a, "elev_tolerance", opt.ElevTolerance);
            opt.TableDepth = GetDouble(a, "table_depth", opt.TableDepth);
            opt.PairTolX = GetDouble(a, "pair_tol_x", opt.PairTolX);
            Sections.Check(opt);

            Regex filter = Sections.CompileFilter(GetString(a, "line_filter", null));
            bool includePoints = GetBool(a, "include_ground_points", false);

            Database db = doc.Database;
            var sections = new JsonArray();
            var lines = new JsonArray();
            int valid = 0, invalid = 0, total = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                List<MeasuredSection> all = Sections.Read(db, tr, opt);
                if (all.Count == 0)
                    throw new InvalidOperationException(
                        "模型空间在图层 '" + opt.GroundLayer + "' 上找不到地面线多段线（≥2 顶点）；" +
                        "先确认 ground_layer 与本图一致。");

                List<MeasuredSection> picked = all.Where(s => Sections.Matches(s, filter)).ToList();
                if (picked.Count == 0)
                    throw new InvalidOperationException(
                        "共读到 " + all.Count + " 个断面，但没有一个匹配 line_filter；放宽或去掉该参数。");

                foreach (MeasuredSection s in picked)
                {
                    total++;
                    if (s.Valid) valid++; else invalid++;

                    var item = new JsonObject
                    {
                        ["title"] = s.Title,
                        ["line_name"] = s.LineName,
                        ["stake_m"] = Math.Round(s.StakeM, 3),
                        ["stake"] = string.IsNullOrEmpty(s.LineName) ? null : s.Stake,
                        ["valid"] = s.Valid,
                        ["report"] = s.Report,
                        ["offset_marks"] = s.OffsetMarks,
                        ["elev_marks"] = s.ElevMarks,
                        ["offset_residual"] = Sections.Num(s.OffsetResidual, 4),
                        ["elev_residual"] = Sections.Num(s.ElevResidual, 4),
                        ["scale_x"] = Sections.Num(s.Kx * 1000, 2),
                        ["scale_y"] = Sections.Num(s.Ky * 1000, 2),
                        ["ground_handle"] = s.GroundHandle,
                        ["ground_points"] = s.Ground.Count
                    };
                    if (s.Ground.Count > 0)
                    {
                        item["offset_min"] = Math.Round(s.Ground.Min(p => p.X), 3);
                        item["offset_max"] = Math.Round(s.Ground.Max(p => p.X), 3);
                        item["elev_min"] = Math.Round(s.Ground.Min(p => p.Y), 3);
                        item["elev_max"] = Math.Round(s.Ground.Max(p => p.Y), 3);
                        if (includePoints)
                        {
                            var pts = new JsonArray();
                            foreach (Point2d p in s.Ground)
                                pts.Add(new JsonArray { Math.Round(p.X, 3), Math.Round(p.Y, 3) });
                            item["points"] = pts;
                        }
                    }
                    sections.Add(item);
                }

                foreach (var grp in picked.Where(s => !string.IsNullOrEmpty(s.LineName))
                                          .GroupBy(s => s.LineName).OrderBy(g => g.Key))
                {
                    List<MeasuredSection> ord = grp.OrderBy(s => s.StakeM).ToList();
                    var spacings = new List<double>();
                    for (int i = 1; i < ord.Count; i++) spacings.Add(ord[i].StakeM - ord[i - 1].StakeM);
                    List<double> crests = ord.Where(s => s.Ground.Count > 0)
                                             .Select(s => s.Ground.Max(p => p.Y)).ToList();
                    lines.Add(new JsonObject
                    {
                        ["line_name"] = grp.Key,
                        ["sections"] = ord.Count,
                        ["valid"] = ord.Count(s => s.Valid),
                        ["stake_from"] = Math.Round(ord[0].StakeM, 3),
                        ["stake_to"] = Math.Round(ord[ord.Count - 1].StakeM, 3),
                        ["spacing_min"] = spacings.Count == 0 ? null : Sections.Num(spacings.Min(), 3),
                        ["spacing_max"] = spacings.Count == 0 ? null : Sections.Num(spacings.Max(), 3),
                        ["crest_elev_max"] = crests.Count == 0 ? null : Sections.Num(crests.Max(), 3)
                    });
                }

                tr.Commit();
            }

            return new JsonObject
            {
                ["sections"] = sections,
                ["section_count"] = total,
                ["valid_count"] = valid,
                ["invalid_count"] = invalid,
                ["lines"] = lines
            };
        }
    }
}
