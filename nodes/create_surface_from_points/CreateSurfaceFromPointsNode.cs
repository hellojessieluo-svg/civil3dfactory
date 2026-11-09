using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeCreateSurfaceFromPoints(JsonObject a, Document doc)
        {
            string name = GetString(a, "name", "");
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("name is required");
            bool replace = GetBool(a, "replace", true);
            bool dropZero = GetBool(a, "drop_zero", true);
            double zMin = GetDouble(a, "z_min", double.NegativeInfinity);
            double zMax = GetDouble(a, "z_max", double.PositiveInfinity);
            double maxLen = GetDouble(a, "max_triangle_length", 0);
            string styleName = GetString(a, "style", null);
            string layer = GetString(a, "layer", null);
            string blockLayer = GetString(a, "block_layer", null);
            var blockNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (a["block_names"] is JsonArray bn) foreach (JsonNode n in bn) if (n != null) blockNames.Add(n.ToString());

            var raw = new List<Point3d>();
            int fromPoints = 0, fromFile = 0, fromBlocks = 0;
            if (a["points"] is JsonArray pa) foreach (JsonNode p in pa) { raw.Add(new Point3d(p[0].GetValue<double>(), p[1].GetValue<double>(), p[2].GetValue<double>())); fromPoints++; }
            string file = GetString(a, "points_file", null);
            if (!string.IsNullOrEmpty(file))
            {
                string text = File.ReadAllText(file);
                if (text.TrimStart().StartsWith("["))
                {
                    foreach (JsonNode p in (JsonArray)JsonNode.Parse(text)) { raw.Add(new Point3d(p[0].GetValue<double>(), p[1].GetValue<double>(), p[2].GetValue<double>())); fromFile++; }
                }
                else
                {
                    foreach (string line in File.ReadAllLines(file))
                    {
                        var f = line.Split(new[] { ',', '\t', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
                        if (f.Length < 3) continue;
                        if (double.TryParse(f[f.Length - 3], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                            && double.TryParse(f[f.Length - 2], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                            && double.TryParse(f[f.Length - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                        { raw.Add(new Point3d(x, y, z)); fromFile++; }
                    }
                }
            }

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var warn = new JsonArray();
            var rep = new JsonObject();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (blockLayer != null || blockNames.Count > 0)
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (br == null) continue;
                        if (blockLayer != null && !string.Equals(br.Layer, blockLayer, StringComparison.OrdinalIgnoreCase)) continue;
                        if (blockNames.Count > 0)
                        {
                            string bname = br.Name;
                            try { if (br.IsDynamicBlock) bname = ((BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead)).Name; } catch { }
                            if (!blockNames.Contains(bname)) continue;
                        }
                        raw.Add(br.Position); fromBlocks++;
                    }
                }
                if (raw.Count < 3) throw new InvalidOperationException("fewer than 3 usable points (give at least one source: points / points_file / block_layer / block_names)");

                int droppedZero = 0, droppedRange = 0, duplicates = 0;
                var seen = new HashSet<(long, long)>();
                var pts = new Point3dCollection();
                double kMin = double.MaxValue, kMax = double.MinValue;
                foreach (var p in raw)
                {
                    if (dropZero && p.Z == 0) { droppedZero++; continue; }
                    if (p.Z < zMin || p.Z > zMax) { droppedRange++; continue; }
                    if (!seen.Add(((long)Math.Round(p.X * 1000), (long)Math.Round(p.Y * 1000)))) { duplicates++; continue; }
                    pts.Add(p); kMin = Math.Min(kMin, p.Z); kMax = Math.Max(kMax, p.Z);
                }
                if (pts.Count < 3) throw new InvalidOperationException("fewer than 3 points after filtering");

                foreach (ObjectId sid0 in civ.GetSurfaceIds())
                {
                    var s0 = tr.GetObject(sid0, OpenMode.ForRead) as CivSurface;
                    if (s0 != null && s0.Name == name) { if (!replace) throw new InvalidOperationException("surface already exists: " + name); s0.UpgradeOpen(); s0.Erase(); break; }
                }
                ObjectId sid = CivTinSurface.Create(db, name);
                var tin = (CivTinSurface)tr.GetObject(sid, OpenMode.ForWrite);
                if (maxLen > 0)
                {
                    try { tin.BuildOptions.UseMaximumTriangleLength = true; tin.BuildOptions.MaximumTriangleLength = maxLen; }
                    catch (System.Exception ex) { warn.Add("setting the maximum triangle length failed: " + ex.Message); }
                }
                tin.AddVertices(pts);
                bool outerAdded = false;
                if (a["outer"] is JsonArray outer && outer.Count >= 3)
                {
                    var op = new Point3dCollection();
                    foreach (JsonNode p in outer) op.Add(new Point3d(p[0].GetValue<double>(), p[1].GetValue<double>(), 0));
                    try { tin.BoundariesDefinition.AddBoundaries(op, 1.0, SurfaceBoundaryType.Outer, true); outerAdded = true; }
                    catch (System.Exception ex) { warn.Add("Outer boundary failed: " + ex.Message); }
                }
                if (!string.IsNullOrEmpty(styleName)) { ObjectId st = FindStyleId(tr, civ.Styles.SurfaceStyles, styleName); if (!st.IsNull) tin.StyleId = st; else warn.Add("Style not found: " + styleName); }
                if (!string.IsNullOrEmpty(layer)) { EnsureLayer(db, tr, layer, 4); tin.Layer = layer; }
                try { tin.Rebuild(); } catch (System.Exception ex) { warn.Add("Rebuild: " + ex.Message); }
                int tri = 0; double maxEdge = 0;
                try { var tp = tin.GetTinProperties(); tri = tp.NumberOfTriangles; maxEdge = Math.Round(tp.MaximumTriangleLength, 3); } catch { }
                if (tri == 0) warn.Add("0 triangles: the points may be collinear or all filtered out");
                rep["name"] = name;
                rep["handle"] = sid.Handle.ToString();
                rep["sources"] = new JsonObject { ["points"] = fromPoints, ["file"] = fromFile, ["blocks"] = fromBlocks };
                rep["kept"] = pts.Count;
                rep["dropped_zero"] = droppedZero;
                rep["dropped_range"] = droppedRange;
                rep["duplicates"] = duplicates;
                rep["z_min"] = Math.Round(kMin, 3);
                rep["z_max"] = Math.Round(kMax, 3);
                rep["outer_added"] = outerAdded;
                rep["max_triangle_length"] = maxLen;
                rep["triangles"] = tri;
                rep["max_edge"] = maxEdge;
                rep["warnings"] = warn;
                tr.Commit();
            }
            return rep;
        }
    }
}
