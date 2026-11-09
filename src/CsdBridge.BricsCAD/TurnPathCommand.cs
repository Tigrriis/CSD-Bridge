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
    /// points), simulates the vehicle and draws the result through the same drawing host as
    /// Claude's tools, so it lands on the configured layers and is one UNDO step.
    /// </summary>
    internal static class TurnPathCommand
    {
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
                List<(TurnPathRequest Request, string Label)>? runs = BuildRequests(ed, choice, warnings);
                if (runs == null || runs.Count == 0)
                {
                    return;
                }

                Draw(doc, choice, runs, warnings);
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
        private static List<(TurnPathRequest, string)>? BuildRequests(Editor ed, TurnPathChoice c, List<string> warnings)
        {
            DesignVehicle v = c.Vehicle;
            var runs = new List<(TurnPathRequest, string)>();
            switch (c.Mode)
            {
                case TurnPathChoice.ManoeuvreMode.Template:
                {
                    PromptPointResult start = ed.GetPoint(new PromptPointOptions("\nStart point (centre of the rear axle of the parked vehicle): "));
                    if (start.Status != PromptStatus.OK)
                    {
                        return null;
                    }

                    Point2 s = Wcs(ed, start.Value);
                    Point3d startWcs = start.Value.TransformBy(ed.CurrentUserCoordinateSystem);
                    // Preview the (largest) template as the direction swings with the cursor.
                    double previewAngle = c.TurnAngles.Max();
                    var jig = new TurnPathJig("\nDirection of travel (pick a point ahead of the vehicle): ", startWcs, null, cursor =>
                    {
                        if (Distance(s, cursor) < 1e-6)
                        {
                            return null;
                        }

                        return TurnPathBuilder.Template(new TurnTemplate
                        {
                            Vehicle = v,
                            Start = s,
                            Heading = Math.Atan2(cursor.Y - s.Y, cursor.X - s.X),
                            Radius = c.Radius,
                            TurnAngleDeg = previewAngle,
                            Left = c.Left,
                            LeadIn = c.LeadIn,
                            LeadOut = c.LeadOut,
                            Reference = c.Reference,
                        });
                    });
                    PromptResult ahead = ed.Drag(jig);
                    if (ahead.Status != PromptStatus.OK)
                    {
                        return null;
                    }

                    Point2 a = jig.Point2;
                    if (Distance(s, a) < 1e-6)
                    {
                        throw new ArgumentException("The direction point must be away from the start point.");
                    }

                    double heading = Math.Atan2(a.Y - s.Y, a.X - s.X);
                    if (c.Radius < v.MinTurnRadius - 1e-9)
                    {
                        warnings.Add($"A {c.Radius:0.##} m radius is below the {v.Name}'s minimum of {v.MinTurnRadius:0.#} m (AS/NZS 2890.1 B3.1).");
                    }

                    foreach (double angle in c.TurnAngles)
                    {
                        TurnPathRequest req = TurnPathBuilder.Template(new TurnTemplate
                        {
                            Vehicle = v,
                            Start = s,
                            Heading = heading,
                            Radius = c.Radius,
                            TurnAngleDeg = angle,
                            Left = c.Left,
                            LeadIn = c.LeadIn,
                            LeadOut = c.LeadOut,
                            Reference = c.Reference,
                        });
                        runs.Add((req, $"{v.Name} R{c.Radius:0.#} m"));
                    }

                    break;
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
                    runs.Add((new TurnPathRequest { Vehicle = v, Path = path, Reference = v.Resolve(c.Reference, leftTurn: true), Reverse = c.Reverse }, v.Name));
                    break;
                }

                case TurnPathChoice.ManoeuvreMode.Points:
                {
                    VehiclePoint reference = v.Resolve(c.Reference, leftTurn: true);
                    string what = c.Reference == ReferencePoint.RearAxle ? "rear axle centre" : c.Reference == ReferencePoint.FrontAxle ? "front axle centre" : "outer front wheel";
                    var pts = new List<Point2>();
                    PromptPointResult first = ed.GetPoint(new PromptPointOptions($"\nFirst point of the path (the {what} follows it): "));
                    if (first.Status != PromptStatus.OK)
                    {
                        return null;
                    }

                    pts.Add(Wcs(ed, first.Value));
                    Point3d last = first.Value.TransformBy(ed.CurrentUserCoordinateSystem);
                    while (true)
                    {
                        // Preview the manoeuvre through the points so far plus the cursor.
                        List<Point2> fixedPoints = pts;
                        var jig = new TurnPathJig("\nNext point or [Undo] (Enter to finish): ", last, new[] { "Undo" }, cursor =>
                        {
                            if (Distance(fixedPoints[fixedPoints.Count - 1], cursor) < 0.05)
                            {
                                return null;
                            }

                            var trial = new List<Point2>(fixedPoints) { cursor };
                            Path2 path = TurnPathBuilder.FromPoints(trial, c.Radius);
                            return new TurnPathRequest { Vehicle = v, Path = path, Reference = reference, Reverse = c.Reverse };
                        });
                        PromptResult next = ed.Drag(jig);
                        if (next.Status == PromptStatus.None)
                        {
                            break;
                        }

                        if (next.Status == PromptStatus.Keyword)
                        {
                            if (pts.Count > 1)
                            {
                                pts.RemoveAt(pts.Count - 1);
                            }

                            Point2 back = pts[pts.Count - 1];
                            last = new Point3d(back.X, back.Y, 0);
                            continue;
                        }

                        if (next.Status != PromptStatus.OK)
                        {
                            return null;
                        }

                        pts.Add(jig.Point2);
                        last = jig.Point;
                    }

                    if (pts.Count < 2)
                    {
                        ed.WriteMessage("\nCSD-Bridge: at least two points are needed.\n");
                        return null;
                    }

                    Path2 path = TurnPathBuilder.FromPoints(pts, c.Radius, warnings);
                    runs.Add((new TurnPathRequest { Vehicle = v, Path = path, Reference = reference, Reverse = c.Reverse }, v.Name));
                    break;
                }
            }

            return runs;
        }

        private static void Draw(Document doc, TurnPathChoice c, List<(TurnPathRequest Request, string Label)> runs, List<string> warnings)
        {
            Editor ed = doc.Editor;
            var host = new BricsCadDrawingHost(doc);
            TurnPathConventions conv = c.Conventions;
            var drafted = new List<DraftedEntity>();
            var summary = new List<string>();
            foreach (var (req, label) in runs)
            {
                req.Step = conv.Step;
                req.Tolerance = conv.Tolerance;
                req.ClearanceInside = c.ClearanceInside;
                req.ClearanceOutside = c.ClearanceOutside;
                TurnPathResult result = TurnPathSimulator.Simulate(req);
                warnings.AddRange(result.Warnings);

                var style = new TurnPathStyle
                {
                    Layers = c.Style.Layers,
                    Draw = c.Style.Draw,
                    OutlineInterval = c.Style.OutlineInterval,
                    TextHeight = c.Style.TextHeight,
                    Hatch = c.Style.Hatch,
                    Label = label,
                };
                drafted.AddRange(TurnPathDrafter.Draw(result, style));

                string tightest = double.IsInfinity(result.TightestRadius) ? "straight" : $"tightest turn R {result.TightestRadius:0.00} m at the outer front wheel";
                summary.Add($"{label}: path {result.PathLength:0.0} m, net turn {result.NetTurnDeg:0} deg, {tightest} (minimum {c.Vehicle.MinTurnRadius:0.0} m)" +
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

            ed.WriteMessage($"\nCSD-Bridge: drew {ok} entities for the {c.Vehicle.Name} turning path" +
                            (missing.Count > 0 ? $" (created layers: {string.Join(", ", missing.Select(m => m.Name))})" : "") + ".");
            foreach (string line in summary)
            {
                ed.WriteMessage("\n  " + line);
            }

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
        private static Point2 Wcs(Editor ed, Point3d ucs)
        {
            Point3d w = ucs.TransformBy(ed.CurrentUserCoordinateSystem);
            return new Point2(w.X, w.Y);
        }
    }
}
