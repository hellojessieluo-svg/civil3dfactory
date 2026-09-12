using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory
{
    /// <summary>
    /// Scan closed LWPOLYLINEs in model space, write area labels and export a detail table.
    /// Node output is tagged with C3DF_POLYAREA XData; a rerun only cleans its own text and never touches user objects.
    /// area_overrides lets you override the area per source polyline handle, e.g. to enter manually checked values.
    /// </summary>
    public static partial class Ops
    {
        const string PolyAreaAppName = "C3DF_POLYAREA";

        sealed class PolyAreaItem
        {
            public ObjectId SourceId;
            public string Handle;
            public string Layer;
            public double RawArea;
            public double FinalArea;
            public bool Corrected;
            public Point3d LabelPoint;
            public string LabelHandle;
        }

        static JsonNode RunNodeAnnotateClosedPolylineAreas(JsonObject a, Document doc)
            => AnnotateClosedPolylineAreas(a, doc);

        public static JsonNode AnnotateClosedPolylineAreas(JsonObject a, Document doc)
        {
            double textHeight = GetDouble(a, "text_height", 2.5);
            if (textHeight <= 1e-9)
                throw new InvalidOperationException("text_height must be greater than 0.");

            int decimals = (int)GetDouble(a, "decimals", 2);
            if (decimals < 0 || decimals > 8) decimals = 2;

            string sourceLayer = GetString(a, "source_layer", null);
            string annotationLayer = GetString(a, "annotation_layer", "C3DF-AREA-LABEL");
            string prefix = GetString(a, "prefix", "");
            string suffix = GetString(a, "suffix", " m²");
            short colorIndex = (short)Math.Max(1, Math.Min(255, (int)GetDouble(a, "color_index", 1)));
            bool includeZero = GetBool(a, "include_zero", true);
            bool clearExisting = GetBool(a, "clear_existing", true);
            bool exportExcel = GetBool(a, "export_excel", true);
            string excelFormat = GetString(a, "excel_format", "xlsx");   // normalised/validated in Excel.Write

            JsonObject overrides = a["area_overrides"] as JsonObject;
            JsonObject offsets = a["label_offsets"] as JsonObject;
            string numberFormat = "F" + decimals.ToString(CultureInfo.InvariantCulture);

            Database db = doc.Database;
            int cleared = 0, zeroCount = 0, correctedCount = 0;
            double rawTotal = 0.0, finalTotal = 0.0;
            var items = new List<PolyAreaItem>();
            var occupied = new Dictionary<string, int>(StringComparer.Ordinal);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EnsureRegApp(tr, db, PolyAreaAppName);
                if (clearExisting) cleared = EraseTaggedEntities(tr, db, PolyAreaAppName);

                ObjectId labelLayerId = PolyAreaEnsureLayer(tr, db, annotationLayer, colorIndex);
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var sourceIds = new List<ObjectId>();
                foreach (ObjectId id in ms)
                {
                    Polyline pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (pl == null || !pl.Closed) continue;
                    if (!string.IsNullOrWhiteSpace(sourceLayer) &&
                        !string.Equals(pl.Layer, sourceLayer, StringComparison.OrdinalIgnoreCase))
                        continue;
                    sourceIds.Add(id);
                }

                foreach (ObjectId id in sourceIds)
                {
                    Polyline pl = (Polyline)tr.GetObject(id, OpenMode.ForRead);
                    double rawArea;
                    try { rawArea = Math.Abs(pl.Area); }
                    catch (System.Exception) { continue; }
                    if (!includeZero && rawArea <= 1e-9) continue;

                    string handle = pl.Handle.ToString();
                    double finalArea;
                    bool corrected = PolyAreaTryGetNumber(overrides, handle, out finalArea);
                    if (!corrected) finalArea = rawArea;

                    Point3d labelPoint = PolyAreaInteriorPoint(pl);
                    Vector2d manualOffset;
                    if (PolyAreaTryGetOffset(offsets, handle, out manualOffset))
                        labelPoint = new Point3d(
                            labelPoint.X + manualOffset.X,
                            labelPoint.Y + manualOffset.Y,
                            labelPoint.Z);

                    string positionKey = Math.Round(labelPoint.X, 4).ToString(CultureInfo.InvariantCulture)
                                       + "|" + Math.Round(labelPoint.Y, 4).ToString(CultureInfo.InvariantCulture);
                    int duplicateIndex;
                    if (occupied.TryGetValue(positionKey, out duplicateIndex))
                    {
                        duplicateIndex++;
                        occupied[positionKey] = duplicateIndex;
                        labelPoint = new Point3d(
                            labelPoint.X,
                            labelPoint.Y - duplicateIndex * textHeight * 1.6,
                            labelPoint.Z);
                    }
                    else occupied[positionKey] = 0;

                    string content = prefix + finalArea.ToString(numberFormat, CultureInfo.InvariantCulture) + suffix;
                    var text = new DBText();
                    text.SetDatabaseDefaults();
                    text.LayerId = labelLayerId;
                    text.Height = textHeight;
                    text.TextString = content;
                    text.Position = labelPoint;
                    text.HorizontalMode = TextHorizontalMode.TextCenter;
                    text.VerticalMode = TextVerticalMode.TextVerticalMid;
                    text.AlignmentPoint = labelPoint;
                    ms.AppendEntity(text);
                    tr.AddNewlyCreatedDBObject(text, true);
                    text.AdjustAlignment(db);
                    PolyAreaSetXData(text, handle, finalArea);

                    items.Add(new PolyAreaItem
                    {
                        SourceId = id,
                        Handle = handle,
                        Layer = pl.Layer,
                        RawArea = rawArea,
                        FinalArea = finalArea,
                        Corrected = corrected,
                        LabelPoint = labelPoint,
                        LabelHandle = text.Handle.ToString()
                    });

                    rawTotal += rawArea;
                    finalTotal += finalArea;
                    if (rawArea <= 1e-9) zeroCount++;
                    if (corrected) correctedCount++;
                }

                if (items.Count == 0)
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(sourceLayer)
                            ? "No usable closed lightweight polyline (LWPOLYLINE) in model space."
                            : "No usable closed lightweight polyline (LWPOLYLINE) on layer '" + sourceLayer + "'.");

                var excelFiles = new List<string>();
                if (exportExcel)
                {
                    string outdir = ResolveOutDir(a, doc);
                    string excelOutPath = GetString(a, "excel_out_path", null);
                    string baseName = string.IsNullOrWhiteSpace(excelOutPath)
                        ? Sanitize(Path.GetFileNameWithoutExtension(SafeFile(db))) + "_ClosedPolylineAreas"
                        : Path.GetFileNameWithoutExtension(excelOutPath);
                    string targetDir = string.IsNullOrWhiteSpace(excelOutPath)
                        ? outdir
                        : (Path.GetDirectoryName(excelOutPath) ?? outdir);
                    if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                    string[] headers =
                    {
                        "No.", "Polyline handle", "Layer", "Object type",
                        "Read area (m2)", "Adopted area (m2)", "Remarks"
                    };
                    var rows = new List<object[]>();
                    for (int i = 0; i < items.Count; i++)
                    {
                        PolyAreaItem item = items[i];
                        rows.Add(new object[]
                        {
                            i + 1, item.Handle, item.Layer, "LWPOLYLINE",
                            Math.Round(item.RawArea, decimals),
                            Math.Round(item.FinalArea, decimals),
                            item.Corrected ? "Corrected by area_overrides" : ""
                        });
                    }
                    rows.Add(new object[]
                    {
                        "Total", "", "", "",
                        Math.Round(rawTotal, decimals),
                        Math.Round(finalTotal, decimals),
                        correctedCount > 0 ? "Includes manual corrections" : ""
                    });
                    excelFiles = Excel.Write(targetDir, baseName, headers, rows, excelFormat);
                }

                var perPolyline = new JsonArray();
                foreach (PolyAreaItem item in items)
                {
                    perPolyline.Add(new JsonObject
                    {
                        ["handle"] = item.Handle,
                        ["layer"] = item.Layer,
                        ["raw_area"] = Math.Round(item.RawArea, 8),
                        ["final_area"] = Math.Round(item.FinalArea, 8),
                        ["corrected"] = item.Corrected,
                        ["label_handle"] = item.LabelHandle,
                        ["label_point"] = new JsonArray
                        {
                            Math.Round(item.LabelPoint.X, 4),
                            Math.Round(item.LabelPoint.Y, 4),
                            Math.Round(item.LabelPoint.Z, 4)
                        }
                    });
                }

                var excelJson = new JsonArray();
                foreach (string path in excelFiles) excelJson.Add(path);

                var result = new JsonObject
                {
                    ["polylines"] = items.Count,
                    ["labels"] = items.Count,
                    ["zero_areas"] = zeroCount,
                    ["corrected"] = correctedCount,
                    ["raw_total_area"] = Math.Round(rawTotal, 8),
                    ["final_total_area"] = Math.Round(finalTotal, 8),
                    ["annotation_layer"] = annotationLayer,
                    ["cleared_old"] = cleared,
                    ["excel_files"] = excelJson,
                    ["per_polyline"] = perPolyline
                };

                tr.Commit();
                return result;
            }
        }

        static Point3d PolyAreaInteriorPoint(Polyline pl)
        {
            double length = 0.0;
            try { length = pl.Length; } catch (System.Exception) { }
            List<Point2d> pts = GridSamplePolygon(pl, Math.Max(length / 256.0, 0.1));

            if (pts.Count < 3)
            {
                try
                {
                    Extents3d ext = pl.GeometricExtents;
                    return new Point3d(
                        (ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                        (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0,
                        (ext.MinPoint.Z + ext.MaxPoint.Z) / 2.0);
                }
                catch (System.Exception)
                {
                    return pl.NumberOfVertices > 0 ? pl.GetPoint3dAt(0) : Point3d.Origin;
                }
            }

            double twiceArea = 0.0, cx = 0.0, cy = 0.0;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            {
                double cross = pts[j].X * pts[i].Y - pts[i].X * pts[j].Y;
                twiceArea += cross;
                cx += (pts[j].X + pts[i].X) * cross;
                cy += (pts[j].Y + pts[i].Y) * cross;
            }

            Point2d centroid;
            if (Math.Abs(twiceArea) > 1e-12)
                centroid = new Point2d(cx / (3.0 * twiceArea), cy / (3.0 * twiceArea));
            else
                centroid = pts[0];
            if (GridPointInPolygon(centroid, pts))
                return new Point3d(centroid.X, centroid.Y, pl.Elevation);

            double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
            foreach (Point2d p in pts) { minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
            Point2d best = pts[0];
            double bestWidth = -1.0;
            for (int k = 1; k < 20; k++)
            {
                double y = minY + (maxY - minY) * k / 20.0;
                List<double> xs = PolyAreaScanlineXs(y, pts);
                for (int i = 0; i + 1 < xs.Count; i += 2)
                {
                    double width = xs[i + 1] - xs[i];
                    if (width > bestWidth)
                    {
                        bestWidth = width;
                        best = new Point2d((xs[i] + xs[i + 1]) / 2.0, y);
                    }
                }
            }
            return new Point3d(best.X, best.Y, pl.Elevation);
        }

        static List<double> PolyAreaScanlineXs(double y, List<Point2d> pts)
        {
            var xs = new List<double>();
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            {
                Point2d a = pts[j], b = pts[i];
                if ((a.Y > y) == (b.Y > y)) continue;
                xs.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
            }
            xs.Sort();
            return xs;
        }

        static bool PolyAreaTryGetNumber(JsonObject values, string handle, out double value)
        {
            value = 0.0;
            if (values == null) return false;
            foreach (KeyValuePair<string, JsonNode> kv in values)
            {
                if (!string.Equals(kv.Key, handle, StringComparison.OrdinalIgnoreCase)) continue;
                try { value = kv.Value.GetValue<double>(); return true; }
                catch (System.Exception)
                {
                    return double.TryParse(kv.Value.ToString(), NumberStyles.Any,
                        CultureInfo.InvariantCulture, out value);
                }
            }
            return false;
        }

        static bool PolyAreaTryGetOffset(JsonObject offsets, string handle, out Vector2d offset)
        {
            offset = new Vector2d(0, 0);
            if (offsets == null) return false;
            JsonNode node = null;
            foreach (KeyValuePair<string, JsonNode> kv in offsets)
                if (string.Equals(kv.Key, handle, StringComparison.OrdinalIgnoreCase))
                { node = kv.Value; break; }
            JsonArray arr = node as JsonArray;
            if (arr == null || arr.Count < 2) return false;
            try
            {
                offset = new Vector2d(arr[0].GetValue<double>(), arr[1].GetValue<double>());
                return true;
            }
            catch (System.Exception) { return false; }
        }

        static ObjectId PolyAreaEnsureLayer(Transaction tr, Database db, string name, short colorIndex)
        {
            LayerTable table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
            var record = new LayerTableRecord
            {
                Name = name,
                Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIndex)
            };
            ObjectId id = table.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
            return id;
        }

        static void PolyAreaSetXData(DBText text, string sourceHandle, double area)
        {
            text.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, PolyAreaAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "AREA_LABEL"),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, sourceHandle),
                new TypedValue((int)DxfCode.ExtendedDataReal, area));
        }
    }
}
