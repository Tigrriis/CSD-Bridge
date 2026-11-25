using System.Collections.Generic;
using CsdBridge.Core.Layouts;
using Xunit;

namespace CsdBridge.Core.Tests
{
    /// <summary>The rules behind AIVPLAYERS, the layer-by-layout viewport grid.</summary>
    public class ViewportLayersTests
    {
        [Theory]
        [InlineData(0, 0, ViewportLayerState.NoViewports)]
        [InlineData(3, 0, ViewportLayerState.Shown)]
        [InlineData(3, 3, ViewportLayerState.Hidden)]
        [InlineData(1, 1, ViewportLayerState.Hidden)]
        [InlineData(3, 1, ViewportLayerState.Mixed)]
        public void StateFromViewportCounts(int viewports, int frozenIn, ViewportLayerState expected)
        {
            Assert.Equal(expected, ViewportLayers.StateOf(viewports, frozenIn));
        }

        [Fact]
        public void ToggleFollowsTheCellItStartedOn()
        {
            Assert.False(ViewportLayers.ShowWhenToggling(ViewportLayerState.Shown));
            Assert.True(ViewportLayers.ShowWhenToggling(ViewportLayerState.Hidden));
            Assert.True(ViewportLayers.ShowWhenToggling(ViewportLayerState.Mixed));
            Assert.Null(ViewportLayers.ShowWhenToggling(ViewportLayerState.NoViewports));
        }

        [Fact]
        public void ToggleFromAnEmptyLayoutUsesTheNextCellWithViewports()
        {
            var others = new[] { ViewportLayerState.NoViewports, ViewportLayerState.Shown, ViewportLayerState.Hidden };
            Assert.False(ViewportLayers.ShowWhenToggling(ViewportLayerState.NoViewports, others));
            Assert.Null(ViewportLayers.ShowWhenToggling(ViewportLayerState.NoViewports, new[] { ViewportLayerState.NoViewports }));
        }

        [Fact]
        public void OnlyLayersNotAlreadyRightAreChanged()
        {
            var frozen = new HashSet<string> { "A", "B" };
            Assert.Equal(new[] { "A", "B" }, ViewportLayers.LayersToChange(new[] { "A", "B", "C", "A" }, frozen, show: true));
            Assert.Equal(new[] { "C" }, ViewportLayers.LayersToChange(new[] { "A", "B", "C" }, frozen, show: false));
            Assert.Empty(ViewportLayers.LayersToChange(new[] { "C" }, frozen, show: true));
        }

        [Theory]
        [InlineData("AE CIV - SEWER PIPE", "", true)]
        [InlineData("AE CIV - SEWER PIPE", "sewer", true)]
        [InlineData("AE CIV - SEWER PIPE", "storm", false)]
        [InlineData("AE CIV - SEWER PIPE", "storm, sewer", true)]
        [InlineData("AE CIV - SEWER PIPE", "AE*PIPE", true)]
        [InlineData("AE CIV - SEWER PIPE", "*SEWER", false)]
        [InlineData("LS-SW1|PIPES", "LS-SW?|*", true)]
        [InlineData("LS-SW1|PIPES", "~LS-*", false)]
        [InlineData("AE CIV - SEWER PIPE", "~LS-*", true)]
        [InlineData("LS-SW1|PIPES", "pipe, ~ls-sw1|*", false)]
        [InlineData("LS-SW1|TEXT", "pipe, ~ls-sw2|*", false)]
        public void NameFilter(string name, string filter, bool expected)
        {
            Assert.Equal(expected, ViewportLayers.MatchesFilter(name, filter));
        }

        [Fact]
        public void XrefLayersHaveABar()
        {
            Assert.True(ViewportLayers.IsXrefLayer("LS-SW1|PIPES"));
            Assert.False(ViewportLayers.IsXrefLayer("AE CIV - SEWER PIPE"));
        }

        [Fact]
        public void DescribesEachState()
        {
            Assert.Contains("all 2 viewports", ViewportLayers.Describe("L", "Sheet 1", 2, 0));
            Assert.Contains("frozen in 1 of 2", ViewportLayers.Describe("L", "Sheet 1", 2, 1));
            Assert.Contains("frozen in the viewport", ViewportLayers.Describe("L", "Sheet 1", 1, 1));
            Assert.Contains("no viewports", ViewportLayers.Describe("L", "Sheet 1", 0, 0));
        }
    }
}
