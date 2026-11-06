using System;
using System.IO;
using System.Linq;
using CsdBridge.Core.Agent;
using CsdBridge.Core.History;
using Xunit;

namespace CsdBridge.Core.Tests
{
    public class HistoryStoreTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "csdbridge-history-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, true);
            }
        }

        private static RunRecord Finished(string prompt, DateTimeOffset at)
        {
            var r = new RunRecord { Prompt = prompt, StartedAt = at, Drawing = "Slab.dwg", Model = "claude-opus-5-5" };
            r.Record(new AgentEvent(AgentEventKind.Waiting, "1"));
            r.Record(new AgentEvent(AgentEventKind.ToolStarted, "drawing 4 entities", "draw_entities"));
            r.Record(new AgentEvent(AgentEventKind.ToolFinished, "Block 'X' is not defined", "draw_entities", isError: true));
            r.Record(new AgentEvent(AgentEventKind.AssistantText, "Done.\nTwo lines."));
            r.Complete(new AgentRunResult
            {
                Status = AgentRunStatus.Completed,
                FinalText = "Done.\nTwo lines.",
                ToolCalls = 1,
                Usage = new UsageTotals { Requests = 2, InputTokens = 1000, OutputTokens = 300, CacheReadTokens = 4000, CostUsd = 0.0108m },
            });
            return r;
        }

        [Fact]
        public void Round_trips_records_newest_first_across_days()
        {
            var store = new HistoryStore(_folder);
            var day1 = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(10));
            store.Append(Finished("first", day1));
            store.Append(Finished("second", day1.AddHours(2)));
            store.Append(Finished("third", day1.AddDays(1)));

            var loaded = store.Load();

            Assert.Equal(new[] { "third", "second", "first" }, loaded.Select(r => r.Prompt));
            Assert.Equal(2, Directory.GetFiles(_folder, "*.jsonl").Length);

            RunRecord r = loaded[0];
            Assert.Equal("Completed", r.Status);
            Assert.Equal("Done.\nTwo lines.", r.FinalText); // newlines survive
            Assert.Equal(3, r.Steps.Count);                  // Waiting is not recorded
            Assert.True(r.Steps[1].IsError);
            Assert.Equal(4000, r.CacheReadTokens);
            Assert.False(r.IsRunning);
            Assert.Contains("2 request(s)", r.UsageSummary());
            Assert.Equal(0.0108m, r.CostUsd);
            Assert.Null(r.UnpricedRequests);
            Assert.Contains("about US$0.011", r.UsageSummary());
        }

        [Fact]
        public void Skips_damaged_lines_and_respects_max()
        {
            var store = new HistoryStore(_folder);
            var at = DateTimeOffset.Now;
            for (int i = 0; i < 5; i++)
            {
                store.Append(Finished("p" + i, at.AddMinutes(i)));
            }

            string file = Directory.GetFiles(_folder, "*.jsonl").Single();
            File.AppendAllText(file, "{ this is not json\n\n");

            Assert.Equal(5, store.Load().Count);
            Assert.Equal(new[] { "p4", "p3" }, store.Load(2).Select(r => r.Prompt));
        }

        [Fact]
        public void Missing_folder_loads_empty()
        {
            Assert.Empty(new HistoryStore(Path.Combine(_folder, "nope")).Load());
        }

        [Fact]
        public void Search_matches_prompt_replies_steps_and_drawing()
        {
            RunRecord r = Finished("dimension the beams", DateTimeOffset.Now);
            Assert.True(r.Matches("BEAMS"));
            Assert.True(r.Matches("two lines"));
            Assert.True(r.Matches("not defined"));
            Assert.True(r.Matches("slab.dwg"));
            Assert.True(r.Matches(""));
            Assert.False(r.Matches("column"));
        }

        [Fact]
        public void Failed_run_keeps_the_error()
        {
            var r = new RunRecord { Prompt = "x" };
            Assert.True(r.IsRunning);
            r.Fail("Rate limited");
            Assert.Equal("Failed", r.Status);
            Assert.Equal("Rate limited", r.Error);
            Assert.NotNull(r.FinishedAt);
        }
    }
}
