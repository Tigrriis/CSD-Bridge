using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Bricscad.ApplicationServices;
using Bricscad.EditorInput;
using CsdBridge.Core.Civil;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;
using CsdBridge.Core.Vehicles;
using Teigha.DatabaseServices;

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// AICLEARANCE: AS/NZS 2890.1 Appendix C ground clearance check. Either slide the template
    /// along a plotted long section you select, or drive the vehicle along a path and check each
    /// side of it against a BricsCAD TIN surface at every position.
    /// </summary>
    internal static class GroundClearanceCommand
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

            List<string> surfaces;
            try
            {
                surfaces = Csd.GroundSampler.SurfaceNames(doc);
            }
            catch (System.Exception)
            {
                surfaces = new List<string>();
            }

            GroundClearanceChoice choice;
            using (var dialog = new GroundClearanceDialog(conventions, savePath, surfaces))
            {
                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                choice = dialog.Choice;
            }

            try
            {
                var host = new BricsCadDrawingHost(doc);
                var warnings = new List<string>();
                var drafted = new List<DraftedEntity>();
                string summary;
                if (choice.Mode == GroundClearanceChoice.CheckMode.Section)
                {
                    GroundClearanceResult? r = CheckSection(ed, host, choice, drafted);
                    if (r == null)
                    {
                        return;
                    }

                    summary = r.Summary();
                    warnings.AddRange(r.Warnings);
                    foreach (GroundingRange g in r.Groundings)
                    {
                        ed.WriteMessage($"\n  Grounds ({g.Part}, facing {(g.Forward ? "up" : "down")} chainage) with the rear wheel between ch {g.From:0.00} and {g.To:0.00}, by up to {-g.WorstGap * 1000:0} mm.");
                    }
                }
                else
                {
                    PathClearanceResult? r = CheckSurface(ed, host, choice, drafted);
                    if (r == null)
                    {
                        return;
                    }

                    summary = r.Summary();
                    warnings.AddRange(r.Warnings);
                    foreach (PathClearanceSide side in r.Sides)
                    {
                        if (side.Worst != null)
                        {
                            ed.WriteMessage($"\n  {side.Name} side: closest {Math.Abs(side.Worst.MinGap) * 1000:0} mm {(side.Worst.MinGap < 0 ? "into" : "above")} the surface ({side.Worst.Part}) {side.Worst.Chainage:0.0} m along the path.");
                        }

                        foreach (var (from, to, worst) in side.Groundings)
                        {
                            ed.WriteMessage($"\n  {side.Name} side grounds between {from:0.0} and {to:0.0} m along the path, by up to {-worst * 1000:0} mm.");
                        }
                    }
                }

                int ok = 0;
                if (drafted.Count > 0)
                {
                    DrawingInfo info = host.GetDrawingInfo();
                    List<LayerSpec> missing = TurnPathDrafter.MissingLayers(drafted.Select(d => d.Spec.Layer).Where(l => l != null).Select(l => l!), info.Layers.Select(l => l.Name), conventions.LayerStyles);
                    if (missing.Count > 0)
                    {
                        host.EnsureLayers(missing);
                    }

                    ok = host.CreateEntities(drafted.Select(d => d.Spec).ToList()).Count(x => x.Ok);
                }

                ed.WriteMessage($"\nCSD-Bridge: {summary}" + (ok > 0 ? $" Drew {ok} entities." : "") + "\n");
                foreach (string w in warnings.Distinct())
                {
                    ed.WriteMessage("  Warning: " + w + "\n");
                }
            }
            catch (ArgumentException ex)
            {
                ed.WriteMessage("\nCSD-Bridge: " + ex.Message + "\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nCSD-Bridge: the ground clearance check failed: " + ex.Message + "\n");
            }
        }

        private static GroundClearanceResult? CheckSection(Editor ed, BricsCadDrawingHost host, GroundClearanceChoice c, List<DraftedEntity> drafted)
        {
            var options = new PromptEntityOptions("\nSelect the plotted long section (a polyline of the surface, drawn to the vertical exaggeration you entered): ");
            options.SetRejectMessage("\nThat isn't a line, arc or polyline.");
            options.AddAllowedClass(typeof(Curve), false);
            PromptEntityResult res = ed.GetEntity(options);
            if (res.Status != PromptStatus.OK)
            {
                return null;
            }

            IReadOnlyDictionary<string, EntitySpec> curves = host.GetCurves(new[] { res.ObjectId.Handle.ToString() });
            if (curves.Count == 0)
            {
                throw new ArgumentException("That entity's geometry can't be read as a 2D curve.");
            }

            Path2 plot = Path2.From(curves.Values.First()) ?? throw new ArgumentException("That entity has no curve geometry.");
            GroundProfile profile = GroundClearanceChecker.ProfileFromPlot(plot, c.VerticalExaggeration, c.HorizontalScale);
            GroundClearanceResult r = GroundClearanceChecker.CheckProfile(profile, c.Vehicle, c.Step, c.BothDirections, c.GroundClearance);
            if (c.DrawTemplates || c.DrawGroundings || c.DrawLabels || c.DrawEnvelope)
            {
                drafted.AddRange(GroundClearanceDrafter.DrawOnSection(r, SectionFrame.OfPlot(c.VerticalExaggeration, c.HorizontalScale), c.Layers, c.TextHeight, c.DrawTemplates, c.DrawGroundings, c.DrawLabels, c.DrawEnvelope));
            }

            return r;
        }

        private static PathClearanceResult? CheckSurface(Editor ed, BricsCadDrawingHost host, GroundClearanceChoice c, List<DraftedEntity> drafted)
        {
            var options = new PromptEntityOptions("\nSelect the line, arc or polyline the vehicle drives along (pick near the end it starts from): ");
            options.SetRejectMessage("\nThat isn't a line, arc, circle or polyline.");
            options.AddAllowedClass(typeof(Curve), false);
            PromptEntityResult res = ed.GetEntity(options);
            if (res.Status != PromptStatus.OK)
            {
                return null;
            }

            IReadOnlyDictionary<string, EntitySpec> curves = host.GetCurves(new[] { res.ObjectId.Handle.ToString() });
            if (curves.Count == 0)
            {
                throw new ArgumentException("That entity's geometry can't be read as a 2D curve.");
            }

            Path2 path = TurnPathBuilder.FromCurve(curves.Values.First(), TurnPathCommand.Wcs(ed, res.PickedPoint));
            DesignVehicle v = c.Vehicle;
            var req = new TurnPathRequest
            {
                Vehicle = v,
                Path = path,
                Reference = v.Resolve(c.Reverse ? ReferencePoint.RearAxle : ReferencePoint.FrontAxle, leftTurn: true),
                Reverse = c.Reverse,
                Step = c.Conventions.Step,
                Tolerance = c.Conventions.Tolerance,
            };
            TurnPathResult driven = TurnPathSimulator.Simulate(req);

            Func<IReadOnlyList<Point2>, IReadOnlyList<double?>> sampler = host.Sampler(c.Surface, out string surfaceName);
            ed.WriteMessage($"\nChecking against surface '{surfaceName}'...");
            PathClearanceResult r = GroundClearanceChecker.CheckAlongPath(driven, sampler, c.PoseInterval, c.GroundClearance);

            if (c.DrawGroundings || c.DrawLabels)
            {
                drafted.AddRange(GroundClearanceDrafter.DrawPlan(r, c.Layers, c.TextHeight, c.DrawLabels));
            }

            if (c.PlotSections)
            {
                PromptPointResult at = ed.GetPoint(new PromptPointOptions("\nBottom-left corner for the wheel path long section plots: "));
                if (at.Status == PromptStatus.OK)
                {
                    drafted.AddRange(GroundClearanceDrafter.DrawSectionPlots(r, TurnPathCommand.Wcs(ed, at.Value), c.PlotVerticalExaggeration, c.Layers, c.TextHeight, c.DrawEnvelope));
                }
            }

            return r;
        }
    }
}
