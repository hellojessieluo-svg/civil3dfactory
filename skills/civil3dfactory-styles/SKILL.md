---
name: civil3dfactory-styles
description: Read and build Civil 3D styles headlessly - section view and profile view styles (grid spacing, axes, title), section and shape styles (line display, cut/fill hatch colours), label styles, band sets and code sets, then apply them to existing views. Use when the look of section views, profile views, labels or material hatching must be created or changed. Load civil3dfactory first.
---

# civil3dfactory-styles

Repository: `{{C3DF_ROOT}}`. All ops run through `civil3dfactory.ps1 -Dwg <drawing> -Task <task.json>`; keep results with `save_dwg`.

## When to use

"Make the section views look like X": grid every 5 m, station title on top, thin ground line, cut hatched red, fill hatched blue,
labels moved or hidden, a different band set on profile views. Also to inspect what a style currently does before touching it.

## Inputs

| Input | Where | Check with |
|---|---|---|
| style collection and name | `list_styles` (category paths such as `SectionViewStyles`, `SectionStyles`, `ShapeStyles`, `CodeSetStyles`, `LabelStyles.SectionLabelStyles...`) | `list_styles` |
| display components of a style | `style_display` without `component` lists them with current colour / layer / visibility per view | `style_display` |
| label components | `dump_label_styles` | |
| code set mapping | `code_set_dump` | |

## Node sequence

```
list_styles                    find the collection path and existing names
create_style                   new empty style in any collection (cat, name)            -- or create:true on the ops below
section_view_style             grid (tick intervals), axes, title text / location / height, exaggeration, padding, display overrides
shape_style                    hatch pattern / angle / scale of a material shape style + display (colour) overrides
style_display                  colour / layer / linetype / lineweight / visibility of any component of any style (view = Plan | Section | Profile | Model)
set_label_style                label components: visibility, offsets, contents, anchor
code_set_edit                  which style / label style a link or point code uses
restyle_section_views          apply section view style, section style, material shape style to existing views (no rebuild)
restyle_profile_view_bands     swap band styles on existing profile views
view screenshot                see the result
save_dwg out:<path>
```

Grid spacing lives in the axis tick intervals: `grid.major_h` / `minor_h` set the bottom and top axes (offset direction),
`grid.major_v` / `minor_v` the left and right axes (elevation direction). Titles: `title.location` accepts `Top`, `Bottom`, `Left`, `Right`;
axis titles use `AxisTitleLocationType` names (`TopCenter` etc.), matched loosely.

## Example task

`examples/styles/01-section-view-style.json` builds `@C3DF-Section-5m` from scratch and recolours `@C3DF-Cut` / `@C3DF-Fill`;
`examples/styles/02-apply-to-views.json` switches existing section views to it and takes before / after PNGs.

Core of it:

```json
{ "op": "section_view_style", "name": "@C3DF-Section-5m", "create": true,
  "grid": { "major_h": 5, "minor_h": 1, "major_v": 5, "minor_v": 1 },
  "title": { "text": "<[Section View Station]>", "location": "Top", "text_height": 4 } }
{ "op": "shape_style", "name": "@C3DF-Cut", "hatch": { "type": "Pattern", "pattern": "ANSI31", "angle": 45 },
  "display": [ { "component": "AreaFill", "view": "Section", "set": { "color": 1, "visible": true } } ] }
{ "op": "style_display", "cat": "SectionStyles", "name": "@C3DF-GroundLine", "view": "Section", "component": "Segments",
  "set": { "linetype": "Continuous", "lineweight": 18 }, "dry_run": false }
{ "op": "restyle_section_views", "alignment": "C1", "style": "@C3DF-Section-5m", "material_style": "@C3DF-Cut" }
```

Component names are matched loosely (case and spaces ignored); run `style_display` without `component` to list the exact ones for a style type.

## Acceptance

- `section_view_style` echoes the style (`style.BottomAxis.major_interval` etc.) with the values you set and `changed` non-empty.
- `restyle_section_views` reports the number of views restyled equal to the number of views; `view screenshot` before / after differ where expected.
- `style_display` with `dry_run:false` reports the component's new colour / visibility; `dry_run:true` (the default) changes nothing.

## Known pitfalls

- `style_display` defaults to `dry_run: true` - pass `dry_run: false` to apply.
- Material hatching renders through each material section's own style slot: pass `material_style: "<shape style>"` (e.g. `@C3DF-Cut`) to `restyle_section_views` or `create_section_views`; editing the shape style alone changes nothing until the sections point at it (`dump_material_styles` shows what they use).
- A code set decides which link / point codes get a style at all (`code_set_dump`); links with no mapping draw nothing.
- Label text height and offsets are in plot units (mm) scaled by the drawing's annotation scale (`set_scale`).
- Styles are stored in the drawing: a new style exists only in the `save_dwg` copy.
