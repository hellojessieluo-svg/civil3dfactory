using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlign = Autodesk.Civil.DatabaseServices.Alignment;
using CivilDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// import_design_lines: bring a design-line DWG (output of export_design_lines, or hand-drawn) into the current drawing;
    /// centerlines become alignments, closed boundaries stay as the source for later width targets. This is S01 of the "polylines are the input" pipeline.
    ///
    /// Identity comes from the layer name, not the object name -- polylines have no names:
    ///   CL-{channel}        -> create an alignment named {channel}
    ///   BOUNDARY-{channel}  -> copied as-is onto boundary_layer, for offsets_from_boundary
    ///
    /// Assemblies / styles / QTO criteria / existing-ground surface cannot be created here; they must already be in the current drawing (template).
    /// This node only brings the design intent in and does not touch them.
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeImportDesignLines(JsonObject args, Document doc)
            => ImportDesignLines(args, doc);

        public static JsonNode ImportDesignLines(JsonObject a, Document doc)
        {
            string from = Need(a, "dwg");
            if (!File.Exists(from))
                throw new InvalidOperationException("Design-line drawing not found: " + from);

            string cPrefix = GetString(a, "center_prefix", "CL-");
            string bPrefix = GetString(a, "boundary_prefix", "BOUNDARY-");
            string style = GetString(a, "style", null);
            string labelSet = GetString(a, "label_set", null);
            string alLayer = GetString(a, "alignment_layer", null);
            bool addCurves = GetBool(a, "add_curves", false);
            bool replaceExisting = GetBool(a, "replace_existing", true);
            double minBoundaryArea = GetDouble(a, "min_boundary_area", 100.0);

            Database db = doc.Database;
            var created = new JsonArray();
            var boundaries = new JsonArray();
            var notes = new JsonArray();

            // ---- 1 Pick the polylines to bring over from the design-line drawing ----
            var srcIds = new ObjectIdCollection();
            var roleByHandle = new Dictionary<string, string>();   // source handle -> "C:channel" / "B:channel"
            using (var srcDb = new Database(false, true))
            {
                srcDb.ReadDwgFile(from, FileOpenMode.OpenForReadAndAllShare, true, null);
                srcDb.CloseInput(true);
                using (Transaction str = srcDb.TransactionManager.StartTransaction())
                {
                    BlockTable sbt = (BlockTable)str.GetObject(srcDb.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord sms = (BlockTableRecord)str.GetObject(
                        sbt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in sms)
                    {
                        var pl = str.GetObject(id, OpenMode.ForRead) as Polyline;
                        if (pl == null) continue;
                        string layer = pl.Layer ?? "";
                        string role = null, channel = null;
                        if (layer.StartsWith(cPrefix, StringComparison.Ordinal))
                        { role = "C"; channel = layer.Substring(cPrefix.Length); }
                        else if (layer.StartsWith(bPrefix, StringComparison.Ordinal))
                        { role = "B"; channel = layer.Substring(bPrefix.Length); }
                        else continue;
                        if (string.IsNullOrWhiteSpace(channel)) continue;

                        if (role == "B")
                        {
                            double area = 0.0;
                            try { area = Math.Abs(pl.Area); } catch (System.Exception) { }
                            if (area < minBoundaryArea)
                            { notes.Add("Dropped fragment boundary " + layer + " (area " + Math.Round(area, 2) + " < " + minBoundaryArea + ")"); continue; }
                        }
                        srcIds.Add(id);
                        roleByHandle[pl.Handle.ToString()] = role + ":" + channel;
                    }
                    str.Commit();
                }

                if (srcIds.Count == 0)
                    throw new InvalidOperationException(
                        "No matching layers in the design-line drawing (centerline prefix \"" + cPrefix + "\", boundary prefix \"" + bPrefix + "\").");

                // ---- 2 Copy into the current drawing's model space ----
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    ObjectId msId = bt[BlockTableRecord.ModelSpace];
                    var map = new IdMapping();
                    srcDb.WblockCloneObjects(srcIds, msId, map, DuplicateRecordCloning.Ignore, false);
                    tr.Commit();
                }
            }

            // ---- 3 Claim by layer in the current drawing; convert centerlines to alignments ----
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivilDoc civ = Civ(db);
                // Called unconditionally: FindStyleId falls back to the first entry of the collection when the name is empty.
                // Alignment.Create's labelSetId does not accept ObjectId.Null (throws
                // "Value cannot be null. (Parameter 'labelSetId')"), so a real style is required.
                ObjectId styleId = FindStyleId(tr, civ.Styles.AlignmentStyles, style);
                ObjectId labelId = FindStyleId(tr,
                    civ.Styles.LabelSetStyles.AlignmentLabelSetStyles, labelSet);
                if (labelId.IsNull)
                    throw new InvalidOperationException("The current drawing has no alignment label set style; cannot create alignments.");
                ObjectId layerId = db.Clayer;
                if (!string.IsNullOrWhiteSpace(alLayer))
                    layerId = A2PEnsureLayer(tr, db, alLayer, 7, "Continuous", "acadiso.lin");

                // Existing alignment with the same name: erase it first if requested, otherwise Create fails on the duplicate name
                var existing = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId aid in civ.GetAlignmentIds())
                {
                    var x = tr.GetObject(aid, OpenMode.ForRead) as CivAlign;
                    if (x != null) existing[x.Name] = aid;
                }

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                // Only handle what was just copied in: identified by layer prefix + not yet registered
                var pending = new List<KeyValuePair<ObjectId, string>>();   // entity -> "C:channel"/"B:channel"
                foreach (ObjectId id in ms)
                {
                    var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (pl == null) continue;
                    string layer = pl.Layer ?? "";
                    if (layer.StartsWith(cPrefix, StringComparison.Ordinal))
                        pending.Add(new KeyValuePair<ObjectId, string>(id, "C:" + layer.Substring(cPrefix.Length)));
                    else if (layer.StartsWith(bPrefix, StringComparison.Ordinal))
                        pending.Add(new KeyValuePair<ObjectId, string>(id, "B:" + layer.Substring(bPrefix.Length)));
                }

                foreach (var kv in pending)
                {
                    string role = kv.Value.Substring(0, 1);
                    string channel = kv.Value.Substring(2);
                    if (role == "B")
                    {
                        var pl = tr.GetObject(kv.Key, OpenMode.ForRead) as Polyline;
                        double area = 0.0;
                        try { area = Math.Abs(pl.Area); } catch (System.Exception) { }
                        boundaries.Add(new JsonObject
                        {
                            ["channel"] = channel,
                            ["handle"] = pl.Handle.ToString(),
                            ["layer"] = pl.Layer,
                            ["closed"] = pl.Closed,
                            ["vertices"] = pl.NumberOfVertices,
                            ["area"] = Round(area, 3)
                        });
                        continue;
                    }

                    // Rename first to free the name; erase the old one only after the new one is created.
                    // It was once written as "erase old -> create new"; when creation failed the old alignment was already gone -- one failure cost the original data.
                    CivAlign old = null;
                    string parked = null;
                    if (existing.ContainsKey(channel))
                    {
                        if (!replaceExisting)
                        { notes.Add("Alignment " + channel + " already exists, not replaced (replace_existing=false)"); continue; }
                        old = tr.GetObject(existing[channel], OpenMode.ForWrite) as CivAlign;
                        if (old != null)
                        {
                            parked = channel + "_old_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                            try { old.Name = parked; }
                            catch (System.Exception ex)
                            { notes.Add("Alignment " + channel + " could not be renamed to free the name, skipped: " + ex.Message); continue; }
                        }
                    }

                    ObjectId newId;
                    try
                    {
                        newId = CreateAlignmentFromEntity(tr, civ, channel, ObjectId.Null, kv.Key,
                            layerId, styleId, labelId, true, addCurves);
                    }
                    catch (System.Exception ex)
                    {
                        if (old != null)
                        {
                            try { old.Name = channel; notes.Add("Old alignment " + channel + " renamed back to its original name"); }
                            catch (System.Exception) { notes.Add("WARNING: old alignment could not be renamed back, now named " + parked); }
                        }
                        notes.Add("Centerline " + channel + " failed to convert to an alignment: " + ex.Message);
                        continue;
                    }

                    if (old != null)
                    {
                        try { old.Erase(); notes.Add("Alignment " + channel + " rebuilt from the design line, old one erased"); }
                        catch (System.Exception ex)
                        { notes.Add("WARNING: new alignment " + channel + " created, but the old one could not be erased (now named " + parked + "): " + ex.Message); }
                    }

                    var al = tr.GetObject(newId, OpenMode.ForRead) as CivAlign;
                    created.Add(new JsonObject
                    {
                        ["channel"] = channel,
                        ["alignment"] = al == null ? channel : al.Name,
                        ["handle"] = al == null ? "" : al.Handle.ToString(),
                        ["length"] = al == null ? 0.0 : Round(al.Length, 4),
                        ["start_station"] = al == null ? 0.0 : Round(al.StartingStation, 4),
                        ["end_station"] = al == null ? 0.0 : Round(al.EndingStation, 4)
                    });
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["source"] = from,
                ["alignments_created"] = created.Count,
                ["boundaries_imported"] = boundaries.Count,
                ["alignments"] = created,
                ["boundaries"] = boundaries,
                ["notes"] = notes
            };
        }
    }
}
