using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DisplayScale
{
    public class ScaleRule
    {
        public string MonitorKey;   // friendly-name substring, or exact \\.\DISPLAYn
        public int Percent;
    }

    public class Profile
    {
        public string Name;
        public List<string> Match = new List<string>();      // lowercase substrings of raw input device names
        public List<ScaleRule> Scales = new List<ScaleRule>();

        public bool MatchesDevice(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName)) return false;
            string lower = deviceName.ToLowerInvariant();
            for (int i = 0; i < Match.Count; i++)
                if (lower.Contains(Match[i])) return true;
            return false;
        }
    }

    /// <summary>
    /// Hand-rolled INI so the file stays commentable and hand-editable; the config is
    /// small enough that a serializer would cost more than it saves.
    /// </summary>
    public class Config
    {
        public int EvidenceWindowMs = 800;
        public int MinEvents = 3;
        public int CooldownMs = 4000;
        public bool Log = true;
        public string StartProfile = null;
        public string Hotkey = "Ctrl+Alt+Shift+S";

        public List<Profile> Profiles = new List<Profile>();

        public Profile Find(string name)
        {
            foreach (var p in Profiles)
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        /// <summary>Which profile owns this device, or null when no profile claims it.</summary>
        public Profile MatchProfile(string deviceName)
        {
            foreach (var p in Profiles)
                if (p.MatchesDevice(deviceName)) return p;
            return null;
        }

        public static string DefaultPath()
        {
            string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            return Path.Combine(exeDir, "displayscale.ini");
        }

        /// <summary>
        /// A starting config built from the displays actually attached to this
        /// machine. Profiles deliberately start with no `match` rules: which devices
        /// mean "near" and "far" is the one thing that can't be guessed, and the
        /// settings page can capture them in two clicks.
        /// </summary>
        public static Config CreateDefault()
        {
            var cfg = new Config();

            var near = new Profile();
            near.Name = "desk";
            var far = new Profile();
            far.Name = "couch";

            try
            {
                foreach (var m in Dpi.Enumerate())
                {
                    string key = string.IsNullOrEmpty(m.FriendlyName) ? m.GdiName : m.FriendlyName;

                    var atDesk = new ScaleRule();
                    atDesk.MonitorKey = key;
                    atDesk.Percent = m.CurrentPercent;
                    near.Scales.Add(atDesk);

                    // 300% is the point of the tool, but small panels cap lower.
                    int target = m.Supports(300) ? 300 : m.MaxPercent;
                    var awayFromDesk = new ScaleRule();
                    awayFromDesk.MonitorKey = key;
                    awayFromDesk.Percent = target;
                    far.Scales.Add(awayFromDesk);
                }
            }
            catch { /* no displays readable; profiles are still usable once edited */ }

            cfg.Profiles.Add(near);
            cfg.Profiles.Add(far);
            return cfg;
        }

        public static Config Load(string path)
        {
            var cfg = new Config();

            // First run on a new machine: write a config describing its own displays
            // rather than shipping one that names somebody else's hardware.
            if (!File.Exists(path))
            {
                Config generated = CreateDefault();
                generated.Save(path);
                return generated;
            }

            string section = "";
            Profile current = null;

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = StripComment(rawLine).Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    if (section.StartsWith("profile:", StringComparison.OrdinalIgnoreCase))
                    {
                        current = new Profile();
                        current.Name = section.Substring("profile:".Length).Trim();
                        cfg.Profiles.Add(current);
                    }
                    else
                    {
                        current = null;
                    }
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string value = line.Substring(eq + 1).Trim();
                if (value.Length == 0) continue;

                if (current != null)
                {
                    if (key == "match")
                    {
                        current.Match.Add(value.ToLowerInvariant());
                    }
                    else if (key == "scale")
                    {
                        // "Odyssey G70NC : 300"  -- monitor key may contain spaces
                        int colon = value.LastIndexOf(':');
                        if (colon < 0) continue;
                        var rule = new ScaleRule();
                        rule.MonitorKey = value.Substring(0, colon).Trim();
                        int pct;
                        if (!int.TryParse(value.Substring(colon + 1).Trim(),
                                NumberStyles.Integer, CultureInfo.InvariantCulture, out pct)) continue;
                        rule.Percent = pct;
                        current.Scales.Add(rule);
                    }
                }
                else if (section.Equals("settings", StringComparison.OrdinalIgnoreCase))
                {
                    switch (key)
                    {
                        case "evidence_window_ms": cfg.EvidenceWindowMs = ParseInt(value, cfg.EvidenceWindowMs); break;
                        case "min_events": cfg.MinEvents = ParseInt(value, cfg.MinEvents); break;
                        case "cooldown_ms": cfg.CooldownMs = ParseInt(value, cfg.CooldownMs); break;
                        case "log": cfg.Log = value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1"; break;
                        case "start_profile": cfg.StartProfile = value; break;
                        case "hotkey": cfg.Hotkey = value; break;
                    }
                }
            }

            if (cfg.MinEvents < 1) cfg.MinEvents = 1;
            return cfg;
        }

        static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        /// <summary>Strips ; and # comments, honouring nothing else (values never contain them).</summary>
        static string StripComment(string line)
        {
            int cut = -1;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == ';' || line[i] == '#') { cut = i; break; }
            }
            return cut < 0 ? line : line.Substring(0, cut);
        }

        /// <summary>
        /// Rewrites the file from the model. The explanatory comments are re-emitted
        /// so the file stays self-documenting, but any comments *you* added are lost
        /// -- hence the .bak alongside it.
        /// </summary>
        public void Save(string path)
        {
            try
            {
                if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            }
            catch { /* a failed backup must not block saving */ }

            var sb = new StringBuilder();
            sb.AppendLine("; displayscale configuration");
            sb.AppendLine(";");
            sb.AppendLine("; A profile fires when input arrives from one of its `match` devices, then applies");
            sb.AppendLine("; every `scale` rule it lists. Devices no profile claims are ignored entirely.");
            sb.AppendLine(";");
            sb.AppendLine("; Rewritten whenever settings are saved. The previous version is kept next to");
            sb.AppendLine("; it as displayscale.ini.bak, so hand-added comments survive one save.");
            sb.AppendLine();
            sb.AppendLine("[settings]");
            sb.AppendLine();
            sb.AppendLine("; A switch needs min_events input events from the new device set inside");
            sb.AppendLine("; evidence_window_ms, which is what stops an accidental nudge of the desk mouse");
            sb.AppendLine("; from rescaling the desktop while you're on the couch.");
            sb.AppendLine("min_events         = " + MinEvents);
            sb.AppendLine("evidence_window_ms = " + EvidenceWindowMs);
            sb.AppendLine();
            sb.AppendLine("; Minimum gap between switches. A DPI change relayouts every running window, so");
            sb.AppendLine("; flapping is far worse than switching a couple of seconds late.");
            sb.AppendLine("cooldown_ms        = " + CooldownMs);
            sb.AppendLine();
            sb.AppendLine("; Append activity to displayscale.log next to the exe.");
            sb.AppendLine("log                = " + (Log ? "true" : "false"));
            sb.AppendLine();
            sb.AppendLine("; Global hotkey that steps to the next profile, and holds that choice against");
            sb.AppendLine("; the device set you pressed it with. Set to `none` to disable.");
            sb.AppendLine("hotkey             = " + (string.IsNullOrEmpty(Hotkey) ? "none" : Hotkey));
            if (!string.IsNullOrEmpty(StartProfile))
            {
                sb.AppendLine();
                sb.AppendLine("; Profile forced at startup instead of waiting for the first keystroke.");
                sb.AppendLine("start_profile      = " + StartProfile);
            }

            foreach (var p in Profiles)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("[profile:" + p.Name + "]");
                foreach (string m in p.Match)
                    sb.AppendLine("match = " + m + "        ; " + RawInputWatcher.Describe(m));
                if (p.Match.Count > 0) sb.AppendLine();
                foreach (var s in p.Scales)
                    sb.AppendLine("scale = " + s.MonitorKey + " : " + s.Percent);
            }
            sb.AppendLine();

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine("settings: min_events=" + MinEvents +
                          " evidence_window_ms=" + EvidenceWindowMs +
                          " cooldown_ms=" + CooldownMs);
            foreach (var p in Profiles)
            {
                sb.AppendLine("profile " + p.Name + ":");
                foreach (var m in p.Match) sb.AppendLine("    match  " + m);
                foreach (var s in p.Scales) sb.AppendLine("    scale  " + s.MonitorKey + " -> " + s.Percent + "%");
            }
            return sb.ToString();
        }
    }
}
