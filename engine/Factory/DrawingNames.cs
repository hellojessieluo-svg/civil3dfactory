using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivAssembly = Autodesk.Civil.DatabaseServices.Assembly;
using CivCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using CivProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivSampleLineGroup = Autodesk.Civil.DatabaseServices.SampleLineGroup;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// 参数对话框用：按 node.json 的 inputs 类型，列出当前图纸里可选的对象名。
    /// 写成 Ops 的分部类是为了直接复用 Civ()/ModelSpace() 这些既有私有辅助。
    /// 任何一步失败都退化成空列表——界面上仍可手工输入名字，不能因为列不出来就挡住执行。
    /// </summary>
    public static partial class Ops
    {
        public static List<string> UiListNames(string typeKey, Document doc)
        {
            var names = new List<string>();
            if (doc == null || string.IsNullOrEmpty(typeKey)) return names;

            string key = typeKey.Trim();
            if (key.EndsWith("[]", StringComparison.Ordinal)) key = key.Substring(0, key.Length - 2);

            if (key.StartsWith("dwg.", StringComparison.OrdinalIgnoreCase))
                return DwgNames(key.ToLowerInvariant(), doc);
            if (key.StartsWith("civil.style.", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("civil.labelset.", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("civil.bandset.", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("civil.codeset", StringComparison.OrdinalIgnoreCase))
                return StyleNames(key.ToLowerInvariant(), doc);
            if (!key.StartsWith("civil.", StringComparison.OrdinalIgnoreCase)) return names;

            try
            {
                Database db = doc.Database;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "civil.alignment":
                            foreach (ObjectId id in Civ(db).GetAlignmentIds())
                                Add(names, SafeName(tr, id));
                            break;

                        case "civil.surface":
                        case "civil.corridor_surface":
                            foreach (ObjectId id in Civ(db).GetSurfaceIds())
                                Add(names, SafeName(tr, id));
                            break;

                        case "civil.profile":
                            foreach (ObjectId aid in Civ(db).GetAlignmentIds())
                            {
                                var al = tr.GetObject(aid, OpenMode.ForRead) as CivAlignment;
                                if (al == null) continue;
                                foreach (ObjectId pid in al.GetProfileIds())
                                    Add(names, SafeName(tr, pid));
                            }
                            break;

                        case "civil.sample_line_group":
                            foreach (ObjectId aid in Civ(db).GetAlignmentIds())
                            {
                                var al = tr.GetObject(aid, OpenMode.ForRead) as CivAlignment;
                                if (al == null) continue;
                                foreach (ObjectId gid in al.GetSampleLineGroupIds())
                                    Add(names, SafeName(tr, gid));
                            }
                            break;

                        case "civil.corridor":
                            foreach (ObjectId id in ModelSpace(db, tr))
                            {
                                var c = tr.GetObject(id, OpenMode.ForRead) as CivCorridor;
                                if (c != null) Add(names, c.Name);
                            }
                            break;

                        case "civil.assembly":
                            foreach (ObjectId id in ModelSpace(db, tr))
                            {
                                CivAssembly asm;
                                try { asm = tr.GetObject(id, OpenMode.ForRead) as CivAssembly; }
                                catch { continue; }
                                if (asm != null) Add(names, asm.Name);
                            }
                            break;
                    }
                    tr.Commit();
                }
            }
            catch { }

            names.Sort(StringComparer.CurrentCulture);
            return names;
        }

        /// <summary>纯 CAD 侧的命名表：图层、块、文字/标注样式、线型、布局、打印样式表。</summary>
        static List<string> DwgNames(string key, Document doc)
        {
            var names = new List<string>();

            // 打印样式表来自 CAD 的搜索路径，不在图纸里，单独处理
            if (key == "dwg.ctb" || key == "dwg.plotstyle")
            {
                try
                {
                    foreach (string s in PlotSettingsValidator.Current.GetPlotStyleSheetList())
                        Add(names, s);
                }
                catch { }
                names.Sort(StringComparer.CurrentCulture);
                return names;
            }

            try
            {
                Database db = doc.Database;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    switch (key)
                    {
                        case "dwg.layer":
                            foreach (ObjectId id in (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead))
                            {
                                var r = tr.GetObject(id, OpenMode.ForRead) as LayerTableRecord;
                                if (r != null) Add(names, r.Name);
                            }
                            break;

                        case "dwg.blockname":
                            foreach (ObjectId id in (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead))
                            {
                                var r = tr.GetObject(id, OpenMode.ForRead) as BlockTableRecord;
                                // 模型/布局空间和匿名块不是可插入的图框块，别塞进下拉里
                                if (r == null || r.IsLayout || r.IsAnonymous) continue;
                                Add(names, r.Name);
                            }
                            break;

                        case "dwg.textstyle":
                            foreach (ObjectId id in (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead))
                            {
                                var r = tr.GetObject(id, OpenMode.ForRead) as TextStyleTableRecord;
                                if (r != null) Add(names, r.Name);
                            }
                            break;

                        case "dwg.dimstyle":
                            foreach (ObjectId id in (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead))
                            {
                                var r = tr.GetObject(id, OpenMode.ForRead) as DimStyleTableRecord;
                                if (r != null) Add(names, r.Name);
                            }
                            break;

                        case "dwg.linetype":
                            foreach (ObjectId id in (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead))
                            {
                                var r = tr.GetObject(id, OpenMode.ForRead) as LinetypeTableRecord;
                                if (r != null) Add(names, r.Name);
                            }
                            break;

                        case "dwg.layout":
                            var dict = tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead) as DBDictionary;
                            if (dict != null)
                                foreach (DBDictionaryEntry e in dict)
                                    Add(names, e.Key);
                            break;
                    }
                    tr.Commit();
                }
            }
            catch { }

            names.Sort(StringComparer.CurrentCulture);
            return names;
        }

        /// <summary>Civil 3D 的样式 / 标注集 / 带状集 / 代码集。每一类单独 try，缺哪类都不影响其余。</summary>
        static List<string> StyleNames(string key, Document doc)
        {
            var names = new List<string>();
            try
            {
                Database db = doc.Database;
                CivDoc civ = Civ(db);
                if (civ == null) return names;

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    System.Collections.IEnumerable source = StyleSource(civ, key);
                    if (source != null)
                    {
                        foreach (ObjectId id in source)
                        {
                            try
                            {
                                var s = tr.GetObject(id, OpenMode.ForRead)
                                    as Autodesk.Civil.DatabaseServices.Styles.StyleBase;
                                if (s != null) Add(names, s.Name);
                            }
                            catch { }
                        }
                    }
                    tr.Commit();
                }
            }
            catch { }

            names.Sort(StringComparer.CurrentCulture);
            return names;
        }

        /// <summary>契约里的样式源键 → Civil 3D 的样式集合。加新键只要在这里补一行。</summary>
        static System.Collections.IEnumerable StyleSource(CivDoc civ, string key)
        {
            try
            {
                switch (key)
                {
                    case "civil.style.alignment": return civ.Styles.AlignmentStyles;
                    case "civil.style.profile": return civ.Styles.ProfileStyles;
                    case "civil.style.profile_view": return civ.Styles.ProfileViewStyles;
                    case "civil.style.surface": return civ.Styles.SurfaceStyles;
                    case "civil.style.section": return civ.Styles.SectionStyles;
                    case "civil.style.section_view": return civ.Styles.SectionViewStyles;
                    case "civil.style.sample_line": return civ.Styles.SampleLineStyles;

                    case "civil.labelset.alignment": return civ.Styles.LabelSetStyles.AlignmentLabelSetStyles;
                    case "civil.labelset.profile": return civ.Styles.LabelSetStyles.ProfileLabelSetStyles;
                    case "civil.labelset.section": return civ.Styles.LabelSetStyles.SectionLabelSetStyles;

                    case "civil.bandset.profile_view": return civ.Styles.ProfileViewBandSetStyles;
                    case "civil.bandset.section_view": return civ.Styles.SectionViewBandSetStyles;

                    case "civil.codeset": return civ.Styles.CodeSetStyles;
                }
            }
            catch { }
            return null;
        }

        static void Add(List<string> names, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!names.Contains(name)) names.Add(name);
        }

        static string SafeName(Transaction tr, ObjectId id)
        {
            try
            {
                DBObject o = tr.GetObject(id, OpenMode.ForRead);
                var surface = o as CivSurface;
                if (surface != null) return surface.Name;
                var alignment = o as CivAlignment;
                if (alignment != null) return alignment.Name;
                var profile = o as CivProfile;
                if (profile != null) return profile.Name;
                var group = o as CivSampleLineGroup;
                if (group != null) return group.Name;
                return TryGetName(o);
            }
            catch { return null; }
        }
    }
}
