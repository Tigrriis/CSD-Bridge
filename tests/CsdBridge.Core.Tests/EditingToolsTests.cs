using System;
using System.Linq;
using System.Text.Json;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Offline;
using CsdBridge.Core.Tools;
using Xunit;

namespace CsdBridge.Core.Tests
{
    /// <summary>The hatch and curve-editing tools, end to end on the offline host.</summary>
    public class EditingToolsTests
    {
        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

        private static (InMemoryDrawingHost Host, ToolDispatcher Dispatcher) New() { var h = new InMemoryDrawingHost("Meters"); return (h, new ToolDispatcher(h)); }

        private static string Draw(ToolDispatcher d, string entity)
        {
            ToolOutcome o = d.Execute(ToolCatalog.DrawEntities, Json(@"{""entities"":[" + entity + "]}"));
            Assert.False(o.IsError, o.Content);
            return JsonDocument.Parse(o.Content).RootElement.GetProperty("results")[0].GetProperty("handle").GetString()!;
        }

        private static JsonElement Run(ToolDispatcher d, string tool, string json)
        {
            ToolOutcome o = d.Execute(tool, Json(json));
            Assert.False(o.IsError, o.Content);
            return JsonDocument.Parse(o.Content).RootElement.Clone();
        }

        [Fact]
        public void HatchFromPointsAndFromAClosedOutline()
        {
            var (host, d) = New();
            string outline = Draw(d, @"{""kind"":""polyline"",""points"":[[0,0],[10,0],[10,4],[0,4]],""closed"":true}");
            JsonElement r = Run(d, ToolCatalog.Hatch, @"{""hatches"":[
                {""points"":[[20,0],[30,0],[30,2]],""pattern"":""ANSI31"",""layer"":""C-HATCH""},
                {""boundary_handles"":[""" + outline + @"""],""pattern"":""SOLID""}]}");

            Assert.Equal(2, r.GetProperty("succeeded").GetInt32());
            HatchSpec h = host.Entities.Select(e => e.Spec).OfType<HatchSpec>().Last();
            Assert.Equal("SOLID", h.Pattern);
            EntityListResult list = host.ListEntities(new EntityQuery { Kinds = new() { EntityKind.Hatch }, Limit = 10 });
            Assert.Equal(40, list.Entities.Last().Area);
        }

        [Fact]
        public void HatchBySeedPointNeedsBricsCad()
        {
            var (_, d) = New();
            ToolOutcome o = d.Execute(ToolCatalog.Hatch, Json(@"{""hatches"":[{""seed_points"":[[1,1]]}]}"));
            Assert.True(o.IsError);
            Assert.Contains("needs BricsCAD", o.Content);
        }

        [Fact]
        public void TrimAndExtendReplaceTheCurve()
        {
            var (host, d) = New();
            string a = Draw(d, @"{""kind"":""line"",""start"":[0,0],""end"":[10,0],""layer"":""KERB""}");
            string b = Draw(d, @"{""kind"":""line"",""start"":[0,5],""end"":[3,5]}");
            string wall = Draw(d, @"{""kind"":""line"",""start"":[6,-10],""end"":[6,10]}");

            JsonElement r = Run(d, ToolCatalog.TrimExtend, @"{""operations"":[
                {""handle"":""" + a + @""",""mode"":""trim"",""boundaries"":[""" + wall + @"""],""keep_point"":[1,0]},
                {""handle"":""" + b + @""",""mode"":""extend"",""boundaries"":[""" + wall + @"""],""end"":""end""}]}");

            Assert.Equal(2, r.GetProperty("succeeded").GetInt32());
            var lines = host.Entities.Select(e => e.Spec).OfType<LineSpec>().ToList();
            Assert.Equal(3, lines.Count); // originals replaced, not duplicated
            Assert.Contains(lines, l => Math.Abs(l.End.X - 6) < 1e-9 && l.Start.Y == 0 && l.Layer == "KERB");
            Assert.Contains(lines, l => Math.Abs(l.End.X - 6) < 1e-9 && l.Start.Y == 5);
        }

        [Fact]
        public void FilletAddsTheArcAndCanJoin()
        {
            var (host, d) = New();
            string a = Draw(d, @"{""kind"":""line"",""start"":[0,0],""end"":[15,0],""layer"":""KERB""}");
            string b = Draw(d, @"{""kind"":""line"",""start"":[12,-3],""end"":[12,10],""layer"":""KERB""}");

            JsonElement r = Run(d, ToolCatalog.Fillet, @"{""fillets"":[{""a"":""" + a + @""",""b"":""" + b + @""",""radius"":2,""pick_a"":[5,0],""pick_b"":[12,8],""join"":true}]}");

            Assert.Equal(1, r.GetProperty("succeeded").GetInt32());
            var pl = Assert.Single(host.Entities.Select(e => e.Spec).OfType<PolylineSpec>());
            Assert.Empty(host.Entities.Select(e => e.Spec).OfType<LineSpec>());
            Assert.Equal(4, pl.Points.Count);
            Assert.Equal(Math.Tan(Math.PI / 8), pl.Bulges![1], 9); // the 90° kerb return
            Assert.Equal("KERB", host.Entities.Single().Layer);
        }

        [Fact]
        public void JoinThenHatchTheOutline()
        {
            var (host, d) = New();
            string[] h =
            {
                Draw(d, @"{""kind"":""line"",""start"":[0,0],""end"":[10,0]}"),
                Draw(d, @"{""kind"":""arc"",""center"":[10,2],""radius"":2,""start_angle"":-90,""end_angle"":90}"),
                Draw(d, @"{""kind"":""line"",""start"":[0,4],""end"":[10,4]}"),
                Draw(d, @"{""kind"":""line"",""start"":[0,4],""end"":[0,0]}"),
            };

            JsonElement r = Run(d, ToolCatalog.JoinCurves, @"{""joins"":[{""handles"":[" + string.Join(",", h.Select(x => "\"" + x + "\"")) + "]}]}");
            string joined = r.GetProperty("results")[0].GetProperty("handle").GetString()!;
            var pl = Assert.Single(host.Entities.Select(e => e.Spec).OfType<PolylineSpec>());
            Assert.True(pl.Closed);

            Run(d, ToolCatalog.Hatch, @"{""hatches"":[{""boundary_handles"":[""" + joined + @"""]}]}");
            EntityListResult list = host.ListEntities(new EntityQuery { Kinds = new() { EntityKind.Hatch }, Limit = 10 });
            Assert.Equal(40 + Math.PI * 2, list.Entities.Single().Area!.Value, 3);
        }

        [Fact]
        public void RoadLinesFromChainageAndBack()
        {
            var (host, d) = New();

            // Alignment: 100 m east, then a 50 m long curve of radius 100 to the left (bulge = tan(sweep/4)).
            string bulge = Math.Tan(0.5 / 4).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            double ex = 100 + 100 * Math.Sin(0.5), ey = 100 - 100 * Math.Cos(0.5);
            string align = Draw(d, @"{""kind"":""polyline"",""points"":[[0,0],[100,0],[" +
                ex.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + ey.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                @"]],""bulges"":[0," + bulge + @",0]}");

            JsonElement r = Run(d, ToolCatalog.OffsetByChainage, @"{""alignment"":""" + align + @""",""start_chainage"":1000,""lines"":[{""layer"":""KERB"",""stretches"":[
                {""from"":1000,""to"":1040,""offset"":3.5},
                {""from"":1040,""to"":1060,""offset_start"":3.5,""offset_end"":5.5},
                {""from"":1060,""to"":1150,""offset"":5.5}]}]}");
            string kerb = r.GetProperty("results")[0].GetProperty("handle").GetString()!;
            PolylineSpec pl = host.Entities.Select(e => e.Spec).OfType<PolylineSpec>().Last();
            Assert.NotNull(pl.Bulges);

            // Reading it back gives the same stretches.
            JsonElement a = Run(d, ToolCatalog.AnalyseOffsets, @"{""alignment"":""" + align + @""",""start_chainage"":1000,""lines"":[""" + kerb + @"""]}");
            JsonElement s = a.GetProperty("lines")[0].GetProperty("stretches");
            Assert.Equal(3, s.GetArrayLength());
            Assert.Equal(3.5, s[0].GetProperty("offset").GetDouble(), 3);
            Assert.Equal(1040, s[1].GetProperty("from").GetDouble(), 3);
            Assert.Equal(5.5, s[2].GetProperty("offset").GetDouble(), 3);
            Assert.Equal(1150, s[2].GetProperty("to").GetDouble(), 3);
        }
    }
}
