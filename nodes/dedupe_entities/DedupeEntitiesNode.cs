using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        /// <summary>
        /// 模型空间同位置同内容实体去重 +（可选）按内容平移 MTEXT。
        ///
        /// 为什么有这个零件（2026-08-26 项目B初设横断面）：断面标签被引擎画了两遍、
        /// 655 个 MTEXT/引线完全重合（成品 PDF 上就是 LX 说的「字体重叠」）；样式侧无解
        /// （同一样式渲染两份，关样式=两份全没）。在导出后的纯 CAD 件上去重是唯一稳路。
        /// 「疏浚控制线」与「设计常水位」行距只有 0.42m 必然挤，顺带按内容匹配上移。
        /// </summary>
        static JsonNode RunNodeDedupeEntities(JsonObject a, Document doc)
        {
            // 限定图层（缺省全模型空间）；位置量化精度（米）
            string layerFilter = GetString(a, "layer", null);
            double tol = GetDouble(a, "tol", 0.001);
            // moves: [{contains, dy, dx?}] —— 去重后，把内容含 contains 的 MTEXT/TEXT 平移
            var moves = a["moves"] as JsonArray;
            // erases: [{contains?, regex?, layer?}] —— 删内容匹配的 MTEXT/TEXT。
            // 为什么有它（2026-08-26 项目B纵断面）：桩号高程/坡度标签挂在 Civil 标签机制深处，
            // 配置无标签集、清视图标签组、清全部 Profile 标签组三轮全部无效（图面纹丝不动），
            // 断路器拉闸后唯一稳路=在导出件上按内容删普通文字实体。
            var erases = a["erases"] as JsonArray;
            // erase_lines: [{layer, vertical?, min_len?}] —— 删几何匹配的 LINE（标签的长引出竖线）
            var eraseLines = a["erase_lines"] as JsonArray;

            Database db = doc.Database;
            int erased = 0, moved = 0, scanned = 0, erasedContentTotal = 0;
            var byKey = new Dictionary<string, ObjectId>();
            var perLayer = new Dictionary<string, int>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    Entity e;
                    try { e = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                    catch { continue; }
                    if (e == null) continue;
                    if (!string.IsNullOrEmpty(layerFilter) && e.Layer != layerFilter) continue;
                    scanned++;

                    string key = DedupeKey(e, tol);
                    if (key == null) continue;
                    ObjectId first;
                    if (byKey.TryGetValue(key, out first))
                    {
                        e.UpgradeOpen();
                        e.Erase();
                        erased++;
                        int n; perLayer.TryGetValue(e.Layer, out n); perLayer[e.Layer] = n + 1;
                    }
                    else byKey[key] = id;
                }

                if (erases != null)
                {
                    foreach (ObjectId id in ms)
                    {
                        Entity e;
                        try { e = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                        catch { continue; }
                        if (e == null || e.IsErased) continue;
                        string txt = e is MText ? ((MText)e).Contents : (e is DBText ? ((DBText)e).TextString : null);
                        if (txt == null) continue;
                        txt = DecodeMtextUnicode(txt);
                        string plain = System.Text.RegularExpressions.Regex.Replace(txt, @"\\f[^;]*;|[{}]|\\P|\\pxq[^;]*;|\\[A-Za-z]", "");
                        foreach (JsonNode en2 in erases)
                        {
                            var r = en2 as JsonObject;
                            if (r == null) continue;
                            string lay = GetString(r, "layer", null);
                            if (!string.IsNullOrEmpty(lay) && e.Layer != lay) continue;
                            string pat = GetString(r, "contains", null);
                            string rx = GetString(r, "regex", null);
                            bool hit2 = false;
                            // 原文和去格式码后的 plain 都试：竖排字每字夹一个 \P（纵\P坡\P转…），
                            // 只对原文匹配会漏掉整批竖排标签（2026-08-26 实测）
                            if (!string.IsNullOrEmpty(pat) &&
                                (txt.IndexOf(pat, StringComparison.Ordinal) >= 0
                                 || plain.IndexOf(pat, StringComparison.Ordinal) >= 0)) hit2 = true;
                            if (!hit2 && !string.IsNullOrEmpty(rx))
                            {
                                try { hit2 = System.Text.RegularExpressions.Regex.IsMatch(plain.Trim(), rx); }
                                catch { }
                            }
                            if (!hit2) continue;
                            e.UpgradeOpen();
                            e.Erase();
                            erasedContentTotal++;
                            break;
                        }
                    }
                }

                if (eraseLines != null)
                {
                    foreach (ObjectId id in ms)
                    {
                        Line ln2;
                        try { ln2 = tr.GetObject(id, OpenMode.ForRead) as Line; }
                        catch { continue; }
                        if (ln2 == null || ln2.IsErased) continue;
                        foreach (JsonNode en3 in eraseLines)
                        {
                            var r = en3 as JsonObject;
                            if (r == null) continue;
                            string lay = GetString(r, "layer", null);
                            if (!string.IsNullOrEmpty(lay) && ln2.Layer != lay) continue;
                            double minLen = GetDouble(r, "min_len", 0);
                            double dx = Math.Abs(ln2.EndPoint.X - ln2.StartPoint.X);
                            double dy = Math.Abs(ln2.EndPoint.Y - ln2.StartPoint.Y);
                            if (GetBool(r, "vertical", false) && dx > 0.01) continue;
                            if (minLen > 0 && Math.Sqrt(dx * dx + dy * dy) < minLen) continue;
                            ln2.UpgradeOpen();
                            ln2.Erase();
                            erasedContentTotal++;
                            break;
                        }
                    }
                }

                if (moves != null)
                {
                    foreach (ObjectId id in ms)
                    {
                        Entity e;
                        try { e = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                        catch { continue; }
                        if (e == null || e.IsErased) continue;
                        string txt = e is MText ? ((MText)e).Contents : (e is DBText ? ((DBText)e).TextString : null);
                        if (txt == null) continue;
                        // 导出件（proxy 炸开产物）的中文常以 \U+XXXX 转义存在内容里，先解码再匹配
                        txt = DecodeMtextUnicode(txt);
                        foreach (JsonNode mn in moves)
                        {
                            var m = mn as JsonObject;
                            if (m == null) continue;
                            string pat = GetString(m, "contains", null);
                            if (string.IsNullOrEmpty(pat) || txt.IndexOf(pat, StringComparison.Ordinal) < 0) continue;
                            double dx = GetDouble(m, "dx", 0), dy = GetDouble(m, "dy", 0);
                            if (dx == 0 && dy == 0) continue;
                            e.UpgradeOpen();
                            e.TransformBy(Matrix3d.Displacement(new Vector3d(dx, dy, 0)));
                            moved++;
                            break;
                        }
                    }
                }
                tr.Commit();
            }

            var layerStats = new JsonObject();
            foreach (var kv in perLayer) layerStats[kv.Key] = kv.Value;
            return new JsonObject
            {
                ["scanned"] = scanned,
                ["erased_duplicates"] = erased,
                ["erased_by_content"] = erasedContentTotal,
                ["moved_texts"] = moved,
                ["erased_by_layer"] = layerStats
            };
        }

        /// <summary>把 MTEXT 内容里的 \U+XXXX 转义解码成真字符。</summary>
        static string DecodeMtextUnicode(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf("\\U+", StringComparison.OrdinalIgnoreCase) < 0) return s;
            return System.Text.RegularExpressions.Regex.Replace(
                s, @"\\U\+([0-9A-Fa-f]{4})",
                m => ((char)System.Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        }

        /// <summary>实体的「同位置同内容」指纹；认不出的类型返回 null（不参与去重）。</summary>
        static string DedupeKey(Entity e, double tol)
        {
            Func<double, string> q = v => Math.Round(v / tol).ToString(CultureInfo.InvariantCulture);
            Func<Point3d, string> qp = p => q(p.X) + "," + q(p.Y);
            var mt = e as MText;
            if (mt != null) return "MT|" + e.Layer + "|" + qp(mt.Location) + "|" + mt.Contents;
            var tx = e as DBText;
            if (tx != null) return "TX|" + e.Layer + "|" + qp(tx.Position) + "|" + tx.TextString;
            var ln = e as Line;
            if (ln != null) return "LN|" + e.Layer + "|" + qp(ln.StartPoint) + "|" + qp(ln.EndPoint);
            var pl = e as Polyline;
            if (pl != null)
            {
                var sb = new System.Text.StringBuilder("PL|" + e.Layer + "|");
                for (int i = 0; i < pl.NumberOfVertices; i++) sb.Append(qp(pl.GetPoint3dAt(i))).Append(";");
                return sb.ToString();
            }
            var br = e as BlockReference;
            if (br != null) return "BR|" + e.Layer + "|" + br.Name + "|" + qp(br.Position) + "|" + q(br.Rotation);
            var so = e as Solid;
            if (so != null) return "SO|" + e.Layer + "|" + qp(so.GetPointAt(0)) + "|" + qp(so.GetPointAt(3));
            return null;
        }
    }
}
