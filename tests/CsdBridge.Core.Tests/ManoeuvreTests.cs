using System;
using System.Collections.Generic;
using System.IO;
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
    /// <summary>Arriving at a heading, conflict detection and saved manoeuvres.</summary>
    public class ManoeuvreTests
    {
        private static TurnPathConventions Conventions() =>
            TurnPathConventions.Load(Path.Combine(AppContext.BaseDirectory, "turn-paths.json"));

        private static double Dist(Point2 a, Point2 b) => Segment.Dist(a, b);

        private static double AngleDiff(double a, double b) => Math.Abs(Math.Atan2(Math.Sin(a - b), Math.Cos(a - b)));

        // ------------------------------------------------------ arrive at a heading

        [Fact]
        public void DriveToPoseArrivesAtThePointFacingTheHeading()
        {
            DesignVehicle v = DesignVehicle.B99;
            var start = new VehiclePose(new Point2(0, 0), 0);
            var target = new Point2(20, 12);
            TurnPathBuilder.DriveLeg leg = TurnPathBuilder.DriveToPose(v, start, target, Math.PI / 2)!;
            Assert.False(leg.Reverse);
            Assert.Equal(3, leg.Request.Path.Segments.Count); // turn, straight, turn
            Assert.IsType<ArcSegment>(leg.Request.Path.Segments[0]);
            Assert.IsType<LineSegment>(leg.Request.Path.Segments[1]);
            Assert.IsType<ArcSegment>(leg.Request.Path.Segments[2]);
            Assert.Equal(v.RearAxleRadius(v.MinTurnRadius), ((ArcSegment)leg.Request.Path.Segments[0]).Radius, 9);
            Assert.Equal(3, leg.Moves.Count);

            TurnPathResult r = TurnPathSimulator.Simulate(leg.Request);
            VehiclePose end = r.Poses[r.Poses.Count - 1];
            Assert.True(AngleDiff(end.Heading, Math.PI / 2) < 1e-6);
            Assert.Equal(0, Dist(end.ToWorld(v.FrontAxleCentre), target), 1e-3); // forwards, the front axle arrives at the point
            Assert.True(r.WithinMinimumRadius);
            Assert.InRange(r.TightestRadius, v.MinTurnRadius - 0.001, v.MinTurnRadius + 0.01);
        }

        [Fact]
        public void DriveToPoseReversesIntoABay()
        {
            DesignVehicle v = DesignVehicle.B99;
            // Facing east in an aisle; reverse into a bay to the south, ending facing north.
            var start = new VehiclePose(new Point2(0, 0), 0);
            var bay = new Point2(-7, -7);
            TurnPathBuilder.DriveLeg leg = TurnPathBuilder.DriveToPose(v, start, bay, Math.PI / 2)!;
            Assert.True(leg.Reverse);
            TurnPathResult r = TurnPathSimulator.Simulate(leg.Request);
            VehiclePose end = r.Poses[r.Poses.Count - 1];
            Assert.True(AngleDiff(end.Heading, Math.PI / 2) < 1e-6);
            Assert.Equal(0, Dist(end.RearAxle, bay), 1e-3); // in reverse, the rear axle arrives at the point
            Assert.True(r.Reverse);
            Assert.True(leg.ArcDeg <= TurnPathBuilder.MaxLegTurnDeg + 1e-6);
        }

        [Fact]
        public void DriveToPoseRefusesLoopsAndHonoursAForcedDirection()
        {
            DesignVehicle v = DesignVehicle.B99;
            var start = new VehiclePose(new Point2(0, 0), 0);
            // Three metres sideways, same heading: no turn-straight-turn without a loop.
            Assert.Null(TurnPathBuilder.DriveToPose(v, start, new Point2(v.Wheelbase + 0.5, 3), 0));
            // Straight ahead, same heading: just a line.
            TurnPathBuilder.DriveLeg ahead = TurnPathBuilder.DriveToPose(v, start, new Point2(15, 0), 0)!;
            Assert.Single(ahead.Request.Path.Segments);
            Assert.False(ahead.Reverse);
            // A lane change: left then right, no loop.
            TurnPathBuilder.DriveLeg lane = TurnPathBuilder.DriveToPose(v, start, new Point2(25, 3.5), 0)!;
            Assert.Equal(3, lane.Request.Path.Segments.Count);
            Assert.True(lane.ArcDeg < 60);
            // Forced reverse to a point behind, and the radius limit.
            Assert.True(TurnPathBuilder.DriveToPose(v, start, new Point2(-10, 0), 0, reverse: true)!.Reverse);
            Assert.Throws<ArgumentException>(() => TurnPathBuilder.DriveToPose(v, start, new Point2(20, 10), 1, 4));
        }

        // ------------------------------------------------------------- conflicts

        private static TurnPathResult StraightEast(DesignVehicle v) =>
            TurnPathSimulator.Simulate(new TurnPathRequest
            {
                Vehicle = v,
                Path = new Path2(new List<Segment> { new LineSegment(new Point2(v.Wheelbase, 0), new Point2(20, 0)) }, false),
                Reference = v.FrontAxleCentre,
            });

        private static Obstruction Line(string layer, double x1, double y1, double x2, double y2) =>
            new Obstruction("H" + layer, layer, new Path2(new List<Segment> { new LineSegment(new Point2(x1, y1), new Point2(x2, y2)) }, false));

        [Fact]
        public void LayerPatternsMatchWildcardsAndXrefLayers()
        {
            Assert.True(LayerPattern.Matches("KERB", "kerb"));
            Assert.True(LayerPattern.Matches("AE CIV PLN - KERB*", "AE CIV PLN - KERB NEW"));
            Assert.False(LayerPattern.Matches("KERB", "KERB NEW"));
            Assert.True(LayerPattern.Matches("A-WALL*", "arch|A-WALL-EXT"));
            Assert.True(LayerPattern.Matches("arch|*", "arch|A-WALL-EXT"));
            Assert.False(LayerPattern.Matches("struct|*", "arch|A-WALL-EXT"));
            Assert.True(LayerPattern.Matches("COL?", "COL1"));
            Assert.Equal(new[] { "A", "B*", "C D" }, LayerPattern.Parse(" A, B* ; C D ,"));
        }

        [Fact]
        public void ConflictsDistinguishHitsEncroachmentsAndClearMisses()
        {
            DesignVehicle v = DesignVehicle.B99; // 1.94 wide: sides at +-0.97; a straight has 300 mm clearance each side
            TurnPathResult r = StraightEast(v);
            var legs = new[] { r };

            // Well clear: 1.5 m off the centreline.
            ConflictReport clear = ConflictChecker.Check(legs, new[] { Line("KERB", 2, 1.5, 18, 1.5) });
            Assert.True(clear.Passes);
            Assert.Equal(1, clear.ObstructionsChecked);
            Assert.Equal(0.53, clear.ClosestGap!.Value, 2);
            Assert.Contains("No conflicts", clear.Summary());

            // Inside the clearance: 130 mm from the body.
            ConflictReport enc = ConflictChecker.Check(legs, new[] { Line("KERB", 2, 1.1, 18, 1.1) });
            Conflict e = Assert.Single(enc.Conflicts);
            Assert.False(e.BodyHit);
            Assert.Equal(0.13, e.Gap, 2);
            Assert.Equal(0.3, e.Required, 6);
            Assert.Equal(0.17, e.Shortfall, 2);
            Assert.False(enc.BodyHits);
            Assert.Contains("CONFLICT", enc.Summary());
            Assert.True(e.Stretch.Count >= 2);

            // Inside the swept path: 170 mm in from the side.
            ConflictReport hit = ConflictChecker.Check(legs, new[] { Line("WALL", 2, 0.8, 18, 0.8) });
            Conflict h = Assert.Single(hit.Conflicts);
            Assert.True(h.BodyHit);
            Assert.Equal(0.17, h.Depth, 2);
            Assert.True(hit.BodyHits);

            // A wall across the path: one conflict, a hit, with clearance stretches either side merged into it.
            ConflictReport across = ConflictChecker.Check(legs, new[] { Line("WALL", 10, -5, 10, 5) });
            Conflict a = Assert.Single(across.Conflicts);
            Assert.True(a.BodyHit);
            Assert.InRange(a.Depth, 0.9, 0.98);
            Assert.InRange(a.Stretch.Last().Y - a.Stretch.First().Y, 2.4, 2.6); // 1.94 + 2 x 0.3

            // A column just outside the body: an encroachment of the clearance.
            var column = new Obstruction("C1", "COLS", Path2.From(new CircleSpec { Center = new Point2(10, 1.2), Radius = 0.2 })!);
            Conflict col = Assert.Single(ConflictChecker.Check(legs, new[] { column }).Conflicts);
            Assert.False(col.BodyHit);
            Assert.Equal(0.03, col.Gap, 2);

            // Far away: not even counted.
            Assert.Equal(0, ConflictChecker.Check(legs, new[] { Line("KERB", 2, 30, 18, 30) }).ObstructionsChecked);
        }

        [Fact]
        public void ConflictMarkersGoOnTheConflictLayer()
        {
            TurnPathResult r = StraightEast(DesignVehicle.B99);
            ConflictReport report = ConflictChecker.Check(new[] { r }, new[] { Line("WALL", 10, -5, 10, 5), Line("KERB", 2, 1.1, 18, 1.1) });
            List<DraftedEntity> drawn = ConflictChecker.Draw(report, "CONFLICTS", 0.3);
            Assert.Equal(2, drawn.Count(d => d.Spec is CircleSpec));
            Assert.Equal(2, drawn.Count(d => d.Spec is PolylineSpec));
            Assert.Contains(drawn, d => d.Spec is TextSpec t && t.Text.StartsWith("HIT"));
            Assert.Contains(drawn, d => d.Spec is TextSpec t && t.Text.Contains("needs 300"));
            Assert.All(drawn, d => Assert.Equal("CONFLICTS", d.Spec.Layer));
            var (min, max) = ConflictChecker.Window(new[] { r });
            Assert.True(min.X < -2 && max.X > 21 && min.Y < -2.5 && max.Y > 2.5);
        }

        // ------------------------------------------------------- saved manoeuvres

        private static ManoeuvreRecord Drive() => new ManoeuvreRecord
        {
            Id = "abc",
            Vehicle = "B99",
            Mode = "drive",
            Start = new[] { 0.0, 0.0 },
            HeadingDeg = 0,
            Radius = 6.3,
            Stops = new List<DriveStop>
            {
                new DriveStop { Point = new[] { 12.0, 5.0 } },
                new DriveStop { Point = new[] { 6.0, -4.0 } },
                new DriveStop { Point = new[] { 20.0, 14.0 }, HeadingDeg = 90 },
            },
            ClearanceInside = 0.3,
            ClearanceOutside = 0.6,
        };

        [Fact]
        public void RecordRoundTripsThroughJsonAndMovesWithItsBlock()
        {
            ManoeuvreRecord r = Drive();
            r.Layers.SweptPath = "X-{vehicle}";
            r.Draw.Block = true;
            r.ObstructionLayers.Add("KERB*");
            r.Handles.AddRange(new[] { "1A", "1B" });
            ManoeuvreRecord back = ManoeuvreRecord.FromJson(r.ToJson());
            Assert.Equal("drive", back.Mode);
            Assert.Equal(3, back.Stops.Count);
            Assert.Equal(90, back.Stops[2].HeadingDeg);
            Assert.Null(back.Stops[0].HeadingDeg);
            Assert.Equal("X-{vehicle}", back.Layers.SweptPath);
            Assert.True(back.Draw.Block);
            Assert.Equal(new[] { "1A", "1B" }, back.Handles);
            Assert.Equal("KERB*", back.ObstructionLayers.Single());

            // Only what was set is saved: nothing worked out from it, and a path with no start point
            // (one that follows a line) saves too.
            Assert.DoesNotContain("start_point", r.ToJson());
            Assert.DoesNotContain("turns_kind", r.ToJson());
            var follow = new ManoeuvreRecord { Id = "f", Mode = "follow", FollowHandle = "2C", FollowNear = new[] { 1.0, 2 }, Reverse = true };
            ManoeuvreRecord followed = ManoeuvreRecord.FromJson(follow.ToJson());
            Assert.Equal("2C", followed.FollowHandle);
            Assert.True(followed.Reverse);
            Assert.Null(followed.Start);

            // The block was rotated 90 degrees about the start and moved 100 east.
            back.Transform(new Point2(0, 0), Math.PI / 2, 100, 0);
            Assert.Equal(100, back.Start![0], 9);
            Assert.Equal(90, back.HeadingDeg, 9);
            Assert.Equal(95, back.Stops[0].Point[0], 9);  // (12, 5) -> (-5, 12) -> (95, 12)
            Assert.Equal(12, back.Stops[0].Point[1], 9);
            Assert.Equal(180, back.Stops[2].HeadingDeg!.Value, 9);
        }

        [Fact]
        public void PlannerBuildsEveryModeAndRegeneratesWithNewSettings()
        {
            TurnPathConventions c = Conventions();

            ManoeuvrePlan drive = ManoeuvrePlanner.Plan(Drive(), c);
            Assert.Equal(3, drive.Legs.Count);
            Assert.True(drive.Legs[0].First);
            Assert.False(drive.Legs[1].First);
            Assert.True(drive.Legs[1].Result.Reverse);
            Assert.Contains("reverse", drive.Legs[1].Description);
            Assert.True(AngleDiff(drive.EndPose.Heading, Math.PI / 2) < 1e-6);
            Assert.Equal(0, Dist(drive.StartPoint, new Point2(0, 0)), 1e-9);
            // The clearances come from the record: B99 turning leg 300 / 600.
            Assert.Equal(0.6, Math.Max(drive.Legs[0].Result.ClearanceLeft, drive.Legs[0].Result.ClearanceRight));

            // The same drive for the smaller car still works; entities follow the record's style.
            ManoeuvreRecord r = Drive();
            r.Vehicle = "B85";
            r.Radius = 5.8;
            r.Layers.SweptPath = "SWEPT {vehicle}";
            ManoeuvrePlan small = ManoeuvrePlanner.Plan(r, c);
            Assert.Equal("B85", small.Vehicle.Name);
            List<DraftedEntity> drafted = ManoeuvrePlanner.Draft(small, r, c);
            Assert.Contains(drafted, d => d.Group == "swept_path" && d.Spec.Layer == "SWEPT B85");
            Assert.Single(drafted, d => d.Group == "labels");
            Assert.Equal(4 * 7, drafted.Count(d => d.Group == "vehicle")); // start + three leg ends

            // A destination the car can't reach is named, and nothing is planned.
            r.Stops[0].Point = new[] { 5.0, 3.0 };
            ArgumentException ex = Assert.Throws<ArgumentException>(() => ManoeuvrePlanner.Plan(r, c));
            Assert.Contains("destination 1", ex.Message);

            var template = new ManoeuvreRecord { Mode = "template", Start = new[] { 5.0, 5.0 }, HeadingDeg = 90, Radius = 6.3, TurnAngles = new List<double> { 45, 90 }, Left = false };
            ManoeuvrePlan t = ManoeuvrePlanner.Plan(template, c);
            Assert.Equal(2, t.Legs.Count);
            Assert.All(t.Legs, l => Assert.True(l.First));
            Assert.Equal("B99 R6.3 m", t.Legs[0].Label);

            var follow = new ManoeuvreRecord { Mode = "follow", FollowHandle = "AA", FollowNear = new[] { 29.0, 0.0 }, Reference = "front_axle" };
            ManoeuvrePlan f = ManoeuvrePlanner.Plan(follow, c, h => h == "AA" ? new LineSpec { Start = new Point2(0, 0), End = new Point2(30, 0) } : null);
            Assert.Equal(30, f.Legs.Single().Result.Path.Start.X, 9); // run from the end nearest the pick
            Assert.Throws<ArgumentException>(() => ManoeuvrePlanner.Plan(follow, c, h => null));

            Assert.Contains("SWEPT B99", ManoeuvrePlanner.OwnLayers(r, c));
            Assert.Contains(c.Layers.Conflict, ManoeuvrePlanner.OwnLayers(r, c));
        }

        // ------------------------------------------------------------------ tool

        [Fact]
        public void ToolReportsAndMarksConflictsAndDrivesToAHeading()
        {
            var host = new InMemoryDrawingHost("Meters");
            host.CreateEntities(new EntitySpec[]
            {
                new LineSpec { Start = new Point2(10, -5), End = new Point2(10, 5), Layer = "arch|A-WALL" },
                new LineSpec { Start = new Point2(0, 8), End = new Point2(30, 8), Layer = "KERB" },
            });
            var tools = new TurnPathTools(host, Conventions());
            using JsonDocument input = JsonDocument.Parse(
                "{\"mode\":\"points\",\"points\":[[0,0],[25,0]],\"heading_deg\":0,\"arrival_headings_deg\":[0],\"obstruction_layers\":[\"A-WALL\",\"KERB\"]}");
            ToolOutcome outcome = tools.Execute(TurnPathTools.DrawTurnPath, input.RootElement);
            Assert.False(outcome.IsError, outcome.Content);
            using JsonDocument doc = JsonDocument.Parse(outcome.Content);
            JsonElement conflicts = doc.RootElement.GetProperty("conflicts");
            Assert.False(conflicts.GetProperty("passes").GetBoolean());
            JsonElement item = Assert.Single(conflicts.GetProperty("items").EnumerateArray());
            Assert.Equal("body_hit", item.GetProperty("kind").GetString());
            Assert.Equal("arch|A-WALL", item.GetProperty("layer").GetString());
            Assert.True(doc.RootElement.GetProperty("drawn").GetProperty("conflict").GetArrayLength() >= 2);
            Assert.Contains(host.Entities, e => e.Layer == "AE CIV PLN - VEHICLE CONFLICT");

            // The tool's own swept path layer is never an obstruction, even with a catch-all pattern.
            using JsonDocument again = JsonDocument.Parse("{\"mode\":\"points\",\"points\":[[0,20],[25,20]],\"heading_deg\":0,\"obstruction_layers\":[\"*\"],\"dry_run\":true}");
            ToolOutcome second = tools.Execute(TurnPathTools.DrawTurnPath, again.RootElement);
            using JsonDocument doc2 = JsonDocument.Parse(second.Content);
            Assert.True(doc2.RootElement.GetProperty("conflicts").GetProperty("passes").GetBoolean());
        }
    }
}
