using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Bricscad.ApplicationServices;
using Bricscad.EditorInput;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;
using CsdBridge.Core.Vehicles;
using Teigha.DatabaseServices;
using Teigha.Geometry;

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// AITURN: draw an AS/NZS 2890.1 B85/B99 swept path. The dialog collects the options, then
    /// the command prompts for the geometry (start point and direction, a curve to follow, or
    /// destinations to drive to), simulates the vehicle and draws the result through the same
    /// drawing host as Claude's tools, so it lands on the configured layers and is one UNDO step.
    /// </summary>
    internal static class TurnPathCommand
    {
        /// <summary>One manoeuvre to draw: a template, a followed curve, or one leg of a drive.</summary>
        private sealed class Leg
        {
            public Leg(TurnPathRequest request, string label, bool first)
            {
                Request = request;
                Label = label;
                First = first;
            }

            public TurnPathRequest Request { get; }
            public string Label { get; }

            /// <summary>The first manoeuvre gets the start vehicle and the label.</summary>
            public bool First { get; }
        }

        public static void Run(Document doc)
        {
            Editor ed = doc.Editor;
            BridgeSettings settings = AgentRunner.Settings;
            TurnPathConventions? conventions = settings.LoadTurnPathConventions(out string? loadError);
            if (conventions == null)
            {
                ed.WriteMessage($"\nCSD-Bridge: the turning path conventions couldn't be loaded ({loadError}); using the built-in B99/B85 defaults.\n");
                conventions = TurnPathConventions.Default();
            }

            string savePath = string.IsNullOrWhiteSpace(settings.TurnPathsFile)
                ? BridgeSettings.UserTurnPathsPath
                : Environment.ExpandEnvironmentVariables(settings.TurnPathsFile!);

            TurnPathChoice choice;
            using (var dialog = new TurnPathDialog(conventions, savePath))
            {
                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                choice = dialog.Choice;
            }

            try
            {
                var warnings = new List<string>();
                List<Leg>? legs = BuildLegs(ed, choice, warnings);
                if (legs == null || legs.Count == 0)
                {
                    return;
                }

                Draw(doc, choice, legs, warnings);
            }
            catch (ArgumentException ex)
            {
                ed.WriteMessage("\nCSD-Bridge: " + ex.Message + "\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nCSD-Bridge: the turning path failed: " + ex.Message + "\n");
            }
        }

        /// <summary>Prompts for the geometry the chosen manoeuvre needs. Null when the user cancels.</summary>
        private static List<Leg>? BuildLegs(Editor ed, TurnPathChoice c, List<string> warnings)
        {
            DesignVehicle v = c.Vehicle;
            var preview = new PreviewBuilder();
            switch (c.Mode)
            {
                case TurnPathChoice.ManoeuvreMode.Template:
                {
                    if (!PickStartAndHeading(ed, v, "\nStart point (centre of the rear axle of the parked vehicle): ", out Point2 s, out double heading,
                        (at, cursor) => preview.Specs(TurnPathBuilder.Template(TemplateFor(c, at, Math.Atan2(cursor.Y - at.Y, cursor.X - at.X), c.TurnAngles.Max())))))
                    {
                        return null;
                    }

                    if (c.Radius < v.MinTurnRadius - 1e-9)
                    {
                        warnings.Add($"A {c.Radius:0.##} m radius is below the {v.Name}'s minimum of {v.MinTurnRadius:0.#} m (AS/NZS 2890.1 B3.1).");
                    }

                    var legs = new List<Leg>();
                    foreach (double angle in c.TurnAngles)
                    {
                        legs.Add(new Leg(TurnPathBuilder.Template(TemplateFor(c, s, heading, angle)), $"{v.Name} R{c.Radius:0.#} m", first: true));
                    }

                    return legs;
                }

                case TurnPathChoice.ManoeuvreMode.Follow:
                {
                    var options = new PromptEntityOptions("\nSelect the line, arc or polyline the vehicle follows (pick near the end it starts from): ");
                    options.SetRejectMessage("\nThat isn't a line, arc, circle or polyline.");
                    options.AddAllowedClass(typeof(Curve), false);
                    PromptEntityResult res = ed.GetEntity(options);
                    if (res.Status != PromptStatus.OK)
                    {
                        return null;
                    }

                    var host = new BricsCadDrawingHost(ed.Document);
                    string handle = res.ObjectId.Handle.ToString();
                    IReadOnlyDictionary<string, EntitySpec> curves = host.GetCurves(new[] { handle });
                    if (curves.Count == 0)
                    {
                        throw new ArgumentException("That entity's geometry can't be read as a 2D curve.");
                    }

                    Path2 path = TurnPathBuilder.FromCurve(curves.Values.First(), Wcs(ed, res.PickedPoint));
                    var req = new TurnPathRequest { Vehicle = v, Path = path, Reference = v.Resolve(c.Reference, leftTurn: true), Reverse = c.Reverse };
                    return new List<Leg> { new Leg(req, v.Name, first: true) };
                }

                case TurnPathChoice.ManoeuvreMode.Drive:
                    return Drive(ed, c, preview, warnings);

                default:
                    return null;
            }
        }

        private static TurnTemplate TemplateFor(TurnPathChoice c, Point2 start, double heading, double angle) => new TurnTemplate
        {
            Vehicle = c.Vehicle,
            Start = start,
            Heading = heading,
            Radius = c.Radius,
            TurnAngleDeg = angle,
            Left = c.Left,
            LeadIn = c.LeadIn,
            LeadOut = c.LeadOut,
            Reference = c.Reference,
        };

        /// <summary>
        /// Drive mode: each click is where the vehicle goes next. It turns at the chosen radius until
        /// it points at the destination, then goes straight to it; forwards when the destination is
        /// ahead, in reverse when it is behind. Every leg keeps its own swept path, so a three-point
        /// turn is three legs. U takes the last leg back; Enter finishes.
        /// </summary>
        private static List<Leg>? Drive(Editor ed, TurnPathChoice c, PreviewBuilder preview, List<string> warnings)
        {
            DesignVehicle v = c.Vehicle;
            if (!PickStartAndHeading(ed, v, "\nStart point (centre of the rear axle of the vehicle): ", out Point2 start, out double heading,
                (at, cursor) => PreviewBuilder.Vehicle(v, new VehiclePose(at, Math.Atan2(cursor.Y - at.Y, cursor.X - at.X)))))
            {
                return null;
            }

            var pose = new VehiclePose(start, heading);
            var legs = new List<(Leg Leg, VehiclePose End, List<EntitySpec> Graphics)>();
            while (true)
            {
                VehiclePose from = pose;
                var fixedGraphics = legs.SelectMany(l => l.Graphics).ToList();
                if (legs.Count == 0)
                {
                    fixedGraphics.AddRange(PreviewBuilder.Vehicle(v, from));
                }

                var jig = new TurnPathJig(
                    legs.Count == 0 ? "\nWhere the vehicle drives to (click ahead to go forward, behind to reverse): " : "\nNext destination or [Undo] (Enter to finish): ",
                    new Point3d(from.RearAxle.X, from.RearAxle.Y, 0),
                    legs.Count == 0 ? null : new[] { "Undo" },
                    cursor =>
                    {
                        TurnPathBuilder.DriveLeg? leg = TurnPathBuilder.DriveTo(v, from, cursor, c.Radius);
                        return leg == null ? null : preview.Specs(leg.Request, startVehicle: false);
                    },
                    fixedGraphics);
                PromptResult res = ed.Drag(jig);
                if (res.Status == PromptStatus.None)
                {
                    break;
                }

                if (res.Status == PromptStatus.Keyword)
                {
                    if (legs.Count > 0)
                    {
                        legs.RemoveAt(legs.Count - 1);
                        pose = legs.Count > 0 ? legs[legs.Count - 1].End : new VehiclePose(start, heading);
                    }

                    continue;
                }

                if (res.Status != PromptStatus.OK)
                {
                    return null;
                }

                TurnPathBuilder.DriveLeg? chosen = TurnPathBuilder.DriveTo(v, from, jig.Point2, c.Radius);
                if (chosen == null)
                {
                    ed.WriteMessage("\nToo close to the vehicle to drive to; click further away.");
                    continue;
                }

                // Simulate now so the next leg starts from the real end pose, and keep its graphics on screen.
                TurnPathResult result = TurnPathSimulator.Simulate(CopyFor(chosen.Request, c));
                pose = result.Poses[result.Poses.Count - 1];
                string what = $"{(chosen.Reverse ? "reverse" : "forward")}, {(chosen.ArcDeg > 0.5 ? $"turn {(chosen.Left ? "left" : "right")} {chosen.ArcDeg:0}° then " : "")}{chosen.StraightLength:0.0} m straight";
                ed.WriteMessage($"\n  Leg {legs.Count + 1}: {what}.");
                legs.Add((new Leg(chosen.Request, v.Name, first: legs.Count == 0), pose, preview.Specs(CopyFor(chosen.Request, c), startVehicle: false)));
            }

            if (legs.Count == 0)
            {
                ed.WriteMessage("\nCSD-Bridge: no destinations were clicked, so nothing was drawn.\n");
                return null;
            }

            return legs.Select(l => l.Leg).ToList();
        }

        /// <summary>A copy of a request with the conventions' step and the chosen clearances, so the original stays as built.</summary>
        private static TurnPathRequest CopyFor(TurnPathRequest r, TurnPathChoice c) => new TurnPathRequest
        {
            Vehicle = r.Vehicle,
            Path = r.Path,
            Reference = r.Reference,
            Reverse = r.Reverse,
            InitialHeading = r.InitialHeading,
            Step = c.Conventions.Step,
            Tolerance = c.Conventions.Tolerance,
            ClearanceInside = c.ClearanceInside,
            ClearanceOutside = c.ClearanceOutside,
        };

        /// <summary>Start point, then the direction of travel from a second point, with a live preview.</summary>
        private static bool PickStartAndHeading(Editor ed, DesignVehicle v, string startPrompt, out Point2 start, out double heading, Func<Point2, Point2, List<EntitySpec>?> previewAt)
        {
            start = default;
            heading = 0;
            PromptPointResult first = ed.GetPoint(new PromptPointOptions(startPrompt));
            if (first.Status != PromptStatus.OK)
            {
                return false;
            }

            Point2 s = Wcs(ed, first.Value);
            Point3d startWcs = first.Value.TransformBy(ed.CurrentUserCoordinateSystem);
            var jig = new TurnPathJig("\nDirection of travel (pick a point ahead of the vehicle): ", startWcs, null,
                cursor => Distance(s, cursor) < 1e-6 ? null : previewAt(s, cursor));
            PromptResult ahead = ed.Drag(jig);
            if (ahead.Status != PromptStatus.OK)
            {
                return false;
            }

            Point2 a = jig.Point2;
            if (Distance(s, a) < 1e-6)
            {
                throw new ArgumentException("The direction point must be away from the start point.");
            }

            start = s;
            heading = Math.Atan2(a.Y - s.Y, a.X - s.X);
            return true;
        }

        private static void Draw(Document doc, TurnPathChoice c, List<Leg> legs, List<string> warnings)
        {
            Editor ed = doc.Editor;
            var host = new BricsCadDrawingHost(doc);
            TurnPathConventions conv = c.Conventions;
            var drafted = new List<DraftedEntity>();
            var summary = new List<string>();
            Point2? blockBase = null;
            int n = 0;
            foreach (Leg leg in legs)
            {
                n++;
                TurnPathResult result = TurnPathSimulator.Simulate(CopyFor(leg.Request, c));
                warnings.AddRange(result.Warnings.Select(w => legs.Count > 1 ? $"Leg {n}: {w}" : w));
                blockBase ??= result.Poses[0].RearAxle;

                var style = new TurnPathStyle
                {
                    Layers = c.Style.Layers,
                    Draw = c.Style.Draw.Clone(),
                    OutlineInterval = c.Style.OutlineInterval,
                    TextHeight = c.Style.TextHeight,
                    Hatch = c.Style.Hatch,
                    Label = leg.Label,
                    SkipStartVehicle = !leg.First,
                };
                style.Draw.Labels = c.Style.Draw.Labels && leg.First;
                drafted.AddRange(TurnPathDrafter.Draw(result, style));

                string tightest = double.IsInfinity(result.TightestRadius) ? "straight" : $"tightest turn R {result.TightestRadius:0.00} m at the outer front wheel";
                string name = legs.Count > 1 && c.Mode == TurnPathChoice.ManoeuvreMode.Drive ? $"Leg {n} ({(result.Reverse ? "reverse" : "forward")})" : leg.Label;
                summary.Add($"{name}: path {result.PathLength:0.0} m, net turn {result.NetTurnDeg:0} deg, {tightest} (minimum {c.Vehicle.MinTurnRadius:0.0} m)" +
                            $", clearances {result.ClearanceLeft * 1000:0} mm left / {result.ClearanceRight * 1000:0} mm right");
            }

            if (drafted.Count == 0)
            {
                ed.WriteMessage("\nCSD-Bridge: nothing to draw (every part is switched off).\n");
                return;
            }

            DrawingInfo info = host.GetDrawingInfo();
            List<LayerSpec> missing = TurnPathDrafter.MissingLayers(
                drafted.Select(d => d.Spec.Layer).Where(l => l != null).Select(l => l!),
                info.Layers.Select(l => l.Name),
                conv.LayerStyles);
            if (missing.Count > 0)
            {
                host.EnsureLayers(missing);
            }

            IReadOnlyList<ItemResult> created = host.CreateEntities(drafted.Select(d => d.Spec).ToList());
            int ok = created.Count(r => r.Ok);
            List<string> failed = created.Where(r => !r.Ok).Select(r => r.Error ?? "unknown error").Distinct().ToList();

            string blockNote = "";
            if (c.Style.Draw.Block && ok > 0 && blockBase is Point2 basePoint)
            {
                try
                {
                    List<string> handles = created.Where(r => r.Ok && r.Handle != null).Select(r => r.Handle!).ToList();
                    string blockLayer = c.Style.Layers.Resolve(c.Vehicle.Name).Block;
                    List<LayerSpec> blockLayerSpec = TurnPathDrafter.MissingLayers(new[] { blockLayer }, info.Layers.Select(l => l.Name), conv.LayerStyles);
                    if (blockLayerSpec.Count > 0)
                    {
                        host.EnsureLayers(blockLayerSpec);
                    }

                    BlockResult block = host.MakeBlock(handles, $"{c.Vehicle.Name} turning path", basePoint, blockLayer);
                    blockNote = $"\n  Made block '{block.BlockName}' ({block.EntityCount} entities) on layer {blockLayer}, inserted at the vehicle's start point.";
                }
                catch (System.Exception ex)
                {
                    blockNote = "\n  Couldn't make the block (the entities are left as drawn): " + ex.Message;
                }
            }

            ed.WriteMessage($"\nCSD-Bridge: drew {ok} entities for the {c.Vehicle.Name} turning path" +
                            (missing.Count > 0 ? $" (created layers: {string.Join(", ", missing.Select(m => m.Name))})" : "") + ".");
            foreach (string line in summary)
            {
                ed.WriteMessage("\n  " + line);
            }

            ed.WriteMessage(blockNote);
            foreach (string w in warnings.Distinct())
            {
                ed.WriteMessage("\n  Warning: " + w);
            }

            foreach (string f in failed)
            {
                ed.WriteMessage("\n  Not drawn: " + f);
            }

            ed.WriteMessage("\n");
        }

        private static double Distance(Point2 a, Point2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        /// <summary>Prompt results are in the current UCS; the simulator works in world coordinates.</summary>
        internal static Point2 Wcs(Editor ed, Point3d ucs)
        {
            Point3d w = ucs.TransformBy(ed.CurrentUserCoordinateSystem);
            return new Point2(w.X, w.Y);
        }
    }
}
