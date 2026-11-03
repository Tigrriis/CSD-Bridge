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
        public int ToolCalls { get; set; }
        public int Requests { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
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
            Requests = result.Usage.Requests;
            InputTokens = result.Usage.InputTokens;
            OutputTokens = result.Usage.OutputTokens;
            CacheReadTokens = result.Usage.CacheReadTokens;
            CacheWriteTokens = result.Usage.CacheWriteTokens;
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
