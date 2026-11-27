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
    /// AILAYERWHEEL's wheel: a see-through window centred on the pointer, with rings of layer
    /// groups round the middle and each layer as a wedge of the outer band in its own colour.
    /// A click on a layer picks it; Esc, a right-click or a click outside closes the wheel.
    /// Letting go of a key while on a layer also picks it (hold the shortcut, flick, release).
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

        private WheelNode? _hover;
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

            int radius = (int)Math.Ceiling(_layout.OuterRadius) + margin;
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
            if (e.Button == MouseButtons.Right || HitAt(e.Location).Kind == WheelHitKind.Outside)
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
            if (e.Button == MouseButtons.Left && TryPick())
            {
                return;
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
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (_live && _releaseToPick && e.KeyCode != Keys.Escape)
            {
                TryPick();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _leafFont.Dispose();
                _groupFont.Dispose();
                _captionFont.Dispose();
            }

            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ picking

        private WheelHit HitAt(Point client) => _layout.Hit(client.X - _centre.X, client.Y - _centre.Y);

        private void Track(Point client)
        {
            WheelHit hit = HitAt(client);
            WheelNode? node = hit.Kind == WheelHitKind.Layer || hit.Kind == WheelHitKind.Group ? hit.Node : null;
            if (!ReferenceEquals(node, _hover))
            {
                _hover = node;
                Redraw();
            }
        }

        /// <summary>Pick the layer under the pointer, if it is one the drawing has.</summary>
        private bool TryPick()
        {
            if (_hover == null || !_hover.IsLeaf || !_layers[_hover.LayerIndex].Exists)
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
            float outer = (float)_layout.OuterRadius + 5 * _scale;
            using (var backdrop = new SolidBrush(Backdrop))
            {
                g.FillEllipse(backdrop, cx - outer, cy - outer, outer * 2, outer * 2);
            }

            WheelLeaf? hoverLeaf = _hover != null && _hover.IsLeaf ? _layout.Leaves.First(l => ReferenceEquals(l.Node, _hover)) : null;
            foreach (WheelSector sector in _layout.Sectors)
            {
                bool lit = _hover != null && _hover.IsWithin(sector.Node);
                using (GraphicsPath path = Sector(sector.Inner, sector.Outer, sector.Start, sector.Sweep, _gap))
                using (var fill = new SolidBrush(lit ? RingHover : RingFill))
                {
                    g.FillPath(fill, path);
                }

                DrawGroupLabel(g, sector);
            }

            foreach (WheelLeaf leaf in _layout.Leaves)
            {
                DrawLeaf(g, leaf, ReferenceEquals(leaf, hoverLeaf));
            }

            // Outlines go on last so the neighbours' wedges don't cover them.
            foreach (WheelLeaf leaf in _layout.Leaves)
            {
                if (_layers[leaf.LayerIndex].Current && !ReferenceEquals(leaf, hoverLeaf))
                {
                    Outline(g, leaf, Color.White, 2f);
                }
            }

            if (hoverLeaf != null)
            {
                Outline(g, hoverLeaf, _layers[hoverLeaf.LayerIndex].Exists ? Color.White : MissingText, 3f);
            }

            DrawCentre(g);
            DrawCaption(g, hoverLeaf);
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
        private void DrawGroupLabel(Graphics g, WheelSector sector)
        {
            double mid = sector.Node.Mid;
            float radius = (float)((sector.Inner + sector.Outer) / 2);
            float degrees = (float)(mid * 180 / Math.PI);
            bool lowerHalf = mid > Math.PI / 2 && mid < Math.PI * 1.5;
            float room = (float)(sector.Sweep * radius);

            GraphicsState saved = g.Save();
            g.TranslateTransform(_centre.X, _centre.Y);
            g.RotateTransform(lowerHalf ? degrees + 180 : degrees);
            float y = lowerHalf ? radius : -radius;
            float height = (float)(sector.Outer - sector.Inner);
            using (var format = Centred())
            using (var brush = new SolidBrush(Color.White))
            {
                g.DrawString(sector.Node.Term, _groupFont, brush, new RectangleF(-room / 2, y - height / 2, room, height), format);
            }

            g.Restore(saved);
        }

        /// <summary>A layer's wedge: from its group (or the middle) out to the rim, a hairline apart from its neighbours.</summary>
        private GraphicsPath Wedge(WheelLeaf leaf) =>
            Sector(leaf.InnerHit + _gap, _layout.OuterRadius, leaf.Node.Start, leaf.Node.Sweep, 1.5 * _scale);

        private void DrawLeaf(Graphics g, WheelLeaf leaf, bool hover)
        {
            WheelLayerState state = _layers[leaf.LayerIndex];
            Color fill = !state.Exists ? MissingFill
                : state.Frozen || state.Off ? Blend(state.Color, Color.FromArgb(40, 44, 52), 0.55)
                : state.Color;
            if (hover && state.Exists)
            {
                fill = Blend(fill, Brightness(fill) > 200 ? Color.FromArgb(150, 170, 200) : Color.White, 0.22);
            }

            Color text = !state.Exists ? MissingText : Brightness(fill) > 140 ? Color.Black : Color.White;

            using (GraphicsPath wedge = Wedge(leaf))
            using (var brush = new SolidBrush(fill))
            {
                g.FillPath(brush, wedge);
                if (!state.Exists)
                {
                    using (var pen = new Pen(MissingText, _scale) { DashStyle = DashStyle.Dash })
                    {
                        g.DrawPath(pen, wedge);
                    }
                }
            }

            float start = (float)leaf.BarStart;
            float length = (float)leaf.BarLength;

            GraphicsState saved = g.Save();
            g.TranslateTransform(_centre.X, _centre.Y);
            g.RotateTransform((float)(leaf.Angle * 180 / Math.PI - 90)); // x now runs outwards along the middle of the wedge

            using (var format = Centred())
            using (var brush = new SolidBrush(text))
            {
                if (leaf.ReadsInwards)
                {
                    // Turn the text round so it reads towards the middle.
                    g.TranslateTransform(start + length / 2, 0);
                    g.RotateTransform(180);
                    format.Alignment = StringAlignment.Far;
                    g.DrawString(leaf.Label, _leafFont, brush, new RectangleF(-length / 2 + _pad / 2, -_bar / 2, length - _pad * 1.5f, _bar), format);
                }
                else
                {
                    format.Alignment = StringAlignment.Near;
                    g.DrawString(leaf.Label, _leafFont, brush, new RectangleF(start + _pad, -_bar / 2, length - _pad * 1.5f, _bar), format);
                }
            }

            g.Restore(saved);
        }

        /// <summary>A line round a layer's wedge: the current layer, and the one under the pointer.</summary>
        private void Outline(Graphics g, WheelLeaf leaf, Color color, float width)
        {
            using (GraphicsPath wedge = Wedge(leaf))
            using (var dark = new Pen(Color.FromArgb(200, 20, 22, 26), (width + 2) * _scale) { LineJoin = LineJoin.Round })
            using (var pen = new Pen(color, width * _scale) { LineJoin = LineJoin.Round })
            {
                g.DrawPath(dark, wedge);
                g.DrawPath(pen, wedge);
            }
        }

        /// <summary>The middle: the words every layer on the wheel shares, one to a line.</summary>
        private void DrawCentre(Graphics g)
        {
            float r = (float)_layout.CentreRadius - _gap;
            using (var fill = new SolidBrush(CentreFill))
            using (var edge = new Pen(Color.FromArgb(120, 255, 255, 255), _scale))
            {
                g.FillEllipse(fill, _centre.X - r, _centre.Y - r, r * 2, r * 2);
                g.DrawEllipse(edge, _centre.X - r, _centre.Y - r, r * 2, r * 2);
            }

            string label = _layout.CentreLabel.Replace(' ', '\n');
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
        private void DrawCaption(Graphics g, WheelLeaf? leaf)
        {
            string text;
            if (leaf != null)
            {
                WheelLayerState state = _layers[leaf.LayerIndex];
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
                text = PathOf(_hover);
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

        /// <summary>"AE CIV PLN": the words in the middle, then the rings down to this group.</summary>
        private string PathOf(WheelNode node)
        {
            var words = new List<string>();
            for (WheelNode? n = node; n != null && n.Depth > 0; n = n.Parent)
            {
                words.Insert(0, n.Term);
            }

            return (_layout.CentreLabel + " " + string.Join(" ", words)).Trim();
        }

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
