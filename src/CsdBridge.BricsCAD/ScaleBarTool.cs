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
    /// AISCALEBARS: puts a scale bar block ("Scale Bar 750") on every layout for each
    /// viewport scale on it, stacked when a layout has several. Runs entirely in the
    /// add-in: no Claude call, so it costs nothing and gives the same result every time.
    ///
    /// Section sheets have no viewports: the sections are xrefs placed straight on the
    /// layout. Their scales come from the xrefs' scale text instead ("SCALES: HORIZONTAL
    /// 1:50 VERTICAL 1:20"), counting only text that lands on the sheet, in case one xref
    /// holds several sheets.
    ///
    /// A layout whose bars already match its viewports is left alone, so bars moved by
    /// hand stay put. Otherwise its old bars are replaced. The whole run is one UNDO step.
    /// </summary>
    internal static class ScaleBarTool
    {
        public static void Run(Document doc)
        {
            Database db = doc.Database;
            Editor ed = doc.Editor;
            ScaleBarOptions options = BridgeSettings.Load().ScaleBars ?? new ScaleBarOptions();
            bool mmDrawing = db.Insunits == UnitsValue.Millimeters;

            var lines = new List<string>();
            int added = 0;
            int removed = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (layers.Has(options.Layer) && ((LayerTableRecord)tr.GetObject(layers[options.Layer], OpenMode.ForRead)).IsLocked)
                {
                    ed.WriteMessage($"\nCSD-Bridge: layer {options.Layer} is locked. Unlock it and run AISCALEBARS again.\n");
                    return;
                }

                ObjectId layerId = ObjectId.Null;
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

                foreach (Layout layout in Layouts(db, tr))
                {
                    var space = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                    var viewports = new List<Viewport>();
                    var existing = new List<(BlockReference Bar, int Scale)>();
                    var xrefs = new List<BlockReference>();
                    foreach (ObjectId id in space)
                    {
                        DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                        if (obj is Viewport vp)
                        {
                            viewports.Add(vp);
                        }
                        else if (obj is BlockReference br)
                        {
                            var def = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                            if (def.IsFromExternalReference)
                            {
                                if (br.Visible && !hidden(br.LayerId))
                                {
                                    xrefs.Add(br);
                                }
                            }
                            else if (ScaleBars.TryParseBlockName(options, BlockName(br, tr), out int barScale))
                            {
                                existing.Add((br, barScale));
                            }
                        }
                    }

                    // The first viewport is the layout's own paper-space view, not a view of the model.
                    var scales = new List<int>();
                    var problems = new List<string>();
                    foreach (Viewport vp in viewports.Skip(1))
                    {
                        IReadOnlyList<int> candidates = ScaleBars.Candidates(vp.CustomScale, mmDrawing);
                        if (candidates.Count == 0)
                        {
                            problems.Add($"viewport {vp.Handle} has a non-standard scale ({DescribeScale(vp.CustomScale, mmDrawing)})");
                            continue;
                        }

                        int found = candidates.FirstOrDefault(n => blocks.Has(ScaleBars.BlockName(options, n)));
                        if (found > 0)
                        {
                            scales.Add(found);
                        }
                        else
                        {
                            string missing = $"block '{ScaleBars.BlockName(options, candidates[0])}' isn't loaded";
                            if (!problems.Contains(missing))
                            {
                                problems.Add(missing);
                            }
                        }
                    }

                    // No views of the model: a section sheet. Use the scale text in its xrefs.
                    string source = "";
                    if (viewports.Count <= 1)
                    {
                        source = " (from section scale text)";
                        Func<Point3d, bool> onSheet = SheetTest(layout, options);
                        var stated = new List<int>();
                        int offSheet = 0;
                        foreach (BlockReference xref in xrefs)
                        {
                            foreach (ScaleText text in TextIn(xref.BlockTableRecord, tr, textCache, hidden, 0))
                            {
                                if (onSheet(text.Position.TransformBy(xref.BlockTransform)))
                                {
                                    stated.AddRange(text.Scales);
                                }
                                else
                                {
                                    offSheet++;
                                }
                            }
                        }

                        foreach (int n in stated.Distinct())
                        {
                            if (blocks.Has(ScaleBars.BlockName(options, n)))
                            {
                                scales.Add(n);
                            }
                            else
                            {
                                problems.Add($"block '{ScaleBars.BlockName(options, n)}' isn't loaded");
                            }
                        }

                        if (stated.Count == 0 && offSheet > 0)
                        {
                            problems.Add("its xrefs' scale text is all outside the sheet");
                        }
                    }

                    string name = layout.LayoutName;
                    IReadOnlyList<ScaleBarPlacement> placements = ScaleBars.Stack(scales, options);
                    string issues = problems.Count > 0 ? " (" + string.Join("; ", problems) + ")" : "";
                    if (placements.Count == 0)
                    {
                        string why = viewports.Count > 1 || problems.Count > 0 ? "nothing placed"
                            : xrefs.Count == 0 ? "no viewports or xrefs"
                            : "no viewports, and no scale text found in its xrefs";
                        lines.Add($"{name}: {why}, left as is{issues}");
                        continue;
                    }

                    string list = string.Join(", ", placements.Select(p => "1:" + p.Scale.ToString(CultureInfo.InvariantCulture)));
                    bool upToDate = existing.Select(e => e.Scale).OrderBy(s => s).SequenceEqual(placements.Select(p => p.Scale));
                    if (upToDate)
                    {
                        lines.Add($"{name}: {list} already there{source}{issues}");
                        continue;
                    }

                    string? locked = existing.Select(e => e.Bar.Layer).FirstOrDefault(l => ((LayerTableRecord)tr.GetObject(layers[l], OpenMode.ForRead)).IsLocked);
                    if (locked != null)
                    {
                        lines.Add($"{name}: skipped, its old scale bar is on locked layer {locked}{issues}");
                        continue;
                    }

                    foreach (var (bar, _) in existing)
                    {
                        bar.UpgradeOpen();
                        bar.Erase();
                        removed++;
                    }

                    if (layerId.IsNull)
                    {
                        layerId = GetOrCreateLayer(layers, options.Layer, tr);
                    }

                    space.UpgradeOpen();
                    foreach (ScaleBarPlacement p in placements)
                    {
                        Insert(db, space, blocks[p.BlockName], layerId, p, options, tr);
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
                : $"\n  {added} added, {removed} removed. One UNDO reverts all of it.\n");
        }

        /// <summary>A line of text stating scales, and where it is in its block's coordinates.</summary>
        private sealed class ScaleText
        {
            public ScaleText(IReadOnlyList<int> scales, Point3d position)
            {
                Scales = scales;
                Position = position;
            }

            public IReadOnlyList<int> Scales { get; }

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
                            found.Add(new ScaleText(inner.Scales, inner.Position.TransformBy(br.BlockTransform)));
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

        private static void AddScaleText(List<ScaleText> found, string? text, Point3d position)
        {
            IReadOnlyList<int> scales = ScaleBars.ScalesInText(text);
            if (scales.Count > 0)
            {
                found.Add(new ScaleText(scales, position));
            }
        }

        /// <summary>
        /// Whether a paper-space point is on the layout's sheet (its paper size, with a margin).
        /// If the page setup's paper doesn't even hold the scale bar point, it hasn't been set
        /// up, so every point counts.
        /// </summary>
        private static Func<Point3d, bool> SheetTest(Layout layout, ScaleBarOptions options)
        {
            const double margin = 25;
            double w = layout.PlotPaperSize.X;
            double h = layout.PlotPaperSize.Y;
            if (layout.PlotRotation == PlotRotation.Degrees090 || layout.PlotRotation == PlotRotation.Degrees270)
            {
                (w, h) = (h, w);
            }

            Func<double, double, bool> inside = (x, y) => x >= -margin && x <= w + margin && y >= -margin && y <= h + margin;
            if (!(w > 0 && h > 0) || !inside(options.X, options.Y))
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
        private static string BlockName(BlockReference br, Transaction tr)
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

        private static void Insert(Database db, BlockTableRecord space, ObjectId blockId, ObjectId layerId, ScaleBarPlacement p, ScaleBarOptions options, Transaction tr)
        {
            var br = new BlockReference(new Point3d(p.X, p.Y, 0), blockId);
            br.SetDatabaseDefaults(db);
            br.LayerId = layerId;
            br.ColorIndex = 256; // ByLayer
            br.Linetype = "ByLayer";
            br.LineWeight = LineWeight.ByLayer;
            br.ScaleFactors = new Scale3d(options.Scale > 0 ? options.Scale : 1);
            br.Rotation = options.RotationDeg * Math.PI / 180.0;
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

        private static string DescribeScale(double customScale, bool mmDrawing)
        {
            if (!(customScale > 0))
            {
                return "zero";
            }

            double n = (mmDrawing ? 1.0 : 1000.0) / customScale;
            return "1:" + n.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
