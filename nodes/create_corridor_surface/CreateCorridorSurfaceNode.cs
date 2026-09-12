using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeCreateCorridorSurface(JsonObject args, Document doc)
            => CreateCorridorSurface(args, doc);
    }
}
