---
name: civil3dfactory-corridor
description: Linear infrastructure in Civil 3D from the command line - alignment to profiles, corridor, corridor surface, sample lines, material-based quantities (cumulative and incremental cut/fill per station, xlsx), section views and profile views. Use for channels, dikes, ditches, canals and roads whenever a centerline and a cross-section describe the object. Load civil3dfactory first.
---

# civil3dfactory-corridor

Repository: `{{C3DF_ROOT}}`. Entry: `civil3dfactory.ps1`. Everything below is a task JSON run with
`civil3dfactory.ps1 -Dwg <drawing> -Task <task.json>`; in-memory results are kept only by a final `save_dwg` with an `out` path.

## When to use

The object is "a line plus a cross-section": river channel dredging, canal, dike, road. You want the model, the
quantities per station, and the section / profile views. Not for area objects (islands, ponds, intersections) -
those ops exist in the engine (`-Help create_dredge_grading`, `bounded_volumes`) but are not covered here.

## Inputs

| Input | Where it comes from | Check with |
|---|---|---|
| terrain surface | the drawing (TIN) | `view outline` -> `surfaces[]` |
| centerline alignment | the drawing, or `create_alignment` from points / a polyline handle | `view outline` -> `alignments[]` |
| assembly with PKT subassemblies | the drawing (`create_assembly`, see the pkt skill) | `view outline` -> `assemblies[].subassemblies[].status == UpToDate` |
| design bottom elevation | the designer | `create_profiles` `design_elev` |
| quantity criteria | the drawing's `QuantityTakeoffCriterias` (`list_styles`) | slots are classified by name: EG / existing / ground vs Datum / design / FG |
| sample interval and swath | sheet scale: 50 m at 1:500, 20 m for detail | `create_sample_lines` |

Never guess object or style names: read them with `view outline` / `civil_env` / `list_styles` first.

## Node sequence

```
view outline                       what is there; PKT status must be UpToDate
create_profiles                    ground profile from the surface + flat design profile at design_elev
create_corridor                    baseline = alignment, region = whole alignment, assembly, surface target -> terrain
create_corridor_surface            corridor surface from the Top links (+ boundary)   -> <alignment>_Design
create_sample_lines                group <alignment>_SampleLines; terrain, corridor and corridor surface are sampled
compute_quantities                 criteria -> material list on the group (average end area)
corridor_stats / view issues       link codes, surface elevation range, nothing broken
create_section_views               one view per sample line, grid layout, code set applied
create_profile_view                ground + design profiles, band set
export_material_volumes            xlsx: Summary sheet + one sheet per alignment (station, cumulative and incremental cut / fill)
save_dwg out:<path>                persist a copy
```

Default names produced along the way: `<alignment>_EG`, `<alignment>_FG`, `<alignment>_Corridor` (baseline `Baseline`,
region `Region1`), `<alignment>_Design`, `<alignment>_SampleLines`, `<alignment>_ProfileView`. Every op accepts explicit names.

## Example task

`examples/corridor/02-build.json` then `examples/corridor/03-views-and-export.json` on the demo drawing (centerline `C1`,
assembly `C3DF-Channel`, criteria `@C3DF-CutFill`). Expected: 23 sample lines, cut about 54,800 m3, fill 0, 23 section views,
`tmp/C1-quantities.xlsx`.

Minimal task (bottom elevation 3.0, everything else default):

```json
{ "ops": [
  { "op": "create_profiles", "alignment": "C1", "surface": "EG", "design_elev": 3.0 },
  { "op": "create_corridor", "alignment": "C1", "assembly": "C3DF-Channel", "surface": "EG" },
  { "op": "create_corridor_surface", "alignment": "C1" },
  { "op": "create_sample_lines", "alignment": "C1", "surface": "EG", "interval": 50, "swath": 40 },
  { "op": "compute_quantities", "alignment": "C1", "surface": "EG", "criteria": "@C3DF-CutFill" },
  { "op": "create_section_views", "alignment": "C1", "style": "@C3DF-SimpleGrid", "code_set": "@C3DF-Dredge",
    "section_style": "@C3DF-GroundLine", "material_style": "@C3DF-CutFill", "x": 0, "y": -1500,
    "placement": "production", "template": "{{C3DF_ROOT}}/examples/sheet-A3.dwt", "layout": "A3", "group_plot_style": "@C3DF-PrintStyle-A3" },
  { "op": "create_profile_view", "alignment": "C1", "x": 0, "y": -600, "style": "@C3DF-ProfileView", "band_set": "@C3DF-ProfileBands" },
  { "op": "export_material_volumes", "out": "{{C3DF_ROOT}}/tmp/C1-quantities.xlsx" },
  { "op": "save_dwg", "out": "{{C3DF_ROOT}}/tmp/corridor.dwg", "overwrite": true }
] }
```

Targets: every surface slot of the assembly is mapped to `surface`; offset slots (e.g. `TopLine` of the `slopetop` subassembly) are mapped to
the alignments named `<alignment>_L*` and `<alignment>_R*` when they exist (same-side pick), so name the two design lines that way or create them with
`offset_alignment` (`alignment`, `distance`, `style?`: writes `<alignment>_L` and `<alignment>_R`).

Section view placement: `placement: "production"` is Civil 3D's native *Create Multiple Section Views* sheet path - the layout in `template` (a .dwt/.dwg with a viewport, `examples/sheet-A3.dwt` is built by `examples/build-sheet-template.json`) sizes the page and the `group_plot_style` arranges the views page by page in model space. `placement: "draft"` (default) uses `rows` / `cols` / spacing instead. Elevation range stays automatic per view; `elev_min` / `elev_max` pin it afterwards.

## Acceptance

- `create_corridor` reports `surface_targets_set > 0` and non-empty `link_codes`; `corridor_stats` shows the corridor surface with a real elevation range.
- `create_sample_lines` lists the corridor and the corridor surface under `sampled_sources` (terrain-only sampling gives sections without the design and zero quantities).
- `compute_quantities` `total_cut_m3` is non-zero and `mapped_slots` names the right surfaces. Zero volume with `ok: true` is a failure, not a success.
- The xlsx has one row per sample line with cumulative and incremental values; the last cumulative equals `total_cut_m3`.
- `view issues` returns `healthy: true` (or only the warnings you expect).

## Known pitfalls

- A subassembly whose `status` is not `UpToDate` (PKT missing, not embedded) builds an empty corridor shell: fix the assembly first (pkt skill, `check_sac_paths`).
- `create_corridor` needs a design (FG) profile: run `create_profiles` before it.
- Sample lines must sample the corridor surface, not only the terrain: pass `corridor` / `road_surface` when names are not the defaults.
- The QTO criteria surface slots are matched by name; if the criteria uses other words pass `road_surface_slot` explicitly (`-Help compute_quantities`).
- Section view spacing: use `row_spacing` >= the view height (stock styles are about 50 m tall) or views overlap.
- Nothing is written to the host drawing; the result lives in the `save_dwg` copy. accoreconsole cannot overwrite the drawing it opened.
