using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsdBridge.Core.Agent;

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// Per-user settings in %APPDATA%\CsdBridge\settings.json, and the API key stored
    /// encrypted for the current Windows user (DPAPI) in %APPDATA%\CsdBridge\apikey.bin.
    /// </summary>
    internal sealed class BridgeSettings
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CsdBridge.ApiKey.v1");

        public static string Folder { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CsdBridge");

        public static string SettingsPath => Path.Combine(Folder, "settings.json");

        private static string KeyPath => Path.Combine(Folder, "apikey.bin");

        public static string LogFolder { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsdBridge", "logs");

        [JsonPropertyName("model")]
        public string Model { get; set; } = "claude-opus-5-5";

        /// <summary>Effort level for Normal requests: low | medium | high | xhigh | max.</summary>
        [JsonPropertyName("effort")]
        public string Effort { get; set; } = "high";

        /// <summary>auto | quick | normal | thorough. Auto picks per request.</summary>
        [JsonPropertyName("effort_mode")]
        public string EffortModeName { get; set; } = "auto";

        /// <summary>Effort level for Quick requests (simple edits and questions).</summary>
        [JsonPropertyName("effort_quick")]
        public string EffortQuick { get; set; } = "low";

        /// <summary>Effort level for Thorough requests (big, multi-step jobs).</summary>
        [JsonPropertyName("effort_thorough")]
        public string EffortThorough { get; set; } = "xhigh";

        [JsonPropertyName("max_turns")]
        public int MaxTurns { get; set; } = 30;

        [JsonPropertyName("server_side_fallback")]
        public bool ServerSideFallback { get; set; } = true;

        /// <summary>
        /// Office standards file. Point this at a network share so the whole office uses
        /// one file. Empty means office-standards.md next to the add-in DLL.
        /// </summary>
        /// <summary>Civil Site Design tools: "off", "read" (look but don't change; default) or "edit".</summary>
        [JsonPropertyName("csd_mode")]
        public string CsdMode { get; set; } = "read";

        [JsonPropertyName("standards_file")]
        public string? StandardsFile { get; set; }

        /// <summary>Optional API base URL, e.g. an internal gateway that holds the key.</summary>
        [JsonPropertyName("base_url")]
        public string? BaseUrl { get; set; }

        public static BridgeSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    return JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(SettingsPath)) ?? new BridgeSettings();
                }
            }
            catch (Exception)
            {
                // Fall back to defaults; AISTATUS shows which file is in use.
            }

            var defaults = new BridgeSettings();
            defaults.Save(); // write a template the user can edit
            return defaults;
        }

        public void Save()
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }

        public EffortLevels EffortLevels() => new EffortLevels
        {
            Quick = DraftingAgent.NormalizeEffort(EffortQuick),
            Normal = DraftingAgent.NormalizeEffort(Effort),
            Thorough = DraftingAgent.NormalizeEffort(EffortThorough),
        };

        public AgentOptions ToAgentOptions() => new AgentOptions
        {
            Model = Model,
            Effort = Effort,
            MaxTurns = MaxTurns,
            ServerSideFallback = ServerSideFallback,
            HostName = "BricsCAD",
            OfficeStandards = LoadStandards(),
        };

        public string ResolvedStandardsPath()
        {
            if (!string.IsNullOrWhiteSpace(StandardsFile))
            {
                return Environment.ExpandEnvironmentVariables(StandardsFile!);
            }

            string dir = Path.GetDirectoryName(typeof(BridgeSettings).Assembly.Location) ?? ".";
            return Path.Combine(dir, "office-standards.md");
        }

        private string? LoadStandards()
        {
            string path = ResolvedStandardsPath();
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        // ------------------------------------------------------------- API key

        /// <summary>The stored key, else ANTHROPIC_API_KEY. Null if neither is set.</summary>
        public static string? GetApiKey(out string source)
        {
            try
            {
                if (File.Exists(KeyPath))
                {
                    byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), Entropy, DataProtectionScope.CurrentUser);
                    source = "stored key (AIKEY)";
                    return Encoding.UTF8.GetString(plain);
                }
            }
            catch (CryptographicException)
            {
                // Stored by another Windows user or corrupted; ignore.
            }

            string? env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            source = string.IsNullOrEmpty(env) ? "none" : "ANTHROPIC_API_KEY environment variable";
            return string.IsNullOrEmpty(env) ? null : env;
        }

        public static void SetApiKey(string? key)
        {
            Directory.CreateDirectory(Folder);
            if (string.IsNullOrWhiteSpace(key))
            {
                if (File.Exists(KeyPath))
                {
                    File.Delete(KeyPath);
                }

                return;
            }

            byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(key!.Trim()), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(KeyPath, cipher);
        }
    }
}
