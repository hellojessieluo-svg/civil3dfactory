using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivSampleLine = Autodesk.Civil.DatabaseServices.SampleLine;
using CivSampleLineGroup = Autodesk.Civil.DatabaseServices.SampleLineGroup;
using CivSectionSource = Autodesk.Civil.DatabaseServices.SectionSource;
using CivQtoMaterialList = Autodesk.Civil.DatabaseServices.QTOMaterialList;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        /// <summary>
        /// Restyle **existing** section views in place / set the elevation range; no delete/rebuild.
        ///
        /// Why this part exists: create_section_views overwrite-rebuild must delete the old views first, and when they carry volume tables
        /// save_dwg then always fails with eWasOpenForWrite (bisected on project B, 2026-08-16: even the parameters copied from a
        /// successful task card crash on their own; the only combination that ever worked had "create_sample_lines deleting the group and its views" first).
        /// View style and elevation are set by create_section_views **after** creation anyway (same mechanism),
        /// so re-setting them on existing views is equivalent, and sample lines, material lists, volume tables and quantities are untouched.
        /// </summary>
        static JsonNode RunNodeRestyleSectionViews(JsonObject a, Document doc)
        {
            string alName = GetString(a, "alignment", null);          // default = all alignments
            string style = GetString(a, "style", null);               // section view style (SectionViewStyles)
            string secStyle = GetString(a, "section_style", null);    // ground line section style (SectionStyles)
            string matStyle = GetString(a, "material_style", null);   // material section style (controls the cut hatch look); untouched when omitted
            // Material **shape** style (ShapeStyles): controls how the cut hatch area looks on the sheet,
            // attached to QTOMaterial.ShapeStyleId in the material list; a different thing from the section style
            string matShape = GetString(a, "material_shape_style", null);
            // Corridor surface sections (SourceType=CorridorSurface): point to a no-plot style to hide them; untouched when omitted
            string corSurfStyle = GetString(a, "corridor_surface_style", null);
            double elevMin = GetDouble(a, "elev_min", 0);
            double elevMax = GetDouble(a, "elev_max", 0);             // min>=max -> elevation untouched
            bool manualElev = elevMin < elevMax;
            bool autoElev = GetBool(a, "elev_auto", false);           // explicitly back to automatic
            if (string.IsNullOrEmpty(style) && string.IsNullOrEmpty(secStyle)
                && string.IsNullOrEmpty(matStyle) && string.IsNullOrEmpty(matShape)
                && string.IsNullOrEmpty(corSurfStyle) && !manualElev && !autoElev)
                throw new InvalidOperationException("Give at least one of style / section_style / material_style / material_shape_style / corridor_surface_style / elev_min+elev_max / elev_auto.");

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            int views = 0, groundStyled = 0, matStyled = 0, alignHit = 0, matShapeSet = 0, corSurfStyled = 0;
            string matStyleResolved = null;
            var perAlign = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId svStyleId = ObjectId.Null;
                if (!string.IsNullOrEmpty(style))
                {
                    svStyleId = FindStyleId(tr, civ.Styles.SectionViewStyles, style);
                    if (svStyleId.IsNull)
                        throw new InvalidOperationException("Section view style '" + style + "' not found.");
                }
                ObjectId secStyleId = string.IsNullOrEmpty(secStyle)
                    ? ObjectId.Null : FindStyleId(tr, civ.Styles.SectionStyles, secStyle);
                if (!string.IsNullOrEmpty(secStyle) && secStyleId.IsNull)
                    throw new InvalidOperationException("Section style '" + secStyle + "' not found.");
                // Material sections (cut hatch): **untouched when omitted**.
                // Do not fall back to the ground line style -- that would overwrite the user's hand-set @C3DF-CutFill with C3DF-GroundLine
                // (learned 2026-08-16 after the user hand-set Style=@C3DF-CutFill on 215 Material Sections in the properties panel:
                // rendering reads the MaterialSection.StyleId slot).
                // Look the name up in SectionStyles first, then ShapeStyles -- @C3DF-CutFill lives in the latter.
                // FindStyleId falls back to the first item on no match (never Null); a two-level lookup needs the strict version
                ObjectId matStyleId = ObjectId.Null;
                if (!string.IsNullOrEmpty(matStyle))
                {
                    matStyleId = FindStyleIdStrict(tr, civ.Styles.SectionStyles, matStyle);
                    if (matStyleId.IsNull)
                        matStyleId = FindStyleIdStrict(tr, civ.Styles.ShapeStyles, matStyle);
                    if (matStyleId.IsNull)
                        throw new InvalidOperationException("Material section style '" + matStyle + "' not found in SectionStyles or ShapeStyles.");
                    matStyleResolved = StyleName(tr.GetObject(matStyleId, OpenMode.ForRead));
                }
                ObjectId matShapeId = ObjectId.Null;
                if (!string.IsNullOrEmpty(matShape))
                {
                    matShapeId = FindStyleId(tr, civ.Styles.ShapeStyles, matShape);
                    if (matShapeId.IsNull)
                        throw new InvalidOperationException("Shape style (ShapeStyle) '" + matShape + "' not found.");
                }
                ObjectId corSurfId = ObjectId.Null;
                if (!string.IsNullOrEmpty(corSurfStyle))
                {
                    corSurfId = FindStyleIdStrict(tr, civ.Styles.SectionStyles, corSurfStyle);
                    if (corSurfId.IsNull)
                        throw new InvalidOperationException("Corridor surface section style '" + corSurfStyle + "' not found (SectionStyles).");
                }

                foreach (ObjectId alId in ModelSpace(db, tr))
                {
                    CivAlignment al;
                    try { al = tr.GetObject(alId, OpenMode.ForRead) as CivAlignment; }
                    catch { continue; }
                    if (al == null) continue;
                    if (!string.IsNullOrEmpty(alName)
                        && !string.Equals(al.Name, alName, StringComparison.OrdinalIgnoreCase)) continue;

                    int myViews = 0;
                    foreach (ObjectId gid in al.GetSampleLineGroupIds())
                    {
                        // The group must be open for write: when section styles under GetSectionSources change, Civil writes back into the group,
                        // and a read-only group hard-crashes the process with eNotOpenForWrite (verified 2026-08-16;
                        // create_section_views also does slg.UpgradeOpen before touching the sources)
                        var g = (CivSampleLineGroup)tr.GetObject(gid, OpenMode.ForWrite);
                        foreach (ObjectId slId in g.GetSampleLineIds())
                        {
                            var sl = (CivSampleLine)tr.GetObject(slId, OpenMode.ForRead);
                            // Material sections are not listed under the group's section sources; reach them through the
                            // sample line's own sections and set their style slot (a ShapeStyle renders the hatch).
                            if (!matStyleId.IsNull)
                            {
                                ObjectIdCollection secIds = null;
                                try { secIds = sl.GetSectionIds(); } catch { }
                                if (secIds != null)
                                    foreach (ObjectId secId in secIds)
                                    {
                                        try
                                        {
                                            DBObject so = tr.GetObject(secId, OpenMode.ForRead);
                                            if (so.GetType().Name != "MaterialSection") continue;
                                            so.UpgradeOpen();
                                            ((Autodesk.Civil.DatabaseServices.Section)so).StyleId = matStyleId;
                                            matStyled++;
                                        }
                                        catch { }
                                    }
                            }
                            foreach (ObjectId svId in sl.GetSectionViewIds())
                            {
                                var sv = (Autodesk.Civil.DatabaseServices.SectionView)
                                    tr.GetObject(svId, OpenMode.ForWrite);
                                if (!svStyleId.IsNull) sv.StyleId = svStyleId;
                                if (manualElev)
                                {
                                    sv.IsElevationRangeAutomatic = false;
                                    sv.ElevationMin = elevMin;
                                    sv.ElevationMax = elevMax;
                                }
                                else if (autoElev) sv.IsElevationRangeAutomatic = true;
                                myViews++;
                            }
                        }

                        // Material shape style: set ShapeStyleId on every QTOMaterial in the material list
                        if (!matShapeId.IsNull)
                        {
                            foreach (CivQtoMaterialList ml in g.MaterialLists)
                            {
                                foreach (object item in (System.Collections.IEnumerable)ml)
                                {
                                    var p = item?.GetType().GetProperty("ShapeStyleId");
                                    if (p == null || !p.CanWrite) continue;
                                    try { p.SetValue(item, matShapeId); matShapeSet++; }
                                    catch { }
                                }
                            }
                        }

                        // Section styles: the corridor body section's StyleId is a code set style, **untouched** here
                        // (skip SourceType containing Corridor but not CorridorSurface);
                        // material sections -> material_style, the rest (ground line / corridor surface) -> section_style.
                        // Classification copied from create_section_views (verified property by property on 2026-07-28).
                        if (secStyleId.IsNull && matStyleId.IsNull && corSurfId.IsNull) continue;
                        foreach (CivSectionSource src in g.GetSectionSources())
                        {
                            string sourceType = "";
                            try { sourceType = src.SourceType.ToString(); } catch { }
                            bool isMaterial = sourceType.IndexOf("Material", StringComparison.OrdinalIgnoreCase) >= 0;
                            bool isCorridorSurface = !isMaterial
                                && sourceType.IndexOf("CorridorSurface", StringComparison.OrdinalIgnoreCase) >= 0;
                            bool isCorridorBody = !isMaterial && !isCorridorSurface
                                && sourceType.IndexOf("Corridor", StringComparison.OrdinalIgnoreCase) >= 0;
                            if (isCorridorBody) continue;
                            ObjectId want = isMaterial ? matStyleId
                                          : isCorridorSurface ? (corSurfId.IsNull ? secStyleId : corSurfId)
                                          : secStyleId;
                            if (want.IsNull) continue;
                            foreach (ObjectId secId in src.GetSectionIds())
                            {
                                try
                                {
                                    var sec = (Autodesk.Civil.DatabaseServices.Section)
                                        tr.GetObject(secId, OpenMode.ForWrite);
                                    sec.StyleId = want;
                                    if (isMaterial) matStyled++;
                                    else if (isCorridorSurface && !corSurfId.IsNull) corSurfStyled++;
                                    else groundStyled++;
                                }
                                catch { }
                            }
                        }
                    }
                    if (myViews > 0)
                    {
                        alignHit++;
                        views += myViews;
                        perAlign.Add(new JsonObject { ["alignment"] = al.Name, ["views"] = myViews });
                    }
                }
                tr.Commit();
            }
            if (views == 0)
                throw new InvalidOperationException(string.IsNullOrEmpty(alName)
                    ? "The drawing has no section view."
                    : "Alignment '" + alName + "' has no section view.");

            return new JsonObject
            {
                ["alignments"] = alignHit,
                ["material_style_resolved"] = matStyleResolved,
                ["views_restyled"] = views,
                ["view_style"] = style,
                ["ground_sections_styled"] = groundStyled,
                ["material_sections_styled"] = matStyled,
                ["corridor_surface_sections_styled"] = corSurfStyled,
                ["material_shape_styles_set"] = matShapeSet,
                ["material_shape_style"] = matShape,
                ["elevation"] = manualElev ? elevMin + " ~ " + elevMax
                              : (autoElev ? "auto" : "unchanged"),
                ["per_alignment"] = perAlign
            };
        }

        /// <summary>Strict version of FindStyleId: exact > case-insensitive > prefix > contains; returns Null on no match
        /// (FindStyleId falls back to the first item; a cross-collection two-level lookup needs Null to move to the next level).</summary>
        static ObjectId FindStyleIdStrict(Transaction tr, object collection, string name)
        {
            if (string.IsNullOrEmpty(name)) return ObjectId.Null;
            if (!(collection is System.Collections.IEnumerable en)) return ObjectId.Null;
            var all = new List<KeyValuePair<ObjectId, string>>();
            foreach (object item in en)
            {
                if (!(item is ObjectId id)) continue;
                string n;
                try { n = StyleName(tr.GetObject(id, OpenMode.ForRead)); }
                catch { continue; }
                if (string.IsNullOrEmpty(n)) continue;
                if (n == name) return id;
                all.Add(new KeyValuePair<ObjectId, string>(id, n));
            }
            foreach (var kv in all)
                if (string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            foreach (var kv in all)
                if (kv.Value.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            foreach (var kv in all)
                if (kv.Value.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return kv.Key;
            return ObjectId.Null;
        }
    }
}
