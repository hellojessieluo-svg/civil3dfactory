# civil3dfactory

**Headless Civil 3D for AI agents.** Build linear infrastructure (channels, dikes, roads) from alignment to corridor,
sections, profiles and material-based quantities - and batch-plot title-blocked drawings to PDF - all from the command
line. No UI, no clicks. A task JSON goes in, a result JSON comes out.

```powershell
civil3dfactory.ps1 -Dwg examples\channel-demo.dwg -Task examples\corridor\02-build.json
```

## What it does

| Linear work (Civil 3D objects) | Plotting work (plain AutoCAD drawings) |
|---|---|
| terrain surfaces, alignments, profiles | read / rewrite title block attributes |
| subassemblies as .pkt (no Subassembly Composer UI), assemblies | one PDF per title block, batch |
| corridors, corridor surfaces, sample lines | merge |
| material-based quantities: cumulative / incremental cut and fill per station, xlsx | |
| section views, profile views, styles | |

The two lines are separate on purpose: headless plotting cannot render Civil 3D objects, so the bridge from a Civil model
to plain-CAD sheets (layouts, viewports, export) stays a manual step in this version.

## For AI agents - one line

```
Read https://github.com/hellojessieluo-svg/civil3dfactory/blob/main/SKILL.md and set it up.
```

## For humans - three steps

1. Install Civil 3D 2022-2027 (2025 / 2026 tested by the author; 2027 and 2022-2024 compile against their SDKs and wait for community testing).
2. Download the latest release zip and unzip it, or `git clone` this repository.
3. Run `powershell -ExecutionPolicy Bypass -File install.ps1` - it detects Civil 3D, installs the engine bundle (downloads the prebuilt one), fetches the two exe tools, installs the skills into your AI agents' skill folders and runs a read-only self-check. Restart Civil 3D once if it was open.

## Requirements

Windows x64 and Civil 3D 2022-2027. Nothing else: no Python, no .NET SDK, no AutoCAD settings changed
(the engine is loaded from the standard `ApplicationPlugins` bundle folder).

| Civil 3D | Engine | Status |
|---|---|---|
| 2025 / 2026 (.NET 8) | `Contents/2025-2026` | tested on the author's machine |
| 2027 (.NET 10) | `Contents/2027` | compiles; community testing welcome |
| 2022 / 2023 / 2024 (.NET Framework 4.8) | `Contents/2022-2024` | compiles; a few 2023+ API calls degrade gracefully on 2022 |
| 2021 and older | - | not supported |

## Quick start

```powershell
civil3dfactory.ps1 -Help                                                       # every op, one line each
civil3dfactory.ps1 -Dwg examples\channel-demo.dwg -View outline                 # what is in the drawing
civil3dfactory.ps1 -Dwg examples\channel-demo.dwg -Task examples\corridor\02-build.json
civil3dfactory.ps1 -Dwg tmp\corridor.dwg -Task examples\corridor\03-views-and-export.json
```

Open `tmp\corridor-views.dwg` in Civil 3D: corridor, 23 section views, a profile view; `tmp\C1-quantities.xlsx` has the cut per station.

## How it works

- **L1 view** - `-View outline | stats | issues | screenshot`: read-only reconnaissance, never guess object or style names.
- **L2 ops** - 140+ operations in the engine (`-Help`), composed in a task JSON `{ "ops": [ {"op": "...", ...}, ... ] }`.
  Every op returns its own receipt; `ok: true` with empty results is treated as a failure, not a success.
- **L3 exe** - `DWGAttributeEditor.exe` and `DWGTitleblockPlotter.exe` for batch work on plain drawings; `PktForge.exe` writes subassemblies offline.
- Nothing is written to the drawing you opened: add `save_dwg` with an `out` path to keep a result.
- `-Resident` keeps one accoreconsole per drawing alive and skips the ~6 s cold start on every following call.

## Where things are

```
civil3dfactory.ps1   entry point (task JSON in, result JSON out, -Help, -View)
install.ps1          one-shot installer (engine, exe tools, skills, self-check)
SKILL.md             the agent manual; skills/ has the four specialised skills (pkt, styles, corridor, plot)
engine/              C# engine source -> Civil3DFactory.dll / Civil3DFactory.bundle
nodes/               op contracts (node.json) + implementations + factory test records
examples/            channel-demo.dwg, title-block-demo.dwg, one task per skill, README with the conventions
tools/               pkt-forge, dwg-attribute-editor, dwg-titleblock-plotter (source; exe files come with the release)
scripts/             build.ps1, resident.ps1 + resident-lib.ps1, gate.ps1 (pre-commit blacklist), contract-audit.ps1
```

## Examples

See [examples/README.md](examples/README.md): what each demo drawing contains and what every task file produces.

## Contributing

[CONTRIBUTING.md](CONTRIBUTING.md) - building the engine for all three generations (.NET 8 + .NET 10 SDK), packaging the
exe tools (Python 3.10+), the blacklist gate, the contract audit, and the lookup order examples -> skills -> `-Help` -> tools.

## License

Apache-2.0. The repository contains only its own source; Autodesk assemblies are referenced through NuGet at build time and are never redistributed.
