using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using CsdBridge.Core.Tools;

namespace CsdBridge.Core.Agent
{
    /// <summary>
    /// The Claude tool-use loop. One instance holds one conversation (typically one per
    /// open drawing), so follow-up requests like "make it 200 wider" have context.
    ///
    /// Threading: <see cref="RunAsync"/> is meant to run on a background thread. Tool
    /// calls are marshalled to the CAD main thread through <see cref="IMainThreadInvoker"/>.
    ///
    /// History rules (important for prompt caching and for Claude's thinking blocks):
    /// the conversation is append-only. The system prompt and tool list are fixed for
    /// the agent's lifetime, assistant turns are echoed back exactly as received, and
    /// every tool_use gets exactly one tool_result in the next user message.
    /// </summary>
    public sealed class DraftingAgent
    {
        private const string FallbackBeta = "server-side-fallback-2026-07-01";

        private readonly AnthropicClient _client;
        private readonly ToolDispatcher _dispatcher;
        private readonly AgentOptions _options;
        private readonly IMainThreadInvoker _invoker;
        private readonly string _systemPrompt;
        private readonly List<BetaToolUnion> _tools;
        private readonly List<BetaMessageParam> _messages = new List<BetaMessageParam>();
        private int _running;

        public DraftingAgent(AnthropicClient client, ToolDispatcher dispatcher, AgentOptions options, IMainThreadInvoker? invoker = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _invoker = invoker ?? InlineInvoker.Instance;
            _systemPrompt = SystemPrompt.Build(options);
            _tools = ToolCatalog.All.Select(ToSdkTool).ToList();
        }

        public bool IsRunning => Volatile.Read(ref _running) == 1;

        /// <summary>Number of messages in the conversation so far.</summary>
        public int MessageCount => _messages.Count;

        /// <summary>Total usage across every request in this conversation.</summary>
        public UsageTotals ConversationUsage { get; } = new UsageTotals();

        /// <summary>Forget the conversation. The next request starts fresh.</summary>
        public void Reset()
        {
            if (IsRunning)
            {
                throw new InvalidOperationException("Cannot reset while a request is running.");
            }

            _messages.Clear();
        }

        public async Task<AgentRunResult> RunAsync(string userPrompt, IProgress<AgentEvent>? progress = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userPrompt))
            {
                throw new ArgumentException("Prompt is empty.", nameof(userPrompt));
            }

            if (Interlocked.Exchange(ref _running, 1) == 1)
            {
                throw new InvalidOperationException("A request is already running for this drawing.");
            }

            try
            {
                AgentRunResult result = await RunCoreAsync(userPrompt, progress, ct).ConfigureAwait(false);
                ConversationUsage.Add(result.Usage);
                return result;
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        }

        private async Task<AgentRunResult> RunCoreAsync(string userPrompt, IProgress<AgentEvent>? progress, CancellationToken ct)
        {
            var result = new AgentRunResult();
            int historyLengthAtStart = _messages.Count;
            _messages.Add(new BetaUserMessageParam { Content = userPrompt });

            for (int turn = 1; ; turn++)
            {
                if (ct.IsCancellationRequested)
                {
                    result.Status = AgentRunStatus.Cancelled;
                    return result;
                }

                if (turn > _options.MaxTurns)
                {
                    // History is valid here: the last message is a user turn of tool results.
                    result.Status = AgentRunStatus.TurnLimitReached;
                    result.Error = $"Stopped after {_options.MaxTurns} model turns. Send another message to let Claude continue.";
                    return result;
                }

                progress?.Report(new AgentEvent(AgentEventKind.Waiting, turn.ToString(System.Globalization.CultureInfo.InvariantCulture)));

                BetaMessage response;
                try
                {
                    response = await _client.Beta.Messages.Create(BuildParams(), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    result.Status = AgentRunStatus.Cancelled;
                    return result;
                }
                catch (Exception ex)
                {
                    // The request never produced an assistant turn, so history is still valid
                    // (consecutive user turns are merged by the API).
                    result.Status = AgentRunStatus.Failed;
                    result.Error = DescribeApiError(ex);
                    return result;
                }

                AddUsage(result.Usage, response);
                string stopReason = StopReasonOf(response);

                if (stopReason == "refusal")
                {
                    result.Status = AgentRunStatus.Refused;
                    result.FinalText = "Claude declined this request" +
                        (response.StopDetails?.Category is { } cat ? $" (category: {cat})." : ".");

                    // Nothing in the drawing changed during this request, so drop it from the
                    // history to keep it out of future turns. (Trimming the tail is fine; it
                    // is only edits to earlier turns that break the history rules.)
                    if (result.ToolCalls == 0)
                    {
                        _messages.RemoveRange(historyLengthAtStart, _messages.Count - historyLengthAtStart);
                    }

                    return result;
                }

                AssistantTurn assistant = ConvertAssistantTurn(response.Content);
                if (assistant.Blocks.Count > 0)
                {
                    _messages.Add(new BetaAssistantMessageParam { Content = assistant.Blocks });
                }

                foreach (string text in assistant.Texts)
                {
                    progress?.Report(new AgentEvent(AgentEventKind.AssistantText, text));
                }

                if (assistant.Texts.Count > 0)
                {
                    result.FinalText = string.Join("\n\n", assistant.Texts);
                }

                if (assistant.ToolUses.Count == 0)
                {
                    if (stopReason == "max_tokens")
                    {
                        result.FinalText += "\n\n(Claude's reply was cut off at the output limit.)";
                    }

                    result.Status = AgentRunStatus.Completed;
                    return result;
                }

                var toolResults = new List<BetaContentBlockParam>(assistant.ToolUses.Count);
                foreach (BetaToolUseBlock toolUse in assistant.ToolUses)
                {
                    ToolOutcome outcome;
                    if (stopReason == "max_tokens")
                    {
                        // The reply was cut off, so this call's input may be incomplete. Don't run it.
                        outcome = new ToolOutcome(
                            "Not run: your reply hit the output token limit, so this call may be incomplete. " +
                            "Re-issue it with fewer items per call.",
                            true);
                        progress?.Report(new AgentEvent(AgentEventKind.Notice, "Reply was cut off; asking Claude to resend in smaller batches."));
                    }
                    else if (ct.IsCancellationRequested)
                    {
                        outcome = new ToolOutcome("Not run: the user cancelled the request.", true);
                    }
                    else
                    {
                        outcome = await RunToolAsync(toolUse, progress).ConfigureAwait(false);
                        result.ToolCalls++;
                    }

                    // Every tool_use must get exactly one tool_result, all in one user message.
                    toolResults.Add(new BetaToolResultBlockParam
                    {
                        ToolUseID = toolUse.ID,
                        Content = outcome.Content,
                        IsError = outcome.IsError,
                    });
                }

                _messages.Add(new BetaUserMessageParam { Content = toolResults });
            }
        }

        private async Task<ToolOutcome> RunToolAsync(BetaToolUseBlock toolUse, IProgress<AgentEvent>? progress)
        {
            JsonElement input = JsonSerializer.SerializeToElement(toolUse.Input);
            progress?.Report(new AgentEvent(AgentEventKind.ToolStarted, DescribeToolCall(toolUse.Name, input), toolUse.Name));

            ToolOutcome outcome;
            try
            {
                outcome = await _invoker.InvokeAsync(() => _dispatcher.Execute(toolUse.Name, input)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                outcome = new ToolOutcome($"{toolUse.Name} could not run: {ex.Message}", true) { Summary = ex.Message };
            }

            progress?.Report(new AgentEvent(AgentEventKind.ToolFinished, outcome.Summary ?? "", toolUse.Name, outcome.IsError));
            return outcome;
        }

        private MessageCreateParams BuildParams()
        {
            var effort = new BetaOutputConfig { Effort = ParseEffort(_options.Effort) };

            // A copy of the history, so later appends never alias a request in flight.
            var messages = new List<BetaMessageParam>(_messages);

            if (_options.ServerSideFallback)
            {
                return new MessageCreateParams
                {
                    Model = _options.Model,
                    MaxTokens = _options.MaxTokens,
                    System = _systemPrompt,
                    Messages = messages,
                    Tools = _tools,
                    Thinking = new BetaThinkingConfigAdaptive(),
                    OutputConfig = effort,
                    CacheControl = new BetaCacheControlEphemeral(),
                    Betas = [FallbackBeta],
                    Fallbacks = new Default(), // serialises as "default"
                };
            }

            return new MessageCreateParams
            {
                Model = _options.Model,
                MaxTokens = _options.MaxTokens,
                System = _systemPrompt,
                Messages = messages,
                Tools = _tools,
                Thinking = new BetaThinkingConfigAdaptive(),
                OutputConfig = effort,
                CacheControl = new BetaCacheControlEphemeral(),
            };
        }

        private sealed class AssistantTurn
        {
            public List<BetaContentBlockParam> Blocks { get; } = new List<BetaContentBlockParam>();
            public List<BetaToolUseBlock> ToolUses { get; } = new List<BetaToolUseBlock>();
            public List<string> Texts { get; } = new List<string>();
        }

        /// <summary>
        /// Turn the response content into the assistant message we send back next time.
        /// Thinking blocks are echoed unchanged (signature included). If a server-side
        /// fallback happened mid-reply, blocks before the last fallback marker other than
        /// text are dropped, as the API requires.
        /// </summary>
        private static AssistantTurn ConvertAssistantTurn(IReadOnlyList<BetaContentBlock> content)
        {
            int lastFallback = -1;
            for (int i = 0; i < content.Count; i++)
            {
                if (content[i].TryPickFallback(out _))
                {
                    lastFallback = i;
                }
            }

            var turn = new AssistantTurn();
            for (int i = 0; i < content.Count; i++)
            {
                BetaContentBlock block = content[i];
                bool beforeFallback = i < lastFallback;

                if (block.TryPickText(out BetaTextBlock? text))
                {
                    turn.Blocks.Add(new BetaTextBlockParam { Text = text.Text });
                    if (!beforeFallback && !string.IsNullOrWhiteSpace(text.Text))
                    {
                        turn.Texts.Add(text.Text.Trim());
                    }
                }
                else if (beforeFallback)
                {
                    // Partial output from a model that declined; not echoed.
                }
                else if (block.TryPickThinking(out BetaThinkingBlock? thinking))
                {
                    turn.Blocks.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                }
                else if (block.TryPickRedactedThinking(out BetaRedactedThinkingBlock? redacted))
                {
                    turn.Blocks.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
                }
                else if (block.TryPickToolUse(out BetaToolUseBlock? toolUse))
                {
                    turn.Blocks.Add(new BetaToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                    turn.ToolUses.Add(toolUse);
                }

                // Fallback markers are audit-only and may be dropped. No server tools are
                // enabled, so no other block types are expected.
            }

            return turn;
        }

        private static BetaToolUnion ToSdkTool(ToolDefinition definition)
        {
            using JsonDocument doc = JsonDocument.Parse(definition.InputSchemaJson);
            JsonElement root = doc.RootElement;

            var properties = new Dictionary<string, JsonElement>();
            if (root.TryGetProperty("properties", out JsonElement props))
            {
                foreach (JsonProperty p in props.EnumerateObject())
                {
                    properties[p.Name] = p.Value.Clone();
                }
            }

            List<string>? required = root.TryGetProperty("required", out JsonElement req)
                ? req.EnumerateArray().Select(r => r.GetString()!).ToList()
                : null;

            return new BetaTool
            {
                Name = definition.Name,
                Description = definition.Description,
                InputSchema = new() { Properties = properties, Required = required },
            };
        }

        private static string ParseEffortName(string effort) => (effort ?? "").Trim().ToLowerInvariant();

        private static Effort ParseEffort(string effort)
        {
            switch (ParseEffortName(effort))
            {
                case "low": return Effort.Low;
                case "medium": return Effort.Medium;
                case "xhigh": return Effort.Xhigh;
                case "max": return Effort.Max;
                default: return Effort.High;
            }
        }

        /// <summary>The raw wire value ("end_turn", "tool_use", "max_tokens", "refusal", ...).</summary>
        private static string StopReasonOf(BetaMessage response) => response.StopReason?.Raw() ?? "";

        private static void AddUsage(UsageTotals totals, BetaMessage response)
        {
            totals.Requests++;
            if (response.Usage is { } u)
            {
                totals.InputTokens += u.InputTokens;
                totals.OutputTokens += u.OutputTokens;
                totals.CacheReadTokens += u.CacheReadInputTokens ?? 0;
                totals.CacheWriteTokens += u.CacheCreationInputTokens ?? 0;
            }
        }

        internal static string DescribeToolCall(string name, JsonElement input)
        {
            static int Count(JsonElement e, string prop) =>
                e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.GetArrayLength()
                    : 0;

            switch (name)
            {
                case ToolCatalog.DrawEntities: return $"drawing {Count(input, "entities")} entities";
                case ToolCatalog.ModifyEntities: return $"modifying ({Count(input, "operations")} operations)";
                case ToolCatalog.EnsureLayers: return $"setting up {Count(input, "layers")} layer(s)";
                case ToolCatalog.ListEntities: return "reading drawing entities";
                case ToolCatalog.GetDrawingInfo: return "reading drawing setup";
                case ToolCatalog.PickPoint: return "waiting for you to pick a point";
                case ToolCatalog.SelectEntities: return "waiting for you to select objects";
                default: return name;
            }
        }

        internal static string DescribeApiError(Exception ex)
        {
            switch (ex)
            {
                case AnthropicUnauthorizedException _:
                    return "The Anthropic API key was rejected. Set a valid key (AIKEY command or ANTHROPIC_API_KEY).";
                case AnthropicForbiddenException _:
                    return "The API key does not have access to this model or feature: " + ex.Message;
                case AnthropicRateLimitException _:
                    return "Rate limited by the Anthropic API. Wait a moment and try again.";
                case Anthropic5xxException _:
                    return "The Anthropic API had a temporary problem. Try again shortly. (" + ex.Message + ")";
                case AnthropicBadRequestException _:
                    return "The Anthropic API rejected the request: " + ex.Message;
                case AnthropicIOException _:
                    return "Could not reach the Anthropic API. Check the network or proxy settings. (" + ex.Message + ")";
                case AnthropicApiException _:
                    return "Anthropic API error: " + ex.Message;
                default:
                    return ex.GetType().Name + ": " + ex.Message;
            }
        }
    }
}
