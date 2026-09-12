using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        const string PlanFrameRegApp = "C3DF_PLAN_FRAME";

        sealed class HorizontalPlanFrameSpec
        {
            public int Index;
            public string Name;
            public double StartStation;
            public double EndStation;
            public double MinX;
            public double MinY;
            public double MaxX;
            public double MaxY;
        }

        static JsonNode RunNodeCreatePlanFramesFromAlignment(JsonObject args, Document doc)
            => CreatePlanFramesFromAlignment(args, doc);

        static JsonNode CreatePlanFramesFromAlignment(JsonObject a, Document doc)
        {
            string alignmentName = Need(a, "alignment");
            string paper = GetString(a, "paper", "A3");
            double scale = GetDouble(a, "scale", 5000);
            double viewportWidthMm = GetDouble(a, "viewport_width_mm", 350);
            double viewportHeightMm = GetDouble(a, "viewport_height_mm", 267);
            double edgeMarginMm = GetDouble(a, "edge_margin_mm", 10);
            double sampleStep = GetDouble(a, "sample_step", 10);
            double overlapRatio = GetDouble(a, "overlap_ratio", 0.10);
            int maxFrames = Math.Max(0, (int)GetDouble(a, "max_frames", 0));
            string prefix = GetString(a, "frame_prefix", "PLAN");
            string layerName = GetString(a, "layer", "C3DF-PLAN-FRAME-NOPLOT");
            bool replaceExisting = GetBool(a, "replace_existing", true);

            if (scale <= 0) throw new InvalidOperationException("scale 必须大于 0。");
            if (viewportWidthMm <= 0 || viewportHeightMm <= 0)
                throw new InvalidOperationException("视口有效宽度和高度必须大于 0。");
            if (edgeMarginMm < 0 || edgeMarginMm * 2 >= Math.Min(viewportWidthMm, viewportHeightMm))
                throw new InvalidOperationException("edge_margin_mm 过大，已挤占全部有效视口。");
            if (sampleStep <= 0) throw new InvalidOperationException("sample_step 必须大于 0。");
            if (overlapRatio < 0 || overlapRatio >= 0.50)
                throw new InvalidOperationException("overlap_ratio 必须在 0（含）到 0.50（不含）之间。");

            double modelWidth = viewportWidthMm * scale / 1000.0;
            double modelHeight = viewportHeightMm * scale / 1000.0;
            double availableWidth = (viewportWidthMm - 2 * edgeMarginMm) * scale / 1000.0;
            double availableHeight = (viewportHeightMm - 2 * edgeMarginMm) * scale / 1000.0;

            Database db = doc.Database;
            CivilDocument civil = Civ(db);
            var specs = new List<HorizontalPlanFrameSpec>();
            double alignmentStart;
            double alignmentEnd;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Alignment alignment = FindAlignment(tr, civil, alignmentName);
                if (alignment == null)
                    throw new InvalidOperationException("找不到路线 '" + alignmentName + "'。");

                alignmentStart = alignment.StartingStation;
                alignmentEnd = alignment.EndingStation;
                double requestedStart = GetDouble(a, "start_station", alignmentStart);
                double requestedEnd = GetDouble(a, "end_station", alignmentEnd);
                double current = Math.Max(alignmentStart, requestedStart);
                double endLimit = Math.Min(alignmentEnd, requestedEnd);
                if (endLimit <= current)
                    throw new InvalidOperationException("路线分幅桩号范围无效。");

                int index = 1;
                while (current < endLimit - 1e-7 && (maxFrames == 0 || specs.Count < maxFrames))
                {
                    bool hasPoint = false;
                    double minX = 0, minY = 0, maxX = 0, maxY = 0;
                    double lastFit = current;
                    double station = current;

                    while (true)
                    {
                        double e = 0, n = 0;
                        alignment.PointLocation(station, 0, ref e, ref n);
                        double nextMinX = hasPoint ? Math.Min(minX, e) : e;
                        double nextMinY = hasPoint ? Math.Min(minY, n) : n;
                        double nextMaxX = hasPoint ? Math.Max(maxX, e) : e;
                        double nextMaxY = hasPoint ? Math.Max(maxY, n) : n;
                        if (hasPoint &&
                            (nextMaxX - nextMinX > availableWidth + 1e-7 ||
                             nextMaxY - nextMinY > availableHeight + 1e-7))
                            break;

                        minX = nextMinX; minY = nextMinY;
                        maxX = nextMaxX; maxY = nextMaxY;
                        hasPoint = true;
                        lastFit = station;
                        if (station >= endLimit - 1e-7) break;
                        station = Math.Min(endLimit, station + sampleStep);
                    }

                    if (!hasPoint || lastFit <= current + 1e-7)
                        throw new InvalidOperationException(
                            "当前比例和水平视口无法容纳一个采样步长；请减小 sample_step、降低比例尺分母或增大视口。");

                    double centerX = (minX + maxX) / 2.0;
                    double centerY = (minY + maxY) / 2.0;
                    specs.Add(new HorizontalPlanFrameSpec
                    {
                        Index = index,
                        Name = prefix + "-" + index.ToString("00", CultureInfo.InvariantCulture),
                        StartStation = current,
                        EndStation = lastFit,
                        MinX = centerX - modelWidth / 2.0,
                        MinY = centerY - modelHeight / 2.0,
                        MaxX = centerX + modelWidth / 2.0,
                        MaxY = centerY + modelHeight / 2.0
                    });

                    if (lastFit >= endLimit - 1e-7) break;
                    double covered = lastFit - current;
                    double nextStart = lastFit - covered * overlapRatio;
                    current = Math.Max(current + Math.Min(sampleStep, covered), nextStart);
                    index++;
                }
                tr.Commit();
            }

            int erased = 0;
            var windows = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EnsurePlanFrameLayerAndRegApp(db, tr, layerName);
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                if (replaceExisting)
                {
                    var ids = new List<ObjectId>();
                    foreach (ObjectId id in ms) ids.Add(id);
                    foreach (ObjectId id in ids)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead, false)
                                  as Autodesk.AutoCAD.DatabaseServices.Entity;
                        if (ent == null || ent.IsErased ||
                            !string.Equals(ent.Layer, layerName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        ent.UpgradeOpen();
                        ent.Erase();
                        erased++;
                    }
                }

                foreach (HorizontalPlanFrameSpec f in specs)
                {
                    var pl = new Polyline(4) { Closed = true, Layer = layerName };
                    pl.AddVertexAt(0, new Point2d(f.MinX, f.MinY), 0, 0, 0);
                    pl.AddVertexAt(1, new Point2d(f.MaxX, f.MinY), 0, 0, 0);
                    pl.AddVertexAt(2, new Point2d(f.MaxX, f.MaxY), 0, 0, 0);
                    pl.AddVertexAt(3, new Point2d(f.MinX, f.MaxY), 0, 0, 0);
                    ms.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                    pl.XData = new ResultBuffer(
                        new TypedValue(1001, PlanFrameRegApp),
                        new TypedValue(1000, f.Name),
                        new TypedValue(1070, f.Index),
                        new TypedValue(1040, f.StartStation),
                        new TypedValue(1040, f.EndStation));

                    var label = new DBText
                    {
                        Position = new Point3d(f.MinX, f.MaxY, 0),
                        Height = Math.Max(modelHeight / 80.0, 0.5),
                        TextString = f.Name + "  " + FormatPlanStation(f.StartStation) +
                                     "—" + FormatPlanStation(f.EndStation) +
                                     "  1:" + scale.ToString("0.##", CultureInfo.InvariantCulture),
                        Layer = layerName
                    };
                    ms.AppendEntity(label);
                    tr.AddNewlyCreatedDBObject(label, true);

                    windows.Add(new JsonObject
                    {
                        ["index"] = f.Index,
                        ["name"] = f.Name,
                        ["start_station"] = f.StartStation,
                        ["end_station"] = f.EndStation,
                        ["start_station_text"] = FormatPlanStation(f.StartStation),
                        ["end_station_text"] = FormatPlanStation(f.EndStation),
                        ["minx"] = f.MinX, ["miny"] = f.MinY,
                        ["maxx"] = f.MaxX, ["maxy"] = f.MaxY,
                        ["width"] = modelWidth, ["height"] = modelHeight,
                        ["rotation"] = 0,
                        ["boundary_handle"] = pl.Handle.ToString()
                    });
                }
                tr.Commit();
            }

            bool truncated = specs.Count > 0 &&
                             specs[specs.Count - 1].EndStation < Math.Min(
                                 alignmentEnd, GetDouble(a, "end_station", alignmentEnd)) - 1e-7;
            return new JsonObject
            {
                ["alignment"] = alignmentName,
                ["paper"] = paper,
                ["scale"] = scale,
                ["horizontal"] = true,
                ["rotation"] = 0,
                ["viewport_width_mm"] = viewportWidthMm,
                ["viewport_height_mm"] = viewportHeightMm,
                ["edge_margin_mm"] = edgeMarginMm,
                ["overlap_ratio"] = overlapRatio,
                ["sample_step"] = sampleStep,
                ["layer"] = layerName,
                ["count"] = specs.Count,
                ["truncated_by_max_frames"] = truncated,
                ["old_entities_erased"] = erased,
                ["frames"] = windows.DeepClone(),
                ["windows"] = windows
            };
        }

        static void EnsurePlanFrameLayerAndRegApp(Database db, Transaction tr, string layerName)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(layerName))
            {
                lt.UpgradeOpen();
                var ltr = new LayerTableRecord
                {
                    Name = layerName,
                    IsPlottable = false,
                    Color = Color.FromColorIndex(ColorMethod.ByAci, 8)
                };
                lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
            }
            else
            {
                var ltr = (LayerTableRecord)tr.GetObject(lt[layerName], OpenMode.ForWrite);
                ltr.IsPlottable = false;
            }

            EnsureRegApp(tr, db, PlanFrameRegApp);
        }

        static string FormatPlanStation(double station)
        {
            int km = (int)Math.Floor(station / 1000.0);
            double remainder = station - km * 1000.0;
            return "K" + km.ToString(CultureInfo.InvariantCulture) + "+" +
                   remainder.ToString("000.000", CultureInfo.InvariantCulture);
        }
    }
}
