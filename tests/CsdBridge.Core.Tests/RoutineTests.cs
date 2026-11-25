using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CsdBridge.Core.Agent;
using CsdBridge.Core.History;
using CsdBridge.Core.Routines;
using Xunit;

namespace CsdBridge.Core.Tests
{
    public class RoutineFileTests
    {
        private const string Sample =
            "---\n" +
            "name: Kerb returns\n" +
            "description: Fillet kerb returns at a T-junction\n" +
            "effort: thorough\n" +
            "csd_edit: true\n" +
            "inputs:\n" +
            "  - name: radius\n" +
            "    label: Return radius\n" +
            "    type: number\n" +
            "    default: 6\n" +
            "    unit: m\n" +
            "    required: true\n" +
            "  - name: side\n" +
            "    type: choice\n" +
            "    options: [Left, Right, \"Both, mirrored\"]\n" +
            "    default: Left\n" +
            "  - name: corner\n" +
            "    type: point\n" +
            "    help: The kerb corner\n" +
            "---\n" +
            "\n" +
            "1. Fillet the kerbs at {{radius}} on the {{side}} side.\n" +
            "2. Ask for the {{corner}}.\n";

        [Fact]
        public void ParsesHeaderInputsAndInstructions()
        {
            Routine r = RoutineFile.Parse(Sample);

            Assert.Null(r.LoadProblem);
            Assert.Equal("Kerb returns", r.Name);
            Assert.Equal("Fillet kerb returns at a T-junction", r.Description);
            Assert.Equal(EffortMode.Thorough, r.Effort);
            Assert.True(r.CsdEdit);
            Assert.Equal(3, r.Inputs.Count);

            RoutineInput radius = r.Inputs[0];
            Assert.Equal("radius", radius.Name);
            Assert.Equal("Return radius", radius.DisplayLabel);
            Assert.Equal(RoutineInputType.Number, radius.Type);
            Assert.Equal("6", radius.Default);
            Assert.Equal("m", radius.Unit);
            Assert.True(radius.Required);

            Assert.Equal(new[] { "Left", "Right", "Both, mirrored" }, r.Inputs[1].Options);
            Assert.Equal("Side", r.Inputs[1].DisplayLabel);
            Assert.Equal(RoutineInputType.Point, r.Inputs[2].Type);
            Assert.False(r.Inputs[2].IsTyped);

            Assert.StartsWith("1. Fillet the kerbs at {{radius}}", r.Instructions);
            Assert.EndsWith("Ask for the {{corner}}.", r.Instructions);
            Assert.Empty(r.Validate());
        }

        [Fact]
        public void WriteThenParseGivesTheSameRoutine()
        {
            var original = new Routine
            {
                Name = "Tricky: values # here",
                Description = "  leading spaces, \"quotes\", 90° and a: colon",
                Effort = EffortMode.Quick,
                Instructions = "Line one {{a}}\r\n\r\n  - indented\r\nLast line",
                Inputs =
                {
                    new RoutineInput { Name = "a", Label = "- dash first", Type = RoutineInputType.Choice, Options = { "x, y", "[z]", "'q'" }, Default = "x, y" },
                    new RoutineInput { Name = "b", Type = RoutineInputType.YesNo, Default = "yes", Help = "two\nlines" },
                    new RoutineInput { Name = "c", Type = RoutineInputType.Selection, Required = true },
                },
            };

            string text = RoutineFile.Write(original);
            Assert.Contains("\r\n", text);
            Routine back = RoutineFile.Parse(text);

            Assert.Null(back.LoadProblem);
            Assert.Equal(original.Name, back.Name);
            Assert.Equal(original.Description!.Trim(), back.Description);
            Assert.Equal(EffortMode.Quick, back.Effort);
            Assert.False(back.CsdEdit);
            Assert.Equal("Line one {{a}}\n\n  - indented\nLast line", back.Instructions);
            Assert.Equal(original.Inputs[0].Label, back.Inputs[0].Label);
            Assert.Equal(original.Inputs[0].Options, back.Inputs[0].Options);
            Assert.Equal("x, y", back.Inputs[0].Default);
            Assert.Equal(RoutineInputType.YesNo, back.Inputs[1].Type);
            Assert.Equal("two\nlines", back.Inputs[1].Help);
            Assert.True(back.Inputs[2].Required);

            // Writing it again is stable.
            Assert.Equal(text, RoutineFile.Write(back));
        }

        [Fact]
        public void AFileWithNoHeaderIsAllInstructions()
        {
            Routine r = RoutineFile.Parse("Dimension the selected outline to office standard.\n", "Dimension outline");

            Assert.Equal("Dimension outline", r.Name);
            Assert.Equal("Dimension the selected outline to office standard.", r.Instructions);
            Assert.Empty(r.Inputs);
            Assert.Null(r.LoadProblem);
        }

        [Fact]
        public void ProblemsAreReportedNotThrown()
        {
            Routine unclosed = RoutineFile.Parse("---\nname: X\nNo closing line", "file");
            Assert.NotNull(unclosed.LoadProblem);
            Assert.Contains("No closing line", unclosed.Instructions);

            Routine odd = RoutineFile.Parse("﻿---\r\nname: X\r\neffort: extreme\r\n  stray: line\r\ninputs:\r\n  - name: a\r\n    type: colour\r\n---\r\nDo {{a}}");
            Assert.Equal("X", odd.Name);
            Assert.Contains("effort", odd.LoadProblem);
            Assert.Contains("indented", odd.LoadProblem);
            Assert.Contains("unknown input type", odd.LoadProblem);
            Assert.Equal(RoutineInputType.Text, odd.Inputs[0].Type);
            Assert.Equal("Do {{a}}", odd.Instructions);
        }

        [Fact]
        public void UnknownKeysAreKept()
        {
            string text = "---\nname: X\nversion: 2\ninputs:\n  - name: a\n    colour: red\n---\nDo {{a}}\n";
            Routine r = RoutineFile.Parse(text);
            string written = RoutineFile.Write(r);

            Assert.Contains("version: 2", written);
            Assert.Contains("    colour: red", written);
        }

        [Fact]
        public void TheShippedExampleIsValid()
        {
            string file = Path.Combine(AppContext.BaseDirectory, "routine-examples", "smooth-vertical-profile.md");
            Routine r = RoutineFile.Parse(File.ReadAllText(file));

            Assert.Null(r.LoadProblem);
            Assert.Equal("Smooth vertical profile", r.Name);
            Assert.Empty(r.Validate());
            Assert.Contains(r.Inputs, i => i.Name == "road" && i.Required);
        }
    }

    public class RoutineValidationTests
    {
        [Fact]
        public void ValidateExplainsEachProblem()
        {
            var r = new Routine
            {
                Name = " ",
                Instructions = "Use {{road}} and {{missing}}",
                Inputs =
                {
                    new RoutineInput { Name = "road" },
                    new RoutineInput { Name = "Road" },
                    new RoutineInput { Name = "max grade" },
                    new RoutineInput { Name = "side", Type = RoutineInputType.Choice },
                    new RoutineInput { Name = "lane", Type = RoutineInputType.Choice, Options = { "1", "2" }, Default = "3" },
                    new RoutineInput { Name = "k", Type = RoutineInputType.Number, Default = "ten" },
                    new RoutineInput { Name = "ok", Type = RoutineInputType.YesNo, Default = "maybe" },
                },
            };

            List<string> errors = r.Validate();
            Assert.Contains(errors, e => e.Contains("name"));
            Assert.Contains(errors, e => e.Contains("\"Road\" is listed twice"));
            Assert.Contains(errors, e => e.Contains("max_grade"));
            Assert.Contains(errors, e => e.Contains("needs some options"));
            Assert.Contains(errors, e => e.Contains("\"3\" isn't one of its options"));
            Assert.Contains(errors, e => e.Contains("isn't a number"));
            Assert.Contains(errors, e => e.Contains("yes or no"));
            Assert.Contains(errors, e => e.Contains("{{missing}}"));
            Assert.DoesNotContain(errors, e => e.Contains("{{road}}"));
        }

        [Fact]
        public void CheckValuesFindsMissingAndBadValues()
        {
            var r = new Routine
            {
                Name = "R",
                Instructions = "x",
                Inputs =
                {
                    new RoutineInput { Name = "road", Required = true },
                    new RoutineInput { Name = "grade", Type = RoutineInputType.Number },
                    new RoutineInput { Name = "side", Type = RoutineInputType.Choice, Options = { "Left", "Right" } },
                    new RoutineInput { Name = "corner", Type = RoutineInputType.Point, Required = true },
                },
            };

            var bad = new Dictionary<string, string?> { ["road"] = " ", ["grade"] = "steep", ["side"] = "Up" };
            List<string> errors = r.CheckValues(bad);
            Assert.Equal(3, errors.Count);
            Assert.Contains("Road is required.", errors);

            var good = new Dictionary<string, string?> { ["ROAD"] = "MC01", ["grade"] = "8.5", ["side"] = "left" };
            Assert.Empty(r.CheckValues(good));
        }

        [Fact]
        public void PlaceholdersAreFoundOnceEach()
        {
            Assert.Equal(new[] { "a", "b_2" }, Routine.Placeholders("{{a}} {{ b_2 }} {{A}} {single} {{not valid}}"));
            Assert.Equal("max_grade", Routine.Suggest("Max grade %"));
            Assert.Equal("v_2nd", Routine.Suggest("2nd"));
        }
    }

    public class RoutinePromptTests
    {
        private static Routine Kerbs() => new Routine
        {
            Name = "Kerb returns",
            Description = "Fillet kerb returns",
            Instructions = "Fillet at {{radius}}. Ask for {{corner}}. Use {{edges}}.",
            Inputs =
            {
                new RoutineInput { Name = "radius", Label = "Radius", Type = RoutineInputType.Number, Unit = "m" },
                new RoutineInput { Name = "note", Type = RoutineInputType.Text },
                new RoutineInput { Name = "tidy", Label = "Tidy up", Type = RoutineInputType.YesNo },
                new RoutineInput { Name = "corner", Type = RoutineInputType.Point, Help = "The kerb corner." },
                new RoutineInput { Name = "edges", Type = RoutineInputType.Selection },
            },
        };

        [Fact]
        public void RunPromptKeepsPlaceholdersAndListsValues()
        {
            var values = new Dictionary<string, string?> { ["radius"] = "6", ["tidy"] = "yes" };
            string prompt = RoutinePrompt.BuildRun(Kerbs(), values);

            Assert.StartsWith("Run the routine \"Kerb returns\" below.", prompt);
            Assert.Contains("<routine name=\"Kerb returns\">", prompt);
            Assert.Contains("Purpose: Fillet kerb returns", prompt);
            Assert.Contains("Fillet at {{radius}}. Ask for {{corner}}.", prompt);
            Assert.Contains("{{radius}} Radius: 6 m", prompt);
            Assert.Contains("{{note}} Note: (blank)", prompt);
            Assert.Contains("{{tidy}} Tidy up: yes", prompt);
            Assert.Contains("{{corner}} Corner: a point in the drawing; ask the user to pick it with pick_point when you need it (The kerb corner).", prompt);
            Assert.Contains("{{edges}} Edges: objects in the drawing; use the ones the user selected before starting", prompt);
            Assert.EndsWith("</routine_inputs>", prompt);
        }

        [Fact]
        public void DescribeIsShort()
        {
            var values = new Dictionary<string, string?> { ["radius"] = "6", ["tidy"] = "no" };
            Assert.Equal("Routine: Kerb returns (Radius = 6 m, Tidy up = no)", RoutinePrompt.Describe(Kerbs(), values));

            var plain = new Routine { Name = "Just do it", Instructions = "x" };
            Assert.Equal("Routine: Just do it", RoutinePrompt.Describe(plain, new Dictionary<string, string?>()));
            Assert.DoesNotContain("routine_inputs", RoutinePrompt.BuildRun(plain, new Dictionary<string, string?>()));
        }

        [Fact]
        public void ARoutineCanFixItsEffort()
        {
            var levels = new EffortLevels();
            EffortChoice choice = EffortPolicy.Fixed(EffortMode.Thorough, levels, "set by the routine");
            Assert.Equal("xhigh", choice.Effort);
            Assert.Equal("Thorough (set by the routine)", choice.Describe());
            Assert.Equal(EffortMode.Normal, EffortPolicy.Fixed(EffortMode.Auto, levels, "x").Tier);
        }

        [Fact]
        public void HistoryKeepsTheRoutineName()
        {
            string folder = Path.Combine(Path.GetTempPath(), "csdbridge-hist-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new HistoryStore(folder);
                store.Append(new RunRecord { Prompt = "Run the routine ...", Routine = "Kerb returns", Status = "Completed" });
                RunRecord loaded = store.Load().Single();
                Assert.Equal("Kerb returns", loaded.Routine);
                Assert.True(loaded.Matches("kerb ret"));
            }
            finally
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
        }
    }

    public sealed class RoutineLibraryTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "csdbridge-routines-" + Guid.NewGuid().ToString("N"));

        private string Mine => Path.Combine(_root, "mine");

        private string Office => Path.Combine(_root, "office");

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        private static Routine New(string name) => new Routine { Name = name, Instructions = "Do it." };

        [Fact]
        public void SavesLoadsAndFindsByName()
        {
            var lib = new RoutineLibrary(Mine, Office);
            Routine saved = lib.Save(New("Kerb returns"), RoutineLocation.Personal);

            Assert.Equal(Path.Combine(Mine, "Kerb returns.md"), saved.FilePath);
            Assert.NotNull(saved.LoadedWriteTimeUtc);

            List<Routine> all = lib.LoadAll(out List<string> problems);
            Assert.Single(all);
            Assert.Contains(problems, p => p.Contains("office routines folder wasn't found"));
            Assert.Equal("Kerb returns", lib.Find("KERB RETURNS")!.Name);
            Assert.Null(lib.Find("nothing"));

            // Same name in the same folder is refused; in the other folder it's fine.
            Assert.Throws<ArgumentException>(() => lib.Save(New("kerb returns"), RoutineLocation.Personal));
            lib.Save(New("Kerb returns"), RoutineLocation.Office);
            Assert.Equal(2, lib.LoadAll(out _).Count);
            Assert.Equal(RoutineLocation.Personal, lib.Find("Kerb returns")!.Location);
        }

        [Fact]
        public void RenameMovesTheFile()
        {
            var lib = new RoutineLibrary(Mine, null);
            Routine saved = lib.Save(New("Kerb returns"), RoutineLocation.Personal);

            Routine renamed = lib.Rename(saved, "Kerb returns: T-junction");
            Assert.Equal(Path.Combine(Mine, "Kerb returns- T-junction.md"), renamed.FilePath);
            Assert.False(File.Exists(saved.FilePath));
            Assert.Equal("Kerb returns: T-junction", lib.LoadAll(out _).Single().Name);

            // A change of case keeps the same file.
            Routine recased = lib.Rename(renamed, "Kerb Returns: T-junction");
            Assert.Equal(renamed.FilePath, recased.FilePath);
            Assert.Equal("Kerb Returns: T-junction", lib.LoadAll(out _).Single().Name);
        }

        [Fact]
        public void EditingCanMoveARoutineToTheOfficeFolder()
        {
            var lib = new RoutineLibrary(Mine, Office);
            Routine mine = lib.Save(New("Shared one"), RoutineLocation.Personal);

            Routine edited = mine.Clone();
            edited.Description = "Now for everyone";
            Routine moved = lib.Save(edited, RoutineLocation.Office, mine);

            Assert.False(File.Exists(mine.FilePath));
            Assert.Equal(Path.Combine(Office, "Shared one.md"), moved.FilePath);
            Assert.Equal(RoutineLocation.Office, lib.Find("Shared one")!.Location);
        }

        [Fact]
        public void DuplicateAndDelete()
        {
            var lib = new RoutineLibrary(Mine, null);
            Routine saved = lib.Save(New("Profile"), RoutineLocation.Personal);

            Assert.Equal("Profile (copy)", lib.Duplicate(saved, RoutineLocation.Personal).Name);
            Assert.Equal("Profile (copy 2)", lib.Duplicate(saved, RoutineLocation.Personal).Name);
            Assert.Equal(3, lib.LoadAll(out _).Count);

            string deletedTo = lib.Delete(saved);
            Assert.Equal(Path.Combine(Mine, RoutineLibrary.DeletedFolderName, "Profile.md"), deletedTo);
            Assert.Equal(2, lib.LoadAll(out _).Count);

            // Deleting another with the same file name keeps both copies.
            Routine again = lib.Save(New("Profile"), RoutineLocation.Personal);
            string second = lib.Delete(again);
            Assert.NotEqual(deletedTo, second);
            Assert.True(File.Exists(deletedTo) && File.Exists(second));
            Assert.Throws<FileNotFoundException>(() => lib.Delete(again));
        }

        [Fact]
        public void NoticesChangesMadeBySomeoneElse()
        {
            var lib = new RoutineLibrary(Mine, null);
            Routine opened = lib.Save(New("Shared"), RoutineLocation.Personal);

            File.WriteAllText(opened.FilePath!, RoutineFile.Write(New("Shared")).Replace("Do it.", "Their change."));
            File.SetLastWriteTimeUtc(opened.FilePath!, opened.LoadedWriteTimeUtc!.Value.AddMinutes(1));

            Routine mine = opened.Clone();
            mine.Instructions = "My change.";
            Assert.Throws<RoutineConflictException>(() => lib.Save(mine, RoutineLocation.Personal, opened));

            lib.Save(mine, RoutineLocation.Personal, opened, overwriteChanges: true);
            Assert.Equal("My change.", lib.Find("Shared")!.Instructions);
        }

        [Fact]
        public void FileNamesAreSafeAndDontCollide()
        {
            Assert.Equal("a-b", RoutineLibrary.FileStem("a/b"));
            Assert.Equal("_CON", RoutineLibrary.FileStem("CON"));
            Assert.Equal("routine", RoutineLibrary.FileStem("???"));
            Assert.Equal("Kerb returns (6 m)", RoutineLibrary.FileStem("  Kerb   returns (6 m). "));

            var lib = new RoutineLibrary(Mine, null);
            Routine first = lib.Save(New("a/b"), RoutineLocation.Personal);
            Routine second = lib.Save(New("a-b"), RoutineLocation.Personal);
            Assert.NotEqual(first.FilePath, second.FilePath);
            Assert.Equal(2, lib.LoadAll(out _).Count);
        }

        [Fact]
        public void TheSameFolderTwiceIsOnlyListedOnce()
        {
            var lib = new RoutineLibrary(Mine, Mine + Path.DirectorySeparatorChar);
            Assert.False(lib.HasOffice);
            lib.Save(New("Once"), RoutineLocation.Personal);
            Assert.Single(lib.LoadAll(out _));
        }

        [Fact]
        public void BrokenFilesStillLoad()
        {
            Directory.CreateDirectory(Mine);
            File.WriteAllText(Path.Combine(Mine, "Half written.md"), "---\nname: Half written\n");

            Routine r = new RoutineLibrary(Mine, null).LoadAll(out _).Single();
            Assert.Equal("Half written", r.Name);
            Assert.NotNull(r.LoadProblem);
        }

        [Fact]
        public void RemembersTheLastValuesPerRoutine()
        {
            var memory = new RoutineInputMemory(Path.Combine(_root, "inputs.json"));
            var r = new Routine
            {
                Name = "Profile",
                Instructions = "x",
                Inputs =
                {
                    new RoutineInput { Name = "road" },
                    new RoutineInput { Name = "corner", Type = RoutineInputType.Point },
                },
            };

            Assert.Empty(memory.Get(r));
            memory.Remember(r, new Dictionary<string, string?> { ["road"] = "MC01", ["corner"] = "ignored", ["gone"] = "x" });
            Dictionary<string, string> got = memory.Get(r);
            Assert.Equal("MC01", got["ROAD"]);
            Assert.Single(got);

            var office = r.Clone();
            office.Location = RoutineLocation.Office;
            Assert.Empty(memory.Get(office));

            File.WriteAllText(Path.Combine(_root, "inputs.json"), "{ not json");
            Assert.Empty(memory.Get(r));
        }
    }

    public class RoutineToolsTests
    {
        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

        [Fact]
        public void ADraftGoesToTheHostForReview()
        {
            RoutineDraft? draft = null;
            var tools = new RoutineTools(_ => null, d => draft = d);

            var outcome = tools.Execute(RoutineTools.SaveRoutine, Json(
                "{\"name\":\"Kerb returns\",\"description\":\"Fillet kerb returns\",\"effort\":\"thorough\"," +
                "\"inputs\":[{\"name\":\"radius\",\"type\":\"number\",\"default\":6,\"unit\":\"m\",\"required\":true}," +
                "{\"name\":\"tidy\",\"type\":\"yesno\",\"default\":true}," +
                "{\"name\":\"side\",\"type\":\"choice\",\"options\":[\"Left\",\"Right\"]}]," +
                "\"instructions\":\"1. Fillet at {{radius}} on the {{side}}.\"}"));

            Assert.False(outcome.IsError, outcome.Content);
            Assert.NotNull(draft);
            Assert.Null(draft!.Replaces);
            Assert.Equal("Kerb returns", draft.Routine.Name);
            Assert.Equal(EffortMode.Thorough, draft.Routine.Effort);
            Assert.Equal("6", draft.Routine.Inputs[0].Default);
            Assert.Equal("yes", draft.Routine.Inputs[1].Default);
            Assert.Equal(new[] { "Left", "Right" }, draft.Routine.Inputs[2].Options);
        }

        [Fact]
        public void ProblemsGoBackToClaude()
        {
            RoutineDraft? draft = null;
            var tools = new RoutineTools(_ => null, d => draft = d);

            var outcome = tools.Execute(RoutineTools.SaveRoutine, Json(
                "{\"name\":\"X\",\"description\":\"d\",\"instructions\":\"Use {{road}}\"}"));
            Assert.True(outcome.IsError);
            Assert.Contains("{{road}}", outcome.Content);
            Assert.Null(draft);

            Assert.True(tools.Execute(RoutineTools.SaveRoutine, Json(
                "{\"name\":\"X\",\"description\":\"d\",\"instructions\":\"i\",\"inputs\":[{\"name\":\"a\",\"type\":\"colour\"}]}")).IsError);
        }

        [Fact]
        public void AnUpdateFindsTheSavedRoutine()
        {
            var saved = new Routine { Name = "Kerb returns", Instructions = "old", Location = RoutineLocation.Office };
            RoutineDraft? draft = null;
            var tools = new RoutineTools(n => string.Equals(n, "kerb returns", StringComparison.OrdinalIgnoreCase) ? saved : null, d => draft = d);

            tools.Execute(RoutineTools.SaveRoutine, Json(
                "{\"name\":\"Kerb returns\",\"description\":\"d\",\"instructions\":\"new\",\"replaces\":\"kerb returns\"}"));
            Assert.Same(saved, draft!.Replaces);
            Assert.Null(draft.Note);

            var outcome = tools.Execute(RoutineTools.SaveRoutine, Json(
                "{\"name\":\"Other\",\"description\":\"d\",\"instructions\":\"new\",\"replaces\":\"Gone\"}"));
            Assert.Null(draft!.Replaces);
            Assert.Contains("no saved routine called \"Gone\"", draft.Note);
            Assert.Contains("no saved routine", outcome.Content);
        }
    }
}
