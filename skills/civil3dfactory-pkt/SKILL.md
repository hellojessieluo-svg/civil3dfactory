---
name: civil3dfactory-pkt
description: Parametric Civil 3D subassemblies without the Subassembly Composer UI - generate a LEFT/RIGHT .pkt pair (flat bottom, side slopes, optional bench and lining) with pkt-forge, import it as an assembly and embed it in the drawing, check the point / link / shape codes by building a corridor. Use when a cross-section needs a custom subassembly or an assembly must be created or repaired. Load civil3dfactory first.
---

# civil3dfactory-pkt

Repository: `{{C3DF_ROOT}}`. Two tools: `tools\pkt-forge\bin\out\PktForge.exe` (offline, no CAD needed) writes the .pkt files;
the engine op `create_assembly` puts them in the drawing.

## When to use

- A section is described in words ("20 m bottom, 1:3 slopes, 2 m bench") and must become an assembly.
- An assembly exists but its subassemblies report `status != UpToDate` (external PKT lost): re-import with `create_assembly` (new) or `check_sac_paths` (replace in place).

## Inputs

| Input | Source |
|---|---|
| bottom width | designer; pkt-forge takes the half width per side (`--bottom-half-width` = width / 2) |
| side slope 1:n | designer (`--slope1-h n`) |
| bench | height above the bottom and width (`--bench-height`, `--bench-width`); 0 height = no bench |
| upper slope | `--slope2-h` |
| lining | `--lining <thickness>` adds a closed shape coded `Lining` under the bottom (only when > 0) |
| target surface | the terrain; the subassembly exposes one surface target `EG_Surface`, `create_corridor` maps it |

## Node sequence

```
PktForge.exe channel --name <Name> --bottom-half-width W/2 --slope1-h n1 [--bench-height h --bench-width b --slope2-h n2] [--lining t] --out <folder>
create_assembly  name, left_pkt, right_pkt, params?{...}      imports both sides, embeds the PKT, reports status + params
create_profiles + create_corridor (corridor skill)             proves the geometry
corridor_stats                                                 point codes, link codes, corridor surfaces
view screenshot                                                picture of the section views if you built them
```

Templates: `channel` (bottom + slope + optional bench + optional lining shape), `daylight` (flat search to ground then slope),
`flatdig` (flat bottom only). `PktForge.exe` with no arguments prints every option.

Codes written by `channel`: points `Origin, Bottom, Toe, Hinge, BenchIn, BenchOut, Daylight, Daylight_Cut` (+ `LiningBottom`);
links `Top, Datum, Bottom, Flat, Slope, Bench, Daylight, Cut` (+ `Lining, Subbase`); shape `Lining`. `Top` / `Datum` are what
`create_corridor_surface` and the quantity criteria look for.

## Example task

`examples/pkt/01-bench-assembly.json` on `examples/channel-demo.dwg`: imports `examples/pkt/ChannelBench_*.pkt` as `C3DF-Bench`,
builds corridor `C1_Bench`, saves `tmp/bench.dwg`. `corridor_stats` in the result lists the codes above.

Generate the pair for "20 m bottom, 1:3 slopes, 2 m bench 1.5 m up, 1:3 above":

```powershell
tools\pkt-forge\bin\out\PktForge.exe channel --name Dredge20 --bottom-half-width 10 --slope1-h 3 --bench-height 1.5 --bench-width 2 --slope2-h 3 --out tmp
```

Then `{"op":"create_assembly","name":"Dredge20","left_pkt":"<abs>/tmp/Dredge20_LEFT.pkt","right_pkt":"<abs>/tmp/Dredge20_RIGHT.pkt"}`.
Parameters stay editable afterwards: `params: {"BenchWidth": 3}` on `create_assembly`, or in the Civil 3D UI.

## Acceptance

- `create_assembly` reports both subassemblies with `status: UpToDate` and `embedded: true` (Civil 3D 2023+; on 2022 embedding is unavailable and the .pkt files must stay where they are).
- `create_corridor` on that assembly returns the link codes; `corridor_stats` shows the point codes; with `--lining` the corridor also has shape code `Lining`.
- The section (`create_section_views` + `view screenshot`) shows bottom, slope and bench.

## Known pitfalls

- The subassembly name inside a PKT must start with a letter and contain only letters, digits and underscores; pkt-forge sanitises `--name` for the internal name, the file name keeps what you typed.
- Ground lower than the bench height: the `channel` template skips the bench and daylights from the first slope (that is a feature); ground below the bottom edge produces no cut on that side.
- The corridor's surface target must be set (`create_corridor` does it); an unmapped `EG_Surface` gives no daylight points.
- `params` keys are the PKT display names (`BottomHalfWidth`, `Slope1H`, ...); unknown keys are ignored and counted in `params_applied`.
- `ImportSACSubassembly` needs Civil 3D 2023 or newer; on 2022 import the PKT through the tool palette once and use `check_sac_paths`.
