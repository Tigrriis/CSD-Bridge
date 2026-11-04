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
                foreach (Layout layout in Layouts(db, tr))
                {
                    var space = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                    var viewports = new List<Viewport>();
                    var existing = new List<(BlockReference Bar, int Scale)>();
                    foreach (ObjectId id in space)
                    {
                        DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                        if (obj is Viewport vp)
                        {
                            viewports.Add(vp);
                        }
                        else if (obj is BlockReference br && ScaleBars.TryParseBlockName(options, BlockName(br, tr), out int barScale))
                        {
                            existing.Add((br, barScale));
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

                    string name = layout.LayoutName;
                    IReadOnlyList<ScaleBarPlacement> placements = ScaleBars.Stack(scales, options);
                    string issues = problems.Count > 0 ? " (" + string.Join("; ", problems) + ")" : "";
                    if (placements.Count == 0)
                    {
                        lines.Add($"{name}: {(viewports.Count <= 1 ? "no viewports" : "nothing placed")}, left as is{issues}");
                        continue;
                    }

                    string list = string.Join(", ", placements.Select(p => "1:" + p.Scale.ToString(CultureInfo.InvariantCulture)));
                    bool upToDate = existing.Select(e => e.Scale).OrderBy(s => s).SequenceEqual(placements.Select(p => p.Scale));
                    if (upToDate)
                    {
                        lines.Add($"{name}: {list} already there{issues}");
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

                    lines.Add($"{name}: {list}{(existing.Count > 0 ? " (replaced old bars)" : "")}{issues}");
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
