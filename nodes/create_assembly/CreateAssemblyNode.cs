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
using CivPoint = Autodesk.Civil.DatabaseServices.Point;

namespace Civil3DFactory
{
    /// <summary>
    /// create_assembly: build an assembly from Subassembly Composer .pkt files and/or Civil 3D stock subassemblies,
    /// embed the PKT projects in the drawing so the assembly keeps working when the files move.
    /// Two forms:
    ///   left_pkt / right_pkt          - a LEFT/RIGHT pair hooked to the baseline (the original form)
    ///   items: [ {...}, ... ]         - any number of pieces in order; each is a PKT ({pkt}) or a stock subassembly
    ///                                   ({stock: "Subassembly.MarkPoint"}), with its own params and an optional attach point
    ///                                   ({attach: {to: "<piece name>", point_code: "toe-left"}}) on a piece added earlier.
    /// This is the "PKT -> assembly in the drawing" step of the pkt skill; corridors are built from it.
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeCreateAssembly(JsonObject a, Document doc)
        {
            string name = Need(a, "name");
            bool embed = GetBool(a, "embed", true);
            bool replace = GetBool(a, "replace", true);
            var sharedParams = a["params"] as JsonObject;
            double ox = GetDouble(a, "x", 0), oy = GetDouble(a, "y", 0);

            // Normalise both forms into one ordered item list
            var items = new List<JsonObject>();
            var itemsArr = a["items"] as JsonArray;
            if (itemsArr != null && itemsArr.Count > 0)
            {
                foreach (JsonNode n in itemsArr) if (n is JsonObject o) items.Add(o);
            }
            else
            {
                string leftPkt = Need(a, "left_pkt");
                string rightPkt = Need(a, "right_pkt");
                items.Add(new JsonObject { ["name"] = name + "_LEFT", ["pkt"] = leftPkt });
                items.Add(new JsonObject { ["name"] = name + "_RIGHT", ["pkt"] = rightPkt });
            }
            for (int i = 0; i < items.Count; i++)
            {
                JsonObject it = items[i];
                string pkt = GetString(it, "pkt", null);
                string stock = GetString(it, "stock", null);
                if (string.IsNullOrEmpty(pkt) == string.IsNullOrEmpty(stock))
                    throw new InvalidOperationException("items[" + i + "]: give exactly one of pkt (a .pkt path) or stock (a stock class such as Subassembly.LinkToMarkedPoint).");
                if (pkt != null)
                {
                    if (!Path.IsPathRooted(pkt)) throw new InvalidOperationException("PKT path must be absolute: " + pkt);
                    if (!File.Exists(pkt)) throw new InvalidOperationException("PKT file not found: " + pkt);
                }
                if (GetString(it, "name", null) == null)
                    it["name"] = pkt != null ? name + "_" + Path.GetFileNameWithoutExtension(pkt) : name + "_" + stock.Substring(stock.LastIndexOf('.') + 1);
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
                var byName = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonObject it in items)
                {
                    string saName = GetString(it, "name", null);
                    string pkt = GetString(it, "pkt", null);
                    string stock = GetString(it, "stock", null);
                    ObjectId saId = pkt != null
                        ? civ.SubassemblyCollection.ImportSac(saName, pkt, origin)
                        : civ.SubassemblyCollection.ImportStockSubassembly(saName, stock, origin);
                    var sa = (CivSubassembly)tr.GetObject(saId, OpenMode.ForWrite);

                    int applied = 0;
                    var notApplied = new JsonArray();
                    foreach (JsonObject pv in new[] { sharedParams, it["params"] as JsonObject })
                    {
                        if (pv == null) continue;
                        foreach (KeyValuePair<string, JsonNode> kv in pv)
                        {
                            if (kv.Value == null) continue;
                            if (SetSubassemblyParam(sa, kv.Key, kv.Value, stock)) applied++;
                            else if (pv != sharedParams) notApplied.Add(kv.Key);
                        }
                    }

                    // Hook: baseline (default) or a point of a piece added earlier, found by point code or index
                    var attach = it["attach"] as JsonObject;
                    string attachedTo = null;
                    if (attach != null)
                    {
                        string hostName = Need(attach, "to");
                        if (!byName.TryGetValue(hostName, out ObjectId hostId))
                            throw new InvalidOperationException("attach.to '" + hostName + "' is not a piece added before '" + saName + "'.");
                        var host = (CivSubassembly)tr.GetObject(hostId, OpenMode.ForRead);
                        CivPoint hook = FindHookPoint(host, GetString(attach, "point_code", null), (int)GetDouble(attach, "point_index", -1));
                        if (hook == null)
                            throw new InvalidOperationException("No point on '" + hostName + "' matches attach " + attach.ToJsonString() + "; its point codes: " + string.Join(" | ", PointCodeList(host)));
                        asm.AddSubassembly(saId, hook);
                        attachedTo = hostName + " @ " + string.Join(",", hook.Codes);
                    }
                    else asm.AddSubassembly(saId);
                    byName[saName] = saId;

                    bool embedded = false;
                    if (embed && pkt != null)
                    {
                        try { sa.UseEmbeddedProject = true; embedded = sa.UseEmbeddedProject; }
                        catch (System.Exception ex) { report["embed_error"] = ex.Message; }
                    }
                    var entry = new JsonObject
                    {
                        ["name"] = sa.Name,
                        ["side"] = SafeStr(() => sa.Side.ToString()),
                        ["status"] = sa.StatusOf(),
                        ["params_applied"] = applied,
                        ["params"] = DumpSubassemblyParams(sa)
                    };
                    if (pkt != null) { entry["pkt"] = pkt; entry["embedded"] = embedded; }
                    else entry["stock"] = stock;
                    if (attachedTo != null) entry["attached_to"] = attachedTo;
                    if (notApplied.Count > 0) entry["params_not_found"] = notApplied;
                    subs.Add(entry);
                }
                report["subassemblies"] = subs;
                report["handle"] = asm.Handle.ToString();
                tr.Commit();
            }
            return report;
        }

        /// <summary>The point of a subassembly to hook the next piece to: by code (case-insensitive) or by index.</summary>
        static CivPoint FindHookPoint(CivSubassembly host, string code, int index)
        {
            var pts = host.Points;
            if (pts == null) return null;
            int i = 0;
            CivPoint last = null;
            foreach (CivPoint p in pts)
            {
                if (index >= 0 && i == index) return p;
                if (code != null)
                    foreach (string c in p.Codes)
                        if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) return p;
                last = p; i++;
            }
            return code == null && index < 0 ? last : null;   // no selector: the last point of the piece
        }

        static List<string> PointCodeList(CivSubassembly host)
        {
            var list = new List<string>();
            try { foreach (CivPoint p in host.Points) list.Add(string.Join(",", p.Codes)); } catch { }
            return list;
        }

        /// <summary>
        /// Stock (.NET) subassemblies expose their parameters under resource ids ("4205") instead of names; the tool catalogs
        /// (%ProgramData%\Autodesk\C3D &lt;year&gt;\enu\Tool Catalogs\Road Catalog\*.atc) map class + parameter name to that id.
        /// Scanned once; a small built-in table covers the two classes used for closing channel bottoms when no catalog is found.
        /// </summary>
        static Dictionary<string, Dictionary<string, string>> _stockParamIds;
        static Dictionary<string, string> StockParamIds(string className)
        {
            if (_stockParamIds == null)
            {
                var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Subassembly.MarkPoint"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PointName"] = "4205", ["PointCodes"] = "4207" },
                    ["Subassembly.LinkToMarkedPoint"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["MarkedPointName"] = "3905", ["SurfaceCodes"] = "3907", ["OmitLink"] = "3909" }
                };
                try
                {
                    string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                    foreach (string dir in Directory.GetDirectories(Path.Combine(pd, "Autodesk"), "C3D *"))
                    {
                        string cat = Path.Combine(dir, "enu", "Tool Catalogs", "Road Catalog");
                        if (!Directory.Exists(cat)) continue;
                        foreach (string atc in Directory.GetFiles(cat, "*.atc"))
                        {
                            string text = File.ReadAllText(atc);
                            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text,
                                @"<DotNetClass[^>]*>(?<cls>[\w.]+)</DotNetClass>.*?<Params>(?<body>.*?)</Params>", System.Text.RegularExpressions.RegexOptions.Singleline))
                            {
                                string cls = m.Groups["cls"].Value;
                                if (!map.TryGetValue(cls, out Dictionary<string, string> d)) map[cls] = d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                foreach (System.Text.RegularExpressions.Match pm in System.Text.RegularExpressions.Regex.Matches(m.Groups["body"].Value, @"<(?<name>\w+) DataType=""\w+"" DisplayName=""(?<id>\d+)"""))
                                    d[pm.Groups["name"].Value] = pm.Groups["id"].Value;
                            }
                        }
                    }
                }
                catch { }
                _stockParamIds = map;
            }
            return _stockParamIds.TryGetValue(className ?? "", out Dictionary<string, string> found) ? found : null;
        }

        /// <summary>Set one subassembly parameter by name (the code name, the display name, or the catalog resource id of a stock class) across the double/long/bool/string collections.</summary>
        static bool SetSubassemblyParam(CivSubassembly sa, string paramName, JsonNode value, string stockClass = null)
        {
            string alias = null;
            if (stockClass != null)
            {
                Dictionary<string, string> ids = StockParamIds(stockClass);
                if (ids != null && ids.TryGetValue(paramName, out string id)) alias = id;
            }
            foreach (object coll in new object[] { sa.ParamsDouble, sa.ParamsLong, sa.ParamsBool, sa.ParamsString })
            {
                if (coll == null) continue;
                foreach (object p in ToObjectList(coll))
                {
                    string n = ReadProperty(p, "Name") as string;
                    string dn = ReadProperty(p, "DisplayName") as string;
                    if (!string.Equals(n, paramName, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(dn, paramName, StringComparison.OrdinalIgnoreCase) &&
                        !(alias != null && (n == alias || dn == alias))) continue;
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
                    string n = ReadProperty(p, "Name") as string;
                    if (string.IsNullOrEmpty(n)) n = ReadProperty(p, "DisplayName") as string;
                    if (string.IsNullOrEmpty(n) || o.ContainsKey(n)) continue;
                    object v = ReadProperty(p, "Value");
                    o[n] = v == null ? null : JsonValue.Create(v.ToString());
                }
            }
            return o;
        }
    }
}
