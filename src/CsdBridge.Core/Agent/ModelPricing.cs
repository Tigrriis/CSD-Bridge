using System;
using System.Collections.Generic;
using System.Globalization;

namespace CsdBridge.Core.Agent
{
    /// <summary>List prices for one model, in US dollars per million tokens.</summary>
    public sealed class TokenPrices
    {
        public TokenPrices(decimal input, decimal cacheWrite5m, decimal cacheWrite1h, decimal cacheRead, decimal output)
        {
            Input = input;
            CacheWrite5m = cacheWrite5m;
            CacheWrite1h = cacheWrite1h;
            CacheRead = cacheRead;
            Output = output;
        }

        public decimal Input { get; }
        public decimal CacheWrite5m { get; }
        public decimal CacheWrite1h { get; }
        public decimal CacheRead { get; }

        /// <summary>Output, including thinking.</summary>
        public decimal Output { get; }
    }

    /// <summary>
    /// Estimates what a request cost from its token counts, using the Claude API's list
    /// prices (global endpoint, standard speed, no batch discount). Taken from
    /// https://platform.claude.com/docs/en/about-claude/pricing in November 2026; check
    /// them against claude.com/pricing now and then. It's an estimate: a gateway set in
    /// base_url, US-only inference or refusal credits can bill differently.
    /// </summary>
    public static class ModelPricing
    {
        /// <summary>Haiku 5.5 prompts longer than this (all input tokens, cache included) pay its higher prices.</summary>
        public const long HaikuLongPromptTokens = 100_000;

        private static readonly TokenPrices HaikuShortPrompt = new TokenPrices(0.10m, 0.125m, 0.20m, 0.01m, 0.50m);
        private static readonly TokenPrices HaikuLongPrompt = new TokenPrices(0.50m, 0.625m, 1m, 0.05m, 2.50m);

        private static readonly Dictionary<string, TokenPrices> Prices = new Dictionary<string, TokenPrices>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-fable-5-1"] = new TokenPrices(10m, 12.50m, 20m, 0.25m, 50m),
            ["claude-mythos-5-1"] = new TokenPrices(10m, 12.50m, 20m, 0.25m, 50m),
            ["claude-fable-5"] = new TokenPrices(10m, 12.50m, 20m, 1m, 50m),
            [ModelCatalog.Opus] = new TokenPrices(4m, 5m, 8m, 0.20m, 20m),
            ["claude-opus-5"] = new TokenPrices(5m, 6.25m, 10m, 0.50m, 25m),
            ["claude-opus-4-8"] = new TokenPrices(5m, 6.25m, 10m, 0.50m, 25m),
            ["claude-opus-4-7"] = new TokenPrices(5m, 6.25m, 10m, 0.50m, 25m),
            ["claude-opus-4-6"] = new TokenPrices(5m, 6.25m, 10m, 0.50m, 25m),
            [ModelCatalog.Sonnet] = new TokenPrices(2m, 2.50m, 4m, 0.10m, 10m),
            ["claude-sonnet-5"] = new TokenPrices(2m, 2.50m, 4m, 0.20m, 10m),
            ["claude-sonnet-4-6"] = new TokenPrices(3m, 3.75m, 6m, 0.30m, 15m),
            [ModelCatalog.Haiku] = HaikuShortPrompt,
            ["claude-haiku-4-5"] = new TokenPrices(1m, 1.25m, 2m, 0.10m, 5m),
        };

        public static bool IsPriced(string? model) => Prices.ContainsKey(model ?? "");

        /// <summary>The prices one request pays, or null for a model not in the table.</summary>
        /// <param name="promptTokens">The request's input tokens, cache reads and cache writes together.</param>
        public static TokenPrices? For(string? model, long promptTokens)
        {
            if (!Prices.TryGetValue(model ?? "", out TokenPrices? prices))
            {
                return null;
            }

            bool haiku = string.Equals(model, ModelCatalog.Haiku, StringComparison.OrdinalIgnoreCase);
            return haiku && promptTokens > HaikuLongPromptTokens ? HaikuLongPrompt : prices;
        }

        /// <summary>Estimated cost of one request in US dollars, or null for a model not in the table.</summary>
        public static decimal? Cost(string? model, long input, long cacheRead, long cacheWrite5m, long cacheWrite1h, long output)
        {
            TokenPrices? p = For(model, input + cacheRead + cacheWrite5m + cacheWrite1h);
            if (p == null)
            {
                return null;
            }

            decimal perToken =
                input * p.Input +
                cacheRead * p.CacheRead +
                cacheWrite5m * p.CacheWrite5m +
                cacheWrite1h * p.CacheWrite1h +
                output * p.Output;
            return perToken / 1_000_000m;
        }

        /// <summary>"US$1.24", "US$0.12", "US$0.004" or "under US$0.001".</summary>
        public static string Format(decimal usd)
        {
            if (usd > 0m && usd < 0.0005m)
            {
                return "under US$0.001";
            }

            string format = usd < 0.1m ? "0.000" : "0.00";
            return "US$" + usd.ToString(format, CultureInfo.InvariantCulture);
        }
    }
}
