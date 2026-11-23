using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;
using CsdBridge.Core.Rendering;

namespace CsdBridge.Core.Tools
{
    /// <summary>
    /// Annotation: the extra dimension types, multileaders and tables that draw_entities
    /// takes, editing existing annotation, dimension chains, alignment labels, set-out
    /// tables, and the picture used to check a layout. Positions are worked out here on
    /// exact lines and arcs, so the BricsCAD and offline hosts place things identically.
    /// </summary>
    public sealed partial class ToolDispatcher
    {
        private const int MaxTableColumns = 50;
        private const int MaxLabels = 2000;
        private const int MaxLabelsListed = 100;
        private const int MaxViewEntities = 5000;

        private ToolOutcome? ExecuteAnnotation(string toolName, JsonElement input)
        {
            switch (toolName)
            {
                case ToolCatalog.EditAnnotations:
                {
                    List<AnnotationEdit> edits = ParseEdits(input);
                    return Items(_host.EditAnnotations(edits), $"edited {edits.Count} annotation object(s)");
                }

                case ToolCatalog.DimensionChain: return DoDimensionChain(input);
                case ToolCatalog.LabelAlignment: return DoLabelAlignment(input);
                case ToolCatalog.SetoutPoints: return DoSetoutPoints(input);
                case ToolCatalog.ViewDrawing: return DoViewDrawing(input);
                default: return null;
            }
        }

        // ------------------------------------------------------------------ draw_entities kinds

        /// <summary>The annotation kinds of draw_entities; null when <paramref name="kind"/> isn't one.</summary>
        private static EntitySpec? ParseAnnotationEntity(JsonElement e, string kind, string path)
        {
            switch (kind)
            {
                case "radius_dimension":
                case "diameter_dimension":
                {
                    Point2 center = JsonArgs.RequirePoint(e, "center", path);
                    Point2? point = JsonArgs.OptionalPoint(e, "point", path);
                    if (point == null)
                    {
                        double? radius = JsonArgs.OptionalPositive(e, "radius", path);
                        double? angle = JsonArgs.OptionalNumber(e, "angle", path);
                        if (radius == null || angle == null)
                        {
                            throw new ToolInputException($"{path}: give point (on the curve), or radius and angle");
                        }

                        double a = angle.Value * Math.PI / 180;
                        point = new Point2(center.X + radius.Value * Math.Cos(a), center.Y + radius.Value * Math.Sin(a));
                    }

                    if (point.Value.Equals(center))
                    {
                        throw new ToolInputException($"{path}: point is at the centre; it must be on the curve");
                    }

                    return new RadialDimensionSpec
                    {
                        Diameter = kind == "diameter_dimension",
                        Center = center,
                        ChordPoint = point.Value,
                        LeaderLength = JsonArgs.OptionalPositive(e, "leader_length", path),
                        TextOverride = JsonArgs.OptionalString(e, "text_override", path),
                    };
                }

                case "angular_dimension":
                case "arc_length_dimension":
                {
                    var dim = new AngularDimensionSpec
                    {
                        ArcLength = kind == "arc_length_dimension",
                        Center = JsonArgs.RequirePoint(e, "center", path),
                        P1 = JsonArgs.RequirePoint(e, "p1", path),
                        P2 = JsonArgs.RequirePoint(e, "p2", path),
                        ArcPoint = JsonArgs.RequirePoint(e, "arc_point", path),
                        TextOverride = JsonArgs.OptionalString(e, "text_override", path),
                    };
                    if (dim.P1.Equals(dim.Center) || dim.P2.Equals(dim.Center) || dim.ArcPoint.Equals(dim.Center))
                    {
                        throw new ToolInputException($"{path}: p1, p2 and arc_point must all be away from center");
                    }

                    double r1 = Segment.Dist(dim.Center, dim.P1), r2 = Segment.Dist(dim.Center, dim.P2);
                    if (dim.ArcLength && Math.Abs(r1 - r2) > Math.Max(0.01, 0.005 * r1))
                    {
                        throw new ToolInputException(
                            $"{path}: p1 and p2 must be the ends of one arc about center (they are {JsonArgs.Fmt(r1)} and {JsonArgs.Fmt(r2)} from it)");
                    }

                    return dim;
                }

                case "ordinate_dimension":
                {
                    string axis = JsonArgs.RequireString(e, "axis", path).Trim().ToLowerInvariant();
                    if (axis != "x" && axis != "y")
                    {
                        throw new ToolInputException($"{path}.axis must be x or y");
                    }

                    var dim = new OrdinateDimensionSpec
                    {
                        FeaturePoint = JsonArgs.RequirePoint(e, "point", path),
                        LeaderEnd = JsonArgs.RequirePoint(e, "leader_end", path),
                        UseXAxis = axis == "x",
                        Origin = JsonArgs.OptionalPoint(e, "origin", path),
                        TextOverride = JsonArgs.OptionalString(e, "text_override", path),
                    };
                    if (dim.FeaturePoint.Equals(dim.LeaderEnd))
                    {
                        throw new ToolInputException($"{path}: point and leader_end are the same point");
                    }

                    return dim;
                }

                case "mleader":
                case "multileader":
                {
                    var ml = new MLeaderSpec
                    {
                        ArrowPoints = ParseArrowPoints(e, path) ?? throw new ToolInputException($"{path}: arrow_point (or arrow_points) is required"),
                        TextPosition = JsonArgs.RequirePoint(e, "position", path),
                        Text = JsonArgs.RequireString(e, "text", path),
                        Height = JsonArgs.OptionalPositive(e, "height", path),
                        Width = JsonArgs.OptionalPositive(e, "width", path),
                        ArrowSize = JsonArgs.OptionalPositive(e, "arrow_size", path),
                        TextStyle = JsonArgs.OptionalString(e, "text_style", path)?.Trim(),
                    };
                    if (ml.ArrowPoints.Any(p => p.Equals(ml.TextPosition)))
                    {
                        throw new ToolInputException($"{path}: an arrow point is at the text position; the leader needs some length");
                    }

                    return ml;
                }

                case "table":
                {
                    var table = new TableSpec
                    {
                        Position = JsonArgs.RequirePoint(e, "position", path),
                        Title = JsonArgs.OptionalString(e, "title", path),
                        Header = JsonArgs.OptionalBool(e, "header", path, true),
                        RowHeight = JsonArgs.OptionalPositive(e, "row_height", path),
                        TextHeight = JsonArgs.OptionalPositive(e, "height", path),
                        TextStyle = JsonArgs.OptionalString(e, "text_style", path)?.Trim(),
                        Rows = ParseRows(JsonArgs.RequireArray(e, "rows", path, 1, 500), JsonArgs.Path(path, "rows")),
                    };

                    List<JsonElement>? widths = JsonArgs.OptionalArray(e, "column_widths", path, 1, MaxTableColumns);
                    if (widths != null)
                    {
                        if (widths.Count != table.ColumnCount)
                        {
                            throw new ToolInputException($"{path}.column_widths has {widths.Count} values but the table has {table.ColumnCount} columns");
                        }

                        table.ColumnWidths = widths.Select((w, k) =>
                        {
                            double v = JsonArgs.ReadNumber(w, JsonArgs.Index(JsonArgs.Path(path, "column_widths"), k));
                            return v > 0 ? v : throw new ToolInputException($"{path}.column_widths[{k}] must be greater than 0");
                        }).ToList();
                    }

                    return table;
                }

                default:
                    return null;
            }
        }

        private static List<Point2>? ParseArrowPoints(JsonElement e, string path)
        {
            List<JsonElement>? many = JsonArgs.OptionalArray(e, "arrow_points", path, 1, 20);
            if (many != null)
            {
                return many.Select((p, k) => JsonArgs.ReadPoint(p, JsonArgs.Index(JsonArgs.Path(path, "arrow_points"), k))).ToList();
            }

            Point2? one = JsonArgs.OptionalPoint(e, "arrow_point", path);
            return one == null ? null : new List<Point2> { one.Value };
        }

        /// <summary>Table rows as text; short rows are padded so every row has the same number of cells.</summary>
        private static List<List<string>> ParseRows(List<JsonElement> rows, string path)
        {
            var result = new List<List<string>>();
            for (int r = 0; r < rows.Count; r++)
            {
                string rp = JsonArgs.Index(path, r);
                if (rows[r].ValueKind != JsonValueKind.Array)
                {
                    throw new ToolInputException($"{rp} must be an array of cell text");
                }

                result.Add(rows[r].EnumerateArray().Select(CellText).ToList());
            }

            int columns = result.Max(r => r.Count);
            if (columns == 0 || columns > MaxTableColumns)
            {
                throw new ToolInputException($"{path}: a table needs 1 to {MaxTableColumns} columns");
            }

            foreach (List<string> row in result)
            {
                while (row.Count < columns)
                {
                    row.Add("");
                }
            }

            return result;
        }

        private static string CellText(JsonElement cell)
        {
            switch (cell.ValueKind)
            {
                case JsonValueKind.String: return cell.GetString() ?? "";
                case JsonValueKind.Null: return "";
                default: return cell.GetRawText();
            }
        }

        // ------------------------------------------------------------------ edit_annotations

        internal static List<AnnotationEdit> ParseEdits(JsonElement input)
        {
            JsonArgs.RequireObject(input, "");
            List<JsonElement> items = JsonArgs.RequireArray(input, "edits", "", 1, 200);
            var errors = new List<string>();
            var result = new List<AnnotationEdit>();
            for (int i = 0; i < items.Count; i++)
            {
                string path = JsonArgs.Index("edits", i);
                try
                {
                    JsonElement e = items[i];
                    JsonArgs.RequireObject(e, path);
                    var edit = new AnnotationEdit
                    {
                        Handle = JsonArgs.RequireString(e, "handle", path).Trim(),
                        Text = OptionalText(e, "text", path),
                        Attributes = ParseAttributes(e, path),
                        TextOverride = OptionalText(e, "text_override", path),
                        Height = JsonArgs.OptionalPositive(e, "height", path),
                        Width = JsonArgs.OptionalNumber(e, "width", path),
                        RotationDeg = JsonArgs.OptionalNumber(e, "rotation", path),
                        Position = JsonArgs.OptionalPoint(e, "position", path),
                        ArrowPoints = ParseArrowPoints(e, path),
                        Style = JsonArgs.OptionalString(e, "style", path)?.Trim(),
                    };

                    if (edit.Width is double w && w < 0)
                    {
                        throw new ToolInputException($"{path}.width must be 0 (no wrapping) or more");
                    }

                    List<JsonElement>? cells = JsonArgs.OptionalArray(e, "cells", path, 1, 2000);
                    if (cells != null)
                    {
                        edit.Cells = cells.Select((c, k) =>
                        {
                            string cp = JsonArgs.Index(JsonArgs.Path(path, "cells"), k);
                            JsonArgs.RequireObject(c, cp);
                            return new TableCellEdit
                            {
                                Row = JsonArgs.OptionalInt(c, "row", cp, 0, 100000) ?? throw new ToolInputException($"{cp}.row is required"),
                                Column = JsonArgs.OptionalInt(c, "column", cp, 0, 1000) ?? throw new ToolInputException($"{cp}.column is required"),
                                Text = OptionalText(c, "text", cp) ?? throw new ToolInputException($"{cp}.text is required"),
                            };
                        }).ToList();
                    }

                    if (!edit.HasChanges)
                    {
                        throw new ToolInputException($"{path} changes nothing; give text, attributes, text_override, cells, height, width, rotation, position, arrow_points or style");
                    }

                    result.Add(edit);
                }
                catch (ToolInputException ex)
                {
                    errors.Add(ex.Message);
                }
            }

            ThrowIfErrors(errors);
            return result;
        }

        /// <summary>A string that may be empty (unlike <see cref="JsonArgs.OptionalString"/>, where empty means not given).</summary>
        private static string? OptionalText(JsonElement obj, string name, string parent)
        {
            if (!JsonArgs.TryGet(obj, name, out JsonElement v))
            {
                return null;
            }

            return v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : throw new ToolInputException($"{JsonArgs.Path(parent, name)} must be a string");
        }

        // ------------------------------------------------------------------ dimension_chain

        private ToolOutcome DoDimensionChain(JsonElement input)
        {
            JsonArgs.RequireObject(input, "");
            List<Point2> points = JsonArgs.RequireArray(input, "points", "", 2, 200)
                .Select((p, k) => JsonArgs.ReadPoint(p, JsonArgs.Index("points", k))).ToList();
            Point2 through = JsonArgs.RequirePoint(input, "dim_line_point", "");
            string mode = (JsonArgs.OptionalString(input, "mode", "") ?? "chain").Trim().ToLowerInvariant();
            if (mode != "chain" && mode != "baseline")
            {
                throw new ToolInputException("mode must be chain or baseline");
            }

            Point2 first = points[0], last = points[points.Count - 1];
            double rotation = JsonArgs.OptionalNumber(input, "rotation", "")
                ?? (first.Equals(last)
                    ? throw new ToolInputException("the first and last points are the same; give rotation")
                    : Math.Atan2(last.Y - first.Y, last.X - first.X) * 180 / Math.PI);
            double spacing = 0;
            if (mode == "baseline")
            {
                spacing = JsonArgs.OptionalPositive(input, "baseline_spacing", "")
                    ?? throw new ToolInputException("baseline_spacing is required for mode baseline (the distance between dimension lines)");
            }

            string? layer = ValidateLayerName(JsonArgs.OptionalString(input, "layer", ""), "layer");
            string? style = JsonArgs.OptionalString(input, "style", "")?.Trim();
            short? color = JsonArgs.OptionalColor(input, "color", "");

            // Baseline dimension lines step away from the points, on the side dim_line_point is on.
            double r = rotation * Math.PI / 180, nx = -Math.Sin(r), ny = Math.Cos(r);
            double side = (through.X - first.X) * nx + (through.Y - first.Y) * ny < 0 ? -1 : 1;

            var specs = new List<EntitySpec>();
            for (int i = 1; i < points.Count; i++)
            {
                Point2 from = mode == "chain" ? points[i - 1] : first;
                double step = mode == "chain" ? 0 : (i - 1) * spacing * side;
                var dim = new LinearDimensionSpec
                {
                    P1 = from,
                    P2 = points[i],
                    DimLinePoint = new Point2(through.X + nx * step, through.Y + ny * step),
                    RotationDeg = rotation,
                    Layer = layer,
                    Style = style,
                    ColorIndex = color,
                };
                if (AnnotationGeometry.Measure(dim) < 1e-9)
                {
                    throw new ToolInputException($"points[{i}] is no further along the measuring direction than the point before it; nothing to dimension");
                }

                specs.Add(dim);
            }

            IReadOnlyList<ItemResult> created = _host.CreateEntities(specs);
            int failed = created.Count(c => !c.Ok);
            var results = created.Select((c, i) => new
            {
                index = i,
                ok = c.Ok,
                handle = c.Handle,
                measurement = c.Ok ? Math.Round(AnnotationGeometry.Measure(specs[i]), 4) : (double?)null,
                error = c.Error,
                note = c.Note,
            }).ToList();
            string json = JsonSerializer.Serialize(new { mode, rotation = Math.Round(rotation, 4), succeeded = created.Count - failed, failed, results }, JsonOptions);
            return new ToolOutcome(json, failed == created.Count)
            {
                Summary = $"{mode} of {created.Count - failed} dimension(s){(failed > 0 ? $" ({failed} failed)" : "")}",
            };
        }

        // ------------------------------------------------------------------ label_alignment

        private sealed class Label
        {
            public double Chainage;
            public string Text = "";
            public Point2 Point;
            public string Kind = "";
            public int TickIndex = -1, TextIndex = -1;
        }

        private ToolOutcome DoLabelAlignment(JsonElement input)
        {
            JsonArgs.RequireObject(input, "");
            string handle = JsonArgs.RequireString(input, "alignment", "").Trim();
            double start = JsonArgs.OptionalNumber(input, "start_chainage", "") ?? 0;
            double? interval = JsonArgs.OptionalPositive(input, "interval", "");
            bool tangentPoints = JsonArgs.OptionalBool(input, "tangent_points", "", false);
            bool ends = JsonArgs.OptionalBool(input, "ends", "", false);
            bool radii = JsonArgs.OptionalBool(input, "radii", "", false);
            if (interval == null && !tangentPoints && !ends && !radii)
            {
                throw new ToolInputException("give at least one of interval, tangent_points, ends or radii");
            }

            string sideName = (JsonArgs.OptionalString(input, "side", "") ?? "right").Trim().ToLowerInvariant();
            if (sideName != "left" && sideName != "right")
            {
                throw new ToolInputException("side must be left or right");
            }

            string asKind = (JsonArgs.OptionalString(input, "as", "") ?? "text").Trim().ToLowerInvariant();
            if (asKind != "text" && asKind != "mleader")
            {
                throw new ToolInputException("as must be text or mleader");
            }

            string prefix = OptionalText(input, "prefix", "") ?? "CH ";
            int decimals = JsonArgs.OptionalInt(input, "decimals", "", 0, 4)
                ?? (interval is double iv && Math.Abs(iv - Math.Round(iv)) < 1e-9 && Math.Abs(start - Math.Round(start)) < 1e-9 ? 0 : 3);
            string? layer = ValidateLayerName(JsonArgs.OptionalString(input, "layer", ""), "layer");
            string? tickLayer = ValidateLayerName(JsonArgs.OptionalString(input, "tick_layer", ""), "tick_layer") ?? layer;
            string? style = JsonArgs.OptionalString(input, "style", "")?.Trim();

            Path2 path = Curves(new[] { handle })[handle].Path;
            double length = path.Length, end = start + length;
            double height = JsonArgs.OptionalPositive(input, "text_height", "") ?? DefaultTextHeight();
            double tick = JsonArgs.OptionalNumber(input, "tick_length", "") ?? 2 * height;
            double gap = JsonArgs.OptionalNumber(input, "label_gap", "") ?? 0.5 * height;
            if (tick < 0 || gap < 0)
            {
                throw new ToolInputException("tick_length and label_gap can't be negative");
            }

            double from = Math.Max(start, JsonArgs.OptionalNumber(input, "from_chainage", "") ?? start);
            double to = Math.Min(end, JsonArgs.OptionalNumber(input, "to_chainage", "") ?? end);
            if (from > to + 1e-9)
            {
                throw new ToolInputException(
                    $"from_chainage to to_chainage doesn't overlap the alignment, which runs from chainage {JsonArgs.Fmt(start)} to {JsonArgs.Fmt(end)}");
            }

            // Special points first, so a round chainage that falls on one isn't labelled twice.
            var labels = new List<Label>();
            void Add(double chainage, string text, string kind)
            {
                if (chainage >= from - 1e-6 && chainage <= to + 1e-6 && !labels.Any(l => Math.Abs(l.Chainage - chainage) < 1e-6))
                {
                    labels.Add(new Label { Chainage = chainage, Text = text, Kind = kind });
                }
            }

            if (ends)
            {
                Add(start, prefix + Fixed(start, 3), "start");
                Add(end, prefix + Fixed(end, 3), "end");
            }

            if (tangentPoints)
            {
                foreach (double c in TangentPoints(path).Select(d => start + d))
                {
                    Add(c, "TP " + prefix + Fixed(c, 3), "tangent_point");
                }
            }

            if (interval is double step)
            {
                double firstStation = Math.Ceiling((from - 1e-6) / step) * step;
                if ((to - firstStation) / step > MaxLabels)
                {
                    throw new ToolInputException($"interval {JsonArgs.Fmt(step)} gives more than {MaxLabels} labels; use a larger interval or a chainage range");
                }

                for (int k = 0; firstStation + k * step <= to + 1e-6; k++)
                {
                    double c = firstStation + k * step;
                    Add(c, prefix + Fixed(c, decimals), "chainage");
                }
            }

            labels.Sort((a, b) => a.Chainage.CompareTo(b.Chainage));

            double sign = sideName == "right" ? 1 : -1;
            var specs = new List<EntitySpec>();
            foreach (Label label in labels)
            {
                double d = Math.Max(0, Math.Min(length, label.Chainage - start));
                Point2 on = path.PointAt(d);
                double heading = path.TangentAt(d);
                double nx = Math.Sin(heading) * sign, ny = -Math.Cos(heading) * sign; // towards the label side
                label.Point = on;

                if (tick > 0)
                {
                    label.TickIndex = specs.Count;
                    specs.Add(new LineSpec
                    {
                        Start = new Point2(on.X - nx * tick / 2, on.Y - ny * tick / 2),
                        End = new Point2(on.X + nx * tick / 2, on.Y + ny * tick / 2),
                        Layer = tickLayer,
                    });
                }

                label.TextIndex = specs.Count;
                if (asKind == "mleader")
                {
                    double reach = tick / 2 + gap + 3 * height;
                    specs.Add(new MLeaderSpec
                    {
                        ArrowPoints = new List<Point2> { on },
                        TextPosition = new Point2(on.X + nx * reach, on.Y + ny * reach),
                        Text = label.Text,
                        Height = height,
                        Layer = layer,
                        Style = style,
                    });
                }
                else
                {
                    var anchor = new Point2(on.X + nx * (tick / 2 + gap), on.Y + ny * (tick / 2 + gap));
                    specs.Add(TextAlong(label.Text, anchor, Math.Atan2(ny, nx), height, layer, style, centred: false));
                }
            }

            var radiusLabels = new List<(double Chainage, string Text, int Index)>();
            if (radii)
            {
                double along = 0;
                foreach (Segment s in path.Segments)
                {
                    double mid = along + s.Length / 2;
                    along += s.Length;
                    double chainage = start + mid;
                    if (!(s is ArcSegment arc) || chainage < from - 1e-6 || chainage > to + 1e-6)
                    {
                        continue;
                    }

                    // On the other side from the chainage labels, reading along the road.
                    Point2 on = path.PointAt(mid);
                    double heading = path.TangentAt(mid);
                    double nx = -Math.Sin(heading) * sign, ny = Math.Cos(heading) * sign;
                    double away = tick / 2 + gap + height / 2;
                    string text = "R " + Fixed(arc.Radius, 3);
                    radiusLabels.Add((Math.Round(chainage, 4), text, specs.Count));
                    specs.Add(TextAlong(text, new Point2(on.X + nx * away, on.Y + ny * away), heading, height, layer, style, centred: true));
                }
            }

            if (specs.Count == 0)
            {
                return Ok(new { drawn = 0, note = "Nothing to label in that chainage range." }, "no labels to draw");
            }

            IReadOnlyList<ItemResult> created = _host.CreateEntities(specs);
            int failed = created.Count(c => !c.Ok);
            string? HandleAt(int index) => index >= 0 && created[index].Ok ? created[index].Handle : null;
            var payload = new
            {
                alignment_length = Math.Round(length, 4),
                chainage_from = Math.Round(start, 4),
                chainage_to = Math.Round(end, 4),
                text_height = height,
                drawn = created.Count - failed,
                failed = failed > 0 ? failed : (int?)null,
                first_error = created.FirstOrDefault(c => !c.Ok)?.Error,
                labels_not_listed = labels.Count > MaxLabelsListed ? labels.Count - MaxLabelsListed : (int?)null,
                labels = labels.Take(MaxLabelsListed).Select(l => new
                {
                    chainage = Math.Round(l.Chainage, 4),
                    text = l.Text,
                    kind = l.Kind == "chainage" ? null : l.Kind,
                    point = l.Point.ToArray(),
                    handle = HandleAt(l.TextIndex),
                    tick = HandleAt(l.TickIndex),
                }).ToList(),
                radius_labels = radiusLabels.Count == 0
                    ? null
                    : radiusLabels.Select(o => new { chainage = o.Chainage, text = o.Text, handle = HandleAt(o.Index) }).ToList(),
            };
            string json = JsonSerializer.Serialize(payload, JsonOptions);
            return new ToolOutcome(json, failed == created.Count)
            {
                Summary = $"labelled {labels.Count} chainage(s){(radiusLabels.Count > 0 ? $" and {radiusLabels.Count} radius/radii" : "")}{(failed > 0 ? $" ({failed} failed)" : "")}",
            };
        }

        /// <summary>
        /// Single-line text reading along a direction, flipped if it would be upside down, and
        /// set so the direction's line runs through the middle of the text's height. Not
        /// centred: it starts at (or, flipped, ends at) the anchor. Centred: its middle is there.
        /// </summary>
        private static TextSpec TextAlong(string text, Point2 anchor, double direction, double height, string? layer, string? style, bool centred)
        {
            double degrees = direction * 180 / Math.PI;
            double readable = AnnotationGeometry.Readable(degrees);
            bool flipped = Math.Abs(Math.IEEERemainder(readable - degrees, 360)) > 1;
            double r = readable * Math.PI / 180;

            // Drop the baseline by half the text height so the text is centred on the line.
            var position = new Point2(anchor.X + Math.Sin(r) * height / 2, anchor.Y - Math.Cos(r) * height / 2);
            return new TextSpec
            {
                Text = text,
                Position = position,
                Height = height,
                RotationDeg = Math.Round(readable, 6),
                Align = centred ? TextAlign.Center : flipped ? TextAlign.Right : TextAlign.Left,
                Layer = layer,
                Style = style,
            };
        }

        /// <summary>Distances along a path where its geometry changes: line to arc, arc to line, or to a different arc.</summary>
        internal static List<double> TangentPoints(Path2 path)
        {
            var result = new List<double>();
            double along = 0;
            for (int i = 0; i < path.Segments.Count - 1; i++)
            {
                Segment a = path.Segments[i], b = path.Segments[i + 1];
                along += a.Length;
                bool sameArc = a is ArcSegment x && b is ArcSegment y
                    && Segment.Dist(x.Center, y.Center) < 1e-6 && Math.Abs(x.Radius - y.Radius) < 1e-6 && Math.Sign(x.Sweep) == Math.Sign(y.Sweep);
                bool sameLine = a is LineSegment && b is LineSegment
                    && Math.Abs(Math.IEEERemainder(a.TangentAt(a.Length) - b.TangentAt(0), 2 * Math.PI)) < 1e-7;
                if (!sameArc && !sameLine && (a is ArcSegment || b is ArcSegment))
                {
                    result.Add(along);
                }
            }

            return result;
        }

        // ------------------------------------------------------------------ setout_points

        private sealed class SetoutPoint
        {
            public string? Name;
            public Point2 Point;
            public double? Level, Chainage, Offset;
        }

        private ToolOutcome DoSetoutPoints(JsonElement input)
        {
            JsonArgs.RequireObject(input, "");
            List<JsonElement>? explicitPoints = JsonArgs.OptionalArray(input, "points", "", 1, 500);
            string? alignmentHandle = JsonArgs.OptionalString(input, "alignment", "")?.Trim();
            if ((explicitPoints == null) == (alignmentHandle == null))
            {
                throw new ToolInputException("give either points, or an alignment with chainages or interval");
            }

            var points = new List<SetoutPoint>();
            if (explicitPoints != null)
            {
                for (int i = 0; i < explicitPoints.Count; i++)
                {
                    string pp = JsonArgs.Index("points", i);
                    JsonArgs.RequireObject(explicitPoints[i], pp);
                    points.Add(new SetoutPoint
                    {
                        Point = JsonArgs.RequirePoint(explicitPoints[i], "point", pp),
                        Name = JsonArgs.OptionalString(explicitPoints[i], "name", pp)?.Trim(),
                        Level = JsonArgs.OptionalNumber(explicitPoints[i], "level", pp),
                    });
                }
            }
            else
            {
                points = PointsFromAlignment(input, alignmentHandle!);
            }

            string prefix = OptionalText(input, "prefix", "") ?? "SP";
            int number = JsonArgs.OptionalInt(input, "start_number", "", 0, 1000000) ?? 1;
            foreach (SetoutPoint p in points)
            {
                p.Name ??= prefix + (number++).ToString(CultureInfo.InvariantCulture);
            }

            string marker = (JsonArgs.OptionalString(input, "marker", "") ?? "cross").Trim().ToLowerInvariant();
            if (marker != "cross" && marker != "circle" && marker != "none")
            {
                throw new ToolInputException("marker must be cross, circle or none");
            }

            int decimals = JsonArgs.OptionalInt(input, "decimals", "", 0, 4) ?? 3;
            double height = JsonArgs.OptionalPositive(input, "text_height", "") ?? DefaultTextHeight();
            double size = JsonArgs.OptionalPositive(input, "marker_size", "") ?? height;
            string? layer = ValidateLayerName(JsonArgs.OptionalString(input, "layer", ""), "layer");
            string? style = JsonArgs.OptionalString(input, "style", "")?.Trim();
            string? tableStyle = JsonArgs.OptionalString(input, "table_style", "")?.Trim();
            Point2? tablePosition = JsonArgs.OptionalPoint(input, "table_position", "");
            string title = JsonArgs.OptionalString(input, "title", "") ?? "SETOUT TABLE";

            var specs = new List<EntitySpec>();
            var labelIndex = new List<int>();
            foreach (SetoutPoint p in points)
            {
                double half = size / 2;
                if (marker == "cross")
                {
                    specs.Add(new LineSpec { Start = new Point2(p.Point.X - half, p.Point.Y), End = new Point2(p.Point.X + half, p.Point.Y), Layer = layer });
                    specs.Add(new LineSpec { Start = new Point2(p.Point.X, p.Point.Y - half), End = new Point2(p.Point.X, p.Point.Y + half), Layer = layer });
                }
                else if (marker == "circle")
                {
                    specs.Add(new CircleSpec { Center = p.Point, Radius = half, Layer = layer });
                }

                labelIndex.Add(specs.Count);
                specs.Add(new TextSpec
                {
                    Text = p.Name!,
                    Position = new Point2(p.Point.X + half + 0.3 * height, p.Point.Y + half + 0.3 * height),
                    Height = height,
                    Layer = layer,
                    Style = style,
                });
            }

            bool withChainage = points.Any(p => p.Chainage != null);
            bool withLevel = points.Any(p => p.Level != null);
            int tableIndex = -1;
            if (tablePosition is Point2 at)
            {
                var header = new List<string> { "POINT", "EASTING", "NORTHING" };
                if (withChainage)
                {
                    header.Add("CHAINAGE");
                    header.Add("OFFSET");
                }

                if (withLevel)
                {
                    header.Add("LEVEL");
                }

                var rows = new List<List<string>> { header };
                foreach (SetoutPoint p in points)
                {
                    var row = new List<string> { p.Name!, Fixed(p.Point.X, decimals), Fixed(p.Point.Y, decimals) };
                    if (withChainage)
                    {
                        row.Add(p.Chainage is double c ? Fixed(c, decimals) : "");
                        row.Add(p.Offset is double o ? Fixed(o, decimals) : "");
                    }

                    if (withLevel)
                    {
                        row.Add(p.Level is double l ? Fixed(l, decimals) : "");
                    }

                    rows.Add(row);
                }

                tableIndex = specs.Count;
                specs.Add(new TableSpec
                {
                    Position = at,
                    Title = title,
                    Rows = rows,
                    TextHeight = height,
                    TextStyle = style,
                    Style = tableStyle,
                    Layer = layer,
                });
            }

            IReadOnlyList<ItemResult> created = _host.CreateEntities(specs);
            int failed = created.Count(c => !c.Ok);
            var payload = new
            {
                drawn = created.Count - failed,
                failed = failed > 0 ? failed : (int?)null,
                first_error = created.FirstOrDefault(c => !c.Ok)?.Error,
                table = tableIndex >= 0 ? created[tableIndex].Handle : null,
                table_error = tableIndex >= 0 && !created[tableIndex].Ok ? created[tableIndex].Error : null,
                points = points.Select((p, i) => new
                {
                    name = p.Name,
                    easting = Math.Round(p.Point.X, 4),
                    northing = Math.Round(p.Point.Y, 4),
                    chainage = p.Chainage is double c ? Math.Round(c, 4) : (double?)null,
                    offset = p.Offset is double o ? Math.Round(o, 4) : (double?)null,
                    level = p.Level,
                    label = created[labelIndex[i]].Handle,
                }).ToList(),
            };
            string json = JsonSerializer.Serialize(payload, JsonOptions);
            return new ToolOutcome(json, failed == created.Count)
            {
                Summary = $"set out {points.Count} point(s){(tableIndex >= 0 ? " with a table" : "")}{(failed > 0 ? $" ({failed} failed)" : "")}",
            };
        }

        private List<SetoutPoint> PointsFromAlignment(JsonElement input, string handle)
        {
            Path2 path = Curves(new[] { handle })[handle].Path;
            double start = JsonArgs.OptionalNumber(input, "start_chainage", "") ?? 0;
            double end = start + path.Length;
            double offset = JsonArgs.OptionalNumber(input, "offset", "") ?? 0;
            double from = Math.Max(start, JsonArgs.OptionalNumber(input, "from_chainage", "") ?? start);
            double to = Math.Min(end, JsonArgs.OptionalNumber(input, "to_chainage", "") ?? end);

            var chainages = new List<double>();
            void Add(double c)
            {
                if (c >= from - 1e-6 && c <= to + 1e-6 && !chainages.Any(x => Math.Abs(x - c) < 1e-6))
                {
                    chainages.Add(c);
                }
            }

            List<JsonElement>? given = JsonArgs.OptionalArray(input, "chainages", "", 1, 500);
            if (given != null)
            {
                for (int i = 0; i < given.Count; i++)
                {
                    double c = JsonArgs.ReadNumber(given[i], JsonArgs.Index("chainages", i));
                    if (c < start - 1e-6 || c > end + 1e-6)
                    {
                        throw new ToolInputException(
                            $"chainages[{i}] ({JsonArgs.Fmt(c)}) is off the alignment, which runs from chainage {JsonArgs.Fmt(start)} to {JsonArgs.Fmt(end)}");
                    }

                    Add(c);
                }
            }

            if (JsonArgs.OptionalPositive(input, "interval", "") is double step)
            {
                double first = Math.Ceiling((from - 1e-6) / step) * step;
                if ((to - first) / step > 500)
                {
                    throw new ToolInputException("interval gives more than 500 points; use a larger interval or a chainage range");
                }

                for (int k = 0; first + k * step <= to + 1e-6; k++)
                {
                    Add(first + k * step);
                }
            }

            if (JsonArgs.OptionalBool(input, "tangent_points", "", false))
            {
                Add(start);
                foreach (double d in TangentPoints(path))
                {
                    Add(start + d);
                }

                Add(end);
            }

            if (chainages.Count == 0)
            {
                throw new ToolInputException("with an alignment, give chainages, interval or tangent_points: true");
            }

            chainages.Sort();
            List<JsonElement>? levels = JsonArgs.OptionalArray(input, "levels", "", 1, 500);
            if (levels != null && levels.Count != chainages.Count)
            {
                throw new ToolInputException($"levels has {levels.Count} values but there are {chainages.Count} points; give one level per point, in chainage order");
            }

            return chainages.Select((c, i) => new SetoutPoint
            {
                Chainage = c,
                Offset = offset,
                Point = RoadGeometry.PointAt(path, Math.Max(start, Math.Min(end, c)), offset, start),
                Level = levels == null ? (double?)null : JsonArgs.ReadNumber(levels[i], JsonArgs.Index("levels", i)),
            }).ToList();
        }

        // ------------------------------------------------------------------ view_drawing

        private ToolOutcome DoViewDrawing(JsonElement input)
        {
            if (input.ValueKind != JsonValueKind.Object)
            {
                input = JsonDocument.Parse("{}").RootElement;
            }

            Point2? wmin = JsonArgs.OptionalPoint(input, "window_min", "");
            Point2? wmax = JsonArgs.OptionalPoint(input, "window_max", "");
            if ((wmin == null) != (wmax == null))
            {
                throw new ToolInputException("window_min and window_max must be given together");
            }

            List<JsonElement>? handleItems = JsonArgs.OptionalArray(input, "handles", "", 1, 500);
            List<string>? handles = handleItems == null ? null : JsonArgs.ReadStringList(handleItems, "handles");
            string? layer = JsonArgs.OptionalString(input, "layer", "");
            int widthPx = JsonArgs.OptionalInt(input, "width_px", "", 300, 1568) ?? 1000;
            bool picture = JsonArgs.OptionalBool(input, "picture", "", true);

            if (wmin == null)
            {
                (wmin, wmax) = handles != null ? ExtentsOf(handles) : DrawingExtents();
            }

            // A little air around the edges; a point or a single line still gets a window with some size.
            double x0 = Math.Min(wmin!.Value.X, wmax!.Value.X), x1 = Math.Max(wmin.Value.X, wmax.Value.X);
            double y0 = Math.Min(wmin.Value.Y, wmax.Value.Y), y1 = Math.Max(wmin.Value.Y, wmax.Value.Y);
            double pad = Math.Max(Math.Max(x1 - x0, y1 - y0) * 0.04, 1e-6);
            var window = new ViewWindow(x0 - pad, y0 - pad, x1 + pad, y1 + pad);

            EntityListResult list = _host.ListEntities(new EntityQuery
            {
                WindowMin = new Point2(window.MinX, window.MinY),
                WindowMax = new Point2(window.MaxX, window.MaxY),
                Layer = layer,
                Limit = MaxViewEntities,
                ForView = true,
            });

            List<Overlap> overlaps = AnnotationClashes.Find(list.Entities);
            var clashing = new HashSet<string>(overlaps.SelectMany(o => new[] { o.A, o.B }), StringComparer.OrdinalIgnoreCase);

            RenderedView? view = picture ? SchematicRenderer.Render(list.Entities, window, widthPx, clashing) : null;
            const int maxListed = 40;
            var payload = new
            {
                window = new[] { new[] { R(window.MinX), R(window.MinY) }, new[] { R(window.MaxX), R(window.MaxY) } },
                picture = view == null ? null : new { width_px = view.Width, height_px = view.Height, units_per_px = Math.Round(view.UnitsPerPixel, 5) },
                grid_spacing = view?.GridSpacing,
                entities_shown = list.Entities.Count,
                entities_not_shown = list.Truncated ? list.TotalMatched - list.Entities.Count : (int?)null,
                overlap_count = overlaps.Count,
                overlaps = overlaps.Take(maxListed).Select(o => new
                {
                    a = o.A,
                    a_kind = o.KindA,
                    a_text = o.TextA,
                    b = o.B,
                    b_kind = o.KindB,
                    b_text = o.TextB,
                    at = o.At.ToArray(),
                }).ToList(),
                overlaps_not_listed = overlaps.Count > maxListed ? overlaps.Count - maxListed : (int?)null,
            };

            var outcome = new ToolOutcome(JsonSerializer.Serialize(payload, JsonOptions), false)
            {
                Summary = $"looked at {list.Entities.Count} entit{(list.Entities.Count == 1 ? "y" : "ies")}: " +
                          (overlaps.Count == 0 ? "no overlapping annotation" : $"{overlaps.Count} overlap(s)"),
            };
            if (view != null)
            {
                outcome.Images.Add(new ToolImage(view.Png, "image/png"));
            }

            return outcome;

            static double R(double v) => Math.Round(v, 3);
        }

        private (Point2? Min, Point2? Max) ExtentsOf(List<string> handles)
        {
            EntityListResult found = _host.ListEntities(new EntityQuery { Handles = handles, Limit = handles.Count });
            var withBounds = found.Entities.Where(e => e.BoundsMin != null && e.BoundsMax != null).ToList();
            if (withBounds.Count == 0)
            {
                throw new ToolInputException("none of those handles is an entity in the current space");
            }

            return (
                new Point2(withBounds.Min(e => e.BoundsMin![0]), withBounds.Min(e => e.BoundsMin![1])),
                new Point2(withBounds.Max(e => e.BoundsMax![0]), withBounds.Max(e => e.BoundsMax![1])));
        }

        private (Point2? Min, Point2? Max) DrawingExtents()
        {
            DrawingInfo info = _host.GetDrawingInfo();
            if (info.ExtentsMin == null || info.ExtentsMax == null)
            {
                throw new ToolInputException("the drawing is empty; there is nothing to look at");
            }

            return (new Point2(info.ExtentsMin[0], info.ExtentsMin[1]), new Point2(info.ExtentsMax[0], info.ExtentsMax[1]));
        }

        // ------------------------------------------------------------------ helpers

        private double DefaultTextHeight()
        {
            double? h = _host.GetDrawingInfo().DefaultTextHeight;
            return h is double v && v > 0 ? v : 2.5;
        }

        private static string Fixed(double value, int decimals) =>
            Math.Round(value, decimals, MidpointRounding.AwayFromZero).ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }
}
