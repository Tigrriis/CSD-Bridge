using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Bricscad.ApplicationServices;
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
        private readonly Label _status;

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

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            buttons.Controls.AddRange(new Control[] { _send, _stop, _new, _history });

            _status = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = 34, ForeColor = SystemColors.GrayText, Text = "Enter to send, Shift+Enter for a new line." };

            var inputPanel = new Panel { Dock = DockStyle.Bottom, Height = 84, Padding = new Padding(0, 4, 0, 0) };
            inputPanel.Controls.Add(_input);

            Controls.Add(_log);
            Controls.Add(inputPanel);
            Controls.Add(buttons);
            Controls.Add(_status);

            AgentRunner.BusyChanged += OnBusyChanged;
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
            }

            base.Dispose(disposing);
        }
    }
}
