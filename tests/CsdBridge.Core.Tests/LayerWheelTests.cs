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

        // ------------------------------------------------------------------ a short list: everything at once

        private static WheelPart PartFor(WheelView view, string layer) => view.Parts.Single(p => p.Layer == layer);

        private static WheelPart Group(WheelView view, string term) => view.Parts.Single(p => p.Kind != WheelPartKind.Layer && p.Node.Term == term);

        private static WheelHit HitAt(WheelView view, double angle, double radius)
        {
            (double x, double y) = WheelLayout.PointAt(angle, radius);
            return view.Hit(x, y);
        }

        [Fact]
        public void TwoDisciplinesMakeTwoRings()
        {
            var layers = Office.Concat(new[] { "AE GEN TBL - SCALE BAR" }).ToArray();
            WheelNode root = LayerWheelTree.Build(layers, 3, out string centre);

            Assert.Equal("AE", centre);
            Assert.Equal(new[] { "CIV", "GEN" }, root.Children.Select(c => c.Term));

            WheelLayout layout = WheelLayout.Build(layers, 3);
            Assert.False(layout.Steps);
            WheelView view = layout.Top;
            Assert.Equal(new[] { "CIV", "PLN", "HTC", "LNE", "GEN", "TBL" }, view.Parts.Where(p => p.RoundTheRing).Select(p => p.Label));
            WheelPart civ = Group(view, "CIV"), pln = Group(view, "PLN");
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

            WheelView view = WheelLayout.Build(layers, 3).Top;
            WheelPart zero = PartFor(view, "0");
            Assert.Equal("0", HitAt(view, zero.Mid, view.CentreRadius + 5).Part!.Layer); // its wedge starts at the middle, no rings in the way
        }

        [Fact]
        public void EveryLayerHasItsOwnWedgeAndTheWedgesFillTheCircle()
        {
            WheelLayout layout = WheelLayout.Build(Office, 3);
            WheelView view = layout.Top;
            List<WheelPart> wedges = view.Parts.Where(p => p.Kind == WheelPartKind.Layer).ToList();

            Assert.False(layout.Steps);
            Assert.Equal("AE CIV", view.CentreLabel);
            Assert.Equal(Office.Length, wedges.Count);
            Assert.Equal(Math.PI * 2, wedges.Sum(p => p.Sweep), 6);
            Assert.Equal(Math.PI * 2, view.Parts.Where(p => p.RoundTheRing).Sum(p => p.Sweep), 6);

            foreach (WheelPart wedge in wedges)
            {
                Assert.Equal(view.OuterRadius, wedge.Outer, 6); // every wedge reaches the rim: no gaps
                Assert.False(wedge.RoundTheRing);

                // Anywhere in the wedge picks the layer, and so does a little beyond the rim.
                foreach (double radius in new[] { wedge.TextStart + 2, (wedge.Inner + wedge.Outer) / 2, view.OuterRadius + 5 })
                {
                    WheelHit hit = HitAt(view, wedge.Mid, radius);
                    Assert.Equal(WheelHitKind.Part, hit.Kind);
                    Assert.Equal(wedge.Layer, hit.Part!.Layer);
                }
            }
        }

        [Fact]
        public void GroupsSitInTheRingInsideTheirLayers()
        {
            WheelView view = WheelLayout.Build(Office, 3).Top;
            WheelPart pln = Group(view, "PLN");

            Assert.Same(pln, HitAt(view, pln.Mid, (pln.Inner + pln.Outer) / 2).Part);
            foreach (WheelPart wedge in view.Parts.Where(p => p.Kind == WheelPartKind.Layer && p.Node.Parent == pln.Node))
            {
                Assert.True(wedge.Inner > pln.Outer);
                Assert.True(wedge.Mid > pln.Start && wedge.Mid < pln.Start + pln.Sweep);
            }

            // PLN has four of the seven layers, so the widest slice.
            Assert.True(pln.Sweep > view.Parts.Where(p => p.RoundTheRing && p != pln).Max(p => p.Sweep));

            Assert.Equal(WheelHitKind.Centre, view.Hit(2, -3).Kind);
            Assert.Equal(WheelHitKind.Outside, view.Hit(view.OuterRadius + 40, 0).Kind);
        }

        [Fact]
        public void TheWheelGrowsUntilWedgesAndLabelsHaveRoom()
        {
            var options = new WheelLayoutOptions();
            string[] more = Enumerable.Range(1, 40).Select(i => $"AE CIV {(i % 4 == 0 ? "HTC" : "PLN")} - LAYER {i}").ToArray();
            WheelView small = WheelLayout.Build(Office, 3, options).Top;
            WheelView big = WheelLayout.Build(more, 3, options).Top;

            Assert.True(big.Parts.First(p => !p.RoundTheRing).TextStart > small.Parts.First(p => !p.RoundTheRing).TextStart);
            foreach (WheelView view in new[] { small, big })
            {
                foreach (WheelPart part in view.Parts)
                {
                    if (part.RoundTheRing)
                    {
                        // A group's word fits round its part of the ring.
                        Assert.True(part.Sweep * (part.Inner + part.Outer) / 2 >= options.MeasureGroup(part.Label) + 2 * options.LabelPadding - 1e-6);
                    }
                    else
                    {
                        // Every wedge has room for a line of text at its narrow end.
                        Assert.True(part.Sweep * part.TextStart >= options.LeafPitch - 1e-6);
                    }
                }

                Assert.False(view.TooBig);
            }
        }

        [Fact]
        public void LongNamesAreCutToTheScreen()
        {
            string[] longName = { "AE CIV PLN - " + new string('X', 200), "AE CIV PLN - B" };
            WheelLayout cut = WheelLayout.Build(longName, 3, new WheelLayoutOptions { MaxRadius = 300 });
            Assert.False(cut.TooBig);
            Assert.True(cut.LargestRadius <= 300.5);
        }

        [Fact]
        public void TextOnTheLeftHalfReadsTowardsTheMiddle()
        {
            WheelView view = WheelLayout.Build(Office, 3).Top;
            List<WheelPart> wedges = view.Parts.Where(p => !p.RoundTheRing).ToList();
            Assert.Contains(wedges, p => p.ReadsInwards);
            Assert.All(wedges, p => Assert.Equal(WheelLayout.PointAt(p.Mid, 100).X < 0, p.ReadsInwards));
        }

        // ------------------------------------------------------------------ a long list: a step at a time

        private static string[] ManyLayers()
        {
            var layers = new List<string>();
            foreach (string service in new[] { "SEWER", "STORMWATER", "WATER", "GAS", "POWER", "ROAD", "FENCE", "BUILDING" })
            {
                foreach (string kind in new[] { "PIPE", "PIT", "IO", "MAIN", "EDGE" })
                {
                    foreach (string state in new[] { "DEMO", "EXISTING", "NEW" })
                    {
                        layers.Add($"AE CIV PLN - {service} {kind} {state}");
                    }
                }
            }

            layers.Add("AE CIV PLN - LINEMARKING"); // shares its first word with nothing
            layers.AddRange(Enumerable.Range(1, 12).Select(i => $"AE CIV ANO - NOTE {i}"));
            layers.AddRange(new[] { "AE CIV HTC - ASPHALT TYPE 1", "AE CIV HTC - CONCRETE NEW" });
            return layers.ToArray();
        }

        [Fact]
        public void ALongListOpensOnTheGroups()
        {
            WheelLayout layout = WheelLayout.Build(ManyLayers(), 3);

            Assert.True(layout.Steps);
            WheelView top = layout.Top;
            Assert.Equal("AE CIV", top.CentreLabel);
            Assert.Equal(new[] { "PLN (121)", "ANO (12)", "HTC (2)" }, top.Parts.Select(p => p.Label));
            Assert.All(top.Parts, p => Assert.Equal(WheelPartKind.Group, p.Kind));
            Assert.All(top.Parts, p => Assert.False(p.RoundTheRing)); // wedges round the whole circle
            Assert.Equal(Math.PI * 2, top.Parts.Sum(p => p.Sweep), 6);
            Assert.Null(layout.Back(top));
        }

        [Fact]
        public void AClickOnAGroupOpensItsLayers()
        {
            WheelLayout layout = WheelLayout.Build(ManyLayers(), 3);
            WheelPart ano = Group(layout.Top, "ANO");
            Assert.Same(ano, HitAt(layout.Top, ano.Mid, (ano.Inner + ano.Outer) / 2).Part);

            WheelView view = layout.Open(ano.Node);
            Assert.Equal("AE CIV ANO", view.CentreLabel);
            Assert.Equal(12, view.Parts.Count);
            Assert.All(view.Parts, p => Assert.Equal(WheelPartKind.Layer, p.Kind));
            Assert.Equal("NOTE 1", view.Parts[0].Label);
            Assert.Equal("AE CIV ANO - NOTE 1", view.Parts[0].Layer);
            Assert.Equal(0, view.Parts[0].Start, 9); // clockwise from the top, in the order listed

            foreach (WheelPart wedge in view.Parts)
            {
                Assert.Same(wedge, HitAt(view, wedge.Mid, (wedge.Inner + wedge.Outer) / 2).Part);
            }

            Assert.Same(layout.Top, layout.Back(view));
        }

        [Fact]
        public void AGroupTooBigForOneWheelIsSplitByFirstWord()
        {
            WheelLayout layout = WheelLayout.Build(ManyLayers(), 3, new WheelLayoutOptions { SplitOver = 40 });
            WheelView pln = layout.Open(Group(layout.Top, "PLN").Node);

            Assert.Equal("AE CIV PLN", pln.CentreLabel);
            Assert.Equal(
                new[] { "SEWER (15)", "STORMWATER (15)", "WATER (15)", "GAS (15)", "POWER (15)", "ROAD (15)", "FENCE (15)", "BUILDING (15)", "LINEMARKING" },
                pln.Parts.Select(p => p.Label));
            Assert.Equal(WheelPartKind.Branch, pln.Parts[0].Kind);
            Assert.Equal(WheelPartKind.Layer, pln.Parts[8].Kind); // on its own, so still a layer to pick here

            WheelView sewer = layout.Open(pln.Parts[0].Node);
            Assert.Equal("AE CIV PLN SEWER", sewer.CentreLabel);
            Assert.Equal(15, sewer.Parts.Count);
            Assert.Equal("PIPE DEMO", sewer.Parts[0].Label); // the shared word is in the middle, not repeated on every wedge
            Assert.Equal("AE CIV PLN - SEWER PIPE DEMO", sewer.Parts[0].Layer);
            Assert.Same(pln, layout.Back(sewer));

            // The current layer can be traced a step back: it is within its branch and its group.
            WheelNode current = layout.Find("ae civ pln - sewer pit new")!;
            Assert.True(current.IsWithin(pln.Parts[0].Node));
            Assert.True(current.IsWithin(Group(layout.Top, "PLN").Node));
            Assert.False(current.IsWithin(Group(layout.Top, "ANO").Node));
        }

        [Fact]
        public void BranchesAreNamedByTheWordsTheirLayersShare()
        {
            var layers = Enumerable.Range(1, 50).Select(i => $"AE CIV DET - FILLER{i}").ToList();
            layers.AddRange(new[] { "AE CIV DET - RETAINING WALL BLOCK", "AE CIV DET - RETAINING WALL FOOTING", "AE CIV DET - REO BAR", "AE CIV DET - REO MESH", "AE CIV DET - KERB", "AE CIV DET - KERB NEW" });
            WheelLayout layout = WheelLayout.Build(layers, 3, new WheelLayoutOptions { SplitOver = 40 });

            WheelView top = layout.Top; // everything is under AE CIV DET, so that is the middle
            Assert.Equal("AE CIV DET", top.CentreLabel);
            Assert.Contains("RETAINING WALL (2)", top.Parts.Select(p => p.Label));
            Assert.Contains("REO (2)", top.Parts.Select(p => p.Label));

            WheelView wall = layout.Open(top.Parts.Single(p => p.Label == "RETAINING WALL (2)").Node);
            Assert.Equal(new[] { "BLOCK", "FOOTING" }, wall.Parts.Select(p => p.Label));
            WheelView kerb = layout.Open(top.Parts.Single(p => p.Label == "KERB (2)").Node);
            Assert.Equal(new[] { "KERB", "NEW" }, kerb.Parts.Select(p => p.Label)); // nothing left after the shared word: keep the name
        }

        [Fact]
        public void NoSplittingWhenItIsTurnedOff()
        {
            WheelLayout layout = WheelLayout.Build(ManyLayers(), 3, new WheelLayoutOptions { SplitOver = 0 });
            WheelView pln = layout.Open(Group(layout.Top, "PLN").Node);
            Assert.Equal(121, pln.Parts.Count);
            Assert.All(pln.Parts, p => Assert.Equal(WheelPartKind.Layer, p.Kind));
        }

        [Fact]
        public void TheShippedFileLoads()
        {
            LayerWheelSettings settings = LayerWheelSettings.Load(Path.Combine(AppContext.BaseDirectory, "layer-wheel.json"));

            Assert.True(settings.Layers.Count >= 10);
            Assert.Equal(3, settings.MaxTerms);
            Assert.True(settings.ReleaseToPick);
            Assert.Equal(settings.Layers.Count, settings.Layers.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(settings.Layers, l => Assert.StartsWith("AE CIV ", l));

            WheelLayout layout = WheelLayout.Build(settings.Layers, settings.MaxTerms, new WheelLayoutOptions { SplitOver = settings.SplitGroupsOver });
            Assert.Equal("AE CIV", layout.Top.CentreLabel);
            Assert.False(layout.TooBig);
            Assert.True(layout.LargestRadius < 400); // every step is a wheel of a comfortable size

            // Every layer can be reached, and in at most three clicks.
            foreach (string layer in settings.Layers)
            {
                WheelNode node = layout.Find(layer)!;
                int steps = 0;
                for (WheelNode? n = node; n != null; n = n.Parent)
                {
                    steps++;
                }

                Assert.InRange(steps - 1, 1, 3);
                Assert.Contains(layout.Open(node.Parent!).Parts, p => p.Layer == layer);
            }
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
        /// Set CSD_BRIDGE_WHEEL_OUT to a folder to see the layout of the shipped file, one picture
        /// per step: a plain sketch of where things go, not the wheel as BricsCAD draws it.
        /// </summary>
        [Fact]
        public void SketchOfTheShippedWheel()
        {
            string? output = Environment.GetEnvironmentVariable("CSD_BRIDGE_WHEEL_OUT");
            LayerWheelSettings settings = LayerWheelSettings.Load(Path.Combine(AppContext.BaseDirectory, "layer-wheel.json"));
            WheelLayout layout = WheelLayout.Build(settings.Layers, settings.MaxTerms, new WheelLayoutOptions
            {
                SplitOver = settings.SplitGroupsOver,
                MeasureLeaf = s => s.Length * 8.5 * StrokeFont.Advance, // the sketch's own lettering
                MeasureGroup = s => s.Length * 10 * StrokeFont.Advance,
            });
            Assert.NotEmpty(layout.Top.Parts);
            if (string.IsNullOrEmpty(output))
            {
                return;
            }

            Directory.CreateDirectory(output!);
            var views = new List<(string Name, WheelView View)> { ("1-top", layout.Top) };
            if (layout.Steps)
            {
                WheelView pln = layout.Open(layout.Top.Parts.Single(p => p.Node.Term == "PLN").Node);
                views.Add(("2-PLN", pln));
                views.Add(("3-PLN-SEWER", layout.Open(pln.Parts.Single(p => p.Node.Term == "SEWER").Node)));
                views.Add(("2-DET", layout.Open(layout.Top.Parts.Single(p => p.Node.Term == "DET").Node)));
            }

            foreach ((string name, WheelView view) in views)
            {
                File.WriteAllBytes(Path.Combine(output!, name + ".png"), Sketch(view, (int)(layout.LargestRadius * 2) + 40));
            }
        }

        private static byte[] Sketch(WheelView view, int size)
        {
            double c = size / 2.0;
            var canvas = new Canvas(size, size);
            var ink = Rgb.Hex(0x202830);
            var white = Rgb.Hex(0xFFFFFF);
            var ring = Rgb.Hex(0x4A5568);
            var palette = new[] { 0xFF00FF, 0xFFFF00, 0x00A600, 0xFF0000, 0x4060FF, 0xB5C7FF, 0xD6D6D6, 0x00C8C8 };

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

            canvas.Fill(Slice(0, view.OuterRadius + 6, 0, Math.PI * 2), Rgb.Hex(0x16191E));
            foreach (WheelPart part in view.Parts)
            {
                if (part.RoundTheRing)
                {
                    canvas.Fill(Slice(part.Inner, part.Outer, part.Start + 0.01, part.Sweep - 0.02), ring);
                    (double x, double y) = WheelLayout.PointAt(part.Mid, (part.Inner + part.Outer) / 2);
                    Text(part.Label, c + x - part.Label.Length * 4.5, c + y + 5, 0, 10, white);
                    continue;
                }

                bool layer = part.Kind == WheelPartKind.Layer;
                double hair = 1.0 / part.Inner;
                canvas.Fill(Slice(part.Inner, part.Outer, part.Start + hair, part.Sweep - 2 * hair),
                    layer ? Rgb.Hex(palette[part.LayerIndex % palette.Length]) : ring);
                double height = layer ? 8.5 : 10;
                double along = part.Mid - Math.PI / 2; // screen angle of the wedge, x right, y down
                double from = part.ReadsInwards ? part.TextStart + part.TextLength - 7 : part.TextStart + 7;
                (double tx, double ty) = WheelLayout.PointAt(part.Mid, from);
                double turn = part.ReadsInwards ? along + Math.PI : along;
                Text(part.Label, c + tx - height / 2 * Math.Sin(turn), c + ty + height / 2 * Math.Cos(turn), turn, height, layer ? ink : white);
            }

            canvas.Fill(Slice(0, view.CentreRadius - 3, 0, Math.PI * 2), Rgb.Hex(0x22262E));
            string[] words = view.CentreLabel.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                Text(words[i], c - words[i].Length * 4.5, c + 5 + (i - (words.Length - 1) / 2.0) * 16, 0, 10, white);
            }

            return canvas.ToPng();
        }
    }
}
