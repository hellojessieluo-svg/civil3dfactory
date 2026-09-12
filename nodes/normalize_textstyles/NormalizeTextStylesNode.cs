using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        // 归化必须走完整 AutoCAD COM（SetFont / GetFont 在 accoreconsole 里不可用），
        // 所以宿主进程只登记契约、不执行，实际执行在 nodes/normalize_textstyles/run.ps1。
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
