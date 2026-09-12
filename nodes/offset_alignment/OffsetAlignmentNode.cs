using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeOffsetAlignment(JsonObject args, Document doc)
            => OffsetAlignment(args, doc);
    }
}
