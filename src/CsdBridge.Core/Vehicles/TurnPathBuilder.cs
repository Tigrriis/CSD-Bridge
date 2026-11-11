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

            /// <summary>The moves in order, in words: "turn left 40°", "6.0 m straight".</summary>
            public List<string> Moves { get; set; } = new List<string>();

            public double Length => Request.Path.Length;
        }

        /// <summary>How a drive leg turns.</summary>
        public enum DriveTurns
        {
            /// <summary>One arc to the destination when its radius allows; otherwise a minimum-radius turn then straight.</summary>
            Arc,

            /// <summary>Always a turn at the chosen radius, then straight to the destination.</summary>
            Corner,
        }

        /// <summary>The most a leg may turn: a U-turn and a little, never a loop.</summary>
        public const double MaxLegTurnDeg = 190;

        /// <summary>
        /// The leg that takes the vehicle from <paramref name="start"/> to <paramref name="target"/>
        /// the way a car can. With <see cref="DriveTurns.Arc"/>: a single arc, tangent to the way
        /// the vehicle faces, when its radius is at least the turn radius; otherwise (and always with
        /// <see cref="DriveTurns.Corner"/>) a turn at the turn radius until it points at the
        /// destination, then straight to it. Forwards when the destination is ahead of the rear
        /// axle, in reverse when it is behind, unless <paramref name="reverse"/> says. Forwards the
        /// front axle centre arrives at the destination; in reverse the rear axle centre. A leg never
        /// turns more than <see cref="MaxLegTurnDeg"/>: a destination inside the turning circle, or
        /// one that would need a loop, gives null, as does one too close to move to.
        /// </summary>
        public static DriveLeg? DriveTo(DesignVehicle v, VehiclePose start, Point2 target, double? outerWheelRadius = null, bool? reverse = null, DriveTurns turns = DriveTurns.Arc)
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
            double dxT = target.X - p0.X, dyT = target.Y - p0.Y;
            double along = dxT * ux + dyT * uy, lateral = dxT * nx + dyT * ny;
            if (Math.Sqrt(dxT * dxT + dyT * dyT) < 0.05)
            {
                return null;
            }

            double maxSweep = MaxLegTurnDeg * Math.PI / 180;

            if (turns == DriveTurns.Arc && along > 1e-9)
            {
                // The one arc tangent to the direction of travel through the destination.
                double d2 = along * along + lateral * lateral;
                if (Math.Abs(lateral) < 1e-9)
                {
                    return Leg(v, start, back, reference, new Path2(new List<Segment> { new LineSegment(p0, target) }, false), left: true, sweep: 0, straight: along);
                }

                double arcRadius = d2 / (2 * Math.Abs(lateral));
                if (arcRadius >= refRadius - 1e-9)
                {
                    double side = Math.Sign(lateral);
                    var centre = new Point2(p0.X + nx * arcRadius * side, p0.Y + ny * arcRadius * side);
                    double startAngle = Math.Atan2(p0.Y - centre.Y, p0.X - centre.X);
                    double sweep = 2 * Math.Atan2(Math.Abs(lateral), along) * side; // the inscribed-angle relation: always under 180 degrees
                    var arc = new ArcSegment(centre, arcRadius, startAngle, sweep);
                    return Leg(v, start, back, reference, new Path2(new List<Segment> { arc }, false), left: back ? side < 0 : side > 0, sweep: Math.Abs(sweep), straight: 0);
                }
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
                    continue; // inside this circle
                }

                double phi = Math.Atan2(dy, dx);
                double beta = Math.Acos(Math.Min(1, refRadius / d));
                double startAngle = Math.Atan2(p0.Y - centre.Y, p0.X - centre.X);
                double exitAngle = side > 0 ? phi - beta : phi + beta;
                double sweep = side > 0 ? Angles.Normalize(exitAngle - startAngle) : -Angles.Normalize(startAngle - exitAngle);
                if (Math.Abs(sweep) > maxSweep)
                {
                    continue; // that would be a loop
                }

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

                DriveLeg leg = Leg(v, start, back, reference, new Path2(segments, false), left: back ? side < 0 : side > 0, sweep: Math.Abs(sweep), straight: straight);
                if (best == null || leg.Length < best.Length)
                {
                    best = leg;
                }
            }

            return best;
        }

        private static DriveLeg Leg(DesignVehicle v, VehiclePose start, bool back, VehiclePoint reference, Path2 path, bool left, double sweep, double straight)
        {
            var leg = new DriveLeg
            {
                Request = new TurnPathRequest { Vehicle = v, Path = path, Reference = reference, Reverse = back, InitialHeading = start.Heading },
                Reverse = back,
                Left = left,
                ArcDeg = sweep * 180 / Math.PI,
                StraightLength = straight,
            };
            if (leg.ArcDeg > 0.5)
            {
                leg.Moves.Add($"turn {(left ? "left" : "right")} {leg.ArcDeg:0}°");
            }

            if (straight > 0.005)
            {
                leg.Moves.Add($"{straight:0.0} m straight");
            }

            return leg;
        }

        /// <summary>
        /// The leg that brings the vehicle to <paramref name="target"/> facing <paramref name="arrivalHeading"/>:
        /// a turn at the turn radius, a straight, and a second turn (either may be left or right, or
        /// vanish). The rear axle follows the path, so the vehicle arrives exactly on the heading.
        /// Forwards the front axle centre arrives at the target, in reverse the rear axle centre;
        /// both are tried and the shorter is used unless <paramref name="reverse"/> says. No turn is
        /// more than <see cref="MaxLegTurnDeg"/>, and two turns the same way together no more than
        /// that, so there are no loops: null when that can't be done in one leg.
        /// </summary>
        public static DriveLeg? DriveToPose(DesignVehicle v, VehiclePose start, Point2 target, double arrivalHeading, double? outerWheelRadius = null, bool? reverse = null)
        {
            double radius = outerWheelRadius ?? v.MinTurnRadius;
            if (radius < v.MinTurnRadius - 1e-9)
            {
                throw new ArgumentException($"The turn radius can't be less than the {v.Name}'s minimum of {v.MinTurnRadius:0.#} m.");
            }

            double rho = v.RearAxleRadius(radius);
            DriveLeg? best = null;
            foreach (bool back in reverse is bool forced ? new[] { forced } : new[] { false, true })
            {
                Point2 goal = back ? target : new Point2(target.X - v.Wheelbase * Math.Cos(arrivalHeading), target.Y - v.Wheelbase * Math.Sin(arrivalHeading));
                double flip = back ? Math.PI : 0;
                DriveLeg? leg = TurnStraightTurn(v, start, start.RearAxle, start.Heading + flip, goal, arrivalHeading + flip, rho, back);
                if (leg != null && (best == null || leg.Length < best.Length - 1e-9))
                {
                    best = leg;
                }
            }

            return best;
        }

        /// <summary>The shortest turn-straight-turn path between two positions and directions of travel, within the turn caps.</summary>
        private static DriveLeg? TurnStraightTurn(DesignVehicle v, VehiclePose start, Point2 p0, double t0, Point2 p1, double t1, double rho, bool back)
        {
            double maxSweep = MaxLegTurnDeg * Math.PI / 180;
            DriveLeg? best = null;
            foreach (double s0 in new[] { 1.0, -1.0 })
            {
                foreach (double s1 in new[] { 1.0, -1.0 })
                {
                    var c0 = new Point2(p0.X - Math.Sin(t0) * rho * s0, p0.Y + Math.Cos(t0) * rho * s0);
                    var c1 = new Point2(p1.X - Math.Sin(t1) * rho * s1, p1.Y + Math.Cos(t1) * rho * s1);
                    double dx = c1.X - c0.X, dy = c1.Y - c0.Y;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    double beta, straight;
                    if (s0 == s1)
                    {
                        beta = dist < 1e-9 ? t1 : Math.Atan2(dy, dx);
                        straight = dist;
                    }
                    else
                    {
                        if (dist < 2 * rho)
                        {
                            continue; // the circles overlap: no crossing tangent
                        }

                        double gamma = Math.Asin(Math.Min(1, 2 * rho / dist));
                        beta = Math.Atan2(dy, dx) + gamma * s0;
                        straight = Math.Sqrt(Math.Max(0, dist * dist - 4 * rho * rho));
                    }

                    double sweep0 = Turn(t0, beta, s0), sweep1 = Turn(beta, t1, s1);
                    if (Math.Abs(sweep0) > maxSweep || Math.Abs(sweep1) > maxSweep || (s0 == s1 && Math.Abs(sweep0) + Math.Abs(sweep1) > maxSweep))
                    {
                        continue; // that would be a loop
                    }

                    var segments = new List<Segment>();
                    Point2 at = p0;
                    if (Math.Abs(sweep0) > 1e-6)
                    {
                        var arc = new ArcSegment(c0, rho, Math.Atan2(p0.Y - c0.Y, p0.X - c0.X), sweep0);
                        segments.Add(arc);
                        at = arc.End;
                    }

                    if (straight > 1e-6)
                    {
                        var to = new Point2(at.X + Math.Cos(beta) * straight, at.Y + Math.Sin(beta) * straight);
                        segments.Add(new LineSegment(at, to));
                        at = to;
                    }

                    if (Math.Abs(sweep1) > 1e-6)
                    {
                        var arc = new ArcSegment(c1, rho, Math.Atan2(at.Y - c1.Y, at.X - c1.X), sweep1);
                        segments.Add(arc);
                        at = arc.End;
                    }

                    if (segments.Count == 0 || Segment.Dist(at, p1) > 1e-4)
                    {
                        continue;
                    }

                    var path = new Path2(segments, false);
                    if (path.Length < 0.05 || (best != null && path.Length >= best.Length))
                    {
                        continue;
                    }

                    // In reverse the driver's left is the right of the direction of travel.
                    bool Left(double side) => back ? side < 0 : side > 0;
                    var leg = new DriveLeg
                    {
                        Request = new TurnPathRequest { Vehicle = v, Path = path, Reference = v.RearAxleCentre, Reverse = back, InitialHeading = start.Heading },
                        Reverse = back,
                        Left = Math.Abs(sweep0) > 1e-6 ? Left(s0) : Left(s1),
                        ArcDeg = (Math.Abs(sweep0) + Math.Abs(sweep1)) * 180 / Math.PI,
                        StraightLength = straight,
                    };
                    if (Math.Abs(sweep0) * 180 / Math.PI > 0.5)
                    {
                        leg.Moves.Add($"turn {(Left(s0) ? "left" : "right")} {Math.Abs(sweep0) * 180 / Math.PI:0}°");
                    }

                    if (straight > 0.005)
                    {
                        leg.Moves.Add($"{straight:0.0} m straight");
                    }

                    if (Math.Abs(sweep1) * 180 / Math.PI > 0.5)
                    {
                        leg.Moves.Add($"turn {(Left(s1) ? "left" : "right")} {Math.Abs(sweep1) * 180 / Math.PI:0}°");
                    }

                    best = leg;
                }
            }

            return best;
        }

        /// <summary>The signed angle turned going from one heading to another, turning left (side +1) or right (-1).</summary>
        private static double Turn(double from, double to, double side)
        {
            double a = side > 0 ? Angles.Normalize(to - from) : Angles.Normalize(from - to);
            if (a > 2 * Math.PI - 1e-7)
            {
                a = 0;
            }

            return a * side;
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
