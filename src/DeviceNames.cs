using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DisplayScale
{
    /// <summary>
    /// Turns a raw input device path into something a human recognises, using only
    /// what Windows knows about the hardware -- no built-in table of specific
    /// products, so this reads the same on anyone's machine.
    ///
    /// Neither available source is sufficient alone. USB devices expose a HID product
    /// string ("Logitech USB Receiver") but Bluetooth LE ones return nothing; BLE
    /// devices carry their name as a FriendlyName a couple of levels up the PnP tree
    /// ("MX KEYS S") where USB devices have none.
    /// </summary>
    internal static class DeviceNames
    {
        const uint CM_DRP_DEVICEDESC = 1;
        const uint CM_DRP_FRIENDLYNAME = 13;
        const int MaxWalkDepth = 5;

        static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll")]
        static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_DevNode_Registry_PropertyW(uint devInst, uint property,
            out uint regType, StringBuilder buffer, ref uint length, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFileW(string name, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        static extern bool HidD_GetProductString(IntPtr device, StringBuilder buffer, int byteLength);

        /// <summary>
        /// Nodes at or above these are shared plumbing, not the device you plugged in.
        /// Without this the walk cheerfully reports your USB host controller as the
        /// name of your keyboard.
        /// </summary>
        static readonly string[] BusNodes =
        {
            "root hub", "host controller", "composite device", "root complex",
            "enumerator", "usb hub", "lpc controller", "acpi-compliant",
            "wireless bluetooth", "bluetooth radio"
        };

        /// <summary>Best available human-readable name for a raw input device path.</summary>
        public static string ForDevicePath(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return "unknown device";

            string friendly = WalkForFriendlyName(devicePath);
            if (!string.IsNullOrEmpty(friendly)) return friendly;

            string product = HidProductString(devicePath);
            if (!string.IsNullOrEmpty(product)) return product;

            string desc = LeafDescription(devicePath);
            if (!string.IsNullOrEmpty(desc)) return desc;

            return FromVidPid(devicePath);
        }

        /// <summary>
        /// Name a config `match` pattern by finding a connected device it selects.
        /// Falls back to describing the pattern itself when nothing matches -- the
        /// device is simply unplugged or switched off.
        /// </summary>
        public static string ForPattern(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return "(empty rule)";
            string needle = pattern.ToLowerInvariant();

            try
            {
                foreach (var device in RawInputWatcher.ListDevices())
                {
                    if (device.Name != null && device.Name.ToLowerInvariant().Contains(needle))
                        return ForDevicePath(device.Name);
                }
            }
            catch { }

            string vidpid = FromVidPid(pattern);
            return vidpid + ", not connected";
        }

        static string WalkForFriendlyName(string devicePath)
        {
            uint node;
            if (CM_Locate_DevNodeW(out node, ToInstanceId(devicePath), 0) != 0) return null;

            for (int depth = 0; depth < MaxWalkDepth; depth++)
            {
                string desc = Property(node, CM_DRP_DEVICEDESC);
                if (IsBusNode(desc)) return null; // gone past the physical device

                string friendly = Property(node, CM_DRP_FRIENDLYNAME);
                if (!string.IsNullOrEmpty(friendly)) return friendly;

                uint parent;
                if (CM_Get_Parent(out parent, node, 0) != 0) return null;
                node = parent;
            }
            return null;
        }

        static string LeafDescription(string devicePath)
        {
            uint node;
            if (CM_Locate_DevNodeW(out node, ToInstanceId(devicePath), 0) != 0) return null;
            return Property(node, CM_DRP_DEVICEDESC);
        }

        static bool IsBusNode(string description)
        {
            if (string.IsNullOrEmpty(description)) return false;
            string lower = description.ToLowerInvariant();
            foreach (string marker in BusNodes)
                if (lower.Contains(marker)) return true;
            return false;
        }

        static string Property(uint node, uint property)
        {
            uint regType;
            uint length = 512;
            var buffer = new StringBuilder(256);
            if (CM_Get_DevNode_Registry_PropertyW(node, property, out regType, buffer, ref length, 0) != 0)
                return null;
            string value = buffer.ToString().Trim();
            return value.Length == 0 ? null : value;
        }

        static string HidProductString(string devicePath)
        {
            // Zero desired access: enough to query, and the only thing Windows allows
            // on keyboard HID devices.
            IntPtr handle = CreateFileW(devicePath, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle == INVALID_HANDLE) return null;
            try
            {
                var buffer = new StringBuilder(256);
                if (!HidD_GetProductString(handle, buffer, 256 * 2)) return null;
                string value = buffer.ToString().Trim();
                return value.Length == 0 ? null : value;
            }
            catch { return null; }
            finally { CloseHandle(handle); }
        }

        /// <summary>
        /// \\?\HID#VID_046D&amp;PID_C52B&amp;MI_00#8&amp;1360167a&amp;0&amp;0000#{guid}
        ///   -&gt; HID\VID_046D&amp;PID_C52B&amp;MI_00\8&amp;1360167a&amp;0&amp;0000
        /// </summary>
        public static string ToInstanceId(string devicePath)
        {
            string s = devicePath;
            if (s.StartsWith(@"\\?\")) s = s.Substring(4);

            int lastHash = s.LastIndexOf('#');
            if (lastHash > 0 && s.IndexOf('{', lastHash) > 0) s = s.Substring(0, lastHash);

            return s.Replace('#', '\\');
        }

        /// <summary>Last resort: identify by vendor and product id.</summary>
        static string FromVidPid(string text)
        {
            string lower = text.ToLowerInvariant();

            // Bluetooth LE form: _dev_vid&0200XXXX_pid&YYYY_
            int blePid = lower.IndexOf("_pid&");
            if (blePid >= 0)
            {
                string pid = Read(lower, blePid + 5, 4);
                string vid = null;
                int bleVid = lower.IndexOf("_vid&");
                if (bleVid >= 0) vid = Read(lower, bleVid + 5, 6); // includes a 2-digit namespace
                if (vid != null && vid.Length == 6) vid = vid.Substring(2);
                if (pid != null)
                    return "Bluetooth device " + (vid == null ? "" : vid.ToUpperInvariant() + ":") + pid.ToUpperInvariant();
            }

            // USB form: vid_046d&pid_c52b
            int usbVid = lower.IndexOf("vid_");
            int usbPid = lower.IndexOf("pid_");
            if (usbVid >= 0 && usbPid > usbVid)
            {
                string vid = Read(lower, usbVid + 4, 4);
                string pid = Read(lower, usbPid + 4, 4);
                if (vid != null && pid != null)
                    return "USB device " + vid.ToUpperInvariant() + ":" + pid.ToUpperInvariant();
            }

            return "unrecognised device";
        }

        static string Read(string text, int start, int count)
        {
            if (start < 0 || start + count > text.Length) return null;
            for (int i = start; i < start + count; i++)
                if (!Uri.IsHexDigit(text[i])) return null;
            return text.Substring(start, count);
        }
    }
}
