using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Aec.PropertyData;
using Autodesk.Aec.PropertyData.DatabaseServices;

namespace Civil3DFactory
{
    /// <summary>
    /// Headless read/write of AEC Property Sets. API idioms copied from the field-proven
    /// WaterBox/Commands/ChannelProps.cs (C3DF-ChannelProps, the <HalfWidth> property set attached to main lines).
    /// Design rule: property sets hold only "identity + design intent", never computed results that go stale;
    /// area/perimeter use "@area"/"@length" so the op computes them live from geometry and a re-run refreshes them.
    /// </summary>
    public static partial class Ops
    {
        static JsonNode PropertySets(JsonObject a, Document doc)
        {
            // define / assign / dump each open their own transaction: fields added to a definition only appear
            // on already-attached objects after the transaction commits; a SetAt in the same transaction gives eKeyNotFound.
            Database db = doc.Database;
            var res = new JsonObject();
            string defName = null;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var dict = new DictionaryPropertySetDefinitions(db);

                // ---------- define: create/extend the property set definition (idempotent) ----------
                var def = a["define"] as JsonObject;
                if (def != null)
                {
                    defName = GetString(def, "name", null);
                    if (string.IsNullOrEmpty(defName))
                        throw new InvalidOperationException("define.name is required.");
                    ObjectId psdId;
                    bool created = false;
                    if (dict.Has(defName, tr)) psdId = dict.GetAt(defName);
                    else
                    {
                        var psd0 = new PropertySetDefinition();
                        psd0.SetToStandard(db);
                        psd0.SubSetDatabaseDefaults(db);
                        var appliesArr = def["applies_to"] as JsonArray;
                        if (appliesArr != null && appliesArr.Count > 0)
                        {
                            var sc = new System.Collections.Specialized.StringCollection();
                            foreach (JsonNode n in appliesArr) sc.Add(n.GetValue<string>());
                            // if the filter cannot be applied, fall back to no filter (same fallback as ChannelProps)
                            try { psd0.SetAppliesToFilter(sc, false); } catch { }
                        }
                        dict.AddNewRecord(defName, psd0);
                        tr.AddNewlyCreatedDBObject(psd0, true);
                        psdId = psd0.ObjectId;
                        created = true;
                    }
                    var psdW = (PropertySetDefinition)tr.GetObject(psdId, OpenMode.ForWrite);
                    var existing = new HashSet<string>();
                    foreach (PropertyDefinition d0 in psdW.Definitions) existing.Add(d0.Name);
                    int added = 0;
                    var fields = def["fields"] as JsonArray;
                    if (fields != null)
                        foreach (JsonNode fn0 in fields)
                        {
                            var f = (JsonObject)fn0;
                            string fn = f["name"].GetValue<string>();
                            if (existing.Contains(fn)) continue;
                            var pd = new PropertyDefinition();
                            pd.SetToStandard(db);
                            pd.SubSetDatabaseDefaults(db);
                            pd.Name = fn;
                            pd.Description = GetString(f, "description", "");
                            string ty = GetString(f, "type", "text").ToLowerInvariant();
                            if (ty == "real")
                            { pd.DataType = Autodesk.Aec.PropertyData.DataType.Real; pd.DefaultData = GetDouble(f, "default", 0.0); }
                            else if (ty == "integer")
                            { pd.DataType = Autodesk.Aec.PropertyData.DataType.Integer; pd.DefaultData = (int)GetDouble(f, "default", 0); }
                            else
                            { pd.DataType = Autodesk.Aec.PropertyData.DataType.Text; pd.DefaultData = GetString(f, "default", ""); }
                            psdW.Definitions.Add(pd);
                            added++;
                        }
                    res["define"] = new JsonObject
                    { ["name"] = defName, ["created"] = created, ["fields_added"] = added };
                }
                tr.Commit();
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var dict = new DictionaryPropertySetDefinitions(db);

                // ---------- assign: attach to objects + set values (idempotent; re-run = refresh values) ----------
                var assign = a["assign"] as JsonArray;
                if (assign != null)
                {
                    string setName = GetString(a, "set", defName);
                    if (string.IsNullOrEmpty(setName) || !dict.Has(setName, tr))
                        throw new InvalidOperationException("assign needs an existing property set name (set parameter or define.name).");
                    ObjectId psdId2 = dict.GetAt(setName);
                    var arr = new JsonArray();
                    foreach (JsonNode itn in assign)
                    {
                        var it = (JsonObject)itn;
                        string h = it["handle"].GetValue<string>();
                        var obj = tr.GetObject(ResolveHandle(db, h), OpenMode.ForWrite);
                        ObjectId psId = ObjectId.Null;
                        try { psId = PropertyDataServices.GetPropertySet(obj, psdId2); } catch { }
                        bool attached = false;
                        if (psId.IsNull)
                        {
                            PropertyDataServices.AddPropertySet(obj, psdId2);
                            psId = PropertyDataServices.GetPropertySet(obj, psdId2);
                            attached = true;
                        }
                        var ps = (PropertySet)tr.GetObject(psId, OpenMode.ForWrite);
                        int nset = 0;
                        var vals = it["values"] as JsonObject;
                        if (vals != null)
                            foreach (var kv in vals)
                            {
                                int pid = ps.PropertyNameToId(kv.Key);
                                object v;
                                var jv = kv.Value as JsonValue;
                                string sval;
                                double dval;
                                if (jv != null && jv.TryGetValue(out sval) && sval != null && sval.StartsWith("@"))
                                {
                                    var cur = obj as Curve;
                                    if (cur == null)
                                        throw new InvalidOperationException(h + " is not a curve; cannot compute " + sval);
                                    if (sval == "@area") v = Math.Round(cur.Area, 2);
                                    else if (sval == "@length")
                                        v = Math.Round(cur.GetDistanceAtParameter(cur.EndParam), 2);
                                    else throw new InvalidOperationException("Unknown geometry value " + sval + " (supported: @area/@length)");
                                }
                                else if (jv != null && jv.TryGetValue(out dval) && !(jv.TryGetValue(out sval) && sval != null))
                                    v = dval;
                                else
                                    v = kv.Value == null ? "" : kv.Value.ToString();
                                ps.SetAt(pid, v);
                                nset++;
                            }
                        arr.Add(new JsonObject
                        { ["handle"] = h, ["attached"] = attached, ["values_set"] = nset });
                    }
                    res["assign"] = arr;
                }
                tr.Commit();
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // ---------- dump: read back for verification ----------
                var dmp = a["dump"] as JsonObject;
                if (dmp != null)
                {
                    var targets = new List<DBObject>();
                    var hs = dmp["handles"] as JsonArray;
                    string lay = GetString(dmp, "layer", null);
                    if (hs != null)
                        foreach (JsonNode hn in hs)
                            targets.Add(tr.GetObject(ResolveHandle(db, hn.GetValue<string>()), OpenMode.ForRead));
                    else if (!string.IsNullOrEmpty(lay))
                        foreach (ObjectId id in ModelSpace(db, tr))
                        {
                            var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent != null && ent.Layer == lay) targets.Add(ent);
                        }
                    else throw new InvalidOperationException("dump needs handles or layer.");

                    var arr = new JsonArray();
                    foreach (var obj in targets)
                    {
                        var one = new JsonObject { ["handle"] = obj.Handle.ToString() };
                        var sets = new JsonArray();
                        ObjectIdCollection psIds = null;
                        try { psIds = PropertyDataServices.GetPropertySets(obj); } catch { }
                        if (psIds != null)
                            foreach (ObjectId psId in psIds)
                            {
                                var ps = (PropertySet)tr.GetObject(psId, OpenMode.ForRead);
                                var psd = (PropertySetDefinition)tr.GetObject(
                                    ps.PropertySetDefinition, OpenMode.ForRead);
                                var so = new JsonObject { ["set"] = psd.Name };
                                var vo = new JsonObject();
                                foreach (PropertyDefinition d0 in psd.Definitions)
                                {
                                    object v;
                                    try { v = ps.GetAt(ps.PropertyNameToId(d0.Name)); }
                                    catch (System.Exception ex) { vo[d0.Name] = "(read failed: " + ex.GetType().Name + ")"; continue; }
                                    if (v is double) vo[d0.Name] = (double)v;
                                    else if (v is int) vo[d0.Name] = (int)v;
                                    else vo[d0.Name] = v == null ? null : (JsonNode)v.ToString();
                                }
                                so["values"] = vo;
                                sets.Add(so);
                            }
                        one["property_sets"] = sets;
                        arr.Add(one);
                    }
                    res["dump"] = arr;
                }
                tr.Commit();
            }
            if (res.Count == 0)
                throw new InvalidOperationException("Give at least one of define / assign / dump.");
            return res;
        }
    }
}
