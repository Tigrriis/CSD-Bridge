using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CsdBridge.Core.Civil;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;
using CsdBridge.Core.Tools;

namespace CsdBridge.Core.Vehicles
{
    /// <summary>
    /// Optional ability of a drawing host: read levels from a terrain surface in the drawing.
    /// </summary>
    public interface IGroundSurfaceProvider
    {
        /// <summary>Names of the surfaces in the drawing that can be sampled.</summary>
        IReadOnlyList<string> SurfaceNames();

        /// <summary>
        /// A sampler for the named surface (null = the only one, or the first). Returns a level for
        /// each plan point, null where the point is off the surface. Throws when there is no such surface.
        /// </summary>
        Func<IReadOnlyList<Point2>, IReadOnlyList<double?>> Sampler(string? surfaceName, out string resolvedName);
    }

    /// <summary>The check_ground_clearance tool: AS/NZS 2890.1 Appendix C, on a long section or a surface.</summary>
    public sealed class GroundClearanceTools : IToolExtension
    {
        public const string CheckGroundClearance = "check_ground_clearance";

        private const string Point = "{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2}";

        private readonly IDrawingHost _host;
        private readonly TurnPathConventions? _conventions;
        private readonly string? _loadError;

        public GroundClearanceTools(IDrawingHost host, TurnPathConventions? conventions, string? loadError = null)
        {
            _host = host;
            _conventions = conventions;
            _loadError = loadError;
            Tools = new List<ToolDefinition>
            {
                new ToolDefinition(
                    CheckGroundClearance,
                    "Check an AS/NZS 2890.1 design vehicle's ground clearance (Appendix C template: 120 mm under the body between the " +
                    "axles, underside rising at 10 degrees over the overhangs) against a grade change: a hump, dip, gutter or ramp. " +
                    "Mode 'section': slide the template along a long section, both wheels on the plot, in both directions. Give the " +
                    "section as the handle of a plotted profile polyline (with its vertical_exaggeration and horizontal_scale), a " +
                    "ground_id from sample_ground_profile/sample_csd_ground, or [chainage, level] points; results are drawn onto a " +
                    "plotted polyline, or at plot_at for the other inputs. Mode 'surface': drive the vehicle along a path (handle or " +
                    "points, like draw_turn_path 'follow'/'points') and check each side of the car against a terrain surface in the " +
                    "drawing at every position; grounding spots are ringed on the plan and, with plot_at, each wheel path is plotted " +
                    "as a long section with the template at its worst position. Returns the smallest underside-to-ground gap " +
                    "(negative = grounds), where, and the stretches that ground. B99 applies everywhere except domestic driveways (B85).",
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"vehicle\":{\"type\":\"string\"}," +
                    "\"mode\":{\"type\":\"string\",\"enum\":[\"section\",\"surface\"]}," +
                    "\"handle\":{\"type\":\"string\",\"description\":\"Section: the plotted profile polyline. Surface: the path to drive.\"}," +
                    "\"ground_id\":{\"type\":\"string\"}," +
                    "\"points\":{\"type\":\"array\",\"items\":" + Point + ",\"minItems\":2,\"maxItems\":2000,\"description\":\"Section: [chainage, level] pairs. Surface: plan points of the path.\"}," +
                    "\"vertical_exaggeration\":{\"type\":\"number\",\"description\":\"Of the plotted section (default 1), also used for plots made at plot_at.\"}," +
                    "\"horizontal_scale\":{\"type\":\"number\",\"description\":\"Drawing units per metre of chainage on the plot (default 1).\"}," +
                    "\"both_directions\":{\"type\":\"boolean\",\"description\":\"Section: also run facing down chainage (default true).\"}," +
                    "\"surface\":{\"type\":\"string\",\"description\":\"Surface name; default the only one.\"}," +
                    "\"radius\":{\"type\":\"number\",\"description\":\"Surface with points: corner radius of the path.\"}," +
                    "\"reverse\":{\"type\":\"boolean\"}," +
                    "\"pose_interval\":{\"type\":\"number\",\"description\":\"Surface: distance between checked positions (default 0.1).\"}," +
                    "\"ground_clearance\":{\"type\":\"number\",\"description\":\"Underside height to check with (m); default the vehicle's (0.12).\"}," +
                    "\"step\":{\"type\":\"number\",\"description\":\"Section: distance the template is moved each step (default 0.05).\"}," +
                    "\"plot_at\":" + Point.Replace("}", ",\"description\":\"Draw long section plot(s) with their bottom-left here.\"}") + "," +
                    "\"draw\":{\"type\":\"boolean\",\"description\":\"Draw the results (default true).\"}," +
                    "\"layers\":{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"},\"description\":\"Overrides: clearance_check, clearance_fail, labels.\"}" +
                    "},\"required\":[\"mode\"]}"),
            };
        }

        public IReadOnlyList<ToolDefinition> Tools { get; }

        public string? PromptSection =>
            "## Ground clearance (AS/NZS 2890.1 Appendix C)\n" +
            "Use check_ground_clearance wherever a ramp, hump, dip or gutter changes grade on a vehicle path. The template has 120 mm " +
            "clearance between the axles and 10 degree approach/departure angles (B5); a grade change over 8 degrees (14 %) needs " +
            "two stages at least a wheelbase apart or a vertical curve (B5), and the Standard caps ramp grade changes at 12.5 % " +
            "(Clause 2.5.3). For a CSD surface, make a BricsCAD copy with Surface Exchange or sample along the wheel paths with " +
            "sample_csd_ground and check the ground_id.";

        public bool RunsOnMainThread(string toolName) => true;

        public string? DescribeCall(string toolName, JsonElement input) => "checking ground clearance";

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
                double? clearance = JsonArgs.OptionalNumber(input, "ground_clearance", "");
                bool draw = JsonArgs.OptionalBool(input, "draw", "", true);
                double ve = JsonArgs.OptionalPositive(input, "vertical_exaggeration", "") ?? c.SectionVerticalExaggeration;
                double hs = JsonArgs.OptionalPositive(input, "horizontal_scale", "") ?? 1;
                Point2? plotAt = JsonArgs.OptionalPoint(input, "plot_at", "");
                TurnPathLayers layers = c.Layers.Clone();
                ApplyLayers(input, layers);
                var warnings = new List<string>();
                var drafted = new List<DraftedEntity>();
                var report = new Dictionary<string, object?> { ["vehicle"] = vehicle.Name, ["mode"] = mode, ["ground_clearance"] = clearance ?? vehicle.GroundClearance };

                switch (mode)
                {
                    case "section":
                    {
                        string? handle = JsonArgs.OptionalString(input, "handle", "");
                        string? groundId = JsonArgs.OptionalString(input, "ground_id", "");
                        List<JsonElement>? pts = JsonArgs.OptionalArray(input, "points", "", 2, 2000);
                        GroundProfile profile;
                        SectionFrame? frame = null;
                        if (!string.IsNullOrWhiteSpace(handle))
                        {
                            IReadOnlyDictionary<string, EntitySpec> curves = _host.GetCurves(new[] { handle! });
                            if (curves.Count == 0)
                            {
                                throw new ArgumentException($"{handle} is not a line, arc or polyline in this drawing.");
                            }

                            Path2 plot = Path2.From(curves.Values.First()) ?? throw new ArgumentException("That entity has no curve geometry.");
                            profile = GroundClearanceChecker.ProfileFromPlot(plot, ve, hs);
                            frame = SectionFrame.OfPlot(ve, hs);
                        }
                        else if (!string.IsNullOrWhiteSpace(groundId))
                        {
                            profile = GroundProfileStore.Get(groundId!) ?? throw new ArgumentException($"ground_id '{groundId}' is unknown in this session; sample the ground again.");
                        }
                        else if (pts != null)
                        {
                            profile = new GroundProfile(pts.Select((e, i) =>
                            {
                                Point2 p = JsonArgs.ReadPoint(e, JsonArgs.Index("points", i));
                                return new GroundPoint(p.X, p.Y);
                            }));
                        }
                        else
                        {
                            throw new ArgumentException("Section mode needs handle, ground_id or points.");
                        }

                        double step = JsonArgs.OptionalPositive(input, "step", "") ?? c.ClearanceStep;
                        GroundClearanceResult r = GroundClearanceChecker.CheckProfile(profile, vehicle, step, JsonArgs.OptionalBool(input, "both_directions", "", true), clearance);
                        warnings.AddRange(r.Warnings);
                        report["summary"] = r.Summary();
                        report["passes"] = r.Passes;
                        report["min_gap_mm"] = Math.Round(r.MinGap * 1000);
                        report["worst"] = Describe(r.Worst);
                        report["worst_facing_up_chainage"] = r.WorstForward == null ? null : Describe(r.WorstForward);
                        report["worst_facing_down_chainage"] = r.WorstBackward == null ? null : Describe(r.WorstBackward);
                        report["groundings"] = r.Groundings.Select(g => new { rear_wheel_from = Math.Round(g.From, 2), rear_wheel_to = Math.Round(g.To, 2), facing = g.Forward ? "up chainage" : "down chainage", worst_gap_mm = Math.Round(g.WorstGap * 1000), part = g.Part }).ToList();
                        report["profile"] = new { from = Math.Round(profile.Start, 3), to = Math.Round(profile.End, 3), points = profile.Points.Count };

                        if (draw)
                        {
                            if (frame == null && plotAt is Point2 at)
                            {
                                double datum = Math.Floor(profile.Points.Min(p => p.Level)) - 0.5;
                                frame = new SectionFrame { Origin = at, Datum = datum, VerticalExaggeration = ve };
                                drafted.Add(new DraftedEntity("clearance_section", new PolylineSpec { Layer = layers.Resolve(vehicle.Name).ClearanceCheck, Points = profile.Points.Select(p => frame.ToDrawing(p.Chainage, p.Level)).ToList() }));
                            }

                            if (frame != null)
                            {
                                drafted.AddRange(GroundClearanceDrafter.DrawOnSection(r, frame, layers, c.TextHeight));
                            }
                            else
                            {
                                warnings.Add("Nothing was drawn: give plot_at to plot the section, or the handle of a plotted section.");
                            }
                        }

                        break;
                    }

                    case "surface":
                    {
                        if (!(_host is IGroundSurfaceProvider surfaces))
                        {
                            throw new ArgumentException("This drawing host has no terrain surfaces. In BricsCAD, make a BricsCAD copy of a CSD surface with Surface Exchange first; or sample along the wheel paths with sample_csd_ground and check the ground_id in section mode.");
                        }

                        Func<IReadOnlyList<Point2>, IReadOnlyList<double?>> sampler = surfaces.Sampler(JsonArgs.OptionalString(input, "surface", ""), out string surfaceName);
                        bool reverse = JsonArgs.OptionalBool(input, "reverse", "", false);
                        VehiclePoint reference = vehicle.Resolve(reverse ? ReferencePoint.RearAxle : ReferencePoint.FrontAxle, leftTurn: true);
                        Path2 path;
                        string? handle = JsonArgs.OptionalString(input, "handle", "");
                        if (!string.IsNullOrWhiteSpace(handle))
                        {
                            IReadOnlyDictionary<string, EntitySpec> curves = _host.GetCurves(new[] { handle! });
                            if (curves.Count == 0)
                            {
                                throw new ArgumentException($"{handle} is not a line, arc or polyline in this drawing.");
                            }

                            path = TurnPathBuilder.FromCurve(curves.Values.First(), null);
                        }
                        else
                        {
                            List<JsonElement> items = JsonArgs.RequireArray(input, "points", "", 2, 200);
                            var pts = items.Select((e, i) => JsonArgs.ReadPoint(e, JsonArgs.Index("points", i))).ToList();
                            double radius = JsonArgs.OptionalPositive(input, "radius", "") ?? vehicle.RadiusOfPoint(vehicle.MinTurnRadius, reference, left: true);
                            path = TurnPathBuilder.FromPoints(pts, radius, warnings);
                        }

                        TurnPathResult driven = TurnPathSimulator.Simulate(new TurnPathRequest { Vehicle = vehicle, Path = path, Reference = reference, Reverse = reverse, Step = c.Step, Tolerance = c.Tolerance });
                        double interval = JsonArgs.OptionalPositive(input, "pose_interval", "") ?? c.ClearancePoseInterval;
                        PathClearanceResult r = GroundClearanceChecker.CheckAlongPath(driven, sampler, interval, clearance);
                        warnings.AddRange(r.Warnings);
                        report["surface"] = surfaceName;
                        report["summary"] = r.Summary();
                        report["passes"] = r.Passes;
                        report["min_gap_mm"] = r.Worst == null ? null : (object)Math.Round(r.MinGap * 1000);
                        report["sides"] = r.Sides.Select(s => new
                        {
                            side = s.Name,
                            worst = s.Worst == null ? null : new { chainage = Math.Round(s.Worst.Chainage, 2), gap_mm = Math.Round(s.Worst.MinGap * 1000), part = s.Worst.Part, at = s.Worst.GapPoint.ToArray() },
                            groundings = s.Groundings.Select(g => new { from = Math.Round(g.From, 2), to = Math.Round(g.To, 2), worst_gap_mm = Math.Round(g.WorstGap * 1000) }).ToList(),
                            positions_checked = s.Samples.Count - s.OffSurfaceCount,
                        }).ToList();

                        if (draw)
                        {
                            drafted.AddRange(GroundClearanceDrafter.DrawPlan(r, layers, c.TextHeight));
                            if (plotAt is Point2 at)
                            {
                                drafted.AddRange(GroundClearanceDrafter.DrawSectionPlots(r, at, ve, layers, c.TextHeight));
                            }
                        }

                        break;
                    }

                    default:
                        throw new ArgumentException("mode must be section or surface.");
                }

                if (drafted.Count > 0)
                {
                    DrawingInfo info = _host.GetDrawingInfo();
                    List<LayerSpec> missing = TurnPathDrafter.MissingLayers(drafted.Select(d => d.Spec.Layer).Where(l => l != null).Select(l => l!), info.Layers.Select(l => l.Name), c.LayerStyles);
                    if (missing.Count > 0)
                    {
                        _host.EnsureLayers(missing);
                        report["created_layers"] = missing.Select(m => m.Name).ToList();
                    }

                    IReadOnlyList<ItemResult> created = _host.CreateEntities(drafted.Select(d => d.Spec).ToList());
                    var handles = new Dictionary<string, List<string>>();
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
                    }

                    report["drawn"] = handles;
                }

                if (warnings.Count > 0)
                {
                    report["warnings"] = warnings;
                }

                string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
                return new ToolOutcome(json, false) { Summary = report["summary"] as string };
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

        private static object Describe(ClearancePosition p) => new
        {
            rear_wheel_chainage = Math.Round(p.RearChainage, 2),
            front_wheel_chainage = Math.Round(p.FrontChainage, 2),
            facing = p.Forward ? "up chainage" : "down chainage",
            gap_mm = Math.Round(p.MinGap * 1000),
            gap_chainage = Math.Round(p.GapChainage, 2),
            part = p.Part,
        };

        private static void ApplyLayers(JsonElement input, TurnPathLayers layers)
        {
            if (!JsonArgs.TryGet(input, "layers", out JsonElement obj) || obj.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (JsonProperty p in obj.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()) && !layers.Set(p.Name, p.Value.GetString()!.Trim()))
                {
                    throw new ArgumentException($"layers.{p.Name}: unknown part.");
                }
            }
        }
    }
}
