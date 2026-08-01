using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DisplayScale
{
    /// <summary>
    /// Tray host: owns the raw input watcher and the switcher, and gives you a manual
    /// override for the cases automation can't infer (reading from across the room on
    /// the desk keyboard, say).
    /// </summary>
    internal class TrayContext : ApplicationContext
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr hIcon);

        Config _cfg;
        readonly string _configPath;
        readonly RawInputWatcher _watcher;
        readonly Switcher _switcher;
        readonly NotifyIcon _tray;
        readonly ConfigServer _server;

        Icon _currentIcon;
        string _hotkeyText;
        string _lastDeviceName;

        // Read by the settings page (marshalled onto this thread by ConfigServer).
        internal Config CurrentConfig { get { return _cfg; } }
        internal string CurrentProfileName { get { return _switcher.CurrentProfile; } }
        internal bool IsPaused { get { return _switcher.Paused; } }
        internal bool IsHeld { get { return _switcher.OverrideActive; } }
        internal string ActiveHotkeyText { get { return _hotkeyText; } }
        internal string LastDeviceName { get { return _lastDeviceName; } }

        public TrayContext(Config cfg, string configPath)
        {
            _cfg = cfg;
            _configPath = configPath;

            _switcher = new Switcher(cfg);
            _switcher.ProfileChanged += delegate { UpdateTray(); };
            _switcher.InferCurrentProfile();

            _watcher = new RawInputWatcher();
            _watcher.Input += delegate(object sender, InputEventArgs e)
            {
                _lastDeviceName = e.DeviceName;
                _switcher.OnInput(e.DeviceName);
            };
            _watcher.HotkeyPressed += delegate { _switcher.CycleProfile("hotkey"); };
            _watcher.OpenSettingsRequested += delegate { OpenSettings(); };

            _server = new ConfigServer(this, _watcher);

            _tray = new NotifyIcon();
            _tray.Visible = true;
            _tray.ContextMenuStrip = BuildMenu();
            _tray.DoubleClick += delegate { TogglePause(); };

            SetupHotkey(cfg);
            SystemEvents_Register();
            UpdateTray();

            _switcher.Log("displayscale started; watching " + cfg.Profiles.Count + " profiles");
            if (!string.IsNullOrEmpty(cfg.StartProfile))
                _switcher.Force(cfg.StartProfile, "start_profile");
        }

        void SetupHotkey(Config cfg)
        {
            _hotkeyText = null; // a failed re-register must not leave the old chord showing

            HotkeySpec spec;
            string parseError;

            if (!HotkeySpec.TryParse(cfg.Hotkey, out spec, out parseError))
            {
                // No error means the config said "none" -- deliberately disabled.
                if (parseError != null)
                {
                    _switcher.Log("hotkey: " + parseError);
                    Warn("Hotkey not set", parseError + ".");
                }
                return;
            }

            string regError;
            if (_watcher.TryRegisterHotkey(spec, out regError))
            {
                _hotkeyText = spec.Text;
                _switcher.Log("hotkey " + spec.Text + " registered");
                return;
            }

            _switcher.Log("hotkey: " + regError);
            Warn("Hotkey unavailable", regError + ".\nChoose another with `hotkey =` in displayscale.ini.");
        }

        void Warn(string title, string text)
        {
            try
            {
                _tray.BalloonTipIcon = ToolTipIcon.Warning;
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text;
                _tray.ShowBalloonTip(8000);
            }
            catch { /* notifications may be suppressed; the log still has it */ }
        }

        void SystemEvents_Register()
        {
            // Device handles are invalidated when hardware comes and goes.
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += delegate { _watcher.InvalidateCache(); };
            Microsoft.Win32.SystemEvents.SessionSwitch += delegate { _watcher.InvalidateCache(); };
        }

        ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e) { RebuildMenu(menu); };
            RebuildMenu(menu);
            return menu;
        }

        void RebuildMenu(ContextMenuStrip menu)
        {
            menu.Items.Clear();

            var header = new ToolStripMenuItem(StatusLine());
            header.Enabled = false;
            menu.Items.Add(header);
            menu.Items.Add(new ToolStripSeparator());

            foreach (var profile in _cfg.Profiles)
            {
                Profile captured = profile;
                var item = new ToolStripMenuItem("Switch to " + captured.Name);
                item.Checked = string.Equals(captured.Name, _switcher.CurrentProfile, StringComparison.OrdinalIgnoreCase);
                item.Click += delegate { _switcher.Force(captured.Name, "tray menu"); };
                menu.Items.Add(item);
            }

            menu.Items.Add(new ToolStripSeparator());

            var toggle = new ToolStripMenuItem(
                _hotkeyText == null ? "Toggle profile" : "Toggle profile  (" + _hotkeyText + ")");
            toggle.Click += delegate { _switcher.CycleProfile("tray menu"); };
            menu.Items.Add(toggle);

            var release = new ToolStripMenuItem("Resume auto-switching now");
            release.Enabled = _switcher.OverrideActive;
            release.Click += delegate { _switcher.ClearOverride("tray menu"); };
            menu.Items.Add(release);

            menu.Items.Add(new ToolStripSeparator());

            var pause = new ToolStripMenuItem("Pause auto-switching");
            pause.Checked = _switcher.Paused;
            pause.Click += delegate { TogglePause(); };
            menu.Items.Add(pause);

            var settings = new ToolStripMenuItem("Settings…");
            settings.Font = new Font(settings.Font, FontStyle.Bold);
            settings.Click += delegate { OpenSettings(); };
            menu.Items.Add(settings);

            var reload = new ToolStripMenuItem("Edit displayscale.ini");
            reload.Click += delegate
            {
                try { Process.Start(new ProcessStartInfo(_configPath) { UseShellExecute = true }); }
                catch (Exception ex) { _switcher.Log("could not open config: " + ex.Message); }
            };
            menu.Items.Add(reload);

            var exit = new ToolStripMenuItem("Exit");
            exit.Click += delegate { ExitApp(); };
            menu.Items.Add(exit);
        }

        string StatusLine()
        {
            string profile = string.IsNullOrEmpty(_switcher.CurrentProfile) ? "waiting for input" : _switcher.CurrentProfile;
            string suffix = "";
            if (_switcher.Paused) suffix = " (paused)";
            else if (_switcher.OverrideActive) suffix = " (held)";
            return "displayscale - " + profile + suffix;
        }

        void TogglePause()
        {
            _switcher.Paused = !_switcher.Paused;
            _switcher.Log(_switcher.Paused ? "auto-switching paused" : "auto-switching resumed");
            UpdateTray();
        }

        void UpdateTray()
        {
            string profile = _switcher.CurrentProfile;

            var tip = new List<string>();
            tip.Add(StatusLine());
            try
            {
                foreach (var m in Dpi.Enumerate())
                    tip.Add(m.FriendlyName + ": " + m.CurrentPercent + "%");
            }
            catch { /* tooltip is cosmetic */ }

            // NotifyIcon.Text throws at 64 characters, so build up only what fits.
            var text = new StringBuilder(tip[0]);
            for (int i = 1; i < tip.Count; i++)
            {
                if (text.Length + 1 + tip[i].Length > 63) break;
                text.Append('\n').Append(tip[i]);
            }
            // Cosmetics must never stop the watcher from running.
            try { _tray.Text = text.Length > 63 ? text.ToString(0, 63) : text.ToString(); }
            catch { }

            Icon old = _currentIcon;
            _currentIcon = RenderIcon(profile, _switcher.Paused, _switcher.OverrideActive);
            _tray.Icon = _currentIcon;
            if (old != null) DisposeIcon(old);
        }

        /// <summary>
        /// Draws the active profile's initial so the tray shows state at a glance,
        /// with a corner dot while a manual choice is holding off auto-switching.
        /// </summary>
        static Icon RenderIcon(string profile, bool paused, bool held)
        {
            char glyph = string.IsNullOrEmpty(profile) ? '?' : char.ToUpperInvariant(profile[0]);
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);

                    Color fill = paused ? Color.FromArgb(120, 120, 120) : Color.FromArgb(0, 120, 215);
                    using (var brush = new SolidBrush(fill))
                        g.FillEllipse(brush, 0, 0, 31, 31);

                    using (var font = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var text = new SolidBrush(Color.White))
                    {
                        var fmt = new StringFormat();
                        fmt.Alignment = StringAlignment.Center;
                        fmt.LineAlignment = StringAlignment.Center;
                        g.DrawString(glyph.ToString(), font, text, new RectangleF(0, 0, 32, 32), fmt);
                    }

                    if (held)
                    {
                        // Sized to survive the tray's downscale to 16x16.
                        using (var ring = new Pen(Color.FromArgb(30, 30, 30), 2f))
                        using (var dot = new SolidBrush(Color.FromArgb(255, 185, 0)))
                        {
                            g.FillEllipse(dot, 20, 20, 11, 11);
                            g.DrawEllipse(ring, 20, 20, 11, 11);
                        }
                    }
                }

                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    // Clone so the icon survives DestroyIcon on the temporary handle.
                    using (var temp = Icon.FromHandle(hIcon))
                        return (Icon)temp.Clone();
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }

        static void DisposeIcon(Icon icon)
        {
            try { icon.Dispose(); }
            catch { }
        }

        // ---- settings page --------------------------------------------------

        void OpenSettings()
        {
            string error;
            if (!_server.Start(out error))
            {
                _switcher.Log("settings: " + error);
                Warn("Settings unavailable", error);
                return;
            }

            _switcher.Log("settings page at " + _server.Url);
            LaunchBrowser(_server.Url);
        }

        /// <summary>Prefer Chrome explicitly, but fall back to whatever handles http.</summary>
        internal static void LaunchBrowser(string url)
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Google\Chrome\Application\chrome.exe")
            };

            foreach (string exe in candidates)
            {
                if (!File.Exists(exe)) continue;
                try
                {
                    var psi = new ProcessStartInfo(exe, "--new-window " + url);
                    psi.UseShellExecute = false;
                    Process.Start(psi);
                    return;
                }
                catch { /* fall through to the default handler */ }
            }

            try
            {
                var psi = new ProcessStartInfo(url);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { }
        }

        internal void ApplyProfileFromUi(string profileName)
        {
            if (string.IsNullOrEmpty(profileName)) return;
            _switcher.Force(profileName, "settings page");
        }

        /// <summary>
        /// Validate, write displayscale.ini, and swap the running config in place.
        /// Returns null on success or a message to show in the page.
        /// </summary>
        internal string SaveConfigFromUi(Dictionary<string, object> body)
        {
            try
            {
                var next = new Config();

                var settings = Get<Dictionary<string, object>>(body, "settings");
                next.MinEvents = ConfigServer.Int(settings, "min_events", _cfg.MinEvents);
                next.EvidenceWindowMs = ConfigServer.Int(settings, "evidence_window_ms", _cfg.EvidenceWindowMs);
                next.CooldownMs = ConfigServer.Int(settings, "cooldown_ms", _cfg.CooldownMs);
                next.Log = ConfigServer.Bool(settings, "log", _cfg.Log);
                next.StartProfile = _cfg.StartProfile;

                string hotkey = ConfigServer.Str(settings, "hotkey");
                next.Hotkey = string.IsNullOrEmpty(hotkey) ? "none" : hotkey.Trim();

                // Reject a chord that cannot work before it reaches disk.
                if (!next.Hotkey.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    HotkeySpec probe;
                    string hotkeyError;
                    if (!HotkeySpec.TryParse(next.Hotkey, out probe, out hotkeyError) && hotkeyError != null)
                        return hotkeyError;
                }

                var profiles = Get<object[]>(body, "profiles");
                if (profiles != null)
                {
                    foreach (object raw in profiles)
                    {
                        var pd = raw as Dictionary<string, object>;
                        if (pd == null) continue;

                        var profile = new Profile();
                        profile.Name = ConfigServer.Str(pd, "name");
                        if (string.IsNullOrEmpty(profile.Name)) continue;

                        var matches = Get<object[]>(pd, "match");
                        if (matches != null)
                        {
                            foreach (object rawMatch in matches)
                            {
                                var md = rawMatch as Dictionary<string, object>;
                                string value = md != null
                                    ? ConfigServer.Str(md, "value")
                                    : (rawMatch == null ? null : rawMatch.ToString());
                                if (!string.IsNullOrEmpty(value))
                                    profile.Match.Add(value.Trim().ToLowerInvariant());
                            }
                        }

                        var scales = Get<object[]>(pd, "scales");
                        if (scales != null)
                        {
                            foreach (object rawScale in scales)
                            {
                                var sd = rawScale as Dictionary<string, object>;
                                if (sd == null) continue;
                                string key = ConfigServer.Str(sd, "key");
                                int percent = ConfigServer.Int(sd, "percent", 0);
                                if (string.IsNullOrEmpty(key) || percent <= 0) continue;

                                var rule = new ScaleRule();
                                rule.MonitorKey = key.Trim();
                                rule.Percent = percent;
                                profile.Scales.Add(rule);
                            }
                        }

                        next.Profiles.Add(profile);
                    }
                }

                if (next.Profiles.Count == 0)
                    return "That would leave no profiles at all — not saving.";
                if (next.MinEvents < 1) next.MinEvents = 1;

                next.Save(_configPath);
                _switcher.Log("config saved from the settings page");

                _cfg = next;
                _switcher.UpdateConfig(next);
                SetupHotkey(next);
                _switcher.Reapply("config saved");
                UpdateTray();
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        static T Get<T>(Dictionary<string, object> map, string key) where T : class
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value)) return null;
            return value as T;
        }

        void ExitApp()
        {
            _server.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            _watcher.Dispose();
            ExitThread();
        }
    }
}
