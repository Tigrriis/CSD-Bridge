using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;

namespace CsdBridge.Core.Vehicles
{
    /// <summary>What to solve: get a vehicle from one pose to another between obstructions.</summary>
    public sealed class SolveRequest
    {
        public DesignVehicle Vehicle { get; set; } = DesignVehicle.B99;

        /// <summary>Rear axle centre and heading at the start and at the end.</summary>
        public VehiclePose Start { get; set; }

        public VehiclePose Goal { get; set; }

        /// <summary>Turn radius at the outer front wheel (m). Null: the vehicle's minimum.</summary>
        public double? Radius { get; set; }

        /// <summary>Clearance kept between the body and every obstruction (m): the manoeuvring clearance.</summary>
        public double Clearance { get; set; } = 0.3;

        public IReadOnlyList<Obstruction> Obstructions { get; set; } = new List<Obstruction>();

        /// <summary>The area the rear axle may move in.</summary>
        public Point2 Min { get; set; }

        public Point2 Max { get; set; }

        /// <summary>How many positions the search may examine, and for how long (seconds).</summary>
        public int MaxExpansions { get; set; } = 150000;

        public double TimeLimitSeconds { get; set; } = 10;

        /// <summary>What a change of direction costs, in metres of driving: higher prefers fewer movements.</summary>
        public double SwitchCost { get; set; } = 6;

        /// <summary>Reversing a metre costs this many metres of driving forward.</summary>
        public double ReverseFactor { get; set; } = 1.2;
    }

    /// <summary>One movement of a solved manoeuvre: the rear axle's path, forwards or in reverse.</summary>
    public sealed class SolvedLeg
    {
        public bool Reverse { get; set; }
        public Path2 Path { get; set; } = null!;

        /// <summary>The way the vehicle faces at the start of the leg, radians.</summary>
        public double StartHeading { get; set; }

        public double Length => Path.Length;

        public TurnPathRequest ToRequest(DesignVehicle v) => new TurnPathRequest
        {
            Vehicle = v,
            Path = Path,
            Reference = v.RearAxleCentre,
            Reverse = Reverse,
            InitialHeading = StartHeading,
        };
    }

    public sealed class SolveResult
    {
        public bool Solved { get; set; }
        public List<SolvedLeg> Legs { get; } = new List<SolvedLeg>();

        /// <summary>The clearance asked for, and the clearance the solution keeps in the open (less when it had to be reduced).</summary>
        public double ClearanceAsked { get; set; }

        public double ClearanceUsed { get; set; }

        /// <summary>
        /// The clearance kept to the linework that is closer than the clearance used where the vehicle
        /// starts, and where it ends: the room it has there, or less if that had to be reduced too.
        /// </summary>
        public double StartClearance { get; set; }

        public double GoalClearance { get; set; }

        /// <summary>True when no clearance could help: the start or target overlaps an obstruction or is outside the search area.</summary>
        public bool Blocked { get; set; }

        /// <summary>True when some linework was closer than the clearance at the start or target, and was allowed less.</summary>
        public bool TightPieces { get; set; }

        public int Expansions { get; set; }
        public double Seconds { get; set; }

        /// <summary>How many obstruction objects the search was given.</summary>
        public int ObstructionsUsed { get; set; }

        /// <summary>Why there is no solution.</summary>
        public string? Failure { get; set; }

        /// <summary>The number of movements: 3 for a three-point turn.</summary>
        public int Movements => Legs.Count;

        public double Length => Legs.Sum(l => l.Length);

        /// <summary>True when the clearance had to be reduced away from the start and target as well.</summary>
        public bool ClearanceReduced => Solved && ClearanceUsed < ClearanceAsked - 1e-9;

        /// <summary>True when the start or target position itself has less room than the clearance used.</summary>
        public bool TightAtEnds => Solved && (StartClearance < ClearanceUsed - 5e-4 || GoalClearance < ClearanceUsed - 5e-4);

        public string Summary()
        {
            if (!Solved)
            {
                return "No solution: " + (Failure ?? "unknown reason") + ".";
            }

            string moves = string.Join(", ", Legs.Select(l => $"{(l.Reverse ? "reverse" : "forward")} {l.Length:0.0} m"));
            string text = $"Solved in {Movements} movement{(Movements == 1 ? "" : "s")} ({moves}), {Length:0.0} m in all";
            if (ClearanceReduced)
            {
                text += $", but only with the clearance reduced from {ClearanceAsked * 1000:0} to {ClearanceUsed * 1000:0} mm";
            }

            if (TightAtEnds)
            {
                bool start = StartClearance < ClearanceUsed - 5e-4, goal = GoalClearance < ClearanceUsed - 5e-4;
                string where = start && goal ? "start and target" : start ? "start" : "target";
                double least = Math.Min(start ? StartClearance : double.MaxValue, goal ? GoalClearance : double.MaxValue);
                text += ClearanceReduced
                    ? $" (and {least * 1000:0} mm to what is already closer than that at the {where})"
                    : $", keeping {ClearanceUsed * 1000:0} mm clearance except to what is already closer than that at the {where}, which keeps {least * 1000:0} mm";
            }

            return text + ".";
        }
    }

    /// <summary>
    /// Finds the forward and reverse movements that take a vehicle from one pose to another without
    /// its body, enlarged by the clearance, touching any obstruction: a multi-point turn solver.
    ///
    /// Method (hybrid A*): the search steps the rear axle along full-lock arcs and straights,
    /// forwards and in reverse, keeping the cheapest way found into each small cell of position
    /// and heading. Cost is distance, more for reversing and much more for each change of
    /// direction, so it looks for the fewest movements first. From each position it also tries
    /// to finish in one movement: a turn, a straight and a turn to the target itself, or to a
    /// place on the line through the target from which the vehicle can run straight in (how a
    /// car is put into a bay). Every step is tested by sweeping the body between two poses and
    /// checking it against the obstruction linework. The body is its true outline (curved ends,
    /// rounded corners) enlarged by the clearance and wrapped in straight edges that stand no
    /// more than 10 mm outside it.
    ///
    /// The search works on a grid (0.25 m, 5 degrees), so it finds a good manoeuvre, not always
    /// the one with the very fewest movements.
    /// </summary>
    public static class ManoeuvreSolver
    {
        private const int Headings = 72;        // 5 degree cells
        private const double Cell = 0.25;       // position cells, metres
        private const double BodyMargin = 0.01; // kept on top of the clearance: covers the chords used for curves
        private const double ShotSpacing = 0.2; // pose spacing when checking a path to the target
        private const double ShotRange = 15;    // within this of the target, every position tries to finish
        private const double EntrySpacing = 1.0; // ways in: this far apart along the line through the target,
        private const double MaxEntry = 5.0;     // up to this far from it,
        private const double EntryRange = 12;    // tried from positions within this of the target

        /// <summary>
        /// Solves with the clearance asked for. Linework that is already closer than that where the
        /// vehicle starts or ends (the sides of a narrow bay) can't have it, so it keeps the distance
        /// it has there; or half the clearance, when passing that close to it saves movements. If
        /// there is still no way through, everything is allowed half the clearance, then none. The
        /// result always says how much room the manoeuvre really has.
        /// </summary>
        public static SolveResult SolveWithFallback(SolveRequest q)
        {
            double asked = Math.Max(0, q.Clearance);
            SolveResult Try(double open, double tight, double budget)
            {
                SolveResult r = Solve(q, open, budget, true, tight);
                r.ClearanceAsked = asked;
                return r;
            }

            SolveResult first = Try(asked, asked, 1.0);
            if (first.Blocked || asked < 1e-9 || (first.Solved && (!first.TightPieces || first.Movements <= 2)))
            {
                return first;
            }

            SolveResult? best = first.Solved ? first : null;
            if (first.TightPieces)
            {
                // What is tight at the ends: half the clearance, then none.
                SolveResult closer = Try(asked, asked / 2, 0.5);
                if (closer.Solved && (best == null || closer.Movements < best.Movements))
                {
                    best = closer;
                }

                if (best == null)
                {
                    closer = Try(asked, 0, 0.5);
                    best = closer.Solved ? closer : null;
                }
            }

            if (best != null)
            {
                return best;
            }

            SolveResult last = Try(asked / 2, asked / 2, 0.5);
            if (!last.Solved)
            {
                last = Try(0, 0, 0.5);
            }

            if (!last.Solved)
            {
                last.Failure = (last.Failure ?? "no route found") + " (even with no clearance)";
            }

            return last;
        }

        /// <summary>
        /// Solves keeping <paramref name="clearance"/> to every obstruction. With <paramref name="relaxAtEnds"/>,
        /// linework closer than that at the start or target pose keeps the distance it has there instead
        /// (or <paramref name="tightClearance"/>, if that is less).
        /// </summary>
        public static SolveResult Solve(SolveRequest q, double clearance, double budget = 1.0, bool relaxAtEnds = false, double? tightClearance = null)
        {
            var watch = Stopwatch.StartNew();
            DesignVehicle v = q.Vehicle;
            double radius = q.Radius ?? v.MinTurnRadius;
            if (radius < v.MinTurnRadius - 1e-9)
            {
                throw new ArgumentException($"The turn radius can't be less than the {v.Name}'s minimum of {v.MinTurnRadius:0.#} m.");
            }

            var result = new SolveResult { ClearanceAsked = q.Clearance, ClearanceUsed = clearance, StartClearance = clearance, GoalClearance = clearance };
            double rho = v.RearAxleRadius(radius);
            var space = new Space(v, q.Obstructions, q.Min, q.Max);

            SolveResult Done(string? failure, bool blocked = false)
            {
                result.Failure = failure;
                result.Blocked = blocked;
                result.Seconds = watch.Elapsed.TotalSeconds;
                return result;
            }

            if (!space.Inside(q.Start.RearAxle) || !space.Inside(q.Goal.RearAxle))
            {
                return Done("the start or target is outside the search area", true);
            }

            double startRoom = space.Room(q.Start, clearance), goalRoom = space.Room(q.Goal, clearance);
            if (startRoom < 0 || goalRoom < 0)
            {
                return Done($"the vehicle at the {(startRoom < 0 ? "start" : "target")} overlaps an obstruction", true);
            }

            result.StartClearance = Math.Floor(startRoom * 1000 + 0.5) / 1000;
            result.GoalClearance = Math.Floor(goalRoom * 1000 + 0.5) / 1000;
            space.SetClearance(clearance);
            if (relaxAtEnds)
            {
                double tight = Math.Min(clearance, tightClearance ?? clearance);
                if (!space.RelaxAtEnds(q.Start, q.Goal, tight, out bool tightAtStart, out bool tightAtGoal))
                {
                    return Done("the vehicle at the start or target overlaps an obstruction", true);
                }

                // What is kept to the linework that is tight at each end.
                result.TightPieces = tightAtStart || tightAtGoal;
                if (tightAtStart)
                {
                    result.StartClearance = Math.Min(result.StartClearance, tight);
                }

                if (tightAtGoal)
                {
                    result.GoalClearance = Math.Min(result.GoalClearance, tight);
                }
            }
            else if (!space.Free(q.Start) || !space.Free(q.Goal))
            {
                bool start = !space.Free(q.Start);
                return Done($"the vehicle at the {(start ? "start" : "target")} is within {clearance * 1000:0} mm of an obstruction");
            }

            if (Segment.Dist(q.Start.RearAxle, q.Goal.RearAxle) < 0.02 && Math.Abs(Angles.Wrap(q.Start.Heading - q.Goal.Heading)) < 0.01)
            {
                return Done("the start and the target are the same position");
            }

            // ---- the search grid: position cells x heading cells x direction of travel
            double minX = q.Min.X, minY = q.Min.Y;
            double cell = Cell;
            double width = q.Max.X - minX, height = q.Max.Y - minY;
            const double MaxStates = 12e6;
            if (width / cell * (height / cell) * Headings * 2 > MaxStates)
            {
                cell = Math.Sqrt(width * height * Headings * 2 / MaxStates);
            }

            int nx = (int)Math.Ceiling(width / cell) + 1, ny = (int)Math.Ceiling(height / cell) + 1;
            var bestG = new float[(long)nx * ny * Headings * 2];
            for (int i = 0; i < bestG.Length; i++)
            {
                bestG[i] = float.MaxValue;
            }

            double dTheta = 2 * Math.PI / Headings;
            double step = Math.Max(cell * 1.5, Math.Min(0.6, rho * dTheta));
            double maxLegTurn = TurnPathBuilder.MaxLegTurnDeg * Math.PI / 180;
            double goalX = q.Goal.RearAxle.X, goalY = q.Goal.RearAxle.Y, goalTh = q.Goal.Heading;

            long Key(double x, double y, double th, int dir)
            {
                int ix = (int)((x - minX) / cell), iy = (int)((y - minY) / cell);
                int ith = (int)(Angles.Normalize(th) / dTheta) % Headings;
                return (((long)ix * ny + iy) * Headings + ith) * 2 + (dir < 0 ? 1 : 0);
            }

            // Never an overestimate: the straight-line distance, or the distance needed just to turn to the heading.
            double Heuristic(double x, double y, double th)
            {
                double dx = goalX - x, dy = goalY - y;
                return Math.Max(Math.Sqrt(dx * dx + dy * dy), rho * Math.Abs(Angles.Wrap(goalTh - th)));
            }

            var nodes = new List<Node> { new Node { X = q.Start.RearAxle.X, Y = q.Start.RearAxle.Y, Th = q.Start.Heading, Parent = -1 } };
            var open = new Heap();
            open.Push(Heuristic(nodes[0].X, nodes[0].Y, nodes[0].Th), 0);
            bestG[Key(nodes[0].X, nodes[0].Y, nodes[0].Th, 1)] = 0;
            bestG[Key(nodes[0].X, nodes[0].Y, nodes[0].Th, -1)] = 0;

            // The body swept through half a step, for each way of stepping; and standing still.
            var moves = new Sweep[2, 3];
            for (int k = -1; k <= 1; k++)
            {
                moves[0, k + 1] = space.For(k / rho, step / 2);
                moves[1, k + 1] = space.For(k / rho, -step / 2);
            }

            // Ways in: places on the line through the target from which the vehicle can run straight
            // to it. Forwards it comes from behind the target; in reverse, from in front of it.
            var waysIn = new List<(double X, double Y, double Run)>[2];
            for (int b = 0; b < 2; b++)
            {
                waysIn[b] = new List<(double, double, double)> { (goalX, goalY, 0) };
                double side = b == 1 ? 1 : -1;
                Sweep run = space.For(0, b == 1 ? -EntrySpacing : EntrySpacing);
                for (int i = 1; i * EntrySpacing <= MaxEntry + 1e-9; i++)
                {
                    double x = goalX + side * i * EntrySpacing * Math.Cos(goalTh), y = goalY + side * i * EntrySpacing * Math.Sin(goalTh);
                    if (!space.Inside(x, y) || !space.Clear(x, y, goalTh, run))
                    {
                        break;
                    }

                    waysIn[b].Add((x, y, i * EntrySpacing));
                }
            }

            int bestNode = -1;
            Path2? bestPath = null;
            bool bestBack = false;
            double bestTotal = double.MaxValue;
            int maxExpansions = Math.Max(1000, (int)(q.MaxExpansions * budget));
            double timeLimit = q.TimeLimitSeconds * budget;
            int improveUntil = int.MaxValue;
            int expansions = 0;
            bool exhausted = false;
            var shots = new List<Shot>();

            while (open.Count > 0)
            {
                open.Pop(out double f, out int index);
                Node n = nodes[index];
                if (index != 0 && n.G > bestG[Key(n.X, n.Y, n.Th, n.Dir)] + 1e-4)
                {
                    continue; // a cheaper way into this cell was found since
                }

                if (f >= bestTotal)
                {
                    break; // nothing left can beat the solution in hand
                }

                if (++expansions > maxExpansions || expansions > improveUntil || ((expansions & 255) == 0 && watch.Elapsed.TotalSeconds > timeLimit))
                {
                    exhausted = true;
                    break;
                }

                // ---- try to finish from here in one go: a turn, a straight and a turn to the target, or to a way in
                double away = Math.Sqrt((goalX - n.X) * (goalX - n.X) + (goalY - n.Y) * (goalY - n.Y));
                if (away < ShotRange || (expansions & 3) == 0)
                {
                    shots.Clear();
                    for (int b = 0; b < 2; b++)
                    {
                        bool back = b == 1;
                        bool sameLeg = n.Dir == (back ? -1 : 1);
                        double factor = back ? q.ReverseFactor : 1;
                        double before = n.G + (n.Dir != 0 && !sameLeg ? q.SwitchCost : 0);
                        double turnLeft = maxLegTurn - (sameLeg ? Math.Abs(n.LegTurn) : 0); // more than this would make the movement a loop
                        double flip = back ? Math.PI : 0;
                        foreach (var (x, y, run) in waysIn[b])
                        {
                            if (run > 0 && away > EntryRange)
                            {
                                break;
                            }

                            double direct = Math.Sqrt((x - n.X) * (x - n.X) + (y - n.Y) * (y - n.Y));
                            if (before + (direct + run) * factor < bestTotal)
                            {
                                Shot.Add(shots, n.X, n.Y, n.Th + flip, x, y, goalTh + flip, rho, turnLeft, back, run, goalX, goalY, before, factor);
                            }
                        }
                    }

                    shots.Sort(Shot.ByTotal);
                    foreach (Shot shot in shots)
                    {
                        if (shot.Total >= bestTotal - 1e-6)
                        {
                            break;
                        }

                        Path2 path = shot.Path(rho);
                        if (space.PathFree(path, shot.Back))
                        {
                            bestTotal = shot.Total;
                            bestNode = index;
                            bestPath = path;
                            bestBack = shot.Back;
                            if (improveUntil == int.MaxValue)
                            {
                                // A solution is in hand: look a while longer for one with fewer movements, then stop.
                                improveUntil = expansions + Math.Max(20000, 4 * expansions);
                            }

                            break;
                        }
                    }
                }

                // ---- step on: forwards and in reverse, hard left, straight, hard right
                for (int dir = 1; dir >= -1; dir -= 2)
                {
                    for (int k = -1; k <= 1; k++)
                    {
                        double d = dir * step;
                        Advance(n.X, n.Y, n.Th, k / rho, d / 2, out double mx, out double my, out double mth);
                        Advance(n.X, n.Y, n.Th, k / rho, d, out double ex, out double ey, out double eth);
                        if (!space.Inside(ex, ey))
                        {
                            continue;
                        }

                        bool sameLeg = n.Dir == dir || n.Dir == 0;
                        double legTurn = (sameLeg ? n.LegTurn : 0) + k * d / rho;
                        if (Math.Abs(legTurn) > maxLegTurn)
                        {
                            continue;
                        }

                        double g = n.G + step * (dir < 0 ? q.ReverseFactor : 1) + (sameLeg ? 0 : q.SwitchCost) + (k != n.K ? 0.05 : 0);
                        long key = Key(ex, ey, eth, dir);
                        if (g >= bestG[key] - 1e-4 || g + Heuristic(ex, ey, eth) >= bestTotal)
                        {
                            continue;
                        }

                        Sweep sweep = moves[dir > 0 ? 0 : 1, k + 1];
                        if (!space.Clear(n.X, n.Y, n.Th, sweep) || !space.Clear(mx, my, mth, sweep))
                        {
                            continue;
                        }

                        bestG[key] = (float)g;
                        nodes.Add(new Node { X = ex, Y = ey, Th = eth, Parent = index, Dir = (sbyte)dir, K = (sbyte)k, G = g, LegTurn = legTurn });
                        open.Push(g + Heuristic(ex, ey, eth), nodes.Count - 1);
                    }
                }
            }

            result.Expansions = expansions;
            if (bestNode < 0)
            {
                return Done(exhausted
                    ? $"no route found after trying {expansions:N0} positions in {watch.Elapsed.TotalSeconds:0} s; the space may be too tight, or the search area or time too small"
                    : "the positions the vehicle can reach from the start don't connect to the target: the space is too tight");
            }

            BuildLegs(result, nodes, bestNode, bestPath, bestBack, rho, step);
            result.Solved = true;
            return Done(null);
        }

        /// <summary>Moves a pose along an arc (or straight) of signed length d: positive forwards, negative in reverse.</summary>
        private static void Advance(double x, double y, double th, double kappa, double d, out double nx, out double ny, out double nth)
        {
            if (Math.Abs(kappa) < 1e-12)
            {
                nx = x + d * Math.Cos(th);
                ny = y + d * Math.Sin(th);
                nth = th;
                return;
            }

            nth = th + kappa * d;
            nx = x + (Math.Sin(nth) - Math.Sin(th)) / kappa;
            ny = y - (Math.Cos(nth) - Math.Cos(th)) / kappa;
        }

        /// <summary>Turns the chain of search steps, and the final path to the target, into movements.</summary>
        private static void BuildLegs(SolveResult result, List<Node> nodes, int last, Path2? shot, bool shotBack, double rho, double step)
        {
            var chain = new List<int>();
            for (int i = last; i > 0; i = nodes[i].Parent)
            {
                chain.Add(i);
            }

            chain.Reverse();

            SolvedLeg? leg = null;
            List<Segment>? segments = null;
            int run = 0;          // steps in the current arc or straight
            Node runStart = default;
            Node previous = nodes[0];

            void CloseRun(Node end)
            {
                if (run == 0 || segments == null)
                {
                    return;
                }

                Node first = runStart;
                int k = end.K, dir = end.Dir;
                var from = new Point2(first.X, first.Y);
                if (k == 0)
                {
                    segments.Add(new LineSegment(from, new Point2(end.X, end.Y)));
                }
                else
                {
                    // The turn centre is rho to the vehicle's left (k = +1) or right (k = -1).
                    var centre = new Point2(first.X - Math.Sin(first.Th) * rho * k, first.Y + Math.Cos(first.Th) * rho * k);
                    segments.Add(new ArcSegment(centre, rho, Math.Atan2(from.Y - centre.Y, from.X - centre.X), k * dir * run * step / rho));
                }

                run = 0;
            }

            void CloseLeg(Node end)
            {
                CloseRun(end);
                if (leg != null && segments != null && segments.Count > 0)
                {
                    leg.Path = new Path2(segments, false);
                    result.Legs.Add(leg);
                }

                leg = null;
                segments = null;
            }

            foreach (int i in chain)
            {
                Node n = nodes[i];
                if (leg == null || (n.Dir < 0) != leg.Reverse)
                {
                    CloseLeg(previous);
                    leg = new SolvedLeg { Reverse = n.Dir < 0, StartHeading = previous.Th };
                    segments = new List<Segment>();
                }
                else if (run > 0 && n.K != previous.K)
                {
                    CloseRun(previous);
                }

                if (run == 0)
                {
                    runStart = previous;
                }

                run++;
                previous = n;
            }

            if (shot != null)
            {
                if (leg != null && leg.Reverse == shotBack)
                {
                    CloseRun(previous);
                    segments!.AddRange(shot.Segments);
                    CloseLeg(previous);
                }
                else
                {
                    CloseLeg(previous);
                    result.Legs.Add(new SolvedLeg { Reverse = shotBack, StartHeading = previous.Th, Path = shot });
                }
            }
            else
            {
                CloseLeg(previous);
            }
        }

        /// <summary>
        /// A way to finish in one movement: a turn, a straight and a turn of the rear axle to a place
        /// on the line through the target, then straight along that line to the target.
        /// </summary>
        private struct Shot
        {
            public static readonly Comparison<Shot> ByTotal = (a, b) => a.Total.CompareTo(b.Total);

            public double X0, Y0, C0X, C0Y, Sweep0, Beta, Straight, C1X, C1Y, Sweep1, GoalX, GoalY, Run, Total;
            public bool Back;

            /// <summary>Adds each of the four turn-straight-turn paths that exists and doesn't loop.</summary>
            public static void Add(List<Shot> into, double x0, double y0, double t0, double x1, double y1, double t1, double rho,
                double maxTurn, bool back, double run, double goalX, double goalY, double before, double factor)
            {
                for (int a = 0; a < 2; a++)
                {
                    for (int b = 0; b < 2; b++)
                    {
                        double s0 = a == 0 ? 1 : -1, s1 = b == 0 ? 1 : -1;
                        double c0x = x0 - Math.Sin(t0) * rho * s0, c0y = y0 + Math.Cos(t0) * rho * s0;
                        double c1x = x1 - Math.Sin(t1) * rho * s1, c1y = y1 + Math.Cos(t1) * rho * s1;
                        double dx = c1x - c0x, dy = c1y - c0y;
                        double dist = Math.Sqrt(dx * dx + dy * dy);
                        double beta, straight;
                        if (a == b)
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

                            beta = Math.Atan2(dy, dx) + Math.Asin(Math.Min(1, 2 * rho / dist)) * s0;
                            straight = Math.Sqrt(Math.Max(0, dist * dist - 4 * rho * rho));
                        }

                        double sweep0 = Turn(t0, beta, s0), sweep1 = Turn(beta, t1, s1);
                        double length = rho * (Math.Abs(sweep0) + Math.Abs(sweep1)) + straight;
                        if (Math.Abs(sweep0) + Math.Abs(sweep1) > maxTurn || length < 0.05)
                        {
                            continue;
                        }

                        into.Add(new Shot
                        {
                            X0 = x0, Y0 = y0, C0X = c0x, C0Y = c0y, Sweep0 = sweep0, Beta = beta, Straight = straight,
                            C1X = c1x, C1Y = c1y, Sweep1 = sweep1, GoalX = goalX, GoalY = goalY, Run = run, Back = back,
                            Total = before + (length + run) * factor,
                        });
                    }
                }
            }

            private static double Turn(double from, double to, double side)
            {
                double a = side > 0 ? Angles.Normalize(to - from) : Angles.Normalize(from - to);
                return a > 2 * Math.PI - 1e-7 ? 0 : a * side;
            }

            public Path2 Path(double rho)
            {
                var segments = new List<Segment>(4);
                var at = new Point2(X0, Y0);
                if (Math.Abs(Sweep0) > 1e-6)
                {
                    var arc = new ArcSegment(new Point2(C0X, C0Y), rho, Math.Atan2(Y0 - C0Y, X0 - C0X), Sweep0);
                    segments.Add(arc);
                    at = arc.End;
                }

                if (Straight > 1e-6)
                {
                    var to = new Point2(at.X + Math.Cos(Beta) * Straight, at.Y + Math.Sin(Beta) * Straight);
                    segments.Add(new LineSegment(at, to));
                    at = to;
                }

                if (Math.Abs(Sweep1) > 1e-6)
                {
                    var arc = new ArcSegment(new Point2(C1X, C1Y), rho, Math.Atan2(at.Y - C1Y, at.X - C1X), Sweep1);
                    segments.Add(arc);
                    at = arc.End;
                }

                if (Run > 0)
                {
                    segments.Add(new LineSegment(at, new Point2(GoalX, GoalY)));
                }

                return new Path2(segments, false);
            }
        }

        private struct Node
        {
            public double X;
            public double Y;
            public double Th;
            public int Parent;
            public sbyte Dir;       // +1 forwards, -1 reverse, 0 at the start
            public sbyte K;         // +1 left, 0 straight, -1 right
            public double G;
            public double LegTurn;  // heading change so far in this movement
        }

        /// <summary>A binary min-heap of (cost, node).</summary>
        private sealed class Heap
        {
            private double[] _keys = new double[1024];
            private int[] _values = new int[1024];

            public int Count { get; private set; }

            public void Push(double key, int value)
            {
                if (Count == _keys.Length)
                {
                    Array.Resize(ref _keys, Count * 2);
                    Array.Resize(ref _values, Count * 2);
                }

                int i = Count++;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (_keys[parent] <= key)
                    {
                        break;
                    }

                    _keys[i] = _keys[parent];
                    _values[i] = _values[parent];
                    i = parent;
                }

                _keys[i] = key;
                _values[i] = value;
            }

            public void Pop(out double key, out int value)
            {
                key = _keys[0];
                value = _values[0];
                Count--;
                double lastKey = _keys[Count];
                int lastValue = _values[Count];
                int i = 0;
                while (true)
                {
                    int child = 2 * i + 1;
                    if (child >= Count)
                    {
                        break;
                    }

                    if (child + 1 < Count && _keys[child + 1] < _keys[child])
                    {
                        child++;
                    }

                    if (lastKey <= _keys[child])
                    {
                        break;
                    }

                    _keys[i] = _keys[child];
                    _values[i] = _values[child];
                    i = child;
                }

                _keys[i] = lastKey;
                _values[i] = lastValue;
            }
        }

        /// <summary>
        /// The enlarged body swept through one small movement, as a convex outline in the frame of the
        /// pose it starts from: one outline for each class of obstruction piece.
        /// </summary>
        private sealed class Sweep
        {
            public readonly double[][] X = new double[3][];
            public readonly double[][] Y = new double[3][];
            public double LowX;
            public double LowY;
            public double HighX;
            public double HighY;
        }

        /// <summary>
        /// The obstructions as short straight pieces in a coarse grid, and the vehicle's body as a
        /// convex outline enlarged by the clearance. A movement between two poses is clear when no
        /// piece touches the convex hull of the body at both poses.
        ///
        /// Pieces that are already closer than the clearance where the vehicle starts or ends (the
        /// sides of a narrow bay), and what joins them, can be given the distance they have there as
        /// a clearance of their own, so the manoeuvre still keeps the full clearance to everything else.
        /// </summary>
        private sealed class Space
        {
            private const double QueryPad = 0.5;
            private const double OutlineTolerance = 0.01; // how far the straight-edged outline may stand outside the true curves
            private const int Open = 0, AtStart = 1, AtGoal = 2;

            private readonly DesignVehicle _vehicle;
            private readonly double _minX;
            private readonly double _minY;
            private readonly double _maxX;
            private readonly double _maxY;
            private readonly List<double> _ax = new List<double>();
            private readonly List<double> _ay = new List<double>();
            private readonly List<double> _bx = new List<double>();
            private readonly List<double> _by = new List<double>();
            private readonly List<byte> _class = new List<byte>();
            private readonly List<int>?[] _grid;
            private readonly double _gridX;
            private readonly double _gridY;
            private readonly double _gridCell;
            private readonly int _gridWidth;
            private readonly int _gridHeight;
            private readonly double[] _margin = new double[3];
            private readonly double[][] _outlineX = new double[3][];
            private readonly double[][] _outlineY = new double[3][];
            private readonly Dictionary<(long Curvature, int Millimetres), Sweep> _sweeps = new Dictionary<(long, int), Sweep>();
            private int[] _stamp = new int[0];
            private int _tick;

            public Space(DesignVehicle v, IReadOnlyList<Obstruction> obstructions, Point2 min, Point2 max)
            {
                _vehicle = v;
                _minX = min.X;
                _minY = min.Y;
                _maxX = max.X;
                _maxY = max.Y;

                // Only linework that the body could reach from inside the search area matters.
                double reach = v.Length + 2;
                _gridX = min.X - reach - 1;
                _gridY = min.Y - reach - 1;
                double spanX = max.X - min.X + 2 * reach + 2, spanY = max.Y - min.Y + 2 * reach + 2;
                _gridCell = Math.Max(1.0, Math.Sqrt(spanX * spanY / 2e6));
                _gridWidth = (int)Math.Ceiling(spanX / _gridCell) + 1;
                _gridHeight = (int)Math.Ceiling(spanY / _gridCell) + 1;
                _grid = new List<int>?[_gridWidth * _gridHeight];
                foreach (Obstruction o in obstructions)
                {
                    foreach (Segment s in o.Path.Segments)
                    {
                        if (s is ArcSegment arc)
                        {
                            // Chords within 2 mm of the arc.
                            double half = Math.Acos(Math.Max(-1, 1 - 0.002 / Math.Max(arc.Radius, 0.003)));
                            int parts = Math.Max(2, Math.Min(180, (int)Math.Ceiling(Math.Abs(arc.Sweep) / (2 * half))));
                            Point2 a = arc.Start;
                            for (int i = 1; i <= parts; i++)
                            {
                                Point2 b = arc.At(arc.StartAngle + arc.Sweep * i / parts);
                                Add(a.X, a.Y, b.X, b.Y, Open, reach);
                                a = b;
                            }
                        }
                        else
                        {
                            Add(s.Start.X, s.Start.Y, s.End.X, s.End.Y, Open, reach);
                        }
                    }
                }

                _stamp = new int[_ax.Count];
                SetClearance(0);
            }

            /// <summary>Sets the clearance kept to every piece. A little is added for the chords that stand in for curves.</summary>
            public void SetClearance(double clearance)
            {
                _margin[Open] = _margin[AtStart] = _margin[AtGoal] = clearance + BodyMargin;
                Outline(_vehicle, _margin[Open], out _outlineX[Open], out _outlineY[Open]);
                _outlineX[AtStart] = _outlineX[AtGoal] = _outlineX[Open];
                _outlineY[AtStart] = _outlineY[AtGoal] = _outlineY[Open];
                for (int i = 0; i < _class.Count; i++)
                {
                    _class[i] = Open;
                }

                _sweeps.Clear();
            }

            /// <summary>
            /// Lets the pieces that are closer than the clearance at the start pose, and at the target pose,
            /// keep the distance they have there. False when the vehicle overlaps a piece at either.
            /// </summary>
            public bool RelaxAtEnds(VehiclePose start, VehiclePose goal, double atMost, out bool tightAtStart, out bool tightAtGoal)
            {
                tightAtGoal = false;
                if (!Relax(start, AtStart, atMost, out tightAtStart) || !Relax(goal, AtGoal, atMost, out tightAtGoal))
                {
                    return false;
                }

                Spread();
                _sweeps.Clear();
                return true;
            }

            private bool Relax(VehiclePose p, int cls, double atMost, out bool any)
            {
                var tight = new List<int>();
                any = Touching(p, _margin[Open], tight);
                if (!any)
                {
                    return true; // the full clearance fits here
                }

                if (Touching(p, 0, null))
                {
                    return false;
                }

                // The largest enlargement of the body that still fits at this pose, less a hair.
                double low = 0, high = _margin[Open];
                for (int i = 0; i < 14; i++)
                {
                    double mid = (low + high) / 2;
                    if (Touching(p, mid, null))
                    {
                        high = mid;
                    }
                    else
                    {
                        low = mid;
                    }
                }

                _margin[cls] = Math.Max(0, Math.Min(low - 0.002, atMost + BodyMargin));
                Outline(_vehicle, _margin[cls], out _outlineX[cls], out _outlineY[cls]);
                foreach (int s in tight)
                {
                    // A piece that is tight at both ends keeps the smaller of the two.
                    if (_class[s] == Open || _margin[cls] < _margin[_class[s]])
                    {
                        _class[s] = (byte)cls;
                    }
                }

                return true;
            }

            /// <summary>
            /// Where another piece comes within the clearance of a tight one (the corner where an aisle
            /// edge meets the side of a narrow bay), that part of it takes the tight piece's clearance:
            /// the vehicle can't keep more to it than to the piece it joins.
            /// </summary>
            private void Spread()
            {
                var tight = new List<int>();
                for (int i = 0; i < _class.Count; i++)
                {
                    if (_class[i] != Open)
                    {
                        tight.Add(i);
                    }
                }

                if (tight.Count == 0)
                {
                    return;
                }

                double near = _margin[Open];
                const double Sample = 0.05;
                int count = _ax.Count;
                var close = new List<int>();
                for (int i = 0; i < count; i++)
                {
                    if (_class[i] != Open)
                    {
                        continue;
                    }

                    double ax = _ax[i], ay = _ay[i], bx = _bx[i], by = _by[i];
                    close.Clear();
                    foreach (int t in tight)
                    {
                        if (Math.Max(_ax[t], _bx[t]) >= Math.Min(ax, bx) - near && Math.Min(_ax[t], _bx[t]) <= Math.Max(ax, bx) + near
                            && Math.Max(_ay[t], _by[t]) >= Math.Min(ay, by) - near && Math.Min(_ay[t], _by[t]) <= Math.Max(ay, by) + near)
                        {
                            close.Add(t);
                        }
                    }

                    if (close.Count == 0)
                    {
                        continue;
                    }

                    // Class of each short stretch of the piece, by its middle.
                    double length = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                    int parts = Math.Max(1, (int)Math.Ceiling(length / Sample));
                    var classes = new byte[parts];
                    bool any = false;
                    for (int k = 0; k < parts; k++)
                    {
                        double t = (k + 0.5) / parts;
                        double x = ax + (bx - ax) * t, y = ay + (by - ay) * t;
                        byte cls = Open;
                        foreach (int j in close)
                        {
                            if (PointToPiece(x, y, _ax[j], _ay[j], _bx[j], _by[j]) <= near && (cls == Open || _margin[_class[j]] < _margin[cls]))
                            {
                                cls = _class[j];
                            }
                        }

                        classes[k] = cls;
                        any |= cls != Open;
                    }

                    if (!any)
                    {
                        continue;
                    }

                    // Cut the piece where the class changes: the first run stays in place, the rest are added.
                    bool first = true;
                    for (int k = 0; k < parts;)
                    {
                        int end = k;
                        while (end + 1 < parts && classes[end + 1] == classes[k])
                        {
                            end++;
                        }

                        double t0 = (double)k / parts, t1 = (double)(end + 1) / parts;
                        double x0 = ax + (bx - ax) * t0, y0 = ay + (by - ay) * t0, x1 = ax + (bx - ax) * t1, y1 = ay + (by - ay) * t1;
                        if (first)
                        {
                            _ax[i] = x0;
                            _ay[i] = y0;
                            _bx[i] = x1;
                            _by[i] = y1;
                            _class[i] = classes[k];
                            first = false;
                        }
                        else
                        {
                            Add(x0, y0, x1, y1, classes[k], double.MaxValue);
                        }

                        k = end + 1;
                    }
                }

                _stamp = new int[_ax.Count];
            }

            /// <summary>True when the body, enlarged by the margin, touches a piece at the pose. Collects the pieces it touches.</summary>
            private bool Touching(VehiclePose p, double margin, List<int>? touched)
            {
                Outline(_vehicle, margin, out double[] xs, out double[] ys);
                Sweep sw = Make(new[] { xs, xs, xs }, new[] { ys, ys, ys }, 0, 0, 0);
                touched?.Clear();
                return !Clear(p.RearAxle.X, p.RearAxle.Y, p.Heading, sw, touched ?? new List<int>(), true);
            }

            /// <summary>
            /// The distance from the body to the nearest piece at a pose, up to <paramref name="upTo"/>;
            /// -1 when the body overlaps one.
            /// </summary>
            public double Room(VehiclePose p, double upTo)
            {
                Outline(_vehicle, 0, out double[] xs, out double[] ys);
                Sweep sw = Make(new[] { xs, xs, xs }, new[] { ys, ys, ys }, 0, 0, 0);
                double[] hx = sw.X[0], hy = sw.Y[0];
                int m = hx.Length;
                double x = p.RearAxle.X, y = p.RearAxle.Y, c = Math.Cos(p.Heading), sn = Math.Sin(p.Heading);
                double reach = upTo + 0.05, best = upTo;
                for (int s = 0; s < _ax.Count; s++)
                {
                    // The piece in the vehicle's frame.
                    double ux = _ax[s] - x, uy = _ay[s] - y, vx = _bx[s] - x, vy = _by[s] - y;
                    double ax = ux * c + uy * sn, ay = uy * c - ux * sn, bx = vx * c + vy * sn, by = vy * c - vx * sn;
                    if (Math.Max(ax, bx) < sw.LowX - reach || Math.Min(ax, bx) > sw.HighX + reach
                        || Math.Max(ay, by) < sw.LowY - reach || Math.Min(ay, by) > sw.HighY + reach)
                    {
                        continue;
                    }

                    if (Touches(ax, ay, bx, by, hx, hy))
                    {
                        return -1;
                    }

                    for (int i = 0; i < m; i++)
                    {
                        int j = i + 1 == m ? 0 : i + 1;
                        best = Math.Min(best, PointToPiece(ax, ay, hx[i], hy[i], hx[j], hy[j]));
                        best = Math.Min(best, PointToPiece(bx, by, hx[i], hy[i], hx[j], hy[j]));
                        best = Math.Min(best, PointToPiece(hx[i], hy[i], ax, ay, bx, by));
                    }
                }

                return best;
            }

            private static double PointToPiece(double px, double py, double ax, double ay, double bx, double by)
            {
                double dx = bx - ax, dy = by - ay;
                double len2 = dx * dx + dy * dy;
                double t = len2 < 1e-18 ? 0 : Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / len2));
                double ex = px - (ax + dx * t), ey = py - (ay + dy * t);
                return Math.Sqrt(ex * ex + ey * ey);
            }

            public bool Inside(Point2 p) => Inside(p.X, p.Y);

            public bool Inside(double x, double y) => x >= _minX && x <= _maxX && y >= _minY && y <= _maxY;

            public bool Free(VehiclePose p) => Clear(p.RearAxle.X, p.RearAxle.Y, p.Heading, For(0, 0));

            /// <summary>True when the rear axle can follow the path (in reverse: facing backwards along it) without touching anything.</summary>
            public bool PathFree(Path2 path, bool reverse)
            {
                // The end of the path first: that is where it is tightest, as a rule.
                double flip = reverse ? Math.PI : 0;
                for (int n = path.Segments.Count - 1; n >= 0; n--)
                {
                    Segment seg = path.Segments[n];
                    double length = seg.Length;
                    if (length < 1e-9)
                    {
                        continue;
                    }

                    // Every step along one line or arc is the same movement, seen from the vehicle.
                    int steps = Math.Max(1, (int)Math.Ceiling(length / ShotSpacing));
                    double ds = length / steps;
                    double kappa = seg is ArcSegment arc ? Math.Sign(arc.Sweep) / arc.Radius : 0;
                    Sweep sw = For(reverse ? -kappa : kappa, reverse ? -ds : ds);
                    for (int i = 0; i < steps; i++)
                    {
                        Point2 from = seg.PointAt(ds * i), to = seg.PointAt(ds * (i + 1));
                        if (!Inside(to.X, to.Y) || !Clear(from.X, from.Y, seg.TangentAt(ds * i) + flip, sw))
                        {
                            return false;
                        }
                    }
                }

                return true;
            }

            /// <summary>
            /// The sweep for moving a signed distance (negative: in reverse) along a curve of the given
            /// curvature (positive: to the left). Distances are rounded up to the millimetre and kept.
            /// </summary>
            public Sweep For(double kappa, double distance)
            {
                int mm = (int)Math.Ceiling(Math.Abs(distance) * 1000 - 1e-9) * Math.Sign(distance);
                var key = ((long)Math.Round(kappa * 1e7), mm);
                if (!_sweeps.TryGetValue(key, out Sweep? sw))
                {
                    double d = mm / 1000.0, turn = kappa * d;
                    double dx = Math.Abs(kappa) < 1e-12 ? d : Math.Sin(turn) / kappa;
                    double dy = Math.Abs(kappa) < 1e-12 ? 0 : (1 - Math.Cos(turn)) / kappa;
                    sw = Make(_outlineX, _outlineY, dx, dy, turn);
                    _sweeps[key] = sw;
                }

                return sw;
            }

            /// <summary>The hulls of each class's outline where it is and moved by (dx, dy) and turned.</summary>
            private static Sweep Make(double[][] outlineX, double[][] outlineY, double dx, double dy, double turn)
            {
                var sw = new Sweep();
                double c = Math.Cos(turn), s = Math.Sin(turn);
                for (int cls = 0; cls < 3; cls++)
                {
                    int same = Array.FindIndex(outlineX, 0, cls, o => ReferenceEquals(o, outlineX[cls]));
                    if (same >= 0)
                    {
                        sw.X[cls] = sw.X[same];
                        sw.Y[cls] = sw.Y[same];
                        continue;
                    }

                    double[] xs = outlineX[cls], ys = outlineY[cls];
                    var pts = new List<(double X, double Y)>(2 * xs.Length);
                    for (int i = 0; i < xs.Length; i++)
                    {
                        pts.Add((xs[i], ys[i]));
                        pts.Add((dx + xs[i] * c - ys[i] * s, dy + xs[i] * s + ys[i] * c));
                    }

                    Hull(pts, out sw.X[cls], out sw.Y[cls]);
                }

                // The open class has the largest outline: its box holds the others.
                sw.LowX = sw.X[0].Min();
                sw.HighX = sw.X[0].Max();
                sw.LowY = sw.Y[0].Min();
                sw.HighY = sw.Y[0].Max();
                return sw;
            }

            /// <summary>The convex hull of the points, anticlockwise (monotone chain).</summary>
            private static void Hull(List<(double X, double Y)> pts, out double[] hx, out double[] hy)
            {
                pts.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
                var unique = new List<(double X, double Y)>(pts.Count);
                foreach (var p in pts)
                {
                    if (unique.Count == 0 || Math.Abs(p.X - unique[unique.Count - 1].X) > 1e-7 || Math.Abs(p.Y - unique[unique.Count - 1].Y) > 1e-7)
                    {
                        unique.Add(p);
                    }
                }

                var h = new (double X, double Y)[2 * unique.Count + 1];
                int m = 0;
                for (int i = 0; i < unique.Count; i++)
                {
                    while (m >= 2 && Cross(h[m - 2], h[m - 1], unique[i]) <= 1e-13)
                    {
                        m--;
                    }

                    h[m++] = unique[i];
                }

                int lower = m + 1;
                for (int i = unique.Count - 2; i >= 0; i--)
                {
                    while (m >= lower && Cross(h[m - 2], h[m - 1], unique[i]) <= 1e-13)
                    {
                        m--;
                    }

                    h[m++] = unique[i];
                }

                m = Math.Max(1, m - 1); // the last point repeats the first
                hx = new double[m];
                hy = new double[m];
                for (int i = 0; i < m; i++)
                {
                    hx[i] = h[i].X;
                    hy[i] = h[i].Y;
                }
            }

            private static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) =>
                (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

            /// <summary>
            /// True when no piece touches the sweep placed at a pose. With a list, every piece that
            /// touches is collected (and, with <paramref name="oneClass"/>, all pieces are tested against the open outline).
            /// </summary>
            public bool Clear(double x, double y, double th, Sweep sw, List<int>? touched = null, bool oneClass = false)
            {
                if (_ax.Count == 0)
                {
                    return true;
                }

                // The sweep's box, in the drawing.
                double c = Math.Cos(th), s = Math.Sin(th);
                double x1 = sw.LowX * c, x2 = sw.HighX * c, x3 = sw.LowY * s, x4 = sw.HighY * s;
                double y1 = sw.LowX * s, y2 = sw.HighX * s, y3 = sw.LowY * c, y4 = sw.HighY * c;
                double lowX = x + Math.Min(x1, x2) - Math.Max(x3, x4), highX = x + Math.Max(x1, x2) - Math.Min(x3, x4);
                double lowY = y + Math.Min(y1, y2) + Math.Min(y3, y4), highY = y + Math.Max(y1, y2) + Math.Max(y3, y4);

                bool clear = true;
                _tick++;
                int cx0 = Math.Max(0, (int)Math.Floor((lowX - QueryPad - _gridX) / _gridCell)), cx1 = Math.Min(_gridWidth - 1, (int)Math.Floor((highX + QueryPad - _gridX) / _gridCell));
                int cy0 = Math.Max(0, (int)Math.Floor((lowY - QueryPad - _gridY) / _gridCell)), cy1 = Math.Min(_gridHeight - 1, (int)Math.Floor((highY + QueryPad - _gridY) / _gridCell));
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    for (int cy = cy0; cy <= cy1; cy++)
                    {
                        List<int>? list = _grid[cy * _gridWidth + cx];
                        if (list == null)
                        {
                            continue;
                        }

                        foreach (int i in list)
                        {
                            if (_stamp[i] == _tick)
                            {
                                continue;
                            }

                            _stamp[i] = _tick;

                            // The piece in the frame of the pose.
                            double ux = _ax[i] - x, uy = _ay[i] - y, vx = _bx[i] - x, vy = _by[i] - y;
                            double ax = ux * c + uy * s, ay = uy * c - ux * s, bx = vx * c + vy * s, by = vy * c - vx * s;
                            if (Math.Max(ax, bx) < sw.LowX || Math.Min(ax, bx) > sw.HighX || Math.Max(ay, by) < sw.LowY || Math.Min(ay, by) > sw.HighY)
                            {
                                continue;
                            }

                            int cls = oneClass ? Open : _class[i];
                            if (Touches(ax, ay, bx, by, sw.X[cls], sw.Y[cls]))
                            {
                                if (touched == null)
                                {
                                    return false;
                                }

                                touched.Add(i);
                                clear = false;
                            }
                        }
                    }
                }

                return clear;
            }

            /// <summary>True when the piece a-b touches or lies inside the anticlockwise convex outline.</summary>
            private static bool Touches(double ax, double ay, double bx, double by, double[] hx, double[] hy)
            {
                int m = hx.Length;
                double enter = 0, leave = 1;
                double dx = bx - ax, dy = by - ay;
                for (int i = 0; i < m; i++)
                {
                    int j = i + 1 == m ? 0 : i + 1;
                    double ex = hx[j] - hx[i], ey = hy[j] - hy[i];
                    // Outward normal of an anticlockwise edge: (ey, -ex).
                    double num = ey * (ax - hx[i]) - ex * (ay - hy[i]);
                    double den = ey * dx - ex * dy;
                    if (Math.Abs(den) < 1e-14)
                    {
                        if (num > 0)
                        {
                            return false;
                        }

                        continue;
                    }

                    double t = -num / den;
                    if (den < 0)
                    {
                        if (t > enter)
                        {
                            enter = t;
                        }
                    }
                    else if (t < leave)
                    {
                        leave = t;
                    }

                    if (enter > leave)
                    {
                        return false;
                    }
                }

                return true;
            }

            private void Add(double ax, double ay, double bx, double by, byte cls, double reach)
            {
                if (Math.Max(ax, bx) < _minX - reach || Math.Min(ax, bx) > _maxX + reach
                    || Math.Max(ay, by) < _minY - reach || Math.Min(ay, by) > _maxY + reach)
                {
                    return;
                }

                int index = _ax.Count;
                _ax.Add(ax);
                _ay.Add(ay);
                _bx.Add(bx);
                _by.Add(by);
                _class.Add(cls);

                // Register the piece in every grid cell it passes through (sampled; queries are padded to match).
                double length = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                int parts = Math.Max(1, (int)Math.Ceiling(length / 0.4));
                int lastCell = -1;
                for (int i = 0; i <= parts; i++)
                {
                    double t = (double)i / parts;
                    int cx = (int)Math.Floor((ax + (bx - ax) * t - _gridX) / _gridCell), cy = (int)Math.Floor((ay + (by - ay) * t - _gridY) / _gridCell);
                    if (cx < 0 || cy < 0 || cx >= _gridWidth || cy >= _gridHeight)
                    {
                        continue; // beyond anything the body can reach
                    }

                    int cell = cy * _gridWidth + cx;
                    if (cell == lastCell)
                    {
                        continue;
                    }

                    lastCell = cell;
                    List<int> list = _grid[cell] ??= new List<int>();
                    if (list.Count == 0 || list[list.Count - 1] != index)
                    {
                        list.Add(index);
                    }
                }
            }

            /// <summary>
            /// The body enlarged by the margin, as an anticlockwise convex outline of straight edges that
            /// stands just outside the true shape: its curved ends and rounded corners, and the round
            /// the margin makes at each corner, are each wrapped in tangents.
            /// </summary>
            private static void Outline(DesignVehicle v, double margin, out double[] xs, out double[] ys)
            {
                var (pts, bulges) = v.BodyOutline();
                int n = pts.Count;
                var ox = new List<double>();
                var oy = new List<double>();

                void Emit(double x, double y)
                {
                    if (ox.Count > 0 && Math.Abs(x - ox[ox.Count - 1]) < 1e-9 && Math.Abs(y - oy[oy.Count - 1]) < 1e-9)
                    {
                        return;
                    }

                    ox.Add(x);
                    oy.Add(y);
                }

                // An anticlockwise arc: its start, the corners where its tangents meet, its end.
                void Arc(double cx, double cy, double r, double from, double sweep)
                {
                    Emit(cx + r * Math.Cos(from), cy + r * Math.Sin(from));
                    if (r < 1e-9 || sweep < 1e-9)
                    {
                        return;
                    }

                    double part = 2 * Math.Acos(r / (r + OutlineTolerance));
                    int k = Math.Max(1, (int)Math.Ceiling(sweep / part));
                    double reach = r / Math.Cos(sweep / (2 * k));
                    for (int i = 0; i < k; i++)
                    {
                        double a = from + sweep * (i + 0.5) / k;
                        Emit(cx + reach * Math.Cos(a), cy + reach * Math.Sin(a));
                    }

                    Emit(cx + r * Math.Cos(from + sweep), cy + r * Math.Sin(from + sweep));
                }

                // The direction the outline faces (its outward normal) at the start and end of each side.
                var startNormal = new double[n];
                var endNormal = new double[n];
                var arcs = new ArcSegment?[n];
                for (int i = 0; i < n; i++)
                {
                    VehiclePoint a = pts[i], b = pts[(i + 1) % n];
                    if (Math.Abs(bulges[i]) < 1e-12)
                    {
                        startNormal[i] = endNormal[i] = Math.Atan2(b.Y - a.Y, b.X - a.X) - Math.PI / 2;
                    }
                    else
                    {
                        ArcSegment arc = ArcSegment.FromBulge(new Point2(a.X, a.Y), new Point2(b.X, b.Y), bulges[i]);
                        arcs[i] = arc;
                        startNormal[i] = arc.StartAngle;
                        endNormal[i] = arc.StartAngle + arc.Sweep;
                    }
                }

                for (int i = 0; i < n; i++)
                {
                    VehiclePoint a = pts[i], b = pts[(i + 1) % n];

                    // Where the outline turns a corner here, the margin goes round it.
                    double before = endNormal[(i + n - 1) % n];
                    double turn = Angles.Normalize(startNormal[i] - before);
                    if (turn > 1e-6 && turn < Math.PI)
                    {
                        Arc(a.X, a.Y, margin, before, turn);
                    }

                    if (arcs[i] is ArcSegment arc)
                    {
                        Arc(arc.Center.X, arc.Center.Y, arc.Radius + margin, arc.StartAngle, arc.Sweep);
                    }
                    else
                    {
                        double nx = Math.Cos(startNormal[i]), ny = Math.Sin(startNormal[i]);
                        Emit(a.X + margin * nx, a.Y + margin * ny);
                        Emit(b.X + margin * nx, b.Y + margin * ny);
                    }
                }

                xs = ox.ToArray();
                ys = oy.ToArray();
            }
        }
    }
}
