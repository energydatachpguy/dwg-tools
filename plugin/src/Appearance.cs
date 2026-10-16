// DwgFlatPublisher -- ByLayer / ByBlock / layer-0 inheritance.
//
// How AutoCAD displays an entity that sits inside a block reference:
//   * layer "0"          -> drawn on the layer of the containing insert
//                           (recursively: if that insert is on 0 too, keep going up)
//   * colour ByLayer     -> colour of the entity's EFFECTIVE layer (after the rule above)
//   * colour ByBlock     -> whatever the containing insert resolves to
//   * linetype / lineweight / transparency follow the same ByLayer / ByBlock rules
//   * a frozen layer on ANY containing insert hides the whole subtree
//   * an insert's layer being OFF only hides its layer-0 children (they take its layer)
//
// Once the hierarchy is collapsed, the containing inserts are gone, so every
// inherited value has to be resolved into something the flat entity can
// carry by itself. An "Inh" value is the inherited state passed down the
// tree: Explicit (a concrete value), ByBlock (still inherit from further
// up), or LayerOf(L) ("use layer L's value").
//
// At the very top (model space) nothing is above, so the root context is
// ByBlock everywhere on layer 0 -- an entity whose chain is ByBlock all the
// way up displays exactly like a ByBlock entity drawn directly in model
// space, so leaving it ByBlock in the output is exact.
//
// In OPTIMIZED mode a leaf block is built against a "symbolic" root: layer 0
// and ByBlock mean "inherit from the new single-level reference", which is
// given the original reference's properties, so the same leaf can be shared
// by every instance. A value that cannot be expressed that way (ByLayer of
// the outer insert's layer, on an entity that sits on another layer) makes
// the leaf fall back to a per-context definition.

using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace DwgFlat
{
    public enum InhKind { Explicit, ByBlock, LayerOf }

    /// <summary>One inherited attribute (colour, linetype, lineweight or transparency).</summary>
    public struct Inh
    {
        public InhKind Kind;
        public ObjectId Layer;      // LayerOf: which layer
        public bool LayerSymbolic;  // LayerOf a symbolic layer-0 (OPTIMIZED leaf root)
        public object Value;        // Explicit: Color / ObjectId (linetype) / LineWeight / Transparency

        public static Inh ByBlock() { Inh i = new Inh(); i.Kind = InhKind.ByBlock; return i; }
        public static Inh Explicit(object v) { Inh i = new Inh(); i.Kind = InhKind.Explicit; i.Value = v; return i; }
        public static Inh LayerOf(ObjectId layer, bool symbolic) { Inh i = new Inh(); i.Kind = InhKind.LayerOf; i.Layer = layer; i.LayerSymbolic = symbolic; return i; }

        public string Signature()
        {
            switch (Kind)
            {
                case InhKind.ByBlock: return "B";
                case InhKind.LayerOf: return "L" + (LayerSymbolic ? "*" : Layer.Handle.ToString());
                default:
                    if (Value is Color) { Color c = (Color)Value; return "C" + c.ColorMethod + ":" + c.ColorValue.ToArgb(); }
                    if (Value is ObjectId) return "T" + ((ObjectId)Value).Handle.ToString();
                    if (Value is Transparency) return "A" + ((Transparency)Value).Alpha;
                    return "V" + Convert.ToString(Value);
            }
        }
    }

    /// <summary>Inherited display state at one point of the block tree.</summary>
    public class Ctx
    {
        public ObjectId Layer;        // effective layer for layer-0 children
        public bool LayerSymbolic;    // OPTIMIZED leaf root: "layer 0 = whatever the new reference is on"
        public Inh Color, Linetype, Lineweight, Transparency;
        public bool AncestorFrozen;   // some containing insert is on a frozen layer
        /// <summary>OPTIMIZED: the layer of the original top-level reference.
        /// The new single-level reference stays on that layer, so its being
        /// frozen is expressed by the reference itself and must not hide
        /// content inside the leaf (it would make the leaf depend on it).</summary>
        public ObjectId IgnoreFrozenLayer = ObjectId.Null;

        public static Ctx Root(ObjectId layerZero, bool symbolic)
        {
            Ctx c = new Ctx();
            c.Layer = layerZero; c.LayerSymbolic = symbolic;
            c.Color = Inh.ByBlock(); c.Linetype = Inh.ByBlock(); c.Lineweight = Inh.ByBlock(); c.Transparency = Inh.ByBlock();
            return c;
        }

        public string Signature()
        {
            return (LayerSymbolic ? "*" : Layer.Handle.ToString()) + "|" + Color.Signature() + "|" + Linetype.Signature()
                + "|" + Lineweight.Signature() + "|" + Transparency.Signature() + "|" + (AncestorFrozen ? "F" : "");
        }
    }

    /// <summary>Properties of a block reference that matter for inheritance --
    /// captured once so the reference object can be closed.</summary>
    public struct InsertProps
    {
        public ObjectId LayerId, LinetypeId;
        public Color Color;
        public LineWeight LineWeight;
        public Transparency Transparency;
        public bool Visible;

        public static InsertProps From(Entity e)
        {
            InsertProps p = new InsertProps();
            p.LayerId = e.LayerId; p.LinetypeId = e.LinetypeId; p.Color = e.Color;
            p.LineWeight = e.LineWeight; p.Transparency = e.Transparency; p.Visible = e.Visible;
            return p;
        }
    }

    /// <summary>The concrete values an output entity gets.</summary>
    public class ResolvedProps
    {
        public ObjectId Layer;
        public Color Color;
        public ObjectId Linetype;
        public LineWeight LineWeight;
        public Transparency Transparency;
        public bool Omit;
        public string OmitReason;
        public string OmitCode;
        public bool NotExpressible;   // OPTIMIZED symbolic leaf must be rebuilt per context
    }

    public class LayerInfo
    {
        public string Name;
        public bool Frozen, Off, Plottable;
        public Color Color;
        public ObjectId Linetype;
        public LineWeight LineWeight;
        public Transparency Transparency;
    }

    /// <summary>Applies the inheritance rules. One instance per database.</summary>
    public class Appearance
    {
        readonly Dictionary<ObjectId, LayerInfo> _layers = new Dictionary<ObjectId, LayerInfo>();
        public readonly ObjectId LayerZero;
        readonly ObjectId _ltByBlock, _ltByLayer;
        readonly PublishOptions _opt;
        readonly Report _rep;
        public ObjectId OutputLayer = ObjectId.Null;   // SINGLE_LAYER target

        public Appearance(Database db, Transaction tr, PublishOptions opt, Report rep)
        {
            _opt = opt; _rep = rep;
            LayerZero = db.LayerZero;
            _ltByBlock = db.ByBlockLinetype;
            _ltByLayer = db.ByLayerLinetype;
            RefreshLayers(db, tr);
        }

        public void RefreshLayers(Database db, Transaction tr)
        {
            _layers.Clear();
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in lt)
            {
                LayerTableRecord l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                LayerInfo li = new LayerInfo();
                li.Name = l.Name; li.Frozen = l.IsFrozen; li.Off = l.IsOff; li.Plottable = l.IsPlottable;
                li.Color = l.Color; li.Linetype = l.LinetypeObjectId; li.LineWeight = l.LineWeight;
                li.Transparency = l.Transparency;
                _layers[id] = li;
            }
        }

        public LayerInfo Layer(ObjectId id)
        {
            LayerInfo li;
            if (_layers.TryGetValue(id, out li)) return li;
            return null;
        }

        public bool IsByBlockLinetype(ObjectId id) { return id == _ltByBlock; }
        public bool IsByLayerLinetype(ObjectId id) { return id == _ltByLayer; }

        /// <summary>Context for the children of an insert (or of an exploded
        /// annotation object, which behaves the same way for its pieces).</summary>
        public Ctx Enter(Ctx outer, InsertProps ins)
        {
            Ctx c = new Ctx();
            if (ins.LayerId == LayerZero) { c.Layer = outer.Layer; c.LayerSymbolic = outer.LayerSymbolic; }
            else { c.Layer = ins.LayerId; c.LayerSymbolic = false; }

            c.Color = ins.Color.IsByBlock ? outer.Color
                    : ins.Color.IsByLayer ? Inh.LayerOf(c.Layer, c.LayerSymbolic)
                    : Inh.Explicit(ins.Color);
            c.Linetype = ins.LinetypeId == _ltByBlock ? outer.Linetype
                    : ins.LinetypeId == _ltByLayer ? Inh.LayerOf(c.Layer, c.LayerSymbolic)
                    : Inh.Explicit(ins.LinetypeId);
            c.Lineweight = ins.LineWeight == LineWeight.ByBlock ? outer.Lineweight
                    : ins.LineWeight == LineWeight.ByLayer ? Inh.LayerOf(c.Layer, c.LayerSymbolic)
                    : Inh.Explicit(ins.LineWeight);
            c.Transparency = ins.Transparency.IsByBlock ? outer.Transparency
                    : ins.Transparency.IsByLayer ? Inh.LayerOf(c.Layer, c.LayerSymbolic)
                    : Inh.Explicit(ins.Transparency);

            c.AncestorFrozen = outer.AncestorFrozen;
            c.IgnoreFrozenLayer = outer.IgnoreFrozenLayer;
            if (!c.LayerSymbolic && !(ins.LayerId == LayerZero && c.Layer == c.IgnoreFrozenLayer))
            {
                LayerInfo li = Layer(c.Layer);
                if (li != null && li.Frozen) c.AncestorFrozen = true;
            }
            return c;
        }

        /// <summary>Resolve what entity e (with properties p, inside context ctx)
        /// must carry. 'leafTarget' = OPTIMIZED leaf content (lives inside a
        /// block definition), false = top-level model space output.</summary>
        public ResolvedProps Resolve(InsertProps p, Ctx ctx, bool leafTarget)
        {
            return Resolve(p, ctx, leafTarget, true);
        }

        /// <summary>checkLayerVisibility=false: resolve properties only (used for
        /// the OPTIMIZED replacement reference itself, whose off / no-plot
        /// layer must not remove children that sit on other layers).</summary>
        public ResolvedProps Resolve(InsertProps p, Ctx ctx, bool leafTarget, bool checkLayerVisibility)
        {
            ResolvedProps r = new ResolvedProps();
            ObjectId effLayer; bool effSym;
            if (p.LayerId == LayerZero) { effLayer = ctx.Layer; effSym = ctx.LayerSymbolic; }
            else { effLayer = p.LayerId; effSym = false; }

            // ---- visibility ---------------------------------------------------
            if (ctx.AncestorFrozen) { r.Omit = true; r.OmitReason = "hidden: a containing block reference is on a frozen layer"; r.OmitCode = "underFrozenReference"; return r; }
            bool single = _opt.Layers == LayerPolicy.SingleLayer;
            if (single && _opt.OmitHiddenInSingleLayer && checkLayerVisibility)
            {
                if (effSym) { r.NotExpressible = true; }
                else
                {
                    LayerInfo li = Layer(effLayer);
                    if (li != null)
                    {
                        if (li.Frozen) { r.Omit = true; r.OmitReason = "hidden: on frozen layer " + li.Name; r.OmitCode = "onFrozenLayer"; return r; }
                        if (li.Off) { r.Omit = true; r.OmitReason = "hidden: on layer that is off: " + li.Name; r.OmitCode = "onOffLayer"; return r; }
                        if (!li.Plottable && !_opt.KeepNoPlot) { r.Omit = true; r.OmitReason = "not plotted: on no-plot layer " + li.Name; r.OmitCode = "onNoPlotLayer"; return r; }
                    }
                }
            }

            // ---- target layer -------------------------------------------------
            ObjectId target; bool targetIsSymbolicZero;
            if (single)
            {
                if (leafTarget) { target = LayerZero; targetIsSymbolicZero = false; }  // leaf content on 0, all props explicit
                else { target = OutputLayer; targetIsSymbolicZero = false; }
            }
            else
            {
                if (effSym) { target = LayerZero; targetIsSymbolicZero = true; }
                else { target = effLayer; targetIsSymbolicZero = false; }
            }
            r.Layer = target;

            Inh color = p.Color.IsByBlock ? ctx.Color : p.Color.IsByLayer ? Inh.LayerOf(effLayer, effSym) : Inh.Explicit(p.Color);
            Inh lt = p.LinetypeId == _ltByBlock ? ctx.Linetype : p.LinetypeId == _ltByLayer ? Inh.LayerOf(effLayer, effSym) : Inh.Explicit(p.LinetypeId);
            Inh lw = p.LineWeight == LineWeight.ByBlock ? ctx.Lineweight : p.LineWeight == LineWeight.ByLayer ? Inh.LayerOf(effLayer, effSym) : Inh.Explicit(p.LineWeight);
            Inh tr = p.Transparency.IsByBlock ? ctx.Transparency : p.Transparency.IsByLayer ? Inh.LayerOf(effLayer, effSym) : Inh.Explicit(p.Transparency);

            bool keepByLayer = !single;   // SINGLE_LAYER: everything explicit
            bool ne = false;
            r.Color = (Color)Materialize(color, target, targetIsSymbolicZero, keepByLayer, 0, ref ne);
            r.Linetype = (ObjectId)Materialize(lt, target, targetIsSymbolicZero, keepByLayer, 1, ref ne);
            r.LineWeight = (LineWeight)Materialize(lw, target, targetIsSymbolicZero, keepByLayer, 2, ref ne);
            r.Transparency = (Transparency)Materialize(tr, target, targetIsSymbolicZero, keepByLayer, 3, ref ne);
            r.NotExpressible = r.NotExpressible || ne;
            return r;
        }

        // which: 0 colour, 1 linetype, 2 lineweight, 3 transparency
        object Materialize(Inh v, ObjectId target, bool targetSymZero, bool keepByLayer, int which, ref bool notExpressible)
        {
            switch (v.Kind)
            {
                case InhKind.Explicit:
                    return v.Value;
                case InhKind.ByBlock:
                    return ByBlockValue(which);
                default: // LayerOf
                    if (v.LayerSymbolic)
                    {
                        // "the layer of the new reference": only expressible as ByLayer on layer 0
                        if (targetSymZero && keepByLayer) return ByLayerValue(which);
                        notExpressible = true;
                        return ByLayerValue(which);
                    }
                    if (keepByLayer && v.Layer == target) return ByLayerValue(which);
                    LayerInfo li = Layer(v.Layer);
                    if (li == null) return ByLayerValue(which);
                    switch (which)
                    {
                        case 0: return li.Color;
                        case 1: return li.Linetype;
                        case 2: return li.LineWeight;
                        default: return li.Transparency;
                    }
            }
        }

        object ByBlockValue(int which)
        {
            switch (which)
            {
                case 0: return Color.FromColorIndex(ColorMethod.ByBlock, 0);
                case 1: return _ltByBlock;
                case 2: return LineWeight.ByBlock;
                default: return new Transparency(TransparencyMethod.ByBlock);
            }
        }

        object ByLayerValue(int which)
        {
            switch (which)
            {
                case 0: return Color.FromColorIndex(ColorMethod.ByLayer, 256);
                case 1: return _ltByLayer;
                case 2: return LineWeight.ByLayer;
                default: return new Transparency(TransparencyMethod.ByLayer);
            }
        }

        public static void Apply(Entity e, ResolvedProps r)
        {
            e.LayerId = r.Layer;
            e.Color = r.Color;
            e.LinetypeId = r.Linetype;
            e.LineWeight = r.LineWeight;
            e.Transparency = r.Transparency;
        }
    }
}
