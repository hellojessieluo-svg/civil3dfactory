using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Aec.PropertyData.DatabaseServices;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;
using CivFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivFlPointType = Autodesk.Civil.FeatureLinePointType;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// 要素线工作法（项目B L4 重构 2026-08-24 定调，记忆 feedback-design-input-draggable-lines）：
    /// 设计真源=带分类特性的要素线（人拉线），自动化=串环+距离场放坡+建TIN（消费线）。
    /// create_feature_lines 把线升级成 FeatureLine（三种高程模式），dredge_from_feature_lines
    /// 按分类把一组线串成环直接出设计面——复用 create_dredge_grading 的成面公共段，零算法复制。
    /// v1 口径：弧段建线时按 densify_step 细分成折线（R459 弧 10m 弦矢高 2.7cm，几何无感）。
    /// </summary>
    public static partial class Ops
    {
        // ==================== create_feature_lines ====================

        static JsonNode CreateFeatureLines(JsonObject a, Document doc)
        {
            var items = a["items"] as JsonArray;
            if (items == null || items.Count == 0)
                throw new InvalidOperationException("items 必需：[{handle,name,z_mode,...}]。");
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var made = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                foreach (JsonNode n in items)
                {
                    var it = (JsonObject)n;
                    string h = Need(it, "handle");
                    string name = Need(it, "name");
                    string zMode = GetString(it, "z_mode", "keep").ToLowerInvariant();
                    double zConst = GetDouble(it, "z", 0.0);
                    string surfName = GetString(it, "surface", null);
                    double step = GetDouble(it, "densify_step", 10.0);
                    string layer = GetString(it, "layer", null);
                    bool eraseSource = GetBool(it, "erase_source", true);

                    var src = tr.GetObject(ResolveHandle(db, h), OpenMode.ForRead) as Curve;
                    if (src == null) throw new InvalidOperationException(h + " 不是曲线。");

                    // 采样：顶点必收，弧段按 densify_step 细分（v1：要素线里弧=细分折线）
                    var pts = new Point3dCollection();
                    var pl = src as Polyline;
                    if (pl != null)
                    {
                        int nv = pl.NumberOfVertices;
                        for (int i = 0; i < nv; i++)
                        {
                            Point3d vp = pl.GetPoint3dAt(i);
                            pts.Add(vp);
                            if (i == nv - 1 && !pl.Closed) break;
                            double bulge = pl.GetBulgeAt(i);
                            if (Math.Abs(bulge) < 1e-9) continue;
                            double d0 = pl.GetDistanceAtParameter(i);
                            double d1 = (i == nv - 1)
                                ? pl.GetDistanceAtParameter(pl.EndParam)
                                : pl.GetDistanceAtParameter(i + 1);
                            int nseg = Math.Max(2, (int)Math.Ceiling((d1 - d0) / Math.Max(step, 0.5)));
                            for (int k = 1; k < nseg; k++)
                                pts.Add(pl.GetPointAtDist(d0 + (d1 - d0) * k / nseg));
                        }
                    }
                    else
                    {
                        // 其他曲线（三维多段线等）：按等距采样，步长 densify_step
                        double L = src.GetDistanceAtParameter(src.EndParam);
                        int nseg = Math.Max(1, (int)Math.Ceiling(L / Math.Max(step, 0.5)));
                        for (int k = 0; k <= nseg; k++)
                            pts.Add(src.GetPointAtDist(Math.Min(L, L * k / nseg)));
                    }

                    // 高程
                    var pts2 = new Point3dCollection();
                    foreach (Point3d p in pts)
                    {
                        double z = zMode == "const" ? zConst
                                 : zMode == "surface" ? 0.0
                                 : p.Z;
                        pts2.Add(new Point3d(p.X, p.Y, z));
                    }

                    var tmp = new Polyline3d(Poly3dType.SimplePoly, pts2, false);
                    btr.AppendEntity(tmp);
                    tr.AddNewlyCreatedDBObject(tmp, true);
                    ObjectId flId = CivFeatureLine.Create(name, tmp.ObjectId);
                    if (!tmp.IsErased) { tmp.UpgradeOpen(); tmp.Erase(); }   // Create 若消费了源就不用再删

                    var fl = (CivFeatureLine)tr.GetObject(flId, OpenMode.ForWrite);
                    if (!string.IsNullOrEmpty(layer))
                    {
                        fl.LayerId = GridEnsureLayer(tr, db, layer, 4);
                    }
                    if (zMode == "surface" || zMode == "cap")
                    {
                        if (string.IsNullOrEmpty(surfName))
                            throw new InvalidOperationException(name + "：z_mode=" + zMode + " 必须给 surface。");
                        ObjectId sfId = FindSurfaceId(tr, civ, surfName);
                        if (sfId.IsNull) throw new InvalidOperationException("找不到曲面 '" + surfName + "'。");
                        fl.AssignElevationsFromSurface(sfId, true);
                        if (zMode == "cap")
                        {
                            // 上限模式：min(地形, z)——接台田的上口线口径
                            var allp = fl.GetPoints(CivFlPointType.AllPoints);
                            for (int i = 0; i < allp.Count; i++)
                                if (allp[i].Z > zConst)
                                    try { fl.SetPointElevation(i, zConst); } catch { }
                        }
                    }
                    if (eraseSource)
                    {
                        var s2 = tr.GetObject(src.ObjectId, OpenMode.ForWrite);
                        if (!s2.IsErased) s2.Erase();
                    }
                    // 建线即挂分类：props={set,values}（集定义须已存在，先跑 property_sets define）
                    var props = it["props"] as JsonObject;
                    if (props != null)
                    {
                        string psName = GetString(props, "set", "疏浚要素");
                        var dict = new DictionaryPropertySetDefinitions(db);
                        if (!dict.Has(psName, tr))
                            throw new InvalidOperationException("特性集 '" + psName + "' 不存在，先用 property_sets define 建。");
                        ObjectId psdId = dict.GetAt(psName);
                        PropertyDataServices.AddPropertySet(fl, psdId);
                        ObjectId psId = PropertyDataServices.GetPropertySet(fl, psdId);
                        var ps = (PropertySet)tr.GetObject(psId, OpenMode.ForWrite);
                        var vals = props["values"] as JsonObject;
                        if (vals != null)
                            foreach (var kv in vals)
                            {
                                int pid = ps.PropertyNameToId(kv.Key);
                                var jv = kv.Value as JsonValue;
                                double dv;
                                if (jv != null && jv.TryGetValue(out dv)) ps.SetAt(pid, dv);
                                else ps.SetAt(pid, kv.Value == null ? "" : kv.Value.ToString());
                            }
                    }
                    made.Add(new JsonObject
                    {
                        ["name"] = name,
                        ["handle"] = fl.Handle.ToString(),
                        ["points"] = fl.PointsCount,
                        ["z_min"] = Math.Round(fl.MinElevation, 3),
                        ["z_max"] = Math.Round(fl.MaxElevation, 3),
                        ["length_2d"] = Math.Round(fl.Length2D, 2)
                    });
                }
                tr.Commit();
            }
            return new JsonObject { ["created"] = made };
        }

        // ==================== dredge_from_feature_lines ====================

        static string FlPsText(Transaction tr, Database db, DBObject obj, string setName, string field)
        {
            try
            {
                var dict = new DictionaryPropertySetDefinitions(db);
                if (!dict.Has(setName, tr)) return null;
                ObjectId psId = PropertyDataServices.GetPropertySet(obj, dict.GetAt(setName));
                var ps = (PropertySet)tr.GetObject(psId, OpenMode.ForRead);
                object v = ps.GetAt(ps.PropertyNameToId(field));
                return v == null ? null : Convert.ToString(v).Trim();
            }
            catch { return null; }
        }

        static double FlPsReal(Transaction tr, Database db, DBObject obj, string setName, string field)
        {
            try
            {
                var dict = new DictionaryPropertySetDefinitions(db);
                if (!dict.Has(setName, tr)) return double.NaN;
                ObjectId psId = PropertyDataServices.GetPropertySet(obj, dict.GetAt(setName));
                var ps = (PropertySet)tr.GetObject(psId, OpenMode.ForRead);
                return Convert.ToDouble(ps.GetAt(ps.PropertyNameToId(field)));
            }
            catch { return double.NaN; }
        }

        /// <summary>要素线折线（AllPoints 直线插值）按步长重采样，保 z 线性。</summary>
        static List<Point3d> DredgeResample(List<Point3d> src, double step)
        {
            var outp = new List<Point3d> { src[0] };
            for (int i = 1; i < src.Count; i++)
            {
                Point3d a = src[i - 1], b = src[i];
                double L = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                int nseg = Math.Max(1, (int)Math.Ceiling(L / Math.Max(step, 0.2)));
                for (int k = 1; k <= nseg; k++)
                {
                    double t = (double)k / nseg;
                    outp.Add(new Point3d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t,
                                         a.Z + (b.Z - a.Z) * t));
                }
            }
            return outp;
        }

        static JsonNode DredgeFromFeatureLines(JsonObject a, Document doc)
        {
            string setName = GetString(a, "set", "疏浚要素");
            string region = GetString(a, "region", null);
            var lineHandles = a["lines"] as JsonArray;
            string terrName = GetString(a, "surface", null);
            double sampleStep = GetDouble(a, "sample_step", 2.0);
            double slopeStep = GetDouble(a, "slope_step", 2.0);
            double flatStep = GetDouble(a, "flat_step", 20.0);
            double chainTol = GetDouble(a, "chain_tol", 0.1);
            bool drawToe = GetBool(a, "draw_toe", true);
            bool drawCrest = GetBool(a, "draw_crest", false);   // 上口线就是要素线本身，缺省不再另画
            string surfLayer = GetString(a, "surface_layer", "C3DF-DREDGE-SURFACE");
            string crestLayer = GetString(a, "crest_layer", "C3DF-DREDGE-TOP");
            string toeLayer = GetString(a, "toe_layer", "C3DF-DREDGE-TOE");

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var res = new JsonObject();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // ---- 收线（句柄白名单 或 按分类扫描分区号）----
                var fls = new List<CivFeatureLine>();
                if (lineHandles != null && lineHandles.Count > 0)
                {
                    foreach (JsonNode hn in lineHandles)
                    {
                        var fl = tr.GetObject(ResolveHandle(db, hn.GetValue<string>()), OpenMode.ForRead) as CivFeatureLine;
                        if (fl == null) throw new InvalidOperationException(hn + " 不是要素线。");
                        fls.Add(fl);
                    }
                }
                else if (!string.IsNullOrEmpty(region))
                {
                    foreach (ObjectId id in ModelSpace(db, tr))
                    {
                        var fl = tr.GetObject(id, OpenMode.ForRead) as CivFeatureLine;
                        if (fl == null) continue;
                        if (string.Equals(FlPsText(tr, db, fl, setName, "分区号"), region,
                                          StringComparison.OrdinalIgnoreCase)) fls.Add(fl);
                    }
                }
                else throw new InvalidOperationException("lines 或 region 至少给一个。");
                if (fls.Count < 2)
                    throw new InvalidOperationException("凑不成环：只找到 " + fls.Count + " 条要素线。");

                // 地形：参数缺省时从线上的〈地形曲面〉分类字段读（跟地形线才需要）
                if (string.IsNullOrEmpty(terrName))
                    foreach (var fl in fls)
                    {
                        string t = FlPsText(tr, db, fl, setName, "地形曲面");
                        if (!string.IsNullOrEmpty(t)) { terrName = t; break; }
                    }
                CivSurface terr = null;
                if (!string.IsNullOrEmpty(terrName))
                {
                    ObjectId sfId = FindSurfaceId(tr, civ, terrName);
                    if (sfId.IsNull) throw new InvalidOperationException("找不到曲面 '" + terrName + "'。");
                    terr = (CivSurface)tr.GetObject(sfId, OpenMode.ForRead);
                }

                // ---- 逐线读分类与采样 ----
                var segs = new List<(CivFeatureLine Fl, string Role, string Mode, double M, List<Point3d> Pts)>();
                var lineRows = new JsonArray();
                foreach (var fl in fls)
                {
                    string role = FlPsText(tr, db, fl, setName, "角色") ?? "";
                    string mode = FlPsText(tr, db, fl, setName, "高程模式") ?? "定值";
                    double m = FlPsReal(tr, db, fl, setName, "坡比m");
                    if (double.IsNaN(m) || m < 0.5 || m > 50)
                        throw new InvalidOperationException("要素线 '" + fl.Name + "' 的〈坡比m〉缺失/出界(0.5~50)。");
                    // 跟地形/上限线：重建时刷新存储高程快照——线上显示的 z、特征线 z、引擎现采 z 三者一致
                    if ((mode.Contains("跟地") || mode.StartsWith("上限")) && terr != null)
                    {
                        try
                        {
                            var flW = (CivFeatureLine)tr.GetObject(fl.ObjectId, OpenMode.ForWrite);
                            flW.AssignElevationsFromSurface(terr.ObjectId, true);
                            double capv;
                            if (mode.StartsWith("上限") &&
                                double.TryParse(mode.Substring(2).Trim(), out capv))
                            {
                                var ap = flW.GetPoints(CivFlPointType.AllPoints);
                                for (int i = 0; i < ap.Count; i++)
                                    if (ap[i].Z > capv)
                                        try { flW.SetPointElevation(i, capv); } catch { }
                            }
                        }
                        catch { }
                    }
                    var raw = new List<Point3d>();
                    foreach (Point3d p in fl.GetPoints(CivFlPointType.AllPoints)) raw.Add(p);
                    if (raw.Count < 2)
                        throw new InvalidOperationException("要素线 '" + fl.Name + "' 点数不足。");
                    segs.Add((fl, role, mode, m, DredgeResample(raw, sampleStep)));
                    lineRows.Add(new JsonObject
                    {
                        ["name"] = fl.Name, ["角色"] = role, ["高程模式"] = mode, ["坡比m"] = m,
                        ["points"] = raw.Count, ["z_min"] = Math.Round(fl.MinElevation, 3),
                        ["z_max"] = Math.Round(fl.MaxElevation, 3)
                    });
                }

                // ---- 串环（端点就近，必要时倒向）。距离一律按平面算：
                //      接头处高程跳变是设计（上口线5.0 接 跟地形线），不是缝 ----
                double D2(Point3d p, Point3d q)
                {
                    double dx = p.X - q.X, dy = p.Y - q.Y;
                    return Math.Sqrt(dx * dx + dy * dy);
                }
                var order = new List<(int Idx, bool Rev)> { (0, false) };
                var used = new HashSet<int> { 0 };
                var gaps = new List<double>();
                while (order.Count < segs.Count)
                {
                    var last = order[order.Count - 1];
                    var lp = last.Rev ? segs[last.Idx].Pts[0] : segs[last.Idx].Pts[segs[last.Idx].Pts.Count - 1];
                    int best = -1; bool bestRev = false; double bestD = double.MaxValue;
                    for (int i = 0; i < segs.Count; i++)
                    {
                        if (used.Contains(i)) continue;
                        double dh = D2(lp, segs[i].Pts[0]);
                        double dt = D2(lp, segs[i].Pts[segs[i].Pts.Count - 1]);
                        if (dh < bestD) { bestD = dh; best = i; bestRev = false; }
                        if (dt < bestD) { bestD = dt; best = i; bestRev = true; }
                    }
                    if (bestD > chainTol)
                        throw new InvalidOperationException(
                            "串环断链：'" + segs[order[order.Count - 1].Idx].Fl.Name + "' 之后最近端点差 " +
                            bestD.ToString("0.###") + "m（容差 " + chainTol + "）。");
                    gaps.Add(bestD);
                    order.Add((best, bestRev));
                    used.Add(best);
                }
                {
                    var first = segs[order[0].Idx].Pts[0];
                    var lastSeg = order[order.Count - 1];
                    var lastP = lastSeg.Rev ? segs[lastSeg.Idx].Pts[0]
                                            : segs[lastSeg.Idx].Pts[segs[lastSeg.Idx].Pts.Count - 1];
                    double dClose = D2(lastP, first);
                    if (dClose > chainTol)
                        throw new InvalidOperationException("环不闭合：尾点到首点差 " + dClose.ToString("0.###") + "m。");
                    gaps.Add(dClose);
                }

                // ---- 底高程：参数 > 线上〈设计底高程〉字段（多线不一致报错） > 口门线最低点 ----
                double bottom;
                if (a["bottom_elev"] != null) bottom = GetDouble(a, "bottom_elev", 0.0);
                else
                {
                    var fieldVals = new List<double>();
                    foreach (var fl in fls)
                    {
                        double b = FlPsReal(tr, db, fl, setName, "设计底高程");
                        if (!double.IsNaN(b) && Math.Abs(b) > 1e-9 &&
                            !fieldVals.Exists(v => Math.Abs(v - b) < 1e-6)) fieldVals.Add(b);
                    }
                    if (fieldVals.Count > 1)
                        throw new InvalidOperationException("各线的〈设计底高程〉不一致：" +
                            string.Join("/", fieldVals) + "——统一了再来。");
                    if (fieldVals.Count == 1) bottom = fieldVals[0];
                    else
                    {
                        double mn = double.MaxValue;
                        foreach (var s in segs)
                            if (s.Role.Contains("口门"))
                                foreach (Point3d p in s.Pts) if (p.Z < mn) mn = p.Z;
                        if (mn == double.MaxValue)
                            throw new InvalidOperationException(
                                "底高程三处都没有：没给 bottom_elev、线上没挂〈设计底高程〉、也没有〈角色〉含「口门」的线。");
                        bottom = mn;
                    }
                }

                // ---- 组环（Z：跟地形现采；M：逐线）----
                var ring = new DredgeRing();
                int followed = 0;
                foreach (var (idx, rev) in order)
                {
                    var s = segs[idx];
                    var ptsSeg = new List<Point3d>(s.Pts);
                    if (rev) ptsSeg.Reverse();
                    bool follow = s.Mode.Contains("跟地");
                    // 「上限X」：接台田口径，顶=min(地形,X)，重建时现采现 clamp
                    double cap = double.NaN;
                    if (s.Mode.StartsWith("上限"))
                    {
                        if (!double.TryParse(s.Mode.Substring(2).Trim(), out cap))
                            throw new InvalidOperationException(
                                "要素线 '" + s.Fl.Name + "' 高程模式 '" + s.Mode + "' 解析不了（写法：上限5.0）。");
                        follow = true;
                    }
                    if (follow && terr == null)
                        throw new InvalidOperationException(
                            "要素线 '" + s.Fl.Name + "' 是" + s.Mode + "模式，但没有地形曲面可采。");
                    for (int i = 0; i < ptsSeg.Count - 1; i++)   // 每段丢尾点（=下段首点）
                    {
                        Point3d p = ptsSeg[i];
                        double z = p.Z;
                        if (follow && terr != null)
                        {
                            double zs;
                            if (GridTrySample(terr, new Point3d(p.X, p.Y, 0), out zs)) { z = zs; followed++; }
                        }
                        if (!double.IsNaN(cap) && z > cap) z = cap;
                        if (z < bottom) z = bottom;
                        ring.P.Add(new Point2d(p.X, p.Y));
                        ring.Z.Add(z);
                        ring.M.Add(s.M);
                    }
                }
                if (ring.Count < 16)
                    throw new InvalidOperationException("环采样点过少（" + ring.Count + "）。");
                ring.Recalc();
                double perim = 0;
                for (int i = 0; i < ring.Count; i++)
                {
                    var q = ring.P[(i + 1) % ring.Count];
                    perim += Math.Sqrt((q.X - ring.P[i].X) * (q.X - ring.P[i].X) +
                                       (q.Y - ring.P[i].Y) * (q.Y - ring.P[i].Y));
                }
                ring.TotalLen = perim;

                // 清本区旧坡顶/坡脚线（C3DF_DREDGE 标记 + 首顶点落在环内），防重建堆积
                int erasedOld = 0;
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var p3 = tr.GetObject(id, OpenMode.ForRead) as Polyline3d;
                    if (p3 == null || p3.IsErased || !DredgeIsTagged(p3)) continue;
                    Point3d fp;
                    try { fp = p3.GetPointAtParameter(p3.StartParam); } catch { continue; }
                    if (!GridPointInPolygon(new Point2d(fp.X, fp.Y), ring.P)) continue;
                    p3.UpgradeOpen();
                    p3.Erase();
                    erasedOld++;
                }

                string sName = GetString(a, "name", "疏浚设计-" + (region ?? segs[0].Fl.Name));
                ObjectId lyS = GridEnsureLayer(tr, db, surfLayer, 7);
                ObjectId lyC = GridEnsureLayer(tr, db, crestLayer, 1);
                ObjectId lyT = GridEnsureLayer(tr, db, toeLayer, 3);
                var btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                EnsureRegApp(tr, db, DredgeAppName);

                DredgeSurfaceOut so = DredgeBuildSurface(tr, db, civ, btr, ring, bottom, sName,
                    lyS, lyC, lyT, drawCrest, drawToe, slopeStep, flatStep);

                // 要素线挂进曲面定义（Definition→Breaklines）：定义自我描述 + 边线几何强制贴合。
                // 注意：内部坡面点是 Edits，别在 GUI 里手点原生 REBUILD（会用新线配旧点）；重建一律走联动/op。
                int breaklines = 0;
                try
                {
                    ObjectId sid2 = FindSurfaceId(tr, civ, so.Surface);
                    var ts2 = (CivTinSurface)tr.GetObject(sid2, OpenMode.ForWrite);
                    var bIds = new ObjectIdCollection();
                    foreach (var fl in fls) bIds.Add(fl.ObjectId);
                    ts2.BreaklinesDefinition.AddStandardBreaklines(bIds, 1.0, 0.0, 0.0, 0.0);
                    breaklines = bIds.Count;
                }
                catch (System.Exception ex)
                { so.Notes.Add("要素线挂特征线失败（曲面本体不受影响）：" + ex.Message); }

                double zMin = double.MaxValue, zMax = double.MinValue;
                foreach (double z in ring.Z) { if (z < zMin) zMin = z; if (z > zMax) zMax = z; }
                double gMax = 0; foreach (double g in gaps) if (g > gMax) gMax = g;

                res["region"] = region;
                res["surface"] = so.Surface;
                res["bottom_elev"] = bottom;
                res["lines"] = lineRows;
                res["chain_gap_max"] = Math.Round(gMax, 4);
                res["ring_points"] = ring.Count;
                res["z_top_min"] = Math.Round(zMin, 3);
                res["z_top_max"] = Math.Round(zMax, 3);
                res["band_max"] = Math.Round(so.BandMax, 2);
                res["terrain"] = terrName;
                res["breaklines"] = breaklines;
                res["erased_old_lines"] = erasedOld;
                res["terrain_followed_points"] = followed;
                res["toe_points"] = so.ToePoints;
                res["grid_points"] = so.GridPoints;
                res["vertices"] = so.Vertices;
                var notes = new JsonArray();
                foreach (string nte in so.Notes) notes.Add((JsonNode)nte);
                res["warnings"] = notes;
                tr.Commit();
            }
            return res;
        }
    }
}
