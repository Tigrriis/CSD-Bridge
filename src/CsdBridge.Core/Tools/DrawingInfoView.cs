using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsdBridge.Core.Drawing;

namespace CsdBridge.Core.Tools
{
    /// <summary>
    /// A compact view of the drawing setup for Claude: layers as rows, xref layers left out,
    /// and Civil Site Design's many per-road string layers folded into one road → codes list.
    /// Civil drawings often have hundreds of layers, so this keeps the reply small.
    /// </summary>
    internal static class DrawingInfoView
    {
        private const int MaxLayers = 300;
        private const int MaxBlocks = 150;

        public static object Compact(DrawingInfo info, JsonElement input)
        {
            string? filter = input.ValueKind == JsonValueKind.Object ? JsonArgs.OptionalString(input, "layer_filter", "") : null;
            bool xrefs = input.ValueKind == JsonValueKind.Object && JsonArgs.OptionalBool(input, "include_xref_layers", "", false);

            IEnumerable<LayerInfo> layers = info.Layers;
            int xrefCount = layers.Count(l => l.Name.IndexOf('|') >= 0);
            if (!xrefs)
            {
                layers = layers.Where(l => l.Name.IndexOf('|') < 0);
            }

            if (!string.IsNullOrWhiteSpace(filter))
            {
                layers = layers.Where(l => l.Name.IndexOf(filter!, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            // CSD string layers: group by road. The code is the part after the last '-' that matches
            // the road's other layers; codes may contain '-' too (LDR-1), so keep the full remainder.
            var roads = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            var kept = new List<LayerInfo>();
            foreach (LayerInfo l in layers)
            {
                Match m = Regex.Match(l.Name, @"^CSD-(2D|3D)-(.+)$", RegexOptions.IgnoreCase);
                if (m.Success && string.IsNullOrWhiteSpace(filter))
                {
                    (string road, string code) = SplitRoadCode(m.Groups[2].Value, info.Layers.Select(x => x.Name));
                    string key = $"{m.Groups[1].Value.ToUpperInvariant()} {road}";
                    if (!roads.TryGetValue(key, out SortedSet<string>? codes))
                    {
                        codes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                        roads[key] = codes;
                    }

                    codes.Add(code);
                    continue;
                }

                kept.Add(l);
            }

            List<object[]> rows = kept.Take(MaxLayers).Select(l => new object[]
            {
                l.Name,
                (int)l.Color,
                l.Linetype ?? "",
                l.LineweightMm.HasValue ? (object)l.LineweightMm.Value : "default",
                State(l),
            }).ToList();

            return new
            {
                file_name = info.FileName,
                units = info.Units,
                current_space = info.CurrentSpace,
                current_layer = info.CurrentLayer,
                current_text_style = info.CurrentTextStyle,
                current_dim_style = info.CurrentDimStyle,
                default_text_height = info.DefaultTextHeight,
                extents_min = info.ExtentsMin,
                extents_max = info.ExtentsMax,
                entity_count = info.EntityCount,
                layer_columns = "name, colour (ACI), linetype, lineweight mm, state",
                layers = rows,
                layers_not_listed = kept.Count > MaxLayers ? kept.Count - MaxLayers : (int?)null,
                xref_layers_hidden = !xrefs && xrefCount > 0 ? xrefCount : (int?)null,
                csd_roads = roads.Count > 0 ? roads.ToDictionary(kv => kv.Key, kv => string.Join(", ", kv.Value)) : null,
                csd_roads_note = roads.Count > 0 ? "String layers are CSD-<2D|3D>-<road>-<code>." : null,
                linetypes = info.Linetypes,
                text_styles = info.TextStyles,
                dim_styles = info.DimStyles,
                blocks = info.Blocks.Take(MaxBlocks).ToList(),
                blocks_not_listed = info.Blocks.Count > MaxBlocks ? info.Blocks.Count - MaxBlocks : (int?)null,
            };
        }

        /// <summary>
        /// Split "CL-01-LDR-1" into road "CL-01" and code "LDR-1". Road names and codes can both
        /// contain '-', so prefer the split whose road prefix is shared by a C.L. layer (every CSD
        /// road has a centreline string), else split at the last '-'.
        /// </summary>
        internal static (string Road, string Code) SplitRoadCode(string rest, IEnumerable<string> allLayers)
        {
            var names = new HashSet<string>(allLayers.Select(n => n.ToUpperInvariant()));
            int idx = -1;
            while ((idx = rest.IndexOf('-', idx + 1)) > 0)
            {
                string road = rest.Substring(0, idx);
                if (names.Contains(("CSD-3D-" + road + "-C.L.").ToUpperInvariant()) || names.Contains(("CSD-2D-" + road + "-C.L.").ToUpperInvariant()))
                {
                    return (road, rest.Substring(idx + 1));
                }
            }

            int last = rest.LastIndexOf('-');
            return last > 0 ? (rest.Substring(0, last), rest.Substring(last + 1)) : (rest, "");
        }

        private static string State(LayerInfo l)
        {
            var parts = new List<string>();
            if (l.Off == true)
            {
                parts.Add("off");
            }

            if (l.Frozen == true)
            {
                parts.Add("frozen");
            }

            if (l.Locked == true)
            {
                parts.Add("locked");
            }

            return string.Join(" ", parts);
        }
    }
}
