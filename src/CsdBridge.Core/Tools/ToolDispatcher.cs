using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;

namespace CsdBridge.Core.Tools
{
    /// <summary>The text returned to Claude as a tool_result.</summary>
    public sealed class ToolOutcome
    {
        public ToolOutcome(string content, bool isError)
        {
            Content = content;
            IsError = isError;
        }

        public string Content { get; }
        public bool IsError { get; }

        /// <summary>One-line summary for the progress log in the CAD UI.</summary>
        public string? Summary { get; set; }
    }

    /// <summary>
    /// Validates Claude's tool input, calls the <see cref="IDrawingHost"/>, and serialises
    /// the result. Validation happens here, before anything touches the drawing, so a bad
    /// batch is rejected as a whole and Claude gets every problem back in one message.
    /// </summary>
    public sealed partial class ToolDispatcher
    {
        private const int MaxReportedErrors = 20;

        private static readonly char[] InvalidLayerChars = { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`' };

        private readonly IDrawingHost _host;

        public ToolDispatcher(IDrawingHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

        public ToolOutcome Execute(string toolName, JsonElement input)
        {
            try
            {
                switch (toolName)
                {
                    case ToolCatalog.GetDrawingInfo:
                    {
                        DrawingInfo info = _host.GetDrawingInfo();
                        return Ok(DrawingInfoView.Compact(info, input), $"read drawing setup ({info.Units}, {info.Layers.Count} layers)");
                    }

                    case ToolCatalog.ListEntities:
                    {
                        EntityQuery query = ParseQuery(input);
                        EntityListResult list = _host.ListEntities(query);
                        return Ok(list, $"listed {list.Entities.Count} of {list.TotalMatched} entities");
                    }

                    case ToolCatalog.EnsureLayers:
                    {
                        List<LayerSpec> layers = ParseLayers(input);
                        var results = _host.EnsureLayers(layers);
                        return Items(results, $"layers: {string.Join(", ", layers.Select(l => l.Name))}");
                    }

                    case ToolCatalog.DrawEntities:
                    {
                        List<EntitySpec> entities = ParseEntities(input);
                        var results = _host.CreateEntities(entities);
                        return Items(results, "drew " + DescribeKinds(entities));
                    }

                    case ToolCatalog.ModifyEntities:
                    {
                        List<ModifyOperation> ops = ParseOperations(input);
                        var results = _host.ModifyEntities(ops);
                        return Items(results, "modified: " + string.Join(", ", ops.Select(o => $"{Snake(o.Action)} x{o.Handles.Count}")));
                    }

                    case ToolCatalog.Calculate:
                        return Calculate(input);

                    case ToolCatalog.OffsetEntities:
                    {
                        List<OffsetOperation> ops = ParseOffsets(input);
                        var results = _host.OffsetEntities(ops);
                        int made = results.Sum(r => r.NewHandles?.Count ?? 0);
                        return Items(results, $"offset {ops.Sum(o => o.Handles.Count)} curve(s), {made} new");
                    }

                    case ToolCatalog.Intersect:
                    {
                        IntersectQuery query = ParseIntersect(input);
                        IntersectionResult r = _host.Intersect(query);
                        return Ok(r, $"{r.Intersections.Sum(i => i.Points.Count)} intersection point(s) in {r.PairsChecked} pair(s)");
                    }

                    case ToolCatalog.Measure:
                    {
                        (MeasureQuery query, Point2? from, Point2? to) = ParseMeasure(input);
                        MeasureResult r = query.Handles.Count > 0 ? _host.Measure(query) : new MeasureResult();
                        if (from is Point2 a && to is Point2 b)
                        {
                            r.Between = Between(a, b);
                        }

                        return Ok(r, $"measured {r.Entities.Count} entit{(r.Entities.Count == 1 ? "y" : "ies")}{(r.Between != null ? " and a distance" : "")}");
                    }

                    case ToolCatalog.PointsOnCurve:
                    {
                        List<PointsRequest> requests = ParsePointsRequests(input);
                        var results = _host.PointsOnCurves(requests);
                        int failed = results.Count(r => !r.Ok);
                        string json = JsonSerializer.Serialize(new { results }, JsonOptions);
                        return new ToolOutcome(json, failed == results.Count)
                        {
                            Summary = $"{results.Sum(r => r.Points?.Count ?? 0)} set-out point(s){(failed > 0 ? $" ({failed} failed)" : "")}",
                        };
                    }

                    case ToolCatalog.PickPoint:
                    {
                        JsonArgs.RequireObject(input, "");
                        string prompt = JsonArgs.RequireString(input, "prompt", "");
                        PickPointResult r = _host.PickPoint(prompt);
                        return Ok(r, r.Cancelled ? "point pick cancelled" : "user picked a point");
                    }

                    case ToolCatalog.SelectEntities:
                    {
                        JsonArgs.RequireObject(input, "");
                        string prompt = JsonArgs.RequireString(input, "prompt", "");
                        SelectionResult r = _host.SelectEntities(prompt);
                        return Ok(r, r.Cancelled ? "selection cancelled" : $"user selected {r.Entities.Count} entities");
                    }

                    default:
                        return ExecuteEditing(toolName, input) ?? Error($"Unknown tool '{toolName}'.", "unknown tool");
                }
            }
            catch (ToolInputException ex)
            {
                return Error("Invalid input - nothing was changed. " + ex.Message, "rejected invalid input");
            }
            catch (Exception ex)
            {
                // CAD API exceptions land here. Report them so Claude can adjust.
                return Error($"{toolName} failed: {ex.GetType().Name}: {ex.Message}", "failed: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- parsing

        internal static EntityQuery ParseQuery(JsonElement input)
        {
            if (input.ValueKind != JsonValueKind.Object)
            {
                return new EntityQuery();
            }

            var q = new EntityQuery
            {
                Layer = JsonArgs.OptionalString(input, "layer", ""),
                WindowMin = JsonArgs.OptionalPoint(input, "window_min", ""),
                WindowMax = JsonArgs.OptionalPoint(input, "window_max", ""),
                Limit = JsonArgs.OptionalInt(input, "limit", "", 1, ToolCatalog.MaxListLimit) ?? 100,
            };

            if ((q.WindowMin == null) != (q.WindowMax == null))
            {
                throw new ToolInputException("window_min and window_max must be given together");
            }

            var handles = JsonArgs.OptionalArray(input, "handles", "", 0, 10000);
            if (handles != null)
            {
                q.Handles = JsonArgs.ReadStringList(handles, "handles");
            }

            var kinds = JsonArgs.OptionalArray(input, "kinds", "", 0, 20);
            if (kinds != null)
            {
                q.Kinds = new List<EntityKind>();
                foreach (string k in JsonArgs.ReadStringList(kinds, "kinds"))
                {
                    switch (k.ToLowerInvariant())
                    {
                        case "line": q.Kinds.Add(EntityKind.Line); break;
                        case "polyline": q.Kinds.Add(EntityKind.Polyline); break;
                        case "circle": q.Kinds.Add(EntityKind.Circle); break;
                        case "arc": q.Kinds.Add(EntityKind.Arc); break;
                        case "text": q.Kinds.Add(EntityKind.Text); break;
                        case "mtext": q.Kinds.Add(EntityKind.MText); break;
                        case "dimension":
                            q.Kinds.Add(EntityKind.LinearDimension);
                            q.Kinds.Add(EntityKind.AlignedDimension);
                            break;
                        case "block_insert": q.Kinds.Add(EntityKind.BlockInsert); break;
                        case "hatch": q.Kinds.Add(EntityKind.Hatch); break;
                        case "other": q.IncludeOtherKinds = true; break;
                        default: throw new ToolInputException($"kinds: unknown kind '{k}'");
                    }
                }
            }

            return q;
        }

        internal static List<LayerSpec> ParseLayers(JsonElement input)
        {
            JsonArgs.RequireObject(input, "");
            var items = JsonArgs.RequireArray(input, "layers", "", 1, 100);
            var errors = new List<string>();
            var result = new List<LayerSpec>();
            for (int i = 0; i < items.Count; i++)
            {
                string path = JsonArgs.Index("layers", i);
                try
                {
                    JsonArgs.RequireObject(items[i], path);
                    var spec = new LayerSpec
                    {
                        Name = ValidateLayerName(JsonArgs.RequireString(items[i], "name", path), JsonArgs.Path(path, "name"))!,
                        ColorIndex = JsonArgs.OptionalColor(items[i], "color", path),
                        Linetype = JsonArgs.OptionalString(items[i], "linetype", path),
                        LineweightMm = JsonArgs.OptionalNumber(items[i], "lineweight_mm", path),
                        Description = JsonArgs.OptionalString(items[i], "description", path),
                        Plot = JsonArgs.TryGet(items[i], "plot", out _) ? JsonArgs.OptionalBool(items[i], "plot", path, true) : (bool?)null,
                    };
                    List<JsonElement>? rgb = JsonArgs.OptionalArray(items[i], "rgb", path, 3, 3);
                    if (rgb != null)
                    {
                        spec.Rgb = rgb.Select((c, k) =>
                        {
                            double v = JsonArgs.ReadNumber(c, JsonArgs.Index(JsonArgs.Path(path, "rgb"), k));
                            if (v < 0 || v > 255)
                            {
                                throw new ToolInputException($"{path}.rgb values must be 0-255");
                            }

                            return (byte)Math.Round(v);
                        }).ToArray();
                    }

                    if (spec.LineweightMm != null && (spec.LineweightMm < 0 || spec.LineweightMm > 2.11))
                    {
                        throw new ToolInputException($"{path}.lineweight_mm must be between 0 and 2.11 mm");
                    }

                    result.Add(spec);
                }
                catch (ToolInputException ex)
                {
                    errors.Add(ex.Message);
                }
            }

            ThrowIfErrors(errors);
            return result;
        }

        internal static List<EntitySpec> ParseEntities(JsonElement input)
        {
            JsonArgs.RequireObject(input, "");
            var items = JsonArgs.RequireArray(input, "entities", "", 1, ToolCatalog.MaxEntitiesPerCall);
            var errors = new List<string>();
            var result = new List<EntitySpec>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                try
                {
                    result.Add(ParseEntity(items[i], JsonArgs.Index("entities", i)));
                }
                catch (ToolInputException ex)
                {
                    errors.Add(ex.Message);
                }
            }

            ThrowIfErrors(errors);
            return result;
        }

        internal static EntitySpec ParseEntity(JsonElement e, string path)
        {
            JsonArgs.RequireObject(e, path);
            string kind = JsonArgs.RequireString(e, "kind", path).Trim().ToLowerInvariant();
            EntitySpec spec;
            switch (kind)
            {
                case "line":
                {
                    var line = new LineSpec
                    {
                        Start = JsonArgs.RequirePoint(e, "start", path),
                        End = JsonArgs.RequirePoint(e, "end", path),
                    };
                    if (line.Start.Equals(line.End))
                    {
                        throw new ToolInputException($"{path}: line start and end are the same point");
                    }

                    spec = line;
                    break;
                }

                case "polyline":
                {
                    var pts = JsonArgs.RequireArray(e, "points", path, 2, 10000);
                    var pl = new PolylineSpec { Closed = JsonArgs.OptionalBool(e, "closed", path, false) };
                    for (int i = 0; i < pts.Count; i++)
                    {
                        pl.Points.Add(JsonArgs.ReadPoint(pts[i], JsonArgs.Index(path + ".points", i)));
                    }

                    var bulges = JsonArgs.OptionalArray(e, "bulges", path, 0, 10000);
                    if (bulges != null && bulges.Count > 0)
                    {
                        if (bulges.Count != pl.Points.Count)
                        {
                            throw new ToolInputException(
                                $"{path}.bulges has {bulges.Count} values but there are {pl.Points.Count} points; give one bulge per vertex (0 for straight)");
                        }

                        pl.Bulges = bulges.Select((b, i) => JsonArgs.ReadNumber(b, JsonArgs.Index(path + ".bulges", i))).ToList();
                    }

                    spec = pl;
                    break;
                }

                case "circle":
                    spec = new CircleSpec
                    {
                        Center = JsonArgs.RequirePoint(e, "center", path),
                        Radius = JsonArgs.RequirePositive(e, "radius", path),
                    };
                    break;

                case "arc":
                    spec = new ArcSpec
                    {
                        Center = JsonArgs.RequirePoint(e, "center", path),
                        Radius = JsonArgs.RequirePositive(e, "radius", path),
                        StartAngleDeg = JsonArgs.RequireNumber(e, "start_angle", path),
                        EndAngleDeg = JsonArgs.RequireNumber(e, "end_angle", path),
                    };
                    break;

                case "text":
                {
                    string text = JsonArgs.RequireString(e, "text", path);
                    if (text.IndexOf('\n') >= 0)
                    {
                        throw new ToolInputException($"{path}: 'text' entities are single-line; use kind 'mtext' for multi-line text");
                    }

                    spec = new TextSpec
                    {
                        Position = JsonArgs.RequirePoint(e, "position", path),
                        Text = text,
                        Height = JsonArgs.OptionalPositive(e, "height", path),
                        RotationDeg = JsonArgs.OptionalNumber(e, "rotation", path) ?? 0,
                        Align = ParseAlign(JsonArgs.OptionalString(e, "align", path), path),
                    };
                    break;
                }

                case "mtext":
                    spec = new MTextSpec
                    {
                        Position = JsonArgs.RequirePoint(e, "position", path),
                        Text = JsonArgs.RequireString(e, "text", path),
                        Height = JsonArgs.OptionalPositive(e, "height", path),
                        Width = JsonArgs.OptionalNumber(e, "width", path),
                        RotationDeg = JsonArgs.OptionalNumber(e, "rotation", path) ?? 0,
                    };
                    break;

                case "linear_dimension":
                {
                    var dim = new LinearDimensionSpec
                    {
                        P1 = JsonArgs.RequirePoint(e, "p1", path),
                        P2 = JsonArgs.RequirePoint(e, "p2", path),
                        DimLinePoint = JsonArgs.RequirePoint(e, "dim_line_point", path),
                        RotationDeg = JsonArgs.OptionalNumber(e, "rotation", path) ?? 0,
                        TextOverride = JsonArgs.OptionalString(e, "text_override", path),
                    };
                    if (dim.P1.Equals(dim.P2))
                    {
                        throw new ToolInputException($"{path}: p1 and p2 are the same point");
                    }

                    spec = dim;
                    break;
                }

                case "aligned_dimension":
                {
                    var dim = new AlignedDimensionSpec
                    {
                        P1 = JsonArgs.RequirePoint(e, "p1", path),
                        P2 = JsonArgs.RequirePoint(e, "p2", path),
                        DimLinePoint = JsonArgs.RequirePoint(e, "dim_line_point", path),
                        TextOverride = JsonArgs.OptionalString(e, "text_override", path),
                    };
                    if (dim.P1.Equals(dim.P2))
                    {
                        throw new ToolInputException($"{path}: p1 and p2 are the same point");
                    }

                    spec = dim;
                    break;
                }

                case "block_insert":
                case "block":
                case "insert":
                    spec = new BlockInsertSpec
                    {
                        BlockName = JsonArgs.RequireString(e, "block_name", path).Trim(),
                        Position = JsonArgs.RequirePoint(e, "position", path),
                        Scale = JsonArgs.OptionalPositive(e, "scale", path) ?? 1.0,
                        RotationDeg = JsonArgs.OptionalNumber(e, "rotation", path) ?? 0,
                        Attributes = ParseAttributes(e, path),
                    };
                    break;

                default:
                    throw new ToolInputException(
                        $"{path}.kind '{kind}' is not supported (use line, polyline, circle, arc, text, mtext, linear_dimension, aligned_dimension or block_insert)");
            }

            spec.Layer = ValidateLayerName(JsonArgs.OptionalString(e, "layer", path), JsonArgs.Path(path, "layer"));
            spec.ColorIndex = JsonArgs.OptionalColor(e, "color", path);
            spec.LinetypeScale = JsonArgs.OptionalPositive(e, "linetype_scale", path);
            return spec;
        }

        internal static List<ModifyOperation> ParseOperations(JsonElement input)
        {
            JsonArgs.RequireObject(input, "");
            var items = JsonArgs.RequireArray(input, "operations", "", 1, ToolCatalog.MaxOperationsPerCall);
            var errors = new List<string>();
            var result = new List<ModifyOperation>();
            for (int i = 0; i < items.Count; i++)
            {
                string path = JsonArgs.Index("operations", i);
                try
                {
                    JsonElement e = items[i];
                    JsonArgs.RequireObject(e, path);
                    var op = new ModifyOperation
                    {
                        Handles = JsonArgs.ReadStringList(JsonArgs.RequireArray(e, "handles", path, 1, 10000), path + ".handles"),
                    };
                    string action = JsonArgs.RequireString(e, "action", path).Trim().ToLowerInvariant();
                    switch (action)
                    {
                        case "move":
                            op.Action = ModifyAction.Move;
                            op.Dx = JsonArgs.OptionalNumber(e, "dx", path) ?? 0;
                            op.Dy = JsonArgs.OptionalNumber(e, "dy", path) ?? 0;
                            break;
                        case "copy":
                            op.Action = ModifyAction.Copy;
                            op.Dx = JsonArgs.OptionalNumber(e, "dx", path) ?? 0;
                            op.Dy = JsonArgs.OptionalNumber(e, "dy", path) ?? 0;
                            op.Count = JsonArgs.OptionalInt(e, "count", path, 1, 500) ?? 1;
                            if (op.Dx == 0 && op.Dy == 0)
                            {
                                throw new ToolInputException($"{path}: copy needs a non-zero dx or dy");
                            }

                            break;
                        case "rotate":
                            op.Action = ModifyAction.Rotate;
                            op.BasePoint = JsonArgs.RequirePoint(e, "base_point", path);
                            op.AngleDeg = JsonArgs.RequireNumber(e, "angle", path);
                            break;
                        case "scale":
                            op.Action = ModifyAction.Scale;
                            op.BasePoint = JsonArgs.RequirePoint(e, "base_point", path);
                            op.Factor = JsonArgs.RequirePositive(e, "factor", path);
                            break;
                        case "erase":
                        case "delete":
                            op.Action = ModifyAction.Erase;
                            break;
                        case "set_layer":
                            op.Action = ModifyAction.SetLayer;
                            op.Layer = ValidateLayerName(JsonArgs.RequireString(e, "layer", path), path + ".layer");
                            break;
                        case "set_color":
                            op.Action = ModifyAction.SetColor;
                            op.ColorIndex = JsonArgs.OptionalColor(e, "color", path);
                            break;
                        default:
                            throw new ToolInputException($"{path}.action '{action}' is not supported");
                    }

                    result.Add(op);
                }
                catch (ToolInputException ex)
                {
                    errors.Add(ex.Message);
                }
            }

            ThrowIfErrors(errors);
            return result;
        }

        private static Dictionary<string, string>? ParseAttributes(JsonElement e, string path)
        {
            if (!JsonArgs.TryGet(e, "attributes", out JsonElement attrs))
            {
                return null;
            }

            if (attrs.ValueKind != JsonValueKind.Object)
            {
                throw new ToolInputException($"{path}.attributes must be an object of TAG: value pairs");
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty p in attrs.EnumerateObject())
            {
                result[p.Name.Trim()] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
            }

            return result.Count == 0 ? null : result;
        }

        // ---------------------------------------------------------------- geometry tools

        private static ToolOutcome Calculate(JsonElement input)
        {
            JsonArgs.RequireObject(input, "");
            var items = JsonArgs.RequireArray(input, "expressions", "", 1, 200);
            var calc = new Calculator();
            var results = new List<object>();
            int failed = 0;
            for (int i = 0; i < items.Count; i++)
            {
                string path = JsonArgs.Index("expressions", i);
                string? name = null;
                string expr = "";
                try
                {
                    JsonArgs.RequireObject(items[i], path);
                    name = JsonArgs.OptionalString(items[i], "name", path)?.Trim();
                    expr = JsonArgs.RequireString(items[i], "expr", path);
                    double value = calc.Evaluate(expr);
                    if (name != null)
                    {
                        calc.Set(name, value);
                    }

                    results.Add(new { name, expr, value = Math.Round(value, 10) });
                }
                catch (Exception ex) when (ex is FormatException || ex is ToolInputException)
                {
                    failed++;
                    results.Add(new { name, expr, error = ex.Message });
                }
            }

            string json = JsonSerializer.Serialize(new { results }, JsonOptions);
            return new ToolOutcome(json, failed == items.Count)
            {
                Summary = failed == 0 ? $"calculated {items.Count} value(s)" : $"calculated {items.Count - failed} value(s), {failed} failed",
            };
        }

        internal static List<OffsetOperation> ParseOffsets(JsonElement input)
        {
            JsonArgs.RequireObject(input, "");
            var items = JsonArgs.RequireArray(input, "offsets", "", 1, 100);
            var errors = new List<string>();
            var result = new List<OffsetOperation>();
            for (int i = 0; i < items.Count; i++)
            {
                string path = JsonArgs.Index("offsets", i);
                try
                {
                    JsonElement e = items[i];
                    JsonArgs.RequireObject(e, path);
                    var op = new OffsetOperation
                    {
                        Handles = JsonArgs.ReadStringList(JsonArgs.RequireArray(e, "handles", path, 1, 1000), path + ".handles"),
                        Distance = JsonArgs.RequirePositive(e, "distance", path),
                        Count = JsonArgs.OptionalInt(e, "count", path, 1, 100) ?? 1,
                        Layer = ValidateLayerName(JsonArgs.OptionalString(e, "layer", path), path + ".layer"),
                    };

                    Point2? sidePoint = JsonArgs.OptionalPoint(e, "side_point", path);
                    string? side = JsonArgs.OptionalString(e, "side", path)?.Trim().ToLowerInvariant();
                    if ((sidePoint == null) == (side == null))
                    {
                        throw new ToolInputException($"{path}: give either side_point or side ('inside' / 'outside'), not both");
                    }

                    if (sidePoint is Point2 sp)
                    {
                        op.Side = OffsetSide.TowardsPoint;
                        op.SidePoint = sp;
                    }
                    else if (side == "inside" || side == "outside")
                    {
                        op.Side = side == "inside" ? OffsetSide.Inside : OffsetSide.Outside;
                    }
                    else
                    {
                        throw new ToolInputException($"{path}.side must be 'inside' or 'outside'");
                    }

                    result.Add(op);
                }
                catch (ToolInputException ex)
                {
                    errors.Add(ex.Message);
                }
            }

            ThrowIfErrors(errors);
            return result;
        }

        internal static IntersectQuery ParseIntersect(JsonElement input)
        {
            JsonArgs.RequireObject(input, "");
            var query = new IntersectQuery
            {
                Handles = JsonArgs.ReadStringList(JsonArgs.RequireArray(input, "handles", "", 1, 200), "handles"),
                Extend = JsonArgs.OptionalBool(input, "extend", "", false),
            };
            var with = JsonArgs.OptionalArray(input, "with", "", 0, 200);
            if (with != null && with.Count > 0)
            {
                query.With = JsonArgs.ReadStringList(with, "with");
            }
            else if (query.Handles.Count < 2)
            {
                throw new ToolInputException("give at least two handles, or handles plus with");
            }

            return query;
        }

        internal static (MeasureQuery Query, Point2? From, Point2? To) ParseMeasure(JsonElement input)
        {
            JsonArgs.RequireObject(input, "");
            var handles = JsonArgs.OptionalArray(input, "handles", "", 0, 500);
            var query = new MeasureQuery
            {
                Handles = handles == null ? new List<string>() : JsonArgs.ReadStringList(handles, "handles"),
                Point = JsonArgs.OptionalPoint(input, "point", ""),
            };
            Point2? from = JsonArgs.OptionalPoint(input, "from", "");
            Point2? to = JsonArgs.OptionalPoint(input, "to", "");
            if ((from == null) != (to == null))
            {
                throw new ToolInputException("from and to must be given together");
            }

            if (query.Handles.Count == 0 && from == null)
            {
                throw new ToolInputException("give handles to measure, or from and to");
            }

            return (query, from, to);
        }

        internal static List<PointsRequest> ParsePointsRequests(JsonElement input)
        {
            JsonArgs.RequireObject(input, "");
            var items = JsonArgs.RequireArray(input, "requests", "", 1, 100);
            var errors = new List<string>();
            var result = new List<PointsRequest>();
            for (int i = 0; i < items.Count; i++)
            {
                string path = JsonArgs.Index("requests", i);
                try
                {
                    JsonElement e = items[i];
                    JsonArgs.RequireObject(e, path);
                    var r = new PointsRequest
                    {
                        Handle = JsonArgs.RequireString(e, "handle", path).Trim(),
                        Divide = JsonArgs.OptionalInt(e, "divide", path, 1, SetOut.MaxPoints),
                        Spacing = JsonArgs.OptionalPositive(e, "spacing", path),
                        StartOffset = JsonArgs.OptionalNumber(e, "start_offset", path) ?? 0,
                        FromEnd = JsonArgs.OptionalBool(e, "from_end", path, false),
                    };
                    var distances = JsonArgs.OptionalArray(e, "distances", path, 1, SetOut.MaxPoints);
                    if (distances != null)
                    {
                        r.Distances = distances.Select((d, k) => JsonArgs.ReadNumber(d, JsonArgs.Index(path + ".distances", k))).ToList();
                    }

                    int modes = (r.Divide != null ? 1 : 0) + (r.Spacing != null ? 1 : 0) + (r.Distances != null ? 1 : 0);
                    if (modes != 1)
                    {
                        throw new ToolInputException($"{path}: give exactly one of divide, spacing or distances");
                    }

                    result.Add(r);
                }
                catch (ToolInputException ex)
                {
                    errors.Add(ex.Message);
                }
            }

            ThrowIfErrors(errors);
            return result;
        }

        internal static PointToPoint Between(Point2 a, Point2 b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
            return new PointToPoint
            {
                Distance = Math.Round(Math.Sqrt(dx * dx + dy * dy), 6),
                Dx = Math.Round(dx, 6),
                Dy = Math.Round(dy, 6),
                Angle = Math.Round(angle < 0 ? angle + 360 : angle, 6),
            };
        }

        private static TextAlign ParseAlign(string? align, string path)
        {
            switch ((align ?? "left").Trim().ToLowerInvariant())
            {
                case "left": return TextAlign.Left;
                case "center":
                case "centre": return TextAlign.Center;
                case "right": return TextAlign.Right;
                case "middle_center":
                case "middle":
                case "middle_centre": return TextAlign.MiddleCenter;
                default: throw new ToolInputException($"{path}.align must be left, center, right or middle_center");
            }
        }

        private static string? ValidateLayerName(string? name, string path)
        {
            if (name == null)
            {
                return null;
            }

            name = name.Trim();
            if (name.Length == 0 || name.Length > 255 || name.IndexOfAny(InvalidLayerChars) >= 0)
            {
                throw new ToolInputException($"{path} '{name}' is not a valid layer name (no < > / \\ \" : ; ? * | , = ` characters)");
            }

            return name;
        }

        private static void ThrowIfErrors(List<string> errors)
        {
            if (errors.Count == 0)
            {
                return;
            }

            string msg = string.Join("\n", errors.Take(MaxReportedErrors));
            if (errors.Count > MaxReportedErrors)
            {
                msg += $"\n... and {errors.Count - MaxReportedErrors} more";
            }

            throw new ToolInputException(errors.Count == 1 ? msg : $"{errors.Count} problems:\n{msg}");
        }

        // ---------------------------------------------------------------- results

        private static ToolOutcome Ok(object result, string summary) =>
            new ToolOutcome(JsonSerializer.Serialize(result, JsonOptions), false) { Summary = summary };

        private static ToolOutcome Error(string message, string summary) =>
            new ToolOutcome(message, true) { Summary = summary };

        private static ToolOutcome Items(IReadOnlyList<ItemResult> results, string summary)
        {
            int failed = results.Count(r => !r.Ok);
            var payload = new { succeeded = results.Count - failed, failed, results };
            string json = JsonSerializer.Serialize(payload, JsonOptions);

            // Partial failure is not a tool error: some items did change the drawing.
            // Only flag is_error when nothing at all succeeded.
            bool allFailed = results.Count > 0 && failed == results.Count;
            return new ToolOutcome(json, allFailed)
            {
                Summary = failed == 0 ? summary : $"{summary} ({failed} failed)",
            };
        }

        private static string DescribeKinds(List<EntitySpec> entities) =>
            string.Join(", ", entities.GroupBy(e => e.Kind).Select(g => $"{g.Count()} {Snake(g.Key)}"));

        internal static string Snake(Enum value)
        {
            string s = value.ToString();
            var sb = new System.Text.StringBuilder(s.Length + 4);
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1]))
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(s[i]));
            }

            return sb.ToString();
        }

        private static JsonSerializerOptions CreateJsonOptions()
        {
            var o = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = false,
            };
            o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
            return o;
        }
    }
}
