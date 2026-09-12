using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivLabelStyle = Autodesk.Civil.DatabaseServices.Styles.LabelStyle;
using CivLabelStyleComponent = Autodesk.Civil.DatabaseServices.Styles.LabelStyleComponent;
using CivLabelStyleComponentType = Autodesk.Civil.DatabaseServices.Styles.LabelStyleComponentType;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        /// <summary>
        /// Read-only diagnostic: list label styles (LabelStyle) with the content and position parameters of their text components.
        ///
        /// Why this node exists: fixed text shown by labels (e.g. "Dredge control line") hides in the
        /// text components of the LabelStyle; DXFOUT never writes it as plain text (Civil custom objects go through proxies),
        /// so the API is the only way to read it.
        ///
        /// WARNING, lesson learned (three crashes on 2026-08-26): **never deep-crawl style objects by reflection**;
        /// flatten-style reflection touches properties like Database/Document and crashes accoreconsole natively
        /// (not a .NET exception; cannot be caught). This version uses only the strongly typed documented path:
        /// LabelStyle.GetComponents(Text) -> LabelStyleTextComponent.Text.Contents/XOffset/YOffset.
        /// </summary>
        static JsonNode RunNodeDumpLabelStyles(JsonObject a, Document doc)
        {
            // only report styles whose text content contains this string; report all if absent
            string contains = GetString(a, "contains", null);
            int maxStyles = (int)GetDouble(a, "max_styles", 800);

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var styles = new JsonArray();
            int scanned = 0, matched = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // Walk properties only to **collect ObjectIds** (verified safe on 08-26; the crash was in the later reflective expansion).
                var ids = new List<ObjectId>();
                var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
                object root = null;
                try { root = civ.Styles.GetType().GetProperty("LabelStyles").GetValue(civ.Styles); }
                catch { }
                CollectIds(root ?? (object)civ.Styles, ids, seen, 0, 7);

                // Code Set Style branch: Point/Link labels of corridor sections (fixed text like "Dredge control line")
                // hang on the LabelStyleId of each CodeSetStyle item, not under the LabelStyles root.
                // Also record "label style -> which code of which code set"; needed when hunting duplicate labels.
                var styleOwners = new Dictionary<ObjectId, List<string>>();
                try
                {
                    foreach (ObjectId csId in civ.Styles.CodeSetStyles)
                    {
                        var cs = tr.GetObject(csId, OpenMode.ForRead)
                            as Autodesk.Civil.DatabaseServices.Styles.CodeSetStyle;
                        if (cs == null) continue;
                        foreach (Autodesk.Civil.DatabaseServices.Styles.CodeSetStyleItem item in cs)
                        {
                            ObjectId lid = ObjectId.Null;
                            try { lid = item.LabelStyleId; } catch { }
                            if (lid.IsNull) continue;
                            ids.Add(lid);
                            string codeName = null;
                            try { codeName = item.Code; } catch { }
                            if (codeName == null)
                            {
                                // Property names vary by version (Code / CodeName / Description); reflection fetches a single string, no deep crawl
                                foreach (string pn in new[] { "CodeName", "Description" })
                                {
                                    try
                                    {
                                        var pi = item.GetType().GetProperty(pn);
                                        if (pi != null) { codeName = pi.GetValue(item) as string; }
                                    }
                                    catch { }
                                    if (codeName != null) break;
                                }
                            }
                            List<string> owners;
                            if (!styleOwners.TryGetValue(lid, out owners))
                                styleOwners[lid] = owners = new List<string>();
                            owners.Add(cs.Name + " / " + (codeName ?? "?"));
                        }
                    }
                }
                catch { }

                var done = new HashSet<ObjectId>();
                foreach (ObjectId id in ids)
                {
                    if (id.IsNull || !done.Add(id)) continue;
                    if (scanned >= maxStyles) break;
                    CivLabelStyle st;
                    try { st = tr.GetObject(id, OpenMode.ForRead) as CivLabelStyle; }
                    catch { continue; }
                    if (st == null) continue;
                    scanned++;

                    var comps = new JsonArray();
                    bool hit = false;
                    // Text components: content + offsets. Strongly typed only, no reflection.
                    foreach (CivLabelStyleComponentType ct in new CivLabelStyleComponentType[]
                    {
                        CivLabelStyleComponentType.Text,
                        CivLabelStyleComponentType.Line,
                        CivLabelStyleComponentType.Block
                    })
                    {
                        ObjectIdCollection cids;
                        try { cids = st.GetComponents(ct); } catch { continue; }
                        foreach (ObjectId cid in cids)
                        {
                            CivLabelStyleComponent c;
                            try { c = tr.GetObject(cid, OpenMode.ForRead) as CivLabelStyleComponent; }
                            catch { continue; }
                            if (c == null) continue;
                            var jc = new JsonObject
                            {
                                ["kind"] = ct.ToString(),
                                ["name"] = c.Name,
                                ["type"] = c.GetType().Name
                            };
                            var txt = c as Autodesk.Civil.DatabaseServices.Styles.LabelStyleTextComponent;
                            if (txt != null)
                            {
                                string contents = null;
                                try { contents = txt.Text.Contents.Value; } catch { }
                                jc["contents"] = contents;
                                try { jc["x_offset"] = txt.Text.XOffset.Value; } catch { }
                                try { jc["y_offset"] = txt.Text.YOffset.Value; } catch { }
                                try { jc["height"] = txt.Text.Height.Value; } catch { }
                                try { jc["attachment"] = txt.Text.Attachment.Value.ToString(); } catch { }
                                try { jc["angle"] = txt.Text.Angle.Value; } catch { }
                                if (contains != null && contents != null &&
                                    contents.IndexOf(contains, StringComparison.Ordinal) >= 0)
                                    hit = true;
                            }
                            // The two anchor items are in the General group: AnchorComponent (which component/feature it hangs on), AnchorLocation (which point of it).
                            // Vertical spacing between components is determined by "anchor + attachment + offset"; reporting offsets alone hides the source of the spacing.
                            {
                                object ac = GetGroupProp(c, "General", "AnchorComponent");
                                if (ac != null) jc["anchor_component"] = ac.ToString();
                                object al = GetGroupProp(c, "General", "AnchorLocation") ?? GetGroupProp(c, "General", "AnchorPoint");
                                if (al != null) jc["anchor_location"] = al.ToString();
                                if (GetBool(a, "debug_props", false))
                                {
                                    JsonObject gen = ShallowPropertyValues(c.GetType().GetProperty("General")?.GetValue(c));
                                    if (gen != null) jc["general"] = gen;
                                }
                            }
                            comps.Add(jc);
                        }
                    }

                    if (contains != null && !hit) continue;
                    matched++;
                    var entry = new JsonObject
                    {
                        ["style_type"] = st.GetType().Name,
                        ["name"] = st.Name,
                        ["handle"] = st.Handle.ToString(),
                        ["components"] = comps
                    };
                    // Dragged State page and Leader page: read only the Value of the Property* wrappers,
                    // one whitelisted level, no deep crawl (avoids the 08-26 style native crash).
                    JsonObject ds = ShallowPropertyValues(GetGroupProp(st, "Properties", "DraggedStateComponents", false));
                    if (ds != null && ds.Count > 0) entry["dragged_state"] = ds;
                    JsonObject ld = ShallowPropertyValues(GetGroupProp(st, "Properties", "Leader", false));
                    if (ld != null && ld.Count > 0) entry["leader"] = ld;
                    List<string> own;
                    if (styleOwners.TryGetValue(id, out own))
                    {
                        var ja = new JsonArray();
                        foreach (string o2 in own) ja.Add(o2);
                        entry["used_by_codes"] = ja;
                    }
                    styles.Add(entry);
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["contains"] = contains,
                ["styles_scanned"] = scanned,
                ["styles_matched"] = matched,
                ["styles"] = styles
            };
        }

        /// <summary>Recursively collect ObjectIds in the object tree (style collections are mostly IEnumerable&lt;ObjectId&gt;).
        /// Collect ids only, never open objects; verified safe.</summary>
        static void CollectIds(object root, List<ObjectId> outIds, HashSet<object> seen, int depth, int maxDepth)
        {
            if (root == null || depth > maxDepth) return;
            if (root is ObjectId oid) { if (!oid.IsNull) outIds.Add(oid); return; }
            if (root is string || root.GetType().IsPrimitive) return;
            if (!root.GetType().IsValueType && !seen.Add(root)) return;

            if (root is IEnumerable en && !(root is string))
            {
                IEnumerator it;
                try { it = en.GetEnumerator(); } catch { return; }
                while (true)
                {
                    object cur;
                    try { if (!it.MoveNext()) break; cur = it.Current; }
                    catch { break; }
                    CollectIds(cur, outIds, seen, depth + 1, maxDepth);
                }
                return;
            }

            foreach (PropertyInfo p in root.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                // Drill into style-tree containers only, never engine objects
                string tn = p.PropertyType.Name;
                bool container = tn.IndexOf("Styles", StringComparison.OrdinalIgnoreCase) >= 0
                              || tn.IndexOf("Collection", StringComparison.OrdinalIgnoreCase) >= 0
                              || tn.IndexOf("Root", StringComparison.OrdinalIgnoreCase) >= 0
                              || p.PropertyType == typeof(ObjectId);
                if (!container) continue;
                object v;
                try { v = p.GetValue(root); } catch { continue; }
                CollectIds(v, outIds, seen, depth + 1, maxDepth);
            }
        }

        sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            bool IEqualityComparer<object>.Equals(object x, object y) { return ReferenceEquals(x, y); }
            int IEqualityComparer<object>.GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
