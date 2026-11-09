using System;
using System.Collections.Generic;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;

namespace CsdBridge.Core.Vehicles
{
    /// <summary>A standard single-turn template, as in AS/NZS 2890.1:2004 Figures B3 to B6.</summary>
    public sealed class TurnTemplate
    {
        public DesignVehicle Vehicle { get; set; } = DesignVehicle.B99;

        /// <summary>Centre of the rear axle at the start (the parked position).</summary>
        public Point2 Start { get; set; }

        /// <summary>Direction of travel at the start, radians anticlockwise from +X.</summary>
        public double Heading { get; set; }

        /// <summary>Turn radius at the outer front wheel (m). Null: the vehicle's minimum.</summary>
        public double? Radius { get; set; }

        /// <summary>Change of direction, degrees (positive).</summary>
        public double TurnAngleDeg { get; set; } = 90;

        public bool Left { get; set; } = true;

        /// <summary>Straight travel before the turn starts (m).</summary>
        public double LeadIn { get; set; }

        /// <summary>Straight travel after the turn (m), which also lets the rear axle settle.</summary>
        public double LeadOut { get; set; } = 6;

        /// <summary>Which point follows the arc. The standard measures the radius at the outer front wheel.</summary>
        public ReferencePoint Reference { get; set; } = ReferencePoint.OuterFrontWheel;
    }

    /// <summary>Builds the paths that <see cref="TurnPathSimulator"/> drives a vehicle along.</summary>
    public static class TurnPathBuilder
    {
        /// <summary>
        /// The request for a single-turn template: the reference point travels straight for the
        /// lead-in, round an arc, then straight for the lead-out. The arc's radius is the
        /// steady-turn radius of the reference point when the outer front wheel is at the
        /// nominated radius.
        /// </summary>
        public static TurnPathRequest Template(TurnTemplate t)
        {
            DesignVehicle v = t.Vehicle;
            double radius = t.Radius ?? v.MinTurnRadius;
            if (radius <= v.Wheelbase)
            {
                throw new ArgumentException($"The turn radius must be larger than the wheelbase ({v.Wheelbase} m).");
            }

            if (t.TurnAngleDeg <= 0 || t.TurnAngleDeg > 360)
            {
                throw new ArgumentException("The turn angle must be between 0 and 360 degrees.");
            }

            VehiclePoint reference = v.Resolve(t.Reference, t.Left);
            double refRadius = v.RadiusOfPoint(radius, reference, t.Left);
            var pose = new VehiclePose(t.Start, t.Heading);
            Point2 p0 = pose.ToWorld(reference);
            double c = Math.Cos(t.Heading), s = Math.Sin(t.Heading);

            var segments = new List<Segment>();
            Point2 p1 = p0;
            if (t.LeadIn > 1e-9)
            {
                p1 = new Point2(p0.X + c * t.LeadIn, p0.Y + s * t.LeadIn);
                segments.Add(new LineSegment(p0, p1));
            }

            double side = t.Left ? 1 : -1;
            var centre = new Point2(p1.X - s * refRadius * side, p1.Y + c * refRadius * side);
            double startAngle = Math.Atan2(p1.Y - centre.Y, p1.X - centre.X);
            double sweep = t.TurnAngleDeg * Math.PI / 180 * side;
            var arc = new ArcSegment(centre, refRadius, startAngle, sweep);
            segments.Add(arc);

            if (t.LeadOut > 1e-9)
            {
                Point2 p2 = arc.End;
                double h = t.Heading + sweep;
                segments.Add(new LineSegment(p2, new Point2(p2.X + Math.Cos(h) * t.LeadOut, p2.Y + Math.Sin(h) * t.LeadOut)));
            }

            return new TurnPathRequest
            {
                Vehicle = v,
                Path = new Path2(segments, false),
                Reference = reference,
                InitialHeading = t.Heading,
            };
        }

        /// <summary>One leg of a drive: how the vehicle gets from a pose to a destination.</summary>
        public sealed class DriveLeg
        {
            public TurnPathRequest Request { get; set; } = null!;
            public bool Reverse { get; set; }

            /// <summary>Turned left (as the driver sees it) on the arc.</summary>
            public bool Left { get; set; }

            /// <summary>Angle turned on the arc, degrees.</summary>
            public double ArcDeg { get; set; }

            public double StraightLength { get; set; }

            public double Length => Request.Path.Length;
        }

        /// <summary>
        /// The leg that takes the vehicle from <paramref name="start"/> to <paramref name="target"/>
        /// the way a car can: a turn at the given radius (never tighter than the vehicle can steer)
        /// until it points at the destination, then straight to it. Forwards when the destination is
        /// ahead of the rear axle, in reverse when it is behind, unless <paramref name="reverse"/> says.
        /// Forwards the front axle centre arrives at the destination; in reverse the rear axle centre.
        /// A destination inside one turning circle is reached by turning the other way, round as far
        /// as it takes. Null when the destination is too close to move to.
        /// </summary>
        public static DriveLeg? DriveTo(DesignVehicle v, VehiclePose start, Point2 target, double? outerWheelRadius = null, bool? reverse = null)
        {
            double radius = outerWheelRadius ?? v.MinTurnRadius;
            if (radius < v.MinTurnRadius - 1e-9)
            {
                throw new ArgumentException($"The turn radius can't be less than the {v.Name}'s minimum of {v.MinTurnRadius:0.#} m.");
            }

            double c = Math.Cos(start.Heading), s = Math.Sin(start.Heading);
            double ahead = (target.X - start.RearAxle.X) * c + (target.Y - start.RearAxle.Y) * s;
            bool back = reverse ?? ahead < v.Wheelbase / 2;

            // The point that drives to the destination, its turning radius, and the direction of travel.
            VehiclePoint reference = back ? v.RearAxleCentre : v.FrontAxleCentre;
            double refRadius = v.RadiusOfPoint(radius, reference, left: true);
            Point2 p0 = start.ToWorld(reference);
            double travel = back ? start.Heading + Math.PI : start.Heading;
            double ux = Math.Cos(travel), uy = Math.Sin(travel);
            double nx = -uy, ny = ux;

            if (Math.Sqrt((target.X - p0.X) * (target.X - p0.X) + (target.Y - p0.Y) * (target.Y - p0.Y)) < 0.05)
            {
                return null;
            }

            DriveLeg? best = null;
            foreach (double side in new[] { 1.0, -1.0 })
            {
                // Turning circle on this side of the direction of travel.
                var centre = new Point2(p0.X + nx * refRadius * side, p0.Y + ny * refRadius * side);
                double dx = target.X - centre.X, dy = target.Y - centre.Y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < refRadius + 1e-9)
                {
                    continue; // inside this circle: go round the other way
                }

                double phi = Math.Atan2(dy, dx);
                double beta = Math.Acos(Math.Min(1, refRadius / d));
                double startAngle = Math.Atan2(p0.Y - centre.Y, p0.X - centre.X);
                double exitAngle = side > 0 ? phi - beta : phi + beta;
                double sweep = side > 0 ? Angles.Normalize(exitAngle - startAngle) : -Angles.Normalize(startAngle - exitAngle);
                double straight = Math.Sqrt(Math.Max(0, d * d - refRadius * refRadius));

                var segments = new List<Segment>();
                if (Math.Abs(sweep) > 1e-6)
                {
                    segments.Add(new ArcSegment(centre, refRadius, startAngle, sweep));
                }

                Point2 exit = segments.Count > 0 ? segments[0].End : p0;
                if (straight > 1e-6)
                {
                    segments.Add(new LineSegment(exit, target));
                }

                if (segments.Count == 0)
                {
                    continue;
                }

                var leg = new DriveLeg
                {
                    Request = new TurnPathRequest
                    {
                        Vehicle = v,
                        Path = new Path2(segments, false),
                        Reference = reference,
                        Reverse = back,
                        InitialHeading = start.Heading,
                    },
                    Reverse = back,
                    // In reverse the driver's left is the right of the direction of travel.
                    Left = back ? side < 0 : side > 0,
                    ArcDeg = Math.Abs(sweep) * 180 / Math.PI,
                    StraightLength = straight,
                };
                if (best == null || leg.Length < best.Length)
                {
                    best = leg;
                }
            }

            return best;
        }

        /// <summary>
        /// A path through points with a tangent arc at each corner. Corners that don't have room
        /// for the radius get a smaller one (reported in <paramref name="warnings"/>).
        /// </summary>
        public static Path2 FromPoints(IReadOnlyList<Point2> points, double cornerRadius, List<string>? warnings = null)
        {
            var pts = new List<Point2>();
            foreach (Point2 p in points)
            {
                if (pts.Count == 0 || Segment.Dist(pts[pts.Count - 1], p) > 1e-6)
                {
                    pts.Add(p);
                }
            }

            if (pts.Count < 2)
            {
                throw new ArgumentException("At least two distinct points are needed.");
            }

            if (cornerRadius < 0)
            {
                throw new ArgumentException("The corner radius can't be negative.");
            }

            var segments = new List<Segment>();
            Point2 cursor = pts[0];
            double usedOnIncoming = 0; // tangent length already taken from the current straight by the previous corner
            for (int i = 1; i < pts.Count - 1; i++)
            {
                Point2 a = pts[i - 1], b = pts[i], c = pts[i + 1];
                double d1x = b.X - a.X, d1y = b.Y - a.Y, l1 = Math.Sqrt(d1x * d1x + d1y * d1y);
                double d2x = c.X - b.X, d2y = c.Y - b.Y, l2 = Math.Sqrt(d2x * d2x + d2y * d2y);
                d1x /= l1;
                d1y /= l1;
                d2x /= l2;
                d2y /= l2;
                double turn = Math.Atan2(d1x * d2y - d1y * d2x, d1x * d2x + d1y * d2y);
                if (Math.Abs(turn) < 0.5 * Math.PI / 180 || cornerRadius < 1e-9)
                {
                    segments.Add(new LineSegment(cursor, b));
                    cursor = b;
                    usedOnIncoming = 0;
                    continue;
                }

                if (Math.PI - Math.Abs(turn) < 0.5 * Math.PI / 180)
                {
                    throw new ArgumentException($"Point {i + 1} doubles straight back on the previous one; a vehicle can't follow that.");
                }

                double radius = cornerRadius;
                double tangent = radius * Math.Tan(Math.Abs(turn) / 2);
                bool last = i == pts.Count - 2;
                double available = Math.Min(l1 - usedOnIncoming, last ? l2 : l2 / 2);
                if (tangent > available + 1e-9)
                {
                    radius = available / Math.Tan(Math.Abs(turn) / 2);
                    tangent = available;
                    warnings?.Add($"Corner at point {i + 1}: not enough room for a {cornerRadius:0.##} m radius; used {radius:0.##} m.");
                }

                var start = new Point2(b.X - d1x * tangent, b.Y - d1y * tangent);
                var end = new Point2(b.X + d2x * tangent, b.Y + d2y * tangent);
                if (Segment.Dist(cursor, start) > 1e-9)
                {
                    segments.Add(new LineSegment(cursor, start));
                }

                double side = Math.Sign(turn);
                var centre = new Point2(start.X - d1y * radius * side, start.Y + d1x * radius * side);
                segments.Add(new ArcSegment(centre, radius, Math.Atan2(start.Y - centre.Y, start.X - centre.X), turn));
                cursor = end;
                usedOnIncoming = tangent;
            }

            Point2 final = pts[pts.Count - 1];
            if (Segment.Dist(cursor, final) > 1e-9)
            {
                segments.Add(new LineSegment(cursor, final));
            }

            return new Path2(segments, false);
        }

        /// <summary>
        /// The path of an existing curve, run from the end nearest <paramref name="nearStart"/>
        /// (when given). Closed curves are opened at their first vertex.
        /// </summary>
        public static Path2 FromCurve(EntitySpec curve, Point2? nearStart)
        {
            Path2? path = Path2.From(curve);
            if (path == null)
            {
                throw new ArgumentException("That entity isn't a line, arc, circle or polyline.");
            }

            if (path.Closed)
            {
                path = new Path2(new List<Segment>(path.Segments), false);
            }

            if (nearStart is Point2 p && Segment.Dist(p, path.End) < Segment.Dist(p, path.Start))
            {
                path = PathOps.Reverse(path);
            }

            return path;
        }
    }
}
