// DwgFlatPublisher -- the ONLY place allowed to delete, move, replace or
// overwrite files.
//
// Rule: the tool may delete or overwrite a file only if
//   (a) it lives in the tool's own scratch folder (%TEMP%\dwgflat), or
//   (b) the tool created it itself: it is recorded in the ledger
//       (%LOCALAPPDATA%\DwgTools\created_files.jsonl) AND its current SHA-256
//       still matches what was recorded (so a file someone else saved over,
//       or a different file with the same name, is never touched).
// Anything else is refused with an exception. Drawings that are read
// (sources, xrefs) are never passed to these functions; they are only ever
// opened for reading.
//
// tools\safety_audit.py checks at every start-up that no other source file
// calls File.Delete / Directory.Delete / File.Move / File.Replace / SaveAs
// directly.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DwgFlat
{
    public static class SafeFs
    {
        static readonly object _lock = new object();

        public static string ScratchRoot
        {
            get { return Util.NormPath(Path.Combine(Path.GetTempPath(), "dwgflat")); }
        }

        public static string LedgerPath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DwgTools");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "created_files.jsonl");
            }
        }

        public static bool InScratch(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return Util.NormPath(path).StartsWith(ScratchRoot + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Record a file this tool just created (call after it is complete).</summary>
        public static void RecordCreated(string path)
        {
            if (!File.Exists(path) || InScratch(path)) return;
            string hash = Util.Sha256(path);
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["path"] = Path.GetFullPath(path);
            d["sha256"] = hash;
            d["at"] = DateTime.Now.ToString("s");
            d["by"] = Environment.UserName;
            string line = Json.Serialize(d) + "\n";
            lock (_lock) File.AppendAllText(LedgerPath, line, Encoding.UTF8);
        }

        /// <summary>True when the tool created this exact file (same path, same content).</summary>
        public static bool IsOurs(string path)
        {
            if (!File.Exists(path)) return false;
            string want = Util.NormPath(path);
            string recorded = null;
            try
            {
                foreach (string line in File.ReadAllLines(LedgerPath))
                {
                    if (line.Trim().Length == 0) continue;
                    Dictionary<string, object> d = Json.ParseObject(line);
                    object p; object h;
                    if (d.TryGetValue("path", out p) && Util.NormPath(Convert.ToString(p)) == want && d.TryGetValue("sha256", out h))
                        recorded = Convert.ToString(h);   // last record for this path wins
                }
            }
            catch { return false; }
            return recorded != null && recorded == Util.Sha256(path);
        }

        static void AssertMayChange(string path, string what)
        {
            if (!File.Exists(path)) return;
            if (InScratch(path) || IsOurs(path)) return;
            throw new PublishAbortException("Refusing to " + what + " " + path +
                ": a file already exists there that DWG Tools did not create (or it has been changed since). " +
                "Nothing was deleted or overwritten. Choose a different file name or folder.");
        }

        /// <summary>Throws unless writing to 'path' cannot destroy anything that isn't ours.</summary>
        public static void AssertMayOverwrite(string path) { AssertMayChange(path, "overwrite"); }

        public static void Delete(string path)
        {
            if (!File.Exists(path)) return;
            AssertMayChange(path, "delete");
            File.Delete(path);
        }

        /// <summary>Delete a scratch folder. Only ever inside %TEMP%\dwgflat.</summary>
        public static void DeleteScratchDirectory(string dir)
        {
            if (!Directory.Exists(dir)) return;
            if (!InScratch(dir)) throw new PublishAbortException("Refusing to delete folder outside the scratch area: " + dir);
            Directory.Delete(dir, true);
        }

        /// <summary>Move a file to a destination that must not exist yet.</summary>
        public static void MoveToNew(string from, string to)
        {
            if (!InScratch(from) && !IsOurs(from))
                throw new PublishAbortException("Refusing to move " + from + ": not created by DWG Tools.");
            if (File.Exists(to)) throw new PublishAbortException("Refusing to move onto existing file " + to + ".");
            File.Move(from, to);
        }

        /// <summary>Replace 'target' (which must be ours) with 'replacement'; the old
        /// target is kept at 'backup', which must not exist yet. Nothing is deleted.</summary>
        public static void ReplaceKeepingBackup(string replacement, string target, string backup)
        {
            AssertMayOverwrite(target);
            if (File.Exists(backup)) throw new PublishAbortException("Refusing to overwrite existing backup " + backup + ".");
            File.Replace(replacement, target, backup, true);
        }

        /// <summary>Write a file in the scratch folder only.</summary>
        public static void WriteScratchText(string path, string text)
        {
            if (!InScratch(path)) throw new PublishAbortException("Refusing to write outside the scratch folder: " + path);
            File.WriteAllText(path, text, Encoding.UTF8);
        }

        /// <summary>Create a new file in the scratch folder (byte copies for the tree).</summary>
        public static FileStream CreateScratchFile(string path)
        {
            if (!InScratch(path)) throw new PublishAbortException("Refusing to create a file outside the scratch folder: " + path);
            return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        }

        /// <summary>A folder the self-test may write into: it must not exist yet, or be empty.</summary>
        public static void AssertFreshFolder(string folder)
        {
            if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length > 0)
                throw new PublishAbortException("Refusing to generate test drawings into " + folder +
                    ": the folder already has files in it. Use a new, empty folder.");
            Directory.CreateDirectory(folder);
        }

        /// <summary>Write a text file (report, manifest) without ever destroying a file that isn't ours.</summary>
        public static void WriteText(string path, string text)
        {
            AssertMayOverwrite(path);
            File.WriteAllText(path, text, Encoding.UTF8);
            RecordCreated(path);
        }

        /// <summary>Save a side database to a path that must not hold anything that isn't ours.</summary>
        public static void SaveDatabase(Autodesk.AutoCAD.DatabaseServices.Database db, string path)
        {
            AssertMayOverwrite(path);
            db.SaveAs(path, Autodesk.AutoCAD.DatabaseServices.DwgVersion.Current);
        }

        /// <summary>"name.previous-20261014-163012.dwg" -- unique, so no older file is ever replaced.</summary>
        public static string Stamped(string folder, string stem, string tag, string ext)
        {
            string p = Path.Combine(folder, stem + "." + tag + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ext);
            int n = 1;
            while (File.Exists(p)) p = Path.Combine(folder, stem + "." + tag + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + (++n) + ext);
            return p;
        }
    }
}
