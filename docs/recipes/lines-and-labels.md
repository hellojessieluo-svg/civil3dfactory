# Lines and labels: collect design lines, alignment networks with fillets, coordinate labels

Distilled 2026-11-09 from three field-tested recipes.

## 1. Collect elevated design lines from many drawings into one CAD drawing

Task: merge every "design line with elevation" (corridor design lines, feature lines, polylines with elevation)
from a project's calculation drawings into one plain CAD DWG, layered `<source>-<original layer>` so every line
keeps its origin.

1. Recon (read-only): `list_alignments` / `list_profiles` for corridor design lines (flat design profiles copy
   straight); `entity_stats` / `list_layers` for feature lines and elevated polylines.
2. Corridor centre lines: `export_alignments_to_dwg` (centers_only) -> `set_polyline_elevation` per layer from
   the design profile -> `save_dwg` to a sibling file. Lossless when the design profile is flat.
3. Corridor edge lines: `export_corridor_feature_lines` with the daylight/toe codes and a layer template such as
   `<source>-{corridor}-{code}`; points keep their elevation, breaks split automatically. Check: toe elevation =
   design bottom; highest daylight point ~ the corridor surface maximum.
4. Feature lines: in an ASCII-path copy of the source, explode each feature line in place into a 3D polyline
   (layer and elevation kept), select elevated geometry (3D polylines, LW polylines with elevation != 0, lines and
   arcs with Z), move them to `<source>-<layer>` (layer 0 -> `<source>-design`), WBLOCK.
5. Merge: centre-line drawing as base, `-INSERT name=file` with scale 1 and rotation 0 preset, explode block by
   block, erase entities on layers outside the whitelist (WBLOCK drags extras), PURGE twice, SAVEAS.
6. Done means: per-layer counts close (sum of the extraction reports = merged drawing) and every layer's Z range
   matches the design. Report odd elevations (an auxiliary line at Z=30) by name instead of deleting them.

Pitfalls: moving feature lines together with their AEC site lets site merging swallow lines (77 -> 4) - explode to
plain geometry first. Multi-select EXPLODE only explodes the first object - explode one by one. `-INSERT *name=file`
refuses to explode - insert, then explode. Grading baselines are not selectable but sneak in with the site clone;
count layers without the source prefix after merging. Zero-elevation feature lines are plan helpers and drop out
naturally. accoreconsole: copy non-ASCII paths to ASCII, keep non-ASCII logic in UTF-8-BOM `.lsp`, end with `QUIT` `_Y`.

## 2. Turn a hand-drawn centre-line network into live alignments with fillets

Task: centre lines drawn as polylines, constant channel width, the same corner radius everywhere; the client wants
native behaviour (drag a centre line, offsets and corners follow) and the radius as a parameter.

Default method: solve the corner arcs analytically. The native connected-alignment solver refuses tight angles
(12 of 44 corners failed even with full parameter sweeps), while a fillet between two tangents and a radius is
closed-form geometry. Use `create_fillet_alignment` (two fixed lines + radius -> tangent arc as an alignment) or
`rebuild_fillet_network` for a whole network; self-check tangent error < 0.01 and the arc midpoint on the land side.

Order of work:
1. `create_alignment` (from polyline handle, erase_source) for every centre line.
2. `export_alignments_to_dwg` centers_only to compute the crossing topology and first corner hints.
3. Offsets: `offset_alignment` with `start_station` / `end_station` per segment; between corners the offsets give
   way, segment ends reach about 2 m past the corner point so the two parents really intersect.
4. Corners: fillets as above. If you use `create_connected_alignment` instead, the parents must be the two
   segmented offset alignments, station hints pulled about 8 m into the segment, `overlap_in/out` > 0.
   Run a probe pass on a copy first and trust only its measured tangent points (`start_end_measurements`),
   not polyline direction (it may run against the stationing).
5. One task: erase old offsets -> offsets per segment -> corners -> `save_dwg` (save to a side file and swap).

Done means: every corner reports `geometry_verified: true`; alignment count = centre lines + segments + corners;
in the GUI, dragging a centre-line grip moves its offsets and corners.

Pitfalls: a parent made of centre line + OffsetIn/Out gives the through-arc across the crossing (bulging into the
water), never the land corner. Offset stations are the start anchor plus their own arc length (longer on the outer
side), so hints near segment ends must move inward or the solver drifts into a false "No solution". Short stubs
between two neighbouring corners that lie inside the crossing channel are removed by design.

Sister case - edges drawn by hand, widths in steps, flared crossings: list corridor targets first (`corridor_targets`;
empty slots mean widths are hard-coded in the assembly). Sample the edges every 2 m against the centre line, call a
stretch "constant offset" when |measured - nominal| <= 0.3 m, merge pieces under 20 m into neighbours. Constant
stretches -> `offset_alignment` segments; varying stretches -> clip the edge polyline by the parent's station window
(split bulge arcs exactly) -> `create_alignment`. Always run a check pass (export -> DXF -> measured offsets,
coverage, end points) on intermediate DXF exported in the same round as the current drawing: a three-hour-old
centre-line export once shifted a whole offset window by 114.8 m.

## 3. Coordinate labels with General Note Labels

Coordinates belong in a label style with dynamic fields; `add_note_label` places them at points, the value follows
the point and dragging keeps it live - nothing is typed by hand.

- Find the style with `dump_label_styles` (search by field name such as `Northing`; localised style names may not
  match). Look at it headless: `add_note_label` with points carrying `dx/dy` (dragged state) -> `plot_pdf` with a small
  window -> render to PNG. Iterate `set_label_style` on `contents`, all in memory; `save_dwg` to a sibling and swap
  into the template at the end (`apply:true` throws eFilerError headless).
- Two-line X/Y labels: two components anchored at the same point overlap in the original state, and the dragged state
  stacks lines at about 2.3 x text height. The anchor location and dragged-state layout are not exposed in .NET.
  Use one text component with an MText stacked fraction instead:
  `\SX=<[Northing(Um|P4|RN|AP|GC|UN|Sn|OF)]>/Y=<[Easting(Um|P4|RN|AP|GC|UN|Sn|OF)]>;`
  X above, centred bar, Y below, line spacing ~1.5 x height, identical in original, right-dragged and left-dragged
  states, and the leader meets the bar. Do not add a height factor (`\H1.4286x;`): Civil does not shrink stacked
  text, so the factor enlarges it. Components cannot be deleted - hide the old one.
- Done means: rendered PNGs of the three states show equal text heights, Y directly under the bar, no overlap, and a
  re-read of the template shows the new contents.
- Batch placement along polyline vertices or feature-line points: feed their coordinates as `points`.
