using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Bricscad.ApplicationServices;
using Bricscad.EditorInput;
using CsdBridge.Core.Drawing;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using CadApp = Bricscad.ApplicationServices.Application;

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// AISCALEBARSETUP: set up the office's scale bars from the command line, mostly by picking
    /// things in a drawing rather than typing names and coordinates.
    ///
    ///   Bar       pick a scale bar block and say its scale: that names the block for the scale,
    ///             or the pattern all of them follow, and can take the bar's position as standard
    ///   Combined  the same for a bar that shows a horizontal and a vertical scale
    ///   Position  pick where the first bar goes, for all sheets or this sheet size
    ///   Stack     pick where the first and second bars go, for the offset between bars
    ///   List      show what is set up
    ///   File      open the settings file, or use a file somewhere else (a network drive)
    ///
    /// Every change is saved to the scale bar file straight away.
    /// </summary>
    internal static class ScaleBarSetup
    {
        public static void Run(Document doc)
        {
            Editor ed = doc.Editor;
            if (AgentRunner.IsBusy)
            {
                ed.WriteMessage("\nA request is running. Press Esc first.\n");
                return;
            }

            BridgeSettings app = AgentRunner.Settings;
            if (app.ScaleBars != null)
            {
                ed.WriteMessage($"\nCSD-Bridge: {BridgeSettings.SettingsPath} has a \"scale_bars\" entry, the older way to set scale bars up, and it is used instead of the scale bar file." +
                    " Remove that entry, run AIRESET, then run AISCALEBARSETUP again.\n");
                return;
            }

            ScaleBarSettings settings = app.LoadScaleBars(out string readFrom, out string? problem);
            if (problem != null)
            {
                ed.WriteMessage($"\nCSD-Bridge: could not read the scale bar file ({problem}). Starting from the defaults; the first change replaces the file.\n");
            }

            ed.WriteMessage("\nCSD-Bridge scale bar setup\n" + settings.Describe());
            ed.WriteMessage($"\n  File:      {readFrom}");
            if (!SamePath(readFrom, app.ScaleBarSavePath()))
            {
                ed.WriteMessage($"\n             Changes are saved to your own copy, {app.ScaleBarSavePath()}, which is then used instead.");
            }

            ed.WriteMessage("\n");

            while (true)
            {
                var options = new PromptKeywordOptions("\nScale bar setup");
                foreach (string keyword in new[] { "Bar", "Combined", "Position", "Stack", "List", "File", "Done" })
                {
                    options.Keywords.Add(keyword);
                }

                options.Keywords.Default = "Done";
                options.AllowNone = true;
                PromptResult res = ed.GetKeywords(options);
                if (res.Status != PromptStatus.OK || res.StringResult == "Done")
                {
                    break;
                }

                string? change = null;
                try
                {
                    switch (res.StringResult)
                    {
                        case "Bar":
                            change = LearnBar(doc, settings, combined: false);
                            break;
                        case "Combined":
                            change = LearnBar(doc, settings, combined: true);
                            break;
                        case "Position":
                            change = PickPosition(doc, settings);
                            break;
                        case "Stack":
                            change = PickStack(doc, settings);
                            break;
                        case "List":
                            ed.WriteMessage("\n" + settings.Describe() + $"\n  File:      {app.ResolvedScaleBarPath()}\n");
                            break;
                        case "File":
                            settings = FileOptions(doc, settings);
                            break;
                    }
                }
                catch (System.Exception ex)
                {
                    ed.WriteMessage($"\nCSD-Bridge: that didn't work: {ex.Message}\n");
                    continue;
                }

                if (change != null && Save(ed, settings))
                {
                    ed.WriteMessage($"\nCSD-Bridge: {change}. Saved to {AgentRunner.Settings.ScaleBarSavePath()}\n");
                }
            }

            ed.WriteMessage("\nCSD-Bridge: AISCALEBARS puts the scale bars on the layouts.\n");
        }

        private static bool Save(Editor ed, ScaleBarSettings settings)
        {
            string path = AgentRunner.Settings.ScaleBarSavePath();
            try
            {
                settings.Save(path);
                return true;
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nCSD-Bridge: could not save {path}: {ex.Message}\n");
                return false;
            }
        }

        // ------------------------------------------------------------------ Bar and Combined

        /// <summary>A scale bar picked in the drawing.</summary>
        private sealed class PickedBar
        {
            public string Name = "";
            public Point3d Position;
            public string Layer = "";
            public double Size = 1;
            public double RotationDeg;
            public bool OnALayout;
            public string Sheet = "";
        }

        /// <summary>
        /// Pick a scale bar block and say what it is for. Returns what changed, or null if the
        /// user backed out.
        /// </summary>
        private static string? LearnBar(Document doc, ScaleBarSettings settings, bool combined)
        {
            Editor ed = doc.Editor;
            PickedBar? bar = PickBar(doc, combined ? "\nSelect a combined scale bar block: " : "\nSelect a scale bar block: ");
            if (bar == null)
            {
                return null;
            }

            IReadOnlyList<int> numbers = ScaleBarSettings.NumbersIn(bar.Name);
            string change;
            if (combined)
            {
                int? horizontal = AskScale(ed, $"\nBlock '{bar.Name}'. Its horizontal scale, 1:", numbers.Count > 0 ? numbers[0] : 0);
                int? vertical = horizontal == null ? null : AskScale(ed, "\nIts vertical scale, 1:", numbers.Count > 1 ? numbers[1] : 0);
                if (horizontal == null || vertical == null)
                {
                    return null;
                }

                if (horizontal == vertical)
                {
                    ed.WriteMessage("\nThe two scales are the same, so that is an ordinary scale bar: use Bar for it.\n");
                    return null;
                }

                string? pattern = ScaleBarSettings.SuggestCombinedPattern(bar.Name, horizontal.Value, vertical.Value);
                bool asPattern = pattern != null && (string.Equals(pattern, settings.CombinedBlockName, StringComparison.OrdinalIgnoreCase)
                    || AskYesNo(ed, $"\nAre the other combined bars named the same way ('{pattern}')?", true) == true);
                change = settings.LearnCombined(bar.Name, horizontal.Value, vertical.Value, asPattern);
            }
            else
            {
                int? scale = AskScale(ed, $"\nBlock '{bar.Name}'. The scale it is for, 1:", numbers.Count > 0 ? numbers[numbers.Count - 1] : 0);
                if (scale == null)
                {
                    return null;
                }

                string? pattern = ScaleBarSettings.SuggestPattern(bar.Name, scale.Value);
                bool asPattern = pattern != null && (string.Equals(pattern, settings.BlockName, StringComparison.OrdinalIgnoreCase)
                    || AskYesNo(ed, $"\nAre the other scale bars named the same way ('{pattern}')?", true) == true);
                change = settings.LearnSingle(bar.Name, scale.Value, asPattern);
            }

            // Optionally take where this bar is as the standard place.
            if (bar.OnALayout)
            {
                string? scope = AskScope(ed, "\nUse this bar's position, layer, size and rotation as the standard", bar.Sheet, allowNo: true);
                if (scope != null && scope != "No")
                {
                    change += "; " + settings.SetPosition(
                        scope == "Sheet" ? bar.Sheet : null,
                        combined ? "combined" : null,
                        Math.Round(bar.Position.X, 3), Math.Round(bar.Position.Y, 3),
                        bar.Layer, Math.Round(bar.Size, 4), Math.Round(bar.RotationDeg, 3));
                }
            }

            return change;
        }

        private static PickedBar? PickBar(Document doc, string prompt)
        {
            var options = new PromptEntityOptions(prompt);
            options.SetRejectMessage("\nThat isn't a block.");
            options.AddAllowedClass(typeof(BlockReference), false);
            PromptEntityResult picked = doc.Editor.GetEntity(options);
            if (picked.Status != PromptStatus.OK)
            {
                return null;
            }

            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var br = (BlockReference)tr.GetObject(picked.ObjectId, OpenMode.ForRead);
                var owner = (BlockTableRecord)tr.GetObject(br.OwnerId, OpenMode.ForRead);
                var bar = new PickedBar
                {
                    Name = ScaleBarTool.BlockName(br, tr),
                    Position = br.Position,
                    Layer = br.Layer,
                    Size = Math.Abs(br.ScaleFactors.X),
                    RotationDeg = br.Rotation * 180.0 / Math.PI,
                };

                if (owner.IsLayout && owner.LayoutId != ObjectId.Null && tr.GetObject(owner.LayoutId, OpenMode.ForRead) is Layout layout && !layout.ModelType)
                {
                    bar.OnALayout = true;
                    (double w, double h) = ScaleBarTool.PaperSize(layout);
                    bar.Sheet = SheetSizes.Name(w, h);
                }

                tr.Commit();
                return bar;
            }
        }

        // ------------------------------------------------------------------ Position and Stack

        private static string? PickPosition(Document doc, ScaleBarSettings settings)
        {
            Editor ed = doc.Editor;
            string? sheet = CurrentSheet(doc);
            if (sheet == null)
            {
                ed.WriteMessage("\nSwitch to a layout (paper space) first: positions are measured on the sheet.\n");
                return null;
            }

            var kind = new PromptKeywordOptions("\nPosition for which bars");
            kind.Keywords.Add("All");
            kind.Keywords.Add("Combined");
            kind.Keywords.Default = "All";
            kind.AllowNone = true;
            PromptResult which = ed.GetKeywords(kind);
            if (which.Status != PromptStatus.OK)
            {
                return null;
            }

            string? scope = AskScope(ed, "\nUse it on", sheet, allowNo: false);
            if (scope == null)
            {
                return null;
            }

            PromptPointResult point = ed.GetPoint(new PromptPointOptions("\nPick where the first scale bar goes (its insertion point): "));
            if (point.Status != PromptStatus.OK)
            {
                return null;
            }

            Point3d at = point.Value.TransformBy(ed.CurrentUserCoordinateSystem);
            return settings.SetPosition(
                scope == "Sheet" ? sheet : null,
                which.StringResult == "Combined" ? "combined" : null,
                Math.Round(at.X, 3), Math.Round(at.Y, 3));
        }

        private static string? PickStack(Document doc, ScaleBarSettings settings)
        {
            Editor ed = doc.Editor;
            if (CurrentSheet(doc) == null)
            {
                ed.WriteMessage("\nSwitch to a layout (paper space) first: the offset is measured on the sheet.\n");
                return null;
            }

            PromptPointResult first = ed.GetPoint(new PromptPointOptions("\nPick where the first scale bar goes: "));
            if (first.Status != PromptStatus.OK)
            {
                return null;
            }

            var next = new PromptPointOptions("\nPick where the second scale bar goes: ") { UseBasePoint = true, BasePoint = first.Value };
            PromptPointResult second = ed.GetPoint(next);
            if (second.Status != PromptStatus.OK)
            {
                return null;
            }

            Matrix3d ucs = ed.CurrentUserCoordinateSystem;
            Vector3d offset = second.Value.TransformBy(ucs) - first.Value.TransformBy(ucs);
            settings.StackOffsetX = Math.Round(offset.X, 3);
            settings.StackOffsetY = Math.Round(offset.Y, 3);
            return string.Format(CultureInfo.InvariantCulture, "each further bar goes ({0:0.###}, {1:0.###}) from the one before", settings.StackOffsetX, settings.StackOffsetY);
        }

        /// <summary>The sheet size of the layout that is showing ("" if it has no page setup), or null in model space.</summary>
        private static string? CurrentSheet(Document doc)
        {
            Database db = doc.Database;
            if (db.TileMode || Convert.ToInt32(CadApp.GetSystemVariable("CVPORT"), CultureInfo.InvariantCulture) != 1)
            {
                return null; // the Model tab, or inside a viewport
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                string sheet = "";
                if (space.IsLayout && space.LayoutId != ObjectId.Null && tr.GetObject(space.LayoutId, OpenMode.ForRead) is Layout layout)
                {
                    (double w, double h) = ScaleBarTool.PaperSize(layout);
                    sheet = SheetSizes.Name(w, h);
                }

                tr.Commit();
                return sheet;
            }
        }

        // ------------------------------------------------------------------ File

        private static ScaleBarSettings FileOptions(Document doc, ScaleBarSettings settings)
        {
            Editor ed = doc.Editor;
            string path = AgentRunner.Settings.ScaleBarSavePath();
            var options = new PromptKeywordOptions($"\nScale bar file ({path})");
            options.Keywords.Add("Open");
            options.Keywords.Add("Change");
            options.Keywords.Default = "Open";
            options.AllowNone = true;
            PromptResult res = ed.GetKeywords(options);
            if (res.Status != PromptStatus.OK)
            {
                return settings;
            }

            if (res.StringResult == "Open")
            {
                if (Save(ed, settings))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                    ed.WriteMessage($"\nCSD-Bridge: opened {path}. The notes at the top explain each setting. AISCALEBARS reads it afresh each time.\n");
                }

                return settings;
            }

            using (var dialog = new SaveFileDialog
            {
                Title = "Scale bar file",
                Filter = "Scale bar file (*.json)|*.json",
                FileName = Path.GetFileName(path),
                InitialDirectory = Path.GetDirectoryName(path),
                OverwritePrompt = false,
                CheckPathExists = true,
            })
            {
                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return settings;
                }

                string chosen = dialog.FileName;
                if (File.Exists(chosen))
                {
                    settings = ScaleBarSettings.Load(chosen); // use what the office already has there
                    AgentRunner.SetScaleBarFile(chosen);
                    ed.WriteMessage($"\nCSD-Bridge: now using {chosen}\n" + settings.Describe() + "\n");
                }
                else
                {
                    AgentRunner.SetScaleBarFile(chosen);
                    if (Save(ed, settings))
                    {
                        ed.WriteMessage($"\nCSD-Bridge: saved these settings to {chosen}, and this PC now uses that file.\n");
                    }
                }
            }

            return settings;
        }

        // ------------------------------------------------------------------ prompts

        /// <summary>Ask for a scale (the N of 1:N), offering <paramref name="guess"/> if there is one. Null if cancelled.</summary>
        private static int? AskScale(Editor ed, string prompt, int guess)
        {
            var options = new PromptIntegerOptions(prompt) { AllowNegative = false, AllowZero = false, AllowNone = guess > 0 };
            if (guess > 0)
            {
                options.DefaultValue = guess;
                options.UseDefaultValue = true;
            }

            PromptIntegerResult res = ed.GetInteger(options);
            return res.Status == PromptStatus.OK ? res.Value
                : res.Status == PromptStatus.None && guess > 0 ? guess
                : (int?)null;
        }

        /// <summary>True, false, or null if cancelled.</summary>
        private static bool? AskYesNo(Editor ed, string prompt, bool defaultYes)
        {
            var options = new PromptKeywordOptions(prompt);
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            options.Keywords.Default = defaultYes ? "Yes" : "No";
            options.AllowNone = true;
            PromptResult res = ed.GetKeywords(options);
            return res.Status == PromptStatus.OK ? res.StringResult == "Yes" : (bool?)null;
        }

        /// <summary>
        /// "All" sheets, this "Sheet" size, or (if allowed) "No". Null if cancelled. With no page
        /// setup there is no sheet size to offer.
        /// </summary>
        private static string? AskScope(Editor ed, string prompt, string sheet, bool allowNo)
        {
            bool hasSheet = !string.IsNullOrEmpty(sheet);
            var options = new PromptKeywordOptions(prompt + (hasSheet ? $" (All sheets, or only {sheet} Sheets)" : " (All sheets)"));
            options.Keywords.Add("All");
            if (hasSheet)
            {
                options.Keywords.Add("Sheet");
            }

            if (allowNo)
            {
                options.Keywords.Add("No");
            }

            options.Keywords.Default = allowNo ? "No" : "All";
            options.AllowNone = true;
            PromptResult res = ed.GetKeywords(options);
            return res.Status == PromptStatus.OK ? res.StringResult : null;
        }

        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch (System.Exception)
            {
                return false;
            }
        }
    }
}
