using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CsdBridge.Core.Drawing;

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
                TextStyles = new List<string> { "Standard" },
                DimStyles = new List<string> { "Standard" },
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
                Entities = all.Take(query.Limit).Select(Summarize).ToList(),
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

                string handle = NewHandle();
                _entities.Add(new StoredEntity(handle, spec));
                results.Add(ItemResult.Success(i, handle, note));
            }

            return results;
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

        public static double Measure(EntitySpec spec)
        {
            switch (spec)
            {
                case LinearDimensionSpec d:
                {
                    double r = d.RotationDeg * Math.PI / 180;
                    return Math.Abs((d.P2.X - d.P1.X) * Math.Cos(r) + (d.P2.Y - d.P1.Y) * Math.Sin(r));
                }

                case AlignedDimensionSpec a:
                    return Math.Sqrt(Math.Pow(a.P2.X - a.P1.X, 2) + Math.Pow(a.P2.Y - a.P1.Y, 2));
                default:
                    return 0;
            }
        }

        private EntitySummary Summarize(StoredEntity e)
        {
            var s = new EntitySummary { Handle = e.Handle, Layer = e.Layer, Color = e.Spec.ColorIndex };
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
                    s.Closed = p.Closed;
                    break;
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
                    s.Kind = "dimension";
                    s.Start = d.P1.ToArray();
                    s.End = d.P2.ToArray();
                    s.Measurement = Math.Round(Measure(d), 4);
                    s.Text = d.TextOverride;
                    break;
                case AlignedDimensionSpec d:
                    s.Kind = "dimension";
                    s.Start = d.P1.ToArray();
                    s.End = d.P2.ToArray();
                    s.Measurement = Math.Round(Measure(d), 4);
                    s.Text = d.TextOverride;
                    break;
                case BlockInsertSpec b:
                    s.Kind = "block_insert";
                    s.Block = b.BlockName;
                    s.Position = b.Position.ToArray();
                    s.Rotation = b.RotationDeg;
                    break;
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
                case BlockInsertSpec b: return new[] { (b.Position, 0.0) };
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
                default: throw new NotSupportedException(s.GetType().Name);
            }

            c.Layer = s.Layer;
            c.ColorIndex = s.ColorIndex;
            return c;
        }
    }
}
