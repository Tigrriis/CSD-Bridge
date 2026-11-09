using System;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bricscad.ApplicationServices;
using Bricscad.Ribbon;
using Bricscad.Windows;
using CadApp = Bricscad.ApplicationServices.Application;

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// Adds a "CSD-Bridge" tab to the BricsCAD ribbon:
    ///   Claude panel:   Chat | Draft | History   (large)    New chat / Status / API key   (small)
    ///   Vehicles panel: Turn path (large)
    /// The ribbon is optional: if it is turned off, or BricsCAD rebuilds it (e.g. a
    /// workspace switch), the commands still work and AIRIBBON adds the tab again.
    ///
    /// BricsCAD exposes its ribbon through ComponentManager.Ribbon. RibbonServices'
    /// RibbonPaletteSet (the AutoCAD route) can be null even while the ribbon shows.
    /// </summary>
    internal static class RibbonSetup
    {
        private const string TabId = "CSDBRIDGE_TAB";
        private static bool _waitingForRibbon;
        private static bool _waitingForIdle;
        private static bool _watchingWorkspace;

        /// <summary>Called at startup. Adds the tab now, or as soon as the ribbon exists.</summary>
        public static void Initialize()
        {
            try
            {
                if (!_watchingWorkspace)
                {
                    _watchingWorkspace = true;
                    CadApp.SystemVariableChanged += OnSystemVariableChanged;
                }

                if (GetRibbon() != null)
                {
                    AddTab();
                    return;
                }

                // Not built yet (e.g. loaded at startup): try again once BricsCAD is idle,
                // and when the ribbon palette is created.
                if (!_waitingForIdle)
                {
                    _waitingForIdle = true;
                    CadApp.Idle += OnFirstIdle;
                }

                if (!_waitingForRibbon)
                {
                    _waitingForRibbon = true;
                    RibbonServices.RibbonPaletteSetCreated += OnRibbonCreated;
                }
            }
            catch (Exception)
            {
                // The ribbon is a convenience; never let it stop the add-in loading.
            }
        }

        /// <summary>
        /// Add the tab if it isn't there. With <paramref name="activate"/>, also switch the
        /// ribbon to it (used by AIRIBBON). Returns a message for the command line.
        /// </summary>
        public static string AddTab(bool activate = false)
        {
            try
            {
                RibbonControl? ribbon = GetRibbon();
                if (ribbon == null)
                {
                    return "BricsCAD didn't give the add-in access to the ribbon. If the ribbon is off, turn it on with RIBBON and run AIRIBBON again.";
                }

                RibbonTab? existing = ribbon.FindTab(TabId);
                if (existing != null)
                {
                    existing.IsVisible = true;
                    if (activate)
                    {
                        ribbon.ActiveTab = existing;
                    }

                    return "the CSD-Bridge ribbon tab is already there" + (activate ? " (switched to it)." : ".");
                }

                var source = new RibbonPanelSource { Id = "CSDBRIDGE_PANEL", Name = "Claude", Title = "Claude" };
                source.Items.Add(Button("Chat", "AIPANEL", "Open the Claude chat panel", "chat", large: true));
                source.Items.Add(Button("Draft", "AIDRAFT", "Ask Claude to draw or change something (command line)", "draft", large: true));
                source.Items.Add(Button("History", "AIHISTORY", "Every request to Claude and what it did", "history", large: true));

                var stack = new RibbonRowPanel();
                stack.Items.Add(Button("New chat", "AIRESET", "Start a new conversation for this drawing", "newchat", large: false));
                stack.Items.Add(new RibbonRowBreak());
                stack.Items.Add(Button("Status", "AISTATUS", "Show the model, API key source and settings", "status", large: false));
                stack.Items.Add(new RibbonRowBreak());
                stack.Items.Add(Button("API key", "AIKEY", "Set or remove the Anthropic API key", "key", large: false));
                source.Items.Add(stack);

                var vehicles = new RibbonPanelSource { Id = "CSDBRIDGE_VEHICLES", Name = "Vehicles", Title = "Vehicles" };
                vehicles.Items.Add(Button("Turn path", "AITURN", "Draw an AS/NZS 2890.1 B85 or B99 swept path: a turning template, or a vehicle driven along a line you pick", "turnpath", large: true));

                var tab = new RibbonTab { Id = TabId, Name = "CSD-Bridge", Title = "CSD-Bridge" };
                tab.Panels.Add(new RibbonPanel { Source = source });
                tab.Panels.Add(new RibbonPanel { Source = vehicles });
                ribbon.Tabs.Add(tab);
                if (activate)
                {
                    ribbon.ActiveTab = tab;
                }

                return "added the CSD-Bridge ribbon tab" + (activate ? " and switched to it." : ".");
            }
            catch (Exception ex)
            {
                return "could not add the ribbon tab: " + ex.Message;
            }
        }

        private static RibbonControl? GetRibbon()
        {
            try
            {
                RibbonControl? ribbon = ComponentManager.Ribbon;
                if (ribbon != null)
                {
                    return ribbon;
                }
            }
            catch (Exception)
            {
                // fall through to the AutoCAD-style route
            }

            try
            {
                return RibbonServices.RibbonPaletteSet?.RibbonControl;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void OnFirstIdle(object? sender, EventArgs e)
        {
            CadApp.Idle -= OnFirstIdle;
            _waitingForIdle = false;
            if (GetRibbon() != null)
            {
                AddTab();
            }
        }

        private static void OnRibbonCreated(object? sender, EventArgs e)
        {
            RibbonServices.RibbonPaletteSetCreated -= OnRibbonCreated;
            _waitingForRibbon = false;
            AddTab();
        }

        /// <summary>A workspace switch rebuilds the ribbon without our tab; put it back.</summary>
        private static void OnSystemVariableChanged(object? sender, SystemVariableChangedEventArgs e)
        {
            if (string.Equals(e.Name, "WSCURRENT", StringComparison.OrdinalIgnoreCase))
            {
                AddTab();
            }
        }

        private static RibbonButton Button(string text, string command, string toolTip, string icon, bool large)
        {
            var button = new RibbonButton
            {
                Id = "CSDBRIDGE_" + command,
                Name = text,
                Text = text,
                ToolTip = toolTip,
                ShowText = true,
                Size = large ? RibbonItemSize.Large : RibbonItemSize.Standard,
                ButtonStyle = large ? RibbonButtonStyle.LargeWithText : RibbonButtonStyle.SmallWithText,
                CommandHandler = CommandHandler.Instance,
                CommandParameter = command,
            };

            ImageSource? small = LoadIcon(icon, 16);
            ImageSource? big = LoadIcon(icon, 32);
            button.ShowImage = small != null || big != null;
            if (small != null)
            {
                button.Image = small;
            }

            if (big != null)
            {
                button.LargeImage = big;
            }

            return button;
        }

        /// <summary>Icons are embedded PNGs (see tools/icons/make_icons.py).</summary>
        private static ImageSource? LoadIcon(string name, int size)
        {
            try
            {
                Stream? stream = typeof(RibbonSetup).Assembly.GetManifestResourceStream($"CsdBridge.BricsCAD.Resources.{name}_{size}.png");
                if (stream == null)
                {
                    return null;
                }

                using (stream)
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch (Exception)
            {
                return null; // text-only button
            }
        }

        /// <summary>Runs a ribbon button's command.</summary>
        private sealed class CommandHandler : ICommand
        {
            public static readonly CommandHandler Instance = new CommandHandler();

            public event EventHandler? CanExecuteChanged
            {
                add { }
                remove { }
            }

            public bool CanExecute(object? parameter) => true;

            public void Execute(object? parameter)
            {
                try
                {
                    switch (parameter as string)
                    {
                        // Windows open directly, so they also work while a request is running.
                        case "AIPANEL":
                            ChatPalette.Show();
                            return;
                        case "AIHISTORY":
                            HistoryWindow.ShowWindow();
                            return;
                        case string command:
                            Document? doc = CadApp.DocumentManager.MdiActiveDocument;
                            doc?.SendStringToExecute(command + " ", true, false, true);
                            return;
                    }
                }
                catch (Exception)
                {
                    // A failed click shouldn't take anything else down.
                }
            }
        }
    }
}
