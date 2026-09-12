---
name: civil3dfactory-plot
description: Batch title-block work on plain AutoCAD drawings without opening the UI - read and rewrite title block attributes (sheet title, number, date, notes) with DWGAttributeEditor, plot every title block to its own PDF with DWGTitleblockPlotter (or the engine's plot_pdf), merge PDFs. Use when sheets already exist as pure CAD drawings with attributed title blocks and only values or PDFs are needed. Load civil3dfactory first.
---

# civil3dfactory-plot

Repository: `{{C3DF_ROOT}}`. Tools: `tools\dwg-attribute-editor\DWGAttributeEditor.exe`, `tools\dwg-titleblock-plotter\DWGTitleblockPlotter.exe`
(installed by `install.ps1`; both are also plain Python scripts next to the exe with the same arguments).

## When to use

Drawings with attributed title blocks (block name contains `TITLE`, or pass `--block`) need new values and PDFs.
The input must be pure CAD: headless plotting does not render Civil 3D objects (section views, profile views) - such a
drawing plots as an empty page without an error. Turning a Civil model into pure-CAD sheets (layouts, viewports,
`export_to_autocad`) is manual in this version and not covered here.

## Inputs

| Input | Where |
|---|---|
| drawings (folder or single file) | your sheet drawings, e.g. `examples/title-block-demo.dwg` |
| title block name filter | default `TITLE`; `--block <substring>` |
| values to write | your text; multi-line MText attributes take `\P` as line break |
| plotter | `DWG To PDF.pc3` + `monochrome.ctb` (default) or `--color` |

## Node sequence

```
dump_block_attributes name:TITLE                              (engine op) see references, handles and current values
DWGAttributeEditor.exe --headless-export <folder|dwg> <out.json>  export every title block's attributes to JSON
  edit "value" fields in the JSON (keep handles and original_value)
DWGAttributeEditor.exe --headless-update <folder|dwg> <in.json>   write back (originals backed up under .dwg-attribute-backups)
DWGTitleblockPlotter.exe <folder-or-dwg> <outdir> [--block X] [--color]   one A3 PDF per title block, named <sheet no>-<title>
merge: pypdf or any PDF tool                                   optional
```

Engine-only alternative (no exe): `set_block_attributes` -> `save_dwg` -> `plot_pdf` with a `window` per sheet
(`examples/plot/02-fill-and-plot-engine.json`).

## Example task

`examples/plot/01-inspect-title-blocks.json` on `examples/title-block-demo.dwg` lists the 3 sheets. Then:

```powershell
tools\dwg-attribute-editor\DWGAttributeEditor.exe --headless-export examples\title-block-demo.dwg tmp\attrs.json
# edit tmp\attrs.json: e.g. every "SHEET_TITLE" value -> "Dredging section, demo", "DATE" -> today
tools\dwg-attribute-editor\DWGAttributeEditor.exe --headless-update examples\title-block-demo.dwg tmp\attrs.json
tools\dwg-titleblock-plotter\DWGTitleblockPlotter.exe examples\title-block-demo.dwg tmp\pdf
```

Run the update on a copy of the drawing when the original must stay untouched.

## Acceptance

- Export JSON lists as many blocks as `dump_block_attributes` `count`.
- After update, re-export and diff: only the values you changed differ.
- Number of PDFs = number of title block references; each PDF is larger than about 10 KB (a 2 KB PDF is an empty page).

## Known pitfalls

- Civil objects in the drawing = blank PDFs. Check `list_civil_views` returns 0 before plotting; export to pure CAD first.
- Model-space and paper-space title blocks in one folder: the active tab when the drawing was saved decides what plots; keep one kind per folder.
- `.dwg-attribute-backups\` created by the update step is a folder of drawings - move it out before plotting a folder, or the backups plot too.
- Attribute tags are matched exactly (`SHEET_NO` not `SHEETNO`); `dump_block_attributes` shows the real tags.
- PDF text is not searchable for SHX fonts: verify by looking at the page, not by text search.
