using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        static JsonNode RunNodeReflectMembers(JsonObject a, Document doc)
        {
            string q = Need(a, "type");
            int max = (int)GetDouble(a, "max", 20);
            string asmFilter = GetString(a, "assembly", null);
            var found = new JsonArray();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string asmName = asm.GetName().Name ?? "";
                if (!string.IsNullOrEmpty(asmFilter) && asmName.IndexOf(asmFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                foreach (var t in types)
                {
                    if (t.FullName == null || t.FullName.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var members = new JsonArray();
                    foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        string s;
                        switch (m)
                        {
                            case MethodInfo mi:
                                if (mi.IsSpecialName) continue;
                                s = mi.ReturnType.Name + " " + mi.Name + "(" + string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")";
                                break;
                            case PropertyInfo pi:
                                s = pi.PropertyType.Name + " " + pi.Name + " {" + (pi.CanRead ? " get;" : "") + (pi.CanWrite ? " set;" : "") + " }";
                                break;
                            case ConstructorInfo ci:
                                s = "ctor(" + string.Join(", ", ci.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")";
                                break;
                            case FieldInfo fi:
                                s = "field " + fi.FieldType.Name + " " + fi.Name;
                                break;
                            default:
                                s = m.MemberType + " " + m.Name;
                                break;
                        }
                        members.Add(s);
                    }
                    found.Add(new JsonObject { ["type"] = t.FullName, ["assembly"] = asmName, ["base"] = t.BaseType?.Name, ["members"] = members });
                    if (found.Count >= max) break;
                }
                if (found.Count >= max) break;
            }
            return new JsonObject { ["query"] = q, ["types"] = found };
        }
    }
}
