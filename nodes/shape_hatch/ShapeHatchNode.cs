using System;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivShapeStyle = Autodesk.Civil.DatabaseServices.Styles.ShapeStyle;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeShapeHatch(JsonObject a, Document doc)
        {
            string name = Need(a, "name");
            string view = GetString(a, "view", "Section");
            var set = a["set"] as JsonObject;
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var rep = new JsonObject { ["name"] = name, ["view"] = view };
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId id = FindStyleId(tr, civ.Styles.ShapeStyles, name);
                if (id.IsNull) throw new InvalidOperationException("Shape style not found: '" + name + "'");
                var st = (CivShapeStyle)tr.GetObject(id, set == null ? OpenMode.ForRead : OpenMode.ForWrite);
                var m = st.GetType().GetMethod("GetHatchDisplayStyle" + view, Type.EmptyTypes);
                if (m == null) throw new InvalidOperationException("GetHatchDisplayStyle is unavailable" + view);
                object h = m.Invoke(st, null);
                Func<JsonObject> snap = () =>
                {
                    var o = new JsonObject();
                    foreach (var p in h.GetType().GetProperties())
                    {
                        if (p.GetIndexParameters().Length > 0) continue;
                        try { var v = p.GetValue(h, null); o[p.Name] = v == null ? null : JsonValue.Create(v.ToString()); } catch { }
                    }
                    return o;
                };
                rep["before"] = snap();
                if (set != null)
                {
                    var log = new JsonArray();
                    foreach (var kv in set)
                    {
                        var p = h.GetType().GetProperty(kv.Key);
                        if (p == null || !p.CanWrite) { log.Add(kv.Key + " is missing or read-only"); continue; }
                        try
                        {
                            object val = p.PropertyType.IsEnum ? Enum.Parse(p.PropertyType, kv.Value.ToString(), true)
                                       : Convert.ChangeType(kv.Value.ToString(), p.PropertyType, System.Globalization.CultureInfo.InvariantCulture);
                            p.SetValue(h, val, null); log.Add(kv.Key + " → " + val);
                        }
                        catch (System.Exception ex) { log.Add(kv.Key + " failed: " + ex.Message); }
                    }
                    rep["changes"] = log;
                    rep["after"] = snap();
                }
                tr.Commit();
            }
            return rep;
        }
    }
}
