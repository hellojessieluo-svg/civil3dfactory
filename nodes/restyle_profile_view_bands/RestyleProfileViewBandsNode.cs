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
        /// 给**已存在**的纵断面图换标注栏（Band）样式，不删不重建，不碰视图样式和标签。
        ///
        /// 为什么有这个零件：create_profile_view 只能在**建图时**套一整套 band set，对已经出好的
        /// 视图无能为力；几十张图统一换四条 band 的样式，GUI 里只能一张张开
        /// Profile View Properties → Bands 手改。
        ///
        /// **为什么按位置(index)而不是按「旧样式名→新样式名」映射**（2026-08-27 实证，别再试）：
        /// Civil 3D 2025 托管 API 里 ProfileViewBandItem.BandStyleId 只写不可读
        /// （反射 probe 出来是 BandStyleId(ObjectId,w)，没有 getter；同类的 Profile1Id/Profile2Id 是 r,w）。
        /// 读不出当前样式名，就没法做「认名换名」。错位风险靠条数校验兜：每个视图的 band 条数
        /// 必须等于给定数组长度，不等就整张跳过并报进 views_mismatched，绝不半写。
        /// 动手前先 dry_run 看 per_view 的 fingerprint（BandType/Gap/间隔/标注开关），
        /// 各视图逐位置同构 = 这批图出自同一套 band set，位置顺序可信。
        /// </summary>
        static JsonNode RunNodeRestyleProfileViewBands(JsonObject a, Document doc)
        {
            List<string> bottomWant = StyleNameList(a, "bottom");
            List<string> topWant = StyleNameList(a, "top");
            if (bottomWant.Count == 0 && topWant.Count == 0)
                throw new InvalidOperationException(
                    "bottom / top 至少给一个：按标注栏**从上到下的位置**列样式名，"
                    + "如 bottom=[\"C3DF-GroundElevation\",\"C3DF-DesignElevation\",\"C3DF-CutFillDepth\",\"C3DF-Station\"]；"
                    + "数组里给 null 或空串表示该位置不动。");

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
                // 目标样式在 CivilDocument.Styles.BandStyles 底下所有集合里按**精确名**找。
                // 不走 FindStyleId 的「兜底返回第一个」——band 样式设错了照样出图，只是内容全不对，
                // 比报错难查得多。
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
                    // 条数对不上就整张跳过：位置替换错位 = 四条 band 全串味，比不改糟得多
                    string bad = null;
                    if (topWant.Count > 0 && top.Count != topWant.Count)
                        bad = "顶部 band " + top.Count + " 条，给了 " + topWant.Count + " 个位置";
                    if (bottomWant.Count > 0 && bottom.Count != bottomWant.Count)
                        bad = (bad == null ? "" : bad + "；") + "底部 band " + bottom.Count
                            + " 条，给了 " + bottomWant.Count + " 个位置";
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
                        // 集合改完必须整体写回，band 项是值拷贝语义（照 create_profile_view 设数据源那套）
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
                    ? "按 views 过滤后一张纵断面图都没匹配上：" + string.Join("、", onlyViews)
                    : "图里没有纵断面图（ProfileView）。");

            var missedArr = new JsonArray();
            foreach (string s in viewsMissed) missedArr.Add(s);

            return new JsonObject
            {
                ["dry_run"] = dryRun,
                ["views_scanned"] = viewsScanned,
                ["views_changed"] = viewsChanged,
                ["bands_changed"] = bandsChanged,
                ["views_mismatched"] = mismatched,   // band 条数对不上、整张跳过的
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

        /// <summary>位置 → 样式 ObjectId。空位置（null/空串）不进表 = 不动那条 band。</summary>
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
                    where + " 里这些标注栏样式图中不存在：" + string.Join("、", missing)
                    + "。图里现有的是：" + string.Join("、", new List<string>(bandStyles.Keys)));
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
                    if (!dryRun) item.BandStyleId = styleId;   // 只写不可读，改完无法回读校验
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

        /// <summary>band 的可读指纹：拿它横向比各视图是否逐位置同构，
        /// 同构 = 同一套 band set 生成、位置顺序可信（BandStyleId 读不出来，只能这么侧证）。</summary>
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

        /// <summary>把 CivilDocument.Styles.BandStyles 底下所有样式集合摊平成「名字 → ObjectId」。
        /// 反射走属性树，不手写集合名（照 list_styles 那套）；重名以先遇到的为准。</summary>
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
