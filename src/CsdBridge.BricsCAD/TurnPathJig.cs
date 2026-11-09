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
    /// swept path, clearance lines, driven path and end vehicle are drawn as temporary graphics.
    /// Points acquired by a jig are in world coordinates.
    /// </summary>
    internal sealed class TurnPathJig : DrawJig
    {
        private const short SweptColour = 1;       // red
        private const short ClearanceColour = 8;   // grey
        private const short VehicleColour = 4;     // cyan
        private const short PathColour = 3;        // green

        private readonly Func<Point2, TurnPathRequest?> _build;
        private readonly string _message;
        private readonly Point3d? _basePoint;
        private readonly IReadOnlyList<string> _keywords;
        private readonly TurnPathStyle _style;
        private Point3d _current;
        private bool _hasPoint;
        private List<EntitySpec> _preview = new List<EntitySpec>();
        private double _lastSimulationMs;

        /// <param name="build">The simulation request for a cursor position, or null when there is nothing to show yet.</param>
        /// <param name="basePoint">World point for the rubber-band line, if any.</param>
        public TurnPathJig(string message, Point3d? basePoint, IReadOnlyList<string>? keywords, Func<Point2, TurnPathRequest?> build)
        {
            _message = message;
            _basePoint = basePoint;
            _keywords = keywords ?? Array.Empty<string>();
            _build = build;
            _style = new TurnPathStyle
            {
                Draw = new TurnPathDrawOptions { SweptPath = true, Clearance = true, Vehicle = true, Path = true, WheelPaths = false, Labels = false, Hatch = false },
            };
        }

        /// <summary>The last point the cursor was at (world coordinates).</summary>
        public Point3d Point => _current;

        public Point2 Point2 => new Point2(_current.X, _current.Y);

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
            Update();
            return SamplerStatus.OK;
        }

        private void Update()
        {
            TurnPathRequest? req = null;
            try
            {
                req = _build(Point2);
                if (req == null)
                {
                    _preview = new List<EntitySpec>();
                    return;
                }

                // Coarser than the final drawing, and coarser still when the last run was slow, so
                // the preview keeps up with the cursor.
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
                _preview = TurnPathDrafter.Draw(result, _style).Select(d => d.Spec).ToList();
            }
            catch (Exception)
            {
                // The path can't be driven yet (too short, doubles back...): show just the path.
                _preview = req == null ? new List<EntitySpec>() : new List<EntitySpec> { Core.Geometry.PathOps.ToSpec(req.Path) };
            }
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            foreach (EntitySpec spec in _preview)
            {
                Entity? ent = BricsCadDrawingHost.PreviewEntity(spec);
                if (ent == null)
                {
                    continue;
                }

                using (ent)
                {
                    ent.ColorIndex = ColourFor(spec);
                    draw.Geometry.Draw(ent);
                }
            }

            return true;
        }

        private short ColourFor(EntitySpec spec)
        {
            string layer = spec.Layer ?? "";
            if (layer == _style.Layers.Clearance)
            {
                return ClearanceColour;
            }

            if (layer == _style.Layers.Vehicle || layer == _style.Layers.VehicleIntermediate)
            {
                return VehicleColour;
            }

            if (layer == _style.Layers.Path)
            {
                return PathColour;
            }

            return SweptColour;
        }
    }
}
