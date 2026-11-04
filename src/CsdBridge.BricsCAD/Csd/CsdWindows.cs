using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CsdBridge.BricsCAD.Csd
{
    /// <summary>An open dialog: a CSD WinForms form, or a plain Windows dialog (message box, file dialog).</summary>
    internal sealed class OpenDialog
    {
        public OpenDialog(IntPtr handle, Form? form, string title, string name, string kind)
        {
            Handle = handle;
            Form = form;
            Title = title;
            Name = name;
            Kind = kind;
        }

        public IntPtr Handle { get; }

        /// <summary>The WinForms form, or null for a Win32 dialog.</summary>
        public Form? Form { get; }

        public string Title { get; }

        /// <summary>Internal form name (frmVGEGrid...) or "win32".</summary>
        public string Name { get; }

        /// <summary>"csd" for a CSD form, "win32" for a Windows dialog.</summary>
        public string Kind { get; }

        /// <summary>Id the tools use: the form name, or win32:hex handle.</summary>
        public string Id => Form != null ? Name : "win32:" + Handle.ToInt64().ToString("x");
    }

    /// <summary>
    /// Finds CSD's dialogs and runs code on their UI thread. Called from the agent's
    /// background thread: BricsCAD's main thread may be busy inside a CSD command or a
    /// modal dialog, which still processes the work posted here.
    /// </summary>
    internal static class CsdWindows
    {
        public const int DefaultTimeoutMs = 30000;

        private static readonly object BusyLock = new object();
        private static readonly List<string> BusyWith = new List<string>();

        /// <summary>
        /// What is occupying BricsCAD's main thread (a CSD command, or a button that opened
        /// a modal dialog), or null. While set, tools that need the main thread can't run.
        /// </summary>
        public static string? Busy
        {
            get
            {
                lock (BusyLock)
                {
                    return BusyWith.Count == 0 ? null : string.Join(" > ", BusyWith);
                }
            }
        }

        private static void AddBusy(string what)
        {
            lock (BusyLock)
            {
                BusyWith.Add(what);
            }
        }

        private static void RemoveBusy(string what)
        {
            lock (BusyLock)
            {
                BusyWith.Remove(what);
            }
        }

        /// <summary>True for a form defined by Civil Site Design (not CSD-Bridge or BricsCAD).</summary>
        public static bool IsCsdForm(Form f)
        {
            Type t = f.GetType();
            string asm = t.Assembly.GetName().Name ?? "";
            if (asm.StartsWith("CsdBridge", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return asm.StartsWith("CSD", StringComparison.OrdinalIgnoreCase)
                || asm.StartsWith("ARD", StringComparison.OrdinalIgnoreCase)
                || (t.Namespace ?? "").StartsWith("VbMgdAcad", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>CSD forms that are open and visible, plus visible Win32 dialogs (#32770) of this process.</summary>
        public static List<OpenDialog> List()
        {
            var result = new List<OpenDialog>();
            var forms = new List<Form>();
            int pid = Process.GetCurrentProcess().Id;

            try
            {
                foreach (Form f in Application.OpenForms.Cast<Form>().ToList())
                {
                    if (!forms.Contains(f))
                    {
                        forms.Add(f);
                    }
                }
            }
            catch (Exception)
            {
                // The collection changed while being read; the window scan below covers it.
            }

            var win32 = new List<IntPtr>();
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint windowPid);
                if (windowPid != pid || !IsWindowVisible(hwnd))
                {
                    return true;
                }

                Form? f = null;
                try
                {
                    f = Control.FromHandle(hwnd) as Form;
                }
                catch (Exception)
                {
                    // Not a WinForms window.
                }

                if (f != null)
                {
                    if (!forms.Contains(f))
                    {
                        forms.Add(f);
                    }
                }
                else if (ClassName(hwnd) == "#32770")
                {
                    win32.Add(hwnd);
                }

                return true;
            }, IntPtr.Zero);

            foreach (Form f in forms)
            {
                try
                {
                    if (f.IsDisposed || !f.IsHandleCreated || !IsCsdForm(f) || !IsWindowVisible(f.Handle))
                    {
                        continue;
                    }

                    result.Add(new OpenDialog(f.Handle, f, WindowText(f.Handle), string.IsNullOrEmpty(f.Name) ? f.GetType().Name : f.Name, "csd"));
                }
                catch (Exception)
                {
                    // Closing while we looked.
                }
            }

            foreach (IntPtr h in win32)
            {
                result.Add(new OpenDialog(h, null, WindowText(h), "win32", "win32"));
            }

            return result;
        }

        /// <summary>Find a dialog by form name, win32:handle id, or a piece of its title.</summary>
        public static OpenDialog Find(string dialog)
        {
            List<OpenDialog> open = List();
            string d = dialog.Trim();
            OpenDialog? hit =
                open.FirstOrDefault(o => string.Equals(o.Id, d, StringComparison.OrdinalIgnoreCase))
                ?? open.FirstOrDefault(o => string.Equals(o.Name, d, StringComparison.OrdinalIgnoreCase))
                ?? open.FirstOrDefault(o => string.Equals(o.Title, d, StringComparison.OrdinalIgnoreCase))
                ?? open.FirstOrDefault(o => o.Title.IndexOf(d, StringComparison.OrdinalIgnoreCase) >= 0);
            if (hit == null)
            {
                string names = open.Count == 0 ? "none" : string.Join(", ", open.Select(o => $"{o.Id} \"{o.Title}\""));
                throw new CsdException($"No open dialog matches '{dialog}'. Open dialogs: {names}.");
            }

            return hit;
        }

        /// <summary>Run work on the control's UI thread and wait for the result.</summary>
        public static T OnUiThread<T>(Control control, Func<T> work, int timeoutMs = DefaultTimeoutMs)
        {
            if (!control.IsHandleCreated || control.IsDisposed)
            {
                throw new CsdException("The dialog has closed.");
            }

            if (!control.InvokeRequired)
            {
                return work();
            }

            IAsyncResult pending = control.BeginInvoke(work);
            if (!pending.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                throw new CsdException($"The dialog didn't respond within {timeoutMs / 1000} s (CSD may be busy recalculating). Try again shortly.");
            }

            try
            {
                return (T)control.EndInvoke(pending)!;
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        /// <summary>
        /// Start something that may open a modal dialog (a button, a CSD command) without
        /// waiting for it to finish. Returns when it finishes, a new dialog appears, or the
        /// wait runs out.
        /// </summary>
        /// <summary>
        /// Start something that may open a modal dialog (a button, a CSD command) without
        /// waiting for it to finish. <paramref name="post"/> queues an action on the right
        /// UI thread. Returns when the work finishes, a new dialog appears, or the wait runs out.
        /// </summary>
        public static LaunchResult Launch(string what, Action<Action> post, Action work, int waitMs)
        {
            HashSet<IntPtr> before = new HashSet<IntPtr>(List().Select(o => o.Handle));
            var done = new ManualResetEventSlim(false);
            Exception? error = null;

            AddBusy(what);
            Action run = () =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    error = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                }
                finally
                {
                    RemoveBusy(what);
                    done.Set();
                }
            };

            try
            {
                post(run);
            }
            catch (Exception)
            {
                RemoveBusy(what);
                throw;
            }

            var sw = Stopwatch.StartNew();
            List<OpenDialog> opened = new List<OpenDialog>();
            while (sw.ElapsedMilliseconds < waitMs)
            {
                if (done.Wait(200))
                {
                    // Give a modeless dialog a moment to appear after the command returns.
                    Thread.Sleep(400);
                    opened = List().Where(o => !before.Contains(o.Handle)).ToList();
                    break;
                }

                opened = List().Where(o => !before.Contains(o.Handle)).ToList();
                if (opened.Count > 0)
                {
                    // Let it finish drawing before anyone reads it.
                    Thread.Sleep(500);
                    opened = List().Where(o => !before.Contains(o.Handle)).ToList();
                    break;
                }
            }

            return new LaunchResult(done.IsSet, error, opened);
        }

        public static string WindowText(IntPtr hwnd)
        {
            int len = GetWindowTextLength(hwnd);
            if (len <= 0)
            {
                return "";
            }

            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string ClassName(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        // ------------------------------------------------------------------ Win32 dialogs

        internal sealed class Win32Control
        {
            public IntPtr Handle { get; set; }
            public int Id { get; set; }
            public string Class { get; set; } = "";
            public string Text { get; set; } = "";
            public bool Visible { get; set; }
            public bool Enabled { get; set; }
        }

        public static List<Win32Control> Win32Children(IntPtr dialog)
        {
            var list = new List<Win32Control>();
            EnumChildWindows(dialog, (hwnd, _) =>
            {
                string cls = ClassName(hwnd);
                string text = cls == "Edit" ? EditText(hwnd) : WindowText(hwnd);
                list.Add(new Win32Control
                {
                    Handle = hwnd,
                    Id = GetDlgCtrlID(hwnd),
                    Class = cls,
                    Text = text,
                    Visible = IsWindowVisible(hwnd),
                    Enabled = IsWindowEnabled(hwnd),
                });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static void SetEditText(IntPtr hwnd, string text) => SendMessageText(hwnd, WmSetText, IntPtr.Zero, text);

        /// <summary>Press a Win32 button. Posted, so a button that closes a modal message box doesn't block us.</summary>
        public static void ClickButton(IntPtr hwnd) => PostMessage(hwnd, BmClick, IntPtr.Zero, IntPtr.Zero);

        /// <summary>Post Enter key down/up to a window (as if the user pressed Enter in it).</summary>
        public static void PostEnter(IntPtr hwnd)
        {
            PostMessage(hwnd, WmKeyDown, (IntPtr)VkReturn, (IntPtr)0x001C0001);
            PostMessage(hwnd, WmKeyUp, (IntPtr)VkReturn, unchecked((IntPtr)0xC01C0001));
        }

        private static string EditText(IntPtr hwnd)
        {
            int len = (int)SendMessage(hwnd, WmGetTextLength, IntPtr.Zero, IntPtr.Zero);
            if (len <= 0)
            {
                return "";
            }

            var sb = new StringBuilder(len + 1);
            SendMessageBuilder(hwnd, WmGetText, (IntPtr)(len + 1), sb);
            return sb.ToString();
        }

        private const uint WmSetText = 0x000C;
        private const uint WmGetText = 0x000D;
        private const uint WmGetTextLength = 0x000E;
        private const uint WmKeyDown = 0x0100;
        private const uint WmKeyUp = 0x0101;
        private const uint BmClick = 0x00F5;
        private const int VkReturn = 0x0D;

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int GetDlgCtrlID(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageText(IntPtr hwnd, uint msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageBuilder(IntPtr hwnd, uint msg, IntPtr wParam, StringBuilder lParam);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    }

    internal sealed class LaunchResult
    {
        public LaunchResult(bool finished, Exception? error, List<OpenDialog> opened)
        {
            Finished = finished;
            Error = error;
            Opened = opened;
        }

        public bool Finished { get; }
        public Exception? Error { get; }
        public List<OpenDialog> Opened { get; }
    }

    /// <summary>A problem to report back to Claude as a tool error.</summary>
    internal sealed class CsdException : Exception
    {
        public CsdException(string message)
            : base(message)
        {
        }
    }
}
