using System;
using System.Collections.Generic;
using System.Globalization;
using CsdBridge.Core.Drawing;

namespace CsdBridge.Core.Geometry
{
    /// <summary>
    /// Turns a points_on_curve request into exact distances along a curve of known
    /// length. Shared by every host so "divide", "spacing" and "distances" mean the same
    /// thing in BricsCAD and offline.
    /// </summary>
    public static class SetOut
    {
        public const int MaxPoints = 1000;
        private const double Tol = 1e-9;

        /// <summary>Distances from the start of the curve. Throws ArgumentException with a readable message.</summary>
        public static List<double> Distances(PointsRequest request, double length, bool closed)
        {
            var result = new List<double>();
            if (request.Divide is int parts)
            {
                // Closed curves: the end is the start, so don't repeat it.
                int last = closed ? parts - 1 : parts;
                for (int k = 0; k <= last; k++)
                {
                    result.Add(length * k / parts);
                }
            }
            else if (request.Spacing is double spacing)
            {
                if (request.StartOffset < -Tol || request.StartOffset > length + Tol)
                {
                    throw new ArgumentException($"start_offset {F(request.StartOffset)} is outside the curve (length {F(length)})");
                }

                double count = Math.Floor((length - request.StartOffset) / spacing + 1e-9) + 1;
                if (count > MaxPoints)
                {
                    throw new ArgumentException($"that spacing gives {count:0} points; the limit is {MaxPoints}");
                }

                for (int k = 0; k < count; k++)
                {
                    result.Add(Math.Min(length, request.StartOffset + k * spacing));
                }
            }
            else if (request.Distances != null)
            {
                foreach (double d in request.Distances)
                {
                    if (d < -Tol || d > length + Tol)
                    {
                        throw new ArgumentException($"distance {F(d)} is beyond the curve (length {F(length)})");
                    }

                    result.Add(Math.Max(0, Math.Min(length, d)));
                }
            }

            if (request.FromEnd)
            {
                for (int i = 0; i < result.Count; i++)
                {
                    result[i] = length - result[i];
                }
            }

            return result;
        }

        private static string F(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
