using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using CsdBridge.Core.Drawing;

namespace CsdBridge.Core.Vehicles
{
    /// <summary>
    /// A design vehicle for swept path (turning path) analysis. Dimensions in metres.
    ///
    /// The vehicle frame used throughout: origin at the centre of the rear axle, X forward,
    /// Y to the left. The swept path is traced by the body's *effective* extremities
    /// (AS/NZS 2890.1:2004 Figures B1 and B2, "relevant longitudinal dimensions that affect
    /// swept path"): the rounded front and rear corners reach the effective overhangs at the
    /// effective width, and the overall overhangs only at the centreline.
    /// </summary>
    public sealed class DesignVehicle
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

        public DesignVehicle Clone() => (DesignVehicle)MemberwiseClone();
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
