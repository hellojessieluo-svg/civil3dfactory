using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeComputeQuantities(JsonObject args, Document doc)
            => ComputeQuantities(args, doc);
    }
}
