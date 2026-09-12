using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeCreateSheetRegion(JsonObject args, Document doc)
            => CreateSheetRegion(args, doc);
    }
}
