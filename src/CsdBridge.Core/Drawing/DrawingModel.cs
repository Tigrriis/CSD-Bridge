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
        RadiusDimension,
        DiameterDimension,
        AngularDimension,
        ArcLengthDimension,
        OrdinateDimension,
        MLeader,
        Table,
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

        /// <summary>
        /// The named style for this kind of object: a text style for text and mtext, a dimension
        /// style for dimensions, a multileader style for multileaders, a table style for tables.
        /// Null means the drawing's current style. The style must exist in the drawing.
        /// </summary>
        public string? Style { get; set; }
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

    /// <summary>What every dimension has.</summary>
    public abstract class DimensionSpec : EntitySpec
    {
        /// <summary>Text shown instead of the measured value; null shows the measurement.</summary>
        public string? TextOverride { get; set; }
    }

    public sealed class LinearDimensionSpec : DimensionSpec
    {
        public override EntityKind Kind => EntityKind.LinearDimension;
        public Point2 P1 { get; set; }
        public Point2 P2 { get; set; }
        public Point2 DimLinePoint { get; set; }

        /// <summary>0 = horizontal dimension, 90 = vertical dimension.</summary>
        public double RotationDeg { get; set; }
    }

    public sealed class AlignedDimensionSpec : DimensionSpec
    {
        public override EntityKind Kind => EntityKind.AlignedDimension;
        public Point2 P1 { get; set; }
        public Point2 P2 { get; set; }
        public Point2 DimLinePoint { get; set; }
    }

    /// <summary>A radius or diameter dimension on a circle or arc.</summary>
    public sealed class RadialDimensionSpec : DimensionSpec
    {
        public override EntityKind Kind => Diameter ? EntityKind.DiameterDimension : EntityKind.RadiusDimension;
        public bool Diameter { get; set; }
        public Point2 Center { get; set; }

        /// <summary>The point on the curve the dimension points at.</summary>
        public Point2 ChordPoint { get; set; }

        /// <summary>Length of the leader beyond the curve; null uses a default from the text size.</summary>
        public double? LeaderLength { get; set; }

        public double Radius => Math.Sqrt(Math.Pow(ChordPoint.X - Center.X, 2) + Math.Pow(ChordPoint.Y - Center.Y, 2));
    }

    /// <summary>
    /// An angle between two legs from a vertex, or (ArcLength) the length of the arc from
    /// P1 to P2 about Center. ArcPoint is a point the dimension arc passes through; it also
    /// picks which of the two angles between the legs is measured.
    /// </summary>
    public sealed class AngularDimensionSpec : DimensionSpec
    {
        public override EntityKind Kind => ArcLength ? EntityKind.ArcLengthDimension : EntityKind.AngularDimension;
        public bool ArcLength { get; set; }
        public Point2 Center { get; set; }
        public Point2 P1 { get; set; }
        public Point2 P2 { get; set; }
        public Point2 ArcPoint { get; set; }
    }

    /// <summary>The X or Y coordinate of a point, measured from an origin, on a leader.</summary>
    public sealed class OrdinateDimensionSpec : DimensionSpec
    {
        public override EntityKind Kind => EntityKind.OrdinateDimension;
        public Point2 FeaturePoint { get; set; }
        public Point2 LeaderEnd { get; set; }

        /// <summary>True measures the X coordinate, false the Y coordinate.</summary>
        public bool UseXAxis { get; set; }

        /// <summary>The datum the coordinate is measured from; null means the world origin.</summary>
        public Point2? Origin { get; set; }
    }

    /// <summary>A multileader: text with one or more arrowed leader lines.</summary>
    public sealed class MLeaderSpec : EntitySpec
    {
        public override EntityKind Kind => EntityKind.MLeader;

        /// <summary>Where each leader's arrow points (at least one).</summary>
        public List<Point2> ArrowPoints { get; set; } = new List<Point2>();

        /// <summary>Where the leaders land and the text starts.</summary>
        public Point2 TextPosition { get; set; }

        public string Text { get; set; } = "";

        /// <summary>Text height; null uses the multileader style's.</summary>
        public double? Height { get; set; }

        /// <summary>Wrap width for the text; null or 0 means no wrapping.</summary>
        public double? Width { get; set; }

        /// <summary>Arrowhead size; null uses the multileader style's.</summary>
        public double? ArrowSize { get; set; }

        /// <summary>Text style for the text; null uses the multileader style's.</summary>
        public string? TextStyle { get; set; }
    }

    /// <summary>A table of text cells. Position is the top-left corner; rows run downwards.</summary>
    public sealed class TableSpec : EntitySpec
    {
        public override EntityKind Kind => EntityKind.Table;
        public Point2 Position { get; set; }

        /// <summary>Optional title in a merged row above the others.</summary>
        public string? Title { get; set; }

        /// <summary>Cell text, row by row (every row has the same number of cells).</summary>
        public List<List<string>> Rows { get; set; } = new List<List<string>>();

        /// <summary>True when the first row holds the column headings.</summary>
        public bool Header { get; set; } = true;

        /// <summary>One width per column; null sizes columns from their text.</summary>
        public List<double>? ColumnWidths { get; set; }

        /// <summary>Height of every row; null uses twice the text height.</summary>
        public double? RowHeight { get; set; }

        /// <summary>Cell text height; null uses the drawing's default text height.</summary>
        public double? TextHeight { get; set; }

        /// <summary>Text style for the cells; null uses the table style's.</summary>
        public string? TextStyle { get; set; }

        public int ColumnCount => Rows.Count == 0 ? 0 : Rows[0].Count;
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

    /// <summary>
    /// A change to the content of an existing annotation object. Only the fields that are set
    /// are changed; a field that doesn't apply to the object's kind fails the whole edit.
    /// </summary>
    public sealed class AnnotationEdit
    {
        public string Handle { get; set; } = "";

        /// <summary>New text of a text, mtext or multileader.</summary>
        public string? Text { get; set; }

        /// <summary>Attribute values of a block insert, by tag.</summary>
        public Dictionary<string, string>? Attributes { get; set; }

        /// <summary>Dimension text override; an empty string restores the measured value.</summary>
        public string? TextOverride { get; set; }

        public double? Height { get; set; }

        /// <summary>Wrap width of an mtext or multileader.</summary>
        public double? Width { get; set; }

        public double? RotationDeg { get; set; }

        /// <summary>New position of a text or mtext, or of a multileader's text.</summary>
        public Point2? Position { get; set; }

        /// <summary>New arrow points of a multileader (replaces its leaders).</summary>
        public List<Point2>? ArrowPoints { get; set; }

        /// <summary>The object's style, as in <see cref="EntitySpec.Style"/>.</summary>
        public string? Style { get; set; }

        /// <summary>Table cells to change.</summary>
        public List<TableCellEdit>? Cells { get; set; }

        public bool HasChanges =>
            Text != null || Attributes != null || TextOverride != null || Height != null || Width != null
            || RotationDeg != null || Position != null || ArrowPoints != null || Style != null || Cells != null;
    }

    /// <summary>Which changes fit which kind of object; shared by every host so they answer alike.</summary>
    public static class AnnotationEdits
    {
        /// <summary>
        /// Why this edit can't be made to an object of this kind (text, mtext, mleader,
        /// dimension, block_insert, table or anything else), or null when it can.
        /// </summary>
        public static string? Problem(AnnotationEdit e, string kind)
        {
            bool text = kind == "text", mtext = kind == "mtext", leader = kind == "mleader";
            bool dimension = kind == "dimension", block = kind == "block_insert", table = kind == "table";
            if (!(text || mtext || leader || dimension || block || table))
            {
                return $"a {kind} isn't annotation that can be edited here (text, mtext, mleader, dimension, block insert or table)";
            }

            if (e.Text != null && !(text || mtext || leader))
            {
                return block ? "a block insert's text is in its attributes: use attributes {TAG: value}"
                    : dimension ? "a dimension's text is set with text_override"
                    : "a table's text is set with cells";
            }

            if (e.Text != null && text && e.Text.IndexOf('\n') >= 0)
            {
                return "single-line text can't hold a line break; redraw it as mtext";
            }

            if (e.Attributes != null && !block)
            {
                return $"attributes only apply to block inserts, not a {kind}";
            }

            if (e.TextOverride != null && !dimension)
            {
                return $"text_override only applies to dimensions, not a {kind}";
            }

            if (e.Cells != null && !table)
            {
                return $"cells only apply to tables, not a {kind}";
            }

            if (e.Height != null && !(text || mtext || leader))
            {
                return $"height doesn't apply to a {kind}";
            }

            if (e.Width != null && !(mtext || leader))
            {
                return $"width (text wrapping) only applies to mtext and multileaders, not a {kind}";
            }

            if (e.RotationDeg != null && !(text || mtext))
            {
                return $"rotation here only applies to text and mtext; rotate a {kind} with modify_entities";
            }

            if (e.Position != null && !(text || mtext || leader))
            {
                return $"position here only applies to text, mtext and multileader text; move a {kind} with modify_entities";
            }

            if (e.ArrowPoints != null && !leader)
            {
                return $"arrow_points only apply to multileaders, not a {kind}";
            }

            if (e.Style != null && block)
            {
                return "a block insert has no style";
            }

            return null;
        }
    }

    public sealed class TableCellEdit
    {
        /// <summary>Row from 0, counting the title row if the table has one.</summary>
        public int Row { get; set; }

        public int Column { get; set; }
        public string Text { get; set; } = "";
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

        /// <summary>
        /// Also fill the fields used to draw a picture of the entities and to find clashing
        /// labels: <see cref="EntitySummary.TextBox"/>, and vertices of 2D and 3D polylines.
        /// </summary>
        public bool ForView { get; set; }

        public static bool IsDimension(EntityKind kind) =>
            kind == EntityKind.LinearDimension || kind == EntityKind.AlignedDimension || kind == EntityKind.RadiusDimension
            || kind == EntityKind.DiameterDimension || kind == EntityKind.AngularDimension || kind == EntityKind.ArcLengthDimension
            || kind == EntityKind.OrdinateDimension;
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

        public string? CurrentMLeaderStyle { get; set; }
        public string? CurrentTableStyle { get; set; }
        public List<string> MLeaderStyles { get; set; } = new List<string>();
        public List<string> TableStyles { get; set; } = new List<string>();

        /// <summary>
        /// What each text, dimension and multileader style looks like, when the host knows
        /// (<see cref="TextStyles"/> etc. hold just the names).
        /// </summary>
        public List<StyleInfo>? TextStyleDetails { get; set; }
        public List<StyleInfo>? DimStyleDetails { get; set; }
        public List<StyleInfo>? MLeaderStyleDetails { get; set; }
    }

    /// <summary>The settings of a style that decide how big its text and arrows come out.</summary>
    public sealed class StyleInfo
    {
        public string Name { get; set; } = "";

        /// <summary>Text styles: the font (file or typeface).</summary>
        public string? Font { get; set; }

        /// <summary>Text height; 0 or null means the height is set per object.</summary>
        public double? TextHeight { get; set; }

        /// <summary>Dimension and multileader styles: overall scale (sizes are multiplied by it).</summary>
        public double? Scale { get; set; }

        public double? ArrowSize { get; set; }

        /// <summary>Dimension and multileader styles: the text style they use.</summary>
        public string? TextStyle { get; set; }

        public bool? Annotative { get; set; }
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

        /// <summary>Block inserts: uniform scale, when it isn't 1.</summary>
        public double? Scale { get; set; }

        /// <summary>Block inserts: attribute values by tag.</summary>
        public Dictionary<string, string>? Attributes { get; set; }

        /// <summary>Text, dimension, multileader or table style name.</summary>
        public string? Style { get; set; }

        /// <summary>Dimensions: linear, aligned, radius, diameter, angular, arc_length or ordinate.</summary>
        public string? DimType { get; set; }

        /// <summary>Dimensions: a point on the dimension line (or arc).</summary>
        public double[]? DimLinePoint { get; set; }

        /// <summary>Multileaders: where each arrow points.</summary>
        public double[][]? ArrowPoints { get; set; }

        /// <summary>Tables: cell text row by row (the first rows only, on large tables).</summary>
        public string[][]? Rows { get; set; }

        /// <summary>Tables: number of rows and columns.</summary>
        public int? RowCount { get; set; }
        public int? ColumnCount { get; set; }

        /// <summary>Tables: width of each column.</summary>
        public double[]? ColumnWidths { get; set; }

        public double[]? BoundsMin { get; set; }
        public double[]? BoundsMax { get; set; }

        /// <summary>
        /// The four corners of the box around the object's text, turned with the text. Only
        /// filled for <see cref="EntityQuery.ForView"/>; not sent to Claude.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public double[][]? TextBox { get; set; }
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
