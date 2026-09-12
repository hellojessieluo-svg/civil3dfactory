using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeExportQuantities(JsonObject args, Document doc)
            => ExportQuantities(args, doc);
    }
}
