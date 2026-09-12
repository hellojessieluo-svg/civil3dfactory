using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeTrimOffsetsToCorners(JsonObject args, Document doc)
            => TrimOffsetsToCorners(args, doc);

        // 台田角收口：连接路线建成时父段必须伸过角点相交（留下小尾巴）；
        // 本零件把每个角两侧偏移段的区间端**原位**缩回到弧切点外 margin 处
        // （AlignmentRegion.Start/EndStation 可写，不删不重建，角保持存活）。
        // 桩号帧：区间端用母线中线帧（StationOffset 对中线量），避开偏移自家弧长帧漂移。
        static JsonNode TrimOffsetsToCorners(JsonObject a, Document doc)
        {
            var arr = a["corners"] as JsonArray;
            if (arr == null || arr.Count == 0)
                throw new InvalidOperationException("需要 corners:[{corner,in_alignment,out_alignment},...]");
            double margin = GetDouble(a, "margin", 0.05);

            Database db = doc.Database;
            var civ = Civ(db);
            var report = new JsonArray();
            int trimmed = 0, skipped = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (JsonNode n in arr)
                {
                    var o = (JsonObject)n;
                    string cname = Need(o, "corner");
                    var corner = FindAlignment(tr, civ, cname);
                    var segIn = FindAlignment(tr, civ, Need(o, "in_alignment"));
                    var segOut = FindAlignment(tr, civ, Need(o, "out_alignment"));
                    if (corner == null || segIn == null || segOut == null)
                    {
                        report.Add(cname + ": 对象缺失，跳过");
                        skipped++;
                        continue;
                    }

                    foreach (double sta in new[] { corner.StartingStation, corner.EndingStation })
                    {
                        double e = 0, nn = 0;
                        corner.PointLocation(sta, 0, ref e, ref nn);
                        // 该端点属于哪条段：先对段量偏移≈0；量不到（切点在段区间外）就退回
                        // 用段的母线量——|偏移−标称值|≈0 也算命中（区间端本就存母线帧桩号）。
                        CivAlignment seg = null;
                        foreach (var cand in new[] { segIn, segOut })
                        {
                            double s2 = 0, off2 = 0;
                            try { cand.StationOffset(e, nn, ref s2, ref off2); } catch { continue; }
                            if (Math.Abs(off2) < 0.05) { seg = cand; break; }
                        }
                        if (seg == null)
                        {
                            foreach (var cand in new[] { segIn, segOut })
                            {
                                Autodesk.Civil.DatabaseServices.OffsetAlignmentInfo oi;
                                try { oi = cand.OffsetAlignmentInfo; } catch { continue; }
                                if (oi == null) continue;
                                CivAlignment par;
                                try { par = (CivAlignment)tr.GetObject(oi.ParentAlignmentId, OpenMode.ForRead); }
                                catch { continue; }
                                double s3 = 0, off3 = 0;
                                try { par.StationOffset(e, nn, ref s3, ref off3); } catch { continue; }
                                if (Math.Abs(Math.Abs(off3) - Math.Abs(oi.NominalOffset)) < 0.1) { seg = cand; break; }
                            }
                        }
                        if (seg == null) { report.Add(cname + ": 有端点不在任何段上"); continue; }

                        Autodesk.Civil.DatabaseServices.OffsetAlignmentInfo info;
                        try { info = seg.OffsetAlignmentInfo; }
                        catch { info = null; }   // 非偏移路线（如边界中线）取该属性会抛异常而非返回空
                        if (info == null) { report.Add(seg.Name + ": 不是偏移路线，跳过该端"); continue; }
                        var parent = (CivAlignment)tr.GetObject(info.ParentAlignmentId, OpenMode.ForRead);
                        double psta = 0, poff = 0;
                        try { parent.StationOffset(e, nn, ref psta, ref poff); }
                        catch { report.Add(seg.Name + ": 切点超出母线范围"); continue; }

                        var segW = (CivAlignment)tr.GetObject(seg.ObjectId, OpenMode.ForWrite);
                        var regions = segW.OffsetAlignmentInfo.Regions;
                        // 找覆盖/最近该桩号的区间
                        int best = -1;
                        double bestD = double.MaxValue;
                        for (int i = 0; i < regions.Count; i++)
                        {
                            var r = regions[i];
                            double d = (psta >= r.StartStation && psta <= r.EndStation) ? 0
                                : Math.Min(Math.Abs(psta - r.StartStation), Math.Abs(psta - r.EndStation));
                            if (d < bestD) { bestD = d; best = i; }
                        }
                        if (best < 0) { report.Add(seg.Name + ": 无区间"); continue; }
                        var reg = regions[best];
                        bool nearStart = Math.Abs(psta - reg.StartStation) < Math.Abs(psta - reg.EndStation);
                        double before, after;
                        if (nearStart)
                        {
                            before = reg.StartStation;
                            after = psta - margin;
                            if (after > reg.EndStation - 1) { report.Add(seg.Name + ": 收口会吃光区间，跳过"); continue; }
                            reg.StartStation = after;
                        }
                        else
                        {
                            before = reg.EndStation;
                            after = psta + margin;
                            if (after < reg.StartStation + 1) { report.Add(seg.Name + ": 收口会吃光区间，跳过"); continue; }
                            reg.EndStation = after;
                        }
                        trimmed++;
                        report.Add(seg.Name + (nearStart ? " 起点 " : " 终点 ")
                            + Math.Round(before, 2) + " -> " + Math.Round(after, 2));
                    }
                }
                tr.Commit();
            }
            return new JsonObject
            {
                ["corners_input"] = arr.Count,
                ["ends_trimmed"] = trimmed,
                ["skipped"] = skipped,
                ["details"] = report
            };
        }
    }
}
