using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;
using Bricscad.ApplicationServices;
using CsdBridge.Core.Layouts;
using CadApp = Bricscad.ApplicationServices.Application;

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// AIVPLAYERS: a grid of the drawing's layers (rows) against its layouts (columns). Each cell
    /// shows whether the layer is shown in that layout's viewports: ● shown in all, ○ frozen in
    /// all, ◐ frozen in some, – the layout has no viewports.
    ///
    /// Select cells (drag, Ctrl/Shift-click; click a layout's name for its column, a layer's name
    /// for its row) and press Show or Hide to thaw or freeze those layers in every viewport of
    /// those layouts (VP freeze, as VPLAYER does). Space or a double-click toggles. No Claude,
    /// no API key: it only reads and changes the drawing.
    /// </summary>
    internal sealed class ViewportLayersWindow : Form
    {
        private const int CellWidth = 26;
        private const int MaxHeaderHeight = 220;

        private static ViewportLayersWindow? _instance;

        private static readonly Color ShownColor = Color.FromArgb(34, 150, 70);
        private static readonly Color MixedColor = Color.FromArgb(225, 135, 30);
        private static readonly Color HiddenColor = Color.FromArgb(150, 150, 150);

        private readonly LayerGrid _grid;
        private readonly TextBox _layerFilter;
        private readonly TextBox _layoutFilter;
        private readonly CheckBox _xrefLayers;
        private readonly Button _show;
        private readonly Button _hide;
        private readonly Label _status;
        private readonly ToolTip _tips = new ToolTip();

        private ViewportLayerData? _data;
        private List<int> _rows = new List<int>();    // grid row -> layer index
        private List<int> _cols = new List<int>();    // grid column - 1 -> layout index
        private int _lastHeaderColumn = -1;
        private int _lastNameRow = -1;
        private bool _busy;

        private ViewportLayersWindow()
        {
            Name = "CsdBridgeViewportLayers"; // BricsCAD remembers size and position by this name
            Text = "CSD-Bridge - Viewport layers";
            Font = new Font("Segoe UI", 9f);
            Size = new Size(1000, 640);
            MinimumSize = new Size(520, 320);
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            KeyPreview = true;

            // --- top bar: filters
            _layerFilter = new TextBox { Width = 200 };
            _layoutFilter = new TextBox { Width = 160 };
            _xrefLayers = new CheckBox { Text = "Xref layers", Checked = true, AutoSize = true, Margin = new Padding(10, 6, 4, 0) };
            var refresh = new Button { Text = "Refresh", AutoSize = true };
            _layerFilter.TextChanged += (s, e) => Rebuild();
            _layoutFilter.TextChanged += (s, e) => Rebuild();
            _xrefLayers.CheckedChanged += (s, e) => Rebuild();
            refresh.Click += (s, e) => Reload();
            const string filterTip = "Text matches anywhere in the name; * and ? are wildcards; separate several with commas; ~ in front leaves names out (e.g. sewer, storm, ~*TEXT*)";
            _tips.SetToolTip(_layerFilter, filterTip);
            _tips.SetToolTip(_layoutFilter, filterTip);
            _tips.SetToolTip(_xrefLayers, "Include layers from attached drawings (xref|layer)");
            _tips.SetToolTip(refresh, "Read the drawing again (e.g. after UNDO or adding layouts)");

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 6, 6, 2), WrapContents = false };
            top.Controls.Add(new Label { Text = "Layers:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
            top.Controls.Add(_layerFilter);
            top.Controls.Add(new Label { Text = "Layouts:", AutoSize = true, Margin = new Padding(10, 7, 4, 0) });
            top.Controls.Add(_layoutFilter);
            top.Controls.Add(_xrefLayers);
            top.Controls.Add(refresh);

            // --- the grid
            _grid = new LayerGrid
            {
                Dock = DockStyle.Fill,
                VirtualMode = true,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AllowUserToOrderColumns = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                MultiSelect = true,
                BorderStyle = BorderStyle.None,
                BackgroundColor = SystemColors.Window,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ShowCellToolTips = true,
                StandardTab = true,
            };
            _grid.RowTemplate.Height = 20;
            _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(246, 248, 250);
            _grid.CellValueNeeded += OnCellValueNeeded;
            _grid.CellFormatting += OnCellFormatting;
            _grid.CellPainting += OnCellPainting;
            _grid.CellToolTipTextNeeded += OnCellToolTipTextNeeded;
            _grid.ColumnHeaderMouseClick += OnColumnHeaderMouseClick;
            _grid.CellMouseDown += OnCellMouseDown;
            _grid.CellMouseClick += OnCellMouseClick;
            _grid.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex > 0)
                {
                    Toggle();
                }
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Show in viewports", null, (s, e) => Apply(true));
            menu.Items.Add("Hide in viewports", null, (s, e) => Apply(false));
            menu.Items.Add("Toggle", null, (s, e) => Toggle());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Select all", null, (s, e) => _grid.SelectAll());
            _grid.ContextMenuStrip = menu;

            // --- bottom: actions, legend, status
            _show = new Button { Text = "Show in viewports", AutoSize = true };
            _hide = new Button { Text = "Hide in viewports", AutoSize = true };
            _show.Click += (s, e) => Apply(true);
            _hide.Click += (s, e) => Apply(false);
            _tips.SetToolTip(_show, "Thaw the selected layers in every viewport of the selected layouts");
            _tips.SetToolTip(_hide, "Freeze the selected layers in every viewport of the selected layouts (VP freeze)");
            var legend = new Label
            {
                Text = "●  shown      ○  frozen in all viewports      ◐  frozen in some      –  no viewports      Space / double-click toggles",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(14, 8, 0, 0),
            };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6, 4, 6, 2), WrapContents = false };
            actions.Controls.AddRange(new Control[] { _show, _hide, legend });

            _status = new Label { Dock = DockStyle.Bottom, Height = 22, ForeColor = SystemColors.GrayText, Padding = new Padding(6, 3, 6, 0), AutoEllipsis = true };

            Controls.Add(_grid);
            Controls.Add(top);
            Controls.Add(actions);
            Controls.Add(_status);

            // Follow the drawing that's open, and catch changes made while the window was in the background.
            DocumentCollection docs = CadApp.DocumentManager;
            docs.DocumentActivated += OnDocumentActivated;
            docs.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            Activated += (s, e) => Reload();
            FormClosed += (s, e) =>
            {
                docs.DocumentActivated -= OnDocumentActivated;
                docs.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
                _instance = null;
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
                _instance.Reload();
                return;
            }

            _instance = new ViewportLayersWindow();
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

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Space && (_grid.Focused || _grid.ContainsFocus))
            {
                Toggle();
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ------------------------------------------------------------------ reading the drawing

        private void OnDocumentActivated(object? sender, DocumentCollectionEventArgs e)
        {
            if (Visible)
            {
                Reload();
            }
        }

        private void OnDocumentToBeDestroyed(object? sender, DocumentCollectionEventArgs e)
        {
            if (_data != null && _data.Document == e.Document)
            {
                _data = null;
                Rebuild();
                SetStatus("The drawing was closed.");
            }
        }

        /// <summary>Read the active drawing again. Keeps the selection when the layers and layouts haven't changed.</summary>
        private void Reload()
        {
            if (_busy)
            {
                return;
            }

            if (AgentRunner.IsBusy && _data != null)
            {
                SetStatus("Claude is working on a request; press Refresh when it has finished.");
                return;
            }

            Document? doc = CadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                _data = null;
                Rebuild();
                SetStatus("Open a drawing to see its layouts.");
                return;
            }

            ViewportLayerData data;
            _busy = true;
            try
            {
                data = ViewportLayerData.Read(doc);
            }
            catch (Exception ex)
            {
                SetStatus("Could not read the drawing (is a command running?): " + ex.Message);
                return;
            }
            finally
            {
                _busy = false;
            }

            bool sameShape = data.SameShape(_data);
            _data = data;
            Text = "CSD-Bridge - Viewport layers - " + DrawingName(doc);
            if (sameShape)
            {
                _grid.Invalidate();
                UpdateSummary();
            }
            else
            {
                Rebuild();
            }
        }

        /// <summary>Rebuild rows and columns from the data and the filters.</summary>
        private void Rebuild()
        {
            _grid.SuspendLayout();
            _grid.RowCount = 0;
            _grid.Columns.Clear();
            _rows = new List<int>();
            _cols = new List<int>();
            _lastHeaderColumn = -1;
            _lastNameRow = -1;

            if (_data != null)
            {
                for (int i = 0; i < _data.Layers.Count; i++)
                {
                    GridLayer layer = _data.Layers[i];
                    if ((_xrefLayers.Checked || !layer.Xref) && ViewportLayers.MatchesFilter(layer.Name, _layerFilter.Text))
                    {
                        _rows.Add(i);
                    }
                }

                for (int i = 0; i < _data.Layouts.Count; i++)
                {
                    if (ViewportLayers.MatchesFilter(_data.Layouts[i].Name, _layoutFilter.Text))
                    {
                        _cols.Add(i);
                    }
                }

                var nameColumn = new DataGridViewTextBoxColumn
                {
                    HeaderText = "Layer",
                    Frozen = true,
                    SortMode = DataGridViewColumnSortMode.NotSortable,
                    Width = NameColumnWidth(),
                };
                nameColumn.HeaderCell.Style.Alignment = DataGridViewContentAlignment.BottomLeft;
                _grid.Columns.Add(nameColumn);

                int headerHeight = 40;
                foreach (int c in _cols)
                {
                    string name = _data.Layouts[c].Name;
                    _grid.Columns.Add(new DataGridViewTextBoxColumn
                    {
                        HeaderText = name,
                        ToolTipText = name + (_data.Layouts[c].Viewports.Count == 0
                            ? "\nno viewports"
                            : $"\n{_data.Layouts[c].Viewports.Count} viewport(s). Click to select this layout's column; Ctrl or Shift to add more."),
                        Width = CellWidth,
                        MinimumWidth = CellWidth,
                        Resizable = DataGridViewTriState.False,
                        SortMode = DataGridViewColumnSortMode.NotSortable,
                    });
                    headerHeight = Math.Max(headerHeight, TextRenderer.MeasureText(name, _grid.Font).Width + 16);
                }

                _grid.ColumnHeadersHeight = Math.Min(headerHeight, MaxHeaderHeight);
                _grid.RowCount = _rows.Count;
            }

            _grid.ResumeLayout();
            _grid.ClearSelection();
            UpdateSummary();
        }

        private int NameColumnWidth()
        {
            int widest = TextRenderer.MeasureText("Layer", _grid.Font).Width;
            if (_data != null)
            {
                // Measuring thousands of names is slow; the longest few by length are enough.
                foreach (int r in _rows.OrderByDescending(r => _data.Layers[r].Name.Length).Take(40))
                {
                    widest = Math.Max(widest, TextRenderer.MeasureText(DisplayName(_data.Layers[r]), _grid.Font).Width);
                }
            }

            return Math.Min(Math.Max(widest + 16, 140), 420);
        }

        private void UpdateSummary()
        {
            if (_data == null)
            {
                return;
            }

            int hiddenLayers = _data.Layers.Count - _rows.Count;
            int hiddenLayouts = _data.Layouts.Count - _cols.Count;
            string summary = $"{_rows.Count} layer(s)" + (hiddenLayers > 0 ? $" ({hiddenLayers} filtered out)" : "")
                + $" × {_cols.Count} layout(s)" + (hiddenLayouts > 0 ? $" ({hiddenLayouts} filtered out)" : "")
                + " in " + DrawingName(_data.Document) + ".";
            if (_data.Layouts.Count == 0)
            {
                summary = "This drawing has no paper space layouts.";
            }

            SetStatus(summary);
        }

        // ------------------------------------------------------------------ grid display

        private static string DisplayName(GridLayer layer) =>
            layer.Name + (layer.Frozen ? "   (frozen)" : layer.Off ? "   (off)" : "");

        private void OnCellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
        {
            if (_data != null && e.ColumnIndex == 0 && e.RowIndex >= 0 && e.RowIndex < _rows.Count)
            {
                e.Value = DisplayName(_data.Layers[_rows[e.RowIndex]]);
            }
        }

        private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (_data != null && e.ColumnIndex == 0 && e.RowIndex >= 0 && e.RowIndex < _rows.Count && e.CellStyle != null)
            {
                GridLayer layer = _data.Layers[_rows[e.RowIndex]];
                if (layer.Off || layer.Frozen)
                {
                    e.CellStyle.ForeColor = SystemColors.GrayText;
                }
            }
        }

        private void OnCellToolTipTextNeeded(object? sender, DataGridViewCellToolTipTextNeededEventArgs e)
        {
            if (_data == null || e.RowIndex < 0 || e.RowIndex >= _rows.Count)
            {
                return;
            }

            GridLayer layer = _data.Layers[_rows[e.RowIndex]];
            if (e.ColumnIndex == 0)
            {
                e.ToolTipText = layer.Name
                    + (layer.Frozen ? "\nFrozen in the whole drawing: no viewport shows it until it is thawed (LAYER)." : "")
                    + (!layer.Frozen && layer.Off ? "\nOff in the whole drawing: no viewport shows it until it is turned on (LAYER)." : "")
                    + "\nClick to select this layer's row; Ctrl or Shift to add more.";
            }
            else if (e.ColumnIndex - 1 < _cols.Count)
            {
                int c = _cols[e.ColumnIndex - 1];
                int r = _rows[e.RowIndex];
                e.ToolTipText = ViewportLayers.Describe(layer.Name, _data.Layouts[c].Name, _data.Layouts[c].Viewports.Count, _data.FrozenIn(r, c));
            }
        }

        private void OnCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
        {
            if (_data == null || e.ColumnIndex <= 0 || e.Graphics == null)
            {
                return;
            }

            if (e.RowIndex == -1)
            {
                PaintLayoutHeader(e);
                return;
            }

            if (e.RowIndex >= _rows.Count || e.ColumnIndex - 1 >= _cols.Count)
            {
                return;
            }

            bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
            e.PaintBackground(e.CellBounds, selected);

            ViewportLayerState state = _data.State(_rows[e.RowIndex], _cols[e.ColumnIndex - 1]);
            Graphics g = e.Graphics;
            SmoothingMode smoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            const float d = 11f;
            var circle = new RectangleF(
                e.CellBounds.Left + (e.CellBounds.Width - d) / 2f,
                e.CellBounds.Top + (e.CellBounds.Height - d) / 2f,
                d, d);
            Color ring = selected ? Color.White : Color.Empty;

            switch (state)
            {
                case ViewportLayerState.Shown:
                    using (var fill = new SolidBrush(ShownColor))
                    {
                        g.FillEllipse(fill, circle);
                    }

                    DrawRing(g, circle, ring);
                    break;

                case ViewportLayerState.Hidden:
                    using (var pen = new Pen(selected ? Color.White : HiddenColor, 1.6f))
                    {
                        g.DrawEllipse(pen, circle);
                    }

                    break;

                case ViewportLayerState.Mixed:
                    using (var fill = new SolidBrush(MixedColor))
                    using (var pen = new Pen(MixedColor, 1.6f))
                    {
                        g.FillPie(fill, circle.X, circle.Y, circle.Width, circle.Height, 90, 180);
                        g.DrawEllipse(pen, circle);
                    }

                    DrawRing(g, circle, ring);
                    break;

                default:
                    using (var pen = new Pen(selected ? Color.White : Color.FromArgb(200, 200, 200), 1.4f))
                    {
                        float y = e.CellBounds.Top + e.CellBounds.Height / 2f;
                        g.DrawLine(pen, circle.Left + 2, y, circle.Right - 2, y);
                    }

                    break;
            }

            g.SmoothingMode = smoothing;
            e.Paint(e.CellBounds, DataGridViewPaintParts.Border);
            e.Handled = true;
        }

        private static void DrawRing(Graphics g, RectangleF circle, Color ring)
        {
            if (ring.IsEmpty)
            {
                return;
            }

            using (var pen = new Pen(ring, 1.2f))
            {
                g.DrawEllipse(pen, RectangleF.Inflate(circle, 1.2f, 1.2f));
            }
        }

        /// <summary>Layout names are written upwards so many layouts fit across the window.</summary>
        private void PaintLayoutHeader(DataGridViewCellPaintingEventArgs e)
        {
            e.PaintBackground(e.CellBounds, false);
            Graphics g = e.Graphics!;
            string text = _grid.Columns[e.ColumnIndex].HeaderText;
            bool empty = e.ColumnIndex - 1 < _cols.Count && _data!.Layouts[_cols[e.ColumnIndex - 1]].Viewports.Count == 0;

            GraphicsState saved = g.Save();
            TextRenderingHint hint = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float lineHeight = _grid.Font.GetHeight(g);
            g.TranslateTransform(e.CellBounds.Left + (e.CellBounds.Width - lineHeight) / 2f, e.CellBounds.Bottom - 6);
            g.RotateTransform(-90);
            using (var format = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter })
            using (var brush = new SolidBrush(empty ? SystemColors.GrayText : SystemColors.ControlText))
            {
                g.DrawString(text, _grid.Font, brush, new RectangleF(0, 0, e.CellBounds.Height - 10, lineHeight + 2), format);
            }

            g.TextRenderingHint = hint;
            g.Restore(saved);
            e.Paint(e.CellBounds, DataGridViewPaintParts.Border);
            e.Handled = true;
        }

        // ------------------------------------------------------------------ selecting

        /// <summary>Click a layout's name: select its column. Ctrl adds, Shift selects a range.</summary>
        private void OnColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex <= 0 || e.Button != MouseButtons.Left || _rows.Count == 0)
            {
                return;
            }

            Keys mods = ModifierKeys;
            int from = e.ColumnIndex, to = e.ColumnIndex;
            if ((mods & Keys.Shift) != 0 && _lastHeaderColumn > 0)
            {
                from = Math.Min(_lastHeaderColumn, e.ColumnIndex);
                to = Math.Max(_lastHeaderColumn, e.ColumnIndex);
            }
            else
            {
                _lastHeaderColumn = e.ColumnIndex;
            }

            SelectBlock(0, _rows.Count - 1, from, to, add: (mods & (Keys.Control | Keys.Shift)) != 0);
        }

        private void OnCellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
        {
            // A right-click on an unselected cell works on that cell.
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.ColumnIndex > 0 && !_grid[e.ColumnIndex, e.RowIndex].Selected)
            {
                _grid.ClearSelection();
                _grid[e.ColumnIndex, e.RowIndex].Selected = true;
            }
        }

        /// <summary>Click a layer's name: select its row. Ctrl adds, Shift selects a range.</summary>
        private void OnCellMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex != 0 || e.RowIndex < 0 || e.Button != MouseButtons.Left || _cols.Count == 0)
            {
                return;
            }

            Keys mods = ModifierKeys;
            int from = e.RowIndex, to = e.RowIndex;
            if ((mods & Keys.Shift) != 0 && _lastNameRow >= 0)
            {
                from = Math.Min(_lastNameRow, e.RowIndex);
                to = Math.Max(_lastNameRow, e.RowIndex);
            }
            else
            {
                _lastNameRow = e.RowIndex;
            }

            SelectBlock(from, to, 0, _cols.Count, add: (mods & (Keys.Control | Keys.Shift)) != 0);
        }

        private void SelectBlock(int firstRow, int lastRow, int firstColumn, int lastColumn, bool add)
        {
            Cursor = Cursors.WaitCursor;
            _grid.SuspendLayout();
            try
            {
                if (!add)
                {
                    _grid.ClearSelection();
                }

                for (int r = firstRow; r <= lastRow; r++)
                {
                    for (int c = firstColumn; c <= lastColumn; c++)
                    {
                        _grid[c, r].Selected = true;
                    }
                }
            }
            finally
            {
                _grid.ResumeLayout();
                Cursor = Cursors.Default;
            }
        }

        /// <summary>The selected cells as layout index → layer indices (layer name cells don't count).</summary>
        private Dictionary<int, HashSet<int>> SelectedWork(out int cells)
        {
            var work = new Dictionary<int, HashSet<int>>();
            cells = 0;
            foreach (DataGridViewCell cell in _grid.SelectedCells)
            {
                if (cell.ColumnIndex <= 0 || cell.RowIndex < 0 || cell.RowIndex >= _rows.Count || cell.ColumnIndex - 1 >= _cols.Count)
                {
                    continue;
                }

                int layout = _cols[cell.ColumnIndex - 1];
                if (!work.TryGetValue(layout, out HashSet<int>? layers))
                {
                    work[layout] = layers = new HashSet<int>();
                }

                if (layers.Add(_rows[cell.RowIndex]))
                {
                    cells++;
                }
            }

            return work;
        }

        // ------------------------------------------------------------------ changing the drawing

        /// <summary>Show if the cell the selection started on is hidden or mixed; hide if it is shown.</summary>
        private void Toggle()
        {
            if (_data == null)
            {
                return;
            }

            DataGridViewCell? anchor = _grid.CurrentCell;
            ViewportLayerState anchorState = ViewportLayerState.NoViewports;
            if (anchor != null && anchor.Selected && anchor.ColumnIndex > 0 && anchor.RowIndex >= 0
                && anchor.RowIndex < _rows.Count && anchor.ColumnIndex - 1 < _cols.Count)
            {
                anchorState = _data.State(_rows[anchor.RowIndex], _cols[anchor.ColumnIndex - 1]);
            }

            IEnumerable<ViewportLayerState> others = _grid.SelectedCells.Cast<DataGridViewCell>()
                .Where(c => c.ColumnIndex > 0 && c.RowIndex >= 0 && c.RowIndex < _rows.Count && c.ColumnIndex - 1 < _cols.Count)
                .OrderBy(c => c.RowIndex).ThenBy(c => c.ColumnIndex)
                .Select(c => _data.State(_rows[c.RowIndex], _cols[c.ColumnIndex - 1]));

            bool? show = ViewportLayers.ShowWhenToggling(anchorState, others);
            if (show == null)
            {
                SetStatus("None of the selected layouts have viewports.");
                return;
            }

            Apply(show.Value);
        }

        private void Apply(bool show)
        {
            if (_data == null || _busy)
            {
                return;
            }

            if (AgentRunner.IsBusy)
            {
                SetStatus("Claude is working on a request; wait for it to finish.");
                return;
            }

            if (_data.Document != CadApp.DocumentManager.MdiActiveDocument)
            {
                Reload();
                SetStatus("The open drawing changed. Check the selection and try again.");
                return;
            }

            Dictionary<int, HashSet<int>> work = SelectedWork(out int cells);
            if (cells == 0)
            {
                SetStatus("Select cells first: click a layout's name for its column, a layer's name for its row, or drag across cells.");
                return;
            }

            int layers = work.Values.SelectMany(l => l).Distinct().Count();
            int viewports, layouts;
            _busy = true;
            Cursor = Cursors.WaitCursor;
            try
            {
                viewports = _data.Apply(work, show, out layouts);
            }
            catch (Exception ex)
            {
                SetStatus("Could not change the viewports (is a command running?): " + ex.Message);
                return;
            }
            finally
            {
                _busy = false;
                Cursor = Cursors.Default;
            }

            Reload();
            string verb = show ? "Showed" : "Hid";
            SetStatus(viewports == 0
                ? $"Nothing to change: the {layers} layer(s) were already {(show ? "shown" : "frozen")} in those viewports."
                : $"{verb} {layers} layer(s) in {viewports} viewport(s) on {layouts} layout(s). UNDO takes it back.");
        }

        private void SetStatus(string text) => _status.Text = text;

        private static string DrawingName(Document doc)
        {
            try
            {
                return System.IO.Path.GetFileName(doc.Name);
            }
            catch (ArgumentException)
            {
                return doc.Name;
            }
        }

        /// <summary>A DataGridView that doesn't flicker when scrolled or repainted.</summary>
        private sealed class LayerGrid : DataGridView
        {
            public LayerGrid()
            {
                DoubleBuffered = true;
            }
        }
    }
}
