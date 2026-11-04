using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Bricscad.ApplicationServices;

namespace CsdBridge.BricsCAD.Csd
{
    /// <summary>
    /// Gets ground levels from a CSD surface. CSD keeps its own surfaces, which the BricsCAD
    /// API can't read, so this makes a temporary BricsCAD TIN copy with CSD's Surface Exchange
    /// dialog, samples it along the centreline, then removes it again (or hides it).
    ///
    /// Runs on the agent's background thread and drives the dialogs the same way the csd_
    /// tools do. Only the exchange item and surface it created are ever removed.
    /// </summary>
    internal static class SurfaceExchange
    {
        public const string ExchangeForm = "AJEBfrmBricsCADSurfaceExchange";
        public const string AddItemForm = "AJEBfrmAddBricsCADExchange";
        private const string MenuPath = "CSD to BricsCAD Surface > Surface";
        private const string NamePrefix = "CSDBridge ";

        private sealed class Row
        {
            public int Index { get; set; }
            public string Conversion { get; set; } = "";
            public string BricsName { get; set; } = "";
            public string CsdName { get; set; } = "";

            public bool IsCsdToBricscad => Conversion.IndexOf("CSD to BricsCAD", StringComparison.OrdinalIgnoreCase) >= 0
                && Conversion.IndexOf("Volume", StringComparison.OrdinalIgnoreCase) < 0
                && Conversion.IndexOf("Merge", StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <param name="after">"remove" (default), "hide" or "keep": what to do with the BricsCAD copy afterwards.</param>
        public static object Sample(Document doc, string csdSurface, GroundSampler.Request req, string after, string? exchangeCommand)
        {
            var log = new List<string>();
            bool openedForm = false;
            bool addedRow = false;
            string? bricsName = null;
            OpenDialog exchange = FindOpen(ExchangeForm) ?? OpenExchange(exchangeCommand, log, out openedForm);

            try
            {
                Row? existing = ReadRows(exchange).FirstOrDefault(r => r.IsCsdToBricscad && string.Equals(r.CsdName, csdSurface, StringComparison.OrdinalIgnoreCase));
                if (existing != null && SurfaceExists(doc, existing.BricsName))
                {
                    bricsName = existing.BricsName;
                    log.Add($"Used the existing BricsCAD copy '{bricsName}' of {csdSurface}.");
                }
                else
                {
                    bricsName = NamePrefix + csdSurface;
                    AddRow(exchange, csdSurface, bricsName, log);
                    addedRow = true;
                    Build(exchange, log);
                    if (!SurfaceExists(doc, bricsName))
                    {
                        throw new CsdException($"Build/Update didn't create the BricsCAD surface '{bricsName}'. Check the Surface Exchange dialog (csd_read_dialog \"{ExchangeForm}\").");
                    }
                }

                req.Surface = bricsName;
                object sample = CsdWindows.OnMainThread(() => GroundSampler.Sample(doc, req));
                return new { csd_surface = csdSurface, sample, steps = log };
            }
            finally
            {
                if (addedRow && bricsName != null)
                {
                    CleanUp(doc, exchange, bricsName, after, log);
                }

                if (openedForm)
                {
                    Try(() => Press(exchange, "btnClose", 15000, null), log, "close the Surface Exchange dialog");
                }
            }
        }

        // ------------------------------------------------------------------ steps

        private static OpenDialog OpenExchange(string? commandName, List<string> log, out bool opened)
        {
            CsdCommand cmd = FindCommand(commandName);
            Action work = CsdCommands.Prepare(cmd, cmd.Method != null ? "direct" : "command_line");
            LaunchResult r = CsdWindows.Launch($"CSD command {cmd.Name}", CsdWindows.MainPoster(), work, 30000);
            if (r.Error != null)
            {
                throw new CsdException($"{cmd.Name} failed: {r.Error.Message}");
            }

            OpenDialog d = FindOpen(ExchangeForm)
                ?? throw new CsdException($"{cmd.Name} didn't open the Surface Exchange dialog" +
                    (r.Finished ? "." : " (it may be waiting for input at the command line).") +
                    " Open it yourself (Surfaces tab > BricsCAD Surfaces) and ask again, or name the command with exchange_command.");
            log.Add($"Opened Surface Exchange ({cmd.Name}).");
            opened = true;
            return d;
        }

        /// <summary>The command that opens Surface Exchange: named, or found from command names and ribbon labels.</summary>
        private static CsdCommand FindCommand(string? name)
        {
            Dictionary<string, CsdCommand> all = CsdCommands.All;
            if (!string.IsNullOrWhiteSpace(name))
            {
                return all.TryGetValue(name!.Trim().TrimStart('_', '.').ToUpperInvariant(), out CsdCommand? named)
                    ? named
                    : throw new CsdException($"'{name}' is not a CSD command known to this installation (see csd_commands).");
            }

            int Score(CsdCommand c)
            {
                string n = c.Name;
                string buttons = string.Join(" | ", c.Buttons);
                int s = 0;
                if (n.Contains("SURFACEEXCHANGE") || n.Contains("BRICSCADSURFACE") || n.Contains("BRICSSURFACE"))
                {
                    s += 3;
                }

                if (n.Contains("EXCHANGE") && n.Contains("SURF"))
                {
                    s += 2;
                }

                if (buttons.IndexOf("Surface Exchange", StringComparison.OrdinalIgnoreCase) >= 0 || buttons.IndexOf("BricsCAD Surface", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    s += 2;
                }

                if (n.Contains("STYLE") || buttons.IndexOf("Style", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    s -= 3;
                }

                return s;
            }

            CsdCommand? best = all.Values.Select(c => new { c, s = Score(c) }).Where(x => x.s > 0)
                .OrderByDescending(x => x.s).ThenBy(x => x.c.Method == null ? 1 : 0).Select(x => x.c).FirstOrDefault();
            return best ?? throw new CsdException(
                "Couldn't find the command that opens CSD's Surface Exchange. Open it yourself (Surfaces tab > BricsCAD Surfaces) and ask again, " +
                "or give exchange_command (run AICSD > List to see the commands).");
        }

        private static void AddRow(OpenDialog exchange, string csdSurface, string bricsName, List<string> log)
        {
            Form form = exchange.Form!;

            // The menu drops down from "Add Surface Exchange". It may exist only once shown.
            bool found = CsdWindows.OnUiThread(form, () => CsdDialogs.TryFindMenuItem(form, MenuPath) != null);
            if (!found)
            {
                CsdWindows.OnUiThread(form, () =>
                {
                    ((Button)RequireControl(form, "btnAddExchange")).PerformClick();
                    return true;
                });
                Thread.Sleep(400);
            }

            LaunchResult opened = CsdWindows.Launch(
                "adding a Surface Exchange item",
                a => form.BeginInvoke(a),
                () =>
                {
                    ToolStripItem item = CsdDialogs.TryFindMenuItem(form, MenuPath)
                        ?? throw new CsdException($"The Surface Exchange menu has no '{MenuPath}' item.");

                    // Close the drop-down first: the click opens a modal dialog.
                    foreach (ToolStripDropDown dd in CsdWindows.OpenDropDowns())
                    {
                        dd.Close(ToolStripDropDownCloseReason.CloseCalled);
                    }

                    item.PerformClick();
                },
                20000);
            if (opened.Error != null)
            {
                throw new CsdException(opened.Error.Message);
            }

            OpenDialog add = opened.Opened.FirstOrDefault(o => o.Name == AddItemForm) ?? FindOpen(AddItemForm)
                ?? throw new CsdException("The Add Item dialog didn't open.");
            Form addForm = add.Form!;

            // Fill in the surface and name.
            string? problem = CsdWindows.OnUiThread(addForm, () =>
            {
                var source = (ComboBox)RequireControl(addForm, "cboSource");
                List<string> items = source.Items.Cast<object>().Select(i => (source.GetItemText(i) ?? "").Trim()).ToList();
                int idx = items.FindIndex(i => string.Equals(i, csdSurface.Trim(), StringComparison.OrdinalIgnoreCase));
                if (idx < 0)
                {
                    return $"There is no CSD surface '{csdSurface}'. CSD surfaces in this drawing: {string.Join(", ", items)}.";
                }

                source.Focus();
                source.SelectedIndex = idx;
                InvokeProtected(source, "OnSelectionChangeCommitted");
                RequireControl(addForm, "txtSurfaceName").Text = bricsName;
                if (CsdDialogs.FindByName(addForm, "txtConversionName") is TextBox conversion && conversion.Visible)
                {
                    conversion.Text = bricsName;
                }

                return null;
            });

            if (problem != null)
            {
                CsdWindows.OnUiThread(addForm, () =>
                {
                    addForm.Close();
                    return true;
                });
                throw new CsdException(problem);
            }

            LaunchResult added = CsdWindows.Launch("adding the item", a => addForm.BeginInvoke(a), () => ((Button)RequireControl(addForm, "btnAddExchange")).PerformClick(), 30000);
            ThrowIfMessage(added, "adding the Surface Exchange item");
            if (FindOpen(AddItemForm) != null)
            {
                throw new CsdException("The Add Item dialog is still open after Add Item; CSD may not have accepted the values. Read it with csd_read_dialog.");
            }

            if (!ReadRows(exchange).Any(r => string.Equals(r.BricsName, bricsName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new CsdException("CSD didn't add the Surface Exchange item.");
            }

            log.Add($"Added a CSD to BricsCAD exchange: {csdSurface} → '{bricsName}'.");
        }

        private static void Build(OpenDialog exchange, List<string> log)
        {
            LaunchResult r = Press(exchange, "btnRefresh", 600000, "building the BricsCAD surface");
            ThrowIfMessage(r, "Build/Update");
            if (!r.Finished)
            {
                throw new CsdException("Build/Update is still running after 10 minutes.");
            }

            log.Add("Built the BricsCAD surface (Build/Update).");
        }

        private static void CleanUp(Document doc, OpenDialog exchange, string bricsName, string after, List<string> log)
        {
            switch (after)
            {
                case "keep":
                    log.Add($"Kept the BricsCAD surface '{bricsName}' and its exchange item.");
                    return;

                case "hide":
                    Try(() => CsdWindows.OnMainThread(() => GroundSampler.HideSurface(doc, bricsName)), log, "hide the surface");
                    log.Add($"Hid the BricsCAD surface '{bricsName}' on layer {GroundSampler.HiddenLayer} (off, frozen, not plotted); its exchange item is kept so it can be reused.");
                    return;

                default:
                    Try(() => RemoveRow(exchange, bricsName), log, "remove the exchange item");
                    Try(() => CsdWindows.OnMainThread(() => GroundSampler.EraseSurface(doc, bricsName)), log, "erase the surface");
                    log.Add($"Removed the temporary BricsCAD surface '{bricsName}' and its exchange item.");
                    return;
            }
        }

        private static bool RemoveRow(OpenDialog exchange, string bricsName)
        {
            Form form = exchange.Form!;
            Row row = ReadRows(exchange).FirstOrDefault(r => string.Equals(r.BricsName, bricsName, StringComparison.OrdinalIgnoreCase))
                ?? throw new CsdException("The exchange item has gone already.");

            CsdWindows.OnUiThread(form, () =>
            {
                var grid = (DataGridView)RequireControl(form, "dgvSurfaceList");
                grid.Focus();
                DataGridViewCell? cell = grid.Rows[row.Index].Cells.Cast<DataGridViewCell>().FirstOrDefault(c => c.Visible);
                if (cell != null)
                {
                    grid.CurrentCell = cell;
                }

                grid.ClearSelection();
                grid.Rows[row.Index].Selected = true;
                return true;
            });

            LaunchResult r = Press(exchange, "btnRemoveItem", 30000, "removing the exchange item");

            // A confirmation for the item we added: answer Yes.
            foreach (OpenDialog d in r.Opened.Where(o => o.Form == null))
            {
                CsdWindows.Win32Control? yes = CsdWindows.Win32Children(d.Handle)
                    .FirstOrDefault(c => c.Visible && c.Class == "Button" && (CsdDialogs.CleanText(c.Text) == "Yes" || CsdDialogs.CleanText(c.Text) == "OK"));
                if (yes != null)
                {
                    CsdWindows.ClickButton(yes.Handle);
                    Thread.Sleep(500);
                }
            }

            return true;
        }

        // ------------------------------------------------------------------ helpers

        private static OpenDialog? FindOpen(string formName) =>
            CsdWindows.List().FirstOrDefault(o => string.Equals(o.Name, formName, StringComparison.OrdinalIgnoreCase));

        private static List<Row> ReadRows(OpenDialog exchange)
        {
            Form form = exchange.Form!;
            return CsdWindows.OnUiThread(form, () =>
            {
                var grid = (DataGridView)RequireControl(form, "dgvSurfaceList");
                int Col(string header, int fallback)
                {
                    DataGridViewColumn? c = grid.Columns.Cast<DataGridViewColumn>()
                        .FirstOrDefault(x => (x.HeaderText ?? "").IndexOf(header, StringComparison.OrdinalIgnoreCase) >= 0);
                    return c?.Index ?? (fallback < grid.Columns.Count ? fallback : -1);
                }

                int conversion = Col("Conversion", 3), brics = Col("BricsCAD Surface", 4), csd = Col("CSD Surface", 6);
                string Text(DataGridViewRow r, int i) => i < 0 ? "" : (r.Cells[i].FormattedValue?.ToString() ?? "").Trim();
                return grid.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).Select(r => new Row
                {
                    Index = r.Index,
                    Conversion = Text(r, conversion),
                    BricsName = Text(r, brics),
                    CsdName = Text(r, csd),
                }).ToList();
            });
        }

        private static bool SurfaceExists(Document doc, string name) =>
            !string.IsNullOrWhiteSpace(name)
            && CsdWindows.OnMainThread(() => GroundSampler.SurfaceNames(doc)).Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

        private static LaunchResult Press(OpenDialog d, string button, int waitMs, string? what)
        {
            Form form = d.Form!;
            return CsdWindows.Launch(what ?? $"pressing {button}", a => form.BeginInvoke(a), () => ((Button)RequireControl(form, button)).PerformClick(), waitMs);
        }

        private static void ThrowIfMessage(LaunchResult r, string step)
        {
            if (r.Error != null)
            {
                throw new CsdException($"{step} failed: {r.Error.Message}");
            }

            OpenDialog? message = r.Opened.FirstOrDefault(o => o.Form == null);
            if (message != null)
            {
                string text = string.Join(" ", CsdWindows.Win32Children(message.Handle).Where(c => c.Visible && c.Class == "Static" && c.Text.Length > 0).Select(c => c.Text));
                throw new CsdException($"CSD showed a message during {step}: \"{message.Title}: {text}\". It is still open: read it with csd_read_dialog \"{message.Id}\" and answer it with csd_press.");
            }
        }

        private static Control RequireControl(Form form, string name) =>
            CsdDialogs.FindByName(form, name) ?? throw new CsdException($"'{form.Text}' has no control '{name}'; this CSD version's Surface Exchange may differ.");

        private static void InvokeProtected(Control c, string method)
        {
            c.GetType().GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(EventArgs) }, null)
                ?.Invoke(c, new object[] { EventArgs.Empty });
        }

        private static void Try(Func<object> action, List<string> log, string what)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                log.Add($"Couldn't {what}: {ex.Message}");
            }
        }

        private static void Try(Action action, List<string> log, string what) => Try(() =>
        {
            action();
            return true;
        }, log, what);
    }
}
