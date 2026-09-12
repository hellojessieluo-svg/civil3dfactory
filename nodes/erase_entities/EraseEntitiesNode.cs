using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DFactory
{
    /// <summary>
    /// erase_entities: erase entities by handle.
    ///
    /// Before erasing, report what each object looks like (type / layer / elevation / vertex count / length / area);
    /// that list stays in the result after the erase -- an irreversible action must at least leave evidence.
    /// dry_run:true only reports, used to verify the handles were not mistyped.
    ///
    /// Handle-only: no by-layer / by-type batch erase is offered, since that kind of entry point wipes a whole layer too easily.
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeEraseEntities(JsonObject a, Document doc)
        {
            var handles = a["handles"] as JsonArray;
            if (handles == null || handles.Count == 0)
                throw new InvalidOperationException("handles is required; erasing is by handle only (no batch erase by layer).");
            bool dryRun = GetBool(a, "dry_run", false);

            var wanted = new List<string>();
            foreach (JsonNode h in handles)
                if (h != null && !string.IsNullOrWhiteSpace(h.ToString())) wanted.Add(h.ToString().Trim());

            Database db = doc.Database;
            var done = new JsonArray();
            var missing = new JsonArray();
            int erased = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (string hs in wanted)
                {
                    ObjectId id = ResolveHandle(db, hs);
                    if (id.IsNull || id.IsErased) { missing.Add((JsonNode)hs); continue; }

                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) { missing.Add((JsonNode)hs); continue; }

                    var info = new JsonObject
                    {
                        ["handle"] = hs,
                        ["type"] = ent.GetType().Name,
                        ["layer"] = ent.Layer
                    };
                    var pl = ent as Polyline;
                    if (pl != null)
                    {
                        info["vertices"] = pl.NumberOfVertices;
                        info["closed"] = pl.Closed;
                        info["elevation"] = Round(pl.Elevation, 3);
                        try { info["length"] = Round(pl.Length, 3); } catch (System.Exception) { }
                        try { info["area"] = Round(pl.Area, 3); } catch (System.Exception) { }
                    }
                    var ln = ent as Line;
                    if (ln != null)
                    {
                        info["length"] = Round(ln.Length, 3);
                        info["start"] = new JsonArray { Round(ln.StartPoint.X, 3), Round(ln.StartPoint.Y, 3), Round(ln.StartPoint.Z, 3) };
                        info["end"] = new JsonArray { Round(ln.EndPoint.X, 3), Round(ln.EndPoint.Y, 3), Round(ln.EndPoint.Z, 3) };
                    }
                    var txt = ent as DBText;
                    if (txt != null) info["text"] = txt.TextString;

                    if (!dryRun)
                    {
                        ent.UpgradeOpen();
                        try
                        {
                            ent.Erase();
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception ex)
                        {
                            // Feature line protected by site topology (eNotApplicable): remove it from the site, then erase
                            if (ent is Autodesk.Civil.DatabaseServices.FeatureLine)
                            {
                                Autodesk.Civil.DatabaseServices.FeatureLine.MoveToNoneSite(ent.ObjectId);
                                ent.Erase();
                                info["note"] = "site feature line, removed from site before erasing";
                            }
                            else throw new InvalidOperationException(
                                ent.GetType().Name + " " + ent.Handle + " refused erase: " + ex.ErrorStatus);
                        }
                        erased++;
                    }
                    done.Add(info);
                }
                tr.Commit();
            }

            // Assert: handles were named but none found = wrong handles; must not report success silently
            if (done.Count == 0)
                throw new InvalidOperationException(
                    "None of the " + wanted.Count + " named handles were found in the drawing; nothing erased.");

            return new JsonObject
            {
                ["dry_run"] = dryRun,
                ["erased"] = erased,
                ["not_found"] = missing,
                ["details"] = done
            };
        }
    }
}
