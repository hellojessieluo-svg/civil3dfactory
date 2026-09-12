using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeInsertTitleBlocks(JsonObject args, Document doc)
            => InsertTitleBlocks(args, doc);
    }
}
