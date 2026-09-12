using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DFactory
{
    /// <summary>
    /// Shared drawing scaffold (duplicates merged 2026-08-20): paper sizes, re-runnable XData markers.
    /// Single source of truth for the whole repo: new nodes call this instead of copying the switch/RegApp boilerplate.
    /// </summary>
    public static partial class Ops
    {
        /// <summary>A0-A4 paper sizes (mm, landscape). Change sheet size conventions here only.</summary>
        internal static void PaperSizeMm(string paper, out double wMm, out double hMm)
        {
            switch ((paper ?? "A3").Trim().ToUpperInvariant())
            {
                case "A0": wMm = 1189; hMm = 841; break;
                case "A1": wMm = 841; hMm = 594; break;
                case "A2": wMm = 594; hMm = 420; break;
                case "A3": wMm = 420; hMm = 297; break;
                case "A4": wMm = 297; hMm = 210; break;
                default: throw new InvalidOperationException("Unknown paper size '" + paper + "'; use A0/A1/A2/A3/A4.");
            }
        }

        /// <summary>Registers the XData application name (idempotent).</summary>
        internal static void EnsureRegApp(Transaction tr, Database db, string appName)
        {
            var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (rat.Has(appName)) return;
            rat.UpgradeOpen();
            var rec = new RegAppTableRecord { Name = appName };
            rat.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }

        /// <summary>Erases model-space entities tagged with the given XData application name (re-run cleanup); returns the count erased.</summary>
        internal static int EraseTaggedEntities(Transaction tr, Database db, string appName)
        {
            var kill = new List<ObjectId>();
            foreach (ObjectId id in ModelSpace(db, tr))
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;
                ResultBuffer rb = null;
                try { rb = ent.GetXDataForApplication(appName); }
                catch (System.Exception) { }
                if (rb == null) continue;
                rb.Dispose();
                kill.Add(id);
            }
            foreach (ObjectId id in kill)
                ((Entity)tr.GetObject(id, OpenMode.ForWrite)).Erase();
            return kill.Count;
        }
    }
}
