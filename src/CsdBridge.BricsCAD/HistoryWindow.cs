using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CsdBridge.Core.Agent;
using CsdBridge.Core.History;
using CadApp = Bricscad.ApplicationServices.Application;

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// A floating window listing every request sent to Claude (all drawings, all days),
    /// with the full detail of the selected one: what was asked, each step Claude took,
    /// its replies, how it ended and the token use. Updates live while a request runs.
    /// Opened with AIHISTORY, the ribbon, or the chat panel's History button.
    /// </summary>
    internal sealed class HistoryWindow : Form
    {
        private const int MaxLoaded = 1000;
        private static HistoryWindow? _instance;

        private readonly TextBox _search;
        private readonly ListView _list;
        private readonly RichTextBox _detail;
        private readonly Button _rerun;
        private readonly Button _edit;
        private readonly Button _copy;
        private readonly Label _summary;
        private readonly List<RunRecord> _records = new List<RunRecord>(); // newest first
        private RunRecord? _shown;

        private HistoryWindow()
        {
            Name = "CsdBridgeHistory"; // BricsCAD remembers size and position by this name
            Text = "CSD-Bridge - Claude history";
            Font = new Font("Segoe UI", 9f);
            Size = new Size(1000, 600);
            MinimumSize = new Size(640, 360);
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;

            // --- top bar: search + actions
            _search = new TextBox { Width = 280 };
            _search.TextChanged += (s, e) => Populate();
            var refresh = new Button { Text = "Refresh", AutoSize = true };
            refresh.Click += (s, e) => Reload();
            var openFolder = new Button { Text = "Open log folder", AutoSize = true };
            openFolder.Click += (s, e) => OpenLogFolder();
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 6, 6, 2), WrapContents = false };
            top.Controls.Add(new Label { Text = "Search:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
            top.Controls.AddRange(new Control[] { _search, refresh, openFolder });

            // --- left: request list
            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                BorderStyle = BorderStyle.None,
            };
            _list.Columns.Add("When", 105);
            _list.Columns.Add("Drawing", 120);
            _list.Columns.Add("Request", 230);
            _list.Columns.Add("Result", 90);
            _list.SelectedIndexChanged += (s, e) => ShowSelected();

            // --- right: detail + per-request actions
            _detail = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Window,
                DetectUrls = false,
            };
            _rerun = new Button { Text = "Run again", AutoSize = true, Enabled = false };
            _edit = new Button { Text = "Edit in chat", AutoSize = true, Enabled = false };
            _copy = new Button { Text = "Copy request", AutoSize = true, Enabled = false };
            _rerun.Click += (s, e) => RunAgain();
            _edit.Click += (s, e) => EditInChat();
            _copy.Click += (s, e) => CopyRequest();
            var tips = new ToolTip();
            tips.SetToolTip(_rerun, "Send this request again, in the drawing that is open now");
            tips.SetToolTip(_edit, "Put this request in the chat panel to change it before sending");
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 4, 0, 4) };
            actions.Controls.AddRange(new Control[] { _rerun, _edit, _copy });
            var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 4, 0) };
            right.Controls.Add(_detail);
            right.Controls.Add(actions);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
            split.Panel1.Controls.Add(_list);
            split.Panel2.Controls.Add(right);

            _summary = new Label { Dock = DockStyle.Bottom, Height = 22, ForeColor = SystemColors.GrayText, Padding = new Padding(6, 3, 6, 0) };

            Controls.Add(split);
            Controls.Add(top);
            Controls.Add(_summary);

            Load += (s, e) => split.SplitterDistance = Math.Max(300, (int)(ClientSize.Width * 0.5));
            AgentRunner.RunUpdated += OnRunUpdated;
            AgentRunner.BusyChanged += OnBusyChanged;
            FormClosed += (s, e) =>
            {
                AgentRunner.RunUpdated -= OnRunUpdated;
                AgentRunner.BusyChanged -= OnBusyChanged;
            };

            Reload();
        }

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

                _instance.Activate();
                return;
            }

            _instance = new HistoryWindow();
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

        // ------------------------------------------------------------------ data

        private void Reload()
        {
            _records.Clear();
            try
            {
                _records.AddRange(AgentRunner.History.Load(MaxLoaded));
            }
            catch (Exception ex)
            {
                _summary.Text = "Could not read the history: " + ex.Message;
            }

            // A request still running is not saved yet; show it at the top.
            if (AgentRunner.CurrentRun is { } running && _records.All(r => r.Id != running.Id))
            {
                _records.Insert(0, running);
            }

            Populate();
        }

        private void Populate()
        {
            string? keepId = _shown?.Id;
            string query = _search.Text;

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (RunRecord r in _records.Where(r => r.Matches(query)))
            {
                _list.Items.Add(CreateItem(r));
            }

            _list.EndUpdate();

            ListViewItem? select = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag as string == keepId)
                ?? (_list.Items.Count > 0 ? _list.Items[0] : null);
            if (select != null)
            {
                select.Selected = true;
                select.EnsureVisible();
            }
            else
            {
                ShowDetail(null);
            }

            UpdateSummary();
        }

        private void OnRunUpdated(RunRecord record)
        {
            if (IsDisposed)
            {
                return;
            }

            int index = _records.FindIndex(r => r.Id == record.Id);
            bool isNew = index < 0;
            if (isNew)
            {
                _records.Insert(0, record);
            }
            else
            {
                _records[index] = record;
            }

            if (!record.Matches(_search.Text))
            {
                return;
            }

            ListViewItem? item = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag as string == record.Id);
            if (item == null)
            {
                item = CreateItem(record);
                _list.Items.Insert(0, item);
            }
            else
            {
                ListViewItem fresh = CreateItem(record);
                for (int i = 0; i < fresh.SubItems.Count; i++)
                {
                    item.SubItems[i].Text = fresh.SubItems[i].Text;
                }

                item.ForeColor = fresh.ForeColor;
            }

            if (isNew)
            {
                // Follow a new request live.
                _list.SelectedItems.Clear();
                item.Selected = true;
                item.EnsureVisible();
            }
            else if (_shown?.Id == record.Id)
            {
                ShowDetail(record);
            }

            UpdateSummary();
        }

        private void OnBusyChanged(bool busy)
        {
            if (!IsDisposed)
            {
                UpdateButtons();
            }
        }

        // ------------------------------------------------------------------ list

        private static ListViewItem CreateItem(RunRecord r)
        {
            var item = new ListViewItem(When(r.StartedAt)) { Tag = r.Id };
            item.SubItems.Add(r.Drawing ?? "");
            item.SubItems.Add(FirstLine(r.Prompt));
            item.SubItems.Add(ResultText(r));
            item.ForeColor = r.IsRunning ? Color.RoyalBlue
                : r.Status == nameof(AgentRunStatus.Completed) ? SystemColors.WindowText
                : r.Status == nameof(AgentRunStatus.Cancelled) ? SystemColors.GrayText
                : Color.Firebrick;
            item.ToolTipText = r.Prompt;
            return item;
        }

        private void ShowSelected()
        {
            if (_list.SelectedItems.Count == 0)
            {
                return; // keep the current detail while the selection is changing
            }

            string? id = _list.SelectedItems[0].Tag as string;
            ShowDetail(_records.FirstOrDefault(r => r.Id == id));
        }

        // ------------------------------------------------------------------ detail

        private void ShowDetail(RunRecord? r)
        {
            _shown = r;
            UpdateButtons();
            _detail.Clear();
            if (r == null)
            {
                Append("No requests yet. Ask Claude something with AIDRAFT or the chat panel.", SystemColors.GrayText);
                return;
            }

            DateTimeOffset local = r.StartedAt.ToLocalTime();
            Append(local.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture) + (r.Drawing == null ? "" : "  -  " + r.Drawing), SystemColors.GrayText);
            if (r.SelectedCount > 0)
            {
                Append($"{r.SelectedCount} object(s) were selected", SystemColors.GrayText);
            }

            Append("");
            Append("You:", SystemColors.WindowText, bold: true);
            Append(r.Prompt);
            Append("");

            foreach (RunStep step in r.Steps)
            {
                switch (step.Kind)
                {
                    case nameof(AgentEventKind.AssistantText):
                        Append("Claude:", SystemColors.WindowText, bold: true);
                        Append(step.Text);
                        Append("");
                        break;
                    case nameof(AgentEventKind.ToolStarted):
                        Append("  > " + step.Text, SystemColors.GrayText);
                        break;
                    case nameof(AgentEventKind.ToolFinished):
                        if (step.IsError == true)
                        {
                            Append("  ! " + step.Text, Color.Firebrick);
                        }
                        else if (!string.IsNullOrEmpty(step.Text))
                        {
                            Append("      " + step.Text, SystemColors.GrayText);
                        }

                        break;
                    default:
                        Append("  " + step.Text, SystemColors.GrayText);
                        break;
                }
            }

            if (r.IsRunning)
            {
                Append("");
                Append("Working... (Esc or Stop in the chat panel to cancel)", Color.RoyalBlue);
                return;
            }

            bool replyShown = r.Steps.Any(s => s.Kind == nameof(AgentEventKind.AssistantText));
            if (!replyShown && !string.IsNullOrWhiteSpace(r.FinalText))
            {
                Append("Claude:", SystemColors.WindowText, bold: true);
                Append(r.FinalText!);
            }

            if (r.Error != null)
            {
                Append("");
                Append(r.Error, Color.Firebrick);
            }

            Append("");
            string took = r.FinishedAt is { } end ? $" in {Seconds(end - r.StartedAt)}" : "";
            Append($"{ResultText(r)}{took}  -  {r.ToolCalls} tool call(s)  -  {r.UsageSummary()}{(r.Model == null ? "" : "  -  " + r.Model)}", SystemColors.GrayText);
            _detail.SelectionStart = 0;
            _detail.ScrollToCaret();
        }

        private void Append(string text, Color? color = null, bool bold = false)
        {
            _detail.SelectionStart = _detail.TextLength;
            _detail.SelectionLength = 0;
            _detail.SelectionColor = color ?? SystemColors.WindowText;
            _detail.SelectionFont = new Font(Font, bold ? FontStyle.Bold : FontStyle.Regular);
            _detail.AppendText(text + "\n");
        }

        private void UpdateButtons()
        {
            bool has = _shown != null;
            _copy.Enabled = has;
            _edit.Enabled = has;
            _rerun.Enabled = has && !AgentRunner.IsBusy;
        }

        private void UpdateSummary()
        {
            DateTime today = DateTime.Now.Date;
            var todays = _records.Where(r => r.StartedAt.ToLocalTime().Date == today).ToList();
            long tokens = todays.Sum(r => r.InputTokens + r.CacheReadTokens + r.CacheWriteTokens + r.OutputTokens);
            string shown = _list.Items.Count == _records.Count ? $"{_records.Count} request(s)" : $"{_list.Items.Count} of {_records.Count} request(s)";
            _summary.Text = $"{shown}  -  today: {todays.Count} request(s), {tokens:N0} tokens  -  {AgentRunner.History.Folder}";
        }

        // ------------------------------------------------------------------ actions

        private void RunAgain()
        {
            if (_shown == null)
            {
                return;
            }

            if (!AgentRunner.QueueRun(_shown.Prompt))
            {
                MessageBox.Show(this, "Open a drawing first, or wait for the current request to finish.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void EditInChat()
        {
            if (_shown == null)
            {
                return;
            }

            ChatPalette.Show();
            ChatPalette.Control?.SetInput(_shown.Prompt);
        }

        private void CopyRequest()
        {
            if (_shown == null)
            {
                return;
            }

            try
            {
                Clipboard.SetText(_shown.Prompt);
            }
            catch (Exception)
            {
                // Clipboard can be briefly locked by another app; not worth an error.
            }
        }

        private void OpenLogFolder()
        {
            try
            {
                Directory.CreateDirectory(AgentRunner.History.Folder);
                Process.Start(new ProcessStartInfo(AgentRunner.History.Folder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ------------------------------------------------------------------ formatting

        private static string When(DateTimeOffset at)
        {
            DateTime t = at.ToLocalTime().DateTime;
            DateTime today = DateTime.Now.Date;
            if (t.Date == today)
            {
                return "Today " + t.ToString("HH:mm", CultureInfo.CurrentCulture);
            }

            if (t.Date == today.AddDays(-1))
            {
                return "Yesterday " + t.ToString("HH:mm", CultureInfo.CurrentCulture);
            }

            return t.Year == today.Year
                ? t.ToString("d MMM HH:mm", CultureInfo.CurrentCulture)
                : t.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        }

        private static string ResultText(RunRecord r)
        {
            switch (r.Status)
            {
                case RunRecord.StatusRunning: return "Running...";
                case nameof(AgentRunStatus.Completed): return "Done";
                case nameof(AgentRunStatus.TurnLimitReached): return "Stopped (turn limit)";
                default: return r.Status;
            }
        }

        private static string FirstLine(string text)
        {
            string line = (text ?? "").Replace("\r", "").Split('\n')[0].Trim();
            return line.Length > 140 ? line.Substring(0, 140) + "..." : line;
        }

        private static string Seconds(TimeSpan span) =>
            span.TotalSeconds < 60 ? $"{span.TotalSeconds:0}s" : $"{(int)span.TotalMinutes}m {span.Seconds}s";
    }
}
