using CsdBridge.Core.Agent;
using Xunit;

namespace CsdBridge.Core.Tests
{
    public class EffortPolicyTests
    {
        [Theory]
        [InlineData("move these 200 to the right", EffortMode.Quick)]
        [InlineData("Rotate the column 45 degrees", EffortMode.Quick)]
        [InlineData("make it 300 wider", EffortMode.Quick)]
        [InlineData("delete the old grid", EffortMode.Quick)]
        [InlineData("what layer is the slab on?", EffortMode.Quick)]
        [InlineData("how many piers are there", EffortMode.Quick)]
        [InlineData("Draw a 6000 x 4000 slab outline with a 2400 grid and dimension it", EffortMode.Normal)]
        [InlineData("set out piers every 1800 along the selected wall", EffortMode.Normal)]
        [InlineData("put a 600 x 600 column at a point I pick", EffortMode.Normal)]
        [InlineData("make a title for this drawing", EffortMode.Normal)]
        [InlineData("dimension these", EffortMode.Normal)]
        [InlineData("Draw the level 2 framing plan from the grid", EffortMode.Thorough)]
        [InlineData("Add a section through the footing", EffortMode.Thorough)]
        [InlineData("Draw the slab. Add the grid. Dimension everything. Then label the beams.", EffortMode.Thorough)]
        [InlineData("1. draw the slab\n2. add grids\n3. dimension", EffortMode.Thorough)]
        public void Auto_picks_a_sensible_tier(string prompt, EffortMode expected)
        {
            Assert.Equal(expected, EffortPolicy.Classify(prompt).Tier);
        }

        [Fact]
        public void Long_requests_are_thorough()
        {
            string prompt = string.Join(" ", System.Linq.Enumerable.Repeat("word", 70));
            var (tier, reason) = EffortPolicy.Classify(prompt);
            Assert.Equal(EffortMode.Thorough, tier);
            Assert.Contains("long", reason);
        }

        [Fact]
        public void Manual_modes_map_to_the_configured_levels()
        {
            var levels = new EffortLevels { Quick = "medium", Normal = "high", Thorough = "max" };
            Assert.Equal("medium", EffortPolicy.Choose(EffortMode.Quick, "draw the whole layout", 0, levels).Effort);
            Assert.Equal("max", EffortPolicy.Choose(EffortMode.Thorough, "move it", 0, levels).Effort);

            EffortChoice auto = EffortPolicy.Choose(EffortMode.Auto, "move it left 100", 0, levels);
            Assert.True(auto.Automatic);
            Assert.Equal("medium", auto.Effort);
            Assert.Equal("Quick (auto: a simple edit)", auto.Describe());
            Assert.Equal("Thorough (set by you)", EffortPolicy.Choose(EffortMode.Thorough, "x", 0, levels).Describe());
        }

        [Theory]
        [InlineData("Auto", EffortMode.Auto, true)]
        [InlineData(" thorough ", EffortMode.Thorough, true)]
        [InlineData("quick", EffortMode.Quick, true)]
        [InlineData("fast", EffortMode.Auto, false)]
        public void Parses_mode_names(string text, EffortMode expected, bool ok)
        {
            Assert.Equal(ok, EffortPolicy.TryParseMode(text, out EffortMode mode));
            Assert.Equal(expected, mode);
        }
    }
}
