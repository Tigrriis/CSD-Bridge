using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bricscad.ApplicationServices;
using CsdBridge.Core.Tools;

namespace CsdBridge.BricsCAD.Csd
{
    /// <summary>
    /// Tools for driving Civil Site Design: find and run its commands, read its dialogs,
    /// set values, press buttons, read volumes, and sample the ground for vertical design.
    ///
    /// Safety: in "read" mode (the default) nothing that changes the design is allowed
    /// (no value edits; buttons and commands that look like they change data are refused).
    /// "edit" mode allows everything. The user switches with the AICSD command.
    /// </summary>
    internal sealed class CsdTools : IToolExtension
    {
        public const string Help = "csd_help";
        public const string ListCommands = "csd_commands";
        public const string RunCommand = "csd_run_command";
        public const string ListDialogs = "csd_list_dialogs";
        public const string ReadDialog = "csd_read_dialog";
        public const string ReadVolumes = "csd_read_volumes";
        public const string SetValues = "csd_set_values";
        public const string Press = "csd_press";
        public const string SampleGround = "sample_ground_profile";
        public const string SampleCsdGround = "sample_csd_ground";
        public const string WriteVcFile = "csd_write_vc_file";
        public const string OpenVge = "csd_open_vge";

        private readonly Document _doc;
        private readonly Func<string> _mode;

        public CsdTools(Document doc, Func<string> mode)
        {
            _doc = doc;
            _mode = mode;
        }

        private bool CanEdit => string.Equals(_mode(), "edit", StringComparison.OrdinalIgnoreCase);

        public IReadOnlyList<ToolDefinition> Tools { get; } = new List<ToolDefinition>
        {
            new ToolDefinition(Help,
                "Civil Site Design (CSD) reference built into this add-in: how to automate it, its dialogs and their controls, the " +
                "Vertical Grading Editor and grading grid, design data, templates, volumes, getting ground levels, and how this office " +
                "wants vertical design done. Call with no topic for the list of topics. Read the relevant topic before working in a CSD dialog.",
                "{\"type\":\"object\",\"properties\":{\"topic\":{\"type\":\"string\"}}}"),

            new ToolDefinition(OpenVge,
                "One step to the vertical design: opens the Vertical Grading Editor (ARDUV7NPROADVGE, which asks the user to pick the " +
                "road) unless one is already open, opens its grading grid, and returns the IP table and the volumes readout. If the " +
                "command is waiting for the user to pick a road, it says so: ask them to pick it, then call this again. 'road' picks " +
                "between several open VGE windows by title.",
                "{\"type\":\"object\",\"properties\":{\"road\":{\"type\":\"string\"}," +
                "\"command\":{\"type\":\"string\",\"description\":\"Default ARDUV7NPROADVGE.\"}," +
                "\"open_grid\":{\"type\":\"boolean\",\"description\":\"Default true.\"}," +
                "\"wait_seconds\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":120}}}"),

            new ToolDefinition(ListCommands,
                "List the commands this Civil Site Design installation defines (read from its .NET assemblies) and the ribbon/menu buttons " +
                "that run them (from its menu file). Use it to find the exact command name before csd_run_command; never guess names. " +
                "'filter' matches the command name, the button label or help text (e.g. \"vertical\", \"design data\", \"volume\").",
                "{\"type\":\"object\",\"properties\":{\"filter\":{\"type\":\"string\"}," +
                "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":400,\"description\":\"Default 120.\"}}}"),

            new ToolDefinition(RunCommand,
                "Run a Civil Site Design command (a name from csd_commands) to open its dialog. Returns when the command finishes or a " +
                "dialog opens (it doesn't wait for you to close it), listing the dialogs that opened. If it reports the command still " +
                "running with no dialog, it is probably prompting at the command line (e.g. select an alignment): ask the user to answer " +
                "it in BricsCAD, then call csd_list_dialogs. While a CSD command or modal dialog runs, the drafting tools can't run. " +
                "method \"direct\" (default) calls the command inside this request; \"command_line\" runs it through BricsCAD's command line " +
                "(try it if direct fails, and for menu-only commands).",
                "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"}," +
                "\"method\":{\"type\":\"string\",\"enum\":[\"direct\",\"command_line\"]}," +
                "\"wait_seconds\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":120,\"description\":\"How long to wait for a dialog. Default 15.\"}" +
                "},\"required\":[\"command\"]}"),

            new ToolDefinition(ListDialogs,
                "List the open Civil Site Design dialogs (id = internal form name, title) and any Windows message boxes or file dialogs. " +
                "Also says whether BricsCAD is busy running a CSD command.",
                "{\"type\":\"object\",\"properties\":{}}"),

            new ToolDefinition(ReadDialog,
                "Read an open dialog: its fields (with labels, values and options), grids (columns and cell values), buttons, menu items, " +
                "tabs and labels. 'dialog' is an id from csd_list_dialogs (e.g. frmVGEGrid) or part of the title. Hidden controls and " +
                "hidden grid columns are left out unless include_hidden. 'grid' reads just that grid. For Windows dialogs it returns " +
                "the message text, edit boxes and buttons.",
                "{\"type\":\"object\",\"properties\":{\"dialog\":{\"type\":\"string\"}," +
                "\"include_hidden\":{\"type\":\"boolean\"},\"grid\":{\"type\":\"string\"}," +
                "\"max_rows\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000,\"description\":\"Grid rows to return. Default 100.\"}" +
                "},\"required\":[\"dialog\"]}"),

            new ToolDefinition(ReadVolumes,
                "Read the earthworks volumes readout of a Vertical Grading Editor window (stripping, total cut, total fill, pavement fill, " +
                "net cut or fill). 'dialog' defaults to the open VGE (frmPopUp); give the title when several roads are open.",
                "{\"type\":\"object\",\"properties\":{\"dialog\":{\"type\":\"string\"}}}"),

            new ToolDefinition(SetValues,
                "Change values in an open dialog the way a user typing would, so CSD recalculates: text boxes, choices (by option text), " +
                "check boxes and options (true/false), numbers, and grid cells (grid, row from 0, column by name, header or index). " +
                "{grid, row, select: true} highlights a row (e.g. before pressing Delete). Changes apply in order; each reports the " +
                "value before and after so you can check it took. commit: \"auto\" (default: tab out of fields, end the edit in grids), " +
                "\"enter\" (press Enter, as a user would), \"tab\" or \"none\". For Windows dialogs, 'control' is edit1, edit2... " +
                "(e.g. a file name). Needs edit mode (the user turns it on with AICSD). CSD changes are not in BricsCAD's UNDO.",
                "{\"type\":\"object\",\"properties\":{\"dialog\":{\"type\":\"string\"}," +
                "\"changes\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":200,\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"control\":{\"type\":\"string\"},\"grid\":{\"type\":\"string\"},\"row\":{\"type\":\"integer\",\"minimum\":0}," +
                "\"column\":{\"type\":[\"string\",\"integer\"]},\"value\":{\"type\":[\"string\",\"number\",\"boolean\"]}," +
                "\"select\":{\"type\":\"boolean\"},\"commit\":{\"type\":\"string\",\"enum\":[\"auto\",\"enter\",\"tab\",\"none\"]}}}}" +
                "},\"required\":[\"dialog\",\"changes\"]}"),

            new ToolDefinition(Press,
                "Press a button (by name or text), a menu item ('menu': a path such as \"Tools > Volume Report\" or an item name) in an " +
                "open dialog, or a button of a Windows message box or file dialog (by its text). Returns when the click is handled or " +
                "a new dialog opens. Hidden buttons (e.g. the VGE's legacy panel behind its ribbon) need allow_hidden: true. In read " +
                "mode, buttons that look like they change the design (OK, Save, Delete, Adjust, Rebuild...) are refused.",
                "{\"type\":\"object\",\"properties\":{\"dialog\":{\"type\":\"string\"},\"control\":{\"type\":\"string\"},\"menu\":{\"type\":\"string\"}," +
                "\"allow_hidden\":{\"type\":\"boolean\"}," +
                "\"wait_seconds\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":120,\"description\":\"Default 15.\"}" +
                "},\"required\":[\"dialog\"]}"),

            new ToolDefinition(SampleCsdGround,
                "Get existing ground levels along a centreline from a Civil Site Design surface (e.g. the natural surface, often named " +
                "NS...). CSD surfaces can't be read directly, so this makes a temporary BricsCAD copy with CSD's Surface Exchange, samples " +
                "it every 'interval' metres, then removes the copy and its exchange item (after: \"remove\", the default) or hides it on a " +
                "frozen layer for reuse (\"hide\", edit mode only). Returns a ground_id for design_vertical_profile / " +
                "evaluate_vertical_profile. If the surface name is wrong, the error lists the CSD surfaces in the drawing. The centreline " +
                "is a polyline, line, arc or alignment (by handle); start_chainage is its chainage at the start (match CSD's).",
                "{\"type\":\"object\",\"properties\":{\"csd_surface\":{\"type\":\"string\",\"description\":\"CSD surface name.\"}," +
                "\"centreline\":{\"type\":\"string\",\"description\":\"Entity handle.\"}," +
                "\"interval\":{\"type\":\"number\",\"description\":\"Metres. Default 5.\"}," +
                "\"start_chainage\":{\"type\":\"number\"},\"from_chainage\":{\"type\":\"number\"},\"to_chainage\":{\"type\":\"number\"}," +
                "\"reverse\":{\"type\":\"boolean\",\"description\":\"The polyline runs the opposite way to the road.\"}," +
                "\"after\":{\"type\":\"string\",\"enum\":[\"remove\",\"hide\",\"keep\"]}," +
                "\"exchange_command\":{\"type\":\"string\",\"description\":\"The command that opens Surface Exchange, if it can't be found automatically.\"}" +
                "},\"required\":[\"csd_surface\",\"centreline\"]}"),

            new ToolDefinition(SampleGround,
                "Sample existing ground levels along a centreline (a polyline, line, arc or alignment, by handle) from a BricsCAD TIN " +
                "surface already in the drawing (for CSD surfaces use sample_csd_ground instead), " +
                "surface, every 'interval' metres. Returns a ground_id for design_vertical_profile / evaluate_vertical_profile, the " +
                "range and a short preview. start_chainage is the chainage at the start of the centreline (match CSD's).",
                "{\"type\":\"object\",\"properties\":{\"centreline\":{\"type\":\"string\",\"description\":\"Entity handle.\"}," +
                "\"surface\":{\"type\":\"string\",\"description\":\"Surface name or handle; needed if there are several.\"}," +
                "\"interval\":{\"type\":\"number\",\"description\":\"Metres. Default 5.\"}," +
                "\"start_chainage\":{\"type\":\"number\"},\"from_chainage\":{\"type\":\"number\"},\"to_chainage\":{\"type\":\"number\"}," +
                "\"reverse\":{\"type\":\"boolean\",\"description\":\"The polyline runs the opposite way to the road.\"}" +
                "},\"required\":[\"centreline\"]}"),

            new ToolDefinition(WriteVcFile,
                "Write a vertical design (IP table) to a text file for the VGE's File > Import VC: one line per IP, " +
                "\"chainage,level,VC length\", with 0.001 for no curve. Returns the file path. An alternative to editing the grading " +
                "grid cell by cell; compare the format with a file exported from the VGE before relying on it.",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"File name, e.g. the road name.\"}," +
                "\"ips\":{\"type\":\"array\",\"minItems\":2,\"maxItems\":500,\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"chainage\":{\"type\":\"number\"},\"level\":{\"type\":\"number\"},\"vc_length\":{\"type\":\"number\"}},\"required\":[\"chainage\",\"level\"]}}" +
                "},\"required\":[\"ips\"]}"),
        };

        public string? PromptSection =>
            "## Civil Site Design\n" +
            "Civil Site Design (CSD) is loaded in BricsCAD for road and grading design. You can drive it with the csd_ tools: find " +
            "commands with csd_commands, open dialogs with csd_run_command, then csd_list_dialogs, csd_read_dialog, csd_set_values, " +
            "csd_press and csd_read_volumes. Before working in a CSD dialog, read the matching csd_help topic (start with \"overview\"; " +
            "\"vertical_method\" for vertical design). Everyday commands in this install: ARDUV7NPROADVGE (Vertical Grading Editor; " +
            "csd_open_vge wraps it), ARDUV7NPCREATEEDITROAD (create/edit road), ARDUV7ALIGNMENTCREATE (alignment from a polyline), " +
            "ARDUV7SELECTROADANDEDITDESIGN (design data), ARDUV7EDITTEMPLATE (templates), ARDUV7EDITDEFAULTS (drawing settings). " +
            "Surfaces: NS = existing ground, BM = design. Rules:\n" +
            "- Read before you change: read the dialog first, keep the original values, and read it back after changing it.\n" +
            "- CSD changes are not in BricsCAD's UNDO. Before a change the user didn't ask for in so many words, say what you'll change and wait for their go-ahead. " +
            "Don't press OK/Save/Delete or close dialogs unless the user asked.\n" +
            "- If a tool says edits are off (read mode), tell the user they can turn them on with the AICSD command; don't work around it.\n" +
            "- If CSD shows a message box, read it and tell the user what it says before answering it.";

        // Everything runs on the agent's background thread: the tools marshal to BricsCAD's main
        // thread or the dialogs' UI thread themselves, which also works while a CSD dialog is open.
        public bool RunsOnMainThread(string toolName) => false;

        public string? DescribeCall(string toolName, JsonElement input)
        {
            string? Arg(string n) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(n, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            switch (toolName)
            {
                case Help: return "reading the CSD notes" + (Arg("topic") is string t ? $" ({t})" : "");
                case ListCommands: return "listing CSD commands";
                case RunCommand: return $"running {Arg("command")}";
                case ListDialogs: return "checking open CSD dialogs";
                case ReadDialog: return $"reading {Arg("dialog")}";
                case ReadVolumes: return "reading volumes";
                case SetValues: return $"changing values in {Arg("dialog")}";
                case Press: return $"pressing {Arg("control") ?? Arg("menu")} in {Arg("dialog")}";
                case SampleGround: return "sampling the ground along the centreline";
                case SampleCsdGround: return $"getting ground levels from {Arg("csd_surface")}";
                case WriteVcFile: return "writing a VC file";
                case OpenVge: return "opening the vertical design";
                default: return null;
            }
        }

        public ToolOutcome Execute(string toolName, JsonElement input)
        {
            try
            {
                if (input.ValueKind != JsonValueKind.Object)
                {
                    throw new ToolInputException("input must be an object");
                }

                switch (toolName)
                {
                    case Help: return DoHelp(input);
                    case ListCommands: return DoListCommands(input);
                    case RunCommand: return DoRunCommand(input);
                    case ListDialogs: return DoListDialogs();
                    case ReadDialog: return DoReadDialog(input);
                    case ReadVolumes: return DoReadVolumes(input);
                    case SetValues: return DoSetValues(input);
                    case Press: return DoPress(input);
                    case SampleGround: return DoSampleGround(input);
                    case SampleCsdGround: return DoSampleCsdGround(input);
                    case WriteVcFile: return DoWriteVcFile(input);
                    case OpenVge: return DoOpenVge(input);
                    default: return Fail($"Unknown tool '{toolName}'.");
                }
            }
            catch (ToolInputException ex)
            {
                return Fail("Invalid input - nothing was changed. " + ex.Message);
            }
            catch (CsdException ex)
            {
                return Fail(ex.Message);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                return Fail($"{toolName} failed: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
            catch (Exception ex)
            {
                return Fail($"{toolName} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ tools

        private static ToolOutcome DoHelp(JsonElement input)
        {
            string? topic = JsonArgs.OptionalString(input, "topic", "");
            Dictionary<string, (string Title, string Body)> topics = Knowledge.Topics;
            if (string.IsNullOrWhiteSpace(topic))
            {
                var sb = new StringBuilder("Topics (call csd_help with one):\n");
                foreach (var kv in topics)
                {
                    sb.Append("- ").Append(kv.Key).Append(": ").Append(kv.Value.Title).Append('\n');
                }

                return Ok(sb.ToString(), "listed CSD topics");
            }

            string key = topic!.Trim().ToLowerInvariant().Replace(' ', '_');
            if (!topics.TryGetValue(key, out var section))
            {
                var match = topics.FirstOrDefault(kv => kv.Key.Contains(key) || kv.Value.Title.IndexOf(topic, StringComparison.OrdinalIgnoreCase) >= 0);
                if (match.Key == null)
                {
                    return Fail($"No topic '{topic}'. Topics: {string.Join(", ", topics.Keys)}.");
                }

                key = match.Key;
                section = match.Value;
            }

            return Ok($"# {section.Title}\n{section.Body}", $"read CSD notes: {key}");
        }

        private static ToolOutcome DoListCommands(JsonElement input)
        {
            string? filter = JsonArgs.OptionalString(input, "filter", "");
            int limit = JsonArgs.OptionalInt(input, "limit", "", 1, 400) ?? 120;
            Dictionary<string, CsdCommand> all = CsdCommands.All;
            if (all.Count == 0)
            {
                return Fail("No Civil Site Design commands were found: CSD doesn't seem to be loaded in this BricsCAD session. Run any CSD command once (or check CSD is installed), then try again.");
            }

            IEnumerable<CsdCommand> list = all.Values;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                string[] words = filter!.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                list = list.Where(c => words.All(w => Haystack(c).IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            List<CsdCommand> matched = list.OrderBy(c => c.Buttons.Count == 0 ? 1 : 0).ThenBy(c => c.Name).ToList();
            var payload = new
            {
                sources = CsdCommands.Sources,
                total = all.Count,
                matched = matched.Count,
                commands = matched.Take(limit).Select(c => new
                {
                    name = c.Name,
                    buttons = c.Buttons.Count > 0 ? c.Buttons : null,
                    help = c.Help,
                    method = c.Method == null ? null : $"{c.Method.DeclaringType?.Name}.{c.Method.Name}",
                    menu_only = c.Method == null ? true : (bool?)null,
                    flags = c.Flags,
                }).ToList(),
                note = matched.Count > limit ? $"{matched.Count - limit} more; narrow the filter." : null,
            };
            return Json(payload, $"{matched.Count} CSD command(s)");
        }

        private ToolOutcome DoRunCommand(JsonElement input)
        {
            string name = JsonArgs.RequireString(input, "command", "").Trim().TrimStart('_', '.').ToUpperInvariant();
            string method = JsonArgs.OptionalString(input, "method", "") ?? "direct";
            int wait = JsonArgs.OptionalInt(input, "wait_seconds", "", 1, 120) ?? 15;
            if (method != "direct" && method != "command_line")
            {
                throw new ToolInputException("method must be direct or command_line");
            }

            if (!CsdCommands.All.TryGetValue(name, out CsdCommand? cmd))
            {
                return Fail($"'{name}' is not a Civil Site Design command known to this installation. Find the name with csd_commands.");
            }

            if (CsdSafety.IsInternalCommand(cmd.Name))
            {
                return Fail($"{cmd.Name} looks like one of CSD's internal test or debugging commands, so CSD-Bridge won't run it.");
            }

            if (!CanEdit)
            {
                string? word = CsdSafety.ChangingCommandWord(cmd.Name, cmd.Buttons);
                if (word != null)
                {
                    return Fail($"{cmd.Name} ({string.Join(" / ", cmd.Buttons)}) looks like it changes the design or drawing (\"{word}\"), and CSD edits are off (read mode). Ask the user to run it, or to turn edits on with AICSD.");
                }
            }

            if (method == "direct" && cmd.Method == null)
            {
                method = "command_line";
            }

            Action work = CsdCommands.Prepare(cmd, method);
            LaunchResult r = CsdWindows.Launch($"CSD command {cmd.Name}", CsdWindows.MainPoster(), work, wait * 1000);
            var dialogs = CsdWindows.List();
            string status = r.Error != null ? "failed" : r.Opened.Count > 0 ? "dialog_open" : r.Finished ? "finished" : "running";
            var payload = new
            {
                command = cmd.Name,
                method,
                status,
                error = r.Error == null ? null : $"{r.Error.GetType().Name}: {r.Error.Message}",
                dialogs_opened = r.Opened.Select(DialogJson).ToList(),
                open_dialogs = dialogs.Select(DialogJson).ToList(),
                note = status == "running"
                    ? "The command is still running without a new dialog. It is probably waiting for input at BricsCAD's command line (e.g. select an alignment or road): ask the user to answer it, then call csd_list_dialogs."
                    : status == "failed" && method == "direct" ? "Try method \"command_line\"." : null,
            };

            return new ToolOutcome(JsonSerializer.Serialize(payload, ToolDispatcher.JsonOptions), r.Error != null)
            {
                Summary = $"{cmd.Name}: {status.Replace('_', ' ')}" + (r.Opened.Count > 0 ? $" ({string.Join(", ", r.Opened.Select(o => o.Title))})" : ""),
            };
        }

        private static ToolOutcome DoListDialogs()
        {
            List<OpenDialog> dialogs = CsdWindows.List();
            var payload = new
            {
                dialogs = dialogs.Select(DialogJson).ToList(),
                busy = CsdWindows.Busy,
                note = dialogs.Count == 0 ? "No CSD dialogs are open." : null,
            };
            return Json(payload, $"{dialogs.Count} dialog(s) open");
        }

        private static ToolOutcome DoReadDialog(JsonElement input)
        {
            OpenDialog d = CsdWindows.Find(JsonArgs.RequireString(input, "dialog", ""));
            bool hidden = JsonArgs.OptionalBool(input, "include_hidden", "", false);
            string? grid = JsonArgs.OptionalString(input, "grid", "");
            int maxRows = JsonArgs.OptionalInt(input, "max_rows", "", 1, 1000) ?? 100;
            object reading = CsdDialogs.Read(d, hidden, grid, maxRows);
            return Json(reading, $"read {d.Title}");
        }

        private static ToolOutcome DoReadVolumes(JsonElement input)
        {
            string? dialog = JsonArgs.OptionalString(input, "dialog", "");
            OpenDialog d;
            if (string.IsNullOrWhiteSpace(dialog))
            {
                List<OpenDialog> vges = CsdWindows.List().Where(o => o.Name == "frmPopUp").ToList();
                if (vges.Count == 0)
                {
                    return Fail("No Vertical Grading Editor is open. Open the VGE for the road first.");
                }

                if (vges.Count > 1)
                {
                    return Fail($"Several VGE windows are open ({string.Join(", ", vges.Select(v => v.Title))}); say which with 'dialog'.");
                }

                d = vges[0];
            }
            else
            {
                d = CsdWindows.Find(dialog!);
            }

            return Json(CsdDialogs.ReadVolumes(d), $"read volumes ({d.Title})");
        }

        private ToolOutcome DoSetValues(JsonElement input)
        {
            OpenDialog d = CsdWindows.Find(JsonArgs.RequireString(input, "dialog", ""));
            List<JsonElement> items = JsonArgs.RequireArray(input, "changes", "", 1, 200);
            var changes = new List<CsdDialogs.Change>();
            for (int i = 0; i < items.Count; i++)
            {
                string path = JsonArgs.Index("changes", i);
                JsonElement e = items[i];
                JsonArgs.RequireObject(e, path);
                var c = new CsdDialogs.Change
                {
                    Control = JsonArgs.OptionalString(e, "control", path),
                    Grid = JsonArgs.OptionalString(e, "grid", path),
                    Row = JsonArgs.OptionalInt(e, "row", path, 0, 100000),
                    Column = ScalarText(e, "column"),
                    Value = ScalarText(e, "value"),
                    Select = JsonArgs.OptionalBool(e, "select", path, false),
                    Commit = JsonArgs.OptionalString(e, "commit", path) ?? "auto",
                };

                if ((c.Control == null) == (c.Grid == null))
                {
                    throw new ToolInputException($"{path} needs either control or grid");
                }

                if (c.Grid != null && c.Row == null)
                {
                    throw new ToolInputException($"{path}.row is required for a grid change");
                }

                if (!c.Select && c.Value == null)
                {
                    throw new ToolInputException($"{path}.value is required");
                }

                if (!new[] { "auto", "enter", "tab", "none" }.Contains(c.Commit))
                {
                    throw new ToolInputException($"{path}.commit must be auto, enter, tab or none");
                }

                changes.Add(c);
            }

            bool onlySelections = changes.All(c => c.Select);
            if (!CanEdit && !onlySelections)
            {
                return Fail("CSD edits are off (read mode), so nothing was changed. Ask the user to turn edits on with the AICSD command (choose Edit), then try again.");
            }

            var results = new List<object>();
            int failed = 0;
            for (int i = 0; i < changes.Count; i++)
            {
                object r = CsdDialogs.Apply(d, changes[i], i);
                results.Add(r);
                bool ok = (bool)(r.GetType().GetProperty("ok")?.GetValue(r) ?? false);
                if (!ok)
                {
                    failed++;

                    // Stop after a failure that opened a dialog or hung: later changes would land in the wrong place.
                    if (r.GetType().GetProperty("dialogs_opened") != null)
                    {
                        results.Add(new { stopped = $"Stopped after change {i}; {changes.Count - i - 1} change(s) not applied." });
                        break;
                    }
                }
            }

            var payload = new { dialog = d.Title, applied = results.Count - failed, failed, results };
            return new ToolOutcome(JsonSerializer.Serialize(payload, ToolDispatcher.JsonOptions), failed == changes.Count)
            {
                Summary = $"{changes.Count - failed} change(s) in {d.Title}" + (failed > 0 ? $", {failed} failed" : ""),
            };
        }

        private ToolOutcome DoPress(JsonElement input)
        {
            OpenDialog d = CsdWindows.Find(JsonArgs.RequireString(input, "dialog", ""));
            string? control = JsonArgs.OptionalString(input, "control", "");
            string? menu = JsonArgs.OptionalString(input, "menu", "");
            bool allowHidden = JsonArgs.OptionalBool(input, "allow_hidden", "", false);
            int wait = JsonArgs.OptionalInt(input, "wait_seconds", "", 1, 120) ?? 15;
            if ((control == null) == (menu == null))
            {
                throw new ToolInputException("give either control or menu");
            }

            Action press;
            string description;
            Action<Action> post;

            if (d.Form == null)
            {
                if (menu != null)
                {
                    throw new ToolInputException("Windows dialogs have no menus; press a button by its text");
                }

                CsdWindows.Win32Control button = CsdDialogs.FindWin32Button(d, control!);
                description = CsdDialogs.CleanText(button.Text);
                bool onlyButton = CsdDialogs.Win32ButtonCount(d) <= 1;
                if (!CanEdit && !onlyButton && CsdSafety.IsChanging(description))
                {
                    return Fail($"\"{description}\" in '{d.Title}' would confirm an action, and CSD edits are off (read mode). Tell the user what the message says and let them answer it, or ask them to turn edits on with AICSD.");
                }

                press = () => CsdWindows.ClickButton(button.Handle);
                post = a => a(); // posting BM_CLICK never blocks
            }
            else
            {
                System.Windows.Forms.Form form = d.Form;
                (Action Press, string Description) prepared = CsdWindows.OnUiThread(form, () =>
                {
                    Action a = CsdDialogs.PrepareWinFormsPress(form, control, menu, allowHidden, out string desc);
                    return (a, desc);
                });
                press = prepared.Press;
                description = prepared.Description;

                string checkText = menu ?? control!;
                if (!CanEdit && (CsdSafety.IsChanging(description) || CsdSafety.IsChanging(checkText)))
                {
                    return Fail($"'{checkText}' looks like it changes the design, and CSD edits are off (read mode). Ask the user to press it, or to turn edits on with AICSD.");
                }

                post = a => form.BeginInvoke(a);
            }

            LaunchResult r = CsdWindows.Launch($"pressing {description} in {d.Title}", post, press, wait * 1000);
            System.Threading.Thread.Sleep(300);
            bool stillOpen = CsdWindows.List().Any(o => o.Handle == d.Handle);
            string status = r.Error != null ? "failed" : r.Opened.Count > 0 ? "dialog_open" : r.Finished ? "done" : "running";
            var payload = new
            {
                pressed = description,
                status,
                error = r.Error?.Message,
                dialog_closed = stillOpen ? (bool?)null : true,
                dialogs_opened = r.Opened.Select(DialogJson).ToList(),
                note = status == "running" ? "CSD is still handling the click (it may be waiting for input at the command line or recalculating). Check csd_list_dialogs." : null,
            };

            return new ToolOutcome(JsonSerializer.Serialize(payload, ToolDispatcher.JsonOptions), r.Error != null)
            {
                Summary = $"pressed {description}: {status.Replace('_', ' ')}",
            };
        }

        private ToolOutcome DoOpenVge(JsonElement input)
        {
            string? road = JsonArgs.OptionalString(input, "road", "");
            string commandName = (JsonArgs.OptionalString(input, "command", "") ?? "ARDUV7NPROADVGE").Trim().ToUpperInvariant();
            bool openGrid = JsonArgs.OptionalBool(input, "open_grid", "", true);
            int wait = JsonArgs.OptionalInt(input, "wait_seconds", "", 1, 120) ?? 30;
            var steps = new List<string>();

            OpenDialog? FindVge() => CsdWindows.List()
                .Where(o => o.Name == "frmPopUp" && (road == null || o.Title.IndexOf(road, StringComparison.OrdinalIgnoreCase) >= 0))
                .FirstOrDefault();

            OpenDialog? vge = FindVge();
            if (vge == null)
            {
                if (!CsdCommands.All.TryGetValue(commandName, out CsdCommand? cmd))
                {
                    return Fail($"{commandName} isn't a CSD command in this installation (see csd_commands).");
                }

                LaunchResult r = CsdWindows.Launch($"CSD command {cmd.Name}", CsdWindows.MainPoster(), CsdCommands.Prepare(cmd, cmd.Method != null ? "direct" : "command_line"), wait * 1000);
                if (r.Error != null)
                {
                    return Fail($"{cmd.Name} failed: {r.Error.Message}");
                }

                vge = FindVge();
                if (vge == null)
                {
                    var waiting = new
                    {
                        status = r.Finished ? "finished_without_vge" : "waiting_for_user",
                        note = r.Finished
                            ? $"{cmd.Name} finished without opening a Vertical Grading Editor{(road != null ? $" for '{road}'" : "")}."
                            : $"{cmd.Name} is waiting at BricsCAD's command line, probably for the user to pick the road. Ask them to pick it, then call csd_open_vge again.",
                        dialogs = CsdWindows.List().Select(DialogJson).ToList(),
                    };
                    return Json(waiting, waiting.status.Replace('_', ' '));
                }

                steps.Add($"opened the VGE with {cmd.Name}");
            }

            OpenDialog? grid = CsdWindows.List().FirstOrDefault(o => o.Name == "frmVGEGrid");
            if (grid == null && openGrid)
            {
                System.Windows.Forms.Form form = vge.Form!;
                LaunchResult g = CsdWindows.Launch(
                    "opening the grading grid",
                    a => form.BeginInvoke(a),
                    () => CsdDialogs.PrepareWinFormsPress(form, "cmdVGEByGrid", null, allowHidden: true, out _)(),
                    15000);
                grid = g.Opened.FirstOrDefault(o => o.Name == "frmVGEGrid") ?? CsdWindows.List().FirstOrDefault(o => o.Name == "frmVGEGrid");
                steps.Add(grid != null ? "opened the grading grid" : "couldn't open the grading grid" + (g.Error != null ? ": " + g.Error.Message : ""));
            }

            object? ips = grid != null ? CsdDialogs.Read(grid, includeHidden: false, onlyGrid: "flexVC", maxRows: 300) : null;
            object? volumes = null;
            try
            {
                volumes = CsdDialogs.ReadVolumes(vge);
            }
            catch (CsdException ex)
            {
                volumes = ex.Message;
            }

            var payload = new { vge = new { id = vge.Id, title = vge.Title }, steps = steps.Count > 0 ? steps : null, grading_grid = ips, volumes };
            return Json(payload, $"vertical design for {vge.Title}");
        }

        private ToolOutcome DoSampleGround(JsonElement input)
        {
            var req = new GroundSampler.Request
            {
                Centreline = JsonArgs.RequireString(input, "centreline", ""),
                Surface = JsonArgs.OptionalString(input, "surface", ""),
                Interval = JsonArgs.OptionalPositive(input, "interval", "") ?? 5,
                StartChainage = JsonArgs.OptionalNumber(input, "start_chainage", "") ?? 0,
                From = JsonArgs.OptionalNumber(input, "from_chainage", ""),
                To = JsonArgs.OptionalNumber(input, "to_chainage", ""),
                Reverse = JsonArgs.OptionalBool(input, "reverse", "", false),
            };

            if (req.Interval < 0.1)
            {
                throw new ToolInputException("interval must be at least 0.1 m");
            }

            object r = CsdWindows.OnMainThread(() => GroundSampler.Sample(_doc, req));
            return Json(r, "sampled the ground along the centreline");
        }

        private ToolOutcome DoSampleCsdGround(JsonElement input)
        {
            string csdSurface = JsonArgs.RequireString(input, "csd_surface", "");
            string after = JsonArgs.OptionalString(input, "after", "") ?? "remove";
            if (after != "remove" && after != "hide" && after != "keep")
            {
                throw new ToolInputException("after must be remove, hide or keep");
            }

            if (after != "remove" && !CanEdit)
            {
                return Fail($"Leaving a BricsCAD copy of the surface in the drawing (after: \"{after}\") needs CSD edits on (AICSD > Edit). With after: \"remove\" nothing is left behind.");
            }

            var req = new GroundSampler.Request
            {
                Centreline = JsonArgs.RequireString(input, "centreline", ""),
                Interval = JsonArgs.OptionalPositive(input, "interval", "") ?? 5,
                StartChainage = JsonArgs.OptionalNumber(input, "start_chainage", "") ?? 0,
                From = JsonArgs.OptionalNumber(input, "from_chainage", ""),
                To = JsonArgs.OptionalNumber(input, "to_chainage", ""),
                Reverse = JsonArgs.OptionalBool(input, "reverse", "", false),
            };

            if (req.Interval < 0.1)
            {
                throw new ToolInputException("interval must be at least 0.1 m");
            }

            object r = SurfaceExchange.Sample(_doc, csdSurface, req, after, JsonArgs.OptionalString(input, "exchange_command", ""));
            return Json(r, $"ground levels from {csdSurface}");
        }

        private static ToolOutcome DoWriteVcFile(JsonElement input)
        {
            List<JsonElement> rows = JsonArgs.RequireArray(input, "ips", "", 2, 500);
            string name = JsonArgs.OptionalString(input, "name", "") ?? "vertical-design";
            name = Regex.Replace(name, @"[^A-Za-z0-9 _\-\.]", "_").Trim();
            if (name.Length == 0)
            {
                name = "vertical-design";
            }

            var sb = new StringBuilder();
            double last = double.NegativeInfinity;
            for (int i = 0; i < rows.Count; i++)
            {
                string path = JsonArgs.Index("ips", i);
                double ch = JsonArgs.RequireNumber(rows[i], "chainage", path);
                double level = JsonArgs.RequireNumber(rows[i], "level", path);
                double vc = JsonArgs.OptionalNumber(rows[i], "vc_length", path) ?? 0;
                if (ch <= last)
                {
                    throw new ToolInputException($"{path}.chainage must be after the previous IP");
                }

                last = ch;
                sb.Append(ch.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                  .Append(level.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                  .Append((vc <= 0.001 ? 0.001 : vc).ToString("0.000", CultureInfo.InvariantCulture)).Append("\r\n");
            }

            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsdBridge", "vc");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? name : name + ".txt"));
            File.WriteAllText(file, sb.ToString());
            return Json(new
            {
                file,
                lines = rows.Count,
                note = "Import it in the VGE with File > Import VC (From File). The format follows CSD's documentation; if the import looks wrong, export the current design from the VGE and compare.",
            }, $"wrote {rows.Count} IPs to {Path.GetFileName(file)}");
        }

        // ------------------------------------------------------------------ helpers

        private static object DialogJson(OpenDialog o) => new
        {
            id = o.Id,
            title = o.Title,
            kind = o.Kind,
            modal = o.Form != null && SafeModal(o.Form) ? true : (bool?)null,
        };

        private static bool SafeModal(System.Windows.Forms.Form f)
        {
            try
            {
                return f.Modal;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Haystack(CsdCommand c) =>
            c.Name + " " + string.Join(" ", c.Buttons) + " " + (c.Help ?? "") + " " + (c.Method?.Name ?? "");

        private static string? ScalarText(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out JsonElement v))
            {
                return null;
            }

            switch (v.ValueKind)
            {
                case JsonValueKind.String: return v.GetString();
                case JsonValueKind.Number: return v.GetRawText();
                case JsonValueKind.True: return "true";
                case JsonValueKind.False: return "false";
                case JsonValueKind.Null: return null;
                default: throw new ToolInputException($"{name} must be a string, number or true/false");
            }
        }

        private static ToolOutcome Json(object payload, string summary) =>
            new ToolOutcome(JsonSerializer.Serialize(payload, ToolDispatcher.JsonOptions), false) { Summary = summary };

        private static ToolOutcome Ok(string text, string summary) => new ToolOutcome(text, false) { Summary = summary };

        private static ToolOutcome Fail(string message) => new ToolOutcome(message, true) { Summary = message.Length > 90 ? message.Substring(0, 90) + "..." : message };
    }

    /// <summary>The CSD notes (Resources/csd-knowledge.md), split into topics by "## key: Title" headings.</summary>
    internal static class Knowledge
    {
        private static Dictionary<string, (string Title, string Body)>? _topics;

        public static Dictionary<string, (string Title, string Body)> Topics => _topics ??= Load();

        private static Dictionary<string, (string Title, string Body)> Load()
        {
            var topics = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            using (Stream? s = typeof(Knowledge).Assembly.GetManifestResourceStream("CsdBridge.BricsCAD.Resources.csd-knowledge.md"))
            {
                if (s == null)
                {
                    return topics;
                }

                string text = new StreamReader(s).ReadToEnd().Replace("\r\n", "\n");
                foreach (Match m in Regex.Matches(text, @"^## ([a-z_]+): (.+)\n([\s\S]*?)(?=^## |\z)", RegexOptions.Multiline))
                {
                    topics[m.Groups[1].Value] = (m.Groups[2].Value.Trim(), m.Groups[3].Value.Trim());
                }
            }

            return topics;
        }
    }
}
