using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CsdBridge.Core.Agent;

namespace CsdBridge.Core.Routines
{
    /// <summary>
    /// Reads and writes routine files: Markdown with a short header between "---" lines.
    ///
    /// <code>
    /// ---
    /// name: Smooth vertical profile
    /// description: Propose a smoother vertical for a CSD road
    /// effort: thorough
    /// csd_edit: false
    /// inputs:
    ///   - name: road
    ///     label: Road
    ///     type: text
    ///     required: true
    ///   - name: max_grade
    ///     type: number
    ///     default: 8
    ///     unit: "%"
    ///   - name: side
    ///     type: choice
    ///     options: [Left, Right, Both]
    /// ---
    ///
    /// 1. Open the VGE for {{road}} ...
    /// </code>
    ///
    /// The header is a small, fixed subset of YAML: "key: value" lines, and under inputs a
    /// list of "- key: value" items. Values may be quoted ("..." with JSON escapes, or '...').
    /// Options are written [a, b, c]. A file with no header is all instructions, named after
    /// the file, so a plain prompt saved as .md works as a routine too.
    /// </summary>
    public static class RoutineFile
    {
        private const string Fence = "---";

        private static readonly JsonSerializerOptions QuoteOptions = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep °, ² etc. readable
        };

        public static Routine Parse(string text, string? fallbackName = null)
        {
            string normalized = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('﻿');
            string[] lines = normalized.Split('\n');
            var routine = new Routine { Name = fallbackName ?? "" };

            if (lines.Length == 0 || lines[0].TrimEnd() != Fence)
            {
                routine.Instructions = normalized.Trim();
                return routine;
            }

            int close = -1;
            for (int i = 1; i < lines.Length; i++)
            {
                string t = lines[i].TrimEnd();
                if (t == Fence || t == "...")
                {
                    close = i;
                    break;
                }
            }

            if (close < 0)
            {
                routine.Instructions = normalized.Trim();
                routine.LoadProblem = "The header isn't closed with a --- line, so the whole file was read as instructions.";
                return routine;
            }

            var problems = new List<string>();
            ParseHeader(lines, 1, close, routine, problems);
            routine.Instructions = string.Join("\n", lines.Skip(close + 1)).Trim('\n').TrimEnd();
            if (string.IsNullOrWhiteSpace(routine.Name))
            {
                routine.Name = fallbackName ?? "";
            }

            if (problems.Count > 0)
            {
                routine.LoadProblem = string.Join(" ", problems);
            }

            return routine;
        }

        public static string Write(Routine routine)
        {
            var sb = new StringBuilder();
            sb.Append(Fence).Append('\n');
            Line(sb, "", "name", Scalar(routine.Name.Trim()));
            if (!string.IsNullOrWhiteSpace(routine.Description))
            {
                Line(sb, "", "description", Scalar(routine.Description!.Trim()));
            }

            if (routine.Effort is EffortMode effort && effort != EffortMode.Auto)
            {
                Line(sb, "", "effort", effort.ToString().ToLowerInvariant());
            }

            if (routine.CsdEdit)
            {
                Line(sb, "", "csd_edit", "true");
            }

            foreach (KeyValuePair<string, string> extra in routine.ExtraFields)
            {
                Line(sb, "", extra.Key, extra.Value);
            }

            if (routine.Inputs.Count > 0)
            {
                sb.Append("inputs:\n");
                foreach (RoutineInput input in routine.Inputs)
                {
                    sb.Append("  - name: ").Append(Scalar(input.Name.Trim())).Append('\n');
                    const string indent = "    ";
                    if (!string.IsNullOrWhiteSpace(input.Label))
                    {
                        Line(sb, indent, "label", Scalar(input.Label!.Trim()));
                    }

                    Line(sb, indent, "type", RoutineInput.TypeName(input.Type));
                    if (!string.IsNullOrWhiteSpace(input.Default))
                    {
                        Line(sb, indent, "default", Scalar(input.Default!.Trim()));
                    }

                    List<string> options = input.Options.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToList();
                    if (options.Count > 0)
                    {
                        Line(sb, indent, "options", "[" + string.Join(", ", options.Select(o => Scalar(o, inList: true))) + "]");
                    }

                    if (!string.IsNullOrWhiteSpace(input.Unit))
                    {
                        Line(sb, indent, "unit", Scalar(input.Unit!.Trim()));
                    }

                    if (input.Required)
                    {
                        Line(sb, indent, "required", "true");
                    }

                    if (!string.IsNullOrWhiteSpace(input.Help))
                    {
                        Line(sb, indent, "help", Scalar(input.Help!.Trim()));
                    }

                    foreach (KeyValuePair<string, string> extra in input.ExtraFields)
                    {
                        Line(sb, indent, extra.Key, extra.Value);
                    }
                }
            }

            sb.Append(Fence).Append("\n\n");
            string instructions = (routine.Instructions ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n').TrimEnd();
            if (instructions.Length > 0)
            {
                sb.Append(instructions).Append('\n');
            }

            // Windows line endings: these files are opened in all sorts of editors on Windows PCs.
            return sb.ToString().Replace("\n", "\r\n");
        }

        // ------------------------------------------------------------------ header

        private static void ParseHeader(string[] lines, int start, int end, Routine routine, List<string> problems)
        {
            bool inInputs = false;
            RoutineInput? current = null;

            for (int i = start; i < end; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();
                int lineNo = i + 1;
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                bool indented = char.IsWhiteSpace(line[0]);
                bool isItem = trimmed == "-" || trimmed.StartsWith("- ", StringComparison.Ordinal);

                if (!indented && !isItem)
                {
                    inInputs = false;
                    current = null;
                    if (!SplitKeyValue(trimmed, out string key, out string raw))
                    {
                        problems.Add($"Line {lineNo} should be \"key: value\".");
                        continue;
                    }

                    if (key == "inputs")
                    {
                        inInputs = true;
                        if (raw.Length > 0 && raw != "[]")
                        {
                            problems.Add($"Line {lineNo}: list the inputs on the lines below \"inputs:\".");
                        }

                        continue;
                    }

                    SetTop(routine, key, raw, lineNo, problems);
                    continue;
                }

                if (!inInputs)
                {
                    problems.Add($"Line {lineNo} is indented but isn't part of the inputs.");
                    continue;
                }

                string rest = trimmed;
                if (isItem)
                {
                    current = new RoutineInput();
                    routine.Inputs.Add(current);
                    rest = trimmed.Substring(1).Trim();
                    if (rest.Length == 0)
                    {
                        continue;
                    }
                }

                if (current == null)
                {
                    problems.Add($"Line {lineNo}: start each input with \"- name: ...\".");
                    continue;
                }

                if (!SplitKeyValue(rest, out string inputKey, out string inputRaw))
                {
                    problems.Add($"Line {lineNo} should be \"key: value\".");
                    continue;
                }

                SetInput(current, inputKey, inputRaw, lineNo, problems);
            }
        }

        private static void SetTop(Routine routine, string key, string raw, int lineNo, List<string> problems)
        {
            switch (key)
            {
                case "name":
                    routine.Name = Unquote(raw, lineNo, problems).Trim();
                    break;
                case "description":
                    routine.Description = Blank(Unquote(raw, lineNo, problems));
                    break;
                case "effort":
                {
                    string value = Unquote(raw, lineNo, problems).Trim();
                    if (value.Length == 0 || string.Equals(value, "default", StringComparison.OrdinalIgnoreCase))
                    {
                        routine.Effort = null;
                    }
                    else if (EffortPolicy.TryParseMode(value, out EffortMode mode))
                    {
                        routine.Effort = mode == EffortMode.Auto ? (EffortMode?)null : mode;
                    }
                    else
                    {
                        problems.Add($"Line {lineNo}: effort should be quick, normal or thorough.");
                    }

                    break;
                }

                case "csd_edit":
                    routine.CsdEdit = Bool(Unquote(raw, lineNo, problems), lineNo, problems);
                    break;
                default:
                    routine.ExtraFields.Add(new KeyValuePair<string, string>(key, raw));
                    break;
            }
        }

        private static void SetInput(RoutineInput input, string key, string raw, int lineNo, List<string> problems)
        {
            switch (key)
            {
                case "name":
                    input.Name = Unquote(raw, lineNo, problems).Trim();
                    break;
                case "label":
                    input.Label = Blank(Unquote(raw, lineNo, problems));
                    break;
                case "type":
                {
                    string value = Unquote(raw, lineNo, problems);
                    if (!RoutineInput.TryParseType(value, out RoutineInputType type))
                    {
                        problems.Add($"Line {lineNo}: unknown input type \"{value}\" (read as text).");
                    }

                    input.Type = type;
                    break;
                }

                case "default":
                    input.Default = Blank(Unquote(raw, lineNo, problems));
                    break;
                case "options":
                    input.Options = ParseList(raw, lineNo, problems);
                    break;
                case "unit":
                    input.Unit = Blank(Unquote(raw, lineNo, problems));
                    break;
                case "required":
                    input.Required = Bool(Unquote(raw, lineNo, problems), lineNo, problems);
                    break;
                case "help":
                    input.Help = Blank(Unquote(raw, lineNo, problems));
                    break;
                default:
                    input.ExtraFields.Add(new KeyValuePair<string, string>(key, raw));
                    break;
            }
        }

        private static bool SplitKeyValue(string text, out string key, out string value)
        {
            int colon = text.IndexOf(':');
            if (colon <= 0)
            {
                key = value = "";
                return false;
            }

            key = text.Substring(0, colon).Trim().ToLowerInvariant();
            value = text.Substring(colon + 1).Trim();
            return key.Length > 0 && key.IndexOf(' ') < 0;
        }

        // ------------------------------------------------------------------ values

        private static string Unquote(string raw, int lineNo, List<string> problems)
        {
            string s = raw.Trim();
            if (s.StartsWith("\"", StringComparison.Ordinal))
            {
                try
                {
                    return JsonSerializer.Deserialize<string>(s) ?? "";
                }
                catch (JsonException)
                {
                    problems.Add($"Line {lineNo}: the quotes don't match.");
                    return s.Trim('"');
                }
            }

            if (s.Length >= 2 && s.StartsWith("'", StringComparison.Ordinal) && s.EndsWith("'", StringComparison.Ordinal))
            {
                return s.Substring(1, s.Length - 2).Replace("''", "'");
            }

            return s;
        }

        private static List<string> ParseList(string raw, int lineNo, List<string> problems)
        {
            string s = raw.Trim();
            if (!(s.StartsWith("[", StringComparison.Ordinal) && s.EndsWith("]", StringComparison.Ordinal)))
            {
                // Lenient: "Left, Right, Both" without brackets.
                return s.Split(',').Select(o => Unquote(o, lineNo, problems).Trim()).Where(o => o.Length > 0).ToList();
            }

            string inner = s.Substring(1, s.Length - 2);
            var items = new List<string>();
            var token = new StringBuilder();
            char quote = '\0';
            for (int i = 0; i < inner.Length; i++)
            {
                char c = inner[i];
                if (quote != '\0')
                {
                    token.Append(c);
                    if (c == '\\' && quote == '"' && i + 1 < inner.Length)
                    {
                        token.Append(inner[++i]);
                    }
                    else if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    quote = c;
                    token.Append(c);
                }
                else if (c == ',')
                {
                    items.Add(token.ToString());
                    token.Clear();
                }
                else
                {
                    token.Append(c);
                }
            }

            items.Add(token.ToString());
            return items.Select(o => Unquote(o, lineNo, problems).Trim()).Where(o => o.Length > 0).ToList();
        }

        private static bool Bool(string value, int lineNo, List<string> problems)
        {
            bool? b = RoutineInput.ParseYesNo(value);
            if (b == null && value.Trim().Length > 0)
            {
                problems.Add($"Line {lineNo}: expected true or false.");
            }

            return b ?? false;
        }

        private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private static void Line(StringBuilder sb, string indent, string key, string value) =>
            sb.Append(indent).Append(key).Append(": ").Append(value).Append('\n');

        /// <summary>The value as written in the header, quoted when it would otherwise be misread.</summary>
        internal static string Scalar(string value, bool inList = false)
        {
            if (NeedsQuotes(value, inList))
            {
                return JsonSerializer.Serialize(value, QuoteOptions);
            }

            return value;
        }

        private static bool NeedsQuotes(string s, bool inList)
        {
            if (s.Length == 0 || s != s.Trim())
            {
                return true;
            }

            if (s.IndexOfAny(new[] { '\n', '\r', '\t' }) >= 0)
            {
                return true;
            }

            if ("-?:,[]{}#&*!|>'\"%@`".IndexOf(s[0]) >= 0)
            {
                return true;
            }

            if (s.Contains(": ") || s.Contains(" #") || s.EndsWith(":", StringComparison.Ordinal))
            {
                return true;
            }

            return inList && (s.IndexOf(',') >= 0 || s.IndexOf(']') >= 0 || s.IndexOf('[') >= 0);
        }
    }
}
