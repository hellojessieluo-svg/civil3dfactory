using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    ///
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeCopyStyles(JsonObject a, Document doc)
        {
            var items = a["items"] as JsonArray;
            if (items == null || items.Count == 0)
                throw new InvalidOperationException("copy_styles requires items:[{cat,from,to}]");
            bool dry = GetBool(a, "dry_run", true);
            string forceMethod = GetString(a, "method", null);

            Database db = doc.Database;
            CivDoc civ = null;
            try { civ = CivDoc.GetCivilDocument(db); } catch { }
            if (civ == null) throw new InvalidOperationException("CivilDocument unavailable.");

            var done = new JsonArray();
            var failed = new JsonArray();

            foreach (JsonNode n in items)
            {
                var o = n as JsonObject; if (o == null) continue;
                string cat = o["cat"] == null ? null : o["cat"].ToString();
                string from = o["from"] == null ? null : o["from"].ToString();
                string to = o["to"] == null ? null : o["to"].ToString();
                if (cat == null || from == null || to == null)
                { failed.Add(new JsonObject { ["item"] = o.ToJsonString(), ["why"] = "cat, from and to are all required" }); continue; }
                if (from == to)
                { failed.Add(new JsonObject { ["from"] = from, ["why"] = "from and to are identical" }); continue; }

                object coll = ResolveStyleCollection(civ, cat);
                var en = coll as System.Collections.IEnumerable;
                if (en == null)
                { failed.Add(new JsonObject { ["from"] = from, ["why"] = "category path does not resolve: " + cat }); continue; }

                ObjectId srcId = ObjectId.Null; bool toExists = false;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (object item in en)
                    {
                        if (!(item is ObjectId)) continue;
                        ObjectId oid = (ObjectId)item;
                        if (oid.IsErased) continue;
                        string nm;
                        try { nm = TryGetName(tr.GetObject(oid, OpenMode.ForRead)); } catch { continue; }
                        if (nm == from) srcId = oid;
                        if (nm == to) toExists = true;
                    }
                    tr.Commit();
                }
                if (srcId.IsNull)
                { failed.Add(new JsonObject { ["from"] = from, ["why"] = "Category " + cat + " has no style " + from }); continue; }
                if (toExists)
                { failed.Add(new JsonObject { ["from"] = from, ["why"] = "Target name already exists: " + to + " (not overwritten)" }); continue; }
                if (dry)
                { done.Add(new JsonObject { ["cat"] = cat, ["from"] = from, ["to"] = to, ["method"] = "dry run" }); continue; }

                string err1 = null;
                if (forceMethod != "add_copy_display")
                {
                    try
                    {
                        string method = CopyStyleByClone(db, srcId, to);
                        done.Add(new JsonObject { ["cat"] = cat, ["from"] = from, ["to"] = to, ["method"] = method });
                        continue;
                    }
                    catch (System.Exception ex)
                    {
                        err1 = ex.GetType().Name + ": " + Truncate(ex.InnerException != null ? ex.InnerException.Message : ex.Message, 120);
                        if (forceMethod == "clone")
                        { failed.Add(new JsonObject { ["from"] = from, ["to"] = to, ["why"] = "clone failed: " + err1 }); continue; }
                    }
                }
                try
                {
                    var rep = CopyStyleByAddAndCopyDisplay(db, coll, srcId, to);
                    rep["cat"] = cat; rep["from"] = from; rep["to"] = to;
                    rep["method"] = "add_copy_display";
                    if (err1 != null) rep["clone_error"] = err1;
                    done.Add(rep);
                }
                catch (System.Exception ex)
                {
                    failed.Add(new JsonObject
                    {
                        ["from"] = from, ["to"] = to,
                        ["why"] = "Both methods failed. clone: " + (err1 ?? "not attempted") + " | add_copy_display: "
                                  + ex.GetType().Name + ": " + Truncate(ex.InnerException != null ? ex.InnerException.Message : ex.Message, 120)
                    });
                }
            }
            return new JsonObject { ["dry_run"] = dry, ["done"] = done, ["failed"] = failed };
        }

        static string CopyStyleByClone(Database db, ObjectId srcId, string to)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var src = tr.GetObject(srcId, OpenMode.ForRead);
                var owner = tr.GetObject(src.OwnerId, OpenMode.ForWrite) as DBDictionary;
                if (owner == null) throw new InvalidOperationException("Source style owner is not a dictionary: " + src.OwnerId.ObjectClass.Name);
                if (owner.Contains(to)) throw new InvalidOperationException("Dictionary already contains key " + to);
                var clone = src.Clone() as DBObject;
                if (clone == null) throw new InvalidOperationException("Clone() returned null");
                ObjectId cloneId = owner.SetAt(to, clone);
                tr.AddNewlyCreatedDBObject(clone, true);
                string before = TryGetName(clone);
                if (before != to)
                {
                    try { SetStyleName(clone, to); }
                    catch (System.Exception ex)
                    { throw new InvalidOperationException("Clone inserted in dictionary (key " + to + ") but Name remains " + before + ": " + ex.Message); }
                }
                string after = TryGetName(clone);
                if (after != to)
                    throw new InvalidOperationException("Rename after cloning did not take effect; current name: " + after);
                tr.Commit();
            }
            return "clone";
        }

        static JsonObject CopyStyleByAddAndCopyDisplay(Database db, object coll, ObjectId srcId, string to)
        {
            var mAdd = coll.GetType().GetMethod("Add", new Type[] { typeof(string) });
            if (mAdd == null) throw new InvalidOperationException("Style collection has no Add(string)");
            object ret = mAdd.Invoke(coll, new object[] { to });
            if (!(ret is ObjectId)) throw new InvalidOperationException("Add did not return ObjectId");
            ObjectId newId = (ObjectId)ret;

            int copied = 0, skipped = 0;
            var views = new JsonArray();
            JsonObject labelRep = null;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var src = tr.GetObject(srcId, OpenMode.ForRead);
                var dst = tr.GetObject(newId, OpenMode.ForWrite);
                if (src is Autodesk.Civil.DatabaseServices.Styles.LabelStyle
                    && dst is Autodesk.Civil.DatabaseServices.Styles.LabelStyle)
                {
                    try
                    {
                        labelRep = CopyLabelStyleBody(tr,
                            (Autodesk.Civil.DatabaseServices.Styles.LabelStyle)src,
                            (Autodesk.Civil.DatabaseServices.Styles.LabelStyle)dst);
                    }
                    catch
                    {
                        try { dst.Erase(); tr.Commit(); } catch { }
                        throw;
                    }
                }
                foreach (var m in src.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (!m.Name.StartsWith("GetDisplayStyle")) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 1 || !ps[0].ParameterType.IsEnum) continue;
                    Type enumT = ps[0].ParameterType;
                    var mDst = dst.GetType().GetMethod(m.Name, new Type[] { enumT });
                    if (mDst == null) continue;
                    views.Add(m.Name);
                    foreach (string en in Enum.GetNames(enumT))
                    {
                        object ev = Enum.Parse(enumT, en);
                        object ds, dd;
                        try { ds = m.Invoke(src, new object[] { ev }); dd = mDst.Invoke(dst, new object[] { ev }); }
                        catch { skipped++; continue; }
                        if (ds == null || dd == null) { skipped++; continue; }
                        foreach (string pn in new[] { "Visible", "Layer", "Color", "Linetype", "Lineweight", "LinetypeScale", "PlotStyle" })
                        {
                            var ps1 = ds.GetType().GetProperty(pn); var pd1 = dd.GetType().GetProperty(pn);
                            if (ps1 == null || pd1 == null || !pd1.CanWrite) continue;
                            try { pd1.SetValue(dd, ps1.GetValue(ds, null), null); } catch { }
                        }
                        copied++;
                    }
                }
                tr.Commit();
            }
            var rep = new JsonObject { ["display_components_copied"] = copied, ["skipped"] = skipped, ["views"] = views };
            if (labelRep != null) rep["label"] = labelRep;
            return rep;
        }

        static JsonObject CopyLabelStyleBody(Transaction tr,
            Autodesk.Civil.DatabaseServices.Styles.LabelStyle src,
            Autodesk.Civil.DatabaseServices.Styles.LabelStyle dst)
        {
            int compAdded = 0, propsCopied = 0, propsSkipped = 0;
            var compNames = new JsonArray();
            var errs = new JsonArray();
            foreach (Autodesk.Civil.DatabaseServices.Styles.LabelStyleComponentType ct in
                Enum.GetValues(typeof(Autodesk.Civil.DatabaseServices.Styles.LabelStyleComponentType)))
            {
                ObjectIdCollection cids;
                try { cids = src.GetComponents(ct); } catch { continue; }
                foreach (ObjectId cid in cids)
                {
                    DBObject sc;
                    try { sc = tr.GetObject(cid, OpenMode.ForRead); } catch { continue; }
                    string cname = TryGetName(sc) ?? ("comp" + compAdded);
                    ObjectId nid;
                    try { nid = dst.AddComponent(cname, ct); }
                    catch (System.Exception ex)
                    { errs.Add(cname + " AddComponent(" + ct + ") failed: " + Truncate(ex.Message, 80)); continue; }
                    DBObject dc;
                    try { dc = tr.GetObject(nid, OpenMode.ForWrite); } catch { errs.Add(cname + " could not open the new component"); continue; }
                    compAdded++; compNames.Add(cname + ":" + ct);
                    CopyWrapperGroups(sc, dc, ref propsCopied, ref propsSkipped, 0);
                }
            }
            try
            {
                var ps = src.GetType().GetProperty("Properties"); var pd = dst.GetType().GetProperty("Properties");
                if (ps != null && pd != null)
                    CopyWrapperGroups(ps.GetValue(src, null), pd.GetValue(dst, null), ref propsCopied, ref propsSkipped, 0);
            }
            catch (System.Exception ex) { errs.Add("Properties: " + Truncate(ex.Message, 80)); }
            return new JsonObject
            {
                ["components_added"] = compAdded, ["components"] = compNames,
                ["props_copied"] = propsCopied, ["props_skipped"] = propsSkipped, ["errors"] = errs
            };
        }

        static void CopyWrapperGroups(object s, object d, ref int copied, ref int skipped, int depth)
        {
            if (s == null || d == null || depth > 3) return;
            foreach (var p in s.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
                if (p.Name == "Name" || p.Name == "Id" || p.Name == "ObjectId" || p.Name == "Handle" || p.Name == "Database"
                    || p.Name == "OwnerId" || p.PropertyType == typeof(ObjectId)) continue;
                var pd = d.GetType().GetProperty(p.Name);
                if (pd == null || !pd.CanRead) continue;
                object sv, dv;
                try { sv = p.GetValue(s, null); dv = pd.GetValue(d, null); } catch { skipped++; continue; }
                if (sv == null || dv == null) continue;
                Type t = sv.GetType();
                var valP = FindValueProp(t);
                if (valP != null && valP.CanRead)
                {
                    var dvalP = FindValueProp(dv.GetType());
                    if (dvalP == null || !dvalP.CanWrite) { skipped++; continue; }
                    try { dvalP.SetValue(dv, valP.GetValue(sv, null), null); copied++; }
                    catch { skipped++; }
                    continue;
                }
                if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(double)) continue;
                if (t.Namespace != null && t.Namespace.StartsWith("Autodesk.Civil"))
                    CopyWrapperGroups(sv, dv, ref copied, ref skipped, depth + 1);
            }
        }

        static System.Reflection.PropertyInfo FindValueProp(Type t)
        {
            System.Reflection.PropertyInfo best = null;
            foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (p.Name != "Value" || p.GetIndexParameters().Length > 0) continue;
                if (best == null || p.DeclaringType == t || (best.DeclaringType != t && best.DeclaringType.IsAssignableFrom(p.DeclaringType)))
                    best = p;
            }
            return best;
        }

        static void SetStyleName(DBObject obj, string name)
        {
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                      | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
            System.Exception last = null;
            for (Type t = obj.GetType(); t != null; t = t.BaseType)
            {
                var p = t.GetProperty("Name", flags);
                if (p == null) continue;
                var setter = p.GetSetMethod(true);
                if (setter == null) continue;
                try { setter.Invoke(obj, new object[] { name }); return; }
                catch (System.Exception ex) { last = ex; }
            }
            throw new InvalidOperationException("no writable Name property found" + (last != null ? ": " + last.Message : ""));
        }
    }
}
