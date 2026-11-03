using System.Collections.Generic;

namespace CsdBridge.Core.Drawing
{
    // Requests and results for the exact-geometry tools (offset, intersect, measure,
    // points along a curve). The CAD host does the maths; Claude only decides what to ask.

    public enum OffsetSide
    {
        /// <summary>Towards <see cref="OffsetOperation.SidePoint"/>.</summary>
        TowardsPoint,
        Inside,
        Outside,
    }

    public sealed class OffsetOperation
    {
        public List<string> Handles { get; set; } = new List<string>();
        public double Distance { get; set; }

        /// <summary>Number of offsets: copy k is at k × Distance.</summary>
        public int Count { get; set; } = 1;

        public OffsetSide Side { get; set; }
        public Point2 SidePoint { get; set; }

        /// <summary>Layer for the new curves; null keeps the source entity's layer.</summary>
        public string? Layer { get; set; }
    }

    public sealed class IntersectQuery
    {
        public List<string> Handles { get; set; } = new List<string>();

        /// <summary>If set, intersect each of Handles with each of these; otherwise all pairs within Handles.</summary>
        public List<string>? With { get; set; }

        /// <summary>Treat lines as infinite and arcs as full circles.</summary>
        public bool Extend { get; set; }
    }

    public sealed class IntersectionPair
    {
        public string A { get; set; } = "";
        public string B { get; set; } = "";
        public List<double[]> Points { get; set; } = new List<double[]>();
    }

    public sealed class IntersectionResult
    {
        /// <summary>Only pairs that intersect.</summary>
        public List<IntersectionPair> Intersections { get; set; } = new List<IntersectionPair>();

        public int PairsChecked { get; set; }

        /// <summary>Handles that could not be used, with the reason.</summary>
        public List<string>? Skipped { get; set; }
    }

    public sealed class MeasureQuery
    {
        public List<string> Handles { get; set; } = new List<string>();

        /// <summary>If set, report each entity's closest point to this point and the distance.</summary>
        public Point2? Point { get; set; }
    }

    public sealed class EntityMeasurement
    {
        public string Handle { get; set; } = "";
        public string? Kind { get; set; }
        public double? Length { get; set; }
        public double? Area { get; set; }
        public bool? Closed { get; set; }
        public double[]? Start { get; set; }
        public double[]? End { get; set; }

        /// <summary>Point halfway along the curve.</summary>
        public double[]? Mid { get; set; }

        public double[]? ClosestPoint { get; set; }
        public double? DistanceToPoint { get; set; }

        /// <summary>For dimensions: the measured value.</summary>
        public double? Measurement { get; set; }

        public double[]? BoundsMin { get; set; }
        public double[]? BoundsMax { get; set; }
        public string? Error { get; set; }
    }

    public sealed class PointToPoint
    {
        public double Distance { get; set; }
        public double Dx { get; set; }
        public double Dy { get; set; }

        /// <summary>Degrees counter-clockwise from +X, from the first point to the second.</summary>
        public double Angle { get; set; }
    }

    public sealed class MeasureResult
    {
        public List<EntityMeasurement> Entities { get; set; } = new List<EntityMeasurement>();
        public PointToPoint? Between { get; set; }
    }

    public sealed class PointsRequest
    {
        public string Handle { get; set; } = "";

        /// <summary>Divide into this many equal parts.</summary>
        public int? Divide { get; set; }

        /// <summary>A point every Spacing along the curve, starting at StartOffset.</summary>
        public double? Spacing { get; set; }

        public double StartOffset { get; set; }

        /// <summary>Explicit distances along the curve.</summary>
        public List<double>? Distances { get; set; }

        /// <summary>Measure from the end of the curve instead of the start.</summary>
        public bool FromEnd { get; set; }
    }

    public sealed class PointOnCurve
    {
        public double[] Point { get; set; } = new double[2];
        public double Distance { get; set; }

        /// <summary>Direction of the curve at the point, degrees.</summary>
        public double Angle { get; set; }
    }

    public sealed class CurvePoints
    {
        public int Index { get; set; }
        public string Handle { get; set; } = "";
        public bool Ok { get; set; }
        public double? Length { get; set; }
        public List<PointOnCurve>? Points { get; set; }
        public string? Error { get; set; }
    }
}
