using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// bounded_volumes：逐条闭合边界报体积曲面的挖填方（只读）。
    ///
    /// 走 Civil 的 <c>Surface.GetBoundedVolumes(多边形)</c>——就是体积面板里
    /// 「加一条统计边界」拿到的那组数，边界内的挖/填/净。
    ///
    /// **别拿 calculate_surface_volume 干这活**：那个走 GetVolumeProperties()，
    /// 拿的是整个体积曲面的量，boundary_polyline 只进了报表的文字说明，
    /// 给 N 条边界会返回 N 个一模一样的数（安静地错）。
    ///
    /// 口径：Cut = 现状高于设计要挖掉的，Fill = 现状低于设计要填起来的（相对基准曲面）。
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeBoundedVolumes(JsonObject a, Document doc)
        {
            string volName = GetString(a, "volume_surface", null);
            string baseName = GetString(a, "base_surface", null);
            string compName = GetString(a, "comparison_surface", null);
            if (string.IsNullOrWhiteSpace(volName) &&
                (string.IsNullOrWhiteSpace(baseName) || string.IsNullOrWhiteSpace(compName)))
                throw new InvalidOperationException(
                    "给 volume_surface（已有体积曲面名），或者 base_surface + comparison_surface 两个都给。");

            string layer = GetString(a, "layer", null);
            var handles = a["handles"] as JsonArray;
            var names = a["names"] as JsonObject;
            double step = GetDouble(a, "sample_step", 1.0);   // 边界含弧段，按步长展成点环
            double inset = GetDouble(a, "inset", 0.0);        // 边界往内缩，躲开曲面外沿
            bool closeRing = GetBool(a, "close_ring", true);  // 首点补到末尾
            double cutFactor = GetDouble(a, "cut_factor", 1.0);
            double fillFactor = GetDouble(a, "fill_factor", 1.0);
            bool hasDatum = a["datum_elevation"] != null;
            double datum = GetDouble(a, "datum_elevation", 0.0);
            bool exportExcel = GetBool(a, "export_excel", false);
            string excelFormat = GetString(a, "excel_format", "xlsx");

            if (step <= 0) throw new InvalidOperationException("sample_step 必须大于 0。");
            if (string.IsNullOrWhiteSpace(layer) && (handles == null || handles.Count == 0))
                throw new InvalidOperationException("layer 与 handles 至少给一个来圈定边界。");

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (handles != null)
                foreach (JsonNode h in handles) if (h != null) wanted.Add(h.ToString().Trim());

            Database db = doc.Database;
            CivDoc civ = Civ(db);

            var per = new JsonArray();
            var warnings = new JsonArray();
            var rows = new List<object[]>();
            double tArea = 0, tCut = 0, tFill = 0;
            string usedVol = null;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId volId = ObjectId.Null;
                if (!string.IsNullOrWhiteSpace(volName))
                {
                    volId = FindSurfaceId(tr, civ, volName);
                    if (volId.IsNull) throw new InvalidOperationException("找不到体积曲面 '" + volName + "'。");
                }
                else
                {
                    ObjectId bId = FindSurfaceId(tr, civ, baseName);
                    if (bId.IsNull) throw new InvalidOperationException("找不到基准曲面 '" + baseName + "'。");
                    ObjectId cId = FindSurfaceId(tr, civ, compName);
                    if (cId.IsNull) throw new InvalidOperationException("找不到对比曲面 '" + compName + "'。");
                    volName = "Vol_" + Sanitize(baseName) + "_" + Sanitize(compName);
                    ObjectId old = FindSurfaceId(tr, civ, volName);
                    if (!old.IsNull) volId = old;
                    else volId = Autodesk.Civil.DatabaseServices.TinVolumeSurface.Create(volName, bId, cId);
                }
                var vol = (CivSurface)tr.GetObject(volId, OpenMode.ForRead);
                usedVol = vol.Name;
                if (!(vol is Autodesk.Civil.DatabaseServices.TinVolumeSurface))
                    warnings.Add((JsonNode)("'" + usedVol + "' 不是体积曲面，取到的不是挖填方。"));

                int seq = 0;
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (pl == null) continue;
                    string handle = pl.Handle.ToString();
                    if (wanted.Count > 0) { if (!wanted.Contains(handle)) continue; }
                    else if (!string.Equals(pl.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;

                    seq++;
                    string label = null;
                    if (names != null && names[handle] != null) label = names[handle].ToString();
                    if (string.IsNullOrEmpty(label)) label = "B" + seq.ToString("00");

                    // 边界展成点环：与放坡链共用 DredgeBuildRing，弧段按真实弧长展开
                    DredgeRing ring = DredgeBuildRing(pl, step);
                    if (ring.Count < 3)
                    {
                        warnings.Add((JsonNode)(label + "（" + handle + "）成不了环，跳过。"));
                        continue;
                    }
                    // 边界压在设计面外沿时 Civil 会判「illegal bounding polygon」，
                    // inset 往形心缩一点点把它挪进曲面里（缺省 0 = 不缩）
                    double cx = 0, cy = 0;
                    for (int i = 0; i < ring.Count; i++) { cx += ring.P[i].X; cy += ring.P[i].Y; }
                    cx /= ring.Count; cy /= ring.Count;

                    var pts = new Point3dCollection();
                    for (int i = 0; i < ring.Count; i++)
                    {
                        double px = ring.P[i].X, py = ring.P[i].Y;
                        if (inset > 0)
                        {
                            double dx = px - cx, dy = py - cy, len = Math.Sqrt(dx * dx + dy * dy);
                            if (len > inset) { px -= dx / len * inset; py -= dy / len * inset; }
                        }
                        pts.Add(new Point3d(px, py, 0.0));
                    }
                    if (closeRing && pts.Count > 0) pts.Add(pts[0]);   // 首点补到末尾，显式闭合

                    double cut = 0, fill = 0, net = 0;
                    string err = null;
                    try
                    {
                        var info = hasDatum ? vol.GetBoundedVolumes(pts, datum)
                                            : vol.GetBoundedVolumes(pts);
                        cut = info.Cut; fill = info.Fill; net = info.Net;
                    }
                    catch (System.Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }

                    double area = 0;
                    try { area = pl.Area; } catch (System.Exception) { }

                    if (err != null)
                    {
                        warnings.Add((JsonNode)(label + "（" + handle + "）取体积失败 —— " + err));
                    }
                    else
                    {
                        tArea += area; tCut += cut; tFill += fill;
                    }

                    var row = new JsonObject
                    {
                        ["id"] = label,
                        ["handle"] = handle,
                        ["layer"] = pl.Layer,
                        ["area"] = Round(area, 2),
                        ["cut"] = err == null ? (JsonNode)Round(cut, 2) : null,
                        ["fill"] = err == null ? (JsonNode)Round(fill, 2) : null,
                        ["net"] = err == null ? (JsonNode)Round(net, 2) : null,
                        ["cut_adjusted"] = err == null ? (JsonNode)Round(cut * cutFactor, 2) : null,
                        ["fill_adjusted"] = err == null ? (JsonNode)Round(fill * fillFactor, 2) : null,
                        ["ring_points"] = ring.Count,
                        ["error"] = err
                    };
                    per.Add(row);
                    if (exportExcel)
                        rows.Add(new object[] { label, handle, Round(area, 2),
                            err == null ? (object)Round(cut, 2) : null,
                            err == null ? (object)Round(fill, 2) : null,
                            err == null ? (object)Round(net, 2) : null,
                            cutFactor, fillFactor,
                            err == null ? (object)Round(cut * cutFactor, 2) : null,
                            err == null ? (object)Round(fill * fillFactor, 2) : null });
                }
                tr.Commit();
            }

            // 断言：一条边界都没算出来 = 没成功
            if (per.Count == 0)
                throw new InvalidOperationException(
                    "一条边界都没匹配上（layer='" + (layer ?? "") + "'，点名 " + wanted.Count + " 条）。");
            bool anyOk = false;
            foreach (JsonNode r in per) if (r["cut"] != null) { anyOk = true; break; }
            if (!anyOk)
            {
                // 报错要带着原因走，别让调用方再去别处捞 warnings
                string first = warnings.Count > 0 ? warnings[0].ToString() : "（没有 warning，说明失败在别处）";
                throw new InvalidOperationException(
                    "每条边界取体积都失败了（共 " + per.Count + " 条）。第一条的原因：" + first);
            }

            var files = new JsonArray();
            string outdir = null;
            if (exportExcel)
            {
                outdir = ResolveOutDir(a, doc);
                foreach (string p in Excel.Write(outdir, "边界挖填方_" + Sanitize(usedVol),
                                                 HeadersBoundedVol, rows, excelFormat))
                    files.Add(p);
            }

            return new JsonObject
            {
                ["volume_surface"] = usedVol,
                ["boundaries"] = per.Count,
                ["total_area"] = Round(tArea, 2),
                ["total_cut"] = Round(tCut, 2),
                ["total_fill"] = Round(tFill, 2),
                ["total_net"] = Round(tFill - tCut, 2),
                ["cut_factor"] = cutFactor,
                ["fill_factor"] = fillFactor,
                ["total_cut_adjusted"] = Round(tCut * cutFactor, 2),
                ["total_fill_adjusted"] = Round(tFill * fillFactor, 2),
                ["per_boundary"] = per,
                ["excel_files"] = files,
                ["outdir"] = outdir,
                ["warnings"] = warnings
            };
        }

        static readonly string[] HeadersBoundedVol = {
            "编号", "句柄", "边界面积m2", "挖方m3", "填方m3", "净方m3",
            "挖方系数", "填方系数", "调整后挖方m3", "调整后填方m3" };
    }
}
