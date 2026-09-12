using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivAssembly = Autodesk.Civil.DatabaseServices.Assembly;
using CivCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using CivSampleLineGroup = Autodesk.Civil.DatabaseServices.SampleLineGroup;
using CivSectionSource = Autodesk.Civil.DatabaseServices.SectionSource;
using CivSubassembly = Autodesk.Civil.DatabaseServices.Subassembly;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DFactory
{
    /// <summary>
    /// CLI-facing ops: the op catalog behind `civil3dfactory.ps1 -Help` and the L1 `view` modes
    /// (outline / stats / issues / screenshot). Both are generated at run time from the registry and
    /// nodes/node.json, so there is no catalog file to keep in sync.
    /// </summary>
    public static partial class Ops
    {
        static Ops()
        {
            Registry["help"] = new OpDef
            {
                Description = "Op catalog: every registered op with description, parameter summary, write flag and its nodes/node.json contract (what -Help prints)",
                Parameters = "op?(only this op)",
                WritesDrawing = false,
                Run = HelpOp
            };
            Registry["view"] = new OpDef
            {
                Description = "L1 read-only overview of the drawing. outline = what Civil objects exist; stats = entity counts; "
                            + "issues = broken links and empty results (profiles, corridors, PKT status, sample lines, material lists); "
                            + "screenshot = PNG of the model space (or a layout) rendered by the Civil 3D kernel",
                Parameters = "mode(outline|stats|issues|screenshot, default outline) out?(png path for screenshot, default next to the drawing) "
                           + "layout?(screenshot only, default Model) width?(screenshot pixels: 1600|1024|800|640, default 1600) window?{minx,miny,maxx,maxy}(screenshot only) iso?(default false; south-west isometric 3D view)",
                WritesDrawing = false,
                Run = ViewOp
            };
            RegisterStyleOps();   // Ops.Styles.cs (only one static constructor is allowed across the partial class)
        }

        // ------------------------------------------------------------------
        // help
        // ------------------------------------------------------------------

        /// <summary>Catalog object: { engine_version, ops:[{op, desc, params, writes_dwg, contract?}] }.</summary>
        internal static JsonObject BuildCatalog(string onlyOp)
        {
            Dictionary<string, JsonObject> contracts = LoadContracts();
            var arr = new JsonArray();
            foreach (KeyValuePair<string, OpDef> kv in Registry)
            {
                if (!string.IsNullOrEmpty(onlyOp) && !string.Equals(kv.Key, onlyOp, StringComparison.OrdinalIgnoreCase)) continue;
                var o = new JsonObject
                {
                    ["op"] = kv.Key,
                    ["desc"] = kv.Value.Description,
                    ["params"] = kv.Value.Parameters,
                    ["writes_dwg"] = kv.Value.WritesDrawing
                };
                JsonObject c;
                if (contracts.TryGetValue(kv.Key, out c)) o["contract"] = c;
                arr.Add(o);
            }
            string version = "unknown";
            try
            {
                var attr = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    typeof(Ops).Assembly, typeof(AssemblyInformationalVersionAttribute));
                if (attr != null) version = attr.InformationalVersion;
                else version = typeof(Ops).Assembly.GetName().Version.ToString();
            }
            catch { }
            return new JsonObject
            {
                ["engine_version"] = version,
                ["op_count"] = arr.Count,
                ["ops"] = arr
            };
        }

        /// <summary>id -> {name, kind, effect, inputs, parameters, outputs} from nodes/node.json; empty when the repo root is unknown.</summary>
        static Dictionary<string, JsonObject> LoadContracts()
        {
            var map = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string root = FactoryPaths.Root;
                if (string.IsNullOrEmpty(root)) return map;
                string path = Path.Combine(root, "nodes", "node.json");
                if (!File.Exists(path)) return map;
                var arr = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonArray;
                if (arr == null) return map;
                foreach (JsonNode n in arr)
                {
                    var o = n as JsonObject;
                    if (o == null || o["id"] == null) continue;
                    var c = new JsonObject();
                    foreach (string key in new[] { "name", "kind", "effect", "inputs", "parameters", "outputs" })
                        if (o[key] != null) c[key] = o[key].DeepClone();
                    map[o["id"].GetValue<string>()] = c;
                }
            }
            catch { }
            return map;
        }

        static JsonNode HelpOp(JsonObject a, Document doc)
        {
            return BuildCatalog(GetString(a, "op", null));
        }

        // ------------------------------------------------------------------
        // view
        // ------------------------------------------------------------------

        static JsonNode ViewOp(JsonObject a, Document doc)
        {
            string mode = (GetString(a, "mode", "outline") ?? "outline").Trim().ToLowerInvariant();
            switch (mode)
            {
                case "outline": return ViewOutline(doc);
                case "stats": return ViewStats(doc);
                case "issues": return ViewIssues(doc);
                case "screenshot": return ViewScreenshot(a, doc);
                default:
                    throw new InvalidOperationException("Unknown view mode '" + mode + "'. Use outline, stats, issues or screenshot.");
            }
        }

        /// <summary>Run a registered read-only op and return its data, or an {error} object; never throws.</summary>
        static JsonNode Part(string op, JsonObject args, Document doc)
        {
            try { return Execute(op, args ?? new JsonObject(), doc); }
            catch (System.Exception ex)
            {
                return new JsonObject { ["error"] = ex.Message, ["type"] = ex.GetType().Name };
            }
        }

        static JsonNode ViewOutline(Document doc)
        {
            var o = new JsonObject();
            o["drawing"] = Part("drawing_info", null, doc);
            o["surfaces"] = Part("list_surfaces", null, doc);
            o["alignments"] = Part("list_alignments", null, doc);
            o["profiles"] = Part("list_profiles", null, doc);
            o["corridors"] = CorridorOutline(doc);
            o["assemblies"] = AssemblyOutline(doc);
            o["sample_line_groups"] = SampleLineGroupOutline(doc);
            o["civil_views"] = Part("list_civil_views", new JsonObject { ["max"] = 200 }, doc);
            o["blocks"] = Part("list_blocks", null, doc);
            o["layers"] = Part("list_layers", new JsonObject { ["used_only"] = true }, doc);
            return o;
        }

        static JsonNode ViewStats(Document doc)
        {
            var o = new JsonObject();
            o["drawing"] = Part("drawing_info", null, doc);
            o["by_type"] = Part("entity_stats", new JsonObject { ["by"] = "type", ["top"] = 40 }, doc);
            o["by_layer"] = Part("entity_stats", new JsonObject { ["by"] = "layer", ["top"] = 40 }, doc);
            o["surfaces"] = Part("surface_stats", null, doc);
            o["corridors"] = Part("corridor_stats", null, doc);
            return o;
        }

        static JsonArray CorridorOutline(Document doc)
        {
            var arr = new JsonArray();
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var c = tr.GetObject(id, OpenMode.ForRead) as CivCorridor;
                    if (c == null) continue;
                    var o = new JsonObject { ["name"] = c.Name, ["handle"] = c.Handle.ToString() };
                    try { o["out_of_date"] = c.IsOutOfDate; } catch { }
                    var bls = new JsonArray();
                    try
                    {
                        foreach (Autodesk.Civil.DatabaseServices.Baseline b in c.Baselines)
                        {
                            var regions = new JsonArray();
                            try
                            {
                                foreach (Autodesk.Civil.DatabaseServices.BaselineRegion r in b.BaselineRegions)
                                {
                                    string asm = null;
                                    try { asm = ((CivAssembly)tr.GetObject(r.AssemblyId, OpenMode.ForRead)).Name; } catch { }
                                    regions.Add(new JsonObject
                                    {
                                        ["name"] = r.Name,
                                        ["assembly"] = asm,
                                        ["start"] = Math.Round(r.StartStation, 3),
                                        ["end"] = Math.Round(r.EndStation, 3)
                                    });
                                }
                            }
                            catch (System.Exception ex) { regions.Add(new JsonObject { ["error"] = ex.Message }); }
                            string alName = null;
                            try { alName = ((CivAlignment)tr.GetObject(b.AlignmentId, OpenMode.ForRead)).Name; } catch { }
                            bls.Add(new JsonObject { ["name"] = b.Name, ["alignment"] = alName, ["regions"] = regions });
                        }
                    }
                    catch (System.Exception ex) { o["baseline_error"] = ex.Message; }
                    o["baselines"] = bls;
                    var surfs = new JsonArray();
                    try { foreach (Autodesk.Civil.DatabaseServices.CorridorSurface cs in c.CorridorSurfaces) surfs.Add(cs.Name); } catch { }
                    o["corridor_surfaces"] = surfs;
                    arr.Add(o);
                }
                tr.Commit();
            }
            return arr;
        }

        static JsonArray AssemblyOutline(Document doc)
        {
            var arr = new JsonArray();
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (CivAssembly asm in FindAllAssemblies(db, tr))
                {
                    var subs = new JsonArray();
                    try
                    {
                        foreach (ObjectId sid in AllSubassemblyIds(asm))
                        {
                            var sa = tr.GetObject(sid, OpenMode.ForRead) as CivSubassembly;
                            if (sa == null) continue;
                            var so = new JsonObject { ["name"] = sa.Name };
                            try { so["side"] = sa.Side.ToString(); } catch { }
                            try { so["status"] = sa.StatusOf(); } catch { }
                            try { so["embedded_pkt"] = sa.UseEmbeddedProject; } catch { }
                            subs.Add(so);
                        }
                    }
                    catch (System.Exception ex) { subs.Add(new JsonObject { ["error"] = ex.Message }); }
                    arr.Add(new JsonObject { ["name"] = asm.Name, ["handle"] = asm.Handle.ToString(), ["subassemblies"] = subs });
                }
                tr.Commit();
            }
            return arr;
        }

        static List<ObjectId> AllSubassemblyIds(CivAssembly asm)
        {
            var ids = new List<ObjectId>();
            foreach (Autodesk.Civil.DatabaseServices.AssemblyGroup group in asm.Groups)
                foreach (ObjectId id in group.GetSubassemblyIds()) ids.Add(id);
            return ids;
        }

        static JsonArray SampleLineGroupOutline(Document doc)
        {
            var arr = new JsonArray();
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var al = tr.GetObject(id, OpenMode.ForRead) as CivAlignment;
                    if (al == null) continue;
                    ObjectIdCollection slgIds;
                    try { slgIds = al.GetSampleLineGroupIds(); } catch { continue; }
                    foreach (ObjectId gid in slgIds)
                    {
                        var slg = tr.GetObject(gid, OpenMode.ForWrite) as CivSampleLineGroup;   // GetSectionSources / MaterialLists need a write-open group (read-open crashes accoreconsole with eNotOpenForWrite); nothing is changed
                        if (slg == null) continue;
                        var o = new JsonObject { ["name"] = slg.Name, ["alignment"] = al.Name };
                        try { o["sample_lines"] = slg.GetSampleLineIds().Count; } catch { }
                        var sources = new JsonArray();
                        try
                        {
                            foreach (CivSectionSource src in slg.GetSectionSources())
                            {
                                string t = ""; try { t = src.SourceType.ToString(); } catch { }
                                sources.Add(new JsonObject { ["name"] = src.SourceNameOf(), ["type"] = t, ["sampled"] = src.IsSampled });
                            }
                        }
                        catch { }
                        o["sources"] = sources;
                        try { o["material_lists"] = slg.MaterialLists.Count; } catch { }
                        arr.Add(o);
                    }
                }
                tr.Commit();
            }
            return arr;
        }

        // ---- issues -------------------------------------------------------

        static void Issue(JsonArray list, string level, string code, string obj, string message)
        {
            list.Add(new JsonObject { ["level"] = level, ["code"] = code, ["object"] = obj, ["message"] = message });
        }

        static JsonNode ViewIssues(Document doc)
        {
            var issues = new JsonArray();
            Database db = doc.Database;

            // 1. alignments and profiles
            var profiles = Part("list_profiles", null, doc) as JsonObject;
            if (profiles != null && profiles["data"] is JsonArray)
            {
                foreach (JsonNode n in (JsonArray)profiles["data"])
                {
                    var al = n as JsonObject; if (al == null) continue;
                    string name = al["alignment"] != null ? al["alignment"].ToString() : "?";
                    bool ground = al["has_ground"] != null && al["has_ground"].GetValue<bool>();
                    bool design = al["has_design"] != null && al["has_design"].GetValue<bool>();
                    if (!ground) Issue(issues, "warn", "alignment_no_ground_profile", name, "No surface (existing ground) profile: create_profiles first.");
                    if (!design) Issue(issues, "warn", "alignment_no_design_profile", name, "No design (layout) profile: a corridor needs one.");
                }
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // 2. assemblies / PKT status
                foreach (CivAssembly asm in FindAllAssemblies(db, tr))
                {
                    int count = 0;
                    try
                    {
                        foreach (ObjectId sid in AllSubassemblyIds(asm))
                        {
                            count++;
                            var sa = tr.GetObject(sid, OpenMode.ForRead) as CivSubassembly;
                            if (sa == null) continue;
                            string status = ""; try { status = sa.StatusOf(); } catch { }
                            if (status.Length > 0 && !string.Equals(status, "UpToDate", StringComparison.OrdinalIgnoreCase))
                                Issue(issues, "error", "subassembly_status", asm.Name + " / " + sa.Name,
                                    "Subassembly status is " + status + " (PKT not embedded or file missing): the corridor will build an empty shell. Re-import the PKT (check_sac_paths) or embed it.");
                        }
                    }
                    catch (System.Exception ex) { Issue(issues, "warn", "assembly_unreadable", asm.Name, ex.Message); }
                    if (count == 0) Issue(issues, "warn", "assembly_empty", asm.Name, "Assembly has no subassemblies.");
                }

                // 3. corridors
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var c = tr.GetObject(id, OpenMode.ForRead) as CivCorridor;
                    if (c == null) continue;
                    try { if (c.IsOutOfDate) Issue(issues, "warn", "corridor_out_of_date", c.Name, "Corridor is out of date: rebuild_corridor."); } catch { }
                    int baselines = 0;
                    try
                    {
                        foreach (Autodesk.Civil.DatabaseServices.Baseline b in c.Baselines)
                        {
                            baselines++;
                            int regions = 0;
                            try
                            {
                                foreach (Autodesk.Civil.DatabaseServices.BaselineRegion r in b.BaselineRegions)
                                {
                                    regions++;
                                    if (r.AssemblyId.IsNull)
                                        Issue(issues, "error", "region_no_assembly", c.Name + " / " + b.Name + " / " + r.Name, "Region has no assembly.");
                                    if (r.EndStation - r.StartStation < 1e-6)
                                        Issue(issues, "warn", "region_zero_length", c.Name + " / " + b.Name + " / " + r.Name, "Region has zero length.");
                                }
                            }
                            catch (System.Exception ex) { Issue(issues, "warn", "baseline_unreadable", c.Name + " / " + b.Name, ex.Message); }
                            if (regions == 0) Issue(issues, "error", "baseline_no_regions", c.Name + " / " + b.Name, "Baseline has no regions: create_corridor_regions.");
                            try { if (b.ProfileId.IsNull) Issue(issues, "error", "baseline_no_profile", c.Name + " / " + b.Name, "Baseline profile reference is empty: attach_baseline_profile."); } catch { }
                        }
                    }
                    catch (System.Exception ex) { Issue(issues, "warn", "corridor_unreadable", c.Name, ex.Message); }
                    if (baselines == 0) Issue(issues, "error", "corridor_no_baselines", c.Name, "Corridor has no baselines.");
                    int surfaces = 0;
                    try
                    {
                        foreach (Autodesk.Civil.DatabaseServices.CorridorSurface cs in c.CorridorSurfaces)
                        {
                            surfaces++;
                            try
                            {
                                var s = tr.GetObject(cs.SurfaceId, OpenMode.ForRead) as CivSurface;
                                if (s == null) { Issue(issues, "error", "corridor_surface_missing", c.Name + " / " + cs.Name, "Corridor surface object is gone."); continue; }
                                var gp = s.GetGeneralProperties();
                                if (gp.MaximumElevation - gp.MinimumElevation < 1e-9 && gp.NumberOfPoints == 0)
                                    Issue(issues, "error", "corridor_surface_empty", c.Name + " / " + cs.Name, "Corridor surface has no points: the assembly produced no links (check PKT status and targets).");
                            }
                            catch (System.Exception ex) { Issue(issues, "warn", "corridor_surface_unreadable", c.Name + " / " + cs.Name, ex.Message); }
                        }
                    }
                    catch { }
                    if (surfaces == 0) Issue(issues, "info", "corridor_no_surface", c.Name, "Corridor has no corridor surface: create_corridor_surface before sampling.");
                }

                // 4. sample line groups and material lists
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var al = tr.GetObject(id, OpenMode.ForRead) as CivAlignment;
                    if (al == null) continue;
                    ObjectIdCollection slgIds;
                    try { slgIds = al.GetSampleLineGroupIds(); } catch { continue; }
                    foreach (ObjectId gid in slgIds)
                    {
                        var slg = tr.GetObject(gid, OpenMode.ForWrite) as CivSampleLineGroup;   // GetSectionSources / MaterialLists need a write-open group (read-open crashes accoreconsole with eNotOpenForWrite); nothing is changed
                        if (slg == null) continue;
                        string obj = al.Name + " / " + slg.Name;
                        int lines = 0; try { lines = slg.GetSampleLineIds().Count; } catch { }
                        if (lines == 0) Issue(issues, "error", "slg_empty", obj, "Sample line group has no sample lines.");
                        bool corridorSampled = false, surfaceSampled = false;
                        try
                        {
                            foreach (CivSectionSource src in slg.GetSectionSources())
                            {
                                string t = ""; try { t = src.SourceType.ToString(); } catch { }
                                if (!src.IsSampled) continue;
                                if (t.IndexOf("Corridor", StringComparison.OrdinalIgnoreCase) >= 0) corridorSampled = true;
                                else surfaceSampled = true;
                            }
                        }
                        catch { }
                        if (!corridorSampled) Issue(issues, "error", "slg_no_corridor_source", obj, "No corridor / corridor surface is sampled: sections will only show ground and quantities will be zero.");
                        if (!surfaceSampled) Issue(issues, "warn", "slg_no_surface_source", obj, "No terrain surface is sampled.");
                        int lists = 0; try { lists = slg.MaterialLists.Count; } catch { }
                        if (lists == 0) Issue(issues, "info", "slg_no_material_list", obj, "No material list: compute_quantities has not run for this group.");
                    }
                }
                tr.Commit();
            }

            int errors = 0, warns = 0;
            foreach (JsonNode n in issues)
            {
                string lv = n["level"].ToString();
                if (lv == "error") errors++; else if (lv == "warn") warns++;
            }
            return new JsonObject
            {
                ["errors"] = errors,
                ["warnings"] = warns,
                ["issues"] = issues,
                ["healthy"] = errors == 0
            };
        }

        // ---- screenshot ---------------------------------------------------

        static JsonNode ViewScreenshot(JsonObject a, Document doc)
        {
            string outPng = GetString(a, "out", null);
            if (string.IsNullOrEmpty(outPng))
            {
                string dwg = SafeFile(doc.Database);
                string dir = Path.GetDirectoryName(dwg);
                if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
                outPng = Path.Combine(dir, Path.GetFileNameWithoutExtension(dwg) + ".view.png");
            }
            string width = GetString(a, "width", "1600");
            var args = new JsonObject
            {
                ["out"] = outPng,
                ["device"] = "PublishToWeb PNG.pc3",
                ["paper"] = width,
                ["ctb"] = "",
                ["lineweights"] = false,
                ["overwrite"] = true,
                ["layout"] = GetString(a, "layout", "Model"),
                ["min_bytes"] = 256
            };
            if (a["window"] != null) args["window"] = a["window"].DeepClone();
            if (GetBool(a, "iso", false))
            {
                // South-west isometric view for a 3D impression (corridors, surfaces); plot_pdf projects the window through the current view.
                using (ViewTableRecord v = doc.Editor.GetCurrentView())
                {
                    v.ViewDirection = new Autodesk.AutoCAD.Geometry.Vector3d(-1, -1, 1);
                    doc.Editor.SetCurrentView(v);
                }
            }
            JsonNode r = Execute("plot_pdf", args, doc);
            var o = r as JsonObject ?? new JsonObject();
            o["png"] = outPng;
            return o;
        }
    }
}
