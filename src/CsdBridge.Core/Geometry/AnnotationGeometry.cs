using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CsdBridge.Core.Drawing;

namespace CsdBridge.Core.Geometry
{
    /// <summary>
    /// Geometry of annotation that doesn't need a CAD application: what a dimension
    /// measures, and roughly how big a piece of text is. BricsCAD reports real text sizes;
    /// the estimates here are for the offline host and for dimension text.
    /// </summary>
    public static class AnnotationGeometry
    {
        /// <summary>Average width of a character, as a fraction of the text height.</summary>
        public const double CharWidth = 0.9;

        /// <summary>Distance between lines of multi-line text, as a fraction of the text height.</summary>
        public const double LineSpacing = 1.667;

        /// <summary>The value a dimension shows: a length, or degrees for an angular dimension.</summary>
        public static double Measure(EntitySpec spec)
        {
            switch (spec)
            {
                case LinearDimensionSpec d:
                {
                    double r = d.RotationDeg * Math.PI / 180;
                    return Math.Abs((d.P2.X - d.P1.X) * Math.Cos(r) + (d.P2.Y - d.P1.Y) * Math.Sin(r));
                }

                case AlignedDimensionSpec a:
                    return Dist(a.P1, a.P2);

                case RadialDimensionSpec r:
                    return r.Diameter ? 2 * r.Radius : r.Radius;

                case AngularDimensionSpec a:
                {
                    double sweep = Sweep(a);
                    return a.ArcLength ? sweep * Dist(a.Center, a.P1) : sweep * 180 / Math.PI;
                }

                case OrdinateDimensionSpec o:
                {
                    Point2 origin = o.Origin ?? new Point2(0, 0);
                    return o.UseXAxis ? o.FeaturePoint.X - origin.X : o.FeaturePoint.Y - origin.Y;
                }

                default:
                    return 0;
            }
        }

        /// <summary>The dimension's value as its text would show it.</summary>
        public static string MeasureText(EntitySpec spec)
        {
            string value = Measure(spec).ToString("0.###", CultureInfo.InvariantCulture);
            switch (spec)
            {
                case RadialDimensionSpec r: return (r.Diameter ? "%%c" : "R") + value;
                case AngularDimensionSpec a when !a.ArcLength: return value + "%%d";
                default: return value;
            }
        }

        /// <summary>
        /// The angle from the P1 leg to the P2 leg, in radians, on the side of the vertex
        /// that <see cref="AngularDimensionSpec.ArcPoint"/> lies on.
        /// </summary>
        public static double Sweep(AngularDimensionSpec a) => SweepFrom(a, out _);

        /// <summary>As <see cref="Sweep"/>; also gives the angle the sweep starts at and its sign.</summary>
        public static double SweepFrom(AngularDimensionSpec a, out (double Start, double Signed) arc)
        {
            double a1 = Math.Atan2(a.P1.Y - a.Center.Y, a.P1.X - a.Center.X);
            double a2 = Math.Atan2(a.P2.Y - a.Center.Y, a.P2.X - a.Center.X);
            double am = Math.Atan2(a.ArcPoint.Y - a.Center.Y, a.ArcPoint.X - a.Center.X);
            double ccw = Norm(a2 - a1);
            bool arcPointInCcw = Norm(am - a1) <= ccw + 1e-12;
            double sweep = arcPointInCcw ? ccw : 2 * Math.PI - ccw;
            arc = (a1, arcPointInCcw ? sweep : -sweep);
            return sweep;
        }

        /// <summary>
        /// A box of the given size around text, turned by <paramref name="rotationDeg"/>. The
        /// anchor is where the text's insertion point sits in the box, as fractions of its
        /// width and height (0, 0 = bottom-left; 0, 1 = top-left; 0.5, 0.5 = centre).
        /// Corners come back as bottom-left, bottom-right, top-right, top-left.
        /// </summary>
        public static Point2[] Box(Point2 anchor, double width, double height, double rotationDeg, double anchorX, double anchorY)
        {
            double r = rotationDeg * Math.PI / 180, cos = Math.Cos(r), sin = Math.Sin(r);
            Point2 At(double x, double y)
            {
                double lx = x - anchorX * width, ly = y - anchorY * height;
                return new Point2(anchor.X + lx * cos - ly * sin, anchor.Y + lx * sin + ly * cos);
            }

            return new[] { At(0, 0), At(width, 0), At(width, height), At(0, height) };
        }

        public static double[][] ToArrays(Point2[] box) => box.Select(p => p.ToArray(4)).ToArray();

        /// <summary>Lines of a text as displayed, wrapped at whole words to a width when one is given.</summary>
        public static List<string> Lines(string text, double height, double? wrapWidth)
        {
            var lines = new List<string>();
            int maxChars = wrapWidth is double w && w > 0 ? Math.Max(1, (int)Math.Floor(w / (CharWidth * height))) : int.MaxValue;
            foreach (string raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                string rest = raw;
                while (rest.Length > maxChars)
                {
                    int cut = rest.LastIndexOf(' ', maxChars);
                    if (cut <= 0)
                    {
                        cut = maxChars;
                    }

                    lines.Add(rest.Substring(0, cut).TrimEnd());
                    rest = rest.Substring(cut).TrimStart();
                }

                lines.Add(rest);
            }

            return lines;
        }

        /// <summary>Estimated width and height of a block of text lines.</summary>
        public static (double Width, double Height) Size(IReadOnlyList<string> lines, double height)
        {
            int longest = lines.Count == 0 ? 0 : lines.Max(l => l.Length);
            return (Math.Max(longest, 1) * CharWidth * height, height + Math.Max(0, lines.Count - 1) * LineSpacing * height);
        }

        /// <summary>The estimated box around single-line text.</summary>
        public static Point2[] TextBox(TextSpec t, double height)
        {
            (double w, double h) = Size(new[] { t.Text }, height);
            switch (t.Align)
            {
                case TextAlign.Center: return Box(t.Position, w, h, t.RotationDeg, 0.5, 0);
                case TextAlign.Right: return Box(t.Position, w, h, t.RotationDeg, 1, 0);
                case TextAlign.MiddleCenter: return Box(t.Position, w, h, t.RotationDeg, 0.5, 0.5);
                default: return Box(t.Position, w, h, t.RotationDeg, 0, 0);
            }
        }

        /// <summary>The estimated box around multi-line text (its position is the top-left corner).</summary>
        public static Point2[] TextBox(MTextSpec m, double height)
        {
            (double w, double h) = Size(Lines(m.Text, height, m.Width), height);
            return Box(m.Position, w, h, m.RotationDeg, 0, 1);
        }

        /// <summary>
        /// The estimated box around a multileader's text: beside the landing point, on the
        /// side away from the arrows.
        /// </summary>
        public static Point2[] TextBox(MLeaderSpec m, double height)
        {
            (double w, double h) = Size(Lines(m.Text, height, m.Width), height);
            bool textOnRight = m.ArrowPoints.Count == 0 || m.ArrowPoints.Average(p => p.X) <= m.TextPosition.X;
            double gap = 0.5 * height;
            var anchor = new Point2(m.TextPosition.X + (textOnRight ? gap : -gap), m.TextPosition.Y);
            return Box(anchor, w, h, 0, textOnRight ? 0 : 1, 0.5);
        }

        /// <summary>Where a dimension's text sits, roughly: its box, turned to read along the dimension.</summary>
        public static Point2[] TextBox(DimensionSpec d, double height)
        {
            string text = d.TextOverride ?? MeasureText(d).Replace("%%c", "D").Replace("%%d", "d");
            (double w, double h) = Size(new[] { text }, height);
            (Point2 centre, double rotationDeg) = TextPlace(d, height);
            return Box(centre, w, h, Readable(rotationDeg), 0.5, 0.5);
        }

        /// <summary>The centre and direction of a dimension's text.</summary>
        public static (Point2 Centre, double RotationDeg) TextPlace(DimensionSpec d, double height)
        {
            switch (d)
            {
                case LinearDimensionSpec l:
                    return OnDimLine(l.P1, l.P2, l.DimLinePoint, l.RotationDeg, height);

                case AlignedDimensionSpec a:
                    return OnDimLine(a.P1, a.P2, a.DimLinePoint, Math.Atan2(a.P2.Y - a.P1.Y, a.P2.X - a.P1.X) * 180 / Math.PI, height);

                case RadialDimensionSpec r:
                {
                    double angle = Math.Atan2(r.ChordPoint.Y - r.Center.Y, r.ChordPoint.X - r.Center.X);
                    double outBy = (r.LeaderLength ?? 2 * height) + 2 * height;
                    return (new Point2(r.ChordPoint.X + Math.Cos(angle) * outBy, r.ChordPoint.Y + Math.Sin(angle) * outBy), 0);
                }

                case AngularDimensionSpec g:
                {
                    SweepFrom(g, out (double Start, double Signed) arc);
                    double mid = arc.Start + arc.Signed / 2;
                    double radius = Dist(g.Center, g.ArcPoint) + height;
                    return (new Point2(g.Center.X + Math.Cos(mid) * radius, g.Center.Y + Math.Sin(mid) * radius), mid * 180 / Math.PI - 90);
                }

                case OrdinateDimensionSpec o:
                    return (o.LeaderEnd, o.UseXAxis ? 90 : 0);

                default:
                    return (new Point2(0, 0), 0);
            }
        }

        /// <summary>The two ends of a linear or aligned dimension's dimension line.</summary>
        public static (Point2 Foot1, Point2 Foot2) DimLine(Point2 p1, Point2 p2, Point2 through, double rotationDeg)
        {
            double r = rotationDeg * Math.PI / 180, ux = Math.Cos(r), uy = Math.Sin(r);
            Point2 Foot(Point2 p)
            {
                double t = (p.X - through.X) * ux + (p.Y - through.Y) * uy;
                return new Point2(through.X + ux * t, through.Y + uy * t);
            }

            return (Foot(p1), Foot(p2));
        }

        /// <summary>
        /// Column widths, row height and text height of a table, filling in what the spec leaves
        /// out: columns wide enough for their longest text, rows twice the text height.
        /// </summary>
        public static (List<double> ColumnWidths, double RowHeight, double TextHeight) TableLayout(TableSpec t, double defaultTextHeight)
        {
            double h = t.TextHeight ?? defaultTextHeight;
            double rowHeight = t.RowHeight ?? 2 * h;
            if (t.ColumnWidths != null)
            {
                return (t.ColumnWidths.ToList(), rowHeight, h);
            }

            List<double> widths = Enumerable.Range(0, t.ColumnCount)
                .Select(c => (Math.Max(3, t.Rows.Max(r => r[c].Length)) * CharWidth + 2) * h)
                .ToList();
            if (t.Title != null)
            {
                // The title spans every column: widen them all if it wouldn't fit.
                double need = (t.Title.Length * CharWidth + 2) * h, total = widths.Sum();
                if (need > total)
                {
                    widths = widths.Select(w => w * need / total).ToList();
                }
            }

            return (widths, rowHeight, h);
        }

        /// <summary>Every row of a table as text, the title (if any) first, in a row of its own.</summary>
        public static List<List<string>> TableCells(TableSpec t)
        {
            var all = new List<List<string>>();
            if (t.Title != null)
            {
                var title = new List<string> { t.Title };
                title.AddRange(Enumerable.Repeat("", Math.Max(0, t.ColumnCount - 1)));
                all.Add(title);
            }

            all.AddRange(t.Rows);
            return all;
        }

        /// <summary>Turn text that would read upside down the other way up.</summary>
        public static double Readable(double rotationDeg)
        {
            double a = rotationDeg % 360;
            if (a < 0)
            {
                a += 360;
            }

            return a > 90.0001 && a <= 270.0001 ? a - 180 : a;
        }

        private static (Point2, double) OnDimLine(Point2 p1, Point2 p2, Point2 through, double rotationDeg, double height)
        {
            (Point2 f1, Point2 f2) = DimLine(p1, p2, through, rotationDeg);
            double r = Readable(rotationDeg) * Math.PI / 180;
            var mid = new Point2((f1.X + f2.X) / 2 - Math.Sin(r) * height, (f1.Y + f2.Y) / 2 + Math.Cos(r) * height);
            return (mid, rotationDeg);
        }

        private static double Norm(double angle)
        {
            double a = angle % (2 * Math.PI);
            return a < 0 ? a + 2 * Math.PI : a;
        }

        private static double Dist(Point2 a, Point2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }
}
