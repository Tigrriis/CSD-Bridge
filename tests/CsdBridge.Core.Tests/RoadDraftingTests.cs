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
using Xunit;

namespace CsdBridge.Core.Tests
{
    /// <summary>draft_road on a synthetic CSD road, using the shipped road-drafting.json.</summary>
    public class RoadDraftingTests
    {
        private static RoadConventions Conventions() =>
            RoadConventions.Load(Path.Combine(AppContext.BaseDirectory, "road-drafting.json"));

        // 100 m straight east, then 50 m of a 100 m radius curve to the left.
        private static Path2 Alignment() => new Path2(new List<Segment>
        {
            new LineSegment(new Point2(0, 0), new Point2(100, 0)),
            new ArcSegment(new Point2(100, 100), 100, -Math.PI / 2, 0.5),
        }, false);

        /// <summary>A drawing laid out the way CSD leaves it: alignment lines/arcs, and chorded code strings.</summary>
        private static InMemoryDrawingHost Drawing()
        {
            var host = new InMemoryDrawingHost("Meters");
            host.CreateEntities(new EntitySpec[]
            {
                new LineSpec { Start = new Point2(0, 0), End = new Point2(100, 0), Layer = "Align-Lines" },
                new ArcSpec { Center = new Point2(100, 100), Radius = 100, StartAngleDeg = -90, EndAngleDeg = -90 + 0.5 * 180 / Math.PI, Layer = "Align-Arcs" },
                // Another road's alignment, which must not be picked up.
                new LineSpec { Start = new Point2(0, 50), End = new Point2(60, 50), Layer = "Align-Lines" },
            });

            Path2 align = Alignment();
            void String(string code, double offset, double from = 0, double to = 150)
            {
                var pts = new List<Point2>();
                for (double ch = from; ch <= to + 1e-9; ch += 2.5)
                {
                    pts.Add(RoadGeometry.PointAt(align, ch, offset));
                }

                host.CreateEntities(new[] { new PolylineSpec { Points = pts, Layer = $"CSD-3D-CL-01-{code}" } });
            }

            String("C.L.", 0);
            String("LEB", -1.5);
            String("REB", 1.5);
            String("LSH", -2.0);
            String("RSH", 2.0);
            String("FOOTPATH_RIGHT", 4.0, 20, 120);
            return host;
        }

        [Fact]
        public void ShippedConventionsLoad()
        {
            RoadConventions c = Conventions();
            Assert.Equal("AE CIV PLN - ROAD CENTRELINE NEW", c.Centreline!.Layer);
            Assert.Contains(c.Areas, a => a.Name == "pavement" && a.Hatch!.Layer == "AE CIV HTC - ASPHALT TYPE 1");
            Assert.Equal(new[] { 181, 199, 255 }, c.Layers["AE CIV HTC - ASPHALT TYPE 1"].Rgb);
            Assert.Contains("pavement: between LEB and REB", c.Describe());
        }

        [Fact]
        public void DraftsTheRoadExactlyFromTheAlignment()
        {
            InMemoryDrawingHost host = Drawing();
            var tools = new RoadDraftingTools(host, Conventions());
            using JsonDocument input = JsonDocument.Parse("{\"road\":\"CL-01\"}");
            ToolOutcome outcome = tools.Execute(RoadDraftingTools.DraftRoad, input.RootElement);
            Assert.False(outcome.IsError, outcome.Content);

            var on = host.Entities.ToLookup(e => e.Layer, e => e.Spec);

            // Centreline: the exact alignment (a line and a true arc), with the linetype scale.
            var cl = Assert.IsType<PolylineSpec>(Assert.Single(on["AE CIV PLN - ROAD CENTRELINE NEW"]));
            Assert.Equal(0.5, cl.LinetypeScale);
            Assert.Equal(Math.Tan(0.5 / 4), cl.Bulges![1], 9);

            // Pavement: one closed outline whose sides are exact arcs at 98.5 and 101.5 m, hatched.
            var pave = Assert.IsType<PolylineSpec>(Assert.Single(on["AE CIV PLN - ROAD PAVEMENT EDGE NEW"]));
            Assert.True(pave.Closed);
            List<double> radii = Path2.From(pave)!.Segments.OfType<ArcSegment>().Select(a => Math.Round(a.Radius, 6)).OrderBy(r => r).ToList();
            Assert.Equal(new[] { 98.5, 101.5 }, radii);
            Assert.Equal(3.0 * 150, Path2.From(pave)!.Area, 6); // 3 m wide; inside and outside of the curve balance
            var hatch = Assert.IsType<HatchSpec>(Assert.Single(on["AE CIV HTC - ASPHALT TYPE 1"]));
            Assert.Equal("SOLID", hatch.Pattern);

            // Footpath right only where its string is (ch 20-120); so no right shoulder, but a left one.
            var path = Assert.IsType<PolylineSpec>(Assert.Single(on["AE CIV PLN - FOOTPATH EDGE NEW"]));
            // 2.5 m wide: 80 m straight plus 20 m on the outside of the curve (radii 101.5-104 over 0.2 rad).
            Assert.Equal(2.5 * 80 + 0.5 * 0.2 * (104 * 104 - 101.5 * 101.5), Path2.From(path)!.Area, 6);
            var shoulder = Assert.IsType<PolylineSpec>(Assert.Single(on["AE CIV PLN - ROAD SHOULDER NEW"]));
            Assert.Equal(0.5 * 100 + 0.5 * 0.5 * (98.5 * 98.5 - 98 * 98), Path2.From(shoulder)!.Area, 6); // inside of the curve

            // Missing layers were created with the conventions' style; the other road was ignored.
            Assert.Contains(host.Layers, l => l.Name == "AE CIV HTC - ASPHALT TYPE 1");
            using JsonDocument report = JsonDocument.Parse(outcome.Content);
            Assert.Equal(2, report.RootElement.GetProperty("alignment_from").GetArrayLength());
            var skipped = report.RootElement.GetProperty("skipped").EnumerateArray().Select(x => x.GetString()!).ToList();
            Assert.Contains(skipped, x => x.StartsWith("shoulder right"));
            Assert.Contains(skipped, x => x.StartsWith("footpath left: no FOOTPATH_LEFT"));
            Assert.Equal(1.5, report.RootElement.GetProperty("codes").GetProperty("REB").GetProperty("stretches")[0].GetProperty("offset").GetDouble());
        }

        [Fact]
        public void DryRunDrawsNothing()
        {
            InMemoryDrawingHost host = Drawing();
            int before = host.Entities.Count;
            var tools = new RoadDraftingTools(host, Conventions());
            using JsonDocument input = JsonDocument.Parse("{\"road\":\"CL-01\",\"dry_run\":true,\"from_chainage\":20,\"to_chainage\":60,\"only\":[\"pavement\"]}");
            ToolOutcome outcome = tools.Execute(RoadDraftingTools.DraftRoad, input.RootElement);

            Assert.False(outcome.IsError, outcome.Content);
            Assert.Equal(before, host.Entities.Count);
            Assert.Contains("pavement outline", outcome.Content);
            Assert.DoesNotContain("centreline\"", outcome.Content);
        }

        [Fact]
        public void UnknownRoadSaysWhatWasLookedFor()
        {
            var tools = new RoadDraftingTools(Drawing(), Conventions());
            using JsonDocument input = JsonDocument.Parse("{\"road\":\"NOPE\"}");
            ToolOutcome outcome = tools.Execute(RoadDraftingTools.DraftRoad, input.RootElement);
            Assert.True(outcome.IsError);
            Assert.Contains("CSD-3D-NOPE-C.L.", outcome.Content);
        }
    }
}
