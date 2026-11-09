using System;
using System.Collections.Generic;
using System.Linq;
using CsdBridge.Core.Civil;
using CsdBridge.Core.Drawing;

namespace CsdBridge.Core.Vehicles
{
    /// <summary>
    /// How a long section is plotted: drawing X = Origin.X + chainage × HorizontalScale,
    /// drawing Y = Origin.Y + (level − Datum) × VerticalExaggeration.
    /// </summary>
    public sealed class SectionFrame
    {
        public Point2 Origin { get; set; }
        public double Datum { get; set; }
        public double HorizontalScale { get; set; } = 1;
        public double VerticalExaggeration { get; set; } = 1;

        public Point2 ToDrawing(double chainage, double level) =>
            new Point2(Origin.X + chainage * HorizontalScale, Origin.Y + (level - Datum) * VerticalExaggeration);

        /// <summary>The frame of an existing plot read with <see cref="GroundClearanceChecker.ProfileFromPlot"/>.</summary>
        public static SectionFrame OfPlot(double verticalExaggeration, double horizontalScale) =>
            new SectionFrame { HorizontalScale = horizontalScale, VerticalExaggeration = verticalExaggeration };
    }

    /// <summary>Draws ground clearance results: templates on a section, grounding marks, labels, wheel path plots.</summary>
    public static class GroundClearanceDrafter
    {
        /// <summary>Half-width of a drawn wheel marker (m, natural scale).</summary>
        private const double WheelMark = 0.12;

        /// <summary>Radius of a plan marker (m).</summary>
        private const double PlanMark = 0.15;

        /// <summary>
        /// Onto a long section: the template at its worst position in each direction, the ground over
        /// which the vehicle grounds, and a label.
        /// </summary>
        public static List<DraftedEntity> DrawOnSection(GroundClearanceResult r, SectionFrame f, TurnPathLayers layers, double? textHeight, bool templates = true, bool groundings = true, bool label = true, bool envelope = true)
        {
            var list = new List<DraftedEntity>();
            TurnPathLayers l = layers.Resolve(r.Vehicle.Name);
            if (envelope && r.UndersideEnvelope.Count >= 2)
            {
                list.Add(new DraftedEntity("clearance_envelope", new PolylineSpec { Layer = l.ClearanceCheck, Points = r.UndersideEnvelope.Select(u => f.ToDrawing(u.Chainage, u.Level)).ToList() }));
            }

            if (templates)
            {
                foreach (ClearancePosition? p in new[] { r.WorstForward, r.WorstBackward })
                {
                    if (p != null)
                    {
                        AddTemplate(list, p.Underside, p.RearChainage, p.RearLevel, p.FrontChainage, p.FrontLevel, p.GapChainage, p.GapGroundLevel, p.MinGap, f, l, textHeight);
                    }
                }
            }

            if (groundings)
            {
                foreach (GroundingRange g in r.Groundings)
                {
                    var touched = r.Positions.Where(p => p.Forward == g.Forward && p.RearChainage >= g.From - 1e-9 && p.RearChainage <= g.To + 1e-9 && p.MinGap < -1e-6).Select(p => p.GapChainage).ToList();
                    if (touched.Count > 0)
                    {
                        AddGroundLine(list, r.Profile, touched.Min(), touched.Max(), f, l.ClearanceFail);
                    }
                }
            }

            if (label)
            {
                ClearancePosition w = r.Worst;
                double top = w.Underside.Max(u => u.Level) + 0.3;
                list.Add(new DraftedEntity("labels", new TextSpec
                {
                    Layer = l.Labels,
                    Position = f.ToDrawing((w.Underside[0].Chainage + w.Underside[w.Underside.Length - 1].Chainage) / 2, top),
                    Text = r.Summary(),
                    Height = textHeight,
                    Align = TextAlign.Center,
                }));
            }

            return list;
        }

        /// <summary>On the plan: a ring at each side's worst point, the grounding stretches as rings along the way, and a label at the start.</summary>
        public static List<DraftedEntity> DrawPlan(PathClearanceResult r, TurnPathLayers layers, double? textHeight, bool label = true)
        {
            var list = new List<DraftedEntity>();
            TurnPathLayers l = layers.Resolve(r.Vehicle.Name);
            foreach (PathClearanceSide side in r.Sides)
            {
                foreach (PathClearanceSample s in side.Samples.Where(s => !s.OffSurface && s.MinGap < -1e-6))
                {
                    list.Add(new DraftedEntity("clearance_fail", new CircleSpec { Center = s.GapPoint, Radius = PlanMark, Layer = l.ClearanceFail }));
                }

                if (side.Worst != null)
                {
                    bool fails = side.Worst.MinGap < -1e-6;
                    list.Add(new DraftedEntity(fails ? "clearance_fail" : "clearance_check", new CircleSpec { Center = side.Worst.GapPoint, Radius = PlanMark * 2, Layer = fails ? l.ClearanceFail : l.ClearanceCheck }));
                    list.Add(new DraftedEntity("labels", new TextSpec
                    {
                        Layer = l.Labels,
                        Position = new Point2(side.Worst.GapPoint.X + PlanMark * 3, side.Worst.GapPoint.Y),
                        Text = $"{side.Name} {(fails ? "grounds" : "min")} {Math.Abs(side.Worst.MinGap) * 1000:0} mm",
                        Height = textHeight,
                    }));
                }
            }

            if (label && r.Path.Poses.Count > 0)
            {
                VehiclePose start = r.Path.Poses[0];
                list.Add(new DraftedEntity("labels", new TextSpec
                {
                    Layer = l.Labels,
                    Position = start.ToWorld(-r.Vehicle.RearOverhang - 0.5, 0),
                    Text = r.Summary(),
                    Height = textHeight,
                    Align = TextAlign.Right,
                }));
            }

            return list;
        }

        /// <summary>
        /// Long section plots of each side's wheel path with the template at its worst position,
        /// stacked downwards from <paramref name="origin"/>.
        /// </summary>
        public static List<DraftedEntity> DrawSectionPlots(PathClearanceResult r, Point2 origin, double verticalExaggeration, TurnPathLayers layers, double? textHeight, bool envelope = true)
        {
            var list = new List<DraftedEntity>();
            TurnPathLayers l = layers.Resolve(r.Vehicle.Name);
            double y = origin.Y;
            foreach (PathClearanceSide side in r.Sides)
            {
                GroundProfile? profile = side.WheelPathProfile;
                if (profile == null)
                {
                    continue;
                }

                double datum = Math.Floor(profile.Points.Min(p => p.Level)) - 0.5;
                double top = profile.Points.Max(p => p.Level);
                if (side.UndersideEnvelope.Count > 0)
                {
                    top = Math.Max(top, side.UndersideEnvelope.Max(u => u.Level));
                }

                double height = (top - datum) * verticalExaggeration;
                var f = new SectionFrame { Origin = new Point2(origin.X, y - height - 1.5), Datum = datum, VerticalExaggeration = verticalExaggeration };

                // Title, datum line and the ground.
                list.Add(new DraftedEntity("labels", new TextSpec { Layer = l.Labels, Position = new Point2(origin.X, y - 0.6), Text = $"{r.Vehicle.Name} ground clearance, {side.Name} wheel path (VE {verticalExaggeration:0.#}, datum {datum:0.0})", Height = textHeight }));
                list.Add(new DraftedEntity("clearance_section", new LineSpec { Layer = l.ClearanceCheck, Start = f.ToDrawing(profile.Start, datum), End = f.ToDrawing(profile.End, datum) }));
                list.Add(new DraftedEntity("clearance_section", new PolylineSpec { Layer = l.ClearanceCheck, Points = profile.Points.Select(p => f.ToDrawing(p.Chainage, p.Level)).ToList() }));
                if (envelope && side.UndersideEnvelope.Count >= 2)
                {
                    list.Add(new DraftedEntity("clearance_envelope", new PolylineSpec { Layer = l.ClearanceCheck, Points = side.UndersideEnvelope.Select(u => f.ToDrawing(u.Chainage, u.Level)).ToList() }));
                }

                if (side.Worst != null)
                {
                    PathClearanceSample w = side.Worst;
                    double rear = w.WheelPathChainage;
                    double front = rear + r.Vehicle.Wheelbase;
                    (double Chainage, double Level)[] underside = GroundClearanceChecker.Underside(r.Vehicle.UndersideTemplate(r.GroundClearance), rear, w.RearLevel, front, w.FrontLevel);
                    AddTemplate(list, underside, rear, w.RearLevel, front, w.FrontLevel, rear + w.GapAlong, w.GapGroundLevel, w.MinGap, f, l, textHeight);
                }

                foreach (var (from, to, _) in side.Groundings)
                {
                    var touched = side.Samples.Where(s => !s.OffSurface && s.MinGap < -1e-6 && s.Chainage >= from - 1e-9 && s.Chainage <= to + 1e-9)
                        .Select(s => s.WheelPathChainage + s.GapAlong).ToList();
                    if (touched.Count > 0)
                    {
                        AddGroundLine(list, profile, touched.Min(), touched.Max(), f, l.ClearanceFail);
                    }
                }

                y = f.Origin.Y - 2.5;
            }

            return list;
        }

        /// <summary>The template on the section: underside, wheel markers, and the gap at its tightest point.</summary>
        private static void AddTemplate(List<DraftedEntity> list, (double Chainage, double Level)[] underside, double rear, double rearZ, double front, double frontZ,
            double gapChainage, double gapGround, double gap, SectionFrame f, TurnPathLayers l, double? textHeight)
        {
            string layer = gap < -1e-6 ? l.ClearanceFail : l.ClearanceCheck;
            string group = gap < -1e-6 ? "clearance_fail" : "clearance_template";
            list.Add(new DraftedEntity(group, new PolylineSpec { Layer = layer, Points = underside.Select(u => f.ToDrawing(u.Chainage, u.Level)).ToList() }));
            foreach (var (c, z) in new[] { (rear, rearZ), (front, frontZ) })
            {
                // A knife-edge wheel: a small triangle standing on the contact point.
                list.Add(new DraftedEntity(group, new PolylineSpec
                {
                    Layer = layer,
                    Closed = true,
                    Points = new List<Point2>
                    {
                        f.ToDrawing(c, z),
                        f.ToDrawing(c - WheelMark, z + GroundClearanceChecker.Interpolate(underside, c) - z),
                        f.ToDrawing(c + WheelMark, z + GroundClearanceChecker.Interpolate(underside, c) - z),
                    },
                }));
            }

            // The tightest gap, as a tick and its size.
            double undersideZ = GroundClearanceChecker.Interpolate(underside, gapChainage);
            list.Add(new DraftedEntity(group, new LineSpec { Layer = layer, Start = f.ToDrawing(gapChainage, gapGround), End = f.ToDrawing(gapChainage, undersideZ) }));
            list.Add(new DraftedEntity("labels", new TextSpec
            {
                Layer = l.Labels,
                Position = f.ToDrawing(gapChainage + 0.15, Math.Max(gapGround, undersideZ) + 0.05),
                Text = gap < -1e-6 ? $"grounds {-gap * 1000:0} mm" : $"{gap * 1000:0} mm",
                Height = textHeight,
            }));
        }

        private static void AddGroundLine(List<DraftedEntity> list, GroundProfile profile, double from, double to, SectionFrame f, string layer)
        {
            var pts = new List<Point2> { f.ToDrawing(from, profile.LevelAt(from)) };
            foreach (double c in profile.ChainagesBetween(from, to))
            {
                pts.Add(f.ToDrawing(c, profile.LevelAt(c)));
            }

            pts.Add(f.ToDrawing(to, profile.LevelAt(to)));
            if (pts.Count >= 2 && (pts[0].X != pts[pts.Count - 1].X || pts[0].Y != pts[pts.Count - 1].Y))
            {
                list.Add(new DraftedEntity("clearance_fail", new PolylineSpec { Layer = layer, Points = pts }));
            }
            else
            {
                list.Add(new DraftedEntity("clearance_fail", new CircleSpec { Layer = layer, Center = pts[0], Radius = WheelMark }));
            }
        }
    }
}
