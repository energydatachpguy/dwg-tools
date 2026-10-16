"""
DWG Tools setup check. Read-only: it only looks, it never changes a file.

    venv\\Scripts\\python.exe tools\\check_setup.py

Prints one line per item with OK / WARN / FAIL and what to do about it.
setup.bat runs it at the end; run it any time something doesn't work.
Exit code 1 if anything FAILs.
"""
import json
import os
import platform
import socket
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "webapp"))

results = []


def item(status, what, fix=""):
    results.append(status)
    print("  %-4s  %s" % (status, what))
    if fix:
        print("        -> " + fix)


def main():
    print()
    print("  DWG Tools setup check  (%s)" % ROOT)
    print("  " + "-" * 60)

    # Windows + Python
    if platform.system() != "Windows":
        item("FAIL", "Windows is needed (this is %s)" % platform.system())
    v = sys.version_info
    if v >= (3, 10):
        item("OK", "Python %d.%d.%d" % (v[0], v[1], v[2]))
    else:
        item("FAIL", "Python %d.%d is too old" % (v[0], v[1]), "Install Python 3.10 or newer from python.org")
    if " " in ROOT:
        item("WARN", "The folder path has spaces in it",
             "It works, but a path like C:\\Tools\\dwg-tools avoids trouble")

    # Packages
    try:
        import flask  # noqa: F401
        item("OK", "Flask")
    except Exception:
        item("FAIL", "Flask is not installed", "Run setup.bat again")
    try:
        import win32com.client  # noqa: F401
        item("OK", "pywin32 (the bridge to AutoCAD)")
    except Exception:
        item("FAIL", "pywin32 is not installed", "Run setup.bat again")

    # Settings
    sp = os.path.join(ROOT, "settings.json")
    s = {}
    if os.path.isfile(sp):
        try:
            with open(sp, encoding="utf-8") as fh:
                s = json.load(fh)
            item("OK", "settings.json")
        except Exception as e:
            item("FAIL", "settings.json can't be read (%s)" % e,
                 "Fix it, or delete it and run setup.bat to get a fresh one")
    else:
        item("FAIL", "settings.json is missing", "Run setup.bat")

    # AutoCAD
    try:
        import cad
        exe = cad.acad_exe()
    except Exception as e:
        exe = ""
        item("FAIL", "Could not load the app's AutoCAD module (%s)" % e)
    if exe:
        year = cad._acad_year(exe)
        name = "AutoCAD %s" % (year or "(unknown version)")
        if year >= 2025:
            item("FAIL", "%s found at %s" % (name, exe),
                 "AutoCAD 2025+ runs on .NET 8, which the plugin doesn't support yet. "
                 "If 2020-2024 is also installed, put its acad.exe in settings.json as \"acad_exe\"")
        else:
            item("OK", "%s at %s" % (name, exe))
        plant = os.path.isdir(os.path.join(os.path.dirname(exe), "PLNT3D"))
        args = cad.acad_args()
        if plant:
            item("OK", "Plant 3D is installed alongside it")
        elif args:
            item("WARN", "Plant 3D was not found next to AutoCAD",
                 "For plain AutoCAD put  \"acad_args\": []  in settings.json")
    else:
        item("FAIL", "AutoCAD / Plant 3D was not found under C:\\Program Files\\Autodesk",
             "Put the full path of acad.exe in settings.json as \"acad_exe\"")

    # Plugin build
    cj = os.path.join(ROOT, "plugin", "bin", "current.json")
    if os.path.isfile(cj):
        try:
            with open(cj, encoding="utf-8-sig") as fh:
                j = json.load(fh)
            dll = os.path.join(ROOT, "plugin", "bin", j["dll"])
            if os.path.isfile(dll):
                item("OK", "Plugin built: %s (command %s)" % (j["dll"], j["command"]))
            else:
                item("FAIL", "plugin\\bin\\current.json points to a missing file", "Run plugin\\build.bat")
        except Exception as e:
            item("FAIL", "plugin\\bin\\current.json can't be read (%s)" % e, "Run plugin\\build.bat")
    else:
        item("FAIL", "The AutoCAD plugin is not built yet", "Run plugin\\build.bat and read plugin\\build.log")
    log = os.path.join(ROOT, "plugin", "build.log")
    if os.path.isfile(log):
        with open(log, encoding="utf-8", errors="replace") as fh:
            if "BUILD FAILED" in fh.read():
                item("FAIL", "The last plugin build failed", "Open plugin\\build.log to see why")

    # Layer standard
    try:
        import layer_standard
        p = layer_standard.path(ROOT)
        if p and os.path.isfile(p):
            item("OK", "Layer standard: %s" % os.path.basename(p))
        else:
            item("WARN", "%s is not in the folder" % layer_standard.FILE_NAME,
                 "Copy it next to run.bat; without it no standard layers are offered")
    except Exception as e:
        item("WARN", "Layer standard could not be checked (%s)" % e)

    # Port
    port = int(s.get("port", 5014))
    sk = socket.socket()
    try:
        sk.bind(("127.0.0.1", port))
        item("OK", "Port %d is free" % port)
    except OSError:
        item("WARN", "Port %d is in use (DWG Tools may already be running)" % port,
             "Close the other DWG Tools window, or set another \"port\" in settings.json")
    finally:
        sk.close()

    # Running AutoCAD (optional)
    try:
        import win32com.client
        app = win32com.client.GetActiveObject("AutoCAD.Application")
        item("OK", "AutoCAD is running (version %s)" % app.Version)
    except Exception:
        item("INFO", "AutoCAD is not running right now (fine; start Plant 3D before you use the app)")

    print("  " + "-" * 60)
    if "FAIL" in results:
        print("  Something needs fixing (see FAIL above).")
        return 1
    print("  All good. Start the app with run.bat.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
