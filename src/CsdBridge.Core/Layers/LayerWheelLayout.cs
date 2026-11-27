using System;
using System.Collections.Generic;
using System.Linq;

namespace CsdBridge.Core.Layers
{
    /// <summary>Sizes for the wheel, in pixels, and how wide a piece of text is.</summary>
    public sealed class WheelLayoutOptions
    {
        /// <summary>Smallest radius of the disc in the middle.</summary>
        public double CentreRadius { get; set; } = 34;

        /// <summary>Thickness of a ring of groups.</summary>
        public double RingThickness { get; set; } = 40;

        public double Gap { get; set; } = 3;

        /// <summary>Room each layer needs round the circle at the inner edge of its wedge: a line of text and a little more.</summary>
        public double LeafPitch { get; set; } = 23;

        /// <summary>Space between the edge of a wedge or ring and its text.</summary>
        public double LabelPadding { get; set; } = 7;

        public double MinBarLength { get; set; } = 60;

        /// <summary>The wheel may not be bigger than this (half the screen, say); longer names are cut short.</summary>
        public double MaxRadius { get; set; } = 480;

        /// <summary>Width of a layer's label.</summary>
        public Func<string, double> MeasureLeaf { get; set; } = s => s.Length * 6.6;

        /// <summary>Width of a group's label.</summary>
        public Func<string, double> MeasureGroup { get; set; } = s => s.Length * 8.0;
    }

    /// <summary>A group's slice of its ring.</summary>
    public sealed class WheelSector
    {
        internal WheelSector(WheelNode node, double inner, double outer)
        {
            Node = node;
            Inner = inner;
            Outer = outer;
        }

        public WheelNode Node { get; }
        public double Inner { get; }
        public double Outer { get; }
        public double Start => Node.Start;
        public double Sweep => Node.Sweep;
    }

    /// <summary>A layer: a wedge of the outer band, which is also the slice that picks it.</summary>
    public sealed class WheelLeaf
    {
        internal WheelLeaf(WheelNode node, double innerHit, double barStart, double barLength)
        {
            Node = node;
            InnerHit = innerHit;
            BarStart = barStart;
            BarLength = barLength;
        }

        public WheelNode Node { get; }
        public string Layer => Node.Layer!;
        public string Label => Node.Term;
        public int LayerIndex => Node.LayerIndex;

        /// <summary>The direction of the wedge's middle, and of its text, in radians clockwise from 12 o'clock.</summary>
        public double Angle => Node.Mid;

        /// <summary>
        /// The pointer picks this layer from this radius outwards, within the node's slice. The
        /// wedge is drawn from here too, so a layer with fewer words in front of its dash fills
        /// the rings it has no group in.
        /// </summary>
        public double InnerHit { get; }

        /// <summary>Where the text starts: the same radius for every layer.</summary>
        public double BarStart { get; }

        /// <summary>From the text's start to the rim: the same for every layer, so the wedges leave no gaps.</summary>
        public double BarLength { get; internal set; }

        /// <summary>On the left half the text reads towards the middle, so it is never upside down.</summary>
        public bool ReadsInwards => Angle > Math.PI;
    }

    public enum WheelHitKind
    {
        /// <summary>Beyond the wheel: a click here closes it.</summary>
        Outside,

        /// <summary>The disc in the middle.</summary>
        Centre,

        Group,
        Layer,

        /// <summary>On the wheel, but on nothing.</summary>
        Nothing,
    }

    public readonly struct WheelHit
    {
        public WheelHit(WheelHitKind kind, WheelNode? node = null)
        {
            Kind = kind;
            Node = node;
        }

        public WheelHitKind Kind { get; }
        public WheelNode? Node { get; }
    }

    /// <summary>
    /// Where everything on the layer wheel goes. Rings of groups surround the middle, one ring
    /// per word in front of the dash; each layer is a wedge of the band outside its group, and
    /// the wedges fill the band. A slice is as wide as its layers need, and the wheel grows until
    /// they all fit round it.
    ///
    /// Angles are radians clockwise from 12 o'clock; positions are pixels from the middle of
    /// the wheel with y downwards, as on screen. Free of BricsCAD and Windows so it can be
    /// unit-tested.
    /// </summary>
    public sealed class WheelLayout
    {
        private const double FullTurn = Math.PI * 2;
        private const double PickMargin = 14;

        private WheelLayout()
        {
        }

        /// <summary>The words every layer shares, shown in the middle (e.g. "AE CIV").</summary>
        public string CentreLabel { get; private set; } = "";

        public double CentreRadius { get; private set; }

        /// <summary>Rings of groups.</summary>
        public int Rings { get; private set; }

        /// <summary>The radius the band of layers starts at.</summary>
        public double LeafStart { get; private set; }

        public double OuterRadius { get; private set; }

        /// <summary>The wheel can't fit in <see cref="WheelLayoutOptions.MaxRadius"/> even with the names cut short.</summary>
        public bool TooBig { get; private set; }

        public IReadOnlyList<WheelSector> Sectors { get; private set; } = Array.Empty<WheelSector>();
        public IReadOnlyList<WheelLeaf> Leaves { get; private set; } = Array.Empty<WheelLeaf>();

        public static WheelLayout Build(IReadOnlyList<string> layers, int maxTerms, WheelLayoutOptions? options = null)
        {
            WheelLayoutOptions o = options ?? new WheelLayoutOptions();
            WheelNode root = LayerWheelTree.Build(layers, maxTerms, out string centre);
            var all = new List<WheelNode>();
            Collect(root, all);
            int rings = all.Where(n => !n.IsLeaf).Select(n => n.Depth).DefaultIfEmpty(0).Max();

            // The radius the band of layers starts at: far enough out for the rings, then further until
            // every layer and every group label has room round the circle.
            double leafStart = o.CentreRadius + o.Gap + rings * (o.RingThickness + o.Gap);
            for (int i = 0; i < 40; i++)
            {
                double total = Need(root, leafStart, rings, o);
                if (total <= FullTurn)
                {
                    break;
                }

                leafStart *= total / FullTurn * 1.005;
            }

            Need(root, leafStart, rings, o);
            Assign(root, 0, FullTurn);

            var sectors = new List<WheelSector>();
            var leaves = new List<WheelLeaf>();
            double roomForBars = Math.Max(o.MinBarLength, o.MaxRadius - leafStart);
            double longest = 0;
            foreach (WheelNode node in all)
            {
                if (ReferenceEquals(node, root))
                {
                    continue;
                }

                if (!node.IsLeaf)
                {
                    double outer = RingOuter(node.Depth, leafStart, rings, o);
                    sectors.Add(new WheelSector(node, outer - o.RingThickness, outer));
                    continue;
                }

                WheelNode parent = node.Parent!;
                double innerHit = ReferenceEquals(parent, root)
                    ? leafStart - o.Gap - rings * (o.RingThickness + o.Gap)
                    : RingOuter(parent.Depth, leafStart, rings, o);
                double length = Math.Min(roomForBars, o.MeasureLeaf(node.Term) + 2 * o.LabelPadding);
                longest = Math.Max(longest, length);
                leaves.Add(new WheelLeaf(node, innerHit, leafStart, length));
            }

            foreach (WheelLeaf leaf in leaves)
            {
                leaf.BarLength = longest;
            }

            return new WheelLayout
            {
                CentreLabel = centre,
                Rings = rings,
                CentreRadius = leafStart - o.Gap - rings * (o.RingThickness + o.Gap),
                LeafStart = leafStart,
                OuterRadius = leafStart + longest,
                TooBig = leafStart + longest > o.MaxRadius + 0.5,
                Sectors = sectors,
                Leaves = leaves,
            };
        }

        /// <summary>What is under a point <paramref name="dx"/> right of and <paramref name="dy"/> below the middle.</summary>
        public WheelHit Hit(double dx, double dy)
        {
            double r = Math.Sqrt(dx * dx + dy * dy);
            if (r <= CentreRadius)
            {
                return new WheelHit(WheelHitKind.Centre);
            }

            if (r > OuterRadius + PickMargin)
            {
                return new WheelHit(WheelHitKind.Outside);
            }

            double angle = AngleOf(dx, dy);
            foreach (WheelLeaf leaf in Leaves)
            {
                if (r >= leaf.InnerHit && leaf.Node.Contains(angle))
                {
                    return new WheelHit(WheelHitKind.Layer, leaf.Node);
                }
            }

            foreach (WheelSector sector in Sectors)
            {
                if (r >= sector.Inner && r <= sector.Outer && sector.Node.Contains(angle))
                {
                    return new WheelHit(WheelHitKind.Group, sector.Node);
                }
            }

            return new WheelHit(WheelHitKind.Nothing);
        }

        /// <summary>The angle of a point, clockwise from 12 o'clock, from 0 up to a full turn.</summary>
        public static double AngleOf(double dx, double dy)
        {
            double angle = Math.Atan2(dx, -dy);
            return angle < 0 ? angle + FullTurn : angle;
        }

        /// <summary>The point at an angle and radius, as (right of, below) the middle.</summary>
        public static (double X, double Y) PointAt(double angle, double radius) =>
            (radius * Math.Sin(angle), -radius * Math.Cos(angle));

        private static void Collect(WheelNode node, List<WheelNode> into)
        {
            into.Add(node);
            foreach (WheelNode child in node.Children)
            {
                Collect(child, into);
            }
        }

        private static double RingOuter(int depth, double leafStart, int rings, WheelLayoutOptions o) =>
            leafStart - o.Gap - (rings - depth) * (o.RingThickness + o.Gap);

        /// <summary>The angle each node needs with the layers starting at <paramref name="leafStart"/>.</summary>
        private static double Need(WheelNode node, double leafStart, int rings, WheelLayoutOptions o)
        {
            if (node.IsLeaf)
            {
                return node.Need = o.LeafPitch / leafStart;
            }

            double need = node.Children.Sum(c => Need(c, leafStart, rings, o));
            if (node.Depth > 0)
            {
                double mid = RingOuter(node.Depth, leafStart, rings, o) - o.RingThickness / 2;
                need = Math.Max(need, (o.MeasureGroup(node.Term) + 2 * o.LabelPadding) / mid);
            }

            return node.Need = need;
        }

        /// <summary>Share a slice among a node's children in proportion to what each needs.</summary>
        private static void Assign(WheelNode node, double start, double sweep)
        {
            node.Start = start;
            node.Sweep = sweep;
            double total = node.Children.Sum(c => c.Need);
            if (total <= 0)
            {
                return;
            }

            double at = start;
            foreach (WheelNode child in node.Children)
            {
                double share = sweep * child.Need / total;
                Assign(child, at, share);
                at += share;
            }
        }
    }

    /// <summary>The standard 255 CAD colours (AutoCAD Colour Index) as red, green, blue.</summary>
    public static class AciColors
    {
        private static readonly int[][] First =
        {
            new[] { 0, 0, 0 }, new[] { 255, 0, 0 }, new[] { 255, 255, 0 }, new[] { 0, 255, 0 }, new[] { 0, 255, 255 },
            new[] { 0, 0, 255 }, new[] { 255, 0, 255 }, new[] { 255, 255, 255 }, new[] { 65, 65, 65 }, new[] { 128, 128, 128 },
        };

        private static readonly int[] Greys = { 51, 80, 105, 130, 190, 255 };
        private static readonly int[] Shades = { 255, 165, 127, 76, 38 };

        /// <summary>
        /// Colour 7 is white here (the wheel has a dark background). Numbers outside 1 to 255
        /// (ByBlock, ByLayer) also give white.
        /// </summary>
        public static (int R, int G, int B) Rgb(int index)
        {
            if (index < 1 || index > 255)
            {
                return (255, 255, 255);
            }

            if (index < 10)
            {
                return (First[index][0], First[index][1], First[index][2]);
            }

            if (index >= 250)
            {
                int grey = Greys[index - 250];
                return (grey, grey, grey);
            }

            // 10 to 249: 24 hues, 15 degrees apart, each in five shades; odd numbers are the paler version.
            int hue = index / 10 - 1;
            int value = Shades[index % 10 / 2];
            bool pale = index % 2 == 1;
            double[] full = HueRgb(hue * 15);
            int Channel(double c)
            {
                double v = c * value;
                return (int)(pale ? (v + value) / 2 : v);
            }

            return (Channel(full[0]), Channel(full[1]), Channel(full[2]));
        }

        /// <summary>A fully saturated hue (degrees) as fractions of red, green and blue.</summary>
        private static double[] HueRgb(int degrees)
        {
            double h = degrees / 60.0;
            int sector = (int)h % 6;
            double f = h - (int)h;
            switch (sector)
            {
                case 0: return new[] { 1, f, 0 };
                case 1: return new[] { 1 - f, 1, 0 };
                case 2: return new[] { 0, 1, f };
                case 3: return new[] { 0, 1 - f, 1 };
                case 4: return new[] { f, 0, 1 };
                default: return new[] { 1, 0, 1 - f };
            }
        }
    }
}
