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
    /// <summary>AS/NZS 2890.1 B85/B99 swept paths: kinematics, envelopes, templates and the tool.</summary>
    public class TurnPathTests
    {
        private static TurnPathConventions Conventions() =>
            TurnPathConventions.Load(Path.Combine(AppContext.BaseDirectory, "turn-paths.json"));

        private static double Dist(Point2 a, Point2 b) => Segment.Dist(a, b);

        /// <summary>A long left turn so the rear axle settles into its steady-state circle.</summary>
        private static TurnPathResult SteadyTurn(DesignVehicle v, double radius, ReferencePoint reference = ReferencePoint.OuterFrontWheel) =>
            TurnPathSimulator.Simulate(TurnPathBuilder.Template(new TurnTemplate
            {
                Vehicle = v,
                Start = new Point2(0, 0),
                Heading = 0,
                Radius = radius,
                TurnAngleDeg = 270,
                Left = true,
                LeadIn = 0,
                LeadOut = 0,
                Reference = reference,
            }));

        // ------------------------------------------------------------- vehicles

        [Fact]
        public void StandardVehiclesMatchAppendixB()
        {
            DesignVehicle b99 = DesignVehicle.B99, b85 = DesignVehicle.B85;
            Assert.Equal(5.2, b99.Length);
            Assert.Equal(1.94, b99.Width);
            Assert.Equal(3.05, b99.Wheelbase);
            Assert.Equal(5.2, b99.FrontOverhang + b99.Wheelbase + b99.RearOverhang, 9);
            Assert.Equal(6.3, b99.MinTurnRadius);
            Assert.Equal(0.6, b99.ClearanceOutside);

            Assert.Equal(4.91, b85.Length);
            Assert.Equal(1.87, b85.Width);
            Assert.Equal(4.91, b85.FrontOverhang + b85.Wheelbase + b85.RearOverhang, 9);
            Assert.Equal(5.8, b85.MinTurnRadius);
            Assert.Equal(0.3, b85.ClearanceOutside);
            Assert.Empty(b99.Validate());
            Assert.Empty(b85.Validate());
        }

        [Fact]
        public void ShippedConventionsLoadAndMatchTheBuiltIns()
        {
            TurnPathConventions c = Conventions();
            Assert.Equal("B99", c.Vehicle(null).Name);
            foreach (DesignVehicle built in DesignVehicle.Standard)
            {
                DesignVehicle file = c.Vehicle(built.Name);
                Assert.Equal(built.Length, file.Length);
                Assert.Equal(built.Wheelbase, file.Wheelbase);
                Assert.Equal(built.EffectiveWidth, file.EffectiveWidth);
                Assert.Equal(built.MinTurnRadius, file.MinTurnRadius);
                Assert.Equal(built.ClearanceOutside, file.ClearanceOutside);
            }

            Assert.Throws<ArgumentException>(() => c.Vehicle("HRV"));
            Assert.Equal("AE CIV PLN - VEHICLE BLOCK", c.Layers.Block);
            Assert.False(c.Draw.Block);
            Assert.Contains(c.Layers.All(), kv => kv.Key == "block");
            Assert.True(c.LayerStyles.ContainsKey(c.Layers.Block));
            Assert.True(c.LayerStyles.ContainsKey(c.Layers.Clearance));
            Assert.Equal("DASHED", c.LayerStyles[c.Layers.Clearance].Linetype);
        }

        [Fact]
        public void ConventionsRoundTripThroughJson()
        {
            TurnPathConventions c = TurnPathConventions.Default();
            c.Layers.SweptPath = "MY-{vehicle}-SWEPT";
            c.Draw.Hatch = true;
            c.Draw.Block = true;
            c.Layers.Set("block", "BLK-{vehicle}");
            string path = Path.Combine(Path.GetTempPath(), "csdbridge-turn-paths-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                c.Save(path);
                TurnPathConventions back = TurnPathConventions.Load(path);
                Assert.Equal("MY-{vehicle}-SWEPT", back.Layers.SweptPath);
                Assert.Equal("MY-B85-SWEPT", back.Layers.Resolve("B85").SweptPath);
                Assert.True(back.Draw.Hatch);
                Assert.True(back.Draw.Block);
                Assert.Equal("BLK-B99", back.Layers.Resolve("B99").Block);
                Assert.Equal(2, back.Vehicles.Count);
                Assert.Equal(6.3, back.Vehicle("b99").MinTurnRadius);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void BodyOutlineIsTheStandardsShape()
        {
            DesignVehicle v = DesignVehicle.B99;
            var (pts, bulges) = v.BodyOutline();
            Assert.Equal(8, pts.Count);
            // Rounded corners: tangent to the side, through the effective corner.
            Assert.Equal(2 * Math.Atan2(0.05, v.CornerLength), v.CornerSweep, 9);
            Assert.Equal(Math.Tan(v.CornerSweep / 4), bulges[0], 9);
            Assert.True(bulges.All(b => b >= 0));
            // Nose and tail reach the overall length at the centreline: sagitta = overall - effective overhang.
            double frontSagitta = bulges[3] * v.EffectiveWidth / 2;
            double rearSagitta = bulges[7] * v.EffectiveWidth / 2;
            Assert.Equal(0.05, frontSagitta, 9);
            Assert.Equal(0.15, rearSagitta, 9);
            Assert.True(bulges[3] > 0 && bulges[7] > 0);
            Assert.Equal(v.Wheelbase + v.EffectiveFrontOverhang, pts.Max(p => p.X), 9);
            Assert.Equal(-v.EffectiveRearOverhang, pts.Min(p => p.X), 9);
            Assert.Equal(v.Width / 2, pts.Max(p => p.Y), 9);
        }

        // ----------------------------------------------------------- kinematics

        [Fact]
        public void SteadyTurnHasTheOuterFrontWheelOnTheNominatedRadius()
        {
            DesignVehicle v = DesignVehicle.B99;
            TurnPathResult r = SteadyTurn(v, 6.3);
            var arc = (ArcSegment)r.Path.Segments[0];
            Point2 centre = arc.Center;
            Assert.Equal(6.3, arc.Radius, 9);

            // Near the end of a 270 degree turn the tractrix has converged.
            VehiclePose last = r.Poses[r.Poses.Count - 1];
            double rr = v.RearAxleRadius(6.3);
            // (the tractrix converges asymptotically: within a few millimetres after 270 degrees)
            Assert.Equal(rr, Dist(last.RearAxle, centre), 2);
            Assert.Equal(6.3, Dist(last.ToWorld(v.OuterFrontWheel(left: true)), centre), 3);
            Assert.InRange(r.TightestRadius, 6.3 - 0.001, 6.32);
            Assert.True(r.WithinMinimumRadius);
            Assert.Empty(r.Warnings);
            // The heading lags the reference point's arc by the steady-state slip angle (about 29 degrees here).
            double slip = Math.Atan2(v.Wheelbase, rr + v.Track / 2) * 180 / Math.PI;
            Assert.Equal(270 - slip, r.NetTurnDeg, 0);
            Assert.InRange(r.MaxSteerAngleDeg, 30, 45);
        }

        [Fact]
        public void HeadingLagsThePathAndSettlesOnTheLeadOut()
        {
            DesignVehicle v = DesignVehicle.B99;
            TurnPathResult r = TurnPathSimulator.Simulate(TurnPathBuilder.Template(new TurnTemplate
            {
                Vehicle = v, Start = new Point2(10, 20), Heading = Math.PI / 2, Radius = 8, TurnAngleDeg = 90, Left = false, LeadIn = 2, LeadOut = 15,
            }));

            // Starts where asked, facing north; the first poses go straight.
            Assert.Equal(10, r.Poses[0].RearAxle.X, 9);
            Assert.Equal(20, r.Poses[0].RearAxle.Y, 9);
            Assert.Equal(Math.PI / 2, r.Poses[0].Heading, 9);
            VehiclePose during = r.PoseAt(1.0);
            Assert.Equal(Math.PI / 2, during.Heading, 6);

            // Ends facing east, with the rear axle on the lead-out line (within the settling tolerance).
            VehiclePose last = r.Poses[r.Poses.Count - 1];
            Assert.Equal(0, Math.Abs(((last.Heading + Math.PI) % (2 * Math.PI)) - Math.PI), 2);
            Assert.Equal(-90, r.NetTurnDeg, 0);
            Assert.True(r.WithinMinimumRadius);
        }

        [Fact]
        public void RearAxleReferenceFollowsThePathExactly()
        {
            DesignVehicle v = DesignVehicle.B85;
            TurnPathResult r = SteadyTurn(v, 5.8, ReferencePoint.RearAxle);
            var arc = (ArcSegment)r.Path.Segments[0];
            Assert.Equal(v.RearAxleRadius(5.8), arc.Radius, 9);
            foreach (VehiclePose p in r.Poses)
            {
                Assert.Equal(arc.Radius, Dist(p.RearAxle, arc.Center), 6);
            }

            Assert.Equal(5.8, r.TightestRadius, 3);
        }

        [Fact]
        public void TooTightATurnIsReported()
        {
            TurnPathResult r = SteadyTurn(DesignVehicle.B99, 5.0);
            Assert.False(r.WithinMinimumRadius);
            Assert.Contains(r.Warnings, w => w.Contains("tighter than"));
            Assert.InRange(r.TightestRadius, 4.99, 5.02);
        }

        // ------------------------------------------------------------- envelope

        [Fact]
        public void SteadyStateEnvelopeIsTheBodysExtremeCircles()
        {
            DesignVehicle v = DesignVehicle.B99;
            TurnPathResult r = SteadyTurn(v, 6.3);
            var arc = (ArcSegment)r.Path.Segments[0];
            Point2 c = arc.Center;
            double rr = v.RearAxleRadius(6.3);
            double inner = rr - v.Width / 2;                                       // inner side at the rear axle
            double xf = v.Wheelbase + v.EffectiveFrontOverhang, he = v.EffectiveWidth / 2;
            double outer = Math.Sqrt(xf * xf + (rr + he) * (rr + he));             // outer front effective corner

            // Second half of the turn: everything has settled. Left is inside, right outside.
            double along = arc.Length;
            var leftLate = r.LeftBody.Where(p => AngleAlong(c, arc, p) > 0.55 * along && AngleAlong(c, arc, p) < 0.95 * along).ToList();
            var rightLate = r.RightBody.Where(p => AngleAlong(c, arc, p) > 0.55 * along && AngleAlong(c, arc, p) < 0.95 * along).ToList();
            Assert.True(leftLate.Count > 10 && rightLate.Count > 10);
            foreach (Point2 p in leftLate)
            {
                Assert.InRange(Dist(p, c), inner - 0.002, inner + 0.012);
            }

            foreach (Point2 p in rightLate)
            {
                Assert.InRange(Dist(p, c), outer - 0.012, outer + 0.002);
            }

            // The outer body line is a little outside the nominated (wheel) radius, as the standard notes.
            Assert.True(outer > 6.3 && outer < 7.2);

            // Clearances: 300 mm inside, 600 mm outside for B99.
            foreach (Point2 p in r.LeftClearance.Where(p => AngleAlong(c, arc, p) > 0.55 * along && AngleAlong(c, arc, p) < 0.95 * along))
            {
                Assert.InRange(Dist(p, c), inner - 0.3 - 0.002, inner - 0.3 + 0.012);
            }

            foreach (Point2 p in r.RightClearance.Where(p => AngleAlong(c, arc, p) > 0.55 * along && AngleAlong(c, arc, p) < 0.95 * along))
            {
                Assert.InRange(Dist(p, c), outer + 0.6 - 0.012, outer + 0.6 + 0.002);
            }

            Assert.Equal(0.3, r.ClearanceLeft);
            Assert.Equal(0.6, r.ClearanceRight);
        }

        /// <summary>Arc length from the arc's start to the point's bearing from the centre (for ordering points round the turn).</summary>
        private static double AngleAlong(Point2 c, ArcSegment arc, Point2 p)
        {
            double a = Math.Atan2(p.Y - c.Y, p.X - c.X);
            double along = arc.AngleAlong(a);
            return along < 0 ? -1 : along * arc.Radius;
        }

        [Fact]
        public void EnvelopeStartsAtTheParkedBodyAndEndsAtTheFrontCorners()
        {
            DesignVehicle v = DesignVehicle.B99;
            TurnPathResult r = TurnPathSimulator.Simulate(TurnPathBuilder.Template(new TurnTemplate
            {
                Vehicle = v, Start = new Point2(0, 0), Heading = 0, Radius = 6.3, TurnAngleDeg = 90, Left = true, LeadIn = 3, LeadOut = 6,
            }));

            VehiclePose start = r.Poses[0], end = r.Poses[r.Poses.Count - 1];
            double xf = v.Wheelbase + v.EffectiveFrontOverhang, xr = -v.EffectiveRearOverhang, he = v.EffectiveWidth / 2;
            Assert.Equal(0, Dist(r.LeftBody[0], start.ToWorld(xr, he)), 1e-6);
            Assert.Equal(0, Dist(r.RightBody[0], start.ToWorld(xr, -he)), 1e-6);
            Assert.Equal(0, Dist(r.LeftBody[r.LeftBody.Count - 1], end.ToWorld(xf, he)), 1e-6);
            Assert.Equal(0, Dist(r.RightBody[r.RightBody.Count - 1], end.ToWorld(xf, -he)), 1e-6);

            // Along the lead-in the swept path is the body width, both sides straight.
            foreach (Point2 p in r.LeftBody.Where(p => p.X > 0.1 && p.X < 2.9))
            {
                Assert.Equal(v.Width / 2, p.Y, 6);
            }

            foreach (Point2 p in r.RightBody.Where(p => p.X > 0.1 && p.X < 2.9))
            {
                Assert.Equal(-v.Width / 2, p.Y, 6);
            }

            // The lines are monotone along the manoeuvre: no doubling back or self-crossing.
            Assert.True(IsSimple(r.LeftBody), "left envelope crosses itself");
            Assert.True(IsSimple(r.RightBody), "right envelope crosses itself");
            Assert.True(IsSimple(r.LeftClearance), "left clearance crosses itself");
            Assert.True(IsSimple(r.RightClearance), "right clearance crosses itself");

            // The closed outline has the body's end arcs and encloses roughly the expected area.
            PolylineSpec outline = r.ClosedOutline();
            Assert.True(outline.Closed);
            Assert.Equal(outline.Points.Count, outline.Bulges!.Count);
            Assert.Equal(2, outline.Bulges.Count(b => Math.Abs(b) > 1e-9));
            Assert.True(IsSimple(outline.Points), "closed outline crosses itself");
            Path2 loop = Path2.From(outline)!;
            Assert.InRange(loop.Area, 30, 60);
        }

        private static bool IsSimple(List<Point2> pts)
        {
            for (int i = 0; i < pts.Count - 1; i++)
            {
                for (int j = i + 2; j < pts.Count - 1; j++)
                {
                    var a = new Path2(new List<Segment> { new LineSegment(pts[i], pts[i + 1]) }, false);
                    var b = new Path2(new List<Segment> { new LineSegment(pts[j], pts[j + 1]) }, false);
                    if (Intersections.Find(a, b, extend: false).Count > 0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        [Fact]
        public void EnvelopeContainsEveryBodyPosition()
        {
            // The swept path must enclose the body at every pose: sample body corners and check they lie
            // between the left and right envelopes (never left of the left line or right of the right line).
            DesignVehicle v = DesignVehicle.B85;
            TurnPathResult r = TurnPathSimulator.Simulate(TurnPathBuilder.Template(new TurnTemplate
            {
                Vehicle = v, Start = new Point2(0, 0), Heading = 0.3, Radius = 5.8, TurnAngleDeg = 120, Left = false, LeadIn = 2, LeadOut = 5,
            }));

            var leftPath = new Path2(Segments(r.LeftBody), false);
            var rightPath = new Path2(Segments(r.RightBody), false);
            var (bodyPts, _) = v.BodyOutline();
            for (int i = 0; i < r.Poses.Count; i += 7)
            {
                foreach (VehiclePoint bp in bodyPts)
                {
                    Point2 w = r.Poses[i].ToWorld(bp);
                    // Signed side of the nearest envelope segment: the body must be on the inner side of each line.
                    Assert.True(SideOf(leftPath, w) <= 0.003, $"pose {i}: body point {w} is outside the left envelope");
                    Assert.True(SideOf(rightPath, w) >= -0.003, $"pose {i}: body point {w} is outside the right envelope");
                }
            }
        }

        [Fact]
        public void RearSwingOutIsPartOfTheOuterEnvelope()
        {
            // Turning right from a straight: the rear swings out to the left, so the left envelope must
            // bulge past the lead-in's straight side line before the front corner's trace takes over.
            DesignVehicle v = DesignVehicle.B99;
            TurnPathResult r = TurnPathSimulator.Simulate(TurnPathBuilder.Template(new TurnTemplate
            {
                Vehicle = v, Start = new Point2(0, 0), Heading = 0, Radius = 6.3, TurnAngleDeg = 90, Left = false, LeadIn = 3, LeadOut = 4,
            }));
            double hw = v.Width / 2;
            double maxBulge = r.LeftBody.Where(p => p.X > 1 && p.X < 4).Select(p => p.Y - hw).DefaultIfEmpty(0).Max();
            Assert.InRange(maxBulge, 0.005, 0.08);
            Assert.True(IsSimple(r.LeftBody));
        }

        private static List<Segment> Segments(List<Point2> pts)
        {
            var list = new List<Segment>();
            for (int i = 0; i < pts.Count - 1; i++)
            {
                list.Add(new LineSegment(pts[i], pts[i + 1]));
            }

            return list;
        }

        /// <summary>Positive when the point is to the left of the polyline's direction at its closest point.</summary>
        private static double SideOf(Path2 line, Point2 p)
        {
            var (closest, along, _) = line.Closest(p);
            double t = line.TangentAt(along);
            double nx = -Math.Sin(t), ny = Math.Cos(t);
            return (p.X - closest.X) * nx + (p.Y - closest.Y) * ny;
        }

        [Fact]
        public void ReverseTravelSwapsTheLeadingEnd()
        {
            DesignVehicle v = DesignVehicle.B99;
            var line = new Path2(new List<Segment> { new LineSegment(new Point2(0, 0), new Point2(0, 10)) }, false);
            TurnPathResult r = TurnPathSimulator.Simulate(new TurnPathRequest { Vehicle = v, Path = line, Reference = v.RearAxleCentre, Reverse = true });

            // Facing south while moving north; the envelope starts at the front corners and ends at the rear corners.
            Assert.Equal(1.5 * Math.PI, r.Poses[0].Heading, 9);
            double xf = v.Wheelbase + v.EffectiveFrontOverhang, xr = -v.EffectiveRearOverhang, he = v.EffectiveWidth / 2;
            Assert.Equal(0, Dist(r.LeftBody[0], r.Poses[0].ToWorld(xf, he)), 1e-6);
            Assert.Equal(0, Dist(r.LeftBody[r.LeftBody.Count - 1], r.Poses[r.Poses.Count - 1].ToWorld(xr, he)), 1e-6);
            Assert.Equal(0, r.NetTurnDeg, 6);
            Assert.True(double.IsPositiveInfinity(r.TightestRadius));

            // Facing south, the vehicle's left is at +X: the left clearance is further out that way.
            // (Away from the ends, where the lines wrap round the corners.)
            Assert.All(r.LeftBody.Where(p => p.Y > -3.5 && p.Y < 9.5), p => Assert.Equal(v.Width / 2, p.X, 6));
            Assert.All(r.LeftClearance.Where(p => p.Y > -3.5 && p.Y < 9.5), p => Assert.Equal(v.Width / 2 + 0.3, p.X, 6));
            Assert.All(r.RightClearance.Where(p => p.Y > -3.5 && p.Y < 9.5), p => Assert.Equal(-v.Width / 2 - 0.3, p.X, 6));
        }

        [Fact]
        public void SCurveKeepsTheEnvelopeOutsideTheBody()
        {
            // Left then right: the extreme corner switches sides at the inflection. The clearance offsets
            // must stay outside the body lines everywhere.
            DesignVehicle v = DesignVehicle.B99;
            Path2 path = TurnPathBuilder.FromPoints(new[] { new Point2(0, 0), new Point2(12, 0), new Point2(20, 8), new Point2(32, 8) }, 8);
            TurnPathResult r = TurnPathSimulator.Simulate(new TurnPathRequest { Vehicle = v, Path = path, Reference = v.FrontAxleCentre });
            Assert.True(r.WithinMinimumRadius);
            Assert.True(IsSimple(r.LeftBody));
            Assert.True(IsSimple(r.RightBody));

            var leftBody = new Path2(Segments(r.LeftBody), false);
            var rightBody = new Path2(Segments(r.RightBody), false);
            foreach (Point2 p in r.LeftClearance)
            {
                Assert.InRange(leftBody.Closest(p).Distance, 0.299, 0.62);
            }

            foreach (Point2 p in r.RightClearance)
            {
                Assert.InRange(rightBody.Closest(p).Distance, 0.299, 0.62);
            }
        }

        // ------------------------------------------------------------- builders

        [Fact]
        public void TemplatePathIsLeadInArcLeadOut()
        {
            DesignVehicle v = DesignVehicle.B99;
            TurnPathRequest req = TurnPathBuilder.Template(new TurnTemplate
            {
                Vehicle = v, Start = new Point2(5, 5), Heading = 0, Radius = 6.3, TurnAngleDeg = 90, Left = true, LeadIn = 2, LeadOut = 4,
            });
            Assert.Equal(3, req.Path.Segments.Count);
            var lead = Assert.IsType<LineSegment>(req.Path.Segments[0]);
            var arc = Assert.IsType<ArcSegment>(req.Path.Segments[1]);
            var tail = Assert.IsType<LineSegment>(req.Path.Segments[2]);
            // The outer (right) front wheel starts the path.
            Assert.Equal(5 + v.Wheelbase, lead.P.X, 9);
            Assert.Equal(5 - v.Track / 2, lead.P.Y, 9);
            Assert.Equal(2, lead.Length, 9);
            Assert.Equal(6.3, arc.Radius, 9);
            Assert.Equal(Math.PI / 2, arc.Sweep, 9);
            Assert.Equal(4, tail.Length, 9);
            Assert.Equal(Math.PI / 2, Math.Atan2(tail.Q.Y - tail.P.Y, tail.Q.X - tail.P.X), 9);

            Assert.Throws<ArgumentException>(() => TurnPathBuilder.Template(new TurnTemplate { Vehicle = v, Radius = 2 }));
        }

        [Fact]
        public void PointsPathGetsTangentCornerArcsAndShrinksThemWhenShort()
        {
            var warnings = new List<string>();
            Path2 p = TurnPathBuilder.FromPoints(new[] { new Point2(0, 0), new Point2(10, 0), new Point2(10, 10) }, 4, warnings);
            Assert.Equal(3, p.Segments.Count);
            var arc = Assert.IsType<ArcSegment>(p.Segments[1]);
            Assert.Equal(4, arc.Radius, 9);
            Assert.Equal(Math.PI / 2, arc.Sweep, 9);
            Assert.Equal(0, Dist(arc.Start, new Point2(6, 0)), 1e-9);
            Assert.Equal(0, Dist(arc.End, new Point2(10, 4)), 1e-9);
            Assert.Empty(warnings);

            Path2 tight = TurnPathBuilder.FromPoints(new[] { new Point2(0, 0), new Point2(3, 0), new Point2(3, 10) }, 6, warnings);
            var small = Assert.IsType<ArcSegment>(tight.Segments[0]);
            Assert.Equal(3, small.Radius, 9); // limited by the 3 m first leg
            Assert.Single(warnings);

            Assert.Throws<ArgumentException>(() => TurnPathBuilder.FromPoints(new[] { new Point2(0, 0), new Point2(5, 0), new Point2(0, 0) }, 2));
        }

        [Fact]
        public void DriveToTurnsAtTheRadiusThenGoesStraight()
        {
            DesignVehicle v = DesignVehicle.B99;
            var start = new VehiclePose(new Point2(0, 0), 0); // facing east, rear axle at the origin
            TurnPathBuilder.DriveLeg leg = TurnPathBuilder.DriveTo(v, start, new Point2(20, 12))!;
            Assert.False(leg.Reverse);
            Assert.True(leg.Left);
            Assert.Equal(2, leg.Request.Path.Segments.Count);
            var arc = Assert.IsType<ArcSegment>(leg.Request.Path.Segments[0]);
            var line = Assert.IsType<LineSegment>(leg.Request.Path.Segments[1]);
            // The front axle centre starts the path and arrives at the destination.
            Assert.Equal(0, Dist(leg.Request.Path.Start, new Point2(v.Wheelbase, 0)), 1e-9);
            Assert.Equal(0, Dist(leg.Request.Path.End, new Point2(20, 12)), 1e-9);
            Assert.Equal(v.RadiusOfPoint(v.MinTurnRadius, v.FrontAxleCentre, true), arc.Radius, 9);
            // The straight is tangent to the arc: same direction at the join.
            Assert.Equal(arc.TangentAt(arc.Length), Math.Atan2(line.Q.Y - line.P.Y, line.Q.X - line.P.X), 6);

            // Driving it, the vehicle ends at the destination with the wheels never past full lock.
            TurnPathResult r = TurnPathSimulator.Simulate(leg.Request);
            Assert.True(r.WithinMinimumRadius);
            Assert.Equal(0, Dist(r.Poses[r.Poses.Count - 1].ToWorld(v.FrontAxleCentre), new Point2(20, 12)), 0.03);
        }

        [Fact]
        public void DriveToReversesForPointsBehindAndGoesRoundForPointsInsideTheCircle()
        {
            DesignVehicle v = DesignVehicle.B99;
            var start = new VehiclePose(new Point2(0, 0), Math.PI / 2); // facing north

            TurnPathBuilder.DriveLeg back = TurnPathBuilder.DriveTo(v, start, new Point2(2, -8))!;
            Assert.True(back.Reverse);
            Assert.Equal(0, Dist(back.Request.Path.Start, new Point2(0, 0)), 1e-9); // the rear axle drives in reverse
            Assert.Equal(0, Dist(back.Request.Path.End, new Point2(2, -8)), 1e-9);
            TurnPathResult rb = TurnPathSimulator.Simulate(back.Request);
            Assert.True(rb.Reverse);
            Assert.Equal(0, Dist(rb.Poses[rb.Poses.Count - 1].RearAxle, new Point2(2, -8)), 0.03);

            // A point just to the left, inside the left turning circle: the car turns right, most of the way round.
            TurnPathBuilder.DriveLeg around = TurnPathBuilder.DriveTo(v, start, new Point2(-2, v.Wheelbase + 1))!;
            Assert.False(around.Left);
            Assert.True(around.ArcDeg > 180);

            // Too close to move: nothing. Too tight a radius: refused.
            Assert.Null(TurnPathBuilder.DriveTo(v, start, new Point2(0, v.Wheelbase)));
            Assert.Throws<ArgumentException>(() => TurnPathBuilder.DriveTo(v, start, new Point2(10, 10), 4));
            // A forced direction is honoured.
            Assert.True(TurnPathBuilder.DriveTo(v, start, new Point2(10, 10), reverse: true)!.Reverse);
        }

        [Fact]
        public void ThreePointTurnLegsChainFromEachOthersEndPose()
        {
            DesignVehicle v = DesignVehicle.B99;
            var pose = new VehiclePose(new Point2(0, 0), 0);
            var targets = new[] { new Point2(8, 4), new Point2(4, 2), new Point2(0, 6) };
            var results = new List<TurnPathResult>();
            foreach (Point2 t in targets)
            {
                TurnPathBuilder.DriveLeg leg = TurnPathBuilder.DriveTo(v, pose, t)!;
                TurnPathResult r = TurnPathSimulator.Simulate(leg.Request);
                Assert.True(r.WithinMinimumRadius);
                results.Add(r);
                pose = r.Poses[r.Poses.Count - 1];
            }

            Assert.False(results[0].Reverse);
            Assert.True(results[1].Reverse);
            // Each leg keeps its own swept path; the middle one starts where the first ended.
            Assert.Equal(0, Dist(results[1].Poses[0].RearAxle, results[0].Poses[results[0].Poses.Count - 1].RearAxle), 1e-9);
            Assert.All(results, r => Assert.True(r.LeftBody.Count > 3 && r.RightBody.Count > 3));
        }

        [Fact]
        public void CurveIsRunFromTheEndNearestThePick()
        {
            var spec = new LineSpec { Start = new Point2(0, 0), End = new Point2(20, 0) };
            Assert.Equal(20, TurnPathBuilder.FromCurve(spec, new Point2(19, 1)).Start.X, 9);
            Assert.Equal(0, TurnPathBuilder.FromCurve(spec, new Point2(1, 1)).Start.X, 9);
            Assert.Equal(0, TurnPathBuilder.FromCurve(spec, null).Start.X, 9);
            Assert.Throws<ArgumentException>(() => TurnPathBuilder.FromCurve(new TextSpec(), null));
        }

        // --------------------------------------------------------------- clean-up

        [Fact]
        public void OffsetChainRoundsConvexCornersAndMitresConcaveOnes()
        {
            var pts = new List<Point2> { new Point2(0, 0), new Point2(10, 0), new Point2(10, 10) };
            // Left of travel: east then north turns left, so the left offset is the inside: a mitre at (9, 1).
            List<Point2> left = TurnPathSimulator.OffsetChain(pts, 1, leftSide: true);
            Assert.Contains(left, p => Dist(p, new Point2(9, 1)) < 1e-9);
            Assert.Equal(3, left.Count);
            // Right of travel is the outside: a quarter-circle of radius 1 about the corner.
            List<Point2> right = TurnPathSimulator.OffsetChain(pts, 1, leftSide: false);
            Assert.True(right.Count > 10);
            foreach (Point2 p in right.Skip(1).Take(right.Count - 2))
            {
                Assert.Equal(1, Dist(p, new Point2(10, 0)), 9);
            }

            Assert.Equal(0, Dist(right[0], new Point2(0, -1)), 1e-9);
            Assert.Equal(0, Dist(right[right.Count - 1], new Point2(11, 10)), 1e-9);
        }

        [Fact]
        public void CleanUpRemovesBacktracksAndSmallLoops()
        {
            var back = new List<Point2> { new Point2(0, 0), new Point2(5, 0), new Point2(2, 0), new Point2(3, 0), new Point2(8, 0), new Point2(8, 5) };
            List<Point2> cleaned = TurnPathSimulator.RemoveBacktracks(back, 1e-6);
            Assert.Equal(new[] { 0.0, 5, 8, 8 }, cleaned.Select(p => p.X).ToArray());
            // A short spike forward then a longer move back: the spike goes.
            var spike = new List<Point2> { new Point2(0, 0), new Point2(1, 0), new Point2(-5, 0), new Point2(-5, 3) };
            Assert.Equal(new[] { 0.0, -5, -5 }, TurnPathSimulator.RemoveBacktracks(spike, 1e-6).Select(p => p.X).ToArray());

            var loop = new List<Point2> { new Point2(0, 0), new Point2(4, 0), new Point2(4, 1), new Point2(3, 1), new Point2(3, -1), new Point2(10, -1) };
            List<Point2> noLoop = TurnPathSimulator.RemoveLoops(loop, 100);
            Assert.Equal(4, noLoop.Count);
            Assert.Equal(0, Dist(noLoop[1], new Point2(3, 0)), 1e-9);
            Assert.Equal(0, Dist(noLoop[2], new Point2(3, -1)), 1e-9);

            // A loop longer than the limit is left alone.
            Assert.Equal(6, TurnPathSimulator.RemoveLoops(loop, 1).Count);

            var zig = new List<Point2> { new Point2(0, 0), new Point2(1, 0.0004), new Point2(2, 0), new Point2(3, 0.0004), new Point2(4, 0) };
            Assert.Equal(2, TurnPathSimulator.Simplify(zig, 0.001).Count);
        }

        // --------------------------------------------------------------- drafter

        [Fact]
        public void DrafterPutsEachPartOnItsLayerAndSplitsLongPolylines()
        {
            TurnPathConventions c = Conventions();
            DesignVehicle v = c.Vehicle("B99");
            TurnPathResult r = SteadyTurn(v, 6.3);
            TurnPathStyle style = TurnPathStyle.From(c);
            style.Layers.SweptPath = "SWEPT {vehicle}";
            style.Draw.WheelPaths = true;
            style.Draw.Hatch = true;
            style.OutlineInterval = 5;
            List<DraftedEntity> drafted = TurnPathDrafter.Draw(r, style);

            Assert.All(drafted.Where(d => d.Group == "swept_path"), d => Assert.Equal("SWEPT B99", d.Spec.Layer));
            Assert.All(drafted.Where(d => d.Group == "clearance"), d => Assert.Equal(c.Layers.Clearance, d.Spec.Layer));
            Assert.Equal(4, drafted.Count(d => d.Group == "wheel_paths"));
            Assert.Single(drafted, d => d.Group == "hatch");
            Assert.Single(drafted, d => d.Group == "labels");
            Assert.Single(drafted, d => d.Group == "path");
            Assert.True(drafted.Count(d => d.Group == "vehicle_intermediate") >= 3);
            // Start and end: body + 4 wheels + 2 axle lines each.
            Assert.Equal(14, drafted.Count(d => d.Group == "vehicle"));
            Assert.All(drafted.Select(d => d.Spec).OfType<PolylineSpec>(), p => Assert.True(p.Points.Count <= TurnPathDrafter.MaxPolylinePoints));
            var label = Assert.IsType<TextSpec>(drafted.Single(d => d.Group == "labels").Spec);
            Assert.Equal("B99", label.Text);
            Assert.Equal(0.3, label.Height);

            // Missing layers are created with the conventions' styles; existing ones are left alone.
            List<LayerSpec> missing = TurnPathDrafter.MissingLayers(
                drafted.Select(d => d.Spec.Layer!), new[] { c.Layers.Clearance }, c.LayerStyles);
            Assert.DoesNotContain(missing, m => m.Name == c.Layers.Clearance);
            Assert.Contains(missing, m => m.Name == "SWEPT B99");
            Assert.Equal("DASHDOT", missing.Single(m => m.Name == c.Layers.Path).Linetype);
        }

        // ------------------------------------------------------------------ tool

        private static JsonDocument Run(TurnPathTools tools, string json, out ToolOutcome outcome)
        {
            using JsonDocument input = JsonDocument.Parse(json);
            outcome = tools.Execute(TurnPathTools.DrawTurnPath, input.RootElement);
            Assert.False(outcome.IsError, outcome.Content);
            return JsonDocument.Parse(outcome.Content);
        }

        [Fact]
        public void ToolDrawsATemplateOnTheConfiguredLayers()
        {
            var host = new InMemoryDrawingHost("Meters");
            var tools = new TurnPathTools(host, Conventions());
            using JsonDocument doc = Run(tools, "{\"mode\":\"template\",\"vehicle\":\"B85\",\"start\":[100,200],\"heading_deg\":90,\"direction\":\"right\",\"turn_angle_deg\":90," +
                                               "\"layers\":{\"swept_path\":\"C-SWEPT\"},\"draw\":{\"wheel_paths\":true}}", out ToolOutcome outcome);
            JsonElement root = doc.RootElement;
            Assert.Equal("B85", root.GetProperty("vehicle").GetString());
            Assert.True(root.GetProperty("within_minimum_radius").GetBoolean());
            // A 90 degree turn from rest doesn't quite reach the steady-state radius.
            Assert.InRange(root.GetProperty("tightest_radius_outer_front_wheel").GetDouble(), 5.8, 6.3);
            Assert.Equal(5.8, root.GetProperty("nominated_radius").GetDouble());
            Assert.InRange(root.GetProperty("net_turn_deg").GetDouble(), -90, -84); // still settling on the 6 m lead-out
            Assert.Equal(0.3, root.GetProperty("clearances").GetProperty("left").GetDouble()); // right turn: left is outside, B85 = 0.3 both
            JsonElement drawn = root.GetProperty("drawn");
            Assert.Equal(2, drawn.GetProperty("swept_path").GetArrayLength());
            Assert.Equal(2, drawn.GetProperty("clearance").GetArrayLength());
            Assert.Equal(4, drawn.GetProperty("wheel_paths").GetArrayLength());
            Assert.Equal("C-SWEPT", root.GetProperty("layers").GetProperty("swept_path").GetString());
            Assert.Contains("C-SWEPT", root.GetProperty("created_layers").EnumerateArray().Select(e => e.GetString()));
            Assert.Contains(host.Entities, e => e.Layer == "C-SWEPT");
            Assert.Contains(host.Entities, e => e.Layer == "AE CIV PLN - VEHICLE CLEARANCE");
            Assert.Contains("B85", outcome.Summary);
        }

        [Fact]
        public void ToolFollowsAnExistingPolylineAndWarnsWhenTooTight()
        {
            var host = new InMemoryDrawingHost("Meters");
            // A 4 m radius corner: too tight for a B99 (6.3 m at the wheel).
            Path2 tight = TurnPathBuilder.FromPoints(new[] { new Point2(0, 0), new Point2(15, 0), new Point2(15, 15) }, 4);
            PolylineSpec pl = PathOps.ToPolyline(tight);
            pl.Layer = "KERB";
            string handle = host.CreateEntities(new[] { pl })[0].Handle!;

            var tools = new TurnPathTools(host, Conventions());
            using JsonDocument doc = Run(tools, $"{{\"mode\":\"follow\",\"handle\":\"{handle}\",\"dry_run\":true}}", out ToolOutcome outcome);
            JsonElement root = doc.RootElement;
            Assert.True(root.GetProperty("dry_run").GetBoolean());
            Assert.False(root.GetProperty("within_minimum_radius").GetBoolean());
            Assert.Contains(root.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("tighter than"));
            Assert.Single(host.Entities); // dry run drew nothing

            // From the other end, in reverse, drawing this time.
            using JsonDocument doc2 = Run(tools, $"{{\"mode\":\"follow\",\"handle\":\"{handle}\",\"start\":[15,15],\"reverse\":true,\"radius\":8}}", out _);
            Assert.Equal(15, doc2.RootElement.GetProperty("start").GetProperty("rear_axle")[0].GetDouble(), 3);
            Assert.True(doc2.RootElement.GetProperty("reverse").GetBoolean());
            Assert.True(host.Entities.Count > 10);
        }

        [Fact]
        public void ToolBlockOptionIsReportedWhenTheHostCantMakeBlocks()
        {
            var host = new InMemoryDrawingHost("Meters");
            var tools = new TurnPathTools(host, Conventions());
            using JsonDocument doc = Run(tools, "{\"mode\":\"template\",\"start\":[0,0],\"heading_deg\":0,\"draw\":{\"block\":true}}", out _);
            Assert.False(doc.RootElement.TryGetProperty("block", out _));
            Assert.Contains(doc.RootElement.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("can't make blocks"));
            Assert.True(host.Entities.Count > 5); // still drawn
        }

        [Fact]
        public void ToolPointsModeDrivesLegByLeg()
        {
            var host = new InMemoryDrawingHost("Meters");
            var tools = new TurnPathTools(host, Conventions());
            // Start at the origin facing east; forward to (10,6), reverse to (6,4), forward to (12,12).
            using JsonDocument doc = Run(tools, "{\"mode\":\"points\",\"points\":[[0,0],[10,6],[6,4],[12,12]],\"heading_deg\":0}", out _);
            JsonElement legs = doc.RootElement.GetProperty("legs");
            Assert.Equal(3, legs.GetArrayLength());
            Assert.Contains("forward", legs[0].GetString());
            Assert.Contains("reverse", legs[1].GetString());
            Assert.True(doc.RootElement.GetProperty("within_minimum_radius").GetBoolean());
            // Three legs: three end vehicles plus the start vehicle (7 entities each), one label.
            Assert.Equal(4 * 7, doc.RootElement.GetProperty("drawn").GetProperty("vehicle").GetArrayLength());
            Assert.Equal(1, doc.RootElement.GetProperty("drawn").GetProperty("labels").GetArrayLength());
            Assert.True(doc.RootElement.GetProperty("drawn").GetProperty("swept_path").GetArrayLength() >= 6); // two lines per leg, long ones split
        }

        [Fact]
        public void ToolRejectsBadInput()
        {
            var tools = new TurnPathTools(new InMemoryDrawingHost("Meters"), Conventions());
            foreach (string bad in new[]
            {
                "{\"mode\":\"template\"}",
                "{\"mode\":\"template\",\"start\":[0,0],\"heading_deg\":0,\"vehicle\":\"TRUCK\"}",
                "{\"mode\":\"follow\",\"handle\":\"ZZZ\"}",
                "{\"mode\":\"points\",\"points\":[[0,0]]}",
                "{\"mode\":\"points\",\"points\":[[0,0],[10,0]],\"radius\":3}",
                "{\"mode\":\"template\",\"start\":[0,0],\"heading_deg\":0,\"layers\":{\"kerb\":\"X\"}}",
                "{\"mode\":\"sideways\"}",
            })
            {
                using JsonDocument input = JsonDocument.Parse(bad);
                ToolOutcome outcome = tools.Execute(TurnPathTools.DrawTurnPath, input.RootElement);
                Assert.True(outcome.IsError, bad);
            }

            var none = new TurnPathTools(new InMemoryDrawingHost("Meters"), null, "missing file");
            using JsonDocument ok = JsonDocument.Parse("{\"mode\":\"template\",\"start\":[0,0],\"heading_deg\":0}");
            Assert.True(none.Execute(TurnPathTools.DrawTurnPath, ok.RootElement).IsError);
            Assert.Contains("missing file", none.Execute(TurnPathTools.DrawTurnPath, ok.RootElement).Content);
        }

        [Fact]
        public void ToolDefinitionsAreValidJsonSchema()
        {
            var tools = new TurnPathTools(new InMemoryDrawingHost("Meters"), Conventions());
            foreach (ToolDefinition t in tools.Tools)
            {
                using JsonDocument schema = JsonDocument.Parse(t.InputSchemaJson);
                Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
            }

            Assert.Contains("B99", tools.PromptSection);
            Assert.Contains("6.3", tools.PromptSection);
        }
    }
}
