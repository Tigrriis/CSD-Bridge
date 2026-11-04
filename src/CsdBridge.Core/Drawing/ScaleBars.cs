using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CsdBridge.Core.Drawing
{
    /// <summary>
    /// Where AISCALEBARS puts scale bars on each layout. Paper-space millimetres.
    /// Defaults are the office standard; settings.json "scale_bars" overrides them.
    /// </summary>
    public sealed class ScaleBarOptions
    {
        /// <summary>Block name for a scale; {0} is the scale denominator ("Scale Bar 750").</summary>
        [JsonPropertyName("block_name")]
        public string BlockName { get; set; } = "Scale Bar {0}";

        /// <summary>Insertion point of the first scale bar on the layout.</summary>
        [JsonPropertyName("x")]
        public double X { get; set; } = 0;

        [JsonPropertyName("y")]
        public double Y { get; set; } = 0;

        [JsonPropertyName("layer")]
        public string Layer { get; set; } = "AE CIV ANO - ALL PLANS";

        /// <summary>Block insert scale.</summary>
        [JsonPropertyName("scale")]
        public double Scale { get; set; } = 1;

        [JsonPropertyName("rotation")]
        public double RotationDeg { get; set; } = 0;

        /// <summary>Y distance from one bar to the next when a layout has several scales (negative stacks downward).</summary>
        [JsonPropertyName("stack_offset")]
        public double StackOffset { get; set; } = 40;
    }

    /// <summary>One scale bar to insert on a layout.</summary>
    public sealed class ScaleBarPlacement
    {
        public ScaleBarPlacement(int scale, string blockName, double x, double y)
        {
            Scale = scale;
            BlockName = blockName;
            X = x;
            Y = y;
        }

        public int Scale { get; }

        public string BlockName { get; }

        public double X { get; }

        public double Y { get; }
    }

    /// <summary>
    /// Works out which scale bars a layout needs from its viewports' scales. No CAD
    /// dependency, so it is unit-tested; the BricsCAD side only reads and writes the drawing.
    /// </summary>
    public static class ScaleBars
    {
        /// <summary>A viewport scale counts as 1:N if it is within this fraction of N.</summary>
        private const double Tolerance = 0.0001;

        /// <summary>
        /// The scale denominators a viewport's CustomScale (paper mm per model unit) could mean,
        /// best guess first. Civil drawings are in metres (1:750 is CustomScale 1.333), so metres
        /// come first unless the drawing's units say millimetres. Many metre drawings have
        /// INSUNITS left at millimetres, so the other reading is offered as a fallback: the
        /// caller uses the first one whose block is loaded.
        /// </summary>
        public static IReadOnlyList<int> Candidates(double customScale, bool millimetreDrawing)
        {
            var result = new List<int>(2);
            if (!(customScale > 0) || double.IsInfinity(customScale))
            {
                return result;
            }

            double metres = 1000.0 / customScale;
            double millimetres = 1.0 / customScale;
            foreach (double raw in millimetreDrawing ? new[] { millimetres, metres } : new[] { metres, millimetres })
            {
                int? n = Round(raw);
                if (n.HasValue && !result.Contains(n.Value))
                {
                    result.Add(n.Value);
                }
            }

            return result;
        }

        /// <summary>The block name for 1:<paramref name="scale"/>.</summary>
        public static string BlockName(ScaleBarOptions options, int scale) =>
            string.Format(CultureInfo.InvariantCulture, options.BlockName, scale);

        /// <summary>If <paramref name="blockName"/> is a scale bar block, its scale.</summary>
        public static bool TryParseBlockName(ScaleBarOptions options, string? blockName, out int scale)
        {
            scale = 0;
            if (string.IsNullOrEmpty(blockName) || !options.BlockName.Contains("{0}"))
            {
                return false;
            }

            string pattern = "^" + Regex.Escape(options.BlockName).Replace(@"\{0}", @"(\d+)") + "$";
            Match m = Regex.Match(blockName, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out scale) && scale > 0;
        }

        /// <summary>
        /// Positions for a layout's scale bars: one per distinct scale, smallest denominator
        /// (most detailed) at the insertion point, the rest stacked StackOffset apart.
        /// </summary>
        public static IReadOnlyList<ScaleBarPlacement> Stack(IEnumerable<int> scales, ScaleBarOptions options)
        {
            return scales
                .Distinct()
                .OrderBy(s => s)
                .Select((s, i) => new ScaleBarPlacement(s, BlockName(options, s), options.X, options.Y + i * options.StackOffset))
                .ToList();
        }

        private static int? Round(double raw)
        {
            if (double.IsNaN(raw) || double.IsInfinity(raw) || raw < 0.5 || raw > int.MaxValue)
            {
                return null;
            }

            double n = Math.Round(raw);
            return Math.Abs(raw - n) <= n * Tolerance ? (int)n : (int?)null;
        }
    }
}
