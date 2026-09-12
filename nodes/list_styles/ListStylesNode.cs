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
        /// Read-only reconnaissance: list the names in each Civil style collection of the drawing (profile view styles, band sets,
        /// label sets, section view styles, ...). Use it before swapping styles to check whether two drawings share style names.
        ///
        /// Only walks TWO levels of civ.Styles properties, never recursing deeper -- a deep crawl reaches
        /// Database/Document and crashes accoreconsole natively (lesson of three crashes in a row on 2026-08-26).
        /// </summary>
        static JsonNode RunNodeListStyles(JsonObject a, Document doc)
        {
            string filter = GetString(a, "contains", null);   // only report collections whose name contains this string
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

                    // first level is already a style collection
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

                    // first level is a Root (such as BandStyles / LabelSetStyles); go one level down
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

        /// <summary>Read a style collection as a name table; returns null if it is not a collection.</summary>
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
                if (!(cur is ObjectId)) return null;      // not an ObjectId collection, so not a style set
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
