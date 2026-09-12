using System;
using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivSampleLineGroup = Autodesk.Civil.DatabaseServices.SampleLineGroup;
using CivQtoMaterialList = Autodesk.Civil.DatabaseServices.QTOMaterialList;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        /// <summary>
        /// Read-only diagnostic: flatten "material list -> material -> style" and the style references of MaterialSection entities.
        /// The API docs do not say which style a material hatch actually uses; reflection lays out the facts.
        /// </summary>
        static JsonNode RunNodeDumpMaterialStyles(JsonObject a, Document doc)
        {
            int sampleCap = (int)GetDouble(a, "sample", 5);
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var lists = new JsonArray();
            var sections = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId alId in ModelSpace(db, tr))
                {
                    CivAlignment al;
                    try { al = tr.GetObject(alId, OpenMode.ForRead) as CivAlignment; }
                    catch { continue; }
                    if (al == null) continue;
                    foreach (ObjectId gid in al.GetSampleLineGroupIds())
                    {
                        var g = (CivSampleLineGroup)tr.GetObject(gid, OpenMode.ForRead);
                        foreach (CivQtoMaterialList ml in g.MaterialLists)
                        {
                            var entry = new JsonObject
                            {
                                ["alignment"] = al.Name,
                                ["group"] = g.Name,
                                ["list"] = ReflectStr(ml, "Name"),
                                ["guid"] = ml.Guid.ToString()
                            };
                            var items = new JsonArray();
                            if (ml is IEnumerable en)
                            {
                                foreach (object item in en)
                                {
                                    if (item == null) continue;
                                    var it = new JsonObject
                                    {
                                        ["type"] = item.GetType().Name,
                                        ["name"] = ReflectStr(item, "Name")
                                    };
                                    foreach (PropertyInfo p in item.GetType().GetProperties())
                                    {
                                        if (p.PropertyType != typeof(ObjectId)) continue;
                                        object v = null;
                                        try { v = p.GetValue(item); } catch { continue; }
                                        if (!(v is ObjectId sid) || sid.IsNull) continue;
                                        try
                                        {
                                            DBObject so = tr.GetObject(sid, OpenMode.ForRead);
                                            it[p.Name] = so.GetType().Name + ":" + StyleName(so);
                                        }
                                        catch { }
                                    }
                                    items.Add(it);
                                }
                            }
                            entry["items"] = items;
                            lists.Add(entry);
                        }
                    }
                }

                int seen = 0;
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    if (seen >= sampleCap) break;
                    DBObject o;
                    try { o = tr.GetObject(id, OpenMode.ForRead); }
                    catch { continue; }
                    if (o.GetType().Name != "MaterialSection") continue;
                    seen++;
                    var s = new JsonObject { ["handle"] = o.Handle.ToString() };
                    foreach (PropertyInfo p in o.GetType().GetProperties())
                    {
                        if (p.PropertyType != typeof(ObjectId)) continue;
                        if (p.Name.IndexOf("Style", StringComparison.OrdinalIgnoreCase) < 0
                            && p.Name != "MaterialId") continue;
                        object v = null;
                        try { v = p.GetValue(o); } catch { continue; }
                        if (!(v is ObjectId sid) || sid.IsNull) continue;
                        try
                        {
                            DBObject so = tr.GetObject(sid, OpenMode.ForRead);
                            s[p.Name] = so.GetType().Name + ":" + StyleName(so);
                        }
                        catch { }
                    }
                    sections.Add(s);
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["material_lists"] = lists,
                ["material_sections_sample"] = sections
            };
        }

        static string StyleName(DBObject so)
        {
            if (so is Autodesk.Civil.DatabaseServices.Styles.StyleBase sb)
            {
                try { return sb.Name; } catch (System.Exception ex) { return "<name unreadable:" + ex.GetType().Name + ">"; }
            }
            return ReflectStr(so, "Name") ?? "<not StyleBase:" + so.GetType().FullName + ">";
        }

        static string ReflectStr(object o, string prop)
        {
            // GetProperty(name) throws AmbiguousMatchException when a derived class hides a property with new;
            // scan one by one and take the first readable (Civil's Style family has this problem)
            foreach (PropertyInfo p in o.GetType().GetProperties())
            {
                if (p.Name != prop) continue;
                try
                {
                    if (p.GetValue(o) is string s) return s;
                }
                catch { }
            }
            return null;
        }
    }
}
