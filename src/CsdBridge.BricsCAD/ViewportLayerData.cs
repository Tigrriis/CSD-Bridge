using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Bricscad.ApplicationServices;
using CsdBridge.Core.Layouts;
using Teigha.DatabaseServices;
using CadApp = Bricscad.ApplicationServices.Application;

namespace CsdBridge.BricsCAD
{
    /// <summary>One layer of the drawing, as a row of the viewport layer grid.</summary>
    internal sealed class GridLayer
    {
        public GridLayer(ObjectId id, string name, bool off, bool frozen)
        {
            Id = id;
            Name = name;
            Off = off;
            Frozen = frozen;
        }

        public ObjectId Id { get; }
        public string Name { get; }

        /// <summary>Switched off for the whole drawing: a viewport can't show it either way.</summary>
        public bool Off { get; }

        /// <summary>Frozen for the whole drawing: a viewport can't show it either way.</summary>
        public bool Frozen { get; }

        public bool Xref => ViewportLayers.IsXrefLayer(Name);
    }

    /// <summary>One paper space layout, as a column of the viewport layer grid.</summary>
    internal sealed class GridLayout
    {
        public GridLayout(string name, List<ObjectId> viewports)
        {
            Name = name;
            Viewports = viewports;
        }

        public string Name { get; }

        /// <summary>The layout's floating viewports (not the paper space viewport itself).</summary>
        public List<ObjectId> Viewports { get; }
    }

    /// <summary>
    /// What AIVPLAYERS shows: every layer, every layout, and in how many of each layout's
    /// viewports each layer is frozen. Read in one go, so the grid can redraw without touching
    /// the drawing. Also applies Show/Hide (VP thaw/freeze) to a set of layers × layouts.
    /// </summary>
    internal sealed class ViewportLayerData
    {
        private readonly int[,] _frozen; // [layer, layout] = viewports the layer is frozen in

        private ViewportLayerData(Document doc, List<GridLayer> layers, List<GridLayout> layouts, int[,] frozen)
        {
            Document = doc;
            Layers = layers;
            Layouts = layouts;
            _frozen = frozen;
        }

        public Document Document { get; }
        public List<GridLayer> Layers { get; }
        public List<GridLayout> Layouts { get; }

        public int FrozenIn(int layer, int layout) => _frozen[layer, layout];

        public ViewportLayerState State(int layer, int layout) =>
            ViewportLayers.StateOf(Layouts[layout].Viewports.Count, _frozen[layer, layout]);

        /// <summary>Same layers and layouts in the same order (so the grid can keep its selection).</summary>
        public bool SameShape(ViewportLayerData? other)
        {
            return other != null
                && other.Document == Document
                && other.Layers.Select(l => l.Id).SequenceEqual(Layers.Select(l => l.Id))
                && other.Layouts.Select(l => l.Name).SequenceEqual(Layouts.Select(l => l.Name), StringComparer.Ordinal);
        }

        public static ViewportLayerData Read(Document doc)
        {
            Database db = doc.Database;
            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var layers = new List<GridLayer>();
                var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!ltr.IsErased)
                    {
                        layers.Add(new GridLayer(id, ltr.Name, ltr.IsOff, ltr.IsFrozen));
                    }
                }

                layers.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                var index = new Dictionary<ObjectId, int>();
                for (int i = 0; i < layers.Count; i++)
                {
                    index[layers[i].Id] = i;
                }

                var layouts = new List<(int TabOrder, GridLayout Layout)>();
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dict)
                {
                    if (tr.GetObject(entry.Value, OpenMode.ForRead) is Layout layout && !layout.ModelType)
                    {
                        layouts.Add((layout.TabOrder, new GridLayout(layout.LayoutName, FloatingViewports(tr, layout))));
                    }
                }

                List<GridLayout> ordered = layouts.OrderBy(l => l.TabOrder).Select(l => l.Layout).ToList();
                var frozen = new int[layers.Count, ordered.Count];
                for (int c = 0; c < ordered.Count; c++)
                {
                    foreach (ObjectId vpId in ordered[c].Viewports)
                    {
                        var vp = (Viewport)tr.GetObject(vpId, OpenMode.ForRead);
                        foreach (ObjectId layerId in vp.GetFrozenLayers())
                        {
                            if (index.TryGetValue(layerId, out int r))
                            {
                                frozen[r, c]++;
                            }
                        }
                    }
                }

                tr.Commit();
                return new ViewportLayerData(doc, layers, ordered, frozen);
            }
        }

        /// <summary>
        /// Thaw (show) or freeze (hide) layers in every floating viewport of the given layouts.
        /// <paramref name="work"/> maps a layout index to the layer indices to change there.
        /// Viewports already right are left alone. Returns the number of viewports changed.
        /// </summary>
        public int Apply(IDictionary<int, HashSet<int>> work, bool show, out int layoutsChanged)
        {
            Document doc = Document;
            Database db = doc.Database;
            int viewports = 0;
            layoutsChanged = 0;
            var changedLayouts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (KeyValuePair<int, HashSet<int>> item in work)
                {
                    GridLayout layout = Layouts[item.Key];
                    List<ObjectId> layerIds = item.Value.Select(i => Layers[i].Id).Where(id => !id.IsErased).ToList();
                    bool changedHere = false;
                    foreach (ObjectId vpId in layout.Viewports)
                    {
                        if (vpId.IsErased)
                        {
                            continue;
                        }

                        var vp = (Viewport)tr.GetObject(vpId, OpenMode.ForRead);
                        var frozen = new HashSet<ObjectId>(vp.GetFrozenLayers().Cast<ObjectId>());
                        List<ObjectId> change = ViewportLayers.LayersToChange(layerIds, frozen, show);
                        if (change.Count == 0)
                        {
                            continue;
                        }

                        // Viewports often sit on a locked or no-plot layer; that mustn't stop this.
                        vp = (Viewport)tr.GetObject(vpId, OpenMode.ForWrite, false, true);
                        IEnumerator ids = ((IEnumerable)change).GetEnumerator();
                        if (show)
                        {
                            vp.ThawLayersInViewport(ids);
                        }
                        else
                        {
                            vp.FreezeLayersInViewport(ids);
                        }

                        viewports++;
                        changedHere = true;
                    }

                    if (changedHere)
                    {
                        layoutsChanged++;
                        changedLayouts.Add(layout.Name);
                    }
                }

                tr.Commit();
            }

            if (viewports > 0)
            {
                RegenIfShowing(doc, changedLayouts);
            }

            return viewports;
        }

        /// <summary>
        /// The layout's viewports, without the paper space viewport (the sheet itself), which
        /// is the first one: Layout.GetViewports() lists it first once the layout has been
        /// opened, and it is the first in the layout's block either way.
        /// </summary>
        private static List<ObjectId> FloatingViewports(Transaction tr, Layout layout)
        {
            var all = new List<ObjectId>();
            var space = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                if (string.Equals(id.ObjectClass.DxfName, "VIEWPORT", StringComparison.OrdinalIgnoreCase))
                {
                    all.Add(id);
                }
            }

            if (all.Count == 0)
            {
                return all;
            }

            ObjectId sheet = all[0];
            try
            {
                ObjectIdCollection listed = layout.GetViewports();
                if (listed != null && listed.Count > 0)
                {
                    sheet = listed[0];
                }
            }
            catch (Exception)
            {
                // keep the first in the block
            }

            all.Remove(sheet);
            return all;
        }

        /// <summary>The screen only catches up with viewport layer changes after a regen.</summary>
        private static void RegenIfShowing(Document doc, HashSet<string> layouts)
        {
            try
            {
                // CTAB is the layout tab showing ("Model" for model space).
                string? current = CadApp.GetSystemVariable("CTAB") as string;
                if (string.IsNullOrEmpty(current) || layouts.Contains(current!))
                {
                    doc.Editor.Regen();
                }
            }
            catch (Exception)
            {
                // The change is made; the screen updates on the next regen.
            }
        }
    }
}
