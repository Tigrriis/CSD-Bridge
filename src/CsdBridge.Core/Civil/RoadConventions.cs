using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CsdBridge.Core.Civil
{
    /// <summary>
    /// How the office drafts roads from Civil Site Design: where CSD's strings and alignments
    /// are, which codes become lines, which code pairs become closed outlines and hatches, and
    /// the layers to use. Loaded from road-drafting.json.
    /// </summary>
    public sealed class RoadConventions
    {
        /// <summary>Layer names of CSD's code strings; {road} and {code} are filled in. The first match wins.</summary>
        public List<string> StringLayers { get; set; } = new List<string> { "CSD-2D-{road}-{code}", "CSD-3D-{road}-{code}" };

        /// <summary>Layers holding CSD's exact alignment geometry (lines and arcs).</summary>
        public List<string> AlignmentLayers { get; set; } = new List<string> { "Align-Lines", "Align-Arcs" };

        public string CentrelineCode { get; set; } = "C.L.";

        /// <summary>Offsets are fitted to CSD's strings within this (m).</summary>
        public double OffsetTolerance { get; set; } = 0.01;

        /// <summary>Fitted offsets are rounded to this (m).</summary>
        public double OffsetRounding { get; set; } = 0.001;

        /// <summary>Where a code's strings have a gap longer than this (m), the line is split.</summary>
        public double GapTolerance { get; set; } = 0.5;

        public Dictionary<string, LayerStyle> Layers { get; set; } = new Dictionary<string, LayerStyle>();

        public LineStyle? Centreline { get; set; }

        public List<RoadLine> Lines { get; set; } = new List<RoadLine>();

        public List<RoadArea> Areas { get; set; } = new List<RoadArea>();

        public static JsonSerializerOptions JsonOptions { get; } = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static RoadConventions Load(string path) =>
            JsonSerializer.Deserialize<RoadConventions>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"{path} is empty.");

        /// <summary>A short description for Claude's instructions.</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("String layers: ").Append(string.Join(" or ", StringLayers)).Append(". Alignment geometry on ")
              .Append(string.Join(", ", AlignmentLayers)).Append(".\n");
            if (Centreline != null)
            {
                sb.Append("- centreline: alignment on ").Append(Centreline.Layer).Append('\n');
            }

            foreach (RoadLine l in Lines)
            {
                sb.Append("- ").Append(l.Name).Append(": codes ").Append(string.Join("/", l.Codes)).Append(" on ").Append(l.Layer).Append('\n');
            }

            foreach (RoadArea a in Areas)
            {
                sb.Append("- ").Append(a.Name).Append(": between ").Append(string.Join(" and ", a.Between));
                if (a.OutlineLayer != null)
                {
                    sb.Append(", outline on ").Append(a.OutlineLayer);
                }

                if (a.Hatch != null)
                {
                    sb.Append(", ").Append(a.Hatch.Pattern).Append(" hatch on ").Append(a.Hatch.Layer);
                }

                if (a.UnlessCodes != null && a.UnlessCodes.Count > 0)
                {
                    sb.Append(" (not where ").Append(string.Join("/", a.UnlessCodes)).Append(" exists)");
                }

                sb.Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>Every layer the conventions draw on.</summary>
        public IEnumerable<string> UsedLayers()
        {
            if (Centreline != null)
            {
                yield return Centreline.Layer;
            }

            foreach (RoadLine l in Lines)
            {
                yield return l.Layer;
            }

            foreach (RoadArea a in Areas)
            {
                if (a.OutlineLayer != null)
                {
                    yield return a.OutlineLayer;
                }

                if (a.Hatch != null)
                {
                    yield return a.Hatch.Layer;
                }
            }
        }
    }

    public sealed class LayerStyle
    {
        public short? Color { get; set; }
        public int[]? Rgb { get; set; }
        public string? Linetype { get; set; }
        public double? Lineweight { get; set; }
        public bool? Plot { get; set; }
        public string? Description { get; set; }
    }

    public sealed class LineStyle
    {
        public string Layer { get; set; } = "0";
        public double? LinetypeScale { get; set; }
    }

    /// <summary>A road line drawn from one code, e.g. kerb lines from KERB_LEFT and KERB_RIGHT.</summary>
    public sealed class RoadLine
    {
        public string Name { get; set; } = "";
        public List<string> Codes { get; set; } = new List<string>();
        public string Layer { get; set; } = "0";
        public double? LinetypeScale { get; set; }
    }

    /// <summary>An area between two codes, drawn as a closed outline and/or hatched.</summary>
    public sealed class RoadArea
    {
        public string Name { get; set; } = "";

        /// <summary>The two codes bounding the area, e.g. ["LEB", "REB"].</summary>
        public List<string> Between { get; set; } = new List<string>();

        public string? OutlineLayer { get; set; }
        public double? LinetypeScale { get; set; }
        public HatchStyle? Hatch { get; set; }

        /// <summary>Skip this area on roads that have any of these codes (e.g. no shoulder hatch where there is a footpath).</summary>
        public List<string>? UnlessCodes { get; set; }
    }

    public sealed class HatchStyle
    {
        public string Layer { get; set; } = "0";
        public string Pattern { get; set; } = "SOLID";
        public double Scale { get; set; } = 1;
        public double Angle { get; set; }
    }
}
