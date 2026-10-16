// DwgFlatPublisher -- the WBLOCK helper, driven from the Reference Tree.
//
//   find   : every occurrence of a block (at any nesting level) in the drawing
//            open in AutoCAD; light them up (AutoCAD highlight, no extra graphics)
//            and zoom to them.
//   pick   : the user clicks the one they want in AutoCAD.
//   wblock : copy THAT occurrence and everything inside it into a new drawing,
//            explode it all to plain geometry on layer 0 (colors kept), keep
//            master coordinates (base point 0,0,0 = master 0,0,0, so inserting
//            the file at 0,0 puts it back exactly where it was), save it as a
//            NEW file and check it against AutoCAD's own Explode.
//
// The drawing in AutoCAD is only read: nothing in it is changed or saved.
// An existing file is never overwritten (SafeFs).

using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;

namespace DwgFlat
{
    public class Occurrence
    {
        public List<ObjectId> Path = new List<ObjectId>();   // top-level reference ... the block itself
        public Matrix3d Parent = Matrix3d.Identity;          // owner coordinates -> WCS
        public Matrix3d Full = Matrix3d.Identity;            // block coordinates -> WCS
        public Extents3d Box;                                // what you can SEE (frozen/off/invisible left out)
        public bool HasBox;
        public Extents3d FullBox;                            // AutoCAD's own extents (counts hidden things too)
        public bool HasFullBox;
        public long Visible, Hidden, Attdefs;
        public bool Partial;                                 // too big to walk completely
        public Dictionary<string, long> HiddenLayers = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public Extents3d AllVisible; public bool HasAllVisible;  // every visible object, strays included
        public long Strays;                                  // visible objects far from the main cluster
        public List<object> StrayList = new List<object>();  // [type, layer, count, x, y]
        public string Layer = "0";
        public List<string> Inside = new List<string>();
    }

    /// <summary>One layer of the block-level layer standard (from the Excel file).</summary>
    public class StdLayer
    {
        public string Name, Linetype = "Continuous", Lineweight = "ByLineWeightDefault";
        public bool Plot = true;
        public int Aci, Transparency;

        /// <summary>name|plot|aci|linetype|lineweight|transparency per layer (U+0001 / U+0002).</summary>
        public static List<StdLayer> Parse(string s)
        {
            List<StdLayer> list = new List<StdLayer>();
            foreach (string row in (s ?? "").Split('\u0002'))
            {
                string[] f = row.Split('\u0001');
                if (f.Length < 6 || f[0].Trim().Length == 0) continue;
                StdLayer l = new StdLayer();
                l.Name = f[0].Trim(); l.Plot = f[1] == "1";
                int.TryParse(f[2], out l.Aci); l.Linetype = f[3].Trim().Length > 0 ? f[3].Trim() : "Continuous";
                l.Lineweight = f[4].Trim(); int.TryParse(f[5], out l.Transparency);
                list.Add(l);
            }
            return list;
        }
    }

    public static class WblockTool
    {
        // ---------------------------------------------------------------- find
        public static string EffName(BlockReference br, Transaction tr)
        {
            try
            {
                if (br.IsDynamicBlock)
                {
                    BlockTableRecord d = tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                    if (d != null) return d.Name;
                }
            }
            catch { }
            return DefinitionCache.SafeName(br);
        }

        public static List<Occurrence> Find(Database db, Transaction tr, List<string> names, int cap, out bool truncated)
        {
            return Find(db, tr, names, null, cap, out truncated);
        }

        static bool Allowed(List<HashSet<string>> allow, int level, ObjectId id)
        {
            if (allow == null || level >= allow.Count || allow[level] == null || allow[level].Count == 0) return true;
            return allow[level].Contains(id.Handle.ToString());
        }

        /// <summary>allow[level] = the handles ticked at that level (empty = every one).</summary>
        public static List<Occurrence> Find(Database db, Transaction tr, List<string> names, List<HashSet<string>> allow, int cap, out bool truncated)
        {
            truncated = false;
            List<Occurrence> level = new List<Occurrence>();
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                if (br == null || br is MInsertBlock || IsXref(br, tr)) continue;
                if (!Same(EffName(br, tr), names[0])) continue;
                if (!Allowed(allow, 0, id)) continue;
                Occurrence o = new Occurrence();
                o.Path.Add(id);
                o.Full = br.BlockTransform;
                o.Layer = br.Layer;
                o.Inside.Add("Model space");
                level.Add(o);
            }
            for (int i = 1; i < names.Count; i++)
            {
                List<Occurrence> next = new List<Occurrence>();
                foreach (Occurrence p in level)
                {
                    BlockReference last = (BlockReference)tr.GetObject(p.Path[p.Path.Count - 1], OpenMode.ForRead);
                    BlockTableRecord def = (BlockTableRecord)tr.GetObject(last.BlockTableRecord, OpenMode.ForRead);
                    foreach (ObjectId cid in def)
                    {
                        BlockReference nb = tr.GetObject(cid, OpenMode.ForRead) as BlockReference;
                        if (nb == null || nb is MInsertBlock || IsXref(nb, tr)) continue;
                        if (!Same(EffName(nb, tr), names[i])) continue;
                        if (!Allowed(allow, i, cid)) continue;
                        Occurrence o = new Occurrence();
                        o.Path.AddRange(p.Path);
                        o.Path.Add(cid);
                        o.Parent = p.Full;
                        o.Full = p.Full * nb.BlockTransform;
                        o.Layer = nb.Layer == "0" ? p.Layer : nb.Layer;
                        o.Inside.AddRange(p.Inside);
                        o.Inside.Add(EffName(last, tr));
                        next.Add(o);
                        if (next.Count >= cap) { truncated = true; break; }
                    }
                    if (truncated) break;
                }
                level = next;
            }
            if (level.Count > cap) { level.RemoveRange(cap, level.Count - cap); truncated = true; }
            Dictionary<string, bool> layerHidden = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            long budget = 2000000;   // entities walked across all occurrences in one request
            foreach (Occurrence o in level)
            {
                BlockReference t = null;
                try
                {
                    t = (BlockReference)tr.GetObject(o.Path[o.Path.Count - 1], OpenMode.ForRead);
                    o.FullBox = TransformBox(t.GeometricExtents, o.Parent);
                    o.HasFullBox = true;
                }
                catch { }
                if (t == null) continue;
                try
                {
                    BoxWalk w = new BoxWalk();
                    w.Budget = Math.Min(300000, Math.Max(0, budget));
                    w.LayerHidden = layerHidden;
                    w.Db = db; w.Tr = tr;
                    bool selfHidden = !t.Visible || IsLayerHidden(w, o.Layer);
                    WalkRef(w, t, o.Parent, o.Layer, selfHidden, 0);
                    budget -= 300000 - w.Budget;
                    o.Visible = w.Visible; o.Hidden = w.Hidden; o.Attdefs = w.Attdefs; o.HiddenLayers = w.HiddenLayers;
                    o.Partial = w.Budget <= 0;
                    if (w.HaveVis && !o.Partial) Core(w, o);
                }
                catch { }
                if (!o.HasBox && o.HasFullBox) { o.Box = o.FullBox; o.HasBox = true; }
            }
            return level;
        }

        // ---------------------------------------------------------------- what is actually visible
        class BoxWalk
        {
            public Database Db; public Transaction Tr;
            public Extents3d Vis; public bool HaveVis;
            public long Visible, Hidden, Attdefs, Budget;
            public Dictionary<string, long> HiddenLayers = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, bool> LayerHidden;
            public List<Extents3d> Boxes = new List<Extents3d>();
            public List<string> Kinds = new List<string>();      // "type|layer"
            public List<Point3d> Points;                          // real points (line ends, vertices, centers), when asked
        }

        /// <summary>Points that exist in the geometry itself and survive an explode unchanged.</summary>
        static void AddPoints(BoxWalk w, Entity e, Matrix3d xf)
        {
            if (w.Points == null || w.Points.Count >= 60000) return;
            Line l = e as Line;
            if (l != null) { w.Points.Add(l.StartPoint.TransformBy(xf)); w.Points.Add(l.EndPoint.TransformBy(xf)); return; }
            Autodesk.AutoCAD.DatabaseServices.Polyline pl = e as Autodesk.AutoCAD.DatabaseServices.Polyline;
            if (pl != null) { for (int i = 0; i < pl.NumberOfVertices && i < 50; i++) w.Points.Add(pl.GetPoint3dAt(i).TransformBy(xf)); return; }
            Circle c = e as Circle;
            if (c != null) { w.Points.Add(c.Center.TransformBy(xf)); return; }
            DBPoint pt = e as DBPoint;
            if (pt != null) w.Points.Add(pt.Position.TransformBy(xf));
        }

        /// <summary>The box to zoom to: the main cluster of what is visible. A few
        /// objects far away from everything else (a stray line pasted in with the
        /// block, say) would otherwise make the zoom useless; they are reported.</summary>
        static void Core(BoxWalk w, Occurrence o)
        {
            o.AllVisible = w.Vis; o.HasAllVisible = w.HaveVis;
            int n = w.Boxes.Count;
            o.Box = w.Vis; o.HasBox = w.HaveVis;
            if (n < 5) return;
            double[] xs = new double[n], ys = new double[n];
            for (int i = 0; i < n; i++)
            {
                xs[i] = (w.Boxes[i].MinPoint.X + w.Boxes[i].MaxPoint.X) / 2;
                ys[i] = (w.Boxes[i].MinPoint.Y + w.Boxes[i].MaxPoint.Y) / 2;
            }
            double[] sx = (double[])xs.Clone(), sy = (double[])ys.Clone();
            Array.Sort(sx); Array.Sort(sy);
            int lo = (int)Math.Floor(n * 0.02), hi = Math.Min(n - 1, (int)Math.Ceiling(n * 0.98) - 1);
            double x0 = sx[lo], x1 = sx[hi], y0 = sy[lo], y1 = sy[hi];
            double span = Math.Max(Math.Max(x1 - x0, y1 - y0), 1e-6);
            x0 -= span; x1 += span; y0 -= span; y1 += span;          // generous: only true strays fall outside
            Extents3d core = new Extents3d(); bool haveCore = false;
            Dictionary<string, long> strayKinds = new Dictionary<string, long>();
            Dictionary<string, Point2d> strayAt = new Dictionary<string, Point2d>();
            long strays = 0;
            for (int i = 0; i < n; i++)
            {
                if (xs[i] >= x0 && xs[i] <= x1 && ys[i] >= y0 && ys[i] <= y1)
                {
                    if (!haveCore) { core = w.Boxes[i]; haveCore = true; } else core.AddExtents(w.Boxes[i]);
                }
                else
                {
                    strays++;
                    long c; strayKinds.TryGetValue(w.Kinds[i], out c); strayKinds[w.Kinds[i]] = c + 1;
                    if (!strayAt.ContainsKey(w.Kinds[i])) strayAt[w.Kinds[i]] = new Point2d(xs[i], ys[i]);
                }
            }
            if (!haveCore || strays == 0 || strays > n / 10) return;   // no clear main cluster: zoom to everything
            o.Box = core; o.HasBox = true; o.Strays = strays;
            List<KeyValuePair<string, long>> kinds = new List<KeyValuePair<string, long>>(strayKinds);
            kinds.Sort(delegate (KeyValuePair<string, long> a, KeyValuePair<string, long> b) { return b.Value.CompareTo(a.Value); });
            for (int i = 0; i < kinds.Count && i < 6; i++)
            {
                string[] tl = kinds[i].Key.Split('|');
                Point2d at = strayAt[kinds[i].Key];
                o.StrayList.Add(new object[] { tl[0], tl.Length > 1 ? tl[1] : "", kinds[i].Value, Math.Round(at.X, 2), Math.Round(at.Y, 2) });
            }
        }

        static bool IsLayerHidden(BoxWalk w, string name)
        {
            bool h;
            if (w.LayerHidden.TryGetValue(name, out h)) return h;
            h = false;
            try
            {
                LayerTable lt = (LayerTable)w.Tr.GetObject(w.Db.LayerTableId, OpenMode.ForRead);
                if (lt.Has(name))
                {
                    LayerTableRecord l = (LayerTableRecord)w.Tr.GetObject(lt[name], OpenMode.ForRead);
                    h = l.IsFrozen || l.IsOff;
                }
            }
            catch { }
            w.LayerHidden[name] = h;
            return h;
        }

        /// <summary>Walk one block reference: its definition's entities (through
        /// nested blocks) and its attributes. An entity on layer 0 inside a block
        /// takes the layer of the reference that holds it.</summary>
        static void WalkRef(BoxWalk w, BlockReference br, Matrix3d owner, string refLayer, bool hidden, int depth)
        {
            Matrix3d xf = owner * br.BlockTransform;
            foreach (ObjectId aid in br.AttributeCollection)
            {
                AttributeReference a = w.Tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                if (a == null) continue;
                bool ah = hidden || a.Invisible || !a.Visible || IsLayerHidden(w, a.Layer == "0" ? refLayer : a.Layer);
                Count(w, a, owner, ah, a.Layer == "0" ? refLayer : a.Layer);
            }
            if (depth > 16) return;
            BlockTableRecord def = w.Tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
            if (def == null || def.IsFromExternalReference) return;
            foreach (ObjectId id in def)
            {
                if (--w.Budget <= 0) return;
                Entity e = w.Tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (e == null) continue;
                string lay = e.Layer == "0" ? refLayer : e.Layer;
                bool h = hidden || !e.Visible || IsLayerHidden(w, lay);
                AttributeDefinition ad = e as AttributeDefinition;
                if (ad != null && !ad.Constant) { w.Attdefs++; continue; }   // never shown in an inserted block
                BlockReference nb = e as BlockReference;
                if (nb != null) { WalkRef(w, nb, xf, lay, h, depth + 1); continue; }
                Count(w, e, xf, h, lay);
            }
        }

        static void Count(BoxWalk w, Entity e, Matrix3d xf, bool hidden, string layer)
        {
            if (hidden)
            {
                w.Hidden++;
                long n; w.HiddenLayers.TryGetValue(layer, out n); w.HiddenLayers[layer] = n + 1;
                return;
            }
            Extents3d x;
            try { x = TrueBox(e, xf); } catch { return; }
            w.Visible++;
            if (!w.HaveVis) { w.Vis = x; w.HaveVis = true; } else w.Vis.AddExtents(x);
            w.Boxes.Add(x);
            w.Kinds.Add(e.GetType().Name + "|" + layer);
            AddPoints(w, e, xf);
        }

        static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        static bool IsXref(BlockReference br, Transaction tr)
        {
            BlockTableRecord d = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
            return d == null || d.IsFromExternalReference || d.IsDependent;
        }

        static Extents3d TransformBox(Extents3d e, Matrix3d m)
        {
            Extents3d r = new Extents3d();
            bool first = true;
            for (int i = 0; i < 8; i++)
            {
                Point3d p = new Point3d((i & 1) == 0 ? e.MinPoint.X : e.MaxPoint.X, (i & 2) == 0 ? e.MinPoint.Y : e.MaxPoint.Y, (i & 4) == 0 ? e.MinPoint.Z : e.MaxPoint.Z).TransformBy(m);
                if (first) { r = new Extents3d(p, p); first = false; } else r.AddPoint(p);
            }
            return r;
        }

        public static Dictionary<string, object> Describe(Occurrence o, int index, Transaction tr)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["index"] = index + 1;
            List<string> hs = new List<string>();
            foreach (ObjectId id in o.Path) hs.Add(id.Handle.ToString());
            d["handles"] = hs;
            Point3d ip = Point3d.Origin.TransformBy(o.Full);
            d["x"] = Math.Round(ip.X, 4); d["y"] = Math.Round(ip.Y, 4); d["z"] = Math.Round(ip.Z, 4);
            Vector3d xa = Vector3d.XAxis.TransformBy(o.Full), ya = Vector3d.YAxis.TransformBy(o.Full), za = Vector3d.ZAxis.TransformBy(o.Full);
            d["rotationDeg"] = Math.Round(Math.Atan2(xa.Y, xa.X) * 180.0 / Math.PI, 4);
            d["scale"] = Util.Fmt(Math.Round(xa.Length, 6)) + "," + Util.Fmt(Math.Round(ya.Length, 6)) + "," + Util.Fmt(Math.Round(za.Length, 6));
            d["mirrored"] = xa.CrossProduct(ya).DotProduct(za) < 0;
            d["layer"] = o.Layer;
            d["inside"] = string.Join(" > ", o.Inside.ToArray());
            if (o.HasBox) d["box"] = new double[] { o.Box.MinPoint.X, o.Box.MinPoint.Y, o.Box.MaxPoint.X, o.Box.MaxPoint.Y };
            if (o.HasFullBox) d["fullBox"] = new double[] { o.FullBox.MinPoint.X, o.FullBox.MinPoint.Y, o.FullBox.MaxPoint.X, o.FullBox.MaxPoint.Y };
            d["visibleObjects"] = o.Visible;
            d["hiddenObjects"] = o.Hidden;
            d["attributeDefinitions"] = o.Attdefs;
            d["partial"] = o.Partial;
            List<object> hl = new List<object>();
            List<KeyValuePair<string, long>> sorted = new List<KeyValuePair<string, long>>(o.HiddenLayers);
            sorted.Sort(delegate (KeyValuePair<string, long> a, KeyValuePair<string, long> b) { return b.Value.CompareTo(a.Value); });
            for (int i = 0; i < sorted.Count && i < 8; i++) hl.Add(new object[] { sorted[i].Key, sorted[i].Value });
            d["hiddenLayers"] = hl;
            if (o.HasAllVisible) d["allBox"] = new double[] { o.AllVisible.MinPoint.X, o.AllVisible.MinPoint.Y, o.AllVisible.MaxPoint.X, o.AllVisible.MaxPoint.Y };
            d["strays"] = o.Strays;
            d["strayList"] = o.StrayList;
            return d;
        }

        // ---------------------------------------------------------------- light up
        static readonly List<Drawable> _marks = new List<Drawable>();
        static Database _marksDb;
        static readonly List<KeyValuePair<ObjectId, FullSubentityPath>> _lit = new List<KeyValuePair<ObjectId, FullSubentityPath>>();

        public static void Clear(Database db)
        {
            if (_marks.Count > 0)
            {
                if (_marksDb != null && db != _marksDb) return;   // drawn in another drawing: clear them there (FLATCLEAR)
                TransientManager tm = TransientManager.CurrentTransientManager;
                foreach (Drawable d in _marks)
                {
                    bool erased = false;
                    try { erased = tm.EraseTransient(d, new IntegerCollection()); } catch { }
                    if (erased) { try { d.Dispose(); } catch { } }
                }
                _marks.Clear();
                _marksDb = null;
            }
            if (_lit.Count > 0 && db != null)
            {
                try
                {
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        foreach (KeyValuePair<ObjectId, FullSubentityPath> kv in _lit)
                        {
                            try
                            {
                                if (kv.Key.IsErased || kv.Key.Database != db) continue;
                                Entity e = (Entity)tr.GetObject(kv.Key, OpenMode.ForRead);
                                e.Unhighlight(kv.Value, false);
                            }
                            catch { }
                        }
                        tr.Commit();
                    }
                }
                catch { }
            }
            _lit.Clear();
        }

        /// <summary>Highlight each occurrence (and everything inside it), draw a
        /// zoom to them (or to one: focus >= 0). Nothing is drawn.</summary>
        // Esc in AutoCAD clears our highlight (AutoCAD itself only clears it on REGEN).
        // The key is only looked at, never swallowed: Esc still does what it always does.
        static bool _escHooked;
        static void HookEsc()
        {
            if (_escHooked) return;
            try { Autodesk.AutoCAD.ApplicationServices.Core.Application.PreTranslateMessage += OnKey; _escHooked = true; } catch { }
        }
        static void OnKey(object sender, Autodesk.AutoCAD.ApplicationServices.PreTranslateMessageEventArgs e)
        {
            if (_lit.Count == 0) return;
            const int WM_KEYDOWN = 0x0100, VK_ESCAPE = 0x1B;
            if (e.Message.message != WM_KEYDOWN || e.Message.wParam.ToInt64() != VK_ESCAPE) return;
            try
            {
                Autodesk.AutoCAD.ApplicationServices.Document doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                using (doc.LockDocument()) Clear(doc.Database);
                try { doc.Editor.UpdateScreen(); } catch { }
            }
            catch { }
        }

        public static void LightUp(Editor ed, Database db, Transaction tr, List<Occurrence> occ, int focus)
        {
            HookEsc();
            Clear(db);
            _marks.Clear();
            _marksDb = db;
            Extents3d all = new Extents3d(); bool haveAll = false;
            int drawn = 0;
            for (int i = 0; i < occ.Count; i++)
            {
                Occurrence o = occ[i];
                bool isFocus = focus < 0 || i == focus;
                if (drawn < 2000 && (focus < 0 || i == focus))
                {
                    try
                    {
                        Entity top = (Entity)tr.GetObject(o.Path[0], OpenMode.ForRead);
                        FullSubentityPath p = new FullSubentityPath(o.Path.ToArray(), new SubentityId(SubentityType.Null, IntPtr.Zero));
                        top.Highlight(p, false);
                        _lit.Add(new KeyValuePair<ObjectId, FullSubentityPath>(o.Path[0], p));
                    }
                    catch { }
                }
                // no boxes or labels are drawn: only AutoCAD's own highlight, and a zoom
                if (o.HasBox && isFocus)
                {
                    Extents3d b = o.Box;
                    double pad = Math.Max(1e-6, Math.Max(b.MaxPoint.X - b.MinPoint.X, b.MaxPoint.Y - b.MinPoint.Y)) * 0.05;
                    Extents3d padded = new Extents3d(new Point3d(b.MinPoint.X - pad, b.MinPoint.Y - pad, b.MinPoint.Z), new Point3d(b.MaxPoint.X + pad, b.MaxPoint.Y + pad, b.MaxPoint.Z));
                    if (!haveAll) { all = padded; haveAll = true; } else all.AddExtents(padded);
                }
                drawn++;
            }
            if (haveAll)
            {
                if (db.TileMode) ZoomTo(ed, all);
                else ed.WriteMessage("\nDWG Tools: switch to the Model tab to see the highlighted blocks.");
            }
            try { ed.UpdateScreen(); } catch { }
        }

        static void ZoomTo(Editor ed, Extents3d ext)
        {
            try
            {
                using (ViewTableRecord v = ed.GetCurrentView())
                {
                    Matrix3d w2d = (Matrix3d.Rotation(-v.ViewTwist, v.ViewDirection, v.Target)
                                  * Matrix3d.Displacement(v.Target - Point3d.Origin)
                                  * Matrix3d.PlaneToWorld(v.ViewDirection)).Inverse();
                    Extents3d e = TransformBox(ext, w2d);
                    double w = Math.Max(e.MaxPoint.X - e.MinPoint.X, 1e-6) * 1.15;
                    double h = Math.Max(e.MaxPoint.Y - e.MinPoint.Y, 1e-6) * 1.15;
                    double ratio = v.Width / Math.Max(v.Height, 1e-9);
                    if (w / h > ratio) h = w / ratio; else w = h * ratio;
                    v.CenterPoint = new Point2d((e.MinPoint.X + e.MaxPoint.X) / 2, (e.MinPoint.Y + e.MaxPoint.Y) / 2);
                    v.Width = w;
                    v.Height = h;
                    ed.SetCurrentView(v);
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------- pick
        /// <summary>Ask the user to click one occurrence in AutoCAD. Returns its index, or -1.</summary>
        public static int Pick(Editor ed, Database db, List<Occurrence> occ, string name)
        {
            for (int tries = 0; tries < 20; tries++)
            {
                PromptNestedEntityOptions po = new PromptNestedEntityOptions("\nClick the " + name + " you want (one of the highlighted ones), or Esc to cancel: ");
                po.AllowNone = false;
                PromptNestedEntityResult r = ed.GetNestedEntity(po);
                if (r.Status != PromptStatus.OK) return -1;
                ObjectId[] inner = r.GetContainers();      // innermost first
                List<ObjectId> chain = new List<ObjectId>();
                for (int i = inner.Length - 1; i >= 0; i--) chain.Add(inner[i]);
                if (r.ObjectId.ObjectClass.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(AttributeReference))))
                {
                    // an attribute: its owner is the block that carries it
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        DBObject a = tr.GetObject(r.ObjectId, OpenMode.ForRead);
                        if (chain.Count == 0 || chain[chain.Count - 1] != a.OwnerId) chain.Add(a.OwnerId);
                        tr.Commit();
                    }
                }
                chain.Add(r.ObjectId);
                for (int k = 0; k < occ.Count; k++)
                {
                    List<ObjectId> p = occ[k].Path;
                    if (p.Count > chain.Count) continue;
                    bool ok = true;
                    for (int i = 0; i < p.Count && ok; i++) ok = p[i] == chain[i];
                    if (ok) return k;
                }
                ed.WriteMessage("\nThat is not part of a " + name + ". Click one of the highlighted ones.");
            }
            return -1;
        }

        // ---------------------------------------------------------------- insert back
        public class InsertItem { public string File, Block, Layer; public double X, Y; public int Color = 72; }

        static string LegalName(string n)
        {
            string r = System.Text.RegularExpressions.Regex.Replace(n ?? "", "[<>/\\\\\":;?*|,=`]", "_").Trim();
            return r.Length == 0 ? "WBLOCK" : (r.Length > 255 ? r.Substring(0, 255) : r);
        }

        /// <summary>Insert WBLOCK files into the drawing open in CAD: each file becomes a
        /// block definition (named after the file; an existing one of that name is
        /// REDEFINED, so every copy updates), placed at 0,0 (+ offset), scale 1, rotation 0,
        /// on a layer named after the file. Optionally erase the originals the WBLOCK was
        /// made from (only top-level ones: a block nested inside another is part of that
        /// block's definition and is never touched). Nothing is saved.</summary>
        /// <summary>Before inserting: does the block / layer already exist, is the block
        /// the same as the original it was made from, and how many other copies of it
        /// are in the drawing. Reads only.</summary>
        public static Dictionary<string, object> InsertCheck(Database db, List<InsertItem> items, List<List<ObjectId>> replace)
        {
            Dictionary<string, object> res = new Dictionary<string, object>();
            List<object> list = new List<object>();
            res["items"] = list;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                List<string> origNames = new List<string>();
                List<ObjectId> checkedTop = new List<ObjectId>();
                if (replace != null)
                    foreach (List<ObjectId> ch in replace)
                    {
                        BlockReference br = tr.GetObject(ch[ch.Count - 1], OpenMode.ForRead) as BlockReference;
                        if (br != null) { string en = EffName(br, tr); if (!origNames.Exists(x => Same(x, en))) origNames.Add(en); }
                        if (ch.Count == 1) checkedTop.Add(ch[0]);
                    }
                res["originals"] = origNames;
                foreach (InsertItem it in items)
                {
                    string name = LegalName(string.IsNullOrEmpty(it.Block) ? Path.GetFileNameWithoutExtension(it.File) : it.Block);
                    string layer = LegalName(string.IsNullOrEmpty(it.Layer) ? Path.GetFileNameWithoutExtension(it.File) : it.Layer);
                    Dictionary<string, object> d = new Dictionary<string, object>();
                    d["block"] = name; d["layer"] = layer;
                    d["layerExists"] = lt.Has(layer);
                    d["layerIsCurrent"] = lt.Has(layer) && lt[layer] == db.Clayer;
                    d["sameAsOriginal"] = origNames.Exists(x => Same(x, name));
                    long total = 0, others = 0;
                    if (bt.Has(name))
                    {
                        BlockTableRecord def = (BlockTableRecord)tr.GetObject(bt[name], OpenMode.ForRead);
                        List<ObjectId> refs = new List<ObjectId>();
                        foreach (ObjectId rid in def.GetBlockReferenceIds(true, false)) refs.Add(rid);
                        if (def.IsDynamicBlock)
                            foreach (ObjectId aid in def.GetAnonymousBlockIds())
                            {
                                BlockTableRecord a = (BlockTableRecord)tr.GetObject(aid, OpenMode.ForRead);
                                foreach (ObjectId rid in a.GetBlockReferenceIds(true, false)) refs.Add(rid);
                            }
                        total = refs.Count;
                        foreach (ObjectId rid in refs) if (!checkedTop.Contains(rid)) others++;
                    }
                    d["blockExists"] = bt.Has(name);
                    d["copies"] = total;
                    d["otherCopies"] = others;     // copies that are not among the ones being replaced
                    list.Add(d);
                }
                tr.Commit();
            }
            return res;
        }

        /// <summary>Purge only what erasing the originals freed: their block definitions and,
        /// below them, any nested definition no longer used anywhere.</summary>
        static long PurgeFreed(Database db, List<ObjectId> roots, Progress prog)
        {
            List<ObjectId> cand = new List<ObjectId>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Stack<ObjectId> st = new Stack<ObjectId>(roots);
                while (st.Count > 0)
                {
                    ObjectId id = st.Pop();
                    if (cand.Contains(id) || id.IsErased) continue;
                    BlockTableRecord b = tr.GetObject(id, OpenMode.ForRead) as BlockTableRecord;
                    if (b == null || b.IsLayout || b.IsFromExternalReference) continue;
                    cand.Add(id);
                    if (b.IsDynamicBlock) foreach (ObjectId aid in b.GetAnonymousBlockIds()) st.Push(aid);
                    foreach (ObjectId eid in b)
                    {
                        BlockReference br = tr.GetObject(eid, OpenMode.ForRead) as BlockReference;
                        if (br == null) continue;
                        st.Push(br.BlockTableRecord);
                        if (br.IsDynamicBlock) st.Push(br.DynamicBlockTableRecord);
                    }
                }
                tr.Commit();
            }
            long total = 0;
            for (int pass = 0; pass < 100; pass++)
            {
                ObjectIdCollection c = new ObjectIdCollection();
                foreach (ObjectId id in cand) if (!id.IsErased) c.Add(id);
                if (c.Count == 0) break;
                db.Purge(c);                  // leaves only what nothing uses any more
                if (c.Count == 0) break;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in c) { try { tr.GetObject(id, OpenMode.ForWrite).Erase(); total++; } catch { } }
                    tr.Commit();
                }
            }
            prog.Log("Purged " + total + " block definition(s) that the erased originals no longer need.");
            return total;
        }

        public static Dictionary<string, object> Insert(Database db, Editor ed, List<InsertItem> items, List<List<ObjectId>> replace, Progress prog)
        {
            return Insert(db, ed, items, replace, false, true, false, prog);
        }

        public static Dictionary<string, object> Insert(Database db, Editor ed, List<InsertItem> items, List<List<ObjectId>> replace,
                                                        bool purgeFreed, bool allowRedefine, bool makeCurrent, Progress prog)
        {
            // everything that could stop the insert is checked before anything changes
            foreach (InsertItem it in items) if (!File.Exists(it.File)) throw new PublishAbortException("WBLOCK file not found: " + it.File + ". Nothing was changed.");
            Dictionary<string, object> pre = InsertCheck(db, items, replace);
            foreach (object o in (List<object>)pre["items"])
            {
                Dictionary<string, object> d = (Dictionary<string, object>)o;
                bool exists = (bool)d["blockExists"], same = (bool)d["sameAsOriginal"];
                long others = Convert.ToInt64(d["otherCopies"]);
                bool goneAfterPurge = purgeFreed && same && others == 0;
                if (exists && !allowRedefine && !goneAfterPurge)
                    throw new PublishAbortException("A block named '" + d["block"] + "' is already in the drawing (" + others + " other cop" + (others == 1 ? "y" : "ies") + "). Nothing was changed.");
            }
            Dictionary<string, object> res = new Dictionary<string, object>();
            List<object> done = new List<object>();
            List<string> warnings = new List<string>();
            res["inserted"] = done; res["warnings"] = warnings;
            List<ObjectId> newRefs = new List<ObjectId>();
            // 1) originals to erase (checked before anything changes)
            List<ObjectId> erase = new List<ObjectId>();
            if (replace != null)
                foreach (List<ObjectId> ch in replace)
                {
                    if (ch.Count != 1) { warnings.Add("A block nested inside another block was not erased (it is part of the parent's definition); the WBLOCK was inserted alongside it."); continue; }
                    if (!erase.Contains(ch[0])) erase.Add(ch[0]);
                }
            if (erase.Count > 0)
            {
                List<ObjectId> freed = new List<ObjectId>();
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ObjectId msId = SymbolUtilityServices.GetBlockModelSpaceId(db);
                    int n = 0;
                    foreach (ObjectId id in erase)
                    {
                        if (id.IsErased) continue;
                        BlockReference e = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (e == null || e.OwnerId != msId) continue;
                        if (!freed.Contains(e.BlockTableRecord)) freed.Add(e.BlockTableRecord);
                        if (e.IsDynamicBlock && !freed.Contains(e.DynamicBlockTableRecord)) freed.Add(e.DynamicBlockTableRecord);
                        e.UpgradeOpen(); e.Erase(); n++;
                    }
                    tr.Commit();
                    res["erased"] = n;
                    prog.Log("Erased " + n + " original block reference(s) (UNDO brings them back).");
                }
                if (purgeFreed) res["purged"] = PurgeFreed(db, freed, prog);
            }
            foreach (InsertItem it in items)
            {
                if (!File.Exists(it.File)) throw new PublishAbortException("WBLOCK file not found: " + it.File);
                string name = LegalName(string.IsNullOrEmpty(it.Block) ? Path.GetFileNameWithoutExtension(it.File) : it.Block);
                string layer = LegalName(string.IsNullOrEmpty(it.Layer) ? Path.GetFileNameWithoutExtension(it.File) : it.Layer);
                prog.Stage("Inserting " + Path.GetFileName(it.File));
                bool redefined = false;
                ObjectId btrId;
                using (Database src = new Database(false, true))
                {
                    src.ReadDwgFile(it.File, FileOpenMode.OpenForReadAndAllShare, true, "");
                    src.CloseInput(true);
                    bool exists;
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        exists = bt.Has(name);
                        tr.Commit();
                    }
                    if (!exists) btrId = db.Insert(name, src, true);
                    else if (!allowRedefine) throw new PublishAbortException("A block named '" + name + "' is still in the drawing (other copies use it). Nothing was inserted.");
                    else
                    {
                        // redefine: bring it in under a temporary name, move its contents into the
                        // existing definition, drop the temporary one
                        string tmp = name + "$DWGT" + DateTime.Now.Ticks.ToString("X");
                        ObjectId tmpId = db.Insert(tmp, src, true);
                        using (Transaction tr = db.TransactionManager.StartTransaction())
                        {
                            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                            btrId = bt[name];
                            BlockTableRecord old = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForWrite);
                            if (old.IsFromExternalReference || old.IsLayout) throw new PublishAbortException("'" + name + "' in this drawing is an xref or a layout, not a block. Choose another block name.");
                            foreach (ObjectId eid in old) tr.GetObject(eid, OpenMode.ForWrite).Erase();
                            BlockTableRecord t = (BlockTableRecord)tr.GetObject(tmpId, OpenMode.ForRead);
                            ObjectIdCollection ids = new ObjectIdCollection();
                            foreach (ObjectId eid in t) ids.Add(eid);
                            IdMapping map = new IdMapping();
                            db.DeepCloneObjects(ids, btrId, map, false);
                            old.Origin = Point3d.Origin;
                            tr.Commit();
                        }
                        using (Transaction tr = db.TransactionManager.StartTransaction())
                        {
                            BlockTableRecord t = (BlockTableRecord)tr.GetObject(tmpId, OpenMode.ForWrite);
                            foreach (ObjectId eid in t) tr.GetObject(eid, OpenMode.ForWrite).Erase();
                            t.Erase();
                            // every existing copy shows the new contents
                            BlockTableRecord nb = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                            foreach (ObjectId rid in nb.GetBlockReferenceIds(true, false))
                            { try { ((BlockReference)tr.GetObject(rid, OpenMode.ForWrite)).RecordGraphicsModified(true); } catch { } }
                            tr.Commit();
                        }
                        redefined = true;
                    }
                }
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    ObjectId layerId;
                    bool layerExisted = lt.Has(layer);
                    if (layerExisted)
                    {
                        layerId = lt[layer];
                        LayerTableRecord le = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);
                        if (le.IsFrozen || le.IsOff || le.IsLocked) warnings.Add("Layer '" + layer + "' is " + (le.IsFrozen ? "frozen" : le.IsOff ? "off" : "locked") + "; the block is on it but may not show until you change that.");
                        if (makeCurrent && db.Clayer != layerId && !le.IsFrozen) { db.Clayer = layerId; res["currentLayer"] = layer; }
                    }
                    else
                    {
                        lt.UpgradeOpen();
                        LayerTableRecord l = new LayerTableRecord(); l.Name = layer; l.IsPlottable = true;
                        if (it.Color >= 1 && it.Color <= 255) l.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)it.Color);
                        layerId = lt.Add(l); tr.AddNewlyCreatedDBObject(l, true);
                    }
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                    BlockReference br = new BlockReference(new Point3d(it.X, it.Y, 0), btrId);
                    br.SetDatabaseDefaults(db);
                    br.LayerId = layerId;
                    br.ScaleFactors = new Scale3d(1, 1, 1);
                    br.Rotation = 0;
                    ObjectId rid = ms.AppendEntity(br);
                    tr.AddNewlyCreatedDBObject(br, true);
                    newRefs.Add(rid);
                    tr.Commit();
                    Dictionary<string, object> d = new Dictionary<string, object>();
                    d["file"] = it.File; d["block"] = name; d["layer"] = layer; d["x"] = it.X; d["y"] = it.Y;
                    d["redefined"] = redefined; d["handle"] = rid.Handle.ToString();
                    done.Add(d);
                    prog.Log(string.Format("Inserted {0} as block '{1}' on layer '{2}' at {3:0.###},{4:0.###}{5}.", Path.GetFileName(it.File), name, layer, it.X, it.Y, redefined ? " (existing definition updated)" : ""));
                }
            }
            // show what went in
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                List<Occurrence> occ = new List<Occurrence>();
                foreach (ObjectId id in newRefs)
                {
                    BlockReference br = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
                    Occurrence o = new Occurrence(); o.Path.Add(id); o.Full = br.BlockTransform;
                    // zoom to the linework itself (main cluster), not AutoCAD's extents,
                    // which count curve control points and far-off strays
                    try
                    {
                        BoxWalk w = new BoxWalk();
                        w.Budget = 300000; w.LayerHidden = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                        w.Db = db; w.Tr = tr;
                        WalkRef(w, br, Matrix3d.Identity, br.Layer, false, 0);
                        if (w.HaveVis) Core(w, o);
                    }
                    catch { }
                    if (!o.HasBox) { try { o.Box = br.GeometricExtents; o.HasBox = true; } catch { } }
                    occ.Add(o);
                }
                LightUp(ed, db, tr, occ, -1);
                tr.Commit();
            }
            res["saved"] = false;
            return res;
        }

        /// <summary>The blocks selected in CAD (each as its chain from model space:
        /// [{name, handle, x, y}]). Nothing selected: ask for one click (nested).</summary>
        public static List<object> Selected(Editor ed, Database db, out string how)
        {
            List<object> items = new List<object>();
            how = "selection";
            PromptSelectionResult ps = null;
            try { ps = ed.SelectImplied(); } catch { }
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId msId = SymbolUtilityServices.GetBlockModelSpaceId(db);
                if (ps != null && ps.Status == PromptStatus.OK && ps.Value != null)
                    foreach (SelectedObject so in ps.Value)
                    {
                        if (so == null || items.Count >= 500) continue;
                        BlockReference br = tr.GetObject(so.ObjectId, OpenMode.ForRead) as BlockReference;
                        if (br == null || br.OwnerId != msId) continue;
                        items.Add(ChainOf(new List<ObjectId> { so.ObjectId }, tr));
                    }
                tr.Commit();
            }
            if (items.Count > 0) return items;
            how = "click";
            PromptNestedEntityOptions po = new PromptNestedEntityOptions("\nDWG Tools: click the block you want to find in the tree (Esc cancels): ");
            po.AllowNone = false;
            PromptNestedEntityResult r = ed.GetNestedEntity(po);
            if (r.Status != PromptStatus.OK) { how = "cancelled"; return items; }
            ObjectId[] inner = r.GetContainers();      // innermost first
            List<ObjectId> chain = new List<ObjectId>();
            for (int i = inner.Length - 1; i >= 0; i--) chain.Add(inner[i]);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DBObject picked = tr.GetObject(r.ObjectId, OpenMode.ForRead);
                if (picked is BlockReference) chain.Add(r.ObjectId);
                else if (picked is AttributeReference && (chain.Count == 0 || chain[chain.Count - 1] != picked.OwnerId)) chain.Add(picked.OwnerId);
                if (chain.Count > 0) items.Add(ChainOf(chain, tr));
                tr.Commit();
            }
            return items;
        }

        static List<object> ChainOf(List<ObjectId> chain, Transaction tr)
        {
            List<object> out1 = new List<object>();
            Matrix3d m = Matrix3d.Identity;
            foreach (ObjectId id in chain)
            {
                BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                if (br == null) break;
                m = m * br.BlockTransform;
                Point3d ip = Point3d.Origin.TransformBy(m);
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["name"] = EffName(br, tr);
                d["handle"] = id.Handle.ToString();
                d["x"] = Math.Round(ip.X, 4); d["y"] = Math.Round(ip.Y, 4);
                d["xref"] = IsXref(br, tr);
                out1.Add(d);
                if (IsXref(br, tr)) break;
            }
            return out1;
        }

        public static List<ObjectId> FromHandles(Database db, IEnumerable<string> handles)
        {
            List<ObjectId> ids = new List<ObjectId>();
            foreach (string h0 in handles)
            {
                string h = (h0 ?? "").Trim();
                if (h.Length == 0) continue;
                ObjectId id = ObjectId.Null;
                try { id = db.GetObjectId(false, new Handle(Convert.ToInt64(h, 16)), 0); } catch { }
                if (id.IsNull || id.IsErased) throw new PublishAbortException("That block is no longer in the drawing (handle " + h + "). Light it up again.");
                ids.Add(id);
            }
            if (ids.Count == 0) throw new PublishAbortException("No block chosen.");
            return ids;
        }

        // ---------------------------------------------------------------- wblock
        /// <summary>Write one or more occurrences (each a path: top-level reference ...
        /// the block) to ONE new file: exploded to plain geometry on layer 0, master
        /// coordinates. Each occurrence goes through the same copy-isolate-explode as a
        /// single one; several are then merged into one drawing.</summary>
        public static Dictionary<string, object> Make(Database db, List<List<ObjectId>> chains, string savePath, Progress prog)
        {
            return Make(db, chains, savePath, null, prog);
        }

        public static Dictionary<string, object> Make(Database db, List<List<ObjectId>> chains, string savePath, List<StdLayer> std, Progress prog)
        {
            Dictionary<string, object> res = new Dictionary<string, object>();
            savePath = Path.GetFullPath(savePath);
            if (!savePath.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)) savePath += ".dwg";
            if (File.Exists(savePath)) throw new PublishAbortException(savePath + " already exists. Choose another name; existing files are never replaced.");
            if (!string.IsNullOrEmpty(db.Filename) && Util.SameFile(savePath, db.Filename)) throw new PublishAbortException("That is the drawing itself. Choose another name.");
            if (chains.Count > 5000) throw new PublishAbortException(chains.Count + " placements is too many for one WBLOCK (limit 5,000). Tick fewer.");
            string dir = Path.GetDirectoryName(savePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            res["file"] = savePath;
            res["count"] = chains.Count;

            // what is being copied, and where it is in the MASTER (measured the same way
            // as the saved file is, below)
            Extents3d origBox = new Extents3d(); bool haveOrig = false;
            Dictionary<string, object> masterMeasure = null;
            Dictionary<string, long> blockCounts = new Dictionary<string, long>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Extents3d mainAll = new Extents3d(); bool haveMain = false;
                List<double[]> sample = new List<double[]>();
                long objects = 0; bool complete = true;
                int perChain = Math.Max(3, 200 / chains.Count);
                long budgetEach = Math.Max(20000, 1000000 / chains.Count);
                for (int c = 0; c < chains.Count; c++)
                {
                    List<ObjectId> path = chains[c];
                    Matrix3d parent = Matrix3d.Identity;
                    List<string> names = new List<string>();
                    string effLayer = "0";
                    for (int i = 0; i < path.Count; i++)
                    {
                        BlockReference br = tr.GetObject(path[i], OpenMode.ForRead) as BlockReference;
                        if (br == null) throw new PublishAbortException("The chosen object is not a block reference.");
                        effLayer = (i == 0 || br.Layer != "0") ? br.Layer : effLayer;
                        if (i > 0)
                        {
                            BlockReference prev = (BlockReference)tr.GetObject(path[i - 1], OpenMode.ForRead);
                            if (br.OwnerId != prev.BlockTableRecord) throw new PublishAbortException("The chosen block is no longer inside its parent. Light it up again.");
                        }
                        names.Add(EffName(br, tr));
                        if (i < path.Count - 1) parent = parent * br.BlockTransform;
                    }
                    BlockReference target = (BlockReference)tr.GetObject(path[path.Count - 1], OpenMode.ForRead);
                    string bn = names[names.Count - 1];
                    long bc; blockCounts.TryGetValue(bn, out bc); blockCounts[bn] = bc + 1;
                    Point3d ip = target.Position.TransformBy(parent);
                    Vector3d xa = Vector3d.XAxis.TransformBy(parent * target.BlockTransform);
                    if (c == 0)
                    {
                        res["inside"] = string.Join(" > ", names.ToArray());
                        res["insertionPoint"] = new double[] { ip.X, ip.Y, ip.Z };
                        masterMeasure = new Dictionary<string, object>();
                        masterMeasure["insertion"] = new double[] { ip.X, ip.Y, ip.Z };
                        masterMeasure["rotationDeg"] = Math.Round(Math.Atan2(xa.Y, xa.X) * 180.0 / Math.PI, 4);
                        masterMeasure["units"] = db.Insunits.ToString();
                    }
                    try
                    {
                        BoxWalk mw = new BoxWalk();
                        mw.Budget = budgetEach; mw.LayerHidden = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                        mw.Db = db; mw.Tr = tr;
                        mw.Points = new List<Point3d>();
                        WalkRef(mw, target, parent, effLayer, false, 0);
                        objects += mw.Visible;
                        if (mw.Budget <= 0) complete = false;
                        Occurrence mo = new Occurrence();
                        if (mw.HaveVis) Core(mw, mo);
                        if (mo.HasBox) { if (!haveMain) { mainAll = mo.Box; haveMain = true; } else mainAll.AddExtents(mo.Box); }
                        if (mo.HasAllVisible) { if (!haveOrig) { origBox = mo.AllVisible; haveOrig = true; } else origBox.AddExtents(mo.AllVisible); }   // true size, not control points
                        // points spread evenly over each placement
                        int np = mw.Points.Count, step = Math.Max(1, np / perChain);
                        for (int i = 0, k = 0; i < np && k < perChain && sample.Count < 1000; i += step, k++)
                            sample.Add(new double[] { mw.Points[i].X, mw.Points[i].Y, mw.Points[i].Z });
                    }
                    catch { }
                }
                if (masterMeasure != null)
                {
                    masterMeasure["objects"] = objects;
                    if (haveMain) masterMeasure["mainBox"] = BoxArr(mainAll);
                    if (haveOrig) masterMeasure["allBox"] = BoxArr(origBox);
                    masterMeasure["complete"] = complete;
                    masterMeasure["samplePoints"] = sample;
                    masterMeasure["placements"] = chains.Count;
                }
                tr.Commit();
            }
            List<string> bl = new List<string>(); foreach (KeyValuePair<string, long> kv in blockCounts) bl.Add(chains.Count > 1 ? kv.Key + " x" + kv.Value : kv.Key);
            res["block"] = string.Join(" + ", bl.ToArray());
            res["blocks"] = blockCounts;

            PublishOptions opt = new PublishOptions();
            opt.Source = string.IsNullOrEmpty(db.Filename) ? "(drawing)" : db.Filename;
            opt.Output = savePath;
            opt.Mode = PublishMode.FullFlat;
            opt.Layers = LayerPolicy.SingleLayer;
            opt.OutputLayer = "0";
            opt.KeepNoPlot = true;             // if you can see it, it goes into the WBLOCK
            opt.ExtentsInfoOnly = true;
            opt.Strict = true;
            opt.VerifyGeometry = true;
            opt.PurgeUnused = true;
            Report rep = new Report();
            Database prevWorking = HostApplicationServices.WorkingDatabase;
            string temp = null;
            Flattener fl = null;
            Database b = null;
            try
            {
                if (chains.Count == 1)
                {
                    prog.Stage("Copying it into a new drawing (WBLOCK, base point 0,0,0)");
                    b = CopyAndExplode(db, chains[0], opt, rep, prog, out fl);
                }
                else
                {
                    // each placement exactly as a single one, then all merged into one drawing
                    b = new Database(true, true);
                    b.Insunits = db.Insunits;
                    for (int c = 0; c < chains.Count; c++)
                    {
                        prog.Stage(string.Format("Copying {0} of {1} into the new drawing (base point 0,0,0)", c + 1, chains.Count));
                        Flattener f1;
                        Database one = CopyAndExplode(db, chains[c], opt, rep, null, out f1);
                        try
                        {
                            HostApplicationServices.WorkingDatabase = one;
                            Publisher.Purge(one, true);
                            HostApplicationServices.WorkingDatabase = b;
                            b.Insert(Matrix3d.Identity, one, false);
                        }
                        finally { HostApplicationServices.WorkingDatabase = prevWorking; one.Dispose(); }
                    }
                    prog.Log(string.Format("Merged {0} placements into one drawing.", chains.Count));
                }
                HostApplicationServices.WorkingDatabase = b;
                using (Transaction tr = b.TransactionManager.StartTransaction())
                {
                    b.Insbase = Point3d.Origin;
                    b.Insunits = db.Insunits;     // same units as the master, so inserting it does not rescale it
                    // linetypes look the same as in the master: same global linetype scales
                    b.Ltscale = db.Ltscale; b.Celtscale = db.Celtscale; b.Psltscale = db.Psltscale;
                    tr.Commit();
                }
                // only layer 0. Done AFTER the explode (which already resolved every
                // ByLayer colour / linetype from the original layers) and only on what the
                // explode produced in model space: layer 0 plain and current, so every other
                // layer is unused and purges away
                using (Transaction tr = b.TransactionManager.StartTransaction())
                {
                    LayerTable lt0 = (LayerTable)tr.GetObject(b.LayerTableId, OpenMode.ForRead);
                    ObjectId zero = lt0["0"];
                    LayerTableRecord l0 = (LayerTableRecord)tr.GetObject(zero, OpenMode.ForWrite);
                    if (l0.IsFrozen) l0.IsFrozen = false;
                    if (l0.IsOff) l0.IsOff = false;
                    if (l0.IsLocked) l0.IsLocked = false;
                    if (!l0.IsPlottable) l0.IsPlottable = true;
                    if (b.Clayer != zero) b.Clayer = zero;
                    BlockTable bt0 = (BlockTable)tr.GetObject(b.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms0 = (BlockTableRecord)tr.GetObject(bt0[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    int moved = 0, stuck = 0;
                    foreach (ObjectId eid in ms0)
                    {
                        Entity e0 = tr.GetObject(eid, OpenMode.ForRead) as Entity;
                        if (e0 == null || e0.LayerId == zero) continue;
                        try { e0.UpgradeOpen(); e0.LayerId = zero; moved++; }
                        catch (System.Exception ex) { stuck++; if (stuck <= 3) prog.Log("  could not move a " + e0.GetType().Name + " to layer 0: " + ex.Message); }
                    }
                    if (moved > 0 || stuck > 0) prog.Log(string.Format("Moved {0} leftover object(s) to layer 0{1}.", moved, stuck > 0 ? "; " + stuck + " could not be moved" : ""));
                    tr.Commit();
                }
                // no geographic location: the WBLOCK is only about its offset from 0,0
                using (Transaction tr = b.TransactionManager.StartTransaction())
                {
                    int geo = 0;
                    try { ObjectId gid = b.GeoDataObject; if (!gid.IsNull && !gid.IsErased) { tr.GetObject(gid, OpenMode.ForWrite).Erase(); geo++; } } catch { }
                    BlockTable btg = (BlockTable)tr.GetObject(b.BlockTableId, OpenMode.ForRead);
                    foreach (ObjectId eid in (BlockTableRecord)tr.GetObject(btg[BlockTableRecord.ModelSpace], OpenMode.ForRead))
                        if (eid.ObjectClass.DxfName == "GEOMAPIMAGE" || eid.ObjectClass.DxfName == "GEOPOSITIONMARKER") { try { tr.GetObject(eid, OpenMode.ForWrite).Erase(); geo++; } catch { } }
                    if (geo > 0) prog.Log("Removed the geographic location (" + geo + " object" + (geo > 1 ? "s" : "") + "): only the offset from 0,0 matters.");
                    tr.Commit();
                }
                long purged = Publisher.Purge(b, true);
                rep.Count("purged.objects", purged);
                List<string> stdNames = new List<string>();
                if (std != null && std.Count > 0)
                {
                    prog.Stage("Sorting the objects onto the standard layers by colour");
                    res["layerSort"] = SortOntoLayers(b, std, res, prog);
                    foreach (StdLayer l in std) stdNames.Add(l.Name);
                }
                long msCount = 0;
                Extents3d newBox = new Extents3d(); bool haveNew = false;
                List<string> outside = new List<string>(); long outsideCount = 0;
                List<string> warnings = new List<string>();
                res["warnings"] = warnings;
                Dictionary<string, long> types = new Dictionary<string, long>();
                res["types"] = types;
                Dictionary<string, long> ltypes = new Dictionary<string, long>();
                res["linetypes"] = ltypes;
                using (Transaction tr = b.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(b.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    double otol = haveOrig ? 0.01 * Math.Max(1.0, origBox.MinPoint.DistanceTo(origBox.MaxPoint)) : 0;
                    foreach (ObjectId id in ms)
                    {
                        msCount++;
                        Entity e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (e != null) { string tn = e.GetType().Name; long tc; types.TryGetValue(tn, out tc); types[tn] = tc + 1; }
                        if (e != null) { string ltn = e.Linetype; long lc; ltypes.TryGetValue(ltn, out lc); ltypes[ltn] = lc + 1; }
                        Extents3d x;
                        try { x = TrueBox(e, Matrix3d.Identity); } catch { continue; }
                        if (!haveNew) { newBox = x; haveNew = true; } else newBox.AddExtents(x);
                        // anything in the copy that sticks out past the original block: name it
                        if (haveOrig && (x.MinPoint.X < origBox.MinPoint.X - otol || x.MinPoint.Y < origBox.MinPoint.Y - otol
                                      || x.MaxPoint.X > origBox.MaxPoint.X + otol || x.MaxPoint.Y > origBox.MaxPoint.Y + otol))
                        {
                            outsideCount++;
                            if (outside.Count < 15)
                            {
                                string what = e.GetType().Name;
                                DBText dt = e as DBText; MText mt = e as MText;
                                if (dt != null) what += " \"" + dt.TextString + "\"";
                                if (mt != null) what += " \"" + mt.Contents + "\"";
                                outside.Add(string.Format("{0} from {1:0.#},{2:0.#} to {3:0.#},{4:0.#}", what.Length > 80 ? what.Substring(0, 80) : what,
                                    x.MinPoint.X, x.MinPoint.Y, x.MaxPoint.X, x.MaxPoint.Y));
                            }
                        }
                    }
                    LayerTable lt = (LayerTable)tr.GetObject(b.LayerTableId, OpenMode.ForRead);
                    List<string> layersLeft = new List<string>();
                    foreach (ObjectId lid in lt) { if (lid.IsErased) continue; LayerTableRecord l = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead); layersLeft.Add(l.Name); }
                    res["layers"] = layersLeft;
                    { List<string> lts = new List<string>(); foreach (KeyValuePair<string, long> kv in ltypes) lts.Add(kv.Key + " " + kv.Value);
                      prog.Log("Linetypes in the WBLOCK: " + string.Join(", ", lts.ToArray()) + " (LTSCALE " + b.Ltscale + ")"); }
                    prog.Log("Layers in the WBLOCK: " + string.Join(", ", layersLeft.ToArray()));
                    List<string> extra = new List<string>();
                    foreach (string ln in layersLeft) if (ln != "0" && !stdNames.Exists(x => string.Equals(x, ln, StringComparison.OrdinalIgnoreCase))) extra.Add(ln);
                    if (extra.Count > 0) warnings.Add("The WBLOCK has layers that are not in the standard: " + string.Join(", ", extra.ToArray()));
                    tr.Commit();
                }
                res["entities"] = msCount;
                if (msCount == 0) throw new PublishAbortException("Nothing visible came out of this block (everything in it is on hidden layers?). No file was written.");

                long omitted = 0;
                foreach (KeyValuePair<string, long> kv in rep.Counters) if (kv.Key.StartsWith("omitted.")) omitted += kv.Value;
                res["leftOut"] = omitted;
                if (haveOrig && haveNew)
                {
                    double size = Math.Max(1.0, origBox.MinPoint.DistanceTo(origBox.MaxPoint));
                    // A gross-placement check only (the real check is the comparison with
                    // AutoCAD's own Explode, below). AutoCAD's stored size for a block can be
                    // slightly stale, so a correct copy may stick out of it by a hair: allow 2%.
                    double tol = 0.02 * size;
                    bool inside = newBox.MinPoint.X >= origBox.MinPoint.X - tol && newBox.MinPoint.Y >= origBox.MinPoint.Y - tol
                               && newBox.MaxPoint.X <= origBox.MaxPoint.X + tol && newBox.MaxPoint.Y <= origBox.MaxPoint.Y + tol;
                    res["sameLocation"] = inside;
                    string boxes = string.Format("original {0:0.##},{1:0.##} to {2:0.##},{3:0.##}; copy {4:0.##},{5:0.##} to {6:0.##},{7:0.##}",
                        origBox.MinPoint.X, origBox.MinPoint.Y, origBox.MaxPoint.X, origBox.MaxPoint.Y,
                        newBox.MinPoint.X, newBox.MinPoint.Y, newBox.MaxPoint.X, newBox.MaxPoint.Y);
                    res["placement"] = boxes;
                    prog.Log("Placement: " + boxes);
                    if (!inside)
                    {
                        // Not a stop: the file is still saved, and what sticks out is named so it can be looked at.
                        warnings.Add(outsideCount + " object(s) in the WBLOCK reach past where the block sits in the drawing (" + boxes + ").");
                        res["outside"] = outside;
                        res["outsideCount"] = outsideCount;
                        foreach (string o2 in outside) prog.Log("  outside: " + o2);
                    }
                    res["box"] = new double[] { newBox.MinPoint.X, newBox.MinPoint.Y, newBox.MaxPoint.X, newBox.MaxPoint.Y };
                }

                prog.Stage("Saving " + Path.GetFileName(savePath));
                temp = Path.Combine(dir, Path.GetFileNameWithoutExtension(savePath) + ".saving-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".dwg");
                HostApplicationServices.WorkingDatabase = prevWorking;
                SafeFs.SaveDatabase(b, temp);
                SafeFs.RecordCreated(temp);

                prog.Stage("Reopening it and checking it against AutoCAD's own Explode");
                Validator v = new Validator(opt, rep, prog);
                res["validation"] = v.Run(temp, msCount, fl);
                res["report"] = rep.ToDictionary();
                if (!v.Passed || rep.Errors > 0)
                {
                    // saved anyway, under the name chosen; what did not match is reported
                    List<string> failed = new List<string>();
                    object chk; Dictionary<string, object> val = res["validation"] as Dictionary<string, object>;
                    if (val != null && val.TryGetValue("checks", out chk))
                        foreach (object c in (System.Collections.IEnumerable)chk)
                        {
                            Dictionary<string, object> cd = c as Dictionary<string, object>;
                            if (cd != null && !(bool)cd["passed"]) failed.Add(Convert.ToString(cd["check"]) + (cd["detail"] != null ? ": " + cd["detail"] : ""));
                        }
                    warnings.Add("Some checks against AutoCAD's own Explode did not match" + (failed.Count > 0 ? ": " + string.Join("; ", failed.ToArray()) : "") + ". Open the file and compare it with the drawing.");
                }
                SafeFs.MoveToNew(temp, savePath);
                SafeFs.RecordCreated(savePath);
                temp = null;
                prog.Stage("Checking the coordinates: the saved file against the master");
                res["coordinates"] = VerifyCoordinates(savePath, masterMeasure, warnings, prog);
                res["sizeBytes"] = new FileInfo(savePath).Length;
                return res;
            }
            finally
            {
                HostApplicationServices.WorkingDatabase = prevWorking;
                if (b != null) b.Dispose();
                if (temp != null) { try { SafeFs.Delete(temp); } catch { } }
            }
        }

        /// <summary>After the explode everything is on layer 0 with its real colour, linetype
        /// and lineweight. Create the standard layers (colour, linetype, lineweight,
        /// transparency, plot) and move each object whose colour is a standard layer's
        /// colour onto that layer, colour ByLayer. Linetype / lineweight become ByLayer only
        /// where they already equal the layer's, so nothing changes how it looks.
        /// Objects in any other colour go on layer 0 with colour ByLayer, linetype
        /// Continuous and lineweight ByLayer.</summary>
        static Dictionary<string, long> SortOntoLayers(Database b, List<StdLayer> std, Dictionary<string, object> res, Progress prog)
        {
            Dictionary<string, long> moved = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, long> other = new Dictionary<string, long>();
            List<string> notes = new List<string>();
            using (Transaction tr = b.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr.GetObject(b.LayerTableId, OpenMode.ForWrite);
                LinetypeTable ltt = (LinetypeTable)tr.GetObject(b.LinetypeTableId, OpenMode.ForRead);
                ObjectId zero = lt["0"];
                LayerTableRecord l0rec = (LayerTableRecord)tr.GetObject(zero, OpenMode.ForRead);
                ObjectId zeroLt = l0rec.LinetypeObjectId;          // what ByLayer meant on layer 0
                LineWeight zeroLw = l0rec.LineWeight;
                Dictionary<int, ObjectId> byAci = new Dictionary<int, ObjectId>();
                Dictionary<ObjectId, ObjectId> layerLt = new Dictionary<ObjectId, ObjectId>();
                Dictionary<ObjectId, LineWeight> layerLw = new Dictionary<ObjectId, LineWeight>();
                Dictionary<ObjectId, string> layerName = new Dictionary<ObjectId, string>();
                foreach (StdLayer s in std)
                {
                    ObjectId ltId = ObjectId.Null;
                    if (ltt.Has(s.Linetype)) ltId = ltt[s.Linetype];
                    else
                    {
                        foreach (string lin in new string[] { "acad.lin", "acadiso.lin" })
                        {
                            try { b.LoadLineTypeFile(s.Linetype, lin); } catch { }
                            ltt = (LinetypeTable)tr.GetObject(b.LinetypeTableId, OpenMode.ForRead);
                            if (ltt.Has(s.Linetype)) { ltId = ltt[s.Linetype]; break; }
                        }
                    }
                    if (ltId.IsNull) { ltId = ltt["Continuous"]; notes.Add("Linetype " + s.Linetype + " (layer " + s.Name + ") was not found in acad.lin; Continuous used."); }
                    LayerTableRecord l;
                    if (lt.Has(s.Name)) l = (LayerTableRecord)tr.GetObject(lt[s.Name], OpenMode.ForWrite);
                    else { l = new LayerTableRecord(); l.Name = s.Name; lt.Add(l); tr.AddNewlyCreatedDBObject(l, true); }
                    if (s.Aci >= 1 && s.Aci <= 255) l.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)s.Aci);
                    l.LinetypeObjectId = ltId;
                    l.LineWeight = ParseLw(s.Lineweight);
                    l.IsPlottable = s.Plot;
                    // never touch IsFrozen on the current layer (AutoCAD refuses, even to thaw)
                    if (l.IsOff) l.IsOff = false;
                    if (l.IsLocked) l.IsLocked = false;
                    if (l.IsFrozen && l.ObjectId != b.Clayer) l.IsFrozen = false;
                    int pct = Math.Max(0, Math.Min(90, s.Transparency));
                    l.Transparency = new Transparency((byte)Math.Round(255 * (100 - pct) / 100.0));
                    if (s.Aci >= 1 && s.Aci <= 255 && !byAci.ContainsKey(s.Aci)) byAci[s.Aci] = l.ObjectId;
                    layerLt[l.ObjectId] = ltId; layerLw[l.ObjectId] = l.LineWeight; layerName[l.ObjectId] = s.Name;
                    moved[s.Name] = 0;
                }
                ltt = (LinetypeTable)tr.GetObject(b.LinetypeTableId, OpenMode.ForRead);
                ObjectId continuous = ltt["Continuous"];
                BlockTable bt = (BlockTable)tr.GetObject(b.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    Entity e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (e == null) continue;
                    Color c = e.Color;
                    int aci = -1; string key;
                    if (c.IsByLayer || c.IsByBlock) { aci = 7; key = "7 (white)"; }
                    else if (c.IsByAci) { aci = c.ColorIndex; key = aci.ToString(); }
                    else { key = "RGB " + c.Red + "," + c.Green + "," + c.Blue; }
                    ObjectId target;
                    if (aci < 0 || !byAci.TryGetValue(aci, out target))
                    {
                        // not a standard colour: layer 0, colour ByLayer, linetype Continuous, lineweight ByLayer
                        long n; other.TryGetValue(key, out n); other[key] = n + 1;
                        e.UpgradeOpen();
                        if (e.LayerId != zero) e.LayerId = zero;
                        e.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                        e.LinetypeId = continuous;
                        e.LineWeight = LineWeight.ByLayer;
                        continue;
                    }
                    // what it looks like now (ByLayer on layer 0 = layer 0's)
                    ObjectId curLt = (e.LinetypeId == b.ByLayerLinetype) ? zeroLt : (e.LinetypeId == b.ByBlockLinetype ? ltt["Continuous"] : e.LinetypeId);
                    LineWeight curLw = e.LineWeight == LineWeight.ByLayer ? zeroLw : (e.LineWeight == LineWeight.ByBlock ? LineWeight.ByLineWeightDefault : e.LineWeight);
                    e.UpgradeOpen();
                    e.LayerId = target;
                    e.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                    e.LinetypeId = curLt == layerLt[target] ? b.ByLayerLinetype : curLt;
                    e.LineWeight = curLw == layerLw[target] ? LineWeight.ByLayer : curLw;
                    moved[layerName[target]]++;
                }
                tr.Commit();
            }
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, long> kv in moved) parts.Add(kv.Key + " " + kv.Value);
            prog.Log("Standard layers: " + string.Join(", ", parts.ToArray()));
            List<string> op = new List<string>();
            foreach (KeyValuePair<string, long> kv in other) op.Add("colour " + kv.Key + ": " + kv.Value);
            if (op.Count > 0) prog.Log("Not a standard colour, put on layer 0 (ByLayer, Continuous): " + string.Join(", ", op.ToArray()));
            foreach (string n in notes) prog.Log(n);
            res["layerSortOther"] = other;
            res["layerSortNotes"] = notes;
            return moved;
        }

        static LineWeight ParseLw(string s)
        {
            string t = (s ?? "").Trim();
            if (t.Length == 0 || t.Equals("ByLineWeightDefault", StringComparison.OrdinalIgnoreCase) || t.Equals("Default", StringComparison.OrdinalIgnoreCase)) return LineWeight.ByLineWeightDefault;
            double mm;
            if (double.TryParse(t.Replace("mm", "").Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out mm))
            {
                int v = (int)Math.Round(mm < 3 ? mm * 100 : mm);
                foreach (LineWeight w in Enum.GetValues(typeof(LineWeight))) if ((int)w == v) return w;
            }
            return LineWeight.ByLineWeightDefault;
        }

        /// <summary>One occurrence: WBLOCK its top-level reference to a new database, keep
        /// only the chain down to it, make the chain's layers visible, explode it all.
        /// The caller disposes the result.</summary>
        static Database CopyAndExplode(Database db, List<ObjectId> path, PublishOptions opt, Report rep, Progress prog, out Flattener fl)
        {
            Database prevWorking = HostApplicationServices.WorkingDatabase;
            ObjectIdCollection ids = new ObjectIdCollection();
            ids.Add(path[0]);
            Database b = db.Wblock(ids, Point3d.Origin);
            bool ok = false;
            try
            {
                HostApplicationServices.WorkingDatabase = b;
                List<string> chainLayers = IsolatePath(b, db, path, rep);
                // the block's own layer must be visible in the copy, or the
                // explode would leave out what the drawing shows
                using (Transaction tr = b.TransactionManager.StartTransaction())
                {
                    LayerTable lt = (LayerTable)tr.GetObject(b.LayerTableId, OpenMode.ForRead);
                    foreach (string ln in chainLayers)
                    {
                        if (!lt.Has(ln)) continue;
                        LayerTableRecord l = (LayerTableRecord)tr.GetObject(lt[ln], OpenMode.ForWrite);
                        if (l.IsFrozen && l.ObjectId != b.Clayer) l.IsFrozen = false;
                        if (l.IsOff) l.IsOff = false;
                        if (!l.IsPlottable) l.IsPlottable = true;
                    }
                    b.Insbase = Point3d.Origin;
                    b.Insunits = db.Insunits;
                    b.Ltscale = db.Ltscale; b.Celtscale = db.Celtscale; b.Psltscale = db.Psltscale;
                    tr.Commit();
                }
                if (prog != null) prog.Stage("Exploding everything inside it onto layer 0");
                fl = new Flattener(b, opt, rep, prog ?? new Progress(null, null));
                fl.Run();
                ok = true;
                return b;
            }
            finally
            {
                HostApplicationServices.WorkingDatabase = prevWorking;
                if (!ok) b.Dispose();
            }
        }

        /// <summary>In the WBLOCKed copy (which holds only the top-level reference),
        /// keep the real chain top-level -> ... -> the chosen block and remove
        /// everything else along it (siblings, and the parents' own geometry and
        /// attributes). The explode then expands exactly the chosen occurrence,
        /// with every parent's transform and layer-0 / ByBlock inheritance, as
        /// AutoCAD would. Returns the layers along the chain (kept visible).</summary>
        static List<string> IsolatePath(Database b, Database src, List<ObjectId> path, Report rep)
        {
            List<string> layers = new List<string>();
            using (Transaction tr = b.TransactionManager.StartTransaction())
            using (Transaction ts = src.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(b.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                BlockReference topB = null;
                foreach (ObjectId id in ms) { BlockReference r = tr.GetObject(id, OpenMode.ForRead) as BlockReference; if (r != null) { topB = r; break; } }
                if (topB == null) throw new PublishAbortException("The copy is empty (WBLOCK returned nothing).");

                // walk the same chain in the copy
                List<BlockReference> chain = new List<BlockReference>();
                chain.Add(topB);
                for (int i = 1; i < path.Count; i++)
                {
                    BlockReference s = (BlockReference)ts.GetObject(path[i], OpenMode.ForRead);
                    BlockTableRecord sOwner = (BlockTableRecord)ts.GetObject(s.OwnerId, OpenMode.ForRead);
                    string sName = EffName(s, ts);
                    int rank = 0;   // which of the identical siblings
                    foreach (ObjectId cid in sOwner)
                    {
                        if (cid == path[i]) break;
                        BlockReference o = ts.GetObject(cid, OpenMode.ForRead) as BlockReference;
                        if (o != null && Twin(o, s, ts, sName)) rank++;
                    }
                    BlockTableRecord bOwner = (BlockTableRecord)tr.GetObject(chain[i - 1].BlockTableRecord, OpenMode.ForRead);
                    BlockReference found = null;
                    int seen = 0;
                    foreach (ObjectId cid in bOwner)
                    {
                        BlockReference o = tr.GetObject(cid, OpenMode.ForRead) as BlockReference;
                        if (o == null || !Twin(o, s, tr, sName)) continue;
                        if (seen++ == rank) { found = o; break; }
                    }
                    if (found == null) throw new PublishAbortException("Could not find '" + sName + "' inside its parent in the copy.");
                    chain.Add(found);
                }
                foreach (BlockReference c in chain) if (!layers.Contains(c.Layer)) layers.Add(c.Layer);

                long removed = 0;
                for (int i = 0; i < chain.Count - 1; i++)
                {
                    // the parent's own attributes are not part of the chosen block
                    foreach (ObjectId aid in chain[i].AttributeCollection)
                    {
                        try { tr.GetObject(aid, OpenMode.ForWrite, false, true).Erase(); removed++; } catch { }
                    }
                    BlockTableRecord def = (BlockTableRecord)tr.GetObject(chain[i].BlockTableRecord, OpenMode.ForRead);
                    ObjectId keep = chain[i + 1].ObjectId;
                    List<ObjectId> drop = new List<ObjectId>();
                    foreach (ObjectId cid in def) if (cid != keep) drop.Add(cid);
                    foreach (ObjectId cid in drop)
                    {
                        try { tr.GetObject(cid, OpenMode.ForWrite, false, true).Erase(); removed++; } catch { }
                    }
                }
                rep.Count("isolate.removedAroundPath", removed);
                tr.Commit();
                ts.Commit();
            }
            return layers;
        }

        /// <summary>The box of what is actually drawn. For a spline (and ellipse) AutoCAD's
        /// extents come from the control points, which can sit far from the curve; walk the
        /// curve itself instead.</summary>
        static Extents3d TrueBox(Entity e, Matrix3d xf)
        {
            Curve c = e as Curve;
            if (c != null && (e is Spline || e is Ellipse))
            {
                try
                {
                    double a = c.StartParam, b = c.EndParam;
                    Extents3d r = new Extents3d(); bool first = true;
                    for (int i = 0; i <= 128; i++)
                    {
                        Point3d p = c.GetPointAtParameter(a + (b - a) * i / 128.0).TransformBy(xf);
                        if (first) { r = new Extents3d(p, p); first = false; } else r.AddPoint(p);
                    }
                    return r;
                }
                catch { }
            }
            return TransformBox(e.GeometricExtents, xf);
        }

        static double[] BoxArr(Extents3d b)
        {
            return new double[] { Math.Round(b.MinPoint.X, 4), Math.Round(b.MinPoint.Y, 4), Math.Round(b.MaxPoint.X, 4), Math.Round(b.MaxPoint.Y, 4) };
        }

        /// <summary>Reopen the saved WBLOCK and measure where its linework is, the same
        /// way the block was measured in the master. The main linework must sit on the
        /// same coordinates, and the base point must be 0,0,0.</summary>
        static Dictionary<string, object> VerifyCoordinates(string file, Dictionary<string, object> master, List<string> warnings, Progress prog)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["master"] = master;
            Dictionary<string, object> f = new Dictionary<string, object>();
            r["wblock"] = f;
            List<Point3d> filePoints = null;
            using (Database fd = new Database(false, true))
            {
                fd.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, true, "");
                fd.CloseInput(true);
                f["basePoint"] = new double[] { fd.Insbase.X, fd.Insbase.Y, fd.Insbase.Z };
                f["units"] = fd.Insunits.ToString();
                if (master != null && master.ContainsKey("units") && Convert.ToString(master["units"]) != fd.Insunits.ToString())
                    warnings.Add("Units differ: the master is " + master["units"] + ", the WBLOCK is " + fd.Insunits + ". Inserting it may rescale it.");
                BoxWalk w = new BoxWalk();
                w.Points = new List<Point3d>();
                using (Transaction tr = fd.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(fd.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        Entity e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (e == null) continue;
                        Extents3d x;
                        try { x = TrueBox(e, Matrix3d.Identity); } catch { continue; }
                        w.Visible++;
                        if (!w.HaveVis) { w.Vis = x; w.HaveVis = true; } else w.Vis.AddExtents(x);
                        w.Boxes.Add(x); w.Kinds.Add(e.GetType().Name + "|" + e.Layer);
                        AddPoints(w, e, Matrix3d.Identity);
                    }
                    tr.Commit();
                }
                filePoints = w.Points;
                Occurrence fo = new Occurrence();
                if (w.HaveVis) Core(w, fo);
                f["objects"] = w.Visible;
                if (fo.HasBox) f["mainBox"] = BoxArr(fo.Box);
                if (fo.HasAllVisible) f["allBox"] = BoxArr(fo.AllVisible);
                bool baseOk = fd.Insbase.DistanceTo(Point3d.Origin) < 1e-9;
                r["baseIsOrigin"] = baseOk;
                if (!baseOk) warnings.Add("The WBLOCK's base point is not 0,0,0.");
            }
            // ---- point by point: every sampled point of the block must be in the WBLOCK at the same X,Y,Z
            List<double[]> want = master != null && master.ContainsKey("samplePoints") ? (List<double[]>)master["samplePoints"] : null;
            if (want != null && want.Count > 0 && filePoints != null)
            {
                double ptol = 0.001;
                Dictionary<string, List<Point3d>> grid = new Dictionary<string, List<Point3d>>();
                double g = 1.0;
                foreach (Point3d p in filePoints)
                {
                    string k = Math.Floor(p.X / g) + "," + Math.Floor(p.Y / g);
                    List<Point3d> l; if (!grid.TryGetValue(k, out l)) { l = new List<Point3d>(); grid[k] = l; } l.Add(p);
                }
                int found = 0; List<string> missed = new List<string>();
                foreach (double[] q in want)
                {
                    Point3d qp = new Point3d(q[0], q[1], q[2]);
                    bool hit = false;
                    for (int dx = -1; dx <= 1 && !hit; dx++)
                        for (int dy = -1; dy <= 1 && !hit; dy++)
                        {
                            List<Point3d> l;
                            if (grid.TryGetValue((Math.Floor(q[0] / g) + dx) + "," + (Math.Floor(q[1] / g) + dy), out l))
                                foreach (Point3d p in l) if (p.DistanceTo(qp) <= ptol) { hit = true; break; }
                        }
                    if (hit) found++; else if (missed.Count < 8) missed.Add(string.Format("{0:0.###}, {1:0.###}", q[0], q[1]));
                }
                r["pointsChecked"] = want.Count;
                r["pointsFound"] = found;
                r["pointsMissed"] = missed;
                prog.Log(string.Format("Points: {0} of {1} sampled points of the block are at the same coordinates in the WBLOCK (within {2}).", found, want.Count, ptol));
                if (found < want.Count)
                    warnings.Add(string.Format("{0} of {1} sampled points are not at the same coordinates in the WBLOCK (first: {2}).", want.Count - found, want.Count, string.Join("; ", missed.ToArray())));
            }
            double[] a = master != null && master.ContainsKey("mainBox") ? (double[])master["mainBox"] : null;
            double[] b = f.ContainsKey("mainBox") ? (double[])f["mainBox"] : null;
            if (a == null || b == null) { r["status"] = "NOT MEASURED"; return r; }
            double[] d = new double[4];
            double worst = 0;
            for (int i = 0; i < 4; i++) { d[i] = Math.Round(b[i] - a[i], 4); worst = Math.Max(worst, Math.Abs(d[i])); }
            double size = Math.Max(Math.Abs(a[2] - a[0]), Math.Abs(a[3] - a[1]));
            double tol = Math.Max(0.01, size * 0.001);   // text and curves measure a hair differently once exploded
            r["difference"] = d;
            r["worst"] = worst;
            r["tolerance"] = Math.Round(tol, 4);
            bool pointsOk = !r.ContainsKey("pointsChecked") || Convert.ToInt32(r["pointsFound"]) == Convert.ToInt32(r["pointsChecked"]);
            r["status"] = pointsOk && Convert.ToBoolean(r["baseIsOrigin"]) ? "PASS" : "CHECK";
            prog.Log(string.Format("Coordinates: master main linework {0:0.###},{1:0.###} to {2:0.###},{3:0.###}; WBLOCK {4:0.###},{5:0.###} to {6:0.###},{7:0.###}; worst difference {8:0.####} ({9})",
                a[0], a[1], a[2], a[3], b[0], b[1], b[2], b[3], worst, r["status"]));

            return r;
        }

        static bool Twin(BlockReference o, BlockReference s, Transaction tr, string sName)
        {
            if (!Same(EffName(o, tr), sName)) return false;
            if (o.Position.DistanceTo(s.Position) > 1e-9 * Math.Max(1.0, s.Position.GetAsVector().Length)) return false;
            if (Math.Abs(o.Rotation - s.Rotation) > 1e-12) return false;
            Scale3d a = o.ScaleFactors, c = s.ScaleFactors;
            return Math.Abs(a.X - c.X) < 1e-12 && Math.Abs(a.Y - c.Y) < 1e-12 && Math.Abs(a.Z - c.Z) < 1e-12;
        }
    }
}
