using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using CsdBridge.Core.Vehicles;

namespace CsdBridge.BricsCAD
{
    /// <summary>How the AITURN command should run, as chosen in the dialog.</summary>
    internal sealed class TurnPathChoice
    {
        public enum ManoeuvreMode
        {
            Template,
            Follow,
            Drive,
        }

        public DesignVehicle Vehicle { get; set; } = DesignVehicle.B99;
        public ManoeuvreMode Mode { get; set; }

        /// <summary>Template: radius at the outer front wheel. Points: corner radius of the reference point's path.</summary>
        public double Radius { get; set; }

        /// <summary>Template: one swept path per angle.</summary>
        public List<double> TurnAngles { get; set; } = new List<double> { 90 };

        public bool Left { get; set; } = true;
        public double LeadIn { get; set; }
        public double LeadOut { get; set; } = 6;
        public ReferencePoint Reference { get; set; } = ReferencePoint.OuterFrontWheel;

        /// <summary>Drive mode: arc to the destination where possible, or always turn at the radius then straight.</summary>
        public TurnPathBuilder.DriveTurns Turns { get; set; } = TurnPathBuilder.DriveTurns.Arc;
        public bool Reverse { get; set; }

        /// <summary>Drive mode: ask which way the vehicle faces at each destination.</summary>
        public bool ArrivalHeading { get; set; }

        /// <summary>Check the result against linework on the obstruction layers.</summary>
        public bool CheckConflicts { get; set; }

        public List<string> ObstructionLayers { get; set; } = new List<string>();
        public double ClearanceInside { get; set; } = 0.3;
        public double ClearanceOutside { get; set; } = 0.3;
        public TurnPathStyle Style { get; set; } = new TurnPathStyle();
        public TurnPathConventions Conventions { get; set; } = TurnPathConventions.Default();
    }

    /// <summary>
    /// Options for a vehicle turning path: vehicle, manoeuvre, clearances, what to draw and the
    /// layer for each part. Defaults come from turn-paths.json; "Save as defaults" writes them
    /// back to the user's copy.
    /// </summary>
    internal sealed class TurnPathDialog : Form
    {
        private readonly TurnPathConventions _conventions;
        private readonly string _savePath;
        private readonly ComboBox _vehicle;
        private readonly Label _vehicleInfo;
        private readonly RadioButton _modeTemplate;
        private readonly RadioButton _modeFollow;
        private readonly RadioButton _modePoints;
        private readonly TextBox _radius;
        private readonly Label _radiusLabel;
        private readonly TextBox _angles;
        private readonly RadioButton _left;
        private readonly RadioButton _right;
        private readonly TextBox _leadIn;
        private readonly TextBox _leadOut;
        private readonly ComboBox _reference;
        private readonly ComboBox _turns;
        private readonly CheckBox _reverse;
        private readonly CheckBox _arrival;
        private readonly CheckBox _checkConflicts;
        private readonly TextBox _obstructions;
        private readonly bool _editing;
        private readonly TextBox _clearInside;
        private readonly TextBox _clearOutside;
        private readonly CheckBox _drawSwept;
        private readonly CheckBox _drawClearance;
        private readonly CheckBox _drawVehicle;
        private readonly CheckBox _drawPath;
        private readonly CheckBox _drawWheels;
        private readonly CheckBox _drawLabels;
        private readonly CheckBox _drawHatch;
        private readonly CheckBox _closedOutline;
        private readonly CheckBox _block;
        private readonly TextBox _outlineInterval;
        private readonly TextBox _textHeight;
        private readonly Dictionary<string, TextBox> _layers = new Dictionary<string, TextBox>();
        private readonly Label _status;
        private string _lastVehicle = "";

        /// <param name="editing">A saved turning path to change: its settings fill the dialog and its manoeuvre can't be switched.</param>
        public TurnPathDialog(TurnPathConventions conventions, string savePath, ManoeuvreRecord? editing = null)
        {
            _conventions = conventions;
            _savePath = savePath;
            _editing = editing != null;

            Text = _editing ? "CSD-Bridge - Edit vehicle turning path" : "CSD-Bridge - Vehicle turning path (AS/NZS 2890.1)";
            Font = new Font("Segoe UI", 9f);
            AutoScaleDimensions = new SizeF(7f, 15f);
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            // Everything sizes to its text, so the layout holds at any display scaling.
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
            _vehicleInfo = Note("", LeftWidth);
            left.Controls.Add(_vehicleInfo);

            TableLayoutPanel manoeuvre = Table(3);
            _modeTemplate = new RadioButton { Text = "Standard turn template: pick the start point and direction", AutoSize = true, Checked = true };
            _modeFollow = new RadioButton { Text = "Follow a line, arc or polyline you select", AutoSize = true };
            _modePoints = new RadioButton { Text = "Drive: click where it goes next (turns at the radius; behind = reverse; each leg kept)", AutoSize = true };
            foreach (RadioButton rb in new[] { _modeTemplate, _modeFollow, _modePoints })
            {
                rb.Margin = new Padding(3, 2, 3, 2);
                manoeuvre.Controls.Add(rb, 0, manoeuvre.RowCount);
                manoeuvre.SetColumnSpan(rb, 3);
                manoeuvre.RowCount++;
            }

            _radiusLabel = Caption("Turn radius (m)");
            _radius = Box(60);
            Row(manoeuvre, _radiusLabel, _radius, Hint("at the outer front wheel"));
            _angles = Box(110);
            _angles.Text = "90";
            Row(manoeuvre, Caption("Turn angle(s), degrees"), _angles, Hint("e.g. 90, or 30,60,90,120,150,180"));
            _left = new RadioButton { Text = "Left", AutoSize = true, Checked = true };
            _right = new RadioButton { Text = "Right", AutoSize = true };
            Row(manoeuvre, Caption("Direction"), Flow(_left, _right), null);
            _leadIn = Box(55);
            _leadOut = Box(55);
            Row(manoeuvre, Caption("Straight before / after (m)"), Flow(_leadIn, Hint("/"), _leadOut), null);
            _reference = new ComboBox { Width = 230, DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left };
            _reference.Items.AddRange(new object[] { "Outer front wheel (AS 2890 radius)", "Front axle centre", "Rear axle centre" });
            Row(manoeuvre, Caption("Point following the path"), _reference, null);
            _turns = new ComboBox { Width = 230, DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left };
            _turns.Items.AddRange(new object[] { "Arc to the point where the radius allows", "Turn at the radius, then straight" });
            Row(manoeuvre, Caption("Drive turns"), _turns, null);
            _arrival = new CheckBox { Text = "Drive: also pick which way it faces at each destination", AutoSize = true };
            Row(manoeuvre, null, _arrival, null);
            manoeuvre.SetColumnSpan(_arrival, 2);
            _reverse = new CheckBox { Text = "Reverse (drive backwards along the path)", AutoSize = true };
            Row(manoeuvre, null, _reverse, null);
            manoeuvre.SetColumnSpan(_reverse, 2);
            left.Controls.Add(Group("Manoeuvre", manoeuvre));

            TableLayoutPanel clearances = Table(2);
            _clearInside = Box(55);
            _clearOutside = Box(55);
            Row(clearances, Caption("Inside / outside of the turn (m)"), Flow(_clearInside, Hint("/"), _clearOutside), null);
            Label clearanceNote = Note("B3.2: 300 mm manoeuvring clearance both sides; the B99 adds 300 mm circulation clearance on the outside.", LeftWidth - 30);
            clearances.Controls.Add(clearanceNote, 0, clearances.RowCount);
            clearances.SetColumnSpan(clearanceNote, 2);
            clearances.RowCount++;
            left.Controls.Add(Group("Clearances added to the swept path (dashed template)", clearances));

            TableLayoutPanel conflicts = Table(2);
            _checkConflicts = new CheckBox { Text = "Check against kerbs, walls and columns on these layers", AutoSize = true };
            conflicts.Controls.Add(_checkConflicts, 0, conflicts.RowCount);
            conflicts.SetColumnSpan(_checkConflicts, 2);
            conflicts.RowCount++;
            _obstructions = Box(300);
            Row(conflicts, Caption("Layers"), _obstructions, null);
            Label conflictNote = Note("Separate with commas; * is a wildcard and matches xref layers too (A-WALL* finds arch|A-WALL-EXT). Leave blank to pick objects on the layers after OK. Frozen and off layers are ignored.", LeftWidth - 30);
            conflicts.Controls.Add(conflictNote, 0, conflicts.RowCount);
            conflicts.SetColumnSpan(conflictNote, 2);
            conflicts.RowCount++;
            left.Controls.Add(Group("Conflicts", conflicts));

            left.Controls.Add(Note(
                "B99 is required for access driveways, ramps and circulation roadways; B85 only for parking spaces and aisles " +
                "(B2.2, B2.3). Minimum template radii: B99 6.3 m, B85 5.8 m (B3.1). Turns on circulating roadways should be at " +
                "least 8 m (A4). The rear axle tracks in behind the point that follows the path, so the swept path includes the " +
                "transition into the turn, as in Figures B3 to B6.", LeftWidth));

            // ----------------------------------------------------------- right column
            TableLayoutPanel right = Column();

            TableLayoutPanel draw = Table(2);
            _drawSwept = Check("Swept path (body)");
            _drawClearance = Check("Swept path + clearances");
            _drawVehicle = Check("Vehicle at start and end");
            _drawPath = Check("Driven path");
            _drawWheels = Check("Wheel paths");
            _drawLabels = Check("Label");
            _drawHatch = Check("Hatch the swept area");
            _closedOutline = Check("Swept path as one closed outline");
            _block = Check("Make the result a block");
            Control[] column1 = { _drawSwept, _drawClearance, _drawVehicle, _drawPath, _block };
            Control[] column2 = { _drawWheels, _drawLabels, _drawHatch, _closedOutline, Hint("named '<vehicle> turning path', inserted at the start") };
            for (int i = 0; i < column1.Length; i++)
            {
                draw.Controls.Add(column1[i], 0, i);
                draw.Controls.Add(column2[i], 1, i);
            }

            draw.RowCount = column1.Length;
            TableLayoutPanel drawNumbers = Table(3);
            _outlineInterval = Box(55);
            Row(drawNumbers, Caption("Vehicle outline every (m)"), _outlineInterval, Hint("0 = none"));
            _textHeight = Box(55);
            Row(drawNumbers, Caption("Label text height"), _textHeight, Hint("blank = drawing default"));
            draw.Controls.Add(drawNumbers, 0, draw.RowCount);
            draw.SetColumnSpan(drawNumbers, 2);
            draw.RowCount++;
            right.Controls.Add(Group("Draw", draw));

            TableLayoutPanel layers = Table(2);
            foreach (var (part, caption) in new[]
            {
                ("swept_path", "Swept path"), ("clearance", "Clearances"), ("vehicle", "Vehicle outline"),
                ("vehicle_intermediate", "Outlines along the path"), ("path", "Driven path"), ("wheel_paths", "Wheel paths"),
                ("labels", "Label"), ("hatch", "Hatch"), ("block", "Block"), ("conflict", "Conflicts"),
            })
            {
                TextBox box = Box(260);
                Row(layers, Caption(caption), box, null);
                _layers[part] = box;
            }

            Label layerNote = Note("New layers get the styles from turn-paths.json; existing layers are not changed.", 400);
            layers.Controls.Add(layerNote, 0, layers.RowCount);
            layers.SetColumnSpan(layerNote, 2);
            layers.RowCount++;
            right.Controls.Add(Group("Layers ({vehicle} is replaced by the vehicle name)", layers));

            // ------------------------------------------------------------------ bottom
            _status = Note("", 440);
            var save = Button("Save as defaults");
            save.Click += (s, e) => SaveDefaults();
            var reset = Button("Reset");
            reset.Click += (s, e) => LoadFrom(_conventions, keepVehicle: false);
            var ok = Button("OK");
            ok.DialogResult = DialogResult.OK;
            var cancel = Button("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Anchor = AnchorStyles.Right | AnchorStyles.Bottom, Margin = new Padding(0) };
            buttons.Controls.AddRange(new Control[] { save, reset, ok, cancel });
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
            foreach (RadioButton rb in new[] { _modeTemplate, _modeFollow, _modePoints })
            {
                rb.CheckedChanged += (s, e) =>
                {
                    if (rb.Checked)
                    {
                        OnModeChanged();
                    }
                };
            }

            _drawSwept.CheckedChanged += (s, e) => _closedOutline.Enabled = _drawSwept.Checked;
            FormClosing += (s, e) =>
            {
                if (DialogResult == DialogResult.OK && !TryBuildChoice(out _, out string? problem))
                {
                    MessageBox.Show(this, problem, "Turning path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    e.Cancel = true;
                }
            };

            LoadFrom(conventions, keepVehicle: false);
            if (editing != null)
            {
                LoadRecord(editing);
            }
        }

        /// <summary>Fills the dialog from a saved turning path.</summary>
        private void LoadRecord(ManoeuvreRecord r)
        {
            int index = _vehicle.Items.IndexOf(_conventions.Vehicles.Keys.FirstOrDefault(k => string.Equals(k, r.Vehicle, StringComparison.OrdinalIgnoreCase)) ?? r.Vehicle);
            if (index >= 0)
            {
                _vehicle.SelectedIndex = index;
            }

            string mode = (r.Mode ?? "").ToLowerInvariant();
            _modeTemplate.Checked = mode == "template";
            _modeFollow.Checked = mode == "follow";
            _modePoints.Checked = mode == "drive";
            OnModeChanged();
            _modeTemplate.Enabled = _modeFollow.Enabled = _modePoints.Enabled = false;

            if (r.Radius > 0)
            {
                _radius.Text = Num(r.Radius);
            }

            _angles.Text = string.Join(",", r.TurnAngles.Select(Num));
            _left.Checked = r.Left;
            _right.Checked = !r.Left;
            _leadIn.Text = Num(r.LeadIn);
            _leadOut.Text = Num(r.LeadOut);
            _reverse.Checked = r.Reverse && mode == "follow";
            _reference.SelectedIndex = r.Reference == "rear_axle" ? 2 : r.Reference == "front_axle" ? 1 : 0;
            _turns.SelectedIndex = r.TurnsKind == TurnPathBuilder.DriveTurns.Corner ? 1 : 0;
            _arrival.Checked = r.Stops.Any(s => s.HeadingDeg != null);
            _clearInside.Text = Num(r.ClearanceInside);
            _clearOutside.Text = Num(r.ClearanceOutside);
            _drawSwept.Checked = r.Draw.SweptPath;
            _drawClearance.Checked = r.Draw.Clearance;
            _drawVehicle.Checked = r.Draw.Vehicle;
            _drawPath.Checked = r.Draw.Path;
            _drawWheels.Checked = r.Draw.WheelPaths;
            _drawLabels.Checked = r.Draw.Labels;
            _drawHatch.Checked = r.Draw.Hatch;
            _closedOutline.Checked = r.Draw.ClosedOutline;
            _block.Checked = r.Draw.Block;
            _outlineInterval.Text = Num(r.OutlineInterval);
            _textHeight.Text = r.TextHeight is double th ? Num(th) : "";
            foreach (KeyValuePair<string, string> kv in r.Layers.All())
            {
                if (_layers.TryGetValue(kv.Key, out TextBox? box))
                {
                    box.Text = kv.Value;
                }
            }

            _checkConflicts.Checked = r.CheckConflicts;
            _obstructions.Text = string.Join(", ", r.ObstructionLayers);
            _status.Text = "Settings of the selected turning path. OK regenerates it.";
        }

        /// <summary>What the user chose; valid once the dialog returned OK.</summary>
        public TurnPathChoice Choice
        {
            get
            {
                TryBuildChoice(out TurnPathChoice? choice, out _);
                return choice ?? new TurnPathChoice();
            }
        }

        // ------------------------------------------------------------- layout helpers
        // Design-time pixel widths; AutoScaleMode.Font scales them with the display.

        private const int LeftWidth = 420;

        /// <summary>An auto-sizing table whose rows grow to their content.</summary>
        private static TableLayoutPanel Table(int columns) => new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = columns,
            RowCount = 0,
            Margin = new Padding(0),
        };

        /// <summary>A one-column stack in which every item stretches to the widest.</summary>
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

        /// <summary>Adds one row: caption | control | hint. Nulls leave the cell empty.</summary>
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

        /// <summary>A grey note that wraps at the given design width.</summary>
        private static Label Note(string text, int width) => new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            ForeColor = SystemColors.GrayText,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 2, 3, 6),
        };

        private static TextBox Box(int width) => new TextBox { Width = width, Anchor = AnchorStyles.Left, Margin = new Padding(3, 2, 3, 2) };

        private static CheckBox Check(string text) => new CheckBox { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 2, 12, 2) };

        private static Button Button(string text) => new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(80, 27),
            Padding = new Padding(8, 0, 8, 0),
            Margin = new Padding(6, 0, 0, 0),
        };

        /// <summary>Controls side by side, sized to fit.</summary>
        private static FlowLayoutPanel Flow(params Control[] controls)
        {
            var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
            foreach (Control c in controls)
            {
                c.Anchor = AnchorStyles.Left;
                if (c is Label)
                {
                    c.Margin = new Padding(2, 5, 2, 0);
                }

                flow.Controls.Add(c);
            }

            return flow;
        }

        private void LoadFrom(TurnPathConventions c, bool keepVehicle)
        {
            string vehicle = keepVehicle && _vehicle.SelectedItem is string sel ? sel : c.DefaultVehicle;
            int index = _vehicle.Items.IndexOf(vehicle);
            _vehicle.SelectedIndex = index >= 0 ? index : 0;
            _lastVehicle = "";
            OnVehicleChanged();

            _leadIn.Text = Num(c.LeadIn);
            _leadOut.Text = Num(c.LeadOut);
            _turns.SelectedIndex = c.DriveTurnsMode == TurnPathBuilder.DriveTurns.Corner ? 1 : 0;
            _arrival.Checked = c.DriveArrivalHeading;
            _checkConflicts.Checked = c.CheckConflicts;
            _obstructions.Text = string.Join(", ", c.ObstructionLayers);
            _drawSwept.Checked = c.Draw.SweptPath;
            _drawClearance.Checked = c.Draw.Clearance;
            _drawVehicle.Checked = c.Draw.Vehicle;
            _drawPath.Checked = c.Draw.Path;
            _drawWheels.Checked = c.Draw.WheelPaths;
            _drawLabels.Checked = c.Draw.Labels;
            _drawHatch.Checked = c.Draw.Hatch;
            _closedOutline.Checked = c.Draw.ClosedOutline;
            _block.Checked = c.Draw.Block;
            _outlineInterval.Text = Num(c.OutlineInterval);
            _textHeight.Text = c.TextHeight is double th ? Num(th) : "";
            foreach (KeyValuePair<string, string> kv in c.Layers.All())
            {
                // The conventions also hold the ground clearance layers, which this dialog doesn't show.
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
            // Only replace values the user hasn't typed over.
            if (previous == null || _radius.Text == Num(previous.MinTurnRadius) || _radius.Text.Length == 0)
            {
                _radius.Text = Num(v.MinTurnRadius);
            }

            if (previous == null || _clearInside.Text == Num(previous.ClearanceInside))
            {
                _clearInside.Text = Num(v.ClearanceInside);
            }

            if (previous == null || _clearOutside.Text == Num(previous.ClearanceOutside))
            {
                _clearOutside.Text = Num(v.ClearanceOutside);
            }

            _vehicleInfo.Text = $"{v.Length:0.##} x {v.Width:0.##} m, wheelbase {v.Wheelbase:0.##} m, overhangs {v.FrontOverhang:0.##} / {v.RearOverhang:0.##} m, " +
                                $"minimum turn radius {v.MinTurnRadius:0.#} m. {v.Description}";
            _lastVehicle = name;
        }

        private void OnModeChanged()
        {
            bool template = _modeTemplate.Checked, points = _modePoints.Checked;
            _radius.Enabled = template || points;
            _radiusLabel.Text = "Turn radius (m)";
            _angles.Enabled = template;
            _left.Enabled = _right.Enabled = template;
            _leadIn.Enabled = _leadOut.Enabled = template;
            // Drive mode decides forward/reverse and the reference point from where you click.
            _reverse.Enabled = !template && !points;
            _reference.Enabled = !points;
            _turns.Enabled = points;
            _arrival.Enabled = points;
            if (template || points)
            {
                _reverse.Checked = false;
            }

            _reference.SelectedIndex = template ? 0 : _reverse.Checked ? 2 : 1;
        }

        private static string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

        private static bool TryNum(string text, out double value) =>
            double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);

        private bool TryBuildChoice(out TurnPathChoice? choice, out string? problem)
        {
            choice = null;
            problem = null;
            if (!(_vehicle.SelectedItem is string name) || !_conventions.Vehicles.TryGetValue(name, out DesignVehicle? vehicle))
            {
                problem = "Choose a design vehicle.";
                return false;
            }

            var c = new TurnPathChoice
            {
                Vehicle = vehicle,
                Mode = _modeTemplate.Checked ? TurnPathChoice.ManoeuvreMode.Template : _modeFollow.Checked ? TurnPathChoice.ManoeuvreMode.Follow : TurnPathChoice.ManoeuvreMode.Drive,
                Left = _left.Checked,
                Reverse = _reverse.Checked,
                Reference = _reference.SelectedIndex == 2 ? ReferencePoint.RearAxle : _reference.SelectedIndex == 1 ? ReferencePoint.FrontAxle : ReferencePoint.OuterFrontWheel,
                Turns = _turns.SelectedIndex == 1 ? TurnPathBuilder.DriveTurns.Corner : TurnPathBuilder.DriveTurns.Arc,
                ArrivalHeading = _arrival.Checked,
                CheckConflicts = _checkConflicts.Checked,
                ObstructionLayers = LayerPattern.Parse(_obstructions.Text),
                Conventions = _conventions,
            };

            if (c.Mode != TurnPathChoice.ManoeuvreMode.Follow)
            {
                if (!TryNum(_radius.Text, out double radius) || radius <= 0)
                {
                    problem = "Enter a radius in metres.";
                    return false;
                }

                if (c.Mode == TurnPathChoice.ManoeuvreMode.Drive && radius < vehicle.MinTurnRadius - 1e-9)
                {
                    problem = $"The {vehicle.Name} can't turn tighter than {Num(vehicle.MinTurnRadius)} m (outer front wheel, AS/NZS 2890.1 B3.1).";
                    return false;
                }

                c.Radius = radius;
            }

            if (c.Mode == TurnPathChoice.ManoeuvreMode.Template)
            {
                c.TurnAngles = new List<double>();
                foreach (string part in _angles.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!TryNum(part, out double a) || a <= 0 || a > 360)
                    {
                        problem = "Turn angles must be between 0 and 360 degrees, separated by commas.";
                        return false;
                    }

                    c.TurnAngles.Add(a);
                }

                if (c.TurnAngles.Count == 0)
                {
                    problem = "Enter at least one turn angle.";
                    return false;
                }

                if (!TryNum(_leadIn.Text, out double leadIn) || leadIn < 0 || !TryNum(_leadOut.Text, out double leadOut) || leadOut < 0)
                {
                    problem = "The straight lengths before and after the turn must be 0 or more.";
                    return false;
                }

                c.LeadIn = leadIn;
                c.LeadOut = leadOut;
                if (c.Radius < vehicle.MinTurnRadius - 1e-9)
                {
                    // Allowed (the command warns), but make sure it's deliberate.
                    _status.Text = $"Note: {Num(c.Radius)} m is below the {vehicle.Name}'s minimum of {Num(vehicle.MinTurnRadius)} m.";
                }
            }

            if (!TryNum(_clearInside.Text, out double ci) || ci < 0 || !TryNum(_clearOutside.Text, out double co) || co < 0)
            {
                problem = "Clearances must be 0 or more.";
                return false;
            }

            c.ClearanceInside = ci;
            c.ClearanceOutside = co;

            if (!TryNum(_outlineInterval.Text, out double interval) || interval < 0)
            {
                problem = "The outline interval must be 0 or more.";
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

            var style = TurnPathStyle.From(_conventions);
            style.Draw.SweptPath = _drawSwept.Checked;
            style.Draw.Clearance = _drawClearance.Checked;
            style.Draw.Vehicle = _drawVehicle.Checked;
            style.Draw.Path = _drawPath.Checked;
            style.Draw.WheelPaths = _drawWheels.Checked;
            style.Draw.Labels = _drawLabels.Checked;
            style.Draw.Hatch = _drawHatch.Checked;
            style.Draw.ClosedOutline = _closedOutline.Checked;
            style.Draw.Block = _block.Checked;
            style.OutlineInterval = interval;
            style.TextHeight = textHeight;
            foreach (KeyValuePair<string, TextBox> kv in _layers)
            {
                string layer = kv.Value.Text.Trim();
                if (layer.Length == 0)
                {
                    problem = "Every part needs a layer name.";
                    return false;
                }

                if (layer.IndexOfAny(new[] { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`' }) >= 0)
                {
                    problem = $"Layer name '{layer}' has characters BricsCAD doesn't allow.";
                    return false;
                }

                style.Layers.Set(kv.Key, layer);
            }

            c.Style = style;
            choice = c;
            return true;
        }

        private void SaveDefaults()
        {
            if (!TryBuildChoice(out TurnPathChoice? choice, out string? problem) || choice == null)
            {
                MessageBox.Show(this, problem, "Turning path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                TurnPathConventions c = _conventions;
                c.DefaultVehicle = choice.Vehicle.Name;
                c.LeadIn = choice.LeadIn;
                c.LeadOut = choice.LeadOut;
                c.DriveTurns = choice.Turns == TurnPathBuilder.DriveTurns.Corner ? "corner" : "arc";
                c.DriveArrivalHeading = choice.ArrivalHeading;
                c.CheckConflicts = choice.CheckConflicts;
                c.ObstructionLayers = choice.ObstructionLayers.ToList();
                c.OutlineInterval = choice.Style.OutlineInterval;
                c.TextHeight = choice.Style.TextHeight;
                c.Draw = choice.Style.Draw.Clone();
                c.Layers = choice.Style.Layers.Clone();
                choice.Vehicle.ClearanceInside = choice.ClearanceInside;
                choice.Vehicle.ClearanceOutside = choice.ClearanceOutside;
                c.Save(_savePath);
                _status.Text = "Saved to " + _savePath + " (used from now on; Claude picks it up after New chat).";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't save: " + ex.Message, "Turning path", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
