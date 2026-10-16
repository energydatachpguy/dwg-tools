// DwgFlatPublisher -- memoised block-definition prototypes.
//
// Each block definition is read ONCE and turned into a list of items:
//   * Leaf   -- a non-database-resident copy of a drawable entity, in the
//               block's own coordinates
//   * Nested -- "insert block B here with matrix T", with the reference's
//               own properties and its attribute texts
// Expanding 1,000 instances of an equipment block that holds 10 sub-blocks
// of 100 entities therefore reads 10*100 + 10 source entities, not a
// million: instantiation walks this DAG with a composed matrix and only
// creates output objects. Recursion is guarded against cycles.

using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace DwgFlat
{
    public class ProtoItem
    {
        // Leaf
        public Entity Leaf;
        public InsertProps LeafProps;
        // Nested
        public bool IsNested;
        public ObjectId BlockId;
        /// <summary>Nested content that is not a block definition: the pieces of
        /// an exploded dimension / leader / custom object. They display like a
        /// block's children (layer 0 and ByBlock follow the object), so they are
        /// instantiated exactly like a nested reference.</summary>
        public BlockProto Inline;
        public Matrix3d Transform;
        public InsertProps Props;
        public List<Entity> Attribs;
        public List<InsertProps> AttribProps;
        public string Where;
        // placement of the original reference (OPTIMIZED re-creates it exactly)
        public Point3d Position;
        public Scale3d Scale;
        public double Rotation;
        public Vector3d Normal;
    }

    public class BlockProto
    {
        public string Name = "";
        public Point3d Origin;
        public readonly List<ProtoItem> Items = new List<ProtoItem>();
        public bool Building, Missing, Counting;
        public bool Instantiating;   // on the instantiation stack (cycle guard)
        public long ExpandedMemo = -1;
    }

    public class DefinitionCache : IDisposable
    {
        readonly Database _db;
        readonly PublishOptions _opt;
        readonly Report _rep;
        readonly Appearance _app;
        readonly Dictionary<ObjectId, BlockProto> _map = new Dictionary<ObjectId, BlockProto>();
        public bool DrawOrderSensitive;
        const int MaxExplodeDepth = 8;

        public DefinitionCache(Database db, PublishOptions opt, Report rep, Appearance app)
        {
            _db = db; _opt = opt; _rep = rep; _app = app;
        }

        public int Count { get { return _map.Count; } }

        public BlockProto Get(ObjectId btrId, Transaction tr)
        {
            BlockProto p;
            if (_map.TryGetValue(btrId, out p))
            {
                if (p.Building)
                {
                    _rep.Issue(Severity.Error, "BLOCK_CYCLE", "Block '" + p.Name + "' contains itself (circular definition); the inner occurrence is skipped.");
                    return null;
                }
                return p;
            }
            p = new BlockProto();
            _map[btrId] = p;
            if (btrId.IsNull || btrId.IsErased) { p.Missing = true; return p; }
            BlockTableRecord btr = tr.GetObject(btrId, OpenMode.ForRead) as BlockTableRecord;
            if (btr == null) { p.Missing = true; return p; }
            p.Name = btr.Name;
            p.Origin = btr.Origin;
            if (btr.IsFromExternalReference)
            {
                p.Missing = true;
                _rep.Issue(Severity.Error, "XREF_NOT_BOUND", "Block '" + btr.Name + "' is still an external reference; its geometry cannot be expanded.");
                return p;
            }
            p.Building = true;
            _rep.Count("definitions.read");
            foreach (ObjectId id in OrderedIds(btr, tr))
            {
                if (id.IsErased) continue;
                Entity e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (e == null) continue;
                _rep.Count("definitions.sourceEntities");
                AddEntity(p.Items, e, true, tr, p.Name, 0);
            }
            p.Building = false;
            return p;
        }

        /// <summary>Entity ids of a block in display order (honours the block's
        /// own draw-order table, which matters for wipeouts and solid fills).</summary>
        public static IEnumerable<ObjectId> OrderedIds(BlockTableRecord btr, Transaction tr)
        {
            ObjectId dot = ObjectId.Null;
            try { dot = btr.DrawOrderTableId; } catch { }
            if (!dot.IsNull && !dot.IsErased)
            {
                ObjectIdCollection ids = null;
                try
                {
                    DrawOrderTable dt = tr.GetObject(dot, OpenMode.ForRead) as DrawOrderTable;
                    if (dt != null) ids = dt.GetFullDrawOrder(0);
                }
                catch { ids = null; }
                if (ids != null)
                {
                    List<ObjectId> l = new List<ObjectId>(ids.Count);
                    foreach (ObjectId id in ids) l.Add(id);
                    return l;
                }
            }
            List<ObjectId> l2 = new List<ObjectId>();
            foreach (ObjectId id in btr) l2.Add(id);
            return l2;
        }

        public static bool IsCustom(Entity e)
        {
            if (e is ProxyEntity) return true;
            Type t = e.GetType();
            if (t.Namespace != "Autodesk.AutoCAD.DatabaseServices") return true;
            return t.Name == "ImpEntity";
        }

        public static bool IsAnnotationToExplode(Entity e)
        {
            // Nested annotation objects own hidden helper blocks / linked
            // objects that a plain copy would share with the original, so
            // inside blocks they are reduced to their exact displayed pieces.
            return e is Dimension || e is Leader || e is MLeader || e is Table || e is Mline;
        }

        public static bool IsNonDwgDependency(Entity e)
        {
            if (e is Wipeout) return false;   // wipeouts are self-contained
            if (e is RasterImage || e is UnderlayReference) return true;
            string n = e.GetType().Name;
            return n.StartsWith("PointCloud", StringComparison.Ordinal);
        }

        /// <summary>Turn one entity into prototype items. 'resident' = it is
        /// the database's own object (must be copied), otherwise it is a
        /// throw-away piece from an explode that we now own. Returns true only
        /// when the entity was carried over as a plain copy of itself (so a
        /// caller rebuilding a top-level object knows nothing changed).</summary>
        public bool AddEntity(List<ProtoItem> items, Entity e, bool resident, Transaction tr, string where, int depth)
        {
            if (!e.Visible) { _rep.Count("omitted.invisibleEntity"); if (!resident) e.Dispose(); return false; }

            AttributeDefinition ad = e as AttributeDefinition;
            if (ad != null)
            {
                if (ad.Constant && !ad.Invisible)
                {
                    Entity t = EntityTransformer.TextFromConstantAttDef(ad);
                    AddLeaf(items, t, where);
                    _rep.Count("text.constantAttributes");
                }
                else _rep.Count("omitted.attributeDefinitionNotDisplayed");
                if (!resident) e.Dispose();
                return false;
            }

            BlockReference br = e as BlockReference;
            if (br != null)
            {
                AddNested(items, br, resident, tr, where);
                if (!resident) e.Dispose();
                return false;
            }

            if (IsNonDwgDependency(e))
            {
                switch (_opt.NonDwgDependencies)
                {
                    case DependencyPolicy.Exclude:
                        _rep.Count("omitted.nonDwgDependency");
                        _rep.Issue(Severity.Warning, "NONDWG_EXCLUDED", e.GetType().Name + " excluded by policy (nonDwgDependencies=EXCLUDE).", where);
                        if (!resident) e.Dispose();
                        return false;
                    case DependencyPolicy.Fail:
                        _rep.Issue(Severity.Error, "NONDWG_DEPENDENCY", e.GetType().Name + " references an external file (image/underlay/point cloud); policy is FAIL.", where);
                        break;
                    default:
                        _rep.Issue(Severity.Warning, "NONDWG_DEPENDENCY", e.GetType().Name + " still references an external file; the output is NOT self-contained.", where);
                        break;
                }
            }

            if (depth < MaxExplodeDepth && (IsAnnotationToExplode(e) || (IsCustom(e) && _opt.ExplodeCustomObjects)))
            {
                bool custom = !IsAnnotationToExplode(e);
                DBObjectCollection pieces = new DBObjectCollection();
                bool ok = true;
                try { e.Explode(pieces); } catch (System.Exception) { ok = false; }
                if (ok && pieces.Count > 0)
                {
                    _rep.Count(custom ? "exploded.customObjects" : "exploded.annotation");
                    BlockProto inline = new BlockProto();
                    inline.Name = e.GetType().Name;
                    string w2 = where + "/" + e.GetType().Name;
                    foreach (DBObject o in pieces)
                    {
                        Entity pe = o as Entity;
                        if (pe == null) { o.Dispose(); continue; }
                        AddEntity(inline.Items, pe, false, tr, w2, depth + 1);
                    }
                    ProtoItem it = new ProtoItem();
                    it.IsNested = true;
                    it.Inline = inline;
                    it.BlockId = ObjectId.Null;
                    it.Transform = Matrix3d.Identity;   // explode pieces are already in the owner's coordinates
                    it.Props = InsertProps.From(e);
                    it.Props.Visible = true;
                    it.Where = w2;
                    it.Normal = Vector3d.ZAxis; it.Scale = new Scale3d(1);
                    items.Add(it);
                    if (!resident) e.Dispose();
                    return false;
                }
                foreach (DBObject o in pieces) o.Dispose();
                if (custom)
                    _rep.Issue(Severity.Warning, "CUSTOM_RETAINED",
                        e.GetType().Name + " (" + EntityTransformer.SafeDxf(e) + ") could not be exploded and is carried over as-is; viewers need its object enabler.", where);
                else
                    _rep.Issue(Severity.Warning, "ANNOTATION_COPIED", e.GetType().Name + " could not be exploded; copied as an object.", where);
            }
            else if (IsCustom(e))
            {
                _rep.Issue(Severity.Warning, "CUSTOM_RETAINED",
                    e.GetType().Name + " (" + EntityTransformer.SafeDxf(e) + ") carried over as a custom object (explodeCustomObjects=false); viewers need its object enabler.", where);
            }

            Entity leaf;
            if (resident)
            {
                try { leaf = (Entity)e.Clone(); }
                catch (System.Exception ex)
                {
                    _rep.Count("failed.entities");
                    _rep.Issue(Severity.Error, "COPY_FAILED", e.GetType().Name + " (" + EntityTransformer.SafeDxf(e) + ") could not be copied: " + ex.Message, where);
                    return true;   // "carried over as-is": a top-level caller keeps the original
                }
            }
            else leaf = e;
            Hatch h = leaf as Hatch;
            if (h != null) { try { if (h.Associative) h.Associative = false; } catch { } }
            AddLeaf(items, leaf, where);
            return resident;
        }

        void AddLeaf(List<ProtoItem> items, Entity leaf, string where)
        {
            if (leaf is Wipeout || leaf is Hatch || leaf is Solid || leaf is Trace || leaf is RasterImage)
                DrawOrderSensitive = true;
            MText mt = leaf as MText;
            if (mt != null) { try { if (mt.BackgroundFill) DrawOrderSensitive = true; } catch { } }
            ProtoItem it = new ProtoItem();
            it.Leaf = leaf;
            it.LeafProps = InsertProps.From(leaf);
            it.Where = where;
            items.Add(it);
        }

        /// <summary>A block reference (resident or exploded piece) as nested
        /// item(s). MINSERT arrays become one item per cell.</summary>
        public void AddNested(List<ProtoItem> items, BlockReference br, bool resident, Transaction tr, string where)
        {
            List<Entity> attribs = new List<Entity>();
            List<InsertProps> attProps = new List<InsertProps>();
            if (resident)
            {
                foreach (ObjectId aid in br.AttributeCollection)
                {
                    if (aid.IsErased) continue;
                    AttributeReference ar = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                    if (ar == null) continue;
                    if (ar.Invisible || !ar.Visible) { _rep.Count("omitted.invisibleAttribute"); continue; }
                    Entity t = EntityTransformer.TextFromAttribute(ar);
                    attribs.Add(t);
                    attProps.Add(InsertProps.From(t));
                    _rep.Count("text.attributes");
                }
            }

            if (HasClip(br, tr))
                _rep.Issue(Severity.Warning, "CLIPPED_BLOCK",
                    "Block reference '" + SafeName(br) + "' has an XCLIP boundary; the published geometry is NOT clipped (shows the full block).", where);

            InsertProps props = InsertProps.From(br);
            MInsertBlock mi = br as MInsertBlock;
            if (mi != null && (mi.Rows > 1 || mi.Columns > 1))
            {
                Matrix3d basis = br.BlockTransform;
                Matrix3d planeToWorld = Matrix3d.PlaneToWorld(br.Normal);
                for (int r = 0; r < mi.Rows; r++)
                    for (int c = 0; c < mi.Columns; c++)
                    {
                        Vector3d off = new Vector3d(c * mi.ColumnSpacing, r * mi.RowSpacing, 0).RotateBy(br.Rotation, Vector3d.ZAxis).TransformBy(planeToWorld);
                        ProtoItem it = NewNested(br, props, Matrix3d.Displacement(off) * basis, where);
                        it.Position = br.Position + off;
                        if (r == 0 && c == 0) { it.Attribs = attribs; it.AttribProps = attProps; }
                        items.Add(it);
                    }
                _rep.Count("minsert.cells", (long)mi.Rows * mi.Columns);
                if (attribs.Count > 0)
                    _rep.Issue(Severity.Info, "MINSERT_ATTRIBUTES", "MINSERT with attributes: attribute text is placed once (first cell). Check against the source.", where);
                return;
            }
            ProtoItem one = NewNested(br, props, br.BlockTransform, where);
            one.Attribs = attribs; one.AttribProps = attProps;
            items.Add(one);
        }

        static ProtoItem NewNested(BlockReference br, InsertProps props, Matrix3d m, string where)
        {
            ProtoItem it = new ProtoItem();
            it.IsNested = true;
            it.BlockId = br.BlockTableRecord;
            it.Transform = m;
            it.Props = props;
            it.Where = where + ">" + SafeName(br);
            it.Position = br.Position;
            it.Scale = br.ScaleFactors;
            it.Rotation = br.Rotation;
            it.Normal = br.Normal;
            return it;
        }

        public static string SafeName(BlockReference br)
        {
            try { return br.Name; } catch { return "?"; }
        }

        public static bool HasClip(Entity e, Transaction tr)
        {
            try
            {
                ObjectId x = e.ExtensionDictionary;
                if (x.IsNull || x.IsErased) return false;
                DBDictionary d = tr.GetObject(x, OpenMode.ForRead) as DBDictionary;
                return d != null && d.Contains("ACAD_FILTER");
            }
            catch { return false; }
        }

        /// <summary>Number of output entities one instance of the block produces
        /// (upper bound: before visibility omissions). Memoised; a block that
        /// (corruptly) contains itself counts 0 on the repeat instead of looping.</summary>
        public long Expanded(ObjectId btrId, Transaction tr, int depth)
        {
            BlockProto p = Get(btrId, tr);
            if (p == null || p.Missing) return 0;
            if (p.ExpandedMemo >= 0) return p.ExpandedMemo;
            if (p.Counting || depth > 64) return 0;
            p.Counting = true;
            long n = CountItems(p.Items, tr, depth);
            p.Counting = false;
            p.ExpandedMemo = n;
            return n;
        }

        public long ExpandedOf(ProtoItem it, Transaction tr)
        {
            if (it.Inline != null) return CountItems(it.Inline.Items, tr, 0);
            return Expanded(it.BlockId, tr, 0);
        }

        long CountItems(List<ProtoItem> items, Transaction tr, int depth)
        {
            long n = 0;
            foreach (ProtoItem it in items)
            {
                if (!it.IsNested) n++;
                else if (it.Inline != null) n += CountItems(it.Inline.Items, tr, depth + 1);
                else n += (it.Attribs == null ? 0 : it.Attribs.Count) + Expanded(it.BlockId, tr, depth + 1);
            }
            return n;
        }

        public void Dispose()
        {
            foreach (BlockProto p in _map.Values) DisposeItems(p.Items);
            _map.Clear();
        }

        public static void DisposeItems(List<ProtoItem> items)
        {
            foreach (ProtoItem it in items)
            {
                if (it.Leaf != null) { try { it.Leaf.Dispose(); } catch { } }
                if (it.Attribs != null) foreach (Entity a in it.Attribs) { try { a.Dispose(); } catch { } }
                if (it.Inline != null) DisposeItems(it.Inline.Items);
            }
        }
    }
}
