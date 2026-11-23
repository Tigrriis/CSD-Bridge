using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;

namespace CsdBridge.Core.Rendering
{
    /// <summary>The part of the drawing to look at, in world coordinates.</summary>
    public readonly struct ViewWindow
    {
        public ViewWindow(double minX, double minY, double maxX, double maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        public double MinX { get; }
        public double MinY { get; }
        public double MaxX { get; }
        public double MaxY { get; }
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
    }

    public sealed class RenderedView
    {
        public byte[] Png { get; set; } = new byte[0];
        public int Width { get; set; }
        public int Height { get; set; }
        public double UnitsPerPixel { get; set; }

        /// <summary>Spacing of the coordinate grid drawn behind the entities, in drawing units.</summary>
        public double GridSpacing { get; set; }
    }

    /// <summary>
    /// Draws a schematic picture of entities for Claude to look at: linework, the box and
    /// text of each piece of annotation, dimensions, multileaders, tables and block
    /// outlines over a labelled coordinate grid. It is drawn from entity summaries, so it
    /// needs no CAD application; text boxes come from the host (BricsCAD measures the real
    /// text), which is what makes overlaps in the picture true to the drawing.
    /// </summary>
    public static class SchematicRenderer
    {
        private const int MinSide = 160;
        private const int MaxSide = 1568; // larger pictures are scaled down by the API anyway

        private static readonly Rgb Linework = Rgb.Hex(0x3C3C3C);
        private static readonly Rgb Other = Rgb.Hex(0xB4B4B4);
        private static readonly Rgb Block = Rgb.Hex(0x8A4FBF);
        private static readonly Rgb Dimension = Rgb.Hex(0x0B7D32);
        private static readonly Rgb Leader = Rgb.Hex(0x1F5FBF);
        private static readonly Rgb TextBoxLine = Rgb.Hex(0x8FB4EA);
        private static readonly Rgb Clash = Rgb.Hex(0xD81E1E);
        private static readonly Rgb ClashFill = Rgb.Hex(0xFDDCDC);
        private static readonly Rgb Ink = Rgb.Hex(0x000000);
        private static readonly Rgb Grid = Rgb.Hex(0xE6E6E6);
        private static readonly Rgb GridText = Rgb.Hex(0x8C8C8C);

        private sealed class View
        {
            public Canvas Canvas = null!;
            public double Scale, CentreX, CentreY;

            public (double X, double Y) Px(double x, double y) =>
                ((x - CentreX) * Scale + Canvas.Width / 2.0, Canvas.Height / 2.0 - (y - CentreY) * Scale);

            public (double X, double Y) Px(Point2 p) => Px(p.X, p.Y);

            public void Line(Point2 a, Point2 b, Rgb color, double width = 1)
            {
                (double x0, double y0) = Px(a);
                (double x1, double y1) = Px(b);
                Canvas.Line(x0, y0, x1, y1, color, width);
            }
        }

        public static RenderedView Render(IReadOnlyList<EntitySummary> entities, ViewWindow window, int widthPx, ISet<string>? clashing = null)
        {
            double scale = widthPx / window.Width;
            int width = widthPx, height = (int)Math.Round(window.Height * scale);
            if (height > MaxSide)
            {
                scale = MaxSide / window.Height;
                height = MaxSide;
                width = Math.Max(MinSide, (int)Math.Round(window.Width * scale));
            }

            height = Math.Max(MinSide, height);
            var view = new View
            {
                Canvas = new Canvas(width, height),
                Scale = scale,
                CentreX = (window.MinX + window.MaxX) / 2,
                CentreY = (window.MinY + window.MaxY) / 2,
            };

            double grid = DrawGrid(view);
            clashing ??= new HashSet<string>();

            // Fills first, then linework, then annotation, so text is never hidden.
            foreach (EntitySummary e in entities)
            {
                if (clashing.Contains(e.Handle) && AnnotationClashes.BoxOf(e) is Point2[] box)
                {
                    view.Canvas.Fill(box.Select(p => view.Px(p)).ToList(), ClashFill);
                }
            }

            foreach (EntitySummary e in entities)
            {
                DrawGeometry(view, e);
            }

            foreach (EntitySummary e in entities)
            {
                DrawAnnotation(view, e, clashing.Contains(e.Handle));
            }

            return new RenderedView
            {
                Png = view.Canvas.ToPng(),
                Width = width,
                Height = height,
                UnitsPerPixel = 1 / scale,
                GridSpacing = grid,
            };
        }

        // ------------------------------------------------------------------ grid

        private static double DrawGrid(View view)
        {
            double worldWidth = view.Canvas.Width / view.Scale, worldHeight = view.Canvas.Height / view.Scale;
            double step = NiceStep(worldWidth / 8);
            double minX = view.CentreX - worldWidth / 2, maxX = view.CentreX + worldWidth / 2;
            double minY = view.CentreY - worldHeight / 2, maxY = view.CentreY + worldHeight / 2;
            string format = step >= 1 ? "0" : "0." + new string('0', (int)Math.Ceiling(-Math.Log10(step)));
            const double labelPx = 9;

            // Count in whole steps so the labels stay exact, and 0 never prints as -0.
            string Text(long k) => (k == 0 ? 0 : k * step).ToString(format, CultureInfo.InvariantCulture);

            for (long k = (long)Math.Ceiling(minX / step); k * step <= maxX; k++)
            {
                (double px, double _) = view.Px(k * step, 0);
                view.Canvas.Line(px, 0, px, view.Canvas.Height, Grid);
                Label(view.Canvas, Text(k), px + 3, view.Canvas.Height - 4, labelPx, GridText);
            }

            for (long k = (long)Math.Ceiling(minY / step); k * step <= maxY; k++)
            {
                (double _, double py) = view.Px(0, k * step);
                view.Canvas.Line(0, py, view.Canvas.Width, py, Grid);
                if (py > labelPx + 6 && py < view.Canvas.Height - 2 * labelPx)
                {
                    Label(view.Canvas, Text(k), 3, py - 3, labelPx, GridText);
                }
            }

            return step;
        }

        /// <summary>1, 2 or 5 times a power of ten, close to the value.</summary>
        private static double NiceStep(double value)
        {
            double power = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(value, 1e-12))));
            double fraction = value / power;
            return (fraction < 1.5 ? 1 : fraction < 3.5 ? 2 : fraction < 7.5 ? 5 : 10) * power;
        }

        /// <summary>Text straight on the picture (not in the drawing): baseline-left at a pixel, capitals this many pixels high.</summary>
        private static void Label(Canvas canvas, string text, double x, double y, double heightPx, Rgb color) =>
            StrokeFont.Strokes(text, (x0, y0, x1, y1) =>
                canvas.Line(x + x0 * heightPx, y - y0 * heightPx, x + x1 * heightPx, y - y1 * heightPx, color));

        // ------------------------------------------------------------------ linework

        private static void DrawGeometry(View view, EntitySummary e)
        {
            switch (e.Kind)
            {
                case "text":
                case "mtext":
                case "mleader":
                case "dimension":
                case "table":
                case "hatch":
                    return;

                case "block_insert":
                {
                    // An outline with the block's name; a cross where the block is too small to outline.
                    Point2[]? outline = Bounds(e);
                    bool tiny = outline == null || Math.Max(Segment.Dist(outline[0], outline[1]), Segment.Dist(outline[0], outline[3])) * view.Scale < 6;
                    if (!tiny)
                    {
                        Polygon(view, outline!, Block);
                    }

                    Point2? at = tiny ? (e.Position != null ? P(e.Position) : outline?[0]) : outline![3];
                    if (at is Point2 anchor)
                    {
                        (double x, double y) = view.Px(anchor);
                        if (tiny)
                        {
                            view.Canvas.Line(x - 5, y - 5, x + 5, y + 5, Block, 1.5);
                            view.Canvas.Line(x - 5, y + 5, x + 5, y - 5, Block, 1.5);
                        }

                        if (e.Block != null && (tiny || Segment.Dist(outline![0], outline[1]) * view.Scale > 30))
                        {
                            Label(view.Canvas, e.Block, x + (tiny ? 8 : 2), y + (tiny ? -6 : 11), 8, Block);
                        }
                    }

                    return;
                }
            }

            Path2? path = PathOf(e);
            if (path != null)
            {
                foreach (Segment s in path.Segments)
                {
                    DrawSegment(view, s, Linework, 1.2);
                }
            }
            else if (Bounds(e) is Point2[] box)
            {
                Polygon(view, box, Other);
            }
        }

        private static Path2? PathOf(EntitySummary e)
        {
            try
            {
                if (e.Points != null && e.Points.Length >= 2)
                {
                    var spec = new PolylineSpec
                    {
                        Points = e.Points.Select(p => new Point2(p[0], p[1])).ToList(),
                        Closed = e.Closed == true,
                        Bulges = e.Bulges != null && e.Bulges.Length == e.Points.Length ? e.Bulges.ToList() : null,
                    };
                    return Path2.From(spec);
                }

                if (e.Kind == "line" && e.Start != null && e.End != null)
                {
                    return Path2.From(new LineSpec { Start = P(e.Start), End = P(e.End) });
                }

                if (e.Center != null && e.Radius is double r && r > 0)
                {
                    return e.StartAngle is double a0 && e.EndAngle is double a1
                        ? Path2.From(new ArcSpec { Center = P(e.Center), Radius = r, StartAngleDeg = a0, EndAngleDeg = a1 })
                        : Path2.From(new CircleSpec { Center = P(e.Center), Radius = r });
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is DivideByZeroException)
            {
                // Odd geometry: fall through to its bounding box.
            }

            return null;
        }

        private static void DrawSegment(View view, Segment s, Rgb color, double width)
        {
            if (s is ArcSegment arc)
            {
                DrawArc(view, arc.Center, arc.Radius, arc.StartAngle, arc.Sweep, color, width);
            }
            else
            {
                view.Line(s.Start, s.End, color, width);
            }
        }

        private static void DrawArc(View view, Point2 centre, double radius, double startAngle, double sweep, Rgb color, double width)
        {
            double radiusPx = radius * view.Scale;
            int pieces = (int)Math.Max(6, Math.Min(2000, Math.Ceiling(Math.Abs(sweep) * Math.Sqrt(Math.Max(radiusPx, 1)) * 1.5)));
            Point2 previous = At(centre, radius, startAngle);
            for (int i = 1; i <= pieces; i++)
            {
                Point2 next = At(centre, radius, startAngle + sweep * i / pieces);
                view.Line(previous, next, color, width);
                previous = next;
            }
        }

        // ------------------------------------------------------------------ annotation

        private static void DrawAnnotation(View view, EntitySummary e, bool clashing)
        {
            switch (e.Kind)
            {
                case "text":
                case "mtext":
                    DrawTextBox(view, e, e.Text, clashing);
                    break;

                case "mleader":
                    DrawLeaders(view, e);
                    DrawTextBox(view, e, e.Text, clashing);
                    break;

                case "dimension":
                    DrawDimension(view, e);
                    DrawTextBox(view, e, DimensionText(e), clashing, outline: clashing);
                    break;

                case "table":
                    DrawTable(view, e, clashing);
                    break;
            }
        }

        private static string? DimensionText(EntitySummary e)
        {
            if (!string.IsNullOrEmpty(e.Text) && e.Text != "<>")
            {
                return e.Text;
            }

            return e.Measurement is double m ? m.ToString("0.###", CultureInfo.InvariantCulture) : null;
        }

        private static void DrawTextBox(View view, EntitySummary e, string? text, bool clashing, bool outline = true)
        {
            Point2[]? box = AnnotationClashes.BoxOf(e);
            if (box == null)
            {
                return;
            }

            if (outline)
            {
                Polygon(view, box, clashing ? Clash : TextBoxLine, clashing ? 1.6 : 1);
            }

            if (!string.IsNullOrEmpty(text))
            {
                TextInBox(view, text!, box, Ink);
            }
        }

        /// <summary>Draw text scaled to fit a box (bottom-left, bottom-right, top-right, top-left), reading from the first corner to the second.</summary>
        private static void TextInBox(View view, string text, Point2[] box, Rgb color)
        {
            string[] lines = text.Replace("\r", "").Split('\n');
            double boxWidth = Segment.Dist(box[0], box[1]), boxHeight = Segment.Dist(box[0], box[3]);
            if (boxWidth <= 0 || boxHeight <= 0)
            {
                return;
            }

            double naturalWidth = Math.Max(1, lines.Max(l => l.Length)) * StrokeFont.Advance;
            double naturalHeight = 1 + (lines.Length - 1) * AnnotationGeometry.LineSpacing;
            double k = Math.Min(boxWidth / naturalWidth, boxHeight / naturalHeight); // drawing units per capital height
            double ux = (box[1].X - box[0].X) / boxWidth, uy = (box[1].Y - box[0].Y) / boxWidth;
            double vx = (box[3].X - box[0].X) / boxHeight, vy = (box[3].Y - box[0].Y) / boxHeight;
            double slackX = (boxWidth - naturalWidth * k) / 2, slackY = (boxHeight - naturalHeight * k) / 2;
            double capPx = k * view.Scale;

            for (int i = 0; i < lines.Length; i++)
            {
                double baseline = slackY + k * (naturalHeight - 1 - i * AnnotationGeometry.LineSpacing);
                Point2 At(double x, double y) => new Point2(
                    box[0].X + ux * (slackX + x * k) + vx * (baseline + y * k),
                    box[0].Y + uy * (slackX + x * k) + vy * (baseline + y * k));

                if (capPx < 4.5)
                {
                    // Too small to read: a bar where the line of text is.
                    if (lines[i].Length > 0)
                    {
                        view.Line(At(0, 0.5), At(lines[i].Length * StrokeFont.Advance, 0.5), color, Math.Max(1, capPx * 0.5));
                    }

                    continue;
                }

                StrokeFont.Strokes(lines[i], (x0, y0, x1, y1) => view.Line(At(x0, y0), At(x1, y1), color, capPx > 16 ? 1.5 : 1));
            }
        }

        private static void DrawLeaders(View view, EntitySummary e)
        {
            if (e.ArrowPoints == null || e.Position == null)
            {
                return;
            }

            Point2 landing = P(e.Position);
            foreach (double[] arrow in e.ArrowPoints)
            {
                Point2 tip = P(arrow);
                view.Line(tip, landing, Leader, 1.2);

                // Arrowhead, a fixed size on the picture.
                (double tx, double ty) = view.Px(tip);
                (double lx, double ly) = view.Px(landing);
                double length = Math.Sqrt((lx - tx) * (lx - tx) + (ly - ty) * (ly - ty));
                if (length > 6)
                {
                    double dx = (lx - tx) / length, dy = (ly - ty) / length;
                    view.Canvas.Fill(
                        new[]
                        {
                            (tx, ty),
                            (tx + dx * 9 - dy * 3, ty + dy * 9 + dx * 3),
                            (tx + dx * 9 + dy * 3, ty + dy * 9 - dx * 3),
                        },
                        Leader);
                }
            }
        }

        private static void DrawDimension(View view, EntitySummary e)
        {
            Point2? start = e.Start == null ? (Point2?)null : P(e.Start);
            Point2? end = e.End == null ? (Point2?)null : P(e.End);
            Point2? centre = e.Center == null ? (Point2?)null : P(e.Center);
            Point2? through = e.DimLinePoint == null ? (Point2?)null : P(e.DimLinePoint);

            switch (e.DimType)
            {
                case "linear" when start != null && end != null && through != null:
                case "aligned" when start != null && end != null && through != null:
                {
                    double rotation = e.DimType == "aligned"
                        ? Math.Atan2(end.Value.Y - start.Value.Y, end.Value.X - start.Value.X) * 180 / Math.PI
                        : e.Rotation ?? 0;
                    (Point2 f1, Point2 f2) = AnnotationGeometry.DimLine(start.Value, end.Value, through.Value, rotation);
                    view.Line(start.Value, f1, Dimension);
                    view.Line(end.Value, f2, Dimension);
                    view.Line(f1, f2, Dimension, 1.2);
                    return;
                }

                case "radius" when centre != null && start != null:
                    view.Line(centre.Value, start.Value, Dimension, 1.2);
                    return;

                case "diameter" when centre != null && start != null:
                    view.Line(new Point2(2 * centre.Value.X - start.Value.X, 2 * centre.Value.Y - start.Value.Y), start.Value, Dimension, 1.2);
                    return;

                case "angular" when centre != null && start != null && end != null && through != null:
                case "arc_length" when centre != null && start != null && end != null && through != null:
                {
                    var spec = new AngularDimensionSpec { Center = centre.Value, P1 = start.Value, P2 = end.Value, ArcPoint = through.Value };
                    AnnotationGeometry.SweepFrom(spec, out (double Start, double Signed) arc);
                    double radius = Segment.Dist(centre.Value, through.Value);
                    DrawArc(view, centre.Value, radius, arc.Start, arc.Signed, Dimension, 1.2);
                    view.Line(start.Value, At(centre.Value, radius, arc.Start), Dimension);
                    view.Line(end.Value, At(centre.Value, radius, arc.Start + arc.Signed), Dimension);
                    return;
                }

                case "ordinate" when start != null && end != null:
                    view.Line(start.Value, end.Value, Dimension, 1.2);
                    return;
            }

            if (Bounds(e) is Point2[] box)
            {
                Polygon(view, box, Dimension);
            }
        }

        private static void DrawTable(View view, EntitySummary e, bool clashing)
        {
            if (!(Bounds(e) is Point2[] box))
            {
                return;
            }

            Rgb line = clashing ? Clash : Linework;
            Polygon(view, box, line, clashing ? 1.6 : 1.2);
            int rows = e.RowCount ?? e.Rows?.Length ?? 0, columns = e.ColumnCount ?? (e.Rows != null && e.Rows.Length > 0 ? e.Rows[0].Length : 0);
            if (rows <= 0 || columns <= 0)
            {
                return;
            }

            double left = box[0].X, right = box[1].X, bottom = box[0].Y, top = box[3].Y;
            double rowHeight = (top - bottom) / rows;
            var edges = new List<double> { left };
            double total = e.ColumnWidths != null && e.ColumnWidths.Length == columns ? e.ColumnWidths.Sum() : 0;
            for (int c = 0; c < columns; c++)
            {
                double share = total > 0 ? e.ColumnWidths![c] / total : 1.0 / columns;
                edges.Add(edges[c] + share * (right - left));
            }

            for (int r = 1; r < rows; r++)
            {
                view.Line(new Point2(left, top - r * rowHeight), new Point2(right, top - r * rowHeight), line);
            }

            // A title is one merged cell: its row has text in the first cell only.
            bool titleRow = e.Rows != null && e.Rows.Length > 1 && columns > 1 && e.Rows[0].Skip(1).All(string.IsNullOrEmpty) && e.Rows[1].Skip(1).Any(s => !string.IsNullOrEmpty(s));
            for (int c = 1; c < columns; c++)
            {
                view.Line(new Point2(edges[c], titleRow ? top - rowHeight : top), new Point2(edges[c], bottom), line);
            }

            if (e.Rows == null)
            {
                return;
            }

            for (int r = 0; r < e.Rows.Length && r < rows; r++)
            {
                for (int c = 0; c < e.Rows[r].Length && c < columns; c++)
                {
                    string text = e.Rows[r][c];
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    double x0 = r == 0 && titleRow ? left : edges[c], x1 = r == 0 && titleRow ? right : edges[c + 1];
                    double y1 = top - r * rowHeight, y0 = y1 - rowHeight;
                    double mx = (x1 - x0) * 0.08, my = rowHeight * 0.25;
                    TextInBox(view, text.Replace("\n", " "), new[]
                    {
                        new Point2(x0 + mx, y0 + my), new Point2(x1 - mx, y0 + my), new Point2(x1 - mx, y1 - my), new Point2(x0 + mx, y1 - my),
                    }, Ink);
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        private static void Polygon(View view, Point2[] corners, Rgb color, double width = 1)
        {
            for (int i = 0; i < corners.Length; i++)
            {
                view.Line(corners[i], corners[(i + 1) % corners.Length], color, width);
            }
        }

        private static Point2[]? Bounds(EntitySummary e) =>
            e.BoundsMin == null || e.BoundsMax == null
                ? null
                : new[]
                {
                    new Point2(e.BoundsMin[0], e.BoundsMin[1]), new Point2(e.BoundsMax[0], e.BoundsMin[1]),
                    new Point2(e.BoundsMax[0], e.BoundsMax[1]), new Point2(e.BoundsMin[0], e.BoundsMax[1]),
                };

        private static Point2 P(double[] p) => new Point2(p[0], p[1]);

        private static Point2 At(Point2 centre, double radius, double angle) =>
            new Point2(centre.X + radius * Math.Cos(angle), centre.Y + radius * Math.Sin(angle));
    }
}
