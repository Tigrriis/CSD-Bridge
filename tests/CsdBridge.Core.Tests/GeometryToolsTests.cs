using System;
using System.Linq;
using System.Text.Json;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;
using CsdBridge.Core.Offline;
using CsdBridge.Core.Tools;
using Xunit;

namespace CsdBridge.Core.Tests
{
    /// <summary>The exact-geometry tools, end to end through the dispatcher, with known answers.</summary>
    public class GeometryToolsTests
    {
        private readonly InMemoryDrawingHost _host = new InMemoryDrawingHost();
        private readonly ToolDispatcher _tools;

        public GeometryToolsTests()
        {
            _tools = new ToolDispatcher(_host);
        }

        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

        private JsonElement Run(string tool, string input, bool expectError = false)
        {
            ToolOutcome outcome = _tools.Execute(tool, Json(input));
            Assert.True(outcome.IsError == expectError, outcome.Content);
            return expectError ? default : JsonDocument.Parse(outcome.Content).RootElement.Clone();
        }

        private string Draw(string entityJson)
        {
            JsonElement r = Run(ToolCatalog.DrawEntities, "{\"entities\":[" + entityJson + "]}");
            return r.GetProperty("results")[0].GetProperty("handle").GetString()!;
        }

        private static double[] Pt(JsonElement e) => e.EnumerateArray().Select(v => v.GetDouble()).ToArray();

        private static void Near(double expected, double actual, double tol = 1e-6) => Assert.True(Math.Abs(expected - actual) <= tol, $"expected {expected}, got {actual}");

        private string Rect() => Draw("{\"kind\":\"polyline\",\"points\":[[0,0],[6000,0],[6000,4000],[0,4000]],\"closed\":true}");

        // ------------------------------------------------------------------ calculate

        [Fact]
        public void Calculate_chains_named_values_and_reports_errors_per_expression()
        {
            JsonElement r = Run(ToolCatalog.Calculate, @"{""expressions"":[
                {""name"":""bays"",""expr"":""ceil(6000/2400)""},
                {""name"":""bay"",""expr"":""6000/bays""},
                {""expr"":""(6000-2*150)/3""},
                {""expr"":""sin(30) + atan2(1,1)""},
                {""expr"":""2^3^2 + -2^2 + 1e3""},
                {""expr"":""1/0""},
                {""expr"":""wall + 1""},
                {""expr"":""sqrt(1,2)""},
                {""expr"":""round(10/3, 2) + hypot(3,4) + max(1,7,3) + abs(-2)""}]}");

            var results = r.GetProperty("results").EnumerateArray().ToList();
            Near(3, results[0].GetProperty("value").GetDouble());
            Near(2000, results[1].GetProperty("value").GetDouble());
            Near(1900, results[2].GetProperty("value").GetDouble());
            Near(45.5, results[3].GetProperty("value").GetDouble());
            Near(512 - 4 + 1000, results[4].GetProperty("value").GetDouble());
            Assert.Contains("division by zero", results[5].GetProperty("error").GetString());
            Assert.Contains("unknown name 'wall'", results[6].GetProperty("error").GetString());
            Assert.Contains("takes 1 argument", results[7].GetProperty("error").GetString());
            Near(3.33 + 5 + 7 + 2, results[8].GetProperty("value").GetDouble());
        }

        [Fact]
        public void Calculator_rejects_builtin_names_and_bad_syntax()
        {
            var calc = new Calculator();
            Assert.Throws<FormatException>(() => calc.Set("pi", 3));
            Assert.Throws<FormatException>(() => calc.Set("2x", 3));
            Assert.Throws<FormatException>(() => calc.Evaluate("(1+2"));
            Assert.Throws<FormatException>(() => calc.Evaluate("1 + * 2"));
            Assert.Throws<FormatException>(() => calc.Evaluate("asin(2)"));
        }

        // ------------------------------------------------------------------ offset

        [Fact]
        public void Offset_line_goes_towards_the_side_point()
        {
            string line = Draw("{\"kind\":\"line\",\"start\":[0,0],\"end\":[1000,0],\"layer\":\"S-CONC\"}");

            JsonElement up = Run(ToolCatalog.OffsetEntities, "{\"offsets\":[{\"handles\":[\"" + line + "\"],\"distance\":50,\"side_point\":[500,100]}]}");
            JsonElement down = Run(ToolCatalog.OffsetEntities, "{\"offsets\":[{\"handles\":[\"" + line + "\"],\"distance\":50,\"side_point\":[500,-10],\"layer\":\"S-REO\"}]}");

            var a = (LineSpec)_host.Entities.Single(e => e.Handle == up.GetProperty("results")[0].GetProperty("new_handles")[0].GetString()).Spec;
            var b = (LineSpec)_host.Entities.Single(e => e.Handle == down.GetProperty("results")[0].GetProperty("new_handles")[0].GetString()).Spec;
            Near(50, a.Start.Y);
            Near(50, a.End.Y);
            Assert.Equal("S-CONC", a.Layer); // keeps the source layer
            Near(-50, b.Start.Y);
            Assert.Equal("S-REO", b.Layer);
            Assert.Contains(_host.Layers, l => l.Name == "S-REO");
        }

        [Fact]
        public void Offset_closed_rectangle_inside_and_outside_with_count()
        {
            string rect = Rect();
            JsonElement r = Run(ToolCatalog.OffsetEntities, "{\"offsets\":[" +
                "{\"handles\":[\"" + rect + "\"],\"distance\":50,\"side\":\"inside\",\"count\":3}," +
                "{\"handles\":[\"" + rect + "\"],\"distance\":100,\"side\":\"outside\"}]}");

            var inner = r.GetProperty("results")[0].GetProperty("new_handles").EnumerateArray().Select(h => h.GetString()).ToList();
            Assert.Equal(3, inner.Count);
            var third = (PolylineSpec)_host.Entities.Single(e => e.Handle == inner[2]).Spec;
            Assert.Equal(new[] { 150.0, 150, 5850, 150, 5850, 3850, 150, 3850 }, third.Points.SelectMany(p => new[] { p.X, p.Y }).Select(v => Math.Round(v, 6)));

            string outer = r.GetProperty("results")[1].GetProperty("new_handles")[0].GetString()!;
            JsonElement m = Run(ToolCatalog.Measure, "{\"handles\":[\"" + outer + "\"]}");
            Near(6200 * 4200, m.GetProperty("entities")[0].GetProperty("area").GetDouble());
        }

        [Fact]
        public void Offset_works_whichever_way_the_polyline_runs()
        {
            string clockwise = Draw("{\"kind\":\"polyline\",\"points\":[[0,0],[0,4000],[6000,4000],[6000,0]],\"closed\":true}");
            JsonElement r = Run(ToolCatalog.OffsetEntities, "{\"offsets\":[{\"handles\":[\"" + clockwise + "\"],\"distance\":50,\"side\":\"inside\"}]}");
            string h = r.GetProperty("results")[0].GetProperty("new_handles")[0].GetString()!;
            Near(5900 * 3900, Run(ToolCatalog.Measure, "{\"handles\":[\"" + h + "\"]}").GetProperty("entities")[0].GetProperty("area").GetDouble());
        }

        [Fact]
        public void Offset_open_polyline_mitres_the_corner()
        {
            string l = Draw("{\"kind\":\"polyline\",\"points\":[[0,0],[1000,0],[1000,1000]]}");
            JsonElement r = Run(ToolCatalog.OffsetEntities, "{\"offsets\":[{\"handles\":[\"" + l + "\"],\"distance\":100,\"side_point\":[500,200]}]}");
            var spec = (PolylineSpec)_host.Entities.Single(e => e.Handle == r.GetProperty("results")[0].GetProperty("new_handles")[0].GetString()).Spec;
            Assert.Equal(new[] { 0.0, 100, 900, 100, 900, 1000 }, spec.Points.SelectMany(p => new[] { p.X, p.Y }).Select(v => Math.Round(v, 6)));
        }

        [Fact]
        public void Offset_circles_and_arcs()
        {
            string circle = Draw("{\"kind\":\"circle\",\"center\":[0,0],\"radius\":500}");
            string arc = Draw("{\"kind\":\"arc\",\"center\":[0,0],\"radius\":1000,\"start_angle\":0,\"end_angle\":90}");
            JsonElement r = Run(ToolCatalog.OffsetEntities, "{\"offsets\":[" +
                "{\"handles\":[\"" + circle + "\"],\"distance\":100,\"side\":\"outside\"}," +
                "{\"handles\":[\"" + circle + "\"],\"distance\":100,\"side\":\"inside\"}," +
                "{\"handles\":[\"" + arc + "\"],\"distance\":250,\"side_point\":[100,100]}]}");

            CircleSpec Circle(int i) => (CircleSpec)_host.Entities.Single(e => e.Handle == r.GetProperty("results")[i].GetProperty("new_handles")[0].GetString()).Spec;
            Near(600, Circle(0).Radius);
            Near(400, Circle(1).Radius);
            var a = (ArcSpec)_host.Entities.Single(e => e.Handle == r.GetProperty("results")[2].GetProperty("new_handles")[0].GetString()).Spec;
            Near(750, a.Radius);
        }

        [Fact]
        public void Offset_failures_explain_themselves_and_change_nothing()
        {
            string rect = Rect();
            string line = Draw("{\"kind\":\"line\",\"start\":[0,0],\"end\":[1000,0]}");
            string text = Draw("{\"kind\":\"text\",\"position\":[0,0],\"text\":\"A\"}");
            string bulged = Draw("{\"kind\":\"polyline\",\"points\":[[0,0],[1000,0]],\"bulges\":[0.5,0]}");
            int before = _host.Entities.Count;

            ToolOutcome o = _tools.Execute(ToolCatalog.OffsetEntities, Json("{\"offsets\":[" +
                "{\"handles\":[\"" + rect + "\"],\"distance\":2500,\"side\":\"inside\"}," +
                "{\"handles\":[\"" + line + "\"],\"distance\":10,\"side\":\"inside\"}," +
                "{\"handles\":[\"" + text + "\"],\"distance\":10,\"side_point\":[0,10]}," +
                "{\"handles\":[\"" + line + "\"],\"distance\":10,\"side_point\":[500,0]}," +
                "{\"handles\":[\"" + bulged + "\"],\"distance\":10,\"side_point\":[0,10]}," +
                "{\"handles\":[\"" + line + "\",\"" + text + "\"],\"distance\":10,\"side_point\":[500,50]}]}"));

            Assert.True(o.IsError); // every operation failed
            Assert.Equal(before, _host.Entities.Count); // including the part-good last one
            Assert.Contains("too large to fit inside", o.Content);
            Assert.Contains("not closed", o.Content);
            Assert.Contains("not a curve", o.Content);
            Assert.Contains("on the curve", o.Content);
            Assert.Contains("arc segments", o.Content);
        }

        [Fact]
        public void Offset_input_needs_exactly_one_side()
        {
            ToolOutcome o = _tools.Execute(ToolCatalog.OffsetEntities, Json(
                "{\"offsets\":[{\"handles\":[\"1\"],\"distance\":10},{\"handles\":[\"1\"],\"distance\":10,\"side\":\"inside\",\"side_point\":[0,0]},{\"handles\":[\"1\"],\"distance\":-5,\"side\":\"inside\"}]}"));
            Assert.True(o.IsError);
            Assert.Contains("3 problems", o.Content);
            Assert.Contains("offsets[2].distance must be greater than 0", o.Content);
        }

        // ------------------------------------------------------------------ intersect

        [Fact]
        public void Intersect_lines_circles_and_arcs()
        {
            string x1 = Draw("{\"kind\":\"line\",\"start\":[0,0],\"end\":[10,10]}");
            string x2 = Draw("{\"kind\":\"line\",\"start\":[0,10],\"end\":[10,0]}");
            string axis = Draw("{\"kind\":\"line\",\"start\":[-10,0],\"end\":[10,0]}");
            string circle = Draw("{\"kind\":\"circle\",\"center\":[0,0],\"radius\":5}");
            string circle2 = Draw("{\"kind\":\"circle\",\"center\":[8,0],\"radius\":5}");
            string topArc = Draw("{\"kind\":\"arc\",\"center\":[0,0],\"radius\":5,\"start_angle\":0,\"end_angle\":180}");
            string low = Draw("{\"kind\":\"line\",\"start\":[-10,-3],\"end\":[10,-3]}");
            string high = Draw("{\"kind\":\"line\",\"start\":[-10,3],\"end\":[10,3]}");

            JsonElement r = Run(ToolCatalog.Intersect, "{\"handles\":[\"" + x1 + "\",\"" + x2 + "\"]}");
            Assert.Equal(new[] { 5.0, 5.0 }, Pt(r.GetProperty("intersections")[0].GetProperty("points")[0]));

            r = Run(ToolCatalog.Intersect, "{\"handles\":[\"" + axis + "\"],\"with\":[\"" + circle + "\"]}");
            var pts = r.GetProperty("intersections")[0].GetProperty("points").EnumerateArray().Select(Pt).OrderBy(p => p[0]).ToList();
            Assert.Equal(new[] { -5.0, 0 }, pts[0]);
            Assert.Equal(new[] { 5.0, 0 }, pts[1]);

            r = Run(ToolCatalog.Intersect, "{\"handles\":[\"" + circle + "\",\"" + circle2 + "\"]}");
            Assert.Equal(new[] { 4.0, 4.0 }, r.GetProperty("intersections")[0].GetProperty("points").EnumerateArray().Select(p => Pt(p)[0]));

            r = Run(ToolCatalog.Intersect, "{\"handles\":[\"" + topArc + "\"],\"with\":[\"" + low + "\",\"" + high + "\"]}");
            Assert.Equal(1, r.GetProperty("intersections").GetArrayLength()); // only the upper line meets the upper arc
            Assert.Equal(high, r.GetProperty("intersections")[0].GetProperty("b").GetString());
            Assert.Equal(2, r.GetProperty("pairs_checked").GetInt32());
        }

        [Fact]
        public void Intersect_extend_finds_apparent_intersections_and_skips_non_curves()
        {
            string a = Draw("{\"kind\":\"line\",\"start\":[0,0],\"end\":[1,0]}");
            string b = Draw("{\"kind\":\"line\",\"start\":[5,1],\"end\":[5,2]}");
            string text = Draw("{\"kind\":\"text\",\"position\":[0,0],\"text\":\"A\"}");

            JsonElement plain = Run(ToolCatalog.Intersect, "{\"handles\":[\"" + a + "\",\"" + b + "\",\"" + text + "\"]}");
            Assert.Equal(0, plain.GetProperty("intersections").GetArrayLength());
            Assert.Contains("not a curve", plain.GetProperty("skipped")[0].GetString());

            JsonElement extended = Run(ToolCatalog.Intersect, "{\"handles\":[\"" + a + "\",\"" + b + "\"],\"extend\":true}");
            Assert.Equal(new[] { 5.0, 0 }, Pt(extended.GetProperty("intersections")[0].GetProperty("points")[0]));
        }

        // ------------------------------------------------------------------ measure

        [Fact]
        public void Measure_lengths_areas_points_and_distances()
        {
            string rect = Rect();
            string line = Draw("{\"kind\":\"line\",\"start\":[0,0],\"end\":[3,4]}");
            string circle = Draw("{\"kind\":\"circle\",\"center\":[0,0],\"radius\":1}");
            string arc = Draw("{\"kind\":\"arc\",\"center\":[0,0],\"radius\":2,\"start_angle\":0,\"end_angle\":90}");
            string disc = Draw("{\"kind\":\"polyline\",\"points\":[[0,0],[2,0]],\"bulges\":[1,1],\"closed\":true}");
            string dim = Draw("{\"kind\":\"aligned_dimension\",\"p1\":[0,0],\"p2\":[300,400],\"dim_line_point\":[0,100]}");

            JsonElement r = Run(ToolCatalog.Measure, "{\"handles\":[\"" + rect + "\",\"" + line + "\",\"" + circle + "\",\"" + arc + "\",\"" + disc + "\",\"" + dim + "\",\"BAD\"],\"point\":[3,0],\"from\":[0,0],\"to\":[3,4]}");
            var e = r.GetProperty("entities").EnumerateArray().ToList();

            Near(20000, e[0].GetProperty("length").GetDouble());
            Near(24e6, e[0].GetProperty("area").GetDouble());
            Assert.True(e[0].GetProperty("closed").GetBoolean());

            Near(5, e[1].GetProperty("length").GetDouble());
            Assert.Equal(new[] { 1.5, 2.0 }, Pt(e[1].GetProperty("mid")));
            Near(2.4, e[1].GetProperty("distance_to_point").GetDouble()); // (3,0) to the 3-4-5 line

            Near(2 * Math.PI, e[2].GetProperty("length").GetDouble());
            Near(Math.PI, e[2].GetProperty("area").GetDouble());
            Near(Math.PI, e[3].GetProperty("length").GetDouble());
            Near(Math.PI, e[4].GetProperty("area").GetDouble()); // two bulge-1 semicircles = a circle
            Near(500, e[5].GetProperty("measurement").GetDouble());
            Assert.Equal("not found", e[6].GetProperty("error").GetString());

            JsonElement between = r.GetProperty("between");
            Near(5, between.GetProperty("distance").GetDouble());
            Near(53.130102, between.GetProperty("angle").GetDouble());
        }

        [Fact]
        public void Measure_between_points_needs_no_entities()
        {
            JsonElement r = Run(ToolCatalog.Measure, "{\"from\":[1000,1000],\"to\":[0,1000]}");
            Near(1000, r.GetProperty("between").GetProperty("distance").GetDouble());
            Near(180, r.GetProperty("between").GetProperty("angle").GetDouble());
            Assert.True(_tools.Execute(ToolCatalog.Measure, Json("{\"from\":[0,0]}")).IsError);
        }

        // ------------------------------------------------------------------ points on curve

        [Fact]
        public void Points_on_curve_divide_spacing_distances_and_from_end()
        {
            string line = Draw("{\"kind\":\"line\",\"start\":[0,0],\"end\":[6000,0]}");
            string rect = Rect();
            string circle = Draw("{\"kind\":\"circle\",\"center\":[0,0],\"radius\":10}");

            JsonElement r = Run(ToolCatalog.PointsOnCurve, "{\"requests\":[" +
                "{\"handle\":\"" + line + "\",\"divide\":3}," +
                "{\"handle\":\"" + line + "\",\"spacing\":1800}," +
                "{\"handle\":\"" + line + "\",\"spacing\":1800,\"start_offset\":300}," +
                "{\"handle\":\"" + line + "\",\"distances\":[1000],\"from_end\":true}," +
                "{\"handle\":\"" + rect + "\",\"divide\":4}," +
                "{\"handle\":\"" + circle + "\",\"divide\":4}," +
                "{\"handle\":\"" + line + "\",\"distances\":[7000]}]}");
            var results = r.GetProperty("results").EnumerateArray().ToList();
            double[] Xs(int i) => results[i].GetProperty("points").EnumerateArray().Select(p => Pt(p.GetProperty("point"))[0]).ToArray();

            Assert.Equal(new[] { 0.0, 2000, 4000, 6000 }, Xs(0));
            Assert.Equal(new[] { 0.0, 1800, 3600, 5400 }, Xs(1));
            Assert.Equal(new[] { 300.0, 2100, 3900, 5700 }, Xs(2));
            Assert.Equal(new[] { 5000.0 }, Xs(3));

            var corners = results[4].GetProperty("points").EnumerateArray().Select(p => Pt(p.GetProperty("point"))).ToList();
            Assert.Equal(4, corners.Count); // closed: the start isn't repeated at the end
            Assert.Equal(new[] { 6000.0, 4000 }, corners[2]);
            Near(0, results[4].GetProperty("points")[1].GetProperty("angle").GetDouble()); // 5000 along is still on the 6000 bottom edge
            var circlePts = results[5].GetProperty("points").EnumerateArray().Select(p => Pt(p.GetProperty("point"))).ToList();
            Assert.Equal(new[] { 0.0, 10 }, circlePts[1]);
            Near(180, results[5].GetProperty("points")[1].GetProperty("angle").GetDouble());

            Assert.False(results[6].GetProperty("ok").GetBoolean());
            Assert.Contains("beyond the curve", results[6].GetProperty("error").GetString());
        }

        [Fact]
        public void Points_on_curve_needs_exactly_one_mode()
        {
            ToolOutcome o = _tools.Execute(ToolCatalog.PointsOnCurve, Json("{\"requests\":[{\"handle\":\"1\"},{\"handle\":\"1\",\"divide\":2,\"spacing\":5}]}"));
            Assert.True(o.IsError);
            Assert.Contains("exactly one of divide, spacing or distances", o.Content);
        }

        [Fact]
        public void Every_catalogue_tool_is_handled()
        {
            foreach (ToolDefinition t in ToolCatalog.All)
            {
                ToolOutcome o = _tools.Execute(t.Name, Json("{}"));
                Assert.DoesNotContain("Unknown tool", o.Content);
            }
        }
    }
}
