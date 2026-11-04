using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CsdBridge.Core.Civil
{
    /// <summary>A chainage the design must have an IP at. Level null: the designer chooses the level.</summary>
    public sealed class FixedIp
    {
        public FixedIp(double chainage, double? level)
        {
            Chainage = chainage;
            Level = level;
        }

        public double Chainage { get; }
        public double? Level { get; }
    }

    public sealed class ProfileDesignOptions
    {
        /// <summary>Design range; defaults to the ground profile's range.</summary>
        public double? Start { get; set; }
        public double? End { get; set; }

        /// <summary>Tie the start/end into the ground (or StartLevel/EndLevel when given).</summary>
        public bool TieStart { get; set; } = true;
        public bool TieEnd { get; set; } = true;
        public double? StartLevel { get; set; }
        public double? EndLevel { get; set; }

        /// <summary>IPs the design must keep (intersections, tie-ins, existing kerb levels...).</summary>
        public List<FixedIp> Fixed { get; set; } = new List<FixedIp>();

        public double MaxGradePct { get; set; } = 10;

        /// <summary>Minimum absolute grade (e.g. 0.5 % for kerb drainage). 0 = none.</summary>
        public double MinGradePct { get; set; }

        /// <summary>Largest allowed change of grade at an IP, in %. Null = no limit.</summary>
        public double? MaxGradeChangePct { get; set; }

        public double MinIpSpacing { get; set; } = 20;

        /// <summary>Most IPs the designer may add between the start, end and fixed IPs.</summary>
        public int MaxIps { get; set; } = 8;

        /// <summary>Only add (or keep) an IP when it lowers the centreline RMS cut/fill depth by at least this much (m).</summary>
        public double MinImprovement { get; set; } = 0.05;

        /// <summary>IPs are placed on multiples of this chainage step (m).</summary>
        public double ChainageStep { get; set; } = 5;

        /// <summary>Design target = ground + offset (e.g. a small fill to keep the road above ground).</summary>
        public double TargetOffset { get; set; }

        /// <summary>Make the centreline fill area equal the cut area (a rough balance, not a volume balance).</summary>
        public bool BalanceCentreline { get; set; }

        public double MinVcLength { get; set; }

        /// <summary>VC length = K × |grade change %| (then at least MinVcLength).</summary>
        public double? KValue { get; set; }
    }

    public sealed class ProfileDesignStep
    {
        public int AddedIps { get; set; }
        public double Rms { get; set; }
        public double MaxCut { get; set; }
        public double MaxFill { get; set; }
    }

    public sealed class ProfileDesignResult
    {
        public List<ProfileIp> Ips { get; set; } = new List<ProfileIp>();

        /// <summary>Centreline cut/fill of the final design including vertical curves.</summary>
        public CutFillStats Stats { get; set; } = new CutFillStats();

        /// <summary>How the fit improved as IPs were added (before refinement).</summary>
        public List<ProfileDesignStep> History { get; set; } = new List<ProfileDesignStep>();

        public List<string> Notes { get; set; } = new List<string>();

        public bool ConstraintsMet { get; set; }
    }

    /// <summary>
    /// Designs a vertical profile against a ground long section. Priorities, in order:
    /// 1. meet the constraints (grades, grade change, IP spacing, fixed IPs);
    /// 2. use as few IPs as possible: an IP is only added (and kept) when it lowers the
    ///    centreline RMS cut/fill by at least <see cref="ProfileDesignOptions.MinImprovement"/>;
    /// 3. within that, minimise centreline cut/fill (least squares, exactly integrated).
    /// Grades are fitted on tangents; vertical curves are sized afterwards.
    /// </summary>
    public static class ProfileDesigner
    {
        private const double FeasibleTolerance = 1e-5;

        public static ProfileDesignResult Design(GroundProfile ground, ProfileDesignOptions o)
        {
            var result = new ProfileDesignResult();
            double start = o.Start ?? ground.Start;
            double end = o.End ?? ground.End;
            if (start < ground.Start - 1e-6 || end > ground.End + 1e-6)
            {
                result.Notes.Add($"The ground profile only covers ch {F(ground.Start)} to {F(ground.End)}; the design range was trimmed to it.");
                start = Math.Max(start, ground.Start);
                end = Math.Min(end, ground.End);
            }

            if (end - start < 2 * Math.Max(o.ChainageStep, 1e-3))
            {
                throw new ArgumentException("The design range is too short.");
            }

            var ctx = new Context(ground, o, start, end);

            // Start, end and fixed IPs.
            var nodes = new List<Node>
            {
                new Node(start, o.TieStart ? o.StartLevel ?? ground.LevelAt(start) + o.TargetOffset : o.StartLevel, true),
                new Node(end, o.TieEnd ? o.EndLevel ?? ground.LevelAt(end) + o.TargetOffset : o.EndLevel, true),
            };
            foreach (FixedIp f in o.Fixed)
            {
                if (f.Chainage <= start + 1e-6 || f.Chainage >= end - 1e-6)
                {
                    Node endNode = Math.Abs(f.Chainage - start) < Math.Abs(f.Chainage - end) ? nodes[0] : nodes[1];
                    if (f.Level.HasValue)
                    {
                        endNode.Level = f.Level;
                    }

                    continue;
                }

                nodes.Add(new Node(f.Chainage, f.Level, true));
            }

            nodes = nodes.OrderBy(n => n.Chainage).ToList();
            for (int i = 1; i < nodes.Count; i++)
            {
                if (nodes[i].Chainage - nodes[i - 1].Chainage < o.MinIpSpacing - 1e-6)
                {
                    result.Notes.Add($"Fixed IPs at {F(nodes[i - 1].Chainage)} and {F(nodes[i].Chainage)} are closer than the minimum IP spacing ({F(o.MinIpSpacing)} m).");
                }
            }

            Solution current = ctx.Solve(nodes);
            result.History.Add(Step(0, current, ctx, nodes));
            if (!current.Feasible)
            {
                result.Notes.Add("The constraints can't all be met with straight grades between the fixed IPs; IPs were added to get closer.");
            }

            // 1. Add IPs one at a time where they help most.
            int added = 0;
            while (added < o.MaxIps)
            {
                List<double> candidates = ctx.Candidates(nodes).ToList();
                if (candidates.Count == 0)
                {
                    break;
                }

                // Rank cheaply without inequality constraints, then solve the best few properly.
                var ranked = candidates
                    .Select(c => new { c, rms = ctx.UnconstrainedRms(WithNode(nodes, c)) })
                    .OrderBy(t => t.rms)
                    .Take(4)
                    .Select(t => new { t.c, sol = ctx.Solve(WithNode(nodes, t.c)) })
                    .OrderBy(t => t.sol.Feasible ? 0 : 1)
                    .ThenBy(t => t.sol.Feasible ? t.sol.Rms : t.sol.Violation)
                    .First();

                bool fixesFeasibility = !current.Feasible && (ranked.sol.Feasible || ranked.sol.Violation < current.Violation - 1e-6);
                if (!fixesFeasibility && current.Rms - ranked.sol.Rms < o.MinImprovement)
                {
                    break;
                }

                nodes = WithNode(nodes, ranked.c);
                current = ranked.sol;
                added++;
                result.History.Add(Step(added, current, ctx, nodes));
            }

            // 2. Slide the added IPs to better chainages.
            for (int pass = 0; pass < 4; pass++)
            {
                bool improved = false;
                for (int i = 1; i < nodes.Count - 1; i++)
                {
                    if (nodes[i].Locked)
                    {
                        continue;
                    }

                    List<Node>? best = null;
                    Solution? bestSol = null;
                    foreach (int k in new[] { -4, -2, -1, 1, 2, 4 })
                    {
                        double c = nodes[i].Chainage + k * o.ChainageStep;
                        if (c - nodes[i - 1].Chainage < o.MinIpSpacing - 1e-6 || nodes[i + 1].Chainage - c < o.MinIpSpacing - 1e-6)
                        {
                            continue;
                        }

                        var trial = nodes.Select(n => n.Clone()).ToList();
                        trial[i].Chainage = c;
                        Solution s = ctx.Solve(trial);
                        if (Better(s, bestSol ?? current))
                        {
                            best = trial;
                            bestSol = s;
                        }
                    }

                    if (best != null && bestSol != null)
                    {
                        nodes = best;
                        current = bestSol;
                        improved = true;
                    }
                }

                if (!improved)
                {
                    break;
                }
            }

            // 3. Drop added IPs that no longer earn their place.
            bool removed = true;
            while (removed)
            {
                removed = false;
                for (int i = 1; i < nodes.Count - 1; i++)
                {
                    if (nodes[i].Locked)
                    {
                        continue;
                    }

                    var trial = nodes.Where((_, j) => j != i).Select(n => n.Clone()).ToList();
                    Solution s = ctx.Solve(trial);
                    if (s.Feasible && (current.Feasible || s.Violation <= current.Violation) && s.Rms - current.Rms < o.MinImprovement)
                    {
                        nodes = trial;
                        current = s;
                        removed = true;
                        break;
                    }
                }
            }

            // Size the vertical curves, then refit the levels with the curves in place (the
            // design under a VC is still linear in the IP levels), and size them again.
            double[] chainages = nodes.Select(n => n.Chainage).ToArray();
            var levels = current.X.Select(v => Math.Round(v, 3)).ToArray();
            List<ProfileIp> ips = SizeCurves(chainages, levels, o, new List<string>());
            for (int pass = 0; pass < 2 && ips.Any(ip => ip.VcLength > 0); pass++)
            {
                Solution withCurves = ctx.Solve(nodes, ctx.BuildWithCurves(chainages, ips.Select(ip => ip.VcLength).ToArray()));
                if (!withCurves.Feasible && current.Feasible)
                {
                    break;
                }

                current = withCurves;
                levels = current.X.Select(v => Math.Round(v, 3)).ToArray();
                ips = SizeCurves(chainages, levels, o, new List<string>());
            }

            result.Ips = SizeCurves(chainages, levels, o, result.Notes);
            result.ConstraintsMet = current.Feasible;
            if (!current.Feasible)
            {
                result.Notes.Add($"Not every constraint could be met (largest miss {F(current.Violation * 100)} % of grade or m of level). Relax the grade limits, IP spacing or fixed levels.");
            }

            result.Stats = VerticalProfile.CutFill(c => VerticalProfile.LevelAt(result.Ips, c), ground, start, end, o.TargetOffset);
            return result;
        }

        /// <summary>Size symmetric VCs at the interior IPs from the K value / minimum length, within the space available.</summary>
        internal static List<ProfileIp> SizeCurves(double[] chainages, double[] levels, ProfileDesignOptions o, List<string> notes)
        {
            var ips = new List<ProfileIp>();
            int n = chainages.Length;
            for (int i = 0; i < n; i++)
            {
                double vc = 0;
                if (i > 0 && i < n - 1)
                {
                    double gIn = (levels[i] - levels[i - 1]) / (chainages[i] - chainages[i - 1]);
                    double gOut = (levels[i + 1] - levels[i]) / (chainages[i + 1] - chainages[i]);
                    double aPct = Math.Abs(gOut - gIn) * 100;
                    if (aPct >= 0.05)
                    {
                        double wanted = Math.Max(o.KValue.HasValue ? o.KValue.Value * aPct : 0, o.MinVcLength);
                        double room = Math.Min(chainages[i] - chainages[i - 1], chainages[i + 1] - chainages[i]);
                        vc = Math.Min(Math.Ceiling(wanted - 1e-9), Math.Floor(room));
                        if (vc < wanted - 1e-6)
                        {
                            notes.Add($"VC at ch {F(chainages[i])} limited to {F(vc)} m by the IP spacing (wanted {F(Math.Ceiling(wanted))} m).");
                        }
                    }
                }

                ips.Add(new ProfileIp(chainages[i], levels[i], vc));
            }

            return ips;
        }

        private static bool Better(Solution s, Solution than)
        {
            if (s.Feasible != than.Feasible)
            {
                return s.Feasible;
            }

            return s.Feasible ? s.Rms < than.Rms - 1e-6 : s.Violation < than.Violation - 1e-9;
        }

        private static ProfileDesignStep Step(int added, Solution s, Context ctx, List<Node> nodes)
        {
            var ips = nodes.Select((n, i) => new ProfileIp(n.Chainage, s.X[i])).ToList();
            CutFillStats st = VerticalProfile.CutFill(c => VerticalProfile.LevelAt(ips, c), ctx.Ground, ctx.Start, ctx.End, ctx.Options.TargetOffset);
            return new ProfileDesignStep { AddedIps = added, Rms = Math.Round(st.Rms, 3), MaxCut = Math.Round(st.MaxCut, 3), MaxFill = Math.Round(st.MaxFill, 3) };
        }

        private static List<Node> WithNode(List<Node> nodes, double chainage)
        {
            var list = nodes.Select(n => n.Clone()).ToList();
            list.Add(new Node(chainage, null, false));
            return list.OrderBy(n => n.Chainage).ToList();
        }

        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private sealed class Node
        {
            public Node(double chainage, double? level, bool locked)
            {
                Chainage = chainage;
                Level = level;
                Locked = locked;
            }

            public double Chainage { get; set; }

            /// <summary>Fixed level, or null when the designer chooses it.</summary>
            public double? Level { get; set; }

            /// <summary>Start, end or a fixed IP: never moved or removed.</summary>
            public bool Locked { get; }

            public Node Clone() => new Node(Chainage, Level, Locked);
        }

        private sealed class Solution
        {
            public double[] X { get; set; } = Array.Empty<double>();
            public double Rms { get; set; }
            public double Violation { get; set; }
            public bool Feasible => Violation <= FeasibleTolerance;
        }

        /// <summary>The least-squares objective and constraints for a set of IP chainages.</summary>
        private sealed class Context
        {
            public Context(GroundProfile ground, ProfileDesignOptions options, double start, double end)
            {
                Ground = ground;
                Options = options;
                Start = start;
                End = end;
            }

            public GroundProfile Ground { get; }
            public ProfileDesignOptions Options { get; }
            public double Start { get; }
            public double End { get; }

            public IEnumerable<double> Candidates(List<Node> nodes)
            {
                double step = Options.ChainageStep;
                double first = Math.Ceiling((Start + 1e-6) / step) * step;
                for (double c = first; c < End - 1e-6; c += step)
                {
                    double cc = Math.Round(c, 6);
                    if (nodes.All(n => Math.Abs(n.Chainage - cc) >= Options.MinIpSpacing - 1e-6))
                    {
                        yield return cc;
                    }
                }
            }

            public double UnconstrainedRms(List<Node> nodes)
            {
                Objective obj = Build(nodes);
                double[]? x = SolveEqualityOnly(obj, nodes);
                return x == null ? double.PositiveInfinity : Math.Sqrt(Math.Max(0, obj.Value(x)));
            }

            public Solution Solve(List<Node> nodes, Objective? objective = null)
            {
                Objective obj = objective ?? Build(nodes);
                double[] x0 = SolveEqualityOnly(obj, nodes) ?? nodes.Select(n => Ground.LevelAt(n.Chainage)).ToArray();
                int n = nodes.Count;
                var signs = new int[n - 1];
                Solution best = SolveWithSigns(nodes, obj, signs, x0);
                if (Options.MinGradePct <= 0)
                {
                    return best;
                }

                // Grades flatter than the minimum: commit each to a direction and re-solve.
                // A flat grade has no preferred direction, so those alternate (up, down, up...)
                // and, if that doesn't work, each choice is flipped in turn.
                double gMin = Options.MinGradePct / 100;
                for (int round = 0; round < 4; round++)
                {
                    var assigned = new List<int>();
                    int alternate = 1;
                    for (int j = 0; j < n - 1; j++)
                    {
                        if (signs[j] != 0)
                        {
                            continue;
                        }

                        double g = (best.X[j + 1] - best.X[j]) / (nodes[j + 1].Chainage - nodes[j].Chainage);
                        if (Math.Abs(g) < gMin - 1e-9)
                        {
                            if (Math.Abs(g) > 1e-6)
                            {
                                signs[j] = Math.Sign(g);
                            }
                            else
                            {
                                signs[j] = alternate;
                                alternate = -alternate;
                            }

                            assigned.Add(j);
                        }
                    }

                    if (assigned.Count == 0)
                    {
                        break;
                    }

                    Solution s = SolveWithSigns(nodes, obj, signs, best.X);
                    foreach (int j in assigned)
                    {
                        if (s.Feasible)
                        {
                            break;
                        }

                        signs[j] = -signs[j];
                        Solution flipped = SolveWithSigns(nodes, obj, signs, best.X);
                        if (flipped.Violation < s.Violation - 1e-9)
                        {
                            s = flipped;
                        }
                        else
                        {
                            signs[j] = -signs[j];
                        }
                    }

                    best = s;
                }

                return best;
            }

            private Solution SolveWithSigns(List<Node> nodes, Objective obj, int[] signs, double[] x0)
            {
                BuildConstraints(nodes, obj, signs, out var rows, out var lo, out var hi);
                QpSolver.Result r = QpSolver.Solve(obj.P, obj.Q, rows, lo.ToArray(), hi.ToArray(), x0);
                return new Solution { X = r.X, Rms = Math.Sqrt(Math.Max(0, obj.Value(r.X))), Violation = r.MaxViolation };
            }

            private void BuildConstraints(List<Node> nodes, Objective obj, int[] signs, out List<double[]> rows, out List<double> lo, out List<double> hi)
            {
                int n = nodes.Count;
                rows = new List<double[]>();
                lo = new List<double>();
                hi = new List<double>();
                double gMax = Options.MaxGradePct / 100;
                double gMin = Math.Max(0, Options.MinGradePct / 100);

                for (int i = 0; i < n; i++)
                {
                    if (nodes[i].Level is double level)
                    {
                        var row = new double[n];
                        row[i] = 1;
                        rows.Add(row);
                        lo.Add(level);
                        hi.Add(level);
                    }
                }

                for (int j = 0; j < n - 1; j++)
                {
                    double d = nodes[j + 1].Chainage - nodes[j].Chainage;
                    var row = new double[n];
                    row[j] = -1 / d;
                    row[j + 1] = 1 / d;
                    rows.Add(row);
                    lo.Add(signs[j] > 0 ? gMin : -gMax);
                    hi.Add(signs[j] < 0 ? -gMin : gMax);
                }

                if (Options.MaxGradeChangePct is double maxChange)
                {
                    double dg = maxChange / 100;
                    for (int i = 1; i < n - 1; i++)
                    {
                        double d1 = nodes[i].Chainage - nodes[i - 1].Chainage;
                        double d2 = nodes[i + 1].Chainage - nodes[i].Chainage;
                        var row = new double[n];
                        row[i - 1] = 1 / d1;
                        row[i] = -1 / d1 - 1 / d2;
                        row[i + 1] = 1 / d2;
                        rows.Add(row);
                        lo.Add(-dg);
                        hi.Add(dg);
                    }
                }

                if (Options.BalanceCentreline)
                {
                    rows.Add(obj.MeanRow);
                    lo.Add(obj.MeanTarget);
                    hi.Add(obj.MeanTarget);
                }
            }

            private double[]? SolveEqualityOnly(Objective obj, List<Node> nodes)
            {
                int n = nodes.Count;
                var free = Enumerable.Range(0, n).Where(i => nodes[i].Level == null).ToList();
                var x = nodes.Select(nd => nd.Level ?? 0).ToArray();
                if (free.Count == 0)
                {
                    return x;
                }

                var k = new double[free.Count, free.Count];
                var b = new double[free.Count];
                for (int a = 0; a < free.Count; a++)
                {
                    int i = free[a];
                    b[a] = -obj.Q[i];
                    for (int j = 0; j < n; j++)
                    {
                        if (nodes[j].Level is double fixedLevel)
                        {
                            b[a] -= obj.P[i, j] * fixedLevel;
                        }
                    }

                    for (int c = 0; c < free.Count; c++)
                    {
                        k[a, c] = obj.P[i, free[c]];
                    }

                    k[a, a] += 1e-9;
                }

                double[]? xf = QpSolver.SolveSpd(k, b);
                if (xf == null)
                {
                    return null;
                }

                for (int a = 0; a < free.Count; a++)
                {
                    x[free[a]] = xf[a];
                }

                return x;
            }

            /// <summary>
            /// The same objective for a design with vertical curves: the level anywhere is a
            /// linear combination of the IP levels, found by evaluating unit profiles. Integrated
            /// with the trapezoid rule on a fine grid plus every ground point.
            /// </summary>
            public Objective BuildWithCurves(double[] chainages, double[] vcLengths)
            {
                int n = chainages.Length;
                var unit = new List<ProfileIp>[n];
                for (int j = 0; j < n; j++)
                {
                    unit[j] = Enumerable.Range(0, n).Select(i => new ProfileIp(chainages[i], i == j ? 1 : 0, vcLengths[i])).ToList();
                }

                var samples = new SortedSet<double>(chainages);
                foreach (double g in Ground.ChainagesBetween(Start, End))
                {
                    samples.Add(g);
                }

                double length = End - Start;
                int steps = (int)Math.Min(4000, Math.Ceiling(length));
                for (int k = 1; k < steps; k++)
                {
                    samples.Add(Start + length * k / steps);
                }

                double[] cs = samples.ToArray();
                var p = new double[n, n];
                var q = new double[n];
                double c0 = 0, meanTarget = 0;
                var meanRow = new double[n];
                var phi = new double[n];
                for (int k = 0; k < cs.Length; k++)
                {
                    double w = ((k > 0 ? cs[k] - cs[k - 1] : 0) + (k < cs.Length - 1 ? cs[k + 1] - cs[k] : 0)) / 2 / length;
                    double t = Ground.LevelAt(cs[k]) + Options.TargetOffset;
                    for (int j = 0; j < n; j++)
                    {
                        phi[j] = VerticalProfile.LevelAt(unit[j], cs[k]);
                    }

                    for (int i = 0; i < n; i++)
                    {
                        if (phi[i] == 0)
                        {
                            continue;
                        }

                        for (int j = 0; j < n; j++)
                        {
                            p[i, j] += 2 * w * phi[i] * phi[j];
                        }

                        q[i] -= 2 * w * t * phi[i];
                        meanRow[i] += w * phi[i];
                    }

                    c0 += w * t * t;
                    meanTarget += w * t;
                }

                for (int i = 0; i < n; i++)
                {
                    p[i, i] += 1e-9;
                }

                return new Objective(p, q, c0, meanRow, meanTarget);
            }

            /// <summary>(1/L)∫(design − target)² as ½xᵀPx + qᵀx + c, exact for straight grades.</summary>
            public Objective Build(List<Node> nodes)
            {
                int n = nodes.Count;
                double[] c = nodes.Select(nd => nd.Chainage).ToArray();
                var breaks = new SortedSet<double>(c);
                foreach (double g in Ground.ChainagesBetween(Start, End))
                {
                    breaks.Add(g);
                }

                double length = End - Start;
                var p = new double[n, n];
                var q = new double[n];
                double c0 = 0;
                var meanRow = new double[n];
                double meanTarget = 0;

                double[] bs = breaks.ToArray();
                var idx = new int[bs.Length];
                var t = new double[bs.Length];
                var target = new double[bs.Length];
                int seg = 0;
                for (int k = 0; k < bs.Length; k++)
                {
                    while (seg < n - 2 && bs[k] > c[seg + 1] + 1e-9)
                    {
                        seg++;
                    }

                    idx[k] = seg;
                    t[k] = (bs[k] - c[seg]) / (c[seg + 1] - c[seg]);
                    target[k] = Ground.LevelAt(bs[k]) + Options.TargetOffset;
                }

                for (int k = 0; k < bs.Length - 1; k++)
                {
                    double h = bs[k + 1] - bs[k];
                    if (h <= 0)
                    {
                        continue;
                    }

                    // Mass matrix h/6 [[2,1],[1,2]] on the two end values.
                    int[] pts = { k, k + 1 };
                    double[,] mass = { { h / 3, h / 6 }, { h / 6, h / 3 } };
                    for (int r = 0; r < 2; r++)
                    {
                        int kr = pts[r];
                        for (int s = 0; s < 2; s++)
                        {
                            int ks = pts[s];
                            double m = mass[r, s] * 2 / length;
                            AddOuter(p, idx[kr], t[kr], idx[ks], t[ks], m);
                            q[idx[kr]] -= m * (1 - t[kr]) * target[ks];
                            q[idx[kr] + 1] -= m * t[kr] * target[ks];
                            c0 += m / 2 * target[kr] * target[ks];
                        }

                        // Mean depth row: ∫ design / L.
                        double w = h / 2 / length;
                        meanRow[idx[kr]] += w * (1 - t[kr]);
                        meanRow[idx[kr] + 1] += w * t[kr];
                        meanTarget += w * target[kr];
                    }
                }

                for (int i = 0; i < n; i++)
                {
                    p[i, i] += 1e-9;
                }

                return new Objective(p, q, c0, meanRow, meanTarget);
            }

            private static void AddOuter(double[,] p, int i, double ti, int j, double tj, double m)
            {
                double ai0 = 1 - ti, ai1 = ti, aj0 = 1 - tj, aj1 = tj;
                p[i, j] += m * ai0 * aj0;
                p[i, j + 1] += m * ai0 * aj1;
                p[i + 1, j] += m * ai1 * aj0;
                p[i + 1, j + 1] += m * ai1 * aj1;
            }
        }

        private sealed class Objective
        {
            public Objective(double[,] p, double[] q, double c0, double[] meanRow, double meanTarget)
            {
                P = p;
                Q = q;
                C0 = c0;
                MeanRow = meanRow;
                MeanTarget = meanTarget;
            }

            public double[,] P { get; }
            public double[] Q { get; }
            public double C0 { get; }
            public double[] MeanRow { get; }
            public double MeanTarget { get; }

            public double Value(double[] x)
            {
                int n = x.Length;
                double v = C0;
                for (int i = 0; i < n; i++)
                {
                    double px = 0;
                    for (int j = 0; j < n; j++)
                    {
                        px += P[i, j] * x[j];
                    }

                    v += 0.5 * x[i] * px + Q[i] * x[i];
                }

                return v;
            }
        }
    }
}
