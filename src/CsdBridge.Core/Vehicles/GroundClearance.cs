using System;
using System.Collections.Generic;
using System.Linq;
using CsdBridge.Core.Civil;
using CsdBridge.Core.Drawing;

namespace CsdBridge.Core.Vehicles
{
    /// <summary>One position of the vehicle on a ground profile and how close its underside came to the ground.</summary>
    public sealed class ClearancePosition
    {
        /// <summary>Chainage (distance along the profile) of the rear wheel contact.</summary>
        public double RearChainage { get; set; }

        /// <summary>Chainage of the front wheel contact.</summary>
        public double FrontChainage { get; set; }

        /// <summary>True when the vehicle faces increasing chainage.</summary>
        public bool Forward { get; set; }

        /// <summary>Smallest vertical distance from the underside template to the ground (m); negative = grounds.</summary>
        public double MinGap { get; set; }

        /// <summary>Chainage where the minimum gap occurs.</summary>
        public double GapChainage { get; set; }

        /// <summary>Ground level at <see cref="GapChainage"/>.</summary>
        public double GapGroundLevel { get; set; }

        /// <summary>rear overhang, between axles, or front overhang.</summary>
        public string Part { get; set; } = "";

        /// <summary>The underside template placed on the profile: (chainage, level) from the rear extremity to the front extremity.</summary>
        public (double Chainage, double Level)[] Underside { get; set; } = Array.Empty<(double, double)>();

        public double RearLevel { get; set; }

        public double FrontLevel { get; set; }
    }

    /// <summary>A stretch of rear-wheel chainages over which the vehicle grounds.</summary>
    public sealed class GroundingRange
    {
        public double From { get; set; }
        public double To { get; set; }
        public bool Forward { get; set; }
        public double WorstGap { get; set; }
        public string Part { get; set; } = "";
    }

    /// <summary>Result of sliding the ground clearance template along a long section (Appendix C).</summary>
    public sealed class GroundClearanceResult
    {
        public DesignVehicle Vehicle { get; set; } = null!;
        public GroundProfile Profile { get; set; } = null!;

        /// <summary>The ground clearance the template was built with (m).</summary>
        public double GroundClearance { get; set; }

        public List<ClearancePosition> Positions { get; } = new List<ClearancePosition>();

        /// <summary>The worst position overall.</summary>
        public ClearancePosition Worst { get; set; } = null!;

        /// <summary>The worst position facing increasing chainage, and facing decreasing chainage (null when not checked).</summary>
        public ClearancePosition? WorstForward { get; set; }

        public ClearancePosition? WorstBackward { get; set; }

        public List<GroundingRange> Groundings { get; } = new List<GroundingRange>();

        /// <summary>
        /// The lowest the underside ever gets at each chainage, over every position and direction:
        /// a (chainage, level) line that must stay above the ground everywhere.
        /// </summary>
        public List<(double Chainage, double Level)> UndersideEnvelope { get; set; } = new List<(double, double)>();

        /// <summary>Smallest underside-to-ground distance anywhere (m); negative = the vehicle grounds.</summary>
        public double MinGap => Worst.MinGap;

        /// <summary>True when the underside never falls below the ground.</summary>
        public bool Passes => MinGap >= -1e-6;

        public List<string> Warnings { get; } = new List<string>();

        public string Summary()
        {
            string where = $"{Worst.Part} at ch {Worst.GapChainage:0.00} (rear wheel at ch {Worst.RearChainage:0.00}, facing {(Worst.Forward ? "up" : "down")} chainage)";
            return Passes
                ? $"{Vehicle.Name} ground clearance OK: the underside stays at least {MinGap * 1000:0} mm above the ground ({where})."
                : $"{Vehicle.Name} GROUNDS by {-MinGap * 1000:0} mm: {where}.";
        }
    }

    /// <summary>One side of the vehicle at one pose along a driven path, checked against a surface.</summary>
    public sealed class PathClearanceSample
    {
        /// <summary>Distance of the reference point along the driven path.</summary>
        public double Chainage { get; set; }

        /// <summary>Distance along this side's rear wheel trace (for plotting).</summary>
        public double WheelPathChainage { get; set; }

        public bool Left { get; set; }
        public double MinGap { get; set; }

        /// <summary>Plan position of the ground sample with the smallest gap.</summary>
        public Point2 GapPoint { get; set; }

        public double GapGroundLevel { get; set; }
        public string Part { get; set; } = "";

        /// <summary>Distance along the side line of the gap point, from the rear axle (for plotting the template).</summary>
        public double GapAlong { get; set; }

        public double RearLevel { get; set; }
        public double FrontLevel { get; set; }

        /// <summary>True when the wheels were off the surface, so this pose couldn't be checked.</summary>
        public bool OffSurface { get; set; }
    }

    /// <summary>One side's results along the path.</summary>
    public sealed class PathClearanceSide
    {
        public bool Left { get; set; }
        public List<PathClearanceSample> Samples { get; } = new List<PathClearanceSample>();

        /// <summary>Ground level along this side's rear wheel trace, by distance along the trace.</summary>
        public GroundProfile? WheelPathProfile { get; set; }

        public PathClearanceSample? Worst { get; set; }

        /// <summary>Reference-path chainage ranges over which this side grounds.</summary>
        public List<(double From, double To, double WorstGap)> Groundings { get; } = new List<(double, double, double)>();

        /// <summary>The lowest the underside gets along this side's wheel path, by distance along the trace.</summary>
        public List<(double Chainage, double Level)> UndersideEnvelope { get; set; } = new List<(double, double)>();

        public int OffSurfaceCount { get; set; }

        public string Name => Left ? "left" : "right";
    }

    /// <summary>Result of checking a driven path against a surface, side by side.</summary>
    public sealed class PathClearanceResult
    {
        public DesignVehicle Vehicle { get; set; } = null!;
        public TurnPathResult Path { get; set; } = null!;
        public double GroundClearance { get; set; }
        public PathClearanceSide LeftSide { get; } = new PathClearanceSide { Left = true };
        public PathClearanceSide RightSide { get; } = new PathClearanceSide { Left = false };

        public IEnumerable<PathClearanceSide> Sides
        {
            get
            {
                yield return LeftSide;
                yield return RightSide;
            }
        }

        public PathClearanceSample? Worst { get; set; }

        public double MinGap => Worst?.MinGap ?? double.PositiveInfinity;

        public bool Passes => Worst == null || Worst.MinGap >= -1e-6;

        public List<string> Warnings { get; } = new List<string>();

        public string Summary()
        {
            if (Worst == null)
            {
                return $"{Vehicle.Name}: no part of the path is on the surface, so nothing could be checked.";
            }

            string where = $"{Worst.Part}, {(Worst.Left ? "left" : "right")} side, {Worst.Chainage:0.00} m along the path";
            return Passes
                ? $"{Vehicle.Name} ground clearance OK: the underside stays at least {MinGap * 1000:0} mm above the surface ({where})."
                : $"{Vehicle.Name} GROUNDS by {-MinGap * 1000:0} mm: {where}.";
        }
    }

    /// <summary>
    /// AS/NZS 2890.1 Appendix C ground clearance check. The template (Figure C1) sits with both
    /// wheel points on the ground profile; its underside, 120 mm above the wheel line between the
    /// axles and rising at the approach/departure angles over the overhangs, must never fall below
    /// the profile. This slides the template along the profile in small steps and reports the
    /// smallest gap, where it is and over which stretch the vehicle grounds.
    /// </summary>
    public static class GroundClearanceChecker
    {
        /// <summary>Spacing of the ground samples under the template (m).</summary>
        private const double SampleStep = 0.05;

        /// <summary>
        /// Slides the template along a long section. Chainage and level in metres, natural scale.
        /// With <paramref name="bothDirections"/> the vehicle is also run facing down chainage, since
        /// the two overhangs differ.
        /// </summary>
        public static GroundClearanceResult CheckProfile(GroundProfile profile, DesignVehicle v, double step = 0.05, bool bothDirections = true, double? groundClearance = null)
        {
            if (step <= 0)
            {
                throw new ArgumentException("The step must be positive.");
            }

            double h = groundClearance ?? v.GroundClearance;
            if (h < 0)
            {
                throw new ArgumentException("The ground clearance can't be negative.");
            }

            var result = new GroundClearanceResult { Vehicle = v, Profile = profile, GroundClearance = h };
            double length = profile.End - profile.Start;
            if (length < v.Wheelbase)
            {
                throw new ArgumentException($"The profile is {length:0.##} m long, shorter than the {v.Name}'s {v.Wheelbase:0.##} m wheelbase.");
            }

            (double X, double Z)[] template = v.UndersideTemplate(h);
            var forward = Slide(profile, v, template, step, forward: true);
            result.Positions.AddRange(forward);
            result.WorstForward = forward.OrderBy(p => p.MinGap).First();
            if (bothDirections)
            {
                // Facing down chainage: run forward on the mirrored profile, then mirror the answers back.
                var mirrored = new GroundProfile(profile.Points.Select(p => new GroundPoint(-p.Chainage, p.Level)));
                var backward = Slide(mirrored, v, template, step, forward: false);
                foreach (ClearancePosition p in backward)
                {
                    p.RearChainage = -p.RearChainage;
                    p.FrontChainage = -p.FrontChainage;
                    p.GapChainage = -p.GapChainage;
                    p.Underside = p.Underside.Select(u => (-u.Chainage, u.Level)).Reverse().ToArray();
                }

                result.Positions.AddRange(backward);
                result.WorstBackward = backward.OrderBy(p => p.MinGap).First();
            }

            result.Worst = result.Positions.OrderBy(p => p.MinGap).First();
            foreach (bool dir in bothDirections ? new[] { true, false } : new[] { true })
            {
                result.Groundings.AddRange(Ranges(result.Positions.Where(p => p.Forward == dir).OrderBy(p => p.RearChainage).ToList()));
            }

            result.UndersideEnvelope = LowerEnvelope(result.Positions.Select(p => p.Underside), profile.Start, profile.End, Math.Min(step, SampleStep));
            return result;
        }

        /// <summary>
        /// The lowest of a set of (chainage, level) lines at every chainage: the undersides over all
        /// positions give the lowest the vehicle ever reaches. Simplified to 1 mm.
        /// </summary>
        public static List<(double Chainage, double Level)> LowerEnvelope(IEnumerable<(double Chainage, double Level)[]> lines, double from, double to, double step)
        {
            int n = Math.Max(2, (int)Math.Round((to - from) / step) + 1);
            double[] low = new double[n];
            for (int i = 0; i < n; i++)
            {
                low[i] = double.PositiveInfinity;
            }

            foreach ((double Chainage, double Level)[] line in lines)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                double a = line[0].Chainage, b = line[line.Length - 1].Chainage;
                int i0 = Math.Max(0, (int)Math.Ceiling((a - from) / step));
                int i1 = Math.Min(n - 1, (int)Math.Floor((b - from) / step));
                for (int i = i0; i <= i1; i++)
                {
                    double x = from + i * step;
                    double z = Interpolate(line, x);
                    if (z < low[i])
                    {
                        low[i] = z;
                    }
                }
            }

            var pts = new List<Point2>();
            for (int i = 0; i < n; i++)
            {
                if (!double.IsInfinity(low[i]))
                {
                    pts.Add(new Point2(from + i * step, low[i]));
                }
            }

            return TurnPathSimulator.Simplify(pts, 0.001).Select(p => (p.X, p.Y)).ToList();
        }

        /// <summary>Template positions with the rear wheel every <paramref name="step"/> along the profile, facing up chainage.</summary>
        private static List<ClearancePosition> Slide(GroundProfile profile, DesignVehicle v, (double X, double Z)[] template, double step, bool forward)
        {
            var positions = new List<ClearancePosition>();
            double first = profile.Start;
            double last = profile.End;
            for (double rear = first; rear <= last - 1e-9; rear += step)
            {
                double rearZ = profile.LevelAt(rear);
                if (!TryFrontWheel(profile, rear, rearZ, v.Wheelbase, out double front))
                {
                    break; // the front wheel would be past the end of the profile
                }

                if (front > last + 1e-9)
                {
                    break;
                }

                positions.Add(Place(profile, template, v, rear, rearZ, front, profile.LevelAt(front), forward));
            }

            if (positions.Count == 0)
            {
                throw new ArgumentException("The vehicle doesn't fit on the profile.");
            }

            return positions;
        }

        /// <summary>Chainage of the front wheel: the point on the profile a wheelbase (straight line) ahead of the rear wheel.</summary>
        private static bool TryFrontWheel(GroundProfile profile, double rear, double rearZ, double wheelbase, out double front)
        {
            double lo = rear, hi = rear + wheelbase;
            double Chord(double d)
            {
                double dz = profile.LevelAt(d) - rearZ;
                return Math.Sqrt((d - rear) * (d - rear) + dz * dz);
            }

            // On a slope the chord to rear + L is longer than L; the root is between. Extend only if needed.
            for (int i = 0; i < 8 && Chord(hi) < wheelbase; i++)
            {
                hi += wheelbase * 0.25;
            }

            if (Chord(hi) < wheelbase)
            {
                front = hi;
                return false;
            }

            for (int i = 0; i < 60; i++)
            {
                double mid = (lo + hi) / 2;
                if (Chord(mid) < wheelbase)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            front = (lo + hi) / 2;
            return front <= profile.End + 1e-9;
        }

        /// <summary>Puts the template on two wheel contacts and measures the gap to the ground under it.</summary>
        private static ClearancePosition Place(GroundProfile profile, (double X, double Z)[] template, DesignVehicle v, double rear, double rearZ, double front, double frontZ, bool forward)
        {
            (double Chainage, double Level)[] underside = Underside(template, rear, rearZ, front, frontZ);
            var pos = new ClearancePosition
            {
                RearChainage = rear,
                FrontChainage = front,
                RearLevel = rearZ,
                FrontLevel = frontZ,
                Forward = forward,
                Underside = underside,
            };

            double min = double.MaxValue, minD = rear, minZ = rearZ;
            double d0 = underside[0].Chainage, d1 = underside[underside.Length - 1].Chainage;
            foreach (double d in SamplePoints(profile, d0, d1))
            {
                double ground = profile.LevelAt(d);
                double gap = Interpolate(underside, d) - ground;
                if (gap < min)
                {
                    min = gap;
                    minD = d;
                    minZ = ground;
                }
            }

            pos.MinGap = min;
            pos.GapChainage = minD;
            pos.GapGroundLevel = minZ;
            pos.Part = minD < Math.Min(rear, front) - 1e-9 ? (forward ? "rear overhang" : "front overhang")
                : minD > Math.Max(rear, front) + 1e-9 ? (forward ? "front overhang" : "rear overhang")
                : "between axles";
            return pos;
        }

        /// <summary>
        /// The template transformed onto the section: the chassis line runs from the rear contact to
        /// the front contact, and the template's Z is measured perpendicular to it, upwards.
        /// </summary>
        public static (double Chainage, double Level)[] Underside((double X, double Z)[] template, double rear, double rearZ, double front, double frontZ)
        {
            double dx = front - rear, dz = frontZ - rearZ;
            double len = Math.Sqrt(dx * dx + dz * dz);
            double ux = dx / len, uz = dz / len;
            double nx = -uz, nz = ux;
            if (nz < 0)
            {
                nx = -nx;
                nz = -nz;
            }

            var pts = template.Select(t => (rear + t.X * ux + t.Z * nx, rearZ + t.X * uz + t.Z * nz)).ToArray();
            return pts[0].Item1 <= pts[pts.Length - 1].Item1 ? pts : pts.Reverse().ToArray();
        }

        private static IEnumerable<double> SamplePoints(GroundProfile profile, double from, double to)
        {
            var set = new SortedSet<double>();
            for (double d = from; d < to; d += SampleStep)
            {
                set.Add(d);
            }

            set.Add(to);
            foreach (double c in profile.ChainagesBetween(from, to))
            {
                set.Add(c); // the ground's own vertices, where the gap can be extreme
            }

            return set;
        }

        /// <summary>Level of a (chainage, level) polyline at a chainage, clamped at the ends.</summary>
        public static double Interpolate((double Chainage, double Level)[] line, double d)
        {
            if (d <= line[0].Chainage)
            {
                return line[0].Level;
            }

            for (int i = 1; i < line.Length; i++)
            {
                if (d <= line[i].Chainage)
                {
                    var a = line[i - 1];
                    var b = line[i];
                    double t = b.Chainage - a.Chainage < 1e-12 ? 0 : (d - a.Chainage) / (b.Chainage - a.Chainage);
                    return a.Level + t * (b.Level - a.Level);
                }
            }

            return line[line.Length - 1].Level;
        }

        private static IEnumerable<GroundingRange> Ranges(List<ClearancePosition> ordered)
        {
            GroundingRange? current = null;
            foreach (ClearancePosition p in ordered)
            {
                if (p.MinGap < -1e-6)
                {
                    if (current == null)
                    {
                        current = new GroundingRange { From = p.RearChainage, To = p.RearChainage, Forward = p.Forward, WorstGap = p.MinGap, Part = p.Part };
                    }
                    else
                    {
                        current.To = p.RearChainage;
                        if (p.MinGap < current.WorstGap)
                        {
                            current.WorstGap = p.MinGap;
                            current.Part = p.Part;
                        }
                    }
                }
                else if (current != null)
                {
                    yield return current;
                    current = null;
                }
            }

            if (current != null)
            {
                yield return current;
            }
        }

        // ------------------------------------------------------------------ 3D

        /// <summary>
        /// Checks a driven path against a surface, each side of the vehicle separately: at every pose
        /// the surface is sampled along the line through that side's wheels, from the rear extremity
        /// to the front extremity, and the template is placed on the two wheel contacts.
        /// </summary>
        /// <param name="sampler">Surface levels for plan points, null where off the surface. Called once with every point.</param>
        /// <param name="poseInterval">Distance between checked poses along the path (m).</param>
        public static PathClearanceResult CheckAlongPath(
            TurnPathResult path,
            Func<IReadOnlyList<Point2>, IReadOnlyList<double?>> sampler,
            double poseInterval = 0.1,
            double? groundClearance = null)
        {
            DesignVehicle v = path.Vehicle;
            double h = groundClearance ?? v.GroundClearance;
            var result = new PathClearanceResult { Vehicle = v, Path = path, GroundClearance = h };
            (double X, double Z)[] template = v.UndersideTemplate(h);

            // Every plan point to sample, laid out so one call does them all.
            var alongs = new List<double>();
            for (double d = -v.RearOverhang; d < v.Wheelbase + v.FrontOverhang - 1e-9; d += SampleStep)
            {
                alongs.Add(d);
            }

            alongs.Add(v.Wheelbase + v.FrontOverhang);
            int rearIndex = alongs.FindIndex(d => Math.Abs(d) < 1e-9);
            if (rearIndex < 0)
            {
                alongs.Add(0);
                alongs.Sort();
                rearIndex = alongs.FindIndex(d => Math.Abs(d) < 1e-9);
            }

            if (!alongs.Any(d => Math.Abs(d - v.Wheelbase) < 1e-9))
            {
                alongs.Add(v.Wheelbase);
                alongs.Sort();
                rearIndex = alongs.FindIndex(d => Math.Abs(d) < 1e-9);
            }

            int frontIndex = alongs.FindIndex(d => Math.Abs(d - v.Wheelbase) < 1e-9);

            var poses = new List<(VehiclePose Pose, double Chainage)>();
            for (double ch = 0; ch <= path.PathLength + 1e-9; ch += poseInterval)
            {
                poses.Add((path.PoseAt(Math.Min(ch, path.PathLength)), Math.Min(ch, path.PathLength)));
            }

            var points = new List<Point2>(poses.Count * alongs.Count * 2);
            foreach (var (pose, _) in poses)
            {
                foreach (double y in new[] { v.Track / 2, -v.Track / 2 })
                {
                    foreach (double d in alongs)
                    {
                        points.Add(pose.ToWorld(d, y));
                    }
                }
            }

            IReadOnlyList<double?> levels = sampler(points);
            if (levels.Count != points.Count)
            {
                throw new InvalidOperationException("The surface sampler returned the wrong number of levels.");
            }

            int index = 0;
            var wheelTrace = new Dictionary<bool, (List<GroundPoint> Profile, Point2? Last, double Along)>
            {
                [true] = (new List<GroundPoint>(), null, 0),
                [false] = (new List<GroundPoint>(), null, 0),
            };

            foreach (var (pose, chainage) in poses)
            {
                foreach (bool left in new[] { true, false })
                {
                    PathClearanceSide side = left ? result.LeftSide : result.RightSide;
                    double y = left ? v.Track / 2 : -v.Track / 2;
                    double?[] z = new double?[alongs.Count];
                    for (int i = 0; i < alongs.Count; i++)
                    {
                        z[i] = levels[index++];
                    }

                    // Rear wheel trace for plotting.
                    var (profile, last, along) = wheelTrace[left];
                    Point2 rearPoint = pose.ToWorld(0, y);
                    if (last is Point2 lp)
                    {
                        along += Math.Sqrt((rearPoint.X - lp.X) * (rearPoint.X - lp.X) + (rearPoint.Y - lp.Y) * (rearPoint.Y - lp.Y));
                    }

                    if (z[rearIndex] is double rz)
                    {
                        profile.Add(new GroundPoint(along, rz));
                    }

                    wheelTrace[left] = (profile, rearPoint, along);

                    var sample = new PathClearanceSample { Chainage = chainage, WheelPathChainage = along, Left = left };
                    if (!(z[rearIndex] is double rearZ) || !(z[frontIndex] is double frontZ))
                    {
                        sample.OffSurface = true;
                        side.OffSurfaceCount++;
                        side.Samples.Add(sample);
                        continue;
                    }

                    (double Chainage, double Level)[] underside = Underside(template, 0, rearZ, v.Wheelbase, frontZ);
                    double min = double.MaxValue;
                    int minI = rearIndex;
                    for (int i = 0; i < alongs.Count; i++)
                    {
                        if (!(z[i] is double g))
                        {
                            continue;
                        }

                        double gap = Interpolate(underside, alongs[i]) - g;
                        if (gap < min)
                        {
                            min = gap;
                            minI = i;
                        }
                    }

                    sample.MinGap = min;
                    sample.GapAlong = alongs[minI];
                    sample.GapPoint = pose.ToWorld(alongs[minI], y);
                    sample.GapGroundLevel = z[minI] ?? 0;
                    sample.RearLevel = rearZ;
                    sample.FrontLevel = frontZ;
                    sample.Part = alongs[minI] < -1e-9 ? "rear overhang" : alongs[minI] > v.Wheelbase + 1e-9 ? "front overhang" : "between axles";
                    side.Samples.Add(sample);
                }
            }

            foreach (PathClearanceSide side in result.Sides)
            {
                var (profile, _, _) = wheelTrace[side.Left];
                if (profile.Count >= 2)
                {
                    side.WheelPathProfile = new GroundProfile(profile);
                }

                side.Worst = side.Samples.Where(s => !s.OffSurface).OrderBy(s => s.MinGap).FirstOrDefault();
                var checkedSamples = side.Samples.Where(s => !s.OffSurface).ToList();
                if (checkedSamples.Count > 0)
                {
                    double from = checkedSamples.Min(s => s.WheelPathChainage) - v.RearOverhang;
                    double to = checkedSamples.Max(s => s.WheelPathChainage) + v.Wheelbase + v.FrontOverhang;
                    side.UndersideEnvelope = LowerEnvelope(
                        checkedSamples.Select(s => Underside(template, s.WheelPathChainage, s.RearLevel, s.WheelPathChainage + v.Wheelbase, s.FrontLevel)),
                        from, to, SampleStep);
                }
                (double From, double To, double Worst)? current = null;
                foreach (PathClearanceSample s in side.Samples)
                {
                    bool grounds = !s.OffSurface && s.MinGap < -1e-6;
                    if (grounds)
                    {
                        current = current == null ? (s.Chainage, s.Chainage, s.MinGap) : (current.Value.From, s.Chainage, Math.Min(current.Value.Worst, s.MinGap));
                    }
                    else if (current != null)
                    {
                        side.Groundings.Add(current.Value);
                        current = null;
                    }
                }

                if (current != null)
                {
                    side.Groundings.Add(current.Value);
                }

                if (side.OffSurfaceCount > 0)
                {
                    result.Warnings.Add($"{side.OffSurfaceCount} of {side.Samples.Count} positions on the {side.Name} side had a wheel off the surface and weren't checked.");
                }
            }

            result.Worst = result.Sides.Select(s => s.Worst).Where(w => w != null).OrderBy(w => w!.MinGap).FirstOrDefault();
            return result;
        }

        /// <summary>
        /// A ground profile from a plotted long section: X is chainage times the horizontal scale,
        /// Y is level times the vertical exaggeration (any datum; only differences matter).
        /// Arcs in the plot are sampled.
        /// </summary>
        public static GroundProfile ProfileFromPlot(Geometry.Path2 plot, double verticalExaggeration, double horizontalScale = 1)
        {
            if (verticalExaggeration <= 0 || horizontalScale <= 0)
            {
                throw new ArgumentException("The vertical exaggeration and horizontal scale must be positive.");
            }

            var pts = new List<GroundPoint>();
            foreach (Geometry.Segment s in plot.Segments)
            {
                int parts = s is Geometry.ArcSegment ? Math.Max(2, (int)Math.Ceiling(s.Length / 0.1)) : 1;
                for (int i = 0; i < parts; i++)
                {
                    Point2 p = s.PointAt(s.Length * i / parts);
                    pts.Add(new GroundPoint(p.X / horizontalScale, p.Y / verticalExaggeration));
                }
            }

            Point2 end = plot.End;
            pts.Add(new GroundPoint(end.X / horizontalScale, end.Y / verticalExaggeration));
            if (pts.Count >= 2 && pts[0].Chainage > pts[pts.Count - 1].Chainage)
            {
                pts.Reverse();
            }

            for (int i = 1; i < pts.Count; i++)
            {
                if (pts[i].Chainage < pts[i - 1].Chainage - 1e-9)
                {
                    throw new ArgumentException("The plotted section doubles back in X; a long section must run one way in chainage.");
                }
            }

            return new GroundProfile(pts);
        }
    }
}
