using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Bricscad.ApplicationServices;
using CadApp = Bricscad.ApplicationServices.Application;

namespace CsdBridge.BricsCAD.Csd
{
    /// <summary>A CSD command: from a [CommandMethod] in CSD's assemblies and/or a button in CSD's menu file.</summary>
    internal sealed class CsdCommand
    {
        public CsdCommand(string name)
        {
            Name = name;
        }

        /// <summary>The typed command name (upper case).</summary>
        public string Name { get; }

        public MethodInfo? Method { get; set; }
        public string? Flags { get; set; }

        /// <summary>Ribbon/menu button labels that run this command.</summary>
        public List<string> Buttons { get; } = new List<string>();

        public string? Help { get; set; }
    }

    /// <summary>
    /// Finds the commands this CSD installation defines, so Claude works from real names
    /// rather than guesses, and runs them.
    /// </summary>
    internal static class CsdCommands
    {
        private static readonly object Gate = new object();
        private static Dictionary<string, CsdCommand>? _commands;
        private static List<string> _sources = new List<string>();
        private static readonly Dictionary<Type, object> Instances = new Dictionary<Type, object>();

        public static IReadOnlyList<string> Sources
        {
            get
            {
                Load();
                return _sources;
            }
        }

        public static Dictionary<string, CsdCommand> All
        {
            get
            {
                Load();
                return _commands!;
            }
        }

        /// <summary>Forget the list (e.g. after CSD finished loading).</summary>
        public static void Refresh()
        {
            lock (Gate)
            {
                _commands = null;
            }
        }

        private static void Load()
        {
            lock (Gate)
            {
                if (_commands != null && _commands.Count > 0)
                {
                    return;
                }

                var commands = new Dictionary<string, CsdCommand>(StringComparer.OrdinalIgnoreCase);
                var sources = new List<string>();
                ScanAssemblies(commands, sources);
                ScanMenus(commands, sources);
                _commands = commands;
                _sources = sources;
            }
        }

        private static void ScanAssemblies(Dictionary<string, CsdCommand> commands, List<string> sources)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = asm.GetName().Name ?? "";
                if (name.StartsWith("CsdBridge", StringComparison.OrdinalIgnoreCase)
                    || !(name.StartsWith("CSD", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ARD", StringComparison.OrdinalIgnoreCase) || name.IndexOf("VbMgdAcad", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    continue;
                }

                Type?[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                catch (Exception)
                {
                    continue;
                }

                int found = 0;
                foreach (Type? t in types)
                {
                    if (t == null)
                    {
                        continue;
                    }

                    MethodInfo[] methods;
                    try
                    {
                        methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    foreach (MethodInfo m in methods)
                    {
                        object[] attrs;
                        try
                        {
                            attrs = m.GetCustomAttributes(false);
                        }
                        catch (Exception)
                        {
                            continue;
                        }

                        foreach (object a in attrs)
                        {
                            if (a.GetType().Name != "CommandMethodAttribute")
                            {
                                continue;
                            }

                            string? global = a.GetType().GetProperty("GlobalName")?.GetValue(a) as string;
                            if (string.IsNullOrWhiteSpace(global) || m.GetParameters().Length != 0)
                            {
                                continue;
                            }

                            CsdCommand cmd = GetOrAdd(commands, global!);
                            cmd.Method ??= m;
                            cmd.Flags = a.GetType().GetProperty("Flags")?.GetValue(a)?.ToString();
                            found++;
                        }
                    }
                }

                if (found > 0)
                {
                    sources.Add($"{name} {asm.GetName().Version}: {found} commands");
                }
            }
        }

        /// <summary>Read button labels and macros from CSD's menu files (C:\ProgramData\CSS\ARD\Menu\*.cui / *.cuix).</summary>
        private static void ScanMenus(Dictionary<string, CsdCommand> commands, List<string> sources)
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CSS", "ARD", "Menu");
            if (!Directory.Exists(folder))
            {
                return;
            }

            IEnumerable<string> files = Directory.GetFiles(folder, "*.cui").Concat(Directory.GetFiles(folder, "*.cuix"));

            // Prefer the menu for this BricsCAD version (CSDBricsV26.cui) when there are several.
            string version = CadApp.Version.Major.ToString(System.Globalization.CultureInfo.InvariantCulture);
            List<string> list = files.ToList();
            List<string> preferred = list.Where(f => Path.GetFileName(f).IndexOf("Brics", StringComparison.OrdinalIgnoreCase) >= 0 && Path.GetFileName(f).Contains(version)).ToList();
            if (preferred.Count > 0)
            {
                list = preferred;
            }

            foreach (string file in list)
            {
                try
                {
                    int found = 0;
                    foreach (XDocument doc in ReadMenuXml(file))
                    {
                        foreach (XElement macro in doc.Descendants().Where(e => e.Name.LocalName == "MenuMacro"))
                        {
                            string? label = macro.Descendants().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value?.Trim();
                            string? text = macro.Descendants().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value;
                            string? help = macro.Descendants().FirstOrDefault(e => e.Name.LocalName == "HelpString")?.Value?.Trim();
                            string? command = CommandOfMacro(text);
                            if (command == null)
                            {
                                continue;
                            }

                            CsdCommand cmd = GetOrAdd(commands, command);
                            if (!string.IsNullOrWhiteSpace(label) && !cmd.Buttons.Contains(label!))
                            {
                                cmd.Buttons.Add(label!);
                            }

                            if (string.IsNullOrWhiteSpace(cmd.Help) && !string.IsNullOrWhiteSpace(help))
                            {
                                cmd.Help = help;
                            }

                            found++;
                        }
                    }

                    sources.Add($"{Path.GetFileName(file)}: {found} menu macros");
                }
                catch (Exception ex)
                {
                    sources.Add($"{Path.GetFileName(file)}: could not be read ({ex.Message})");
                }
            }
        }

        private static IEnumerable<XDocument> ReadMenuXml(string file)
        {
            if (file.EndsWith(".cuix", StringComparison.OrdinalIgnoreCase))
            {
                using (ZipArchive zip = ZipFile.OpenRead(file))
                {
                    var docs = new List<XDocument>();
                    foreach (ZipArchiveEntry e in zip.Entries.Where(e => e.FullName.EndsWith(".cui", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                    {
                        using (Stream s = e.Open())
                        {
                            docs.Add(XDocument.Load(s));
                        }
                    }

                    return docs;
                }
            }

            return new[] { XDocument.Load(file) };
        }

        /// <summary>The command a menu macro runs: "^C^C_ARDUV7FOO;" → ARDUV7FOO. Null for macros that aren't a plain command.</summary>
        internal static string? CommandOfMacro(string? macro)
        {
            if (string.IsNullOrWhiteSpace(macro))
            {
                return null;
            }

            string m = macro!.Trim();
            while (m.StartsWith("^C", StringComparison.OrdinalIgnoreCase) || m.StartsWith("^c"))
            {
                m = m.Substring(2);
            }

            m = m.TrimStart('*', '\'', '_', '.', ' ', '-').TrimStart('_', '.');
            Match match = Regex.Match(m, @"^[A-Za-z][A-Za-z0-9_\-]*");
            if (!match.Success || m.StartsWith("(", StringComparison.Ordinal))
            {
                return null;
            }

            return match.Value.ToUpperInvariant();
        }

        private static CsdCommand GetOrAdd(Dictionary<string, CsdCommand> commands, string name)
        {
            string key = name.Trim().ToUpperInvariant();
            if (!commands.TryGetValue(key, out CsdCommand? cmd))
            {
                cmd = new CsdCommand(key);
                commands[key] = cmd;
            }

            return cmd;
        }

        // ------------------------------------------------------------------ running

        /// <summary>
        /// Run a command on BricsCAD's main thread, through the agent's main-thread pump.
        /// "direct" calls the command's method (works inside the running AI request);
        /// "command_line" runs it through BricsCAD's command processor (Editor.Command).
        /// </summary>
        public static Action Prepare(CsdCommand cmd, string method)
        {
            Document doc = CadApp.DocumentManager.MdiActiveDocument ?? throw new CsdException("No drawing is open.");
            if (method == "direct")
            {
                MethodInfo m = cmd.Method ?? throw new CsdException($"{cmd.Name} comes from CSD's menu, not a .NET command; run it with method \"command_line\".");
                return () =>
                {
                    object? target = null;
                    if (!m.IsStatic)
                    {
                        Type t = m.DeclaringType!;
                        lock (Instances)
                        {
                            if (!Instances.TryGetValue(t, out target))
                            {
                                target = Activator.CreateInstance(t, nonPublic: true)!;
                                Instances[t] = target;
                            }
                        }
                    }

                    m.Invoke(target, null);
                };
            }

            return () => doc.Editor.Command(new object[] { cmd.Name });
        }
    }
}
