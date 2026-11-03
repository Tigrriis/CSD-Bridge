using System;
using System.Globalization;
using System.Threading.Tasks;

namespace CsdBridge.Core.Agent
{
    /// <summary>
    /// Runs work on the CAD application's main thread. The Claude API calls happen on
    /// a background thread; every drawing operation is marshalled back through this.
    /// </summary>
    public interface IMainThreadInvoker
    {
        Task<T> InvokeAsync<T>(Func<T> work);
    }

    /// <summary>Runs work on the calling thread. For the CLI harness and tests.</summary>
    public sealed class InlineInvoker : IMainThreadInvoker
    {
        public static InlineInvoker Instance { get; } = new InlineInvoker();

        public Task<T> InvokeAsync<T>(Func<T> work)
        {
            try
            {
                return Task.FromResult(work());
            }
            catch (Exception ex)
            {
                var tcs = new TaskCompletionSource<T>();
                tcs.SetException(ex);
                return tcs.Task;
            }
        }
    }

    public sealed class AgentOptions
    {
        /// <summary>Claude model id.</summary>
        public string Model { get; set; } = "claude-opus-5-5";

        /// <summary>low | medium | high | xhigh | max. Higher is slower but more careful.</summary>
        public string Effort { get; set; } = "high";

        public int MaxTokens { get; set; } = 16000;

        /// <summary>Hard stop on model round trips per user request.</summary>
        public int MaxTurns { get; set; } = 30;

        /// <summary>
        /// Retry policy-declined requests on Anthropic's recommended fallback model,
        /// server-side. Claude API only (not Bedrock / Vertex / Foundry).
        /// </summary>
        public bool ServerSideFallback { get; set; } = true;

        /// <summary>Name of the CAD application, used in the system prompt.</summary>
        public string HostName { get; set; } = "BricsCAD";

        /// <summary>Office drafting standards appended to the system prompt (Markdown).</summary>
        public string? OfficeStandards { get; set; }
    }

    public enum AgentEventKind
    {
        /// <summary>A request to Claude was sent (Text holds the turn number).</summary>
        Waiting,

        /// <summary>Claude wrote text for the user.</summary>
        AssistantText,

        /// <summary>A tool is about to run.</summary>
        ToolStarted,

        /// <summary>A tool finished (IsError when it failed).</summary>
        ToolFinished,

        /// <summary>Informational message from the agent loop itself.</summary>
        Notice,
    }

    public sealed class AgentEvent
    {
        public AgentEvent(AgentEventKind kind, string text, string? toolName = null, bool isError = false)
        {
            Kind = kind;
            Text = text;
            ToolName = toolName;
            IsError = isError;
        }

        public AgentEventKind Kind { get; }
        public string Text { get; }
        public string? ToolName { get; }
        public bool IsError { get; }

        public override string ToString() => ToolName == null ? $"[{Kind}] {Text}" : $"[{Kind}] {ToolName}: {Text}";
    }

    public enum AgentRunStatus
    {
        Completed,
        Cancelled,
        Refused,
        TurnLimitReached,
        Failed,
    }

    public sealed class UsageTotals
    {
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public int Requests { get; set; }

        public void Add(UsageTotals other)
        {
            InputTokens += other.InputTokens;
            OutputTokens += other.OutputTokens;
            CacheReadTokens += other.CacheReadTokens;
            CacheWriteTokens += other.CacheWriteTokens;
            Requests += other.Requests;
        }

        public override string ToString() =>
            $"{Requests} request(s), {K(InputTokens + CacheReadTokens + CacheWriteTokens)} in " +
            $"({K(CacheReadTokens)} cached), {K(OutputTokens)} out";

        private static string K(long n) =>
            n >= 1000 ? (n / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k" : n.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class AgentRunResult
    {
        public AgentRunStatus Status { get; set; }

        /// <summary>Claude's last text reply (what to show the user).</summary>
        public string FinalText { get; set; } = "";

        public int ToolCalls { get; set; }
        public UsageTotals Usage { get; set; } = new UsageTotals();
        public string? Error { get; set; }
    }
}
