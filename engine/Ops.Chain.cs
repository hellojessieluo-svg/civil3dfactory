using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivAlignmentType = Autodesk.Civil.DatabaseServices.AlignmentType;
using CivAssembly = Autodesk.Civil.DatabaseServices.Assembly;
using CivCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using CivCorridorSurface = Autodesk.Civil.DatabaseServices.CorridorSurface;
using CivMaterialItemType = Autodesk.Civil.DatabaseServices.MaterialItemType;
using CivProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivProfileType = Autodesk.Civil.DatabaseServices.ProfileType;
using CivQtoCriteria = Autodesk.Civil.DatabaseServices.Styles.QuantityTakeoffCriteria;
using CivQtoMapping = Autodesk.Civil.DatabaseServices.QTOCriteriaNameMapping;
using CivQtoMaterialList = Autodesk.Civil.DatabaseServices.QTOMaterialList;
using CivQtoSectionalResult = Autodesk.Civil.DatabaseServices.QTOSectionalResult;
using CivSampleLine = Autodesk.Civil.DatabaseServices.SampleLine;
using CivSampleLineGroup = Autodesk.Civil.DatabaseServices.SampleLineGroup;
using CivSectionSource = Autodesk.Civil.DatabaseServices.SectionSource;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivTargetInfo = Autodesk.Civil.DatabaseServices.SubassemblyTargetInfo;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;
using CivVolumeMethod = Autodesk.Civil.DatabaseServices.MaterialVolumeCalculationMethodType;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivConnectedParams = Autodesk.Civil.DatabaseServices.ConnectedAlignmentParams;
using CivCurveGroupType = Autodesk.Civil.CurbReturnCurveGroupType;

namespace Civil3DFactory
{
    /// <summary>
    /// 「一条直线 → 路线 → 偏移 → 纵断面 → 走廊 → 道路曲面 → 采样线 → 工程量」整条链。
    ///
    /// 全部操作直接改 /i 传入的**内存中的**图纸数据库，磁盘上的原文件不动；
    /// 要留下成果必须显式跑 save_dwg（缺省另存新文件，写回原图须 apply:true）。
    ///
    /// 计算逻辑移植自已实战验证的 RiverQto（Civil3D-009），并保留它踩过的坑：
    ///   · 建采样线组前清空本路线所有旧组，否则算材质报 "should have been sampled"；
    ///   · 采样源标记必须与建组同事务，提交后再标记无效；
    ///   · 准则的曲面槽位名从准则本身读，不能写死（写死会静默失配 → 工程量全 0）。
    /// </summary>
    public static partial class Ops
    {
        // ===================== 1. 原地形曲面（造一块地形，链路才自足）=====================

        static JsonNode CreateSurfaceGrid(JsonObject a, Document doc)
        {
            string name = Need(a, "name");
            double minx = GetDouble(a, "minx", 0), miny = GetDouble(a, "miny", 0);
            double maxx = GetDouble(a, "maxx", 0), maxy = GetDouble(a, "maxy", 0);
            if (maxx <= minx || maxy <= miny)
                throw new InvalidOperationException("需要 minx/miny/maxx/maxy，且 max 必须大于 min。");
            double step = GetDouble(a, "step", 20);
            if (step <= 0) throw new InvalidOperationException("step 必须大于 0。");
            double elev = GetDouble(a, "elev", 0);
            double slopeX = GetDouble(a, "slope_x", 0);   // 每米高差
            double slopeY = GetDouble(a, "slope_y", 0);
            string style = GetString(a, "style", null);

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            // 覆盖重建：先删同名曲面（单独事务提交，避免同事务删了又建导致名字未释放）
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId old = FindSurfaceId(tr, civ, name);
                if (!old.IsNull)
                {
                    var s = (CivSurface)tr.GetObject(old, OpenMode.ForWrite);
                    s.Erase();
                }
                tr.Commit();
            }

            int nx = 0, ny = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId styleId = FindStyleId(tr, civ.Styles.SurfaceStyles, style);
                ObjectId sid = CivTinSurface.Create(db, name);
                var ts = (CivTinSurface)tr.GetObject(sid, OpenMode.ForWrite);
                if (!styleId.IsNull) ts.StyleId = styleId;

                var pts = new Point3dCollection();
                for (double x = minx; x <= maxx + 1e-9; x += step)
                {
                    nx++;
                    ny = 0;
                    for (double y = miny; y <= maxy + 1e-9; y += step)
                    {
                        ny++;
                        pts.Add(new Point3d(x, y, elev + slopeX * (x - minx) + slopeY * (y - miny)));
                    }
                }
                ts.AddVertices(pts);
                tr.Commit();

                return new JsonObject
                {
                    ["surface"] = name,
                    ["vertices"] = nx * ny,
                    ["grid"] = nx + " × " + ny,
                    ["extent"] = string.Format("({0},{1}) - ({2},{3})", minx, miny, maxx, maxy),
                    ["elev_range"] = Math.Round(elev, 3) + " ~ " +
                                     Math.Round(elev + slopeX * (maxx - minx) + slopeY * (maxy - miny), 3)
                };
            }
        }

        // ===================== 2. 画线 → 定义为路线 =====================

        static JsonNode CreateAlignment(JsonObject a, Document doc)
        {
            string name = Need(a, "name");
            string handle = GetString(a, "handle", null);
            var ptsArr = a["points"] as JsonArray;
            if (string.IsNullOrEmpty(handle) && (ptsArr == null || ptsArr.Count < 2))
                throw new InvalidOperationException("要么给 points:[[x,y],[x,y],...]（现画一条），要么给 handle（图中已有的线）。");

            string layer = GetString(a, "layer", "0");
            string style = GetString(a, "style", null);
            string labelSet = GetString(a, "label_set", null);
            string site = GetString(a, "site", null);
            bool eraseSource = GetBool(a, "erase_source", true);      // 转成路线后删掉那条辅助线
            bool addCurves = GetBool(a, "add_curves", false);

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            // 覆盖重建：先删同名路线
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EraseAlignments(tr, civ, name);
                tr.Commit();
            }

            string drawnFrom;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId plId;
                if (!string.IsNullOrEmpty(handle))
                {
                    plId = ResolveHandle(db, handle);
                    drawnFrom = "图中已有对象 " + handle;
                }
                else
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    var pl = new Polyline();
                    int i = 0;
                    foreach (JsonNode pn in ptsArr)
                    {
                        var pair = pn as JsonArray;
                        if (pair == null || pair.Count < 2)
                            throw new InvalidOperationException("points 里每项必须是 [x, y]。");
                        pl.AddVertexAt(i++, new Point2d(
                            pair[0].GetValue<double>(), pair[1].GetValue<double>()), 0, 0, 0);
                    }
                    ms.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                    if (!string.IsNullOrEmpty(layer) && layer != "0") pl.Layer = layer;   // 入库后才能设图层
                    plId = pl.ObjectId;
                    drawnFrom = "现画多段线 " + i + " 点，长 " + Math.Round(pl.Length, 3) + " m";
                }

                ObjectId siteId = ObjectId.Null;
                if (!string.IsNullOrEmpty(site))
                {
                    foreach (ObjectId s in civ.GetSiteIds())
                        if (TryGetName(tr.GetObject(s, OpenMode.ForRead)) == site) { siteId = s; break; }
                    if (siteId.IsNull) throw new InvalidOperationException("找不到场地 '" + site + "'。");
                }

                ObjectId styleId = FindStyleId(tr, civ.Styles.AlignmentStyles, style);
                ObjectId labelId = FindStyleId(tr, civ.Styles.LabelSetStyles.AlignmentLabelSetStyles, labelSet);

                ObjectId alId = CreateAlignmentFromEntity(
                    tr, civ, name, siteId, plId, db.Clayer, styleId, labelId, eraseSource, addCurves);

                var al = (CivAlignment)tr.GetObject(alId, OpenMode.ForRead);
                var res = new JsonObject
                {
                    ["alignment"] = al.Name,
                    ["from"] = drawnFrom,
                    ["length"] = Math.Round(al.Length, 3),
                    ["start_station"] = Math.Round(al.StartingStation, 3),
                    ["end_station"] = Math.Round(al.EndingStation, 3),
                    ["handle"] = al.Handle.ToString(),
                    ["source_erased"] = eraseSource
                };
                tr.Commit();
                return res;
            }
        }

        // ===================== 3. 偏移路线 =====================

        static JsonNode OffsetAlignment(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            double dist = GetDouble(a, "distance", 15);
            string style = GetString(a, "style", null);

            // offsets 显式给就用它；否则按 ±distance 左右各一条（名字带 _左/_右 前缀，
            // 走廊那步就是按这个前缀找偏移目标的，别改）。
            // 每项可以是数（整条偏移），也可以是对象 {distance, start_station, end_station, name?}
            // ——带父路线桩号区间即"分段偏移"，交叉口范围让位给连接路线（create_connected_alignment）。
            var pairs = new List<(string Name, double Dist, double? S0, double? S1)>();
            var arr = a["offsets"] as JsonArray;
            if (arr != null && arr.Count > 0)
            {
                foreach (JsonNode n in arr)
                {
                    if (n is JsonObject o)
                    {
                        JsonNode dn = o["distance"];
                        if (dn == null) throw new InvalidOperationException("分段偏移对象必须给 distance。");
                        double d = dn.GetValue<double>();
                        double? s0 = o["start_station"] != null ? o["start_station"].GetValue<double>() : (double?)null;
                        double? s1 = o["end_station"] != null ? o["end_station"].GetValue<double>() : (double?)null;
                        if (s0.HasValue != s1.HasValue)
                            throw new InvalidOperationException("start_station 与 end_station 要么都给要么都不给。");
                        string side = d < 0 ? "左" : "右";
                        string nm = GetString(o, "name", null);
                        if (string.IsNullOrEmpty(nm))
                            nm = alName + "_" + side + Math.Abs(d) + "m"
                               + (s0.HasValue ? "_" + Math.Round(s0.Value) + "-" + Math.Round(s1.Value) : "");
                        pairs.Add((nm, d, s0, s1));
                    }
                    else
                    {
                        double d = n.GetValue<double>();
                        string side = d < 0 ? "左" : "右";
                        pairs.Add((alName + "_" + side + Math.Abs(d) + "m", d, null, null));
                    }
                }
            }
            else
            {
                if (dist <= 0) throw new InvalidOperationException("distance 必须大于 0。");
                pairs.Add((alName + "_左" + dist + "m", -dist, null, null));
                pairs.Add((alName + "_右" + dist + "m", dist, null, null));
            }

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (FindAlignment(tr, civ, alName) == null)
                    throw new InvalidOperationException("找不到路线 '" + alName + "'。");
                var names = new List<string>();
                foreach (var kv in pairs) names.Add(kv.Name);
                EraseAlignments(tr, civ, names.ToArray());
                tr.Commit();
            }

            var made = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment parent = FindAlignment(tr, civ, alName);
                ObjectId styleId = FindStyleId(tr, civ.Styles.AlignmentStyles, style);
                foreach (var kv in pairs)
                {
                    if (kv.S0.HasValue)
                        CivAlignment.CreateOffsetAlignment(kv.Name, parent.ObjectId, kv.Dist, styleId,
                                                           kv.S0.Value, kv.S1.Value);
                    else
                        CivAlignment.CreateOffsetAlignment(kv.Name, parent.ObjectId, kv.Dist, styleId);
                    var item = new JsonObject { ["name"] = kv.Name, ["offset"] = kv.Dist };
                    if (kv.S0.HasValue) { item["start_station"] = kv.S0.Value; item["end_station"] = kv.S1.Value; }
                    made.Add(item);
                }
                tr.Commit();
            }
            return new JsonObject { ["parent"] = alName, ["created"] = made };
        }

        // ===================== 3b. 连接路线：两条路线间按半径转角（交叉口） =====================
        //
        // 原生 CreateConnectedAlignment：进线/出线各给一个连接桩号 + 半径，生成动态连接路线，
        // 父路线（含动态偏移路线）改动后转角自动跟随。CurveGroupType 固定用 Arc（单圆弧）。
        // 分段偏移（offset_alignment 的 start/end_station）在交叉口让出的缺口正是给它接的。

        static JsonNode CreateConnectedAlignmentOp(JsonObject a, Document doc)
        {
            string name = Need(a, "name");
            string inName = Need(a, "in_alignment");
            string outName = Need(a, "out_alignment");
            JsonNode inNode = a["in_station"], outNode = a["out_station"];
            if (inNode == null || outNode == null)
                throw new InvalidOperationException("需要 in_station / out_station（两条线上的连接桩号）。");
            double inSta = inNode.GetValue<double>();
            double outSta = outNode.GetValue<double>();
            double radius = GetDouble(a, "radius", 20);
            if (radius <= 0) throw new InvalidOperationException("radius 必须大于 0。");
            string style = GetString(a, "style", null);
            string labelSet = GetString(a, "label_set", null);
            bool big = GetBool(a, "greater_than_180", false);

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EraseAlignments(tr, civ, name);
                tr.Commit();
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment ain = FindAlignment(tr, civ, inName);
                if (ain == null) throw new InvalidOperationException("找不到进线 '" + inName + "'。");
                CivAlignment aout = FindAlignment(tr, civ, outName);
                if (aout == null) throw new InvalidOperationException("找不到出线 '" + outName + "'。");
                ObjectId styleId = FindStyleId(tr, civ.Styles.AlignmentStyles, style);
                ObjectId labelId = FindStyleId(tr, civ.Styles.LabelSetStyles.AlignmentLabelSetStyles, labelSet);

                var p = new CivConnectedParams
                {
                    IncomingParentAlignmentId = ain.ObjectId,
                    IncomingParentAlignmentStation = inSta,
                    OutgoingParentAlignmentId = aout.ObjectId,
                    OutgoingParentAlignmentStation = outSta,
                    CurveRadius = radius,
                    CurveGroupType = CivCurveGroupType.Arc,
                    GreaterThan180 = big,
                    OffsetIn = GetDouble(a, "offset_in", 0),
                    OffsetOut = GetDouble(a, "offset_out", 0),
                    // API 强制 >0：连接路线沿两条母线各"搭"一小段再起弧。默认压到最小，
                    // 让连接路线基本就是转角弧本身。
                    ConnectionOverlapLengthIn = GetDouble(a, "overlap_in", 0.01),
                    ConnectionOverlapLengthOut = GetDouble(a, "overlap_out", 0.01)
                };

                // 求解器的坑（2026-08-23 项目B实测钉死）：
                //  ① 进/出顺序挑剔，每个角只有部分流向可解；
                //  ② OffsetIn/Out 的符号随求解器选择的通行方向翻转，光靠参数钉不住象限——
                //     同一批里 +15 有时落右侧有时落左侧，弧还可能整体跑到别的角。
                // 唯一可靠做法：穷举 8 种组合（顺序×两侧符号），建成后用 StationOffset 几何自验
                // （两端点必须真切在两条母线的**意图侧** ±15 上、且在提示桩号附近），验不过就删掉换下一种。
                double offIn = p.OffsetIn, offOut = p.OffsetOut;
                double tol = GetDouble(a, "verify_tol", 0.5);
                double staTol = GetDouble(a, "verify_station_window", 80);
                // 陆侧判据（第 6 坑）：同一对偏移线交点四周有 4 个可行切弧，侧别+桩号钉不住凸向，
                // 弧可能凸进水道。切点必须离**另一条**母线也 ≥W（在台田一侧）才算对。
                double landMin = GetDouble(a, "land_min",
                    Math.Min(Math.Abs(offIn), Math.Abs(offOut)) - 0.5);

                bool VerifyEnd(Point3d pt, CivAlignment parent, double wantOff, double hintSta)
                {
                    double sta = 0, off = 0;
                    try { parent.StationOffset(pt.X, pt.Y, ref sta, ref off); }
                    catch { return false; }
                    return Math.Abs(off - wantOff) <= tol && Math.Abs(sta - hintSta) <= staTol;
                }

                bool LandSide(Point3d pt, CivAlignment other)
                {
                    double sta = 0, off = 0;
                    try { other.StationOffset(pt.X, pt.Y, ref sta, ref off); }
                    catch { return true; }   // 超出对方桩号范围＝离得远，天然在陆侧
                    return Math.Abs(off) >= landMin;
                }

                // 弓向判据（第 7 坑）：同一对切点间有正弓/反弓两条弧，端点完全相同，
                // 端点判据分不出——弧中点必须也在陆侧（离带偏移的父线 ≥ landMin）。
                bool MidLand(CivAlignment cand)
                {
                    if (landMin <= 0) return true;
                    double ms = (cand.StartingStation + cand.EndingStation) / 2;
                    double e = 0, n = 0;
                    cand.PointLocation(ms, 0, ref e, ref n);
                    var mid = new Point3d(e, n, 0);
                    if (Math.Abs(offIn) >= 1 && !LandSide(mid, ain)) return false;
                    if (Math.Abs(offOut) >= 1 && !LandSide(mid, aout)) return false;
                    return true;
                }

                ObjectId id = ObjectId.Null;
                string combo = null;
                var attempts = new JsonArray();
                foreach (bool swap in new[] { false, true })
                foreach (double sIn in new[] { offIn, -offIn })
                foreach (double sOut in new[] { offOut, -offOut })
                {
                    CivAlignment pin = swap ? aout : ain, pout = swap ? ain : aout;
                    p.IncomingParentAlignmentId = pin.ObjectId;
                    p.OutgoingParentAlignmentId = pout.ObjectId;
                    p.IncomingParentAlignmentStation = swap ? outSta : inSta;
                    p.OutgoingParentAlignmentStation = swap ? inSta : outSta;
                    p.OffsetIn = swap ? sOut : sIn;
                    p.OffsetOut = swap ? sIn : sOut;
                    string tag = (swap ? "换序" : "原序") + $" in{p.OffsetIn:+0;-0} out{p.OffsetOut:+0;-0}";
                    ObjectId tryId;
                    try
                    {
                        tryId = CivAlignment.CreateConnectedAlignment(
                            name, ObjectId.Null, db.Clayer, styleId, labelId, p);
                    }
                    catch (System.Exception ex)
                    {
                        attempts.Add(tag + " 建失败:" + ex.Message);
                        continue;
                    }
                    var cand = (CivAlignment)tr.GetObject(tryId, OpenMode.ForRead);
                    double e0 = 0, n0 = 0, e1 = 0, n1 = 0;
                    cand.PointLocation(cand.StartingStation, 0, ref e0, ref n0);
                    cand.PointLocation(cand.EndingStation, 0, ref e1, ref n1);
                    var p0 = new Point3d(e0, n0, 0);
                    var p1 = new Point3d(e1, n1, 0);
                    // 意图：一端切在 ain 的 offIn 侧、另一端切在 aout 的 offOut 侧（两种端点分配都认），
                    // 且两个切点都在陆侧（离另一条母线 ≥ landMin，弧不许凸进水道）。
                    bool okGeom =
                        ((VerifyEnd(p0, ain, offIn, inSta) && VerifyEnd(p1, aout, offOut, outSta)
                          && LandSide(p0, aout) && LandSide(p1, ain)) ||
                         (VerifyEnd(p1, ain, offIn, inSta) && VerifyEnd(p0, aout, offOut, outSta)
                          && LandSide(p1, aout) && LandSide(p0, ain)))
                        && MidLand(cand);
                    if (okGeom && cand.Length > 0)
                    {
                        id = tryId;
                        combo = tag;
                        break;
                    }
                    attempts.Add(tag + $" 建成但验几何不过(len={Math.Round(cand.Length, 1)})");
                    var kill = (CivAlignment)tr.GetObject(tryId, OpenMode.ForWrite);
                    kill.Erase();
                }
                if (id.IsNull)
                    throw new InvalidOperationException(
                        "连接路线 '" + name + "' 8 种组合全部失败或验几何不过：" + attempts.ToJsonString());

                var al = (CivAlignment)tr.GetObject(id, OpenMode.ForRead);

                // 回报实测切点：两端点分别对两条母线做 StationOffset（Civil 带号：负=左）。
                // 下游用这个当真值重排偏移分段——比在 DXF 里猜多段线方向可靠。
                JsonObject Measure(double sta0)
                {
                    double e = 0, n = 0;
                    al.PointLocation(sta0, 0, ref e, ref n);
                    var m = new JsonObject();
                    foreach (var (tag, parent) in new[] { ("on_in", ain), ("on_out", aout) })
                    {
                        double s = 0, off = 0;
                        try
                        {
                            parent.StationOffset(e, n, ref s, ref off);
                            m[tag] = new JsonObject { ["station"] = Math.Round(s, 3), ["offset"] = Math.Round(off, 3) };
                        }
                        catch { m[tag] = null; }
                    }
                    return m;
                }

                var res = new JsonObject
                {
                    ["alignment"] = al.Name,
                    ["handle"] = al.Handle.ToString(),
                    ["length"] = Math.Round(al.Length, 3),
                    ["radius"] = radius,
                    ["in"] = inName,
                    ["in_station"] = inSta,
                    ["out"] = outName,
                    ["out_station"] = outSta,
                    ["combo"] = combo,
                    ["attempts_before_success"] = attempts.Count,
                    ["geometry_verified"] = true,
                    ["start_end_measurements"] = new JsonObject
                    {
                        ["start"] = Measure(al.StartingStation),
                        ["end"] = Measure(al.EndingStation)
                    }
                };
                tr.Commit();
                return res;
            }
        }

        // ===================== 4. 纵断面：地面线 + 平坡设计线 =====================

        static JsonNode CreateProfiles(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string sfName = Need(a, "surface");
            double designElev = GetDouble(a, "design_elev", 0);
            string groundStyle = GetString(a, "ground_style", null);
            string designStyle = GetString(a, "design_style", null);
            string labelSet = GetString(a, "label_set", null);

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            string groundName = alName + "_地面线";
            string designName = alName + "_设计线";

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EraseProfiles(tr, civ, groundName, designName);
                tr.Commit();
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = FindAlignment(tr, civ, alName);
                if (al == null) throw new InvalidOperationException("找不到路线 '" + alName + "'。");
                ObjectId sfId = FindSurfaceId(tr, civ, sfName);
                if (sfId.IsNull) throw new InvalidOperationException("找不到曲面 '" + sfName + "'。");

                ObjectId gStyle = FindStyleId(tr, civ.Styles.ProfileStyles, groundStyle);
                ObjectId dStyle = FindStyleId(tr, civ.Styles.ProfileStyles, designStyle);
                ObjectId labelId = FindStyleId(tr, civ.Styles.LabelSetStyles.ProfileLabelSetStyles, labelSet);

                CivProfile.CreateFromSurface(groundName, al.ObjectId, sfId, db.Clayer, gStyle, labelId);

                ObjectId dId = CivProfile.CreateByLayout(designName, al.ObjectId, db.Clayer, dStyle, labelId);
                var design = (CivProfile)tr.GetObject(dId, OpenMode.ForWrite);
                design.PVIs.AddPVI(al.StartingStation, designElev);
                design.PVIs.AddPVI(al.EndingStation, designElev);

                var res = new JsonObject
                {
                    ["ground_profile"] = groundName,
                    ["design_profile"] = designName,
                    ["design_elev"] = designElev,
                    ["from_surface"] = sfName
                };
                tr.Commit();
                return res;
            }
        }

        // ===================== 5. 走廊（建 + 设目标）=====================

        static JsonNode CreateCorridor(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string asmName = Need(a, "assembly");
            string sfName = Need(a, "surface");
            string baseline = GetString(a, "baseline", "基准线");
            string region = GetString(a, "region", "区域1");
            string corridorName = GetString(a, "name", alName + "_走廊");

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EraseCorridors(tr, db, corridorName);
                tr.Commit();
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = FindAlignment(tr, civ, alName);
                if (al == null) throw new InvalidOperationException("找不到路线 '" + alName + "'。");

                ObjectId fgId = ObjectId.Null;
                foreach (ObjectId pid in al.GetProfileIds())
                {
                    var p = (CivProfile)tr.GetObject(pid, OpenMode.ForRead);
                    if (p.ProfileType == CivProfileType.FG) { fgId = pid; break; }
                }
                if (fgId.IsNull)
                    throw new InvalidOperationException("路线 '" + alName + "' 没有设计纵断面，先跑 create_profiles。");

                ObjectId sfId = FindSurfaceId(tr, civ, sfName);
                if (sfId.IsNull) throw new InvalidOperationException("找不到曲面 '" + sfName + "'。");

                ObjectId asmId = ObjectId.Null;
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var asm = tr.GetObject(id, OpenMode.ForRead) as CivAssembly;
                    if (asm != null && asm.Name == asmName) { asmId = id; break; }
                }
                if (asmId.IsNull) throw new InvalidOperationException("图中没有装配 '" + asmName + "'（用 civil_env 查名称）。");

                // 左右偏移路线：按名前缀找，不依赖偏移距离
                ObjectId leftId = ObjectId.Null, rightId = ObjectId.Null;
                foreach (ObjectId aid in civ.GetAlignmentIds())
                {
                    var x = (CivAlignment)tr.GetObject(aid, OpenMode.ForRead);
                    if (x.Name.StartsWith(alName + "_左")) leftId = aid;
                    else if (x.Name.StartsWith(alName + "_右")) rightId = aid;
                }

                ObjectId corridorId = civ.CorridorCollection.Add(
                    corridorName, baseline, al.ObjectId, fgId, region, asmId);
                var corridor = (CivCorridor)tr.GetObject(corridorId, OpenMode.ForWrite);
                corridor.Rebuild();

                // 设目标：曲面槽 → 原地形；偏移槽 → 左右偏移路线
                var targets = corridor.GetTargets();
                var sfIds = new ObjectIdCollection { sfId };
                var offIds = new ObjectIdCollection();
                if (!leftId.IsNull) offIds.Add(leftId);
                if (!rightId.IsNull) offIds.Add(rightId);

                int sCount = 0, oCount = 0;
                var slots = new JsonArray();
                foreach (CivTargetInfo t in targets)
                {
                    string tt = t.TargetType.ToString();
                    slots.Add(t.DisplayName + " [" + tt + "]");
                    if (tt == "Surface") { t.TargetIds = sfIds; sCount++; }
                    else if (tt == "Offset" && offIds.Count > 0) { t.TargetIds = offIds; oCount++; }
                }
                corridor.SetTargets(targets);
                corridor.Rebuild();

                var codes = new JsonArray();
                foreach (string c in corridor.GetLinkCodes()) codes.Add(c);

                var res = new JsonObject
                {
                    ["corridor"] = corridorName,
                    ["assembly"] = asmName,
                    ["surface_targets_set"] = sCount,
                    ["offset_targets_set"] = oCount,
                    ["offset_alignments"] = offIds.Count,
                    ["target_slots"] = slots,
                    ["link_codes"] = codes
                };
                tr.Commit();
                return res;
            }
        }

        // ===================== 6. 道路曲面 =====================

        static JsonNode CreateCorridorSurface(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string corridorName = GetString(a, "corridor", alName + "_走廊");
            string surfName = GetString(a, "name", alName + "_道路曲面");
            bool boundary = GetBool(a, "boundary", true);

            var wanted = new List<string>();
            var arr = a["link_codes"] as JsonArray;
            if (arr != null) foreach (JsonNode n in arr) wanted.Add(n.GetValue<string>());

            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivCorridor corridor = FindCorridor(tr, db, corridorName);
                if (corridor == null)
                    throw new InvalidOperationException("找不到走廊 '" + corridorName + "'，先跑 create_corridor。");

                var valid = new List<string>(corridor.GetLinkCodes());

                foreach (CivCorridorSurface ex in corridor.CorridorSurfaces)
                    if (ex.Name == surfName) { corridor.CorridorSurfaces.Remove(ex); break; }

                var cs = corridor.CorridorSurfaces.Add(surfName);
                var added = new JsonArray();
                var skipped = new JsonArray();

                if (valid.Count > 0)
                {
                    if (wanted.Count == 0)
                    {
                        foreach (string c in valid)
                            if (c.IndexOf("Top", StringComparison.OrdinalIgnoreCase) >= 0) wanted.Add(c);
                        if (wanted.Count == 0) wanted.AddRange(valid);
                    }

                    foreach (string code in wanted)
                    {
                        if (valid.Contains(code)) { cs.AddLinkCode(code, true); added.Add(code); }
                        else skipped.Add(code);
                    }
                }
                if (valid.Count > 0 && added.Count == 0)
                {
                    var avail = new JsonArray();
                    foreach (string c in valid) avail.Add(c);
                    throw new InvalidOperationException(
                        "一个链接代码都没加上。该走廊可用代码: " + avail.ToJsonString());
                }

                if (boundary) cs.Boundaries.AddCorridorExtentsBoundary(surfName + "_外边界");
                corridor.Rebuild();

                var res = new JsonObject
                {
                    ["corridor_surface"] = surfName,
                    ["corridor"] = corridorName,
                    ["link_codes_added"] = added,
                    ["link_codes_skipped"] = skipped,
                    ["boundary"] = boundary
                };
                tr.Commit();
                return res;
            }
        }

        // ===================== 7. 采样线组 =====================

        static void TraceSampleLineStage(string stage, string detail = "")
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("C3DF_AUDIT_LOG");
                if (string.IsNullOrWhiteSpace(path)) return;
                var row = new JsonObject
                {
                    ["at"] = DateTimeOffset.Now.ToString("O"),
                    ["event"] = "create_sample_lines_stage",
                    ["stage"] = stage,
                    ["detail"] = detail
                };
                File.AppendAllText(path, row.ToJsonString() + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        static JsonNode CreateSampleLines(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string sfName = Need(a, "surface");
            double interval = GetDouble(a, "interval", 50);
            double swath = GetDouble(a, "swath", 50);
            string style = GetString(a, "style", null);
            string corridorName = GetString(a, "corridor", alName + "_走廊");
            string roadSurf = GetString(a, "road_surface", alName + "_道路曲面");
            bool clearExisting = GetBool(a, "clear_existing", true);

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            string groupName = alName + "_采样线组";
            JsonObject res;

            // 建组前清空本路线**所有**旧采样线组：残组会让算材质取错组，
            // 报 "mappedSurface should have been sampled"（RiverQto 踩坑 #3，走过大弯路）
            int erased = 0;
            if (clearExisting)
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = FindAlignment(tr, civ, alName);
                if (al == null) throw new InvalidOperationException("找不到路线 '" + alName + "'。");
                foreach (ObjectId gid in al.GetSampleLineGroupIds())
                {
                    var g = (CivSampleLineGroup)tr.GetObject(gid, OpenMode.ForWrite);
                    g.Erase();
                    erased++;
                }
                tr.Commit();
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = FindAlignment(tr, civ, alName);
                if (al == null) throw new InvalidOperationException("找不到路线 '" + alName + "'。");

                ObjectId gid2 = CivSampleLineGroup.Create(groupName, al.ObjectId);
                var group = (CivSampleLineGroup)tr.GetObject(gid2, OpenMode.ForWrite);
                TraceSampleLineStage("group_created", groupName);

                var sampled = new JsonArray();
                var notSampled = new JsonArray();

                // 两种来源二选一：显式 lines（每条给端点坐标，堤埝等按图上断面线建线的场景），
                // 或默认按 interval/swath 等间距生成。创建、设样式、标记采样源全走同一条路径。
                var linesArr = a["lines"] as JsonArray;
                int createdCount = 0;
                string creationMode;
                if (linesArr != null && linesArr.Count > 0)
                {
                    creationMode = "explicit_lines";
                    foreach (JsonNode ln in linesArr)
                    {
                        var lo = ln as JsonObject;
                        var ptsA = lo == null ? null : lo["points"] as JsonArray;
                        if (ptsA == null || ptsA.Count < 2)
                            throw new InvalidOperationException("lines 每项必须含 points:[[x,y],[x,y],...]（至少两点）。");
                        var coll = new Point2dCollection();
                        foreach (JsonNode p in ptsA)
                        {
                            var pair = p as JsonArray;
                            if (pair == null || pair.Count < 2)
                                throw new InvalidOperationException("lines.points 每项必须是 [x, y]。");
                            coll.Add(new Point2d(pair[0].GetValue<double>(), pair[1].GetValue<double>()));
                        }
                        string slName = GetString(lo, "name", alName + "_SL" + (createdCount + 1));
                        TraceSampleLineStage("polyline_create_begin", slName);
                        CivSampleLine.Create(slName, gid2, coll);
                        TraceSampleLineStage("polyline_create_end", slName);
                        createdCount++;
                    }
                }
                else
                {
                    creationMode = "interval";
                    double start = al.StartingStation, end = al.EndingStation;
                    var stations = new List<double>();
                    for (double st = start; st < end - 0.001; st += interval) stations.Add(st);
                    if (stations.Count == 0 || end - stations[stations.Count - 1] > 0.5) stations.Add(end);

                    // 沿用旧操作台已验证路径：先按左右端点创建全部采样线，
                    // 再设置采样源；不改命令默认值、不设 Dynamic、不额外 Rebuild。
                    foreach (double st in stations)
                    {
                        double xL = 0, yL = 0, xR = 0, yR = 0;
                        al.PointLocation(st, -swath, ref xL, ref yL);
                        al.PointLocation(st, swath, ref xR, ref yR);
                        TraceSampleLineStage("polyline_create_begin", st.ToString("F3"));
                        CivSampleLine.Create(alName + "_SL-" + st.ToString("F0"), gid2,
                            new Point2dCollection
                            {
                                new Point2d(xL, yL),
                                new Point2d(xR, yR)
                            });
                        TraceSampleLineStage("polyline_create_end", st.ToString("F3"));
                        createdCount++;
                    }
                }

                ObjectId slStyle = FindStyleId(tr, civ.Styles.SampleLineStyles, style);
                if (!slStyle.IsNull)
                    foreach (ObjectId sid in group.GetSampleLineIds())
                        ((CivSampleLine)tr.GetObject(sid, OpenMode.ForWrite)).StyleId = slStyle;

                // 与旧操作台一致：线建完后，只设置 IsSampled。
                foreach (CivSectionSource src in group.GetSectionSources())
                {
                    string st = "";
                    try { st = src.SourceType.ToString(); } catch { }
                    bool isCorridorSurf =
                        st.IndexOf("CorridorSurface", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool minesToo = src.SourceNameOf().StartsWith(corridorName + " ")
                                 || src.SourceNameOf().Contains(roadSurf);
                    bool keep = src.SourceNameOf() == sfName
                             || src.SourceNameOf() == corridorName
                             || (isCorridorSurf && minesToo);
                    src.IsSampled = keep;
                    string tag = src.SourceNameOf() + "  [" + st + "]";
                    if (keep) sampled.Add(tag); else notSampled.Add(tag);
                }

                res = new JsonObject
                {
                    ["group"] = groupName,
                    ["old_groups_erased"] = erased,
                    ["clear_existing"] = clearExisting,
                    ["sample_lines"] = createdCount,
                    ["interval"] = interval,
                    ["swath"] = swath,
                    ["creation_mode"] = creationMode,
                    ["sampled_sources"] = sampled,
                    ["ignored_sources"] = notSampled
                };
                TraceSampleLineStage("first_transaction_commit_begin");
                tr.Commit();
                TraceSampleLineStage("first_transaction_commit_end");
            }

            res["corridor_rebuilt_after_sampling"] = false;
            return res;
        }

        // ===================== 8. 算工程量（材质列表）=====================

        static JsonNode ComputeQuantities(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string sfName = Need(a, "surface");
            string critName = Need(a, "criteria");
            string roadSlot = GetString(a, "road_surface_slot", "道路曲面");
            bool autoSurfaceMapping =
                string.Equals(GetString(a, "surface_mapping", null), "auto",
                    StringComparison.OrdinalIgnoreCase);
            string roadSurf = GetString(a, "road_surface", alName + "_道路曲面");
            string corridorName = GetString(a, "corridor", alName + "_走廊");

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = FindAlignment(tr, civ, alName);
                if (al == null) throw new InvalidOperationException("找不到路线 '" + alName + "'。");

                ObjectId slgId = ObjectId.Null;
                foreach (ObjectId gid in al.GetSampleLineGroupIds()) { slgId = gid; break; }
                if (slgId.IsNull)
                    throw new InvalidOperationException("路线 '" + alName + "' 下没有采样线组，先跑 create_sample_lines。");

                ObjectId critId = FindQtoCriteria(tr, civ, critName);
                if (critId.IsNull) throw new InvalidOperationException("找不到工程量准则 '" + critName + "'。");

                ObjectId sfId = FindSurfaceId(tr, civ, sfName);
                if (sfId.IsNull) throw new InvalidOperationException("找不到曲面 '" + sfName + "'。");

                ObjectId roadId = FindCorridorSurfaceId(tr, db, corridorName, roadSurf);
                if (roadId.IsNull)
                    throw new InvalidOperationException("找不到道路曲面 '" + roadSurf + "'，先跑 create_corridor_surface。");

                var slg = (CivSampleLineGroup)tr.GetObject(slgId, OpenMode.ForWrite);

                // 预检：要被映射的两个面必须已在组内采样。
                // 没标的先自动补标——道路曲面被原名重造（新 ObjectId）后，组里的新源
                // 默认 IsSampled=false，这正是"曲面修好了、体积还是 0"的最后一环。
                bool egOk = false, roadOk = false;
                int marked = 0;
                foreach (CivSectionSource src in slg.GetSectionSources())
                {
                    bool isEg = src.SourceNameOf() == sfName;
                    bool isRoad = src.SourceNameOf().Contains(roadSurf);
                    if ((isEg || isRoad) && !src.IsSampled)
                    {
                        try { src.IsSampled = true; marked++; } catch { }
                    }
                    if (!src.IsSampled) continue;
                    if (isEg) egOk = true;
                    else if (isRoad) roadOk = true;
                }
                if (!egOk || !roadOk)
                    throw new InvalidOperationException("采样线组里缺采样源（原地形=" + egOk + ", 道路曲面=" + roadOk +
                                                        "）。重跑 create_sample_lines 再算。");

                slg.MaterialLists.VolumeCalculationMethodType = CivVolumeMethod.AverageEndArea;

                // 覆盖重建：清空组上全部材质列表（名字自动生成，按名匹配不了）
                var toRemove = new List<Guid>();
                foreach (CivQtoMaterialList ex in slg.MaterialLists) toRemove.Add(ex.Guid);
                foreach (Guid g in toRemove) slg.MaterialLists.Remove(g);

                var slotLog = new JsonArray();
                using (var mapping = new CivQtoMapping(critId, slgId))
                {
                    // 槽位真实名从准则本身读，不写死——写死会静默失配、用错默认面 → 工程量全 0
                    var crit = (CivQtoCriteria)tr.GetObject(critId, OpenMode.ForRead);
                    var surfaceSlots = new List<string>();
                    for (int i = 0; i < crit.Count; i++)
                    {
                        var item = crit[i];
                        for (int j = 0; j < item.Count; j++)
                        {
                            var data = item[j];
                            if (data.ItemType != CivMaterialItemType.Surface) continue;
                            if (!surfaceSlots.Contains(data.Name)) surfaceSlots.Add(data.Name);
                        }
                    }

                    var slotKinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (autoSurfaceMapping)
                    {
                        foreach (string slot in surfaceSlots)
                        {
                            string lower = slot.ToLowerInvariant();
                            bool ground = slot.Contains("原地形") || slot.Contains("现状")
                                || lower.Contains("ground") || lower.Contains("existing");
                            bool road = slot.Contains("道路") || slot.Contains("设计")
                                || lower.Contains("road") || lower.Contains("proposed");
                            if (ground == road) slotKinds[slot] = "unknown";
                            else slotKinds[slot] = road ? "road" : "ground";
                        }
                        if (surfaceSlots.Count == 2)
                        {
                            string a0 = surfaceSlots[0], a1 = surfaceSlots[1];
                            if (slotKinds[a0] == "unknown" && slotKinds[a1] == "ground") slotKinds[a0] = "road";
                            if (slotKinds[a1] == "unknown" && slotKinds[a0] == "ground") slotKinds[a1] = "road";
                            if (slotKinds[a0] == "unknown" && slotKinds[a1] == "road") slotKinds[a0] = "ground";
                            if (slotKinds[a1] == "unknown" && slotKinds[a0] == "road") slotKinds[a1] = "ground";
                        }
                        var unknown = new List<string>();
                        foreach (string slot in surfaceSlots)
                            if (slotKinds[slot] == "unknown") unknown.Add(slot);
                        if (unknown.Count > 0)
                            throw new InvalidOperationException(
                                "工程量准则曲面槽自动映射存在歧义: " + string.Join(", ", unknown));
                    }

                    foreach (string slot in surfaceSlots)
                    {
                        bool isRoad = autoSurfaceMapping
                            ? slotKinds[slot] == "road"
                            : slot == roadSlot;
                        mapping.MapSurface(slot, isRoad ? roadId : sfId);
                        slotLog.Add(slot + " → " + (isRoad ? roadSurf : sfName));
                    }
                    if (!mapping.isMappingCompleted)
                        throw new InvalidOperationException("准则映射未完成，已映射槽位: " + slotLog.ToJsonString());

                    var ml = slg.MaterialLists.ImportCriteria(mapping);
                    var result = slg.GetTotalVolumeResultDataForMaterialList(ml.Guid);
                    var sections = result.GetResultsAlongSampleLines();

                    double cut = 0, fill = 0;
                    foreach (CivQtoSectionalResult sec in sections)
                    { cut = sec.VolumeResult.CumulativeCutVolume; fill = sec.VolumeResult.CumulativeFillVolume; }

                    var res = new JsonObject
                    {
                        ["alignment"] = alName,
                        ["criteria"] = critName,
                        ["sections"] = sections.Length,
                        ["mapped_slots"] = slotLog,
                        ["total_cut_m3"] = Math.Round(cut, 2),
                        ["total_fill_m3"] = Math.Round(fill, 2)
                    };
                    tr.Commit();
                    return res;
                }
            }
        }

        // ===================== 9. 导出工程量 =====================

        static JsonNode ExportQuantities(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string outdir = ResolveOutDir(a, doc);
            string format = GetString(a, "format", "both");

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var rows = new List<object[]>();
            double cut = 0, fill = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = FindAlignment(tr, civ, alName);
                if (al == null) throw new InvalidOperationException("找不到路线 '" + alName + "'。");

                ObjectId slgId = ObjectId.Null;
                foreach (ObjectId gid in al.GetSampleLineGroupIds()) { slgId = gid; break; }
                if (slgId.IsNull) throw new InvalidOperationException("路线 '" + alName + "' 下没有采样线组。");

                var slg = (CivSampleLineGroup)tr.GetObject(slgId, OpenMode.ForRead);
                Guid mlGuid = Guid.Empty;
                bool found = false;
                foreach (CivQtoMaterialList ml in slg.MaterialLists) { mlGuid = ml.Guid; found = true; break; }
                if (!found) throw new InvalidOperationException("采样线组上没有材质列表，先跑 compute_quantities。");

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
                    cut = v.CumulativeCutVolume; fill = v.CumulativeFillVolume;
                }
                rows.Add(new object[] { "合计", "", Math.Round(cut, 3), Math.Round(fill, 3), "", "" });
                tr.Commit();
            }

            var files = new JsonArray();
            foreach (string p in Excel.Write(outdir, "工程量_" + Sanitize(alName), HeadersQto, rows, format))
                files.Add(p);

            return new JsonObject
            {
                ["alignment"] = alName,
                ["sections"] = rows.Count - 1,
                ["total_cut_m3"] = Math.Round(cut, 2),
                ["total_fill_m3"] = Math.Round(fill, 2),
                ["files"] = files,
                ["outdir"] = outdir
            };
        }

        static readonly string[] HeadersQto =
            { "序号", "桩号", "累计挖方(m³)", "累计填方(m³)", "增量挖方(m³)", "增量填方(m³)" };

        // ===================== 9.5 出图：纵断面图 / 横断面图（模型空间）=====================
        // 移植自 RiverQto\出图.cs（已实战验证）。摆放沿用那边的定稿方案：
        // 先让 Civil 3D 草稿创建，再按桩号排序逐张 TransformBy 挪到自定网格
        // ——Civil 3D 自己的草稿排布会换行/重叠，不可控。锚点 = 断面图底边中点 = sv.Location。

        static JsonNode CreateProfileView(JsonObject a, Document doc)
        {
            double segmentLength = GetDouble(a, "segment_length", 0);
            if (segmentLength > 0 && !GetBool(a, "_single_segment", false))
                return CreateSegmentedProfileViews(a, doc, segmentLength);

            string alName = Need(a, "alignment");
            string style = GetString(a, "style", null);
            string bandSet = GetString(a, "band_set", null);
            string name = GetString(a, "name", alName + "_纵断面图");
            bool eraseExisting = GetBool(a, "erase_existing", true);
            double stationStart = GetDouble(a, "station_start", double.NaN);
            double stationEnd = GetDouble(a, "station_end", double.NaN);
            double elevMin = GetDouble(a, "elev_min", double.NaN);
            double elevMax = GetDouble(a, "elev_max", double.NaN);
            string groundProfileName = GetString(a, "ground_profile", null);
            string designProfileName = GetString(a, "design_profile", null);
            string groundLabelSet = GetString(a, "ground_label_set", null);
            string designLabelSet = GetString(a, "design_label_set", null);
            // 直接给「主桩号标注样式」（如 @原地形 / @设计高程）比给标签集更准：
            // 给了就按它建标注组，不再走标签集那条路。
            string groundLabelStyle = GetString(a, "ground_label_style", null);
            string designLabelStyle = GetString(a, "design_label_style", null);
            double labelIncrement = GetDouble(a, "label_increment", 50);
            // ProfileView.Create 会按默认标签集带进来一个「线标注组」
            // （Vertical Alignment Line Label Group，样式 Standard），图上多余，默认清掉
            bool dropLineLabels = GetBool(a, "drop_line_labels", true);

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            // 覆盖重建：删本路线已有的纵断面图
            int erased = 0;
            if (eraseExisting)
            {
                using (var trDel = db.TransactionManager.StartTransaction())
                {
                    CivAlignment al0 = FindAlignment(trDel, civ, alName);
                    if (al0 == null) throw new InvalidOperationException("找不到路线 '" + alName + "'。");
                    foreach (ObjectId pvId in al0.GetProfileViewIds())
                    {
                        var pv = (Autodesk.Civil.DatabaseServices.ProfileView)trDel.GetObject(pvId, OpenMode.ForWrite);
                        pv.Erase();
                        erased++;
                    }
                    trDel.Commit();
                }
            }

            using (var tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = FindAlignment(tr, civ, alName);
                if (al == null) throw new InvalidOperationException("找不到路线 '" + alName + "'。");

                // 放置点：缺省摆在路线起点正下方 200 m，避开路线本体
                double x = GetDouble(a, "x", double.NaN), y = GetDouble(a, "y", double.NaN);
                if (double.IsNaN(x) || double.IsNaN(y))
                {
                    double e = 0, n = 0;
                    al.PointLocation(al.StartingStation, 0, ref e, ref n);
                    if (double.IsNaN(x)) x = e;
                    if (double.IsNaN(y)) y = n - 200;
                }
                var origin = new Point3d(x, y, 0);

                ObjectId styleId = FindStyleId(tr, civ.Styles.ProfileViewStyles, style);
                ObjectId bandId = FindStyleId(tr, civ.Styles.ProfileViewBandSetStyles, bandSet);

                // 图里没有带状图集样式时 bandId 是 Null，带 bandSet 的重载会直接抛
                // "An ObjectId of ProfileViewBandSetStyle is excepted"——那就走两参重载，
                // 名字和样式建完再补上。
                ObjectId pvId2 = bandId.IsNull
                    ? Autodesk.Civil.DatabaseServices.ProfileView.Create(al.ObjectId, origin)
                    : Autodesk.Civil.DatabaseServices.ProfileView.Create(
                          al.ObjectId, origin, name, bandId, styleId);

                var pv2 = (Autodesk.Civil.DatabaseServices.ProfileView)tr.GetObject(pvId2, OpenMode.ForWrite);
                try { pv2.Name = name; } catch { }
                if (!styleId.IsNull) { try { pv2.StyleId = styleId; } catch { } }
                if (!double.IsNaN(stationStart) || !double.IsNaN(stationEnd))
                {
                    double s0 = double.IsNaN(stationStart) ? al.StartingStation : stationStart;
                    double s1 = double.IsNaN(stationEnd) ? al.EndingStation : stationEnd;
                    s0 = Math.Max(al.StartingStation, s0);
                    s1 = Math.Min(al.EndingStation, s1);
                    if (s1 <= s0)
                        throw new InvalidOperationException(
                            "纵断面桩号范围无效：" + Station(s0) + "～" + Station(s1) + "。");
                    pv2.StationRangeMode = Autodesk.Civil.DatabaseServices.StationRangeType.UserSpecified;
                    // 先扩大/收缩终点，再设置起点，避免中间状态出现 start > end。
                    pv2.StationEnd = s1;
                    pv2.StationStart = s0;
                }
                if (!double.IsNaN(elevMin) || !double.IsNaN(elevMax))
                {
                    if (double.IsNaN(elevMin) || double.IsNaN(elevMax)
                        || elevMax <= elevMin)
                        throw new InvalidOperationException(
                            "纵断面高程范围无效；elev_min / elev_max 必须同时提供且 max > min。");
                    pv2.ElevationRangeMode =
                        Autodesk.Civil.DatabaseServices.ElevationRangeType.UserSpecified;
                    // 先放大上限，再降低下限，避免中间状态 min > max。
                    pv2.ElevationMax = elevMax;
                    pv2.ElevationMin = elevMin;
                }

                var profiles = new JsonArray();
                ObjectId groundProfileId = ObjectId.Null;
                ObjectId designProfileId = ObjectId.Null;
                foreach (ObjectId pid in al.GetProfileIds())
                {
                    var p = (CivProfile)tr.GetObject(pid, OpenMode.ForRead);
                    profiles.Add(p.Name);
                    if (!string.IsNullOrEmpty(groundProfileName) && p.Name == groundProfileName)
                        groundProfileId = pid;
                    if (!string.IsNullOrEmpty(designProfileName) && p.Name == designProfileName)
                        designProfileId = pid;
                    string pt = p.ProfileType.ToString();
                    if (groundProfileId.IsNull && string.IsNullOrEmpty(groundProfileName)
                        && (pt.Equals("EG", StringComparison.OrdinalIgnoreCase)
                            || pt.IndexOf("Surface", StringComparison.OrdinalIgnoreCase) >= 0))
                        groundProfileId = pid;
                    if (designProfileId.IsNull && string.IsNullOrEmpty(designProfileName)
                        && (pt.Equals("FG", StringComparison.OrdinalIgnoreCase)
                            || pt.IndexOf("Layout", StringComparison.OrdinalIgnoreCase) >= 0))
                        designProfileId = pid;
                }
                if (groundProfileId.IsNull || designProfileId.IsNull)
                    throw new InvalidOperationException(
                        "路线 '" + alName + "' 无法识别现状地形/设计纵断面；"
                        + "可显式传 ground_profile / design_profile。");

                // 标注栏数据源：Profile1=现状地形，Profile2=设计纵断面。
                int bandSourcesSet = 0;
                var topBands = pv2.Bands.GetTopBandItems();
                foreach (Autodesk.Civil.DatabaseServices.ProfileViewBandItem item in topBands)
                {
                    item.Profile1Id = groundProfileId;
                    item.Profile2Id = designProfileId;
                    bandSourcesSet++;
                }
                pv2.Bands.SetTopBandItems(topBands);
                var bottomBands = pv2.Bands.GetBottomBandItems();
                foreach (Autodesk.Civil.DatabaseServices.ProfileViewBandItem item in bottomBands)
                {
                    item.Profile1Id = groundProfileId;
                    item.Profile2Id = designProfileId;
                    bandSourcesSet++;
                }
                pv2.Bands.SetBottomBandItems(bottomBands);

                // ProfileView.Create 会按旧/默认标签集带入若干 ProfileLabelGroup。
                // 全部清掉，再分别按两个 @ 标签集创建，避免旧标签与新标签叠加。
                int oldProfileLabelGroupsErased = 0;
                foreach (ObjectId lid in pv2.GetLabelIds())
                {
                    var lg = tr.GetObject(lid, OpenMode.ForRead, false)
                        as Autodesk.Civil.DatabaseServices.ProfileLabelGroup;
                    if (lg == null) continue;
                    lg.UpgradeOpen();
                    lg.Erase();
                    oldProfileLabelGroupsErased++;
                }

                int groundLabels, designLabels;
                // 传「无」/"none" = 这条剖面线不挂任何标签（2026-08-26 项目B纵断面：
                // 用户打回桩号高程标签和折点坡度标签，全部不要）
                bool groundNone = groundLabelSet == "无" || string.Equals(groundLabelSet, "none", StringComparison.OrdinalIgnoreCase);
                bool designNone = designLabelSet == "无" || string.Equals(designLabelSet, "none", StringComparison.OrdinalIgnoreCase);
                if (groundNone)
                {
                    groundLabels = 0;
                    groundLabelSet = "(无标签)";
                }
                else if (!string.IsNullOrWhiteSpace(groundLabelStyle))
                {
                    ObjectId sid = NeedProfileLabelStyle(tr, civ, groundLabelStyle);
                    Autodesk.Civil.DatabaseServices.ProfileStationLabelGroup.CreateMajor(
                        pvId2, groundProfileId, sid, labelIncrement);
                    groundLabels = 1;
                    groundLabelSet = "(直接用样式 " + TryGetName(tr.GetObject(sid, OpenMode.ForRead)) + ")";
                }
                else
                {
                    ObjectId groundLabelSetId = FindStyleId(
                        tr, civ.Styles.LabelSetStyles.ProfileLabelSetStyles, groundLabelSet);
                    if (groundLabelSetId.IsNull)
                        throw new InvalidOperationException(
                            "找不到原地形标签集，且没给 ground_label_style。");
                    groundLabelSet = TryGetName(tr.GetObject(groundLabelSetId, OpenMode.ForRead));
                    groundLabels = ApplyProfileLabelSet(tr, pvId2, groundProfileId, groundLabelSetId);
                }

                if (designNone)
                {
                    designLabels = 0;
                    designLabelSet = "(无标签)";
                }
                else if (!string.IsNullOrWhiteSpace(designLabelStyle))
                {
                    ObjectId sid = NeedProfileLabelStyle(tr, civ, designLabelStyle);
                    Autodesk.Civil.DatabaseServices.ProfileStationLabelGroup.CreateMajor(
                        pvId2, designProfileId, sid, labelIncrement);
                    designLabels = 1;
                    designLabelSet = "(直接用样式 " + TryGetName(tr.GetObject(sid, OpenMode.ForRead)) + ")";
                }
                else
                {
                    ObjectId designLabelSetId = FindStyleId(
                        tr, civ.Styles.LabelSetStyles.ProfileLabelSetStyles, designLabelSet);
                    if (designLabelSetId.IsNull)
                        throw new InvalidOperationException(
                            "找不到设计线标签集，且没给 design_label_style。");
                    designLabelSet = TryGetName(tr.GetObject(designLabelSetId, OpenMode.ForRead));
                    designLabels = ApplyProfileLabelSet(tr, pvId2, designProfileId, designLabelSetId);
                }

                // 「无」还要清挂在剖面线 Profile 对象上的标签组（PVI 桩号高程、切线坡度这些）——
                // 它们不属于视图，pv2.GetLabelIds() 和视图侧标签集都管不着，视图重建后照样渲染
                // （2026-08-26 项目B纵断面实测：标签集传「无」图面纹丝不动，就是这批）。
                int profileLabelGroupsErased = 0;
                {
                    var killP = new List<ObjectId>();
                    if (groundNone) CollectProfileLabelGroupsInDb(tr, db, groundProfileId, killP);
                    if (designNone) CollectProfileLabelGroupsInDb(tr, db, designProfileId, killP);
                    // 两个都「无」＝这条路线的纵断面图面彻底无剖面线标签。
                    // 必须扫路线**全部** Profile：旧设计线（被走廊引用、修复步不动）身上的
                    // PVI/坡度标签组照样渲染进新视图——只清新采两条线等于没清
                    // （2026-08-26 项目B实测两轮图面纹丝不动，就是它）。
                    if (groundNone && designNone)
                    {
                        foreach (ObjectId pid in al.GetProfileIds())
                            CollectProfileLabelGroupsInDb(tr, db, pid, killP);
                    }
                    foreach (ObjectId lid in killP)
                    {
                        try
                        {
                            var o = tr.GetObject(lid, OpenMode.ForWrite, false);
                            if (o != null && !o.IsErased) { o.Erase(); profileLabelGroupsErased++; }
                        }
                        catch { }
                    }
                }

                // 清掉线标注组：它挂在剖面线上（不在断面图的标签里），
                // 上面那轮 pv2.GetLabelIds() 的清场扫不到，所以留到这里单独收拾。
                int lineLabelGroupsErased = 0;
                if (dropLineLabels)
                {
                    var kill = new List<ObjectId>();
                    CollectLineLabelGroups(tr, pv2.GetLabelIds(), kill);
                    // 线标注组挂在剖面线上、断面图的标签列表里未必有；
                    // 再扫一遍模型空间，只收 ProfileViewId 指向本图的那些，不误伤别的图
                    CollectLineLabelGroupsInDb(tr, db, pvId2, kill);
                    foreach (ObjectId lid in kill)
                    {
                        try
                        {
                            var o = tr.GetObject(lid, OpenMode.ForWrite, false);
                            if (o != null && !o.IsErased) { o.Erase(); lineLabelGroupsErased++; }
                        }
                        catch { }
                    }
                }

                var res = new JsonObject
                {
                    ["profile_view"] = name,
                    ["alignment"] = alName,
                    ["old_erased"] = erased,
                    ["origin"] = Math.Round(x, 3) + ", " + Math.Round(y, 3),
                    ["station_start"] = Math.Round(pv2.StationStart, 3),
                    ["station_end"] = Math.Round(pv2.StationEnd, 3),
                    ["elev_range"] = pv2.ElevationRangeMode ==
                        Autodesk.Civil.DatabaseServices.ElevationRangeType.UserSpecified
                        ? Math.Round(pv2.ElevationMin, 3) + " ~ "
                          + Math.Round(pv2.ElevationMax, 3)
                        : "自动",
                    ["profiles_shown"] = profiles,
                    ["band_profile1"] = TryGetName(tr.GetObject(groundProfileId, OpenMode.ForRead)),
                    ["band_profile2"] = TryGetName(tr.GetObject(designProfileId, OpenMode.ForRead)),
                    ["band_items_sources_set"] = bandSourcesSet,
                    ["old_profile_label_groups_erased"] = oldProfileLabelGroupsErased,
                    ["ground_label_set"] = groundLabelSet,
                    ["ground_label_groups_created"] = groundLabels,
                    ["design_label_set"] = designLabelSet,
                    ["design_label_groups_created"] = designLabels,
                    ["line_label_groups_erased"] = lineLabelGroupsErased
                };
                tr.Commit();
                return res;
            }
        }

        static JsonNode CreateSegmentedProfileViews(
            JsonObject a, Document doc, double segmentLength)
        {
            if (segmentLength <= 0)
                throw new InvalidOperationException("segment_length 必须大于 0。");

            string alName = Need(a, "alignment");
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            double routeStart, routeEnd, baseX, baseY;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = FindAlignment(tr, civ, alName);
                if (al == null)
                    throw new InvalidOperationException("找不到路线 '" + alName + "'。");
                routeStart = al.StartingStation;
                routeEnd = al.EndingStation;
                baseX = GetDouble(a, "x", double.NaN);
                baseY = GetDouble(a, "y", double.NaN);
                if (double.IsNaN(baseX) || double.IsNaN(baseY))
                {
                    double e = 0, n = 0;
                    al.PointLocation(routeStart, 0, ref e, ref n);
                    if (double.IsNaN(baseX)) baseX = e;
                    if (double.IsNaN(baseY)) baseY = n - 200;
                }
                tr.Commit();
            }

            int count = Math.Max(1,
                (int)Math.Ceiling((routeEnd - routeStart) / segmentLength));
            int cols = Math.Max(1, (int)GetDouble(a, "segment_cols", 3));
            double gapX = GetDouble(a, "segment_spacing_x", segmentLength + 50);
            double gapY = GetDouble(a, "segment_spacing_y", 220);
            string baseName = GetString(a, "name", alName + "_纵断面图");
            var views = new JsonArray();
            int erased = 0;

            for (int i = 0; i < count; i++)
            {
                double s0 = routeStart + i * segmentLength;
                double s1 = Math.Min(routeEnd, s0 + segmentLength);
                int col = i % cols, row = i / cols;
                var child = JsonNode.Parse(a.ToJsonString()) as JsonObject;
                child["segment_length"] = 0;
                child["_single_segment"] = true;
                child["erase_existing"] = i == 0 && GetBool(a, "erase_existing", true);
                child["station_start"] = s0;
                child["station_end"] = s1;
                child["x"] = baseX + col * gapX;
                child["y"] = baseY - row * gapY;
                child["name"] = count == 1
                    ? baseName
                    : baseName + "-" + (i + 1).ToString("00");
                JsonObject result = CreateProfileView(child, doc) as JsonObject;
                if (result == null)
                    throw new InvalidOperationException("分段纵断面节点未返回对象。");
                erased += result["old_erased"] == null
                    ? 0 : result["old_erased"].GetValue<int>();
                views.Add(result);
            }

            var first = views[0] as JsonObject;
            return new JsonObject
            {
                ["profile_view"] = first == null || first["profile_view"] == null
                    ? null : first["profile_view"].DeepClone(),
                ["profile_views"] = views,
                ["count"] = views.Count,
                ["alignment"] = alName,
                ["segment_length"] = segmentLength,
                ["station_start"] = routeStart,
                ["station_end"] = routeEnd,
                ["old_erased"] = erased,
                ["layout"] = new JsonObject
                {
                    ["cols"] = cols,
                    ["spacing_x"] = gapX,
                    ["spacing_y"] = gapY
                }
            };
        }

        static ObjectId FindExactStyleId(Transaction tr, object collection, string name, string category)
        {
            var en = collection as System.Collections.IEnumerable;
            if (en != null)
                foreach (object item in en)
                {
                    if (!(item is ObjectId)) continue;
                    ObjectId id = (ObjectId)item;
                    if (TryGetName(tr.GetObject(id, OpenMode.ForRead)) == name) return id;
                }
            throw new InvalidOperationException("找不到" + category + " '" + name + "'。");
        }

        /// <summary>挑出「线标注组」（Vertical Alignment Line Label Group）——按类型名认，
        /// 站号/曲线/变坡点那些不动。</summary>
        static void CollectLineLabelGroups(Transaction tr, ObjectIdCollection ids, List<ObjectId> outIds)
        {
            if (ids == null) return;
            foreach (ObjectId id in ids)
            {
                if (id.IsNull || outIds.Contains(id)) continue;
                try
                {
                    DBObject o = tr.GetObject(id, OpenMode.ForRead, false);
                    if (o == null) continue;
                    string t = o.GetType().Name;
                    if (t.IndexOf("Line", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        t.IndexOf("LabelGroup", StringComparison.OrdinalIgnoreCase) >= 0)
                        outIds.Add(id);
                }
                catch { }
            }
        }

        /// <summary>模型空间里找本断面图的线标注组：类型名认，归属按 ProfileViewId 反射比对，
        /// 认不出归属的一律不动（宁可留着，也不删别的图的标注）。</summary>
        /// <summary>收挂在指定剖面线（Profile 对象）上的所有标签组实体：
        /// PVI 桩号高程、切线坡度等，类型名含 LabelGroup 且 ProfileId 指向它。
        /// 这批标签不属于任何视图，视图侧标签集管不着，「无标签」时只能在库里点名删。</summary>
        static void CollectProfileLabelGroupsInDb(Transaction tr, Database db,
            ObjectId profileId, List<ObjectId> outIds)
        {
            if (profileId.IsNull) return;
            foreach (ObjectId id in ModelSpace(db, tr))
            {
                if (id.IsNull || outIds.Contains(id)) continue;
                try
                {
                    DBObject o = tr.GetObject(id, OpenMode.ForRead, false);
                    if (o == null) continue;
                    if (o.GetType().Name.IndexOf("LabelGroup", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var p = o.GetType().GetProperty("ProfileId");
                    if (p == null) continue;
                    object v = p.GetValue(o, null);
                    if (v is ObjectId && (ObjectId)v == profileId) outIds.Add(id);
                }
                catch { }
            }
        }

        static void CollectLineLabelGroupsInDb(Transaction tr, Database db,
            ObjectId profileViewId, List<ObjectId> outIds)
        {
            foreach (ObjectId id in ModelSpace(db, tr))
            {
                if (id.IsNull || outIds.Contains(id)) continue;
                try
                {
                    DBObject o = tr.GetObject(id, OpenMode.ForRead, false);
                    if (o == null) continue;
                    string t = o.GetType().Name;
                    if (t.IndexOf("Line", StringComparison.OrdinalIgnoreCase) < 0 ||
                        t.IndexOf("LabelGroup", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var p = o.GetType().GetProperty("ProfileViewId");
                    if (p == null) continue;
                    object v = p.GetValue(o, null);
                    if (v is ObjectId && (ObjectId)v == profileViewId) outIds.Add(id);
                }
                catch { }
            }
        }

        /// <summary>按名字找纵断面主桩号标注样式（@原地形 这类）；找不到就抛错并列出可用的，
        /// 绝不退回“集合第一个”——标注样式设错比不设更难发现。</summary>
        static ObjectId NeedProfileLabelStyle(Transaction tr, CivDoc civ, string name)
        {
            object coll = civ.Styles.LabelStyles.ProfileLabelStyles.MajorStationLabelStyles;
            var seen = new List<string>();
            ObjectId exact = ObjectId.Null, ci = ObjectId.Null;
            var en = coll as System.Collections.IEnumerable;
            if (en != null)
                foreach (object item in en)
                {
                    if (!(item is ObjectId)) continue;
                    ObjectId id = (ObjectId)item;
                    string n = null;
                    try { n = TryGetName(tr.GetObject(id, OpenMode.ForRead)); }
                    catch { }
                    if (string.IsNullOrEmpty(n)) continue;
                    seen.Add(n);
                    if (n == name) { exact = id; break; }
                    if (ci.IsNull && string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ci = id;
                }
            if (!exact.IsNull) return exact;
            if (!ci.IsNull) return ci;
            throw new InvalidOperationException(
                "找不到纵断面主桩号标注样式 '" + name + "'。图里可用的有：" +
                (seen.Count == 0 ? "（一个也没有）" : string.Join("、", seen)));
        }

        static int ApplyProfileLabelSet(Transaction tr, ObjectId profileViewId,
                                        ObjectId profileId, ObjectId labelSetId)
        {
            var set = (Autodesk.Civil.DatabaseServices.Styles.ProfileLabelSetStyle)
                tr.GetObject(labelSetId, OpenMode.ForRead);
            int created = 0;
            foreach (Autodesk.Civil.DatabaseServices.Styles.ProfileLabelSetItem item in set)
            {
                string kind = item.LabelStyleType.ToString();
                if (!kind.Equals("ProfileMajorStation", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "当前 create_profile_view 尚未支持标签集条目类型 '" + kind + "'。");
                double increment = 50;
                try { if (item.Increment > 0) increment = item.Increment; } catch { }
                Autodesk.Civil.DatabaseServices.ProfileStationLabelGroup.CreateMajor(
                    profileViewId, profileId, item.LabelStyleId, increment);
                created++;
            }
            return created;
        }

        static JsonNode CreateSectionViews(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string style = GetString(a, "style", null);
            string codeSet = GetString(a, "code_set", null);
            double elevMin = GetDouble(a, "elev_min", 0);
            double elevMax = GetDouble(a, "elev_max", 0);      // min>=max → 自动高程范围
            double offLeft = GetDouble(a, "offset_left", 50);
            double offRight = GetDouble(a, "offset_right", 50);
            int rows = (int)GetDouble(a, "rows", 2);
            int cols = (int)GetDouble(a, "cols", 2);
            double colSpacing = GetDouble(a, "col_spacing", 130);
            double rowSpacing = GetDouble(a, "row_spacing", 45);
            // 每个 rows×cols 页组之间追加的垂直净距；用于让连续断面组严格落入相邻材料框。
            double groupSpacing = GetDouble(a, "group_spacing", 0);
            // 体积表格默认关：建得出来，但会留下没关的写句柄，之后 save_dwg 必报 eWasOpenForWrite
            // （ForRead 打开断面图去建表则直接硬崩进程）。要表格就把 save_dwg 换成别的落盘方式，
            // 或者在 GUI 里补。详见 2026-07-26 任务的 踩坑.md 第 8 条。
            bool wantVolTable = GetBool(a, "volume_table", false);
            string corridorName = GetString(a, "corridor", alName + "_走廊");

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            // 组名解析：给了 group 用 group；没给先找老约定 <路线>_采样线组，
            // 找不到且路线只有一个组就用它（GUI 手建的组名字五花八门），多个组必须点名。
            string groupName = GetString(a, "group", null);
            using (Transaction trg = db.TransactionManager.StartTransaction())
            {
                CivAlignment alg = FindAlignment(trg, civ, alName);
                if (alg == null)
                    throw new InvalidOperationException("找不到路线 '" + alName + "'。");
                var names = new List<string>();
                foreach (ObjectId gid in alg.GetSampleLineGroupIds())
                    names.Add(((CivSampleLineGroup)trg.GetObject(gid, OpenMode.ForRead)).Name);
                if (groupName == null)
                {
                    string conv = alName + "_采样线组";
                    if (names.Contains(conv)) groupName = conv;
                    else if (names.Count == 1) groupName = names[0];
                    else if (names.Count == 0)
                        throw new InvalidOperationException(
                            "路线 '" + alName + "' 没有采样线组，先跑 create_sample_lines（或换线后用 refresh_sample_lines）。");
                    else
                        throw new InvalidOperationException(
                            "路线 '" + alName + "' 有多个采样线组（" + string.Join("、", names) + "），用 group 参数指定一个。");
                }
                else if (!names.Contains(groupName))
                {
                    throw new InvalidOperationException(
                        "路线 '" + alName + "' 下没有名为 '" + groupName + "' 的采样线组（现有：" +
                        (names.Count == 0 ? "无" : string.Join("、", names)) + "）。");
                }

                // 走廊名解析（与组名同一个病同一个方子）：给了 corridor 用 corridor；
                // 老约定 <路线>_走廊 存在就用它；否则基线挂在本路线上的走廊恰好一个就用它
                // （GUI 建的走廊名如「道路[Z1](2)」），多个必须点名，一个没有直接报错——
                // 不然断面建完守卫才发现走廊本体断面为零，白建一堆还把排版拦死。
                if (a["corridor"] == null)
                {
                    string convCorr = alName + "_走廊";
                    bool convExists = false;
                    var mine = new List<string>();
                    foreach (ObjectId id in ModelSpace(db, trg))
                    {
                        CivCorridor cor;
                        try { cor = trg.GetObject(id, OpenMode.ForRead) as CivCorridor; }
                        catch { continue; }
                        if (cor == null) continue;
                        if (string.Equals(cor.Name, convCorr, StringComparison.OrdinalIgnoreCase))
                            convExists = true;
                        try
                        {
                            foreach (Autodesk.Civil.DatabaseServices.Baseline bl in cor.Baselines)
                            {
                                CivAlignment bal;
                                try { bal = trg.GetObject(bl.AlignmentId, OpenMode.ForRead) as CivAlignment; }
                                catch { continue; }
                                if (bal != null && string.Equals(bal.Name, alName, StringComparison.OrdinalIgnoreCase))
                                {
                                    mine.Add(cor.Name);
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                    if (convExists) corridorName = convCorr;
                    else if (mine.Count == 1) corridorName = mine[0];
                    else if (mine.Count > 1)
                        throw new InvalidOperationException(
                            "路线 '" + alName + "' 挂着多个走廊（" + string.Join("、", mine) +
                            "），用 corridor 参数指定一个。");
                    else
                        throw new InvalidOperationException(
                            "路线 '" + alName + "' 没有走廊：找不到 '" + convCorr +
                            "'，也没有基线挂在这条路线上的走廊。先建走廊再出断面。");
                }
                trg.Commit();
            }

            // 项目自定义 SAC 往往产生新的 Link Code，而样式库代码集只有点代码。
            // 源、Draw、代码集名字都正常，但实际链接代码一个也没映射时，走廊断面仍是空白。
            JsonObject codeMappingResult = null;
            var codeMappings = a["code_mappings"] as JsonArray;
            if (codeMappings != null && codeMappings.Count > 0)
            {
                if (string.IsNullOrEmpty(codeSet))
                    throw new InvalidOperationException("提供 code_mappings 时必须指定 code_set。");

                // 新增代码条目和设置标签不能挤在同一事务：Civil 3D 对刚 Add 的
                // CodeSetStyleItem 立刻写 LabelStyleId 会报 eInvalidInput。
                // 先提交链接样式，再在第二个事务里挂标签。
                var styleItems = new JsonArray();
                var labelItems = new JsonArray();
                foreach (JsonNode n in codeMappings)
                {
                    var item = n as JsonObject;
                    if (item == null || item["code"] == null) continue;
                    if (item["style"] != null)
                        styleItems.Add(new JsonObject
                        {
                            ["code"] = item["code"].DeepClone(),
                            ["style"] = item["style"].DeepClone(),
                            ["style_type"] = item["style_type"] == null
                                ? "link" : item["style_type"].DeepClone()
                        });
                    if (item["label_style"] != null)
                        labelItems.Add(new JsonObject
                        {
                            ["code"] = item["code"].DeepClone(),
                            ["label_style"] = item["label_style"].DeepClone(),
                            ["style_type"] = item["style_type"] == null
                                ? "link" : item["style_type"].DeepClone()
                        });
                }

                var styleArgs = new JsonObject
                {
                    ["name"] = codeSet,
                    ["items"] = styleItems,
                    ["dry_run"] = false
                };
                JsonObject styleResult = CodeSetEdit(styleArgs, doc) as JsonObject;
                var styleFailed = styleResult == null ? null : styleResult["failed"] as JsonArray;
                if (styleResult == null || (styleFailed != null && styleFailed.Count > 0))
                    throw new InvalidOperationException(
                        "道路代码集链接样式映射失败：" +
                        (styleResult == null ? "(无回执)" : styleResult.ToJsonString()));

                JsonObject labelResult = null;
                if (labelItems.Count > 0)
                {
                    var labelArgs = new JsonObject
                    {
                        ["name"] = codeSet,
                        ["items"] = labelItems,
                        ["dry_run"] = false
                    };
                    labelResult = CodeSetEdit(labelArgs, doc) as JsonObject;
                    var labelFailed = labelResult == null
                        ? null : labelResult["failed"] as JsonArray;
                    if (labelResult == null || (labelFailed != null && labelFailed.Count > 0))
                        throw new InvalidOperationException(
                            "道路代码集链接标签映射失败：" +
                            (labelResult == null ? "(无回执)" : labelResult.ToJsonString()));
                }
                codeMappingResult = new JsonObject
                {
                    ["style_pass"] = styleResult,
                    ["label_pass"] = labelResult
                };
            }

            // 放置基点：缺省摆在路线起点下方 400 m（纵断面图之下，互不压）
            double bx = GetDouble(a, "x", double.NaN), by = GetDouble(a, "y", double.NaN);
            using (var tr0 = db.TransactionManager.StartTransaction())
            {
                CivAlignment al0 = FindAlignment(tr0, civ, alName);
                if (al0 == null) throw new InvalidOperationException("找不到路线 '" + alName + "'。");
                if (double.IsNaN(bx) || double.IsNaN(by))
                {
                    double e = 0, n = 0;
                    al0.PointLocation(al0.StartingStation, 0, ref e, ref n);
                    if (double.IsNaN(bx)) bx = e;
                    if (double.IsNaN(by)) by = n - 400;
                }
                tr0.Commit();
            }
            var basePoint = new Point3d(bx, by, 0);

            // 覆盖重建：删本组已有断面图（挂在图上的体积表格随图删）
            int erased = 0;
            using (var trDel = db.TransactionManager.StartTransaction())
            {
                CivSampleLineGroup g = FindSampleLineGroup(trDel, civ, alName, groupName);
                if (g == null)
                    throw new InvalidOperationException("找不到采样线组 '" + groupName + "'，先跑 create_sample_lines。");
                foreach (ObjectId slId in g.GetSampleLineIds())
                {
                    var sl = (CivSampleLine)trDel.GetObject(slId, OpenMode.ForRead);
                    foreach (ObjectId svId in sl.GetSectionViewIds())
                    {
                        ((Autodesk.Civil.DatabaseServices.SectionView)trDel.GetObject(svId, OpenMode.ForWrite)).Erase();
                        erased++;
                    }
                }
                trDel.Commit();
            }

            int count = 0;
            int secStyled = 0;
            int surfStyled = 0;   // 曲面（地面线）断面设了样式的条数
            int corridorBodySections = 0;
            int corridorSurfaceSections = 0;
            int corridorDisplayOverrides = 0;
            string creationMode = "";
            string creationNote = "";
            string corridorCodeSetBefore = "";
            bool corridorCodeSetSet = false;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                CivSampleLineGroup slg = FindSampleLineGroup(tr, civ, alName, groupName);
                slg.UpgradeOpen();

                ObjectId codeSetId = FindStyleId(
                    tr, civ.Styles.CodeSetStyles, codeSet);

                // ★ 代码集**首要**落点：走廊本体自己的 CodeSetStyleId。
                // 断面在图上怎么渲染（链接线、点标记、以及挂在它们上的标注）读的是这个，
                // 不是断面源的 StyleId、也不是 Section.StyleId。
                // 之前只设后两者，走廊仍挂着旧代码集（项目B里是 river-dregde-1-500[recommend]，
                // 只有 10 条映射、10 个代码没覆盖），所以点标注一直出不来。
                // （2026-07-28 查实；用户从一开始就指出"代码集挂在道路本体"，我绕了很久才照做）
                if (!codeSetId.IsNull)
                {
                    foreach (ObjectId eid in ModelSpace(db, tr))
                    {
                        var cor = tr.GetObject(eid, OpenMode.ForRead)
                                  as Autodesk.Civil.DatabaseServices.Corridor;
                        if (cor == null || cor.Name != corridorName) continue;
                        try
                        {
                            corridorCodeSetBefore = cor.CodeSetStyleName;
                            cor.UpgradeOpen();
                            cor.CodeSetStyleId = codeSetId;
                            corridorCodeSetSet = true;
                        }
                        catch (System.Exception ex)
                        { creationNote += " 设走廊代码集失败: " + ex.GetType().Name + ": " + Truncate(ex.Message, 80) + ";"; }
                        break;
                    }
                }

                // 代码集保险①：源级（走廊源的 Style 槽）
                if (!codeSetId.IsNull)
                    foreach (CivSectionSource src in slg.GetSectionSources())
                        if (src.SourceNameOf() == corridorName)
                            try { src.StyleId = codeSetId; } catch { }

                var rangeOpts = new Autodesk.Civil.DatabaseServices.SectionViewGroupCreationRangeOptions(slg.ObjectId);
                rangeOpts.SetOffsetRange(-offLeft, offRight);
                rangeOpts.UseUserSpecifiedOffset = true;

                var placeOpts = new Autodesk.Civil.DatabaseServices.SectionViewGroupCreationPlacementOptions();
                placeOpts.UseDraftPlacement();

                CivAlignment al = FindAlignment(tr, civ, alName);
                ObjectId svStyleId = FindStyleId(tr, civ.Styles.SectionViewStyles, style);
                // 曲面（地面线）断面用的断面样式，建组时就交代，别等事后补
                ObjectId secStyleId = FindStyleId(tr, civ.Styles.SectionStyles,
                                                  GetString(a, "section_style", null));
                // 材质断面（挖方等）自己的断面样式；不给就跟曲面断面走同一个
                ObjectId matStyleId = FindStyleId(tr, civ.Styles.SectionStyles,
                                                  GetString(a, "material_style", null));

                // 沿用旧操作台已验证路径：五参数草稿创建。
                slg.SectionViewGroups.Add(basePoint, al.StartingStation, al.EndingStation,
                                          rangeOpts, placeOpts);
                creationMode = "5参数(旧操作台基线)";

                bool manualElev = elevMin < elevMax;
                foreach (ObjectId slId in slg.GetSampleLineIds())
                {
                    var sl = (CivSampleLine)tr.GetObject(slId, OpenMode.ForRead);
                    foreach (ObjectId svId in sl.GetSectionViewIds())
                    {
                        var sv = (Autodesk.Civil.DatabaseServices.SectionView)tr.GetObject(svId, OpenMode.ForWrite);
                        if (!svStyleId.IsNull) sv.StyleId = svStyleId;
                        if (manualElev)
                        {
                            sv.IsElevationRangeAutomatic = false;
                            sv.ElevationMin = elevMin;
                            sv.ElevationMax = elevMax;
                        }
                        else sv.IsElevationRangeAutomatic = true;
                        count++;
                    }
                }

                // 断面自身的样式，两类来源规则不同（2026-07-28 用新旧断面逐属性对比查实）：
                //   走廊断面 → StyleId 就是**代码集样式**（Civil 的设计如此）
                //   曲面断面（地面线）→ StyleId 是**断面样式**（如 Existing Ground / @C3DF-GroundLine）
                // 以前只设了前者，后者一直停在 Standard，出图就是「样式不对」。
                // secStyleId 在建组前已求出，这里是事后兜底（8 参数路径失败退回五参数时仍能补上）
                foreach (CivSectionSource src in slg.GetSectionSources())
                {
                    bool isCorridor = src.SourceNameOf() == corridorName;
                    string sourceType = "";
                    try { sourceType = src.SourceType.ToString(); } catch { }
                    // 三类来源三种样式：走廊断面=代码集样式（Civil 的设计），
                    // 材质断面（挖方）= material_style，其余曲面断面 = section_style
                    bool isMaterial = sourceType.IndexOf("Material", StringComparison.OrdinalIgnoreCase) >= 0;
                    int sourceSectionCount = 0;
                    ObjectId want = isCorridor ? codeSetId
                                  : isMaterial && !matStyleId.IsNull ? matStyleId
                                  : secStyleId;
                    foreach (ObjectId secId in src.GetSectionIds())
                    {
                        sourceSectionCount++;
                        try
                        {
                            var sec = (Autodesk.Civil.DatabaseServices.Section)
                                tr.GetObject(secId, OpenMode.ForWrite);
                            if (!want.IsNull)
                            {
                                sec.StyleId = want;
                                if (isCorridor) secStyled++; else surfStyled++;
                            }
                        }
                        catch { }
                    }
                    if (isCorridor) corridorBodySections += sourceSectionCount;
                    else if (sourceType.IndexOf("CorridorSurface",
                             StringComparison.OrdinalIgnoreCase) >= 0)
                        corridorSurfaceSections += sourceSectionCount;
                }
                tr.Commit();
            }
            if (count == 0) throw new InvalidOperationException("没有生成任何断面图。");
            if (corridorBodySections == 0)
                throw new InvalidOperationException(
                    "采样线组虽已建立，但没有生成走廊本体断面；停止出图，避免只画道路曲面的假成功。");

            // ★ 第二个事务里刷新断面图组。
            // 依据：把「创建视图 / 导入标签集 / 更新布局」挤在同一事务里，
            // Civil 3D 还没完成对象依赖更新，标注就渲染不出来。必须提交后另起事务。
            // （2026-07-28，来自用户提供的排查文档「推荐的稳定出图顺序」一节）
            int layoutUpdated = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                CivSampleLineGroup slg2 = FindSampleLineGroup(tr, civ, alName, groupName);
                foreach (Autodesk.Civil.DatabaseServices.SectionViewGroup g in slg2.SectionViewGroups)
                {
                    try
                    {
                        var m = g.GetType().GetMethod("UpdateLayout", Type.EmptyTypes);
                        if (m != null) { m.Invoke(g, null); layoutUpdated++; }
                    }
                    catch (System.Exception ex)
                    { creationNote += " UpdateLayout 失败: " + ex.GetType().Name + ": " + Truncate(ex.Message, 80) + ";"; }
                }
                tr.Commit();
            }

            // ★ 显式创建「走廊点标注组」。
            // 横断面图有个全局开关 eSectionViewCorridorPointLabelOption（走廊点代码标注方法：
            // Section Label Set / Code Set Style），程序化建图时它停在 Section Label Set，
            // **只压制 Point 的代码集标注，不影响 Link** —— 正是「链接标注在、点标注全没」的成因。
            // 该枚举在托管 API 里没有任何成员可读写，设不了；但可以绕过去：
            // 直接为「每个断面图 × 每个断面」建 SectionCorridorPointLabelGroup，
            // 等价于界面上选中断面 → 右键 Edit labels 加走廊点标注。
            // （2026-07-28，用户提出该开关的假设后按此方向查实）
            // 默认**不建**：实测它走的是 Label Set 分支，只能用 CorridorPointLabelStyles 里的样式
            // （本图只有 Standard），出来的是 "Subassembly Point Elevation/Offset" 那种通用文字，
            // 不是代码集里的标注。要的是 Code Set 分支。留作可选，需要时显式打开。
            int pointLabelGroups = 0;
            if (GetBool(a, "corridor_point_labels", false))
            using (var tr = db.TransactionManager.StartTransaction())
            {
                CivSampleLineGroup slg3 = FindSampleLineGroup(tr, civ, alName, groupName);
                foreach (ObjectId slId in slg3.GetSampleLineIds())
                {
                    var sl = (CivSampleLine)tr.GetObject(slId, OpenMode.ForRead);
                    foreach (ObjectId svId in sl.GetSectionViewIds())
                        foreach (ObjectId secId in sl.GetSectionIds())
                        {
                            try
                            {
                                // 直接建，不预先用 GetAvailableLabelGroupIds 过滤——
                                // 那个方法返回的是**已存在**的组，不是"可创建"的组，
                                // 拿它当守卫会把 Create 全拦下（实测 0 个，且不抛异常）。
#if NET472
                                throw new NotSupportedException("SectionCorridorPointLabelGroup needs Civil 3D 2023 or newer.");
#else
                                Autodesk.Civil.DatabaseServices.SectionCorridorPointLabelGroup
                                    .Create(svId, secId);
                                pointLabelGroups++;
#endif
                            }
                            catch (System.Exception ex)
                            {
                                if (creationNote.Length < 300)
                                    creationNote += " 建走廊点标注组失败: " + ex.GetType().Name + ": "
                                                  + Truncate(ex.Message, 60) + ";";
                            }
                        }
                }
                tr.Commit();
            }

            // 网格摆放：按桩号排序，列优先，组间向下接续
            using (var tr = db.TransactionManager.StartTransaction())
            {
                CivSampleLineGroup slg = FindSampleLineGroup(tr, civ, alName, groupName);
                var views = new List<KeyValuePair<double, ObjectId>>();
                foreach (ObjectId slId in slg.GetSampleLineIds())
                {
                    var sl = (CivSampleLine)tr.GetObject(slId, OpenMode.ForRead);
                    foreach (ObjectId svId in sl.GetSectionViewIds())
                        views.Add(new KeyValuePair<double, ObjectId>(sl.Station, svId));
                }
                views.Sort((p, q) => p.Key.CompareTo(q.Key));

                if (rows < 1) rows = 1;
                if (cols < 1) cols = 1;
                int perGroup = rows * cols;
                for (int i = 0; i < views.Count; i++)
                {
                    int grp = i / perGroup, g = i % perGroup;
                    int col = g / rows;
                    int row = grp * rows + (g % rows);
                    var target = new Point3d(
                        basePoint.X + col * colSpacing,
                        basePoint.Y - row * rowSpacing - grp * groupSpacing,
                        0);
                    var sv = (Autodesk.Civil.DatabaseServices.SectionView)tr.GetObject(views[i].Value, OpenMode.ForWrite);
                    sv.TransformBy(Matrix3d.Displacement(target - sv.Location));
                }
                tr.Commit();
            }

            // 体积表格（每张挂一张；需先算过材质列表，缺了就跳过，不影响断面图）
            int tables = 0;
            string tableNote = "未要求";
            if (wantVolTable)
            {
                // ⚠ 体积表格必须**一张图一个事务**，且用完释放 VolumeTables 包装对象。
                // 全部塞进一个事务里能建出来，但会留下没关的写句柄，
                // 后面 save_dwg 直接报 eWasOpenForWrite（实测：关掉体积表格就能存）。
                Guid mlGuid = Guid.Empty;
                var svIds = new List<ObjectId>();
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    CivSampleLineGroup slg = FindSampleLineGroup(tr, civ, alName, groupName);
                    foreach (CivQtoMaterialList ml in slg.MaterialLists) { mlGuid = ml.Guid; break; }
                    foreach (ObjectId slId in slg.GetSampleLineIds())
                    {
                        var sl = (CivSampleLine)tr.GetObject(slId, OpenMode.ForRead);
                        foreach (ObjectId svId in sl.GetSectionViewIds()) svIds.Add(svId);
                    }
                    tr.Commit();
                }

                if (mlGuid == Guid.Empty) tableNote = "该组没有材质列表，已跳过（先跑 compute_quantities）";
                else
                {
                    string firstErr = null;
                    foreach (ObjectId svId in svIds)
                    {
                        using (var tr = db.TransactionManager.StartTransaction())
                        {
                            try
                            {
                                var sv = (Autodesk.Civil.DatabaseServices.SectionView)
                                         tr.GetObject(svId, OpenMode.ForWrite);   // ForRead 会硬崩进程
                                var vt = sv.VolumeTables;
                                vt.SectionViewAnchorType =
                                    Autodesk.Civil.DatabaseServices.SectionViewVolumeTableAnchorType.TopRight;
                                vt.TableAnchorType =
                                    Autodesk.Civil.DatabaseServices.SectionViewVolumeTableAnchorType.TopLeft;
                                vt.OffsetX = 5;
                                vt.OffsetY = 0;
                                // CreateVolumeTable 返回新建表格的 ObjectId：它是以**写打开**的状态
                                // 交回来的，不显式关掉就一直挂着 —— save_dwg 报 eWasOpenForWrite，
                                // 存出来的 DWG 打开时 ErrorStatus=434（实测）。
                                // 一图一事务解决不了这个，因为句柄不归本事务管。
                                ObjectId vtId = vt.CreateVolumeTable(
                                    Autodesk.Civil.DatabaseServices.VolumeTableType.TotalVolume, mlGuid);
                                if (!vtId.IsNull)
                                {
                                    try
                                    {
                                        DBObject tbl = vtId.Open(OpenMode.ForWrite);
                                        tbl.Close();          // 关掉 Civil 交回来的写句柄
                                    }
                                    catch (System.Exception) { }
                                }
                                tables++;
                                tr.Commit();
                            }
                            catch (System.Exception ex)
                            {
                                if (firstErr == null) firstErr = ex.Message;
                                tr.Abort();
                            }
                        }
                    }
                    tableNote = firstErr == null ? "全部成功" : ("首个失败: " + Truncate(firstErr, 80));

                    // ⚠ 建完体积表格后，把断面图对象的写句柄逐个关掉。
                    // sv.VolumeTables 这个包装对象会让 SectionView 在事务提交后**仍然处于写打开**，
                    // 不关的话 save_dwg 抛 eWasOpenForWrite，存出的 DWG 打开时 ErrorStatus=434。
                    // 只做「一图一事务」堵不住 —— 句柄不归事务管，得用老式 Open/Close 显式收。
                    int reclaimed = 0;
                    foreach (ObjectId svId in svIds)
                    {
                        try
                        {
                            DBObject o = svId.Open(OpenMode.ForWrite);
                            o.Close();
                            reclaimed++;
                        }
                        catch (System.Exception) { }
                    }
                    tableNote += "；回收写句柄 " + reclaimed + "/" + svIds.Count;
                }
            }

            return new JsonObject
            {
                ["section_views"] = count,
                ["old_erased"] = erased,
                ["grid"] = rows + " 行 × " + cols + " 列/组，列距 " + colSpacing
                         + " 行距 " + rowSpacing + " 组间距 " + groupSpacing,
                ["base_point"] = Math.Round(bx, 3) + ", " + Math.Round(by, 3),
                ["elev_range"] = elevMin < elevMax ? (elevMin + " ~ " + elevMax) : "自动",
                ["offset_range"] = "左 " + offLeft + " / 右 " + offRight,
                ["code_set_applied_sections"] = secStyled,
                ["corridor_body_sections"] = corridorBodySections,
                ["corridor_geometry_check"] = "按旧操作台基线生成；不以Section包络值判定",
                ["code_set_mapping"] = codeMappingResult,
                ["corridor_surface_sections"] = corridorSurfaceSections,
                ["corridor_display_overrides"] = corridorDisplayOverrides,
                ["surface_sections_styled"] = surfStyled,
                ["creation_mode"] = creationMode,
                ["creation_note"] = creationNote,
                ["layout_updated_groups"] = layoutUpdated,
                ["corridor_code_set_before"] = corridorCodeSetBefore,
                ["corridor_code_set_applied"] = corridorCodeSetSet,
                ["corridor_point_label_groups"] = pointLabelGroups,
                ["volume_tables"] = tables,
                ["volume_table_note"] = tableNote
            };
        }

        static CivSampleLineGroup FindSampleLineGroup(Transaction tr, CivDoc civ, string alName, string groupName)
        {
            CivAlignment al = FindAlignment(tr, civ, alName);
            if (al == null) return null;
            foreach (ObjectId gid in al.GetSampleLineGroupIds())
            {
                var g = (CivSampleLineGroup)tr.GetObject(gid, OpenMode.ForRead);
                if (g.Name == groupName) return g;
            }
            return null;
        }

        // ===================== 出图前置：模型空间比例 =====================
        // Civil 3D 的"模型空间比例"就是注释比例 CANNOSCALE：标签、符号、图框大小全跟它走。
        // 出图前先定死它（如 1:500），后面图框尺寸和断面图网格间距才有依据。

        static JsonNode SetScale(JsonObject a, Document doc)
        {
            double drawingUnits = GetDouble(a, "scale", 0);      // 1:500 就传 500
            string scaleName = GetString(a, "name", null);
            if (drawingUnits <= 0 && string.IsNullOrEmpty(scaleName))
                throw new InvalidOperationException("给 scale（1:500 就传 500）或 name（比例名，如 \"1:500\"）。");
            if (string.IsNullOrEmpty(scaleName)) scaleName = "1:" + drawingUnits.ToString("0.###");

            Database db = doc.Database;
            var ocm = db.ObjectContextManager;
            var coll = ocm.GetContextCollection("ACDB_ANNOTATIONSCALES");
            if (coll == null) throw new InvalidOperationException("图里没有注释比例集合。");

            var available = new JsonArray();
            foreach (Autodesk.AutoCAD.DatabaseServices.ObjectContext c in coll)
                available.Add(c.Name);

            bool created = false;
            var ctx = coll.GetContext(scaleName) as AnnotationScale;
            if (ctx == null)
            {
                if (drawingUnits <= 0)
                    throw new InvalidOperationException("图里没有比例 '" + scaleName + "'，要新建请同时给 scale。");
                var sc = new AnnotationScale
                {
                    Name = scaleName,
                    PaperUnits = 1.0,
                    DrawingUnits = drawingUnits
                };
                coll.AddContext(sc);
                created = true;
                ctx = coll.GetContext(scaleName) as AnnotationScale;
                available.Add(scaleName);
            }
            if (ctx == null) throw new InvalidOperationException("比例 '" + scaleName + "' 建了但取不回来。");

            db.Cannoscale = ctx;

            // Civil 3D 的标注（断面图/纵断面图的标签与带）**不看 CANNOSCALE**，
            // 它们按「图形设置 → 单位和比例 → 比例」缩放。只设 CANNOSCALE 时字号纹丝不动
            // ——实测四种设法（不设 / scale:500 / name 半角 / name 全角，含先设再建断面图）
            // 字号完全一样，只有比例注记会跟着 CANNOSCALE 变。所以两个都得设。
            JsonNode civilBefore = null, civilAfter = null;
            string civilNote = null;
            try
            {
                CivDoc civ = Civ(db);
                var uz = civ.Settings.DrawingSettings.UnitZoneSettings;
                civilBefore = uz.DrawingScale;
                // 米制图纸：注释比例 1:500 存成「纸 1 : 图 0.5」，Civil 图形比例要的也是 0.5（不是 500）。
                // 项目A配方卡实测：0.5 → 注记「横向1:500」；设成 500 → 注记「1:500000」且全部标注字号 ×1000
                //（2026-09-05 工单回归三类图全中招）。缺省按注释比例同款比值，要别的显式传 drawing_scale。
                double want = ctx.PaperUnits > 0 ? ctx.DrawingUnits / ctx.PaperUnits : 0;
                double target = GetDouble(a, "drawing_scale", want);
                if (target > 0)
                {
                    uz.DrawingScale = target;
                    civilAfter = uz.DrawingScale;
                }
                else civilNote = "算不出 Civil 图形比例，显式传 drawing_scale";
            }
            catch (System.Exception ex)
            { civilNote = "Civil 图形比例没设上：" + ex.GetType().Name + ": " + Truncate(ex.Message, 120); }

            return new JsonObject
            {
                ["scale"] = ctx.Name,
                ["paper_units"] = ctx.PaperUnits,
                ["drawing_units"] = ctx.DrawingUnits,
                ["created"] = created,
                ["civil_drawing_scale_before"] = civilBefore,
                ["civil_drawing_scale_after"] = civilAfter,
                ["civil_note"] = civilNote,
                ["available"] = available
            };
        }

        // ===================== 出图后置：Civil 对象导出为纯 CAD =====================
        // Civil 3D 对象（路线/断面图/曲面）在别的机器、别的软件里是 proxy，打印也不稳。
        // `-EXPORTTOAUTOCAD` 把它们炸成纯 CAD 实体另存新文件（原图不动）。
        // 注意：带年份的 AECEXPORTTOAUTOCAD20xx 只有完整 GUI 有，acc 里必须用带连字符的这个。

        static JsonNode ExportToAutocad(JsonObject a, Document doc)
        {
            string outPath = GetString(a, "out", null);
            if (string.IsNullOrEmpty(outPath))
                throw new InvalidOperationException("缺少参数 out（导出的纯 CAD dwg 路径）。");
            if (!Path.IsPathRooted(outPath))
                throw new InvalidOperationException("out 必须是绝对路径：" + outPath);
            if (File.Exists(outPath) && !GetBool(a, "overwrite", false))
                throw new InvalidOperationException("文件已存在，拒绝覆盖：" + outPath + "（确需覆盖传 overwrite:true）");

            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(outPath)) File.Delete(outPath);   // 命令自己不覆盖，先清掉

            string version = GetString(a, "version", "2018");   // 2010 遇到未炸开的 AEC 会转 proxy 并警告
            var ed = doc.Editor;

            // 某些 AEC 对象（已确诊：断面 QTO 体积表）会让 -EXPORTTOAUTOCAD 内部
            // erase failed (eLockViolation) 而整体流产。带表出图的解法：save_dwg 落盘后
            // （底稿保住活表），在同一会话内把这些类先就地炸成普通图元，再导出。
            // explode_classes 传 DXF 类名数组（如 AECC_SECTION_VIEW_QUANTITY_TAKEOFF_TABLE）。
            int exploded = 0, explodeFailed = 0;
            var explodeClasses = a["explode_classes"] as JsonArray;
            if (explodeClasses != null && explodeClasses.Count > 0)
            {
                Database dbx = doc.Database;
                var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var n in explodeClasses) want.Add(n.ToString());
                var targets = new List<ObjectId>();
                using (var tr = dbx.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(dbx.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                        if (want.Contains(id.ObjectClass.DxfName)) targets.Add(id);
                    tr.Commit();
                }
                foreach (ObjectId tid in targets)
                {
                    using (var tr = dbx.TransactionManager.StartTransaction())
                    {
                        try
                        {
                            var ent = (Entity)tr.GetObject(tid, OpenMode.ForWrite);
                            var parts = new DBObjectCollection();
                            ent.Explode(parts);
                            var bt2 = (BlockTable)tr.GetObject(dbx.BlockTableId, OpenMode.ForRead);
                            var msr = (BlockTableRecord)tr.GetObject(bt2[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                            foreach (DBObject p in parts)
                            {
                                var pe = p as Entity;
                                if (pe == null) { p.Dispose(); continue; }
                                msr.AppendEntity(pe);
                                tr.AddNewlyCreatedDBObject(pe, true);
                            }
                            ent.Erase();
                            exploded++;
                            tr.Commit();
                        }
                        catch { explodeFailed++; tr.Abort(); }
                    }
                }
            }

            // 命令行版路径要用正斜杠，反斜杠在命令流里会被当转义。
            // 提示序列（2026-07-21 实测记录）：
            //   Export options [Format\Bind\...] <Enter for filename>:   ← 这一步必须给个空回车才进文件名
            //   Export drawing name <默认>:
            // 漏掉那个空回车，命令会把路径当成非法选项一直重问 → headless 永久挂住（本次实测踩过）。
            string cmdPath = outPath.Replace('\\', '/');
            if (string.Equals(version, "2018", StringComparison.OrdinalIgnoreCase))
                ed.Command("_-EXPORTTOAUTOCAD", "", cmdPath);          // 默认格式，直接回车进文件名
            else
                ed.Command("_-EXPORTTOAUTOCAD", "_F", version, "", cmdPath);

            if (!File.Exists(outPath))
                throw new InvalidOperationException("命令跑完但没生成文件：" + outPath +
                    "（提示序列可能与预期不符，先在 acc 里手动跑一次 -EXPORTTOAUTOCAD 看提示）");

            return new JsonObject
            {
                ["exported"] = outPath,
                ["bytes"] = new FileInfo(outPath).Length,
                ["version"] = version,
                ["pre_exploded"] = exploded,
                ["pre_explode_failed"] = explodeFailed,
                ["source"] = SafeFile(doc.Database)
            };
        }

        // 给**既有**断面视图挂材质体积表（不重建视图）。为什么单独成节点：
        // ① 表在视图创建时挂、之后 arrange 搬动视图，无头下锚定不跟着走，表留在老位置页外；
        //    正确工序 = 先排版后挂表，本节点就是"后挂表"那一步。
        // ② create_section_views 是删了重建，会毁掉排版/点标注；本节点只动表。
        // clear_all=true 时先删模型空间全部断面 QTO 表（链里第一条路线传一次，防旧表残留）。
        static JsonNode RunNodeAddVolumeTables(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string groupName = GetString(a, "group", null);
            bool clearAll = GetBool(a, "clear_all", false);
            bool create = GetBool(a, "create", true);      // false = 只清场不建表（AEC 表出不了图时的卸载入口）
            double offX = GetDouble(a, "offset_x", 5);
            double offY = GetDouble(a, "offset_y", 0);

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            int cleared = 0;
            if (clearAll)
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    var kill = new List<ObjectId>();
                    foreach (ObjectId id in ms)
                        if (id.ObjectClass.DxfName == "AECC_SECTION_VIEW_QUANTITY_TAKEOFF_TABLE")
                            kill.Add(id);
                    foreach (ObjectId id in kill)
                    {
                        try
                        {
                            var o = tr.GetObject(id, OpenMode.ForWrite);
                            o.Erase();
                            cleared++;
                        }
                        catch { }
                    }
                    tr.Commit();
                }
            }

            Guid mlGuid = Guid.Empty;
            var svIds = new List<ObjectId>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                // 组名口径与 create_section_views 相同：缺省找 <路线>_采样线组，
                // 找不到且路线只有一个组则用它。
                CivSampleLineGroup slg = FindSampleLineGroup(
                    tr, civ, alName, string.IsNullOrEmpty(groupName) ? alName + "_采样线组" : groupName);
                if (slg == null && string.IsNullOrEmpty(groupName))
                {
                    CivAlignment al0 = FindAlignment(tr, civ, alName);
                    if (al0 != null)
                    {
                        var gids = al0.GetSampleLineGroupIds();
                        if (gids.Count == 1)
                            slg = (CivSampleLineGroup)tr.GetObject(gids[0], OpenMode.ForRead);
                    }
                }
                if (slg == null)
                    throw new InvalidOperationException(
                        "路线 '" + alName + "' 找不到采样线组"
                        + (string.IsNullOrEmpty(groupName) ? "（默认名 " + alName + "_采样线组）" : "「" + groupName + "」")
                        + "。");
                foreach (CivQtoMaterialList ml in slg.MaterialLists) { mlGuid = ml.Guid; break; }
                foreach (ObjectId slId in slg.GetSampleLineIds())
                {
                    var sl = (CivSampleLine)tr.GetObject(slId, OpenMode.ForRead);
                    foreach (ObjectId svId in sl.GetSectionViewIds()) svIds.Add(svId);
                }
                tr.Commit();
            }
            if (mlGuid == Guid.Empty)
                throw new InvalidOperationException(
                    "路线 '" + alName + "' 的采样线组没有材质列表，先跑 compute_quantities。");
            if (!create)
                return new JsonObject
                {
                    ["alignment"] = alName,
                    ["tables_created"] = 0,
                    ["section_views"] = svIds.Count,
                    ["cleared_old_tables"] = cleared,
                    ["note"] = "只清场（create=false）"
                };

            int made = 0;
            string firstErr = null;
            foreach (ObjectId svId in svIds)
            {
                // ⚠ 一张图一个事务；ForRead 会硬崩进程（沿用 create_section_views 的教训）
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    try
                    {
                        var sv = (Autodesk.Civil.DatabaseServices.SectionView)
                                 tr.GetObject(svId, OpenMode.ForWrite);
                        var vt = sv.VolumeTables;
                        vt.SectionViewAnchorType =
                            Autodesk.Civil.DatabaseServices.SectionViewVolumeTableAnchorType.TopRight;
                        vt.TableAnchorType =
                            Autodesk.Civil.DatabaseServices.SectionViewVolumeTableAnchorType.TopLeft;
                        vt.OffsetX = offX;
                        vt.OffsetY = offY;
                        vt.CreateVolumeTable(
                            Autodesk.Civil.DatabaseServices.VolumeTableType.TotalVolume, mlGuid);
                        made++;
                        tr.Commit();
                    }
                    catch (System.Exception ex)
                    {
                        if (firstErr == null) firstErr = ex.Message;
                        tr.Abort();
                    }
                }
            }

            return new JsonObject
            {
                ["alignment"] = alName,
                ["tables_created"] = made,
                ["section_views"] = svIds.Count,
                ["cleared_old_tables"] = cleared,
                ["note"] = firstErr == null ? "全部成功" : ("首个失败: " + Truncate(firstErr, 80))
            };
        }

        // 自画断面体积表（纯 CAD 线+文字，非 AEC 表）。为什么不用 Civil 的 QTO 表：
        // AECC 断面 QTO 表让 -EXPORTTOAUTOCAD 内部 erase failed (eLockViolation) 整体流产，
        // 且 accore 里托管 Explode 返回空集、原生 EXPLODE 拒炸——无头链里根本带不出去。
        // 厂里拆堤链早有「方量标注画在断面上」的先例（compute_embankment_demolition），本节点同款思路。
        // 数据源与 export_quantities 相同：材质列表逐桩号增量/累计挖方。
        // 实体带 XData(C3DF_SVT) 认亲，clear=true 重跑先清旧表，幂等。
        const string SvtRegApp = "C3DF_SVT";

        static JsonNode RunNodeDrawSectionVolumeTables(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string groupName = GetString(a, "group", null);
            double scale = GetDouble(a, "scale", 500);
            double k = scale / 1000.0;                       // mm → 模型米
            double textH = GetDouble(a, "text_mm", 2.5) * k;
            double rowH = GetDouble(a, "row_mm", 5.0) * k;
            double col1 = GetDouble(a, "col1_mm", GetDouble(a, "label_col_mm", 16.0)) * k;   // 项目列
            double col2 = GetDouble(a, "col2_mm", 30.0) * k;                                  // 断面面积列
            double col3 = GetDouble(a, "col3_mm", 26.0) * k;                                  // 挖方量列
            double offX = GetDouble(a, "offset_x_mm", 2.0) * k;
            double offY = GetDouble(a, "offset_y_mm", 0.0) * k;
            bool clear = GetBool(a, "clear", true);
            string layerName = GetString(a, "layer", "C3DF-体积表");
            string targetDwg = GetString(a, "target_dwg", null);
            string textStyleName = GetString(a, "text_style", null);
            string stationPrefix = GetString(a, "station_prefix", "桩号 ");
            string headArea = GetString(a, "header_area", "断面面积（m²）");
            string headVol = GetString(a, "header_volume", "挖方量（m³）");
            string headItem = GetString(a, "header_item", "项目");
            string rowLabel = GetString(a, "row_label", "挖方");
            int limit = (int)GetDouble(a, "limit", 0);
            bool dryRun = GetBool(a, "dry_run", false);

            if (!string.IsNullOrEmpty(targetDwg))
            {
                if (!Path.IsPathRooted(targetDwg))
                    throw new InvalidOperationException("target_dwg 必须是绝对路径：" + targetDwg);
                if (!File.Exists(targetDwg))
                    throw new InvalidOperationException("找不到目标图纸：" + targetDwg);
            }

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            // ---------- 阶段 A：在 Civil 图里取数据与位置（只读） ----------
            // station -> { 挖方断面面积 m², 增量挖方 m³ }
            var dataByStation = new List<KeyValuePair<double, double[]>>();
            var views = new List<KeyValuePair<double, ObjectId>>();
            bool areaFromApi = true;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                CivSampleLineGroup slg = FindSampleLineGroup(
                    tr, civ, alName, string.IsNullOrEmpty(groupName) ? alName + "_采样线组" : groupName);
                if (slg == null && string.IsNullOrEmpty(groupName))
                {
                    CivAlignment al0 = FindAlignment(tr, civ, alName);
                    if (al0 != null)
                    {
                        var gids = al0.GetSampleLineGroupIds();
                        if (gids.Count == 1)
                            slg = (CivSampleLineGroup)tr.GetObject(gids[0], OpenMode.ForRead);
                    }
                }
                if (slg == null)
                    throw new InvalidOperationException("路线 '" + alName + "' 找不到采样线组。");
                Guid mlGuid = Guid.Empty;
                foreach (CivQtoMaterialList ml in slg.MaterialLists) { mlGuid = ml.Guid; break; }
                if (mlGuid == Guid.Empty)
                    throw new InvalidOperationException(
                        "路线 '" + alName + "' 的采样线组没有材质列表，先跑 compute_quantities。");

                var result = slg.GetTotalVolumeResultDataForMaterialList(mlGuid);
                foreach (CivQtoSectionalResult sec in result.GetResultsAlongSampleLines())
                {
                    double area = double.NaN;
                    try { area = sec.AreaResult.CutArea; }
                    catch { areaFromApi = false; }
                    dataByStation.Add(new KeyValuePair<double, double[]>(
                        sec.Station, new[] { area, sec.VolumeResult.IncrementalCutVolume }));
                }
                foreach (ObjectId slId in slg.GetSampleLineIds())
                {
                    var sl = (CivSampleLine)tr.GetObject(slId, OpenMode.ForRead);
                    foreach (ObjectId svId in sl.GetSectionViewIds())
                        views.Add(new KeyValuePair<double, ObjectId>(sl.Station, svId));
                }
                tr.Commit();
            }

            // 每张表一行：{ 表左 x, 表顶 y, 桩号, 面积, 体积 }
            var tables = new List<double[]>();
            int noData = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var kv in views)
                {
                    double station = kv.Key;
                    double area = double.NaN, vol = double.NaN;
                    foreach (var vv in dataByStation)
                        if (Math.Abs(vv.Key - station) < 0.01) { area = vv.Value[0]; vol = vv.Value[1]; break; }
                    if (double.IsNaN(vol)) { noData++; continue; }
                    var sv = (Autodesk.Civil.DatabaseServices.SectionView)
                             tr.GetObject(kv.Value, OpenMode.ForRead);
                    Extents3d ext = sv.GeometricExtents;
                    tables.Add(new[] { ext.MaxPoint.X + offX, ext.MaxPoint.Y + offY, station, area, vol });
                }
                tr.Commit();
            }
            tables.Sort((p, q) => p[2].CompareTo(q[2]));
            int found = tables.Count;
            if (limit > 0 && tables.Count > limit) tables = tables.GetRange(0, limit);

            var sample = new JsonArray();
            for (int i = 0; i < tables.Count && i < 3; i++)
                sample.Add(new JsonObject
                {
                    ["station"] = tables[i][2],
                    ["x"] = Math.Round(tables[i][0], 3),
                    ["y_top"] = Math.Round(tables[i][1], 3),
                    ["area"] = double.IsNaN(tables[i][3]) ? (JsonNode)null : Math.Round(tables[i][3], 2),
                    ["volume"] = Math.Round(tables[i][4], 2)
                });

            if (dryRun)
                return new JsonObject
                {
                    ["alignment"] = alName,
                    ["mode"] = "dry_run（没画任何东西）",
                    ["section_views"] = views.Count,
                    ["tables_planned"] = tables.Count,
                    ["views_without_data"] = noData,
                    ["area_from_api"] = areaFromApi,
                    ["table_w"] = Math.Round(col1 + col2 + col3, 3),
                    ["table_h"] = Math.Round(3 * rowH, 3),
                    ["first"] = sample
                };

            // ---------- 阶段 B：落图 ----------
            int made = 0, cleared = 0;
            string writtenTo, note = null;
            if (string.IsNullOrEmpty(targetDwg))
            {
                made = PaintSvtTables(db, tables, clear, layerName, textStyleName,
                                      textH, rowH, col1, col2, col3,
                                      stationPrefix, headItem, headArea, headVol, rowLabel, out cleared);
                writtenTo = "(当前图纸内存，落盘走 save_dwg)";
            }
            else
            {
                using (Database tdb = new Database(false, true))
                {
                    tdb.ReadDwgFile(targetDwg, FileOpenMode.OpenForReadAndAllShare, true, null);
                    tdb.CloseInput(true);
                    made = PaintSvtTables(tdb, tables, clear, layerName, textStyleName,
                                          textH, rowH, col1, col2, col3,
                                          stationPrefix, headItem, headArea, headVol, rowLabel, out cleared);
                    writtenTo = targetDwg;
                    try
                    {
                        tdb.SaveAs(targetDwg, DwgVersion.Current);
                    }
                    catch (System.Exception ex)
                    {
                        // 厂规：目标被占用只许在原文件旁留一个「-被占用待替换」件
                        string alt = Path.Combine(
                            Path.GetDirectoryName(targetDwg),
                            Path.GetFileNameWithoutExtension(targetDwg) + "-被占用待替换.dwg");
                        tdb.SaveAs(alt, DwgVersion.Current);
                        writtenTo = alt;
                        note = "原图写不进（" + Truncate(ex.Message, 60) + "），成果落在 -被占用待替换 件，关图后自行换位。";
                    }
                }
            }

            return new JsonObject
            {
                ["alignment"] = alName,
                ["tables_drawn"] = made,
                ["tables_found"] = found,
                ["cleared"] = cleared,
                ["views_without_data"] = noData,
                ["area_from_api"] = areaFromApi,
                ["layer"] = layerName,
                ["written_to"] = writtenTo,
                ["first"] = sample,
                ["note"] = note ?? "全部成功"
            };
        }

        // 把表画进给定 Database 的模型空间（当前图或外部成品图都走这里）。
        // 表式（项目B初设 1201 定稿）：三行，首行跨列写桩号，二行表头，三行数据。
        //   ┌──────────────────────────────┐
        //   │          桩号 0+000.00        │
        //   ├──────┬────────────┬──────────┤
        //   │ 项目 │断面面积(m²)│挖方量(m³)│
        //   ├──────┼────────────┼──────────┤
        //   │ 挖方 │    9.57    │   0.00   │
        //   └──────┴────────────┴──────────┘
        static int PaintSvtTables(Database tdb, List<double[]> tables, bool clear,
                                  string layerName, string textStyleName,
                                  double textH, double rowH, double col1, double col2, double col3,
                                  string stationPrefix, string headItem, string headArea, string headVol,
                                  string rowLabel, out int cleared)
        {
            // 外部 Database 里建实体，必须把 WorkingDatabase 切过去，
            // 否则 SetDatabaseDefaults 拿的是当前图的默认值，AppendEntity 抛 eWrongDatabase。
            Database prevWorking = HostApplicationServices.WorkingDatabase;
            bool switched = !ReferenceEquals(prevWorking, tdb);
            if (switched) HostApplicationServices.WorkingDatabase = tdb;
            try
            {
                return PaintSvtTablesCore(tdb, tables, clear, layerName, textStyleName,
                                          textH, rowH, col1, col2, col3,
                                          stationPrefix, headItem, headArea, headVol, rowLabel, out cleared);
            }
            finally
            {
                if (switched) HostApplicationServices.WorkingDatabase = prevWorking;
            }
        }

        static int PaintSvtTablesCore(Database tdb, List<double[]> tables, bool clear,
                                  string layerName, string textStyleName,
                                  double textH, double rowH, double col1, double col2, double col3,
                                  string stationPrefix, string headItem, string headArea, string headVol,
                                  string rowLabel, out int cleared)
        {
            cleared = 0;

            // 图层 + RegApp
            using (var tr = tdb.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(tdb.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(layerName))
                {
                    lt.UpgradeOpen();
                    var ltr = new LayerTableRecord { Name = layerName };
                    lt.Add(ltr);
                    tr.AddNewlyCreatedDBObject(ltr, true);
                }
                var rat = (RegAppTable)tr.GetObject(tdb.RegAppTableId, OpenMode.ForRead);
                if (!rat.Has(SvtRegApp))
                {
                    rat.UpgradeOpen();
                    var r = new RegAppTableRecord { Name = SvtRegApp };
                    rat.Add(r);
                    tr.AddNewlyCreatedDBObject(r, true);
                }
                tr.Commit();
            }

            if (clear)
            {
                using (var tr = tdb.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(tdb.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        Entity ent0;
                        try { ent0 = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                        catch { continue; }
                        if (ent0 == null) continue;
                        // 认亲两条：本层上的（图层是本节点专用）或带 C3DF_SVT XData 的。
                        // 只认 XData 不够——纯 RegApp 名的空 XData 存盘会被丢，旧表就清不掉了。
                        bool mine = string.Equals(ent0.Layer, layerName, StringComparison.OrdinalIgnoreCase);
                        if (!mine && ent0.GetXDataForApplication(SvtRegApp) == null) continue;
                        ent0.UpgradeOpen();
                        ent0.Erase();
                        cleared++;
                    }
                    tr.Commit();
                }
            }

            int made = 0;
            double w = col1 + col2 + col3, h = 3 * rowH;
            using (var tr = tdb.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(tdb.BlockTableId, OpenMode.ForRead);
                var msr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var lt = (LayerTable)tr.GetObject(tdb.LayerTableId, OpenMode.ForRead);
                ObjectId layerId = lt[layerName];

                // 文字样式：优先参数指定，其次图里的 -黑体（归化链留下的中文样式），再退当前默认
                ObjectId styleId = ObjectId.Null;
                var tst = (TextStyleTable)tr.GetObject(tdb.TextStyleTableId, OpenMode.ForRead);
                if (!string.IsNullOrEmpty(textStyleName))
                {
                    if (!tst.Has(textStyleName))
                        throw new InvalidOperationException("图里没有文字样式 '" + textStyleName + "'。");
                    styleId = tst[textStyleName];
                }

                var xdata = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, SvtRegApp),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, "断面体积表"));

                Action<Entity> put = e =>
                {
                    e.SetDatabaseDefaults();
                    e.LayerId = layerId;
                    msr.AppendEntity(e);
                    tr.AddNewlyCreatedDBObject(e, true);
                    e.XData = xdata;
                };
                Action<string, double, double> txtMid = (s, cx, cy) =>
                {
                    var t = new DBText
                    {
                        TextString = s,
                        Height = textH,
                        Position = new Point3d(cx, cy, 0)
                    };
                    if (!styleId.IsNull) t.TextStyleId = styleId;
                    t.HorizontalMode = TextHorizontalMode.TextCenter;
                    t.VerticalMode = TextVerticalMode.TextVerticalMid;
                    t.AlignmentPoint = new Point3d(cx, cy, 0);
                    put(t);
                };

                foreach (var row in tables)
                {
                    double x0 = row[0], yTop = row[1], station = row[2], area = row[3], vol = row[4];
                    double y0 = yTop - h;
                    double yR1 = yTop - rowH;          // 首行底
                    double yR2 = yTop - 2 * rowH;      // 表头行底

                    var pl = new Polyline(4) { Closed = true };
                    pl.AddVertexAt(0, new Point2d(x0, y0), 0, 0, 0);
                    pl.AddVertexAt(1, new Point2d(x0 + w, y0), 0, 0, 0);
                    pl.AddVertexAt(2, new Point2d(x0 + w, yTop), 0, 0, 0);
                    pl.AddVertexAt(3, new Point2d(x0, yTop), 0, 0, 0);
                    put(pl);
                    put(new Line(new Point3d(x0, yR1, 0), new Point3d(x0 + w, yR1, 0)));
                    put(new Line(new Point3d(x0, yR2, 0), new Point3d(x0 + w, yR2, 0)));
                    // 竖线只跨下两行，首行是合并单元格
                    put(new Line(new Point3d(x0 + col1, y0, 0), new Point3d(x0 + col1, yR1, 0)));
                    put(new Line(new Point3d(x0 + col1 + col2, y0, 0), new Point3d(x0 + col1 + col2, yR1, 0)));

                    int km = (int)Math.Floor(station / 1000.0);
                    string stTxt = km.ToString() + "+" + (station - km * 1000.0).ToString("000.00");
                    double cx1 = x0 + col1 / 2;
                    double cx2 = x0 + col1 + col2 / 2;
                    double cx3 = x0 + col1 + col2 + col3 / 2;
                    txtMid(stationPrefix + stTxt, x0 + w / 2, yTop - rowH / 2);
                    txtMid(headItem, cx1, yR1 - rowH / 2);
                    txtMid(headArea, cx2, yR1 - rowH / 2);
                    txtMid(headVol, cx3, yR1 - rowH / 2);
                    txtMid(rowLabel, cx1, yR2 - rowH / 2);
                    txtMid(double.IsNaN(area) ? "—" : area.ToString("0.00"), cx2, yR2 - rowH / 2);
                    txtMid(vol.ToString("0.00"), cx3, yR2 - rowH / 2);
                    made++;
                }
                tr.Commit();
            }
            return made;
        }

        // ===================== 出图：按比例批量插图框 =====================
        // 图框在模型空间里的实际大小 = 纸张尺寸(mm) × 比例分母 ÷ 1000（米）。
        // A3 横放 420×297，1:500 → 210 m × 148.5 m 一张。所以必须先 set_scale 再插图框。
        // 幂等：本操作插的图框带 XData 标记，重跑先删上次的，不会越插越多。

        const string TitleBlockXdataApp = "C3DF_TITLEBLOCK";

        static JsonNode InsertTitleBlocks(JsonObject a, Document doc)
        {
            string blockName = Need(a, "block");
            string fromDwg = GetString(a, "from_dwg", null);
            int count = (int)GetDouble(a, "count", 1);
            if (count < 1) throw new InvalidOperationException("count 必须 ≥ 1。");
            int cols = (int)GetDouble(a, "cols", 1);
            if (cols < 1) cols = 1;
            double x0 = GetDouble(a, "x", 0), y0 = GetDouble(a, "y", 0);
            string layer = GetString(a, "layer", null);

            Database db = doc.Database;

            // 比例：默认取图纸当前注释比例（set_scale 设的那个）
            double scale = GetDouble(a, "scale", 0);
            string scaleFrom = "参数";
            if (scale <= 0)
            {
                try
                {
                    var cs = db.Cannoscale;
                    if (cs != null && cs.PaperUnits > 0) { scale = cs.DrawingUnits / cs.PaperUnits; scaleFrom = "图纸当前比例 " + cs.Name; }
                }
                catch { }
            }
            if (scale <= 0) throw new InvalidOperationException("取不到比例，先跑 set_scale 或传 scale（1:500 就传 500）。");

            // 纸张（mm）：预设 + 自定义
            double pw = GetDouble(a, "paper_w", 0), ph = GetDouble(a, "paper_h", 0);
            string paper = GetString(a, "paper", "A3");
            if (pw <= 0 || ph <= 0) PaperSizeMm(paper, out pw, out ph);   // 预设不认识就报错；自定义走 paper_w/paper_h
            // 块本身若已按 mm 画好，插入比例就是 scale/1000（图纸单位是米）
            double blockScale = GetDouble(a, "block_scale", scale / 1000.0);
            double frameW = pw * scale / 1000.0;
            double frameH = ph * scale / 1000.0;
            double gapX = GetDouble(a, "gap_x", 0), gapY = GetDouble(a, "gap_y", 0);

            var attrs = a["attributes"] as JsonObject;
            // 属性宽度因子 {标签:因子}：长文字塞窄格的惯用手法，值太长就压扁
            var widthFactors = a["width_factors"] as JsonObject;
            JsonArray placed = null;
            // AdjustAlignment 的经典坑：无头会话里 WorkingDatabase 不是本图时，
            // 非左对齐属性按错误基准摆——图名/图号整体右撇就是它。全程钉住，收尾还原。
            Database prevWdb = HostApplicationServices.WorkingDatabase;
            HostApplicationServices.WorkingDatabase = db;
            try {
            // at 模式：给定逐点位置（通常来自 arrange_section_sheets 的 sheets[].origin_x/y），
            // 忽略 count/cols 网格；插入后把块的外包左下角对齐到给定点（块基点在哪都不怕）。
            // 每个点可带自己的 attributes，覆盖全局同名项——逐框填图名/页码就靠它。
            var atArr = a["at"] as JsonArray;
            if (atArr != null && atArr.Count == 0)
                throw new InvalidOperationException("at 给了但是空数组；要走网格模式就别给 at。");
            int erased = 0, inserted = 0, attrsFilled = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                // 块定义：图里没有就从块库文件克隆一份过来
                if (!bt.Has(blockName))
                {
                    if (string.IsNullOrEmpty(fromDwg))
                        throw new InvalidOperationException("图中没有块 '" + blockName + "'，给 from_dwg 从块库导入（先对块库跑 list_blocks 查名字）。");
                    if (!File.Exists(fromDwg)) throw new InvalidOperationException("找不到块库文件：" + fromDwg);
                    using (var src = new Database(false, true))
                    {
                        src.ReadDwgFile(fromDwg, FileOpenMode.OpenForReadAndAllShare, true, null);
                        src.CloseInput(true);
                        using (var stx = src.TransactionManager.StartTransaction())
                        {
                            var sbt = (BlockTable)stx.GetObject(src.BlockTableId, OpenMode.ForRead);
                            if (!sbt.Has(blockName))
                                throw new InvalidOperationException("块库里没有块 '" + blockName + "'：" + fromDwg);
                            var ids = new ObjectIdCollection { sbt[blockName] };
                            var map = new IdMapping();
                            db.WblockCloneObjects(ids, db.BlockTableId, map, DuplicateRecordCloning.Replace, false);
                            stx.Commit();
                        }
                    }
                    bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    if (!bt.Has(blockName))
                        throw new InvalidOperationException("从块库克隆后仍找不到块 '" + blockName + "'。");
                }
                ObjectId btrId = bt[blockName];

                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                EnsureRegApp(tr, db, TitleBlockXdataApp);

                // 幂等：删掉上次本操作插的图框。
                // 两条腿：XData 标记 + 同名块兜底（erase_same_block，默认开）。
                // 光靠 XData 不行——只挂 RegAppName 不带载荷的 XData 存盘再开就没了，
                // 旧图框找不着、越插越摞（2026-08-16 项目B三轮跑出 174 个图框查实）。
                bool eraseSameName = GetBool(a, "erase_same_block", true);
                foreach (ObjectId id in ms)
                {
                    var old = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (old == null) continue;
                    bool marked = false;
                    var rb = old.GetXDataForApplication(TitleBlockXdataApp);
                    if (rb != null) { rb.Dispose(); marked = true; }
                    if (!marked && eraseSameName)
                    {
                        string bn = null;
                        try
                        {
                            var obtr = (BlockTableRecord)tr.GetObject(
                                old.DynamicBlockTableRecord.IsNull ? old.BlockTableRecord : old.DynamicBlockTableRecord,
                                OpenMode.ForRead);
                            bn = obtr.Name;
                        }
                        catch { }
                        if (!string.Equals(bn, blockName, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    else if (!marked) continue;
                    old.UpgradeOpen();
                    old.Erase();
                    erased++;
                }

                placed = new JsonArray();
                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                int total = atArr != null ? atArr.Count : count;

                // clone_from：整体深克隆成品里的原装块引用（连实例级手调过的属性几何），
                // 逐点复制后擦掉原型。定义造出来的属性不是原样（2026-08-16 项目B 1301 对照实锤）。
                var cloneFrom = a["clone_from"] as JsonObject;
                if (cloneFrom != null && atArr != null)
                {
                    string srcPath = cloneFrom["dwg"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath))
                        throw new InvalidOperationException("clone_from.dwg 不存在：" + srcPath);
                    ObjectId protoId;
                    using (var src = new Database(false, true))
                    {
                        src.ReadDwgFile(srcPath, FileOpenMode.OpenForReadAndAllShare, true, null);
                        src.CloseInput(true);
                        ObjectId found = ObjectId.Null;
                        using (var stx = src.TransactionManager.StartTransaction())
                        {
                            var sbt = (BlockTable)stx.GetObject(src.BlockTableId, OpenMode.ForRead);
                            foreach (ObjectId recId in sbt)
                            {
                                var rec = (BlockTableRecord)stx.GetObject(recId, OpenMode.ForRead);
                                if (!rec.IsLayout) continue;
                                foreach (ObjectId eid in rec)
                                {
                                    var brf = stx.GetObject(eid, OpenMode.ForRead) as BlockReference;
                                    if (brf == null) continue;
                                    string bn2 = null;
                                    try
                                    {
                                        var obtr2 = (BlockTableRecord)stx.GetObject(
                                            brf.DynamicBlockTableRecord.IsNull ? brf.BlockTableRecord : brf.DynamicBlockTableRecord,
                                            OpenMode.ForRead);
                                        bn2 = obtr2.Name;
                                    }
                                    catch { }
                                    if (string.Equals(bn2, blockName, StringComparison.OrdinalIgnoreCase)) { found = eid; break; }
                                }
                                if (!found.IsNull) break;
                            }
                            stx.Commit();
                        }
                        if (found.IsNull)
                            throw new InvalidOperationException("clone_from 图里没找到块 '" + blockName + "' 的引用。");
                        var cids = new ObjectIdCollection { found };
                        var cmap = new IdMapping();
                        src.WblockCloneObjects(cids, ms.ObjectId, cmap, DuplicateRecordCloning.Replace, false);
                        protoId = cmap[found].Value;
                    }
                    var protoBr = (BlockReference)tr.GetObject(protoId, OpenMode.ForRead);
                    double srcScale = protoBr.ScaleFactors.X;
                    for (int i = 0; i < total; i++)
                    {
                        var it2 = atArr[i] as JsonObject;
                        var pos2 = new Point3d(it2["x"].GetValue<double>(), it2["y"].GetValue<double>(), 0);
                        var m2 = new IdMapping();
                        db.DeepCloneObjects(new ObjectIdCollection { protoId }, ms.ObjectId, m2, false);
                        var brC = (BlockReference)tr.GetObject(m2[protoId].Value, OpenMode.ForWrite);
                        double k = srcScale > 1e-12 ? blockScale / srcScale : blockScale;
                        brC.TransformBy(Matrix3d.Scaling(k, brC.Position));
                        Extents3d extC = brC.GeometricExtents;
                        brC.TransformBy(Matrix3d.Displacement(
                            new Vector3d(pos2.X - extC.MinPoint.X, pos2.Y - extC.MinPoint.Y, 0)));
                        brC.XData = new ResultBuffer(
                            new TypedValue((int)DxfCode.ExtendedDataRegAppName, TitleBlockXdataApp),
                            new TypedValue((int)DxfCode.ExtendedDataAsciiString, "C3DF"));
                        placed.Add(Math.Round(pos2.X, 3) + ", " + Math.Round(pos2.Y, 3));
                        inserted++;
                    }
                    ((Entity)tr.GetObject(protoId, OpenMode.ForWrite)).Erase();
                }
                else
                for (int i = 0; i < total; i++)
                {
                    Point3d pos;
                    JsonObject itemAttrs = null;
                    if (atArr != null)
                    {
                        var it = atArr[i] as JsonObject;
                        if (it == null || it["x"] == null || it["y"] == null)
                            throw new InvalidOperationException("at[" + i + "] 缺 x/y。");
                        pos = new Point3d(it["x"].GetValue<double>(), it["y"].GetValue<double>(), 0);
                        itemAttrs = it["attributes"] as JsonObject;
                    }
                    else
                    {
                        int col = i % cols, row = i / cols;
                        pos = new Point3d(x0 + col * (frameW + gapX), y0 - row * (frameH + gapY), 0);
                    }
                    var br = new BlockReference(pos, btrId) { ScaleFactors = new Scale3d(blockScale) };
                    ms.AppendEntity(br);
                    tr.AddNewlyCreatedDBObject(br, true);
                    if (!string.IsNullOrEmpty(layer)) br.Layer = layer;
                    // 载荷不能省：只有 RegAppName 的 XData 存盘重开后取不回来，幂等删除会失明
                    br.XData = new ResultBuffer(
                        new TypedValue((int)DxfCode.ExtendedDataRegAppName, TitleBlockXdataApp),
                        new TypedValue((int)DxfCode.ExtendedDataAsciiString, "C3DF"));

                    if (atArr != null)
                    {
                        // 对齐：给定点 = 块外包的左下角（先量几何、再挪，属性还没挂不掺和）
                        try
                        {
                            Extents3d ext = br.GeometricExtents;
                            var d = new Vector3d(pos.X - ext.MinPoint.X, pos.Y - ext.MinPoint.Y, 0);
                            if (d.Length > 1e-9) br.Position = br.Position + d;
                        }
                        catch { }
                    }

                    if (btr.HasAttributeDefinitions)
                    {
                        foreach (ObjectId eid in btr)
                        {
                            var ad = tr.GetObject(eid, OpenMode.ForRead) as AttributeDefinition;
                            if (ad == null || ad.Constant) continue;
                            var ar = new AttributeReference();
                            ar.SetAttributeFromBlock(ad, br.BlockTransform);
                            JsonNode v = itemAttrs != null && itemAttrs[ad.Tag] != null
                                ? itemAttrs[ad.Tag]
                                : (attrs != null ? attrs[ad.Tag] : null);
                            if (v != null)
                            {
                                // 值里带 {n} 就替换成图号（从 1 开始）
                                ar.TextString = v.ToString().Replace("{n}", (i + 1).ToString());
                                attrsFilled++;
                            }
                            if (widthFactors != null && widthFactors[ad.Tag] != null)
                            {
                                double wf = widthFactors[ad.Tag].GetValue<double>();
                                if (wf > 0) ar.WidthFactor = wf;
                                // 只有改过宽度才需要重摆；无头会话里对没动过的居中属性调
                                // AdjustAlignment 会按错误字宽右移（2026-08-16 项目B「右撇」元凶）
                                try { ar.AdjustAlignment(db); } catch { }
                            }
                            br.AttributeCollection.AppendAttribute(ar);
                            tr.AddNewlyCreatedDBObject(ar, true);
                        }
                    }
                    placed.Add(Math.Round(br.Position.X, 3) + ", " + Math.Round(br.Position.Y, 3));
                    inserted++;
                }
                tr.Commit();
            }

            // ATTSYNC（API 版）：把每个属性的位置/格式按块定义重置（= 界面上的 ATTSYNC），
            // 保留已填的值，最后再补 width_factors——同步会把宽度重置回定义值。
            // 不走 _.ATTSYNC 命令：无头会话里提示序列不稳，实测抛 eInvalidInput。
            bool wantAttsync = GetBool(a, "attsync", true);
            int widthReapplied = 0, attrsSynced = 0;
            if (wantAttsync && inserted > 0)
            {
                using (Transaction tr2 = db.TransactionManager.StartTransaction())
                {
                    var bt2 = (BlockTable)tr2.GetObject(db.BlockTableId, OpenMode.ForRead);
                    if (bt2.Has(blockName))
                    {
                        var defs = new Dictionary<string, AttributeDefinition>(StringComparer.OrdinalIgnoreCase);
                        var btr2 = (BlockTableRecord)tr2.GetObject(bt2[blockName], OpenMode.ForRead);
                        foreach (ObjectId eid in btr2)
                        {
                            var ad = tr2.GetObject(eid, OpenMode.ForRead) as AttributeDefinition;
                            if (ad != null && !ad.Constant) defs[ad.Tag] = ad;
                        }
                        var ms2 = (BlockTableRecord)tr2.GetObject(bt2[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                        foreach (ObjectId id in ms2)
                        {
                            var br2 = tr2.GetObject(id, OpenMode.ForRead) as BlockReference;
                            if (br2 == null) continue;
                            string bn = null;
                            try
                            {
                                var obtr = (BlockTableRecord)tr2.GetObject(
                                    br2.DynamicBlockTableRecord.IsNull ? br2.BlockTableRecord : br2.DynamicBlockTableRecord,
                                    OpenMode.ForRead);
                                bn = obtr.Name;
                            }
                            catch { }
                            if (!string.Equals(bn, blockName, StringComparison.OrdinalIgnoreCase)) continue;
                            foreach (ObjectId attId in br2.AttributeCollection)
                            {
                                var ar2 = tr2.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                                if (ar2 == null) continue;
                                AttributeDefinition ad2;
                                if (!defs.TryGetValue(ar2.Tag, out ad2)) continue;
                                ar2.UpgradeOpen();
                                string keep = ar2.TextString;
                                try
                                {
                                    ar2.SetAttributeFromBlock(ad2, br2.BlockTransform);
                                    ar2.TextString = keep;
                                    attrsSynced++;
                                }
                                catch { }
                                JsonNode wfNode = widthFactors != null ? widthFactors[ar2.Tag] : null;
                                if (wfNode != null)
                                {
                                    double wf2 = wfNode.GetValue<double>();
                                    if (wf2 > 0) { ar2.WidthFactor = wf2; widthReapplied++; }
                                }
                                try { ar2.AdjustAlignment(db); } catch { }
                            }
                        }
                    }
                    tr2.Commit();
                }
            }

            {

                return new JsonObject
                {
                    ["block"] = blockName,
                    ["mode"] = atArr != null ? "at（逐点，对齐外包左下角）" : "grid",
                    ["inserted"] = inserted,
                    ["old_erased"] = erased,
                    ["scale"] = "1:" + scale.ToString("0.###") + "（来源：" + scaleFrom + "）",
                    ["paper"] = paper + " " + pw + "×" + ph + " mm",
                    ["frame_size_model"] = Math.Round(frameW, 3) + " × " + Math.Round(frameH, 3) + " m",
                    ["block_scale"] = blockScale,
                    ["attributes_filled"] = attrsFilled,
                    ["attsync"] = wantAttsync,
                    ["attsync_attrs"] = attrsSynced,
                    ["attsync_width_reapplied"] = widthReapplied,
                    ["positions"] = placed
                };
            }
            } finally { HostApplicationServices.WorkingDatabase = prevWdb; }
        }

        // ===================== 诊断：图上都有些什么 =====================

        static JsonNode EntityStats(JsonObject a, Document doc)
        {
            string byWhat = GetString(a, "by", "type");     // type | layer
            int top = (int)GetDouble(a, "top", 30);
            Database db = doc.Database;
            var counts = new Dictionary<string, int>();
            int total = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    DBObject o;
                    try { o = tr.GetObject(id, OpenMode.ForRead); } catch { continue; }
                    total++;
                    string key;
                    if (byWhat == "layer") key = SafeLayer(o);
                    else key = o.GetType().Name;
                    int c;
                    counts.TryGetValue(key, out c);
                    counts[key] = c + 1;
                }
                tr.Commit();
            }

            var list = new List<KeyValuePair<string, int>>(counts);
            list.Sort((p, q) => q.Value.CompareTo(p.Value));
            var arr = new JsonArray();
            for (int i = 0; i < list.Count && i < top; i++)
                arr.Add(new JsonObject { [byWhat] = list[i].Key, ["count"] = list[i].Value });

            return new JsonObject { ["total_entities"] = total, ["group_by"] = byWhat, ["top"] = arr };
        }

        // ===================== 出图辅助：图层全开 =====================
        // headless 打印时最常见的"打出来是白纸"：对象在的图层被关/冻结了。
        // 新建的 Civil 对象按 LayerKey 落到项目自己的图层上，那些图层在原图里可能本来就是关的。

        static JsonNode LayersOff(JsonObject a, Document doc)
        {
            var names = new List<string>();
            var arr = a["names"] as JsonArray;
            if (arr != null) foreach (JsonNode n in arr) names.Add(n.GetValue<string>());
            string one = GetString(a, "name", null);
            if (!string.IsNullOrEmpty(one)) names.Add(one);
            if (names.Count == 0) throw new InvalidOperationException("给 name 或 names[]（图层名）。");

            Database db = doc.Database;
            var done = new JsonArray();
            var missing = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (string nm in names)
                {
                    if (!lt.Has(nm)) { missing.Add(nm); continue; }
                    ObjectId id = lt[nm];
                    if (id == db.Clayer) { missing.Add(nm + "(当前图层，跳过)"); continue; }
                    var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite);
                    ltr.IsOff = true;
                    done.Add(nm);
                }
                tr.Commit();
            }
            return new JsonObject { ["turned_off"] = done, ["not_found"] = missing };
        }

        static JsonNode LayersOn(JsonObject a, Document doc)
        {
            Database db = doc.Database;
            var turnedOn = new JsonArray();
            var thawed = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in lt)
                {
                    var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    bool needOn = ltr.IsOff, needThaw = ltr.IsFrozen;
                    if (!needOn && !needThaw) continue;
                    // 当前图层不能冻结，跳过以免抛异常
                    if (needThaw && id == db.Clayer) needThaw = false;
                    ltr.UpgradeOpen();
                    if (needOn) { ltr.IsOff = false; turnedOn.Add(ltr.Name); }
                    if (needThaw) { ltr.IsFrozen = false; thawed.Add(ltr.Name); }
                }
                tr.Commit();
            }
            return new JsonObject
            {
                ["turned_on"] = turnedOn,
                ["thawed"] = thawed,
                ["note"] = "只改内存中的图纸；不 save_dwg 就不会落盘"
            };
        }

        // ===================== 10. 存盘 =====================

        // Civil 部分 API（已确诊：SectionViewVolumeTableGroup.CreateVolumeTable）把新对象
        // 以写打开状态交回且不归调用方事务管，事务提交也关不掉，SaveAs 直接抛
        // eWasOpenForWrite 且落盘文件是坏的（ErrorStatus=434）。存盘前扫全库，把还挂着
        // 写打开的对象逐个 DowngradeOpen 收回来，任何节点漏关都在这里兜住。
        // ⚠ 必须用普通 StartTransaction：OpenCloseTransaction 对已在别处写打开的对象
        // 直接抛 eWasOpenForWrite（数出来永远是 0）；普通事务能挂上已打开对象。
        static int ReclaimWriteOpen(Database db)
        {
            int reclaimed = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId btrId in bt)
                {
                    BlockTableRecord btr;
                    try { btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead); }
                    catch { continue; }
                    foreach (ObjectId id in btr)
                    {
                        try
                        {
                            DBObject o = tr.GetObject(id, OpenMode.ForRead, false, true);
                            if (o != null && o.IsWriteEnabled) { o.DowngradeOpen(); reclaimed++; }
                        }
                        catch { }
                    }
                }
                tr.Commit();
            }
            return reclaimed;
        }


        static JsonNode SaveDwg(JsonObject a, Document doc)
        {
            Database db = doc.Database;
            int reclaimed = ReclaimWriteOpen(db);
            string host = SafeFile(db);
            bool apply = GetBool(a, "apply", false);
            string outPath = GetString(a, "out", null);

            if (apply) outPath = host;
            else if (string.IsNullOrEmpty(outPath))
            {
                string dir = Path.GetDirectoryName(host);
                string stem = Path.GetFileNameWithoutExtension(host);
                outPath = Path.Combine(dir, stem + "_链路_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".dwg");
            }
            if (!Path.IsPathRooted(outPath))
                throw new InvalidOperationException("out 必须是绝对路径：" + outPath);
            if (!apply && string.Equals(Path.GetFullPath(outPath), Path.GetFullPath(host),
                                        StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("out 指向了宿主图纸本身；要写回原图请显式传 apply:true。");
            if (File.Exists(outPath) && !apply && !GetBool(a, "overwrite", false))
                throw new InvalidOperationException("文件已存在，拒绝覆盖：" + outPath + "（确需覆盖传 overwrite:true）");

            string backup = null;
            if (apply && GetBool(a, "backup", true) && File.Exists(host))
            {
                backup = Path.Combine(Path.GetDirectoryName(host),
                    Path.GetFileNameWithoutExtension(host) + "_备份_" +
                    DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".dwg");
                File.Copy(host, backup, false);   // 备份失败就抛，不带伤前进
            }

            string dir2 = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir2) && !Directory.Exists(dir2)) Directory.CreateDirectory(dir2);
            bool replaceExisting = !apply && File.Exists(outPath) && GetBool(a, "overwrite", false);
            string requestedOutPath = outPath;
            bool fallbackBecauseLocked = false;
            string actualSavePath = outPath;
            if (replaceExisting)
            {
                actualSavePath = Path.Combine(
                    dir2,
                    Path.GetFileNameWithoutExtension(outPath) + ".__c3df_tmp_" +
                    Guid.NewGuid().ToString("N").Substring(0, 8) + ".dwg");
            }

            try
            {
                db.SaveAs(actualSavePath, DwgVersion.Current);
                if (replaceExisting)
                {
                    try
                    {
                        File.Replace(actualSavePath, outPath, null, true);
                    }
                    catch (IOException)
                    {
                        string fallback = Path.Combine(
                            dir2,
                            Path.GetFileNameWithoutExtension(outPath) + "_运行_" +
                            DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".dwg");
                        File.Move(actualSavePath, fallback);
                        actualSavePath = fallback;
                        outPath = fallback;
                        fallbackBecauseLocked = true;
                    }
                }
            }
            finally
            {
                if (!string.Equals(actualSavePath, outPath, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(actualSavePath))
                {
                    try { File.Delete(actualSavePath); } catch { }
                }
            }

            return new JsonObject
            {
                ["mode"] = apply ? "已写回宿主图纸" : "另存新文件（宿主图纸磁盘副本未动）",
                ["output"] = outPath,
                ["requested_output"] = requestedOutPath,
                ["replaced_existing"] = replaceExisting,
                ["fallback_because_target_locked"] = fallbackBecauseLocked,
                ["backup"] = backup ?? "(无需备份)",
                ["bytes"] = File.Exists(outPath) ? new FileInfo(outPath).Length : 0,
                ["write_handles_reclaimed"] = reclaimed
            };
        }

        // ===================== 诊断：曲面/走廊到底有没有几何 =====================
        // 「算出来是 0」最常见的原因不是算错，而是上游某个面/走廊压根是空的。
        // 这两个操作把"有没有东西"这件事变成可看的数字。

        static JsonNode SurfaceStats(JsonObject a, Document doc)
        {
            string want = GetString(a, "name", null);
            Database db = doc.Database;
            var arr = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var s = tr.GetObject(id, OpenMode.ForRead) as CivSurface;
                    if (s == null) continue;
                    if (!string.IsNullOrEmpty(want) && s.Name != want) continue;

                    // ⚠ 不要遍历 Vertices/Triangles：headless 里会**硬崩**进程（AccessViolation，
                    // .NET 捕不到，表现为结果文件停在 ops:[] 什么都没有）。统计一律走属性接口。
                    var o = new JsonObject { ["name"] = s.Name, ["type"] = s.GetType().Name };
                    o["general"] = InvokeAndDump(s, "GetGeneralProperties");
                    if (s is CivTinSurface) o["tin"] = InvokeAndDump(s, "GetTinProperties");
                    arr.Add(o);
                }
                tr.Commit();
            }
            if (arr.Count == 0) throw new InvalidOperationException("没有匹配的曲面。");
            return arr;
        }

        static JsonNode CorridorStats(JsonObject a, Document doc)
        {
            string want = GetString(a, "name", null);
            Database db = doc.Database;
            var arr = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var c = tr.GetObject(id, OpenMode.ForRead) as CivCorridor;
                    if (c == null) continue;
                    if (!string.IsNullOrEmpty(want) && c.Name != want) continue;

                    var o = new JsonObject { ["name"] = c.Name };
                    var codes = new JsonArray();
                    try { foreach (string s in c.GetLinkCodes()) codes.Add(s); } catch { }
                    o["link_codes"] = codes;

                    var pcodes = new JsonArray();
                    try { foreach (string s in c.GetPointCodes()) pcodes.Add(s); } catch { }
                    o["point_codes"] = pcodes;

                    var surfs = new JsonArray();
                    try
                    {
                        foreach (CivCorridorSurface cs in c.CorridorSurfaces)
                        {
                            var one = new JsonObject { ["name"] = cs.Name };
                            try
                            {
                                var ts = tr.GetObject(cs.SurfaceId, OpenMode.ForRead) as CivSurface;
                                if (ts != null) one["general"] = InvokeAndDump(ts, "GetGeneralProperties");
                            }
                            catch (System.Exception ex) { one["error"] = ex.GetType().Name; }
                            surfs.Add(one);
                        }
                    }
                    catch (System.Exception ex) { o["surface_error"] = ex.GetType().Name + ": " + ex.Message; }
                    o["corridor_surfaces"] = surfs;

                    // 基准线/区域要遍历托管包装对象，headless 下有硬崩风险，默认不碰
                    if (GetBool(a, "deep", false))
                    {
                        var bls = new JsonArray();
                        try
                        {
                            foreach (Autodesk.Civil.DatabaseServices.Baseline b in c.Baselines)
                            {
                                var regions = new JsonArray();
                                foreach (Autodesk.Civil.DatabaseServices.BaselineRegion r in b.BaselineRegions)
                                    regions.Add(new JsonObject
                                    {
                                        ["name"] = r.Name,
                                        ["start"] = Math.Round(r.StartStation, 3),
                                        ["end"] = Math.Round(r.EndStation, 3)
                                    });
                                bls.Add(new JsonObject
                                {
                                    ["name"] = b.Name,
                                    ["start"] = SafeStr(() => Math.Round(b.StartStation, 3).ToString()),
                                    ["end"] = SafeStr(() => Math.Round(b.EndStation, 3).ToString()),
                                    ["regions"] = regions
                                });
                            }
                        }
                        catch (System.Exception ex) { o["baseline_error"] = ex.GetType().Name + ": " + ex.Message; }
                        o["baselines"] = bls;
                    }
                    arr.Add(o);
                }
                tr.Commit();
            }
            if (arr.Count == 0) throw new InvalidOperationException("没有匹配的走廊。");
            return arr;
        }

        /// <summary>只读盘点走廊目标：走廊→基线→区域→每个目标槽指向的对象。
        /// 曲线类目标（多段线/要素线等）顺带沿线采样，实测相对基线路线的偏移分布——
        /// spread 小＝等距偏移段（可换偏移路线），spread 大＝变宽段（该把线升级成路线再当目标）。</summary>
        static JsonNode CorridorTargets(JsonObject a, Document doc)
        {
            string want = GetString(a, "name", null);
            double step = GetDouble(a, "sample_step", 10.0);
            int maxSamples = (int)GetDouble(a, "max_samples", 300);
            Database db = doc.Database;
            var arr = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var c = tr.GetObject(id, OpenMode.ForRead) as CivCorridor;
                    if (c == null) continue;
                    if (!string.IsNullOrEmpty(want) && c.Name != want) continue;
                    var co = new JsonObject { ["corridor"] = c.Name };
                    var bls = new JsonArray();
                    try
                    {
                        foreach (Autodesk.Civil.DatabaseServices.Baseline b in c.Baselines)
                        {
                            CivAlignment mainAl = null;
                            try { mainAl = tr.GetObject(b.AlignmentId, OpenMode.ForRead) as CivAlignment; } catch { }
                            var bo = new JsonObject
                            {
                                ["baseline"] = b.Name,
                                ["alignment"] = mainAl == null ? null : (JsonNode)mainAl.Name
                            };
                            var regs = new JsonArray();
                            foreach (Autodesk.Civil.DatabaseServices.BaselineRegion r in b.BaselineRegions)
                            {
                                var ro = new JsonObject
                                {
                                    ["region"] = r.Name,
                                    ["start"] = Math.Round(r.StartStation, 3),
                                    ["end"] = Math.Round(r.EndStation, 3)
                                };
                                var slots = new JsonArray();
                                try
                                {
                                    double rlo = Math.Min(r.StartStation, r.EndStation) - 1.0;
                                    double rhi = Math.Max(r.StartStation, r.EndStation) + 1.0;
                                    foreach (CivTargetInfo t in r.GetTargets())
                                        slots.Add(DumpTargetSlot(tr, t, mainAl, rlo, rhi, step, maxSamples));
                                }
                                catch (System.Exception ex) { ro["targets_error"] = ex.GetType().Name + ": " + ex.Message; }
                                ro["slots"] = slots;
                                regs.Add(ro);
                            }
                            bo["regions"] = regs;
                            bls.Add(bo);
                        }
                    }
                    catch (System.Exception ex) { co["baseline_error"] = ex.GetType().Name + ": " + ex.Message; }
                    co["baselines"] = bls;

                    // 走廊级目标（create_corridor 设目标走的就是这一级；区域级读不到的这里兜底）
                    var cslots = new JsonArray();
                    try
                    {
                        CivAlignment sampleAl = null;
                        foreach (Autodesk.Civil.DatabaseServices.Baseline b in c.Baselines)
                        {
                            try { sampleAl = tr.GetObject(b.AlignmentId, OpenMode.ForRead) as CivAlignment; } catch { }
                            break;
                        }
                        double clo = double.MinValue, chi = double.MaxValue;
                        if (sampleAl != null)
                        {
                            clo = Math.Min(sampleAl.StartingStation, sampleAl.EndingStation) - 1.0;
                            chi = Math.Max(sampleAl.StartingStation, sampleAl.EndingStation) + 1.0;
                        }
                        foreach (CivTargetInfo t in c.GetTargets())
                            cslots.Add(DumpTargetSlot(tr, t, sampleAl, clo, chi, step, maxSamples));
                    }
                    catch (System.Exception ex) { co["corridor_targets_error"] = ex.GetType().Name + ": " + ex.Message; }
                    co["corridor_slots"] = cslots;
                    arr.Add(co);
                }
                tr.Commit();
            }
            if (arr.Count == 0) throw new InvalidOperationException("没有匹配的走廊。");
            return arr;
        }

        /// <summary>批量改路线名（对象与句柄不动，偏移/走廊等引用全保留）。</summary>
        static JsonNode RenameAlignments(JsonObject a, Document doc)
        {
            var items = a["items"] as JsonArray;
            if (items == null || items.Count == 0)
                throw new InvalidOperationException("items 必需：[{from,to}]。");
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var done = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (JsonNode n in items)
                {
                    var o = (JsonObject)n;
                    string from = Need(o, "from"), to = Need(o, "to");
                    var al = FindAlignment(tr, civ, from);
                    if (al == null) throw new InvalidOperationException("找不到路线 '" + from + "'。");
                    if (FindAlignment(tr, civ, to) != null)
                        throw new InvalidOperationException("目标名 '" + to + "' 已存在。");
                    al.UpgradeOpen();
                    al.Name = to;
                    done.Add(new JsonObject { ["from"] = from, ["to"] = to, ["handle"] = al.Handle.ToString() });
                }
                tr.Commit();
            }
            return new JsonObject { ["renamed"] = done };
        }

        /// <summary>解析一个目标槽：显示名/类型/子装配，逐个目标对象报类名/句柄/图层；
        /// 目标是路线时报偏移路线信息，是普通曲线时沿线采样实测相对参照路线的偏移分布。</summary>
        static JsonObject DumpTargetSlot(Transaction tr, CivTargetInfo t, CivAlignment refAl, double lo, double hi, double step, int maxSamples)
        {
            var so = new JsonObject
            {
                ["slot"] = t.DisplayName,
                ["type"] = t.TargetType.ToString(),
                ["subassembly"] = SafeStr(() => t.SubassemblyName)
            };
            var objs = new JsonArray();
            foreach (ObjectId tid in t.TargetIds)
            {
                var eo = new JsonObject();
                try
                {
                    var ent = tr.GetObject(tid, OpenMode.ForRead);
                    eo["class"] = ent.GetType().Name;
                    eo["handle"] = ent.Handle.ToString();
                    var e2 = ent as Entity;
                    if (e2 != null) eo["layer"] = e2.Layer;
                    var tal = ent as CivAlignment;
                    if (tal != null)
                    {
                        eo["name"] = tal.Name;
                        bool isOff = false;
                        try { isOff = tal.IsOffsetAlignment; } catch { }
                        eo["is_offset_alignment"] = isOff;
                        if (isOff)
                        {
                            try
                            {
                                var info = tal.OffsetAlignmentInfo;
                                var po = tr.GetObject(info.ParentAlignmentId, OpenMode.ForRead) as CivAlignment;
                                eo["offset_parent"] = po == null ? null : (JsonNode)po.Name;
                                eo["nominal_offset"] = Math.Round(info.NominalOffset, 3);
                            }
                            catch (System.Exception ex) { eo["offset_info_error"] = ex.GetType().Name; }
                        }
                    }
                    else if (refAl != null)
                    {
                        var cur = ent as Curve;
                        if (cur != null)
                            eo["offset_profile"] = SampleCurveOffsets(cur, refAl, lo, hi, step, maxSamples);
                    }
                }
                catch (System.Exception ex) { eo["error"] = ex.GetType().Name + ": " + ex.Message; }
                objs.Add(eo);
            }
            so["objects"] = objs;
            return so;
        }

        /// <summary>沿曲线等距采样，逐点求相对路线的桩号/偏移；只统计落在 [lo,hi] 桩号窗内的样本。</summary>
        static JsonNode SampleCurveOffsets(Curve cur, CivAlignment al, double lo, double hi, double step, int maxSamples)
        {
            double len;
            try { len = cur.GetDistanceAtParameter(cur.EndParam); }
            catch (System.Exception ex) { return "(取不到曲线长度: " + ex.GetType().Name + ")"; }
            if (len <= 0) return "(零长度曲线)";
            int n = Math.Min(Math.Max(2, maxSamples), Math.Max(2, (int)Math.Ceiling(len / Math.Max(0.5, step)) + 1));
            double dstep = len / (n - 1);
            var offs = new List<double>();
            double staMin = double.MaxValue, staMax = double.MinValue;
            int outside = 0, failed = 0;
            for (int i = 0; i < n; i++)
            {
                Point3d p;
                try { p = cur.GetPointAtDist(Math.Min(len, i * dstep)); } catch { failed++; continue; }
                double sta = 0, off = 0;
                try { al.StationOffset(p.X, p.Y, ref sta, ref off); } catch { failed++; continue; }
                if (sta < lo || sta > hi) { outside++; continue; }
                offs.Add(off);
                if (sta < staMin) staMin = sta;
                if (sta > staMax) staMax = sta;
            }
            var o = new JsonObject
            {
                ["curve_length"] = Math.Round(len, 3),
                ["samples_in_region"] = offs.Count,
                ["samples_outside_region"] = outside,
                ["samples_failed"] = failed
            };
            if (offs.Count > 0)
            {
                double mn = double.MaxValue, mx = double.MinValue, sum = 0;
                foreach (double v in offs) { if (v < mn) mn = v; if (v > mx) mx = v; sum += v; }
                o["offset_min"] = Math.Round(mn, 3);
                o["offset_max"] = Math.Round(mx, 3);
                o["offset_mean"] = Math.Round(sum / offs.Count, 3);
                o["offset_spread"] = Math.Round(mx - mn, 3);
                o["station_min"] = Math.Round(staMin, 3);
                o["station_max"] = Math.Round(staMax, 3);
            }
            return o;
        }

        /// <summary>调对象上一个无参方法，把返回值的可读属性全 dump 成 JSON。
        /// 统计类接口（GetGeneralProperties / GetTinProperties）返回的结构体成员名不用猜。</summary>
        static JsonNode InvokeAndDump(object target, string methodName)
        {
            try
            {
                var m = target.GetType().GetMethod(methodName, Type.EmptyTypes);
                if (m == null) return "(无 " + methodName + " 方法)";
                object v = m.Invoke(target, null);
                if (v == null) return "(返回 null)";
                var o = new JsonObject();
                foreach (var p in v.GetType().GetProperties())
                {
                    if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                    try
                    {
                        object pv = p.GetValue(v, null);
                        if (pv == null) o[p.Name] = null;
                        else if (pv is double) o[p.Name] = Math.Round((double)pv, 4);
                        else if (pv is int) o[p.Name] = (int)pv;
                        else if (pv is bool) o[p.Name] = (bool)pv;
                        else o[p.Name] = pv.ToString();
                    }
                    catch (System.Exception ex) { o[p.Name] = "(取值失败: " + ex.GetType().Name + ")"; }
                }
                return o;
            }
            catch (System.Exception ex) { return "(" + ex.GetType().Name + ": " + Truncate(ex.Message, 100) + ")"; }
        }

        /// <summary>重建走廊（诊断用：GUI 建好的走廊在 headless 重建后还剩什么，
        /// 就能判断子装配代码在 accoreconsole 里到底跑不跑）。</summary>
        static JsonNode RebuildCorridor(JsonObject a, Document doc)
        {
            string name = Need(a, "name");
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivCorridor c = FindCorridor(tr, db, name);
                if (c == null) throw new InvalidOperationException("找不到走廊 '" + name + "'。");
                var before = new JsonArray();
                foreach (string s in c.GetLinkCodes()) before.Add(s);
                c.Rebuild();
                var after = new JsonArray();
                foreach (string s in c.GetLinkCodes()) after.Add(s);
                var res = new JsonObject
                {
                    ["corridor"] = name,
                    ["link_codes_before"] = before,
                    ["link_codes_after"] = after
                };
                tr.Commit();
                return res;
            }
        }

        static string SafeStr(Func<string> f)
        {
            try { return f() ?? ""; } catch (System.Exception ex) { return "(" + ex.GetType().Name + ")"; }
        }

        // ===================== 查 API 真实签名 =====================
        // AeccDbMgd 是混合模式程序集，进程外反射不了（MetadataLoadContext 也累），
        // 但在 acc 进程里它已经加载好了——直接反射最省事。写新操作前用它查签名，别猜。

        static JsonNode ApiSignatures(JsonObject a, Document doc)
        {
            string typeName = Need(a, "type");
            string filter = GetString(a, "member", null);
            bool staticsOnly = GetBool(a, "statics_only", false);
            int max = (int)GetDouble(a, "max", 120);

            // 先按全名直取（GetType 不会因为程序集里有加载不了的类型而整体失败），
            // 取不到再退回扫简单名——扫的时候 GetTypes() 可能抛 ReflectionTypeLoadException，
            // 那时它的 Types 里仍有能用的部分，别整个丢掉。
            // AutoCAD 把 AeccDbMgd 等加载在自定义 AssemblyLoadContext 里，
            // AppDomain.CurrentDomain.GetAssemblies() **看不到它们**（第一版就栽在这）。
            // 所以从本插件已引用的类型反查程序集，作为搜索种子。
            var pool = new List<System.Reflection.Assembly>
            {
                typeof(CivAlignment).Assembly,      // AeccDbMgd
                typeof(CivDoc).Assembly,            // AeccDbMgd / AeccApplicationMgd
                typeof(Database).Assembly,          // acdbmgd
                typeof(Document).Assembly           // acmgd
            };
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (!pool.Contains(asm)) pool.Add(asm);

            var hits = new List<Type>();
            foreach (var asm in pool)
            {
                try
                {
                    Type t = asm.GetType(typeName, false, false);
                    if (t != null && !hits.Contains(t)) hits.Add(t);
                }
                catch { }
            }
            if (hits.Count == 0)
            {
                foreach (var asm in pool)
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (System.Reflection.ReflectionTypeLoadException rex) { types = rex.Types; }
                    catch { continue; }
                    foreach (Type t in types)
                    {
                        if (t == null) continue;
                        if (t.FullName == typeName || t.Name == typeName) hits.Add(t);
                    }
                    if (hits.Count > 12) break;
                }
            }
            if (hits.Count == 0) throw new InvalidOperationException("找不到类型 '" + typeName + "'。");

            var arr = new JsonArray();
            foreach (Type t in hits)
            {
                var members = new JsonArray();
                var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
                          | System.Reflection.BindingFlags.DeclaredOnly;
                if (!staticsOnly) flags |= System.Reflection.BindingFlags.Instance;

                foreach (var m in t.GetMethods(flags))
                {
                    if (m.IsSpecialName) continue;
                    if (!string.IsNullOrEmpty(filter) &&
                        m.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (members.Count >= max) { members.Add("…(还有更多)"); break; }
                    var ps = new List<string>();
                    foreach (var p in m.GetParameters()) ps.Add(Short(p.ParameterType) + " " + p.Name);
                    members.Add((m.IsStatic ? "static " : "") + Short(m.ReturnType) + " " +
                                m.Name + "(" + string.Join(", ", ps) + ")");
                }

                if (string.IsNullOrEmpty(filter))
                {
                    foreach (var p in t.GetProperties(flags))
                    {
                        if (members.Count >= max) break;
                        members.Add("prop " + Short(p.PropertyType) + " " + p.Name +
                                    (p.CanWrite ? " {get;set;}" : " {get;}"));
                    }
                }

                arr.Add(new JsonObject
                {
                    ["type"] = t.FullName,
                    ["assembly"] = t.Assembly.GetName().Name,
                    ["base"] = t.BaseType == null ? "" : t.BaseType.FullName,
                    ["members"] = members
                });
            }
            return arr;
        }

        /// <summary>把一条线/多段线转成路线。
        /// 这个版本**没有** CreateFromPolyline（网上抄来的写法在 2025 上编译不过），
        /// 真名是 Alignment.Create(CivilDocument, PolylineOptions, ...)，
        /// 多段线本身、是否删源、是否加缓和曲线都塞在 PolylineOptions 里（用 api 操作查出来的）。</summary>
        static ObjectId CreateAlignmentFromEntity(Transaction tr, CivDoc civ, string name,
            ObjectId siteId, ObjectId entId, ObjectId layerId, ObjectId styleId, ObjectId labelId,
            bool eraseSource, bool addCurves)
        {
            var opts = new Autodesk.Civil.DatabaseServices.PolylineOptions
            {
                PlineId = entId,
                EraseExistingEntities = eraseSource,
                AddCurvesBetweenTangents = addCurves
            };
            return CivAlignment.Create(civ, opts, name, siteId, layerId, styleId, labelId);
        }

        static string Short(Type t)
        {
            if (t == null) return "?";
            string n = t.Name;
            if (t.IsGenericType)
            {
                var args = new List<string>();
                foreach (Type g in t.GetGenericArguments()) args.Add(Short(g));
                int tick = n.IndexOf('`');
                if (tick > 0) n = n.Substring(0, tick);
                return n + "<" + string.Join(",", args) + ">";
            }
            return n;
        }

        // ===================== 公共辅助 =====================

        static CivDoc Civ(Database db)
        {
            CivDoc c = CivDoc.GetCivilDocument(db);
            if (c == null) throw new InvalidOperationException("拿不到 CivilDocument（这张图可能不是 Civil 3D 图纸）。");
            return c;
        }

        static string Need(JsonObject a, string key)
        {
            string v = GetString(a, key, null);
            if (string.IsNullOrEmpty(v)) throw new InvalidOperationException("缺少参数 " + key + "。");
            return v;
        }

        static ObjectId ResolveHandle(Database db, string handle)
        {
            long h = Convert.ToInt64(handle.Trim(), 16);
            ObjectId id;
            if (!db.TryGetObjectId(new Handle(h), out id) || id.IsNull)
                throw new InvalidOperationException("图中没有句柄 " + handle + " 的对象。");
            return id;
        }

        /// <summary>按名找样式；name 为空或找不到时退回集合第一个（集合空则返回 Null）。</summary>
        static ObjectId FindStyleId(Transaction tr, object collection, string name)
        {
            // 匹配顺序：精确 > 忽略大小写 > 唯一前缀 > 唯一包含 > 集合第一个（老兜底）。
            // 前缀/包含是给「样式名带版本后缀」的库准备的：图里叫 @C3DF-SimpleGrid[Defalt]、
            // @C3DF-river-dregde[Default-v2.0]，调用方只记得短名。精确匹配不到就直接退回
            // 第一个的老行为会安静地设错样式，比不设还糟。
            ObjectId first = ObjectId.Null;
            var en = collection as System.Collections.IEnumerable;
            if (en == null) return ObjectId.Null;

            var all = new List<KeyValuePair<ObjectId, string>>();
            foreach (object item in en)
            {
                if (!(item is ObjectId)) continue;
                ObjectId id = (ObjectId)item;
                if (first.IsNull) first = id;
                if (string.IsNullOrEmpty(name)) continue;
                string n = null;
                try { n = TryGetName(tr.GetObject(id, OpenMode.ForRead)); }
                catch { }
                if (string.IsNullOrEmpty(n)) continue;
                if (n == name) return id;                    // 精确命中，到此为止
                all.Add(new KeyValuePair<ObjectId, string>(id, n));
            }
            if (string.IsNullOrEmpty(name)) return first;

            var ci = new List<KeyValuePair<ObjectId, string>>();
            foreach (var kv in all)
                if (string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase)) ci.Add(kv);
            if (ci.Count > 0) return PickNewestStyle(ci);

            var pre = new List<KeyValuePair<ObjectId, string>>();
            foreach (var kv in all)
                if (kv.Value.StartsWith(name, StringComparison.OrdinalIgnoreCase)) pre.Add(kv);
            if (pre.Count > 0) return PickNewestStyle(pre);

            var con = new List<KeyValuePair<ObjectId, string>>();
            foreach (var kv in all)
                if (kv.Value.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) con.Add(kv);
            if (con.Count > 0) return PickNewestStyle(con);

            return first;
        }

        /// <summary>同一短名命中多个样式时取版本最高的那个：样式库习惯把版本写进名字
        /// （@C3DF-river-dregde[Default] / [Default-v2.0]），取集合第一个等于随机挑一个。</summary>
        static ObjectId PickNewestStyle(List<KeyValuePair<ObjectId, string>> cands)
        {
            ObjectId best = cands[0].Key;
            double bestVer = StyleVersion(cands[0].Value);
            string bestName = cands[0].Value;
            for (int i = 1; i < cands.Count; i++)
            {
                double v = StyleVersion(cands[i].Value);
                if (v > bestVer ||
                    (v == bestVer && string.Compare(cands[i].Value, bestName, StringComparison.OrdinalIgnoreCase) > 0))
                {
                    best = cands[i].Key;
                    bestVer = v;
                    bestName = cands[i].Value;
                }
            }
            return best;
        }

        /// <summary>从样式名里抠出 vN[.N] 的版本号，没有算 0。</summary>
        static double StyleVersion(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            double best = 0;
            for (int i = 0; i < name.Length - 1; i++)
            {
                if (name[i] != 'v' && name[i] != 'V') continue;
                if (i > 0 && char.IsLetterOrDigit(name[i - 1])) continue;   // 要 v 前是分隔符
                int j = i + 1;
                while (j < name.Length && (char.IsDigit(name[j]) || name[j] == '.')) j++;
                if (j == i + 1) continue;
                double val;
                if (double.TryParse(name.Substring(i + 1, j - i - 1),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out val) && val > best)
                    best = val;
            }
            return best;
        }

        static CivAlignment FindAlignment(Transaction tr, CivDoc civ, string name)
        {
            foreach (ObjectId id in civ.GetAlignmentIds())
            {
                var a = (CivAlignment)tr.GetObject(id, OpenMode.ForRead);
                if (a.Name == name) return a;
            }
            return null;
        }

        static ObjectId FindSurfaceId(Transaction tr, CivDoc civ, string name)
        {
            foreach (ObjectId id in civ.GetSurfaceIds())
            {
                var s = (CivSurface)tr.GetObject(id, OpenMode.ForRead);
                if (s.Name == name) return id;
            }
            return ObjectId.Null;
        }

        static void EraseAlignments(Transaction tr, CivDoc civ, params string[] names)
        {
            var set = new HashSet<string>(names);
            foreach (ObjectId id in civ.GetAlignmentIds())
            {
                var a = (CivAlignment)tr.GetObject(id, OpenMode.ForRead);
                if (!set.Contains(a.Name)) continue;
                a.UpgradeOpen();
                a.Erase();
            }
        }

        static void EraseProfiles(Transaction tr, CivDoc civ, params string[] names)
        {
            var set = new HashSet<string>(names);
            foreach (ObjectId aid in civ.GetAlignmentIds())
            {
                var a = (CivAlignment)tr.GetObject(aid, OpenMode.ForRead);
                foreach (ObjectId pid in a.GetProfileIds())
                {
                    var p = (CivProfile)tr.GetObject(pid, OpenMode.ForRead);
                    if (!set.Contains(p.Name)) continue;
                    p.UpgradeOpen();
                    p.Erase();
                }
            }
        }

        static void EraseCorridors(Transaction tr, Database db, params string[] names)
        {
            var set = new HashSet<string>(names);
            foreach (ObjectId id in ModelSpace(db, tr))
            {
                var c = tr.GetObject(id, OpenMode.ForRead) as CivCorridor;
                if (c == null || !set.Contains(c.Name)) continue;
                c.UpgradeOpen();
                c.Erase();
            }
        }

        static CivCorridor FindCorridor(Transaction tr, Database db, string name)
        {
            foreach (ObjectId id in ModelSpace(db, tr))
            {
                var c = tr.GetObject(id, OpenMode.ForRead) as CivCorridor;
                if (c != null && c.Name == name) return (CivCorridor)tr.GetObject(id, OpenMode.ForWrite);
            }
            return null;
        }

        static ObjectId FindCorridorSurfaceId(Transaction tr, Database db, string corridorName, string surfaceName)
        {
            CivCorridor c = FindCorridor(tr, db, corridorName);
            if (c == null) return ObjectId.Null;
            foreach (CivCorridorSurface cs in c.CorridorSurfaces)
                if (cs.Name == surfaceName) return cs.SurfaceId;
            return ObjectId.Null;
        }

        static ObjectId FindQtoCriteria(Transaction tr, CivDoc civ, string name)
        {
            foreach (ObjectId id in civ.Styles.QuantityTakeoffCriterias)
                if (TryGetName(tr.GetObject(id, OpenMode.ForRead)) == name) return id;
            return ObjectId.Null;
        }

        // ===================== 导出 DWG 图纸中所有的表格 =====================

        public class ExtractedTableData
        {
            public int Id { get; set; }
            public string Title { get; set; } = "";
            public string Space { get; set; } = "";
            public string Type { get; set; } = "";
            public List<List<string>> Rows { get; set; } = new List<List<string>>();
        }

        public static JsonNode ExportAllDwgTables(JsonObject a, Document doc)
        {
            string targetPath = GetString(a, "target_dwg", null);
            string outdir = ResolveOutDir(a, doc);
            string excelOutPath = GetString(a, "excel_out", null);

            Database dbToRead = doc.Database;
            bool ownDb = false;

            if (!string.IsNullOrEmpty(targetPath) && File.Exists(targetPath))
            {
                dbToRead = new Database(false, true);
                dbToRead.ReadDwgFile(targetPath, FileOpenMode.OpenForReadAndAllShare, true, null);
                ownDb = true;
            }

            try
            {
                var allExtractedTables = new List<ExtractedTableData>();
                int tableCount = 0;

                using (Transaction tr = dbToRead.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(dbToRead.BlockTableId, OpenMode.ForRead);

                    foreach (ObjectId btrId in bt)
                    {
                        BlockTableRecord btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                        if (btr.IsLayout || btr.Name.Equals(BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (ObjectId entId in btr)
                            {
                                Entity ent = tr.GetObject(entId, OpenMode.ForRead) as Entity;
                                if (ent is Table tbl)
                                {
                                    tableCount++;
                                    ExtractedTableData tableData = ExtractAcadTable(tbl, tableCount, btr.Name);
                                    if (tableData != null && tableData.Rows.Count > 0)
                                    {
                                        allExtractedTables.Add(tableData);
                                    }
                                }
                            }
                        }
                    }

                    try
                    {
                        CivDoc civ = Civ(dbToRead);
                        if (civ != null)
                        {
                            foreach (ObjectId alId in civ.GetAlignmentIds())
                            {
                                CivAlignment al = tr.GetObject(alId, OpenMode.ForRead) as CivAlignment;
                                if (al == null) continue;

                                foreach (ObjectId slgId in al.GetSampleLineGroupIds())
                                {
                                    CivSampleLineGroup slg = tr.GetObject(slgId, OpenMode.ForRead) as CivSampleLineGroup;
                                    if (slg == null) continue;

                                    foreach (CivQtoMaterialList ml in slg.MaterialLists)
                                    {
                                        var civTables = ExtractCivMaterialList(slg, ml, ref tableCount, al.Name);
                                        if (civTables != null && civTables.Count > 0)
                                        {
                                            allExtractedTables.AddRange(civTables);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { }

                    tr.Commit();
                }

                var tablesJson = new JsonArray();
                foreach (var t in allExtractedTables)
                {
                    var tj = new JsonObject
                    {
                        ["id"] = t.Id,
                        ["title"] = t.Title,
                        ["space"] = t.Space,
                        ["type"] = t.Type,
                        ["rows_count"] = t.Rows.Count,
                        ["cols_count"] = t.Rows.Count > 0 ? t.Rows[0].Count : 0
                    };
                    var rowsJ = new JsonArray();
                    foreach (var r in t.Rows)
                    {
                        var rowJ = new JsonArray();
                        foreach (var c in r) rowJ.Add(c ?? "");
                        rowsJ.Add(rowJ);
                    }
                    tj["rows"] = rowsJ;
                    tablesJson.Add(tj);
                }

                string jsonExportPath = Path.Combine(outdir, "extracted_dwg_tables.json");
                File.WriteAllText(jsonExportPath, tablesJson.ToJsonString(), System.Text.Encoding.UTF8);

                return new JsonObject
                {
                    ["tables_found"] = allExtractedTables.Count,
                    ["json_export_path"] = jsonExportPath,
                    ["tables"] = tablesJson
                };
            }
            finally
            {
                if (ownDb) dbToRead.Dispose();
            }
        }

        static ExtractedTableData ExtractAcadTable(Table tbl, int index, string spaceName)
        {
            var data = new ExtractedTableData
            {
                Id = index,
                Space = spaceName,
                Type = "AcadTable",
                Title = "表格_" + index
            };

            int numRows = tbl.Rows.Count;
            int numCols = tbl.Columns.Count;

            for (int r = 0; r < numRows; r++)
            {
                var rowData = new List<string>();
                for (int c = 0; c < numCols; c++)
                {
                    string txt = "";
                    try { txt = tbl.Cells[r, c].TextString; } catch { }
                    txt = CleanMText(txt);
                    rowData.Add(txt);
                }

                if (r == 0)
                {
                    string joined = string.Join(" ", rowData).Trim();
                    if (!string.IsNullOrEmpty(joined))
                    {
                        data.Title = joined;
                    }
                }
                data.Rows.Add(rowData);
            }

            return data;
        }

        static List<ExtractedTableData> ExtractCivMaterialList(CivSampleLineGroup slg, CivQtoMaterialList ml, ref int index, string alignmentName)
        {
            var list = new List<ExtractedTableData>();

            try
            {
                var result = slg.GetTotalVolumeResultDataForMaterialList(ml.Guid);
                var dataTotal = new ExtractedTableData
                {
                    Id = ++index,
                    Space = "Civil3D_QTO",
                    Type = "CivQtoMaterialList_Total",
                    Title = "材质体积表_" + alignmentName + "_" + ml.Name
                };
                dataTotal.Rows.Add(new List<string> { "序号", "桩号", "累计挖方(m³)", "累计填方(m³)", "增量挖方(m³)", "增量填方(m³)" });

                int i = 0;
                double cut = 0, fill = 0;
                foreach (CivQtoSectionalResult sec in result.GetResultsAlongSampleLines())
                {
                    var v = sec.VolumeResult;
                    dataTotal.Rows.Add(new List<string>
                    {
                        (++i).ToString(),
                        Station(sec.Station),
                        Math.Round(v.CumulativeCutVolume, 3).ToString(),
                        Math.Round(v.CumulativeFillVolume, 3).ToString(),
                        Math.Round(v.IncrementalCutVolume, 3).ToString(),
                        Math.Round(v.IncrementalFillVolume, 3).ToString()
                    });
                    cut = v.CumulativeCutVolume;
                    fill = v.CumulativeFillVolume;
                }
                dataTotal.Rows.Add(new List<string> { "合计", "", Math.Round(cut, 3).ToString(), Math.Round(fill, 3).ToString(), "", "" });
                if (dataTotal.Rows.Count > 1) list.Add(dataTotal);
            }
            catch { }

            return list;
        }

        static string CleanMText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string t = raw.Replace("\\P", "\n").Replace("\\p", "\n");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\\[fFhHwWtTqQaAcC][^;]*;", "");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"[\{\}]", "");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\\L|\\l|\\O|\\o|\\K|\\k|\\~", "");
            return t.Trim();
        }
    }
}
