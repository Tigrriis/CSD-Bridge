using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Bricscad.EditorInput;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Vehicles;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using Teigha.GraphicsInterface;

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// Live preview while the user picks a point for AITURN: as the cursor moves, the manoeuvre
    /// that point would give is simulated (at a coarser step than the final result) and its
    /// swept path, clearance lines, driven path and end vehicle are drawn as temporary graphics,
    /// along with anything already decided (earlier legs of a drive). Points acquired by a jig
    /// are in world coordinates, in the drawing's units; the preview works in metres, so the
    /// cursor is converted on the way in and the graphics on the way out.
    /// </summary>
    internal sealed class TurnPathJig : DrawJig
    {
        private readonly DrawingUnits _units;
        private readonly Func<Point2, List<EntitySpec>?> _preview;
        private readonly IReadOnlyList<EntitySpec> _fixed;
        private readonly string _message;
        private readonly Point3d? _basePoint;
        private readonly IReadOnlyList<string> _keywords;
        private Point3d _current;
        private bool _hasPoint;
        private List<EntitySpec> _live = new List<EntitySpec>();

        /// <param name="units">The drawing's units.</param>
        /// <param name="preview">Temporary graphics (in metres) for a cursor position (in metres), or null for none.</param>
        /// <param name="fixedGraphics">Temporary graphics (in metres) drawn whatever the cursor does (earlier legs).</param>
        /// <param name="basePoint">World point, in drawing units, for the rubber-band line, if any.</param>
        public TurnPathJig(DrawingUnits units, string message, Point3d? basePoint, IReadOnlyList<string>? keywords, Func<Point2, List<EntitySpec>?> preview, IReadOnlyList<EntitySpec>? fixedGraphics = null)
        {
            _units = units;
            _message = message;
            _basePoint = basePoint;
            _keywords = keywords ?? Array.Empty<string>();
            _preview = preview;
            _fixed = (fixedGraphics ?? Array.Empty<EntitySpec>()).Select(s => units.ToDrawing(s)).ToList();
        }

        /// <summary>The last point the cursor was at (world coordinates, drawing units).</summary>
        public Point3d Point => _current;

        /// <summary>The last point the cursor was at, in metres.</summary>
        public Point2 Point2 => _units.ToModel(new Point2(_current.X, _current.Y));

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptPointOptions(_message)
            {
                UserInputControls = UserInputControls.Accept3dCoordinates | UserInputControls.NullResponseAccepted,
            };
            if (_basePoint is Point3d bp)
            {
                options.BasePoint = bp;
                options.UseBasePoint = true;
                options.Cursor = CursorType.RubberBand;
            }

            foreach (string keyword in _keywords)
            {
                options.Keywords.Add(keyword);
            }

            PromptPointResult res = prompts.AcquirePoint(options);
            if (res.Status != PromptStatus.OK)
            {
                return SamplerStatus.Cancel;
            }

            if (_hasPoint && res.Value.DistanceTo(_current) < 1e-6)
            {
                return SamplerStatus.NoChange;
            }

            _current = res.Value;
            _hasPoint = true;
            try
            {
                _live = (_preview(Point2) ?? new List<EntitySpec>()).Select(s => _units.ToDrawing(s)).ToList();
            }
            catch (Exception)
            {
                _live = new List<EntitySpec>();
            }

            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            foreach (EntitySpec spec in _fixed.Concat(_live))
            {
                Entity? ent = BricsCadDrawingHost.PreviewEntity(spec);
                if (ent == null)
                {
                    continue;
                }

                using (ent)
                {
                    ent.ColorIndex = PreviewBuilder.ColourFor(spec);
                    draw.Geometry.Draw(ent);
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Makes the temporary graphics for previews: a quick simulation of a request, or just the
    /// vehicle at a pose. The simulation step is coarser than the final drawing's, and coarser
    /// still when the last run was slow, so the preview keeps up with the cursor.
    /// </summary>
    internal sealed class PreviewBuilder
    {
        private const short SweptColour = 1;       // red
        private const short ClearanceColour = 8;   // grey
        private const short VehicleColour = 4;     // cyan
        private const short PathColour = 3;        // green

        private static readonly TurnPathLayers Layers = new TurnPathLayers();

        private readonly TurnPathStyle _style = new TurnPathStyle
        {
            Layers = Layers,
            Draw = new TurnPathDrawOptions { SweptPath = true, Clearance = true, Vehicle = true, Path = true, WheelPaths = false, Labels = false, Hatch = false },
        };

        private double _lastSimulationMs;

        /// <summary>Simulates the request coarsely and returns its graphics; on failure, just the path.</summary>
        public List<EntitySpec> Specs(TurnPathRequest req, bool startVehicle = true)
        {
            try
            {
                double step = Math.Max(0.1, req.Path.Length / 250);
                if (_lastSimulationMs > 120)
                {
                    step *= 2;
                }

                req.Step = step;
                req.Tolerance = 0.005;
                var watch = Stopwatch.StartNew();
                TurnPathResult result = TurnPathSimulator.Simulate(req);
                _lastSimulationMs = watch.Elapsed.TotalMilliseconds;
                _style.SkipStartVehicle = !startVehicle;
                return TurnPathDrafter.Draw(result, _style).Select(d => d.Spec).ToList();
            }
            catch (Exception)
            {
                return new List<EntitySpec> { Core.Geometry.PathOps.ToSpec(req.Path) };
            }
        }

        /// <summary>Preview graphics for a leg that has already been simulated.</summary>
        public List<EntitySpec> Specs(TurnPathResult result, bool startVehicle)
        {
            _style.SkipStartVehicle = !startVehicle;
            return TurnPathDrafter.Draw(result, _style).Select(d => d.Spec).ToList();
        }

        /// <summary>The vehicle outline with wheels at a pose.</summary>
        public static List<EntitySpec> Vehicle(DesignVehicle v, VehiclePose pose) =>
            TurnPathDrafter.VehicleOutline(v, pose, Layers.Vehicle, wheels: true);

        public static short ColourFor(EntitySpec spec)
        {
            string layer = spec.Layer ?? "";
            if (layer == Layers.Clearance)
            {
                return ClearanceColour;
            }

            if (layer == Layers.Vehicle || layer == Layers.VehicleIntermediate)
            {
                return VehicleColour;
            }

            if (layer == Layers.Path)
            {
                return PathColour;
            }

            return SweptColour;
        }
    }
}
