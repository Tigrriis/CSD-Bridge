using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CsdBridge.Core.Agent;

namespace CsdBridge.Core.Routines
{
    /// <summary>What kind of value a routine input takes, and how it is asked for.</summary>
    public enum RoutineInputType
    {
        /// <summary>Free text, typed in the run dialog.</summary>
        Text,

        /// <summary>A number, typed in the run dialog.</summary>
        Number,

        /// <summary>One of a fixed list of options, chosen in the run dialog.</summary>
        Choice,

        /// <summary>A tick box in the run dialog.</summary>
        YesNo,

        /// <summary>A point Claude asks the user to pick in the drawing (pick_point).</summary>
        Point,

        /// <summary>Objects selected before the run, or that Claude asks for (select_entities).</summary>
        Selection,
    }

    /// <summary>Where a routine is kept.</summary>
    public enum RoutineLocation
    {
        /// <summary>The user's own routines (%APPDATA%\CsdBridge\routines).</summary>
        Personal,

        /// <summary>The office's shared routines folder (office_routines_folder).</summary>
        Office,
    }

    /// <summary>One value a routine asks for when it runs.</summary>
    public sealed class RoutineInput
    {
        private static readonly Regex NamePattern = new Regex("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

        /// <summary>Identifier used as {{name}} in the instructions, e.g. max_grade.</summary>
        public string Name { get; set; } = "";

        /// <summary>What the run dialog shows; blank uses the name ("max_grade" shows as "Max grade").</summary>
        public string? Label { get; set; }

        public RoutineInputType Type { get; set; } = RoutineInputType.Text;

        /// <summary>Starting value in the run dialog (text, number, choice or yes/no inputs).</summary>
        public string? Default { get; set; }

        /// <summary>The options of a choice input.</summary>
        public List<string> Options { get; set; } = new List<string>();

        /// <summary>Shown after the value, e.g. "%" or "m".</summary>
        public string? Unit { get; set; }

        public bool Required { get; set; }

        /// <summary>A hint shown in the run dialog.</summary>
        public string? Help { get; set; }

        /// <summary>Keys in the file this version doesn't know, kept so saving doesn't lose them.</summary>
        public List<KeyValuePair<string, string>> ExtraFields { get; set; } = new List<KeyValuePair<string, string>>();

        public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Prettify(Name) : Label!.Trim();

        /// <summary>True for inputs the run dialog asks for; points and selections are picked in the drawing.</summary>
        public bool IsTyped => Type != RoutineInputType.Point && Type != RoutineInputType.Selection;

        public static bool IsValidName(string? name) => name != null && NamePattern.IsMatch(name);

        public RoutineInput Clone() => new RoutineInput
        {
            Name = Name,
            Label = Label,
            Type = Type,
            Default = Default,
            Options = new List<string>(Options),
            Unit = Unit,
            Required = Required,
            Help = Help,
            ExtraFields = new List<KeyValuePair<string, string>>(ExtraFields),
        };

        /// <summary>"max_grade" becomes "Max grade".</summary>
        public static string Prettify(string name)
        {
            string s = (name ?? "").Replace('_', ' ').Trim();
            return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        /// <summary>The file and the tool name types in lower case: text, number, choice, yesno, point, selection.</summary>
        public static string TypeName(RoutineInputType type) => type == RoutineInputType.YesNo ? "yesno" : type.ToString().ToLowerInvariant();

        public static bool TryParseType(string? value, out RoutineInputType type)
        {
            switch ((value ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", ""))
            {
                case "":
                case "text":
                case "string":
                    type = RoutineInputType.Text;
                    return true;
                case "number":
                case "numeric":
                    type = RoutineInputType.Number;
                    return true;
                case "choice":
                case "list":
                case "options":
                    type = RoutineInputType.Choice;
                    return true;
                case "yesno":
                case "yes/no":
                case "bool":
                case "boolean":
                case "checkbox":
                    type = RoutineInputType.YesNo;
                    return true;
                case "point":
                case "pick":
                case "pickpoint":
                    type = RoutineInputType.Point;
                    return true;
                case "selection":
                case "select":
                case "objects":
                    type = RoutineInputType.Selection;
                    return true;
                default:
                    type = RoutineInputType.Text;
                    return false;
            }
        }

        /// <summary>yes / no from the usual spellings, or null if it isn't one.</summary>
        public static bool? ParseYesNo(string? value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "yes":
                case "y":
                case "true":
                case "1":
                case "on":
                    return true;
                case "no":
                case "n":
                case "false":
                case "0":
                case "off":
                    return false;
                default:
                    return null;
            }
        }

        /// <summary>Accepts "8", "8.5" and the Windows number format (e.g. "8,5" where comma is the decimal mark).</summary>
        public static bool TryParseNumber(string? value, out double number)
        {
            string s = (value ?? "").Trim();
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                || double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out number);
        }
    }

    /// <summary>
    /// A saved, reusable procedure: a name, the inputs it asks for, and step-by-step
    /// instructions for Claude. Stored as a Markdown file (see <see cref="RoutineFile"/>).
    /// </summary>
    public sealed class Routine
    {
        public const int MaxNameLength = 80;
        public const int MaxInputs = 30;

        private static readonly Regex PlaceholderPattern = new Regex(@"\{\{\s*([^{}\s]+)\s*\}\}", RegexOptions.CultureInvariant);

        public string Name { get; set; } = "";

        /// <summary>One line on what it does, shown in the list.</summary>
        public string? Description { get; set; }

        /// <summary>Quick, Normal or Thorough; null uses the user's effort setting.</summary>
        public EffortMode? Effort { get; set; }

        /// <summary>The routine changes Civil Site Design values, so it needs AICSD Edit.</summary>
        public bool CsdEdit { get; set; }

        public List<RoutineInput> Inputs { get; set; } = new List<RoutineInput>();

        /// <summary>The steps for Claude, in Markdown. Inputs appear as {{name}}.</summary>
        public string Instructions { get; set; } = "";

        /// <summary>Top-level keys in the file this version doesn't know, kept on save.</summary>
        public List<KeyValuePair<string, string>> ExtraFields { get; set; } = new List<KeyValuePair<string, string>>();

        // ---- where it was loaded from (not part of the file)

        public string? FilePath { get; set; }

        public RoutineLocation Location { get; set; }

        /// <summary>The file's last-write time when it was read, to notice changes made by someone else.</summary>
        public DateTime? LoadedWriteTimeUtc { get; set; }

        /// <summary>Why the file couldn't be read cleanly, or null.</summary>
        public string? LoadProblem { get; set; }

        public RoutineInput? FindInput(string name) =>
            Inputs.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

        public Routine Clone() => new Routine
        {
            Name = Name,
            Description = Description,
            Effort = Effort,
            CsdEdit = CsdEdit,
            Inputs = Inputs.Select(i => i.Clone()).ToList(),
            Instructions = Instructions,
            ExtraFields = new List<KeyValuePair<string, string>>(ExtraFields),
            FilePath = FilePath,
            Location = Location,
            LoadedWriteTimeUtc = LoadedWriteTimeUtc,
            LoadProblem = LoadProblem,
        };

        /// <summary>The names used as {{name}} in a text, in order of first use.</summary>
        public static List<string> Placeholders(string? text)
        {
            var names = new List<string>();
            foreach (Match m in PlaceholderPattern.Matches(text ?? ""))
            {
                string name = m.Groups[1].Value;
                if (!names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                {
                    names.Add(name);
                }
            }

            return names;
        }

        /// <summary>Problems with the name alone (blank, too long, line breaks), or null.</summary>
        public static string? CheckName(string? name)
        {
            string n = (name ?? "").Trim();
            if (n.Length == 0)
            {
                return "Give the routine a name.";
            }

            if (n.Length > MaxNameLength)
            {
                return $"The name is {n.Length} characters; keep it to {MaxNameLength}.";
            }

            if (n.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0)
            {
                return "The name must be on one line.";
            }

            return null;
        }

        /// <summary>Everything that would stop the routine working, in plain words. Empty when it is fine.</summary>
        public List<string> Validate()
        {
            var errors = new List<string>();
            if (CheckName(Name) is string nameProblem)
            {
                errors.Add(nameProblem);
            }

            if (string.IsNullOrWhiteSpace(Instructions))
            {
                errors.Add("Write the instructions: the steps Claude should follow.");
            }

            if (Inputs.Count > MaxInputs)
            {
                errors.Add($"There are {Inputs.Count} inputs; the limit is {MaxInputs}.");
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Inputs.Count; i++)
            {
                RoutineInput input = Inputs[i];
                string which = string.IsNullOrWhiteSpace(input.Name) ? $"Input {i + 1}" : $"Input \"{input.Name}\"";
                if (string.IsNullOrWhiteSpace(input.Name))
                {
                    errors.Add($"{which} needs a name (letters, digits and _, e.g. max_grade).");
                    continue;
                }

                if (!RoutineInput.IsValidName(input.Name))
                {
                    errors.Add($"{which}: use letters, digits and _ in the name, starting with a letter (e.g. {Suggest(input.Name)}).");
                }
                else if (!seen.Add(input.Name))
                {
                    errors.Add($"{which} is listed twice.");
                }

                string? def = string.IsNullOrWhiteSpace(input.Default) ? null : input.Default!.Trim();
                switch (input.Type)
                {
                    case RoutineInputType.Choice:
                        if (input.Options.Count(o => !string.IsNullOrWhiteSpace(o)) == 0)
                        {
                            errors.Add($"{which} is a choice, so it needs some options.");
                        }
                        else if (def != null && !input.Options.Any(o => string.Equals(o.Trim(), def, StringComparison.OrdinalIgnoreCase)))
                        {
                            errors.Add($"{which}: the default \"{def}\" isn't one of its options.");
                        }

                        break;
                    case RoutineInputType.Number:
                        if (def != null && !RoutineInput.TryParseNumber(def, out _))
                        {
                            errors.Add($"{which}: the default \"{def}\" isn't a number.");
                        }

                        break;
                    case RoutineInputType.YesNo:
                        if (def != null && RoutineInput.ParseYesNo(def) == null)
                        {
                            errors.Add($"{which}: the default should be yes or no.");
                        }

                        break;
                }
            }

            foreach (string used in Placeholders(Instructions))
            {
                if (FindInput(used) == null)
                {
                    errors.Add($"The instructions use {{{{{used}}}}}, but there's no input called {used}.");
                }
            }

            return errors;
        }

        /// <summary>
        /// Problems with the values given for a run: required inputs left blank, numbers that
        /// aren't numbers, choices that aren't options. Keyed by input name; picks are not checked.
        /// </summary>
        public List<string> CheckValues(IReadOnlyDictionary<string, string?> values)
        {
            var errors = new List<string>();
            foreach (RoutineInput input in Inputs.Where(i => i.IsTyped))
            {
                string? value = Value(values, input.Name);
                if (value == null)
                {
                    if (input.Required && input.Type != RoutineInputType.YesNo)
                    {
                        errors.Add($"{input.DisplayLabel} is required.");
                    }

                    continue;
                }

                if (input.Type == RoutineInputType.Number && !RoutineInput.TryParseNumber(value, out _))
                {
                    errors.Add($"{input.DisplayLabel} must be a number.");
                }
                else if (input.Type == RoutineInputType.Choice && !input.Options.Any(o => string.Equals(o.Trim(), value, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add($"{input.DisplayLabel} must be one of: {string.Join(", ", input.Options)}.");
                }
            }

            return errors;
        }

        /// <summary>The trimmed value for an input, or null when blank or missing.</summary>
        public static string? Value(IReadOnlyDictionary<string, string?> values, string name)
        {
            foreach (KeyValuePair<string, string?> kv in values)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return string.IsNullOrWhiteSpace(kv.Value) ? null : kv.Value!.Trim();
                }
            }

            return null;
        }

        public static string LocationName(RoutineLocation location) =>
            location == RoutineLocation.Office ? "Office routines" : "My routines";

        /// <summary>An input name close to what was typed: "Max grade %" becomes max_grade.</summary>
        public static string Suggest(string typed)
        {
            var sb = new StringBuilder();
            foreach (char c in (typed ?? "").Trim().ToLowerInvariant())
            {
                if (c < 128 && char.IsLetterOrDigit(c))
                {
                    sb.Append(c);
                }
                else if (sb.Length > 0 && sb[sb.Length - 1] != '_')
                {
                    sb.Append('_');
                }
            }

            string s = sb.ToString().Trim('_');
            if (s.Length == 0)
            {
                return "value";
            }

            return char.IsLetter(s[0]) ? s : "v_" + s;
        }
    }
}
