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
        /// Dedupe model-space entities with identical position and content + (optional) move MTEXT by content.
        ///
        /// Why this node exists (2026-08-26, project B preliminary-design cross sections): the engine drew the section labels twice,
        /// 655 MTEXT/leaders exactly overlapping (the "overlapping text" LX saw on the final PDF); no fix on the style side
        /// (one style rendered twice; turning it off removes both). Deduping the exported pure-CAD file is the only reliable route.
        /// "Dredge control line" and "Design normal water level" are only 0.42 m apart and always crowd; shift them up by content match while at it.
        /// </summary>
        static JsonNode RunNodeDedupeEntities(JsonObject a, Document doc)
        {
            // restrict to a layer (default whole model space); position quantisation (m)
            string layerFilter = GetString(a, "layer", null);
            double tol = GetDouble(a, "tol", 0.001);
            // moves: [{contains, dy, dx?}]: after dedupe, translate MTEXT/TEXT whose content contains `contains`
            var moves = a["moves"] as JsonArray;
            // erases: [{contains?, regex?, layer?}]: erase MTEXT/TEXT whose content matches.
            // Why (2026-08-26, project B profiles): station-elevation/grade labels sit deep in Civil's label machinery;
            // three rounds (no-label set, clearing view label groups, clearing all Profile label groups) had no effect (drawing unchanged),
            // so after the circuit breaker tripped the only reliable route = delete plain text entities by content on the exported file.
            var erases = a["erases"] as JsonArray;
            // erase_lines: [{layer, vertical?, min_len?}]: erase LINEs matching geometry (the long vertical leader lines of labels)
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
                            // Try both raw and format-stripped plain text: vertical text has a \P between every character (e.g. A\PB\PC...),
                            // matching raw text only would miss the whole batch of vertical labels (verified 2026-08-26)
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
                        // In exported files (exploded proxies) non-ASCII text is often stored as \U+XXXX escapes; decode before matching
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

        /// <summary>Decode \U+XXXX escapes in MTEXT content into real characters.</summary>
        static string DecodeMtextUnicode(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf("\\U+", StringComparison.OrdinalIgnoreCase) < 0) return s;
            return System.Text.RegularExpressions.Regex.Replace(
                s, @"\\U\+([0-9A-Fa-f]{4})",
                m => ((char)System.Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        }

        /// <summary>"Same position, same content" fingerprint of an entity; null for unrecognised types (excluded from dedupe).</summary>
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
