using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeCreateProfileView(JsonObject args, Document doc)
            => CreateProfileView(args, doc);
    }
}
