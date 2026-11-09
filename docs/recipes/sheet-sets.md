# Sheet sets: re-plot, reproduce, check, re-brand

Distilled 2026-11-09 from four field-tested recipes. Tool for the mechanical parts: `tools/sheet-set/sheet_set.py`
(see its README). Every recipe ends with a measured check, never with "looks the same".

## 1. Re-plot a set after title block edits (the print chain)

1. Edit values with `DWGAttributeEditor.exe --headless-export` -> change `value` -> `--headless-update`
   (it backs up first). Re-export at once and diff: only the target tag may change
   (`sheet_set.py verify --reference <export_before.json> --allow-tags <TAG>`).
2. `sheet_set.py plot`: re-plots only DWGs whose sha256 (or a listed sibling file, or the plot settings) changed.
   Per-sheet settings come from `plot.rules` - sets are rarely plotted with one setting throughout.
3. `album` -> `catalog` -> `verify`. Done means `verify: PASS`: title blocks = PDFs = catalogue rows = album pages,
   unique numbers, no blank page, no PDF older than its DWG, album pages identical to the single PDFs.

## 2. Reproduce a set the client already has

The risk is not the edited field, it is the re-plot: old sets were often plotted in several rounds with different
backends and switches, and one uniform re-plot changes ink density, line weights and transparency everywhere.

1. Prove the client's PDFs and your DWGs are the same generation: title block count = old page count; find the
   sheets that do not match before anything else.
2. Use the old PDFs as the calibration sample. Plot one small sheet, then `sheet_set.py compare <old_pdf_dir>`
   and switch `lineweights`, `transparency` and `ctb` (monochrome or colour) until the difference shrinks to a few
   dozen pixels inside the edited title block cell. Put the winning settings into `plot.rules` per sheet group.
3. Plot all, compare all. Reading the numbers:

| Result | Meaning |
|---|---|
| Difference box inside the edited cell, ~50 px | Perfect reproduction |
| Same ink, differences across the page, `overlap_within_1px` > 0.99 | Sub-pixel shift from another backend; acceptable |
| Ink down 30 % or more, overlap ~0.5 | Lineweights off where the old set had them on (lines became hairlines) |
| Ink doubled | Lineweights on where the old set had them off (0.30 mm default smears small text) |
| New ink contains the old plus 3-5 % | Anti-aliasing; confirm with a difference image that the extra pixels sit on old lines |

## 3. Check a sheet catalogue against the drawings

Only the drawings count, never file names (a file called "detail A" can carry the title "detail B").

- Title blocks with attributes: `sheet_set.py catalog --check <catalogue.xlsx>` lists listed-not-drawn,
  drawn-not-listed, title mismatches and duplicates; ranges like `X-1003~1004` are expanded.
- Title text as plain TEXT (often an xref'd frame plus host text, several frames side by side in model space):
  1. `extract_dwg_text --keep-dxf`, one drawing per call (a crash in one drawing loses the whole batch;
     the Markdown output is de-duplicated across the drawing, so counts and duplicates must come from the DXF).
  2. Read TEXT/MTEXT/ATTRIB with insertion points (ezdxf), pair every sheet number with the nearest title.
     Derive the offset from a sheet you trust (print the six nearest neighbours of one number), then list all
     pairing distances: a clean set clusters tightly (for example 19-29 units) with nothing in the grey zone up to
     the threshold. Points in the grey zone mean a different title block layout - open those sheets.
  3. Exclude frame source files, base maps, other stages and older copies (list older copies separately:
     "do not send the wrong CAD"). Drop the project-number cell that every frame carries.
- Pitfalls: two-line titles (check the next neighbours before reporting a mismatch); drawings with many proxy
  objects can write truncated DXF (`missing ENDSEC`) - after three identical failures mark the sheet "not checked";
  decide "catalogue wrong" versus "drawing wrong" - never copy a wrong drawing into the catalogue.
- Deliver the corrected catalogue plus a "check record" sheet: number, title, issue, action and the evidence
  ("counted 8 title blocks", not "checked"). Re-run the comparison after editing: fixed findings must disappear and
  no new ones appear; new rows copy the style of the row above, gutter columns included.

## 4. Swap a whole title block for another organisation's

For drawings whose old frame has no attributes (static text in the block definition plus loose text on the layout).

1. Recon: `block_refs` + `list_blocks` (frame block name, count, space).
2. New frame source: remove stage/part cells written into the block definition (`erase_entities`), so one source
   serves every stage; write those per sheet instead.
3. Target: `erase_entities` old frame references and the loose title texts -> `modify_dwg apply` with
   `from_dwg` + `source_block` to insert the new frame -> a second `modify_dwg` for the texts (the text style comes
   in with the block, so blocks first, texts in a later op).
4. Insertion point: old insertion point + (old block bbox min - old insertion point) = sheet corner.
   Cell boundaries are computed from the new block's lines, not measured.
5. Text: `modify_dwg` texts have a left-bottom insertion point only, so centre them yourself, and pass the width
   factor explicitly (it does not follow the style). Measure glyph widths from a plotted PDF rather than trusting the
   style width factor; shrink the height of long titles to fit the cell.
6. Re-defining a same-name block (`DuplicateRecordCloning.Replace`) swaps every reference at once, including nested
   ones; cells that lived in the old definition must be re-written.
7. Check: extract the text of every plotted PDF - exactly one number and one stage string per page, none of the old
   organisation's identifiers left; glue the title block corners of all pages into one contact sheet for review.

Related pitfalls: centred TEXT renders from its stale insertion point (DXF 10), not from the alignment point
(DXF 11) - compare with a correct sample and rewrite, don't nudge by eye. Under `monochrome.ctb`, objects with a true
colour (DXF 420) keep their colour, which is how "black and white except water features" is done. Dimensions with
lines but no digits usually mean a missing big font (`findfile` both fonts). Logos are images with relative paths:
ship the image next to the DWG.

## 5. Extract and desensitise a demo title block

WBLOCK the frame -> delete identity entities in the extracted model space and clear every ATTDEF default ->
normalise fonts -> SAVEAS -> test-insert into an empty drawing -> fill values with the attribute editor -> recompute
centred text positions (DXF10.x = DXF11.x - width/2 for the current value; keep DXF10.y) -> plot and inspect.
A logo's extent comes from its full geometry (hatch boundaries, old polylines), not from the first vertex - the
first attempt left a logo centre behind. Done means: defaults empty, no extra block definitions, a text scan finds
no identity strings, and the plotted crop shows no logo remains.
