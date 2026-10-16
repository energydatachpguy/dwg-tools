// DwgFlatPublisher -- placing one entity at its final world position.
//
// The primary path is Entity.GetTransformedCopy(M) with the fully composed
// parent-to-child matrix -- AutoCAD's own implementation, exact for every
// entity under rotation, translation, uniform scale and mirroring.
//
// A NON-UNIFORM matrix (block inserted with X/Y/Z scales that differ) is
// where AutoCAD entities refuse (eCannotScaleNonUniformly) because the
// result is a different kind of geometry. Fallback ladder:
//   1. GetTransformedCopy anyway (lines, splines, polylines without bulges,
//      hatches, solids, images... accept non-uniform transforms natively)
//   2. DBText / attributes  -> exact: an affine image of single-line text is
//      still single-line text (rotation + height + width factor + oblique +
//      mirror), solved for directly
//   3. MText                -> exploded to its DBText fragments, each exact
//   4. Curves (circle, arc, ellipse...) -> converted to their exact NURBS
//      (Curve.Spline) which transforms exactly under any affine map
//   5. Anything else explodable (polylines with arcs/widths, regions...)
//      -> native Explode, then each piece through this same ladder
//   6. Otherwise the entity is reported as a failure -- never dropped silently.

using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcRtEx = Autodesk.AutoCAD.Runtime.Exception;

namespace DwgFlat
{
    public class EntityTransformer
    {
        readonly Report _rep;
        // Types learned (at run time) to reject non-uniform GetTransformedCopy,
        // so the exception is paid once per type, not once per entity.
        readonly HashSet<Type> _noNonUniform = new HashSet<Type>();

        public EntityTransformer(Report rep) { _rep = rep; }

        public static bool IsIdentity(Matrix3d m) { return m.IsEqualTo(Matrix3d.Identity, Tolerance.Global); }

        public static bool IsUniform(Matrix3d m) { return m.IsUniscaledOrtho(); }

        /// <summary>Scale of linetype patterns / in-plane size. For the usual
        /// plan-view case (rotations about Z, X = Y scale, any Z scale) this is
        /// the exact in-plane scale; otherwise the geometric mean.</summary>
        public static double ScaleOf(Matrix3d m)
        {
            double a, b, c, d;
            if (PlanarBlock(m, out a, out b, out c, out d))
            {
                double det2 = Math.Abs(a * d - b * c);
                if (det2 > 0) return Math.Sqrt(det2);
            }
            double det = Math.Abs(m.GetDeterminant());
            return det <= 0 ? 1.0 : Math.Pow(det, 1.0 / 3.0);
        }

        /// <summary>True when the linear part of m keeps the XY plane and the Z
        /// axis separate (only rotations about Z, mirrors in XY, any Z scale).
        /// Returns the 2x2 XY block.</summary>
        static bool PlanarBlock(Matrix3d m, out double a, out double b, out double c, out double d)
        {
            a = m[0, 0]; b = m[0, 1]; c = m[1, 0]; d = m[1, 1];
            const double eps = 1e-12;
            double scale = Math.Max(1.0, Math.Abs(a) + Math.Abs(b) + Math.Abs(c) + Math.Abs(d) + Math.Abs(m[2, 2]));
            return Math.Abs(m[0, 2]) < eps * scale && Math.Abs(m[1, 2]) < eps * scale
                && Math.Abs(m[2, 0]) < eps * scale && Math.Abs(m[2, 1]) < eps * scale;
        }

        /// <summary>A block inserted with X = Y but a different Z scale is NOT
        /// uniform to AutoCAD, yet for an entity lying flat in a plane z = z0
        /// it acts exactly like a uniform scale. Returns an equivalent uniform
        /// matrix for such an entity (same image of every point of the entity),
        /// or the input unchanged.</summary>
        static Matrix3d PlanarEquivalent(Entity e, Matrix3d m)
        {
            double a, b, c, d;
            if (!PlanarBlock(m, out a, out b, out c, out d)) return m;
            double sx = Math.Sqrt(a * a + c * c), sy = Math.Sqrt(b * b + d * d);
            if (sx <= 0 || Math.Abs(sx - sy) > 1e-9 * sx || Math.Abs(a * b + c * d) > 1e-9 * sx * sx) return m;   // not a similarity in XY
            double sz = m[2, 2];
            if (Math.Abs(sz) < 1e-300) return m;
            double z0;
            if (!FlatInXY(e, out z0)) return m;
            // re-scale Z about the entity's own plane so the Z scale equals the XY scale
            double k = sx / Math.Abs(sz);
            Matrix3d fix = Matrix3d.Displacement(new Vector3d(0, 0, z0))
                         * new Matrix3d(new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, k, 0, 0, 0, 0, 1 })
                         * Matrix3d.Displacement(new Vector3d(0, 0, -z0));
            Matrix3d r = m * fix;
            return r.IsUniscaledOrtho() ? r : m;
        }

        /// <summary>Entity lies in a plane z = z0 with normal along Z.</summary>
        static bool FlatInXY(Entity e, out double z0)
        {
            z0 = 0;
            try
            {
                // thickness extrudes along the normal: a different Z scale would change it
                double thick = 0;
                if (e is DBText) thick = ((DBText)e).Thickness;
                else if (e is Circle) thick = ((Circle)e).Thickness;
                else if (e is Arc) thick = ((Arc)e).Thickness;
                else if (e is Polyline) thick = ((Polyline)e).Thickness;
                else if (e is Line) thick = ((Line)e).Thickness;
                if (thick != 0) return false;
                Vector3d n;
                if (e is DBText) n = ((DBText)e).Normal;
                else if (e is MText) n = ((MText)e).Normal;
                else if (e is Hatch) n = ((Hatch)e).Normal;
                else if (e is Curve)
                {
                    Curve cv = (Curve)e;
                    if (!cv.IsPlanar) return false;
                    Plane pl = cv.GetPlane();
                    n = pl.Normal;
                }
                else return false;
                if (!n.IsParallelTo(Vector3d.ZAxis, Tolerance.Global)) return false;
                Extents3d x = e.GeometricExtents;
                if (Math.Abs(x.MaxPoint.Z - x.MinPoint.Z) > 1e-9 * Math.Max(1.0, x.MinPoint.DistanceTo(x.MaxPoint))) return false;
                z0 = x.MinPoint.Z;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Returns new non-database-resident entities (usually one)
        /// equivalent to 'src' transformed by m. Empty list = failure (already
        /// reported). 'where' is a breadcrumb for diagnostics.</summary>
        public List<Entity> Transform(Entity src, Matrix3d m, string where)
        {
            List<Entity> outList = new List<Entity>();
            string why;
            if (!TransformInto(src, m, outList, 0, out why))
                Fail(src, where, why);
            else if (_explodedNonCurve) _rep.Count("verify.nonCurveExplodedToCurves");
            _explodedNonCurve = false;
            return outList;
        }

        bool _explodedNonCurve;
        /// <summary>Count of non-curves (hatch, region...) turned into curves by
        /// the explode fallback. A verification sample during which this grew
        /// is not comparable curve-for-curve with the oracle and is skipped.</summary>
        public long NonCurveExplosions;

        /// <summary>Place src under m into outList. Never reports; on failure
        /// leaves outList exactly as it was and returns false with a reason.</summary>
        bool TransformInto(Entity src, Matrix3d m, List<Entity> outList, int depth, out string why)
        {
            why = "";
            bool uniform = IsUniform(m);
            if (!uniform)
            {
                Matrix3d eq = PlanarEquivalent(src, m);
                if (eq.IsUniscaledOrtho()) { m = eq; uniform = true; _rep.Count("planarZScaleNormalized"); }
            }
            Polyline plq = src as Polyline;
            bool polyWithArcs = false;
            if (!uniform && plq != null) { try { polyWithArcs = plq.HasBulges; } catch { polyWithArcs = true; } }
            if (uniform || (!NeedsSpecialNonUniform(src) && !polyWithArcs))
            {
                // (lightweight polylines are judged per entity: straight ones transform natively)
                if (uniform || plq != null || !_noNonUniform.Contains(src.GetType()))
                {
                    try
                    {
                        Entity c = src.GetTransformedCopy(m);
                        if (c != null) { outList.Add(c); return true; }
                        why = "GetTransformedCopy returned nothing";
                    }
                    catch (AcRtEx ex) { why = "GetTransformedCopy failed: " + ex.ErrorStatus; }
                    catch (System.Exception ex) { why = "GetTransformedCopy failed: " + ex.Message; }
                    if (uniform)
                    {
                        // last resort for uniform: explode and place the pieces
                        if (depth < 3 && ExplodeInto(src, m, outList, depth)) { _rep.Count("uniform.explodedThenTransformed"); return true; }
                        return false;
                    }
                    if (plq == null) _noNonUniform.Add(src.GetType());
                }
            }

            // ---- non-uniform fallbacks ------------------------------------------
            if (depth == 0) _rep.Count("nonUniform.entities");
            DBText txt = src as DBText;   // includes converted attributes
            if (txt != null)
            {
                Entity t = AffineText(txt, m);
                if (t != null) { outList.Add(t); _rep.Count("nonUniform.textExact"); return true; }
            }

            MText mt = src as MText;
            if (mt != null && depth < 3)
            {
                if (ExplodeInto(src, m, outList, depth)) { _rep.Count("nonUniform.mtextExplodedToText"); return true; }
            }

            Curve cu = src as Curve;
            if (cu != null && !(src is Polyline) && !(src is Polyline2d) && !(src is Polyline3d) && !(src is Line))
            {
                Spline sp = null;
                try
                {
                    sp = cu.Spline;
                    if (sp != null)
                    {
                        sp.SetPropertiesFrom(src);
                        sp.TransformBy(m);
                        outList.Add(sp);
                        _rep.Count("nonUniform.curveToSpline");
                        return true;
                    }
                }
                catch (System.Exception) { if (sp != null) { try { sp.Dispose(); } catch { } } }
            }

            Polyline pl = src as Polyline;
            bool widthLost = false;
            if (pl != null) { try { widthLost = pl.ConstantWidth != 0.0 || HasSegmentWidths(pl); } catch { } }

            if (mt == null && depth < 3 && ExplodeInto(src, m, outList, depth))
            {
                _rep.Count("nonUniform.explodedThenTransformed");
                if (widthLost)
                    _rep.Issue(Severity.Warning, "NONUNIFORM_POLYLINE_WIDTH",
                        "Polyline with width under a non-uniform scale was converted to centerline geometry (width is not representable).");
                return true;
            }

            why = "cannot be represented under a non-uniform block scale";
            return false;
        }

        static bool HasSegmentWidths(Polyline pl)
        {
            for (int i = 0; i < pl.NumberOfVertices; i++)
                if (pl.GetStartWidthAt(i) != 0.0 || pl.GetEndWidthAt(i) != 0.0) return true;
            return false;
        }

        static bool NeedsSpecialNonUniform(Entity e)
        {
            // Known to refuse or to be wrong under non-uniform scale -- go straight to the fallbacks.
            return e is DBText || e is MText || e is Circle || e is Arc || e is Ellipse;
        }

        /// <summary>All-or-nothing: on any failed piece, whatever was added is
        /// removed and disposed, so nothing is emitted twice.</summary>
        bool ExplodeInto(Entity src, Matrix3d m, List<Entity> outList, int depth)
        {
            DBObjectCollection pieces = new DBObjectCollection();
            try { src.Explode(pieces); }
            catch (System.Exception) { DisposeAll(pieces); return false; }
            if (pieces.Count == 0) return false;
            int before = outList.Count;
            bool allOk = true;
            foreach (DBObject o in pieces)
            {
                Entity pe = o as Entity;
                if (pe == null || !allOk) { o.Dispose(); continue; }
                string why;
                if (!TransformInto(pe, m, outList, depth + 1, out why)) allOk = false;
                pe.Dispose();
            }
            if (!allOk || outList.Count == before)
            {
                for (int i = outList.Count - 1; i >= before; i--) { try { outList[i].Dispose(); } catch { } }
                outList.RemoveRange(before, outList.Count - before);
                return false;
            }
            // a hatch / region / solid turned into curves: the curve fingerprint of
            // whatever contains it is no longer comparable with the oracle's
            if (!(src is Curve) && !(src is DBText) && !(src is MText)) { _explodedNonCurve = true; NonCurveExplosions++; }
            return true;
        }

        static void DisposeAll(DBObjectCollection c)
        {
            foreach (DBObject o in c) { try { o.Dispose(); } catch { } }
        }

        void Fail(Entity src, string where, string why)
        {
            _rep.Count("failed.entities");
            _rep.Issue(Severity.Error, "TRANSFORM_FAILED",
                src.GetType().Name + " (" + SafeDxf(src) + "): " + why, where);
        }

        public static string SafeDxf(Entity e)
        {
            try { return e.GetRXClass().DxfName; } catch { return "?"; }
        }

        // ---- exact affine transform of single-line text ---------------------------

        /// <summary>AutoCAD's arbitrary-axis algorithm: OCS X axis for a normal.</summary>
        public static Vector3d OcsX(Vector3d n)
        {
            n = n.GetNormal();
            Vector3d ax = (Math.Abs(n.X) < 1.0 / 64.0 && Math.Abs(n.Y) < 1.0 / 64.0)
                ? Vector3d.YAxis.CrossProduct(n) : Vector3d.ZAxis.CrossProduct(n);
            return ax.GetNormal();
        }

        /// <summary>Glyph space (x along the baseline in units of height*widthFactor,
        /// y up in units of height, shear by oblique, then mirror) is mapped to
        /// world by two vectors c1, c2. Transform them by M and solve the text
        /// parameters back out. Exact for any affine M.</summary>
        public static DBText AffineText(DBText t, Matrix3d m)
        {
            try
            {
                Vector3d n = t.Normal.GetNormal();
                Vector3d u = OcsX(n).RotateBy(t.Rotation, n);
                Vector3d v = n.CrossProduct(u);
                Vector3d gx = t.IsMirroredInX ? u.Negate() : u;
                Vector3d gy = t.IsMirroredInY ? v.Negate() : v;
                double h = t.Height, wf = t.WidthFactor;
                Vector3d c1 = gx * (h * wf);
                Vector3d c2 = gy * h + gx * (h * Math.Tan(t.Oblique));

                Vector3d c1p = c1.TransformBy(m), c2p = c2.TransformBy(m);
                // transformed plane normal (direction of M^-T n), mirrored when det < 0
                Vector3d nRef = u.TransformBy(m).CrossProduct(v.TransformBy(m));
                if (m.GetDeterminant() < 0) nRef = nRef.Negate();
                if (nRef.Length < 1e-12 || c1p.Length < 1e-12) return null;
                nRef = nRef.GetNormal();

                Vector3d e1 = c1p.GetNormal();
                double along = c2p.DotProduct(e1);
                Vector3d perp = c2p - e1 * along;
                double hNew = perp.Length;
                if (hNew < 1e-12) return null;
                Vector3d vNew = perp / hNew;
                double tanOb = along / hNew;
                double wfNew = c1p.Length / hNew;

                Vector3d n0 = e1.CrossProduct(vNew);
                bool mirror; Vector3d normal, uNew;
                if (n0.DotProduct(nRef) >= 0) { mirror = false; normal = n0; uNew = e1; }
                else { mirror = true; normal = n0.Negate(); uNew = e1.Negate(); }
                double rot = OcsX(normal).GetAngleTo(uNew, normal);

                double ob = Math.Atan(tanOb);
                double lim = 85.0 * Math.PI / 180.0;
                if (ob > lim) ob = lim; else if (ob < -lim) ob = -lim;
                if (wfNew < 0.01) wfNew = 0.01; else if (wfNew > 100) wfNew = 100;

                DBText r = new DBText();
                r.SetPropertiesFrom(t);
                r.TextStyleId = t.TextStyleId;
                r.TextString = t.TextString;
                r.Normal = normal;
                r.Height = hNew;
                r.WidthFactor = wfNew;
                r.Oblique = ob;
                r.Rotation = rot;
                r.IsMirroredInX = mirror;
                r.IsMirroredInY = false;
                r.Thickness = t.Thickness * Math.Abs(nRef.DotProduct(n.TransformBy(m)));
                r.HorizontalMode = t.HorizontalMode;
                r.VerticalMode = t.VerticalMode;
                r.Position = t.Position.TransformBy(m);
                if (!(t.HorizontalMode == TextHorizontalMode.TextLeft && t.VerticalMode == TextVerticalMode.TextBase))
                    r.AlignmentPoint = t.AlignmentPoint.TransformBy(m);
                return r;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        // ---- attribute -> plain text ----------------------------------------------

        /// <summary>The visible text of an attribute reference as a standalone
        /// DBText / MText carrying the attribute's own properties (value as
        /// shown, not the definition's default).</summary>
        public static Entity TextFromAttribute(AttributeReference ar)
        {
            if (ar.IsMTextAttribute)
            {
                MText mt = ar.MTextAttribute;   // returns a copy
                if (mt != null) { mt.SetPropertiesFrom(ar); return mt; }
            }
            return CopyText(ar, ar.TextString);
        }

        public static Entity TextFromConstantAttDef(AttributeDefinition ad)
        {
            if (ad.IsMTextAttributeDefinition)
            {
                MText mt = ad.MTextAttributeDefinition;
                if (mt != null) { mt.SetPropertiesFrom(ad); return mt; }
            }
            return CopyText(ad, ad.TextString);
        }

        static DBText CopyText(DBText src, string text)
        {
            DBText t = new DBText();
            t.SetPropertiesFrom(src);
            t.TextStyleId = src.TextStyleId;
            t.TextString = text;
            t.Normal = src.Normal;
            t.Height = src.Height;
            t.WidthFactor = src.WidthFactor;
            t.Oblique = src.Oblique;
            t.Rotation = src.Rotation;
            t.Thickness = src.Thickness;
            t.IsMirroredInX = src.IsMirroredInX;
            t.IsMirroredInY = src.IsMirroredInY;
            t.HorizontalMode = src.HorizontalMode;
            t.VerticalMode = src.VerticalMode;
            t.Position = src.Position;
            if (!(src.HorizontalMode == TextHorizontalMode.TextLeft && src.VerticalMode == TextVerticalMode.TextBase))
                t.AlignmentPoint = src.AlignmentPoint;
            return t;
        }
    }

    /// <summary>Independent geometric fingerprint of a set of curves: total
    /// length and the length-weighted first moment (integral of p ds). Both
    /// are invariant to how a curve is split into pieces or which entity type
    /// represents it, so "circle under non-uniform scale" (ellipse) and
    /// "same circle as NURBS" (spline) fingerprint identically.</summary>
    public class CurveMetrics
    {
        public double Length;
        public Vector3d Moment = new Vector3d();
        public long Curves;
        public long Unmeasured;
        const int Samples = 48;

        public void Add(Entity e)
        {
            Curve c = e as Curve;
            if (c == null) return;
            try
            {
                Line ln = c as Line;
                if (ln != null) { AddSegment(ln.StartPoint, ln.EndPoint); Curves++; return; }
                Polyline pl = c as Polyline;
                if (pl != null)
                {
                    int segs = pl.Closed ? pl.NumberOfVertices : pl.NumberOfVertices - 1;
                    for (int i = 0; i < segs; i++)
                    {
                        SegmentType st = pl.GetSegmentType(i);
                        if (st == SegmentType.Line) { LineSegment3d s = pl.GetLineSegmentAt(i); AddSegment(s.StartPoint, s.EndPoint); }
                        else if (st == SegmentType.Arc) AddGe(pl.GetArcSegmentAt(i));
                    }
                    Curves++;
                    return;
                }
                if (c is Polyline2d || c is Polyline3d)
                {
                    DBObjectCollection parts = new DBObjectCollection();
                    c.Explode(parts);
                    foreach (DBObject o in parts) { Entity pe = o as Entity; if (pe != null) Add(pe); o.Dispose(); }
                    return;
                }
                double len = c.GetDistanceAtParameter(c.EndParam) - c.GetDistanceAtParameter(c.StartParam);
                if (len <= 0) { Curves++; return; }
                for (int i = 0; i < Samples; i++)
                {
                    Point3d p = c.GetPointAtDist(len * (i + 0.5) / Samples);
                    Moment += p.GetAsVector() * (len / Samples);
                }
                Length += len;
                Curves++;
            }
            catch (System.Exception) { Unmeasured++; }
        }

        void AddSegment(Point3d a, Point3d b)
        {
            double len = a.DistanceTo(b);
            Length += len;
            Moment += ((a.GetAsVector() + b.GetAsVector()) * 0.5) * len;
        }

        void AddGe(Curve3d g)
        {
            Interval iv = g.GetInterval();
            double len = g.GetLength(iv.LowerBound, iv.UpperBound, 1e-10);
            if (len <= 0) return;
            for (int i = 0; i < Samples; i++)
            {
                double prm = g.GetParameterAtLength(iv.LowerBound, len * (i + 0.5) / Samples, true, 1e-10);
                Point3d p = g.EvaluatePoint(prm);
                Moment += p.GetAsVector() * (len / Samples);
            }
            Length += len;
        }

        public void Merge(CurveMetrics o)
        {
            Length += o.Length; Moment += o.Moment; Curves += o.Curves; Unmeasured += o.Unmeasured;
        }

        /// <summary>Relative deviation between two fingerprints (0 = identical).</summary>
        public static double Deviation(CurveMetrics a, CurveMetrics b, double scaleRef)
        {
            double s = Math.Max(1e-9, Math.Max(a.Length, b.Length));
            double dl = Math.Abs(a.Length - b.Length) / s;
            double dm = (a.Moment - b.Moment).Length / Math.Max(1e-9, s * Math.Max(1.0, scaleRef));
            return Math.Max(dl, dm);
        }
    }
}
