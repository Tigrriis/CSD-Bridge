using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Bricscad.ApplicationServices;
using CsdBridge.Core.Civil;
using Teigha.DatabaseServices;
using Teigha.Geometry;

namespace CsdBridge.BricsCAD.Csd
{
    /// <summary>
    /// Samples existing ground levels along a centreline from a BricsCAD TIN surface.
    /// Runs on the main thread. The Civil types are reached by reflection so the add-in
    /// still loads on BricsCAD versions or editions without them.
    /// </summary>
    internal static class GroundSampler
    {
        public sealed class Request
        {
            public string Centreline { get; set; } = "";
            public string? Surface { get; set; }
            public double Interval { get; set; } = 5;
            public double StartChainage { get; set; }
            public double? From { get; set; }
            public double? To { get; set; }
            public bool Reverse { get; set; }
        }

        public static object Sample(Document doc, Request req)
        {
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Curve curve = OpenCurve(db, tr, req.Centreline);
                List<Entity> surfaces = FindSurfaces(db, tr);
                if (surfaces.Count == 0)
                {
                    throw new CsdException(
                        "There is no BricsCAD TIN surface in this drawing. Civil Site Design keeps its own surfaces, so make a BricsCAD copy " +
                        "of the existing surface first with CSD's Surface Exchange (Surfaces tab > BricsCAD Surfaces), then sample again. " +
                        "(See csd_help topic \"ground\".)");
                }

                Entity surface = PickSurface(surfaces, req.Surface);
                MethodInfo elevationAt = surface.GetType().GetMethod("GetElevationAtPoint", new[] { typeof(Point3d) })
                    ?? throw new CsdException("This BricsCAD version can't read surface elevations.");

                double length = curve.GetDistanceAtParameter(curve.EndParam);
                double from = Math.Max(0, (req.From ?? req.StartChainage) - req.StartChainage);
                double to = Math.Min(length, (req.To ?? req.StartChainage + length) - req.StartChainage);
                if (to <= from)
                {
                    throw new CsdException($"Nothing to sample: the centreline is {length:0.###} long and starts at chainage {req.StartChainage:0.###}.");
                }

                var points = new List<GroundPoint>();
                int misses = 0;
                var distances = new List<double>();
                for (double d = from; d < to - 1e-6; d += req.Interval)
                {
                    distances.Add(d);
                }

                distances.Add(to);
                foreach (double d in distances)
                {
                    double along = req.Reverse ? length - d : d;
                    Point3d p = curve.GetPointAtDist(along);
                    try
                    {
                        double z = (double)elevationAt.Invoke(surface, new object[] { new Point3d(p.X, p.Y, 0) })!;
                        if (double.IsNaN(z) || double.IsInfinity(z) || Math.Abs(z) > 1e7)
                        {
                            misses++;
                            continue;
                        }

                        points.Add(new GroundPoint(req.StartChainage + d, z));
                    }
                    catch (Exception)
                    {
                        misses++; // outside the surface
                    }
                }

                tr.Commit();
                if (points.Count < 2)
                {
                    throw new CsdException($"The centreline doesn't cross the surface '{SurfaceName(surface)}' (no levels found).");
                }

                var profile = new GroundProfile(points);
                string id = GroundProfileStore.Add(profile);
                int step = Math.Max(1, points.Count / 20);
                return new
                {
                    ground_id = id,
                    surface = SurfaceName(surface),
                    surfaces_available = surfaces.Count > 1 ? surfaces.Select(SurfaceName).ToList() : null,
                    centreline_length = Math.Round(length, 3),
                    chainage_from = Math.Round(profile.Start, 3),
                    chainage_to = Math.Round(profile.End, 3),
                    points = points.Count,
                    missed = misses > 0 ? misses : (int?)null,
                    missed_note = misses > 0 ? "Some points are outside the surface; the profile skips them." : null,
                    min_level = Math.Round(points.Min(p => p.Level), 3),
                    max_level = Math.Round(points.Max(p => p.Level), 3),
                    preview = points.Where((_, i) => i % step == 0 || i == points.Count - 1)
                        .Select(p => new[] { Math.Round(p.Chainage, 3), Math.Round(p.Level, 3) }).ToList(),
                };
            }
        }

        public static List<string> SurfaceNames(Document doc)
        {
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                return FindSurfaces(doc.Database, tr).Select(SurfaceName).ToList();
            }
        }

        private static Curve OpenCurve(Database db, Transaction tr, string handle)
        {
            if (!long.TryParse(handle, System.Globalization.NumberStyles.HexNumber, null, out long h))
            {
                throw new CsdException($"'{handle}' is not an entity handle.");
            }

            if (!db.TryGetObjectId(new Handle(h), out ObjectId id) || id.IsErased)
            {
                throw new CsdException($"No entity with handle {handle}.");
            }

            return tr.GetObject(id, OpenMode.ForRead) as Curve
                ?? throw new CsdException($"Entity {handle} is not a line, polyline, arc or alignment.");
        }

        private static List<Entity> FindSurfaces(Database db, Transaction tr)
        {
            var list = new List<Entity>();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e))
                {
                    continue;
                }

                for (Type? t = e.GetType(); t != null; t = t.BaseType)
                {
                    if (t.FullName == "Bricscad.Civil.TinSurfaceStatic")
                    {
                        list.Add(e);
                        break;
                    }
                }
            }

            return list;
        }

        private static Entity PickSurface(List<Entity> surfaces, string? wanted)
        {
            if (string.IsNullOrWhiteSpace(wanted))
            {
                if (surfaces.Count == 1)
                {
                    return surfaces[0];
                }

                throw new CsdException($"The drawing has {surfaces.Count} TIN surfaces; say which with 'surface': {string.Join(", ", surfaces.Select(SurfaceName))}.");
            }

            return surfaces.FirstOrDefault(s => string.Equals(SurfaceName(s), wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.Handle.ToString(), wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new CsdException($"No TIN surface '{wanted}'. Surfaces: {string.Join(", ", surfaces.Select(SurfaceName))}.");
        }

        private static string SurfaceName(Entity e)
        {
            string? name = e.GetType().GetProperty("Name")?.GetValue(e) as string;
            return string.IsNullOrWhiteSpace(name) ? e.Handle.ToString() : name!;
        }
    }
}
