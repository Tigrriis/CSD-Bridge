using System;
using System.Collections.Generic;
using System.Linq;
using CsdBridge.Core.Civil;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;

namespace CsdBridge.Core.Vehicles
{
    /// <summary>How one turning path is drawn: layers, what to include, label.</summary>
    public sealed class TurnPathStyle
    {
        public TurnPathLayers Layers { get; set; } = new TurnPathLayers();

        public TurnPathDrawOptions Draw { get; set; } = new TurnPathDrawOptions();

        /// <summary>Vehicle outlines every this many metres along the path (0 = none).</summary>
        public double OutlineInterval { get; set; }

        public double? TextHeight { get; set; }

        public HatchStyle Hatch { get; set; } = new HatchStyle { Pattern = "SOLID" };

        /// <summary>Label text at the start position; null = the vehicle name.</summary>
        public string? Label { get; set; }

        /// <summary>Leave out the vehicle at the start (it is the end of the previous leg).</summary>
        public bool SkipStartVehicle { get; set; }

        public static TurnPathStyle From(TurnPathConventions c) => new TurnPathStyle
        {
            Layers = c.Layers.Clone(),
            Draw = c.Draw.Clone(),
            OutlineInterval = c.OutlineInterval,
            TextHeight = c.TextHeight,
            Hatch = c.Hatch,
        };
    }

    /// <summary>An entity to draw and which part of the turning path it belongs to.</summary>
    public sealed class DraftedEntity
    {
        public DraftedEntity(string group, EntitySpec spec)
        {
            Group = group;
            Spec = spec;
        }

        /// <summary>swept_path, clearance, vehicle, vehicle_intermediate, path, wheel_paths, labels or hatch.</summary>
        public string Group { get; }

        public EntitySpec Spec { get; }
    }

    /// <summary>Turns a simulated turning path into drawing entities.</summary>
    public static class TurnPathDrafter
    {
        /// <summary>Polylines longer than this are split (the CAD host's limit per polyline).</summary>
        public const int MaxPolylinePoints = 500;

        public static List<DraftedEntity> Draw(TurnPathResult r, TurnPathStyle style)
        {
            DesignVehicle v = r.Vehicle;
            TurnPathLayers layers = style.Layers.Resolve(v.Name);
            TurnPathDrawOptions draw = style.Draw;
            var list = new List<DraftedEntity>();

            if (draw.Hatch)
            {
                var hatch = new HatchSpec
                {
                    Layer = layers.Hatch,
                    Pattern = string.IsNullOrWhiteSpace(style.Hatch.Pattern) ? "SOLID" : style.Hatch.Pattern,
                    Scale = style.Hatch.Scale > 0 ? style.Hatch.Scale : 1,
                    AngleDeg = style.Hatch.Angle,
                    SendToBack = true,
                };
                hatch.Loops.Add(r.ClosedOutline());
                list.Add(new DraftedEntity("hatch", hatch));
            }

            if (draw.SweptPath)
            {
                if (draw.ClosedOutline)
                {
                    PolylineSpec outline = r.ClosedOutline();
                    outline.Layer = layers.SweptPath;
                    list.Add(new DraftedEntity("swept_path", outline));
                }
                else
                {
                    AddPolylines(list, "swept_path", r.LeftBody, layers.SweptPath);
                    AddPolylines(list, "swept_path", r.RightBody, layers.SweptPath);
                }
            }

            if (draw.Clearance)
            {
                AddPolylines(list, "clearance", r.LeftClearance, layers.Clearance);
                AddPolylines(list, "clearance", r.RightClearance, layers.Clearance);
            }

            if (draw.Path)
            {
                EntitySpec path = PathOps.ToSpec(r.Path);
                path.Layer = layers.Path;
                list.Add(new DraftedEntity("path", path));
            }

            if (draw.WheelPaths)
            {
                foreach (List<Point2> wheel in r.WheelPaths)
                {
                    AddPolylines(list, "wheel_paths", wheel, layers.WheelPaths);
                }
            }

            if (draw.Vehicle)
            {
                VehiclePose start = r.Poses[0], end = r.Poses[r.Poses.Count - 1];
                if (!style.SkipStartVehicle)
                {
                    AddVehicle(list, "vehicle", v, start, layers.Vehicle, wheels: true);
                }

                AddVehicle(list, "vehicle", v, end, layers.Vehicle, wheels: true);
            }

            if (style.OutlineInterval > 0.05)
            {
                for (double ch = style.OutlineInterval; ch < r.PathLength - 0.05; ch += style.OutlineInterval)
                {
                    AddVehicle(list, "vehicle_intermediate", v, r.PoseAt(ch), layers.VehicleIntermediate, wheels: false);
                }
            }

            if (draw.Labels)
            {
                VehiclePose start = r.Poses[0];
                double xMid = (v.Wheelbase + v.EffectiveFrontOverhang - v.EffectiveRearOverhang) / 2;
                double rot = start.Heading * 180 / Math.PI;
                rot = ((rot % 360) + 360) % 360;
                if (rot > 90 && rot <= 270)
                {
                    rot -= 180; // keep the text readable
                }

                list.Add(new DraftedEntity("labels", new TextSpec
                {
                    Layer = layers.Labels,
                    Position = start.ToWorld(xMid, 0),
                    Text = style.Label ?? v.Name,
                    Height = style.TextHeight,
                    RotationDeg = rot,
                    Align = TextAlign.MiddleCenter,
                }));
            }

            return list;
        }

        /// <summary>Layer specs for the layers in <paramref name="names"/> that don't exist yet, styled from the conventions.</summary>
        public static List<LayerSpec> MissingLayers(IEnumerable<string> names, IEnumerable<string> existing, IReadOnlyDictionary<string, LayerStyle> styles)
        {
            var have = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
            var specs = new List<LayerSpec>();
            foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (have.Contains(name))
                {
                    continue;
                }

                var spec = new LayerSpec { Name = name };
                if (styles.TryGetValue(name, out LayerStyle? st))
                {
                    spec.ColorIndex = st.Color;
                    spec.Rgb = st.Rgb != null && st.Rgb.Length == 3 ? st.Rgb.Select(x => (byte)Math.Max(0, Math.Min(255, x))).ToArray() : null;
                    spec.Linetype = st.Linetype;
                    spec.LineweightMm = st.Lineweight;
                    spec.Plot = st.Plot;
                    spec.Description = st.Description;
                }

                specs.Add(spec);
            }

            return specs;
        }

        private static void AddPolylines(List<DraftedEntity> list, string group, List<Point2> pts, string layer)
        {
            if (pts.Count < 2)
            {
                return;
            }

            for (int start = 0; start < pts.Count - 1; start += MaxPolylinePoints - 1)
            {
                int count = Math.Min(MaxPolylinePoints, pts.Count - start);
                list.Add(new DraftedEntity(group, new PolylineSpec { Points = pts.GetRange(start, count), Layer = layer }));
                if (start + count >= pts.Count)
                {
                    break;
                }
            }
        }

        /// <summary>The vehicle outline (and wheels and axles) at a pose, as entities on a layer.</summary>
        public static List<EntitySpec> VehicleOutline(DesignVehicle v, VehiclePose pose, string layer, bool wheels)
        {
            var list = new List<DraftedEntity>();
            AddVehicle(list, "vehicle", v, pose, layer, wheels);
            return list.Select(d => d.Spec).ToList();
        }

        private static void AddVehicle(List<DraftedEntity> list, string group, DesignVehicle v, VehiclePose pose, string layer, bool wheels)
        {
            var (pts, bulges) = v.BodyOutline();
            list.Add(new DraftedEntity(group, new PolylineSpec
            {
                Points = pts.Select(pose.ToWorld).ToList(),
                Bulges = bulges.Any(b => Math.Abs(b) > 1e-12) ? bulges : null,
                Closed = true,
                Layer = layer,
            }));

            if (!wheels)
            {
                return;
            }

            foreach (List<VehiclePoint> wheel in v.Wheels())
            {
                list.Add(new DraftedEntity(group, new PolylineSpec { Points = wheel.Select(pose.ToWorld).ToList(), Closed = true, Layer = layer }));
            }

            // Axle centre lines, as on the standard's vehicle drawings.
            double half = v.Track / 2 + v.WheelWidth / 2;
            list.Add(new DraftedEntity(group, new LineSpec { Start = pose.ToWorld(0, -half), End = pose.ToWorld(0, half), Layer = layer }));
            list.Add(new DraftedEntity(group, new LineSpec { Start = pose.ToWorld(v.Wheelbase, -half), End = pose.ToWorld(v.Wheelbase, half), Layer = layer }));
        }
    }
}
