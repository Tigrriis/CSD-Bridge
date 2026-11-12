using System;
using System.Collections.Generic;
using System.Linq;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;

namespace CsdBridge.Core.Vehicles
{
    /// <summary>
    /// The units a drawing is drawn in. The vehicle tools work in metres: vehicle sizes, radii and
    /// clearances are metres whatever the drawing. So everything read from the drawing (picked
    /// points, curves, obstructions, surface levels) is converted to metres on the way in, and
    /// everything drawn (and every coordinate reported) is converted back on the way out.
    /// </summary>
    public sealed class DrawingUnits
    {
        private DrawingUnits(double perMetre, string name)
        {
            PerMetre = perMetre;
            Name = name;
        }

        /// <summary>Drawing units in a metre: 1 in a metre drawing, 1000 in a millimetre drawing.</summary>
        public double PerMetre { get; }

        /// <summary>"metres", "millimetres", ...</summary>
        public string Name { get; }

        public bool IsMetres => Math.Abs(PerMetre - 1) < 1e-9;

        public static DrawingUnits Metres { get; } = new DrawingUnits(1, "metres");

        public static DrawingUnits Millimetres { get; } = new DrawingUnits(1000, "millimetres");

        private static readonly (string Insunits, double PerMetre, string Name)[] Known =
        {
            ("Millimeters", 1000, "millimetres"),
            ("Centimeters", 100, "centimetres"),
            ("Decimeters", 10, "decimetres"),
            ("Meters", 1, "metres"),
            ("Kilometers", 0.001, "kilometres"),
            ("Inches", 1 / 0.0254, "inches"),
            ("Feet", 1 / 0.3048, "feet"),
            ("Yards", 1 / 0.9144, "yards"),
            ("USSurveyFeet", 3937.0 / 1200, "US survey feet"),
        };

        /// <summary>The units for a scale saved with a turning path.</summary>
        public static DrawingUnits Of(double perMetre)
        {
            if (!(perMetre > 0) || double.IsInfinity(perMetre))
            {
                return Metres;
            }

            foreach (var (_, scale, name) in Known)
            {
                if (Math.Abs(scale - perMetre) <= 1e-9 * scale)
                {
                    return scale == 1 ? Metres : scale == 1000 ? Millimetres : new DrawingUnits(scale, name);
                }
            }

            return new DrawingUnits(perMetre, $"{perMetre:0.####} units per metre");
        }

        /// <summary>
        /// The units a drawing says it is in (its INSUNITS setting, by name: "Millimeters",
        /// "Meters", ...). Null when it doesn't say, or says something that isn't a length.
        /// </summary>
        public static DrawingUnits? FromInsunits(string? insunits)
        {
            string text = (insunits ?? "").Trim();
            foreach (var (name, scale, _) in Known)
            {
                if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                {
                    return Of(scale);
                }
            }

            return null;
        }

        /// <summary>"auto", "m" or "mm" from a setting as typed ("Millimetres", "metres", "", ...).</summary>
        public static string Setting(string? text)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "":
                case "auto":
                case "automatic":
                    return "auto";
                case "m":
                case "metre":
                case "metres":
                case "meter":
                case "meters":
                    return "m";
                case "mm":
                case "millimetre":
                case "millimetres":
                case "millimeter":
                case "millimeters":
                    return "mm";
                default:
                    throw new ArgumentException($"Drawing units '{text}' aren't known: use auto, m or mm.");
            }
        }

        /// <summary>
        /// The units to work in: metres or millimetres when the setting says so; otherwise what the
        /// drawing says it is in, or metres when it doesn't say.
        /// </summary>
        public static DrawingUnits Resolve(string? setting, string? insunits)
        {
            switch (Setting(setting))
            {
                case "m":
                    return Metres;
                case "mm":
                    return Millimetres;
                default:
                    return FromInsunits(insunits) ?? Metres;
            }
        }

        /// <summary>
        /// A reason to doubt these units, from how big the drawing is (the larger of its width and
        /// height, in drawing units): a "millimetre" drawing smaller than a car, or a "metre"
        /// drawing the size of a region. Null when the size is believable, or isn't known.
        /// </summary>
        public string? Doubt(double? drawingExtent)
        {
            if (!(drawingExtent > 0) || double.IsInfinity(drawingExtent.Value))
            {
                return null;
            }

            double metres = drawingExtent.Value / PerMetre;
            if (PerMetre > 1 + 1e-9 && metres < 15)
            {
                return $"In {Name} the whole drawing is only {metres:0.##} m across, less than a car needs to turn. If it is drawn in metres, choose Metres.";
            }

            if (PerMetre <= 1 + 1e-9 && metres > 50000)
            {
                return $"In {Name} the drawing is {metres / 1000:0} km across. If it is drawn in millimetres, choose Millimetres.";
            }

            return null;
        }

        // ------------------------------------------------------------ drawing to metres

        public double ToModel(double length) => length / PerMetre;

        public Point2 ToModel(Point2 p) => IsMetres ? p : new Point2(p.X / PerMetre, p.Y / PerMetre);

        public EntitySpec ToModel(EntitySpec spec) => IsMetres ? spec : Scale(spec, 1 / PerMetre);

        public Path2 ToModel(Path2 path) => IsMetres ? path : Scale(path, 1 / PerMetre);

        public IReadOnlyList<Obstruction> ToModel(IReadOnlyList<Obstruction> obstructions) =>
            IsMetres ? obstructions : obstructions.Select(o => new Obstruction(o.Handle, o.Layer, Scale(o.Path, 1 / PerMetre))).ToList();

        /// <summary>A surface sampler that takes points in metres and gives levels in metres.</summary>
        public Func<IReadOnlyList<Point2>, IReadOnlyList<double?>> ToModel(Func<IReadOnlyList<Point2>, IReadOnlyList<double?>> sampler)
        {
            if (IsMetres)
            {
                return sampler;
            }

            return points => sampler(points.Select(ToDrawing).ToList()).Select(z => z is double level ? level / PerMetre : (double?)null).ToList();
        }

        // ------------------------------------------------------------ metres to drawing

        public double ToDrawing(double length) => length * PerMetre;

        public Point2 ToDrawing(Point2 p) => IsMetres ? p : new Point2(p.X * PerMetre, p.Y * PerMetre);

        public EntitySpec ToDrawing(EntitySpec spec) => IsMetres ? spec : Scale(spec, PerMetre);

        public List<DraftedEntity> ToDrawing(IEnumerable<DraftedEntity> drafted) =>
            IsMetres ? drafted.ToList() : drafted.Select(d => new DraftedEntity(d.Group, Scale(d.Spec, PerMetre))).ToList();

        // ------------------------------------------------------------ scaling

        /// <summary>A path scaled about the origin.</summary>
        public static Path2 Scale(Path2 path, double factor)
        {
            var segments = new List<Segment>(path.Segments.Count);
            foreach (Segment s in path.Segments)
            {
                segments.Add(s is ArcSegment arc
                    ? (Segment)new ArcSegment(Times(arc.Center, factor), arc.Radius * factor, arc.StartAngle, arc.Sweep)
                    : new LineSegment(Times(s.Start, factor), Times(s.End, factor)));
            }

            return new Path2(segments, path.Closed);
        }

        /// <summary>
        /// A copy of an entity scaled about the origin: its points and radii, and the sizes that go
        /// with them (text height, hatch pattern scale). Linetype scale is left alone: it is relative
        /// to the drawing's own.
        /// </summary>
        public static EntitySpec Scale(EntitySpec spec, double factor)
        {
            EntitySpec scaled;
            switch (spec)
            {
                case LineSpec l:
                    scaled = new LineSpec { Start = Times(l.Start, factor), End = Times(l.End, factor) };
                    break;
                case PolylineSpec p:
                    scaled = Scale(p, factor);
                    break;
                case CircleSpec c:
                    scaled = new CircleSpec { Center = Times(c.Center, factor), Radius = c.Radius * factor };
                    break;
                case ArcSpec a:
                    scaled = new ArcSpec { Center = Times(a.Center, factor), Radius = a.Radius * factor, StartAngleDeg = a.StartAngleDeg, EndAngleDeg = a.EndAngleDeg };
                    break;
                case TextSpec t:
                    scaled = new TextSpec { Position = Times(t.Position, factor), Text = t.Text, Height = t.Height * factor, RotationDeg = t.RotationDeg, Align = t.Align };
                    break;
                case HatchSpec h:
                    scaled = new HatchSpec
                    {
                        Loops = h.Loops.Select(loop => Scale(loop, factor)).ToList(),
                        BoundaryHandles = h.BoundaryHandles.ToList(),
                        SeedPoints = h.SeedPoints.Select(p => Times(p, factor)).ToList(),
                        Pattern = h.Pattern,
                        Scale = h.Scale * factor,
                        AngleDeg = h.AngleDeg,
                        Associative = h.Associative,
                        SendToBack = h.SendToBack,
                    };
                    break;
                default:
                    throw new NotSupportedException($"A {spec.GetType().Name} can't be converted between drawing units.");
            }

            scaled.Layer = spec.Layer;
            scaled.ColorIndex = spec.ColorIndex;
            scaled.LinetypeScale = spec.LinetypeScale;
            return scaled;
        }

        private static PolylineSpec Scale(PolylineSpec p, double factor) => new PolylineSpec
        {
            Points = p.Points.Select(q => Times(q, factor)).ToList(),
            Bulges = p.Bulges?.ToList(),
            Closed = p.Closed,
            Layer = p.Layer,
            ColorIndex = p.ColorIndex,
            LinetypeScale = p.LinetypeScale,
        };

        private static Point2 Times(Point2 p, double factor) => new Point2(p.X * factor, p.Y * factor);
    }
}
