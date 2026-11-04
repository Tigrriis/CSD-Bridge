using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using CsdBridge.Core.Agent;
using CsdBridge.Core.Offline;
using CsdBridge.Core.Tools;
using Xunit;

namespace CsdBridge.Core.Tests
{
    /// <summary>
    /// Drives the real agent loop and the real Anthropic SDK against canned HTTP
    /// responses, then checks the JSON the SDK actually sent.
    /// </summary>
    public class DraftingAgentTests
    {
        private const string Rectangle =
            @"{""entities"":[{""kind"":""polyline"",""points"":[[0,0],[1200,0],[1200,600],[0,600]],""closed"":true,""layer"":""S-SLAB""}]}";

        [Fact]
        public async Task Tool_round_trip_echoes_thinking_and_returns_results()
        {
            var http = new FakeAnthropic(
                Response("tool_use",
                    @"{""type"":""thinking"",""thinking"":"""",""signature"":""sig-123""}",
                    @"{""type"":""text"",""text"":""Drawing the slab outline.""}",
                    ToolUse("toolu_1", ToolCatalog.DrawEntities, Rectangle)),
                Response("end_turn", @"{""type"":""text"",""text"":""Done - 1200 x 600 slab on S-SLAB.""}"));

            var host = new InMemoryDrawingHost();
            var agent = NewAgent(http, host);
            var events = new List<AgentEvent>();

            AgentRunResult result = await agent.RunAsync("Draw a 1200x600 slab", new SyncProgress(events));

            Assert.Equal(AgentRunStatus.Completed, result.Status);
            Assert.Equal("Done - 1200 x 600 slab on S-SLAB.", result.FinalText);
            Assert.Equal(1, result.ToolCalls);
            Assert.Single(host.Entities);
            Assert.Equal(2, result.Usage.Requests);
            Assert.Contains(events, e => e.Kind == AgentEventKind.ToolFinished && !e.IsError);

            // First request: settings that matter.
            JsonElement first = http.Bodies[0];
            Assert.Equal("claude-opus-5-5", first.GetProperty("model").GetString());
            Assert.Equal("default", first.GetProperty("fallbacks").GetString());
            Assert.Equal("adaptive", first.GetProperty("thinking").GetProperty("type").GetString());
            Assert.Equal("high", first.GetProperty("output_config").GetProperty("effort").GetString());
            Assert.Equal("ephemeral", first.GetProperty("cache_control").GetProperty("type").GetString());
            Assert.Equal("1h", first.GetProperty("cache_control").GetProperty("ttl").GetString());
            JsonElement clear = first.GetProperty("context_management").GetProperty("edits")[0];
            Assert.Equal("clear_tool_uses_20250919", clear.GetProperty("type").GetString());
            Assert.Equal(60000, clear.GetProperty("trigger").GetProperty("value").GetInt64());
            Assert.Equal(6, clear.GetProperty("keep").GetProperty("value").GetInt64());
            Assert.Contains("context-management-2025-06-27", http.BetaHeaders[0]);
            Assert.Equal(ToolCatalog.All.Count, first.GetProperty("tools").GetArrayLength());
            Assert.Contains("server-side-fallback-2026-07-01", http.BetaHeaders[0]);
            Assert.False(first.TryGetProperty("temperature", out _));

            // Second request: history is the first request plus the echoed assistant turn and tool results.
            JsonElement msgs = http.Bodies[1].GetProperty("messages");
            Assert.Equal(3, msgs.GetArrayLength());
            JsonElement assistant = msgs[1];
            Assert.Equal("assistant", assistant.GetProperty("role").GetString());
            JsonElement thinking = assistant.GetProperty("content")[0];
            Assert.Equal("thinking", thinking.GetProperty("type").GetString());
            Assert.Equal("sig-123", thinking.GetProperty("signature").GetString());
            Assert.Equal("tool_use", assistant.GetProperty("content")[2].GetProperty("type").GetString());

            JsonElement toolResult = msgs[2].GetProperty("content")[0];
            Assert.Equal("tool_result", toolResult.GetProperty("type").GetString());
            Assert.Equal("toolu_1", toolResult.GetProperty("tool_use_id").GetString());
            Assert.Contains("\"succeeded\":1", ContentText(toolResult));

            // System prompt and tools are byte-identical across requests (cache prefix).
            Assert.Equal(first.GetProperty("system").GetRawText(), http.Bodies[1].GetProperty("system").GetRawText());
            Assert.Equal(first.GetProperty("tools").GetRawText(), http.Bodies[1].GetProperty("tools").GetRawText());
        }

        [Fact]
        public async Task Follow_up_request_continues_the_same_conversation()
        {
            var http = new FakeAnthropic(
                Response("end_turn", @"{""type"":""text"",""text"":""Which size?""}"),
                Response("end_turn", @"{""type"":""text"",""text"":""OK.""}"));
            var agent = NewAgent(http, new InMemoryDrawingHost());

            await agent.RunAsync("Draw a slab");
            await agent.RunAsync("1200 x 600");

            JsonElement msgs = http.Bodies[1].GetProperty("messages");
            Assert.Equal(3, msgs.GetArrayLength());
            Assert.Equal("user", msgs[2].GetProperty("role").GetString());
            Assert.Equal(4, agent.MessageCount);
        }

        [Fact]
        public async Task Invalid_tool_input_goes_back_as_an_error_result()
        {
            var http = new FakeAnthropic(
                Response("tool_use", ToolUse("toolu_1", ToolCatalog.DrawEntities, @"{""entities"":[{""kind"":""circle"",""center"":[0,0]}]}")),
                Response("end_turn", @"{""type"":""text"",""text"":""Fixed.""}"));
            var host = new InMemoryDrawingHost();
            var agent = NewAgent(http, host);

            await agent.RunAsync("circle please");

            JsonElement toolResult = http.Bodies[1].GetProperty("messages")[2].GetProperty("content")[0];
            Assert.True(toolResult.GetProperty("is_error").GetBoolean());
            Assert.Contains("entities[0].radius is required", ContentText(toolResult));
            Assert.Empty(host.Entities);
        }

        [Fact]
        public async Task Cut_off_reply_does_not_run_tools()
        {
            var http = new FakeAnthropic(
                Response("max_tokens", ToolUse("toolu_1", ToolCatalog.DrawEntities, Rectangle)),
                Response("end_turn", @"{""type"":""text"",""text"":""Resent.""}"));
            var host = new InMemoryDrawingHost();
            var agent = NewAgent(http, host);

            AgentRunResult result = await agent.RunAsync("big job");

            Assert.Empty(host.Entities);
            Assert.Equal(0, result.ToolCalls);
            JsonElement toolResult = http.Bodies[1].GetProperty("messages")[2].GetProperty("content")[0];
            Assert.True(toolResult.GetProperty("is_error").GetBoolean());
            Assert.Contains("output token limit", ContentText(toolResult));
        }

        [Fact]
        public async Task Refusal_is_reported_and_dropped_from_history()
        {
            var http = new FakeAnthropic(Response("refusal"));
            var agent = NewAgent(http, new InMemoryDrawingHost());

            AgentRunResult result = await agent.RunAsync("something declined");

            Assert.Equal(AgentRunStatus.Refused, result.Status);
            Assert.Equal(0, agent.MessageCount);
        }

        [Fact]
        public async Task Fallback_switch_drops_pre_boundary_tool_use()
        {
            var http = new FakeAnthropic(
                Response("end_turn",
                    @"{""type"":""thinking"",""thinking"":"""",""signature"":""sig-declined""}",
                    @"{""type"":""fallback"",""from"":{""model"":""claude-opus-5-5""},""to"":{""model"":""claude-opus-5""}}",
                    @"{""type"":""text"",""text"":""Answer from fallback.""}"),
                Response("end_turn", @"{""type"":""text"",""text"":""ok""}"));
            var agent = NewAgent(http, new InMemoryDrawingHost());

            AgentRunResult result = await agent.RunAsync("q1");
            await agent.RunAsync("q2");

            Assert.Equal("Answer from fallback.", result.FinalText);
            JsonElement assistant = http.Bodies[1].GetProperty("messages")[1];
            var types = assistant.GetProperty("content").EnumerateArray().Select(b => b.GetProperty("type").GetString()).ToList();
            Assert.Equal(new[] { "text" }, types);
        }

        [Fact]
        public async Task Cancellation_before_tools_still_leaves_valid_history()
        {
            using var cts = new CancellationTokenSource();
            var http = new FakeAnthropic(
                Response("tool_use",
                    ToolUse("toolu_1", ToolCatalog.GetDrawingInfo, "{}"),
                    ToolUse("toolu_2", ToolCatalog.DrawEntities, Rectangle)));
            var host = new InMemoryDrawingHost();
            var agent = NewAgent(http, host, new CancelAfterFirstTool(cts));

            AgentRunResult result = await agent.RunAsync("go", null, cts.Token);

            Assert.Equal(AgentRunStatus.Cancelled, result.Status);
            Assert.Empty(host.Entities); // second tool not run
            Assert.Equal(3, agent.MessageCount); // user, assistant(tool_use x2), user(tool_result x2)
        }

        [Fact]
        public async Task Per_request_effort_is_an_effort_only_system_message_and_the_prefix_never_changes()
        {
            var http = new FakeAnthropic(
                Response("end_turn", @"{""type"":""text"",""text"":""a""}"),
                Response("end_turn", @"{""type"":""text"",""text"":""b""}"),
                Response("end_turn", @"{""type"":""text"",""text"":""c""}"));
            var agent = NewAgent(http, new InMemoryDrawingHost());

            AgentRunResult first = await agent.RunAsync("move it left 100", null, default, "low");
            await agent.RunAsync("draw a slab");   // default (high): switch back
            await agent.RunAsync("draw another");  // still high: no new message

            Assert.Equal("low", first.Effort);
            Assert.All(http.Bodies, b => Assert.Equal("high", b.GetProperty("output_config").GetProperty("effort").GetString()));
            Assert.All(http.BetaHeaders, h => Assert.Contains("mid-conversation-output-config-2026-07-01", h));

            JsonElement m0 = http.Bodies[0].GetProperty("messages");
            Assert.Equal(2, m0.GetArrayLength());
            Assert.Equal("system", m0[1].GetProperty("role").GetString());
            Assert.Equal(0, m0[1].GetProperty("content").GetArrayLength());
            Assert.Equal("low", m0[1].GetProperty("output_config").GetProperty("effort").GetString());

            JsonElement m1 = http.Bodies[1].GetProperty("messages");
            Assert.Equal(5, m1.GetArrayLength()); // user, system(low), assistant, user, system(high)
            Assert.Equal("high", m1[4].GetProperty("output_config").GetProperty("effort").GetString());

            JsonElement m2 = http.Bodies[2].GetProperty("messages");
            Assert.Equal(7, m2.GetArrayLength());
            Assert.Equal("user", m2[6].GetProperty("role").GetString());

            // Append-only: every earlier message is byte-identical in later requests.
            for (int i = 0; i < m1.GetArrayLength(); i++)
            {
                Assert.Equal(m1[i].GetRawText(), m2[i].GetRawText());
            }

            Assert.Equal("high", agent.CurrentEffort);
        }

        [Fact]
        public async Task Older_models_set_the_top_level_effort_per_request_instead()
        {
            var http = new FakeAnthropic(Response("end_turn", @"{""type"":""text"",""text"":""a""}"));
            var agent = NewAgent(http, new InMemoryDrawingHost(), options: new AgentOptions { Model = "claude-opus-4-8" });

            await agent.RunAsync("move it", null, default, "low");

            Assert.False(agent.UsesPerMessageEffort);
            Assert.Equal("low", http.Bodies[0].GetProperty("output_config").GetProperty("effort").GetString());
            Assert.Equal(1, http.Bodies[0].GetProperty("messages").GetArrayLength());
            Assert.DoesNotContain("mid-conversation", http.BetaHeaders[0]);
        }

        [Fact]
        public async Task Refused_request_rolls_back_its_effort_change_too()
        {
            var http = new FakeAnthropic(Response("refusal"));
            var agent = NewAgent(http, new InMemoryDrawingHost());

            await agent.RunAsync("something", null, default, "xhigh");

            Assert.Equal(0, agent.MessageCount);
            Assert.Equal("high", agent.CurrentEffort);
        }

        // ------------------------------------------------------------------ helpers

        private static DraftingAgent NewAgent(
            FakeAnthropic http,
            InMemoryDrawingHost host,
            IMainThreadInvoker? invoker = null,
            AgentOptions? options = null,
            IReadOnlyList<IToolExtension>? extensions = null)
        {
            var client = new AnthropicClient
            {
                ApiKey = "test-key",
                HttpClient = new HttpClient(http),
                MaxRetries = 0,
            };
            return new DraftingAgent(client, new ToolDispatcher(host), options ?? new AgentOptions(), invoker, extensions);
        }

        [Fact]
        public async Task Extension_tools_are_offered_and_dispatched()
        {
            const string design = @"{""ground"":[[0,10],[100,14],[200,12]],""max_grade_pct"":8}";
            var http = new FakeAnthropic(
                Response("tool_use", ToolUse("toolu_1", CsdBridge.Core.Civil.ProfileTools.Design, design)),
                Response("end_turn", @"{""type"":""text"",""text"":""Designed.""}"));

            var invoker = new CountingInvoker();
            var agent = NewAgent(http, new InMemoryDrawingHost(), invoker, extensions: new IToolExtension[] { new CsdBridge.Core.Civil.ProfileTools() });
            AgentRunResult result = await agent.RunAsync("Design the vertical");

            Assert.Equal(AgentRunStatus.Completed, result.Status);
            JsonElement first = http.Bodies[0];
            Assert.Equal(ToolCatalog.All.Count + 2, first.GetProperty("tools").GetArrayLength());
            Assert.Contains("## Vertical design", first.GetProperty("system").GetRawText());

            JsonElement toolResult = http.Bodies[1].GetProperty("messages")[2].GetProperty("content")[0];
            Assert.False(toolResult.TryGetProperty("is_error", out JsonElement err) && err.GetBoolean(), ContentText(toolResult));
            Assert.Contains("\"ips\"", ContentText(toolResult));

            // Pure tools don't go through the CAD main thread.
            Assert.Equal(0, invoker.Calls);
        }

        private sealed class CountingInvoker : IMainThreadInvoker
        {
            public int Calls;

            public Task<T> InvokeAsync<T>(Func<T> work)
            {
                Interlocked.Increment(ref Calls);
                return Task.FromResult(work());
            }
        }

        private static string ToolUse(string id, string name, string inputJson) =>
            $@"{{""type"":""tool_use"",""id"":""{id}"",""name"":""{name}"",""input"":{inputJson},""caller"":{{""type"":""direct""}}}}";

        private static string Response(string stopReason, params string[] blocks) =>
            $@"{{""id"":""msg_{Guid.NewGuid():N}"",""type"":""message"",""role"":""assistant"",""model"":""claude-opus-5-5"",
                ""content"":[{string.Join(",", blocks)}],""stop_reason"":""{stopReason}"",""stop_sequence"":null,
                ""stop_details"":{(stopReason == "refusal" ? @"{""type"":""refusal"",""category"":null,""explanation"":null}" : "null")},
                ""container"":null,""context_management"":null,
                ""usage"":{{""input_tokens"":120,""output_tokens"":40,""cache_read_input_tokens"":0,""cache_creation_input_tokens"":0}}}}";

        private static string ContentText(JsonElement toolResult)
        {
            JsonElement c = toolResult.GetProperty("content");
            return c.ValueKind == JsonValueKind.String ? c.GetString()! : c.GetRawText();
        }

        private sealed class SyncProgress : IProgress<AgentEvent>
        {
            private readonly List<AgentEvent> _events;
            public SyncProgress(List<AgentEvent> events) => _events = events;
            public void Report(AgentEvent value) => _events.Add(value);
        }

        private sealed class CancelAfterFirstTool : IMainThreadInvoker
        {
            private readonly CancellationTokenSource _cts;
            public CancelAfterFirstTool(CancellationTokenSource cts) => _cts = cts;

            public Task<T> InvokeAsync<T>(Func<T> work)
            {
                T result = work();
                _cts.Cancel();
                return Task.FromResult(result);
            }
        }

        private sealed class FakeAnthropic : HttpMessageHandler
        {
            private readonly Queue<string> _responses;

            public FakeAnthropic(params string[] responses) => _responses = new Queue<string>(responses);

            public List<JsonElement> Bodies { get; } = new List<JsonElement>();
            public List<string> BetaHeaders { get; } = new List<string>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string body = request.Content == null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
                Bodies.Add(JsonDocument.Parse(body).RootElement.Clone());
                BetaHeaders.Add(request.Headers.TryGetValues("anthropic-beta", out var v) ? string.Join(",", v) : "");

                if (_responses.Count == 0)
                {
                    throw new InvalidOperationException("No more canned responses.");
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json"),
                };
            }
        }
    }
}
