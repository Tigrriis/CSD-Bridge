using System;
using System.Collections.Generic;
using System.Linq;
using CsdBridge.Core.Civil;

namespace CsdBridge.Core.Layouts
{
    /// <summary>How one layer shows in the floating viewports of one layout.</summary>
    public enum ViewportLayerState
    {
        /// <summary>The layout has no floating viewports, so there is nothing to show or hide.</summary>
        NoViewports,

        /// <summary>Thawed in every viewport of the layout.</summary>
        Shown,

        /// <summary>Frozen (VP freeze) in every viewport of the layout.</summary>
        Hidden,

        /// <summary>Frozen in some of the layout's viewports and not others.</summary>
        Mixed,
    }

    /// <summary>
    /// The rules behind AIVPLAYERS, the layer-by-layout grid: a cell's state from its viewport
    /// counts, what a toggle does, and the layer and layout name filters. Kept free of BricsCAD
    /// so it can be unit-tested.
    /// </summary>
    public static class ViewportLayers
    {
        public static ViewportLayerState StateOf(int viewports, int frozenIn)
        {
            if (viewports <= 0)
            {
                return ViewportLayerState.NoViewports;
            }

            if (frozenIn <= 0)
            {
                return ViewportLayerState.Shown;
            }

            return frozenIn >= viewports ? ViewportLayerState.Hidden : ViewportLayerState.Mixed;
        }

        /// <summary>
        /// What toggling a selection does, judged from the cell it was started on (like ticking a
        /// group of check boxes): shown → hide, hidden or mixed → show. Null when that cell has
        /// no viewports; then the first selected cell that has some decides.
        /// </summary>
        public static bool? ShowWhenToggling(ViewportLayerState anchor, IEnumerable<ViewportLayerState>? others = null)
        {
            if (anchor == ViewportLayerState.NoViewports)
            {
                if (others == null)
                {
                    return null;
                }

                foreach (ViewportLayerState state in others)
                {
                    if (state != ViewportLayerState.NoViewports)
                    {
                        return state != ViewportLayerState.Shown;
                    }
                }

                return null;
            }

            return anchor != ViewportLayerState.Shown;
        }

        /// <summary>
        /// The layers a viewport needs changed: those to thaw (show) or freeze (hide) that aren't
        /// already that way. Empty when the viewport is already right.
        /// </summary>
        public static List<T> LayersToChange<T>(IEnumerable<T> layers, ICollection<T> frozenInViewport, bool show)
        {
            return layers.Distinct().Where(l => frozenInViewport.Contains(l) == show).ToList();
        }

        /// <summary>A layer from an attached drawing ("xref|layer").</summary>
        public static bool IsXrefLayer(string name) => name.IndexOf('|') >= 0;

        /// <summary>
        /// Name filter for the grid. Blank matches everything. Several patterns can be given,
        /// separated by commas; a name matches if any does. A pattern with * or ? is a wildcard
        /// over the whole name; plain text matches anywhere in the name. A pattern starting with
        /// ~ excludes the names it matches. Case is ignored.
        /// </summary>
        public static bool MatchesFilter(string name, string? filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                return true;
            }

            bool anyInclude = false, included = false;
            foreach (string raw in filter!.Split(','))
            {
                string pattern = raw.Trim();
                bool exclude = pattern.StartsWith("~", StringComparison.Ordinal);
                if (exclude)
                {
                    pattern = pattern.Substring(1).Trim();
                }

                if (pattern.Length == 0)
                {
                    continue;
                }

                bool match = pattern.IndexOfAny(new[] { '*', '?' }) >= 0
                    ? LongSectionPipes.WildcardMatch(name, pattern)
                    : name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;

                if (exclude)
                {
                    if (match)
                    {
                        return false;
                    }
                }
                else
                {
                    anyInclude = true;
                    included |= match;
                }
            }

            return !anyInclude || included;
        }

        /// <summary>Tooltip text for one cell.</summary>
        public static string Describe(string layer, string layout, int viewports, int frozenIn)
        {
            switch (StateOf(viewports, frozenIn))
            {
                case ViewportLayerState.NoViewports:
                    return $"{layout} has no viewports.";
                case ViewportLayerState.Shown:
                    return $"{layer}\nshown in {(viewports == 1 ? "the viewport" : $"all {viewports} viewports")} on {layout}";
                case ViewportLayerState.Hidden:
                    return $"{layer}\nfrozen in {(viewports == 1 ? "the viewport" : $"all {viewports} viewports")} on {layout}";
                default:
                    return $"{layer}\nfrozen in {frozenIn} of {viewports} viewports on {layout}";
            }
        }
    }
}
