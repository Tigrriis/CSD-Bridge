using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// AIFORMDUMP: a read-only look at other add-ins' Windows Forms dialogs from inside
    /// BricsCAD (Civil Site Design's dialogs run in the same process). For each open dialog
    /// it lists every control's name, type and current value, and reads data grids cell by
    /// cell through the grid's own methods - which UI Automation could not do for CSD's
    /// grids. It also reports which editing methods each grid offers, to plan how values
    /// can later be written so the dialog recalculates as if typed. Nothing is changed.
    ///
    /// Runs after a countdown on a background thread; each dialog is read on its own UI
    /// thread (Control.BeginInvoke works while a modal dialog's message loop is running).
    /// </summary>
    internal static class FormDump
    {
        private const int MaxGridRows = 80;
        private const int MaxGridColumns = 20;
        private const int MaxText = 160;
        private const int MaxComboItems = 25;

        public static string Folder => UiDump.Folder;

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
                        File.WriteAllText(Path.Combine(Folder, "formdump-error.txt"), ex.ToString());
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
                Name = "CSD-Bridge form dump",
            };
            thread.Start();
        }

        public static string Capture()
        {
            Process me = Process.GetCurrentProcess();
            var text = new StringBuilder();
            text.AppendLine("CSD-Bridge form dump - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            text.AppendLine($"BricsCAD process {me.Id}   CSD-Bridge: {typeof(FormDump).Assembly.GetName().Version}   .NET: {Environment.Version}");
            text.AppendLine("This lists the controls and values in open dialogs, including grid contents and what is typed in them.");
            text.AppendLine("Check it contains nothing confidential before sending it on. Nothing was changed.");
            text.AppendLine();

            List<Form> forms = FindForms(me.Id);
            text.AppendLine($"== Open dialogs found: {forms.Count}");
            if (forms.Count == 0)
            {
                text.AppendLine("No Windows Forms dialogs found. Was the dialog open when the countdown finished?");
            }

            foreach (Form form in forms)
            {
                text.AppendLine();
                text.Append(DumpOnUiThread(form));
            }

            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, "formdump-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
            File.WriteAllText(path, text.ToString(), Encoding.UTF8);
            return path;
        }

        // ------------------------------------------------------------------ finding dialogs

        /// <summary>Open forms other than CSD-Bridge's own: Application.OpenForms plus any top-level window that maps to a Form.</summary>
        private static List<Form> FindForms(int pid)
        {
            var found = new List<Form>();
            void Add(Form? f)
            {
                if (f != null && !found.Contains(f) && f.GetType().Assembly != typeof(FormDump).Assembly)
                {
                    found.Add(f);
                }
            }

            try
            {
                foreach (Form f in Application.OpenForms.Cast<Form>().ToList())
                {
                    Add(f);
                }
            }
            catch (Exception)
            {
                // The collection changed while we read it; the window scan below still runs.
            }

            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint windowPid);
                if (windowPid == pid)
                {
                    try
                    {
                        Add(Control.FromHandle(hwnd) as Form);
                    }
                    catch (Exception)
                    {
                        // Not a Windows Forms window.
                    }
                }

                return true;
            }, IntPtr.Zero);

            return found;
        }

        private static string DumpOnUiThread(Form form)
        {
            try
            {
                if (!form.IsHandleCreated || form.IsDisposed)
                {
                    return $"== Form {form.Name}: not available (closed or not created)\n";
                }

                IAsyncResult pending = form.BeginInvoke(new Func<string>(() => DumpForm(form)));
                if (!pending.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(20)))
                {
                    return $"== Form {form.Name}: its window didn't respond within 20 s\n";
                }

                return form.EndInvoke(pending) as string ?? string.Empty;
            }
            catch (Exception ex)
            {
                return $"== Form {form.Name}: could not be read ({ex.GetType().Name}: {ex.Message})\n";
            }
        }

        // ------------------------------------------------------------------ dumping (on the dialog's UI thread)

        private static string DumpForm(Form form)
        {
            var sb = new StringBuilder();
            Type t = form.GetType();
            sb.AppendLine($"== Form \"{Short(form.Text)}\" name={form.Name} type={t.FullName}");
            sb.AppendLine($"   assembly={AssemblyInfo(t)}  modal={form.Modal}  visible={form.Visible}  size={form.Width}x{form.Height}");
            sb.AppendLine($"   AcceptButton={NameOf(form.AcceptButton as Control)}  CancelButton={NameOf(form.CancelButton as Control)}");
            foreach (Control child in form.Controls)
            {
                DumpControl(child, 1, sb);
            }

            return sb.ToString();
        }

        private static void DumpControl(Control c, int depth, StringBuilder sb)
        {
            string indent = new string(' ', depth * 2);
            Type t = c.GetType();
            bool winForms = t.Namespace != null && t.Namespace.StartsWith("System.Windows.Forms", StringComparison.Ordinal);
            var line = new StringBuilder(indent).Append(winForms ? t.Name : t.FullName);
            if (!winForms)
            {
                line.Append(" [").Append(AssemblyInfo(t)).Append(']');
            }

            if (!string.IsNullOrEmpty(c.Name))
            {
                line.Append(" name=").Append(c.Name);
            }

            if (!c.Visible)
            {
                line.Append(" hidden");
            }

            if (!c.Enabled)
            {
                line.Append(" disabled");
            }

            line.Append(Details(c));
            sb.AppendLine(line.ToString());

            GridReader? grid = GridReader.TryCreate(c);
            if (grid != null)
            {
                grid.Dump(indent + "  ", sb);
                return; // a grid's child controls are its editors
            }

            if (c is ToolStrip strip)
            {
                DumpToolStripItems(strip.Items, depth + 1, sb);
            }

            foreach (Control child in c.Controls)
            {
                DumpControl(child, depth + 1, sb);
            }
        }

        private static string Details(Control c)
        {
            switch (c)
            {
                case TextBoxBase tb:
                    return $" text=\"{(tb is TextBox box && box.UseSystemPasswordChar ? "****" : Short(tb.Text))}\"{(tb.ReadOnly ? " read-only" : "")}";
                case ComboBox cb:
                {
                    var items = cb.Items.Cast<object>().Take(MaxComboItems).Select(i => Short(cb.GetItemText(i)));
                    return $" value=\"{Short(cb.Text)}\" index={cb.SelectedIndex} items[{cb.Items.Count}]=[{string.Join(", ", items)}{(cb.Items.Count > MaxComboItems ? ", ..." : "")}]";
                }

                case CheckBox chk:
                    return $" \"{Short(chk.Text)}\" checked={chk.Checked}";
                case RadioButton rb:
                    return $" \"{Short(rb.Text)}\" checked={rb.Checked}";
                case NumericUpDown num:
                    return $" value={num.Value}";
                case ListBox lb:
                    return $" items[{lb.Items.Count}] selected={lb.SelectedIndex}";
                case TabControl tabs:
                    return $" selected=\"{tabs.SelectedTab?.Text}\" tabs=[{string.Join(", ", tabs.TabPages.Cast<TabPage>().Select(p => p.Text))}]";
                case ButtonBase b:
                    return string.IsNullOrEmpty(b.Text) ? "" : $" \"{Short(b.Text)}\"";
                default:
                    return string.IsNullOrEmpty(c.Text) ? "" : $" text=\"{Short(c.Text)}\"";
            }
        }

        private static void DumpToolStripItems(ToolStripItemCollection items, int depth, StringBuilder sb)
        {
            string indent = new string(' ', depth * 2);
            foreach (ToolStripItem item in items)
            {
                sb.Append(indent).Append("item ").Append(item.GetType().Name);
                if (!string.IsNullOrEmpty(item.Name))
                {
                    sb.Append(" name=").Append(item.Name);
                }

                if (!string.IsNullOrEmpty(item.Text))
                {
                    sb.Append(" \"").Append(Short(item.Text)).Append('"');
                }

                if (!item.Enabled)
                {
                    sb.Append(" disabled");
                }

                if (item is ToolStripTextBox tb)
                {
                    sb.Append(" text=\"").Append(Short(tb.Text)).Append('"');
                }

                sb.AppendLine();
                if (item is ToolStripDropDownItem dd && dd.HasDropDownItems)
                {
                    DumpToolStripItems(dd.DropDownItems, depth + 1, sb);
                }
            }
        }

        // ------------------------------------------------------------------ grids

        /// <summary>
        /// Reads grids through whatever their own API offers: DataGridView, ComponentOne
        /// FlexGrid (GetDataDisplay/GetData + Rows/Cols), ActiveX MSFlexGrid wrappers
        /// (get_TextMatrix + Rows/Cols), or any [int,int] indexer.
        /// </summary>
        private sealed class GridReader
        {
            private readonly Control _grid;
            private readonly Func<int, int, object?> _read;
            private readonly string _how;
            private readonly int _rows;
            private readonly int _cols;
            private readonly int _fixedRows;
            private readonly int _fixedCols;

            private GridReader(Control grid, Func<int, int, object?> read, string how, int rows, int cols, int fixedRows, int fixedCols)
            {
                _grid = grid;
                _read = read;
                _how = how;
                _rows = rows;
                _cols = cols;
                _fixedRows = fixedRows;
                _fixedCols = fixedCols;
            }

            public static GridReader? TryCreate(Control c)
            {
                try
                {
                    if (c is DataGridView dgv)
                    {
                        return new GridReader(c, (r, col) => dgv.Rows[r].Cells[col].FormattedValue, "DataGridView", dgv.Rows.Count, dgv.Columns.Count, 0, 0);
                    }

                    Type t = c.GetType();
                    var two = new[] { typeof(int), typeof(int) };

                    MethodInfo? display = t.GetMethod("GetDataDisplay", two);
                    MethodInfo? data = t.GetMethod("GetData", two);
                    MethodInfo? reader = display ?? data;
                    if (reader != null)
                    {
                        object? rowsObj = t.GetProperty("Rows")?.GetValue(c);
                        object? colsObj = t.GetProperty("Cols")?.GetValue(c);
                        return new GridReader(
                            c,
                            (r, col) => reader.Invoke(c, new object[] { r, col }),
                            reader.Name,
                            CountOf(rowsObj),
                            CountOf(colsObj),
                            IntProp(rowsObj, "Fixed"),
                            IntProp(colsObj, "Fixed"));
                    }

                    MethodInfo? textMatrix = t.GetMethod("get_TextMatrix", two);
                    if (textMatrix != null)
                    {
                        return new GridReader(
                            c,
                            (r, col) => textMatrix.Invoke(c, new object[] { r, col }),
                            "TextMatrix",
                            IntProp(c, "Rows"),
                            IntProp(c, "Cols"),
                            IntProp(c, "FixedRows"),
                            IntProp(c, "FixedCols"));
                    }

                    PropertyInfo? indexer = t.GetProperties().FirstOrDefault(p =>
                    {
                        ParameterInfo[] ps = p.GetIndexParameters();
                        return ps.Length == 2 && ps.All(x => x.ParameterType == typeof(int));
                    });
                    if (indexer != null && (t.GetProperty("Rows") != null || t.GetProperty("RowCount") != null))
                    {
                        object? rowsObj = t.GetProperty("Rows")?.GetValue(c);
                        object? colsObj = t.GetProperty("Cols")?.GetValue(c) ?? t.GetProperty("Columns")?.GetValue(c);
                        return new GridReader(
                            c,
                            (r, col) => indexer.GetValue(c, new object[] { r, col }),
                            "indexer",
                            rowsObj != null ? CountOf(rowsObj) : IntProp(c, "RowCount"),
                            colsObj != null ? CountOf(colsObj) : IntProp(c, "ColumnCount"),
                            0,
                            0);
                    }
                }
                catch (Exception)
                {
                    // Not a grid we can read.
                }

                return null;
            }

            public void Dump(string indent, StringBuilder sb)
            {
                sb.AppendLine($"{indent}[grid read via {_how}: {_rows} rows x {_cols} columns, fixed rows {_fixedRows}, fixed columns {_fixedCols}]");
                sb.AppendLine($"{indent}[edit-related members: {EditMembers(_grid.GetType())}]");

                int cols = Math.Min(_cols, MaxGridColumns);
                for (int r = 0; r < Math.Min(_rows, MaxGridRows); r++)
                {
                    var cells = new List<string>(cols);
                    for (int col = 0; col < cols; col++)
                    {
                        cells.Add(Cell(r, col));
                    }

                    sb.Append(indent).Append($"[row {r}] ").AppendLine(string.Join(" | ", cells));
                }

                if (_rows > MaxGridRows || _cols > MaxGridColumns)
                {
                    sb.AppendLine($"{indent}[first {Math.Min(_rows, MaxGridRows)} rows and {cols} columns shown]");
                }
            }

            private string Cell(int r, int col)
            {
                try
                {
                    object? v = _read(r, col);
                    string s = v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
                    s = Short(s);
                    return s.Length > 40 ? s.Substring(0, 40) + "..." : s;
                }
                catch (Exception ex)
                {
                    return "?" + (ex.InnerException ?? ex).GetType().Name;
                }
            }

            private static string EditMembers(Type t)
            {
                string[] interesting =
                {
                    "StartEditing", "FinishEditing", "SetData", "set_TextMatrix", "Select", "Editor",
                    "AllowEditing", "Row", "Col", "AfterEdit", "ValidateEdit", "CellChanged",
                };
                var found = t.GetMembers(BindingFlags.Public | BindingFlags.Instance)
                    .Select(m => m.Name)
                    .Where(interesting.Contains)
                    .Distinct()
                    .OrderBy(n => n);
                string list = string.Join(", ", found);
                return list.Length == 0 ? "none found" : list;
            }

            private static int CountOf(object? o)
            {
                if (o is int i)
                {
                    return i;
                }

                return IntProp(o, "Count");
            }
        }

        // ------------------------------------------------------------------ helpers

        private static int IntProp(object? o, string name)
        {
            try
            {
                object? v = o?.GetType().GetProperty(name)?.GetValue(o);
                return v is int i ? i : v is short s ? s : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static string AssemblyInfo(Type t)
        {
            AssemblyName n = t.Assembly.GetName();
            return $"{n.Name} {n.Version}";
        }

        private static string NameOf(Control? c) => c == null ? "-" : (string.IsNullOrEmpty(c.Name) ? c.GetType().Name : c.Name);

        private static string Short(string? s)
        {
            string t = (s ?? "").Replace("\r", "\\r").Replace("\n", "\\n");
            return t.Length > MaxText ? t.Substring(0, MaxText) + "..." : t;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    }
}
