using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeRestoreCorridorSectionLabels(
            JsonObject args, Document doc)
        {
            return new JsonObject
            {
                ["mode"] = "deferred_to_civil3d_com_host",
                ["command"] = "CORRIDORSECTIONLABELSCONV -> ALL -> C",
                ["executed"] = false
            };
        }
    }
}
