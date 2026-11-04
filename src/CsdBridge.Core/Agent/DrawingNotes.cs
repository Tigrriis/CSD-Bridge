using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CsdBridge.Core.Tools;

namespace CsdBridge.Core.Agent
{
    /// <summary>
    /// Short facts Claude saves about a drawing (its roads and their alignment handles, start
    /// chainages, surface names, what is drafted, the user's choices for this job), so a new
    /// conversation on the same drawing starts from them instead of finding them out again.
    /// Stored per drawing path under the user's local app data, not next to the drawing.
    /// </summary>
    public sealed class DrawingNotes
    {
        public const int MaxNotes = 40;
        public const int MaxNoteLength = 400;

        private readonly string _file;

        public DrawingNotes(string folder, string drawingPath)
        {
            DrawingPath = drawingPath;
            string name = Path.GetFileNameWithoutExtension(drawingPath);
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            _file = Path.Combine(folder, $"{name}-{Hash(drawingPath)}.json");
        }

        public string DrawingPath { get; }

        public List<string> Load()
        {
            try
            {
                if (!File.Exists(_file))
                {
                    return new List<string>();
                }

                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(_file));
                return doc.RootElement.GetProperty("notes").EnumerateArray().Select(n => n.GetString() ?? "").Where(n => n.Length > 0).ToList();
            }
            catch (Exception)
            {
                return new List<string>(); // unreadable notes are ignored, not fatal
            }
        }

        public void Save(List<string> notes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            string json = JsonSerializer.Serialize(new { drawing = DrawingPath, updated = DateTime.Now.ToString("s"), notes }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_file, json);
        }

        /// <summary>The notes as context for the first message of a new conversation, or null.</summary>
        public string? AsContext()
        {
            List<string> notes = Load();
            if (notes.Count == 0)
            {
                return null;
            }

            var sb = new StringBuilder("<drawing_notes>\nNotes saved in earlier sessions on this drawing (check them if something seems to have changed):\n");
            for (int i = 0; i < notes.Count; i++)
            {
                sb.Append(i + 1).Append(". ").Append(notes[i]).Append('\n');
            }

            return sb.Append("</drawing_notes>").ToString();
        }

        private static string Hash(string path)
        {
            using SHA256 sha = SHA256.Create();
            byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(path.Trim().ToLowerInvariant()));
            return BitConverter.ToString(h, 0, 4).Replace("-", "").ToLowerInvariant();
        }
    }

    /// <summary>The save_drawing_note tool.</summary>
    public sealed class DrawingNotesTools : IToolExtension
    {
        public const string SaveNote = "save_drawing_note";

        private readonly DrawingNotes _notes;

        public DrawingNotesTools(DrawingNotes notes)
        {
            _notes = notes;
        }

        public IReadOnlyList<ToolDefinition> Tools { get; } = new List<ToolDefinition>
        {
            new ToolDefinition(
                SaveNote,
                "Save, update or delete a short note about this drawing for future conversations: facts that took effort to find " +
                "and will be needed again (e.g. \"Road CL-01: alignment = polyline 2F4A, start ch 0, strings on CSD-3D-CL-01-*\", " +
                "\"Existing ground surface: NS Stage 1\", \"CL-01 drafted ch 0-189 on 2026-11-04\", user preferences for this job). " +
                "Give 'replace' (a note number) to update a note, or delete: true with 'replace' to remove one. Keep notes short " +
                "and factual; don't save things that are quick to read from the drawing.",
                "{\"type\":\"object\",\"properties\":{" +
                "\"note\":{\"type\":\"string\",\"maxLength\":400}," +
                "\"replace\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"Note number to replace (from <drawing_notes>).\"}," +
                "\"delete\":{\"type\":\"boolean\"}}}"),
        };

        public string? PromptSection =>
            "## Drawing notes\n" +
            "Notes saved in earlier conversations about this drawing arrive in <drawing_notes> at the start of a conversation: use " +
            "them instead of finding the same things out again. When you work out something about the drawing that will be needed " +
            "next time (roads and their alignment handles, start chainages, surface names, what has been drafted, the user's " +
            "choices for this job), save it with save_drawing_note. Update a note rather than adding a near-duplicate.";

        public bool RunsOnMainThread(string toolName) => false;

        public string? DescribeCall(string toolName, JsonElement input) => "saving a note about this drawing";

        public ToolOutcome Execute(string toolName, JsonElement input)
        {
            try
            {
                JsonArgs.RequireObject(input, "");
                List<string> notes = _notes.Load();
                int? replace = JsonArgs.OptionalInt(input, "replace", "", 1, 1000);
                bool delete = JsonArgs.OptionalBool(input, "delete", "", false);
                string? note = JsonArgs.OptionalString(input, "note", "")?.Trim();
                if (note != null && note.Length > DrawingNotes.MaxNoteLength)
                {
                    note = note.Substring(0, DrawingNotes.MaxNoteLength);
                }

                if (replace.HasValue && replace.Value > notes.Count)
                {
                    throw new ToolInputException($"there are only {notes.Count} notes");
                }

                string summary;
                if (delete)
                {
                    if (!replace.HasValue)
                    {
                        throw new ToolInputException("give the note number to delete in 'replace'");
                    }

                    notes.RemoveAt(replace.Value - 1);
                    summary = $"deleted note {replace}";
                }
                else if (string.IsNullOrEmpty(note))
                {
                    throw new ToolInputException("note is required");
                }
                else if (replace.HasValue)
                {
                    notes[replace.Value - 1] = note!;
                    summary = $"updated note {replace}";
                }
                else
                {
                    if (notes.Count >= DrawingNotes.MaxNotes)
                    {
                        throw new ToolInputException($"this drawing already has {DrawingNotes.MaxNotes} notes; replace or delete one first");
                    }

                    notes.Add(note!);
                    summary = $"saved note {notes.Count}";
                }

                _notes.Save(notes);
                return new ToolOutcome($"{summary}. The drawing now has {notes.Count} note(s).", false) { Summary = summary };
            }
            catch (ToolInputException ex)
            {
                return new ToolOutcome("Invalid input: " + ex.Message, true) { Summary = "rejected invalid input" };
            }
            catch (IOException ex)
            {
                return new ToolOutcome("Couldn't save the note: " + ex.Message, true) { Summary = ex.Message };
            }
        }
    }
}
