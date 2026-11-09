using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using CsdBridge.Core.Vehicles;

namespace CsdBridge.BricsCAD
{
    /// <summary>How the AICLEARANCE command should run, as chosen in the dialog.</summary>
    internal sealed class GroundClearanceChoice
    {
        public enum CheckMode
        {
            Section,
            Surface,
        }

        public DesignVehicle Vehicle { get; set; } = DesignVehicle.B99;
        public CheckMode Mode { get; set; }
        public double VerticalExaggeration { get; set; } = 1;
        public double HorizontalScale { get; set; } = 1;
        public bool BothDirections { get; set; } = true;
        public string? Surface { get; set; }
        public bool Reverse { get; set; }
        public double PoseInterval { get; set; } = 0.1;
        public bool PlotSections { get; set; } = true;
        public double PlotVerticalExaggeration { get; set; } = 1;
        public double GroundClearance { get; set; } = 0.12;
        public double Step { get; set; } = 0.05;
        public bool DrawTemplates { get; set; } = true;
        public bool DrawGroundings { get; set; } = true;
        public bool DrawLabels { get; set; } = true;
        public TurnPathLayers Layers { get; set; } = new TurnPathLayers();
        public double? TextHeight { get; set; }
        public TurnPathConventions Conventions { get; set; } = TurnPathConventions.Default();
    }

    /// <summary>Options for the ground clearance check (AS/NZS 2890.1 Appendix C).</summary>
    internal sealed class GroundClearanceDialog : Form
    {
        private readonly TurnPathConventions _conventions;
        private readonly string _savePath;
        private readonly ComboBox _vehicle;
        private readonly Label _vehicleInfo;
        private readonly RadioButton _modeSection;
        private readonly RadioButton _modeSurface;
        private readonly TextBox _ve;
        private readonly TextBox _hs;
        private readonly CheckBox _both;
        private readonly ComboBox _surface;
        private readonly CheckBox _reverse;
        private readonly TextBox _interval;
        private readonly CheckBox _plot;
        private readonly TextBox _plotVe;
        private readonly TextBox _clearance;
        private readonly TextBox _approach;
        private readonly TextBox _departure;
        private readonly TextBox _step;
        private readonly CheckBox _drawTemplates;
        private readonly CheckBox _drawGroundings;
        private readonly CheckBox _drawLabels;
        private readonly TextBox _textHeight;
        private readonly Dictionary<string, TextBox> _layers = new Dictionary<string, TextBox>();
        private readonly Label _status;
        private string _lastVehicle = "";

        public GroundClearanceDialog(TurnPathConventions conventions, string savePath, IReadOnlyList<string> surfaces)
        {
            _conventions = conventions;
            _savePath = savePath;

            Text = "CSD-Bridge - Ground clearance check (AS/NZS 2890.1 Appendix C)";
            Font = new Font("Segoe UI", 9f);
            AutoScaleDimensions = new SizeF(7f, 15f);
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            // ------------------------------------------------------------ left column
            TableLayoutPanel left = Column();
            TableLayoutPanel vehicleRow = Table(2);
            vehicleRow.Controls.Add(Caption("Design vehicle"), 0, 0);
            _vehicle = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left };
            foreach (string name in conventions.Vehicles.Keys)
            {
                _vehicle.Items.Add(name);
            }

            vehicleRow.Controls.Add(_vehicle, 1, 0);
            left.Controls.Add(vehicleRow);
            _vehicleInfo = Note("", 420);
            left.Controls.Add(_vehicleInfo);

            TableLayoutPanel check = Table(3);
            _modeSection = new RadioButton { Text = "2D: slide the template along a plotted long section you select", AutoSize = true, Checked = true };
            _modeSurface = new RadioButton { Text = "3D: drive along a path and check each side against a surface", AutoSize = true };
            foreach (RadioButton rb in new[] { _modeSection, _modeSurface })
            {
                rb.Margin = new Padding(3, 2, 3, 2);
                check.Controls.Add(rb, 0, check.RowCount);
                check.SetColumnSpan(rb, 3);
                check.RowCount++;
            }

            _ve = Box(55);
            Row(check, Caption("Section vertical exaggeration"), _ve, Hint("1 = natural scale"));
            _hs = Box(55);
            Row(check, Caption("Section horizontal scale"), _hs, Hint("drawing units per metre"));
            _both = new CheckBox { Text = "Check both directions of travel", AutoSize = true };
            Row(check, null, _both, null);
            check.SetColumnSpan(_both, 2);
            _surface = new ComboBox { Width = 230, DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left };
            foreach (string s in surfaces)
            {
                _surface.Items.Add(s);
            }

            if (_surface.Items.Count > 0)
            {
                _surface.SelectedIndex = 0;
            }

            Row(check, Caption("Surface (BricsCAD TIN)"), _surface, null);
            _reverse = new CheckBox { Text = "Reverse along the path", AutoSize = true };
            Row(check, null, _reverse, null);
            check.SetColumnSpan(_reverse, 2);
            _interval = Box(55);
            Row(check, Caption("Check every (m) along the path"), _interval, null);
            _plot = new CheckBox { Text = "Plot each wheel path as a long section (you pick where)", AutoSize = true };
            Row(check, null, _plot, null);
            check.SetColumnSpan(_plot, 2);
            _plotVe = Box(55);
            Row(check, Caption("Plot vertical exaggeration"), _plotVe, null);
            left.Controls.Add(Group("Check", check));

            left.Controls.Add(Note(
                surfaces.Count == 0
                    ? "No BricsCAD TIN surface was found in this drawing. Civil Site Design keeps its own surfaces: make a BricsCAD copy " +
                      "with CSD's Surface Exchange (Surfaces tab > BricsCAD Surfaces) and run this again, or ask Claude to sample the wheel " +
                      "paths with sample_csd_ground and check the result."
                    : "CSD surfaces aren't listed: make a BricsCAD copy with CSD's Surface Exchange first.", 420));

            // ----------------------------------------------------------- right column
            TableLayoutPanel right = Column();
            TableLayoutPanel template = Table(3);
            _clearance = Box(55);
            Row(template, Caption("Ground clearance (m)"), _clearance, Hint("B5: 0.12 fully loaded"));
            _approach = Box(55);
            Row(template, Caption("Approach angle (deg)"), _approach, Hint("B5: 10, ahead of the front axle"));
            _departure = Box(55);
            Row(template, Caption("Departure angle (deg)"), _departure, Hint("B5: 10, behind the rear axle"));
            _step = Box(55);
            Row(template, Caption("Move the template every (m)"), _step, null);
            right.Controls.Add(Group("Template (Figure C1)", template));

            TableLayoutPanel draw = Table(3);
            _drawTemplates = new CheckBox { Text = "Template at its worst position (each direction)", AutoSize = true };
            _drawGroundings = new CheckBox { Text = "Where it grounds", AutoSize = true };
            _drawLabels = new CheckBox { Text = "Labels", AutoSize = true };
            foreach (CheckBox cb in new[] { _drawTemplates, _drawGroundings, _drawLabels })
            {
                draw.Controls.Add(cb, 0, draw.RowCount);
                draw.SetColumnSpan(cb, 3);
                draw.RowCount++;
            }

            _textHeight = Box(55);
            Row(draw, Caption("Label text height"), _textHeight, Hint("blank = drawing default"));
            right.Controls.Add(Group("Draw", draw));

            TableLayoutPanel layers = Table(2);
            foreach (var (part, caption) in new[] { ("clearance_check", "Templates and plots"), ("clearance_fail", "Where it grounds"), ("labels", "Labels") })
            {
                TextBox box = Box(260);
                Row(layers, Caption(caption), box, null);
                _layers[part] = box;
            }

            right.Controls.Add(Group("Layers ({vehicle} is replaced by the vehicle name)", layers));

            // ------------------------------------------------------------------ bottom
            _status = Note("", 440);
            var save = Button("Save as defaults");
            save.Click += (s, e) => SaveDefaults();
            var ok = Button("OK");
            ok.DialogResult = DialogResult.OK;
            var cancel = Button("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Anchor = AnchorStyles.Right | AnchorStyles.Bottom, Margin = new Padding(0) };
            buttons.Controls.AddRange(new Control[] { save, ok, cancel });
            TableLayoutPanel bottom = Table(2);
            bottom.Dock = DockStyle.Fill;
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _status.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            bottom.Controls.Add(_status, 0, 0);
            bottom.Controls.Add(buttons, 1, 0);
            AcceptButton = ok;
            CancelButton = cancel;

            TableLayoutPanel root = Table(2);
            root.Controls.Add(left, 0, 0);
            root.Controls.Add(right, 1, 0);
            root.Controls.Add(bottom, 0, 1);
            root.SetColumnSpan(bottom, 2);
            left.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            right.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            left.Margin = new Padding(0, 0, 16, 0);
            root.Dock = DockStyle.Fill;
            Controls.Add(root);

            _vehicle.SelectedIndexChanged += (s, e) => OnVehicleChanged();
            _modeSection.CheckedChanged += (s, e) => OnModeChanged();
            _modeSurface.CheckedChanged += (s, e) => OnModeChanged();
            FormClosing += (s, e) =>
            {
                if (DialogResult == DialogResult.OK && !TryBuild(out _, out string? problem))
                {
                    MessageBox.Show(this, problem, "Ground clearance", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    e.Cancel = true;
                }
            };

            LoadFrom(conventions);
            if (surfaces.Count == 0)
            {
                _modeSurface.Enabled = false;
            }
        }

        public GroundClearanceChoice Choice
        {
            get
            {
                TryBuild(out GroundClearanceChoice? c, out _);
                return c ?? new GroundClearanceChoice();
            }
        }

        private void LoadFrom(TurnPathConventions c)
        {
            int index = _vehicle.Items.IndexOf(c.DefaultVehicle);
            _vehicle.SelectedIndex = index >= 0 ? index : 0;
            _lastVehicle = "";
            OnVehicleChanged();
            _ve.Text = Num(c.SectionVerticalExaggeration);
            _hs.Text = "1";
            _both.Checked = true;
            _interval.Text = Num(c.ClearancePoseInterval);
            _plot.Checked = true;
            _plotVe.Text = Num(Math.Max(1, c.SectionVerticalExaggeration));
            _step.Text = Num(c.ClearanceStep);
            _drawTemplates.Checked = _drawGroundings.Checked = _drawLabels.Checked = true;
            _textHeight.Text = c.TextHeight is double th ? Num(th) : "";
            foreach (KeyValuePair<string, string> kv in c.Layers.All())
            {
                if (_layers.TryGetValue(kv.Key, out TextBox? box))
                {
                    box.Text = kv.Value;
                }
            }

            OnModeChanged();
            _status.Text = "Defaults from " + _savePath;
        }

        private void OnVehicleChanged()
        {
            if (!(_vehicle.SelectedItem is string name) || !_conventions.Vehicles.TryGetValue(name, out DesignVehicle? v))
            {
                return;
            }

            DesignVehicle? previous = _lastVehicle.Length > 0 && _conventions.Vehicles.TryGetValue(_lastVehicle, out DesignVehicle? p) ? p : null;
            if (previous == null || _clearance.Text == Num(previous.GroundClearance))
            {
                _clearance.Text = Num(v.GroundClearance);
            }

            if (previous == null || _approach.Text == Num(previous.ApproachAngleDeg))
            {
                _approach.Text = Num(v.ApproachAngleDeg);
            }

            if (previous == null || _departure.Text == Num(previous.DepartureAngleDeg))
            {
                _departure.Text = Num(v.DepartureAngleDeg);
            }

            _vehicleInfo.Text = $"Wheelbase {v.Wheelbase:0.##} m, overhangs {v.FrontOverhang:0.##} m front / {v.RearOverhang:0.##} m rear. " +
                                "Appendix C: B99 at all car parks except domestic properties, B85 for domestic driveways.";
            _lastVehicle = name;
        }

        private void OnModeChanged()
        {
            bool section = _modeSection.Checked;
            _ve.Enabled = _hs.Enabled = _both.Enabled = section;
            _surface.Enabled = _reverse.Enabled = _interval.Enabled = _plot.Enabled = _plotVe.Enabled = !section;
        }

        private static string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

        private static bool TryNum(string text, out double value) =>
            double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);

        private bool TryBuild(out GroundClearanceChoice? choice, out string? problem)
        {
            choice = null;
            problem = null;
            if (!(_vehicle.SelectedItem is string name) || !_conventions.Vehicles.TryGetValue(name, out DesignVehicle? vehicle))
            {
                problem = "Choose a design vehicle.";
                return false;
            }

            if (!TryNum(_clearance.Text, out double h) || h < 0 || !TryNum(_approach.Text, out double app) || app < 0 || app >= 90 || !TryNum(_departure.Text, out double dep) || dep < 0 || dep >= 90)
            {
                problem = "Ground clearance must be 0 or more, and the angles between 0 and 90 degrees.";
                return false;
            }

            if (!TryNum(_step.Text, out double step) || step <= 0 || !TryNum(_interval.Text, out double interval) || interval <= 0)
            {
                problem = "The step and the check interval must be positive.";
                return false;
            }

            if (!TryNum(_ve.Text, out double ve) || ve <= 0 || !TryNum(_hs.Text, out double hs) || hs <= 0 || !TryNum(_plotVe.Text, out double plotVe) || plotVe <= 0)
            {
                problem = "Exaggerations and scales must be positive.";
                return false;
            }

            double? textHeight = null;
            if (_textHeight.Text.Trim().Length > 0)
            {
                if (!TryNum(_textHeight.Text, out double th) || th <= 0)
                {
                    problem = "The text height must be positive, or blank for the drawing's default.";
                    return false;
                }

                textHeight = th;
            }

            if (_modeSurface.Checked && _surface.SelectedItem == null)
            {
                problem = "Choose a surface for the 3D check.";
                return false;
            }

            // The template's angles live on the vehicle: use a copy with the dialog's values.
            DesignVehicle v = vehicle.Clone();
            v.GroundClearance = h;
            v.ApproachAngleDeg = app;
            v.DepartureAngleDeg = dep;

            var layers = _conventions.Layers.Clone();
            foreach (KeyValuePair<string, TextBox> kv in _layers)
            {
                string layer = kv.Value.Text.Trim();
                if (layer.Length == 0 || layer.IndexOfAny(new[] { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`' }) >= 0)
                {
                    problem = $"Layer name '{layer}' is empty or has characters BricsCAD doesn't allow.";
                    return false;
                }

                layers.Set(kv.Key, layer);
            }

            choice = new GroundClearanceChoice
            {
                Vehicle = v,
                Mode = _modeSection.Checked ? GroundClearanceChoice.CheckMode.Section : GroundClearanceChoice.CheckMode.Surface,
                VerticalExaggeration = ve,
                HorizontalScale = hs,
                BothDirections = _both.Checked,
                Surface = _surface.SelectedItem as string,
                Reverse = _reverse.Checked,
                PoseInterval = interval,
                PlotSections = _plot.Checked,
                PlotVerticalExaggeration = plotVe,
                GroundClearance = h,
                Step = step,
                DrawTemplates = _drawTemplates.Checked,
                DrawGroundings = _drawGroundings.Checked,
                DrawLabels = _drawLabels.Checked,
                Layers = layers,
                TextHeight = textHeight,
                Conventions = _conventions,
            };
            return true;
        }

        private void SaveDefaults()
        {
            if (!TryBuild(out GroundClearanceChoice? c, out string? problem) || c == null)
            {
                MessageBox.Show(this, problem, "Ground clearance", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                TurnPathConventions conv = _conventions;
                if (conv.Vehicles.TryGetValue(c.Vehicle.Name, out DesignVehicle? stored))
                {
                    stored.GroundClearance = c.GroundClearance;
                    stored.ApproachAngleDeg = c.Vehicle.ApproachAngleDeg;
                    stored.DepartureAngleDeg = c.Vehicle.DepartureAngleDeg;
                }

                conv.ClearanceStep = c.Step;
                conv.ClearancePoseInterval = c.PoseInterval;
                conv.SectionVerticalExaggeration = c.VerticalExaggeration;
                conv.TextHeight = c.TextHeight;
                conv.Layers.ClearanceCheck = c.Layers.ClearanceCheck;
                conv.Layers.ClearanceFail = c.Layers.ClearanceFail;
                conv.Layers.Labels = c.Layers.Labels;
                conv.Save(_savePath);
                _status.Text = "Saved to " + _savePath + " (used from now on; Claude picks it up after New chat).";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't save: " + ex.Message, "Ground clearance", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ------------------------------------------------------------- layout helpers (as in TurnPathDialog)

        private static TableLayoutPanel Table(int columns) => new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = columns, RowCount = 0, Margin = new Padding(0) };

        private static TableLayoutPanel Column()
        {
            TableLayoutPanel t = Table(1);
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ControlAdded += (s, e) =>
            {
                if (e.Control == null)
                {
                    return;
                }

                e.Control.Dock = DockStyle.Fill;
                if (e.Control.Margin == new Padding(0))
                {
                    e.Control.Margin = new Padding(0, 0, 0, 8);
                }
            };
            return t;
        }

        private static GroupBox Group(string title, Control content)
        {
            content.Dock = DockStyle.Fill;
            var group = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 4, 8, 6), Margin = new Padding(0, 0, 0, 10) };
            group.Controls.Add(content);
            return group;
        }

        private static void Row(TableLayoutPanel table, Control? caption, Control control, Control? hint)
        {
            int row = table.RowCount;
            if (caption != null)
            {
                table.Controls.Add(caption, 0, row);
            }

            table.Controls.Add(control, 1, row);
            if (hint != null && table.ColumnCount > 2)
            {
                table.Controls.Add(hint, 2, row);
            }

            table.RowCount = row + 1;
        }

        private static Label Caption(string text) => new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 8, 0) };

        private static Label Hint(string text) => new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 0) };

        private static Label Note(string text, int width) => new Label { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left, Margin = new Padding(3, 2, 3, 6) };

        private static TextBox Box(int width) => new TextBox { Width = width, Anchor = AnchorStyles.Left, Margin = new Padding(3, 2, 3, 2) };

        private static Button Button(string text) => new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 27), Padding = new Padding(8, 0, 8, 0), Margin = new Padding(6, 0, 0, 0) };
    }
}
