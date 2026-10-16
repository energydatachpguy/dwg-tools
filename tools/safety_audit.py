"""
Start-up safety audit for DWG Tools.

Fails (exit code 1) if any source file other than the two guard modules
contains a call that can delete, move, replace or overwrite a file:

  C#     plugin/src/SafeFs.cs   is the only file allowed to call File.Delete,
         Directory.Delete, File.Move, File.Replace, File.WriteAllText,
         File.AppendAllText, FileMode.Create, File.Copy or Database.SaveAs.
  Python webapp/safe_fs.py      is the only file allowed to call os.remove,
         os.unlink, os.rmdir, os.replace, os.rename, shutil.* or open() for writing.

run.bat runs this before every start and refuses to start the app if it fails,
so a future edit cannot quietly add a way to delete or overwrite files.
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CS_GUARD = os.path.join("plugin", "src", "SafeFs.cs")
PY_GUARD = os.path.join("webapp", "safe_fs.py")

CS_FORBIDDEN = re.compile(r"\b(File\.(Delete|Move|Replace|WriteAllText|WriteAllBytes|WriteAllLines|AppendAllText|AppendAllLines|Copy|Create)|"
                          r"Directory\.(Delete|Move)|FileMode\.(Create|CreateNew|Truncate|Append|OpenOrCreate)|\.SaveAs\s*\(|"
                          r"FileInfo\s*\([^)]*\)\s*\.\s*(Delete|MoveTo|Replace|CopyTo))")
PY_FORBIDDEN = re.compile(r"\b(os\.(remove|unlink|rmdir|removedirs|replace|rename|renames|truncate)|shutil\.\w+|"
                          r"open\s*\([^)]*['\"](w|a|x|r\+|wb|ab|xb|w\+|a\+)['\"])")


def scan(rel_dir, ext, pattern, guard):
    bad = []
    base = os.path.join(ROOT, rel_dir)
    for dirpath, _, files in os.walk(base):
        if "venv" in dirpath.split(os.sep) or "__pycache__" in dirpath:
            continue
        for f in files:
            if not f.endswith(ext):
                continue
            path = os.path.join(dirpath, f)
            rel = os.path.relpath(path, ROOT)
            if os.path.normcase(rel) == os.path.normcase(guard):
                continue
            with open(path, encoding="utf-8", errors="replace") as fh:
                for n, line in enumerate(fh, 1):
                    code = line.split("//")[0] if ext == ".cs" else line.split("#")[0]
                    if pattern.search(code):
                        bad.append("%s:%d: %s" % (rel, n, line.strip()))
    return bad


def main():
    bad = scan(os.path.join("plugin", "src"), ".cs", CS_FORBIDDEN, CS_GUARD)
    bad += scan("webapp", ".py", PY_FORBIDDEN, PY_GUARD)
    if bad:
        print("SAFETY AUDIT FAILED: file-changing calls outside the guard modules:")
        for b in bad:
            print("   " + b)
        print("The app will not start until these go through SafeFs.cs / safe_fs.py.")
        return 1
    print("   Safety audit passed: every delete / move / overwrite goes through the guard.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
