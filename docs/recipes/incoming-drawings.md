# Incoming drawings: clip to a boundary, fix text styles, make plan text annotative

Distilled 2026-11-09 from three field-tested recipes. Drawings from other offices are full of proxy objects,
locked layers, pasted anonymous blocks and broken text styles; every recipe starts read-only.

## 1. Keep only what lies inside one closed boundary

Task: a large drawing (tens of thousands of entities) and one closed polyline someone drew around the part
they want; deliver a DWG with only that part, everything inside kept as is.

1. Recon: `entities_locate` (geometry:true, all types) + `entity_stats` + `block_refs`; identify the boundary
   handle by length, area and vertex count.
2. Select with shapely: polylines and lines by true geometry, everything else by its extents.
   `contains` -> keep, `intersects` -> keep whole, the rest -> erase list.
3. On a copy: unlock all layers (accoreconsole script `_.-LAYER _U *` + `_.QSAVE`; there is no op for it), then
   `erase_entities` (outside handles) -> `save_dwg` to a new file -> `plot_pdf` with the boundary window and
   `ctb:""` as a colour preview.
4. Done means: re-read entity count = kept + objects without extents; proxy count unchanged inside the boundary;
   zero entities whose extents lie fully outside; the preview matches the requester's screenshot.

Pitfalls:
- Do not build a new drawing and clone by handle: `import_entities` fails on proxy entities (the whole op fails and
  `save_dwg` still writes an empty drawing). Erase outside on a copy instead.
- `erase_entities` fails as a batch on a locked layer and `save_dwg` still writes the unchanged drawing; unlock
  first and delete outputs of failed runs.
- MText extents are unreliable (kilometres wide); anonymous pasted blocks inserted at 0,0 can span thousands of
  kilometres because of outliers. Decide boundary-crossing MText and large blocks one by one.
- Boundary-crossing linework is kept whole, so the result can reach beyond the boundary (no trim op).

Follow-up on the same file - keep model space only and fix missing fonts:
- Missing fonts: the `Substituting [simplex.shx] for [...]` lines accoreconsole prints on open are the list.
- Delete layouts with native `-LAYOUT _D <name>` one by one (no wildcard); one empty paper layout always remains.
- TTF styles: native `-STYLE <name> <font.ttf>` followed by five Enters writes the typeface correctly;
  restore `TEXTSTYLE` afterwards. SHX styles: `textstyle_edit`, replacing only the missing main or big font.
- Done means: reopening prints no `Substituting` line and `-LAYOUT ?` lists one layout.
- A remaining "missing font" can be a shape file of a custom linetype. Garbled text inside proxy graphics was
  written wrongly by the authoring add-in (code page conversion baked into Unicode) and cannot be fixed by fonts.

## 2. Normalise text to the standard body text style

Task: text looks wrong or CJK characters show as boxes; bring every text to the company's single body style.

1. Diagnose before changing. The entities may already use the right style name while the style record is broken
   (big font pointing to the wrong file). Read-only recon: `entity_stats` + `list_blocks` + `list_layers used_only:true`;
   `texts_edit` with `dry_run:true` groups hits by host, type, style and height; DXFOUT + ezdxf shows the STYLE table.
2. Fix: `textstyle_edit` (font, big font, width, oblique, height 0) -> `texts_edit` with `where:"all"` and
   `match.styles` limited to the styles you mean -> `save_dwg` to a side file -> swap it in after the session ends.
3. Take the style values from the company baseline's title block source (DXFOUT it), not from memory; take the
   minimum text height from the drafting standard.
4. Done means: the receipt shows the before/after of the style record, `matched == applied`, no failures, and an
   independent DXFOUT check shows every text on the style with the expected height and width, entity count unchanged.

Pitfalls: `save_dwg apply:true` on the drawing accoreconsole opened throws eFilerError and still leaves a backup
file - save to a side file. Never start accoreconsole from a detached Git Bash background job (it idles at
`Command:`). `normalize_textstyles` is the wrong tool here (it needs specific styles and full AutoCAD COM).
Report, but do not touch, other problems you notice in someone else's drawing (layers inside blocks, unused styles).

## 3. Convert plan text to annotative

Task: plan text was written in model units (paper mm x plot scale, e.g. 17.5 at 1:5000); make it annotative so it
is written in paper millimetres and follows the annotation scale.

```
set_scale -> textstyle_edit (create the annotative body style) -> texts_edit (per height band: style, paper
height, annotative) -> viewport_annoscale -> save_dwg -> separate accoreconsole script: -OBJECTSCALE
```

```json
{"ops":[
 {"op":"set_scale","scale":5,"name":"1:5000"},
 {"op":"textstyle_edit","items":[{"name":"<body-style>-anno","font":"<font.shx>","bigfont":"<bigfont.shx>",
   "width":0.8,"oblique":0,"height":0,"annotative":true,"paper_orientation":true,"create":true}],"dry_run":false},
 {"op":"texts_edit","where":"model","match":{"layers":["<annotation layer>"],"height":17.5},
  "set":{"style":"<body-style>-anno","height":3.5,"annotative":true},"dry_run":false},
 {"op":"viewport_annoscale","dry_run":false},
 {"op":"save_dwg","out":"<scratch>/x.dwg","overwrite":true}
]}
```

Script for the last step (ASCII only, CRLF; select by style name, not by a non-ASCII layer name):

```
TILEMODE
1
(setvar "CANNOSCALE" "1:5000")
(setq ss (ssget "_X" (list (cons 0 "TEXT") (cons 7 "<body-style>-anno") (cons 410 "Model"))))
(if ss (command "_.-OBJECTSCALE" ss "" "_A" "1:5000" ""))
TILEMODE
0
_.QSAVE
QUIT
_Y
```

- Read the plot scale, do not ask: the `viewport_annoscale` dry-run receipt gives each main viewport's
  `custom_scale` (0.2 -> 1:5000, 0.5 -> 1:2000). Sheets in one set can differ - check each.
  `set_scale` takes `1/custom_scale` for metric drawings.
- Original height / target paper height must equal the plot scale; when it does not, the drawing was off-standard
  to begin with - ask whether to normalise or keep the look.
- Only model-space annotation layers; paper-space text is already in paper units.
- Done means: the script prints `N objects updated to support annotation scale`, N = the sum of `texts_edit.applied`,
  and a re-plot compared with the original at 150 dpi differs by 0 pixels. Copy the original into the same folder
  before plotting it as the reference (relative xrefs resolve differently elsewhere).

Pitfalls:
1. Headless edits mark text annotative but attach no scale context (no regen); the entity disappears at any
   viewport scale other than 1:1. Adding the context through the internal ObjectContexts API crashes accoreconsole
   outright; `-OBJECTSCALE` is the only way, and only in model space (`TILEMODE 1` first).
2. Viewports created headless keep the annotation scale that was current when the drawing was made;
   `viewport_annoscale` aligns each viewport with its own custom scale.
3. Annotative objects whose insertion point lies outside the viewport are not drawn at all.
4. `plot_pdf` with only `layout` sometimes plots a blank page; pass the paper-space window explicitly.
5. For leader-style notes prefer multileaders (`_MLEADER` objects get their scale right); keep this TEXT route for
   titles and notes without leaders. One sheet stayed at 1/5 size after every check matched a working sheet;
   next time try `ANNOUPDATE` in a full AutoCAD session.

## Gaps found while distilling (not built; for a separate request)

- An op to delete layouts (by list, or all but Model).
- TTF typeface writing in `textstyle_edit` (today native `-STYLE`).
- `unlock:true` for `erase_entities`, or a layer lock/unlock op.
- Trimming along a boundary (EXTRIM-like).
