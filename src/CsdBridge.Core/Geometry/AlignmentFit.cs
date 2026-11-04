using System;
using System.Collections.Generic;
using System.Linq;
using CsdBridge.Core.Drawing;

namespace CsdBridge.Core.Geometry
{
    /// <summary>
    /// Rebuilds a horizontal alignment (straights and circular arcs) from points that lie on it,
    /// such as the vertices of CSD's centreline string. The straight pieces of the string are
    /// chords, but its vertices are on the true alignment, so each straight and arc is fitted
    /// to the vertices it holds and the tangent points are then worked out exactly.
    /// Transition spirals aren't modelled: the fit reports how far any vertex is off.
    /// </summary>
    public static class AlignmentFit
    {
        public sealed class Result
        {
            public Result(Path2 path, double maxDeviation, int lines, int arcs)
            {
                Path = path;
                MaxDeviation = maxDeviation;
                Lines = lines;
                Arcs = arcs;
            }

            public Path2 Path { get; }

            /// <summary>Largest distance from an input point to the fitted alignment.</summary>
            public double MaxDeviation { get; }

            public int Lines { get; }
            public int Arcs { get; }
        }

        /// <summary>Vertices with a larger radius than this count as straight.</summary>
        private const double StraightRadius = 5000;

        private abstract class Element
        {
            public int First { get; set; }
            public int Last { get; set; }
        }

        private sealed class StraightElement : Element
        {
            public Point2 Origin { get; set; }
            public (double X, double Y) Dir { get; set; }
        }

        private sealed class CircleElement : Element
        {
            public Point2 Center { get; set; }
            public double Radius { get; set; }
        }

        public static Result Fit(IReadOnlyList<Point2> input, double tolerance = 0.005)
        {
            var pts = new List<Point2>();
            foreach (Point2 p in input)
            {
                if (pts.Count == 0 || Segment.Dist(pts[pts.Count - 1], p) > 1e-4)
                {
                    pts.Add(p);
                }
            }

            if (pts.Count < 2)
            {
                throw new ArgumentException("Too few points to fit an alignment.");
            }

            // 1. Group the points by local curvature. The curvature at a vertex comes from it and its
            //    two neighbours: runs of straight (or same-radius) vertices are one element, and a
            //    vertex whose neighbours straddle a tangent point gets an in-between value and is left
            //    out of the fits (the tangent point is worked out exactly in step 2).
            var elements = new List<Element>();
            if (pts.Count < 3)
            {
                elements.Add(FitStraight(pts, 0, pts.Count - 1));
            }
            else
            {
                var runs = new List<(bool Straight, int I0, int I1, double Radius)>();
                for (int i = 1; i < pts.Count - 1; i++)
                {
                    double k = Curvature(pts[i - 1], pts[i], pts[i + 1]);
                    bool straight = Math.Abs(k) < 1.0 / StraightRadius;
                    double radius = straight ? double.PositiveInfinity : 1 / k; // signed
                    if (runs.Count > 0)
                    {
                        var run = runs[runs.Count - 1];
                        bool same = run.I1 == i - 1 && (straight
                            ? run.Straight
                            : !run.Straight && Math.Sign(radius) == Math.Sign(run.Radius) && Math.Abs(Math.Abs(radius) - Math.Abs(run.Radius)) <= 0.05 * Math.Abs(run.Radius));
                        if (same)
                        {
                            int len = i - run.I0;
                            runs[runs.Count - 1] = (run.Straight, run.I0, i, straight ? radius : (run.Radius * len + radius) / (len + 1));
                            continue;
                        }
                    }

                    runs.Add((straight, i, i, radius));
                }

                foreach (var run in runs)
                {
                    // A single curved vertex is a transition between elements, not an arc of its own.
                    if (!run.Straight && run.I1 == run.I0)
                    {
                        continue;
                    }

                    elements.Add(run.Straight ? FitStraight(pts, run.I0 - 1, run.I1 + 1) : (Element)FitCircle(pts, run.I0 - 1, run.I1 + 1));
                }

                if (elements.Count == 0)
                {
                    elements.Add(FitStraight(pts, 0, pts.Count - 1));
                }
            }

            // Neighbouring straights on the same line, or arcs on the same circle, are one element.
            elements = Merge(pts, elements, tolerance);

            // 2. Work out the joins exactly: tangent points, or the intersection of two straights.
            var joins = new List<Point2> { Project(elements[0], pts[0]) };
            for (int i = 0; i < elements.Count - 1; i++)
            {
                Point2 shared = pts[(elements[i].Last + elements[i + 1].First) / 2];
                Point2 join = Join(elements[i], elements[i + 1], shared);

                // A join far from where the elements meet means they aren't tangent (or the fit is
                // poor there): use the shared vertex, which is on the alignment.
                double spacing = Segment.Dist(pts[elements[i].Last], pts[Math.Min(pts.Count - 1, Math.Max(elements[i + 1].First, elements[i].Last + 1))]);
                joins.Add(Segment.Dist(join, shared) <= 2 * spacing + tolerance ? join : shared);
            }

            joins.Add(Project(elements[elements.Count - 1], pts[pts.Count - 1]));

            // 3. Build the path.
            var segments = new List<Segment>();
            for (int i = 0; i < elements.Count; i++)
            {
                Point2 a = joins[i], b = joins[i + 1];
                if (Segment.Dist(a, b) < 1e-6)
                {
                    continue;
                }

                if (elements[i] is CircleElement c)
                {
                    double a0 = Math.Atan2(a.Y - c.Center.Y, a.X - c.Center.X);
                    double a1 = Math.Atan2(b.Y - c.Center.Y, b.X - c.Center.X);

                    // Direction round the circle from the order of its own vertices.
                    Point2 p0 = pts[c.First], p1 = pts[c.First + 1];
                    double turn = (p0.X - c.Center.X) * (p1.Y - c.Center.Y) - (p0.Y - c.Center.Y) * (p1.X - c.Center.X);
                    double sweep = a1 - a0;
                    if (turn > 0)
                    {
                        while (sweep <= 0)
                        {
                            sweep += 2 * Math.PI;
                        }
                    }
                    else
                    {
                        while (sweep >= 0)
                        {
                            sweep -= 2 * Math.PI;
                        }
                    }

                    segments.Add(new ArcSegment(c.Center, c.Radius, a0, sweep));
                }
                else
                {
                    segments.Add(new LineSegment(a, b));
                }
            }

            // Close any small gaps (a join that isn't exactly on an arc) with short lines.
            var continuous = new List<Segment>();
            foreach (Segment seg in segments)
            {
                if (continuous.Count > 0 && Segment.Dist(continuous[continuous.Count - 1].End, seg.Start) > 1e-6)
                {
                    continuous.Add(new LineSegment(continuous[continuous.Count - 1].End, seg.Start));
                }

                continuous.Add(seg);
            }

            segments = continuous;
            var path = new Path2(segments, false);
            double dev = pts.Max(p => path.Closest(p).Distance);
            return new Result(path, dev, segments.Count(x => x is LineSegment), segments.Count(x => x is ArcSegment));
        }

        private static bool FitsLine(List<Point2> pts, int s, int e, double tol)
        {
            Point2 a = pts[s], b = pts[e];
            double len = Segment.Dist(a, b);
            if (len < 1e-9)
            {
                return false;
            }

            for (int i = s + 1; i < e; i++)
            {
                if (Math.Abs(Cross(a, b, pts[i])) / len > tol)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool FitsCircle(List<Point2> pts, int s, int e, double tol)
        {
            if (e - s < 2)
            {
                return false;
            }

            Point2? c = Circumcentre(pts[s], pts[(s + e) / 2], pts[e]);
            if (c == null)
            {
                return false;
            }

            double r = Segment.Dist(c.Value, pts[s]);
            if (r > 1e5)
            {
                return false; // effectively straight
            }

            double turn = Math.Sign(Cross(pts[s], pts[(s + e) / 2], pts[e]));
            for (int i = s; i <= e; i++)
            {
                if (Math.Abs(Segment.Dist(c.Value, pts[i]) - r) > tol)
                {
                    return false;
                }

                // Points must run one way round (no doubling back).
                if (i > s && i < e && Math.Sign(Cross(pts[i - 1], pts[i], pts[i + 1])) != turn && Math.Abs(Cross(pts[i - 1], pts[i], pts[i + 1])) > 1e-9)
                {
                    return false;
                }
            }

            return true;
        }

        private static StraightElement FitStraight(List<Point2> pts, int s, int e)
        {
            // Total least squares through the run's points.
            double mx = 0, my = 0;
            int n = e - s + 1;
            for (int i = s; i <= e; i++)
            {
                mx += pts[i].X;
                my += pts[i].Y;
            }

            mx /= n;
            my /= n;
            double sxx = 0, syy = 0, sxy = 0;
            for (int i = s; i <= e; i++)
            {
                double dx = pts[i].X - mx, dy = pts[i].Y - my;
                sxx += dx * dx;
                syy += dy * dy;
                sxy += dx * dy;
            }

            double angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            var dir = (Math.Cos(angle), Math.Sin(angle));

            // Point it the way the points run.
            if ((pts[e].X - pts[s].X) * dir.Item1 + (pts[e].Y - pts[s].Y) * dir.Item2 < 0)
            {
                dir = (-dir.Item1, -dir.Item2);
            }

            return new StraightElement { First = s, Last = e, Origin = new Point2(mx, my), Dir = dir };
        }

        private static CircleElement FitCircle(List<Point2> pts, int s, int e)
        {
            // Algebraic (Kasa) least-squares circle, refined from the three-point circle.
            int n = e - s + 1;
            double mx = 0, my = 0;
            for (int i = s; i <= e; i++)
            {
                mx += pts[i].X;
                my += pts[i].Y;
            }

            mx /= n;
            my /= n;
            double suu = 0, svv = 0, suv = 0, suuu = 0, svvv = 0, suvv = 0, svuu = 0;
            for (int i = s; i <= e; i++)
            {
                double u = pts[i].X - mx, v = pts[i].Y - my;
                suu += u * u;
                svv += v * v;
                suv += u * v;
                suuu += u * u * u;
                svvv += v * v * v;
                suvv += u * v * v;
                svuu += v * u * u;
            }

            double det = suu * svv - suv * suv;
            Point2 center;
            if (Math.Abs(det) > 1e-12)
            {
                double b1 = 0.5 * (suuu + suvv), b2 = 0.5 * (svvv + svuu);
                double uc = (b1 * svv - b2 * suv) / det, vc = (suu * b2 - suv * b1) / det;
                center = new Point2(mx + uc, my + vc);
            }
            else
            {
                center = Circumcentre(pts[s], pts[(s + e) / 2], pts[e]) ?? new Point2(mx, my);
            }

            double r = 0;
            for (int i = s; i <= e; i++)
            {
                r += Segment.Dist(center, pts[i]);
            }

            return new CircleElement { First = s, Last = e, Center = center, Radius = r / n };
        }

        private static List<Element> Merge(List<Point2> pts, List<Element> elements, double tol)
        {
            var result = new List<Element> { elements[0] };
            for (int i = 1; i < elements.Count; i++)
            {
                Element prev = result[result.Count - 1], cur = elements[i];
                if (prev is StraightElement && cur is StraightElement && FitsLine(pts, prev.First, cur.Last, tol))
                {
                    result[result.Count - 1] = FitStraight(pts, prev.First, cur.Last);
                }
                else if (prev is CircleElement && cur is CircleElement && FitsCircle(pts, prev.First, cur.Last, tol))
                {
                    result[result.Count - 1] = FitCircle(pts, prev.First, cur.Last);
                }
                else
                {
                    result.Add(cur);
                }
            }

            return result;
        }

        /// <summary>Where two elements meet: tangent point (straight/arc, arc/arc) or intersection (two straights).</summary>
        private static Point2 Join(Element a, Element b, Point2 near)
        {
            switch (a)
            {
                case StraightElement la when b is StraightElement lb:
                {
                    double denom = la.Dir.X * lb.Dir.Y - la.Dir.Y * lb.Dir.X;
                    if (Math.Abs(denom) < 1e-12)
                    {
                        return near;
                    }

                    double t = ((lb.Origin.X - la.Origin.X) * lb.Dir.Y - (lb.Origin.Y - la.Origin.Y) * lb.Dir.X) / denom;
                    return new Point2(la.Origin.X + la.Dir.X * t, la.Origin.Y + la.Dir.Y * t);
                }

                case StraightElement l when b is CircleElement c:
                    return Foot(l, c.Center);

                case CircleElement c when b is StraightElement l:
                    return Foot(l, c.Center);

                default:
                {
                    // Two arcs: the tangent point is on the line through both centres.
                    var c1 = (CircleElement)a;
                    var c2 = (CircleElement)b;
                    double dx = c2.Center.X - c1.Center.X, dy = c2.Center.Y - c1.Center.Y, d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < 1e-9)
                    {
                        return near;
                    }

                    var p1 = new Point2(c1.Center.X + dx / d * c1.Radius, c1.Center.Y + dy / d * c1.Radius);
                    var p2 = new Point2(c1.Center.X - dx / d * c1.Radius, c1.Center.Y - dy / d * c1.Radius);
                    return Segment.Dist(p1, near) < Segment.Dist(p2, near) ? p1 : p2;
                }
            }
        }

        private static Point2 Project(Element e, Point2 p) =>
            e is StraightElement l
                ? Foot(l, p)
                : Toward((CircleElement)e, p);

        private static Point2 Foot(StraightElement l, Point2 p)
        {
            double t = (p.X - l.Origin.X) * l.Dir.X + (p.Y - l.Origin.Y) * l.Dir.Y;
            return new Point2(l.Origin.X + l.Dir.X * t, l.Origin.Y + l.Dir.Y * t);
        }

        private static Point2 Toward(CircleElement c, Point2 p)
        {
            double d = Segment.Dist(c.Center, p);
            return new Point2(c.Center.X + (p.X - c.Center.X) / d * c.Radius, c.Center.Y + (p.Y - c.Center.Y) / d * c.Radius);
        }

        /// <summary>Signed curvature (1/radius, positive turning left) of the circle through three points.</summary>
        private static double Curvature(Point2 a, Point2 b, Point2 c)
        {
            double ab = Segment.Dist(a, b), bc = Segment.Dist(b, c), ac = Segment.Dist(a, c);
            return ab * bc * ac < 1e-12 ? 0 : 2 * Cross(a, b, c) / (ab * bc * ac);
        }

        private static double Cross(Point2 a, Point2 b, Point2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        private static Point2? Circumcentre(Point2 a, Point2 b, Point2 c)
        {
            // Relative to a, so map coordinates (millions of metres) keep their precision.
            double bx = b.X - a.X, by = b.Y - a.Y, cx = c.X - a.X, cy = c.Y - a.Y;
            double d = 2 * (bx * cy - by * cx);
            if (Math.Abs(d) < 1e-12)
            {
                return null;
            }

            double b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
            return new Point2(a.X + (cy * b2 - by * c2) / d, a.Y + (bx * c2 - cx * b2) / d);
        }
    }
}
