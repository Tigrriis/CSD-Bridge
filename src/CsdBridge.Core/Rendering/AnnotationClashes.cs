using System;
using System.Collections.Generic;
using System.Linq;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;

namespace CsdBridge.Core.Rendering
{
    /// <summary>Two pieces of annotation whose text boxes overlap.</summary>
    public sealed class Overlap
    {
        public string A { get; set; } = "";
        public string B { get; set; } = "";
        public string KindA { get; set; } = "";
        public string KindB { get; set; } = "";
        public string? TextA { get; set; }
        public string? TextB { get; set; }

        /// <summary>Roughly where they overlap.</summary>
        public Point2 At { get; set; }
    }

    /// <summary>
    /// Finds annotation that sits on top of other annotation: text, dimension text,
    /// multileader text and tables. Boxes are compared turned as the text is turned, so
    /// labels along a road don't look like clashes just because their upright bounding
    /// boxes cross.
    /// </summary>
    public static class AnnotationClashes
    {
        /// <summary>Boxes must overlap by more than this fraction of the smaller text's height to count.</summary>
        private const double Tolerance = 0.1;

        private sealed class Item
        {
            public EntitySummary Entity = null!;
            public Point2[] Box = null!;
            public double MinX, MaxX, MinY, MaxY, ShortSide;
        }

        /// <summary>
        /// The box around an entity's text, turned with it (bottom-left, bottom-right,
        /// top-right, top-left), or null for entities that aren't annotation.
        /// </summary>
        public static Point2[]? BoxOf(EntitySummary e)
        {
            if (e.TextBox != null && e.TextBox.Length == 4)
            {
                return e.TextBox.Select(p => new Point2(p[0], p[1])).ToArray();
            }

            bool annotation = e.Kind == "text" || e.Kind == "mtext" || e.Kind == "mleader" || e.Kind == "table";
            if (!annotation)
            {
                return null;
            }

            if (e.Kind != "table" && e.Kind != "mleader" && e.Position != null && e.Height is double h && h > 0 && !string.IsNullOrEmpty(e.Text))
            {
                // No measured box: estimate one from the text itself.
                List<string> lines = AnnotationGeometry.Lines(e.Text!, h, null);
                (double w, double height) = AnnotationGeometry.Size(lines, h);
                return AnnotationGeometry.Box(new Point2(e.Position[0], e.Position[1]), w, height, e.Rotation ?? 0, 0, e.Kind == "mtext" ? 1 : 0);
            }

            if (e.Kind == "mleader" || e.BoundsMin == null || e.BoundsMax == null)
            {
                return null; // a multileader's bounds take in its leaders, so they say nothing about its text
            }

            return new[]
            {
                new Point2(e.BoundsMin[0], e.BoundsMin[1]), new Point2(e.BoundsMax[0], e.BoundsMin[1]),
                new Point2(e.BoundsMax[0], e.BoundsMax[1]), new Point2(e.BoundsMin[0], e.BoundsMax[1]),
            };
        }

        public static List<Overlap> Find(IReadOnlyList<EntitySummary> entities)
        {
            var items = new List<Item>();
            foreach (EntitySummary e in entities)
            {
                Point2[]? box = BoxOf(e);
                if (box == null)
                {
                    continue;
                }

                double w = Segment.Dist(box[0], box[1]), h = Segment.Dist(box[0], box[3]);
                if (w <= 0 || h <= 0)
                {
                    continue;
                }

                items.Add(new Item
                {
                    Entity = e,
                    Box = box,
                    MinX = box.Min(p => p.X),
                    MaxX = box.Max(p => p.X),
                    MinY = box.Min(p => p.Y),
                    MaxY = box.Max(p => p.Y),
                    ShortSide = Math.Min(w, h),
                });
            }

            // Sweep along X so only boxes that could touch are compared.
            items.Sort((a, b) => a.MinX.CompareTo(b.MinX));
            var result = new List<Overlap>();
            for (int i = 0; i < items.Count; i++)
            {
                Item a = items[i];
                for (int j = i + 1; j < items.Count && items[j].MinX <= a.MaxX; j++)
                {
                    Item b = items[j];
                    if (b.MinY > a.MaxY || b.MaxY < a.MinY)
                    {
                        continue;
                    }

                    if (Penetration(a.Box, b.Box) > Tolerance * Math.Min(a.ShortSide, b.ShortSide))
                    {
                        result.Add(new Overlap
                        {
                            A = a.Entity.Handle,
                            B = b.Entity.Handle,
                            KindA = a.Entity.Kind,
                            KindB = b.Entity.Kind,
                            TextA = Shorten(a.Entity),
                            TextB = Shorten(b.Entity),
                            At = new Point2(
                                (Math.Max(a.MinX, b.MinX) + Math.Min(a.MaxX, b.MaxX)) / 2,
                                (Math.Max(a.MinY, b.MinY) + Math.Min(a.MaxY, b.MaxY)) / 2),
                        });
                    }
                }
            }

            return result;
        }

        /// <summary>How far two convex boxes overlap (separating axis test); 0 or less when they don't.</summary>
        internal static double Penetration(Point2[] a, Point2[] b)
        {
            double least = double.MaxValue;
            foreach (Point2[] box in new[] { a, b })
            {
                for (int i = 0; i < 2; i++)
                {
                    Point2 p = box[i], q = box[i + 1];
                    double ex = q.X - p.X, ey = q.Y - p.Y, length = Math.Sqrt(ex * ex + ey * ey);
                    if (length < 1e-12)
                    {
                        continue;
                    }

                    double nx = -ey / length, ny = ex / length;
                    (double minA, double maxA) = Project(a, nx, ny);
                    (double minB, double maxB) = Project(b, nx, ny);
                    double overlap = Math.Min(maxA, maxB) - Math.Max(minA, minB);
                    if (overlap <= 0)
                    {
                        return 0;
                    }

                    least = Math.Min(least, overlap);
                }
            }

            return least == double.MaxValue ? 0 : least;
        }

        private static (double Min, double Max) Project(Point2[] box, double nx, double ny)
        {
            double min = double.MaxValue, max = double.MinValue;
            foreach (Point2 p in box)
            {
                double d = p.X * nx + p.Y * ny;
                min = Math.Min(min, d);
                max = Math.Max(max, d);
            }

            return (min, max);
        }

        private static string? Shorten(EntitySummary e)
        {
            string? text = e.Text;
            if (string.IsNullOrEmpty(text) && e.Measurement is double m)
            {
                text = m.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            }

            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            text = text!.Replace("\r", "").Replace("\n", " / ");
            return text.Length > 40 ? text.Substring(0, 40) + "..." : text;
        }
    }
}
