using System;
using System.Collections.Generic;
using System.Linq;

namespace CsdBridge.Core.Agent
{
    /// <summary>A model offered in the chat panel's model list.</summary>
    public sealed class ModelChoice
    {
        public ModelChoice(string id, string label, string description)
        {
            Id = id;
            Label = label;
            Description = description;
        }

        /// <summary>API model id, e.g. "claude-sonnet-5-5".</summary>
        public string Id { get; }

        /// <summary>Short name for the UI, e.g. "Sonnet 5.5".</summary>
        public string Label { get; }

        public string Description { get; }

        public override string ToString() => Label;
    }

    /// <summary>The models the add-in offers, and what each one accepts.</summary>
    public static class ModelCatalog
    {
        public const string Opus = "claude-opus-5-5";
        public const string Sonnet = "claude-sonnet-5-5";
        public const string Haiku = "claude-haiku-5-5";

        public static IReadOnlyList<ModelChoice> Choices { get; } = new[]
        {
            new ModelChoice(Opus, "Opus 5.5", "most capable; slowest and most expensive"),
            new ModelChoice(Sonnet, "Sonnet 5.5", "faster and cheaper than Opus"),
            new ModelChoice(Haiku, "Haiku 5.5", "fastest and cheapest"),
        };

        /// <summary>Models that accept a per-message effort change (an effort-only system message).</summary>
        private static readonly HashSet<string> PerMessageEffortModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "claude-fable-5-1", "claude-mythos-5-1", Opus, "claude-opus-5", Sonnet, Haiku,
        };

        /// <summary>Models with no server-side refusal fallback (the API ignores or rejects "fallbacks").</summary>
        private static readonly HashSet<string> NoServerSideFallback = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Haiku,
        };

        public static bool SupportsPerMessageEffort(string? model) => PerMessageEffortModels.Contains(model ?? "");

        public static bool SupportsServerSideFallback(string? model) => !NoServerSideFallback.Contains(model ?? "");

        /// <summary>"Sonnet 5.5" for a listed model, otherwise the id itself.</summary>
        public static string Label(string? model) =>
            Choices.FirstOrDefault(c => string.Equals(c.Id, model, StringComparison.OrdinalIgnoreCase))?.Label ?? (model ?? "");

        /// <summary>The listed model with this id or label ("sonnet", "Haiku 5.5"), or null.</summary>
        public static ModelChoice? Find(string? idOrLabel)
        {
            string s = (idOrLabel ?? "").Trim();
            if (s.Length == 0)
            {
                return null;
            }

            return Choices.FirstOrDefault(c =>
                string.Equals(c.Id, s, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Label, s, StringComparison.OrdinalIgnoreCase) ||
                c.Label.StartsWith(s + " ", StringComparison.OrdinalIgnoreCase));
        }
    }
}
