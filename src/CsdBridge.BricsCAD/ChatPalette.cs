using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Bricscad.ApplicationServices;
using CsdBridge.Core.Agent;
using CadApp = Bricscad.ApplicationServices.Application;
using DockSides = Bricscad.Windows.DockSides;
using PaletteSet = Bricscad.Windows.PaletteSet;
using PaletteSetStyles = Bricscad.Windows.PaletteSetStyles;

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// The dockable chat panel. Sending a message does not call Claude directly: it
    /// queues the prompt and starts a command, so the request runs in command context
    /// like AIDRAFT (document locked, one UNDO step, pick/select prompts work).
    /// </summary>
    internal static class ChatPalette
    {
        private static readonly Guid PaletteId = new Guid("4f0d6f2a-6c1e-4f43-9a52-2b8b3f1d7c11");
        private static PaletteSet? _set;

        /// <summary>The panel's control, or null if the panel was never opened.</summary>
        public static ChatControl? Control { get; private set; }

        public static void Show()
        {
            if (_set == null)
            {
                Control = new ChatControl();
                _set = new PaletteSet("CSD-Bridge", PaletteId)
                {
                    Style = PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowAutoHideButton,
                    MinimumSize = new Size(280, 320),
                    DockEnabled = DockSides.Left | DockSides.Right,
                };
                _set.Add("Claude", Control);
                _set.Size = new Size(380, 640);
            }

            _set.Visible = true;
        }

        public static void OnDocumentActivated(Document? doc)
        {
            if (doc != null && Control != null && !AgentRunner.IsBusy)
            {
                Control.AppendInfo($"-- {Path.GetFileName(doc.Name)} (conversations are per drawing) --");
            }
        }
    }

    internal sealed class ChatControl : UserControl
    {
        private readonly RichTextBox _log;
        private readonly TextBox _input;
        private readonly Button _send;
        private readonly Button _stop;
        private readonly Button _new;
        private readonly Button _history;
        private readonly ComboBox _model;
        private readonly Button _routines;
        private readonly ToolStripMenuItem _saveRoutine;
        private readonly ComboBox _effort;
        private readonly Label _status;
        private bool _updatingEffort;
        private bool _updatingModel;

        public ChatControl()
        {
            Font = new Font("Segoe UI", 9f);

            _log = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Window,
                DetectUrls = false,
            };

            _input = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
            _input.KeyDown += OnInputKeyDown;

            _send = new Button { Text = "Send", AutoSize = true };
            _stop = new Button { Text = "Stop", AutoSize = true, Enabled = false };
            _new = new Button { Text = "New chat", AutoSize = true };
            _history = new Button { Text = "History", AutoSize = true };
            _send.Click += (s, e) => Send();
            _stop.Click += (s, e) => AgentRunner.Cancel();
            _new.Click += (s, e) => NewChat();
            _history.Click += (s, e) => HistoryWindow.ShowWindow();

            // Routines: save the job done in this chat as a routine, or open the list.
            _routines = new Button { Text = "Routines \u25BE", AutoSize = true };
            var routinesMenu = new ContextMenuStrip();
            _saveRoutine = new ToolStripMenuItem("Save this chat as a routine...", null, (s, e) => SaveAsRoutine())
            {
                ToolTipText = "Claude writes up what you did in this chat as a routine; you check it in the editor before it's saved",
            };
            routinesMenu.Items.Add(_saveRoutine);
            routinesMenu.Items.Add(new ToolStripMenuItem("Open routines...", null, (s, e) => Routines.RoutinesWindow.ShowWindow()));
            routinesMenu.Opening += (s, e) => _saveRoutine.Enabled = !AgentRunner.IsBusy;
            _routines.Click += (s, e) => routinesMenu.Show(_routines, new Point(0, _routines.Height));

            _model = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 4, 12, 3) };
            _model.Items.AddRange(ModelCatalog.Choices.Cast<object>().ToArray());
            ShowModel(AgentRunner.Model);
            _model.SelectedIndexChanged += OnModelPicked;
            new ToolTip().SetToolTip(_model,
                "Which Claude model answers.\n" +
                string.Join("\n", ModelCatalog.Choices.Select(c => $"{c.Label}: {c.Description}.")) + "\n" +
                "Applies from your next message; the conversation carries on. Press New chat for a clean comparison.");
            var modelLabel = new Label { Text = "Model", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };

            _effort = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 4, 3, 3) };
            _effort.Items.AddRange(new object[] { "Auto", "Quick", "Normal", "Thorough" });
            _effort.SelectedItem = AgentRunner.EffortMode.ToString();
            _effort.SelectedIndexChanged += OnEffortChanged;
            new ToolTip().SetToolTip(_effort,
                "How hard Claude works on each request.\n" +
                "Auto: Quick for simple edits and questions, Thorough for big multi-step jobs, Normal otherwise.\n" +
                "Quick is fastest and cheapest; Thorough is slowest and most careful.");
            var effortLabel = new Label { Text = "Effort", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            FitWidth(_model);
            FitWidth(_effort);

            var choices = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
            choices.Controls.AddRange(new Control[] { modelLabel, _model, effortLabel, _effort });

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            buttons.Controls.AddRange(new Control[] { _send, _stop, _new, _history, _routines });

            // Grows to fit its text (see FitStatus), so the whole result and token use can be read.
            _status = new Label
            {
                Dock = DockStyle.Bottom,
                AutoSize = false,
                Padding = new Padding(2, 2, 2, 4),
                ForeColor = SystemColors.GrayText,
                Text = "Enter to send, Shift+Enter for a new line.",
            };
            _status.TextChanged += (s, e) => FitStatus();

            var inputPanel = new Panel { Dock = DockStyle.Bottom, Height = 84, Padding = new Padding(0, 4, 0, 0) };
            inputPanel.Controls.Add(_input);

            // Docked from the bottom up: status, buttons, model/effort, input; the log fills the rest.
            Controls.Add(_log);
            Controls.Add(inputPanel);
            Controls.Add(choices);
            Controls.Add(buttons);
            Controls.Add(_status);
            FitStatus();

            AgentRunner.BusyChanged += OnBusyChanged;
            AgentRunner.EffortModeChanged += OnEffortModeChanged;
            AgentRunner.ModelChanged += OnModelChanged;
            AppendInfo("Ask Claude to draw or change something in the current drawing. Select objects first to give it context.");
        }

        public void AppendUser(string text) => Append("\nYou: ", text, SystemColors.WindowText, bold: true);

        public void AppendAssistant(string text) => Append("Claude: ", text, SystemColors.WindowText, bold: false);

        public void AppendTool(string text) => Append("  > ", text, SystemColors.GrayText, bold: false);

        public void AppendError(string text) => Append("  ! ", text, Color.Firebrick, bold: false);

        public void AppendInfo(string text) => Append("", text, SystemColors.GrayText, bold: false);

        public void SetStatus(string text) => _status.Text = text;

        public void ClearLog() => _log.Clear();

        private void Send()
        {
            string text = _input.Text.Trim();
            if (text.Length == 0 || AgentRunner.IsBusy)
            {
                return;
            }

            if (CadApp.DocumentManager.MdiActiveDocument == null)
            {
                AppendError("Open a drawing first.");
                return;
            }

            // Runs in command context (see class comment); the run echoes the prompt here.
            if (AgentRunner.QueueRun(text))
            {
                _input.Clear();
            }
        }

        /// <summary>Put text in the input box for editing (used by "Edit in chat" in History).</summary>
        public void SetInput(string text)
        {
            _input.Text = text;
            _input.SelectionStart = _input.TextLength;
            _input.Focus();
        }

        private void SaveAsRoutine()
        {
            string? problem = AgentRunner.QueueSaveChatAsRoutine();
            if (problem != null)
            {
                AppendError(problem);
            }
        }

        private void NewChat()
        {
            Document? doc = CadApp.DocumentManager.MdiActiveDocument;
            if (doc == null || AgentRunner.IsBusy)
            {
                return;
            }

            AgentRunner.Forget(doc);
            AgentRunner.ReloadSettings();
            ClearLog();
            AppendInfo("New conversation for " + Path.GetFileName(doc.Name) + ".");
        }

        private void OnInputKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                Send();
            }
        }

        private void OnEffortChanged(object? sender, EventArgs e)
        {
            if (!_updatingEffort && EffortPolicy.TryParseMode(_effort.SelectedItem as string, out EffortMode mode))
            {
                AgentRunner.SetEffortMode(mode);
            }
        }

        private void OnEffortModeChanged(EffortMode mode)
        {
            _updatingEffort = true;
            _effort.SelectedItem = mode.ToString();
            _updatingEffort = false;
        }

        private void OnModelPicked(object? sender, EventArgs e)
        {
            if (!_updatingModel && _model.SelectedItem is ModelChoice choice && choice.Id != AgentRunner.Model)
            {
                AgentRunner.SetModel(choice.Id);
                SetStatus($"{choice.Label} from your next message. New chat starts a clean conversation.");
            }
        }

        private void OnModelChanged(string model) => ShowModel(model);

        /// <summary>Select the model in the list, adding it if settings.json names one that isn't listed.</summary>
        private void ShowModel(string model)
        {
            _updatingModel = true;
            try
            {
                ModelChoice? choice = _model.Items.OfType<ModelChoice>()
                    .FirstOrDefault(c => string.Equals(c.Id, model, StringComparison.OrdinalIgnoreCase));
                if (choice == null)
                {
                    choice = new ModelChoice(model, model, "set in settings.json");
                    _model.Items.Add(choice);
                    FitWidth(_model);
                }

                _model.SelectedItem = choice;
            }
            finally
            {
                _updatingModel = false;
            }
        }

        /// <summary>Make a drop-down wide enough for its longest item at the current font and DPI.</summary>
        private void FitWidth(ComboBox box)
        {
            int text = box.Items.Cast<object>().Select(i => TextRenderer.MeasureText(i.ToString(), Font).Width).DefaultIfEmpty(40).Max();
            box.Width = text + SystemInformation.VerticalScrollBarWidth + 10;
        }

        /// <summary>
        /// Size the status line to its text at the panel's width: at least two lines so the
        /// layout doesn't jump, at most eight (long errors are in the log above as well).
        /// </summary>
        private void FitStatus()
        {
            if (_status == null || ClientSize.Width <= 0)
            {
                return;
            }

            int lineHeight = _status.Font.Height;
            int needed = _status.GetPreferredSize(new Size(ClientSize.Width, int.MaxValue)).Height;
            int min = 2 * lineHeight + _status.Padding.Vertical;
            int max = 8 * lineHeight + _status.Padding.Vertical;
            int height = Math.Min(max, Math.Max(min, needed));
            if (_status.Height != height)
            {
                _status.Height = height;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            FitStatus();
        }

        private void OnBusyChanged(bool busy)
        {
            _send.Enabled = !busy;
            _new.Enabled = !busy;
            _stop.Enabled = busy;
        }

        private void Append(string prefix, string text, Color color, bool bold)
        {
            _log.SelectionStart = _log.TextLength;
            _log.SelectionLength = 0;
            _log.SelectionColor = color;
            _log.SelectionFont = new Font(Font, bold ? FontStyle.Bold : FontStyle.Regular);
            _log.AppendText(prefix + text + "\n");
            _log.ScrollToCaret();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                AgentRunner.BusyChanged -= OnBusyChanged;
                AgentRunner.EffortModeChanged -= OnEffortModeChanged;
                AgentRunner.ModelChanged -= OnModelChanged;
            }

            base.Dispose(disposing);
        }
    }
}
