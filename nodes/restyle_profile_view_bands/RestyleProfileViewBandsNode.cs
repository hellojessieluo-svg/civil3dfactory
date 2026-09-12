using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivProfileView = Autodesk.Civil.DatabaseServices.ProfileView;
using CivBandItem = Autodesk.Civil.DatabaseServices.ProfileViewBandItem;
using CivBandItems = Autodesk.Civil.DatabaseServices.ProfileViewBandItemCollection;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        /// <summary>
        /// Swap band styles on **existing** profile views; no delete/rebuild, view style and labels untouched.
        ///
        /// Why this part exists: create_profile_view can only apply a whole band set **at creation time** and cannot help
        /// views already produced; changing four band styles across dozens of views in the GUI means opening each one's
        /// Profile View Properties -> Bands by hand.
        ///
        /// **Why by position (index) instead of an "old style name -> new style name" map** (verified 2026-08-27, do not retry):
        /// in the Civil 3D 2025 managed API ProfileViewBandItem.BandStyleId is write-only
        /// (a reflection probe shows BandStyleId(ObjectId,w) with no getter; the sibling Profile1Id/Profile2Id are r,w).
        /// Without reading the current style name there is no name-based swap. Misalignment risk is caught by a count check: each view's band count
        /// must equal the given array length; otherwise the whole view is skipped and reported in views_mismatched, never half-written.
        /// Run dry_run first and check the per_view fingerprint (BandType/Gap/intervals/label switches);
        /// if the views are position-wise identical they came from the same band set and the positional order can be trusted.
        /// </summary>
        static JsonNode RunNodeRestyleProfileViewBands(JsonObject a, Document doc)
        {
            List<string> bottomWant = StyleNameList(a, "bottom");
            List<string> topWant = StyleNameList(a, "top");
            if (bottomWant.Count == 0 && topWant.Count == 0)
                throw new InvalidOperationException(
                    "Give at least one of bottom / top: list style names by band **position from top to bottom**, "
                    + "e.g. bottom=[\"C3DF-GroundElevation\",\"C3DF-DesignElevation\",\"C3DF-CutFillDepth\",\"C3DF-Station\"]; "
                    + "null or an empty string in the array leaves that position untouched.");

            var onlyViews = new List<string>();
            if (a["views"] is JsonArray va)
                foreach (JsonNode v in va) { string s = v?.ToString(); if (!string.IsNullOrEmpty(s)) onlyViews.Add(s); }
            bool dryRun = GetBool(a, "dry_run", false);

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            int viewsScanned = 0, viewsChanged = 0, bandsChanged = 0;
            var perView = new JsonArray();
            var mismatched = new JsonArray();
            var viewsMissed = new List<string>(onlyViews);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // Target styles are looked up by **exact name** across every collection under CivilDocument.Styles.BandStyles.
                // Not via FindStyleId's "fall back to the first" -- a wrong band style still plots, just with all-wrong content,
                // which is far harder to catch than an error.
                Dictionary<string, ObjectId> bandStyles = CollectBandStyles(tr, civ);
                Dictionary<int, ObjectId> bottomIds = ResolveByIndex(bottomWant, bandStyles, "bottom");
                Dictionary<int, ObjectId> topIds = ResolveByIndex(topWant, bandStyles, "top");

                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    CivProfileView pv;
                    try { pv = tr.GetObject(id, OpenMode.ForRead) as CivProfileView; }
                    catch { continue; }
                    if (pv == null) continue;

                    string pvName = null;
                    try { pvName = pv.Name; } catch { }
                    if (onlyViews.Count > 0)
                    {
                        bool hit = false;
                        foreach (string want in onlyViews)
                            if (string.Equals(want, pvName, StringComparison.OrdinalIgnoreCase))
                            { hit = true; viewsMissed.Remove(want); break; }
                        if (!hit) continue;
                    }
                    viewsScanned++;

                    CivBandItems top = pv.Bands.GetTopBandItems();
                    CivBandItems bottom = pv.Bands.GetBottomBandItems();
                    // Skip the whole view when the counts differ: a misaligned positional swap scrambles all four bands, much worse than no change
                    string bad = null;
                    if (topWant.Count > 0 && top.Count != topWant.Count)
                        bad = "top has " + top.Count + " bands, " + topWant.Count + " positions given";
                    if (bottomWant.Count > 0 && bottom.Count != bottomWant.Count)
                        bad = (bad == null ? "" : bad + "; ") + "bottom has " + bottom.Count
                            + " bands, " + bottomWant.Count + " positions given";
                    if (bad != null)
                    {
                        mismatched.Add(new JsonObject { ["view"] = pvName, ["why"] = bad });
                        continue;
                    }

                    if (!dryRun) pv.UpgradeOpen();
                    var changes = new JsonArray();
                    JsonArray fp = Fingerprint(top, bottom);
                    int n = ApplyByIndex(top, topIds, "top", changes, bottomWant, topWant, dryRun);
                    n += ApplyByIndex(bottom, bottomIds, "bottom", changes, bottomWant, topWant, dryRun);
                    if (!dryRun && n > 0)
                    {
                        // The collection must be written back as a whole; band items are value copies (same as create_profile_view setting data sources)
                        if (topIds.Count > 0) pv.Bands.SetTopBandItems(top);
                        if (bottomIds.Count > 0) pv.Bands.SetBottomBandItems(bottom);
                    }
                    if (n > 0) { viewsChanged++; bandsChanged += n; }
                    perView.Add(new JsonObject
                    {
                        ["view"] = pvName,
                        ["bands_set"] = n,
                        ["fingerprint"] = fp,
                        ["changes"] = changes
                    });
                }
                if (dryRun) tr.Abort(); else tr.Commit();
            }

            if (viewsScanned == 0)
                throw new InvalidOperationException(onlyViews.Count > 0
                    ? "No profile view matched the views filter: " + string.Join(", ", onlyViews)
                    : "The drawing has no profile view (ProfileView).");

            var missedArr = new JsonArray();
            foreach (string s in viewsMissed) missedArr.Add(s);

            return new JsonObject
            {
                ["dry_run"] = dryRun,
                ["views_scanned"] = viewsScanned,
                ["views_changed"] = viewsChanged,
                ["bands_changed"] = bandsChanged,
                ["views_mismatched"] = mismatched,   // band count mismatch, whole view skipped
                ["views_not_found"] = missedArr,
                ["per_view"] = perView
            };
        }

        static List<string> StyleNameList(JsonObject a, string key)
        {
            var list = new List<string>();
            if (a[key] is JsonArray arr)
                foreach (JsonNode n in arr) list.Add(n?.ToString());
            return list;
        }

        /// <summary>Position -> style ObjectId. Empty positions (null/empty string) are left out = that band is untouched.</summary>
        static Dictionary<int, ObjectId> ResolveByIndex(
            List<string> want, Dictionary<string, ObjectId> bandStyles, string where)
        {
            var map = new Dictionary<int, ObjectId>();
            var missing = new List<string>();
            for (int i = 0; i < want.Count; i++)
            {
                string name = want[i];
                if (string.IsNullOrWhiteSpace(name)) continue;
                ObjectId id;
                if (!bandStyles.TryGetValue(name, out id)) { missing.Add(name); continue; }
                map[i] = id;
            }
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    where + ": these band styles do not exist in the drawing: " + string.Join(", ", missing)
                    + ". Available: " + string.Join(", ", new List<string>(bandStyles.Keys)));
            return map;
        }

        static int ApplyByIndex(CivBandItems items, Dictionary<int, ObjectId> ids, string position,
                                JsonArray changes, List<string> bottomWant, List<string> topWant, bool dryRun)
        {
            if (ids.Count == 0) return 0;
            List<string> want = position == "top" ? topWant : bottomWant;
            int done = 0, idx = 0;
            foreach (CivBandItem item in items)
            {
                ObjectId styleId;
                if (ids.TryGetValue(idx, out styleId))
                {
                    if (!dryRun) item.BandStyleId = styleId;   // write-only, cannot be read back for verification
                    changes.Add(new JsonObject
                    {
                        ["position"] = position,
                        ["index"] = idx,
                        ["to"] = want[idx]
                    });
                    done++;
                }
                idx++;
            }
            return done;
        }

        /// <summary>Readable band fingerprint: used to compare views for position-wise identity;
        /// identical = generated from the same band set, positional order trustworthy (BandStyleId is unreadable, so this is the only indirect proof).</summary>
        static JsonArray Fingerprint(CivBandItems top, CivBandItems bottom)
        {
            var arr = new JsonArray();
            foreach (string position in new[] { "top", "bottom" })
            {
                CivBandItems items = position == "top" ? top : bottom;
                int idx = 0;
                foreach (CivBandItem it in items)
                {
                    var o = new JsonObject { ["position"] = position, ["index"] = idx++ };
                    try { o["band_type"] = it.BandType.ToString(); } catch { }
                    try { o["gap"] = it.Gap; } catch { }
                    try { o["major"] = it.MajorInterval; } catch { }
                    try { o["minor"] = it.MinorInterval; } catch { }
                    try { o["show_labels"] = it.ShowLabels; } catch { }
                    try { o["label_start"] = it.LabelAtStartStation; } catch { }
                    try { o["label_end"] = it.LabelAtEndStation; } catch { }
                    try { o["weeding"] = it.Weeding; } catch { }
                    try { o["profile1"] = !it.Profile1Id.IsNull; } catch { }
                    try { o["profile2"] = !it.Profile2Id.IsNull; } catch { }
                    arr.Add(o);
                }
            }
            return arr;
        }

        /// <summary>Flatten every style collection under CivilDocument.Styles.BandStyles into "name -> ObjectId".
        /// Walks the property tree by reflection instead of hard-coding collection names (same as list_styles); first one wins on duplicate names.</summary>
        static Dictionary<string, ObjectId> CollectBandStyles(Transaction tr, CivDoc civ)
        {
            var result = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            object styles = civ.Styles;
            object root = null;
            foreach (PropertyInfo p in styles.GetType().GetProperties())
            {
                if (p.Name != "BandStyles") continue;
                try { root = p.GetValue(styles); } catch { }
                if (root != null) break;
            }
            if (root == null) return result;
            foreach (PropertyInfo p in root.GetType().GetProperties())
            {
                object coll = null;
                try { coll = p.GetValue(root); } catch { }
                if (!(coll is IEnumerable en)) continue;
                foreach (object o in en)
                {
                    if (!(o is ObjectId id)) continue;
                    string n = null;
                    try { n = StyleName(tr.GetObject(id, OpenMode.ForRead)); }
                    catch { }
                    if (string.IsNullOrEmpty(n) || result.ContainsKey(n)) continue;
                    result[n] = id;
                }
            }
            return result;
        }
    }
}
