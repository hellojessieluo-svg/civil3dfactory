# Terrain and earthwork: grid balance, estimated post-dredge terrain, drawing-to-survey overlays

Distilled 2026-11-09 from field-tested recipes.

## 1. Grid earthwork balance (where to cut, where to fill, how far to haul)

For any site that balances internally: islands, crossing plots, dredged water areas, terraced fields.
Rule: **the TIN gives the quantities** (bounded volumes), the grid only gives haul-distance resolution. A zone that
balances itself still has real internal haul distances - never count it as zero haul.

1. Prerequisites: a base surface (existing) and a comparison surface (design), or a ready volume surface; the
   boundary is a closed polyline.
2. `grid_earthwork_balance` (example task in `nodes/grid_earthwork_balance/test.json`):
   `volume_surface` (or base + comparison), boundary by `handles` / `layer`, `step` (2-3 m for islands and plots,
   20-30 m for a whole site), `solver_max_nodes` (default 900; above it the grid is coarsened automatically and the
   receipt reports `solver_step`), `volume_factor` (report factor such as 1.06, geometry listed separately), `bands`
   (haul classes aligned with the cost estimate's machinery classes). It draws cut cells, fill cells, arrows and
   notes on four layers (idempotent) and writes a balance table and a haul-class table (xlsx).
   Point `save_dwg.out` at a work folder to leave the source drawing untouched.
3. Done means, all of: `closure_*_pct` <= 2 % (grid integral vs TIN; above 10 % the node fails); `total_tin_cut/fill`
   equal the quantities of the same boundary in the official quantity table (otherwise surface or boundary is wrong);
   `internal_moved == min(cut, fill)` and the histogram sums to it; the remainder shows up only on one side, as
   `shortfall_borrow` or `surplus_export`.

Limits: haul distance is the straight line between cell centres (no detours around channels; build a cost surface
for that); construction sequence and cofferdams are not modelled; moves inside one cell are not counted, so agree the
lowest haul class with the estimate first. Reference run: 46,395 m2, step 3 m -> 5,146 cells, closure 0.42 % / 0.13 %,
solver coarsened to 6 m, 56 s; internal haul 20,381 m3, mean 38.6 m.

## 2. Estimated post-dredge terrain when nobody surveyed after construction

Task: a channel was dredged, there is no as-built survey, someone needs "the terrain now" (planting zones, landform
earthwork). Build it from the pre-construction terrain plus the construction design, and label it as an estimate.

With design surfaces in the drawing:
1. `paste_surfaces`: channel corridor surfaces + crossing surfaces -> one design surface (later pastes win).
2. `merge_post_dredge_surface`: design ∧ existing, lower wins -> post-dredge surface inside the channel
   (verify fill ~ 0).
3. `paste_surfaces` [existing, post-dredge] -> estimated surface for the whole area; make a separate copy of the
   existing surface for drawing (never add boundaries to the existing surface itself - pasted surfaces follow it).
4. `dump_geometry` of the design outline -> shapely buffer (for example 100 m) -> `add_surface_points` as outer
   boundary on both surfaces.

With survey points and design lines only:
1. `create_surface_from_points` from the survey blocks (`block_layer`, `block_names`, `drop_zero`, `z_min`,
   `max_triangle_length` ~60).
2. Glue: join the design centre line into one chain, every 5 m cut a section to both bottom edges; bottom = design
   bottom elevation, slope 1:m outward from the bottom edge up to bottom + a few metres -> point cloud JSON ->
   `create_surface_from_points` (`points_file`, `max_triangle_length` ~40).
3. Then steps 2-4 above, plus one more: clip the estimate to the survey coverage (outline of the existing surface,
   largest ring outer, inner rings holes; shapely `buffer(0)` -> intersect -> `simplify(0.5)` or the boundary is
   illegal). Without it, design slopes beyond the surveyed area survive as fake terrain (2.3 ha in one case).

Output for other offices: elevation points with Z, fine contours and existing features - not a sparse grid of labels
(a 25 m grid was rejected as "sparser than the TIN"). Import contour styles from the drafting template,
`set_surface_style`, `set_contour_interval` (0.5 / 2.5 m), `place_elevation_points` in two layers (existing points;
estimated points only where the estimate is > 5 cm below existing), `export_to_autocad` (switch to model space with
`TILEMODE 1` first or contours are not exploded), keep only what touches the area, add a note box ("estimate, not a
survey") with `modify_dwg` hosted on another drawing, plot full/detail/note previews. When asked for one terrain, let
estimated points replace existing ones: square buffers around estimated points, erase existing points inside the
union (and their paired separate text), leave the holes (existing points there already are the post-dredge value).

Done means: estimated cut vs the construction drawings' quantities within ±10 %; zero grid cells with an estimate but
no existing terrain; preview numbers are digits, not question marks.

Pitfalls: check which exported layer is existing and which is estimate by elevation, not by name; a drawing parked on
a layout tab sends headless output to paper space and skips contour explosion; missing SHX fonts print `?` - move grid
text to the standard style; joining paths in task JSON with a raw string plus `\\` gives `eNoFileName` - use
`os.path.join`. Original survey points cannot be read back from a surface headless; find the source drawing through
the data shortcut path shown in Prospector, then prove it by sampling candidate points against the surface
(`sample_polyline_elevations` step 0: more than 90 % within 2 mm). Survey blocks that carry their own height
attribute need no extra labels - move them with `import_entities` (filter by `types`; proxy objects on the layer make
the whole batch fail).

## 3. Overlay drawing geometry with surveyed points (read-only analysis)

Task: decide which monitoring points lie inside a screen, near a jetty or along a haul route, from construction
drawings and point coordinates in a report. No drawing is changed.

- Copy the drawings to a work folder first (accoreconsole leaves lock files in incoming folders).
- `list_layers` for layer names and counts (result in `ops[0].data.layers`), then `dump_geometry` with those layers
  and `out` to JSON; identical model spaces across sheets need one export only.
- Take the coordinate system from the design report; check registration against a second source (for example a KML
  of the same features) with **point-to-segment** distance - vertex-to-vertex distance is inflated by vertex spacing.
  A median residual of a few metres is good.
- Drop far-away entities (origin junk, details, index symbols) by buffering the point extent (for example 3 km).
- Closed polylines -> polygons need `make_valid` before union (TopologyException otherwise).
- Done means: registration residual known and small, per-area check plots reviewed, every number in the text comes
  straight from the script output.
