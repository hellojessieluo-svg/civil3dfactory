using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeCreateConnectedAlignment(JsonObject args, Document doc)
            => CreateConnectedAlignmentOp(args, doc);
    }
}
