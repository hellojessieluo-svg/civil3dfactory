using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlign = Autodesk.Civil.DatabaseServices.Alignment;
using CivilDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// offsets_from_boundary（S02）：把闭合边界多段线劈成左右两条偏移路线，供走廊当宽度目标。
    ///
    /// 做法：边界每个顶点用中心线 StationOffset 投影成 (桩号, 偏距)，按 interval 分桶，
    /// 桶内最负偏距 = 左半宽、最正偏距 = 右半宽，再用 PointLocation(桩号, ±半宽) 回投成点。
    /// 顺带解决两个问题：端部封口顶点被自然排除（它们不构成某桩号的极值）；
    /// 三角网轮廓那几百个碎顶点被抽稀成按 interval 的规整线。
    ///
    /// 命名按 create_corridor 的约定：{通道}_左 / {通道}_右（它按名前缀找目标，不看偏移距离）。
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeOffsetsFromBoundary(JsonObject args, Document doc)
            => OffsetsFromBoundary(args, doc);

        public static JsonNode OffsetsFromBoundary(JsonObject a, Document doc)
        {
            string bPrefix = GetString(a, "boundary_prefix", "BOUNDARY-");
            double interval = GetDouble(a, "interval", 25.0);
            if (interval <= 1e-6) interval = 25.0;
            string onlyCh = GetString(a, "channel", null);
            string style = GetString(a, "style", null);
            string labelSet = GetString(a, "label_set", null);
            bool eraseBoundary = GetBool(a, "erase_boundary", false);
            bool replaceExisting = GetBool(a, "replace_existing", true);
            double minArea = GetDouble(a, "min_boundary_area", 100.0);

            Database db = doc.Database;
            var made = new JsonArray();
            var notes = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivilDoc civ = Civ(db);
                ObjectId styleId = FindStyleId(tr, civ.Styles.AlignmentStyles, style);
                ObjectId labelId = FindStyleId(tr,
                    civ.Styles.LabelSetStyles.AlignmentLabelSetStyles, labelSet);
                if (labelId.IsNull)
                    throw new InvalidOperationException("当前图里没有任何路线标签集样式，无法建偏移路线。");

                var byName = new Dictionary<string, CivAlign>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId aid in civ.GetAlignmentIds())
                {
                    var x = tr.GetObject(aid, OpenMode.ForRead) as CivAlign;
                    if (x != null) byName[x.Name] = x;
                }

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // 收边界多段线
                var bounds = new List<KeyValuePair<string, Polyline>>();
                foreach (ObjectId id in ms)
                {
                    var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (pl == null) continue;
                    string layer = pl.Layer ?? "";
                    if (!layer.StartsWith(bPrefix, StringComparison.Ordinal)) continue;
                    string ch = layer.Substring(bPrefix.Length);
                    if (string.IsNullOrWhiteSpace(ch)) continue;
                    if (!string.IsNullOrWhiteSpace(onlyCh) &&
                        !string.Equals(ch, onlyCh, StringComparison.OrdinalIgnoreCase)) continue;
                    double area = 0.0;
                    try { area = Math.Abs(pl.Area); } catch (System.Exception) { }
                    if (area < minArea)
                    { notes.Add("丢弃碎片边界 " + layer + "（面积 " + Math.Round(area, 2) + "）"); continue; }
                    bounds.Add(new KeyValuePair<string, Polyline>(ch, pl));
                }
                if (bounds.Count == 0)
                    throw new InvalidOperationException("没找到任何边界多段线（前缀 \"" + bPrefix + "\"）。");

                foreach (var kv in bounds)
                {
                    string ch = kv.Key;
                    Polyline bpl = kv.Value;
                    CivAlign center;
                    if (!byName.TryGetValue(ch, out center))
                    { notes.Add("边界 " + ch + " 找不到同名中心线路线，跳过"); continue; }

                    double s0 = center.StartingStation, s1 = center.EndingStation;
                    int nv = bpl.NumberOfVertices;

                    // 探针半长：取边界顶点里最大偏距再放宽，保证垂线一定穿透两侧
                    double probeHalf = 0.0;
                    for (int i = 0; i < nv; i++)
                    {
                        Point2d p = bpl.GetPoint2dAt(i);
                        double st = 0, off = 0; bool oor = false;
                        try { center.StationOffsetAcceptOutOfRange(p.X, p.Y, ref st, ref off, ref oor); }
                        catch (System.Exception) { continue; }
                        if (Math.Abs(off) > probeHalf) probeHalf = Math.Abs(off);
                    }
                    probeHalf = probeHalf * 1.5 + 10.0;

                    // ⚠ 不要用「顶点按桩号分桶取极值」：三角网轮廓的顶点沿桩号分布极不均匀，
                    // 某些桶采不到最外侧顶点，线会被掐细（实测 B1 左侧被掐到 18.6m，实际 35m）。
                    // 改为几何求交：每个桩号作一条垂线去截边界，交点即真实左右边缘。
                    var lp = new List<Point2d>();
                    var rp = new List<Point2d>();
                    double lMin = double.MaxValue, lMax = 0, rMin = double.MaxValue, rMax = 0;
                    int nStep = Math.Max(2, (int)Math.Ceiling((s1 - s0) / interval));
                    double eps = Math.Min(0.05, (s1 - s0) * 1e-4);   // 躲开两端封口
                    for (int i = 0; i <= nStep; i++)
                    {
                        double st = s0 + (s1 - s0) * i / (double)nStep;
                        if (i == 0) st += eps;
                        if (i == nStep) st -= eps;

                        double ax = 0, ay = 0, bx = 0, by = 0;
                        try
                        {
                            center.PointLocation(st, -probeHalf, ref ax, ref ay);
                            center.PointLocation(st, probeHalf, ref bx, ref by);
                        }
                        catch (System.Exception) { continue; }

                        double lo = 0.0, hi = 0.0;
                        bool okL = false, okR = false;
                        using (var probe = new Line(new Point3d(ax, ay, 0.0), new Point3d(bx, by, 0.0)))
                        {
                            var xs = new Point3dCollection();
                            try { probe.IntersectWith(bpl, Intersect.OnBothOperands, xs, IntPtr.Zero, IntPtr.Zero); }
                            catch (System.Exception) { continue; }
                            foreach (Point3d x in xs)
                            {
                                double xst = 0, xoff = 0; bool oor = false;
                                try { center.StationOffsetAcceptOutOfRange(x.X, x.Y, ref xst, ref xoff, ref oor); }
                                catch (System.Exception) { continue; }
                                if (xoff < 0) { if (!okL || -xoff > lo) { lo = -xoff; okL = true; } }
                                else { if (!okR || xoff > hi) { hi = xoff; okR = true; } }
                            }
                        }
                        if (!okL || !okR || lo <= 1e-9 || hi <= 1e-9) continue;

                        double e = 0, n = 0;
                        try { center.PointLocation(st, -lo, ref e, ref n); lp.Add(new Point2d(e, n)); }
                        catch (System.Exception) { continue; }
                        try { center.PointLocation(st, hi, ref e, ref n); rp.Add(new Point2d(e, n)); }
                        catch (System.Exception) { continue; }
                        if (lo < lMin) lMin = lo; if (lo > lMax) lMax = lo;
                        if (hi < rMin) rMin = hi; if (hi > rMax) rMax = hi;
                    }
                    if (lp.Count < 2 || rp.Count < 2)
                    { notes.Add("边界 " + ch + " 有效采样点不足（左 " + lp.Count + " 右 " + rp.Count + "），跳过"); continue; }

                    string lName = ch + "_左", rName = ch + "_右";
                    ObjectId lId = OfbMakeAlignment(tr, civ, ms, db, lp, lName, styleId, labelId,
                                                    byName, replaceExisting, notes);
                    ObjectId rId = OfbMakeAlignment(tr, civ, ms, db, rp, rName, styleId, labelId,
                                                    byName, replaceExisting, notes);

                    var row = new JsonObject
                    {
                        ["channel"] = ch,
                        ["boundary_vertices"] = nv,
                        ["samples"] = lp.Count,
                        ["left_name"] = lName,
                        ["right_name"] = rName,
                        ["left_offset_min"] = Round(lMin, 3),
                        ["left_offset_max"] = Round(lMax, 3),
                        ["right_offset_min"] = Round(rMin, 3),
                        ["right_offset_max"] = Round(rMax, 3),
                        ["width_min"] = Round(lMin + rMin, 3),
                        ["width_max"] = Round(lMax + rMax, 3)
                    };
                    if (!lId.IsNull)
                    {
                        var al = tr.GetObject(lId, OpenMode.ForRead) as CivAlign;
                        if (al != null) row["left_length"] = Round(al.Length, 3);
                    }
                    if (!rId.IsNull)
                    {
                        var al = tr.GetObject(rId, OpenMode.ForRead) as CivAlign;
                        if (al != null) row["right_length"] = Round(al.Length, 3);
                    }
                    made.Add(row);

                    if (eraseBoundary)
                    { try { bpl.UpgradeOpen(); bpl.Erase(); } catch (System.Exception) { } }
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["channels"] = made.Count,
                ["interval"] = interval,
                ["items"] = made,
                ["notes"] = notes
            };
        }

        /// <summary>点串 → 临时多段线 → 路线。临时线由 CreateAlignmentFromEntity 的 eraseSource 收走。</summary>
        static ObjectId OfbMakeAlignment(Transaction tr, CivilDoc civ, BlockTableRecord ms, Database db,
            List<Point2d> pts, string name, ObjectId styleId, ObjectId labelId,
            Dictionary<string, CivAlign> byName, bool replaceExisting, JsonArray notes)
        {
            // 同名已存在：改名腾位，建成功才删（别先删后建，失败会赔掉原件）
            CivAlign old = null;
            string parked = null;
            if (byName.ContainsKey(name))
            {
                if (!replaceExisting)
                { notes.Add("偏移路线 " + name + " 已存在，未替换"); return ObjectId.Null; }
                old = byName[name];
                try
                {
                    old.UpgradeOpen();
                    parked = name + "_旧_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                    old.Name = parked;
                }
                catch (System.Exception ex)
                { notes.Add("偏移路线 " + name + " 无法改名腾位：" + ex.Message); return ObjectId.Null; }
            }

            var pl = new Polyline(pts.Count);
            pl.SetDatabaseDefaults(db);
            for (int i = 0; i < pts.Count; i++) pl.AddVertexAt(i, pts[i], 0.0, 0.0, 0.0);
            pl.Closed = false;
            ms.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);

            ObjectId id;
            try
            {
                id = CreateAlignmentFromEntity(tr, civ, name, ObjectId.Null, pl.ObjectId,
                    db.Clayer, styleId, labelId, true, false);
            }
            catch (System.Exception ex)
            {
                try { pl.Erase(); } catch (System.Exception) { }
                if (old != null)
                {
                    try { old.Name = name; }
                    catch (System.Exception) { notes.Add("⚠ 旧路线改不回原名，现名 " + parked); }
                }
                notes.Add("建偏移路线 " + name + " 失败：" + ex.Message);
                return ObjectId.Null;
            }

            if (old != null)
            {
                try { old.Erase(); }
                catch (System.Exception ex)
                { notes.Add("⚠ " + name + " 新的已建，旧的删不掉（现名 " + parked + "）：" + ex.Message); }
            }
            return id;
        }
    }
}
