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

        /// <summary>Thickness of a ring of groups, when everything is shown at once.</summary>
        public double RingThickness { get; set; } = 40;

        public double Gap { get; set; } = 3;

        /// <summary>Room each wedge needs round the circle at its narrow end: a line of text and a little more.</summary>
        public double LeafPitch { get; set; } = 23;

        /// <summary>Space between the edge of a wedge or ring and its text.</summary>
        public double LabelPadding { get; set; } = 7;

        public double MinBarLength { get; set; } = 60;

        /// <summary>The wheel may not be bigger than this (half the screen, say); longer names are cut short.</summary>
        public double MaxRadius { get; set; } = 480;

        /// <summary>
        /// Every layer is shown at once while the band of layers starts no further out than
        /// this (about 50 layers). Beyond that the wheel goes a step at a time.
        /// </summary>
        public double AllAtOnceUpTo { get; set; } = 200;

        /// <summary>Going a step at a time, a group with more layers than this is split by the first word of their names. 0 never splits.</summary>
        public int SplitOver { get; set; } = 40;

        /// <summary>
        /// Going a step at a time, a group whose contents are all layers shows a chip of each
        /// layer's colour on its rim, this long. Under the pointer the chips grow into the layers.
        /// </summary>
        public double ChipLength { get; set; } = 10;

        /// <summary>Width of a layer's label.</summary>
        public Func<string, double> MeasureLeaf { get; set; } = s => s.Length * 6.6;

        /// <summary>Width of a group's label.</summary>
        public Func<string, double> MeasureGroup { get; set; } = s => s.Length * 8.0;
    }

    public enum WheelPartKind
    {
        /// <summary>A word in front of the dash (PLN).</summary>
        Group,

        /// <summary>Layers of one group that share their first word (SEWER), when the group is too big to show whole.</summary>
        Branch,

        Layer,
    }

    /// <summary>One piece of the wheel: part of a ring, or a wedge. Radii in pixels; angles in radians clockwise from 12 o'clock.</summary>
    public sealed class WheelPart
    {
        internal WheelPart(WheelNode node, string label)
        {
            Node = node;
            Label = label;
        }

        public WheelNode Node { get; }
        public WheelPartKind Kind => Node.IsLeaf ? WheelPartKind.Layer : Node.IsBranch ? WheelPartKind.Branch : WheelPartKind.Group;

        /// <summary>What to write on it.</summary>
        public string Label { get; }

        public string? Layer => Node.Layer;
        public int LayerIndex => Node.LayerIndex;

        public double Inner { get; internal set; }
        public double Outer { get; internal set; }
        public double Start { get; internal set; }
        public double Sweep { get; internal set; }
        public double Mid => Start + Sweep / 2;

        /// <summary>The pointer is on this part between these radii: a little more than is drawn, so it needn't be exact.</summary>
        public double HitInner { get; internal set; }

        public double HitOuter { get; internal set; }

        /// <summary>True for a group's slice of a ring, with its word written round the ring; false for a wedge, written along it.</summary>
        public bool RoundTheRing { get; internal set; }

        /// <summary>For a wedge: the radius its text starts at and the room it has.</summary>
        public double TextStart { get; internal set; }

        public double TextLength { get; internal set; }

        /// <summary>On the left half the text reads towards the middle, so it is never upside down.</summary>
        public bool ReadsInwards => Turn(Mid) > Math.PI;

        public bool Contains(double radius, double angle) =>
            radius >= HitInner && radius <= HitOuter && Spans(angle);

        /// <summary>True when the angle is within the part's slice, which may run past 12 o'clock.</summary>
        public bool Spans(double angle) => Turn(angle - Start) < Sweep;

        /// <summary>An angle brought into 0 up to a full turn.</summary>
        private static double Turn(double angle)
        {
            double turn = angle % (Math.PI * 2);
            return turn < 0 ? turn + Math.PI * 2 : turn;
        }
    }

    /// <summary>
    /// The layers of one group, set round its rim. Closed, they are chips of colour as wide as
    /// the group's own wedge; open, they are wedges with names, spread as wide as the names need.
    /// </summary>
    public sealed class WheelFan
    {
        internal WheelFan(WheelPart of, IReadOnlyList<WheelPart> parts, double outerRadius, bool open, bool tooBig)
        {
            Of = of;
            Parts = parts;
            OuterRadius = outerRadius;
            Open = open;
            TooBig = tooBig;
        }

        /// <summary>The group whose layers these are.</summary>
        public WheelPart Of { get; }

        public IReadOnlyList<WheelPart> Parts { get; }
        public double OuterRadius { get; }

        /// <summary>Fully open: the names are shown and the layers can be picked.</summary>
        public bool Open { get; }

        internal bool TooBig { get; }
    }

    public enum WheelHitKind
    {
        /// <summary>Beyond the wheel: a click here closes it.</summary>
        Outside,

        /// <summary>The disc in the middle: a click goes back a step.</summary>
        Centre,

        /// <summary>On a group, branch or layer.</summary>
        Part,

        /// <summary>On the wheel, but on nothing.</summary>
        Nothing,
    }

    public readonly struct WheelHit
    {
        public WheelHit(WheelHitKind kind, WheelPart? part = null)
        {
            Kind = kind;
            Part = part;
        }

        public WheelHitKind Kind { get; }
        public WheelPart? Part { get; }
    }

    /// <summary>What the wheel shows at one moment: everything, or one step of the way to a layer.</summary>
    public sealed class WheelView
    {
        private const double PickMargin = 14;

        internal WheelView(WheelNode node, string centreLabel, double centreRadius, double outerRadius, IReadOnlyList<WheelPart> parts, bool tooBig)
        {
            Node = node;
            CentreLabel = centreLabel;
            CentreRadius = centreRadius;
            OuterRadius = outerRadius;
            Parts = parts;
            TooBig = tooBig;
        }

        /// <summary>The group or branch whose contents are shown; the top of the tree for the first view.</summary>
        public WheelNode Node { get; }

        /// <summary>The words that lead here, shown in the middle ("AE CIV PLN").</summary>
        public string CentreLabel { get; }

        public double CentreRadius { get; }
        public double OuterRadius { get; }
        public IReadOnlyList<WheelPart> Parts { get; }

        /// <summary>It doesn't fit in <see cref="WheelLayoutOptions.MaxRadius"/> even with the names cut short.</summary>
        public bool TooBig { get; }

        /// <summary>
        /// What is under a point <paramref name="dx"/> right of and <paramref name="dy"/> below the middle.
        /// With a group's layers fanned out (<paramref name="fan"/>), everything beyond the rim
        /// belongs to the fan, so moving sideways along it doesn't jump to a neighbouring group.
        /// </summary>
        public WheelHit Hit(double dx, double dy, WheelFan? fan = null)
        {
            double r = Math.Sqrt(dx * dx + dy * dy);
            if (r <= CentreRadius)
            {
                return new WheelHit(WheelHitKind.Centre);
            }

            if (fan != null && r > OuterRadius)
            {
                double fanAngle = WheelLayout.AngleOf(dx, dy);
                foreach (WheelPart part in fan.Parts)
                {
                    if (part.Contains(r, fanAngle))
                    {
                        return new WheelHit(WheelHitKind.Part, part);
                    }
                }

                return new WheelHit(r > fan.OuterRadius + PickMargin ? WheelHitKind.Outside : WheelHitKind.Nothing);
            }

            if (r > OuterRadius + PickMargin)
            {
                return new WheelHit(WheelHitKind.Outside);
            }

            double angle = WheelLayout.AngleOf(dx, dy);
            foreach (WheelPart part in Parts)
            {
                if (part.Contains(r, angle))
                {
                    return new WheelHit(WheelHitKind.Part, part);
                }
            }

            return new WheelHit(WheelHitKind.Nothing);
        }

        internal const double Margin = PickMargin;
    }

    /// <summary>
    /// Where everything on the layer wheel goes.
    ///
    /// A short list is shown all at once: rings of groups round the middle, one ring per word in
    /// front of the dash, and each layer a wedge of the band outside its group. A slice is as
    /// wide as its layers need, and the wheel grows until they all fit round it.
    ///
    /// A long list goes a step at a time (<see cref="Steps"/>): the first view is a wheel of the
    /// groups, a click on one opens a wheel of what is in it, and so on down to the layers. A
    /// group with too many layers for one wheel is first split by the first word of their names.
    /// A group whose contents are all layers also carries them on its rim (<see cref="Fan"/>):
    /// chips of colour that open into the named layers under the pointer, to pick without a click.
    ///
    /// Angles are radians clockwise from 12 o'clock; positions are pixels from the middle of
    /// the wheel with y downwards, as on screen. Free of BricsCAD and Windows so it can be
    /// unit-tested.
    /// </summary>
    public sealed class WheelLayout
    {
        private const double FullTurn = Math.PI * 2;

        private readonly WheelLayoutOptions _o;
        private readonly WheelNode _root;
        private readonly string _folded;
        private readonly Dictionary<WheelNode, WheelView> _views = new Dictionary<WheelNode, WheelView>();
        private readonly Dictionary<string, WheelNode> _leaves = new Dictionary<string, WheelNode>(StringComparer.OrdinalIgnoreCase);

        private WheelLayout(WheelNode root, string folded, WheelLayoutOptions options)
        {
            _root = root;
            _folded = folded;
            _o = options;
            Top = null!;
        }

        /// <summary>True when the wheel goes a step at a time: a click on a group opens it.</summary>
        public bool Steps { get; private set; }

        /// <summary>The first view: everything, or the top groups.</summary>
        public WheelView Top { get; private set; }

        /// <summary>The radius of the biggest view, for sizing the window.</summary>
        public double LargestRadius { get; private set; }

        /// <summary>Some view doesn't fit the screen even with the names cut short.</summary>
        public bool TooBig { get; private set; }

        public static WheelLayout Build(IReadOnlyList<string> layers, int maxTerms, WheelLayoutOptions? options = null)
        {
            WheelLayoutOptions o = options ?? new WheelLayoutOptions();
            WheelNode root = LayerWheelTree.Build(layers, maxTerms, out string folded);
            var layout = new WheelLayout(root, folded, o);

            WheelView? all = layout.AllAtOnce();
            if (all != null)
            {
                layout.Top = all;
                layout._views[root] = all;
            }
            else
            {
                layout.Steps = true;
                LayerWheelTree.Split(root, o.SplitOver);
                layout.Top = layout.Open(root);
                layout.OpenAll(root);
            }

            layout.Index(root);
            layout.LargestRadius = layout._views.Values.Max(v => v.OuterRadius);
            layout.TooBig = layout._views.Values.Any(v => v.TooBig);
            foreach (WheelView view in layout._views.Values.ToList())
            {
                foreach (WheelPart part in view.Parts)
                {
                    if (layout.Fan(view, part) is WheelFan fan)
                    {
                        layout.LargestRadius = Math.Max(layout.LargestRadius, fan.OuterRadius);
                        layout.TooBig |= fan.TooBig;
                    }
                }
            }

            return layout;
        }

        /// <summary>The view of what is in a group or branch. Only when the wheel goes a step at a time.</summary>
        public WheelView Open(WheelNode node)
        {
            if (!Steps || node.IsLeaf)
            {
                return Top;
            }

            if (!_views.TryGetValue(node, out WheelView? view))
            {
                _views[node] = view = Step(node);
            }

            return view;
        }

        /// <summary>True for a group or branch whose contents are all layers: its layers can fan out from its rim.</summary>
        public bool CanFan(WheelPart part) =>
            Steps && !part.Node.IsLeaf && part.Node.Children.Count > 0 && part.Node.Children.All(c => c.IsLeaf);

        /// <summary>
        /// The layers of a group in <paramref name="view"/>, set round its rim. At
        /// <paramref name="progress"/> 0 they are chips of colour across the group's own wedge;
        /// at 1 they are wedges with names, centred on the group and as wide as the names need.
        /// In between is for growing from one to the other. Null if the group can't fan.
        /// </summary>
        public WheelFan? Fan(WheelView view, WheelPart of, double progress = 1)
        {
            if (!CanFan(of))
            {
                return null;
            }

            double t = Math.Max(0, Math.Min(1, progress));
            WheelNode node = of.Node;
            int n = node.Children.Count;
            double inner = view.OuterRadius + _o.Gap;

            // Open, each layer needs a line of text's room where its name starts. If that is more
            // than fits round the rim, the names start further out.
            const double widest = FullTurn * 0.92;
            double textStart = Math.Max(inner, n * _o.LeafPitch / widest);
            double openSweep = Math.Min(widest, Math.Max(of.Sweep, n * _o.LeafPitch / textStart));

            var labels = node.Children.Select(c => node.IsBranch ? Remainder(c.Term, node.Term) : c.Term).ToList();
            double longest = labels.Max(l => _o.MeasureLeaf(l)) + 2 * _o.LabelPadding;
            double length = Math.Min(longest, Math.Max(_o.MinBarLength, _o.MaxRadius - textStart));
            double openOuter = textStart + length;
            double closedOuter = inner + _o.ChipLength;

            double sweep = of.Sweep + (openSweep - of.Sweep) * t;
            double outer = closedOuter + (openOuter - closedOuter) * t;
            double start = of.Mid - sweep / 2;
            var parts = new List<WheelPart>();
            for (int i = 0; i < n; i++)
            {
                parts.Add(new WheelPart(node.Children[i], labels[i])
                {
                    Start = start + sweep * i / n,
                    Sweep = sweep / n,
                    Inner = inner,
                    Outer = outer,
                    HitInner = view.OuterRadius,
                    HitOuter = outer + WheelView.Margin,
                    TextStart = textStart,
                    TextLength = length,
                });
            }

            return new WheelFan(of, parts, outer, t >= 1, openOuter > _o.MaxRadius + 0.5);
        }

        /// <summary>The view one step back, or null from the first view.</summary>
        public WheelView? Back(WheelView view) =>
            Steps && view.Node.Parent != null ? Open(view.Node.Parent) : null;

        /// <summary>The layer's place in the tree, or null if it isn't on the wheel.</summary>
        public WheelNode? Find(string? layer) =>
            layer != null && _leaves.TryGetValue(layer, out WheelNode? node) ? node : null;

        /// <summary>The words that lead to a group or branch: "AE CIV PLN SEWER".</summary>
        public string PathOf(WheelNode node)
        {
            var words = new List<string>();
            for (WheelNode? n = node; n != null && n.Parent != null; n = n.Parent)
            {
                if (!n.IsLeaf)
                {
                    words.Insert(0, n.Term);
                }
            }

            return (_folded + " " + string.Join(" ", words)).Trim();
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

        private void Index(WheelNode node)
        {
            if (node.IsLeaf)
            {
                _leaves[node.Layer!] = node;
            }

            foreach (WheelNode child in node.Children)
            {
                Index(child);
            }
        }

        private void OpenAll(WheelNode node)
        {
            foreach (WheelNode child in node.Children.Where(c => !c.IsLeaf))
            {
                Open(child);
                OpenAll(child);
            }
        }

        // ------------------------------------------------------------------ a step at a time

        /// <summary>What is in a group or branch, as equal wedges round the whole circle.</summary>
        private WheelView Step(WheelNode node)
        {
            int n = node.Children.Count;
            double start = Math.Max(_o.CentreRadius + _o.Gap, n * _o.LeafPitch / FullTurn);
            var parts = new List<WheelPart>();
            double longest = 0;
            foreach (WheelNode child in node.Children)
            {
                string label = child.IsLeaf
                    ? (node.IsBranch ? Remainder(child.Term, node.Term) : child.Term)
                    : $"{child.Term} ({CountLayers(child)})";
                longest = Math.Max(longest, (child.IsLeaf ? _o.MeasureLeaf(label) : _o.MeasureGroup(label)) + 2 * _o.LabelPadding);
                parts.Add(new WheelPart(child, label));
            }

            double length = Math.Min(longest, Math.Max(_o.MinBarLength, _o.MaxRadius - start));
            double outer = start + length;
            for (int i = 0; i < n; i++)
            {
                WheelPart part = parts[i];
                part.Start = FullTurn * i / n;
                part.Sweep = FullTurn / n;
                part.Inner = start;
                part.Outer = outer;
                part.HitInner = start - _o.Gap;
                part.HitOuter = outer + WheelView.Margin;
                part.TextStart = start;
                part.TextLength = length;
            }

            return new WheelView(node, PathOf(node), start - _o.Gap, outer, parts, outer > _o.MaxRadius + 0.5);
        }

        private static int CountLayers(WheelNode node) =>
            node.IsLeaf ? 1 : node.Children.Sum(CountLayers);

        /// <summary>A layer's name without the words its branch is named by: "PIPE NEW" under SEWER.</summary>
        private static string Remainder(string label, string shared)
        {
            if (shared.Length > 0 && label.Length > shared.Length && label.StartsWith(shared, StringComparison.OrdinalIgnoreCase))
            {
                string rest = label.Substring(shared.Length).Trim();
                if (rest.Length > 0)
                {
                    return rest;
                }
            }

            return label;
        }

        // ------------------------------------------------------------------ everything at once

        /// <summary>Every layer in one view, or null when there are too many for that.</summary>
        private WheelView? AllAtOnce()
        {
            WheelLayoutOptions o = _o;
            var all = new List<WheelNode>();
            Collect(_root, all);
            int rings = all.Where(n => !n.IsLeaf).Select(n => n.Depth).DefaultIfEmpty(0).Max();

            // The radius the band of layers starts at: far enough out for the rings, then further
            // until every layer and every group label has room round the circle.
            double leafStart = o.CentreRadius + o.Gap + rings * (o.RingThickness + o.Gap);
            for (int i = 0; i < 40; i++)
            {
                double total = Need(_root, leafStart, rings, o);
                if (total <= FullTurn)
                {
                    break;
                }

                leafStart *= total / FullTurn * 1.005;
            }

            if (leafStart > o.AllAtOnceUpTo + 0.5)
            {
                return null;
            }

            Need(_root, leafStart, rings, o);
            Assign(_root, 0, FullTurn);

            double room = Math.Max(o.MinBarLength, o.MaxRadius - leafStart);
            double length = Math.Min(room, all.Where(n => n.IsLeaf).Select(n => o.MeasureLeaf(n.Term) + 2 * o.LabelPadding).DefaultIfEmpty(0).Max());
            double outer = leafStart + length;
            double centre = leafStart - o.Gap - rings * (o.RingThickness + o.Gap);

            var parts = new List<WheelPart>();
            foreach (WheelNode node in all)
            {
                if (ReferenceEquals(node, _root))
                {
                    continue;
                }

                var part = new WheelPart(node, node.Term) { Start = node.Start, Sweep = node.Sweep };
                if (node.IsLeaf)
                {
                    // The wedge runs from the layer's group (or the middle) out to the rim, so a layer
                    // with fewer words in front of its dash fills the rings it has no group in.
                    WheelNode parent = node.Parent!;
                    double from = ReferenceEquals(parent, _root) ? centre : RingOuter(parent.Depth, leafStart, rings, o);
                    part.Inner = from + o.Gap;
                    part.Outer = outer;
                    part.HitInner = from;
                    part.HitOuter = outer + WheelView.Margin;
                    part.TextStart = leafStart;
                    part.TextLength = length;
                }
                else
                {
                    part.Outer = RingOuter(node.Depth, leafStart, rings, o);
                    part.Inner = part.Outer - o.RingThickness;
                    part.HitInner = part.Inner;
                    part.HitOuter = part.Outer;
                    part.RoundTheRing = true;
                }

                parts.Add(part);
            }

            return new WheelView(_root, _folded, centre, outer, parts, outer > o.MaxRadius + 0.5);
        }

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
