// DwgFlatPublisher -- expanding the block hierarchy into the side database.
//
// FULL_FLAT : every model-space block reference is replaced by its fully
//             expanded geometry; nothing in model space is a BlockReference.
// OPTIMIZED : every model-space block reference is replaced by ONE reference
//             to a generated flat "leaf" block (no nested references inside),
//             shared by all instances that display identically.
//
// Writes go through a rolling transaction committed every BatchSize objects,
// so memory stays flat no matter how many millions of entities are created.
// Everything runs on AutoCAD's main thread; nothing touches the database from
// another thread.

using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace DwgFlat
{
    interface ISink { void Emit(Entity e); }

    /// <summary>Batched writer: appends new entities, commits every N.</summary>
    class Writer : IDisposable
    {
        readonly Database _db;
        readonly int _batch;
        readonly Action _onFlush;
        public Transaction Tr;
        readonly Dictionary<ObjectId, BlockTableRecord> _owners = new Dictionary<ObjectId, BlockTableRecord>();
        int _pending;
        public long Total;

        public Writer(Database db, int batch, Action onFlush)
        {
            _db = db; _batch = batch; _onFlush = onFlush;
            Tr = db.TransactionManager.StartTransaction();
        }

        public ObjectId Append(Entity e, ObjectId ownerId)
        {
            BlockTableRecord owner;
            if (!_owners.TryGetValue(ownerId, out owner))
            {
                owner = (BlockTableRecord)Tr.GetObject(ownerId, OpenMode.ForWrite);
                _owners[ownerId] = owner;
            }
            ObjectId id = owner.AppendEntity(e);
            Tr.AddNewlyCreatedDBObject(e, true);
            Total++;
            if (++_pending >= _batch) Flush();
            return id;
        }

        /// <summary>Called between items (never in the middle of reading an object).</summary>
        public void Flush()
        {
            Tr.Commit();
            Tr.Dispose();
            _owners.Clear();
            _pending = 0;
            Tr = _db.TransactionManager.StartTransaction();
            if (_onFlush != null) _onFlush();
        }

        /// <summary>Unwinding from an error: throw the open batch away without
        /// committing (a second exception from Commit would hide the first).</summary>
        public void Abort()
        {
            if (Tr != null)
            {
                try { Tr.Abort(); } catch { }
                try { Tr.Dispose(); } catch { }
                Tr = null;
            }
        }

        public void Dispose()
        {
            if (Tr != null)
            {
                Tr.Commit();
                Tr.Dispose();
                Tr = null;
            }
        }
    }

    class ModelSpaceSink : ISink
    {
        readonly Writer _w; readonly ObjectId _ms;
        public List<ObjectId> Slot;   // ids emitted for the current top-level item
        public ModelSpaceSink(Writer w, ObjectId ms) { _w = w; _ms = ms; }
        public void Emit(Entity e)
        {
            ObjectId id = _w.Append(e, _ms);
            if (Slot != null) Slot.Add(id);
        }
    }

    class ListSink : ISink
    {
        public readonly List<Entity> Items = new List<Entity>();
        public void Emit(Entity e) { Items.Add(e); }
        public void DisposeAll() { foreach (Entity e in Items) { try { e.Dispose(); } catch { } } Items.Clear(); }
    }

    /// <summary>One verification sample: the oracle's fingerprint of a source
    /// reference and the handles of what replaced it.</summary>
    public class VerifySample
    {
        public string Where;
        public CurveMetrics Oracle;
        public List<long> OutputHandles = new List<long>();
        public bool OutputIsLeafReference;   // OPTIMIZED: output references are exploded one level to measure
        public bool Unmeasurable;            // a non-curve was exploded into curves: not comparable
    }

    public class Flattener
    {
        readonly Database _db;
        readonly PublishOptions _opt;
        readonly Report _rep;
        readonly Progress _prog;
        Appearance _app;
        DefinitionCache _defs;
        EntityTransformer _xf;
        Writer _w;
        long _expectedTotal = 1;
        int _leafSerial;
        readonly Dictionary<string, ObjectId> _leafCache = new Dictionary<string, ObjectId>();
        readonly HashSet<ObjectId> _leafHasExplodedNonCurve = new HashSet<ObjectId>();
        public readonly List<VerifySample> Samples = new List<VerifySample>();
        public Extents3d SourceExtents;
        public bool HasSourceExtents;
        public long ModelSpaceCountAfter;
        public int LeafBlocks, LeafReferences, TopLevelReferences, TopLevelOther;

        public Flattener(Database db, PublishOptions opt, Report rep, Progress prog)
        {
            _db = db; _opt = opt; _rep = rep; _prog = prog;
        }

        public void Run()
        {
            ObjectId msId;
            List<ObjectId> order;
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                _app = new Appearance(_db, tr, _opt, _rep);
                if (_opt.Layers == LayerPolicy.SingleLayer)
                {
                    _app.OutputLayer = EnsureLayer(tr, _opt.OutputLayer);
                    _app.RefreshLayers(_db, tr);
                }
                BlockTable bt = (BlockTable)tr.GetObject(_db.BlockTableId, OpenMode.ForRead);
                msId = bt[BlockTableRecord.ModelSpace];
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(msId, OpenMode.ForRead);
                order = new List<ObjectId>(DefinitionCache.OrderedIds(ms, tr));
                tr.Commit();
            }
            _defs = new DefinitionCache(_db, _opt, _rep, _app);
            _xf = new EntityTransformer(_rep);

            bool completed = false;
            try
            {
                _w = new Writer(_db, _opt.BatchSize, OnFlush);
                ModelSpaceSink msSink = new ModelSpaceSink(_w, msId);

                // ---- pass 1: count, extents, expected output size -------------------
                _prog.Stage("Measuring model space");
                int topRefs = 0;
                long expected = 0;
                int reads = 0;
                foreach (ObjectId id in order)
                {
                    if (id.IsErased) continue;
                    if (++reads % _opt.BatchSize == 0) _w.Flush();   // keep the read transaction small
                    Entity e = _w.Tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (e == null) continue;
                    try
                    {
                        Extents3d ex = e.GeometricExtents;
                        if (!HasSourceExtents) { SourceExtents = ex; HasSourceExtents = true; } else SourceExtents.AddExtents(ex);
                    }
                    catch { }
                    BlockReference br = e as BlockReference;
                    if (br != null)
                    {
                        topRefs++;
                        expected += br.AttributeCollection.Count + _defs.Expanded(br.BlockTableRecord, _w.Tr, 0);
                    }
                    else expected++;
                }
                _expectedTotal = Math.Max(1, expected);
                _rep.Sections["estimate"] = MakeEstimate(expected, topRefs);
                _prog.Log(string.Format("{0:N0} model-space objects, {1:N0} block references, about {2:N0} entities once expanded ({3:N0} distinct definitions read).",
                    order.Count, topRefs, expected, _defs.Count));
                _w.Flush();

                int sampleEvery = Math.Max(1, topRefs / Math.Max(1, _opt.VerifySamples));

                // ---- pass 2: expand --------------------------------------------------
                _prog.Stage(_opt.Mode == PublishMode.FullFlat ? "Expanding block references (FULL_FLAT)" : "Building flat leaf blocks (OPTIMIZED)");
                List<ObjectId> finalOrder = new List<ObjectId>(order.Count);
                int refIndex = 0;
                Ctx root = Ctx.Root(_app.LayerZero, false);
                reads = 0;
                foreach (ObjectId id in order)
                {
                    if (id.IsErased) continue;
                    if (++reads % _opt.BatchSize == 0) _w.Flush();
                    Entity e = _w.Tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (e == null) { finalOrder.Add(id); continue; }

                    BlockReference br = e as BlockReference;
                    if (br != null)
                    {
                        TopLevelReferences++;
                        string where = "MS>" + DefinitionCache.SafeName(br) + "#" + br.Handle;
                        List<ProtoItem> items = new List<ProtoItem>();
                        _defs.AddNested(items, br, true, _w.Tr, "MS");

                        VerifySample sample = null;
                        if (_opt.VerifyGeometry && (refIndex % sampleEvery) == 0 && Samples.Count < _opt.VerifySamples)
                        {
                            CurveMetrics oracle = Oracle.Measure(br, _w.Tr, _app, _opt, _rep);
                            if (oracle != null) { sample = new VerifySample(); sample.Where = where; sample.Oracle = oracle; }
                        }
                        refIndex++;

                        msSink.Slot = new List<ObjectId>();
                        long ncBefore = _xf.NonCurveExplosions;
                        bool visible = br.Visible;
                        Handle h = br.Handle;
                        if (!visible) _rep.Count("omitted.invisibleTopLevelReference");
                        else foreach (ProtoItem it in items)
                        {
                            if (_opt.Mode == PublishMode.FullFlat)
                                InstantiateNested(it, Matrix3d.Identity, root, false, msSink, 0);
                            else
                                PlaceLeafReference(it, root, msSink, sample);
                        }
                        // replace the original reference
                        Entity old = (Entity)_w.Tr.GetObject(id, OpenMode.ForWrite, false, true);   // works on locked layers too
                        old.Erase();
                        finalOrder.AddRange(msSink.Slot);
                        if (sample != null)
                        {
                            foreach (ObjectId oid in msSink.Slot) sample.OutputHandles.Add(oid.Handle.Value);
                            sample.OutputIsLeafReference = _opt.Mode == PublishMode.Optimized;
                            // a hatch/region exploded into curves makes the curve fingerprint incomparable
                            if (_xf.NonCurveExplosions != ncBefore) sample.Unmeasurable = true;
                            Samples.Add(sample);
                        }
                        msSink.Slot = null;
                        DefinitionCache.DisposeItems(items);
                        continue;
                    }

                    TopLevelOther++;
                    HandleTopLevelEntity(e, id, root, msSink, finalOrder);
                }
                _w.Flush();

                // ---- draw order ------------------------------------------------------
                if (_defs.DrawOrderSensitive)
                {
                    _prog.Log("Restoring draw order (wipeouts / fills present).");
                    try
                    {
                        BlockTableRecord ms = (BlockTableRecord)_w.Tr.GetObject(msId, OpenMode.ForWrite);
                        ObjectId dotId = ms.DrawOrderTableId;   // created on demand for model space
                        if (dotId.IsNull) throw new InvalidOperationException("model space has no draw-order table");
                        DrawOrderTable dt = (DrawOrderTable)_w.Tr.GetObject(dotId, OpenMode.ForWrite);
                        ObjectIdCollection coll = new ObjectIdCollection();
                        foreach (ObjectId oid in finalOrder) if (!oid.IsErased) coll.Add(oid);
                        dt.SetRelativeDrawOrder(coll);
                    }
                    catch (System.Exception ex)
                    {
                        _rep.Issue(Severity.Warning, "DRAW_ORDER", "Could not restore draw order (wipeouts/fills may stack differently): " + ex.Message);
                    }
                }
                _w.Flush();
                completed = true;
            }
            finally
            {
                if (_w != null) { if (completed) _w.Dispose(); else _w.Abort(); }
                if (_defs != null) _defs.Dispose();
            }

            _rep.Count("output.entitiesCreated", _w.Total);
            _rep.Sections["structure"] = MakeStructure();
        }

        Dictionary<string, object> MakeEstimate(long expected, int topRefs)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["topLevelBlockReferences"] = topRefs;
            d["expandedEntitiesUpperBound"] = expected;
            return d;
        }

        Dictionary<string, object> MakeStructure()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["topLevelReferencesReplaced"] = TopLevelReferences;
            d["topLevelOtherEntities"] = TopLevelOther;
            if (_opt.Mode == PublishMode.Optimized)
            {
                d["leafBlocksCreated"] = LeafBlocks;
                d["leafReferencesPlaced"] = LeafReferences;
            }
            return d;
        }

        void OnFlush()
        {
            if (Autodesk.AutoCAD.DatabaseServices.HostApplicationServices.Current.UserBreak())
                throw new PublishCancelledException();
            double pct = Math.Min(99.0, 100.0 * _w.Total / Math.Max(1, _expectedTotal));
            _prog.Write(pct, string.Format("{0:N0} of ~{1:N0} entities", _w.Total, _expectedTotal), false);
        }

        ObjectId EnsureLayer(Transaction tr, string name)
        {
            LayerTable lt = (LayerTable)tr.GetObject(_db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return lt[name];
            lt.UpgradeOpen();
            LayerTableRecord l = new LayerTableRecord();
            l.Name = name;
            ObjectId id = lt.Add(l);
            tr.AddNewlyCreatedDBObject(l, true);
            _prog.Log("Created output layer " + name);
            return id;
        }

        // ---- instantiation ----------------------------------------------------------

        void InstantiateItems(BlockProto p, Matrix3d m, Ctx ctx, bool leafTarget, ISink sink, int depth, ref bool notExpr)
        {
            bool identity = EntityTransformer.IsIdentity(m);
            double ltScale = identity ? 1.0 : EntityTransformer.ScaleOf(m);
            foreach (ProtoItem it in p.Items)
            {
                if (it.IsNested) { InstantiateNested(it, m, ctx, leafTarget, sink, depth, ref notExpr); continue; }
                EmitLeaf(it.Leaf, it.LeafProps, m, identity, ltScale, ctx, leafTarget, sink, it.Where, ref notExpr);
            }
        }

        void InstantiateNested(ProtoItem it, Matrix3d m, Ctx ctx, bool leafTarget, ISink sink, int depth)
        {
            bool dummy = false;
            InstantiateNested(it, m, ctx, leafTarget, sink, depth, ref dummy);
        }

        void InstantiateNested(ProtoItem it, Matrix3d m, Ctx ctx, bool leafTarget, ISink sink, int depth, ref bool notExpr)
        {
            if (!it.Props.Visible) { _rep.Count("omitted.invisibleReference"); return; }
            if (depth > 64)
            {
                _rep.Issue(Severity.Error, "NESTING_TOO_DEEP", "Block nesting deeper than 64 levels; the remainder is skipped.", it.Where);
                return;
            }
            Ctx child = _app.Enter(ctx, it.Props);
            if (child.AncestorFrozen && !ctx.AncestorFrozen)
            {
                long n = _defs.ExpandedOf(it, _w.Tr);
                _rep.Count("omitted.underFrozenReference", n);
                return;
            }
            if (it.Attribs != null)
            {
                bool identity = EntityTransformer.IsIdentity(m);
                double lts = identity ? 1.0 : EntityTransformer.ScaleOf(m);
                for (int i = 0; i < it.Attribs.Count; i++)
                    EmitLeaf(it.Attribs[i], it.AttribProps[i], m, identity, lts, child, leafTarget, sink, it.Where, ref notExpr);
            }
            BlockProto p = it.Inline != null ? it.Inline : _defs.Get(it.BlockId, _w.Tr);
            if (p == null) return;
            if (p.Missing)
            {
                _rep.Issue(_opt.AllowMissingXrefs ? Severity.Warning : Severity.Error, "MISSING_DEFINITION", "Referenced block definition is missing or unresolved; nothing to expand.", it.Where);
                return;
            }
            if (p.Instantiating)
            {
                _rep.Issue(Severity.Error, "BLOCK_CYCLE", "Block '" + p.Name + "' contains itself (corrupt definition); the inner occurrence is skipped.", it.Where);
                return;
            }
            if (it.Inline == null) _rep.Count("references.expanded");
            p.Instantiating = true;
            try { InstantiateItems(p, m * it.Transform, child, leafTarget, sink, depth + 1, ref notExpr); }
            finally { p.Instantiating = false; }
        }

        void EmitLeaf(Entity leaf, InsertProps props, Matrix3d m, bool identity, double ltScale, Ctx ctx, bool leafTarget, ISink sink, string where, ref bool notExpr)
        {
            ResolvedProps rp = _app.Resolve(props, ctx, leafTarget);
            if (rp.Omit) { _rep.Count("omitted.hidden"); _rep.Count("omitted." + rp.OmitCode); return; }
            if (rp.NotExpressible) notExpr = true;
            if (identity)
            {
                Entity c = (Entity)leaf.Clone();
                Appearance.Apply(c, rp);
                sink.Emit(c);
                return;
            }
            List<Entity> outs = _xf.Transform(leaf, m, where);
            foreach (Entity o in outs)
            {
                Appearance.Apply(o, rp);
                if (ltScale != 1.0) { try { o.LinetypeScale = leaf.LinetypeScale * ltScale; } catch { } }
                sink.Emit(o);
            }
        }

        // ---- OPTIMIZED: one level of shared leaf blocks ----------------------------

        void PlaceLeafReference(ProtoItem it, Ctx root, ModelSpaceSink msSink, VerifySample sample)
        {
            if (!it.Props.Visible) { _rep.Count("omitted.invisibleReference"); return; }
            // The replacement reference keeps the original's layer. Only a FROZEN
            // layer hides the whole reference; an off / no-plot layer hides just
            // its layer-0 children, which the leaf content handles per entity.
            ResolvedProps refProps = _app.Resolve(it.Props, root, false, false);
            if (_opt.Layers == LayerPolicy.SingleLayer && _opt.OmitHiddenInSingleLayer)
            {
                LayerInfo rl = _app.Layer(it.Props.LayerId);
                if (rl != null && rl.Frozen) { _rep.Count("omitted.hidden"); _rep.Count("omitted.onFrozenLayer"); return; }
            }

            BlockProto p = _defs.Get(it.BlockId, _w.Tr);
            if (p == null || p.Missing)
            {
                _rep.Issue(Severity.Error, "MISSING_DEFINITION", "Referenced block definition is missing or unresolved.", it.Where);
                return;
            }

            // context for the leaf contents
            Ctx concrete = _app.Enter(root, it.Props);
            concrete.AncestorFrozen = false;          // the new reference keeps the original's layer, frozen or not,
            concrete.IgnoreFrozenLayer = concrete.Layer;   // so inheriting that layer must not hide leaf content
            bool single = _opt.Layers == LayerPolicy.SingleLayer;
            string baseKey = LeafKey(it, p);
            ObjectId leafId = ObjectId.Null;

            if (!single)
            {
                string symKey = baseKey + "|sym";
                if (!_leafCache.TryGetValue(symKey, out leafId))
                {
                    ListSink tmp = new ListSink();
                    bool ne = false;
                    long nc0 = _xf.NonCurveExplosions;
                    InstantiateItems(p, Matrix3d.Identity, Ctx.Root(_app.LayerZero, true), true, tmp, 1, ref ne);
                    if (!ne) { leafId = CreateLeaf(p, tmp, nc0); _leafCache[symKey] = leafId; }
                    else { tmp.DisposeAll(); leafId = ObjectId.Null; _leafCache[symKey] = ObjectId.Null; }
                }
            }
            if (leafId.IsNull)
            {
                string ctxKey = baseKey + "|" + concrete.Signature();
                if (!_leafCache.TryGetValue(ctxKey, out leafId))
                {
                    ListSink tmp = new ListSink();
                    bool ne = false;
                    long nc0 = _xf.NonCurveExplosions;
                    InstantiateItems(p, Matrix3d.Identity, concrete, true, tmp, 1, ref ne);
                    leafId = CreateLeaf(p, tmp, nc0);
                    _leafCache[ctxKey] = leafId;
                    if (!single) _rep.Count("optimized.contextSpecificLeaves");
                }
            }

            // attributes of the original reference become model-space text
            if (it.Attribs != null)
            {
                Ctx child = _app.Enter(root, it.Props);
                child.AncestorFrozen = false;
                child.IgnoreFrozenLayer = child.Layer;
                bool dummy = false;
                for (int i = 0; i < it.Attribs.Count; i++)
                    EmitLeaf(it.Attribs[i], it.AttribProps[i], Matrix3d.Identity, true, 1.0, child, false, msSink, it.Where, ref dummy);
            }

            if (leafId.IsNull) { _rep.Count("optimized.emptyLeaf"); return; }   // everything inside was hidden
            if (sample != null && _leafHasExplodedNonCurve.Contains(leafId)) sample.Unmeasurable = true;

            BlockReference nr = new BlockReference(it.Position, leafId);
            nr.SetDatabaseDefaults(_db);
            nr.Normal = it.Normal;
            nr.Position = it.Position;
            nr.ScaleFactors = it.Scale;
            nr.Rotation = it.Rotation;
            Appearance.Apply(nr, refProps);
            msSink.Emit(nr);
            LeafReferences++;
        }

        string LeafKey(ProtoItem it, BlockProto p)
        {
            // Instances of the same definition share a leaf. Dynamic-block
            // instances each own an anonymous *U definition; two *U blocks
            // of the same dynamic block with identical parameter values are
            // geometrically identical, so they share too.
            string key = it.BlockId.Handle.ToString();
            try
            {
                BlockTableRecord b = (BlockTableRecord)_w.Tr.GetObject(it.BlockId, OpenMode.ForRead);
                if (b.IsAnonymous && b.Name.StartsWith("*U", StringComparison.OrdinalIgnoreCase))
                {
                    string dk = DynamicKeyFor(b);
                    if (dk != null) key = dk + "@" + Util.Fmt(p.Origin.X) + "," + Util.Fmt(p.Origin.Y) + "," + Util.Fmt(p.Origin.Z);
                }
            }
            catch { }
            return key;
        }

        readonly Dictionary<ObjectId, string> _dynKeys = new Dictionary<ObjectId, string>();

        string DynamicKeyFor(BlockTableRecord anon)
        {
            string k;
            if (_dynKeys.TryGetValue(anon.ObjectId, out k)) return k;
            k = null;
            // find one reference to this *U block and read its dynamic state
            foreach (ObjectId rid in anon.GetBlockReferenceIds(true, false))
            {
                BlockReference r = _w.Tr.GetObject(rid, OpenMode.ForRead) as BlockReference;
                if (r == null || !r.IsDynamicBlock) continue;
                List<string> parts = new List<string>();
                foreach (DynamicBlockReferenceProperty dp in r.DynamicBlockReferencePropertyCollection)
                {
                    object v = null;
                    try { v = dp.Value; } catch { }
                    parts.Add(dp.PropertyName + "=" + Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
                }
                parts.Sort(StringComparer.Ordinal);
                k = "DYN:" + r.DynamicBlockTableRecord.Handle + ":" + string.Join(";", parts.ToArray());
                break;
            }
            _dynKeys[anon.ObjectId] = k;
            return k;
        }

        ObjectId CreateLeaf(BlockProto p, ListSink content, long nonCurveBefore)
        {
            if (content.Items.Count == 0) return ObjectId.Null;
            BlockTable bt = (BlockTable)_w.Tr.GetObject(_db.BlockTableId, OpenMode.ForWrite);
            string name;
            do { name = "FLAT$" + Util.Sanitize(p.Name.TrimStart('*')) + "$" + (++_leafSerial); } while (bt.Has(name));
            BlockTableRecord b = new BlockTableRecord();
            b.Name = name;
            b.Origin = p.Origin;
            ObjectId id = bt.Add(b);
            _w.Tr.AddNewlyCreatedDBObject(b, true);
            foreach (Entity e in content.Items) _w.Append(e, id);
            content.Items.Clear();
            LeafBlocks++;
            if (_xf.NonCurveExplosions != nonCurveBefore) _leafHasExplodedNonCurve.Add(id);
            return id;
        }

        // ---- top-level non-block entities --------------------------------------------

        void HandleTopLevelEntity(Entity e, ObjectId id, Ctx root, ModelSpaceSink msSink, List<ObjectId> finalOrder)
        {
            bool rebuild = false;
            if (DefinitionCache.IsCustom(e) && _opt.ExplodeCustomObjects) rebuild = true;
            if (DefinitionCache.IsNonDwgDependency(e) && _opt.NonDwgDependencies != DependencyPolicy.Keep) rebuild = true;

            bool warned = false;
            if (rebuild)
            {
                List<ProtoItem> items = new List<ProtoItem>();
                bool copiedAsIs = _defs.AddEntity(items, e, true, _w.Tr, "MS", 0);
                if (copiedAsIs)
                {
                    // could not be exploded / policy keeps it: leave the original where it is
                    DefinitionCache.DisposeItems(items);
                    warned = true;
                }
                else
                {
                    msSink.Slot = new List<ObjectId>();
                    bool dummy = false;
                    foreach (ProtoItem it in items)
                    {
                        if (it.IsNested) InstantiateNested(it, Matrix3d.Identity, root, false, msSink, 0, ref dummy);
                        else EmitLeaf(it.Leaf, it.LeafProps, Matrix3d.Identity, true, 1.0, root, false, msSink, "MS", ref dummy);
                    }
                    DefinitionCache.DisposeItems(items);
                    Entity old = (Entity)_w.Tr.GetObject(id, OpenMode.ForWrite, false, true);   // works on locked layers too
                    old.Erase();
                    finalOrder.AddRange(msSink.Slot);
                    msSink.Slot = null;
                    _rep.Count("topLevel.rebuilt");
                    return;
                }
            }

            if (!warned && DefinitionCache.IsNonDwgDependency(e))
                _rep.Issue(Severity.Warning, "NONDWG_DEPENDENCY", e.GetType().Name + " in model space still references an external file; the output is NOT self-contained.", "MS#" + e.Handle);
            else if (!warned && DefinitionCache.IsCustom(e))
                _rep.Issue(Severity.Warning, "CUSTOM_RETAINED", e.GetType().Name + " in model space kept as a custom object.", "MS#" + e.Handle);

            if (e is Wipeout || e is Hatch || e is Solid || e is RasterImage) _defs.DrawOrderSensitive = true;

            if (_opt.Layers == LayerPolicy.SingleLayer)
            {
                ResolvedProps rp = _app.Resolve(InsertProps.From(e), root, false);
                Entity w = (Entity)_w.Tr.GetObject(id, OpenMode.ForWrite, false, true);
                if (rp.Omit)
                {
                    w.Erase();
                    _rep.Count("omitted.hidden");
                    _rep.Count("omitted." + rp.OmitCode);
                    return;
                }
                Appearance.Apply(w, rp);
                _rep.Count("topLevel.movedToOutputLayer");
            }
            finalOrder.Add(id);
        }
    }
}
