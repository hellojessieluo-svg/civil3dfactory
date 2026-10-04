using System;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivOffsetInfo = Autodesk.Civil.DatabaseServices.OffsetAlignmentInfo;
using CivRegion = Autodesk.Civil.DatabaseServices.AlignmentRegion;
using CivTransition = Autodesk.Civil.DatabaseServices.AlignmentTransition;
using CivLinearTD = Autodesk.Civil.DatabaseServices.LinearTransitionDescription;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeSetOffsetWidening(JsonObject args, Document doc)
            => SetOffsetWidening(args, doc);

        static JsonNode SetOffsetWidening(JsonObject a, Document doc)
        {
            string name = GetString(a, "alignment", null);
            string handle = GetString(a, "handle", null);
            string action = (GetString(a, "action", "list") ?? "list").ToLowerInvariant();
            bool dry = GetBool(a, "dry_run", false);
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(handle))
                throw new InvalidOperationException("Specify alignment (offset alignment name) or handle.");
            if (action != "list" && action != "add")
                throw new InvalidOperationException("action must be list or add.");

            Database db = doc.Database;
            var civ = Civ(db);
            var result = new JsonObject { ["action"] = action, ["dry_run"] = dry };
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = null;
                if (!string.IsNullOrEmpty(handle))
                {
                    if (!db.TryGetObjectId(new Handle(Convert.ToInt64(handle, 16)), out ObjectId hid))
                        throw new InvalidOperationException("Handle not found: " + handle);
                    al = tr.GetObject(hid, OpenMode.ForRead) as CivAlignment;
                }
                else al = FindAlignment(tr, civ, name);
                if (al == null) throw new InvalidOperationException("Alignment not found: " + (name ?? handle));
                CivOffsetInfo oi;
                try { oi = al.OffsetAlignmentInfo; } catch { oi = null; }
                if (oi == null) throw new InvalidOperationException(al.Name + " is not an offset alignment (no OffsetAlignmentInfo).");
                result["alignment"] = al.Name;
                result["handle"] = al.Handle.ToString();
                result["nominal_offset"] = Math.Round(oi.NominalOffset, 4);
                result["state"] = DumpOffsetState(al, oi);
                var parent = (CivAlignment)tr.GetObject(oi.ParentAlignmentId, OpenMode.ForRead);
                result["parent_start_station_raw"] = parent.StartingStation;
                result["parent_end_station_raw"] = parent.EndingStation;
                result["before"] = DumpOffsetRegions(oi);

                if (action == "add")
                {
                    var regions = a["regions"] as JsonArray;
                    if (regions == null || regions.Count == 0) throw new InvalidOperationException("add requires regions:[{start_station,end_station,offset,entry_length?,exit_length?}]");
                    var alW = (CivAlignment)tr.GetObject(al.ObjectId, OpenMode.ForWrite);
                    CivOffsetInfo oiW = alW.OffsetAlignmentInfo;
                    var added = new JsonArray();
                    foreach (JsonNode n in regions)
                    {
                        var r = n as JsonObject ?? throw new InvalidOperationException("Each regions entry must be an object.");
                        double s0 = r["start_station"].GetValue<double>();
                        double s1 = r["end_station"].GetValue<double>();
                        double off = r["offset"].GetValue<double>();
                        oiW.AddWidening(s0, s1, off);
                        var item = new JsonObject { ["start_station"] = s0, ["end_station"] = s1, ["offset"] = off };
                        double? en = r["entry_length"] != null ? r["entry_length"].GetValue<double>() : (double?)null;
                        double? ex = r["exit_length"] != null ? r["exit_length"].GetValue<double>() : (double?)null;
                        if (en.HasValue || ex.HasValue)
                        {
                            CivRegion hit = null;
                            for (int i = 0; i < oiW.Regions.Count; i++)
                            {
                                CivRegion rg = oiW.Regions[i];
                                double a0 = 0, a1 = 0; try { a0 = rg.StartStation; a1 = rg.EndStation; } catch { continue; }
                                if (Math.Abs(a0 - s0) < 0.01 && Math.Abs(a1 - s1) < 0.01) { hit = rg; break; }
                            }
                            if (hit == null) item["transition_note"] = "Could not locate the new region by station; transition lengths unchanged.";
                            else
                            {
                                if (en.HasValue) item["entry_set"] = SetTransitionLength(hit.EntryTransition, en.Value);
                                if (ex.HasValue) item["exit_set"] = SetTransitionLength(hit.ExitTransition, ex.Value);
                            }
                        }
                        added.Add(item);
                    }
                    result["added"] = added;
                    if (GetBool(a, "update", true))
                    {
                        var upd = new JsonObject();
                        try { alW.Update(); upd["offset_alignment"] = "updated"; }
                        catch (System.Exception ex) { upd["offset_alignment_error"] = ex.GetType().Name + ": " + ex.Message; }
                        if (GetBool(a, "update_parent", true))
                        {
                            try
                            {
                                var par = (CivAlignment)tr.GetObject(oiW.ParentAlignmentId, OpenMode.ForWrite);
                                par.Update(); upd["parent"] = par.Name + " updated";
                            }
                            catch (System.Exception ex) { upd["parent_error"] = ex.GetType().Name + ": " + ex.Message; }
                        }
                        result["update"] = upd;
                    }
                    result["after"] = DumpOffsetRegions(oiW);
                }
                if (dry) tr.Abort(); else tr.Commit();
            }
            result["note"] = dry ? "dry_run: database unchanged" : "Memory only; call save_dwg to persist and rebuild_corridor if used as a corridor target.";
            return result;
        }

        static JsonObject DumpOffsetState(CivAlignment al, CivOffsetInfo oi)
        {
            var o = new JsonObject();
            try { o["update_mode"] = oi.UpdateMode.ToString(); } catch (System.Exception ex) { o["update_mode_error"] = ex.GetType().Name; }
            try { o["lock_mode"] = oi.LockMode.ToString(); } catch (System.Exception ex) { o["lock_mode_error"] = ex.GetType().Name; }
            try { o["lock_to_start"] = oi.LockToStartStation; } catch { }
            try { o["lock_to_end"] = oi.LockToEndStation; } catch { }
            try { o["side"] = oi.Side.ToString(); } catch { }
            try { o["alignment_type"] = al.AlignmentType.ToString(); } catch { }
            try { o["is_offset"] = al.IsOffsetAlignment; } catch { }
            try { o["length"] = Math.Round(al.Length, 4); } catch { }
            try { o["start_station"] = Math.Round(al.StartingStation, 4); } catch { }
            try { o["end_station"] = Math.Round(al.EndingStation, 4); } catch { }
            try { o["style"] = al.StyleName; } catch { }
            try { o["layer"] = al.Layer; } catch { }
            try
            {
                var ents = al.Entities; var kinds = new JsonArray(); var detail = new JsonArray(); var byOrder = new JsonArray();
                for (int i = 0; i < ents.Count; i++)
                {
                    try { kinds.Add(ents[i].EntityType.ToString()); } catch { kinds.Add("?"); }
                    detail.Add(DumpAlignmentEntity(ents[i], i));
                }
                try { for (int i = 0; i < ents.Count; i++) byOrder.Add(ents.GetEntityByOrder(i).EntityId); } catch (System.Exception ex) { o["by_order_error"] = ex.GetType().Name; }
                o["entities"] = ents.Count; o["entity_types"] = kinds; o["entity_detail"] = detail; o["entity_ids_by_station_order"] = byOrder;
                try { o["first_entity"] = ents.FirstEntity; o["last_entity"] = ents.LastEntity; } catch { }
            }
            catch (System.Exception ex) { o["entities_error"] = ex.GetType().Name; }
            return o;
        }

        static JsonObject DumpAlignmentEntity(Autodesk.Civil.DatabaseServices.AlignmentEntity e, int index)
        {
            var o = new JsonObject { ["index"] = index };
            try { o["id"] = e.EntityId; } catch { }
            try { o["type"] = e.EntityType.ToString(); } catch { }
            try { o["before"] = e.EntityBefore; o["after"] = e.EntityAfter; } catch { }
            try { o["constraint"] = e.Constraint1.ToString(); } catch { }
            var subs = new JsonArray();
            try
            {
                for (int j = 0; j < e.SubEntityCount; j++)
                {
                    var s = e[j]; var so = new JsonObject();
                    try { so["kind"] = s.SubEntityType.ToString(); } catch { }
                    try { so["start"] = Math.Round(s.StartStation, 4); so["end"] = Math.Round(s.EndStation, 4); so["length"] = Math.Round(s.Length, 4); } catch { }
                    subs.Add(so);
                }
            }
            catch (System.Exception ex) { o["sub_error"] = ex.GetType().Name; }
            o["subs"] = subs;
            return o;
        }

        static JsonNode SetTransitionLength(CivTransition t, double len)
        {
            var o = new JsonObject { ["want"] = len };
            try
            {
                var d = t.TransitionDescription;
                o["before"] = Math.Round(d.Length, 4);
                d.Length = len;
                o["after"] = Math.Round(t.TransitionDescription.Length, 4);
            }
            catch (System.Exception ex) { o["error"] = ex.GetType().Name + ": " + ex.Message; }
            return o;
        }

        static JsonObject DumpTransition(CivTransition t)
        {
            var o = new JsonObject();
            if (t == null) { o["null"] = true; return o; }
            try { o["type"] = t.TransitionType.ToString(); } catch (System.Exception ex) { o["type_error"] = ex.GetType().Name; }
            try
            {
                var d = t.TransitionDescription;
                if (d != null)
                {
                    o["description"] = d.GetType().Name;
                    try { o["start_station"] = Math.Round(d.StartStation, 4); } catch { }
                    try { o["end_station"] = Math.Round(d.EndStation, 4); } catch { }
                    try { o["length"] = Math.Round(d.Length, 4); } catch { }
                    if (d is CivLinearTD ld)
                    {
                        try { o["taper_input"] = ld.TaperInput.ToString(); } catch { }
                        try { o["taper_ratio"] = Math.Round(ld.TaperRatio, 6); } catch { }
                    }
                }
            }
            catch (System.Exception ex) { o["description_error"] = ex.GetType().Name; }
            return o;
        }

        static JsonObject DumpOffsetRegions(CivOffsetInfo oi)
        {
            var rows = new JsonArray();
            int count = 0;
            try { count = oi.Regions.Count; } catch { }
            for (int i = 0; i < count; i++)
            {
                var o = new JsonObject { ["index"] = i };
                try
                {
                    CivRegion rg = oi.Regions[i];
                    try { o["region_type"] = rg.RegionType.ToString(); } catch { }
                    try { o["start_station"] = Math.Round(rg.StartStation, 4); } catch { }
                    try { o["end_station"] = Math.Round(rg.EndStation, 4); } catch { }
                    try { o["start_station_raw"] = rg.StartStation; } catch { }
                    try { o["end_station_raw"] = rg.EndStation; } catch { }
                    try { o["length"] = Math.Round(rg.Length, 4); } catch { }
                    try { o["offset"] = Math.Round(rg.Offset, 4); } catch { }
                    try { o["offset_dist"] = Math.Round(rg.OffsetDist, 4); } catch { }
                    try { o["increased_width"] = Math.Round(rg.IncreasedWidth, 4); } catch { }
                    try { o["entry"] = DumpTransition(rg.EntryTransition); } catch (System.Exception ex) { o["entry_error"] = ex.GetType().Name; }
                    try { o["exit"] = DumpTransition(rg.ExitTransition); } catch (System.Exception ex) { o["exit_error"] = ex.GetType().Name; }
                }
                catch (System.Exception ex) { o["error"] = ex.GetType().Name + ": " + ex.Message; }
                rows.Add(o);
            }
            int tcount = 0;
            try { tcount = oi.Transitions.Count; } catch { }
            return new JsonObject { ["regions"] = count, ["transitions"] = tcount, ["items"] = rows };
        }
    }
}
