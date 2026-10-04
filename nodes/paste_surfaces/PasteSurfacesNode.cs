using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DFactory
{
    /// <summary>
    ///
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodePasteSurfaces(JsonObject a, Document doc)
        {
            string name = Need(a, "name");
            var srcArr = a["sources"] as JsonArray;
            if (srcArr == null || srcArr.Count == 0) throw new InvalidOperationException("sources is required: [surface_name,...], pasted in order.");
            bool replace = GetBool(a, "replace", true);
            string styleName = GetString(a, "style", null);
            string layer = GetString(a, "layer", null);

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var rep = new JsonObject { ["name"] = name };
            var pasted = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var srcIds = new List<KeyValuePair<string, ObjectId>>();
                var byName = new Dictionary<string, ObjectId>();
                foreach (ObjectId sid in civ.GetSurfaceIds())
                {
                    var s = tr.GetObject(sid, OpenMode.ForRead) as CivSurface;
                    if (s != null && !byName.ContainsKey(s.Name)) byName[s.Name] = sid;
                }
                foreach (JsonNode n in srcArr)
                {
                    string sn = n.ToString();
                    ObjectId sid;
                    if (!byName.TryGetValue(sn, out sid))
                        throw new InvalidOperationException("Surface '" + sn + "'; available: " + string.Join(", ", byName.Keys));
                    if (sn == name) throw new InvalidOperationException("Source list contains target surface '" + name + "'; a surface cannot paste itself.");
                    srcIds.Add(new KeyValuePair<string, ObjectId>(sn, sid));
                }

                ObjectId oldId;
                if (byName.TryGetValue(name, out oldId))
                {
                    if (!replace) throw new InvalidOperationException("Surface already exists: " + name + " (replace:false)");
                    var old = tr.GetObject(oldId, OpenMode.ForWrite);
                    old.Erase();
                    rep["replaced_old_handle"] = oldId.Handle.ToString();
                }

                ObjectId styleId = ObjectId.Null;
                if (styleName != null)
                {
                    foreach (ObjectId stId in civ.Styles.SurfaceStyles)
                    {
                        var st = tr.GetObject(stId, OpenMode.ForRead);
                        if (TryGetName(st) == styleName) { styleId = stId; break; }
                    }
                    if (styleId.IsNull) throw new InvalidOperationException("Surface style not found: '" + styleName + "'; ambiguous or missing styles are not guessed.");
                }
                else
                {
                    var first = tr.GetObject(srcIds[0].Value, OpenMode.ForRead) as CivSurface;
                    styleId = first.StyleId;
                }

                ObjectId newId = CivTinSurface.Create(name, styleId);
                var tin = (CivTinSurface)tr.GetObject(newId, OpenMode.ForWrite);
                if (layer != null) { EnsureLayer(db, tr, layer, 7); tin.Layer = layer; }
                foreach (var kv in srcIds)
                {
                    try
                    {
                        tin.PasteSurface(kv.Value);
                        pasted.Add(new JsonObject { ["surface"] = kv.Key, ["ok"] = true });
                    }
                    catch (System.Exception ex)
                    {
                        pasted.Add(new JsonObject { ["surface"] = kv.Key, ["ok"] = false, ["why"] = ex.GetType().Name + ": " + ex.Message });
                    }
                }
                tr.Commit();
                rep["handle"] = newId.Handle.ToString();
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId sid in civ.GetSurfaceIds())
                {
                    var s = tr.GetObject(sid, OpenMode.ForRead) as CivTinSurface;
                    if (s == null || s.Name != name) continue;
                    var g = s.GetGeneralProperties();
                    var t = s.GetTinProperties();
                    rep["points"] = g.NumberOfPoints;
                    rep["triangles"] = t.NumberOfTriangles;
                    rep["min_z"] = Math.Round(g.MinimumElevation, 3);
                    rep["max_z"] = Math.Round(g.MaximumElevation, 3);
                    rep["mean_z"] = Math.Round(g.MeanElevation, 3);
                    if (t.NumberOfTriangles == 0) rep["warning"] = "Pasted surface has zero triangles; operation failed.";
                }
                tr.Commit();
            }
            rep["pasted"] = pasted;
            return rep;
        }

        static void EnsureLayer(Database db, Transaction tr, string name, short color)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return;
            lt.UpgradeOpen();
            var ltr = new LayerTableRecord { Name = name, Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, color) };
            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
        }
    }
}
