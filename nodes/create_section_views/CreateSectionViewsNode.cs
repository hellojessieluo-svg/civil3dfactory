using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeCreateSectionViews(JsonObject args, Document doc)
            => CreateSectionViews(args, doc);
    }
}
