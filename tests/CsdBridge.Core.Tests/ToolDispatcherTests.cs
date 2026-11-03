using System.Linq;
using System.Text.Json;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Offline;
using CsdBridge.Core.Tools;
using Xunit;

namespace CsdBridge.Core.Tests
{
    public class ToolDispatcherTests
    {
        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

        [Fact]
        public void DrawEntities_creates_every_kind_and_returns_handles()
        {
            var host = new InMemoryDrawingHost();
            host.Blocks.Add("NORTH");
            var dispatcher = new ToolDispatcher(host);

            ToolOutcome outcome = dispatcher.Execute(ToolCatalog.DrawEntities, Json(@"{""entities"":[
                {""kind"":""line"",""start"":[0,0],""end"":[1000,0],""layer"":""S-BEAM""},
                {""kind"":""polyline"",""points"":[[0,0],[100,0],[100,50],[0,50]],""closed"":true},
                {""kind"":""circle"",""center"":[50,25],""radius"":10,""color"":1},
                {""kind"":""arc"",""center"":[0,0],""radius"":5,""start_angle"":0,""end_angle"":90},
                {""kind"":""text"",""position"":[0,-10],""text"":""BEAM B1"",""align"":""center""},
                {""kind"":""mtext"",""position"":[0,-20],""text"":""NOTES:\n1. ALL DIMENSIONS IN MM""},
                {""kind"":""linear_dimension"",""p1"":[0,0],""p2"":[1000,0],""dim_line_point"":[0,-300]},
                {""kind"":""aligned_dimension"",""p1"":[0,0],""p2"":[300,400],""dim_line_point"":[-50,50]},
                {""kind"":""block_insert"",""block_name"":""NORTH"",""position"":[2000,2000]}
            ]}"));

            Assert.False(outcome.IsError, outcome.Content);
            Assert.Equal(9, host.Entities.Count);
            using var doc = JsonDocument.Parse(outcome.Content);
            Assert.Equal(9, doc.RootElement.GetProperty("succeeded").GetInt32());
            Assert.All(doc.RootElement.GetProperty("results").EnumerateArray(), r => Assert.False(string.IsNullOrEmpty(r.GetProperty("handle").GetString())));
            Assert.Contains(host.Layers, l => l.Name == "S-BEAM");
            Assert.Equal(500, InMemoryDrawingHost.Measure(host.Entities[7].Spec), 6);
        }

        [Fact]
        public void Invalid_batch_is_rejected_whole_and_reports_every_problem()
        {
            var host = new InMemoryDrawingHost();
            var dispatcher = new ToolDispatcher(host);

            ToolOutcome outcome = dispatcher.Execute(ToolCatalog.DrawEntities, Json(@"{""entities"":[
                {""kind"":""line"",""start"":[0,0],""end"":[10,0]},
                {""kind"":""circle"",""center"":[0,0],""radius"":-5},
                {""kind"":""polyline"",""points"":[[0,0],[1,1]],""bulges"":[0]},
                {""kind"":""text"",""position"":[0,0],""text"":""two\nlines""},
                {""kind"":""spline"",""points"":[[0,0],[1,1]]},
                {""kind"":""line"",""start"":[0,0],""end"":[5,5],""layer"":""BAD:NAME""}
            ]}"));

            Assert.True(outcome.IsError);
            Assert.Empty(host.Entities); // nothing drawn when any item is invalid
            Assert.Contains("5 problems", outcome.Content);
            Assert.Contains("entities[1].radius", outcome.Content);
            Assert.Contains("entities[2].bulges", outcome.Content);
            Assert.Contains("use kind 'mtext'", outcome.Content);
            Assert.Contains("'spline' is not supported", outcome.Content);
            Assert.Contains("entities[5].layer", outcome.Content);
        }

        [Fact]
        public void Missing_block_fails_that_item_only()
        {
            var host = new InMemoryDrawingHost();
            var dispatcher = new ToolDispatcher(host);

            ToolOutcome outcome = dispatcher.Execute(ToolCatalog.DrawEntities, Json(@"{""entities"":[
                {""kind"":""line"",""start"":[0,0],""end"":[10,0]},
                {""kind"":""block_insert"",""block_name"":""TITLE-A1"",""position"":[0,0]}
            ]}"));

            Assert.False(outcome.IsError); // partial success is not a tool error
            Assert.Single(host.Entities);
            Assert.Contains("TITLE-A1", outcome.Content);
            Assert.Contains("\"failed\":1", outcome.Content);
        }

        [Fact]
        public void Copy_with_count_makes_a_linear_array()
        {
            var host = new InMemoryDrawingHost();
            var dispatcher = new ToolDispatcher(host);
            dispatcher.Execute(ToolCatalog.DrawEntities, Json(@"{""entities"":[{""kind"":""circle"",""center"":[0,0],""radius"":10}]}"));
            string handle = host.Entities[0].Handle;

            ToolOutcome outcome = dispatcher.Execute(ToolCatalog.ModifyEntities, Json(
                @"{""operations"":[{""action"":""copy"",""handles"":[""" + handle + @"""],""dx"":100,""dy"":0,""count"":4}]}"));

            Assert.False(outcome.IsError, outcome.Content);
            Assert.Equal(5, host.Entities.Count);
            var last = (CircleSpec)host.Entities.Last().Spec;
            Assert.Equal(400, last.Center.X, 6);
        }

        [Fact]
        public void Rotate_and_erase_by_handle()
        {
            var host = new InMemoryDrawingHost();
            var dispatcher = new ToolDispatcher(host);
            dispatcher.Execute(ToolCatalog.DrawEntities, Json(@"{""entities"":[
                {""kind"":""line"",""start"":[0,0],""end"":[100,0]},
                {""kind"":""line"",""start"":[0,10],""end"":[100,10]}]}"));
            string a = host.Entities[0].Handle, b = host.Entities[1].Handle;

            dispatcher.Execute(ToolCatalog.ModifyEntities, Json(
                @"{""operations"":[{""action"":""rotate"",""handles"":[""" + a + @"""],""base_point"":[0,0],""angle"":90},
                                   {""action"":""erase"",""handles"":[""" + b + @"""]}]}"));

            Assert.Single(host.Entities);
            var line = (LineSpec)host.Entities[0].Spec;
            Assert.Equal(0, line.End.X, 6);
            Assert.Equal(100, line.End.Y, 6);
        }

        [Fact]
        public void Unknown_handle_is_reported()
        {
            var dispatcher = new ToolDispatcher(new InMemoryDrawingHost());
            ToolOutcome outcome = dispatcher.Execute(ToolCatalog.ModifyEntities, Json(
                @"{""operations"":[{""action"":""move"",""handles"":[""FFFF""],""dx"":1}]}"));

            Assert.True(outcome.IsError); // the only item failed
            Assert.Contains("FFFF", outcome.Content);
        }

        [Fact]
        public void ListEntities_filters_by_window_layer_and_kind()
        {
            var host = new InMemoryDrawingHost();
            var dispatcher = new ToolDispatcher(host);
            dispatcher.Execute(ToolCatalog.DrawEntities, Json(@"{""entities"":[
                {""kind"":""circle"",""center"":[0,0],""radius"":5,""layer"":""A""},
                {""kind"":""circle"",""center"":[1000,1000],""radius"":5,""layer"":""A""},
                {""kind"":""line"",""start"":[0,0],""end"":[10,0],""layer"":""B""}]}"));

            ToolOutcome outcome = dispatcher.Execute(ToolCatalog.ListEntities, Json(
                @"{""window_min"":[-10,-10],""window_max"":[50,50],""kinds"":[""circle""],""layer"":""a""}"));

            using var doc = JsonDocument.Parse(outcome.Content);
            Assert.Equal(1, doc.RootElement.GetProperty("total_matched").GetInt32());
            var e = doc.RootElement.GetProperty("entities")[0];
            Assert.Equal("circle", e.GetProperty("kind").GetString());
            Assert.Equal(5, e.GetProperty("radius").GetDouble());
        }

        [Fact]
        public void Every_tool_schema_is_valid_json_with_object_type()
        {
            foreach (ToolDefinition tool in ToolCatalog.All)
            {
                using var doc = JsonDocument.Parse(tool.InputSchemaJson);
                Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
                Assert.True(doc.RootElement.TryGetProperty("properties", out _), tool.Name);
                Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            }

            Assert.Equal(ToolCatalog.All.Count, ToolCatalog.All.Select(t => t.Name).Distinct().Count());
        }

        [Fact]
        public void Dxf_preview_contains_the_drawn_entities()
        {
            var host = new InMemoryDrawingHost();
            var dispatcher = new ToolDispatcher(host);
            dispatcher.Execute(ToolCatalog.DrawEntities, Json(@"{""entities"":[
                {""kind"":""polyline"",""points"":[[0,0],[100,0],[100,50]],""closed"":true,""layer"":""S-SLAB""},
                {""kind"":""mtext"",""position"":[0,-20],""text"":""A\nB""},
                {""kind"":""linear_dimension"",""p1"":[0,0],""p2"":[100,0],""dim_line_point"":[0,-30]}]}"));

            var sw = new System.IO.StringWriter();
            DxfPreviewWriter.Write(host, sw);
            string dxf = sw.ToString();

            Assert.Contains("POLYLINE", dxf);
            Assert.Contains("S-SLAB", dxf);
            Assert.Equal(2 + 1, CountOf(dxf, "\r\nTEXT\r\n")); // 2 mtext lines + 1 dimension text
            Assert.EndsWith("EOF\r\n", dxf);
        }

        private static int CountOf(string haystack, string needle)
        {
            int count = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                i += needle.Length;
            }

            return count;
        }
    }
}
