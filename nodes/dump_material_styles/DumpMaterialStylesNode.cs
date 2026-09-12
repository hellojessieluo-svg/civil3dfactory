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
        /// 只读诊断：摊平「材质列表 → 材质 → 样式」和 MaterialSection 实体的样式指向。
        /// 材质填充在图上到底用哪个样式，API 文档说不清，反射摊出来看事实。
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
                try { return sb.Name; } catch (System.Exception ex) { return "<读名失败:" + ex.GetType().Name + ">"; }
            }
            return ReflectStr(so, "Name") ?? "<非StyleBase:" + so.GetType().FullName + ">";
        }

        static string ReflectStr(object o, string prop)
        {
            // GetProperty(name) 在派生类 new/隐藏同名属性时抛 AmbiguousMatchException，
            // 逐个扫、第一个能读出来的算数（Civil 的 Style 系就有这毛病）
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
