using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CsdBridge.BricsCAD.Csd
{
    /// <summary>
    /// The read-only mode check: in read mode, buttons, menu items and commands whose
    /// names contain these words are refused, because they look like they change the design.
    /// </summary>
    internal static class CsdSafety
    {
        // Words that mark a button, menu item or command as changing the design. Refused in read mode.
        public static readonly HashSet<string> ChangingWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ok", "yes", "save", "apply", "delete", "del", "remove", "adjust", "rebuild", "recalculate", "recalc", "update", "drape",
            "import", "merge", "add", "insert", "copy", "move", "fix", "balance", "set", "accept", "finish", "replace",
            "synchronise", "synchronize", "sync", "raise", "lower", "undo", "redo", "reset", "clear", "compute", "run", "paste",
            "erase", "purge", "pack", "optimise", "optimize", "new",
        };

        public static readonly HashSet<string> ChangingCommandWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "delete", "remove", "erase", "purge", "import", "update", "rebuild", "recalculate", "compute", "synchronise", "synchronize",
            "drape", "apply", "pack", "run", "thread",
        };

        public static bool IsChanging(string text) => Tokens(text).Any(ChangingWords.Contains);

        /// <summary>
        /// The word that makes a command look like it changes things, or null. Command names are
        /// often all capitals run together (ARDUV7REBUILDMODELS), so they are searched as text too.
        /// </summary>
        public static string? ChangingCommandWord(string name, IEnumerable<string> buttons) =>
            Tokens(string.Join(" ", buttons)).FirstOrDefault(ChangingCommandWords.Contains)
            ?? ChangingCommandWords.FirstOrDefault(w => name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>Words in a name or label: "cmdAdjustAll" → cmd, adjust, all; "Rebuild Model" → rebuild, model.</summary>
        public static IEnumerable<string> Tokens(string text) =>
            Regex.Matches(text ?? "", @"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+").Cast<Match>().Select(m => m.Value.ToLowerInvariant());
    }
}
