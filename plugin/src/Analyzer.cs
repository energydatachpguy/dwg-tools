// DwgFlatPublisher -- FLATANALYZE: preflight statistics without changing anything.
//
// Every block definition is scanned once; per-definition counts are combined
// through the reference DAG with memoisation, so "1,000 inserts of a block
// holding 10 sub-blocks of 100 entities" is computed as 1000 * (10*100)
// without expanding anything. Cycles are guarded.

using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace DwgFlat
{
    public class Analyzer
    {
        class Child { public ObjectId Btr; public long Multiplier; public bool NonUniform, Mirrored; }

        class Stat
        {
            public string Name = "";
            public Dictionary<string, long> Direct = new Dictionary<string, long>();
            public long DirectLeaf, VisibleAttribs, Proxies, Custom, Dynamic, Clipped, NonDwg, Annotation;
            public List<Child> Children = new List<Child>();
            public bool Visiting, Done, External;
            public long Expanded = -1; public int Depth = -1;
            public Dictionary<string, long> ExpandedTypes;
            public long NonUniformAffected = -1;
        }

        readonly Database _db;
        readonly Report _rep;
        readonly Dictionary<ObjectId, Stat> _stats = new Dictionary<ObjectId, Stat>();
        int _cycles;

        public Analyzer(Database db, Report rep) { _db = db; _rep = rep; }

        public Dictionary<string, object> Run(string sourcePath, XrefStage xrefs)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["source"] = sourcePath;
            try { r["fileSizeBytes"] = new FileInfo(sourcePath).Length; } catch { }

            int defs = 0, anonymous = 0, dynamicDefs = 0, layouts = 0;
            ObjectId msId;
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(_db.BlockTableId, OpenMode.ForRead);
                msId = bt[BlockTableRecord.ModelSpace];
                foreach (ObjectId id in bt)
                {
                    BlockTableRecord b = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (b.IsLayout) { layouts++; continue; }
                    if (b.IsFromExternalReference) continue;
                    defs++;
                    if (b.IsAnonymous) anonymous++;
                    if (b.IsDynamicBlock) dynamicDefs++;
                }
                Scan(msId, tr);
                tr.Commit();
            }
            Stat ms = _stats[msId];
            long expanded = Expanded(msId, 0);
            Dictionary<string, long> types = ExpandedTypes(msId, 0);
            int depth = Depth(msId, 0);

            long msEntities = 0, msRefs = 0;
            foreach (KeyValuePair<string, long> kv in ms.Direct) msEntities += kv.Value;
            foreach (Child c in ms.Children) msRefs += c.Multiplier;
            msEntities += msRefs;

            long totalRefs = 0, nonUniformRefs = 0, mirroredRefs = 0, proxies = 0, custom = 0, dyn = 0, clipped = 0, nonDwg = 0, annotation = 0;
            foreach (Stat s in _stats.Values)
            {
                foreach (Child c in s.Children) { totalRefs += c.Multiplier; if (c.NonUniform) nonUniformRefs++; if (c.Mirrored) mirroredRefs++; }
                proxies += s.Proxies; custom += s.Custom; dyn += s.Dynamic; clipped += s.Clipped; nonDwg += s.NonDwg; annotation += s.Annotation;
            }

            // OPTIMIZED estimate: one leaf per distinct top-level definition
            HashSet<ObjectId> distinctTop = new HashSet<ObjectId>();
            foreach (Child c in ms.Children) distinctTop.Add(c.Btr);
            long leafEntities = 0;
            foreach (ObjectId b in distinctTop) leafEntities += Expanded(b, 0);
            long optEstimate = leafEntities + msEntities;

            r["modelSpaceEntities"] = msEntities;
            r["modelSpaceBlockReferences"] = msRefs;
            r["blockReferencesAllLevels"] = totalRefs;
            r["blockDefinitions"] = defs;
            r["anonymousDefinitions"] = anonymous;
            r["dynamicBlockDefinitions"] = dynamicDefs;
            r["dynamicBlockReferences"] = dyn;
            r["maxNestingDepth"] = depth;
            r["estimatedExpandedEntities_FULL_FLAT"] = expanded;
            r["estimatedEntities_OPTIMIZED"] = optEstimate;
            r["distinctTopLevelDefinitions"] = distinctTop.Count;
            r["expandedEntityTypes"] = Sorted(types);
            r["proxyEntities"] = proxies;
            r["customObjects"] = custom;
            r["nonUniformlyScaledReferences"] = nonUniformRefs;
            r["entitiesUnderNonUniformScale"] = NonUniformAffected(msId, 0);
            r["mirroredReferences"] = mirroredRefs;
            r["xclippedReferences"] = clipped;
            r["nonDwgDependencyEntities"] = nonDwg;
            r["annotationObjectsInsideBlocks"] = annotation;
            r["circularBlockDefinitions"] = _cycles;
            if (xrefs != null) r["xrefs"] = xrefs.ToDictionary();

            // ---- risks -----------------------------------------------------------------
            List<string> risks = new List<string>();
            if (expanded > 5000000) risks.Add(string.Format("HIGH: {0:N0} entities when fully expanded. FULL_FLAT will be large and slow to open; compare with OPTIMIZED (~{1:N0}).", expanded, optEstimate));
            else if (expanded > 1000000) risks.Add(string.Format("MEDIUM: {0:N0} entities when fully expanded (FULL_FLAT). OPTIMIZED estimate ~{1:N0}.", expanded, optEstimate));
            if (expanded > 0 && optEstimate > 0 && expanded > 20 * optEstimate)
                risks.Add("Heavy reuse: the same definitions are inserted many times. OPTIMIZED keeps them shared and will be far smaller.");
            if (nonUniformRefs > 0) risks.Add(nonUniformRefs + " reference(s) with non-uniform X/Y/Z scale: circles/arcs become exact splines, text is re-solved exactly, polyline widths are lost.");
            if (proxies > 0) risks.Add(proxies + " proxy entities (object enabler not loaded). They are exploded to their proxy graphics if possible; run in Plant 3D so the real objects load.");
            if (custom > proxies) risks.Add((custom - proxies) + " custom objects (e.g. Plant 3D). They will be exploded to standard geometry.");
            if (clipped > 0) risks.Add(clipped + " XCLIPped reference(s): clipping is NOT applied by flattening (full block will show).");
            if (nonDwg > 0) risks.Add(nonDwg + " image / PDF / point-cloud references: the output will not be self-contained unless excluded.");
            if (xrefs != null && xrefs.Missing > 0) risks.Add(xrefs.Missing + " missing xref(s): publication will FAIL unless allowMissingXrefs.");
            if (xrefs != null && xrefs.Unloaded > 0) risks.Add(xrefs.Unloaded + " unloaded xref(s): excluded by default (not displayed in the source).");
            if (depth > 20) risks.Add("Very deep nesting (" + depth + " levels).");
            if (_cycles > 0) risks.Add(_cycles + " circular block definition(s).");
            r["risks"] = risks;
            return r;
        }

        static Dictionary<string, object> Sorted(Dictionary<string, long> d)
        {
            List<KeyValuePair<string, long>> l = new List<KeyValuePair<string, long>>(d);
            l.Sort(delegate (KeyValuePair<string, long> a, KeyValuePair<string, long> b) { return b.Value.CompareTo(a.Value); });
            Dictionary<string, object> o = new Dictionary<string, object>();
            foreach (KeyValuePair<string, long> kv in l) o[kv.Key] = kv.Value;
            return o;
        }

        void Scan(ObjectId btrId, Transaction tr)
        {
            Stack<ObjectId> todo = new Stack<ObjectId>();
            todo.Push(btrId);
            while (todo.Count > 0)
            {
                ObjectId id = todo.Pop();
                if (_stats.ContainsKey(id)) continue;
                Stat s = new Stat();
                _stats[id] = s;
                if (id.IsNull || id.IsErased) continue;
                BlockTableRecord b = tr.GetObject(id, OpenMode.ForRead) as BlockTableRecord;
                if (b == null) continue;
                s.Name = b.Name;
                if (b.IsFromExternalReference && !b.IsResolved) { s.External = true; continue; }
                foreach (ObjectId eid in b)
                {
                    Entity e = tr.GetObject(eid, OpenMode.ForRead) as Entity;
                    if (e == null || !e.Visible) continue;
                    BlockReference br = e as BlockReference;
                    if (br != null)
                    {
                        Child c = new Child();
                        c.Btr = br.BlockTableRecord;
                        MInsertBlock mi = br as MInsertBlock;
                        c.Multiplier = mi != null ? Math.Max(1, (long)mi.Rows * mi.Columns) : 1;
                        Scale3d sc = br.ScaleFactors;
                        double ax = Math.Abs(sc.X), ay = Math.Abs(sc.Y), az = Math.Abs(sc.Z);
                        double mx = Math.Max(ax, Math.Max(ay, az));
                        c.NonUniform = mx > 0 && (Math.Abs(ax - ay) > 1e-9 * mx || Math.Abs(ax - az) > 1e-9 * mx);
                        c.Mirrored = sc.X * sc.Y * sc.Z < 0;
                        s.Children.Add(c);
                        if (br.IsDynamicBlock) s.Dynamic++;
                        if (DefinitionCache.HasClip(br, tr)) s.Clipped++;
                        foreach (ObjectId aid in br.AttributeCollection)
                        {
                            AttributeReference ar = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                            if (ar != null && !ar.Invisible) s.VisibleAttribs++;
                        }
                        if (!_stats.ContainsKey(c.Btr)) todo.Push(c.Btr);
                        continue;
                    }
                    string dxf = EntityTransformer.SafeDxf(e);
                    long v; s.Direct.TryGetValue(dxf, out v); s.Direct[dxf] = v + 1;
                    s.DirectLeaf++;
                    if (e is ProxyEntity) s.Proxies++;
                    if (DefinitionCache.IsCustom(e)) s.Custom++;
                    if (DefinitionCache.IsNonDwgDependency(e)) s.NonDwg++;
                    if (DefinitionCache.IsAnnotationToExplode(e) && id != _db.CurrentSpaceId) s.Annotation++;
                }
            }
        }

        long Expanded(ObjectId id, int depth)
        {
            Stat s;
            if (!_stats.TryGetValue(id, out s)) return 0;
            if (s.Expanded >= 0) return s.Expanded;
            if (s.Visiting || depth > 200) { _cycles++; return 0; }
            s.Visiting = true;
            long n = s.DirectLeaf + s.VisibleAttribs;
            foreach (Child c in s.Children) n += c.Multiplier * Expanded(c.Btr, depth + 1);
            s.Visiting = false;
            s.Expanded = n;
            return n;
        }

        Dictionary<string, long> ExpandedTypes(ObjectId id, int depth)
        {
            Stat s;
            if (!_stats.TryGetValue(id, out s)) return new Dictionary<string, long>();
            if (s.ExpandedTypes != null) return s.ExpandedTypes;
            Dictionary<string, long> d = new Dictionary<string, long>(s.Direct);
            if (s.VisibleAttribs > 0) Add(d, "ATTRIB (as text)", s.VisibleAttribs);
            if (s.Visiting || depth > 200) return d;
            s.Visiting = true;
            foreach (Child c in s.Children)
                foreach (KeyValuePair<string, long> kv in ExpandedTypes(c.Btr, depth + 1))
                    Add(d, kv.Key, kv.Value * c.Multiplier);
            s.Visiting = false;
            s.ExpandedTypes = d;
            return d;
        }

        static void Add(Dictionary<string, long> d, string k, long n) { long v; d.TryGetValue(k, out v); d[k] = v + n; }

        int Depth(ObjectId id, int depth)
        {
            Stat s;
            if (!_stats.TryGetValue(id, out s)) return 0;
            if (s.Depth >= 0) return s.Depth;
            if (s.Visiting || depth > 200) return 0;
            s.Visiting = true;
            int m = 0;
            foreach (Child c in s.Children) m = Math.Max(m, 1 + Depth(c.Btr, depth + 1));
            s.Visiting = false;
            s.Depth = m;
            return m;
        }

        long NonUniformAffected(ObjectId id, int depth)
        {
            Stat s;
            if (!_stats.TryGetValue(id, out s)) return 0;
            if (s.NonUniformAffected >= 0) return s.NonUniformAffected;
            if (s.Visiting || depth > 200) return 0;
            s.Visiting = true;
            long n = 0;
            foreach (Child c in s.Children)
                n += c.Multiplier * (c.NonUniform ? Expanded(c.Btr, depth + 1) : NonUniformAffected(c.Btr, depth + 1));
            s.Visiting = false;
            s.NonUniformAffected = n;
            return n;
        }

        /// <summary>Readable command-line summary.</summary>
        public static List<string> Summary(Dictionary<string, object> r)
        {
            List<string> l = new List<string>();
            l.Add("Source: " + r["source"]);
            object sz; if (r.TryGetValue("fileSizeBytes", out sz)) l.Add(string.Format("File size: {0:N1} MB", Convert.ToDouble(sz) / 1048576.0));
            l.Add(string.Format("Model space: {0:N0} objects, {1:N0} block references", r["modelSpaceEntities"], r["modelSpaceBlockReferences"]));
            l.Add(string.Format("Block definitions: {0:N0} ({1:N0} anonymous, {2:N0} dynamic); references at all levels: {3:N0}; max nesting depth: {4}",
                r["blockDefinitions"], r["anonymousDefinitions"], r["dynamicBlockDefinitions"], r["blockReferencesAllLevels"], r["maxNestingDepth"]));
            object x;
            if (r.TryGetValue("xrefs", out x))
            {
                Dictionary<string, object> xd = (Dictionary<string, object>)x;
                l.Add(string.Format("Xrefs: {0} direct, {1} nested, {2} overlay, {3} missing, {4} unloaded, {5} circular",
                    xd["direct"], xd["nested"], xd["overlays"], xd["missing"], xd["unloaded"], xd["circular"]));
            }
            l.Add(string.Format("Estimated FULL_FLAT entities: {0:N0}    OPTIMIZED: ~{1:N0}", r["estimatedExpandedEntities_FULL_FLAT"], r["estimatedEntities_OPTIMIZED"]));
            l.Add(string.Format("Dynamic block refs: {0:N0}; non-uniform scaled refs: {1:N0} (affecting {2:N0} entities); mirrored refs: {3:N0}",
                r["dynamicBlockReferences"], r["nonUniformlyScaledReferences"], r["entitiesUnderNonUniformScale"], r["mirroredReferences"]));
            l.Add(string.Format("Proxy entities: {0:N0}; custom objects: {1:N0}; xclipped refs: {2:N0}; image/underlay refs: {3:N0}",
                r["proxyEntities"], r["customObjects"], r["xclippedReferences"], r["nonDwgDependencyEntities"]));
            Dictionary<string, object> types = (Dictionary<string, object>)r["expandedEntityTypes"];
            List<string> top = new List<string>();
            foreach (KeyValuePair<string, object> kv in types) { if (top.Count >= 10) break; top.Add(kv.Key + " " + string.Format("{0:N0}", kv.Value)); }
            l.Add("Expanded entity types: " + string.Join(", ", top.ToArray()));
            List<string> risks = (List<string>)r["risks"];
            if (risks.Count == 0) l.Add("Risks: none found.");
            else foreach (string s in risks) l.Add("RISK: " + s);
            return l;
        }
    }
}
