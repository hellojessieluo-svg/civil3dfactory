using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeCreateProfiles(JsonObject args, Document doc)
            => CreateProfiles(args, doc);
    }
}
