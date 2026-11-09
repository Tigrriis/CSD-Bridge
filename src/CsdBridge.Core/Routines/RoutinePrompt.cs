using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CsdBridge.Core.Routines
{
    /// <summary>
    /// Builds the user message for a routine run. The routine goes in the user message,
    /// never the system prompt, so the system prompt and its cache stay the same.
    ///
    /// The instructions are sent as written, with their {{name}} placeholders, and the values
    /// follow in &lt;routine_inputs&gt;. Keeping the placeholders means that if the user asks to
    /// update the routine later in the same chat, Claude still has its original wording.
    /// </summary>
    public static class RoutinePrompt
    {
        public static string BuildRun(Routine routine, IReadOnlyDictionary<string, string?> values)
        {
            string name = routine.Name.Trim();
            var sb = new StringBuilder();
            sb.Append("Run the routine \"").Append(name).Append("\" below. Follow its steps in order");
            if (routine.Inputs.Count > 0)
            {
                sb.Append("; {{name}} in it stands for that input in <routine_inputs>");
            }

            sb.Append(". If a step doesn't fit this drawing, stop and ask instead of guessing.\n\n");

            sb.Append("<routine name=\"").Append(name.Replace("\"", "'")).Append("\">\n");
            if (!string.IsNullOrWhiteSpace(routine.Description))
            {
                sb.Append("Purpose: ").Append(routine.Description!.Trim()).Append("\n\n");
            }

            if (routine.CsdEdit)
            {
                sb.Append("This routine changes Civil Site Design values (needs CSD edit mode).\n\n");
            }

            sb.Append(routine.Instructions.Replace("\r\n", "\n").Trim()).Append("\n</routine>");

            if (routine.Inputs.Count > 0)
            {
                sb.Append("\n\n<routine_inputs>\n");
                foreach (RoutineInput input in routine.Inputs)
                {
                    sb.Append("{{").Append(input.Name).Append("}} ").Append(input.DisplayLabel).Append(": ")
                      .Append(DescribeValue(input, Routine.Value(values, input.Name))).Append('\n');
                }

                sb.Append("</routine_inputs>");
            }

            return sb.ToString();
        }

        /// <summary>A short line for the chat panel and command line, e.g. "Routine: Kerb returns (Radius = 6 m)".</summary>
        public static string Describe(Routine routine, IReadOnlyDictionary<string, string?> values)
        {
            List<string> parts = routine.Inputs
                .Where(i => i.IsTyped)
                .Select(i => (i, v: Routine.Value(values, i.Name)))
                .Where(x => x.v != null)
                .Select(x => $"{x.i.DisplayLabel} = {FormatValue(x.i, x.v!)}")
                .ToList();

            string text = "Routine: " + routine.Name.Trim() + (parts.Count > 0 ? " (" + string.Join(", ", parts) + ")" : "");
            return text.Length > 300 ? text.Substring(0, 297) + "..." : text;
        }

        private static string DescribeValue(RoutineInput input, string? value)
        {
            switch (input.Type)
            {
                case RoutineInputType.Point:
                    return "a point in the drawing; ask the user to pick it with pick_point when you need it" + Help(input) + ".";
                case RoutineInputType.Selection:
                    return "objects in the drawing; use the ones the user selected before starting (in <cad_context>), " +
                           "or if there are none, ask with select_entities when you need them" + Help(input) + ".";
                case RoutineInputType.YesNo:
                    return RoutineInput.ParseYesNo(value) == true ? "yes" : "no";
                default:
                    return value == null ? "(blank)" : FormatValue(input, value);
            }
        }

        private static string FormatValue(RoutineInput input, string value)
        {
            if (input.Type == RoutineInputType.YesNo)
            {
                return RoutineInput.ParseYesNo(value) == true ? "yes" : "no";
            }

            return string.IsNullOrWhiteSpace(input.Unit) ? value : value + " " + input.Unit!.Trim();
        }

        private static string Help(RoutineInput input) =>
            string.IsNullOrWhiteSpace(input.Help) ? "" : " (" + input.Help!.Trim().TrimEnd('.') + ")";
    }
}
