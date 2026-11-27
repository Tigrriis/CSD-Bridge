using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CsdBridge.Core.Layers;
using CsdBridge.Core.Rendering;
using Xunit;

namespace CsdBridge.Core.Tests
{
    /// <summary>The layer wheel (AILAYERWHEEL): grouping layer names, laying the wheel out, and its settings file.</summary>
    public class LayerWheelTests
    {
        private static readonly string[] Office =
        {
            "AE CIV PLN - ROAD CENTRELINE NEW",
            "AE CIV PLN - KERB NEW",
            "AE CIV PLN - STORMWATER PIPE NEW",
            "AE CIV HTC - ASPHALT TYPE 1",
            "AE CIV HTC - CONCRETE NEW",
            "AE CIV LNE - TEMP",
            "AE CIV PLN - BUILDING NEW",
        };

        [Theory]
        [InlineData("AE CIV HTC - ASPHALT TYPE 1", "AE|CIV|HTC", "ASPHALT TYPE 1")]
        [InlineData("AE CIV MOD - SURF BM-Design-MAJOR", "AE|CIV|MOD", "SURF BM-Design-MAJOR")]
        [InlineData("AE GEN - NOTES", "AE|GEN", "NOTES")]
        [InlineData("AE CIV HTC EXTRA - X", "AE|CIV|HTC EXTRA", "X")]
        [InlineData("E-TEXT", "E", "TEXT")]
        [InlineData("  AE  CIV   LNE -  0.18mm ", "AE|CIV|LNE", "0.18mm")]
        [InlineData("0", "", "0")]
        [InlineData("Defpoints", "", "Defpoints")]
        [InlineData("-ODD", "", "-ODD")]
        [InlineData("AE CIV ANO - ", "AE|CIV|ANO", "AE CIV ANO -")]
        public void EverythingBeforeTheDashIsTheGrouping(string name, string terms, string label)
        {
            LayerNameParts parts = LayerNames.Split(name);
            Assert.Equal(terms, string.Join("|", parts.Terms));
            Assert.Equal(label, parts.Label);
        }

        [Fact]
        public void FewerLevelsKeepTheRestWithTheLastOne()
        {
            LayerNameParts parts = LayerNames.Split("AE CIV HTC - ASPHALT", maxTerms: 2);
            Assert.Equal(new[] { "AE", "CIV HTC" }, parts.Terms);
        }

        [Fact]
        public void ALevelWithOneChoiceFoldsIntoTheMiddle()
        {
            WheelNode root = LayerWheelTree.Build(Office, 3, out string centre);

            Assert.Equal("AE CIV", centre);
            Assert.Equal(new[] { "PLN", "HTC", "LNE" }, root.Children.Select(c => c.Term)); // in the order first listed
            Assert.Equal(
                new[] { "ROAD CENTRELINE NEW", "KERB NEW", "STORMWATER PIPE NEW", "BUILDING NEW" },
                root.Children[0].Children.Select(c => c.Term));
            Assert.All(root.Children, c => Assert.Equal(1, c.Depth));
        }

        [Fact]
        public void TwoDisciplinesMakeTwoRings()
        {
            var layers = Office.Concat(new[] { "AE GEN TBL - SCALE BAR" }).ToArray();
            WheelNode root = LayerWheelTree.Build(layers, 3, out string centre);

            Assert.Equal("AE", centre);
            Assert.Equal(new[] { "CIV", "GEN" }, root.Children.Select(c => c.Term));

            WheelLayout layout = WheelLayout.Build(layers, 3);
            Assert.Equal(2, layout.Rings);
            Assert.Equal(new[] { "CIV", "PLN", "HTC", "LNE", "GEN", "TBL" }, layout.Sectors.Select(s => s.Node.Term));
            WheelSector civ = layout.Sectors[0], pln = layout.Sectors[1];
            Assert.True(civ.Outer < pln.Inner); // CIV is the inner ring, its groups the next one out
            Assert.True(pln.Start >= civ.Start && pln.Start + pln.Sweep <= civ.Start + civ.Sweep + 1e-9);
        }

        [Fact]
        public void ALayerWithoutADashHangsStraightOffTheMiddle()
        {
            var layers = Office.Concat(new[] { "0" }).ToArray();
            WheelNode root = LayerWheelTree.Build(layers, 3, out string centre);

            Assert.Equal("", centre); // AE can't fold away: "0" sits beside it
            WheelNode loose = Assert.Single(root.Children, c => c.IsLeaf);
            Assert.Equal("0", loose.Layer);

            WheelLayout layout = WheelLayout.Build(layers, 3);
            WheelLeaf leaf = layout.Leaves.Single(l => l.Layer == "0");
            (double x, double y) = WheelLayout.PointAt(leaf.Angle, layout.CentreRadius + 5);
            Assert.Equal("0", layout.Hit(x, y).Node!.Layer); // its slice starts at the middle, no rings in the way
        }

        [Fact]
        public void EveryLayerHasItsOwnSliceAndTheSlicesFillTheCircle()
        {
            WheelLayout layout = WheelLayout.Build(Office, 3);

            Assert.Equal(Office.Length, layout.Leaves.Count);
            Assert.Equal(Math.PI * 2, layout.Leaves.Sum(l => l.Node.Sweep), 6);
            Assert.Equal(Math.PI * 2, layout.Sectors.Sum(s => s.Sweep), 6);

            foreach (WheelLeaf leaf in layout.Leaves)
            {
                // Anywhere in the wedge picks the layer, and so does a little beyond the rim.
                Assert.Equal(layout.OuterRadius, leaf.BarStart + leaf.BarLength, 6); // every wedge reaches the rim
                foreach (double radius in new[] { leaf.BarStart + 2, leaf.BarStart + leaf.BarLength / 2, layout.OuterRadius + 5 })
                {
                    (double x, double y) = WheelLayout.PointAt(leaf.Angle, radius);
                    WheelHit hit = layout.Hit(x, y);
                    Assert.Equal(WheelHitKind.Layer, hit.Kind);
                    Assert.Equal(leaf.Layer, hit.Node!.Layer);
                }
            }
        }

        [Fact]
        public void GroupsSitInTheRingInsideTheirLayers()
        {
            WheelLayout layout = WheelLayout.Build(Office, 3);
            WheelSector pln = layout.Sectors.Single(s => s.Node.Term == "PLN");

            (double x, double y) = WheelLayout.PointAt(pln.Node.Mid, (pln.Inner + pln.Outer) / 2);
            WheelHit hit = layout.Hit(x, y);
            Assert.Equal(WheelHitKind.Group, hit.Kind);
            Assert.Same(pln.Node, hit.Node);

            Assert.True(pln.Outer < layout.LeafStart);
            Assert.All(layout.Leaves.Where(l => l.Node.Parent == pln.Node), l => Assert.True(pln.Node.Contains(l.Angle)));
            // PLN has four of the seven layers, so the widest slice.
            Assert.True(pln.Sweep > layout.Sectors.Where(s => s != pln).Max(s => s.Sweep));

            Assert.Equal(WheelHitKind.Centre, layout.Hit(2, -3).Kind);
            Assert.Equal(WheelHitKind.Outside, layout.Hit(layout.OuterRadius + 40, 0).Kind);
        }

        [Fact]
        public void TheWheelGrowsUntilBarsAndLabelsHaveRoom()
        {
            var options = new WheelLayoutOptions();
            string[] many = Enumerable.Range(1, 60).Select(i => $"AE CIV {(i % 4 == 0 ? "HTC" : "PLN")} - LAYER {i}").ToArray();
            WheelLayout small = WheelLayout.Build(Office, 3, options);
            WheelLayout big = WheelLayout.Build(many, 3, options);

            Assert.True(big.LeafStart > small.LeafStart);
            foreach (WheelLayout layout in new[] { small, big })
            {
                // Every wedge has room for a line of text at its narrow end.
                Assert.All(layout.Leaves, l => Assert.True(l.Node.Sweep * layout.LeafStart >= options.LeafPitch - 1e-6));
                // A group's word fits round its part of the ring.
                Assert.All(layout.Sectors, s => Assert.True(
                    s.Sweep * (s.Inner + s.Outer) / 2 >= options.MeasureGroup(s.Node.Term) + 2 * options.LabelPadding - 1e-6));
                Assert.False(layout.TooBig);
            }
        }

        [Fact]
        public void LongNamesAreCutToTheScreenAndTooManyLayersIsReported()
        {
            string[] longName = { "AE CIV PLN - " + new string('X', 200), "AE CIV PLN - B" };
            WheelLayout cut = WheelLayout.Build(longName, 3, new WheelLayoutOptions { MaxRadius = 300 });
            Assert.False(cut.TooBig);
            Assert.True(cut.OuterRadius <= 300.5);

            string[] many = Enumerable.Range(1, 400).Select(i => $"AE CIV PLN - LAYER {i}").ToArray();
            Assert.True(WheelLayout.Build(many, 3, new WheelLayoutOptions { MaxRadius = 300 }).TooBig);
        }

        [Fact]
        public void TextOnTheLeftHalfReadsTowardsTheMiddle()
        {
            WheelLayout layout = WheelLayout.Build(Office, 3);
            Assert.Contains(layout.Leaves, l => l.ReadsInwards);
            Assert.All(layout.Leaves, l => Assert.Equal(WheelLayout.PointAt(l.Angle, 100).X < 0, l.ReadsInwards));
        }

        [Fact]
        public void TheShippedFileLoads()
        {
            LayerWheelSettings settings = LayerWheelSettings.Load(Path.Combine(AppContext.BaseDirectory, "layer-wheel.json"));

            Assert.True(settings.Layers.Count >= 10);
            Assert.Equal(3, settings.MaxTerms);
            Assert.True(settings.ReleaseToPick);
            Assert.Equal(settings.Layers.Count, settings.Layers.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            WheelLayout layout = WheelLayout.Build(settings.Layers, settings.MaxTerms);
            Assert.Equal("AE CIV", layout.CentreLabel);
            Assert.False(layout.TooBig);
        }

        [Fact]
        public void SavingKeepsTheNotesAndReadsBackTheSame()
        {
            string path = Path.Combine(Path.GetTempPath(), "csd-bridge-wheel-" + Guid.NewGuid().ToString("N"), "layer-wheel.json");
            try
            {
                var settings = new LayerWheelSettings { Layers = { "AE CIV PLN - KERB NEW", " ae civ pln - kerb new ", "", "0" }, TextSize = 10 };
                settings.Save(path);

                string text = File.ReadAllText(path);
                Assert.StartsWith("// CSD-Bridge: the layers on the layer wheel", text);
                LayerWheelSettings back = LayerWheelSettings.Load(path);
                Assert.Equal(new[] { "AE CIV PLN - KERB NEW", "0" }, back.Layers); // blanks and repeats dropped
                Assert.Equal(10, back.TextSize);
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
            }
        }

        [Fact]
        public void LayersAlreadyOnTheWheelKeepTheirPlace()
        {
            var current = new[] { "AE CIV PLN - KERB NEW", "AE CIV HTC - GRAVEL NEW", "AE CIV PLN - BATTER LINE" };
            var chosen = new[] { "AE CIV HTC - ASPHALT TYPE 1", "AE CIV PLN - BATTER LINE", "AE CIV PLN - KERB NEW", "AE CIV ANO - NOTES" };

            Assert.Equal(
                new[] { "AE CIV PLN - KERB NEW", "AE CIV PLN - BATTER LINE", "AE CIV ANO - NOTES", "AE CIV HTC - ASPHALT TYPE 1" },
                LayerWheelSettings.Merge(current, chosen));
        }

        [Theory]
        [InlineData(1, 255, 0, 0)]
        [InlineData(2, 255, 255, 0)]
        [InlineData(5, 0, 0, 255)]
        [InlineData(6, 255, 0, 255)]
        [InlineData(7, 255, 255, 255)]
        [InlineData(30, 255, 127, 0)]
        [InlineData(94, 0, 127, 0)]
        [InlineData(151, 127, 191, 255)]
        [InlineData(220, 255, 0, 191)]
        [InlineData(40, 255, 191, 0)]
        [InlineData(12, 165, 0, 0)]
        [InlineData(13, 165, 82, 82)]
        [InlineData(250, 51, 51, 51)]
        [InlineData(254, 190, 190, 190)]
        [InlineData(0, 255, 255, 255)]
        [InlineData(256, 255, 255, 255)]
        public void StandardColourNumbersBecomeScreenColours(int index, int r, int g, int b)
        {
            Assert.Equal((r, g, b), AciColors.Rgb(index));
        }

        /// <summary>
        /// Set CSD_BRIDGE_WHEEL_OUT to a .png path to see the layout of the shipped file: a plain
        /// sketch of where things go, not the wheel as BricsCAD draws it.
        /// </summary>
        [Fact]
        public void SketchOfTheShippedWheel()
        {
            string? output = Environment.GetEnvironmentVariable("CSD_BRIDGE_WHEEL_OUT");
            LayerWheelSettings settings = LayerWheelSettings.Load(Path.Combine(AppContext.BaseDirectory, "layer-wheel.json"));
            WheelLayout layout = WheelLayout.Build(settings.Layers, settings.MaxTerms, new WheelLayoutOptions
            {
                MeasureLeaf = s => s.Length * 8.5 * StrokeFont.Advance, // the sketch's own lettering
                MeasureGroup = s => s.Length * 10 * StrokeFont.Advance,
            });
            Assert.NotEmpty(layout.Leaves);
            if (string.IsNullOrEmpty(output))
            {
                return;
            }

            int size = (int)(layout.OuterRadius * 2) + 40;
            double c = size / 2.0;
            var canvas = new Canvas(size, size);
            var ink = Rgb.Hex(0x202830);
            var ring = Rgb.Hex(0x4A5568);
            var palette = new[] { 0xFF00FF, 0xFFFF00, 0x00A600, 0xFF0000, 0x0000FF, 0xB5C7FF, 0xD6D6D6 };

            List<(double, double)> Slice(double inner, double outer, double start, double sweep)
            {
                var points = new List<(double, double)>();
                int steps = Math.Max(2, (int)(sweep * outer / 4));
                for (int i = 0; i <= steps; i++)
                {
                    (double x, double y) = WheelLayout.PointAt(start + sweep * i / steps, outer);
                    points.Add((c + x, c + y));
                }

                for (int i = steps; i >= 0; i--)
                {
                    (double x, double y) = WheelLayout.PointAt(start + sweep * i / steps, inner);
                    points.Add((c + x, c + y));
                }

                return points;
            }

            void Text(string text, double x, double y, double angle, double height, Rgb color)
            {
                double cos = Math.Cos(angle), sin = Math.Sin(angle);
                StrokeFont.Strokes(text, (x0, y0, x1, y1) => canvas.Line(
                    x + (x0 * cos + y0 * sin) * height, y + (x0 * sin - y0 * cos) * height,
                    x + (x1 * cos + y1 * sin) * height, y + (x1 * sin - y1 * cos) * height, color, 1.2));
            }

            canvas.Fill(Slice(0, layout.OuterRadius + 6, 0, Math.PI * 2), Rgb.Hex(0x16191E));
            foreach (WheelSector s in layout.Sectors)
            {
                canvas.Fill(Slice(s.Inner, s.Outer, s.Start + 0.01, s.Sweep - 0.02), ring);
                (double x, double y) = WheelLayout.PointAt(s.Node.Mid, (s.Inner + s.Outer) / 2);
                Text(s.Node.Term, c + x - s.Node.Term.Length * 4.5, c + y + 5, 0, 10, Rgb.Hex(0xFFFFFF));
            }

            foreach (WheelLeaf leaf in layout.Leaves)
            {
                double hair = 1.0 / leaf.BarStart;
                canvas.Fill(Slice(leaf.InnerHit + 3, layout.OuterRadius, leaf.Node.Start + hair, leaf.Node.Sweep - 2 * hair),
                    Rgb.Hex(palette[leaf.LayerIndex % palette.Length]));
                double along = leaf.Angle - Math.PI / 2; // screen angle of the bar, x right, y down
                double from = leaf.ReadsInwards ? leaf.BarStart + leaf.BarLength - 7 : leaf.BarStart + 7;
                (double tx, double ty) = WheelLayout.PointAt(leaf.Angle, from);
                double turn = leaf.ReadsInwards ? along + Math.PI : along;
                Text(leaf.Label, c + tx - 4 * Math.Sin(turn), c + ty + 4 * Math.Cos(turn), turn, 8.5, ink);
            }

            Text(layout.CentreLabel, c - layout.CentreLabel.Length * 4.5, c + 5, 0, 10, Rgb.Hex(0xFFFFFF));
            File.WriteAllBytes(output!, canvas.ToPng());
        }
    }
}
