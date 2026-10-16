"""
The DWG Tools log: one file per day in <app>\\logs\\dwgtools-YYYY-MM-DD.log.

Every CAD request (light up, pick, WBLOCK, tree), its steps, what the plugin
said and how it ended is written here, so a problem can be traced after the
fact. Append only (safe_fs.append_log). Plain text, one block per event:

    2026-10-15 10:41:07  WBLOCK.find  START  drawing=...  path=A$C...
        step  ✓ Attached to AutoCAD (...)
        ...
"""
import os
import threading
import time

import safe_fs

_lock = threading.Lock()


def path_for(day=None):
    return os.path.join(safe_fs.LOG_DIR, "dwgtools-%s.log" % (day or time.strftime("%Y-%m-%d")))


def _fmt(v):
    s = str(v)
    return '"%s"' % s if (" " in s or not s) else s


def log(event, status="", detail=None, **fields):
    """event: short name (WBLOCK.find); status: START/OK/FAILED/INFO;
    fields: key=value on the first line; detail: list of lines (or one str)."""
    head = "%s  %-18s %-7s %s" % (time.strftime("%Y-%m-%d %H:%M:%S"), event, status,
                                  "  ".join("%s=%s" % (k, _fmt(v)) for k, v in fields.items() if v not in (None, "")))
    lines = [head.rstrip()]
    if detail:
        if isinstance(detail, str):
            detail = detail.splitlines()
        lines += ["    " + str(d).rstrip() for d in detail if str(d).strip()]
    try:
        with _lock:
            safe_fs.append_log(path_for(), "\n".join(lines) + "\n")
    except Exception:
        pass        # logging must never break the work


def steps_lines(steps):
    return ["step  %s %s" % ("✓" if ok else "✗", m) for m, ok in (steps or [])]


def files():
    try:
        return sorted((f for f in os.listdir(safe_fs.LOG_DIR) if f.endswith(".log")), reverse=True)
    except OSError:
        return []
