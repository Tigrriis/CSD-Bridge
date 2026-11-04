using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CsdBridge.Core.Civil;
using Xunit;

namespace CsdBridge.Core.Tests
{
    public class ProfileDesignTests
    {
        private static GroundProfile Ground(Func<double, double> level, double end = 400, double step = 2) =>
            new GroundProfile(Enumerable.Range(0, (int)(end / step) + 1).Select(i => new GroundPoint(i * step, level(i * step))));

        private static double Ridge(double c) => c <= 150 ? 10 + 0.04 * c : 16 - 0.02 * (c - 150);

        private static List<double> Grades(IReadOnlyList<ProfileIp> ips) =>
            Enumerable.Range(0, ips.Count - 1).Select(i => VerticalProfile.Grade(ips, i) * 100).ToList();

        [Fact]
        public void QpSolverRespectsBounds()
        {
            // minimise (x-3)^2 + (y+1)^2 subject to x + y <= 1, y >= 0
            var p = new double[,] { { 2, 0 }, { 0, 2 } };
            var q = new[] { -6.0, 2.0 };
            var rows = new List<double[]> { new[] { 1.0, 1.0 }, new[] { 0.0, 1.0 } };
            var r = QpSolver.Solve(p, q, rows, new[] { double.NegativeInfinity, 0 }, new[] { 1.0, double.PositiveInfinity });
            Assert.Equal(1.0, r.X[0], 4);
            Assert.Equal(0.0, r.X[1], 4);
            Assert.True(r.MaxViolation < 1e-6);
        }

        [Fact]
        public void VerticalCurveFollowsParabola()
        {
            var ips = new List<ProfileIp> { new ProfileIp(0, 0), new ProfileIp(100, 1, 40), new ProfileIp(200, 0) };
            Assert.Equal(0.8, VerticalProfile.LevelAt(ips, 80), 9);   // BVC
            Assert.Equal(0.9, VerticalProfile.LevelAt(ips, 100), 9);  // IP: e = A L / 8 = 0.02*40/8
            Assert.Equal(0.8, VerticalProfile.LevelAt(ips, 120), 9);  // EVC
            Assert.Equal(0.5, VerticalProfile.LevelAt(ips, 50), 9);   // tangent
            Assert.Equal(2.0, VerticalProfile.KValue(4, -2));
        }

        [Fact]
        public void CutFillIntegratesExactly()
        {
            GroundProfile ground = Ground(c => c / 100, end: 100, step: 10); // 0 .. 1
            CutFillStats s = VerticalProfile.CutFill(_ => 0.5, ground, 0, 100);
            Assert.Equal(0.5, s.MaxFill, 9);
            Assert.Equal(0.5, s.MaxCut, 9);
            Assert.Equal(12.5, s.FillArea, 6);
            Assert.Equal(12.5, s.CutArea, 6);
            Assert.Equal(0.25, s.MeanAbs, 6);
            Assert.Equal(Math.Sqrt(1.0 / 12), s.Rms, 6);
        }

        [Fact]
        public void FindsTheBreakInATwoGradeGround()
        {
            ProfileDesignResult r = ProfileDesigner.Design(Ground(Ridge), new ProfileDesignOptions { MaxGradePct = 8, MinIpSpacing = 20 });

            Assert.True(r.ConstraintsMet);
            Assert.Equal(3, r.Ips.Count);
            Assert.Equal(150, r.Ips[1].Chainage, 6);
            Assert.Equal(16, r.Ips[1].Level, 2);
            Assert.True(r.History[1].Rms < 0.01);
        }

        [Fact]
        public void MinImprovementKeepsTheDesignSimple()
        {
            // Small bumps on a straight grade: a high threshold should ignore them.
            GroundProfile ground = Ground(c => 10 + 0.01 * c + 0.15 * Math.Sin(c / 15));
            ProfileDesignResult simple = ProfileDesigner.Design(ground, new ProfileDesignOptions { MinImprovement = 0.2 });
            ProfileDesignResult detailed = ProfileDesigner.Design(ground, new ProfileDesignOptions { MinImprovement = 0.005, MinIpSpacing = 20 });

            Assert.Equal(2, simple.Ips.Count);
            Assert.True(detailed.Ips.Count > simple.Ips.Count);
            Assert.True(detailed.Stats.Rms < simple.Stats.Rms);
        }

        [Fact]
        public void RespectsMaxGrade()
        {
            ProfileDesignResult r = ProfileDesigner.Design(Ground(Ridge), new ProfileDesignOptions { MaxGradePct = 3, MinImprovement = 0.01 });

            Assert.True(r.ConstraintsMet);
            Assert.All(Grades(r.Ips), g => Assert.True(Math.Abs(g) <= 3.0005, $"grade {g}"));
        }

        [Fact]
        public void RespectsMinGradeOnFlatGround()
        {
            ProfileDesignResult r = ProfileDesigner.Design(Ground(_ => 5.0, end: 200), new ProfileDesignOptions { MinGradePct = 0.5, MinIpSpacing = 20 });

            Assert.True(r.ConstraintsMet);
            Assert.True(r.Ips.Count >= 3);
            Assert.All(Grades(r.Ips), g => Assert.True(Math.Abs(g) >= 0.4995, $"grade {g}"));
        }

        [Fact]
        public void BalanceMakesNetCentrelineAreaZero()
        {
            GroundProfile ground = Ground(c => 10 + 0.5 * Math.Sin(c / 40) + 0.3 * Math.Cos(c / 23));
            ProfileDesignResult r = ProfileDesigner.Design(ground, new ProfileDesignOptions
            {
                TieStart = false,
                TieEnd = false,
                BalanceCentreline = true,
                MinImprovement = 1, // keep it to a single straight grade
            });

            Assert.Equal(2, r.Ips.Count);
            Assert.True(Math.Abs(r.Stats.NetArea) < 0.5, $"net area {r.Stats.NetArea}");
        }

        [Fact]
        public void FixedIpsAreKept()
        {
            ProfileDesignResult r = ProfileDesigner.Design(Ground(Ridge), new ProfileDesignOptions
            {
                Fixed = { new FixedIp(300, 14.5) },
                MinImprovement = 0.01,
            });

            ProfileIp ip = Assert.Single(r.Ips, i => Math.Abs(i.Chainage - 300) < 1e-6);
            Assert.Equal(14.5, ip.Level, 3);
        }

        [Fact]
        public void VcLengthsComeFromKAndFitTheSpacing()
        {
            ProfileDesignResult r = ProfileDesigner.Design(Ground(Ridge), new ProfileDesignOptions { KValue = 10, MinVcLength = 20 });

            // Grade change at the crest is about 6 % (a little more after refitting with the
            // curve in place): VC = K x |A|, rounded up to the metre.
            Assert.Equal(3, r.Ips.Count);
            double a = Math.Abs(VerticalProfile.Grade(r.Ips, 1) - VerticalProfile.Grade(r.Ips, 0)) * 100;
            Assert.InRange(a, 5.9, 7.0);
            Assert.Equal(Math.Ceiling(10 * a - 1e-9), r.Ips[1].VcLength, 6);
            Assert.Equal(0, r.Ips[0].VcLength);

            // The refit with the curve in place fits better than the tangents alone would.
            var tangentOnly = new List<ProfileIp> { r.Ips[0], new ProfileIp(150, 16, r.Ips[1].VcLength), r.Ips[2] };
            CutFillStats plain = VerticalProfile.CutFill(c => VerticalProfile.LevelAt(tangentOnly, c), Ground(Ridge), 0, 400);
            Assert.True(r.Stats.Rms < plain.Rms, $"{r.Stats.Rms} vs {plain.Rms}");
        }

        [Fact]
        public void DesignToolReturnsAnIpTable()
        {
            string id = GroundProfileStore.Add(Ground(Ridge));
            var tools = new ProfileTools();
            using JsonDocument input = JsonDocument.Parse("{\"ground_id\":\"" + id + "\",\"max_grade_pct\":8,\"k_value\":10," +
                "\"current_ips\":[{\"chainage\":0,\"level\":10},{\"chainage\":400,\"level\":11}]}");
            var outcome = tools.Execute(ProfileTools.Design, input.RootElement);

            Assert.False(outcome.IsError, outcome.Content);
            using JsonDocument result = JsonDocument.Parse(outcome.Content);
            Assert.Equal(3, result.RootElement.GetProperty("ips").GetArrayLength());
            Assert.True(result.RootElement.GetProperty("constraints_met").GetBoolean());
            Assert.True(result.RootElement.GetProperty("current").GetProperty("stats").GetProperty("rms_depth").GetDouble() > 1);
        }

        [Fact]
        public void EvaluateToolFlagsProblems()
        {
            var tools = new ProfileTools();
            using JsonDocument input = JsonDocument.Parse(
                "{\"ips\":[{\"chainage\":0,\"level\":10},{\"chainage\":30,\"level\":13,\"vc_length\":70},{\"chainage\":60,\"level\":13.1}]," +
                "\"max_grade_pct\":8,\"min_grade_pct\":0.5,\"min_ip_spacing\":40}");
            var outcome = tools.Execute(ProfileTools.Evaluate, input.RootElement);

            Assert.False(outcome.IsError, outcome.Content);
            Assert.Contains("steeper than 8%", outcome.Content);
            Assert.Contains("flatter than 0.5%", outcome.Content);
            Assert.Contains("apart", outcome.Content);
            Assert.Contains("overlap", outcome.Content);
        }
    }
}
