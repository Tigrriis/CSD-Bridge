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
using Xunit.Abstractions;

namespace CsdBridge.Core.Tests
{
    /// <summary>The multi-point turn solver.</summary>
    public class SolverTests
    {
        private readonly ITestOutputHelper _out;

        public SolverTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static Obstruction Wall(double x1, double y1, double x2, double y2) =>
            new Obstruction("W", "WALL", new Path2(new List<Segment> { new LineSegment(new Point2(x1, y1), new Point2(x2, y2)) }, false));

        private static double AngleDiff(double a, double b) => Math.Abs(Math.Atan2(Math.Sin(a - b), Math.Cos(a - b)));

        /// <summary>Simulates the solved legs and checks they join up, end at the target and hit nothing.</summary>
        private void Verify(SolveRequest q, SolveResult r)
        {
            _out.WriteLine($"{r.Summary()}  [{r.Expansions:N0} positions, {r.Seconds:0.00} s]");
            Assert.True(r.Solved, r.Failure);
            var results = new List<TurnPathResult>();
            VehiclePose at = q.Start;
            foreach (SolvedLeg leg in r.Legs)
            {
                TurnPathRequest req = leg.ToRequest(q.Vehicle);
                req.ClearanceInside = req.ClearanceOutside = r.ClearanceUsed;
                TurnPathResult sim = TurnPathSimulator.Simulate(req);
                Assert.Equal(0, Segment.Dist(sim.Poses[0].RearAxle, at.RearAxle), 3);
                Assert.True(AngleDiff(sim.Poses[0].Heading, at.Heading) < 1e-3, "legs don't join on the heading");
                Assert.Equal(leg.Reverse, sim.Reverse);
                Assert.True(sim.WithinMinimumRadius);
                at = sim.Poses[sim.Poses.Count - 1];
                results.Add(sim);
            }

            Assert.Equal(0, Segment.Dist(at.RearAxle, q.Goal.RearAxle), 3);
            Assert.True(AngleDiff(at.Heading, q.Goal.Heading) < 1e-3);
            // Legs alternate direction.
            for (int i = 1; i < r.Legs.Count; i++)
            {
                Assert.NotEqual(r.Legs[i - 1].Reverse, r.Legs[i].Reverse);
            }

            // Nothing is hit, and the clearance is kept, except to what was already closer than that at the start or target.
            ConflictReport conflicts = ConflictChecker.Check(results, q.Obstructions);
            Assert.False(conflicts.BodyHits, conflicts.Summary());
            if (!r.TightAtEnds)
            {
                Assert.True(conflicts.Passes, conflicts.Summary());
            }
            else
            {
                double least = Math.Min(r.StartClearance, r.GoalClearance);
                Assert.All(conflicts.Conflicts, c => Assert.True(c.Gap >= least - 0.01, c.Describe()));
            }
        }

        [Fact]
        public void OpenSpaceIsOneMovement()
        {
            var q = new SolveRequest
            {
                Start = new VehiclePose(new Point2(0, 0), 0),
                Goal = new VehiclePose(new Point2(20, 8), Math.PI / 2),
                Min = new Point2(-12, -12),
                Max = new Point2(32, 20),
            };
            SolveResult r = ManoeuvreSolver.SolveWithFallback(q);
            Verify(q, r);
            Assert.Equal(1, r.Movements);
            Assert.False(r.Legs[0].Reverse);
            Assert.False(r.ClearanceReduced);
        }

        [Fact]
        public void TurningRoundInAStreetTakesThreeMovements()
        {
            // A street 9 m wide between kerbs: the B99's turning circle is 12.5 m, so it can't U-turn.
            var q = new SolveRequest
            {
                Start = new VehiclePose(new Point2(0, -2), 0),
                Goal = new VehiclePose(new Point2(0, 2), Math.PI),
                Obstructions = new[] { Wall(-30, -4.5, 30, -4.5), Wall(-30, 4.5, 30, 4.5) },
                Min = new Point2(-14, -6),
                Max = new Point2(14, 6),
            };
            SolveResult r = ManoeuvreSolver.SolveWithFallback(q);
            Verify(q, r);
            Assert.Equal(3, r.Movements);
            Assert.False(r.ClearanceReduced);
            Assert.Contains("3 movements", r.Summary());
        }

        [Fact]
        public void AWideStreetIsAUTurn()
        {
            var q = new SolveRequest
            {
                Start = new VehiclePose(new Point2(0, -6), 0),
                Goal = new VehiclePose(new Point2(0, 5), Math.PI),
                Obstructions = new[] { Wall(-40, -8.5, 40, -8.5), Wall(-40, 8.5, 40, 8.5) },
                Min = new Point2(-16, -10),
                Max = new Point2(16, 10),
            };
            SolveResult r = ManoeuvreSolver.SolveWithFallback(q);
            Verify(q, r);
            Assert.Equal(1, r.Movements);
        }

        [Fact]
        public void ReversesIntoABayOffAnAisle()
        {
            // An aisle 6.2 m wide running east-west (y from 0 to 6.2), a bay 2.6 m wide and 5.4 m deep to the south.
            var walls = new[]
            {
                Wall(-20, 6.2, 20, 6.2),                       // far side of the aisle
                Wall(-20, 0, -1.3, 0), Wall(1.3, 0, 20, 0),    // near side, with the bay opening
                Wall(-1.3, 0, -1.3, -5.4), Wall(1.3, 0, 1.3, -5.4), Wall(-1.3, -5.4, 1.3, -5.4),
            };
            DesignVehicle v = DesignVehicle.B85;
            var q = new SolveRequest
            {
                Vehicle = v,
                Start = new VehiclePose(new Point2(-10, 3.5), 0),                // driving east along the aisle
                Goal = new VehiclePose(new Point2(0, -5.4 + 0.4 + v.RearOverhang), Math.PI / 2),   // backed in, nose out
                Obstructions = walls,
                Min = new Point2(-16, -6),
                Max = new Point2(16, 7),
            };
            // Keeping 300 mm to the sides of the bay (365 mm away once parked) leaves next to no play:
            // it takes some shuffling, but it can be done, and the last movement is the reverse into the bay.
            SolveResult r = ManoeuvreSolver.SolveWithFallback(q);
            Verify(q, r);
            Assert.InRange(r.Movements, 2, 6);
            Assert.True(r.Legs[r.Legs.Count - 1].Reverse, "the last movement should be the reverse into the bay");
            Assert.False(r.ClearanceReduced);
            Assert.False(r.TightAtEnds);

            // With 150 mm it is the usual two movements: forward past the bay, and reverse in.
            q.Clearance = 0.15;
            r = ManoeuvreSolver.SolveWithFallback(q);
            Verify(q, r);
            Assert.Equal(2, r.Movements);
            Assert.False(r.Legs[0].Reverse);
            Assert.True(r.Legs[1].Reverse);
            Assert.False(r.ClearanceReduced);
        }

        [Fact]
        public void ANarrowBayKeepsTheClearanceEverywhereElse()
        {
            // The same aisle with a bay 2.4 m wide: 265 mm each side of a B85, less than the 300 mm asked for.
            var walls = new[]
            {
                Wall(-20, 6.2, 20, 6.2),
                Wall(-20, 0, -1.2, 0), Wall(1.2, 0, 20, 0),
                Wall(-1.2, 0, -1.2, -5.4), Wall(1.2, 0, 1.2, -5.4), Wall(-1.2, -5.4, 1.2, -5.4),
            };
            DesignVehicle v = DesignVehicle.B85;
            var q = new SolveRequest
            {
                Vehicle = v,
                Start = new VehiclePose(new Point2(-10, 3.5), 0),
                Goal = new VehiclePose(new Point2(0, -5.4 + 0.4 + v.RearOverhang), Math.PI / 2),
                Obstructions = walls,
                Min = new Point2(-16, -6),
                Max = new Point2(16, 7),
            };
            SolveResult r = ManoeuvreSolver.SolveWithFallback(q);
            Verify(q, r);
            Assert.False(r.ClearanceReduced);          // 300 mm is still kept to the aisle
            Assert.True(r.TightAtEnds);
            Assert.Equal(0.3, r.StartClearance, 3);
            Assert.Equal(0.15, r.GoalClearance, 3);    // passing closer to the sides of the bay saves movements
            Assert.Contains("except to what is already closer than that at the target, which keeps 150 mm", r.Summary());
            Assert.Equal(2, r.Movements);
            Assert.True(r.Legs[1].Reverse);

            // Driving out of the same bay, nose first: one movement, the sides of the bay keeping what they have.
            var leave = new SolveRequest
            {
                Vehicle = v,
                Start = q.Goal,
                Goal = new VehiclePose(new Point2(10, 3), 0),
                Obstructions = walls,
                Min = q.Min,
                Max = q.Max,
            };
            SolveResult left = ManoeuvreSolver.SolveWithFallback(leave);
            Verify(leave, left);
            Assert.Equal(1, left.Movements);
            Assert.True(left.TightAtEnds);
            Assert.Contains("at the start", left.Summary());

            // Asked to keep the clearance strictly, the same bay can't be solved.
            SolveResult strict = ManoeuvreSolver.Solve(q, 0.3);
            Assert.False(strict.Solved);
            Assert.Contains("target", strict.Failure);
        }

        [Fact]
        public void ReportsWhyThereIsNoSolution()
        {
            // The target is boxed in by walls closer than the car is long.
            var box = new[] { Wall(8, -1.2, 8, 1.2), Wall(14.5, -1.2, 14.5, 1.2), Wall(8, -1.2, 14.5, -1.2), Wall(8, 1.2, 14.5, 1.2) };
            var q = new SolveRequest
            {
                Start = new VehiclePose(new Point2(0, 0), 0),
                Goal = new VehiclePose(new Point2(10, 0), 0),
                Obstructions = box,
                Min = new Point2(-10, -10),
                Max = new Point2(25, 10),
                TimeLimitSeconds = 4,
                MaxExpansions = 30000,
            };
            SolveResult r = ManoeuvreSolver.SolveWithFallback(q);
            _out.WriteLine(r.Summary());
            Assert.False(r.Solved);
            Assert.NotNull(r.Failure);

            // A start that overlaps a wall says so.
            q.Obstructions = new[] { Wall(1, -3, 1, 3) };
            SolveResult blocked = ManoeuvreSolver.SolveWithFallback(q);
            Assert.False(blocked.Solved);
            Assert.Contains("start", blocked.Failure);

            Assert.Throws<ArgumentException>(() => ManoeuvreSolver.Solve(new SolveRequest { Radius = 3, Min = new Point2(-5, -5), Max = new Point2(5, 5) }, 0.3));
        }

        [Fact]
        public void ReducesTheClearanceWhenTheSpaceIsTooTightForIt()
        {
            // A gap 2.4 m wide half way along: 230 mm each side of a B99, less than the 300 mm asked for,
            // and nowhere near the start or the target.
            var walls = new[]
            {
                Wall(-8, 10, -1.2, 10), Wall(1.2, 10, 8, 10),
                Wall(-8, -6, -8, 26), Wall(8, -6, 8, 26), Wall(-8, -6, 8, -6), Wall(-8, 26, 8, 26),
            };
            var q = new SolveRequest
            {
                Start = new VehiclePose(new Point2(0, 0), Math.PI / 2),
                Goal = new VehiclePose(new Point2(0, 20), Math.PI / 2),
                Obstructions = walls,
                Min = new Point2(-8, -6),
                Max = new Point2(8, 26),
            };
            SolveResult r = ManoeuvreSolver.SolveWithFallback(q);
            Verify(q, r);
            Assert.True(r.ClearanceReduced);
            Assert.Equal(0.15, r.ClearanceUsed, 9);
            Assert.Equal(0.3, r.ClearanceAsked, 9);
            Assert.Equal(1, r.Movements);
            Assert.Contains("reduced from 300 to 150 mm", r.Summary());
        }

        // ------------------------------------------------------------------ saved with the drawing

        private static ManoeuvreRecord StreetRecord() => new ManoeuvreRecord
        {
            Id = "abc",
            Vehicle = "B99",
            Mode = "solve",
            Start = new[] { 0.0, -2 },
            HeadingDeg = 0,
            Target = new[] { 0.0, 2 },
            TargetHeadingDeg = 180,
            Radius = 6.3,
            ClearanceInside = 0.3,
            ClearanceOutside = 0.3,
            ObstructionLayers = new List<string> { "KERB" },
        };

        private static readonly Obstruction[] Street = { Wall(-30, -4.5, 30, -4.5), Wall(-30, 4.5, 30, 4.5) };

        [Fact]
        public void ASolvedManoeuvreIsKeptWithItsRecordAndPlannedFromIt()
        {
            TurnPathConventions c = TurnPathConventions.Default();
            ManoeuvreRecord r = StreetRecord();
            Assert.True(r.NeedsSolving);
            Assert.Throws<ArgumentException>(() => ManoeuvrePlanner.Plan(r, c));

            Point2 askedMin = default, askedMax = default;
            SolveResult solved = ManoeuvrePlanner.Solve(r, c, (min, max) =>
            {
                askedMin = min;
                askedMax = max;
                return Street;
            });
            Assert.True(solved.Solved, solved.Failure);
            Assert.Equal(3, solved.Movements);
            Assert.Equal(2, solved.ObstructionsUsed);
            Assert.True(askedMin.X < -15 && askedMax.X > 15, "the obstructions are fetched for the search area and a vehicle length beyond it");
            Assert.False(r.NeedsSolving);
            Assert.Contains("3 movements", r.SolvedSummary);

            // It survives being saved, and plans to the same three movements, joined end to end.
            ManoeuvreRecord back = ManoeuvreRecord.FromJson(r.ToJson());
            Assert.False(back.NeedsSolving);
            ManoeuvrePlan plan = ManoeuvrePlanner.Plan(back, c);
            Assert.Equal(3, plan.Legs.Count);
            Assert.Equal("B99, 3 movements", plan.Legs[0].Label);
            Assert.True(plan.Legs[0].First);
            Assert.False(plan.Legs[1].First);
            Assert.StartsWith("forward ", plan.Legs[0].Description);
            Assert.StartsWith("reverse ", plan.Legs[1].Description);
            for (int i = 1; i < plan.Legs.Count; i++)
            {
                TurnPathResult before = plan.Legs[i - 1].Result, after = plan.Legs[i].Result;
                Assert.True(Segment.Dist(before.Poses[before.Poses.Count - 1].RearAxle, after.Poses[0].RearAxle) < 1e-3);
            }

            Assert.True(Segment.Dist(plan.EndPose.RearAxle, new Point2(0, 2)) < 1e-3);
            Assert.True(AngleDiff(plan.EndPose.Heading, Math.PI) < 1e-3);
            Assert.True(ConflictChecker.Check(plan.Results, Street).Passes);
            Assert.NotEmpty(ManoeuvrePlanner.Draft(plan, back, c));

            // Changing what it was solved for means solving again: the clearance, the target, the layers...
            back.ClearanceInside = back.ClearanceOutside = 0.2;
            Assert.True(back.NeedsSolving);
            back.ClearanceInside = back.ClearanceOutside = 0.3;
            Assert.False(back.NeedsSolving);
            back.ObstructionLayers.Add("A-WALL");
            Assert.True(back.NeedsSolving);

            // ...and so does moving the block it was drawn into: the kerbs didn't move with it.
            ManoeuvreRecord moved = ManoeuvreRecord.FromJson(r.ToJson());
            moved.Transform(new Point2(0, -2), Math.PI / 2, 10, 0);
            Assert.Null(moved.Solved);
            Assert.True(moved.NeedsSolving);
            Assert.Equal(6.0, moved.Target![0], 6);
            Assert.Equal(-2.0, moved.Target[1], 6);
            Assert.Equal(270, moved.TargetHeadingDeg, 6);

            // A manoeuvre with no solution leaves the record as it was.
            ManoeuvreRecord stuck = StreetRecord();
            stuck.Target = new[] { 0.0, 4.2 };
            SolveResult none = ManoeuvrePlanner.Solve(stuck, c, (min, max) => Street);
            Assert.False(none.Solved);
            Assert.True(stuck.NeedsSolving);
            Assert.Null(stuck.Solved);
        }

        [Fact]
        public void TheToolSolvesBetweenTheObstructionLayers()
        {
            var host = new InMemoryDrawingHost("Meters");
            host.CreateEntities(new EntitySpec[]
            {
                new LineSpec { Start = new Point2(-30, -4.5), End = new Point2(30, -4.5), Layer = "KERB" },
                new LineSpec { Start = new Point2(-30, 4.5), End = new Point2(30, 4.5), Layer = "KERB" },
                new LineSpec { Start = new Point2(-3, -3), End = new Point2(-3, 3), Layer = "NOTES" },   // not an obstruction
            });
            var tools = new TurnPathTools(host, TurnPathConventions.Default());
            using JsonDocument input = JsonDocument.Parse(
                "{\"mode\":\"solve\",\"start\":[0,-2],\"heading_deg\":0,\"target\":[0,2],\"target_heading_deg\":180,\"obstruction_layers\":[\"KERB\"]}");
            ToolOutcome outcome = tools.Execute(TurnPathTools.DrawTurnPath, input.RootElement);
            Assert.False(outcome.IsError, outcome.Content);
            using JsonDocument doc = JsonDocument.Parse(outcome.Content);
            JsonElement solver = doc.RootElement.GetProperty("solver");
            Assert.Equal(3, solver.GetProperty("movements").GetInt32());
            Assert.Equal(300, solver.GetProperty("clearance_kept_mm").GetDouble());
            Assert.False(solver.GetProperty("clearance_reduced").GetBoolean());
            Assert.Equal(2, solver.GetProperty("obstructions_in_range").GetInt32());
            Assert.Equal(3, doc.RootElement.GetProperty("legs").GetArrayLength());
            Assert.True(doc.RootElement.GetProperty("conflicts").GetProperty("passes").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("within_minimum_radius").GetBoolean());
            Assert.Contains("\"movement 2: reverse", outcome.Content);
            Assert.Contains(host.Entities, e => e.Layer == "AE CIV PLN - VEHICLE SWEPT PATH");

            // Solving again doesn't treat the swept path just drawn as an obstruction.
            using JsonDocument all = JsonDocument.Parse(
                "{\"mode\":\"solve\",\"start\":[0,-2],\"heading_deg\":0,\"target\":[0,2],\"target_heading_deg\":180,\"obstruction_layers\":[\"KERB\",\"AE CIV*\"],\"dry_run\":true}");
            ToolOutcome second = tools.Execute(TurnPathTools.DrawTurnPath, all.RootElement);
            Assert.False(second.IsError, second.Content);
            using JsonDocument doc2 = JsonDocument.Parse(second.Content);
            Assert.Equal(3, doc2.RootElement.GetProperty("solver").GetProperty("movements").GetInt32());

            // No obstruction layers, a radius it can't steer, a target in a kerb: each says why.
            using JsonDocument noLayers = JsonDocument.Parse("{\"mode\":\"solve\",\"start\":[0,-2],\"heading_deg\":0,\"target\":[0,2],\"target_heading_deg\":180}");
            ToolOutcome refused = tools.Execute(TurnPathTools.DrawTurnPath, noLayers.RootElement);
            Assert.True(refused.IsError);
            Assert.Contains("obstruction_layers", refused.Content);

            using JsonDocument tight = JsonDocument.Parse(
                "{\"mode\":\"solve\",\"start\":[0,-2],\"heading_deg\":0,\"target\":[0,2],\"target_heading_deg\":180,\"radius\":4,\"obstruction_layers\":[\"KERB\"]}");
            Assert.Contains("minimum", tools.Execute(TurnPathTools.DrawTurnPath, tight.RootElement).Content);

            using JsonDocument blocked = JsonDocument.Parse(
                "{\"mode\":\"solve\",\"start\":[0,-2],\"heading_deg\":0,\"target\":[0,4.2],\"target_heading_deg\":180,\"obstruction_layers\":[\"KERB\"],\"dry_run\":true}");
            ToolOutcome stuck = tools.Execute(TurnPathTools.DrawTurnPath, blocked.RootElement);
            Assert.True(stuck.IsError);
            Assert.Contains("target overlaps an obstruction", stuck.Content);
        }
    }
}
