using System.IO;
using Bricscad.ApplicationServices;
using Bricscad.EditorInput;
using CsdBridge.Core.Agent;
using Teigha.DatabaseServices;
using Teigha.Runtime;
using CadApp = Bricscad.ApplicationServices.Application;

[assembly: CommandClass(typeof(CsdBridge.BricsCAD.Commands))]

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// AIDRAFT    ask Claude to draw or change something (command line)
    /// AIPANEL    open the chat panel
    /// AIHISTORY  open the history of requests to Claude
    /// AIROUTINES open the routines: saved procedures to run, create, edit, rename or delete
    /// AITURN     draw an AS/NZS 2890.1 B85/B99 vehicle swept path (turning template or along a path)
    /// AIRIBBON   add the CSD-Bridge ribbon tab again (if a workspace change removed it)
    /// AIEFFORT   choose how hard Claude works: Auto, Quick, Normal or Thorough
    /// AIMODEL    choose the Claude model: Opus, Sonnet or Haiku
    /// AIUIDUMP   diagnostic: save the controls of open dialogs to a text file
    /// AIFORMDUMP diagnostic: save open dialogs' WinForms controls and grid cells (read from inside)
    /// AICSD      Civil Site Design tools: read only, edit, off, or list CSD's commands
    /// AISCALEBARS add scale bar blocks to every layout from its viewport scales (no Claude call)
    /// AIRESET    start a new conversation for this drawing
    /// AIKEY      set or remove the Anthropic API key
    /// AISTATUS   show settings, key source and usage
    /// </summary>
    public sealed class Commands
    {
        private const string Group = "CSDBRIDGE";

        /// <summary>Internal: run the prompt queued by the chat panel, history window or ribbon.</summary>
        internal const string QueuedRunCommand = "CSDAI_PALETTE_RUN";

        [CommandMethod(Group, "AIDRAFT", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void AiDraft()
        {
            Document doc = CadApp.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            ObjectId[]? preselected = Preselection(ed);

            var options = new PromptStringOptions("\nWhat should Claude draw or change? ") { AllowSpaces = true };
            PromptResult res = ed.GetString(options);
            if (res.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(res.StringResult))
            {
                return;
            }

            AgentRunner.Run(doc, new RunRequest(res.StringResult), preselected);
        }

        [CommandMethod(Group, QueuedRunCommand, CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void QueuedRun()
        {
            RunRequest? request = AgentRunner.TakePendingRequest();
            if (request == null)
            {
                return;
            }

            Document doc = CadApp.DocumentManager.MdiActiveDocument;
            AgentRunner.Run(doc, request, Preselection(doc.Editor));
        }

        [CommandMethod(Group, "AIPANEL", CommandFlags.Modal)]
        public void AiPanel() => ChatPalette.Show();

        [CommandMethod(Group, "AIEFFORT", CommandFlags.Modal)]
        public void AiEffort()
        {
            Editor ed = CadApp.DocumentManager.MdiActiveDocument.Editor;
            var options = new PromptKeywordOptions($"\nEffort for Claude requests (now {AgentRunner.EffortMode})");
            options.Keywords.Add("Auto");
            options.Keywords.Add("Quick");
            options.Keywords.Add("Normal");
            options.Keywords.Add("Thorough");
            options.Keywords.Default = AgentRunner.EffortMode.ToString();
            options.AllowNone = true;

            PromptResult res = ed.GetKeywords(options);
            if (res.Status != PromptStatus.OK || !EffortPolicy.TryParseMode(res.StringResult, out EffortMode mode))
            {
                return;
            }

            AgentRunner.SetEffortMode(mode);
            ed.WriteMessage(mode == EffortMode.Auto
                ? "\nCSD-Bridge: effort is Auto: Quick for simple edits and questions, Thorough for big multi-step jobs, Normal otherwise.\n"
                : $"\nCSD-Bridge: effort is {mode} for every request until you change it.\n");
        }

        [CommandMethod(Group, "AIMODEL", CommandFlags.Modal)]
        public void AiModel()
        {
            Editor ed = CadApp.DocumentManager.MdiActiveDocument.Editor;
            var options = new PromptKeywordOptions($"\nClaude model (now {ModelCatalog.Label(AgentRunner.Model)})");
            foreach (ModelChoice c in ModelCatalog.Choices)
            {
                options.Keywords.Add(Keyword(c));
            }

            if (ModelCatalog.Find(AgentRunner.Model) is ModelChoice current)
            {
                options.Keywords.Default = Keyword(current);
            }

            options.AllowNone = true;

            PromptResult res = ed.GetKeywords(options);
            if (res.Status != PromptStatus.OK || !(ModelCatalog.Find(res.StringResult) is ModelChoice choice))
            {
                return;
            }

            AgentRunner.SetModel(choice.Id);
            ed.WriteMessage(
                $"\nCSD-Bridge: {choice.Label} ({choice.Description}) from the next request." +
                "\nConversations carry on with it; AIRESET starts a clean one.\n");
        }

        /// <summary>"Opus", "Sonnet", "Haiku": the first word of the model's name.</summary>
        private static string Keyword(ModelChoice c) => c.Label.Split(' ')[0];

        [CommandMethod(Group, "AIUIDUMP", CommandFlags.Modal)]
        public void AiUiDump()
        {
            Editor ed = CadApp.DocumentManager.MdiActiveDocument.Editor;
            if (!TryGetCountdown(ed, out int seconds))
            {
                return;
            }

            UiDump.CaptureAfter(seconds);
            ed.WriteMessage(
                $"\nCSD-Bridge: capturing open windows in {seconds} s. Open the Civil Site Design dialog now and leave it on screen." +
                $"\nYou'll hear a chime when it's saved, and Explorer will open on the file in {UiDump.Folder}\n");
        }

        [CommandMethod(Group, "AICSD", CommandFlags.Modal)]
        public void AiCsd()
        {
            Editor ed = CadApp.DocumentManager.MdiActiveDocument.Editor;
            string current = (AgentRunner.Settings.CsdMode ?? "read").ToLowerInvariant();
            string currentKeyword = current == "edit" ? "Edit" : current == "off" ? "Off" : "Read";
            var options = new PromptKeywordOptions($"\nCivil Site Design tools (now {Describe(current)})");
            options.Keywords.Add("Read");
            options.Keywords.Add("Edit");
            options.Keywords.Add("Off");
            options.Keywords.Add("List");
            options.Keywords.Default = currentKeyword;
            options.AllowNone = true;

            PromptResult res = ed.GetKeywords(options);
            if (res.Status != PromptStatus.OK)
            {
                return;
            }

            if (res.StringResult == "List")
            {
                Csd.CsdCommands.Refresh();
                var all = Csd.CsdCommands.All;
                string file = Path.Combine(BridgeSettings.LogFolder, "..", "csd-commands.txt");
                file = Path.GetFullPath(file);
                var lines = new System.Collections.Generic.List<string> { "Civil Site Design commands found by CSD-Bridge", "" };
                lines.AddRange(Csd.CsdCommands.Sources);
                lines.Add("");
                foreach (var c in System.Linq.Enumerable.OrderBy(all.Values, c => c.Name))
                {
                    lines.Add($"{c.Name,-45} {string.Join(" / ", c.Buttons)}{(c.Method == null ? "  (menu only)" : "")}");
                }

                File.WriteAllLines(file, lines);
                ed.WriteMessage($"\nCSD-Bridge: found {all.Count} Civil Site Design commands. List saved to {file}\n");
                if (all.Count > 0)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
                }

                return;
            }

            string mode = res.StringResult.ToLowerInvariant();
            bool needsReset = AgentRunner.SetCsdMode(mode);
            ed.WriteMessage($"\nCSD-Bridge: Civil Site Design tools are {Describe(mode)}." +
                (mode == "edit" ? " Claude can now change CSD values and press buttons. CSD changes are not in BricsCAD's UNDO, so work on a copy of the drawing until you trust it." : "") +
                (needsReset ? " Run AIRESET (or start a new chat) for this to apply." : "") + "\n");
        }

        private static string Describe(string mode) =>
            mode == "edit" ? "on, with edits allowed" : mode == "off" ? "off" : "on, read only";

        [CommandMethod(Group, "AIFORMDUMP", CommandFlags.Modal)]
        public void AiFormDump()
        {
            Editor ed = CadApp.DocumentManager.MdiActiveDocument.Editor;
            if (!TryGetCountdown(ed, out int seconds))
            {
                return;
            }

            FormDump.CaptureAfter(seconds);
            ed.WriteMessage(
                $"\nCSD-Bridge: reading open dialogs from the inside in {seconds} s. Open the Civil Site Design dialog(s) now and leave them on screen." +
                $"\nYou'll hear a chime when it's saved, and Explorer will open on the file in {FormDump.Folder}\n");
        }

        private static bool TryGetCountdown(Editor ed, out int seconds)
        {
            var options = new PromptIntegerOptions("\nSeconds before capturing (open the dialog in that time)")
            {
                DefaultValue = 10,
                UseDefaultValue = true,
                AllowNone = true,
                AllowNegative = false,
                LowerLimit = 0,
                UpperLimit = 120,
            };

            PromptIntegerResult res = ed.GetInteger(options);
            seconds = 10;
            if (res.Status == PromptStatus.OK)
            {
                seconds = res.Value;
                return true;
            }

            return res.Status == PromptStatus.None;
        }

        [CommandMethod(Group, "AIHISTORY", CommandFlags.Modal)]
        public void AiHistory() => HistoryWindow.ShowWindow();

        [CommandMethod(Group, "AIROUTINES", CommandFlags.Modal)]
        public void AiRoutines() => Routines.RoutinesWindow.ShowWindow();
        [CommandMethod(Group, "AITURN", CommandFlags.Modal)]
        public void AiTurn() => TurnPathCommand.Run(CadApp.DocumentManager.MdiActiveDocument);

        [CommandMethod(Group, "AIRIBBON", CommandFlags.Modal)]
        public void AiRibbon()
        {
            string result = RibbonSetup.AddTab(activate: true);
            CadApp.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\nCSD-Bridge: " + result + "\n");
        }

        [CommandMethod(Group, "AISCALEBARS", CommandFlags.Modal)]
        public void AiScaleBars()
        {
            Document doc = CadApp.DocumentManager.MdiActiveDocument;
            if (AgentRunner.IsBusy)
            {
                doc.Editor.WriteMessage("\nA request is running. Press Esc first.\n");
                return;
            }

            ScaleBarTool.Run(doc);
        }

        [CommandMethod(Group, "AIRESET", CommandFlags.Modal)]
        public void AiReset()
        {
            Document doc = CadApp.DocumentManager.MdiActiveDocument;
            if (AgentRunner.IsBusy)
            {
                doc.Editor.WriteMessage("\nA request is running. Press Esc first.\n");
                return;
            }

            AgentRunner.Forget(doc);
            AgentRunner.ReloadSettings();
            ChatPalette.Control?.ClearLog();
            doc.Editor.WriteMessage("\nCSD-Bridge: new conversation started for this drawing (settings reloaded).\n");
        }

        [CommandMethod(Group, "AIKEY", CommandFlags.Modal)]
        public void AiKey()
        {
            Editor ed = CadApp.DocumentManager.MdiActiveDocument.Editor;
            using (var dialog = new ApiKeyDialog())
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                {
                    return;
                }

                BridgeSettings.SetApiKey(dialog.ApiKey);
                AgentRunner.ResetAll();
                ed.WriteMessage(string.IsNullOrWhiteSpace(dialog.ApiKey)
                    ? "\nCSD-Bridge: stored API key removed.\n"
                    : "\nCSD-Bridge: API key saved (encrypted for your Windows account).\n");
            }
        }

        [CommandMethod(Group, "AISTATUS", CommandFlags.Modal)]
        public void AiStatus()
        {
            Document doc = CadApp.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            BridgeSettings s = BridgeSettings.Load(); // as on disk now
            BridgeSettings.GetApiKey(out string keySource);
            string standards = s.ResolvedStandardsPath();

            ed.WriteMessage("\nCSD-Bridge status");
            EffortLevels levels = s.EffortLevels();
            string model = AgentRunner.Model;
            string fallback = !s.ServerSideFallback ? "off" : ModelCatalog.SupportsServerSideFallback(model) ? "on" : "none for this model";
            ed.WriteMessage($"\n  Model:      {ModelCatalog.Label(model)} [{model}] (change with AIMODEL or the chat panel; max {s.MaxTurns} turns, fallback {fallback})");
            ed.WriteMessage($"\n  Effort:     {AgentRunner.EffortMode} (Quick = {levels.Quick}, Normal = {levels.Normal}, Thorough = {levels.Thorough})");
            ed.WriteMessage($"\n  API key:    {keySource}");
            ed.WriteMessage($"\n  Standards:  {standards}{(File.Exists(standards) ? "" : " (not found)")}");
            string? office = s.ResolvedOfficeRoutinesFolder();
            ed.WriteMessage($"\n  Routines:   {BridgeSettings.PersonalRoutinesFolder}");
            ed.WriteMessage($"\n  Office routines: {(office == null ? "none (set office_routines_folder)" : office + (Directory.Exists(office) ? "" : " (not found)"))}");
            ed.WriteMessage($"\n  CSD tools:  {Describe((s.CsdMode ?? "read").ToLowerInvariant())} (change with AICSD)");
            string turnPaths = s.ResolvedTurnPathsPath();
            ed.WriteMessage($"\n  Turn paths: {turnPaths}{(File.Exists(turnPaths) ? "" : " (not found; built-in B99/B85 used)")}");
            ed.WriteMessage($"\n  Settings:   {BridgeSettings.SettingsPath}");
            ed.WriteMessage($"\n  Logs:       {BridgeSettings.LogFolder}");
            if (AgentRunner.Peek(doc) is { } agent)
            {
                ed.WriteMessage($"\n  This drawing: {agent.MessageCount} messages, last on {ModelCatalog.Label(agent.Model)}, {agent.ConversationUsage}");
            }

            ed.WriteMessage("\n  Edit settings.json, then run AIRESET to apply changes.\n");
        }

        private static ObjectId[]? Preselection(Editor ed)
        {
            PromptSelectionResult implied = ed.SelectImplied();
            return implied.Status == PromptStatus.OK ? implied.Value.GetObjectIds() : null;
        }
    }
}
