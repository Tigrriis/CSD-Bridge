using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Bricscad.ApplicationServices;
using Bricscad.EditorInput;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Vehicles;
using Teigha.DatabaseServices;
using Teigha.Geometry;

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// AITURN draws an AS/NZS 2890.1 B85/B99 swept path: the dialog collects the options, the
    /// command prompts for the geometry (start point and direction, a curve to follow, or
    /// destinations to drive to), and the result is drawn through the same drawing host as
    /// Claude's tools, so it lands on the configured layers and is one UNDO step.
    ///
    /// Everything that defines the path is kept as a <see cref="ManoeuvreRecord"/> and saved in
    /// the drawing with the entities, so AITURNEDIT can change the settings or the geometry and
    /// regenerate it.
    /// </summary>
    internal static class TurnPathCommand
    {
        public static void Run(Document doc)
        {
            Editor ed = doc.Editor;
            TurnPathConventions conventions = LoadConventions(ed, out string savePath);
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
                var record = new ManoeuvreRecord { Id = ManoeuvreStore.NewId(), Mode = ModeName(choice.Mode) };
                Apply(choice, record);
                if (!PromptGeometry(ed, record, choice, continueDrive: false))
                {
                    return;
                }

                PromptObstructions(doc, record);
                Execute(doc, record, conventions, null, null);
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

        /// <summary>AITURNEDIT: pick a turning path, change its settings or geometry, and regenerate it.</summary>
        public static void Edit(Document doc)
        {
            Editor ed = doc.Editor;
            PromptEntityResult pick = ed.GetEntity(new PromptEntityOptions("\nSelect a turning path to edit (any part of it, or its block): "));
            if (pick.Status != PromptStatus.OK)
            {
                return;
            }

            try
            {
                ManoeuvreRecord? record = ManoeuvreStore.Find(doc, pick.ObjectId, out ManoeuvreStore.PickedBlock? block, out string? problem);
                if (record == null)
                {
                    ed.WriteMessage("\nCSD-Bridge: " + problem + "\n");
                    return;
                }

                // What to erase once the new version is ready: the path exactly as it was saved.
                ManoeuvreRecord old = ManoeuvreRecord.FromJson(record.ToJson());

                // A block that was moved or rotated takes its manoeuvre with it.
                if (block != null && record.BlockBase != null && record.BlockBase.Length >= 2 && record.Mode != "follow")
                {
                    double dx = block.X - record.BlockBase[0], dy = block.Y - record.BlockBase[1];
                    if (Math.Abs(dx) + Math.Abs(dy) > 1e-6 || Math.Abs(block.Rotation) > 1e-9)
                    {
                        record.Transform(new Point2(record.BlockBase[0], record.BlockBase[1]), block.Rotation, dx, dy);
                        ed.WriteMessage("\nThe block has been moved since it was drawn; the path is regenerated where it is now.");
                    }
                }

                TurnPathConventions conventions = LoadConventions(ed, out string savePath);
                if (!conventions.Vehicles.ContainsKey(record.Vehicle))
                {
                    ed.WriteMessage($"\nCSD-Bridge: the {record.Vehicle} vehicle this path was drawn with isn't in turn-paths.json any more; choose another in the dialog.");
                }

                TurnPathChoice choice;
                using (var dialog = new TurnPathDialog(conventions, savePath, record))
                {
                    if (dialog.ShowDialog() != DialogResult.OK)
                    {
                        return;
                    }

                    choice = dialog.Choice;
                }

                Apply(choice, record);

                var options = new PromptKeywordOptions("\nKeep the geometry and regenerate, pick it again, or (drive) continue from the end");
                options.Keywords.Add("Regenerate");
                options.Keywords.Add("Pick");
                if (record.Mode == "drive")
                {
                    options.Keywords.Add("Continue");
                }

                options.Keywords.Default = "Regenerate";
                options.AllowNone = true;
                PromptResult what = ed.GetKeywords(options);
                if (what.Status != PromptStatus.OK && what.Status != PromptStatus.None)
                {
                    return;
                }

                string action = what.Status == PromptStatus.OK ? what.StringResult : "Regenerate";
                if (action == "Pick")
                {
                    if (!PromptGeometry(ed, record, choice, continueDrive: false))
                    {
                        return;
                    }
                }
                else if (action == "Continue")
                {
                    if (!PromptGeometry(ed, record, choice, continueDrive: true))
                    {
                        return;
                    }
                }
                else if (record.Mode == "drive" && !choice.ArrivalHeading)
                {
                    // Arrival directions switched off in the dialog: the destinations become free again.
                    foreach (DriveStop stop in record.Stops)
                    {
                        stop.HeadingDeg = null;
                    }
                }

                PromptObstructions(doc, record);
                Execute(doc, record, conventions, old, block);
            }
            catch (ArgumentException ex)
            {
                ed.WriteMessage("\nCSD-Bridge: " + ex.Message + " The turning path was left as it was.\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nCSD-Bridge: editing the turning path failed: " + ex.Message + "\n");
            }
        }

        private static TurnPathConventions LoadConventions(Editor ed, out string savePath)
        {
            BridgeSettings settings = AgentRunner.Settings;
            TurnPathConventions? conventions = settings.LoadTurnPathConventions(out string? loadError);
            if (conventions == null)
            {
                ed.WriteMessage($"\nCSD-Bridge: the turning path conventions couldn't be loaded ({loadError}); using the built-in B99/B85 defaults.\n");
                conventions = TurnPathConventions.Default();
            }

            savePath = string.IsNullOrWhiteSpace(settings.TurnPathsFile)
                ? BridgeSettings.UserTurnPathsPath
                : Environment.ExpandEnvironmentVariables(settings.TurnPathsFile!);
            return conventions;
        }

        private static string ModeName(TurnPathChoice.ManoeuvreMode mode) =>
            mode == TurnPathChoice.ManoeuvreMode.Template ? "template" : mode == TurnPathChoice.ManoeuvreMode.Follow ? "follow" : "drive";

        /// <summary>Copies the dialog's settings onto a record, leaving its manoeuvre and geometry alone.</summary>
        private static void Apply(TurnPathChoice c, ManoeuvreRecord r)
        {
            r.Vehicle = c.Vehicle.Name;
            if (r.Mode != "follow")
            {
                r.Radius = c.Radius;
            }

            r.TurnAngles = c.TurnAngles.ToList();
            r.Left = c.Left;
            r.LeadIn = c.LeadIn;
            r.LeadOut = c.LeadOut;
            r.Reference = c.Reference == ReferencePoint.RearAxle ? "rear_axle" : c.Reference == ReferencePoint.FrontAxle ? "front_axle" : "outer_front_wheel";
            r.Reverse = c.Reverse;
            r.Turns = c.Turns == TurnPathBuilder.DriveTurns.Corner ? "corner" : "arc";
            r.ClearanceInside = c.ClearanceInside;
            r.ClearanceOutside = c.ClearanceOutside;
            r.Layers = c.Style.Layers.Clone();
            r.Draw = c.Style.Draw.Clone();
            r.OutlineInterval = c.Style.OutlineInterval;
            r.TextHeight = c.Style.TextHeight;
            r.CheckConflicts = c.CheckConflicts;
            r.ObstructionLayers = c.ObstructionLayers.ToList();
        }

        // ---------------------------------------------------------------- geometry

        /// <summary>Prompts for the geometry the record's manoeuvre needs. False when the user cancels.</summary>
        private static bool PromptGeometry(Editor ed, ManoeuvreRecord r, TurnPathChoice c, bool continueDrive)
        {
            DesignVehicle v = c.Vehicle;
            var preview = new PreviewBuilder();
            switch (r.Mode)
            {
                case "template":
                {
                    if (!PickStartAndHeading(ed, "\nStart point (centre of the rear axle of the parked vehicle): ", out Point2 s, out double heading,
                        (at, cursor) => preview.Specs(TurnPathBuilder.Template(new TurnTemplate
                        {
                            Vehicle = v,
                            Start = at,
                            Heading = Math.Atan2(cursor.Y - at.Y, cursor.X - at.X),
                            Radius = r.Radius,
                            TurnAngleDeg = r.TurnAngles.Max(),
                            Left = r.Left,
                            LeadIn = r.LeadIn,
                            LeadOut = r.LeadOut,
                            Reference = r.ReferencePointKind,
                        }))))
                    {
                        return false;
                    }

                    r.Start = new[] { s.X, s.Y };
                    r.HeadingDeg = heading * 180 / Math.PI;
                    return true;
                }

                case "follow":
                {
                    var options = new PromptEntityOptions("\nSelect the line, arc or polyline the vehicle follows (pick near the end it starts from): ");
                    options.SetRejectMessage("\nThat isn't a line, arc, circle or polyline.");
                    options.AddAllowedClass(typeof(Curve), false);
                    PromptEntityResult res = ed.GetEntity(options);
                    if (res.Status != PromptStatus.OK)
                    {
                        return false;
                    }

                    Point2 near = Wcs(ed, res.PickedPoint);
                    r.FollowHandle = res.ObjectId.Handle.ToString();
                    r.FollowNear = new[] { near.X, near.Y };
                    return true;
                }

                case "drive":
                    return Drive(ed, r, c, preview, continueDrive);

                default:
                    return false;
            }
        }

        /// <summary>
        /// Drive mode: each click is where the vehicle goes next, forwards when the destination is
        /// ahead and in reverse when it is behind. With the arrival direction on, a second pick
        /// says which way the vehicle faces there (turn, straight, turn). Every leg keeps its own
        /// swept path, so a three-point turn is three legs. U takes the last leg back, D switches
        /// the arrival direction on or off, Enter finishes.
        /// </summary>
        private static bool Drive(Editor ed, ManoeuvreRecord r, TurnPathChoice c, PreviewBuilder preview, bool continueDrive)
        {
            DesignVehicle v = c.Vehicle;
            TurnPathConventions conv = c.Conventions;
            var legs = new List<(DriveStop Stop, VehiclePose End, List<EntitySpec> Graphics)>();
            if (continueDrive)
            {
                // The existing legs, as drawn: the new ones carry on from where they end.
                ManoeuvrePlan plan = ManoeuvrePlanner.Plan(r, conv);
                for (int i = 0; i < plan.Legs.Count; i++)
                {
                    TurnPathResult result = plan.Legs[i].Result;
                    legs.Add((r.Stops[i], result.Poses[result.Poses.Count - 1], preview.Specs(result, startVehicle: false)));
                }
            }
            else
            {
                if (!PickStartAndHeading(ed, "\nStart point (centre of the rear axle of the vehicle): ", out Point2 start, out double heading,
                    (at, cursor) => PreviewBuilder.Vehicle(v, new VehiclePose(at, Math.Atan2(cursor.Y - at.Y, cursor.X - at.X)))))
                {
                    return false;
                }

                r.Start = new[] { start.X, start.Y };
                r.HeadingDeg = heading * 180 / Math.PI;
            }

            var startPose = new VehiclePose(r.StartPoint, r.HeadingDeg * Math.PI / 180);
            int kept = legs.Count; // legs that were already there can't be undone past
            bool askHeading = c.ArrivalHeading;
            while (true)
            {
                VehiclePose from = legs.Count > 0 ? legs[legs.Count - 1].End : startPose;
                List<EntitySpec> fixedGraphics = PreviewBuilder.Vehicle(v, startPose);
                fixedGraphics.AddRange(legs.SelectMany(l => l.Graphics));

                bool canUndo = legs.Count > kept;
                string prompt = legs.Count == 0
                    ? "\nWhere the vehicle drives to (ahead = forward, behind = reverse) or [Direction]: "
                    : canUndo ? "\nNext destination or [Undo/Direction] (Enter to finish): " : "\nNext destination or [Direction] (Enter to finish): ";
                var jig = new TurnPathJig(prompt, new Point3d(from.RearAxle.X, from.RearAxle.Y, 0), canUndo ? new[] { "Undo", "Direction" } : new[] { "Direction" },
                    cursor =>
                    {
                        TurnPathBuilder.DriveLeg? leg = TurnPathBuilder.DriveTo(v, from, cursor, r.Radius, null, r.TurnsKind);
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
                    if (string.Equals(res.StringResult, "Direction", StringComparison.OrdinalIgnoreCase))
                    {
                        askHeading = !askHeading;
                        ed.WriteMessage(askHeading
                            ? "\n  Arrival direction on: after each destination, pick which way the vehicle faces there."
                            : "\n  Arrival direction off: the vehicle arrives however the turn leaves it.");
                    }
                    else if (canUndo)
                    {
                        legs.RemoveAt(legs.Count - 1);
                    }

                    continue;
                }

                if (res.Status != PromptStatus.OK)
                {
                    return false;
                }

                Point2 target = jig.Point2;
                var stop = new DriveStop { Point = new[] { target.X, target.Y } };
                if (askHeading)
                {
                    var facing = new TurnPathJig("\nDirection the vehicle faces on arrival (pick a point, or Enter to leave it free): ", jig.Point, null,
                        cursor =>
                        {
                            if (Distance(target, cursor) < 1e-6)
                            {
                                return null;
                            }

                            TurnPathBuilder.DriveLeg? leg = TurnPathBuilder.DriveToPose(v, from, target, Math.Atan2(cursor.Y - target.Y, cursor.X - target.X), r.Radius);
                            return leg == null ? null : preview.Specs(leg.Request, startVehicle: false);
                        },
                        fixedGraphics);
                    PromptResult dir = ed.Drag(facing);
                    if (dir.Status == PromptStatus.OK && Distance(target, facing.Point2) > 1e-6)
                    {
                        stop.HeadingDeg = Math.Atan2(facing.Point2.Y - target.Y, facing.Point2.X - target.X) * 180 / Math.PI;
                    }
                    else if (dir.Status != PromptStatus.None)
                    {
                        continue; // Esc: choose the destination again
                    }
                }

                TurnPathBuilder.DriveLeg? chosen = ManoeuvrePlanner.Leg(v, from, stop, r.Radius, r.TurnsKind);
                if (chosen == null)
                {
                    ed.WriteMessage(stop.HeadingDeg != null
                        ? "\nThe vehicle can't arrive there facing that way in one leg (it would need a loop, or the turns overlap). Pick another direction, or get closer in an earlier leg."
                        : "\nThe vehicle can't get there in one leg: the point is too close, inside its turning circle, or more than a half turn away. Click further away, or drive past and reverse.");
                    continue;
                }

                // Simulate now so the next leg starts from the real end pose, and keep its graphics on screen.
                TurnPathRequest req = chosen.Request;
                req.Step = conv.Step;
                req.Tolerance = conv.Tolerance;
                req.ClearanceInside = r.ClearanceInside;
                req.ClearanceOutside = r.ClearanceOutside;
                TurnPathResult result = TurnPathSimulator.Simulate(req);
                ed.WriteMessage($"\n  Leg {legs.Count + 1}: {ManoeuvrePlanner.Describe(chosen)}.");
                legs.Add((stop, result.Poses[result.Poses.Count - 1], preview.Specs(result, startVehicle: false)));
            }

            if (legs.Count == 0)
            {
                ed.WriteMessage("\nCSD-Bridge: no destinations were clicked, so nothing was drawn.\n");
                return false;
            }

            r.Stops = legs.Select(l => l.Stop).ToList();
            return true;
        }

        /// <summary>Start point, then the direction of travel from a second point, with a live preview.</summary>
        private static bool PickStartAndHeading(Editor ed, string startPrompt, out Point2 start, out double heading, Func<Point2, Point2, List<EntitySpec>?> previewAt)
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

        /// <summary>With the conflict check on and no layers given, asks for objects on the obstruction layers.</summary>
        private static void PromptObstructions(Document doc, ManoeuvreRecord r)
        {
            if (!r.CheckConflicts || r.ObstructionLayers.Count > 0)
            {
                return;
            }

            Editor ed = doc.Editor;
            var options = new PromptSelectionOptions { MessageForAdding = "\nSelect objects on the obstruction layers (kerbs, walls, columns), or Enter to skip the conflict check: " };
            PromptSelectionResult res = ed.GetSelection(options);
            if (res.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nNo obstruction layers, so conflicts aren't checked.");
                return;
            }

            r.ObstructionLayers = ObstructionScanner.LayersOf(doc, res.Value.GetObjectIds());
            ed.WriteMessage($"\nChecking against: {string.Join(", ", r.ObstructionLayers)}. To keep these, type them in the dialog and use Save as defaults.");
        }

        // ------------------------------------------------------------------ drawing

        /// <summary>
        /// Plans, checks and draws a record, saves it with its entities, and reports. When
        /// <paramref name="old"/> is given its entities are erased first, but only once the new
        /// version has been planned successfully.
        /// </summary>
        private static void Execute(Document doc, ManoeuvreRecord r, TurnPathConventions conv, ManoeuvreRecord? old, ManoeuvreStore.PickedBlock? pickedBlock)
        {
            Editor ed = doc.Editor;
            var host = new BricsCadDrawingHost(doc);
            ManoeuvrePlan plan = ManoeuvrePlanner.Plan(r, conv, handle => host.GetCurves(new[] { handle }).Values.FirstOrDefault());
            DesignVehicle v = plan.Vehicle;
            var warnings = new List<string>(plan.Warnings);
            TurnPathLayers layers = r.Layers.Resolve(v.Name);

            ConflictReport? conflicts = null;
            if (r.CheckConflicts && r.ObstructionLayers.Count > 0)
            {
                var (min, max) = ConflictChecker.Window(plan.Results);
                conflicts = ConflictChecker.Check(plan.Results, host.Obstructions(r.ObstructionLayers, min, max, ManoeuvrePlanner.OwnLayers(r, conv)));
            }

            List<DraftedEntity> drafted = ManoeuvrePlanner.Draft(plan, r, conv);
            if (conflicts != null)
            {
                drafted.AddRange(ConflictChecker.Draw(conflicts, layers.Conflict, r.TextHeight));
            }

            if (drafted.Count == 0)
            {
                ed.WriteMessage("\nCSD-Bridge: nothing to draw (every part is switched off).\n");
                return;
            }

            if (old != null)
            {
                ManoeuvreStore.Erase(doc, old, pickedBlock);
            }

            DrawingInfo info = host.GetDrawingInfo();
            List<LayerSpec> missing = TurnPathDrafter.MissingLayers(
                drafted.Select(d => d.Spec.Layer).Where(l => l != null).Select(l => l!).Concat(r.Draw.Block ? new[] { layers.Block } : new string[0]),
                info.Layers.Select(l => l.Name),
                conv.LayerStyles);
            if (missing.Count > 0)
            {
                host.EnsureLayers(missing);
            }

            IReadOnlyList<ItemResult> created = host.CreateEntities(drafted.Select(d => d.Spec).ToList());
            List<string> handles = created.Where(x => x.Ok && x.Handle != null).Select(x => x.Handle!).ToList();
            List<string> failed = created.Where(x => !x.Ok).Select(x => x.Error ?? "unknown error").Distinct().ToList();
            r.Handles = handles;
            r.BlockName = null;
            r.BlockHandle = null;
            r.BlockBase = null;

            string blockNote = "";
            if (r.Draw.Block && handles.Count > 0)
            {
                try
                {
                    Point2 basePoint = plan.StartPoint;
                    BlockResult block = host.MakeBlock(handles, $"{v.Name} turning path", basePoint, layers.Block);
                    r.BlockName = block.BlockName;
                    r.BlockHandle = block.Handle;
                    r.BlockBase = new[] { basePoint.X, basePoint.Y };
                    blockNote = $"\n  Made block '{block.BlockName}' ({block.EntityCount} entities) on layer {layers.Block}, inserted at the vehicle's start point.";
                }
                catch (System.Exception ex)
                {
                    blockNote = "\n  Couldn't make the block (the entities are left as drawn): " + ex.Message;
                }
            }

            try
            {
                ManoeuvreStore.Save(doc, r);
            }
            catch (System.Exception ex)
            {
                warnings.Add("The path was drawn, but its settings couldn't be saved for AITURNEDIT: " + ex.Message);
            }

            ed.WriteMessage($"\nCSD-Bridge: {(old != null ? "regenerated" : "drew")} the {v.Name} turning path, {handles.Count} entities" +
                            (missing.Count > 0 ? $" (created layers: {string.Join(", ", missing.Select(m => m.Name))})" : "") + ".");
            int n = 0;
            foreach (PlannedLeg leg in plan.Legs)
            {
                n++;
                TurnPathResult result = leg.Result;
                string tightest = double.IsInfinity(result.TightestRadius) ? "straight" : $"tightest turn R {result.TightestRadius:0.00} m at the outer front wheel";
                string name = leg.Description != null ? $"Leg {n} ({leg.Description})" : leg.Label;
                ed.WriteMessage($"\n  {name}: path {result.PathLength:0.0} m, net turn {result.NetTurnDeg:0} deg, {tightest} (minimum {v.MinTurnRadius:0.0} m)" +
                                $", clearances {result.ClearanceLeft * 1000:0} mm left / {result.ClearanceRight * 1000:0} mm right");
            }

            if (conflicts != null)
            {
                ed.WriteMessage("\n  " + conflicts.Summary());
                foreach (Conflict k in conflicts.Conflicts.OrderByDescending(k => k.Shortfall).Take(10))
                {
                    ed.WriteMessage($"\n    {(plan.Legs.Count > 1 ? $"Leg {k.Leg}: " : "")}{k.Describe()} at {k.At.X:0.00}, {k.At.Y:0.00}");
                }

                if (conflicts.Conflicts.Count > 10)
                {
                    ed.WriteMessage($"\n    ...and {conflicts.Conflicts.Count - 10} more, all marked on layer {layers.Conflict}.");
                }
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

            ed.WriteMessage("\n  Change it later with AITURNEDIT.\n");
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
