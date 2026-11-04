using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace CsdBridge.BricsCAD.Csd
{
    /// <summary>
    /// Reads and operates CSD's WinForms dialogs from inside BricsCAD. Everything that
    /// touches a control runs on the dialog's UI thread (see <see cref="CsdWindows.OnUiThread"/>).
    /// Edits go through the same paths a user's typing does (focus, edit, commit), so
    /// CSD's own event handlers run and recalculate.
    /// </summary>
    internal static class CsdDialogs
    {
        private const int MaxItems = 60;
        private const int MaxText = 300;

        // ================================================================== reading

        public static object Read(OpenDialog d, bool includeHidden, string? onlyGrid, int maxRows)
        {
            if (d.Form == null)
            {
                return ReadWin32(d);
            }

            Form form = d.Form;
            return CsdWindows.OnUiThread(form, () =>
            {
                var r = new FormReading();
                Walk(form, form, r, includeHidden, onlyGrid, maxRows, null);
                return (object)new
                {
                    dialog = new { id = d.Id, title = form.Text, type = form.GetType().Name, modal = form.Modal },
                    fields = r.Fields.Count > 0 ? r.Fields : null,
                    grids = r.Grids.Count > 0 ? r.Grids : null,
                    buttons = r.Buttons.Count > 0 ? r.Buttons : null,
                    menus = r.Menus.Count > 0 ? r.Menus : null,
                    tabs = r.Tabs.Count > 0 ? r.Tabs : null,
                    labels = r.Labels.Count > 0 ? r.Labels : null,
                };
            });
        }

        private sealed class FormReading
        {
            public List<object> Fields { get; } = new List<object>();
            public List<object> Grids { get; } = new List<object>();
            public List<object> Buttons { get; } = new List<object>();
            public List<object> Menus { get; } = new List<object>();
            public List<object> Tabs { get; } = new List<object>();
            public List<object> Labels { get; } = new List<object>();
        }

        private static void Walk(Form form, Control parent, FormReading r, bool includeHidden, string? onlyGrid, int maxRows, string? group)
        {
            foreach (Control c in parent.Controls)
            {
                bool hidden = !c.Visible;
                if (hidden && !includeHidden)
                {
                    continue;
                }

                bool? isHidden = hidden ? true : (bool?)null;
                bool? isDisabled = c.Enabled ? (bool?)null : true;
                string name = NameOf(c);

                switch (c)
                {
                    case DataGridView grid:
                        if (onlyGrid == null || string.Equals(grid.Name, onlyGrid, StringComparison.OrdinalIgnoreCase))
                        {
                            r.Grids.Add(ReadGrid(grid, includeHidden, maxRows, isHidden));
                        }

                        continue;

                    case TextBoxBase tb:
                        if (onlyGrid != null)
                        {
                            continue;
                        }

                        bool password = tb is TextBox t && (t.UseSystemPasswordChar || t.PasswordChar != '\0');
                        r.Fields.Add(new
                        {
                            name,
                            type = "text",
                            label = LabelFor(c),
                            group,
                            value = password ? "(hidden)" : Short(tb.Text),
                            read_only = tb.ReadOnly ? true : (bool?)null,
                            hidden = isHidden,
                            disabled = isDisabled,
                        });
                        continue;

                    case ComboBox cb:
                        if (onlyGrid != null)
                        {
                            continue;
                        }

                        r.Fields.Add(new
                        {
                            name,
                            type = "choice",
                            label = LabelFor(c),
                            group,
                            value = Short(cb.Text),
                            options = cb.Items.Cast<object>().Take(MaxItems).Select(i => Short(cb.GetItemText(i))).ToList(),
                            more_options = cb.Items.Count > MaxItems ? cb.Items.Count - MaxItems : (int?)null,
                            free_text = cb.DropDownStyle != ComboBoxStyle.DropDownList ? true : (bool?)null,
                            hidden = isHidden,
                            disabled = isDisabled,
                        });
                        continue;

                    case CheckBox chk:
                        if (onlyGrid == null)
                        {
                            r.Fields.Add(new { name, type = "check", text = Short(chk.Text), group, value = chk.Checked, hidden = isHidden, disabled = isDisabled });
                        }

                        continue;

                    case RadioButton rb:
                        if (onlyGrid == null)
                        {
                            r.Fields.Add(new { name, type = "option", text = Short(rb.Text), group, value = rb.Checked, hidden = isHidden, disabled = isDisabled });
                        }

                        continue;

                    case NumericUpDown num:
                        if (onlyGrid == null)
                        {
                            r.Fields.Add(new
                            {
                                name,
                                type = "number",
                                label = LabelFor(c),
                                group,
                                value = num.Value,
                                min = num.Minimum,
                                max = num.Maximum,
                                hidden = isHidden,
                                disabled = isDisabled,
                            });
                        }

                        continue;

                    case ListBox lb:
                        if (onlyGrid == null)
                        {
                            r.Fields.Add(new
                            {
                                name,
                                type = "list",
                                label = LabelFor(c),
                                group,
                                selected = lb.SelectedItems.Cast<object>().Select(i => Short(lb.GetItemText(i))).ToList(),
                                options = lb.Items.Cast<object>().Take(MaxItems).Select(i => Short(lb.GetItemText(i))).ToList(),
                                hidden = isHidden,
                                disabled = isDisabled,
                            });
                        }

                        continue;

                    case ButtonBase btn:
                        if (onlyGrid == null)
                        {
                            r.Buttons.Add(new { name, text = NullIfEmpty(Short(btn.Text)), tooltip = TooltipFor(form, btn), group, hidden = isHidden, disabled = isDisabled });
                        }

                        continue;

                    case Label lbl:
                        if (onlyGrid == null && !string.IsNullOrWhiteSpace(lbl.Text))
                        {
                            r.Labels.Add(new { name, text = Short(lbl.Text), group, hidden = isHidden });
                        }

                        continue;

                    case ToolStrip strip:
                        if (onlyGrid == null)
                        {
                            AddToolStripItems(strip.Items, "", r.Menus, includeHidden);
                        }

                        continue;

                    case TabControl tabs:
                        if (onlyGrid == null)
                        {
                            r.Tabs.Add(new
                            {
                                name,
                                pages = tabs.TabPages.Cast<TabPage>().Select(p => string.IsNullOrEmpty(p.Text) ? p.Name : p.Text).ToList(),
                                selected = tabs.SelectedTab == null ? null : (string.IsNullOrEmpty(tabs.SelectedTab.Text) ? tabs.SelectedTab.Name : tabs.SelectedTab.Text),
                            });
                        }

                        break; // and walk into the pages
                }

                string? childGroup = c is GroupBox gb && !string.IsNullOrWhiteSpace(gb.Text) ? gb.Text.Trim() : group;
                if (c.HasChildren)
                {
                    Walk(form, c, r, includeHidden, onlyGrid, maxRows, childGroup);
                }
            }
        }

        private static object ReadGrid(DataGridView grid, bool includeHidden, int maxRows, bool? hidden)
        {
            List<DataGridViewColumn> columns = grid.Columns.Cast<DataGridViewColumn>().OrderBy(col => col.DisplayIndex).ToList();
            List<DataGridViewColumn> shown = columns.Where(col => (includeHidden || col.Visible) && !(col is DataGridViewImageColumn)).ToList();

            var data = new List<List<string>>();
            int rows = Math.Min(grid.Rows.Count, maxRows);
            for (int i = 0; i < rows; i++)
            {
                DataGridViewRow row = grid.Rows[i];
                data.Add(shown.Select(col => Short(CellText(row.Cells[col.Index]))).ToList());
            }

            return new
            {
                name = NameOf(grid),
                hidden,
                rows = grid.Rows.Count,
                rows_shown = rows < grid.Rows.Count ? rows : (int?)null,
                new_row = grid.AllowUserToAddRows && grid.NewRowIndex >= 0 ? grid.NewRowIndex : (int?)null,
                read_only = grid.ReadOnly ? true : (bool?)null,
                current_cell = grid.CurrentCell == null ? null : new[] { grid.CurrentCell.RowIndex, grid.CurrentCell.ColumnIndex },
                columns = shown.Select(col => new
                {
                    index = col.Index,
                    name = col.Name,
                    header = NullIfEmpty(col.HeaderText?.Trim()),
                    hidden = col.Visible ? (bool?)null : true,
                    read_only = col.ReadOnly ? true : (bool?)null,
                }).ToList(),
                data,
            };
        }

        private static void AddToolStripItems(ToolStripItemCollection items, string path, List<object> into, bool includeHidden)
        {
            foreach (ToolStripItem item in items)
            {
                if (item is ToolStripSeparator || (!item.Available && !includeHidden))
                {
                    continue;
                }

                string text = CleanText(item.Text);
                string itemPath = path.Length == 0 ? (text.Length > 0 ? text : item.Name ?? "") : path + " > " + (text.Length > 0 ? text : item.Name ?? "");
                bool clickable = item is ToolStripMenuItem || item is ToolStripButton || item is ToolStripDropDownItem;
                bool isLabel = item is ToolStripStatusLabel || item is ToolStripLabel;

                if (isLabel)
                {
                    if (text.Length > 0)
                    {
                        into.Add(new { path = itemPath, name = item.Name, kind = "status" });
                    }
                }
                else if (clickable)
                {
                    into.Add(new { path = itemPath, name = item.Name, disabled = item.Enabled ? (bool?)null : true });
                }

                if (item is ToolStripDropDownItem dd && dd.HasDropDownItems)
                {
                    AddToolStripItems(dd.DropDownItems, itemPath, into, includeHidden);
                }
            }
        }

        private static object ReadWin32(OpenDialog d)
        {
            List<CsdWindows.Win32Control> controls = CsdWindows.Win32Children(d.Handle).Where(c => c.Visible).ToList();
            int editNo = 0;
            return new
            {
                dialog = new { id = d.Id, title = d.Title, type = "windows dialog" },
                texts = controls.Where(c => c.Class == "Static" && !string.IsNullOrWhiteSpace(c.Text)).Select(c => Short(c.Text)).ToList(),
                edits = controls.Where(c => c.Class == "Edit").Select(c => new { name = "edit" + (++editNo).ToString(CultureInfo.InvariantCulture), id = c.Id, text = Short(c.Text) }).ToList(),
                buttons = controls.Where(c => c.Class == "Button" && !string.IsNullOrWhiteSpace(c.Text))
                    .Select(c => new { text = CleanText(c.Text), id = c.Id, disabled = c.Enabled ? (bool?)null : true }).ToList(),
            };
        }

        /// <summary>The VGE's volumes readout (hidden label lblVols on frmPopUp), parsed into numbers.</summary>
        public static object ReadVolumes(OpenDialog d)
        {
            if (d.Form == null)
            {
                throw new CsdException("Volumes are read from a Vertical Grading Editor window (frmPopUp).");
            }

            Form form = d.Form;
            string? text = CsdWindows.OnUiThread(form, () => FindByName(form, "lblVols")?.Text);
            if (text == null)
            {
                throw new CsdException($"'{d.Title}' has no volumes readout (lblVols). Use the Vertical Grading Editor window (frmPopUp).");
            }

            var items = new List<object>();
            double? net = null;
            string? netSide = null;
            foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Match m = Regex.Match(line.Trim(), @"^(-?[0-9][0-9,]*\.?[0-9]*)\s+(.+)$");
                if (!m.Success)
                {
                    continue;
                }

                double value = double.Parse(m.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture);
                string label = m.Groups[2].Value.Trim();
                items.Add(new { label, value });
                if (label.IndexOf("Total Vol", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    net = value;
                    netSide = label.StartsWith("CUT", StringComparison.OrdinalIgnoreCase) ? "cut" : label.StartsWith("FILL", StringComparison.OrdinalIgnoreCase) ? "fill" : null;
                }
            }

            return new
            {
                dialog = d.Title,
                volumes = items,
                net_surplus = net.HasValue ? new { side = netSide, volume = net } : null,
                raw = items.Count == 0 ? (string.IsNullOrWhiteSpace(text) ? "(empty: the models may not have been updated yet; press Rebuild Model or turn Auto Update on)" : Short(text)) : null,
            };
        }

        // ================================================================== changing values

        internal sealed class Change
        {
            public string? Control { get; set; }
            public string? Grid { get; set; }
            public int? Row { get; set; }
            public string? Column { get; set; }
            public string? Value { get; set; }
            public bool Select { get; set; }
            public string Commit { get; set; } = "auto";
        }

        /// <summary>Apply one change. Returns what the control shows afterwards.</summary>
        public static object Apply(OpenDialog d, Change change, int index)
        {
            if (d.Form == null)
            {
                return ApplyWin32(d, change, index);
            }

            Form form = d.Form;
            string what = change.Grid != null ? $"{change.Grid}[{change.Row}, {change.Column}]" : change.Control ?? "?";
            string? before = null;
            string? after = null;
            bool posted = false;

            LaunchResult r = CsdWindows.Launch(
                $"setting {what} in {d.Title}",
                a => form.BeginInvoke(a),
                () =>
                {
                    if (change.Grid != null)
                    {
                        DataGridView grid = FindGrid(form, change.Grid);
                        before = change.Select ? null : CellText(Cell(grid, change));
                        posted = SetGridCell(grid, change);
                        after = posted || change.Select ? null : CellText(Cell(grid, change));
                    }
                    else
                    {
                        Control c = FindControl(form, change.Control!);
                        before = ValueOf(c);
                        posted = SetControl(form, c, change);
                        after = posted ? null : ValueOf(c);
                    }
                },
                CsdWindows.DefaultTimeoutMs);

            if (r.Error != null)
            {
                return new { index, target = what, ok = false, error = r.Error.Message };
            }

            if (!r.Finished || r.Opened.Count > 0)
            {
                return new
                {
                    index,
                    target = what,
                    ok = false,
                    error = r.Opened.Count > 0
                        ? "CSD opened a dialog while applying this change (probably a message or a question). Read it with csd_read_dialog and answer it with csd_press."
                        : "CSD hasn't finished handling this change yet (it may still be recalculating).",
                    dialogs_opened = r.Opened.Select(o => new { id = o.Id, title = o.Title }).ToList(),
                };
            }

            if (posted)
            {
                // Enter was posted like a keypress; let CSD handle it, then read the result.
                Thread.Sleep(400);
                after = CsdWindows.OnUiThread(form, () =>
                    change.Grid != null ? CellText(Cell(FindGrid(form, change.Grid), change)) : ValueOf(FindControl(form, change.Control!)));
            }

            bool ok = change.Select || change.Value == null || ValueMatches(after, change.Value);
            return new
            {
                index,
                target = what,
                ok,
                before = change.Select ? null : before,
                now = change.Select ? null : after,
                selected = change.Select ? true : (bool?)null,
                note = ok ? null : "The control doesn't show the value that was set: CSD may have rejected or reformatted it.",
            };
        }

        private static bool SetGridCell(DataGridView grid, Change change)
        {
            DataGridViewCell cell = Cell(grid, change);
            grid.Focus();
            if (change.Select)
            {
                grid.CurrentCell = cell;
                grid.ClearSelection();
                cell.OwningRow.Selected = true;
                return false;
            }

            if (cell.ReadOnly)
            {
                throw new CsdException($"Cell [{change.Row}, {change.Column}] of {grid.Name} is read-only.");
            }

            grid.CurrentCell = cell;
            string value = change.Value ?? "";
            if (grid.BeginEdit(true) && grid.EditingControl != null)
            {
                grid.EditingControl.Text = value;
                grid.NotifyCurrentCellDirty(true);
                if (change.Commit == "enter")
                {
                    CsdWindows.PostEnter(grid.EditingControl.Handle);
                    return true;
                }

                if (!grid.EndEdit())
                {
                    grid.CancelEdit();
                    throw new CsdException($"CSD didn't accept '{value}' in {grid.Name} [{change.Row}, {change.Column}].");
                }
            }
            else
            {
                cell.Value = value;
            }

            return false;
        }

        /// <summary>Returns true when Enter was posted and the result must be read after a pause.</summary>
        private static bool SetControl(Form form, Control c, Change change)
        {
            string value = change.Value ?? "";
            switch (c)
            {
                case TextBoxBase tb:
                    if (tb.ReadOnly)
                    {
                        throw new CsdException($"{NameOf(c)} is read-only.");
                    }

                    RequireUsable(c);
                    tb.Focus();
                    tb.Text = value;
                    return Commit(form, c, change.Commit, defaultCommit: "tab");

                case ComboBox cb:
                {
                    RequireUsable(c);
                    int idx = -1;
                    for (int i = 0; i < cb.Items.Count; i++)
                    {
                        if (string.Equals((cb.GetItemText(cb.Items[i]) ?? "").Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            idx = i;
                            break;
                        }
                    }

                    cb.Focus();
                    if (idx >= 0)
                    {
                        cb.SelectedIndex = idx;
                        CallProtected(cb, "OnSelectionChangeCommitted", EventArgs.Empty);
                    }
                    else if (cb.DropDownStyle != ComboBoxStyle.DropDownList)
                    {
                        cb.Text = value;
                    }
                    else
                    {
                        throw new CsdException($"'{value}' is not one of the options of {NameOf(c)}.");
                    }

                    return Commit(form, c, change.Commit, defaultCommit: "tab");
                }

                case CheckBox chk:
                {
                    RequireUsable(c);
                    bool want = ParseBool(value, NameOf(c));
                    if (chk.Checked != want)
                    {
                        if (chk.AutoCheck)
                        {
                            CallProtected(chk, "OnClick", EventArgs.Empty); // toggles and raises Click, like a user click
                        }
                        else
                        {
                            chk.Checked = want;
                        }
                    }

                    return false;
                }

                case RadioButton rb:
                    RequireUsable(c);
                    if (!ParseBool(value, NameOf(c)))
                    {
                        throw new CsdException("Choose an option by setting the option you want to true.");
                    }

                    if (!rb.Checked)
                    {
                        rb.PerformClick();
                    }

                    return false;

                case NumericUpDown num:
                    RequireUsable(c);
                    if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d))
                    {
                        throw new CsdException($"{NameOf(c)} needs a number.");
                    }

                    num.Focus();
                    num.Value = Math.Min(num.Maximum, Math.Max(num.Minimum, d));
                    return Commit(form, c, change.Commit, defaultCommit: "tab");

                default:
                    throw new CsdException($"{NameOf(c)} is a {c.GetType().Name}; csd_set_values can set text boxes, choices, check boxes, options, numbers and grid cells. Use csd_press for buttons.");
            }
        }

        private static bool Commit(Form form, Control c, string commit, string defaultCommit)
        {
            string mode = commit == "auto" ? defaultCommit : commit;
            switch (mode)
            {
                case "enter":
                    CsdWindows.PostEnter(c.Handle);
                    return true;
                case "tab":
                    // Moving focus on raises Leave / Validating / Validated, as when the user tabs out.
                    form.SelectNextControl(c, true, true, true, true);
                    return false;
                default:
                    return false;
            }
        }

        private static object ApplyWin32(OpenDialog d, Change change, int index)
        {
            if (change.Grid != null)
            {
                throw new CsdException("Windows dialogs have no grids.");
            }

            List<CsdWindows.Win32Control> edits = CsdWindows.Win32Children(d.Handle).Where(c => c.Visible && c.Class == "Edit").ToList();
            string key = (change.Control ?? "edit1").Trim();
            CsdWindows.Win32Control? target = null;
            Match m = Regex.Match(key, @"^edit(\d+)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                target = n >= 1 && n <= edits.Count ? edits[n - 1] : null;
            }
            else if (int.TryParse(key, out int id))
            {
                target = edits.FirstOrDefault(e => e.Id == id);
            }

            if (target == null)
            {
                throw new CsdException($"No edit box '{key}' in '{d.Title}'. It has {edits.Count} edit box(es): edit1..edit{edits.Count}.");
            }

            CsdWindows.SetEditText(target.Handle, change.Value ?? "");
            string now = CsdWindows.Win32Children(d.Handle).FirstOrDefault(c => c.Handle == target.Handle)?.Text ?? "";
            return new { index, target = key, ok = now == (change.Value ?? ""), now };
        }

        // ================================================================== pressing

        /// <summary>Find what to press and return an action that presses it (run on the dialog's UI thread).</summary>
        public static Action PrepareWinFormsPress(Form form, string? control, string? menu, bool allowHidden, out string description)
        {
            if (menu != null)
            {
                ToolStripItem item = FindMenuItem(form, menu);
                description = $"menu '{menu}'";
                return () =>
                {
                    if (!item.Enabled)
                    {
                        throw new CsdException($"Menu item '{menu}' is disabled.");
                    }

                    item.PerformClick();
                };
            }

            Control c = FindControl(form, control!);
            description = NameOf(c);
            switch (c)
            {
                case ButtonBase btn when btn.Visible && btn.Enabled:
                    if (btn is Button b)
                    {
                        return b.PerformClick;
                    }

                    return () => CallProtected(btn, "OnClick", EventArgs.Empty);

                case ButtonBase btn when !btn.Enabled:
                    throw new CsdException($"{NameOf(c)} is disabled.");

                case ButtonBase btn:
                    if (!allowHidden)
                    {
                        throw new CsdException($"{NameOf(c)} is hidden. Hidden buttons (such as the VGE's legacy panel behind its ribbon) can only be pressed with allow_hidden: true.");
                    }

                    return () => CallProtected(btn, "OnClick", EventArgs.Empty);

                default:
                    throw new CsdException($"{NameOf(c)} is a {c.GetType().Name}, not a button. Use csd_set_values for fields and check boxes.");
            }
        }

        /// <summary>The visible button of a Win32 dialog matching a text or id.</summary>
        public static CsdWindows.Win32Control FindWin32Button(OpenDialog d, string control)
        {
            List<CsdWindows.Win32Control> buttons = CsdWindows.Win32Children(d.Handle).Where(c => c.Visible && c.Class == "Button").ToList();
            CsdWindows.Win32Control? hit = int.TryParse(control, out int id)
                ? buttons.FirstOrDefault(b => b.Id == id)
                : buttons.FirstOrDefault(b => string.Equals(CleanText(b.Text), CleanText(control), StringComparison.OrdinalIgnoreCase));
            return hit ?? throw new CsdException($"No button '{control}' in '{d.Title}'. Buttons: {string.Join(", ", buttons.Where(b => b.Text.Length > 0).Select(b => CleanText(b.Text)))}.");
        }

        /// <summary>Button texts of a Win32 dialog, for the read-only safety check.</summary>
        public static int Win32ButtonCount(OpenDialog d) =>
            CsdWindows.Win32Children(d.Handle).Count(c => c.Visible && c.Class == "Button" && c.Text.Length > 0);

        // ================================================================== finding controls

        public static Control FindControl(Form form, string key)
        {
            string k = key.Trim();
            Control? hit = FindByName(form, k);
            if (hit != null)
            {
                return hit;
            }

            List<Control> all = AllControls(form).ToList();
            hit = all.FirstOrDefault(c => (c is ButtonBase || c is GroupBox) && string.Equals(CleanText(c.Text), CleanText(k), StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(c => !(c is Label) && string.Equals(LabelFor(c), k, StringComparison.OrdinalIgnoreCase));
            return hit ?? throw new CsdException($"No control named '{key}' in '{form.Text}'. Read the dialog with csd_read_dialog for the names.");
        }

        public static Control? FindByName(Control parent, string name) =>
            AllControls(parent).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        private static IEnumerable<Control> AllControls(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                yield return c;
                foreach (Control child in AllControls(c))
                {
                    yield return child;
                }
            }
        }

        private static DataGridView FindGrid(Form form, string name) =>
            FindByName(form, name) as DataGridView ?? throw new CsdException($"No grid named '{name}' in '{form.Text}'.");

        private static DataGridViewCell Cell(DataGridView grid, Change change)
        {
            int row = change.Row ?? throw new CsdException("row is required for a grid change");
            if (row < 0 || row >= grid.Rows.Count)
            {
                throw new CsdException($"{grid.Name} has rows 0..{grid.Rows.Count - 1}; there is no row {row}.");
            }

            DataGridViewColumn? col;
            if (change.Column == null)
            {
                col = grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).OrderBy(c => c.DisplayIndex).FirstOrDefault();
            }
            else if (int.TryParse(change.Column, out int ci))
            {
                col = ci >= 0 && ci < grid.Columns.Count ? grid.Columns[ci] : null;
            }
            else
            {
                string key = change.Column.Trim();
                col = grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase))
                    ?? grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => string.Equals((c.HeaderText ?? "").Trim(), key, StringComparison.OrdinalIgnoreCase));
            }

            if (col == null)
            {
                throw new CsdException($"{grid.Name} has no column '{change.Column}'. Columns: {string.Join(", ", grid.Columns.Cast<DataGridViewColumn>().Select(c => $"{c.Index} {c.Name} \"{c.HeaderText}\""))}.");
            }

            return grid.Rows[row].Cells[col.Index];
        }

        private static ToolStripItem FindMenuItem(Form form, string menu) =>
            TryFindMenuItem(form, menu)
            ?? throw new CsdException($"No menu item '{menu}' in '{form.Text}'. Use a path such as \"File > Export Data\", or an item name from csd_read_dialog. " +
                "For a menu that drops down from a button, press the button first.");

        /// <summary>
        /// Find a menu item by path ("File > Export Data") or name, in the form's menus and tool
        /// strips, its context menus (including ones only held in fields), and any drop-down menu
        /// open right now (e.g. one a button just showed). Call on the form's UI thread.
        /// </summary>
        public static ToolStripItem? TryFindMenuItem(Form form, string menu)
        {
            string[] parts = menu.Split('>').Select(p => CleanText(p)).Where(p => p.Length > 0).ToArray();
            foreach (ToolStrip strip in MenuSources(form))
            {
                ToolStripItem? hit = parts.Length > 1 ? WalkPath(strip.Items, parts, 0) : FindItem(strip.Items, parts.Length == 1 ? parts[0] : menu);
                if (hit != null)
                {
                    return hit;
                }
            }

            return null;
        }

        private static List<ToolStrip> MenuSources(Form form)
        {
            var strips = new List<ToolStrip>();
            void Add(ToolStrip? t)
            {
                if (t != null && !t.IsDisposed && !strips.Contains(t))
                {
                    strips.Add(t);
                }
            }

            foreach (ToolStrip t in AllControls(form).OfType<ToolStrip>())
            {
                Add(t);
            }

            Add(form.ContextMenuStrip);
            foreach (Control c in AllControls(form))
            {
                Add(c.ContextMenuStrip);
            }

            // Context menus kept in the form's fields (VB "WithEvents" members) or its components.
            for (Type? t = form.GetType(); t != null && t != typeof(Form); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (typeof(ToolStrip).IsAssignableFrom(f.FieldType))
                    {
                        try
                        {
                            Add(f.GetValue(form) as ToolStrip);
                        }
                        catch (Exception)
                        {
                            // Unreadable field.
                        }
                    }
                }
            }

            foreach (ToolStripDropDown open in CsdWindows.OpenDropDowns())
            {
                Add(open);
            }

            return strips;
        }

        private static ToolStripItem? WalkPath(ToolStripItemCollection items, string[] parts, int i)
        {
            foreach (ToolStripItem item in items)
            {
                if (!Matches(item, parts[i]))
                {
                    continue;
                }

                if (i == parts.Length - 1)
                {
                    return item;
                }

                if (item is ToolStripDropDownItem dd)
                {
                    ToolStripItem? hit = WalkPath(dd.DropDownItems, parts, i + 1);
                    if (hit != null)
                    {
                        return hit;
                    }
                }
            }

            return null;
        }

        private static ToolStripItem? FindItem(ToolStripItemCollection items, string key)
        {
            foreach (ToolStripItem item in items)
            {
                if (!(item is ToolStripSeparator) && Matches(item, key))
                {
                    return item;
                }

                if (item is ToolStripDropDownItem dd)
                {
                    ToolStripItem? hit = FindItem(dd.DropDownItems, key);
                    if (hit != null)
                    {
                        return hit;
                    }
                }
            }

            return null;
        }

        private static bool Matches(ToolStripItem item, string key) =>
            string.Equals(item.Name, key, StringComparison.OrdinalIgnoreCase) || string.Equals(CleanText(item.Text), key, StringComparison.OrdinalIgnoreCase);

        // ================================================================== helpers

        /// <summary>The label to the left of (or just above) a control, in the same container.</summary>
        private static string? LabelFor(Control c)
        {
            if (c.Parent == null)
            {
                return null;
            }

            Label? best = null;
            int bestGap = int.MaxValue;
            foreach (Control other in c.Parent.Controls)
            {
                if (!(other is Label l) || string.IsNullOrWhiteSpace(l.Text))
                {
                    continue;
                }

                bool sameRow = l.Bottom > c.Top && l.Top < c.Bottom && l.Right <= c.Left + 8;
                bool above = l.Bottom <= c.Top + 4 && Math.Abs(l.Left - c.Left) < 30;
                int gap = sameRow ? c.Left - l.Right : above ? (c.Top - l.Bottom) + 1000 : int.MaxValue;
                if (gap < bestGap && gap < 1300)
                {
                    best = l;
                    bestGap = gap;
                }
            }

            return best == null ? null : CleanText(best.Text).TrimEnd(':').Trim();
        }

        private static string? TooltipFor(Form form, Control c)
        {
            try
            {
                // Tool tips live in a ToolTip component on the form, kept in the 'components' field.
                FieldInfo? f = form.GetType().GetField("components", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f?.GetValue(form) is System.ComponentModel.IContainer container)
                {
                    foreach (object comp in container.Components)
                    {
                        if (comp is ToolTip tip)
                        {
                            string? t = tip.GetToolTip(c);
                            if (!string.IsNullOrWhiteSpace(t))
                            {
                                return Short(t);
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // No tool tip.
            }

            return null;
        }

        private static string ValueOf(Control c)
        {
            switch (c)
            {
                case CheckBox chk: return chk.Checked ? "true" : "false";
                case RadioButton rb: return rb.Checked ? "true" : "false";
                case NumericUpDown num: return num.Value.ToString(CultureInfo.InvariantCulture);
                default: return c.Text ?? "";
            }
        }

        private static bool ValueMatches(string? now, string wanted)
        {
            if (now == null)
            {
                return false;
            }

            string a = now.Trim(), b = wanted.Trim();
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Numbers may come back reformatted (19.3 → 19.300).
            return double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                && Math.Abs(x - y) <= 0.0005 + 1e-9 * Math.Abs(y);
        }

        private static string CellText(DataGridViewCell cell)
        {
            object? v = cell.FormattedValue ?? cell.Value;
            return v is System.Drawing.Image ? "" : (v?.ToString() ?? "").Trim();
        }

        private static void RequireUsable(Control c)
        {
            if (!c.Visible)
            {
                throw new CsdException($"{NameOf(c)} is hidden (another tab or option may need to be selected first).");
            }

            if (!c.Enabled)
            {
                throw new CsdException($"{NameOf(c)} is disabled.");
            }
        }

        private static bool ParseBool(string v, string name)
        {
            switch (v.Trim().ToLowerInvariant())
            {
                case "true": case "yes": case "on": case "1": case "checked": return true;
                case "false": case "no": case "off": case "0": case "unchecked": return false;
                default: throw new CsdException($"{name} needs true or false.");
            }
        }

        private static void CallProtected(object target, string method, EventArgs args)
        {
            MethodInfo? m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(EventArgs) }, null);
            if (m == null)
            {
                throw new CsdException($"Can't call {method} on {target.GetType().Name}.");
            }

            m.Invoke(target, new object[] { args });
        }

        public static string CleanText(string? s) => (s ?? "").Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&").Replace("\r", " ").Replace("\n", " ").Trim();

        private static string NameOf(Control c) => string.IsNullOrEmpty(c.Name) ? c.GetType().Name : c.Name;

        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private static string Short(string? s)
        {
            string t = (s ?? "").Trim();
            return t.Length > MaxText ? t.Substring(0, MaxText) + "..." : t;
        }
    }
}
