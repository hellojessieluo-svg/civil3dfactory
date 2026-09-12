using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeCreateAlignment(JsonObject args, Document doc)
            => CreateAlignment(args, doc);
    }
}
