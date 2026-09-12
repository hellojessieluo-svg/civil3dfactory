using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        // Normalisation must go through full AutoCAD COM (SetFont / GetFont are unavailable in accoreconsole),
        // so the host process only registers the contract and does not execute; the real work is in nodes/normalize_textstyles/run.ps1.
        static JsonNode RunNodeNormalizeTextStyles(
            JsonObject args, Document doc)
        {
            return new JsonObject
            {
                ["mode"] = "deferred_to_powershell_external_host",
                ["implementation"] = "nodes/normalize_textstyles/run.ps1",
                ["backend"] = "AutoCAD COM (AutoCAD.Application.25)",
                ["executed"] = false
            };
        }
    }
}
