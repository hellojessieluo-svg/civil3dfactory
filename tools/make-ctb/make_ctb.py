# Builds a plot style table (.ctb). The rules (lineweight, screening and output colour per colour number) live in a JSON spec; the script holds no company values.
# Usage: python make_ctb.py <spec.json> [<out.ctb>]
#   out defaults to the spec path minus ".json" (waterway.ctb.json -> waterway.ctb).
#   Keep a company's spec with its drafting standards, next to the .ctb it generates; demo-plot.ctb.json
#   next to this script is a neutral example. Spec keys: description (ASCII only), end_digit_lineweight,
#   end_digit_screen, palette {aci: [lineweight_mm, screen_pct]}, bands [{from, to, rgb}], default_rgb.
#   ACI listed in palette are plotted black; the others take their band colour (default_rgb outside bands).
#
# Pitfall found plotting real drawings (2026-09-24): ezdxf writes color=(0xC2<<24)|rgb and color_policy=0, and AutoCAD
# **ignores that colour and plots objects in their own colours** (magenta lines, green text). Black in Autodesk's own monochrome.ctb is
# color=-16777216 (0xFF000000) + color_policy=5 (DITHERING_ON|NAMED_COLOR). So the generated table gets a text fix-up:
# black entries become -16777216 and every entry's color_policy gets the NAMED_COLOR (4) bit.
# Same-day isolation test: a non-ASCII description also makes AutoCAD treat the whole table as invalid - **ASCII description and the colour fix are both required** (either one alone still plots in colour).
#
# Second pitfall (2026-09-26, measured cell by cell on a 255-colour chart): lineweights had no effect. ezdxf's lineweight table is array('f') (float32);
# set_lineweight(0.09) looks it up with ==, float32 0.09 is 0.0900000036, so it is not found, a new entry is appended and an index from 27 upward is returned;
# all of 0.09/0.13/0.18/0.35/0.70 behave like this; only 0.25/0.5/1.0 are found. AutoCAD only knows the 27 entries 0-26: out-of-range indices plot with the object lineweight,
# entry 27 (colour 1) becomes 2.11 mm. Reading back with ezdxf uses its own extended table and hides the error. So: write the index of the nearest of entries 0-26 directly,
# then self-check the way AutoCAD reads it (27 entries, 0 out of range, 0 wrong) and discard the file when the check fails.
from ezdxf.addons import acadctb
from ezdxf.addons.acadctb import _compress
import json, os, re, sys, zlib

ACAD_BLACK = -16777216          # how monochrome.ctb writes black
EZDXF_BLACK = -1040187392       # black as ezdxf writes it (0xC2<<24)
NAMED_COLOR = 4
# The 27 lineweights AutoCAD knows (mm, index 0-26; same as custom_lineweight_table in Autodesk's acad.ctb / monochrome.ctb)
ACAD_LW = [0.00, 0.05, 0.09, 0.10, 0.13, 0.15, 0.18, 0.20, 0.25, 0.30, 0.35, 0.40, 0.45, 0.50,
           0.53, 0.60, 0.65, 0.70, 0.80, 0.90, 1.00, 1.06, 1.20, 1.40, 1.58, 2.00, 2.11]


class Spec:
    """The pen rules of one plot style table, read from JSON."""

    def __init__(self, path: str):
        d = json.load(open(path, encoding="utf-8"))
        self.description = d["description"]
        if not self.description.isascii():
            raise SystemExit("spec description must be ASCII (AutoCAD ignores the whole table otherwise)")
        self.end_lw = {int(k): float(v) for k, v in d["end_digit_lineweight"].items()}
        if sorted(self.end_lw) != list(range(10)):
            raise SystemExit("end_digit_lineweight needs all ten digits 0-9")
        self.end_screen = {int(k): int(v) for k, v in d.get("end_digit_screen", {}).items()}
        self.palette = {int(k): (float(v[0]), int(v[1])) for k, v in d.get("palette", {}).items()}
        self.bands = [(int(b["from"]), int(b["to"]), tuple(b["rgb"])) for b in d.get("bands", [])]
        self.default_rgb = tuple(d.get("default_rgb", [0, 0, 0]))

    def expect(self, aci: int):
        """ACI -> (lineweight mm, screen %)."""
        if aci in self.palette:
            return self.palette[aci]
        return self.end_lw[aci % 10], self.end_screen.get(aci % 10, 100)

    def color(self, aci: int):
        if aci in self.palette:
            return (0, 0, 0)
        for lo, hi, rgb in self.bands:
            if lo <= aci <= hi:
                return rgb
        return self.default_rgb


def lw_index(lw: float) -> int:
    """Lineweight in mm -> index of the nearest of AutoCAD's 27 entries (not ezdxf set_lineweight, see the header)."""
    return min(range(len(ACAD_LW)), key=lambda i: abs(ACAD_LW[i] - lw))


def ctb_text(path: str) -> str:
    raw = open(path, "rb").read()
    i = raw.find(b"pmzlibcodec")
    return zlib.decompress(raw[i + 11 + 12:]).decode("latin1")


def acad_check(path: str, spec: Spec) -> dict:
    """Self-check the way AutoCAD reads the file (not through ezdxf): the lineweight table must have exactly 27 entries equal to the standard table; every style's lineweight index
    stays in range (0-26), and lineweight and screening read through the standard table equal the spec."""
    text = ctb_text(path)
    pairs = re.findall(r"(\d+)=([\d.]+)", re.search(r"custom_lineweight_table\{([^}]*)\}", text).group(1))
    table_bad = [k for k, v in pairs if int(k) >= len(ACAD_LW) or abs(float(v) - ACAD_LW[int(k)]) > 1e-6]
    idx = [int(x) for x in re.findall(r"\n\s*lineweight=(\d+)", text)]
    scr = [int(x) for x in re.findall(r"\n\s*screen=(\d+)", text)]
    out_of_range = [aci for aci in range(1, 256) if idx[aci - 1] > 26]
    wrong = [aci for aci in range(1, 256) if aci not in out_of_range and
             (abs(ACAD_LW[idx[aci - 1]] - lw_snap(spec.expect(aci)[0])) > 1e-9 or scr[aci - 1] != spec.expect(aci)[1])]
    return {"table": len(pairs), "table_bad": len(table_bad), "styles": len(idx), "out_of_range": len(out_of_range),
            "wrong": len(wrong), "wrong_aci": wrong[:10], "acad_black": text.count("color=%d" % ACAD_BLACK),
            "policy5": text.count("color_policy=5")}


def lw_snap(lw: float) -> float:
    return ACAD_LW[lw_index(lw)]


def fixup(path: str):
    """Rewrite the colours ezdxf writes into the form the AutoCAD plotter accepts (see the header)."""
    raw = open(path, "rb").read()
    i = raw.find(b"pmzlibcodec")
    text = zlib.decompress(raw[i + 11 + 12:]).decode("latin1")
    text = text.replace("color=%d" % EZDXF_BLACK, "color=%d" % ACAD_BLACK)
    text = re.sub(r"color_policy=(\d+)", lambda m: "color_policy=%d" % (int(m.group(1)) | NAMED_COLOR), text)
    with open(path, "wb") as f:
        _compress(f, text)


def main():
    if len(sys.argv) < 2:
        raise SystemExit("usage: python make_ctb.py <spec.json> [<out.ctb>]")
    spec_path = os.path.abspath(sys.argv[1])
    out = os.path.abspath(sys.argv[2]) if len(sys.argv) > 2 else re.sub(r"\.json$", "", spec_path)
    if not out.lower().endswith(".ctb"):
        raise SystemExit(f"output must end with .ctb: {out}")
    spec = Spec(spec_path)
    ctb = acadctb.new_ctb()
    ctb.description = spec.description
    for aci in range(1, 256):
        st = ctb.new_style(aci)
        lw, screen = spec.expect(aci)
        st.color = spec.color(aci)
        st.screen = screen
        st.lineweight = lw_index(lw)   # write the index directly; set_lineweight appends out-of-range entries (see header)
        st.dithering = True
    tmp = out + ".new"
    ctb.save(tmp)
    fixup(tmp)
    r = acad_check(tmp, spec)
    ok = (r["table"] == 27 and r["table_bad"] == 0 and r["styles"] == 255 and r["out_of_range"] == 0
          and r["wrong"] == 0 and r["policy5"] == 255)
    print("AutoCAD check:", r, "PASS" if ok else "FAIL")
    if not ok:
        os.remove(tmp)
        raise SystemExit("self-check failed, not overwriting " + out)
    os.replace(tmp, out)
    print("saved", out, "size", os.path.getsize(out))


if __name__ == "__main__":
    main()
