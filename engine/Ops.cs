using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using AcDbPlotType = Autodesk.AutoCAD.DatabaseServices.PlotType;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;
using CivApp = Autodesk.Civil.ApplicationServices.CivilApplication;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    public class OpDef
    {
        public string Description;
        public string Parameters;
        public bool WritesDrawing;              // true = modifies the dwg (all false in v1)
        public Func<JsonObject, Document, JsonNode> Run;
    }

    /// <summary>
    /// Operation registry: adding an operation = adding one entry here; it stays available forever (this is how know-how "settles").
    /// Note: never touch CivilApplication.ActiveDocument (unavailable in accoreconsole);
    ///       Civil 3D objects are always obtained by iterating the database ModelSpace.
    /// </summary>
    public static partial class Ops
    {
        public static readonly Dictionary<string, OpDef> Registry =
            new Dictionary<string, OpDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["drawing_info"] = new OpDef
            {
                Description = "Drawing overview: alignment/surface/layer counts, units, file path",
                Parameters = "(none)",
                WritesDrawing = false,
                Run = DrawingInfo
            },
            ["list_alignments"] = new OpDef
            {
                Description = "List all alignments: name, layer, length, start/end station",
                Parameters = "(none)",
                WritesDrawing = false,
                Run = ListAlignments
            },
            ["list_surfaces"] = new OpDef
            {
                Description = "List all surfaces: name, type, layer, elevation range",
                Parameters = "(none)",
                WritesDrawing = false,
                Run = ListSurfaces
            },
            ["export_stations"] = new OpDef
            {
                Description = "Export plan coordinates of an alignment at each station by interval (xlsx/csv)",
                Parameters = "name?(alignment name) names?[] all?(bool, default all) interval?(default 50) outdir? format?(xlsx|csv|both, default both)",
                WritesDrawing = false,
                Run = ExportStations
            },
            ["station_elevations"] = new OpDef
            {
                Description = "Sample ground elevation of a surface along an alignment by interval (xlsx/csv); null outside the surface",
                Parameters = "alignment(required) surface(required) interval?(default 50) outdir? format?",
                WritesDrawing = false,
                Run = StationElevations
            },
            ["export_surface_grid"] = new OpDef
            {
                Description = "Sample a surface on a grid and export a CSV point cloud (x,y,z; read-only; points outside the surface are skipped). "
                            + "For 3D visualisation/rendering terrain; uses FindElevationAtXY per point, so surfaces with broken styles still export "
                            + "(headless must not iterate Vertices/Triangles, it hard-crashes; see the note in surface_stats)",
                Parameters = "surface(required, surface name) out(required, absolute csv path) minx miny maxx maxy(required, sample extent) "
                           + "step?(default 10) overwrite?(default false)",
                WritesDrawing = false,
                Run = ExportSurfaceGrid
            },
            ["create_dwg"] = new OpDef
            {
                Description = "Create a new DWG file and draw entities into it (circles/lines/arcs/polylines/texts) without touching the open drawing",
                Parameters = "path(required, absolute path) layers?[{name,color}] circles?[{x,y,r,layer}] lines?[{x1,y1,x2,y2,layer}] "
                           + "arcs?[{x,y,r,start_angle,end_angle,layer}(degrees)] polylines?[{points:[[x,y]..],closed,width,layer}] "
                           + "texts?[{x,y,text,height,rotation,layer,style?,width_factor?,color?}] layer?(default layer, default \"0\") overwrite?(default false)",
                WritesDrawing = true,   // produces a new dwg file (does not modify the drawing passed with /i)
                Run = CreateDwg
            },
            ["list_blocks"] = new OpDef
            {
                Description = "List block definitions in the drawing (name, has attributes, attribute tags, insert count)",
                Parameters = "include_layout?(default false, do not list *Model_Space and other system blocks)",
                WritesDrawing = false,
                Run = ListBlocks
            },
            ["block_refs"] = new OpDef
            {
                Description = "List block references: block name, space, layer, insertion point, rotation, scale, bounding box (use it to locate title blocks)",
                Parameters = "name?(block-name substring filter, e.g. \"TitleBlock\") space?(model|layout|all, default all) max?(default 200)",
                WritesDrawing = false,
                Run = BlockRefs
            },
            ["dump_block_attributes"] = new OpDef
            {
                Description = "Export block reference attributes to JSON (use it for title blocks): per reference reports handle, block name, space and all tag->value pairs",
                Parameters = "name?(block-name substring filter, e.g. \"TitleBlock\") space?(model|layout|all, default all) max?(default 500) "
                           + "with_attributes_only?(default true, skip blocks without attributes) out?(also write the result as JSON to this absolute path)",
                WritesDrawing = false,
                Run = DumpBlockAttributes
            },
            ["set_block_attributes"] = new OpDef
            {
                Description = "Write block attributes back from JSON: exact per-handle fill, or batch-edit the same tags by block name (use it to change title-block date/sheet number/notes)",
                Parameters = "from?(absolute path of a JSON exported by dump_block_attributes and edited) items?[{handle,attributes:{tag:value}}] "
                           + "name?(block-name substring, batch edit together with attributes) attributes?{tag:value} space?(model|layout|all, default all) "
                           + "width_factors?{tag:factor}(squeeze long text into a narrow cell: horizontally compresses that tag's attribute text; can be given alone, "
                           + "runs without changing values; multi-line (MText) attributes ignore width factor and are recorded in mtext_skipped instead of faking success) "
                           + "missing_ok?(default true, a tag missing from the drawing is only recorded, not an error)",
                WritesDrawing = true,
                Run = SetBlockAttributes
            },
            ["model_extents"] = new OpDef
            {
                Description = "Model space overall bounding box + INSBASE (answers \"how big is this drawing/legend and where is the base point when inserting it as a block\")",
                Parameters = "layer?(layer-name substring filter)",
                WritesDrawing = false,
                Run = ModelExtents
            },
            ["list_marked_regions"] = new OpDef
            {
                Description = "List closed-frame candidates in model space with their Note/XData/extension dictionary/hyperlink, to identify user-marked sheet regions",
                Parameters = "marked_only?(default true, only return objects with attached info) layer?(layer-name substring) max?(default 100)",
                WritesDrawing = false,
                Run = ListMarkedRegions
            },
            ["list_civil_views"] = new OpDef
            {
                Description = "List section view / profile view objects in model space with bounding boxes, to check whether they fall inside sheet frames",
                Parameters = "type?(section|profile|all, default all) max?(default 1000)",
                WritesDrawing = false,
                Run = ListCivilViews
            },
            ["modify_dwg"] = new OpDef
            {
                Description = "Modify an existing drawing (draw entities/insert blocks). **Defaults to a preview copy**, never touches the original; apply:true writes back to the original with an automatic backup",
                Parameters = "dwg(required, target drawing) apply?(default false = preview to a copy) backup?(default true, back up before apply) "
                           + "space?(default Model, or a layout name; applies to entities and hatches, blocks have their own per-item space) "
                           + "blocks?[{name,from_dwg?,source_block?,x,y,scale?,rotation?,layer?,space?(layout name),attributes?{tag:value}}] "
                           + "circles?/lines?/arcs?/polylines?/texts?/layers?(same as create_dwg; lines/texts accept color=ACI index, texts also style/width_factor) "
                           + "hatches?[{points:[[x,y],...],pattern?(default SOLID),scale?,angle?,layer?,color?}] "
                           + "xrefs?[{path(required, absolute path),name?,x?,y?,layer?,overlay?}](attach xref + insert a reference into model space; "
                           + "skipped if the same name already exists, so re-runnable; relative xref paths get lost when the plotter moves files, always absolute) "
                           + "out?(preview file path, auto-named by default)",
                WritesDrawing = true,
                Run = ModifyDwg
            },
            ["plot_pdf"] = new OpDef
            {
                Description = "Plot the drawing to PDF (DWG To PDF.pc3). Plots the current drawing's model space extents by default; a window or an external dwg can be given",
                Parameters = "out(required, pdf path) dwg?(plot an external file, default current drawing) layout?(default Model) "
                           + "window?{minx,miny,maxx,maxy}(model space = world coordinates, converted to DCS automatically; paper-space layout = layout paper coordinates in mm, "
                           + "supported since 2026-08-28; use it to cut single A3 pages out of a merged layout with several frames in a row) "
                           + "paper?(default A3) ctb?(default monochrome.ctb, pass \"\" for colour) "
                           + "lineweights?(default true) overwrite?(default false) "
                           + "fit?(default false; paper-space layouts only: scale the layout extents to fill one page, "
                           + "used to get a whole overview layout with several frames on one page. Default false is still Layout type at 1:1, "
                           + "cropped to the paper size)",
                WritesDrawing = false,  // only produces a PDF, drawing untouched
                Run = PlotPdf
            },
            ["normalize_textstyles"] = new OpDef
            {
                Description = "Host external node: normalise the -SimHei/txt1 text styles to standard fonts (mandatory node before plotting), saves a copy without changing the original",
                Parameters = "out(required, save-as path) overwrite? visible? timeout_sec?; actual entry point nodes/normalize_textstyles/run.ps1",
                WritesDrawing = false,
                Run = RunNodeNormalizeTextStyles
            },
            ["plot_attribute_titleblocks"] = new OpDef
            {
                Description = "Host external node: batch-plot model-space and layout-space attribute title blocks with the pinned-version ACC-only EXE",
                Parameters = "output_directory(required); actual entry point nodes/plot_attribute_titleblocks/run.ps1",
                WritesDrawing = false,
                Run = RunNodePlotAttributeTitleblocks
            },
            // ── The whole chain from a single line to quantities (implementation in Ops.Chain.cs) ────────────
            // All of these only change the in-memory drawing; an explicit save_dwg is required to persist.
            ["create_assembly"] = new OpDef
            {
                Description = "Import a LEFT/RIGHT pair of Subassembly Composer .pkt files as a new assembly and embed the PKT projects in the drawing (the assembly then survives the files moving). Existing assembly of the same name is replaced",
                Parameters = "name(required) left_pkt right_pkt(required, absolute .pkt paths) params?{ParamName:value}(applied to both subassemblies, e.g. SearchOffset/SlopeH) "
                           + "embed?(default true) replace?(default true) x? y?(assembly origin, default 0,0)",
                WritesDrawing = true,
                Run = RunNodeCreateAssembly
            },
            ["create_block_definition"] = new OpDef
            {
                Description = "Define or redefine a block from JSON geometry plus attribute definitions, and insert references with attribute values (build a title block without opening the UI)",
                Parameters = "name(required) entities?{lines,polylines,circles,arcs,texts}(same shapes as create_dwg) "
                           + "attributes?[{tag,prompt?,default?,x,y,height?(2.5),rotation?,width_factor?,justify?(left|center|right|middle-left),style?,mtext?(false),width?(mtext width),layer?}] "
                           + "inserts?[{x,y,scale?,rotation?,space?(Model or a layout name),layer?,attributes?{TAG:value}}] "
                           + "layers?[{name,color}] layer?(default 0) base_x? base_y? replace?(default true)",
                WritesDrawing = true,
                Run = RunNodeCreateBlockDefinition
            },
            ["check_sac_paths"] = new OpDef
            {
                Description = "Pipeline pre-check node: verify the two fixed PKTs; if unlinked, replace the SAC subassemblies in place and re-check",
                Parameters = "assembly(required) paths(required, exactly two absolute PKT paths, left and right)",
                WritesDrawing = true,
                Run = RunNodeCheckSacPaths
            },
            ["create_surface_grid"] = new OpDef
            {
                Description = "Build a gridded existing-ground surface (for chain self-tests; real projects use the existing terrain surface)",
                Parameters = "name(required) minx miny maxx maxy(required) step?(20) elev?(0) slope_x? slope_y?(rise per metre) undulation_amp?(0, metres) undulation_len?(200, metres; adds amp*sin(2*pi*x/len)*cos(2*pi*y/len)) style?",
                WritesDrawing = true,
                Run = CreateSurfaceGrid
            },
            ["create_alignment"] = new OpDef
            {
                Description = "Draw a line and define it as an alignment (or convert an existing line in the drawing into an alignment)",
                Parameters = "name(required) points?[[x,y],...](draw a new one) handle?(existing line in the drawing) style? label_set? site? layer? "
                           + "erase_source?(default true) add_curves?(default false)",
                WritesDrawing = true,
                Run = RunNodeCreateAlignment
            },
            ["alignment_to_polyline"] = new OpDef
            {
                Description = "Convert the plan geometry of an alignment into a plain polyline (lines/arcs exact, spirals sampled by step) for pure-CAD output",
                Parameters = "alignment(alignment name) or handle(alignment handle), one of the two; layer?(default \"C3DF-CL\") color_index?(default 3 green) "
                           + "color_bylayer?(default false) linetype?(default CENTER2) linetype_file?(default acadiso.lin) "
                           + "linetype_scale?(default 5) elevation?(default 0) spiral_step?(default 5) erase_source?(default false)",
                WritesDrawing = true,
                Run = RunNodeAlignmentToPolyline
            },
            ["export_alignments_to_dwg"] = new OpDef
            {
                Description = "Extract alignments as plain polylines into a brand-new blank DWG (no Civil objects); offset alignments are grouped under their parent channel via ParentAlignmentId and split left/right",
                Parameters = "out(required, absolute path) overwrite?(default false) alignments?([] export only these) centers_only?(default false, centerlines only) "
                           + "center_layer?(default \"CL-{channel}\") edge_layer?(default \"EDGE-{channel}-{side}\") "
                           + "center_color?(default 3) edge_color?(default 4) center_linetype?(default CENTER2) edge_linetype?(default Continuous) "
                           + "linetype_scale?(default 5) spiral_step?(default 5)",
                WritesDrawing = true,
                Run = RunNodeExportAlignmentsToDwg
            },
            ["export_corridor_feature_lines"] = new OpDef
            {
                Description = "Extract corridor feature lines (by point code) as 3D polylines into a brand-new blank DWG (no Civil objects), keeping elevation per point; point codes follow the PKT code table (daylight/toe/mp/controlpoint... +-left/right)",
                Parameters = "out(required, absolute path) overwrite?(default false) corridors?([] export only these corridors) codes?([] export only these point codes, default all) "
                           + "layer?(default \"FL-{corridor}-{code}\", placeholders {corridor}/{baseline}/{code}) color?(default 2) min_points?(default 2)",
                WritesDrawing = true,
                Run = RunNodeExportCorridorFeatureLines
            },
            ["dump_corridor_params"] = new OpDef
            {
                Description = "Flatten all corridor design parameters: corridor -> baseline -> region -> assembly -> subassembly -> display name and current value of every parameter (read-only)",
                Parameters = "corridors?(default true) assemblies?(default true) assembly?(export only one assembly)",
                WritesDrawing = false,
                Run = RunNodeDumpCorridorParams
            },
            ["list_polylines"] = new OpDef
            {
                Description = "List model-space polylines by layer: handle, length, vertex count, arc-segment count, start/end (to find source lines for replace_alignment_geometry)",
                Parameters = "layer?(exact match, default all) min_length?(default 0) max?(default 200)",
                WritesDrawing = false,
                Run = RunNodeListPolylines
            },
            ["sample_polyline_elevations"] = new OpDef
            {
                Description = "Sample surface elevation along polylines (read-only): every polyline on the layer or in the handle whitelist is sampled vertex by vertex on the given surface; "
                            + "reports min/max/mean/median elevation and the number of points outside the surface; **open polylines are accepted**, "
                            + "which is how it differs from survey_dredge_regions (closed boundaries only, aggregates only). "
                            + "Use it to see how high the existing ground is under a set of design edge lines; for quantities use calculate_surface_volume",
                Parameters = "surface(required, surface name) layer?(give at least one of layer/handles) handles?([] handle whitelist) "
                           + "names?({handle:name}, if given the id is carried into the result) step?(default 0 = vertices only, >0 densifies between vertices) "
                           + "interior_step?(default 0; >0 also samples a grid **inside** closed polylines and outputs z_in_*; the 'existing elevation' of an island/parcel is this, not the edge ring) "
                           + "include_open?(default true, open polylines count too) min_length?(default 0) points?(default false, include per-point [x,y,z] in the result) "
                           + "export_excel?(default false, summary + per-point sheets) outdir? excel_format?(xlsx|csv|both, default xlsx)",
                WritesDrawing = false,
                Run = RunNodeSamplePolylineElevations
            },
            ["replace_alignment_geometry"] = new OpDef
            {
                Description = "Rewrite alignment geometry in place: clear the entity set and rebuild segment by segment from a polyline (all Fixed entities). ObjectId unchanged, so offset alignments/profiles/sample lines stay attached",
                Parameters = "alignment(alignment name) or alignment_handle, one of the two; polyline(required, polyline handle) erase_polyline?(default false)",
                WritesDrawing = true,
                Run = RunNodeReplaceAlignmentGeometry
            },
            ["import_design_lines"] = new OpDef
            {
                Description = "S01: bring the design-line DWG into the current drawing; CL-{channel} becomes an alignment, BOUNDARY-{channel} is kept as the width target source (identity by layer name)",
                Parameters = "dwg(required, absolute path of the design-line drawing) center_prefix?(default \"CL-\") boundary_prefix?(default \"BOUNDARY-\") "
                           + "style? label_set? alignment_layer? add_curves?(default false) replace_existing?(default true) "
                           + "min_boundary_area?(default 100, closed lines smaller than this are dropped as fragments)",
                WritesDrawing = true,
                Run = RunNodeImportDesignLines
            },
            ["create_corridor_regions"] = new OpDef
            {
                Description = "S04: build a multi-region corridor, each region with its own assembly (the existing create_corridor is single-region only); stations are clipped to the alignment's actual range",
                Parameters = "alignment(required) surface(required, existing ground) regions(required, [{assembly,start,end,name?}]) "
                           + "name?(default \"{alignment}_Corridor\") baseline?(default \"{alignment}_Baseline\")",
                WritesDrawing = true,
                Run = RunNodeCreateCorridorRegions
            },
            ["create_design_profiles"] = new OpDef
            {
                Description = "S03: build ground profile + design profile; where the ground at an end is above the bottom elevation, ramp at 1:10 to meet the terrain (ramp length from the height difference), otherwise flat",
                Parameters = "surface(required, existing ground surface name) design_elev?(default 3.0) end_slope?(default 10, i.e. 1:10) "
                           + "ramps?({channel:[\"start\",\"end\"]}, ramps per end, default all flat) "
                           + "ramp_tolerance?(default 0.05, no ramp if the height difference is below it) channels?([] only these) "
                           + "ground_name?(default \"{channel}-EG\") design_name?(default \"{channel}-FG\") "
                           + "ground_style? design_style? label_set? replace_existing?(default true)",
                WritesDrawing = true,
                Run = RunNodeCreateDesignProfiles
            },
            ["offsets_from_boundary"] = new OpDef
            {
                Description = "S02: split a closed boundary into left/right offset alignments {channel}_L/{channel}_R (corridor width targets); left/right extremes are bucketed by station and thinned",
                Parameters = "boundary_prefix?(default \"BOUNDARY-\") interval?(default 25, sampling and thinning step) channel?(only this one) "
                           + "style? label_set? erase_boundary?(default false) replace_existing?(default true) min_boundary_area?(default 100)",
                WritesDrawing = true,
                Run = RunNodeOffsetsFromBoundary
            },
            ["measure_channel_width"] = new OpDef
            {
                Description = "Measure the actual dredging width along each channel: boundary vertices projected onto the centerline give (station, offset), bucketed into left/right half widths (channels vary in width; a single mean top width cannot express it)",
                Parameters = "interval?(default 50) min_area?(default 100, boundaries smaller than this are dropped as fragments) channel?(only this one)",
                WritesDrawing = false,
                Run = RunNodeMeasureChannelWidth
            },
            ["export_design_lines"] = new OpDef
            {
                Description = "Export modelling input lines to a brand-new blank DWG: centerlines/edge lines/corridor boundaries (closed), layer = identity; edge ownership and side measured via StationOffset",
                Parameters = "out(required, absolute path) overwrite?(default false) boundaries?(default true, export corridor surface outer boundaries) "
                           + "center_layer?(default \"CL-{channel}\") edge_layer?(default \"EDGE-{channel}-{side}\") "
                           + "boundary_layer?(default \"BOUNDARY-{channel}\") center_color?(3) edge_color?(4) boundary_color?(2) "
                           + "center_linetype?(CENTER2) edge_linetype?(Continuous) boundary_linetype?(Continuous) "
                           + "linetype_scale?(default 5) spiral_step?(default 5) probe_points?(default 20) offset_tolerance?(default 200)",
                WritesDrawing = true,
                Run = RunNodeExportDesignLines
            },
            ["offset_alignment"] = new OpDef
            {
                Description = "Create left/right offset alignments by distance (names carry _L/_R; the corridor step finds its targets by them)",
                Parameters = "alignment(required) distance?(default 15) offsets?[number = whole length; object {distance,start_station,end_station,name?} = segmented offset] style?",
                WritesDrawing = true,
                Run = RunNodeOffsetAlignment
            },
            ["create_connected_alignment"] = new OpDef
            {
                Description = "Create a connected alignment between two alignments by radius (intersection corner, dynamically follows the parents; pair with segmented offsets)",
                Parameters = "name(required) in_alignment/in_station(required) out_alignment/out_station(required) radius?(default 20) "
                           + "greater_than_180?(default false) offset_in?/offset_out?(default 0) style? label_set?",
                WritesDrawing = true,
                Run = RunNodeCreateConnectedAlignment
            },
            ["create_fillet_alignment"] = new OpDef
            {
                Description = "Properly constrained corner alignment: fixed line + free arc (tangent, radius parameter) + fixed line (changing the radius keeps tangency)",
                Parameters = "name(required) line1:[[x,y],[x,y]](required) line2:same(required) radius?(default 20) greater_than_180?(default false) style? label_set?",
                WritesDrawing = true,
                Run = RunNodeCreateFilletAlignment
            },
            ["trim_offsets_to_corners"] = new OpDef
            {
                Description = "Parcel corner close-up: pull the offset-segment range ends back in place to just outside the connected alignment's arc tangent points (no delete, no rebuild, corner survives)",
                Parameters = "corners:[{corner,in_alignment,out_alignment},...](required) margin?(default 0.05)",
                WritesDrawing = true,
                Run = RunNodeTrimOffsetsToCorners
            },
            ["rebuild_fillet_network"] = new OpDef
            {
                Description = "Recast the corner network (point-to-point contract): per corner from the pairing table, resolve, re-solve and rebuild in place (create if missing), "
                            + "write C3DF_FILLET XData for identity (used by WaterBox), pull parent segment range ends exactly to the outer leg ends (segment end point == corner start point), "
                            + "report the measured gap width at every joint",
                Parameters = "corners:[{corner,a,b,radius?},...](required) radius?(default 20) leg?(default 0.1) erase?([] delete these leftovers first)",
                WritesDrawing = true,
                Run = RunNodeRebuildFilletNetwork
            },
            ["assign_layers"] = new OpDef
            {
                Description = "Batch re-layer: per group move alignments (by name) / entities (by handle) to the given layer, creating it (with colour) if missing; only Layer changes, geometry untouched",
                Parameters = "groups:[{layer(required) color?(ACI, default 7) alignments?([] alignment names) handles?([] handles)},...](required)",
                WritesDrawing = true,
                Run = RunNodeAssignLayers
            },
            ["list_site_parcels"] = new OpDef
            {
                Description = "Parcel inventory (read-only): per site reports parcel count + each parcel's name/area (to reconcile parcel ring closure). No managed API to move alignments between sites; do it in Prospector: multi-select, right-click, Move to Site",
                Parameters = "site?(only this site, default all)",
                WritesDrawing = false,
                Run = RunNodeListSiteParcels
            },
            ["draw_polylines"] = new OpDef
            {
                Description = "Batch-draw polylines (with bulge arcs) into the host drawing: parcel boundaries and other derived products; clear_layers removes old lines first so re-runs are idempotent",
                Parameters = "polylines:[{vertices:[[x,y,bulge?],...](required) layer? color? closed?(default true) name?},...](required) "
                           + "layer?(default \"0\") color?(default 7) clear_layers?([] delete polylines on these layers first)",
                WritesDrawing = true,
                Run = RunNodeDrawPolylines
            },
            ["draw_table"] = new OpDef
            {
                Description = "Self-drawn data table (pure CAD lines + text): rows as a 2D array drawn as a table, in model space or a given layout; for quantity tables on sheets, "
                            + "replacing OLE (OLE cannot be pasted headless, edited, or plotted by accore). Entities carry XData (C3DF_TBL:name) for identity; re-running with the same name clears the old table first",
                Parameters = "rows(required, [[cell,...],...]) x,y(required, table top-left; in a layout = paper mm) space?(default Model, or a layout name) "
                           + "width?(total width, columns scaled proportionally if given) col_widths?([] per-column widths) row_height?(default 5) text_height?(default 2.5) "
                           + "header_rows?(default 1) header_row_height?(default = row_height) layer?(default C3DF-TABLE) color?(default 7) "
                           + "text_style?(default auto-picks -SimHei) name?(default \"Table\", identity tag used to clear the old table) clear?(default true) "
                           + "mask?(default false, Wipeout white background under the table to hide model content in the viewport) lineweight?(layer lineweight mm, default 0.25, 0 = unset); "
                           + "new entities always go to the top of the draw order (viewports are often brought to front in layouts and would otherwise cover them)",
                WritesDrawing = true,
                Run = RunNodeDrawTable
            },
            ["set_offset_width"] = new OpDef
            {
                Description = "Change channel half width (headless): set NominalOffset = +-width on all offset children of the main alignment; the report includes read-back values to catch silent failures of snapshot properties",
                Parameters = "alignment(required, main alignment name) width?(default 15)",
                WritesDrawing = true,
                Run = RunNodeSetOffsetWidth
            },
            ["list_offset_widths"] = new OpDef
            {
                Description = "Offset width list (read-only): parent/NominalOffset/range count of every offset alignment (to reconcile per-channel widths when making parcels)",
                Parameters = "none",
                WritesDrawing = false,
                Run = RunNodeListOffsetWidths
            },
            ["erase_alignments"] = new OpDef
            {
                Description = "Batch-delete alignments by name or by handle (handles are immune to name-encoding issues); missing ones are reported, not thrown",
                Parameters = "names?([] alignment names) handles?([] handles), give at least one",
                WritesDrawing = true,
                Run = RunNodeEraseAlignments
            },
            ["create_profiles"] = new OpDef
            {
                Description = "Create ground profile (sampled from a surface) + design profile (flat)",
                Parameters = "alignment(required) surface(required) design_elev?(default 0) ground_style? design_style? label_set?",
                WritesDrawing = true,
                Run = RunNodeCreateProfiles
            },
            ["create_corridor"] = new OpDef
            {
                Description = "Create a corridor and set targets (surface slot -> existing ground, offset slots -> left/right offset alignments)",
                Parameters = "alignment(required) assembly(required, look up the name with civil_env) surface(required, existing ground) name? baseline? region?",
                WritesDrawing = true,
                Run = RunNodeCreateCorridor
            },
            ["create_corridor_surface"] = new OpDef
            {
                Description = "Create a corridor surface on the corridor (defaults to link codes containing Top, falls back to all) + outer boundary",
                Parameters = "alignment(required) corridor? name? link_codes?[] boundary?(default true)",
                WritesDrawing = true,
                Run = RunNodeCreateCorridorSurface
            },
            ["create_sample_lines"] = new OpDef
            {
                Description = "Create a sample line group (clears all old groups on this alignment first) and mark existing ground/corridor/corridor surface as sampled; supports equal spacing or explicit endpoints",
                Parameters = "alignment(required) surface(required, existing ground) interval?(default 50) swath?(default 50) style? corridor? road_surface? "
                           + "lines?([{name?,points:[[x,y],...]}], if given lines are built from explicit endpoints and interval/swath are ignored)",
                WritesDrawing = true,
                Run = RunNodeCreateSampleLines
            },
            ["import_surface"] = new OpDef
            {
                Description = "WblockClone a TIN surface of the given name from an external DWG into the current drawing (skipped if the name already exists)",
                Parameters = "dwg(required, absolute path of the source drawing) name(required, surface name)",
                WritesDrawing = true,
                Run = RunNodeImportSurface
            },
            ["create_dike_sample_lines"] = new OpDef
            {
                Description = "Embankment batch: convert each centerline on the layers into an alignment (start oriented by K-station texts along it), build a sample line group from section lines already drawn and sample the given existing ground; original centerlines are kept",
                Parameters = "centerline_layers(required, [] centerline layer names) surface(required, existing ground surface name) number_layer?(default DIKE-NO) "
                           + "station_layer?(default 0) section_layers?([], default [SECTION-LINE]) number_max_dist?(150) station_max_dist?(60) "
                           + "section_margin?(120) fallback_swath?(50, one-side width used as fallback from station texts when no section line intersects) "
                           + "rebuild_only?(default false; true = alignment exists, only rebuild the sample line group; use after the corridor is built) corridor_suffix?(_Corridor) road_surface_suffix?(_Design)",
                WritesDrawing = true,
                Run = RunNodeCreateDikeSampleLines
            },
            ["extract_measured_sections"] = new OpDef
            {
                Description = "Calibrate surveyed sections and extract ground lines (read-only): one drawing tiles many surveyed sections; by layer convention "
                            + "(ground line dmx*, scale texts zdmt*, title 1-SheetTitle*) each section is calibrated paper -> engineering coordinates by least squares on the scale texts, "
                            + "reporting PASS/FAIL, residuals, station, point count and crest elevation per section. First step of the embankment-demolition chain; derive the parameters of later steps from its output",
                Parameters = "ground_layer?(default dmx*, * wildcard) column_layer?(default zdmt*) title_layer?(default 1-SheetTitle*) "
                           + "title_regex?(title parsing regex, default 'line name-K km+m section', must contain the named groups ln/km/m) "
                           + "offset_tolerance?(0.06) elev_tolerance?(0.02) table_depth?(80, how far down to look for scale texts, paper units) "
                           + "pair_tol_x?(3, X tolerance for pairing scale texts with vertices) line_filter?(regex, only process matching survey lines/titles) "
                           + "include_ground_points?(default false, true = output all (offset, elevation) pairs per section)",
                WritesDrawing = false,
                Run = RunNodeExtractMeasuredSections
            },
            ["generate_demolition_design_lines"] = new OpDef
            {
                Description = "Embankment-demolition section design lines: per section draw the stripping line (segments where ground is above the threshold lowered by the stripping thickness), the excavation line "
                            + "(segments where the stripped surface is above the bottom elevation, limited to +-half_width, out-of-range ends sloped at 1:m to daylight), "
                            + "ANSI31/ANSI37 hatches and leader labels. Ported from C3DF-GenDesignLine of the demolition plugin V1; "
                            + "boundaries can be edited by hand afterwards, quantities are recomputed by compute_embankment_demolition from the lines actually in the drawing",
                Parameters = "ground_layer? column_layer? title_layer? title_regex? offset_tolerance? elev_tolerance? "
                           + "table_depth? pair_tol_x? line_filter?(same as extract_measured_sections) "
                           + "strip_threshold_elev?(6.5, stripping threshold elevation; set above the crest to disable stripping) strip_thickness?(0.3, stripping thickness) "
                           + "bottom_elev?(5.0, design demolition bottom elevation) bottom_elev_by_section?({title or line name:elevation}, per-section override) "
                           + "slope_ratio_m?(3.0, m of slope 1:m) excavation_half_width?(8.0, excavation half width from centerline, sloped side only) "
                           + "strip_line_layer?(C3DF-STRIP-LINE) excavation_line_layer?(C3DF-CUT-LINE) hatch_layer?(C3DF-HATCH) "
                           + "annotation_layer?(C3DF-LABEL) centerline_layer?(C3DF-CL) hatch_scale?(15) text_height?(2.5, paper units) "
                           + "text_style?(label text style, default the drawing's current style; if the default style uses txt.shx Chinese renders as ????, so give a CJK-capable style or run normalize_textstyles first) "
                           + "draw_hatch?(true) draw_labels?(true) clear_existing?(true, clear lines/hatches/texts on these 5 layers before re-running)",
                WritesDrawing = true,
                Run = RunNodeGenerateDemolitionDesignLines
            },
            ["compute_embankment_demolition"] = new OpDef
            {
                Description = "Embankment-demolition quantities (average end area): reads the actual stripping/excavation lines in the drawing (manual edits allowed), "
                            + "measures stripping area and subsoil excavation area per section, labels them above the section, and outputs a volume table (xlsx/csv) by average end area between adjacent stations on the same survey line. "
                            + "Ported from C3DF-CalcVolume of the demolition plugin V1; run after the design lines are drawn (and edited)",
                Parameters = "ground_layer? column_layer? title_layer? title_regex? offset_tolerance? elev_tolerance? "
                           + "table_depth? pair_tol_x? line_filter?(same as extract_measured_sections) "
                           + "strip_line_layer?(C3DF-STRIP-LINE) excavation_line_layer?(C3DF-CUT-LINE) own_margin?(30, bounding-box expansion for assigning design lines to sections, paper units) "
                           + "annotate_sections?(true, write areas above sections) annotation_layer?(C3DF-QTY-LABEL) text_height?(2.5) "
                           + "text_style?(same as generate_demolition_design_lines) "
                           + "clear_existing?(true, only clears annotation_layer) export_excel?(true) outdir? excel_out_path? excel_format?(xlsx|csv|both)",
                WritesDrawing = true,
                Run = RunNodeComputeEmbankmentDemolition
            },
            ["compute_quantities"] = new OpDef
            {
                Description = "Compute a material list by QTO criteria, giving cut/fill per station",
                Parameters = "alignment(required) surface(required, existing ground) criteria(required, criteria name) road_surface_slot?(default \"Design\") road_surface? corridor?",
                WritesDrawing = true,
                Run = RunNodeComputeQuantities
            },
            ["create_profile_view"] = new OpDef
            {
                Description = "Create a profile view in model space (both ground and design profiles are drawn)",
                Parameters = "alignment(required) x? y?(default 200 m below the alignment start) style? band_set? name?",
                WritesDrawing = true,
                Run = RunNodeCreateProfileView
            },
            ["create_section_views"] = new OpDef
            {
                Description = "Create section views in model space: one per sample line + grid placement + volume table",
                Parameters = "alignment(required) group?(sample line group name, default <alignment>_SampleLines; if not found and there is exactly one group, that one is used) "
                           + "style? code_set? section_style?(ground-line section style, e.g. @C3DF-GroundLine) elev_min? elev_max?(automatic if min>=max) "
                           + "offset_left?(50) offset_right?(50) x? y? rows?(2) cols?(2) col_spacing?(130) row_spacing?(45) group_spacing?(0) "
                           + "placement?(draft|production, default draft) template?(production: .dwt/.dwg whose layout holds the sheet viewport) "
                           + "layout?(production: layout name in the template, default the first) group_plot_style?(GroupPlotStyles name, production) "
                           + "material_style?(shape or section style for cut/fill sections) volume_table?(default false) corridor?",
                WritesDrawing = true,
                Run = RunNodeCreateSectionViews
            },
            ["dump_label_styles"] = new OpDef
            {
                Description = "Read-only diagnostic: reflect and flatten the text components (content and offsets) of label styles (LabelStyle). "
                            + "The fixed text a label shows lives inside the style's text components and never reaches plain text via DXFOUT (Civil objects are proxies), "
                            + "so only the API can read it; before adjusting label positions use it to identify which style and component produces the text",
                Parameters = "contains?(only report styles whose flattened result contains this string, e.g. DredgeControlLine; all if omitted) "
                           + "depth?(flatten depth, default 4) max_styles?(max styles to scan, default 400). "
                           + "Each component reports anchor_component/anchor_location (which point of which component it is anchored to) + attachment; "
                           + "each style reports dragged_state (Dragged State page: DisplayType/TextHeight/LeaderType...) and leader",
                WritesDrawing = false,
                Run = RunNodeDumpLabelStyles
            },
            ["list_styles"] = new OpDef
            {
                Description = "Read-only survey: list the names in each Civil style collection of the drawing (profile view styles/band sets/label sets/section view styles...). "
                            + "Before swapping styles or moving styles between drawings, use it to check whether both drawings have same-named styles",
                Parameters = "contains?(only collections whose name contains this string, e.g. Profile / Band / LabelSet)",
                WritesDrawing = false,
                Run = RunNodeListStyles
            },
            ["dedupe_entities"] = new OpDef
            {
                Description = "Deduplicate entities with identical position and content in model space (MTEXT/TEXT/LINE/PLINE/block refs/SOLID), "
                            + "optionally moving MTEXT matched by content. Used to clean the 655 overlapping labels drawn twice by the engine in project B's preliminary-design sections on the exported file",
                Parameters = "layer?(restrict to layer) tol?(position quantisation in metres, default 0.001) "
                           + "moves?(array of {contains, dx?, dy?}; after dedup, move texts whose content contains 'contains')",
                WritesDrawing = true,
                Run = RunNodeDedupeEntities
            },
            ["set_label_style"] = new OpDef
            {
                Description = "Edit label style components: visibility/text offset/text content/line angle and length. Styles are searched in the LabelStyles root and "
                            + "CodeSetStyles branch (same source as dump_label_styles). Used to turn off one branch of the 655 doubled labels in project B's preliminary-design sections "
                            + "(code set branch + marker branch each drew one)",
                Parameters = "style(label style name, required) components(array, required): each {name(component name, * = all), "
                           + "visible?, x_offset?, y_offset?, contents?, angle_deg?, length?, height?, "
                           + "attachment?(TopCenter/MiddleCenter/BottomCenter...), anchor_component?(component name or <Feature>), "
                           + "anchor_location?(TopCenter/BottomCenter…)} "
                           + "dragged_state?({property:value}, keys as reported by dump_label_styles under dragged_state, e.g. DisplayType/TextHeight)",
                WritesDrawing = true,
                Run = RunNodeSetLabelStyle
            },
            ["add_note_label"] = new OpDef
            {
                Description = "Batch-place General Note Labels: one label of the given style per point. Coordinate labelling automation goes here: "
                            + "put <[Northing]>/<[Easting]> fields in the style, the value is taken at the drop point and follows drags, no manual copying. "
                            + "Points may carry dx/dy to drop straight into the dragged state (for dragged layout or batch leaders)",
                Parameters = "style(general note label style name, required, case-sensitive) points(required, [{x,y,dx?,dy?}] dx/dy = drag offset, drawing units) layer?(target layer, created if missing)",
                WritesDrawing = true,
                Run = RunNodeAddNoteLabel
            },
            ["dump_material_styles"] = new OpDef
            {
                Description = "Read-only diagnostic: flatten 'material list -> material -> style' and the style references of MaterialSection entities (reflection), to find material hatches using the wrong style",
                Parameters = "sample?(number of MaterialSection samples, default 5)",
                WritesDrawing = false,
                Run = RunNodeDumpMaterialStyles
            },
            ["restyle_section_views"] = new OpDef
            {
                Description = "Swap styles / set elevations on existing section views in place, without delete or rebuild (create_section_views rebuilds by overwriting, "
                            + "which requires deleting old views with volume tables, after which save_dwg always throws eWasOpenForWrite; this node avoids the delete path "
                            + "and never touches sample lines, material lists, volume tables or quantities)",
                Parameters = "alignment?(default all alignments) style?(section view style) section_style?(ground-line section style) "
                           + "material_style?(material section style, default follows section_style) "
                           + "material_shape_style?(material hatch style = ShapeStyles, e.g. @C3DF-CutFill; set on QTOMaterial.ShapeStyleId of the material list) "
                           + "corridor_surface_style?(corridor surface section style; point it at a no-plot style to hide corridor surface lines) "
                           + "elev_min? elev_max?(only applied if min<max) elev_auto?(explicitly back to automatic elevation); give at least one. "
                           + "material_style is looked up in SectionStyles first, then ShapeStyles (@C3DF-CutFill is in the latter); untouched if omitted, "
                           + "never fall back to the ground-line style and overwrite values the user set by hand. Code set styles of the corridor section itself are untouched",
                WritesDrawing = true,
                Run = RunNodeRestyleSectionViews
            },
            ["restyle_profile_view_bands"] = new OpDef
            {
                Description = "Swap band styles on existing profile views: replaced in place by an 'old style name -> new style name' mapping, "
                            + "no delete/rebuild, band data sources and view styles untouched (create_profile_view can only apply a whole band set at creation "
                            + "and cannot help finished views; in the GUI it is Profile View Properties -> Bands, one view at a time)",
                Parameters = "bottom?([] bottom band style names, one per position from top to bottom, null/empty = leave that position) top?([] likewise); give at least one; "
                           + "views?([] view names, default all profile views) dry_run?(default false, true = report only, no save). "
                           + "Style names must exist exactly under BandStyles in the drawing; a missing one is an error, no fallback; "
                           + "views whose band count differs from the array length are skipped as a whole (views_mismatched), never half-written. "
                           + "**Positional replacement only**: BandStyleId is write-only in the Civil 2025 API, the old style name cannot be read, "
                           + "so 'match by name' is impossible; dry_run first and check per_view.fingerprint to see whether the views are position-isomorphic before saving",
                WritesDrawing = true,
                Run = RunNodeRestyleProfileViewBands
            },
            ["sync_corridor_range"] = new OpDef
            {
                Description = "Align corridor regions to the alignment start/end (after a centerline swap): first region start / last region end of each baseline pulled to the alignment ends, middle regions clipped into range, then Rebuild",
                Parameters = "corridor(required, corridor name)",
                WritesDrawing = true,
                Run = RunNodeSyncCorridorRange
            },
            ["refresh_sample_lines"] = new OpDef
            {
                Description = "Keep the sample line group, regenerate the lines along new geometry (after a centerline swap; create_sample_lines deletes and rebuilds the group, this node keeps the group and its sampled sources)",
                Parameters = "alignment(required) interval?(default inferred from the existing line count) swath?(default inferred from the existing line lengths)",
                WritesDrawing = true,
                Run = RunNodeRefreshSampleLines
            },
            ["restore_sample_line_labels"] = new OpDef
            {
                Description = "Rebuild sample line labels (station names) that were erased: one SampleLineLabelGroup per sample line group. "
                            + "Note: SampleLine has no LabelStyleId; labels are separate entities attached to the group, not to the lines, "
                            + "so 'assign a label style to each line' is a dead end; ERASE on the sample line family before export wipes the labels too, use this node to restore them",
                Parameters = "style?(sample line label style name, default @C3DF-AlignmentStation, case-sensitive) alignment?(only this alignment's groups, default whole drawing) "
                           + "skip_existing?(default true, groups that already have labels are skipped) dry_run?(default false, inventory only). "
                           + "Built-in acceptance: label group count = sample line group count and total SubEntityCount = total sample lines; throws without committing if short",
                WritesDrawing = true,
                Run = RunNodeRestoreSampleLineLabels
            },
            ["arrange_section_sheets"] = new OpDef
            {
                Description = "Section view layout source of truth: place section views to the right of all alignments. layout=rows (default) one alignment per row with the alignment name at the row start; "
                            + "layout=sheets arranges frames in a grid. Only Location changes, no rebuild, all styles and labels kept. "
                            + "Batch output goes 'create_section_views generates -> this node lays out'; the x/y/spacing of create_section_views are for single-alignment self-tests only",
                Parameters = "layout?(rows|sheets, default rows) scale?(default 200, plot scale) paper?(default A3) paper_w? paper_h?(mm) "
                           + "rows?(1) cols?(1, section grid inside one frame; A3@1:200 only fits 1) "
                           + "row_pitch?/col_pitch?(model units, centre distance between adjacent sections; 0 = divide the frame evenly. When sections are shorter than the cells even division leaves gaps; this is the knob to tighten row spacing) "
                           + "sheets_per_row?(10, sheets layout only) "
                           + "margin?(200, distance from the alignment area) sheet_gap?(0) row_gap?(0) inner_margin_ratio?(0.05) "
                           + "offset_left? offset_right?(if given also narrows the section display width, to fix cells that do not fit) "
                           + "draw_frames?(default true, draw a closed polyline frame per sheet, can be fed straight to DWGTitleblockPlotter for batch plotting) "
                           + "row_label?(default true) label_height?(default 5% of frame height) label_gap? "
                           + "per_alignment_new_sheet?(default true, sheets layout only) alignments?[](default all) x? y?(layout origin, default automatic)",
                WritesDrawing = true,
                Run = RunNodeArrangeSectionSheets
            },
            ["restore_corridor_section_labels"] = new OpDef
            {
                Description = "Host COM node: run CORRIDORSECTIONLABELSCONV -> ALL -> C on the result DWG (selection set and action are hard-coded in the command, not parameters)",
                Parameters = "out(required, save-as path) overwrite? visible?; actual entry point nodes/restore_corridor_section_labels/run.ps1, "
                           + "called by civil3dfactory.ps1 after save_dwg; this entry only registers the deferred execution",
                WritesDrawing = false,
                Run = RunNodeRestoreCorridorSectionLabels
            },
            ["export_quantities"] = new OpDef
            {
                Description = "Export computed quantities to xlsx/csv (cumulative/incremental cut and fill per station + totals)",
                Parameters = "alignment(required) outdir? format?(xlsx|csv|both, default both)",
                WritesDrawing = false,
                Run = RunNodeExportQuantities
            },
            ["insert_title_blocks"] = new OpDef
            {
                Description = "Batch-insert title block references at model scale (A3@1:500 = 210x148.5 m per sheet); XData-marked so it can be re-run",
                Parameters = "block(required, block name) from_dwg?(block library file, used when the block is not in the drawing) count?(default 1) cols?(default 1) x? y? "
                           + "paper?(A0..A4, default A3) paper_w? paper_h?(mm, override presets) scale?(default the drawing's current annotation scale) "
                           + "block_scale?(default = scale/1000) gap_x? gap_y? layer? attributes?{tag:value}({n} in a value is replaced by the index) "
                           + "at?[{x,y,attributes?}](if given, insert point by point and ignore grid parameters; insertion point = lower-left of the block extents, "
                           + "feeding sheets[].origin_x/y from arrange_section_sheets aligns exactly with the locating frames; per-point attributes override same-named global ones) "
                           + "erase_same_block?(default true, erase all model-space references of the same block before re-running; XData marks are lost on save, relying on them alone would stack up inserts) "
                           + "width_factors?{tag:factor}(squeeze long text into narrow cells: compress attribute text by tag) "
                           + "attsync?(default true: after inserting run ATTSYNC on this block to sync attribute position/format back to the definition, then re-apply width_factors)",
                WritesDrawing = true,
                Run = RunNodeInsertTitleBlocks
            },
            ["create_layout_sheet"] = new OpDef
            {
                Description = "Create a layout sheet: title block in paper space, viewport positioned by a model window, fixed scale optional, locked or not",
                Parameters = "layout?(default C3DF-A3) block(required) from_dwg?(used when the block definition is not in the drawing) "
                           + "model_window?{minx,miny,maxx,maxy} boundary_handle? boundary_layer? paper?(default A3) "
                           + "viewport?{x,y,width,height,scale?,locked?,layer?(default C3DF-VPORT-NOPLOT non-plotting; "
                           + "give a plottable layer to plot the border),freeze_layers?[]} frame_x? frame_y? frame_scale?(default 1) "
                           + "frame_layer?(default 0) attributes?{tag:value} width_factors?{tag:factor}(squeeze long sheet titles into narrow cells) "
                           + "extra_viewports?[{x,y,width,height,scale(required),center_x?,center_y?,locked?,layer?,"
                           + "freeze_layers?[]}](additional small viewports in the same frame, e.g. the key map of a plan sheet; centre defaults to the main window centre) "
                           + "Every new viewport first thaws all layers and then freezes freeze_layers, washing out the source drawing's 'frozen in new viewports' layer state "
                           + "(that state is invisible to DXF parsing and makes whole layers vanish from the finished sheet) "
                           + "clear?(default true = clear this layout before re-running; false = stack another sheet in the same layout, "
                           + "shifting frame_x/viewport.x per sheet lines up several plan sheets in one layout)",
                WritesDrawing = true,
                Run = CreateLayoutSheet
            },
            ["create_sheet_region"] = new OpDef
            {
                Description = "Create non-plotting sheet frames in model space; frame size derived from paper size and plot scale",
                Parameters = "x y(lower-left; or give alignment to position automatically at the alignment start) paper?(default A3) scale?(default 500) "
                           + "alignment? offset_x?(default -100) offset_y?(default -1600) "
                           + "name?(default SHEET-01) layer?(default C3DF-SHEET-REGION-NOPLOT) "
                           + "count?(default 1) pitch_x?(default 0) pitch_y?(default 0; in batches names get -01/-02 appended)",
                WritesDrawing = true,
                Run = RunNodeCreateSheetRegion
            },
            ["create_plan_frames_from_alignment"] = new OpDef
            {
                Description = "Generate consecutive horizontal plan-sheet frames along a Civil alignment; fixed world orientation, no sheet set created",
                Parameters = "alignment(required) paper?(default A3) scale?(default 5000) "
                           + "viewport_width_mm?(default 350) viewport_height_mm?(default 267) "
                           + "edge_margin_mm?(default 10) sample_step?(default 10 m) overlap_ratio?(default 0.10) "
                           + "start_station? end_station? max_frames?(0 = all) frame_prefix? layer? replace_existing?",
                WritesDrawing = true,
                Run = RunNodeCreatePlanFramesFromAlignment
            },
            ["create_plan_layouts_from_frames"] = new OpDef
            {
                Description = "Batch-convert horizontal plan-sheet frames into ordinary layouts of the current DWG; creates locked zero-rotation viewports and inserts the attribute title block",
                Parameters = "block(required) from_dwg? paper?(default A3) scale?(default 5000) "
                           + "frame_layer? layout_prefix? max_layouts?(0 = all) viewport?{} "
                           + "frame_x? frame_y? frame_scale? frame_block_layer? attributes?{}",
                WritesDrawing = true,
                Run = RunNodeCreatePlanLayoutsFromFrames
            },
            ["compose_layout_sheet"] = new OpDef
            {
                Description = "Create an entity-based layout sheet: scale-copy model-space entities of the current/external DWG into paper space, then wrap them with a whole title-block DWG",
                Parameters = "layout?(default C3DF-A3-Entities) paper?(default A3) clear?(default true) "
                           + "sources?[{dwg?(default current drawing),source_window?{minx,miny,maxx,maxy},crossing?(default false),"
                           + "exclude_layers?[],paper_scale?(fixed model -> paper factor),target?{x,y,width,height},rotation?}] "
                           + "frame{block,from_dwg,x?,y?,scale?,attributes?{}}(required) "
                           + "notes?[strings] notes_x? notes_y? notes_width? notes_height?",
                WritesDrawing = true,
                Run = ComposeLayoutSheet
            },
            ["entity_stats"] = new OpDef
            {
                Description = "Model-space entity statistics (grouped by type or layer), to answer \"what is actually in this drawing\"",
                Parameters = "by?(type|layer, default type) top?(default 30)",
                WritesDrawing = false,
                Run = EntityStats
            },
            ["set_scale"] = new OpDef
            {
                Description = "Set the model-space scale: annotation scale CANNOSCALE + Civil drawing scale (Drawing Settings -> Units and Scale; section/profile label text heights depend on it); created if the drawing lacks that entry. Result carries civil_drawing_scale_before/after",
                Parameters = "scale?(N of paper 1:drawing N; metric 1:500 passes 0.5, passing 500 creates 1:500000) name?(scale name, e.g. \"1:500\"; must match the drawing's entry exactly, including fullwidth vs. ASCII colon) "
                           + "drawing_scale?(Civil drawing scale, default the same ratio as the annotation scale DrawingUnits/PaperUnits; project A metric measurements: 0.5 -> labels 1:500, 500 -> 1:500000 with text x1000)",
                WritesDrawing = true,
                Run = SetScale
            },
            ["set_model_view"] = new OpDef
            {
                Description = "Set and save the initial model-space view window, so drawings with many Civil objects do not draw everything on open",
                Parameters = "minx miny maxx maxy(required)",
                WritesDrawing = true,
                Run = SetModelView
            },
            ["export_to_autocad"] = new OpDef
            {
                Description = "Explode Civil 3D objects into pure CAD entities and save as a new file (-EXPORTTOAUTOCAD); the original is untouched",
                Parameters = "out(required, absolute path) version?(default 2018) overwrite?(default false) "
                           + "explode_classes?([] DXF class names, exploded in place in memory into plain entities before export; "
                           + "the section QTO volume table AECC_SECTION_VIEW_QUANTITY_TAKEOFF_TABLE aborts the export with eLockViolation unless exploded; "
                           + "save_dwg first and then export so the master keeps its live tables)",
                WritesDrawing = false,
                Run = RunNodeExportToAutocad
            },
            ["annotate_grid_elevations"] = new OpDef
            {
                Description = "Grid corner elevation labels: inside every closed polyline on the boundary layer lay a square grid and label design/existing/difference at each intersection",
                Parameters = "boundary_layer(required, layer of the boundaries) design_surface(required) existing_surface(required) "
                           + "spacing?(default 50) text_height?(default 2.5) decimals?(default 2) offset_factor?(default 0.4) "
                           + "closed_only?(default true) draw_grid?(default true) clear_existing?(default true, only deletes this node's XData objects) "
                           + "grid_layer?/design_layer?/exist_layer?/diff_layer?(default C3DF-GRID/C3DF-DESIGN-ELEV/C3DF-EXISTING-ELEV/C3DF-DIFFERENCE)",
                WritesDrawing = true,
                Run = RunNodeAnnotateGridElevations
            },
            ["annotate_closed_polyline_areas"] = new OpDef
            {
                Description = "Closed polyline area labels with Excel summary: supports per-handle area overrides, automatic offset of overlapping labels and safe re-runs",
                Parameters = "source_layer?(default all layers) text_height?(default 2.5) decimals?(default 2) "
                           + "annotation_layer?(default C3DF-AREA-LABEL) color_index?(default 1) prefix? suffix?(default m²) "
                           + "include_zero?(default true) clear_existing?(default true) "
                           + "area_overrides?({handle:area}) label_offsets?({handle:[dx,dy]}) "
                           + "export_excel?(default true) outdir? excel_out_path? excel_format?(xlsx|csv|both)",
                WritesDrawing = true,
                Run = RunNodeAnnotateClosedPolylineAreas
            },
            ["calculate_surface_volume"] = new OpDef
            {
                Description = "Surface volume calculation and dredging statistics (full-extent GetVolumeProperties between two surfaces; per closed boundary use bounded_volumes. "
                            + "The fake boundary_polyline parameter was removed on 2026-08-20: it never took part in the calculation and is now an error)",
                Parameters = "base_surface(required) comparison_surface(required) cut_factor? fill_factor? export_excel? excel_out_path? draw_dwg_table? table_insertion_point?",
                WritesDrawing = true,
                Run = RunNodeCalculateSurfaceVolume
            },
            ["survey_dredge_regions"] = new OpDef
            {
                Description = "Dredging region boundary inventory and parameter trial (read-only): every closed polyline on the boundary layer reports handle/area/perimeter/centroid/winding/"
                            + "text inside (auto-detected region name)/existing ground elevation along the boundary; with bottom_elev it also trial-computes cut/fill on a grid "
                            + "using the same cone model as create_dredge_grading, without building surfaces or changing the drawing: settle parameters first, build later. First step of the grading chain",
                Parameters = "boundary_layer(required, layer of the boundaries) boundaries?([] handle whitelist, only these if given) "
                           + "surface?(existing ground surface name; elevations and trial only if given) bottom_elev?(volume trial only if given) "
                           + "slope_ratio_m?(default 5, m of 1:m) regions?(same as create_dredge_grading, per-region override; both nodes share the same merge rules) "
                           + "start_surface?(take the slope start elevation from this surface, e.g. an already designed channel surface; falls back to existing ground where unsampled) "
                           + "start_elev?(fixed slope start elevation) start_elev_cap?(cap on the slope start elevation = min(terrain, cap)) "
                           + "interface_layers?([] interface line layers; boundary segments near these lines are pushed down to interface_start_elev) "
                           + "interface_tolerance?(default 2, distance tolerance from boundary point to interface line) interface_start_elev?(default = bottom_elev, i.e. no slope on that segment) "
                           + "label_layer?(layer of region-name texts, default = texts on all non C3DF-* layers) sample_step?(default 2, boundary sampling step) "
                           + "grid_step?(default 5, trial grid) export_excel?(default false) outdir? excel_out_path? excel_format?(xlsx|csv|both)",
                WritesDrawing = false,
                Run = RunNodeSurveyDredgeRegions
            },
            ["create_dredge_grading"] = new OpDef
            {
                Description = "Dredging grading design surface (closed boundary -> bottom elevation + 1:m slope): the boundary polyline is the **slope start line (top edge)**; per region the area is cut to "
                            + "bottom_elev, sloped inward and downward at 1:m from the boundary to build a TIN design surface, plus crest/toe lines. "
                            + "Uses a distance field to the boundary (z = max(bottom, start elevation - distance/m)), so narrow strips at concave corners do not self-intersect; dense sampling in the slope band, sparse on the flat bottom. "
                            + "Global parameters + per-region overrides in regions = the parameter model (different slopes per region go here)",
                Parameters = "boundary_layer(required) surface(required, existing ground surface name) bottom_elev(required, design bottom elevation) "
                           + "slope_ratio_m(required, m of 1:m) boundaries?([] handle whitelist) "
                           + "start_surface?(take the slope start elevation from this surface, e.g. an already designed channel surface; falls back to existing ground where unsampled) "
                           + "start_elev?(fixed slope start elevation for the whole ring) start_elev_cap?(cap on the slope start elevation = min(terrain, cap)) "
                           + "interface_layers?([] interface line layers: boundary segments adjoining an already dredged channel get their start elevation pushed to interface_start_elev) "
                           + "interface_tolerance?(default 2) interface_start_elev?(default = bottom_elev, i.e. no slope on that segment, the channel provides its own) "
                           + "regions?([{id?,handle?,label?,bottom_elev?,slope_ratio_m?,start_elev?,start_elev_cap?,interface_start_elev?,z_segments?([{s0,s1,mode(existing|cap|fixed|bottom),value?}] top elevation per arc-length segment of the ring, authoritative over the interface rules where it hits),channel?,note?}] per-region override) "
                           + "label_layer?(layer of region-name texts, default = texts on all non C3DF-* layers) sample_step?(default 2) slope_step?(default 2, slope band grid) "
                           + "flat_step?(default 20, flat bottom grid) surface_prefix?(default \"DredgeDesign-\") surface_layer?(default C3DF-DREDGE-SURFACE) "
                           + "crest_layer?(default C3DF-DREDGE-TOP) toe_layer?(default C3DF-DREDGE-TOE) draw_crest_line?(default true) "
                           + "draw_toe_line?(default true) clear_existing?(default true, deletes surfaces with the surface_prefix and lines carrying C3DF_DREDGE XData)",
                WritesDrawing = true,
                Run = RunNodeCreateDredgeGrading
            },
            ["merge_post_dredge_surface"] = new OpDef
            {
                Description = "Post-dredge surface composition (design AND existing, take the lower): dredging only cuts, never fills; per point z = min(design surface, existing terrain) builds the real post-dredge TIN, "
                            + "existing ground below design elevation is kept as is. A temporary difference TIN yields the zero-difference contour at epsilon on the cut side as crease breaklines, "
                            + "also drawn on crease_layer as cut-area boundary lines; extent = design surface outline (outer boundary clip; with several rings the largest is the outer ring, the rest are holes). "
                            + "verify is on by default: existing vs post-dredge GetVolumeProperties; min() is never above existing => fill must be ~0, "
                            + "residual fill >0.5% of cut raises a warning; silent success = no success",
                Parameters = "design_surface(required, design surface name) existing_surface(required, existing terrain surface name) "
                           + "out_surface?(default \"Post-<design surface name>\"; idempotent re-run overwrites the same name) surface_layer?(default C3DF-POST-SURFACE) "
                           + "crease_layer?(default C3DF-POST-ZERO-LINE) draw_crease_lines?(default true, zero-difference lines drawn as cut-area boundary lines) "
                           + "min_crease_length?(default 1, shorter zero-difference fragments are dropped) contour_epsilon?(default 0.001, the zero line is actually taken 1 mm into the cut side) "
                           + "border_step?(default 2, step for unrolling the design surface outline) edge_step?(default 2, step for subdividing along the TIN edges of both surfaces, 0 = off; "
                           + "inside a face any triangulation is exact, error only arises across edges, so sampling along edges fits better than a blind grid) "
                           + "clear_existing?(default true, clears the same-named post-dredge surface and this node's zero lines) "
                           + "verify?(default true, self-check comparing existing vs post-dredge and existing vs design volumes)",
                WritesDrawing = true,
                Run = RunNodeMergePostDredgeSurface
            },
            ["parcel_grading_slope"] = new OpDef
            {
                Description = "Automatic inward/outward grading of a parcel",
                Parameters = "parcel_boundary(required) target_surface? direction? target_type? target_value? cut_slope? fill_slope? bench_height? bench_width? draw_comb_lines? comb_spacing?",
                WritesDrawing = true,
                Run = RunNodeParcelGradingSlope
            },
            ["offset_cone_contours"] = new OpDef
            {
                Description = "Offset cone: batch-offset a closed boundary to produce contours (same algorithm core as C3DF-OffsetCone/YT in products\\waterbox). "
                            + "Inward by default (island narrows to the top / pit narrows to the bottom); outward=true offsets outward: the boundary is the top elevation line spread out to target_z, which is island outward grading",
                Parameters = "target_z(required, target design elevation) slope_n(required, n of slope 1:n) step_dz?(elevation interval, default 0.5) "
                           + "outward?(default false = inward; true = outward, boundary fixed, slope goes outward) "
                           + "fillet_r?(fillet radius, default 2*n*dz, 0 = no smoothing) boundaries?[handle array] layer?(layer name, either this or boundaries)",
                WritesDrawing = true,
                Run = RunNodeOffsetConeContours
            },
            ["erase_entities"] = new OpDef
            {
                Description = "Delete entities by handle. Before deleting, reports what each object looks like (type/layer/elevation/vertices/length/area) "
                            + "and keeps the list in the result; dry_run:true reports without deleting. Handles only, no batch delete by layer",
                Parameters = "handles(required, [] handle array) dry_run?(default false)",
                WritesDrawing = true,
                Run = RunNodeEraseEntities
            },
            ["bounded_volumes"] = new OpDef
            {
                Description = "Cut/fill of a volume surface per closed boundary (read-only): uses Surface.GetBoundedVolumes, "
                            + "the same numbers the volumes panel gives when you 'add a bounded region'. "
                            + "Note: do not use calculate_surface_volume for this; that one uses GetVolumeProperties for the whole surface and ignores boundaries",
                Parameters = "volume_surface(existing volume surface name) or base_surface+comparison_surface(created if absent) "
                           + "layer?(layer of the boundaries) handles?([] handle whitelist), give at least one "
                           + "names?({handle:name}) sample_step?(default 1, step for unrolling the boundary into a point ring, arcs by true arc length) "
                           + "cut_factor?/fill_factor?(default 1, earthwork factor such as 1.06) datum_elevation?(if given uses the overload with datum elevation) "
                           + "export_excel?(default false) outdir? excel_format?(xlsx|csv|both, default xlsx)",
                WritesDrawing = false,
                Run = RunNodeBoundedVolumes
            },
            ["grid_earthwork_balance"] = new OpDef
            {
                Description = "Grid earthwork balance: on a volume surface, cells scattered by cell centre give per-cell cut/fill -> inter-cell transport problem (exact minimum of volume x haul distance) "
                            + "-> internal reallocation volume/weighted haul distance/haul band histogram; draws cut/fill colour cells + aggregated haul arrows in the drawing (four C3DF-Balance-* layers, old ones cleared idempotently). "
                            + "The true volume comes from GetBoundedVolumes with a closure assertion before balancing; cell size only affects haul-distance resolution, not volume. "
                            + "The cut/fill imbalance hangs on a virtual node (not in the histogram, no arrow), reported as shortfall (borrow) / surplus (spoil). "
                            + "Same algorithm core as C3DF-GridBalance/PH in products\\waterbox (GridBalanceCore.cs)",
                Parameters = "volume_surface(existing volume surface name) or base_surface+comparison_surface(created if absent) "
                           + "layer?(boundary layer) handles?([] handle whitelist), give at least one; names?({handle:name}) "
                           + "step?(statistics cell size, default 5) solver_max_nodes?(solver node limit, default 900, coarsened automatically above it) "
                           + "bands?([] haul distance band limits in m, default [30,50,100,200,300,500]) volume_factor?(report factor such as 1.06, default 1) "
                           + "closure_warn_pct?(default 2) closure_fail_pct?(default 10) sample_step?(boundary unrolling step, default 1) "
                           + "draw_cells?(default true) draw_arrows?(default true) max_arrows?(default 60) clear_previous?(default true) "
                           + "export_excel?(default true) outdir? excel_format?(xlsx|csv|both, default xlsx)",
                WritesDrawing = true,
                Run = RunNodeGridEarthworkBalance
            },
            ["make_parcels"] = new OpDef
            {
                Description = "Make parcels: extent lines + smoothed centerlines -> channel band boolean -> closed parcel boundaries (automatic sharp-corner detection and rounding). "
                            + "Several outer rings are grouped automatically (containment depth decides outer ring vs island hole; centerlines grouped by midpoint); centerline ends touching an edge are extended automatically to pierce the extent line; "
                            + "if any band fails the whole run errors and draws nothing. Same algorithm core as C3DF-MakeParcels/CT in products\\waterbox (MakeParcelsCore.cs)",
                Parameters = "boundary_layer? boundary_handles?([]) at least one of the two (closed polylines or polylines whose ends coincide) "
                           + "centerline_layer? centerline_handles?([]) at least one of the two (open polylines) "
                           + "half_width?(channel half width, default 15) fillet_r?(parcel corner radius, default 20) min_defl_deg?(rounding threshold angle, default 25) "
                           + "min_area?(warning area for small parcels, default 500, report only) out_layer?(default C3DF-TERRACE-BOUNDARY) "
                           + "channel_layer?(layer of the closed channel-extent rings, default C3DF-CHANNEL-EXTENT, hatchable) clear_previous?(default true, both layers cleared idempotently)",
                WritesDrawing = true,
                Run = RunNodeMakeParcels
            },
            ["set_polyline_elevation"] = new OpDef
            {
                Description = "Set polyline elevation (Z): design lines drawn in plan often sit at elevation 0 for the whole layer; lift them to design elevation before TIN or grading. Only Elevation changes, geometry and layer untouched",
                Parameters = "elevation?(one elevation for all) elevation_by_handle?({handle:elevation}, per line, takes precedence over elevation) "
                           + "layer?(whole layer) handles?([] handle whitelist); one of the object selectors, one of the elevation options",
                WritesDrawing = true,
                Run = RunNodeSetPolylineElevation
            },
            ["automate_2d_cross_sections"] = new OpDef
            {
                Description = "Batch-hatch existing closed polylines in model space (layer names containing FILL/CONC decide cut-fill/structure) and draw a six-row bottom table. "
                            + "Note: placeholder implementation without coordinate calibration: stations are index x 50, design elevation = bounding-box bottom + 5, cut depth hard-coded 5.0, "
                            + "area taken straight from polyline Area, interaction_mode read but unused; only a drawing aid, not for quantities. "
                            + "For surveyed-section quantities use extract_measured_sections -> generate_demolition_design_lines -> compute_embankment_demolition",
                Parameters = "interaction_mode?(echoed into the report only, no effect) hatch_rules?({cut,fill,concrete} pattern names) "
                           + "draw_bottom_table? table_style?(read but unused) annotation_text_height?(read but unused)",
                WritesDrawing = true,
                Run = RunNodeAutomate2DCrossSections
            },
            ["test_twotier_channel"] = new OpDef
            {
                Description = "Two-tier slope trial operator (main channel, first-tier slope, berm/bench, second-tier slope and lining)",
                Parameters = "center_x? center_y? bottom_width? h1? m1? bench_width? bench_slope? h2? m2? lining_thickness? layer?",
                WritesDrawing = true,
                Run = RunTestTwoTierChannel
            },
            ["create_twotier_bench_corridor"] = new OpDef
            {
                Description = "Option B two-tier slope with berm: native C# subassembly and 3D Corridor pipeline",
                Parameters = "bottom_width? h1? m1? bench_width? bench_slope? h2? m2? lining_thickness? alignment? surface? left_pkt? right_pkt? assembly? corridor?",
                WritesDrawing = true,
                Run = RunNodeCreateTwoTierBenchCorridor
            },
            ["create_3d_box"] = new OpDef
            {
                Description = "Create a 3D solid box (Solid3d Box) in AutoCAD/Civil 3D",
                Parameters = "x?(length, default 10) y?(width, default 10) z?(height, default 5) cx?(default 0) cy?(default 0) cz?(default 0) layer?(default C3DF-BOX-3D)",
                WritesDrawing = true,
                Run = RunCreate3DBox
            },
            ["create_3d_sphere"] = new OpDef
            {
                Description = "Create a 3D solid sphere (Solid3d Sphere) in AutoCAD/Civil 3D",
                Parameters = "radius?(radius, default 3.0) cx?(default 0) cy?(default 0) cz?(default 8) layer?(default C3DF-SPHERE-3D)",
                WritesDrawing = true,
                Run = RunCreate3DSphere
            },
            ["layers_off"] = new OpDef
            {
                Description = "Turn off the given layers (for hatch layers that block the view when plotting, e.g. C-RIVR-HATC-CUT)",
                Parameters = "name?(single) names?[](multiple)",
                WritesDrawing = true,
                Run = LayersOff
            },
            ["layers_on"] = new OpDef
            {
                Description = "Turn on all off/frozen layers (run it first when a headless plot comes out blank)",
                Parameters = "(none)",
                WritesDrawing = true,
                Run = LayersOn
            },
            ["save_dwg"] = new OpDef
            {
                Description = "Save in-memory changes to disk. Saves as a new file by default; writing back to the host drawing needs apply:true (automatic backup first)",
                Parameters = "out?(absolute path) apply?(default false) backup?(default true) overwrite?(default false)",
                WritesDrawing = true,
                Run = RunNodeSaveDwg
            },
            ["rebuild_corridor"] = new OpDef
            {
                Description = "Rebuild the corridor and compare link codes before/after (diagnoses whether subassemblies run in the current host)",
                Parameters = "name(required)",
                WritesDrawing = true,
                Run = RebuildCorridor
            },
            ["add_volume_tables"] = new OpDef
            {
                Description = "Attach Civil QTO volume tables to existing section views (no view rebuild). Note: AEC tables cannot be exported to pure CAD in accore "
                            + "(-EXPORTTOAUTOCAD aborts with eLockViolation); the sheet chain uses draw_section_volume_tables to draw tables itself instead; "
                            + "this node is kept for GUI output/cleanup",
                Parameters = "alignment(required) group?(default <alignment>_SampleLines) clear_all?(default false, first delete all section QTO tables in model space; pass once for the first alignment in the chain) "
                           + "create?(default true; false = cleanup only, no table) offset_x?(default 5) offset_y?(default 0)",
                WritesDrawing = true,
                Run = RunNodeAddVolumeTables
            },
            ["draw_section_volume_tables"] = new OpDef
            {
                Description = "Self-drawn section volume tables (pure CAD lines + text, three rows: station spanning columns/header/cut data), attached at the top-right of each section view by sample line station. "
                            + "Area from QTOSectionalResult.AreaResult.CutArea (exact per section), volume = incremental cut; both raw values without factors. "
                            + "target_dwg draws the tables straight into an already exported pure-CAD product (the current Civil drawing only supplies data and coordinates, unchanged); "
                            + "entities carry XData (C3DF_SVT) for identity, re-runs clear the old tables first. AEC QTO tables cannot be exported headless to pure CAD, the sheet chain always uses this node",
                Parameters = "alignment(required) group?(default <alignment>_SampleLines) scale?(default 500, plot scale; mm size x scale/1000 = model metres) "
                           + "text_mm?(2.5) row_mm?(5) col1_mm?(item column 16) col2_mm?(area column 30) col3_mm?(cut column 26) "
                           + "offset_x_mm?(2) offset_y_mm?(0) clear?(default true) layer?(default C3DF-VolumeTable) "
                           + "target_dwg?(absolute path; tables are drawn into this product drawing and saved; if locked a -locked-pending-replace file is written) "
                           + "text_style?(default auto-picks -SimHei) station_prefix?(default \"Sta \") "
                           + "header_item?/header_area?/header_volume?/row_label?(header texts) "
                           + "limit?(only draw the first N, for samples) dry_run?(default false, report positions and data without drawing)",
                WritesDrawing = true,
                Run = RunNodeDrawSectionVolumeTables
            },
            ["find_write_open"] = new OpDef
            {
                Description = "Scan the whole database for objects still open for write (type + handle). eWasOpenForWrite does not name the culprit; this locates it, "
                            + "and also verifies whether SaveDwg's ReclaimWriteOpen fallback really cleaned everything up",
                Parameters = "(none)",
                WritesDrawing = false,
                Run = RunNodeFindWriteOpen
            },
            ["rebind_volume_tables"] = new OpDef
            {
                Description = "Rebind volume tables with broken bindings to the current material list of their alignment (after compute_quantities recomputes, the old Guid dangles and tables show all zeros); "
                            + "position and style untouched, only MaterialListGuid is swapped and materials reselected; ownership by nearest alignment, distance reported",
                Parameters = "(none)",
                WritesDrawing = true,
                Run = RunNodeRebindVolumeTables
            },
            ["attach_baseline_profile"] = new OpDef
            {
                Description = "Re-attach the profile reference on corridor baselines (SetAlignmentAndProfile) and Rebuild: after the design profile was deleted and restored, "
                            + "regions/link codes are intact but the corridor surface is dead and rebuild_corridor cannot revive it; this is that disease",
                Parameters = "corridor(required) alignment?(only baselines of this alignment) profile?(profile name, default auto-picks the design profile FG/Layout) rebuild?(default true)",
                WritesDrawing = true,
                Run = RunNodeAttachBaselineProfile
            },
            ["list_profiles"] = new OpDef
            {
                Description = "Per-alignment inventory of profiles/profile views/sample line groups: has_ground and has_design use the same detection rules as create_profile_view; "
                            + "an alignment with can_make_profile_view=false will fail to produce a profile view: the pre-plot health check",
                Parameters = "alignment?(only one) skip_offset_alignments?(default true, do not list offset alignments such as Alignment - (n)-Left)",
                WritesDrawing = false,
                Run = RunNodeListProfiles
            },
            ["fix_self_intersections"] = new OpDef
            {
                Description = "Detect and repair self-intersecting polylines (remove loops): closed lines keep the larger-area ring, open lines drop lasso loops; "
                            + "if the dropped share exceeds max_drop_ratio it stops and reports 'needs manual confirmation' instead of destroying design intent. "
                            + "Run it before TIN / offset / area / hatch",
                Parameters = "handles?[handle array] layer?(layer name, either this or handles) max_drop_ratio?(default 0.25) report_only?(default false, check only, no change)",
                WritesDrawing = true,
                Run = RunNodeFixSelfIntersections
            },
            ["export_material_volumes"] = new OpDef
            {
                Description = "One-click export of all alignments' material volume tables into one xlsx: sheet1 'Summary' (Alignment/Length/Total volume = cumulative cut) + one sheet per alignment (cumulative/incremental cut and fill per station); alignments without a material list are skipped and listed",
                Parameters = "out?(absolute xlsx path, default outdir\\material-volumes.xlsx) outdir?",
                WritesDrawing = false,
                Run = RunNodeExportMaterialVolumes
            },
            ["export_all_dwg_tables"] = new OpDef
            {
                Description = "Export all tables in the DWG (material volume tables, CAD Table entities, Civil 3D material quantities) to Excel",
                Parameters = "target_dwg? excel_out?",
                WritesDrawing = false,
                Run = ExportAllDwgTables
            },
            ["surface_stats"] = new OpDef
            {
                Description = "Does the surface have geometry at all: vertex count, triangle count, elevation range (first stop when 'the result is 0')",
                Parameters = "name?(default all surfaces)",
                WritesDrawing = false,
                Run = SurfaceStats
            },
            ["corridor_stats"] = new OpDef
            {
                Description = "Corridor structure: baselines/regions/station ranges/link codes/corridor surface triangle counts",
                Parameters = "name?(default all corridors)",
                WritesDrawing = false,
                Run = CorridorStats
            },
            ["corridor_targets"] = new OpDef
            {
                Description = "Read-only inventory of corridor targets: what each target slot (surface/offset/elevation) of every region on every baseline points to; curve targets are also sampled along the line to measure their offset distribution relative to the baseline alignment, to judge 'constant-offset segments'",
                Parameters = "name?(default all corridors) sample_step?(curve offset sampling step in m, default 10) max_samples?(max sample points per curve, default 300)",
                WritesDrawing = false,
                Run = CorridorTargets
            },
            ["create_feature_lines"] = new OpDef
            {
                Description = "Upgrade lines to feature lines (FeatureLine): elevation mode const(fixed)/surface(AssignElevationsFromSurface snapshot incl. intermediate points)/cap(snapshot then clamp to min(terrain, z), matching the parcel rule)/keep(keep source z); arcs are subdivided into polylines by densify_step (v1 rule); props attaches the property set at creation. Entry point of the 'design truth = draggable classified feature lines' workflow",
                Parameters = "items(required, [{handle,name,z_mode(const|surface|cap|keep),z?(fixed value for const or cap for cap),surface?,densify_step?(default 10),layer?,erase_source?(default true),props?{set,values}}])",
                WritesDrawing = true,
                Run = CreateFeatureLines
            },
            ["dredge_from_feature_lines"] = new OpDef
            {
                Description = "Classified feature lines -> dredging design surface: collect lines by the 'DredgeFeatures' property set (region id/role/elevation mode [fixed|terrain|cap X]/slope m/design bottom elevation) -> chain endpoints into a ring (plan distance checked point to point; elevation jumps at joints are design, not gaps) -> distance-field grading (slope per line; terrain/cap lines sample the surface live) -> clear old lines of this region -> TIN + toe line. Bottom elevation precedence: parameter > 'design bottom elevation' field on the lines (conflict = error) > lowest point of the mouth line; terrain defaults to the 'terrain surface' field on the lines. Shares the surface-building stage with create_dredge_grading",
                Parameters = "region?(region id, scanned by property set) lines?([] handle whitelist, either this or region) set?(property set name, default DredgeFeatures) surface?(terrain) bottom_elev? name?(design surface name, default DredgeDesign-{region}) sample_step?(2) slope_step?(2) flat_step?(20) chain_tol?(0.1) draw_toe?(true) draw_crest?(false, the top edge is the feature line itself) surface_layer? crest_layer? toe_layer?",
                WritesDrawing = true,
                Run = DredgeFromFeatureLines
            },
            ["rename_alignments"] = new OpDef
            {
                Description = "Batch-rename alignments: objects and handles untouched, offsets/corridors/property sets and other references all kept; an existing target name or a missing source name is an error, never silent",
                Parameters = "items(required, [{from,to}])",
                WritesDrawing = true,
                Run = RenameAlignments
            },
            ["property_sets"] = new OpDef
            {
                Description = "Headless read/write of property sets (AEC PropertySet): define creates/completes the definition (idempotent), assign attaches to objects + sets values (re-run = refresh; values \"@area\"/\"@length\" are computed live from geometry), dump reads back for checking. Rule: property sets hold identity + design intent only, never computed results that go stale",
                Parameters = "define?{name,applies_to?[RXClass names],fields[{name,type(text|real|integer),description?,default?}]} set?(set name for assign, default = define.name) assign?[{handle,values{field:value}}] dump?{handles?[]|layer?}",
                WritesDrawing = true,
                Run = PropertySets
            },
            ["api"] = new OpDef
            {
                Description = "Reflect the real method signatures of a type (look up the Civil API when writing new operations instead of guessing)",
                Parameters = "type(required, class name or full name) member?(method name filter) statics_only?(default false) max?(default 120)",
                WritesDrawing = false,
                Run = ApiSignatures
            },
            ["civil_env"] = new OpDef
            {
                Description = "Inspect the drawing's Civil 3D inventory: whether CivilDocument is reachable, which assemblies/corridors/QTO criteria/styles exist",
                Parameters = "max?(max items per category, default 40)",
                WritesDrawing = false,
                Run = CivilEnv
            },
            ["list_styles"] = new OpDef
            {
                Description = "List **all** styles in the drawing: reflection walks the whole CivilDocument.Styles tree, no hand-written category names",
                Parameters = "filter?(name substring filter, e.g. \"@\") max?(max items per category, default 500) empty?(output empty categories, default false) depth?(max recursion depth, default 6)",
                WritesDrawing = false,
                Run = ListStyles
            },
            ["profile_label_sets_dump"] = new OpDef
            {
                Description = "List every label entry of a profile label set with its type and actual label style",
                Parameters = "name?(name substring, default all)",
                WritesDrawing = false,
                Run = ProfileLabelSetsDump
            },
            ["delete_styles"] = new OpDef
            {
                Description = "Batch-delete styles by 'category path + style name'. Default dry_run only reports; a real delete only changes memory, save_dwg persists",
                Parameters = "items(required, [{cat,name},...] cat is the category path from list_styles) dry_run?(default true) passes?(default 3, referenced styles are retried after their parents are deleted)",
                WritesDrawing = false,
                Run = DeleteStyles
            },
            ["style_display"] = new OpDef
            {
                Description = "Read/modify the 'Display' settings of a style (layer/colour/linetype/lineweight/visible on the style dialog's Display page). Without component lists all components with current values",
                Parameters = "cat(required, category path from list_styles) name(required) view?(Plan|Model|Section|Profile, default Plan) component?(component name, spaces/case ignored) set?{color,layer,linetype,lineweight,linetype_scale,visible} dry_run?(default true)",
                WritesDrawing = false,
                Run = StyleDisplay
            },
            ["list_layers"] = new OpDef
            {
                Description = "List the layer table: name, colour, linetype, lineweight, on/frozen/locked, used by entities",
                Parameters = "used_only?(default false) max?(default 2000)",
                WritesDrawing = false,
                Run = ListLayers
            },
            ["styles_audit"] = new OpDef
            {
                Description = "Batch-export style display settings: colour/layer/visible of every style x every view x every component, for whole-drawing normalisation",
                Parameters = "filter?(style name substring, e.g. \"@\") cats?[](only these category paths) max?(default 2000)",
                WritesDrawing = false,
                Run = StylesAudit
            },
            ["layers_edit"] = new OpDef
            {
                Description = "Create/rename/delete layers. Renaming **also rewrites the layer strings inside all styles** (Civil's DisplayStyle.Layer is a string; without sync the link breaks)",
                Parameters = "create?[{name,color?,linetype?}] rename?[{from,to}] delete?[name array] sync_styles?(default true) skip_cats?[](default skips AssemblyStyles, scanning it crashes) dry_run?(default true)",
                WritesDrawing = false,
                Run = LayersEdit
            },
            ["styles_normalize"] = new OpDef
            {
                Description = "Batch-normalise style display: all colours ByLayer, components on the given layer reassigned to the target layer",
                Parameters = "filter?(style name substring) color_bylayer?(default false) layer_moves?[{style,from,to}] dry_run?(default true)",
                WritesDrawing = false,
                Run = StylesNormalize
            },
            ["rename_styles"] = new OpDef
            {
                Description = "Batch-rename styles",
                Parameters = "items(required, [{cat,from,to}]) dry_run?(default true)",
                WritesDrawing = false,
                Run = RenameStyles
            },
            ["import_styles"] = new OpDef
            {
                Description = "Import styles from another DWG (style library) into the current drawing. Deep clone, dependent sub-styles come along",
                Parameters = "from(required, absolute path of the library file) filter?(style name substring, default \"@\") items?[{cat,name}](only these if given) cats?[](only search these categories) mode?(ignore|replace, default ignore = same name not overwritten) dry_run?(default true)",
                WritesDrawing = false,
                Run = ImportStyles
            },
            ["code_set_dump"] = new OpDef
            {
                Description = "List code set style mappings: which style/label style each code has. Use it to reconcile when section labels do not show",
                Parameters = "name?(default all code sets) corridor?(if given also lists the codes the corridor actually produces, marking uncovered ones)",
                WritesDrawing = false,
                Run = CodeSetDump
            },
            ["code_set_edit"] = new OpDef
            {
                Description = "Add/modify code set mappings: which style and label style a code gets. Use it to wire up links on sections that have no style or label",
                Parameters = "name(required, code set name) items(required, [{code,style?,label_style?,style_type?(link|marker|shape),remove?(true drops the code)}]) dry_run?(default true)",
                WritesDrawing = false,
                Run = CodeSetEdit
            },
            ["label_style_dump"] = new OpDef
            {
                Description = "Dissect a label style: how many components, each component's visibility/text content/layer/colour. Use it to locate why a label does not show",
                Parameters = "name(required, label style name, case-sensitive) cats?[](only search these categories, default all label categories)",
                WritesDrawing = false,
                Run = LabelStyleDump
            },
            ["dump_sections"] = new OpDef
            {
                Description = "Take one section/section view per alignment and dump every property, for old-vs-new comparison",
                Parameters = "alignments(required, alignment name array) which?(section|view|both, default both)",
                WritesDrawing = false,
                Run = DumpSections
            },
            ["api_search"] = new OpDef
            {
                Description = "Search types and members by keyword in the Civil/AutoCAD assemblies; search here first when unsure which API to use",
                Parameters = "q(required, keywords, space-separated) members?(also search members, default true) max?(default 60)",
                WritesDrawing = false,
                Run = ApiSearch
            },
            ["snoop"] = new OpDef
            {
                Description = "Reflect the real property names and current values of an object (check the API before coding, replaces manual Snoop)",
                Parameters = "type?(type name containing Alignment/Surface etc., default first match) name?(object name) handle?(handle) max?(default 80)",
                WritesDrawing = false,
                Run = Snoop
            },
        };

        public static JsonNode Execute(string op, JsonObject args, Document doc)
        {
            OpDef def;
            if (!Registry.TryGetValue(op, out def))
                throw new InvalidOperationException(
                    "Unknown operation '" + op + "'. Available: " + string.Join(", ", Registry.Keys));
            return def.Run(args ?? new JsonObject(), doc);
        }

        // ===================== Operation implementations =====================

        static JsonNode DrawingInfo(JsonObject a, Document doc)
        {
            Database db = doc.Database;
            int alignments = 0, surfaces = 0, entities = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    entities++;
                    DBObject o = tr.GetObject(id, OpenMode.ForRead);
                    if (o is CivAlignment) alignments++;
                    else if (o is CivSurface) surfaces++;
                }
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                int layers = 0;
                foreach (ObjectId _ in lt) layers++;
                tr.Commit();

                return new JsonObject
                {
                    ["file"] = SafeFile(db),
                    ["entities"] = entities,
                    ["alignments"] = alignments,
                    ["surfaces"] = surfaces,
                    ["layers"] = layers,
                    ["units"] = db.Insunits.ToString()
                };
            }
        }

        static JsonNode ListAlignments(JsonObject a, Document doc)
        {
            var arr = new JsonArray();
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var al = tr.GetObject(id, OpenMode.ForRead) as CivAlignment;
                    if (al == null) continue;
                    arr.Add(new JsonObject
                    {
                        ["name"] = al.Name,
                        ["layer"] = SafeLayer(al),
                        ["length"] = Round(al.Length, 3),
                        ["start_station"] = Round(al.StartingStation, 3),
                        ["end_station"] = Round(al.EndingStation, 3),
                        ["handle"] = al.Handle.ToString()
                    });
                }
                tr.Commit();
            }
            return arr;
        }

        static JsonNode ListSurfaces(JsonObject a, Document doc)
        {
            var arr = new JsonArray();
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var s = tr.GetObject(id, OpenMode.ForRead) as CivSurface;
                    if (s == null) continue;
                    var o = new JsonObject
                    {
                        ["name"] = s.Name,
                        ["type"] = s.GetType().Name,
                        ["layer"] = SafeLayer(s),
                        ["handle"] = s.Handle.ToString()
                    };
                    arr.Add(o);
                }
                tr.Commit();
            }
            return arr;
        }

        static JsonNode ExportStations(JsonObject a, Document doc)
        {
            double interval = GetDouble(a, "interval", 50.0);
            string format = GetString(a, "format", "both");
            string outdir = ResolveOutDir(a, doc);
            var wanted = WantedNames(a);

            var files = new JsonArray();
            var summary = new JsonArray();
            Database db = doc.Database;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var al = tr.GetObject(id, OpenMode.ForRead) as CivAlignment;
                    if (al == null) continue;
                    if (wanted != null && !wanted.Contains(al.Name)) continue;

                    var rows = new List<object[]>();
                    int i = 0;
                    foreach (double st in Stations(al.StartingStation, al.EndingStation, interval))
                    {
                        double e = 0, n = 0;
                        try { al.PointLocation(st, 0.0, ref e, ref n); } catch { continue; }
                        i++;
                        rows.Add(new object[] { i, Station(st), Round2(n), Round2(e) });
                    }

                    string baseName = string.Format("AlignmentCoords_{0}_{1:0}m", Sanitize(al.Name), interval);
                    foreach (string p in Excel.Write(outdir, baseName, HeadersXY, rows, format))
                        files.Add(p);

                    summary.Add(new JsonObject
                    {
                        ["alignment"] = al.Name,
                        ["points"] = rows.Count,
                        ["start_station"] = Round(al.StartingStation, 3),
                        ["end_station"] = Round(al.EndingStation, 3)
                    });
                }
                tr.Commit();
            }

            if (summary.Count == 0)
                throw new InvalidOperationException("No matching alignment. Run list_alignments first to see the names.");

            return new JsonObject { ["exported"] = summary, ["files"] = files, ["outdir"] = outdir };
        }

        static JsonNode StationElevations(JsonObject a, Document doc)
        {
            string alName = GetString(a, "alignment", null);
            string sfName = GetString(a, "surface", null);
            if (string.IsNullOrEmpty(alName)) throw new InvalidOperationException("Missing parameter alignment.");
            if (string.IsNullOrEmpty(sfName)) throw new InvalidOperationException("Missing parameter surface.");
            double interval = GetDouble(a, "interval", 50.0);
            string format = GetString(a, "format", "both");
            string outdir = ResolveOutDir(a, doc);

            Database db = doc.Database;
            var rows = new List<object[]>();
            int hit = 0, miss = 0;
            string usedAl = null, usedSf = null;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = null;
                CivSurface sf = null;
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    DBObject o = tr.GetObject(id, OpenMode.ForRead);
                    if (al == null && o is CivAlignment && ((CivAlignment)o).Name == alName) al = (CivAlignment)o;
                    else if (sf == null && o is CivSurface && ((CivSurface)o).Name == sfName) sf = (CivSurface)o;
                    if (al != null && sf != null) break;
                }
                if (al == null) throw new InvalidOperationException("Alignment '" + alName + "' not found.");
                if (sf == null) throw new InvalidOperationException("Surface '" + sfName + "' not found.");
                usedAl = al.Name; usedSf = sf.Name;

                int i = 0;
                foreach (double st in Stations(al.StartingStation, al.EndingStation, interval))
                {
                    double e = 0, n = 0;
                    try { al.PointLocation(st, 0.0, ref e, ref n); } catch { continue; }
                    i++;
                    object elev;
                    try { elev = Math.Round(sf.FindElevationAtXY(e, n), 3); hit++; }
                    catch { elev = null; miss++; }   // point outside the surface
                    rows.Add(new object[] { i, Station(st), Round2(n), Round2(e), elev });
                }
                tr.Commit();
            }

            string baseName = string.Format("AlignmentGroundElev_{0}_{1}_{2:0}m",
                Sanitize(usedAl), Sanitize(usedSf), interval);
            var files = new JsonArray();
            foreach (string p in Excel.Write(outdir, baseName, HeadersXYZ, rows, format)) files.Add(p);

            return new JsonObject
            {
                ["alignment"] = usedAl,
                ["surface"] = usedSf,
                ["points"] = rows.Count,
                ["on_surface"] = hit,
                ["off_surface"] = miss,
                ["files"] = files,
                ["outdir"] = outdir
            };
        }

        static JsonNode ExportSurfaceGrid(JsonObject a, Document doc)
        {
            string sfName = GetString(a, "surface", null);
            string outPath = GetString(a, "out", null);
            if (string.IsNullOrEmpty(sfName)) throw new InvalidOperationException("Missing parameter surface.");
            if (string.IsNullOrEmpty(outPath)) throw new InvalidOperationException("Missing parameter out.");
            double minx = GetDouble(a, "minx", double.NaN), miny = GetDouble(a, "miny", double.NaN);
            double maxx = GetDouble(a, "maxx", double.NaN), maxy = GetDouble(a, "maxy", double.NaN);
            if (double.IsNaN(minx) || double.IsNaN(miny) || double.IsNaN(maxx) || double.IsNaN(maxy))
                throw new InvalidOperationException("Missing parameters minx/miny/maxx/maxy.");
            if (maxx <= minx || maxy <= miny) throw new InvalidOperationException("Invalid extent: max must be greater than min.");
            double step = GetDouble(a, "step", 10.0);
            if (step <= 0) throw new InvalidOperationException("step must be greater than 0.");
            bool overwrite = GetBool(a, "overwrite", false);
            if (File.Exists(outPath) && !overwrite)
                throw new InvalidOperationException("Output already exists and overwrite:true not given: " + outPath);

            Database db = doc.Database;
            long hit = 0, miss = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivSurface sf = null;
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var o = tr.GetObject(id, OpenMode.ForRead) as CivSurface;
                    if (o != null && o.Name == sfName) { sf = o; break; }
                }
                if (sf == null) throw new InvalidOperationException("Surface '" + sfName + "' not found. Run list_surfaces first to see the names.");

                using (var w = new StreamWriter(outPath, false, System.Text.Encoding.UTF8))
                {
                    w.WriteLine("x,y,z");
                    for (double y = miny; y <= maxy; y += step)
                        for (double x = minx; x <= maxx; x += step)
                        {
                            double z;
                            try { z = sf.FindElevationAtXY(x, y); }
                            catch { miss++; continue; }   // point outside the surface
                            hit++;
                            w.WriteLine(x.ToString("0.###", CultureInfo.InvariantCulture) + ","
                                      + y.ToString("0.###", CultureInfo.InvariantCulture) + ","
                                      + z.ToString("0.###", CultureInfo.InvariantCulture));
                        }
                }
                tr.Commit();
            }
            if (hit == 0) throw new InvalidOperationException("No sample point in the extent falls on the surface; the extent or surface name may be wrong.");
            return new JsonObject
            {
                ["surface"] = sfName,
                ["out"] = outPath,
                ["step"] = step,
                ["on_surface"] = hit,
                ["off_surface"] = miss
            };
        }

        // ---------- Civil 3D inventory probe ----------
        // Key question: can CivilDocument be obtained inside accoreconsole?
        // No -> objects can only be read (the v1 approach); yes -> the alignment/corridor/quantity chain becomes possible.
        // Try both paths, keep whichever works, and report both results.
        static JsonNode CivilEnv(JsonObject a, Document doc)
        {
            int max = (int)GetDouble(a, "max", 40);
            Database db = doc.Database;
            var res = new JsonObject();

            CivDoc civ = null;
            var access = new JsonObject();
            try
            {
                CivDoc c = CivApp.ActiveDocument;
                access["CivilApplication.ActiveDocument"] = c != null ? "ok" : "null";
                if (c != null) civ = c;
            }
            catch (System.Exception ex)
            { access["CivilApplication.ActiveDocument"] = ex.GetType().Name + ": " + Truncate(ex.Message, 120); }

            try
            {
                CivDoc c = CivDoc.GetCivilDocument(db);
                access["CivilDocument.GetCivilDocument(db)"] = c != null ? "ok" : "null";
                if (c != null && civ == null) civ = c;
            }
            catch (System.Exception ex)
            { access["CivilDocument.GetCivilDocument(db)"] = ex.GetType().Name + ": " + Truncate(ex.Message, 120); }

            res["civil_document"] = access;
            res["usable"] = civ != null;

            // Civil objects in model space (independent of CivilDocument; v1 always used this path)
            var assemblies = new JsonArray();
            var corridors = new JsonArray();
            var alignments = new JsonArray();
            var surfaces = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    DBObject o;
                    try { o = tr.GetObject(id, OpenMode.ForRead); } catch { continue; }
                    if (o is Autodesk.Civil.DatabaseServices.Assembly)
                    {
                        var asm = (Autodesk.Civil.DatabaseServices.Assembly)o;
                        // Assembly -> group (AssemblyGroup) -> GetSubassemblyIds()
                        var subs = new JsonArray();
                        try
                        {
                            foreach (Autodesk.Civil.DatabaseServices.AssemblyGroup g in asm.Groups)
                                foreach (ObjectId sid in g.GetSubassemblyIds())
                                {
                                    var sa = tr.GetObject(sid, OpenMode.ForRead)
                                             as Autodesk.Civil.DatabaseServices.Subassembly;
                                    if (sa == null) continue;
                                    // Status is the key: Subassembly Composer parts reference .pkt by path;
                                    // when the path is broken Status=FileNotFound, the corridor still builds but has **no geometry at all**
                                    subs.Add(new JsonObject
                                    {
                                        ["name"] = sa.Name,
                                        ["status"] = sa.StatusOf(),
                                        ["from_composer"] = sa.IsComposer()
                                    });
                                }
                        }
                        catch (System.Exception ex) { subs.Add("(read failed: " + ex.GetType().Name + ")"); }
                        assemblies.Add(new JsonObject { ["name"] = asm.Name, ["subassemblies"] = subs });
                    }
                    else if (o is Autodesk.Civil.DatabaseServices.Corridor)
                        corridors.Add(((Autodesk.Civil.DatabaseServices.Corridor)o).Name);
                    else if (o is CivAlignment) alignments.Add(((CivAlignment)o).Name);
                    else if (o is CivSurface) surfaces.Add(((CivSurface)o).Name);
                }
                tr.Commit();
            }
            res["assemblies"] = assemblies;
            res["corridors"] = corridors;
            res["alignments"] = alignments;
            res["surfaces"] = surfaces;

            if (civ == null) return res;

            // Styles and criteria: only readable once CivilDocument is available
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var styles = new JsonObject();
                Action<string, Func<object>> take = (key, getter) =>
                {
                    try { styles[key] = StyleNames(tr, getter(), max); }
                    catch (System.Exception ex) { styles[key] = ex.GetType().Name + ": " + Truncate(ex.Message, 100); }
                };
                take("alignment_styles", () => civ.Styles.AlignmentStyles);
                take("profile_styles", () => civ.Styles.ProfileStyles);
                take("surface_styles", () => civ.Styles.SurfaceStyles);
                take("sample_line_styles", () => civ.Styles.SampleLineStyles);
                take("assembly_styles", () => civ.Styles.AssemblyStyles);
                take("qto_criteria", () => civ.Styles.QuantityTakeoffCriterias);
                take("alignment_label_sets", () => civ.Styles.LabelSetStyles.AlignmentLabelSetStyles);
                take("profile_label_sets", () => civ.Styles.LabelSetStyles.ProfileLabelSetStyles);
                res["styles"] = styles;

                try
                {
                    var sites = new JsonArray();
                    foreach (ObjectId sid in civ.GetSiteIds())
                    {
                        var s = tr.GetObject(sid, OpenMode.ForRead);
                        string nm = TryGetName(s);
                        if (nm != null) sites.Add(nm);
                    }
                    res["sites"] = sites;
                }
                catch (System.Exception ex) { res["sites"] = ex.GetType().Name + ": " + Truncate(ex.Message, 100); }

                tr.Commit();
            }
            return res;
        }

        // ---- list_styles: reflection walk of the whole Styles tree ----
        //
        // Why not hand-write category names like civil_env: Civil 3D's CivilDocument.Styles is a nested tree
        // (StylesRoot -> LabelStyles -> AlignmentLabelStyles -> StationLabelStyles -> ...);
        // a hand-written list reads one level only and the missed categories are invisible in the result. Only a reflection walk guarantees "all".
        //
        // Rule: if the property value is IEnumerable -> treat as a style collection and collect names; otherwise if the type is in the Autodesk.Civil namespace
        // -> treat as a group and recurse. A reference set guards against cycles, depth is the fallback.
        static JsonNode ListStyles(JsonObject a, Document doc)
        {
            int max = (int)GetDouble(a, "max", 500);
            int maxDepth = (int)GetDouble(a, "depth", 6);
            bool keepEmpty = GetBool(a, "empty", false);
            string filter = GetString(a, "filter", null);

            Database db = doc.Database;
            CivDoc civ = null;
            try { civ = CivDoc.GetCivilDocument(db); } catch { }
            if (civ == null) { try { civ = CivApp.ActiveDocument; } catch { } }
            if (civ == null) throw new InvalidOperationException("CivilDocument unavailable, cannot read styles.");

            var styles = new JsonObject();
            var errors = new JsonObject();
            int totalCollections = 0, totalStyles = 0, matched = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var seen = new List<object>();
                var stack = new List<KeyValuePair<string, object>>();
                stack.Add(new KeyValuePair<string, object>("", civ.Styles));

                while (stack.Count > 0)
                {
                    var cur = stack[0]; stack.RemoveAt(0);
                    string path = cur.Key;
                    object node = cur.Value;
                    if (node == null) continue;
                    if (path.Length > 0 && path.Split('.').Length > maxDepth) continue;

                    bool dup = false;
                    foreach (object s in seen) if (ReferenceEquals(s, node)) { dup = true; break; }
                    if (dup) continue;
                    seen.Add(node);

                    var props = node.GetType().GetProperties(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    foreach (var p in props)
                    {
                        if (!p.CanRead) continue;
                        if (p.GetIndexParameters().Length > 0) continue;   // indexers are not categories
                        string key = path.Length == 0 ? p.Name : path + "." + p.Name;

                        object val;
                        try { val = p.GetValue(node, null); }
                        catch (System.Exception ex)
                        {
                            errors[key] = ex.GetType().Name + ": "
                                        + Truncate(ex.InnerException != null ? ex.InnerException.Message : ex.Message, 100);
                            continue;
                        }
                        if (val == null) continue;

                        Type vt = val.GetType();
                        if (val is string || vt.IsPrimitive || vt.IsEnum) continue;

                        if (val is System.Collections.IEnumerable)
                        {
                            JsonArray names;
                            try { names = StyleNames(tr, val, max); }
                            catch (System.Exception ex)
                            { errors[key] = ex.GetType().Name + ": " + Truncate(ex.Message, 100); continue; }

                            totalCollections++;
                            totalStyles += names.Count;

                            if (filter != null)
                            {
                                var hit = new JsonArray();
                                foreach (var n in names)
                                {
                                    string s = n == null ? "" : n.ToString();
                                    if (s.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                                        hit.Add(s);
                                }
                                matched += hit.Count;
                                if (hit.Count > 0 || keepEmpty) styles[key] = hit;
                            }
                            else if (names.Count > 0 || keepEmpty) styles[key] = names;
                        }
                        else if (vt.FullName != null && vt.FullName.StartsWith("Autodesk.Civil"))
                        {
                            // Only descend into "groups", never into "single style objects": LabelStyles.DefaultLabelStyle
                            // is a concrete style; descending hits a pile of .Overridden properties which throw
                            // TargetInvocationException("This property is not overridable") for non-override items,
                            // flooding the output with 30+ bogus errors.
                            // The rule uses the type-name suffix Root (StylesRoot / LabelStyleRoot / BandStyleRoot /
                            // TableStyleRoot ... every group has this suffix); TryGetName was tried but
                            // DefaultLabelStyle yields no name here, so it cannot filter.
                            if (!vt.Name.EndsWith("Root")) continue;
                            stack.Add(new KeyValuePair<string, object>(key, val));
                        }
                    }
                }
                tr.Commit();
            }

            var res = new JsonObject
            {
                ["collections_found"] = totalCollections,
                ["styles_total"] = totalStyles
            };
            if (filter != null) { res["filter"] = filter; res["styles_matched"] = matched; }
            res["styles"] = styles;
            if (errors.Count > 0) res["unreadable"] = errors;
            return res;
        }

        // Walk the list_styles category path (e.g. "LabelStyles.ProfileLabelStyles.MajorStationLabelStyles")
        // property by property to get the style collection object. Returns null if unreachable.
        static object ResolveStyleCollection(CivDoc civ, string path)
        {
            object node = civ.Styles;
            foreach (string seg in path.Split('.'))
            {
                if (node == null) return null;
                var p = node.GetType().GetProperty(seg,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (p == null || !p.CanRead) return null;
                try { node = p.GetValue(node, null); }
                catch { return null; }
            }
            return node;
        }

        // ---- delete_styles ----
        //
        // Delete only styles exactly matched by "category path + name". Two safety lines:
        //   1. default dry_run=true: resolve and report only;
        //   2. even a real delete only changes memory; disk needs save_dwg, which defaults to save-as.
        // Styles referenced by other styles/objects cannot be deleted and Civil throws; record the error as is, never swallow, never force.
        // Multiple passes: only after the parent (e.g. a label group) is deleted can the child styles it referenced be deleted.
        static JsonNode DeleteStyles(JsonObject a, Document doc)
        {
            var items = a["items"] as JsonArray;
            if (items == null || items.Count == 0)
                throw new InvalidOperationException("delete_styles requires items:[{cat,name},...]");
            bool dry = GetBool(a, "dry_run", true);
            int passes = (int)GetDouble(a, "passes", 3);

            Database db = doc.Database;
            CivDoc civ = null;
            try { civ = CivDoc.GetCivilDocument(db); } catch { }
            if (civ == null) throw new InvalidOperationException("CivilDocument unavailable.");

            // Target list -> (cat, name)
            var targets = new List<string[]>();
            foreach (JsonNode it in items)
            {
                var o = it as JsonObject;
                if (o == null) continue;
                string cat = o["cat"] != null ? o["cat"].ToString() : null;
                string nm = o["name"] != null ? o["name"].ToString() : null;
                if (cat != null && nm != null) targets.Add(new string[] { cat, nm });
            }

            var done = new JsonArray();
            var failed = new JsonArray();
            var notfound = new JsonArray();
            int deleted = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var pending = targets;
                for (int pass = 1; pass <= passes && pending.Count > 0; pass++)
                {
                    var stillPending = new List<string[]>();
                    var lastErr = new Dictionary<string, string>();

                    foreach (string[] t in pending)
                    {
                        object coll = ResolveStyleCollection(civ, t[0]);
                        var en = coll as System.Collections.IEnumerable;
                        if (en == null)
                        {
                            if (pass == passes)
                                notfound.Add(new JsonObject
                                { ["cat"] = t[0], ["name"] = t[1], ["why"] = "category path does not resolve to a collection" });
                            continue;
                        }

                        ObjectId hit = ObjectId.Null;
                        foreach (object item in en)
                        {
                            if (!(item is ObjectId)) continue;
                            ObjectId oid = (ObjectId)item;
                            if (oid.IsErased) continue;
                            try
                            {
                                if (TryGetName(tr.GetObject(oid, OpenMode.ForRead)) == t[1])
                                { hit = oid; break; }
                            }
                            catch { }
                        }

                        if (hit.IsNull)
                        {
                            if (pass == passes)
                                notfound.Add(new JsonObject
                                { ["cat"] = t[0], ["name"] = t[1], ["why"] = "no such name in the collection" });
                            continue;
                        }

                        if (dry)
                        {
                            done.Add(new JsonObject
                            { ["cat"] = t[0], ["name"] = t[1], ["handle"] = hit.Handle.ToString() });
                            deleted++;
                            continue;
                        }

                        try
                        {
                            DBObject o = tr.GetObject(hit, OpenMode.ForWrite);
                            o.Erase();
                            done.Add(new JsonObject
                            { ["cat"] = t[0], ["name"] = t[1], ["pass"] = pass });
                            deleted++;
                        }
                        catch (System.Exception ex)
                        {
                            stillPending.Add(t);
                            lastErr[t[0] + "||" + t[1]] =
                                ex.GetType().Name + ": " + Truncate(ex.Message, 120);
                        }
                    }

                    if (pass == passes || stillPending.Count == 0)
                    {
                        foreach (string[] t in stillPending)
                            failed.Add(new JsonObject
                            {
                                ["cat"] = t[0],
                                ["name"] = t[1],
                                ["error"] = lastErr[t[0] + "||" + t[1]]
                            });
                    }
                    pending = stillPending;
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["dry_run"] = dry,
                ["requested"] = targets.Count,
                ["deleted"] = deleted,
                ["failed_count"] = failed.Count,
                ["notfound_count"] = notfound.Count,
                ["failed"] = failed,
                ["notfound"] = notfound,
                ["done"] = done
            };
        }

        // Walk every display component of every style and hand it to the callback.
        // skipCats contains AssemblyStyles by default: scanning it hard-crashes the process with AccessViolation (.NET cannot catch it).
        static void ForEachDisplay(Database db, Transaction tr, CivDoc civ,
                                   List<string> skipCats, string nameFilter,
                                   Action<string, string, object, DBObject> visit)
        {
            var stack = new List<KeyValuePair<string, object>>();
            stack.Add(new KeyValuePair<string, object>("", civ.Styles));
            var seen = new List<object>();
            var collections = new List<KeyValuePair<string, object>>();

            while (stack.Count > 0)
            {
                var cur = stack[0]; stack.RemoveAt(0);
                if (cur.Value == null) continue;
                bool dup = false;
                foreach (object s in seen) if (ReferenceEquals(s, cur.Value)) { dup = true; break; }
                if (dup) continue;
                seen.Add(cur.Value);
                foreach (var p in cur.Value.GetType().GetProperties(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                    string key = cur.Key.Length == 0 ? p.Name : cur.Key + "." + p.Name;
                    object val;
                    try { val = p.GetValue(cur.Value, null); } catch { continue; }
                    if (val == null) continue;
                    Type vt = val.GetType();
                    if (val is string || vt.IsPrimitive || vt.IsEnum) continue;
                    if (val is System.Collections.IEnumerable)
                        collections.Add(new KeyValuePair<string, object>(key, val));
                    else if (vt.FullName != null && vt.FullName.StartsWith("Autodesk.Civil")
                             && vt.Name.EndsWith("Root"))
                        stack.Add(new KeyValuePair<string, object>(key, val));
                }
            }

            foreach (var kv in collections)
            {
                bool skip = false;
                foreach (string sc in skipCats)
                    if (kv.Key == sc || kv.Key.EndsWith("." + sc)) { skip = true; break; }
                if (skip) continue;
                var en = kv.Value as System.Collections.IEnumerable;
                if (en == null) continue;
                foreach (object item in en)
                {
                    if (!(item is ObjectId)) continue;
                    ObjectId oid = (ObjectId)item;
                    if (oid.IsErased) continue;
                    DBObject so;
                    try { so = tr.GetObject(oid, OpenMode.ForRead); } catch { continue; }
                    string nm = TryGetName(so);
                    if (nm == null) continue;
                    if (nameFilter != null &&
                        nm.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    foreach (var m in so.GetType().GetMethods(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    {
                        if (!m.Name.StartsWith("GetDisplayStyle")) continue;
                        var ps = m.GetParameters();
                        if (ps.Length != 1 || !ps[0].ParameterType.IsEnum) continue;
                        Type et = ps[0].ParameterType;
                        foreach (string cn in Enum.GetNames(et))
                        {
                            object ds;
                            try { ds = m.Invoke(so, new object[] { Enum.Parse(et, cn) }); }
                            catch { continue; }
                            if (ds == null) continue;
                            visit(kv.Key, nm, ds, so);
                        }
                    }
                }
            }
        }

        static List<string> SkipCats(JsonObject a)
        {
            var skip = new List<string>();
            var arr = a["skip_cats"] as JsonArray;
            if (arr == null) skip.Add("AssemblyStyles");
            else foreach (JsonNode n in arr) if (n != null) skip.Add(n.ToString());
            return skip;
        }

        static JsonNode LayersEdit(JsonObject a, Document doc)
        {
            bool dry = GetBool(a, "dry_run", true);
            bool sync = GetBool(a, "sync_styles", true);
            var skip = SkipCats(a);
            Database db = doc.Database;
            CivDoc civ = null;
            try { civ = CivDoc.GetCivilDocument(db); } catch { }

            var created = new JsonArray();
            var renamed = new JsonArray();
            var deleted = new JsonArray();
            var failed = new JsonArray();
            int styleRefsPatched = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId,
                    dry ? OpenMode.ForRead : OpenMode.ForWrite);

                // --- create ---
                var arr = a["create"] as JsonArray;
                if (arr != null)
                    foreach (JsonNode n in arr)
                    {
                        var o = n as JsonObject; if (o == null) continue;
                        string nm = o["name"].ToString();
                        if (lt.Has(nm)) { created.Add(nm + " (already exists, skipped)"); continue; }
                        if (dry) { created.Add(nm + " (dry run)"); continue; }
                        var ltr = new LayerTableRecord { Name = nm };
                        if (o["color"] != null) ltr.Color = ParseColor(o["color"].ToString());
                        lt.Add(ltr);
                        tr.AddNewlyCreatedDBObject(ltr, true);
                        created.Add(nm);
                    }

                // --- rename (with style string sync) ---
                var ren = a["rename"] as JsonArray;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (ren != null)
                    foreach (JsonNode n in ren)
                    {
                        var o = n as JsonObject; if (o == null) continue;
                        string from = o["from"].ToString(), to = o["to"].ToString();
                        if (!lt.Has(from))
                        { failed.Add(new JsonObject { ["op"] = "rename", ["name"] = from, ["why"] = "layer does not exist" }); continue; }
                        if (lt.Has(to))
                        { failed.Add(new JsonObject { ["op"] = "rename", ["name"] = from, ["why"] = "target name " + to + " already exists" }); continue; }
                        map[from] = to;
                        if (dry) { renamed.Add(from + " -> " + to + " (dry run)"); continue; }
                        try
                        {
                            var ltr = (LayerTableRecord)tr.GetObject(lt[from], OpenMode.ForWrite);
                            ltr.Name = to;
                            renamed.Add(from + " → " + to);
                        }
                        catch (System.Exception ex)
                        { failed.Add(new JsonObject { ["op"] = "rename", ["name"] = from, ["why"] = ex.GetType().Name + ": " + Truncate(ex.Message, 90) }); }
                    }

                // Layer strings inside styles follow the rename (skipping this step breaks the links)
                if (sync && map.Count > 0 && civ != null)
                {
                    ForEachDisplay(db, tr, civ, skip, null, delegate (string cat, string sname, object ds, DBObject so)
                    {
                        var p = ds.GetType().GetProperty("Layer");
                        if (p == null) return;
                        object v;
                        try { v = p.GetValue(ds, null); } catch { return; }
                        string cur = v as string;
                        if (cur == null || !map.ContainsKey(cur)) return;
                        styleRefsPatched++;
                        if (dry) return;
                        try { so.UpgradeOpen(); } catch { }
                        try { p.SetValue(ds, map[cur], null); } catch { }
                    });
                }

                // --- delete ---
                var del = a["delete"] as JsonArray;
                if (del != null)
                    foreach (JsonNode n in del)
                    {
                        if (n == null) continue;
                        string nm = n.ToString();
                        if (!lt.Has(nm))
                        { failed.Add(new JsonObject { ["op"] = "delete", ["name"] = nm, ["why"] = "layer does not exist" }); continue; }
                        if (dry) { deleted.Add(nm + " (dry run)"); continue; }
                        try
                        {
                            var ltr = (LayerTableRecord)tr.GetObject(lt[nm], OpenMode.ForWrite);
                            ltr.Erase();
                            deleted.Add(nm);
                        }
                        catch (System.Exception ex)
                        { failed.Add(new JsonObject { ["op"] = "delete", ["name"] = nm, ["why"] = ex.GetType().Name + ": " + Truncate(ex.Message, 90) }); }
                    }

                tr.Commit();
            }

            return new JsonObject
            {
                ["dry_run"] = dry,
                ["created"] = created,
                ["renamed"] = renamed,
                ["deleted"] = deleted,
                ["style_refs_patched"] = styleRefsPatched,
                ["failed"] = failed
            };
        }

        static JsonNode StylesNormalize(JsonObject a, Document doc)
        {
            bool dry = GetBool(a, "dry_run", true);
            string filter = GetString(a, "filter", null);
            bool toByLayer = GetBool(a, "color_bylayer", false);
            var skip = SkipCats(a);

            // {style,from,to}: reassign components with layer==from in a style to layer to
            var moves = new List<string[]>();
            var mv = a["layer_moves"] as JsonArray;
            if (mv != null)
                foreach (JsonNode n in mv)
                {
                    var o = n as JsonObject; if (o == null) continue;
                    moves.Add(new string[]{
                        o["style"] == null ? null : o["style"].ToString(),
                        o["from"] == null ? "0" : o["from"].ToString(),
                        o["to"] == null ? null : o["to"].ToString() });
                }

            Database db = doc.Database;
            CivDoc civ = null;
            try { civ = CivDoc.GetCivilDocument(db); } catch { }
            if (civ == null) throw new InvalidOperationException("CivilDocument unavailable.");

            int colorChanged = 0, layerChanged = 0, visited = 0;
            var byStyle = new JsonObject();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ForEachDisplay(db, tr, civ, skip, filter,
                delegate (string cat, string sname, object ds, DBObject so)
                {
                    visited++;
                    Type t = ds.GetType();

                    if (toByLayer)
                    {
                        var pc = t.GetProperty("Color");
                        if (pc != null)
                        {
                            object v = null;
                            try { v = pc.GetValue(ds, null); } catch { }
                            var col = v as Autodesk.AutoCAD.Colors.Color;
                            if (col != null && !col.IsByLayer)
                            {
                                colorChanged++;
                                Bump(byStyle, sname, "color");
                                if (!dry)
                                {
                                    try { so.UpgradeOpen(); } catch { }
                                    try { pc.SetValue(ds, ParseColor("ByLayer"), null); } catch { }
                                }
                            }
                        }
                    }

                    if (moves.Count > 0)
                    {
                        var pl = t.GetProperty("Layer");
                        if (pl != null)
                        {
                            object v = null;
                            try { v = pl.GetValue(ds, null); } catch { }
                            string cur = v as string;
                            if (cur != null)
                                foreach (string[] m in moves)
                                {
                                    if (m[2] == null) continue;
                                    if (m[0] != null && m[0] != sname) continue;
                                    if (cur != m[1]) continue;
                                    layerChanged++;
                                    Bump(byStyle, sname, "layer");
                                    if (!dry)
                                    {
                                        try { so.UpgradeOpen(); } catch { }
                                        try { pl.SetValue(ds, m[2], null); } catch { }
                                    }
                                    break;
                                }
                        }
                    }
                });
                tr.Commit();
            }

            return new JsonObject
            {
                ["dry_run"] = dry,
                ["filter"] = filter,
                ["components_visited"] = visited,
                ["color_set_bylayer"] = colorChanged,
                ["layer_reassigned"] = layerChanged,
                ["by_style"] = byStyle
            };
        }

        static void Bump(JsonObject o, string style, string kind)
        {
            var cell = o[style] as JsonObject;
            if (cell == null) { cell = new JsonObject(); o[style] = cell; }
            int n = cell[kind] != null ? (int)cell[kind] : 0;
            cell[kind] = n + 1;
        }

        static JsonNode RenameStyles(JsonObject a, Document doc)
        {
            bool dry = GetBool(a, "dry_run", true);
            var items = a["items"] as JsonArray;
            if (items == null || items.Count == 0)
                throw new InvalidOperationException("rename_styles requires items:[{cat,from,to}]");

            Database db = doc.Database;
            CivDoc civ = null;
            try { civ = CivDoc.GetCivilDocument(db); } catch { }
            if (civ == null) throw new InvalidOperationException("CivilDocument unavailable.");

            var done = new JsonArray();
            var failed = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (JsonNode n in items)
                {
                    var o = n as JsonObject; if (o == null) continue;
                    string cat = o["cat"].ToString(), from = o["from"].ToString(), to = o["to"].ToString();
                    object coll = ResolveStyleCollection(civ, cat);
                    var en = coll as System.Collections.IEnumerable;
                    if (en == null)
                    { failed.Add(new JsonObject { ["from"] = from, ["why"] = "category path does not resolve: " + cat }); continue; }

                    DBObject hit = null;
                    foreach (object item in en)
                    {
                        if (!(item is ObjectId)) continue;
                        ObjectId oid = (ObjectId)item;
                        if (oid.IsErased) continue;
                        DBObject so;
                        try { so = tr.GetObject(oid, OpenMode.ForRead); } catch { continue; }
                        if (TryGetName(so) == from) { hit = so; break; }
                    }
                    if (hit == null)
                    { failed.Add(new JsonObject { ["from"] = from, ["why"] = "no such style in this category" }); continue; }
                    if (dry) { done.Add(from + " -> " + to + " (dry run)"); continue; }

                    // Name is re-declared with new in derived classes and may lack a setter; walk up the base classes to the one that really has set
                    bool ok = false; string err = null;
                    var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                              | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
                    for (Type t = hit.GetType(); t != null && !ok; t = t.BaseType)
                    {
                        var p = t.GetProperty("Name", flags);
                        if (p == null) continue;
                        var setter = p.GetSetMethod(true);
                        if (setter == null) continue;
                        try
                        {
                            hit.UpgradeOpen();
                            setter.Invoke(hit, new object[] { to });
                            ok = true;
                        }
                        catch (System.Exception ex)
                        { err = ex.GetType().Name + ": " + Truncate(ex.InnerException != null ? ex.InnerException.Message : ex.Message, 90); }
                    }
                    if (ok) done.Add(from + " → " + to);
                    else failed.Add(new JsonObject { ["from"] = from, ["why"] = err ?? "no writable Name property found" });
                }
                tr.Commit();
            }
            return new JsonObject { ["dry_run"] = dry, ["done"] = done, ["failed"] = failed };
        }

        // ---- import_styles: bring styles over from a style library DWG ----
        //
        // Approach: open the library file as a side database, find the source style's ObjectId by "category path + name",
        // then WblockCloneObjects it into the **owner dictionary of the same category collection** in the target drawing.
        // Deep clone because Civil styles reference each other (code set -> link/marker/shape styles, section view style -> band set...);
        // moving just the shell would miss dependencies. The target dictionary comes from "the OwnerId of any existing style in that collection";
        // every collection has at least a Standard entry, so this path is reliable.
        static JsonNode ImportStyles(JsonObject a, Document doc)
        {
            string from = GetString(a, "from", null);
            if (from == null) throw new InvalidOperationException("import_styles requires from (library file path)");
            if (!File.Exists(from)) throw new InvalidOperationException("Library file not found: " + from);
            bool dry = GetBool(a, "dry_run", true);
            string filter = GetString(a, "filter", "@");
            string mode = GetString(a, "mode", "ignore");
            var wantItems = a["items"] as JsonArray;
            var onlyCats = a["cats"] as JsonArray;

            Database dstDb = doc.Database;
            CivDoc dstCiv = null;
            try { dstCiv = CivDoc.GetCivilDocument(dstDb); } catch { }
            if (dstCiv == null) throw new InvalidOperationException("CivilDocument unavailable for the target drawing.");

            var planned = new JsonArray();
            var done = new JsonArray();
            var skipped = new JsonArray();
            var failed = new JsonArray();

            using (var srcDb = new Database(false, true))
            {
                srcDb.ReadDwgFile(from, FileOpenMode.OpenForReadAndAllShare, true, null);
                srcDb.CloseInput(true);

                CivDoc srcCiv = null;
                try { srcCiv = CivDoc.GetCivilDocument(srcDb); } catch { }
                if (srcCiv == null)
                    throw new InvalidOperationException("CivilDocument unavailable for the library file (is it a Civil drawing?)");

                // Enumerate all style collections in the source library (reusing the Root-suffix rule)
                var srcCollections = new List<KeyValuePair<string, object>>();
                {
                    var stack = new List<KeyValuePair<string, object>>();
                    stack.Add(new KeyValuePair<string, object>("", srcCiv.Styles));
                    var seen = new List<object>();
                    while (stack.Count > 0)
                    {
                        var cur = stack[0]; stack.RemoveAt(0);
                        if (cur.Value == null) continue;
                        bool dup = false;
                        foreach (object s in seen) if (ReferenceEquals(s, cur.Value)) { dup = true; break; }
                        if (dup) continue;
                        seen.Add(cur.Value);
                        foreach (var p in cur.Value.GetType().GetProperties(
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        {
                            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                            string key = cur.Key.Length == 0 ? p.Name : cur.Key + "." + p.Name;
                            object val;
                            try { val = p.GetValue(cur.Value, null); } catch { continue; }
                            if (val == null) continue;
                            Type vt = val.GetType();
                            if (val is string || vt.IsPrimitive || vt.IsEnum) continue;
                            if (val is System.Collections.IEnumerable)
                                srcCollections.Add(new KeyValuePair<string, object>(key, val));
                            else if (vt.FullName != null && vt.FullName.StartsWith("Autodesk.Civil")
                                     && vt.Name.EndsWith("Root"))
                                stack.Add(new KeyValuePair<string, object>(key, val));
                        }
                    }
                }

                // Pick the styles to import per category
                var perCat = new Dictionary<string, List<KeyValuePair<ObjectId, string>>>();
                using (Transaction str = srcDb.TransactionManager.StartTransaction())
                {
                    foreach (var kv in srcCollections)
                    {
                        if (kv.Key == "AssemblyStyles") continue;   // touching it crashes, and it is not needed
                        if (onlyCats != null)
                        {
                            bool want = false;
                            foreach (JsonNode c in onlyCats)
                                if (c != null && c.ToString() == kv.Key) { want = true; break; }
                            if (!want) continue;
                        }
                        var en = kv.Value as System.Collections.IEnumerable;
                        if (en == null) continue;
                        foreach (object item in en)
                        {
                            if (!(item is ObjectId)) continue;
                            ObjectId oid = (ObjectId)item;
                            if (oid.IsErased) continue;
                            string nm;
                            try { nm = TryGetName(str.GetObject(oid, OpenMode.ForRead)); } catch { continue; }
                            if (nm == null) continue;

                            bool take;
                            if (wantItems != null)
                            {
                                take = false;
                                foreach (JsonNode n in wantItems)
                                {
                                    var o = n as JsonObject; if (o == null) continue;
                                    if (o["cat"].ToString() == kv.Key && o["name"].ToString() == nm)
                                    { take = true; break; }
                                }
                            }
                            else
                                take = filter == null ||
                                       nm.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

                            if (!take) continue;
                            if (!perCat.ContainsKey(kv.Key))
                                perCat[kv.Key] = new List<KeyValuePair<ObjectId, string>>();
                            perCat[kv.Key].Add(new KeyValuePair<ObjectId, string>(oid, nm));
                        }
                    }
                    str.Commit();
                }

                // Skip styles that already exist in the target drawing (same category, same name)
                var existing = new Dictionary<string, List<string>>();
                using (Transaction dtr = dstDb.TransactionManager.StartTransaction())
                {
                    foreach (string cat in perCat.Keys)
                    {
                        var lst = new List<string>();
                        var dcoll = ResolveStyleCollection(dstCiv, cat) as System.Collections.IEnumerable;
                        if (dcoll != null)
                            foreach (object item in dcoll)
                            {
                                if (!(item is ObjectId)) continue;
                                ObjectId oid = (ObjectId)item;
                                if (oid.IsErased) continue;
                                string nm;
                                try { nm = TryGetName(dtr.GetObject(oid, OpenMode.ForRead)); } catch { continue; }
                                if (nm != null) lst.Add(nm);
                            }
                        existing[cat] = lst;
                    }
                    dtr.Commit();
                }

                foreach (string cat in perCat.Keys)
                {
                    var ids = new ObjectIdCollection();
                    var names = new List<string>();
                    foreach (var pair in perCat[cat])
                    {
                        if (mode == "ignore" && existing[cat].Contains(pair.Value))
                        { skipped.Add(new JsonObject { ["cat"] = cat, ["name"] = pair.Value, ["why"] = "same name already in target drawing" }); continue; }
                        ids.Add(pair.Key);
                        names.Add(pair.Value);
                    }
                    if (ids.Count == 0) continue;

                    foreach (string nm in names) planned.Add(cat + " :: " + nm);
                    if (dry) continue;

                    // The proper way to import Civil styles across databases is StyleBase.ExportTo (brings dependent sub-styles automatically);
                    // WblockCloneObjects always reports eInvalidOwnerObject for Civil styles (measured 2026-08-11).
                    // In ignore mode same-named styles were skipped above, so the resolver here always uses Override.
                    using (Transaction str2 = srcDb.TransactionManager.StartTransaction())
                    {
                        for (int i2 = 0; i2 < ids.Count; i2++)
                        {
                            try
                            {
                                var srcObj = str2.GetObject(ids[i2], OpenMode.ForRead);
                                var styleBase = srcObj as Autodesk.Civil.DatabaseServices.Styles.StyleBase;
                                if (styleBase == null)
                                    throw new InvalidOperationException(
                                        "Type is not a StyleBase, ExportTo not applicable: " + srcObj.GetType().Name);
                                styleBase.ExportTo(dstDb,
                                    Autodesk.Civil.StyleConflictResolverType.Override);
                                done.Add(cat + " :: " + names[i2]);
                            }
                            catch (System.Exception ex)
                            {
                                failed.Add(new JsonObject
                                {
                                    ["cat"] = cat,
                                    ["name"] = names[i2],
                                    ["why"] = ex.GetType().Name + ": " + Truncate(
                                        ex.InnerException != null ? ex.InnerException.Message : ex.Message, 120)
                                });
                            }
                        }
                        str2.Commit();
                    }
                }
            }

            return new JsonObject
            {
                ["dry_run"] = dry,
                ["from"] = from,
                ["mode"] = mode,
                ["planned_count"] = planned.Count,
                ["imported_count"] = done.Count,
                ["planned"] = planned,
                ["imported"] = done,
                ["skipped"] = skipped,
                ["failed"] = failed
            };
        }

        // A code set is a mapping table "code -> style / label style". When labels do not appear on section views,
        // nine times out of ten the code set has no label for that code, or the code name does not match what the corridor actually produces.
        // CodeSetStyleItem property names are not guessed: reflection lists them all, ObjectIds are always resolved to names.
        static JsonNode CodeSetDump(JsonObject a, Document doc)
        {
            string want = GetString(a, "name", null);
            string corName = GetString(a, "corridor", null);
            string styleTypeFilter = GetString(a, "style_type", null);

            Database db = doc.Database;
            CivDoc civ = null;
            try { civ = CivDoc.GetCivilDocument(db); } catch { }
            if (civ == null) throw new InvalidOperationException("CivilDocument unavailable.");

            var res = new JsonObject();
            var sets = new JsonArray();
            var usedCodes = new List<string>();
            var usedLinkCodes = new List<string>();
            var usedPointCodes = new List<string>();
            var usedShapeCodes = new List<string>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // Codes actually produced by the corridor
                if (corName != null)
                {
                    foreach (ObjectId id in ModelSpace(db, tr))
                    {
                        var cor = tr.GetObject(id, OpenMode.ForRead)
                                  as Autodesk.Civil.DatabaseServices.Corridor;
                        if (cor == null || cor.Name != corName) continue;
                        try { foreach (string c in cor.GetLinkCodes()) {
                            if (!usedCodes.Contains(c)) usedCodes.Add(c);
                            if (!usedLinkCodes.Contains(c)) usedLinkCodes.Add(c);
                        } }
                        catch { }
                        try { foreach (string c in cor.GetPointCodes()) {
                            if (!usedCodes.Contains(c)) usedCodes.Add(c);
                            if (!usedPointCodes.Contains(c)) usedPointCodes.Add(c);
                        } }
                        catch { }
                        try { foreach (string c in cor.GetShapeCodes()) {
                            if (!usedCodes.Contains(c)) usedCodes.Add(c);
                            if (!usedShapeCodes.Contains(c)) usedShapeCodes.Add(c);
                        } }
                        catch { }
                    }
                }

                var coll = civ.Styles.CodeSetStyles as System.Collections.IEnumerable;
                foreach (object item in coll)
                {
                    if (!(item is ObjectId)) continue;
                    ObjectId oid = (ObjectId)item;
                    if (oid.IsErased) continue;
                    DBObject so;
                    try { so = tr.GetObject(oid, OpenMode.ForRead); } catch { continue; }
                    string nm = TryGetName(so);
                    if (nm == null) continue;
                    if (want != null && nm != want) continue;

                    var entry = new JsonObject { ["name"] = nm };
                    if (!string.IsNullOrEmpty(styleTypeFilter))
                    {
                        try { so.UpgradeOpen(); } catch { }
                        var pSubType = so.GetType().GetProperty("SubentityStyleType");
                        if (pSubType == null || !pSubType.CanWrite || !pSubType.PropertyType.IsEnum)
                            throw new InvalidOperationException("Code set SubentityStyleType is not writable, cannot check by type.");
                        string enumName = styleTypeFilter.Equals("link", StringComparison.OrdinalIgnoreCase)
                            ? "LinkType"
                            : styleTypeFilter.Equals("shape", StringComparison.OrdinalIgnoreCase)
                                ? "ShapeType" : "MarkerType";
                        pSubType.SetValue(so, Enum.Parse(pSubType.PropertyType, enumName), null);
                        entry["style_type_filter"] = enumName;
                    }
                    var items = new JsonArray();
                    var covered = new List<string>();
                    var coveredLinks = new List<string>();
                    var coveredPoints = new List<string>();
                    var coveredShapes = new List<string>();

                    var en = so as System.Collections.IEnumerable;
                    if (en != null)
                    {
                        foreach (object it in en)
                        {
                            if (it == null) continue;
                            var row = new JsonObject();
                            foreach (var p in it.GetType().GetProperties(
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                            {
                                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                                object v;
                                try { v = p.GetValue(it, null); } catch { continue; }
                                if (v == null) continue;
                                if (v is ObjectId)
                                {
                                    ObjectId vid = (ObjectId)v;
                                    if (vid.IsNull) { row[p.Name] = "(not set)"; continue; }
                                    string sn = null;
                                    try { sn = TryGetName(tr.GetObject(vid, OpenMode.ForRead)); } catch { }
                                    row[p.Name] = sn ?? "(name unavailable)";
                                }
                                else if (v is string || v.GetType().IsPrimitive || v.GetType().IsEnum)
                                    row[p.Name] = v.ToString();
                            }
                            if (row["Code"] != null)
                            {
                                string code = row["Code"].ToString();
                                covered.Add(code);
                                string st = row["StyleType"] == null ? "" : row["StyleType"].ToString();
                                if (st == "LinkType") coveredLinks.Add(code);
                                else if (st == "MarkerType") coveredPoints.Add(code);
                                else if (st == "ShapeType") coveredShapes.Add(code);
                            }
                            items.Add(row);
                        }
                    }
                    entry["item_count"] = items.Count;
                    entry["items"] = items;

                    if (usedCodes.Count > 0)
                    {
                        var missing = new JsonArray();
                        foreach (string c in usedCodes)
                            if (!covered.Contains(c)) missing.Add(c);
                        entry["corridor"] = corName;
                        entry["codes_not_covered"] = missing;
                        var missingLinks = new JsonArray();
                        foreach (string c in usedLinkCodes)
                            if (!coveredLinks.Contains(c)) missingLinks.Add(c);
                        var missingPoints = new JsonArray();
                        foreach (string c in usedPointCodes)
                            if (!coveredPoints.Contains(c)) missingPoints.Add(c);
                        var missingShapes = new JsonArray();
                        foreach (string c in usedShapeCodes)
                            if (!coveredShapes.Contains(c)) missingShapes.Add(c);
                        entry["link_codes_not_covered"] = missingLinks;
                        entry["point_codes_not_covered"] = missingPoints;
                        entry["shape_codes_not_covered"] = missingShapes;
                    }
                    sets.Add(entry);
                }
                tr.Commit();
            }

            if (usedCodes.Count > 0)
            {
                var uc = new JsonArray();
                foreach (string c in usedCodes) uc.Add(c);
                res["corridor_codes"] = uc;
            }
            res["code_sets"] = sets;
            return res;
        }

        // Find an ObjectId by name across several style collections (a code set style may be link/marker/shape/feature line; labels likewise)
        static ObjectId FindStyleAnywhere(Transaction tr, CivDoc civ, string name, string[] cats)
        {
            foreach (string cat in cats)
            {
                object coll;
                try { coll = ResolveStyleCollection(civ, cat); } catch { continue; }
                var en = coll as System.Collections.IEnumerable;
                if (en == null) continue;
                foreach (object item in en)
                {
                    if (!(item is ObjectId)) continue;
                    ObjectId oid = (ObjectId)item;
                    if (oid.IsErased) continue;
                    string nm;
                    try { nm = TryGetName(tr.GetObject(oid, OpenMode.ForRead)); } catch { continue; }
                    if (nm == name) return oid;   // case-sensitive: @C3DF-centerline and @C3DF-CenterLine are two different things
                }
            }
            return ObjectId.Null;
        }

        static readonly string[] CodeStyleCats = {
            "LinkStyles", "MarkerStyles", "ShapeStyles", "FeatureLineStyles" };
        static readonly string[] CodeLabelCats = {
            "LabelStyles.GeneralLinkLabelStyles", "LabelStyles.GeneralMarkerLabelStyles",
            "LabelStyles.GeneralShapeLabelStyles" };

        static JsonNode CodeSetEdit(JsonObject a, Document doc)
        {
            string csName = GetString(a, "name", null);
            if (csName == null) throw new InvalidOperationException("code_set_edit requires name");
            var items = a["items"] as JsonArray;
            if (items == null || items.Count == 0)
                throw new InvalidOperationException("code_set_edit requires items:[{code,style?,label_style?}]");
            bool dry = GetBool(a, "dry_run", true);

            Database db = doc.Database;
            CivDoc civ = null;
            try { civ = CivDoc.GetCivilDocument(db); } catch { }
            if (civ == null) throw new InvalidOperationException("CivilDocument unavailable.");

            var done = new JsonArray();
            var failed = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DBObject csObj = null;
                var coll = civ.Styles.CodeSetStyles as System.Collections.IEnumerable;
                foreach (object item in coll)
                {
                    if (!(item is ObjectId)) continue;
                    ObjectId oid = (ObjectId)item;
                    if (oid.IsErased) continue;
                    DBObject o;
                    try { o = tr.GetObject(oid, OpenMode.ForRead); } catch { continue; }
                    if (TryGetName(o) == csName) { csObj = o; break; }
                }
                if (csObj == null) throw new InvalidOperationException("Code set not found: " + csName);

                if (!dry) { try { csObj.UpgradeOpen(); } catch { } }

                foreach (JsonNode n in items)
                {
                    var o = n as JsonObject; if (o == null) continue;
                    string code = o["code"] == null ? null : o["code"].ToString();
                    string styleName = o["style"] == null ? null : o["style"].ToString();
                    string labelName = o["label_style"] == null ? null : o["label_style"].ToString();
                    string styleType = o["style_type"] == null ? null : o["style_type"].ToString().ToLowerInvariant();
                    if (code == null) continue;

                    // {code, remove:true, style_type?} drops the mapping (CodeSetStyle.Remove); the group is chosen by style_type like Add.
                    if (GetBool(o, "remove", false))
                    {
                        if (dry) { done.Add(code + " -> removed (dry run)"); continue; }
                        try
                        {
                            if (styleType != null)
                            {
                                var pSub = csObj.GetType().GetProperty("SubentityStyleType");
                                if (pSub != null && pSub.CanWrite && pSub.PropertyType.IsEnum)
                                    pSub.SetValue(csObj, Enum.Parse(pSub.PropertyType,
                                        styleType == "link" ? "LinkType" : styleType == "shape" ? "ShapeType" : "MarkerType"), null);
                            }
                            var mRemove = csObj.GetType().GetMethod("Remove", new Type[] { typeof(string) });
                            if (mRemove == null) throw new InvalidOperationException("CodeSetStyle.Remove(string) not available");
                            mRemove.Invoke(csObj, new object[] { code });
                            done.Add(code + " -> removed");
                        }
                        catch (System.Exception ex)
                        {
                            var inner = ex.InnerException ?? ex;
                            failed.Add(new JsonObject { ["code"] = code, ["why"] = "remove failed: " + inner.GetType().Name + ": " + Truncate(inner.Message, 120) });
                        }
                        continue;
                    }

                    ObjectId styleId = ObjectId.Null, labelId = ObjectId.Null;
                    if (styleName != null)
                    {
                        string[] styleCats = styleType == "link" ? new string[] { "LinkStyles" }
                            : styleType == "point" || styleType == "marker" ? new string[] { "MarkerStyles" }
                            : styleType == "shape" ? new string[] { "ShapeStyles" }
                            : CodeStyleCats;
                        styleId = FindStyleAnywhere(tr, civ, styleName, styleCats);
                        if (styleId.IsNull)
                        { failed.Add(new JsonObject { ["code"] = code, ["why"] = (styleType ?? "specified type") + " style not found: " + styleName }); continue; }
                    }
                    if (labelName != null)
                    {
                        string[] labelCats = styleType == "link"
                            ? new string[] { "LabelStyles.GeneralLinkLabelStyles" }
                            : styleType == "point" || styleType == "marker"
                                ? new string[] { "LabelStyles.GeneralMarkerLabelStyles" }
                                : styleType == "shape"
                                    ? new string[] { "LabelStyles.GeneralShapeLabelStyles" }
                                    : CodeLabelCats;
                        labelId = FindStyleAnywhere(tr, civ, labelName, labelCats);
                        if (labelId.IsNull)
                        { failed.Add(new JsonObject { ["code"] = code, ["why"] = "label style not found: " + labelName }); continue; }
                    }

                    if (dry)
                    {
                        done.Add(code + " -> style " + (styleName ?? "(unchanged)")
                                 + " / label " + (labelName ?? "(unchanged)") + " (dry run)");
                        continue;
                    }

                    try
                    {
                        // The enumerator, GetItemBy and Add of CodeSetStyle are all governed by the current
                        // SubentityStyleType. Switch to the target group first, then look for the existing code.
                        if (styleType != null)
                        {
                            var pSubType = csObj.GetType().GetProperty("SubentityStyleType");
                            if (pSubType == null || !pSubType.CanWrite || !pSubType.PropertyType.IsEnum)
                                throw new InvalidOperationException("Code set SubentityStyleType is not writable");
                            string enumName = styleType == "link" ? "LinkType"
                                : styleType == "shape" ? "ShapeType" : "MarkerType";
                            pSubType.SetValue(csObj, Enum.Parse(pSubType.PropertyType, enumName), null);
                        }

                        // Existing code with the same name is fetched and modified, otherwise Add
                        object entry = null;
                        var mGet = csObj.GetType().GetMethod("GetItemBy");
                        var en2 = csObj as System.Collections.IEnumerable;
                        if (en2 != null)
                            foreach (object it in en2)
                            {
                                var pc = it.GetType().GetProperty("Code");
                                if (pc == null) continue;
                                object cv = null;
                                try { cv = pc.GetValue(it, null); } catch { }
                                if (cv != null && cv.ToString() == code) { entry = it; break; }
                            }

                        if (entry == null)
                        {
                            // CodeSetStyle.Add(code, styleId) decides whether the new code goes into the Link/Point/Shape
                            // group by the style set's current SubentityStyleType;
                            // the default is MarkerType, it cannot be inferred from styleId alone.
                            if (styleType != null)
                            {
                                var pSubType = csObj.GetType().GetProperty("SubentityStyleType");
                                if (pSubType == null || !pSubType.CanWrite || !pSubType.PropertyType.IsEnum)
                                    throw new InvalidOperationException("Code set SubentityStyleType is not writable");
                                string enumName = styleType == "link" ? "LinkType"
                                    : styleType == "shape" ? "ShapeType" : "MarkerType";
                                pSubType.SetValue(csObj, Enum.Parse(pSubType.PropertyType, enumName), null);
                            }
                            var mAdd = csObj.GetType().GetMethod("Add",
                                new Type[] { typeof(string), typeof(ObjectId) });
                            if (mAdd == null) throw new InvalidOperationException("Code set has no Add(string,ObjectId)");
                            entry = mAdd.Invoke(csObj, new object[] { code, styleId });
                        }
                        else if (!styleId.IsNull)
                        {
                            var ps = entry.GetType().GetProperty("CodeStyleId");
                            if (ps != null && ps.CanWrite) ps.SetValue(entry, styleId, null);
                        }

                        if (entry != null && styleType != null)
                        {
                            var pt = entry.GetType().GetProperty("StyleType");
                            string actual = pt == null ? "(unknown)" : Convert.ToString(pt.GetValue(entry, null));
                            string expected = styleType == "link" ? "LinkType"
                                : styleType == "shape" ? "ShapeType" : "MarkerType";
                            if (actual != expected)
                                throw new InvalidOperationException(
                                    "Wrong code type: expected " + expected + ", actual " + actual);
                        }

                        if (!labelId.IsNull && entry != null)
                        {
                            var pl = entry.GetType().GetProperty("LabelStyleId");
                            if (pl == null || !pl.CanWrite)
                                throw new InvalidOperationException("LabelStyleId is not writable");
                            try
                            {
                                pl.SetValue(entry, labelId, null);
                            }
                            catch
                            {
                                // Civil 3D 2025 reports "Value does not fall within expected range" when writing the ObjectId
                                // directly for some newly created Link Codes, but the same
                                // CodeSetStyleItem can be resolved by the host by type via LabelStyleName.
                                var pn = entry.GetType().GetProperty("LabelStyleName");
                                if (pn == null || !pn.CanWrite) throw;
                                pn.SetValue(entry, labelName, null);
                            }
                        }
                        done.Add(code + " -> style " + (styleName ?? "(unchanged)")
                                 + " / label " + (labelName ?? "(unchanged)"));
                    }
                    catch (System.Exception ex)
                    {
                        failed.Add(new JsonObject
                        {
                            ["code"] = code,
                            ["why"] = ex.GetType().Name + ": " + Truncate(
                                ex.InnerException != null ? ex.InnerException.Message : ex.Message, 120)
                        });
                    }
                }
                tr.Commit();
            }
            return new JsonObject
            {
                ["dry_run"] = dry,
                ["code_set"] = csName,
                ["done"] = done,
                ["failed"] = failed
            };
        }

        // Label styles have no DisplayStyle; use LabelStyle's own component model:
        // GetComponentsDrawOrder() gives component ObjectIds, then read the properties of each by reflection (visibility, text, layer, colour...).
        // Common reasons a label does not show: 0 components, component Visible=false, empty text content, layer off.
        static JsonNode LabelStyleDump(JsonObject a, Document doc)
        {
            string want = GetString(a, "name", null);
            if (want == null) throw new InvalidOperationException("label_style_dump requires name");

            var cats = new List<string>();
            var arr = a["cats"] as JsonArray;
            if (arr != null) foreach (JsonNode n in arr) if (n != null) cats.Add(n.ToString());

            Database db = doc.Database;
            CivDoc civ = null;
            try { civ = CivDoc.GetCivilDocument(db); } catch { }
            if (civ == null) throw new InvalidOperationException("CivilDocument unavailable.");

            var hits = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // Without categories, scan the whole LabelStyles tree
                var collections = new List<KeyValuePair<string, object>>();
                if (cats.Count > 0)
                    foreach (string c in cats)
                    {
                        object o2 = null;
                        try { o2 = ResolveStyleCollection(civ, c); } catch { }
                        if (o2 != null) collections.Add(new KeyValuePair<string, object>(c, o2));
                    }
                else
                {
                    var stack = new List<KeyValuePair<string, object>>();
                    stack.Add(new KeyValuePair<string, object>("LabelStyles", civ.Styles.LabelStyles));
                    var seen = new List<object>();
                    while (stack.Count > 0)
                    {
                        var cur = stack[0]; stack.RemoveAt(0);
                        if (cur.Value == null) continue;
                        bool dup = false;
                        foreach (object s in seen) if (ReferenceEquals(s, cur.Value)) { dup = true; break; }
                        if (dup) continue;
                        seen.Add(cur.Value);
                        foreach (var p in cur.Value.GetType().GetProperties(
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        {
                            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                            object val;
                            try { val = p.GetValue(cur.Value, null); } catch { continue; }
                            if (val == null) continue;
                            string key = cur.Key + "." + p.Name;
                            if (val is System.Collections.IEnumerable && !(val is string))
                                collections.Add(new KeyValuePair<string, object>(key, val));
                            else if (val.GetType().FullName != null
                                     && val.GetType().FullName.StartsWith("Autodesk.Civil")
                                     && val.GetType().Name.EndsWith("Root"))
                                stack.Add(new KeyValuePair<string, object>(key, val));
                        }
                    }
                }

                foreach (var kv in collections)
                {
                    var en = kv.Value as System.Collections.IEnumerable;
                    if (en == null) continue;
                    foreach (object item in en)
                    {
                        if (!(item is ObjectId)) continue;
                        ObjectId oid = (ObjectId)item;
                        if (oid.IsErased) continue;
                        DBObject so;
                        try { so = tr.GetObject(oid, OpenMode.ForRead); } catch { continue; }
                        if (TryGetName(so) != want) continue;   // case-sensitive

                        var entry = new JsonObject { ["cat"] = kv.Key, ["name"] = want,
                                                     ["type"] = so.GetType().Name };

                        // Component count
                        try
                        {
                            var mCnt = so.GetType().GetMethod("GetComponentsCount", Type.EmptyTypes);
                            if (mCnt != null) entry["component_count"] = (int)mCnt.Invoke(so, null);
                        }
                        catch (System.Exception ex)
                        { entry["component_count_error"] = ex.GetType().Name; }

                        // Each component
                        var comps = new JsonArray();
                        try
                        {
                            var mOrder = so.GetType().GetMethod("GetComponentsDrawOrder", Type.EmptyTypes);
                            var ids = mOrder == null ? null : mOrder.Invoke(so, null) as ObjectId[];
                            if (ids != null)
                                foreach (ObjectId cid in ids)
                                {
                                    if (cid.IsNull || cid.IsErased) continue;
                                    object c;
                                    try { c = tr.GetObject(cid, OpenMode.ForRead); } catch { continue; }
                                    comps.Add(DumpShallow(tr, c, 2));
                                }
                        }
                        catch (System.Exception ex)
                        { entry["components_error"] = ex.GetType().Name + ": " + Truncate(ex.Message, 90); }
                        entry["components"] = comps;

                        // The style's own properties (visibility, layer etc. live here)
                        try
                        {
                            var pProps = so.GetType().GetProperty("Properties");
                            if (pProps != null)
                            {
                                object pv = pProps.GetValue(so, null);
                                if (pv != null) entry["properties"] = DumpShallow(tr, pv, 2);
                            }
                        }
                        catch (System.Exception ex)
                        { entry["properties_error"] = ex.GetType().Name; }

                        hits.Add(entry);
                    }
                }
                tr.Commit();
            }
            return new JsonObject { ["name"] = want, ["found"] = hits.Count, ["hits"] = hits };
        }

        // Shallow reflection to JSON: scalars as is, ObjectIds resolved to names, nested objects recursed depth levels
        static JsonObject DumpShallow(Transaction tr, object o, int depth)
        {
            var row = new JsonObject();
            if (o == null) return row;
            foreach (var p in o.GetType().GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                object v;
                try { v = p.GetValue(o, null); }
                catch (System.Exception ex)
                {
                    string msg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                    if (msg.IndexOf("not overridable", StringComparison.OrdinalIgnoreCase) < 0)
                        row[p.Name] = "(read failed: " + ex.GetType().Name + ")";
                    continue;
                }
                if (v == null) continue;
                Type vt = v.GetType();
                if (v is ObjectId)
                {
                    ObjectId vid = (ObjectId)v;
                    if (vid.IsNull) { row[p.Name] = "(empty)"; continue; }
                    string sn = null;
                    try { sn = TryGetName(tr.GetObject(vid, OpenMode.ForRead)); } catch { }
                    row[p.Name] = sn ?? "(name unavailable)";
                }
                else if (v is string || vt.IsPrimitive || vt.IsEnum)
                    row[p.Name] = v.ToString();
                else if (depth > 0 && vt.FullName != null && vt.FullName.StartsWith("Autodesk"))
                    row[p.Name] = DumpShallow(tr, v, depth - 1);
            }
            return row;
        }

        // Take one section + section view per alignment and dump every property.
        // Purpose: diff directly when "old sections made by Civil commands" and "new sections made by this tool" coexist in one drawing.
        static JsonNode DumpSections(JsonObject a, Document doc)
        {
            var names = new List<string>();
            var arr = a["alignments"] as JsonArray;
            if (arr != null) foreach (JsonNode n in arr) if (n != null) names.Add(n.ToString());
            if (names.Count == 0) throw new InvalidOperationException("dump_sections requires alignments");
            string which = GetString(a, "which", "both");

            Database db = doc.Database;
            var res = new JsonObject();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (string alName in names)
                {
                    var entry = new JsonObject();
                    CivAlignment al = null;
                    foreach (ObjectId id in ModelSpace(db, tr))
                    {
                        var x = tr.GetObject(id, OpenMode.ForRead) as CivAlignment;
                        if (x != null && x.Name == alName) { al = x; break; }
                    }
                    if (al == null) { entry["error"] = "alignment not found"; res[alName] = entry; continue; }

                    // Sample line group -> sample line -> section. **All groups, all section views are needed**:
                    // one alignment may carry both "mine" and "Civil-command-made" groups;
                    // taking only the first never reveals the difference (earlier rounds failed exactly here).
                    var groups = new JsonArray();
                    try
                    {
                        foreach (ObjectId gid in al.GetSampleLineGroupIds())
                        {
                            bool gotSection = false, gotView = false;
                            var grp = tr.GetObject(gid, OpenMode.ForRead)
                                      as Autodesk.Civil.DatabaseServices.SampleLineGroup;
                            if (grp == null) continue;
                            var gEntry = new JsonObject { ["group"] = grp.Name };
                            entry = gEntry;                     // the entry variable below fills this group
                            groups.Add(gEntry);
                            entry["sample_line_group"] = grp.Name;

                            foreach (ObjectId sid in grp.GetSampleLineIds())
                            {
                                var sl = tr.GetObject(sid, OpenMode.ForRead)
                                         as Autodesk.Civil.DatabaseServices.SampleLine;
                                if (sl == null) continue;
                                entry["sample_line"] = sl.Name;
                                entry["sample_line_dump"] = DumpShallow(tr, sl, 1);

                                // Section views come straight from the sample line. **One sample line may carry several**:
                                // a sample line group may have several section view groups (one mine, one from the Civil command);
                                // taking only the first never shows the other group.
                                if (which != "section" && !gotView)
                                    try
                                    {
                                        var views = new JsonArray();
                                        foreach (ObjectId svId in sl.GetSectionViewIds())
                                        {
                                            var sv2 = tr.GetObject(svId, OpenMode.ForRead);
                                            var one = DumpShallow(tr, sv2, 1);
                                            one["_name"] = TryGetName(sv2);

                                            // * View-level label group query: decides "were labels created at all".
                                            // Non-empty collection but invisible on screen -> display optimisation/layer/visibility issue;
                                            // empty collection -> the label set was never applied.
                                            var lg = new JsonObject();
                                            foreach (var mm in sv2.GetType().GetMethods(
                                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                                            {
                                                if (mm.GetParameters().Length != 0) continue;
                                                if (mm.Name.IndexOf("Label", StringComparison.Ordinal) < 0) continue;
                                                if (!mm.Name.StartsWith("Get")) continue;
                                                try
                                                {
                                                    object rv = mm.Invoke(sv2, null);
                                                    var oc = rv as ObjectIdCollection;
                                                    lg[mm.Name] = oc == null ? -1 : oc.Count;
                                                }
                                                catch (System.Exception ex)
                                                { lg[mm.Name] = "err:" + ex.GetType().Name; }
                                            }
                                            one["_label_groups"] = lg;

                                            // * Each view's **overrides** for each section: both view groups share the same sections,
                                            // so display differences can only hide here (corridor section overrides included).
                                            try
                                            {
                                                var pOv = sv2.GetType().GetProperty("GraphOverrides");
                                                object ov = pOv == null ? null : pOv.GetValue(sv2, null);
                                                var ovs = new JsonArray();
                                                var en3 = ov as System.Collections.IEnumerable;
                                                if (en3 != null)
                                                    foreach (object it in en3)
                                                    {
                                                        var row = DumpShallow(tr, it, 1);
                                                        // * Does this section have a label group in this view at all:
                                                        // the Autodesk support article says labels default to "corridor point style labels" rather than code set labels,
                                                        // and the presence of a label group is the most direct evidence
                                                        try
                                                        {
                                                            var m2 = it.GetType().GetMethod("GetSectionLabelGroupIds", Type.EmptyTypes);
                                                            var ids2 = m2 == null ? null : m2.Invoke(it, null) as ObjectIdCollection;
                                                            row["_label_group_count"] = ids2 == null ? -1 : ids2.Count;
                                                        }
                                                        catch (System.Exception ex)
                                                        { row["_label_group_error"] = ex.GetType().Name; }
                                                        ovs.Add(row);
                                                    }
                                                one["_overrides"] = ovs;
                                                one["_override_count"] = ovs.Count;
                                            }
                                            catch (System.Exception ex)
                                            { one["_override_error"] = ex.GetType().Name + ": " + Truncate(ex.Message, 90); }

                                            views.Add(one);
                                        }
                                        entry["views"] = views;
                                        entry["view_count"] = views.Count;
                                        gotView = true;
                                    }
                                    catch (System.Exception ex)
                                    { entry["view_error"] = ex.GetType().Name + ": " + Truncate(ex.Message, 90); }

                                // A sample line carries several sections (ground line, corridor...); take them all,
                                // the first alone would be the ground-line section while point labels belong to the corridor section.
                                if (which != "view" && !gotSection)
                                    try
                                    {
                                        var secs = new JsonArray();
                                        foreach (ObjectId secId in sl.GetSectionIds())
                                        {
                                            var sec = tr.GetObject(secId, OpenMode.ForRead);
                                            var one = DumpShallow(tr, sec, 1);
                                            one["_type"] = sec.GetType().Name;
                                            secs.Add(one);
                                        }
                                        entry["sections"] = secs;
                                        entry["section_count"] = secs.Count;
                                        gotSection = true;
                                    }
                                    catch (System.Exception ex)
                                    { entry["section_error"] = ex.GetType().Name + ": " + Truncate(ex.Message, 90); }
                                break;   // one sample line per group is enough as a sample for comparison
                            }
                        }
                    }
                    catch (System.Exception ex)
                    { groups.Add(new JsonObject { ["group_error"] = ex.GetType().Name + ": " + Truncate(ex.Message, 90) }); }

                    res[alName] = new JsonObject { ["group_count"] = groups.Count, ["groups"] = groups };
                }
                tr.Commit();
            }
            return res;
        }

        // Search here first when unsure which API to use.
        // Note: AeccDbMgd lives in a custom ALC; AppDomain.CurrentDomain.GetAssemblies() cannot see it,
        // typeof(Alignment).Assembly must be used as the seed (old trap, see reference-accoreconsole-civil3d).
        static JsonNode ApiSearch(JsonObject a, Document doc)
        {
            string q = GetString(a, "q", null);
            if (q == null) throw new InvalidOperationException("api_search requires q");
            bool withMembers = GetBool(a, "members", true);
            int max = (int)GetDouble(a, "max", 60);
            string[] terms = q.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            var asms = new List<System.Reflection.Assembly>();
            asms.Add(typeof(CivAlignment).Assembly);                 // AeccDbMgd
            try { asms.Add(typeof(CivDoc).Assembly); } catch { }     // AeccUiMgd / ApplicationServices
            try { asms.Add(typeof(Database).Assembly); } catch { }   // acdbmgd

            Func<string, bool> hit = s =>
            {
                foreach (string t in terms)
                    if (s.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0) return false;
                return true;
            };

            var types = new JsonArray();
            var members = new JsonArray();
            var seenAsm = new List<string>();

            foreach (var asm in asms)
            {
                string an = asm.GetName().Name;
                if (seenAsm.Contains(an)) continue;
                seenAsm.Add(an);
                Type[] all;
                try { all = asm.GetTypes(); } catch { continue; }
                foreach (Type t in all)
                {
                    if (t.FullName == null) continue;
                    if (hit(t.Name) && types.Count < max)
                        types.Add(an + " :: " + t.FullName);

                    if (!withMembers || members.Count >= max) continue;
                    if (!t.IsPublic) continue;
                    System.Reflection.MethodInfo[] ms;
                    try
                    {
                        ms = t.GetMethods(System.Reflection.BindingFlags.Public
                                        | System.Reflection.BindingFlags.Instance
                                        | System.Reflection.BindingFlags.Static
                                        | System.Reflection.BindingFlags.DeclaredOnly);
                    }
                    catch { continue; }
                    foreach (var m in ms)
                    {
                        if (members.Count >= max) break;
                        // Match on member names and also on **type names appearing in the signature**.
                        // The latter is the key: to find "who uses the eXxx enum" the member name does not contain it at all.
                        var ps = new List<string>();
                        bool sigHit = hit(m.ReturnType.Name);
                        foreach (var p in m.GetParameters())
                        {
                            ps.Add(p.ParameterType.Name + " " + p.Name);
                            if (hit(p.ParameterType.Name)) sigHit = true;
                        }
                        if (!hit(m.Name) && !sigHit) continue;
                        members.Add(t.Name + "." + m.Name + "(" + string.Join(", ", ps.ToArray()) + ")"
                                    + " → " + m.ReturnType.Name);
                    }
                }
            }

            return new JsonObject
            {
                ["query"] = q,
                ["assemblies"] = string.Join(", ", seenAsm.ToArray()),
                ["type_hits"] = types.Count,
                ["types"] = types,
                ["member_hits"] = members.Count,
                ["members"] = members
            };
        }

        static JsonNode ListLayers(JsonObject a, Document doc)
        {
            bool usedOnly = GetBool(a, "used_only", false);
            int max = (int)GetDouble(a, "max", 2000);
            Database db = doc.Database;
            var arr = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // Count entities per layer in model space first, to decide "is it used"
                var used = new Dictionary<string, int>();
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    Entity e;
                    try { e = tr.GetObject(id, OpenMode.ForRead) as Entity; } catch { continue; }
                    if (e == null) continue;
                    string ln;
                    try { ln = e.Layer; } catch { continue; }
                    used[ln] = used.ContainsKey(ln) ? used[ln] + 1 : 1;
                }

                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in lt)
                {
                    if (arr.Count >= max) break;
                    var ltr = tr.GetObject(id, OpenMode.ForRead) as LayerTableRecord;
                    if (ltr == null) continue;
                    int n = used.ContainsKey(ltr.Name) ? used[ltr.Name] : 0;
                    if (usedOnly && n == 0) continue;
                    arr.Add(new JsonObject
                    {
                        ["name"] = ltr.Name,
                        ["color"] = DescribeColor(ltr.Color),
                        ["linetype"] = SafeLinetype(tr, ltr),
                        ["lineweight"] = ltr.LineWeight.ToString(),
                        ["off"] = ltr.IsOff,
                        ["frozen"] = ltr.IsFrozen,
                        ["locked"] = ltr.IsLocked,
                        ["plottable"] = ltr.IsPlottable,
                        ["entities"] = n
                    });
                }
                tr.Commit();
            }
            return new JsonObject { ["count"] = arr.Count, ["layers"] = arr };
        }

        static string SafeLinetype(Transaction tr, LayerTableRecord ltr)
        {
            try
            {
                var lt = tr.GetObject(ltr.LinetypeObjectId, OpenMode.ForRead) as LinetypeTableRecord;
                return lt == null ? "?" : lt.Name;
            }
            catch { return "?"; }
        }

        // Batch version of style_display: all (or filtered) styles x all views x all components.
        // Get the full picture before normalising: which colours are not ByLayer, how many spellings of layer names, references to missing layers.
        static JsonNode StylesAudit(JsonObject a, Document doc)
        {
            string filter = GetString(a, "filter", null);
            int max = (int)GetDouble(a, "max", 2000);
            var onlyCats = a["cats"] as JsonArray;

            Database db = doc.Database;
            CivDoc civ = null;
            try { civ = CivDoc.GetCivilDocument(db); } catch { }
            if (civ == null) throw new InvalidOperationException("CivilDocument unavailable.");

            // Layer table: to decide whether the layers referenced by styles exist
            var layerSet = new List<string>();
            using (Transaction tr0 = db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr0.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in lt)
                {
                    var ltr = tr0.GetObject(id, OpenMode.ForRead) as LayerTableRecord;
                    if (ltr != null) layerSet.Add(ltr.Name);
                }
                tr0.Commit();
            }

            var rows = new JsonArray();
            int styles = 0, comps = 0, nonByLayer = 0, missingLayer = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // Reuse the list_styles walk: category path -> collection
                var stack = new List<KeyValuePair<string, object>>();
                stack.Add(new KeyValuePair<string, object>("", civ.Styles));
                var seen = new List<object>();
                var collections = new List<KeyValuePair<string, object>>();

                while (stack.Count > 0)
                {
                    var cur = stack[0]; stack.RemoveAt(0);
                    if (cur.Value == null) continue;
                    bool dup = false;
                    foreach (object s in seen) if (ReferenceEquals(s, cur.Value)) { dup = true; break; }
                    if (dup) continue;
                    seen.Add(cur.Value);

                    foreach (var p in cur.Value.GetType().GetProperties(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    {
                        if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                        string key = cur.Key.Length == 0 ? p.Name : cur.Key + "." + p.Name;
                        object val;
                        try { val = p.GetValue(cur.Value, null); } catch { continue; }
                        if (val == null) continue;
                        Type vt = val.GetType();
                        if (val is string || vt.IsPrimitive || vt.IsEnum) continue;
                        if (val is System.Collections.IEnumerable)
                            collections.Add(new KeyValuePair<string, object>(key, val));
                        else if (vt.FullName != null && vt.FullName.StartsWith("Autodesk.Civil")
                                 && vt.Name.EndsWith("Root"))
                            stack.Add(new KeyValuePair<string, object>(key, val));
                    }
                }

                foreach (var kv in collections)
                {
                    if (onlyCats != null)
                    {
                        bool want = false;
                        foreach (JsonNode c in onlyCats)
                            if (c != null && c.ToString() == kv.Key) { want = true; break; }
                        if (!want) continue;
                    }
                    var en = kv.Value as System.Collections.IEnumerable;
                    if (en == null) continue;

                    foreach (object item in en)
                    {
                        if (rows.Count >= max) break;
                        if (!(item is ObjectId)) continue;
                        ObjectId oid = (ObjectId)item;
                        if (oid.IsErased) continue;
                        DBObject so;
                        try { so = tr.GetObject(oid, OpenMode.ForRead); } catch { continue; }
                        string nm = TryGetName(so);
                        if (nm == null) continue;
                        if (filter != null && nm.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        styles++;

                        foreach (var m in so.GetType().GetMethods(
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        {
                            if (!m.Name.StartsWith("GetDisplayStyle")) continue;
                            var ps = m.GetParameters();
                            if (ps.Length != 1 || !ps[0].ParameterType.IsEnum) continue;
                            Type et = ps[0].ParameterType;
                            string viewName = m.Name.Substring("GetDisplayStyle".Length);
                            if (viewName.Length == 0) viewName = "(default)";

                            foreach (string cn in Enum.GetNames(et))
                            {
                                if (rows.Count >= max) break;
                                var row = new JsonObject
                                {
                                    ["cat"] = kv.Key,
                                    ["style"] = nm,
                                    ["view"] = viewName,
                                    ["component"] = cn
                                };
                                try
                                {
                                    object ds = m.Invoke(so, new object[] { Enum.Parse(et, cn) });
                                    FillDisplay(ds, row);
                                    comps++;
                                    string col = row["color"] != null ? row["color"].ToString() : null;
                                    if (col != null && col != "ByLayer") { nonByLayer++; row["color_todo"] = true; }
                                    string lay = row["layer"] != null ? row["layer"].ToString() : null;
                                    if (lay != null && !layerSet.Contains(lay))
                                    { missingLayer++; row["layer_missing"] = true; }
                                }
                                catch { continue; }   // component not applicable on this style, skip
                                rows.Add(row);
                            }
                        }
                    }
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["filter"] = filter,
                ["styles_scanned"] = styles,
                ["components"] = comps,
                ["color_not_bylayer"] = nonByLayer,
                ["layer_missing_refs"] = missingLayer,
                ["rows"] = rows
            };
        }

        // ---- style_display: read/write of the style dialog's Display page ----
        //
        // Civil's way (verified with the api operations on 2026-07-28, not guessed):
        //   the style class has GetDisplayStyle<View>(some enum component) -> returns DisplayStyle,
        //   whose Color / Layer / Linetype / Lineweight / LinetypeScale / Visible are all writable.
        // The enum type differs per style class (ProfileDataDisplayStyleType, AlignmentDisplayStyleType...),
        // so nothing is hard-coded: reflection finds the GetDisplayStyle* methods and parses component names by their enum parameter.
        static JsonNode StyleDisplay(JsonObject a, Document doc)
        {
            string cat = GetString(a, "cat", null);
            string name = GetString(a, "name", null);
            if (cat == null || name == null)
                throw new InvalidOperationException("style_display requires cat and name");
            string view = GetString(a, "view", "Plan");
            string comp = GetString(a, "component", null);
            var set = a["set"] as JsonObject;
            bool dry = GetBool(a, "dry_run", true);

            Database db = doc.Database;
            CivDoc civ = null;
            try { civ = CivDoc.GetCivilDocument(db); } catch { }
            if (civ == null) throw new InvalidOperationException("CivilDocument unavailable.");

            object coll = ResolveStyleCollection(civ, cat);
            var en = coll as System.Collections.IEnumerable;
            if (en == null) throw new InvalidOperationException("Category path does not resolve to a collection: " + cat);

            var res = new JsonObject { ["cat"] = cat, ["name"] = name, ["view"] = view, ["dry_run"] = dry };

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DBObject styleObj = null;
                foreach (object item in en)
                {
                    if (!(item is ObjectId)) continue;
                    ObjectId oid = (ObjectId)item;
                    if (oid.IsErased) continue;
                    DBObject o;
                    try { o = tr.GetObject(oid, OpenMode.ForRead); } catch { continue; }
                    if (TryGetName(o) == name) { styleObj = o; break; }
                }
                if (styleObj == null)
                    throw new InvalidOperationException("Collection " + cat + " has no style: " + name);
                res["style_type"] = styleObj.GetType().Name;

                // Find GetDisplayStyle<View>(enum)
                System.Reflection.MethodInfo getter = null;
                var candidates = new List<System.Reflection.MethodInfo>();
                foreach (var m in styleObj.GetType().GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (!m.Name.StartsWith("GetDisplayStyle")) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 1 || !ps[0].ParameterType.IsEnum) continue;
                    candidates.Add(m);
                    if (m.Name.EndsWith(view, StringComparison.OrdinalIgnoreCase)) getter = m;
                }
                if (getter == null && candidates.Count == 1) getter = candidates[0];
                if (getter == null)
                {
                    var avail = new JsonArray();
                    foreach (var m in candidates) avail.Add(m.Name);
                    res["error"] = "no GetDisplayStyle method matching view=" + view;
                    res["available_views"] = avail;
                    tr.Commit();
                    return res;
                }
                res["getter"] = getter.Name;
                Type enumT = getter.GetParameters()[0].ParameterType;
                res["component_enum"] = enumT.Name;

                Func<string, string> norm = s =>
                    s.Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();

                // No component given: list all components with current values
                if (comp == null)
                {
                    var arr = new JsonArray();
                    foreach (string en2 in Enum.GetNames(enumT))
                    {
                        var row = new JsonObject { ["component"] = en2 };
                        try
                        {
                            object ds = getter.Invoke(styleObj, new object[] { Enum.Parse(enumT, en2) });
                            FillDisplay(ds, row);
                        }
                        catch (System.Exception ex)
                        { row["error"] = ex.GetType().Name + ": " + Truncate(
                            ex.InnerException != null ? ex.InnerException.Message : ex.Message, 80); }
                        arr.Add(row);
                    }
                    res["components"] = arr;
                    tr.Commit();
                    return res;
                }

                // component given: parse the enum.
                // Dialog wording and enum names often differ (dialog "Band Title Box Text" = enum TitleBoxText),
                // so match exactly first, then fall back to "one contains the other", accepting only a unique hit.
                string matched = null;
                string want = norm(comp);
                foreach (string en2 in Enum.GetNames(enumT))
                    if (norm(en2) == want) { matched = en2; break; }
                if (matched == null)
                {
                    var loose = new List<string>();
                    foreach (string en2 in Enum.GetNames(enumT))
                    {
                        string e = norm(en2);
                        if (want.EndsWith(e) || e.EndsWith(want) || want.Contains(e) || e.Contains(want))
                            loose.Add(en2);
                    }
                    // With several hits take the **longest**: "Band Title Box Text" matches both TitleBox and
                    // TitleBoxText, and the longer one is what the user meant. Only a tie for longest is a real ambiguity.
                    if (loose.Count > 0)
                    {
                        loose.Sort(delegate (string x, string y) { return y.Length.CompareTo(x.Length); });
                        if (loose.Count == 1 || loose[0].Length > loose[1].Length)
                        { matched = loose[0]; res["matched_loosely"] = true; }
                        else
                            throw new InvalidOperationException(
                                "Ambiguous component name: " + comp + ", candidates: " + string.Join(", ", loose.ToArray()));
                    }
                }
                if (matched == null)
                    throw new InvalidOperationException(
                        "Unknown component name: " + comp + ", valid values: "
                        + string.Join(", ", Enum.GetNames(enumT)));
                res["component"] = matched;

                object disp = getter.Invoke(styleObj, new object[] { Enum.Parse(enumT, matched) });
                var before = new JsonObject(); FillDisplay(disp, before);
                res["before"] = before;

                if (set == null || set.Count == 0)
                { tr.Commit(); return res; }

                var changes = new JsonArray();
                if (!dry)
                {
                    // DisplayStyle is a wrapper; to modify it the style itself must be opened for write first
                    try { styleObj.UpgradeOpen(); } catch { }
                    disp = getter.Invoke(styleObj, new object[] { Enum.Parse(enumT, matched) });
                }
                foreach (var kv in set)
                {
                    string k = kv.Key.ToLowerInvariant();
                    string v = kv.Value == null ? null : kv.Value.ToString();
                    try
                    {
                        if (dry) { changes.Add(k + " -> " + v + " (dry run, not written)"); continue; }
                        ApplyDisplay(disp, k, v);
                        changes.Add(k + " → " + v);
                    }
                    catch (System.Exception ex)
                    {
                        changes.Add(k + " failed: " + ex.GetType().Name + ": " + Truncate(
                            ex.InnerException != null ? ex.InnerException.Message : ex.Message, 100));
                    }
                }
                res["changes"] = changes;

                var after = new JsonObject();
                FillDisplay(getter.Invoke(styleObj, new object[] { Enum.Parse(enumT, matched) }), after);
                res["after"] = after;

                tr.Commit();
            }
            return res;
        }

        static void FillDisplay(object ds, JsonObject row)
        {
            if (ds == null) { row["error"] = "DisplayStyle is null"; return; }
            Func<string, object> get = pn =>
            {
                var p = ds.GetType().GetProperty(pn);
                if (p == null) return null;
                try { return p.GetValue(ds, null); } catch { return null; }
            };
            object c = get("Color");
            if (c != null) row["color"] = DescribeColor(c);
            object v;
            if ((v = get("Layer")) != null) row["layer"] = v.ToString();
            if ((v = get("Linetype")) != null) row["linetype"] = v.ToString();
            if ((v = get("Lineweight")) != null) row["lineweight"] = v.ToString();
            if ((v = get("LinetypeScale")) != null) row["linetype_scale"] = Convert.ToDouble(v);
            if ((v = get("Visible")) != null) row["visible"] = Convert.ToBoolean(v);
            if ((v = get("PlotStyle")) != null) row["plot_style"] = v.ToString();
        }

        static string DescribeColor(object c)
        {
            var col = c as Autodesk.AutoCAD.Colors.Color;
            if (col == null) return c.ToString();
            if (col.IsByLayer) return "ByLayer";
            if (col.IsByBlock) return "ByBlock";
            if (col.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByAci)
                return "ACI " + col.ColorIndex;
            return string.Format("{0},{1},{2}", col.Red, col.Green, col.Blue);
        }

        // Value syntax: color = ByLayer|ByBlock|<ACI 0-256>|"r,g,b"; visible = true/false; the rest as string/number
        static void ApplyDisplay(object ds, string key, string val)
        {
            Type t = ds.GetType();
            if (key == "color")
            {
                var p = t.GetProperty("Color");
                p.SetValue(ds, ParseColor(val), null);
                return;
            }
            if (key == "visible")
            { t.GetProperty("Visible").SetValue(ds, bool.Parse(val), null); return; }
            if (key == "linetype_scale")
            { t.GetProperty("LinetypeScale").SetValue(ds, double.Parse(val, CultureInfo.InvariantCulture), null); return; }
            if (key == "lineweight")
            {
                var p = t.GetProperty("Lineweight");
                p.SetValue(ds, Enum.Parse(p.PropertyType, val, true), null);
                return;
            }
            if (key == "layer") { t.GetProperty("Layer").SetValue(ds, val, null); return; }
            if (key == "linetype") { t.GetProperty("Linetype").SetValue(ds, val, null); return; }
            if (key == "plot_style") { t.GetProperty("PlotStyle").SetValue(ds, val, null); return; }
            throw new InvalidOperationException("Unknown display property: " + key);
        }

        static Autodesk.AutoCAD.Colors.Color ParseColor(string v)
        {
            string s = (v ?? "").Trim();
            if (string.Equals(s, "ByLayer", StringComparison.OrdinalIgnoreCase))
                return Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByLayer, 256);
            if (string.Equals(s, "ByBlock", StringComparison.OrdinalIgnoreCase))
                return Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByBlock, 0);
            if (s.Contains(","))
            {
                string[] p = s.Split(',');
                return Autodesk.AutoCAD.Colors.Color.FromRgb(
                    byte.Parse(p[0].Trim()), byte.Parse(p[1].Trim()), byte.Parse(p[2].Trim()));
            }
            short aci;
            if (short.TryParse(s, out aci))
                return Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByAci, aci);
            throw new InvalidOperationException("Unrecognised colour syntax: " + v);
        }

        // The collection may hold ObjectIds or objects directly; collect names from both.
        static void CollectNames(Transaction tr, object collection, JsonArray outArr)
        {
            var en = collection as System.Collections.IEnumerable;
            if (en == null) return;
            foreach (object item in en)
            {
                if (item == null) continue;
                if (item is ObjectId)
                {
                    try
                    {
                        string n = TryGetName(tr.GetObject((ObjectId)item, OpenMode.ForRead));
                        if (n != null) outArr.Add(n);
                    }
                    catch { }
                }
                else if (item is DBObject)
                {
                    string n = TryGetName((DBObject)item);
                    if (n != null) outArr.Add(n);
                }
                else
                {
                    var p = item.GetType().GetProperty("Name");
                    if (p != null)
                    {
                        try { outArr.Add(System.Convert.ToString(p.GetValue(item, null))); }
                        catch { }
                    }
                }
            }
        }

        // Style collections are always enumerated as ObjectIds + Name by reflection (collection types differ; reflection is simplest and least error-prone)
        static JsonArray StyleNames(Transaction tr, object collection, int max)
        {
            var arr = new JsonArray();
            var en = collection as System.Collections.IEnumerable;
            if (en == null) return arr;
            foreach (object item in en)
            {
                if (arr.Count >= max) { arr.Add("...(more)"); break; }
                if (!(item is ObjectId)) { arr.Add(item == null ? "(null)" : item.ToString()); continue; }
                try
                {
                    DBObject o = tr.GetObject((ObjectId)item, OpenMode.ForRead);
                    string n = TryGetName(o);
                    // When the name cannot be read, carry the reason; do not just drop a class name and leave people guessing
                    if (n == null)
                    {
                        string why;
                        try
                        {
                            var p = o.GetType().GetProperty("Name");
                            if (p == null) why = "(no Name property)";
                            else
                            {
                                object v = p.GetValue(o, null);
                                why = v == null ? "(Name is null)" : v.ToString();
                            }
                        }
                        catch (System.Exception ex2) { why = "(" + ex2.GetType().Name + ": " + Truncate(ex2.Message, 60) + ")"; }
                        n = o.GetType().Name + " " + why;
                    }
                    arr.Add(n);
                }
                catch (System.Exception ex) { arr.Add("(read failed: " + ex.GetType().Name + ")"); }
            }
            return arr;
        }

        // Create a standalone Database and write it as a dwg file. new Database(true,false) starts an empty drawing,
        // fully isolated from the host drawing passed with /i: the host is never modified and no template file is needed.
        static JsonNode CreateDwg(JsonObject a, Document doc)
        {
            string path = GetString(a, "path", null);
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException("Missing parameter path (absolute path of the new dwg).");
            if (!Path.IsPathRooted(path))
                throw new InvalidOperationException("path must be absolute: " + path);

            bool overwrite = GetBool(a, "overwrite", false);
            if (File.Exists(path) && !overwrite)
                throw new InvalidOperationException(
                    "File already exists, refusing to overwrite: " + path + " (pass overwrite:true to overwrite)");

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string layer = GetString(a, "layer", "0");
            var drawn = new JsonArray();

            // The second argument noDocument must be true: a side database not associated with a document.
            // With false the drawing is generated fine, but accoreconsole never exits afterwards (hangs at QUIT, needs taskkill).
            using (Database db = new Database(true, true))
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    EnsureLayers(db, tr, a["layers"] as JsonArray);
                    AddEntities(db, tr, ms, a, layer, drawn);
                    InsertBlocks(db, tr, ms, a, layer, drawn);
                    tr.Commit();
                }

                if (drawn.Count == 0)
                    throw new InvalidOperationException(
                        "Nothing to draw; no empty file generated. Available keys: blocks / circles / lines / arcs / polylines / texts.");

                db.SaveAs(path, DwgVersion.Current);
            }

            var fi = new FileInfo(path);
            return new JsonObject
            {
                ["created"] = path,
                ["bytes"] = fi.Exists ? fi.Length : 0,
                ["entities"] = drawn.Count,
                ["drawn"] = drawn
            };
        }

        // ---------- Drawing: layers + entity types ----------

        // Create layers on demand (if it exists, only update the colour when color is given)
        static void EnsureLayers(Database db, Transaction tr, JsonArray layers)
        {
            if (layers == null) return;
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (JsonNode ln in layers)
            {
                var l = ln as JsonObject;
                if (l == null) continue;
                string name = GetString(l, "name", null);
                if (string.IsNullOrEmpty(name)) continue;
                int color = (int)GetDouble(l, "color", -1);   // ACI 1..255

                if (lt.Has(name))
                {
                    if (color >= 0 && color <= 255)
                    {
                        var rec = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);
                        rec.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                            Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)color);
                    }
                    continue;
                }
                lt.UpgradeOpen();
                var ltr = new LayerTableRecord { Name = name };
                if (color >= 0 && color <= 255)
                    ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                        Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)color);
                lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
            }
        }

        // Draw all circles/lines/arcs/polylines/texts from a into ms
        static void AddEntities(Database db, Transaction tr, BlockTableRecord ms,
                                JsonObject a, string defaultLayer, JsonArray drawn)
        {
            Action<Entity, JsonObject> place = (ent, spec) =>
            {
                // Order matters: append to the database first, then set Layer. An un-appended entity cannot resolve the layer name
                // (setting it first throws eKeyNotFound, stack pointing at Entity.set_Layer).
                ms.AppendEntity(ent);
                tr.AddNewlyCreatedDBObject(ent, true);
                string lay = GetString(spec, "layer", defaultLayer);
                if (!string.IsNullOrEmpty(lay) && lay != "0") ent.Layer = lay;
                // color: ACI index, ByLayer if omitted (must be specifiable to match the colour of existing labels)
                double ci = GetDouble(spec, "color", double.NaN);
                if (!double.IsNaN(ci))
                    ent.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                        Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)ci);
            };

            foreach (JsonObject c in Items(a, "circles"))
            {
                double r = GetDouble(c, "r", 0);
                if (r <= 0) throw new InvalidOperationException("Circle radius must be greater than 0 (got r=" + r + ").");
                double x = GetDouble(c, "x", 0), y = GetDouble(c, "y", 0);
                place(new Circle(new Point3d(x, y, 0), Vector3d.ZAxis, r), c);
                drawn.Add(new JsonObject { ["type"] = "Circle", ["x"] = x, ["y"] = y, ["r"] = r });
            }

            foreach (JsonObject l in Items(a, "lines"))
            {
                double x1 = GetDouble(l, "x1", 0), y1 = GetDouble(l, "y1", 0);
                double x2 = GetDouble(l, "x2", 0), y2 = GetDouble(l, "y2", 0);
                place(new Line(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0)), l);
                drawn.Add(new JsonObject
                {
                    ["type"] = "Line",
                    ["length"] = Math.Round(Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)), 4)
                });
            }

            foreach (JsonObject ar in Items(a, "arcs"))
            {
                double r = GetDouble(ar, "r", 0);
                if (r <= 0) throw new InvalidOperationException("Arc radius must be greater than 0.");
                double x = GetDouble(ar, "x", 0), y = GetDouble(ar, "y", 0);
                // Input in degrees (engineering convention), the API needs radians
                double sa = GetDouble(ar, "start_angle", 0) * Math.PI / 180.0;
                double ea = GetDouble(ar, "end_angle", 90) * Math.PI / 180.0;
                place(new Arc(new Point3d(x, y, 0), r, sa, ea), ar);
                drawn.Add(new JsonObject { ["type"] = "Arc", ["x"] = x, ["y"] = y, ["r"] = r });
            }

            foreach (JsonObject p in Items(a, "polylines"))
            {
                var pts = p["points"] as JsonArray;
                if (pts == null || pts.Count < 2)
                    throw new InvalidOperationException("A polyline needs at least 2 points, format points:[[x,y],[x,y],...].");
                var pl = new Polyline();
                double width = GetDouble(p, "width", 0);
                int i = 0;
                foreach (JsonNode pn in pts)
                {
                    var pair = pn as JsonArray;
                    if (pair == null || pair.Count < 2)
                        throw new InvalidOperationException("Each item in points must be [x, y].");
                    pl.AddVertexAt(i++, new Point2d(
                        pair[0].GetValue<double>(), pair[1].GetValue<double>()), 0, width, width);
                }
                pl.Closed = GetBool(p, "closed", false);
                place(pl, p);
                drawn.Add(new JsonObject
                {
                    ["type"] = "Polyline",
                    ["vertices"] = i,
                    ["closed"] = pl.Closed,
                    ["length"] = Math.Round(pl.Length, 4)
                });
            }

            foreach (JsonObject t in Items(a, "texts"))
            {
                string content = GetString(t, "text", null);
                if (string.IsNullOrEmpty(content))
                    throw new InvalidOperationException("Text is missing its text content.");
                double h = GetDouble(t, "height", 2.5);
                if (h <= 0) throw new InvalidOperationException("Text height must be greater than 0.");
                var dbt = new DBText
                {
                    Position = new Point3d(GetDouble(t, "x", 0), GetDouble(t, "y", 0), 0),
                    Height = h,
                    TextString = content,
                    Rotation = GetDouble(t, "rotation", 0) * Math.PI / 180.0
                };
                // Two things needed to match existing labels in the drawing: text style (CJK font) and width factor
                string sty = GetString(t, "style", null);
                if (!string.IsNullOrEmpty(sty))
                {
                    var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                    if (!tst.Has(sty))
                        throw new InvalidOperationException("The drawing has no text style '" + sty + "'.");
                    dbt.TextStyleId = tst[sty];
                }
                double wf = GetDouble(t, "width_factor", 0);
                if (wf > 0) dbt.WidthFactor = wf;
                place(dbt, t);
                drawn.Add(new JsonObject { ["type"] = "Text", ["text"] = content, ["height"] = h });
            }

            foreach (JsonObject hz in Items(a, "hatches"))
            {
                var pts = hz["points"] as JsonArray;
                if (pts == null || pts.Count < 3)
                    throw new InvalidOperationException("A hatch needs points:[[x,y],...] with at least 3 vertices.");
                var hatch = new Hatch();
                // Hatch call order matters: append to the database, set the pattern, attach the boundary loop, then evaluate.
                ms.AppendEntity(hatch);
                tr.AddNewlyCreatedDBObject(hatch, true);
                hatch.PatternScale = Math.Max(GetDouble(hz, "scale", 1.0), 1e-6);
                hatch.SetHatchPattern(HatchPatternType.PreDefined, GetString(hz, "pattern", "SOLID"));
                double hatchAngle = GetDouble(hz, "angle", 0);
                if (hatchAngle != 0) hatch.PatternAngle = hatchAngle * Math.PI / 180.0;
                var ringPts = new Point2dCollection();
                var ringBulges = new DoubleCollection();
                foreach (JsonNode pn in pts)
                {
                    var pair = pn as JsonArray;
                    if (pair == null || pair.Count < 2)
                        throw new InvalidOperationException("Each item in hatches.points must be [x, y].");
                    ringPts.Add(new Point2d(pair[0].GetValue<double>(), pair[1].GetValue<double>()));
                    ringBulges.Add(0);
                }
                hatch.AppendLoop(HatchLoopTypes.Default, ringPts, ringBulges);
                hatch.EvaluateHatch(true);
                string hatchLayer = GetString(hz, "layer", defaultLayer);
                if (!string.IsNullOrEmpty(hatchLayer) && hatchLayer != "0") hatch.Layer = hatchLayer;
                double hatchColor = GetDouble(hz, "color", double.NaN);
                if (!double.IsNaN(hatchColor))
                    hatch.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                        Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)hatchColor);
                drawn.Add(new JsonObject
                {
                    ["type"] = "Hatch",
                    ["pattern"] = hatch.PatternName,
                    ["vertices"] = ringPts.Count
                });
            }
        }

        // Take the JsonObject items of the a[key] array (missing/empty returns an empty sequence)
        static IEnumerable<JsonObject> Items(JsonObject a, string key)
        {
            var arr = a[key] as JsonArray;
            if (arr == null) yield break;
            foreach (JsonNode n in arr)
            {
                var o = n as JsonObject;
                if (o != null) yield return o;
            }
        }

        // ---------- Blocks ----------

        static JsonNode ListBlocks(JsonObject a, Document doc)
        {
            bool includeLayout = GetBool(a, "include_layout", false);
            Database db = doc.Database;
            var arr = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // Count how many times each block definition is inserted first
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    string nm = EffectiveBlockName(tr, br);
                    counts.TryGetValue(nm, out int c);
                    counts[nm] = c + 1;
                }

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId btrId in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                    if (!includeLayout && (btr.IsLayout || btr.IsAnonymous)) continue;

                    var tags = new JsonArray();
                    if (btr.HasAttributeDefinitions)
                    {
                        foreach (ObjectId eid in btr)
                        {
                            var ad = tr.GetObject(eid, OpenMode.ForRead) as AttributeDefinition;
                            if (ad != null && !ad.Constant) tags.Add(ad.Tag);
                        }
                    }
                    counts.TryGetValue(btr.Name, out int used);
                    arr.Add(new JsonObject
                    {
                        ["name"] = btr.Name,
                        ["has_attributes"] = btr.HasAttributeDefinitions,
                        ["attribute_tags"] = tags,
                        ["inserted"] = used
                    });
                }
                tr.Commit();
            }
            return arr;
        }

        // Title blocks are often inserted in **layouts**; scanning model space only would wrongly conclude "no title block in the drawing".
        // So model space and every layout are each treated as a "space" and walked uniformly.
        static List<KeyValuePair<string, ObjectId>> Spaces(Database db, Transaction tr, string which)
        {
            var list = new List<KeyValuePair<string, ObjectId>>();
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            bool wantModel = !string.Equals(which, "layout", StringComparison.OrdinalIgnoreCase);
            bool wantLayout = !string.Equals(which, "model", StringComparison.OrdinalIgnoreCase);

            if (wantModel)
                list.Add(new KeyValuePair<string, ObjectId>("Model", bt[BlockTableRecord.ModelSpace]));
            if (wantLayout)
            {
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry e in dict)
                {
                    var lo = tr.GetObject(e.Value, OpenMode.ForRead) as Layout;
                    if (lo == null || lo.ModelType) continue;
                    list.Add(new KeyValuePair<string, ObjectId>(lo.LayoutName, lo.BlockTableRecordId));
                }
            }
            return list;
        }

        // List block references with bounding boxes (model space + every layout). This is how to find "where is each frame, where is its lower-left corner".
        static JsonNode BlockRefs(JsonObject a, Document doc)
        {
            string filter = GetString(a, "name", null);
            string which = GetString(a, "space", "all");
            int max = (int)GetDouble(a, "max", 200);
            Database db = doc.Database;
            var arr = new JsonArray();
            int total = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
              foreach (var sp in Spaces(db, tr, which))
              {
                var btrSpace = (BlockTableRecord)tr.GetObject(sp.Value, OpenMode.ForRead);
                foreach (ObjectId id in btrSpace)
                {
                    var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    string nm = EffectiveBlockName(tr, br);
                    if (!string.IsNullOrEmpty(filter) &&
                        nm.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    total++;
                    if (arr.Count >= max) continue;

                    var o = new JsonObject
                    {
                        ["name"] = nm,
                        ["space"] = sp.Key,
                        ["layer"] = br.Layer,
                        ["handle"] = br.Handle.ToString(),
                        ["x"] = Math.Round(br.Position.X, 3),
                        ["y"] = Math.Round(br.Position.Y, 3),
                        ["rotation"] = Math.Round(br.Rotation * 180.0 / Math.PI, 3),
                        ["scale_x"] = Math.Round(br.ScaleFactors.X, 6),
                        ["scale_y"] = Math.Round(br.ScaleFactors.Y, 6)
                    };
                    // Empty/degenerate blocks throw eNullExtents on bounding box; record the reason and continue without failing the batch
                    try
                    {
                        Extents3d ex = br.GeometricExtents;
                        o["minx"] = Math.Round(ex.MinPoint.X, 3);
                        o["miny"] = Math.Round(ex.MinPoint.Y, 3);
                        o["maxx"] = Math.Round(ex.MaxPoint.X, 3);
                        o["maxy"] = Math.Round(ex.MaxPoint.Y, 3);
                        o["width"] = Math.Round(ex.MaxPoint.X - ex.MinPoint.X, 3);
                        o["height"] = Math.Round(ex.MaxPoint.Y - ex.MinPoint.Y, 3);
                    }
                    catch (System.Exception ex) { o["extents_error"] = ex.Message; }

                    arr.Add(o);
                }
              }
              tr.Commit();
            }
            return new JsonObject { ["count"] = total, ["returned"] = arr.Count, ["refs"] = arr };
        }

        // JsonNode -> string. String nodes give the raw value, numbers/booleans their JSON text, null becomes empty string.
        static string NodeToStr(JsonNode n)
        {
            if (n == null) return "";
            try { return n.GetValue<string>(); } catch { return n.ToString(); }
        }

        // Export block reference attributes (the single source of truth for title blocks). Read-only, no change to the drawing.
        // JSON is produced so that people can edit it and set_block_attributes writes it back as is; the handle is the anchor on both sides.
        static JsonNode DumpBlockAttributes(JsonObject a, Document doc)
        {
            string filter = GetString(a, "name", null);
            string which = GetString(a, "space", "all");
            int max = (int)GetDouble(a, "max", 500);
            bool onlyAttr = GetBool(a, "with_attributes_only", true);
            string outPath = GetString(a, "out", null);

            Database db = doc.Database;
            var arr = new JsonArray();
            int total = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
              foreach (var sp in Spaces(db, tr, which))
              {
                var btrSpace = (BlockTableRecord)tr.GetObject(sp.Value, OpenMode.ForRead);
                foreach (ObjectId id in btrSpace)
                {
                    var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    string nm = EffectiveBlockName(tr, br);
                    if (!string.IsNullOrEmpty(filter) &&
                        nm.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var dict = new JsonObject();
                    var list = new JsonArray();
                    foreach (ObjectId attId in br.AttributeCollection)
                    {
                        var att = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                        if (att == null) continue;
                        string tag = att.Tag ?? "";
                        string val = att.TextString ?? "";
                        dict[tag] = val;
                        list.Add(new JsonObject
                        {
                            ["tag"] = tag,
                            ["value"] = val,
                            ["handle"] = att.Handle.ToString(),
                            ["is_mtext"] = att.IsMTextAttribute
                        });
                    }
                    if (onlyAttr && list.Count == 0) continue;

                    total++;
                    if (arr.Count >= max) continue;

                    arr.Add(new JsonObject
                    {
                        ["handle"] = br.Handle.ToString(),
                        ["name"] = nm,
                        ["space"] = sp.Key,
                        ["layer"] = br.Layer,
                        ["x"] = Math.Round(br.Position.X, 3),
                        ["y"] = Math.Round(br.Position.Y, 3),
                        ["attributes"] = dict,
                        ["attribute_list"] = list
                    });
                }
              }
              tr.Commit();
            }

            var result = new JsonObject { ["count"] = total, ["returned"] = arr.Count, ["blocks"] = arr };
            if (!string.IsNullOrEmpty(outPath))
            {
                if (!Path.IsPathRooted(outPath))
                    throw new InvalidOperationException("out must be an absolute path: " + outPath);
                string dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var opts = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                File.WriteAllText(outPath, result.ToJsonString(opts), new System.Text.UTF8Encoding(false));
                result["out"] = outPath;
            }
            return result;
        }

        // Write block attributes back from JSON. Handle match takes precedence; without handles, batch-edit the same tags by block name + attributes.
        // Equal values are skipped (recorded as unchanged); tags missing in the drawing go into missing: one missing tag should not kill the whole batch.
        static JsonNode SetBlockAttributes(JsonObject a, Document doc)
        {
            string fromPath = GetString(a, "from", null);
            string filter = GetString(a, "name", null);
            string which = GetString(a, "space", "all");
            bool missingOk = GetBool(a, "missing_ok", true);

            var byHandle = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Action<JsonArray> take = items =>
            {
                if (items == null) return;
                foreach (var it in items)
                {
                    var o = it as JsonObject;
                    if (o == null) continue;
                    string h = GetString(o, "handle", null);
                    var attrs = o["attributes"] as JsonObject;
                    if (string.IsNullOrEmpty(h) || attrs == null) continue;
                    Dictionary<string, string> d;
                    if (!byHandle.TryGetValue(h, out d))
                    {
                        d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        byHandle[h] = d;
                    }
                    foreach (var kv in attrs) d[kv.Key] = NodeToStr(kv.Value);
                }
            };

            take(a["items"] as JsonArray);
            if (!string.IsNullOrEmpty(fromPath))
            {
                if (!File.Exists(fromPath))
                    throw new InvalidOperationException("from file does not exist: " + fromPath);
                var parsed = JsonNode.Parse(File.ReadAllText(fromPath)) as JsonObject;
                if (parsed != null) take(parsed["blocks"] as JsonArray);
            }

            var bulk = a["attributes"] as JsonObject;

            // Width factors: per tag, same scope as the batch edit with attributes (name filter + per-handle hits).
            // Can be given alone (squeeze text without changing values), so the "at least one" check below must include it.
            var widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var wfObj = a["width_factors"] as JsonObject;
            if (wfObj != null)
                foreach (var kv in wfObj)
                {
                    double f = GetDouble(wfObj, kv.Key, double.NaN);
                    if (double.IsNaN(f) || f <= 0)
                        throw new InvalidOperationException(
                            "width_factors['" + kv.Key + "'] must be positive, got: " + NodeToStr(kv.Value));
                    widths[kv.Key] = f;
                }

            if (byHandle.Count == 0 && bulk == null && widths.Count == 0)
                throw new InvalidOperationException(
                    "Give items/from (per-handle fill), or attributes (batch edit by name), or width_factors (text squeeze only).");

            Database db = doc.Database;
            int blocksTouched = 0, attrsSet = 0, unchanged = 0, widthSet = 0, widthUnchanged = 0;
            var missing = new JsonArray();
            var touched = new JsonArray();
            var mtextSkipped = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
              foreach (var sp in Spaces(db, tr, which))
              {
                var btrSpace = (BlockTableRecord)tr.GetObject(sp.Value, OpenMode.ForRead);
                foreach (ObjectId id in btrSpace)
                {
                    var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    string h = br.Handle.ToString();
                    string nm = EffectiveBlockName(tr, br);

                    Dictionary<string, string> want = null;
                    bool inScope = false;
                    if (byHandle.ContainsKey(h)) { want = byHandle[h]; inScope = true; }
                    else if (bulk != null || widths.Count > 0)
                    {
                        if (!string.IsNullOrEmpty(filter) &&
                            nm.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        inScope = true;
                        if (bulk != null)
                        {
                            want = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var kv in bulk) want[kv.Key] = NodeToStr(kv.Value);
                        }
                    }
                    // With only width_factors, want is empty but we still go on: that pass only squeezes text, no value change
                    if (!inScope) continue;
                    if ((want == null || want.Count == 0) && widths.Count == 0) continue;

                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int setHere = 0;
                    foreach (ObjectId attId in br.AttributeCollection)
                    {
                        var att = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                        if (att == null) continue;
                        string tag = att.Tag ?? "";
                        seen.Add(tag);

                        string v;
                        if (want != null && want.TryGetValue(tag, out v))
                        {
                            if (string.Equals(att.TextString ?? "", v, StringComparison.Ordinal)) unchanged++;
                            else
                            {
                                att.UpgradeOpen();
                                att.TextString = v;
                                // The MText content of a multi-line attribute does not follow TextString automatically; refresh explicitly
                                if (att.IsMTextAttribute) { try { att.UpdateMTextAttribute(); } catch { } }
                                att.DowngradeOpen();
                                attrsSet++; setHere++;
                            }
                        }

                        double wf;
                        if (widths.TryGetValue(tag, out wf))
                        {
                            // Multi-line attributes use MText layout; WidthFactor has no effect even if written. Record them, do not succeed silently
                            if (att.IsMTextAttribute)
                                mtextSkipped.Add(new JsonObject
                                { ["handle"] = h, ["block"] = nm, ["tag"] = tag });
                            else if (Math.Abs(att.WidthFactor - wf) < 1e-9) widthUnchanged++;
                            else
                            {
                                att.UpgradeOpen();
                                att.WidthFactor = wf;
                                att.DowngradeOpen();
                                widthSet++; setHere++;
                            }
                        }
                    }
                    var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (want != null) foreach (var kv in want) expected.Add(kv.Key);
                    foreach (var kv in widths) expected.Add(kv.Key);
                    foreach (string tagWanted in expected)
                        if (!seen.Contains(tagWanted))
                            missing.Add(new JsonObject { ["handle"] = h, ["block"] = nm, ["tag"] = tagWanted });

                    if (setHere > 0)
                    {
                        blocksTouched++;
                        touched.Add(new JsonObject
                        {
                            ["handle"] = h, ["block"] = nm, ["space"] = sp.Key, ["set"] = setHere
                        });
                    }
                }
              }
              tr.Commit();
            }

            if (missing.Count > 0 && !missingOk)
                throw new InvalidOperationException("These tags do not exist in the drawing: " + missing.ToJsonString());

            return new JsonObject
            {
                ["blocks_touched"] = blocksTouched,
                ["attributes_set"] = attrsSet,
                ["unchanged"] = unchanged,
                ["width_set"] = widthSet,
                ["width_unchanged"] = widthUnchanged,
                ["mtext_skipped_count"] = mtextSkipped.Count,
                ["mtext_skipped"] = mtextSkipped,
                ["missing_count"] = missing.Count,
                ["missing"] = missing,
                ["touched"] = touched
            };
        }

        // Overall model-space bounding box. When inserting the whole drawing as a block the base point is the source INSBASE, so report it too.
        static JsonNode ModelExtents(JsonObject a, Document doc)
        {
            string layerFilter = GetString(a, "layer", null);
            Database db = doc.Database;
            bool any = false;
            double minx = 0, miny = 0, maxx = 0, maxy = 0;
            int counted = 0, skipped = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    if (!string.IsNullOrEmpty(layerFilter) &&
                        ent.Layer.IndexOf(layerFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Extents3d ex;
                    try { ex = ent.GeometricExtents; } catch { skipped++; continue; }
                    if (!any)
                    {
                        minx = ex.MinPoint.X; miny = ex.MinPoint.Y;
                        maxx = ex.MaxPoint.X; maxy = ex.MaxPoint.Y; any = true;
                    }
                    else
                    {
                        minx = Math.Min(minx, ex.MinPoint.X); miny = Math.Min(miny, ex.MinPoint.Y);
                        maxx = Math.Max(maxx, ex.MaxPoint.X); maxy = Math.Max(maxy, ex.MaxPoint.Y);
                    }
                    counted++;
                }
                tr.Commit();
            }
            if (!any)
                throw new InvalidOperationException("Model space has no entity with a bounding box (layer filter too strict?)");

            return new JsonObject
            {
                ["entities_counted"] = counted,
                ["entities_skipped"] = skipped,
                ["minx"] = Math.Round(minx, 3),
                ["miny"] = Math.Round(miny, 3),
                ["maxx"] = Math.Round(maxx, 3),
                ["maxy"] = Math.Round(maxy, 3),
                ["width"] = Math.Round(maxx - minx, 3),
                ["height"] = Math.Round(maxy - miny, 3),
                ["insbase_x"] = Math.Round(db.Insbase.X, 3),
                ["insbase_y"] = Math.Round(db.Insbase.Y, 3)
            };
        }

        static JsonNode ListMarkedRegions(JsonObject a, Document doc)
        {
            bool markedOnly = GetBool(a, "marked_only", true);
            string layerFilter = GetString(a, "layer", null);
            int max = (int)GetDouble(a, "max", 100);
            var rows = new JsonArray();
            int candidates = 0;

            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(doc.Database, tr))
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (ent == null) continue;
                    bool supported = ent is Polyline || ent is Polyline2d || ent is Polyline3d || ent is Circle;
                    if (!supported) continue;
                    if (layerFilter != null
                        && ent.Layer.IndexOf(layerFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    candidates++;

                    var xdata = ResultBufferJson(ent.XData);
                    var ext = ExtensionDictionaryJson(tr, ent);
                    var links = HyperlinksJson(ent);
                    bool marked = HasMeaningfulXData(xdata) || ext.Count > 0 || links.Count > 0;
                    if (markedOnly && !marked) continue;
                    if (rows.Count >= max) continue;

                    var row = new JsonObject
                    {
                        ["handle"] = ent.Handle.ToString(),
                        ["type"] = ent.GetType().Name,
                        ["layer"] = ent.Layer,
                        ["closed"] = IsClosedRegion(ent),
                        ["marked"] = marked,
                        ["xdata"] = xdata,
                        ["extension_dictionary"] = ext,
                        ["hyperlinks"] = links,
                        ["vertices"] = RegionVerticesJson(tr, ent)
                    };
                    try
                    {
                        Extents3d ex = ent.GeometricExtents;
                        row["extents"] = new JsonObject
                        {
                            ["minx"] = ex.MinPoint.X, ["miny"] = ex.MinPoint.Y,
                            ["maxx"] = ex.MaxPoint.X, ["maxy"] = ex.MaxPoint.Y,
                            ["width"] = ex.MaxPoint.X - ex.MinPoint.X,
                            ["height"] = ex.MaxPoint.Y - ex.MinPoint.Y
                        };
                    }
                    catch { }
                    try
                    {
                        if (ent is Polyline pl) row["area"] = pl.Area;
                        else if (ent is Circle c) row["area"] = Math.PI * c.Radius * c.Radius;
                    }
                    catch { }
                    rows.Add(row);
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["candidate_count"] = candidates,
                ["returned_count"] = rows.Count,
                ["marked_only"] = markedOnly,
                ["regions"] = rows
            };
        }

        static JsonNode ListCivilViews(JsonObject a, Document doc)
        {
            string filter = (GetString(a, "type", "all") ?? "all").Trim().ToLowerInvariant();
            int max = (int)GetDouble(a, "max", 1000);
            var rows = new JsonArray();
            int sectionCount = 0, profileCount = 0, extentsFailed = 0;

            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(doc.Database, tr))
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (ent == null) continue;
                    string typeName = ent.GetType().Name;
                    bool isSection = typeName.Equals("SectionView", StringComparison.OrdinalIgnoreCase);
                    bool isProfile = typeName.Equals("ProfileView", StringComparison.OrdinalIgnoreCase);
                    if (!isSection && !isProfile) continue;
                    if (isSection) sectionCount++; else profileCount++;
                    if (filter == "section" && !isSection) continue;
                    if (filter == "profile" && !isProfile) continue;
                    if (rows.Count >= max) continue;

                    var row = new JsonObject
                    {
                        ["handle"] = ent.Handle.ToString(),
                        ["type"] = typeName,
                        ["layer"] = ent.Layer
                    };
                    try
                    {
                        var p = ent.GetType().GetProperty("Name");
                        if (p != null) row["name"] = Convert.ToString(p.GetValue(ent, null));
                    }
                    catch { }
                    try
                    {
                        Extents3d ex = ent.GeometricExtents;
                        row["extents"] = new JsonObject
                        {
                            ["minx"] = ex.MinPoint.X, ["miny"] = ex.MinPoint.Y,
                            ["maxx"] = ex.MaxPoint.X, ["maxy"] = ex.MaxPoint.Y,
                            ["width"] = ex.MaxPoint.X - ex.MinPoint.X,
                            ["height"] = ex.MaxPoint.Y - ex.MinPoint.Y
                        };
                    }
                    catch
                    {
                        extentsFailed++;
                    }
                    rows.Add(row);
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["section_view_count"] = sectionCount,
                ["profile_view_count"] = profileCount,
                ["returned_count"] = rows.Count,
                ["extents_failed"] = extentsFailed,
                ["views"] = rows
            };
        }

        static JsonNode ProfileLabelSetsDump(JsonObject a, Document doc)
        {
            string filter = GetString(a, "name", null);
            var result = new JsonArray();
            CivDoc civ = Civ(doc.Database);
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civ.Styles.LabelSetStyles.ProfileLabelSetStyles)
                {
                    var style = tr.GetObject(id, OpenMode.ForRead)
                        as Autodesk.Civil.DatabaseServices.Styles.ProfileLabelSetStyle;
                    if (style == null) continue;
                    if (!string.IsNullOrEmpty(filter)
                        && style.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var items = new JsonArray();
                    foreach (Autodesk.Civil.DatabaseServices.Styles.ProfileLabelSetItem item in style)
                    {
                        items.Add(new JsonObject
                        {
                            ["label_style_type"] = item.LabelStyleType.ToString(),
                            ["label_style_name"] = item.LabelStyleName,
                            ["label_style_id"] = item.LabelStyleId.Handle.ToString()
                        });
                    }
                    result.Add(new JsonObject
                    {
                        ["name"] = style.Name,
                        ["item_count"] = items.Count,
                        ["items"] = items
                    });
                }
                tr.Commit();
            }
            return result;
        }

        static JsonNode SetModelView(JsonObject a, Document doc)
        {
            if (a["minx"] == null || a["miny"] == null || a["maxx"] == null || a["maxy"] == null)
                throw new InvalidOperationException("set_model_view requires minx/miny/maxx/maxy.");
            double minx = GetDouble(a, "minx", 0);
            double miny = GetDouble(a, "miny", 0);
            double maxx = GetDouble(a, "maxx", 0);
            double maxy = GetDouble(a, "maxy", 0);
            if (maxx <= minx || maxy <= miny)
                throw new InvalidOperationException("Invalid window extent for set_model_view.");

            try
            {
                var lm = LayoutManager.Current;
                if (!string.Equals(lm.CurrentLayout, "Model", StringComparison.OrdinalIgnoreCase))
                    lm.CurrentLayout = "Model";
            }
            catch { }

            using (var view = doc.Editor.GetCurrentView())
            {
                view.CenterPoint = new Point2d((minx + maxx) / 2.0, (miny + maxy) / 2.0);
                view.Width = maxx - minx;
                view.Height = maxy - miny;
                doc.Editor.SetCurrentView(view);
            }

            return new JsonObject
            {
                ["minx"] = minx, ["miny"] = miny,
                ["maxx"] = maxx, ["maxy"] = maxy,
                ["center_x"] = (minx + maxx) / 2.0,
                ["center_y"] = (miny + maxy) / 2.0,
                ["width"] = maxx - minx,
                ["height"] = maxy - miny
            };
        }

        static bool IsClosedRegion(Entity ent)
        {
            if (ent is Polyline pl) return pl.Closed;
            if (ent is Polyline2d pl2) return pl2.Closed;
            if (ent is Polyline3d pl3) return pl3.Closed;
            return ent is Circle;
        }

        static JsonArray RegionVerticesJson(Transaction tr, Entity ent)
        {
            var arr = new JsonArray();
            if (ent is Polyline pl)
            {
                for (int i = 0; i < pl.NumberOfVertices; i++)
                {
                    Point2d p = pl.GetPoint2dAt(i);
                    arr.Add(new JsonArray(p.X, p.Y));
                }
            }
            else if (ent is Polyline2d pl2)
            {
                foreach (ObjectId vid in pl2)
                {
                    var v = tr.GetObject(vid, OpenMode.ForRead) as Vertex2d;
                    if (v != null) arr.Add(new JsonArray(v.Position.X, v.Position.Y, v.Position.Z));
                }
            }
            else if (ent is Polyline3d pl3)
            {
                foreach (ObjectId vid in pl3)
                {
                    var v = tr.GetObject(vid, OpenMode.ForRead) as PolylineVertex3d;
                    if (v != null) arr.Add(new JsonArray(v.Position.X, v.Position.Y, v.Position.Z));
                }
            }
            else if (ent is Circle c)
            {
                arr.Add(new JsonObject
                {
                    ["center"] = new JsonArray(c.Center.X, c.Center.Y, c.Center.Z),
                    ["radius"] = c.Radius
                });
            }
            return arr;
        }

        static JsonArray ResultBufferJson(ResultBuffer rb)
        {
            var arr = new JsonArray();
            if (rb == null) return arr;
            try
            {
                foreach (TypedValue tv in rb)
                    arr.Add(new JsonObject
                    {
                        ["code"] = tv.TypeCode,
                        ["value"] = tv.Value == null ? "" : Convert.ToString(tv.Value, CultureInfo.InvariantCulture)
                    });
            }
            catch { }
            return arr;
        }

        static bool HasMeaningfulXData(JsonArray xdata)
        {
            foreach (JsonNode n in xdata)
            {
                var o = n as JsonObject;
                if (o == null || o["code"] == null || o["value"] == null) continue;
                int code;
                if (!int.TryParse(o["code"].ToString(), out code) || code != 1001) continue;
                string app = o["value"].ToString();
                if (!string.Equals(app, "AeccUiBase", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        static JsonObject ExtensionDictionaryJson(Transaction tr, Entity ent)
        {
            var result = new JsonObject();
            if (ent.ExtensionDictionary.IsNull) return result;
            try
            {
                var dict = tr.GetObject(ent.ExtensionDictionary, OpenMode.ForRead) as DBDictionary;
                if (dict == null) return result;
                foreach (DBDictionaryEntry de in dict)
                {
                    var value = new JsonObject { ["type"] = de.Value.ObjectClass.Name };
                    try
                    {
                        var obj = tr.GetObject(de.Value, OpenMode.ForRead);
                        var xr = obj as Xrecord;
                        if (xr != null) value["data"] = ResultBufferJson(xr.Data);
                        else if (obj != null) value["properties"] = DumpShallow(tr, obj, 2);
                    }
                    catch { }
                    result[de.Key] = value;
                }
            }
            catch (System.Exception ex)
            {
                result["_error"] = ex.GetType().Name + ": " + Truncate(ex.Message, 120);
            }
            return result;
        }

        static JsonArray HyperlinksJson(Entity ent)
        {
            var arr = new JsonArray();
            try
            {
                var p = ent.GetType().GetProperty("Hyperlinks");
                var links = p == null ? null : p.GetValue(ent, null) as System.Collections.IEnumerable;
                if (links == null) return arr;
                foreach (object link in links)
                {
                    var row = new JsonObject();
                    foreach (string name in new[] { "Name", "Description", "DisplayString" })
                    {
                        try
                        {
                            var lp = link.GetType().GetProperty(name);
                            if (lp != null) row[name.ToLowerInvariant()] =
                                Convert.ToString(lp.GetValue(link, null), CultureInfo.InvariantCulture);
                        }
                        catch { }
                    }
                    arr.Add(row);
                }
            }
            catch { }
            return arr;
        }

        static string EffectiveBlockName(Transaction tr, BlockReference br)
        {
            try
            {
                ObjectId defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                return ((BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead)).Name;
            }
            catch { return "(unknown)"; }
        }

        // Find a layout's block table record by name (when the title block lives in a layout, the block must be inserted into that layout)
        static ObjectId LayoutBtr(Database db, Transaction tr, string layoutName)
        {
            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            var names = new List<string>();
            foreach (DBDictionaryEntry e in dict)
            {
                var lo = tr.GetObject(e.Value, OpenMode.ForRead) as Layout;
                if (lo == null) continue;
                if (string.Equals(lo.LayoutName, layoutName, StringComparison.OrdinalIgnoreCase))
                    return lo.BlockTableRecordId;
                names.Add(lo.LayoutName);
            }
            throw new InvalidOperationException(
                "The drawing has no layout named '" + layoutName + "'. Existing: " + string.Join(" / ", names));
        }

        // Insert a block reference; if from_dwg is given, import that dwg as the block definition first (redefined if the name exists)
        static void InsertBlocks(Database db, Transaction tr, BlockTableRecord ms,
                                 JsonObject a, string defaultLayer, JsonArray drawn)
        {
            string defaultSpace = GetString(a, "space", null);
            foreach (JsonObject b in Items(a, "blocks"))
            {
                string name = GetString(b, "name", null);
                if (string.IsNullOrEmpty(name))
                    throw new InvalidOperationException("Block insert is missing name.");
                string fromDwg = GetString(b, "from_dwg", null);
                // source_block: take one block definition by name from a block library file (the usual form of title-block libraries).
                // If omitted, fall back to "import the whole dwg as one block".
                string srcBlock = GetString(b, "source_block", null);

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                ObjectId btrId;

                if (!string.IsNullOrEmpty(fromDwg))
                {
                    if (!File.Exists(fromDwg))
                        throw new InvalidOperationException("Block source file not found: " + fromDwg);
                    using (Database src = new Database(false, true))
                    {
                        src.ReadDwgFile(fromDwg, FileOpenMode.OpenForReadAndAllShare, true, null);
                        src.CloseInput(true);

                        if (!string.IsNullOrEmpty(srcBlock))
                        {
                            // Clone the given block definition (together with its attribute definitions)
                            using (Transaction stx = src.TransactionManager.StartTransaction())
                            {
                                BlockTable sbt = (BlockTable)stx.GetObject(src.BlockTableId, OpenMode.ForRead);
                                if (!sbt.Has(srcBlock))
                                    throw new InvalidOperationException(
                                        "Block source file has no block definition '" + srcBlock + "': " + fromDwg +
                                        " (run list_blocks on that file first to check the names)");
                                var ids = new ObjectIdCollection();
                                ids.Add(sbt[srcBlock]);
                                var map = new IdMapping();
                                db.WblockCloneObjects(ids, db.BlockTableId, map,
                                                      DuplicateRecordCloning.Replace, false);
                                stx.Commit();
                            }
                            // After cloning the block keeps the source name; if name differs, look up by name (usually identical)
                            BlockTable bt2 = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                            string lookup = bt2.Has(name) ? name : srcBlock;
                            if (!bt2.Has(lookup))
                                throw new InvalidOperationException("Still not found after cloning the block definition: " + lookup);
                            btrId = bt2[lookup];
                        }
                        else btrId = db.Insert(name, src, false);   // import the whole dwg as one block definition
                    }
                }
                else
                {
                    if (!bt.Has(name))
                        throw new InvalidOperationException(
                            "The drawing has no block definition named '" + name + "' (use from_dwg to import from an external file, or run list_blocks first to check the names).");
                    btrId = bt[name];
                }

                double x = GetDouble(b, "x", 0), y = GetDouble(b, "y", 0);
                double scale = GetDouble(b, "scale", 1.0);
                if (scale <= 0) throw new InvalidOperationException("Block scale must be greater than 0.");
                double rot = GetDouble(b, "rotation", 0) * Math.PI / 180.0;

                var br = new BlockReference(new Point3d(x, y, 0), btrId)
                {
                    ScaleFactors = new Scale3d(scale),
                    Rotation = rot
                };

                // space: model space by default; a layout name inserts into that layout (required when the title block lives in a layout)
                string space = GetString(b, "space", defaultSpace);
                BlockTableRecord dest = ms;
                if (!string.IsNullOrEmpty(space) &&
                    !string.Equals(space, "Model", StringComparison.OrdinalIgnoreCase))
                    dest = (BlockTableRecord)tr.GetObject(LayoutBtr(db, tr, space), OpenMode.ForWrite);

                dest.AppendEntity(br);
                tr.AddNewlyCreatedDBObject(br, true);
                string lay = GetString(b, "layer", defaultLayer);
                if (!string.IsNullOrEmpty(lay) && lay != "0") br.Layer = lay;

                // Fill block attributes (sheet number/title of the frame rely on this)
                var wanted = b["attributes"] as JsonObject;
                int filled = 0;
                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                if (btr.HasAttributeDefinitions)
                {
                    foreach (ObjectId eid in btr)
                    {
                        var ad = tr.GetObject(eid, OpenMode.ForRead) as AttributeDefinition;
                        if (ad == null || ad.Constant) continue;
                        var ar = new AttributeReference();
                        ar.SetAttributeFromBlock(ad, br.BlockTransform);
                        if (wanted != null && wanted[ad.Tag] != null)
                        {
                            ar.TextString = wanted[ad.Tag].ToString();
                            filled++;
                        }
                        br.AttributeCollection.AppendAttribute(ar);
                        tr.AddNewlyCreatedDBObject(ar, true);
                    }
                }

                drawn.Add(new JsonObject
                {
                    ["type"] = "BlockReference",
                    ["name"] = name,
                    ["space"] = string.IsNullOrEmpty(space) ? "Model" : space,
                    ["x"] = x,
                    ["y"] = y,
                    ["scale"] = scale,
                    ["attributes_filled"] = filled,
                    ["imported_from"] = fromDwg ?? "(already in drawing)"
                });
            }
        }

        // ---------- Modify an existing drawing (preview copy by default) ----------

        static JsonNode ModifyDwg(JsonObject a, Document doc)
        {
            string target = GetString(a, "dwg", null);
            if (string.IsNullOrEmpty(target))
                throw new InvalidOperationException("Missing parameter dwg (path of the drawing to modify).");
            if (!Path.IsPathRooted(target))
                throw new InvalidOperationException("dwg must be an absolute path: " + target);
            if (!File.Exists(target))
                throw new InvalidOperationException("Drawing not found: " + target);

            bool apply = GetBool(a, "apply", false);      // preview by default, never modify the original unasked
            bool backup = GetBool(a, "backup", true);
            string layer = GetString(a, "layer", "0");
            var drawn = new JsonArray();
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            // Preview output path
            string outPath;
            if (apply) outPath = target;
            else
            {
                outPath = GetString(a, "out", null);
                if (string.IsNullOrEmpty(outPath))
                {
                    string dir = Path.GetDirectoryName(target);
                    string stem = Path.GetFileNameWithoutExtension(target);
                    outPath = Path.Combine(dir, stem + "_preview_" + stamp + ".dwg");
                }
            }

            string backupPath = null;
            if (apply && backup)
            {
                string dir = Path.GetDirectoryName(target);
                string stem = Path.GetFileNameWithoutExtension(target);
                backupPath = Path.Combine(dir, stem + "_backup_" + stamp + ".dwg");
                File.Copy(target, backupPath, false);      // let a failed backup throw; do not proceed wounded
            }

            using (Database db = new Database(false, true))
            {
                db.ReadDwgFile(target, FileOpenMode.OpenForReadAndAllShare, true, null);
                db.CloseInput(true);

                // Attach xrefs first (AttachXref dislikes running inside an open transaction); skip existing names, re-runnable
                foreach (JsonObject xr in Items(a, "xrefs"))
                {
                    string xrPath = GetString(xr, "path", null);
                    if (string.IsNullOrEmpty(xrPath) || !Path.IsPathRooted(xrPath))
                        throw new InvalidOperationException("xrefs.path must be an absolute path: " + (xrPath ?? "(empty)"));
                    if (!File.Exists(xrPath))
                        throw new InvalidOperationException("Xref file does not exist: " + xrPath);
                    string xrName = GetString(xr, "name", Path.GetFileNameWithoutExtension(xrPath));
                    bool already;
                    using (Transaction ck = db.TransactionManager.StartTransaction())
                    {
                        var ckBt = (BlockTable)ck.GetObject(db.BlockTableId, OpenMode.ForRead);
                        already = ckBt.Has(xrName);
                        ck.Commit();
                    }
                    if (already)
                    {
                        drawn.Add(new JsonObject
                        { ["type"] = "Xref", ["name"] = xrName, ["skipped"] = "block/xref with the same name already exists" });
                        continue;
                    }
                    ObjectId xrDefId = GetBool(xr, "overlay", false)
                        ? db.OverlayXref(xrPath, xrName)
                        : db.AttachXref(xrPath, xrName);
                    using (Transaction xtr = db.TransactionManager.StartTransaction())
                    {
                        var xbt = (BlockTable)xtr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        var xms = (BlockTableRecord)xtr.GetObject(
                            xbt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                        var xbr = new BlockReference(new Point3d(
                            GetDouble(xr, "x", 0), GetDouble(xr, "y", 0), 0), xrDefId);
                        xms.AppendEntity(xbr);
                        xtr.AddNewlyCreatedDBObject(xbr, true);
                        string xrLayer = GetString(xr, "layer", null);
                        if (!string.IsNullOrEmpty(xrLayer))
                        {
                            EnsureLayers(db, xtr, new JsonArray(new JsonObject { ["name"] = xrLayer }));
                            xbr.Layer = xrLayer;
                        }
                        xtr.Commit();
                    }
                    drawn.Add(new JsonObject
                    {
                        ["type"] = "Xref",
                        ["name"] = xrName,
                        ["path"] = xrPath,
                        ["overlay"] = GetBool(xr, "overlay", false)
                    });
                }

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    // Global space: entities (lines/texts/hatches...) can also go into the given layout; blocks have their own per-item space.
                    string spaceName = GetString(a, "space", null);
                    BlockTableRecord entDest = ms;
                    if (!string.IsNullOrEmpty(spaceName) &&
                        !string.Equals(spaceName, "Model", StringComparison.OrdinalIgnoreCase))
                        entDest = (BlockTableRecord)tr.GetObject(
                            LayoutBtr(db, tr, spaceName), OpenMode.ForWrite);

                    EnsureLayers(db, tr, a["layers"] as JsonArray);
                    AddEntities(db, tr, entDest, a, layer, drawn);
                    InsertBlocks(db, tr, ms, a, layer, drawn);
                    tr.Commit();
                }

                if (drawn.Count == 0)
                    throw new InvalidOperationException(
                        "Nothing to change. Available keys: blocks / circles / lines / arcs / polylines / texts / hatches.");

                db.SaveAs(outPath, DwgVersion.Current);
            }

            return new JsonObject
            {
                ["mode"] = apply ? "written back to original" : "preview copy (original unchanged)",
                ["source"] = target,
                ["output"] = outPath,
                ["backup"] = backupPath ?? (apply ? "(not backed up)" : "(no backup needed for preview)"),
                ["added"] = drawn.Count,
                ["items"] = drawn,
                ["bytes"] = File.Exists(outPath) ? new FileInfo(outPath).Length : 0
            };
        }

        // ---------- Model-space sheet frames ----------
        static JsonNode CreateSheetRegion(JsonObject a, Document doc)
        {
            double x = GetDouble(a, "x", double.NaN);
            double y = GetDouble(a, "y", double.NaN);
            string alignment = GetString(a, "alignment", null);
            string anchor = "explicit";
            if (double.IsNaN(x) || double.IsNaN(y))
            {
                if (string.IsNullOrWhiteSpace(alignment))
                    throw new InvalidOperationException(
                        "Sheet frame is missing lower-left x / y and no alignment is available for automatic positioning.");
                Database anchorDb = doc.Database;
                CivDoc anchorCiv = Civ(anchorDb);
                using (Transaction anchorTr = anchorDb.TransactionManager.StartTransaction())
                {
                    CivAlignment al = FindAlignment(anchorTr, anchorCiv, alignment);
                    if (al == null)
                        throw new InvalidOperationException("Alignment '" + alignment + "' not found.");
                    double e = 0, n = 0;
                    al.PointLocation(al.StartingStation, 0, ref e, ref n);
                    if (double.IsNaN(x)) x = e + GetDouble(a, "offset_x", -100);
                    if (double.IsNaN(y)) y = n + GetDouble(a, "offset_y", -1600);
                    anchor = "alignment_start:" + alignment;
                }
            }
            double scale = GetDouble(a, "scale", 500);
            if (scale <= 0) throw new InvalidOperationException("scale must be greater than 0.");
            string paper = GetString(a, "paper", "A3");
            string name = GetString(a, "name", "SHEET-01");
            string layerName = GetString(a, "layer", "C3DF-SHEET-REGION-NOPLOT");
            int count = Math.Max(1, (int)GetDouble(a, "count", 1));
            double pitchX = GetDouble(a, "pitch_x", 0);
            double pitchY = GetDouble(a, "pitch_y", 0);
            bool replaceExisting = GetBool(a, "replace_existing", true);

            double paperW, paperH;
            PaperSizeMm(paper, out paperW, out paperH);
            double modelW = paperW * scale / 1000.0;
            double modelH = paperH * scale / 1000.0;
            var windows = new JsonArray();
            int erased = 0;

            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(layerName))
                {
                    lt.UpgradeOpen();
                    var ltr = new LayerTableRecord
                    {
                        Name = layerName,
                        IsPlottable = false,
                        Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                            Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 8)
                    };
                    lt.Add(ltr);
                    tr.AddNewlyCreatedDBObject(ltr, true);
                }
                else
                {
                    var ltr = (LayerTableRecord)tr.GetObject(lt[layerName], OpenMode.ForWrite);
                    ltr.IsPlottable = false;
                }

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                for (int i = 0; i < count; i++)
                {
                    double xi = x + i * pitchX;
                    double yi = y + i * pitchY;
                    string itemName = count == 1 ? name : name + "-" + (i + 1).ToString("00");
                    if (replaceExisting)
                    {
                        var oldIds = new List<ObjectId>();
                        foreach (ObjectId oldId in ms) oldIds.Add(oldId);
                        foreach (ObjectId oldId in oldIds)
                        {
                            var oldEntity = tr.GetObject(oldId, OpenMode.ForRead, false) as Entity;
                            if (oldEntity == null || oldEntity.IsErased
                                || !string.Equals(oldEntity.Layer, layerName,
                                    StringComparison.OrdinalIgnoreCase)) continue;
                            bool same = false;
                            var oldText = oldEntity as DBText;
                            if (oldText != null)
                                same = oldText.TextString.StartsWith(itemName + "  ",
                                    StringComparison.Ordinal)
                                    && oldText.Position.DistanceTo(
                                        new Point3d(xi, yi + modelH, 0)) < 0.001;
                            var oldFrame = oldEntity as Polyline;
                            if (oldFrame != null && oldFrame.Closed && oldFrame.NumberOfVertices == 4)
                            {
                                try
                                {
                                    Extents3d ext = oldFrame.GeometricExtents;
                                    same = ext.MinPoint.DistanceTo(new Point3d(xi, yi, 0)) < 0.001
                                        && ext.MaxPoint.DistanceTo(
                                            new Point3d(xi + modelW, yi + modelH, 0)) < 0.001;
                                }
                                catch { }
                            }
                            if (!same) continue;
                            oldEntity.UpgradeOpen();
                            oldEntity.Erase();
                            erased++;
                        }
                    }
                    var pl = new Polyline(4) { Closed = true, Layer = layerName };
                    pl.AddVertexAt(0, new Point2d(xi, yi), 0, 0, 0);
                    pl.AddVertexAt(1, new Point2d(xi + modelW, yi), 0, 0, 0);
                    pl.AddVertexAt(2, new Point2d(xi + modelW, yi + modelH), 0, 0, 0);
                    pl.AddVertexAt(3, new Point2d(xi, yi + modelH), 0, 0, 0);
                    ms.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);

                    var label = new DBText
                    {
                        Position = new Point3d(xi, yi + modelH, 0),
                        Height = Math.Max(modelH / 80.0, 0.5),
                        TextString = itemName + "  " + paper + "  1:" +
                                     scale.ToString("0.##", CultureInfo.InvariantCulture),
                        Layer = layerName
                    };
                    ms.AppendEntity(label);
                    tr.AddNewlyCreatedDBObject(label, true);
                    windows.Add(new JsonObject
                    {
                        ["name"] = itemName,
                        ["minx"] = xi, ["miny"] = yi,
                        ["maxx"] = xi + modelW, ["maxy"] = yi + modelH,
                        ["width"] = modelW, ["height"] = modelH
                    });
                }
                tr.Commit();
            }

            var result = new JsonObject
            {
                ["name"] = name,
                ["paper"] = paper,
                ["scale"] = scale,
                ["paper_space_scale"] = 1000.0 / scale,
                ["layer"] = layerName,
                ["count"] = count,
                ["pitch_x"] = pitchX,
                ["pitch_y"] = pitchY,
                ["anchor"] = anchor,
                ["replace_existing"] = replaceExisting,
                ["old_entities_erased"] = erased,
                ["windows"] = windows
            };
            if (count == 1) result["window"] = windows[0].DeepClone();
            return result;
        }

        // ---------- Entity-based layout sheet: model-space content copied straight into paper space ----------
        static JsonNode ComposeLayoutSheet(JsonObject a, Document doc)
        {
            string layoutName = GetString(a, "layout", "C3DF-A3-Entities");
            string paper = GetString(a, "paper", "A3");
            bool clear = GetBool(a, "clear", true);
            var frameArg = a["frame"] as JsonObject;
            if (frameArg == null)
                throw new InvalidOperationException(
                    "Missing frame{block,from_dwg,x,y,scale,attributes}.");

            double paperW, paperH;
            PaperSizeMm(paper, out paperW, out paperH);

            Database db = doc.Database;
            var lm = LayoutManager.Current;
            ObjectId layoutId = lm.GetLayoutId(layoutName);
            bool created = layoutId.IsNull;
            if (created) layoutId = lm.CreateLayout(layoutName);
            if (!string.Equals(lm.CurrentLayout, layoutName, StringComparison.Ordinal))
                lm.CurrentLayout = layoutName;

            var sourceArgs = a["sources"] as JsonArray;
            if (sourceArgs == null || sourceArgs.Count == 0)
            {
                sourceArgs = new JsonArray
                {
                    new JsonObject
                    {
                        ["target"] = new JsonObject
                        {
                            ["x"] = 20, ["y"] = 72,
                            ["width"] = paperW - 35, ["height"] = paperH - 92
                        }
                    }
                };
            }

            var copied = new JsonArray();
            var drawn = new JsonArray();
            int totalCloned = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForWrite);
                var paperSpace = (BlockTableRecord)tr.GetObject(
                    layout.BlockTableRecordId, OpenMode.ForWrite);

                if (clear)
                {
                    foreach (ObjectId id in paperSpace)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        var oldVp = ent as Autodesk.AutoCAD.DatabaseServices.Viewport;
                        if (oldVp != null && oldVp.Number == 1) continue;
                        ent.UpgradeOpen();
                        ent.Erase();
                    }
                }

                int sourceIndex = 0;
                foreach (JsonNode node in sourceArgs)
                {
                    var srcArg = node as JsonObject;
                    if (srcArg == null) continue;
                    sourceIndex++;

                    var target = srcArg["target"] as JsonObject;
                    double tx = target == null ? 20 : GetDouble(target, "x", 20);
                    double ty = target == null ? 72 : GetDouble(target, "y", 72);
                    double tw = target == null ? paperW - 35 : GetDouble(target, "width", paperW - 35);
                    double th = target == null ? paperH - 92 : GetDouble(target, "height", paperH - 92);
                    if (tw <= 0 || th <= 0)
                        throw new InvalidOperationException("source target width/height must be greater than 0.");
                    double rotation = GetDouble(srcArg, "rotation", 0) * Math.PI / 180.0;
                    string sourcePath = GetString(srcArg, "dwg", null);
                    var sourceWindow = srcArg["source_window"] as JsonObject;
                    bool crossing = GetBool(srcArg, "crossing", false);
                    double swMinX = sourceWindow == null ? double.NegativeInfinity : GetDouble(sourceWindow, "minx", 0);
                    double swMinY = sourceWindow == null ? double.NegativeInfinity : GetDouble(sourceWindow, "miny", 0);
                    double swMaxX = sourceWindow == null ? double.PositiveInfinity : GetDouble(sourceWindow, "maxx", 0);
                    double swMaxY = sourceWindow == null ? double.PositiveInfinity : GetDouble(sourceWindow, "maxy", 0);
                    if (sourceWindow != null && (swMaxX <= swMinX || swMaxY <= swMinY))
                        throw new InvalidOperationException("Invalid source_window extent.");
                    var excludedLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var excludedArg = srcArg["exclude_layers"] as JsonArray;
                    if (excludedArg != null)
                        foreach (JsonNode layerNode in excludedArg)
                            if (layerNode != null) excludedLayers.Add(layerNode.ToString());

                    var ids = new ObjectIdCollection();
                    Extents3d bounds = new Extents3d();
                    bool haveBounds = false;
                    var map = new IdMapping();

                    if (string.IsNullOrEmpty(sourcePath))
                    {
                        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        var ms = (BlockTableRecord)tr.GetObject(
                            bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                        foreach (ObjectId id in ms)
                        {
                            var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null || ent.IsErased) continue;
                            if (excludedLayers.Contains(ent.Layer)) continue;
                            try
                            {
                                Extents3d ex = ent.GeometricExtents;
                                if (sourceWindow != null)
                                {
                                    bool inside = ex.MinPoint.X >= swMinX && ex.MaxPoint.X <= swMaxX &&
                                                  ex.MinPoint.Y >= swMinY && ex.MaxPoint.Y <= swMaxY;
                                    bool intersects = !(ex.MaxPoint.X < swMinX || ex.MinPoint.X > swMaxX ||
                                                        ex.MaxPoint.Y < swMinY || ex.MinPoint.Y > swMaxY);
                                    if (crossing ? !intersects : !inside) continue;
                                }
                                if (!haveBounds) { bounds = ex; haveBounds = true; }
                                else bounds.AddExtents(ex);
                                ids.Add(id);
                            }
                            catch { }
                        }
                        if (ids.Count == 0 || !haveBounds)
                            throw new InvalidOperationException("The current drawing's model space has no entities to copy.");
                        db.DeepCloneObjects(ids, paperSpace.ObjectId, map, false);
                        sourcePath = SafeFile(db);
                    }
                    else
                    {
                        if (!File.Exists(sourcePath))
                            throw new InvalidOperationException("Source DWG not found: " + sourcePath);
                        using (var src = new Database(false, true))
                        {
                            src.ReadDwgFile(sourcePath, FileOpenMode.OpenForReadAndAllShare, true, null);
                            src.CloseInput(true);
                            using (var stx = src.TransactionManager.StartTransaction())
                            {
                                var sbt = (BlockTable)stx.GetObject(src.BlockTableId, OpenMode.ForRead);
                                var sms = (BlockTableRecord)stx.GetObject(
                                    sbt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                                foreach (ObjectId id in sms)
                                {
                                    var ent = stx.GetObject(id, OpenMode.ForRead) as Entity;
                                    if (ent == null || ent.IsErased) continue;
                                    if (excludedLayers.Contains(ent.Layer)) continue;
                                    try
                                    {
                                        Extents3d ex = ent.GeometricExtents;
                                        if (sourceWindow != null)
                                        {
                                            bool inside = ex.MinPoint.X >= swMinX && ex.MaxPoint.X <= swMaxX &&
                                                          ex.MinPoint.Y >= swMinY && ex.MaxPoint.Y <= swMaxY;
                                            bool intersects = !(ex.MaxPoint.X < swMinX || ex.MinPoint.X > swMaxX ||
                                                                ex.MaxPoint.Y < swMinY || ex.MinPoint.Y > swMaxY);
                                            if (crossing ? !intersects : !inside) continue;
                                        }
                                        if (!haveBounds) { bounds = ex; haveBounds = true; }
                                        else bounds.AddExtents(ex);
                                        ids.Add(id);
                                    }
                                    catch { }
                                }
                                if (ids.Count == 0 || !haveBounds)
                                    throw new InvalidOperationException(
                                        "Source model space has no entities to copy: " + sourcePath);
                                src.WblockCloneObjects(ids, paperSpace.ObjectId, map,
                                                       DuplicateRecordCloning.Ignore, false);
                                stx.Commit();
                            }
                        }
                    }

                    double sourceW = bounds.MaxPoint.X - bounds.MinPoint.X;
                    double sourceH = bounds.MaxPoint.Y - bounds.MinPoint.Y;
                    if (sourceW <= 0 || sourceH <= 0)
                        throw new InvalidOperationException("Invalid source extent: " + sourcePath);
                    bool fixedScale = srcArg["paper_scale"] != null;
                    double scale;
                    Point3d sourceOrigin;
                    Point3d targetOrigin;
                    if (fixedScale)
                    {
                        scale = GetDouble(srcArg, "paper_scale", 0);
                        if (scale <= 0)
                            throw new InvalidOperationException("paper_scale must be greater than 0.");
                        sourceOrigin = sourceWindow == null
                            ? bounds.MinPoint
                            : new Point3d(swMinX, swMinY, 0);
                        targetOrigin = new Point3d(tx, ty, 0);
                    }
                    else
                    {
                        double fitW = Math.Abs(Math.Cos(rotation)) * sourceW +
                                      Math.Abs(Math.Sin(rotation)) * sourceH;
                        double fitH = Math.Abs(Math.Sin(rotation)) * sourceW +
                                      Math.Abs(Math.Cos(rotation)) * sourceH;
                        scale = Math.Min(tw / fitW, th / fitH);
                        scale *= GetDouble(srcArg, "fit_factor", 0.98);
                        sourceOrigin = new Point3d(
                            (bounds.MinPoint.X + bounds.MaxPoint.X) / 2.0,
                            (bounds.MinPoint.Y + bounds.MaxPoint.Y) / 2.0, 0);
                        targetOrigin = new Point3d(tx + tw / 2.0, ty + th / 2.0, 0);
                    }
                    int clonedHere = 0;
                    foreach (IdPair pair in map)
                    {
                        if (!pair.IsCloned || pair.Value.IsNull) continue;
                        var ent = tr.GetObject(pair.Value, OpenMode.ForWrite, false) as Entity;
                        if (ent == null || ent.IsErased) continue;
                        ent.TransformBy(Matrix3d.Displacement(Point3d.Origin - sourceOrigin));
                        ent.TransformBy(Matrix3d.Scaling(scale, Point3d.Origin));
                        if (Math.Abs(rotation) > 1e-12)
                            ent.TransformBy(Matrix3d.Rotation(
                                rotation, Vector3d.ZAxis, Point3d.Origin));
                        ent.TransformBy(Matrix3d.Displacement(targetOrigin - Point3d.Origin));
                        clonedHere++;
                    }
                    totalCloned += clonedHere;
                    copied.Add(new JsonObject
                    {
                        ["source"] = sourcePath,
                        ["entities"] = clonedHere,
                        ["source_width"] = Math.Round(sourceW, 4),
                        ["source_height"] = Math.Round(sourceH, 4),
                        ["paper_scale"] = Math.Round(scale, 8),
                        ["scale_mode"] = fixedScale ? "fixed" : "fit",
                        ["target"] = new JsonObject
                        {
                            ["x"] = tx, ["y"] = ty, ["width"] = tw, ["height"] = th
                        }
                    });
                }

                var notes = a["notes"] as JsonArray;
                if (notes != null && notes.Count > 0)
                {
                    var noteText = new System.Text.StringBuilder();
                    int n = 0;
                    foreach (JsonNode note in notes)
                    {
                        if (n > 0) noteText.Append("\\P");
                        noteText.Append((n + 1).ToString(CultureInfo.InvariantCulture));
                        noteText.Append(", ");
                        noteText.Append(note == null ? "" : note.ToString());
                        n++;
                    }
                    var mt = new MText
                    {
                        Location = new Point3d(
                            GetDouble(a, "notes_x", 30),
                            GetDouble(a, "notes_y", 58), 0),
                        Width = GetDouble(a, "notes_width", 230),
                        TextHeight = GetDouble(a, "notes_height", 2.5),
                        Attachment = AttachmentPoint.TopLeft,
                        Contents = "Notes:" + "\\P" + noteText.ToString()
                    };
                    paperSpace.AppendEntity(mt);
                    tr.AddNewlyCreatedDBObject(mt, true);
                    drawn.Add(new JsonObject { ["type"] = "MText", ["space"] = layoutName });
                }

                // The frame file itself lives in model space; without source_block the whole DWG is imported as the block definition.
                var frame = (JsonObject)JsonNode.Parse(frameArg.ToJsonString());
                string frameName = Need(frame, "block");
                string frameDwg = GetString(frame, "from_dwg", null);
                string frameSourceBlock = GetString(frame, "source_block", null);
                var bt2 = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                // Database.Insert runs at high CPU for a long time in hosts with many AEC dependencies.
                // For the "whole DWG as frame" case, clone the source model-space entities straight into a new block definition.
                if (!bt2.Has(frameName) && !string.IsNullOrEmpty(frameDwg) &&
                    string.IsNullOrEmpty(frameSourceBlock))
                {
                    if (!File.Exists(frameDwg))
                        throw new InvalidOperationException("Frame source file not found: " + frameDwg);
                    bt2.UpgradeOpen();
                    var frameDef = new BlockTableRecord
                    {
                        Name = frameName,
                        Origin = Point3d.Origin
                    };
                    ObjectId frameDefId = bt2.Add(frameDef);
                    tr.AddNewlyCreatedDBObject(frameDef, true);

                    using (var frameDb = new Database(false, true))
                    {
                        frameDb.ReadDwgFile(
                            frameDwg, FileOpenMode.OpenForReadAndAllShare, true, null);
                        frameDb.CloseInput(true);
                        using (var ftx = frameDb.TransactionManager.StartTransaction())
                        {
                            var fbt = (BlockTable)ftx.GetObject(
                                frameDb.BlockTableId, OpenMode.ForRead);
                            var fms = (BlockTableRecord)ftx.GetObject(
                                fbt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                            var frameIds = new ObjectIdCollection();
                            foreach (ObjectId id in fms) frameIds.Add(id);
                            if (frameIds.Count == 0)
                                throw new InvalidOperationException(
                                    "Frame source model space is empty: " + frameDwg);
                            var frameMap = new IdMapping();
                            frameDb.WblockCloneObjects(
                                frameIds, frameDefId, frameMap,
                                DuplicateRecordCloning.Ignore, false);
                            ftx.Commit();
                        }
                    }
                    frame.Remove("from_dwg");
                }
                // compose_layout_sheet exposes frame.block; when reusing the generic block insert it maps to blocks[].name.
                frame["name"] = frameName;
                frame["space"] = layoutName;
                if (frame["x"] == null) frame["x"] = 0;
                if (frame["y"] == null) frame["y"] = 0;
                if (frame["scale"] == null) frame["scale"] = 1;
                var blockArgs = new JsonObject
                {
                    ["space"] = layoutName,
                    ["blocks"] = new JsonArray(frame)
                };
                var modelSpace = (BlockTableRecord)tr.GetObject(
                    bt2[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                InsertBlocks(db, tr, modelSpace, blockArgs, "0", drawn);

                PlotSettingsValidator psv = PlotSettingsValidator.Current;
                psv.SetPlotConfigurationName(layout, "DWG To PDF.pc3", null);
                psv.RefreshLists(layout);
                string media = null, firstMatch = null;
                foreach (string m in psv.GetCanonicalMediaNameList(layout))
                {
                    if (m.IndexOf(paper, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (firstMatch == null) firstMatch = m;
                    if (m.IndexOf("full_bleed", StringComparison.OrdinalIgnoreCase) >= 0)
                    { media = m; break; }
                }
                media = media ?? firstMatch;
                if (media == null)
                    throw new InvalidOperationException("DWG To PDF.pc3 has no paper size: " + paper);
                psv.SetCanonicalMediaName(layout, media);
                psv.SetPlotType(layout, AcDbPlotType.Layout);
                psv.SetUseStandardScale(layout, true);
                psv.SetStdScaleType(layout, StdScaleType.StdScale1To1);
                psv.SetPlotPaperUnits(layout, PlotPaperUnit.Millimeters);
                psv.SetPlotRotation(layout, PlotRotation.Degrees090);
                layout.PlotPlotStyles = true;
                psv.SetCurrentStyleSheet(layout, "monochrome.ctb");
                layout.PrintLineweights = true;
                tr.Commit();
            }

            try { lm.CurrentLayout = layoutName; } catch { }
            return new JsonObject
            {
                ["layout"] = layoutName,
                ["created"] = created,
                ["paper"] = paper + " " + paperW + "×" + paperH + " mm",
                ["mode"] = "entities copied straight into paper space (no model viewport)",
                ["entities_cloned"] = totalCloned,
                ["sources"] = copied,
                ["items_added"] = drawn
            };
        }

        // ---------- Layout sheet: paper-space frame + model-space viewport ----------
        static JsonNode CreateLayoutSheet(JsonObject a, Document doc)
        {
            string layoutName = GetString(a, "layout", "C3DF-A3");
            string blockName = Need(a, "block");
            string fromDwg = GetString(a, "from_dwg", null);
            string paper = GetString(a, "paper", "A3");
            var win = a["model_window"] as JsonObject;
            string boundaryHandle = GetString(a, "boundary_handle", null);
            string boundaryLayer = GetString(a, "boundary_layer", null);
            double minx = 0, miny = 0, maxx = 0, maxy = 0;
            string windowSource = "model_window";
            string matchedBoundaryHandle = null;

            if (win != null)
            {
                minx = GetDouble(win, "minx", 0); miny = GetDouble(win, "miny", 0);
                maxx = GetDouble(win, "maxx", 0); maxy = GetDouble(win, "maxy", 0);
            }
            else
            {
                if (string.IsNullOrEmpty(boundaryHandle) && string.IsNullOrEmpty(boundaryLayer))
                    throw new InvalidOperationException(
                        "Provide model_window, or use boundary_handle / boundary_layer to specify the model-space source frame.");
                Database lookupDb = doc.Database;
                using (Transaction lookupTr = lookupDb.TransactionManager.StartTransaction())
                {
                    var lookupBt = (BlockTable)lookupTr.GetObject(lookupDb.BlockTableId, OpenMode.ForRead);
                    var lookupMs = (BlockTableRecord)lookupTr.GetObject(
                        lookupBt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    Entity boundary = null;
                    foreach (ObjectId id in lookupMs)
                    {
                        var ent = lookupTr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        if (!string.IsNullOrEmpty(boundaryHandle) &&
                            !ent.Handle.ToString().Equals(boundaryHandle, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!string.IsNullOrEmpty(boundaryLayer) &&
                            ent.Layer.IndexOf(boundaryLayer, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        if (!(ent is Polyline) && !(ent is Polyline2d) && !(ent is Polyline3d) &&
                            !(ent is Circle))
                            continue;
                        boundary = ent;
                    }
                    if (boundary == null)
                        throw new InvalidOperationException("The specified plan source frame was not found.");
                    Extents3d ext = boundary.GeometricExtents;
                    minx = ext.MinPoint.X; miny = ext.MinPoint.Y;
                    maxx = ext.MaxPoint.X; maxy = ext.MaxPoint.Y;
                    matchedBoundaryHandle = boundary.Handle.ToString();
                    windowSource = !string.IsNullOrEmpty(boundaryHandle)
                        ? "boundary_handle" : "boundary_layer";
                    lookupTr.Commit();
                }
            }
            if (maxx <= minx || maxy <= miny)
                throw new InvalidOperationException("Invalid model-space source extent.");

            double paperW, paperH;
            PaperSizeMm(paper, out paperW, out paperH);

            var vpArg = a["viewport"] as JsonObject;
            double vpX = vpArg == null ? paperW * 0.42 : GetDouble(vpArg, "x", paperW * 0.42);
            double vpY = vpArg == null ? paperH * 0.52 : GetDouble(vpArg, "y", paperH * 0.52);
            double vpW = vpArg == null ? paperW - 70 : GetDouble(vpArg, "width", paperW - 70);
            double vpH = vpArg == null ? paperH - 30 : GetDouble(vpArg, "height", paperH - 30);
            double vpScale = vpArg == null ? 0 : GetDouble(vpArg, "scale", 0);
            bool vpLocked = vpArg == null ? true : GetBool(vpArg, "locked", true);
            if (vpW <= 0 || vpH <= 0) throw new InvalidOperationException("viewport size must be greater than 0.");
            if (vpScale < 0) throw new InvalidOperationException("viewport.scale must be greater than 0.");

            double frameX = GetDouble(a, "frame_x", -9.372);
            double frameY = GetDouble(a, "frame_y", -10.01);
            double frameScale = GetDouble(a, "frame_scale", 1);
            string frameLayer = GetString(a, "frame_layer", "0");
            bool clearLayout = GetBool(a, "clear", true);

            Database db = doc.Database;
            var lm = LayoutManager.Current;
            ObjectId layoutId = lm.GetLayoutId(layoutName);
            bool created = layoutId.IsNull;
            if (created) layoutId = lm.CreateLayout(layoutName);
            // Viewport.On may only be set in the current paper-space layout; otherwise eNotInPaperspace.
            if (!string.Equals(lm.CurrentLayout, layoutName, StringComparison.Ordinal))
                lm.CurrentLayout = layoutName;

            int attributesFilled = 0;
            var extraVpReport = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForWrite);
                var paperSpace = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);

                // On re-run clear the ordinary entities and user viewports in this layout; keep the system paper-space viewport 1.
                // clear:false stacks instead: several frames + viewports side by side in one layout (for merged plan sheets).
                if (clearLayout)
                {
                    foreach (ObjectId id in paperSpace)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        var oldVp = ent as Autodesk.AutoCAD.DatabaseServices.Viewport;
                        if (oldVp != null && oldVp.Number == 1) continue;
                        ent.UpgradeOpen();
                        ent.Erase();
                    }
                }

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (!bt.Has(blockName))
                {
                    if (string.IsNullOrEmpty(fromDwg))
                        throw new InvalidOperationException("The drawing has no block '" + blockName + "'; from_dwg is required.");
                    if (!File.Exists(fromDwg)) throw new InvalidOperationException("Block library file not found: " + fromDwg);
                    using (var src = new Database(false, true))
                    {
                        src.ReadDwgFile(fromDwg, FileOpenMode.OpenForReadAndAllShare, true, null);
                        src.CloseInput(true);
                        using (var stx = src.TransactionManager.StartTransaction())
                        {
                            var sbt = (BlockTable)stx.GetObject(src.BlockTableId, OpenMode.ForRead);
                            if (sbt.Has(blockName))
                            {
                                var ids = new ObjectIdCollection { sbt[blockName] };
                                var map = new IdMapping();
                                db.WblockCloneObjects(ids, db.BlockTableId, map,
                                                      DuplicateRecordCloning.Replace, false);
                            }
                            else
                            {
                                // The frame library may also be a "bare frame DWG" without a same-named block definition;
                                // in that case import the whole DWG as blockName.
                                db.Insert(blockName, src, false);
                            }
                            stx.Commit();
                        }
                    }
                    bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                }

                // The frame stays in paper space; this project's TK block has its base point about 9.372/10.01 mm off the outer lower-left corner.
                var frame = new BlockReference(new Point3d(frameX, frameY, 0), bt[blockName])
                {
                    ScaleFactors = new Scale3d(frameScale),
                    Layer = frameLayer
                };
                paperSpace.AppendEntity(frame);
                tr.AddNewlyCreatedDBObject(frame, true);

                var wanted = a["attributes"] as JsonObject;
                var widthFactors = a["width_factors"] as JsonObject;   // {tag:factor}, for squeezing long sheet titles into narrow cells
                var frameDef = (BlockTableRecord)tr.GetObject(bt[blockName], OpenMode.ForRead);
                if (frameDef.HasAttributeDefinitions)
                {
                    foreach (ObjectId id in frameDef)
                    {
                        var ad = tr.GetObject(id, OpenMode.ForRead) as AttributeDefinition;
                        if (ad == null || ad.Constant) continue;
                        var ar = new AttributeReference();
                        ar.SetAttributeFromBlock(ad, frame.BlockTransform);
                        if (wanted != null && wanted[ad.Tag] != null)
                        {
                            ar.TextString = wanted[ad.Tag].ToString();
                            attributesFilled++;
                        }
                        if (widthFactors != null && widthFactors[ad.Tag] != null)
                        {
                            double wfv = GetDouble(widthFactors, ad.Tag, 0);
                            if (wfv > 0) ar.WidthFactor = wfv;
                        }
                        frame.AttributeCollection.AppendAttribute(ar);
                        tr.AddNewlyCreatedDBObject(ar, true);
                    }
                }

                // Viewport border goes to a non-plotting layer by default; viewport.layer may give another (a plottable one to plot the border).
                const string vpLayerName = "C3DF-VPORT-NOPLOT";
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(vpLayerName))
                {
                    lt.UpgradeOpen();
                    var ltr = new LayerTableRecord { Name = vpLayerName, IsPlottable = false };
                    lt.Add(ltr);
                    tr.AddNewlyCreatedDBObject(ltr, true);
                    lt.DowngradeOpen();
                }

                // Source drawing layers often carry the 'frozen in new viewports' state, which hides whole layers in new viewports
                // (invisible to DXF parsing; the vanished existing-elevation layer on project B SY5 was exactly this).
                // So every new viewport thaws all layers first, then freezes by parameter: behaviour independent of the source state.
                Func<string, ObjectId> layerIdStrict = name =>
                {
                    if (!lt.Has(name))
                        throw new InvalidOperationException("Viewport layer parameter: the drawing has no layer '" + name + "'.");
                    return lt[name];
                };
                Action<Autodesk.AutoCAD.DatabaseServices.Viewport, JsonArray, string> applyVpLayerState =
                    (vport, freezeArr, tag) =>
                {
                    var all = new List<ObjectId>();
                    foreach (ObjectId lid in lt) all.Add(lid);
                    vport.ThawLayersInViewport(all.GetEnumerator());
                    if (freezeArr != null && freezeArr.Count > 0)
                    {
                        var freezeIds = new List<ObjectId>();
                        foreach (JsonNode fn in freezeArr)
                        {
                            string ln = fn?.ToString();
                            if (string.IsNullOrEmpty(ln)) continue;
                            if (!lt.Has(ln))
                                throw new InvalidOperationException(
                                    tag + ".freeze_layers: the drawing has no layer '" + ln + "'.");
                            freezeIds.Add(lt[ln]);
                        }
                        if (freezeIds.Count > 0) vport.FreezeLayersInViewport(freezeIds.GetEnumerator());
                    }
                };

                string vpLayer = vpArg == null ? vpLayerName : GetString(vpArg, "layer", vpLayerName);
                if (vpLayer != vpLayerName) layerIdStrict(vpLayer);
                double modelW = maxx - minx, modelH = maxy - miny;
                double fitViewHeight = Math.Max(modelH, modelW * vpH / vpW) * 1.04;
                var vp = new Autodesk.AutoCAD.DatabaseServices.Viewport
                {
                    CenterPoint = new Point3d(vpX, vpY, 0),
                    Width = vpW,
                    Height = vpH,
                    ViewTarget = new Point3d((minx + maxx) / 2.0, (miny + maxy) / 2.0, 0),
                    ViewCenter = Point2d.Origin,
                    ViewHeight = fitViewHeight,
                    TwistAngle = 0,
                    Layer = vpLayer
                };
                paperSpace.AppendEntity(vp);
                tr.AddNewlyCreatedDBObject(vp, true);
                vp.On = true;
                if (vpScale > 0) vp.CustomScale = 1000.0 / vpScale;
                applyVpLayerState(vp, vpArg == null ? null : vpArg["freeze_layers"] as JsonArray, "viewport");
                vp.Locked = vpLocked;

                // Extra viewports: additional small windows in the same frame (e.g. the "key map" at the lower-left of a plan sheet).
                // Each has its own paper position/size/scale/model centre; the centre defaults to the main window centre.
                var extraVps = a["extra_viewports"] as JsonArray;
                if (extraVps != null)
                {
                    foreach (JsonNode en in extraVps)
                    {
                        var ev = en as JsonObject;
                        if (ev == null) continue;
                        double exW = GetDouble(ev, "width", 0), exH = GetDouble(ev, "height", 0);
                        double exScale = GetDouble(ev, "scale", 0);
                        if (exW <= 0 || exH <= 0 || exScale <= 0)
                            throw new InvalidOperationException(
                                "Each extra_viewports item must give width / height / scale (all > 0).");
                        double exX = GetDouble(ev, "x", 0), exY = GetDouble(ev, "y", 0);
                        double exCx = GetDouble(ev, "center_x", (minx + maxx) / 2.0);
                        double exCy = GetDouble(ev, "center_y", (miny + maxy) / 2.0);
                        string exLayer = GetString(ev, "layer", vpLayerName);
                        if (exLayer != vpLayerName) layerIdStrict(exLayer);
                        var evp = new Autodesk.AutoCAD.DatabaseServices.Viewport
                        {
                            CenterPoint = new Point3d(exX, exY, 0),
                            Width = exW,
                            Height = exH,
                            ViewTarget = new Point3d(exCx, exCy, 0),
                            ViewCenter = Point2d.Origin,
                            ViewHeight = exH * exScale / 1000.0,
                            TwistAngle = 0,
                            Layer = exLayer
                        };
                        paperSpace.AppendEntity(evp);
                        tr.AddNewlyCreatedDBObject(evp, true);
                        evp.On = true;
                        evp.CustomScale = 1000.0 / exScale;
                        // Thaw all layers first (washing out the 'frozen in new viewports' hiding), then freeze the named ones; rule 13: every name must exist
                        applyVpLayerState(evp, ev["freeze_layers"] as JsonArray, "extra_viewports");
                        evp.Locked = GetBool(ev, "locked", true);
                        extraVpReport.Add(new JsonObject
                        {
                            ["center_x"] = exX, ["center_y"] = exY,
                            ["width"] = exW, ["height"] = exH,
                            ["scale"] = exScale,
                            ["model_center_x"] = exCx, ["model_center_y"] = exCy
                        });
                    }
                }

                // Configure the layout itself as A3 landscape 1:1; plotting can target this layout directly.
                PlotSettingsValidator psv = PlotSettingsValidator.Current;
                psv.SetPlotConfigurationName(layout, "DWG To PDF.pc3", null);
                psv.RefreshLists(layout);
                string media = null, firstMatch = null;
                foreach (string m in psv.GetCanonicalMediaNameList(layout))
                {
                    if (m.IndexOf(paper, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (firstMatch == null) firstMatch = m;
                    if (m.IndexOf("full_bleed", StringComparison.OrdinalIgnoreCase) >= 0)
                    { media = m; break; }
                }
                media = media ?? firstMatch;
                if (media == null) throw new InvalidOperationException("DWG To PDF.pc3 has no paper size: " + paper);
                psv.SetCanonicalMediaName(layout, media);
                psv.SetPlotType(layout, AcDbPlotType.Layout);
                psv.SetUseStandardScale(layout, true);
                psv.SetStdScaleType(layout, StdScaleType.StdScale1To1);
                psv.SetPlotPaperUnits(layout, PlotPaperUnit.Millimeters);
                psv.SetPlotRotation(layout, PlotRotation.Degrees090);
                layout.PlotPlotStyles = true;
                psv.SetCurrentStyleSheet(layout, "monochrome.ctb");
                layout.PrintLineweights = true;

                tr.Commit();
            }
            try { lm.CurrentLayout = layoutName; } catch { }

            return new JsonObject
            {
                ["layout"] = layoutName,
                ["created"] = created,
                ["paper"] = paper + " " + paperW + "×" + paperH + " mm",
                ["frame_space"] = "Layout",
                ["frame_block"] = blockName,
                ["attributes_filled"] = attributesFilled,
                ["viewport"] = new JsonObject
                {
                    ["center_x"] = vpX, ["center_y"] = vpY,
                    ["width"] = vpW, ["height"] = vpH,
                    ["scale"] = vpScale > 0 ? JsonValue.Create(vpScale) : null,
                    ["locked"] = vpLocked
                },
                ["extra_viewports"] = extraVpReport,
                ["model_window"] = new JsonObject
                {
                    ["minx"] = minx, ["miny"] = miny, ["maxx"] = maxx, ["maxy"] = maxy,
                    ["source"] = windowSource,
                    ["boundary_handle"] = matchedBoundaryHandle
                }
            };
        }

        // ---------- Plot to PDF ----------
        // Ported from the battle-tested CadPlotPlugin (81 PDFs with zero errors on 2026-07-23),
        // bringing its three traps along: (1) side database set as WorkingDatabase (2) target layout must be current
        // (3) MediaMatchingPolicy on the side database must be MatchEnabled.
        static JsonNode PlotPdf(JsonObject a, Document doc)
        {
            string outPdf = GetString(a, "out", null);
            if (string.IsNullOrEmpty(outPdf))
                throw new InvalidOperationException("Missing parameter out (PDF output path).");
            if (!Path.IsPathRooted(outPdf))
                throw new InvalidOperationException("out must be an absolute path: " + outPdf);
            if (File.Exists(outPdf) && !GetBool(a, "overwrite", false))
                throw new InvalidOperationException(
                    "PDF already exists, refusing to overwrite: " + outPdf + " (pass overwrite:true to overwrite)");

            string outDir = Path.GetDirectoryName(outPdf);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            string extDwg = GetString(a, "dwg", null);
            string layoutName = GetString(a, "layout", "Model");
            string paper = GetString(a, "paper", "A3");
            string ctb = GetString(a, "ctb", "monochrome.ctb");
            bool lineweights = GetBool(a, "lineweights", true);
            bool fitLayout = GetBool(a, "fit", false);
            string device = GetString(a, "device", "DWG To PDF.pc3");   // any installed plotter, e.g. "PublishToWeb PNG.pc3" for a PNG screenshot
            long minBytes = (long)GetDouble(a, "min_bytes", 1024);

            Database prevWorking = HostApplicationServices.WorkingDatabase;
            Database side = null;
            try
            {
                Database db;
                if (!string.IsNullOrEmpty(extDwg))
                {
                    if (!File.Exists(extDwg))
                        throw new InvalidOperationException("dwg to plot not found: " + extDwg);
                    side = new Database(false, true);
                    side.ReadDwgFile(extDwg, FileOpenMode.OpenForReadAndAllShare, true, null);
                    side.CloseInput(true);
                    // Trap (1): the side database must be the current working database for PlotEngine to plot its layouts
                    HostApplicationServices.WorkingDatabase = side;
                    db = side;
                }
                else db = doc.Database;

                // The model-space plot window is interpreted in the **current UCS**, not the world coordinate system.
                // With a shifted UCS in the drawing, a world-coordinate window plots somewhere else (symptom: wrong content or blank page, no error).
                // Reset the UCS before plotting so the window parameters equal world coordinates.
                try { doc.Editor.CurrentUserCoordinateSystem = Matrix3d.Identity; } catch { }

                ObjectId layoutId;
                Extents3d window;
                bool userWindow;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var lm = LayoutManager.Current;
                    layoutId = lm.GetLayoutId(layoutName);
                    if (layoutId.IsNull)
                        throw new InvalidOperationException("Layout '" + layoutName + "' not found.");
                    // Trap (2): PlotInfoValidator requires the target layout to be current, otherwise eLayoutNotCurrent
                    try
                    {
                        if (!string.Equals(lm.CurrentLayout, layoutName, StringComparison.Ordinal))
                            lm.CurrentLayout = layoutName;
                    }
                    catch { }

                    var win = a["window"] as JsonObject;
                    userWindow = win != null;
                    if (win != null)
                    {
                        window = new Extents3d(
                            new Point3d(GetDouble(win, "minx", 0), GetDouble(win, "miny", 0), 0),
                            new Point3d(GetDouble(win, "maxx", 0), GetDouble(win, "maxy", 0), 0));
                    }
                    else
                    {
                        // Default to the drawing extents; for an empty drawing or uninitialised extents Extmin>Extmax, so fail clearly
                        db.UpdateExt(true);
                        if (db.Extmin.X > db.Extmax.X)
                            throw new InvalidOperationException("Drawing extents are empty, cannot determine the plot window automatically; pass the window parameter.");
                        window = new Extents3d(db.Extmin, db.Extmax);
                    }
                    tr.Commit();
                }

                Extents3d plotWindow = window;
                if (side == null && string.Equals(layoutName, "Model",
                    StringComparison.OrdinalIgnoreCase))
                {
                    // SetPlotWindowArea takes the current view DCS, not the drawing WCS.
                    // A model-space frame far from the origin passed as WCS produces a PDF fine, but blank.
                    using (ViewTableRecord view = doc.Editor.GetCurrentView())
                    {
                        Matrix3d wcsToDcs = Matrix3d.PlaneToWorld(view.ViewDirection);
                        wcsToDcs = Matrix3d.Displacement(
                            view.Target - Point3d.Origin) * wcsToDcs;
                        wcsToDcs = Matrix3d.Rotation(
                            -view.ViewTwist, view.ViewDirection, view.Target) * wcsToDcs;
                        wcsToDcs = wcsToDcs.Inverse();
                        plotWindow = TransformWindowExtents(window, wcsToDcs);
                    }
                }

                PlotWindow(db, layoutId, plotWindow, outPdf, paper, ctb, lineweights, fitLayout, userWindow, device);

                long size = File.Exists(outPdf) ? new FileInfo(outPdf).Length : 0;
                if (size < minBytes)
                    throw new InvalidOperationException("Plot output missing or too small (" + size + " bytes): " + outPdf);

                return new JsonObject
                {
                    ["pdf"] = outPdf,
                    ["bytes"] = size,
                    ["source"] = string.IsNullOrEmpty(extDwg) ? SafeFile(doc.Database) : extDwg,
                    ["layout"] = layoutName,
                    ["paper"] = paper,
                    ["ctb"] = string.IsNullOrEmpty(ctb) ? "(colour)" : ctb,
                    ["window"] = new JsonObject
                    {
                        ["minx"] = Math.Round(window.MinPoint.X, 4),
                        ["miny"] = Math.Round(window.MinPoint.Y, 4),
                        ["maxx"] = Math.Round(window.MaxPoint.X, 4),
                        ["maxy"] = Math.Round(window.MaxPoint.Y, 4)
                    },
                    ["plot_window_dcs"] = new JsonObject
                    {
                        ["minx"] = Math.Round(plotWindow.MinPoint.X, 4),
                        ["miny"] = Math.Round(plotWindow.MinPoint.Y, 4),
                        ["maxx"] = Math.Round(plotWindow.MaxPoint.X, 4),
                        ["maxy"] = Math.Round(plotWindow.MaxPoint.Y, 4)
                    }
                };
            }
            finally
            {
                HostApplicationServices.WorkingDatabase = prevWorking;
                if (side != null) side.Dispose();
            }
        }

        static Extents3d TransformWindowExtents(Extents3d source, Matrix3d transform)
        {
            var points = new[]
            {
                new Point3d(source.MinPoint.X, source.MinPoint.Y, 0),
                new Point3d(source.MaxPoint.X, source.MinPoint.Y, 0),
                new Point3d(source.MaxPoint.X, source.MaxPoint.Y, 0),
                new Point3d(source.MinPoint.X, source.MaxPoint.Y, 0)
            };
            var result = new Extents3d(
                points[0].TransformBy(transform), points[0].TransformBy(transform));
            for (int i = 1; i < points.Length; i++)
                result.AddPoint(points[i].TransformBy(transform));
            return result;
        }

        // Window plot core (ported from CadPlotPlugin.PlotWindow)
        static void PlotWindow(Database db, ObjectId layoutId, Extents3d window,
                               string outPdf, string paper, string ctb, bool printLineweights,
                               bool fitLayout = false, bool userWindow = false, string device = "DWG To PDF.pc3")
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);

                var ps = new PlotSettings(layout.ModelType);
                ps.CopyFrom(layout);
                PlotSettingsValidator psv = PlotSettingsValidator.Current;
                psv.SetPlotConfigurationName(ps, device, null);
                psv.RefreshLists(ps);
                // Raster drivers (PublishToWeb PNG/JPG) measure paper in pixels; mm is eInvalidInput for them.
                bool raster = device.IndexOf("PNG", StringComparison.OrdinalIgnoreCase) >= 0
                           || device.IndexOf("JPG", StringComparison.OrdinalIgnoreCase) >= 0;
                PlotPaperUnit units = raster ? PlotPaperUnit.Pixels : PlotPaperUnit.Millimeters;

                // Prefer the full_bleed paper (no margins, so the frame is not clipped)
                string media = null, firstMatch = null;
                foreach (string m in psv.GetCanonicalMediaNameList(ps))
                {
                    if (m.IndexOf(paper, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (firstMatch == null) firstMatch = m;
                    if (m.IndexOf("full_bleed", StringComparison.OrdinalIgnoreCase) >= 0) { media = m; break; }
                }
                media = media ?? firstMatch;
                if (media == null)
                    throw new InvalidOperationException(device + " has no media matching '" + paper + "'.");
                psv.SetCanonicalMediaName(ps, media);

                if (layout.ModelType)
                {
                    // Measured in AutoCAD 2025 model space: the window must be written first, then switch to Window.
                    // Calling SetPlotType first throws eInvalidInput in some model layout states.
                    psv.SetPlotWindowArea(ps, new Extents2d(
                        window.MinPoint.X, window.MinPoint.Y, window.MaxPoint.X, window.MaxPoint.Y));
                    psv.SetPlotType(ps, AcDbPlotType.Window);
                    psv.SetUseStandardScale(ps, true);
                    psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);
                    psv.SetPlotCentered(ps, true);
                    psv.SetPlotPaperUnits(ps, units);

                    // Rotate a landscape drawing 90 degrees to fit portrait paper
                    double w = window.MaxPoint.X - window.MinPoint.X;
                    double h = window.MaxPoint.Y - window.MinPoint.Y;
                    psv.SetPlotRotation(ps, (w >= h && !raster) ? PlotRotation.Degrees090 : PlotRotation.Degrees000);
                }
                else if (userWindow && !fitLayout)
                {
                    // Paper space + explicit window: window coordinates are the layout paper coordinates (mm).
                    // Order is critical: SetPlotWindowArea first, then SetPlotType(Window); the reverse throws eInvalidInput
                    // in some layout states (same approach as CadPlotPlugin.PlotWindow, verified;
                    // the old comment "Window on a layout always throws eInvalidInput" came from switching the type before setting the window).
                    // Use: cut single A3 sheets out of a merged layout with several frames in a row (e.g. PlanSheets-All).
                    psv.SetPlotWindowArea(ps, new Extents2d(
                        window.MinPoint.X, window.MinPoint.Y,
                        window.MaxPoint.X, window.MaxPoint.Y));
                    psv.SetPlotType(ps, AcDbPlotType.Window);
                    psv.SetUseStandardScale(ps, true);
                    psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);
                    psv.SetPlotCentered(ps, true);
                    psv.SetPlotPaperUnits(ps, units);
                    double w = window.MaxPoint.X - window.MinPoint.X;
                    double h = window.MaxPoint.Y - window.MinPoint.Y;
                    psv.SetPlotRotation(ps, (w >= h && !raster) ? PlotRotation.Degrees090 : PlotRotation.Degrees000);
                }
                else
                {
                    // Paper-space layouts plot as Layout by default (without an explicit window, window is only the extents fallback
                    // and cannot be used as the plot window).
                    psv.SetPlotType(ps, fitLayout ? AcDbPlotType.Extents : AcDbPlotType.Layout);
                    psv.SetUseStandardScale(ps, true);
                    psv.SetStdScaleType(ps, fitLayout ? StdScaleType.ScaleToFit
                                                      : StdScaleType.StdScale1To1);
                    psv.SetPlotPaperUnits(ps, units);
                    if (fitLayout)
                    {
                        // Long layouts (several frames in a row) rotated upright onto the paper: width/height laid along the paper's long side.
                        psv.SetPlotCentered(ps, true);
                        psv.SetPlotRotation(ps, PlotRotation.Degrees000);
                    }
                    else psv.SetPlotRotation(ps, PlotRotation.Degrees090);
                }

                if (!string.IsNullOrEmpty(ctb))
                {
                    ps.PlotPlotStyles = true;
                    psv.SetCurrentStyleSheet(ps, ctb);
                }
                else ps.PlotPlotStyles = false;

                ps.PrintLineweights = printLineweights;   // cures faint hairline text
                ps.ScaleLineweights = false;

                var pi = new PlotInfo { Layout = layoutId, OverrideSettings = ps };
                // Trap (3): media matching must still be enabled on the side database, otherwise eNoMatchingMedia
                var piv = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled };
                piv.Validate(pi);

                using (PlotEngine pe = PlotFactory.CreatePublishEngine())
                {
                    pe.BeginPlot(null, null);
                    pe.BeginDocument(pi, outPdf, null, 1, true, outPdf);
                    using (var ppi = new PlotPageInfo())
                    {
                        pe.BeginPage(ppi, pi, true, null);
                        pe.BeginGenerateGraphics(null);
                        pe.EndGenerateGraphics(null);
                        pe.EndPage(null);
                    }
                    pe.EndDocument(null);
                    pe.EndPlot(null);
                }
                tr.Commit();
            }
        }

        // Reflection dump: inspect an object's real API inside the acc process (AeccDbMgd is a mixed-mode assembly, cannot be reflected out of process)
        static JsonNode Snoop(JsonObject a, Document doc)
        {
            string typeFilter = GetString(a, "type", null);
            string nameFilter = GetString(a, "name", null);
            string handle = GetString(a, "handle", null);
            int max = (int)GetDouble(a, "max", 80);

            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DBObject target = null;
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    DBObject o = tr.GetObject(id, OpenMode.ForRead);
                    string tn = o.GetType().Name;
                    if (!string.IsNullOrEmpty(handle))
                    {
                        if (o.Handle.ToString().Equals(handle, StringComparison.OrdinalIgnoreCase))
                        { target = o; break; }
                        continue;
                    }
                    if (!string.IsNullOrEmpty(typeFilter) &&
                        tn.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!string.IsNullOrEmpty(nameFilter))
                    {
                        string nm = TryGetName(o);
                        if (nm == null || nm != nameFilter) continue;
                    }
                    target = o; break;
                }
                if (target == null)
                    throw new InvalidOperationException("No matching object found (type/name/handle too strict?)");

                Type t = target.GetType();
                var props = new JsonArray();
                int n = 0;
                foreach (var p in t.GetProperties(
                             System.Reflection.BindingFlags.Public |
                             System.Reflection.BindingFlags.Instance))
                {
                    if (n++ >= max) break;
                    var item = new JsonObject
                    {
                        ["name"] = p.Name,
                        ["type"] = p.PropertyType.Name
                    };
                    if (p.CanRead && p.GetIndexParameters().Length == 0)
                    {
                        try
                        {
                            object v = p.GetValue(target, null);
                            item["value"] = v == null ? "(null)" : Truncate(v.ToString(), 200);
                        }
                        catch (System.Exception ex)
                        {
                            item["value"] = "(read failed: " + ex.GetType().Name + ")";
                        }
                    }
                    else item["value"] = "(not readable)";
                    props.Add(item);
                }
                tr.Commit();

                return new JsonObject
                {
                    ["object_type"] = t.FullName,
                    ["base_type"] = t.BaseType != null ? t.BaseType.FullName : "",
                    ["handle"] = target.Handle.ToString(),
                    ["property_count"] = props.Count,
                    ["properties"] = props
                };
            }
        }

        // Read Name by reflection. Note: Civil style classes re-declare Name with new in derived classes,
        // so GetProperty("Name") throws AmbiguousMatchException (seen as "name unavailable, degraded to class name");
        // hence walk GetProperties() and take the first readable Name.
        static string TryGetName(object o)
        {
            if (o == null) return null;
            // Civil style classes re-declare Name with new in derived classes, and the derived one **has no get**:
            // GetProperty("Name") hits the derived one, CanRead says true, but GetValue reports
            // "Property Get method was not found." So walk up the base classes to the first one with a real getter.
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                      | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                try
                {
                    var p = t.GetProperty("Name", flags);
                    if (p == null) continue;
                    var getter = p.GetGetMethod(true);
                    if (getter == null) continue;
                    object v = getter.Invoke(o, null);
                    if (v != null) return v.ToString();
                }
                catch { }
            }
            return null;
        }

        static string Truncate(string s, int len)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= len ? s : s.Substring(0, len) + "…";
        }

        // ===================== Shared helpers =====================

        static readonly string[] HeadersXY =
            { "No.", "Station", "X (Northing)", "Y (Easting)" };
        static readonly string[] HeadersXYZ =
            { "No.", "Station", "X (Northing)", "Y (Easting)", "Ground elevation" };

        static IEnumerable<ObjectId> ModelSpace(Database db, Transaction tr)
        {
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms) yield return id;
        }

        // Start, every interval, and end are always taken
        static IEnumerable<double> Stations(double start, double end, double interval)
        {
            if (interval <= 0) interval = 50.0;
            double s = start;
            while (s < end - 1e-6) { yield return s; s += interval; }
            yield return end;
        }

        static HashSet<string> WantedNames(JsonObject a)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            string one = GetString(a, "name", null);
            if (!string.IsNullOrEmpty(one)) set.Add(one);
            var arr = a["names"] as JsonArray;
            if (arr != null)
                foreach (var n in arr) if (n != null) set.Add(n.GetValue<string>());
            return set.Count == 0 ? null : set;   // null = all
        }

        static string ResolveOutDir(JsonObject a, Document doc)
        {
            string dir = GetString(a, "outdir", null);
            if (string.IsNullOrEmpty(dir))
            {
                try { dir = Path.GetDirectoryName(doc.Database.Filename); } catch { }
            }
            if (string.IsNullOrEmpty(dir))
                dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        static string Station(double s)
        {
            int km = (int)Math.Floor(s / 1000.0);
            double rem = s - km * 1000.0;
            return "K" + km.ToString(CultureInfo.InvariantCulture) + "+" +
                   rem.ToString("000.000", CultureInfo.InvariantCulture);
        }

        static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        static string SafeLayer(DBObject o)
        {
            var e = o as Entity;
            try { return e != null ? e.Layer : ""; } catch { return ""; }
        }

        static string SafeFile(Database db)
        {
            try { return db.Filename; } catch { return "(unknown)"; }
        }

        static double Round(double v, int d) { return Math.Round(v, d); }
        static double Round2(double v) { return Math.Round(v, 4); }

        static double GetDouble(JsonObject a, string key, double dflt)
        {
            var n = a[key];
            if (n == null) return dflt;
            try { return n.GetValue<double>(); }
            catch
            {
                double v;
                if (double.TryParse(n.ToString(), NumberStyles.Any,
                        CultureInfo.InvariantCulture, out v)) return v;
                return dflt;
            }
        }

        static bool GetBool(JsonObject a, string key, bool dflt)
        {
            var n = a[key];
            if (n == null) return dflt;
            try { return n.GetValue<bool>(); }
            catch
            {
                bool v;
                return bool.TryParse(n.ToString(), out v) ? v : dflt;
            }
        }

        static string GetString(JsonObject a, string key, string dflt)
        {
            var n = a[key];
            if (n == null) return dflt;
            try { return n.GetValue<string>(); } catch { return n.ToString(); }
        }

        static JsonNode RunTestTwoTierChannel(JsonObject a, Document doc)
        {
            double cx = GetDouble(a, "center_x", 0.0);
            double cy = GetDouble(a, "center_y", 0.0);
            double bottomWidth = GetDouble(a, "bottom_width", 4.0);
            double h1 = GetDouble(a, "h1", 2.5);
            double m1 = GetDouble(a, "m1", 1.5);
            double benchWidth = GetDouble(a, "bench_width", 1.5);
            double benchSlope = GetDouble(a, "bench_slope", 0.0);
            double h2 = GetDouble(a, "h2", 3.0);
            double m2 = GetDouble(a, "m2", 1.75);
            double thickness = GetDouble(a, "lining_thickness", 0.15);
            string layerName = GetString(a, "layer", "C3DF-CHANNEL-2TIER");

            double halfW = bottomWidth / 2.0;

            Point2d pTopLeft2 = new Point2d(cx - (halfW + m1 * h1 + benchWidth + m2 * h2), cy + h1 + benchWidth * benchSlope + h2);
            Point2d pBenchLeftOut = new Point2d(cx - (halfW + m1 * h1 + benchWidth), cy + h1 + benchWidth * benchSlope);
            Point2d pBenchLeftIn = new Point2d(cx - (halfW + m1 * h1), cy + h1);
            Point2d pToeLeft = new Point2d(cx - halfW, cy);
            Point2d pCenterBottom = new Point2d(cx, cy);
            Point2d pToeRight = new Point2d(cx + halfW, cy);
            Point2d pBenchRightIn = new Point2d(cx + halfW + m1 * h1, cy + h1);
            Point2d pBenchRightOut = new Point2d(cx + halfW + m1 * h1 + benchWidth, cy + h1 + benchWidth * benchSlope);
            Point2d pTopRight2 = new Point2d(cx + halfW + m1 * h1 + benchWidth + m2 * h2, cy + h1 + benchWidth * benchSlope + h2);

            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                ObjectId layerId = GridEnsureLayer(tr, db, layerName, 1);

                using (Polyline pl = new Polyline())
                {
                    pl.Layer = layerName;
                    pl.AddVertexAt(0, pTopLeft2, 0, 0, 0);
                    pl.AddVertexAt(1, pBenchLeftOut, 0, 0, 0);
                    pl.AddVertexAt(2, pBenchLeftIn, 0, 0, 0);
                    pl.AddVertexAt(3, pToeLeft, 0, 0, 0);
                    pl.AddVertexAt(4, pToeRight, 0, 0, 0);
                    pl.AddVertexAt(5, pBenchRightIn, 0, 0, 0);
                    pl.AddVertexAt(6, pBenchRightOut, 0, 0, 0);
                    pl.AddVertexAt(7, pTopRight2, 0, 0, 0);

                    btr.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                }

                if (thickness > 0)
                {
                    ObjectId liningLayerId = GridEnsureLayer(tr, db, layerName + "-LINING", 3);
                    using (Polyline plLining = new Polyline())
                    {
                        plLining.Layer = layerName + "-LINING";
                        plLining.AddVertexAt(0, new Point2d(pTopLeft2.X, pTopLeft2.Y - thickness), 0, 0, 0);
                        plLining.AddVertexAt(1, new Point2d(pBenchLeftOut.X, pBenchLeftOut.Y - thickness), 0, 0, 0);
                        plLining.AddVertexAt(2, new Point2d(pBenchLeftIn.X, pBenchLeftIn.Y - thickness), 0, 0, 0);
                        plLining.AddVertexAt(3, new Point2d(pToeLeft.X, pToeLeft.Y - thickness), 0, 0, 0);
                        plLining.AddVertexAt(4, new Point2d(pToeRight.X, pToeRight.Y - thickness), 0, 0, 0);
                        plLining.AddVertexAt(5, new Point2d(pBenchRightIn.X, pBenchRightIn.Y - thickness), 0, 0, 0);
                        plLining.AddVertexAt(6, new Point2d(pBenchRightOut.X, pBenchRightOut.Y - thickness), 0, 0, 0);
                        plLining.AddVertexAt(7, new Point2d(pTopRight2.X, pTopRight2.Y - thickness), 0, 0, 0);

                        btr.AppendEntity(plLining);
                        tr.AddNewlyCreatedDBObject(plLining, true);
                    }
                }

                ObjectId textLayerId = GridEnsureLayer(tr, db, layerName + "-TEXT", 2);
                using (MText txt = new MText())
                {
                    txt.Layer = layerName + "-TEXT";
                    txt.Location = new Point3d(cx, cy + 0.5, 0);
                    txt.TextHeight = 0.5;
                    txt.Contents = string.Format("[Two-tier channel]\\PMain channel bottom width: {0}m | Tier-1 slope: 1:{1} (H={2}m) \\PBerm width: {3}m | Tier-2 slope: 1:{4} (H={5}m) \\PLining thickness: {6}m",
                        bottomWidth, m1, h1, benchWidth, m2, h2, thickness);

                    btr.AppendEntity(txt);
                    tr.AddNewlyCreatedDBObject(txt, true);
                }

                tr.Commit();
            }

            double totalTopWidth = (pTopRight2.X - pTopLeft2.X);
            double totalDepth = h1 + benchWidth * benchSlope + h2;

            var res = new JsonObject();
            res["status"] = "success";
            res["channel_type"] = "Two-tier slope channel (with main channel and berm)";
            res["total_top_width"] = Round(totalTopWidth, 3);
            res["total_depth"] = Round(totalDepth, 3);
            res["bottom_width"] = bottomWidth;
            res["h1"] = h1;
            res["m1"] = m1;
            res["bench_width"] = benchWidth;
            res["h2"] = h2;
            res["m2"] = m2;
            res["lining_thickness"] = thickness;
            res["layer"] = layerName;
            return res;
        }

        static JsonNode RunCreate3DBox(JsonObject a, Document doc)
        {
            double dx = GetDouble(a, "x", 10.0);
            double dy = GetDouble(a, "y", 10.0);
            double dz = GetDouble(a, "z", 5.0);
            double cx = GetDouble(a, "cx", 0.0);
            double cy = GetDouble(a, "cy", 0.0);
            double cz = GetDouble(a, "cz", 0.0);
            string layerName = GetString(a, "layer", "C3DF-BOX-3D");

            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                ObjectId layerId = GridEnsureLayer(tr, db, layerName, 5); // Blue

                using (Solid3d solid = new Solid3d())
                {
                    solid.CreateBox(dx, dy, dz);
                    solid.TransformBy(Autodesk.AutoCAD.Geometry.Matrix3d.Displacement(
                        new Autodesk.AutoCAD.Geometry.Vector3d(cx, cy, cz + dz / 2.0)));
                    solid.Layer = layerName;

                    btr.AppendEntity(solid);
                    tr.AddNewlyCreatedDBObject(solid, true);
                }

                tr.Commit();
            }

            var res = new JsonObject();
            res["status"] = "success";
            res["type"] = "Solid3d Box";
            res["x"] = dx;
            res["y"] = dy;
            res["z"] = dz;
            res["center"] = new JsonArray { cx, cy, cz };
            res["layer"] = layerName;
            return res;
        }

        static JsonNode RunCreate3DSphere(JsonObject a, Document doc)
        {
            double r = GetDouble(a, "radius", 3.0);
            double cx = GetDouble(a, "cx", 0.0);
            double cy = GetDouble(a, "cy", 0.0);
            double cz = GetDouble(a, "cz", 8.0);
            string layerName = GetString(a, "layer", "C3DF-SPHERE-3D");

            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                ObjectId layerId = GridEnsureLayer(tr, db, layerName, 1); // Red

                using (Solid3d solid = new Solid3d())
                {
                    solid.CreateSphere(r);
                    solid.TransformBy(Autodesk.AutoCAD.Geometry.Matrix3d.Displacement(
                        new Autodesk.AutoCAD.Geometry.Vector3d(cx, cy, cz)));
                    solid.Layer = layerName;

                    btr.AppendEntity(solid);
                    tr.AddNewlyCreatedDBObject(solid, true);
                }

                tr.Commit();
            }

            var res = new JsonObject();
            res["status"] = "success";
            res["type"] = "Solid3d Sphere";
            res["radius"] = r;
            res["center"] = new JsonArray { cx, cy, cz };
            res["layer"] = layerName;
            return res;
        }
    }
}
