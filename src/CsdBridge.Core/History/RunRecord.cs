using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using CsdBridge.Core.Agent;

namespace CsdBridge.Core.History
{
    /// <summary>
    /// Everything about one request to Claude: what was asked, each step Claude took,
    /// how it ended and what it cost. Saved as one line of JSON in the history files
    /// and shown in the CAD history window.
    /// </summary>
    public sealed class RunRecord
    {
        public const string StatusRunning = "Running";

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset? FinishedAt { get; set; }

        /// <summary>Drawing file name, e.g. "Level 2 slab.dwg".</summary>
        public string? Drawing { get; set; }

        public string? DrawingPath { get; set; }
        public string Prompt { get; set; } = "";

        /// <summary>Objects selected when the request was made.</summary>
        public int SelectedCount { get; set; }

        /// <summary>Running, Completed, Cancelled, Refused, TurnLimitReached or Failed.</summary>
        public string Status { get; set; } = StatusRunning;

        public string? FinalText { get; set; }
        public string? Error { get; set; }
        public string? Model { get; set; }

        /// <summary>API effort level the request ran at (low ... max).</summary>
        public string? Effort { get; set; }

        /// <summary>How it was chosen, e.g. "Quick (auto: a simple edit)".</summary>
        public string? EffortChoice { get; set; }
        public int ToolCalls { get; set; }
        public int Requests { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }

        /// <summary>Estimated cost in US dollars; null for older records and models with no known price.</summary>
        public decimal? CostUsd { get; set; }

        /// <summary>Requests in this run that couldn't be priced (left out of <see cref="CostUsd"/>).</summary>
        public int? UnpricedRequests { get; set; }

        public List<RunStep> Steps { get; set; } = new List<RunStep>();

        [JsonIgnore]
        public bool IsRunning => Status == StatusRunning;

        public void Record(AgentEvent e)
        {
            if (e.Kind == AgentEventKind.Waiting)
            {
                return;
            }

            Steps.Add(new RunStep
            {
                At = DateTimeOffset.Now,
                Kind = e.Kind.ToString(),
                Tool = e.ToolName,
                Text = e.Text,
                IsError = e.IsError ? true : (bool?)null,
            });
        }

        public void Complete(AgentRunResult result)
        {
            FinishedAt = DateTimeOffset.Now;
            Status = result.Status.ToString();
            FinalText = string.IsNullOrWhiteSpace(result.FinalText) ? null : result.FinalText;
            Error = result.Error;
            ToolCalls = result.ToolCalls;
            Effort = result.Effort ?? Effort;
            Requests = result.Usage.Requests;
            InputTokens = result.Usage.InputTokens;
            OutputTokens = result.Usage.OutputTokens;
            CacheReadTokens = result.Usage.CacheReadTokens;
            CacheWriteTokens = result.Usage.CacheWriteTokens;
            bool priced = result.Usage.UnpricedRequests < result.Usage.Requests;
            CostUsd = priced ? Math.Round(result.Usage.CostUsd, 6) : (decimal?)null;
            UnpricedRequests = result.Usage.UnpricedRequests > 0 ? result.Usage.UnpricedRequests : (int?)null;
        }

        public void Fail(string error)
        {
            FinishedAt = DateTimeOffset.Now;
            Status = AgentRunStatus.Failed.ToString();
            Error = error;
        }

        public string UsageSummary() => new UsageTotals
        {
            Requests = Requests,
            InputTokens = InputTokens,
            OutputTokens = OutputTokens,
            CacheReadTokens = CacheReadTokens,
            CacheWriteTokens = CacheWriteTokens,
            CostUsd = CostUsd ?? 0m,
            UnpricedRequests = CostUsd == null ? Requests : UnpricedRequests ?? 0,
        }.ToString();

        /// <summary>Case-insensitive search across the request, replies, steps and drawing name.</summary>
        public bool Matches(string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            string q = query!.Trim();
            bool Has(string? s) => s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            return Has(Prompt) || Has(FinalText) || Has(Drawing) || Has(Error) || Has(Status)
                || Steps.Any(s => Has(s.Text) || Has(s.Tool));
        }
    }

    public sealed class RunStep
    {
        public DateTimeOffset At { get; set; }

        /// <summary>AssistantText, ToolStarted, ToolFinished or Notice.</summary>
        public string Kind { get; set; } = "";

        public string? Tool { get; set; }
        public string Text { get; set; } = "";
        public bool? IsError { get; set; }
    }
}
