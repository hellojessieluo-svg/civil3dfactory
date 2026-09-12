using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeCreateSampleLines(JsonObject args, Document doc)
            => CreateSampleLines(args, doc);
    }
}
