using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;

namespace CsdBridge.Core.Offline
{
    /// <summary>
    /// Writes an <see cref="InMemoryDrawingHost"/> to a minimal R12 ASCII DXF so the
    /// CLI harness output can be opened in BricsCAD (or any DXF viewer) for a look.
    ///
    /// This is a preview, not an exporter: MText becomes one TEXT per line, dimensions
    /// are exploded into lines + text, block inserts become a marker, and every layer
    /// uses CONTINUOUS linetype.
    /// </summary>
    public static class DxfPreviewWriter
    {
        public static void Write(InMemoryDrawingHost drawing, string path)
        {
            using (var writer = new StreamWriter(path, false))
            {
                Write(drawing, writer);
            }
        }

        public static void Write(InMemoryDrawingHost drawing, TextWriter w)
        {
            var dxf = new Dxf(w);

            dxf.Pair(0, "SECTION").Pair(2, "HEADER");
            dxf.Pair(9, "$ACADVER").Pair(1, "AC1009");
            dxf.Pair(0, "ENDSEC");

            dxf.Pair(0, "SECTION").Pair(2, "TABLES");
            dxf.Pair(0, "TABLE").Pair(2, "LTYPE").Pair(70, 1);
            dxf.Pair(0, "LTYPE").Pair(2, "CONTINUOUS").Pair(70, 0).Pair(3, "Solid line").Pair(72, 65).Pair(73, 0).Pair(40, 0.0);
            dxf.Pair(0, "ENDTAB");
            var layers = drawing.Layers.ToList();
            dxf.Pair(0, "TABLE").Pair(2, "LAYER").Pair(70, layers.Count);
            foreach (LayerInfo layer in layers)
            {
                dxf.Pair(0, "LAYER").Pair(2, layer.Name).Pair(70, 0).Pair(62, (int)layer.Color).Pair(6, "CONTINUOUS");
            }

            dxf.Pair(0, "ENDTAB");
            dxf.Pair(0, "ENDSEC");

            dxf.Pair(0, "SECTION").Pair(2, "ENTITIES");
            foreach (var e in drawing.Entities)
            {
                WriteEntity(dxf, e.Spec, e.Layer, drawing.DefaultTextHeight);
            }

            dxf.Pair(0, "ENDSEC");
            dxf.Pair(0, "EOF");
        }

        private static void WriteEntity(Dxf dxf, EntitySpec spec, string layer, double defaultHeight)
        {
            short? color = spec.ColorIndex;
            switch (spec)
            {
                case LineSpec l:
                    Line(dxf, layer, color, l.Start, l.End);
                    break;

                case PolylineSpec p:
                    dxf.Pair(0, "POLYLINE").Common(layer, color).Pair(66, 1).Point(10, new Point2(0, 0)).Pair(70, p.Closed ? 1 : 0);
                    for (int i = 0; i < p.Points.Count; i++)
                    {
                        dxf.Pair(0, "VERTEX").Common(layer, null).Point(10, p.Points[i]);
                        double bulge = p.Bulges != null && i < p.Bulges.Count ? p.Bulges[i] : 0;
                        if (bulge != 0)
                        {
                            dxf.Pair(42, bulge);
                        }
                    }

                    dxf.Pair(0, "SEQEND").Common(layer, null);
                    break;

                case HatchSpec h:
                    // Preview: the boundary loops (the pattern itself isn't drawn).
                    foreach (PolylineSpec loop in h.Loops)
                    {
                        dxf.Pair(0, "POLYLINE").Common(layer, color).Pair(66, 1).Point(10, new Point2(0, 0)).Pair(70, 1);
                        for (int k = 0; k < loop.Points.Count; k++)
                        {
                            dxf.Pair(0, "VERTEX").Common(layer, null).Point(10, loop.Points[k]);
                            double bulge = loop.Bulges != null && k < loop.Bulges.Count ? loop.Bulges[k] : 0;
                            if (bulge != 0)
                            {
                                dxf.Pair(42, bulge);
                            }
                        }

                        dxf.Pair(0, "SEQEND").Common(layer, null);
                    }

                    break;

                case CircleSpec c:
                    dxf.Pair(0, "CIRCLE").Common(layer, color).Point(10, c.Center).Pair(40, c.Radius);
                    break;

                case ArcSpec a:
                    dxf.Pair(0, "ARC").Common(layer, color).Point(10, a.Center).Pair(40, a.Radius)
                       .Pair(50, a.StartAngleDeg).Pair(51, a.EndAngleDeg);
                    break;

                case TextSpec t:
                    Text(dxf, layer, color, t.Position, t.Text, t.Height ?? defaultHeight, t.RotationDeg, t.Align);
                    break;

                case MTextSpec m:
                {
                    double h = m.Height ?? defaultHeight;
                    double r = m.RotationDeg * Math.PI / 180;
                    string[] lines = m.Text.Replace("\r", "").Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        // First baseline sits one text height below the top-left corner.
                        double down = h + i * h * 1.667;
                        var p = new Point2(m.Position.X + down * Math.Sin(r), m.Position.Y - down * Math.Cos(r));
                        Text(dxf, layer, color, p, lines[i], h, m.RotationDeg, TextAlign.Left);
                    }

                    break;
                }

                case LinearDimensionSpec d:
                {
                    double r = d.RotationDeg * Math.PI / 180;
                    Dimension(dxf, layer, color, d.P1, d.P2, d.DimLinePoint, new Point2(Math.Cos(r), Math.Sin(r)),
                        d.TextOverride ?? Fmt(InMemoryDrawingHost.Measure(d)), defaultHeight);
                    break;
                }

                case AlignedDimensionSpec d:
                {
                    double len = Math.Sqrt(Math.Pow(d.P2.X - d.P1.X, 2) + Math.Pow(d.P2.Y - d.P1.Y, 2));
                    var u = new Point2((d.P2.X - d.P1.X) / len, (d.P2.Y - d.P1.Y) / len);
                    Dimension(dxf, layer, color, d.P1, d.P2, d.DimLinePoint, u, d.TextOverride ?? Fmt(len), defaultHeight);
                    break;
                }

                case RadialDimensionSpec d:
                {
                    Point2 from = d.Diameter ? new Point2(2 * d.Center.X - d.ChordPoint.X, 2 * d.Center.Y - d.ChordPoint.Y) : d.Center;
                    (Point2 at, double _) = AnnotationGeometry.TextPlace(d, defaultHeight);
                    Line(dxf, layer, color, from, d.ChordPoint);
                    Line(dxf, layer, color, d.ChordPoint, at);
                    DimensionText(dxf, layer, color, d, defaultHeight);
                    break;
                }

                case AngularDimensionSpec d:
                {
                    AnnotationGeometry.SweepFrom(d, out (double Start, double Signed) arc);
                    double radius = Math.Sqrt(Math.Pow(d.ArcPoint.X - d.Center.X, 2) + Math.Pow(d.ArcPoint.Y - d.Center.Y, 2));
                    double a0 = arc.Start * 180 / Math.PI, a1 = (arc.Start + arc.Signed) * 180 / Math.PI;
                    dxf.Pair(0, "ARC").Common(layer, color).Point(10, d.Center).Pair(40, radius)
                       .Pair(50, arc.Signed >= 0 ? a0 : a1).Pair(51, arc.Signed >= 0 ? a1 : a0);
                    Point2 OnArc(double angle) => new Point2(d.Center.X + radius * Math.Cos(angle), d.Center.Y + radius * Math.Sin(angle));
                    Line(dxf, layer, color, d.P1, OnArc(arc.Start));
                    Line(dxf, layer, color, d.P2, OnArc(arc.Start + arc.Signed));
                    DimensionText(dxf, layer, color, d, defaultHeight);
                    break;
                }

                case OrdinateDimensionSpec d:
                    Line(dxf, layer, color, d.FeaturePoint, d.LeaderEnd);
                    DimensionText(dxf, layer, color, d, defaultHeight);
                    break;

                case MLeaderSpec l:
                {
                    double h = l.Height ?? defaultHeight;
                    foreach (Point2 arrow in l.ArrowPoints)
                    {
                        Line(dxf, layer, color, arrow, l.TextPosition);
                    }

                    Point2[] box = AnnotationGeometry.TextBox(l, h);
                    List<string> lines = AnnotationGeometry.Lines(l.Text, h, l.Width);
                    for (int i = 0; i < lines.Count; i++)
                    {
                        // Top line first; box[3] is the top-left corner.
                        Text(dxf, layer, color, new Point2(box[3].X, box[3].Y - h - i * h * AnnotationGeometry.LineSpacing), lines[i], h, 0, TextAlign.Left);
                    }

                    break;
                }

                case TableSpec t:
                {
                    (List<double> widths, double rowHeight, double h) = AnnotationGeometry.TableLayout(t, defaultHeight);
                    List<List<string>> cells = AnnotationGeometry.TableCells(t);
                    double left = t.Position.X, top = t.Position.Y, right = left + widths.Sum(), bottom = top - cells.Count * rowHeight;
                    for (int r = 0; r <= cells.Count; r++)
                    {
                        Line(dxf, layer, color, new Point2(left, top - r * rowHeight), new Point2(right, top - r * rowHeight));
                    }

                    double x = left;
                    for (int c = 0; c <= widths.Count; c++)
                    {
                        // Inner column lines stop under the title, which is one merged cell.
                        bool inner = c > 0 && c < widths.Count;
                        Line(dxf, layer, color, new Point2(x, inner && t.Title != null ? top - rowHeight : top), new Point2(x, bottom));
                        x += c < widths.Count ? widths[c] : 0;
                    }

                    for (int r = 0; r < cells.Count; r++)
                    {
                        bool title = t.Title != null && r == 0;
                        double cellLeft = left;
                        for (int c = 0; c < widths.Count; c++)
                        {
                            double cellWidth = title ? right - left : widths[c];
                            if (cells[r][c].Length > 0)
                            {
                                var middle = new Point2(cellLeft + cellWidth / 2, top - (r + 0.5) * rowHeight);
                                Text(dxf, layer, color, middle, cells[r][c], h, 0, TextAlign.MiddleCenter);
                            }

                            cellLeft += widths[c];
                        }
                    }

                    break;
                }

                case BlockInsertSpec b:
                {
                    double s = defaultHeight;
                    Line(dxf, layer, color, new Point2(b.Position.X - s, b.Position.Y), new Point2(b.Position.X + s, b.Position.Y));
                    Line(dxf, layer, color, new Point2(b.Position.X, b.Position.Y - s), new Point2(b.Position.X, b.Position.Y + s));
                    Text(dxf, layer, color, new Point2(b.Position.X + s, b.Position.Y + s), "[" + b.BlockName + "]", defaultHeight, 0, TextAlign.Left);
                    break;
                }
            }
        }

        private static void Line(Dxf dxf, string layer, short? color, Point2 a, Point2 b) =>
            dxf.Pair(0, "LINE").Common(layer, color).Point(10, a).Point(11, b);

        private static void Text(Dxf dxf, string layer, short? color, Point2 p, string text, double height, double rotationDeg, TextAlign align)
        {
            dxf.Pair(0, "TEXT").Common(layer, color).Point(10, p).Pair(40, height).Pair(1, text);
            if (rotationDeg != 0)
            {
                dxf.Pair(50, rotationDeg);
            }

            if (align != TextAlign.Left)
            {
                int h = align == TextAlign.Right ? 2 : 1;
                dxf.Pair(72, h).Point(11, p);
                if (align == TextAlign.MiddleCenter)
                {
                    dxf.Pair(73, 2);
                }
            }
        }

        /// <summary>Exploded dimension: two extension lines, the dimension line and its text.</summary>
        private static void Dimension(Dxf dxf, string layer, short? color, Point2 p1, Point2 p2, Point2 through, Point2 u, string text, double textHeight)
        {
            Point2 Foot(Point2 p)
            {
                double t = (p.X - through.X) * u.X + (p.Y - through.Y) * u.Y;
                return new Point2(through.X + u.X * t, through.Y + u.Y * t);
            }

            Point2 f1 = Foot(p1), f2 = Foot(p2);
            Line(dxf, layer, color, p1, f1);
            Line(dxf, layer, color, p2, f2);
            Line(dxf, layer, color, f1, f2);

            // Text centred on the dimension line, lifted off it along the normal.
            var n = new Point2(-u.Y, u.X);
            var mid = new Point2((f1.X + f2.X) / 2 + n.X * textHeight * 0.6, (f1.Y + f2.Y) / 2 + n.Y * textHeight * 0.6);
            double angle = Math.Atan2(u.Y, u.X) * 180 / Math.PI;
            if (angle > 90.0001 || angle <= -90)
            {
                angle += 180; // keep text readable
            }

            Text(dxf, layer, color, mid, text, textHeight, angle, TextAlign.Center);
        }

        private static void DimensionText(Dxf dxf, string layer, short? color, DimensionSpec d, double height)
        {
            (Point2 at, double rotation) = AnnotationGeometry.TextPlace(d, height);
            Text(dxf, layer, color, at, d.TextOverride ?? AnnotationGeometry.MeasureText(d), height, AnnotationGeometry.Readable(rotation), TextAlign.MiddleCenter);
        }

        private static string Fmt(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

        private sealed class Dxf
        {
            private readonly TextWriter _w;

            public Dxf(TextWriter w) => _w = w;

            public Dxf Pair(int code, string value)
            {
                _w.Write(code.ToString(CultureInfo.InvariantCulture).PadLeft(3));
                _w.Write("\r\n");
                _w.Write(value);
                _w.Write("\r\n");
                return this;
            }

            public Dxf Pair(int code, int value) => Pair(code, value.ToString(CultureInfo.InvariantCulture));

            public Dxf Pair(int code, double value) => Pair(code, value.ToString("0.0#########", CultureInfo.InvariantCulture));

            public Dxf Point(int code, Point2 p) => Pair(code, p.X).Pair(code + 10, p.Y).Pair(code + 20, 0.0);

            public Dxf Common(string layer, short? color)
            {
                Pair(8, layer);
                if (color != null)
                {
                    Pair(62, (int)color.Value);
                }

                return this;
            }
        }
    }
}
