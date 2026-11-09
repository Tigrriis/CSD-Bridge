using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CsdBridge.Core.Agent;
using CsdBridge.Core.Routines;
using CadApp = Bricscad.ApplicationServices.Application;

namespace CsdBridge.BricsCAD.Routines
{
    /// <summary>
    /// The Routines window (ribbon, AIROUTINES, or the chat panel's Routines menu): every
    /// saved routine, the user's own and the office's, with the selected one shown in full.
    /// Run, New (blank, or written by Claude from the current chat), Edit, Rename, Duplicate
    /// and Delete. Keys: Enter runs, F2 renames, Delete deletes, Ctrl+N new, F5 refresh.
    /// </summary>
    internal sealed class RoutinesWindow : Form
    {
        private static RoutinesWindow? _instance;

        private readonly TextBox _search;
        private readonly ListView _list;
        private readonly RichTextBox _detail;
        private readonly Button _run;
        private readonly Button _edit;
        private readonly Button _rename;
        private readonly Button _duplicate;
        private readonly Button _delete;
        private readonly Label _summary;
        private readonly ListViewGroup _mineGroup = new ListViewGroup("mine", Routine.LocationName(RoutineLocation.Personal));
        private readonly ListViewGroup _officeGroup = new ListViewGroup("office", Routine.LocationName(RoutineLocation.Office));
        private readonly List<Routine> _routines = new List<Routine>();
        private RoutineLibrary _library;
        private List<string> _problems = new List<string>();
        private string? _message;
        private Routine? _shown;

        private RoutinesWindow()
        {
            Name = "CsdBridgeRoutines"; // BricsCAD remembers size and position by this name
            Text = "CSD-Bridge - Routines";
            Font = new Font("Segoe UI", 9f);
            Size = new Size(980, 600);
            MinimumSize = new Size(660, 380);
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            KeyPreview = true;
            _library = RoutineUi.CreateLibrary();

            // --- top bar: New, search, refresh, folders
            var newButton = new Button { Text = "New ▾", AutoSize = true };
            var newMenu = new ContextMenuStrip();
            newMenu.Items.Add(new ToolStripMenuItem("Blank routine...", null, (s, e) => NewBlank()) { ShortcutKeyDisplayString = "Ctrl+N" });
            newMenu.Items.Add(new ToolStripMenuItem("From the chat in this drawing (Claude writes it)...", null, (s, e) => NewFromChat())
            {
                ToolTipText = "Do the job in the chat panel first. Claude writes it up as a routine and the editor opens for you to check it.",
            });
            newButton.Click += (s, e) => newMenu.Show(newButton, new Point(0, newButton.Height));

            _search = new TextBox { Width = 260 };
            _search.TextChanged += (s, e) => Populate();
            var refresh = new Button { Text = "Refresh", AutoSize = true };
            refresh.Click += (s, e) => Reload();
            var folders = new Button { Text = "Open folder ▾", AutoSize = true };
            var folderMenu = new ContextMenuStrip();
            folderMenu.Opening += (s, e) =>
            {
                folderMenu.Items.Clear();
                folderMenu.Items.Add(new ToolStripMenuItem(Routine.LocationName(RoutineLocation.Personal), null, (s2, e2) => OpenFolder(_library.PersonalFolder)));
                var office = new ToolStripMenuItem(Routine.LocationName(RoutineLocation.Office), null, (s2, e2) => OpenFolder(_library.OfficeFolder!))
                {
                    Enabled = _library.HasOffice,
                    ToolTipText = _library.HasOffice ? _library.OfficeFolder : "Set office_routines_folder in settings.json to share routines with the office",
                };
                folderMenu.Items.Add(office);
                e.Cancel = false;
            };
            folders.Click += (s, e) => folderMenu.Show(folders, new Point(0, folders.Height));

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 6, 6, 2), WrapContents = false };
            top.Controls.Add(newButton);
            top.Controls.Add(new Label { Text = "Search:", AutoSize = true, Margin = new Padding(12, 7, 4, 0) });
            top.Controls.AddRange(new Control[] { _search, refresh, folders });

            // --- left: routine list
            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                BorderStyle = BorderStyle.None,
                ShowItemToolTips = true,
            };
            _list.Columns.Add("Name", 220);
            _list.Columns.Add("Description", 300);
            _list.Groups.Add(_mineGroup);
            _list.Groups.Add(_officeGroup);
            _list.SelectedIndexChanged += (s, e) => ShowSelected();
            _list.ItemActivate += (s, e) => RunSelected(); // double-click or Enter

            // --- right: the routine in full, and actions on it
            _detail = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Window,
                DetectUrls = false,
            };
            _run = new Button { Text = "Run...", AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
            _edit = new Button { Text = "Edit...", AutoSize = true };
            _rename = new Button { Text = "Rename...", AutoSize = true };
            _duplicate = new Button { Text = "Duplicate", AutoSize = true };
            _delete = new Button { Text = "Delete", AutoSize = true };
            _run.Click += (s, e) => RunSelected();
            _edit.Click += (s, e) => EditSelected();
            _rename.Click += (s, e) => RenameSelected();
            _duplicate.Click += (s, e) => DuplicateSelected();
            _delete.Click += (s, e) => DeleteSelected();
            var tips = new ToolTip();
            tips.SetToolTip(_run, "Fill in the inputs and run it in the drawing that is open now (Enter or double-click)");
            tips.SetToolTip(_edit, "Change the name, inputs, steps or where it's saved");
            tips.SetToolTip(_rename, "Rename it (F2)");
            tips.SetToolTip(_duplicate, "Make your own copy in My routines, e.g. to change an office routine");
            tips.SetToolTip(_delete, "Move it to the 'deleted' folder next to it (Delete)");
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 4, 0, 4) };
            actions.Controls.AddRange(new Control[] { _run, _edit, _rename, _duplicate, _delete });
            var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 4, 0) };
            right.Controls.Add(_detail);
            right.Controls.Add(actions);

            var listMenu = new ContextMenuStrip();
            listMenu.Items.Add("Run...", null, (s, e) => RunSelected());
            listMenu.Items.Add("Edit...", null, (s, e) => EditSelected());
            listMenu.Items.Add(new ToolStripMenuItem("Rename...", null, (s, e) => RenameSelected()) { ShortcutKeyDisplayString = "F2" });
            listMenu.Items.Add("Duplicate", null, (s, e) => DuplicateSelected());
            listMenu.Items.Add(new ToolStripMenuItem("Delete", null, (s, e) => DeleteSelected()) { ShortcutKeyDisplayString = "Del" });
            listMenu.Opening += (s, e) => e.Cancel = _shown == null;
            _list.ContextMenuStrip = listMenu;

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
            split.Panel1.Controls.Add(_list);
            split.Panel2.Controls.Add(right);

            _summary = new Label { Dock = DockStyle.Bottom, Height = 36, ForeColor = SystemColors.GrayText, Padding = new Padding(6, 3, 6, 0) };

            Controls.Add(split);
            Controls.Add(top);
            Controls.Add(_summary);

            Load += (s, e) => split.SplitterDistance = Math.Max(320, (int)(ClientSize.Width * 0.45));
            KeyDown += OnKeyDown;
            AgentRunner.BusyChanged += OnBusyChanged;
            FormClosed += (s, e) => AgentRunner.BusyChanged -= OnBusyChanged;

            RoutineUi.SeedExamplesIfNew(_library);
            Reload();
        }

        /// <summary>The window if it is open, e.g. to own a dialog.</summary>
        public static RoutinesWindow? OpenInstance => _instance != null && !_instance.IsDisposed && _instance.Visible ? _instance : null;

        public static void ShowWindow()
        {
            if (_instance != null && !_instance.IsDisposed)
            {
                if (!_instance.Visible)
                {
                    _instance.Show();
                }

                if (_instance.WindowState == FormWindowState.Minimized)
                {
                    _instance.WindowState = FormWindowState.Normal;
                }

                _instance.Reload();
                _instance.Activate();
                return;
            }

            _instance = new RoutinesWindow();
            try
            {
                // Owned by BricsCAD's main window; persist = remember size and position.
                CadApp.ShowModelessDialog(null, _instance, true);
            }
            catch (Exception)
            {
                _instance.Show();
            }
        }

        /// <summary>Reload the list if the window is open, selecting the routine saved at <paramref name="selectPath"/>.</summary>
        public static void RefreshIfOpen(string? selectPath = null)
        {
            if (_instance != null && !_instance.IsDisposed)
            {
                _instance.Reload(selectPath);
            }
        }

        // ------------------------------------------------------------------ data

        private void Reload(string? selectPath = null)
        {
            _library = RoutineUi.CreateLibrary();
            _routines.Clear();
            try
            {
                _routines.AddRange(_library.LoadAll(out _problems));
            }
            catch (Exception ex)
            {
                _problems = new List<string> { "Couldn't read the routines: " + ex.Message };
            }

            _list.ShowGroups = _library.HasOffice;
            Populate(selectPath ?? _shown?.FilePath);
        }

        private void Populate(string? selectPath = null)
        {
            selectPath ??= _shown?.FilePath;
            string query = _search.Text.Trim();

            _list.BeginUpdate();
            _list.Items.Clear();
            // In the order shown (mine, then the office's), so the first item is the top one.
            foreach (Routine r in _routines.Where(r => Matches(r, query)).OrderBy(r => r.Location))
            {
                var item = new ListViewItem(r.Name) { Tag = r, ToolTipText = r.Description ?? r.Name };
                item.SubItems.Add(r.Description ?? "");
                item.Group = r.Location == RoutineLocation.Office ? _officeGroup : _mineGroup;
                if (r.LoadProblem != null)
                {
                    item.ForeColor = Color.Firebrick;
                }

                _list.Items.Add(item);
            }

            _list.EndUpdate();

            ListViewItem? select = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => SamePath((i.Tag as Routine)?.FilePath, selectPath))
                ?? (_list.Items.Count > 0 ? _list.Items[0] : null);
            if (select != null)
            {
                select.Selected = true;
                select.Focused = true;
                select.EnsureVisible();
                ShowDetail(select.Tag as Routine);
            }
            else
            {
                ShowDetail(null);
            }

            UpdateSummary();
        }

        private static bool Matches(Routine r, string query)
        {
            if (query.Length == 0)
            {
                return true;
            }

            bool Has(string? s) => s != null && s.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            return Has(r.Name) || Has(r.Description) || Has(r.Instructions) || r.Inputs.Any(i => Has(i.DisplayLabel) || Has(i.Name));
        }

        private void ShowSelected()
        {
            if (_list.SelectedItems.Count > 0)
            {
                ShowDetail(_list.SelectedItems[0].Tag as Routine);
            }
        }

        // ------------------------------------------------------------------ detail

        private void ShowDetail(Routine? r)
        {
            _shown = r;
            UpdateButtons();
            _detail.Clear();
            if (r == null)
            {
                Append(_routines.Count == 0
                    ? "No routines yet.\n\nTo make one, do the job once with Claude in the chat panel, then use Routines > Save this chat as a routine " +
                      "(or New > From the chat in this drawing here). Claude writes it up with its inputs and steps, and you check it before it's saved.\n\n" +
                      "Or use New > Blank routine to write one yourself."
                    : "No routines match the search.", SystemColors.GrayText);
                return;
            }

            Append(r.Name, SystemColors.WindowText, bold: true, size: 11f);
            string changed = r.LoadedWriteTimeUtc is DateTime t ? "  -  changed " + t.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture) : "";
            Append(Routine.LocationName(r.Location) + "  -  " + Path.GetFileName(r.FilePath ?? "") + changed, SystemColors.GrayText);
            if (r.LoadProblem != null)
            {
                Append("This file has problems: " + r.LoadProblem + " Edit and save it to fix them.", Color.Firebrick);
            }

            if (!string.IsNullOrWhiteSpace(r.Description))
            {
                Append("");
                Append(r.Description!);
            }

            var settings = new List<string>();
            if (r.Effort is EffortMode effort)
            {
                settings.Add($"{effort} effort");
            }

            if (r.CsdEdit)
            {
                settings.Add("changes Civil Site Design values (needs AICSD Edit)");
            }

            if (settings.Count > 0)
            {
                Append(string.Join("  -  ", settings), SystemColors.GrayText);
            }

            Append("");
            Append("Inputs", SystemColors.WindowText, bold: true);
            if (r.Inputs.Count == 0)
            {
                Append("None: it runs as it is.", SystemColors.GrayText);
            }

            foreach (RoutineInput input in r.Inputs)
            {
                Append("  " + DescribeInput(input));
            }

            Append("");
            Append("Steps", SystemColors.WindowText, bold: true);
            Append(r.Instructions.Length > 0 ? r.Instructions : "(none yet)");
            _detail.SelectionStart = 0;
            _detail.ScrollToCaret();
        }

        private static string DescribeInput(RoutineInput input)
        {
            var parts = new List<string>();
            switch (input.Type)
            {
                case RoutineInputType.Point:
                    parts.Add("a point: Claude asks you to pick it");
                    break;
                case RoutineInputType.Selection:
                    parts.Add("objects: select them before running, or Claude asks");
                    break;
                case RoutineInputType.Choice:
                    parts.Add("one of " + string.Join(" / ", input.Options));
                    break;
                case RoutineInputType.YesNo:
                    parts.Add("yes/no");
                    break;
                default:
                    parts.Add(input.Type == RoutineInputType.Number ? "number" : "text");
                    break;
            }

            if (input.Required)
            {
                parts.Add("required");
            }

            if (!string.IsNullOrWhiteSpace(input.Default))
            {
                parts.Add("default " + input.Default + (string.IsNullOrWhiteSpace(input.Unit) ? "" : " " + input.Unit));
            }

            string text = $"{input.DisplayLabel} {{{{{input.Name}}}}} - {string.Join(", ", parts)}";
            return string.IsNullOrWhiteSpace(input.Help) ? text : text + ". " + input.Help;
        }

        private void Append(string text, Color? color = null, bool bold = false, float? size = null)
        {
            _detail.SelectionStart = _detail.TextLength;
            _detail.SelectionLength = 0;
            _detail.SelectionColor = color ?? SystemColors.WindowText;
            _detail.SelectionFont = new Font(Font.FontFamily, size ?? Font.Size, bold ? FontStyle.Bold : FontStyle.Regular);
            _detail.AppendText(text + "\n");
        }

        private void UpdateButtons()
        {
            bool has = _shown != null;
            _edit.Enabled = has;
            _rename.Enabled = has;
            _duplicate.Enabled = has;
            _delete.Enabled = has;
            _run.Enabled = has && !AgentRunner.IsBusy;
        }

        private void UpdateSummary()
        {
            if (_problems.Count > 0)
            {
                _summary.ForeColor = Color.Firebrick;
                _summary.Text = string.Join("  ", _problems);
                return;
            }

            _summary.ForeColor = SystemColors.GrayText;
            string counts = _list.Items.Count == _routines.Count ? $"{_routines.Count} routine(s)" : $"{_list.Items.Count} of {_routines.Count} routine(s)";
            string office = _library.HasOffice ? "Office: " + _library.OfficeFolder : "No office folder (set office_routines_folder in settings.json to share routines)";
            _summary.Text = (_message != null ? _message + "\n" : "") + $"{counts}  -  Mine: {_library.PersonalFolder}  -  {office}";
        }

        private void Say(string message)
        {
            _message = message;
            UpdateSummary();
        }

        private void OnBusyChanged(bool busy)
        {
            if (!IsDisposed)
            {
                UpdateButtons();
            }
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F2 && _shown != null)
            {
                RenameSelected();
            }
            else if (e.KeyCode == Keys.Delete && _list.Focused && _shown != null)
            {
                DeleteSelected();
            }
            else if (e.KeyCode == Keys.N && e.Control)
            {
                NewBlank();
            }
            else if (e.KeyCode == Keys.F && e.Control)
            {
                _search.Focus();
                _search.SelectAll();
            }
            else if (e.KeyCode == Keys.F5)
            {
                Reload();
            }
            else
            {
                return;
            }

            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        // ------------------------------------------------------------------ actions

        private void RunSelected()
        {
            if (_shown == null)
            {
                return;
            }

            if (AgentRunner.IsBusy)
            {
                Say("Wait for the current request to finish (or press Stop in the chat panel), then run the routine.");
                return;
            }

            if (RoutineUi.Run(this, _shown))
            {
                Say($"Running \"{_shown.Name}\": follow it in the chat panel.");
            }
        }

        private void NewBlank()
        {
            var blank = new Routine { Name = "", Instructions = "" };
            Routine? saved = RoutineUi.Edit(this, _library, blank, null, RoutineLocation.Personal, null, isDraft: false);
            if (saved != null)
            {
                Say($"Saved \"{saved.Name}\".");
                Reload(saved.FilePath);
            }
        }

        private void NewFromChat()
        {
            string? problem = AgentRunner.QueueSaveChatAsRoutine();
            if (problem != null)
            {
                MessageBox.Show(this, problem, "New routine from the chat", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ChatPalette.Show();
            Say("Claude is writing the routine from this drawing's chat. The editor opens when it's done.");
        }

        private void EditSelected()
        {
            if (_shown == null)
            {
                return;
            }

            string? banner = _shown.LoadProblem != null
                ? "This file had problems (" + _shown.LoadProblem + "). Saving rewrites it in the standard format."
                : null;
            Routine? saved = RoutineUi.Edit(this, _library, _shown.Clone(), _shown, _shown.Location, banner, isDraft: false);
            if (saved != null)
            {
                Say($"Saved \"{saved.Name}\".");
                Reload(saved.FilePath);
            }
        }

        private void RenameSelected()
        {
            Routine? r = _shown;
            if (r == null)
            {
                return;
            }

            string? name = RoutineUi.AskText(this, "Rename routine", "New name:", r.Name, n =>
                Routine.CheckName(n)
                ?? (!string.Equals(n, r.Name, StringComparison.OrdinalIgnoreCase) && _library.NameTaken(n, r.Location, r.FilePath)
                    ? $"There's already a routine called \"{n}\" in {Routine.LocationName(r.Location)}."
                    : null));
            if (name == null || name == r.Name)
            {
                return;
            }

            try
            {
                Routine renamed;
                try
                {
                    renamed = _library.Rename(r, name);
                }
                catch (RoutineConflictException ex)
                {
                    if (!ConfirmOverwrite(ex.Message))
                    {
                        return;
                    }

                    renamed = _library.Rename(r, name, overwriteChanges: true);
                }

                Say($"Renamed \"{r.Name}\" to \"{renamed.Name}\".");
                Reload(renamed.FilePath);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException)
            {
                RoutineUi.ShowError(this, "Rename routine", "Couldn't rename it: " + ex.Message);
            }
        }

        private void DuplicateSelected()
        {
            if (_shown == null)
            {
                return;
            }

            try
            {
                Routine copy = _library.Duplicate(_shown, RoutineLocation.Personal);
                Say($"Made \"{copy.Name}\" in My routines. Edit or rename it as you like.");
                Reload(copy.FilePath);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException)
            {
                RoutineUi.ShowError(this, "Duplicate routine", "Couldn't copy it: " + ex.Message);
            }
        }

        private void DeleteSelected()
        {
            Routine? r = _shown;
            if (r == null)
            {
                return;
            }

            string where = r.Location == RoutineLocation.Office ? " It is an office routine, so it goes for everyone." : "";
            if (MessageBox.Show(this,
                    $"Delete the routine \"{r.Name}\"?{where}\n\nIt's moved to the '{RoutineLibrary.DeletedFolderName}' folder in {Routine.LocationName(r.Location)}, so it can be recovered.",
                    "Delete routine", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK)
            {
                return;
            }

            try
            {
                _library.Delete(r);
                _shown = null;
                Say($"Deleted \"{r.Name}\".");
                Reload();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                RoutineUi.ShowError(this, "Delete routine", "Couldn't delete it: " + ex.Message);
                Reload();
            }
        }

        private bool ConfirmOverwrite(string message) =>
            MessageBox.Show(this, message + "\n\nReplace their version with yours?", "Routine changed",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

        private void OpenFolder(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                RoutineUi.ShowError(this, Text, ex.Message);
            }
        }

        private static bool SamePath(string? a, string? b) =>
            a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
