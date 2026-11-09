using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bricscad.ApplicationServices;
using Bricscad.EditorInput;
using CsdBridge.Core.Civil;
using CsdBridge.Core.Drawing;
using Teigha.Colors;
using Teigha.DatabaseServices;
using Teigha.Geometry;

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// AIPIPEHATCH: hatches the sewer and stormwater pipes of Civil Site Design long sections.
    ///
    /// Each long section is a CSD drawing xref'd into its own layout. For every layout whose
    /// name marks it as a long section sheet, the pipe runs are found in the xref's linework
    /// (<see cref="LongSectionPipes"/>) and one hatch covering all of them is added to the
    /// layout, on the service's hatch layer. The hatch can't follow the xref, so the command
    /// replaces its own earlier hatches (marked with xdata) each time it runs.
    /// </summary>
    internal static class PipeHatcher
    {
        /// <summary>Xdata application name marking the hatches this command made.</summary>
        private const string AppName = "CSDBRIDGE_PIPEHATCH";

        private const int MaxNesting = 6;
        private const int MaxTexts = 5000;

        private enum Scope
        {
            All,
            Current,
            Remove,
        }

        public static void Run(Document doc)
        {
            Editor ed = doc.Editor;
            Database db = doc.Database;

            BridgeSettings bridge = BridgeSettings.Load();
            LongSectionHatchSettings settings = bridge.LoadLongSectionHatch(out string? settingsNote);
            if (settingsNote != null)
            {
                ed.WriteMessage($"\nCSD-Bridge: {settingsNote}; using the built-in pipe hatch settings.");
            }

            var options = new PromptKeywordOptions("\nHatch long section pipes in");
            options.Keywords.Add("All");
            options.Keywords.Add("Current");
            options.Keywords.Add("Remove");
            options.Keywords.Default = "All";
            options.AllowNone = true;
            PromptResult res = ed.GetKeywords(options);
            if (res.Status != PromptStatus.OK && res.Status != PromptStatus.None)
            {
                return;
            }

            Scope scope = res.Status == PromptStatus.OK && Enum.TryParse(res.StringResult, true, out Scope chosen) ? chosen : Scope.All;

            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                List<Layout> layouts = Layouts(tr, db);
                List<Layout> targets;
                if (scope == Scope.Current)
                {
                    var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                    targets = layouts.Where(l => l.BlockTableRecordId == space.ObjectId).ToList();
                    if (targets.Count == 0)
                    {
                        ed.WriteMessage("\nCSD-Bridge: switch to the long section's layout first (the current space is model space).\n");
                        return;
                    }
                }
                else
                {
                    targets = layouts.Where(l => settings.IsLongSectionLayout(l.LayoutName)).ToList();
                    if (targets.Count == 0)
                    {
                        ed.WriteMessage(
                            $"\nCSD-Bridge: no layout has \"{string.Join("\" or \"", settings.LayoutNameContains)}\" in its name. " +
                            "Use the Current option to hatch the layout you are in.\n");
                        return;
                    }
                }

                int hatches = 0, pipes = 0, removed = 0;
                foreach (Layout layout in targets)
                {
                    try
                    {
                        string line = ProcessLayout(tr, db, ed, layout, settings, scope == Scope.Remove, ref hatches, ref pipes, ref removed);
                        ed.WriteMessage($"\n  {layout.LayoutName}: {line}");
                    }
                    catch (System.Exception ex)
                    {
                        ed.WriteMessage($"\n  {layout.LayoutName}: failed ({ex.Message}). Is the hatch layer locked?");
                    }
                }

                tr.Commit();
                ed.WriteMessage(scope == Scope.Remove
                    ? $"\nCSD-Bridge: removed {removed} pipe hatch(es) from {targets.Count} layout(s).\n"
                    : $"\nCSD-Bridge: hatched {pipes} pipe run(s) with {hatches} hatch(es) in {targets.Count} layout(s)" +
                      (removed > 0 ? $", replacing {removed} earlier hatch(es)" : "") + ". One UNDO takes it back.\n");
            }
        }

        private static List<Layout> Layouts(Transaction tr, Database db)
        {
            var result = new List<Layout>();
            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in dict)
            {
                if (tr.GetObject(entry.Value, OpenMode.ForRead) is Layout layout && !layout.ModelType)
                {
                    result.Add(layout);
                }
            }

            return result.OrderBy(l => l.TabOrder).ToList();
        }

        private static string ProcessLayout(
            Transaction tr, Database db, Editor ed, Layout layout, LongSectionHatchSettings settings, bool removeOnly,
            ref int hatches, ref int pipes, ref int removed)
        {
            var space = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            bool appKnown = ((RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead)).Has(AppName);

            var old = new List<ObjectId>();
            var xrefs = new List<BlockReference>();
            foreach (ObjectId id in space)
            {
                DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                if (obj is Hatch hatch)
                {
                    if (appKnown && hatch.GetXDataForApplication(AppName) != null)
                    {
                        old.Add(id);
                    }
                }
                else if (obj is BlockReference br
                    && tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) is BlockTableRecord def
                    && def.IsFromExternalReference)
                {
                    xrefs.Add(br);
                }
            }

            foreach (ObjectId id in old)
            {
                tr.GetObject(id, OpenMode.ForWrite).Erase();
            }

            removed += old.Count;
            if (removeOnly)
            {
                return old.Count == 0 ? "no pipe hatches to remove" : $"removed {old.Count} pipe hatch(es)";
            }

            if (xrefs.Count == 0)
            {
                return "no xref in this layout's paper space, nothing hatched";
            }

            var notes = new List<string>();
            int layoutPipes = 0;
            foreach (BlockReference br in xrefs)
            {
                var def = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                if (def.IsUnloaded)
                {
                    notes.Add($"xref {def.Name} is unloaded");
                    continue;
                }

                var content = new SectionContent();
                Collect(tr, def, Matrix3d.Identity, null, 0, content);
                List<PipeQuad> quads = LongSectionPipes.Find(content.Lines, settings.PipeLayers);
                if (quads.Count == 0)
                {
                    continue; // a title block or other xref, or a long section whose file wasn't found
                }

                List<string> pipeLayers = quads.Select(q => q.Layer).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                PipeService? service = LongSectionPipes.Classify(settings.Services, new IEnumerable<string>[]
                {
                    new[] { layout.LayoutName },
                    new[] { def.Name, SafeFileName(def.PathName) },
                    pipeLayers,
                    content.Texts,
                }) ?? AskService(ed, settings, layout.LayoutName, def.Name);

                if (service == null)
                {
                    notes.Add($"{def.Name} skipped");
                    continue;
                }

                AddHatch(tr, db, space, br, quads, service, settings);
                hatches++;
                layoutPipes += quads.Count;
                notes.Add($"{quads.Count} {service.Name.ToLowerInvariant()} pipe run(s) from {def.Name} (pipe lines on {string.Join(", ", pipeLayers)}) hatched on {service.HatchLayer}");
            }

            pipes += layoutPipes;
            if (layoutPipes == 0 && notes.Count == 0)
            {
                notes.Add("no pipes found in its xref(s). Check the xref is loaded, and pipe_layers in long-section-hatch.json");
            }

            return string.Join("; ", notes);
        }

        private static PipeService? AskService(Editor ed, LongSectionHatchSettings settings, string layoutName, string xrefName)
        {
            // Keywords can't hold spaces.
            var byKeyword = new Dictionary<string, PipeService>(StringComparer.OrdinalIgnoreCase);
            foreach (PipeService s in settings.Services)
            {
                string keyword = new string(s.Name.Where(char.IsLetterOrDigit).ToArray());
                if (keyword.Length > 0 && !byKeyword.ContainsKey(keyword) && !keyword.Equals("Skip", StringComparison.OrdinalIgnoreCase))
                {
                    byKeyword[keyword] = s;
                }
            }

            if (byKeyword.Count == 0)
            {
                return null;
            }

            var options = new PromptKeywordOptions($"\nCan't tell which service \"{xrefName}\" in layout \"{layoutName}\" is. Hatch its pipes as");
            foreach (string keyword in byKeyword.Keys)
            {
                options.Keywords.Add(keyword);
            }

            options.Keywords.Add("Skip");
            options.Keywords.Default = "Skip";
            options.AllowNone = true;
            PromptResult res = ed.GetKeywords(options);
            return res.Status == PromptStatus.OK && byKeyword.TryGetValue(res.StringResult, out PipeService? chosen) ? chosen : null;
        }

        // ------------------------------------------------------------- reading the xref

        private sealed class SectionContent
        {
            public List<SectionLine> Lines { get; } = new List<SectionLine>();
            public List<string> Texts { get; } = new List<string>();
            public Dictionary<ObjectId, (string Name, bool Shown)> LayerCache { get; } = new Dictionary<ObjectId, (string, bool)>();

            public void Add(Point3d a, Point3d b, Matrix3d toSection, string layer)
            {
                a = a.TransformBy(toSection);
                b = b.TransformBy(toSection);
                Lines.Add(new SectionLine(new Point2(a.X, a.Y), new Point2(b.X, b.Y), layer));
            }
        }

        /// <summary>
        /// The straight linework and the labels of a block definition, in its own coordinates,
        /// following nested blocks. Frozen and switched-off layers are left out.
        /// </summary>
        private static void Collect(Transaction tr, BlockTableRecord def, Matrix3d toSection, string? insertLayer, int depth, SectionContent content)
        {
            foreach (ObjectId id in def)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent) || !ent.Visible)
                {
                    continue;
                }

                if (!content.LayerCache.TryGetValue(ent.LayerId, out (string Name, bool Shown) layerInfo))
                {
                    var ltr = (LayerTableRecord)tr.GetObject(ent.LayerId, OpenMode.ForRead);
                    layerInfo = (LongSectionPipes.BareLayer(ltr.Name), !ltr.IsFrozen && !ltr.IsOff);
                    content.LayerCache[ent.LayerId] = layerInfo;
                }

                if (!layerInfo.Shown)
                {
                    continue;
                }

                // Linework on layer 0 inside a block takes the layer of the block's insert.
                string layer = insertLayer != null && layerInfo.Name == "0" ? insertLayer : layerInfo.Name;

                switch (ent)
                {
                    case Line line:
                        content.Add(line.StartPoint, line.EndPoint, toSection, layer);
                        break;

                    case Polyline pl:
                    {
                        int n = pl.NumberOfVertices;
                        int segments = pl.Closed ? n : n - 1;
                        for (int i = 0; i < segments; i++)
                        {
                            if (Math.Abs(pl.GetBulgeAt(i)) < 1e-9)
                            {
                                content.Add(pl.GetPoint3dAt(i), pl.GetPoint3dAt((i + 1) % n), toSection, layer);
                            }
                        }

                        break;
                    }

                    case Polyline2d _:
                    case Polyline3d _:
                    {
                        try
                        {
                            var curve = (Curve)ent;
                            int segments = (int)Math.Round(curve.EndParam);
                            Point3d previous = curve.GetPointAtParameter(0);
                            for (int i = 1; i <= segments; i++)
                            {
                                Point3d next = curve.GetPointAtParameter(i);
                                content.Add(previous, next, toSection, layer);
                                previous = next;
                            }
                        }
                        catch (System.Exception)
                        {
                            // An empty or degenerate polyline.
                        }

                        break;
                    }

                    case DBText text:
                        if (content.Texts.Count < MaxTexts)
                        {
                            content.Texts.Add(text.TextString);
                        }

                        break;

                    case MText mtext:
                        if (content.Texts.Count < MaxTexts)
                        {
                            content.Texts.Add(mtext.Text);
                        }

                        break;

                    case BlockReference nested:
                        if (depth < MaxNesting && tr.GetObject(nested.BlockTableRecord, OpenMode.ForRead) is BlockTableRecord nestedDef)
                        {
                            Collect(tr, nestedDef, toSection * nested.BlockTransform, layer, depth + 1, content);
                        }

                        break;
                }
            }
        }

        // ------------------------------------------------------------- drawing the hatch

        private static void AddHatch(
            Transaction tr, Database db, BlockTableRecord space, BlockReference xref, List<PipeQuad> quads,
            PipeService service, LongSectionHatchSettings settings)
        {
            EnsureLayer(tr, db, service);
            EnsureRegApp(tr, db);
            if (!space.IsWriteEnabled)
            {
                space.UpgradeOpen();
            }

            var hatch = new Hatch();
            hatch.SetDatabaseDefaults(db);
            hatch.Layer = service.HatchLayer;
            hatch.ColorIndex = 256; // ByLayer
            space.AppendEntity(hatch);
            tr.AddNewlyCreatedDBObject(hatch, true);

            try
            {
                hatch.SetHatchPattern(HatchPatternType.PreDefined, (settings.Pattern ?? "SOLID").ToUpperInvariant());
                Matrix3d toLayout = xref.BlockTransform;
                foreach (PipeQuad quad in quads)
                {
                    var pts = new Point2dCollection();
                    var bulges = new DoubleCollection();
                    // Hatch polyline loops repeat the first point at the end.
                    for (int i = 0; i <= quad.Corners.Count; i++)
                    {
                        Point2 c = quad.Corners[i % quad.Corners.Count];
                        Point3d p = new Point3d(c.X, c.Y, 0).TransformBy(toLayout);
                        pts.Add(new Point2d(p.X, p.Y));
                        bulges.Add(0);
                    }

                    hatch.AppendLoop(HatchLoopTypes.External | HatchLoopTypes.Polyline, pts, bulges);
                }

                hatch.EvaluateHatch(true);
                hatch.XData = new ResultBuffer(
                    new TypedValue(1001, AppName),
                    new TypedValue(1000, service.Name));
            }
            catch (System.Exception)
            {
                hatch.Erase(); // don't leave an empty hatch behind
                throw;
            }

            if (settings.SendToBack)
            {
                var order = (DrawOrderTable)tr.GetObject(space.DrawOrderTableId, OpenMode.ForWrite);
                order.MoveToBottom(new ObjectIdCollection { hatch.ObjectId });
            }
        }

        private static void EnsureLayer(Transaction tr, Database db, PipeService service)
        {
            var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (table.Has(service.HatchLayer))
            {
                return;
            }

            var layer = new LayerTableRecord { Name = service.HatchLayer };
            if (service.Rgb != null && service.Rgb.Length == 3)
            {
                layer.Color = Color.FromRgb((byte)service.Rgb[0], (byte)service.Rgb[1], (byte)service.Rgb[2]);
            }
            else if (service.Color is short index)
            {
                layer.Color = Color.FromColorIndex(ColorMethod.ByAci, index);
            }

            table.UpgradeOpen();
            table.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
        }

        private static void EnsureRegApp(Transaction tr, Database db)
        {
            var table = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (table.Has(AppName))
            {
                return;
            }

            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = AppName };
            table.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
        }

        private static string SafeFileName(string? path)
        {
            try
            {
                return string.IsNullOrEmpty(path) ? "" : Path.GetFileNameWithoutExtension(path);
            }
            catch (ArgumentException)
            {
                return "";
            }
        }
    }
}
