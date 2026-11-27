using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CsdBridge.Core.Drawing
{
    /// <summary>What AISCALEBARS found on one layout.</summary>
    public sealed class LayoutScales
    {
        public LayoutScales(SheetInfo sheet)
        {
            Sheet = sheet;
        }

        public SheetInfo Sheet { get; }

        /// <summary>The scale of each viewport onto the model (paper millimetres per model unit), with a name for messages.</summary>
        public List<(double CustomScale, string Name)> Viewports { get; } = new List<(double, string)>();

        /// <summary>The drawing's units say millimetres.</summary>
        public bool MillimetreDrawing { get; set; }

        /// <summary>Scales stated in the text of the sheet's section xrefs. Used when the layout has no viewports.</summary>
        public List<int> SectionScales { get; } = new List<int>();

        /// <summary>Horizontal and vertical scales stated together in text on the sheet.</summary>
        public List<(int Horizontal, int Vertical)> Pairs { get; } = new List<(int, int)>();
    }

    /// <summary>The scale bars a layout should have, and anything that couldn't be done.</summary>
    public sealed class ScaleBarPlan
    {
        public ScaleBarPlan(IReadOnlyList<ScaleBarPlacement> placements, IReadOnlyList<string> problems, bool fromSectionText)
        {
            Placements = placements;
            Problems = problems;
            FromSectionText = fromSectionText;
        }

        public IReadOnlyList<ScaleBarPlacement> Placements { get; }
        public IReadOnlyList<string> Problems { get; }

        /// <summary>The scales came from the section xrefs' text, the layout having no viewports.</summary>
        public bool FromSectionText { get; }
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

        /// <summary>
        /// The scales stated in a line of drawing text, such as a section title's
        /// "SCALES: HORIZONTAL 1:50 VERTICAL 1:20" (50 and 20). Only text that mentions a scale
        /// counts, so batter slopes ("1:4") and other ratios are ignored, as is "NOT TO SCALE".
        /// When ratios are tagged with sheet sizes ("1:500 @ A1, 1:1000 @ A3"), only the
        /// largest sheet's are kept.
        /// </summary>
        public static IReadOnlyList<int> ScalesInText(string? text)
        {
            var result = new List<int>();
            if (string.IsNullOrEmpty(text)
                || text!.IndexOf("SCALE", StringComparison.OrdinalIgnoreCase) < 0
                || NotToScale.IsMatch(text))
            {
                return result;
            }

            var found = new List<(int Scale, int? Sheet)>();
            foreach (Match m in Ratio.Matches(text))
            {
                if (int.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0)
                {
                    found.Add((n, m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : (int?)null));
                }
            }

            int? biggestSheet = found.Where(f => f.Sheet.HasValue).Select(f => f.Sheet).Min();
            foreach (var (scale, sheet) in found)
            {
                if ((biggestSheet == null || sheet == null || sheet == biggestSheet) && !result.Contains(scale))
                {
                    result.Add(scale);
                }
            }

            return result;
        }

        /// <summary>"1:500", "1 : 2,000", optionally followed by a sheet size ("@ A1").</summary>
        private static readonly Regex Ratio = new Regex(
            @"\b1\s*:\s*(\d{1,3}(?:,\d{3})+|\d+)(?!\d|\.\d)(?:\s*@\s*A(\d))?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex NotToScale = new Regex(
            @"NOT\s+TO\s+SCALE|\bN\.?T\.?S\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// The horizontal and vertical scale stated together in a line of text, such as
        /// "SCALES: HORIZONTAL 1:1000 VERTICAL 1:100" or "SCALE 1:1000 (H) 1:100 (V)". Both must
        /// be labelled; null otherwise, and for "NOT TO SCALE".
        /// </summary>
        public static (int Horizontal, int Vertical)? PairInText(string? text)
        {
            if (string.IsNullOrEmpty(text) || NotToScale.IsMatch(text))
            {
                return null;
            }

            MatchCollection ratios = Ratio.Matches(text);
            if (ratios.Count < 2)
            {
                return null;
            }

            // The label is the word in front of each ratio ("HORIZONTAL 1:50") or, when the
            // first ratio has nothing in front, the word after each ("1:50 H").
            bool labelsInFront = AxisOf(text!.Substring(0, ratios[0].Index), last: true, out _) != null;
            int? horizontal = null, vertical = null;
            bool spelledOut = false;
            for (int i = 0; i < ratios.Count; i++)
            {
                int from = labelsInFront ? (i == 0 ? 0 : ratios[i - 1].Index + ratios[i - 1].Length) : ratios[i].Index + ratios[i].Length;
                int to = labelsInFront ? ratios[i].Index : (i + 1 < ratios.Count ? ratios[i + 1].Index : text.Length);
                char? axis = AxisOf(text.Substring(from, to - from), last: labelsInFront, out bool longForm);
                if (axis == null
                    || !int.TryParse(ratios[i].Groups[1].Value.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                    || n <= 0)
                {
                    continue;
                }

                spelledOut |= longForm;
                if (axis == 'H')
                {
                    horizontal ??= n;
                }
                else
                {
                    vertical ??= n;
                }
            }

            // "H" and "V" alone only count in text that is about scales.
            if (horizontal == null || vertical == null
                || (!spelledOut && text.IndexOf("SCALE", StringComparison.OrdinalIgnoreCase) < 0))
            {
                return null;
            }

            return (horizontal.Value, vertical.Value);
        }

        /// <summary>The last (or first) word in a piece of text that names an axis: 'H', 'V' or null.</summary>
        private static char? AxisOf(string text, bool last, out bool longForm)
        {
            longForm = false;
            char? found = null;
            foreach (Match word in Regex.Matches(text, "[A-Za-z]+"))
            {
                string w = word.Value.ToUpperInvariant();
                char? axis = w == "H" || w == "HZ" || w == "HOR" || w == "HORI" || w == "HORIZ" || w == "HORIZONTAL" ? 'H'
                    : w == "V" || w == "VER" || w == "VERT" || w == "VERTICAL" ? 'V'
                    : (char?)null;
                if (axis == null)
                {
                    continue;
                }

                found = axis;
                longForm = w.Length >= 3;
                if (!last)
                {
                    break;
                }
            }

            return found;
        }

        /// <summary>
        /// The scale bars for a layout. Each viewport gets the bar for its scale. A layout with
        /// no viewports (a section sheet) gets a bar for each scale stated in its sections' text.
        /// Where the sheet states a horizontal and a vertical scale together and the combined
        /// block for them is in the drawing, that one bar takes the place of the two.
        /// <paramref name="hasBlock"/> says whether a block is in the drawing.
        /// </summary>
        public static ScaleBarPlan Plan(LayoutScales layout, ScaleBarSettings settings, Func<string, bool> hasBlock)
        {
            var bars = new List<ScaleBarId>();
            var problems = new List<string>();
            void Problem(string text)
            {
                if (!problems.Contains(text))
                {
                    problems.Add(text);
                }
            }

            string Missing(ScaleBarId bar) => settings.BlockFor(bar) is string name
                ? $"block '{name}' isn't loaded"
                : $"no block is set up for {bar}";

            foreach ((double customScale, string name) in layout.Viewports)
            {
                IReadOnlyList<int> candidates = Candidates(customScale, layout.MillimetreDrawing);
                if (candidates.Count == 0)
                {
                    Problem($"{name} has a non-standard scale ({DescribeScale(customScale, layout.MillimetreDrawing)})");
                    continue;
                }

                int found = candidates.FirstOrDefault(n => settings.BlockFor(new ScaleBarId(n)) is string block && hasBlock(block));
                if (found > 0)
                {
                    bars.Add(new ScaleBarId(found));
                }
                else
                {
                    Problem(Missing(new ScaleBarId(candidates[0])));
                }
            }

            bool fromSectionText = layout.Viewports.Count == 0;
            if (fromSectionText)
            {
                foreach (int n in layout.SectionScales.Distinct())
                {
                    var bar = new ScaleBarId(n);
                    if (settings.BlockFor(bar) is string block && hasBlock(block))
                    {
                        bars.Add(bar);
                    }
                    else
                    {
                        Problem(Missing(bar));
                    }
                }
            }

            // One combined bar in place of a bar for each of the two scales.
            if (settings.HasCombined)
            {
                foreach ((int horizontal, int vertical) in layout.Pairs.Distinct())
                {
                    if (horizontal == vertical)
                    {
                        continue;
                    }

                    var combined = new ScaleBarId(horizontal, vertical);
                    if (settings.BlockFor(combined) is string block && hasBlock(block))
                    {
                        bars.RemoveAll(b => !b.IsCombined && (b.Scale == horizontal || b.Scale == vertical));
                        problems.RemoveAll(p => p == Missing(new ScaleBarId(horizontal)) || p == Missing(new ScaleBarId(vertical)));
                        bars.Add(combined);
                    }
                    else
                    {
                        Problem(Missing(combined) + ", so no combined bar was placed");
                    }
                }
            }

            return new ScaleBarPlan(settings.Place(bars, layout.Sheet), problems, fromSectionText);
        }

        /// <summary>A viewport scale as text, for messages.</summary>
        public static string DescribeScale(double customScale, bool millimetreDrawing)
        {
            if (!(customScale > 0))
            {
                return "zero";
            }

            double n = (millimetreDrawing ? 1.0 : 1000.0) / customScale;
            return "1:" + n.ToString("0.##", CultureInfo.InvariantCulture);
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
