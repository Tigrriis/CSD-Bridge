using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Bricscad.ApplicationServices;
using Bricscad.EditorInput;
using CsdBridge.Core.Layers;
using Teigha.Colors;
using Teigha.DatabaseServices;

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// AILAYERWHEEL: show the layer wheel at the pointer. With nothing selected the layer picked
    /// becomes the current layer; with objects selected they move to it. No Claude, no API key.
    /// AILAYERWHEELSETUP chooses which layers are on the wheel.
    /// </summary>
    internal static class LayerWheelTool
    {
        public static void Run(Document doc, ObjectId[]? preselected)
        {
            Editor ed = doc.Editor;
            if (AgentRunner.IsBusy)
            {
                ed.WriteMessage("\nA request is running. Press Esc first.\n");
                return;
            }

            LayerWheelSettings settings = AgentRunner.Settings.LoadLayerWheel(out string path, out string? problem);
            if (problem != null)
            {
                ed.WriteMessage($"\nCSD-Bridge: could not read the layer wheel file ({problem}).\n");
                return;
            }

            if (settings.Layers.Count == 0)
            {
                ed.WriteMessage($"\nCSD-Bridge: no layers are on the wheel yet ({path}). Choose them now.\n");
                if (!Setup(doc))
                {
                    return;
                }

                settings = AgentRunner.Settings.LoadLayerWheel(out path, out problem);
                if (problem != null || settings.Layers.Count == 0)
                {
                    return;
                }
            }

            ObjectId[] ids = preselected ?? new ObjectId[0];
            List<WheelLayerState> layers = ReadLayers(doc.Database, settings.Layers);
            if (!layers.Any(l => l.Exists))
            {
                ed.WriteMessage($"\nCSD-Bridge: this drawing has none of the {layers.Count} layers on the wheel. AILAYERWHEELSETUP chooses the layers.\n");
                return;
            }

            string? picked;
            using (var wheel = new LayerWheelForm(settings, layers, ids.Length))
            {
                if (wheel.TooBig)
                {
                    ed.WriteMessage("\nCSD-Bridge: some groups have more layers than fit round the wheel on this screen. Lower split_groups_over in the layer wheel file, or take layers off with AILAYERWHEELSETUP.\n");
                }

                picked = wheel.ShowDialog() == DialogResult.OK ? wheel.PickedLayer : null;
            }

            if (picked == null)
            {
                return;
            }

            try
            {
                if (ids.Length > 0)
                {
                    Move(doc, ids, picked);
                }
                else
                {
                    SetCurrent(doc, picked);
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nCSD-Bridge: could not use layer {picked}: {ex.Message}\n");
            }
        }

        /// <summary>Open the window that chooses the wheel's layers. True if the list was saved.</summary>
        public static bool Setup(Document doc)
        {
            LayerWheelSettings settings = AgentRunner.Settings.LoadLayerWheel(out _, out string? problem);
            if (problem != null)
            {
                doc.Editor.WriteMessage($"\nCSD-Bridge: could not read the layer wheel file ({problem}). Saving will replace it.\n");
            }

            using (var setup = new LayerWheelSetupForm(settings, DrawingLayers(doc.Database)))
            {
                if (setup.ShowDialog() != DialogResult.OK)
                {
                    return false;
                }

                doc.Editor.WriteMessage($"\nCSD-Bridge: {setup.SavedCount} layer(s) on the wheel, saved to {setup.SavedPath}\n");
                return true;
            }
        }

        /// <summary>The drawing's own layers (not those of attached drawings), by name.</summary>
        private static List<string> DrawingLayers(Database db)
        {
            var names = new List<string>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!ltr.IsErased && ltr.Name.IndexOf('|') < 0)
                    {
                        names.Add(ltr.Name);
                    }
                }

                tr.Commit();
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>Each wheel layer as this drawing has it: colour, frozen, off, locked, current.</summary>
        private static List<WheelLayerState> ReadLayers(Database db, IReadOnlyList<string> names)
        {
            var states = new List<WheelLayerState>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (string name in names)
                {
                    var state = new WheelLayerState { Name = name };
                    if (table.Has(name))
                    {
                        ObjectId id = table[name];
                        var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (!ltr.IsErased)
                        {
                            state.Name = ltr.Name;
                            state.Exists = true;
                            state.Color = ScreenColor(ltr.Color);
                            state.Frozen = ltr.IsFrozen;
                            state.Off = ltr.IsOff;
                            state.Locked = ltr.IsLocked;
                            state.Current = id == db.Clayer;
                        }
                    }

                    states.Add(state);
                }

                tr.Commit();
            }

            return states;
        }

        private static System.Drawing.Color ScreenColor(Color color)
        {
            if (color.ColorMethod == ColorMethod.ByAci)
            {
                (int r, int g, int b) = AciColors.Rgb(color.ColorIndex);
                return System.Drawing.Color.FromArgb(r, g, b);
            }

            return System.Drawing.Color.FromArgb(color.Red, color.Green, color.Blue);
        }

        private static void SetCurrent(Document doc, string layer)
        {
            Database db = doc.Database;
            Editor ed = doc.Editor;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!table.Has(layer))
                {
                    ed.WriteMessage($"\nCSD-Bridge: layer {layer} is not in this drawing.\n");
                    return;
                }

                ObjectId id = table[layer];
                var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (ltr.IsFrozen)
                {
                    ed.WriteMessage($"\nCSD-Bridge: {ltr.Name} is frozen, and a frozen layer can't be the current layer. Thaw it first.\n");
                    return;
                }

                if (db.Clayer != id)
                {
                    db.Clayer = id;
                }

                tr.Commit();
                ed.WriteMessage($"\nCurrent layer: {ltr.Name}{(ltr.IsOff ? "  (it is off, so new objects won't show)" : "")}\n");
            }
        }

        /// <summary>Move the selected objects to the layer, as one UNDO step. Objects on locked layers are left alone.</summary>
        private static void Move(Document doc, ObjectId[] ids, string layer)
        {
            Database db = doc.Database;
            Editor ed = doc.Editor;
            int moved = 0, already = 0, onLocked = 0;
            string name = layer;
            bool hidden = false;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!table.Has(layer))
                {
                    ed.WriteMessage($"\nCSD-Bridge: layer {layer} is not in this drawing.\n");
                    return;
                }

                ObjectId target = table[layer];
                var targetLayer = (LayerTableRecord)tr.GetObject(target, OpenMode.ForRead);
                name = targetLayer.Name;
                hidden = targetLayer.IsFrozen || targetLayer.IsOff;

                var locked = new Dictionary<ObjectId, bool>();
                foreach (ObjectId id in ids)
                {
                    if (id.IsErased || !(tr.GetObject(id, OpenMode.ForRead) is Entity ent))
                    {
                        continue;
                    }

                    ObjectId from = ent.LayerId;
                    if (from == target)
                    {
                        already++;
                        continue;
                    }

                    if (!locked.TryGetValue(from, out bool isLocked))
                    {
                        locked[from] = isLocked = ((LayerTableRecord)tr.GetObject(from, OpenMode.ForRead)).IsLocked;
                    }

                    if (isLocked)
                    {
                        onLocked++;
                        continue;
                    }

                    ent.UpgradeOpen();
                    ent.LayerId = target;
                    moved++;
                }

                tr.Commit();
            }

            var notes = new List<string>();
            if (already > 0)
            {
                notes.Add($"{already} already there");
            }

            if (onLocked > 0)
            {
                notes.Add($"{onLocked} on locked layers left alone");
            }

            ed.WriteMessage($"\nCSD-Bridge: moved {moved} object(s) to {name}" +
                (notes.Count > 0 ? $" ({string.Join("; ", notes)})" : "") + "." +
                (hidden && moved > 0 ? " That layer is frozen or off, so they are hidden until it is shown." : "") + "\n");

            try
            {
                ed.SetImpliedSelection(ids); // leave them selected, as the layer list does
            }
            catch (System.Exception)
            {
                // The move is done; only the selection highlight is lost.
            }
        }
    }
}
