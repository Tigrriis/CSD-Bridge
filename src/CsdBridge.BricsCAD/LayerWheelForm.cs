using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CsdBridge.Core.Layers;

namespace CsdBridge.BricsCAD
{
    /// <summary>A layer on the wheel, as the open drawing has it.</summary>
    internal sealed class WheelLayerState
    {
        public string Name { get; set; } = "";

        /// <summary>False when the drawing doesn't have the layer: it is shown greyed out and can't be picked.</summary>
        public bool Exists { get; set; }

        public Color Color { get; set; } = Color.White;
        public bool Frozen { get; set; }
        public bool Off { get; set; }
        public bool Locked { get; set; }
        public bool Current { get; set; }
    }

    /// <summary>
    /// AILAYERWHEEL's wheel: a see-through window centred on the pointer. A short list of layers
    /// is shown all at once: rings of layer groups round the middle and each layer as a wedge of
    /// the outer band in its own colour. A long list goes a step at a time: the wheel opens on
    /// the groups, a click on a group opens a wheel of what is in it, and a click on the middle
    /// (or a right-click, or Backspace) goes back a step.
    ///
    /// Going a step at a time, a group whose contents are all layers has a chip of each layer's
    /// colour on its rim. With the pointer on the group the chips grow into the named layers,
    /// fanned out beyond the rim, and they can be picked there without opening the group.
    ///
    /// A click on a layer picks it; Esc or a click outside closes the wheel. Letting go of a key
    /// while on a layer also picks it (hold the shortcut, flick, release).
    ///
    /// The window is drawn as one picture with its own transparency (a layered window), so the
    /// drawing shows through around and between the parts. Where everything goes comes from
    /// <see cref="WheelLayout"/>.
    /// </summary>
    internal sealed class LayerWheelForm : Form
    {
        private static readonly Color Backdrop = Color.FromArgb(170, 22, 25, 30);
        private static readonly Color RingFill = Color.FromArgb(245, 52, 58, 68);
        private static readonly Color RingHover = Color.FromArgb(250, 84, 96, 116);
        private static readonly Color CentreFill = Color.FromArgb(245, 34, 38, 46);
        private static readonly Color MissingFill = Color.FromArgb(235, 58, 62, 70);
        private static readonly Color MissingText = Color.FromArgb(150, 155, 165);

        private readonly IReadOnlyList<WheelLayerState> _layers;
        private readonly bool _releaseToPick;
        private readonly int _selected;
        private readonly string? _currentLayer;

        private readonly WheelLayout _layout;
        private readonly float _scale;
        private readonly float _bar;
        private readonly float _pad;
        private readonly float _gap;
        private readonly Font _leafFont;
        private readonly Font _groupFont;
        private readonly Font _captionFont;
        private readonly Point _centre;      // middle of the wheel, in the window
        private readonly int _captionHeight;

        private readonly WheelNode? _currentNode;
        private WheelView _view;
        private WheelPart? _hover;
        private WheelPart? _fanOf;          // the group whose layers are fanned out
        private WheelFan? _fan;             // that fan, fully open: what the pointer is tested against
        private double _fanProgress;        // 0 to 1 while the chips grow into the fan
        private readonly Timer _grow = new Timer { Interval = 15 };
        private bool _overCentre;
        private bool _live;

        /// <param name="selected">How many objects are selected; 0 means the wheel sets the current layer.</param>
        public LayerWheelForm(LayerWheelSettings settings, IReadOnlyList<WheelLayerState> layers, int selected)
        {
            _layers = layers;
            _releaseToPick = settings.ReleaseToPick;
            _selected = selected;
            _currentLayer = layers.FirstOrDefault(l => l.Current)?.Name;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            KeyPreview = true;
            Text = "CSD-Bridge layer wheel";

            Point pointer = Cursor.Position;
            Rectangle screen = Screen.FromPoint(pointer).WorkingArea;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                _scale = Math.Max(1f, g.DpiX / 96f);
            }

            _captionHeight = (int)(30 * _scale);
            int margin = (int)(8 * _scale);
            double maxRadius = Math.Min(screen.Width, screen.Height - _captionHeight) / 2.0 - margin;
            string[] names = layers.Select(l => l.Name).ToArray();

            // Use the text size asked for; step down while the wheel is too big for the screen.
            double size = Math.Min(Math.Max(settings.TextSize, 7), 16);
            using (var scratch = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(scratch))
            {
                while (true)
                {
                    _leafFont = MakeFont(size, FontStyle.Regular);
                    _groupFont = MakeFont(size + 1, FontStyle.Bold);
                    float line = _leafFont.GetHeight(g);
                    _bar = (float)Math.Ceiling(line + 4 * _scale);
                    _pad = 7 * _scale;
                    _gap = 3 * _scale;
                    Font leafFont = _leafFont, groupFont = _groupFont;
                    _layout = WheelLayout.Build(names, settings.MaxTerms, new WheelLayoutOptions
                    {
                        CentreRadius = 34 * _scale,
                        RingThickness = Math.Max(38 * _scale, _groupFont.GetHeight(g) + 16 * _scale),
                        Gap = _gap,
                        LeafPitch = _bar + 3 * _scale,
                        LabelPadding = _pad,
                        MinBarLength = 60 * _scale,
                        MaxRadius = maxRadius,
                        AllAtOnceUpTo = 200 * _scale,
                        ChipLength = 10 * _scale,
                        SplitOver = settings.SplitGroupsOver,
                        MeasureLeaf = s => Measure(g, s, leafFont),
                        MeasureGroup = s => Measure(g, s, groupFont),
                    });

                    if (!_layout.TooBig || size <= 7)
                    {
                        break;
                    }

                    _leafFont.Dispose();
                    _groupFont.Dispose();
                    size -= 1;
                }
            }

            _captionFont = MakeFont(Math.Max(size, 9), FontStyle.Regular);

            _view = _layout.Top;
            _grow.Tick += (sender, e) =>
            {
                _fanProgress = Math.Min(1, _fanProgress + 0.25);
                if (_fanProgress >= 1)
                {
                    _grow.Stop();
                }

                Redraw();
            };
            _currentNode = _layout.Find(_currentLayer);

            int radius = (int)Math.Ceiling(_layout.LargestRadius) + margin;
            int width = Math.Max(radius * 2, (int)(420 * _scale));
            _centre = new Point(width / 2, radius);
            Size = new Size(width, radius * 2 + _captionHeight);

            // Centre the wheel on the pointer; if that runs off the screen, move both.
            int left = pointer.X - _centre.X;
            int top = pointer.Y - _centre.Y;
            int fitLeft = Math.Max(screen.Left, Math.Min(left, screen.Right - Width));
            int fitTop = Math.Max(screen.Top, Math.Min(top, screen.Bottom - Height));
            Location = new Point(fitLeft, fitTop);
            if (fitLeft != left || fitTop != top)
            {
                Cursor.Position = new Point(fitLeft + _centre.X, fitTop + _centre.Y);
            }
        }

        /// <summary>The layer picked, or null if the wheel was closed without picking.</summary>
        public string? PickedLayer { get; private set; }

        /// <summary>True when there are more layers than fit on this screen even at the smallest text.</summary>
        public bool TooBig => _layout.TooBig;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED;
                return cp;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Redraw();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            Capture = true; // so a click anywhere reaches the wheel, and one outside closes it
            _live = true;
            Track(PointToClient(Cursor.Position));
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (_live)
            {
                Finish(null);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Track(e.Location);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (HitAt(e.Location).Kind == WheelHitKind.Outside)
            {
                Finish(null);
            }
            else if (e.Button == MouseButtons.Right && !GoBack())
            {
                Finish(null);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_live)
            {
                return;
            }

            Track(e.Location);
            if (e.Button == MouseButtons.Left)
            {
                if (TryPick())
                {
                    return;
                }

                if (_hover != null && _hover.Kind != WheelPartKind.Layer && _layout.Steps)
                {
                    ShowView(_layout.Open(_hover.Node));
                }
                else if (_overCentre)
                {
                    GoBack();
                }
            }

            Capture = true; // Windows lets go of the mouse after each click
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                Finish(null);
            }
            else if (e.KeyCode == Keys.Back)
            {
                e.Handled = true;
                GoBack();
            }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (_live && _releaseToPick && e.KeyCode != Keys.Escape && e.KeyCode != Keys.Back)
            {
                TryPick();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _grow.Dispose();
                _leafFont.Dispose();
                _groupFont.Dispose();
                _captionFont.Dispose();
            }

            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ picking

        private WheelHit HitAt(Point client) => _view.Hit(client.X - _centre.X, client.Y - _centre.Y, _fan);

        private void Track(Point client)
        {
            WheelHit hit = HitAt(client);
            bool overCentre = hit.Kind == WheelHitKind.Centre;
            bool changed = !ReferenceEquals(hit.Part, _hover) || overCentre != _overCentre;
            _hover = hit.Part;
            _overCentre = overCentre;

            // On a group that can fan, open its layers. They stay open while the pointer is out
            // among them, and close when it comes back in over something else.
            bool onTheWheel = overCentre || (_hover != null && (_fan == null || !_fan.Parts.Contains(_hover)));
            if (onTheWheel)
            {
                WheelPart? fanOf = _hover != null && _layout.CanFan(_hover) ? _hover : null;
                if (!ReferenceEquals(fanOf, _fanOf))
                {
                    SetFan(fanOf);
                    changed = true;
                }
            }

            if (changed)
            {
                Redraw();
            }
        }

        /// <summary>Fan out a group's layers from its rim, or (null) close the fan.</summary>
        private void SetFan(WheelPart? of)
        {
            _grow.Stop();
            _fanOf = of;
            _fan = of == null ? null : _layout.Fan(_view, of);
            _fanProgress = 0;
            if (_fan != null)
            {
                _fanProgress = 0.25;
                _grow.Start();
            }
        }

        /// <summary>Change what the wheel shows (a step in, or a step back).</summary>
        private void ShowView(WheelView view)
        {
            SetFan(null);
            _view = view;
            _hover = null;
            _overCentre = false;
            Redraw();
            Track(PointToClient(Cursor.Position));
        }

        /// <summary>Go back a step. False from the first view, where there is nowhere to go back to.</summary>
        private bool GoBack()
        {
            WheelView? back = _layout.Back(_view);
            if (back == null)
            {
                return false;
            }

            ShowView(back);
            return true;
        }

        /// <summary>Pick the layer under the pointer, if it is one the drawing has.</summary>
        private bool TryPick()
        {
            if (_hover == null || _hover.Kind != WheelPartKind.Layer || !_layers[_hover.LayerIndex].Exists)
            {
                return false;
            }

            Finish(_layers[_hover.LayerIndex].Name);
            return true;
        }

        private void Finish(string? layer)
        {
            if (!_live)
            {
                return;
            }

            _live = false;
            PickedLayer = layer;
            Capture = false;
            DialogResult = layer == null ? DialogResult.Cancel : DialogResult.OK;
            Close();
        }

        // ------------------------------------------------------------------ drawing

        private Font MakeFont(double points, FontStyle style) =>
            new Font("Segoe UI", (float)(points * _scale * 96.0 / 72.0), style, GraphicsUnit.Pixel);

        private static double Measure(Graphics g, string text, Font font) =>
            Math.Ceiling(g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width) + 2;

        private void Redraw()
        {
            if (!IsHandleCreated || IsDisposed)
            {
                return;
            }

            using (var picture = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(picture))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias; // ClearType can't blend onto a see-through picture
                    g.Clear(Color.Transparent);
                    PaintWheel(g);
                }

                Present(picture);
            }
        }

        private void PaintWheel(Graphics g)
        {
            float cx = _centre.X, cy = _centre.Y;
            bool chips = _view.Parts.Any(_layout.CanFan);
            float outer = (float)_view.OuterRadius + (chips ? 18 : 5) * _scale;
            using (var backdrop = new SolidBrush(Backdrop))
            {
                g.FillEllipse(backdrop, cx - outer, cy - outer, outer * 2, outer * 2);
            }

            foreach (WheelPart part in _view.Parts)
            {
                bool hover = ReferenceEquals(part, _hover);
                if (part.RoundTheRing)
                {
                    // Lit when the pointer is on the group or on one of its layers.
                    bool lit = hover || (_hover != null && _hover.Node.IsWithin(part.Node));
                    using (GraphicsPath path = Sector(part.Inner, part.Outer, part.Start, part.Sweep, _gap))
                    using (var fill = new SolidBrush(lit ? RingHover : RingFill))
                    {
                        g.FillPath(fill, path);
                    }

                    DrawRingLabel(g, part);
                }
                else
                {
                    DrawWedge(g, part, hover || ReferenceEquals(part, _fanOf));
                }
            }

            // A chip of each layer's colour on the rim of every group that holds only layers.
            foreach (WheelPart part in _view.Parts)
            {
                if (!ReferenceEquals(part, _fanOf) && _layout.Fan(_view, part, 0) is WheelFan closed)
                {
                    foreach (WheelPart chip in closed.Parts)
                    {
                        using (GraphicsPath path = Sector(chip.Inner, chip.Outer, chip.Start, chip.Sweep, _scale))
                        using (var brush = new SolidBrush(FillOf(_layers[chip.LayerIndex])))
                        {
                            g.FillPath(brush, path);
                        }
                    }
                }
            }

            // The group under the pointer: its chips grown into its layers, with names once fully open.
            WheelFan? open = _fanOf == null ? null : _layout.Fan(_view, _fanOf, _fanProgress);
            WheelPart? hoverDrawn = _hover;
            if (open != null)
            {
                foreach (WheelPart part in open.Parts)
                {
                    bool hover = _hover != null && ReferenceEquals(part.Node, _hover.Node);
                    DrawWedge(g, part, hover, open.Open);
                    if (hover)
                    {
                        hoverDrawn = part;
                    }
                }

                foreach (WheelPart part in open.Parts)
                {
                    if (ReferenceEquals(part.Node, _currentNode) && !ReferenceEquals(part, hoverDrawn))
                    {
                        Outline(g, part, Color.White, 2f);
                    }
                }
            }

            // Outlines go on last so the neighbours' wedges don't cover them: the current layer
            // (or, a step back from it, the group it is in), then the one under the pointer.
            foreach (WheelPart part in _view.Parts)
            {
                if (!part.RoundTheRing && !ReferenceEquals(part, _hover) && _currentNode != null && _currentNode.IsWithin(part.Node))
                {
                    Outline(g, part, Color.White, 2f);
                }
            }

            if (hoverDrawn != null && !hoverDrawn.RoundTheRing)
            {
                bool usable = hoverDrawn.Kind != WheelPartKind.Layer || _layers[hoverDrawn.LayerIndex].Exists;
                Outline(g, hoverDrawn, usable ? Color.White : MissingText, 3f);
            }
            else if (_fanOf != null)
            {
                Outline(g, _fanOf, Color.White, 2f);
            }

            DrawCentre(g);
            DrawCaption(g);
        }

        /// <summary>Part of a ring. Angles are radians clockwise from 12 o'clock; <paramref name="gap"/> is left between neighbours.</summary>
        private GraphicsPath Sector(double inner, double outer, double start, double sweep, double gap)
        {
            var path = new GraphicsPath();
            float cx = _centre.X, cy = _centre.Y;
            if (sweep >= Math.PI * 2 - 1e-6)
            {
                path.AddEllipse((float)(cx - outer), (float)(cy - outer), (float)(outer * 2), (float)(outer * 2));
                path.AddEllipse((float)(cx - inner), (float)(cy - inner), (float)(inner * 2), (float)(inner * 2));
                return path; // the default fill mode leaves the hole
            }

            // GDI+ measures clockwise from 3 o'clock, in degrees.
            double trimOuter = Math.Min(gap / 2 / outer, sweep / 2.2);
            double trimInner = Math.Min(gap / 2 / Math.Max(inner, 1), sweep / 2.2);
            float Degrees(double a) => (float)(a * 180 / Math.PI - 90);
            float Sweep(double a) => (float)(a * 180 / Math.PI);

            path.AddArc((float)(cx - outer), (float)(cy - outer), (float)(outer * 2), (float)(outer * 2),
                Degrees(start + trimOuter), Sweep(sweep - 2 * trimOuter));
            path.AddArc((float)(cx - inner), (float)(cy - inner), (float)(inner * 2), (float)(inner * 2),
                Degrees(start + sweep - trimInner), -Sweep(sweep - 2 * trimInner));
            path.CloseFigure();
            return path;
        }

        /// <summary>The group's word, written round the ring and never upside down.</summary>
        private void DrawRingLabel(Graphics g, WheelPart part)
        {
            double mid = part.Mid;
            float radius = (float)((part.Inner + part.Outer) / 2);
            float degrees = (float)(mid * 180 / Math.PI);
            bool lowerHalf = mid > Math.PI / 2 && mid < Math.PI * 1.5;
            float room = (float)(part.Sweep * radius);

            GraphicsState saved = g.Save();
            g.TranslateTransform(_centre.X, _centre.Y);
            g.RotateTransform(lowerHalf ? degrees + 180 : degrees);
            float y = lowerHalf ? radius : -radius;
            float height = (float)(part.Outer - part.Inner);
            using (var format = Centred())
            using (var brush = new SolidBrush(Color.White))
            {
                g.DrawString(part.Label, _groupFont, brush, new RectangleF(-room / 2, y - height / 2, room, height), format);
            }

            g.Restore(saved);
        }

        /// <summary>A wedge, a hairline apart from its neighbours.</summary>
        private GraphicsPath Wedge(WheelPart part) =>
            Sector(part.Inner, part.Outer, part.Start, part.Sweep, 1.5 * _scale);

        /// <summary>A layer in its own colour, or a group or branch to open, with its text along the wedge.</summary>
        private void DrawWedge(Graphics g, WheelPart part, bool hover, bool withText = true)
        {
            bool isLayer = part.Kind == WheelPartKind.Layer;
            WheelLayerState? state = isLayer ? _layers[part.LayerIndex] : null;
            bool missing = state != null && !state.Exists;
            Color fill = state == null ? (hover ? RingHover : RingFill) : FillOf(state);
            if (hover && state != null && !missing)
            {
                fill = Blend(fill, Brightness(fill) > 200 ? Color.FromArgb(150, 170, 200) : Color.White, 0.22);
            }

            Color text = missing ? MissingText : Brightness(fill) > 140 ? Color.Black : Color.White;

            using (GraphicsPath wedge = Wedge(part))
            using (var brush = new SolidBrush(fill))
            {
                g.FillPath(brush, wedge);
                if (missing)
                {
                    using (var pen = new Pen(MissingText, _scale) { DashStyle = DashStyle.Dash })
                    {
                        g.DrawPath(pen, wedge);
                    }
                }
            }

            if (!withText)
            {
                return;
            }

            float start = (float)part.TextStart;
            float length = (float)part.TextLength;
            Font font = isLayer ? _leafFont : _groupFont;
            float height = _bar + 4 * _scale;

            GraphicsState saved = g.Save();
            g.TranslateTransform(_centre.X, _centre.Y);
            g.RotateTransform((float)(part.Mid * 180 / Math.PI - 90)); // x now runs outwards along the middle of the wedge

            using (var format = Centred())
            using (var brush = new SolidBrush(text))
            {
                if (part.ReadsInwards)
                {
                    // Turn the text round so it reads towards the middle.
                    g.TranslateTransform(start + length / 2, 0);
                    g.RotateTransform(180);
                    format.Alignment = StringAlignment.Far;
                    g.DrawString(part.Label, font, brush, new RectangleF(-length / 2 + _pad / 2, -height / 2, length - _pad * 1.5f, height), format);
                }
                else
                {
                    format.Alignment = StringAlignment.Near;
                    g.DrawString(part.Label, font, brush, new RectangleF(start + _pad, -height / 2, length - _pad * 1.5f, height), format);
                }
            }

            g.Restore(saved);
        }

        /// <summary>A layer's colour on the wheel: its own, dimmed if frozen or off, grey if the drawing lacks it.</summary>
        private static Color FillOf(WheelLayerState state) =>
            !state.Exists ? MissingFill
            : state.Frozen || state.Off ? Blend(state.Color, Color.FromArgb(40, 44, 52), 0.55)
            : state.Color;

        /// <summary>A line round a wedge: the current layer, and the one under the pointer.</summary>
        private void Outline(Graphics g, WheelPart part, Color color, float width)
        {
            using (GraphicsPath wedge = Wedge(part))
            using (var dark = new Pen(Color.FromArgb(200, 20, 22, 26), (width + 2) * _scale) { LineJoin = LineJoin.Round })
            using (var pen = new Pen(color, width * _scale) { LineJoin = LineJoin.Round })
            {
                g.DrawPath(dark, wedge);
                g.DrawPath(pen, wedge);
            }
        }

        /// <summary>The middle: the words that lead to what the wheel shows, one to a line, and the way back.</summary>
        private void DrawCentre(Graphics g)
        {
            bool canGoBack = _layout.Back(_view) != null;
            float r = (float)_view.CentreRadius - _gap;
            using (var fill = new SolidBrush(canGoBack && _overCentre ? RingHover : CentreFill))
            using (var edge = new Pen(Color.FromArgb(120, 255, 255, 255), _scale))
            {
                g.FillEllipse(fill, _centre.X - r, _centre.Y - r, r * 2, r * 2);
                g.DrawEllipse(edge, _centre.X - r, _centre.Y - r, r * 2, r * 2);
            }

            string label = _view.CentreLabel.Replace(' ', '\n');
            if (canGoBack)
            {
                label = (label.Length > 0 ? label + "\n" : "") + "\u2039 back";
            }

            if (label.Length == 0)
            {
                return;
            }

            using (var format = Centred())
            using (var brush = new SolidBrush(Color.White))
            {
                format.FormatFlags &= ~StringFormatFlags.NoWrap;
                g.DrawString(label, _groupFont, brush, new RectangleF(_centre.X - r, _centre.Y - r, r * 2, r * 2), format);
            }
        }

        /// <summary>One line under the wheel saying what a click will do.</summary>
        private void DrawCaption(Graphics g)
        {
            string text;
            if (_hover != null && _hover.Kind == WheelPartKind.Layer)
            {
                WheelLayerState state = _layers[_hover.LayerIndex];
                string note = !state.Exists ? "  (not in this drawing)"
                    : state.Current ? "  (current layer)"
                    : state.Frozen ? "  (frozen)"
                    : state.Off ? "  (off)"
                    : state.Locked ? "  (locked)"
                    : "";
                text = (_selected > 0 ? $"Move {Objects(_selected)} to:  " : "Make current:  ") + state.Name + note;
            }
            else if (_hover != null)
            {
                text = !_layout.Steps ? _layout.PathOf(_hover.Node)
                    : _layout.CanFan(_hover) ? _layout.PathOf(_hover.Node) + ":  move out to its layers, or click to open"
                    : "Open  " + _layout.PathOf(_hover.Node);
            }
            else if (_overCentre && _layout.Back(_view) != null)
            {
                text = "Back a step";
            }
            else
            {
                text = _selected > 0
                    ? $"Pick a layer for the {Objects(_selected)} selected"
                    : "Pick the current layer" + (_currentLayer != null ? $"   (now {_currentLayer})" : "");
            }

            float height = _captionHeight - 6 * _scale;
            float width = Math.Min(Width - 4 * _scale, (float)Measure(g, text, _captionFont) + 28 * _scale);
            var box = new RectangleF((Width - width) / 2, Height - height - 2 * _scale, width, height);
            using (GraphicsPath pill = Pill(box))
            using (var fill = new SolidBrush(Color.FromArgb(240, 22, 25, 30)))
            using (var brush = new SolidBrush(Color.White))
            using (var format = Centred())
            {
                g.FillPath(fill, pill);
                g.DrawString(text, _captionFont, brush, box, format);
            }
        }

        private static string Objects(int count) => count == 1 ? "1 object" : count + " objects";

        private static StringFormat Centred() => new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };

        private static GraphicsPath Pill(RectangleF box)
        {
            var path = new GraphicsPath();
            float d = box.Height;
            path.AddArc(box.Left, box.Top, d, d, 90, 180);
            path.AddArc(box.Right - d, box.Top, d, d, 270, 180);
            path.CloseFigure();
            return path;
        }

        private static int Brightness(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000;

        private static Color Blend(Color a, Color b, double towardsB) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * towardsB),
            (int)(a.G + (b.G - a.G) * towardsB),
            (int)(a.B + (b.B - a.B) * towardsB));

        // ------------------------------------------------------------------ the layered window

        private const int WS_EX_LAYERED = 0x00080000;
        private const int ULW_ALPHA = 2;
        private const byte AC_SRC_OVER = 0;
        private const byte AC_SRC_ALPHA = 1;

        /// <summary>Hand Windows the picture, transparency included, as the whole window.</summary>
        private void Present(Bitmap picture)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr memory = CreateCompatibleDC(screen);
            IntPtr bitmap = IntPtr.Zero;
            IntPtr previous = IntPtr.Zero;
            try
            {
                bitmap = picture.GetHbitmap(Color.FromArgb(0));
                previous = SelectObject(memory, bitmap);
                var size = new NativeSize { Width = picture.Width, Height = picture.Height };
                var source = new NativePoint { X = 0, Y = 0 };
                var at = new NativePoint { X = Left, Y = Top };
                var blend = new BlendFunction
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA,
                };
                UpdateLayeredWindow(Handle, screen, ref at, ref size, memory, ref source, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
                if (bitmap != IntPtr.Zero)
                {
                    SelectObject(memory, previous);
                    DeleteObject(bitmap);
                }

                DeleteDC(memory);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int Width;
            public int Height;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref NativePoint pptDst, ref NativeSize psize,
            IntPtr hdcSrc, ref NativePoint pptSrc, int crKey, ref BlendFunction pblend, int dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }
}
