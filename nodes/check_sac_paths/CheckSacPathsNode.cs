using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivAssembly = Autodesk.Civil.DatabaseServices.Assembly;
using CivSubassembly = Autodesk.Civil.DatabaseServices.Subassembly;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeCheckSacPaths(JsonObject args, Document doc)
        {
            string assemblyName = Need(args, "assembly");
            var configuredPaths = args["paths"] as JsonArray;
            if (configuredPaths == null || configuredPaths.Count != 2)
                throw new ArgumentException("check_sac_paths 必须且只能配置左右两个 PKT 路径。");

            string leftPath = null;
            string rightPath = null;
            var pathResults = new JsonArray();
            var missing = new List<string>();
            foreach (JsonNode item in configuredPaths)
            {
                string path = item == null ? "" : item.GetValue<string>();
                bool absolute = Path.IsPathRooted(path);
                bool exists = absolute && File.Exists(path);
                long bytes = exists ? new FileInfo(path).Length : 0;
                if (!exists || bytes <= 0) missing.Add(path);

                string filename = Path.GetFileNameWithoutExtension(path) ?? "";
                if (filename.IndexOf("LEFT", StringComparison.OrdinalIgnoreCase) >= 0)
                    leftPath = path;
                if (filename.IndexOf("RIGHT", StringComparison.OrdinalIgnoreCase) >= 0)
                    rightPath = path;

                pathResults.Add(new JsonObject
                {
                    ["path"] = path,
                    ["absolute"] = absolute,
                    ["exists"] = exists,
                    ["bytes"] = bytes,
                    ["last_write_time"] = exists
                        ? File.GetLastWriteTime(path).ToString("yyyy-MM-ddTHH:mm:sszzz")
                        : null
                });
            }

            if (missing.Count > 0)
                throw new InvalidOperationException(
                    "SAC 前置检查失败：固定 PKT 路径不存在或文件为空：" +
                    string.Join("；", missing));
            if (string.IsNullOrEmpty(leftPath) || string.IsNullOrEmpty(rightPath))
                throw new InvalidOperationException(
                    "SAC 前置检查失败：两个 PKT 文件名必须分别含 LEFT 和 RIGHT。");

            Database db = doc.Database;
            var repaired = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var assemblies = FindAllAssemblies(db, tr);
                if (assemblies.Count == 0)
                    throw new InvalidOperationException("SAC 前置检查失败：图中没有任何装配");

                foreach (var assembly in assemblies)
                {
                    var targets = ComposerSubassemblies(assembly, tr);
                    if (targets.Count == 0) continue;

                    assembly.UpgradeOpen();
                    foreach (CivSubassembly oldSa in targets)
                    {
                        string before = oldSa.StatusOf();
                        if (string.Equals(before, "UpToDate", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string path = ResolvePkt(oldSa, leftPath, rightPath);
                        string oldName = oldSa.Name;
                        Point3d oldOrigin = oldSa.Origin;
                        var oldSide = oldSa.Side;
                        string oldCodeSet = SafeString(() => oldSa.CodeSetStyleName);
                        string oldResourceModule = SafeString(() => oldSa.ResourceModule);
                        string oldHelpData = SafeString(() => oldSa.HelpData);
                        string oldHelpCommand = SafeString(() => oldSa.HelpCommand);
                        string oldHelpFile = SafeString(() => oldSa.HelpFile);
                        bool oldEmbedded = SafeBool(() => oldSa.UseEmbeddedProject);
                        // FileNotFound 状态的 SAC 子装配读这两个属性会抛 InvalidOperationException
                        // （2026-09-05 项目A实测，泛型异常的真身就是它）；读不到就按 (0,0) 处理，
                        // 反正新子装配 Origin 已照抄、Side 已照抄，偏移量对挂在装配基线上的部件恒为 0。
                        Vector2d oldParentOffset = new Vector2d(0, 0), oldAssemblyOffset = new Vector2d(0, 0);
                        try { oldParentOffset = oldSa.OffsetToParentAssembly; } catch { }
                        try { oldAssemblyOffset = oldSa.OffsetToAssembly; } catch { }

                        string importName = oldName + "__C3DF_REPATH_" +
                            Guid.NewGuid().ToString("N").Substring(0, 8);
                        ObjectId newId = CivDoc.GetCivilDocument(db).SubassemblyCollection
                            .ImportSac(importName, path, oldOrigin);
                        var newSa = (CivSubassembly)tr.GetObject(newId, OpenMode.ForWrite);

                        CopyParamValues(oldSa.ParamsDouble, newSa.ParamsDouble);
                        CopyParamValues(oldSa.ParamsString, newSa.ParamsString);
                        CopyParamValues(oldSa.ParamsLong, newSa.ParamsLong);
                        CopyParamValues(oldSa.ParamsBool, newSa.ParamsBool);

                        try { newSa.Side = oldSide; } catch { }
                        try { newSa.Origin = oldOrigin; } catch { }
                        try { newSa.OffsetToParentAssembly = oldParentOffset; } catch { }
                        try { newSa.OffsetToAssembly = oldAssemblyOffset; } catch { }
                        if (!string.IsNullOrEmpty(oldCodeSet))
                            try { newSa.CodeSetStyleName = oldCodeSet; } catch { }
                        if (!string.IsNullOrEmpty(oldResourceModule))
                            try { newSa.ResourceModule = oldResourceModule; } catch { }
                        if (!string.IsNullOrEmpty(oldHelpData))
                            try { newSa.HelpData = oldHelpData; } catch { }
                        if (!string.IsNullOrEmpty(oldHelpCommand))
                            try { newSa.HelpCommand = oldHelpCommand; } catch { }
                        if (!string.IsNullOrEmpty(oldHelpFile))
                            try { newSa.HelpFile = oldHelpFile; } catch { }
                        try { newSa.UseEmbeddedProject = oldEmbedded; } catch { }

                        // 参数序：第一个是装配里现有的（被换掉的），第二个是新导入的目标。
                        // 反过来传 API 报 "The target subassembly should be in the assembly."（2026-09-05 实测）
                        assembly.ReplaceSubassembly(oldSa.ObjectId, newId);
                        try { newSa.Name = oldName; } catch { }
                        repaired.Add(new JsonObject
                        {
                            ["assembly"] = assembly.Name,
                            ["name"] = oldName,
                            ["actual_name"] = newSa.Name,
                            ["before"] = before,
                            ["path"] = path,
                            ["replacement_handle"] = newSa.Handle.ToString()
                        });
                    }
                }
                tr.Commit();
            }

            var sacResults = new JsonArray();
            var unhealthy = new List<string>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var assemblies = FindAllAssemblies(db, tr);
                foreach (var assembly in assemblies)
                {
                    foreach (CivSubassembly sa in ComposerSubassemblies(assembly, tr))
                    {
                        string status = sa.StatusOf();
                        string sideStr = SafeString(() => sa.Side.ToString()) ?? "Unknown";
                        sacResults.Add(new JsonObject
                        {
                            ["assembly"] = assembly.Name,
                            ["name"] = sa.Name,
                            ["side"] = sideStr,
                            ["status"] = status,
                            ["from_composer"] = true
                        });
                        if (!string.Equals(status, "UpToDate", StringComparison.OrdinalIgnoreCase))
                            unhealthy.Add(assembly.Name + ":" + sa.Name + "=" + status);
                    }
                }
                tr.Commit();
            }

            if (unhealthy.Count > 0)
                throw new InvalidOperationException(
                    "SAC 自动修复后仍未通过复检：" + string.Join("；", unhealthy));

            return new JsonObject
            {
                ["assembly"] = assemblyName,
                ["paths"] = pathResults,
                ["repair_mode"] = "ImportSACSubassembly+ReplaceSubassembly",
                ["repaired_count"] = repaired.Count,
                ["repaired"] = repaired,
                ["sac_subassemblies"] = sacResults,
                ["all_up_to_date"] = true
            };
        }

        static List<CivAssembly> FindAllAssemblies(Database db, Transaction tr)
        {
            var list = new List<CivAssembly>();
            foreach (ObjectId id in ModelSpace(db, tr))
            {
                CivAssembly assembly;
                try { assembly = tr.GetObject(id, OpenMode.ForRead) as CivAssembly; }
                catch { continue; }
                if (assembly != null) list.Add(assembly);
            }
            return list;
        }

        static CivAssembly FindAssembly(Database db, Transaction tr, string name)
        {
            foreach (ObjectId id in ModelSpace(db, tr))
            {
                CivAssembly assembly;
                try { assembly = tr.GetObject(id, OpenMode.ForRead) as CivAssembly; }
                catch { continue; }
                if (assembly != null &&
                    string.Equals(assembly.Name, name, StringComparison.OrdinalIgnoreCase))
                    return assembly;
            }
            return null;
        }

        static List<CivSubassembly> ComposerSubassemblies(CivAssembly assembly, Transaction tr)
        {
            var result = new List<CivSubassembly>();
            foreach (Autodesk.Civil.DatabaseServices.AssemblyGroup group in assembly.Groups)
            {
                foreach (ObjectId id in group.GetSubassemblyIds())
                {
                    var sa = tr.GetObject(id, OpenMode.ForRead) as CivSubassembly;
                    if (sa != null && sa.IsComposer()) result.Add(sa);
                }
            }
            return result;
        }

        static string ResolvePkt(CivSubassembly sa, string leftPath, string rightPath)
        {
            string marker = (sa.Name + " " + sa.Side).ToLowerInvariant();
            if (marker.Contains("left")) return leftPath;
            if (marker.Contains("right")) return rightPath;
            throw new InvalidOperationException(
                "无法自动判断 SAC 子装配左右侧：" + sa.Name + "（Side=" + sa.Side + "）");
        }

        static void CopyParamValues(object sourceCollection, object targetCollection)
        {
            var source = ToObjectList(sourceCollection);
            var target = ToObjectList(targetCollection);
            var used = new HashSet<int>();
            for (int i = 0; i < source.Count; i++)
            {
                object src = source[i];
                string display = ReadProperty(src, "DisplayName") as string;
                int match = -1;
                if (!string.IsNullOrEmpty(display))
                {
                    for (int j = 0; j < target.Count; j++)
                    {
                        if (used.Contains(j)) continue;
                        string candidate = ReadProperty(target[j], "DisplayName") as string;
                        if (string.Equals(display, candidate, StringComparison.Ordinal))
                        {
                            match = j;
                            break;
                        }
                    }
                }
                if (match < 0 && i < target.Count && !used.Contains(i)) match = i;
                if (match < 0) continue;

                PropertyInfo srcValue = src.GetType().GetProperty("Value");
                PropertyInfo dstValue = target[match].GetType().GetProperty("Value");
                if (srcValue == null || dstValue == null || !srcValue.CanRead || !dstValue.CanWrite)
                    continue;
                try
                {
                    dstValue.SetValue(target[match], srcValue.GetValue(src));
                    used.Add(match);
                }
                catch { }
            }
        }

        static List<object> ToObjectList(object collection)
        {
            var result = new List<object>();
            var enumerable = collection as IEnumerable;
            if (enumerable != null)
                foreach (object item in enumerable) result.Add(item);
            return result;
        }

        static object ReadProperty(object target, string name)
        {
            try
            {
                PropertyInfo p = target.GetType().GetProperty(name);
                return p != null && p.CanRead ? p.GetValue(target) : null;
            }
            catch { return null; }
        }

        static string SafeString(Func<string> getter)
        {
            try { return getter(); } catch { return null; }
        }

        static bool SafeBool(Func<bool> getter)
        {
            try { return getter(); } catch { return false; }
        }
    }
}
