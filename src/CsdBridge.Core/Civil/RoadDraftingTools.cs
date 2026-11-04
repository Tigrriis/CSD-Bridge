using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Tools;

namespace CsdBridge.Core.Civil
{
    /// <summary>The draft_road tool: a whole road drafted to the office conventions in one call.</summary>
    public sealed class RoadDraftingTools : IToolExtension
    {
        public const string DraftRoad = "draft_road";

        private readonly IDrawingHost _host;
        private readonly RoadConventions? _conventions;
        private readonly string? _loadError;

        /// <param name="conventions">Null when the conventions file couldn't be loaded (loadError says why).</param>
        public RoadDraftingTools(IDrawingHost host, RoadConventions? conventions, string? loadError = null)
        {
            _host = host;
            _conventions = conventions;
            _loadError = loadError;
            Tools = new List<ToolDefinition>
            {
                new ToolDefinition(
                    DraftRoad,
                    "Draft a Civil Site Design road in plan to the office conventions, in one call: the centreline from the road's " +
                    "exact alignment, road lines (kerbs, batters...) and closed outlines with hatches for areas between two codes " +
                    "(pavement, shoulders, footpaths...). The geometry follows the alignment exactly (arcs stay arcs); CSD's code strings " +
                    "only give the offsets, fitted as constant stretches and straight tapers, so the chords between CSD's sample " +
                    "sections don't show. 'road' is the road name in CSD's string layers (get_drawing_info lists them under csd_roads). " +
                    "The alignment is found from the alignment layers unless given. Use dry_run first on a new road to check the offsets " +
                    "and what will be drawn. Junctions between roads are not trimmed: tidy them afterwards with trim_extend and fillet. " +
                    "Returns the handles drawn and each code's offsets.",
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"road\":{\"type\":\"string\"}," +
                    "\"alignment\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Alignment handle(s), if not found automatically.\"}," +
                    "\"start_chainage\":{\"type\":\"number\",\"description\":\"Chainage at the alignment's start (default 0).\"}," +
                    "\"from_chainage\":{\"type\":\"number\"},\"to_chainage\":{\"type\":\"number\"}," +
                    "\"only\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Only these items, by name (centreline, or line/area names from the conventions).\"}," +
                    "\"dry_run\":{\"type\":\"boolean\"}" +
                    "},\"required\":[\"road\"]}"),
            };
        }

        public IReadOnlyList<ToolDefinition> Tools { get; }

        public string? PromptSection => _conventions == null
            ? null
            : "## Road drafting conventions\n" +
              "Draft CSD roads with draft_road, which applies these conventions (from road-drafting.json):\n" +
              _conventions.Describe() +
              "Anything else (junction clean-up, linemarking, labels) uses the general tools on the same layers.";

        public bool RunsOnMainThread(string toolName) => true;

        public string? DescribeCall(string toolName, JsonElement input) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty("road", out JsonElement road) && road.ValueKind == JsonValueKind.String
                ? (input.TryGetProperty("dry_run", out JsonElement dr) && dr.ValueKind == JsonValueKind.True ? "checking" : "drafting") + $" road {road.GetString()}"
                : "drafting a road";

        public ToolOutcome Execute(string toolName, JsonElement input)
        {
            if (_conventions == null)
            {
                return new ToolOutcome("The road drafting conventions couldn't be loaded: " + (_loadError ?? "no road-drafting.json") + ". Fix the file, then start a new chat.", true);
            }

            try
            {
                JsonArgs.RequireObject(input, "");
                var r = new DraftRoadRequest
                {
                    Road = JsonArgs.RequireString(input, "road", "").Trim(),
                    StartChainage = JsonArgs.OptionalNumber(input, "start_chainage", "") ?? 0,
                    From = JsonArgs.OptionalNumber(input, "from_chainage", ""),
                    To = JsonArgs.OptionalNumber(input, "to_chainage", ""),
                    DryRun = JsonArgs.OptionalBool(input, "dry_run", "", false),
                };
                List<JsonElement>? alignment = JsonArgs.OptionalArray(input, "alignment", "", 1, 200);
                if (alignment != null)
                {
                    r.Alignment = JsonArgs.ReadStringList(alignment, "alignment");
                }

                List<JsonElement>? only = JsonArgs.OptionalArray(input, "only", "", 1, 50);
                if (only != null)
                {
                    r.Only = JsonArgs.ReadStringList(only, "only");
                }

                RoadDrafter.Report report = new RoadDrafter(_host, _conventions).Draft(r);
                int drawn = report.Drawn.Count;
                return new ToolOutcome(JsonSerializer.Serialize(report, ToolDispatcher.JsonOptions), false)
                {
                    Summary = r.DryRun ? $"checked road {r.Road}: {drawn} object(s) planned" : $"drafted road {r.Road}: {drawn} object(s)",
                };
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
    }
}
