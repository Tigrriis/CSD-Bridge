using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsdBridge.Core.Civil;

namespace CsdBridge.Core.Vehicles
{
    /// <summary>
    /// How turning paths are drawn: the design vehicles, the clearances, which layers the
    /// swept path, clearance lines, vehicle outlines and labels go on, and what to draw by
    /// default. Loaded from turn-paths.json; every value can be changed per run.
    /// </summary>
    public sealed class TurnPathConventions
    {
        /// <summary>Design vehicles by name. The shipped file has AS/NZS 2890.1's B99 and B85.</summary>
        public Dictionary<string, DesignVehicle> Vehicles { get; set; } = new Dictionary<string, DesignVehicle>(StringComparer.OrdinalIgnoreCase);

        public string DefaultVehicle { get; set; } = "B99";

        /// <summary>Simulation step along the path (m).</summary>
        public double Step { get; set; } = 0.02;

        /// <summary>Output polylines are simplified to this (m).</summary>
        public double Tolerance { get; set; } = 0.001;

        /// <summary>Straight travel before a template turn (m).</summary>
        public double LeadIn { get; set; }

        /// <summary>Straight travel after a template turn (m).</summary>
        public double LeadOut { get; set; } = 6;

        /// <summary>Drive mode turns: "arc" (one arc to the destination where its radius allows) or "corner" (turn at the radius, then straight).</summary>
        public string DriveTurns { get; set; } = "arc";

        /// <summary>Drive mode: ask for the direction the vehicle faces at each destination.</summary>
        public bool DriveArrivalHeading { get; set; }

        /// <summary>Check the swept path and clearances against linework on the obstruction layers.</summary>
        public bool CheckConflicts { get; set; }

        /// <summary>Layers holding kerbs, walls, columns and so on; * and ? wildcards; xref layers match by the part after '|'.</summary>
        public List<string> ObstructionLayers { get; set; } = new List<string>();

        /// <summary>Solve mode: how long the search for a multi-point turn may take, seconds.</summary>
        public double SolverTimeLimit { get; set; } = 10;

        /// <summary>Solve mode: how far beyond the start and target positions the vehicle may go while manoeuvring, metres.</summary>
        public double SolverSearchMargin { get; set; } = 15;

        [System.Text.Json.Serialization.JsonIgnore]
        public TurnPathBuilder.DriveTurns DriveTurnsMode =>
            string.Equals(DriveTurns, "corner", StringComparison.OrdinalIgnoreCase) ? TurnPathBuilder.DriveTurns.Corner : TurnPathBuilder.DriveTurns.Arc;

        /// <summary>Draw a vehicle outline every this many metres along the path (0 = only at the start and end).</summary>
        public double OutlineInterval { get; set; }

        /// <summary>Height of the label text in drawing units. Null: the drawing's default text height.</summary>
        public double? TextHeight { get; set; } = 0.3;

        /// <summary>Ground clearance check: distance the template is moved along a section each step (m).</summary>
        public double ClearanceStep { get; set; } = 0.05;

        /// <summary>Ground clearance check on a surface: distance between checked positions along the path (m).</summary>
        public double ClearancePoseInterval { get; set; } = 0.1;

        /// <summary>Default vertical exaggeration of long section plots.</summary>
        public double SectionVerticalExaggeration { get; set; } = 1;

        public TurnPathDrawOptions Draw { get; set; } = new TurnPathDrawOptions();

        public TurnPathLayers Layers { get; set; } = new TurnPathLayers();

        /// <summary>Hatch pattern for the swept area (its layer is Layers.Hatch).</summary>
        public HatchStyle Hatch { get; set; } = new HatchStyle { Pattern = "SOLID" };

        /// <summary>Styles for layers that don't exist in the drawing yet. Existing layers are never changed.</summary>
        public Dictionary<string, LayerStyle> LayerStyles { get; set; } = new Dictionary<string, LayerStyle>(StringComparer.OrdinalIgnoreCase);

        public static JsonSerializerOptions JsonOptions { get; } = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };

        /// <summary>The built-in conventions: the two standard vehicles and office-style layer names.</summary>
        public static TurnPathConventions Default()
        {
            var c = new TurnPathConventions();
            foreach (DesignVehicle v in DesignVehicle.Standard)
            {
                c.Vehicles[v.Name] = v;
            }

            c.LayerStyles[c.Layers.SweptPath] = new LayerStyle { Color = 1, Lineweight = 0.35, Description = "Design vehicle swept path (body)" };
            c.LayerStyles[c.Layers.Clearance] = new LayerStyle { Color = 1, Linetype = "DASHED", Lineweight = 0.25, Description = "Design vehicle swept path plus AS/NZS 2890.1 clearances" };
            c.LayerStyles[c.Layers.Vehicle] = new LayerStyle { Color = 4, Lineweight = 0.25, Description = "Design vehicle outline at the start and end" };
            c.LayerStyles[c.Layers.VehicleIntermediate] = new LayerStyle { Color = 8, Lineweight = 0.13, Description = "Design vehicle outlines along the path" };
            c.LayerStyles[c.Layers.Path] = new LayerStyle { Color = 8, Linetype = "DASHDOT", Lineweight = 0.13, Description = "Path driven by the design vehicle's reference point" };
            c.LayerStyles[c.Layers.WheelPaths] = new LayerStyle { Color = 8, Lineweight = 0.13, Description = "Design vehicle wheel paths" };
            c.LayerStyles[c.Layers.Labels] = new LayerStyle { Color = 4, Lineweight = 0.18, Description = "Design vehicle labels" };
            c.LayerStyles[c.Layers.Hatch] = new LayerStyle { Color = 254, Rgb = new[] { 255, 220, 220 }, Lineweight = 0.13, Plot = true, Description = "Design vehicle swept area" };
            c.LayerStyles[c.Layers.Block] = new LayerStyle { Color = 7, Lineweight = 0.25, Description = "Design vehicle turning path blocks" };
            c.LayerStyles[c.Layers.Conflict] = new LayerStyle { Color = 6, Lineweight = 0.7, Description = "Design vehicle conflicts with obstructions" };
            c.LayerStyles[c.Layers.ClearanceCheck] = new LayerStyle { Color = 4, Lineweight = 0.25, Description = "Design vehicle ground clearance check (AS/NZS 2890.1 Appendix C)" };
            c.LayerStyles[c.Layers.ClearanceFail] = new LayerStyle { Color = 1, Lineweight = 0.5, Description = "Design vehicle ground clearance check: where it grounds" };
            return c;
        }

        public static TurnPathConventions Load(string path)
        {
            TurnPathConventions c = JsonSerializer.Deserialize<TurnPathConventions>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"{path} is empty.");
            c.Vehicles = new Dictionary<string, DesignVehicle>(c.Vehicles, StringComparer.OrdinalIgnoreCase);
            c.LayerStyles = new Dictionary<string, LayerStyle>(c.LayerStyles, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, DesignVehicle> kv in c.Vehicles)
            {
                if (string.IsNullOrWhiteSpace(kv.Value.Name))
                {
                    kv.Value.Name = kv.Key;
                }

                List<string> problems = kv.Value.Validate();
                if (problems.Count > 0)
                {
                    throw new InvalidDataException($"{path}: vehicle {kv.Key}: {string.Join("; ", problems)}.");
                }
            }

            if (c.Vehicles.Count == 0)
            {
                throw new InvalidDataException($"{path} defines no vehicles.");
            }

            return c;
        }

        public void Save(string path)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }

        /// <summary>The vehicle by name (case-insensitive); null name means the default.</summary>
        public DesignVehicle Vehicle(string? name)
        {
            string key = string.IsNullOrWhiteSpace(name) ? DefaultVehicle : name!.Trim();
            if (Vehicles.TryGetValue(key, out DesignVehicle? v))
            {
                return v;
            }

            if (Vehicles.Count > 0 && string.IsNullOrWhiteSpace(name))
            {
                return Vehicles.Values.First();
            }

            throw new ArgumentException($"Unknown vehicle '{key}'. Available: {string.Join(", ", Vehicles.Keys)}.");
        }

        /// <summary>A short description for Claude's instructions.</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            foreach (DesignVehicle v in Vehicles.Values)
            {
                sb.Append("- ").Append(v.Name).Append(": ").Append(v.Length.ToString("0.##")).Append(" x ").Append(v.Width.ToString("0.##"))
                  .Append(" m, wheelbase ").Append(v.Wheelbase.ToString("0.##")).Append(" m, min turn radius ").Append(v.MinTurnRadius.ToString("0.#"))
                  .Append(" m (outer front wheel), clearances ").Append((v.ClearanceInside * 1000).ToString("0")).Append(" mm inside / ")
                  .Append((v.ClearanceOutside * 1000).ToString("0")).Append(" mm outside");
                if (!string.IsNullOrWhiteSpace(v.Description))
                {
                    sb.Append(". ").Append(v.Description);
                }

                sb.Append('\n');
            }

            sb.Append("Layers: swept path on ").Append(Layers.SweptPath).Append(", clearance on ").Append(Layers.Clearance)
              .Append(", vehicle outlines on ").Append(Layers.Vehicle).Append(", path on ").Append(Layers.Path)
              .Append(", labels on ").Append(Layers.Labels).Append(", block references on ").Append(Layers.Block)
              .Append(" ({vehicle} is replaced by the vehicle name).\n");
            return sb.ToString();
        }
    }

    /// <summary>What a turning path run draws.</summary>
    public sealed class TurnPathDrawOptions
    {
        /// <summary>The body's swept path (solid lines).</summary>
        public bool SweptPath { get; set; } = true;

        /// <summary>The swept path plus clearances (the design template).</summary>
        public bool Clearance { get; set; } = true;

        /// <summary>Vehicle outline with wheels at the start and end.</summary>
        public bool Vehicle { get; set; } = true;

        /// <summary>The line the reference point was driven along.</summary>
        public bool Path { get; set; } = true;

        public bool WheelPaths { get; set; }

        public bool Labels { get; set; } = true;

        /// <summary>Hatch the swept area.</summary>
        public bool Hatch { get; set; }

        /// <summary>Draw the swept path as one closed outline instead of two lines.</summary>
        public bool ClosedOutline { get; set; }

        /// <summary>Gather everything drawn into a block, inserted at the vehicle's start point on the Block layer.</summary>
        public bool Block { get; set; }

        public TurnPathDrawOptions Clone() => (TurnPathDrawOptions)MemberwiseClone();
    }

    /// <summary>Layer names for each part of a turning path. "{vehicle}" is replaced by the vehicle's name.</summary>
    public sealed class TurnPathLayers
    {
        public string SweptPath { get; set; } = "AE CIV PLN - VEHICLE SWEPT PATH";
        public string Clearance { get; set; } = "AE CIV PLN - VEHICLE CLEARANCE";
        public string Vehicle { get; set; } = "AE CIV PLN - VEHICLE OUTLINE";
        public string VehicleIntermediate { get; set; } = "AE CIV PLN - VEHICLE OUTLINE INTERMEDIATE";
        public string Path { get; set; } = "AE CIV PLN - VEHICLE PATH";
        public string WheelPaths { get; set; } = "AE CIV PLN - VEHICLE WHEEL PATHS";
        public string Labels { get; set; } = "AE CIV PLN - VEHICLE TEXT";
        public string Hatch { get; set; } = "AE CIV HTC - VEHICLE SWEPT PATH";

        /// <summary>Layer of the block reference when the result is made into a block.</summary>
        public string Block { get; set; } = "AE CIV PLN - VEHICLE BLOCK";

        /// <summary>Where the swept path or its clearance meets an obstruction.</summary>
        public string Conflict { get; set; } = "AE CIV PLN - VEHICLE CONFLICT";

        /// <summary>Ground clearance templates, markers and section plots.</summary>
        public string ClearanceCheck { get; set; } = "AE CIV PLN - VEHICLE GROUND CLEARANCE";

        /// <summary>Where the vehicle grounds.</summary>
        public string ClearanceFail { get; set; } = "AE CIV PLN - VEHICLE GROUND CLEARANCE FAIL";

        public TurnPathLayers Clone() => (TurnPathLayers)MemberwiseClone();

        /// <summary>A copy with "{vehicle}" filled in and blanks replaced by the defaults.</summary>
        public TurnPathLayers Resolve(string vehicleName)
        {
            var defaults = new TurnPathLayers();
            string Fill(string? s, string fallback) =>
                (string.IsNullOrWhiteSpace(s) ? fallback : s!).Replace("{vehicle}", vehicleName).Replace("{VEHICLE}", vehicleName.ToUpperInvariant()).Trim();

            return new TurnPathLayers
            {
                SweptPath = Fill(SweptPath, defaults.SweptPath),
                Clearance = Fill(Clearance, defaults.Clearance),
                Vehicle = Fill(Vehicle, defaults.Vehicle),
                VehicleIntermediate = Fill(VehicleIntermediate, defaults.VehicleIntermediate),
                Path = Fill(Path, defaults.Path),
                WheelPaths = Fill(WheelPaths, defaults.WheelPaths),
                Labels = Fill(Labels, defaults.Labels),
                Hatch = Fill(Hatch, defaults.Hatch),
                Block = Fill(Block, defaults.Block),
                Conflict = Fill(Conflict, defaults.Conflict),
                ClearanceCheck = Fill(ClearanceCheck, defaults.ClearanceCheck),
                ClearanceFail = Fill(ClearanceFail, defaults.ClearanceFail),
            };
        }

        /// <summary>Every layer name, keyed by the JSON/tool name of the part.</summary>
        public IEnumerable<KeyValuePair<string, string>> All()
        {
            yield return new KeyValuePair<string, string>("swept_path", SweptPath);
            yield return new KeyValuePair<string, string>("clearance", Clearance);
            yield return new KeyValuePair<string, string>("vehicle", Vehicle);
            yield return new KeyValuePair<string, string>("vehicle_intermediate", VehicleIntermediate);
            yield return new KeyValuePair<string, string>("path", Path);
            yield return new KeyValuePair<string, string>("wheel_paths", WheelPaths);
            yield return new KeyValuePair<string, string>("labels", Labels);
            yield return new KeyValuePair<string, string>("hatch", Hatch);
            yield return new KeyValuePair<string, string>("block", Block);
            yield return new KeyValuePair<string, string>("conflict", Conflict);
            yield return new KeyValuePair<string, string>("clearance_check", ClearanceCheck);
            yield return new KeyValuePair<string, string>("clearance_fail", ClearanceFail);
        }

        /// <summary>Sets a layer by its part name; false if the name is unknown.</summary>
        public bool Set(string part, string layer)
        {
            switch (part.ToLowerInvariant())
            {
                case "swept_path": SweptPath = layer; return true;
                case "clearance": Clearance = layer; return true;
                case "vehicle": Vehicle = layer; return true;
                case "vehicle_intermediate": VehicleIntermediate = layer; return true;
                case "path": Path = layer; return true;
                case "wheel_paths": WheelPaths = layer; return true;
                case "labels": Labels = layer; return true;
                case "hatch": Hatch = layer; return true;
                case "block": Block = layer; return true;
                case "conflict": Conflict = layer; return true;
                case "clearance_check": ClearanceCheck = layer; return true;
                case "clearance_fail": ClearanceFail = layer; return true;
                default: return false;
            }
        }
    }
}
