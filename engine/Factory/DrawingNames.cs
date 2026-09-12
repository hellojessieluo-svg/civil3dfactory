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
    /// For the parameter dialog: lists the selectable object names in the current drawing by node.json input type.
    /// Written as a partial class of Ops so it can reuse the existing private helpers such as Civ()/ModelSpace().
    /// Any failure degrades to an empty list; the UI still accepts a typed name, and a failed listing must never block execution.
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

        /// <summary>Plain-CAD name tables: layers, blocks, text/dim styles, linetypes, layouts, plot style tables.</summary>
        static List<string> DwgNames(string key, Document doc)
        {
            var names = new List<string>();

            // plot style tables come from the CAD search path, not the drawing; handled separately
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
                                // model/paper space and anonymous blocks are not insertable title blocks; keep them out of the dropdown
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

        /// <summary>Civil 3D styles / label sets / band sets / code sets. Each category has its own try so a missing one does not affect the rest.</summary>
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

        /// <summary>Contract style source key -> Civil 3D style collection. Adding a key is one more line here.</summary>
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
