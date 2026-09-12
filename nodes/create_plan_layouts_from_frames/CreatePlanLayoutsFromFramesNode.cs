using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        sealed class StoredPlanFrame
        {
            public int Index;
            public string Name;
            public double StartStation;
            public double EndStation;
            public double MinX;
            public double MinY;
            public double MaxX;
            public double MaxY;
            public string Handle;
        }

        static JsonNode RunNodeCreatePlanLayoutsFromFrames(JsonObject args, Document doc)
            => CreatePlanLayoutsFromFrames(args, doc);

        static JsonNode CreatePlanLayoutsFromFrames(JsonObject a, Document doc)
        {
            string block = Need(a, "block");
            string layer = GetString(a, "frame_layer", "C3DF-PLAN-FRAME-NOPLOT");
            string layoutPrefix = GetString(a, "layout_prefix", "PlanSheet-");
            int maxLayouts = Math.Max(0, (int)GetDouble(a, "max_layouts", 0));
            var frames = ReadStoredPlanFrames(doc.Database, layer);
            if (frames.Count == 0)
                throw new InvalidOperationException(
                    "No usable horizontal sheet frame in the drawing; run create_plan_frames_from_alignment first.");
            if (maxLayouts > 0) frames = frames.Take(maxLayouts).ToList();

            int total = frames.Count;
            var results = new JsonArray();
            foreach (StoredPlanFrame frame in frames)
            {
                string layoutName = layoutPrefix + frame.Index.ToString("00", CultureInfo.InvariantCulture);
                var child = new JsonObject
                {
                    ["layout"] = layoutName,
                    ["block"] = block,
                    ["paper"] = GetString(a, "paper", "A3"),
                    ["model_window"] = new JsonObject
                    {
                        ["minx"] = frame.MinX, ["miny"] = frame.MinY,
                        ["maxx"] = frame.MaxX, ["maxy"] = frame.MaxY
                    },
                    ["frame_x"] = GetDouble(a, "frame_x", -9.372),
                    ["frame_y"] = GetDouble(a, "frame_y", -10.01),
                    ["frame_scale"] = GetDouble(a, "frame_scale", 1),
                    ["frame_layer"] = GetString(a, "frame_block_layer", "0")
                };

                string fromDwg = GetString(a, "from_dwg", null);
                if (!string.IsNullOrWhiteSpace(fromDwg)) child["from_dwg"] = fromDwg;
                var viewport = a["viewport"] as JsonObject;
                child["viewport"] = viewport == null
                    ? new JsonObject
                    {
                        ["x"] = 176.4, ["y"] = 154.44,
                        ["width"] = 350, ["height"] = 267,
                        ["scale"] = GetDouble(a, "scale", 5000),
                        ["locked"] = true
                    }
                    : viewport.DeepClone();
                child["attributes"] = ExpandPlanAttributes(
                    a["attributes"] as JsonObject, frame, total);

                JsonNode created = CreateLayoutSheet(child, doc);
                results.Add(new JsonObject
                {
                    ["index"] = frame.Index,
                    ["frame"] = frame.Name,
                    ["layout"] = layoutName,
                    ["start_station"] = frame.StartStation,
                    ["end_station"] = frame.EndStation,
                    ["boundary_handle"] = frame.Handle,
                    ["result"] = created
                });
            }

            return new JsonObject
            {
                ["count"] = results.Count,
                ["frame_layer"] = layer,
                ["layout_prefix"] = layoutPrefix,
                ["sheet_set"] = false,
                ["view_rotation"] = 0,
                ["layouts"] = results
            };
        }

        static List<StoredPlanFrame> ReadStoredPlanFrames(Database db, string layer)
        {
            var frames = new List<StoredPlanFrame>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (pl == null || !pl.Closed ||
                        !string.Equals(pl.Layer, layer, StringComparison.OrdinalIgnoreCase))
                        continue;
                    ResultBuffer rb = pl.GetXDataForApplication(PlanFrameRegApp);
                    if (rb == null) continue;
                    string name = null;
                    int index = 0;
                    var doubles = new List<double>();
                    foreach (TypedValue tv in rb)
                    {
                        if (tv.TypeCode == 1000 && name == null) name = Convert.ToString(tv.Value);
                        else if (tv.TypeCode == 1070) index = Convert.ToInt32(tv.Value);
                        else if (tv.TypeCode == 1040) doubles.Add(Convert.ToDouble(tv.Value));
                    }
                    if (index <= 0 || doubles.Count < 2) continue;
                    Extents3d ext = pl.GeometricExtents;
                    frames.Add(new StoredPlanFrame
                    {
                        Index = index,
                        Name = name ?? ("PLAN-" + index.ToString("00", CultureInfo.InvariantCulture)),
                        StartStation = doubles[0],
                        EndStation = doubles[1],
                        MinX = ext.MinPoint.X, MinY = ext.MinPoint.Y,
                        MaxX = ext.MaxPoint.X, MaxY = ext.MaxPoint.Y,
                        Handle = pl.Handle.ToString()
                    });
                }
                tr.Commit();
            }
            return frames.OrderBy(f => f.Index).ToList();
        }

        static JsonObject ExpandPlanAttributes(
            JsonObject template, StoredPlanFrame frame, int total)
        {
            var result = new JsonObject();
            if (template == null) return result;
            foreach (var pair in template)
            {
                string value;
                try { value = pair.Value == null ? "" : pair.Value.GetValue<string>(); }
                catch { value = pair.Value == null ? "" : pair.Value.ToString(); }
                value = value
                    .Replace("{n}", frame.Index.ToString(CultureInfo.InvariantCulture))
                    .Replace("{n:00}", frame.Index.ToString("00", CultureInfo.InvariantCulture))
                    .Replace("{total}", total.ToString(CultureInfo.InvariantCulture))
                    .Replace("{frame}", frame.Name)
                    .Replace("{start_station}", FormatPlanStation(frame.StartStation))
                    .Replace("{end_station}", FormatPlanStation(frame.EndStation));
                result[pair.Key] = value;
            }
            return result;
        }
    }
}
