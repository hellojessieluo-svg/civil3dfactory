using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivAssembly = Autodesk.Civil.DatabaseServices.Assembly;
using CivSubassembly = Autodesk.Civil.DatabaseServices.Subassembly;
using CivPoint = Autodesk.Civil.DatabaseServices.Point;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeAddStockSubassemblies(JsonObject a, Document doc)
        {
            string asmName = Need(a, "assembly");
            var items = a["items"] as JsonArray;
            if (items == null || items.Count == 0) throw new InvalidOperationException("items is required");
            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var rep = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAssembly asm = null;
                foreach (ObjectId id in civ.AssemblyCollection)
                { var x = tr.GetObject(id, OpenMode.ForRead) as CivAssembly; if (x != null && x.Name == asmName) { asm = x; break; } }
                if (asm == null) throw new InvalidOperationException("Assembly not found: '" + asmName + "'");
                asm.UpgradeOpen();

                foreach (JsonNode n in items)
                {
                    var o = n as JsonObject; if (o == null) continue;
                    string name = o["name"] == null ? null : o["name"].ToString();
                    string cls = o["class"] == null ? "Subassembly.MarkPoint" : o["class"].ToString();
                    var att = o["attach"] as JsonObject;
                    if (name == null || att == null) throw new InvalidOperationException("Each item requires name and attach{subassembly,point_code}.");
                    string hostSub = att["subassembly"].ToString(); string code = att["point_code"].ToString();

                    CivSubassembly host = null; CivPoint hook = null; var seen = new List<string>();
                    foreach (CivSubassembly sa in ComposerSubassemblies(asm, tr))
                    {
                        if (sa.Name.IndexOf(hostSub, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        host = sa;
                        foreach (CivPoint p in sa.Points)
                        {
                            var codes = new List<string>();
                            foreach (var c in p.Codes) codes.Add(c.ToString());
                            seen.Add(string.Join("/", codes.ToArray()));
                            if (codes.Contains(code)) { hook = p; break; }
                        }
                        if (hook != null) break;
                    }
                    if (host == null) throw new InvalidOperationException("No subassembly name contains '" + hostSub + "' in the assembly");
                    if (hook == null) throw new InvalidOperationException("Subassembly " + host.Name + " has no point with code '" + code + "'; available point codes: " + string.Join(" | ", seen.ToArray()));

                    ObjectId newId = civ.SubassemblyCollection.ImportStockSubassembly(name, cls, asm.Location);
                    var sub = (CivSubassembly)tr.GetObject(newId, OpenMode.ForWrite);
                    var plog = new JsonArray();
                    ApplyParams(sub, o["params_string"] as JsonObject, "string", plog);
                    ApplyParams(sub, o["params_double"] as JsonObject, "double", plog);
                    ApplyParams(sub, o["params_long"] as JsonObject, "long", plog);
                    asm.AddSubassembly(newId, hook);
                    rep.Add(new JsonObject { ["name"] = name, ["class"] = cls, ["handle"] = newId.Handle.ToString(), ["attached_to"] = host.Name + " @ " + code + " (idx " + hook.Index + ")", ["params"] = plog });
                }
                tr.Commit();
            }
            return new JsonObject { ["assembly"] = asmName, ["added"] = rep };
        }

        static void ApplyParams(CivSubassembly sub, JsonObject prm, string kind, JsonArray log)
        {
            if (prm == null) return;
            System.Collections.IEnumerable coll = kind == "string" ? (System.Collections.IEnumerable)sub.ParamsString
                                                 : kind == "double" ? (System.Collections.IEnumerable)sub.ParamsDouble
                                                 : (System.Collections.IEnumerable)sub.ParamsLong;
            var names = new List<string>();
            foreach (var kv in prm)
            {
                bool hit = false;
                foreach (object p in coll)
                {
                    string dn = ReadProperty(p, "DisplayName") as string;
                    if (dn == null) continue;
                    if (!names.Contains(dn)) names.Add(dn);
                    if (!string.Equals(dn, kv.Key, StringComparison.OrdinalIgnoreCase)) continue;
                    var vp = p.GetType().GetProperty("Value");
                    object val = kind == "string" ? (object)kv.Value.ToString()
                               : kind == "double" ? (object)kv.Value.GetValue<double>() : (object)(long)kv.Value.GetValue<double>();
                    try { vp.SetValue(p, Convert.ChangeType(val, vp.PropertyType), null); hit = true; }
                    catch (System.Exception ex) { log.Add(new JsonObject { ["param"] = kv.Key, ["ok"] = false, ["why"] = ex.Message }); hit = true; }
                    if (hit) { log.Add(new JsonObject { ["param"] = kv.Key, ["value"] = val.ToString(), ["ok"] = true }); break; }
                }
                if (!hit) log.Add(new JsonObject { ["param"] = kv.Key, ["ok"] = false, ["why"] = "Parameter not found; available (" + kind + "): " + string.Join(", ", names.ToArray()) });
            }
        }
    }
}
