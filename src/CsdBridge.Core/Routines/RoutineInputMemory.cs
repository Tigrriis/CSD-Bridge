using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CsdBridge.Core.Routines
{
    /// <summary>
    /// The values each user last ran each routine with, so the run dialog starts from them
    /// (e.g. the same grades and K value, with only the road changed). One small JSON file
    /// per user; a missing or unreadable file just means the routine's defaults are used.
    /// </summary>
    public sealed class RoutineInputMemory
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };
        private readonly string _file;

        public RoutineInputMemory(string file)
        {
            _file = file;
        }

        /// <summary>The last values used, by input name (only inputs the routine still has).</summary>
        public Dictionary<string, string> Get(Routine routine)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!LoadAll().TryGetValue(Key(routine), out Dictionary<string, string>? saved))
            {
                return result;
            }

            foreach (KeyValuePair<string, string> kv in saved)
            {
                RoutineInput? input = routine.FindInput(kv.Key);
                if (input != null && input.IsTyped)
                {
                    result[input.Name] = kv.Value;
                }
            }

            return result;
        }

        public void Remember(Routine routine, IReadOnlyDictionary<string, string?> values)
        {
            try
            {
                Dictionary<string, Dictionary<string, string>> all = LoadAll();
                all[Key(routine)] = routine.Inputs
                    .Where(i => i.IsTyped)
                    .Select(i => (i.Name, Value: Routine.Value(values, i.Name)))
                    .Where(x => x.Value != null)
                    .ToDictionary(x => x.Name, x => x.Value!, StringComparer.OrdinalIgnoreCase);

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
                File.WriteAllText(_file, JsonSerializer.Serialize(all, Json), new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Remembering values is a convenience; never stop a run over it.
            }
        }

        private Dictionary<string, Dictionary<string, string>> LoadAll()
        {
            try
            {
                if (File.Exists(_file))
                {
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(_file));
                    if (loaded != null)
                    {
                        return new Dictionary<string, Dictionary<string, string>>(loaded, StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                // Unreadable: start again.
            }

            return new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        }

        private static string Key(Routine routine) => routine.Location + "/" + routine.Name.Trim().ToLowerInvariant();
    }
}
