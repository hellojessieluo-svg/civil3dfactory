using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivAssembly = Autodesk.Civil.DatabaseServices.Assembly;
using CivCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using CivProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivProfileType = Autodesk.Civil.DatabaseServices.ProfileType;
using CivSubassembly = Autodesk.Civil.DatabaseServices.Subassembly;
using CivTargetInfo = Autodesk.Civil.DatabaseServices.SubassemblyTargetInfo;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeCreateTwoTierBenchCorridor(JsonObject args, Document doc)
        {
            double bottomWidth = GetDouble(args, "bottom_width", 4.0);
            double h1 = GetDouble(args, "h1", 2.5);
            double m1 = GetDouble(args, "m1", 1.5);
            double benchWidth = GetDouble(args, "bench_width", 1.5);
            double benchSlope = GetDouble(args, "bench_slope", 0.0);
            double h2 = GetDouble(args, "h2", 3.0);
            double m2 = GetDouble(args, "m2", 1.75);
            double thickness = GetDouble(args, "lining_thickness", 0.15);

            string alName = GetString(args, "alignment", "中心线");
            string sfName = GetString(args, "target_surface", GetString(args, "surface", "EG"));
            string leftPkt = Need(args, "left_pkt");
            string rightPkt = Need(args, "right_pkt");
            string asmName = GetString(args, "assembly", "TwoTierBench_Assembly");
            string corridorName = GetString(args, "corridor", alName + "_走廊");
            string baselineName = GetString(args, "baseline", "基准线");
            string regionName = GetString(args, "region", "区域1");

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
                    throw new InvalidOperationException("路线 '" + alName + "' 没有设计纵断面，请先创建纵断面。");

                ObjectId sfId = FindSurfaceId(tr, civ, sfName);
                if (sfId.IsNull) throw new InvalidOperationException("找不到曲面 '" + sfName + "'。");

                // Find or create Assembly
                CivAssembly asm = FindAssembly(db, tr, asmName);
                if (asm == null)
                {
                    ObjectId asmId = civ.AssemblyCollection.Add(asmName, Autodesk.Civil.DatabaseServices.AssemblyType.Other, Point3d.Origin);
                    asm = (CivAssembly)tr.GetObject(asmId, OpenMode.ForWrite);
                }
                else
                {
                    asm.UpgradeOpen();
                }

                // Import SAC PKTs if provided and exist
                if (File.Exists(leftPkt) && File.Exists(rightPkt))
                {
                    try
                    {
                        var composerSubs = ComposerSubassemblies(asm, tr);
                        if (composerSubs.Count == 0)
                        {
                            ObjectId leftSaId = civ.SubassemblyCollection.ImportSACSubassembly(asmName + "_LEFT", leftPkt, Point3d.Origin);
                            ObjectId rightSaId = civ.SubassemblyCollection.ImportSACSubassembly(asmName + "_RIGHT", rightPkt, Point3d.Origin);

                            var leftSa = (CivSubassembly)tr.GetObject(leftSaId, OpenMode.ForWrite);
                            var rightSa = (CivSubassembly)tr.GetObject(rightSaId, OpenMode.ForWrite);

                            ApplySubassemblyParams(leftSa, bottomWidth, h1, m1, benchWidth, benchSlope, h2, m2, thickness);
                            ApplySubassemblyParams(rightSa, bottomWidth, h1, m1, benchWidth, benchSlope, h2, m2, thickness);

                            asm.AddSubassembly(leftSaId);
                            asm.AddSubassembly(rightSaId);
                        }
                        else
                        {
                            foreach (var sa in composerSubs)
                            {
                                sa.UpgradeOpen();
                                ApplySubassemblyParams(sa, bottomWidth, h1, m1, benchWidth, benchSlope, h2, m2, thickness);
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        System.Console.WriteLine("Import subassembly warning: " + ex.Message);
                    }
                }

                ObjectId corridorId = civ.CorridorCollection.Add(
                    corridorName, baselineName, al.ObjectId, fgId, regionName, asm.ObjectId);
                var corridor = (CivCorridor)tr.GetObject(corridorId, OpenMode.ForWrite);
                corridor.Rebuild();

                // Set surface target
                var targets = corridor.GetTargets();
                var sfIds = new ObjectIdCollection { sfId };
                int sCount = 0;
                var slots = new JsonArray();
                foreach (CivTargetInfo t in targets)
                {
                    string tt = t.TargetType.ToString();
                    slots.Add(t.DisplayName + " [" + tt + "]");
                    if (tt == "Surface") { t.TargetIds = sfIds; sCount++; }
                }
                corridor.SetTargets(targets);
                corridor.Rebuild();

                var codes = new JsonArray();
                foreach (string c in corridor.GetLinkCodes()) codes.Add(c);

                var res = new JsonObject
                {
                    ["corridor"] = corridorName,
                    ["assembly"] = asmName,
                    ["bottom_width"] = bottomWidth,
                    ["h1"] = h1,
                    ["m1"] = m1,
                    ["bench_width"] = benchWidth,
                    ["bench_slope"] = benchSlope,
                    ["h2"] = h2,
                    ["m2"] = m2,
                    ["lining_thickness"] = thickness,
                    ["surface_targets_set"] = sCount,
                    ["target_slots"] = slots,
                    ["link_codes"] = codes
                };

                tr.Commit();
                return res;
            }
        }

        static void ApplySubassemblyParams(CivSubassembly sa, double bottomWidth, double h1, double m1, double benchWidth, double benchSlope, double h2, double m2, double thickness)
        {
            SetSubassemblyParamDouble(sa, "BottomWidth", bottomWidth);
            SetSubassemblyParamDouble(sa, "H1", h1);
            SetSubassemblyParamDouble(sa, "M1", m1);
            SetSubassemblyParamDouble(sa, "BenchWidth", benchWidth);
            SetSubassemblyParamDouble(sa, "BenchSlope", benchSlope);
            SetSubassemblyParamDouble(sa, "H2", h2);
            SetSubassemblyParamDouble(sa, "M2", m2);
            SetSubassemblyParamDouble(sa, "LiningThickness", thickness);
        }

        static void SetSubassemblyParamDouble(CivSubassembly sa, string paramName, double val)
        {
            if (sa.ParamsDouble == null) return;
            var list = ToObjectList(sa.ParamsDouble);
            foreach (object p in list)
            {
                string name = ReadProperty(p, "DisplayName") as string;
                if (string.IsNullOrEmpty(name)) name = ReadProperty(p, "Name") as string;
                if (string.Equals(name, paramName, StringComparison.OrdinalIgnoreCase))
                {
                    System.Reflection.PropertyInfo prop = p.GetType().GetProperty("Value");
                    if (prop != null && prop.CanWrite)
                    {
                        prop.SetValue(p, val);
                    }
                }
            }
        }
    }
}
