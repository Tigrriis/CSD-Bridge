using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;

namespace CsdBridge.Core.Vehicles
{
    /// <summary>A piece of linework a vehicle must not hit: a kerb, wall, column, bay line.</summary>
    public sealed class Obstruction
    {
        public Obstruction(string handle, string layer, Path2 path)
        {
            Handle = handle;
            Layer = layer;
            Path = path;
        }

        public string Handle { get; }
        public string Layer { get; }
        public Path2 Path { get; }
    }

    /// <summary>Optional ability of a drawing host: find the linework on obstruction layers near a path.</summary>
    public interface IObstructionSource
    {
        /// <summary>
        /// Curves on layers matching any of <paramref name="layerPatterns"/> (see <see cref="LayerPattern"/>)
        /// that reach into the window, leaving out anything on <paramref name="excludeLayers"/>.
        /// </summary>
        IReadOnlyList<Obstruction> Obstructions(IReadOnlyList<string> layerPatterns, Point2 min, Point2 max, IReadOnlyCollection<string> excludeLayers);
    }

    /// <summary>Layer name patterns: case-insensitive, * and ? wildcards; a pattern also matches the part of an xref layer after the last '|'.</summary>
    public static class LayerPattern
    {
        public static bool Matches(string pattern, string layer)
        {
            pattern = pattern.Trim();
            if (pattern.Length == 0)
            {
                return false;
            }

            if (One(pattern, layer))
            {
                return true;
            }

            int bar = layer.LastIndexOf('|');
            return bar >= 0 && pattern.IndexOf('|') < 0 && One(pattern, layer.Substring(bar + 1));
        }

        public static bool MatchesAny(IEnumerable<string> patterns, string layer) => patterns.Any(p => Matches(p, layer));

        /// <summary>Splits "A, B*; C" into patterns.</summary>
        public static List<string> Parse(string? text) =>
            (text ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        private static bool One(string pattern, string text)
        {
            string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    /// <summary>A stretch of an obstruction that the vehicle hits, or that is inside its clearance.</summary>
    public sealed class Conflict
    {
        public string Handle { get; set; } = "";
        public string Layer { get; set; } = "";

        /// <summary>Which leg of the manoeuvre (1-based).</summary>
        public int Leg { get; set; }

        /// <summary>True when the vehicle's body sweeps over the obstruction; false when only the clearance is encroached.</summary>
        public bool BodyHit { get; set; }

        /// <summary>Body hit: how far the obstruction reaches inside the swept path (m).</summary>
        public double Depth { get; set; }

        /// <summary>Clearance encroachment: the smallest gap between the swept path and the obstruction (m).</summary>
        public double Gap { get; set; }

        /// <summary>The clearance required on that side (m).</summary>
        public double Required { get; set; }

        /// <summary>How much clearance is missing (m): Required - Gap, or Required + Depth for a body hit.</summary>
        public double Shortfall => BodyHit ? Required + Depth : Math.Max(0, Required - Gap);

        /// <summary>The worst point.</summary>
        public Point2 At { get; set; }

        /// <summary>The conflicting stretch of the obstruction.</summary>
        public List<Point2> Stretch { get; set; } = new List<Point2>();

        public string Describe() => BodyHit
            ? $"hits {Layer}: {Depth * 1000:0} mm inside the swept path"
            : $"clearance to {Layer}: {Gap * 1000:0} mm (needs {Required * 1000:0})";
    }

    public sealed class ConflictReport
    {
        public List<Conflict> Conflicts { get; } = new List<Conflict>();
        public int ObstructionsChecked { get; set; }

        /// <summary>The closest any obstruction outside the swept path comes to it, alongside the vehicle (m). Null when nothing is near.</summary>
        public double? ClosestGap { get; set; }

        public Point2? ClosestAt { get; set; }

        public bool BodyHits => Conflicts.Any(c => c.BodyHit);

        public bool Passes => Conflicts.Count == 0;

        public string Summary()
        {
            if (ObstructionsChecked == 0)
            {
                return "No linework on the obstruction layers is near the path.";
            }

            if (Passes)
            {
                return ClosestGap is double g
                    ? $"No conflicts: the nearest obstruction is {g * 1000:0} mm from the swept path."
                    : "No conflicts: nothing on the obstruction layers is inside the clearance envelope.";
            }

            int hits = Conflicts.Count(c => c.BodyHit), enc = Conflicts.Count - hits;
            Conflict worst = Conflicts.OrderByDescending(c => c.Shortfall).First();
            string counts = hits > 0 && enc > 0 ? $"{hits} place(s) where the body hits and {enc} clearance encroachment(s)"
                : hits > 0 ? $"the body hits in {hits} place(s)" : $"{enc} clearance encroachment(s)";
            return $"CONFLICT: {counts}. Worst: {worst.Describe()}.";
        }
    }

    /// <summary>
    /// Checks a manoeuvre against obstruction linework. An obstruction point inside the swept path
    /// is a hit; one between the swept path and the clearance line is an encroachment, measured as
    /// the gap left between it and the swept path.
    /// </summary>
    public static class ConflictChecker
    {
        public static ConflictReport Check(IReadOnlyList<TurnPathResult> legs, IReadOnlyList<Obstruction> obstructions, double sample = 0.05)
        {
            var report = new ConflictReport();
            var regions = legs.Select((l, i) => new Region(l, i + 1)).ToList();
            foreach (Obstruction o in obstructions)
            {
                List<Point2> pts = Sample(o.Path, sample);
                bool near = false;
                foreach (Region r in regions)
                {
                    if (!pts.Any(r.NearBox))
                    {
                        continue;
                    }

                    near = true;
                    var runs = new List<Conflict>();
                    Conflict? run = null;
                    bool startsInConflict = false;
                    for (int n = 0; n < pts.Count; n++)
                    {
                        Point2 p = pts[n];
                        if (!r.Classify(p, out bool hit, out double value, out double required))
                        {
                            // Not a conflict: note the closest lateral approach, then close any open stretch.
                            if (r.LateralGap(p) is double gap && (report.ClosestGap == null || gap < report.ClosestGap))
                            {
                                report.ClosestGap = gap;
                                report.ClosestAt = p;
                            }

                            if (run != null)
                            {
                                runs.Add(run);
                                run = null;
                            }

                            continue;
                        }

                        if (run == null)
                        {
                            run = new Conflict { Handle = o.Handle, Layer = o.Layer, Leg = r.Leg, Gap = double.MaxValue, Required = required };
                            startsInConflict |= n == 0;
                        }

                        run.Stretch.Add(p);
                        if (hit)
                        {
                            if (!run.BodyHit || value > run.Depth)
                            {
                                run.BodyHit = true;
                                run.Depth = value;
                                run.Required = required;
                                run.At = p;
                            }
                        }
                        else if (!run.BodyHit && value < run.Gap)
                        {
                            run.Gap = value;
                            run.Required = required;
                            run.At = p;
                        }
                    }

                    if (run != null)
                    {
                        // A closed outline (a column) whose conflict runs through its start point is one stretch, not two.
                        if (o.Path.Closed && startsInConflict && runs.Count > 0)
                        {
                            Conflict first = runs[0];
                            run.Stretch.AddRange(first.Stretch);
                            first.Stretch = run.Stretch;
                            if ((run.BodyHit && (!first.BodyHit || run.Depth > first.Depth)) || (!run.BodyHit && !first.BodyHit && run.Gap < first.Gap))
                            {
                                first.BodyHit = run.BodyHit;
                                first.Depth = run.Depth;
                                first.Gap = run.Gap;
                                first.Required = run.Required;
                                first.At = run.At;
                            }
                        }
                        else
                        {
                            runs.Add(run);
                        }
                    }

                    report.Conflicts.AddRange(runs);
                }

                if (near)
                {
                    report.ObstructionsChecked++;
                }
            }

            foreach (Conflict c in report.Conflicts)
            {
                if (c.BodyHit)
                {
                    c.Gap = 0;
                }

                c.Stretch = TurnPathSimulator.Simplify(c.Stretch, 0.002);
            }

            if (!report.Passes)
            {
                report.ClosestGap = null;
                report.ClosestAt = null;
            }

            return report;
        }

        /// <summary>The window to fetch obstructions for: the manoeuvre's clearance envelope plus a margin.</summary>
        public static (Point2 Min, Point2 Max) Window(IEnumerable<TurnPathResult> legs, double margin = 1.5)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (TurnPathResult r in legs)
            {
                foreach (Point2 p in r.LeftClearance.Concat(r.RightClearance).Concat(r.LeftBody).Concat(r.RightBody))
                {
                    minX = Math.Min(minX, p.X);
                    minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X);
                    maxY = Math.Max(maxY, p.Y);
                }
            }

            return (new Point2(minX - margin, minY - margin), new Point2(maxX + margin, maxY + margin));
        }

        /// <summary>Markers for the conflicts: the offending stretch, a ring at the worst point and a note.</summary>
        public static List<DraftedEntity> Draw(ConflictReport report, string layer, double? textHeight)
        {
            var list = new List<DraftedEntity>();
            foreach (Conflict c in report.Conflicts)
            {
                if (c.Stretch.Count >= 2)
                {
                    list.Add(new DraftedEntity("conflict", new PolylineSpec { Layer = layer, Points = c.Stretch }));
                }

                list.Add(new DraftedEntity("conflict", new CircleSpec { Layer = layer, Center = c.At, Radius = c.BodyHit ? 0.3 : 0.2 }));
                list.Add(new DraftedEntity("conflict", new TextSpec
                {
                    Layer = layer,
                    Position = new Point2(c.At.X + 0.35, c.At.Y + 0.1),
                    Text = c.BodyHit ? $"HIT {c.Depth * 1000:0} mm" : $"{c.Gap * 1000:0} mm (needs {c.Required * 1000:0})",
                    Height = textHeight,
                }));
            }

            return list;
        }

        private static List<Point2> Sample(Path2 path, double step)
        {
            var pts = new List<Point2>();
            foreach (Segment s in path.Segments)
            {
                int parts = Math.Max(1, (int)Math.Ceiling(s.Length / step));
                for (int i = 0; i < parts; i++)
                {
                    pts.Add(s.PointAt(s.Length * i / parts));
                }
            }

            pts.Add(path.Closed ? path.Start : path.End);
            return pts;
        }

        /// <summary>One leg's swept path and clearance envelope, as polygons to test points against.</summary>
        private sealed class Region
        {
            private readonly List<Point2> _left;
            private readonly List<Point2> _right;
            private readonly List<Point2> _body;
            private readonly List<Point2> _clear;
            private readonly double _clearLeft;
            private readonly double _clearRight;
            private readonly double _minX;
            private readonly double _minY;
            private readonly double _maxX;
            private readonly double _maxY;

            public Region(TurnPathResult r, int leg)
            {
                Leg = leg;
                _left = r.LeftBody;
                _right = r.RightBody;
                _clearLeft = r.ClearanceLeft;
                _clearRight = r.ClearanceRight;
                _body = Polygon(r.LeftBody, r.RightBody);
                _clear = Polygon(r.LeftClearance, r.RightClearance);
                _minX = _clear.Concat(_body).Min(p => p.X);
                _minY = _clear.Concat(_body).Min(p => p.Y);
                _maxX = _clear.Concat(_body).Max(p => p.X);
                _maxY = _clear.Concat(_body).Max(p => p.Y);
            }

            public int Leg { get; }

            private static List<Point2> Polygon(List<Point2> left, List<Point2> right)
            {
                var poly = new List<Point2>(left);
                for (int i = right.Count - 1; i >= 0; i--)
                {
                    poly.Add(right[i]);
                }

                return poly;
            }

            /// <summary>Within a couple of metres of the envelope's bounding box.</summary>
            public bool NearBox(Point2 p) => p.X >= _minX - 2 && p.X <= _maxX + 2 && p.Y >= _minY - 2 && p.Y <= _maxY + 2;

            private bool InBox(Point2 p) => p.X >= _minX && p.X <= _maxX && p.Y >= _minY && p.Y <= _maxY;

            /// <summary>
            /// True when the point conflicts. A hit gives the depth inside the swept path; an encroachment
            /// gives the gap left to the swept path. Either way <paramref name="required"/> is that side's clearance.
            /// </summary>
            public bool Classify(Point2 p, out bool hit, out double value, out double required)
            {
                hit = false;
                value = 0;
                required = 0;
                if (!InBox(p))
                {
                    return false;
                }

                bool inBody = Inside(_body, p);
                if (!inBody && !Inside(_clear, p))
                {
                    return false;
                }

                double dl = Distance(_left, p, out _), dr = Distance(_right, p, out _);
                required = dl <= dr ? _clearLeft : _clearRight;
                double d = Math.Min(dl, dr);
                if (inBody)
                {
                    hit = true;
                    value = d;
                    return d > 0.001; // touching the edge of the swept path isn't a hit
                }

                value = d;
                return d < required - 0.001;
            }

            /// <summary>Distance to the swept path for a point beside it (not beyond its ends); null otherwise.</summary>
            public double? LateralGap(Point2 p)
            {
                if (!NearBox(p) || Inside(_body, p))
                {
                    return null;
                }

                double dl = Distance(_left, p, out bool endL), dr = Distance(_right, p, out bool endR);
                double best = double.MaxValue;
                if (!endL)
                {
                    best = dl;
                }

                if (!endR && dr < best)
                {
                    best = dr;
                }

                return best == double.MaxValue ? (double?)null : best;
            }

            private static bool Inside(List<Point2> poly, Point2 p)
            {
                bool inside = false;
                for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                {
                    Point2 a = poly[i], b = poly[j];
                    if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    {
                        inside = !inside;
                    }
                }

                return inside;
            }

            /// <summary>Distance from a point to a polyline; <paramref name="atEnd"/> when the nearest point is one of its two ends.</summary>
            private static double Distance(List<Point2> line, Point2 p, out bool atEnd)
            {
                double best = double.MaxValue;
                atEnd = false;
                for (int i = 0; i < line.Count - 1; i++)
                {
                    Point2 a = line[i], b = line[i + 1];
                    double dx = b.X - a.X, dy = b.Y - a.Y;
                    double len2 = dx * dx + dy * dy;
                    double t = len2 < 1e-18 ? 0 : ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
                    double tc = Math.Max(0, Math.Min(1, t));
                    double ex = p.X - (a.X + dx * tc), ey = p.Y - (a.Y + dy * tc);
                    double d = Math.Sqrt(ex * ex + ey * ey);
                    if (d < best)
                    {
                        best = d;
                        atEnd = (i == 0 && t <= 0) || (i == line.Count - 2 && t >= 1);
                    }
                }

                return best;
            }
        }
    }
}
