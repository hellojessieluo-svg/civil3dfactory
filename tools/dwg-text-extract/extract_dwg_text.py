#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""Extract DWG text through accoreconsole DXFOUT and ezdxf; work on temporary copies."""
import argparse
import os
import shutil
import subprocess
import sys
import tempfile

ACCORECONSOLE = r"C:\Program Files\Autodesk\AutoCAD 2025\accoreconsole.exe"


def dwg_to_dxf(dwg_path, work_dir):
    """Convert a DWG copy to DXF; return its path or None on failure."""
    base = os.path.splitext(os.path.basename(dwg_path))[0]
    tmp_dwg = os.path.join(work_dir, "in.dwg")
    shutil.copyfile(dwg_path, tmp_dwg)
    dxf_path = os.path.join(work_dir, "out.dxf")
    scr_path = os.path.join(work_dir, "job.scr")
    with open(scr_path, "w", encoding="ascii") as f:
        f.write("FILEDIA\n0\nDXFOUT\n{}\n16\n".format(dxf_path.replace("\\", "/")))
    try:
        subprocess.run(
            [ACCORECONSOLE, "/i", tmp_dwg, "/s", scr_path],
            capture_output=True, text=True, encoding="utf-8", errors="ignore",
            timeout=600,
        )
    except subprocess.TimeoutExpired:
        print("  [timeout] accoreconsole exceeded 600s for {}".format(base), file=sys.stderr)
        return None
    if os.path.exists(dxf_path) and os.path.getsize(dxf_path) > 0:
        return dxf_path
    return None


def clean(s):
    return " ".join(s.replace("\r", " ").replace("\n", " ").split()).strip()


def extract_texts(dxf_path):
    """Read all DXF text, deduplicated in encounter order."""
    import ezdxf
    doc = ezdxf.readfile(dxf_path)
    seen = set()
    out = []

    def add(s):
        s = clean(s)
        if s and s not in seen:
            seen.add(s)
            out.append(s)

    for block in doc.blocks:
        for e in block:
            t = e.dxftype()
            try:
                if t == "TEXT":
                    add(e.dxf.text)
                elif t == "MTEXT":
                    add(e.plain_text())
                elif t == "ATTDEF":
                    add(e.dxf.text)
                elif t == "DIMENSION":
                    txt = e.dxf.get("text", "")
                    if txt and txt not in ("<>", " "):
                        add(txt)
                elif t == "INSERT":
                    for att in e.attribs:
                        add(att.dxf.text)
            except Exception:
                continue
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dwg", nargs="+", help="One or more DWG paths")
    ap.add_argument("-o", "--output", required=True, help="Output Markdown path")
    ap.add_argument("--keep-dxf", action="store_true", help="Keep intermediate DXF")
    args = ap.parse_args()

    if not os.path.exists(ACCORECONSOLE):
        print("accoreconsole.exe not found: {}".format(ACCORECONSOLE), file=sys.stderr)
        sys.exit(1)

    sections = []
    for dwg in args.dwg:
        name = os.path.basename(dwg)
        print("Processing: {}".format(name), file=sys.stderr)
        if not os.path.exists(dwg):
            sections.append((name, None, ["[File not found]"]))
            continue
        work = tempfile.mkdtemp(prefix="dwgtxt_")
        try:
            dxf = dwg_to_dxf(dwg, work)
            if not dxf:
                sections.append((name, None, ["[DWG to DXF conversion failed]"]))
                continue
            texts = extract_texts(dxf)
            sections.append((name, len(texts), texts))
            if args.keep_dxf:
                shutil.copyfile(dxf, os.path.join(os.path.dirname(args.output),
                                                   os.path.splitext(name)[0] + ".dxf"))
        finally:
            if not args.keep_dxf:
                shutil.rmtree(work, ignore_errors=True)

    lines = ["# DWG text extraction", ""]
    for name, n, texts in sections:
        lines.append("## {}".format(name))
        if n is not None:
            lines.append("")
            lines.append("> {} text entries".format(n))
        lines.append("")
        for t in texts:
            lines.append("- {}".format(t))
        lines.append("")

    os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
    with open(args.output, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
    print("Written: {}".format(args.output), file=sys.stderr)


if __name__ == "__main__":
    main()
