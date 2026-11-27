using System;
using System.IO;
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
            var settings = new ScaleBarSettings();
            Assert.Equal("Scale Bar 750", settings.BlockFor(new ScaleBarId(750)));
            Assert.True(settings.TryParseBlockName("SCALE BAR 750", out ScaleBarId bar));
            Assert.Equal(new ScaleBarId(750), bar);
            Assert.False(settings.TryParseBlockName("Scale Bar 750 old", out _));
            Assert.False(settings.TryParseBlockName("North Arrow", out _));
        }

        [Fact]
        public void Several_scales_stack_from_the_insertion_point()
        {
            var settings = new ScaleBarSettings { X = 700, Y = 60, StackOffsetY = 40 };
            var placed = settings.Place(new[] { 750, 250, 750, 1000 }.Select(n => new ScaleBarId(n)), new SheetInfo("Plan"));

            Assert.Equal(new[] { 250, 750, 1000 }, placed.Select(p => p.Scale));
            Assert.Equal(new[] { 60.0, 100.0, 140.0 }, placed.Select(p => p.Y));
            Assert.All(placed, p => Assert.Equal(700, p.X));
            Assert.Equal("Scale Bar 250", placed[0].BlockName);
            Assert.Equal("AE GEN TBL - SCALE BAR", placed[0].Layer);
        }

        // ------------------------------------------------------------------ an office's own blocks

        [Fact]
        public void A_block_can_be_named_for_one_scale()
        {
            var settings = new ScaleBarSettings { BlockName = "SB-{0}" };
            settings.Blocks["750"] = "SB 750 metric";

            Assert.Equal("SB-500", settings.BlockFor(new ScaleBarId(500)));
            Assert.Equal("SB 750 metric", settings.BlockFor(new ScaleBarId(750)));
            Assert.True(settings.TryParseBlockName("sb 750 METRIC", out ScaleBarId listed));
            Assert.Equal(new ScaleBarId(750), listed);
            Assert.True(settings.TryParseBlockName("SB-200", out ScaleBarId patterned));
            Assert.Equal(new ScaleBarId(200), patterned);
        }

        [Fact]
        public void With_no_pattern_only_the_listed_blocks_count()
        {
            var settings = new ScaleBarSettings { BlockName = "" };
            settings.Blocks["100"] = "Bar A";

            Assert.Equal("Bar A", settings.BlockFor(new ScaleBarId(100)));
            Assert.Null(settings.BlockFor(new ScaleBarId(200)));
            Assert.Empty(settings.Place(new[] { new ScaleBarId(200) }, new SheetInfo("Plan")));
        }

        [Fact]
        public void Combined_blocks_come_from_a_pattern_or_a_list()
        {
            var settings = new ScaleBarSettings { CombinedBlockName = "Scale Bar H{h} V{v}" };
            settings.CombinedBlocks.Add(new CombinedScaleBar { Horizontal = 500, Vertical = 50, Block = "LS Special" });
            var bar = new ScaleBarId(1000, 100);

            Assert.True(settings.HasCombined);
            Assert.Equal("Scale Bar H1000 V100", settings.BlockFor(bar));
            Assert.Equal("LS Special", settings.BlockFor(new ScaleBarId(500, 50)));
            Assert.True(settings.TryParseBlockName("scale bar h1000 v100", out ScaleBarId parsed));
            Assert.Equal(bar, parsed);
            Assert.True(parsed.IsCombined);
            Assert.Equal("H 1:1000 V 1:100", parsed.ToString());
            Assert.True(settings.TryParseBlockName("LS Special", out ScaleBarId listed));
            Assert.Equal(new ScaleBarId(500, 50), listed);

            Assert.False(new ScaleBarSettings().HasCombined);
            Assert.Null(new ScaleBarSettings().BlockFor(bar));
        }

        [Theory]
        [InlineData("SCALES: HORIZONTAL 1:50 VERTICAL 1:20", 50, 20)]
        [InlineData("HORIZONTAL SCALE 1:500  VERTICAL SCALE 1:100", 500, 100)]
        [InlineData("SCALE H 1:1000 V 1:100", 1000, 100)]
        [InlineData("SCALE 1:1000 (H) 1:100 (V)", 1000, 100)]
        [InlineData("Scales: Vert. 1:200, Horiz. 1:2,000", 2000, 200)]
        [InlineData("1:500 HORIZONTAL, 1:50 VERTICAL", 500, 50)]
        public void Horizontal_and_vertical_scales_are_read_together(string text, int horizontal, int vertical)
        {
            Assert.Equal((horizontal, vertical), ScaleBars.PairInText(text));
        }

        [Theory]
        [InlineData("SCALE 1:100")]
        [InlineData("SCALES 1:500 AND 1:100")]            // not said which is which
        [InlineData("BATTER 1:4 H 1:2 V")]                // H and V alone, in text that isn't about scales
        [InlineData("HORIZONTAL 1:50 VERTICAL 1:20 - NOT TO SCALE")]
        [InlineData("HORIZONTAL SCALE 1:500")]
        [InlineData("")]
        public void Text_without_both_scales_labelled_is_not_a_pair(string text)
        {
            Assert.Null(ScaleBars.PairInText(text));
        }

        // ------------------------------------------------------------------ what a layout gets

        private static LayoutScales Sheet(string name = "Plan", double w = 841, double h = 594) => new LayoutScales(new SheetInfo(name, w, h));

        [Fact]
        public void A_viewport_gets_the_bar_for_its_scale()
        {
            LayoutScales layout = Sheet();
            layout.Viewports.Add((1000.0 / 500, "viewport A"));
            layout.Viewports.Add((1000.0 / 200, "viewport B"));
            layout.Viewports.Add((1000.0 / 500, "viewport C"));

            ScaleBarPlan plan = ScaleBars.Plan(layout, new ScaleBarSettings(), _ => true);

            Assert.Equal(new[] { "Scale Bar 200", "Scale Bar 500" }, plan.Placements.Select(p => p.BlockName));
            Assert.Empty(plan.Problems);
            Assert.False(plan.FromSectionText);
        }

        [Fact]
        public void A_missing_block_is_reported_and_skipped()
        {
            LayoutScales layout = Sheet();
            layout.Viewports.Add((1000.0 / 500, "viewport A"));
            layout.Viewports.Add((1000.0 / 333.3, "viewport B"));

            ScaleBarPlan plan = ScaleBars.Plan(layout, new ScaleBarSettings(), name => name != "Scale Bar 500");

            Assert.Empty(plan.Placements);
            Assert.Contains("block 'Scale Bar 500' isn't loaded", plan.Problems);
            Assert.Contains(plan.Problems, p => p.StartsWith("viewport B has a non-standard scale"));
        }

        [Fact]
        public void A_section_sheet_gets_a_bar_for_each_scale_when_there_is_no_combined_block()
        {
            LayoutScales layout = Sheet("Long section 1");
            layout.SectionScales.AddRange(new[] { 1000, 100 });
            layout.Pairs.Add((1000, 100));

            ScaleBarPlan plan = ScaleBars.Plan(layout, new ScaleBarSettings(), _ => true);

            Assert.True(plan.FromSectionText);
            Assert.Equal(new[] { new ScaleBarId(100), new ScaleBarId(1000) }, plan.Placements.Select(p => p.Bar));
            Assert.Empty(plan.Problems); // combined bars aren't set up, so nothing is missing
        }

        [Fact]
        public void A_long_section_gets_one_combined_bar_in_place_of_two()
        {
            var settings = new ScaleBarSettings { CombinedBlockName = "Scale Bar H{h} V{v}" };
            LayoutScales layout = Sheet("Long section 1");
            layout.SectionScales.AddRange(new[] { 1000, 100 });
            layout.Pairs.Add((1000, 100));
            layout.Pairs.Add((1000, 100)); // stated on two sections

            // The office has the combined block but no ordinary 1:100 bar: that isn't a problem.
            ScaleBarPlan plan = ScaleBars.Plan(layout, settings, name => name != "Scale Bar 100");

            ScaleBarPlacement only = Assert.Single(plan.Placements);
            Assert.Equal("Scale Bar H1000 V100", only.BlockName);
            Assert.Equal(new ScaleBarId(1000, 100), only.Bar);
            Assert.Empty(plan.Problems);
        }

        [Fact]
        public void A_combined_bar_also_replaces_the_viewport_bar_at_the_same_scale()
        {
            var settings = new ScaleBarSettings { CombinedBlockName = "LS {h}-{v}" };
            LayoutScales layout = Sheet();
            layout.Viewports.Add((1000.0 / 1000, "the long section viewport"));
            layout.Viewports.Add((1000.0 / 250, "a detail"));
            layout.Pairs.Add((1000, 100)); // stated in the title block

            ScaleBarPlan plan = ScaleBars.Plan(layout, settings, _ => true);

            Assert.Equal(new[] { "LS 1000-100", "Scale Bar 250" }, plan.Placements.Select(p => p.BlockName));
        }

        [Fact]
        public void A_combined_block_that_is_not_in_the_drawing_falls_back_to_separate_bars()
        {
            var settings = new ScaleBarSettings { CombinedBlockName = "LS {h}-{v}" };
            LayoutScales layout = Sheet("Long section 1");
            layout.SectionScales.AddRange(new[] { 500, 50 });
            layout.Pairs.Add((500, 50));

            ScaleBarPlan plan = ScaleBars.Plan(layout, settings, name => name.StartsWith("Scale Bar"));

            Assert.Equal(new[] { new ScaleBarId(50), new ScaleBarId(500) }, plan.Placements.Select(p => p.Bar));
            Assert.Contains(plan.Problems, p => p.Contains("'LS 500-50' isn't loaded"));
        }

        // ------------------------------------------------------------------ where bars go

        [Fact]
        public void Bars_can_stack_sideways()
        {
            var settings = new ScaleBarSettings { X = 100, Y = 20, StackOffsetX = -120, StackOffsetY = 0 };
            var placed = settings.Place(new[] { new ScaleBarId(100), new ScaleBarId(500) }, new SheetInfo("Plan"));
            Assert.Equal(new[] { 100.0, -20.0 }, placed.Select(p => p.X));
            Assert.All(placed, p => Assert.Equal(20, p.Y));
        }

        [Fact]
        public void A_sheet_size_can_have_its_own_position_and_size()
        {
            var settings = new ScaleBarSettings();
            settings.Positions.Add(new ScaleBarPosition { Sheet = "A3", X = 395, Y = 30, Scale = 0.5, StackOffsetY = 20, Layer = "TITLE" });
            var bars = new[] { new ScaleBarId(100), new ScaleBarId(500) };

            var a1 = settings.Place(bars, new SheetInfo("Plan", 841, 594));
            Assert.Equal((821.0, 68.5), (a1[0].X, a1[0].Y));
            Assert.Equal(1, a1[0].BlockScale);

            var a3 = settings.Place(bars, new SheetInfo("Plan", 297, 420)); // portrait is still A3
            Assert.Equal(new[] { (395.0, 30.0), (395.0, 50.0) }, a3.Select(p => (p.X, p.Y)));
            Assert.All(a3, p => Assert.Equal(0.5, p.BlockScale));
            Assert.All(a3, p => Assert.Equal("TITLE", p.Layer));

            var unknown = settings.Place(bars, new SheetInfo("Plan")); // no page setup: the main position
            Assert.Equal(821.0, unknown[0].X);
        }

        [Fact]
        public void Combined_bars_and_named_layouts_can_go_somewhere_else()
        {
            var settings = new ScaleBarSettings { CombinedBlockName = "LS {h}-{v}" };
            settings.Positions.Add(new ScaleBarPosition { Bars = "combined", X = 700, Y = 68.5 });
            settings.Positions.Add(new ScaleBarPosition { LayoutNameContains = "detail", X = 40, Y = 20 });
            var bars = new[] { new ScaleBarId(100), new ScaleBarId(1000, 100), new ScaleBarId(500) };

            var plan = settings.Place(bars, new SheetInfo("Road plan", 841, 594));
            ScaleBarPlacement combined = plan.Single(p => p.Bar.IsCombined);
            Assert.Equal((700.0, 68.5), (combined.X, combined.Y));
            // The ordinary bars stack on their own at the main position: the combined bar doesn't take a place there.
            Assert.Equal(new[] { 68.5, 108.5 }, plan.Where(p => !p.Bar.IsCombined).Select(p => p.Y));

            var detail = settings.Place(new[] { new ScaleBarId(20) }, new SheetInfo("Kerb DETAILS", 841, 594));
            Assert.Equal((40.0, 20.0), (detail[0].X, detail[0].Y));
        }

        [Theory]
        [InlineData(841, 594, "A1")]
        [InlineData(594, 841, "A1")]
        [InlineData(1189, 841, "A0")]
        [InlineData(420, 297, "A3")]
        [InlineData(838, 590, "A1")]     // a plotter's own "A1"
        [InlineData(600, 400, "600x400")]
        [InlineData(0, 0, "")]
        public void Sheet_sizes_have_names(double w, double h, string expected)
        {
            Assert.Equal(expected, SheetSizes.Name(w, h));
            Assert.Equal(expected.Length > 0, SheetSizes.Matches(expected.Length > 0 ? expected : "A1", w, h));
        }

        [Fact]
        public void A_sheet_can_be_named_by_its_size()
        {
            Assert.True(SheetSizes.Matches("841 x 594", 594, 841));
            Assert.True(SheetSizes.Matches(null, 0, 0));       // a rule with no sheet fits anything
            Assert.False(SheetSizes.Matches("A3", 841, 594));
            Assert.False(SheetSizes.Matches("big", 841, 594));
        }

        // ------------------------------------------------------------------ setting up from a drawing

        [Fact]
        public void A_picked_block_can_give_the_naming_pattern()
        {
            Assert.Equal("SB-{0}", ScaleBarSettings.SuggestPattern("SB-500", 500));
            Assert.Equal("Bar {0}m", ScaleBarSettings.SuggestPattern("Bar 250m", 250));
            Assert.Null(ScaleBarSettings.SuggestPattern("SB-500", 50));        // 50 is only part of 500
            Assert.Null(ScaleBarSettings.SuggestPattern("100 Bar 100", 100));  // which one?
            Assert.Null(ScaleBarSettings.SuggestPattern("Scale Bar", 100));

            var settings = new ScaleBarSettings();
            Assert.Contains("'SB-{0}'", settings.LearnSingle("SB-500", 500, asPattern: true));
            Assert.Equal("SB-200", settings.BlockFor(new ScaleBarId(200)));
        }

        [Fact]
        public void A_picked_block_can_be_for_one_scale_only()
        {
            var settings = new ScaleBarSettings();
            Assert.Equal("1:750 uses block 'Odd Bar'", settings.LearnSingle("Odd Bar", 750, asPattern: true)); // no scale in the name
            Assert.Equal("Odd Bar", settings.BlockFor(new ScaleBarId(750)));
            Assert.Equal("Scale Bar 500", settings.BlockFor(new ScaleBarId(500)));
            Assert.Contains("already", settings.LearnSingle("Scale Bar 500", 500, asPattern: false));
            Assert.Single(settings.Blocks);
        }

        [Fact]
        public void A_picked_combined_block_can_give_the_pattern_or_be_listed()
        {
            Assert.Equal("LS {h}-{v}", ScaleBarSettings.SuggestCombinedPattern("LS 1000-100", 1000, 100));
            Assert.Equal("V{v} H{h}", ScaleBarSettings.SuggestCombinedPattern("V100 H1000", 1000, 100));
            Assert.Null(ScaleBarSettings.SuggestCombinedPattern("Long section bar", 1000, 100));
            Assert.Equal(new[] { 1000, 100 }, ScaleBarSettings.NumbersIn("LS 1000-100"));

            var settings = new ScaleBarSettings();
            settings.LearnCombined("Long section bar", 1000, 100, asPattern: true);
            Assert.Equal("Long section bar", settings.BlockFor(new ScaleBarId(1000, 100)));
            Assert.Null(settings.BlockFor(new ScaleBarId(500, 50)));

            settings.LearnCombined("LS 500-50", 500, 50, asPattern: true);
            Assert.Equal("LS {h}-{v}", settings.CombinedBlockName);
            Assert.Equal("LS 200-20", settings.BlockFor(new ScaleBarId(200, 20)));
            Assert.Equal("Long section bar", settings.BlockFor(new ScaleBarId(1000, 100))); // the listed one still wins
        }

        [Fact]
        public void A_picked_position_is_for_all_sheets_or_one_kind_of_sheet_or_bar()
        {
            var settings = new ScaleBarSettings();
            settings.SetPosition(null, null, 800, 50, "BARS", 2, 90);
            Assert.Equal((800.0, 50.0, "BARS", 2.0, 90.0), (settings.X, settings.Y, settings.Layer, settings.Scale, settings.RotationDeg));
            Assert.Empty(settings.Positions);

            settings.SetPosition("A3", null, 395, 30);
            settings.SetPosition(null, "Combined", 700, 60);
            settings.SetPosition("A3", "combined", 300, 30);
            settings.SetPosition("a3", null, 396, 31); // again: updates the rule, doesn't add another
            Assert.Equal(3, settings.Positions.Count);

            var a3 = new SheetInfo("LS", 420, 297);
            var a1 = new SheetInfo("LS", 841, 594);
            var combined = new ScaleBarId(1000, 100);
            Assert.Equal(300, settings.RuleFor(a3, combined)!.X);      // the most particular rule is found first
            Assert.Equal(396, settings.RuleFor(a3, new ScaleBarId(100))!.X);
            Assert.Equal(700, settings.RuleFor(a1, combined)!.X);
            Assert.Null(settings.RuleFor(a1, new ScaleBarId(100)));
        }

        // ------------------------------------------------------------------ the file

        [Fact]
        public void The_shipped_file_is_the_same_as_the_built_in_defaults()
        {
            ScaleBarSettings shipped = ScaleBarSettings.Load(Path.Combine(AppContext.BaseDirectory, "scale-bars.json"));
            var builtIn = new ScaleBarSettings();

            Assert.Equal(builtIn.BlockName, shipped.BlockName);
            Assert.Equal((builtIn.X, builtIn.Y, builtIn.StackOffsetX, builtIn.StackOffsetY), (shipped.X, shipped.Y, shipped.StackOffsetX, shipped.StackOffsetY));
            Assert.Equal((builtIn.Layer, builtIn.Scale, builtIn.RotationDeg), (shipped.Layer, shipped.Scale, shipped.RotationDeg));
            Assert.Empty(shipped.Blocks);
            Assert.Empty(shipped.Positions);
            Assert.False(shipped.HasCombined);
        }

        [Fact]
        public void Saving_keeps_the_notes_and_reads_back_the_same()
        {
            string path = Path.Combine(Path.GetTempPath(), "csd-bridge-bars-" + Guid.NewGuid().ToString("N"), "scale-bars.json");
            try
            {
                var settings = new ScaleBarSettings { BlockName = "SB-{0}", CombinedBlockName = "LS {h}-{v}", StackOffsetX = 5 };
                settings.Blocks["750"] = "Odd";
                settings.CombinedBlocks.Add(new CombinedScaleBar { Horizontal = 500, Vertical = 50, Block = "LS Special" });
                settings.Positions.Add(new ScaleBarPosition { Sheet = "A3", Bars = "combined", X = 300, Y = 30, RotationDeg = 90 });
                settings.Save(path);

                string text = File.ReadAllText(path);
                Assert.StartsWith("// CSD-Bridge: how AISCALEBARS puts scale bars on layouts.", text);
                Assert.DoesNotContain("\"stack_offset\":", text); // the old name isn't written
                ScaleBarSettings back = ScaleBarSettings.Load(path);
                Assert.Equal("Odd", back.BlockFor(new ScaleBarId(750)));
                Assert.Equal("LS Special", back.BlockFor(new ScaleBarId(500, 50)));
                Assert.Equal("LS 200-20", back.BlockFor(new ScaleBarId(200, 20)));
                Assert.Equal(5, back.StackOffsetX);
                ScaleBarPosition rule = Assert.Single(back.Positions);
                Assert.Equal(("A3", "combined", 300.0, 30.0, 90.0), (rule.Sheet, rule.Bars, rule.X, rule.Y, rule.RotationDeg));
                Assert.Null(rule.Scale); // left out, so it comes from the main settings
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
            }
        }

        [Fact]
        public void Older_settings_with_stack_offset_still_load()
        {
            // The shape of "scale_bars" in settings.json before the scale bar file existed.
            ScaleBarSettings old = System.Text.Json.JsonSerializer.Deserialize<ScaleBarSettings>(
                "{ \"block_name\": \"Bar {0}\", \"x\": 700, \"y\": 60, \"stack_offset\": -30, \"layer\": \"L\", \"scale\": 2, \"rotation\": 90 }")!;

            Assert.Equal(("Bar {0}", 700.0, 60.0, -30.0, "L", 2.0, 90.0), (old.BlockName, old.X, old.Y, old.StackOffsetY, old.Layer, old.Scale, old.RotationDeg));
            Assert.Equal(0, old.StackOffsetX);
        }

        [Fact]
        public void The_summary_says_what_is_set_up()
        {
            var settings = new ScaleBarSettings { CombinedBlockName = "LS {h}-{v}" };
            settings.Blocks["750"] = "Odd";
            settings.SetPosition("A3", null, 395, 30);
            string text = settings.Describe();

            Assert.Contains("'Scale Bar {0}'", text);
            Assert.Contains("1:750 uses 'Odd'", text);
            Assert.Contains("'LS {h}-{v}'", text);
            Assert.Contains("(821, 68.5)", text);
            Assert.Contains("A3 sheets: (395, 30)", text);
        }
    }
}
