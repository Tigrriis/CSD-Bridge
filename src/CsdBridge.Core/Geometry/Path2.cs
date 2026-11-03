using System;
using System.Collections.Generic;
using System.Linq;
using CsdBridge.Core.Drawing;

namespace CsdBridge.Core.Geometry
{
    /// <summary>
    /// Exact 2D curve geometry for the offline (in-memory) host: lines and circular arcs,
    /// chained into paths. In BricsCAD the same tools use BricsCAD's own geometry engine;
    /// this exists so the tools behave the same in the CLI harness and in tests.
    /// </summary>
    public abstract class Segment
    {
        protected const double Eps = 1e-9;

        public abstract Point2 Start { get; }
        public abstract Point2 End { get; }
        public abstract double Length { get; }

        /// <summary>Point at a distance from the start (clamped to the segment).</summary>
        public abstract Point2 PointAt(double distance);

        /// <summary>Direction of travel at a distance from the start, in radians.</summary>
        public abstract double TangentAt(double distance);

        /// <summary>Closest point on the segment and its distance from the segment start.</summary>
        public abstract (Point2 Point, double Along) Closest(Point2 p);

        /// <summary>This segment's term of the closed-path area integral ½∮(x dy − y dx).</summary>
        public abstract double AreaTerm();

        internal static double Dist(Point2 a, Point2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    public sealed class LineSegment : Segment
    {
        public LineSegment(Point2 p, Point2 q)
        {
            P = p;
            Q = q;
        }

        public Point2 P { get; }
        public Point2 Q { get; }
        public override Point2 Start => P;
        public override Point2 End => Q;
        public override double Length => Dist(P, Q);

        public override Point2 PointAt(double distance)
        {
            double len = Length;
            double t = len < Eps ? 0 : Math.Max(0, Math.Min(1, distance / len));
            return new Point2(P.X + (Q.X - P.X) * t, P.Y + (Q.Y - P.Y) * t);
        }

        public override double TangentAt(double distance) => Math.Atan2(Q.Y - P.Y, Q.X - P.X);

        public override (Point2 Point, double Along) Closest(Point2 p)
        {
            double dx = Q.X - P.X, dy = Q.Y - P.Y;
            double len2 = dx * dx + dy * dy;
            double t = len2 < Eps ? 0 : Math.Max(0, Math.Min(1, ((p.X - P.X) * dx + (p.Y - P.Y) * dy) / len2));
            return (new Point2(P.X + dx * t, P.Y + dy * t), t * Math.Sqrt(len2));
        }

        public override double AreaTerm() => 0.5 * (P.X * Q.Y - Q.X * P.Y);
    }

    /// <summary>Circular arc; StartAngle and Sweep in radians, Sweep positive = counter-clockwise.</summary>
    public sealed class ArcSegment : Segment
    {
        public ArcSegment(Point2 center, double radius, double startAngle, double sweep)
        {
            Center = center;
            Radius = radius;
            StartAngle = startAngle;
            Sweep = sweep;
        }

        public Point2 Center { get; }
        public double Radius { get; }
        public double StartAngle { get; }
        public double Sweep { get; }
        public double EndAngle => StartAngle + Sweep;
        public bool IsFullCircle => Math.Abs(Math.Abs(Sweep) - 2 * Math.PI) < 1e-12;
        public override Point2 Start => At(StartAngle);
        public override Point2 End => At(EndAngle);
        public override double Length => Radius * Math.Abs(Sweep);

        public Point2 At(double angle) => new Point2(Center.X + Radius * Math.Cos(angle), Center.Y + Radius * Math.Sin(angle));

        public override Point2 PointAt(double distance)
        {
            double d = Math.Max(0, Math.Min(Length, distance));
            return At(StartAngle + Math.Sign(Sweep) * d / Radius);
        }

        public override double TangentAt(double distance)
        {
            double angle = StartAngle + Math.Sign(Sweep) * Math.Max(0, Math.Min(Length, distance)) / Radius;
            return angle + Math.Sign(Sweep) * Math.PI / 2;
        }

        /// <summary>How far round the arc (radians, 0..|Sweep|) the angle lies, or -1 if outside.</summary>
        public double AngleAlong(double angle)
        {
            double offset = Sweep >= 0 ? angle - StartAngle : StartAngle - angle;
            offset %= 2 * Math.PI;
            if (offset < 0)
            {
                offset += 2 * Math.PI;
            }

            if (offset <= Math.Abs(Sweep) + 1e-12)
            {
                return offset;
            }

            // Within tolerance of the start when wrapping round.
            return 2 * Math.PI - offset < 1e-12 ? 0 : -1;
        }

        public bool Contains(double angle) => AngleAlong(angle) >= 0;

        public override (Point2 Point, double Along) Closest(Point2 p)
        {
            double angle = Math.Atan2(p.Y - Center.Y, p.X - Center.X);
            double along = AngleAlong(angle);
            if (along >= 0)
            {
                return (At(angle), along * Radius);
            }

            return Dist(p, Start) <= Dist(p, End) ? (Start, 0) : (End, Length);
        }

        public override double AreaTerm()
        {
            Point2 s = Start, e = End;
            return 0.5 * (Radius * Radius * Sweep + Center.X * (e.Y - s.Y) - Center.Y * (e.X - s.X));
        }

        /// <summary>Arc for a polyline segment p→q with the given bulge (tan of a quarter of the sweep).</summary>
        public static ArcSegment FromBulge(Point2 p, Point2 q, double bulge)
        {
            double sweep = 4 * Math.Atan(bulge);
            double cx = q.X - p.X, cy = q.Y - p.Y;
            double chord = Math.Sqrt(cx * cx + cy * cy);
            double h = chord * (1 - bulge * bulge) / (4 * bulge);
            var center = new Point2((p.X + q.X) / 2 - cy / chord * h, (p.Y + q.Y) / 2 + cx / chord * h);
            double radius = Dist(p, center);
            return new ArcSegment(center, radius, Math.Atan2(p.Y - center.Y, p.X - center.X), sweep);
        }
    }

    /// <summary>A chain of segments: what a line, polyline, arc or circle is, geometrically.</summary>
    public sealed class Path2
    {
        public Path2(List<Segment> segments, bool closed)
        {
            Segments = segments;
            Closed = closed;
        }

        public List<Segment> Segments { get; }
        public bool Closed { get; }
        public double Length => Segments.Sum(s => s.Length);
        public Point2 Start => Segments[0].Start;
        public Point2 End => Segments[Segments.Count - 1].End;

        /// <summary>Enclosed area (closed paths only).</summary>
        public double Area => Math.Abs(Segments.Sum(s => s.AreaTerm()));

        public Point2 PointAt(double distance) => Locate(distance, (s, d) => s.PointAt(d));

        public double TangentAt(double distance) => Locate(distance, (s, d) => s.TangentAt(d));

        public (Point2 Point, double Along, double Distance) Closest(Point2 p)
        {
            double best = double.MaxValue, start = 0, bestAlong = 0;
            Point2 bestPoint = Start;
            foreach (Segment s in Segments)
            {
                var (point, along) = s.Closest(p);
                double d = Segment.Dist(p, point);
                if (d < best - 1e-12)
                {
                    best = d;
                    bestPoint = point;
                    bestAlong = start + along;
                }

                start += s.Length;
            }

            return (bestPoint, bestAlong, best);
        }

        private T Locate<T>(double distance, Func<Segment, double, T> at)
        {
            double remaining = Math.Max(0, distance);
            for (int i = 0; i < Segments.Count; i++)
            {
                Segment s = Segments[i];
                if (remaining <= s.Length + 1e-9 || i == Segments.Count - 1)
                {
                    return at(s, remaining);
                }

                remaining -= s.Length;
            }

            return at(Segments[Segments.Count - 1], remaining);
        }

        /// <summary>The path of a curve-like entity, or null for text, dimensions and blocks.</summary>
        public static Path2? From(EntitySpec spec)
        {
            switch (spec)
            {
                case LineSpec l:
                    return new Path2(new List<Segment> { new LineSegment(l.Start, l.End) }, false);

                case CircleSpec c:
                    return new Path2(new List<Segment> { new ArcSegment(c.Center, c.Radius, 0, 2 * Math.PI) }, true);

                case ArcSpec a:
                {
                    double start = a.StartAngleDeg * Math.PI / 180, end = a.EndAngleDeg * Math.PI / 180;
                    double sweep = (end - start) % (2 * Math.PI);
                    if (sweep <= 1e-12)
                    {
                        sweep += 2 * Math.PI;
                    }

                    return new Path2(new List<Segment> { new ArcSegment(a.Center, a.Radius, start, sweep) }, false);
                }

                case PolylineSpec p:
                {
                    var segments = new List<Segment>();
                    int n = p.Points.Count;
                    int count = p.Closed ? n : n - 1;
                    for (int i = 0; i < count; i++)
                    {
                        Point2 a = p.Points[i], b = p.Points[(i + 1) % n];
                        if (Segment.Dist(a, b) < 1e-12)
                        {
                            continue;
                        }

                        double bulge = p.Bulges != null ? p.Bulges[i] : 0;
                        segments.Add(Math.Abs(bulge) < 1e-12 ? new LineSegment(a, b) : (Segment)ArcSegment.FromBulge(a, b, bulge));
                    }

                    return segments.Count == 0 ? null : new Path2(segments, p.Closed);
                }

                default:
                    return null;
            }
        }
    }

    public static class Intersections
    {
        private const double Tol = 1e-9;

        /// <summary>Intersection points of two paths. With <paramref name="extend"/>, lines are
        /// treated as infinite and arcs as full circles (like extending both entities).</summary>
        public static List<Point2> Find(Path2 a, Path2 b, bool extend)
        {
            var points = new List<Point2>();
            foreach (Segment sa in a.Segments)
            {
                foreach (Segment sb in b.Segments)
                {
                    foreach (Point2 p in Find(sa, sb, extend))
                    {
                        if (!points.Any(q => Segment.Dist(p, q) < 1e-6))
                        {
                            points.Add(p);
                        }
                    }
                }
            }

            return points;
        }

        public static IEnumerable<Point2> Find(Segment a, Segment b, bool extend)
        {
            if (a is LineSegment la && b is LineSegment lb)
            {
                return LineLine(la, lb, extend);
            }

            if (a is LineSegment l1 && b is ArcSegment c1)
            {
                return LineArc(l1, c1, extend);
            }

            if (a is ArcSegment c2 && b is LineSegment l2)
            {
                return LineArc(l2, c2, extend);
            }

            return ArcArc((ArcSegment)a, (ArcSegment)b, extend);
        }

        private static IEnumerable<Point2> LineLine(LineSegment a, LineSegment b, bool extend)
        {
            double d1x = a.Q.X - a.P.X, d1y = a.Q.Y - a.P.Y;
            double d2x = b.Q.X - b.P.X, d2y = b.Q.Y - b.P.Y;
            double denom = d1x * d2y - d1y * d2x;
            if (Math.Abs(denom) < 1e-12 * Math.Max(1, Math.Sqrt((d1x * d1x + d1y * d1y) * (d2x * d2x + d2y * d2y))))
            {
                yield break; // parallel (overlaps are not reported)
            }

            double ex = b.P.X - a.P.X, ey = b.P.Y - a.P.Y;
            double t = (ex * d2y - ey * d2x) / denom;
            double u = (ex * d1y - ey * d1x) / denom;
            if (extend || (t >= -Tol && t <= 1 + Tol && u >= -Tol && u <= 1 + Tol))
            {
                yield return new Point2(a.P.X + d1x * t, a.P.Y + d1y * t);
            }
        }

        private static IEnumerable<Point2> LineArc(LineSegment l, ArcSegment c, bool extend)
        {
            double dx = l.Q.X - l.P.X, dy = l.Q.Y - l.P.Y;
            double fx = l.P.X - c.Center.X, fy = l.P.Y - c.Center.Y;
            double qa = dx * dx + dy * dy, qb = 2 * (fx * dx + fy * dy), qc = fx * fx + fy * fy - c.Radius * c.Radius;
            double disc = qb * qb - 4 * qa * qc;
            if (qa < 1e-24 || disc < -1e-9 * qa * c.Radius * c.Radius)
            {
                yield break;
            }

            double root = Math.Sqrt(Math.Max(0, disc));
            var ts = root < 1e-12 ? new[] { -qb / (2 * qa) } : new[] { (-qb - root) / (2 * qa), (-qb + root) / (2 * qa) };
            foreach (double t in ts)
            {
                var p = new Point2(l.P.X + dx * t, l.P.Y + dy * t);
                bool onLine = extend || (t >= -Tol && t <= 1 + Tol);
                bool onArc = extend || c.Contains(Math.Atan2(p.Y - c.Center.Y, p.X - c.Center.X));
                if (onLine && onArc)
                {
                    yield return p;
                }
            }
        }

        private static IEnumerable<Point2> ArcArc(ArcSegment a, ArcSegment b, bool extend)
        {
            double dx = b.Center.X - a.Center.X, dy = b.Center.Y - a.Center.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1e-12 || d > a.Radius + b.Radius + 1e-9 || d < Math.Abs(a.Radius - b.Radius) - 1e-9)
            {
                yield break;
            }

            double along = (a.Radius * a.Radius - b.Radius * b.Radius + d * d) / (2 * d);
            double h = Math.Sqrt(Math.Max(0, a.Radius * a.Radius - along * along));
            double mx = a.Center.X + along * dx / d, my = a.Center.Y + along * dy / d;
            var candidates = h < 1e-12
                ? new[] { new Point2(mx, my) }
                : new[] { new Point2(mx - h * dy / d, my + h * dx / d), new Point2(mx + h * dy / d, my - h * dx / d) };
            foreach (Point2 p in candidates)
            {
                if (extend || (a.Contains(Math.Atan2(p.Y - a.Center.Y, p.X - a.Center.X)) && b.Contains(Math.Atan2(p.Y - b.Center.Y, p.X - b.Center.X))))
                {
                    yield return p;
                }
            }
        }
    }

    /// <summary>Exact offsets for lines, circles, arcs and straight-sided polylines.</summary>
    public static class Offsets
    {
        /// <summary>
        /// Offset to the left of the direction of travel (positive distance) or to the right
        /// (negative). Returns null when the shape collapses (offset too large inward).
        /// Throws NotSupportedException for polylines with arc segments.
        /// </summary>
        public static EntitySpec? Offset(EntitySpec spec, double signedDistance)
        {
            double d = signedDistance;
            switch (spec)
            {
                case LineSpec l:
                {
                    var (nx, ny) = LeftNormal(l.Start, l.End);
                    return new LineSpec { Start = Shift(l.Start, nx, ny, d), End = Shift(l.End, nx, ny, d) };
                }

                case CircleSpec c:
                {
                    // Travelling counter-clockwise, left is towards the centre.
                    double r = c.Radius - d;
                    return r > 1e-9 ? new CircleSpec { Center = c.Center, Radius = r } : null;
                }

                case ArcSpec a:
                {
                    double r = a.Radius - d;
                    return r > 1e-9 ? new ArcSpec { Center = a.Center, Radius = r, StartAngleDeg = a.StartAngleDeg, EndAngleDeg = a.EndAngleDeg } : null;
                }

                case PolylineSpec p:
                    if (p.Bulges != null && p.Bulges.Any(b => Math.Abs(b) > 1e-12))
                    {
                        throw new NotSupportedException("offsetting polylines with arc segments isn't supported in the offline preview (it is in BricsCAD)");
                    }

                    return OffsetPolyline(p, d);

                default:
                    return null;
            }
        }

        private static PolylineSpec? OffsetPolyline(PolylineSpec p, double d)
        {
            // Drop repeated points (and a closing point that repeats the first).
            var pts = new List<Point2>();
            foreach (Point2 pt in p.Points)
            {
                if (pts.Count == 0 || Segment.Dist(pts[pts.Count - 1], pt) > 1e-9)
                {
                    pts.Add(pt);
                }
            }

            if (p.Closed && pts.Count > 2 && Segment.Dist(pts[0], pts[pts.Count - 1]) < 1e-9)
            {
                pts.RemoveAt(pts.Count - 1);
            }

            int n = pts.Count;
            if (n < 2)
            {
                return null;
            }

            int segCount = p.Closed ? n : n - 1;
            var origins = new Point2[segCount];
            var dirs = new (double X, double Y)[segCount];
            for (int i = 0; i < segCount; i++)
            {
                Point2 a = pts[i], b = pts[(i + 1) % n];
                double len = Segment.Dist(a, b);
                dirs[i] = ((b.X - a.X) / len, (b.Y - a.Y) / len);
                origins[i] = new Point2(a.X - dirs[i].Y * d, a.Y + dirs[i].X * d);
            }

            var result = new List<Point2>(n);
            for (int i = 0; i < n; i++)
            {
                if (!p.Closed && i == 0)
                {
                    result.Add(origins[0]);
                }
                else if (!p.Closed && i == n - 1)
                {
                    var last = dirs[segCount - 1];
                    result.Add(new Point2(pts[i].X - last.Y * d, pts[i].Y + last.X * d));
                }
                else
                {
                    int prev = (i - 1 + segCount) % segCount, next = i % segCount;
                    result.Add(Meet(origins[prev], dirs[prev], origins[next], dirs[next]));
                }
            }

            // Inward offsets that are too large turn edges round: reject those.
            for (int i = 0; i < segCount; i++)
            {
                Point2 a = result[i], b = result[(i + 1) % n];
                if ((b.X - a.X) * dirs[i].X + (b.Y - a.Y) * dirs[i].Y <= 1e-9)
                {
                    return null;
                }
            }

            return new PolylineSpec { Points = result, Closed = p.Closed };
        }

        /// <summary>Where two offset edges meet (mitred corner); parallel edges just join.</summary>
        private static Point2 Meet(Point2 a, (double X, double Y) da, Point2 b, (double X, double Y) db)
        {
            double denom = da.X * db.Y - da.Y * db.X;
            if (Math.Abs(denom) < 1e-12)
            {
                return b;
            }

            double t = ((b.X - a.X) * db.Y - (b.Y - a.Y) * db.X) / denom;
            return new Point2(a.X + da.X * t, a.Y + da.Y * t);
        }

        private static (double X, double Y) LeftNormal(Point2 a, Point2 b)
        {
            double len = Segment.Dist(a, b);
            return (-(b.Y - a.Y) / len, (b.X - a.X) / len);
        }

        private static Point2 Shift(Point2 p, double nx, double ny, double d) => new Point2(p.X + nx * d, p.Y + ny * d);
    }
}
