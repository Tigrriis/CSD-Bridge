using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace CsdBridge.Core.Agent
{
    /// <summary>How much effort (thinking depth, care) Claude spends on a request.</summary>
    public enum EffortMode
    {
        /// <summary>Pick Quick, Normal or Thorough from the request itself.</summary>
        Auto,

        /// <summary>Simple edits and questions: fastest and cheapest.</summary>
        Quick,

        /// <summary>Ordinary drafting.</summary>
        Normal,

        /// <summary>Big or multi-step jobs: slower, most careful.</summary>
        Thorough,
    }

    /// <summary>The API effort level used for each mode (low | medium | high | xhigh | max).</summary>
    public sealed class EffortLevels
    {
        public string Quick { get; set; } = "low";
        public string Normal { get; set; } = "high";
        public string Thorough { get; set; } = "xhigh";

        public string For(EffortMode mode) =>
            mode == EffortMode.Quick ? Quick : mode == EffortMode.Thorough ? Thorough : Normal;
    }

    public sealed class EffortChoice
    {
        public EffortChoice(EffortMode tier, string effort, string reason, bool automatic)
        {
            Tier = tier;
            Effort = effort;
            Reason = reason;
            Automatic = automatic;
        }

        /// <summary>Quick, Normal or Thorough (never Auto).</summary>
        public EffortMode Tier { get; }

        /// <summary>API effort level, e.g. "high".</summary>
        public string Effort { get; }

        public string Reason { get; }
        public bool Automatic { get; }

        /// <summary>e.g. "Quick (auto: a simple edit)", "Thorough (set by you)" or "Thorough (set by the routine)".</summary>
        public string Describe() => $"{Tier} ({(Automatic ? "auto: " : "")}{Reason})";
    }

    /// <summary>
    /// Chooses the effort for a request. Auto uses simple, predictable rules on the request
    /// text: no extra model call, no cost, and easy to tune. Effort only changes how hard
    /// the same model thinks; it never switches models, so the conversation and prompt
    /// cache carry on unchanged.
    /// </summary>
    public static class EffortPolicy
    {
        // First words of simple edits. ("set out ...", "put a ...", "make a ..." are drafting, not edits.)
        private static readonly string[] EditVerbs =
        {
            "move", "rotate", "scale", "mirror", "erase", "delete", "remove", "copy", "rename",
            "recolour", "recolor", "undo", "change", "shift", "nudge", "swap", "hide",
        };

        private static readonly string[] EditPhrases = { "make it ", "make them ", "make this ", "make that ", "make these " };

        private static readonly string[] QuestionStarts =
        {
            "what", "which", "where", "how many", "how long", "how big", "how far", "is ", "are ", "does ", "do ",
            "list", "measure", "check", "count", "show me",
        };

        // Words that point to a large or multi-part job.
        private static readonly Regex BigJob = new Regex(
            @"\b(layout|general arrangement|framing plan|floor plan|site plan|plan view|elevations?|cross[- ]sections?|" +
            @"sections? through|schedules?|title block|details?|redesign|from scratch|entire|whole drawing|" +
            @"all (levels|floors|grids)|every (level|floor)|reinforcement layout|setout plan|set-out plan)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static EffortChoice Choose(EffortMode mode, string prompt, int selectedCount, EffortLevels levels)
        {
            if (mode != EffortMode.Auto)
            {
                return new EffortChoice(mode, levels.For(mode), "set by you", automatic: false);
            }

            var (tier, reason) = Classify(prompt, selectedCount);
            return new EffortChoice(tier, levels.For(tier), reason, automatic: true);
        }

        /// <summary>A fixed tier chosen by something other than the user, e.g. a routine ("set by the routine").</summary>
        public static EffortChoice Fixed(EffortMode tier, EffortLevels levels, string reason)
        {
            EffortMode t = tier == EffortMode.Auto ? EffortMode.Normal : tier;
            return new EffortChoice(t, levels.For(t), reason, automatic: false);
        }

        public static (EffortMode Tier, string Reason) Classify(string prompt, int selectedCount = 0)
        {
            string text = (prompt ?? "").Trim();
            string lower = text.ToLowerInvariant();
            int words = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;

            // --- Thorough: long, multi-step or whole-drawing work
            if (words > 60)
            {
                return (EffortMode.Thorough, "a long, detailed request");
            }

            int steps = CountSteps(text);
            if (steps >= 3)
            {
                return (EffortMode.Thorough, $"{steps} separate steps");
            }

            Match big = BigJob.Match(text);
            if (big.Success)
            {
                return (EffortMode.Thorough, $"a larger job (\"{big.Value.ToLowerInvariant()}\")");
            }

            // --- Quick: short edits and questions
            if (words <= 25 && steps <= 1)
            {
                string first = lower.Split(new[] { ' ', ',', '.' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (EditVerbs.Contains(first) || EditPhrases.Any(p => lower.StartsWith(p, StringComparison.Ordinal)))
                {
                    return (EffortMode.Quick, "a simple edit");
                }

                if (QuestionStarts.Any(q => lower.StartsWith(q, StringComparison.Ordinal)) || lower.EndsWith("?", StringComparison.Ordinal))
                {
                    return (EffortMode.Quick, "a question about the drawing");
                }
            }

            return (EffortMode.Normal, "a normal drafting request");
        }

        /// <summary>Rough count of separate instructions: list items, sentences, "then"/"and also".</summary>
        private static int CountSteps(string text)
        {
            int listItems = Regex.Matches(text, @"(^|\n)\s*(\d+[.)]|[-*•])\s+", RegexOptions.CultureInvariant).Count;
            if (listItems >= 2)
            {
                return listItems;
            }

            int sentences = Regex.Matches(text, @"[.;!?](\s|$)", RegexOptions.CultureInvariant).Count;
            int thens = Regex.Matches(text, @"\b(then|and also|after that|finally)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;
            return Math.Max(1, Math.Max(sentences, thens + 1));
        }

        public static bool TryParseMode(string? value, out EffortMode mode)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "auto": mode = EffortMode.Auto; return true;
                case "quick": mode = EffortMode.Quick; return true;
                case "normal": mode = EffortMode.Normal; return true;
                case "thorough": mode = EffortMode.Thorough; return true;
                default: mode = EffortMode.Auto; return false;
            }
        }
    }
}
