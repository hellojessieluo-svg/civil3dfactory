# -*- coding: utf-8 -*-
"""Desktop tool: batch-plot DWG title blocks to PDF.

Pick a folder (or DWG files) -> every title block (block name containing "TITLE" by
default) is plotted by its bounding box to one monochrome A3 PDF -> done.

Two engines: a hidden background AutoCAD/Civil 3D instance driven over COM (proven,
supports OLE tables), or accoreconsole with the bundled CadPlotPlugin (one process for
the whole batch, fast, no dialogs). The command line always uses accoreconsole.
"""

from __future__ import annotations

import os
import re
import sys
import json
import time
import shutil
import datetime
import tempfile
import subprocess
import traceback
from pathlib import Path
from typing import Any, Callable, Iterable

# Command-line mode logs with print(); on Windows stdout defaults to a legacy code page
# that cannot encode every character and would crash. Force UTF-8. In GUI (--windowed)
# mode stdout may be None, so check each stream before touching it.
if sys.platform == "win32":
    try:
        os.system("chcp 65001 >nul 2>&1")
    except Exception:
        pass
for _std_name in ("stdout", "stderr"):
    _std = getattr(sys, _std_name, None)
    if _std is not None:
        try:
            _std.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass

from PySide6.QtCore import QObject, QRunnable, QThreadPool, Qt, Signal
from PySide6.QtGui import QIcon
from PySide6.QtWidgets import (
    QApplication,
    QCheckBox,
    QComboBox,
    QFileDialog,
    QFrame,
    QGridLayout,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QMainWindow,
    QMessageBox,
    QProgressBar,
    QPushButton,
    QTextEdit,
    QToolButton,
    QVBoxLayout,
    QWidget,
)


APP_NAME = "DWG Title Block Plotter"
TITLE_BLOCK_FILTER = "TITLE"         # block name (EffectiveName) containing this substring (case-insensitive) is a title block
POLYLINE_OBJECT_NAMES = {"AcDbPolyline", "AcDb2dPolyline", "AcDb3dPolyline"}
PLOTTER = "DWG To PDF.pc3"
CTB_MONO = "monochrome.ctb"
CTB_COLOR = ""                       # empty = no style table (plot in layer colours)
BACKUP_DIR_NAME = ".dwg-attribute-backups"   # same as the attribute editor; skipped when found
STYLE_COLOR_LABEL = "(color, no style table)"
ACCORE_YEARS = ("2027", "2026", "2025", "2024", "2023", "2022")   # newest first

# AutoCAD COM enum constants
AC_WINDOW = 4
AC_SCALE_TO_FIT = 0
AC_90 = 1
AC_0 = 0

# Attribute tags for sheet title / sheet number, in priority order; the file name is
# built as "<number> <title>" and falls back to "<dwg stem>_<layout>" when none match.
TUMING_TAGS = ("SHEET_TITLE", "TITLE", "DWGNAME")
TUHAO_TAGS = ("SHEET_NO", "SHEETNO", "NUMBER", "DWGNO")


# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------
def sanitize(name: str) -> str:
    return re.sub(r'[\\/:*?"<>|]', "_", name).strip()


def parse_keywords(text: str) -> list[str]:
    """Comma-separated keywords -> list (ASCII comma, fullwidth comma U+FF0C tolerated; blanks dropped)."""
    return [part.strip() for part in re.split("[,\uff0c]", text or "") if part.strip()]


def matches_any(name: str, keywords: list[str]) -> bool:
    folded = name.casefold()
    return any(keyword.casefold() in folded for keyword in keywords)


def safe_get(obj: Any, attr: str, default: Any = "") -> Any:
    try:
        return getattr(obj, attr)
    except Exception:
        return default


def effective_name(ref: Any) -> str:
    return str(safe_get(ref, "EffectiveName") or safe_get(ref, "Name"))


def iter_com(collection: Any) -> Iterable[Any]:
    """COM collections occasionally fail to enumerate; fall back to Item(index)."""
    try:
        yield from collection
        return
    except Exception:
        pass
    count = int(collection.Count)
    for index in range(count):
        yield collection.Item(index)


def discover_dwgs(folder: Path, recursive: bool) -> list[Path]:
    pattern = "**/*.dwg" if recursive else "*.dwg"
    files: list[Path] = []
    for path in folder.glob(pattern):
        if not path.is_file() or BACKUP_DIR_NAME in path.parts:
            continue
        files.append(path.resolve())
    return sorted(files, key=lambda p: str(p).casefold())


def resolve_inputs(entries: Iterable[str], recursive: bool) -> tuple[list[Path], Path]:
    """Resolve the GUI inputs (hand-picked DWG files, or whole folders) into
    (DWG list, base folder). The base folder drives relative paths in the log and the default output location."""
    dwgs: list[Path] = []
    dir_inputs: list[Path] = []
    file_inputs: list[Path] = []
    for raw in entries:
        text = (raw or "").strip().strip('"')
        if not text:
            continue
        path = Path(text).expanduser()
        if path.is_dir():
            dir_inputs.append(path)
            dwgs.extend(discover_dwgs(path, recursive))
        elif path.is_file() and path.suffix.lower() == ".dwg":
            file_inputs.append(path.resolve())
    dwgs.extend(file_inputs)

    seen: set[str] = set()
    ordered: list[Path] = []
    for path in dwgs:
        key = str(path).casefold()
        if key not in seen:
            seen.add(key)
            ordered.append(path)
    if not ordered:
        raise RuntimeError("No DWG files selected. Pick .dwg files (multi-select) or enter a folder.")

    if dir_inputs and not file_inputs and len(dir_inputs) == 1:
        base = dir_inputs[0].resolve()
    else:
        base = ordered[0].parent
    return ordered, base


def rel_display(path: Path, base: Path) -> str:
    """Prefer a relative path in the log; fall back to the file name across drives or outside the base."""
    try:
        return path.relative_to(base).as_posix()
    except ValueError:
        return path.name


def resolve_ctb(style_text: str) -> str:
    """Turn the 'Plot style table' combo text into a style sheet name AutoCAD accepts.
    Colour / empty -> empty string (no style table, layer colours).
    A full path to a .ctb/.stb file -> its file name; AutoCAD looks it up in the plot style search path."""
    text = (style_text or "").strip().strip('"')
    if not text or text == STYLE_COLOR_LABEL or text.lower() in ("color", "colour", "none"):
        return ""
    if any(sep in text for sep in ("\\", "/")):
        text = Path(text).name
    return text


def unique_path(path: Path) -> Path:
    """Append _2 / _3 ... when the output name already exists so nothing is overwritten."""
    if not path.exists():
        return path
    stem, suffix, parent = path.stem, path.suffix, path.parent
    index = 2
    while True:
        candidate = parent / f"{stem}_{index}{suffix}"
        if not candidate.exists():
            return candidate
        index += 1


def resolve_out_path(path: Path, used: set[str], overwrite: bool) -> Path:
    """Decide the final path of one PDF.
    overwrite=False: an existing file on disk gets _2/_3 appended (legacy behaviour).
    overwrite=True : replace existing files on disk, but still avoid names produced
                     earlier in this run so two different sheets never clobber each other."""
    if not overwrite:
        chosen = unique_path(path)
        used.add(str(chosen).casefold())
        return chosen
    chosen = path
    if str(chosen).casefold() in used:
        stem, suffix, parent = path.stem, path.suffix, path.parent
        index = 2
        while str(parent / f"{stem}_{index}{suffix}").casefold() in used:
            index += 1
        chosen = parent / f"{stem}_{index}{suffix}"
    used.add(str(chosen).casefold())
    return chosen


# ---------------------------------------------------------------------------
# COM session: hidden background instance (DispatchEx starts a separate process and
# never touches the CAD the user has open)
# ---------------------------------------------------------------------------
class AcadHiddenSession:
    def __init__(self, visible: bool, log: Callable[[str], None]):
        self.visible = visible
        self.log = log
        self.acad = None
        self.pythoncom = None
        self.VARIANT = None

    def __enter__(self) -> "AcadHiddenSession":
        try:
            import pythoncom
            import win32com.client
            from win32com.client import VARIANT
        except ImportError as exc:
            raise RuntimeError(
                "pywin32 is missing. Use the bundled EXE or run: pip install pywin32"
            ) from exc

        self.pythoncom = pythoncom
        self.VARIANT = VARIANT
        pythoncom.CoInitialize()
        t0 = time.time()
        self.log("Starting a background AutoCAD/Civil 3D instance... (a cold start can take 10-30 s)")
        try:
            # DispatchEx = new process; do not use Dispatch/GetActiveObject, which would hijack an open CAD
            self.acad = win32com.client.DispatchEx("AutoCAD.Application")
            self.acad.Visible = bool(self.visible)
            _ = self.acad.Name
        except Exception as exc:
            pythoncom.CoUninitialize()
            self.pythoncom = None
            raise RuntimeError(f"Could not start an AutoCAD/Civil 3D instance: {exc}") from exc
        self.log(f"Instance ready: {self.acad.Name} ({time.time() - t0:.0f}s)")
        self._warmup()
        return self

    def _warmup(self) -> None:
        """Touch the collections of the start-up blank drawing (Drawing1) so the document
        subsystem is warm before the first real DWG (avoids cold-start failures). Reuse the
        existing blank drawing instead of Documents.Add(); a second one would trigger a
        "save Drawing2?" prompt on exit."""
        seed = safe_get(self.acad, "ActiveDocument", None)
        deadline = time.time() + 30
        while time.time() < deadline:
            try:
                if seed is None:
                    if int(self.acad.Documents.Count) > 0:
                        seed = self.acad.Documents.Item(0)
                    else:
                        seed = self.acad.Documents.Add()
                _ = int(seed.Layouts.Count)
                for lay in seed.Layouts:
                    _ = lay.Name
                _ = int(self.acad.Documents.Count)
                self.log("Background instance warmed up")
                return
            except Exception:
                time.sleep(0.5)
                seed = safe_get(self.acad, "ActiveDocument", None)
        self.log("Warm-up timed out; plotting anyway")

    def pt2(self, x: float, y: float) -> Any:
        return self.VARIANT(self.pythoncom.VT_ARRAY | self.pythoncom.VT_R8, [x, y])

    def open_document(self, path: Path) -> Any:
        last_error = None
        for attempt in range(8):
            try:
                # Read-only: plotting does not write, and this avoids clashing with the same drawing open in the foreground
                return self.acad.Documents.Open(str(path), True)
            except Exception as exc:
                last_error = exc
                time.sleep(0.75 + attempt * 0.25)
        raise RuntimeError(f"AutoCAD could not open: {path}\n{last_error}")

    def wait_doc_ready(self, opened_doc: Any, timeout: int = 90) -> Any:
        """The document object may not be ready right after Open; wait until Name/Layout are readable."""
        deadline = time.time() + timeout
        last_error = None
        while time.time() < deadline:
            for candidate in (opened_doc, safe_get(self.acad, "ActiveDocument", None)):
                if candidate is None:
                    continue
                try:
                    _ = candidate.Name
                    _ = candidate.ActiveLayout.Name
                    return candidate
                except Exception as exc:
                    last_error = exc
            time.sleep(1)
        raise RuntimeError(f"DWG opened but the document object never became ready: {last_error!r}")

    def __exit__(self, exc_type: Any, exc: Any, tb: Any) -> None:
        try:
            if self.acad is not None:
                # Close every document without saving before quitting to avoid "save DrawingN?" prompts
                try:
                    while int(self.acad.Documents.Count) > 0:
                        self.acad.Documents.Item(0).Close(False)
                except Exception:
                    pass
                self.acad.Quit()
        except Exception:
            pass
        self.acad = None
        if self.pythoncom is not None:
            self.pythoncom.CoUninitialize()
            self.pythoncom = None


# ---------------------------------------------------------------------------
# One title block -> one PDF
# ---------------------------------------------------------------------------
def read_titleblock_name(block: Any) -> tuple[str, str]:
    """Read sheet number / sheet title from the title block attributes; empty strings when absent."""
    tuming = tuhao = ""
    try:
        attrs = list(block.GetAttributes())
    except Exception:
        return "", ""
    tags = {str(safe_get(att, "TagString")).upper(): str(safe_get(att, "TextString")) for att in attrs}
    for tag in TUMING_TAGS:
        if tag in tags and tags[tag].strip():
            tuming = tags[tag].strip()
            break
    for tag in TUHAO_TAGS:
        if tag in tags and tags[tag].strip():
            tuhao = tags[tag].strip()
            break
    return tuhao, tuming


def iter_spaces(doc: Any) -> Iterable[tuple[str, Any, Any]]:
    """Yield (layout name, layout object, block table record of that layout), model space included."""
    for layout in iter_com(doc.Layouts):
        name = str(safe_get(layout, "Name"))
        if name.casefold() == "model":
            yield name, layout, doc.ModelSpace
        else:
            yield name, layout, layout.Block


def find_titleblocks(space: Any, block_keywords: list[str]) -> list[Any]:
    found: list[Any] = []
    if not block_keywords:
        return found
    for ent in iter_com(space):
        if safe_get(ent, "ObjectName") != "AcDbBlockReference":
            continue
        # Attributes are not required: plain blocks count as frames; attributes only affect
        # the PDF name (fallback: <dwg stem>_<layout>)
        if not matches_any(effective_name(ent), block_keywords):
            continue
        found.append(ent)
    return found


def find_layer_frames(space: Any, layer_keywords: list[str]) -> list[Any]:
    """Closed-polyline frames: closed polylines whose layer name contains any keyword are plotted by their bounding box."""
    found: list[Any] = []
    if not layer_keywords:
        return found
    for ent in iter_com(space):
        if safe_get(ent, "ObjectName") not in POLYLINE_OBJECT_NAMES:
            continue
        if not bool(safe_get(ent, "Closed", False)):
            continue
        if not matches_any(str(safe_get(ent, "Layer")), layer_keywords):
            continue
        found.append(ent)
    return found


def configure_layout_device(doc: Any, ctb: str, paper_hint: str) -> None:
    """Configure plotter / paper / pen style / scale on the active layout. These are layout-level
    settings, so one call per layout is enough even with several title blocks on it."""
    doc.SetVariable("BACKGROUNDPLOT", 0)
    active = doc.ActiveLayout
    active.ConfigName = PLOTTER
    try:
        active.RefreshPlotDeviceInfo()
    except Exception:
        pass

    # Paper: prefer a media name containing paper_hint (A3/A4), full_bleed when available
    chosen = None
    for media in active.GetCanonicalMediaNames():
        if paper_hint in media:
            chosen = media
            if "full_bleed" in media.lower():
                break
    if chosen:
        active.CanonicalMediaName = chosen

    # Monochrome: PlotWithPlotStyles must be enabled before assigning StyleSheet or the ctb is ignored
    if ctb:
        active.PlotWithPlotStyles = True
        active.StyleSheet = ctb
    else:
        active.PlotWithPlotStyles = False

    # Always ignore object/layer lineweights. The COM property is PlotWithLineweights; also
    # disable ScaleLineweights so a stale "scale lineweights" layout setting cannot leak into the PDF.
    active.PlotWithLineweights = False
    try:
        active.ScaleLineweights = False
    except Exception:
        pass

    active.UseStandardScale = True
    active.StandardScale = AC_SCALE_TO_FIT
    active.CenterPlot = True

    try:
        doc.SetVariable("PLOTTRANSPARENCYOVERRIDE", 1)  # 1 = do not plot transparency (solid fills)
    except Exception:
        try:
            doc.SendCommand("PLOTTRANSPARENCYOVERRIDE\n1\n")
        except Exception:
            pass


def plot_block_window(
    session: AcadHiddenSession,
    doc: Any,
    block: Any,
    out_path: Path,
    log: Callable[[str], None],
) -> bool:
    """Set the plot window to one frame's bounding box and plot a PDF (device configured by configure_layout_device)."""
    active = doc.ActiveLayout
    mn, mx = block.GetBoundingBox()
    active.SetWindowToPlot(session.pt2(mn[0], mn[1]), session.pt2(mx[0], mx[1]))
    active.PlotType = AC_WINDOW
    active.PlotRotation = AC_90 if (mx[0] - mn[0]) >= (mx[1] - mn[1]) else AC_0

    ok = doc.Plot.PlotToFile(str(out_path))
    if ok and out_path.exists():
        log(f"    OK   {out_path.name} ({out_path.stat().st_size} bytes)")
        return True
    log(f"    FAIL {out_path.name} was not produced")
    return False


# ---------------------------------------------------------------------------
# Main flow: folder -> batch plot
# ---------------------------------------------------------------------------
def _process_dwg(
    session: AcadHiddenSession,
    path: Path,
    output_dir: Path,
    block_keywords: list[str],
    layer_keywords: list[str],
    ctb: str,
    paper_hint: str,
    overwrite: bool,
    used: set[str],
    log: Callable[[str], None],
) -> tuple[int, bool, str | None]:
    """Process one DWG; returns (pdf count, whether any frame was found, error message or None)."""
    doc = None
    made = 0
    found_any = False
    try:
        opened = session.open_document(path)
        doc = session.wait_doc_ready(opened)
        for layout_name, layout, space in iter_spaces(doc):
            blocks = find_titleblocks(space, block_keywords)
            frames = find_layer_frames(space, layer_keywords)
            if not blocks and not frames:
                continue
            doc.ActiveLayout = layout
            configure_layout_device(doc, ctb, paper_hint)  # configure the plotter once per layout
            for block in blocks:
                found_any = True
                tuhao, tuming = read_titleblock_name(block)
                base = sanitize((tuhao + " " + tuming).strip())
                if not base:
                    base = sanitize(f"{path.stem}_{layout_name}")
                out_path = resolve_out_path(output_dir / (base + ".pdf"), used, overwrite)
                try:
                    if plot_block_window(session, doc, block, out_path, log):
                        made += 1
                except Exception as exc:
                    return made, found_any, f"Plot failed: {exc}"
            for frame in frames:
                found_any = True
                base = sanitize(f"{path.stem}_{layout_name}")
                out_path = resolve_out_path(output_dir / (base + ".pdf"), used, overwrite)
                try:
                    if plot_block_window(session, doc, frame, out_path, log):
                        made += 1
                except Exception as exc:
                    return made, found_any, f"Plot failed: {exc}"
        return made, found_any, None
    except Exception as exc:
        return made, found_any, str(exc)
    finally:
        if doc is not None:
            try:
                doc.Close(False)
            except Exception:
                pass


def plot_folder(
    dwgs: list[Path],
    base: Path,
    output_dir: Path,
    block_keywords: list[str],
    layer_keywords: list[str],
    ctb: str,
    paper_hint: str,
    visible: bool,
    overwrite: bool,
    log: Callable[[str], None],
) -> dict[str, Any]:
    if not dwgs:
        raise RuntimeError("No DWG files to process.")

    output_dir.mkdir(parents=True, exist_ok=True)
    log(f"{len(dwgs)} DWG(s), output folder: {output_dir}")
    log(f"Plot style table: {ctb or '(color, layer colours)'}")
    log("Existing PDF names: overwrite" if overwrite else "Existing PDF names: append _2/_3 and keep the old file")

    dwg_count = 0
    pdf_count = 0
    errors: list[dict[str, str]] = []
    used: set[str] = set()   # output names produced in this run, to avoid clobbering within the batch

    with AcadHiddenSession(visible, log) as session:
        for index, path in enumerate(dwgs, 1):
            rel = rel_display(path, base)
            log(f"[{index}/{len(dwgs)}] {rel}")
            made, found_any, err = _process_dwg(session, path, output_dir, block_keywords, layer_keywords, ctb, paper_hint, overwrite, used, log)
            # Transient cold-start failure (e.g. <unknown>.Count on the first drawing): retry once
            if err and made == 0 and not found_any:
                log("    First read failed; retrying once...")
                time.sleep(1.5)
                made, found_any, err = _process_dwg(session, path, output_dir, block_keywords, layer_keywords, ctb, paper_hint, overwrite, used, log)
            pdf_count += made
            if found_any:
                dwg_count += 1
            if err:
                errors.append({"file": rel, "error": err})
                log(f"    FAIL {err}")
            elif not found_any:
                log("    (no matching title block / closed frame, skipped)")

    return {
        "dwg_total": len(dwgs),
        "dwg_plotted": dwg_count,
        "pdf_count": pdf_count,
        "error_count": len(errors),
        "output_dir": str(output_dir),
    }


# ---------------------------------------------------------------------------
# Engine 2: accoreconsole (headless core) + CadPlotPlugin (one process per batch, fast, no dialogs)
# ---------------------------------------------------------------------------
def find_core_console() -> Path:
    """Locate accoreconsole.exe: C3DF_ACCORECONSOLE first, then AutoCAD 2027..2022 (newest first)."""
    configured = os.environ.get("C3DF_ACCORECONSOLE", "").strip()
    candidates: list[Path] = []
    if configured:
        candidates.append(Path(configured))
    for year in ACCORE_YEARS:
        candidates.append(Path(rf"C:\Program Files\Autodesk\AutoCAD {year}\accoreconsole.exe"))
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    raise RuntimeError(
        "accoreconsole.exe not found. Install Civil 3D / AutoCAD 2022-2027 "
        "or set C3DF_ACCORECONSOLE to its full path."
    )


def bundled_plugin_path() -> Path:
    if getattr(sys, "frozen", False):
        base = Path(getattr(sys, "_MEIPASS"))
        candidate = base / "CadPlotPlugin.dll"
    else:
        candidate = Path(__file__).resolve().parent / "CadPlotPlugin" / "bin" / "hotload" / "CadPlotPlugin.dll"
    if not candidate.is_file():
        raise RuntimeError(f"Headless plot plugin is missing: {candidate}")
    return candidate


def core_output_text(raw: bytes) -> str:
    if not raw:
        return ""
    if b"\x00" in raw[:200]:
        return raw.decode("utf-16-le", errors="replace")
    return raw.decode("utf-8", errors="replace")


def plot_folder_accore(
    dwgs: list[Path],
    base: Path,
    output_dir: Path,
    block_keywords: list[str],
    layer_keywords: list[str],
    ctb: str,
    paper_hint: str,
    overwrite: bool,
    log: Callable[[str], None],
    plot_transparency: bool = False,
    print_lineweights: bool = False,
    lwdefault: int = 0,
) -> dict[str, Any]:
    if not dwgs:
        raise RuntimeError("No DWG files to process.")
    output_dir.mkdir(parents=True, exist_ok=True)
    log(f"{len(dwgs)} DWG(s); starting the headless core for batch plotting... (first engine load takes a moment)")
    log(f"Plot style table: {ctb or '(color, layer colours)'}")
    log("Plot transparency: on (transparent objects stay transparent)" if plot_transparency else "Plot transparency: off (transparent objects print solid)")
    if print_lineweights:
        log(f"Plot lineweights: on, default lineweight {lwdefault/100:.2f} mm (thin text prints solid)" if lwdefault > 0 else "Plot lineweights: on (object lineweights)")
    else:
        log("Plot lineweights: off (thin single-stroke text may print faint)")
    log("Existing PDF names: overwrite" if overwrite else "Existing PDF names: append _2/_3 and keep the old file")

    with tempfile.TemporaryDirectory(prefix="c3df_plot_", ignore_cleanup_errors=True) as temporary:
        workdir = Path(temporary)
        plugin_copy = workdir / "CadPlotPlugin.dll"
        shutil.copy2(bundled_plugin_path(), plugin_copy)
        out_tmp = workdir / "out"
        out_tmp.mkdir()

        # Copy each drawing to an ASCII-named temp file to dodge non-ASCII path issues in the
        # core; the original stem is passed along for the naming fallback.
        jobs = []
        for index, path in enumerate(dwgs):
            input_dwg = workdir / f"input_{index}.dwg"
            shutil.copy2(path, input_dwg)
            # origin_dir lets the plugin rebuild relative xref paths: the copy lives in a temp
            # folder, so a stored .\xrefs\x.dwg only resolves against the original folder.
            jobs.append({"id": str(index), "path": str(input_dwg), "stem": path.stem,
                         "origin_dir": str(path.parent)})

        payload = {
            "block_keywords": block_keywords,
            "layer_keywords": layer_keywords,
            "output_dir": str(out_tmp),
            "paper": paper_hint,
            "ctb": ctb,                          # preferred by the current plugin; empty = colour
            "monochrome": ctb.lower() == "monochrome.ctb",  # fallback field for older plugins
            "plot_transparency": plot_transparency,  # plot transparency switch (default off)
            "print_lineweights": print_lineweights,  # plot lineweights switch (default off)
            "lwdefault": lwdefault,                  # > 0 sets LWDEFAULT (hundredths of mm)
            "jobs": jobs,
        }
        batch_json = workdir / "batch.json"
        result_json = workdir / "result.json"
        batch_json.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")

        # A separate seed.dwg starts the core so the first drawing is not both the start-up
        # file and a side-read database at the same time (file-lock conflict).
        seed = workdir / "seed.dwg"
        shutil.copy2(dwgs[0], seed)

        script = workdir / "plot.scr"
        # /loadmodule does not reliably register managed .NET commands on all
        # AutoCAD/Civil 3D installations. NETLOAD is reliable, but the DLL
        # lives in a random temp folder. Preserve SECURELOAD's exact original
        # value, lower it only for NETLOAD, and restore it before plugin code runs.
        lines = [
            '(setq c3df_old_secureload (getvar "SECURELOAD"))',
            '(setvar "SECURELOAD" 0)',
            "_.NETLOAD",
            plugin_copy.as_posix(),
            '(setvar "SECURELOAD" c3df_old_secureload)',
            "C3DF-BATCHPLOT",
            "_.QUIT",
        ]
        script.write_text("\n".join(lines) + "\n", encoding="ascii")

        env = os.environ.copy()
        env["C3DF_PLOT_BATCH_JSON"] = str(batch_json)
        env["C3DF_PLOT_RESULT_JSON"] = str(result_json)
        flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        try:
            completed = subprocess.run(
                [
                    str(find_core_console()),
                    "/i", str(seed),
                    "/s", str(script), "/l", "en-US",
                ],
                cwd=str(workdir), env=env,
                stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                timeout=3600, creationflags=flags, check=False,
            )
        except subprocess.TimeoutExpired as exc:
            raise RuntimeError("Headless core timed out (60 minutes).") from exc
        output = core_output_text(completed.stdout)
        time.sleep(0.5)  # after exit, Windows occasionally keeps the temp folder handle briefly

        if not result_json.is_file():
            tail = "\n".join(output.strip().splitlines()[-25:])
            raise RuntimeError(f"Headless core produced no result (exit code {completed.returncode}):\n{tail}")
        result = json.loads(result_json.read_text(encoding="utf-8-sig"))

        by_id = {str(f.get("id")): f for f in result.get("files", [])}
        dwg_count = 0
        pdf_count = 0
        errors = 0
        used: set[str] = set()   # output names written in this run, to avoid clobbering within the batch
        for index, path in enumerate(dwgs):
            rel = rel_display(path, base)
            item = by_id.get(str(index))
            if item is None:
                errors += 1
                log(f"[{index + 1}/{len(dwgs)}] {rel} FAIL core returned no result")
                continue
            if item.get("error"):
                errors += 1
                log(f"[{index + 1}/{len(dwgs)}] {rel} FAIL {item['error']}")
                continue
            item_errors = item.get("errors", [])
            if item_errors:
                errors += len(item_errors)
                for message in item_errors:
                    log(f"    FAIL {message}")
            pdfs = item.get("pdfs", [])
            moved = 0
            for pdf in pdfs:
                src = Path(pdf.get("path", ""))
                if not src.is_file():
                    log(f"    FAIL {pdf.get('name')} was not produced")
                    continue
                dest = resolve_out_path(output_dir / src.name, used, overwrite)
                if dest.exists():          # when overwriting, delete the old file first so shutil.move can land
                    try:
                        dest.unlink()
                    except OSError:
                        pass
                shutil.move(str(src), str(dest))
                moved += 1
                pdf_count += 1
                log(f"    OK   {dest.name} ({dest.stat().st_size} bytes)")
            if moved:
                dwg_count += 1
                log(f"[{index + 1}/{len(dwgs)}] {rel}: {moved} PDF(s)")
            elif item_errors:
                log(f"[{index + 1}/{len(dwgs)}] {rel}: no PDF produced")
            else:
                log(f"[{index + 1}/{len(dwgs)}] {rel}: (no matching title block / closed frame, skipped)")

        return {
            "dwg_total": len(dwgs),
            "dwg_plotted": dwg_count,
            "pdf_count": pdf_count,
            "error_count": errors,
            "output_dir": str(output_dir),
        }


def app_icon_path() -> Path:
    """Packaged: app.ico is bundled via --add-data into _MEIPASS; from source it sits next to the script."""
    base = Path(getattr(sys, "_MEIPASS", Path(__file__).parent))
    return base / "app.ico"


def default_output_dir(folder: Path) -> Path:
    stamp = datetime.date.today().strftime("%Y-%m-%d")
    return folder / f"plot-output_{stamp}"


# ---------------------------------------------------------------------------
# PySide6 GUI
# ---------------------------------------------------------------------------
class WorkerSignals(QObject):
    log = Signal(str)
    done = Signal(object, object)
    error = Signal(str)


class Worker(QRunnable):
    def __init__(self, work: Callable[[], dict[str, Any]], done: Callable[[dict[str, Any]], None]):
        super().__init__()
        self.work = work
        self.done_callback = done
        self.signals = WorkerSignals()

    def run(self) -> None:
        try:
            result = self.work()
            self.signals.done.emit(self.done_callback, result)
        except Exception:
            self.signals.error.emit(traceback.format_exc())


class QtApp(QMainWindow):
    """The main page does three things: pick drawings -> output folder -> Plot. Everything else lives under 'Advanced'."""

    _log_signal = Signal(str)
    _PROGRESS_RE = re.compile(r"^\[(\d+)/(\d+)\]")

    def __init__(self) -> None:
        super().__init__()
        self.setWindowTitle(APP_NAME)
        icon = app_icon_path()
        if icon.is_file():
            self.setWindowIcon(QIcon(str(icon)))
        self.resize(760, 600)
        self.setMinimumSize(640, 480)
        self.setAcceptDrops(True)
        self.pool = QThreadPool.globalInstance()
        self.busy = False
        self._build_ui()
        self.setStyleSheet(build_qss())
        self._log_signal.connect(self._append_log)

    # -- build --
    def _build_ui(self) -> None:
        central = QWidget()
        central.setObjectName("root")
        self.setCentralWidget(central)
        outer = QVBoxLayout(central)
        outer.setContentsMargins(24, 20, 24, 16)
        outer.setSpacing(12)

        title = QLabel(APP_NAME)
        title.setObjectName("title")
        subtitle = QLabel("One PDF per title block in each DWG, named <sheet number> <sheet title>. Runs in the background; no need to open CAD first.")
        subtitle.setObjectName("subtitle")
        subtitle.setWordWrap(True)
        outer.addWidget(title)
        outer.addWidget(subtitle)

        # Main card: two input rows
        card = QFrame()
        card.setObjectName("card")
        form = QGridLayout(card)
        form.setContentsMargins(18, 16, 18, 16)
        form.setHorizontalSpacing(12)
        form.setVerticalSpacing(10)
        form.setColumnStretch(1, 1)

        self.folder_edit = QLineEdit()
        self.folder_edit.setPlaceholderText("Drop DWG files or a folder here, or browse on the right")
        folder_button = QPushButton("Select DWG...")
        folder_button.setObjectName("secondary")
        folder_button.clicked.connect(self._choose_inputs)
        dir_button = QPushButton("Select folder...")
        dir_button.setObjectName("secondary")
        dir_button.clicked.connect(self._choose_folder)
        form.addWidget(QLabel("Drawings"), 0, 0)
        form.addWidget(self.folder_edit, 0, 1)
        picks = QHBoxLayout()
        picks.setSpacing(6)
        picks.addWidget(folder_button)
        picks.addWidget(dir_button)
        form.addLayout(picks, 0, 2)

        self.output_edit = QLineEdit()
        self.output_edit.setPlaceholderText("Empty = 'plot-output_<date>' next to the drawings")
        output_button = QPushButton("Browse...")
        output_button.setObjectName("secondary")
        output_button.clicked.connect(self._choose_output)
        form.addWidget(QLabel("PDF output"), 1, 0)
        form.addWidget(self.output_edit, 1, 1)
        form.addWidget(output_button, 1, 2)
        outer.addWidget(card)

        # Advanced options (collapsed by default)
        self.adv_toggle = QToolButton()
        self.adv_toggle.setObjectName("advToggle")
        self.adv_toggle.setText("Advanced")
        self.adv_toggle.setCheckable(True)
        self.adv_toggle.setChecked(False)
        self.adv_toggle.setArrowType(Qt.RightArrow)
        self.adv_toggle.setToolButtonStyle(Qt.ToolButtonTextBesideIcon)
        self.adv_toggle.toggled.connect(self._toggle_advanced)
        outer.addWidget(self.adv_toggle, 0, Qt.AlignLeft)

        self.adv_panel = QFrame()
        self.adv_panel.setObjectName("card")
        self.adv_panel.setVisible(False)
        adv = QGridLayout(self.adv_panel)
        adv.setContentsMargins(18, 14, 18, 14)
        adv.setHorizontalSpacing(12)
        adv.setVerticalSpacing(10)
        adv.setColumnStretch(1, 1)
        adv.setColumnStretch(3, 1)

        self.filter_edit = QLineEdit(TITLE_BLOCK_FILTER)
        self.filter_edit.setToolTip("Keyword(s) in the title block's block name, case-insensitive; separate several with commas.\nLeave empty and fill 'Frame layer contains' to find closed frames by layer only.")
        adv.addWidget(QLabel("Block name contains"), 0, 0)
        adv.addWidget(self.filter_edit, 0, 1)

        self.layer_edit = QLineEdit()
        self.layer_edit.setPlaceholderText("Empty = do not search by layer")
        self.layer_edit.setToolTip("Keyword(s) in the layer name of closed-polyline frames; separate several with commas.\nEvery closed polyline on a matching layer is plotted by its bounding box.")
        adv.addWidget(QLabel("Frame layer contains"), 0, 2)
        adv.addWidget(self.layer_edit, 0, 3)

        self.style_combo = QComboBox()
        self.style_combo.setEditable(True)
        self.style_combo.addItems(["monochrome.ctb", "acad.ctb", STYLE_COLOR_LABEL])
        self.style_combo.setToolTip("Plot style table: monochrome.ctb (black and white) / acad.ctb / color.\nYou can also type a .ctb/.stb file name; AutoCAD looks it up in the plot style search path.")
        adv.addWidget(QLabel("Plot style table"), 1, 0)
        adv.addWidget(self.style_combo, 1, 1)

        self.paper_combo = QComboBox()
        self.paper_combo.addItems(["A3", "A4", "A2", "A1", "A0"])
        adv.addWidget(QLabel("Paper"), 1, 2)
        adv.addWidget(self.paper_combo, 1, 3)

        self.engine_combo = QComboBox()
        self.engine_combo.addItems(["Background CAD instance (robust, supports OLE tables)", "Headless core accoreconsole (fast)"])
        self.engine_combo.setToolTip("Background CAD instance: starts a hidden Civil 3D (about 30 s cold start); best OLE table / style table support.\nHeadless core: accoreconsole batch plotting, fast and dialog-free; OLE tables print as empty frames.")
        self.engine_combo.currentIndexChanged.connect(self._sync_engine_options)
        adv.addWidget(QLabel("Engine"), 2, 0)
        adv.addWidget(self.engine_combo, 2, 1, 1, 3)

        checks = QHBoxLayout()
        checks.setSpacing(18)
        self.recursive_check = QCheckBox("Include subfolders")
        self.recursive_check.setChecked(True)
        self.recursive_check.setToolTip("Search folders recursively for DWG files.")
        self.overwrite_check = QCheckBox("Overwrite existing PDFs")
        self.overwrite_check.setChecked(True)
        self.overwrite_check.setToolTip("Unchecked: existing names get _2/_3 appended and old files are kept.")
        self.lineweight_check = QCheckBox("Plot lineweights (fixes faint text)")
        self.lineweight_check.setChecked(True)
        self.lineweight_check.setToolTip("Raises the default lineweight to 0.30 mm so thin single-stroke text prints solid. Headless core only.")
        self.transparency_check = QCheckBox("Plot transparency")
        self.transparency_check.setToolTip("Semi-transparent fills stay transparent; by default they print solid. Headless core only.")
        self.visible_check = QCheckBox("Show CAD window")
        self.visible_check.setToolTip("Tick to watch the process when troubleshooting. Background CAD instance only.")
        for box in (self.recursive_check, self.overwrite_check, self.lineweight_check,
                    self.transparency_check, self.visible_check):
            checks.addWidget(box)
        checks.addStretch(1)
        adv.addLayout(checks, 3, 0, 1, 4)
        outer.addWidget(self.adv_panel)
        self._sync_engine_options()

        # Action row
        actions = QHBoxLayout()
        actions.setSpacing(10)
        self.run_button = QPushButton("Plot")
        self.run_button.setObjectName("primary")
        self.run_button.setMinimumHeight(40)
        self.run_button.setMinimumWidth(160)
        self.run_button.clicked.connect(self._run)
        self.open_out_button = QPushButton("Open output folder")
        self.open_out_button.setObjectName("link")
        self.open_out_button.setCursor(Qt.PointingHandCursor)
        self.open_out_button.clicked.connect(self._open_output)
        actions.addWidget(self.run_button)
        actions.addWidget(self.open_out_button)
        actions.addStretch(1)
        self.status_label = QLabel("Ready")
        self.status_label.setObjectName("status")
        actions.addWidget(self.status_label)
        outer.addLayout(actions)

        self.progress = QProgressBar()
        self.progress.setTextVisible(False)
        self.progress.setFixedHeight(6)
        self.progress.setRange(0, 1)
        self.progress.setValue(0)
        self.progress.setVisible(False)
        outer.addWidget(self.progress)

        self.log_text = QTextEdit()
        self.log_text.setObjectName("log")
        self.log_text.setReadOnly(True)
        self.log_text.setLineWrapMode(QTextEdit.WidgetWidth)
        self.log_text.setPlaceholderText("Plot progress appears here")
        outer.addWidget(self.log_text, 1)

    def _toggle_advanced(self, on: bool) -> None:
        self.adv_panel.setVisible(on)
        self.adv_toggle.setArrowType(Qt.DownArrow if on else Qt.RightArrow)

    def _sync_engine_options(self) -> None:
        accore = self.engine_combo.currentIndex() == 1
        self.lineweight_check.setEnabled(accore)
        self.transparency_check.setEnabled(accore)
        self.visible_check.setEnabled(not accore)

    # -- drag and drop --
    def dragEnterEvent(self, event) -> None:  # noqa: N802
        if event.mimeData().hasUrls():
            event.acceptProposedAction()

    def dropEvent(self, event) -> None:  # noqa: N802
        paths = [url.toLocalFile() for url in event.mimeData().urls()]
        paths = [p for p in paths if p and (p.lower().endswith(".dwg") or Path(p).is_dir())]
        if paths:
            self._set_inputs(paths)
            event.acceptProposedAction()

    # -- interaction --
    def _set_inputs(self, paths: list[str]) -> None:
        self.folder_edit.setText(" ; ".join(paths))
        first = Path(paths[0])
        base = first if first.is_dir() else first.parent
        if not self.output_edit.text().strip():
            self.output_edit.setText(str(default_output_dir(base)))

    def _start_dir(self) -> str:
        raw = self.folder_edit.text().strip()
        first = raw.split(";")[0].strip().strip('"') if raw else ""
        if not first:
            return ""
        path = Path(first).expanduser()
        return str(path if path.is_dir() else path.parent)

    def _choose_inputs(self) -> None:
        files, _ = QFileDialog.getOpenFileNames(
            self, "Select DWG files (multi-select)", self._start_dir(), "AutoCAD drawings (*.dwg)"
        )
        if files:
            self._set_inputs(files)

    def _choose_folder(self) -> None:
        selected = QFileDialog.getExistingDirectory(self, "Select the folder containing the DWG files", self._start_dir())
        if selected:
            self._set_inputs([selected])

    def _choose_output(self) -> None:
        selected = QFileDialog.getExistingDirectory(self, "Select output folder", self.output_edit.text())
        if selected:
            self.output_edit.setText(selected)

    def _open_output(self) -> None:
        path = Path(self.output_edit.text().strip())
        if not path.is_dir():
            QMessageBox.information(self, APP_NAME, "The output folder does not exist yet; plot once first.")
            return
        os.startfile(path)  # type: ignore[attr-defined]

    def _run(self) -> None:
        raw = self.folder_edit.text().strip()
        entries = [part for part in raw.split(";") if part.strip()]
        if not entries:
            QMessageBox.critical(self, APP_NAME, "Select DWG files or a folder first (drag and drop works too).")
            return
        recursive = self.recursive_check.isChecked()
        try:
            dwgs, base = resolve_inputs(entries, recursive)
        except RuntimeError as exc:
            QMessageBox.critical(self, APP_NAME, str(exc))
            return

        output_text = self.output_edit.text().strip()
        output_dir = Path(output_text).expanduser() if output_text else default_output_dir(base)
        block_keywords = parse_keywords(self.filter_edit.text())
        layer_keywords = parse_keywords(self.layer_edit.text())
        if not block_keywords and not layer_keywords:
            block_keywords = [TITLE_BLOCK_FILTER]
        ctb = resolve_ctb(self.style_combo.currentText())
        visible = self.visible_check.isChecked()
        overwrite = self.overwrite_check.isChecked()
        paper_hint = self.paper_combo.currentText()

        if self.engine_combo.currentIndex() == 1:
            lw_on = self.lineweight_check.isChecked()
            work = lambda: plot_folder_accore(
                dwgs, base, output_dir, block_keywords, layer_keywords,
                ctb, paper_hint, overwrite, self._thread_log,
                self.transparency_check.isChecked(),
                lw_on, 30 if lw_on else 0,
            )
        else:
            work = lambda: plot_folder(
                dwgs, base, output_dir, block_keywords, layer_keywords,
                ctb, paper_hint, visible, overwrite, self._thread_log,
            )
        self._run_worker(work, self._run_done)

    def _run_worker(self, work: Callable[[], dict[str, Any]], done: Callable[[dict[str, Any]], None]) -> None:
        if self.busy:
            return
        self._set_busy(True)
        self._append_log("-" * 60)
        worker = Worker(work, done)
        worker.signals.log.connect(self._append_log)
        worker.signals.done.connect(self._worker_done)
        worker.signals.error.connect(self._worker_error)
        self.pool.start(worker)

    def _thread_log(self, message: str) -> None:
        self._log_signal.emit(message)

    def _worker_done(self, callback: Callable[[dict[str, Any]], None], result: dict[str, Any]) -> None:
        self._set_busy(False)
        callback(result)

    def _worker_error(self, detail: str) -> None:
        self._set_busy(False)
        self._append_log(detail)
        last_line = detail.strip().splitlines()[-1]
        QMessageBox.critical(self, APP_NAME, last_line)

    def _set_busy(self, busy: bool) -> None:
        self.busy = busy
        self.run_button.setEnabled(not busy)
        self.run_button.setText("Plotting..." if busy else "Plot")
        self.status_label.setText("Plotting, please wait..." if busy else "Ready")
        self.progress.setVisible(busy)
        if busy:
            self.progress.setRange(0, 0)  # busy animation until the first progress line arrives

    def _append_log(self, message: str) -> None:
        text = message.rstrip()
        self.log_text.append(text)
        m = self._PROGRESS_RE.match(text)
        if m and self.busy:
            done, total = int(m.group(1)), int(m.group(2))
            self.progress.setRange(0, max(total, 1))
            self.progress.setValue(done)
            self.status_label.setText(f"Plotting {done}/{total}")

    def _run_done(self, result: dict[str, Any]) -> None:
        summary = (
            f"Finished: {result['dwg_total']} DWG(s), {result['dwg_plotted']} with title blocks; "
            f"{result['pdf_count']} PDF(s) produced; {result['error_count']} error(s)."
        )
        self._append_log(summary)
        self.status_label.setText(f"Done - {result['pdf_count']} PDF(s)")
        box = QMessageBox(self)
        box.setWindowTitle(APP_NAME)
        box.setIcon(QMessageBox.Information if not result["error_count"] else QMessageBox.Warning)
        box.setText(summary)
        box.setInformativeText(f"Output: {result['output_dir']}")
        open_btn = box.addButton("Open output folder", QMessageBox.AcceptRole)
        box.addButton("Close", QMessageBox.RejectRole)
        box.exec()
        if box.clickedButton() is open_btn:
            self.output_edit.setText(str(result["output_dir"]))
            self._open_output()


APP_QSS = """
#root { background: #f4f6f8; }
QLabel { color: #2b2f33; font-size: 13px; }
#title { font-size: 20px; font-weight: 600; color: #1d2329; }
#subtitle { color: #6b7480; font-size: 12px; margin-bottom: 4px; }
#status { color: #6b7480; font-size: 12px; }
#card { background: #ffffff; border: 1px solid #e1e5ea; border-radius: 8px; }
QLineEdit, QComboBox {
    background: #ffffff; border: 1px solid #cfd5dc; border-radius: 6px;
    padding: 6px 10px; min-height: 20px; font-size: 13px; color: #1d2329;
    selection-background-color: #2f7bf5;
}
QLineEdit:focus, QComboBox:focus { border: 1px solid #2f7bf5; }
QComboBox::drop-down { subcontrol-origin: padding; subcontrol-position: center right; width: 24px; border: none; }
QComboBox::down-arrow { image: url({ARROW}); width: 10px; height: 10px; }
QComboBox QAbstractItemView { background: #ffffff; border: 1px solid #cfd5dc; selection-background-color: #e8f0fe; selection-color: #1d2329; }
QPushButton#secondary {
    background: #ffffff; border: 1px solid #cfd5dc; border-radius: 6px;
    padding: 6px 14px; color: #2b2f33; font-size: 13px;
}
QPushButton#secondary:hover { background: #f0f3f6; border-color: #b8c0c9; }
QPushButton#secondary:pressed { background: #e4e8ec; }
QPushButton#primary {
    background: #2f7bf5; border: none; border-radius: 8px;
    color: #ffffff; font-size: 15px; font-weight: 600; padding: 0 28px;
}
QPushButton#primary:hover { background: #2569d6; }
QPushButton#primary:pressed { background: #1d56b3; }
QPushButton#primary:disabled { background: #a9c4f2; color: #ffffff; }
QPushButton#link { background: transparent; border: none; color: #2f7bf5; font-size: 13px; padding: 0 6px; }
QPushButton#link:hover { text-decoration: underline; }
QToolButton#advToggle { background: transparent; border: none; color: #4a5560; font-size: 13px; padding: 2px 2px; }
QToolButton#advToggle:hover { color: #2f7bf5; }
QCheckBox { color: #2b2f33; font-size: 13px; spacing: 6px; }
QCheckBox:disabled { color: #a3abb5; }
QCheckBox::indicator { width: 16px; height: 16px; border: 1px solid #b8c0c9; border-radius: 4px; background: #ffffff; }
QCheckBox::indicator:checked { background: #2f7bf5; border-color: #2f7bf5; image: url({CHECK}); }
QCheckBox::indicator:disabled { background: #eef1f4; border-color: #d8dde3; }
QProgressBar { background: #e1e5ea; border: none; border-radius: 3px; }
QProgressBar::chunk { background: #2f7bf5; border-radius: 3px; }
QTextEdit#log {
    background: #ffffff; border: 1px solid #e1e5ea; border-radius: 8px;
    padding: 8px; font-family: Consolas, "Segoe UI", monospace; font-size: 12px; color: #2b2f33;
}
QToolTip { background: #1d2329; color: #ffffff; border: none; padding: 6px 8px; font-size: 12px; }
"""


_ARROW_SVG = ('<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10" viewBox="0 0 10 10">'
              '<path d="M1.5 3.5 L5 7 L8.5 3.5" fill="none" stroke="#6b7480" stroke-width="1.6" '
              'stroke-linecap="round" stroke-linejoin="round"/></svg>')
_CHECK_SVG = ('<svg xmlns="http://www.w3.org/2000/svg" width="12" height="12" viewBox="0 0 12 12">'
              '<path d="M2.5 6.2 L5 8.7 L9.5 3.5" fill="none" stroke="#ffffff" stroke-width="1.8" '
              'stroke-linecap="round" stroke-linejoin="round"/></svg>')


def build_qss() -> str:
    """Qt style sheets do not accept data URIs; write the two small icons to a temp folder and reference them."""
    icon_dir = Path(tempfile.gettempdir()) / "c3df-titleblock-plotter"
    icon_dir.mkdir(parents=True, exist_ok=True)
    arrow = icon_dir / "arrow.svg"
    check = icon_dir / "check.svg"
    arrow.write_text(_ARROW_SVG, encoding="utf-8")
    check.write_text(_CHECK_SVG, encoding="utf-8")
    return APP_QSS.replace("{ARROW}", arrow.as_posix()).replace("{CHECK}", check.as_posix())


USAGE = """Usage:
  DWGTitleblockPlotter.exe                                            GUI
  DWGTitleblockPlotter.exe <folder-or-dwg> [outdir] [--block <substring>] [--color]

  <folder-or-dwg>       a DWG file, or a folder searched recursively for DWG files
  [outdir]              output folder (default: plot-output_<date> next to the drawings)
  --block <substring>   block name filter, case-insensitive (default "TITLE")
  --color               plot in colour (no style table) instead of monochrome.ctb

  Environment: C3DF_PLOT_TRANSPARENCY=1 plots transparency; C3DF_PLOT_NO_LINEWEIGHT=1 disables lineweights.
"""


def parse_cli(argv: list[str]) -> tuple[list[str], str | None, bool]:
    """Split argv into positional arguments, the optional --block value and the --color flag."""
    positional: list[str] = []
    block: str | None = None
    color = False
    index = 0
    while index < len(argv):
        arg = argv[index]
        if arg == "--block":
            if index + 1 >= len(argv):
                raise SystemExit("--block requires a value.\n" + USAGE)
            block = argv[index + 1]
            index += 2
            continue
        if arg.startswith("--block="):
            block = arg.split("=", 1)[1]
            index += 1
            continue
        if arg in ("--color", "--colour"):
            color = True
            index += 1
            continue
        if arg.startswith("--"):
            raise SystemExit(f"Unknown option: {arg}\n" + USAGE)
        positional.append(arg)
        index += 1
    return positional, block, color


def main() -> None:
    # Command-line headless usage (always accoreconsole):
    #   titleblock_plotter.py <folder-or-dwg> [outdir] [--block <substring>] [--color]
    if len(sys.argv) >= 2 and sys.argv[1] in ("-h", "--help", "/?"):
        print(USAGE)
        return
    if len(sys.argv) >= 2 and sys.argv[1] not in ("--gui", ""):
        positional, block, color = parse_cli(sys.argv[1:])
        if not positional:
            raise SystemExit(USAGE)
        dwgs, base = resolve_inputs([positional[0]], True)
        output_dir = Path(positional[1]).resolve() if len(positional) >= 2 else default_output_dir(base)
        block_keywords = parse_keywords(block) if block is not None else []
        if not block_keywords:
            block_keywords = [TITLE_BLOCK_FILTER]
        ctb = CTB_COLOR if color else CTB_MONO
        transparency = os.environ.get("C3DF_PLOT_TRANSPARENCY", "").strip().lower() in ("1", "true", "yes", "on")
        lw_on = os.environ.get("C3DF_PLOT_NO_LINEWEIGHT", "").strip().lower() not in ("1", "true", "yes", "on")
        result = plot_folder_accore(
            dwgs, base, output_dir, block_keywords, [], ctb, "A3", True, print,
            transparency, lw_on, 30 if lw_on else 0,
        )
        print(result)
        return
    app = QApplication(sys.argv)
    window = QtApp()
    window.show()
    if os.environ.get("C3DF_PLOT_SCREENSHOT"):  # unattended layout check: grab the main window and quit
        from PySide6.QtCore import QTimer

        def _shot() -> None:
            if os.environ.get("C3DF_PLOT_SCREENSHOT_ADV"):
                window.adv_toggle.setChecked(True)
                app.processEvents()
            window.grab().save(os.environ["C3DF_PLOT_SCREENSHOT"])
            app.quit()

        QTimer.singleShot(600, _shot)
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
