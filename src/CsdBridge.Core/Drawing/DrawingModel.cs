using System;
using System.Collections.Generic;

namespace CsdBridge.Core.Drawing
{
    /// <summary>A 2D point in world coordinates (drawing units).</summary>
    public readonly struct Point2 : IEquatable<Point2>
    {
        public Point2(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }

        public double[] ToArray(int decimals = 3) => new[] { Math.Round(X, decimals), Math.Round(Y, decimals) };

        public bool Equals(Point2 other) => X.Equals(other.X) && Y.Equals(other.Y);
        public override bool Equals(object? obj) => obj is Point2 p && Equals(p);
        public override int GetHashCode() => (X.GetHashCode() * 397) ^ Y.GetHashCode();
        public override string ToString() => $"({X:0.###}, {Y:0.###})";
    }

    public enum EntityKind
    {
        Line,
        Polyline,
        Circle,
        Arc,
        Text,
        MText,
        LinearDimension,
        AlignedDimension,
        BlockInsert,
        Hatch,
    }

    public enum TextAlign
    {
        Left,
        Center,
        Right,
        MiddleCenter,
    }

    /// <summary>
    /// One entity Claude asked to draw. Angles are degrees, counter-clockwise from +X.
    /// All coordinates are world coordinates in drawing units.
    /// </summary>
    public abstract class EntitySpec
    {
        public abstract EntityKind Kind { get; }

        /// <summary>Layer name; null means the current layer. Missing layers are created.</summary>
        public string? Layer { get; set; }

        /// <summary>AutoCAD Color Index 1-255; null means ByLayer.</summary>
        public short? ColorIndex { get; set; }

        /// <summary>Entity linetype scale; null keeps the default (1).</summary>
        public double? LinetypeScale { get; set; }
    }

    public sealed class LineSpec : EntitySpec
    {
        public override EntityKind Kind => EntityKind.Line;
        public Point2 Start { get; set; }
        public Point2 End { get; set; }
    }

    public sealed class PolylineSpec : EntitySpec
    {
        public override EntityKind Kind => EntityKind.Polyline;
        public List<Point2> Points { get; set; } = new List<Point2>();

        /// <summary>Optional bulge per vertex (tan(sweep/4)); same length as Points.</summary>
        public List<double>? Bulges { get; set; }

        public bool Closed { get; set; }
    }

    public sealed class CircleSpec : EntitySpec
    {
        public override EntityKind Kind => EntityKind.Circle;
        public Point2 Center { get; set; }
        public double Radius { get; set; }
    }

    public sealed class ArcSpec : EntitySpec
    {
        public override EntityKind Kind => EntityKind.Arc;
        public Point2 Center { get; set; }
        public double Radius { get; set; }
        public double StartAngleDeg { get; set; }
        public double EndAngleDeg { get; set; }
    }

    public sealed class TextSpec : EntitySpec
    {
        public override EntityKind Kind => EntityKind.Text;
        public Point2 Position { get; set; }
        public string Text { get; set; } = "";

        /// <summary>Null means the drawing's default text height.</summary>
        public double? Height { get; set; }

        public double RotationDeg { get; set; }
        public TextAlign Align { get; set; } = TextAlign.Left;
    }

    public sealed class MTextSpec : EntitySpec
    {
        public override EntityKind Kind => EntityKind.MText;

        /// <summary>Top-left corner of the text box.</summary>
        public Point2 Position { get; set; }

        public string Text { get; set; } = "";
        public double? Height { get; set; }

        /// <summary>Wrap width; null or 0 means no wrapping.</summary>
        public double? Width { get; set; }

        public double RotationDeg { get; set; }
    }

    public sealed class LinearDimensionSpec : EntitySpec
    {
        public override EntityKind Kind => EntityKind.LinearDimension;
        public Point2 P1 { get; set; }
        public Point2 P2 { get; set; }
        public Point2 DimLinePoint { get; set; }

        /// <summary>0 = horizontal dimension, 90 = vertical dimension.</summary>
        public double RotationDeg { get; set; }

        public string? TextOverride { get; set; }
    }

    public sealed class AlignedDimensionSpec : EntitySpec
    {
        public override EntityKind Kind => EntityKind.AlignedDimension;
        public Point2 P1 { get; set; }
        public Point2 P2 { get; set; }
        public Point2 DimLinePoint { get; set; }
        public string? TextOverride { get; set; }
    }

    public sealed class BlockInsertSpec : EntitySpec
    {
        public override EntityKind Kind => EntityKind.BlockInsert;
        public string BlockName { get; set; } = "";
        public Point2 Position { get; set; }
        public double Scale { get; set; } = 1.0;
        public double RotationDeg { get; set; }

        /// <summary>Attribute values by tag (e.g. title block fields). Optional.</summary>
        public Dictionary<string, string>? Attributes { get; set; }
    }

    /// <summary>
    /// A hatch. The boundary comes from one of: explicit loops (first = outer, the rest
    /// islands), existing closed curves by handle, or points inside the areas (the CAD
    /// application traces the boundary from the linework around each point).
    /// </summary>
    public sealed class HatchSpec : EntitySpec
    {
        public override EntityKind Kind => EntityKind.Hatch;
        public List<PolylineSpec> Loops { get; set; } = new List<PolylineSpec>();
        public List<string> BoundaryHandles { get; set; } = new List<string>();
        public List<Point2> SeedPoints { get; set; } = new List<Point2>();
        public string Pattern { get; set; } = "SOLID";
        public double Scale { get; set; } = 1;
        public double AngleDeg { get; set; }

        /// <summary>Keep the hatch linked to its boundary curves (handles only).</summary>
        public bool Associative { get; set; }

        /// <summary>Put the hatch behind other objects (draw order), so it doesn't cover linework.</summary>
        public bool SendToBack { get; set; } = true;
    }

    public sealed class LayerSpec
    {
        public string Name { get; set; } = "";
        public short? ColorIndex { get; set; }
        public string? Linetype { get; set; }

        /// <summary>Lineweight in millimetres, e.g. 0.35. Null keeps the default.</summary>
        public double? LineweightMm { get; set; }

        public string? Description { get; set; }

        /// <summary>True colour (red, green, blue 0-255); takes precedence over ColorIndex.</summary>
        public byte[]? Rgb { get; set; }

        /// <summary>False makes the layer non-plotting.</summary>
        public bool? Plot { get; set; }
    }

    public enum ModifyAction
    {
        Move,
        Copy,
        Rotate,
        Scale,
        Erase,
        SetLayer,
        SetColor,
    }

    public sealed class ModifyOperation
    {
        public ModifyAction Action { get; set; }
        public List<string> Handles { get; set; } = new List<string>();
        public double Dx { get; set; }
        public double Dy { get; set; }

        /// <summary>Number of copies for Copy; copy k is displaced by k*(dx, dy).</summary>
        public int Count { get; set; } = 1;

        public Point2 BasePoint { get; set; }
        public double AngleDeg { get; set; }
        public double Factor { get; set; } = 1.0;
        public string? Layer { get; set; }
        public short? ColorIndex { get; set; }
    }

    public sealed class EntityQuery
    {
        public string? Layer { get; set; }
        public List<EntityKind>? Kinds { get; set; }

        /// <summary>Also match entity types outside <see cref="EntityKind"/> (hatches, splines, ...).</summary>
        public bool IncludeOtherKinds { get; set; }

        public bool HasKindFilter => (Kinds != null && Kinds.Count > 0) || IncludeOtherKinds;
        public List<string>? Handles { get; set; }
        public Point2? WindowMin { get; set; }
        public Point2? WindowMax { get; set; }
        public int Limit { get; set; } = 100;
    }

    // ---------------------------------------------------------------------
    // Results. These are serialised (snake_case) straight into tool_result
    // content, so keep them compact: null fields are omitted.
    // ---------------------------------------------------------------------

    public sealed class LayerInfo
    {
        public string Name { get; set; } = "";
        public short Color { get; set; }
        public string? Linetype { get; set; }
        public double? LineweightMm { get; set; }
        public bool? Off { get; set; }
        public bool? Frozen { get; set; }
        public bool? Locked { get; set; }
    }

    public sealed class DrawingInfo
    {
        public string? FileName { get; set; }
        public string Units { get; set; } = "Unitless";
        public string? CurrentSpace { get; set; }
        public string? CurrentLayer { get; set; }
        public string? CurrentTextStyle { get; set; }
        public string? CurrentDimStyle { get; set; }
        public double? DefaultTextHeight { get; set; }
        public double[]? ExtentsMin { get; set; }
        public double[]? ExtentsMax { get; set; }
        public int EntityCount { get; set; }
        public List<LayerInfo> Layers { get; set; } = new List<LayerInfo>();
        public List<string> Linetypes { get; set; } = new List<string>();
        public List<string> TextStyles { get; set; } = new List<string>();
        public List<string> DimStyles { get; set; } = new List<string>();
        public List<string> Blocks { get; set; } = new List<string>();
    }

    public sealed class EntitySummary
    {
        public string Handle { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Layer { get; set; } = "";
        public short? Color { get; set; }
        public double[]? Start { get; set; }
        public double[]? End { get; set; }
        public double[]? Center { get; set; }
        public double? Radius { get; set; }
        public double? StartAngle { get; set; }
        public double? EndAngle { get; set; }
        public double[][]? Points { get; set; }

        /// <summary>Polyline arc bulges, one per vertex (only when the polyline has arcs).</summary>
        public double[]? Bulges { get; set; }

        public bool? Closed { get; set; }

        /// <summary>Hatch pattern name.</summary>
        public string? Pattern { get; set; }

        /// <summary>Hatch area.</summary>
        public double? Area { get; set; }
        public double[]? Position { get; set; }
        public string? Text { get; set; }
        public double? Height { get; set; }
        public double? Rotation { get; set; }
        public double? Measurement { get; set; }
        public string? Block { get; set; }
        public double[]? BoundsMin { get; set; }
        public double[]? BoundsMax { get; set; }
    }

    public sealed class EntityListResult
    {
        public List<EntitySummary> Entities { get; set; } = new List<EntitySummary>();
        public int TotalMatched { get; set; }
        public bool Truncated { get; set; }
    }

    public sealed class ItemResult
    {
        public int Index { get; set; }
        public bool Ok { get; set; }

        /// <summary>Handle of the entity that was created or changed.</summary>
        public string? Handle { get; set; }

        /// <summary>Handles created by a copy operation.</summary>
        public List<string>? NewHandles { get; set; }

        public string? Error { get; set; }
        public string? Note { get; set; }

        public static ItemResult Success(int index, string? handle = null, string? note = null) =>
            new ItemResult { Index = index, Ok = true, Handle = handle, Note = note };

        public static ItemResult Failure(int index, string error) =>
            new ItemResult { Index = index, Ok = false, Error = error };
    }

    public sealed class PickPointResult
    {
        public bool Cancelled { get; set; }
        public double[]? Point { get; set; }
    }

    public sealed class SelectionResult
    {
        public bool Cancelled { get; set; }
        public List<EntitySummary> Entities { get; set; } = new List<EntitySummary>();
    }
}
