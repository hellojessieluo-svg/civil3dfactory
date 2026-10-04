using System;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivGroupPlotStyle = Autodesk.Civil.DatabaseServices.Styles.GroupPlotStyle;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeSetGroupPlotStyle(JsonObject a, Document doc)
        {
            string name = Need(a, "name");
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var rep = new JsonObject { ["name"] = name };
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId id = FindStyleId(tr, civ.Styles.GroupPlotStyles, name);
                if (id.IsNull) throw new InvalidOperationException("Group plot style not found: '" + name + "'");
                var g = (CivGroupPlotStyle)tr.GetObject(id, OpenMode.ForWrite);
                Func<JsonObject> snap = () => new JsonObject
                {
                    ["rows_max(MaximumInColumn)"] = g.MaximumInColumn, ["cols_max(MaximumInRow)"] = g.MaximumInRow,
                    ["space_row_mm"] = Math.Round(g.SpaceRow * 1000, 2), ["space_col_mm"] = Math.Round(g.SpaceColumn * 1000, 2),
                    ["gap_between_pages_mm"] = Math.Round(g.GapBetweenPages * 1000, 2),
                    ["cell"] = g.CellSizeType.ToString(), ["rule"] = g.PlotRule.ToString(), ["align"] = g.AlignType.ToString(), ["start"] = g.StartCorner.ToString()
                };
                rep["before"] = snap();
                if (a["rows"] != null) g.MaximumInColumn = (int)GetDouble(a, "rows", 4);
                if (a["cols"] != null) g.MaximumInRow = (int)GetDouble(a, "cols", 1);
                if (a["space_row_mm"] != null) g.SpaceRow = GetDouble(a, "space_row_mm", 40) / 1000.0;
                if (a["space_col_mm"] != null) g.SpaceColumn = GetDouble(a, "space_col_mm", 30) / 1000.0;
                if (a["gap_between_pages_mm"] != null) g.GapBetweenPages = GetDouble(a, "gap_between_pages_mm", 0) / 1000.0;
                rep["after"] = snap();
                tr.Commit();
            }
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId id = FindStyleId(tr, civ.Styles.GroupPlotStyles, name);
                var g = (CivGroupPlotStyle)tr.GetObject(id, OpenMode.ForRead);
                rep["reread_space_row_mm"] = Math.Round(g.SpaceRow * 1000, 2);
                rep["reread_space_col_mm"] = Math.Round(g.SpaceColumn * 1000, 2);
                tr.Commit();
            }
            return rep;
        }
    }
}
