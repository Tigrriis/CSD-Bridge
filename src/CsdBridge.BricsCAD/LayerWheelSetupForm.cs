using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CsdBridge.Core.Layers;
using CsdBridge.Core.Layouts;

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// AILAYERWHEELSETUP: tick the layers that go on the layer wheel. The list shows the open
    /// drawing's layers plus any already on the wheel that this drawing doesn't have. Save
    /// writes the layer wheel file; "Change file" points this PC at another one, e.g. the
    /// office's copy on a network drive.
    /// </summary>
    internal sealed class LayerWheelSetupForm : Form
    {
        private const int AllAtOnce = 50;

        private readonly LayerWheelSettings _settings;
        private readonly HashSet<string> _inDrawing;
        private readonly List<string> _all;
        private readonly HashSet<string> _chosen;

        private readonly TextBox _filter;
        private readonly CheckBox _onlyTicked;
        private readonly ListView _list;
        private readonly Label _count;
        private readonly Label _file;
        private readonly ToolTip _tips = new ToolTip();
        private bool _filling;

        public LayerWheelSetupForm(LayerWheelSettings settings, IReadOnlyList<string> drawingLayers)
        {
            _settings = settings;
            _inDrawing = new HashSet<string>(drawingLayers, StringComparer.OrdinalIgnoreCase);
            _chosen = new HashSet<string>(settings.Layers, StringComparer.OrdinalIgnoreCase);
            _all = LayerWheelSettings.Clean(drawingLayers.Concat(settings.Layers));
            _all.Sort(StringComparer.OrdinalIgnoreCase);

            Text = "CSD-Bridge - Layer wheel layers";
            Font = new Font("Segoe UI", 9f);
            Size = new Size(560, 640);
            MinimumSize = new Size(440, 360);
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;

            _filter = new TextBox { Width = 220 };
            _onlyTicked = new CheckBox { Text = "Only ticked", AutoSize = true, Margin = new Padding(10, 6, 4, 0) };
            var tickShown = new Button { Text = "Tick all shown", AutoSize = true };
            var untickShown = new Button { Text = "Untick all shown", AutoSize = true };
            _filter.TextChanged += (s, e) => Fill();
            _onlyTicked.CheckedChanged += (s, e) => Fill();
            tickShown.Click += (s, e) => SetShown(true);
            untickShown.Click += (s, e) => SetShown(false);
            _tips.SetToolTip(_filter, "Text matches anywhere in the name; * and ? are wildcards; separate several with commas; ~ in front leaves names out (e.g. AE CIV PLN, ~*EXISTING*)");

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 6, 6, 2), WrapContents = false };
            top.Controls.Add(new Label { Text = "Layers:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
            top.Controls.Add(_filter);
            top.Controls.Add(_onlyTicked);
            top.Controls.Add(tickShown);
            top.Controls.Add(untickShown);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.None,
                HideSelection = false,
            };
            _list.Columns.Add("Layer", 500);
            _list.ItemChecked += OnItemChecked;
            _list.Resize += (s, e) => _list.Columns[0].Width = Math.Max(100, _list.ClientSize.Width - 4);

            _count = new Label { Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(6, 3, 6, 0), AutoEllipsis = true };
            _file = new Label { Dock = DockStyle.Bottom, Height = 22, ForeColor = SystemColors.GrayText, Padding = new Padding(6, 3, 6, 0), AutoEllipsis = true };

            var changeFile = new Button { Text = "Change file...", AutoSize = true };
            var save = new Button { Text = "Save", AutoSize = true };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            changeFile.Click += (s, e) => ChangeFile();
            save.Click += (s, e) => Save();
            _tips.SetToolTip(changeFile, "Use a layer wheel file somewhere else, e.g. the office's copy on a network drive. If the file exists its layers are loaded; if not, Save creates it.");
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6, 4, 6, 6), FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            buttons.Controls.AddRange(new Control[] { cancel, save, changeFile });

            Controls.Add(_list);
            Controls.Add(top);
            Controls.Add(_count);
            Controls.Add(_file);
            Controls.Add(buttons);
            AcceptButton = save;
            CancelButton = cancel;

            Fill();
            ShowFile();
        }

        /// <summary>Where the list was saved.</summary>
        public string SavedPath { get; private set; } = "";

        public int SavedCount { get; private set; }

        private void Fill()
        {
            _filling = true;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                var items = new List<ListViewItem>();
                foreach (string name in _all)
                {
                    bool ticked = _chosen.Contains(name);
                    if ((_onlyTicked.Checked && !ticked) || !ViewportLayers.MatchesFilter(name, _filter.Text))
                    {
                        continue;
                    }

                    bool here = _inDrawing.Contains(name);
                    var item = new ListViewItem(here ? name : name + "   (not in this drawing)") { Tag = name, Checked = ticked };
                    if (!here)
                    {
                        item.ForeColor = SystemColors.GrayText;
                    }

                    items.Add(item);
                }

                _list.Items.AddRange(items.ToArray());
            }
            finally
            {
                _list.EndUpdate();
                _filling = false;
            }

            ShowCount();
        }

        private void OnItemChecked(object? sender, ItemCheckedEventArgs e)
        {
            if (_filling || !(e.Item.Tag is string name))
            {
                return;
            }

            if (e.Item.Checked)
            {
                _chosen.Add(name);
            }
            else
            {
                _chosen.Remove(name);
            }

            ShowCount();
        }

        private void SetShown(bool ticked)
        {
            foreach (ListViewItem item in _list.Items)
            {
                if (item.Tag is string name)
                {
                    if (ticked)
                    {
                        _chosen.Add(name);
                    }
                    else
                    {
                        _chosen.Remove(name);
                    }
                }
            }

            Fill();
        }

        private void ShowCount()
        {
            int n = _chosen.Count;
            _count.Text = $"{n} layer(s) on the wheel." +
                (n > AllAtOnce ? " That is too many to show at once, so the wheel opens on the groups; click one for its layers." : "");
        }

        private void ShowFile()
        {
            string path = AgentRunner.Settings.LayerWheelSavePath();
            _file.Text = "Saves to " + path;
            _tips.SetToolTip(_file, path);
        }

        /// <summary>Point this PC at another layer wheel file; load it if it is already there.</summary>
        private void ChangeFile()
        {
            string current = AgentRunner.Settings.LayerWheelSavePath();
            using (var dialog = new SaveFileDialog
            {
                Title = "Layer wheel file",
                Filter = "Layer wheel file (*.json)|*.json",
                FileName = Path.GetFileName(current),
                InitialDirectory = Path.GetDirectoryName(current),
                OverwritePrompt = false,
                CheckPathExists = true,
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                string path = dialog.FileName;
                try
                {
                    if (File.Exists(path))
                    {
                        LayerWheelSettings found = LayerWheelSettings.Load(path);
                        _settings.Layers = found.Layers;
                        _settings.MaxTerms = found.MaxTerms;
                        _settings.ReleaseToPick = found.ReleaseToPick;
                        _settings.TextSize = found.TextSize;
                        _chosen.Clear();
                        _chosen.UnionWith(found.Layers);
                        foreach (string layer in found.Layers.Where(l => !_all.Contains(l, StringComparer.OrdinalIgnoreCase)))
                        {
                            _all.Add(layer);
                        }

                        _all.Sort(StringComparer.OrdinalIgnoreCase);
                    }

                    AgentRunner.SetLayerWheelFile(path);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not use that file: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                Fill();
                ShowFile();
            }
        }

        private void Save()
        {
            string path = AgentRunner.Settings.LayerWheelSavePath();
            try
            {
                _settings.Layers = LayerWheelSettings.Merge(_settings.Layers, _all.Where(_chosen.Contains));
                _settings.Save(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not save {path}: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SavedPath = path;
            SavedCount = _settings.Layers.Count;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
