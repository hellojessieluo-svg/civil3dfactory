using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeCreateCorridor(JsonObject args, Document doc)
            => CreateCorridor(args, doc);
    }
}
