using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace DisplayScale
{
    /// <summary>
    /// A parsed global hotkey chord, e.g. "Ctrl+Alt+Shift+S".
    /// </summary>
    internal class HotkeySpec
    {
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        public uint Modifiers;
        public uint VirtualKey;
        public string Text;

        /// <summary>
        /// Parses "Ctrl+Alt+Shift+S". Returns false with a reason when the chord is
        /// unusable. "none" disables the hotkey and returns false with no error.
        /// </summary>
        public static bool TryParse(string input, out HotkeySpec spec, out string error)
        {
            spec = null;
            error = null;

            if (string.IsNullOrEmpty(input)) return false;
            string trimmed = input.Trim();
            if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("off", StringComparison.OrdinalIgnoreCase)) return false;

            string[] parts = trimmed.Split('+');
            uint mods = 0;
            string keyToken = null;

            foreach (string raw in parts)
            {
                string token = raw.Trim();
                if (token.Length == 0) continue;

                switch (token.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control": mods |= MOD_CONTROL; break;
                    case "alt": mods |= MOD_ALT; break;
                    case "shift": mods |= MOD_SHIFT; break;
                    case "win":
                    case "windows": mods |= MOD_WIN; break;
                    default:
                        if (keyToken != null)
                        {
                            error = "more than one non-modifier key in \"" + input + "\"";
                            return false;
                        }
                        keyToken = token;
                        break;
                }
            }

            if (keyToken == null)
            {
                error = "\"" + input + "\" has no key, only modifiers";
                return false;
            }

            Keys key;
            if (!TryParseKey(keyToken, out key))
            {
                error = "unrecognised key \"" + keyToken + "\"";
                return false;
            }

            if (mods == 0)
            {
                // A bare key would swallow that keystroke system-wide.
                error = "\"" + input + "\" needs at least one modifier (Ctrl/Alt/Shift/Win)";
                return false;
            }

            spec = new HotkeySpec();
            spec.Modifiers = mods | MOD_NOREPEAT;
            spec.VirtualKey = (uint)key;
            spec.Text = Format(mods, key);
            return true;
        }

        static bool TryParseKey(string token, out Keys key)
        {
            key = Keys.None;

            // Enum.TryParse would read a bare "1" as the numeric enum value
            // (Keys.LButton), not the digit key, so map digits explicitly.
            if (token.Length == 1 && token[0] >= '0' && token[0] <= '9')
            {
                key = (Keys)((int)Keys.D0 + (token[0] - '0'));
                return true;
            }

            if (!Enum.TryParse(token, true, out key)) return false;
            return key != Keys.None && Enum.IsDefined(typeof(Keys), key);
        }

        static string Format(uint mods, Keys key)
        {
            var parts = new List<string>();
            if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((mods & MOD_ALT) != 0) parts.Add("Alt");
            if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
            if ((mods & MOD_WIN) != 0) parts.Add("Win");
            parts.Add(key.ToString());
            return string.Join("+", parts.ToArray());
        }

        public override string ToString()
        {
            return Text;
        }
    }
}
