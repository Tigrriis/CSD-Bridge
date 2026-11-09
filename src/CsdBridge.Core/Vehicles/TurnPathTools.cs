using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;
using CsdBridge.Core.Tools;

namespace CsdBridge.Core.Vehicles
{
    /// <summary>The draw_turn_path tool: AS/NZS 2890.1 B85/B99 swept paths, drawn or checked.</summary>
    public sealed class TurnPathTools : IToolExtension
    {
        public const string DrawTurnPath = "draw_turn_path";

        private const string Point = "{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2}";

        private readonly IDrawingHost _host;
        private readonly TurnPathConventions? _conventions;
        private readonly string? _loadError;

        public TurnPathTools(IDrawingHost host, TurnPathConventions? conventions, string? loadError = null)
        {
            _host = host;
            _conventions = conventions;
            _loadError = loadError;
            string vehicles = conventions == null ? "B99, B85" : string.Join(", ", conventions.Vehicles.Keys);
            Tools = new List<ToolDefinition>
            {
                new ToolDefinition(
                    DrawTurnPath,
                    "Simulate an AS/NZS 2890.1 design vehicle (" + vehicles + ") through a manoeuvre and draw its swept path: the body " +
                    "envelope (solid), the envelope plus the standard's clearances (dashed design template), the vehicle outline at the " +
                    "start and end, the driven path and a label; optionally wheel paths, outlines along the way and a hatch. Three modes: " +
                    "'template' = a standard single turn from a start point (rear axle centre) and heading, at a radius measured at the " +
                    "outer front wheel (default the vehicle's minimum: B99 6.3 m, B85 5.8 m), through turn_angle_deg left or right, with " +
                    "straight lead_in/lead_out; 'follow' = drive the vehicle along an existing line, arc or polyline (handle), from the end " +
                    "nearest 'start' or the curve's start, forwards or in reverse; 'points' = drive through points with tangent corner arcs " +
                    "of 'radius' (default the vehicle's minimum). The reference point that follows the path is the outer front wheel for " +
                    "templates and the front axle centre otherwise (rear axle when reversing); the rear axle tracks in behind it. Returns " +
                    "the tightest radius used and whether it meets the vehicle's minimum, the net turn, the handles drawn by part and the " +
                    "extents. Use dry_run to check a manoeuvre without drawing. Layers come from turn-paths.json; override any with " +
                    "'layers'. draw.block gathers the result into a block (named after the vehicle, inserted at its start point on the " +
                    "block layer). B99 is for access driveways, ramps and circulation roadways; B85 only for parking spaces and aisles.",
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"vehicle\":{\"type\":\"string\",\"description\":\"" + vehicles + ". Default the conventions' default (B99).\"}," +
                    "\"mode\":{\"type\":\"string\",\"enum\":[\"template\",\"follow\",\"points\"]}," +
                    "\"start\":" + Point + "," +
                    "\"heading_deg\":{\"type\":\"number\",\"description\":\"Template: direction of travel at the start, degrees anticlockwise from +X.\"}," +
                    "\"radius\":{\"type\":\"number\",\"description\":\"Template: turn radius at the outer front wheel. Points: corner radius of the reference point's path.\"}," +
                    "\"turn_angle_deg\":{\"type\":\"number\",\"description\":\"Template: change of direction (default 90).\"}," +
                    "\"direction\":{\"type\":\"string\",\"enum\":[\"left\",\"right\"],\"description\":\"Template: which way it turns (default left).\"}," +
                    "\"lead_in\":{\"type\":\"number\"},\"lead_out\":{\"type\":\"number\",\"description\":\"Template: straight travel before/after the turn (default 0 and 6).\"}," +
                    "\"handle\":{\"type\":\"string\",\"description\":\"Follow: the curve to drive along.\"}," +
                    "\"reverse\":{\"type\":\"boolean\",\"description\":\"Follow/points: travel backwards along the path.\"}," +
                    "\"points\":{\"type\":\"array\",\"items\":" + Point + ",\"minItems\":2,\"maxItems\":200}," +
                    "\"reference\":{\"type\":\"string\",\"enum\":[\"outer_front_wheel\",\"front_axle\",\"rear_axle\"],\"description\":\"Which point of the vehicle follows the path.\"}," +
                    "\"clearance_inside\":{\"type\":\"number\"},\"clearance_outside\":{\"type\":\"number\",\"description\":\"Metres; default the vehicle's (B99 0.3/0.6, B85 0.3/0.3).\"}," +
                    "\"label\":{\"type\":\"string\"}," +
                    "\"layers\":{\"type\":\"object\",\"description\":\"Layer per part: swept_path, clearance, vehicle, vehicle_intermediate, path, wheel_paths, labels, hatch, block.\",\"additionalProperties\":{\"type\":\"string\"}}," +
                    "\"draw\":{\"type\":\"object\",\"properties\":{" +
                    "\"swept_path\":{\"type\":\"boolean\"},\"clearance\":{\"type\":\"boolean\"},\"vehicle\":{\"type\":\"boolean\"},\"path\":{\"type\":\"boolean\"}," +
                    "\"wheel_paths\":{\"type\":\"boolean\"},\"labels\":{\"type\":\"boolean\"},\"hatch\":{\"type\":\"boolean\"},\"closed_outline\":{\"type\":\"boolean\"}," +
                    "\"block\":{\"type\":\"boolean\",\"description\":\"Make the result one block.\"}," +
                    "\"outline_interval\":{\"type\":\"number\",\"description\":\"Vehicle outline every this many metres (0 = none).\"}}}," +
                    "\"block_name\":{\"type\":\"string\",\"description\":\"Name for the block (default '<vehicle> turning path'; a number is added if taken).\"}," +
                    "\"dry_run\":{\"type\":\"boolean\"}" +
                    "},\"required\":[\"mode\"]}"),
            };
        }

        public IReadOnlyList<ToolDefinition> Tools { get; }

        public string? PromptSection =>
            "## Vehicle turning paths (AS/NZS 2890.1:2004)\n" +
            "Use draw_turn_path for swept paths; never approximate a turning template with offsets or arcs yourself.\n" +
            "- B99 (99.8th percentile car) is required for access driveways, ramps and circulation roadways; B85 (85th percentile) " +
            "only for parking spaces and aisles (B2.2, B2.3).\n" +
            "- Minimum template radii, measured at the outer front wheel: B99 6.3 m, B85 5.8 m (B3.1). Turns on circulating roadways " +
            "and ramps should be at least 8 m radius (A4).\n" +
            "- The dashed template adds 300 mm manoeuvring clearance both sides, plus 300 mm circulation clearance on the outside for " +
            "B99 (600 mm outside in total) where vehicles travel above 10 km/h (B3.2).\n" +
            "- Aisle widths for parking manoeuvres come from Tables B2/B3 and Figure 2.2, not from swept paths (B4.4).\n" +
            (_conventions == null ? "" : "Vehicles and layers from turn-paths.json:\n" + _conventions.Describe());

        public bool RunsOnMainThread(string toolName) => true;

        public string? DescribeCall(string toolName, JsonElement input)
        {
            string vehicle = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("vehicle", out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
            bool dry = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("dry_run", out JsonElement d) && d.ValueKind == JsonValueKind.True;
            return (dry ? "checking a " : "drawing a ") + (vehicle.Length > 0 ? vehicle + " " : "") + "turning path";
        }

        public ToolOutcome Execute(string toolName, JsonElement input)
        {
            if (_conventions == null)
            {
                return new ToolOutcome("The turning path conventions couldn't be loaded: " + (_loadError ?? "no turn-paths.json") + ". Fix the file, then start a new chat.", true);
            }

            try
            {
                JsonArgs.RequireObject(input, "");
                TurnPathConventions c = _conventions;
                DesignVehicle vehicle = c.Vehicle(JsonArgs.OptionalString(input, "vehicle", ""));
                string mode = JsonArgs.RequireString(input, "mode", "").Trim().ToLowerInvariant();
                bool reverse = JsonArgs.OptionalBool(input, "reverse", "", false);
                bool dryRun = JsonArgs.OptionalBool(input, "dry_run", "", false);
                var warnings = new List<string>();

                ReferencePoint? reference = null;
                string? refName = JsonArgs.OptionalString(input, "reference", "");
                if (!string.IsNullOrWhiteSpace(refName))
                {
                    switch (refName!.Trim().ToLowerInvariant())
                    {
                        case "outer_front_wheel": reference = ReferencePoint.OuterFrontWheel; break;
                        case "front_axle": reference = ReferencePoint.FrontAxle; break;
                        case "rear_axle": reference = ReferencePoint.RearAxle; break;
                        default: throw new ArgumentException("reference must be outer_front_wheel, front_axle or rear_axle.");
                    }
                }

                TurnPathRequest req;
                string label;
                double? nominated = null;
                switch (mode)
                {
                    case "template":
                    {
                        bool left = !string.Equals(JsonArgs.OptionalString(input, "direction", ""), "right", StringComparison.OrdinalIgnoreCase);
                        var t = new TurnTemplate
                        {
                            Vehicle = vehicle,
                            Start = JsonArgs.RequirePoint(input, "start", ""),
                            Heading = JsonArgs.RequireNumber(input, "heading_deg", "") * Math.PI / 180,
                            Radius = JsonArgs.OptionalPositive(input, "radius", ""),
                            TurnAngleDeg = JsonArgs.OptionalPositive(input, "turn_angle_deg", "") ?? 90,
                            Left = left,
                            LeadIn = JsonArgs.OptionalNumber(input, "lead_in", "") ?? c.LeadIn,
                            LeadOut = JsonArgs.OptionalNumber(input, "lead_out", "") ?? c.LeadOut,
                            Reference = reference ?? ReferencePoint.OuterFrontWheel,
                        };
                        if (t.LeadIn < 0 || t.LeadOut < 0)
                        {
                            throw new ArgumentException("lead_in and lead_out can't be negative.");
                        }

                        double radius = t.Radius ?? vehicle.MinTurnRadius;
                        if (radius < vehicle.MinTurnRadius - 1e-9)
                        {
                            warnings.Add($"A {radius:0.##} m radius is below the {vehicle.Name}'s minimum of {vehicle.MinTurnRadius:0.#} m (AS/NZS 2890.1 B3.1).");
                        }

                        req = TurnPathBuilder.Template(t);
                        nominated = radius;
                        label = $"{vehicle.Name} R{radius:0.#} m";
                        break;
                    }

                    case "follow":
                    {
                        string handle = JsonArgs.RequireString(input, "handle", "");
                        IReadOnlyDictionary<string, EntitySpec> curves = _host.GetCurves(new[] { handle });
                        if (curves.Count == 0)
                        {
                            throw new ArgumentException($"{handle} is not a line, arc, circle or polyline in this drawing.");
                        }

                        Path2 path = TurnPathBuilder.FromCurve(curves.Values.First(), JsonArgs.OptionalPoint(input, "start", ""));
                        ReferencePoint rp = reference ?? (reverse ? ReferencePoint.RearAxle : ReferencePoint.FrontAxle);
                        req = new TurnPathRequest { Vehicle = vehicle, Path = path, Reference = vehicle.Resolve(rp, leftTurn: true), Reverse = reverse };
                        label = vehicle.Name;
                        break;
                    }

                    case "points":
                    {
                        List<JsonElement> items = JsonArgs.RequireArray(input, "points", "", 2, 200);
                        var pts = items.Select((e, i) => JsonArgs.ReadPoint(e, JsonArgs.Index("points", i))).ToList();
                        ReferencePoint rp = reference ?? (reverse ? ReferencePoint.RearAxle : ReferencePoint.FrontAxle);
                        VehiclePoint refPoint = vehicle.Resolve(rp, leftTurn: true);
                        double radius = JsonArgs.OptionalPositive(input, "radius", "") ?? vehicle.RadiusOfPoint(vehicle.MinTurnRadius, refPoint, left: true);
                        Path2 path = TurnPathBuilder.FromPoints(pts, radius, warnings);
                        req = new TurnPathRequest { Vehicle = vehicle, Path = path, Reference = refPoint, Reverse = reverse };
                        label = vehicle.Name;
                        break;
                    }

                    default:
                        throw new ArgumentException("mode must be template, follow or points.");
                }

                req.Step = c.Step;
                req.Tolerance = c.Tolerance;
                req.ClearanceInside = JsonArgs.OptionalNumber(input, "clearance_inside", "");
                req.ClearanceOutside = JsonArgs.OptionalNumber(input, "clearance_outside", "");
                if (req.ClearanceInside < 0 || req.ClearanceOutside < 0)
                {
                    throw new ArgumentException("Clearances can't be negative.");
                }

                TurnPathResult result = TurnPathSimulator.Simulate(req);
                warnings.AddRange(result.Warnings);

                TurnPathStyle style = TurnPathStyle.From(c);
                style.Label = JsonArgs.OptionalString(input, "label", "") ?? label;
                ApplyLayers(input, style.Layers);
                ApplyDraw(input, style);

                var report = new Dictionary<string, object?>
                {
                    ["vehicle"] = vehicle.Name,
                    ["mode"] = mode,
                    ["reference_point"] = $"({JsonArgs.Fmt(req.Reference.X)}, {JsonArgs.Fmt(req.Reference.Y)}) in the vehicle frame (x forward from the rear axle, y left)",
                    ["reverse"] = reverse,
                    ["path_length"] = Math.Round(result.PathLength, 3),
                    ["net_turn_deg"] = Math.Round(result.NetTurnDeg, 1),
                    ["nominated_radius"] = nominated,
                    ["tightest_radius_outer_front_wheel"] = double.IsInfinity(result.TightestRadius) ? null : (object)Math.Round(result.TightestRadius, 2),
                    ["max_steer_angle_deg"] = Math.Round(result.MaxSteerAngleDeg, 1),
                    ["min_turn_radius"] = vehicle.MinTurnRadius,
                    ["within_minimum_radius"] = result.WithinMinimumRadius,
                    ["clearances"] = new { left = result.ClearanceLeft, right = result.ClearanceRight },
                    ["start"] = Pose(result.Poses[0]),
                    ["end"] = Pose(result.Poses[result.Poses.Count - 1]),
                    ["extents"] = Extents(result),
                };

                if (dryRun)
                {
                    report["dry_run"] = true;
                }
                else
                {
                    List<DraftedEntity> drafted = TurnPathDrafter.Draw(result, style);
                    DrawingInfo info = _host.GetDrawingInfo();
                    List<LayerSpec> missing = TurnPathDrafter.MissingLayers(
                        drafted.Select(d => d.Spec.Layer).Where(l => l != null).Select(l => l!),
                        info.Layers.Select(l => l.Name),
                        c.LayerStyles);
                    if (missing.Count > 0)
                    {
                        _host.EnsureLayers(missing);
                        report["created_layers"] = missing.Select(m => m.Name).ToList();
                    }

                    IReadOnlyList<ItemResult> created = _host.CreateEntities(drafted.Select(d => d.Spec).ToList());
                    var handles = new Dictionary<string, List<string>>();
                    var failures = new List<string>();
                    for (int i = 0; i < created.Count; i++)
                    {
                        if (created[i].Ok && created[i].Handle != null)
                        {
                            if (!handles.TryGetValue(drafted[i].Group, out List<string>? list))
                            {
                                handles[drafted[i].Group] = list = new List<string>();
                            }

                            list.Add(created[i].Handle!);
                        }
                        else
                        {
                            failures.Add($"{drafted[i].Group}: {created[i].Error}");
                        }
                    }

                    report["drawn"] = handles;
                    report["layers"] = style.Layers.Resolve(vehicle.Name).All().ToDictionary(kv => kv.Key, kv => kv.Value);
                    if (style.Draw.Block)
                    {
                        List<string> all = handles.Values.SelectMany(h => h).ToList();
                        if (!(_host is IBlockMaker maker))
                        {
                            warnings.Add("This drawing host can't make blocks, so the entities were left as they are.");
                        }
                        else if (all.Count > 0)
                        {
                            string name = JsonArgs.OptionalString(input, "block_name", "") ?? $"{vehicle.Name} turning path";
                            BlockResult block = maker.MakeBlock(all, name, result.Poses[0].RearAxle, style.Layers.Resolve(vehicle.Name).Block);
                            report["block"] = new { name = block.BlockName, handle = block.Handle, entities = block.EntityCount };
                        }
                    }

                    if (failures.Count > 0)
                    {
                        report["failed"] = failures;
                    }
                }

                if (warnings.Count > 0)
                {
                    report["warnings"] = warnings;
                }

                string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
                return new ToolOutcome(json, false)
                {
                    Summary = $"{vehicle.Name} {mode}: tightest R {(double.IsInfinity(result.TightestRadius) ? "straight" : result.TightestRadius.ToString("0.00", CultureInfo.InvariantCulture) + " m")}" +
                              (result.WithinMinimumRadius ? "" : " (below minimum)"),
                };
            }
            catch (ToolInputException ex)
            {
                return new ToolOutcome(ex.Message, true);
            }
            catch (ArgumentException ex)
            {
                return new ToolOutcome(ex.Message, true);
            }
        }

        private static void ApplyLayers(JsonElement input, TurnPathLayers layers)
        {
            if (!JsonArgs.TryGet(input, "layers", out JsonElement obj) || obj.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (JsonProperty p in obj.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(p.Value.GetString()))
                {
                    continue;
                }

                if (!layers.Set(p.Name, p.Value.GetString()!.Trim()))
                {
                    throw new ArgumentException($"layers.{p.Name}: unknown part; use swept_path, clearance, vehicle, vehicle_intermediate, path, wheel_paths, labels or hatch.");
                }
            }
        }

        private static void ApplyDraw(JsonElement input, TurnPathStyle style)
        {
            if (!JsonArgs.TryGet(input, "draw", out JsonElement obj) || obj.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            TurnPathDrawOptions d = style.Draw;
            d.SweptPath = JsonArgs.OptionalBool(obj, "swept_path", "draw", d.SweptPath);
            d.Clearance = JsonArgs.OptionalBool(obj, "clearance", "draw", d.Clearance);
            d.Vehicle = JsonArgs.OptionalBool(obj, "vehicle", "draw", d.Vehicle);
            d.Path = JsonArgs.OptionalBool(obj, "path", "draw", d.Path);
            d.WheelPaths = JsonArgs.OptionalBool(obj, "wheel_paths", "draw", d.WheelPaths);
            d.Labels = JsonArgs.OptionalBool(obj, "labels", "draw", d.Labels);
            d.Hatch = JsonArgs.OptionalBool(obj, "hatch", "draw", d.Hatch);
            d.ClosedOutline = JsonArgs.OptionalBool(obj, "closed_outline", "draw", d.ClosedOutline);
            d.Block = JsonArgs.OptionalBool(obj, "block", "draw", d.Block);
            double? interval = JsonArgs.OptionalNumber(obj, "outline_interval", "draw");
            if (interval != null)
            {
                style.OutlineInterval = Math.Max(0, interval.Value);
            }
        }

        private static object Pose(VehiclePose p) => new
        {
            rear_axle = p.RearAxle.ToArray(),
            heading_deg = Math.Round(((p.Heading * 180 / Math.PI) % 360 + 360) % 360, 2),
        };

        private static object Extents(TurnPathResult r)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (Point2 p in r.LeftClearance.Concat(r.RightClearance).Concat(r.LeftBody).Concat(r.RightBody))
            {
                minX = Math.Min(minX, p.X);
                minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X);
                maxY = Math.Max(maxY, p.Y);
            }

            return new { min = new[] { Math.Round(minX, 3), Math.Round(minY, 3) }, max = new[] { Math.Round(maxX, 3), Math.Round(maxY, 3) } };
        }
    }
}
