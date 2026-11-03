using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// AIUIDUMP: writes the Windows UI Automation tree of BricsCAD's open windows
    /// (dialogs in full, the main window briefly) to a text file. It's a read-only
    /// diagnostic to judge whether dialogs such as Civil Site Design's can be read and
    /// driven by name (labels, values, buttons, keyboard shortcuts) without the mouse.
    ///
    /// Runs on a background thread after a delay: while a modal dialog is open, the
    /// command line is blocked, and UI Automation must not run on the UI thread of the
    /// windows it inspects (it would deadlock waiting for that thread to answer).
    /// </summary>
    internal static class UiDump
    {
        private const int MaxDepth = 30;
        private const int MainWindowDepth = 2;
        private const int MaxNodes = 8000;
        private const int MaxText = 200;
        private const int GridSampleRows = 8;
        private const int GridSampleColumns = 14;

        public static string Folder { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsdBridge", "uidumps");

        public static void CaptureAfter(int seconds)
        {
            var thread = new Thread(() =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(seconds));
                try
                {
                    string path = Capture();
                    System.Media.SystemSounds.Asterisk.Play();
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                }
                catch (Exception ex)
                {
                    try
                    {
                        Directory.CreateDirectory(Folder);
                        File.WriteAllText(Path.Combine(Folder, "uidump-error.txt"), ex.ToString());
                    }
                    catch (Exception)
                    {
                        // Nothing more we can do from a background thread.
                    }

                    System.Media.SystemSounds.Hand.Play();
                }
            })
            {
                IsBackground = true,
                Name = "CSD-Bridge UI dump",
            };
            thread.SetApartmentState(ApartmentState.MTA); // UI Automation clients work best in MTA
            thread.Start();
        }

        /// <summary>Capture now (call from a background thread). Returns the file path.</summary>
        public static string Capture()
        {
            var stats = new Stats();
            var body = new StringBuilder();
            Process me = Process.GetCurrentProcess();
            IntPtr mainHandle = me.MainWindowHandle;

            var windows = AutomationElement.RootElement.FindAll(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, me.Id));

            body.AppendLine($"== Windows of {me.ProcessName} (process {me.Id}): {windows.Count}");
            foreach (AutomationElement window in windows)
            {
                bool isMain = SafeHandle(window) == mainHandle;
                body.AppendLine();
                body.AppendLine(isMain
                    ? $"-- Main window (first {MainWindowDepth} levels only)"
                    : "-- Window");
                Walk(window, 0, isMain ? MainWindowDepth : MaxDepth, body, stats);
            }

            // A dialog from another program, if one is in front (e.g. a helper .exe).
            IntPtr foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero)
            {
                AutomationElement? front = null;
                try
                {
                    front = AutomationElement.FromHandle(foreground);
                }
                catch (Exception)
                {
                    // Window closed meanwhile.
                }

                if (front != null && SafePid(front) != me.Id)
                {
                    body.AppendLine();
                    body.AppendLine($"-- Foreground window of another program (process {SafePid(front)})");
                    Walk(front, 0, MaxDepth, body, stats);
                }
            }

            var text = new StringBuilder();
            text.AppendLine("CSD-Bridge UI dump - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            text.AppendLine($"BricsCAD: {SafeFileVersion(me)}   CSD-Bridge: {typeof(UiDump).Assembly.GetName().Version}   .NET: {Environment.Version}");
            text.AppendLine("This lists the controls in BricsCAD's open windows, including what is typed in them.");
            text.AppendLine("Check it contains nothing confidential before sending it on. Password fields are masked.");
            text.AppendLine();
            text.Append(Assessment(stats));
            text.AppendLine();
            text.AppendLine("Legend: Type \"Name\" [id=AutomationId] [class=ClassName] [fw=framework] key=keyboard shortcut ...");
            text.AppendLine("        {patterns: what can be done to the control without the mouse}");
            text.AppendLine();
            text.Append(body);

            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, "uidump-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
            File.WriteAllText(path, text.ToString(), Encoding.UTF8);
            return path;
        }

        // ------------------------------------------------------------------ walking

        private static void Walk(AutomationElement element, int depth, int maxDepth, StringBuilder sb, Stats stats)
        {
            if (stats.Nodes >= MaxNodes)
            {
                if (!stats.Truncated)
                {
                    stats.Truncated = true;
                    sb.AppendLine($"... stopped after {MaxNodes} controls");
                }

                return;
            }

            stats.Nodes++;
            sb.Append(' ', depth * 2).AppendLine(Describe(element, stats));

            // Grids: show a sample of cell contents instead of hundreds of cell elements.
            if (SupportsPattern(element, GridPattern.Pattern))
            {
                SampleGrid(element, depth + 1, sb);
                return;
            }

            AutomationElement? child;
            try
            {
                child = TreeWalker.ControlViewWalker.GetFirstChild(element);
            }
            catch (Exception)
            {
                return;
            }

            if (child != null && depth >= maxDepth)
            {
                sb.Append(' ', (depth + 1) * 2).AppendLine("... (not expanded)");
                return;
            }

            while (child != null)
            {
                // Dialogs (e.g. Civil Site Design's) sit under the main window in the UI
                // Automation tree; always expand them fully.
                int childMax = IsDialog(child) ? depth + 1 + MaxDepth : maxDepth;
                Walk(child, depth + 1, childMax, sb, stats);
                try
                {
                    child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                }
                catch (Exception)
                {
                    break;
                }
            }
        }

        private static string Describe(AutomationElement element, Stats stats)
        {
            AutomationElement.AutomationElementInformation c;
            try
            {
                c = element.Current;
            }
            catch (Exception ex)
            {
                return "<unavailable: " + ex.GetType().Name + ">";
            }

            string type = (c.ControlType?.ProgrammaticName ?? "Unknown").Replace("ControlType.", "");
            stats.Count(stats.Types, type);
            bool named = !string.IsNullOrWhiteSpace(c.Name);
            if (named)
            {
                stats.Named++;
            }
            else if (type == "Custom" || type == "Pane")
            {
                stats.UnnamedCustom++;
            }

            var sb = new StringBuilder(type);
            if (named)
            {
                sb.Append(" \"").Append(Short(c.Name)).Append('"');
            }

            Append(sb, "id", c.AutomationId);
            Append(sb, "class", c.ClassName);
            Append(sb, "fw", c.FrameworkId);
            Append(sb, "key", c.AccessKey);
            Append(sb, "accel", c.AcceleratorKey);
            if (!c.IsEnabled)
            {
                sb.Append(" disabled");
            }

            if (c.IsOffscreen)
            {
                sb.Append(" offscreen");
            }

            if (c.HasKeyboardFocus)
            {
                sb.Append(" FOCUSED");
            }

            string patterns = Patterns(element, c, stats);
            if (patterns.Length > 0)
            {
                sb.Append("  {").Append(patterns).Append('}');
            }

            return sb.ToString();
        }

        private static string Patterns(AutomationElement element, AutomationElement.AutomationElementInformation c, Stats stats)
        {
            var parts = new List<string>();
            AutomationPattern[] supported;
            try
            {
                supported = element.GetSupportedPatterns();
            }
            catch (Exception)
            {
                return "";
            }

            foreach (AutomationPattern p in supported)
            {
                string name = p.ProgrammaticName.Replace("PatternIdentifiers.Pattern", "");
                stats.Count(stats.Patterns, name);
                try
                {
                    if (p == ValuePattern.Pattern)
                    {
                        var v = (ValuePattern)element.GetCurrentPattern(p);
                        string value = c.IsPassword ? "****" : Short(v.Current.Value);
                        parts.Add($"Value=\"{value}\"{(v.Current.IsReadOnly ? " read-only" : "")}");
                    }
                    else if (p == TogglePattern.Pattern)
                    {
                        parts.Add("Toggle=" + ((TogglePattern)element.GetCurrentPattern(p)).Current.ToggleState);
                    }
                    else if (p == SelectionItemPattern.Pattern)
                    {
                        parts.Add(((SelectionItemPattern)element.GetCurrentPattern(p)).Current.IsSelected ? "Selected" : "Selectable");
                    }
                    else if (p == ExpandCollapsePattern.Pattern)
                    {
                        parts.Add("ExpandCollapse=" + ((ExpandCollapsePattern)element.GetCurrentPattern(p)).Current.ExpandCollapseState);
                    }
                    else if (p == RangeValuePattern.Pattern)
                    {
                        var r = ((RangeValuePattern)element.GetCurrentPattern(p)).Current;
                        parts.Add($"Range={r.Value} [{r.Minimum}..{r.Maximum}]");
                    }
                    else if (p == GridPattern.Pattern)
                    {
                        var g = ((GridPattern)element.GetCurrentPattern(p)).Current;
                        parts.Add($"Grid {g.RowCount}x{g.ColumnCount}");
                    }
                    else if (p == SelectionPattern.Pattern)
                    {
                        var selected = ((SelectionPattern)element.GetCurrentPattern(p)).Current.GetSelection();
                        string names = string.Join(", ", selected.Take(5).Select(s => Short(SafeName(s))));
                        parts.Add($"Selection=[{names}{(selected.Length > 5 ? ", ..." : "")}]");
                    }
                    else
                    {
                        parts.Add(name);
                    }
                }
                catch (Exception)
                {
                    parts.Add(name + "(?)");
                }
            }

            return string.Join("; ", parts);
        }

        private static bool IsDialog(AutomationElement e)
        {
            try
            {
                var c = e.Current;
                return c.ControlType == ControlType.Window
                    && (c.FrameworkId == "WinForm" || c.FrameworkId == "WPF" || c.ClassName == "#32770");
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool SupportsPattern(AutomationElement e, AutomationPattern pattern)
        {
            try
            {
                return e.GetSupportedPatterns().Contains(pattern);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// First rows of a grid: each cell's value (or name), with * marking cells whose value
        /// can be set directly. Column headers if the grid reports them.
        /// </summary>
        private static void SampleGrid(AutomationElement element, int depth, StringBuilder sb)
        {
            string indent = new string(' ', depth * 2);
            try
            {
                var grid = (GridPattern)element.GetCurrentPattern(GridPattern.Pattern);
                int rows = grid.Current.RowCount, cols = grid.Current.ColumnCount;
                int showCols = Math.Min(cols, GridSampleColumns);

                if (element.TryGetCurrentPattern(TablePattern.Pattern, out object tableObj))
                {
                    AutomationElement[] headers = ((TablePattern)tableObj).Current.GetColumnHeaders();
                    if (headers.Length > 0)
                    {
                        sb.Append(indent).Append("[headers] ")
                          .AppendLine(string.Join(" | ", headers.Take(showCols).Select(h => Short(SafeName(h)))));
                    }
                }

                int editable = 0, readable = 0;
                for (int r = 0; r < Math.Min(rows, GridSampleRows); r++)
                {
                    var cells = new List<string>();
                    for (int col = 0; col < showCols; col++)
                    {
                        cells.Add(Cell(grid, r, col, ref editable, ref readable));
                    }

                    sb.Append(indent).Append($"[row {r}] ").AppendLine(string.Join(" | ", cells));
                }

                sb.Append(indent).AppendLine(
                    $"[grid: {rows} rows x {cols} columns; sampled cells with text: {readable}, settable (*): {editable}" +
                    (cols > showCols ? $"; first {showCols} columns shown" : "") + "]");
            }
            catch (Exception ex)
            {
                sb.Append(indent).AppendLine("[grid cells could not be read: " + ex.GetType().Name + "]");
            }
        }

        private static string Cell(GridPattern grid, int row, int col, ref int editable, ref int readable)
        {
            try
            {
                AutomationElement cell = grid.GetItem(row, col);
                if (cell == null)
                {
                    return "-";
                }

                string text = SafeName(cell);
                bool settable = false;
                if (cell.TryGetCurrentPattern(ValuePattern.Pattern, out object valueObj))
                {
                    var value = ((ValuePattern)valueObj).Current;
                    if (!string.IsNullOrEmpty(value.Value))
                    {
                        text = value.Value;
                    }

                    settable = !value.IsReadOnly;
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    readable++;
                }

                if (settable)
                {
                    editable++;
                }

                text = Short(text);
                if (text.Length > 30)
                {
                    text = text.Substring(0, 30) + "...";
                }

                return (text.Length == 0 ? "\"\"" : text) + (settable ? "*" : "");
            }
            catch (Exception)
            {
                return "?";
            }
        }

        // ------------------------------------------------------------------ summary

        private sealed class Stats
        {
            public int Nodes;
            public int Named;
            public int UnnamedCustom;
            public bool Truncated;
            public Dictionary<string, int> Types { get; } = new Dictionary<string, int>();
            public Dictionary<string, int> Patterns { get; } = new Dictionary<string, int>();

            public void Count(Dictionary<string, int> d, string key) => d[key] = d.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        private static string Assessment(Stats s)
        {
            int Pattern(string name) => s.Patterns.TryGetValue(name, out int n) ? n : 0;
            var sb = new StringBuilder();
            sb.AppendLine("== Summary");
            sb.AppendLine($"Controls found: {s.Nodes}{(s.Truncated ? " (stopped at the limit)" : "")}");
            sb.AppendLine($"With a readable name: {s.Named} ({(s.Nodes == 0 ? 0 : 100 * s.Named / s.Nodes)}%)");
            sb.AppendLine($"Fields that can be read/set (Value): {Pattern("Value")}");
            sb.AppendLine($"Buttons/commands that can be pressed (Invoke): {Pattern("Invoke")}");
            sb.AppendLine($"Check boxes (Toggle): {Pattern("Toggle")}");
            sb.AppendLine($"List/tab items (SelectionItem): {Pattern("SelectionItem")}");
            sb.AppendLine($"Grids/tables (Grid): {Pattern("Grid")}");
            sb.AppendLine($"Unnamed custom panes (may need keyboard or screenshots): {s.UnnamedCustom}");
            sb.AppendLine("Control types: " + string.Join(", ", s.Types.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
            return sb.ToString();
        }

        // ------------------------------------------------------------------ helpers

        private static void Append(StringBuilder sb, string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                sb.Append(' ').Append(label).Append('=').Append(Short(value!));
            }
        }

        private static string Short(string? s)
        {
            string t = (s ?? "").Replace("\r", "\\r").Replace("\n", "\\n");
            return t.Length > MaxText ? t.Substring(0, MaxText) + "..." : t;
        }

        private static string SafeName(AutomationElement e)
        {
            try
            {
                return e.Current.Name;
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static int SafePid(AutomationElement e)
        {
            try
            {
                return e.Current.ProcessId;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static IntPtr SafeHandle(AutomationElement e)
        {
            try
            {
                return new IntPtr(e.Current.NativeWindowHandle);
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        private static string SafeFileVersion(Process p)
        {
            try
            {
                return p.MainModule?.FileVersionInfo.FileVersion ?? "?";
            }
            catch (Exception)
            {
                return "?";
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
    }
}
