using System.IO;
using Bricscad.ApplicationServices;
using Bricscad.EditorInput;
using Teigha.DatabaseServices;
using Teigha.Runtime;
using CadApp = Bricscad.ApplicationServices.Application;

[assembly: CommandClass(typeof(CsdBridge.BricsCAD.Commands))]

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// AIDRAFT    ask Claude to draw or change something (command line)
    /// AIPANEL    open the chat panel
    /// AIRESET    start a new conversation for this drawing
    /// AIKEY      set or remove the Anthropic API key
    /// AISTATUS   show settings, key source and usage
    /// </summary>
    public sealed class Commands
    {
        private const string Group = "CSDBRIDGE";

        /// <summary>Internal: run the prompt queued by the chat panel.</summary>
        internal const string PaletteRunCommand = "CSDAI_PALETTE_RUN";

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

            AgentRunner.Run(doc, res.StringResult, preselected);
        }

        [CommandMethod(Group, PaletteRunCommand, CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void PaletteRun()
        {
            string? prompt = ChatPalette.TakePendingPrompt();
            if (prompt == null)
            {
                return;
            }

            Document doc = CadApp.DocumentManager.MdiActiveDocument;
            AgentRunner.Run(doc, prompt, Preselection(doc.Editor));
        }

        [CommandMethod(Group, "AIPANEL", CommandFlags.Modal)]
        public void AiPanel() => ChatPalette.Show();

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
            ed.WriteMessage($"\n  Model:      {s.Model} (effort {s.Effort}, max {s.MaxTurns} turns, fallback {(s.ServerSideFallback ? "on" : "off")})");
            ed.WriteMessage($"\n  API key:    {keySource}");
            ed.WriteMessage($"\n  Standards:  {standards}{(File.Exists(standards) ? "" : " (not found)")}");
            ed.WriteMessage($"\n  Settings:   {BridgeSettings.SettingsPath}");
            ed.WriteMessage($"\n  Logs:       {BridgeSettings.LogFolder}");
            if (AgentRunner.Peek(doc) is { } agent)
            {
                ed.WriteMessage($"\n  This drawing: {agent.MessageCount} messages, {agent.ConversationUsage}");
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
