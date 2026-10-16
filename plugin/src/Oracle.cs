// DwgFlatPublisher -- independent geometry oracle for verification.
//
// For a sample of model-space references, measure what the SOURCE displays
// using a deliberately different method from the flattener: AutoCAD's own
// BlockReference.Explode applied level by level (AutoCAD composes the
// transforms itself). The flattener instead composes matrices once and
// copies from memoised prototypes. The validator later fingerprints the
// SAVED output for the same references; if both agree, positions,
// orientations, scales and mirroring through every nesting level are right.
//
// Only curves are fingerprinted (lines, arcs, circles, polylines, ellipses,
// splines -- including those from exploded dimensions/leaders): their
// total length and length-weighted centroid catch any misplacement.

using System;
using Autodesk.AutoCAD.DatabaseServices;

namespace DwgFlat
{
    public static class Oracle
    {
        /// <summary>null when AutoCAD's explode refuses somewhere (e.g. a
        /// non-uniformly scaled reference holding rotated sub-blocks) -- then
        /// that sample simply isn't used.</summary>
        public static CurveMetrics Measure(BlockReference br, Transaction tr, Appearance app, PublishOptions opt, Report rep)
        {
            CurveMetrics m = new CurveMetrics();
            try
            {
                LayerInfo li = app.Layer(br.LayerId);
                if (!br.Visible) return m;
                // FULL_FLAT omits a frozen top-level reference (nothing would show);
                // OPTIMIZED keeps it on its frozen layer, so measure it anyway
                if (li != null && li.Frozen && opt.Mode == PublishMode.FullFlat) return m;
                // OPTIMIZED + SINGLE_LAYER drops a reference whose own layer is frozen
                // (off / no-plot only hide its layer-0 children, handled per piece)
                if (li != null && li.Frozen && opt.Mode == PublishMode.Optimized && opt.Layers == LayerPolicy.SingleLayer
                    && opt.OmitHiddenInSingleLayer) return m;
                // OPTIMIZED: the replacement reference stays on the original's layer, so
                // content that INHERITS that layer (through layer 0) is not hidden inside
                // the leaf by it being frozen -- same rule as Ctx.IgnoreFrozenLayer.
                ObjectId ignoreFrozen = opt.Mode == PublishMode.Optimized ? br.LayerId : ObjectId.Null;
                Walk(br, br.LayerId, ignoreFrozen, tr, app, opt, m, 0);
                rep.Count("verify.oracleSamples");
                return m;
            }
            catch (System.Exception)
            {
                rep.Count("verify.oracleUnavailable");
                return null;
            }
        }

        // 'eff' = effective layer of the reference being exploded (for layer-0 pieces)
        static void Walk(Entity container, ObjectId eff, ObjectId ignoreFrozen, Transaction tr, Appearance app, PublishOptions opt, CurveMetrics m, int depth)
        {
            if (depth > 64) throw new InvalidOperationException("too deep");
            DBObjectCollection pieces = new DBObjectCollection();
            container.Explode(pieces);   // throws on refusal -> sample discarded
            try
            {
                foreach (DBObject o in pieces)
                {
                    Entity e = o as Entity;
                    if (e == null || !e.Visible) continue;
                    ObjectId pieceLayer = e.LayerId == app.LayerZero ? eff : e.LayerId;
                    if (e is AttributeDefinition) continue;
                    if (e is BlockReference)
                    {
                        LayerInfo li = app.Layer(pieceLayer);
                        bool inheritedTop = e.LayerId == app.LayerZero && pieceLayer == ignoreFrozen;
                        if (li != null && li.Frozen && !inheritedTop) continue;   // frozen reference hides its subtree
                        Walk(e, pieceLayer, ignoreFrozen, tr, app, opt, m, depth + 1);
                        continue;
                    }
                    if (DefinitionCache.IsAnnotationToExplode(e) || (DefinitionCache.IsCustom(e) && opt.ExplodeCustomObjects))
                    {
                        // an exploded object behaves like a reference: a frozen layer hides all of it
                        LayerInfo ali = app.Layer(pieceLayer);
                        bool inheritedTop2 = e.LayerId == app.LayerZero && pieceLayer == ignoreFrozen;
                        if (ali != null && ali.Frozen && !inheritedTop2) continue;
                        try { Walk(e, pieceLayer, ignoreFrozen, tr, app, opt, m, depth + 1); continue; }
                        catch (System.Exception) { if (!DefinitionCache.IsCustom(e)) throw; }
                    }
                    if (opt.Layers == LayerPolicy.SingleLayer && opt.OmitHiddenInSingleLayer)
                    {
                        LayerInfo li = app.Layer(pieceLayer);
                        if (li != null && (li.Frozen || li.Off || (!li.Plottable && !opt.KeepNoPlot))) continue;
                    }
                    if (DefinitionCache.IsNonDwgDependency(e) && opt.NonDwgDependencies == DependencyPolicy.Exclude) continue;
                    m.Add(e);
                }
            }
            finally
            {
                foreach (DBObject o in pieces) { try { o.Dispose(); } catch { } }
            }
        }
    }
}
