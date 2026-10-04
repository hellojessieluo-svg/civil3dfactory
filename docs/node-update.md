# Node update

The source tree now contains 139 node contracts. It includes all 138 current workshop node IDs and retains
`create_block_definition`, the additional public node. The public `create_assembly` operation retains support
for arbitrary stock/PKT items as well as LEFT/RIGHT pairs. Existing `help`, `view` and style-construction operations remain available.

## Capabilities

- Surfaces: data shortcuts, cloning related Civil objects, flat surfaces, point additions, breaklines, paste operations,
  identity swaps, renaming and Prospector folders.
- Corridors: region assembly changes, region target mappings, stock subassemblies, parameter updates and offset widening.
- Sections and profiles: source sampling, stale-section cleanup, axes and hatch settings, labels and group layout updates.
- Drawings: weighted label placement, coordinate leaders, cross-cut labels, profile intersection brackets, plan indexes,
  viewport layer overrides, multileaders, text editing and layout content transfer.
- Extraction: `nodes/extract_dwg_text/run.ps1` converts temporary DWG copies to DXF and writes extracted text to Markdown.
  This external-host node requires Python and `ezdxf` in addition to AutoCAD 2025. Its engine adapter reports
  `executed: false`; invoke the PowerShell entry point to run it.

Names, help, errors and contracts are in English. Project drawings, company templates, private history and local
regression evidence are not included. New test descriptors explicitly say `awaiting_public_fixture` where a public
regression fixture has not been established.

## Using this source update

This commit updates source and contracts. It does not replace the existing v1.1.1 release downloads.
Build the current source to use the added operations:

```powershell
scripts\build.ps1 -Targets net8.0-windows
civil3dfactory.ps1 -Help
```

Other external-host nodes keep their own `nodes/<id>/run.ps1` entry points. A deferred engine result is not proof
that an external operation ran. Font normalization preserves its documented font convention.

## Validation

- English/private-term gate: passed across the tracked and new public files.
- Contract audit: no undeclared input drift; 37 thin adapters require inspection of their shared engine implementation.
- Builds: `net8.0-windows` and `net472` passed. The .NET 10 SDK is not installed on the validation machine, so
  `net10.0-windows` was not compiled in this update.
- Civil 3D 2025 headless smoke checks: engine loading, help, surface inventory, feature-line inventory, data-shortcut
  API discovery, profile/section axis inspection, dead-section inspection, flat-surface creation, surface renaming,
  surface pasting and folder assignment returned successful results. The created and pasted surfaces were present
  in the final inventory.
- The executable smoke task is [examples/node-sync-smoke.json](../examples/node-sync-smoke.json).
  Run it against a disposable copy of `examples/channel-demo.dwg`; it changes in-memory geometry and does not call `save_dwg`.

These checks do not establish full drawing regression coverage for every node or every Civil 3D version.
Version-dependent APIs fail explicitly when unavailable. Data-shortcut publish/reference/repair and visual annotation
layout still need suitable drawing-specific verification.
