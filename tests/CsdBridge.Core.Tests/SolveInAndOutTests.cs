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
    /// <summary>
    /// The 3-point solve: into a site, to a stop on it, and out again, entering and leaving in a
    /// forward direction. The site here is a yard 40 m wide and 25 m deep on the north side of a
    /// street, with a 7 m driveway in the middle of its frontage (the boundary is y = 0).
    /// </summary>
    public class SolveInAndOutTests
    {
        private readonly ITestOutputHelper _out;

        public SolveInAndOutTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static Obstruction Wall(double x1, double y1, double x2, double y2) =>
            new Obstruction("W", "FENCE", new Path2(new List<Segment> { new LineSegment(new Point2(x1, y1), new Point2(x2, y2)) }, false));

        private static readonly Obstruction[] Site =
        {
            Wall(-20, 0, -3.5, 0), Wall(3.5, 0, 20, 0),                       // the frontage, either side of the driveway
            Wall(-20, 0, -20, 25), Wall(20, 0, 20, 25), Wall(-20, 25, 20, 25),  // the sides and the back
            Wall(-40, -9, 40, -9),                                             // the far kerb of the street
        };

        private static readonly (Point2 A, Point2 B) Driveway = (new Point2(-3.5, 0), new Point2(3.5, 0));

        private static readonly VehiclePose OnTheStreet = new VehiclePose(new Point2(-15, -2.5), 0);          // driving east, before the driveway
        private static readonly VehiclePose LeavingEast = new VehiclePose(new Point2(15, -2.5), 0);           // driving east, past it

        private static double AngleDiff(double a, double b) => Math.Abs(Math.Atan2(Math.Sin(a - b), Math.Cos(a - b)));

        private static SolveRequest Request(VehiclePose start, VehiclePose goal) => new SolveRequest
        {
            Vehicle = DesignVehicle.B99,
            Start = start,
            Goal = goal,
            Obstructions = Site,
            Min = new Point2(-32, -8.5),
            Max = new Point2(32, 25),
        };

        /// <summary>Simulates the movements; checks they join up and hit nothing; returns each with its simulation.</summary>
        private List<(SolvedLeg Leg, TurnPathResult Sim)> Drive(SolveRequest q, SolveResult r)
        {
            _out.WriteLine($"{r.Summary()}  [{r.Expansions:N0} positions, {r.Seconds:0.00} s]");
            Assert.True(r.Solved, r.Failure);
            var driven = new List<(SolvedLeg, TurnPathResult)>();
            VehiclePose at = q.Start;
            foreach (SolvedLeg leg in r.Legs)
            {
                TurnPathRequest req = leg.ToRequest(q.Vehicle);
                req.ClearanceInside = req.ClearanceOutside = r.ClearanceUsed;
                TurnPathResult sim = TurnPathSimulator.Simulate(req);
                Assert.Equal(0, Segment.Dist(sim.Poses[0].RearAxle, at.RearAxle), 3);
                Assert.True(AngleDiff(sim.Poses[0].Heading, at.Heading) < 1e-3, "the movements don't join on the heading");
                at = sim.Poses[sim.Poses.Count - 1];
                driven.Add((leg, sim));
            }

            Assert.Equal(0, Segment.Dist(at.RearAxle, q.Goal.RearAxle), 3);
            Assert.True(AngleDiff(at.Heading, q.Goal.Heading) < 1e-3);
            Assert.False(ConflictChecker.Check(driven.Select(d => d.Item2).ToList(), q.Obstructions).BodyHits);
            return driven;
        }

        /// <summary>True when the body is over the driveway's boundary line at some pose of the movement.</summary>
        private static bool OverTheBoundary(DesignVehicle v, TurnPathResult sim)
        {
            foreach (VehiclePose pose in sim.Poses)
            {
                Point2 nose = pose.ToWorld(v.Wheelbase + v.FrontOverhang, 0), tail = pose.ToWorld(-v.RearOverhang, 0);
                bool straddles = (nose.Y > 0) != (tail.Y > 0);
                double across = nose.Y == tail.Y ? nose.X : tail.X + (nose.X - tail.X) * (0 - tail.Y) / (nose.Y - tail.Y);
                if (straddles && Math.Abs(across) < 3.5 + v.Width / 2)
                {
                    return true;
                }
            }

            return false;
        }

        [Fact]
        public void LeftToItselfTheSolverBacksInOffTheStreet()
        {
            // The stop is just inside the driveway, nose out. The quickest way there from the street is
            // to drive past and reverse in, which is exactly what a site must not need.
            var stop = new VehiclePose(new Point2(0, 9), -Math.PI / 2);
            SolveRequest q = Request(OnTheStreet, stop);
            SolveResult r = ManoeuvreSolver.SolveWithFallback(q);
            List<(SolvedLeg Leg, TurnPathResult Sim)> driven = Drive(q, r);
            Assert.Equal(2, r.Movements);
            Assert.Contains(driven, d => d.Leg.Reverse && OverTheBoundary(q.Vehicle, d.Sim));
        }

        [Fact]
        public void InAndOutEntersAndLeavesForwards()
        {
            var stop = new VehiclePose(new Point2(0, 9), -Math.PI / 2);
            SolveRequest q = Request(OnTheStreet, LeavingEast);
            q.ForwardOnlyLines = new[] { Driveway };
            SolveResult r = ManoeuvreSolver.SolveInAndOut(q, stop);
            List<(SolvedLeg Leg, TurnPathResult Sim)> driven = Drive(q, r);

            // Two stages, in order, and the stop is where the way in ends.
            Assert.NotNull(r.In);
            Assert.NotNull(r.Out);
            Assert.Equal(r.In!.Movements + r.Out!.Movements, r.Movements);
            Assert.All(driven.Take(r.In.Movements), d => Assert.Equal("in", d.Leg.Stage));
            Assert.All(driven.Skip(r.In.Movements), d => Assert.Equal("out", d.Leg.Stage));
            TurnPathResult lastIn = driven[r.In.Movements - 1].Sim;
            VehiclePose parked = lastIn.Poses[lastIn.Poses.Count - 1];
            Assert.Equal(0, Segment.Dist(parked.RearAxle, stop.RearAxle), 3);
            Assert.True(AngleDiff(parked.Heading, stop.Heading) < 1e-3);

            // It drives in forwards and out forwards, and is never over the boundary in reverse.
            Assert.False(driven[0].Leg.Reverse, "the first movement is forwards");
            Assert.False(driven[driven.Count - 1].Leg.Reverse, "the last movement is forwards");
            Assert.DoesNotContain(driven, d => d.Leg.Reverse && OverTheBoundary(q.Vehicle, d.Sim));
            Assert.Contains(driven, d => !d.Leg.Reverse && d.Leg.Stage == "in" && OverTheBoundary(q.Vehicle, d.Sim));
            Assert.Contains(driven, d => !d.Leg.Reverse && d.Leg.Stage == "out" && OverTheBoundary(q.Vehicle, d.Sim));

            // Turning round on the site takes more movements than backing in off the street did.
            Assert.True(r.In.Movements >= 2, "it has to turn round on the site to stand nose out");
            Assert.Equal(1, r.Out.Movements);
            Assert.Contains("Way in:", r.Summary());
            Assert.Contains("Way out:", r.Summary());
            Assert.Contains("in forwards and out forwards", r.Summary());
        }

        [Fact]
        public void ReversingOntoADockIsDoneOnTheSite()
        {
            // A dock on the back fence: the vehicle stands with its tail to the fence, nose to the street.
            DesignVehicle v = DesignVehicle.B99;
            var dock = new VehiclePose(new Point2(10, 25 - 0.6 - v.RearOverhang), -Math.PI / 2);
            SolveRequest q = Request(OnTheStreet, LeavingEast);
            q.ForwardOnlyLines = new[] { Driveway };
            SolveResult r = ManoeuvreSolver.SolveInAndOut(q, dock);
            List<(SolvedLeg Leg, TurnPathResult Sim)> driven = Drive(q, r);
            Assert.False(driven[0].Leg.Reverse);
            Assert.True(driven[r.In!.Movements - 1].Leg.Reverse, "it backs onto the dock");
            Assert.False(driven[driven.Count - 1].Leg.Reverse);
            Assert.DoesNotContain(driven, d => d.Leg.Reverse && OverTheBoundary(q.Vehicle, d.Sim));

            // The rules one at a time. A first movement forwards, and a last movement forwards.
            SolveRequest first = Request(new VehiclePose(new Point2(0, 12), Math.PI / 2), new VehiclePose(new Point2(0, 6), Math.PI / 2));
            Assert.True(ManoeuvreSolver.SolveWithFallback(first).Legs[0].Reverse, "straight back is the obvious way");
            first.FirstForward = true;
            SolveResult forwardsFirst = ManoeuvreSolver.SolveWithFallback(first);
            Assert.True(forwardsFirst.Solved, forwardsFirst.Failure);
            Assert.False(forwardsFirst.Legs[0].Reverse);

            SolveRequest last = Request(new VehiclePose(new Point2(0, 12), Math.PI / 2), new VehiclePose(new Point2(0, 6), Math.PI / 2));
            last.LastForward = true;
            SolveResult forwardsLast = ManoeuvreSolver.SolveWithFallback(last);
            Assert.True(forwardsLast.Solved, forwardsLast.Failure);
            Assert.False(forwardsLast.Legs[forwardsLast.Legs.Count - 1].Reverse);

            // A stop the vehicle can't stand at says which half failed.
            SolveResult blocked = ManoeuvreSolver.SolveInAndOut(q, new VehiclePose(new Point2(19.5, 10), 0));
            Assert.False(blocked.Solved);
            Assert.StartsWith("on the way in", blocked.Failure);
        }

        // ------------------------------------------------------------------ saved, planned and through the tool

        [Fact]
        public void AnInAndOutSolveIsKeptAndPlannedWithItsStages()
        {
            TurnPathConventions c = TurnPathConventions.Default();
            var r = new ManoeuvreRecord
            {
                Vehicle = "B99",
                Mode = "solve",
                Start = new[] { -15.0, -2.5 },
                HeadingDeg = 0,
                Via = new[] { 0.0, 9 },
                ViaHeadingDeg = -90,
                Target = new[] { 15.0, -2.5 },
                TargetHeadingDeg = 0,
                Boundary = new[] { -3.5, 0, 3.5, 0 },
                Radius = 6.3,
                ClearanceInside = 0.3,
                ClearanceOutside = 0.3,
                ObstructionLayers = new List<string> { "FENCE" },
            };
            SolveResult solved = ManoeuvrePlanner.Solve(r, c, (min, max) => Site);
            Assert.True(solved.Solved, solved.Failure);
            Assert.NotNull(solved.In);
            Assert.False(r.NeedsSolving);
            Assert.Contains("Way in:", r.SolvedSummary);

            ManoeuvreRecord back = ManoeuvreRecord.FromJson(r.ToJson());
            Assert.Equal(new[] { 0.0, 9 }, back.Via);
            Assert.Equal(-90, back.ViaHeadingDeg);
            Assert.Equal(new[] { -3.5, 0, 3.5, 0 }, back.Boundary);
            Assert.False(back.NeedsSolving);
            ManoeuvrePlan plan = ManoeuvrePlanner.Plan(back, c);
            Assert.Equal(solved.Movements, plan.Legs.Count);
            Assert.Equal("B99, in and out", plan.Legs[0].Label);
            Assert.StartsWith("in, forward", plan.Legs[0].Description);
            Assert.StartsWith("out, forward", plan.Legs[plan.Legs.Count - 1].Description);
            Assert.True(Segment.Dist(plan.EndPose.RearAxle, new Point2(15, -2.5)) < 1e-3);

            // Moving the stop, or the boundary, means solving again; so does moving the block.
            back.Via = new[] { 1.0, 9 };
            Assert.True(back.NeedsSolving);
            back.Via = new[] { 0.0, 9 };
            Assert.False(back.NeedsSolving);
            back.Boundary = null;
            Assert.True(back.NeedsSolving);

            ManoeuvreRecord moved = ManoeuvreRecord.FromJson(r.ToJson());
            moved.Transform(new Point2(0, 0), Math.PI / 2, 100, 0);
            Assert.Equal(91.0, moved.Via![0], 6);      // (0, 9) turned a quarter turn about the origin, then 100 east
            Assert.Equal(0.0, moved.Via[1], 6);
            Assert.Equal(0, moved.ViaHeadingDeg, 6);
            Assert.Equal(100.0, moved.Boundary![0], 6);
            Assert.Equal(-3.5, moved.Boundary[1], 6);
            Assert.Null(moved.Solved);

            // In a millimetre drawing the stop and the boundary are rescaled with everything else.
            ManoeuvreRecord rescaled = ManoeuvreRecord.FromJson(r.ToJson());
            rescaled.SetUnits(1000);
            Assert.Equal(0.009, rescaled.Via![1], 9);
            Assert.Equal(0.0035, rescaled.Boundary![2], 9);

            // A plain solve has no stages.
            r.Via = null;
            r.Boundary = null;
            SolveResult plain = ManoeuvrePlanner.Solve(r, c, (min, max) => Site);
            Assert.True(plain.Solved, plain.Failure);
            Assert.Null(plain.In);
            Assert.All(r.Solved!, m => Assert.Null(m.Stage));
        }

        [Fact]
        public void TheToolSolvesInAndOutAndUsesTheStandardSolveLayers()
        {
            var host = new InMemoryDrawingHost("Meters");
            host.CreateEntities(Site.Select(o => (EntitySpec)new LineSpec { Start = o.Path.Start, End = o.Path.End, Layer = "C-FENCE" }).ToArray());
            host.CreateEntities(new EntitySpec[] { new LineSpec { Start = new Point2(-5, 12), End = new Point2(5, 12), Layer = "NOTES" } });

            // The standard solve layers are set once, in the conventions: no layers need be given.
            TurnPathConventions c = TurnPathConventions.Default();
            c.SolveLayers = new List<string> { "C-FENCE", "C-KERB*" };
            c.ObstructionLayers = new List<string> { "NOTES" };          // for the conflict check of other manoeuvres
            Assert.Equal(new[] { "C-FENCE", "C-KERB*" }, c.SolveBoundaryLayers);
            var tools = new TurnPathTools(host, c);
            using JsonDocument input = JsonDocument.Parse(
                "{\"mode\":\"solve\",\"start\":[-15,-2.5],\"heading_deg\":0,\"via\":[0,9],\"via_heading_deg\":-90,\"target\":[15,-2.5],\"target_heading_deg\":0," +
                "\"boundary\":[[-3.5,0],[3.5,0]],\"dry_run\":true}");
            ToolOutcome outcome = tools.Execute(TurnPathTools.DrawTurnPath, input.RootElement);
            Assert.False(outcome.IsError, outcome.Content);
            using JsonDocument doc = JsonDocument.Parse(outcome.Content);
            Assert.Contains("Way in:", doc.RootElement.GetProperty("solver").GetProperty("summary").GetString());
            Assert.Equal(6, doc.RootElement.GetProperty("solver").GetProperty("obstructions_in_range").GetInt32());
            string[] legs = doc.RootElement.GetProperty("legs").EnumerateArray().Select(l => l.GetString()!).ToArray();
            Assert.Contains("in, forward", legs[0]);
            Assert.Contains("out, forward", legs[legs.Length - 1]);

            // With no solve layers set, the obstruction layers are the fallback; with neither, it asks for them.
            c.SolveLayers.Clear();
            Assert.Equal(new[] { "NOTES" }, c.SolveBoundaryLayers);
            c.ObstructionLayers.Clear();
            ToolOutcome none = tools.Execute(TurnPathTools.DrawTurnPath, input.RootElement);
            Assert.True(none.IsError);
            Assert.Contains("obstruction_layers", none.Content);

            // A boundary without a stop makes no sense.
            using JsonDocument odd = JsonDocument.Parse(
                "{\"mode\":\"solve\",\"start\":[-15,-2.5],\"heading_deg\":0,\"target\":[15,-2.5],\"target_heading_deg\":0,\"boundary\":[[-3.5,0],[3.5,0]],\"obstruction_layers\":[\"C-FENCE\"]}");
            ToolOutcome refused = tools.Execute(TurnPathTools.DrawTurnPath, odd.RootElement);
            Assert.True(refused.IsError);
            Assert.Contains("via", refused.Content);

            // The setting is read from, and written to, the settings file.
            c.SolveLayers = new List<string> { "C-KERB", "A-WALL*" };
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "csd-bridge-solve-layers-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                c.Save(path);
                Assert.Contains("\"solve_layers\"", System.IO.File.ReadAllText(path));
                Assert.Equal(new[] { "C-KERB", "A-WALL*" }, TurnPathConventions.Load(path).SolveLayers);
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }
    }
}
