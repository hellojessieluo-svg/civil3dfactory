using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        /// <summary>
        /// Resolves objects inside the Civil 3D host by the selection rules of a confirmed work order.
        /// Resolution happens at run time; historical work orders keep the selection rules instead of freezing object names early.
        /// </summary>
        public static JsonObject ResolveFactoryTask(JsonObject task, Document doc)
        {
            var report = new JsonObject();
            if (task == null) return report;
            JsonObject resolution = task["resolution"] as JsonObject;
            JsonArray ops = task["ops"] as JsonArray;
            if (resolution == null || ops == null) return report;

            var bindings = new Dictionary<string, string>(StringComparer.Ordinal);

            JsonObject groundRule = resolution["ground_surface"] as JsonObject;
            if (groundRule != null)
            {
                JsonObject selector = groundRule["selector"] as JsonObject;
                string contains = selector != null && selector["name_contains"] != null
                    ? selector["name_contains"].GetValue<string>()
                    : null;
                if (string.IsNullOrWhiteSpace(contains))
                    throw new InvalidOperationException("ground_surface.selector.name_contains must not be empty.");

                var matches = new List<string>();
                JsonArray surfaces = ListSurfaces(new JsonObject(), doc) as JsonArray;
                foreach (JsonNode item in surfaces)
                {
                    JsonObject obj = item as JsonObject;
                    string name = obj != null && obj["name"] != null
                        ? obj["name"].GetValue<string>()
                        : null;
                    if (name != null && name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
                        matches.Add(name);
                }
                if (matches.Count != 1)
                    throw new InvalidOperationException(
                        "Ground surface selector requires a unique match; matched " + matches.Count +
                        " surfaces: " + string.Join(", ", matches));
                bindings["$ground_surface"] = matches[0];
                report["ground_surface"] = matches[0];
            }

            JsonObject assemblyRule = resolution["assembly"] as JsonObject;
            if (assemblyRule != null)
            {
                JsonObject env = CivilEnv(new JsonObject(), doc) as JsonObject;
                JsonArray assemblies = env != null ? env["assemblies"] as JsonArray : null;
                if (assemblies == null || assemblies.Count == 0)
                    throw new InvalidOperationException("Assembly list is empty; cannot pick the first item.");
                JsonObject first = assemblies[0] as JsonObject;
                string name = first != null && first["name"] != null
                    ? first["name"].GetValue<string>()
                    : null;
                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidOperationException("First assembly in the list has no name.");
                bindings["$assembly"] = name;
                report["assembly"] = name;
            }

            JsonObject criteriaRule = resolution["quantity_criteria"] as JsonObject;
            if (criteriaRule != null)
            {
                Database db = doc.Database;
                CivDoc civ = Civ(db);
                string firstName = null;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in civ.Styles.QuantityTakeoffCriterias)
                    {
                        firstName = TryGetName(tr.GetObject(id, OpenMode.ForRead));
                        if (!string.IsNullOrWhiteSpace(firstName)) break;
                    }
                }
                if (string.IsNullOrWhiteSpace(firstName))
                    throw new InvalidOperationException("Quantity takeoff criteria list is empty; cannot pick the first item.");
                bindings["$quantity_criteria"] = firstName;
                report["quantity_criteria"] = firstName;
            }

            ApplyBindings(ops, bindings);
            return report;
        }

        static void ApplyBindings(JsonNode node, Dictionary<string, string> bindings)
        {
            JsonObject obj = node as JsonObject;
            if (obj != null)
            {
                var keys = new List<string>();
                foreach (var pair in obj) keys.Add(pair.Key);
                foreach (string key in keys)
                {
                    JsonNode child = obj[key];
                    JsonValue value = child as JsonValue;
                    string token;
                    if (value != null && value.TryGetValue<string>(out token)
                        && bindings.TryGetValue(token, out string replacement))
                    {
                        obj[key] = replacement;
                    }
                    else ApplyBindings(child, bindings);
                }
                return;
            }

            JsonArray arr = node as JsonArray;
            if (arr != null)
                foreach (JsonNode child in arr) ApplyBindings(child, bindings);
        }
    }
}
