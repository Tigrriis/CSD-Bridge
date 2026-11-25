using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CsdBridge.Core.Civil;
using CsdBridge.Core.Drawing;
using Xunit;

namespace CsdBridge.Core.Tests
{
    /// <summary>Finding the pipes of a CSD long section for AIPIPEHATCH.</summary>
    public class LongSectionPipesTests
    {
        private static LongSectionHatchSettings Settings() =>
            LongSectionHatchSettings.Load(Path.Combine(AppContext.BaseDirectory, "long-section-hatch.json"));

        private static SectionLine L(double x1, double y1, double x2, double y2, string layer) =>
            new SectionLine(new Point2(x1, y1), new Point2(x2, y2), layer);

        /// <summary>Invert and obvert of one pipe run.</summary>
        private static IEnumerable<SectionLine> Pipe(double x1, double il1, double x2, double il2, double depth, string layer = "PIPE")
        {
            yield return L(x1, il1, x2, il2, layer);
            yield return L(x2, il2 + depth, x1, il1 + depth, layer); // drawn the other way round
        }

        /// <summary>A long section like CSD plots: pits, three pipe runs with a drop, ground lines and a data band.</summary>
        private static List<SectionLine> Section()
        {
            var lines = new List<SectionLine>();
            lines.AddRange(Pipe(0, 10, 40, 10.8, 0.9));
            lines.AddRange(Pipe(40, 11.2, 95, 14, 0.9));   // drop at the pit
            lines.AddRange(Pipe(95, 14, 180, 20, 0.525));

            foreach (double x in new[] { 0.0, 40, 95, 180 })
            {
                lines.Add(L(x, 9, x, 25, "PIPE"));          // pit walls, on the pipe layer
                lines.Add(L(x, 0, x, -12, "BAND"));
            }

            // Existing and design ground: sampled at the same chainages and parallel in places.
            for (int i = 0; i < 18; i++)
            {
                lines.Add(L(i * 10, 13 + i * 0.6, i * 10 + 10, 13.6 + i * 0.6, "EXISTING"));
                lines.Add(L(i * 10, 13.5 + i * 0.6, i * 10 + 10, 14.1 + i * 0.6, "DESIGN"));
            }

            // Data band: level rows over the full width and over each pipe run.
            foreach (double y in new[] { -2.0, -4, -6, -8, -10, -12 })
            {
                lines.Add(L(0, y, 180, y, "BAND"));
                lines.Add(L(0, y, 40, y, "BAND"));
            }

            return lines;
        }

        [Fact]
        public void FindsEachPipeRunAndNothingElse()
        {
            List<PipeQuad> quads = LongSectionPipes.Find(Section());

            Assert.Equal(3, quads.Count);
            Assert.All(quads, q => Assert.Equal("PIPE", q.Layer));
            Assert.Equal(new[] { 0.9, 0.9, 0.525 }, quads.Select(q => Math.Round(q.Depth, 6)));

            // Invert start, invert end, obvert end, obvert start.
            PipeQuad second = quads[1];
            Assert.Equal(new Point2(40, 11.2), second.Corners[0]);
            Assert.Equal(new Point2(95, 14), second.Corners[1]);
            Assert.Equal(95, second.Corners[2].X);
            Assert.Equal(14.9, second.Corners[2].Y, 9);
            Assert.Equal(40, second.Corners[3].X);
            Assert.Equal(12.1, second.Corners[3].Y, 9);
        }

        [Fact]
        public void LevelPipeIsFoundOnALayerThatHasSlopingPipes()
        {
            List<SectionLine> lines = Section();
            lines.AddRange(Pipe(180, 20, 220, 20, 0.525));
            // Level rows on the pipe layer that are far too deep to be a pipe.
            lines.Add(L(180, 0, 220, 0, "PIPE"));
            lines.Add(L(180, -5, 220, -5, "PIPE"));

            List<PipeQuad> quads = LongSectionPipes.Find(lines);

            Assert.Equal(4, quads.Count);
            Assert.Equal(180, quads[3].Corners[0].X);
            Assert.Equal(20, quads[3].Corners[0].Y);
            Assert.Equal(0.525, quads[3].Depth, 9);
        }

        [Fact]
        public void PipeWithWallLinesIsHatchedOverItsFullHeight()
        {
            var lines = new List<SectionLine>();
            lines.AddRange(Pipe(0, 10, 50, 11, 1.0));        // outside of the walls
            lines.AddRange(Pipe(0, 10.05, 50, 11.05, 0.9));  // bore

            PipeQuad quad = Assert.Single(LongSectionPipes.Find(lines));
            Assert.Equal(1.0, quad.Depth, 9);
        }

        [Fact]
        public void PipeLayersLimitTheSearch()
        {
            List<SectionLine> lines = Section();
            lines.AddRange(Pipe(0, 30, 40, 31, 2, "LOGO")); // a look-alike on another layer

            Assert.Equal(4, LongSectionPipes.Find(lines).Count);
            Assert.Equal(3, LongSectionPipes.Find(lines, new[] { "PIP*" }).Count);
            Assert.Empty(LongSectionPipes.Find(lines, new[] { "NOTHING" }));
        }

        [Fact]
        public void LinesThatAreNotParallelOrDontShareChainagesAreNotPipes()
        {
            var lines = new List<SectionLine>
            {
                L(0, 10, 40, 11, "PIPE"), L(0, 11, 40, 12.5, "PIPE"),   // not parallel
                L(50, 10, 90, 11, "PIPE"), L(50, 11, 80, 11.75, "PIPE"), // different end
                L(100, 10, 140, 11, "PIPE"), L(100, 10, 140, 11, "PIPE"), // the same line twice
            };

            Assert.Empty(LongSectionPipes.Find(lines));
        }

        [Fact]
        public void WorksAtPaperScaleAndOnMapCoordinates()
        {
            foreach ((double scale, double shift) in new[] { (0.002, 0.0), (1.0, 6_500_000.0) })
            {
                List<SectionLine> lines = Section()
                    .Select(l => L(l.A.X * scale + shift, l.A.Y * scale + shift, l.B.X * scale + shift, l.B.Y * scale + shift, l.Layer))
                    .ToList();
                Assert.Equal(3, LongSectionPipes.Find(lines).Count);
            }
        }

        [Fact]
        public void CrossingAndNearbyPipesAreLeftOut()
        {
            List<SectionLine> lines = Section();
            // A nearby pipe from the first pit that runs past the second (as CSD plots them)...
            lines.AddRange(Pipe(0, 9.6, 42, 9.2, 0.3));
            // ...one that starts just after the second pit and ends on the third...
            lines.AddRange(Pipe(42, 12.6, 95, 15.3, 0.225));
            // ...one floating between structures, with its own end caps...
            lines.AddRange(Pipe(110, 12, 150, 13, 0.45));
            lines.Add(L(110, 12, 110, 12.45, "PIPE"));
            lines.Add(L(150, 13, 150, 13.45, "PIPE"));
            // ...and a smaller one that does run pit to pit, alongside the main pipe.
            lines.AddRange(Pipe(95, 16, 180, 22, 0.15));

            List<PipeQuad> all = LongSectionPipes.Find(lines);
            Assert.Equal(7, all.Count);

            List<PipeQuad> main = LongSectionPipes.MainLine(all, lines);
            Assert.Equal(3, main.Count);
            Assert.Equal(new[] { 0.0, 40, 95 }, main.Select(q => q.Corners[0].X));
            Assert.Equal(new[] { 0.9, 0.9, 0.525 }, main.Select(q => Math.Round(q.Depth, 6)));
        }

        [Fact]
        public void MainLineKeepsEverythingWhenThereAreNoStructureLines()
        {
            var lines = new List<SectionLine>();
            lines.AddRange(Pipe(0, 10, 40, 10.8, 0.9));
            lines.AddRange(Pipe(40, 11.2, 95, 14, 0.9));

            List<PipeQuad> all = LongSectionPipes.Find(lines);
            Assert.Equal(2, LongSectionPipes.MainLine(all, lines).Count);
        }

        [Theory]
        [InlineData("C410 STORMWATER LONG SECTIONS SHEET 1", "Stormwater")]
        [InlineData("C420 Sewer Long Sections", "Sewer")]
        [InlineData("SW LONG SECTIONS 2", "Stormwater")]
        [InlineData("SS-LONG SECTIONS", "Sewer")]
        public void ServiceComesFromTheLayoutName(string layout, string expected)
        {
            LongSectionHatchSettings s = Settings();
            Assert.True(s.IsLongSectionLayout(layout));
            Assert.Equal(expected, LongSectionPipes.Classify(s.Services, new[] { new[] { layout } })?.Name);
        }

        [Fact]
        public void ServiceFallsBackToXrefNameThenLabels()
        {
            LongSectionHatchSettings s = Settings();
            string[] layout = { "C400 LONG SECTIONS" };
            string[] labels =
            {
                "900Ø CORRUTECH SW SN8", "LINE SW1-12/1 ENTERS 450Ø IL 16.354", "150Ø CROSSING IL 18.275",
                "150Ø SEWER CROSSING IL 17.300", "525Ø CORRUTECH SW SN8",
            };

            Assert.Equal("Sewer", LongSectionPipes.Classify(s.Services, new[] { layout, new[] { "LS-SEWER-LINE1" }, labels })?.Name);
            // One sewer crossing doesn't make a stormwater section a sewer one.
            Assert.Equal("Stormwater", LongSectionPipes.Classify(s.Services, new[] { layout, new[] { "XR-LS01" }, labels })?.Name);
            Assert.Null(LongSectionPipes.Classify(s.Services, new[] { layout, new[] { "XR-LS01" }, new[] { "CROSSING", "ANSWER" } }));
        }

        [Fact]
        public void ShippedSettingsMatchTheOfficeLayers()
        {
            LongSectionHatchSettings s = Settings();
            Assert.Equal("SOLID", s.Pattern);
            Assert.True(s.MainPipeOnly);
            Assert.Equal("AE CIV DET - STORMWATER PIPE NEW", s.Services.Single(x => x.Name == "Stormwater").HatchLayer);
            Assert.Equal("AE CIV DET - SEWER PIPE NEW", s.Services.Single(x => x.Name == "Sewer").HatchLayer);
            Assert.True(s.IsLongSectionLayout("C410 Stormwater Long Sections"));
            Assert.False(s.IsLongSectionLayout("C100 GENERAL ARRANGEMENT"));
            Assert.Equal("PIPES", LongSectionPipes.BareLayer("LS-SW1|PIPES"));
        }
    }
}
