# -*- coding: utf-8 -*-
"""DWG 图框属性批量导出/写回桌面工具。"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import traceback
from datetime import datetime
from pathlib import Path
from typing import Any, Callable, Iterable

# 后台（--headless）模式用 print 输出中文日志；Windows 下 stdout 默认 cp1252
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

from PySide6.QtCore import (
    QAbstractTableModel,
    QModelIndex,
    QObject,
    QRunnable,
    Qt,
    QThreadPool,
    Signal,
)
from PySide6.QtGui import QColor
from PySide6.QtWidgets import (
    QAbstractItemView,
    QApplication,
    QCheckBox,
    QComboBox,
    QDialog,
    QDialogButtonBox,
    QFileDialog,
    QFormLayout,
    QGridLayout,
    QGroupBox,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QMainWindow,
    QMenu,
    QMessageBox,
    QPlainTextEdit,
    QPushButton,
    QScrollArea,
    QTableView,
    QTabWidget,
    QTextEdit,
    QVBoxLayout,
    QWidget,
)


APP_NAME = "DWG 图框属性编辑器"
TITLE_BLOCK_FILTER = "图框"
SCHEMA_VERSION = 1
BACKUP_DIR_NAME = ".dwg-attribute-backups"


def now_iso() -> str:
    return datetime.now().astimezone().isoformat(timespec="seconds")


def safe_get(obj: Any, name: str, default: Any = "") -> Any:
    try:
        return getattr(obj, name)
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
    files = []
    for path in folder.glob(pattern):
        if not path.is_file() or BACKUP_DIR_NAME in path.parts:
            continue
        files.append(path.resolve())
    return sorted(files, key=lambda p: str(p).casefold())


def atomic_json_dump(data: dict[str, Any], target: Path) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    temp = target.with_suffix(target.suffix + ".tmp")
    with temp.open("w", encoding="utf-8", newline="\n") as stream:
        json.dump(data, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    os.replace(temp, target)


class AcadSession:
    """连接用户已打开且处于空闲状态的 AutoCAD/Civil 3D。"""

    def __init__(self, visible: bool, log: Callable[[str], None]):
        self.visible = visible
        self.log = log
        self.acad = None
        self.pythoncom = None

    def __enter__(self) -> "AcadSession":
        try:
            import pythoncom
            import win32com.client
        except ImportError as exc:
            raise RuntimeError(
                "缺少 pywin32。请使用随程序提供的 EXE，或先运行：pip install pywin32"
            ) from exc

        self.pythoncom = pythoncom
        pythoncom.CoInitialize()
        self.log("正在连接已打开的 AutoCAD/Civil 3D……")
        try:
            self.acad = win32com.client.GetActiveObject("AutoCAD.Application")
            _ = self.acad.Name
        except Exception as exc:
            pythoncom.CoUninitialize()
            self.pythoncom = None
            raise RuntimeError(
                "没有找到可连接的 AutoCAD/Civil 3D。请先打开 CAD、等待启动完成并按 ESC 结束当前命令。"
            ) from exc
        return self

    def open_document(self, path: Path, read_only: bool) -> Any:
        last_error = None
        for attempt in range(8):
            try:
                return self.acad.Documents.Open(str(path), read_only)
            except Exception as exc:
                last_error = exc
                time.sleep(0.75 + attempt * 0.25)
        raise RuntimeError(f"AutoCAD 无法打开：{path}\n{last_error}")

    def __exit__(self, exc_type: Any, exc: Any, tb: Any) -> None:
        # 只断开 COM；绝不关闭用户原本已打开的 CAD。
        self.acad = None
        if self.pythoncom is not None:
            self.pythoncom.CoUninitialize()


def iter_spaces(doc: Any) -> Iterable[tuple[str, str, Any]]:
    yield "ModelSpace", "Model", doc.ModelSpace
    for layout in iter_com(doc.Layouts):
        name = str(safe_get(layout, "Name"))
        if name.casefold() == "model":
            continue
        yield "PaperSpace", name, layout.Block


def extract_blocks(doc: Any, block_filter: str) -> list[dict[str, Any]]:
    blocks: list[dict[str, Any]] = []
    filter_folded = block_filter.casefold().strip()
    for space_kind, layout_name, space in iter_spaces(doc):
        for ref in iter_com(space):
            try:
                if not bool(ref.HasAttributes):
                    continue
                attrs = list(ref.GetAttributes())
            except Exception:
                continue
            name = effective_name(ref)
            if filter_folded and filter_folded not in name.casefold():
                continue
            tag_counts: dict[str, int] = {}
            exported_attrs = []
            for att in attrs:
                tag = str(safe_get(att, "TagString"))
                tag_index = tag_counts.get(tag, 0)
                tag_counts[tag] = tag_index + 1
                value = str(safe_get(att, "TextString"))
                exported_attrs.append(
                    {
                        "tag": tag,
                        "tag_index": tag_index,
                        "attribute_handle": str(safe_get(att, "Handle")),
                        "original_value": value,
                        "value": value,
                    }
                )
            if not exported_attrs:
                continue
            blocks.append(
                {
                    "space": space_kind,
                    "layout": layout_name,
                    "block_name": name,
                    "block_handle": str(safe_get(ref, "Handle")),
                    "attributes": exported_attrs,
                }
            )
    return blocks


def export_folder(
    folder: Path,
    json_path: Path,
    recursive: bool,
    block_filter: str,
    visible: bool,
    log: Callable[[str], None],
) -> dict[str, Any]:
    dwgs = discover_dwgs(folder, recursive)
    if not dwgs:
        raise RuntimeError("所选文件夹中没有找到 DWG。")

    result: dict[str, Any] = {
        "schema_version": SCHEMA_VERSION,
        "generated_at": now_iso(),
        "root_folder": str(folder.resolve()),
        "instructions": "只修改 attributes 中的 value；不要修改定位字段和 original_value。",
        "block_name_filter": block_filter,
        "files": [],
        "errors": [],
    }

    with AcadSession(visible, log) as session:
        for index, path in enumerate(dwgs, 1):
            rel = path.relative_to(folder).as_posix()
            log(f"[{index}/{len(dwgs)}] 读取 {rel}")
            doc = None
            try:
                doc = session.open_document(path, read_only=True)
                blocks = extract_blocks(doc, block_filter)
                stat = path.stat()
                result["files"].append(
                    {
                        "relative_path": rel,
                        "source_size": stat.st_size,
                        "source_modified_at": datetime.fromtimestamp(stat.st_mtime).astimezone().isoformat(timespec="seconds"),
                        "blocks": blocks,
                    }
                )
                log(f"    找到 {len(blocks)} 个带属性块")
            except Exception as exc:
                result["errors"].append({"relative_path": rel, "error": str(exc)})
                log(f"    [失败] {exc}")
            finally:
                if doc is not None:
                    try:
                        doc.Close(False)
                    except Exception:
                        pass

    atomic_json_dump(result, json_path)
    block_count = sum(len(item["blocks"]) for item in result["files"])
    attr_count = sum(
        len(block["attributes"])
        for item in result["files"]
        for block in item["blocks"]
    )
    return {
        "dwg_count": len(result["files"]),
        "block_count": block_count,
        "attribute_count": attr_count,
        "error_count": len(result["errors"]),
        "json_path": str(json_path),
    }


def validate_payload(data: Any) -> None:
    if not isinstance(data, dict) or data.get("schema_version") != SCHEMA_VERSION:
        raise RuntimeError(f"JSON 格式不受支持，需要 schema_version={SCHEMA_VERSION}。")
    if not isinstance(data.get("files"), list):
        raise RuntimeError("JSON 缺少 files 列表。")


def requested_change_count(file_item: dict[str, Any]) -> int:
    count = 0
    for block in file_item.get("blocks", []):
        for attr in block.get("attributes", []):
            if str(attr.get("value", "")) != str(attr.get("original_value", "")):
                count += 1
    return count


def find_attribute(ref: Any, record: dict[str, Any]) -> Any | None:
    attrs = list(ref.GetAttributes())
    target_handle = str(record.get("attribute_handle", "")).casefold()
    if target_handle:
        for att in attrs:
            if str(safe_get(att, "Handle")).casefold() == target_handle:
                return att

    target_tag = str(record.get("tag", ""))
    target_index = int(record.get("tag_index", 0))
    matches = [att for att in attrs if str(safe_get(att, "TagString")) == target_tag]
    return matches[target_index] if 0 <= target_index < len(matches) else None


def apply_file_changes(
    doc: Any,
    file_item: dict[str, Any],
    force: bool,
) -> tuple[int, list[dict[str, Any]]]:
    changed = 0
    issues: list[dict[str, Any]] = []
    for block in file_item.get("blocks", []):
        block_handle = str(block.get("block_handle", ""))
        try:
            ref = doc.HandleToObject(block_handle)
        except Exception as exc:
            issues.append({"block_handle": block_handle, "status": "block_not_found", "detail": str(exc)})
            continue

        actual_name = effective_name(ref)
        expected_name = str(block.get("block_name", ""))
        if expected_name and actual_name != expected_name:
            issues.append(
                {
                    "block_handle": block_handle,
                    "status": "block_name_mismatch",
                    "expected": expected_name,
                    "actual": actual_name,
                }
            )
            continue

        for record in block.get("attributes", []):
            original = str(record.get("original_value", ""))
            requested = str(record.get("value", ""))
            if requested == original:
                continue
            att = find_attribute(ref, record)
            if att is None:
                issues.append(
                    {
                        "block_handle": block_handle,
                        "tag": record.get("tag", ""),
                        "status": "attribute_not_found",
                    }
                )
                continue
            current = str(safe_get(att, "TextString"))
            if current != original and not force:
                issues.append(
                    {
                        "block_handle": block_handle,
                        "tag": record.get("tag", ""),
                        "status": "conflict_skipped",
                        "exported_original": original,
                        "current_dwg_value": current,
                        "requested_value": requested,
                    }
                )
                continue
            try:
                att.TextString = requested
                att.Update()
                changed += 1
            except Exception as exc:
                issues.append(
                    {
                        "block_handle": block_handle,
                        "tag": record.get("tag", ""),
                        "status": "write_failed",
                        "detail": str(exc),
                    }
                )
    return changed, issues


def safe_relative_path(root: Path, relative: str) -> Path:
    candidate = (root / Path(relative.replace("/", os.sep))).resolve()
    try:
        candidate.relative_to(root.resolve())
    except ValueError as exc:
        raise RuntimeError(f"JSON 中存在越界路径：{relative}") from exc
    return candidate


def update_folder(
    folder: Path,
    json_path: Path,
    force: bool,
    visible: bool,
    log: Callable[[str], None],
) -> dict[str, Any]:
    with json_path.open("r", encoding="utf-8-sig") as stream:
        data = json.load(stream)
    validate_payload(data)

    work_items = [item for item in data["files"] if requested_change_count(item)]
    if not work_items:
        raise RuntimeError("JSON 中没有检测到 value 的修改。")

    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    backup_root = folder / BACKUP_DIR_NAME / stamp
    report: dict[str, Any] = {
        "updated_at": now_iso(),
        "source_json": str(json_path.resolve()),
        "root_folder": str(folder.resolve()),
        "backup_folder": str(backup_root.resolve()),
        "force_overwrite_conflicts": force,
        "files": [],
    }

    total_changed = 0
    with AcadSession(visible, log) as session:
        for index, file_item in enumerate(work_items, 1):
            relative = str(file_item.get("relative_path", ""))
            path = safe_relative_path(folder, relative)
            entry: dict[str, Any] = {"relative_path": relative, "changed": 0, "issues": []}
            report["files"].append(entry)
            log(f"[{index}/{len(work_items)}] 更新 {relative}")
            if not path.is_file():
                entry["issues"].append({"status": "dwg_not_found", "detail": str(path)})
                log("    [跳过] DWG 不存在")
                continue

            backup = backup_root / Path(relative.replace("/", os.sep))
            backup.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, backup)
            entry["backup"] = str(backup)

            doc = None
            try:
                doc = session.open_document(path, read_only=False)
                changed, issues = apply_file_changes(doc, file_item, force)
                entry["changed"] = changed
                entry["issues"].extend(issues)
                if changed:
                    doc.Save()
                    total_changed += changed
                    log(f"    已保存，修改 {changed} 项")
                else:
                    log("    无可写入项，未保存")
            except Exception as exc:
                entry["issues"].append({"status": "file_failed", "detail": str(exc)})
                log(f"    [失败] {exc}")
            finally:
                if doc is not None:
                    try:
                        doc.Close(False)
                    except Exception:
                        pass

    report_path = json_path.with_name(f"{json_path.stem}_update_report_{stamp}.json")
    atomic_json_dump(report, report_path)
    issue_count = sum(len(item["issues"]) for item in report["files"])
    return {
        "file_count": len(work_items),
        "changed_count": total_changed,
        "issue_count": issue_count,
        "backup_root": str(backup_root),
        "report_path": str(report_path),
    }


# ---------------------------------------------------------------------------
# Headless backend: AutoCAD Core Console + C3DF- prefixed .NET plugin.
# These definitions intentionally replace the earlier COM implementations.
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
    raise RuntimeError("找不到 AutoCAD Core Console（accoreconsole.exe），请确认已安装 AutoCAD/Civil 3D 2025。")


def bundled_plugin_path() -> Path:
    if getattr(sys, "frozen", False):
        base = Path(getattr(sys, "_MEIPASS"))
        candidate = base / "CadBatchPlugin.dll"
    else:
        candidate = Path(__file__).resolve().parent / "CadBatchPlugin" / "bin" / "hotload" / "CadBatchPlugin.dll"
    if not candidate.is_file():
        raise RuntimeError(f"缺少后台 CAD 插件：{candidate}")
    return candidate


def core_output_text(raw: bytes) -> str:
    if not raw:
        return ""
    if b"\x00" in raw[:200]:
        return raw.decode("utf-16-le", errors="replace")
    return raw.decode("utf-8", errors="replace")


def run_core_console(
    source_dwg: Path,
    command: str,
    workdir: Path,
    environment: dict[str, str],
    save_after: bool,
) -> str:
    plugin_copy = workdir / "plugin.dll"
    if not plugin_copy.exists():
        shutil.copy2(bundled_plugin_path(), plugin_copy)
    script = workdir / f"{command.lower()}.scr"
    lines = [
        "_.SECURELOAD",
        "0",
        "_.NETLOAD",
        plugin_copy.as_posix(),
        command,
    ]
    if save_after:
        lines.append("_.QSAVE")
    lines.append("_.QUIT")
    script.write_text("\n".join(lines) + "\n", encoding="ascii")

    env = os.environ.copy()
    env.update(environment)
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    try:
        completed = subprocess.run(
            [str(find_core_console()), "/i", str(source_dwg), "/s", str(script), "/l", "en-US"],
            cwd=str(workdir),
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            timeout=180,
            creationflags=flags,
            check=False,
        )
    except subprocess.TimeoutExpired as exc:
        raise RuntimeError(f"Core Console 处理超时（180 秒）：{source_dwg.name}") from exc
    output = core_output_text(completed.stdout)
    # Core Console 退出后 Windows 偶尔仍短暂持有 cwd/plugin 句柄。
    time.sleep(0.5)
    if completed.returncode != 0:
        tail = "\n".join(output.strip().splitlines()[-20:])
        raise RuntimeError(f"Core Console 失败，退出码 {completed.returncode}：\n{tail}")
    if "Unknown command" in output or "Unable to load" in output:
        tail = "\n".join(output.strip().splitlines()[-20:])
        raise RuntimeError(f"后台插件未正确执行：\n{tail}")
    return output


def _headless_export_folder(
    folder: Path,
    json_path: Path,
    recursive: bool,
    block_filter: str,
    visible: bool,
    log: Callable[[str], None],
) -> dict[str, Any]:
    del visible
    dwgs = discover_dwgs(folder, recursive)
    if not dwgs:
        raise RuntimeError("所选文件夹中没有找到 DWG。")
    result: dict[str, Any] = {
        "schema_version": SCHEMA_VERSION,
        "generated_at": now_iso(),
        "root_folder": str(folder.resolve()),
        "instructions": "只修改 attributes 中的 value；不要修改定位字段和 original_value。",
        "block_name_filter": block_filter,
        "backend": "AutoCAD Core Console + C3DF-EXPORTATTRS",
        "files": [],
        "errors": [],
    }
    filter_folded = block_filter.casefold().strip()

    with tempfile.TemporaryDirectory(prefix="c3df_dwg_attrs_", ignore_cleanup_errors=True) as temporary:
        workdir = Path(temporary)
        for index, path in enumerate(dwgs, 1):
            rel = path.relative_to(folder).as_posix()
            log(f"[{index}/{len(dwgs)}] 后台读取 {rel}")
            raw_json = workdir / f"export_{index}.json"
            try:
                run_core_console(
                    path,
                    "C3DF-EXPORTATTRS",
                    workdir,
                    {"C3DF_ATTR_OUTPUT_JSON": str(raw_json)},
                    save_after=False,
                )
                if not raw_json.is_file():
                    raise RuntimeError("插件未生成属性 JSON。")
                with raw_json.open("r", encoding="utf-8-sig") as stream:
                    raw = json.load(stream)
                blocks = []
                for block in raw.get("blocks", []):
                    if filter_folded and filter_folded not in str(block.get("block_name", "")).casefold():
                        continue
                    attrs = []
                    for attribute in block.get("attributes", []):
                        value = str(attribute.get("value", ""))
                        attrs.append(
                            {
                                "tag": str(attribute.get("tag", "")),
                                "tag_index": int(attribute.get("tag_index", 0)),
                                "attribute_handle": str(attribute.get("attribute_handle", "")),
                                "original_value": value,
                                "value": value,
                            }
                        )
                    if attrs:
                        blocks.append(
                            {
                                "space": str(block.get("space", "")),
                                "layout": str(block.get("layout", "")),
                                "block_name": str(block.get("block_name", "")),
                                "block_handle": str(block.get("block_handle", "")),
                                "attributes": attrs,
                            }
                        )
                stat = path.stat()
                result["files"].append(
                    {
                        "relative_path": rel,
                        "source_size": stat.st_size,
                        "source_modified_at": datetime.fromtimestamp(stat.st_mtime).astimezone().isoformat(timespec="seconds"),
                        "blocks": blocks,
                    }
                )
                log(f"    找到 {len(blocks)} 个带属性块")
            except Exception as exc:
                result["errors"].append({"relative_path": rel, "error": str(exc)})
                log(f"    [失败] {exc}")

    atomic_json_dump(result, json_path)
    block_count = sum(len(item["blocks"]) for item in result["files"])
    attr_count = sum(len(block["attributes"]) for item in result["files"] for block in item["blocks"])
    return {
        "dwg_count": len(result["files"]),
        "block_count": block_count,
        "attribute_count": attr_count,
        "error_count": len(result["errors"]),
        "json_path": str(json_path),
    }


def _update_payload(file_item: dict[str, Any], force: bool) -> dict[str, Any]:
    blocks = []
    for block in file_item.get("blocks", []):
        attrs = []
        for attr in block.get("attributes", []):
            original = str(attr.get("original_value", ""))
            value = str(attr.get("value", ""))
            if value == original:
                continue
            attrs.append(
                {
                    "tag": str(attr.get("tag", "")),
                    "tag_index": int(attr.get("tag_index", 0)),
                    "attribute_handle": str(attr.get("attribute_handle", "")),
                    "original_value": original,
                    "value": value,
                }
            )
        if attrs:
            blocks.append(
                {
                    "block_handle": str(block.get("block_handle", "")),
                    "block_name": str(block.get("block_name", "")),
                    "attributes": attrs,
                }
            )
    return {"force": force, "blocks": blocks}


def _headless_update_folder(
    folder: Path,
    json_path: Path,
    force: bool,
    visible: bool,
    log: Callable[[str], None],
) -> dict[str, Any]:
    del visible
    with json_path.open("r", encoding="utf-8-sig") as stream:
        data = json.load(stream)
    validate_payload(data)
    work_items = [item for item in data["files"] if requested_change_count(item)]
    if not work_items:
        raise RuntimeError("JSON 中没有检测到 value 的修改。")

    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    backup_root = folder / BACKUP_DIR_NAME / stamp
    report: dict[str, Any] = {
        "updated_at": now_iso(),
        "source_json": str(json_path.resolve()),
        "root_folder": str(folder.resolve()),
        "backup_folder": str(backup_root.resolve()),
        "force_overwrite_conflicts": force,
        "backend": "AutoCAD Core Console + C3DF-UPDATEATTRS",
        "files": [],
    }
    total_changed = 0

    with tempfile.TemporaryDirectory(prefix="c3df_dwg_attrs_", ignore_cleanup_errors=True) as temporary:
        workdir = Path(temporary)
        for index, file_item in enumerate(work_items, 1):
            relative = str(file_item.get("relative_path", ""))
            path = safe_relative_path(folder, relative)
            entry: dict[str, Any] = {"relative_path": relative, "changed": 0, "issues": []}
            report["files"].append(entry)
            log(f"[{index}/{len(work_items)}] 后台更新 {relative}")
            if not path.is_file():
                entry["issues"].append({"status": "dwg_not_found", "detail": str(path)})
                continue

            backup = backup_root / Path(relative.replace("/", os.sep))
            backup.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, backup)
            entry["backup"] = str(backup)

            temp_dwg = workdir / f"input_{index}.dwg"
            update_json = workdir / f"update_{index}.json"
            core_report = workdir / f"report_{index}.json"
            staged = path.with_suffix(path.suffix + ".c3df-new")
            try:
                shutil.copy2(path, temp_dwg)
                atomic_json_dump(_update_payload(file_item, force), update_json)
                run_core_console(
                    temp_dwg,
                    "C3DF-UPDATEATTRS",
                    workdir,
                    {
                        "C3DF_ATTR_UPDATE_JSON": str(update_json),
                        "C3DF_ATTR_REPORT_JSON": str(core_report),
                    },
                    save_after=True,
                )
                if not core_report.is_file():
                    raise RuntimeError("插件未生成写回报告。")
                with core_report.open("r", encoding="utf-8-sig") as stream:
                    core_result = json.load(stream)
                changed = int(core_result.get("changed", 0))
                issues = list(core_result.get("issues", []))
                entry["changed"] = changed
                entry["issues"].extend(issues)
                if changed:
                    shutil.copy2(temp_dwg, staged)
                    os.replace(staged, path)
                    total_changed += changed
                    log(f"    已保存，修改 {changed} 项")
                else:
                    log("    无可写入项，原 DWG 未替换")
            except Exception as exc:
                entry["issues"].append({"status": "file_failed", "detail": str(exc)})
                log(f"    [失败] {exc}")
            finally:
                try:
                    if staged.exists():
                        staged.unlink()
                except OSError:
                    pass

    report_path = json_path.with_name(f"{json_path.stem}_update_report_{stamp}.json")
    atomic_json_dump(report, report_path)
    issue_count = sum(len(item["issues"]) for item in report["files"])
    return {
        "file_count": len(work_items),
        "changed_count": total_changed,
        "issue_count": issue_count,
        "backup_root": str(backup_root),
        "report_path": str(report_path),
    }


def _normalize_export_blocks(raw_blocks: list[dict[str, Any]], block_filter: str) -> list[dict[str, Any]]:
    """Convert plug-in output into the editable, conflict-safe JSON shape."""
    blocks: list[dict[str, Any]] = []
    filter_folded = block_filter.casefold().strip()
    for block in raw_blocks:
        if filter_folded and filter_folded not in str(block.get("block_name", "")).casefold():
            continue
        attrs = []
        for attribute in block.get("attributes", []):
            value = str(attribute.get("value", ""))
            attrs.append(
                {
                    "tag": str(attribute.get("tag", "")),
                    "tag_index": int(attribute.get("tag_index", 0)),
                    "attribute_handle": str(attribute.get("attribute_handle", "")),
                    "original_value": value,
                    "value": value,
                }
            )
        if attrs:
            blocks.append(
                {
                    "space": str(block.get("space", "")),
                    "layout": str(block.get("layout", "")),
                    "block_name": str(block.get("block_name", "")),
                    "block_handle": str(block.get("block_handle", "")),
                    "attributes": attrs,
                }
            )
    return blocks


def _batch_export_folder(
    folder: Path,
    json_path: Path,
    recursive: bool,
    block_filter: str,
    visible: bool,
    log: Callable[[str], None],
) -> dict[str, Any]:
    del visible
    dwgs = discover_dwgs(folder, recursive)
    if not dwgs:
        raise RuntimeError("所选文件夹中没有找到 DWG。")

    # This tool is intentionally title-block-specific. Ignore callers that pass
    # a broader filter so unrelated attributed blocks never enter the JSON.
    block_filter = TITLE_BLOCK_FILTER
    result: dict[str, Any] = {
        "schema_version": SCHEMA_VERSION,
        "generated_at": now_iso(),
        "root_folder": str(folder.resolve()),
        "instructions": "只修改 attributes 中的 value；不要修改定位字段和 original_value。",
        "block_name_filter": block_filter,
        "backend": "AutoCAD Core Console（整批单次启动）+ C3DF-BATCHEXPORTATTRS",
        "files": [],
        "errors": [],
    }
    log(f"一次启动后台引擎，批量读取 {len(dwgs)} 个 DWG……")
    with tempfile.TemporaryDirectory(prefix="c3df_dwg_attrs_", ignore_cleanup_errors=True) as temporary:
        workdir = Path(temporary)
        batch_json = workdir / "batch_export.json"
        output_json = workdir / "batch_export_result.json"
        seed_dwg = workdir / "seed.dwg"
        shutil.copy2(dwgs[0], seed_dwg)
        jobs = [{"id": str(index), "path": str(path.resolve())} for index, path in enumerate(dwgs)]
        atomic_json_dump({"jobs": jobs, "block_name_contains": TITLE_BLOCK_FILTER}, batch_json)
        run_core_console(
            seed_dwg,
            "C3DF-BATCHEXPORTATTRS",
            workdir,
            {"C3DF_ATTR_BATCH_JSON": str(batch_json), "C3DF_ATTR_OUTPUT_JSON": str(output_json)},
            save_after=False,
        )
        if not output_json.is_file():
            raise RuntimeError("后台插件未生成批量属性 JSON。")
        with output_json.open("r", encoding="utf-8-sig") as stream:
            raw_result = json.load(stream)
        returned = {str(item.get("id", "")): item for item in raw_result.get("files", [])}

        for index, path in enumerate(dwgs):
            rel = path.relative_to(folder).as_posix()
            item = returned.get(str(index))
            if item is None:
                message = "后台批处理未返回该文件的结果。"
                result["errors"].append({"relative_path": rel, "error": message})
                log(f"[{index + 1}/{len(dwgs)}] [失败] {rel}：{message}")
                continue
            if item.get("error"):
                message = str(item["error"])
                result["errors"].append({"relative_path": rel, "error": message})
                log(f"[{index + 1}/{len(dwgs)}] [失败] {rel}：{message}")
                continue
            blocks = _normalize_export_blocks(list(item.get("blocks", [])), block_filter)
            stat = path.stat()
            result["files"].append(
                {
                    "relative_path": rel,
                    "source_size": stat.st_size,
                    "source_modified_at": datetime.fromtimestamp(stat.st_mtime).astimezone().isoformat(timespec="seconds"),
                    "blocks": blocks,
                }
            )
            log(f"[{index + 1}/{len(dwgs)}] {rel}：找到 {len(blocks)} 个带属性块")

    atomic_json_dump(result, json_path)
    block_count = sum(len(item["blocks"]) for item in result["files"])
    attr_count = sum(len(block["attributes"]) for item in result["files"] for block in item["blocks"])
    return {
        "dwg_count": len(result["files"]),
        "block_count": block_count,
        "attribute_count": attr_count,
        "error_count": len(result["errors"]),
        "json_path": str(json_path),
    }


def _batch_update_folder(
    folder: Path,
    json_path: Path,
    force: bool,
    visible: bool,
    log: Callable[[str], None],
) -> dict[str, Any]:
    del visible
    with json_path.open("r", encoding="utf-8-sig") as stream:
        data = json.load(stream)
    validate_payload(data)
    work_items = [item for item in data["files"] if requested_change_count(item)]
    if not work_items:
        raise RuntimeError("JSON 中没有检测到 value 的修改。")

    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    backup_root = folder / BACKUP_DIR_NAME / stamp
    report: dict[str, Any] = {
        "updated_at": now_iso(),
        "source_json": str(json_path.resolve()),
        "root_folder": str(folder.resolve()),
        "backup_folder": str(backup_root.resolve()),
        "force_overwrite_conflicts": force,
        "backend": "AutoCAD Core Console（整批单次启动）+ C3DF-BATCHUPDATEATTRS",
        "files": [],
    }
    total_changed = 0

    with tempfile.TemporaryDirectory(prefix="c3df_dwg_attrs_", ignore_cleanup_errors=True) as temporary:
        workdir = Path(temporary)
        jobs: list[dict[str, Any]] = []
        entries: dict[str, tuple[dict[str, Any], Path, Path]] = {}
        for index, file_item in enumerate(work_items):
            relative = str(file_item.get("relative_path", ""))
            path = safe_relative_path(folder, relative)
            entry: dict[str, Any] = {"relative_path": relative, "changed": 0, "issues": []}
            report["files"].append(entry)
            if not path.is_file():
                entry["issues"].append({"status": "dwg_not_found", "detail": str(path)})
                log(f"[{index + 1}/{len(work_items)}] [失败] {relative}：DWG 不存在")
                continue

            backup = backup_root / Path(relative.replace("/", os.sep))
            backup.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, backup)
            entry["backup"] = str(backup)
            input_dwg = workdir / f"input_{index}.dwg"
            output_dwg = workdir / f"output_{index}.dwg"
            shutil.copy2(path, input_dwg)
            job_id = str(index)
            payload = _update_payload(file_item, force)
            jobs.append(
                {
                    "id": job_id,
                    "input_dwg": str(input_dwg),
                    "output_dwg": str(output_dwg),
                    "force": force,
                    "blocks": payload["blocks"],
                }
            )
            entries[job_id] = (entry, path, output_dwg)

        if jobs:
            batch_json = workdir / "batch_update.json"
            core_report = workdir / "batch_update_result.json"
            seed_dwg = workdir / "seed.dwg"
            shutil.copy2(Path(jobs[0]["input_dwg"]), seed_dwg)
            atomic_json_dump({"jobs": jobs}, batch_json)
            log(f"一次启动后台引擎，批量更新 {len(jobs)} 个 DWG……")
            run_core_console(
                seed_dwg,
                "C3DF-BATCHUPDATEATTRS",
                workdir,
                {"C3DF_ATTR_BATCH_JSON": str(batch_json), "C3DF_ATTR_REPORT_JSON": str(core_report)},
                save_after=False,
            )
            if not core_report.is_file():
                raise RuntimeError("后台插件未生成批量写回报告。")
            with core_report.open("r", encoding="utf-8-sig") as stream:
                core_result = json.load(stream)
            returned = {str(item.get("id", "")): item for item in core_result.get("files", [])}
            for job_id, (entry, path, output_dwg) in entries.items():
                item = returned.get(job_id)
                if item is None:
                    entry["issues"].append({"status": "file_failed", "detail": "后台批处理未返回该文件的结果。"})
                    continue
                if item.get("error"):
                    entry["issues"].append({"status": "file_failed", "detail": str(item["error"])})
                    log(f"    [失败] {entry['relative_path']}：{item['error']}")
                    continue
                changed = int(item.get("changed", 0))
                entry["changed"] = changed
                entry["issues"].extend(list(item.get("issues", [])))
                if changed:
                    if not output_dwg.is_file():
                        entry["issues"].append({"status": "file_failed", "detail": "后台未生成更新后的 DWG。"})
                        continue
                    staged = path.with_suffix(path.suffix + ".c3df-new")
                    try:
                        shutil.copy2(output_dwg, staged)
                        os.replace(staged, path)
                    finally:
                        if staged.exists():
                            staged.unlink()
                    total_changed += changed
                    log(f"    {entry['relative_path']}：已保存，修改 {changed} 项")
                else:
                    log(f"    {entry['relative_path']}：无可写入项，原 DWG 未替换")

    report_path = json_path.with_name(f"{json_path.stem}_update_report_{stamp}.json")
    atomic_json_dump(report, report_path)
    issue_count = sum(len(item["issues"]) for item in report["files"])
    return {
        "file_count": len(work_items),
        "changed_count": total_changed,
        "issue_count": issue_count,
        "backup_root": str(backup_root),
        "report_path": str(report_path),
    }


# Public names used by the GUI. Keep the old per-file implementation above as a
# fallback reference; the production path starts Core Console only once per batch.
export_folder = _batch_export_folder
update_folder = _batch_update_folder


def default_folder() -> Path:
    candidates = [Path.cwd() / "cad"]
    base = Path(getattr(sys, "executable", __file__)).resolve().parent
    if not getattr(sys, "frozen", False):
        base = Path(__file__).resolve().parent
    candidates.extend([base / "cad", base.parent / "cad"])
    for candidate in candidates:
        if candidate.is_dir():
            return candidate
    return Path.cwd()


class LegacyTkApp:
    """保留的界面布局参考；当前运行环境不使用 Tcl/Tk。"""

    def __init__(self) -> None:
        super().__init__()
        self.title(APP_NAME)
        self.geometry("860x650")
        self.minsize(760, 560)
        self.folder_var = tk.StringVar(value=str(default_folder()))
        self.json_var = tk.StringVar()
        self.recursive_var = tk.BooleanVar(value=True)
        self.visible_var = tk.BooleanVar(value=False)
        self.force_var = tk.BooleanVar(value=False)
        self.filter_var = tk.StringVar(value=TITLE_BLOCK_FILTER)
        self.status_var = tk.StringVar(value="就绪")
        self.events: queue.Queue[tuple[str, Any]] = queue.Queue()
        self._busy = False
        self._build_ui()
        self._suggest_json_path()
        self.after(100, self._poll_events)

    def _build_ui(self) -> None:
        root = ttk.Frame(self, padding=14)
        root.pack(fill="both", expand=True)
        root.columnconfigure(1, weight=1)
        root.rowconfigure(7, weight=1)

        ttk.Label(root, text="DWG 文件夹").grid(row=0, column=0, sticky="w", pady=5)
        ttk.Entry(root, textvariable=self.folder_var).grid(row=0, column=1, sticky="ew", padx=8)
        ttk.Button(root, text="选择…", command=self._choose_folder).grid(row=0, column=2)

        ttk.Label(root, text="属性 JSON").grid(row=1, column=0, sticky="w", pady=5)
        ttk.Entry(root, textvariable=self.json_var).grid(row=1, column=1, sticky="ew", padx=8)
        ttk.Button(root, text="选择…", command=self._choose_json).grid(row=1, column=2)

        ttk.Label(root, text="块名包含").grid(row=2, column=0, sticky="w", pady=5)
        ttk.Entry(root, textvariable=self.filter_var).grid(row=2, column=1, sticky="ew", padx=8)
        ttk.Label(root, text="留空 = 所有带属性块").grid(row=2, column=2, sticky="w")

        options = ttk.Frame(root)
        options.grid(row=3, column=0, columnspan=3, sticky="w", pady=(6, 10))
        ttk.Checkbutton(options, text="包含子文件夹", variable=self.recursive_var).pack(side="left", padx=(0, 16))
        ttk.Checkbutton(options, text="显示 AutoCAD 窗口", variable=self.visible_var).pack(side="left", padx=(0, 16))
        ttk.Checkbutton(options, text="冲突时强制覆盖", variable=self.force_var).pack(side="left")

        buttons = ttk.Frame(root)
        buttons.grid(row=4, column=0, columnspan=3, sticky="ew", pady=4)
        self.export_button = ttk.Button(buttons, text="1  导出属性到 JSON", command=self._export)
        self.export_button.pack(side="left")
        self.open_button = ttk.Button(buttons, text="2  打开 JSON 编辑", command=self._open_json)
        self.open_button.pack(side="left", padx=8)
        self.update_button = ttk.Button(buttons, text="3  将修改写回 DWG", command=self._update)
        self.update_button.pack(side="left")

        note = (
            "编辑规则：只改 JSON 中 attributes → value 的内容。写回前会备份原 DWG；"
            "若 DWG 当前值与导出时 original_value 不同，默认跳过并记入报告。"
        )
        ttk.Label(root, text=note, wraplength=800, foreground="#444").grid(
            row=5, column=0, columnspan=3, sticky="w", pady=(8, 5)
        )

        ttk.Separator(root).grid(row=6, column=0, columnspan=3, sticky="ew", pady=6)
        log_frame = ttk.Frame(root)
        log_frame.grid(row=7, column=0, columnspan=3, sticky="nsew")
        log_frame.columnconfigure(0, weight=1)
        log_frame.rowconfigure(0, weight=1)
        self.log_text = tk.Text(log_frame, wrap="word", state="disabled", font=("Consolas", 10))
        self.log_text.grid(row=0, column=0, sticky="nsew")
        scroll = ttk.Scrollbar(log_frame, orient="vertical", command=self.log_text.yview)
        scroll.grid(row=0, column=1, sticky="ns")
        self.log_text.configure(yscrollcommand=scroll.set)
        ttk.Label(root, textvariable=self.status_var).grid(row=8, column=0, columnspan=3, sticky="w", pady=(7, 0))

    def _choose_folder(self) -> None:
        selected = filedialog.askdirectory(initialdir=self.folder_var.get() or str(Path.cwd()))
        if selected:
            self.folder_var.set(selected)
            self._suggest_json_path()

    def _choose_json(self) -> None:
        selected = filedialog.askopenfilename(
            title="选择属性 JSON",
            initialdir=self.folder_var.get() or str(Path.cwd()),
            filetypes=[("JSON 文件", "*.json"), ("所有文件", "*.*")],
        )
        if selected:
            self.json_var.set(selected)

    def _suggest_json_path(self) -> None:
        folder = Path(self.folder_var.get().strip() or Path.cwd())
        self.json_var.set(str(folder / "dwg_attributes.json"))

    def _open_json(self) -> None:
        path = Path(self.json_var.get().strip())
        if not path.is_file():
            messagebox.showerror(APP_NAME, "JSON 文件不存在，请先导出。")
            return
        os.startfile(path)  # type: ignore[attr-defined]

    def _validated_paths(self, require_json: bool = False) -> tuple[Path, Path] | None:
        folder = Path(self.folder_var.get().strip()).expanduser()
        json_path = Path(self.json_var.get().strip()).expanduser()
        if not folder.is_dir():
            messagebox.showerror(APP_NAME, "请选择有效的 DWG 文件夹。")
            return None
        if not json_path.name:
            messagebox.showerror(APP_NAME, "请选择 JSON 文件。")
            return None
        if require_json and not json_path.is_file():
            messagebox.showerror(APP_NAME, "JSON 文件不存在，请先导出或重新选择。")
            return None
        return folder.resolve(), json_path.resolve()

    def _export(self) -> None:
        paths = self._validated_paths()
        if paths is None:
            return
        folder, json_path = paths
        self._run_worker(
            lambda: export_folder(
                folder,
                json_path,
                self.recursive_var.get(),
                self.filter_var.get(),
                self.visible_var.get(),
                self._thread_log,
            ),
            self._export_done,
        )

    def _update(self) -> None:
        paths = self._validated_paths(require_json=True)
        if paths is None:
            return
        folder, json_path = paths
        if not messagebox.askyesno(
            APP_NAME,
            "即将把 JSON 中修改过的 value 写回 DWG。\n程序会先备份原文件。是否继续？",
        ):
            return
        self._run_worker(
            lambda: update_folder(
                folder,
                json_path,
                self.force_var.get(),
                self.visible_var.get(),
                self._thread_log,
            ),
            self._update_done,
        )

    def _run_worker(self, work: Callable[[], dict[str, Any]], done: Callable[[dict[str, Any]], None]) -> None:
        if self._busy:
            return
        self._set_busy(True)
        self._append_log("-" * 70)

        def target() -> None:
            try:
                result = work()
                self.events.put(("done", (done, result)))
            except Exception:
                self.events.put(("error", traceback.format_exc()))

        threading.Thread(target=target, daemon=True).start()

    def _thread_log(self, message: str) -> None:
        self.events.put(("log", message))

    def _poll_events(self) -> None:
        try:
            while True:
                kind, payload = self.events.get_nowait()
                if kind == "log":
                    self._append_log(str(payload))
                elif kind == "done":
                    callback, result = payload
                    self._set_busy(False)
                    callback(result)
                elif kind == "error":
                    self._set_busy(False)
                    self._append_log(str(payload))
                    last_line = str(payload).strip().splitlines()[-1]
                    messagebox.showerror(APP_NAME, last_line)
        except queue.Empty:
            pass
        self.after(100, self._poll_events)

    def _set_busy(self, busy: bool) -> None:
        self._busy = busy
        state = "disabled" if busy else "normal"
        self.export_button.configure(state=state)
        self.update_button.configure(state=state)
        self.status_var.set("处理中，请等待 AutoCAD 完成……" if busy else "就绪")

    def _append_log(self, message: str) -> None:
        self.log_text.configure(state="normal")
        self.log_text.insert("end", message.rstrip() + "\n")
        self.log_text.see("end")
        self.log_text.configure(state="disabled")

    def _export_done(self, result: dict[str, Any]) -> None:
        summary = (
            f"导出完成：{result['dwg_count']} 个 DWG，{result['block_count']} 个块，"
            f"{result['attribute_count']} 条属性；失败 {result['error_count']} 个。"
        )
        self._append_log(summary)
        messagebox.showinfo(APP_NAME, summary + f"\n\n{result['json_path']}")

    def _update_done(self, result: dict[str, Any]) -> None:
        summary = (
            f"写回完成：处理 {result['file_count']} 个 DWG，修改 {result['changed_count']} 条属性，"
            f"问题 {result['issue_count']} 项。"
        )
        self._append_log(summary)
        messagebox.showinfo(
            APP_NAME,
            summary + f"\n\n备份：{result['backup_root']}\n报告：{result['report_path']}",
        )


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


# ---------------------------------------------------------------------------
# 表格编辑层：导出 JSON 的可视化编辑视图，只改 attributes → value。
# ---------------------------------------------------------------------------

MULTILINE_TAG_HINT = "说明"
INFO_HEADERS = ("文件", "布局")
COLOR_MODIFIED = "#ffe9a8"
COLOR_MISSING = "#ececec"
COLOR_INFO = "#f4f4f4"


def mtext_to_plain(value: str) -> str:
    return value.replace("\\P", "\n")


def plain_to_mtext(text: str) -> str:
    return text.replace("\r\n", "\n").replace("\r", "\n").replace("\n", "\\P")


def display_preview(value: str, limit: int = 80) -> str:
    text = value.replace("\\P", " ⏎ ")
    return text if len(text) <= limit else text[: limit - 1] + "…"


def payload_modified_count(data: dict[str, Any]) -> int:
    return sum(requested_change_count(item) for item in data.get("files", []))


class TableColumn:
    def __init__(self, key: str, tag: str, tag_index: int):
        self.key = key
        self.tag = tag
        self.tag_index = tag_index
        # 2026-09-02 图框 2.0 起说明拆成 说明1~说明7 七个单行属性，只有老块的「说明」才是多行——按全等判，别按子串
        self.multiline = tag == MULTILINE_TAG_HINT

    @property
    def label(self) -> str:
        return self.tag if self.tag_index == 0 else f"{self.tag}#{self.tag_index + 1}"


class TableRow:
    def __init__(self, file_item: dict[str, Any], block: dict[str, Any]):
        self.file_item = file_item
        self.block = block
        self.cells: dict[str, dict[str, Any]] = {}


def build_table(
    data: dict[str, Any],
) -> tuple[list[TableRow], list[TableColumn], list[TableColumn]]:
    """把导出 JSON 摊成 行=图框实例、列=属性标签。

    返回 (行, 表格列, 公共列)：所有图框都有且取值完全相同的单行属性提为公共列，
    在面板里填一次应用到全部；多行“说明”永远留在表格里逐张改。
    """
    rows: list[TableRow] = []
    order: dict[str, TableColumn] = {}
    for file_item in data.get("files", []):
        for block in file_item.get("blocks", []):
            row = TableRow(file_item, block)
            for record in block.get("attributes", []):
                tag = str(record.get("tag", ""))
                index = int(record.get("tag_index", 0))
                key = f"{tag}\x00{index}"
                if key not in order:
                    order[key] = TableColumn(key, tag, index)
                row.cells[key] = record
            rows.append(row)

    table_columns: list[TableColumn] = []
    common_columns: list[TableColumn] = []
    for column in order.values():
        if column.multiline or len(rows) < 2:
            table_columns.append(column)
            continue
        if all(column.key in row.cells for row in rows):
            values = {str(row.cells[column.key].get("value", "")) for row in rows}
            if len(values) == 1:
                common_columns.append(column)
                continue
        table_columns.append(column)
    return rows, table_columns, common_columns


class AttributeTableModel(QAbstractTableModel):
    def __init__(self, parent: Any = None):
        super().__init__(parent)
        self.rows: list[TableRow] = []
        self.columns: list[TableColumn] = []

    def load(self, rows: list[TableRow], columns: list[TableColumn]) -> None:
        self.beginResetModel()
        self.rows = rows
        self.columns = columns
        self.endResetModel()

    def rowCount(self, parent: QModelIndex = QModelIndex()) -> int:
        return 0 if parent.isValid() else len(self.rows)

    def columnCount(self, parent: QModelIndex = QModelIndex()) -> int:
        return 0 if parent.isValid() else len(INFO_HEADERS) + len(self.columns)

    def column_at(self, column: int) -> TableColumn | None:
        offset = column - len(INFO_HEADERS)
        return self.columns[offset] if 0 <= offset < len(self.columns) else None

    def record_at(self, index: QModelIndex) -> dict[str, Any] | None:
        if not index.isValid() or not (0 <= index.row() < len(self.rows)):
            return None
        column = self.column_at(index.column())
        if column is None:
            return None
        return self.rows[index.row()].cells.get(column.key)

    def headerData(self, section: int, orientation: Qt.Orientation, role: int = Qt.DisplayRole) -> Any:
        if role != Qt.DisplayRole:
            return None
        if orientation == Qt.Horizontal:
            if section < len(INFO_HEADERS):
                return INFO_HEADERS[section]
            column = self.column_at(section)
            return column.label if column else None
        return str(section + 1)

    def data(self, index: QModelIndex, role: int = Qt.DisplayRole) -> Any:
        if not index.isValid():
            return None
        row = self.rows[index.row()]
        if index.column() < len(INFO_HEADERS):
            if role in (Qt.DisplayRole, Qt.ToolTipRole):
                if index.column() == 0:
                    return str(row.file_item.get("relative_path", ""))
                return str(row.block.get("layout", ""))
            if role == Qt.BackgroundRole:
                return QColor(COLOR_INFO)
            return None
        column = self.column_at(index.column())
        record = row.cells.get(column.key) if column else None
        if record is None:
            if role == Qt.BackgroundRole:
                return QColor(COLOR_MISSING)
            if role == Qt.ToolTipRole:
                return "该图框没有此属性"
            return None
        value = str(record.get("value", ""))
        original = str(record.get("original_value", ""))
        if role == Qt.DisplayRole:
            return display_preview(value) if column.multiline else value
        if role == Qt.EditRole:
            return value
        if role == Qt.BackgroundRole and value != original:
            return QColor(COLOR_MODIFIED)
        if role == Qt.ToolTipRole:
            lines = []
            if column.multiline:
                lines.append(mtext_to_plain(value))
                lines.append("（双击编辑多行文本）")
            if value != original:
                lines.append(f"导出值：{display_preview(original, 200) if column.multiline else original}")
            return "\n".join(lines) or None
        return None

    def flags(self, index: QModelIndex) -> Qt.ItemFlags:
        base = Qt.ItemIsEnabled | Qt.ItemIsSelectable
        column = self.column_at(index.column())
        if column is not None and not column.multiline and self.record_at(index) is not None:
            base |= Qt.ItemIsEditable
        return base

    def setData(self, index: QModelIndex, value: Any, role: int = Qt.EditRole) -> bool:
        if role != Qt.EditRole:
            return False
        record = self.record_at(index)
        if record is None:
            return False
        text = str(value)
        if text == str(record.get("value", "")):
            return False
        record["value"] = text
        self.dataChanged.emit(index, index, [Qt.DisplayRole, Qt.BackgroundRole, Qt.ToolTipRole])
        return True

    def set_value(self, index: QModelIndex, text: str) -> bool:
        return self.setData(index, text, Qt.EditRole)

    def revert(self, indexes: Iterable[QModelIndex]) -> int:
        count = 0
        for index in indexes:
            record = self.record_at(index)
            if record is None:
                continue
            original = str(record.get("original_value", ""))
            if str(record.get("value", "")) != original:
                record["value"] = original
                self.dataChanged.emit(index, index, [Qt.DisplayRole, Qt.BackgroundRole, Qt.ToolTipRole])
                count += 1
        return count

    def refresh_all(self) -> None:
        if self.rows:
            top = self.index(0, 0)
            bottom = self.index(self.rowCount() - 1, self.columnCount() - 1)
            self.dataChanged.emit(top, bottom, [Qt.DisplayRole, Qt.BackgroundRole, Qt.ToolTipRole])


class MultilineDialog(QDialog):
    def __init__(self, parent: QWidget, title: str, text: str):
        super().__init__(parent)
        self.setWindowTitle(title)
        self.resize(560, 380)
        layout = QVBoxLayout(self)
        hint = QLabel("直接换行即可，保存时自动转成 CAD 多行码 \\P，无需手写转义。")
        hint.setStyleSheet("color: #666;")
        layout.addWidget(hint)
        self.editor = QPlainTextEdit(text)
        layout.addWidget(self.editor, 1)
        buttons = QDialogButtonBox(QDialogButtonBox.Ok | QDialogButtonBox.Cancel)
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)

    def text(self) -> str:
        return self.editor.toPlainText()


class QtApp(QMainWindow):
    def __init__(self) -> None:
        super().__init__()
        self.setWindowTitle(APP_NAME)
        self.resize(1180, 780)
        self.setMinimumSize(900, 620)
        self.pool = QThreadPool.globalInstance()
        self.busy = False
        self.table_data: dict[str, Any] | None = None
        self.table_json_path: Path | None = None
        self.common_fields: list[tuple[TableColumn, QLineEdit]] = []
        self.model = AttributeTableModel(self)
        self._build_ui()
        self._suggest_json_path()

    def _build_ui(self) -> None:
        central = QWidget()
        self.setCentralWidget(central)
        outer = QVBoxLayout(central)
        outer.setContentsMargins(14, 14, 14, 14)

        grid = QGridLayout()
        grid.setColumnStretch(1, 1)
        outer.addLayout(grid)

        self.folder_edit = QLineEdit(str(default_folder()))
        folder_button = QPushButton("选择…")
        folder_button.clicked.connect(self._choose_folder)
        grid.addWidget(QLabel("DWG 文件夹"), 0, 0)
        grid.addWidget(self.folder_edit, 0, 1)
        grid.addWidget(folder_button, 0, 2)

        self.json_edit = QLineEdit()
        json_button = QPushButton("选择…")
        json_button.clicked.connect(self._choose_json)
        grid.addWidget(QLabel("属性 JSON"), 1, 0)
        grid.addWidget(self.json_edit, 1, 1)
        grid.addWidget(json_button, 1, 2)

        self.filter_edit = QLineEdit(TITLE_BLOCK_FILTER)
        self.filter_edit.setReadOnly(True)
        grid.addWidget(QLabel("块名包含"), 2, 0)
        grid.addWidget(self.filter_edit, 2, 1)
        grid.addWidget(QLabel("留空 = 所有带属性块"), 2, 2)

        options = QHBoxLayout()
        self.recursive_check = QCheckBox("包含子文件夹")
        self.recursive_check.setChecked(True)
        self.force_check = QCheckBox("冲突时强制覆盖")
        options.addWidget(self.recursive_check)
        options.addWidget(self.force_check)
        options.addStretch(1)
        outer.addLayout(options)

        actions = QHBoxLayout()
        self.export_button = QPushButton("1  导出属性并载入表格")
        self.update_button = QPushButton("2  将修改写回 DWG")
        self.load_button = QPushButton("载入已有 JSON")
        self.open_button = QPushButton("文本方式打开 JSON")
        self.export_button.clicked.connect(self._export)
        self.update_button.clicked.connect(self._update)
        self.load_button.clicked.connect(self._load_json_clicked)
        self.open_button.clicked.connect(self._open_json)
        actions.addWidget(self.export_button)
        actions.addWidget(self.update_button)
        actions.addSpacing(24)
        actions.addWidget(self.load_button)
        actions.addWidget(self.open_button)
        actions.addStretch(1)
        outer.addLayout(actions)

        note = QLabel(
            "完全后台运行，无需手动打开 CAD。导出后直接在下方表格里改：公共字段填一次应用到全部图框；"
            "「说明」列双击弹出多行编辑框（换行自动转 \\P）；改过的格子黄色高亮，右键可还原。"
            "写回前自动保存 JSON 并备份原 DWG；若 DWG 当前值与导出基准不同，默认跳过并记入报告。"
        )
        note.setWordWrap(True)
        note.setStyleSheet("color: #444;")
        outer.addWidget(note)

        self.tabs = QTabWidget()
        outer.addWidget(self.tabs, 1)

        editor_page = QWidget()
        editor_layout = QVBoxLayout(editor_page)
        editor_layout.setContentsMargins(6, 6, 6, 6)

        self.common_box = QGroupBox("公共字段（全部图框取值相同，改一处应用到全部）")
        box_layout = QVBoxLayout(self.common_box)
        common_inner = QWidget()
        forms_layout = QHBoxLayout(common_inner)
        forms_layout.setContentsMargins(0, 0, 0, 0)
        self.common_forms = [QFormLayout(), QFormLayout()]
        for form in self.common_forms:
            forms_layout.addLayout(form, 1)
        self.common_scroll = QScrollArea()
        self.common_scroll.setWidget(common_inner)
        self.common_scroll.setWidgetResizable(True)
        self.common_scroll.setMaximumHeight(220)
        self.common_scroll.setFrameShape(QScrollArea.NoFrame)
        box_layout.addWidget(self.common_scroll)
        self.common_box.setVisible(False)
        editor_layout.addWidget(self.common_box)

        search_bar = QHBoxLayout()
        search_bar.addWidget(QLabel("查找"))
        self.find_edit = QLineEdit()
        self.find_edit.setPlaceholderText("要查找的文本（区分大小写）")
        search_bar.addWidget(self.find_edit, 2)
        search_bar.addWidget(QLabel("替换为"))
        self.replace_edit = QLineEdit()
        search_bar.addWidget(self.replace_edit, 2)
        self.scope_combo = QComboBox()
        self.scope_combo.addItems(["全表", "当前列", "选中单元格"])
        search_bar.addWidget(self.scope_combo)
        self.replace_button = QPushButton("全部替换")
        search_bar.addWidget(self.replace_button)
        self.match_label = QLabel("")
        self.match_label.setMinimumWidth(120)
        search_bar.addWidget(self.match_label)
        editor_layout.addLayout(search_bar)

        self.table = QTableView()
        self.table.setModel(self.model)
        self.table.setSelectionMode(QAbstractItemView.ExtendedSelection)
        self.table.setContextMenuPolicy(Qt.CustomContextMenu)
        self.table.customContextMenuRequested.connect(self._table_menu)
        self.table.doubleClicked.connect(self._table_double_clicked)
        self.table.horizontalHeader().setDefaultSectionSize(140)
        editor_layout.addWidget(self.table, 1)
        self.tabs.addTab(editor_page, "表格编辑")

        self.log_text = QTextEdit()
        self.log_text.setReadOnly(True)
        self.log_text.setLineWrapMode(QTextEdit.WidgetWidth)
        self.tabs.addTab(self.log_text, "运行日志")

        self.find_edit.textChanged.connect(self._update_match_count)
        self.scope_combo.currentIndexChanged.connect(self._update_match_count)
        self.replace_button.clicked.connect(self._replace_all)
        self.model.dataChanged.connect(self._refresh_status)
        self.table.selectionModel().selectionChanged.connect(
            lambda *_: self._update_match_count()
        )

        self.status_label = QLabel("就绪")
        outer.addWidget(self.status_label)

    def _choose_folder(self) -> None:
        selected = QFileDialog.getExistingDirectory(self, "选择 DWG 文件夹", self.folder_edit.text())
        if selected:
            self.folder_edit.setText(selected)
            self._suggest_json_path()

    def _choose_json(self) -> None:
        selected, _ = QFileDialog.getOpenFileName(
            self, "选择属性 JSON", self.folder_edit.text(), "JSON 文件 (*.json);;所有文件 (*.*)"
        )
        if selected:
            self.json_edit.setText(selected)

    def _suggest_json_path(self) -> None:
        folder = Path(self.folder_edit.text().strip() or Path.cwd())
        self.json_edit.setText(str(folder / "dwg_attributes.json"))

    def _open_json(self) -> None:
        path = Path(self.json_edit.text().strip())
        if not path.is_file():
            QMessageBox.critical(self, APP_NAME, "JSON 文件不存在，请先导出。")
            return
        os.startfile(path)  # type: ignore[attr-defined]

    def _validated_paths(self, require_json: bool = False) -> tuple[Path, Path] | None:
        folder = Path(self.folder_edit.text().strip()).expanduser()
        json_path = Path(self.json_edit.text().strip()).expanduser()
        if not folder.is_dir():
            QMessageBox.critical(self, APP_NAME, "请选择有效的 DWG 文件夹。")
            return None
        if not json_path.name:
            QMessageBox.critical(self, APP_NAME, "请选择 JSON 文件。")
            return None
        if require_json and not json_path.is_file():
            QMessageBox.critical(self, APP_NAME, "JSON 文件不存在，请先导出或重新选择。")
            return None
        return folder.resolve(), json_path.resolve()

    def _export(self) -> None:
        paths = self._validated_paths()
        if paths is None:
            return
        folder, json_path = paths
        recursive = self.recursive_check.isChecked()
        block_filter = self.filter_edit.text()
        self._run_worker(
            lambda: export_folder(folder, json_path, recursive, block_filter, False, self._thread_log),
            self._export_done,
        )

    def _update(self) -> None:
        if self.table_data is not None:
            folder = Path(str(self.table_data.get("root_folder", ""))).expanduser()
            json_path = self.table_json_path
            if not folder.is_dir() or json_path is None:
                QMessageBox.critical(self, APP_NAME, "表格对应的 DWG 文件夹已不存在，请重新导出。")
                return
            modified = payload_modified_count(self.table_data)
            if modified == 0:
                QMessageBox.information(self, APP_NAME, "表格里没有改动，无需写回。")
                return
            try:
                atomic_json_dump(self.table_data, json_path)
            except Exception as exc:
                QMessageBox.critical(self, APP_NAME, f"保存 JSON 失败：{exc}")
                return
            self.json_edit.setText(str(json_path))
            question = f"即将把表格中的 {modified} 条改动写回 DWG。\n程序会先备份原文件。是否继续？"
        else:
            paths = self._validated_paths(require_json=True)
            if paths is None:
                return
            folder, json_path = paths
            question = "即将把 JSON 中修改过的 value 写回 DWG。\n程序会先备份原文件。是否继续？"
        answer = QMessageBox.question(self, APP_NAME, question)
        if answer != QMessageBox.Yes:
            return
        force = self.force_check.isChecked()
        self._run_worker(
            lambda: update_folder(folder, json_path, force, False, self._thread_log),
            self._update_done,
        )

    # ------------------------------------------------------------------
    # 表格编辑
    # ------------------------------------------------------------------

    def _load_json_clicked(self) -> None:
        selected, _ = QFileDialog.getOpenFileName(
            self, "选择属性 JSON", self.folder_edit.text(), "JSON 文件 (*.json);;所有文件 (*.*)"
        )
        if not selected:
            return
        self.json_edit.setText(selected)
        self._load_table(Path(selected))

    def _load_table(self, json_path: Path) -> None:
        try:
            with json_path.open("r", encoding="utf-8-sig") as stream:
                data = json.load(stream)
            validate_payload(data)
        except Exception as exc:
            QMessageBox.critical(self, APP_NAME, f"载入 JSON 失败：{exc}")
            return
        rows, table_columns, common_columns = build_table(data)
        if not rows:
            QMessageBox.warning(self, APP_NAME, "JSON 里没有任何图框属性。")
            return
        self.table_data = data
        self.table_json_path = json_path
        self.model.load(rows, table_columns)
        self._rebuild_common_panel(common_columns)
        self.table.resizeColumnsToContents()
        header = self.table.horizontalHeader()
        for section in range(self.model.columnCount()):
            header.resizeSection(section, min(header.sectionSize(section), 320))
        self.tabs.setCurrentIndex(0)
        self._append_log(
            f"表格已载入：{len(rows)} 个图框 × {len(table_columns)} 列，公共字段 {len(common_columns)} 个。"
        )
        self._update_match_count()
        self._refresh_status()

    def _rebuild_common_panel(self, columns: list[TableColumn]) -> None:
        for form in self.common_forms:
            while form.rowCount():
                form.removeRow(0)
        self.common_fields = []
        usable = [
            (column, record)
            for column in columns
            if (
                record := next(
                    (row.cells[column.key] for row in self.model.rows if column.key in row.cells),
                    None,
                )
            )
            is not None
        ]
        # 左右两列、按列优先排布：前一半在左、后一半在右，读起来自上而下。
        half = (len(usable) + 1) // 2
        for position, (column, record) in enumerate(usable):
            form = self.common_forms[0] if position < half else self.common_forms[1]
            edit = QLineEdit(str(record.get("value", "")))
            edit.editingFinished.connect(
                lambda col=column, widget=edit: self._apply_common_field(col, widget)
            )
            form.addRow(column.label, edit)
            self.common_fields.append((column, edit))
            self._style_common_field(column, edit)
        # QScrollArea 的默认 sizeHint 很小，会把面板压到只露两行；按内容行数给足高度。
        row_height = 34
        self.common_scroll.setMinimumHeight(min(half * row_height + 12, 220) if usable else 0)
        self.common_box.setVisible(bool(self.common_fields))

    def _apply_common_field(self, column: TableColumn, edit: QLineEdit) -> None:
        text = edit.text()
        changed = False
        for row in self.model.rows:
            record = row.cells.get(column.key)
            if record is not None and str(record.get("value", "")) != text:
                record["value"] = text
                changed = True
        self._style_common_field(column, edit)
        if changed:
            self._refresh_status()
            self._update_match_count()

    def _style_common_field(self, column: TableColumn, edit: QLineEdit) -> None:
        modified = any(
            str(row.cells[column.key].get("value", ""))
            != str(row.cells[column.key].get("original_value", ""))
            for row in self.model.rows
            if column.key in row.cells
        )
        edit.setStyleSheet(f"background: {COLOR_MODIFIED};" if modified else "")

    def _scope_indexes(self) -> list[QModelIndex]:
        scope = self.scope_combo.currentText()
        if scope == "选中单元格":
            selection = self.table.selectionModel()
            if selection is None:
                return []
            return [i for i in selection.selectedIndexes() if self.model.record_at(i) is not None]
        if scope == "当前列":
            current = self.table.currentIndex()
            if not current.isValid() or self.model.column_at(current.column()) is None:
                return []
            return [
                self.model.index(row, current.column())
                for row in range(self.model.rowCount())
                if self.model.record_at(self.model.index(row, current.column())) is not None
            ]
        indexes: list[QModelIndex] = []
        for row in range(self.model.rowCount()):
            for col in range(len(INFO_HEADERS), self.model.columnCount()):
                index = self.model.index(row, col)
                if self.model.record_at(index) is not None:
                    indexes.append(index)
        return indexes

    def _scope_targets(self) -> tuple[list[QModelIndex], list[tuple[TableColumn, QLineEdit]]]:
        common = self.common_fields if self.scope_combo.currentText() == "全表" else []
        return self._scope_indexes(), common

    def _update_match_count(self) -> None:
        term = self.find_edit.text()
        if not term or self.table_data is None:
            self.match_label.setText("")
            return
        indexes, common = self._scope_targets()
        hits = cells = 0
        for index in indexes:
            record = self.model.record_at(index)
            found = str(record.get("value", "")).count(term)
            if found:
                hits += found
                cells += 1
        for _column, edit in common:
            found = edit.text().count(term)
            if found:
                hits += found
                cells += 1
        self.match_label.setText(f"命中 {hits} 处 / {cells} 格" if hits else "无命中")

    def _replace_all(self) -> None:
        if self.table_data is None:
            QMessageBox.information(self, APP_NAME, "请先导出或载入 JSON。")
            return
        term = self.find_edit.text()
        if not term:
            QMessageBox.information(self, APP_NAME, "请先输入要查找的文本。")
            return
        replacement = self.replace_edit.text()
        indexes, common = self._scope_targets()
        hits = cells = 0
        for index in indexes:
            record = self.model.record_at(index)
            value = str(record.get("value", ""))
            found = value.count(term)
            if found:
                self.model.set_value(index, value.replace(term, replacement))
                hits += found
                cells += 1
        for column, edit in common:
            value = edit.text()
            found = value.count(term)
            if found:
                edit.setText(value.replace(term, replacement))
                self._apply_common_field(column, edit)
                hits += found
                cells += 1
        self._append_log(
            f"搜索替换：「{term}」→「{replacement}」，替换 {hits} 处 / {cells} 格"
            f"（范围：{self.scope_combo.currentText()}）。"
        )
        if not hits:
            QMessageBox.information(self, APP_NAME, "当前范围内没有命中。")
            self._update_match_count()
        else:
            self.match_label.setText(f"已替换 {hits} 处")
        self._refresh_status()

    def _table_double_clicked(self, index: QModelIndex) -> None:
        column = self.model.column_at(index.column())
        if column is None or not column.multiline:
            return
        record = self.model.record_at(index)
        if record is None:
            return
        dialog = MultilineDialog(
            self,
            f"{column.label} — 第 {index.row() + 1} 行",
            mtext_to_plain(str(record.get("value", ""))),
        )
        if dialog.exec() == QDialog.Accepted:
            self.model.set_value(index, plain_to_mtext(dialog.text()))

    def _table_menu(self, pos: Any) -> None:
        index = self.table.indexAt(pos)
        menu = QMenu(self)
        revert_action = menu.addAction("还原选中格为导出值")
        edit_action = None
        column = self.model.column_at(index.column()) if index.isValid() else None
        if column is not None and column.multiline and self.model.record_at(index) is not None:
            edit_action = menu.addAction("编辑多行文本…")
        chosen = menu.exec(self.table.viewport().mapToGlobal(pos))
        if chosen is None:
            return
        if chosen is revert_action:
            selection = self.table.selectionModel()
            indexes = selection.selectedIndexes() if selection else []
            if not indexes and index.isValid():
                indexes = [index]
            count = self.model.revert(indexes)
            if count:
                self._append_log(f"已还原 {count} 格为导出值。")
            self._refresh_status()
            self._update_match_count()
        elif chosen is edit_action:
            self._table_double_clicked(index)

    def _refresh_status(self, *_args: Any) -> None:
        if self.busy:
            return
        if self.table_data is None:
            self.status_label.setText("就绪")
            return
        modified = payload_modified_count(self.table_data)
        self.status_label.setText(
            f"表格已载入 {len(self.model.rows)} 个图框；待写回改动 {modified} 条"
        )

    def _run_worker(self, work: Callable[[], dict[str, Any]], done: Callable[[dict[str, Any]], None]) -> None:
        if self.busy:
            return
        self._set_busy(True)
        self._append_log("-" * 70)
        worker = Worker(work, done)
        worker.signals.log.connect(self._append_log)
        worker.signals.done.connect(self._worker_done)
        worker.signals.error.connect(self._worker_error)
        self.pool.start(worker)

    def _thread_log(self, message: str) -> None:
        # Signal 是跨线程安全的；临时 WorkerSignals 由主窗口持有。
        QApplication.instance().postEvent  # 保证 QApplication 已创建
        self._log_signal.emit(message)

    _log_signal = Signal(str)

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
        for widget in (
            self.export_button,
            self.update_button,
            self.load_button,
            self.replace_button,
            self.table,
            self.common_box,
        ):
            widget.setEnabled(not busy)
        if busy:
            self.status_label.setText("处理中，请等待后台引擎完成……")
            self.tabs.setCurrentWidget(self.log_text)
        else:
            self._refresh_status()

    def _append_log(self, message: str) -> None:
        self.log_text.append(message.rstrip())

    def _export_done(self, result: dict[str, Any]) -> None:
        summary = (
            f"导出完成：{result['dwg_count']} 个 DWG，{result['block_count']} 个块，"
            f"{result['attribute_count']} 条属性；失败 {result['error_count']} 个。"
        )
        self._append_log(summary)
        self._load_table(Path(result["json_path"]))
        QMessageBox.information(
            self, APP_NAME, summary + "\n\n已载入表格，可直接编辑后写回。\n" + str(result["json_path"])
        )

    def _update_done(self, result: dict[str, Any]) -> None:
        summary = (
            f"写回完成：处理 {result['file_count']} 个 DWG，修改 {result['changed_count']} 条属性，"
            f"问题 {result['issue_count']} 项。"
        )
        self._append_log(summary)
        if self.table_data is not None:
            if result["issue_count"] == 0:
                # DWG 已与表格一致：把表格基准值同步为当前值，可继续下一轮编辑。
                for item in self.table_data.get("files", []):
                    for block in item.get("blocks", []):
                        for attr in block.get("attributes", []):
                            attr["original_value"] = attr.get("value", "")
                if self.table_json_path is not None:
                    try:
                        atomic_json_dump(self.table_data, self.table_json_path)
                    except Exception as exc:
                        self._append_log(f"同步 JSON 基准值失败：{exc}")
                self.model.refresh_all()
                for column, edit in self.common_fields:
                    self._style_common_field(column, edit)
                self._refresh_status()
                self._append_log("写回成功，表格基准值已同步，可继续编辑。")
                self.tabs.setCurrentIndex(0)
            else:
                self._append_log(
                    "写回存在问题项：表格基准值未同步，建议重新导出后再继续编辑（详见报告）。"
                )
        QMessageBox.information(
            self,
            APP_NAME,
            summary + f"\n\n备份：{result['backup_root']}\n报告：{result['report_path']}",
        )


def main() -> None:
    if len(sys.argv) >= 4 and sys.argv[1] == "--headless-export":
        folder = Path(sys.argv[2]).resolve()
        target = Path(sys.argv[3]).resolve()
        block_filter = TITLE_BLOCK_FILTER
        export_folder(folder, target, True, block_filter, False, print)
        return
    if len(sys.argv) >= 4 and sys.argv[1] == "--headless-update":
        folder = Path(sys.argv[2]).resolve()
        source_json = Path(sys.argv[3]).resolve()
        update_folder(folder, source_json, False, False, print)
        return
    app = QApplication(sys.argv)
    window = QtApp()
    window._log_signal.connect(window._append_log)
    window.show()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
