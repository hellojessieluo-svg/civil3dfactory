using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivProfileType = Autodesk.Civil.DatabaseServices.ProfileType;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeSwapSurfaceIdentity(JsonObject a, Document doc)
        {
            string oldName = Need(a, "old");
            string newName = Need(a, "new");
            bool eraseOld = GetBool(a, "erase_old", false);
            bool dryRun = GetBool(a, "dry_run", false);
            Database db = doc.Database;
            CivDoc civ = Civ(db);

            var res = new JsonObject { ["old"] = oldName, ["new"] = newName, ["dry_run"] = dryRun };
            ObjectId oldId, newId;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                oldId = FindSurfaceId(tr, civ, oldName);
                newId = FindSurfaceId(tr, civ, newName);
                if (oldId.IsNull) throw new InvalidOperationException("Surface '" + oldName + "'");
                if (newId.IsNull) throw new InvalidOperationException("Surface '" + newName + "'");
                if (oldId == newId) throw new InvalidOperationException("old and new refer to the same surface");
                var so = (CivSurface)tr.GetObject(oldId, OpenMode.ForRead);
                var sn = (CivSurface)tr.GetObject(newId, OpenMode.ForRead);
                res["before"] = new JsonObject
                {
                    ["old_handle"] = so.Handle.ToString(),
                    ["new_handle"] = sn.Handle.ToString(),
                    ["new_is_reference"] = SwapSafeBool(() => sn.IsReferenceObject),
                    ["ground_profiles_on_old"] = CountEgOn(tr, civ, oldId),
                    ["ground_profiles_on_new"] = CountEgOn(tr, civ, newId)
                };
                if (dryRun) { tr.Abort(); return res; }

                so.UpgradeOpen();
                sn.UpgradeOpen();
                so.SwapIdWith(newId, false, false);
                tr.Commit();
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var atOld = (CivSurface)tr.GetObject(oldId, OpenMode.ForRead);
                var atNew = (CivSurface)tr.GetObject(newId, OpenMode.ForRead);
                var after = new JsonObject
                {
                    ["id_of_old_now_holds"] = atOld.Name,
                    ["id_of_old_is_reference"] = SwapSafeBool(() => atOld.IsReferenceObject),
                    ["id_of_new_now_holds"] = atNew.Name,
                    ["ground_profiles_now_on_new_object"] = CountEgOn(tr, civ, oldId)
                };
                var samples = new JsonArray();
                foreach (ObjectId aid in civ.GetAlignmentIds())
                {
                    var al = (CivAlignment)tr.GetObject(aid, OpenMode.ForRead);
                    foreach (ObjectId pid in al.GetProfileIds())
                    {
                        var p = tr.GetObject(pid, OpenMode.ForRead) as CivProfile;
                        if (p == null || p.ProfileType != CivProfileType.EG) continue;
                        string src = ""; try { src = p.DataSourceName; } catch { }
                        samples.Add(new JsonObject
                        {
                            ["alignment"] = al.Name,
                            ["profile"] = p.Name,
                            ["source"] = src,
                            ["source_is_swapped_object"] = SwapSafeBool(() => p.DataSourceId == oldId),
                            ["start_elev"] = Math.Round(SafeElev(p, al.StartingStation), 3),
                            ["end_elev"] = Math.Round(SafeElev(p, al.EndingStation), 3)
                        });
                    }
                }
                after["ground_profiles"] = samples;
                if (eraseOld)
                {
                    atNew.UpgradeOpen();
                    atNew.Erase();
                    after["erased_old_object"] = true;
                }
                res["after"] = after;
                tr.Commit();
            }
            return res;
        }

        static int CountEgOn(Transaction tr, CivDoc civ, ObjectId sfId)
        {
            int n = 0;
            foreach (ObjectId aid in civ.GetAlignmentIds())
            {
                var al = (CivAlignment)tr.GetObject(aid, OpenMode.ForRead);
                foreach (ObjectId pid in al.GetProfileIds())
                {
                    var p = tr.GetObject(pid, OpenMode.ForRead) as CivProfile;
                    if (p == null || p.ProfileType != CivProfileType.EG) continue;
                    try { if (p.DataSourceId == sfId) n++; } catch { }
                }
            }
            return n;
        }

        static JsonNode SwapSafeBool(Func<bool> f)
        {
            try { return f(); } catch (Exception ex) { return "(read failed: " + ex.GetType().Name + ")"; }
        }
    }
}
