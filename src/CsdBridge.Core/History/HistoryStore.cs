using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CsdBridge.Core.History
{
    /// <summary>
    /// Request history as JSON Lines: one file per day (yyyy-MM-dd.jsonl), one finished
    /// request per line. Plain text, so it can also be read or searched by hand.
    /// </summary>
    public sealed class HistoryStore
    {
        private static readonly JsonSerializerOptions Json = CreateOptions();

        public HistoryStore(string folder)
        {
            Folder = folder;
        }

        public string Folder { get; }

        public void Append(RunRecord record)
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, record.StartedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
            string line = JsonSerializer.Serialize(record, Json) + "\n";

            // FileShare.ReadWrite: another BricsCAD session may be reading or appending too.
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(line);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        /// <summary>The most recent requests, newest first. Unreadable lines are skipped.</summary>
        public List<RunRecord> Load(int max = 1000)
        {
            var result = new List<RunRecord>();
            if (!Directory.Exists(Folder))
            {
                return result;
            }

            foreach (string file in Directory.GetFiles(Folder, "*.jsonl").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal))
            {
                var dayRecords = new List<RunRecord>();
                foreach (string line in ReadLines(file))
                {
                    RunRecord? record = TryParse(line);
                    if (record != null)
                    {
                        dayRecords.Add(record);
                    }
                }

                foreach (RunRecord record in dayRecords.OrderByDescending(r => r.StartedAt))
                {
                    result.Add(record);
                    if (result.Count >= max)
                    {
                        return result;
                    }
                }
            }

            return result;
        }

        private static IEnumerable<string> ReadLines(string file)
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Trim().Length > 0)
                    {
                        yield return line;
                    }
                }
            }
        }

        private static RunRecord? TryParse(string line)
        {
            try
            {
                return JsonSerializer.Deserialize<RunRecord>(line, Json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static JsonSerializerOptions CreateOptions() => new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
    }
}
