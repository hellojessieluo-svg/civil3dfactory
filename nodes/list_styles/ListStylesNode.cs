using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        /// <summary>
        /// 只读侦察：列出图里各类 Civil 样式集合的名字（纵断面视图样式、带状图集、
        /// 标签集、断面图样式…）。换样式前先用它对照两张图有没有同名样式。
        ///
        /// ⚠ 只走 civ.Styles 的**两级**属性，不递归深爬——深爬会摸到
        /// Database/Document 把 accoreconsole 原生崩掉（2026-08-26 三连崩的教训）。
        /// </summary>
        static JsonNode RunNodeListStyles(JsonObject a, Document doc)
        {
            string filter = GetString(a, "contains", null);   // 只报名字含该串的集合
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var result = new JsonObject();
            int collections = 0, styles = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                object root = civ.Styles;
                foreach (PropertyInfo p in root.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    if (IsSkipName(p.PropertyType.Name)) continue;
                    object v;
                    try { v = p.GetValue(root); } catch { continue; }
                    if (v == null) continue;

                    // 一级就是样式集合
                    var names = ReadNames(tr, v);
                    if (names != null && names.Count > 0)
                    {
                        if (filter == null || p.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            result[p.Name] = ToArray(names);
                            collections++; styles += names.Count;
                        }
                        continue;
                    }

                    // 一级是 Root（如 BandStyles / LabelSetStyles），再下一级
                    foreach (PropertyInfo q in v.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (q.GetIndexParameters().Length > 0) continue;
                        if (IsSkipName(q.PropertyType.Name)) continue;
                        object w;
                        try { w = q.GetValue(v); } catch { continue; }
                        if (w == null) continue;
                        var sub = ReadNames(tr, w);
                        if (sub == null || sub.Count == 0) continue;
                        string key = p.Name + "." + q.Name;
                        if (filter != null && key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        result[key] = ToArray(sub);
                        collections++; styles += sub.Count;
                    }
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["collections"] = collections,
                ["styles_total"] = styles,
                ["styles"] = result
            };
        }

        static bool IsSkipName(string tn)
        {
            return tn == "Database" || tn == "Document" || tn == "Transaction"
                || tn == "TransactionManager" || tn == "String" || tn == "Boolean";
        }

        /// <summary>把样式集合读成名字表；不是集合就返回 null。</summary>
        static List<string> ReadNames(Transaction tr, object coll)
        {
            var en = coll as IEnumerable;
            if (en == null) return null;
            var names = new List<string>();
            IEnumerator it;
            try { it = en.GetEnumerator(); } catch { return null; }
            int guard = 0;
            while (guard++ < 2000)
            {
                object cur;
                try { if (!it.MoveNext()) break; cur = it.Current; }
                catch { break; }
                if (!(cur is ObjectId)) return null;      // 不是 ObjectId 集合，不是样式集
                ObjectId id = (ObjectId)cur;
                if (id.IsNull) continue;
                try
                {
                    DBObject o = tr.GetObject(id, OpenMode.ForRead);
                    names.Add(StyleName(o));
                }
                catch { }
            }
            return names;
        }

        static JsonArray ToArray(List<string> names)
        {
            var arr = new JsonArray();
            foreach (string n in names) arr.Add(n);
            return arr;
        }
    }
}
