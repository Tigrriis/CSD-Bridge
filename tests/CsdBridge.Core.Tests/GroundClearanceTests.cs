using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CsdBridge.Core.Civil;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;
using CsdBridge.Core.Offline;
using CsdBridge.Core.Tools;
using CsdBridge.Core.Vehicles;
using Xunit;

namespace CsdBridge.Core.Tests
{
    /// <summary>AS/NZS 2890.1 Appendix C ground clearance template checks.</summary>
    public class GroundClearanceTests
    {
        private static TurnPathConventions Conventions() =>
            TurnPathConventions.Load(Path.Combine(AppContext.BaseDirectory, "turn-paths.json"));

        private static GroundProfile Profile(params (double Ch, double Z)[] pts) => new GroundProfile(pts.Select(p => new GroundPoint(p.Ch, p.Z)));

        /// <summary>A crest: up at +g, then down at -g, 20 m each side.</summary>
        private static GroundProfile Crest(double grade) => Profile((0, 0), (20, 20 * grade), (40, 0));

        /// <summary>Flat, then a ramp climbing at the grade.</summary>
        private static GroundProfile Ramp(double grade) => Profile((0, 0), (20, 0), (40, 20 * grade));

        [Fact]
        public void TemplateMatchesFigureC1()
        {
            DesignVehicle v = DesignVehicle.B99;
            (double X, double Z)[] t = v.UndersideTemplate();
            Assert.Equal(4, t.Length);
            Assert.Equal(-0.95, t[0].X, 9);                       // rear extremity
            Assert.Equal(0.12, t[1].Z, 9);                        // 120 mm at the rear axle
            Assert.Equal(3.05, t[2].X, 9);                        // front axle
            Assert.Equal(0.12, t[2].Z, 9);
            Assert.Equal(4.25, t[3].X, 9);                        // front extremity
            Assert.Equal(0.12 + 1.2 * Math.Tan(10 * Math.PI / 180), t[3].Z, 9);
            Assert.Equal(0.12 + 0.95 * Math.Tan(10 * Math.PI / 180), t[0].Z, 9);
            Assert.Equal(0.08, DesignVehicle.B85.UndersideTemplate(0.08)[1].Z, 9);
        }

        [Fact]
        public void FlatGroundGivesTheFullClearance()
        {
            GroundClearanceResult r = GroundClearanceChecker.CheckProfile(Profile((0, 10), (30, 10)), DesignVehicle.B99);
            Assert.True(r.Passes);
            Assert.Equal(0.12, r.MinGap, 6);
            Assert.Equal("between axles", r.Worst.Part);
            Assert.Empty(r.Groundings);
            Assert.NotNull(r.WorstForward);
            Assert.NotNull(r.WorstBackward);
            Assert.Contains("OK", r.Summary());
        }

        [Fact]
        public void CrestClearanceMatchesTheGeometry()
        {
            // Wheels either side of a symmetrical crest: the chord is level, the crest stands
            // (L/2) * grade above it, so the gap between the axles is h - (L/2) * grade.
            DesignVehicle v = DesignVehicle.B99;
            foreach (double grade in new[] { 0.05, 0.07, 0.09 })
            {
                GroundClearanceResult r = GroundClearanceChecker.CheckProfile(Crest(grade), v, step: 0.02);
                double expected = 0.12 - v.Wheelbase / 2 * grade;
                Assert.Equal(expected, r.MinGap, 2);
                Assert.Equal("between axles", r.Worst.Part);
                Assert.Equal(20, r.Worst.GapChainage, 1);
                Assert.Equal(expected >= 0, r.Passes);
            }

            // B5: an 8 degree grade change (7 % each side) is the limit for the B99's 120 mm.
            Assert.True(GroundClearanceChecker.CheckProfile(Crest(Math.Tan(4 * Math.PI / 180)), v).Passes);
            GroundClearanceResult fail = GroundClearanceChecker.CheckProfile(Crest(0.09), v);
            Assert.False(fail.Passes);
            Assert.Equal(2, fail.Groundings.Count); // once each way
            Assert.Contains("GROUNDS", fail.Summary());
        }

        [Fact]
        public void RampAgainstTheApproachAngle()
        {
            // The underside rises at 10 degrees (17.6 %) from 120 mm at the front axle, so with the
            // wheels still on the flat a ramp grounds the 1.2 m front overhang once it is steeper than
            // (0.12 + 1.2 tan 10) / 1.2 = 27.6 %.
            DesignVehicle v = DesignVehicle.B99;
            GroundClearanceResult gentle = GroundClearanceChecker.CheckProfile(Ramp(0.25), v);
            Assert.True(gentle.Passes);
            Assert.Equal(0.12 + 1.2 * (Math.Tan(10 * Math.PI / 180) - 0.25), gentle.WorstForward!.MinGap, 2);

            GroundClearanceResult steep = GroundClearanceChecker.CheckProfile(Ramp(0.35), v);
            Assert.False(steep.Passes);
            Assert.Contains(steep.Groundings, g => g.Part == "front overhang" && g.Forward);
            Assert.Contains(steep.Groundings, g => g.Part == "rear overhang" && !g.Forward); // reversing down the same ramp

            // Where the front overhang hangs over the ramp: the gap point is up the ramp.
            ClearancePosition w = steep.WorstForward!;
            Assert.True(w.GapChainage > 20 && w.GapChainage > w.FrontChainage);

            // A vehicle with more clearance (or a lower required clearance) passes.
            Assert.True(GroundClearanceChecker.CheckProfile(Ramp(0.35), v, groundClearance: 0.3).Passes);
        }

        [Fact]
        public void DipCatchesTheOverhangFromBothDirections()
        {
            // A gutter dip: 1 m wide, 100 mm deep, in otherwise flat ground.
            GroundProfile dip = Profile((0, 5), (10, 5), (10.5, 4.9), (11, 5), (21, 5));
            GroundClearanceResult r = GroundClearanceChecker.CheckProfile(dip, DesignVehicle.B85, step: 0.02);
            Assert.True(r.Passes); // a 100 mm dip under a 2.8 m wheelbase is fine
            Assert.True(r.MinGap < 0.12);
            Assert.True(r.MinGap > 0);
        }

        [Fact]
        public void BothDirectionsCanBeSwitchedOffAndPositionsAreOrdered()
        {
            GroundClearanceResult r = GroundClearanceChecker.CheckProfile(Crest(0.09), DesignVehicle.B99, bothDirections: false);
            Assert.Null(r.WorstBackward);
            Assert.All(r.Positions, p => Assert.True(p.Forward));
            Assert.All(r.Positions, p => Assert.True(p.FrontChainage > p.RearChainage));
            // Wheels are a wheelbase apart along the chord.
            foreach (ClearancePosition p in r.Positions.Take(50))
            {
                double chord = Math.Sqrt(Math.Pow(p.FrontChainage - p.RearChainage, 2) + Math.Pow(p.FrontLevel - p.RearLevel, 2));
                Assert.Equal(DesignVehicle.B99.Wheelbase, chord, 6);
            }

            Assert.Throws<ArgumentException>(() => GroundClearanceChecker.CheckProfile(Profile((0, 0), (2, 0)), DesignVehicle.B99));
        }

        [Fact]
        public void PlottedSectionIsReadWithItsExaggeration()
        {
            // A plot at VE 10: levels 100..101 drawn as Y 1000..1010, chainage 0..40 as X.
            var plot = new PolylineSpec { Points = new List<Point2> { new Point2(0, 1000), new Point2(20, 1010), new Point2(40, 1000) } };
            GroundProfile p = GroundClearanceChecker.ProfileFromPlot(Path2.From(plot)!, 10);
            Assert.Equal(0, p.Start, 9);
            Assert.Equal(40, p.End, 9);
            Assert.Equal(101, p.LevelAt(20), 9);
            Assert.Equal(100.5, p.LevelAt(10), 9);

            // Drawn right to left: still runs up chainage. A horizontal scale divides X.
            var reversed = new PolylineSpec { Points = new List<Point2> { new Point2(80, 0), new Point2(0, 10) } };
            GroundProfile q = GroundClearanceChecker.ProfileFromPlot(Path2.From(reversed)!, 1, horizontalScale: 2);
            Assert.Equal(0, q.Start, 9);
            Assert.Equal(40, q.End, 9);
            Assert.Equal(10, q.LevelAt(0), 9);

            var zigzag = new PolylineSpec { Points = new List<Point2> { new Point2(0, 0), new Point2(10, 0), new Point2(5, 1) } };
            Assert.Throws<ArgumentException>(() => GroundClearanceChecker.ProfileFromPlot(Path2.From(zigzag)!, 1));
        }

        [Fact]
        public void SectionDrawingUsesTheFrameAndLayers()
        {
            TurnPathConventions c = Conventions();
            GroundClearanceResult r = GroundClearanceChecker.CheckProfile(Crest(0.09), DesignVehicle.B99);
            var frame = SectionFrame.OfPlot(10, 1);
            List<DraftedEntity> drafted = GroundClearanceDrafter.DrawOnSection(r, frame, c.Layers, 0.3);
            // Two templates (one per direction), each failing: polyline + 2 wheels + tick, plus grounding ground lines and labels.
            Assert.True(drafted.Count(d => d.Group == "clearance_fail") >= 8);
            Assert.Contains(drafted, d => d.Group == "labels" && ((TextSpec)d.Spec).Text.Contains("GROUNDS"));
            Assert.All(drafted.Where(d => d.Group == "clearance_fail"), d => Assert.Equal(c.Layers.ClearanceFail, d.Spec.Layer));
            // The template sits on the exaggerated plot: its wheel at chainage ~18.5 is drawn at Y = level * 10.
            var undersides = drafted.Where(d => d.Spec is PolylineSpec p && !p.Closed && p.Points.Count == 4).Select(d => (PolylineSpec)d.Spec).ToList();
            Assert.Equal(2, undersides.Count);
            Assert.All(undersides, u => Assert.True(u.Points.All(p => p.Y > 10 && p.Y < 25)));

            GroundClearanceResult ok = GroundClearanceChecker.CheckProfile(Crest(0.03), DesignVehicle.B99);
            List<DraftedEntity> fine = GroundClearanceDrafter.DrawOnSection(ok, frame, c.Layers, 0.3);
            Assert.DoesNotContain(fine, d => d.Group == "clearance_fail");
            Assert.All(fine.Where(d => d.Group == "clearance_template"), d => Assert.Equal(c.Layers.ClearanceCheck, d.Spec.Layer));
        }

        // ---------------------------------------------------------------- 3D

        /// <summary>A surface: flat at z = 50 for x below 20, then a ramp at the grade; nothing west of x = -5.</summary>
        private static Func<IReadOnlyList<Point2>, IReadOnlyList<double?>> RampSurface(double grade) =>
            pts => pts.Select(p => p.X < -5 ? (double?)null : 50 + Math.Max(0, p.X - 20) * grade).ToList();

        private static TurnPathResult StraightDrive(DesignVehicle v, double from = 0, double to = 40) =>
            TurnPathSimulator.Simulate(new TurnPathRequest
            {
                Vehicle = v,
                Path = new Path2(new List<Segment> { new LineSegment(new Point2(from, 0), new Point2(to, 0)) }, false),
                Reference = v.FrontAxleCentre,
            });

        [Fact]
        public void SurfaceCheckFindsTheRampOnBothSides()
        {
            DesignVehicle v = DesignVehicle.B99;
            PathClearanceResult r = GroundClearanceChecker.CheckAlongPath(StraightDrive(v), RampSurface(0.35), 0.1);
            Assert.False(r.Passes);
            Assert.NotNull(r.Worst);
            Assert.Equal("front overhang", r.Worst!.Part);
            foreach (PathClearanceSide side in r.Sides)
            {
                Assert.NotNull(side.Worst);
                Assert.True(side.Worst!.MinGap < 0);
                // Grounds while the front overhang is over the ramp but the front wheels are still on the
                // flat; and again, less, when the rear wheels reach the foot and the rear overhang tilts into the flat.
                Assert.InRange(side.Groundings.Count, 1, 2);
                Assert.InRange(side.Groundings[0].From, 19, 21);
                Assert.InRange(side.Worst.GapPoint.X, 20, 21.5);
                Assert.NotNull(side.WheelPathProfile);
            }

            // Both sides see the same ramp: same gap.
            Assert.Equal(r.LeftSide.Worst!.MinGap, r.RightSide.Worst!.MinGap, 6);

            PathClearanceResult ok = GroundClearanceChecker.CheckAlongPath(StraightDrive(v), RampSurface(0.25), 0.1);
            Assert.True(ok.Passes);
            Assert.Contains("OK", ok.Summary());
        }

        [Fact]
        public void SurfaceCheckSkipsPositionsOffTheSurfaceAndSaysSo()
        {
            DesignVehicle v = DesignVehicle.B99;
            PathClearanceResult r = GroundClearanceChecker.CheckAlongPath(StraightDrive(v, -12, 10), RampSurface(0.1), 0.2);
            Assert.True(r.LeftSide.OffSurfaceCount > 0);
            Assert.Contains(r.Warnings, w => w.Contains("off the surface"));
            Assert.True(r.Passes);
        }

        [Fact]
        public void SurfacePlansAndPlotsAreDrawn()
        {
            TurnPathConventions c = Conventions();
            DesignVehicle v = DesignVehicle.B99;
            PathClearanceResult r = GroundClearanceChecker.CheckAlongPath(StraightDrive(v), RampSurface(0.35), 0.1);
            List<DraftedEntity> plan = GroundClearanceDrafter.DrawPlan(r, c.Layers, 0.3);
            Assert.True(plan.Count(d => d.Group == "clearance_fail" && d.Spec is CircleSpec) > 2);
            Assert.Contains(plan, d => d.Group == "labels" && ((TextSpec)d.Spec).Text.Contains("GROUNDS"));

            List<DraftedEntity> plots = GroundClearanceDrafter.DrawSectionPlots(r, new Point2(100, 100), 5, c.Layers, 0.3);
            Assert.Equal(2, plots.Count(d => d.Group == "clearance_section" && d.Spec is PolylineSpec)); // one ground line per side
            Assert.Equal(2, plots.Count(d => d.Group == "labels" && ((TextSpec)d.Spec).Text.Contains("wheel path")));
            Assert.True(plots.Count(d => d.Group == "clearance_fail") >= 2);
            Assert.All(plots.Select(d => d.Spec).OfType<PolylineSpec>(), p => Assert.All(p.Points, pt => Assert.True(pt.X >= 99 && pt.Y <= 100.5)));
        }

        // ---------------------------------------------------------------- tool

        private static JsonDocument Run(GroundClearanceTools tools, string json, out ToolOutcome outcome)
        {
            using JsonDocument input = JsonDocument.Parse(json);
            outcome = tools.Execute(GroundClearanceTools.CheckGroundClearance, input.RootElement);
            Assert.False(outcome.IsError, outcome.Content);
            return JsonDocument.Parse(outcome.Content);
        }

        [Fact]
        public void ToolChecksAPlottedSectionAndDrawsOnIt()
        {
            var host = new InMemoryDrawingHost("Meters");
            var plot = new PolylineSpec { Points = new List<Point2> { new Point2(0, 500), new Point2(20, 518), new Point2(40, 500) }, Layer = "LONG SECTION" };
            string handle = host.CreateEntities(new[] { plot })[0].Handle!;
            var tools = new GroundClearanceTools(host, Conventions());
            using JsonDocument doc = Run(tools, $"{{\"mode\":\"section\",\"handle\":\"{handle}\",\"vertical_exaggeration\":10}}", out ToolOutcome outcome);
            JsonElement root = doc.RootElement;
            Assert.False(root.GetProperty("passes").GetBoolean());
            Assert.Equal(Math.Round((0.12 - 3.05 / 2 * 0.09) * 1000), root.GetProperty("min_gap_mm").GetDouble(), 0);
            Assert.Equal("between axles", root.GetProperty("worst").GetProperty("part").GetString());
            Assert.True(root.GetProperty("groundings").GetArrayLength() == 2);
            Assert.True(root.GetProperty("drawn").GetProperty("clearance_fail").GetArrayLength() > 0);
            Assert.Contains(host.Entities, e => e.Layer == "AE CIV PLN - VEHICLE GROUND CLEARANCE FAIL");
            Assert.Contains("GROUNDS", outcome.Summary);
        }

        [Fact]
        public void ToolChecksPointsAndAGroundIdAndPlotsWhenAsked()
        {
            var host = new InMemoryDrawingHost("Meters");
            var tools = new GroundClearanceTools(host, Conventions());
            using JsonDocument doc = Run(tools, "{\"mode\":\"section\",\"vehicle\":\"B85\",\"points\":[[0,10],[20,10],[40,15]],\"both_directions\":false}", out _);
            Assert.True(doc.RootElement.GetProperty("passes").GetBoolean());
            Assert.Contains(doc.RootElement.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("plot_at"));
            Assert.Empty(host.Entities);

            string id = GroundProfileStore.Add(Profile((0, 10), (20, 10), (40, 16)));
            using JsonDocument doc2 = Run(tools, $"{{\"mode\":\"section\",\"ground_id\":\"{id}\",\"plot_at\":[200,300],\"vertical_exaggeration\":5}}", out _);
            Assert.False(doc2.RootElement.GetProperty("passes").GetBoolean());
            Assert.True(host.Entities.Count > 5);
            Assert.All(host.Entities.Select(e => e.Spec).OfType<PolylineSpec>(), p => Assert.All(p.Points, pt => Assert.True(pt.X >= 199 && pt.Y >= 299)));
        }

        [Fact]
        public void ToolExplainsWhenThereIsNoSurfaceAndRejectsBadInput()
        {
            var tools = new GroundClearanceTools(new InMemoryDrawingHost("Meters"), Conventions());
            foreach (string bad in new[]
            {
                "{\"mode\":\"surface\",\"points\":[[0,0],[10,0]]}",
                "{\"mode\":\"section\"}",
                "{\"mode\":\"section\",\"ground_id\":\"nope\"}",
                "{\"mode\":\"sideways\"}",
                "{\"mode\":\"section\",\"points\":[[0,0],[1,0]]}",
            })
            {
                using JsonDocument input = JsonDocument.Parse(bad);
                ToolOutcome outcome = tools.Execute(GroundClearanceTools.CheckGroundClearance, input.RootElement);
                Assert.True(outcome.IsError, bad);
            }
        }

        [Fact]
        public void ConventionsCarryTheClearanceLayersAndSettings()
        {
            TurnPathConventions c = Conventions();
            Assert.Equal(0.12, c.Vehicle("B99").GroundClearance);
            Assert.Equal(10, c.Vehicle("B85").ApproachAngleDeg);
            Assert.Equal(0.05, c.ClearanceStep);
            Assert.Contains(c.Layers.All(), kv => kv.Key == "clearance_fail");
            Assert.True(c.LayerStyles.ContainsKey(c.Layers.ClearanceCheck));
            Assert.Equal("CC-B85", new TurnPathLayers { ClearanceCheck = "CC-{vehicle}" }.Resolve("B85").ClearanceCheck);
        }
    }
}
