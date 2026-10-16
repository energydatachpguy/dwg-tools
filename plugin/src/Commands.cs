// DwgFlatPublisher -- AutoCAD commands.
//
//   FLATANALYZE   preflight report for a drawing (nothing is changed)
//   PUBLISHFLAT   flatten a drawing to a new DWG (interactive prompts)
//   FLATJOB       same, driven by a request file -- used by the Flask app so it
//                 never has to type into prompts (same protocol idea as
//                 PnPSheetCopy):
//                   %TEMP%\dwgflat\job.json   request  {"action": "publish"|"analyze"|"testgen", ...options}
//                   %TEMP%\dwgflat\job.out    result   line 1 "OK|<status>|<output>|<report>" or "ERR|<why>"
//                                                      then one log line per step
//                   %TEMP%\dwgflat\progress.txt        "pct|stage|message", rewritten while running
//   FLATTESTGEN   writes a set of test drawings (nested / rotated / mirrored /
//                 non-uniform / attributes / xrefs / missing xref / 1M-entity
//                 stress file) to a folder
//
// Load with NETLOAD. Every command works on a separate in-memory database;
// the drawing open in the editor is not touched.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Exception = System.Exception;

[assembly: CommandClass(typeof(DwgFlat.Commands))]
[assembly: ExtensionApplication(typeof(DwgFlat.PluginInit))]

namespace DwgFlat
{
    public class PluginInit : IExtensionApplication
    {
        public void Initialize()
        {
            try
            {
                Editor ed = AcApp.DocumentManager.MdiActiveDocument.Editor;
                ed.WriteMessage("\nDwgFlatPublisher " + typeof(PluginInit).Assembly.GetName().Version
                    + " loaded: FLATANALYZE, PUBLISHFLAT, FLATJOB, FLATTESTGEN\n");
            }
            catch { }
        }
        public void Terminate() { }
    }

    public class Commands
    {
        public static string JobDir
        {
            get { return Path.Combine(Path.GetTempPath(), "dwgflat"); }
        }

        static Editor Ed
        {
            get
            {
                Document d = AcApp.DocumentManager.MdiActiveDocument;
                return d == null ? null : d.Editor;
            }
        }

        static void Say(string s)
        {
            Editor ed = Ed;
            if (ed != null) ed.WriteMessage("\n" + s);
        }

        static string CurrentDrawingPath()
        {
            Document d = AcApp.DocumentManager.MdiActiveDocument;
            if (d == null) return "";
            string n = d.Name;
            return File.Exists(n) ? n : "";
        }

        // ---------------------------------------------------------------- FLATANALYZE
        // (no longer a typed command: every command name must be unique per build)
        public void FlatAnalyze()
        {
            Editor ed = Ed;
            string src = AskOpen(ed, "Drawing to analyze", CurrentDrawingPath());
            if (src == null) return;
            PromptKeywordOptions pk = new PromptKeywordOptions("\nExport JSON report? [Yes/No]", "Yes No");
            pk.Keywords.Default = "Yes";
            PromptResult pr = ed.GetKeywords(pk);
            bool json = pr.Status != PromptStatus.OK || pr.StringResult == "Yes";
            // reports go to the tool's own folder, never next to the drawing in the project
            string jsonPath = json ? Path.Combine(JobDir, Path.GetFileNameWithoutExtension(src) + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".flatanalysis.json") : null;
            Progress prog = new Progress(Say, null);
            RunWithMeter(prog, "Analyzing", delegate
            {
                Dictionary<string, object> r = Analyze(src, prog, jsonPath);
                Say("---- FLATANALYZE ----");
                foreach (string l in Analyzer.Summary(r)) Say(l);
                if (jsonPath != null) Say("JSON: " + jsonPath);
            });
        }

        public static Dictionary<string, object> Analyze(string src, Progress prog, string jsonPath)
        {
            Report rep = new Report();
            PublishOptions opt = new PublishOptions();
            opt.AllowMissingXrefs = true;   // analysis reports missing xrefs, never aborts
            Database prev = HostApplicationServices.WorkingDatabase;
            Dictionary<string, object> r;
            try
            {
                using (SideDb side = new SideDb(false, false))
                {
                    Database db = side.Db;
                    prog.Stage("Reading " + Path.GetFileName(src) + " (read-only, in memory)");
                    db.ReadDwgFile(src, FileOpenMode.OpenForReadAndAllShare, true, "");
                    db.CloseInput(true);
                    HostApplicationServices.WorkingDatabase = db;
                    prog.Stage("Resolving xrefs");
                    XrefStage xs = new XrefStage(db, opt, rep, prog);
                    xs.Run(true);   // binds in memory only, so xref contents are counted
                    prog.Stage("Scanning block definitions");
                    r = new Analyzer(db, rep).Run(src, xs);
                }
            }
            finally
            {
                HostApplicationServices.WorkingDatabase = prev;
            }
            r["diagnostics"] = rep.ToDictionary();
            r["analyzedAt"] = DateTime.Now.ToString("s");
            if (!string.IsNullOrEmpty(jsonPath)) { Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)); SafeFs.WriteText(jsonPath, Report.ToJson(r)); }
            return r;
        }

        // ---------------------------------------------------------------- PUBLISHFLAT
        // (no longer a typed command: every command name must be unique per build)
        public void PublishFlat()
        {
            Editor ed = Ed;
            PublishOptions opt = new PublishOptions();
            string src = AskOpen(ed, "Source drawing to flatten", CurrentDrawingPath());
            if (src == null) return;
            opt.Source = src;

            PromptKeywordOptions pm = new PromptKeywordOptions("\nMode [Full_flat/Optimized]", "Full_flat Optimized");
            pm.Keywords.Default = "Full_flat";
            PromptResult rm = ed.GetKeywords(pm);
            if (rm.Status == PromptStatus.Cancel) return;
            opt.Mode = (rm.Status == PromptStatus.OK && rm.StringResult == "Optimized") ? PublishMode.Optimized : PublishMode.FullFlat;

            PromptKeywordOptions pl = new PromptKeywordOptions("\nLayers [Preserve/Single]", "Preserve Single");
            pl.Keywords.Default = "Preserve";
            PromptResult rl = ed.GetKeywords(pl);
            if (rl.Status == PromptStatus.Cancel) return;
            if (rl.Status == PromptStatus.OK && rl.StringResult == "Single")
            {
                opt.Layers = LayerPolicy.SingleLayer;
                PromptStringOptions ps = new PromptStringOptions("\nOutput layer name <EQUIPMENT_REFERENCE>: ");
                ps.AllowSpaces = true;
                PromptResult rs = ed.GetString(ps);
                if (rs.Status == PromptStatus.OK && rs.StringResult.Trim().Length > 0) opt.OutputLayer = rs.StringResult.Trim();
            }

            string def = Publisher.DefaultOutput(src, opt.Mode);
            PromptSaveFileOptions psf = new PromptSaveFileOptions("\nSave flattened drawing as");
            psf.Filter = "Drawing (*.dwg)|*.dwg";
            psf.InitialFileName = def;
            psf.InitialDirectory = Path.GetDirectoryName(def);
            PromptFileNameResult fr = ed.GetFileNameForSave(psf);
            if (fr.Status == PromptStatus.Cancel) return;
            opt.Output = fr.Status == PromptStatus.OK && !string.IsNullOrEmpty(fr.StringResult) ? fr.StringResult : def;

            Progress prog = new Progress(Say, null);
            PublishResult res = null;
            RunWithMeter(prog, "Flattening", delegate { res = Publisher.Publish(opt, prog); });
            if (res != null)
            {
                Say("---- PUBLISHFLAT: " + res.Status + " ----");
                Say(res.Message);
                Say("Report: " + res.ReportPath);
            }
        }

        // ---------------------------------------------------------------- FLATJOB
        // The ONE command, named per build (DWGT<date-time>, see build.bat), so a
        // newer build always loads next to an older one already in Plant 3D.
        [CommandMethod("DWGTOOLS", BuildInfo.Command, CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void FlatJob()
        {
            string dir = JobDir;
            string req = Path.Combine(dir, "job.json");
            string outPath = Path.Combine(dir, "job.out");
            Progress prog = null;
            try
            {
                if (!File.Exists(req)) { WriteOut(outPath, "ERR|No request file at " + req, null); return; }
                Dictionary<string, object> job = Report.ParseJsonObject(File.ReadAllText(req, Encoding.UTF8));
                string action = PublishOptions.Str(job, "action", "publish").ToLowerInvariant();
                // progress always goes to the scratch folder, whatever the request says
                string progressPath = Path.Combine(dir, "progress.txt");
                prog = new Progress(Say, progressPath);
                prog.Write(0, "started " + BuildInfo.Command, true);   // tells the app this build picked up the request

                if (action == "analyze")
                {
                    string src = PublishOptions.Str(job, "source", "");
                    string json = PublishOptions.Str(job, "reportPath", Path.Combine(dir, "analysis.json"));
                    if (!json.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || Util.SameFile(json, src))
                        json = Path.Combine(dir, "analysis.json");
                    Dictionary<string, object> r = null;
                    RunWithMeter(prog, "Analyzing", delegate { r = Analyze(src, prog, json); });
                    prog.Write(100, "done", true);
                    WriteOut(outPath, "OK|ANALYZED|" + src + "|" + json, Analyzer.Summary(r));
                }
                else if (action == "tree")
                {
                    string src = PublishOptions.Str(job, "source", "");
                    if (!File.Exists(src)) throw new PublishAbortException("Drawing not found: " + src);
                    Database liveDb = null;
                    if (PublishOptions.Str(job, "live", "") == "1")
                    {
                        Document ad = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
                        if (ad == null || !Util.SameFile(ad.Name, src)) throw new PublishAbortException(Path.GetFileName(src) + " is not the active drawing in AutoCAD. Switch to it and refresh again.");
                        liveDb = ad.Database;
                    }
                    string root = PublishOptions.Str(job, "projectRoot", "");
                    int maxFiles = Math.Max(1, PublishOptions.Int(job, "maxFiles", 500));
                    string json = PublishOptions.Str(job, "reportPath", Path.Combine(dir, "tree.json"));
                    if (!json.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || Util.SameFile(json, src))
                        json = Path.Combine(dir, "tree.json");
                    string work = Path.Combine(dir, "treework_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    Dictionary<string, object> r = null;
                    RunWithMeter(prog, "Building reference tree", delegate { r = TreeBuilder.Build(src, work, root, maxFiles, false, prog, liveDb); });
                    SafeFs.WriteText(json, Report.ToJson(r));
                    prog.Write(100, "done", true);
                    WriteOut(outPath, "OK|TREE|" + src + "|" + json, prog.Lines);
                }
                else if (action == "find" || action == "pick" || action == "wblock" || action == "clear" || action == "ping" || action == "selected" || action == "insert" || action == "insertcheck")
                {
                    Document doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
                    if (doc == null) throw new PublishAbortException("No drawing is open in AutoCAD.");
                    string want = PublishOptions.Str(job, "drawing", "");
                    if (want.Length > 0 && !Util.SameFile(want, doc.Name))
                        throw new PublishAbortException("The drawing active in AutoCAD is " + doc.Name + ", not " + want + ". Switch to it and try again.");
                    Database db = doc.Database;
                    Editor ed = doc.Editor;
                    // one or more rows (name paths) at the same level; per row, the ticked handles per level
                    List<List<string>> rows = new List<List<string>>();
                    List<List<HashSet<string>>> allows = new List<List<HashSet<string>>>();
                    string rowsS = PublishOptions.Str(job, "rows", "");
                    if (rowsS.Length == 0) rowsS = PublishOptions.Str(job, "path", "");
                    string[] allowRows = PublishOptions.Str(job, "allow", "").Split('\u0002');
                    string[] rowParts = rowsS.Split('\u0002');
                    for (int ri = 0; ri < rowParts.Length; ri++)
                    {
                        List<string> nm = new List<string>();
                        foreach (string n in rowParts[ri].Split('\u0001')) if (n.Length > 0) nm.Add(n);
                        if (nm.Count == 0) continue;
                        List<HashSet<string>> al = new List<HashSet<string>>();
                        if (ri < allowRows.Length)
                            foreach (string lvl in allowRows[ri].Split(';'))
                            {
                                HashSet<string> hs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                foreach (string h in lvl.Split(',')) if (h.Trim().Length > 0) hs.Add(h.Trim());
                                al.Add(hs);
                            }
                        rows.Add(nm); allows.Add(al);
                    }
                    List<string> names = rows.Count > 0 ? rows[0] : new List<string>();
                    string json = Path.Combine(dir, "wblock.json");
                    Dictionary<string, object> r = new Dictionary<string, object>();
                    r["drawing"] = doc.Name;
                    r["plugin"] = typeof(WblockTool).Assembly.Location;   // the app checks this is the newest build
                    try
                    {
                        if (action == "clear") { WblockTool.Clear(db); r["status"] = "OK"; }
                        else if (action == "insert" || action == "insertcheck")
                        {
                            if (action == "insert") WblockTool.Clear(db);
                            List<WblockTool.InsertItem> items = new List<WblockTool.InsertItem>();
                            foreach (string row in PublishOptions.Str(job, "items", "").Split('\u0002'))
                            {
                                string[] f = row.Split('\u0001');
                                if (f.Length < 5 || f[0].Trim().Length == 0) continue;
                                WblockTool.InsertItem it = new WblockTool.InsertItem();
                                it.File = f[0]; it.Block = f[1]; it.Layer = f[2];
                                double.TryParse(f[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out it.X);
                                double.TryParse(f[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out it.Y);
                                int col; if (f.Length > 5 && int.TryParse(f[5], out col)) it.Color = col;
                                items.Add(it);
                            }
                            if (items.Count == 0) throw new PublishAbortException("Nothing to insert.");
                            List<List<ObjectId>> repl = new List<List<ObjectId>>();
                            foreach (string c in PublishOptions.Str(job, "replace", "").Split(';')) if (c.Trim().Length > 0) repl.Add(WblockTool.FromHandles(db, c.Split(',')));
                            Dictionary<string, object> made = null;
                            if (action == "insertcheck") made = WblockTool.InsertCheck(db, items, repl);
                            else
                            {
                                bool purge = PublishOptions.Str(job, "purge", "") == "1", redefine = PublishOptions.Str(job, "redefine", "") == "1", current = PublishOptions.Str(job, "current", "") == "1";
                                RunWithMeter(prog, "Inserting", delegate { made = WblockTool.Insert(db, ed, items, repl, purge, redefine, current, prog); });
                            }
                            foreach (KeyValuePair<string, object> kv in made) r[kv.Key] = kv.Value;
                            r["status"] = "OK";
                            if (action == "insert") ed.WriteMessage("\nDWG Tools: inserted " + items.Count + " WBLOCK(s). Nothing was saved; UNDO reverses it.\n");
                        }
                        else if (action == "selected")
                        {
                            // what is selected in CAD right now (or, if nothing is, ask for a click);
                            // reads only, changes nothing
                            string how;
                            r["selected"] = WblockTool.Selected(ed, db, out how);
                            r["how"] = how;
                            r["status"] = "OK";
                        }
                        else if (action == "ping")
                        {
                            // "which plugin are you, and what is open?" -- changes nothing
                            List<string> docs = new List<string>();
                            foreach (Document d in Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager) { try { docs.Add(d.Name); } catch { } }
                            r["documents"] = docs;
                            r["pluginBuilt"] = File.GetLastWriteTime(typeof(WblockTool).Assembly.Location).ToString("yyyy-MM-dd HH:mm");
                            r["status"] = "OK";
                        }
                        else if (action == "wblock")
                        {
                            WblockTool.Clear(db);
                            List<List<ObjectId>> chains = new List<List<ObjectId>>();
                            string chainsS = PublishOptions.Str(job, "chains", "");
                            if (chainsS.Length == 0) chainsS = PublishOptions.Str(job, "handles", "");
                            foreach (string c in chainsS.Split(';')) if (c.Trim().Length > 0) chains.Add(WblockTool.FromHandles(db, c.Split(',')));
                            if (chains.Count == 0) throw new PublishAbortException("No block chosen.");
                            string save = PublishOptions.Str(job, "savePath", "");
                            Dictionary<string, object> made = null;
                            List<StdLayer> std = StdLayer.Parse(PublishOptions.Str(job, "layers", ""));
                            RunWithMeter(prog, "Making the WBLOCK", delegate { made = WblockTool.Make(db, chains, save, std, prog); });
                            foreach (KeyValuePair<string, object> kv in made) r[kv.Key] = kv.Value;
                            r["status"] = "OK";
                            ed.WriteMessage("\nDWG Tools: saved " + save + "\n");
                        }
                        else
                        {
                            if (names.Count == 0) throw new PublishAbortException("No block chosen.");
                            using (Transaction tr = db.TransactionManager.StartTransaction())
                            {
                                bool truncated = false;
                                List<Occurrence> occ = new List<Occurrence>();
                                List<int> rowOf = new List<int>();
                                for (int ri = 0; ri < rows.Count; ri++)
                                {
                                    bool tr1;
                                    List<Occurrence> found = WblockTool.Find(db, tr, rows[ri], allows[ri], Math.Max(1, 3000 - occ.Count), out tr1);
                                    truncated |= tr1;
                                    if (found.Count == 0) throw new PublishAbortException("No " + rows[ri][rows[ri].Count - 1] + " found in model space of " + Path.GetFileName(doc.Name) + " along " + string.Join(" > ", rows[ri].ToArray()) + (allows[ri].Exists(h => h.Count > 0) ? " (with the placements you ticked)" : "") + ".");
                                    foreach (Occurrence o in found) { occ.Add(o); rowOf.Add(ri); }
                                    if (occ.Count >= 3000) { truncated = true; break; }
                                }
                                int focus = PublishOptions.Int(job, "focus", 0) - 1;   // 1-based; 0 = all
                                if (focus >= occ.Count) focus = -1;
                                WblockTool.LightUp(ed, db, tr, occ, focus);
                                List<object> list = new List<object>();
                                for (int i = 0; i < occ.Count; i++)
                                {
                                    Dictionary<string, object> od = WblockTool.Describe(occ[i], i, tr);
                                    od["row"] = rowOf[i]; od["block"] = rows[rowOf[i]][rows[rowOf[i]].Count - 1];
                                    list.Add(od);
                                }
                                r["occurrences"] = list;
                                r["truncated"] = truncated;
                                if (action == "pick")
                                {
                                    tr.Commit();
                                    int k = WblockTool.Pick(ed, db, occ, rows.Count == 1 ? names[names.Count - 1] : "block");
                                    r["picked"] = k + 1;
                                    if (k >= 0)
                                    {
                                        using (Transaction t2 = db.TransactionManager.StartTransaction())
                                        { WblockTool.LightUp(ed, db, t2, occ, k); t2.Commit(); }
                                    }
                                }
                                else tr.Commit();
                                ed.WriteMessage("\nDWG Tools: " + occ.Count + " x " + (rows.Count == 1 ? names[names.Count - 1] : rows.Count + " blocks") + " highlighted. REGEN (or the page's Remove highlight button) clears it.\n");
                            }
                            r["status"] = "OK";
                        }
                    }
                    catch (PublishAbortException ex) { r["status"] = "FAILED"; r["message"] = ex.Message; }
                    catch (PublishCancelledException) { r["status"] = "FAILED"; r["message"] = "Cancelled."; }
                    r["log"] = prog.Lines;
                    SafeFs.WriteScratchText(json, Report.ToJson(r));
                    prog.Write(100, "done", true);
                    string st = Convert.ToString(r["status"]);
                    WriteOut(outPath, (st == "OK" ? "OK|" + action.ToUpperInvariant() : "ERR|" + Convert.ToString(r["message"]).Replace("|", "/")) + "|" + json, prog.Lines);
                }
                else if (action == "testgen")
                {
                    string folder = PublishOptions.Str(job, "folder", "");
                    int stress = PublishOptions.Int(job, "stressInstances", 1000);
                    List<string> made = null;
                    RunWithMeter(prog, "Generating test drawings", delegate { made = TestGen.Generate(folder, stress, prog); });
                    prog.Write(100, "done", true);
                    WriteOut(outPath, "OK|GENERATED|" + folder + "|", made);
                }
                else if (action != "publish")
                {
                    WriteOut(outPath, "ERR|This DWG Tools plugin does not know the request '" + action + "'. Close Plant 3D completely and open it again so it loads the current plugin.", prog.Lines);
                }
                else
                {
                    PublishOptions opt = PublishOptions.FromDictionary(job);
                    opt.ProgressPath = progressPath;
                    PublishResult res = null;
                    RunWithMeter(prog, "Flattening", delegate { res = Publisher.Publish(opt, prog); });
                    string head = (res.Ok ? "OK|" : "ERR|") + res.Status + "|" + res.OutputPath + "|" + res.ReportPath;
                    if (!res.Ok) head = "ERR|" + res.Status + ": " + res.Message.Replace("|", "/") + "|" + res.ReportPath;
                    WriteOut(outPath, head, prog.Lines);
                }
            }
            catch (Exception ex)
            {
                try { if (prog != null) foreach (string ln in ex.ToString().Split('\n')) prog.Log("  " + ln.TrimEnd()); } catch { }
                WriteOut(outPath, "ERR|" + ex.Message.Replace("|", "/").Replace("\r", " ").Replace("\n", " "),
                    prog == null ? null : prog.Lines);
            }
        }

        static void WriteOut(string path, string head, IEnumerable<string> lines)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(head);
            if (lines != null) foreach (string l in lines) sb.AppendLine(l);
            // both files live in the scratch folder (%TEMP%\dwgflat); SafeFs refuses anything else
            string tmp = path + ".tmp";
            SafeFs.Delete(tmp);
            SafeFs.WriteScratchText(tmp, sb.ToString());
            SafeFs.Delete(path);
            SafeFs.MoveToNew(tmp, path);   // the app sees the result file complete or not at all
        }

        // ---------------------------------------------------------------- FLATCLEAR
        // (no longer a typed command: every command name must be unique per build)
        public void FlatClear()
        {
            Document doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
            WblockTool.Clear(doc == null ? null : doc.Database);
            try { doc.Editor.UpdateScreen(); } catch { }
        }

        // ---------------------------------------------------------------- FLATTESTGEN
        // (no longer a typed command: every command name must be unique per build)
        public void FlatTestGen()
        {
            Editor ed = Ed;
            PromptStringOptions ps = new PromptStringOptions("\nFolder for test drawings: ");
            ps.AllowSpaces = true;
            ps.DefaultValue = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "DWG Consolidate", "testdata", "run_" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            ps.UseDefaultValue = true;
            PromptResult pr = ed.GetString(ps);
            if (pr.Status != PromptStatus.OK) return;
            PromptIntegerOptions pi = new PromptIntegerOptions("\nStress-test instances (1000 = ~1M entities, 0 = skip)");
            pi.DefaultValue = 1000; pi.UseDefaultValue = true; pi.AllowNegative = false;
            PromptIntegerResult ir = ed.GetInteger(pi);
            int n = ir.Status == PromptStatus.OK ? ir.Value : 1000;
            Progress prog = new Progress(Say, null);
            RunWithMeter(prog, "Generating", delegate
            {
                foreach (string f in TestGen.Generate(pr.StringResult, n, prog)) Say("  " + f);
            });
        }

        // ---------------------------------------------------------------- helpers
        static string AskOpen(Editor ed, string what, string def)
        {
            PromptOpenFileOptions po = new PromptOpenFileOptions("\n" + what);
            po.Filter = "Drawing (*.dwg)|*.dwg";
            if (!string.IsNullOrEmpty(def))
            {
                po.InitialFileName = def;
                po.InitialDirectory = Path.GetDirectoryName(def);
            }
            PromptFileNameResult r = ed.GetFileNameForOpen(po);
            if (r.Status == PromptStatus.OK && !string.IsNullOrEmpty(r.StringResult)) return r.StringResult;
            if (r.Status == PromptStatus.None && !string.IsNullOrEmpty(def)) return def;
            return null;
        }

        delegate void Work();

        static void RunWithMeter(Progress prog, string title, Work work)
        {
            ProgressMeter pm = new ProgressMeter();
            int last = 0;
            try
            {
                pm.Start(title);
                pm.SetLimit(100);
                prog.Meter = delegate (double pct)
                {
                    int p = (int)pct;
                    while (last < p && last < 100) { pm.MeterProgress(); last++; }
                };
                work();
            }
            finally
            {
                prog.Meter = null;
                try { pm.Stop(); pm.Dispose(); } catch { }
            }
        }
    }
}
