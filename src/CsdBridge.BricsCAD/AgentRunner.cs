using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Bricscad.ApplicationServices;
using CsdBridge.Core.Agent;
using CsdBridge.Core.Drawing;
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

        /// <summary>Raised with true when a request starts and false when it ends.</summary>
        public static event Action<bool>? BusyChanged;

        public static bool IsBusy => _current != null;

        public static BridgeSettings Settings => _settings ??= BridgeSettings.Load();

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
        }

        public static DraftingAgent? Peek(Document doc) => Agents.TryGetValue(doc, out DraftingAgent? a) ? a : null;

        public static void Cancel() => _current?.Cancel();

        /// <summary>
        /// Run one request to completion. Must be called from inside a command (AIDRAFT or
        /// the palette's run command) so the document is locked and undo is grouped.
        /// </summary>
        public static void Run(Document doc, string prompt, ObjectId[]? preselected)
        {
            var output = new RunOutput(doc);
            if (IsBusy)
            {
                output.Error("A request is already running. Press Esc or Stop to cancel it.");
                return;
            }

            DraftingAgent? agent = GetOrCreateAgent(doc, out string? error);
            if (agent == null)
            {
                output.Error(error ?? "Could not start Claude.");
                return;
            }

            string fullPrompt = WithSelectionContext(doc, prompt, preselected);
            var cts = new CancellationTokenSource();
            _current = cts;
            BusyChanged?.Invoke(true);
            var log = new RunLog(doc, prompt);
            output.Started();

            try
            {
                var progress = new PumpedProgress(e =>
                {
                    output.Event(e);
                    log.Event(e);
                });

                Task<AgentRunResult> task = Task.Run(() => agent.RunAsync(fullPrompt, progress, cts.Token));

                MainThreadPump.Instance.PumpUntil(task, EscapePressed, () =>
                {
                    if (!cts.IsCancellationRequested)
                    {
                        cts.Cancel();
                        output.Notice("Cancelling...");
                    }
                });

                AgentRunResult result = task.GetAwaiter().GetResult();
                output.Finished(result);
                log.Finished(result);
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                log.Line("EXCEPTION " + ex);
            }
            finally
            {
                _current = null;
                cts.Dispose();
                BusyChanged?.Invoke(false);
            }
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

            var agent = new DraftingAgent(
                _client,
                new ToolDispatcher(new BricsCadDrawingHost(doc)),
                settings.ToAgentOptions(),
                MainThreadPump.Instance);
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

            public void Started()
            {
                Write("\nCSD-Bridge: working... (Esc to cancel)");
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

            public void Finished(AgentRunResult r)
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

                string summary = $"{status} - {r.ToolCalls} tool call(s), {r.Usage}. Use U to undo.";
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

        /// <summary>Plain-text log of each request, for reviewing prompts, failures and cost.</summary>
        private sealed class RunLog
        {
            private readonly string _path;

            public RunLog(Document doc, string prompt)
            {
                _path = Path.Combine(BridgeSettings.LogFolder, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
                Line($"---- {DateTime.Now:HH:mm:ss} {Path.GetFileName(doc.Name)}");
                Line("USER " + prompt.Replace("\n", " "));
            }

            public void Event(AgentEvent e)
            {
                if (e.Kind != AgentEventKind.Waiting)
                {
                    Line($"{e.Kind.ToString().ToUpperInvariant()} {(e.ToolName == null ? "" : e.ToolName + ": ")}{e.Text.Replace("\n", " ")}{(e.IsError ? " [error]" : "")}");
                }
            }

            public void Finished(AgentRunResult r) =>
                Line($"RESULT {r.Status} tools={r.ToolCalls} usage=[{r.Usage}]{(r.Error == null ? "" : " error=" + r.Error)}");

            public void Line(string text)
            {
                try
                {
                    Directory.CreateDirectory(BridgeSettings.LogFolder);
                    File.AppendAllText(_path, text + Environment.NewLine);
                }
                catch (Exception)
                {
                    // Logging must never break a drafting request.
                }
            }
        }
    }
}
