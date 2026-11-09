"""Sheet-set tool: re-plot, verify, merge and catalogue a set of title-blocked DWG sheets.

    python sheet_set.py <config.json> plot    [--only PREFIX ...] [--force]
    python sheet_set.py <config.json> verify  [--reference attrs.json --allow-tags TAG ...]
    python sheet_set.py <config.json> album
    python sheet_set.py <config.json> catalog [--check existing.xlsx]
    python sheet_set.py <config.json> compare <reference_pdf_dir> [--dpi 120]

plot     re-plots only the DWGs whose content (or listed sibling files) changed since the last run,
         per-sheet plot settings from config rules, through CadPlotPlugin in accoreconsole.
verify   re-reads every title block (DWGAttributeEditor) and checks: unique sheet numbers, title blocks =
         PDFs = catalog rows = album pages, PDF names, one page each, page size, no blank page, PDF not older
         than its DWG, album page renders equal the single PDFs; optional: only the allowed tags changed
         against a reference attribute export.
album    merges the single PDFs in sheet-number order with one bookmark per sheet and a text TOC.
catalog  writes the sheet catalogue xlsx from the title blocks, or checks an existing one against them.
compare  page-by-page render comparison with a reference set (the "reproduce an old sheet set" check):
         ink ratio, differing pixels and the normalised bounding box of the difference.
Every command writes a JSON receipt into the state folder and exits non-zero when a check fails.
See README.md for the config keys.
"""
from __future__ import annotations

import argparse
import datetime
import fnmatch
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
FACTORY = HERE.parent.parent
DEFAULT_PLUGIN = FACTORY / "tools" / "dwg-titleblock-plotter" / "CadPlotPlugin" / "bin" / "hotload" / "CadPlotPlugin.dll"
DEFAULT_EDITOR = FACTORY / "tools" / "dwg-attribute-editor" / "DWGAttributeEditor.exe"


# ---------- config ----------
class Config:
    def __init__(self, path: Path):
        self.path = path.resolve()
        d = json.loads(path.read_text(encoding="utf-8-sig"))
        base = self.path.parent

        def p(key, default=None):
            v = d.get(key)
            return (base / v).resolve() if v else default

        self.raw = d
        self.drawings = p("drawings")
        if not self.drawings or not self.drawings.is_dir():
            raise SystemExit(f"config.drawings is not a folder: {d.get('drawings')}")
        self.pdf_dir = p("pdf_dir", self.drawings / "PDF")
        self.album = p("album")
        self.catalog = p("catalog")
        self.state = p("state_dir", base / ".sheet-set-state")
        self.expect = d.get("expect_sheets")
        tags = d.get("tags", {})
        self.tag_number = tags.get("number", "SHEET_NO")
        self.tag_title = tags.get("title", "TITLE")
        self.tag_scale = tags.get("scale")
        self.siblings = d.get("siblings", [])
        self.exclude = d.get("exclude", [])
        self.block_keywords = d.get("block_keywords", ["TITLE"])
        self.paper = d.get("paper", "A3")
        self.page_pt = d.get("page_size_pt", [1190.55, 841.89])
        self.plot_default = {"ctb": "monochrome.ctb", "transparency": False, "lineweights": True, "lwdefault": 25,
                             **d.get("plot", {}).get("default", {})}
        self.plot_rules = d.get("plot", {}).get("rules", [])
        self.backup_cmd = d.get("backup_cmd")
        self.editor = Path(d["attribute_editor"]) if d.get("attribute_editor") else DEFAULT_EDITOR
        self.editor_args = d.get("attribute_editor_args", [])
        self.plugin = Path(d["plot_plugin"]) if d.get("plot_plugin") else DEFAULT_PLUGIN
        self.acc = d.get("accoreconsole") or os.environ.get("C3DF_ACCORECONSOLE") or next(
            (x for x in (rf"C:\Program Files\Autodesk\AutoCAD {y}\accoreconsole.exe" for y in (2025, 2026))
             if Path(x).exists()), r"C:\Program Files\Autodesk\AutoCAD 2025\accoreconsole.exe")
        self.catalog_title = d.get("catalog_title", "Sheet catalogue")
        self.catalog_headers = d.get("catalog_headers", ["No.", "Sheet No.", "Title", "Scale"])
        self.blank_ink = float(d.get("blank_ink", 0.002))
        self.state.mkdir(parents=True, exist_ok=True)

    def sheets(self) -> list[Path]:
        skip = set(self.siblings) | set(self.exclude)
        return sorted(f for f in self.drawings.glob("*.dwg")
                      if not any(fnmatch.fnmatch(f.name, pat) for pat in skip))

    def sibling_files(self) -> list[Path]:
        return sorted({f for pat in self.siblings for f in self.drawings.glob(pat) if f.is_file()})

    def plot_settings(self, dwg: Path) -> dict:
        s = dict(self.plot_default)
        for r in self.plot_rules:
            if fnmatch.fnmatch(dwg.name, r["match"]):
                s.update({k: v for k, v in r.items() if k != "match"})
        return s


def sha(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()


def receipt(cfg: Config, name: str, data: dict) -> Path:
    out = cfg.state / f"{name}-{datetime.datetime.now():%y%m%d-%H%M%S}.json"
    out.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    return out


def backup(cfg: Config, path: Path) -> None:
    if cfg.backup_cmd and path.exists():
        cmd = [str(a).replace("{path}", str(path)) for a in cfg.backup_cmd]
        subprocess.run(cmd, check=True, capture_output=True)


def sanitize(name: str) -> str:
    """Same rule as CadPlotPlugin: the PDF is named '<number> <title>' with path characters replaced."""
    return re.sub(r'[\\/:*?"<>|]', "_", name).strip()


def sort_key(number: str):
    m = re.search(r"(\d+)\D*$", number)
    return (int(m.group(1)) if m else 10 ** 9, number)


# ---------- title blocks ----------
def export_one(cfg: Config, dwg: Path, timeout: int = 300) -> dict:
    """Attribute export of a single drawing. The editor's multi-file batch can hang accoreconsole
    (seen 2026-11-09 on three sheets that export fine one by one), so each sheet runs alone."""
    work = Path(tempfile.mkdtemp(prefix="sheet_set_attrs_"))
    try:
        (work / "in").mkdir()
        shutil.copy2(dwg, work / "in" / dwg.name)
        out = work / "attrs.json"
        p = subprocess.Popen([str(cfg.editor), *cfg.editor_args, "--headless-export", str(work / "in"), str(out)],
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, stdin=subprocess.DEVNULL)
        try:
            p.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(p.pid)], capture_output=True)
            raise SystemExit(f"{dwg.name}: attribute export timed out after {timeout} s")
        if not out.exists():
            raise SystemExit(f"{dwg.name}: attribute export wrote nothing (exit {p.returncode})")
        return json.loads(out.read_text(encoding="utf-8-sig"))
    finally:
        shutil.rmtree(work, ignore_errors=True)


def read_title_blocks(cfg: Config) -> list[dict]:
    """One row per title block instance: file, handle, attributes. Also saved as an editor-format export."""
    rows, files = [], []
    for dwg in cfg.sheets():
        for f in export_one(cfg, dwg)["files"]:
            f["relative_path"] = dwg.name
            files.append(f)
            for b in f["blocks"]:
                rows.append({"file": dwg.name, "handle": b.get("block_handle"),
                             "attrs": {a["tag"]: a["value"] for a in b["attributes"]}})
    (cfg.state / "titleblocks-current.json").write_text(
        json.dumps({"files": files}, ensure_ascii=False, indent=2), encoding="utf-8")
    return rows


# ---------- plot ----------
def plot_one(cfg: Config, dwg: Path, settings: dict) -> list[Path]:
    if not cfg.plugin.exists():
        raise SystemExit(f"CadPlotPlugin.dll not found at {cfg.plugin}: build it with "
                         r"tools\dwg-titleblock-plotter\build_exe.ps1 or set plot_plugin in the config")
    work = Path(tempfile.mkdtemp(prefix="sheet_set_plot_"))
    try:
        shutil.copy2(cfg.plugin, work / "CadPlotPlugin.dll")
        (work / "out").mkdir()
        inp = work / "input_0.dwg"
        shutil.copy2(dwg, inp)
        for s in cfg.sibling_files():  # relative xrefs and images must sit next to the copy
            shutil.copy2(s, work / s.name)
        shutil.copy2(dwg, work / "seed.dwg")
        ctb = settings.get("ctb") or ""
        payload = {"block_keywords": cfg.block_keywords, "layer_keywords": [], "output_dir": str(work / "out"),
                   "paper": cfg.paper, "ctb": ctb, "monochrome": ctb.lower() == "monochrome.ctb",
                   "plot_transparency": bool(settings.get("transparency")),
                   "print_lineweights": bool(settings.get("lineweights", True)),
                   "lwdefault": int(settings.get("lwdefault", 25)),
                   "jobs": [{"id": "0", "path": str(inp), "stem": dwg.stem, "origin_dir": str(dwg.parent)}]}
        (work / "batch.json").write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
        (work / "plot.scr").write_text("\n".join([
            '(setq old_secureload (getvar "SECURELOAD"))', '(setvar "SECURELOAD" 0)', "_.NETLOAD",
            (work / "CadPlotPlugin.dll").as_posix(), '(setvar "SECURELOAD" old_secureload)',
            "C3DF-BATCHPLOT", "_.QUIT"]) + "\n", encoding="ascii")
        env = os.environ.copy()
        env["C3DF_PLOT_BATCH_JSON"] = str(work / "batch.json")
        env["C3DF_PLOT_RESULT_JSON"] = str(work / "result.json")
        c = subprocess.run([cfg.acc, "/i", str(work / "seed.dwg"), "/s", str(work / "plot.scr"), "/l", "en-US"],
                           cwd=str(work), env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=3600)
        rj = work / "result.json"
        if not rj.is_file():
            raise SystemExit(f"{dwg.name}: plot produced no result (accoreconsole exit {c.returncode})")
        res = json.loads(rj.read_text(encoding="utf-8-sig"))
        errors = [e for f in res.get("files", []) for e in ([f["error"]] if f.get("error") else []) + f.get("errors", [])]
        if errors:
            raise SystemExit(f"{dwg.name}: plot errors: {errors}")
        cfg.pdf_dir.mkdir(parents=True, exist_ok=True)
        moved = []
        for f in res.get("files", []):
            for pdf in f.get("pdfs", []):
                src, dst = Path(pdf["path"]), cfg.pdf_dir / Path(pdf["path"]).name
                backup(cfg, dst)
                if dst.exists():
                    dst.unlink()
                shutil.move(str(src), str(dst))
                moved.append(dst)
        return moved
    finally:
        shutil.rmtree(work, ignore_errors=True)


def cmd_plot(cfg: Config, args) -> int:
    state_file = cfg.state / "plot-state.json"
    state = json.loads(state_file.read_text(encoding="utf-8")) if state_file.exists() else {}
    sib = {s.name: sha(s) for s in cfg.sibling_files()}
    done, skipped = [], []
    for dwg in cfg.sheets():
        if args.only and not any(dwg.name.startswith(x) for x in args.only):
            continue
        settings = cfg.plot_settings(dwg)
        fp = {"dwg": sha(dwg), "siblings": sib, "settings": settings}
        old = state.get(dwg.name)
        if not args.force and old and old.get("fingerprint") == fp and all(
                (cfg.pdf_dir / n).exists() for n in old.get("pdfs", [])):
            skipped.append(dwg.name)
            continue
        t = time.time()
        pdfs = plot_one(cfg, dwg, settings)
        if not pdfs:
            raise SystemExit(f"{dwg.name}: no title block plotted (block_keywords={cfg.block_keywords})")
        for stale in set((old or {}).get("pdfs", [])) - {p.name for p in pdfs}:
            sp = cfg.pdf_dir / stale
            if sp.exists():
                backup(cfg, sp)
                sp.unlink()
        state[dwg.name] = {"fingerprint": fp, "pdfs": [p.name for p in pdfs], "seconds": round(time.time() - t, 1)}
        state_file.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")
        done.append({"dwg": dwg.name, "pdfs": len(pdfs), "settings": settings})
        print(f"plotted {dwg.name}: {len(pdfs)} pdf, {time.time() - t:.0f} s", flush=True)
    rec = receipt(cfg, "plot", {"plotted": done, "unchanged": skipped})
    print(f"plot: {len(done)} drawing(s) plotted, {len(skipped)} unchanged -> {rec}")
    return 0


# ---------- album ----------
def sheet_pdfs(cfg: Config) -> list[Path]:
    return sorted(cfg.pdf_dir.glob("*.pdf"))


def split_name(p: Path) -> tuple[str, str]:
    number, _, title = p.stem.partition(" ")
    return number, title or number


def cmd_album(cfg: Config, args) -> int:
    import pymupdf
    from pypdf import PdfReader, PdfWriter
    if not cfg.album:
        raise SystemExit("config.album is not set")
    entries = sorted(((*split_name(p), p) for p in sheet_pdfs(cfg)), key=lambda e: sort_key(e[0]))
    numbers = [e[0] for e in entries]
    dup = sorted({n for n in numbers if numbers.count(n) > 1})
    if dup:
        raise SystemExit(f"duplicate sheet numbers: {dup}")
    if cfg.expect and len(entries) != cfg.expect:
        raise SystemExit(f"{len(entries)} PDFs, expected {cfg.expect}")
    w = PdfWriter()
    for i, (number, title, p) in enumerate(entries):
        r = PdfReader(str(p))
        if len(r.pages) != 1:
            raise SystemExit(f"{p.name} has {len(r.pages)} pages, a sheet PDF must have one")
        w.append(r, import_outline=False)
        w.add_outline_item(f"{number} {title}", i)
    backup(cfg, cfg.album)
    cfg.album.parent.mkdir(parents=True, exist_ok=True)
    with open(cfg.album, "wb") as f:
        w.write(f)
    blanks, lines = [], []
    with pymupdf.open(cfg.album) as doc:
        for i, (number, title, _) in enumerate(entries):
            pm = doc[i].get_pixmap(dpi=40, colorspace=pymupdf.csGRAY)
            ink = sum(1 for b in pm.samples if b < 200) / len(pm.samples)
            lines.append(f"{i + 1:>4}  {number}  {title}")
            if ink < cfg.blank_ink:
                blanks.append(f"page {i + 1} {number} (ink {ink:.3%})")
    toc = cfg.album.with_suffix(".toc.txt")
    toc.write_text("\n".join(lines) + "\n\nblank pages: " + ("; ".join(blanks) or "none") + "\n", encoding="utf-8-sig")
    rec = receipt(cfg, "album", {"album": str(cfg.album), "pages": len(entries), "blank": blanks, "toc": str(toc)})
    print(f"album: {len(entries)} pages -> {cfg.album}; blank: {blanks or 'none'}; receipt {rec}")
    return 1 if blanks else 0


# ---------- catalog ----------
def expand_range(number: str) -> list[str]:
    """'X-SG-1003~1004' (also with the full-width tilde) -> both numbers."""
    m = re.fullmatch(r"(.*?)(\d+)\s*[~\uff5e]\s*(\d+)", number.strip())
    if not m:
        return [number.strip()]
    head, a, b = m.group(1), int(m.group(2)), int(m.group(3))
    width = len(m.group(2))
    return [f"{head}{n:0{width}d}" for n in range(a, b + 1)]


def sheet_rows(cfg: Config, blocks: list[dict]) -> list[tuple]:
    rows = [(b["attrs"].get(cfg.tag_number, "").strip(), b["attrs"].get(cfg.tag_title, "").strip(),
             b["attrs"].get(cfg.tag_scale, "").strip() if cfg.tag_scale else "") for b in blocks]
    return sorted(rows, key=lambda r: sort_key(r[0]))


def cmd_catalog(cfg: Config, args) -> int:
    import openpyxl
    from openpyxl.styles import Alignment, Border, Font, Side
    rows = sheet_rows(cfg, read_title_blocks(cfg))
    if args.check:
        wb = openpyxl.load_workbook(args.check, data_only=True)
        listed: dict[str, list[str]] = {}
        for r in wb.active.iter_rows(values_only=True):
            cells = [str(c).strip() if c is not None else "" for c in r]
            num = next((c for c in cells if any(ch.isdigit() for ch in c) and ("-" in c or "~" in c or "\uff5e" in c)), None)
            if not num:
                continue
            title = next((c for c in cells if c and c != num and not c.isdigit()), "")
            for n in expand_range(num):
                listed.setdefault(n, []).append(title)
        drawn = {}
        for n, t, _ in rows:
            drawn.setdefault(n, []).append(t)
        report = {
            "listed_not_drawn": sorted(set(listed) - set(drawn), key=sort_key),
            "drawn_not_listed": sorted(set(drawn) - set(listed), key=sort_key),
            "title_mismatch": [{"number": n, "catalogue": listed[n][0], "drawing": drawn[n][0]}
                               for n in sorted(set(listed) & set(drawn), key=sort_key)
                               if listed[n][0].replace(" ", "") != drawn[n][0].replace(" ", "")],
            "duplicate_in_catalogue": sorted([n for n, v in listed.items() if len(v) > 1], key=sort_key),
            "duplicate_in_drawings": sorted([n for n, v in drawn.items() if len(v) > 1], key=sort_key),
            "title_blocks": len(rows), "catalogue_entries": sum(len(v) for v in listed.values()),
        }
        rec = receipt(cfg, "catalog-check", report)
        bad = sum(len(v) for k, v in report.items() if isinstance(v, list))
        print(json.dumps({k: v for k, v in report.items()}, ensure_ascii=False, indent=2))
        print(f"catalog check: {bad} finding(s) -> {rec}")
        return 1 if bad else 0
    if not cfg.catalog:
        raise SystemExit("config.catalog is not set")
    wb = openpyxl.Workbook()
    ws = wb.active
    ws.title = "Catalogue"
    ws.append([cfg.catalog_title])
    ws.merge_cells(start_row=1, start_column=1, end_row=1, end_column=4)
    ws.append(cfg.catalog_headers)
    for i, (n, t, s) in enumerate(rows, 1):
        ws.append([i, n, t, s])
    thin = Side(style="thin")
    for c, wd in zip("ABCD", (7, 20, 46, 11)):
        ws.column_dimensions[c].width = wd
    ws["A1"].font = Font(bold=True, size=14)
    ws["A1"].alignment = Alignment(horizontal="center")
    for row in ws.iter_rows(min_row=2):
        for c in row:
            c.border = Border(left=thin, right=thin, top=thin, bottom=thin)
            c.alignment = Alignment(horizontal="left" if c.column == 3 else "center", vertical="center")
    backup(cfg, cfg.catalog)
    cfg.catalog.parent.mkdir(parents=True, exist_ok=True)
    wb.save(cfg.catalog)
    rec = receipt(cfg, "catalog", {"catalog": str(cfg.catalog), "rows": len(rows)})
    print(f"catalog: {len(rows)} rows -> {cfg.catalog}; receipt {rec}")
    return 0


# ---------- verify ----------
def cmd_verify(cfg: Config, args) -> int:
    import pymupdf
    problems: list[str] = []
    blocks = read_title_blocks(cfg)
    rows = sheet_rows(cfg, blocks)
    numbers = [r[0] for r in rows]
    if any(not n for n in numbers):
        problems.append(f"{numbers.count('')} title block(s) without {cfg.tag_number}")
    dup = sorted({n for n in numbers if numbers.count(n) > 1})
    if dup:
        problems.append(f"duplicate sheet numbers: {dup}")
    if cfg.expect and len(rows) != cfg.expect:
        problems.append(f"{len(rows)} title blocks, expected {cfg.expect}")
    pdfs = {p.stem: p for p in sheet_pdfs(cfg)}
    want = {sanitize(f"{n} {t}") for n, t, _ in rows}
    if set(pdfs) != want:
        problems.append(f"PDF names differ from title blocks: missing {sorted(want - set(pdfs))[:10]}, "
                        f"extra {sorted(set(pdfs) - want)[:10]}")
    newest_dwg = {}
    for b in blocks:
        f = cfg.drawings / b["file"]
        newest_dwg[sanitize(f"{b['attrs'].get(cfg.tag_number, '')} {b['attrs'].get(cfg.tag_title, '')}")] = f.stat().st_mtime
    renders = {}
    for name, p in pdfs.items():
        with pymupdf.open(p) as d:
            if len(d) != 1:
                problems.append(f"{p.name}: {len(d)} pages")
                continue
            r = d[0].rect
            if abs(r.width - cfg.page_pt[0]) > 8 or abs(r.height - cfg.page_pt[1]) > 8:
                problems.append(f"{p.name}: page {r.width:.0f}x{r.height:.0f} pt, expected {cfg.page_pt}")
            pm = d[0].get_pixmap(dpi=24, colorspace=pymupdf.csGRAY)
            if sum(x < 200 for x in pm.samples) / len(pm.samples) < cfg.blank_ink:
                problems.append(f"{p.name}: blank page")
            if cfg.album and cfg.album.exists():
                renders[name] = hashlib.sha256(d[0].get_pixmap(dpi=96, colorspace=pymupdf.csGRAY).samples).hexdigest()
        if name in newest_dwg and p.stat().st_mtime < newest_dwg[name]:
            problems.append(f"{p.name}: PDF older than its DWG (re-plot)")
    album_pages = None
    if cfg.album and cfg.album.exists():
        order = sorted(pdfs, key=lambda n: sort_key(n.partition(" ")[0]))
        with pymupdf.open(cfg.album) as d:
            album_pages = len(d)
            if len(d) != len(pdfs) or len(d.get_toc()) != len(pdfs):
                problems.append(f"album: {len(d)} pages / {len(d.get_toc())} bookmarks, {len(pdfs)} PDFs")
            else:
                for i, name in enumerate(order):
                    h = hashlib.sha256(d[i].get_pixmap(dpi=96, colorspace=pymupdf.csGRAY).samples).hexdigest()
                    if h != renders.get(name):
                        problems.append(f"album page {i + 1} differs from {name}.pdf")
    catalog_rows = None
    if cfg.catalog and cfg.catalog.exists():
        import openpyxl
        ws = openpyxl.load_workbook(cfg.catalog, data_only=True).active
        cat = [tuple("" if c is None else str(c).strip() for c in r[1:4]) for r in ws.iter_rows(min_row=3, values_only=True) if r[1]]
        catalog_rows = len(cat)
        if cat != [tuple(r) for r in rows]:
            problems.append("catalogue rows differ from title blocks (re-run catalog)")
    if args.reference:
        ref = json.loads(Path(args.reference).read_text(encoding="utf-8-sig"))
        old = {(f["relative_path"], b.get("block_handle")): {a["tag"]: a["value"] for a in b["attributes"]}
               for f in ref["files"] for b in f["blocks"]}
        new = {(b["file"], b["handle"]): b["attrs"] for b in blocks}
        if set(old) != set(new):
            problems.append(f"title block set differs from reference: {len(set(old) ^ set(new))} instance(s)")
        allowed = set(args.allow_tags or [])
        changed = [(k, t) for k in set(old) & set(new) for t in set(old[k]) | set(new[k])
                   if old[k].get(t) != new[k].get(t) and t not in allowed]
        if changed:
            problems.append(f"{len(changed)} attribute(s) changed outside {sorted(allowed)}: {changed[:5]}")
    files = [p for p in cfg.drawings.iterdir() if p.is_file()] + list(pdfs.values())
    files += [x for x in (cfg.album, cfg.catalog) if x and x.exists()]
    report = {"title_blocks": len(rows), "pdfs": len(pdfs), "album_pages": album_pages, "catalog_rows": catalog_rows,
              "problems": problems, "sha256": {str(p): sha(p) for p in files}}
    rec = receipt(cfg, "verify", report)
    print(json.dumps({k: v for k, v in report.items() if k != "sha256"}, ensure_ascii=False, indent=2))
    print(f"verify: {'PASS' if not problems else f'{len(problems)} problem(s)'} -> {rec}")
    return 1 if problems else 0


# ---------- compare ----------
def cmd_compare(cfg: Config, args) -> int:
    import numpy as np
    import pymupdf
    ref_dir = Path(args.reference_dir)
    rows = []
    for p in sheet_pdfs(cfg):
        q = ref_dir / p.name
        if not q.exists():
            rows.append({"sheet": p.name, "result": "no reference page"})
            continue
        with pymupdf.open(p) as a, pymupdf.open(q) as b:
            pa = a[0].get_pixmap(dpi=args.dpi, colorspace=pymupdf.csGRAY)
            pb = b[0].get_pixmap(dpi=args.dpi, colorspace=pymupdf.csGRAY)
        if (pa.width, pa.height) != (pb.width, pb.height):
            rows.append({"sheet": p.name, "result": "page size differs"})
            continue
        x = np.frombuffer(pa.samples, np.uint8).reshape(pa.height, pa.width) < 200
        y = np.frombuffer(pb.samples, np.uint8).reshape(pb.height, pb.width) < 200
        diff = x ^ y
        n = int(diff.sum())
        row = {"sheet": p.name, "diff_pixels": n, "ink_new": round(float(x.mean()), 5), "ink_ref": round(float(y.mean()), 5)}
        if n:
            ys, xs = np.nonzero(diff)
            row["diff_box"] = [round(xs.min() / pa.width, 3), round(ys.min() / pa.height, 3),
                               round(xs.max() / pa.width, 3), round(ys.max() / pa.height, 3)]
            best = max(float((np.roll(np.roll(x, dy, 0), dx, 1) & y).sum()) / max(1, int(y.sum()))
                       for dx in (-1, 0, 1) for dy in (-1, 0, 1))
            row["overlap_within_1px"] = round(best, 4)
        rows.append(row)
    rec = receipt(cfg, "compare", {"reference": str(ref_dir), "dpi": args.dpi, "pages": rows})
    for r in rows:
        print(json.dumps(r, ensure_ascii=False))
    print(f"compare: {len(rows)} page(s), {sum(1 for r in rows if r.get('diff_pixels'))} with differences -> {rec}")
    return 0


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[attr-defined]
    except Exception:  # noqa: BLE001
        pass
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("config")
    sub = ap.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("plot")
    s.add_argument("--only", nargs="*")
    s.add_argument("--force", action="store_true")
    s = sub.add_parser("verify")
    s.add_argument("--reference")
    s.add_argument("--allow-tags", nargs="*")
    sub.add_parser("album")
    s = sub.add_parser("catalog")
    s.add_argument("--check")
    s = sub.add_parser("compare")
    s.add_argument("reference_dir")
    s.add_argument("--dpi", type=int, default=120)
    args = ap.parse_args()
    cfg = Config(Path(args.config))
    return {"plot": cmd_plot, "verify": cmd_verify, "album": cmd_album, "catalog": cmd_catalog,
            "compare": cmd_compare}[args.cmd](cfg, args)


if __name__ == "__main__":
    sys.exit(main())
