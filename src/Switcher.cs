using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DisplayScale
{
    /// <summary>
    /// Decides when input from a different device set means the user has physically
    /// moved, and applies that profile's scales.
    ///
    /// A DPI change forces every running app to relayout, so a false positive is
    /// expensive and flapping is worse. Two guards prevent it: a burst of MinEvents
    /// within EvidenceWindowMs must arrive from the new profile (one stray bump of
    /// the desk mouse is not enough), and CooldownMs must have elapsed since the last
    /// switch.
    /// </summary>
    internal class Switcher
    {
        Config _cfg;
        readonly string _logPath;

        string _currentProfile;
        string _pendingProfile;
        int _pendingCount;
        DateTime _pendingSince = DateTime.MinValue;
        DateTime _lastSwitch = DateTime.MinValue;

        // Which profile's devices produced the most recent input. A manual override
        // latches onto this so it knows what hardware it is contradicting.
        string _lastDeviceProfile;

        bool _overrideActive;
        string _overrideDeviceProfile;

        public bool Paused { get; set; }

        public string CurrentProfile { get { return _currentProfile; } }

        public bool OverrideActive { get { return _overrideActive; } }

        public event EventHandler ProfileChanged;

        public Switcher(Config cfg)
        {
            _cfg = cfg;
            _currentProfile = cfg.StartProfile;

            string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            _logPath = Path.Combine(exeDir, "displayscale.log");
        }

        public void OnInput(string deviceName)
        {
            Profile profile = _cfg.MatchProfile(deviceName);
            if (profile == null) return; // built-in keyboard/touchpad etc: not a location signal

            // Recorded even while paused or overridden: a hotkey press needs to know
            // which hardware is in your hands at the moment it fires.
            _lastDeviceProfile = profile.Name;

            if (Paused) return;

            if (_overrideActive)
            {
                // You told us this device set's implication is wrong. That stands as
                // long as you keep using it -- otherwise using the MX on the couch
                // would drag the scale straight back to 100%. Different hardware is
                // genuinely new evidence, so it hands control back to the watcher.
                if (_overrideDeviceProfile != null &&
                    string.Equals(profile.Name, _overrideDeviceProfile, StringComparison.OrdinalIgnoreCase))
                    return;

                ClearOverride("now using " + Shorten(deviceName));
            }

            if (string.Equals(profile.Name, _currentProfile, StringComparison.OrdinalIgnoreCase))
            {
                _pendingProfile = null;
                _pendingCount = 0;
                return;
            }

            DateTime now = DateTime.UtcNow;

            if (!string.Equals(profile.Name, _pendingProfile, StringComparison.OrdinalIgnoreCase) ||
                (now - _pendingSince).TotalMilliseconds > _cfg.EvidenceWindowMs)
            {
                _pendingProfile = profile.Name;
                _pendingSince = now;
                _pendingCount = 0;
            }

            _pendingCount++;
            if (_pendingCount < _cfg.MinEvents) return;
            if ((now - _lastSwitch).TotalMilliseconds < _cfg.CooldownMs) return;

            Apply(profile, "input from " + Shorten(deviceName));
        }

        /// <summary>Apply a profile unconditionally (tray menu, or startup).</summary>
        public void Force(string profileName, string reason)
        {
            Profile p = _cfg.Find(profileName);
            if (p == null) { Log("no such profile: " + profileName); return; }
            LatchOverride();
            Apply(p, reason);
        }

        /// <summary>
        /// Step to the next profile in config order. With two profiles this is a
        /// straight toggle; a third location would slot in without changes here.
        /// </summary>
        public void CycleProfile(string reason)
        {
            if (_cfg.Profiles.Count == 0) { Log("hotkey pressed but no profiles configured"); return; }

            int index = -1;
            for (int i = 0; i < _cfg.Profiles.Count; i++)
            {
                if (string.Equals(_cfg.Profiles[i].Name, _currentProfile, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            Profile next = _cfg.Profiles[(index + 1) % _cfg.Profiles.Count];
            LatchOverride();
            Apply(next, reason);
        }

        /// <summary>
        /// Pin the manual choice against the device set currently in use.
        ///
        /// Raw Input is delivered below the hotkey layer, so the chord's own
        /// keystrokes have already counted toward an automatic switch. That evidence
        /// has to be discarded here or the watcher undoes the hotkey a moment later.
        /// </summary>
        void LatchOverride()
        {
            _pendingProfile = null;
            _pendingCount = 0;
            _overrideActive = true;
            _overrideDeviceProfile = _lastDeviceProfile;
        }

        /// <summary>Swap in a freshly saved config without restarting the watcher.</summary>
        public void UpdateConfig(Config cfg)
        {
            _cfg = cfg;
            _pendingProfile = null;
            _pendingCount = 0;
        }

        /// <summary>
        /// Re-apply the current profile, e.g. after its scales were edited. Unlike
        /// Force this does not latch an override -- nothing about where you are has
        /// changed, only what the profile means.
        /// </summary>
        public void Reapply(string reason)
        {
            if (string.IsNullOrEmpty(_currentProfile)) return;
            Profile p = _cfg.Find(_currentProfile);
            if (p == null) return;
            Apply(p, reason);
        }

        public void ClearOverride(string reason)
        {
            if (!_overrideActive) return;
            _overrideActive = false;
            _overrideDeviceProfile = null;
            Log("manual override released (" + reason + ")");

            var handler = ProfileChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        /// <summary>
        /// Work out which profile the displays are already in at startup, so the first
        /// hotkey press steps to the other one instead of re-applying the current.
        /// </summary>
        public void InferCurrentProfile()
        {
            if (!string.IsNullOrEmpty(_currentProfile)) return;

            List<Dpi.MonitorScale> monitors;
            try { monitors = Dpi.Enumerate(); }
            catch { return; }

            foreach (var p in _cfg.Profiles)
            {
                if (p.Scales.Count == 0) continue;

                bool all = true;
                foreach (var rule in p.Scales)
                {
                    Dpi.MonitorScale target = null;
                    foreach (var m in monitors)
                        if (m.Matches(rule.MonitorKey)) { target = m; break; }

                    if (target == null || target.CurrentPercent != rule.Percent) { all = false; break; }
                }

                if (all)
                {
                    _currentProfile = p.Name;
                    Log("displays already match profile \"" + p.Name + "\"");
                    return;
                }
            }
        }

        void Apply(Profile profile, string reason)
        {
            _currentProfile = profile.Name;
            _lastSwitch = DateTime.UtcNow;
            _pendingProfile = null;
            _pendingCount = 0;

            List<Dpi.MonitorScale> monitors;
            try
            {
                monitors = Dpi.Enumerate();
            }
            catch (Exception ex)
            {
                Log("enumerate failed: " + ex.Message);
                return;
            }

            var sb = new StringBuilder();
            sb.Append("-> ").Append(profile.Name).Append("  (").Append(reason).Append(")");

            foreach (var rule in profile.Scales)
            {
                Dpi.MonitorScale target = null;
                foreach (var m in monitors)
                {
                    if (m.Matches(rule.MonitorKey)) { target = m; break; }
                }

                if (target == null)
                {
                    sb.Append("\n     ").Append(rule.MonitorKey).Append(": not connected, skipped");
                    continue;
                }

                if (target.CurrentPercent == rule.Percent)
                {
                    sb.Append("\n     ").Append(target.FriendlyName).Append(": already ").Append(rule.Percent).Append('%');
                    continue;
                }

                string error;
                int was = target.CurrentPercent;
                if (Dpi.SetScale(target, rule.Percent, out error))
                    sb.Append("\n     ").Append(target.FriendlyName).Append(": ").Append(was).Append("% -> ").Append(rule.Percent).Append('%');
                else
                    sb.Append("\n     ").Append(target.FriendlyName).Append(": FAILED - ").Append(error);
            }

            Log(sb.ToString());

            var handler = ProfileChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        static string Shorten(string deviceName)
        {
            string desc = RawInputWatcher.Describe(deviceName);
            return desc == "unrecognised" ? deviceName : desc;
        }

        public void Log(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message;
            Console.WriteLine(line);
            if (!_cfg.Log) return;
            try
            {
                // Keep the log from growing without bound across months of logons.
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 512 * 1024)
                    File.Delete(_logPath);
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch { /* logging must never break switching */ }
        }
    }
}
