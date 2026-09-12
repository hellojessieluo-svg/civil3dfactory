# Examples

Two demo drawings and one task file per skill. Every task runs as is from any clone location:
`{{C3DF_ROOT}}` in a task file expands to the repository folder, `{{DWG_DIR}}` to the drawing's folder.
Outputs go to `tmp/` (git-ignored). The demo drawings are never modified: `save_dwg` always writes a copy.

```powershell
civil3dfactory.ps1 -Dwg examples\channel-demo.dwg -Task examples\corridor\01-recon.json
```

## The drawings

### channel-demo.dwg (linear work: pkt / styles / corridor)

Built by `build-channel-demo.json` from a metric drawing seeded with the styles below (a real house style set, renamed `@C3DF-*`; the seeding task lives outside git). Metres, annotation scale 1:500 (the usual sheet scale for channel sections), DWG 2018.

| Object | Name | Notes |
|---|---|---|
| Terrain surface | `EG` | 1200 x 800 m grid, elevations 3.8 - 6.1 m, stock contour style |
| Centerlines | `C1` (1067 m, 3 PIs with curves), `C2` (643 m) | layer `C3DF-CL`, style `@C3DF-Centerline`, station labels |
| Design lines | `C1_L`, `C1_R` | offset alignments 16 m left / right of C1 (`offset_alignment`, style `@C3DF-TopLine`): the top edges for the two-design-lines pattern |
| Assembly | `C3DF-Channel` | subassemblies `C3DF-Channel_LEFT/RIGHT` from `pkt/ChannelSlope_*.pkt` (pkt-forge `channel`, no bench), PKT embedded; params `BottomHalfWidth` 10, `Slope1H` 3 |
| QTO criteria | `@C3DF-CutFill` | stock Earthworks criteria renamed: cut = EG above Datum, fill = Datum above EG; surface slots `EG` / `Datum` |
| Section view styles | `@C3DF-SimpleGrid`, `@C3DF-NoGrid` | 10 m / 1 m grid, station + scale title below, `Elevation (m)` / `Offset (m)` axis captions |
| Section style | `@C3DF-GroundLine` | ground section line |
| Shape style | `@C3DF-CutFill` | material section hatch |
| Code set style | `@C3DF-Dredge` | dredging codes: points `origin`, `toe-left/right`, `daylight-left/right`, `controlpoint-left/right`, `mp-left/right`, `WT`; marker styles `@C3DF-ControlPoint-Left/Right`, `@C3DF-NoDisplay`; link labels `@C3DF-Slope`, `@C3DF-Length-*` |
| Profile view style / band set | `@C3DF-ProfileView`, `@C3DF-ProfileBands` | bands `C3DF-Station`, `C3DF-GroundElevation`, `C3DF-DesignElevation`, `C3DF-CutFillHeight` |
| Profile styles | `@C3DF-GroundProfile`, `@C3DF-DesignProfile` | |
| Alignment / sample line styles | `@C3DF-Centerline`, `@C3DF-TopLine`, `@C3DF-SampleLine` | |
| Group plot style | `@C3DF-PrintStyle-A3` | used by `create_section_views` `placement: production` with `sheet-A3.dwt` |
| Sheet template | `sheet-A3.dwt` | layout `A3`: title block `C3DF-TITLEBLOCK-A3` + 380 x 210 mm viewport; built by `build-sheet-template.json` from title-block-demo.dwg |

No profiles, corridors, sample lines or views exist in the demo: the examples build them.

### title-block-demo.dwg (plotting work: plot)

Plain AutoCAD drawing (acadiso.dwt base, millimetres, no Civil objects), built by `build-title-block-demo.json`.

| Object | Name | Notes |
|---|---|---|
| Title block definition | `C3DF-TITLEBLOCK-A3` | A3 landscape 420 x 297, layers `C3DF-FRAME` / `C3DF-TEXT` |
| Attributes | `PROJECT`, `SHEET_TITLE`, `DESIGNED`, `CHECKED`, `SCALE`, `DATE`, `SHEET_NO`, `NOTES` (multi-line) | |
| Sheets | 3 references in model space at x = 0 / 450 / 900, y = 0 | one PDF per reference when plotted |
| Content block | `C3DF-DEMO-CONTENT` | placeholder line work inside each sheet |

Conventions the tools rely on: a title block is a block whose name contains `TITLE` (case-insensitive, override with `--block`); the plotter uses each reference's bounding box as the plot window; `NOTES` is an MText attribute, line breaks are written as `\P`.

## Task files and what to expect

| File | Drawing | Result |
|---|---|---|
| `corridor/01-recon.json` | channel-demo | `view outline` + `view issues`; 1 surface, 2 alignments, 1 assembly (status UpToDate, embedded), warnings only about missing profiles |
| `corridor/02-build.json` | channel-demo | profiles `C1_EG` / `C1_FG`, corridor `C1_Corridor`, corridor surface `C1_Design`, group `C1_SampleLines` (23 lines), material list from `@C3DF-CutFill`: total cut about 54,800 m3, fill 0; writes `tmp/corridor.dwg` |
| `corridor/03-views-and-export.json` | tmp/corridor.dwg | 23 section views laid out sheet by sheet (`placement: production`, `sheet-A3.dwt`, `@C3DF-PrintStyle-A3`), profile view `C1_ProfileView`, `tmp/C1-quantities.xlsx` (per station cumulative / incremental cut and fill), `tmp/corridor-views.png`, writes `tmp/corridor-views.dwg` |
| `pkt/02-two-design-lines.json` | channel-demo | assembly `C3DF-TwoLines` from `pkt/SlopeTop_*.pkt` + stock MarkPoint / LinkToMarkedPoint hooked to the toes, corridor `C1_TwoLines` targeting `C1_L` / `C1_R`, corridor surface, 23 sample lines, quantities, 23 section views (bottom width follows the lines); writes `tmp/two-lines.dwg`, `tmp/two-lines-section.png` |
| `pkt/01-bench-assembly.json` | channel-demo | assembly `C3DF-Bench` from `pkt/ChannelBench_*.pkt`, corridor `C1_Bench`; `corridor_stats` lists link codes Top/Datum/Bottom/Slope/Bench/Daylight plus `bottom`, `slope-left/right`, `flat-left/right` and point codes Origin/Bottom/Toe/Hinge/BenchIn/BenchOut/Daylight plus `origin`, `toe-*`, `controlpoint-*`, `mp-*`, `daylight-*` (the codes `@C3DF-Dredge` styles); writes `tmp/bench.dwg` |
| `styles/01-section-view-style.json` | tmp/corridor-views.dwg | edits the drawing's own styles: `@C3DF-SimpleGrid` to a 5 m grid with the station title on top, `@C3DF-CutFill` to red ANSI31, thin grey ground line; writes `tmp/styles.dwg` |
| `styles/02-apply-to-views.json` | tmp/styles.dwg (after 01) | every C1 section view re-pointed at those styles; `tmp/sections-before.png` and `tmp/sections-after.png` |
| `plot/01-inspect-title-blocks.json` | title-block-demo | 3 references with their 8 attributes each |
| `plot/02-fill-and-plot-engine.json` | title-block-demo | attributes rewritten, `tmp/title-block-filled.dwg`, three A3 PDFs `tmp/sheet-0N.pdf` |

The exe path for the plot skill (fill values with `DWGAttributeEditor.exe`, plot with `DWGTitleblockPlotter.exe`) is in `skills/civil3dfactory-plot/SKILL.md`.

## Generating your own PKT pair

```powershell
tools\pkt-forge\bin\out\PktForge.exe channel --name MyChannel --bottom-half-width 12 --slope1-h 2.5 --bench-height 2 --bench-width 3 --slope2-h 3 --out examples\pkt
```

`daylight` (flat search to ground) and `flatdig` (flat bottom only) are the other two templates; `PktForge.exe` without arguments prints the options. Build the tool once with `dotnet build tools\pkt-forge\PktForge.csproj -c Release -o tools\pkt-forge\bin\out` (.NET 8 SDK), or download it from the release.
