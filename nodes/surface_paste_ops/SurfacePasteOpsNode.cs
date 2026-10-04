using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;
using CivPasteOp = Autodesk.Civil.DatabaseServices.SurfaceOperationPasteSurface;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeSurfacePasteOps(JsonObject a, Document doc)
        {
            var items = a["items"] as JsonArray;
            bool listOnly = GetBool(a, "list_only", false);
            bool rebuild = GetBool(a, "rebuild", true);
            if (items == null || items.Count == 0) throw new InvalidOperationException("items is required, e.g. [{\"surface\":\"Intersection-1\",\"paste\":\"Design\",\"index\":0}]");
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var outArr = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (JsonNode n in items)
                {
                    var o = n as JsonObject;
                    string sfName = Need(o, "surface");
                    ObjectId sfId = FindSurfaceId(tr, civ, sfName);
                    if (sfId.IsNull) throw new InvalidOperationException("Surface '" + sfName + "'");
                    var sf = tr.GetObject(sfId, OpenMode.ForRead) as CivTinSurface;
                    if (sf == null) throw new InvalidOperationException("'" + sfName + "' is not a TIN surface");
                    var rec = new JsonObject { ["surface"] = sfName, ["handle"] = sf.Handle.ToString() };
                    rec["ops_before"] = OpTypes(sf);
                    string removeName = GetString(o, "remove_paste", null);
                    if (!listOnly && !string.IsNullOrEmpty(removeName))
                    {
                        ObjectId rid = FindSurfaceId(tr, civ, removeName);
                        int removed = 0;
                        if (!rid.IsNull)
                        {
                            sf.UpgradeOpen();
                            for (int i = sf.Operations.Count - 1; i >= 0; i--)
                            {
                                var po = sf.Operations[i] as CivPasteOp;
                                if (po != null && po.SurfaceId == rid) { sf.Operations.RemoveAt(i); removed++; }
                            }
                        }
                        rec["remove_paste"] = removeName; rec["removed"] = removed;
                    }
                    string pasteName = GetString(o, "paste", null);
                    if (!listOnly && !string.IsNullOrEmpty(pasteName))
                    {
                        ObjectId srcId = FindSurfaceId(tr, civ, pasteName);
                        if (srcId.IsNull) throw new InvalidOperationException("Paste source surface not found: '" + pasteName + "'");
                        bool exists = false;
                        for (int i = 0; i < sf.Operations.Count; i++)
                        {
                            var po = sf.Operations[i] as CivPasteOp;
                            if (po != null && po.SurfaceId == srcId) { exists = true; break; }
                        }
                        rec["paste"] = pasteName;
                        if (exists) rec["paste_action"] = "skip (already exists)";
                        else
                        {
                            sf.UpgradeOpen();
                            sf.PasteSurface(srcId);
                            int last = sf.Operations.Count - 1;
                            int target = o["index"] != null ? (int)o["index"].GetValue<double>() : 0;
                            if (target < 0) target = 0;
                            if (target > last) target = last;
                            for (int i = last; i > target; i--) sf.Operations.SwapAt(i, i - 1);
                            rec["paste_action"] = "added@" + target;
                        }
                    }
                    if (!listOnly && rebuild)
                    {
                        if (!sf.IsWriteEnabled) sf.UpgradeOpen();
                        try { sf.Rebuild(); rec["rebuilt"] = true; }
                        catch (Exception ex) { rec["rebuilt"] = false; rec["rebuild_error"] = ex.Message; }
                    }
                    rec["ops_after"] = OpTypes(sf);
                    try { var gp = sf.GetGeneralProperties(); rec["points"] = gp.NumberOfPoints; rec["elev_min"] = Math.Round(gp.MinimumElevation, 3); rec["elev_max"] = Math.Round(gp.MaximumElevation, 3); } catch (Exception ex) { rec["props_error"] = ex.Message; }
                    try { var tp = sf.GetTinProperties(); rec["triangles"] = tp.NumberOfTriangles; } catch (Exception ex) { rec["tin_error"] = ex.Message; }
                    try { rec["breaklines"] = sf.BreaklinesDefinition.Count; } catch { }
                    outArr.Add(rec);
                }
                if (listOnly) tr.Abort(); else tr.Commit();
            }
            return new JsonObject { ["list_only"] = listOnly, ["items"] = outArr };
        }

        static JsonArray OpTypes(CivTinSurface sf)
        {
            var arr = new JsonArray();
            try
            {
                for (int i = 0; i < sf.Operations.Count; i++)
                {
                    var op = sf.Operations[i];
                    string t = op.GetType().Name;
                    var po = op as CivPasteOp;
                    if (po != null)
                    {
                        string nm = "?";
                        try { nm = ((CivSurface)po.SurfaceId.GetObject(OpenMode.ForRead)).Name; } catch { }
                        t += "(" + nm + ")";
                    }
                    arr.Add(t);
                }
            }
            catch (Exception ex) { arr.Add("(read failed: " + ex.Message + ")"); }
            return arr;
        }
    }
}
