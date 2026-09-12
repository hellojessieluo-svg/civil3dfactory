# -*- coding: utf-8 -*-
"""DWG 图框批量打印桌面工具。

选一个文件夹 → 后台起一个隐藏的 AutoCAD/Civil 3D 实例 → 逐个打开里面的 DWG，
把每个「块名包含‘图框’」的图框块按其包围盒窗口出一张黑白 A3 PDF → 全部完成后退出实例。

打印逻辑沿用 2026-07-08_03_项目B补图纸\\headless_plot.py 的 COM 方案（proven）。
界面沿用 2026-07-14_02_图框属性exe 的 PySide6 布局。
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

# 后台（命令行）模式用 print 输出中文日志；Windows 下 stdout 默认 cp1252
# 编不了中文会崩溃。这里强制 UTF-8。GUI(--windowed) 模式 stdout 可能为 None，逐一判空跳过。
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


APP_NAME = "DWG 图框批量打印"
TITLE_BLOCK_FILTER = "图框"          # 块名（EffectiveName）包含此串即视为图框
POLYLINE_OBJECT_NAMES = {"AcDbPolyline", "AcDb2dPolyline", "AcDb3dPolyline"}
PLOTTER = "DWG To PDF.pc3"
CTB_MONO = "monochrome.ctb"
CTB_COLOR = ""                       # 空 = 用图层原色（彩色）
BACKUP_DIR_NAME = ".dwg-attribute-backups"   # 与属性工具一致，发现时跳过

# AutoCAD COM 枚举常量
AC_WINDOW = 4
AC_SCALE_TO_FIT = 0
AC_90 = 1
AC_0 = 0

# 常见图名 / 图号属性标签（按优先级匹配；匹配不到则回退文件名）
TUMING_TAGS = ("02图名", "图名", "DWGNAME", "TITLE")
TUHAO_TAGS = ("03图号", "图号", "DWGNO", "NUMBER")


# ---------------------------------------------------------------------------
# 通用小工具
# ---------------------------------------------------------------------------
def sanitize(name: str) -> str:
    return re.sub(r'[\\/:*?"<>|]', "_", name).strip()


def parse_keywords(text: str) -> list[str]:
    """逗号分隔的多关键字 → 列表（英文逗号为准，顺手兼容中文逗号；去空白、去空项）。"""
    return [part.strip() for part in re.split(r"[,，]", text or "") if part.strip()]


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
    """COM 集合偶发枚举失败时，退回 Item(index)。"""
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
    """把界面里的输入项（手选的 DWG 文件为主，也兼容整个文件夹）解析成
    (待处理 DWG 列表, 基准目录)。基准目录用于日志里的相对路径与默认输出位置。"""
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
        raise RuntimeError("没有选到任何 DWG 文件。请手选 .dwg 文件（可多选），或输入一个文件夹。")

    if dir_inputs and not file_inputs and len(dir_inputs) == 1:
        base = dir_inputs[0].resolve()
    else:
        base = ordered[0].parent
    return ordered, base


def rel_display(path: Path, base: Path) -> str:
    """日志里尽量显示相对路径；跨盘或不在基准目录下时退回文件名。"""
    try:
        return path.relative_to(base).as_posix()
    except ValueError:
        return path.name


def resolve_ctb(style_text: str) -> str:
    """把「打印样式表」下拉里的文本解析成 AutoCAD 能用的样式表名。
    彩色 / 空 → 返回空串（不套样式表，按图层原色）。
    定位到 .ctb/.stb 原件（完整路径）时取文件名——AutoCAD 按名字在打印样式搜索路径里找。"""
    text = (style_text or "").strip().strip('"')
    if not text or "彩色" in text or "无样式表" in text:
        return ""
    if any(sep in text for sep in ("\\", "/")):
        text = Path(text).name
    return text


def unique_path(path: Path) -> Path:
    """输出名冲突时追加 _2 / _3……避免互相覆盖。"""
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
    """决定一张 PDF 的最终落盘路径。
    overwrite=False：遇到磁盘上已存在的同名文件就加 _2/_3（沿用旧行为）。
    overwrite=True ：直接覆盖磁盘上的旧同名文件；但只在「本次运行内」已产出的名字上避让，
                     避免同一批里两个不同图纸的同名输出互相覆盖。"""
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
# COM 会话：后台隐藏实例（DispatchEx 起独立进程，绝不碰用户界面里的 CAD）
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
                "缺少 pywin32。请使用随程序提供的 EXE，或先运行：pip install pywin32"
            ) from exc

        self.pythoncom = pythoncom
        self.VARIANT = VARIANT
        pythoncom.CoInitialize()
        t0 = time.time()
        self.log("正在启动后台 AutoCAD/Civil 3D 实例……（冷启动可能要等十几秒）")
        try:
            # DispatchEx = 新进程；不要用 Dispatch/GetActiveObject，避免影响已开的 CAD
            self.acad = win32com.client.DispatchEx("AutoCAD.Application")
            self.acad.Visible = bool(self.visible)
            _ = self.acad.Name
        except Exception as exc:
            pythoncom.CoUninitialize()
            self.pythoncom = None
            raise RuntimeError(f"无法启动 AutoCAD/Civil 3D 实例：{exc}") from exc
        self.log(f"实例已就绪：{self.acad.Name}（{time.time() - t0:.0f}s）")
        self._warmup()
        return self

    def _warmup(self) -> None:
        """访问启动时自带的空白图（Drawing1）的集合，把文档子系统热起来，避免第一张
        真实 DWG 撞上冷启动故障（COM 版对应 02 项目 seed.dwg 那条踩坑）。复用已有空白图，
        不再 Documents.Add() 另造 Drawing2——否则退出时会弹“是否保存 Drawing2”。"""
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
                self.log("后台实例已预热")
                return
            except Exception:
                time.sleep(0.5)
                seed = safe_get(self.acad, "ActiveDocument", None)
        self.log("预热超时，继续尝试打印")

    def pt2(self, x: float, y: float) -> Any:
        return self.VARIANT(self.pythoncom.VT_ARRAY | self.pythoncom.VT_R8, [x, y])

    def open_document(self, path: Path) -> Any:
        last_error = None
        for attempt in range(8):
            try:
                # 只读打开：打印无需写，也避免与你前台已开的同名图冲突
                return self.acad.Documents.Open(str(path), True)
            except Exception as exc:
                last_error = exc
                time.sleep(0.75 + attempt * 0.25)
        raise RuntimeError(f"AutoCAD 无法打开：{path}\n{last_error}")

    def wait_doc_ready(self, opened_doc: Any, timeout: int = 90) -> Any:
        """Open 返回后文档对象可能还没就绪；等到 Name/Layout 可读再继续。"""
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
        raise RuntimeError(f"DWG 已 Open 但文档对象未就绪：{last_error!r}")

    def __exit__(self, exc_type: Any, exc: Any, tb: Any) -> None:
        try:
            if self.acad is not None:
                # 退出前把所有文档都以“不保存”关闭，避免弹“是否保存 DrawingN”
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
# 单个图框 → 一张 PDF
# ---------------------------------------------------------------------------
def read_titleblock_name(block: Any) -> tuple[str, str]:
    """从图框块属性里读 图号 / 图名，读不到返回空串。"""
    tuming = tuhao = ""
    try:
        attrs = list(block.GetAttributes())
    except Exception:
        return "", ""
    tags = {str(safe_get(att, "TagString")): str(safe_get(att, "TextString")) for att in attrs}
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
    """产出 (布局名, 布局对象, 该布局对应的块表)。含模型空间。"""
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
        # 不要求属性块：普通块也算图框，属性只影响 PDF 命名（读不到回退 DWG名_布局名）
        if not matches_any(effective_name(ent), block_keywords):
            continue
        found.append(ent)
    return found


def find_layer_frames(space: Any, layer_keywords: list[str]) -> list[Any]:
    """闭合多段线图框：图层名包含任一关键字的闭合多段线，按其包围盒当图框打印。"""
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
    """配置当前活动布局的绘图仪 / 纸张 / 笔样式 / 比例。这些是布局级设置，
    一张布局只需做一次；同布局多个图框时不必重复（去掉每图框重载绘图仪的浪费）。"""
    doc.SetVariable("BACKGROUNDPLOT", 0)
    active = doc.ActiveLayout
    active.ConfigName = PLOTTER
    try:
        active.RefreshPlotDeviceInfo()
    except Exception:
        pass

    # 选纸：优先含 paper_hint（A3/A4）的介质名，尽量取 full_bleed
    chosen = None
    for media in active.GetCanonicalMediaNames():
        if paper_hint in media:
            chosen = media
            if "full_bleed" in media.lower():
                break
    if chosen:
        active.CanonicalMediaName = chosen

    # 黑白：必须先开 PlotWithPlotStyles，再赋 StyleSheet，否则 ctb 不生效
    if ctb:
        active.PlotWithPlotStyles = True
        active.StyleSheet = ctb
    else:
        active.PlotWithPlotStyles = False

    # 始终忽略对象/图层线宽。COM 的属性名是 PlotWithLineweights；同时关闭
    # ScaleLineweights，避免布局里遗留的“缩放线宽”设置影响最终 PDF。
    active.PlotWithLineweights = False
    try:
        active.ScaleLineweights = False
    except Exception:
        pass

    active.UseStandardScale = True
    active.StandardScale = AC_SCALE_TO_FIT
    active.CenterPlot = True

    try:
        doc.SetVariable("PLOTTRANSPARENCYOVERRIDE", 1)  # 1=不打印透明度（实心）
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
    """按单个图框包围盒设打印窗口并出一张 PDF（布局设备已由 configure_layout_device 配好）。"""
    active = doc.ActiveLayout
    mn, mx = block.GetBoundingBox()
    active.SetWindowToPlot(session.pt2(mn[0], mn[1]), session.pt2(mx[0], mx[1]))
    active.PlotType = AC_WINDOW
    active.PlotRotation = AC_90 if (mx[0] - mn[0]) >= (mx[1] - mn[1]) else AC_0

    ok = doc.Plot.PlotToFile(str(out_path))
    if ok and out_path.exists():
        log(f"    ✓ {out_path.name}（{out_path.stat().st_size} 字节）")
        return True
    log(f"    ✗ 未生成 {out_path.name}")
    return False


# ---------------------------------------------------------------------------
# 主流程：文件夹 → 批量打印
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
    """处理一个 DWG，返回 (出图数, 是否含图框, 错误信息或None)。"""
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
            configure_layout_device(doc, ctb, paper_hint)  # 每张布局只配一次绘图仪
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
                    return made, found_any, f"打印失败：{exc}"
            for frame in frames:
                found_any = True
                base = sanitize(f"{path.stem}_{layout_name}")
                out_path = resolve_out_path(output_dir / (base + ".pdf"), used, overwrite)
                try:
                    if plot_block_window(session, doc, frame, out_path, log):
                        made += 1
                except Exception as exc:
                    return made, found_any, f"打印失败：{exc}"
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
        raise RuntimeError("没有待处理的 DWG。")

    output_dir.mkdir(parents=True, exist_ok=True)
    log(f"共 {len(dwgs)} 个 DWG，输出到：{output_dir}")
    log(f"打印样式表：{ctb or '（彩色·按图层原色）'}")
    log("同名 PDF：直接覆盖" if overwrite else "同名 PDF：加 _2/_3 保留旧文件")

    dwg_count = 0
    pdf_count = 0
    errors: list[dict[str, str]] = []
    used: set[str] = set()   # 本次运行已产出的输出名，避免同批内互相覆盖

    with AcadHiddenSession(visible, log) as session:
        for index, path in enumerate(dwgs, 1):
            rel = rel_display(path, base)
            log(f"[{index}/{len(dwgs)}] {rel}")
            made, found_any, err = _process_dwg(session, path, output_dir, block_keywords, layer_keywords, ctb, paper_hint, overwrite, used, log)
            # 冷启动瞬时故障（如第一张的 <unknown>.Count）：预热后重试一次
            if err and made == 0 and not found_any:
                log("    首次读取失败，稍候重试一次……")
                time.sleep(1.5)
                made, found_any, err = _process_dwg(session, path, output_dir, block_keywords, layer_keywords, ctb, paper_hint, overwrite, used, log)
            pdf_count += made
            if found_any:
                dwg_count += 1
            if err:
                errors.append({"file": rel, "error": err})
                log(f"    ✗ {err}")
            elif not found_any:
                log("    （未找到匹配的图框块 / 闭合图框，跳过）")

    return {
        "dwg_total": len(dwgs),
        "dwg_plotted": dwg_count,
        "pdf_count": pdf_count,
        "error_count": len(errors),
        "output_dir": str(output_dir),
    }


# ---------------------------------------------------------------------------
# 后端二：accoreconsole 无界面内核 + CadPlotPlugin（一次进程批量，快、零弹窗）
# ---------------------------------------------------------------------------
def find_core_console() -> Path:
    configured = os.environ.get("C3DF_ACCORECONSOLE", "").strip()
    candidates = [
        Path(configured) if configured else None,
        Path(r"C:\Program Files\Autodesk\AutoCAD 2025\accoreconsole.exe"),
        Path(r"C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe"),
    ]
    for candidate in candidates:
        if candidate is not None and candidate.is_file():
            return candidate
    raise RuntimeError("找不到 accoreconsole.exe，请确认已安装 AutoCAD/Civil 3D 2025。")


def bundled_plugin_path() -> Path:
    if getattr(sys, "frozen", False):
        base = Path(getattr(sys, "_MEIPASS"))
        candidate = base / "CadPlotPlugin.dll"
    else:
        candidate = Path(__file__).resolve().parent / "CadPlotPlugin" / "bin" / "hotload" / "CadPlotPlugin.dll"
    if not candidate.is_file():
        raise RuntimeError(f"缺少后台打印插件：{candidate}")
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
        raise RuntimeError("没有待处理的 DWG。")
    output_dir.mkdir(parents=True, exist_ok=True)
    log(f"共 {len(dwgs)} 个 DWG，启动无界面内核批量出图……（首次加载引擎要等一会）")
    log(f"打印样式表：{ctb or '（彩色·按图层原色）'}")
    log("打印透明度：开（透明对象按透明呈现）" if plot_transparency else "打印透明度：关（透明对象出实心）")
    if print_lineweights:
        log(f"打印线宽：开，默认线宽 {lwdefault/100:.2f}mm（细字打实、治淡显）" if lwdefault > 0 else "打印线宽：开（按对象线宽）")
    else:
        log("打印线宽：关（细单线文字可能发灰淡显）")
    log("同名 PDF：直接覆盖" if overwrite else "同名 PDF：加 _2/_3 保留旧文件")

    with tempfile.TemporaryDirectory(prefix="c3df_plot_", ignore_cleanup_errors=True) as temporary:
        workdir = Path(temporary)
        plugin_copy = workdir / "CadPlotPlugin.dll"
        shutil.copy2(bundled_plugin_path(), plugin_copy)
        out_tmp = workdir / "out"
        out_tmp.mkdir()

        # 待处理图纸复制成英文临时副本，规避中文路径在内核里的坑；原名放 stem 供命名回退
        jobs = []
        for index, path in enumerate(dwgs):
            input_dwg = workdir / f"input_{index}.dwg"
            shutil.copy2(path, input_dwg)
            # origin_dir 给插件还原相对外参路径用：副本在临时英文目录里，
            # 图上存的 .\0-参照\xx.dwg 只有按原目录拼才找得到（2026-08-27 项目B 1103）
            jobs.append({"id": str(index), "path": str(input_dwg), "stem": path.stem,
                         "origin_dir": str(path.parent)})

        payload = {
            "block_keywords": block_keywords,
            "layer_keywords": layer_keywords,
            "output_dir": str(out_tmp),
            "paper": paper_hint,
            "ctb": ctb,                          # 新插件优先用它；空串 = 彩色
            "monochrome": ctb.lower() == "monochrome.ctb",  # 兼容旧插件的回退字段
            "plot_transparency": plot_transparency,  # 打印透明度开关（默认关）
            "print_lineweights": print_lineweights,  # 打印线宽开关（默认关）
            "lwdefault": lwdefault,                  # >0 设 LWDEFAULT（百分之毫米）
            "jobs": jobs,
        }
        batch_json = workdir / "batch.json"
        result_json = workdir / "result.json"
        batch_json.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")

        # 独立 seed.dwg 作内核启动文件，避免第一张图既当启动又被侧读的占用冲突（02 项目踩坑）
        seed = workdir / "seed.dwg"
        shutil.copy2(dwgs[0], seed)

        script = workdir / "plot.scr"
        # /loadmodule does not reliably register managed .NET commands on all
        # AutoCAD/Civil 3D 2025 installations. NETLOAD is reliable, but the DLL
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
            raise RuntimeError("无界面内核处理超时（60 分钟）。") from exc
        output = core_output_text(completed.stdout)
        time.sleep(0.5)  # 进程退出后 Windows 偶尔仍短暂持有临时目录句柄

        if not result_json.is_file():
            tail = "\n".join(output.strip().splitlines()[-25:])
            raise RuntimeError(f"无界面内核未生成结果（退出码 {completed.returncode}）：\n{tail}")
        result = json.loads(result_json.read_text(encoding="utf-8-sig"))

        by_id = {str(f.get("id")): f for f in result.get("files", [])}
        dwg_count = 0
        pdf_count = 0
        errors = 0
        used: set[str] = set()   # 本次运行已落盘的输出名，避免同批内互相覆盖
        for index, path in enumerate(dwgs):
            rel = rel_display(path, base)
            item = by_id.get(str(index))
            if item is None:
                errors += 1
                log(f"[{index + 1}/{len(dwgs)}] {rel} ✗ 内核未返回结果")
                continue
            if item.get("error"):
                errors += 1
                log(f"[{index + 1}/{len(dwgs)}] {rel} ✗ {item['error']}")
                continue
            item_errors = item.get("errors", [])
            if item_errors:
                errors += len(item_errors)
                for message in item_errors:
                    log(f"    ✗ {message}")
            pdfs = item.get("pdfs", [])
            moved = 0
            for pdf in pdfs:
                src = Path(pdf.get("path", ""))
                if not src.is_file():
                    log(f"    ✗ 未生成 {pdf.get('name')}")
                    continue
                dest = resolve_out_path(output_dir / src.name, used, overwrite)
                if dest.exists():          # overwrite 时磁盘旧同名先删，shutil.move 才能落
                    try:
                        dest.unlink()
                    except OSError:
                        pass
                shutil.move(str(src), str(dest))
                moved += 1
                pdf_count += 1
                log(f"    ✓ {dest.name}（{dest.stat().st_size} 字节）")
            if moved:
                dwg_count += 1
                log(f"[{index + 1}/{len(dwgs)}] {rel}：{moved} 张")
            elif item_errors:
                log(f"[{index + 1}/{len(dwgs)}] {rel}：未成功生成 PDF")
            else:
                log(f"[{index + 1}/{len(dwgs)}] {rel}：（未找到匹配的图框块 / 闭合图框，跳过）")

        return {
            "dwg_total": len(dwgs),
            "dwg_plotted": dwg_count,
            "pdf_count": pdf_count,
            "error_count": errors,
            "output_dir": str(output_dir),
        }


def app_icon_path() -> Path:
    """打包后 app.ico 随 --add-data 落在 _MEIPASS；源码运行时在脚本旁。"""
    base = Path(getattr(sys, "_MEIPASS", Path(__file__).parent))
    return base / "app.ico"


def default_output_dir(folder: Path) -> Path:
    stamp = datetime.date.today().strftime("%Y-%m-%d")
    return folder / f"打印输出_{stamp}"


# ---------------------------------------------------------------------------
# PySide6 界面（布局参考 2026-07-14_02_图框属性exe 的 QtApp）
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
    """首页只做三件事：选图 → 输出到 → 开始打印。其余全部收进「高级选项」。"""

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

    # -- 构建 --
    def _build_ui(self) -> None:
        central = QWidget()
        central.setObjectName("root")
        self.setCentralWidget(central)
        outer = QVBoxLayout(central)
        outer.setContentsMargins(24, 20, 24, 16)
        outer.setSpacing(12)

        title = QLabel(APP_NAME)
        title.setObjectName("title")
        subtitle = QLabel("把每张 DWG 里的图框各出一张 PDF，文件名取图号+图名。后台运行，不用先开 CAD。")
        subtitle.setObjectName("subtitle")
        subtitle.setWordWrap(True)
        outer.addWidget(title)
        outer.addWidget(subtitle)

        # 主卡片：两行输入
        card = QFrame()
        card.setObjectName("card")
        form = QGridLayout(card)
        form.setContentsMargins(18, 16, 18, 16)
        form.setHorizontalSpacing(12)
        form.setVerticalSpacing(10)
        form.setColumnStretch(1, 1)

        self.folder_edit = QLineEdit()
        self.folder_edit.setPlaceholderText("把 DWG 或文件夹拖到这里，或点右边选择")
        folder_button = QPushButton("选择 DWG…")
        folder_button.setObjectName("secondary")
        folder_button.clicked.connect(self._choose_inputs)
        dir_button = QPushButton("选文件夹…")
        dir_button.setObjectName("secondary")
        dir_button.clicked.connect(self._choose_folder)
        form.addWidget(QLabel("图纸"), 0, 0)
        form.addWidget(self.folder_edit, 0, 1)
        picks = QHBoxLayout()
        picks.setSpacing(6)
        picks.addWidget(folder_button)
        picks.addWidget(dir_button)
        form.addLayout(picks, 0, 2)

        self.output_edit = QLineEdit()
        self.output_edit.setPlaceholderText("留空 = 图纸所在文件夹里的「打印输出_日期」")
        output_button = QPushButton("选择…")
        output_button.setObjectName("secondary")
        output_button.clicked.connect(self._choose_output)
        form.addWidget(QLabel("PDF 输出到"), 1, 0)
        form.addWidget(self.output_edit, 1, 1)
        form.addWidget(output_button, 1, 2)
        outer.addWidget(card)

        # 高级选项（默认折叠）
        self.adv_toggle = QToolButton()
        self.adv_toggle.setObjectName("advToggle")
        self.adv_toggle.setText("高级选项")
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
        self.filter_edit.setToolTip("图框块的块名关键字；英文逗号分隔可多选。\n清空且填了「图框图层含」时，只按图层找闭合图框。")
        adv.addWidget(QLabel("图框块名含"), 0, 0)
        adv.addWidget(self.filter_edit, 0, 1)

        self.layer_edit = QLineEdit()
        self.layer_edit.setPlaceholderText("留空 = 不按图层找")
        self.layer_edit.setToolTip("闭合多段线图框所在图层的关键字；英文逗号分隔可多选。\n图层名包含任一关键字的闭合多段线，按其包围盒各出一张 PDF。")
        adv.addWidget(QLabel("图框图层含"), 0, 2)
        adv.addWidget(self.layer_edit, 0, 3)

        self.style_combo = QComboBox()
        self.style_combo.setEditable(True)
        self.style_combo.addItems(["monochrome.ctb", "acad.ctb", "（彩色·按图层原色，无样式表）"])
        self.style_combo.setToolTip("打印样式表：monochrome.ctb（黑白）/ acad.ctb / 彩色。\n也可直接输入 .ctb/.stb 文件名，AutoCAD 按名字在打印样式搜索路径里找。")
        adv.addWidget(QLabel("打印样式表"), 1, 0)
        adv.addWidget(self.style_combo, 1, 1)

        self.paper_combo = QComboBox()
        self.paper_combo.addItems(["A3", "A4", "A2", "A1", "A0"])
        adv.addWidget(QLabel("纸张"), 1, 2)
        adv.addWidget(self.paper_combo, 1, 3)

        self.engine_combo = QComboBox()
        self.engine_combo.addItems(["后台 CAD 实例（稳，支持 OLE 表）", "无界面内核 accoreconsole（快）"])
        self.engine_combo.setToolTip("后台 CAD 实例：另起隐藏的 Civil 3D，冷启动约 30 秒，OLE 表/样式表支持最全。\n无界面内核：accoreconsole 批量出图，快、无弹窗；图里有 OLE 表会印成空框。")
        self.engine_combo.currentIndexChanged.connect(self._sync_engine_options)
        adv.addWidget(QLabel("引擎"), 2, 0)
        adv.addWidget(self.engine_combo, 2, 1, 1, 3)

        checks = QHBoxLayout()
        checks.setSpacing(18)
        self.recursive_check = QCheckBox("含子文件夹")
        self.recursive_check.setChecked(True)
        self.recursive_check.setToolTip("输入是文件夹时递归查找 DWG。")
        self.overwrite_check = QCheckBox("覆盖同名 PDF")
        self.overwrite_check.setChecked(True)
        self.overwrite_check.setToolTip("取消则同名一律加 _2/_3 保留旧文件。")
        self.lineweight_check = QCheckBox("打印线宽（治细字淡显）")
        self.lineweight_check.setChecked(True)
        self.lineweight_check.setToolTip("默认线宽抬到 0.30mm，细单线文字打实。仅无界面内核支持。")
        self.transparency_check = QCheckBox("打印透明度")
        self.transparency_check.setToolTip("半透明填充按透明呈现；默认出实心。仅无界面内核支持。")
        self.visible_check = QCheckBox("显示 CAD 窗口")
        self.visible_check.setToolTip("排查问题时勾上看过程。仅后台 CAD 实例支持。")
        for box in (self.recursive_check, self.overwrite_check, self.lineweight_check,
                    self.transparency_check, self.visible_check):
            checks.addWidget(box)
        checks.addStretch(1)
        adv.addLayout(checks, 3, 0, 1, 4)
        outer.addWidget(self.adv_panel)
        self._sync_engine_options()

        # 动作行
        actions = QHBoxLayout()
        actions.setSpacing(10)
        self.run_button = QPushButton("开始打印")
        self.run_button.setObjectName("primary")
        self.run_button.setMinimumHeight(40)
        self.run_button.setMinimumWidth(160)
        self.run_button.clicked.connect(self._run)
        self.open_out_button = QPushButton("打开输出文件夹")
        self.open_out_button.setObjectName("link")
        self.open_out_button.setCursor(Qt.PointingHandCursor)
        self.open_out_button.clicked.connect(self._open_output)
        actions.addWidget(self.run_button)
        actions.addWidget(self.open_out_button)
        actions.addStretch(1)
        self.status_label = QLabel("就绪")
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
        self.log_text.setPlaceholderText("打印进度会显示在这里")
        outer.addWidget(self.log_text, 1)

    def _toggle_advanced(self, on: bool) -> None:
        self.adv_panel.setVisible(on)
        self.adv_toggle.setArrowType(Qt.DownArrow if on else Qt.RightArrow)

    def _sync_engine_options(self) -> None:
        accore = self.engine_combo.currentIndex() == 1
        self.lineweight_check.setEnabled(accore)
        self.transparency_check.setEnabled(accore)
        self.visible_check.setEnabled(not accore)

    # -- 拖放 --
    def dragEnterEvent(self, event) -> None:  # noqa: N802
        if event.mimeData().hasUrls():
            event.acceptProposedAction()

    def dropEvent(self, event) -> None:  # noqa: N802
        paths = [url.toLocalFile() for url in event.mimeData().urls()]
        paths = [p for p in paths if p and (p.lower().endswith(".dwg") or Path(p).is_dir())]
        if paths:
            self._set_inputs(paths)
            event.acceptProposedAction()

    # -- 交互 --
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
            self, "选择 DWG 文件（可多选）", self._start_dir(), "AutoCAD 图纸 (*.dwg)"
        )
        if files:
            self._set_inputs(files)

    def _choose_folder(self) -> None:
        selected = QFileDialog.getExistingDirectory(self, "选择存放 DWG 的文件夹", self._start_dir())
        if selected:
            self._set_inputs([selected])

    def _choose_output(self) -> None:
        selected = QFileDialog.getExistingDirectory(self, "选择输出文件夹", self.output_edit.text())
        if selected:
            self.output_edit.setText(selected)

    def _open_output(self) -> None:
        path = Path(self.output_edit.text().strip())
        if not path.is_dir():
            QMessageBox.information(self, APP_NAME, "输出文件夹还不存在，先打印一次。")
            return
        os.startfile(path)  # type: ignore[attr-defined]

    def _run(self) -> None:
        raw = self.folder_edit.text().strip()
        entries = [part for part in raw.split(";") if part.strip()]
        if not entries:
            QMessageBox.critical(self, APP_NAME, "请先选 DWG 文件或文件夹（也可以直接拖进来）。")
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
        self.run_button.setText("正在打印…" if busy else "开始打印")
        self.status_label.setText("正在打印，请等待……" if busy else "就绪")
        self.progress.setVisible(busy)
        if busy:
            self.progress.setRange(0, 0)  # 未拿到进度前走忙碌动画

    def _append_log(self, message: str) -> None:
        text = message.rstrip()
        self.log_text.append(text)
        m = self._PROGRESS_RE.match(text)
        if m and self.busy:
            done, total = int(m.group(1)), int(m.group(2))
            self.progress.setRange(0, max(total, 1))
            self.progress.setValue(done)
            self.status_label.setText(f"正在打印 {done}/{total}")

    def _run_done(self, result: dict[str, Any]) -> None:
        summary = (
            f"完成：{result['dwg_total']} 个 DWG，其中 {result['dwg_plotted']} 个含图框；"
            f"共出 {result['pdf_count']} 张 PDF；失败 {result['error_count']} 项。"
        )
        self._append_log(summary)
        self.status_label.setText(f"完成 · {result['pdf_count']} 张 PDF")
        box = QMessageBox(self)
        box.setWindowTitle(APP_NAME)
        box.setIcon(QMessageBox.Information if not result["error_count"] else QMessageBox.Warning)
        box.setText(summary)
        box.setInformativeText(f"输出：{result['output_dir']}")
        open_btn = box.addButton("打开输出文件夹", QMessageBox.AcceptRole)
        box.addButton("关闭", QMessageBox.RejectRole)
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
    padding: 8px; font-family: Consolas, "Microsoft YaHei", monospace; font-size: 12px; color: #2b2f33;
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
    """Qt 样式表不认 data URI，把两个小图标落到临时目录再引用。"""
    icon_dir = Path(tempfile.gettempdir()) / "c3df-titleblock-plotter"
    icon_dir.mkdir(parents=True, exist_ok=True)
    arrow = icon_dir / "arrow.svg"
    check = icon_dir / "check.svg"
    arrow.write_text(_ARROW_SVG, encoding="utf-8")
    check.write_text(_CHECK_SVG, encoding="utf-8")
    return APP_QSS.replace("{ARROW}", arrow.as_posix()).replace("{CHECK}", check.as_posix())


def main() -> None:
    # 命令行无界面用法：python titleblock_plotter.py <文件夹或DWG> [输出文件夹]（默认走 accoreconsole）
    if len(sys.argv) >= 2 and sys.argv[1] not in ("--gui", ""):
        dwgs, base = resolve_inputs([sys.argv[1]], True)
        output_dir = Path(sys.argv[2]).resolve() if len(sys.argv) >= 3 else default_output_dir(base)
        transparency = os.environ.get("C3DF_PLOT_TRANSPARENCY", "").strip().lower() in ("1", "true", "yes", "on")
        lw_on = os.environ.get("C3DF_PLOT_NO_LINEWEIGHT", "").strip().lower() not in ("1", "true", "yes", "on")
        result = plot_folder_accore(
            dwgs, base, output_dir, [TITLE_BLOCK_FILTER], [], CTB_MONO, "A3", True, print,
            transparency, lw_on, 30 if lw_on else 0,
        )
        print(result)
        return
    app = QApplication(sys.argv)
    window = QtApp()
    window.show()
    if os.environ.get("C3DF_PLOT_SCREENSHOT"):  # 无人值守核版面：截一张主窗口就退出
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
