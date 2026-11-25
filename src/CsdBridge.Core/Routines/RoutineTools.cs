using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using CsdBridge.Core.Agent;
using CsdBridge.Core.Tools;

namespace CsdBridge.Core.Routines
{
    /// <summary>A routine Claude wrote, waiting for the user to check it in the editor and save it.</summary>
    public sealed class RoutineDraft
    {
        public RoutineDraft(Routine routine, Routine? replaces, string? note)
        {
            Routine = routine;
            Replaces = replaces;
            Note = note;
        }

        /// <summary>The draft's content (not saved to any file yet).</summary>
        public Routine Routine { get; }

        /// <summary>The saved routine this revises, as loaded, or null for a new one.</summary>
        public Routine? Replaces { get; }

        /// <summary>Anything the user should know, e.g. that the routine to update wasn't found.</summary>
        public string? Note { get; }
    }

    /// <summary>
    /// The save_routine tool: Claude turns the work done in a chat into a routine. Nothing is
    /// saved by the tool itself; the draft goes to the host, which opens the routine editor
    /// for the user to check, change and save once the request ends.
    /// </summary>
    public sealed class RoutineTools : IToolExtension
    {
        public const string SaveRoutine = "save_routine";

        /// <summary>What the chat panel's "Save this chat as a routine" sends.</summary>
        public const string SaveChatRequest =
            "Save what we did in this chat as a routine with save_routine, following the routine guidelines. " +
            "If this chat ran a routine, update that one instead.";

        private readonly Func<string, Routine?> _find;
        private readonly Action<RoutineDraft> _onDraft;

        /// <param name="find">Looks up a saved routine by name (for updates), or returns null.</param>
        /// <param name="onDraft">Receives the draft. Called on the agent's background thread.</param>
        public RoutineTools(Func<string, Routine?> find, Action<RoutineDraft> onDraft)
        {
            _find = find ?? throw new ArgumentNullException(nameof(find));
            _onDraft = onDraft ?? throw new ArgumentNullException(nameof(onDraft));
        }

        public IReadOnlyList<ToolDefinition> Tools { get; } = new List<ToolDefinition>
        {
            new ToolDefinition(
                SaveRoutine,
                "Write a routine: a named, reusable procedure with inputs that the user runs later from the Routines dialog. " +
                "Only call this when the user asks to save work as a routine or to update one. It opens the routine editor for the " +
                "user to check and save when this request ends; nothing is saved until they do.",
                "{\"type\":\"object\",\"properties\":{" +
                "\"name\":{\"type\":\"string\",\"maxLength\":80,\"description\":\"Short and specific, e.g. \\\"Kerb returns at a T-junction\\\".\"}," +
                "\"description\":{\"type\":\"string\",\"maxLength\":300,\"description\":\"One line on what it does, shown in the list.\"}," +
                "\"effort\":{\"type\":\"string\",\"enum\":[\"default\",\"quick\",\"normal\",\"thorough\"],\"description\":\"default uses the user's effort setting.\"}," +
                "\"csd_edit\":{\"type\":\"boolean\",\"description\":\"True if it changes Civil Site Design values (needs AICSD Edit).\"}," +
                "\"inputs\":{\"type\":\"array\",\"maxItems\":30,\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"name\":{\"type\":\"string\",\"description\":\"Letters, digits and _ (e.g. max_grade); used as {{name}} in the instructions.\"}," +
                "\"label\":{\"type\":\"string\",\"description\":\"What the run dialog shows, e.g. \\\"Max grade\\\".\"}," +
                "\"type\":{\"type\":\"string\",\"enum\":[\"text\",\"number\",\"choice\",\"yesno\",\"point\",\"selection\"]," +
                "\"description\":\"text/number/choice/yesno are asked in the run dialog; point is picked when you ask with pick_point; " +
                "selection is the objects selected before the run (or asked for with select_entities).\"}," +
                "\"default\":{\"type\":\"string\"}," +
                "\"options\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"For choice inputs.\"}," +
                "\"unit\":{\"type\":\"string\",\"description\":\"e.g. %, m.\"}," +
                "\"required\":{\"type\":\"boolean\"}," +
                "\"help\":{\"type\":\"string\",\"description\":\"A hint for the run dialog.\"}" +
                "},\"required\":[\"name\",\"type\"]}}," +
                "\"instructions\":{\"type\":\"string\",\"description\":\"Markdown: numbered steps, rules and final checks. Refer to inputs as {{name}}.\"}," +
                "\"replaces\":{\"type\":\"string\",\"description\":\"Name of the saved routine this updates, if any.\"}" +
                "},\"required\":[\"name\",\"description\",\"instructions\"]}"),
        };

        public string? PromptSection =>
            "## Routines\n" +
            "The user can save a chat as a routine: a named procedure with inputs, run later from the Routines dialog by them or " +
            "colleagues, in a fresh chat on another drawing. Call save_routine only when the user asks to save or update one. " +
            "Write it so that chat can follow it cold:\n" +
            "- Inputs: values that will differ next time (road names, chainages, handles, job-specific layers, picked points) " +
            "become inputs, written {{name}} in the instructions. Use point and selection inputs for things to pick or select in the " +
            "drawing. Give defaults only for values likely to be reused (design limits, office settings).\n" +
            "- Instructions: numbered steps in the order that worked, naming the tools that did each step and the settings that " +
            "mattered. Turn the user's corrections and answers into rules (e.g. \"Kerbs go on AE CIV KERB\"). Leave out dead ends " +
            "and anything specific to this drawing.\n" +
            "- End with the checks to make before finishing.\n" +
            "A routine run arrives as <routine> with its values in <routine_inputs>. If the user asks to save after running one, " +
            "update it: same name, replaces set to that name, keep what still applies and fold in what changed.";

        public bool RunsOnMainThread(string toolName) => false;

        public string? DescribeCall(string toolName, JsonElement input) => "writing the routine";

        public ToolOutcome Execute(string toolName, JsonElement input)
        {
            Routine routine;
            string? replacesName;
            try
            {
                JsonArgs.RequireObject(input, "");
                routine = ReadRoutine(input, out replacesName);
            }
            catch (ToolInputException ex)
            {
                return new ToolOutcome("Invalid input: " + ex.Message, true) { Summary = "rejected invalid input" };
            }

            List<string> problems = routine.Validate();
            if (problems.Count > 0)
            {
                return new ToolOutcome(
                    "The routine wasn't sent to the user. Fix these and call save_routine again:\n- " + string.Join("\n- ", problems),
                    true) { Summary = "the routine has problems" };
            }

            Routine? replaces = null;
            string? note = null;
            if (replacesName != null)
            {
                try
                {
                    replaces = _find(replacesName);
                }
                catch (Exception ex)
                {
                    note = $"Couldn't look up \"{replacesName}\": {ex.Message}";
                }

                if (replaces == null && note == null)
                {
                    note = $"There's no saved routine called \"{replacesName}\", so this will be saved as a new one.";
                }
            }

            _onDraft(new RoutineDraft(routine, replaces, note));

            string what = replaces != null ? $"the update to \"{replaces.Name}\"" : $"\"{routine.Name}\"";
            return new ToolOutcome(
                $"Sent {what} to the routine editor ({routine.Inputs.Count} input(s)). It opens when this request ends, so the user " +
                "can check and change it before saving. Tell them so in one line; don't repeat the routine." +
                (note != null ? " Note for the user: " + note : ""),
                false)
            { Summary = replaces != null ? $"update to \"{replaces.Name}\" ready to review" : $"routine \"{routine.Name}\" ready to review" };
        }

        private static Routine ReadRoutine(JsonElement input, out string? replaces)
        {
            var routine = new Routine
            {
                Name = JsonArgs.RequireString(input, "name", "").Trim(),
                Description = JsonArgs.OptionalString(input, "description", "")?.Trim(),
                Instructions = JsonArgs.RequireString(input, "instructions", "").Trim(),
                CsdEdit = JsonArgs.OptionalBool(input, "csd_edit", "", false),
            };

            string? effort = JsonArgs.OptionalString(input, "effort", "");
            if (effort != null && !string.Equals(effort, "default", StringComparison.OrdinalIgnoreCase))
            {
                if (!EffortPolicy.TryParseMode(effort, out EffortMode mode))
                {
                    throw new ToolInputException("effort must be default, quick, normal or thorough");
                }

                routine.Effort = mode == EffortMode.Auto ? (EffortMode?)null : mode;
            }

            List<JsonElement>? inputs = JsonArgs.OptionalArray(input, "inputs", "", 0, Routine.MaxInputs);
            if (inputs != null)
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    routine.Inputs.Add(ReadInput(inputs[i], JsonArgs.Index("inputs", i)));
                }
            }

            replaces = JsonArgs.OptionalString(input, "replaces", "")?.Trim();
            if (string.IsNullOrEmpty(replaces))
            {
                replaces = null;
            }

            return routine;
        }

        private static RoutineInput ReadInput(JsonElement item, string path)
        {
            JsonArgs.RequireObject(item, path);
            string typeName = JsonArgs.RequireString(item, "type", path);
            if (!RoutineInput.TryParseType(typeName, out RoutineInputType type))
            {
                throw new ToolInputException($"{path}.type must be text, number, choice, yesno, point or selection");
            }

            var result = new RoutineInput
            {
                Name = JsonArgs.RequireString(item, "name", path).Trim(),
                Label = JsonArgs.OptionalString(item, "label", path)?.Trim(),
                Type = type,
                Default = ReadDefault(item, path),
                Unit = JsonArgs.OptionalString(item, "unit", path)?.Trim(),
                Required = JsonArgs.OptionalBool(item, "required", path, false),
                Help = JsonArgs.OptionalString(item, "help", path)?.Trim(),
            };

            List<JsonElement>? options = JsonArgs.OptionalArray(item, "options", path, 0, 50);
            if (options != null)
            {
                result.Options = JsonArgs.ReadStringList(options, JsonArgs.Path(path, "options"));
            }

            return result;
        }

        /// <summary>The default as text; numbers and true/false are accepted too.</summary>
        private static string? ReadDefault(JsonElement item, string path)
        {
            if (!JsonArgs.TryGet(item, "default", out JsonElement v))
            {
                return null;
            }

            switch (v.ValueKind)
            {
                case JsonValueKind.String:
                    string s = (v.GetString() ?? "").Trim();
                    return s.Length == 0 ? null : s;
                case JsonValueKind.Number:
                    return v.GetDouble().ToString("0.######", CultureInfo.InvariantCulture);
                case JsonValueKind.True:
                    return "yes";
                case JsonValueKind.False:
                    return "no";
                case JsonValueKind.Null:
                    return null;
                default:
                    throw new ToolInputException($"{JsonArgs.Path(path, "default")} must be a string");
            }
        }
    }
}
