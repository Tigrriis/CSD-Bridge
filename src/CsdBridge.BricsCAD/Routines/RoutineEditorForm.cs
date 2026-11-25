using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CsdBridge.Core.Agent;
using CsdBridge.Core.Routines;

namespace CsdBridge.BricsCAD.Routines
{
    /// <summary>
    /// Create or change a routine: its name, where it's saved (mine or the office's),
    /// effort, the inputs it asks for, and the steps for Claude. Also used to review a
    /// routine Claude wrote from a chat before it is saved. Changing the name renames the
    /// file; changing "Saved in" moves it.
    /// </summary>
    internal sealed class RoutineEditorForm : Form
    {
        private static readonly RoutineInputType[] Types =
        {
            RoutineInputType.Text, RoutineInputType.Number, RoutineInputType.Choice,
            RoutineInputType.YesNo, RoutineInputType.Point, RoutineInputType.Selection,
        };

        private static readonly string[] TypeLabels = { "Text", "Number", "Choice", "Yes/No", "Point (pick)", "Objects (select)" };
        private static readonly string[] EffortLabels = { "Your setting", "Quick", "Normal", "Thorough" };

        private readonly RoutineLibrary _library;
        private readonly Routine _source;
        private readonly Routine? _original;
        private readonly bool _isDraft;
        private readonly string _initialText;

        private readonly TextBox _name;
        private readonly TextBox _description;
        private readonly ComboBox _location;
        private readonly ComboBox _effort;
        private readonly CheckBox _csdEdit;
        private readonly DataGridView _inputs;
        private readonly TextBox _instructions;
        private readonly Label _problems;

        /// <param name="routine">What to show (a copy is edited).</param>
        /// <param name="original">The saved routine this edit replaces, or null for a new one.</param>
        /// <param name="location">Where it will be saved.</param>
        /// <param name="banner">A note across the top, e.g. that Claude wrote this draft.</param>
        /// <param name="isDraft">Not saved yet: closing without saving asks first.</param>
        public RoutineEditorForm(RoutineLibrary library, Routine routine, Routine? original, RoutineLocation location, string? banner, bool isDraft)
        {
            _library = library;
            _source = routine.Clone();
            _original = original;
            _isDraft = isDraft;

            Name = "CsdBridgeRoutineEditor";
            Text = original == null ? "New routine" : "Edit routine - " + original.Name;
            Font = new Font("Segoe UI", 9f);
            Size = new Size(900, 740);
            MinimumSize = new Size(680, 560);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;

            // --- banner
            Label? bannerLabel = null;
            if (!string.IsNullOrWhiteSpace(banner))
            {
                bannerLabel = new Label
                {
                    Dock = DockStyle.Top,
                    AutoSize = false,
                    Height = 40,
                    Text = banner,
                    BackColor = Color.FromArgb(255, 244, 206),
                    ForeColor = Color.FromArgb(90, 60, 0),
                    Padding = new Padding(8, 4, 8, 4),
                    TextAlign = ContentAlignment.MiddleLeft,
                };
            }

            // --- name, description, where, effort
            _name = new TextBox { Dock = DockStyle.Fill, Text = _source.Name };
            _description = new TextBox { Dock = DockStyle.Fill, Text = _source.Description ?? "" };
            _location = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
            _location.Items.Add(Routine.LocationName(RoutineLocation.Personal));
            if (library.HasOffice)
            {
                _location.Items.Add(Routine.LocationName(RoutineLocation.Office));
            }

            _location.SelectedIndex = location == RoutineLocation.Office && library.HasOffice ? 1 : 0;
            _effort = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
            _effort.Items.AddRange(EffortLabels);
            _effort.SelectedIndex = _source.Effort switch
            {
                EffortMode.Quick => 1,
                EffortMode.Normal => 2,
                EffortMode.Thorough => 3,
                _ => 0,
            };
            _csdEdit = new CheckBox { Text = "Changes Civil Site Design values (needs AICSD Edit)", AutoSize = true, Checked = _source.CsdEdit };

            var tips = new ToolTip();
            tips.SetToolTip(_location, library.HasOffice
                ? "My routines are yours only; Office routines are shared with everyone through the office folder"
                : "To share routines with the office, set office_routines_folder in settings.json");
            tips.SetToolTip(_effort, "How hard Claude works on this routine. \"Your setting\" uses the Effort box in the chat panel.");
            tips.SetToolTip(_csdEdit, "The run dialog warns if Civil Site Design edits are off");

            var whereRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Dock = DockStyle.Fill };
            whereRow.Controls.Add(_location);
            whereRow.Controls.Add(new Label { Text = "Effort", AutoSize = true, Margin = new Padding(16, 6, 4, 0) });
            whereRow.Controls.Add(_effort);
            whereRow.Controls.Add(_csdEdit);
            _csdEdit.Margin = new Padding(16, 4, 0, 0);

            var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8, 8, 8, 4) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(header, "Name", _name);
            AddRow(header, "Description", _description);
            AddRow(header, "Saved in", whereRow);
            tips.SetToolTip(_description, "One line on what it does, shown in the routines list");

            // --- inputs
            _inputs = CreateGrid();
            foreach (RoutineInput input in _source.Inputs)
            {
                AddInputRow(input);
            }

            var addInput = new Button { Text = "Add input", AutoSize = true };
            var removeInput = new Button { Text = "Remove", AutoSize = true };
            var moveUp = new Button { Text = "Up", AutoSize = true };
            var moveDown = new Button { Text = "Down", AutoSize = true };
            var insert = new Button { Text = "Insert {{name}} in steps", AutoSize = true };
            addInput.Click += (s, e) => AddInput();
            removeInput.Click += (s, e) => RemoveInput();
            moveUp.Click += (s, e) => MoveInput(-1);
            moveDown.Click += (s, e) => MoveInput(+1);
            insert.Click += (s, e) => InsertPlaceholder();
            tips.SetToolTip(insert, "Put the selected input's {{name}} into the steps at the cursor");
            var inputButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 2, 0, 2) };
            inputButtons.Controls.AddRange(new Control[] { addInput, removeInput, moveUp, moveDown, insert });

            var inputsTitle = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 38,
                Text = "Inputs: what changes from run to run. Text, numbers, choices and yes/no are asked in the run dialog; " +
                       "points and objects are picked in the drawing when Claude asks.",
                Padding = new Padding(0, 2, 0, 0),
            };
            var inputsPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 0) };
            inputsPanel.Controls.Add(_inputs);
            inputsPanel.Controls.Add(inputButtons);
            inputsPanel.Controls.Add(inputsTitle);

            // --- steps
            _instructions = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                AcceptsReturn = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true,
                Font = new Font("Consolas", 10f),
                Text = (_source.Instructions ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"),
            };
            var stepsTitle = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 38,
                Text = "Steps for Claude, in order. Refer to inputs as {{name}}. Say which tools to use, the rules to follow, " +
                       "and the checks to make before finishing.",
                Padding = new Padding(0, 4, 0, 0),
            };
            var stepsPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 0) };
            stepsPanel.Controls.Add(_instructions);
            stepsPanel.Controls.Add(stepsTitle);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(inputsPanel);
            split.Panel2.Controls.Add(stepsPanel);

            // --- problems and buttons
            _problems = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = 0, ForeColor = Color.Firebrick, Padding = new Padding(8, 2, 8, 2) };
            var save = new Button { Text = "Save", AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            save.Click += (s, e) => Save();
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 4, 8, 8) };
            bottom.Controls.AddRange(new Control[] { cancel, save });

            Controls.Add(split);
            Controls.Add(header);
            if (bannerLabel != null)
            {
                Controls.Add(bannerLabel);
            }

            Controls.Add(_problems);
            Controls.Add(bottom);
            CancelButton = cancel;

            Load += (s, e) => split.SplitterDistance = Math.Max(150, Math.Min(300, split.Height * 2 / 5));
            Shown += (s, e) =>
            {
                // Cursor at the start, nothing selected, so a stray key doesn't replace the steps.
                _instructions.Select(0, 0);
                (_name.Text.Length == 0 ? _name : (Control)_instructions).Focus();
                _instructions.Select(0, 0);
            };
            FormClosing += OnFormClosing;

            _initialText = Snapshot();
        }

        /// <summary>The routine as saved, once Save succeeds.</summary>
        public Routine? Saved { get; private set; }

        private static void AddRow(TableLayoutPanel table, string label, Control control)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 8, 0) }, 0, row);
            table.Controls.Add(control, 1, row);
        }

        // ------------------------------------------------------------------ inputs grid

        private DataGridView CreateGrid()
        {
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                EditMode = DataGridViewEditMode.EditOnEnter,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            };

            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Name", Width = 110, ToolTipText = "Letters, digits and _ (e.g. max_grade). Used as {{name}} in the steps." });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "label", HeaderText = "Label", Width = 120, ToolTipText = "What the run dialog shows. Blank uses the name." });
            var type = new DataGridViewComboBoxColumn
            {
                Name = "type",
                HeaderText = "Type",
                Width = 115,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
                FlatStyle = FlatStyle.Flat,
                ToolTipText = "Point and Objects are picked in the drawing when Claude asks; the rest are asked in the run dialog.",
            };
            type.Items.AddRange(TypeLabels);
            grid.Columns.Add(type);
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "default", HeaderText = "Default", Width = 80, ToolTipText = "Starting value in the run dialog (yes or no for Yes/No)." });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "options", HeaderText = "Choices", Width = 140, ToolTipText = "For Choice inputs: the options, separated by commas." });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "unit", HeaderText = "Unit", Width = 50, ToolTipText = "Shown after the value, e.g. % or m." });
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "required", HeaderText = "Required", Width = 60 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "help", HeaderText = "Help", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 120, ToolTipText = "A hint shown in the run dialog." });

            // Commit combo and tick-box changes straight away, not when the cell is left.
            grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewComboBoxCell or DataGridViewCheckBoxCell)
                {
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                }
            };
            grid.DataError += (s, e) => e.ThrowException = false;
            return grid;
        }

        private void AddInputRow(RoutineInput input)
        {
            int index = _inputs.Rows.Add(
                input.Name,
                input.Label ?? "",
                TypeLabels[Array.IndexOf(Types, input.Type)],
                input.Default ?? "",
                string.Join(", ", input.Options),
                input.Unit ?? "",
                input.Required,
                input.Help ?? "");
            _inputs.Rows[index].Tag = input; // keeps keys this version doesn't edit
        }

        private void AddInput()
        {
            _inputs.EndEdit();
            AddInputRow(new RoutineInput { Name = "", Type = RoutineInputType.Text });
            int row = _inputs.Rows.Count - 1;
            _inputs.CurrentCell = _inputs.Rows[row].Cells["name"];
            _inputs.Focus();
            _inputs.BeginEdit(true);
        }

        private void RemoveInput()
        {
            if (_inputs.CurrentCell == null)
            {
                return;
            }

            _inputs.EndEdit();
            _inputs.Rows.RemoveAt(_inputs.CurrentCell.RowIndex);
        }

        private void MoveInput(int delta)
        {
            if (_inputs.CurrentCell == null)
            {
                return;
            }

            _inputs.EndEdit();
            int from = _inputs.CurrentCell.RowIndex;
            int column = _inputs.CurrentCell.ColumnIndex;
            int to = from + delta;
            if (to < 0 || to >= _inputs.Rows.Count)
            {
                return;
            }

            DataGridViewRow row = _inputs.Rows[from];
            _inputs.Rows.RemoveAt(from);
            _inputs.Rows.Insert(to, row);
            _inputs.CurrentCell = _inputs.Rows[to].Cells[column];
        }

        private void InsertPlaceholder()
        {
            _inputs.EndEdit();
            string name = _inputs.CurrentCell == null ? "" : Cell(_inputs.Rows[_inputs.CurrentCell.RowIndex], "name");
            if (!RoutineInput.IsValidName(name))
            {
                ShowProblems(new[] { "Select an input with a name first (letters, digits and _, e.g. max_grade)." });
                return;
            }

            _instructions.SelectedText = "{{" + name + "}}";
            _instructions.Focus();
        }

        private static string Cell(DataGridViewRow row, string column) => (row.Cells[column].Value as string ?? "").Trim();

        // ------------------------------------------------------------------ read, save, close

        private RoutineLocation SelectedLocation => _location.SelectedIndex == 1 ? RoutineLocation.Office : RoutineLocation.Personal;

        private Routine ReadForm()
        {
            _inputs.EndEdit();
            Routine r = _source.Clone();
            r.Name = _name.Text.Trim();
            r.Description = string.IsNullOrWhiteSpace(_description.Text) ? null : _description.Text.Trim();
            r.Effort = _effort.SelectedIndex switch
            {
                1 => EffortMode.Quick,
                2 => EffortMode.Normal,
                3 => EffortMode.Thorough,
                _ => (EffortMode?)null,
            };
            r.CsdEdit = _csdEdit.Checked;
            r.Instructions = _instructions.Text.Replace("\r\n", "\n").Trim();
            r.Inputs = new List<RoutineInput>();
            foreach (DataGridViewRow row in _inputs.Rows)
            {
                RoutineInput input = (row.Tag as RoutineInput)?.Clone() ?? new RoutineInput();
                input.Name = Cell(row, "name");
                input.Label = Blank(Cell(row, "label"));
                int type = Array.IndexOf(TypeLabels, row.Cells["type"].Value as string);
                input.Type = type >= 0 ? Types[type] : RoutineInputType.Text;
                input.Default = Blank(Cell(row, "default"));
                input.Options = Cell(row, "options").Split(',').Select(o => o.Trim()).Where(o => o.Length > 0).ToList();
                input.Unit = Blank(Cell(row, "unit"));
                input.Required = row.Cells["required"].Value is bool required && required;
                input.Help = Blank(Cell(row, "help"));

                bool empty = input.Name.Length == 0 && input.Label == null && input.Default == null && input.Options.Count == 0
                    && input.Unit == null && input.Help == null;
                if (!empty)
                {
                    r.Inputs.Add(input);
                }
            }

            return r;
        }

        private static string? Blank(string s) => s.Length == 0 ? null : s;

        /// <summary>Everything the user can change, to tell whether anything was.</summary>
        private string Snapshot() => SelectedLocation + "\n" + RoutineFile.Write(ReadForm());

        private void Save()
        {
            Routine routine = ReadForm();
            List<string> problems = routine.Validate();
            if (problems.Count > 0)
            {
                ShowProblems(problems);
                return;
            }

            RoutineLocation location = SelectedLocation;
            try
            {
                // Another routine in that folder already has this name: offer to replace it.
                // (Its file goes to the 'deleted' folder, so it can be recovered.)
                Routine? existing = _library.Find(routine.Name, location);
                if (existing != null && !SameFile(existing, _original))
                {
                    if (MessageBox.Show(this,
                            $"There's already a routine called \"{existing.Name}\" in {Routine.LocationName(location)}. Replace it with this one?",
                            "Save routine", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    {
                        ShowProblems(new[] { "Choose another name, or save it in the other folder." });
                        return;
                    }

                    _library.Delete(existing);
                }

                try
                {
                    Saved = _library.Save(routine, location, _original);
                }
                catch (RoutineConflictException ex)
                {
                    if (MessageBox.Show(this, ex.Message + "\n\nReplace their version with yours?", "Routine changed",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    {
                        return;
                    }

                    Saved = _library.Save(routine, location, _original, overwriteChanges: true);
                }
            }
            catch (ArgumentException ex)
            {
                ShowProblems(new[] { ex.Message });
                return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ShowProblems(new[] { $"Couldn't save to {Routine.LocationName(location)}: {ex.Message}" });
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        private static bool SameFile(Routine a, Routine? b) =>
            b?.FilePath != null && a.FilePath != null && string.Equals(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);

        private void ShowProblems(IEnumerable<string> problems)
        {
            List<string> list = problems.ToList();
            _problems.Text = string.Join("\n", list.Take(6)) + (list.Count > 6 ? $"\n...and {list.Count - 6} more." : "");
            _problems.Height = list.Count == 0 ? 0 : 6 + 17 * Math.Min(7, list.Count);
        }

        private void OnFormClosing(object? sender, FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK || e.CloseReason != CloseReason.UserClosing)
            {
                return;
            }

            bool changed = _isDraft || Snapshot() != _initialText;
            if (!changed)
            {
                return;
            }

            string question = _isDraft ? "Close without saving Claude's routine?" : "Close without saving your changes?";
            if (MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                e.Cancel = true;
                DialogResult = DialogResult.None;
            }
        }
    }
}
