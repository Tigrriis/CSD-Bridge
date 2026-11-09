using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Bricscad.ApplicationServices;
using Teigha.Runtime;
using CadApp = Bricscad.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(CsdBridge.BricsCAD.PluginApp))]

namespace CsdBridge.BricsCAD
{
    /// <summary>Entry point BricsCAD calls when the DLL is loaded (NETLOAD or autoload).</summary>
    public sealed class PluginApp : IExtensionApplication
    {
        public void Initialize()
        {
            // Must run before anything touches the Anthropic SDK or its dependencies.
            DependencyResolver.Install();
            Startup();
        }

        public void Terminate()
        {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Startup()
        {
            DocumentCollection docs = CadApp.DocumentManager;
            docs.DocumentToBeDestroyed += (s, e) => AgentRunner.Forget(e.Document);
            docs.DocumentActivated += (s, e) => ChatPalette.OnDocumentActivated(e.Document);

            RibbonSetup.Initialize();

            docs.MdiActiveDocument?.Editor.WriteMessage(
                "\nCSD-Bridge loaded. Commands: AIDRAFT, AIPANEL, AIHISTORY, AITURN, AIRESET, AIKEY, AISTATUS (or use the CSD-Bridge ribbon tab).\n");
        }
    }

    /// <summary>
    /// On .NET Framework (BricsCAD V24/V25) every add-in shares one AppDomain and the
    /// add-in's own binding redirects are ignored, so the SDK's dependencies
    /// (System.Text.Json, System.Memory, ...) can fail to bind to the versions we ship.
    /// This resolves them from the add-in folder by simple name. On .NET 8 (V26) the
    /// .deps.json next to the DLL does this job, so the handler is not installed.
    /// </summary>
    internal static class DependencyResolver
    {
#if NETFRAMEWORK
        private static bool _installed;
#endif

        public static void Install()
        {
#if NETFRAMEWORK
            if (_installed)
            {
                return;
            }

            _installed = true;
            string folder = Path.GetDirectoryName(typeof(DependencyResolver).Assembly.Location) ?? ".";
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string name = new AssemblyName(args.Name).Name ?? "";
                string candidate = Path.Combine(folder, name + ".dll");
                if (!File.Exists(candidate))
                {
                    return null;
                }

                // Reuse a copy that is already loaded (acts like a binding redirect).
                Assembly? loaded = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
                return loaded ?? Assembly.LoadFrom(candidate);
            };
#endif
        }
    }
}
