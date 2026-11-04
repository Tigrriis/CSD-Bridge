using System;
using System.IO;
using System.Text.Json;
using CsdBridge.Core.Agent;
using Xunit;

namespace CsdBridge.Core.Tests
{
    public class DrawingNotesTests
    {
        [Fact]
        public void NotesAreSavedPerDrawingAndBecomeContext()
        {
            string folder = Path.Combine(Path.GetTempPath(), "csdbridge-notes-" + Guid.NewGuid().ToString("N"));
            try
            {
                var notes = new DrawingNotes(folder, @"C:\Jobs\2026-C51\PR02.dwg");
                var tools = new DrawingNotesTools(notes);
                Assert.Null(notes.AsContext());

                Assert.False(tools.Execute(DrawingNotesTools.SaveNote, Json("{\"note\":\"Road CL-01: alignment 2F4A, start ch 0\"}")).IsError);
                Assert.False(tools.Execute(DrawingNotesTools.SaveNote, Json("{\"note\":\"Existing ground: NS Stage 1\"}")).IsError);
                Assert.False(tools.Execute(DrawingNotesTools.SaveNote, Json("{\"note\":\"Existing ground: NS Stage 2\",\"replace\":2}")).IsError);

                string context = notes.AsContext()!;
                Assert.Contains("1. Road CL-01", context);
                Assert.Contains("2. Existing ground: NS Stage 2", context);
                Assert.DoesNotContain("Stage 1", context);

                // Another drawing has its own notes.
                Assert.Null(new DrawingNotes(folder, @"C:\Jobs\2026-C51\PR03.dwg").AsContext());

                Assert.False(tools.Execute(DrawingNotesTools.SaveNote, Json("{\"replace\":1,\"delete\":true}")).IsError);
                Assert.Single(notes.Load());
                Assert.True(tools.Execute(DrawingNotesTools.SaveNote, Json("{\"replace\":5,\"note\":\"x\"}")).IsError);
            }
            finally
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
        }

        private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();
    }
}
