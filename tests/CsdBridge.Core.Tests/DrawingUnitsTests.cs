using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;
using CsdBridge.Core.Offline;
using CsdBridge.Core.Tools;
using CsdBridge.Core.Vehicles;
using Xunit;

namespace CsdBridge.Core.Tests
{
    /// <summary>Drawings in millimetres (or anything but metres): the vehicle tools work in metres and convert at the edges.</summary>
    public class DrawingUnitsTests
    {
        [Fact]
        public void UnitsComeFromTheDrawingUnlessTheSettingSaysOtherwise()
        {
            Assert.Equal(1000, DrawingUnits.FromInsunits("Millimeters")!.PerMetre);
            Assert.Equal("millimetres", DrawingUnits.FromInsunits("millimeters")!.Name);
            Assert.Equal(100, DrawingUnits.FromInsunits("Centimeters")!.PerMetre);
            Assert.True(DrawingUnits.FromInsunits("Meters")!.IsMetres);
            Assert.Equal(1 / 0.3048, DrawingUnits.FromInsunits("Feet")!.PerMetre, 9);
            Assert.Null(DrawingUnits.FromInsunits("Undefined"));
            Assert.Null(DrawingUnits.FromInsunits("Unitless"));
            Assert.Null(DrawingUnits.FromInsunits(null));
            Assert.Null(DrawingUnits.FromInsunits("LightYears"));

            // auto: what the drawing says, or metres when it doesn't say.
            Assert.Equal(1000, DrawingUnits.Resolve("auto", "Millimeters").PerMetre);
            Assert.Equal(1000, DrawingUnits.Resolve(null, "Millimeters").PerMetre);
            Assert.True(DrawingUnits.Resolve("auto", "Unitless").IsMetres);
            Assert.True(DrawingUnits.Resolve("", null).IsMetres);

            // An explicit setting wins over the drawing.
            Assert.True(DrawingUnits.Resolve("m", "Millimeters").IsMetres);
            Assert.Equal(1000, DrawingUnits.Resolve("Millimetres", "Meters").PerMetre);
            Assert.Equal(1000, DrawingUnits.Resolve("mm", "Unitless").PerMetre);
            Assert.Throws<ArgumentException>(() => DrawingUnits.Resolve("furlongs", "Meters"));

            // A scale saved with a path.
            Assert.Same(DrawingUnits.Millimetres, DrawingUnits.Of(1000));
            Assert.Same(DrawingUnits.Metres, DrawingUnits.Of(1));
            Assert.Same(DrawingUnits.Metres, DrawingUnits.Of(0));
            Assert.Equal("centimetres", DrawingUnits.Of(100).Name);
            Assert.Equal(250, DrawingUnits.Of(250).PerMetre);
        }

        [Fact]
        public void TheSizeOfTheDrawingQuestionsUnlikelyUnits()
        {
            // A car park drawn in millimetres is tens of thousands of units across; one drawn in metres, tens.
            Assert.Null(DrawingUnits.Millimetres.Doubt(85000));
            Assert.Null(DrawingUnits.Metres.Doubt(85));
            Assert.Null(DrawingUnits.Metres.Doubt(4000));        // a 4 km site
            Assert.Null(DrawingUnits.Metres.Doubt(null));        // an empty drawing
            Assert.Null(DrawingUnits.Metres.Doubt(0));
            Assert.Null(DrawingUnits.Metres.Doubt(8));           // a small detail in metres is fine

            // A metre drawing whose INSUNITS says millimetres: 85 units would be 85 mm.
            string? small = DrawingUnits.Millimetres.Doubt(85);
            Assert.NotNull(small);
            Assert.Contains("0.09 m across", small);
            Assert.Contains("choose Metres", small);

            // A millimetre drawing taken as metres: 85 km across.
            string? large = DrawingUnits.Metres.Doubt(85000);
            Assert.NotNull(large);
            Assert.Contains("85 km", large);
            Assert.Contains("choose Millimetres", large);
        }

        [Fact]
        public void GeometryIsScaledBothWaysAndMetresAreLeftAlone()
        {
            DrawingUnits mm = DrawingUnits.Millimetres;
            Assert.Equal(new Point2(2.5, -0.75), mm.ToModel(new Point2(2500, -750)));
            Assert.Equal(new Point2(2500, -750), mm.ToDrawing(new Point2(2.5, -0.75)));
            Assert.Equal(0.3, mm.ToModel(300), 12);

            var line = new LineSpec { Start = new Point2(1, 2), End = new Point2(3, 4), Layer = "L", ColorIndex = 3, LinetypeScale = 0.5 };
            var scaledLine = (LineSpec)mm.ToDrawing(line);
            Assert.Equal(new Point2(1000, 2000), scaledLine.Start);
            Assert.Equal(new Point2(3000, 4000), scaledLine.End);
            Assert.Equal("L", scaledLine.Layer);
            Assert.Equal((short)3, scaledLine.ColorIndex);
            Assert.Equal(0.5, scaledLine.LinetypeScale);     // relative to the drawing's own: not scaled
            Assert.Equal(new Point2(1, 2), line.Start);       // the original is untouched

            var poly = (PolylineSpec)mm.ToDrawing(new PolylineSpec { Points = { new Point2(0, 0), new Point2(1, 0), new Point2(1, 1) }, Bulges = new List<double> { 0, 0.4142, 0 }, Closed = true });
            Assert.Equal(new Point2(1000, 1000), poly.Points[2]);
            Assert.Equal(0.4142, poly.Bulges![1]);            // a bulge is a shape, not a size
            Assert.True(poly.Closed);

            var arc = (ArcSpec)mm.ToDrawing(new ArcSpec { Center = new Point2(1, 1), Radius = 6.3, StartAngleDeg = 10, EndAngleDeg = 80 });
            Assert.Equal(6300, arc.Radius, 9);
            Assert.Equal(10, arc.StartAngleDeg);
            var circle = (CircleSpec)mm.ToDrawing(new CircleSpec { Center = new Point2(2, 3), Radius = 0.15 });
            Assert.Equal(150, circle.Radius, 9);
            Assert.Equal(new Point2(2000, 3000), circle.Center);

            var text = (TextSpec)mm.ToDrawing(new TextSpec { Position = new Point2(1, 1), Text = "B99", Height = 0.3, RotationDeg = 30, Align = TextAlign.Center });
            Assert.Equal(300, text.Height!.Value, 9);
            Assert.Equal(30, text.RotationDeg);
            Assert.Equal(TextAlign.Center, text.Align);
            Assert.Null(((TextSpec)mm.ToDrawing(new TextSpec { Text = "x" })).Height);    // the drawing's default stays the drawing's default

            var hatch = (HatchSpec)mm.ToDrawing(new HatchSpec { Pattern = "ANSI31", Scale = 0.5, Loops = { new PolylineSpec { Points = { new Point2(0, 0), new Point2(2, 0), new Point2(2, 2) }, Closed = true } } });
            Assert.Equal(500, hatch.Scale, 9);
            Assert.Equal(new Point2(2000, 2000), hatch.Loops[0].Points[2]);
            Assert.Throws<NotSupportedException>(() => mm.ToDrawing(new MTextSpec { Text = "x" }));

            // A curve read from a millimetre drawing, in metres.
            Path2 path = mm.ToModel(new Path2(new List<Segment> { new LineSegment(new Point2(0, 0), new Point2(5000, 0)), new ArcSegment(new Point2(5000, 6300), 6300, -Math.PI / 2, Math.PI / 2) }, false));
            Assert.Equal(5 + 6.3 * Math.PI / 2, path.Length, 9);
            Assert.Equal(11.3, path.End.X, 9);

            Obstruction wall = Assert.Single(mm.ToModel(new[] { new Obstruction("1A", "A-WALL", new Path2(new List<Segment> { new LineSegment(new Point2(0, 4500), new Point2(30000, 4500)) }, false)) }));
            Assert.Equal("1A", wall.Handle);
            Assert.Equal(new Point2(30, 4.5), wall.Path.End);

            // A surface: asked in metres, sampled in drawing units, answered in metres.
            var asked = new List<Point2>();
            Func<IReadOnlyList<Point2>, IReadOnlyList<double?>> surface = mm.ToModel(points =>
            {
                asked.AddRange(points);
                return points.Select(p => p.X > 5000 ? (double?)null : 12500).ToList();
            });
            IReadOnlyList<double?> levels = surface(new[] { new Point2(1, 2), new Point2(9, 9) });
            Assert.Equal(new Point2(1000, 2000), asked[0]);
            Assert.Equal(12.5, levels[0]);
            Assert.Null(levels[1]);

            // In a metre drawing nothing is copied or changed.
            Assert.Same(line, DrawingUnits.Metres.ToDrawing(line));
            Assert.Same(line, DrawingUnits.Metres.ToModel(line));
        }

        [Fact]
        public void ARecordKeepsItsUnitsAndStaysPutWhenTheyChange()
        {
            var r = new ManoeuvreRecord
            {
                Mode = "solve",
                UnitsPerMetre = 1000,
                Start = new[] { 10.0, 5 },
                Target = new[] { 12.0, 9 },
                Stops = { new DriveStop { Point = new[] { 20.0, 0 }, HeadingDeg = 90 } },
                FollowNear = new[] { 1.0, 1 },
                BlockBase = new[] { 10.0, 5 },
                Solved = new List<SolvedMove> { new SolvedMove { Segments = { new[] { 0.0, 0, 1, 0 } } } },
                SolvedFor = "x",
            };
            ManoeuvreRecord back = ManoeuvreRecord.FromJson(r.ToJson());
            Assert.Equal(1000, back.UnitsPerMetre);
            Assert.Contains("\"units_per_metre\":1000", r.ToJson());

            // A path saved before units were kept was drawn in metres.
            Assert.Equal(1, ManoeuvreRecord.FromJson("{\"mode\":\"drive\",\"start\":[1,2]}").UnitsPerMetre);

            // Told it is a metre drawing after all: the positions stay where they are in the drawing
            // (10000, 5000 drawing units), so they are 1000 times further apart in metres, and the
            // manoeuvre must be solved again.
            back.SetUnits(1);
            Assert.Equal(1, back.UnitsPerMetre);
            Assert.Equal(new[] { 10000.0, 5000 }, back.Start);
            Assert.Equal(new[] { 12000.0, 9000 }, back.Target);
            Assert.Equal(new[] { 20000.0, 0 }, back.Stops[0].Point);
            Assert.Equal(90, back.Stops[0].HeadingDeg);
            Assert.Equal(new[] { 1000.0, 1000 }, back.FollowNear);
            Assert.Equal(new[] { 10000.0, 5000 }, back.BlockBase);
            Assert.Null(back.Solved);
            Assert.True(back.NeedsSolving);

            // The same units again changes nothing.
            back.SetUnits(1);
            Assert.Equal(new[] { 10000.0, 5000 }, back.Start);
        }

        // ------------------------------------------------------------------ the tools, in a millimetre drawing

        private static JsonDocument Draw(InMemoryDrawingHost host, string json)
        {
            var tools = new TurnPathTools(host, TurnPathConventions.Default());
            using JsonDocument input = JsonDocument.Parse(json);
            ToolOutcome outcome = tools.Execute(TurnPathTools.DrawTurnPath, input.RootElement);
            Assert.False(outcome.IsError, outcome.Content);
            return JsonDocument.Parse(outcome.Content);
        }

        private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(InMemoryDrawingHost host, string layer)
        {
            var pts = host.Entities.Where(e => e.Layer == layer).Select(e => e.Spec).OfType<PolylineSpec>().SelectMany(p => p.Points).ToList();
            Assert.NotEmpty(pts);
            return (pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
        }

        [Fact]
        public void ATurnTemplateInAMillimetreDrawingIsTheSameTurnAThousandTimesLarger()
        {
            const string Swept = "AE CIV PLN - VEHICLE SWEPT PATH";
            var metres = new InMemoryDrawingHost("Meters");
            using JsonDocument m = Draw(metres, "{\"mode\":\"template\",\"start\":[10,5],\"heading_deg\":0}");
            var millimetres = new InMemoryDrawingHost("Millimeters");
            using JsonDocument mm = Draw(millimetres, "{\"mode\":\"template\",\"start\":[10000,5000],\"heading_deg\":0}");

            // The vehicle and the turn are the same: lengths and radii are reported in metres either way.
            Assert.Equal(m.RootElement.GetProperty("path_length").GetDouble(), mm.RootElement.GetProperty("path_length").GetDouble(), 6);
            Assert.Equal(6.3, mm.RootElement.GetProperty("nominated_radius").GetDouble(), 9);
            Assert.Equal(m.RootElement.GetProperty("tightest_radius_outer_front_wheel").GetDouble(), mm.RootElement.GetProperty("tightest_radius_outer_front_wheel").GetDouble(), 6);
            Assert.Equal("metres", m.RootElement.GetProperty("drawing_units").GetString());
            Assert.StartsWith("millimetres", mm.RootElement.GetProperty("drawing_units").GetString());

            // What is drawn, and the points reported, are in drawing units.
            Assert.Equal(metres.Entities.Count, millimetres.Entities.Count);
            var a = Bounds(metres, Swept);
            var b = Bounds(millimetres, Swept);
            Assert.Equal(a.MinX * 1000, b.MinX, 3);
            Assert.Equal(a.MinY * 1000, b.MinY, 3);
            Assert.Equal(a.MaxX * 1000, b.MaxX, 3);
            Assert.Equal(a.MaxY * 1000, b.MaxY, 3);
            Assert.Equal(10000, mm.RootElement.GetProperty("start").GetProperty("rear_axle")[0].GetDouble(), 6);
            Assert.InRange(mm.RootElement.GetProperty("end").GetProperty("rear_axle")[1].GetDouble() - m.RootElement.GetProperty("end").GetProperty("rear_axle")[1].GetDouble() * 1000, -1, 1);
            Assert.InRange(mm.RootElement.GetProperty("extents").GetProperty("max")[0].GetDouble() - m.RootElement.GetProperty("extents").GetProperty("max")[0].GetDouble() * 1000, -1, 1);

            // The label is 0.3 m high: 300 in the millimetre drawing.
            Assert.Equal(0.3, metres.Entities.Select(e => e.Spec).OfType<TextSpec>().First().Height!.Value, 9);
            Assert.Equal(300, millimetres.Entities.Select(e => e.Spec).OfType<TextSpec>().First().Height!.Value, 9);

            // Told the drawing is really in metres (its INSUNITS is wrong), nothing is scaled.
            var wrong = new InMemoryDrawingHost("Millimeters");
            using JsonDocument asMetres = Draw(wrong, "{\"mode\":\"template\",\"start\":[10,5],\"heading_deg\":0,\"drawing_units\":\"m\"}");
            var c = Bounds(wrong, Swept);
            Assert.Equal(a.MaxX, c.MaxX, 6);
            Assert.Equal("metres", asMetres.RootElement.GetProperty("drawing_units").GetString());

            // And a drawing that doesn't say is taken as metres, unless told.
            var unset = new InMemoryDrawingHost("Unitless");
            using JsonDocument assumed = Draw(unset, "{\"mode\":\"template\",\"start\":[10,5],\"heading_deg\":0,\"dry_run\":true}");
            Assert.Equal("metres", assumed.RootElement.GetProperty("drawing_units").GetString());
            using JsonDocument told = Draw(unset, "{\"mode\":\"template\",\"start\":[10000,5000],\"heading_deg\":0,\"drawing_units\":\"mm\"}");
            Assert.Equal(b.MaxX, Bounds(unset, Swept).MaxX, 3);
        }

        [Fact]
        public void FollowingDrivingAndSolvingWorkInAMillimetreDrawing()
        {
            var host = new InMemoryDrawingHost("Millimeters");
            IReadOnlyList<ItemResult> made = host.CreateEntities(new EntitySpec[]
            {
                new LineSpec { Start = new Point2(-30000, -4500), End = new Point2(30000, -4500), Layer = "KERB" },
                new LineSpec { Start = new Point2(-30000, 4500), End = new Point2(30000, 4500), Layer = "KERB" },
                new PolylineSpec { Points = { new Point2(-20000, 0), new Point2(0, 0), new Point2(0, 15000) }, Layer = "CL" },
            });

            // Follow the centreline: 35 m of it, and it runs into the far kerb.
            using JsonDocument follow = Draw(host, $"{{\"mode\":\"follow\",\"handle\":\"{made[2].Handle}\",\"obstruction_layers\":[\"KERB\"],\"dry_run\":true}}");
            Assert.Equal(35, follow.RootElement.GetProperty("path_length").GetDouble(), 1);
            JsonElement hit = follow.RootElement.GetProperty("conflicts").GetProperty("items").EnumerateArray().First(i => i.GetProperty("kind").GetString() == "body_hit");
            Assert.Equal(4500, hit.GetProperty("at")[1].GetDouble(), 0);     // reported where it is in the drawing

            // Drive a B85 3 m off the centreline: its front axle goes to a point 12 m ahead of the rear
            // axle (12 m less the wheelbase), and its side passes 565 mm from the kerb.
            using JsonDocument drive = Draw(host, "{\"mode\":\"points\",\"vehicle\":\"B85\",\"points\":[[-10000,3000],[2000,3000]],\"heading_deg\":0,\"obstruction_layers\":[\"KERB\"],\"dry_run\":true}");
            Assert.Equal(12 - 2.8, drive.RootElement.GetProperty("path_length").GetDouble(), 3);
            Assert.True(drive.RootElement.GetProperty("conflicts").GetProperty("passes").GetBoolean());
            Assert.Equal(4500 - 3000 - 935, drive.RootElement.GetProperty("conflicts").GetProperty("closest_gap_mm").GetDouble(), 0);

            // Solve: the same three-point turn as in a 9 m street drawn in metres.
            using JsonDocument solve = Draw(host, "{\"mode\":\"solve\",\"start\":[0,-2000],\"heading_deg\":0,\"target\":[0,2000],\"target_heading_deg\":180,\"obstruction_layers\":[\"KERB\"],\"draw\":{\"block\":true}}");
            JsonElement solver = solve.RootElement.GetProperty("solver");
            Assert.Equal(3, solver.GetProperty("movements").GetInt32());
            Assert.Equal(300, solver.GetProperty("clearance_kept_mm").GetDouble());
            Assert.Equal(2, solver.GetProperty("obstructions_in_range").GetInt32());
            Assert.True(solve.RootElement.GetProperty("conflicts").GetProperty("passes").GetBoolean());
            JsonElement end = solve.RootElement.GetProperty("end").GetProperty("rear_axle");
            Assert.Equal(0, end[0].GetDouble(), 0);
            Assert.Equal(2000, end[1].GetDouble(), 0);

            // Drawn between the kerbs, in millimetres.
            var swept = host.Entities.Where(e => e.Layer == "AE CIV PLN - VEHICLE SWEPT PATH").Select(e => e.Spec).OfType<PolylineSpec>().SelectMany(p => p.Points).ToList();
            Assert.NotEmpty(swept);
            Assert.True(swept.Max(p => p.Y) < 4200 && swept.Min(p => p.Y) > -4200, "the body stays 300 mm inside the kerbs");
            Assert.True(swept.Max(p => p.X) - swept.Min(p => p.X) > 5000, "drawn at full size");
        }

        [Fact]
        public void TheGroundClearanceCheckReadsAndDrawsOnAMillimetreSection()
        {
            static JsonDocument Check(InMemoryDrawingHost host, PolylineSpec plot)
            {
                string handle = host.CreateEntities(new[] { plot })[0].Handle!;
                var tools = new GroundClearanceTools(host, TurnPathConventions.Default());
                using JsonDocument input = JsonDocument.Parse($"{{\"mode\":\"section\",\"handle\":\"{handle}\",\"vertical_exaggeration\":10}}");
                ToolOutcome outcome = tools.Execute(GroundClearanceTools.CheckGroundClearance, input.RootElement);
                Assert.False(outcome.IsError, outcome.Content);
                return JsonDocument.Parse(outcome.Content);
            }

            // A hump over 40 m, plotted at a vertical exaggeration of 10: in metres, and in millimetres.
            var metres = new InMemoryDrawingHost("Meters");
            using JsonDocument m = Check(metres, new PolylineSpec { Points = { new Point2(0, 500), new Point2(20, 518), new Point2(40, 500) }, Layer = "LS" });
            var millimetres = new InMemoryDrawingHost("Millimeters");
            using JsonDocument mm = Check(millimetres, new PolylineSpec { Points = { new Point2(0, 500000), new Point2(20000, 518000), new Point2(40000, 500000) }, Layer = "LS" });

            Assert.Equal(m.RootElement.GetProperty("min_gap_mm").GetDouble(), mm.RootElement.GetProperty("min_gap_mm").GetDouble());
            Assert.False(mm.RootElement.GetProperty("passes").GetBoolean());
            Assert.Equal(40, mm.RootElement.GetProperty("profile").GetProperty("to").GetDouble(), 6);
            Assert.StartsWith("millimetres", mm.RootElement.GetProperty("drawing_units").GetString());
            Assert.False(m.RootElement.TryGetProperty("drawing_units", out _));

            // The templates land on the plot: within its extents, a thousand times the metre drawing's.
            const string Fail = "AE CIV PLN - VEHICLE GROUND CLEARANCE FAIL";
            var a = Bounds(metres, Fail);
            var b = Bounds(millimetres, Fail);
            Assert.Equal(a.MinX * 1000, b.MinX, 2);
            Assert.Equal(a.MaxY * 1000, b.MaxY, 2);
            Assert.InRange(b.MinX, 0, 40000);
            Assert.InRange(b.MaxY, 500000, 520000);
        }
    }
}
