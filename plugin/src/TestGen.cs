// DwgFlatPublisher -- FLATTESTGEN: synthetic drawings that exercise every
// case the flattener must get right, plus a manifest of expected outcomes
// used by the Flask app's self-test.
//
//   nested_xref.dwg   geometry attached by child_xref (nested xref)
//   ghost_overlay.dwg OVERLAID by child_xref -> must NOT appear in master's output
//   child_xref.dwg    nested blocks + the two above
//   overlay_xref.dwg  overlaid directly by master -> must appear
//   master.dwg        5-level chains, rotation, mirroring, uniform and
//                     NON-uniform scale, ByBlock / ByLayer / layer-0
//                     inheritance, frozen / off / no-plot layers, attributes
//                     (visible, invisible, constant), MINSERT, anonymous block,
//                     hatch, wipeout, dimension inside a block, text/mtext,
//                     relative-path xref, overlay xref
//   master_missing.dwg  references an xref file that no longer exists
//   stress.dwg        <n> instances of an equipment block holding 10 sub-blocks
//                     of 100 entities (n = 1000 -> ~1,000,000 entities)

using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace DwgFlat
{
    public static class TestGen
    {
        public static List<string> Generate(string folder, int stressInstances, Progress prog)
        {
            if (string.IsNullOrEmpty(folder)) throw new ArgumentException("folder");
            SafeFs.AssertFreshFolder(folder);   // never writes into a folder that already has files
            List<string> made = new List<string>();
            Database prev = HostApplicationServices.WorkingDatabase;
            try
            {
                string nested = Path.Combine(folder, "nested_xref.dwg");
                string ghost = Path.Combine(folder, "ghost_overlay.dwg");
                string child = Path.Combine(folder, "child_xref.dwg");
                string overlay = Path.Combine(folder, "overlay_xref.dwg");
                string master = Path.Combine(folder, "master.dwg");
                string missing = Path.Combine(folder, "master_missing.dwg");
                string gone = Path.Combine(folder, "deleted_xref.dwg");
                string stress = Path.Combine(folder, "stress.dwg");

                prog.Stage("nested_xref / ghost_overlay / overlay_xref");
                Simple(nested, "NX", 3, new Point3d(0, 0, 0), 15, prev); made.Add(nested);
                Simple(ghost, "GHOST", 6, new Point3d(0, 0, 0), 400, prev); made.Add(ghost);
                Simple(overlay, "OVL", 4, new Point3d(0, 0, 0), 25, prev); made.Add(overlay);

                prog.Stage("child_xref");
                using (SideDb sd = NewDb(prev))
                using (Transaction tr = sd.Db.TransactionManager.StartTransaction())
                {
                    Database db = sd.Db;
                    BlockTableRecord ms = Ms(db, tr);
                    ObjectId lay = Layer(db, tr, "CX-PIPE", 2, false, false, true);
                    ObjectId inner = Block(db, tr, "CX_INNER", delegate (BlockTableRecord b)
                    {
                        Add(b, tr, new Line(new Point3d(0, 0, 0), new Point3d(10, 0, 0)));
                        Add(b, tr, new Circle(new Point3d(5, 5, 0), Vector3d.ZAxis, 3));
                    });
                    ObjectId outer = Block(db, tr, "CX_OUTER", delegate (BlockTableRecord b)
                    {
                        Ins(b, tr, inner, new Point3d(0, 0, 0), 0, new Scale3d(1));
                        Ins(b, tr, inner, new Point3d(20, 0, 0), Math.PI / 6, new Scale3d(1.5));
                        Entity t = new DBText(); ((DBText)t).TextString = "CHILD"; ((DBText)t).Height = 2; ((DBText)t).Position = new Point3d(0, 15, 0);
                        Add(b, tr, t);
                    });
                    BlockReference r1 = Ins(ms, tr, outer, new Point3d(0, 0, 0), 0, new Scale3d(1)); r1.LayerId = lay;
                    Ins(ms, tr, outer, new Point3d(60, 0, 0), Math.PI / 2, new Scale3d(1));
                    tr.Commit();
                    db.AttachXref(nested, "NESTED_X");
                    db.OverlayXref(ghost, "GHOST_OVL");
                    InsertXref(db, "NESTED_X", new Point3d(100, 0, 0), 0.3, "nested_xref.dwg");
                    InsertXref(db, "GHOST_OVL", new Point3d(0, 0, 0), 0, "ghost_overlay.dwg");
                    Save(db, child);
                }
                made.Add(child);

                prog.Stage("master");
                BuildMaster(master, child, overlay, prev);
                made.Add(master);

                prog.Stage("master_missing");
                Simple(gone, "GONE", 1, Point3d.Origin, 10, prev);
                using (SideDb sd = NewDb(prev))
                {
                    Database db = sd.Db;
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        Add(Ms(db, tr), tr, new Line(Point3d.Origin, new Point3d(100, 100, 0)));
                        tr.Commit();
                    }
                    db.AttachXref(gone, "GONE_X");
                    InsertXref(db, "GONE_X", Point3d.Origin, 0, "deleted_xref.dwg");
                    Save(db, missing);
                }
                SafeFs.Delete(gone);   // created (and recorded) above, in the fresh folder
                made.Add(missing);

                if (stressInstances > 0)
                {
                    prog.Stage("stress (" + stressInstances + " instances)");
                    BuildStress(stress, stressInstances, prev);
                    made.Add(stress);
                }

                // expected outcomes for the app's self-test
                List<object> cases = new List<object>();
                cases.Add(Case("master.dwg", "FULL_FLAT", "PRESERVE_LAYERS", false, true, "Full flat, layers preserved"));
                cases.Add(Case("master.dwg", "FULL_FLAT", "SINGLE_LAYER", false, true, "Full flat, single layer"));
                cases.Add(Case("master.dwg", "OPTIMIZED", "PRESERVE_LAYERS", false, true, "Optimized, layers preserved"));
                cases.Add(Case("master.dwg", "OPTIMIZED", "SINGLE_LAYER", false, true, "Optimized, single layer"));
                cases.Add(Case("master_missing.dwg", "FULL_FLAT", "PRESERVE_LAYERS", false, false, "Missing xref must FAIL and publish nothing"));
                cases.Add(Case("master_missing.dwg", "FULL_FLAT", "PRESERVE_LAYERS", true, true, "Missing xref allowed: publishes with warning"));
                if (stressInstances > 0)
                {
                    cases.Add(Case("stress.dwg", "FULL_FLAT", "PRESERVE_LAYERS", false, true, "Stress: full flat"));
                    cases.Add(Case("stress.dwg", "OPTIMIZED", "PRESERVE_LAYERS", false, true, "Stress: optimized"));
                }
                Dictionary<string, object> man = new Dictionary<string, object>();
                man["generatedAt"] = DateTime.Now.ToString("s");
                man["cases"] = cases;
                man["notes"] = "ghost_overlay.dwg is overlaid inside child_xref.dwg, so it must NOT appear in master's output (big magenta circle radius 400). Dynamic blocks cannot be authored through the API; test those with a real drawing.";
                string mpath = Path.Combine(folder, "manifest.json");
                SafeFs.WriteText(mpath, Report.ToJson(man));
                made.Add(mpath);
            }
            finally
            {
                HostApplicationServices.WorkingDatabase = prev;
            }
            return made;
        }

        static Dictionary<string, object> Case(string file, string mode, string layers, bool allowMissing, bool expectOk, string label)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["file"] = file; d["mode"] = mode; d["layerPolicy"] = layers; d["allowMissingXrefs"] = allowMissing;
            d["expectOk"] = expectOk; d["label"] = label;
            return d;
        }

        // ---------------------------------------------------------------- master
        static void BuildMaster(string path, string child, string overlay, Database prev)
        {
            using (SideDb sd = NewDb(prev))
            {
                Database db = sd.Db;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord ms = Ms(db, tr);
                    ObjectId dashed = db.ContinuousLinetype;
                    try { db.LoadLineTypeFile("DASHED", "acad.lin"); LinetypeTable ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead); if (ltt.Has("DASHED")) dashed = ltt["DASHED"]; } catch { }
                    ObjectId red = Layer(db, tr, "L-RED", 1, false, false, true);
                    ObjectId blue = Layer(db, tr, "L-BLUE", 5, false, false, true);
                    ObjectId green = Layer(db, tr, "L-GREEN", 3, false, false, true);
                    ObjectId frozen = Layer(db, tr, "L-FROZEN", 6, true, false, true);
                    ObjectId off = Layer(db, tr, "L-OFF", 4, false, true, true);
                    ObjectId noplot = Layer(db, tr, "L-NOPLOT", 8, false, false, false);
                    LayerTableRecord blueRec = (LayerTableRecord)tr.GetObject(blue, OpenMode.ForWrite);
                    blueRec.LinetypeObjectId = dashed;
                    blueRec.LineWeight = LineWeight.LineWeight050;

                    ObjectId leaf = Block(db, tr, "LEAF", delegate (BlockTableRecord b)
                    {
                        Add(b, tr, new Line(new Point3d(0, 0, 0), new Point3d(10, 0, 0)));                       // layer 0, ByLayer
                        Arc a = new Arc(new Point3d(5, 0, 0), 5, 0, Math.PI); a.ColorIndex = 0; Add(b, tr, a);      // ByBlock colour
                        Circle c = new Circle(new Point3d(5, 3, 0), Vector3d.ZAxis, 1.5); c.LayerId = red; Add(b, tr, c);
                        Ellipse el = new Ellipse(new Point3d(5, -4, 0), Vector3d.ZAxis, new Vector3d(3, 0, 0), 0.5, 0, Math.PI * 2); el.ColorIndex = 30; Add(b, tr, el);
                        Point3dCollection fit = new Point3dCollection(new Point3d[] { new Point3d(0, -8, 0), new Point3d(3, -6, 0), new Point3d(7, -9, 0), new Point3d(10, -7, 0) });
                        Spline sp = new Spline(fit, 3, 0); sp.LinetypeId = db.ByBlockLinetype; Add(b, tr, sp);
                        Polyline pl = new Polyline();
                        pl.AddVertexAt(0, new Point2d(0, 5), 0.5, 0, 0); pl.AddVertexAt(1, new Point2d(4, 5), 0, 0, 0); pl.AddVertexAt(2, new Point2d(4, 8), 0, 0, 0);
                        pl.ColorIndex = 0; Add(b, tr, pl);
                        Polyline wp = new Polyline();
                        wp.AddVertexAt(0, new Point2d(6, 5), 0, 0, 0); wp.AddVertexAt(1, new Point2d(10, 5), 0, 0, 0); wp.ConstantWidth = 0.3; wp.LayerId = blue; Add(b, tr, wp);
                        Polyline box = new Polyline();
                        box.AddVertexAt(0, new Point2d(11, 0), 0, 0, 0); box.AddVertexAt(1, new Point2d(14, 0), 0, 0, 0);
                        box.AddVertexAt(2, new Point2d(14, 3), 0, 0, 0); box.AddVertexAt(3, new Point2d(11, 3), 0, 0, 0); box.Closed = true;
                        ObjectId boxId = Add(b, tr, box);
                        try
                        {
                            Hatch h = new Hatch(); h.SetDatabaseDefaults(db); h.ColorIndex = 2; Add(b, tr, h);
                            h.Associative = false;
                            h.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
                            h.AppendLoop(HatchLoopTypes.Outermost, new ObjectIdCollection(new ObjectId[] { boxId }));
                            h.EvaluateHatch(true);
                        }
                        catch { }
                        DBText t = new DBText(); t.TextString = "LEAF"; t.Height = 1.2; t.HorizontalMode = TextHorizontalMode.TextCenter; t.VerticalMode = TextVerticalMode.TextVerticalMid;
                        t.AlignmentPoint = new Point3d(5, 10, 0); t.Position = new Point3d(5, 10, 0); t.Oblique = 0.2; t.ColorIndex = 0; Add(b, tr, t);
                        try { t.AdjustAlignment(db); } catch { }
                        MText mt = new MText(); mt.Contents = "M-TEXT\\PLINE 2"; mt.Location = new Point3d(0, 14, 0); mt.TextHeight = 0.8; mt.Width = 8; Add(b, tr, mt);
                        try
                        {
                            Wipeout w = new Wipeout(); w.SetDatabaseDefaults(db);
                            w.SetFrom(new Point2dCollection(new Point2d[] { new Point2d(15, 0), new Point2d(18, 0), new Point2d(18, 2), new Point2d(15, 2), new Point2d(15, 0) }), Vector3d.ZAxis);
                            Add(b, tr, w);
                        }
                        catch { }
                        Add(b, tr, new DBPoint(new Point3d(10, 10, 0)));
                        Line hidden = new Line(new Point3d(0, 0, 0), new Point3d(0, 20, 0)); hidden.Visible = false; Add(b, tr, hidden);
                    });

                    ObjectId mid = Block(db, tr, "MID", delegate (BlockTableRecord b)
                    {
                        Ins(b, tr, leaf, new Point3d(0, 0, 0), 0, new Scale3d(1));
                        Ins(b, tr, leaf, new Point3d(25, 0, 0), Math.PI / 4, new Scale3d(1));
                        Ins(b, tr, leaf, new Point3d(50, 0, 0), 0, new Scale3d(-1, 1, 1));       // mirrored
                        Ins(b, tr, leaf, new Point3d(75, 0, 0), 0, new Scale3d(2));               // uniform x2
                        BlockReference fz = Ins(b, tr, leaf, new Point3d(0, 30, 0), 0, new Scale3d(1)); fz.LayerId = frozen;   // hidden
                        BlockReference bb = Ins(b, tr, leaf, new Point3d(25, 30, 0), 0, new Scale3d(1)); bb.ColorIndex = 0;    // ByBlock chain
                        AttributeDefinition cad = new AttributeDefinition(new Point3d(0, -15, 0), "CONSTANT VALUE", "CONST", "", db.Textstyle);
                        cad.Constant = true; cad.Height = 1.5; Add(b, tr, cad);
                        AttributeDefinition nad = new AttributeDefinition(new Point3d(0, -18, 0), "default-not-shown", "NOTSHOWN", "", db.Textstyle);
                        nad.Height = 1.5; Add(b, tr, nad);   // not constant: a block reference without its attribute shows nothing
                        try
                        {
                            AlignedDimension dim = new AlignedDimension(new Point3d(0, -10, 0), new Point3d(40, -10, 0), new Point3d(20, -13, 0), "", db.Dimstyle);
                            dim.ColorIndex = 0;
                            Add(b, tr, dim);
                        }
                        catch { }
                    });

                    ObjectId top = Block(db, tr, "TOP", delegate (BlockTableRecord b)
                    {
                        Ins(b, tr, mid, new Point3d(0, 0, 0), 0, new Scale3d(1));
                        Ins(b, tr, mid, new Point3d(0, 60, 0), Math.PI / 6, new Scale3d(2, 1, 1));   // NON-UNIFORM + rotated children
                        BlockReference r = Ins(b, tr, mid, new Point3d(120, 0, 0), 0, new Scale3d(1)); r.ColorIndex = 0;
                        BlockReference o = Ins(b, tr, leaf, new Point3d(120, 60, 0), 0, new Scale3d(1)); o.LayerId = off;
                        BlockReference np = Ins(b, tr, leaf, new Point3d(150, 60, 0), 0, new Scale3d(1)); np.LayerId = noplot;
                    });

                    // five-level chain
                    ObjectId lower = Block(db, tr, "CHAIN5", delegate (BlockTableRecord b)
                    {
                        DBText t = new DBText(); t.TextString = "LEVEL 5"; t.Height = 2; t.Position = new Point3d(0, 3, 0); Add(b, tr, t);
                        Add(b, tr, new Circle(Point3d.Origin, Vector3d.ZAxis, 2));
                    });
                    for (int lvl = 4; lvl >= 1; lvl--)
                    {
                        ObjectId child2 = lower;
                        int L = lvl;
                        lower = Block(db, tr, "CHAIN" + lvl, delegate (BlockTableRecord b)
                        {
                            Add(b, tr, new Line(Point3d.Origin, new Point3d(10, 0, 0)));
                            Circle c = new Circle(new Point3d(10, 0, 0), Vector3d.ZAxis, 1); c.ColorIndex = L; Add(b, tr, c);
                            Ins(b, tr, child2, new Point3d(10, 5, 0), Math.PI / 12, new Scale3d(L % 2 == 0 ? -1.1 : 1.1, 1.1, 1.1));
                        });
                    }

                    ObjectId att = Block(db, tr, "TAGGED", delegate (BlockTableRecord b)
                    {
                        Add(b, tr, new Circle(Point3d.Origin, Vector3d.ZAxis, 5));
                        AttributeDefinition a1 = new AttributeDefinition(new Point3d(-4, -1, 0), "DEF", "TAG", "Tag?", db.Textstyle); a1.Height = 2; Add(b, tr, a1);
                        AttributeDefinition a2 = new AttributeDefinition(new Point3d(-4, -4, 0), "SECRET", "HIDDEN", "Hidden?", db.Textstyle); a2.Height = 2; a2.Invisible = true; Add(b, tr, a2);
                    });

                    ObjectId anon;
                    {
                        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
                        BlockTableRecord ab = new BlockTableRecord(); ab.Name = "*U";
                        anon = bt.Add(ab); tr.AddNewlyCreatedDBObject(ab, true);
                        Add(ab, tr, new Line(Point3d.Origin, new Point3d(5, 5, 0)));
                        Add(ab, tr, new Circle(new Point3d(5, 5, 0), Vector3d.ZAxis, 1));
                    }

                    BlockReference t1 = Ins(ms, tr, top, new Point3d(0, 0, 0), 0, new Scale3d(1)); t1.LayerId = green; t1.ColorIndex = 1;
                    Ins(ms, tr, top, new Point3d(600, 0, 0), Math.PI / 2, new Scale3d(-1, 1, 1));
                    Ins(ms, tr, top, new Point3d(0, 400, 0), 0, new Scale3d(0.5));
                    Ins(ms, tr, top, new Point3d(600, 400, 0), Math.PI / 9, new Scale3d(1, 3, 1));        // non-uniform at the top
                    BlockReference tf = Ins(ms, tr, top, new Point3d(1200, 0, 0), 0, new Scale3d(1)); tf.LayerId = frozen;
                    Ins(ms, tr, lower, new Point3d(1200, 400, 0), 0.3, new Scale3d(2));
                    Ins(ms, tr, anon, new Point3d(1400, 400, 0), 0, new Scale3d(3));

                    for (int i = 0; i < 3; i++)
                    {
                        BlockReference ar = Ins(ms, tr, att, new Point3d(1400 + i * 20, 0, 0), i * 0.4, new Scale3d(1));
                        BlockTableRecord ab = (BlockTableRecord)tr.GetObject(att, OpenMode.ForRead);
                        foreach (ObjectId aid in ab)
                        {
                            AttributeDefinition ad = tr.GetObject(aid, OpenMode.ForRead) as AttributeDefinition;
                            if (ad == null) continue;
                            AttributeReference rf = new AttributeReference();
                            rf.SetAttributeFromBlock(ad, ar.BlockTransform);
                            rf.TextString = ad.Tag == "TAG" ? "P-10" + i : "hidden-" + i;
                            ar.AttributeCollection.AppendAttribute(rf);
                            tr.AddNewlyCreatedDBObject(rf, true);
                        }
                    }

                    MInsertBlock mi = new MInsertBlock(new Point3d(1400, 100, 0), leaf, 2, 3, 30, 40);
                    mi.Rotation = 0.2;
                    ms.AppendEntity(mi); tr.AddNewlyCreatedDBObject(mi, true);

                    Add(ms, tr, new Line(new Point3d(-50, -50, 0), new Point3d(1500, -50, 0)));
                    DBText title = new DBText(); title.TextString = "DWG FLAT TEST MASTER"; title.Height = 10; title.Position = new Point3d(0, -80, 0); Add(ms, tr, title);
                    try { Add(ms, tr, new AlignedDimension(new Point3d(0, -100, 0), new Point3d(300, -100, 0), new Point3d(150, -110, 0), "", db.Dimstyle)); } catch { }
                    tr.Commit();
                }
                db.AttachXref(child, "CHILD_X");
                db.OverlayXref(overlay, "OVERLAY_X");
                InsertXref(db, "CHILD_X", new Point3d(0, -400, 0), Math.PI / 18, "child_xref.dwg");
                InsertXref(db, "OVERLAY_X", new Point3d(600, -400, 0), 0, "overlay_xref.dwg");
                Save(db, path);
            }
        }

        // ---------------------------------------------------------------- stress
        static void BuildStress(string path, int instances, Database prev)
        {
            using (SideDb sd = NewDb(prev))
            {
                Database db = sd.Db;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord ms = Ms(db, tr);
                    List<ObjectId> subs = new List<ObjectId>();
                    for (int s = 0; s < 10; s++)
                    {
                        int S = s;
                        subs.Add(Block(db, tr, "SUB" + s, delegate (BlockTableRecord b)
                        {
                            for (int k = 0; k < 100; k++)
                            {
                                double x = (k % 10) * 2, y = (k / 10) * 2;
                                switch (k % 4)
                                {
                                    case 0: Add(b, tr, new Line(new Point3d(x, y, 0), new Point3d(x + 1.5, y + 0.5, 0))); break;
                                    case 1: Add(b, tr, new Circle(new Point3d(x, y, 0), Vector3d.ZAxis, 0.4 + 0.01 * S)); break;
                                    case 2: Add(b, tr, new Arc(new Point3d(x, y, 0), 0.6, 0, 2.0)); break;
                                    default:
                                        Polyline p = new Polyline();
                                        p.AddVertexAt(0, new Point2d(x, y), 0, 0, 0); p.AddVertexAt(1, new Point2d(x + 1, y), 0.3, 0, 0); p.AddVertexAt(2, new Point2d(x + 1, y + 1), 0, 0, 0);
                                        Add(b, tr, p); break;
                                }
                            }
                        }));
                    }
                    ObjectId equip = Block(db, tr, "EQUIPMENT", delegate (BlockTableRecord b)
                    {
                        for (int s = 0; s < subs.Count; s++)
                            Ins(b, tr, subs[s], new Point3d((s % 5) * 25, (s / 5) * 25, 0), s * 0.1, new Scale3d(1));
                    });
                    int side = (int)Math.Ceiling(Math.Sqrt(instances));
                    for (int i = 0; i < instances; i++)
                        Ins(ms, tr, equip, new Point3d((i % side) * 150, (i / side) * 80, 0), (i % 8) * Math.PI / 4, new Scale3d(1));
                    tr.Commit();
                }
                Save(db, path);
            }
        }

        // ---------------------------------------------------------------- helpers
        static void Simple(string path, string tag, short color, Point3d at, double r, Database prev)
        {
            using (SideDb sd = NewDb(prev))
            {
                Database db = sd.Db;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ObjectId lay = Layer(db, tr, tag + "-LAYER", color, false, false, true);
                    BlockTableRecord ms = Ms(db, tr);
                    Circle c = new Circle(at, Vector3d.ZAxis, r); c.LayerId = lay; Add(ms, tr, c);
                    Line l = new Line(at, at + new Vector3d(r, r, 0)); l.LayerId = lay; Add(ms, tr, l);
                    DBText t = new DBText(); t.TextString = tag; t.Height = Math.Max(1, r / 5); t.Position = at; t.LayerId = lay; Add(ms, tr, t);
                    tr.Commit();
                }
                Save(db, path);
            }
        }

        static void Save(Database db, string path)
        {
            SafeFs.SaveDatabase(db, path);
            SafeFs.RecordCreated(path);
        }

        static SideDb NewDb(Database prev)
        {
            return new SideDb(true, true);
        }

        static void InsertXref(Database db, string name, Point3d at, double rot, string relativePath)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                ObjectId xid = bt[name];
                BlockTableRecord x = (BlockTableRecord)tr.GetObject(xid, OpenMode.ForWrite);
                x.PathName = relativePath;   // relative to the host drawing's folder
                BlockReference r = new BlockReference(at, xid);
                r.Rotation = rot;
                Ms(db, tr).AppendEntity(r);
                tr.AddNewlyCreatedDBObject(r, true);
                tr.Commit();
            }
        }

        delegate void Fill(BlockTableRecord b);

        static ObjectId Block(Database db, Transaction tr, string name, Fill fill)
        {
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
            BlockTableRecord b = new BlockTableRecord();
            b.Name = name;
            ObjectId id = bt.Add(b);
            tr.AddNewlyCreatedDBObject(b, true);
            fill(b);
            return id;
        }

        static BlockTableRecord Ms(Database db, Transaction tr)
        {
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            return (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
        }

        static ObjectId Add(BlockTableRecord b, Transaction tr, Entity e)
        {
            ObjectId id = b.AppendEntity(e);
            tr.AddNewlyCreatedDBObject(e, true);
            return id;
        }

        static BlockReference Ins(BlockTableRecord owner, Transaction tr, ObjectId block, Point3d at, double rot, Scale3d sc)
        {
            BlockReference r = new BlockReference(at, block);
            r.SetDatabaseDefaults(owner.Database);
            r.Rotation = rot;
            r.ScaleFactors = sc;
            owner.AppendEntity(r);
            tr.AddNewlyCreatedDBObject(r, true);
            return r;
        }

        static ObjectId Layer(Database db, Transaction tr, string name, short color, bool frozen, bool off, bool plot)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
            if (lt.Has(name)) return lt[name];
            LayerTableRecord l = new LayerTableRecord();
            l.Name = name;
            l.Color = Color.FromColorIndex(ColorMethod.ByAci, color);
            l.IsFrozen = frozen;
            l.IsOff = off;
            l.IsPlottable = plot;
            ObjectId id = lt.Add(l);
            tr.AddNewlyCreatedDBObject(l, true);
            return id;
        }
    }
}
