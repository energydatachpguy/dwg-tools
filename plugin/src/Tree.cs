// DwgFlatPublisher -- reference tree (read-only inspection).
//
// Builds the full picture of how a drawing is put together: which xrefs it
// attaches (and what THOSE attach), which blocks are inserted where, how
// deep the nesting goes, and which files live outside the project.
//
// Safety model: AutoCAD never opens an original file.
//   * each original is byte-copied (read-only, shared) into a work folder,
//     and fingerprinted (SHA-256) during the copy;
//   * AutoCAD reads only the COPY, without resolving xrefs;
//   * the xref paths stored inside the copy are turned into original paths
//     ourselves (relative to the original's folder), and those files are
//     copied the same way -- recursively, each file once;
//   * at the end every original is fingerprinted again and compared.
// Nothing is ever written next to an original.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace DwgFlat
{
    public static class TreeBuilder
    {
        class FileWork
        {
            public int Id;
            public string Original, Copy;
            public int Depth;
            public string FoundVia = "";
            public Dictionary<string, object> Node;
        }

        public static Dictionary<string, object> Build(string source, string workDir, string projectRoot, int maxFiles, bool keepCopies, Progress prog)
        {
            return Build(source, workDir, projectRoot, maxFiles, keepCopies, prog, null);
        }

        /// <summary>live: the drawing's database as it is open in AutoCAD right now (unsaved
        /// changes included). It is only read, the same as a copy would be.</summary>
        public static Dictionary<string, object> Build(string source, string workDir, string projectRoot, int maxFiles, bool keepCopies, Progress prog, Database live)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["source"] = source;
            result["projectRoot"] = projectRoot ?? "";
            result["builtAt"] = DateTime.Now.ToString("s");
            result["method"] = "Each original was byte-copied (read-only) to a work folder; AutoCAD read only the copies, without resolving xrefs. Originals were fingerprinted (SHA-256) before and after.";
            if (live != null) result["method"] = "The drawing was read as it is open in AutoCAD (unsaved changes included), read only. Its xrefs were byte-copied and read from the copies.";
            result["live"] = live != null;

            Directory.CreateDirectory(workDir);
            string normRoot = string.IsNullOrEmpty(projectRoot) ? "" : Util.NormPath(projectRoot) + "\\";

            List<FileWork> all = new List<FileWork>();
            Dictionary<string, FileWork> byPath = new Dictionary<string, FileWork>(StringComparer.OrdinalIgnoreCase);
            Queue<FileWork> queue = new Queue<FileWork>();
            Dictionary<string, string> hashBefore = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            FileWork first = new FileWork();
            first.Original = Path.GetFullPath(source);
            first.Depth = 0;
            first.FoundVia = "selected";
            Enqueue(first, all, byPath, queue);

            int truncated = 0;
            while (queue.Count > 0)
            {
                if (HostApplicationServices.Current.UserBreak()) throw new PublishCancelledException();
                FileWork fw = queue.Dequeue();
                prog.Write(-1, "Reading " + Path.GetFileName(fw.Original) + " (" + all.Count + " files found so far)", false);
                prog.Log("Reading " + fw.Original);
                Dictionary<string, object> node = new Dictionary<string, object>();
                fw.Node = node;
                node["id"] = fw.Id;
                node["path"] = fw.Original;
                node["name"] = Path.GetFileName(fw.Original);
                node["depth"] = fw.Depth;
                node["foundVia"] = fw.FoundVia;
                node["outsideProject"] = normRoot.Length > 0 && !Util.NormPath(fw.Original).StartsWith(normRoot, StringComparison.OrdinalIgnoreCase);
                node["location"] = LocationRoot(fw.Original);
                try
                {
                    FileInfo fi = new FileInfo(fw.Original);
                    node["sizeBytes"] = fi.Length;
                    node["modified"] = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                    List<Dictionary<string, object>> xrefs;
                    if (fw.Depth == 0 && live != null)
                    {
                        // the open drawing itself, as it is in CAD now
                        node["status"] = "read";
                        node["live"] = true;
                        node["sha256"] = "";
                        xrefs = ReadDb(live, fw, node, prog);
                    }
                    else
                    {
                        fw.Copy = Path.Combine(workDir, "f" + fw.Id + "_" + Util.Sanitize(Path.GetFileNameWithoutExtension(fw.Original)) + ".dwg");
                        hashBefore[fw.Original] = CopyAndHash(fw.Original, fw.Copy);
                        node["sha256"] = hashBefore[fw.Original];
                        node["status"] = "read";
                        xrefs = ReadFile(fw, node, prog);
                    }
                    // follow xrefs
                    foreach (Dictionary<string, object> x in xrefs)
                    {
                        string found = x.ContainsKey("resolvedPath") ? (string)x["resolvedPath"] : "";
                        if (string.IsNullOrEmpty(found)) continue;
                        FileWork child;
                        if (byPath.TryGetValue(found, out child)) { x["fileId"] = child.Id; continue; }
                        if (all.Count >= maxFiles) { truncated++; x["fileId"] = -1; continue; }
                        child = new FileWork();
                        child.Original = found;
                        child.Depth = fw.Depth + 1;
                        child.FoundVia = Path.GetFileName(fw.Original) + " > " + (string)x["name"];
                        Enqueue(child, all, byPath, queue);
                        x["fileId"] = child.Id;
                    }
                }
                catch (PublishCancelledException) { throw; }
                catch (System.Exception ex)
                {
                    node["status"] = "error";
                    node["error"] = ex.Message;
                    prog.Log("  could not read: " + ex.Message);
                }
                finally
                {
                    if (!keepCopies && fw.Copy != null) { try { SafeFs.Delete(fw.Copy); } catch { } }
                }
            }

            // ---- prove the originals were not touched ---------------------------------
            prog.Stage("Re-fingerprinting the originals");
            int unchanged = 0, changed = 0;
            List<object> files = new List<object>();
            foreach (FileWork fw in all)
            {
                string before;
                if (fw.Node != null && hashBefore.TryGetValue(fw.Original, out before))
                {
                    string after = "";
                    try { after = Util.Sha256(fw.Original); } catch (System.Exception ex) { after = "unreadable: " + ex.Message; }
                    bool same = after == before;
                    fw.Node["unchangedAfter"] = same;
                    if (same) unchanged++; else changed++;
                }
                files.Add(fw.Node);
            }
            result["files"] = files;
            result["originalsUnchanged"] = unchanged;
            result["originalsChanged"] = changed;
            result["truncatedXrefs"] = truncated;
            result["maxFiles"] = maxFiles;
            if (changed > 0)
                prog.Log("NOTE: " + changed + " original(s) changed on disk while the tree was built (someone saved them). This tool never writes to them.");
            if (!keepCopies) { try { SafeFs.DeleteScratchDirectory(workDir); } catch { } }
            return result;
        }

        static void Enqueue(FileWork fw, List<FileWork> all, Dictionary<string, FileWork> byPath, Queue<FileWork> queue)
        {
            fw.Id = all.Count;
            all.Add(fw);
            byPath[Path.GetFullPath(fw.Original)] = fw;
            queue.Enqueue(fw);
        }

        /// <summary>Byte copy, opening the original read-only and letting others keep
        /// reading, writing and saving it meanwhile. Returns the SHA-256 of the bytes copied.</summary>
        static string CopyAndHash(string original, string copy)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream src = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20))
            using (FileStream dst = SafeFs.CreateScratchFile(copy))
            {
                byte[] buf = new byte[1 << 20];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    sha.TransformBlock(buf, 0, n, null, 0);
                    dst.Write(buf, 0, n);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                System.Text.StringBuilder sb = new System.Text.StringBuilder(64);
                foreach (byte b in sha.Hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>"P:\867 - Name" or "\\server\share\folder": the top folder a file lives under.</summary>
        public static string LocationRoot(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetPathRoot(full);   // "P:\" or "\\server\share"
                string rest = full.Substring(root.Length).TrimStart('\\');
                int i = rest.IndexOf('\\');
                string top = i > 0 ? rest.Substring(0, i) : "";
                return top.Length > 0 ? Path.Combine(root, top) : root;
            }
            catch { return ""; }
        }

        // ---------------------------------------------------------------- one file

        static List<Dictionary<string, object>> ReadFile(FileWork fw, Dictionary<string, object> node, Progress prog)
        {
            using (SideDb side = new SideDb(false, false))
            {
                Database db = side.Db;
                db.ReadDwgFile(fw.Copy, FileOpenMode.OpenForReadAndAllShare, true, "");
                db.CloseInput(true);
                return ReadDb(db, fw, node, prog);
            }
        }

        static List<Dictionary<string, object>> ReadDb(Database db, FileWork fw, Dictionary<string, object> node, Progress prog)
        {
            List<Dictionary<string, object>> xrefList = new List<Dictionary<string, object>>();
            {
                string hostDir = Path.GetDirectoryName(fw.Original);

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    Dictionary<string, object> defs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    Dictionary<ObjectId, string> displayName = new Dictionary<ObjectId, string>();
                    ObjectId msId = bt[BlockTableRecord.ModelSpace];
                    List<object> layouts = new List<object>();
                    int defCount = 0, anonCount = 0;

                    // ---- xrefs attached to THIS file (not nested ones; those are found by reading their own file)
                    foreach (ObjectId id in bt)
                    {
                        BlockTableRecord b = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (!b.IsFromExternalReference || b.IsDependent) continue;
                        Dictionary<string, object> x = new Dictionary<string, object>();
                        x["name"] = b.Name;
                        x["storedPath"] = b.PathName;
                        x["type"] = b.IsFromOverlayReference ? "overlay" : "attach";
                        x["unloaded"] = b.IsUnloaded;
                        int refs = 0;
                        List<object> places = new List<object>();
                        try
                        {
                            foreach (ObjectId rid in b.GetBlockReferenceIds(true, false))
                            {
                                refs++;
                                if (places.Count < 5)
                                {
                                    BlockReference r = tr.GetObject(rid, OpenMode.ForRead) as BlockReference;
                                    if (r != null) places.Add(Placement(r, tr));
                                }
                            }
                        }
                        catch { }
                        x["instances"] = refs;
                        x["placements"] = places;
                        string resolved = ResolveXrefPath(b.PathName, hostDir);
                        x["resolvedPath"] = resolved ?? "";
                        x["status"] = resolved == null ? "not found at saved path" : (b.IsUnloaded ? "unloaded" : (refs == 0 ? "attached, not inserted" : "found"));
                        x["pathType"] = string.IsNullOrEmpty(b.PathName) ? "none" : (Path.IsPathRooted(b.PathName) ? (b.PathName.StartsWith("\\\\") ? "UNC" : "absolute") : "relative");
                        xrefList.Add(x);
                        displayName[id] = b.Name;
                    }

                    // ---- block definitions
                    foreach (ObjectId id in bt)
                    {
                        BlockTableRecord b = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (b.IsFromExternalReference) continue;
                        if (b.IsLayout)
                        {
                            if (id == msId) continue;
                            Dictionary<string, object> lay = new Dictionary<string, object>();
                            string ln = b.Name;
                            try { Layout L = tr.GetObject(b.LayoutId, OpenMode.ForRead) as Layout; if (L != null) ln = L.LayoutName; } catch { }
                            lay["name"] = ln;
                            lay["content"] = Contents(b, tr, displayName, bt);
                            layouts.Add(lay);
                            continue;
                        }
                        defCount++;
                        if (b.IsAnonymous) anonCount++;
                        Dictionary<string, object> d = new Dictionary<string, object>();
                        d["name"] = b.Name;
                        d["anonymous"] = b.IsAnonymous;
                        d["dynamic"] = b.IsDynamicBlock;
                        d["fromBoundXref"] = b.Name.Contains("$0$") || b.Name.Contains("$1$");
                        d["xrefDependent"] = b.IsDependent;
                        d["content"] = Contents(b, tr, displayName, bt);
                        try { d["referencedBy"] = b.GetBlockReferenceIds(true, false).Count; } catch { }
                        try { if (!string.IsNullOrEmpty(b.Comments)) d["description"] = b.Comments; } catch { }
                        defs[b.Name] = d;
                    }

                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(msId, OpenMode.ForRead);
                    node["modelSpace"] = Contents(ms, tr, displayName, bt);
                    node["layouts"] = layouts;
                    node["definitions"] = defs;
                    node["definitionCount"] = defCount;
                    node["anonymousDefinitions"] = anonCount;
                    node["xrefs"] = xrefList;
                    try { node["insunits"] = db.Insunits.ToString(); } catch { }
                    tr.Commit();
                }
            }
            return xrefList;
        }

        /// <summary>Where AutoCAD would look first: the saved path itself, then
        /// relative to the host's folder, then the bare file name in the host's
        /// folder. Deliberately no support-path searching: if it is not where the
        /// drawing says, it is reported as not found rather than guessed.</summary>
        static string ResolveXrefPath(string stored, string hostDir)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            List<string> cands = new List<string>();
            try
            {
                if (Path.IsPathRooted(stored)) cands.Add(stored);
                else cands.Add(Path.GetFullPath(Path.Combine(hostDir, stored)));
                cands.Add(Path.Combine(hostDir, Path.GetFileName(stored)));
            }
            catch { }
            foreach (string c in cands)
            {
                try { if (File.Exists(c)) return Path.GetFullPath(c); } catch { }
            }
            return null;
        }

        static Dictionary<string, object> Placement(BlockReference r, Transaction tr)
        {
            Dictionary<string, object> p = new Dictionary<string, object>();
            Point3d pt = r.Position;
            p["handle"] = r.Handle.ToString();
            p["x"] = Math.Round(pt.X, 4); p["y"] = Math.Round(pt.Y, 4); p["z"] = Math.Round(pt.Z, 4);
            p["rotationDeg"] = Math.Round(r.Rotation * 180.0 / Math.PI, 4);
            Scale3d s = r.ScaleFactors;
            p["scale"] = Util.Fmt(s.X) + "," + Util.Fmt(s.Y) + "," + Util.Fmt(s.Z);
            try { p["layer"] = r.Layer; } catch { }
            try
            {
                BlockTableRecord owner = tr.GetObject(r.OwnerId, OpenMode.ForRead) as BlockTableRecord;
                if (owner != null) p["in"] = owner.IsLayout ? (owner.Name.Equals(BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase) ? "Model" : owner.Name) : owner.Name;
            }
            catch { }
            return p;
        }

        /// <summary>What a block (or model space / a layout) contains: geometry
        /// counted by type, and references grouped by the block they insert.</summary>
        static Dictionary<string, object> Contents(BlockTableRecord b, Transaction tr, Dictionary<ObjectId, string> xrefNames, BlockTable bt)
        {
            Dictionary<string, long> types = new Dictionary<string, long>();
            Dictionary<string, Dictionary<string, object>> refs = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            long total = 0, custom = 0, proxies = 0, attdefs = 0;
            bool inModelSpace = b.IsLayout && b.Name.Equals(BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase);
            foreach (ObjectId eid in b)
            {
                Entity e = tr.GetObject(eid, OpenMode.ForRead) as Entity;
                if (e == null) continue;
                total++;
                BlockReference r = e as BlockReference;
                if (r != null)
                {
                    string key; bool isXref = false; bool dyn = false;
                    ObjectId target = r.BlockTableRecord;
                    if (xrefNames.ContainsKey(target)) { key = xrefNames[target]; isXref = true; }
                    else
                    {
                        key = SafeName(r, tr);
                        try
                        {
                            if (r.IsDynamicBlock)
                            {
                                dyn = true;
                                BlockTableRecord dbtr = tr.GetObject(r.DynamicBlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                                if (dbtr != null) key = dbtr.Name;
                            }
                        }
                        catch { }
                    }
                    Dictionary<string, object> g;
                    if (!refs.TryGetValue(key, out g))
                    {
                        g = new Dictionary<string, object>();
                        g["block"] = key;
                        g["kind"] = isXref ? "xref" : "block";
                        g["count"] = 0L; g["nonUniform"] = 0L; g["mirrored"] = 0L; g["clipped"] = 0L; g["invisible"] = 0L;
                        g["dynamic"] = dyn;
                        g["layers"] = new List<string>();
                        g["placements"] = new List<object>();
                        g["anonymousDefinitions"] = new List<string>();
                        refs[key] = g;
                    }
                    g["count"] = (long)g["count"] + (r is MInsertBlock ? Math.Max(1L, (long)((MInsertBlock)r).Rows * ((MInsertBlock)r).Columns) : 1L);
                    Scale3d s = r.ScaleFactors;
                    double ax = Math.Abs(s.X), ay = Math.Abs(s.Y), az = Math.Abs(s.Z), mx = Math.Max(ax, Math.Max(ay, az));
                    if (mx > 0 && (Math.Abs(ax - ay) > 1e-9 * mx || Math.Abs(ax - az) > 1e-9 * mx)) g["nonUniform"] = (long)g["nonUniform"] + 1;
                    if (s.X * s.Y * s.Z < 0) g["mirrored"] = (long)g["mirrored"] + 1;
                    if (!r.Visible) g["invisible"] = (long)g["invisible"] + 1;
                    if (DefinitionCache.HasClip(r, tr)) g["clipped"] = (long)g["clipped"] + 1;
                    List<string> layers = (List<string>)g["layers"];
                    try { string ly = r.Layer; if (layers.Count < 6 && !layers.Contains(ly)) layers.Add(ly); } catch { }
                    List<object> pl = (List<object>)g["placements"];
                    if (pl.Count < (inModelSpace ? 500 : 5)) pl.Add(Placement(r, tr));
                    if (dyn)
                    {
                        List<string> an = (List<string>)g["anonymousDefinitions"];
                        try { string n = SafeName(r, tr); if (an.Count < 50 && !an.Contains(n)) an.Add(n); } catch { }
                    }
                    continue;
                }
                string tn = TypeLabel(e);
                long v; types.TryGetValue(tn, out v); types[tn] = v + 1;
                if (e is ProxyEntity) proxies++;
                else if (DefinitionCache.IsCustom(e)) custom++;
                if (e is AttributeDefinition) attdefs++;
            }
            Dictionary<string, object> c = new Dictionary<string, object>();
            c["objects"] = total;
            c["geometry"] = SortedTypes(types);
            List<object> rl = new List<object>();
            List<Dictionary<string, object>> sorted = new List<Dictionary<string, object>>(refs.Values);
            sorted.Sort(delegate (Dictionary<string, object> a, Dictionary<string, object> bb) { return ((long)bb["count"]).CompareTo((long)a["count"]); });
            foreach (Dictionary<string, object> g in sorted) rl.Add(g);
            c["references"] = rl;
            c["customObjects"] = custom;
            c["proxies"] = proxies;
            c["attributeDefinitions"] = attdefs;
            return c;
        }

        static string SafeName(BlockReference r, Transaction tr)
        {
            try
            {
                BlockTableRecord t = tr.GetObject(r.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                return t != null ? t.Name : "?";
            }
            catch { return "?"; }
        }

        static string TypeLabel(Entity e)
        {
            ProxyEntity p = e as ProxyEntity;
            if (p != null) { try { return p.OriginalDxfName + " (proxy)"; } catch { return "PROXY"; } }
            string n = EntityTransformer.SafeDxf(e);
            if (DefinitionCache.IsCustom(e)) n += " (custom)";
            return n;
        }

        static Dictionary<string, object> SortedTypes(Dictionary<string, long> d)
        {
            List<KeyValuePair<string, long>> l = new List<KeyValuePair<string, long>>(d);
            l.Sort(delegate (KeyValuePair<string, long> a, KeyValuePair<string, long> b) { return b.Value.CompareTo(a.Value); });
            Dictionary<string, object> o = new Dictionary<string, object>();
            foreach (KeyValuePair<string, long> kv in l) o[kv.Key] = kv.Value;
            return o;
        }
    }
}
