using System.Collections.Generic;

namespace CsdBridge.Core.Tools
{
    /// <summary>A tool as Claude sees it: name, description and JSON Schema for its input.</summary>
    public sealed class ToolDefinition
    {
        public ToolDefinition(string name, string description, string inputSchemaJson)
        {
            Name = name;
            Description = description;
            InputSchemaJson = inputSchemaJson;
        }

        public string Name { get; }
        public string Description { get; }

        /// <summary>The full input_schema object as JSON ({"type":"object","properties":...}).</summary>
        public string InputSchemaJson { get; }
    }

    /// <summary>
    /// The drafting tools offered to Claude. Descriptions are part of the prompt:
    /// they are written to steer Claude towards batching work into few calls and
    /// towards reading the drawing before changing it.
    /// </summary>
    public static class ToolCatalog
    {
        public const string GetDrawingInfo = "get_drawing_info";
        public const string ListEntities = "list_entities";
        public const string EnsureLayers = "ensure_layers";
        public const string DrawEntities = "draw_entities";
        public const string ModifyEntities = "modify_entities";
        public const string PickPoint = "pick_point";
        public const string SelectEntities = "select_entities";
        public const string Calculate = "calculate";
        public const string OffsetEntities = "offset_entities";
        public const string Intersect = "intersect";
        public const string Measure = "measure";
        public const string PointsOnCurve = "points_on_curve";
        public const string Hatch = "hatch";
        public const string TrimExtend = "trim_extend";
        public const string Fillet = "fillet";
        public const string JoinCurves = "join_curves";
        public const string OffsetByChainage = "offset_by_chainage";
        public const string AnalyseOffsets = "analyse_offsets";

        public const int MaxEntitiesPerCall = 500;
        public const int MaxOperationsPerCall = 200;
        public const int MaxListLimit = 500;

        private const string Point =
            "{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2}";

        public static IReadOnlyList<ToolDefinition> All { get; } = new List<ToolDefinition>
        {
            new ToolDefinition(
                GetDrawingInfo,
                "Read the active drawing's setup: drawing units, extents, current layer/text style/dimension style, " +
                "default text height, and the existing layers as rows [name, colour, linetype, lineweight mm, state], " +
                "linetypes, text styles, dimension styles and block definitions. Xref layers are left out unless asked for. " +
                "Civil Site Design's string layers (CSD-3D-<road>-<code>, CSD-2D-...) are summarised as csd_roads: each road " +
                "with its codes. Call this once at the start of a task before drawing so you use the drawing's real units, " +
                "layers and blocks; use layer_filter to look for particular layers.",
                "{\"type\":\"object\",\"properties\":{" +
                "\"layer_filter\":{\"type\":\"string\",\"description\":\"Only layers whose name contains this (case-insensitive).\"}," +
                "\"include_xref_layers\":{\"type\":\"boolean\"}}}"),

            new ToolDefinition(
                ListEntities,
                "List entities in the current space (model space or the active layout) with their key geometry: " +
                "handle, kind, layer, and kind-specific fields (line start/end, circle/arc centre and radius, polyline " +
                "vertices, text content and position, block name, dimension measurement, bounding box). All filters are " +
                "optional and combine with AND; window_min/window_max keep entities whose bounding box overlaps that rectangle. " +
                "Use a window or layer filter on large drawings; results are capped by " +
                "'limit' and report 'truncated' when more matched. Handles returned here are what modify_entities takes.",
                "{\"type\":\"object\",\"properties\":{" +
                "\"layer\":{\"type\":\"string\",\"description\":\"Only entities on this layer (case-insensitive).\"}," +
                "\"kinds\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"enum\":[\"line\",\"polyline\",\"circle\",\"arc\",\"text\",\"mtext\",\"dimension\",\"block_insert\",\"hatch\",\"other\"]},\"description\":\"Only these entity kinds.\"}," +
                "\"handles\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Only these handles.\"}," +
                "\"window_min\":" + Point + "," +
                "\"window_max\":" + Point + "," +
                "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500,\"description\":\"Maximum entities to return (default 100).\"}" +
                "}}"),

            new ToolDefinition(
                EnsureLayers,
                "Create layers that do not exist yet, or update colour/linetype/lineweight/description of existing ones. " +
                "Prefer the office layer standard from the system prompt. Linetypes are loaded from the CAD linetype " +
                "files if they are not in the drawing yet.",
                "{\"type\":\"object\",\"properties\":{" +
                "\"layers\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":100,\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"name\":{\"type\":\"string\"}," +
                "\"color\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":255,\"description\":\"AutoCAD Color Index (1 red, 2 yellow, 3 green, 4 cyan, 5 blue, 6 magenta, 7 white/black, 8 dark grey, 9 light grey).\"}," +
                "\"linetype\":{\"type\":\"string\",\"description\":\"e.g. CONTINUOUS, HIDDEN, CENTER, DASHED, PHANTOM.\"}," +
                "\"lineweight_mm\":{\"type\":\"number\",\"description\":\"Plotted lineweight in mm, e.g. 0.18, 0.25, 0.35, 0.5, 0.7.\"}," +
                "\"description\":{\"type\":\"string\"}," +
                "\"rgb\":{\"type\":\"array\",\"items\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":255},\"minItems\":3,\"maxItems\":3,\"description\":\"True colour, instead of color.\"}," +
                "\"plot\":{\"type\":\"boolean\",\"description\":\"false = non-plotting layer.\"}" +
                "},\"required\":[\"name\"]}}" +
                "},\"required\":[\"layers\"]}"),

            new ToolDefinition(
                DrawEntities,
                "Draw one or more entities in the current space in a single call. Batch everything you can into one " +
                "call (up to 500 entities) rather than one call per entity. Coordinates are world X/Y in drawing units; " +
                "angles are degrees counter-clockwise from +X. Each item has a 'kind' and the fields for that kind:\n" +
                "- line: start, end\n" +
                "- polyline: points (>=2), closed (default false), optional bulges (one per vertex; bulge = tan(sweep/4), " +
                "positive = counter-clockwise arc to the next vertex). Use closed polylines for rectangles and outlines.\n" +
                "- circle: center, radius\n" +
                "- arc: center, radius, start_angle, end_angle (drawn counter-clockwise from start to end)\n" +
                "- text: position, text (single line), optional height, rotation, align (left|center|right|middle_center; " +
                "position is the alignment point)\n" +
                "- mtext: position (top-left corner), text (use \\n for new lines), optional height, width (wrap), rotation\n" +
                "- linear_dimension: p1, p2 (points being measured), dim_line_point (a point the dimension line passes " +
                "through), rotation (0 = horizontal, 90 = vertical), optional text_override\n" +
                "- aligned_dimension: p1, p2, dim_line_point, optional text_override\n" +
                "- block_insert: block_name (must exist - see get_drawing_info), position, optional scale, rotation, " +
                "attributes (object of TAG: value, e.g. title block fields; other attributes keep their defaults)\n" +
                "Every item may also set 'layer' (created if missing; omit for the current layer), 'color' (ACI; " +
                "omit for ByLayer, which is preferred) and 'linetype_scale'. Omitted heights use the drawing's default text height / dimension " +
                "style. Returns one result per item, in order, with the new entity's handle.",
                "{\"type\":\"object\",\"properties\":{" +
                "\"entities\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":500,\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"kind\":{\"type\":\"string\",\"enum\":[\"line\",\"polyline\",\"circle\",\"arc\",\"text\",\"mtext\",\"linear_dimension\",\"aligned_dimension\",\"block_insert\"]}," +
                "\"layer\":{\"type\":\"string\"}," +
                "\"color\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":255}," +
                "\"linetype_scale\":{\"type\":\"number\"}," +
                "\"start\":" + Point + ",\"end\":" + Point + "," +
                "\"points\":{\"type\":\"array\",\"items\":" + Point + ",\"minItems\":2}," +
                "\"bulges\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}}," +
                "\"closed\":{\"type\":\"boolean\"}," +
                "\"center\":" + Point + ",\"radius\":{\"type\":\"number\"}," +
                "\"start_angle\":{\"type\":\"number\"},\"end_angle\":{\"type\":\"number\"}," +
                "\"position\":" + Point + "," +
                "\"text\":{\"type\":\"string\"},\"height\":{\"type\":\"number\"},\"width\":{\"type\":\"number\"}," +
                "\"rotation\":{\"type\":\"number\"}," +
                "\"align\":{\"type\":\"string\",\"enum\":[\"left\",\"center\",\"right\",\"middle_center\"]}," +
                "\"p1\":" + Point + ",\"p2\":" + Point + ",\"dim_line_point\":" + Point + "," +
                "\"text_override\":{\"type\":\"string\"}," +
                "\"block_name\":{\"type\":\"string\"},\"scale\":{\"type\":\"number\"}," +
                "\"attributes\":{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"}}" +
                "},\"required\":[\"kind\"]}}" +
                "},\"required\":[\"entities\"]}"),

            new ToolDefinition(
                ModifyEntities,
                "Change existing entities by handle. Operations run in order; each applies to all of its handles:\n" +
                "- move: dx, dy\n" +
                "- copy: dx, dy, optional count (default 1); copy k is displaced by k*(dx, dy), so count gives a linear " +
                "array. Returns the new handles.\n" +
                "- rotate: base_point, angle (degrees, counter-clockwise)\n" +
                "- scale: base_point, factor\n" +
                "- erase\n" +
                "- set_layer: layer (created if missing)\n" +
                "- set_color: color (ACI 1-255, or omit for ByLayer)\n" +
                "Get handles from draw_entities, list_entities or select_entities.",
                "{\"type\":\"object\",\"properties\":{" +
                "\"operations\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":200,\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"action\":{\"type\":\"string\",\"enum\":[\"move\",\"copy\",\"rotate\",\"scale\",\"erase\",\"set_layer\",\"set_color\"]}," +
                "\"handles\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"minItems\":1}," +
                "\"dx\":{\"type\":\"number\"},\"dy\":{\"type\":\"number\"}," +
                "\"count\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500}," +
                "\"base_point\":" + Point + "," +
                "\"angle\":{\"type\":\"number\"},\"factor\":{\"type\":\"number\"}," +
                "\"layer\":{\"type\":\"string\"},\"color\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":255}" +
                "},\"required\":[\"action\",\"handles\"]}}" +
                "},\"required\":[\"operations\"]}"),

            new ToolDefinition(
                Calculate,
                "Evaluate arithmetic exactly. Use this for every non-trivial number you need (bay widths, spacings, " +
                "set-out coordinates, trigonometry, unit conversions) instead of working it out yourself. Give a list of " +
                "expressions; each may have a name that later expressions can use, e.g. " +
                "[{\"name\":\"bays\",\"expr\":\"ceil(6000/2400)\"},{\"name\":\"bay\",\"expr\":\"6000/bays\"}]. " +
                "Operators + - * / % ^ and parentheses; functions sqrt, abs, round(x[, digits]), floor, ceil, min, max, " +
                "hypot, sin, cos, tan, asin, acos, atan, atan2(y, x) with angles in degrees; constant pi. An error in one " +
                "expression doesn't stop the others.",
                "{\"type\":\"object\",\"properties\":{" +
                "\"expressions\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":200,\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"name\":{\"type\":\"string\",\"description\":\"Optional; letters, digits and _, starting with a letter.\"}," +
                "\"expr\":{\"type\":\"string\"}" +
                "},\"required\":[\"expr\"]}}" +
                "},\"required\":[\"expressions\"]}"),

            new ToolDefinition(
                OffsetEntities,
                "Offset curves (lines, polylines, arcs, circles) by an exact distance using the CAD geometry engine. Use " +
                "this instead of computing offset coordinates yourself: cover lines, wall thicknesses, setbacks, parallel " +
                "lines. Each operation applies to its handles and needs distance (> 0) and a side: side_point (any point " +
                "on the side to offset towards) or side 'inside' / 'outside' (closed shapes only). Optional count makes " +
                "offsets at distance, 2 x distance, ...; optional layer puts the new curves on that layer (created if " +
                "missing; default: the source's layer). Corners of offset polylines are mitred. Returns the new handles.",
                "{\"type\":\"object\",\"properties\":{" +
                "\"offsets\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":100,\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"handles\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"minItems\":1}," +
                "\"distance\":{\"type\":\"number\"}," +
                "\"side_point\":" + Point + "," +
                "\"side\":{\"type\":\"string\",\"enum\":[\"inside\",\"outside\"]}," +
                "\"count\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100}," +
                "\"layer\":{\"type\":\"string\"}" +
                "},\"required\":[\"handles\",\"distance\"]}}" +
                "},\"required\":[\"offsets\"]}"),

            new ToolDefinition(
                Intersect,
                "Find exact intersection points between entities (lines, polylines, arcs, circles). Every pair among " +
                "handles is checked; or give with to check each of handles against each of with. extend: true treats " +
                "lines as infinite and arcs as full circles, to find where things would meet if extended (e.g. wall " +
                "centrelines). Returns only the pairs that intersect, with their points.",
                "{\"type\":\"object\",\"properties\":{" +
                "\"handles\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"minItems\":1,\"maxItems\":200}," +
                "\"with\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":200}," +
                "\"extend\":{\"type\":\"boolean\"}" +
                "},\"required\":[\"handles\"]}"),

            new ToolDefinition(
                Measure,
                "Measure exactly. For entities (handles): length, enclosed area and open/closed for curves, start, end " +
                "and mid points, bounding box, and the measured value of dimensions; with point, also each entity's " +
                "closest point to it and the distance. With from and to (no entities needed): distance, dx, dy and angle " +
                "between two points. Use this to get exact positions to build on and to check your work.",
                "{\"type\":\"object\",\"properties\":{" +
                "\"handles\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":500}," +
                "\"point\":" + Point + "," +
                "\"from\":" + Point + "," +
                "\"to\":" + Point +
                "}}"),

            new ToolDefinition(
                PointsOnCurve,
                "Exact set-out points along curves (lines, polylines, arcs, circles): e.g. pier positions every 1800 " +
                "along a wall line, or a beam divided into 5 equal bays. Each request has a handle and one of: divide " +
                "(number of equal parts; open curves include both ends), spacing (with optional start_offset; points from " +
                "the start up to the end), or distances (explicit distances along the curve). from_end: true measures " +
                "from the end. Returns each point with its distance along and the curve's direction there (degrees).",
                "{\"type\":\"object\",\"properties\":{" +
                "\"requests\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":100,\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"handle\":{\"type\":\"string\"}," +
                "\"divide\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000}," +
                "\"spacing\":{\"type\":\"number\"}," +
                "\"start_offset\":{\"type\":\"number\"}," +
                "\"distances\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"maxItems\":1000}," +
                "\"from_end\":{\"type\":\"boolean\"}" +
                "},\"required\":[\"handle\"]}}" +
                "},\"required\":[\"requests\"]}"),

            new ToolDefinition(
                PickPoint,
                "Ask the CAD user to click a point in the drawing and return its world coordinates. Use this when the " +
                "request depends on a location the user has not given (\"put a title block here\", \"start from this " +
                "corner\"). The prompt is shown on the CAD command line, so keep it short and specific. Returns " +
                "cancelled=true if the user pressed Esc.",
                "{\"type\":\"object\",\"properties\":{" +
                "\"prompt\":{\"type\":\"string\",\"description\":\"e.g. 'Pick the bottom-left corner of the slab'\"}" +
                "},\"required\":[\"prompt\"]}"),

            new ToolDefinition(
                SelectEntities,
                "Ask the CAD user to select entities in the drawing and return their summaries (handles and geometry). " +
                "Use this when the user refers to objects you cannot identify from list_entities alone (\"dimension " +
                "these beams\"). Returns cancelled=true if the user pressed Esc.",
                "{\"type\":\"object\",\"properties\":{" +
                "\"prompt\":{\"type\":\"string\",\"description\":\"e.g. 'Select the walls to dimension'\"}" +
                "},\"required\":[\"prompt\"]}"),

            new ToolDefinition(
                Hatch,
                "Hatch areas. Each item gives its boundary one of three ways: boundary_handles (closed polylines or circles; the " +
                "first is the outline, any others are islands), seed_points (a point inside each area: the CAD finds the boundary " +
                "from the linework around it, like picking points in HATCH; the area must be closed and on screen), or points " +
                "(with optional bulges) for an explicit outline. pattern is a CAD pattern name (SOLID, ANSI31, AR-CONC, AR-SAND, " +
                "DOTS, ...); scale and angle (degrees) as in HATCH. Up to 100 hatches per call.",
                "{\"type\":\"object\",\"properties\":{" +
                "\"hatches\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":100,\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"boundary_handles\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":100}," +
                "\"seed_points\":{\"type\":\"array\",\"items\":" + Point + ",\"maxItems\":100}," +
                "\"points\":{\"type\":\"array\",\"items\":" + Point + ",\"minItems\":3}," +
                "\"bulges\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}}," +
                "\"pattern\":{\"type\":\"string\"},\"scale\":{\"type\":\"number\"},\"angle\":{\"type\":\"number\"}," +
                "\"associative\":{\"type\":\"boolean\",\"description\":\"Keep linked to boundary_handles (default false).\"}," +
                "\"layer\":{\"type\":\"string\"},\"color\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":255}" +
                "}}}},\"required\":[\"hatches\"]}"),

            new ToolDefinition(
                TrimExtend,
                "Trim or extend curves exactly to boundary curves. mode trim: cut the curve where it crosses the boundaries and keep " +
                "the piece nearest keep_point. mode extend: lengthen the curve's start, end or both along its last line or arc to the " +
                "nearest boundary ahead. The curve is replaced by the result (same layer and colour; a new handle is returned).",
                "{\"type\":\"object\",\"properties\":{" +
                "\"operations\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":100,\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"handle\":{\"type\":\"string\"},\"mode\":{\"type\":\"string\",\"enum\":[\"trim\",\"extend\"]}," +
                "\"boundaries\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"minItems\":1,\"maxItems\":50}," +
                "\"keep_point\":" + Point + "," +
                "\"end\":{\"type\":\"string\",\"enum\":[\"start\",\"end\",\"both\"]}" +
                "},\"required\":[\"handle\",\"mode\",\"boundaries\"]}}},\"required\":[\"operations\"]}"),

            new ToolDefinition(
                Fillet,
                "Fillet pairs of curves (lines, arcs, polylines) with a tangent arc, e.g. kerb returns: both curves are trimmed or " +
                "extended to the tangent points. pick_a and pick_b are points on the part of each curve to keep (as when picking " +
                "the lines in CAD). join: true joins a, the arc and b into one polyline. The curves are replaced (new handles " +
                "returned); later items in the same call may use the old handles.",
                "{\"type\":\"object\",\"properties\":{" +
                "\"fillets\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":100,\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"a\":{\"type\":\"string\"},\"b\":{\"type\":\"string\"},\"radius\":{\"type\":\"number\"}," +
                "\"pick_a\":" + Point + ",\"pick_b\":" + Point + "," +
                "\"join\":{\"type\":\"boolean\"},\"layer\":{\"type\":\"string\",\"description\":\"Layer for the arc (default: a's layer).\"}" +
                "},\"required\":[\"a\",\"b\",\"radius\",\"pick_a\",\"pick_b\"]}}},\"required\":[\"fillets\"]}"),

            new ToolDefinition(
                JoinCurves,
                "Join curves that meet end to end (lines, arcs, polylines, in any order and direction) into one polyline, closed " +
                "if the ends meet: e.g. to make a closed outline to hatch. The pieces are replaced by the polyline (first piece's " +
                "layer unless 'layer' is given). Gaps up to 'tolerance' (default 0.001) are bridged.",
                "{\"type\":\"object\",\"properties\":{" +
                "\"joins\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":50,\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"handles\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"minItems\":2,\"maxItems\":500}," +
                "\"tolerance\":{\"type\":\"number\"},\"layer\":{\"type\":\"string\"}," +
                "\"keep_originals\":{\"type\":\"boolean\",\"description\":\"Leave the pieces in place (default false).\"}" +
                "},\"required\":[\"handles\"]}}},\"required\":[\"joins\"]}"),

            new ToolDefinition(
                OffsetByChainage,
                "Draw road lines (kerb, edge of seal, footpath...) exactly from an alignment and offsets by chainage. Each line is a " +
                "list of stretches: {from, to, offset} for a constant offset, which is an exact offset of the alignment (arcs stay arcs " +
                "with the right radius), or {from, to, offset_start, offset_end} for a straight taper between two offsets. Offsets " +
                "are positive to the RIGHT of the alignment's direction, negative to the left. Chainage = start_chainage + distance " +
                "along the alignment. Stretches should meet end to end; gaps are closed with straight lines. Use analyse_offsets " +
                "to get the stretches from CSD's code strings.",
                "{\"type\":\"object\",\"properties\":{" +
                "\"alignment\":{\"type\":\"string\",\"description\":\"Handle of the alignment polyline/line/arc.\"}," +
                "\"start_chainage\":{\"type\":\"number\"}," +
                "\"lines\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":50,\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"stretches\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":200,\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"from\":{\"type\":\"number\"},\"to\":{\"type\":\"number\"},\"offset\":{\"type\":\"number\"}," +
                "\"offset_start\":{\"type\":\"number\"},\"offset_end\":{\"type\":\"number\"}},\"required\":[\"from\",\"to\"]}}," +
                "\"layer\":{\"type\":\"string\"},\"color\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":255}" +
                "},\"required\":[\"stretches\"]}}},\"required\":[\"alignment\",\"lines\"]}"),

            new ToolDefinition(
                AnalyseOffsets,
                "Measure road lines (e.g. CSD's exported code polylines) against an alignment and describe each as stretches of " +
                "constant offset and the straight transitions between them, within 'tolerance' (default 0.01). Only the lines' " +
                "vertices are used, so CSD's chords between sample sections don't show up as false widening. Offsets are positive " +
                "to the right. The stretches can go straight into offset_by_chainage (round them to the design values first).",
                "{\"type\":\"object\",\"properties\":{" +
                "\"alignment\":{\"type\":\"string\"},\"start_chainage\":{\"type\":\"number\"}," +
                "\"lines\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"minItems\":1,\"maxItems\":50}," +
                "\"tolerance\":{\"type\":\"number\"}," +
                "\"arc_interval\":{\"type\":\"number\",\"description\":\"Sampling step along true arcs in the lines (default 1).\"}" +
                "},\"required\":[\"alignment\",\"lines\"]}"),
        };
    }
}
