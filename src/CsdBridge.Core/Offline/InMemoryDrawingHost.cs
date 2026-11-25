using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;

namespace CsdBridge.Core.Offline
{
    /// <summary>
    /// A drawing that lives in memory. Used by the CLI harness (to try prompts without
    /// BricsCAD, then preview the result as DXF) and by the unit tests.
    /// </summary>
    public sealed class InMemoryDrawingHost : IDrawingHost
    {
        private readonly List<StoredEntity> _entities = new List<StoredEntity>();
        private readonly Dictionary<string, LayerInfo> _layers = new Dictionary<string, LayerInfo>(StringComparer.OrdinalIgnoreCase);
        private int _nextHandle = 0x100;

        public InMemoryDrawingHost(string units = "Millimeters")
        {
            Units = units;
            _layers["0"] = new LayerInfo { Name = "0", Color = 7, Linetype = "CONTINUOUS" };
        }

        public string Units { get; set; }
        public string CurrentLayer { get; set; } = "0";
        public double DefaultTextHeight { get; set; } = 2.5;

        /// <summary>Block names that block_insert may use.</summary>
        public List<string> Blocks { get; } = new List<string>();

        /// <summary>Styles that entities may name; anything else is refused, as in a real drawing.</summary>
        public List<string> TextStyles { get; } = new List<string> { "Standard" };
        public List<string> DimStyles { get; } = new List<string> { "Standard" };
        public List<string> MLeaderStyles { get; } = new List<string> { "Standard" };
        public List<string> TableStyles { get; } = new List<string> { "Standard" };

        /// <summary>Answers pick_point. Return null to simulate Esc.</summary>
        public Func<string, Point2?>? PointPicker { get; set; }

        /// <summary>Answers select_entities with handles. Return null to simulate Esc.</summary>
        public Func<string, IReadOnlyList<string>?>? EntitySelector { get; set; }

        public IReadOnlyList<StoredEntity> Entities => _entities;
        public IReadOnlyCollection<LayerInfo> Layers => _layers.Values;

        public sealed class StoredEntity
        {
            public StoredEntity(string handle, EntitySpec spec)
            {
                Handle = handle;
                Spec = spec;
            }

            public string Handle { get; }
            public EntitySpec Spec { get; set; }
            public string Layer => Spec.Layer ?? "0";
        }

        public DrawingInfo GetDrawingInfo()
        {
            var info = new DrawingInfo
            {
                FileName = "in-memory.dwg",
                Units = Units,
                CurrentSpace = "Model",
                CurrentLayer = CurrentLayer,
                CurrentTextStyle = "Standard",
                CurrentDimStyle = "Standard",
                DefaultTextHeight = DefaultTextHeight,
                EntityCount = _entities.Count,
                Layers = _layers.Values.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                Linetypes = new List<string> { "CONTINUOUS", "HIDDEN", "CENTER", "DASHED", "PHANTOM" },
                TextStyles = TextStyles.ToList(),
                DimStyles = DimStyles.ToList(),
                MLeaderStyles = MLeaderStyles.ToList(),
                TableStyles = TableStyles.ToList(),
                CurrentMLeaderStyle = "Standard",
                CurrentTableStyle = "Standard",
                Blocks = Blocks.ToList(),
            };

            if (_entities.Count > 0)
            {
                var (min, max) = Bounds(_entities.Select(e => e.Spec));
                info.ExtentsMin = min.ToArray();
                info.ExtentsMax = max.ToArray();
            }

            return info;
        }

        public EntityListResult ListEntities(EntityQuery query)
        {
            IEnumerable<StoredEntity> matches = _entities;
            if (query.Layer != null)
            {
                matches = matches.Where(e => string.Equals(e.Layer, query.Layer, StringComparison.OrdinalIgnoreCase));
            }

            if (query.HasKindFilter)
            {
                // Every in-memory entity has a known kind, so IncludeOtherKinds adds nothing here.
                matches = matches.Where(e => query.Kinds != null && query.Kinds.Contains(e.Spec.Kind));
            }

            if (query.Handles != null)
            {
                var set = new HashSet<string>(query.Handles, StringComparer.OrdinalIgnoreCase);
                matches = matches.Where(e => set.Contains(e.Handle));
            }

            if (query.WindowMin is Point2 wmin && query.WindowMax is Point2 wmax)
            {
                double x0 = Math.Min(wmin.X, wmax.X), x1 = Math.Max(wmin.X, wmax.X);
                double y0 = Math.Min(wmin.Y, wmax.Y), y1 = Math.Max(wmin.Y, wmax.Y);
                matches = matches.Where(e =>
                {
                    var (min, max) = Bounds(new[] { e.Spec });
                    return min.X <= x1 && max.X >= x0 && min.Y <= y1 && max.Y >= y0;
                });
            }

            var all = matches.ToList();
            return new EntityListResult
            {
                TotalMatched = all.Count,
                Truncated = all.Count > query.Limit,
                Entities = all.Take(query.Limit).Select(e => Summarize(e)).ToList(),
            };
        }

        public IReadOnlyList<ItemResult> EnsureLayers(IReadOnlyList<LayerSpec> layers)
        {
            var results = new List<ItemResult>();
            for (int i = 0; i < layers.Count; i++)
            {
                LayerSpec spec = layers[i];
                bool existed = _layers.TryGetValue(spec.Name, out LayerInfo? layer);
                if (layer == null)
                {
                    layer = new LayerInfo { Name = spec.Name, Color = 7, Linetype = "CONTINUOUS" };
                    _layers[spec.Name] = layer;
                }

                if (spec.ColorIndex != null)
                {
                    layer.Color = spec.ColorIndex.Value;
                }

                if (spec.Linetype != null)
                {
                    layer.Linetype = spec.Linetype.ToUpperInvariant();
                }

                if (spec.LineweightMm != null)
                {
                    layer.LineweightMm = spec.LineweightMm;
                }

                results.Add(ItemResult.Success(i, note: existed ? "updated" : "created"));
            }

            return results;
        }

        public IReadOnlyList<ItemResult> CreateEntities(IReadOnlyList<EntitySpec> entities)
        {
            var results = new List<ItemResult>();
            for (int i = 0; i < entities.Count; i++)
            {
                EntitySpec spec = entities[i];
                if (spec is BlockInsertSpec bi && !Blocks.Contains(bi.BlockName, StringComparer.OrdinalIgnoreCase))
                {
                    results.Add(ItemResult.Failure(i, $"Block '{bi.BlockName}' is not defined in this drawing."));
                    continue;
                }

                if (spec is HatchSpec hatch)
                {
                    string? problem = ResolveHatch(hatch);
                    if (problem != null)
                    {
                        results.Add(ItemResult.Failure(i, problem));
                        continue;
                    }
                }

                string? styleProblem = StyleProblem(spec, spec.Style)
                    ?? (spec is MLeaderSpec leader ? UnknownStyle("Text style", leader.TextStyle, TextStyles) : null)
                    ?? (spec is TableSpec tableSpec ? UnknownStyle("Text style", tableSpec.TextStyle, TextStyles) : null);
                if (styleProblem != null)
                {
                    results.Add(ItemResult.Failure(i, styleProblem));
                    continue;
                }

                string? note = null;
                spec.Layer ??= CurrentLayer;
                if (!_layers.ContainsKey(spec.Layer))
                {
                    _layers[spec.Layer] = new LayerInfo { Name = spec.Layer, Color = 7, Linetype = "CONTINUOUS" };
                    note = $"created layer {spec.Layer}";
                }

                if (spec is TextSpec t && t.Height == null)
                {
                    t.Height = DefaultTextHeight;
                }

                if (spec is MTextSpec m && m.Height == null)
                {
                    m.Height = DefaultTextHeight;
                }

                if (spec is MLeaderSpec ml && ml.Height == null)
                {
                    ml.Height = DefaultTextHeight;
                }

                if (spec is TableSpec table)
                {
                    (table.ColumnWidths, double rowHeight, double textHeight) = AnnotationGeometry.TableLayout(table, DefaultTextHeight);
                    table.RowHeight = rowHeight;
                    table.TextHeight = textHeight;
                }

                string handle = NewHandle();
                _entities.Add(new StoredEntity(handle, spec));
                results.Add(ItemResult.Success(i, handle, note));
            }

            return results;
        }

        /// <summary>Turn boundary handles into explicit loops. Seed points need a real CAD application.</summary>
        private string? ResolveHatch(HatchSpec h)
        {
            if (h.SeedPoints.Count > 0)
            {
                return "Finding a boundary from a point inside it needs BricsCAD; offline, give boundary_handles or points.";
            }

            foreach (string handle in h.BoundaryHandles)
            {
                StoredEntity? e = Find(handle);
                Geometry.Path2? p = e == null ? null : Geometry.Path2.From(e.Spec);
                if (p == null || !p.Closed)
                {
                    return $"{handle} is not a closed polyline or circle.";
                }

                h.Loops.Add(Geometry.PathOps.ToPolyline(p));
            }

            h.BoundaryHandles = new List<string>();
            return h.Loops.Count == 0 ? "The hatch has no boundary." : null;
        }

        public IReadOnlyDictionary<string, EntitySpec> GetCurves(IReadOnlyList<string> handles)
        {
            var result = new Dictionary<string, EntitySpec>(StringComparer.OrdinalIgnoreCase);
            foreach (string h in handles)
            {
                StoredEntity? e = Find(h);
                if (e != null && (e.Spec is LineSpec || e.Spec is ArcSpec || e.Spec is CircleSpec || e.Spec is PolylineSpec))
                {
                    EntitySpec copy = Clone(e.Spec);
                    copy.Layer = e.Layer;
                    result[e.Handle] = copy;
                }
            }

            return result;
        }

        public IReadOnlyList<ItemResult> ModifyEntities(IReadOnlyList<ModifyOperation> operations)
        {
            var results = new List<ItemResult>();
            for (int i = 0; i < operations.Count; i++)
            {
                ModifyOperation op = operations[i];
                var missing = op.Handles.Where(h => Find(h) == null).ToList();
                if (missing.Count > 0)
                {
                    results.Add(ItemResult.Failure(i, "Unknown handle(s): " + string.Join(", ", missing)));
                    continue;
                }

                var targets = op.Handles.Select(h => Find(h)!).ToList();
                var result = ItemResult.Success(i);
                switch (op.Action)
                {
                    case ModifyAction.Move:
                        targets.ForEach(e => Transform(e.Spec, op.Dx, op.Dy, default, 0, 1));
                        break;
                    case ModifyAction.Copy:
                        result.NewHandles = new List<string>();
                        for (int k = 1; k <= op.Count; k++)
                        {
                            foreach (var e in targets)
                            {
                                EntitySpec copy = Clone(e.Spec);
                                Transform(copy, op.Dx * k, op.Dy * k, default, 0, 1);
                                string handle = NewHandle();
                                _entities.Add(new StoredEntity(handle, copy));
                                result.NewHandles.Add(handle);
                            }
                        }

                        break;
                    case ModifyAction.Rotate:
                        targets.ForEach(e => Transform(e.Spec, 0, 0, op.BasePoint, op.AngleDeg, 1));
                        break;
                    case ModifyAction.Scale:
                        targets.ForEach(e => Transform(e.Spec, 0, 0, op.BasePoint, 0, op.Factor));
                        break;
                    case ModifyAction.Erase:
                        _entities.RemoveAll(e => targets.Contains(e));
                        break;
                    case ModifyAction.SetLayer:
                        if (!_layers.ContainsKey(op.Layer!))
                        {
                            _layers[op.Layer!] = new LayerInfo { Name = op.Layer!, Color = 7, Linetype = "CONTINUOUS" };
                        }

                        targets.ForEach(e => e.Spec.Layer = op.Layer);
                        break;
                    case ModifyAction.SetColor:
                        targets.ForEach(e => e.Spec.ColorIndex = op.ColorIndex);
                        break;
                }

                results.Add(result);
            }

            return results;
        }

        public IReadOnlyList<ItemResult> EditAnnotations(IReadOnlyList<AnnotationEdit> edits)
        {
            var results = new List<ItemResult>();
            for (int i = 0; i < edits.Count; i++)
            {
                AnnotationEdit edit = edits[i];
                StoredEntity? e = Find(edit.Handle);
                if (e == null)
                {
                    results.Add(ItemResult.Failure(i, $"Unknown handle: {edit.Handle}"));
                    continue;
                }

                string? problem = AnnotationEdits.Problem(edit, KindName(e.Spec))
                    ?? (edit.Style == null ? null : StyleProblem(e.Spec, edit.Style))
                    ?? CellProblem(e.Spec as TableSpec, edit.Cells);
                if (problem != null)
                {
                    results.Add(ItemResult.Failure(i, problem));
                    continue;
                }

                Apply(e.Spec, edit);
                results.Add(ItemResult.Success(i, e.Handle));
            }

            return results;
        }

        private static string? CellProblem(TableSpec? table, List<TableCellEdit>? cells)
        {
            if (table == null || cells == null)
            {
                return null;
            }

            List<List<string>> all = AnnotationGeometry.TableCells(table);
            foreach (TableCellEdit c in cells)
            {
                if (c.Row >= all.Count || c.Column >= table.ColumnCount)
                {
                    return $"cell row {c.Row}, column {c.Column} is outside the table ({all.Count} rows x {table.ColumnCount} columns, counted from 0)";
                }

                if (table.Title != null && c.Row == 0 && c.Column != 0)
                {
                    return "row 0 is the title, one merged cell: set it with column 0";
                }
            }

            return null;
        }

        private static void Apply(EntitySpec spec, AnnotationEdit edit)
        {
            if (edit.Style != null)
            {
                spec.Style = edit.Style;
            }

            switch (spec)
            {
                case TextSpec t:
                    t.Text = edit.Text ?? t.Text;
                    t.Height = edit.Height ?? t.Height;
                    t.RotationDeg = edit.RotationDeg ?? t.RotationDeg;
                    t.Position = edit.Position ?? t.Position;
                    break;

                case MTextSpec m:
                    m.Text = edit.Text ?? m.Text;
                    m.Height = edit.Height ?? m.Height;
                    m.Width = edit.Width ?? m.Width;
                    m.RotationDeg = edit.RotationDeg ?? m.RotationDeg;
                    m.Position = edit.Position ?? m.Position;
                    break;

                case MLeaderSpec l:
                    l.Text = edit.Text ?? l.Text;
                    l.Height = edit.Height ?? l.Height;
                    l.Width = edit.Width ?? l.Width;
                    l.TextPosition = edit.Position ?? l.TextPosition;
                    l.ArrowPoints = edit.ArrowPoints?.ToList() ?? l.ArrowPoints;
                    break;

                case DimensionSpec d:
                    if (edit.TextOverride != null)
                    {
                        d.TextOverride = edit.TextOverride.Length == 0 || edit.TextOverride == "<>" ? null : edit.TextOverride;
                    }

                    break;

                case BlockInsertSpec b:
                    if (edit.Attributes != null)
                    {
                        b.Attributes ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (KeyValuePair<string, string> kv in edit.Attributes)
                        {
                            b.Attributes[kv.Key] = kv.Value;
                        }
                    }

                    break;

                case TableSpec table:
                    foreach (TableCellEdit c in edit.Cells ?? new List<TableCellEdit>())
                    {
                        int row = c.Row;
                        if (table.Title != null && row-- == 0)
                        {
                            table.Title = c.Text;
                        }
                        else
                        {
                            table.Rows[row][c.Column] = c.Text;
                        }
                    }

                    break;
            }
        }

        /// <summary>Why an entity can't use a style name (it must exist, as in a real drawing), or null.</summary>
        private string? StyleProblem(EntitySpec spec, string? style)
        {
            switch (spec)
            {
                case TextSpec _:
                case MTextSpec _: return UnknownStyle("Text style", style, TextStyles);
                case DimensionSpec _: return UnknownStyle("Dimension style", style, DimStyles);
                case MLeaderSpec _: return UnknownStyle("Multileader style", style, MLeaderStyles);
                case TableSpec _: return UnknownStyle("Table style", style, TableStyles);
                default: return null;
            }
        }

        private static string? UnknownStyle(string what, string? name, List<string> known) =>
            name == null || known.Contains(name, StringComparer.OrdinalIgnoreCase)
                ? null
                : $"{what} '{name}' is not in this drawing. Available: {string.Join(", ", known)}.";

        // ------------------------------------------------------------------ exact geometry

        public IReadOnlyList<ItemResult> OffsetEntities(IReadOnlyList<OffsetOperation> operations)
        {
            var results = new List<ItemResult>();
            for (int i = 0; i < operations.Count; i++)
            {
                OffsetOperation op = operations[i];
                var missing = op.Handles.Where(h => Find(h) == null).ToList();
                if (missing.Count > 0)
                {
                    results.Add(ItemResult.Failure(i, "Unknown handle(s): " + string.Join(", ", missing)));
                    continue;
                }

                // Build everything first so a failure part-way leaves the drawing unchanged.
                var created = new List<EntitySpec>();
                string? error = null;
                foreach (StoredEntity source in op.Handles.Select(h => Find(h)!))
                {
                    error = OffsetOne(source, op, created);
                    if (error != null)
                    {
                        break;
                    }
                }

                if (error != null)
                {
                    results.Add(ItemResult.Failure(i, error));
                    continue;
                }

                var item = ItemResult.Success(i);
                item.NewHandles = new List<string>();
                foreach (EntitySpec spec in created)
                {
                    if (!_layers.ContainsKey(spec.Layer!))
                    {
                        _layers[spec.Layer!] = new LayerInfo { Name = spec.Layer!, Color = 7, Linetype = "CONTINUOUS" };
                    }

                    string handle = NewHandle();
                    _entities.Add(new StoredEntity(handle, spec));
                    item.NewHandles.Add(handle);
                }

                results.Add(item);
            }

            return results;
        }

        private static string? OffsetOne(StoredEntity source, OffsetOperation op, List<EntitySpec> created)
        {
            Path2? path = Path2.From(source.Spec);
            if (path == null)
            {
                return $"{source.Handle} is {KindName(source.Spec)}, not a curve; only lines, polylines, arcs and circles can be offset";
            }

            if (op.Side != OffsetSide.TowardsPoint && !path.Closed)
            {
                return $"{source.Handle} is not closed, so 'inside'/'outside' doesn't apply; give a side_point instead";
            }

            for (int k = 1; k <= op.Count; k++)
            {
                EntitySpec? plus, minus;
                try
                {
                    plus = Offsets.Offset(source.Spec, op.Distance * k);
                    minus = Offsets.Offset(source.Spec, -op.Distance * k);
                }
                catch (NotSupportedException ex)
                {
                    return ex.Message;
                }

                EntitySpec? chosen = ChooseSide(path, plus, minus, op, out string? why);
                if (chosen == null)
                {
                    return $"{source.Handle}: {why}";
                }

                chosen.Layer = op.Layer ?? source.Layer;
                chosen.ColorIndex = source.Spec.ColorIndex;
                created.Add(chosen);
            }

            return null;
        }

        private static EntitySpec? ChooseSide(Path2 original, EntitySpec? plus, EntitySpec? minus, OffsetOperation op, out string? error)
        {
            error = null;
            var candidates = new[] { plus, minus }.Where(c => c != null).Select(c => c!).ToList();

            if (op.Side == OffsetSide.TowardsPoint)
            {
                var (q, _, distance) = original.Closest(op.SidePoint);
                if (distance < 1e-9)
                {
                    error = "side_point is on the curve; give a point clearly on one side";
                    return null;
                }

                double vx = op.SidePoint.X - q.X, vy = op.SidePoint.Y - q.Y;
                EntitySpec? best = null;
                double bestDot = 0;
                foreach (EntitySpec c in candidates)
                {
                    var (p, _, _) = Path2.From(c)!.Closest(q);
                    double dot = ((p.X - q.X) * vx + (p.Y - q.Y) * vy) / distance;
                    if (dot > bestDot + 1e-9)
                    {
                        best = c;
                        bestDot = dot;
                    }
                }

                if (best == null)
                {
                    error = candidates.Count < 2
                        ? "the offset distance is too large for this shape on that side"
                        : "side_point is in line with the curve; give a point clearly to one side of it";
                }

                return best;
            }

            // Inside / outside of a closed shape: compare enclosed areas. An inward offset
            // that is too large collapses (null); outward offsets always exist.
            if (candidates.Count == 0)
            {
                error = "this shape can't be offset";
                return null;
            }

            if (candidates.Count == 1)
            {
                if (op.Side == OffsetSide.Inside)
                {
                    error = "the offset distance is too large to fit inside this shape";
                    return null;
                }

                return candidates[0];
            }

            double areaPlus = Path2.From(plus!)!.Area, areaMinus = Path2.From(minus!)!.Area;
            bool plusIsInside = areaPlus < areaMinus;
            return (op.Side == OffsetSide.Inside) == plusIsInside ? plus : minus;
        }

        public IntersectionResult Intersect(IntersectQuery query)
        {
            var result = new IntersectionResult();
            var skipped = new List<string>();
            var paths = new Dictionary<string, Path2>(StringComparer.OrdinalIgnoreCase);
            foreach (string h in query.Handles.Concat(query.With ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                StoredEntity? e = Find(h);
                Path2? path = e == null ? null : Path2.From(e.Spec);
                if (e == null)
                {
                    skipped.Add($"{h}: not found");
                }
                else if (path == null)
                {
                    skipped.Add($"{h}: {KindName(e.Spec)} is not a curve");
                }
                else
                {
                    paths[h] = path;
                }
            }

            IEnumerable<(string A, string B)> pairs = query.With == null
                ? query.Handles.SelectMany((a, i) => query.Handles.Skip(i + 1).Select(b => (a, b)))
                : query.Handles.SelectMany(a => query.With.Where(b => !string.Equals(a, b, StringComparison.OrdinalIgnoreCase)).Select(b => (a, b)));

            foreach (var (a, b) in pairs)
            {
                if (!paths.TryGetValue(a, out Path2? pa) || !paths.TryGetValue(b, out Path2? pb))
                {
                    continue;
                }

                result.PairsChecked++;
                List<Point2> points = Intersections.Find(pa, pb, query.Extend);
                if (points.Count > 0)
                {
                    result.Intersections.Add(new IntersectionPair { A = a, B = b, Points = points.Select(p => p.ToArray()).ToList() });
                }
            }

            result.Skipped = skipped.Count > 0 ? skipped : null;
            return result;
        }

        public MeasureResult Measure(MeasureQuery query)
        {
            var result = new MeasureResult();
            foreach (string h in query.Handles)
            {
                StoredEntity? e = Find(h);
                if (e == null)
                {
                    result.Entities.Add(new EntityMeasurement { Handle = h, Error = "not found" });
                    continue;
                }

                var m = new EntityMeasurement { Handle = e.Handle, Kind = KindName(e.Spec) };
                Path2? path = Path2.From(e.Spec);
                if (path != null)
                {
                    double length = path.Length;
                    m.Length = Math.Round(length, 6);
                    m.Closed = path.Closed;
                    if (path.Closed)
                    {
                        m.Area = Math.Round(path.Area, 6);
                    }
                    else
                    {
                        m.Start = path.Start.ToArray();
                        m.End = path.End.ToArray();
                    }

                    m.Mid = path.PointAt(length / 2).ToArray();
                    if (query.Point is Point2 p)
                    {
                        var (closest, _, distance) = path.Closest(p);
                        m.ClosestPoint = closest.ToArray();
                        m.DistanceToPoint = Math.Round(distance, 6);
                    }
                }
                else if (e.Spec is DimensionSpec)
                {
                    m.Measurement = Math.Round(Measure(e.Spec), 6);
                }

                var (min, max) = Bounds(new[] { e.Spec });
                m.BoundsMin = min.ToArray();
                m.BoundsMax = max.ToArray();
                result.Entities.Add(m);
            }

            return result;
        }

        public IReadOnlyList<CurvePoints> PointsOnCurves(IReadOnlyList<PointsRequest> requests)
        {
            var results = new List<CurvePoints>();
            for (int i = 0; i < requests.Count; i++)
            {
                PointsRequest r = requests[i];
                var item = new CurvePoints { Index = i, Handle = r.Handle };
                StoredEntity? e = Find(r.Handle);
                Path2? path = e == null ? null : Path2.From(e.Spec);
                if (path == null)
                {
                    item.Error = e == null ? "not found" : $"{KindName(e.Spec)} is not a curve";
                    results.Add(item);
                    continue;
                }

                double length = path.Length;
                item.Length = Math.Round(length, 6);
                try
                {
                    item.Points = SetOut.Distances(r, length, path.Closed)
                        .Select(d => new PointOnCurve
                        {
                            Point = path.PointAt(d).ToArray(),
                            Distance = Math.Round(d, 6),
                            Angle = Degrees(path.TangentAt(d)),
                        })
                        .ToList();
                    item.Ok = true;
                }
                catch (ArgumentException ex)
                {
                    item.Error = ex.Message;
                }

                results.Add(item);
            }

            return results;
        }

        private static double Degrees(double radians)
        {
            double deg = radians * 180 / Math.PI % 360;
            return Math.Round(deg < 0 ? deg + 360 : deg, 6);
        }

        private static string KindName(EntitySpec spec)
        {
            switch (spec)
            {
                case DimensionSpec _:
                    return "dimension";
                case MLeaderSpec _:
                    return "mleader";
                default:
                    return Tools.ToolDispatcher.Snake(spec.Kind);
            }
        }

        public PickPointResult PickPoint(string prompt)
        {
            Point2? p = PointPicker?.Invoke(prompt);
            return p == null
                ? new PickPointResult { Cancelled = true }
                : new PickPointResult { Point = p.Value.ToArray() };
        }

        public SelectionResult SelectEntities(string prompt)
        {
            IReadOnlyList<string>? handles = EntitySelector?.Invoke(prompt);
            if (handles == null)
            {
                return new SelectionResult { Cancelled = true };
            }

            return new SelectionResult
            {
                Entities = handles.Select(Find).Where(e => e != null).Select(e => Summarize(e!)).ToList(),
            };
        }

        // ------------------------------------------------------------------ helpers

        private string NewHandle() => (_nextHandle++).ToString("X", CultureInfo.InvariantCulture);

        private StoredEntity? Find(string handle) =>
            _entities.FirstOrDefault(e => string.Equals(e.Handle, handle, StringComparison.OrdinalIgnoreCase));

        public static double Measure(EntitySpec spec) => AnnotationGeometry.Measure(spec);

        private EntitySummary Summarize(StoredEntity e)
        {
            var s = new EntitySummary { Handle = e.Handle, Layer = e.Layer, Color = e.Spec.ColorIndex, Style = e.Spec.Style };
            double textHeight = DefaultTextHeight;
            switch (e.Spec)
            {
                case LineSpec l:
                    s.Kind = "line";
                    s.Start = l.Start.ToArray();
                    s.End = l.End.ToArray();
                    break;
                case PolylineSpec p:
                    s.Kind = "polyline";
                    s.Points = p.Points.Select(pt => pt.ToArray()).ToArray();
                    s.Bulges = p.Bulges != null && p.Bulges.Any(b => Math.Abs(b) > 1e-12) ? p.Bulges.Select(b => Math.Round(b, 6)).ToArray() : null;
                    s.Closed = p.Closed;
                    break;
                case HatchSpec h:
                {
                    s.Kind = "hatch";
                    s.Pattern = h.Pattern;
                    double area = 0;
                    for (int k = 0; k < h.Loops.Count; k++)
                    {
                        double a = Geometry.Path2.From(h.Loops[k])?.Area ?? 0;
                        area += k == 0 ? a : -a;
                    }

                    s.Area = Math.Round(area, 4);
                    break;
                }
                case CircleSpec c:
                    s.Kind = "circle";
                    s.Center = c.Center.ToArray();
                    s.Radius = Math.Round(c.Radius, 4);
                    break;
                case ArcSpec a:
                    s.Kind = "arc";
                    s.Center = a.Center.ToArray();
                    s.Radius = Math.Round(a.Radius, 4);
                    s.StartAngle = Math.Round(a.StartAngleDeg, 4);
                    s.EndAngle = Math.Round(a.EndAngleDeg, 4);
                    break;
                case TextSpec t:
                    s.Kind = "text";
                    s.Position = t.Position.ToArray();
                    s.Text = t.Text;
                    s.Height = t.Height;
                    s.Rotation = t.RotationDeg;
                    break;
                case MTextSpec m:
                    s.Kind = "mtext";
                    s.Position = m.Position.ToArray();
                    s.Text = m.Text;
                    s.Height = m.Height;
                    s.Rotation = m.RotationDeg;
                    break;
                case LinearDimensionSpec d:
                    s.DimType = "linear";
                    s.Start = d.P1.ToArray();
                    s.End = d.P2.ToArray();
                    s.DimLinePoint = d.DimLinePoint.ToArray();
                    s.Rotation = d.RotationDeg;
                    break;
                case AlignedDimensionSpec d:
                    s.DimType = "aligned";
                    s.Start = d.P1.ToArray();
                    s.End = d.P2.ToArray();
                    s.DimLinePoint = d.DimLinePoint.ToArray();
                    break;
                case RadialDimensionSpec d:
                    s.DimType = d.Diameter ? "diameter" : "radius";
                    s.Center = d.Center.ToArray();
                    s.Start = d.ChordPoint.ToArray();
                    break;
                case AngularDimensionSpec d:
                    s.DimType = d.ArcLength ? "arc_length" : "angular";
                    s.Center = d.Center.ToArray();
                    s.Start = d.P1.ToArray();
                    s.End = d.P2.ToArray();
                    s.DimLinePoint = d.ArcPoint.ToArray();
                    break;
                case OrdinateDimensionSpec d:
                    s.DimType = d.UseXAxis ? "ordinate_x" : "ordinate_y";
                    s.Start = d.FeaturePoint.ToArray();
                    s.End = d.LeaderEnd.ToArray();
                    break;
                case MLeaderSpec l:
                    s.Kind = "mleader";
                    s.Text = l.Text;
                    s.Height = l.Height;
                    s.Position = l.TextPosition.ToArray();
                    s.ArrowPoints = l.ArrowPoints.Select(p => p.ToArray()).ToArray();
                    s.TextBox = AnnotationGeometry.ToArrays(AnnotationGeometry.TextBox(l, l.Height ?? textHeight));
                    break;
                case TableSpec t:
                {
                    const int maxRows = 40;
                    List<List<string>> cells = AnnotationGeometry.TableCells(t);
                    s.Kind = "table";
                    s.Position = t.Position.ToArray();
                    s.Height = t.TextHeight;
                    s.RowCount = cells.Count;
                    s.ColumnCount = t.ColumnCount;
                    s.Rows = cells.Take(maxRows).Select(r => r.ToArray()).ToArray();
                    s.ColumnWidths = t.ColumnWidths?.Select(w => Math.Round(w, 4)).ToArray();
                    break;
                }
                case BlockInsertSpec b:
                    s.Kind = "block_insert";
                    s.Block = b.BlockName;
                    s.Position = b.Position.ToArray();
                    s.Rotation = b.RotationDeg;
                    s.Scale = Math.Abs(b.Scale - 1) > 1e-9 ? b.Scale : (double?)null;
                    s.Attributes = b.Attributes == null || b.Attributes.Count == 0 ? null : new Dictionary<string, string>(b.Attributes);
                    break;
            }

            if (e.Spec is DimensionSpec dim)
            {
                s.Kind = "dimension";
                s.Measurement = Math.Round(Measure(dim), 4);
                s.Text = dim.TextOverride;
                s.TextBox = AnnotationGeometry.ToArrays(AnnotationGeometry.TextBox(dim, textHeight));
            }
            else if (e.Spec is TextSpec text)
            {
                s.TextBox = AnnotationGeometry.ToArrays(AnnotationGeometry.TextBox(text, text.Height ?? textHeight));
            }
            else if (e.Spec is MTextSpec mtext)
            {
                s.TextBox = AnnotationGeometry.ToArrays(AnnotationGeometry.TextBox(mtext, mtext.Height ?? textHeight));
            }

            var (min, max) = Bounds(new[] { e.Spec });
            s.BoundsMin = min.ToArray();
            s.BoundsMax = max.ToArray();
            return s;
        }

        /// <summary>Approximate bounding box (arcs use their full circle).</summary>
        public static (Point2 Min, Point2 Max) Bounds(IEnumerable<EntitySpec> specs)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            void Add(Point2 p, double r = 0)
            {
                x0 = Math.Min(x0, p.X - r);
                y0 = Math.Min(y0, p.Y - r);
                x1 = Math.Max(x1, p.X + r);
                y1 = Math.Max(y1, p.Y + r);
            }

            foreach (EntitySpec spec in specs)
            {
                foreach (var (p, r) in KeyPoints(spec))
                {
                    Add(p, r);
                }
            }

            return x0 == double.MaxValue
                ? (new Point2(0, 0), new Point2(0, 0))
                : (new Point2(x0, y0), new Point2(x1, y1));
        }

        private static IEnumerable<(Point2, double)> KeyPoints(EntitySpec spec)
        {
            switch (spec)
            {
                case LineSpec l: return new[] { (l.Start, 0.0), (l.End, 0.0) };
                case PolylineSpec p: return p.Points.Select(pt => (pt, 0.0));
                case CircleSpec c: return new[] { (c.Center, c.Radius) };
                case ArcSpec a: return new[] { (a.Center, a.Radius) };
                case TextSpec t: return new[] { (t.Position, 0.0) };
                case MTextSpec m: return new[] { (m.Position, 0.0) };
                case LinearDimensionSpec d: return new[] { (d.P1, 0.0), (d.P2, 0.0), (d.DimLinePoint, 0.0) };
                case AlignedDimensionSpec d: return new[] { (d.P1, 0.0), (d.P2, 0.0), (d.DimLinePoint, 0.0) };
                case RadialDimensionSpec d:
                    return new[] { (d.Diameter ? new Point2(2 * d.Center.X - d.ChordPoint.X, 2 * d.Center.Y - d.ChordPoint.Y) : d.Center, 0.0), (d.ChordPoint, 0.0) };
                case AngularDimensionSpec d: return new[] { (d.P1, 0.0), (d.P2, 0.0), (d.ArcPoint, 0.0) };
                case OrdinateDimensionSpec d: return new[] { (d.FeaturePoint, 0.0), (d.LeaderEnd, 0.0) };
                case MLeaderSpec l:
                    return l.ArrowPoints.Concat(new[] { l.TextPosition }).Concat(AnnotationGeometry.TextBox(l, l.Height ?? 2.5)).Select(pt => (pt, 0.0)).ToList();
                case TableSpec t:
                {
                    (List<double> widths, double rowHeight, double _) = AnnotationGeometry.TableLayout(t, t.TextHeight ?? 2.5);
                    int rows = t.Rows.Count + (t.Title != null ? 1 : 0);
                    return new[] { (t.Position, 0.0), (new Point2(t.Position.X + widths.Sum(), t.Position.Y - rows * rowHeight), 0.0) };
                }
                case BlockInsertSpec b: return new[] { (b.Position, 0.0) };
                case HatchSpec h: return h.Loops.SelectMany(l => l.Points).Select(pt => (pt, 0.0)).ToList();
                default: return Array.Empty<(Point2, double)>();
            }
        }

        /// <summary>Apply: scale and rotate about basePoint, then translate by (dx, dy).</summary>
        private static void Transform(EntitySpec spec, double dx, double dy, Point2 basePoint, double angleDeg, double factor)
        {
            double r = angleDeg * Math.PI / 180, cos = Math.Cos(r), sin = Math.Sin(r);
            Point2 T(Point2 p)
            {
                double x = (p.X - basePoint.X) * factor, y = (p.Y - basePoint.Y) * factor;
                return new Point2(basePoint.X + x * cos - y * sin + dx, basePoint.Y + x * sin + y * cos + dy);
            }

            switch (spec)
            {
                case LineSpec l:
                    l.Start = T(l.Start);
                    l.End = T(l.End);
                    break;
                case PolylineSpec p:
                    p.Points = p.Points.Select(T).ToList();
                    break;
                case HatchSpec h:
                    foreach (PolylineSpec loop in h.Loops)
                    {
                        loop.Points = loop.Points.Select(T).ToList();
                    }

                    h.AngleDeg += angleDeg;
                    h.Scale *= factor;
                    break;
                case CircleSpec c:
                    c.Center = T(c.Center);
                    c.Radius *= factor;
                    break;
                case ArcSpec a:
                    a.Center = T(a.Center);
                    a.Radius *= factor;
                    a.StartAngleDeg += angleDeg;
                    a.EndAngleDeg += angleDeg;
                    break;
                case TextSpec t:
                    t.Position = T(t.Position);
                    t.Height *= factor;
                    t.RotationDeg += angleDeg;
                    break;
                case MTextSpec m:
                    m.Position = T(m.Position);
                    m.Height *= factor;
                    m.Width *= factor;
                    m.RotationDeg += angleDeg;
                    break;
                case LinearDimensionSpec d:
                    d.P1 = T(d.P1);
                    d.P2 = T(d.P2);
                    d.DimLinePoint = T(d.DimLinePoint);
                    d.RotationDeg += angleDeg;
                    break;
                case AlignedDimensionSpec d:
                    d.P1 = T(d.P1);
                    d.P2 = T(d.P2);
                    d.DimLinePoint = T(d.DimLinePoint);
                    break;
                case BlockInsertSpec b:
                    b.Position = T(b.Position);
                    b.Scale *= factor;
                    b.RotationDeg += angleDeg;
                    break;
                case RadialDimensionSpec d:
                    d.Center = T(d.Center);
                    d.ChordPoint = T(d.ChordPoint);
                    d.LeaderLength *= factor;
                    break;
                case AngularDimensionSpec d:
                    d.Center = T(d.Center);
                    d.P1 = T(d.P1);
                    d.P2 = T(d.P2);
                    d.ArcPoint = T(d.ArcPoint);
                    break;
                case OrdinateDimensionSpec d:
                    d.FeaturePoint = T(d.FeaturePoint);
                    d.LeaderEnd = T(d.LeaderEnd);
                    break;
                case MLeaderSpec l:
                    l.ArrowPoints = l.ArrowPoints.Select(T).ToList();
                    l.TextPosition = T(l.TextPosition);
                    l.Height *= factor;
                    l.Width *= factor;
                    l.ArrowSize *= factor;
                    break;
                case TableSpec t:
                    t.Position = T(t.Position);
                    t.ColumnWidths = t.ColumnWidths?.Select(w => w * factor).ToList();
                    t.RowHeight *= factor;
                    t.TextHeight *= factor;
                    break;
            }
        }

        private static EntitySpec Clone(EntitySpec s)
        {
            EntitySpec c;
            switch (s)
            {
                case LineSpec l: c = new LineSpec { Start = l.Start, End = l.End }; break;
                case PolylineSpec p: c = new PolylineSpec { Points = p.Points.ToList(), Bulges = p.Bulges?.ToList(), Closed = p.Closed }; break;
                case CircleSpec ci: c = new CircleSpec { Center = ci.Center, Radius = ci.Radius }; break;
                case ArcSpec a: c = new ArcSpec { Center = a.Center, Radius = a.Radius, StartAngleDeg = a.StartAngleDeg, EndAngleDeg = a.EndAngleDeg }; break;
                case TextSpec t: c = new TextSpec { Position = t.Position, Text = t.Text, Height = t.Height, RotationDeg = t.RotationDeg, Align = t.Align }; break;
                case MTextSpec m: c = new MTextSpec { Position = m.Position, Text = m.Text, Height = m.Height, Width = m.Width, RotationDeg = m.RotationDeg }; break;
                case LinearDimensionSpec d: c = new LinearDimensionSpec { P1 = d.P1, P2 = d.P2, DimLinePoint = d.DimLinePoint, RotationDeg = d.RotationDeg, TextOverride = d.TextOverride }; break;
                case AlignedDimensionSpec d: c = new AlignedDimensionSpec { P1 = d.P1, P2 = d.P2, DimLinePoint = d.DimLinePoint, TextOverride = d.TextOverride }; break;
                case BlockInsertSpec b:
                    c = new BlockInsertSpec
                    {
                        BlockName = b.BlockName,
                        Position = b.Position,
                        Scale = b.Scale,
                        RotationDeg = b.RotationDeg,
                        Attributes = b.Attributes == null ? null : new Dictionary<string, string>(b.Attributes, StringComparer.OrdinalIgnoreCase),
                    };
                    break;
                case HatchSpec h:
                    c = new HatchSpec
                    {
                        Loops = h.Loops.Select(l => (PolylineSpec)Clone(l)).ToList(),
                        Pattern = h.Pattern,
                        Scale = h.Scale,
                        AngleDeg = h.AngleDeg,
                    };
                    break;
                case RadialDimensionSpec d:
                    c = new RadialDimensionSpec { Diameter = d.Diameter, Center = d.Center, ChordPoint = d.ChordPoint, LeaderLength = d.LeaderLength, TextOverride = d.TextOverride };
                    break;
                case AngularDimensionSpec d:
                    c = new AngularDimensionSpec { ArcLength = d.ArcLength, Center = d.Center, P1 = d.P1, P2 = d.P2, ArcPoint = d.ArcPoint, TextOverride = d.TextOverride };
                    break;
                case OrdinateDimensionSpec d:
                    c = new OrdinateDimensionSpec { FeaturePoint = d.FeaturePoint, LeaderEnd = d.LeaderEnd, UseXAxis = d.UseXAxis, Origin = d.Origin, TextOverride = d.TextOverride };
                    break;
                case MLeaderSpec l:
                    c = new MLeaderSpec
                    {
                        ArrowPoints = l.ArrowPoints.ToList(),
                        TextPosition = l.TextPosition,
                        Text = l.Text,
                        Height = l.Height,
                        Width = l.Width,
                        ArrowSize = l.ArrowSize,
                        TextStyle = l.TextStyle,
                    };
                    break;
                case TableSpec t:
                    c = new TableSpec
                    {
                        Position = t.Position,
                        Title = t.Title,
                        Rows = t.Rows.Select(r => r.ToList()).ToList(),
                        Header = t.Header,
                        ColumnWidths = t.ColumnWidths?.ToList(),
                        RowHeight = t.RowHeight,
                        TextHeight = t.TextHeight,
                        TextStyle = t.TextStyle,
                    };
                    break;
                default: throw new NotSupportedException(s.GetType().Name);
            }

            c.Layer = s.Layer;
            c.ColorIndex = s.ColorIndex;
            c.LinetypeScale = s.LinetypeScale;
            c.Style = s.Style;
            return c;
        }
    }
}
