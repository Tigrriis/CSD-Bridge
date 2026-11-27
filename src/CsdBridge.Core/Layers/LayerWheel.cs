using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CsdBridge.Core.Civil;

namespace CsdBridge.Core.Layers
{
    /// <summary>
    /// The layers on the AILAYERWHEEL wheel and how it behaves. Loaded from layer-wheel.json,
    /// one file for the whole office so the wheel is the same in every drawing.
    /// </summary>
    public sealed class LayerWheelSettings
    {
        /// <summary>
        /// Full layer names, in the order they go round the wheel. Layers are grouped by the
        /// words in front of the dash; groups appear in the order their first layer is listed.
        /// </summary>
        public List<string> Layers { get; set; } = new List<string>();

        /// <summary>How many words in front of the dash make rings; any further words stay with the last one.</summary>
        public int MaxTerms { get; set; } = 3;

        /// <summary>
        /// Letting go of a key while the pointer is on a layer picks it, so a keyboard shortcut
        /// can be held, the pointer flicked to a layer, and the key released.
        /// </summary>
        public bool ReleaseToPick { get; set; } = true;

        /// <summary>Text size in points.</summary>
        public double TextSize { get; set; } = 9;

        public static LayerWheelSettings Load(string path)
        {
            LayerWheelSettings settings =
                JsonSerializer.Deserialize<LayerWheelSettings>(File.ReadAllText(path), RoadConventions.JsonOptions)
                ?? throw new InvalidDataException($"{path} is empty.");
            settings.Layers = Clean(settings.Layers ?? new List<string>());
            return settings;
        }

        /// <summary>Write the file, with the notes at the top that explain it.</summary>
        public void Save(string path)
        {
            var options = new JsonSerializerOptions(RoadConventions.JsonOptions) { WriteIndented = true };
            var text = new StringBuilder();
            text.Append(Header);
            text.Append(JsonSerializer.Serialize(this, options)).Append('\n');

            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(path, text.ToString());
        }

        public const string Header =
            "// CSD-Bridge: the layers on the layer wheel (AILAYERWHEEL).\n" +
            "//\n" +
            "// With nothing selected, picking a layer on the wheel makes it the current layer. With\n" +
            "// objects selected, it moves them to that layer. AILAYERWHEELSETUP ticks layers on and off\n" +
            "// and writes this file; it can also be edited by hand.\n" +
            "//\n" +
            "// \"layers\" holds full layer names, in the order they go round the wheel (clockwise from\n" +
            "// the top). The words in front of the dash make the rings: \"AE CIV PLN - KERB NEW\" sits\n" +
            "// under AE, then CIV, then PLN. A ring with only one choice is folded into the middle.\n" +
            "// Layers a drawing doesn't have are shown greyed out, so every layer keeps its place.\n" +
            "//\n" +
            "// To share one file across the office, put it on a network drive and set\n" +
            "// \"layer_wheel_file\" in %APPDATA%\\CsdBridge\\settings.json on each PC (the setup window's\n" +
            "// \"Change file\" button does this).\n";

        /// <summary>Trimmed names without blanks or repeats (layer names ignore case), in the order given.</summary>
        public static List<string> Clean(IEnumerable<string> layers)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var clean = new List<string>();
            foreach (string? layer in layers)
            {
                string name = (layer ?? "").Trim();
                if (name.Length > 0 && seen.Add(name))
                {
                    clean.Add(name);
                }
            }

            return clean;
        }

        /// <summary>
        /// The new list after the setup window: layers already on the wheel keep their order, so
        /// they keep their place; newly chosen ones follow in name order (and so land at the end
        /// of their group).
        /// </summary>
        public static List<string> Merge(IEnumerable<string> current, IEnumerable<string> chosen)
        {
            List<string> wanted = Clean(chosen);
            var wantedSet = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
            List<string> kept = Clean(current).Where(wantedSet.Contains).ToList();
            var keptSet = new HashSet<string>(kept, StringComparer.OrdinalIgnoreCase);
            kept.AddRange(wanted.Where(l => !keptSet.Contains(l)).OrderBy(l => l, StringComparer.OrdinalIgnoreCase));
            return kept;
        }
    }

    /// <summary>A layer name split into the words in front of the dash and the name after it.</summary>
    public sealed class LayerNameParts
    {
        public LayerNameParts(IReadOnlyList<string> terms, string label)
        {
            Terms = terms;
            Label = label;
        }

        /// <summary>The grouping words, outermost first. Empty for a name without a dash.</summary>
        public IReadOnlyList<string> Terms { get; }

        /// <summary>What the wheel shows for the layer: the part after the dash.</summary>
        public string Label { get; }
    }

    public static class LayerNames
    {
        private static readonly char[] Spaces = { ' ', '\t' };

        /// <summary>
        /// "AE CIV HTC - ASPHALT TYPE 1" is AE / CIV / HTC and "ASPHALT TYPE 1". Everything in
        /// front of the first dash is the grouping, one word per level up to
        /// <paramref name="maxTerms"/>; further words stay with the last level. A name without
        /// a dash has no grouping.
        /// </summary>
        public static LayerNameParts Split(string name, int maxTerms = 3)
        {
            string trimmed = (name ?? "").Trim();
            int cut = trimmed.IndexOf(" - ", StringComparison.Ordinal);
            int after = cut + 3;
            if (cut < 0)
            {
                cut = trimmed.IndexOf('-');
                after = cut + 1;
            }

            if (cut <= 0)
            {
                return new LayerNameParts(Array.Empty<string>(), trimmed);
            }

            string[] words = trimmed.Substring(0, cut).Split(Spaces, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                return new LayerNameParts(Array.Empty<string>(), trimmed);
            }

            maxTerms = Math.Max(1, maxTerms);
            var terms = new List<string>();
            for (int i = 0; i < words.Length && i < maxTerms; i++)
            {
                terms.Add(i == maxTerms - 1 ? string.Join(" ", words.Skip(i)) : words[i]);
            }

            string label = trimmed.Substring(after).Trim();
            return new LayerNameParts(terms, label.Length > 0 ? label : trimmed);
        }
    }

    /// <summary>A group on the wheel (a word in front of the dash) or, at the tips, a layer.</summary>
    public sealed class WheelNode
    {
        internal WheelNode(string term, WheelNode? parent, string? layer = null, int layerIndex = -1)
        {
            Term = term;
            Parent = parent;
            Layer = layer;
            LayerIndex = layerIndex;
        }

        /// <summary>The group's word, or the layer's name after the dash.</summary>
        public string Term { get; }

        public WheelNode? Parent { get; internal set; }
        public List<WheelNode> Children { get; } = new List<WheelNode>();

        /// <summary>The full layer name, for a layer; null for a group.</summary>
        public string? Layer { get; }

        /// <summary>The layer's position in the list the wheel was built from; -1 for a group.</summary>
        public int LayerIndex { get; }

        public bool IsLeaf => Layer != null;

        /// <summary>The ring a group is drawn in, 1 nearest the middle. 0 for the middle itself.</summary>
        public int Depth { get; internal set; }

        /// <summary>Where the node's slice starts, in radians clockwise from 12 o'clock.</summary>
        public double Start { get; internal set; }

        public double Sweep { get; internal set; }
        public double Mid => Start + Sweep / 2;

        internal double Need { get; set; }

        public bool Contains(double angle) => angle >= Start && angle < Start + Sweep;

        /// <summary>True when this node is <paramref name="other"/> or sits under it.</summary>
        public bool IsWithin(WheelNode other)
        {
            for (WheelNode? n = this; n != null; n = n.Parent)
            {
                if (ReferenceEquals(n, other))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public static class LayerWheelTree
    {
        /// <summary>
        /// Group the layers by the words in front of the dash. Groups and layers keep the order
        /// they are first listed in. A level with only one group is folded away: its word goes
        /// to <paramref name="centre"/>, the label in the middle of the wheel.
        /// </summary>
        public static WheelNode Build(IReadOnlyList<string> layers, int maxTerms, out string centre)
        {
            var root = new WheelNode("", null);
            for (int i = 0; i < layers.Count; i++)
            {
                LayerNameParts parts = LayerNames.Split(layers[i], maxTerms);
                WheelNode at = root;
                foreach (string term in parts.Terms)
                {
                    WheelNode? next = at.Children.FirstOrDefault(c => !c.IsLeaf && string.Equals(c.Term, term, StringComparison.OrdinalIgnoreCase));
                    if (next == null)
                    {
                        next = new WheelNode(term, at);
                        at.Children.Add(next);
                    }

                    at = next;
                }

                at.Children.Add(new WheelNode(parts.Label, at, layers[i], i));
            }

            var folded = new List<string>();
            while (root.Children.Count == 1 && !root.Children[0].IsLeaf)
            {
                root = root.Children[0];
                folded.Add(root.Term);
                root.Parent = null;
            }

            centre = string.Join(" ", folded);
            SetDepth(root, 0);
            return root;
        }

        private static void SetDepth(WheelNode node, int depth)
        {
            node.Depth = depth;
            foreach (WheelNode child in node.Children)
            {
                SetDepth(child, depth + 1);
            }
        }
    }
}
