using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodePlotAttributeTitleblocks(
            JsonObject args, Document doc)
        {
            return new JsonObject
            {
                ["mode"] = "deferred_to_powershell_external_host",
                ["implementation"] = "nodes/plot_attribute_titleblocks/run.ps1",
                ["backend"] = "accoreconsole + CadPlotPlugin",
                ["executed"] = false
            };
        }
    }
}
