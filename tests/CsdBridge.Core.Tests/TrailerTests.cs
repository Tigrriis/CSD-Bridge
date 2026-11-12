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
    /// <summary>
    /// Vehicles towing trailers. The vehicles here are made up for the tests (round numbers, not
    /// any standard's design vehicle): what is checked is the towing geometry, against results
    /// that can be worked out by hand.
    /// </summary>
    public class TrailerTests
    {
        /// <summary>A prime mover: 6 m long, rear axle group 1 m from the back, fifth wheel 0.25 m ahead of it.</summary>
        private static DesignVehicle PrimeMover() => new DesignVehicle
        {
            Name = "TEST PM",
            Length = 6.0,
            Width = 2.5,
            Wheelbase = 3.6,
            FrontOverhang = 1.4,
            RearOverhang = 1.0,
            EffectiveFrontOverhang = 1.4,
            EffectiveRearOverhang = 1.0,
            EffectiveWidth = 2.5,
            Track = 2.1,
            MinTurnRadius = 9,
            Hitch = 0.25,
        };

        /// <summary>The prime mover with a 13 m semi-trailer: kingpin 1.2 m back from its front, axle group 8.5 m behind the kingpin.</summary>
        private static DesignVehicle Semi()
        {
            DesignVehicle v = PrimeMover();
            v.Name = "TEST SEMI";
            v.Trailers.Add(new TrailerUnit { Name = "semi-trailer", Length = 13.0, Width = 2.5, Wheelbase = 8.5, FrontOverhang = 1.2 });
            return v;
        }

        /// <summary>The prime mover with two trailers: the second couples over the first one's axle group.</summary>
        private static DesignVehicle BDouble()
        {
            DesignVehicle v = PrimeMover();
            v.Name = "TEST B-DOUBLE";
            v.Trailers.Add(new TrailerUnit { Name = "A trailer", Length = 8.0, Width = 2.5, Wheelbase = 6.0, FrontOverhang = 1.2, Hitch = 0.0 });
            v.Trailers.Add(new TrailerUnit { Name = "B trailer", Length = 12.0, Width = 2.5, Wheelbase = 8.0, FrontOverhang = 1.2 });
            return v;
        }

        private static double Dist(Point2 a, Point2 b) => Segment.Dist(a, b);

        private static double AngleDiff(double a, double b) => Math.Abs(Math.Atan2(Math.Sin(a - b), Math.Cos(a - b)));

        /// <summary>The rear axle driven most of the way round a circle of the given radius about the origin, anticlockwise.</summary>
        private static TurnPathResult Circle(DesignVehicle v, double radius, double sweepDeg = 340) =>
            TurnPathSimulator.Simulate(new TurnPathRequest
            {
                Vehicle = v,
                Path = new Path2(new List<Segment> { new ArcSegment(new Point2(0, 0), radius, -Math.PI / 2, sweepDeg * Math.PI / 180) }, false),
                Reference = v.RearAxleCentre,
                Step = 0.02,
            });

        [Fact]
        public void TheDimensionsOfACombinationAddUp()
        {
            DesignVehicle semi = Semi();
            Assert.True(semi.IsArticulated);
            Assert.False(PrimeMover().IsArticulated);
            Assert.Equal(13.0 - 1.2 - 8.5, semi.Trailers[0].RearOverhang, 9);

            // Front of the prime mover (5.0 ahead of its rear axle) to the back of the trailer:
            // kingpin at +0.25, trailer axles 8.5 behind it, body 3.3 behind them.
            Assert.Equal(5.0 + 8.5 - 0.25 + 3.3, semi.OverallLength, 9);
            Assert.Equal(6.0, PrimeMover().OverallLength, 9);

            // Straight: the trailer's axles sit 8.25 m behind the prime mover's rear axle, facing the same way.
            VehiclePose axles = Assert.Single(semi.StraightTrailerPoses(new VehiclePose(new Point2(10, 0), 0)));
            Assert.Equal(new Point2(10 - 8.25, 0), axles.RearAxle);
            Assert.Equal(0, axles.Heading);

            // Each trailer hangs on the one ahead.
            List<VehiclePose> two = BDouble().StraightTrailerPoses(new VehiclePose(new Point2(0, 0), Math.PI / 2));
            Assert.Equal(-(6.0 - 0.25), two[0].RearAxle.Y, 9);
            Assert.Equal(-(6.0 - 0.25) - 8.0, two[1].RearAxle.Y, 9);

            Assert.Empty(semi.Validate());
            DesignVehicle bad = Semi();
            bad.Trailers[0].Length = 9;      // shorter than its kingpin setback plus its wheelbase
            Assert.Contains(bad.Validate(), p => p.Contains("trailer"));

            // A copy doesn't share its trailers.
            DesignVehicle copy = semi.Clone();
            copy.Trailers[0].Wheelbase = 1;
            Assert.Equal(8.5, semi.Trailers[0].Wheelbase);
        }

        [Fact]
        public void OnAStraightTheTrailerFollowsInLine()
        {
            DesignVehicle v = Semi();
            TurnPathResult r = TurnPathSimulator.Simulate(new TurnPathRequest
            {
                Vehicle = v,
                Path = new Path2(new List<Segment> { new LineSegment(new Point2(0, 0), new Point2(40, 0)) }, false),
                Reference = v.FrontAxleCentre,
            });
            TrailerTrack trailer = Assert.Single(r.Trailers);
            Assert.Equal(r.Poses.Count, trailer.Poses.Count);
            Assert.All(trailer.Poses, p => Assert.True(Math.Abs(p.RearAxle.Y) < 1e-9 && AngleDiff(p.Heading, 0) < 1e-9));
            Assert.Equal(r.Poses[0].RearAxle.X - 8.25, trailer.Poses[0].RearAxle.X, 9);
            Assert.Equal(0, r.MaxArticulationDeg, 6);

            // The swept path is a 2.5 m strip from the back of the trailer to the front of the prime mover.
            Assert.All(r.LeftBody, p => Assert.Equal(1.25, p.Y, 3));
            Assert.All(r.RightBody, p => Assert.Equal(-1.25, p.Y, 3));
            Assert.Equal(-3.6 - 8.25 - 3.3, r.LeftBody.Min(p => p.X), 2);
            Assert.Equal(40 + 1.4, r.LeftBody.Max(p => p.X), 2);
            Assert.Equal(6, r.WheelPaths.Count);     // four on the prime mover, two on the trailer
        }

        [Fact]
        public void RoundACircleTheTrailerSettlesWhereGeometrySaysItMust()
        {
            // In a steady turn every axle line points at the turn centre. With the prime mover's rear
            // axle on radius R, its fifth wheel (h ahead of the axle) is on sqrt(R² + h²), and a
            // trailer whose axles are S behind its kingpin has them on sqrt(kingpin radius² - S²).
            DesignVehicle v = Semi();
            const double R = 15;
            TurnPathResult r = Circle(v, R);
            double kingpin = Math.Sqrt(R * R + 0.25 * 0.25);
            double expected = Math.Sqrt(kingpin * kingpin - 8.5 * 8.5);
            TrailerTrack trailer = r.Trailers[0];
            double settled = Dist(trailer.Poses[trailer.Poses.Count - 1].RearAxle, new Point2(0, 0));
            Assert.Equal(expected, settled, 2);
            Assert.True(settled < R - 2.5, "the trailer cuts well inside the prime mover");

            // The angle between them settles too. Seen from the turn centre the trailer's axles trail
            // the kingpin by asin(S / kingpin radius), and the kingpin leads the rear axle by atan(h / R).
            double fold = Math.Asin(8.5 / kingpin) - Math.Atan(0.25 / R);
            VehiclePose pm = r.Poses[r.Poses.Count - 1], tr = trailer.Poses[trailer.Poses.Count - 1];
            Assert.Equal(fold * 180 / Math.PI, AngleDiff(pm.Heading, tr.Heading) * 180 / Math.PI, 1);
            Assert.Equal(fold * 180 / Math.PI, r.MaxArticulationDeg, 1);
            Assert.DoesNotContain(r.Warnings, w => w.Contains("jackknife"));

            // It gets there gradually: starting in line, the trailer's radius only shrinks.
            double previous = double.MaxValue;
            for (int i = 0; i < trailer.Poses.Count; i += 200)
            {
                double radius = Dist(trailer.Poses[i].RearAxle, new Point2(0, 0));
                Assert.True(radius <= previous + 1e-6);
                previous = radius;
            }

            // The kingpin never comes off the fifth wheel.
            for (int i = 0; i < r.Poses.Count; i += 97)
            {
                Assert.Equal(0, Dist(r.Poses[i].ToWorld(0.25, 0), trailer.Poses[i].ToWorld(8.5, 0)), 9);
            }
        }

        [Fact]
        public void ASecondTrailerFollowsTheFirst()
        {
            DesignVehicle v = BDouble();
            const double R = 20;
            TurnPathResult r = Circle(v, R, 350);
            Assert.Equal(2, r.Trailers.Count);
            double kingpin = Math.Sqrt(R * R + 0.25 * 0.25);
            double first = Math.Sqrt(kingpin * kingpin - 6.0 * 6.0);      // the A trailer's axles
            double second = Math.Sqrt(first * first - 8.0 * 8.0);         // the B trailer couples over them
            Assert.Equal(first, Dist(r.Trailers[0].Poses[r.Poses.Count - 1].RearAxle, new Point2(0, 0)), 2);
            Assert.Equal(second, Dist(r.Trailers[1].Poses[r.Poses.Count - 1].RearAxle, new Point2(0, 0)), 1);
            Assert.Equal(8, r.WheelPaths.Count);
        }

        [Fact]
        public void TheSweptPathHoldsEveryBodyAndIsBoundedByTheTrailerInsideAndThePrimeMoverOutside()
        {
            // A right-angle left turn: 15 m straight, a quarter circle at 12.5 m (front axle), 25 m straight.
            DesignVehicle v = Semi();
            var path = new Path2(new List<Segment>
            {
                new LineSegment(new Point2(0, 0), new Point2(15, 0)),
                new ArcSegment(new Point2(15, 12.5), 12.5, -Math.PI / 2, Math.PI / 2),
                new LineSegment(new Point2(27.5, 12.5), new Point2(27.5, 37.5)),
            }, false);
            TurnPathResult r = TurnPathSimulator.Simulate(new TurnPathRequest { Vehicle = v, Path = path, Reference = v.FrontAxleCentre, Tolerance = 0.002 });
            Assert.Equal(90, r.NetTurnDeg, 0);
            Assert.InRange(r.MaxArticulationDeg, 20, 80);

            // Every corner of both bodies, at every pose, is inside the swept path (or on its edge).
            var polygon = new List<Point2>(r.LeftBody);
            polygon.AddRange(Enumerable.Reverse(r.RightBody));
            bool Inside(Point2 p)
            {
                bool inside = false;
                for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
                {
                    if ((polygon[i].Y > p.Y) != (polygon[j].Y > p.Y)
                        && p.X < (polygon[j].X - polygon[i].X) * (p.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X)
                    {
                        inside = !inside;
                    }
                }

                return inside;
            }

            double Outside(Point2 p)
            {
                if (Inside(p))
                {
                    return 0;
                }

                double best = double.MaxValue;
                for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
                {
                    double dx = polygon[i].X - polygon[j].X, dy = polygon[i].Y - polygon[j].Y;
                    double t = Math.Max(0, Math.Min(1, ((p.X - polygon[j].X) * dx + (p.Y - polygon[j].Y) * dy) / Math.Max(1e-12, dx * dx + dy * dy)));
                    best = Math.Min(best, Dist(p, new Point2(polygon[j].X + dx * t, polygon[j].Y + dy * t)));
                }

                return best;
            }

            DesignVehicle body = r.Trailers[0].Body;
            double worst = 0;
            for (int i = 0; i < r.Poses.Count; i += 25)
            {
                foreach (double y in new[] { 1.25, -1.25 })
                {
                    worst = Math.Max(worst, Outside(r.Poses[i].ToWorld(v.Wheelbase + v.FrontOverhang, y)));
                    worst = Math.Max(worst, Outside(r.Poses[i].ToWorld(-v.RearOverhang, y)));
                    worst = Math.Max(worst, Outside(r.Trailers[0].Poses[i].ToWorld(body.Wheelbase + body.FrontOverhang, y)));
                    worst = Math.Max(worst, Outside(r.Trailers[0].Poses[i].ToWorld(-body.RearOverhang, y)));
                    worst = Math.Max(worst, Outside(r.Trailers[0].Poses[i].ToWorld(0, y)));
                }
            }

            Assert.True(worst < 0.02, $"a body corner is {worst * 1000:0} mm outside the swept path");

            // Inside the turn the edge is the trailer's: it comes closer to the centre than the prime
            // mover's inner side ever does. Outside, it is the prime mover's front corner.
            var centre = new Point2(15, 12.5);
            double innerPrimeMover = r.Poses.Min(p => Dist(p.ToWorld(0, 1.25), centre));
            double innerTrailer = r.Trailers[0].Poses.Min(p => Dist(p.ToWorld(0, 1.25), centre));
            double innerEdge = r.LeftBody.Min(p => Dist(p, centre));
            Assert.True(innerTrailer < innerPrimeMover - 1, "the trailer off-tracks by more than a metre");
            Assert.Equal(innerTrailer, innerEdge, 1);
            // In the turn the rear axle is on sqrt(12.5² - wheelbase²), and the corner is 1.25 m outside it and 5 m ahead.
            double rearAxle = Math.Sqrt(12.5 * 12.5 - 3.6 * 3.6);
            double outerCorner = Math.Sqrt((rearAxle + 1.25) * (rearAxle + 1.25) + 5.0 * 5.0);
            Assert.Equal(outerCorner, r.RightBody.Where(p => p.X > 14 && p.Y < 13).Max(p => Dist(p, centre)), 1);

            // Clearances are offset from the combined edges, and the outline closes.
            Assert.True(r.LeftClearance.Count > 10 && r.RightClearance.Count > 10);
            Assert.True(r.ClosedOutline().Closed);
            Assert.All(r.ClosedOutline().Bulges!, b => Assert.Equal(0, b));
        }

        [Fact]
        public void ATrailerCanBeStartedWhereTheLastLegLeftItButNotReversed()
        {
            DesignVehicle v = Semi();
            var first = new Path2(new List<Segment> { new ArcSegment(new Point2(0, 12.5), 12.5, -Math.PI / 2, Math.PI / 3) }, false);
            TurnPathResult a = TurnPathSimulator.Simulate(new TurnPathRequest { Vehicle = v, Path = first, Reference = v.RearAxleCentre });
            double folded = Assert.Single(a.EndTrailerHeadings);
            VehiclePose end = a.Poses[a.Poses.Count - 1];
            Assert.True(AngleDiff(folded, end.Heading) > 0.2, "the trailer is still folded at the end of the turn");

            // The next leg carries on from there: the trailer starts folded, not snapped straight.
            var second = new Path2(new List<Segment> { new LineSegment(end.RearAxle, end.ToWorld(40, 0)) }, false);
            TurnPathResult b = TurnPathSimulator.Simulate(new TurnPathRequest
            {
                Vehicle = v,
                Path = second,
                Reference = v.RearAxleCentre,
                InitialHeading = end.Heading,
                InitialTrailerHeadings = a.EndTrailerHeadings,
            });
            Assert.Equal(0, Dist(b.Trailers[0].Poses[0].RearAxle, a.Trailers[0].Poses[a.Poses.Count - 1].RearAxle), 6);
            Assert.True(AngleDiff(b.Trailers[0].Poses[b.Poses.Count - 1].Heading, end.Heading) < 0.05, "40 m of straight pulls it back into line");

            // Reversing with a trailer is refused, with the reason.
            var ex = Assert.Throws<ArgumentException>(() => TurnPathSimulator.Simulate(new TurnPathRequest { Vehicle = v, Path = second, Reference = v.RearAxleCentre, Reverse = true }));
            Assert.Contains("reversing with a trailer", ex.Message);

            // A turn far too tight for the trailer says it would jackknife.
            DesignVehicle tight = Semi();
            tight.MinTurnRadius = 4;
            TurnPathResult folds = Circle(tight, 4, 300);
            Assert.True(folds.MaxArticulationDeg > 90);
            Assert.Contains(folds.Warnings, w => w.Contains("jackknife"));
        }

        [Fact]
        public void TheDrawingShowsTheTrailerWithThePrimeMover()
        {
            DesignVehicle v = Semi();
            var path = new Path2(new List<Segment> { new LineSegment(new Point2(0, 0), new Point2(10, 0)), new ArcSegment(new Point2(10, 15), 15, -Math.PI / 2, Math.PI / 4) }, false);
            TurnPathResult r = TurnPathSimulator.Simulate(new TurnPathRequest { Vehicle = v, Path = path, Reference = v.FrontAxleCentre });
            var style = new TurnPathStyle { Draw = new TurnPathDrawOptions { SweptPath = false, Clearance = false, Path = false, Labels = false, Vehicle = true } };
            List<DraftedEntity> drawn = TurnPathDrafter.Draw(r, style);

            // At the start and at the end: two bodies, six wheels, three axle lines and a kingpin.
            Assert.Equal(2 * 2, drawn.Count(d => d.Spec is PolylineSpec p && p.Closed && p.Points.Count >= 8));
            Assert.Equal(2 * 6, drawn.Count(d => d.Spec is PolylineSpec p && p.Closed && p.Points.Count == 4));
            Assert.Equal(2 * 3, drawn.Count(d => d.Spec is LineSpec));
            Assert.Equal(2, drawn.Count(d => d.Spec is CircleSpec));

            // The trailer drawn at the end is where the simulation left it, folded behind the prime mover.
            VehiclePose last = r.Trailers[0].Poses[r.Poses.Count - 1];
            Assert.Contains(drawn, d => d.Spec is CircleSpec c && Dist(c.Center, last.ToWorld(8.5, 0)) < 1e-6);

            // With nothing simulated yet (the preview of a parked vehicle) the trailer is drawn straight behind.
            List<EntitySpec> parked = TurnPathDrafter.VehicleOutline(v, new VehiclePose(new Point2(0, 0), 0), "V", wheels: false);
            Assert.Equal(2, parked.Count);
            Assert.Equal(-8.25 - 3.3, parked.OfType<PolylineSpec>().SelectMany(p => p.Points).Min(p => p.X), 6);
        }

        // ------------------------------------------------------------------ in the conventions, the planner and the tool

        private static TurnPathConventions Conventions()
        {
            TurnPathConventions c = TurnPathConventions.Default();
            c.Vehicles["TEST SEMI"] = Semi();
            return c;
        }

        [Fact]
        public void ATruckAndItsTrailersAreReadFromTheConventionsFile()
        {
            // A truck leaves out the effective dimensions (its body is square), and its trailers are listed front to back.
            string json = TurnPathConventions.Default().Vehicles.Count > 0 ? @"{
              ""vehicles"": {
                ""RIGID"": { ""length"": 9, ""width"": 2.5, ""wheelbase"": 5, ""front_overhang"": 1.5, ""rear_overhang"": 2.5, ""track"": 2.1, ""min_turn_radius"": 10 },
                ""SEMI"": {
                  ""description"": ""made up for a test"",
                  ""length"": 6, ""width"": 2.5, ""wheelbase"": 3.6, ""front_overhang"": 1.4, ""rear_overhang"": 1.0, ""track"": 2.1, ""min_turn_radius"": 9,
                  ""hitch"": 0.25,
                  // front to back
                  ""trailers"": [ { ""name"": ""semi-trailer"", ""length"": 13, ""width"": 2.5, ""wheelbase"": 8.5, ""front_overhang"": 1.2 } ]
                }
              },
              ""default_vehicle"": ""RIGID""
            }" : "";
            string path = Path.Combine(Path.GetTempPath(), "csd-bridge-trailers-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            try
            {
                TurnPathConventions c = TurnPathConventions.Load(path);
                DesignVehicle rigid = c.Vehicle("rigid");
                Assert.Equal(2.5, rigid.EffectiveWidth);
                Assert.Equal(1.5, rigid.EffectiveFrontOverhang);
                Assert.Equal(2.5, rigid.EffectiveRearOverhang);
                Assert.Equal(0, rigid.CornerInset);            // square corners
                Assert.False(rigid.IsArticulated);

                DesignVehicle semi = c.Vehicle("SEMI");
                Assert.True(semi.IsArticulated);
                Assert.Equal(0.25, semi.Hitch);
                Assert.Equal("semi-trailer", semi.Trailers[0].Name);
                Assert.Equal(3.3, semi.Trailers[0].RearOverhang, 9);
                Assert.Equal(16.55, semi.OverallLength, 9);
                Assert.Contains("towing a 13 m semi-trailer (16.55 m overall; forwards only)", c.Describe());

                // It survives being saved (Save as defaults in the dialog).
                c.Save(path);
                Assert.Equal(8.5, TurnPathConventions.Load(path).Vehicle("SEMI").Trailers[0].Wheelbase);

                // The square truck simulates like any vehicle: on a 10 m turn the body sweeps between the
                // inner side at the rear axle and the outer front corner.
                TurnPathResult r = TurnPathSimulator.Simulate(TurnPathBuilder.Template(new TurnTemplate { Vehicle = rigid, Start = new Point2(0, 0), Heading = 0, TurnAngleDeg = 180, LeadIn = 0, LeadOut = 0 }));
                Assert.True(r.WithinMinimumRadius);
                Assert.Empty(r.Trailers);
            }
            finally
            {
                File.Delete(path);
            }

            // A trailer that doesn't add up is refused when the file is loaded.
            File.WriteAllText(path, json.Replace("\"length\": 13", "\"length\": 5"));
            try
            {
                var ex = Assert.Throws<InvalidDataException>(() => TurnPathConventions.Load(path));
                Assert.Contains("trailer", ex.Message);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ADriveKeepsTheTrailerFoldedFromLegToLegAndOnlyGoesForwards()
        {
            TurnPathConventions c = Conventions();
            var r = new ManoeuvreRecord
            {
                Vehicle = "TEST SEMI",
                Mode = "drive",
                Start = new[] { 0.0, 0 },
                HeadingDeg = 0,
                Radius = 12.5,
                Stops = { new DriveStop { Point = new[] { 20.0, 12 } }, new DriveStop { Point = new[] { 20.0, 40 } } },
            };
            ManoeuvrePlan plan = ManoeuvrePlanner.Plan(r, c);
            Assert.Equal(2, plan.Legs.Count);
            TurnPathResult first = plan.Legs[0].Result, second = plan.Legs[1].Result;
            Assert.Single(first.Trailers);
            VehiclePose left = first.Trailers[0].Poses[first.Poses.Count - 1], taken = second.Trailers[0].Poses[0];
            Assert.Equal(0, Dist(left.RearAxle, taken.RearAxle), 6);
            Assert.True(AngleDiff(left.Heading, taken.Heading) < 1e-9);
            Assert.True(AngleDiff(taken.Heading, second.Poses[0].Heading) > 0.1, "the second leg starts with the trailer still folded");

            // Both bodies are drawn at the end of each leg.
            List<DraftedEntity> drafted = ManoeuvrePlanner.Draft(plan, r, c);
            Assert.True(drafted.Count(d => d.Spec is CircleSpec) >= 3, "a kingpin at the start, and at the end of each leg");

            // A destination behind it would need reversing.
            r.Stops.Add(new DriveStop { Point = new[] { 20.0, 20 } });
            var ex = Assert.Throws<ArgumentException>(() => ManoeuvrePlanner.Plan(r, c));
            Assert.Contains("forwards", ex.Message);

            // Following a line in reverse, solving and the ground clearance check are for rigid vehicles.
            var reverse = new ManoeuvreRecord { Vehicle = "TEST SEMI", Mode = "follow", FollowHandle = "1", Reverse = true };
            Assert.Contains("reversing with a trailer", Assert.Throws<ArgumentException>(() =>
                ManoeuvrePlanner.Plan(reverse, c, _ => new LineSpec { Start = new Point2(0, 0), End = new Point2(30, 0) })).Message);
            var solve = new ManoeuvreRecord { Vehicle = "TEST SEMI", Mode = "solve", Start = new[] { 0.0, 0 }, Target = new[] { 0.0, 5 }, TargetHeadingDeg = 180, Radius = 12.5 };
            Assert.Contains("rigid vehicles", Assert.Throws<ArgumentException>(() => ManoeuvrePlanner.Solve(solve, c, (min, max) => new List<Obstruction>())).Message);
            Assert.Throws<ArgumentException>(() => ManoeuvreSolver.SolveWithFallback(new SolveRequest { Vehicle = Semi(), Min = new Point2(-5, -5), Max = new Point2(5, 5) }));
            var hump = new GroundProfile(new[] { new GroundPoint(0, 0), new GroundPoint(20, 1), new GroundPoint(40, 0) });
            Assert.Contains("rigid vehicles", Assert.Throws<ArgumentException>(() => GroundClearanceChecker.CheckProfile(hump, Semi())).Message);
        }

        [Fact]
        public void TheToolDrawsACombinationAndSaysHowFarItFolds()
        {
            var host = new InMemoryDrawingHost("Meters");
            var tools = new TurnPathTools(host, Conventions());
            using JsonDocument input = JsonDocument.Parse("{\"mode\":\"template\",\"vehicle\":\"TEST SEMI\",\"start\":[0,0],\"heading_deg\":0,\"radius\":12.5,\"turn_angle_deg\":90,\"lead_out\":20}");
            ToolOutcome outcome = tools.Execute(TurnPathTools.DrawTurnPath, input.RootElement);
            Assert.False(outcome.IsError, outcome.Content);
            using JsonDocument doc = JsonDocument.Parse(outcome.Content);
            Assert.Equal("semi-trailer", doc.RootElement.GetProperty("trailers")[0].GetString());
            Assert.InRange(doc.RootElement.GetProperty("max_articulation_deg").GetDouble(), 20, 80);
            Assert.True(doc.RootElement.GetProperty("within_minimum_radius").GetBoolean());
            Assert.Equal(2, host.Entities.Count(e => e.Spec is CircleSpec));     // the kingpin, at the start and at the end

            // Drive it through two points; a point behind it is skipped with the reason.
            using JsonDocument drive = JsonDocument.Parse("{\"mode\":\"points\",\"vehicle\":\"TEST SEMI\",\"points\":[[0,0],[25,0],[45,20],[30,20]],\"heading_deg\":0,\"radius\":12.5,\"dry_run\":true}");
            ToolOutcome driven = tools.Execute(TurnPathTools.DrawTurnPath, drive.RootElement);
            Assert.False(driven.IsError, driven.Content);
            using JsonDocument doc2 = JsonDocument.Parse(driven.Content);
            Assert.Equal(2, doc2.RootElement.GetProperty("legs").GetArrayLength());
            Assert.Contains(doc2.RootElement.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("points[3]"));

            // Reverse, and solving, are refused for it.
            using JsonDocument back = JsonDocument.Parse("{\"mode\":\"points\",\"vehicle\":\"TEST SEMI\",\"points\":[[0,0],[-20,0]],\"heading_deg\":0,\"reverse\":true}");
            Assert.True(tools.Execute(TurnPathTools.DrawTurnPath, back.RootElement).IsError);
            using JsonDocument solve = JsonDocument.Parse("{\"mode\":\"solve\",\"vehicle\":\"TEST SEMI\",\"start\":[0,0],\"heading_deg\":0,\"target\":[0,8],\"target_heading_deg\":180,\"obstruction_layers\":[\"KERB\"]}");
            ToolOutcome refused = tools.Execute(TurnPathTools.DrawTurnPath, solve.RootElement);
            Assert.True(refused.IsError);
            Assert.Contains("rigid vehicles", refused.Content);

            // A rigid vehicle's report doesn't mention trailers.
            using JsonDocument car = JsonDocument.Parse("{\"mode\":\"template\",\"start\":[0,0],\"heading_deg\":0,\"dry_run\":true}");
            using JsonDocument doc3 = JsonDocument.Parse(tools.Execute(TurnPathTools.DrawTurnPath, car.RootElement).Content);
            Assert.False(doc3.RootElement.TryGetProperty("trailers", out _));
        }
    }
}
