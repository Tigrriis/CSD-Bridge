using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CsdBridge.Core.Routines;

namespace CsdBridge.BricsCAD.Routines
{
    /// <summary>
    /// Asks for a routine's inputs before it runs: a field for each text, number, choice and
    /// yes/no input (starting from the values last used), a note for each point or selection
    /// Claude will ask for, and whether to start a new chat.
    /// </summary>
    internal sealed class RoutineRunForm : Form
    {
        private readonly Routine _routine;
        private readonly Dictionary<string, Control> _fields = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase);
        private readonly CheckBox _newChat;
        private readonly Label _problems;

        public RoutineRunForm(Routine routine, IReadOnlyDictionary<string, string> remembered, string csdMode)
        {
            _routine = routine;
            Text = "Run routine - " + routine.Name;
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            MinimumSize = new Size(460, 260);

            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(12, 10, 12, 6) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            AddSpanning(table, new Label { Text = routine.Name, AutoSize = true, Font = new Font(Font.FontFamily, 11f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) });
            if (!string.IsNullOrWhiteSpace(routine.Description))
            {
                AddSpanning(table, Wrapped(routine.Description!, SystemColors.GrayText));
            }

            if (routine.LoadProblem != null)
            {
                AddSpanning(table, Wrapped("The routine's file has problems: " + routine.LoadProblem, Color.Firebrick));
            }

            var tips = new ToolTip();
            List<RoutineInput> typed = routine.Inputs.Where(i => i.IsTyped).ToList();

            foreach (RoutineInput input in typed)
            {
                string? start = remembered.TryGetValue(input.Name, out string? last) ? last : input.Default;
                Control field = CreateField(input, start);
                _fields[input.Name] = field;

                int row = table.RowCount++;
                table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var label = new Label { Text = input.DisplayLabel + (input.Required ? " *" : ""), AutoSize = true, Margin = new Padding(0, 7, 10, 0) };
                table.Controls.Add(label, 0, row);
                table.Controls.Add(field, 1, row);
                table.Controls.Add(new Label { Text = input.Unit ?? "", AutoSize = true, Margin = new Padding(4, 7, 0, 0), ForeColor = SystemColors.GrayText }, 2, row);
                if (!string.IsNullOrWhiteSpace(input.Help))
                {
                    tips.SetToolTip(label, input.Help);
                    tips.SetToolTip(field, input.Help);
                    int helpRow = table.RowCount++;
                    table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    var help = Wrapped(input.Help!, SystemColors.GrayText);
                    help.Margin = new Padding(3, 0, 0, 4);
                    table.Controls.Add(help, 1, helpRow);
                    table.SetColumnSpan(help, 2);
                }
            }

            List<RoutineInput> picks = routine.Inputs.Where(i => !i.IsTyped).ToList();
            if (picks.Count > 0)
            {
                AddSpanning(table, new Label { Text = "In the drawing", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 10, 0, 2) });
                foreach (RoutineInput pick in picks)
                {
                    string how = pick.Type == RoutineInputType.Point
                        ? "Claude asks you to pick it."
                        : "select them before you run, or Claude asks you to.";
                    AddSpanning(table, Wrapped($"• {pick.DisplayLabel}: {how}" + (string.IsNullOrWhiteSpace(pick.Help) ? "" : " " + pick.Help), SystemColors.WindowText));
                }
            }

            if (typed.Count == 0 && picks.Count == 0)
            {
                AddSpanning(table, Wrapped("This routine has no inputs: it runs as it is.", SystemColors.GrayText));
            }

            if (routine.CsdEdit && !string.Equals(csdMode, "edit", StringComparison.OrdinalIgnoreCase))
            {
                AddSpanning(table, Wrapped(
                    "This routine changes Civil Site Design values, but CSD edits are " + (string.Equals(csdMode, "off", StringComparison.OrdinalIgnoreCase) ? "off" : "read only") +
                    ". Turn them on with AICSD > Edit first, or Claude can only look.",
                    Color.Firebrick));
            }

            _newChat = new CheckBox { Text = "Start a new chat for this routine", AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
            tips.SetToolTip(_newChat, "Best when the drawing's chat is long or about something else. Claude still gets this drawing's notes.");
            AddSpanning(table, _newChat);

            _problems = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = 0, ForeColor = Color.Firebrick, Padding = new Padding(12, 0, 12, 0) };

            var run = new Button { Text = "Run", AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            var reset = new Button { Text = "Reset to defaults", AutoSize = true, Visible = typed.Count > 0 };
            run.Click += (s, e) => TryRun();
            reset.Click += (s, e) => ResetToDefaults();
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 4, 8, 8) };
            buttons.Controls.AddRange(new Control[] { cancel, run, reset });
            reset.Margin = new Padding(3, 3, 40, 3);

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            scroll.Controls.Add(table);
            Controls.Add(scroll);
            Controls.Add(_problems);
            Controls.Add(buttons);
            AcceptButton = run;
            CancelButton = cancel;

            int height = Math.Min(680, table.PreferredSize.Height + 64);
            ClientSize = new Size(520, Math.Max(220, height));
            Shown += (s, e) => _fields.Values.FirstOrDefault()?.Focus();
        }

        /// <summary>The values entered, by input name (yes/no as "yes" or "no").</summary>
        public Dictionary<string, string?> Values { get; private set; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        public bool NewChat => _newChat.Checked;

        private static Control CreateField(RoutineInput input, string? value)
        {
            switch (input.Type)
            {
                case RoutineInputType.YesNo:
                    return new CheckBox { Text = "Yes", AutoSize = true, Checked = RoutineInput.ParseYesNo(value) == true, Margin = new Padding(3, 5, 3, 3) };
                case RoutineInputType.Choice:
                {
                    var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
                    if (!input.Required)
                    {
                        combo.Items.Add("");
                    }

                    combo.Items.AddRange(input.Options.Cast<object>().ToArray());
                    SetChoice(combo, value);
                    return combo;
                }

                default:
                    return new TextBox { Text = value ?? "", Dock = DockStyle.Fill };
            }
        }

        private static void SetChoice(ComboBox combo, string? value)
        {
            int index = value == null ? -1 : combo.Items.Cast<object>().ToList().FindIndex(o => string.Equals(o as string, value.Trim(), StringComparison.OrdinalIgnoreCase));
            combo.SelectedIndex = index >= 0 ? index : (combo.Items.Count > 0 ? 0 : -1);
        }

        private void ResetToDefaults()
        {
            foreach (RoutineInput input in _routine.Inputs.Where(i => i.IsTyped))
            {
                switch (_fields[input.Name])
                {
                    case CheckBox box:
                        box.Checked = RoutineInput.ParseYesNo(input.Default) == true;
                        break;
                    case ComboBox combo:
                        SetChoice(combo, input.Default);
                        break;
                    case TextBox text:
                        text.Text = input.Default ?? "";
                        break;
                }
            }
        }

        private void TryRun()
        {
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Control> field in _fields)
            {
                values[field.Key] = field.Value switch
                {
                    CheckBox box => box.Checked ? "yes" : "no",
                    ComboBox combo => combo.SelectedItem as string,
                    _ => field.Value.Text,
                };
            }

            List<string> problems = _routine.CheckValues(values);
            if (problems.Count > 0)
            {
                _problems.Text = string.Join("\n", problems);
                _problems.Height = 6 + 17 * Math.Min(5, problems.Count);
                return;
            }

            Values = values;
            DialogResult = DialogResult.OK;
            Close();
        }

        private static void AddSpanning(TableLayoutPanel table, Control control)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(control, 0, row);
            table.SetColumnSpan(control, 3);
        }

        private static Label Wrapped(string text, Color color) =>
            new Label { Text = text, AutoSize = true, MaximumSize = new Size(480, 0), ForeColor = color, Margin = new Padding(0, 2, 0, 2) };
    }
}
