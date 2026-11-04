using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CsdBridge.Core.Tools;

namespace CsdBridge.Core.Civil
{
    /// <summary>
    /// Vertical design tools: exact maths, no CAD access. The model chooses the strategy
    /// and constraints; these do the fitting and checking.
    /// </summary>
    public sealed class ProfileTools : IToolExtension
    {
        public const string Design = "design_vertical_profile";
        public const string Evaluate = "evaluate_vertical_profile";

        private const int MaxGroundPoints = 20000;

        private const string GroundProps =
            "\"ground_id\":{\"type\":\"string\",\"description\":\"Id of a ground profile from sample_ground_profile.\"}," +
            "\"ground\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2}," +
            "\"description\":\"Ground long section as [chainage, level] pairs, if you have no ground_id.\"},";

        private const string IpArray =
            "{\"type\":\"array\",\"minItems\":2,\"items\":{\"type\":\"object\",\"properties\":{" +
            "\"chainage\":{\"type\":\"number\"},\"level\":{\"type\":\"number\"}," +
            "\"vc_length\":{\"type\":\"number\",\"description\":\"Total VC length centred on the IP (0 or omitted = none).\"}}," +
            "\"required\":[\"chainage\",\"level\"]}}";

        public IReadOnlyList<ToolDefinition> Tools { get; } = new List<ToolDefinition>
        {
            new ToolDefinition(
                Design,
                "Design a vertical profile (IPs, grades and VC lengths) against a ground long section, by exact optimisation. " +
                "Priorities, in order: meet the constraints; use as few IPs as possible (an IP is only added or kept if it lowers the " +
                "centreline RMS cut/fill depth by at least min_improvement); then minimise centreline cut/fill. The start and end tie " +
                "into the ground unless told otherwise; fixed_ips keep IPs at given chainages (with or without a level), e.g. " +
                "intersections or tie-ins. Returns the IP table, centreline cut/fill stats (with VCs), a history of how much each " +
                "extra IP helped (use it to judge the trade-off), and notes. Centreline cut/fill is a guide only: confirm earthworks " +
                "with Civil Site Design's volumes after applying a design.",
                "{\"type\":\"object\",\"properties\":{" + GroundProps +
                "\"start_chainage\":{\"type\":\"number\"},\"end_chainage\":{\"type\":\"number\"}," +
                "\"tie_start\":{\"type\":\"boolean\",\"description\":\"Start level fixed to the ground (default true).\"}," +
                "\"tie_end\":{\"type\":\"boolean\",\"description\":\"End level fixed to the ground (default true).\"}," +
                "\"start_level\":{\"type\":\"number\"},\"end_level\":{\"type\":\"number\"}," +
                "\"fixed_ips\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"chainage\":{\"type\":\"number\"}," +
                "\"level\":{\"type\":\"number\",\"description\":\"Omit to let the designer choose the level.\"}},\"required\":[\"chainage\"]}}," +
                "\"max_grade_pct\":{\"type\":\"number\",\"description\":\"Default 10.\"}," +
                "\"min_grade_pct\":{\"type\":\"number\",\"description\":\"Minimum absolute grade, e.g. 0.5 for kerb drainage. Default 0.\"}," +
                "\"max_grade_change_pct\":{\"type\":\"number\",\"description\":\"Largest change of grade at an IP.\"}," +
                "\"min_ip_spacing\":{\"type\":\"number\",\"description\":\"Metres. Default 20; must leave room for the VCs.\"}," +
                "\"max_added_ips\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":30,\"description\":\"Most IPs to add between start, end and fixed IPs. Default 8.\"}," +
                "\"min_improvement\":{\"type\":\"number\",\"description\":\"Metres of RMS centreline cut/fill an IP must save to be used. Default 0.05. Higher = fewer IPs, more even grades.\"}," +
                "\"chainage_step\":{\"type\":\"number\",\"description\":\"IP chainages are multiples of this. Default 5.\"}," +
                "\"target_offset\":{\"type\":\"number\",\"description\":\"Design target = ground + this (m). Default 0.\"}," +
                "\"balance_centreline\":{\"type\":\"boolean\",\"description\":\"Force centreline fill area = cut area. Default false.\"}," +
                "\"min_vc_length\":{\"type\":\"number\"},\"k_value\":{\"type\":\"number\",\"description\":\"VC length = K x grade change %.\"}," +
                "\"current_ips\":" + IpArray.Replace("\"minItems\":2,", "\"minItems\":2,\"description\":\"The existing design, to compare against.\",") +
                "}}"),

            new ToolDefinition(
                Evaluate,
                "Check a vertical profile (IP table, e.g. read from Civil Site Design's grading grid) against the ground and design " +
                "limits: grades, grade changes, K values, VC overlaps, IP spacing, and centreline cut/fill (with VCs). Optionally lists " +
                "design level, ground and depth at an interval.",
                "{\"type\":\"object\",\"properties\":{" + GroundProps +
                "\"ips\":" + IpArray + "," +
                "\"max_grade_pct\":{\"type\":\"number\"},\"min_grade_pct\":{\"type\":\"number\"}," +
                "\"max_grade_change_pct\":{\"type\":\"number\"},\"min_vc_length\":{\"type\":\"number\"}," +
                "\"min_k\":{\"type\":\"number\"},\"min_ip_spacing\":{\"type\":\"number\"}," +
                "\"target_offset\":{\"type\":\"number\"}," +
                "\"output_interval\":{\"type\":\"number\",\"description\":\"List levels and depths every this many metres.\"}" +
                "},\"required\":[\"ips\"]}"),
        };

        public string? PromptSection =>
            "## Vertical design\n" +
            "For vertical profiles, use design_vertical_profile and evaluate_vertical_profile rather than working out levels yourself. " +
            "Design in this order unless the user says otherwise: (1) even, sensible grades within the limits; (2) as few IPs and " +
            "grade changes as possible; (3) only then minimise centreline cut/fill. Prefer a slightly worse fit with fewer IPs. " +
            "Keep tie-ins, intersections and other levels the user gives as fixed_ips. Say which limits you assumed.";

        public bool RunsOnMainThread(string toolName) => false;

        public string? DescribeCall(string toolName, JsonElement input) =>
            toolName == Design ? "designing a vertical profile" : toolName == Evaluate ? "checking a vertical profile" : null;

        public ToolOutcome Execute(string toolName, JsonElement input)
        {
            try
            {
                JsonArgs.RequireObject(input, "");
                switch (toolName)
                {
                    case Design: return RunDesign(input);
                    case Evaluate: return RunEvaluate(input);
                    default: return new ToolOutcome($"Unknown tool '{toolName}'.", true);
                }
            }
            catch (ToolInputException ex)
            {
                return new ToolOutcome("Invalid input: " + ex.Message, true) { Summary = "rejected invalid input" };
            }
            catch (ArgumentException ex)
            {
                return new ToolOutcome(ex.Message, true) { Summary = ex.Message };
            }
        }

        private static ToolOutcome RunDesign(JsonElement input)
        {
            GroundProfile ground = ReadGround(input);
            var o = new ProfileDesignOptions
            {
                Start = JsonArgs.OptionalNumber(input, "start_chainage", ""),
                End = JsonArgs.OptionalNumber(input, "end_chainage", ""),
                TieStart = JsonArgs.OptionalBool(input, "tie_start", "", true),
                TieEnd = JsonArgs.OptionalBool(input, "tie_end", "", true),
                StartLevel = JsonArgs.OptionalNumber(input, "start_level", ""),
                EndLevel = JsonArgs.OptionalNumber(input, "end_level", ""),
                MaxGradePct = JsonArgs.OptionalPositive(input, "max_grade_pct", "") ?? 10,
                MinGradePct = JsonArgs.OptionalNumber(input, "min_grade_pct", "") ?? 0,
                MaxGradeChangePct = JsonArgs.OptionalPositive(input, "max_grade_change_pct", ""),
                MinIpSpacing = JsonArgs.OptionalPositive(input, "min_ip_spacing", "") ?? 20,
                MaxIps = JsonArgs.OptionalInt(input, "max_added_ips", "", 0, 30) ?? 8,
                MinImprovement = JsonArgs.OptionalNumber(input, "min_improvement", "") ?? 0.05,
                ChainageStep = JsonArgs.OptionalPositive(input, "chainage_step", "") ?? 5,
                TargetOffset = JsonArgs.OptionalNumber(input, "target_offset", "") ?? 0,
                BalanceCentreline = JsonArgs.OptionalBool(input, "balance_centreline", "", false),
                MinVcLength = JsonArgs.OptionalNumber(input, "min_vc_length", "") ?? 0,
                KValue = JsonArgs.OptionalPositive(input, "k_value", ""),
            };

            if (o.MinGradePct >= o.MaxGradePct)
            {
                throw new ToolInputException("min_grade_pct must be less than max_grade_pct");
            }

            var fixedIps = JsonArgs.OptionalArray(input, "fixed_ips", "", 0, 100);
            if (fixedIps != null)
            {
                for (int i = 0; i < fixedIps.Count; i++)
                {
                    string path = JsonArgs.Index("fixed_ips", i);
                    o.Fixed.Add(new FixedIp(JsonArgs.RequireNumber(fixedIps[i], "chainage", path), JsonArgs.OptionalNumber(fixedIps[i], "level", path)));
                }
            }

            ProfileDesignResult r = ProfileDesigner.Design(ground, o);

            object? current = null;
            List<ProfileIp>? currentIps = ReadIps(input, "current_ips", required: false);
            if (currentIps != null)
            {
                double s = r.Ips[0].Chainage, e = r.Ips[r.Ips.Count - 1].Chainage;
                current = new
                {
                    ip_count = currentIps.Count,
                    stats = StatsJson(VerticalProfile.CutFill(c => VerticalProfile.LevelAt(currentIps, c), ground, s, e, o.TargetOffset)),
                };
            }

            var payload = new
            {
                ips = IpTable(r.Ips),
                stats = StatsJson(r.Stats),
                current,
                constraints_met = r.ConstraintsMet,
                history = r.History.Select(h => new { added_ips = h.AddedIps, rms = h.Rms, max_cut = h.MaxCut, max_fill = h.MaxFill }),
                notes = r.Notes.Count > 0 ? r.Notes : null,
            };

            return new ToolOutcome(JsonSerializer.Serialize(payload, ToolDispatcher.JsonOptions), false)
            {
                Summary = $"{r.Ips.Count} IPs, RMS cut/fill {r.Stats.Rms:0.00} m{(r.ConstraintsMet ? "" : " (constraints not all met)")}",
            };
        }

        private static ToolOutcome RunEvaluate(JsonElement input)
        {
            List<ProfileIp> ips = ReadIps(input, "ips", required: true)!;
            bool hasGround = JsonArgs.TryGet(input, "ground_id", out _) || JsonArgs.TryGet(input, "ground", out _);
            GroundProfile? ground = hasGround ? ReadGround(input) : null;
            double offset = JsonArgs.OptionalNumber(input, "target_offset", "") ?? 0;

            var checks = new List<string>();
            double? maxG = JsonArgs.OptionalPositive(input, "max_grade_pct", "");
            double? minG = JsonArgs.OptionalNumber(input, "min_grade_pct", "");
            double? maxDg = JsonArgs.OptionalPositive(input, "max_grade_change_pct", "");
            double? minVc = JsonArgs.OptionalNumber(input, "min_vc_length", "");
            double? minK = JsonArgs.OptionalPositive(input, "min_k", "");
            double? minSpacing = JsonArgs.OptionalPositive(input, "min_ip_spacing", "");

            for (int i = 0; i < ips.Count - 1; i++)
            {
                double spacing = ips[i + 1].Chainage - ips[i].Chainage;
                double g = VerticalProfile.Grade(ips, i) * 100;
                string where = $"ch {Fmt(ips[i].Chainage)} to {Fmt(ips[i + 1].Chainage)}";
                if (maxG.HasValue && Math.Abs(g) > maxG.Value + 1e-9)
                {
                    checks.Add($"{where}: grade {g:0.###}% is steeper than {maxG}%");
                }

                if (minG.HasValue && Math.Abs(g) < minG.Value - 1e-9)
                {
                    checks.Add($"{where}: grade {g:0.###}% is flatter than {minG}%");
                }

                if (minSpacing.HasValue && spacing < minSpacing.Value - 1e-9)
                {
                    checks.Add($"{where}: IPs are {spacing:0.###} m apart (minimum {minSpacing} m)");
                }

                if (ips[i].VcLength / 2 + ips[i + 1].VcLength / 2 > spacing + 1e-6)
                {
                    checks.Add($"{where}: the VCs overlap ({Fmt(ips[i].VcLength / 2 + ips[i + 1].VcLength / 2)} m of curve in {Fmt(spacing)} m)");
                }
            }

            for (int i = 1; i < ips.Count - 1; i++)
            {
                double a = (VerticalProfile.Grade(ips, i) - VerticalProfile.Grade(ips, i - 1)) * 100;
                string where = $"IP at ch {Fmt(ips[i].Chainage)}";
                if (maxDg.HasValue && Math.Abs(a) > maxDg.Value + 1e-9)
                {
                    checks.Add($"{where}: grade change {a:0.###}% exceeds {maxDg}%");
                }

                if (Math.Abs(a) >= 0.05 && minVc.HasValue && ips[i].VcLength < minVc.Value - 1e-9)
                {
                    checks.Add($"{where}: VC {Fmt(ips[i].VcLength)} m is shorter than {minVc} m");
                }

                double k = VerticalProfile.KValue(ips[i].VcLength, a);
                if (minK.HasValue && Math.Abs(a) >= 0.05 && k < minK.Value - 1e-9)
                {
                    checks.Add($"{where}: K {k:0.#} is below {minK}");
                }
            }

            object? stats = null;
            object? depths = null;
            if (ground != null)
            {
                double s = Math.Max(ips[0].Chainage, ground.Start), e = Math.Min(ips[ips.Count - 1].Chainage, ground.End);
                stats = StatsJson(VerticalProfile.CutFill(c => VerticalProfile.LevelAt(ips, c), ground, s, e, offset));
                double? interval = JsonArgs.OptionalPositive(input, "output_interval", "");
                if (interval.HasValue)
                {
                    int count = (int)Math.Floor((e - s) / interval.Value) + 1;
                    if (count > 1000)
                    {
                        throw new ToolInputException("output_interval gives more than 1000 rows; use a larger interval");
                    }

                    depths = Enumerable.Range(0, count).Select(i => s + i * interval.Value).Concat(new[] { e }).Distinct()
                        .Select(c =>
                        {
                            double design = VerticalProfile.LevelAt(ips, c);
                            double gl = ground.LevelAt(c);
                            return new[] { Math.Round(c, 3), Math.Round(design, 3), Math.Round(gl, 3), Math.Round(design - gl - offset, 3) };
                        }).ToList();
                }
            }

            var payload = new
            {
                ips = IpTable(ips),
                checks_passed = checks.Count == 0,
                checks = checks.Count > 0 ? checks : null,
                stats,
                depths_columns = depths != null ? "chainage, design level, ground level, depth (+fill/-cut)" : null,
                depths,
            };

            return new ToolOutcome(JsonSerializer.Serialize(payload, ToolDispatcher.JsonOptions), false)
            {
                Summary = checks.Count == 0 ? $"{ips.Count} IPs, all checks passed" : $"{ips.Count} IPs, {checks.Count} issue(s)",
            };
        }

        private static GroundProfile ReadGround(JsonElement input)
        {
            string? id = JsonArgs.OptionalString(input, "ground_id", "");
            if (!string.IsNullOrEmpty(id))
            {
                return GroundProfileStore.Get(id!) ?? throw new ToolInputException($"ground_id '{id}' is unknown in this session; sample the ground again");
            }

            var rows = JsonArgs.OptionalArray(input, "ground", "", 2, MaxGroundPoints)
                ?? throw new ToolInputException("give ground_id (from sample_ground_profile) or ground as [chainage, level] pairs");
            var points = new List<GroundPoint>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                string path = JsonArgs.Index("ground", i);
                if (rows[i].ValueKind != JsonValueKind.Array || rows[i].GetArrayLength() != 2)
                {
                    throw new ToolInputException($"{path} must be [chainage, level]");
                }

                points.Add(new GroundPoint(JsonArgs.ReadNumber(rows[i][0], path + "[0]"), JsonArgs.ReadNumber(rows[i][1], path + "[1]")));
            }

            return new GroundProfile(points);
        }

        private static List<ProfileIp>? ReadIps(JsonElement input, string name, bool required)
        {
            List<JsonElement>? rows = required
                ? JsonArgs.RequireArray(input, name, "", 2, 500)
                : JsonArgs.OptionalArray(input, name, "", 2, 500);
            if (rows == null)
            {
                return null;
            }

            var ips = new List<ProfileIp>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                string path = JsonArgs.Index(name, i);
                double vc = JsonArgs.OptionalNumber(rows[i], "vc_length", path) ?? 0;
                if (vc < 0)
                {
                    throw new ToolInputException($"{path}.vc_length must not be negative");
                }

                ips.Add(new ProfileIp(JsonArgs.RequireNumber(rows[i], "chainage", path), JsonArgs.RequireNumber(rows[i], "level", path), vc));
            }

            for (int i = 1; i < ips.Count; i++)
            {
                if (ips[i].Chainage <= ips[i - 1].Chainage)
                {
                    throw new ToolInputException($"{name} must be in increasing chainage order ({name}[{i}] is not after {name}[{i - 1}])");
                }
            }

            return ips;
        }

        private static object IpTable(List<ProfileIp> ips) =>
            ips.Select((ip, i) =>
            {
                double? gIn = i > 0 ? VerticalProfile.Grade(ips, i - 1) * 100 : (double?)null;
                double? gOut = i < ips.Count - 1 ? VerticalProfile.Grade(ips, i) * 100 : (double?)null;
                double? change = gIn.HasValue && gOut.HasValue ? gOut - gIn : null;
                double? k = change.HasValue && ip.VcLength > 0 && Math.Abs(change.Value) >= 0.01 ? VerticalProfile.KValue(ip.VcLength, change.Value) : (double?)null;
                return new
                {
                    chainage = Math.Round(ip.Chainage, 3),
                    level = Math.Round(ip.Level, 3),
                    vc_length = ip.VcLength > 0 ? Math.Round(ip.VcLength, 3) : (double?)null,
                    grade_in_pct = Round(gIn, 3),
                    grade_out_pct = Round(gOut, 3),
                    grade_change_pct = Round(change, 3),
                    k = Round(k, 1),
                };
            }).ToList();

        private static object StatsJson(CutFillStats s) => new
        {
            rms_depth = Math.Round(s.Rms, 3),
            mean_abs_depth = Math.Round(s.MeanAbs, 3),
            max_cut = Math.Round(s.MaxCut, 3),
            max_cut_chainage = Math.Round(s.MaxCutChainage, 3),
            max_fill = Math.Round(s.MaxFill, 3),
            max_fill_chainage = Math.Round(s.MaxFillChainage, 3),
            centreline_cut_area = Math.Round(s.CutArea, 1),
            centreline_fill_area = Math.Round(s.FillArea, 1),
            centreline_net_area = Math.Round(s.NetArea, 1),
        };

        private static double? Round(double? v, int digits) => v.HasValue ? Math.Round(v.Value, digits) : (double?)null;

        private static string Fmt(double v) => JsonArgs.Fmt(v);
    }
}
