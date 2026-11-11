using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

        /// <summary>template, follow or drive.</summary>
        public string Mode { get; set; } = "template";

        /// <summary>Rear axle centre at the start (template and drive).</summary>
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
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        public string ToJson() => JsonSerializer.Serialize(this, Json);

        public static ManoeuvreRecord FromJson(string json) =>
            JsonSerializer.Deserialize<ManoeuvreRecord>(json, Json) ?? throw new ArgumentException("The saved manoeuvre is empty.");

        public Point2 StartPoint => Start != null && Start.Length >= 2 ? new Point2(Start[0], Start[1]) : throw new ArgumentException("The manoeuvre has no start point.");

        public ReferencePoint ReferencePointKind =>
            Reference == "rear_axle" ? ReferencePoint.RearAxle : Reference == "front_axle" ? ReferencePoint.FrontAxle : ReferencePoint.OuterFrontWheel;

        public TurnPathBuilder.DriveTurns TurnsKind =>
            string.Equals(Turns, "corner", StringComparison.OrdinalIgnoreCase) ? TurnPathBuilder.DriveTurns.Corner : TurnPathBuilder.DriveTurns.Arc;

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
                    for (int i = 0; i < r.Stops.Count; i++)
                    {
                        TurnPathBuilder.DriveLeg? leg = Leg(v, pose, r.Stops[i], r.Radius, r.TurnsKind);
                        if (leg == null)
                        {
                            throw new ArgumentException($"The {v.Name} can't reach destination {i + 1} in one leg at a {r.Radius:0.##} m radius: it is too close, inside the turning circle, or would need more than a half turn.");
                        }

                        TurnPathResult result = Run(leg.Request);
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

                default:
                    throw new ArgumentException($"Unknown manoeuvre mode '{r.Mode}'.");
            }

            foreach (PlannedLeg leg in plan.Legs)
            {
                plan.Warnings.AddRange(leg.Result.Warnings);
            }

            return plan;
        }

        /// <summary>The leg to one stop: to a pose when the stop has a heading, otherwise to the point.</summary>
        public static TurnPathBuilder.DriveLeg? Leg(DesignVehicle v, VehiclePose from, DriveStop stop, double radius, TurnPathBuilder.DriveTurns turns)
        {
            var target = new Point2(stop.Point[0], stop.Point[1]);
            return stop.HeadingDeg is double h
                ? TurnPathBuilder.DriveToPose(v, from, target, h * Math.PI / 180, radius)
                : TurnPathBuilder.DriveTo(v, from, target, radius, null, turns);
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
