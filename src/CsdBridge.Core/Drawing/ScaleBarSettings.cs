using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CsdBridge.Core.Civil;

namespace CsdBridge.Core.Drawing
{
    /// <summary>
    /// A scale bar: for one scale (1:500), or a combined bar for a horizontal and a vertical
    /// scale (a long section at 1:1000 horizontally and 1:100 vertically).
    /// </summary>
    public readonly struct ScaleBarId : IEquatable<ScaleBarId>, IComparable<ScaleBarId>
    {
        public ScaleBarId(int scale)
        {
            Scale = scale;
            Vertical = null;
        }

        public ScaleBarId(int horizontal, int vertical)
        {
            Scale = horizontal;
            Vertical = vertical;
        }

        /// <summary>The scale; for a combined bar, the horizontal scale.</summary>
        public int Scale { get; }

        /// <summary>The vertical scale of a combined bar; null for an ordinary bar.</summary>
        public int? Vertical { get; }

        public bool IsCombined => Vertical.HasValue;

        public bool Equals(ScaleBarId other) => Scale == other.Scale && Vertical == other.Vertical;

        public override bool Equals(object? obj) => obj is ScaleBarId other && Equals(other);

        public override int GetHashCode() => (Scale * 397) ^ (Vertical ?? -1);

        /// <summary>Most detailed first; at the same scale, a combined bar before an ordinary one.</summary>
        public int CompareTo(ScaleBarId other)
        {
            int byDetail = Math.Min(Scale, Vertical ?? Scale).CompareTo(Math.Min(other.Scale, other.Vertical ?? other.Scale));
            if (byDetail != 0)
            {
                return byDetail;
            }

            if (IsCombined != other.IsCombined)
            {
                return IsCombined ? -1 : 1;
            }

            return Scale.CompareTo(other.Scale);
        }

        /// <summary>"1:500", or "H 1:1000 V 1:100".</summary>
        public override string ToString() => IsCombined
            ? string.Format(CultureInfo.InvariantCulture, "H 1:{0} V 1:{1}", Scale, Vertical)
            : "1:" + Scale.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The block for one horizontal and vertical scale, when its name doesn't follow the pattern.</summary>
    public sealed class CombinedScaleBar
    {
        [JsonPropertyName("horizontal")]
        public int Horizontal { get; set; }

        [JsonPropertyName("vertical")]
        public int Vertical { get; set; }

        [JsonPropertyName("block")]
        public string Block { get; set; } = "";
    }

    /// <summary>
    /// Where scale bars go on particular sheets. The first rule that fits a layout and a bar is
    /// used; anything the rule leaves out comes from the main settings.
    /// </summary>
    public sealed class ScaleBarPosition
    {
        /// <summary>Sheet size this is for: "A1", "A3", or a size in millimetres such as "841x594". Left out = any.</summary>
        [JsonPropertyName("sheet")]
        public string? Sheet { get; set; }

        /// <summary>Only layouts whose name contains this. Left out = any.</summary>
        [JsonPropertyName("layout_name_contains")]
        public string? LayoutNameContains { get; set; }

        /// <summary>"single" or "combined" to place only that kind of bar here. Left out = both.</summary>
        [JsonPropertyName("bars")]
        public string? Bars { get; set; }

        [JsonPropertyName("x")]
        public double? X { get; set; }

        [JsonPropertyName("y")]
        public double? Y { get; set; }

        [JsonPropertyName("stack_offset_x")]
        public double? StackOffsetX { get; set; }

        [JsonPropertyName("stack_offset_y")]
        public double? StackOffsetY { get; set; }

        [JsonPropertyName("layer")]
        public string? Layer { get; set; }

        [JsonPropertyName("scale")]
        public double? Scale { get; set; }

        /// <summary>How many of sheet, layout name and kind of bar the rule names: the more, the more particular.</summary>
        internal int Particularity =>
            (string.IsNullOrWhiteSpace(Sheet) ? 0 : 1) + (string.IsNullOrWhiteSpace(LayoutNameContains) ? 0 : 1) + (string.IsNullOrWhiteSpace(Bars) ? 0 : 1);

        [JsonPropertyName("rotation")]
        public double? RotationDeg { get; set; }
    }

    /// <summary>A layout's sheet, for choosing where its scale bars go.</summary>
    public sealed class SheetInfo
    {
        public SheetInfo(string layoutName, double paperWidth = 0, double paperHeight = 0)
        {
            LayoutName = layoutName ?? "";
            PaperWidth = paperWidth;
            PaperHeight = paperHeight;
        }

        public string LayoutName { get; }

        /// <summary>Paper size in millimetres; 0 when the layout has no page setup.</summary>
        public double PaperWidth { get; }

        public double PaperHeight { get; }

        /// <summary>"A1", or "600x400" for a size that isn't an A sheet; "" when unknown.</summary>
        public string Name => SheetSizes.Name(PaperWidth, PaperHeight);
    }

    /// <summary>Sheet sizes by name.</summary>
    public static class SheetSizes
    {
        private const double Tolerance = 12; // printers' own "A1" sizes differ by a few millimetres

        private static readonly (string Name, double Long, double Short)[] Iso =
        {
            ("A0", 1189, 841), ("A1", 841, 594), ("A2", 594, 420), ("A3", 420, 297), ("A4", 297, 210),
            ("B1", 1000, 707), ("B2", 707, 500),
        };

        public static string Name(double width, double height)
        {
            if (!(width > 0 && height > 0))
            {
                return "";
            }

            double longer = Math.Max(width, height), shorter = Math.Min(width, height);
            foreach ((string name, double l, double s) in Iso)
            {
                if (Math.Abs(longer - l) <= Tolerance && Math.Abs(shorter - s) <= Tolerance)
                {
                    return name;
                }
            }

            return string.Format(CultureInfo.InvariantCulture, "{0:0}x{1:0}", longer, shorter);
        }

        /// <summary>True when <paramref name="wanted"/> ("A1", "841x594", "594 x 841") describes the sheet.</summary>
        public static bool Matches(string? wanted, double width, double height)
        {
            if (string.IsNullOrWhiteSpace(wanted))
            {
                return true;
            }

            if (!(width > 0 && height > 0))
            {
                return false;
            }

            string text = wanted!.Trim();
            if (string.Equals(text, Name(width, height), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            Match m = Regex.Match(text, @"^(\d+(?:\.\d+)?)\s*[xX×]\s*(\d+(?:\.\d+)?)$");
            if (!m.Success)
            {
                return false;
            }

            double a = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            double b = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return Math.Abs(Math.Max(a, b) - Math.Max(width, height)) <= Tolerance
                && Math.Abs(Math.Min(a, b) - Math.Min(width, height)) <= Tolerance;
        }
    }

    /// <summary>One scale bar to insert on a layout.</summary>
    public sealed class ScaleBarPlacement
    {
        public ScaleBarPlacement(ScaleBarId bar, string blockName, double x, double y, string layer, double scale, double rotationDeg)
        {
            Bar = bar;
            BlockName = blockName;
            X = x;
            Y = y;
            Layer = layer;
            BlockScale = scale;
            RotationDeg = rotationDeg;
        }

        public ScaleBarId Bar { get; }

        /// <summary>The scale; for a combined bar, the horizontal scale.</summary>
        public int Scale => Bar.Scale;

        public string BlockName { get; }
        public double X { get; }
        public double Y { get; }
        public string Layer { get; }

        /// <summary>The size the block is inserted at.</summary>
        public double BlockScale { get; }

        public double RotationDeg { get; }
    }

    /// <summary>
    /// How AISCALEBARS works for an office: which block is the scale bar for each scale, the
    /// combined blocks for a horizontal and a vertical scale, and where bars go on the sheet.
    /// Loaded from scale-bars.json. Positions are paper-space millimetres. The defaults are the
    /// file CSD-Bridge ships with.
    /// </summary>
    public sealed class ScaleBarSettings
    {
        /// <summary>Block name for a scale; {0} is the scale ("Scale Bar 750" for 1:750). Empty = only the blocks listed.</summary>
        [JsonPropertyName("block_name")]
        public string BlockName { get; set; } = "Scale Bar {0}";

        /// <summary>Blocks whose names don't follow the pattern, by scale: { "750": "SB 750 metric" }.</summary>
        [JsonPropertyName("blocks")]
        public Dictionary<string, string> Blocks { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Block name for a combined bar; {h} is the horizontal scale and {v} the vertical
        /// ("Scale Bar H{h} V{v}"). Empty = only the combined blocks listed.
        /// </summary>
        [JsonPropertyName("combined_block_name")]
        public string CombinedBlockName { get; set; } = "";

        /// <summary>Combined blocks whose names don't follow the pattern.</summary>
        [JsonPropertyName("combined_blocks")]
        public List<CombinedScaleBar> CombinedBlocks { get; set; } = new List<CombinedScaleBar>();

        /// <summary>Insertion point of the first scale bar on the layout.</summary>
        [JsonPropertyName("x")]
        public double X { get; set; } = 821;

        [JsonPropertyName("y")]
        public double Y { get; set; } = 68.5;

        /// <summary>From one bar to the next when a layout has several.</summary>
        [JsonPropertyName("stack_offset_x")]
        public double StackOffsetX { get; set; }

        [JsonPropertyName("stack_offset_y")]
        public double StackOffsetY { get; set; } = 40;

        /// <summary>The old name for <see cref="StackOffsetY"/>, still read from older settings.</summary>
        [JsonPropertyName("stack_offset")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? StackOffset
        {
            get => null;
            set => StackOffsetY = value ?? StackOffsetY;
        }

        [JsonPropertyName("layer")]
        public string Layer { get; set; } = "AE GEN TBL - SCALE BAR";

        /// <summary>The size the blocks are inserted at.</summary>
        [JsonPropertyName("scale")]
        public double Scale { get; set; } = 1;

        [JsonPropertyName("rotation")]
        public double RotationDeg { get; set; }

        /// <summary>Different places for particular sheet sizes, layouts or kinds of bar. The first that fits is used.</summary>
        [JsonPropertyName("positions")]
        public List<ScaleBarPosition> Positions { get; set; } = new List<ScaleBarPosition>();

        // ------------------------------------------------------------------ the file

        public static ScaleBarSettings Load(string path) =>
            JsonSerializer.Deserialize<ScaleBarSettings>(File.ReadAllText(path), RoadConventions.JsonOptions)
            ?? throw new InvalidDataException($"{path} is empty.");

        /// <summary>Write the file, with the notes at the top that explain it.</summary>
        public void Save(string path)
        {
            var options = new JsonSerializerOptions(RoadConventions.JsonOptions) { WriteIndented = true };
            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(path, Header + JsonSerializer.Serialize(this, options) + "\n");
        }

        public const string Header =
            "// CSD-Bridge: how AISCALEBARS puts scale bars on layouts.\n" +
            "//\n" +
            "// AISCALEBARS reads each layout's viewport scales and inserts the scale bar block for each.\n" +
            "// AISCALEBARSETUP fills this file in from scale bars you pick in a drawing; it can also be\n" +
            "// edited by hand. The blocks must already be in the drawing (in the office template, say).\n" +
            "//\n" +
            "// block_name           The block for a scale; {0} is the scale: \"Scale Bar {0}\" is\n" +
            "//                      \"Scale Bar 750\" for 1:750.\n" +
            "// blocks               Blocks named some other way, by scale: { \"750\": \"SB 750 metric\" }.\n" +
            "// combined_block_name  A combined bar for a horizontal and a vertical scale (long\n" +
            "//                      sections); {h} and {v} are the scales: \"Scale Bar H{h} V{v}\".\n" +
            "// combined_blocks      Combined blocks named some other way:\n" +
            "//                      [ { \"horizontal\": 1000, \"vertical\": 100, \"block\": \"LS Bar 1000-100\" } ]\n" +
            "//                      A sheet gets a combined bar where its text states both scales, as in\n" +
            "//                      \"SCALES: HORIZONTAL 1:1000 VERTICAL 1:100\", and the block is in the\n" +
            "//                      drawing. Otherwise it gets a bar for each scale, as before.\n" +
            "// x, y                 Where the first bar goes, in paper millimetres.\n" +
            "// stack_offset_x, _y   From one bar to the next when a layout has several.\n" +
            "// layer, scale, rotation  The layer the bars go on, the size they are inserted at, and\n" +
            "//                      their rotation in degrees.\n" +
            "// positions            Other places for particular sheets. The first that fits is used, and\n" +
            "//                      anything it leaves out comes from the settings above:\n" +
            "//                      [ { \"sheet\": \"A3\", \"x\": 395, \"y\": 30, \"scale\": 0.5 },\n" +
            "//                        { \"bars\": \"combined\", \"x\": 700, \"y\": 68.5 },\n" +
            "//                        { \"layout_name_contains\": \"detail\", \"x\": 40, \"y\": 20 } ]\n" +
            "//                      \"sheet\" is A0 to A4 or a size such as \"841x594\"; \"bars\" is \"single\" or\n" +
            "//                      \"combined\".\n" +
            "//\n" +
            "// To share one file across the office, put it on a network drive and set \"scale_bar_file\"\n" +
            "// in %APPDATA%\\CsdBridge\\settings.json on each PC.\n";

        // ------------------------------------------------------------------ blocks

        /// <summary>True when combined bars have been set up at all.</summary>
        [JsonIgnore]
        public bool HasCombined => !string.IsNullOrWhiteSpace(CombinedBlockName) || CombinedBlocks.Any(c => !string.IsNullOrWhiteSpace(c.Block));

        /// <summary>The block set up for a bar, or null if there is none.</summary>
        public string? BlockFor(ScaleBarId bar)
        {
            if (bar.IsCombined)
            {
                CombinedScaleBar? listed = CombinedBlocks.FirstOrDefault(c => c.Horizontal == bar.Scale && c.Vertical == bar.Vertical && !string.IsNullOrWhiteSpace(c.Block));
                if (listed != null)
                {
                    return listed.Block.Trim();
                }

                return HasBothPlaceholders(CombinedBlockName)
                    ? Fill(CombinedBlockName, "{h}", bar.Scale).Replace("{v}", bar.Vertical!.Value.ToString(CultureInfo.InvariantCulture)).Trim()
                    : null;
            }

            if (Blocks.TryGetValue(bar.Scale.ToString(CultureInfo.InvariantCulture), out string? named) && !string.IsNullOrWhiteSpace(named))
            {
                return named.Trim();
            }

            return !string.IsNullOrWhiteSpace(BlockName) && BlockName.Contains("{0}") ? Fill(BlockName, "{0}", bar.Scale).Trim() : null;
        }

        /// <summary>If <paramref name="blockName"/> is one of the scale bar blocks, which bar it is.</summary>
        public bool TryParseBlockName(string? blockName, out ScaleBarId bar)
        {
            bar = default;
            if (string.IsNullOrWhiteSpace(blockName))
            {
                return false;
            }

            string name = blockName!.Trim();
            CombinedScaleBar? combined = CombinedBlocks.FirstOrDefault(c => string.Equals(c.Block?.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (combined != null && combined.Horizontal > 0 && combined.Vertical > 0)
            {
                bar = new ScaleBarId(combined.Horizontal, combined.Vertical);
                return true;
            }

            foreach (KeyValuePair<string, string> listed in Blocks)
            {
                if (string.Equals(listed.Value?.Trim(), name, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(listed.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int listedScale) && listedScale > 0)
                {
                    bar = new ScaleBarId(listedScale);
                    return true;
                }
            }

            if (HasBothPlaceholders(CombinedBlockName))
            {
                string pattern = "^" + Regex.Escape(CombinedBlockName.Trim()).Replace(@"\{h}", @"(?<h>\d+)").Replace(@"\{v}", @"(?<v>\d+)") + "$";
                Match m = Regex.Match(name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (m.Success && Number(m.Groups["h"].Value, out int h) && Number(m.Groups["v"].Value, out int v))
                {
                    bar = new ScaleBarId(h, v);
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(BlockName) && BlockName.Contains("{0}"))
            {
                string pattern = "^" + Regex.Escape(BlockName.Trim()).Replace(@"\{0}", @"(\d+)") + "$";
                Match m = Regex.Match(name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (m.Success && Number(m.Groups[1].Value, out int scale))
                {
                    bar = new ScaleBarId(scale);
                    return true;
                }
            }

            return false;
        }

        private static bool HasBothPlaceholders(string? pattern) =>
            !string.IsNullOrWhiteSpace(pattern) && pattern!.Contains("{h}") && pattern.Contains("{v}");

        private static string Fill(string pattern, string placeholder, int value) =>
            pattern.Replace(placeholder, value.ToString(CultureInfo.InvariantCulture));

        private static bool Number(string text, out int value) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

        // ------------------------------------------------------------------ positions

        /// <summary>
        /// Where each bar goes on a sheet: most detailed first at the insertion point, the rest a
        /// stack offset apart. Bars sent somewhere else by a position rule stack there separately.
        /// A bar with no block set up is left out.
        /// </summary>
        public IReadOnlyList<ScaleBarPlacement> Place(IEnumerable<ScaleBarId> bars, SheetInfo sheet)
        {
            var placed = new List<ScaleBarPlacement>();
            var countAt = new Dictionary<object, int>();
            foreach (ScaleBarId bar in bars.Distinct().OrderBy(b => b))
            {
                string? block = BlockFor(bar);
                if (block == null)
                {
                    continue;
                }

                ScaleBarPosition? rule = RuleFor(sheet, bar);
                object key = (object?)rule ?? this;
                countAt.TryGetValue(key, out int index);
                countAt[key] = index + 1;

                placed.Add(new ScaleBarPlacement(
                    bar,
                    block,
                    (rule?.X ?? X) + index * (rule?.StackOffsetX ?? StackOffsetX),
                    (rule?.Y ?? Y) + index * (rule?.StackOffsetY ?? StackOffsetY),
                    string.IsNullOrWhiteSpace(rule?.Layer) ? Layer : rule!.Layer!.Trim(),
                    rule?.Scale ?? Scale,
                    rule?.RotationDeg ?? RotationDeg));
            }

            return placed;
        }

        /// <summary>The first position rule that fits the sheet and the bar, or null for the main settings.</summary>
        public ScaleBarPosition? RuleFor(SheetInfo sheet, ScaleBarId bar)
        {
            foreach (ScaleBarPosition rule in Positions)
            {
                if (!SheetSizes.Matches(rule.Sheet, sheet.PaperWidth, sheet.PaperHeight))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(rule.LayoutNameContains)
                    && sheet.LayoutName.IndexOf(rule.LayoutNameContains!.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(rule.Bars)
                    && !string.Equals(rule.Bars!.Trim(), bar.IsCombined ? "combined" : "single", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return rule;
            }

            return null;
        }

        // ------------------------------------------------------------------ setting up from a drawing

        /// <summary>
        /// The naming pattern a block suggests: "SB-500" for 1:500 suggests "SB-{0}". Null when
        /// the scale isn't in the name, or is there more than once.
        /// </summary>
        public static string? SuggestPattern(string blockName, int scale)
        {
            MatchCollection found = WholeNumber(scale).Matches(blockName);
            return found.Count == 1 ? blockName.Substring(0, found[0].Index) + "{0}" + blockName.Substring(found[0].Index + found[0].Length) : null;
        }

        /// <summary>
        /// The naming pattern a combined block suggests: "LS 1000-100" for H 1:1000 V 1:100
        /// suggests "LS {h}-{v}". Null when either scale isn't in the name exactly once.
        /// </summary>
        public static string? SuggestCombinedPattern(string blockName, int horizontal, int vertical)
        {
            if (horizontal == vertical)
            {
                return null;
            }

            MatchCollection h = WholeNumber(horizontal).Matches(blockName);
            MatchCollection v = WholeNumber(vertical).Matches(blockName);
            if (h.Count != 1 || v.Count != 1)
            {
                return null;
            }

            var cuts = new[] { (h[0].Index, h[0].Length, "{h}"), (v[0].Index, v[0].Length, "{v}") }.OrderByDescending(c => c.Index);
            string pattern = blockName;
            foreach ((int index, int length, string placeholder) in cuts)
            {
                pattern = pattern.Substring(0, index) + placeholder + pattern.Substring(index + length);
            }

            return pattern;
        }

        private static Regex WholeNumber(int n) =>
            new Regex(@"(?<!\d)" + n.ToString(CultureInfo.InvariantCulture) + @"(?!\d)", RegexOptions.CultureInvariant);

        /// <summary>The numbers in a block's name, in order: a first guess at the scales it is for.</summary>
        public static IReadOnlyList<int> NumbersIn(string blockName)
        {
            var numbers = new List<int>();
            foreach (Match m in Regex.Matches(blockName ?? "", @"\d+"))
            {
                if (Number(m.Value, out int n))
                {
                    numbers.Add(n);
                }
            }

            return numbers;
        }

        /// <summary>
        /// Make <paramref name="blockName"/> the block for a scale. With <paramref name="asPattern"/>
        /// and a name that has the scale in it, every scale is named the same way; otherwise
        /// only this scale uses the block. Returns what changed, to tell the user.
        /// </summary>
        public string LearnSingle(string blockName, int scale, bool asPattern)
        {
            string name = blockName.Trim();
            string key = scale.ToString(CultureInfo.InvariantCulture);
            string? pattern = asPattern ? SuggestPattern(name, scale) : null;
            if (pattern != null)
            {
                BlockName = pattern;
                Blocks.Remove(key);
                return $"scale bars are named '{pattern}' ({{0}} is the scale)";
            }

            if (string.Equals(BlockFor(new ScaleBarId(scale)), name, StringComparison.OrdinalIgnoreCase))
            {
                return $"1:{key} already uses block '{name}'";
            }

            Blocks[key] = name;
            return $"1:{key} uses block '{name}'";
        }

        /// <summary>Make <paramref name="blockName"/> the combined block for a horizontal and a vertical scale.</summary>
        public string LearnCombined(string blockName, int horizontal, int vertical, bool asPattern)
        {
            string name = blockName.Trim();
            var bar = new ScaleBarId(horizontal, vertical);
            string? pattern = asPattern ? SuggestCombinedPattern(name, horizontal, vertical) : null;
            CombinedBlocks.RemoveAll(c => c.Horizontal == horizontal && c.Vertical == vertical);
            if (pattern != null)
            {
                CombinedBlockName = pattern;
                return $"combined scale bars are named '{pattern}' ({{h}} and {{v}} are the scales)";
            }

            if (string.Equals(BlockFor(bar), name, StringComparison.OrdinalIgnoreCase))
            {
                return $"{bar} already uses block '{name}'";
            }

            CombinedBlocks.Add(new CombinedScaleBar { Horizontal = horizontal, Vertical = vertical, Block = name });
            return $"{bar} uses block '{name}'";
        }

        /// <summary>
        /// Set where bars go. With no <paramref name="sheet"/> and no <paramref name="bars"/> this
        /// is the main position; otherwise the position rule for that sheet size and kind of bar
        /// ("single" or "combined"), added if there isn't one. Layer, size and rotation are set
        /// too when given.
        /// </summary>
        public string SetPosition(string? sheet, string? bars, double x, double y, string? layer = null, double? scale = null, double? rotationDeg = null)
        {
            sheet = string.IsNullOrWhiteSpace(sheet) ? null : sheet!.Trim();
            bars = string.IsNullOrWhiteSpace(bars) ? null : bars!.Trim().ToLowerInvariant();
            string where = string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###})", x, y);
            if (sheet == null && bars == null)
            {
                X = x;
                Y = y;
                Layer = string.IsNullOrWhiteSpace(layer) ? Layer : layer!.Trim();
                Scale = scale ?? Scale;
                RotationDeg = rotationDeg ?? RotationDeg;
                return $"scale bars go at {where}";
            }

            ScaleBarPosition? rule = Positions.FirstOrDefault(p =>
                string.Equals(p.Sheet?.Trim() ?? "", sheet ?? "", StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.Bars?.Trim() ?? "", bars ?? "", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(p.LayoutNameContains));
            if (rule == null)
            {
                rule = new ScaleBarPosition { Sheet = sheet, Bars = bars };

                // The first rule that fits is the one used, so a rule for one kind of bar on one
                // sheet size goes ahead of a rule for either alone.
                int at = Positions.FindIndex(p => p.Particularity < rule.Particularity);
                Positions.Insert(at < 0 ? Positions.Count : at, rule);
            }

            rule.X = x;
            rule.Y = y;
            rule.Layer = string.IsNullOrWhiteSpace(layer) ? rule.Layer : layer!.Trim();
            rule.Scale = scale ?? rule.Scale;
            rule.RotationDeg = rotationDeg ?? rule.RotationDeg;
            string what = bars == null ? "scale bars" : bars + " scale bars";
            return $"{what}{(sheet == null ? "" : " on " + sheet + " sheets")} go at {where}";
        }

        /// <summary>A few lines saying what is set up, for the command line.</summary>
        public string Describe()
        {
            var text = new StringBuilder();
            text.Append("  Blocks:    ").Append(string.IsNullOrWhiteSpace(BlockName) ? "(no naming pattern)" : $"'{BlockName}' ({{0}} is the scale)");
            foreach (KeyValuePair<string, string> b in Blocks.OrderBy(b => b.Key.Length).ThenBy(b => b.Key, StringComparer.Ordinal))
            {
                text.Append($"\n             1:{b.Key} uses '{b.Value}'");
            }

            text.Append("\n  Combined:  ").Append(!HasCombined ? "none set up" : string.IsNullOrWhiteSpace(CombinedBlockName) ? "(no naming pattern)" : $"'{CombinedBlockName}' ({{h}} and {{v}} are the scales)");
            foreach (CombinedScaleBar c in CombinedBlocks)
            {
                text.Append($"\n             {new ScaleBarId(c.Horizontal, c.Vertical)} uses '{c.Block}'");
            }

            text.Append(string.Format(CultureInfo.InvariantCulture,
                "\n  Position:  ({0:0.###}, {1:0.###}), next bar ({2:0.###}, {3:0.###}) further on; layer {4}, size {5:0.###}, rotation {6:0.###}",
                X, Y, StackOffsetX, StackOffsetY, Layer, Scale, RotationDeg));
            foreach (ScaleBarPosition p in Positions)
            {
                var when = new List<string>();
                if (!string.IsNullOrWhiteSpace(p.Bars))
                {
                    when.Add(p.Bars!.Trim() + " bars");
                }

                if (!string.IsNullOrWhiteSpace(p.Sheet))
                {
                    when.Add(p.Sheet!.Trim() + " sheets");
                }

                if (!string.IsNullOrWhiteSpace(p.LayoutNameContains))
                {
                    when.Add($"layouts named *{p.LayoutNameContains!.Trim()}*");
                }

                text.Append(string.Format(CultureInfo.InvariantCulture, "\n             {0}: ({1:0.###}, {2:0.###})",
                    when.Count > 0 ? string.Join(", ", when) : "everything else", p.X ?? X, p.Y ?? Y));
            }

            return text.ToString();
        }
    }
}
