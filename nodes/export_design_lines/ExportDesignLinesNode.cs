using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlign = Autodesk.Civil.DatabaseServices.Alignment;
using CivCorr = Autodesk.Civil.DatabaseServices.Corridor;
using CivBaseline = Autodesk.Civil.DatabaseServices.Baseline;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivTin = Autodesk.Civil.DatabaseServices.TinSurface;
using CivSubE = Autodesk.Civil.DatabaseServices.AlignmentSubEntity;
using CivSubArc = Autodesk.Civil.DatabaseServices.AlignmentSubEntityArc;
using CivSubType = Autodesk.Civil.DatabaseServices.AlignmentSubEntityType;

namespace Civil3DFactory
{
    /// <summary>
    /// export_design_lines：把设计意图从 Civil 3D 对象里搬到多段线上，写进一张全新空白 DWG。
    /// 产出的三类线就是以后建模的**输入参数**，模型退化为下游产物。
    ///
    ///   中心线-{通道}          走廊基线所用的路线，非闭合
    ///   边线-{通道}-{左|右}    其余路线，按几何归属到通道并定左右，非闭合
    ///   边界-{通道}            走廊曲面的外边界，闭合
    ///
    /// 三处不靠猜：
    /// · 路线枚举走 ModelSpace，不用 CivilDocument.GetAlignmentIds()
    ///   —— 后者漏掉场地内的路线（本图 B1 的两条边线就在场地里，导致 19/21 之差）。
    /// · 通道归属和左右用父中心线的 StationOffset 实测（正=右 负=左），
    ///   不信 OffsetAlignmentInfo.NominalOffset（本图读出 1(E) 左 75 右 25，与图名 25 不符）。
    /// · 中心线集合 = 走廊基线引用的路线，不靠名字规则。
    ///
    /// 几何精确：直线与圆弧原样保留（圆弧走 bulge = tan(Δ/4)，顺时针取负），
    /// 只有缓和曲线按 spiral_step 采样。每条线回报 length_delta 自检。
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeExportDesignLines(JsonObject args, Document doc)
            => ExportDesignLines(args, doc);

        sealed class EdlLine
        {
            public string Source;              // 源对象名
            public string Channel;
            public string Role;                // CENTER / LEFT / RIGHT / BOUNDARY
            public double SrcLength;
            public bool Closed;
            public double OffMin, OffMax, OffMean;
            public List<Point2d> V = new List<Point2d>();
            public List<double> B = new List<double>();
            public int Lines, Arcs, Spirals;
        }

        public static JsonNode ExportDesignLines(JsonObject a, Document doc)
        {
            string outPath = Need(a, "out");
            if (!Path.IsPathRooted(outPath))
                throw new InvalidOperationException("out 必须是绝对路径：" + outPath);
            bool overwrite = GetBool(a, "overwrite", false);
            if (File.Exists(outPath) && !overwrite)
                throw new InvalidOperationException("文件已存在，拒绝覆盖：" + outPath + "（确需覆盖传 overwrite:true）");

            string tplCenter = GetString(a, "center_layer", "CL-{channel}");
            string tplEdge = GetString(a, "edge_layer", "EDGE-{channel}-{side}");
            string tplBound = GetString(a, "boundary_layer", "BOUNDARY-{channel}");
            short cColor = (short)GetDouble(a, "center_color", 3);
            short eColor = (short)GetDouble(a, "edge_color", 4);
            short bColor = (short)GetDouble(a, "boundary_color", 2);
            string cLt = GetString(a, "center_linetype", "CENTER2");
            string eLt = GetString(a, "edge_linetype", "Continuous");
            string bLt = GetString(a, "boundary_linetype", "Continuous");
            double ltScale = GetDouble(a, "linetype_scale", 5.0);
            double spiralStep = GetDouble(a, "spiral_step", 5.0);
            if (spiralStep <= 1e-6) spiralStep = 5.0;
            int probes = (int)GetDouble(a, "probe_points", 20);
            if (probes < 3) probes = 3;
            double offTol = GetDouble(a, "offset_tolerance", 200.0);
            bool withBoundaries = GetBool(a, "boundaries", true);

            Database db = doc.Database;
            var lines = new List<EdlLine>();
            var notes = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // ---- 1 从 ModelSpace 收全部路线、走廊、曲面 ----
                var aligns = new List<CivAlign>();
                var corrs = new List<CivCorr>();
                var tins = new List<CivTin>();
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    DBObject o;
                    try { o = tr.GetObject(id, OpenMode.ForRead); } catch (System.Exception) { continue; }
                    if (o is CivAlign) aligns.Add((CivAlign)o);
                    else if (o is CivCorr) corrs.Add((CivCorr)o);
                    else if (o is CivTin) tins.Add((CivTin)o);
                }

                // ---- 2 中心线 = 走廊基线引用的路线 ----
                var centerIds = new HashSet<ObjectId>();
                var corridorChannel = new Dictionary<string, string>();   // 走廊名 → 通道名
                foreach (CivCorr c in corrs)
                {
                    string cname = "";
                    try { cname = c.Name; } catch (System.Exception) { }
                    try
                    {
                        foreach (CivBaseline bl in c.Baselines)
                        {
                            ObjectId aid = bl.AlignmentId;
                            if (aid.IsNull) continue;
                            centerIds.Add(aid);
                            var al = tr.GetObject(aid, OpenMode.ForRead) as CivAlign;
                            if (al != null && !corridorChannel.ContainsKey(cname))
                                corridorChannel[cname] = al.Name;
                        }
                    }
                    catch (System.Exception ex) { notes.Add("走廊 " + cname + " 读基线失败：" + ex.Message); }
                }
                if (centerIds.Count == 0)
                    throw new InvalidOperationException("图里没有走廊基线，无法确定哪些路线是中心线。");

                var centers = new List<CivAlign>();
                var edges = new List<CivAlign>();
                foreach (CivAlign al in aligns)
                    (centerIds.Contains(al.ObjectId) ? centers : edges).Add(al);

                var diagC = new JsonArray();
                foreach (CivAlign x in centers) diagC.Add(x.Name);
                var diagE = new JsonArray();
                foreach (CivAlign x in edges) diagE.Add(x.Name);
                notes.Add("诊断：ModelSpace 找到路线 " + aligns.Count +
                          " 条；判为中心线 " + centers.Count + " 条 " + diagC.ToJsonString() +
                          "；判为边线 " + edges.Count + " 条 " + diagE.ToJsonString());

                // ---- 3 中心线 ----
                foreach (CivAlign al in centers)
                {
                    var it = new EdlLine { Source = al.Name, Channel = al.Name, Role = "CENTER", SrcLength = al.Length };
                    if (EdlExtract(al, spiralStep, it)) lines.Add(it);
                    else notes.Add("中心线 " + al.Name + " 没有可转换的几何，已跳过");
                }

                // ---- 4 边线：按实测偏距归属通道并定左右 ----
                foreach (CivAlign al in edges)
                {
                    var it = new EdlLine { Source = al.Name, SrcLength = al.Length };
                    if (!EdlExtract(al, spiralStep, it))
                    { notes.Add("边线 " + al.Name + " 没有可转换的几何，已跳过"); continue; }

                    CivAlign best = null;
                    double bestAbs = double.MaxValue, bMin = 0, bMax = 0, bMean = 0;
                    foreach (CivAlign c in centers)
                    {
                        double mn, mx, mean;
                        int hit = EdlMeasure(c, al, probes, out mn, out mx, out mean);
                        if (hit < probes / 2) continue;                 // 多数点落在该中心线桩号范围外
                        double mag = Math.Abs(mean);
                        if (mag > offTol) continue;                     // 离得太远，不是这条通道的边线
                        if (mag < bestAbs) { bestAbs = mag; best = c; bMin = mn; bMax = mx; bMean = mean; }
                    }

                    if (best == null)
                    {
                        // 报清楚每条中心线各自量到了什么，别只说"找不到"
                        var why = new JsonArray();
                        foreach (CivAlign c in centers)
                        {
                            double m1, m2, m3;
                            int h = EdlMeasure(c, al, probes, out m1, out m2, out m3);
                            why.Add(c.Name + ": 命中 " + h + "/" + probes +
                                    " 平均偏距 " + Math.Round(m3, 2));
                        }
                        notes.Add("边线 " + al.Name + " 找不到归属中心线（需命中≥" + (probes / 2) +
                                  " 且 |平均偏距|≤" + offTol.ToString("0.#") + " m）。逐条实测：" +
                                  why.ToJsonString());
                        continue;
                    }
                    it.Channel = best.Name;
                    it.Role = bMean >= 0 ? "RIGHT" : "LEFT";            // Civil 约定：正=右 负=左
                    it.OffMin = Math.Abs(bMin); it.OffMax = Math.Abs(bMax); it.OffMean = Math.Abs(bMean);
                    lines.Add(it);
                }

                // ---- 5 道路边界：走廊曲面外边界（闭合）----
                if (withBoundaries)
                {
                    foreach (CivTin s in tins)
                    {
                        string sname = "";
                        try { sname = s.Name; } catch (System.Exception) { }
                        string channel = null;
                        foreach (var kv in corridorChannel)
                            if (sname.IndexOf(kv.Key, StringComparison.Ordinal) >= 0) { channel = kv.Value; break; }
                        if (channel == null) continue;                  // 原地形等非走廊曲面

                        int got = 0;
                        try
                        {
                            var bdefs = s.BoundariesDefinition;
                            for (int bi = 0; bi < bdefs.Count; bi++)
                            {
                                var op = bdefs[bi];
                                foreach (Autodesk.Civil.DatabaseServices.SurfaceBoundary bd in op)
                                {
                                    var it = new EdlLine
                                    { Source = sname, Channel = channel, Role = "BOUNDARY", Closed = true };
                                    foreach (Point3d p in bd.Vertices)
                                    { it.V.Add(new Point2d(p.X, p.Y)); it.B.Add(0.0); it.Lines++; }
                                    if (it.V.Count < 3) continue;
                                    // 首末点重合则去掉末点，交给 Closed 标志
                                    if (it.V[0].GetDistanceTo(it.V[it.V.Count - 1]) < 1e-6)
                                    { it.V.RemoveAt(it.V.Count - 1); it.B.RemoveAt(it.B.Count - 1); }
                                    lines.Add(it); got++;
                                }
                            }
                        }
                        catch (System.Exception ex)
                        { notes.Add("曲面 " + sname + " 读边界失败：" + ex.Message); }
                        // 定义边界为空时退回 ExtractBorder：取曲面真实轮廓。
                        // 它会往宿主图里生成实体，读完就删；宿主图本来也不保存。
                        if (got == 0)
                        {
                            try
                            {
                                ObjectIdCollection ids = s.ExtractBorder(
                                    Autodesk.Civil.SurfaceExtractionSettingsType.Model);
                                foreach (ObjectId eid in ids)
                                {
                                    var ent = tr.GetObject(eid, OpenMode.ForWrite) as Entity;
                                    if (ent == null) continue;
                                    var it = new EdlLine
                                    { Source = sname + "(ExtractBorder)", Channel = channel, Role = "BOUNDARY", Closed = true };
                                    var pl2 = ent as Polyline;
                                    var p3 = ent as Polyline3d;
                                    if (pl2 != null)
                                    {
                                        for (int k = 0; k < pl2.NumberOfVertices; k++)
                                        { it.V.Add(pl2.GetPoint2dAt(k)); it.B.Add(pl2.GetBulgeAt(k)); }
                                    }
                                    else if (p3 != null)
                                    {
                                        foreach (ObjectId vid in p3)
                                        {
                                            var v = tr.GetObject(vid, OpenMode.ForRead) as PolylineVertex3d;
                                            if (v == null) continue;
                                            it.V.Add(new Point2d(v.Position.X, v.Position.Y)); it.B.Add(0.0);
                                        }
                                    }
                                    ent.Erase();                       // 读完即删，不留在宿主图
                                    if (it.V.Count < 3) continue;
                                    if (it.V[0].GetDistanceTo(it.V[it.V.Count - 1]) < 1e-6)
                                    { it.V.RemoveAt(it.V.Count - 1); it.B.RemoveAt(it.B.Count - 1); }
                                    it.Lines = it.V.Count;
                                    lines.Add(it); got++;
                                }
                                if (got > 0)
                                    notes.Add("曲面 " + sname + " 无定义边界，已用 ExtractBorder 取轮廓（" + got + " 条）");
                            }
                            catch (System.Exception ex)
                            { notes.Add("曲面 " + sname + " ExtractBorder 失败：" + ex.Message); }
                        }
                        if (got == 0) notes.Add("曲面 " + sname + " 取不到任何外边界");
                    }
                }
                tr.Commit();
            }

            if (lines.Count == 0)
                throw new InvalidOperationException("没有可导出的线。");

            // ---- 6 写进全新空白图 ----
            var items = new JsonArray();
            using (var nd = new Database(true, false))
            {
                nd.Insunits = UnitsValue.Meters;
                using (Transaction tr = nd.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(nd.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    foreach (EdlLine it in lines)
                    {
                        string side = it.Role == "LEFT" ? "左" : it.Role == "RIGHT" ? "右" : "";
                        string tpl = it.Role == "CENTER" ? tplCenter
                                   : it.Role == "BOUNDARY" ? tplBound : tplEdge;
                        string layer = tpl.Replace("{channel}", it.Channel).Replace("{side}", side);
                        string lt = it.Role == "CENTER" ? cLt : it.Role == "BOUNDARY" ? bLt : eLt;
                        short color = it.Role == "CENTER" ? cColor : it.Role == "BOUNDARY" ? bColor : eColor;

                        ObjectId ltId = A2PResolveLinetype(tr, nd, lt, "acadiso.lin");
                        ObjectId layerId = EadEnsureLayer(tr, nd, layer, color, ltId);

                        // ⚠ 先入库再设属性：未入库的实体还绑在宿主图上，
                        // 直接设 LayerId（取自新图）会抛 eWrongDatabase。
                        var pl = new Polyline(it.V.Count);
                        pl.SetDatabaseDefaults(nd);
                        for (int i = 0; i < it.V.Count; i++)
                            pl.AddVertexAt(i, it.V[i], it.B[i], 0.0, 0.0);
                        pl.Elevation = 0.0;
                        pl.Closed = it.Closed;
                        ms.AppendEntity(pl);
                        tr.AddNewlyCreatedDBObject(pl, true);

                        pl.LayerId = layerId;
                        pl.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                            Autodesk.AutoCAD.Colors.ColorMethod.ByAci, color);
                        if (!ltId.IsNull) pl.LinetypeId = ltId;
                        pl.LinetypeScale = ltScale;
                        EadSetXData(tr, nd, pl, it.Channel, it.Role, it.OffMean, it.Source);

                        double plLen = 0.0, plArea = 0.0;
                        try { plLen = pl.Length; } catch (System.Exception) { }
                        try { if (it.Closed) plArea = Math.Abs(pl.Area); } catch (System.Exception) { }

                        var row = new JsonObject
                        {
                            ["layer"] = layer,
                            ["channel"] = it.Channel,
                            ["role"] = it.Role,
                            ["source"] = it.Source,
                            ["closed"] = it.Closed,
                            ["vertices"] = it.V.Count,
                            ["arc_seg"] = it.Arcs,
                            ["spiral_seg"] = it.Spirals,
                            ["polyline_length"] = Round(plLen, 4)
                        };
                        if (it.Role == "BOUNDARY") row["area"] = Round(plArea, 3);
                        else
                        {
                            row["source_length"] = Round(it.SrcLength, 4);
                            row["length_delta"] = Round(plLen - it.SrcLength, 4);
                        }
                        if (it.Role == "LEFT" || it.Role == "RIGHT")
                        {
                            row["offset_mean"] = Round(it.OffMean, 3);
                            row["offset_min"] = Round(it.OffMin, 3);
                            row["offset_max"] = Round(it.OffMax, 3);
                        }
                        items.Add(row);
                    }
                    tr.Commit();
                }

                string dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                nd.SaveAs(outPath, DwgVersion.Current);
            }

            long bytes = 0;
            try { bytes = new FileInfo(outPath).Length; } catch (System.Exception) { }

            int nc = 0, ne = 0, nb = 0;
            foreach (EdlLine l in lines)
            { if (l.Role == "CENTER") nc++; else if (l.Role == "BOUNDARY") nb++; else ne++; }

            return new JsonObject
            {
                ["output"] = outPath,
                ["bytes"] = bytes,
                ["centerlines"] = nc,
                ["edge_lines"] = ne,
                ["boundaries"] = nb,
                ["total"] = lines.Count,
                ["notes"] = notes,
                ["items"] = items
            };
        }

        /// <summary>沿 edge 采样，量它相对 center 的桩号偏距。返回落在桩号范围内的点数。
        /// Civil 约定：offset 正 = 中心线右侧，负 = 左侧。</summary>
        static int EdlMeasure(CivAlign center, CivAlign edge, int probes,
            out double min, out double max, out double mean)
        {
            min = 0; max = 0; mean = 0;
            double lo = double.MaxValue, hi = double.MinValue, sum = 0;
            int hit = 0;
            double s0 = edge.StartingStation, s1 = edge.EndingStation;
            for (int i = 0; i < probes; i++)
            {
                double st = s0 + (s1 - s0) * i / (probes - 1.0);
                double e = 0, n = 0;
                try { edge.PointLocation(st, 0.0, ref e, ref n); }
                catch (System.Exception) { continue; }
                double station = 0, offset = 0;
                bool oor = false;
                try { center.StationOffsetAcceptOutOfRange(e, n, ref station, ref offset, ref oor); }
                catch (System.Exception) { continue; }
                if (oor) continue;
                hit++;
                if (offset < lo) lo = offset;
                if (offset > hi) hi = offset;
                sum += offset;
            }
            if (hit == 0) return 0;
            min = lo; max = hi; mean = sum / hit;
            return hit;
        }

        /// <summary>路线 → 顶点/bulge。直线圆弧精确，缓和曲线按步长采样。</summary>
        static bool EdlExtract(CivAlign al, double spiralStep, EdlLine it)
        {
            Point2d tail = new Point2d(0.0, 0.0);
            bool hasTail = false;
            var ents = al.Entities;
            int n = ents.Count;
            if (n <= 0) return false;
            for (int i = 0; i < n; i++)
            {
                var ent = ents.GetEntityByOrder(i);
                for (int j = 0; j < ent.SubEntityCount; j++)
                {
                    CivSubE sub = ent[j];
                    if (sub.SubEntityType == CivSubType.Arc)
                    {
                        var arc = sub as CivSubArc;
                        double bulge = 0.0;
                        if (arc != null)
                        {
                            bulge = Math.Tan(Math.Abs(arc.Delta) / 4.0);
                            if (arc.Clockwise) bulge = -bulge;
                        }
                        it.V.Add(sub.StartPoint); it.B.Add(bulge); it.Arcs++;
                    }
                    else if (sub.SubEntityType == CivSubType.Spiral)
                    {
                        double a0 = sub.StartStation, a1 = sub.EndStation, span = a1 - a0;
                        int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(span) / spiralStep));
                        for (int k = 0; k < steps; k++)
                        {
                            double e = 0, nn = 0;
                            al.PointLocation(a0 + span * k / steps, 0.0, ref e, ref nn);
                            it.V.Add(new Point2d(e, nn)); it.B.Add(0.0);
                        }
                        it.Spirals++;
                    }
                    else { it.V.Add(sub.StartPoint); it.B.Add(0.0); it.Lines++; }
                    tail = sub.EndPoint; hasTail = true;
                }
            }
            if (it.V.Count == 0) return false;
            if (hasTail) { it.V.Add(tail); it.B.Add(0.0); }
            return true;
        }
    }
}
