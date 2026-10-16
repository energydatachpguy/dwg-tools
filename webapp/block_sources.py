"""
Where do the blocks in a drawing come from?

AutoCAD does not record which .dwg file a block was inserted from, so this
module matches each block NAME to .dwg FILE NAMES found under a set of
folders (the project, plus other projects the names point to).

Strictly read-only: it only lists folders (os.scandir). It never opens,
copies, writes, moves or deletes any file.
"""
import os
import re
import threading
import time

# ── name classification ─────────────────────────────────────────────────────
_PASTED = re.compile(r"^A\$C[0-9a-f]{8}$", re.I)            # PASTEBLOCK / Ctrl+Shift+V
_HATCH = re.compile(r"^HATCH\d+$", re.I)                    # generated hatch blocks
_REVIT = re.compile(r"-(\d{6,8}|V\d+)-LEVEL \d+$", re.I)    # Revit "export to CAD" families
_ARCHIVE = re.compile(r"(^|[\\/])(archive|archived|old|superseded|backup|bak|_old|obsolete)([\\/ _-]|$)", re.I)
_PROJ_NO = re.compile(r"(?:^|[-\s_])(\d{3})(?=$|[-\s_])")
_PROJ_DIR = re.compile(r"^(\d{3,})\b")


def loose(s: str) -> str:
    """Key that ignores case, spaces and punctuation ('w_Evap' == 'w/Evap' == 'W Evap')."""
    return re.sub(r"[^0-9a-z]", "", s.lower())


def classify(name: str):
    """-> (kind, file_stem_to_look_for, note)."""
    if "$0$" in name:   # bound xref: <xref name>$0$<block>
        prefix = name.split("$0$", 1)[0]
        return "bound", prefix, "came in with xref '%s', which was later bound" % prefix
    if name.startswith("*"):
        return "generated", None, "anonymous block"
    if _PASTED.match(name):
        return "generated", None, "made by Paste as Block (no source file)"
    if _HATCH.match(name):
        return "generated", None, "generated hatch block (no source file)"
    if _REVIT.search(name):
        return "generated", name, "exported from Revit (usually no source file)"
    if name.startswith("_"):
        return "generated", None, "AutoCAD built-in (arrowheads etc.)"
    return "file", name, ""


def project_numbers(names):
    """3-digit project numbers mentioned in block names: {'866': 4, '862': 1, ...}."""
    out = {}
    for n in names:
        kind, stem, _ = classify(n)
        if kind == "generated":
            continue
        for m in _PROJ_NO.finditer(stem or n):
            out[m.group(1)] = out.get(m.group(1), 0) + 1
    return out


def suggest_folders(master: str, project_root: str, names):
    """Project root first, then other project folders (P:\\866 - ...) the block names mention."""
    out = []
    if project_root and os.path.isdir(project_root):
        out.append({"path": project_root, "why": "this project", "checked": True})
    parent = os.path.dirname(project_root.rstrip("\\/")) if project_root else ""
    nums = project_numbers(names)
    this_no = (_PROJ_DIR.match(os.path.basename(project_root.rstrip("\\/"))) or [None, None])[1] if project_root else None
    if parent and os.path.isdir(parent) and nums:
        try:
            with os.scandir(parent) as it:
                dirs = [e for e in it if e.is_dir()]
        except OSError:
            dirs = []
        for e in sorted(dirs, key=lambda e: e.name):
            m = _PROJ_DIR.match(e.name)
            if m and m.group(1) in nums and m.group(1) != this_no:
                c = nums[m.group(1)]
                out.append({"path": e.path, "why": "%d block name%s mention %s" % (c, "s" if c > 1 else "", m.group(1)), "checked": True})
    return out


# ── folder scan (listing only) ──────────────────────────────────────────────
_SKIP_DIRS = {"$recycle.bin", "system volume information", ".git", "__pycache__", "node_modules"}

_cache = {}            # folder (normcase) -> {"at": t, "files": [(stem, path, size, mtime)], "dirs": n, "errors": n}
_cache_lock = threading.Lock()
_scans = {}            # scan id -> state dict


def _scan_folder(root, st):
    files, ndirs, nerr = [], 0, 0
    stack = [root]
    while stack:
        if st["cancel"]:
            raise _Cancelled()
        d = stack.pop()
        ndirs += 1
        st["dirs"] += 1
        if ndirs % 25 == 0:
            st["current"] = d
        try:
            with os.scandir(d) as it:
                for e in it:
                    try:
                        if e.is_dir(follow_symlinks=False):
                            if e.name.lower() not in _SKIP_DIRS and not e.name.startswith("."):
                                stack.append(e.path)
                        elif e.name.lower().endswith(".dwg"):
                            s = e.stat()
                            files.append((e.name[:-4], e.path, s.st_size, s.st_mtime))
                            st["dwg"] += 1
                    except OSError:
                        nerr += 1
        except OSError:
            nerr += 1
    return {"at": time.time(), "files": files, "dirs": ndirs, "errors": nerr}


class _Cancelled(Exception):
    pass


def _where(path, master, project_root):
    p = os.path.normcase(os.path.abspath(path))
    mdir = os.path.normcase(os.path.dirname(os.path.abspath(master))) if master else ""
    root = os.path.normcase(os.path.abspath(project_root)) if project_root else ""
    if mdir and os.path.dirname(p) == mdir:
        return "model"          # same folder as the equipment model
    if root and p.startswith(root + os.sep):
        return "project"
    return "other"


def _top_folder(path):
    drive, rest = os.path.splitdrive(os.path.abspath(path))
    parts = [x for x in re.split(r"[\\/]", rest) if x]
    return (drive + "\\" + parts[0]) if parts else drive


def _rank(c, master, project_root):
    where_order = {"model": 0, "project": 1, "other": 2}[c["where"]]
    return (0 if c["exact"] else 1, 1 if c["archive"] else 0, where_order, -c["mtime"])


def match(blocks, index, master, project_root):
    """blocks: [{"name", "uses"}] -> rows with candidates and a category."""
    exact, loosek = {}, {}
    for stem, path, size, mtime in index:
        exact.setdefault(stem.lower(), []).append((path, size, mtime))
        loosek.setdefault(loose(stem), []).append((stem, path, size, mtime))
    rows = []
    for b in blocks:
        name = b["name"]
        kind, stem, note = classify(name)
        cands = []
        if stem:
            seen = set()
            for path, size, mtime in exact.get(stem.lower(), []):
                seen.add(path.lower())
                cands.append({"path": path, "size": size, "mtime": mtime, "exact": True})
            for s2, path, size, mtime in loosek.get(loose(stem), []):
                if path.lower() not in seen and loose(stem):
                    cands.append({"path": path, "size": size, "mtime": mtime, "exact": False, "fileName": s2})
        for c in cands:
            c["where"] = _where(c["path"], master, project_root)
            c["archive"] = bool(_ARCHIVE.search(os.path.dirname(c["path"])))
            c["projectFolder"] = _top_folder(c["path"])
            c["modified"] = time.strftime("%Y-%m-%d %H:%M", time.localtime(c["mtime"]))
        cands.sort(key=lambda c: _rank(c, master, project_root))
        if cands:
            best = cands[0]
            cat = {"model": "model", "project": "project", "other": "other"}[best["where"]]
            if not best["exact"]:
                note = (note + "; " if note else "") + "file name differs slightly: " + best.get("fileName", "")
            if best["archive"]:
                note = (note + "; " if note else "") + "only found in an archive/old folder"
        else:
            cat = "generated" if kind == "generated" else "notfound"
        rows.append({"name": name, "uses": b.get("uses", 0), "kind": kind, "lookFor": stem, "category": cat,
                     "note": note, "candidates": cands[:12], "moreCandidates": max(0, len(cands) - 12)})
    return rows


def start_scan(folders, blocks, master, project_root, rescan=False):
    sid = "%x" % int(time.time() * 1000)
    st = {"id": sid, "state": "running", "cancel": False, "dirs": 0, "dwg": 0, "current": "", "folders": folders,
          "folderStats": [], "rows": None, "error": "", "started": time.time(), "elapsed": None}
    _scans[sid] = st

    def run():
        try:
            index = []
            for f in folders:
                key = os.path.normcase(os.path.abspath(f))
                with _cache_lock:
                    c = None if rescan else _cache.get(key)
                if c is None:
                    st["current"] = f
                    c = _scan_folder(f, st)
                    with _cache_lock:
                        _cache[key] = c
                    cached = False
                else:
                    st["dwg"] += len(c["files"])
                    cached = True
                index.extend(c["files"])
                st["folderStats"].append({"path": f, "dwg": len(c["files"]), "dirs": c["dirs"], "errors": c["errors"],
                                          "scannedAt": time.strftime("%H:%M", time.localtime(c["at"])), "cached": cached})
            st["rows"] = match(blocks, index, master, project_root)
            st["state"] = "done"
        except _Cancelled:
            st["state"] = "cancelled"
        except Exception as exc:     # report, never crash the app
            st["state"] = "failed"
            st["error"] = str(exc)
        st["elapsed"] = round(time.time() - st["started"], 1)

    threading.Thread(target=run, daemon=True).start()
    return st


def scan_state(sid):
    st = _scans.get(sid)
    if not st:
        return None
    return {k: v for k, v in st.items() if k != "cancel"}


def cancel(sid):
    st = _scans.get(sid)
    if st:
        st["cancel"] = True
    return bool(st)
