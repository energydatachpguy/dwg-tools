"""
The PEC block-level layer standard, read from the Excel file next to run.bat
("PEC Standard CAD Layers at Block Level.xlsx"). Read-only: the file is only
opened for reading (plain zip + XML, no Excel needed).

Each row: Name | Plot | Color | Linetype | Lineweight | Transparency.
An exploded WBLOCK's objects are sorted onto these layers by their colour.
"""
import os
import re
import zipfile
import xml.etree.ElementTree as ET

FILE_NAME = "PEC Standard CAD Layers at Block Level.xlsx"
_NS = {"m": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
COLOR_NAMES = {"red": 1, "yellow": 2, "green": 3, "cyan": 4, "blue": 5, "magenta": 6, "white": 7, "black": 7}


def path(root):
    return os.path.join(root, FILE_NAME)


def _rows(xlsx):
    with zipfile.ZipFile(xlsx) as z:
        shared = []
        if "xl/sharedStrings.xml" in z.namelist():
            for si in ET.fromstring(z.read("xl/sharedStrings.xml")).findall("m:si", _NS):
                shared.append("".join(t.text or "" for t in si.iter("{%s}t" % _NS["m"])))
        sheet = sorted(n for n in z.namelist() if re.match(r"xl/worksheets/sheet\d+\.xml$", n))[0]
        out = []
        for r in ET.fromstring(z.read(sheet)).iter("{%s}row" % _NS["m"]):
            row = {}
            for c in r.findall("m:c", _NS):
                col = re.match(r"[A-Z]+", c.get("r")).group(0)
                t, v = c.get("t"), c.find("m:v", _NS)
                if t == "s" and v is not None:
                    val = shared[int(v.text)]
                elif t == "inlineStr":
                    val = "".join(x.text or "" for x in c.iter("{%s}t" % _NS["m"]))
                elif t == "b" and v is not None:
                    val = v.text == "1"
                elif v is not None:
                    val = v.text
                else:
                    val = None
                row[col] = val
            out.append(row)
        return out


def _aci(v):
    s = str(v).strip().lower()
    if s in COLOR_NAMES:
        return COLOR_NAMES[s]
    try:
        n = int(float(s))
        return n if 1 <= n <= 255 else None
    except ValueError:
        return None


def load(root):
    """-> {"file", "layers": [{name, plot, color, aci, linetype, lineweight, transparency}], "problems": [...]}"""
    p = path(root)
    res = {"file": p, "layers": [], "problems": []}
    if not os.path.isfile(p):
        res["problems"].append("Layer standard not found: " + p)
        return res
    try:
        rows = _rows(p)
    except Exception as exc:
        res["problems"].append("Could not read %s: %s" % (p, exc))
        return res
    if not rows:
        return res
    head = {str(v).strip().lower(): k for k, v in rows[0].items() if v is not None}
    col = lambda name: head.get(name)
    seen_aci = {}
    for r in rows[1:]:
        name = r.get(col("name"))
        if name is None or str(name).strip() == "":
            continue
        name = str(name).strip()
        if name.endswith(".0"):
            name = name[:-2]
        color = r.get(col("color"))
        aci = _aci(color) if color is not None else None
        plot = r.get(col("plot"))
        plot = plot if isinstance(plot, bool) else str(plot).strip().lower() in ("true", "1", "yes", "y")
        lt = str(r.get(col("linetype")) or "Continuous").strip()
        lw = str(r.get(col("lineweight")) or "ByLineWeightDefault").strip()
        try:
            tr = int(float(r.get(col("transparency")) or 0))
        except ValueError:
            tr = 0
        if aci is None:
            res["problems"].append("Layer %s: colour '%s' is not a colour number 1-255 or a colour name" % (name, color))
        elif aci in seen_aci:
            res["problems"].append("Layers %s and %s both use colour %s; objects of that colour go on %s" % (seen_aci[aci], name, aci, seen_aci[aci]))
        else:
            seen_aci[aci] = name
        res["layers"].append({"name": name, "plot": plot, "color": color, "aci": aci, "linetype": lt,
                              "lineweight": lw, "transparency": tr})
    return res


def to_job(layers):
    """name|plot|aci|linetype|lineweight|transparency per layer (\\u0001 between fields, \\u0002 between layers)."""
    return "\u0002".join("\u0001".join([l["name"], "1" if l["plot"] else "0", str(l["aci"] or 0), l["linetype"],
                                        l["lineweight"], str(l["transparency"])]) for l in layers)
