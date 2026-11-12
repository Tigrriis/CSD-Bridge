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
    /// <summary>
    /// The Austroads design vehicles shipped in turn-paths.json, checked against the dimensions on
    /// Figures 2.3, 2.4, 2.7, 2.9 and 2.10 of AP-G34-23 (Design Vehicles and Turning Path Templates
    /// Guide, Edition 4.0). Every figure's chain of dimensions has to add up to its overall length.
    /// </summary>
    public class AustroadsVehicleTests
    {
        private static TurnPathConventions Conventions() =>
            TurnPathConventions.Load(Path.Combine(AppContext.BaseDirectory, "turn-paths.json"));

        private static double Dist(Point2 a, Point2 b) => Segment.Dist(a, b);

        [Fact]
        public void TheRigidTrucksAreAsDimensioned()
        {
            TurnPathConventions c = Conventions();

            // Figure 2.3: 2300 behind the rear axle, 5000 wheelbase, 1500 ahead of the front axle, 2500 wide.
            DesignVehicle service = c.Vehicle("Service 8.8 m");
            Assert.Equal(2.3, service.RearOverhang);
            Assert.Equal(5.0, service.Wheelbase);
            Assert.Equal(1.5, service.FrontOverhang);
            Assert.Equal(8.8, service.RearOverhang + service.Wheelbase + service.FrontOverhang, 9);
            Assert.Equal(8.8, service.Length);
            Assert.Equal(2.5, service.Width);
            Assert.Equal(9, service.MinTurnRadius);          // the smallest template: 9 m radius
            Assert.Equal(1, service.RearAxles);

            // Figure 2.4: 3450 behind the centre of the rear pair, 6850 to the front axle, 2200 ahead of it.
            DesignVehicle truck = c.Vehicle("Truck 12.5 m");
            Assert.Equal(3.45, truck.RearOverhang);
            Assert.Equal(6.85, truck.Wheelbase);
            Assert.Equal(2.2, truck.FrontOverhang);
            Assert.Equal(12.5, truck.RearOverhang + truck.Wheelbase + truck.FrontOverhang, 9);
            Assert.Equal(12.5, truck.Length);
            Assert.Equal(12.5, truck.MinTurnRadius);
            Assert.Equal(2, truck.RearAxles);
            Assert.Equal(1.3, truck.AxleSpacing);

            foreach (DesignVehicle v in new[] { service, truck })
            {
                Assert.Empty(v.Validate());
                Assert.False(v.IsArticulated);
                // Square bodies: they sweep with their real corners. Clearance 0.5 m (Section 3.4.1).
                Assert.Equal(v.Width, v.EffectiveWidth);
                Assert.Equal(v.FrontOverhang, v.EffectiveFrontOverhang);
                Assert.Equal(v.RearOverhang, v.EffectiveRearOverhang);
                Assert.Equal(0.5, v.ClearanceInside);
                Assert.Equal(0.5, v.ClearanceOutside);
                Assert.Equal(v.Length, v.OverallLength, 9);
            }
        }

        [Fact]
        public void TheCombinationsAddUpToTheirOverallLengths()
        {
            TurnPathConventions c = Conventions();

            // Figure 2.7: 1600 + 5300 to the centre of the drive axles; kingpin 200 ahead of it; trailer
            // 1400 ahead of the kingpin, 9500 to the centre of its three axles, 2800 behind them.
            DesignVehicle semi = c.Vehicle("Semi 19 m");
            Assert.Equal(1.6, semi.FrontOverhang);
            Assert.Equal(5.3, semi.Wheelbase);
            Assert.Equal(0.2, semi.Hitch);
            TrailerUnit trailer = Assert.Single(semi.Trailers);
            Assert.Equal(1.4, trailer.FrontOverhang);
            Assert.Equal(9.5, trailer.Wheelbase);
            Assert.Equal(2.8, trailer.RearOverhang);
            Assert.Equal(13.7, trailer.Length, 9);
            Assert.Equal(3, trailer.Axles);
            Assert.Equal(19.0, semi.OverallLength, 9);
            Assert.Equal(1.6 + 5.3 - 0.2 + 9.5 + 2.8, semi.OverallLength, 9);

            // Figure 2.9: 1000 + 4000; kingpin 400 ahead of the drive axles; A-trailer 900 / 8100 with the
            // B-trailer's kingpin over its axles; B-trailer 200 / 9400 / 2900.
            DesignVehicle b25 = c.Vehicle("B-double 25 m");
            Assert.Equal(1.0, b25.FrontOverhang);
            Assert.Equal(4.0, b25.Wheelbase);
            Assert.Equal(0.4, b25.Hitch);
            Assert.Equal(2, b25.Trailers.Count);
            Assert.Equal(0.9, b25.Trailers[0].FrontOverhang);
            Assert.Equal(8.1, b25.Trailers[0].Wheelbase);
            Assert.Equal(0, b25.Trailers[0].Hitch);
            Assert.Equal(0.2, b25.Trailers[1].FrontOverhang);
            Assert.Equal(9.4, b25.Trailers[1].Wheelbase);
            Assert.Equal(2.9, b25.Trailers[1].RearOverhang);
            Assert.Equal(12.5, b25.Trailers[1].Length, 9);
            Assert.Equal(25.0, b25.OverallLength, 9);
            Assert.Equal(1.0 + 4.0 - 0.4 + 8.1 + 9.4 + 2.9, b25.OverallLength, 9);

            // Figure 2.10: 1400 + 4500 with the kingpin over the drive axles; A-trailer 1400 / 8050;
            // B-trailer 450 / 9150 / 2900 (12500 long).
            DesignVehicle b26 = c.Vehicle("B-double 26 m");
            Assert.Equal(1.4, b26.FrontOverhang);
            Assert.Equal(4.5, b26.Wheelbase);
            Assert.Equal(0, b26.Hitch);
            Assert.Equal(1.4, b26.Trailers[0].FrontOverhang);
            Assert.Equal(8.05, b26.Trailers[0].Wheelbase);
            Assert.Equal(0.45, b26.Trailers[1].FrontOverhang);
            Assert.Equal(9.15, b26.Trailers[1].Wheelbase);
            Assert.Equal(2.9, b26.Trailers[1].RearOverhang);
            Assert.Equal(12.5, b26.Trailers[1].Length, 9);
            Assert.Equal(26.0, b26.OverallLength, 9);
            Assert.Equal(1.4 + 4.5 + 8.05 + 9.15 + 2.9, b26.OverallLength, 9);

            foreach (DesignVehicle v in new[] { semi, b25, b26 })
            {
                Assert.Empty(v.Validate());
                Assert.True(v.IsArticulated);
                Assert.Equal(2.5, v.Width);
                Assert.All(v.Trailers, t => Assert.Equal(2.5, t.Width));
                Assert.Equal(12.5, v.MinTurnRadius);          // the smallest template: 12.5 m radius
                Assert.Equal(70, v.MaxArticulationDeg);       // Section 3.5
                Assert.Equal(0.5, v.ClearanceInside);
            }

            Assert.Contains("towing a 13.7 m semi-trailer (19 m overall; forwards only)", c.Describe());
        }

        /// <summary>The swept width of a left turn: the outer edge's radius less the inner edge's, about the turn centre.</summary>
        private static double SweptWidth(TurnPathResult r)
        {
            Point2 centre = r.Path.Segments.OfType<ArcSegment>().First().Center;
            double inner = r.LeftBody.Min(p => Dist(p, centre));
            double outer = r.RightBody.Where(p => p.X > centre.X - 2 && p.Y < centre.Y + 2).Max(p => Dist(p, centre));
            return outer - inner;
        }

        private static TurnPathResult Turn(DesignVehicle v, double radius, double angle) =>
            TurnPathSimulator.Simulate(TurnPathBuilder.Template(new TurnTemplate
            {
                Vehicle = v,
                Start = new Point2(0, 0),
                Heading = 0,
                Radius = radius,
                TurnAngleDeg = angle,
                Left = true,
                LeadIn = 0,
                LeadOut = 30,
            }));

        [Fact]
        public void EachTurnsAtItsSmallestTemplateRadius()
        {
            TurnPathConventions c = Conventions();
            var widths = new Dictionary<string, double>();
            foreach (string name in new[] { "Service 8.8 m", "Truck 12.5 m", "Semi 19 m", "B-double 25 m", "B-double 26 m" })
            {
                DesignVehicle v = c.Vehicle(name);
                TurnPathResult r = Turn(v, v.MinTurnRadius, 90);
                Assert.True(r.WithinMinimumRadius, name);
                Assert.Empty(r.Warnings);
                Assert.Equal(90, r.NetTurnDeg, 0);
                // A long wheelbase is still settling into the turn after 90 degrees, so the tightest
                // turn the body makes is at, or a little easier than, the radius its front wheel follows.
                Assert.InRange(r.TightestRadius, v.MinTurnRadius - 0.01, v.MinTurnRadius * 1.15);
                Assert.Equal(v.Trailers.Count, r.Trailers.Count);
                widths[name] = SweptWidth(r);

                // The combinations stay inside the 70 degree limit even through a half turn at this radius.
                if (v.IsArticulated)
                {
                    Assert.InRange(r.MaxArticulationDeg, 25, 45);
                    TurnPathResult half = Turn(v, v.MinTurnRadius, 180);
                    Assert.InRange(half.MaxArticulationDeg, 45, 70);
                    Assert.Empty(half.Warnings);
                }
            }

            // The longer the vehicle, the wider it sweeps.
            Assert.InRange(widths["Service 8.8 m"], 4.5, 4.9);
            Assert.InRange(widths["Truck 12.5 m"], 5.3, 5.7);
            Assert.True(widths["Semi 19 m"] > widths["Truck 12.5 m"] + 1.5);
            Assert.True(widths["B-double 25 m"] > widths["Semi 19 m"] + 0.8);
            Assert.True(widths["B-double 26 m"] > widths["B-double 25 m"]);

            // A cross-check from outside the guide. The guide's Table 2.1 names the 19 m semi-trailer
            // as the prescriptive vehicle for PBS Level 1 and the 26 m B-double for Level 2; the PBS
            // scheme limits the swept path in a 90 degree turn at 12.5 m to 7.4 m and 8.7 m for
            // those levels. Each vehicle should sit just inside its limit.
            Assert.InRange(widths["Semi 19 m"], 7.1, 7.4);
            Assert.InRange(widths["B-double 26 m"], 8.4, 8.7);

            // A larger radius sweeps a narrower path.
            Assert.True(SweptWidth(Turn(c.Vehicle("Semi 19 m"), 15, 90)) < widths["Semi 19 m"] - 0.3);

            // Below the smallest template radius it says so.
            Assert.False(Turn(c.Vehicle("Truck 12.5 m"), 10, 90).WithinMinimumRadius);
        }

        [Fact]
        public void TheyAreDrawnWithTheirAxleGroups()
        {
            TurnPathConventions c = Conventions();
            int Wheels(string name) => TurnPathDrafter.VehicleOutline(c.Vehicle(name), new VehiclePose(new Point2(0, 0), 0), "V", wheels: true)
                .Count(e => e is PolylineSpec p && p.Closed && p.Points.Count == 4);
            int Bodies(string name) => TurnPathDrafter.VehicleOutline(c.Vehicle(name), new VehiclePose(new Point2(0, 0), 0), "V", wheels: false).Count;

            Assert.Equal(4, Wheels("Service 8.8 m"));             // two axles
            Assert.Equal(6, Wheels("Truck 12.5 m"));              // steer axle and a rear pair
            Assert.Equal(6 + 6, Wheels("Semi 19 m"));             // prime mover, and three axles on the trailer
            Assert.Equal(6 + 6 + 6, Wheels("B-double 25 m"));
            Assert.Equal(1, Bodies("Truck 12.5 m"));
            Assert.Equal(2, Bodies("Semi 19 m"));
            Assert.Equal(3, Bodies("B-double 26 m"));

            // Parked in a line, the 26 m B-double is 26 m from nose to tail.
            var points = TurnPathDrafter.VehicleOutline(c.Vehicle("B-double 26 m"), new VehiclePose(new Point2(0, 0), 0), "V", wheels: false)
                .OfType<PolylineSpec>().SelectMany(p => p.Points).ToList();
            Assert.Equal(26.0, points.Max(p => p.X) - points.Min(p => p.X), 6);
            Assert.Equal(2.5, points.Max(p => p.Y) - points.Min(p => p.Y), 6);

            // The rear pair's wheels sit further in than the steer axle's (dual tyres).
            TurnPathResult r = Turn(c.Vehicle("Truck 12.5 m"), 15, 45);
            Assert.Equal(2.25, Dist(r.WheelPaths[0][0], r.WheelPaths[1][0]), 6);
            Assert.Equal(1.98, Dist(r.WheelPaths[2][0], r.WheelPaths[3][0]), 6);
        }

        [Fact]
        public void ASavedCopyOfTheSettingsPicksUpVehiclesItDoesNotHave()
        {
            // "Save as defaults" writes a copy that is used from then on. One saved before the trucks
            // were added has only the cars, perhaps edited.
            TurnPathConventions saved = TurnPathConventions.Default();
            saved.Vehicles["B99"].ClearanceOutside = 0.45;
            Assert.Equal(2, saved.Vehicles.Count);

            List<string> added = saved.AddMissingVehicles(Conventions());
            Assert.Equal(new[] { "Service 8.8 m", "Truck 12.5 m", "Semi 19 m", "B-double 25 m", "B-double 26 m" }, added);
            Assert.Equal(7, saved.Vehicles.Count);
            Assert.Equal(0.45, saved.Vehicles["B99"].ClearanceOutside);      // their own edits stay
            Assert.Equal(19.0, saved.Vehicle("semi 19 m").OverallLength, 9);

            // Nothing to add the second time, and the copies are their own.
            Assert.Empty(saved.AddMissingVehicles(Conventions()));
            saved.Vehicles["Semi 19 m"].Trailers[0].Wheelbase = 1;
            Assert.Equal(9.5, Conventions().Vehicle("Semi 19 m").Trailers[0].Wheelbase);
        }

        [Fact]
        public void TheToolKnowsThem()
        {
            var host = new InMemoryDrawingHost("Meters");
            var tools = new TurnPathTools(host, Conventions());
            Assert.Contains("Semi 19 m", tools.Tools[0].Description);

            using JsonDocument input = JsonDocument.Parse("{\"mode\":\"template\",\"vehicle\":\"Semi 19 m\",\"start\":[0,0],\"heading_deg\":0,\"radius\":15,\"turn_angle_deg\":90,\"lead_out\":25}");
            ToolOutcome outcome = tools.Execute(TurnPathTools.DrawTurnPath, input.RootElement);
            Assert.False(outcome.IsError, outcome.Content);
            using JsonDocument doc = JsonDocument.Parse(outcome.Content);
            Assert.Equal("semi-trailer", doc.RootElement.GetProperty("trailers")[0].GetString());
            Assert.Equal(15, doc.RootElement.GetProperty("nominated_radius").GetDouble());
            Assert.Equal(12.5, doc.RootElement.GetProperty("min_turn_radius").GetDouble());
            Assert.True(doc.RootElement.GetProperty("within_minimum_radius").GetBoolean());
            Assert.Equal(0.5, doc.RootElement.GetProperty("clearances").GetProperty("left").GetDouble());
            Assert.Contains(host.Entities, e => e.Layer == "AE CIV PLN - VEHICLE SWEPT PATH");

            // The default radius is the vehicle's smallest template radius.
            using JsonDocument service = JsonDocument.Parse("{\"mode\":\"template\",\"vehicle\":\"Service 8.8 m\",\"start\":[0,0],\"heading_deg\":0,\"dry_run\":true}");
            using JsonDocument doc2 = JsonDocument.Parse(tools.Execute(TurnPathTools.DrawTurnPath, service.RootElement).Content);
            Assert.Equal(9, doc2.RootElement.GetProperty("nominated_radius").GetDouble());
        }
    }
}
