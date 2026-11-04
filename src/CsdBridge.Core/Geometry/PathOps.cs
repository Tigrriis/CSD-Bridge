using System;
using System.Collections.Generic;
using System.Linq;
using CsdBridge.Core.Drawing;

namespace CsdBridge.Core.Geometry
{
    /// <summary>The result of a fillet: both curves trimmed (or extended) to the tangent points, and the arc.</summary>
    public sealed class FilletResult
    {
        public FilletResult(Path2 a, Path2 b, ArcSegment arc)
        {
            A = a;
            B = b;
            Arc = arc;
        }

        public Path2 A { get; }
        public Path2 B { get; }
        public ArcSegment Arc { get; }
    }

    /// <summary>
    /// Exact editing operations on paths of lines and arcs: part of a path, split, offset
    /// (arcs stay arcs), extend to a boundary, fillet, join, and conversion to polylines.
    /// </summary>
    public static class PathOps
    {
        public const double Tol = 1e-7;

        // ------------------------------------------------------------------ basics

        public static Segment Reverse(Segment s) =>
            s is LineSegment l ? new LineSegment(l.Q, l.P) : ReverseArc((ArcSegment)s);

        private static Segment ReverseArc(ArcSegment a) => new ArcSegment(a.Center, a.Radius, a.EndAngle, -a.Sweep);

        public static Path2 Reverse(Path2 p) =>
            new Path2(p.Segments.AsEnumerable().Reverse().Select(Reverse).ToList(), p.Closed);

        /// <summary>The part of a segment between two distances along it.</summary>
        public static Segment Part(Segment s, double d1, double d2)
        {
            if (s is LineSegment)
            {
                return new LineSegment(s.PointAt(d1), s.PointAt(d2));
            }

            var a = (ArcSegment)s;
            double sign = Math.Sign(a.Sweep);
            return new ArcSegment(a.Center, a.Radius, a.StartAngle + sign * d1 / a.Radius, sign * (d2 - d1) / a.Radius);
        }

        /// <summary>The part of a path between two distances along it.</summary>
        public static Path2 Sub(Path2 p, double d1, double d2)
        {
            var segments = new List<Segment>();
            double start = 0;
            foreach (Segment s in p.Segments)
            {
                double end = start + s.Length;
                double a = Math.Max(d1, start), b = Math.Min(d2, end);
                if (b - a > Tol)
                {
                    segments.Add(Part(s, a - start, b - start));
                }

                start = end;
            }

            if (segments.Count == 0)
            {
                throw new ArgumentException("That part of the path is empty.");
            }

            return new Path2(segments, false);
        }

        /// <summary>Split a path at distances along it. A closed path splits into pieces that may run through its start.</summary>
        public static List<Path2> SplitAt(Path2 p, IEnumerable<double> distances)
        {
            double length = p.Length;
            List<double> cuts = distances.Where(d => d > Tol && d < length - Tol).OrderBy(d => d).ToList();
            var unique = new List<double>();
            foreach (double c in cuts)
            {
                if (unique.Count == 0 || c - unique[unique.Count - 1] > Tol)
                {
                    unique.Add(c);
                }
            }

            var pieces = new List<Path2>();
            if (!p.Closed)
            {
                double from = 0;
                foreach (double c in unique.Concat(new[] { length }))
                {
                    pieces.Add(Sub(p, from, c));
                    from = c;
                }

                return pieces;
            }

            if (unique.Count == 0)
            {
                return new List<Path2> { p };
            }

            for (int i = 0; i < unique.Count - 1; i++)
            {
                pieces.Add(Sub(p, unique[i], unique[i + 1]));
            }

            // The last piece runs from the last cut through the start to the first cut.
            var wrap = Sub(p, unique[unique.Count - 1], length).Segments.ToList();
            wrap.AddRange(Sub(p, 0, unique[0]).Segments);
            pieces.Add(new Path2(wrap, false));
            return pieces;
        }

        /// <summary>The same segment with its end moved to a point on its line or circle.</summary>
        public static Segment WithEnd(Segment s, Point2 p)
        {
            if (s is LineSegment l)
            {
                return new LineSegment(l.P, p);
            }

            var a = (ArcSegment)s;
            double angle = Math.Atan2(p.Y - a.Center.Y, p.X - a.Center.X);
            return new ArcSegment(a.Center, a.Radius, a.StartAngle, Nearest(angle - a.StartAngle, a.Sweep));
        }

        /// <summary>The same segment with its start moved to a point on its line or circle.</summary>
        public static Segment WithStart(Segment s, Point2 p)
        {
            if (s is LineSegment l)
            {
                return new LineSegment(p, l.Q);
            }

            var a = (ArcSegment)s;
            double angle = Math.Atan2(p.Y - a.Center.Y, p.X - a.Center.X);
            double sweep = Nearest(a.EndAngle - angle, a.Sweep);
            return new ArcSegment(a.Center, a.Radius, a.EndAngle - sweep, sweep);
        }

        /// <summary>The angle equal to <paramref name="angle"/> (mod 2π) closest to <paramref name="target"/>.</summary>
        private static double Nearest(double angle, double target)
        {
            double twoPi = 2 * Math.PI;
            double k = Math.Round((target - angle) / twoPi);
            return angle + k * twoPi;
        }

        // ------------------------------------------------------------------ offset

        /// <summary>Offset a segment to the left of its direction of travel (negative = right). Null if an arc collapses.</summary>
        public static Segment? OffsetSegment(Segment s, double left)
        {
            if (s is LineSegment l)
            {
                double len = l.Length;
                double nx = -(l.Q.Y - l.P.Y) / len, ny = (l.Q.X - l.P.X) / len;
                return new LineSegment(new Point2(l.P.X + nx * left, l.P.Y + ny * left), new Point2(l.Q.X + nx * left, l.Q.Y + ny * left));
            }

            var a = (ArcSegment)s;
            double r = a.Radius - Math.Sign(a.Sweep) * left; // anticlockwise: left is towards the centre
            return r > Tol ? new ArcSegment(a.Center, r, a.StartAngle, a.Sweep) : null;
        }

        /// <summary>
        /// Offset a path to the left (negative = right). Lines stay lines and arcs stay arcs with
        /// the offset radius. Where the offset pieces don't meet (a kink in the path), they are
        /// extended or trimmed to meet, or joined by a line when they can't.
        /// </summary>
        public static Path2 OffsetPath(Path2 p, double left)
        {
            var parts = new List<Segment>();
            for (int i = 0; i < p.Segments.Count; i++)
            {
                Segment o = OffsetSegment(p.Segments[i], left)
                    ?? throw new ArgumentException($"The offset of {Math.Abs(left)} is larger than the radius of an arc in the path.");
                parts.Add(o);
            }

            int joints = p.Closed ? parts.Count : parts.Count - 1;
            Segment[] fixedParts = parts.ToArray();
            var connectors = new Segment?[parts.Count];
            for (int i = 0; i < joints; i++)
            {
                int j = (i + 1) % parts.Count;
                Segment prev = fixedParts[i], next = fixedParts[j];
                if (Segment.Dist(prev.End, next.Start) < 1e-6)
                {
                    continue;
                }

                Point2 vertex = p.Segments[i].End;
                Point2? meet = Intersections.Find(prev, next, extend: true)
                    .OrderBy(q => Segment.Dist(q, vertex))
                    .Cast<Point2?>()
                    .FirstOrDefault(q => Segment.Dist(q!.Value, vertex) < 10 * Math.Abs(left) + 1e-6);
                if (meet is Point2 m)
                {
                    fixedParts[i] = WithEnd(prev, m);
                    fixedParts[j] = WithStart(next, m);
                }
                else
                {
                    connectors[i] = new LineSegment(prev.End, next.Start);
                }
            }

            var segments = new List<Segment>();
            for (int i = 0; i < fixedParts.Length; i++)
            {
                if (fixedParts[i].Length > Tol)
                {
                    segments.Add(fixedParts[i]);
                }

                if (connectors[i] != null)
                {
                    segments.Add(connectors[i]!);
                }
            }

            return new Path2(segments, p.Closed);
        }

        // ------------------------------------------------------------------ trim / extend

        /// <summary>Distances along a path where it crosses any of the boundaries.</summary>
        public static List<double> Crossings(Path2 p, IEnumerable<Path2> boundaries)
        {
            var list = new List<double>();
            foreach (Path2 b in boundaries)
            {
                foreach (Point2 q in Intersections.Find(p, b, extend: false))
                {
                    list.Add(p.Closest(q).Along);
                }
            }

            return list.OrderBy(d => d).ToList();
        }

        /// <summary>Cut a path where it crosses the boundaries and keep the piece nearest to <paramref name="keep"/>.</summary>
        public static Path2 Trim(Path2 p, IEnumerable<Path2> boundaries, Point2 keep)
        {
            List<double> cuts = Crossings(p, boundaries);
            if (cuts.Count == 0)
            {
                throw new ArgumentException("It doesn't cross the boundary.");
            }

            return SplitAt(p, cuts).OrderBy(piece => piece.Closest(keep).Distance).First();
        }

        /// <summary>
        /// Extend the start or end of an open path along its last line or arc to the nearest
        /// boundary ahead. Throws if no boundary is ahead.
        /// </summary>
        public static Path2 Extend(Path2 p, bool atEnd, IEnumerable<Path2> boundaries)
        {
            if (p.Closed)
            {
                throw new ArgumentException("A closed shape can't be extended.");
            }

            Path2 work = atEnd ? p : Reverse(p);
            Segment last = work.Segments[work.Segments.Count - 1];
            double bestAhead = double.MaxValue;
            Point2? best = null;
            foreach (Path2 b in boundaries)
            {
                foreach (Segment bs in b.Segments)
                {
                    foreach (Point2 q in Intersections.Find(last, bs, extend: true))
                    {
                        if (Segment.Dist(bs.Closest(q).Point, q) > 1e-6)
                        {
                            continue; // on the boundary's extension, not the boundary
                        }

                        double ahead = Ahead(last, q);
                        if (ahead > Tol && ahead < bestAhead)
                        {
                            bestAhead = ahead;
                            best = q;
                        }
                    }
                }
            }

            if (best == null)
            {
                throw new ArgumentException("No boundary lies ahead of that end.");
            }

            var segments = work.Segments.Take(work.Segments.Count - 1).ToList();
            segments.Add(WithEnd(last, best.Value));
            var extended = new Path2(segments, false);
            return atEnd ? extended : Reverse(extended);
        }

        /// <summary>How far beyond a segment's end a point on its line/circle lies (negative or NaN if behind).</summary>
        private static double Ahead(Segment s, Point2 q)
        {
            if (s is LineSegment l)
            {
                double len = l.Length;
                double t = ((q.X - l.P.X) * (l.Q.X - l.P.X) + (q.Y - l.P.Y) * (l.Q.Y - l.P.Y)) / len;
                return t - len;
            }

            var a = (ArcSegment)s;
            double angle = Math.Atan2(q.Y - a.Center.Y, q.X - a.Center.X);
            double delta = (angle - a.EndAngle) * Math.Sign(a.Sweep);
            delta %= 2 * Math.PI;
            if (delta < 0)
            {
                delta += 2 * Math.PI;
            }

            return delta < 2 * Math.PI - Math.Abs(a.Sweep) - Tol ? delta * a.Radius : double.NaN;
        }

        // ------------------------------------------------------------------ fillet

        /// <summary>
        /// Fillet two paths with an arc of the given radius. The pick points say which part of
        /// each path to keep (as when picking the lines to fillet in CAD): each path is trimmed or
        /// extended to its tangent point, and the arc runs from A's tangent point to B's.
        /// </summary>
        public static FilletResult Fillet(Path2 a, Point2 pickA, Path2 b, Point2 pickB, double radius)
        {
            if (radius <= Tol)
            {
                throw new ArgumentException("The radius must be positive.");
            }

            int ia = NearestSegment(a, pickA), ib = NearestSegment(b, pickB);
            Segment sa = a.Segments[ia], sb = b.Segments[ib];

            FilletResult? best = null;
            double bestSweep = double.MaxValue;
            foreach (Segment oa in Carriers(sa, radius))
            {
                foreach (Segment ob in Carriers(sb, radius))
                {
                    foreach (Point2 c in Intersections.Find(oa, ob, extend: true))
                    {
                        Point2 ta = Foot(sa, c), tb = Foot(sb, c);
                        (double X, double Y)? dirA = Heading(sa, pickA, ta, towardsPick: false);
                        (double X, double Y)? dirB = Heading(sb, pickB, tb, towardsPick: true);
                        if (dirA == null || dirB == null)
                        {
                            continue;
                        }

                        double cross = dirA.Value.X * (c.Y - ta.Y) - dirA.Value.Y * (c.X - ta.X);
                        int turn = cross > 0 ? 1 : -1;
                        if (!Matches(CircleHeading(c, ta, turn, radius), dirA.Value) || !Matches(CircleHeading(c, tb, turn, radius), dirB.Value))
                        {
                            continue;
                        }

                        double start = Math.Atan2(ta.Y - c.Y, ta.X - c.X);
                        double end = Math.Atan2(tb.Y - c.Y, tb.X - c.X);
                        double sweep = (end - start) * turn;
                        sweep %= 2 * Math.PI;
                        if (sweep <= Tol)
                        {
                            sweep += 2 * Math.PI;
                        }

                        if (sweep >= bestSweep)
                        {
                            continue;
                        }

                        Path2? keptA = CutTo(a, ia, ta, pickA);
                        Path2? keptB = CutTo(b, ib, tb, pickB);
                        if (keptA == null || keptB == null)
                        {
                            continue;
                        }

                        best = new FilletResult(keptA, keptB, new ArcSegment(c, radius, start, turn * sweep));
                        bestSweep = sweep;
                    }
                }
            }

            return best ?? throw new ArgumentException($"These can't be filleted with radius {radius:0.###} on the picked sides (the radius may be too big for the space).");
        }

        private static int NearestSegment(Path2 p, Point2 pick)
        {
            int best = 0;
            double bestD = double.MaxValue;
            for (int i = 0; i < p.Segments.Count; i++)
            {
                double d = Segment.Dist(p.Segments[i].Closest(pick).Point, pick);
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>The segment's line or circle shifted by ±r: where a fillet centre can lie.</summary>
        private static IEnumerable<Segment> Carriers(Segment s, double r)
        {
            if (s is LineSegment)
            {
                yield return OffsetSegment(s, r)!;
                yield return OffsetSegment(s, -r)!;
                yield break;
            }

            var a = (ArcSegment)s;
            yield return new ArcSegment(a.Center, a.Radius + r, 0, 2 * Math.PI);
            if (a.Radius - r > Tol)
            {
                yield return new ArcSegment(a.Center, a.Radius - r, 0, 2 * Math.PI);
            }
        }

        /// <summary>The foot of the perpendicular from a point to the segment's line or circle.</summary>
        private static Point2 Foot(Segment s, Point2 c)
        {
            if (s is LineSegment l)
            {
                double dx = l.Q.X - l.P.X, dy = l.Q.Y - l.P.Y;
                double t = ((c.X - l.P.X) * dx + (c.Y - l.P.Y) * dy) / (dx * dx + dy * dy);
                return new Point2(l.P.X + dx * t, l.P.Y + dy * t);
            }

            var a = (ArcSegment)s;
            double d = Segment.Dist(a.Center, c);
            return new Point2(a.Center.X + (c.X - a.Center.X) * a.Radius / d, a.Center.Y + (c.Y - a.Center.Y) * a.Radius / d);
        }

        /// <summary>
        /// Unit direction along the segment's line/circle at <paramref name="t"/>: away from the pick
        /// (towardsPick false: leaving the kept part into the fillet) or towards it.
        /// </summary>
        private static (double X, double Y)? Heading(Segment s, Point2 pick, Point2 t, bool towardsPick)
        {
            double hx, hy;
            if (s is LineSegment l)
            {
                Point2 pp = Foot(l, pick);
                hx = t.X - pp.X;
                hy = t.Y - pp.Y;
                double len = Math.Sqrt(hx * hx + hy * hy);
                if (len < 1e-9)
                {
                    return null;
                }

                hx /= len;
                hy /= len;
            }
            else
            {
                var a = (ArcSegment)s;
                double angT = Math.Atan2(t.Y - a.Center.Y, t.X - a.Center.X);
                double angP = Math.Atan2(pick.Y - a.Center.Y, pick.X - a.Center.X);
                double delta = Math.IEEERemainder(angT - angP, 2 * Math.PI); // from pick to t, anticlockwise positive
                if (Math.Abs(delta) < 1e-9)
                {
                    return null;
                }

                int ccw = delta > 0 ? 1 : -1;
                double rx = (t.X - a.Center.X) / a.Radius, ry = (t.Y - a.Center.Y) / a.Radius;
                hx = -ry * ccw;
                hy = rx * ccw;
            }

            return towardsPick ? (-hx, -hy) : (hx, hy);
        }

        private static (double X, double Y) CircleHeading(Point2 c, Point2 p, int turn, double r)
        {
            double rx = (p.X - c.X) / r, ry = (p.Y - c.Y) / r;
            return turn > 0 ? (-ry, rx) : (ry, -rx);
        }

        private static bool Matches((double X, double Y) u, (double X, double Y) v) => u.X * v.X + u.Y * v.Y > 1 - 1e-6;

        /// <summary>Trim or extend a path so it ends at <paramref name="t"/> (on segment i's line/circle), keeping the pick side.</summary>
        private static Path2? CutTo(Path2 p, int i, Point2 t, Point2 pick)
        {
            var (point, along, distance) = p.Closest(t);
            if (distance < 1e-6)
            {
                List<Path2> pieces = SplitAt(p, new[] { along });
                Path2 kept = pieces.OrderBy(piece => piece.Closest(pick).Distance).First();

                // The kept piece must end (or start) at the tangent point.
                bool touches = Segment.Dist(kept.Start, t) < 1e-6 || Segment.Dist(kept.End, t) < 1e-6;
                return touches ? kept : null;
            }

            if (p.Closed)
            {
                return null;
            }

            // Beyond the path: extend its first or last segment.
            var segments = p.Segments.ToList();
            if (i == segments.Count - 1 && Ahead(segments[i], t) > 0)
            {
                segments[i] = WithEnd(segments[i], t);
                return new Path2(segments, false);
            }

            if (i == 0 && Ahead(Reverse(segments[0]), t) > 0)
            {
                segments[0] = WithStart(segments[0], t);
                return new Path2(segments, false);
            }

            return null;
        }

        // ------------------------------------------------------------------ join

        /// <summary>Join paths that meet end to end (within tolerance) into one path, reversing pieces as needed.</summary>
        public static Path2 Join(IReadOnlyList<Path2> paths, double tolerance)
        {
            if (paths.Count == 0)
            {
                throw new ArgumentException("Nothing to join.");
            }

            var remaining = paths.Skip(1).ToList();
            var chain = paths[0].Segments.ToList();
            bool grew = true;
            while (remaining.Count > 0 && grew)
            {
                grew = false;
                for (int k = 0; k < remaining.Count; k++)
                {
                    Path2 next = remaining[k];
                    Point2 head = chain[0].Start, tail = chain[chain.Count - 1].End;
                    if (Segment.Dist(tail, next.Start) <= tolerance)
                    {
                        chain.AddRange(next.Segments);
                    }
                    else if (Segment.Dist(tail, next.End) <= tolerance)
                    {
                        chain.AddRange(Reverse(next).Segments);
                    }
                    else if (Segment.Dist(head, next.End) <= tolerance)
                    {
                        chain.InsertRange(0, next.Segments);
                    }
                    else if (Segment.Dist(head, next.Start) <= tolerance)
                    {
                        chain.InsertRange(0, Reverse(next).Segments);
                    }
                    else
                    {
                        continue;
                    }

                    remaining.RemoveAt(k);
                    grew = true;
                    break;
                }
            }

            if (remaining.Count > 0)
            {
                throw new ArgumentException($"{remaining.Count} piece(s) don't connect end to end with the rest (gap larger than {tolerance}).");
            }

            bool closed = chain.Count > 1 && Segment.Dist(chain[0].Start, chain[chain.Count - 1].End) <= tolerance;
            return new Path2(chain, closed);
        }

        // ------------------------------------------------------------------ conversion

        /// <summary>A line, an arc, or a polyline with bulges, as simple as the path allows.</summary>
        public static EntitySpec ToSpec(Path2 p)
        {
            if (!p.Closed && p.Segments.Count == 1)
            {
                Segment s = p.Segments[0];
                if (s is LineSegment l)
                {
                    return new LineSpec { Start = l.P, End = l.Q };
                }

                var a = (ArcSegment)s;
                if (Math.Abs(a.Sweep) < 2 * Math.PI - 1e-9)
                {
                    // Arcs are anticlockwise from start to end angle.
                    double start = a.Sweep > 0 ? a.StartAngle : a.EndAngle;
                    double end = a.Sweep > 0 ? a.EndAngle : a.StartAngle;
                    return new ArcSpec { Center = a.Center, Radius = a.Radius, StartAngleDeg = Deg(start), EndAngleDeg = Deg(end) };
                }
            }

            return ToPolyline(p);
        }

        public static PolylineSpec ToPolyline(Path2 p)
        {
            var points = new List<Point2>();
            var bulges = new List<double>();
            foreach (Segment s in p.Segments)
            {
                if (s is ArcSegment a && Math.Abs(a.Sweep) > Math.PI + 1e-9)
                {
                    // Bulges can't describe more than a half circle well: split big arcs.
                    double half = a.Sweep / 2;
                    foreach (var part in new[] { new ArcSegment(a.Center, a.Radius, a.StartAngle, half), new ArcSegment(a.Center, a.Radius, a.StartAngle + half, half) })
                    {
                        points.Add(part.Start);
                        bulges.Add(Math.Tan(part.Sweep / 4));
                    }

                    continue;
                }

                points.Add(s.Start);
                bulges.Add(s is ArcSegment arc ? Math.Tan(arc.Sweep / 4) : 0);
            }

            if (!p.Closed)
            {
                points.Add(p.End);
                bulges.Add(0);
            }

            bool anyArc = bulges.Any(b => Math.Abs(b) > 1e-12);
            return new PolylineSpec { Points = points, Bulges = anyArc ? bulges : null, Closed = p.Closed };
        }

        private static double Deg(double rad)
        {
            double d = rad * 180 / Math.PI % 360;
            return d < 0 ? d + 360 : d;
        }
    }
}
