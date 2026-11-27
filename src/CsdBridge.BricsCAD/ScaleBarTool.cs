using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bricscad.ApplicationServices;
using Bricscad.EditorInput;
using CsdBridge.Core.Drawing;
using Teigha.DatabaseServices;
using Teigha.Geometry;

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// AISCALEBARS: puts a scale bar block on every layout for each viewport scale on it,
    /// stacked when a layout has several. Which block is for which scale, and where bars go,
    /// come from the office's scale bar file (<see cref="ScaleBarSettings"/>). Runs entirely
    /// in the add-in: no Claude call, so it costs nothing and gives the same result every time.
    ///
    /// Section sheets have no viewports: the sections are xrefs placed straight on the
    /// layout. Their scales come from the xrefs' scale text instead ("SCALES: HORIZONTAL
    /// 1:50 VERTICAL 1:20"), counting only text that lands on the sheet, in case one xref
    /// holds several sheets.
    ///
    /// Where text on a sheet states a horizontal and a vertical scale together and the office
    /// has a combined block for them, that one bar is used instead of two.
    ///
    /// A layout whose bars already match is left alone, so bars moved by hand stay put.
    /// Otherwise its old bars are replaced. The whole run is one UNDO step.
    /// </summary>
    internal static class ScaleBarTool
    {
        public static void Run(Document doc)
        {
            Database db = doc.Database;
            Editor ed = doc.Editor;
            ScaleBarSettings settings = BridgeSettings.Load().LoadScaleBars(out string path, out string? problem);
            if (problem != null)
            {
                ed.WriteMessage($"\nCSD-Bridge: could not read the scale bar file ({problem}). Fix it or run AISCALEBARSETUP.\n");
                return;
            }

            bool mmDrawing = db.Insunits == UnitsValue.Millimeters;

            var lines = new List<string>();
            int added = 0;
            int removed = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                Func<string, bool> isLocked = name => layers.Has(name) && ((LayerTableRecord)tr.GetObject(layers[name], OpenMode.ForRead)).IsLocked;

                var layerIds = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
                var hiddenLayers = new Dictionary<ObjectId, bool>();
                Func<ObjectId, bool> hidden = id =>
                {
                    if (!hiddenLayers.TryGetValue(id, out bool h))
                    {
                        var ltr = tr.GetObject(id, OpenMode.ForRead) as LayerTableRecord;
                        h = ltr != null && (ltr.IsOff || ltr.IsFrozen);
                        hiddenLayers[id] = h;
                    }

                    return h;
                };
                var textCache = new Dictionary<ObjectId, List<ScaleText>>();
                bool wantPairs = settings.HasCombined; // text on the sheet is only read for combined bars

                foreach (Layout layout in Layouts(db, tr))
                {
                    var space = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                    var viewports = new List<Viewport>();
                    var existing = new List<(BlockReference Bar, ScaleBarId Id)>();
                    var xrefs = new List<BlockReference>();
                    var onLayout = new List<ScaleText>(); // scale text in paper space, other than in the scale bars themselves
                    foreach (ObjectId id in space)
                    {
                        DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                        if (obj is Viewport vp)
                        {
                            viewports.Add(vp);
                            continue;
                        }

                        if (!(obj is Entity ent) || !ent.Visible || hidden(ent.LayerId))
                        {
                            // A hidden scale bar still counts as one to replace.
                            if (obj is BlockReference hiddenBar && settings.TryParseBlockName(BlockName(hiddenBar, tr), out ScaleBarId hiddenId))
                            {
                                existing.Add((hiddenBar, hiddenId));
                            }

                            continue;
                        }

                        switch (ent)
                        {
                            case DBText text when wantPairs:
                                AddScaleText(onLayout, text.TextString, text.Position);
                                break;

                            case MText mtext when wantPairs:
                                AddScaleText(onLayout, mtext.Text, mtext.Location);
                                break;

                            case BlockReference br:
                                var def = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                                if (def.IsFromExternalReference)
                                {
                                    xrefs.Add(br);
                                }
                                else if (settings.TryParseBlockName(BlockName(br, tr), out ScaleBarId barId))
                                {
                                    existing.Add((br, barId));
                                    break; // its own lettering is not a statement about the sheet
                                }

                                if (wantPairs)
                                {
                                    onLayout.AddRange(TextOf(br, tr, textCache, hidden));
                                }

                                break;
                        }
                    }

                    // The first viewport is the layout's own paper-space view, not a view of the model.
                    var found = new LayoutScales(new SheetInfo(layout.LayoutName, PaperSize(layout).Width, PaperSize(layout).Height)) { MillimetreDrawing = mmDrawing };
                    foreach (Viewport vp in viewports.Skip(1))
                    {
                        found.Viewports.Add((vp.CustomScale, $"viewport {vp.Handle}"));
                    }

                    // No views of the model: a section sheet. Use the scale text in its xrefs, counting
                    // only what lands on the sheet, in case one xref holds several sheets.
                    Func<Point3d, bool> onSheet = SheetTest(layout, settings);
                    int offSheet = 0;
                    foreach (BlockReference xref in found.Viewports.Count == 0 ? xrefs : new List<BlockReference>())
                    {
                        foreach (ScaleText text in TextIn(xref.BlockTableRecord, tr, textCache, hidden, 0))
                        {
                            if (onSheet(text.Position.TransformBy(xref.BlockTransform)))
                            {
                                found.SectionScales.AddRange(text.Scales);
                            }
                            else
                            {
                                offSheet++;
                            }
                        }
                    }

                    foreach (ScaleText text in onLayout)
                    {
                        if (text.Pair.HasValue && onSheet(text.Position))
                        {
                            found.Pairs.Add(text.Pair.Value);
                        }
                    }

                    ScaleBarPlan plan = ScaleBars.Plan(found, settings, blocks.Has);
                    var problems = plan.Problems.ToList();
                    if (plan.FromSectionText && found.SectionScales.Count == 0 && offSheet > 0)
                    {
                        problems.Add("its xrefs' scale text is all outside the sheet");
                    }

                    string name = layout.LayoutName;
                    string source = plan.FromSectionText ? " (from section scale text)" : "";
                    IReadOnlyList<ScaleBarPlacement> placements = plan.Placements;
                    string issues = problems.Count > 0 ? " (" + string.Join("; ", problems) + ")" : "";
                    if (placements.Count == 0)
                    {
                        string why = viewports.Count > 1 || problems.Count > 0 ? "nothing placed"
                            : xrefs.Count == 0 ? "no viewports or xrefs"
                            : "no viewports, and no scale text found in its xrefs";
                        lines.Add($"{name}: {why}, left as is{issues}");
                        continue;
                    }

                    string list = string.Join(", ", placements.Select(p => p.Bar.ToString()));
                    bool upToDate = existing.Select(e => e.Id).OrderBy(b => b).SequenceEqual(placements.Select(p => p.Bar).OrderBy(b => b));
                    if (upToDate)
                    {
                        lines.Add($"{name}: {list} already there{source}{issues}");
                        continue;
                    }

                    string? locked = existing.Select(e => e.Bar.Layer).Concat(placements.Select(p => p.Layer)).FirstOrDefault(isLocked);
                    if (locked != null)
                    {
                        lines.Add($"{name}: skipped, layer {locked} is locked{issues}");
                        continue;
                    }

                    foreach (var (bar, _) in existing)
                    {
                        bar.UpgradeOpen();
                        bar.Erase();
                        removed++;
                    }

                    space.UpgradeOpen();
                    foreach (ScaleBarPlacement p in placements)
                    {
                        if (!layerIds.TryGetValue(p.Layer, out ObjectId layerId))
                        {
                            layerIds[p.Layer] = layerId = GetOrCreateLayer(layers, p.Layer, tr);
                        }

                        Insert(db, space, blocks[p.BlockName], layerId, p, tr);
                        added++;
                    }

                    lines.Add($"{name}: {list}{source}{(existing.Count > 0 ? " (replaced old bars)" : "")}{issues}");
                }

                tr.Commit();
            }

            ed.WriteMessage("\nCSD-Bridge scale bars");
            foreach (string line in lines)
            {
                ed.WriteMessage("\n  " + line);
            }

            ed.WriteMessage(lines.Count == 0
                ? "\n  This drawing has no layouts.\n"
                : $"\n  {added} added, {removed} removed. One UNDO reverts all of it. Settings: {path} (AISCALEBARSETUP changes them)\n");
        }

        /// <summary>A line of text stating scales, and where it is in its block's coordinates.</summary>
        private sealed class ScaleText
        {
            public ScaleText(IReadOnlyList<int> scales, (int Horizontal, int Vertical)? pair, Point3d position)
            {
                Scales = scales;
                Pair = pair;
                Position = position;
            }

            public IReadOnlyList<int> Scales { get; }

            /// <summary>A horizontal and a vertical scale stated together, if the text does that.</summary>
            public (int Horizontal, int Vertical)? Pair { get; }

            public Point3d Position { get; }
        }

        /// <summary>
        /// Visible text in a block (an xref's drawing, or a block inside it) that states scales.
        /// Cached per block, since many layouts often show the same xref.
        /// </summary>
        private static List<ScaleText> TextIn(ObjectId blockId, Transaction tr, Dictionary<ObjectId, List<ScaleText>> cache, Func<ObjectId, bool> hidden, int depth)
        {
            if (cache.TryGetValue(blockId, out List<ScaleText>? known))
            {
                return known;
            }

            var found = new List<ScaleText>();
            cache[blockId] = found; // also stops a block that contains itself
            if (depth > 4)
            {
                return found;
            }

            var block = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
            foreach (ObjectId id in block)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent) || !ent.Visible || hidden(ent.LayerId))
                {
                    continue;
                }

                switch (ent)
                {
                    case AttributeDefinition _:
                        break; // a template; its inserts' attributes are read below

                    case DBText text:
                        AddScaleText(found, text.TextString, text.Position);
                        break;

                    case MText mtext:
                        AddScaleText(found, mtext.Text, mtext.Location);
                        break;

                    case BlockReference br:
                        foreach (ScaleText inner in TextIn(br.BlockTableRecord, tr, cache, hidden, depth + 1))
                        {
                            found.Add(new ScaleText(inner.Scales, inner.Pair, inner.Position.TransformBy(br.BlockTransform)));
                        }

                        foreach (ObjectId attId in br.AttributeCollection)
                        {
                            if (tr.GetObject(attId, OpenMode.ForRead) is AttributeReference att && !att.Invisible && !hidden(att.LayerId))
                            {
                                AddScaleText(found, att.TextString, att.Position);
                            }
                        }

                        break;
                }
            }

            return found;
        }

        /// <summary>The scale text of a block placed on a layout (a title block, an xref), in the layout's coordinates.</summary>
        private static IEnumerable<ScaleText> TextOf(BlockReference br, Transaction tr, Dictionary<ObjectId, List<ScaleText>> cache, Func<ObjectId, bool> hidden)
        {
            foreach (ScaleText inner in TextIn(br.BlockTableRecord, tr, cache, hidden, 0))
            {
                yield return new ScaleText(inner.Scales, inner.Pair, inner.Position.TransformBy(br.BlockTransform));
            }

            var own = new List<ScaleText>();
            foreach (ObjectId attId in br.AttributeCollection)
            {
                if (tr.GetObject(attId, OpenMode.ForRead) is AttributeReference att && !att.Invisible && !hidden(att.LayerId))
                {
                    AddScaleText(own, att.TextString, att.Position); // attributes are already in the layout's coordinates
                }
            }

            foreach (ScaleText text in own)
            {
                yield return text;
            }
        }

        private static void AddScaleText(List<ScaleText> found, string? text, Point3d position)
        {
            IReadOnlyList<int> scales = ScaleBars.ScalesInText(text);
            (int Horizontal, int Vertical)? pair = ScaleBars.PairInText(text);
            if (scales.Count > 0 || pair.HasValue)
            {
                found.Add(new ScaleText(scales, pair, position));
            }
        }

        /// <summary>The layout's paper size in millimetres as it is turned for plotting; zero if it has no page setup.</summary>
        internal static (double Width, double Height) PaperSize(Layout layout)
        {
            double w = layout.PlotPaperSize.X;
            double h = layout.PlotPaperSize.Y;
            if (layout.PlotRotation == PlotRotation.Degrees090 || layout.PlotRotation == PlotRotation.Degrees270)
            {
                (w, h) = (h, w);
            }

            return w > 0 && h > 0 ? (w, h) : (0, 0);
        }

        /// <summary>
        /// Whether a paper-space point is on the layout's sheet (its paper size, with a margin).
        /// If the page setup's paper doesn't even hold the scale bar point, it hasn't been set
        /// up, so every point counts.
        /// </summary>
        private static Func<Point3d, bool> SheetTest(Layout layout, ScaleBarSettings settings)
        {
            const double margin = 25;
            (double w, double h) = PaperSize(layout);
            Func<double, double, bool> inside = (x, y) => x >= -margin && x <= w + margin && y >= -margin && y <= h + margin;
            if (!(w > 0 && h > 0) || !inside(settings.X, settings.Y))
            {
                return _ => true;
            }

            return p => inside(p.X, p.Y);
        }

        private static IEnumerable<Layout> Layouts(Database db, Transaction tr)
        {
            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            var layouts = new List<Layout>();
            foreach (DBDictionaryEntry entry in dict)
            {
                var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                if (!layout.ModelType)
                {
                    layouts.Add(layout);
                }
            }

            return layouts.OrderBy(l => l.TabOrder);
        }

        /// <summary>The block's own name, also for dynamic blocks (whose inserts point at an anonymous copy).</summary>
        internal static string BlockName(BlockReference br, Transaction tr)
        {
            ObjectId defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
            return ((BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead)).Name;
        }

        private static ObjectId GetOrCreateLayer(LayerTable layers, string name, Transaction tr)
        {
            if (layers.Has(name))
            {
                return layers[name];
            }

            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = layers.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static void Insert(Database db, BlockTableRecord space, ObjectId blockId, ObjectId layerId, ScaleBarPlacement p, Transaction tr)
        {
            var br = new BlockReference(new Point3d(p.X, p.Y, 0), blockId);
            br.SetDatabaseDefaults(db);
            br.LayerId = layerId;
            br.ColorIndex = 256; // ByLayer
            br.Linetype = "ByLayer";
            br.LineWeight = LineWeight.ByLayer;
            br.ScaleFactors = new Scale3d(p.BlockScale > 0 ? p.BlockScale : 1);
            br.Rotation = p.RotationDeg * Math.PI / 180.0;
            space.AppendEntity(br);
            tr.AddNewlyCreatedDBObject(br, true);

            var def = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
            if (!def.HasAttributeDefinitions)
            {
                return;
            }

            foreach (ObjectId id in def)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is AttributeDefinition ad && !ad.Constant)
                {
                    var attr = new AttributeReference();
                    attr.SetAttributeFromBlock(ad, br.BlockTransform);
                    attr.TextString = ad.TextString;
                    br.AttributeCollection.AppendAttribute(attr);
                    tr.AddNewlyCreatedDBObject(attr, true);
                }
            }
        }
    }
}
