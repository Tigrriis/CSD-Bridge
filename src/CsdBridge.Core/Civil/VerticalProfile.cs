using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CsdBridge.Core.Civil
{
    /// <summary>A ground (existing surface) level at a chainage.</summary>
    public readonly struct GroundPoint
    {
        public GroundPoint(double chainage, double level)
        {
            Chainage = chainage;
            Level = level;
        }

        public double Chainage { get; }
        public double Level { get; }
    }

    /// <summary>A ground long section along a centreline, sorted by chainage.</summary>
    public sealed class GroundProfile
    {
        private readonly GroundPoint[] _points;

        public GroundProfile(IEnumerable<GroundPoint> points)
        {
            _points = points
                .Where(p => !double.IsNaN(p.Chainage) && !double.IsNaN(p.Level) && !double.IsInfinity(p.Level))
                .OrderBy(p => p.Chainage)
                .ToArray();

            // Drop duplicate chainages (keep the first).
            var unique = new List<GroundPoint>(_points.Length);
            foreach (GroundPoint p in _points)
            {
                if (unique.Count == 0 || p.Chainage - unique[unique.Count - 1].Chainage > 1e-6)
                {
                    unique.Add(p);
                }
            }

            _points = unique.ToArray();
            if (_points.Length < 2)
            {
                throw new ArgumentException("A ground profile needs at least 2 points.");
            }
        }

        public IReadOnlyList<GroundPoint> Points => _points;

        public double Start => _points[0].Chainage;

        public double End => _points[_points.Length - 1].Chainage;

        /// <summary>Ground level at a chainage (linear between points, clamped at the ends).</summary>
        public double LevelAt(double chainage)
        {
            if (chainage <= _points[0].Chainage)
            {
                return _points[0].Level;
            }

            if (chainage >= _points[_points.Length - 1].Chainage)
            {
                return _points[_points.Length - 1].Level;
            }

            int lo = 0, hi = _points.Length - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (_points[mid].Chainage <= chainage)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            GroundPoint a = _points[lo], b = _points[hi];
            double t = (chainage - a.Chainage) / (b.Chainage - a.Chainage);
            return a.Level + t * (b.Level - a.Level);
        }

        /// <summary>Chainages of the ground points strictly between two chainages.</summary>
        public IEnumerable<double> ChainagesBetween(double from, double to) =>
            _points.Where(p => p.Chainage > from && p.Chainage < to).Select(p => p.Chainage);
    }

    /// <summary>
    /// Ground profiles sampled in this session, so tools can pass a short id instead of
    /// hundreds of levels through the conversation.
    /// </summary>
    public static class GroundProfileStore
    {
        private static readonly ConcurrentDictionary<string, GroundProfile> Profiles = new ConcurrentDictionary<string, GroundProfile>(StringComparer.OrdinalIgnoreCase);
        private static int _next;

        public static string Add(GroundProfile profile)
        {
            string id = "ground" + Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Profiles[id] = profile;
            return id;
        }

        public static GroundProfile? Get(string id) => Profiles.TryGetValue(id, out GroundProfile? p) ? p : null;
    }

    /// <summary>An intersection point (IP) of a vertical design: chainage, level and vertical curve length.</summary>
    public sealed class ProfileIp
    {
        public ProfileIp(double chainage, double level, double vcLength = 0)
        {
            Chainage = chainage;
            Level = level;
            VcLength = vcLength;
        }

        public double Chainage { get; }
        public double Level { get; }

        /// <summary>Total length of the symmetric parabolic vertical curve centred on the IP (0 = none).</summary>
        public double VcLength { get; }
    }

    /// <summary>Centreline cut/fill of a design against the ground. Depth = design − ground: positive is fill.</summary>
    public sealed class CutFillStats
    {
        public double Rms { get; set; }
        public double MeanAbs { get; set; }
        public double MaxCut { get; set; }
        public double MaxCutChainage { get; set; }
        public double MaxFill { get; set; }
        public double MaxFillChainage { get; set; }

        /// <summary>∫ fill depth along the centreline (m·m). A rough balance indicator, not a volume.</summary>
        public double FillArea { get; set; }

        /// <summary>∫ cut depth along the centreline (positive number).</summary>
        public double CutArea { get; set; }

        /// <summary>FillArea − CutArea.</summary>
        public double NetArea => FillArea - CutArea;
    }

    /// <summary>Vertical alignment maths: levels with parabolic vertical curves, grades and centreline cut/fill.</summary>
    public static class VerticalProfile
    {
        /// <summary>Grade (as a fraction) of the tangent from IP i to IP i+1.</summary>
        public static double Grade(IReadOnlyList<ProfileIp> ips, int i) =>
            (ips[i + 1].Level - ips[i].Level) / (ips[i + 1].Chainage - ips[i].Chainage);

        /// <summary>Design level at a chainage, with symmetric parabolic vertical curves at the IPs.</summary>
        public static double LevelAt(IReadOnlyList<ProfileIp> ips, double chainage)
        {
            if (ips.Count == 0)
            {
                throw new ArgumentException("No IPs.");
            }

            if (ips.Count == 1)
            {
                return ips[0].Level;
            }

            // Inside a vertical curve?
            for (int i = 1; i < ips.Count - 1; i++)
            {
                ProfileIp ip = ips[i];
                double half = ip.VcLength / 2;
                if (half <= 0 || chainage < ip.Chainage - half || chainage > ip.Chainage + half)
                {
                    continue;
                }

                double g1 = Grade(ips, i - 1);
                double g2 = Grade(ips, i);
                double bvc = ip.Chainage - half;
                double zBvc = ip.Level - g1 * half;
                double x = chainage - bvc;
                return zBvc + g1 * x + (g2 - g1) / (2 * ip.VcLength) * x * x;
            }

            // On a tangent.
            int seg = 0;
            while (seg < ips.Count - 2 && chainage > ips[seg + 1].Chainage)
            {
                seg++;
            }

            ProfileIp a = ips[seg], b = ips[seg + 1];
            return a.Level + (chainage - a.Chainage) * (b.Level - a.Level) / (b.Chainage - a.Chainage);
        }

        /// <summary>
        /// Cut/fill of a design (any level function) against the ground from start to end,
        /// integrated with the trapezoid rule on a fine grid that includes every ground point.
        /// </summary>
        public static CutFillStats CutFill(Func<double, double> design, GroundProfile ground, double start, double end, double targetOffset = 0, double step = 0.5)
        {
            var chainages = new SortedSet<double> { start, end };
            foreach (double c in ground.ChainagesBetween(start, end))
            {
                chainages.Add(c);
            }

            int n = (int)Math.Ceiling((end - start) / step);
            for (int i = 1; i < n; i++)
            {
                chainages.Add(start + (end - start) * i / n);
            }

            var stats = new CutFillStats();
            double sumSq = 0, sumAbs = 0, length = end - start;
            double prevC = double.NaN, prevD = 0;
            foreach (double c in chainages)
            {
                double d = design(c) - (ground.LevelAt(c) + targetOffset);
                if (d > stats.MaxFill)
                {
                    stats.MaxFill = d;
                    stats.MaxFillChainage = c;
                }

                if (-d > stats.MaxCut)
                {
                    stats.MaxCut = -d;
                    stats.MaxCutChainage = c;
                }

                if (!double.IsNaN(prevC))
                {
                    double h = c - prevC;
                    sumSq += h * (prevD * prevD + prevD * d + d * d) / 3;
                    sumAbs += h * AbsIntegral(prevD, d);
                    stats.FillArea += h * PositiveIntegral(prevD, d);
                    stats.CutArea += h * PositiveIntegral(-prevD, -d);
                }

                prevC = c;
                prevD = d;
            }

            if (length > 0)
            {
                stats.Rms = Math.Sqrt(sumSq / length);
                stats.MeanAbs = sumAbs / length;
            }

            return stats;
        }

        /// <summary>K value (m per % grade change) of a VC: L / |A|. Infinity when the grades don't change.</summary>
        public static double KValue(double vcLength, double gradeChangePct) =>
            Math.Abs(gradeChangePct) < 1e-9 ? double.PositiveInfinity : vcLength / Math.Abs(gradeChangePct);

        // ∫0..1 |a + (b-a)t| dt
        private static double AbsIntegral(double a, double b) => PositiveIntegral(a, b) + PositiveIntegral(-a, -b);

        // ∫0..1 max(0, a + (b-a)t) dt
        private static double PositiveIntegral(double a, double b)
        {
            if (a >= 0 && b >= 0)
            {
                return (a + b) / 2;
            }

            if (a <= 0 && b <= 0)
            {
                return 0;
            }

            // One sign change: the positive part is a triangle.
            double pos = Math.Max(a, b);
            return pos * pos / (2 * Math.Abs(b - a));
        }
    }
}
