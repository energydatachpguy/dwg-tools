// DwgFlatPublisher -- external reference resolution and binding (in the
// side database only; the source file on disk is never written).
//
// AutoCAD's own machinery decides what an xref graph means: ResolveXrefs
// loads attached xrefs, follows nested attachments, does NOT load nested
// overlays (that is what "overlay" means), and loads a file shared by
// several parents once. BindXrefs then turns each resolved xref into an
// ordinary block. This stage inspects the resulting graph, applies the
// publication policy to missing / unloaded / circular cases, binds, and
// proves no DWG reference is left.

using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;

namespace DwgFlat
{
    public class XrefStage
    {
        readonly Database _db;
        readonly PublishOptions _opt;
        readonly Report _rep;
        readonly Progress _prog;
        public readonly List<Dictionary<string, object>> Nodes = new List<Dictionary<string, object>>();
        public readonly List<string> ResolvedFiles = new List<string>();
        public int Direct, Nested, Missing, Unloaded, Bound, Overlays, Cycles;

        public XrefStage(Database db, PublishOptions opt, Report rep, Progress prog)
        {
            _db = db; _opt = opt; _rep = rep; _prog = prog;
        }

        /// <summary>Resolve, inspect, apply policy. bind=false leaves them
        /// resolved (analysis); bind=true binds (publication). Throws
        /// PublishAbortException when a required dependency is missing and
        /// AllowMissingXrefs is off.</summary>
        public void Run(bool bind)
        {
            try { _db.ResolveXrefs(false, false); }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                _rep.Issue(Severity.Warning, "XREF_RESOLVE", "ResolveXrefs reported " + ex.ErrorStatus + "; individual xref states below.");
            }

            XrefGraph g = _db.GetHostDwgXrefGraph(false);   // no ghost (erased) nodes
            List<ObjectId> toBind = new List<ObjectId>();
            List<ObjectId> toDetach = new List<ObjectId>();
            List<ObjectId> toReload = new List<ObjectId>();
            List<string> missingNames = new List<string>();

            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                for (int i = 0; i < g.NumNodes; i++)
                {
                    XrefGraphNode n = g.GetXrefNode(i);
                    if (i == 0) continue;   // node 0 is always the host drawing
                    Dictionary<string, object> d = new Dictionary<string, object>();
                    d["name"] = n.Name;
                    d["status"] = n.XrefStatus.ToString();
                    d["nested"] = n.IsNested;
                    if (n.IsNested) Nested++; else Direct++;

                    bool overlay = false; string savedPath = ""; bool referenced = false;
                    if (!n.BlockTableRecordId.IsNull && !n.BlockTableRecordId.IsErased)
                    {
                        BlockTableRecord btr = tr.GetObject(n.BlockTableRecordId, OpenMode.ForRead) as BlockTableRecord;
                        if (btr != null)
                        {
                            overlay = btr.IsFromOverlayReference;
                            savedPath = btr.PathName;
                            try { referenced = btr.GetBlockReferenceIds(true, false).Count > 0 || n.IsNested; } catch { }
                        }
                    }
                    if (overlay) Overlays++;
                    d["overlay"] = overlay;
                    d["savedPath"] = savedPath;
                    d["pathType"] = string.IsNullOrEmpty(savedPath) ? "" :
                        (Path.IsPathRooted(savedPath) ? (savedPath.StartsWith("\\\\") ? "UNC" : "absolute") : "relative");
                    string found = "";
                    try { if (n.Database != null) found = n.Database.Filename; } catch { }
                    d["resolvedFile"] = found;
                    if (!string.IsNullOrEmpty(found)) ResolvedFiles.Add(found);

                    switch (n.XrefStatus)
                    {
                        case XrefStatus.Resolved:
                            // binding a parent binds its nested xrefs with it; binding a
                            // nested one separately afterwards would fail
                            if (!n.IsNested) toBind.Add(n.BlockTableRecordId);
                            break;
                        case XrefStatus.Unloaded:
                            Unloaded++;
                            if (_opt.IncludeUnloadedXrefs)
                            {
                                toReload.Add(n.BlockTableRecordId);
                                _rep.Issue(Severity.Info, "XREF_UNLOADED_INCLUDED", "Unloaded xref '" + n.Name + "' will be reloaded and included (includeUnloadedXrefs).");
                            }
                            else
                            {
                                if (!n.IsNested) toDetach.Add(n.BlockTableRecordId);
                                _rep.Issue(Severity.Warning, "XREF_UNLOADED_EXCLUDED",
                                    "Xref '" + n.Name + "' is UNLOADED in the source, so it is not displayed there; it is excluded from the published drawing (set includeUnloadedXrefs to include it).");
                            }
                            break;
                        case XrefStatus.Unreferenced:
                            _rep.Issue(Severity.Info, "XREF_UNREFERENCED", "Xref '" + n.Name + "' is attached but not inserted anywhere; nothing to include.");
                            if (!n.IsNested) toDetach.Add(n.BlockTableRecordId);
                            break;
                        case XrefStatus.FileNotFound:
                        case XrefStatus.Unresolved:
                            if (!referenced) { if (!n.IsNested) toDetach.Add(n.BlockTableRecordId); break; }
                            Missing++;
                            missingNames.Add(n.Name + " (" + savedPath + ")");
                            // a nested one can't be detached directly; it is cleaned up after its parent is bound
                            if (!n.IsNested) toDetach.Add(n.BlockTableRecordId);
                            _rep.Issue(_opt.AllowMissingXrefs ? Severity.Warning : Severity.Error, "XREF_MISSING",
                                "Xref '" + n.Name + "' could not be found: " + savedPath + " [" + n.XrefStatus + "]");
                            break;
                        default:
                            break;
                    }
                    Nodes.Add(d);
                }
                tr.Commit();
            }

            // circular attachments (A -> B -> A): AutoCAD stops loading at the repeat
            Cycles = CountCycles(g);
            if (Cycles > 0)
                _rep.Issue(Severity.Warning, "XREF_CIRCULAR",
                    Cycles + " circular xref reference(s) found. AutoCAD loads each file once and stops at the repeat; the published drawing contains what AutoCAD displays.");

            if (Missing > 0 && !_opt.AllowMissingXrefs)
                throw new PublishAbortException("Required xref(s) missing: " + string.Join("; ", missingNames.ToArray())
                    + ". Nothing was published. Fix the paths (or set allowMissingXrefs to publish without them).");

            if (!bind) return;

            if (toReload.Count > 0)
            {
                try
                {
                    _db.ReloadXrefs(new ObjectIdCollection(toReload.ToArray()));
                    toBind.AddRange(toReload);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    _rep.Issue(Severity.Error, "XREF_RELOAD_FAILED", "Could not reload unloaded xrefs: " + ex.ErrorStatus);
                }
            }

            foreach (ObjectId id in toDetach)
            {
                try { if (!id.IsErased) _db.DetachXref(id); }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    _rep.Issue(Severity.Warning, "XREF_DETACH", "Could not detach excluded xref: " + ex.ErrorStatus);
                }
            }

            if (!_opt.BindKeepsXrefNames) CheckInsertBindConflicts();

            if (toBind.Count > 0)
            {
                _prog.Log("Binding " + toBind.Count + " xref(s) (" + (_opt.BindKeepsXrefNames ? "BIND: xref layers kept as NAME$0$LAYER" : "INSERT") + ")");
                List<ObjectId> live = new List<ObjectId>();
                foreach (ObjectId id in toBind) if (!id.IsErased) live.Add(id);
                try
                {
                    _db.BindXrefs(new ObjectIdCollection(live.ToArray()), !_opt.BindKeepsXrefNames);
                    Bound = live.Count;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    _rep.Issue(Severity.Warning, "XREF_BIND_BATCH", "Binding all xrefs at once reported " + ex.ErrorStatus + "; retrying one by one.");
                    foreach (ObjectId id in live)
                    {
                        if (id.IsErased || !StillExternal(id)) continue;   // already bound along with a parent
                        try { _db.BindXrefs(new ObjectIdCollection(new ObjectId[] { id }), !_opt.BindKeepsXrefNames); Bound++; }
                        catch (Autodesk.AutoCAD.Runtime.Exception ex2)
                        {
                            _rep.Issue(Severity.Error, "XREF_BIND_FAILED", "Could not bind xref: " + ex2.ErrorStatus);
                        }
                    }
                }
            }

            // Whatever is still external after binding is something the source does
            // not display (a nested overlay, an xref attached but never inserted, an
            // unloaded one) -- or a referenced xref that is missing. The first group
            // is always cleaned up; missing-but-referenced only with allowMissingXrefs.
            {
                List<ObjectId> leftovers = new List<ObjectId>();
                using (Transaction tr = _db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(_db.BlockTableId, OpenMode.ForRead);
                    foreach (ObjectId id in bt)
                    {
                        BlockTableRecord b = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (!b.IsFromExternalReference) continue;
                        int refs = 0;
                        try { refs = b.GetBlockReferenceIds(true, false).Count; } catch { }
                        bool notDisplayed = refs == 0 || b.IsFromOverlayReference || (b.IsUnloaded && !_opt.IncludeUnloadedXrefs);
                        bool missingButReferenced = !b.IsResolved && !b.IsUnloaded && refs > 0;
                        if (notDisplayed || (missingButReferenced && _opt.AllowMissingXrefs))
                        {
                            leftovers.Add(id);
                            _rep.Issue(notDisplayed ? Severity.Info : Severity.Warning, "XREF_LEFTOVER_REMOVED",
                                "Xref '" + b.Name + "' was " + (notDisplayed ? "not displayed in the source (overlay / unreferenced / unloaded)" : "missing")
                                + " and was removed from the published drawing.");
                        }
                    }
                    tr.Commit();
                }
                foreach (ObjectId id in leftovers)
                {
                    try { if (!id.IsErased) _db.DetachXref(id); }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex)
                    {
                        _rep.Issue(Severity.Warning, "XREF_DETACH", "Could not detach leftover xref: " + ex.ErrorStatus);
                    }
                }
            }

            // Prove nothing external is left in the block table.
            int left = 0;
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(_db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId id in bt)
                {
                    BlockTableRecord b = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (b.IsFromExternalReference)
                    {
                        left++;
                        _rep.Issue(Severity.Error, "XREF_REMAINS", "Xref block '" + b.Name + "' is still external after binding (status " + b.XrefStatus + ").");
                    }
                }
                tr.Commit();
            }
            if (left == 0 && (Direct + Nested) > 0) _prog.Log("All DWG references are bound; none remain external.");
        }

        bool StillExternal(ObjectId id)
        {
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                BlockTableRecord b = tr.GetObject(id, OpenMode.ForRead) as BlockTableRecord;
                bool ext = b != null && b.IsFromExternalReference;
                tr.Commit();
                return ext;
            }
        }

        void CheckInsertBindConflicts()
        {
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr.GetObject(_db.LayerTableId, OpenMode.ForRead);
                Dictionary<string, LayerTableRecord> host = new Dictionary<string, LayerTableRecord>(StringComparer.OrdinalIgnoreCase);
                List<LayerTableRecord> dep = new List<LayerTableRecord>();
                foreach (ObjectId id in lt)
                {
                    LayerTableRecord l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    int bar = l.Name.IndexOf('|');
                    if (bar > 0) dep.Add(l); else host[l.Name] = l;
                }
                foreach (LayerTableRecord l in dep)
                {
                    string bare = l.Name.Substring(l.Name.IndexOf('|') + 1);
                    LayerTableRecord h;
                    if (host.TryGetValue(bare, out h))
                    {
                        if (h.Color.ColorValue != l.Color.ColorValue || h.LinetypeObjectId != l.LinetypeObjectId || h.LineWeight != l.LineWeight)
                            _rep.Issue(Severity.Warning, "SYMBOL_CONFLICT",
                                "INSERT bind merges xref layer '" + l.Name + "' into host layer '" + bare + "' which has different color/linetype/lineweight: ByLayer geometry from the xref will change appearance. Use bindType BIND to avoid this.");
                    }
                }
                tr.Commit();
            }
        }

        static string Key(GraphNode n)
        {
            // Managed wrappers are not unique per node, so key by identity data.
            XrefGraphNode x = n as XrefGraphNode;
            if (x == null) return "?";
            return x.BlockTableRecordId.IsNull ? "host:" + x.Name : x.BlockTableRecordId.Handle.ToString();
        }

        static int CountCycles(XrefGraph g)
        {
            // colour-marking DFS over the xref graph (1 = on stack, 2 = done)
            int cycles = 0;
            Dictionary<string, int> state = new Dictionary<string, int>();
            Stack<KeyValuePair<GraphNode, int>> st = new Stack<KeyValuePair<GraphNode, int>>();
            int guard = 0;
            for (int i = 0; i < g.NumNodes; i++)
            {
                GraphNode start = g.GetXrefNode(i);
                if (state.ContainsKey(Key(start))) continue;
                st.Push(new KeyValuePair<GraphNode, int>(start, 0));
                state[Key(start)] = 1;
                while (st.Count > 0 && guard++ < 1000000)
                {
                    KeyValuePair<GraphNode, int> top = st.Pop();
                    GraphNode n = top.Key; int k = top.Value;
                    if (k < n.NumOut)
                    {
                        st.Push(new KeyValuePair<GraphNode, int>(n, k + 1));
                        GraphNode c = n.Out(k);
                        int s;
                        if (!state.TryGetValue(Key(c), out s)) { state[Key(c)] = 1; st.Push(new KeyValuePair<GraphNode, int>(c, 0)); }
                        else if (s == 1) cycles++;
                    }
                    else state[Key(n)] = 2;
                }
            }
            return cycles;
        }

        public Dictionary<string, object> ToDictionary()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["direct"] = Direct; d["nested"] = Nested; d["overlays"] = Overlays;
            d["missing"] = Missing; d["unloaded"] = Unloaded; d["circular"] = Cycles; d["bound"] = Bound;
            d["nodes"] = Nodes;
            return d;
        }
    }
}
