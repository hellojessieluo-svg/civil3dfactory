using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivAlign = Autodesk.Civil.DatabaseServices.Alignment;
using CivAssembly = Autodesk.Civil.DatabaseServices.Assembly;
using CivCorr = Autodesk.Civil.DatabaseServices.Corridor;
using CivSubasm = Autodesk.Civil.DatabaseServices.Subassembly;
using CivBaseline = Autodesk.Civil.DatabaseServices.Baseline;
using CivRegion = Autodesk.Civil.DatabaseServices.BaselineRegion;
using CivGroup = Autodesk.Civil.DatabaseServices.AssemblyGroup;
using CivProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivProfileType = Autodesk.Civil.DatabaseServices.ProfileType;
using PDouble = Autodesk.Civil.Runtime.ParamDouble;
using PString = Autodesk.Civil.Runtime.ParamString;
using PLong = Autodesk.Civil.Runtime.ParamLong;
using PBool = Autodesk.Civil.Runtime.ParamBool;

namespace Civil3DFactory
{
    /// <summary>
    /// dump_corridor_params：把走廊模型的全部设计参数摊平成 JSON——
    /// 走廊 → 基线 → 区域 → 装配 → 子装配 → 每个参数的显示名和当前值。
    ///
    /// 用途：把散在各装配里的设计参数（底高程、边坡、宽度…）抽出来，
    /// 作为"参数表驱动模型"的现状快照和回写目标清单。只读，不动图。
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeDumpCorridorParams(JsonObject args, Document doc)
            => DumpCorridorParams(args, doc);

        public static JsonNode DumpCorridorParams(JsonObject a, Document doc)
        {
            bool wantCorridors = GetBool(a, "corridors", true);
            bool wantAssemblies = GetBool(a, "assemblies", true);
            string only = GetString(a, "assembly", null);

            Database db = doc.Database;
            var corrArr = new JsonArray();
            var asmArr = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    DBObject o;
                    try { o = tr.GetObject(id, OpenMode.ForRead); }
                    catch (System.Exception) { continue; }

                    if (wantCorridors && o is CivCorr)
                        corrArr.Add(DcpCorridor(tr, (CivCorr)o));

                    if (wantAssemblies && o is CivAssembly)
                    {
                        var asm = (CivAssembly)o;
                        if (!string.IsNullOrWhiteSpace(only) &&
                            !string.Equals(asm.Name, only, StringComparison.OrdinalIgnoreCase)) continue;
                        asmArr.Add(DcpAssembly(tr, asm));
                    }
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["corridor_count"] = corrArr.Count,
                ["assembly_count"] = asmArr.Count,
                ["corridors"] = corrArr,
                ["assemblies"] = asmArr
            };
        }

        static JsonNode DcpCorridor(Transaction tr, CivCorr corr)
        {
            var blArr = new JsonArray();
            try
            {
                foreach (CivBaseline bl in corr.Baselines)
                {
                    var rgArr = new JsonArray();
                    try
                    {
                        foreach (CivRegion rg in bl.BaselineRegions)
                        {
                            rgArr.Add(new JsonObject
                            {
                                ["name"] = DcpSafe(delegate { return rg.Name; }),
                                ["start_station"] = Round(DcpNum(delegate { return rg.StartStation; }), 3),
                                ["end_station"] = Round(DcpNum(delegate { return rg.EndStation; }), 3),
                                ["assembly"] = DcpNameOf(tr, DcpId(delegate { return rg.AssemblyId; }))
                            });
                        }
                    }
                    catch (System.Exception ex) { rgArr.Add(new JsonObject { ["error"] = ex.Message }); }

                    ObjectId alId = DcpId(delegate { return bl.AlignmentId; });
                    blArr.Add(new JsonObject
                    {
                        ["name"] = DcpSafe(delegate { return bl.Name; }),
                        ["alignment"] = DcpNameOf(tr, alId),
                        ["alignment_length"] = Round(DcpNum(delegate {
                            var x = tr.GetObject(alId, OpenMode.ForRead) as CivAlign;
                            return x == null ? 0.0 : x.Length; }), 3),
                        ["design_profile"] = DcpProfile(tr, alId),
                        ["regions"] = rgArr
                    });
                }
            }
            catch (System.Exception ex) { blArr.Add(new JsonObject { ["error"] = ex.Message }); }

            return new JsonObject
            {
                ["name"] = DcpSafe(delegate { return corr.Name; }),
                ["baselines"] = blArr
            };
        }

        static JsonNode DcpAssembly(Transaction tr, CivAssembly asm)
        {
            var subArr = new JsonArray();
            try
            {
                foreach (CivGroup g in asm.Groups)
                {
                    foreach (ObjectId sid in g.GetSubassemblyIds())
                    {
                        var sub = tr.GetObject(sid, OpenMode.ForRead) as CivSubasm;
                        if (sub == null) continue;
                        subArr.Add(new JsonObject
                        {
                            ["group"] = DcpSafe(delegate { return g.Name; }),
                            ["name"] = DcpSafe(delegate { return sub.Name; }),
                            ["side"] = DcpSafe(delegate { return sub.Side.ToString(); }),
                            ["params"] = DcpParams(sub)
                        });
                    }
                }
            }
            catch (System.Exception ex) { subArr.Add(new JsonObject { ["error"] = ex.Message }); }

            return new JsonObject
            {
                ["name"] = DcpSafe(delegate { return asm.Name; }),
                ["subassemblies"] = subArr
            };
        }

        static JsonNode DcpParams(CivSubasm sub)
        {
            var arr = new JsonArray();
            try
            {
                foreach (PDouble p in sub.ParamsDouble)
                    arr.Add(new JsonObject
                    {
                        ["display"] = p.DisplayName, ["type"] = "double",
                        ["value"] = Round(p.Value, 6), ["readonly"] = p.IsReadOnly
                    });
            }
            catch (System.Exception) { }
            try
            {
                foreach (PLong p in sub.ParamsLong)
                    arr.Add(new JsonObject
                    {
                        ["display"] = p.DisplayName, ["type"] = "long", ["value"] = p.Value
                    });
            }
            catch (System.Exception) { }
            try
            {
                foreach (PBool p in sub.ParamsBool)
                    arr.Add(new JsonObject
                    {
                        ["display"] = p.DisplayName, ["type"] = "bool", ["value"] = p.Value
                    });
            }
            catch (System.Exception) { }
            try
            {
                foreach (PString p in sub.ParamsString)
                    arr.Add(new JsonObject
                    {
                        ["display"] = p.DisplayName, ["type"] = "string", ["value"] = p.Value
                    });
            }
            catch (System.Exception) { }
            return arr;
        }

        /// <summary>取路线的设计纵断面（ProfileType.FG）：名字 + 全部 PVI 的桩号/高程。
        /// 疏浚底高程就落在这里，不在装配参数上。</summary>
        static JsonNode DcpProfile(Transaction tr, ObjectId alId)
        {
            if (alId.IsNull) return null;
            try
            {
                var al = tr.GetObject(alId, OpenMode.ForRead) as CivAlign;
                if (al == null) return null;
                foreach (ObjectId pid in al.GetProfileIds())
                {
                    var p = tr.GetObject(pid, OpenMode.ForRead) as CivProfile;
                    if (p == null) continue;
                    if (p.ProfileType != CivProfileType.FG) continue;
                    var pvis = new JsonArray();
                    double lo = double.MaxValue, hi = double.MinValue;
                    try
                    {
                        foreach (Autodesk.Civil.DatabaseServices.ProfilePVI v in p.PVIs)
                        {
                            double st = v.Station, el = v.Elevation;
                            if (el < lo) lo = el;
                            if (el > hi) hi = el;
                            pvis.Add(new JsonObject
                            {
                                ["station"] = Round(st, 3),
                                ["elevation"] = Round(el, 4)
                            });
                        }
                    }
                    catch (System.Exception) { }
                    return new JsonObject
                    {
                        ["name"] = p.Name,
                        ["pvi_count"] = pvis.Count,
                        ["elev_min"] = lo == double.MaxValue ? 0.0 : Round(lo, 4),
                        ["elev_max"] = hi == double.MinValue ? 0.0 : Round(hi, 4),
                        ["flat"] = (hi - lo) < 1e-6,
                        ["pvis"] = pvis
                    };
                }
            }
            catch (System.Exception) { }
            return null;
        }

        delegate string DcpStrFn();
        delegate double DcpNumFn();
        delegate ObjectId DcpIdFn();

        static string DcpSafe(DcpStrFn f)
        { try { return f() ?? ""; } catch (System.Exception) { return "(取值失败)"; } }

        static double DcpNum(DcpNumFn f)
        { try { return f(); } catch (System.Exception) { return 0.0; } }

        static ObjectId DcpId(DcpIdFn f)
        { try { return f(); } catch (System.Exception) { return ObjectId.Null; } }

        static string DcpNameOf(Transaction tr, ObjectId id)
        {
            if (id.IsNull) return "";
            try
            {
                DBObject o = tr.GetObject(id, OpenMode.ForRead);
                CivAlign al = o as CivAlign;
                if (al != null) return al.Name;
                CivAssembly asm = o as CivAssembly;
                if (asm != null) return asm.Name;
                return TryGetName(o) ?? "";
            }
            catch (System.Exception) { return "(取名失败)"; }
        }
    }
}
