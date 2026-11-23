using System;
using System.Collections.Generic;

namespace CsdBridge.Core.Rendering
{
    /// <summary>
    /// A plain single-stroke font for the schematic view. Each character is a few
    /// polylines on a grid 4 wide and 6 tall; lower-case letters are drawn as small
    /// capitals. It only has to be readable, not match the drawing's fonts: the view
    /// scales each piece of text to the box BricsCAD reports for the real one.
    /// </summary>
    internal static class StrokeFont
    {
        /// <summary>Distance from one character to the next, in units of the capital height.</summary>
        public const double Advance = 0.9;

        private const double GridHeight = 6.0;
        private const double SmallCaps = 0.72;

        // Each polyline is a run of points, two digits (x then y) per point; polylines are separated by spaces.
        private static readonly Dictionary<char, string> Glyphs = new Dictionary<char, string>
        {
            ['A'] = "002640 1333", ['B'] = "00063645443303 3342413000", ['C'] = "4130100105163645", ['D'] = "00062645412000",
            ['E'] = "40000646 0333", ['F'] = "000646 0333", ['G'] = "45361605011030414323", ['H'] = "0006 4046 0343",
            ['I'] = "1030 2026 1636", ['J'] = "0110203136", ['K'] = "0006 460340", ['L'] = "060040",
            ['M'] = "0006234640", ['N'] = "00064046", ['O'] = "100105163645413010", ['P'] = "00063645443303",
            ['Q'] = "100105163645413010 2240", ['R'] = "00063645443303 2340", ['S'] = "011030414233130405163645", ['T'] = "0646 2026",
            ['U'] = "060110304146", ['V'] = "062046", ['W'] = "0610233046", ['X'] = "0046 0640",
            ['Y'] = "062346 2320", ['Z'] = "06460040",
            ['0'] = "100105163645413010", ['1'] = "152620 1030", ['2'] = "05163645440040", ['3'] = "0516364544334241301001 1333",
            ['4'] = "30360242", ['5'] = "460603334241301001", ['6'] = "4536160501103041423303", ['7'] = "064610",
            ['8'] = "13040516364544331302011030414233", ['9'] = "0110304145361605041343",
            ['.'] = "2021", [','] = "2110", ['-'] = "1333", ['+'] = "1333 2224", ['/'] = "0046", ['\\'] = "0640",
            ['('] = "30212536", [')'] = "10212516", [':'] = "2122 2425", [';'] = "2110 2425", ['='] = "0242 0444",
            ['%'] = "0046 0506 4041", ['#'] = "1016 3036 0242 0444", ['\''] = "2426", ['"'] = "1416 3436", ['_'] = "0040",
            ['&'] = "40141526353401102042", ['<'] = "400346", ['>'] = "004306", ['?'] = "05163645442322 2120",
            ['!'] = "2622 2120", ['*'] = "1244 1442 2125", ['['] = "30101636", [']'] = "10303616",
            ['°'] = "1516262515", ['Ø'] = "100105163645413010 0046", ['⌀'] = "100105163645413010 0046",
            ['±'] = "1434 2325 0141", ['@'] = "1030361610",
        };

        private const string Unknown = "1030361610";

        /// <summary>
        /// The strokes of one line of text. The baseline starts at the origin, capitals are
        /// 1 high, and each character is <see cref="Advance"/> wide.
        /// </summary>
        public static void Strokes(string text, Action<double, double, double, double> segment)
        {
            double x = 0;
            foreach (char raw in text)
            {
                if (!char.IsWhiteSpace(raw))
                {
                    bool small = char.IsLower(raw);
                    char c = char.ToUpperInvariant(raw);
                    if (!Glyphs.TryGetValue(c, out string? glyph))
                    {
                        glyph = Unknown;
                    }

                    double sy = (small ? SmallCaps : 1) / GridHeight;
                    double sx = Advance * 0.78 / 4;
                    foreach (string polyline in glyph.Split(' '))
                    {
                        for (int i = 0; i + 3 < polyline.Length; i += 2)
                        {
                            segment(
                                x + (polyline[i] - '0') * sx, (polyline[i + 1] - '0') * sy,
                                x + (polyline[i + 2] - '0') * sx, (polyline[i + 3] - '0') * sy);
                        }
                    }
                }

                x += Advance;
            }
        }
    }
}
