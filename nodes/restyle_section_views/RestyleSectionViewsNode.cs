using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivSampleLine = Autodesk.Civil.DatabaseServices.SampleLine;
using CivSampleLineGroup = Autodesk.Civil.DatabaseServices.SampleLineGroup;
using CivSectionSource = Autodesk.Civil.DatabaseServices.SectionSource;
using CivQtoMaterialList = Autodesk.Civil.DatabaseServices.QTOMaterialList;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        /// <summary>
        /// 给**已存在**的横断面图就地换样式 / 定高程范围，不删不重建。
        ///
        /// 为什么有这个零件：create_section_views 覆盖重建要先删旧视图，旧视图带体积表时
        /// 删完 save_dwg 必报 eWasOpenForWrite（2026-08-16 项目B实测二分：连照抄成功任务卡
        /// 的参数单跑也炸，唯一成功过的组合是「create_sample_lines 删组连带删视图」在前）。
        /// 视图样式和高程本来就是 create_section_views 建完**事后**设的（同一机制），
        /// 对现有视图重设一遍效果等价，且完全不碰采样线、材质列表、体积表和工程量。
        /// </summary>
        static JsonNode RunNodeRestyleSectionViews(JsonObject a, Document doc)
        {
            string alName = GetString(a, "alignment", null);          // 缺省 = 全部路线
            string style = GetString(a, "style", null);               // 断面图样式（SectionViewStyles）
            string secStyle = GetString(a, "section_style", null);    // 地面线断面样式（SectionStyles）
            string matStyle = GetString(a, "material_style", null);   // 材质断面样式（挖方填充观感归它），不给就不碰
            // 材质**填充**样式（ShapeStyles）：挖方填充区在图上长什么样归它管，
            // 挂在材质列表 QTOMaterial.ShapeStyleId 上，跟断面样式是两码事
            string matShape = GetString(a, "material_shape_style", null);
            // 道路曲面断面（SourceType=CorridorSurface）：想隐藏就指个不打印样式，不给不碰
            string corSurfStyle = GetString(a, "corridor_surface_style", null);
            double elevMin = GetDouble(a, "elev_min", 0);
            double elevMax = GetDouble(a, "elev_max", 0);             // min>=max → 不动高程
            bool manualElev = elevMin < elevMax;
            bool autoElev = GetBool(a, "elev_auto", false);           // 显式回自动
            if (string.IsNullOrEmpty(style) && string.IsNullOrEmpty(secStyle)
                && string.IsNullOrEmpty(matStyle) && string.IsNullOrEmpty(matShape)
                && string.IsNullOrEmpty(corSurfStyle) && !manualElev && !autoElev)
                throw new InvalidOperationException("style / section_style / material_style / material_shape_style / corridor_surface_style / elev_min+elev_max / elev_auto 至少给一样。");

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            int views = 0, groundStyled = 0, matStyled = 0, alignHit = 0, matShapeSet = 0, corSurfStyled = 0;
            string matStyleResolved = null;
            var perAlign = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId svStyleId = ObjectId.Null;
                if (!string.IsNullOrEmpty(style))
                {
                    svStyleId = FindStyleId(tr, civ.Styles.SectionViewStyles, style);
                    if (svStyleId.IsNull)
                        throw new InvalidOperationException("找不到断面图样式 '" + style + "'。");
                }
                ObjectId secStyleId = string.IsNullOrEmpty(secStyle)
                    ? ObjectId.Null : FindStyleId(tr, civ.Styles.SectionStyles, secStyle);
                if (!string.IsNullOrEmpty(secStyle) && secStyleId.IsNull)
                    throw new InvalidOperationException("找不到断面样式 '" + secStyle + "'。");
                // 材质断面（挖方填充）：**不给就不碰**。
                // 不能拿地面线样式垫底——那会把用户手设的 @C3DF-CutFill 冲成 C3DF-GroundLine
                // （2026-08-16 用户在属性面板手改 215 个 Material Section 的 Style=@C3DF-CutFill 后教的：
                // 渲染读的是 MaterialSection.StyleId 这个槽）。
                // 名字先在 SectionStyles 找，找不到再找 ShapeStyles——@C3DF-CutFill 就在后者。
                // FindStyleId 匹配不到会兜底返回集合第一个（永不 Null），两级查找必须用严格版
                ObjectId matStyleId = ObjectId.Null;
                if (!string.IsNullOrEmpty(matStyle))
                {
                    matStyleId = FindStyleIdStrict(tr, civ.Styles.SectionStyles, matStyle);
                    if (matStyleId.IsNull)
                        matStyleId = FindStyleIdStrict(tr, civ.Styles.ShapeStyles, matStyle);
                    if (matStyleId.IsNull)
                        throw new InvalidOperationException("材质断面样式 '" + matStyle + "' 在 SectionStyles/ShapeStyles 里都找不到。");
                    matStyleResolved = StyleName(tr.GetObject(matStyleId, OpenMode.ForRead));
                }
                ObjectId matShapeId = ObjectId.Null;
                if (!string.IsNullOrEmpty(matShape))
                {
                    matShapeId = FindStyleId(tr, civ.Styles.ShapeStyles, matShape);
                    if (matShapeId.IsNull)
                        throw new InvalidOperationException("找不到填充样式（ShapeStyle）'" + matShape + "'。");
                }
                ObjectId corSurfId = ObjectId.Null;
                if (!string.IsNullOrEmpty(corSurfStyle))
                {
                    corSurfId = FindStyleIdStrict(tr, civ.Styles.SectionStyles, corSurfStyle);
                    if (corSurfId.IsNull)
                        throw new InvalidOperationException("找不到道路曲面断面样式 '" + corSurfStyle + "'（SectionStyles）。");
                }

                foreach (ObjectId alId in ModelSpace(db, tr))
                {
                    CivAlignment al;
                    try { al = tr.GetObject(alId, OpenMode.ForRead) as CivAlignment; }
                    catch { continue; }
                    if (al == null) continue;
                    if (!string.IsNullOrEmpty(alName)
                        && !string.Equals(al.Name, alName, StringComparison.OrdinalIgnoreCase)) continue;

                    int myViews = 0;
                    foreach (ObjectId gid in al.GetSampleLineGroupIds())
                    {
                        // 组必须开写：改 GetSectionSources 下属断面的样式时，Civil 会回写组内数据，
                        // 组只开读就 eNotOpenForWrite 硬崩进程（2026-08-16 实测；
                        // create_section_views 里也是先 slg.UpgradeOpen 再动来源）
                        var g = (CivSampleLineGroup)tr.GetObject(gid, OpenMode.ForWrite);
                        foreach (ObjectId slId in g.GetSampleLineIds())
                        {
                            var sl = (CivSampleLine)tr.GetObject(slId, OpenMode.ForRead);
                            foreach (ObjectId svId in sl.GetSectionViewIds())
                            {
                                var sv = (Autodesk.Civil.DatabaseServices.SectionView)
                                    tr.GetObject(svId, OpenMode.ForWrite);
                                if (!svStyleId.IsNull) sv.StyleId = svStyleId;
                                if (manualElev)
                                {
                                    sv.IsElevationRangeAutomatic = false;
                                    sv.ElevationMin = elevMin;
                                    sv.ElevationMax = elevMax;
                                }
                                else if (autoElev) sv.IsElevationRangeAutomatic = true;
                                myViews++;
                            }
                        }

                        // 材质填充样式：改材质列表里每个 QTOMaterial 的 ShapeStyleId
                        if (!matShapeId.IsNull)
                        {
                            foreach (CivQtoMaterialList ml in g.MaterialLists)
                            {
                                foreach (object item in (System.Collections.IEnumerable)ml)
                                {
                                    var p = item?.GetType().GetProperty("ShapeStyleId");
                                    if (p == null || !p.CanWrite) continue;
                                    try { p.SetValue(item, matShapeId); matShapeSet++; }
                                    catch { }
                                }
                            }
                        }

                        // 断面自身样式：走廊本体断面的 StyleId 是代码集样式，这里**不碰**
                        // （SourceType 含 Corridor 且不含 CorridorSurface 的跳过）；
                        // 材质断面 → material_style，其余（地面线/道路曲面）→ section_style。
                        // 分型规则照抄 create_section_views（2026-07-28 逐属性对比查实的那套）。
                        if (secStyleId.IsNull && matStyleId.IsNull && corSurfId.IsNull) continue;
                        foreach (CivSectionSource src in g.GetSectionSources())
                        {
                            string sourceType = "";
                            try { sourceType = src.SourceType.ToString(); } catch { }
                            bool isMaterial = sourceType.IndexOf("Material", StringComparison.OrdinalIgnoreCase) >= 0;
                            bool isCorridorSurface = !isMaterial
                                && sourceType.IndexOf("CorridorSurface", StringComparison.OrdinalIgnoreCase) >= 0;
                            bool isCorridorBody = !isMaterial && !isCorridorSurface
                                && sourceType.IndexOf("Corridor", StringComparison.OrdinalIgnoreCase) >= 0;
                            if (isCorridorBody) continue;
                            ObjectId want = isMaterial ? matStyleId
                                          : isCorridorSurface ? (corSurfId.IsNull ? secStyleId : corSurfId)
                                          : secStyleId;
                            if (want.IsNull) continue;
                            foreach (ObjectId secId in src.GetSectionIds())
                            {
                                try
                                {
                                    var sec = (Autodesk.Civil.DatabaseServices.Section)
                                        tr.GetObject(secId, OpenMode.ForWrite);
                                    sec.StyleId = want;
                                    if (isMaterial) matStyled++;
                                    else if (isCorridorSurface && !corSurfId.IsNull) corSurfStyled++;
                                    else groundStyled++;
                                }
                                catch { }
                            }
                        }
                    }
                    if (myViews > 0)
                    {
                        alignHit++;
                        views += myViews;
                        perAlign.Add(new JsonObject { ["alignment"] = al.Name, ["views"] = myViews });
                    }
                }
                tr.Commit();
            }
            if (views == 0)
                throw new InvalidOperationException(string.IsNullOrEmpty(alName)
                    ? "图里没有任何横断面图。"
                    : "路线 '" + alName + "' 没有横断面图。");

            return new JsonObject
            {
                ["alignments"] = alignHit,
                ["material_style_resolved"] = matStyleResolved,
                ["views_restyled"] = views,
                ["view_style"] = style,
                ["ground_sections_styled"] = groundStyled,
                ["material_sections_styled"] = matStyled,
                ["corridor_surface_sections_styled"] = corSurfStyled,
                ["material_shape_styles_set"] = matShapeSet,
                ["material_shape_style"] = matShape,
                ["elevation"] = manualElev ? elevMin + " ~ " + elevMax
                              : (autoElev ? "自动" : "未动"),
                ["per_alignment"] = perAlign
            };
        }

        /// <summary>FindStyleId 的严格版：精确 > 忽略大小写 > 前缀 > 包含，匹配不到返回 Null
        /// （FindStyleId 会兜底返回集合第一个，跨集合两级查找必须靠 Null 才能走下一级）。</summary>
        static ObjectId FindStyleIdStrict(Transaction tr, object collection, string name)
        {
            if (string.IsNullOrEmpty(name)) return ObjectId.Null;
            if (!(collection is System.Collections.IEnumerable en)) return ObjectId.Null;
            var all = new List<KeyValuePair<ObjectId, string>>();
            foreach (object item in en)
            {
                if (!(item is ObjectId id)) continue;
                string n;
                try { n = StyleName(tr.GetObject(id, OpenMode.ForRead)); }
                catch { continue; }
                if (string.IsNullOrEmpty(n)) continue;
                if (n == name) return id;
                all.Add(new KeyValuePair<ObjectId, string>(id, n));
            }
            foreach (var kv in all)
                if (string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            foreach (var kv in all)
                if (kv.Value.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            foreach (var kv in all)
                if (kv.Value.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return kv.Key;
            return ObjectId.Null;
        }
    }
}
