using System;
using System.Collections.Generic;
using System.Linq;
using Bricscad.ApplicationServices;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;
using CsdBridge.Core.Vehicles;
using Teigha.DatabaseServices;
using Teigha.Geometry;

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// Finds the linework a vehicle must not hit: lines, arcs, circles and polylines on the
    /// obstruction layers, in the current space and inside blocks and xrefs (their entities are
    /// brought into world coordinates). Frozen and off layers are ignored, as is anything the
    /// turning path tools draw themselves.
    /// </summary>
    internal static class ObstructionScanner
    {
        private const int MaxCurves = 20000;
        private const int MaxDepth = 4;

        public static IReadOnlyList<Obstruction> Scan(Document doc, IReadOnlyList<string> patterns, Point2 min, Point2 max, IReadOnlyCollection<string> exclude)
        {
            var found = new List<Obstruction>();
            if (patterns.Count == 0)
            {
                return found;
            }

            Database db = doc.Database;
            var scan = new State(patterns, min, max, exclude, found);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                Collect(tr, space, Matrix3d.Identity, null, 0, scan);
                tr.Commit();
            }

            return found;
        }

        /// <summary>The layers of the selected objects, for "pick the obstruction layers".</summary>
        public static List<string> LayersOf(Document doc, IEnumerable<ObjectId> ids)
        {
            var layers = new List<string>();
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is Entity ent && !layers.Contains(ent.Layer, StringComparer.OrdinalIgnoreCase))
                    {
                        layers.Add(ent.Layer);
                    }
                }

                tr.Commit();
            }

            return layers;
        }

        private sealed class State
        {
            public State(IReadOnlyList<string> patterns, Point2 min, Point2 max, IReadOnlyCollection<string> exclude, List<Obstruction> found)
            {
                Patterns = patterns;
                Min = min;
                Max = max;
                Exclude = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
                Found = found;
            }

            public IReadOnlyList<string> Patterns { get; }
            public Point2 Min { get; }
            public Point2 Max { get; }
            public HashSet<string> Exclude { get; }
            public List<Obstruction> Found { get; }
            public Dictionary<ObjectId, bool> HiddenLayers { get; } = new Dictionary<ObjectId, bool>();
            public Dictionary<string, bool> Wanted { get; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        private static void Collect(Transaction tr, BlockTableRecord owner, Matrix3d toWorld, string? ownerLayer, int depth, State s)
        {
            foreach (ObjectId id in owner)
            {
                if (s.Found.Count >= MaxCurves)
                {
                    return;
                }

                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent))
                {
                    continue;
                }

                try
                {
                    if (ent is BlockReference br)
                    {
                        if (depth >= MaxDepth || s.Exclude.Contains(br.Layer) || Hidden(tr, br, s) || (depth == 0 && !NearWindow(br, s)))
                        {
                            continue;
                        }

                        var definition = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                        Collect(tr, definition, toWorld * br.BlockTransform, br.Layer, depth + 1, s);
                        continue;
                    }

                    // Entities on layer 0 inside a block take the layer of the block reference.
                    string layer = ent.Layer;
                    if (ownerLayer != null && string.Equals(layer, "0", StringComparison.Ordinal))
                    {
                        layer = ownerLayer;
                    }

                    if (s.Exclude.Contains(layer) || !Wanted(layer, s) || Hidden(tr, ent, s))
                    {
                        continue;
                    }

                    EntitySpec? spec = BricsCadDrawingHost.CurveSpec(ent);
                    if (spec != null && depth > 0)
                    {
                        spec = ToWorld(spec, toWorld);
                    }

                    Path2? path = spec == null ? null : Path2.From(spec);
                    if (path != null && Overlaps(path, s))
                    {
                        s.Found.Add(new Obstruction(ent.Handle.ToString(), layer, path));
                    }
                }
                catch (System.Exception)
                {
                    // An entity that can't be read or transformed (a non-uniformly scaled arc, say) is skipped.
                }
            }
        }

        /// <summary>
        /// A curve from inside a block, moved into world coordinates by the block's transform
        /// (position, rotation, uniform scale, mirroring). Arcs under a non-uniform scale would
        /// become ellipses and are left out.
        /// </summary>
        private static EntitySpec? ToWorld(EntitySpec spec, Matrix3d m)
        {
            Point2 T(Point2 p)
            {
                Point3d q = new Point3d(p.X, p.Y, 0).TransformBy(m);
                return new Point2(q.X, q.Y);
            }

            Point2 o = T(new Point2(0, 0)), ex = T(new Point2(1, 0)), ey = T(new Point2(0, 1));
            double ax = ex.X - o.X, ay = ex.Y - o.Y, bx = ey.X - o.X, by = ey.Y - o.Y;
            double sx = Math.Sqrt(ax * ax + ay * ay), sy = Math.Sqrt(bx * bx + by * by);
            bool uniform = Math.Abs(sx - sy) <= 1e-6 * Math.Max(sx, sy);
            bool mirrored = ax * by - ay * bx < 0;

            switch (spec)
            {
                case LineSpec l:
                    return new LineSpec { Start = T(l.Start), End = T(l.End) };

                case PolylineSpec p:
                {
                    bool arcs = p.Bulges != null && p.Bulges.Any(b => Math.Abs(b) > 1e-12);
                    if (arcs && !uniform)
                    {
                        return null;
                    }

                    return new PolylineSpec
                    {
                        Points = p.Points.Select(T).ToList(),
                        Bulges = arcs ? p.Bulges!.Select(b => mirrored ? -b : b).ToList() : null,
                        Closed = p.Closed,
                    };
                }

                case CircleSpec c:
                    return uniform ? new CircleSpec { Center = T(c.Center), Radius = c.Radius * sx } : null;

                case ArcSpec a:
                {
                    if (!uniform)
                    {
                        return null;
                    }

                    double a0 = a.StartAngleDeg * Math.PI / 180, a1 = a.EndAngleDeg * Math.PI / 180;
                    Point2 centre = T(a.Center);
                    Point2 start = T(new Point2(a.Center.X + a.Radius * Math.Cos(a0), a.Center.Y + a.Radius * Math.Sin(a0)));
                    Point2 end = T(new Point2(a.Center.X + a.Radius * Math.Cos(a1), a.Center.Y + a.Radius * Math.Sin(a1)));
                    double s0 = Math.Atan2(start.Y - centre.Y, start.X - centre.X) * 180 / Math.PI;
                    double s1 = Math.Atan2(end.Y - centre.Y, end.X - centre.X) * 180 / Math.PI;
                    // Arcs run anticlockwise from start to end; mirroring reverses that, so swap the ends.
                    return new ArcSpec { Center = centre, Radius = a.Radius * sx, StartAngleDeg = mirrored ? s1 : s0, EndAngleDeg = mirrored ? s0 : s1 };
                }

                default:
                    return null;
            }
        }

        private static bool Wanted(string layer, State s)
        {
            if (!s.Wanted.TryGetValue(layer, out bool wanted))
            {
                wanted = LayerPattern.MatchesAny(s.Patterns, layer);
                s.Wanted[layer] = wanted;
            }

            return wanted;
        }

        private static bool Hidden(Transaction tr, Entity ent, State s)
        {
            ObjectId layerId = ent.LayerId;
            if (!s.HiddenLayers.TryGetValue(layerId, out bool hidden))
            {
                hidden = tr.GetObject(layerId, OpenMode.ForRead) is LayerTableRecord ltr && (ltr.IsFrozen || ltr.IsOff);
                s.HiddenLayers[layerId] = hidden;
            }

            return hidden;
        }

        private static bool NearWindow(Entity ent, State s)
        {
            try
            {
                Extents3d e = ent.GeometricExtents;
                return e.MaxPoint.X >= s.Min.X && e.MinPoint.X <= s.Max.X && e.MaxPoint.Y >= s.Min.Y && e.MinPoint.Y <= s.Max.Y;
            }
            catch (System.Exception)
            {
                return true; // no extents: look inside to be sure
            }
        }

        private static bool Overlaps(Path2 path, State s)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            void Grow(double x, double y)
            {
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            foreach (Segment seg in path.Segments)
            {
                if (seg is ArcSegment arc)
                {
                    Grow(arc.Center.X - arc.Radius, arc.Center.Y - arc.Radius);
                    Grow(arc.Center.X + arc.Radius, arc.Center.Y + arc.Radius);
                }
                else
                {
                    Grow(seg.Start.X, seg.Start.Y);
                    Grow(seg.End.X, seg.End.Y);
                }
            }

            return maxX >= s.Min.X && minX <= s.Max.X && maxY >= s.Min.Y && minY <= s.Max.Y;
        }
    }
}
