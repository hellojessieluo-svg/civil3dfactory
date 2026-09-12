using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeExportToAutocad(JsonObject args, Document doc)
            => ExportToAutocad(args, doc);
    }
}
