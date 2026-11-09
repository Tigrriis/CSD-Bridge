using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Bricscad.ApplicationServices;
using CsdBridge.BricsCAD.Csd;
using CsdBridge.BricsCAD.Routines;
using CsdBridge.Core.Civil;
using CsdBridge.Core.Vehicles;
using CsdBridge.Core.Agent;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.History;
using CsdBridge.Core.Routines;
using CsdBridge.Core.Tools;
using Teigha.DatabaseServices;

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// Owns one Claude conversation per open drawing and runs requests. Everything here
    /// is called on BricsCAD's main thread.
    /// </summary>
    internal static class AgentRunner
    {
        private static readonly Dictionary<Document, DraftingAgent> Agents = new Dictionary<Document, DraftingAgent>();
        private static AnthropicClient? _client;
        private static BridgeSettings? _settings;
        private static CancellationTokenSource? _current;
        private static RunRequest? _pending;
        private static RoutineDraft? _draft; // set by save_routine on the agent's thread
        private static EffortMode? _effortMode;
        private static string? _model;

        /// <summary>Raised with true when a request starts and false when it ends.</summary>
        public static event Action<bool>? BusyChanged;

        /// <summary>Raised when the effort mode changes (chat panel, AIEFFORT or settings reload).</summary>
        public static event Action<EffortMode>? EffortModeChanged;

        /// <summary>Raised when the model changes (chat panel, AIMODEL or settings reload).</summary>
        public static event Action<string>? ModelChanged;

        /// <summary>Raised when a request starts, after each step, and when it finishes.</summary>
        public static event Action<RunRecord>? RunUpdated;

        public static bool IsBusy => _current != null;

        /// <summary>The request running now, if any (not saved to history until it ends).</summary>
        public static RunRecord? CurrentRun { get; private set; }

        /// <summary>Saved requests (%LOCALAPPDATA%\CsdBridge\logs\yyyy-MM-dd.jsonl).</summary>
        public static HistoryStore History { get; } = new HistoryStore(BridgeSettings.LogFolder);

        public static BridgeSettings Settings => _settings ??= BridgeSettings.Load();

        /// <summary>Auto, Quick, Normal or Thorough; from settings.json until changed.</summary>
        public static EffortMode EffortMode =>
            _effortMode ??= EffortPolicy.TryParseMode(Settings.EffortModeName, out EffortMode m) ? m : EffortMode.Auto;

        /// <summary>The model for requests (an API id); from settings.json until changed.</summary>
        public static string Model =>
            _model ??= string.IsNullOrWhiteSpace(Settings.Model) ? ModelCatalog.Opus : Settings.Model.Trim();

        /// <summary>
        /// Change the model and remember it in settings.json. It applies from the next request,
        /// and a drawing's conversation carries on with the new model.
        /// </summary>
        public static void SetModel(string model)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                return;
            }

            _model = model.Trim();
            try
            {
                // Re-read the file so manual edits made since loading aren't overwritten.
                BridgeSettings onDisk = BridgeSettings.Load();
                onDisk.Model = _model;
                onDisk.Save();
                if (_settings != null)
                {
                    _settings.Model = _model;
                }
            }
            catch (Exception)
            {
                // Still applies for this session.
            }

            NotifyModel();
        }

        /// <summary>Change the effort mode and remember it in settings.json.</summary>
        public static void SetEffortMode(EffortMode mode)
        {
            _effortMode = mode;
            try
            {
                // Re-read the file so manual edits made since loading aren't overwritten.
                BridgeSettings onDisk = BridgeSettings.Load();
                onDisk.EffortModeName = mode.ToString().ToLowerInvariant();
                onDisk.Save();
                if (_settings != null)
                {
                    _settings.EffortModeName = onDisk.EffortModeName;
                }
            }
            catch (Exception)
            {
                // Still applies for this session.
            }

            NotifyEffortMode();
        }

        /// <summary>
        /// Set the Civil Site Design tool mode (off / read / edit) and remember it. Read and
        /// edit take effect at once; switching to or from off needs a new conversation,
        /// because the tool list is fixed for a conversation. Returns true when it does.
        /// </summary>
        public static bool SetCsdMode(string mode)
        {
            string old = Settings.CsdMode ?? "read";
            try
            {
                BridgeSettings onDisk = BridgeSettings.Load();
                onDisk.CsdMode = mode;
                onDisk.Save();
            }
            catch (Exception)
            {
                // Still applies for this session.
            }

            Settings.CsdMode = mode;
            return string.Equals(old, "off", StringComparison.OrdinalIgnoreCase) != string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase);
        }

        private static void NotifyModel()
        {
            try
            {
                ModelChanged?.Invoke(Model);
            }
            catch (Exception)
            {
                // UI listeners must not break anything.
            }
        }

        private static void NotifyEffortMode()
        {
            try
            {
                EffortModeChanged?.Invoke(EffortMode);
            }
            catch (Exception)
            {
                // UI listeners must not break anything.
            }
        }

        /// <summary>Drop all conversations and reload settings / API key on next use.</summary>
        public static void ResetAll()
        {
            if (IsBusy)
            {
                throw new InvalidOperationException("A request is running. Press Esc first.");
            }

            Agents.Clear();
            _client = null;
            _settings = null;
        }

        public static void Forget(Document doc) => Agents.Remove(doc);

        /// <summary>Re-read settings.json and the API key for conversations started from now on.</summary>
        public static void ReloadSettings()
        {
            _settings = null;
            _client = null;
            _effortMode = null;
            _model = null;
            NotifyEffortMode();
            NotifyModel();
        }

        public static DraftingAgent? Peek(Document doc) => Agents.TryGetValue(doc, out DraftingAgent? a) ? a : null;

        public static void Cancel() => _current?.Cancel();

        /// <summary>
        /// Run a prompt in the active drawing from a panel or window (not a command).
        /// It starts our run command so the request runs in command context like AIDRAFT.
        /// Returns false if there is no drawing open or a request is already running.
        /// </summary>
        public static bool QueueRun(string prompt) => QueueRun(new RunRequest(prompt));

        /// <summary>As <see cref="QueueRun(string)"/>, for a routine run or other request with options.</summary>
        public static bool QueueRun(RunRequest request)
        {
            Document? doc = Bricscad.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null || IsBusy || string.IsNullOrWhiteSpace(request.Prompt))
            {
                return false;
            }

            _pending = request;
            doc.SendStringToExecute(Commands.QueuedRunCommand + " ", true, false, false);
            return true;
        }

        public static RunRequest? TakePendingRequest()
        {
            RunRequest? r = _pending;
            _pending = null;
            return r;
        }

        /// <summary>
        /// Ask Claude to turn this drawing's chat into a routine (save_routine). The routine
        /// editor opens with its draft when the request ends. Returns why it couldn't start, or null.
        /// </summary>
        public static string? QueueSaveChatAsRoutine()
        {
            Document? doc = Bricscad.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return "Open a drawing first.";
            }

            if (IsBusy)
            {
                return "Wait for the current request to finish.";
            }

            if (Peek(doc) is not { MessageCount: > 0 })
            {
                return "There's nothing in this drawing's chat yet. Do the job with Claude first, then save it as a routine.";
            }

            var request = new RunRequest(RoutineTools.SaveChatRequest)
            {
                Display = "Save this chat as a routine",
                Effort = EffortPolicy.Fixed(EffortMode.Normal, Settings.EffortLevels(), "writing a routine"),
            };
            return QueueRun(request) ? null : "Couldn't start the request.";
        }

        /// <summary>
        /// Run one request to completion. Must be called from inside a command (AIDRAFT or
        /// the palette's run command) so the document is locked and undo is grouped.
        /// </summary>
        public static void Run(Document doc, RunRequest request, ObjectId[]? preselected)
        {
            var output = new RunOutput(doc);
            if (IsBusy)
            {
                output.Error("A request is already running. Press Esc or Stop to cancel it.");
                return;
            }

            if (request.NewChat)
            {
                Forget(doc);
                ChatPalette.Control?.ClearLog();
                ChatPalette.Control?.AppendInfo("New conversation for " + Path.GetFileName(doc.Name) + ".");
            }

            string prompt = request.Prompt;
            DraftingAgent? agent = GetOrCreateAgent(doc, out string? error);
            string? switchNotice = null;
            if (agent != null && !string.Equals(agent.Model, Model, StringComparison.OrdinalIgnoreCase))
            {
                agent = SwitchModel(doc, agent, out switchNotice, out error);
            }

            if (agent == null)
            {
                output.Error(error ?? "Could not start Claude.");
                return;
            }

            string fullPrompt = WithSelectionContext(doc, prompt, preselected);
            if (agent.MessageCount == 0 && NotesFor(doc).AsContext() is string notes)
            {
                // Notes go in the first user message (not the system prompt), so the system
                // prompt stays the same for every drawing and its cache is shared.
                fullPrompt = notes + "\n\n" + fullPrompt;
            }
            EffortChoice effort = request.Effort
                ?? EffortPolicy.Choose(EffortMode, request.EffortText ?? prompt, preselected?.Length ?? 0, Settings.EffortLevels());
            var cts = new CancellationTokenSource();
            _current = cts;
            BusyChanged?.Invoke(true);
            var record = new RunRecord
            {
                Drawing = Path.GetFileName(doc.Name),
                DrawingPath = doc.Name,
                Prompt = prompt,
                Routine = request.Routine,
                SelectedCount = preselected?.Length ?? 0,
                Model = agent.Model,
                Effort = effort.Effort,
                EffortChoice = effort.Describe(),
            };
            CurrentRun = record;
            string modelLabel = ModelCatalog.Label(agent.Model);
            output.Started(request.Display ?? prompt, modelLabel, effort);
            if (switchNotice != null)
            {
                output.Notice(switchNotice);
            }

            Notify(record);

            try
            {
                var progress = new PumpedProgress(e =>
                {
                    output.Event(e);
                    record.Record(e);
                    if (e.Kind != AgentEventKind.Waiting)
                    {
                        Notify(record);
                    }
                });

                Task<AgentRunResult> task = Task.Run(() => agent.RunAsync(fullPrompt, progress, cts.Token, effort.Effort));

                MainThreadPump.Instance.PumpUntil(task, EscapePressed, () =>
                {
                    if (!cts.IsCancellationRequested)
                    {
                        cts.Cancel();
                        output.Notice("Cancelling...");
                    }
                });

                AgentRunResult result = task.GetAwaiter().GetResult();
                output.Finished(result, modelLabel, effort);
                record.Complete(result);
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                record.Fail(ex.Message);
            }
            finally
            {
                _current = null;
                CurrentRun = null;
                cts.Dispose();
                Save(record);
                BusyChanged?.Invoke(false);
                Notify(record);
            }

            // Claude wrote a routine (save_routine): open it in the editor once this command ends.
            RoutineDraft? draft = Interlocked.Exchange(ref _draft, null);
            if (draft != null)
            {
                RoutineUi.ReviewDraftWhenIdle(draft);
            }
            else if (request.Routine != null && record.Status == nameof(AgentRunStatus.Completed))
            {
                ChatPalette.Control?.AppendInfo($"Had to correct Claude? Routines > Save this chat as a routine updates \"{request.Routine}\" with what changed.");
            }
        }

        private static void Save(RunRecord record)
        {
            try
            {
                History.Append(record);
            }
            catch (Exception)
            {
                // History must never break a drafting request.
            }
        }

        private static void Notify(RunRecord record)
        {
            try
            {
                RunUpdated?.Invoke(record);
            }
            catch (Exception)
            {
                // A UI problem in the history window must not stop the request.
            }
        }

        /// <summary>Notes Claude has saved about this drawing (see DrawingNotes).</summary>
        public static DrawingNotes NotesFor(Document doc) =>
            new DrawingNotes(Path.Combine(Path.GetDirectoryName(BridgeSettings.LogFolder)!, "notes"), doc.Name);

        /// <summary>
        /// Move the drawing's conversation to the current model. It carries on where it can;
        /// otherwise (a model that can't read the conversation's effort changes) a new one starts.
        /// </summary>
        private static DraftingAgent? SwitchModel(Document doc, DraftingAgent agent, out string? notice, out string? error)
        {
            string from = ModelCatalog.Label(agent.Model);
            string to = ModelCatalog.Label(Model);
            error = null;
            if (agent.CanSwitchTo(Model))
            {
                agent.SwitchModel(Model);
                notice = agent.MessageCount > 0 ? $"Switched from {from} to {to}; the conversation carries on." : null;
                return agent;
            }

            Forget(doc);
            notice = $"Started a new conversation: {to} can't continue the {from} one.";
            return GetOrCreateAgent(doc, out error);
        }

        private static DraftingAgent? GetOrCreateAgent(Document doc, out string? error)
        {
            error = null;
            if (Agents.TryGetValue(doc, out DraftingAgent? existing))
            {
                return existing;
            }

            BridgeSettings settings = Settings;
            string? key = BridgeSettings.GetApiKey(out _);
            if (key == null && string.IsNullOrWhiteSpace(settings.BaseUrl))
            {
                error = "No Anthropic API key. Run AIKEY to set one (or set ANTHROPIC_API_KEY).";
                return null;
            }

            if (_client == null)
            {
                _client = string.IsNullOrWhiteSpace(settings.BaseUrl)
                    ? new AnthropicClient { ApiKey = key }
                    : new AnthropicClient { ApiKey = key ?? "via-gateway", BaseUrl = settings.BaseUrl! };
            }

            RoadConventions? conventions = settings.LoadRoadConventions(out string? conventionsError);
            TurnPathConventions? turnPaths = settings.LoadTurnPathConventions(out string? turnPathsError);
            var extensions = new List<IToolExtension>
            {
                new ProfileTools(),
                new DrawingNotesTools(NotesFor(doc)),
                new RoadDraftingTools(new BricsCadDrawingHost(doc), conventions, conventionsError),
                new RoutineTools(name => RoutineUi.CreateLibrary().Find(name), draft => Interlocked.Exchange(ref _draft, draft)),
                new TurnPathTools(new BricsCadDrawingHost(doc), turnPaths, turnPathsError),
                new GroundClearanceTools(new BricsCadDrawingHost(doc), turnPaths, turnPathsError),
            };
            if (!string.Equals(settings.CsdMode, "off", StringComparison.OrdinalIgnoreCase))
            {
                extensions.Add(new CsdTools(doc, () => Settings.CsdMode));
            }

            AgentOptions options = settings.ToAgentOptions();
            options.Model = Model;
            var agent = new DraftingAgent(
                _client,
                new ToolDispatcher(new BricsCadDrawingHost(doc)),
                options,
                MainThreadPump.Instance,
                extensions);
            Agents[doc] = agent;
            return agent;
        }

        /// <summary>
        /// Objects selected before the request go into the user message (not the system
        /// prompt, which must stay fixed for caching).
        /// </summary>
        private static string WithSelectionContext(Document doc, string prompt, ObjectId[]? preselected)
        {
            if (preselected == null || preselected.Length == 0)
            {
                return prompt;
            }

            List<EntitySummary> summaries = new BricsCadDrawingHost(doc).SummarizeIds(preselected, 50);
            var sb = new StringBuilder(prompt);
            sb.Append("\n\n<cad_context>\nThe user had ").Append(preselected.Length)
              .Append(" object(s) selected when making this request");
            if (preselected.Length > summaries.Count)
            {
                sb.Append(" (first ").Append(summaries.Count).Append(" shown)");
            }

            sb.Append(":\n").Append(JsonSerializer.Serialize(summaries, ToolDispatcher.JsonOptions)).Append("\n</cad_context>");
            return sb.ToString();
        }

        private static bool EscapePressed()
        {
            try
            {
                return HostApplicationServices.Current.UserBreak();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Writes progress to the command line, and to the palette when it is open.</summary>
        private sealed class RunOutput
        {
            private readonly Document _doc;

            public RunOutput(Document doc)
            {
                _doc = doc;
            }

            public void Started(string prompt, string model, EffortChoice effort)
            {
                Write($"\nCSD-Bridge: working, {model}, {effort.Describe()} effort... (Esc to cancel)");
                ChatPalette.Control?.AppendUser(prompt);
                ChatPalette.Control?.AppendTool($"{model}, {effort.Describe()} effort");
                ChatPalette.Control?.SetStatus("Working... (Esc or Stop to cancel)");
            }

            public void Event(AgentEvent e)
            {
                switch (e.Kind)
                {
                    case AgentEventKind.AssistantText:
                        Write("\nClaude: " + e.Text);
                        ChatPalette.Control?.AppendAssistant(e.Text);
                        break;
                    case AgentEventKind.ToolStarted:
                        Write("\n  > " + e.Text);
                        ChatPalette.Control?.AppendTool(e.Text);
                        break;
                    case AgentEventKind.ToolFinished when e.IsError:
                        Write("\n  ! " + Shorten(e.Text));
                        ChatPalette.Control?.AppendError(Shorten(e.Text));
                        break;
                    case AgentEventKind.Notice:
                        Notice(e.Text);
                        break;
                }
            }

            public void Notice(string text)
            {
                Write("\n  " + text);
                ChatPalette.Control?.AppendTool(text);
            }

            public void Finished(AgentRunResult r, string model, EffortChoice effort)
            {
                string status = r.Status == AgentRunStatus.Completed ? "Done" : r.Status.ToString();
                if (r.Status == AgentRunStatus.Refused)
                {
                    Write("\nClaude: " + r.FinalText);
                    ChatPalette.Control?.AppendError(r.FinalText);
                }

                if (r.Error != null)
                {
                    Error(r.Error);
                }

                string summary = $"{status} - {model}, {effort.Tier} effort, {r.ToolCalls} tool call(s), {r.Usage}. Use U to undo.";
                Write("\nCSD-Bridge: " + summary + "\n");
                ChatPalette.Control?.SetStatus(summary);
            }

            public void Error(string message)
            {
                Write("\nCSD-Bridge: " + message + "\n");
                ChatPalette.Control?.AppendError(message);
                ChatPalette.Control?.SetStatus(message);
            }

            private void Write(string text) => _doc.Editor.WriteMessage(text);

            private static string Shorten(string s) => s.Length > 300 ? s.Substring(0, 300) + "..." : s;
        }
    }

    /// <summary>One request to run: a typed prompt, or a routine with its options.</summary>
    internal sealed class RunRequest
    {
        public RunRequest(string prompt)
        {
            Prompt = (prompt ?? "").Trim();
        }

        /// <summary>What is sent to Claude.</summary>
        public string Prompt { get; }

        /// <summary>Shown in the chat panel and on the command line instead of the prompt (e.g. "Routine: Kerb returns").</summary>
        public string? Display { get; set; }

        /// <summary>The routine being run, recorded in the history.</summary>
        public string? Routine { get; set; }

        /// <summary>A fixed effort (set by a routine); null chooses from the effort mode as usual.</summary>
        public EffortChoice? Effort { get; set; }

        /// <summary>Text Auto effort judges instead of the prompt (a routine's instructions).</summary>
        public string? EffortText { get; set; }

        /// <summary>Start a new conversation for the drawing first.</summary>
        public bool NewChat { get; set; }
    }
}
