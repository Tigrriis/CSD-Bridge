using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using CsdBridge.Core.Drawing;

namespace CsdBridge.Core.Tools
{
    /// <summary>Raised for tool input that fails validation. The message goes back to Claude.</summary>
    public sealed class ToolInputException : Exception
    {
        public ToolInputException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Small helpers for reading tool input. Every failure names the exact JSON path
    /// (e.g. <c>entities[3].radius</c>) so Claude can fix the call on the next turn.
    /// </summary>
    public static class JsonArgs
    {
        public static string Path(string parent, string name) => string.IsNullOrEmpty(parent) ? name : parent + "." + name;

        public static string Index(string parent, int index) => parent + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";

        public static bool TryGet(JsonElement obj, string name, out JsonElement value)
        {
            if (obj.ValueKind == JsonValueKind.Object
                && obj.TryGetProperty(name, out value)
                && value.ValueKind != JsonValueKind.Null
                && value.ValueKind != JsonValueKind.Undefined)
            {
                return true;
            }

            value = default;
            return false;
        }

        public static void RequireObject(JsonElement e, string path)
        {
            if (e.ValueKind != JsonValueKind.Object)
            {
                throw new ToolInputException($"{(path.Length == 0 ? "input" : path)} must be an object");
            }
        }

        public static string RequireString(JsonElement obj, string name, string parent, bool allowEmpty = false)
        {
            string path = Path(parent, name);
            if (!TryGet(obj, name, out var v))
            {
                throw new ToolInputException($"{path} is required");
            }

            if (v.ValueKind != JsonValueKind.String)
            {
                throw new ToolInputException($"{path} must be a string");
            }

            string s = v.GetString() ?? "";
            if (!allowEmpty && s.Trim().Length == 0)
            {
                throw new ToolInputException($"{path} must not be empty");
            }

            return s;
        }

        public static string? OptionalString(JsonElement obj, string name, string parent)
        {
            if (!TryGet(obj, name, out var v))
            {
                return null;
            }

            if (v.ValueKind != JsonValueKind.String)
            {
                throw new ToolInputException($"{Path(parent, name)} must be a string");
            }

            string s = v.GetString() ?? "";
            return s.Length == 0 ? null : s;
        }

        public static double RequireNumber(JsonElement obj, string name, string parent)
        {
            double? d = OptionalNumber(obj, name, parent);
            if (d == null)
            {
                throw new ToolInputException($"{Path(parent, name)} is required");
            }

            return d.Value;
        }

        public static double? OptionalNumber(JsonElement obj, string name, string parent)
        {
            if (!TryGet(obj, name, out var v))
            {
                return null;
            }

            return ReadNumber(v, Path(parent, name));
        }

        public static double ReadNumber(JsonElement v, string path)
        {
            double d;
            if (v.ValueKind == JsonValueKind.Number)
            {
                d = v.GetDouble();
            }
            else if (v.ValueKind == JsonValueKind.String
                     && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                // Be lenient with "12.5" - it is unambiguous.
                d = parsed;
            }
            else
            {
                throw new ToolInputException($"{path} must be a number");
            }

            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                throw new ToolInputException($"{path} must be a finite number");
            }

            return d;
        }

        public static double RequirePositive(JsonElement obj, string name, string parent)
        {
            double d = RequireNumber(obj, name, parent);
            if (d <= 0)
            {
                throw new ToolInputException($"{Path(parent, name)} must be greater than 0 (got {Fmt(d)})");
            }

            return d;
        }

        public static double? OptionalPositive(JsonElement obj, string name, string parent)
        {
            double? d = OptionalNumber(obj, name, parent);
            if (d != null && d.Value <= 0)
            {
                throw new ToolInputException($"{Path(parent, name)} must be greater than 0 (got {Fmt(d.Value)})");
            }

            return d;
        }

        public static int? OptionalInt(JsonElement obj, string name, string parent, int min, int max)
        {
            double? d = OptionalNumber(obj, name, parent);
            if (d == null)
            {
                return null;
            }

            if (Math.Abs(d.Value - Math.Round(d.Value)) > 1e-9 || d.Value < min || d.Value > max)
            {
                throw new ToolInputException($"{Path(parent, name)} must be a whole number from {min} to {max}");
            }

            return (int)Math.Round(d.Value);
        }

        public static bool OptionalBool(JsonElement obj, string name, string parent, bool defaultValue)
        {
            if (!TryGet(obj, name, out var v))
            {
                return defaultValue;
            }

            if (v.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (v.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            throw new ToolInputException($"{Path(parent, name)} must be true or false");
        }

        /// <summary>Colour as an AutoCAD Color Index 1-255. Null means ByLayer.</summary>
        public static short? OptionalColor(JsonElement obj, string name, string parent)
        {
            if (!TryGet(obj, name, out var v))
            {
                return null;
            }

            if (v.ValueKind == JsonValueKind.String)
            {
                string s = (v.GetString() ?? "").Trim().ToLowerInvariant();
                if (s == "bylayer" || s.Length == 0)
                {
                    return null;
                }
            }

            int? aci = OptionalInt(obj, name, parent, 1, 255);
            return aci == null ? (short?)null : (short)aci.Value;
        }

        public static Point2 RequirePoint(JsonElement obj, string name, string parent)
        {
            Point2? p = OptionalPoint(obj, name, parent);
            if (p == null)
            {
                throw new ToolInputException($"{Path(parent, name)} is required ([x, y])");
            }

            return p.Value;
        }

        public static Point2? OptionalPoint(JsonElement obj, string name, string parent)
        {
            if (!TryGet(obj, name, out var v))
            {
                return null;
            }

            return ReadPoint(v, Path(parent, name));
        }

        /// <summary>Accepts [x, y], [x, y, z] (z ignored) or {"x":..,"y":..}.</summary>
        public static Point2 ReadPoint(JsonElement v, string path)
        {
            if (v.ValueKind == JsonValueKind.Array)
            {
                int n = v.GetArrayLength();
                if (n < 2 || n > 3)
                {
                    throw new ToolInputException($"{path} must be [x, y]");
                }

                return new Point2(ReadNumber(v[0], path + "[0]"), ReadNumber(v[1], path + "[1]"));
            }

            if (v.ValueKind == JsonValueKind.Object && TryGet(v, "x", out var x) && TryGet(v, "y", out var y))
            {
                return new Point2(ReadNumber(x, path + ".x"), ReadNumber(y, path + ".y"));
            }

            throw new ToolInputException($"{path} must be [x, y]");
        }

        public static List<JsonElement> RequireArray(JsonElement obj, string name, string parent, int minItems, int maxItems)
        {
            string path = Path(parent, name);
            if (!TryGet(obj, name, out var v))
            {
                throw new ToolInputException($"{path} is required");
            }

            return ReadArray(v, path, minItems, maxItems);
        }

        public static List<JsonElement>? OptionalArray(JsonElement obj, string name, string parent, int minItems, int maxItems)
        {
            if (!TryGet(obj, name, out var v))
            {
                return null;
            }

            return ReadArray(v, Path(parent, name), minItems, maxItems);
        }

        private static List<JsonElement> ReadArray(JsonElement v, string path, int minItems, int maxItems)
        {
            if (v.ValueKind != JsonValueKind.Array)
            {
                throw new ToolInputException($"{path} must be an array");
            }

            int n = v.GetArrayLength();
            if (n < minItems)
            {
                throw new ToolInputException($"{path} needs at least {minItems} item(s)");
            }

            if (n > maxItems)
            {
                throw new ToolInputException($"{path} has {n} items; the limit is {maxItems} per call - split it into several calls");
            }

            var list = new List<JsonElement>(n);
            foreach (var item in v.EnumerateArray())
            {
                list.Add(item);
            }

            return list;
        }

        public static List<string> ReadStringList(List<JsonElement> items, string path)
        {
            var result = new List<string>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(items[i].GetString()))
                {
                    throw new ToolInputException($"{Index(path, i)} must be a non-empty string");
                }

                result.Add(items[i].GetString()!.Trim());
            }

            return result;
        }

        public static string Fmt(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
