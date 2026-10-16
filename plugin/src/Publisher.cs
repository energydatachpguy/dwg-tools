// DwgFlatPublisher -- the staged publication pipeline.
//
//   1. Check the request (source exists, output is not the source or one of its xrefs)
//   2. Fingerprint the source file (SHA-256)
//   3. Read the source into a SIDE database (never opened in the editor,
//      never saved back) and release the file
//   4. Resolve + bind xrefs (in memory)          -> XrefStage
//   5. Expand the hierarchy                       -> Flattener
//   6. Purge what is no longer referenced
//   7. Save to a temporary file next to the destination
//   8. Reopen the saved file and validate it      -> Validator
//   9. Only if valid: promote to the destination (previous version kept as
//      <name>.previous.dwg). If not valid: kept as <name>.REJECTED.dwg and the
//      existing published drawing is left untouched.
//  10. Re-fingerprint the source to prove it was not modified; write report.

using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;

namespace DwgFlat
{
    public class PublishResult
    {
        public bool Ok;
        public string Status = "FAILED";   // SUCCESS | SUCCESS_WITH_WARNINGS | FAILED | CANCELLED
        public string Message = "";
        public string OutputPath = "";
        public string ReportPath = "";
        public Report Report;
    }

    public static class Publisher
    {
        public static PublishResult Publish(PublishOptions opt, Progress prog)
        {
            PublishResult res = new PublishResult();
            Report rep = new Report();
            res.Report = rep;
            rep.Sections["tool"] = "DwgFlatPublisher " + typeof(Publisher).Assembly.GetName().Version;
            rep.Sections["startedAt"] = DateTime.Now.ToString("s");
            rep.Sections["options"] = opt.ToDictionary();
            Database prevWorking = HostApplicationServices.WorkingDatabase;
            string temp = null;
            string srcHash = null;
            try
            {
                // ---- 1. request --------------------------------------------------------
                prog.Stage("Checking request");
                if (string.IsNullOrEmpty(opt.Source) || !File.Exists(opt.Source))
                    throw new PublishAbortException("Source drawing not found: " + opt.Source);
                opt.Source = Path.GetFullPath(opt.Source);
                if (string.IsNullOrEmpty(opt.Output))
                    opt.Output = DefaultOutput(opt.Source, opt.Mode);
                opt.Output = Path.GetFullPath(opt.Output);
                if (!opt.Output.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)) opt.Output += ".dwg";
                if (Util.NormPath(opt.Output) == Util.NormPath(opt.Source))
                    throw new PublishAbortException("The output path is the source drawing. The source is never overwritten -- choose another file name.");
                string outDir = Path.GetDirectoryName(opt.Output);
                if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
                res.OutputPath = opt.Output;
                res.ReportPath = (string.IsNullOrEmpty(opt.ReportPath) || !opt.ReportPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    ? opt.Output + ".flatreport.json" : Path.GetFullPath(opt.ReportPath);
                // Backups and rejected results get unique date-stamped names, so no
                // earlier file is ever replaced or deleted to make room for them.
                string stem = Path.GetFileNameWithoutExtension(opt.Output);
                string backupPath = SafeFs.Stamped(outDir, stem, "previous", ".dwg");
                string rejectedPath = SafeFs.Stamped(outDir, stem, "REJECTED", ".dwg");
                string reportTarget = res.ReportPath;
                res.ReportPath = "";   // nothing is written until the paths are proven safe
                if (!string.IsNullOrEmpty(opt.ProgressPath) && !SafeFs.InScratch(opt.ProgressPath))
                    throw new PublishAbortException("progressPath must be inside the scratch folder.");
                GuardWrites(new string[] { opt.Output, backupPath, rejectedPath, reportTarget, opt.ProgressPath },
                            new string[] { opt.Source });
                // Fail fast, before any work: an existing output or report may only be
                // replaced if this tool created it and nobody has changed it since.
                SafeFs.AssertMayOverwrite(opt.Output);
                if (File.Exists(reportTarget) && !SafeFs.IsOurs(reportTarget))
                    reportTarget = SafeFs.Stamped(outDir, stem, "flatreport", ".json");
                res.ReportPath = reportTarget;
                rep.Sections["source"] = opt.Source;
                rep.Sections["output"] = opt.Output;
                WarnIfOpenAndModified(opt.Source, rep);

                // ---- 2. fingerprint --------------------------------------------------------
                FileInfo fi = new FileInfo(opt.Source);
                rep.Sections["sourceSizeBytes"] = fi.Length;
                DateTime srcTime = fi.LastWriteTimeUtc;
                if (opt.HashSource)
                {
                    prog.Stage("Fingerprinting source (SHA-256)");
                    srcHash = Util.Sha256(opt.Source);
                    rep.Sections["sourceSha256Before"] = srcHash;
                }

                using (SideDb side = new SideDb(false, false))
                {
                    Database db = side.Db;
                    // ---- 3. side database ------------------------------------------------
                    prog.Stage("Reading source into an isolated in-memory database");
                    db.ReadDwgFile(opt.Source, FileOpenMode.OpenForReadAndAllShare, true, "");
                    db.CloseInput(true);   // fully loaded; source file handle released
                    HostApplicationServices.WorkingDatabase = db;

                    // ---- 4. xrefs ------------------------------------------------------
                    prog.Stage("Resolving and binding external references");
                    XrefStage xs = new XrefStage(db, opt, rep, prog);
                    xs.Run(true);
                    rep.Sections["xrefs"] = xs.ToDictionary();
                    GuardWrites(new string[] { opt.Output, backupPath, rejectedPath, reportTarget, opt.ProgressPath },
                                xs.ResolvedFiles);
                    if (rep.HasIssue("XREF_BIND_FAILED") || rep.HasIssue("XREF_REMAINS"))
                    {
                        if (opt.Strict) throw new PublishAbortException("Some xrefs could not be bound; see the report.");
                    }

                    // ---- 5. expand -----------------------------------------------------
                    Flattener fl = new Flattener(db, opt, rep, prog);
                    fl.Run();
                    prog.Log(string.Format("Created {0:N0} entities.", rep.Get("output.entitiesCreated")));

                    // ---- 6. purge ------------------------------------------------------
                    prog.Stage("Purging unreferenced definitions");
                    long purged = Purge(db, opt.PurgeUnused);
                    rep.Count("purged.objects", purged);
                    prog.Log(string.Format("Purged {0:N0} unreferenced definitions/symbols.", purged));

                    long msCount = 0;
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                        foreach (ObjectId id in ms) msCount++;
                        if (opt.Layers == LayerPolicy.SingleLayer)
                        {
                            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                            if (lt.Has(opt.OutputLayer)) db.Clayer = lt[opt.OutputLayer];
                        }
                        tr.Commit();
                    }

                    // ---- 7. save to temp ---------------------------------------------------
                    prog.Stage("Saving");
                    temp = Path.Combine(outDir, Path.GetFileNameWithoutExtension(opt.Output) + ".publishing-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".dwg");
                    HostApplicationServices.WorkingDatabase = prevWorking;
                    SafeFs.SaveDatabase(db, temp);     // new, uniquely named file
                    SafeFs.RecordCreated(temp);
                    rep.Sections["outputSizeBytes"] = new FileInfo(temp).Length;

                    // ---- 8. validate -----------------------------------------------------
                    prog.Stage("Reopening the saved file and validating");
                    Validator v = new Validator(opt, rep, prog);
                    Dictionary<string, object> vr = v.Run(temp, msCount, fl);
                    rep.Sections["validation"] = vr;
                    rep.Sections["fullyFlattened"] = v.FullyFlattened;
                    rep.Sections["selfContained"] = v.SelfContained;

                    bool strictFail = opt.Strict && rep.Errors > 0;
                    if (!v.Passed || strictFail)
                    {
                        string rejected = rejectedPath;
                        try { SafeFs.MoveToNew(temp, rejected); SafeFs.RecordCreated(rejected); temp = null; } catch { }
                        res.Status = "FAILED";
                        res.Message = "Validation failed (" + rep.Errors + " error(s)); nothing was published and any existing output is untouched. Inspect " + rejected;
                        rep.Sections["rejectedFile"] = rejected;
                    }
                    else
                    {
                        // ---- 9. promote ---------------------------------------------------
                        prog.Stage("Publishing");
                        string backup = null;
                        if (File.Exists(opt.Output))
                        {
                            backup = backupPath;
                            // only a previous output of this tool can be replaced; it is kept, never deleted
                            try { SafeFs.ReplaceKeepingBackup(temp, opt.Output, backup); }
                            catch (IOException ex)
                            {
                                throw new PublishAbortException("Could not replace " + opt.Output + " (" + ex.Message + "). Is it open in AutoCAD? The validated result is kept as " + temp);
                            }
                            rep.Sections["previousVersion"] = backup;
                            SafeFs.RecordCreated(backup);
                        }
                        else SafeFs.MoveToNew(temp, opt.Output);
                        SafeFs.RecordCreated(opt.Output);
                        temp = null;
                        res.Ok = true;
                        res.Status = rep.Warnings > 0 || rep.Errors > 0 ? "SUCCESS_WITH_WARNINGS" : "SUCCESS";
                        res.Message = "Published " + opt.Output;
                    }
                }

                // ---- 10. source untouched? ------------------------------------------------
                FileInfo after = new FileInfo(opt.Source);
                bool sameTime = after.LastWriteTimeUtc == srcTime;
                if (opt.HashSource)
                {
                    string h2 = Util.Sha256(opt.Source);
                    rep.Sections["sourceSha256After"] = h2;
                    rep.Sections["sourceUnchanged"] = h2 == srcHash && sameTime;
                    if (h2 != srcHash)
                        rep.Issue(Severity.Warning, "SOURCE_CHANGED_DURING_RUN", "The source file changed on disk while publishing (someone saved it?). This tool never writes to it; republish to pick up the new version.");
                }
                else rep.Sections["sourceUnchanged"] = sameTime;
            }
            catch (PublishCancelledException)
            {
                res.Status = "CANCELLED"; res.Message = "Cancelled. Nothing was published.";
            }
            catch (PublishAbortException ex)
            {
                res.Status = "FAILED"; res.Message = ex.Message;
                rep.Issue(Severity.Error, "ABORTED", ex.Message);
            }
            catch (System.Exception ex)
            {
                res.Status = "FAILED"; res.Message = "Unexpected error: " + ex.Message;
                rep.Issue(Severity.Error, "EXCEPTION", ex.ToString());
            }
            finally
            {
                try { HostApplicationServices.WorkingDatabase = prevWorking; } catch { }
                if (temp != null) { try { SafeFs.Delete(temp); } catch { } }   // our own temp file only
            }

            rep.Sections["status"] = res.Status;
            rep.Sections["message"] = res.Message;
            rep.Sections["finishedAt"] = DateTime.Now.ToString("s");
            rep.Sections["elapsedSeconds"] = Math.Round((DateTime.Now - prog.Started).TotalSeconds, 2);
            rep.Sections["log"] = prog.Lines;
            if (!string.IsNullOrEmpty(res.ReportPath))
            {
                try { SafeFs.WriteText(res.ReportPath, Report.ToJson(rep.ToDictionary())); }
                catch (System.Exception ex) { prog.Log("Could not write report: " + ex.Message); }
            }
            prog.Write(100, res.Status + ": " + res.Message, true);
            return res;
        }

        /// <summary>Refuse to start if any file this run may write, replace or
        /// delete is the source drawing or one of its xrefs -- compared by file
        /// identity, so P:\x.dwg and \\server\share\x.dwg are the same file.</summary>
        static void GuardWrites(IEnumerable<string> writes, IEnumerable<string> protectedFiles)
        {
            foreach (string w in writes)
            {
                if (string.IsNullOrEmpty(w)) continue;
                foreach (string p in protectedFiles)
                    if (Util.SameFile(w, p))
                        throw new PublishAbortException("Refusing to run: " + w + " is the source drawing or one of its xrefs (" + p
                            + "), and this run could write or replace it. Choose a different output name/folder.");
            }
        }

        public static string DefaultOutput(string source, PublishMode mode)
        {
            return Path.Combine(Path.GetDirectoryName(source),
                Path.GetFileNameWithoutExtension(source) + (mode == PublishMode.Optimized ? "_OPT.dwg" : "_FLAT.dwg"));
        }

        /// <summary>Repeatedly purge until nothing more becomes purgeable (blocks
        /// only become purgeable once their parents are gone).</summary>
        internal static long Purge(Database db, bool symbols)
        {
            long total = 0;
            for (int pass = 0; pass < 1000; pass++)   // ends when a pass erases nothing
            {
                ObjectIdCollection cand = new ObjectIdCollection();
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    foreach (ObjectId id in bt)
                    {
                        BlockTableRecord b = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (!b.IsLayout && !id.IsErased) cand.Add(id);
                    }
                    if (symbols)
                    {
                        AddAll(cand, tr, db.LayerTableId);
                        AddAll(cand, tr, db.LinetypeTableId);
                        AddAll(cand, tr, db.TextStyleTableId);
                        AddAll(cand, tr, db.DimStyleTableId);
                        DBDictionary nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                        foreach (string dn in new string[] { "ACAD_IMAGE_DICT", "ACAD_PDFDEFINITIONS", "ACAD_DWFDEFINITIONS", "ACAD_DGNDEFINITIONS", "ACAD_MLEADERSTYLE", "ACAD_TABLESTYLE" })
                        {
                            if (!nod.Contains(dn)) continue;
                            DBDictionary d = tr.GetObject(nod.GetAt(dn), OpenMode.ForRead) as DBDictionary;
                            if (d != null) foreach (DBDictionaryEntry de in d) cand.Add(de.Value);
                        }
                    }
                    // never purge the styles the header points at
                    try { cand.Remove(db.MLeaderstyle); } catch { }
                    try { cand.Remove(db.Tablestyle); } catch { }
                    try { cand.Remove(db.Clayer); } catch { }
                    try { cand.Remove(db.Textstyle); } catch { }
                    try { cand.Remove(db.Dimstyle); } catch { }
                    db.Purge(cand);
                    int n = 0;
                    foreach (ObjectId id in cand)
                    {
                        try
                        {
                            DBObject o = tr.GetObject(id, OpenMode.ForWrite);
                            o.Erase();
                            n++;
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception) { }
                    }
                    tr.Commit();
                    total += n;
                    if (n == 0) break;
                }
            }
            return total;
        }

        static void AddAll(ObjectIdCollection c, Transaction tr, ObjectId tableId)
        {
            SymbolTable t = (SymbolTable)tr.GetObject(tableId, OpenMode.ForRead);
            foreach (ObjectId id in t) if (!id.IsErased) c.Add(id);
        }

        static void WarnIfOpenAndModified(string path, Report rep)
        {
            try
            {
                foreach (Autodesk.AutoCAD.ApplicationServices.Document d in Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager)
                {
                    string n = d.Name;
                    if (Util.NormPath(n) != Util.NormPath(path)) continue;
                    rep.Issue(Severity.Info, "SOURCE_OPEN",
                        "The source drawing is open in AutoCAD. The SAVED file on disk is what gets published -- save first if you have changes you want included.");
                    break;
                }
            }
            catch { }
        }
    }
}
