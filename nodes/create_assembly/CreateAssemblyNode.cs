using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivAssembly = Autodesk.Civil.DatabaseServices.Assembly;
using CivSubassembly = Autodesk.Civil.DatabaseServices.Subassembly;

namespace Civil3DFactory
{
    /// <summary>
    /// create_assembly: import a LEFT/RIGHT pair of Subassembly Composer .pkt files, put them on a new
    /// assembly and embed the PKT projects in the drawing so the assembly keeps working when the files move.
    /// This is the "PKT -> assembly in the drawing" step of the pkt skill; corridors are built from it.
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeCreateAssembly(JsonObject a, Document doc)
        {
            string name = Need(a, "name");
            string leftPkt = Need(a, "left_pkt");
            string rightPkt = Need(a, "right_pkt");
            bool embed = GetBool(a, "embed", true);
            bool replace = GetBool(a, "replace", true);
            var paramValues = a["params"] as JsonObject;
            double ox = GetDouble(a, "x", 0), oy = GetDouble(a, "y", 0);

            foreach (string p in new[] { leftPkt, rightPkt })
            {
                if (!Path.IsPathRooted(p)) throw new InvalidOperationException("PKT path must be absolute: " + p);
                if (!File.Exists(p)) throw new InvalidOperationException("PKT file not found: " + p);
            }

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var report = new JsonObject { ["assembly"] = name };

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAssembly existing = FindAssembly(db, tr, name);
                if (existing != null)
                {
                    if (!replace) throw new InvalidOperationException("Assembly '" + name + "' already exists (pass replace:true to rebuild it).");
                    existing.UpgradeOpen();
                    existing.Erase();
                    report["replaced"] = true;
                }
                tr.Commit();
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var origin = new Point3d(ox, oy, 0);
                ObjectId asmId = civ.AssemblyCollection.Add(name, Autodesk.Civil.DatabaseServices.AssemblyType.Other, origin);
                var asm = (CivAssembly)tr.GetObject(asmId, OpenMode.ForWrite);

                var subs = new JsonArray();
                var imported = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>(name + "_LEFT", leftPkt),
                    new KeyValuePair<string, string>(name + "_RIGHT", rightPkt)
                };
                foreach (KeyValuePair<string, string> kv in imported)
                {
                    ObjectId saId = civ.SubassemblyCollection.ImportSac(kv.Key, kv.Value, origin);
                    var sa = (CivSubassembly)tr.GetObject(saId, OpenMode.ForWrite);
                    int applied = 0;
                    if (paramValues != null)
                    {
                        foreach (KeyValuePair<string, JsonNode> pv in paramValues)
                        {
                            if (pv.Value == null) continue;
                            if (SetSubassemblyParam(sa, pv.Key, pv.Value)) applied++;
                        }
                    }
                    asm.AddSubassembly(saId);
                    bool embedded = false;
                    if (embed)
                    {
                        try { sa.UseEmbeddedProject = true; embedded = sa.UseEmbeddedProject; }
                        catch (System.Exception ex) { report["embed_error"] = ex.Message; }
                    }
                    subs.Add(new JsonObject
                    {
                        ["name"] = sa.Name,
                        ["pkt"] = kv.Value,
                        ["side"] = SafeStr(() => sa.Side.ToString()),
                        ["status"] = sa.StatusOf(),
                        ["embedded"] = embedded,
                        ["params_applied"] = applied,
                        ["params"] = DumpSubassemblyParams(sa)
                    });
                }
                report["subassemblies"] = subs;
                report["handle"] = asm.Handle.ToString();
                tr.Commit();
            }
            return report;
        }

        /// <summary>Set one subassembly parameter by (display) name across the double/long/bool/string collections.</summary>
        static bool SetSubassemblyParam(CivSubassembly sa, string paramName, JsonNode value)
        {
            foreach (object coll in new object[] { sa.ParamsDouble, sa.ParamsLong, sa.ParamsBool, sa.ParamsString })
            {
                if (coll == null) continue;
                foreach (object p in ToObjectList(coll))
                {
                    string n = ReadProperty(p, "DisplayName") as string;
                    if (string.IsNullOrEmpty(n)) n = ReadProperty(p, "Name") as string;
                    if (!string.Equals(n, paramName, StringComparison.OrdinalIgnoreCase)) continue;
                    System.Reflection.PropertyInfo prop = p.GetType().GetProperty("Value");
                    if (prop == null || !prop.CanWrite) return false;
                    object converted;
                    try { converted = Convert.ChangeType(value.ToString().Trim('"'), prop.PropertyType, System.Globalization.CultureInfo.InvariantCulture); }
                    catch { converted = value.ToString(); }
                    prop.SetValue(p, converted);
                    return true;
                }
            }
            return false;
        }

        static JsonObject DumpSubassemblyParams(CivSubassembly sa)
        {
            var o = new JsonObject();
            foreach (object coll in new object[] { sa.ParamsDouble, sa.ParamsLong, sa.ParamsBool, sa.ParamsString })
            {
                if (coll == null) continue;
                foreach (object p in ToObjectList(coll))
                {
                    string n = ReadProperty(p, "DisplayName") as string;
                    if (string.IsNullOrEmpty(n)) n = ReadProperty(p, "Name") as string;
                    if (string.IsNullOrEmpty(n) || o.ContainsKey(n)) continue;
                    object v = ReadProperty(p, "Value");
                    o[n] = v == null ? null : JsonValue.Create(v.ToString());
                }
            }
            return o;
        }
    }
}
