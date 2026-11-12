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
                    "start and end, the driven path and a label; optionally wheel paths, outlines along the way and a hatch. Four modes: " +
                    "'template' = a standard single turn from a start point (rear axle centre) and heading, at a radius measured at the " +
                    "outer front wheel (default the vehicle's minimum: B99 6.3 m, B85 5.8 m), through turn_angle_deg left or right, with " +
                    "straight lead_in/lead_out; 'follow' = drive the vehicle along an existing line, arc or polyline (handle), from the end " +
                    "nearest 'start' or the curve's start, forwards or in reverse; 'points' = drive like a car: points[0] is the rear axle " +
                    "at the start (heading_deg, default towards the next point), and each later point is a destination it turns to at " +
                    "'radius' (never tighter than it can steer) then drives straight to, forwards if ahead, in reverse if behind; each leg " +
                    "keeps its own swept path, so a three-point turn is three points; 'solve' = work the manoeuvre out: give the rear axle " +
                    "and heading at the start (start, heading_deg) and at the end (target, target_heading_deg) and the obstruction_layers that " +
                    "bound the space, and it finds the forward and reverse movements between them (a three-point turn, a reverse into a " +
                    "bay), keeping clearance_inside (default 300 mm) to everything on those layers, and reports how many movements it took " +
                    "and whether the clearance had to be reduced. Points (start, points, target) are in the drawing's units; radii, " +
                    "clearances and every other length are metres whatever the drawing is drawn in, and a millimetre drawing is handled " +
                    "from its INSUNITS (pass drawing_units when that is unset or wrong: the result says which units it used). " +
                    "A vehicle defined with trailers (a prime mover and semi-trailer, a B-double) is towed through the manoeuvre and the " +
                    "swept path covers the whole combination; it drives forwards only, so reverse, and mode 'solve', are refused for it. " +
                    "The reference point that follows the path is the " +
                    "outer front wheel for templates and the front axle centre otherwise (rear axle when reversing); the rear axle tracks " +
                    "in behind it. Returns " +
                    "the tightest radius used and whether it meets the vehicle's minimum, the net turn, the handles drawn by part and the " +
                    "extents. Use dry_run to check a manoeuvre without drawing. Layers come from turn-paths.json; override any with " +
                    "'layers'. draw.block gathers the result into a block (named after the vehicle, inserted at its start point on the " +
                    "block layer). B99 is for access driveways, ramps and circulation roadways; B85 only for parking spaces and aisles.",
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"vehicle\":{\"type\":\"string\",\"description\":\"" + vehicles + ". Default the conventions' default (B99).\"}," +
                    "\"mode\":{\"type\":\"string\",\"enum\":[\"template\",\"follow\",\"points\",\"solve\"]}," +
                    "\"start\":" + Point + "," +
                    "\"heading_deg\":{\"type\":\"number\",\"description\":\"Template, points and solve: direction the vehicle faces at the start, degrees anticlockwise from +X (points: default towards the second point).\"}," +
                    "\"target\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2,\"description\":\"Solve: the rear axle centre at the end of the manoeuvre.\"}," +
                    "\"target_heading_deg\":{\"type\":\"number\",\"description\":\"Solve: direction the vehicle faces at the end, degrees anticlockwise from +X.\"}," +
                    "\"radius\":{\"type\":\"number\",\"description\":\"Turn radius at the outer front wheel (template and points); default the vehicle's minimum, and points can't go below it.\"}," +
                    "\"turn_angle_deg\":{\"type\":\"number\",\"description\":\"Template: change of direction (default 90).\"}," +
                    "\"direction\":{\"type\":\"string\",\"enum\":[\"left\",\"right\"],\"description\":\"Template: which way it turns (default left).\"}," +
                    "\"lead_in\":{\"type\":\"number\"},\"lead_out\":{\"type\":\"number\",\"description\":\"Template: straight travel before/after the turn (default 0 and 6).\"}," +
                    "\"handle\":{\"type\":\"string\",\"description\":\"Follow: the curve to drive along.\"}," +
                    "\"reverse\":{\"type\":\"boolean\",\"description\":\"Follow: travel backwards along the path. Points: force every leg forwards (false) or in reverse (true); default by where each point is.\"}," +
                    "\"points\":{\"type\":\"array\",\"items\":" + Point + ",\"minItems\":2,\"maxItems\":200}," +
                    "\"reference\":{\"type\":\"string\",\"enum\":[\"outer_front_wheel\",\"front_axle\",\"rear_axle\"],\"description\":\"Which point of the vehicle follows the path.\"}," +
                    "\"turns\":{\"type\":\"string\",\"enum\":[\"arc\",\"corner\"],\"description\":\"Points: arc = one arc to each destination when its radius allows (default); corner = turn at 'radius' then straight. A leg never turns more than a half turn and a bit.\"}," +
                    "\"clearance_inside\":{\"type\":\"number\"},\"clearance_outside\":{\"type\":\"number\",\"description\":\"Metres; default the vehicle's (B99 0.3/0.6, B85 0.3/0.3).\"}," +
                    "\"label\":{\"type\":\"string\"}," +
                    "\"layers\":{\"type\":\"object\",\"description\":\"Layer per part: swept_path, clearance, vehicle, vehicle_intermediate, path, wheel_paths, labels, hatch, block.\",\"additionalProperties\":{\"type\":\"string\"}}," +
                    "\"draw\":{\"type\":\"object\",\"properties\":{" +
                    "\"swept_path\":{\"type\":\"boolean\"},\"clearance\":{\"type\":\"boolean\"},\"vehicle\":{\"type\":\"boolean\"},\"path\":{\"type\":\"boolean\"}," +
                    "\"wheel_paths\":{\"type\":\"boolean\"},\"labels\":{\"type\":\"boolean\"},\"hatch\":{\"type\":\"boolean\"},\"closed_outline\":{\"type\":\"boolean\"}," +
                    "\"block\":{\"type\":\"boolean\",\"description\":\"Make the result one block.\"}," +
                    "\"outline_interval\":{\"type\":\"number\",\"description\":\"Vehicle outline every this many metres (0 = none).\"}}}," +
                    "\"arrival_headings_deg\":{\"type\":\"array\",\"items\":{\"type\":[\"number\",\"null\"]},\"description\":\"Points: the direction the vehicle faces on arriving at each destination (one entry per point after the first; null = free). With a heading the leg is turn, straight, turn.\"}," +
                    "\"obstruction_layers\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Check for conflicts with linework on these layers (kerbs, walls, columns; * wildcards, xref layers match by the part after |). Reports where the body hits and where the clearance is encroached, and marks them. Solve: the linework the vehicle must manoeuvre between (required).\"}," +
                    "\"block_name\":{\"type\":\"string\",\"description\":\"Name for the block (default '<vehicle> turning path'; a number is added if taken).\"}," +
                    "\"drawing_units\":{\"type\":\"string\",\"enum\":[\"auto\",\"m\",\"mm\"],\"description\":\"What the drawing is drawn in. Default auto: its INSUNITS, or metres when that isn't set.\"}," +
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
            "- To find out whether, and in how many movements, a vehicle can turn round or get into a space, use mode 'solve' with the " +
            "obstruction layers rather than guessing points: it reports the movements and any clearance it had to give up.\n" +
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

                // The simulator works in metres: points from the drawing are converted on the way in,
                // and what is drawn and reported is converted back on the way out.
                DrawingUnits units = DrawingUnits.Resolve(JsonArgs.OptionalString(input, "drawing_units", "") ?? c.DrawingUnits, _host.GetDrawingInfo().Units);
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
                List<TurnPathRequest>? legs = null;
                var legNotes = new List<string>();
                SolveResult? solved = null;
                List<string>? solveLayers = null;
                double solveClearance = 0;
                switch (mode)
                {
                    case "template":
                    {
                        bool left = !string.Equals(JsonArgs.OptionalString(input, "direction", ""), "right", StringComparison.OrdinalIgnoreCase);
                        var t = new TurnTemplate
                        {
                            Vehicle = vehicle,
                            Start = units.ToModel(JsonArgs.RequirePoint(input, "start", "")),
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

                        Point2? near = JsonArgs.OptionalPoint(input, "start", "") is Point2 from ? units.ToModel(from) : (Point2?)null;
                        Path2 path = TurnPathBuilder.FromCurve(units.ToModel(curves.Values.First()), near);
                        ReferencePoint rp = reference ?? (reverse ? ReferencePoint.RearAxle : ReferencePoint.FrontAxle);
                        req = new TurnPathRequest { Vehicle = vehicle, Path = path, Reference = vehicle.Resolve(rp, leftTurn: true), Reverse = reverse };
                        label = vehicle.Name;
                        break;
                    }

                    case "points":
                    {
                        // Drive: points[0] is the rear axle at the start, heading_deg the direction (default towards
                        // points[1]); every later point is a destination the vehicle turns and drives to, forwards
                        // or in reverse. Each leg is a request of its own.
                        List<JsonElement> items = JsonArgs.RequireArray(input, "points", "", 2, 200);
                        var pts = items.Select((e, i) => units.ToModel(JsonArgs.ReadPoint(e, JsonArgs.Index("points", i)))).ToList();
                        double? headingDeg = JsonArgs.OptionalNumber(input, "heading_deg", "");
                        double heading = headingDeg is double hd ? hd * Math.PI / 180 : Math.Atan2(pts[1].Y - pts[0].Y, pts[1].X - pts[0].X);
                        double radius = JsonArgs.OptionalPositive(input, "radius", "") ?? vehicle.MinTurnRadius;
                        var pose = new VehiclePose(pts[0], heading);
                        legs = new List<TurnPathRequest>();
                        IReadOnlyList<double>? trailers = null;   // where the last leg left the trailers
                        for (int i = 1; i < pts.Count; i++)
                        {
                            // A vehicle with a trailer only drives forwards.
                            bool? force = vehicle.IsArticulated ? false : JsonArgs.TryGet(input, "reverse", out _) ? reverse : (bool?)null;
                            string? turnsName = JsonArgs.OptionalString(input, "turns", "");
                            TurnPathBuilder.DriveTurns turns = string.IsNullOrWhiteSpace(turnsName) ? c.DriveTurnsMode
                                : turnsName!.Trim().ToLowerInvariant() == "corner" ? TurnPathBuilder.DriveTurns.Corner
                                : turnsName.Trim().ToLowerInvariant() == "arc" ? TurnPathBuilder.DriveTurns.Arc
                                : throw new ArgumentException("turns must be arc or corner.");
                            double? arrive = null;
                            if (JsonArgs.TryGet(input, "arrival_headings_deg", out JsonElement arrivals) && arrivals.ValueKind == JsonValueKind.Array
                                && i - 1 < arrivals.GetArrayLength() && arrivals[i - 1].ValueKind == JsonValueKind.Number)
                            {
                                arrive = arrivals[i - 1].GetDouble() * Math.PI / 180;
                            }

                            TurnPathBuilder.DriveLeg? leg = arrive is double facing
                                ? TurnPathBuilder.DriveToPose(vehicle, pose, pts[i], facing, radius, force)
                                : TurnPathBuilder.DriveTo(vehicle, pose, pts[i], radius, force, turns);
                            if (leg == null)
                            {
                                warnings.Add($"points[{i}] can't be reached in one leg (too close, inside the turning circle, or more than a half turn away); skipped. Drive past it and reverse, or add a point in between.");
                                continue;
                            }

                            leg.Request.Step = c.Step;
                            leg.Request.Tolerance = c.Tolerance;
                            leg.Request.InitialTrailerHeadings = trailers;
                            TurnPathResult legResult = TurnPathSimulator.Simulate(leg.Request);
                            trailers = legResult.Trailers.Count > 0 ? legResult.EndTrailerHeadings : null;
                            pose = legResult.Poses[legResult.Poses.Count - 1];
                            legs.Add(leg.Request);
                            legNotes.Add($"leg {legs.Count}: {(leg.Reverse ? "reverse" : "forward")}, " +
                                         (leg.ArcDeg > 0.5 ? $"turn {(leg.Left ? "left" : "right")} {leg.ArcDeg:0} deg then " : "") + $"{leg.StraightLength:0.0} m straight");
                        }

                        if (legs.Count == 0)
                        {
                            throw new ArgumentException("No leg could be driven: every destination is too close, inside the turning circle, or more than a half turn away. Drive past and reverse, or add points in between.");
                        }

                        req = legs[0];
                        label = vehicle.Name;
                        break;
                    }

                    case "solve":
                    {
                        // Solve: find the forward and reverse movements from the start pose to the target pose
                        // between the linework on the obstruction layers.
                        if (!(_host is IObstructionSource space))
                        {
                            throw new ArgumentException("This drawing host can't read obstruction linework, so it can't solve a manoeuvre.");
                        }

                        List<JsonElement>? layerItems = JsonArgs.OptionalArray(input, "obstruction_layers", "", 0, 100);
                        solveLayers = layerItems == null ? new List<string>() : JsonArgs.ReadStringList(layerItems, "obstruction_layers");
                        if (solveLayers.Count == 0)
                        {
                            solveLayers = c.ObstructionLayers.ToList();
                        }

                        if (solveLayers.Count == 0)
                        {
                            throw new ArgumentException("solve needs obstruction_layers: the layers with the kerbs, walls and columns the vehicle has to manoeuvre between.");
                        }

                        solveClearance = JsonArgs.OptionalNumber(input, "clearance_inside", "") ?? vehicle.ClearanceInside;
                        if (solveClearance < 0)
                        {
                            throw new ArgumentException("Clearances can't be negative.");
                        }

                        var record = new ManoeuvreRecord
                        {
                            Vehicle = vehicle.Name,
                            Mode = "solve",
                            UnitsPerMetre = units.PerMetre,
                            Start = units.ToModel(JsonArgs.RequirePoint(input, "start", "")).ToArray(),
                            HeadingDeg = JsonArgs.RequireNumber(input, "heading_deg", ""),
                            Target = units.ToModel(JsonArgs.RequirePoint(input, "target", "")).ToArray(),
                            TargetHeadingDeg = JsonArgs.RequireNumber(input, "target_heading_deg", ""),
                            Radius = JsonArgs.OptionalPositive(input, "radius", "") ?? vehicle.MinTurnRadius,
                            ClearanceInside = solveClearance,
                            ClearanceOutside = solveClearance,
                            ObstructionLayers = solveLayers,
                        };
                        if (record.Radius < vehicle.MinTurnRadius - 1e-9)
                        {
                            throw new ArgumentException($"A {record.Radius:0.##} m radius is below the {vehicle.Name}'s minimum of {vehicle.MinTurnRadius:0.#} m (AS/NZS 2890.1 B3.1).");
                        }

                        // Never treat the tool's own layers (an earlier swept path) as an obstruction.
                        TurnPathLayers ownLayers = TurnPathStyle.From(c).Layers;
                        ApplyLayers(input, ownLayers);
                        List<string> exclude = ManoeuvrePlanner.OwnLayers(new ManoeuvreRecord { Layers = ownLayers }, c);
                        List<string> layers = solveLayers;
                        solved = ManoeuvrePlanner.Solve(record, c, (min, max) => units.ToModel(space.Obstructions(layers, units.ToDrawing(min), units.ToDrawing(max), exclude)));
                        if (!solved.Solved)
                        {
                            throw new ArgumentException($"{solved.Summary()} {solved.ObstructionsUsed} object(s) on the obstruction layers were in range. " +
                                                        "Check the start and target positions and headings, or try a smaller clearance.");
                        }

                        legs = solved.Legs.Select(l => l.ToRequest(vehicle)).ToList();
                        legNotes.AddRange(solved.Legs.Select((l, i) => $"movement {i + 1}: {(l.Reverse ? "reverse" : "forward")} {l.Length:0.0} m"));
                        req = legs[0];
                        nominated = record.Radius;
                        label = legs.Count == 1 ? vehicle.Name : $"{vehicle.Name}, {legs.Count} movements";
                        break;
                    }

                    default:
                        throw new ArgumentException("mode must be template, follow, points or solve.");
                }

                req.Step = c.Step;
                req.Tolerance = c.Tolerance;
                req.ClearanceInside = JsonArgs.OptionalNumber(input, "clearance_inside", "");
                req.ClearanceOutside = JsonArgs.OptionalNumber(input, "clearance_outside", "");
                if (solved != null)
                {
                    // The manoeuvring clearance, the same on both sides: which side is inside changes with every movement.
                    req.ClearanceInside = req.ClearanceOutside = solveClearance;
                }

                if (req.ClearanceInside < 0 || req.ClearanceOutside < 0)
                {
                    throw new ArgumentException("Clearances can't be negative.");
                }

                TurnPathResult result = TurnPathSimulator.Simulate(req);
                warnings.AddRange(result.Warnings);
                var results = new List<TurnPathResult> { result };
                if (legs != null)
                {
                    for (int i = 1; i < legs.Count; i++)
                    {
                        legs[i].Step = c.Step;
                        legs[i].Tolerance = c.Tolerance;
                        legs[i].ClearanceInside = req.ClearanceInside;
                        legs[i].ClearanceOutside = req.ClearanceOutside;
                        TurnPathResult more = TurnPathSimulator.Simulate(legs[i]);
                        warnings.AddRange(more.Warnings.Select(w => $"leg {i + 1}: {w}"));
                        results.Add(more);
                    }
                }

                TurnPathStyle style = TurnPathStyle.From(c);
                style.Label = JsonArgs.OptionalString(input, "label", "") ?? label;
                ApplyLayers(input, style.Layers);
                ApplyDraw(input, style);

                var report = new Dictionary<string, object?>
                {
                    ["vehicle"] = vehicle.Name,
                    ["mode"] = mode,
                    ["drawing_units"] = units.IsMetres ? "metres" : $"{units.Name}: points and extents here are in drawing units; lengths, radii and clearances are in metres",
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
                    ["start"] = Pose(result.Poses[0], units),
                    ["end"] = Pose(results[results.Count - 1].Poses[results[results.Count - 1].Poses.Count - 1], units),
                    ["legs"] = legNotes.Count > 0 ? legNotes : null,
                    ["extents"] = Extents(result, units),
                };
                if (vehicle.IsArticulated)
                {
                    report["trailers"] = vehicle.Trailers.Select(t => t.Name).ToList();
                    report["max_articulation_deg"] = Math.Round(results.Max(x => x.MaxArticulationDeg), 1);
                }

                ConflictReport? conflicts = null;
                List<JsonElement>? obstructionItems = JsonArgs.OptionalArray(input, "obstruction_layers", "", 0, 100);
                List<string> obstructionLayers = solveLayers ?? (obstructionItems == null ? new List<string>() : JsonArgs.ReadStringList(obstructionItems, "obstruction_layers"));
                if (solved != null)
                {
                    report["solver"] = new
                    {
                        summary = solved.Summary(),
                        movements = solved.Movements,
                        clearance_asked_mm = Math.Round(solved.ClearanceAsked * 1000),
                        clearance_kept_mm = Math.Round(solved.ClearanceUsed * 1000),
                        clearance_reduced = solved.ClearanceReduced,
                        tight_at_start_or_target = solved.TightAtEnds,
                        obstructions_in_range = solved.ObstructionsUsed,
                        positions_tried = solved.Expansions,
                    };
                }

                if (obstructionLayers.Count > 0)
                {
                    if (_host is IObstructionSource source)
                    {
                        var (min, max) = ConflictChecker.Window(results);
                        var own = new ManoeuvreRecord { Layers = style.Layers };
                        conflicts = ConflictChecker.Check(results, units.ToModel(source.Obstructions(obstructionLayers, units.ToDrawing(min), units.ToDrawing(max), ManoeuvrePlanner.OwnLayers(own, c))));
                        report["conflicts"] = new
                        {
                            summary = conflicts.Summary(),
                            passes = conflicts.Passes,
                            obstructions_checked = conflicts.ObstructionsChecked,
                            closest_gap_mm = conflicts.ClosestGap is double g ? (object)Math.Round(g * 1000) : null,
                            items = conflicts.Conflicts.Select(k => new
                            {
                                leg = k.Leg,
                                layer = k.Layer,
                                handle = k.Handle,
                                kind = k.BodyHit ? "body_hit" : "clearance",
                                depth_mm = k.BodyHit ? (object)Math.Round(k.Depth * 1000) : null,
                                gap_mm = k.BodyHit ? null : (object)Math.Round(k.Gap * 1000),
                                required_mm = Math.Round(k.Required * 1000),
                                at = units.ToDrawing(k.At).ToArray(),
                            }).ToList(),
                        };
                    }
                    else
                    {
                        warnings.Add("This drawing host can't read obstruction linework, so conflicts weren't checked.");
                    }
                }

                if (dryRun)
                {
                    report["dry_run"] = true;
                }
                else
                {
                    var drafted = new List<DraftedEntity>();
                    if (conflicts != null)
                    {
                        drafted.AddRange(ConflictChecker.Draw(conflicts, style.Layers.Resolve(vehicle.Name).Conflict, style.TextHeight));
                    }

                    for (int i = 0; i < results.Count; i++)
                    {
                        var legStyle = new TurnPathStyle
                        {
                            Layers = style.Layers,
                            Draw = style.Draw.Clone(),
                            OutlineInterval = style.OutlineInterval,
                            TextHeight = style.TextHeight,
                            Hatch = style.Hatch,
                            Label = style.Label,
                            SkipStartVehicle = i > 0,
                        };
                        legStyle.Draw.Labels = style.Draw.Labels && i == 0;
                        drafted.AddRange(TurnPathDrafter.Draw(results[i], legStyle));
                    }

                    drafted = units.ToDrawing(drafted);
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
                            BlockResult block = maker.MakeBlock(all, name, units.ToDrawing(result.Poses[0].RearAxle), style.Layers.Resolve(vehicle.Name).Block);
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

        private static object Pose(VehiclePose p, DrawingUnits units) => new
        {
            rear_axle = units.ToDrawing(p.RearAxle).ToArray(),
            heading_deg = Math.Round(((p.Heading * 180 / Math.PI) % 360 + 360) % 360, 2),
        };

        private static object Extents(TurnPathResult r, DrawingUnits units)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (Point2 p in r.LeftClearance.Concat(r.RightClearance).Concat(r.LeftBody).Concat(r.RightBody))
            {
                minX = Math.Min(minX, p.X);
                minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X);
                maxY = Math.Max(maxY, p.Y);
            }

            double k = units.PerMetre;
            return new { min = new[] { Math.Round(minX * k, 3), Math.Round(minY * k, 3) }, max = new[] { Math.Round(maxX * k, 3), Math.Round(maxY * k, 3) } };
        }
    }
}
