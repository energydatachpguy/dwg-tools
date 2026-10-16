// DwgFlatPublisher -- shared plumbing: options, report, progress/log output.
//
// Written for the stock Windows C# compiler (csc.exe v4.0.30319 = C# 5):
// no string interpolation, no ?. operator, no nameof, no expression bodies.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace DwgFlat
{
    public enum PublishMode { FullFlat, Optimized }
    public enum LayerPolicy { PreserveLayers, SingleLayer }
    public enum DependencyPolicy { Keep, Exclude, Fail }

    /// <summary>Everything a publication run needs to know. Built from the
    /// job file (FLATJOB) or from command-line prompts (PUBLISHFLAT).</summary>
    public class PublishOptions
    {
        public string Source = "";
        public string Output = "";
        public PublishMode Mode = PublishMode.FullFlat;
        public LayerPolicy Layers = LayerPolicy.PreserveLayers;
        public string OutputLayer = "EQUIPMENT_REFERENCE";
        /// <summary>true = BIND type (xref layers become XREF$0$LAYER, never
        /// merged with host layers, so appearance is kept). false = INSERT type.</summary>
        public bool BindKeepsXrefNames = true;
        public bool AllowMissingXrefs = false;
        public bool IncludeUnloadedXrefs = false;
        public DependencyPolicy NonDwgDependencies = DependencyPolicy.Keep;
        /// <summary>Explode custom (Plant 3D etc.) and proxy entities to standard
        /// AutoCAD geometry. When false they are carried over as-is (the output
        /// then needs the object enabler to display them).</summary>
        public bool ExplodeCustomObjects = true;
        /// <summary>Any geometry that could not be expanded fails the publication.</summary>
        public bool Strict = true;
        public bool PurgeUnused = true;
        public bool VerifyGeometry = true;
        public int VerifySamples = 200;
        public bool HashSource = true;
        /// <summary>SINGLE_LAYER only: drop geometry whose original layer is
        /// off / frozen / no-plot (it would otherwise become visible).</summary>
        public bool OmitHiddenInSingleLayer = true;
        /// <summary>Keep objects on no-plot layers (they are visible on screen). Used by the WBLOCK helper.</summary>
        public bool KeepNoPlot = false;
        public bool ExtentsInfoOnly = false;   // WBLOCK: the point-by-point coordinate check replaces this rough box test
        public int BatchSize = 2000;
        public string ReportPath = "";   // default: <output>.flatreport.json
        public string ProgressPath = ""; // optional: "pct|message" rewritten as work proceeds

        public static PublishOptions FromDictionary(IDictionary<string, object> d)
        {
            PublishOptions o = new PublishOptions();
            o.Source = Str(d, "source", o.Source);
            o.Output = Str(d, "output", o.Output);
            string mode = Str(d, "mode", "FULL_FLAT").ToUpperInvariant();
            o.Mode = (mode == "OPTIMIZED") ? PublishMode.Optimized : PublishMode.FullFlat;
            string lp = Str(d, "layerPolicy", "PRESERVE_LAYERS").ToUpperInvariant();
            o.Layers = (lp == "SINGLE_LAYER") ? LayerPolicy.SingleLayer : LayerPolicy.PreserveLayers;
            o.OutputLayer = Str(d, "outputLayer", o.OutputLayer);
            if (string.IsNullOrEmpty(o.OutputLayer)) o.OutputLayer = "EQUIPMENT_REFERENCE";
            o.BindKeepsXrefNames = Str(d, "bindType", "BIND").ToUpperInvariant() != "INSERT";
            o.AllowMissingXrefs = Bool(d, "allowMissingXrefs", o.AllowMissingXrefs);
            o.IncludeUnloadedXrefs = Bool(d, "includeUnloadedXrefs", o.IncludeUnloadedXrefs);
            string nd = Str(d, "nonDwgDependencies", "KEEP").ToUpperInvariant();
            o.NonDwgDependencies = nd == "EXCLUDE" ? DependencyPolicy.Exclude : (nd == "FAIL" ? DependencyPolicy.Fail : DependencyPolicy.Keep);
            o.ExplodeCustomObjects = Bool(d, "explodeCustomObjects", o.ExplodeCustomObjects);
            o.Strict = Bool(d, "strict", o.Strict);
            o.PurgeUnused = Bool(d, "purge", o.PurgeUnused);
            o.VerifyGeometry = Bool(d, "verifyGeometry", o.VerifyGeometry);
            o.VerifySamples = Int(d, "verifySamples", o.VerifySamples);
            o.HashSource = Bool(d, "hashSource", o.HashSource);
            o.OmitHiddenInSingleLayer = Bool(d, "omitHiddenInSingleLayer", o.OmitHiddenInSingleLayer);
            o.BatchSize = Math.Max(100, Int(d, "batchSize", o.BatchSize));
            o.ReportPath = Str(d, "reportPath", o.ReportPath);
            o.ProgressPath = Str(d, "progressPath", o.ProgressPath);
            return o;
        }

        public Dictionary<string, object> ToDictionary()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["source"] = Source; d["output"] = Output;
            d["mode"] = Mode == PublishMode.Optimized ? "OPTIMIZED" : "FULL_FLAT";
            d["layerPolicy"] = Layers == LayerPolicy.SingleLayer ? "SINGLE_LAYER" : "PRESERVE_LAYERS";
            d["outputLayer"] = OutputLayer;
            d["bindType"] = BindKeepsXrefNames ? "BIND" : "INSERT";
            d["allowMissingXrefs"] = AllowMissingXrefs;
            d["includeUnloadedXrefs"] = IncludeUnloadedXrefs;
            d["nonDwgDependencies"] = NonDwgDependencies.ToString().ToUpperInvariant();
            d["explodeCustomObjects"] = ExplodeCustomObjects;
            d["strict"] = Strict; d["purge"] = PurgeUnused;
            d["verifyGeometry"] = VerifyGeometry; d["verifySamples"] = VerifySamples;
            d["omitHiddenInSingleLayer"] = OmitHiddenInSingleLayer;
            d["batchSize"] = BatchSize;
            return d;
        }

        internal static string Str(IDictionary<string, object> d, string k, string def)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v) && v != null) return Convert.ToString(v, CultureInfo.InvariantCulture);
            return def;
        }
        internal static bool Bool(IDictionary<string, object> d, string k, bool def)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return def;
            if (v is bool) return (bool)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
            return s == "1" || s == "true" || s == "yes" || s == "y";
        }
        internal static int Int(IDictionary<string, object> d, string k, int def)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return def;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return def; }
        }
    }

    public enum Severity { Info, Warning, Error }

    /// <summary>Collects diagnostics, counters and sections; serialises to JSON.
    /// Counts every issue; keeps full detail for the first N of each code so a
    /// million identical warnings cannot exhaust memory.</summary>
    public class Report
    {
        const int DetailPerCode = 200;
        public readonly Dictionary<string, object> Sections = new Dictionary<string, object>();
        public readonly SortedDictionary<string, long> Counters = new SortedDictionary<string, long>(StringComparer.Ordinal);
        readonly List<Dictionary<string, object>> _issues = new List<Dictionary<string, object>>();
        readonly Dictionary<string, long> _issueCounts = new Dictionary<string, long>();
        readonly Dictionary<string, Severity> _issueSeverity = new Dictionary<string, Severity>();
        public int Errors, Warnings;

        public void Count(string key) { Count(key, 1); }
        public void Count(string key, long n)
        {
            long v; Counters.TryGetValue(key, out v); Counters[key] = v + n;
        }
        public long Get(string key) { long v; Counters.TryGetValue(key, out v); return v; }

        public void Issue(Severity sev, string code, string message) { Issue(sev, code, message, null); }
        public void Issue(Severity sev, string code, string message, string where)
        {
            long n; _issueCounts.TryGetValue(code, out n); _issueCounts[code] = n + 1;
            Severity prev;
            if (!_issueSeverity.TryGetValue(code, out prev) || sev > prev) _issueSeverity[code] = sev;
            if (sev == Severity.Error) Errors++; else if (sev == Severity.Warning) Warnings++;
            if (n < DetailPerCode)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["severity"] = sev.ToString().ToUpperInvariant();
                d["code"] = code;
                d["message"] = message;
                if (!string.IsNullOrEmpty(where)) d["where"] = where;
                _issues.Add(d);
            }
        }
        public bool HasIssue(string code) { return _issueCounts.ContainsKey(code); }

        public Dictionary<string, object> ToDictionary()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> kv in Sections) d[kv.Key] = kv.Value;
            Dictionary<string, object> c = new Dictionary<string, object>();
            foreach (KeyValuePair<string, long> kv in Counters) c[kv.Key] = kv.Value;
            d["counters"] = c;
            List<object> summary = new List<object>();
            foreach (KeyValuePair<string, long> kv in _issueCounts)
            {
                Dictionary<string, object> s = new Dictionary<string, object>();
                s["code"] = kv.Key; s["count"] = kv.Value;
                s["severity"] = _issueSeverity[kv.Key].ToString().ToUpperInvariant();
                summary.Add(s);
            }
            d["issueSummary"] = summary;
            d["issues"] = _issues;
            d["errorCount"] = Errors;
            d["warningCount"] = Warnings;
            return d;
        }

        public static string ToJson(object o)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            js.RecursionLimit = 256;
            return Pretty(js.Serialize(o));
        }

        public static Dictionary<string, object> ParseJsonObject(string text)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return js.Deserialize<Dictionary<string, object>>(text);
        }

        /// <summary>Indent compact JSON so reports are readable in Notepad.</summary>
        static string Pretty(string json)
        {
            StringBuilder sb = new StringBuilder(json.Length * 2);
            int indent = 0; bool inStr = false;
            for (int i = 0; i < json.Length; i++)
            {
                char ch = json[i];
                if (inStr)
                {
                    sb.Append(ch);
                    if (ch == '\\' && i + 1 < json.Length) { sb.Append(json[++i]); }
                    else if (ch == '"') inStr = false;
                    continue;
                }
                switch (ch)
                {
                    case '"': inStr = true; sb.Append(ch); break;
                    case '{': case '[':
                        sb.Append(ch);
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) { sb.Append(json[++i]); break; }
                        indent++; sb.Append('\n').Append(' ', indent * 2); break;
                    case '}': case ']':
                        indent--; sb.Append('\n').Append(' ', indent * 2).Append(ch); break;
                    case ',': sb.Append(ch).Append('\n').Append(' ', indent * 2); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>Thrown when the user presses Esc (or the job is cancelled).</summary>
    public class PublishCancelledException : Exception
    {
        public PublishCancelledException() : base("Cancelled by user (Esc).") { }
    }

    /// <summary>Thrown for a condition that stops publication with a clear message.</summary>
    public class PublishAbortException : Exception
    {
        public PublishAbortException(string msg) : base(msg) { }
    }

    /// <summary>Progress + log sink: the AutoCAD command line, an optional
    /// progress file (read by the Flask app) and an in-memory log.</summary>
    public class Progress
    {
        readonly Action<string> _echo;
        readonly string _progressPath;
        public readonly List<string> Lines = new List<string>();
        DateTime _lastWrite = DateTime.MinValue;
        public readonly DateTime Started = DateTime.Now;
        string _stage = "";
        /// <summary>Optional: drives AutoCAD's status-bar progress meter (0..100).</summary>
        public Action<double> Meter;

        public Progress(Action<string> echo, string progressPath)
        {
            _echo = echo; _progressPath = progressPath;
        }

        public void Stage(string name)
        {
            _stage = name;
            Log(name + "...");
            Write(-1, name, true);
        }

        public void Log(string msg)
        {
            string line = string.Format(CultureInfo.InvariantCulture, "[{0,7:0.0}s] {1}", (DateTime.Now - Started).TotalSeconds, msg);
            Lines.Add(line);
            if (_echo != null) { try { _echo(line); } catch { } }
        }

        /// <summary>pct in 0..100, or -1 for "unknown". Throttled to ~4 writes/s.</summary>
        public void Write(double pct, string msg, bool force)
        {
            DateTime now = DateTime.Now;
            if (!force && (now - _lastWrite).TotalMilliseconds < 250) return;
            _lastWrite = now;
            if (Meter != null && pct >= 0) { try { Meter(pct); } catch { } }
            if (string.IsNullOrEmpty(_progressPath)) return;
            try
            {
                string s = string.Format(CultureInfo.InvariantCulture, "{0:0.0}|{1}|{2}", pct, _stage, msg);
                SafeFs.WriteScratchText(_progressPath, s);
            }
            catch { }
        }
    }

    /// <summary>An in-memory database that may be made AutoCAD's working
    /// database. Dispose puts the previous working database back BEFORE the
    /// side database is destroyed (disposing the current working database is a
    /// known way to crash AutoCAD).</summary>
    public sealed class SideDb : IDisposable
    {
        public readonly Autodesk.AutoCAD.DatabaseServices.Database Db;
        readonly Autodesk.AutoCAD.DatabaseServices.Database _prev;

        public SideDb(bool buildDefaultDrawing, bool makeWorking)
        {
            _prev = Autodesk.AutoCAD.DatabaseServices.HostApplicationServices.WorkingDatabase;
            Db = new Autodesk.AutoCAD.DatabaseServices.Database(buildDefaultDrawing, true);
            if (makeWorking) MakeWorking();
        }

        public void MakeWorking() { Autodesk.AutoCAD.DatabaseServices.HostApplicationServices.WorkingDatabase = Db; }

        public void RestoreWorking()
        {
            try
            {
                if (Autodesk.AutoCAD.DatabaseServices.HostApplicationServices.WorkingDatabase == Db && _prev != null)
                    Autodesk.AutoCAD.DatabaseServices.HostApplicationServices.WorkingDatabase = _prev;
            }
            catch { }
        }

        public void Dispose()
        {
            RestoreWorking();
            Db.Dispose();
        }
    }

    internal static class Util
    {
        public static string Sha256(string path)
        {
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20))
            {
                byte[] h = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(64);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static string NormPath(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant(); }
            catch { return (p ?? "").ToLowerInvariant(); }
        }

        public static string Sanitize(string name)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in name ?? "")
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
                else sb.Append('_');
            }
            string s = sb.ToString();
            if (s.Length > 60) s = s.Substring(0, 60);
            return s.Length == 0 ? "BLK" : s;
        }

        // ---- file identity (same file through UNC vs mapped drive, 8.3 names, links) ----
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out ByHandleFileInformation info);

        static bool FileId(string path, out ulong volume, out ulong index)
        {
            volume = 0; index = 0;
            try
            {
                if (!File.Exists(path)) return false;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    ByHandleFileInformation info;
                    if (!GetFileInformationByHandle(fs.SafeFileHandle, out info)) return false;
                    volume = info.VolumeSerialNumber;
                    index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>True when a and b name the same file on disk, however spelled.</summary>
        public static bool SameFile(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (NormPath(a) == NormPath(b)) return true;
            ulong va, ia, vb, ib;
            if (FileId(a, out va, out ia) && FileId(b, out vb, out ib)) return va == vb && ia == ib;
            return false;
        }

        public static string Fmt(double v) { return v.ToString("0.######", CultureInfo.InvariantCulture); }
    }
}
