using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivSampleLineGroup = Autodesk.Civil.DatabaseServices.SampleLineGroup;
using CivSectionSource = Autodesk.Civil.DatabaseServices.SectionSource;
using CivSection = Autodesk.Civil.DatabaseServices.Section;

namespace Civil3DFactory
{
    /// <summary>
    ///
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeSetSampleLineSources(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            string groupName = GetString(a, "group", null);
            bool sampled = GetBool(a, "sampled", true);
            string sectionStyle = GetString(a, "section_style", null);
            bool draw = GetBool(a, "draw", true);
            var wanted = new List<string>();
            var arr = a["sources"] as JsonArray;
            if (arr != null) foreach (JsonNode n in arr) if (n != null) wanted.Add(n.ToString());
            string single = GetString(a, "surface", null);
            if (!string.IsNullOrEmpty(single)) wanted.Add(single);
            if (wanted.Count == 0)
                throw new InvalidOperationException("Specify sources:[surface_name,...] or surface:surface_name.");

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = FindAlignment(tr, civ, alName);
                if (al == null) throw new InvalidOperationException("Alignment '" + alName + "'.");

                var gids = al.GetSampleLineGroupIds();
                if (gids.Count == 0)
                    throw new InvalidOperationException("Alignment '" + alName + "' has no sample line groups; run create_sample_lines first.");
                CivSampleLineGroup slg = null;
                var names = new List<string>();
                foreach (ObjectId gid in gids)
                {
                    var g = (CivSampleLineGroup)tr.GetObject(gid, OpenMode.ForWrite);
                    names.Add(g.Name);
                    if (groupName == null && gids.Count == 1) slg = g;
                    else if (groupName == null && g.Name == alName + "_SampleLines") slg = g;
                    else if (groupName != null && g.Name == groupName) slg = g;
                }
                if (slg == null)
                    throw new InvalidOperationException("Sample line group not found (group=" + (groupName ?? "default") + "); groups on this alignment: " + string.Join(", ", names));

                ObjectId secStyleId = FindStyleId(tr, civ.Styles.SectionStyles, sectionStyle);

                var rebuilt = new JsonArray();
                foreach (string wn in wanted)
                {
                    ObjectId sid = FindSurfaceId(tr, civ, wn);
                    if (sid.IsNull) continue;
                    try
                    {
                        var sf = tr.GetObject(sid, OpenMode.ForWrite);
                        var pOut = sf.GetType().GetProperty("IsOutOfDate");
                        bool od = pOut != null && (bool)pOut.GetValue(sf);
                        var mRb = sf.GetType().GetMethod("Rebuild", Type.EmptyTypes);
                        if (mRb != null) mRb.Invoke(sf, null);
                        bool od2 = pOut != null && (bool)pOut.GetValue(sf);
                        rebuilt.Add(wn + ": outdated " + od + " -> " + od2);
                    }
                    catch (System.Exception ex) { rebuilt.Add(wn + ": rebuild failed " + (ex.InnerException ?? ex).Message); }
                }
                var changed = new JsonArray();
                var secSamples = new JsonArray();
                var missing = new List<string>(wanted);
                var all = new JsonArray();
                foreach (CivSectionSource src in slg.GetSectionSources())
                {
                    string st = "";
                    try { st = src.SourceType.ToString(); } catch { }
                    bool hit = wanted.Contains(src.SourceNameOf());
                    bool before = src.IsSampled;
                    int sections = 0;
                    int styled = 0;
                    if (hit)
                    {
                        missing.Remove(src.SourceNameOf());
                        if (before != sampled) src.IsSampled = sampled;
                        if (sampled)
                        {
                            foreach (ObjectId secId in src.GetSectionIds())
                            {
                                sections++;
                                if (secStyleId.IsNull) continue;
                                try
                                {
                                    var sec = (CivSection)tr.GetObject(secId, OpenMode.ForWrite);
                                    sec.StyleId = secStyleId;
                                    styled++;
                                }
                                catch { }
                            }
                        }
                        changed.Add(new JsonObject
                        {
                            ["source"] = src.SourceNameOf(),
                            ["type"] = st,
                            ["sampled_before"] = before,
                            ["sampled_after"] = src.IsSampled,
                            ["sections"] = sections,
                            ["sections_styled"] = styled
                        });
                    }
                    all.Add(src.SourceNameOf() + "  [" + st + "]" + (src.IsSampled ? " ✓" : ""));
                }
                double probeSt = GetDouble(a, "probe_station", -1);
                if (probeSt >= 0)
                    foreach (CivSectionSource src in slg.GetSectionSources())
                    {
                        if (!src.IsSampled) continue;
                        foreach (ObjectId secId in src.GetSectionIds())
                        {
                            try
                            {
                                var so = tr.GetObject(secId, OpenMode.ForRead);
                                var stp = so.GetType().GetProperty("Station");
                                double stv = stp == null ? -1 : Convert.ToDouble(stp.GetValue(so));
                                if (Math.Abs(stv - probeSt) > 0.5) continue;
                                var row = new JsonObject { ["source"] = src.SourceNameOf(), ["station"] = stv };
                                foreach (var pn in new[] { "LeftOffset", "RightOffset", "MinimumElevation", "MaximumElevation", "UpdateMode", "StyleName" })
                                {
                                    var pp = so.GetType().GetProperty(pn);
                                    if (pp == null) continue;
                                    object v = null; try { v = pp.GetValue(so); } catch (System.Exception ex) { v = "ERR " + (ex.InnerException ?? ex).GetType().Name + ":" + Truncate((ex.InnerException ?? ex).Message, 60); }
                                    row[pn] = v == null ? null : v.ToString();
                                }
                                secSamples.Add(row);
                            }
                            catch (System.Exception ex) { secSamples.Add(new JsonObject { ["source"] = src.SourceNameOf(), ["error"] = ex.Message }); }
                        }
                    }

                var newSecIds = new HashSet<ObjectId>();
                var newSecNames = new HashSet<string>();
                if (sampled && draw)
                {
                    foreach (CivSectionSource src in slg.GetSectionSources())
                        if (wanted.Contains(src.SourceNameOf()))
                        {
                            newSecNames.Add(src.SourceNameOf());
                            foreach (ObjectId secId in src.GetSectionIds()) newSecIds.Add(secId);
                        }
                }
                int viewsTouched = 0, drawSet = 0, overridesSeen = 0;
                var overrideProps = new JsonArray();
            var firstViewDump = new JsonArray();
                string drawError = null;
                if (newSecIds.Count > 0)
                {
                    foreach (ObjectId slId in slg.GetSampleLineIds())
                    {
                        var sl = (Autodesk.Civil.DatabaseServices.SampleLine)tr.GetObject(slId, OpenMode.ForRead);
                        foreach (ObjectId svId in sl.GetSectionViewIds())
                        {
                            try
                            {
                                var sv = tr.GetObject(svId, OpenMode.ForWrite);
                                var pi = sv.GetType().GetProperty("GraphOverrides");
                                if (pi == null) { drawError = "SectionView has no GraphOverrides property"; break; }
                                var coll = pi.GetValue(sv) as System.Collections.IEnumerable;
                                if (coll == null) continue;
                                viewsTouched++;
                                foreach (object ov in coll)
                                {
                                    overridesSeen++;
                                    var t = ov.GetType();
                                    if (overrideProps.Count == 0)
                                        foreach (var pp in t.GetProperties()) overrideProps.Add(pp.Name + ":" + pp.PropertyType.Name);
                                    if (viewsTouched == 1 && probeSt >= 0)
                                    {
                                        var row = new JsonObject();
                                        foreach (var pp in t.GetProperties())
                                        {
                                            object v = null; try { v = pp.GetValue(ov); } catch { }
                                            row[pp.Name] = v == null ? null : v.ToString();
                                            if (v is ObjectId) row[pp.Name + "_isNew"] = newSecIds.Contains((ObjectId)v);
                                        }
                                        firstViewDump.Add(row);
                                    }
                                    bool hit = false;
                                    foreach (var pp in t.GetProperties())
                                    {
                                        object val = null;
                                        try { val = pp.GetValue(ov); } catch { }
                                        if (val is ObjectId && newSecIds.Contains((ObjectId)val)) { hit = true; break; }
                                        if (val is string && newSecNames.Contains((string)val)) { hit = true; break; }
                                    }
                                    if (!hit) continue;
                                    var dp = t.GetProperty("Draw");
                                    if (dp != null && dp.CanWrite && !(bool)dp.GetValue(ov)) { dp.SetValue(ov, true); drawSet++; }
                                }
                            }
                            catch (System.Exception ex) { drawError = ex.GetType().Name + ": " + ex.Message; }
                        }
                    }
                }

                if (missing.Count > 0)
                    throw new InvalidOperationException(
                        "Sample line group '" + slg.Name + "' has no section sources named: " + string.Join(", ", missing) +
                        ". Check list_surfaces; available sources: " + string.Join("; ", all));

                tr.Commit();
                return new JsonObject
                {
                    ["alignment"] = alName,
                    ["group"] = slg.Name,
                    ["sampled"] = sampled,
                    ["section_style"] = secStyleId.IsNull ? null : sectionStyle,
                    ["surfaces_rebuilt"] = rebuilt,
                    ["changed"] = changed,
                    ["views_touched"] = viewsTouched,
                    ["overrides_seen"] = overridesSeen,
                    ["draw_set"] = drawSet,
                    ["override_props"] = overrideProps,
                    ["first_view_overrides"] = firstViewDump,
                    ["section_samples"] = secSamples,
                    ["draw_error"] = drawError,
                    ["sources_after"] = all
                };
            }
        }
    }
}
