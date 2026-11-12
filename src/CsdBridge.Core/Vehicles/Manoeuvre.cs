using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;

namespace CsdBridge.Core.Vehicles
{
    /// <summary>A destination of a drive: where the vehicle goes, and optionally which way it faces on arrival.</summary>
    public sealed class DriveStop
    {
        public double[] Point { get; set; } = new double[2];

        /// <summary>Direction the vehicle faces on arrival, degrees anticlockwise from +X. Null: whatever the turn gives.</summary>
        public double? HeadingDeg { get; set; }
    }

    /// <summary>One movement of a solved manoeuvre, as saved: the rear axle's path as lines and arcs.</summary>
    public sealed class SolvedMove
    {
        public bool Reverse { get; set; }

        /// <summary>Solving in and out of a site: "in" for the movements to the stop, "out" for those from it.</summary>
        public string? Stage { get; set; }

        /// <summary>The way the vehicle faces at the start of the movement, degrees anticlockwise from +X.</summary>
        public double HeadingDeg { get; set; }

        /// <summary>A line is [x1, y1, x2, y2]; an arc is [centre x, centre y, radius, start angle, sweep] (radians).</summary>
        public List<double[]> Segments { get; set; } = new List<double[]>();
    }

    /// <summary>
    /// Everything needed to draw a turning path again: the vehicle, the manoeuvre and its geometry,
    /// the clearances and how it is drawn. Saved in the drawing with the entities it produced, so
    /// AITURNEDIT can change the settings or the geometry and regenerate.
    /// </summary>
    public sealed class ManoeuvreRecord
    {
        public int Version { get; set; } = 1;
        public string Id { get; set; } = "";
        public string Vehicle { get; set; } = "B99";

        /// <summary>template, follow, drive or solve.</summary>
        public string Mode { get; set; } = "template";

        /// <summary>Rear axle centre at the start (template, drive and solve).</summary>
        public double[]? Start { get; set; }

        public double HeadingDeg { get; set; }

        /// <summary>Turn radius at the outer front wheel.</summary>
        public double Radius { get; set; }

        public List<double> TurnAngles { get; set; } = new List<double> { 90 };
        public bool Left { get; set; } = true;
        public double LeadIn { get; set; }
        public double LeadOut { get; set; } = 6;

        /// <summary>outer_front_wheel, front_axle or rear_axle (template and follow).</summary>
        public string Reference { get; set; } = "outer_front_wheel";

        public bool Reverse { get; set; }

        /// <summary>Drive: arc or corner.</summary>
        public string Turns { get; set; } = "arc";

        public List<DriveStop> Stops { get; set; } = new List<DriveStop>();

        /// <summary>Follow: the curve's handle, and a point near the end the vehicle starts from.</summary>
        public string? FollowHandle { get; set; }

        public double[]? FollowNear { get; set; }

        /// <summary>Solve: the rear axle centre and the way the vehicle faces at the end.</summary>
        public double[]? Target { get; set; }

        public double TargetHeadingDeg { get; set; }

        /// <summary>
        /// Solve in and out (the 3-point solve): the rear axle centre, and the way the vehicle faces,
        /// where it stops on the site between driving in and driving out. Null: a plain solve from
        /// the start to the target.
        /// </summary>
        public double[]? Via { get; set; }

        public double ViaHeadingDeg { get; set; }

        /// <summary>
        /// Solve in and out: a line the vehicle only crosses driving forwards, x1, y1, x2, y2: the
        /// site boundary across the driveway. Null: none given.
        /// </summary>
        public double[]? Boundary { get; set; }

        /// <summary>Solve: the movements the solver found, what they were solved for, and what it said.</summary>
        public List<SolvedMove>? Solved { get; set; }

        public string? SolvedFor { get; set; }

        public string? SolvedSummary { get; set; }

        /// <summary>
        /// Drawing units in a metre when this was drawn (1000 in a millimetre drawing). Everything
        /// else in the record is in metres: positions are the drawing's divided by this.
        /// </summary>
        public double UnitsPerMetre { get; set; } = 1;

        public double ClearanceInside { get; set; } = 0.3;
        public double ClearanceOutside { get; set; } = 0.3;
        public TurnPathLayers Layers { get; set; } = new TurnPathLayers();
        public TurnPathDrawOptions Draw { get; set; } = new TurnPathDrawOptions();
        public double OutlineInterval { get; set; }
        public double? TextHeight { get; set; }

        public bool CheckConflicts { get; set; }
        public List<string> ObstructionLayers { get; set; } = new List<string>();

        /// <summary>Handles of the entities drawn for this manoeuvre, and the block they were gathered into, if any.</summary>
        public List<string> Handles { get; set; } = new List<string>();

        public string? BlockName { get; set; }

        /// <summary>Handle of the block reference, and where the block's base point was put.</summary>
        public string? BlockHandle { get; set; }

        public double[]? BlockBase { get; set; }

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public string ToJson() => JsonSerializer.Serialize(this, Json);

        public static ManoeuvreRecord FromJson(string json) =>
            JsonSerializer.Deserialize<ManoeuvreRecord>(json, Json) ?? throw new ArgumentException("The saved manoeuvre is empty.");

        [JsonIgnore]
        public Point2 StartPoint => Start != null && Start.Length >= 2 ? new Point2(Start[0], Start[1]) : throw new ArgumentException("The manoeuvre has no start point.");

        [JsonIgnore]
        public Point2 TargetPoint => Target != null && Target.Length >= 2 ? new Point2(Target[0], Target[1]) : throw new ArgumentException("The manoeuvre has no target position.");

        /// <summary>Solve: the clearance kept to obstructions (the manoeuvring clearance, the same on both sides).</summary>
        [JsonIgnore]
        public double SolveClearance => Math.Min(ClearanceInside, ClearanceOutside);

        /// <summary>What a solution depends on. When this no longer matches <see cref="SolvedFor"/> the manoeuvre must be solved again.</summary>
        public string SolveSignature()
        {
            string N(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
            string P(double[]? p) => p == null || p.Length < 2 ? "-" : N(p[0]) + "," + N(p[1]);
            return string.Join("|", Vehicle, N(Radius), N(SolveClearance), P(Start), N(HeadingDeg), P(Target), N(TargetHeadingDeg),
                string.Join(",", ObstructionLayers.Select(l => l.Trim().ToUpperInvariant())),
                Via == null ? "-" : P(Via) + "@" + N(ViaHeadingDeg),
                Boundary == null ? "-" : string.Join(",", Boundary.Select(N)));
        }

        [JsonIgnore]
        public bool NeedsSolving => Solved == null || Solved.Count == 0 || SolvedFor != SolveSignature();

        /// <summary>Keeps a solution with the record.</summary>
        public void SetSolution(SolveResult result)
        {
            Solved = new List<SolvedMove>();
            foreach (SolvedLeg leg in result.Legs)
            {
                var move = new SolvedMove { Reverse = leg.Reverse, Stage = leg.Stage, HeadingDeg = Math.Round(leg.StartHeading * 180 / Math.PI, 7) };
                foreach (Segment s in leg.Path.Segments)
                {
                    move.Segments.Add(s is ArcSegment arc
                        ? new[] { Math.Round(arc.Center.X, 6), Math.Round(arc.Center.Y, 6), Math.Round(arc.Radius, 6), Math.Round(arc.StartAngle, 9), Math.Round(arc.Sweep, 9) }
                        : new[] { Math.Round(s.Start.X, 6), Math.Round(s.Start.Y, 6), Math.Round(s.End.X, 6), Math.Round(s.End.Y, 6) });
                }

                Solved.Add(move);
            }

            SolvedFor = SolveSignature();
            SolvedSummary = result.Summary();
        }

        /// <summary>The saved solution as movements to simulate.</summary>
        public List<SolvedLeg> SolvedLegs()
        {
            var legs = new List<SolvedLeg>();
            foreach (SolvedMove move in Solved ?? new List<SolvedMove>())
            {
                var segments = new List<Segment>();
                foreach (double[] n in move.Segments)
                {
                    if (n.Length == 5)
                    {
                        segments.Add(new ArcSegment(new Point2(n[0], n[1]), n[2], n[3], n[4]));
                    }
                    else if (n.Length == 4)
                    {
                        segments.Add(new LineSegment(new Point2(n[0], n[1]), new Point2(n[2], n[3])));
                    }
                    else
                    {
                        throw new ArgumentException("The saved solution can't be read.");
                    }
                }

                if (segments.Count > 0)
                {
                    legs.Add(new SolvedLeg { Reverse = move.Reverse, Stage = move.Stage, StartHeading = move.HeadingDeg * Math.PI / 180, Path = new Path2(segments, false) });
                }
            }

            return legs;
        }

        [JsonIgnore]
        public ReferencePoint ReferencePointKind =>
            Reference == "rear_axle" ? ReferencePoint.RearAxle : Reference == "front_axle" ? ReferencePoint.FrontAxle : ReferencePoint.OuterFrontWheel;

        [JsonIgnore]
        public TurnPathBuilder.DriveTurns TurnsKind =>
            string.Equals(Turns, "corner", StringComparison.OrdinalIgnoreCase) ? TurnPathBuilder.DriveTurns.Corner : TurnPathBuilder.DriveTurns.Arc;

        /// <summary>
        /// Changes the units the drawing is taken to be in, keeping the manoeuvre where it is in the
        /// drawing: its positions (in metres) are rescaled to match. The vehicle doesn't change size
        /// in metres, so a solved manoeuvre has to be solved again.
        /// </summary>
        public void SetUnits(double unitsPerMetre)
        {
            if (!(unitsPerMetre > 0) || Math.Abs(unitsPerMetre - UnitsPerMetre) <= 1e-12 * unitsPerMetre)
            {
                return;
            }

            double factor = UnitsPerMetre / unitsPerMetre;
            double[] Times(double[] p) => p.Select(v => v * factor).ToArray();
            if (Start != null)
            {
                Start = Times(Start);
            }

            foreach (DriveStop stop in Stops)
            {
                stop.Point = Times(stop.Point);
            }

            if (FollowNear != null)
            {
                FollowNear = Times(FollowNear);
            }

            if (Target != null)
            {
                Target = Times(Target);
            }

            if (Via != null)
            {
                Via = Times(Via);
            }

            if (Boundary != null)
            {
                Boundary = Times(Boundary);
            }

            if (BlockBase != null)
            {
                BlockBase = Times(BlockBase);
            }

            UnitsPerMetre = unitsPerMetre;
            Solved = null;
            SolvedFor = null;
            SolvedSummary = null;
        }

        /// <summary>
        /// Moves the geometry: rotate by <paramref name="rotation"/> (radians) about <paramref name="about"/>,
        /// then shift by (dx, dy). Used when the block the manoeuvre was drawn into has been moved or rotated.
        /// </summary>
        public void Transform(Point2 about, double rotation, double dx, double dy)
        {
            double c = Math.Cos(rotation), s = Math.Sin(rotation);
            double[] Move(double[] p) => new[]
            {
                about.X + (p[0] - about.X) * c - (p[1] - about.Y) * s + dx,
                about.Y + (p[0] - about.X) * s + (p[1] - about.Y) * c + dy,
            };

            double deg = rotation * 180 / Math.PI;
            if (Start != null)
            {
                Start = Move(Start);
            }

            HeadingDeg += deg;
            foreach (DriveStop stop in Stops)
            {
                stop.Point = Move(stop.Point);
                if (stop.HeadingDeg != null)
                {
                    stop.HeadingDeg += deg;
                }
            }

            if (FollowNear != null)
            {
                FollowNear = Move(FollowNear);
            }

            if (Via != null)
            {
                Via = Move(Via);
                ViaHeadingDeg += deg;
            }

            if (Boundary != null && Boundary.Length >= 4)
            {
                double[] a = Move(new[] { Boundary[0], Boundary[1] }), b = Move(new[] { Boundary[2], Boundary[3] });
                Boundary = new[] { a[0], a[1], b[0], b[1] };
            }

            if (Target != null)
            {
                Target = Move(Target);
                TargetHeadingDeg += deg;

                // The obstructions haven't moved with it: what was solved no longer holds.
                Solved = null;
                SolvedFor = null;
                SolvedSummary = null;
            }
        }
    }

    /// <summary>One leg of a planned manoeuvre, simulated.</summary>
    public sealed class PlannedLeg
    {
        public TurnPathResult Result { get; set; } = null!;
        public string Label { get; set; } = "";

        /// <summary>The first leg of a continuous run gets the start vehicle and the label.</summary>
        public bool First { get; set; }

        /// <summary>For a drive: "forward, turn left 40°, 6.0 m straight".</summary>
        public string? Description { get; set; }
    }

    public sealed class ManoeuvrePlan
    {
        public DesignVehicle Vehicle { get; set; } = null!;
        public List<PlannedLeg> Legs { get; } = new List<PlannedLeg>();
        public List<string> Warnings { get; } = new List<string>();

        public IReadOnlyList<TurnPathResult> Results => Legs.Select(l => l.Result).ToList();

        /// <summary>Where the vehicle is at the very start, for a block's base point.</summary>
        public Point2 StartPoint => Legs[0].Result.Poses[0].RearAxle;

        public VehiclePose EndPose
        {
            get
            {
                TurnPathResult last = Legs[Legs.Count - 1].Result;
                return last.Poses[last.Poses.Count - 1];
            }
        }
    }

    /// <summary>Turns a saved manoeuvre into simulated legs and drawing entities.</summary>
    public static class ManoeuvrePlanner
    {
        /// <param name="curve">Looks up a curve by handle (follow mode). Null result = not found.</param>
        public static ManoeuvrePlan Plan(ManoeuvreRecord r, TurnPathConventions c, Func<string, EntitySpec?>? curve = null)
        {
            DesignVehicle v = c.Vehicle(r.Vehicle);
            var plan = new ManoeuvrePlan { Vehicle = v };

            TurnPathResult Run(TurnPathRequest req)
            {
                req.Step = c.Step;
                req.Tolerance = c.Tolerance;
                req.ClearanceInside = r.ClearanceInside;
                req.ClearanceOutside = r.ClearanceOutside;
                return TurnPathSimulator.Simulate(req);
            }

            switch ((r.Mode ?? "").ToLowerInvariant())
            {
                case "template":
                {
                    if (r.Radius < v.MinTurnRadius - 1e-9)
                    {
                        plan.Warnings.Add($"A {r.Radius:0.##} m radius is below the {v.Name}'s minimum of {v.MinTurnRadius:0.#} m (AS/NZS 2890.1 B3.1).");
                    }

                    foreach (double angle in r.TurnAngles)
                    {
                        TurnPathRequest req = TurnPathBuilder.Template(new TurnTemplate
                        {
                            Vehicle = v,
                            Start = r.StartPoint,
                            Heading = r.HeadingDeg * Math.PI / 180,
                            Radius = r.Radius,
                            TurnAngleDeg = angle,
                            Left = r.Left,
                            LeadIn = r.LeadIn,
                            LeadOut = r.LeadOut,
                            Reference = r.ReferencePointKind,
                        });
                        plan.Legs.Add(new PlannedLeg { Result = Run(req), Label = $"{v.Name} R{r.Radius:0.#} m", First = true });
                    }

                    break;
                }

                case "follow":
                {
                    EntitySpec? spec = r.FollowHandle == null ? null : curve?.Invoke(r.FollowHandle);
                    if (spec == null)
                    {
                        throw new ArgumentException("The line the vehicle followed is no longer in the drawing (or isn't a line, arc or polyline).");
                    }

                    Point2? near = r.FollowNear != null && r.FollowNear.Length >= 2 ? new Point2(r.FollowNear[0], r.FollowNear[1]) : (Point2?)null;
                    Path2 path = TurnPathBuilder.FromCurve(spec, near);
                    var req = new TurnPathRequest { Vehicle = v, Path = path, Reference = v.Resolve(r.ReferencePointKind, leftTurn: true), Reverse = r.Reverse };
                    plan.Legs.Add(new PlannedLeg { Result = Run(req), Label = v.Name, First = true });
                    break;
                }

                case "drive":
                {
                    var pose = new VehiclePose(r.StartPoint, r.HeadingDeg * Math.PI / 180);
                    IReadOnlyList<double>? trailers = null;   // where the last leg left the trailers
                    for (int i = 0; i < r.Stops.Count; i++)
                    {
                        TurnPathBuilder.DriveLeg? leg = Leg(v, pose, r.Stops[i], r.Radius, r.TurnsKind);
                        if (leg == null)
                        {
                            throw new ArgumentException($"The {v.Name} can't reach destination {i + 1} in one leg at a {r.Radius:0.##} m radius: it is too close, inside the turning circle, or would need more than a half turn" +
                                                        (v.IsArticulated ? " (with a trailer it only drives forwards)." : "."));
                        }

                        leg.Request.InitialTrailerHeadings = trailers;
                        TurnPathResult result = Run(leg.Request);
                        trailers = result.Trailers.Count > 0 ? result.EndTrailerHeadings : null;
                        plan.Warnings.AddRange(result.Warnings.Select(w => $"Leg {i + 1}: {w}"));
                        plan.Legs.Add(new PlannedLeg { Result = result, Label = v.Name, First = i == 0, Description = Describe(leg) });
                        pose = result.Poses[result.Poses.Count - 1];
                    }

                    if (plan.Legs.Count == 0)
                    {
                        throw new ArgumentException("The drive has no destinations.");
                    }

                    return plan;
                }

                case "solve":
                {
                    List<SolvedLeg> legs = r.SolvedLegs();
                    if (legs.Count == 0)
                    {
                        throw new ArgumentException("The manoeuvre hasn't been solved yet.");
                    }

                    string label = r.Via != null ? $"{v.Name}, in and out" : legs.Count == 1 ? v.Name : $"{v.Name}, {legs.Count} movements";
                    for (int i = 0; i < legs.Count; i++)
                    {
                        TurnPathResult result = Run(legs[i].ToRequest(v));
                        plan.Warnings.AddRange(result.Warnings.Select(w => $"Movement {i + 1}: {w}"));
                        plan.Legs.Add(new PlannedLeg
                        {
                            Result = result,
                            Label = label,
                            First = i == 0,
                            Description = (legs[i].Stage != null ? legs[i].Stage + ", " : "") + $"{(legs[i].Reverse ? "reverse" : "forward")} {legs[i].Length:0.0} m",
                        });
                    }

                    return plan;
                }

                default:
                    throw new ArgumentException($"Unknown manoeuvre mode '{r.Mode}'.");
            }

            foreach (PlannedLeg leg in plan.Legs)
            {
                plan.Warnings.AddRange(leg.Result.Warnings);
            }

            return plan;
        }

        /// <summary>
        /// Solves a "solve" manoeuvre: finds the forward and reverse movements from its start to its
        /// target between the obstructions, and keeps them with the record when there is a solution.
        /// </summary>
        /// <param name="obstructions">The linework to stay clear of inside a window (min, max).</param>
        public static SolveResult Solve(ManoeuvreRecord r, TurnPathConventions c, Func<Point2, Point2, IReadOnlyList<Obstruction>> obstructions)
        {
            DesignVehicle v = c.Vehicle(r.Vehicle);
            if (v.IsArticulated)
            {
                throw new ArgumentException($"The {v.Name} tows a trailer, and the solver only works out manoeuvres for rigid vehicles: reversing a trailer isn't modelled.");
            }

            var start = new VehiclePose(r.StartPoint, r.HeadingDeg * Math.PI / 180);
            var goal = new VehiclePose(r.TargetPoint, r.TargetHeadingDeg * Math.PI / 180);
            VehiclePose? stop = r.Via != null && r.Via.Length >= 2 ? new VehiclePose(new Point2(r.Via[0], r.Via[1]), r.ViaHeadingDeg * Math.PI / 180) : (VehiclePose?)null;
            var places = new List<Point2> { start.RearAxle, goal.RearAxle };
            if (stop is VehiclePose at)
            {
                places.Add(at.RearAxle);
            }

            double margin = Math.Max(v.Length, c.SolverSearchMargin);
            var min = new Point2(places.Min(p => p.X) - margin, places.Min(p => p.Y) - margin);
            var max = new Point2(places.Max(p => p.X) + margin, places.Max(p => p.Y) + margin);
            double reach = v.Length + 2;
            var q = new SolveRequest
            {
                Vehicle = v,
                Start = start,
                Goal = goal,
                Radius = r.Radius > 0 ? r.Radius : (double?)null,
                Clearance = r.SolveClearance,
                Obstructions = obstructions(new Point2(min.X - reach, min.Y - reach), new Point2(max.X + reach, max.Y + reach)),
                Min = min,
                Max = max,
                TimeLimitSeconds = c.SolverTimeLimit,
            };
            if (stop != null && r.Boundary != null && r.Boundary.Length >= 4)
            {
                q.ForwardOnlyLines = new List<(Point2, Point2)> { (new Point2(r.Boundary[0], r.Boundary[1]), new Point2(r.Boundary[2], r.Boundary[3])) };
            }

            SolveResult result = stop is VehiclePose on ? ManoeuvreSolver.SolveInAndOut(q, on) : ManoeuvreSolver.SolveWithFallback(q);
            result.ObstructionsUsed = q.Obstructions.Count;
            if (result.Solved)
            {
                r.SetSolution(result);
            }

            return result;
        }

        /// <summary>The leg to one stop: to a pose when the stop has a heading, otherwise to the point.</summary>
        public static TurnPathBuilder.DriveLeg? Leg(DesignVehicle v, VehiclePose from, DriveStop stop, double radius, TurnPathBuilder.DriveTurns turns)
        {
            var target = new Point2(stop.Point[0], stop.Point[1]);
            bool? direction = v.IsArticulated ? false : (bool?)null;   // a trailer can't be reversed: forwards only
            return stop.HeadingDeg is double h
                ? TurnPathBuilder.DriveToPose(v, from, target, h * Math.PI / 180, radius, direction)
                : TurnPathBuilder.DriveTo(v, from, target, radius, direction, turns);
        }

        public static string Describe(TurnPathBuilder.DriveLeg leg)
        {
            var parts = new List<string> { leg.Reverse ? "reverse" : "forward" };
            parts.AddRange(leg.Moves);
            return string.Join(", ", parts);
        }

        /// <summary>The entities for a plan, in the record's style.</summary>
        public static List<DraftedEntity> Draft(ManoeuvrePlan plan, ManoeuvreRecord r, TurnPathConventions c)
        {
            var drafted = new List<DraftedEntity>();
            foreach (PlannedLeg leg in plan.Legs)
            {
                var style = new TurnPathStyle
                {
                    Layers = r.Layers,
                    Draw = r.Draw.Clone(),
                    OutlineInterval = r.OutlineInterval,
                    TextHeight = r.TextHeight,
                    Hatch = c.Hatch,
                    Label = leg.Label,
                    SkipStartVehicle = !leg.First,
                };
                style.Draw.Labels = r.Draw.Labels && leg.First;
                drafted.AddRange(TurnPathDrafter.Draw(leg.Result, style));
            }

            return drafted;
        }

        /// <summary>The layers a record draws on, which a conflict check must not treat as obstructions.</summary>
        public static List<string> OwnLayers(ManoeuvreRecord r, TurnPathConventions c)
        {
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DesignVehicle v in c.Vehicles.Values)
            {
                foreach (KeyValuePair<string, string> kv in r.Layers.Resolve(v.Name).All())
                {
                    own.Add(kv.Value);
                }

                foreach (KeyValuePair<string, string> kv in c.Layers.Resolve(v.Name).All())
                {
                    own.Add(kv.Value);
                }
            }

            return own.ToList();
        }
    }
}
