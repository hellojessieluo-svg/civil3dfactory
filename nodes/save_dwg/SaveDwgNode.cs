using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeSaveDwg(JsonObject args, Document doc)
            => SaveDwg(args, doc);
    }
}
