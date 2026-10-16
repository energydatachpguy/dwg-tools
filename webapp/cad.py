"""
CAD bridge: drives the AutoCAD / Plant 3D running on THIS machine.

Same mechanism as the P&ID Report Card's app/acad.py (attach over COM with
pywin32, start Plant 3D if it isn't running, one operation at a time, retry
while AutoCAD is busy, refuse when it is mid-command, watch for blocking
dialogs, NETLOAD a shadow copy once per AutoCAD process), and the same
protocol as PnPSheetCopy: write a request file, run ONE command that takes
no prompts, read a result file.

    %TEMP%\\dwgflat\\job.json      request (written here)
    FLATJOB                       the command (DwgFlatPublisher.dll)
    %TEMP%\\dwgflat\\progress.txt  "pct|stage|message" while it runs
    %TEMP%\\dwgflat\\job.out       "OK|..." / "ERR|..." + log lines when done

The flattening itself happens inside AutoCAD on a separate in-memory copy of
the source drawing: nothing is opened in the editor, the source file is never
written.
"""

import glob
import re
import json

import safe_fs
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SETTINGS_PATH = os.path.join(ROOT, "settings.json")
JOB_DIR = os.path.join(tempfile.gettempdir(), "dwgflat")
JOB_REQ = os.path.join(JOB_DIR, "job.json")
JOB_OUT = os.path.join(JOB_DIR, "job.out")
JOB_PROGRESS = os.path.join(JOB_DIR, "progress.txt")
DEFAULT_DLL = os.path.join(ROOT, "plugin", "bin", "DwgFlatPublisher.dll")
PLANT3D_ARGS = ["/product", "PLNT3D", "/language", "en-US"]


def settings() -> dict:
    try:
        with open(SETTINGS_PATH, encoding="utf-8") as fh:
            return json.load(fh)
    except Exception:
        return {}


CURRENT_JSON = os.path.join(ROOT, "plugin", "bin", "current.json")


def plugin_info() -> dict:
    """The newest build: {"dll": full path, "command": its own command name}.
    Every build has its own file name AND command (build.bat), so a newer
    build always loads next to an older one already inside Plant 3D."""
    p = str(settings().get("plugin_dll") or "").strip()
    if p:
        return {"dll": p, "command": str(settings().get("plugin_command") or "FLATJOB")}
    try:
        with open(CURRENT_JSON, encoding="utf-8-sig") as fh:
            j = json.load(fh)
        return {"dll": os.path.join(ROOT, "plugin", "bin", j["dll"]), "command": j["command"], "stamp": j.get("stamp", "")}
    except Exception:
        return {"dll": DEFAULT_DLL, "command": "FLATJOB"}


def build_status() -> dict:
    """Did the last plugin build work? A failed build leaves the previous build
    in use, which looks like "old code" -- so this is shown loudly."""
    log = os.path.join(ROOT, "plugin", "build.log")
    out = {"failed": False, "errors": [], "unbuiltChanges": False, "when": ""}
    try:
        with open(log, encoding="utf-8", errors="replace") as fh:
            text = fh.read()
        out["failed"] = "BUILD FAILED" in text
        out["errors"] = [l.strip() for l in text.splitlines() if "error CS" in l][:8]
        out["when"] = time.strftime("%Y-%m-%d %H:%M", time.localtime(os.path.getmtime(log)))
    except OSError:
        pass
    try:
        built = os.path.getmtime(CURRENT_JSON)
        src = os.path.join(ROOT, "plugin", "src")
        out["unbuiltChanges"] = any(os.path.getmtime(os.path.join(src, f)) > built for f in os.listdir(src) if f.endswith(".cs"))
    except OSError:
        out["unbuiltChanges"] = True
    return out


def plugin_dll() -> str:
    return plugin_info()["dll"]


# ── one AutoCAD operation at a time ─────────────────────────────────────────
class OpLock:
    def __init__(self):
        self._l = threading.Lock()
        self.op = ""
        self.since = 0.0

    def acquire(self, op: str, blocking=False) -> bool:
        ok = self._l.acquire(blocking)
        if ok:
            self.op, self.since = op, time.time()
        return ok

    def release(self):
        self.op, self.since = "", 0.0
        self._l.release()

    def busy_msg(self) -> str:
        age = int(time.time() - self.since) if self.since else 0
        return ("AutoCAD is still busy with %s started %ds ago. If AutoCAD is sitting "
                "in a command or has a dialog open, press Esc there." % (self.op or "another job", age))


LOCK = OpLock()


def com_retry(fn, tries=30, delay=0.5):
    """AutoCAD rejects COM calls while busy (RPC_E_CALL_REJECTED); wait and retry."""
    for i in range(tries):
        try:
            return fn()
        except Exception:
            if i == tries - 1:
                raise
            time.sleep(delay)


def not_quiescent(acad, wait=4.0) -> str:
    """'' when AutoCAD is at the command prompt; otherwise why not."""
    deadline = time.time() + wait
    last = "busy"
    while time.time() < deadline:
        try:
            if bool(acad.GetAcadState().IsQuiescent):
                return ""
            last = "in the middle of a command, or a dialog is open"
        except Exception as exc:
            last = "not answering (%s)" % str(exc)[:60]
        time.sleep(0.4)
    return "AutoCAD is %s. Press Esc in AutoCAD (close any dialog), then try again." % last


def _acad_year(exe: str) -> int:
    m = re.search(r"20\d\d", os.path.basename(os.path.dirname(exe)))
    return int(m.group(0)) if m else 0


def acad_exe() -> str:
    """settings.json "acad_exe", else the newest AutoCAD / Plant 3D 2020-2024,
    else the newest 2025+ (.NET 8; plugin built with dotnet, see build.bat)."""
    c = str(settings().get("acad_exe") or "").strip()
    if c and os.path.isfile(c):
        return c
    hits = glob.glob(r"C:\Program Files\Autodesk\AutoCAD*\acad.exe")
    hits.sort(key=lambda h: (_acad_year(h) < 2025, _acad_year(h)))
    return hits[-1] if hits else ""


def acad_args() -> list:
    """Command-line switches for starting AutoCAD. Plant 3D by default; set
    "acad_args": [] in settings.json for plain AutoCAD."""
    a = settings().get("acad_args")
    return list(a) if isinstance(a, list) else list(PLANT3D_ARGS)


def get_acad(steps, launch=True, wait=240.0):
    """The running AutoCAD -- or, when there is none, the Plant 3D this starts."""
    import win32com.client
    try:
        return win32com.client.GetActiveObject("AutoCAD.Application")
    except Exception:
        pass
    if not launch:
        raise RuntimeError("AutoCAD doesn't appear to be running (or it runs as administrator "
                           "and this app doesn't -- they have to match).")
    exe = acad_exe()
    if not exe:
        raise RuntimeError("AutoCAD isn't running and acad.exe could not be found. Start Plant 3D, "
                           "or put its path in settings.json as \"acad_exe\".")
    subprocess.Popen([exe] + acad_args(), cwd=os.path.dirname(exe))
    steps.append(("AutoCAD wasn't running -- started Plant 3D, waiting for it", True))
    deadline = time.time() + wait
    while time.time() < deadline:
        try:
            app = win32com.client.GetActiveObject("AutoCAD.Application")
            if not not_quiescent(app, wait=2.0):
                time.sleep(4.0)
                steps.append(("Plant 3D is up", True))
                return app
        except Exception:
            pass
        time.sleep(2.0)
    raise RuntimeError("Plant 3D was started but hasn't finished loading after %d s." % int(wait))


# Dialogs that are safe to answer automatically. AutoCAD's SECURELOAD prompt
# for an unsigned DLL offers "Load Once"; anything else is left alone and
# reported (a modal dialog blocks SendCommand forever).
_DIALOG_BUTTONS = {
    "Security - Unsigned Executable File": ("load once", "load"),
    "File Loading - Security Concern": ("load once", "load"),
}
_BM_CLICK = 0x00F5


class DialogWatcher(threading.Thread):
    def __init__(self, pid):
        super().__init__(daemon=True)
        self.pid = pid
        self._stop = threading.Event()
        self.answered = set()
        self.seen_other = set()

    def stop(self):
        self._stop.set()

    def _handle(self, hwnd):
        import win32gui
        import win32process
        if not win32gui.IsWindowVisible(hwnd) or win32gui.GetClassName(hwnd) != "#32770":
            return
        if self.pid is not None:
            try:
                if win32process.GetWindowThreadProcessId(hwnd)[1] != self.pid:
                    return
            except Exception:
                return
        title = win32gui.GetWindowText(hwnd)
        wanted = _DIALOG_BUTTONS.get(title)
        if not wanted:
            if title:
                self.seen_other.add(title)
            return
        found = []

        def _cb(child, _):
            try:
                txt = win32gui.GetWindowText(child).replace("&", "").strip().lower()
                if txt:
                    found.append((txt, child))
            except Exception:
                pass
            return True
        try:
            win32gui.EnumChildWindows(hwnd, _cb, None)
        except Exception:
            pass
        for want in wanted:
            for txt, child in found:
                if txt.startswith(want):
                    win32gui.PostMessage(child, _BM_CLICK, 0, 0)
                    self.answered.add(title + " -> " + want)
                    return

    def run(self):
        import win32gui

        def _enum(hwnd, _):
            try:
                self._handle(hwnd)
            except Exception:
                pass
            return True
        while not self._stop.is_set():
            try:
                win32gui.EnumWindows(_enum, None)
            except Exception:
                pass
            self._stop.wait(0.25)


_netloaded = {"done": set()}   # {(AutoCAD pid, build dll)} loaded by this app run

STALE_MSG = ("An older DWG Tools plugin answered instead of the newest build. Close and re-run run.bat "
             "(it rebuilds the plugin), then try again. If it still happens, look at plugin\\build.log.")


class StalePlugin(RuntimeError):   # kept for compatibility; builds no longer go stale
    def __init__(self):
        super().__init__(STALE_MSG)


def _ensure_document(acad, steps):
    """SendCommand needs a current document. Use whatever is active; if
    nothing is open, start a blank drawing (the source is NOT opened)."""
    try:
        if int(com_retry(lambda: acad.Documents.Count)) > 0:
            return com_retry(lambda: acad.ActiveDocument)
    except Exception:
        pass
    doc = com_retry(lambda: acad.Documents.Add())
    steps.append(("No drawing was open -- started a blank one to run the command in", True))
    time.sleep(2.0)
    return doc


def _netload(doc, pid, steps):
    info = plugin_info()
    dll = info["dll"]
    if not os.path.isfile(dll):
        raise RuntimeError("The DWG Tools plugin is not built yet: close and re-run run.bat "
                           "(expected %s)." % dll)
    key = (pid, os.path.normcase(dll))
    if pid is not None and key in _netloaded["done"]:
        steps.append(("Plugin %s already loaded in this AutoCAD" % os.path.basename(dll), True))
        return info
    prev_secure = None
    try:
        prev_secure = int(com_retry(lambda: doc.GetVariable("SECURELOAD"), tries=4))
        if prev_secure != 0:
            com_retry(lambda: doc.SetVariable("SECURELOAD", 0), tries=4)
    except Exception:
        prev_secure = None
    try:
        com_retry(lambda: doc.SendCommand('(command "_.NETLOAD" "%s") ' % dll.replace("\\", "/")))
    finally:
        if prev_secure not in (None, 0):
            try:
                com_retry(lambda: doc.SetVariable("SECURELOAD", prev_secure), tries=10)
            except Exception:
                pass
    _netloaded["done"].add(key)
    steps.append(("Loaded plugin %s (command %s) into AutoCAD" % (os.path.basename(dll), info["command"]), True))
    return info


def read_progress() -> dict:
    try:
        with open(JOB_PROGRESS, encoding="utf-8-sig", errors="replace") as fh:
            raw = fh.read().strip()
        pct, stage, msg = (raw.split("|", 2) + ["", "", ""])[:3]
        return {"pct": float(pct), "stage": stage, "message": msg}
    except Exception:
        return {}


def _norm(p):
    return os.path.normcase(os.path.abspath(str(p or "")))


def _drawing_document(acad, path, steps):
    """The open document for 'path', made active. Opens it normally if it isn't
    open yet (it is the deliberate working copy: blocks can be inserted into it).
    This tool never SAVES a drawing: saving is done by you in CAD."""
    want = _norm(path)
    doc = None
    try:
        n = int(com_retry(lambda: acad.Documents.Count))
        for i in range(n):
            d = com_retry(lambda: acad.Documents.Item(i))
            try:
                if _norm(d.FullName) == want:
                    doc = d
                    break
            except Exception:
                pass
    except Exception:
        pass
    if doc is None:
        if not os.path.isfile(path):
            raise RuntimeError("Drawing not found: %s" % path)
        doc = com_retry(lambda: acad.Documents.Open(path, False))
        ro = False
        try:
            ro = bool(com_retry(lambda: doc.ReadOnly))
        except Exception:
            pass
        steps.append(("Opened %s in AutoCAD%s" % (os.path.basename(path), " (read-only: someone else has it open, or the file is read-only)" if ro else ""), True))
        time.sleep(2.0)
    try:
        if _norm(com_retry(lambda: acad.ActiveDocument.FullName)) != want:
            com_retry(lambda: doc.Activate())
            time.sleep(1.0)
    except Exception:
        pass
    return doc


def bring_to_front(acad):
    try:
        import win32gui
        import win32con
        hwnd = int(acad.HWND)
        if win32gui.IsIconic(hwnd):
            win32gui.ShowWindow(hwnd, win32con.SW_RESTORE)
        win32gui.SetForegroundWindow(hwnd)
    except Exception:
        pass


def run_job(job: dict, wait: float = 6 * 3600.0, label: str = "a flatten job", drawing: str = "", front: bool = False,
            launch: bool = True) -> dict:
    """Run one FLATJOB in the local AutoCAD. Returns
    {"ok", "steps": [(msg, ok)], "head": "...", "lines": [...]}"""
    steps = []
    out = {"ok": False, "steps": steps, "head": "", "lines": []}

    def fail(msg):
        steps.append((msg, False))
        return out

    if not LOCK.acquire(label):
        return fail(LOCK.busy_msg())
    try:
        try:
            import pythoncom
            import win32process
        except ImportError:
            return fail("pywin32 is not installed: venv\\Scripts\\pip install pywin32")
        pythoncom.CoInitialize()
        try:
            try:
                acad = get_acad(steps, launch=launch)
            except Exception as exc:
                return fail(str(exc)[:300])
            try:
                cap = str(com_retry(lambda: acad.Caption, tries=6))
            except Exception:
                cap = ""
            steps.append(("Attached to AutoCAD" + (" (" + cap[:70] + ")" if cap else ""), True))
            nq = not_quiescent(acad)
            if nq:
                return fail(nq)
            try:
                pid = win32process.GetWindowThreadProcessId(acad.HWND)[1]
            except Exception:
                pid = None
            watcher = DialogWatcher(pid)
            watcher.start()
            try:
                if drawing:
                    try:
                        doc = _drawing_document(acad, drawing, steps)
                    except Exception as exc:
                        return fail(str(exc)[:300])
                else:
                    doc = _ensure_document(acad, steps)
                try:
                    steps.append(("Active drawing in AutoCAD: %s" % str(com_retry(lambda: doc.Name, tries=4)), True))
                except Exception:
                    pass
                try:
                    info = _netload(doc, pid, steps)
                except Exception as exc:
                    return fail(str(exc)[:300])

                os.makedirs(JOB_DIR, exist_ok=True)
                for p in (JOB_OUT, JOB_PROGRESS):
                    safe_fs.remove_scratch(p)
                job = dict(job)
                job.setdefault("progressPath", JOB_PROGRESS)
                with safe_fs.open_scratch(JOB_REQ, "w", encoding="utf-8") as fh:
                    json.dump(job, fh, indent=2)
                try:
                    com_retry(lambda: doc.SendCommand(info["command"] + "\n"))
                    steps.append(("Sent %s to AutoCAD (%s)" % (info["command"], job.get("action", "publish")), True))
                    if front:
                        bring_to_front(acad)
                except Exception as exc:
                    return fail("Could not run FLATJOB: %s" % str(exc)[:160])

                deadline = time.time() + wait
                started_by = time.time() + 45
                text = ""
                while time.time() < deadline:
                    if started_by and os.path.exists(JOB_PROGRESS):
                        started_by = 0
                        steps.append(("AutoCAD started the request", True))
                    if started_by and time.time() > started_by and not os.path.exists(JOB_OUT):
                        return fail("AutoCAD did not start the DWG Tools command within 45 s. Usually a dialog or a "
                                    "running command is in the way: look at AutoCAD, press Esc, answer any dialog, then try again. "
                                    "(Command %s, plugin %s.)" % (info["command"], os.path.basename(info["dll"])))
                    if os.path.exists(JOB_OUT):
                        try:
                            with open(JOB_OUT, encoding="utf-8-sig", errors="replace") as fh:
                                text = fh.read()
                        except OSError:
                            text = ""
                        if text.strip():
                            break
                    time.sleep(0.5)
                else:
                    return fail("AutoCAD didn't finish within %d s. If a dialog is open in AutoCAD, "
                                "answer it; press Esc there to cancel." % int(wait))
                lines = [l.rstrip() for l in text.splitlines() if l.strip()]
                out["head"] = lines[0] if lines else "ERR|empty result"
                out["lines"] = lines[1:]
                out["ok"] = out["head"].startswith("OK|")
                steps.append(("AutoCAD finished: " + out["head"].split("|")[1] if "|" in out["head"] else out["head"],
                              out["ok"]))
                return out
            finally:
                watcher.stop()
                for t in sorted(watcher.answered):
                    steps.append(('Answered dialog "%s"' % t, True))
                for t in sorted(watcher.seen_other):
                    steps.append(('A dialog titled "%s" appeared in AutoCAD and was left alone' % t, False))
        finally:
            try:
                pythoncom.CoUninitialize()
            except Exception:
                pass
    except Exception as exc:
        return fail("Unexpected: %s" % str(exc)[:200])
    finally:
        LOCK.release()


# ── background engine: AutoCAD Core Console (no UI, doesn't touch your session) ──
_console_proc = {"p": None}


def console_exe() -> str:
    c = str(settings().get("accoreconsole_exe") or "").strip()
    if c and os.path.isfile(c):
        return c
    exe = acad_exe()
    if exe:
        cand = os.path.join(os.path.dirname(exe), "accoreconsole.exe")
        if os.path.isfile(cand):
            return cand
    return ""


def run_job_console(job: dict, wait: float = 6 * 3600.0, label: str = "a background flatten job") -> dict:
    """Same FLATJOB protocol, run in accoreconsole.exe. Plant 3D object
    enablers may not load there (Plant objects then arrive as proxies and are
    exploded to their proxy graphics) -- the Plant 3D session engine is the
    faithful one for Plant content; this one is for plain AutoCAD geometry,
    batches, and keeping your session free."""
    steps = []
    out = {"ok": False, "steps": steps, "head": "", "lines": []}

    def fail(msg):
        steps.append((msg, False))
        return out

    exe = console_exe()
    if not exe:
        return fail("accoreconsole.exe not found next to acad.exe; set \"accoreconsole_exe\" in settings.json.")
    dll = plugin_dll()
    if not os.path.isfile(dll):
        return fail("DwgFlatPublisher.dll is not built yet: run plugin\\build.bat once.")
    if not LOCK.acquire(label):
        return fail(LOCK.busy_msg())
    try:
        os.makedirs(JOB_DIR, exist_ok=True)
        for p in (JOB_OUT, JOB_PROGRESS):
            safe_fs.remove_scratch(p)
        info = plugin_info()
        shadow = dll   # each build has its own file, never rewritten: load it directly
        job = dict(job)
        job.setdefault("progressPath", JOB_PROGRESS)
        with safe_fs.open_scratch(JOB_REQ, "w", encoding="utf-8") as fh:
            json.dump(job, fh, indent=2)
        script = os.path.join(JOB_DIR, "job.scr")
        with safe_fs.open_scratch(script, "w", encoding="ascii", errors="replace") as fh:
            fh.write('(setvar "SECURELOAD" 0)\n')
            fh.write('(setvar "FILEDIA" 0)\n')
            fh.write('(command "_.NETLOAD" "%s")\n' % shadow.replace("\\", "/"))
            fh.write(info["command"] + "\n")
            fh.write("_.QUIT\n_Y\n")
        args = [exe, "/s", script, "/l", "en-US"]
        seed = str(settings().get("console_seed_dwg") or "").strip()
        if seed and os.path.isfile(seed):
            args[1:1] = ["/i", seed]
        if settings().get("console_product_plnt3d", True):
            args += ["/product", "PLNT3D"]
        log_path = os.path.join(JOB_DIR, "console.log")
        steps.append(("Started AutoCAD Core Console (background, no UI)", True))
        with safe_fs.open_scratch(log_path, "wb") as logf:
            proc = subprocess.Popen(args, cwd=os.path.dirname(exe), stdout=logf, stderr=subprocess.STDOUT,
                                    stdin=subprocess.DEVNULL, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            _console_proc["p"] = proc
            deadline = time.time() + wait
            text = ""
            while time.time() < deadline:
                if os.path.exists(JOB_OUT):
                    try:
                        with open(JOB_OUT, encoding="utf-8-sig", errors="replace") as fh:
                            text = fh.read()
                    except OSError:
                        text = ""
                    if text.strip():
                        break
                if proc.poll() is not None:
                    time.sleep(1.0)   # a last chance for job.out to appear
                    if os.path.exists(JOB_OUT):
                        continue
                    break
                time.sleep(0.5)
            try:
                proc.wait(timeout=60)
            except Exception:
                proc.kill()
            _console_proc["p"] = None
        if not text.strip():
            tail = ""
            try:
                with open(log_path, "rb") as fh:
                    tail = fh.read()[-3000:].decode("utf-16-le", errors="ignore").replace("\x00", "")
            except Exception:
                pass
            if _console_proc.get("cancelled"):
                _console_proc["cancelled"] = False
                return fail("Cancelled.")
            return fail("The console exited without a result. Last output: " + " ".join(tail.split())[-400:])
        lines = [l.rstrip() for l in text.splitlines() if l.strip()]
        out["head"] = lines[0] if lines else "ERR|empty result"
        out["lines"] = lines[1:]
        out["ok"] = out["head"].startswith("OK|")
        steps.append(("Console finished: " + (out["head"].split("|")[1] if "|" in out["head"] else out["head"]), out["ok"]))
        return out
    except Exception as exc:
        return fail("Unexpected: %s" % str(exc)[:200])
    finally:
        LOCK.release()


def cancel_console() -> bool:
    p = _console_proc.get("p")
    if p is not None and p.poll() is None:
        _console_proc["cancelled"] = True
        p.kill()
        return True
    return False


def open_read_only(path: str) -> dict:
    """Open a drawing in AutoCAD read-only (to eyeball a published result)."""
    steps = []
    if not os.path.isfile(path):
        return {"ok": False, "msg": "File not found: " + path}
    if not LOCK.acquire("an open-in-CAD"):
        return {"ok": False, "msg": LOCK.busy_msg()}
    try:
        import pythoncom
        pythoncom.CoInitialize()
        try:
            acad = get_acad(steps)
            nq = not_quiescent(acad)
            if nq:
                return {"ok": False, "msg": nq}
            com_retry(lambda: acad.Documents.Open(path, True))
            try:
                acad.Visible = True
            except Exception:
                pass
            return {"ok": True, "msg": "Opened read-only in AutoCAD"}
        finally:
            pythoncom.CoUninitialize()
    except Exception as exc:
        return {"ok": False, "msg": str(exc)[:200]}
    finally:
        LOCK.release()
