using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsdBridge.Core.Drawing;

namespace CsdBridge.Core.Civil
{
    /// <summary>
    /// How AIPIPEHATCH hatches the pipes of Civil Site Design long sections: which layouts hold
    /// long sections, how a sewer section is told from a stormwater one, and the hatch layers.
    /// Loaded from long-section-hatch.json; the defaults here are used when the file is missing.
    /// </summary>
    public sealed class LongSectionHatchSettings
    {
        /// <summary>A layout is a long section sheet when its name contains one of these (case ignored).</summary>
        public List<string> LayoutNameContains { get; set; } = new List<string> { "long section" };

        /// <summary>
        /// Layers in the long section xref that hold the pipe lines (wildcards * and ?, without
        /// the "xref|" prefix). Empty: pipes are found by their shape alone.
        /// </summary>
        public List<string> PipeLayers { get; set; } = new List<string>();

        public string Pattern { get; set; } = "SOLID";

        /// <summary>Put the hatch behind the long section's linework.</summary>
        public bool SendToBack { get; set; } = true;

        public List<PipeService> Services { get; set; } = new List<PipeService>
        {
            new PipeService
            {
                Name = "Stormwater",
                Keywords = new List<string> { "STORMWATER", "STORM", "DRAINAGE", "SW", "SWD" },
                HatchLayer = "AE CIV DET - STORMWATER PIPE NEW",
                Color = 94,
                Rgb = new[] { 0, 166, 0 },
            },
            new PipeService
            {
                Name = "Sewer",
                Keywords = new List<string> { "SEWER", "SEWERAGE", "SEW", "SS" },
                HatchLayer = "AE CIV DET - SEWER PIPE NEW",
                Color = 1,
                Rgb = new[] { 255, 0, 0 },
            },
        };

        public static LongSectionHatchSettings Load(string path) =>
            JsonSerializer.Deserialize<LongSectionHatchSettings>(File.ReadAllText(path), RoadConventions.JsonOptions)
            ?? throw new InvalidDataException($"{path} is empty.");

        public bool IsLongSectionLayout(string layoutName) =>
            LayoutNameContains.Any(s => !string.IsNullOrWhiteSpace(s)
                && layoutName.IndexOf(s.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>A pipe service (stormwater, sewer): the words that identify it and where its hatch goes.</summary>
    public sealed class PipeService
    {
        public string Name { get; set; } = "";

        /// <summary>
        /// Words that mark this service in a layout, xref, layer name or label. Matched as whole
        /// words (digits and punctuation count as word breaks, so "SW" matches "LINE SW1-12/1").
        /// </summary>
        public List<string> Keywords { get; set; } = new List<string>();

        public string HatchLayer { get; set; } = "0";

        /// <summary>Colour index of the hatch layer, used only when the layer has to be created.</summary>
        public short? Color { get; set; }

        /// <summary>True colour of the hatch layer, used only when the layer has to be created.</summary>
        public int[]? Rgb { get; set; }

        /// <summary>How many of this service's keywords appear in the text.</summary>
        public int Hits(string text)
        {
            int hits = 0;
            foreach (string keyword in Keywords)
            {
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    continue;
                }

                string pattern = "(?<![A-Za-z])" + Regex.Escape(keyword.Trim()) + "(?![A-Za-z])";
                hits += Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;
            }

            return hits;
        }
    }

    /// <summary>A straight piece of linework from a long section, in the long section's own coordinates.</summary>
    public readonly struct SectionLine
    {
        public SectionLine(Point2 a, Point2 b, string layer)
        {
            A = a;
            B = b;
            Layer = layer;
        }

        public Point2 A { get; }
        public Point2 B { get; }
        public string Layer { get; }
    }

    /// <summary>One pipe run in a long section: the area between its invert and obvert lines.</summary>
    public sealed class PipeQuad
    {
        public PipeQuad(Point2 invertStart, Point2 invertEnd, Point2 obvertEnd, Point2 obvertStart, string layer)
        {
            Corners = new[] { invertStart, invertEnd, obvertEnd, obvertStart };
            Layer = layer;
        }

        /// <summary>Invert start, invert end, obvert end, obvert start (a closed outline).</summary>
        public IReadOnlyList<Point2> Corners { get; }

        /// <summary>The layer the pipe lines are on.</summary>
        public string Layer { get; }

        /// <summary>Drawn height of the pipe (its diameter times the vertical exaggeration).</summary>
        public double Depth => Corners[3].Y - Corners[0].Y;
    }

    /// <summary>Finds the pipes in the linework of a CSD long section, and tells sewer from stormwater.</summary>
    public static class LongSectionPipes
    {
        /// <summary>
        /// A pipe run is drawn as two parallel lines, one above the other, starting and ending
        /// at the same chainages (its invert and obvert between two pits). Nothing else in a long
        /// section has that shape: ground lines aren't parallel pairs on one layer, pit walls are
        /// vertical and the data band is level. Level pairs are only taken as pipes on a layer
        /// that also has sloping pipes of about the same height, or on a layer named in
        /// <paramref name="pipeLayers"/>.
        /// </summary>
        public static List<PipeQuad> Find(IReadOnlyList<SectionLine> lines, IReadOnlyList<string>? pipeLayers = null)
        {
            bool filtered = pipeLayers != null && pipeLayers.Any(p => !string.IsNullOrWhiteSpace(p));
            var candidates = new List<SectionLine>();
            double minX = double.MaxValue, maxX = double.MinValue;
            foreach (SectionLine line in lines)
            {
                if (filtered && !pipeLayers!.Any(p => WildcardMatch(line.Layer, p)))
                {
                    continue;
                }

                minX = Math.Min(minX, Math.Min(line.A.X, line.B.X));
                maxX = Math.Max(maxX, Math.Max(line.A.X, line.B.X));
                candidates.Add(line.A.X <= line.B.X ? line : new SectionLine(line.B, line.A, line.Layer));
            }

            var quads = new List<PipeQuad>();
            if (candidates.Count < 2)
            {
                return quads;
            }

            double tol = Math.Max((maxX - minX) * 1e-6, 1e-9);
            candidates.RemoveAll(l => l.B.X - l.A.X <= tol); // pit walls, ticks and other vertical lines

            var levelGroups = new List<List<SectionLine>>();
            foreach (List<SectionLine> group in Group(candidates, tol))
            {
                List<SectionLine> sloping = group.Where(l => Math.Abs(l.B.Y - l.A.Y) > tol).OrderBy(l => l.A.Y).ToList();
                List<SectionLine> level = group.Where(l => Math.Abs(l.B.Y - l.A.Y) <= tol).OrderBy(l => l.A.Y).ToList();
                if (level.Count >= 2)
                {
                    levelGroups.Add(level);
                }

                // The lowest line and the highest one parallel to it: a pipe drawn with wall
                // lines as well is still hatched over its full height.
                while (sloping.Count >= 2)
                {
                    SectionLine invert = sloping[0];
                    int top = 0;
                    for (int i = 1; i < sloping.Count; i++)
                    {
                        if (Parallel(invert, sloping[i], tol))
                        {
                            top = i;
                        }
                    }

                    if (top > 0 && sloping[top].A.Y - invert.A.Y > tol)
                    {
                        quads.Add(Quad(invert, sloping[top]));
                        sloping.RemoveRange(0, top + 1);
                    }
                    else
                    {
                        sloping.RemoveAt(0);
                    }
                }
            }

            // Level pipes. Without named pipe layers, only where the layer already has sloping
            // pipes of a similar height, so the rows of the data band aren't mistaken for pipes.
            var depths = new Dictionary<string, (double Min, double Max)>(StringComparer.OrdinalIgnoreCase);
            foreach (PipeQuad q in quads)
            {
                depths[q.Layer] = depths.TryGetValue(q.Layer, out var d)
                    ? (Math.Min(d.Min, q.Depth), Math.Max(d.Max, q.Depth))
                    : (q.Depth, q.Depth);
            }

            foreach (List<SectionLine> level in levelGroups)
            {
                for (int i = 0; i + 1 < level.Count; i++)
                {
                    double gap = level[i + 1].A.Y - level[i].A.Y;
                    if (gap <= tol)
                    {
                        continue;
                    }

                    bool known = depths.TryGetValue(level[i].Layer, out var range);
                    if (filtered ? (!known || (gap >= range.Min * 0.4 && gap <= range.Max * 2.5))
                                 : (known && gap >= range.Min * 0.6 && gap <= range.Max * 1.6))
                    {
                        quads.Add(Quad(level[i], level[i + 1]));
                        i++;
                    }
                }
            }

            return quads.OrderBy(q => q.Corners[0].X).ThenBy(q => q.Corners[0].Y).ToList();
        }

        /// <summary>
        /// The service a long section belongs to. <paramref name="sources"/> are groups of names
        /// in order of trust (layout name, xref name, pipe layers, labels); the first group in
        /// which one service's keywords clearly outnumber the others' decides. Null if none does.
        /// </summary>
        public static PipeService? Classify(IReadOnlyList<PipeService> services, IEnumerable<IEnumerable<string>> sources)
        {
            foreach (IEnumerable<string> source in sources)
            {
                var hits = new int[services.Count];
                foreach (string text in source)
                {
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    for (int i = 0; i < services.Count; i++)
                    {
                        hits[i] += services[i].Hits(text);
                    }
                }

                int best = -1, second = 0;
                for (int i = 0; i < hits.Length; i++)
                {
                    if (best < 0 || hits[i] > hits[best])
                    {
                        second = best < 0 ? 0 : hits[best];
                        best = i;
                    }
                    else if (hits[i] > second)
                    {
                        second = hits[i];
                    }
                }

                // "Clearly": at least twice the runner-up, so a stormwater section that mentions
                // one sewer crossing is still stormwater.
                if (best >= 0 && hits[best] > 0 && hits[best] >= 2 * second)
                {
                    return services[best];
                }
            }

            return null;
        }

        /// <summary>The layer name without an xref prefix ("LS-SW1|PIPES" gives "PIPES").</summary>
        public static string BareLayer(string layer)
        {
            int bar = layer.LastIndexOf('|');
            return bar >= 0 ? layer.Substring(bar + 1) : layer;
        }

        public static bool WildcardMatch(string text, string pattern)
        {
            string regex = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>Lines on the same layer that start at the same X and end at the same X.</summary>
        private static IEnumerable<List<SectionLine>> Group(List<SectionLine> lines, double tol)
        {
            foreach (IGrouping<string, SectionLine> byLayer in lines.GroupBy(l => l.Layer, StringComparer.OrdinalIgnoreCase))
            {
                List<SectionLine> sorted = byLayer.OrderBy(l => l.A.X).ToList();
                int start = 0;
                while (start < sorted.Count)
                {
                    int end = start + 1;
                    while (end < sorted.Count && sorted[end].A.X - sorted[end - 1].A.X <= tol)
                    {
                        end++;
                    }

                    List<SectionLine> sameStart = sorted.GetRange(start, end - start).OrderBy(l => l.B.X).ToList();
                    int s = 0;
                    while (s < sameStart.Count)
                    {
                        int e = s + 1;
                        while (e < sameStart.Count && sameStart[e].B.X - sameStart[e - 1].B.X <= tol)
                        {
                            e++;
                        }

                        if (e - s >= 2)
                        {
                            yield return sameStart.GetRange(s, e - s);
                        }

                        s = e;
                    }

                    start = end;
                }
            }
        }

        private static bool Parallel(SectionLine lower, SectionLine upper, double tol)
        {
            double gapStart = upper.A.Y - lower.A.Y;
            double gapEnd = upper.B.Y - lower.B.Y;
            return Math.Abs(gapStart - gapEnd) <= Math.Max(Math.Abs(gapStart), Math.Abs(gapEnd)) * 0.005 + tol;
        }

        private static PipeQuad Quad(SectionLine invert, SectionLine obvert) =>
            new PipeQuad(invert.A, invert.B, obvert.B, obvert.A, invert.Layer);
    }
}
