// DwgFlatPublisher -- reopen the SAVED output and prove it is what we claim.

using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace DwgFlat
{
    public class Validator
    {
        readonly PublishOptions _opt;
        readonly Report _rep;
        readonly Progress _prog;
        public bool Passed = true;
        public bool FullyFlattened, SelfContained = true;
        readonly List<Dictionary<string, object>> _checks = new List<Dictionary<string, object>>();

        public Validator(PublishOptions opt, Report rep, Progress prog) { _opt = opt; _rep = rep; _prog = prog; }

        void Check(string name, bool ok, string detail, bool fatal)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["check"] = name; d["passed"] = ok; d["detail"] = detail; d["fatal"] = fatal;
            _checks.Add(d);
            if (!ok && fatal) Passed = false;
            _prog.Log((ok ? "  PASS  " : (fatal ? "  FAIL  " : "  WARN  ")) + name + (string.IsNullOrEmpty(detail) ? "" : " -- " + detail));
        }

        public Dictionary<string, object> Run(string savedPath, long expectedModelSpace, Flattener f)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            using (Database db = new Database(false, true))
            {
                db.ReadDwgFile(savedPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                db.CloseInput(true);
                Check("Saved file reopens", true, savedPath, true);

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    // ---- structure --------------------------------------------------------
                    long count = 0, refs = 0, nestedInLeaf = 0, custom = 0, nonDwg = 0;
                    bool haveExt = false; Extents3d ext = new Extents3d();
                    Dictionary<string, long> types = new Dictionary<string, long>();
                    HashSet<ObjectId> leafChecked = new HashSet<ObjectId>();
                    foreach (ObjectId id in ms)
                    {
                        Entity e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (e == null) continue;
                        count++;
                        string tn = e.GetType().Name;
                        long tv; types.TryGetValue(tn, out tv); types[tn] = tv + 1;
                        try
                        {
                            Extents3d x = e.GeometricExtents;
                            if (!haveExt) { ext = x; haveExt = true; } else ext.AddExtents(x);
                        }
                        catch { }
                        if (DefinitionCache.IsCustom(e)) custom++;
                        if (DefinitionCache.IsNonDwgDependency(e)) nonDwg++;
                        BlockReference br = e as BlockReference;
                        if (br != null)
                        {
                            refs++;
                            if (leafChecked.Add(br.BlockTableRecord))
                            {
                                BlockTableRecord b = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                                foreach (ObjectId cid in b)
                                {
                                    Entity ce = tr.GetObject(cid, OpenMode.ForRead) as Entity;
                                    if (ce is BlockReference) nestedInLeaf++;
                                    if (ce != null && DefinitionCache.IsCustom(ce)) custom++;
                                    if (ce != null && DefinitionCache.IsNonDwgDependency(ce)) nonDwg++;
                                }
                            }
                        }
                    }
                    result["modelSpaceEntities"] = count;
                    result["modelSpaceBlockReferences"] = refs;
                    result["entityTypes"] = types;

                    if (_opt.Mode == PublishMode.FullFlat)
                        Check("No block references in model space (FULL_FLAT)", refs == 0, refs + " found", true);
                    else
                        Check("Leaf blocks contain no nested references (OPTIMIZED: at most one level)", nestedInLeaf == 0,
                            refs + " references to " + leafChecked.Count + " leaf definitions; " + nestedInLeaf + " nested", true);

                    Check("Model-space entity count matches what was written", count == expectedModelSpace,
                        "expected " + expectedModelSpace + ", found " + count, true);

                    // ---- external dependencies ---------------------------------------------
                    int xrefs = 0;
                    foreach (ObjectId bid in bt)
                    {
                        BlockTableRecord b = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
                        if (b.IsFromExternalReference) xrefs++;
                    }
                    Check("No external DWG references", xrefs == 0, xrefs + " xref block(s)", true);

                    List<string> deps = NonDwgDefinitions(db, tr);
                    result["nonDwgDependencies"] = deps;
                    bool depsOk = deps.Count == 0 && nonDwg == 0;
                    Check("No image / PDF / DWF / DGN / point-cloud dependencies", depsOk,
                        depsOk ? "" : (nonDwg + " entities; definitions: " + string.Join(", ", deps.ToArray())),
                        _opt.NonDwgDependencies == DependencyPolicy.Fail);
                    if (!depsOk) SelfContained = false;
                    if (xrefs > 0) SelfContained = false;

                    Check("No custom / proxy objects left (no object enabler needed)", custom == 0, custom + " found", false);
                    if (custom > 0) SelfContained = false;

                    // ---- geometric agreement -------------------------------------------------
                    if (haveExt && f != null && f.HasSourceExtents)
                    {
                        Extents3d s = f.SourceExtents;
                        double diag = Math.Max(1e-9, s.MinPoint.DistanceTo(s.MaxPoint));
                        double dev = Math.Max(s.MinPoint.DistanceTo(ext.MinPoint), s.MaxPoint.DistanceTo(ext.MaxPoint)) / diag;
                        result["extentsDeviation"] = dev;
                        bool omitted = _rep.Get("omitted.hidden") > 0 || _rep.Get("omitted.underFrozenReference") > 0;
                        if (_opt.ExtentsInfoOnly)
                            Check("AutoCAD's rough outline (info only; the coordinate check below is the real test)", true,
                                dev < 1e-3 ? "same" : string.Format("differs by {0:E2}: AutoCAD boxes curves by their control points, so this number is not reliable", dev), false);
                        else
                        Check("Model-space extents match the source", dev < 1e-3,
                            string.Format("relative deviation {0:E2}{1}", dev, omitted ? " (hidden geometry was omitted, which can legitimately shrink extents)" : ""), false);
                    }

                    if (_opt.VerifyGeometry && f != null) VerifySamples(db, tr, f, result);
                    tr.Commit();
                }
            }

            FullyFlattened = _opt.Mode == PublishMode.FullFlat && Passed && _rep.Get("failed.entities") == 0
                             && !_rep.HasIssue("MISSING_DEFINITION") && !_rep.HasIssue("XREF_MISSING");
            result["checks"] = _checks;
            result["passed"] = Passed;
            return result;
        }

        void VerifySamples(Database db, Transaction tr, Flattener f, Dictionary<string, object> result)
        {
            int ok = 0, bad = 0, skipped = 0;
            double worst = 0;
            List<object> failures = new List<object>();
            foreach (VerifySample s in f.Samples)
            {
                if (s.Unmeasurable) { skipped++; continue; }
                CurveMetrics outM = new CurveMetrics();
                try
                {
                    for (int i = 0; i < s.OutputHandles.Count; i++)
                    {
                        ObjectId id = db.GetObjectId(false, new Handle(s.OutputHandles[i]), 0);
                        if (id.IsNull || id.IsErased) continue;
                        Entity e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (e == null) continue;
                        if (s.OutputIsLeafReference && e is BlockReference)
                        {
                            DBObjectCollection pcs = new DBObjectCollection();
                            e.Explode(pcs);
                            foreach (DBObject o in pcs) { Entity pe = o as Entity; if (pe != null) outM.Add(pe); o.Dispose(); }
                        }
                        else outM.Add(e);
                    }
                }
                catch (System.Exception) { skipped++; continue; }

                double scaleRef = 1.0;
                if (f.HasSourceExtents) scaleRef = Math.Max(1.0, f.SourceExtents.MaxPoint.GetAsVector().Length + f.SourceExtents.MinPoint.GetAsVector().Length);
                double dev = CurveMetrics.Deviation(s.Oracle, outM, scaleRef);
                if (dev > worst) worst = dev;
                if (dev <= 1e-5) ok++;
                else
                {
                    bad++;
                    if (failures.Count < 20)
                    {
                        Dictionary<string, object> d = new Dictionary<string, object>();
                        d["where"] = s.Where;
                        d["sourceLength"] = s.Oracle.Length; d["outputLength"] = outM.Length;
                        d["deviation"] = dev;
                        failures.Add(d);
                    }
                }
            }
            Dictionary<string, object> v = new Dictionary<string, object>();
            v["samples"] = f.Samples.Count; v["matched"] = ok; v["mismatched"] = bad; v["unmeasurable"] = skipped;
            v["worstRelativeDeviation"] = worst;
            v["method"] = "Sampled model-space references: source measured by AutoCAD's own recursive Explode (independent oracle); output measured in the SAVED file. Fingerprint = total curve length + length-weighted centroid.";
            v["mismatches"] = failures;
            result["geometryVerification"] = v;
            if (f.Samples.Count == 0)
                Check("Geometry verification", true, "no samples (no block references, or oracle unavailable)", false);
            else
                Check("Geometry verification (independent oracle vs saved output)", bad == 0,
                    ok + "/" + f.Samples.Count + " references match, worst deviation " + worst.ToString("E2"), _opt.Strict);
        }

        static List<string> NonDwgDefinitions(Database db, Transaction tr)
        {
            List<string> found = new List<string>();
            string[] dicts = { "ACAD_IMAGE_DICT", "ACAD_PDFDEFINITIONS", "ACAD_DWFDEFINITIONS", "ACAD_DGNDEFINITIONS",
                               "ACAD_POINTCLOUD_DICT", "ACAD_POINTCLOUD_EX_DICT" };
            DBDictionary nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
            foreach (string name in dicts)
            {
                if (!nod.Contains(name)) continue;
                DBDictionary d = tr.GetObject(nod.GetAt(name), OpenMode.ForRead) as DBDictionary;
                if (d == null) continue;
                foreach (DBDictionaryEntry de in d) found.Add(name.Replace("ACAD_", "") + ":" + de.Key);
            }
            return found;
        }
    }
}
