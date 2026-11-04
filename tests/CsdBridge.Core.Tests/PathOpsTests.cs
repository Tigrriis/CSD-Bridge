using System;
using System.Collections.Generic;
using System.Linq;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;
using Xunit;

namespace CsdBridge.Core.Tests
{
    public class PathOpsTests
    {
        private static Point2 P(double x, double y) => new Point2(x, y);

        private static Path2 Line(double x1, double y1, double x2, double y2) =>
            new Path2(new List<Segment> { new LineSegment(P(x1, y1), P(x2, y2)) }, false);

        private static void Near(Point2 expected, Point2 actual, double tol = 1e-6) =>
            Assert.True(Segment.Dist(expected, actual) < tol, $"expected {expected}, got {actual}");

        // A straight alignment from (0,0) east for 100 m, then a 100 m radius curve to the left for 50 m.
        private static Path2 Alignment()
        {
            var line = new LineSegment(P(0, 0), P(100, 0));
            var arc = new ArcSegment(P(100, 100), 100, -Math.PI / 2, 0.5);
            return new Path2(new List<Segment> { line, arc }, false);
        }

        [Fact]
        public void FilletTrimsCrossingLines()
        {
            FilletResult r = PathOps.Fillet(Line(0, 0, 15, 0), P(5, 0), Line(12, -3, 12, 10), P(12, 8), 2);

            Near(P(10, 0), r.Arc.Start);
            Near(P(12, 2), r.Arc.End);
            Near(P(10, 2), r.Arc.Center);
            Assert.Equal(Math.PI / 2, r.Arc.Sweep, 9);
            Near(P(0, 0), r.A.Start);
            Near(P(10, 0), r.A.End);
            Near(P(12, 2), r.B.Start);
            Near(P(12, 10), r.B.End);
        }

        [Fact]
        public void FilletExtendsShortLines()
        {
            FilletResult r = PathOps.Fillet(Line(0, 0, 8, 0), P(5, 0), Line(12, 10, 12, 4), P(12, 8), 2);

            Near(P(10, 0), r.A.End);
            Near(P(12, 2), r.B.End); // B runs downwards, so its end is extended to the tangent point
            Near(P(10, 0), r.Arc.Start);
            Near(P(12, 2), r.Arc.End);
        }

        [Fact]
        public void FilletPicksTheRightCorner()
        {
            // Same lines, keep the other side of B: the arc turns the other way.
            FilletResult r = PathOps.Fillet(Line(0, 0, 15, 0), P(5, 0), Line(12, -10, 12, 10), P(12, -8), 2);

            Near(P(10, -2), r.Arc.Center);
            Near(P(12, -10), r.B.Start);
            Near(P(12, -2), r.B.End);
            Assert.True(r.Arc.Sweep < 0);
        }

        [Fact]
        public void FilletLineToArcIsTangent()
        {
            var circle = new Path2(new List<Segment> { new ArcSegment(P(0, 30), 20, -Math.PI, Math.PI) }, false); // lower half, (-20,30)..(20,30)
            FilletResult r = PathOps.Fillet(Line(-40, 0, 40, 0), P(-30, 0), circle, P(-19, 24), 5);

            Assert.Equal(5, r.Arc.Radius, 9);
            Assert.Equal(5, r.Arc.Center.Y, 6);                                   // tangent to the line
            Assert.Equal(25, Segment.Dist(r.Arc.Center, P(0, 30)), 6);            // tangent to the circle (outside)
            Near(r.A.End, r.Arc.Start);
            Assert.True(Segment.Dist(r.Arc.End, r.B.Start) < 1e-6 || Segment.Dist(r.Arc.End, r.B.End) < 1e-6);
        }

        [Fact]
        public void TrimKeepsThePickedSide()
        {
            Path2 r = PathOps.Trim(Line(0, 0, 10, 0), new[] { Line(5, -5, 5, 5) }, P(2, 0));
            Near(P(0, 0), r.Start);
            Near(P(5, 0), r.End);
        }

        [Fact]
        public void ExtendReachesTheBoundary()
        {
            Path2 r = PathOps.Extend(Line(0, 0, 3, 0), atEnd: true, new[] { Line(5, -5, 5, 5) });
            Near(P(5, 0), r.End);

            // Extending an arc around its circle.
            var arc = new Path2(new List<Segment> { new ArcSegment(P(0, 0), 10, 0, Math.PI / 4) }, false);
            Path2 a = PathOps.Extend(arc, atEnd: true, new[] { Line(0, 0, 0, 20) });
            Near(P(0, 10), a.End, 1e-6);
        }

        [Fact]
        public void JoinMakesAClosedOutline()
        {
            Path2 r = PathOps.Join(new[] { Line(0, 0, 10, 0), Line(10, 5, 0, 5), Line(10, 0, 10, 5), Line(0, 0, 0, 5) }, 1e-6);
            Assert.True(r.Closed);
            Assert.Equal(4, r.Segments.Count);
            Assert.Equal(50, r.Area, 9);
        }

        [Fact]
        public void OffsetKeepsArcs()
        {
            Path2 o = PathOps.OffsetPath(Alignment(), 3.5); // to the left
            Assert.Equal(2, o.Segments.Count);
            var arc = Assert.IsType<ArcSegment>(o.Segments[1]);
            Assert.Equal(96.5, arc.Radius, 9);
            Near(P(0, 3.5), o.Start);
            Near(o.Segments[0].End, o.Segments[1].Start);

            PolylineSpec pl = PathOps.ToPolyline(o);
            Assert.NotNull(pl.Bulges);
            Assert.Equal(Math.Tan(0.5 / 4), pl.Bulges![1], 9);
        }

        [Fact]
        public void StationOffsetUsesRightPositive()
        {
            var (ch, off, beyond) = RoadGeometry.StationOffset(Alignment(), P(50, -3), 1000);
            Assert.Equal(1050, ch, 9);
            Assert.Equal(3, off, 9);
            Assert.False(beyond);
            Near(P(50, -3), RoadGeometry.PointAt(Alignment(), 1050, 3, 1000));
        }

        [Fact]
        public void AnalyseOffsetsFindsWideningAndIgnoresChordSag()
        {
            Path2 align = Alignment();

            // A "CSD string": points every 10 m at the true offset (chords in between), with a
            // widening from 3.5 m to 5.5 m right between ch 40 and 60, back by ch 130.
            double Offset(double ch) => ch < 40 ? 3.5 : ch < 60 ? 3.5 + (ch - 40) / 20 * 2 : ch < 110 ? 5.5 : ch < 130 ? 5.5 - (ch - 110) / 20 * 2 : 3.5;
            var pts = new List<Point2>();
            for (double ch = 0; ch <= 150 + 1e-9; ch += 10)
            {
                pts.Add(RoadGeometry.PointAt(align, ch, Offset(ch)));
            }

            Path2 csd = Path2.From(new PolylineSpec { Points = pts })!;
            List<OffsetStretch> s = RoadGeometry.AnalyseOffsets(align, csd, 0, 1, 0.01);

            Assert.Equal(5, s.Count);
            Assert.True(s[0].IsConstant);
            Assert.Equal(3.5, s[0].OffsetStart, 6);
            Assert.Equal(40, s[0].To, 6);
            Assert.False(s[1].IsConstant);
            Assert.Equal(5.5, s[2].OffsetStart, 6);
            Assert.Equal(110, s[2].To, 6);
            Assert.Equal(3.5, s[4].OffsetEnd, 6);
        }

        [Fact]
        public void OffsetByChainageRebuildsTheLine()
        {
            Path2 align = Alignment();
            var stretches = new List<OffsetStretch>
            {
                new OffsetStretch { From = 1000, To = 1040, OffsetStart = 3.5, OffsetEnd = 3.5 },
                new OffsetStretch { From = 1040, To = 1060, OffsetStart = 3.5, OffsetEnd = 5.5 },
                new OffsetStretch { From = 1060, To = 1150, OffsetStart = 5.5, OffsetEnd = 5.5 },
            };

            Path2 r = RoadGeometry.OffsetByChainage(align, 1000, stretches);

            Near(P(0, -3.5), r.Start);
            Near(P(40, -3.5), r.Segments[0].End);
            Near(P(60, -5.5), r.Segments[1].End);

            // The constant stretch on the curve is an exact arc 5.5 m outside the 100 m radius.
            var arc = r.Segments.OfType<ArcSegment>().Single();
            Assert.Equal(105.5, arc.Radius, 9);
            Near(RoadGeometry.PointAt(align, 1150, 5.5, 1000), r.End);
            for (int i = 1; i < r.Segments.Count; i++)
            {
                Near(r.Segments[i - 1].End, r.Segments[i].Start);
            }
        }

        [Fact]
        public void AlignmentFitRebuildsStraightsAndArcsFromChordedPoints()
        {
            // Straight, 30 m radius curve left, short straight, 20 m reverse curve right, straight,
            // at map coordinates, sampled every 2.5 m: tangent points fall between samples.
            var x0 = new Point2(530000, 5270000);
            var segs = new List<Segment> { new LineSegment(x0, new Point2(x0.X + 41.3, x0.Y)) };
            var a1 = new ArcSegment(new Point2(x0.X + 41.3, x0.Y + 30), 30, -Math.PI / 2, 0.9);
            segs.Add(a1);
            double h = -Math.PI / 2 + 0.9;
            var dir = (Math.Cos(h + Math.PI / 2), Math.Sin(h + Math.PI / 2));
            var p2 = new Point2(a1.End.X + dir.Item1 * 7.7, a1.End.Y + dir.Item2 * 7.7);
            segs.Add(new LineSegment(a1.End, p2));
            var c2 = new Point2(p2.X + dir.Item2 * 20, p2.Y - dir.Item1 * 20); // centre to the right
            double s2 = Math.Atan2(p2.Y - c2.Y, p2.X - c2.X);
            var a2 = new ArcSegment(c2, 20, s2, -0.7);
            segs.Add(a2);
            double h2 = s2 - 0.7 - Math.PI / 2;
            segs.Add(new LineSegment(a2.End, new Point2(a2.End.X + Math.Cos(h2) * 33.3, a2.End.Y + Math.Sin(h2) * 33.3)));
            var truth = new Path2(segs, false);

            var pts = new List<Point2>();
            for (double d = 0; d < truth.Length; d += 2.5)
            {
                pts.Add(truth.PointAt(d));
            }

            pts.Add(truth.End);
            AlignmentFit.Result fit = AlignmentFit.Fit(pts);

            Assert.Equal(2, fit.Arcs);
            Assert.Equal(3, fit.Lines);
            Assert.True(fit.MaxDeviation < 0.001, $"deviation {fit.MaxDeviation}");
            Assert.Equal(truth.Length, fit.Path.Length, 3);
            var arcs = fit.Path.Segments.OfType<ArcSegment>().ToList();
            Assert.Equal(30, arcs[0].Radius, 3);
            Assert.Equal(20, arcs[1].Radius, 3);
            Assert.True(arcs[0].Sweep > 0 && arcs[1].Sweep < 0);
            for (int i = 0; i <= 200; i++)
            {
                Point2 q = truth.PointAt(truth.Length * i / 200);
                Assert.True(fit.Path.Closest(q).Distance < 0.002, $"off by {fit.Path.Closest(q).Distance} at {q}");
            }
        }
    }
}
