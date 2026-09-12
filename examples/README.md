# Examples

Two demo drawings and one task file per skill. Every task runs as is from any clone location:
`{{C3DF_ROOT}}` in a task file expands to the repository folder, `{{DWG_DIR}}` to the drawing's folder.
Outputs go to `tmp/` (git-ignored). The demo drawings are never modified: `save_dwg` always writes a copy.

```powershell
civil3dfactory.ps1 -Dwg examples\channel-demo.dwg -Task examples\corridor\01-recon.json
```

## The drawings

### channel-demo.dwg (linear work: pkt / styles / corridor)

Built from Civil 3D's stock metric NCS template by `build-channel-demo.json`. Metres, DWG 2018.

| Object | Name | Notes |
|---|---|---|
| Terrain surface | `EG` | 1200 x 800 m grid, elevations 3.8 - 6.1 m, style `@C3DF-Terrain` |
| Centerlines | `C1` (1067 m, 3 PIs with curves), `C2` (643 m) | layer `C3DF-CL`, style `@C3DF-Centerline`, station labels |
| Assembly | `C3DF-Channel` | subassemblies `C3DF-Channel_LEFT/RIGHT` from `pkt/ChannelSlope_*.pkt`, PKT embedded; params `SearchOffset` 10 (bottom half-width), `SlopeH` 3 |
| QTO criteria | `@C3DF-CutFill` | two materials: cut = EG above Datum, fill = Datum above EG; surface slots `EG` / `Datum` |
| Section view style | `@C3DF-Section` | stock "Road Section" renamed |
| Section styles | `@C3DF-GroundLine`, `@C3DF-DesignLine` | ground / design section lines |
| Shape styles | `@C3DF-Cut`, `@C3DF-Fill` | material hatch styles used by the criteria |
| Code set style | `@C3DF-CodeSet` | link / point code display with hatching |
| Profile view style / band set | `@C3DF-ProfileView`, `@C3DF-ProfileBands` | |
| Profile styles | `@C3DF-GroundProfile`, `@C3DF-DesignProfile` | |
| Sample line style | `@C3DF-SampleLine` | |

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
| `corridor/03-views-and-export.json` | tmp/corridor.dwg | 23 section views, profile view `C1_ProfileView`, `tmp/C1-quantities.xlsx` (per station cumulative / incremental cut and fill), `tmp/corridor-views.png`, writes `tmp/corridor-views.dwg` |
| `pkt/01-bench-assembly.json` | channel-demo | assembly `C3DF-Bench` from `pkt/ChannelBench_*.pkt`, corridor `C1_Bench`; `corridor_stats` lists link codes Top/Datum/Bottom/Slope/Bench/Daylight/Lining and point codes Origin/Bottom/Toe/Hinge/BenchIn/BenchOut/Daylight; writes `tmp/bench.dwg` |
| `styles/01-section-view-style.json` | channel-demo (or any derived drawing) | new style `@C3DF-Section-5m` (5 m grid, title on top), red cut / blue fill hatch, thin ground line; writes `tmp/styles.dwg` |
| `styles/02-apply-to-views.json` | tmp/corridor-views.dwg (after 01) | every C1 section view switched to the new style; `tmp/sections-before.png` and `tmp/sections-after.png` |
| `plot/01-inspect-title-blocks.json` | title-block-demo | 3 references with their 8 attributes each |
| `plot/02-fill-and-plot-engine.json` | title-block-demo | attributes rewritten, `tmp/title-block-filled.dwg`, three A3 PDFs `tmp/sheet-0N.pdf` |

The exe path for the plot skill (fill values with `DWGAttributeEditor.exe`, plot with `DWGTitleblockPlotter.exe`) is in `skills/civil3dfactory-plot/SKILL.md`.

## Generating your own PKT pair

```powershell
tools\pkt-forge\bin\out\PktForge.exe channel --name MyChannel --bottom-half-width 12 --slope1-h 2.5 --bench-height 2 --bench-width 3 --slope2-h 3 --out examples\pkt
```

`daylight` (flat search to ground) and `flatdig` (flat bottom only) are the other two templates; `PktForge.exe` without arguments prints the options. Build the tool once with `dotnet build tools\pkt-forge\PktForge.csproj -c Release -o tools\pkt-forge\bin\out` (.NET 8 SDK), or download it from the release.
