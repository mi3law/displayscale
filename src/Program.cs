using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DisplayScale
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        const int SW_HIDE = 0;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint GetConsoleProcessList(uint[] processList, uint count);

        static Mutex _singleInstance;

        /// <summary>
        /// True when this process owns its console, i.e. Windows created one for the
        /// launch. Run from a terminal, the shell is attached too and the count is
        /// higher. It is the difference between a double-click (where the user wants
        /// the app) and typing the bare name (where they want the usage text).
        /// </summary>
        static bool LaunchedFromExplorer()
        {
            try
            {
                var attached = new uint[4];
                uint count = GetConsoleProcessList(attached, (uint)attached.Length);
                return count <= 1;
            }
            catch { return false; }
        }

        [STAThread]
        static int Main(string[] args)
        {
            string verb = args.Length > 0
                ? args[0].ToLowerInvariant()
                : (LaunchedFromExplorer() ? "run" : "help");

            try
            {
                switch (verb)
                {
                    case "monitors": return CmdMonitors();
                    case "devices": return CmdDevices();
                    case "watch": return CmdWatch();
                    case "set": return CmdSet(args);
                    case "run": return CmdRun(args);
                    case "config":
                    case "settings": return CmdConfig();
                    case "install": return CmdInstall();
                    case "uninstall": return CmdUninstall();
                    default: return CmdHelp();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;
            }
        }

        static int CmdHelp()
        {
            Console.WriteLine(@"displayscale - switch monitor scaling based on which input device you're using

  Double-clicking the exe starts the tray watcher. These verbs are for a terminal.

  displayscale monitors        list displays, current scale, and every scale they allow
  displayscale devices         list all keyboards and mice Windows can see
  displayscale watch           print each keystroke/movement's source device (use to write match rules)
  displayscale set <monitor> <percent>
                               set one display's scale now, e.g. set ""Odyssey"" 300
  displayscale run [--profile N]
                               run the tray watcher (this is what auto-switches)
  displayscale config          open the settings page in your browser
  displayscale install         run automatically at logon
  displayscale uninstall       stop running at logon

Config lives next to the exe in displayscale.ini.");
            return 0;
        }

        static int CmdMonitors()
        {
            var monitors = Dpi.Enumerate();
            if (monitors.Count == 0)
            {
                Console.WriteLine("No scalable displays found.");
                return 1;
            }

            foreach (var m in monitors)
            {
                Console.WriteLine();
                Console.WriteLine(m.FriendlyName + "   [" + m.GdiName + "]");
                Console.WriteLine("    current      {0}%", m.CurrentPercent);
                Console.WriteLine("    recommended  {0}%", m.RecommendedPercent);
                Console.WriteLine("    maximum      {0}%", m.MaxPercent);

                var avail = m.AvailablePercents();
                var parts = new List<string>();
                foreach (int p in avail) parts.Add(p == m.CurrentPercent ? "[" + p + "]" : p.ToString());
                Console.WriteLine("    available    {0}", string.Join("  ", parts.ToArray()));

                if (!m.Supports(300))
                    Console.WriteLine("    note         300% unavailable on this panel (cap {0}%)", m.MaxPercent);
            }
            Console.WriteLine();
            return 0;
        }

        static int CmdDevices()
        {
            var devices = RawInputWatcher.ListDevices();
            Console.WriteLine();
            foreach (var d in devices)
            {
                Console.WriteLine("{0,-9} {1}", d.Kind, RawInputWatcher.Describe(d.Name));
                Console.WriteLine("          {0}", d.Name);
            }
            Console.WriteLine();
            Console.WriteLine("{0} devices. Use a distinctive lowercase substring as a `match` rule.", devices.Count);
            Console.WriteLine();
            return 0;
        }

        static int CmdWatch()
        {
            Console.WriteLine();
            Console.WriteLine("Watching input. Type on / move each device in turn to see which is which.");
            Console.WriteLine("Press Ctrl+C to stop.");
            Console.WriteLine();

            var watcher = new RawInputWatcher();
            string last = null;

            watcher.Input += delegate(object sender, InputEventArgs e)
            {
                if (e.DeviceName == last) return; // only report changes of source
                last = e.DeviceName;
                Console.WriteLine("{0:HH:mm:ss}  {1,-8} {2}", DateTime.Now, e.Kind, RawInputWatcher.Describe(e.DeviceName));
                Console.WriteLine("           {0}", e.DeviceName);
            };

            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                Application.ExitThread();
            };

            Application.Run();
            watcher.Dispose();
            return 0;
        }

        static int CmdSet(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("usage: displayscale set <monitor> <percent>");
                return 2;
            }

            string key = args[1];
            int percent;
            if (!int.TryParse(args[2], out percent))
            {
                Console.Error.WriteLine("percent must be a number");
                return 2;
            }

            var monitors = Dpi.Enumerate();
            Dpi.MonitorScale target = null;
            foreach (var m in monitors)
                if (m.Matches(key)) { target = m; break; }

            if (target == null)
            {
                Console.Error.WriteLine("no connected display matches \"" + key + "\"");
                Console.Error.WriteLine("try: displayscale monitors");
                return 1;
            }

            int was = target.CurrentPercent;
            string error;
            if (!Dpi.SetScale(target, percent, out error))
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            Console.WriteLine("{0}: {1}% -> {2}%", target.FriendlyName, was, percent);
            return 0;
        }

        static int CmdRun(string[] args)
        {
            bool isFirstInstance;
            _singleInstance = new Mutex(true, @"Local\displayscale.instance", out isFirstInstance);
            if (!isFirstInstance)
            {
                // Two watchers would fight over the hotkey and the display scale.
                // Now that double-clicking launches the app, that is easy to do by
                // accident, so surface the instance already running instead.
                Console.WriteLine("displayscale is already running; opening its settings.");
                SignalOpenSettings();
                return 0;
            }

            IntPtr console = GetConsoleWindow();
            if (console != IntPtr.Zero) ShowWindow(console, SW_HIDE);

            string configPath = Config.DefaultPath();
            var cfg = Config.Load(configPath);

            for (int i = 1; i < args.Length - 1; i++)
                if (args[i] == "--profile") cfg.StartProfile = args[i + 1];

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext(cfg, configPath));
            return 0;
        }

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        static int CmdConfig()
        {
            if (!SignalOpenSettings())
            {
                Console.WriteLine("Starting displayscale…");
                var psi = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "run");
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
                Thread.Sleep(2000);

                if (!SignalOpenSettings())
                {
                    Console.Error.WriteLine("Could not reach displayscale to open its settings.");
                    return 1;
                }
            }

            Console.WriteLine("Opening the settings page in your browser.");
            return 0;
        }

        /// <summary>
        /// Ask an already-running instance to open its settings page. Returns false
        /// when there is nothing running to ask.
        /// </summary>
        static bool SignalOpenSettings()
        {
            int self = Process.GetCurrentProcess().Id;

            var others = new List<Process>();
            foreach (var p in Process.GetProcessesByName("displayscale"))
                if (p.Id != self) others.Add(p);
            if (others.Count == 0) return false;

            // HWND_BROADCAST looks tidier but UIPI filters it (PostMessage reports
            // success while setting ERROR_ACCESS_DENIED), so target the windows
            // directly. Only the one that registered this message acts on it.
            var targets = new List<IntPtr>();
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                uint owner;
                GetWindowThreadProcessId(hWnd, out owner);
                foreach (var p in others)
                    if (p.Id == (int)owner) { targets.Add(hWnd); break; }
                return true;
            }, IntPtr.Zero);

            foreach (IntPtr hWnd in targets)
                PostMessage(hWnd, RawInputWatcher.OpenSettingsMessage, IntPtr.Zero, IntPtr.Zero);

            return targets.Count > 0;
        }

        static string ShortcutPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "displayscale.lnk");
        }

        static int CmdInstall()
        {
            string exePath = Assembly.GetExecutingAssembly().Location;
            string lnk = ShortcutPath();

            // Late-bound WScript.Shell keeps this free of an interop assembly reference.
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                Console.Error.WriteLine("WScript.Shell unavailable; create the Startup shortcut manually.");
                return 1;
            }

            object shell = Activator.CreateInstance(shellType);
            object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                null, shell, new object[] { lnk });
            Type st = shortcut.GetType();
            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exePath });
            st.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { "run" });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
                new object[] { Path.GetDirectoryName(exePath) });
            st.InvokeMember("WindowStyle", BindingFlags.SetProperty, null, shortcut, new object[] { 7 });
            st.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut,
                new object[] { "Switch display scaling based on active input device" });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);

            Console.WriteLine("Installed: " + lnk);
            Console.WriteLine("It will start at your next logon. To start it now:");
            Console.WriteLine("    " + exePath + " run");
            return 0;
        }

        static int CmdUninstall()
        {
            string lnk = ShortcutPath();
            if (File.Exists(lnk))
            {
                File.Delete(lnk);
                Console.WriteLine("Removed: " + lnk);
            }
            else
            {
                Console.WriteLine("Not installed.");
            }
            return 0;
        }
    }
}
