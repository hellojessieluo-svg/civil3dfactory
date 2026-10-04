using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeExtractDwgText(
            JsonObject args, Document doc)
        {
            return new JsonObject
            {
                ["mode"] = "deferred_to_powershell_external_host",
                ["implementation"] = "nodes/extract_dwg_text/run.ps1",
                ["backend"] = "accoreconsole DXFOUT + ezdxf",
                ["executed"] = false
            };
        }
    }
}
