"""
The ONLY module in the web app allowed to delete, move, copy or write files.

The app itself only ever writes:
  * its scratch folder  %TEMP%\\dwgflat   (job request/result/progress files, console script/log)
  * its own state files  jobs_history.json  next to the app
  * a shadow copy of the plugin DLL in %TEMP% (named DwgFlatPublisher_<n>.dll, created once)
Drawings are never written, moved or deleted from Python at all; the plugin's
SafeFs guard covers everything AutoCAD writes.

tools/safety_audit.py refuses to start the app if any other module calls
os.remove / os.replace / shutil.* / open(..., "w") etc. directly.
"""
import os
import shutil
import tempfile

SCRATCH = os.path.join(tempfile.gettempdir(), "dwgflat")
APP_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
APP_STATE_FILES = {os.path.normcase(os.path.join(APP_ROOT, n)) for n in ("jobs_history.json", "jobs_history.json.tmp")}


class UnsafeWrite(Exception):
    pass


def _norm(p):
    return os.path.normcase(os.path.realpath(os.path.abspath(p)))


def in_scratch(path) -> bool:
    root = _norm(SCRATCH)
    return _norm(path).startswith(root + os.sep)


def _check_scratch(path):
    if not in_scratch(path):
        raise UnsafeWrite("Refusing to touch %s: outside the app's scratch folder %s" % (path, SCRATCH))


def remove_scratch(path):
    """Delete one of the app's own scratch files (job.out, progress.txt ...)."""
    _check_scratch(path)
    try:
        os.remove(path)
    except FileNotFoundError:
        pass


def open_scratch(path, mode="w", **kw):
    """Open a scratch file for writing (job request, console script, console log)."""
    _check_scratch(path)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    return open(path, mode, **kw)


def write_app_state(path, text):
    """Atomically rewrite one of the app's own state files (jobs_history.json)."""
    if _norm(path) not in {_norm(p) for p in APP_STATE_FILES}:
        raise UnsafeWrite("Refusing to write %s: not an app state file" % path)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as fh:
        fh.write(text)
    os.replace(tmp, path)


LOG_DIR = os.path.join(APP_ROOT, "logs")


def append_log(path, text):
    """Append to one of the app's own log files (logs\\*.log). Append only:
    a log is never truncated, rewritten or deleted by the app."""
    p = _norm(path)
    if os.path.dirname(p) != _norm(LOG_DIR) or not p.endswith(".log"):
        raise UnsafeWrite("Refusing to write %s: not an app log file" % path)
    os.makedirs(LOG_DIR, exist_ok=True)
    with open(path, "a", encoding="utf-8") as fh:
        fh.write(text)


def copy_plugin_shadow(dll, shadow):
    """Copy the built DLL to a NEW file in %TEMP% (never over an existing file)."""
    if os.path.dirname(_norm(shadow)) != _norm(tempfile.gettempdir()) or not os.path.basename(shadow).startswith("DwgFlatPublisher_"):
        raise UnsafeWrite("Refusing to copy the plugin to %s" % shadow)
    if os.path.exists(shadow):
        return
    shutil.copy2(dll, shadow)
