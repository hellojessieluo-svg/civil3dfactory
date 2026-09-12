using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlign = Autodesk.Civil.DatabaseServices.Alignment;
using CivEntityColl = Autodesk.Civil.DatabaseServices.AlignmentEntityCollection;
using CivEntity = Autodesk.Civil.DatabaseServices.AlignmentEntity;
using CivSub = Autodesk.Civil.DatabaseServices.AlignmentSubEntity;
using CivSubArc = Autodesk.Civil.DatabaseServices.AlignmentSubEntityArc;
using CivSubType = Autodesk.Civil.DatabaseServices.AlignmentSubEntityType;
using CivilDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    /// <summary>
    /// Convert the plan geometry of a Civil 3D Alignment into a plain LWPOLYLINE,
    /// for pure-CAD deliverables: the recipient has no Civil 3D, so alignment objects would arrive as proxy entities.
    ///
    /// The geometry is **exact**, not a sampled approximation:
    ///   line segment   -> line segment (bulge = 0)
    ///   arc segment    -> bulged segment, bulge = tan(delta/4), negative for clockwise (AutoCAD convention: positive = counter-clockwise)
    ///   spiral         -> the only one sampled, by spiral_step (a spiral has no exact polyline representation)
    /// The result reports length_delta, the difference between polyline length and alignment length, to verify the conversion did not drift.
    ///
    /// The reverse node is create_alignment (polyline -> alignment).
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeAlignmentToPolyline(JsonObject a, Document doc)
            => AlignmentToPolyline(a, doc);

        public static JsonNode AlignmentToPolyline(JsonObject a, Document doc)
        {
            string name = GetString(a, "alignment", null);
            string handle = GetString(a, "handle", null);
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(handle))
                throw new InvalidOperationException("alignment (alignment name) or handle (alignment handle) is required.");

            string layerName = GetString(a, "layer", "C3DF-CL");
            short colorIndex = (short)Math.Max(0, Math.Min(256, (int)GetDouble(a, "color_index", 3)));
            bool colorByLayer = GetBool(a, "color_bylayer", false);
            string linetype = GetString(a, "linetype", "CENTER2");
            string linetypeFile = GetString(a, "linetype_file", "acadiso.lin");
            double ltScale = GetDouble(a, "linetype_scale", 5.0);
            double elevation = GetDouble(a, "elevation", 0.0);
            double spiralStep = GetDouble(a, "spiral_step", 5.0);
            if (spiralStep <= 1e-6) spiralStep = 5.0;
            bool eraseSource = GetBool(a, "erase_source", false);

            Database db = doc.Database;
            var res = new JsonObject();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivilDoc civ = Civ(db);
                CivAlign al;
                if (!string.IsNullOrWhiteSpace(handle))
                {
                    al = tr.GetObject(ResolveHandle(db, handle), OpenMode.ForRead) as CivAlign;
                    if (al == null)
                        throw new InvalidOperationException("Handle " + handle + " is not an alignment.");
                }
                else
                {
                    al = FindAlignment(tr, civ, name);
                    if (al == null)
                        throw new InvalidOperationException("No alignment named " + name + " in the drawing.");
                }

                string alName = al.Name;
                double alLength = al.Length;

                var verts = new List<Point2d>();
                var bulges = new List<double>();
                int nLine = 0, nArc = 0, nSpiral = 0, nOther = 0, sampled = 0;
                Point2d tail = new Point2d(0.0, 0.0);
                bool hasTail = false;

                CivEntityColl ents = al.Entities;
                int entCount = ents.Count;
                if (entCount <= 0)
                    throw new InvalidOperationException("Alignment " + alName + " has no geometry entities; cannot convert.");

                for (int i = 0; i < entCount; i++)
                {
                    CivEntity ent = ents.GetEntityByOrder(i);
                    int subCount = ent.SubEntityCount;
                    for (int j = 0; j < subCount; j++)
                    {
                        CivSub sub = ent[j];
                        if (sub.SubEntityType == CivSubType.Arc)
                        {
                            var arc = sub as CivSubArc;
                            double bulge = 0.0;
                            if (arc != null)
                            {
                                double delta = Math.Abs(arc.Delta);
                                bulge = Math.Tan(delta / 4.0);
                                if (arc.Clockwise) bulge = -bulge;
                            }
                            verts.Add(sub.StartPoint);
                            bulges.Add(bulge);
                            nArc++;
                        }
                        else if (sub.SubEntityType == CivSubType.Spiral)
                        {
                            // Spiral: no polyline equivalent, sample by step
                            double s0 = sub.StartStation, s1 = sub.EndStation;
                            double span = s1 - s0;
                            int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(span) / spiralStep));
                            for (int k = 0; k < steps; k++)
                            {
                                double st = s0 + span * k / steps;
                                double east = 0.0, north = 0.0;
                                al.PointLocation(st, 0.0, ref east, ref north);
                                verts.Add(new Point2d(east, north));
                                bulges.Add(0.0);
                                sampled++;
                            }
                            nSpiral++;
                        }
                        else
                        {
                            verts.Add(sub.StartPoint);
                            bulges.Add(0.0);
                            if (sub.SubEntityType == CivSubType.Line) nLine++; else nOther++;
                        }
                        tail = sub.EndPoint;
                        hasTail = true;
                    }
                }

                if (verts.Count == 0)
                    throw new InvalidOperationException("Alignment " + alName + " has no convertible sub-entities.");
                if (hasTail) { verts.Add(tail); bulges.Add(0.0); }

                ObjectId layerId = A2PEnsureLayer(tr, db, layerName, colorIndex, linetype, linetypeFile);
                ObjectId ltId = A2PResolveLinetype(tr, db, linetype, linetypeFile);

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var pl = new Polyline(verts.Count);
                pl.SetDatabaseDefaults();
                for (int i = 0; i < verts.Count; i++)
                    pl.AddVertexAt(i, verts[i], bulges[i], 0.0, 0.0);
                pl.Elevation = elevation;
                pl.Closed = false;
                pl.LayerId = layerId;
                if (!colorByLayer)
                    pl.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                        Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIndex);
                if (!ltId.IsNull) pl.LinetypeId = ltId;
                pl.LinetypeScale = ltScale;

                ms.AppendEntity(pl);
                tr.AddNewlyCreatedDBObject(pl, true);

                double plLength = 0.0;
                try { plLength = pl.Length; } catch (System.Exception) { }

                bool erased = false;
                if (eraseSource)
                {
                    al.UpgradeOpen();
                    al.Erase();
                    erased = true;
                }

                res["alignment"] = alName;
                res["alignment_handle"] = handle ?? "";
                res["alignment_length"] = Math.Round(alLength, 4);
                res["polyline_handle"] = pl.Handle.ToString();
                res["polyline_length"] = Math.Round(plLength, 4);
                res["length_delta"] = Math.Round(plLength - alLength, 4);
                res["vertices"] = verts.Count;
                res["segments"] = new JsonObject
                {
                    ["line"] = nLine,
                    ["arc"] = nArc,
                    ["spiral"] = nSpiral,
                    ["other"] = nOther,
                    ["spiral_sampled_points"] = sampled
                };
                res["layer"] = layerName;
                res["color_index"] = colorByLayer ? -1 : (int)colorIndex;
                res["linetype"] = ltId.IsNull ? "(not loaded, fell back to ByLayer)" : linetype;
                res["linetype_scale"] = ltScale;
                res["source_erased"] = erased;

                tr.Commit();
            }

            return res;
        }

        /// <summary>Get a linetype; load it from the linetype file if absent. Load failure does not throw; the caller falls back to ByLayer.</summary>
        static ObjectId A2PResolveLinetype(Transaction tr, Database db, string name, string file)
        {
            if (string.IsNullOrWhiteSpace(name)) return ObjectId.Null;
            LinetypeTable table = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            if (table.Has(name)) return table[name];
            try
            {
                db.LoadLineTypeFile(name, file);
                table = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                if (table.Has(name)) return table[name];
            }
            catch (System.Exception) { }
            return ObjectId.Null;
        }

        /// <summary>Get a layer; create it with the given color/linetype if absent. Existing layers keep their settings.</summary>
        static ObjectId A2PEnsureLayer(Transaction tr, Database db, string name,
            short colorIndex, string linetype, string linetypeFile)
        {
            LayerTable table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (table.Has(name)) return table[name];

            ObjectId ltId = A2PResolveLinetype(tr, db, linetype, linetypeFile);
            table.UpgradeOpen();
            var record = new LayerTableRecord
            {
                Name = name,
                Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIndex)
            };
            if (!ltId.IsNull) record.LinetypeObjectId = ltId;
            ObjectId id = table.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
            return id;
        }
    }
}
