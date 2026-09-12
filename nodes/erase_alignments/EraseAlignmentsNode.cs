using System;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeEraseAlignments(JsonObject args, Document doc)
            => EraseAlignmentsOp(args, doc);

        // Batch-erase alignments by name or by handle (handles are immune to name-encoding issues). Missing ones are reported, not thrown.
        static JsonNode EraseAlignmentsOp(JsonObject a, Document doc)
        {
            var names = a["names"] as JsonArray;
            var handles = a["handles"] as JsonArray;
            if ((names == null || names.Count == 0) && (handles == null || handles.Count == 0))
                throw new InvalidOperationException("names:[] or handles:[] is required");

            Database db = doc.Database;
            var civ = Civ(db);
            int erased = 0;
            var missing = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (handles != null)
                    foreach (JsonNode n in handles)
                    {
                        string h = n.GetValue<string>();
                        if (db.TryGetObjectId(new Handle(Convert.ToInt64(h, 16)), out ObjectId id)
                            && tr.GetObject(id, OpenMode.ForWrite) is CivAlignment al)
                        { al.Erase(); erased++; }
                        else missing.Add(h);
                    }
                if (names != null)
                    foreach (JsonNode n in names)
                    {
                        string nm = n.GetValue<string>();
                        var al = FindAlignment(tr, civ, nm);
                        if (al == null) { missing.Add(nm); continue; }
                        var w = (CivAlignment)tr.GetObject(al.ObjectId, OpenMode.ForWrite);
                        w.Erase(); erased++;
                    }
                tr.Commit();
            }
            return new JsonObject { ["erased"] = erased, ["missing"] = missing };
        }
    }
}
