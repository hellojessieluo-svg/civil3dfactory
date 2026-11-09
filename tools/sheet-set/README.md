# sheet-set

One command per stage of a sheet set that is already drawn: re-plot what changed, verify, merge, catalogue,
and compare with a reference set. Generalised on 2026-11-09 from a project's print/verify/album/catalogue
chain (279 sheets) and the "reproduce an old sheet set" and "check the catalogue against the drawings" recipes.

```powershell
python tools\sheet-set\sheet_set.py <config.json> plot      # re-plot changed DWGs only (sha256 cache)
python tools\sheet-set\sheet_set.py <config.json> album     # merge in sheet-number order, bookmarks, TOC, blank check
python tools\sheet-set\sheet_set.py <config.json> catalog   # catalogue xlsx from the title blocks
python tools\sheet-set\sheet_set.py <config.json> verify    # all cross-checks, sha256 of every deliverable
python tools\sheet-set\sheet_set.py <config.json> compare <reference_pdf_dir> [--dpi 120]
python tools\sheet-set\sheet_set.py <config.json> catalog --check <someone_elses_catalogue.xlsx>
python tools\sheet-set\sheet_set.py <config.json> verify --reference <old_attrs.json> --allow-tags <TAG> ...
```

Typical order after editing title blocks: `plot` -> `album` -> `catalog` -> `verify`.
Each command writes a JSON receipt into the state folder and exits 1 when a check fails.

Requires: Civil 3D / AutoCAD 2025 or 2026 (accoreconsole), the factory's `CadPlotPlugin.dll`
(`tools\dwg-titleblock-plotter\CadPlotPlugin\bin\hotload\`) and `DWGAttributeEditor.exe`, Python with
`pymupdf`, `pypdf`, `openpyxl`, `numpy`.

## Config

Paths are relative to the config file. See `example.json`.

| Key | Meaning |
|---|---|
| `drawings` | Folder with the sheet DWGs (top level only). |
| `pdf_dir` | Single-sheet PDFs (default `<drawings>\PDF`). The plotter names each PDF `<number> <title>`. |
| `album`, `catalog` | Merged PDF and catalogue xlsx. |
| `expect_sheets` | Optional sheet count every check must hit. |
| `tags` | Title block tags: `number`, `title`, `scale`. |
| `siblings` | Glob patterns of files that are not sheets but must sit next to every DWG copy when plotting (relative xrefs, images). They also feed the re-plot fingerprint. |
| `exclude` | Other DWGs in the folder that are not sheets. |
| `block_keywords` | Title block name fragments for the plotter. |
| `plot.default`, `plot.rules` | `ctb` (`""` = colour), `transparency`, `lineweights`, `lwdefault`; rules match DWG file names (`"match": "1101*"`). |
| `paper`, `page_size_pt` | Paper name for the plotter and the page size `verify` expects (A3 = 1190.55 x 841.89 pt). |
| `backup_cmd` | Optional command run before a PDF/xlsx is overwritten, `{path}` is replaced by the file. |
| `attribute_editor`, `attribute_editor_args`, `plot_plugin`, `accoreconsole` | Overrides of the tool locations. |
| `catalog_title`, `catalog_headers` | Catalogue sheet header. |

## What the checks mean

- `verify`: title blocks have a number and numbers are unique; title blocks = PDFs (by name) = catalogue rows =
  album pages = album bookmarks; each PDF is one page of the expected size and not blank; no PDF is older than
  its DWG; every album page renders identically (96 dpi) to its single PDF. With `--reference`, only the listed
  tags may differ from an earlier attribute export.
- `compare`: for reproducing a set the client already has. Per page: differing pixels, ink ratio new/old and the
  normalised box of the difference. A clean reproduction shows only a few dozen pixels inside the edited title
  block cell. Ink doubled = lineweights on when the old set had them off; ink down 30 % = the reverse;
  differences everywhere with equal ink = sub-pixel shift from another backend (`overlap_within_1px` > 0.99 is fine).
- `catalog --check`: listed but not drawn, drawn but not listed, title mismatches, duplicates on both sides.
  Ranges such as `X-1003~1004` (also the full-width tilde) are expanded.

## Known limits

- Attributes are exported one DWG at a time: the editor's multi-file batch hung accoreconsole on 2026-11-09
  for sheets that export fine individually.
- Sheets whose title text is plain TEXT (no attributes) cannot be read here; use `extract_dwg_text --keep-dxf`
  and pair numbers and titles by position (see the sheet-sets skill page).
