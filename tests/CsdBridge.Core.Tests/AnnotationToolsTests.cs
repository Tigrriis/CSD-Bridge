using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;
using CsdBridge.Core.Offline;
using CsdBridge.Core.Rendering;
using CsdBridge.Core.Tools;
using Xunit;

namespace CsdBridge.Core.Tests
{
    /// <summary>
    /// Dimensions of every type, styles, multileaders, tables, editing annotation, alignment
    /// labels, set-out tables and the view used to check a layout, on the offline host.
    /// </summary>
    public class AnnotationToolsTests
    {
        // A straight of 100 along +X, then a quarter circle of radius 50 turning left to (150, 50).
        private const string Alignment = @"{""kind"":""polyline"",""points"":[[0,0],[100,0],[150,50]],""bulges"":[0,0.41421356237,0]}";

        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

        private static (InMemoryDrawingHost Host, ToolDispatcher Dispatcher) New()
        {
            var h = new InMemoryDrawingHost("Meters") { DefaultTextHeight = 2 };
            return (h, new ToolDispatcher(h));
        }

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

        private static JsonElement Listed(ToolDispatcher d, string handle) =>
            Run(d, ToolCatalog.ListEntities, @"{""handles"":[""" + handle + @"""]}").GetProperty("entities")[0];

        private static void Near(double expected, double actual, double tolerance = 1e-3) =>
            Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected}, got {actual}");

        // ------------------------------------------------------------------ dimensions

        [Fact]
        public void EveryDimensionTypeDrawsAndMeasures()
        {
            var (_, d) = New();
            string radius = Draw(d, @"{""kind"":""radius_dimension"",""center"":[0,0],""point"":[3,4]}");
            string diameter = Draw(d, @"{""kind"":""diameter_dimension"",""center"":[0,0],""radius"":5,""angle"":90}");
            string angle = Draw(d, @"{""kind"":""angular_dimension"",""center"":[0,0],""p1"":[10,0],""p2"":[0,10],""arc_point"":[5,5]}");
            string reflex = Draw(d, @"{""kind"":""angular_dimension"",""center"":[0,0],""p1"":[10,0],""p2"":[0,10],""arc_point"":[-5,-5]}");
            string arc = Draw(d, @"{""kind"":""arc_length_dimension"",""center"":[0,0],""p1"":[10,0],""p2"":[0,10],""arc_point"":[9,9]}");
            string easting = Draw(d, @"{""kind"":""ordinate_dimension"",""point"":[512.5,80],""leader_end"":[512.5,95],""axis"":""x"",""origin"":[500,0]}");

            Near(5, Listed(d, radius).GetProperty("measurement").GetDouble());
            Assert.Equal("radius", Listed(d, radius).GetProperty("dim_type").GetString());
            Near(10, Listed(d, diameter).GetProperty("measurement").GetDouble());
            Near(90, Listed(d, angle).GetProperty("measurement").GetDouble());
            Near(270, Listed(d, reflex).GetProperty("measurement").GetDouble());
            Near(10 * Math.PI / 2, Listed(d, arc).GetProperty("measurement").GetDouble());
            Assert.Equal("arc_length", Listed(d, arc).GetProperty("dim_type").GetString());
            Near(12.5, Listed(d, easting).GetProperty("measurement").GetDouble());

            // All of them answer to the "dimension" filter.
            JsonElement all = Run(d, ToolCatalog.ListEntities, @"{""kinds"":[""dimension""]}");
            Assert.Equal(6, all.GetProperty("total_matched").GetInt32());
        }

        [Theory]
        [InlineData(@"{""kind"":""radius_dimension"",""center"":[0,0]}", "radius and angle")]
        [InlineData(@"{""kind"":""arc_length_dimension"",""center"":[0,0],""p1"":[10,0],""p2"":[0,7],""arc_point"":[9,9]}", "ends of one arc")]
        [InlineData(@"{""kind"":""ordinate_dimension"",""point"":[1,1],""leader_end"":[1,5],""axis"":""z""}", "axis must be x or y")]
        [InlineData(@"{""kind"":""mleader"",""position"":[5,5],""text"":""NOTE""}", "arrow_point")]
        [InlineData(@"{""kind"":""table"",""position"":[0,0],""rows"":[[""A"",""B""]],""column_widths"":[10]}", "column_widths has 1 values")]
        [InlineData(@"{""kind"":""line"",""start"":[0,0],""end"":[1,1],""style"":""Standard""}", "style doesn't apply")]
        public void BadAnnotationInputIsRejectedBeforeAnythingIsDrawn(string entity, string expected)
        {
            var (host, d) = New();
            ToolOutcome o = d.Execute(ToolCatalog.DrawEntities, Json(@"{""entities"":[" + entity + "]}"));
            Assert.True(o.IsError);
            Assert.Contains(expected, o.Content);
            Assert.Empty(host.Entities);
        }

        [Fact]
        public void DimensionChainAndBaseline()
        {
            var (host, d) = New();
            JsonElement chain = Run(d, ToolCatalog.DimensionChain, @"{""points"":[[0,0],[2.4,0],[4.8,0],[6,0]],""dim_line_point"":[0,-3],""layer"":""DIMS""}");
            Assert.Equal(3, chain.GetProperty("succeeded").GetInt32());
            Assert.Equal(new[] { 2.4, 2.4, 1.2 }, chain.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("measurement").GetDouble()));
            Assert.All(host.Entities.Select(e => e.Spec).OfType<LinearDimensionSpec>(), dim => Assert.Equal(-3, dim.DimLinePoint.Y));

            JsonElement baseline = Run(d, ToolCatalog.DimensionChain,
                @"{""points"":[[0,10],[2,10],[5,10]],""dim_line_point"":[0,13],""mode"":""baseline"",""baseline_spacing"":1.5}");
            Assert.Equal(new[] { 2.0, 5.0 }, baseline.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("measurement").GetDouble()));
            List<LinearDimensionSpec> dims = host.Entities.Select(e => e.Spec).OfType<LinearDimensionSpec>().Skip(3).ToList();
            Assert.Equal(new[] { 13.0, 14.5 }, dims.Select(x => x.DimLinePoint.Y)); // stepping away from the points
            Assert.All(dims, x => Assert.Equal(new Point2(0, 10), x.P1));

            ToolOutcome missing = d.Execute(ToolCatalog.DimensionChain, Json(@"{""points"":[[0,0],[1,0]],""dim_line_point"":[0,1],""mode"":""baseline""}"));
            Assert.True(missing.IsError);
            Assert.Contains("baseline_spacing", missing.Content);
        }

        // ------------------------------------------------------------------ styles

        [Fact]
        public void ObjectsUseTheStyleAskedForAndUnknownStylesAreRefused()
        {
            var (host, d) = New();
            host.TextStyles.Add("AE 2.5");
            host.DimStyles.Add("AE 1-500");
            host.MLeaderStyles.Add("AE NOTES");

            string text = Draw(d, @"{""kind"":""text"",""position"":[0,0],""text"":""KERB"",""style"":""AE 2.5""}");
            string dim = Draw(d, @"{""kind"":""aligned_dimension"",""p1"":[0,0],""p2"":[5,0],""dim_line_point"":[0,2],""style"":""AE 1-500""}");
            string leader = Draw(d, @"{""kind"":""mleader"",""arrow_point"":[0,0],""position"":[4,4],""text"":""PIT"",""style"":""AE NOTES"",""text_style"":""AE 2.5""}");
            Assert.Equal("AE 2.5", Listed(d, text).GetProperty("style").GetString());
            Assert.Equal("AE 1-500", Listed(d, dim).GetProperty("style").GetString());
            Assert.Equal("AE NOTES", Listed(d, leader).GetProperty("style").GetString());

            ToolOutcome unknown = d.Execute(ToolCatalog.DrawEntities, Json(@"{""entities"":[{""kind"":""text"",""position"":[0,0],""text"":""X"",""style"":""ROMANS""}]}"));
            Assert.True(unknown.IsError);
            Assert.Contains("ROMANS", unknown.Content);
            Assert.Contains("is not in this drawing. Available: Standard, AE 2.5", unknown.Content);

            // A dimension style is not a text style.
            ToolOutcome wrongKind = d.Execute(ToolCatalog.DrawEntities, Json(@"{""entities"":[{""kind"":""text"",""position"":[0,0],""text"":""X"",""style"":""AE 1-500""}]}"));
            Assert.True(wrongKind.IsError);

            JsonElement info = Run(d, ToolCatalog.GetDrawingInfo, "{}");
            Assert.Contains("AE NOTES", info.GetProperty("mleader_styles").EnumerateArray().Select(s => s.GetString()));
            Assert.Contains("Standard", info.GetProperty("table_styles").EnumerateArray().Select(s => s.GetString()));
        }

        [Fact]
        public void StyleDetailsAreListedAsRowsWhenTheHostKnowsThem()
        {
            var info = new DrawingInfo
            {
                TextStyles = { "AE 2.5" },
                TextStyleDetails = new List<StyleInfo> { new StyleInfo { Name = "AE 2.5", Font = "arial.ttf", TextHeight = 0 } },
                DimStyles = { "AE 1-500" },
                DimStyleDetails = new List<StyleInfo> { new StyleInfo { Name = "AE 1-500", Scale = 0.5, TextHeight = 2.5, ArrowSize = 2.5, TextStyle = "AE 2.5" } },
            };
            string json = JsonSerializer.Serialize(DrawingInfoView.Compact(info, Json("{}")), ToolDispatcher.JsonOptions);
            JsonElement view = Json(json);
            Assert.Equal("arial.ttf", view.GetProperty("text_styles")[0][1].GetString());
            Assert.Equal(0.5, view.GetProperty("dim_styles")[0][1].GetDouble());
            Assert.Contains("overall scale", view.GetProperty("dim_style_columns").GetString());
        }

        // ------------------------------------------------------------------ multileaders

        [Fact]
        public void MultileadersCanBeDrawnReadAndEdited()
        {
            var (host, d) = New();
            string h = Draw(d, @"{""kind"":""mleader"",""arrow_points"":[[10,10],[12,8]],""position"":[20,15],""text"":""EXISTING PIT\nTO BE RETAINED"",""layer"":""AE CIV ANO - STORMWATER""}");

            JsonElement read = Run(d, ToolCatalog.ListEntities, @"{""kinds"":[""mleader""]}").GetProperty("entities")[0];
            Assert.Equal(h, read.GetProperty("handle").GetString());
            Assert.Equal("EXISTING PIT\nTO BE RETAINED", read.GetProperty("text").GetString());
            Assert.Equal(2, read.GetProperty("arrow_points").GetArrayLength());
            Assert.Equal(20, read.GetProperty("position")[0].GetDouble());
            Assert.False(read.TryGetProperty("text_box", out _)); // drawing aid only, not sent to Claude

            JsonElement edit = Run(d, ToolCatalog.EditAnnotations,
                @"{""edits"":[{""handle"":""" + h + @""",""text"":""NEW PIT"",""arrow_points"":[[11,9]],""position"":[25,20],""height"":3.5}]}");
            Assert.Equal(1, edit.GetProperty("succeeded").GetInt32());
            MLeaderSpec leader = host.Entities.Select(e => e.Spec).OfType<MLeaderSpec>().Single();
            Assert.Equal("NEW PIT", leader.Text);
            Assert.Equal(new Point2(11, 9), Assert.Single(leader.ArrowPoints));
            Assert.Equal(new Point2(25, 20), leader.TextPosition);
            Assert.Equal(3.5, leader.Height);

            // Moves with modify_entities like anything else.
            Run(d, ToolCatalog.ModifyEntities, @"{""operations"":[{""action"":""move"",""handles"":[""" + h + @"""],""dx"":100,""dy"":0}]}");
            Assert.Equal(new Point2(111, 9), leader.ArrowPoints[0]);
        }

        // ------------------------------------------------------------------ editing

        [Fact]
        public void TextAttributesAndDimensionTextAreEditedInPlace()
        {
            var (host, d) = New();
            host.Blocks.Add("AE-TITLE");
            string text = Draw(d, @"{""kind"":""text"",""position"":[0,0],""text"":""STAGE 1""}");
            string mtext = Draw(d, @"{""kind"":""mtext"",""position"":[0,10],""text"":""NOTES""}");
            string block = Draw(d, @"{""kind"":""block_insert"",""block_name"":""AE-TITLE"",""position"":[0,0],""attributes"":{""REV"":""A"",""TITLE"":""ROAD PLAN""}}");
            string dim = Draw(d, @"{""kind"":""linear_dimension"",""p1"":[0,0],""p2"":[6,0],""dim_line_point"":[0,2]}");

            Assert.Equal("A", Listed(d, block).GetProperty("attributes").GetProperty("REV").GetString());

            JsonElement r = Run(d, ToolCatalog.EditAnnotations, @"{""edits"":[
                {""handle"":""" + text + @""",""text"":""STAGE 2"",""rotation"":90},
                {""handle"":""" + mtext + @""",""text"":""NOTES\n1. ALL LEVELS TO AHD"",""width"":60},
                {""handle"":""" + block + @""",""attributes"":{""REV"":""B""}},
                {""handle"":""" + dim + @""",""text_override"":""6000 TYP""}]}");
            Assert.Equal(4, r.GetProperty("succeeded").GetInt32());

            Assert.Equal("STAGE 2", Listed(d, text).GetProperty("text").GetString());
            Assert.Equal(90, Listed(d, text).GetProperty("rotation").GetDouble());
            Assert.Contains("ALL LEVELS", Listed(d, mtext).GetProperty("text").GetString());
            JsonElement attributes = Listed(d, block).GetProperty("attributes");
            Assert.Equal("B", attributes.GetProperty("REV").GetString());
            Assert.Equal("ROAD PLAN", attributes.GetProperty("TITLE").GetString()); // untouched
            Assert.Equal("6000 TYP", Listed(d, dim).GetProperty("text").GetString());

            // "" puts the measured value back.
            Run(d, ToolCatalog.EditAnnotations, @"{""edits"":[{""handle"":""" + dim + @""",""text_override"":""""}]}");
            Assert.False(Listed(d, dim).TryGetProperty("text", out _));
            Assert.Equal(6, Listed(d, dim).GetProperty("measurement").GetDouble());
        }

        [Fact]
        public void AnEditThatDoesNotFitTheObjectFailsThatEditOnly()
        {
            var (host, d) = New();
            host.Blocks.Add("PIT");
            string block = Draw(d, @"{""kind"":""block_insert"",""block_name"":""PIT"",""position"":[0,0]}");
            string line = Draw(d, @"{""kind"":""line"",""start"":[0,0],""end"":[5,5]}");
            string text = Draw(d, @"{""kind"":""text"",""position"":[0,0],""text"":""OK""}");

            JsonElement r = Run(d, ToolCatalog.EditAnnotations, @"{""edits"":[
                {""handle"":""" + block + @""",""text"":""NOPE""},
                {""handle"":""" + line + @""",""text"":""NOPE""},
                {""handle"":""" + text + @""",""text"":""two\nlines""},
                {""handle"":""FFFF"",""text"":""x""},
                {""handle"":""" + text + @""",""text"":""FINE""}]}");

            Assert.Equal(1, r.GetProperty("succeeded").GetInt32());
            JsonElement results = r.GetProperty("results");
            Assert.Contains("attributes", results[0].GetProperty("error").GetString());
            Assert.Contains("isn't annotation", results[1].GetProperty("error").GetString());
            Assert.Contains("line break", results[2].GetProperty("error").GetString());
            Assert.Contains("Unknown handle", results[3].GetProperty("error").GetString());
            Assert.Equal("FINE", host.Entities.Select(e => e.Spec).OfType<TextSpec>().Single().Text);

            ToolOutcome nothing = d.Execute(ToolCatalog.EditAnnotations, Json(@"{""edits"":[{""handle"":""" + text + @"""}]}"));
            Assert.True(nothing.IsError);
            Assert.Contains("changes nothing", nothing.Content);
        }

        // ------------------------------------------------------------------ tables

        [Fact]
        public void TablesCanBeDrawnReadAndHaveCellsChanged()
        {
            var (host, d) = New();
            string h = Draw(d, @"{""kind"":""table"",""position"":[100,200],""title"":""PIT SCHEDULE"",""height"":2.5,
                ""rows"":[[""PIT"",""TYPE"",""SL""],[""P1"",""SEP"",12.345],[""P2"",""JP""]]}");

            TableSpec table = host.Entities.Select(e => e.Spec).OfType<TableSpec>().Single();
            Assert.Equal(3, table.ColumnCount);
            Assert.Equal("", table.Rows[2][2]);          // short rows are padded
            Assert.Equal("12.345", table.Rows[1][2]);    // numbers become text
            Assert.Equal(5.0, table.RowHeight);          // twice the text height
            Assert.Equal(3, table.ColumnWidths!.Count);

            JsonElement read = Listed(d, h);
            Assert.Equal("table", read.GetProperty("kind").GetString());
            Assert.Equal(4, read.GetProperty("row_count").GetInt32()); // title + heading + 2
            Assert.Equal("PIT SCHEDULE", read.GetProperty("rows")[0][0].GetString());
            Assert.Equal("SEP", read.GetProperty("rows")[2][1].GetString());
            Near(200 - 4 * 5, read.GetProperty("bounds_min")[1].GetDouble());

            Run(d, ToolCatalog.EditAnnotations, @"{""edits"":[{""handle"":""" + h + @""",""cells"":[
                {""row"":0,""column"":0,""text"":""STORMWATER PIT SCHEDULE""},{""row"":3,""column"":2,""text"":""11.900""}]}]}");
            Assert.Equal("STORMWATER PIT SCHEDULE", table.Title);
            Assert.Equal("11.900", table.Rows[2][2]);

            JsonElement outside = Run(d, ToolCatalog.EditAnnotations, @"{""edits"":[{""handle"":""" + h + @""",""cells"":[{""row"":9,""column"":0,""text"":""x""}]},{""handle"":""" + h + @""",""style"":""Standard""}]}");
            Assert.Contains("outside the table (4 rows x 3 columns", outside.GetProperty("results")[0].GetProperty("error").GetString());
        }

        // ------------------------------------------------------------------ alignment labels

        [Fact]
        public void ChainageLabelsSitExactlyOnTheAlignment()
        {
            var (host, d) = New();
            string alignment = Draw(d, Alignment);
            JsonElement r = Run(d, ToolCatalog.LabelAlignment,
                @"{""alignment"":""" + alignment + @""",""interval"":50,""tangent_points"":true,""ends"":true,""radii"":true,""layer"":""AE CIV ANO - ALL PLANS"",""tick_layer"":""TICKS""}");

            Near(100 + 50 * Math.PI / 2, r.GetProperty("alignment_length").GetDouble());
            List<JsonElement> labels = r.GetProperty("labels").EnumerateArray().ToList();
            Assert.Equal(
                new[] { "CH 0.000", "CH 50", "TP CH 100.000", "CH 150", "CH 178.540" },
                labels.Select(l => l.GetProperty("text").GetString()));
            Assert.Equal("tangent_point", labels[2].GetProperty("kind").GetString());

            // CH 50 is on the straight: its tick is square to it, 2 x text height long, centred on the line.
            Dictionary<string, EntitySpec> byHandle = host.Entities.ToDictionary(e => e.Handle, e => e.Spec);
            var tick = (LineSpec)byHandle[labels[1].GetProperty("tick").GetString()!];
            Near(50, tick.Start.X);
            Near(50, tick.End.X);
            Near(4, Math.Abs(tick.Start.Y - tick.End.Y));
            Assert.Equal("TICKS", tick.Layer);

            // Its label is on the right of travel (below), reads upwards and ends at the gap below the tick.
            var text = (TextSpec)byHandle[labels[1].GetProperty("handle").GetString()!];
            Assert.Equal(90, text.RotationDeg);
            Assert.Equal(TextAlign.Right, text.Align);
            Near(-3, text.Position.Y);       // tick half-length 2 + gap 1
            Near(51, text.Position.X);       // baseline half a text height to the right, so the text straddles x = 50
            Assert.Equal("AE CIV ANO - ALL PLANS", text.Layer);

            // CH 150 is 50 round the arc (centre 100,50; radius 50): 1 radian from the tangent point.
            JsonElement at150 = labels[3].GetProperty("point");
            Near(100 + 50 * Math.Sin(1), at150[0].GetDouble());
            Near(50 - 50 * Math.Cos(1), at150[1].GetDouble());

            // The radius label sits on the other side (inside the left-hand curve).
            JsonElement radius = r.GetProperty("radius_labels")[0];
            Assert.Equal("R 50.000", radius.GetProperty("text").GetString());
            var radiusText = (TextSpec)byHandle[radius.GetProperty("handle").GetString()!];
            Assert.True(Math.Sqrt(Math.Pow(radiusText.Position.X - 100, 2) + Math.Pow(radiusText.Position.Y - 50, 2)) < 50);
        }

        [Fact]
        public void ChainageLabelsHonourStartChainageRangeAndLeaderStyle()
        {
            var (host, d) = New();
            string alignment = Draw(d, @"{""kind"":""line"",""start"":[0,0],""end"":[0,95]}");
            JsonElement r = Run(d, ToolCatalog.LabelAlignment,
                @"{""alignment"":""" + alignment + @""",""start_chainage"":1000,""interval"":20,""from_chainage"":1030,""side"":""left"",""as"":""mleader"",""prefix"":""""}");

            Assert.Equal(new[] { "1040", "1060", "1080" }, r.GetProperty("labels").EnumerateArray().Select(l => l.GetProperty("text").GetString()));
            List<MLeaderSpec> leaders = host.Entities.Select(e => e.Spec).OfType<MLeaderSpec>().ToList();
            Assert.Equal(3, leaders.Count);
            Assert.Equal(new Point2(0, 40), leaders[0].ArrowPoints[0]);
            Assert.True(leaders[0].TextPosition.X < 0); // left of a line heading north is west

            ToolOutcome nothing = d.Execute(ToolCatalog.LabelAlignment, Json(@"{""alignment"":""" + alignment + @"""}"));
            Assert.True(nothing.IsError);
            Assert.Contains("at least one of interval", nothing.Content);
        }

        [Fact]
        public void TangentPointsAreWhereTheGeometryChanges()
        {
            // Straight, straight on (no change), arc R50, arc R20 the other way (a compound curve), straight.
            var path = new Path2(new List<Segment>
            {
                new LineSegment(new Point2(0, 0), new Point2(40, 0)),
                new LineSegment(new Point2(40, 0), new Point2(100, 0)),
                new ArcSegment(new Point2(100, 50), 50, -Math.PI / 2, Math.PI / 2),
                new ArcSegment(new Point2(170, 50), 20, Math.PI, -Math.PI / 2),
                new LineSegment(new Point2(170, 70), new Point2(200, 70)),
            }, false);

            List<double> found = ToolDispatcher.TangentPoints(path);
            Assert.Equal(3, found.Count);
            Near(100, found[0]);
            Near(100 + 25 * Math.PI, found[1]);
            Near(100 + 25 * Math.PI + 10 * Math.PI, found[2]);
        }

        [Fact]
        public void SetoutPointsFromAnAlignmentAreTabulated()
        {
            var (host, d) = New();
            string alignment = Draw(d, Alignment);
            JsonElement r = Run(d, ToolCatalog.SetoutPoints,
                @"{""alignment"":""" + alignment + @""",""tangent_points"":true,""chainages"":[50],""offset"":3,""levels"":[10.1,10.2,10.3,10.4],""table_position"":[200,100],""prefix"":""K""}");

            List<JsonElement> points = r.GetProperty("points").EnumerateArray().ToList();
            Assert.Equal(new[] { "K1", "K2", "K3", "K4" }, points.Select(p => p.GetProperty("name").GetString()));
            Assert.Equal(new[] { 0.0, 50, 100, 178.5398 }, points.Select(p => p.GetProperty("chainage").GetDouble()));
            Near(-3, points[1].GetProperty("northing").GetDouble());     // 3 right of a line heading east is south
            Near(153, points[3].GetProperty("easting").GetDouble());     // 3 outside the end of the left-hand curve
            Near(50, points[3].GetProperty("northing").GetDouble());

            TableSpec table = host.Entities.Select(e => e.Spec).OfType<TableSpec>().Single();
            Assert.Equal("SETOUT TABLE", table.Title);
            Assert.Equal(new[] { "POINT", "EASTING", "NORTHING", "CHAINAGE", "OFFSET", "LEVEL" }, table.Rows[0]);
            Assert.Equal(new[] { "K2", "50.000", "-3.000", "50.000", "3.000", "10.200" }, table.Rows[2]);
            Assert.Equal(5, table.Rows.Count);
            Assert.Equal(8, host.Entities.Count(e => e.Spec is LineSpec)); // a cross at each point
        }

        [Fact]
        public void SetoutPointsCanBeGivenDirectly()
        {
            var (host, d) = New();
            JsonElement r = Run(d, ToolCatalog.SetoutPoints,
                @"{""points"":[{""point"":[500123.4567,6900456.7891],""name"":""BM1""},{""point"":[500130,6900460]}],""marker"":""circle"",""decimals"":2,""table_position"":[0,0]}");

            Assert.Equal(new[] { "BM1", "SP1" }, r.GetProperty("points").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
            TableSpec table = host.Entities.Select(e => e.Spec).OfType<TableSpec>().Single();
            Assert.Equal(new[] { "POINT", "EASTING", "NORTHING" }, table.Rows[0]);
            Assert.Equal(new[] { "BM1", "500123.46", "6900456.79" }, table.Rows[1]);
            Assert.Equal(2, host.Entities.Count(e => e.Spec is CircleSpec));

            ToolOutcome both = d.Execute(ToolCatalog.SetoutPoints, Json(@"{}"));
            Assert.True(both.IsError);
        }

        // ------------------------------------------------------------------ view

        [Fact]
        public void ViewShowsAPictureAndListsOverlappingLabels()
        {
            var (_, d) = New();
            string a = Draw(d, @"{""kind"":""text"",""position"":[0,0],""text"":""EXISTING KERB""}");
            string b = Draw(d, @"{""kind"":""text"",""position"":[10,0.5],""text"":""NEW KERB""}");
            Draw(d, @"{""kind"":""text"",""position"":[0,20],""text"":""CLEAR OF EVERYTHING""}");
            Draw(d, @"{""kind"":""line"",""start"":[-5,-5],""end"":[60,-5]}");

            ToolOutcome o = d.Execute(ToolCatalog.ViewDrawing, Json(@"{""width_px"":600}"));
            Assert.False(o.IsError, o.Content);
            JsonElement r = Json(o.Content);
            Assert.Equal(1, r.GetProperty("overlap_count").GetInt32());
            JsonElement overlap = r.GetProperty("overlaps")[0];
            Assert.Equal(new[] { a, b }, new[] { overlap.GetProperty("a").GetString()!, overlap.GetProperty("b").GetString()! }.OrderBy(x => x));
            Assert.Contains("KERB", overlap.GetProperty("a_text").GetString());
            Assert.Equal(4, r.GetProperty("entities_shown").GetInt32());

            ToolImage image = Assert.Single(o.Images);
            Assert.Equal("image/png", image.MediaType);
            (int width, int height, byte[] pixels) = DecodePng(image.Data);
            Assert.Equal(600, width);
            Assert.Equal(r.GetProperty("picture").GetProperty("height_px").GetInt32(), height);
            Assert.Contains(pixels, p => p < 40);      // something dark was drawn
            Assert.True(pixels.Count(p => p == 255) > pixels.Length / 2, "mostly white background");

            // Moving one label clears the clash; no picture when only the list is wanted.
            Run(d, ToolCatalog.ModifyEntities, @"{""operations"":[{""action"":""move"",""handles"":[""" + b + @"""],""dx"":0,""dy"":6}]}");
            ToolOutcome again = d.Execute(ToolCatalog.ViewDrawing, Json(@"{""picture"":false}"));
            Assert.Equal(0, Json(again.Content).GetProperty("overlap_count").GetInt32());
            Assert.Empty(again.Images);
        }

        [Fact]
        public void TurnedLabelsOnlyClashWhenTheTextItselfOverlaps()
        {
            // Two labels at 45 degrees, side by side: their upright bounding boxes cross, the text doesn't.
            var entities = new List<EntitySummary>
            {
                Turned("A", 0, 0), Turned("B", 6, 0), Turned("C", 6.5, 0.2),
            };
            List<Overlap> overlaps = AnnotationClashes.Find(entities);
            Overlap only = Assert.Single(overlaps);
            Assert.Equal(new[] { "B", "C" }, new[] { only.A, only.B }.OrderBy(x => x));

            static EntitySummary Turned(string handle, double x, double y) => new EntitySummary
            {
                Handle = handle,
                Kind = "text",
                Text = "CH 120.000",
                TextBox = AnnotationGeometry.ToArrays(AnnotationGeometry.Box(new Point2(x, y), 20, 2, 45, 0, 0)),
            };
        }

        [Fact]
        public void ViewFramesHandlesAndReportsAnEmptyDrawing()
        {
            var (_, d) = New();
            ToolOutcome empty = d.Execute(ToolCatalog.ViewDrawing, Json("{}"));
            Assert.True(empty.IsError);
            Assert.Contains("empty", empty.Content);

            string near = Draw(d, @"{""kind"":""circle"",""center"":[10,10],""radius"":5}");
            Draw(d, @"{""kind"":""circle"",""center"":[5000,5000],""radius"":5}");
            JsonElement r = Json(d.Execute(ToolCatalog.ViewDrawing, Json(@"{""handles"":[""" + near + @"""]}")).Content);
            Assert.Equal(1, r.GetProperty("entities_shown").GetInt32());
            Assert.True(r.GetProperty("window")[1][0].GetDouble() < 20);
        }

        /// <summary>
        /// Draws a scene with every kind of annotation. Set CSD_BRIDGE_VIEW_OUT to a file path
        /// to keep the picture and look at it.
        /// </summary>
        [Fact]
        public void ASceneWithEveryKindOfAnnotationRenders()
        {
            var (host, d) = New();
            host.Blocks.Add("SW-PIT");
            string alignment = Draw(d, Alignment);
            Run(d, ToolCatalog.OffsetByChainage, @"{""alignment"":""" + alignment + @""",""lines"":[
                {""stretches"":[{""from"":0,""to"":178.5,""offset"":3.5}]},{""stretches"":[{""from"":0,""to"":178.5,""offset"":-3.5}]}]}");
            Run(d, ToolCatalog.LabelAlignment, @"{""alignment"":""" + alignment + @""",""interval"":20,""tangent_points"":true,""radii"":true}");
            Run(d, ToolCatalog.SetoutPoints, @"{""alignment"":""" + alignment + @""",""tangent_points"":true,""offset"":3.5,""table_position"":[20,75]}");
            Run(d, ToolCatalog.DrawEntities, @"{""entities"":[
                {""kind"":""block_insert"",""block_name"":""SW-PIT"",""position"":[60,-8]},
                {""kind"":""mleader"",""arrow_point"":[60,-8],""position"":[70,-20],""text"":""NEW SIDE ENTRY PIT\nSL 12.345""},
                {""kind"":""mleader"",""arrow_point"":[30,3.5],""position"":[38,-19],""text"":""KERB AND CHANNEL""},
                {""kind"":""mtext"",""position"":[72,-16],""text"":""CLASHING NOTE""},
                {""kind"":""radius_dimension"",""center"":[100,50],""radius"":50,""angle"":-45},
                {""kind"":""angular_dimension"",""center"":[100,50],""p1"":[100,0],""p2"":[150,50],""arc_point"":[120,30]},
                {""kind"":""linear_dimension"",""p1"":[10,-3.5],""p2"":[10,3.5],""dim_line_point"":[5,0],""rotation"":90}]}");

            ToolOutcome o = d.Execute(ToolCatalog.ViewDrawing, Json(@"{""width_px"":1200}"));
            Assert.False(o.IsError, o.Content);
            JsonElement r = Json(o.Content);
            Assert.True(r.GetProperty("overlap_count").GetInt32() >= 1, o.Content);
            (int width, int height, byte[] pixels) = DecodePng(o.Images[0].Data);
            Assert.Equal(1200, width);
            Assert.True(height > 300);
            Assert.Contains(pixels, p => p < 40);

            string? keep = Environment.GetEnvironmentVariable("CSD_BRIDGE_VIEW_OUT");
            if (!string.IsNullOrEmpty(keep))
            {
                File.WriteAllBytes(keep, o.Images[0].Data);
                File.WriteAllText(keep + ".json", o.Content);
                using var dxf = new StreamWriter(keep + ".dxf");
                DxfPreviewWriter.Write(host, dxf);
            }
        }

        private static uint Crc32(byte[] data, int offset, int count)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++)
            {
                c ^= data[i];
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
            }

            return c ^ 0xFFFFFFFFu;
        }

        /// <summary>Checks the PNG structure and checksums, and returns its size and raw pixel bytes.</summary>
        private static (int Width, int Height, byte[] Pixels) DecodePng(byte[] png)
        {
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.Take(8).ToArray());
            int width = 0, height = 0;
            var data = new MemoryStream();
            var types = new List<string>();
            for (int at = 8; at < png.Length;)
            {
                int length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
                string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
                types.Add(type);
                uint crc = (uint)((png[at + 8 + length] << 24) | (png[at + 9 + length] << 16) | (png[at + 10 + length] << 8) | png[at + 11 + length]);
                Assert.Equal(Crc32(png, at + 4, length + 4), crc);
                if (type == "IHDR")
                {
                    width = (png[at + 8] << 24) | (png[at + 9] << 16) | (png[at + 10] << 8) | png[at + 11];
                    height = (png[at + 12] << 24) | (png[at + 13] << 16) | (png[at + 14] << 8) | png[at + 15];
                    Assert.Equal(8, png[at + 16]);
                    Assert.Equal(2, png[at + 17]);
                }
                else if (type == "IDAT")
                {
                    data.Write(png, at + 8, length);
                }

                at += 12 + length;
            }

            Assert.Equal(new[] { "IHDR", "IDAT", "IEND" }, types);
            data.Position = 0;
            using var zlib = new ZLibStream(data, CompressionMode.Decompress); // verifies the Adler-32 too
            using var raw = new MemoryStream();
            zlib.CopyTo(raw);
            byte[] rows = raw.ToArray();
            Assert.Equal((width * 3 + 1) * height, rows.Length);
            return (width, height, rows);
        }
    }

}
