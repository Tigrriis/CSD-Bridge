using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CsdBridge.Core.Agent;
using CsdBridge.Core.Routines;
using CadApp = Bricscad.ApplicationServices.Application;

namespace CsdBridge.BricsCAD.Routines
{
    /// <summary>
    /// What the Routines window, the chat panel and the runner share: where routines are
    /// kept, running one (the run dialog, then a queued request), and opening Claude's
    /// drafts in the editor.
    /// </summary>
    internal static class RoutineUi
    {
        private const string ExamplePrefix = "CsdBridge.BricsCAD.Resources.routine-examples.";

        /// <summary>The user's routines and, if office_routines_folder is set, the office's.</summary>
        public static RoutineLibrary CreateLibrary() =>
            new RoutineLibrary(BridgeSettings.PersonalRoutinesFolder, AgentRunner.Settings.ResolvedOfficeRoutinesFolder());

        /// <summary>The values each routine was last run with (per user).</summary>
        public static RoutineInputMemory Memory => new RoutineInputMemory(Path.Combine(BridgeSettings.Folder, "routine-inputs.json"));

        /// <summary>
        /// The first time routines are opened, create the user's folder with the example
        /// routine(s) shipped in the add-in, so there's one to look at and copy.
        /// </summary>
        public static void SeedExamplesIfNew(RoutineLibrary library)
        {
            if (Directory.Exists(library.PersonalFolder))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(library.PersonalFolder);
                var assembly = typeof(RoutineUi).Assembly;
                foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ExamplePrefix, StringComparison.Ordinal)))
                {
                    using Stream? stream = assembly.GetManifestResourceStream(resource);
                    if (stream == null)
                    {
                        continue;
                    }

                    using var reader = new StreamReader(stream);
                    Routine example = RoutineFile.Parse(reader.ReadToEnd());
                    if (example.Validate().Count == 0 && !library.NameTaken(example.Name, RoutineLocation.Personal))
                    {
                        library.Save(example, RoutineLocation.Personal);
                    }
                }
            }
            catch (Exception)
            {
                // Examples are a nicety; an empty list still works.
            }
        }

        /// <summary>Ask for the routine's inputs, then run it in the active drawing. False if it didn't start.</summary>
        public static bool Run(IWin32Window? owner, Routine routine)
        {
            Dictionary<string, string> remembered = Memory.Get(routine);
            using var form = new RoutineRunForm(routine, remembered, AgentRunner.Settings.CsdMode ?? "read");
            if (form.ShowDialog(owner) != DialogResult.OK)
            {
                return false;
            }

            Memory.Remember(routine, form.Values);
            var request = new RunRequest(RoutinePrompt.BuildRun(routine, form.Values))
            {
                Display = RoutinePrompt.Describe(routine, form.Values),
                Routine = routine.Name,
                EffortText = routine.Instructions,
                NewChat = form.NewChat,
            };
            if (routine.Effort is EffortMode tier)
            {
                request.Effort = EffortPolicy.Fixed(tier, AgentRunner.Settings.EffortLevels(), "set by the routine");
            }

            ChatPalette.Show(); // progress and any questions from Claude show there
            if (AgentRunner.QueueRun(request))
            {
                return true;
            }

            MessageBox.Show(owner, "Open a drawing first, or wait for the current request to finish.", "Run routine",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        /// <summary>Open the editor. Returns the saved routine, or null if the user cancelled.</summary>
        public static Routine? Edit(IWin32Window? owner, RoutineLibrary library, Routine content, Routine? original,
            RoutineLocation location, string? banner, bool isDraft)
        {
            using var editor = new RoutineEditorForm(library, content, original, location, banner, isDraft);
            return editor.ShowDialog(owner) == DialogResult.OK ? editor.Saved : null;
        }

        /// <summary>
        /// Open Claude's draft in the editor once BricsCAD is idle (after the request's
        /// command has ended), so the user can check it and save it.
        /// </summary>
        public static void ReviewDraftWhenIdle(RoutineDraft draft)
        {
            EventHandler? handler = null;
            handler = (s, e) =>
            {
                CadApp.Idle -= handler;
                try
                {
                    ReviewDraft(draft);
                }
                catch (Exception ex)
                {
                    ChatPalette.Control?.AppendError("Couldn't open the routine editor: " + ex.Message);
                }
            };
            CadApp.Idle += handler;
        }

        private static void ReviewDraft(RoutineDraft draft)
        {
            RoutineLibrary library = CreateLibrary();
            Routine? original = draft.Replaces;
            RoutineLocation location = original?.Location ?? RoutineLocation.Personal;
            if (location == RoutineLocation.Office && !library.HasOffice)
            {
                location = RoutineLocation.Personal;
            }

            string banner = original != null
                ? $"Claude's update to \"{original.Name}\" in {Routine.LocationName(original.Location)}. Check it, then Save to replace the saved version."
                : "Claude wrote this routine from the chat. Check the inputs and steps, change anything you like, then Save.";
            if (draft.Note != null)
            {
                banner += " " + draft.Note;
            }

            Routine? saved = Edit(RoutinesWindow.OpenInstance, library, draft.Routine, original, location, banner, isDraft: true);
            if (saved == null)
            {
                ChatPalette.Control?.AppendInfo("The routine wasn't saved.");
                return;
            }

            ChatPalette.Control?.AppendInfo($"Saved the routine \"{saved.Name}\" in {Routine.LocationName(saved.Location)}. Run it from Routines on the ribbon.");
            RoutinesWindow.RefreshIfOpen(saved.FilePath);
        }

        // ------------------------------------------------------------------ small dialogs

        /// <summary>Ask for one line of text (e.g. a new name). <paramref name="check"/> returns a problem or null.</summary>
        public static string? AskText(IWin32Window? owner, string title, string label, string initial, Func<string, string?> check)
        {
            using var form = new Form
            {
                Text = title,
                Font = new Font("Segoe UI", 9f),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(420, 130),
            };
            var prompt = new Label { Text = label, AutoSize = true, Location = new Point(12, 12) };
            var box = new TextBox { Text = initial, Location = new Point(12, 34), Width = 396, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            var problem = new Label { AutoSize = false, Location = new Point(12, 62), Size = new Size(396, 30), ForeColor = Color.Firebrick };
            var ok = new Button { Text = "OK", Location = new Point(252, 96), Width = 75 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(333, 96), Width = 75 };
            ok.Click += (s, e) =>
            {
                string? why = check(box.Text.Trim());
                if (why != null)
                {
                    problem.Text = why;
                    return;
                }

                form.DialogResult = DialogResult.OK;
            };
            form.Controls.AddRange(new Control[] { prompt, box, problem, ok, cancel });
            form.AcceptButton = ok;
            form.CancelButton = cancel;
            form.Shown += (s, e) => box.SelectAll();
            return form.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
        }

        public static void ShowError(IWin32Window? owner, string title, string message) =>
            MessageBox.Show(owner, message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
