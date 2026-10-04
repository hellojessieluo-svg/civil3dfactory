using System.Text.Json.Nodes;
using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeCreateSectionViews(JsonObject args, Document doc)
            => args["append_station"] == null ? CreateSectionViews(args, doc) : AppendMissingSectionView(args, doc);

        static JsonNode AppendMissingSectionView(JsonObject a, Document doc)
        {
            string alignment = Need(a, "alignment");
            string templateHandle = Need(a, "template_view");
            double station = GetDouble(a, "append_station", double.NaN);
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var al = FindAlignment(tr, civ, alignment);
                if (al == null) throw new InvalidOperationException("Alignment not found: " + alignment);
                var templateId = db.GetObjectId(false, new Handle(Convert.ToInt64(templateHandle, 16)), 0);
                var template = tr.GetObject(templateId, OpenMode.ForRead) as SectionView;
                if (template == null) throw new InvalidOperationException("template_view is not a section view");
                var templateLine = (SampleLine)tr.GetObject(template.SampleLineId, OpenMode.ForRead);
                bool templateInAlignment = false;
                ObjectId target = ObjectId.Null;
                foreach (ObjectId gid in al.GetSampleLineGroupIds())
                {
                    var group = (SampleLineGroup)tr.GetObject(gid, OpenMode.ForRead);
                    foreach (ObjectId sid in group.GetSampleLineIds())
                    {
                        if (sid == templateLine.ObjectId) templateInAlignment = true;
                        var line = (SampleLine)tr.GetObject(sid, OpenMode.ForRead);
                        if (Math.Abs(line.Station - station) > 1e-7) continue;
                        if (!target.IsNull) throw new InvalidOperationException("Station belongs to multiple sample line groups; selection is ambiguous");
                        target = sid;
    }
}
                if (!templateInAlignment || target.IsNull)
                    throw new InvalidOperationException("Template or target sample line does not belong to the specified alignment");
                var sample = (SampleLine)tr.GetObject(target, OpenMode.ForRead);
                var existing = sample.GetSectionViewIds();
                if (existing.Count > 0)
                    return new JsonObject { ["alignment"] = alignment, ["station"] = sample.Station,
                        ["created"] = 0, ["old_erased"] = 0, ["existing"] = existing.Count };
                var point = template.Location + new Vector3d(GetDouble(a, "dx", 0), GetDouble(a, "dy", -28), 0);
                ObjectId id = SectionView.Create("[" + alignment + "]SectionView{Additional" + station.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "}", target, point);
                var view = (SectionView)tr.GetObject(id, OpenMode.ForWrite);
                view.StyleId = template.StyleId;
                view.LayerId = template.LayerId;
                view.IsOffsetRangeAutomatic = template.IsOffsetRangeAutomatic;
                if (!template.IsOffsetRangeAutomatic) { view.OffsetLeft = template.OffsetLeft; view.OffsetRight = template.OffsetRight; }
                view.IsElevationRangeAutomatic = template.IsElevationRangeAutomatic;
                if (!template.IsElevationRangeAutomatic) { view.ElevationMin = template.ElevationMin; view.ElevationMax = template.ElevationMax; }
                tr.Commit();
                return new JsonObject { ["alignment"] = alignment, ["station"] = sample.Station,
                    ["created"] = 1, ["old_erased"] = 0, ["handle"] = id.Handle.ToString(),
                    ["template_view"] = templateHandle, ["x"] = point.X, ["y"] = point.Y };
            }
        }
    }
}
