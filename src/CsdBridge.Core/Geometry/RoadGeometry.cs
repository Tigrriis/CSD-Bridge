using System;
using System.Collections.Generic;
using System.Linq;
using CsdBridge.Core.Drawing;

namespace CsdBridge.Core.Geometry
{
    /// <summary>A stretch of a road line at a constant offset, or a straight transition between two offsets.</summary>
    public sealed class OffsetStretch
    {
        public double From { get; set; }
        public double To { get; set; }
        public double OffsetStart { get; set; }
        public double OffsetEnd { get; set; }

        public bool IsConstant => Math.Abs(OffsetEnd - OffsetStart) < 1e-9;

        /// <summary>Largest gap between this stretch and the sampled line it was fitted to.</summary>
        public double? MaxDeviation { get; set; }
    }

    /// <summary>
    /// Road geometry relative to an alignment. Chainage is distance along the alignment plus
    /// its start chainage; offsets are positive to the right of the direction of travel and
    /// negative to the left (the usual road convention).
    /// </summary>
    public static class RoadGeometry
    {
        /// <summary>Chainage and offset (right positive) of a point relative to an alignment.</summary>
        public static (double Chainage, double Offset, bool Beyond) StationOffset(Path2 alignment, Point2 p, double startChainage = 0)
        {
            var (foot, along, distance) = alignment.Closest(p);
            double heading = alignment.TangentAt(along);
            double cross = Math.Cos(heading) * (p.Y - foot.Y) - Math.Sin(heading) * (p.X - foot.X);
            // Past an end only if the point lies ahead of the end along its direction, not just
            // square to it (a line that starts exactly at the alignment's start is alongside).
            double dx = p.X - foot.X, dy = p.Y - foot.Y;
            double ahead = Math.Cos(heading) * dx + Math.Sin(heading) * dy;
            bool beyond = (along <= 1e-6 && ahead < -1e-6) || (along >= alignment.Length - 1e-6 && ahead > 1e-6);
            return (startChainage + along, cross > 0 ? -distance : distance, beyond);
        }

        /// <summary>The point at a chainage and offset (right positive).</summary>
        public static Point2 PointAt(Path2 alignment, double chainage, double offset, double startChainage = 0)
        {
            double d = chainage - startChainage;
            Point2 on = alignment.PointAt(d);
            double heading = alignment.TangentAt(d);
            return new Point2(on.X + Math.Sin(heading) * offset, on.Y - Math.Cos(heading) * offset);
        }

        /// <summary>
        /// Describe a sampled road line (e.g. a CSD code string) as offsets from the alignment:
        /// stretches of constant offset and the straight transitions between them, fitted within
        /// <paramref name="tolerance"/>. Points beyond the ends of the alignment are ignored.
        /// <paramref name="interval"/> is the sampling step along arcs in the line.
        /// </summary>
        public static List<OffsetStretch> AnalyseOffsets(Path2 alignment, Path2 line, double startChainage, double interval, double tolerance)
        {
            // Sample the line at its vertices, and every 'interval' along any true arcs. Straight
            // pieces between vertices are only chords between CSD's sample sections, so points
            // along them would show the chord sag as false widening.
            var samples = new List<(double Ch, double Off)>();
            var distances = new SortedSet<double>();
            double run = 0;
            foreach (Segment s in line.Segments)
            {
                distances.Add(run);
                if (s is ArcSegment)
                {
                    for (double d = interval; d < s.Length; d += interval)
                    {
                        distances.Add(run + d);
                    }
                }

                run += s.Length;
            }

            distances.Add(run);
            foreach (double d in distances)
            {
                var (ch, off, beyond) = StationOffset(alignment, line.PointAt(d), startChainage);
                if (!beyond)
                {
                    samples.Add((ch, off));
                }
            }

            samples = samples.OrderBy(s => s.Ch).ToList();
            var unique = new List<(double Ch, double Off)>();
            foreach (var s in samples)
            {
                if (unique.Count == 0 || s.Ch - unique[unique.Count - 1].Ch > 1e-6)
                {
                    unique.Add(s);
                }
            }

            if (unique.Count < 2)
            {
                return new List<OffsetStretch>();
            }

            // Simplify offset-along-chainage to as few straight pieces as fit within tolerance.
            var keep = new bool[unique.Count];
            keep[0] = keep[unique.Count - 1] = true;
            Simplify(unique, 0, unique.Count - 1, tolerance, keep);
            List<int> idx = Enumerable.Range(0, unique.Count).Where(i => keep[i]).ToList();

            var stretches = new List<OffsetStretch>();
            for (int k = 0; k < idx.Count - 1; k++)
            {
                var a = unique[idx[k]];
                var b = unique[idx[k + 1]];
                var s = new OffsetStretch { From = a.Ch, To = b.Ch, OffsetStart = a.Off, OffsetEnd = b.Off };
                if (Math.Abs(b.Off - a.Off) <= tolerance)
                {
                    double mean = unique.Skip(idx[k]).Take(idx[k + 1] - idx[k] + 1).Average(u => u.Off);
                    s.OffsetStart = s.OffsetEnd = mean;
                }

                stretches.Add(s);
            }

            // Merge neighbouring constant stretches at (nearly) the same offset.
            var merged = new List<OffsetStretch>();
            foreach (OffsetStretch s in stretches)
            {
                OffsetStretch? last = merged.Count > 0 ? merged[merged.Count - 1] : null;
                if (last != null && last.IsConstant && s.IsConstant && Math.Abs(last.OffsetEnd - s.OffsetStart) <= tolerance)
                {
                    double w1 = last.To - last.From, w2 = s.To - s.From;
                    double mean = (last.OffsetStart * w1 + s.OffsetStart * w2) / (w1 + w2);
                    last.To = s.To;
                    last.OffsetStart = last.OffsetEnd = mean;
                }
                else
                {
                    merged.Add(s);
                }
            }

            // Make transitions start and end at the neighbouring constant offsets, and measure the fit.
            for (int k = 0; k < merged.Count; k++)
            {
                OffsetStretch s = merged[k];
                if (!s.IsConstant)
                {
                    if (k > 0 && merged[k - 1].IsConstant)
                    {
                        s.OffsetStart = merged[k - 1].OffsetEnd;
                    }

                    if (k < merged.Count - 1 && merged[k + 1].IsConstant)
                    {
                        s.OffsetEnd = merged[k + 1].OffsetStart;
                    }
                }

                double dev = 0;
                foreach (var u in unique.Where(u => u.Ch >= s.From - 1e-9 && u.Ch <= s.To + 1e-9))
                {
                    double t = s.To - s.From < 1e-12 ? 0 : (u.Ch - s.From) / (s.To - s.From);
                    dev = Math.Max(dev, Math.Abs(u.Off - (s.OffsetStart + t * (s.OffsetEnd - s.OffsetStart))));
                }

                s.MaxDeviation = dev;
            }

            return merged;
        }

        // Douglas-Peucker on (chainage, offset), measuring the offset error.
        private static void Simplify(List<(double Ch, double Off)> pts, int first, int last, double tol, bool[] keep)
        {
            if (last <= first + 1)
            {
                return;
            }

            var a = pts[first];
            var b = pts[last];
            double worst = 0;
            int at = -1;
            for (int i = first + 1; i < last; i++)
            {
                double t = (pts[i].Ch - a.Ch) / (b.Ch - a.Ch);
                double err = Math.Abs(pts[i].Off - (a.Off + t * (b.Off - a.Off)));
                if (err > worst)
                {
                    worst = err;
                    at = i;
                }
            }

            if (worst > tol && at > 0)
            {
                keep[at] = true;
                Simplify(pts, first, at, tol, keep);
                Simplify(pts, at, last, tol, keep);
            }
        }

        /// <summary>
        /// Build a road line from offset stretches: constant stretches are exact offsets of the
        /// alignment (arcs stay arcs), transitions are straight lines between their end points.
        /// Gaps between stretches are closed with straight lines.
        /// </summary>
        public static Path2 OffsetByChainage(Path2 alignment, double startChainage, IReadOnlyList<OffsetStretch> stretches)
        {
            if (stretches.Count == 0)
            {
                throw new ArgumentException("No stretches given.");
            }

            double length = alignment.Length;
            var segments = new List<Segment>();
            foreach (OffsetStretch s in stretches.OrderBy(s => s.From))
            {
                double d1 = s.From - startChainage, d2 = s.To - startChainage;
                if (d1 < -1e-6 || d2 > length + 1e-6 || d2 - d1 < 1e-6)
                {
                    throw new ArgumentException($"Stretch ch {s.From:0.###} to {s.To:0.###} is outside the alignment (ch {startChainage:0.###} to {startChainage + length:0.###}) or empty.");
                }

                d1 = Math.Max(0, d1);
                d2 = Math.Min(length, d2);
                List<Segment> piece;
                if (s.IsConstant)
                {
                    piece = PathOps.OffsetPath(PathOps.Sub(alignment, d1, d2), -s.OffsetStart).Segments;
                }
                else
                {
                    piece = new List<Segment>
                    {
                        new LineSegment(PointAt(alignment, s.From, s.OffsetStart, startChainage), PointAt(alignment, s.To, s.OffsetEnd, startChainage)),
                    };
                }

                if (segments.Count > 0)
                {
                    Point2 tail = segments[segments.Count - 1].End;
                    if (Segment.Dist(tail, piece[0].Start) > 1e-6)
                    {
                        segments.Add(new LineSegment(tail, piece[0].Start));
                    }
                }

                segments.AddRange(piece);
            }

            return new Path2(segments, false);
        }
    }
}
