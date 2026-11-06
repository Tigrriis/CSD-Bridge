using System.Linq;
using CsdBridge.Core.Drawing;
using Xunit;

namespace CsdBridge.Core.Tests
{
    public class ScaleBarTests
    {
        [Theory]
        [InlineData(1000.0 / 750, 750)]
        [InlineData(1.0, 1000)]
        [InlineData(0.5, 2000)]
        [InlineData(10.0, 100)]
        [InlineData(1000.0 / 250 + 1e-9, 250)]
        public void Metre_drawings_read_paper_mm_per_metre(double customScale, int expected)
        {
            Assert.Equal(expected, ScaleBars.Candidates(customScale, millimetreDrawing: false)[0]);
        }

        [Fact]
        public void Millimetre_drawings_prefer_the_millimetre_reading()
        {
            Assert.Equal(new[] { 20, 20000 }, ScaleBars.Candidates(0.05, millimetreDrawing: true));
        }

        [Fact]
        public void A_metre_drawing_with_mm_insunits_still_finds_the_metre_scale()
        {
            // 1:750 in metres; read as millimetres it would be 1:0.75, which isn't a scale.
            Assert.Equal(new[] { 750 }, ScaleBars.Candidates(1000.0 / 750, millimetreDrawing: true));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(1000.0 / 743.2)]
        public void Bad_or_odd_scales_give_no_candidates(double customScale)
        {
            Assert.Empty(ScaleBars.Candidates(customScale, millimetreDrawing: false));
        }

        [Theory]
        [InlineData("SCALES: HORIZONTAL 1:50 VERTICAL 1:20", new[] { 50, 20 })]
        [InlineData("SCALE 1:100", new[] { 100 })]
        [InlineData("Scale 1 : 2,000", new[] { 2000 })]
        [InlineData("HORIZONTAL SCALE 1:500  VERTICAL SCALE 1:100", new[] { 500, 100 })]
        [InlineData("SCALE 1:500 @ A1 (1:1000 @ A3)", new[] { 500 })]
        [InlineData("BATTER 1:4", new int[0])]
        [InlineData("CL-01 CH 0.000 to CH 25.000", new int[0])]
        [InlineData("DETAIL A - NOT TO SCALE 1:4", new int[0])]
        [InlineData("SCALE: N.T.S.", new int[0])]
        [InlineData("SCALE BAR", new int[0])]
        public void Scales_are_read_from_section_titles(string text, int[] expected)
        {
            Assert.Equal(expected, ScaleBars.ScalesInText(text));
        }

        [Fact]
        public void Block_names_round_trip()
        {
            var options = new ScaleBarOptions();
            Assert.Equal("Scale Bar 750", ScaleBars.BlockName(options, 750));
            Assert.True(ScaleBars.TryParseBlockName(options, "SCALE BAR 750", out int scale));
            Assert.Equal(750, scale);
            Assert.False(ScaleBars.TryParseBlockName(options, "Scale Bar 750 old", out _));
            Assert.False(ScaleBars.TryParseBlockName(options, "North Arrow", out _));
        }

        [Fact]
        public void Several_scales_stack_from_the_insertion_point()
        {
            var options = new ScaleBarOptions { X = 700, Y = 60, StackOffset = 40 };
            var placed = ScaleBars.Stack(new[] { 750, 250, 750, 1000 }, options);

            Assert.Equal(new[] { 250, 750, 1000 }, placed.Select(p => p.Scale));
            Assert.Equal(new[] { 60.0, 100.0, 140.0 }, placed.Select(p => p.Y));
            Assert.All(placed, p => Assert.Equal(700, p.X));
            Assert.Equal("Scale Bar 250", placed[0].BlockName);
        }
    }
}
