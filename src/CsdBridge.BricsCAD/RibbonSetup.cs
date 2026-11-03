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
    ///   Chat | Draft | History   (large)    New chat / Status / API key   (small)
    /// The ribbon is optional: if it is turned off, or BricsCAD rebuilds it (e.g. a
    /// workspace switch), the commands still work and AIRIBBON adds the tab again.
    /// </summary>
    internal static class RibbonSetup
    {
        private const string TabId = "CSDBRIDGE_TAB";
        private static bool _waitingForRibbon;
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

                if (RibbonServices.RibbonPaletteSet != null)
                {
                    AddTab();
                }
                else if (!_waitingForRibbon)
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

        /// <summary>Add the tab if it isn't there. Returns a message for the command line.</summary>
        public static string AddTab()
        {
            try
            {
                RibbonControl? ribbon = RibbonServices.RibbonPaletteSet?.RibbonControl;
                if (ribbon == null)
                {
                    return "the ribbon isn't showing. Turn it on with the RIBBON command, then run AIRIBBON.";
                }

                if (ribbon.FindTab(TabId) != null)
                {
                    return "the CSD-Bridge ribbon tab is already there.";
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

                var tab = new RibbonTab { Id = TabId, Name = "CSD-Bridge", Title = "CSD-Bridge" };
                tab.Panels.Add(new RibbonPanel { Source = source });
                ribbon.Tabs.Add(tab);
                return "added the CSD-Bridge ribbon tab.";
            }
            catch (Exception ex)
            {
                return "could not add the ribbon tab: " + ex.Message;
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
