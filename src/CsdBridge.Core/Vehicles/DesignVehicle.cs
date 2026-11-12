using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using CsdBridge.Core.Drawing;

namespace CsdBridge.Core.Vehicles
{
    /// <summary>
    /// A trailer towed behind a design vehicle or behind another trailer: a semi-trailer on a
    /// fifth wheel, or a dolly on a drawbar. Dimensions in metres.
    /// </summary>
    public sealed class TrailerUnit
    {
        public string Name { get; set; } = "trailer";

        /// <summary>Overall length of the trailer's body.</summary>
        public double Length { get; set; }

        public double Width { get; set; }

        /// <summary>From the coupling (kingpin, or the eye of a drawbar) back to the centre of the trailer's axle group.</summary>
        public double Wheelbase { get; set; }

        /// <summary>
        /// How far the body reaches ahead of the coupling: the kingpin's setback from the front of a
        /// semi-trailer. Negative when the body starts behind the coupling (a dolly on a drawbar).
        /// </summary>
        public double FrontOverhang { get; set; }

        /// <summary>
        /// Where the next trailer couples on, measured forward from the centre of this trailer's axle
        /// group (negative: behind it). Only used when another trailer follows.
        /// </summary>
        public double Hitch { get; set; }

        /// <summary>Distance between the wheel centres across an axle. 0: taken as the width less 0.4 m.</summary>
        public double Track { get; set; }

        /// <summary>From the centre of the axle group to the rear of the body.</summary>
        [JsonIgnore]
        public double RearOverhang => Length - FrontOverhang - Wheelbase;

        /// <summary>
        /// The trailer as a body in its own frame: origin at the centre of its axle group, X forward,
        /// so its "front axle" is the coupling. Square cornered.
        /// </summary>
        internal DesignVehicle Body() => new DesignVehicle
        {
            Name = Name,
            Length = Length,
            Width = Width,
            Wheelbase = Wheelbase,
            FrontOverhang = FrontOverhang,
            RearOverhang = RearOverhang,
            EffectiveFrontOverhang = FrontOverhang,
            EffectiveRearOverhang = RearOverhang,
            EffectiveWidth = Width,
            Track = Track > 0 ? Track : Math.Max(0.1, Width - 0.4),
            MinTurnRadius = double.MaxValue,
        };

        public TrailerUnit Clone() => (TrailerUnit)MemberwiseClone();
    }

    /// <summary>
    /// Trailers: a design vehicle can tow them (a prime mover and semi-trailer, a B-double). Each
    /// follows the unit ahead the way a vehicle's rear axle follows its front: its axle group can't
    /// slip sideways, so it is dragged round behind its coupling and cuts inside the path of the
    /// unit ahead (off-tracking).
    /// </summary>
    public sealed partial class DesignVehicle
    {
        /// <summary>
        /// Where a trailer couples on (the fifth wheel, or a tow coupling), measured forward from
        /// the rear axle; negative when it is behind the axle. With a group of rear axles, measure
        /// everything from the centre of the group.
        /// </summary>
        public double Hitch { get; set; }

        /// <summary>The trailers towed, front to back. Empty for a rigid vehicle.</summary>
        public List<TrailerUnit> Trailers { get; set; } = new List<TrailerUnit>();

        [JsonIgnore]
        public bool IsArticulated => Trailers.Count > 0;

        /// <summary>
        /// Overall length with any trailers straight behind: from the front of the vehicle to the
        /// rearmost point of the combination.
        /// </summary>
        [JsonIgnore]
        public double OverallLength
        {
            get
            {
                double front = Wheelbase + FrontOverhang, rear = -RearOverhang;
                double coupling = Hitch;      // X of the coupling the next trailer hangs on, in the vehicle's frame
                foreach (TrailerUnit t in Trailers)
                {
                    double axle = coupling - t.Wheelbase;
                    rear = Math.Min(rear, axle - t.RearOverhang);
                    front = Math.Max(front, coupling + t.FrontOverhang);
                    coupling = axle + t.Hitch;
                }

                return front - rear;
            }
        }

        /// <summary>
        /// Where each trailer sits with the whole vehicle in a straight line: the pose of each
        /// trailer's axle group behind the vehicle at <paramref name="pose"/>.
        /// </summary>
        public List<VehiclePose> StraightTrailerPoses(VehiclePose pose)
        {
            var poses = new List<VehiclePose>();
            double coupling = Hitch;
            foreach (TrailerUnit t in Trailers)
            {
                double axle = coupling - t.Wheelbase;
                poses.Add(new VehiclePose(pose.ToWorld(axle, 0), pose.Heading));
                coupling = axle + t.Hitch;
            }

            return poses;
        }

        /// <summary>
        /// Fills in what a truck's definition can leave out: the effective overhangs and width (a
        /// square-cornered body sweeps with its real corners).
        /// </summary>
        public void FillDefaults()
        {
            if (EffectiveWidth <= 0)
            {
                EffectiveWidth = Width;
            }

            if (EffectiveFrontOverhang <= 0)
            {
                EffectiveFrontOverhang = FrontOverhang;
            }

            if (EffectiveRearOverhang <= 0)
            {
                EffectiveRearOverhang = RearOverhang;
            }

            for (int i = 0; i < Trailers.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(Trailers[i].Name) || Trailers[i].Name == "trailer")
                {
                    Trailers[i].Name = Trailers.Count == 1 ? "trailer" : $"trailer {i + 1}";
                }
            }
        }
    }

    /// <summary>
    /// A design vehicle for swept path (turning path) analysis. Dimensions in metres.
    ///
    /// The vehicle frame used throughout: origin at the centre of the rear axle, X forward,
    /// Y to the left. The swept path is traced by the body's *effective* extremities
    /// (AS/NZS 2890.1:2004 Figures B1 and B2, "relevant longitudinal dimensions that affect
    /// swept path"): the rounded front and rear corners reach the effective overhangs at the
    /// effective width, and the overall overhangs only at the centreline. A truck's square body
    /// has effective dimensions equal to its real ones.
    /// </summary>
    public sealed partial class DesignVehicle
    {
        public string Name { get; set; } = "";

        public string? Description { get; set; }

        /// <summary>Overall length.</summary>
        public double Length { get; set; }

        /// <summary>Overall width of the body (sides).</summary>
        public double Width { get; set; }

        public double Wheelbase { get; set; }

        /// <summary>Front axle to the front of the vehicle (overall).</summary>
        public double FrontOverhang { get; set; }

        /// <summary>Rear axle to the rear of the vehicle (overall).</summary>
        public double RearOverhang { get; set; }

        /// <summary>Front axle to the effective front corners.</summary>
        public double EffectiveFrontOverhang { get; set; }

        /// <summary>Rear axle to the effective rear corners.</summary>
        public double EffectiveRearOverhang { get; set; }

        /// <summary>Width across the effective corners.</summary>
        public double EffectiveWidth { get; set; }

        /// <summary>Distance between the wheel centres across an axle (for wheel paths and the turn radius).</summary>
        public double Track { get; set; }

        /// <summary>Minimum turn radius, measured to the outer front wheel (AS/NZS 2890.1 Paragraph B3.1).</summary>
        public double MinTurnRadius { get; set; }

        /// <summary>Manoeuvring clearance added on the inside of the turn (m).</summary>
        public double ClearanceInside { get; set; } = 0.3;

        /// <summary>Manoeuvring (and, for B99, circulation) clearance added on the outside of the turn (m).</summary>
        public double ClearanceOutside { get; set; } = 0.3;

        /// <summary>Minimum ground clearance under the body between the axles (m); AS/NZS 2890.1 B5: 120 mm, fully loaded.</summary>
        public double GroundClearance { get; set; } = 0.12;

        /// <summary>Angle of the underside ahead of the front axle, degrees above the chassis line (B5: 10 degrees).</summary>
        public double ApproachAngleDeg { get; set; } = 10;

        /// <summary>Angle of the underside behind the rear axle, degrees above the chassis line (B5: 10 degrees).</summary>
        public double DepartureAngleDeg { get; set; } = 10;

        /// <summary>Length of a drawn wheel (tyre footprint), for the vehicle outline.</summary>
        public double WheelLength { get; set; } = 0.65;

        public double WheelWidth { get; set; } = 0.22;

        [JsonIgnore]
        public double FrontOverhangRounding => Math.Max(0, FrontOverhang - EffectiveFrontOverhang);

        [JsonIgnore]
        public double RearOverhangRounding => Math.Max(0, RearOverhang - EffectiveRearOverhang);

        /// <summary>
        /// Length of the corner rounding: how far behind the effective corner the side reaches full
        /// width. The standard doesn't dimension it; a quarter metre on a B99 reads like its figures.
        /// </summary>
        [JsonIgnore]
        public double CornerLength => Math.Min(0.3, Math.Max(0.05, 0.25 * Wheelbase / 3.05));

        /// <summary>Half the difference between the overall and effective widths: how far each corner is set in.</summary>
        [JsonIgnore]
        public double CornerInset => Math.Max(0, (Width - EffectiveWidth) / 2);

        /// <summary>
        /// Radius of the corner arc that is tangent to the side and passes through the effective
        /// corner (0 when the corners aren't set in).
        /// </summary>
        [JsonIgnore]
        public double CornerRadius
        {
            get
            {
                double c = CornerLength, d = CornerInset;
                return d < 1e-9 ? 0 : (c * c + d * d) / (2 * d);
            }
        }

        /// <summary>Angle swept by a corner arc, radians.</summary>
        [JsonIgnore]
        public double CornerSweep => CornerInset < 1e-9 ? 0 : 2 * Math.Atan2(CornerInset, CornerLength);

        /// <summary>AS/NZS 2890.1:2004 Figure B1: the 99.8th percentile car.</summary>
        public static DesignVehicle B99 => new DesignVehicle
        {
            Name = "B99",
            Description = "AS/NZS 2890.1:2004 Figure B1, 99.8th percentile car (5.2 x 1.94 m)",
            Length = 5.2,
            Width = 1.94,
            Wheelbase = 3.05,
            FrontOverhang = 1.2,
            RearOverhang = 0.95,
            EffectiveFrontOverhang = 1.15,
            EffectiveRearOverhang = 0.8,
            EffectiveWidth = 1.84,
            Track = 1.62,
            MinTurnRadius = 6.3,
            ClearanceInside = 0.3,
            ClearanceOutside = 0.6,
        };

        /// <summary>AS/NZS 2890.1:2004 Figure B2: the 85th percentile car.</summary>
        public static DesignVehicle B85 => new DesignVehicle
        {
            Name = "B85",
            Description = "AS/NZS 2890.1:2004 Figure B2, 85th percentile car (4.91 x 1.87 m)",
            Length = 4.91,
            Width = 1.87,
            Wheelbase = 2.8,
            FrontOverhang = 1.19,
            RearOverhang = 0.92,
            EffectiveFrontOverhang = 1.1,
            EffectiveRearOverhang = 0.75,
            EffectiveWidth = 1.77,
            Track = 1.57,
            MinTurnRadius = 5.8,
            ClearanceInside = 0.3,
            ClearanceOutside = 0.3,
        };

        public static IReadOnlyList<DesignVehicle> Standard => new[] { B99, B85 };

        /// <summary>
        /// The ground clearance template of AS/NZS 2890.1 Figure C1 in the chassis frame: X along
        /// the vehicle from the rear axle, Z up from the line through the wheel contact points.
        /// Rear extremity, rear axle, front axle, front extremity; the underside is straight between
        /// the axles and rises at the departure/approach angles over the overhangs.
        /// </summary>
        public (double X, double Z)[] UndersideTemplate(double? groundClearance = null)
        {
            double h = groundClearance ?? GroundClearance;
            double rear = h + RearOverhang * Math.Tan(DepartureAngleDeg * Math.PI / 180);
            double front = h + FrontOverhang * Math.Tan(ApproachAngleDeg * Math.PI / 180);
            return new[] { (-RearOverhang, rear), (0.0, h), (Wheelbase, h), (Wheelbase + FrontOverhang, front) };
        }

        /// <summary>Problems with the dimensions, or an empty list.</summary>
        public List<string> Validate()
        {
            var problems = new List<string>();
            void Positive(double v, string what)
            {
                if (!(v > 0))
                {
                    problems.Add($"{what} must be positive");
                }
            }

            Positive(Length, "length");
            Positive(Width, "width");
            Positive(Wheelbase, "wheelbase");
            Positive(Track, "track");
            Positive(MinTurnRadius, "min_turn_radius");
            if (FrontOverhang < 0 || RearOverhang < 0 || EffectiveFrontOverhang < 0 || EffectiveRearOverhang < 0)
            {
                problems.Add("overhangs can't be negative");
            }

            if (EffectiveFrontOverhang > FrontOverhang + 1e-9 || EffectiveRearOverhang > RearOverhang + 1e-9)
            {
                problems.Add("effective overhangs can't exceed the overall overhangs");
            }

            if (EffectiveWidth > Width + 1e-9 || EffectiveWidth <= 0)
            {
                problems.Add("effective_width must be positive and no more than the width");
            }

            if (Track > Width)
            {
                problems.Add("track can't exceed the width");
            }

            if (MinTurnRadius <= Wheelbase)
            {
                problems.Add("min_turn_radius must be larger than the wheelbase");
            }

            if (GroundClearance < 0 || ApproachAngleDeg < 0 || ApproachAngleDeg >= 90 || DepartureAngleDeg < 0 || DepartureAngleDeg >= 90)
            {
                problems.Add("ground_clearance must be 0 or more and the approach/departure angles between 0 and 90 degrees");
            }

            for (int i = 0; i < Trailers.Count; i++)
            {
                TrailerUnit t = Trailers[i];
                string what = Trailers.Count == 1 ? "trailer" : $"trailer {i + 1}";
                if (!(t.Length > 0) || !(t.Width > 0) || !(t.Wheelbase > 0))
                {
                    problems.Add($"{what}: length, width and wheelbase (coupling to the centre of its axles) must be positive");
                }
                else if (t.RearOverhang < -1e-9)
                {
                    problems.Add($"{what}: its length is less than its front_overhang plus its wheelbase");
                }
                else if (t.FrontOverhang < -t.Wheelbase)
                {
                    problems.Add($"{what}: front_overhang can't put the front of the body behind its axles");
                }

                if (t.Track < 0 || t.Track > t.Width)
                {
                    problems.Add($"{what}: track can't be negative or exceed the width");
                }
            }

            return problems;
        }

        /// <summary>
        /// Radius of the rear axle centre's path in a steady turn whose outer front wheel is at
        /// <paramref name="outerWheelRadius"/>. In a steady turn the instantaneous centre lies on
        /// the rear axle line, so wheel radius² = (rear radius + track/2)² + wheelbase².
        /// </summary>
        public double RearAxleRadius(double outerWheelRadius)
        {
            double r = Math.Sqrt(Math.Max(0, outerWheelRadius * outerWheelRadius - Wheelbase * Wheelbase)) - Track / 2;
            return Math.Max(0.01, r);
        }

        /// <summary>Outer front wheel radius in a steady turn with the rear axle centre at <paramref name="rearAxleRadius"/>.</summary>
        public double OuterWheelRadius(double rearAxleRadius)
        {
            double a = rearAxleRadius + Track / 2;
            return Math.Sqrt(a * a + Wheelbase * Wheelbase);
        }

        /// <summary>
        /// Steady-turn radius of a point of the vehicle, given in the vehicle frame, when the outer
        /// front wheel is at <paramref name="outerWheelRadius"/>. <paramref name="left"/> says which
        /// way the vehicle turns (it decides which side is the inside).
        /// </summary>
        public double RadiusOfPoint(double outerWheelRadius, VehiclePoint point, bool left)
        {
            double rr = RearAxleRadius(outerWheelRadius);
            double lateral = left ? point.Y : -point.Y; // distance towards the centre of the turn
            double d = rr - lateral;
            return Math.Sqrt(d * d + point.X * point.X);
        }

        public VehiclePoint FrontAxleCentre => new VehiclePoint(Wheelbase, 0);

        public VehiclePoint RearAxleCentre => new VehiclePoint(0, 0);

        /// <summary>The front wheel on the outside of a turn.</summary>
        public VehiclePoint OuterFrontWheel(bool left) => new VehiclePoint(Wheelbase, left ? -Track / 2 : Track / 2);

        public VehiclePoint Resolve(ReferencePoint reference, bool leftTurn)
        {
            switch (reference)
            {
                case ReferencePoint.RearAxle:
                    return RearAxleCentre;
                case ReferencePoint.OuterFrontWheel:
                    return OuterFrontWheel(leftTurn);
                default:
                    return FrontAxleCentre;
            }
        }

        /// <summary>
        /// The body outline in the vehicle frame, anticlockwise from the rear-right corner, with
        /// bulges (tan(sweep/4)) for the rounded corners and the curved front and rear ends. Closed.
        /// </summary>
        public (List<VehiclePoint> Points, List<double> Bulges) BodyOutline()
        {
            double hw = Width / 2, he = EffectiveWidth / 2, c = CornerLength;
            double xFront = Wheelbase + EffectiveFrontOverhang, xRear = -EffectiveRearOverhang;
            // Ends: a three-point arc through the two corners and the overall nose/tail: bulge = 2 * sagitta / chord.
            // Corners: tangent to the side, through the effective corner. Anticlockwise round a convex shape,
            // every arc bulges outwards, so every bulge is positive.
            double frontBulge = EffectiveWidth > 1e-9 ? 2 * FrontOverhangRounding / EffectiveWidth : 0;
            double rearBulge = EffectiveWidth > 1e-9 ? 2 * RearOverhangRounding / EffectiveWidth : 0;
            double cornerBulge = Math.Tan(CornerSweep / 4);

            var pts = new List<VehiclePoint>
            {
                new VehiclePoint(xRear, -he),         // rear-right effective corner; corner arc to the side
                new VehiclePoint(xRear + c, -hw),     // right side, full width
                new VehiclePoint(xFront - c, -hw),    // corner arc to the front-right corner
                new VehiclePoint(xFront, -he),        // front-right effective corner; front end arc to the left corner
                new VehiclePoint(xFront, he),         // front-left effective corner; corner arc to the side
                new VehiclePoint(xFront - c, hw),     // left side, full width
                new VehiclePoint(xRear + c, hw),      // corner arc to the rear-left corner
                new VehiclePoint(xRear, he),          // rear-left effective corner; rear end arc closes to the start
            };
            var bulges = new List<double> { cornerBulge, 0, cornerBulge, frontBulge, cornerBulge, 0, cornerBulge, rearBulge };
            return (pts, bulges);
        }

        /// <summary>The four wheels as rectangles in the vehicle frame (front-left, front-right, rear-left, rear-right).</summary>
        public List<List<VehiclePoint>> Wheels()
        {
            var result = new List<List<VehiclePoint>>();
            foreach (var (x, y) in new[] { (Wheelbase, Track / 2), (Wheelbase, -Track / 2), (0.0, Track / 2), (0.0, -Track / 2) })
            {
                double hl = WheelLength / 2, hw = WheelWidth / 2;
                result.Add(new List<VehiclePoint>
                {
                    new VehiclePoint(x - hl, y - hw), new VehiclePoint(x + hl, y - hw),
                    new VehiclePoint(x + hl, y + hw), new VehiclePoint(x - hl, y + hw),
                });
            }

            return result;
        }

        public DesignVehicle Clone()
        {
            var copy = (DesignVehicle)MemberwiseClone();
            copy.Trailers = Trailers.Select(t => t.Clone()).ToList();
            return copy;
        }
    }

    /// <summary>A point in the vehicle frame: X forward from the rear axle centre, Y to the left.</summary>
    public readonly struct VehiclePoint
    {
        public VehiclePoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }

        public override string ToString() => $"({X:0.###}, {Y:0.###})";
    }

    /// <summary>Which point of the vehicle follows the driven path.</summary>
    public enum ReferencePoint
    {
        /// <summary>The centre of the front axle (the steering point); the rear axle tracks in behind it.</summary>
        FrontAxle,

        /// <summary>The centre of the rear axle: the heading is the path tangent, so the path's radii are the rear axle's.</summary>
        RearAxle,

        /// <summary>The front wheel on the outside of the turn: the AS/NZS 2890.1 turn radius is measured here.</summary>
        OuterFrontWheel,
    }

    /// <summary>A vehicle position: rear axle centre and heading (radians, anticlockwise from +X).</summary>
    public readonly struct VehiclePose
    {
        public VehiclePose(Point2 rearAxle, double heading)
        {
            RearAxle = rearAxle;
            Heading = heading;
        }

        public Point2 RearAxle { get; }
        public double Heading { get; }

        public Point2 ToWorld(VehiclePoint p)
        {
            double c = Math.Cos(Heading), s = Math.Sin(Heading);
            return new Point2(RearAxle.X + p.X * c - p.Y * s, RearAxle.Y + p.X * s + p.Y * c);
        }

        public Point2 ToWorld(double x, double y) => ToWorld(new VehiclePoint(x, y));
    }
}
