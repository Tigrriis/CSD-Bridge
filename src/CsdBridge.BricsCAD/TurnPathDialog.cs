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
            Points,
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
        public bool Reverse { get; set; }
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
        private readonly CheckBox _reverse;
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
        private readonly TextBox _outlineInterval;
        private readonly TextBox _textHeight;
        private readonly Dictionary<string, TextBox> _layers = new Dictionary<string, TextBox>();
        private readonly Label _status;
        private string _lastVehicle = "";

        public TurnPathDialog(TurnPathConventions conventions, string savePath)
        {
            _conventions = conventions;
            _savePath = savePath;

            Text = "CSD-Bridge - Vehicle turning path (AS/NZS 2890.1)";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(770, 640);
            Font = new Font("Segoe UI", 9f);

            // ------------------------------------------------------------ left column
            int x = 12, y = 12;
            Controls.Add(new Label { Text = "Design vehicle", Location = new Point(x, y + 3), AutoSize = true });
            _vehicle = new ComboBox { Location = new Point(x + 110, y), Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (string name in conventions.Vehicles.Keys)
            {
                _vehicle.Items.Add(name);
            }

            Controls.Add(_vehicle);
            _vehicleInfo = new Label { Location = new Point(x, y + 28), Size = new Size(360, 34), ForeColor = SystemColors.GrayText };
            Controls.Add(_vehicleInfo);

            var manoeuvre = new GroupBox { Text = "Manoeuvre", Location = new Point(x, 78), Size = new Size(360, 262) };
            Controls.Add(manoeuvre);
            int gy = 22;
            _modeTemplate = new RadioButton { Text = "Standard turn template: pick the start point and direction", Location = new Point(10, gy), AutoSize = true, Checked = true };
            _modeFollow = new RadioButton { Text = "Follow a line, arc or polyline you select", Location = new Point(10, gy + 22), AutoSize = true };
            _modePoints = new RadioButton { Text = "Drive through points you click (corners get arcs)", Location = new Point(10, gy + 44), AutoSize = true };
            manoeuvre.Controls.AddRange(new Control[] { _modeTemplate, _modeFollow, _modePoints });

            gy += 74;
            _radiusLabel = Field(manoeuvre, "Turn radius (m)", gy, out _radius, 70);
            manoeuvre.Controls.Add(new Label { Text = "at the outer front wheel", Location = new Point(215, gy + 3), AutoSize = true, ForeColor = SystemColors.GrayText });
            gy += 28;
            Field(manoeuvre, "Turn angle(s), degrees", gy, out _angles, 120);
            _angles.Text = "90";
            manoeuvre.Controls.Add(new Label { Text = "e.g. 90 or 30,60,90,120,150,180", Location = new Point(265, gy + 3), AutoSize = true, ForeColor = SystemColors.GrayText });
            gy += 28;
            manoeuvre.Controls.Add(new Label { Text = "Direction", Location = new Point(10, gy + 3), AutoSize = true });
            _left = new RadioButton { Text = "Left", Location = new Point(140, gy), AutoSize = true, Checked = true };
            _right = new RadioButton { Text = "Right", Location = new Point(200, gy), AutoSize = true };
            manoeuvre.Controls.AddRange(new Control[] { _left, _right });
            gy += 28;
            Field(manoeuvre, "Straight before / after (m)", gy, out _leadIn, 50);
            _leadOut = new TextBox { Location = new Point(200, gy), Width = 50 };
            manoeuvre.Controls.Add(_leadOut);
            gy += 28;
            manoeuvre.Controls.Add(new Label { Text = "Point following the path", Location = new Point(10, gy + 3), AutoSize = true });
            _reference = new ComboBox { Location = new Point(140, gy), Width = 205, DropDownStyle = ComboBoxStyle.DropDownList };
            _reference.Items.AddRange(new object[] { "Outer front wheel (AS 2890 radius)", "Front axle centre", "Rear axle centre" });
            manoeuvre.Controls.Add(_reference);
            gy += 28;
            _reverse = new CheckBox { Text = "Reverse (drive backwards along the path)", Location = new Point(140, gy), AutoSize = true };
            manoeuvre.Controls.Add(_reverse);

            var clearances = new GroupBox { Text = "Clearances added to the swept path (dashed template)", Location = new Point(x, 348), Size = new Size(360, 86) };
            Controls.Add(clearances);
            Field(clearances, "Inside / outside of the turn (m)", 22, out _clearInside, 50, 190);
            _clearOutside = new TextBox { Location = new Point(250, 22), Width = 50 };
            clearances.Controls.Add(_clearOutside);
            clearances.Controls.Add(new Label
            {
                Text = "B3.2: 300 mm manoeuvring both sides; B99 adds 300 mm circulation clearance on the outside.",
                Location = new Point(10, 50),
                Size = new Size(340, 30),
                ForeColor = SystemColors.GrayText,
            });

            var notes = new Label
            {
                Location = new Point(x, 442),
                Size = new Size(360, 130),
                ForeColor = SystemColors.GrayText,
                Text = "B99 is required for access driveways, ramps and circulation roadways; B85 only for parking spaces " +
                       "and aisles (B2.2, B2.3). Minimum template radii: B99 6.3 m, B85 5.8 m (B3.1). Turns on circulating " +
                       "roadways should be at least 8 m (A4).\n\nThe rear axle tracks in behind the point that follows the path, " +
                       "so the swept path includes the transition into the turn, as in Figures B3 to B6.",
            };
            Controls.Add(notes);

            // ----------------------------------------------------------- right column
            x = 390;
            var draw = new GroupBox { Text = "Draw", Location = new Point(x, 12), Size = new Size(368, 170) };
            Controls.Add(draw);
            _drawSwept = Check(draw, "Swept path (body)", 10, 22);
            _drawClearance = Check(draw, "Swept path + clearances", 10, 44);
            _drawVehicle = Check(draw, "Vehicle at start and end", 10, 66);
            _drawPath = Check(draw, "Driven path", 10, 88);
            _drawWheels = Check(draw, "Wheel paths", 190, 22);
            _drawLabels = Check(draw, "Label", 190, 44);
            _drawHatch = Check(draw, "Hatch the swept area", 190, 66);
            _closedOutline = Check(draw, "Swept path as one closed outline", 190, 88);
            Field(draw, "Vehicle outline every (m)", 114, out _outlineInterval, 50, 150);
            draw.Controls.Add(new Label { Text = "0 = none", Location = new Point(215, 117), AutoSize = true, ForeColor = SystemColors.GrayText });
            Field(draw, "Label text height", 140, out _textHeight, 50, 150);
            draw.Controls.Add(new Label { Text = "blank = drawing default", Location = new Point(215, 143), AutoSize = true, ForeColor = SystemColors.GrayText });

            var layers = new GroupBox { Text = "Layers ({vehicle} is replaced by the vehicle name)", Location = new Point(x, 190), Size = new Size(368, 262) };
            Controls.Add(layers);
            int ly = 22;
            foreach (var (part, caption) in new[]
            {
                ("swept_path", "Swept path"), ("clearance", "Clearances"), ("vehicle", "Vehicle outline"),
                ("vehicle_intermediate", "Outlines along the path"), ("path", "Driven path"), ("wheel_paths", "Wheel paths"),
                ("labels", "Label"), ("hatch", "Hatch"),
            })
            {
                Field(layers, caption, ly, out TextBox box, 215, 140);
                _layers[part] = box;
                ly += 28;
            }

            layers.Controls.Add(new Label
            {
                Text = "New layers get the styles from turn-paths.json; existing layers are not changed.",
                Location = new Point(10, ly + 2),
                Size = new Size(350, 16),
                ForeColor = SystemColors.GrayText,
            });

            // ------------------------------------------------------------------ bottom
            _status = new Label { Location = new Point(12, 604), Size = new Size(420, 30), ForeColor = SystemColors.GrayText };
            Controls.Add(_status);
            var save = new Button { Text = "Save as defaults", Location = new Point(390, 600), Width = 120 };
            save.Click += (s, e) => SaveDefaults();
            var reset = new Button { Text = "Reset", Location = new Point(516, 600), Width = 70 };
            reset.Click += (s, e) => LoadFrom(_conventions, keepVehicle: false);
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(602, 600), Width = 75 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(683, 600), Width = 75 };
            Controls.AddRange(new Control[] { save, reset, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;

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

            _drawVehicle.CheckedChanged += (s, e) => _closedOutline.Enabled = _drawSwept.Checked;
            FormClosing += (s, e) =>
            {
                if (DialogResult == DialogResult.OK && !TryBuildChoice(out _, out string? problem))
                {
                    MessageBox.Show(this, problem, "Turning path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    e.Cancel = true;
                }
            };

            LoadFrom(conventions, keepVehicle: false);
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

        private static Label Field(Control parent, string caption, int y, out TextBox box, int width, int boxX = 140)
        {
            var label = new Label { Text = caption, Location = new Point(10, y + 3), AutoSize = true };
            box = new TextBox { Location = new Point(boxX, y), Width = width };
            parent.Controls.Add(label);
            parent.Controls.Add(box);
            return label;
        }

        private static CheckBox Check(Control parent, string caption, int x, int y)
        {
            var box = new CheckBox { Text = caption, Location = new Point(x, y), AutoSize = true };
            parent.Controls.Add(box);
            return box;
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
            _drawSwept.Checked = c.Draw.SweptPath;
            _drawClearance.Checked = c.Draw.Clearance;
            _drawVehicle.Checked = c.Draw.Vehicle;
            _drawPath.Checked = c.Draw.Path;
            _drawWheels.Checked = c.Draw.WheelPaths;
            _drawLabels.Checked = c.Draw.Labels;
            _drawHatch.Checked = c.Draw.Hatch;
            _closedOutline.Checked = c.Draw.ClosedOutline;
            _outlineInterval.Text = Num(c.OutlineInterval);
            _textHeight.Text = c.TextHeight is double th ? Num(th) : "";
            foreach (KeyValuePair<string, string> kv in c.Layers.All())
            {
                _layers[kv.Key].Text = kv.Value;
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
            _radiusLabel.Text = points ? "Corner radius (m)" : "Turn radius (m)";
            _angles.Enabled = template;
            _left.Enabled = _right.Enabled = template;
            _leadIn.Enabled = _leadOut.Enabled = template;
            _reverse.Enabled = !template;
            if (template)
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
                Mode = _modeTemplate.Checked ? TurnPathChoice.ManoeuvreMode.Template : _modeFollow.Checked ? TurnPathChoice.ManoeuvreMode.Follow : TurnPathChoice.ManoeuvreMode.Points,
                Left = _left.Checked,
                Reverse = _reverse.Checked,
                Reference = _reference.SelectedIndex == 2 ? ReferencePoint.RearAxle : _reference.SelectedIndex == 1 ? ReferencePoint.FrontAxle : ReferencePoint.OuterFrontWheel,
                Conventions = _conventions,
            };

            if (c.Mode != TurnPathChoice.ManoeuvreMode.Follow)
            {
                if (!TryNum(_radius.Text, out double radius) || radius <= 0)
                {
                    problem = "Enter a radius in metres.";
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
