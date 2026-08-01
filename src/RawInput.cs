using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DisplayScale
{
    public enum InputKind
    {
        Mouse = 0,
        Keyboard = 1,
        Hid = 2
    }

    public class InputEventArgs : EventArgs
    {
        public string DeviceName;  // \\?\HID#VID_046D&PID_C52B&MI_00#...
        public InputKind Kind;
    }

    /// <summary>
    /// Watches every keyboard and mouse in the system and reports which physical
    /// device produced each event.
    ///
    /// Presence detection isn't usable here: the Unifying receiver enumerates its
    /// generic keyboard/mouse endpoints whether or not the K400 is switched on, and
    /// the Bluetooth MX devices stay paired all day. Only Raw Input attributes an
    /// individual event to the hardware that generated it.
    ///
    /// The window is a real (never-shown) top-level window rather than a
    /// message-only one -- HWND_MESSAGE windows are documented to receive WM_INPUT
    /// but do so unreliably with RIDEV_INPUTSINK across Windows versions.
    /// </summary>
    internal class RawInputWatcher : Form
    {
        const int WM_INPUT = 0x00FF;
        const int WM_HOTKEY = 0x0312;
        const int HOTKEY_ID = 0xD5CA;

        const uint RIDEV_INPUTSINK = 0x00000100;
        const uint RID_HEADER = 0x10000005;
        const uint RIDI_DEVICENAME = 0x20000007;

        const ushort USAGE_PAGE_GENERIC = 0x01;
        const ushort USAGE_MOUSE = 0x02;
        const ushort USAGE_KEYBOARD = 0x06;

        [StructLayout(LayoutKind.Sequential)]
        struct RawInputDevice
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RawInputHeader
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint numDevices, uint cbSize);

        [DllImport("user32.dll")]
        static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint cbSizeHeader);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint command, IntPtr data, ref uint size);

        [StructLayout(LayoutKind.Sequential)]
        struct RawInputDeviceList
        {
            public IntPtr hDevice;
            public uint dwType;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetRawInputDeviceList([In, Out] RawInputDeviceList[] list, ref uint numDevices, uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint RegisterWindowMessage(string message);

        /// <summary>
        /// A registered window message resolves to the same id in every process, so
        /// `displayscale config` can broadcast it and the running tray instance picks
        /// it up -- no port file, no stale token on disk, nothing to clean up.
        /// </summary>
        public static readonly uint OpenSettingsMessage = RegisterWindowMessage("DisplayScale.OpenSettings");

        readonly Dictionary<IntPtr, string> _nameCache = new Dictionary<IntPtr, string>();
        readonly int _headerSize = Marshal.SizeOf(typeof(RawInputHeader));
        IntPtr _headerBuffer;

        bool _hotkeyRegistered;

        public event EventHandler<InputEventArgs> Input;
        public event EventHandler HotkeyPressed;
        public event EventHandler OpenSettingsRequested;

        public RawInputWatcher()
        {
            // Never shown, but a genuine top-level window so INPUTSINK is honoured.
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(1, 1);

            _headerBuffer = Marshal.AllocHGlobal(_headerSize);

            var handle = Handle; // force creation before registering
            GC.KeepAlive(handle);

            var devices = new RawInputDevice[2];
            devices[0].usUsagePage = USAGE_PAGE_GENERIC;
            devices[0].usUsage = USAGE_KEYBOARD;
            devices[0].dwFlags = RIDEV_INPUTSINK;
            devices[0].hwndTarget = Handle;
            devices[1].usUsagePage = USAGE_PAGE_GENERIC;
            devices[1].usUsage = USAGE_MOUSE;
            devices[1].dwFlags = RIDEV_INPUTSINK;
            devices[1].hwndTarget = Handle;

            if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf(typeof(RawInputDevice))))
                throw new InvalidOperationException("RegisterRawInputDevices failed: " + Marshal.GetLastWin32Error());
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT)
            {
                try { Dispatch(m.LParam); }
                catch { /* a malformed event must never take down the watcher */ }
            }
            else if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            {
                var handler = HotkeyPressed;
                if (handler != null) handler(this, EventArgs.Empty);
            }
            else if (OpenSettingsMessage != 0 && m.Msg == (int)OpenSettingsMessage)
            {
                var handler = OpenSettingsRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            }
            base.WndProc(ref m);
        }

        void Dispatch(IntPtr hRawInput)
        {
            uint size = (uint)_headerSize;
            uint copied = GetRawInputData(hRawInput, RID_HEADER, _headerBuffer, ref size, (uint)_headerSize);
            if (copied == uint.MaxValue || copied == 0) return;

            var header = (RawInputHeader)Marshal.PtrToStructure(_headerBuffer, typeof(RawInputHeader));
            if (header.hDevice == IntPtr.Zero) return;

            string name = ResolveDeviceName(header.hDevice);
            if (name == null) return;

            var handler = Input;
            if (handler != null)
            {
                var args = new InputEventArgs();
                args.DeviceName = name;
                args.Kind = (InputKind)header.dwType;
                handler(this, args);
            }
        }

        string ResolveDeviceName(IntPtr hDevice)
        {
            string cached;
            if (_nameCache.TryGetValue(hDevice, out cached)) return cached;

            uint charCount = 0;
            GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref charCount);
            if (charCount == 0 || charCount > 4096)
            {
                _nameCache[hDevice] = null;
                return null;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)charCount * 2);
            try
            {
                uint written = GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buffer, ref charCount);
                string name = (written == uint.MaxValue) ? null : Marshal.PtrToStringUni(buffer);
                _nameCache[hDevice] = name;
                return name;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// Claims the chord system-wide. Fails when another application already owns
        /// it -- the caller must surface that, since a silently dead hotkey is far
        /// more confusing than an error.
        /// </summary>
        public bool TryRegisterHotkey(HotkeySpec spec, out string error)
        {
            error = null;
            if (_hotkeyRegistered)
            {
                UnregisterHotKey(Handle, HOTKEY_ID);
                _hotkeyRegistered = false;
            }
            if (spec == null) return false;

            if (!RegisterHotKey(Handle, HOTKEY_ID, spec.Modifiers, spec.VirtualKey))
            {
                int err = Marshal.GetLastWin32Error();
                error = err == 1409 // ERROR_HOTKEY_ALREADY_REGISTERED
                    ? spec.Text + " is already taken by another application"
                    : "could not register " + spec.Text + " (error " + err + ")";
                return false;
            }

            _hotkeyRegistered = true;
            return true;
        }

        /// <summary>Devices are re-enumerated on hotplug, so stale handles must not stick.</summary>
        public void InvalidateCache()
        {
            _nameCache.Clear();
        }

        protected override void Dispose(bool disposing)
        {
            if (_hotkeyRegistered)
            {
                UnregisterHotKey(Handle, HOTKEY_ID);
                _hotkeyRegistered = false;
            }
            if (_headerBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_headerBuffer);
                _headerBuffer = IntPtr.Zero;
            }
            base.Dispose(disposing);
        }

        public class DeviceInfo
        {
            public string Name;
            public InputKind Kind;
        }

        /// <summary>Every keyboard/mouse Windows currently knows about, for writing match rules.</summary>
        public static List<DeviceInfo> ListDevices()
        {
            var result = new List<DeviceInfo>();
            int structSize = Marshal.SizeOf(typeof(RawInputDeviceList));

            uint count = 0;
            if (GetRawInputDeviceList(null, ref count, (uint)structSize) == uint.MaxValue || count == 0)
                return result;

            var list = new RawInputDeviceList[count];
            uint written = GetRawInputDeviceList(list, ref count, (uint)structSize);
            if (written == uint.MaxValue) return result;

            for (int i = 0; i < written; i++)
            {
                // Allowlist rather than "skip HID": Windows reports undocumented types
                // here (a touchpad collection on this machine reports 3), and only
                // mouse/keyboard are registered for WM_INPUT anyway.
                var kind = (InputKind)list[i].dwType;
                if (kind != InputKind.Mouse && kind != InputKind.Keyboard) continue;

                string name = StaticDeviceName(list[i].hDevice);
                if (string.IsNullOrEmpty(name)) continue;

                var info = new DeviceInfo();
                info.Name = name;
                info.Kind = kind;
                result.Add(info);
            }

            result.Sort(delegate(DeviceInfo a, DeviceInfo b)
            {
                int byKind = a.Kind.CompareTo(b.Kind);
                return byKind != 0 ? byKind : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        static string StaticDeviceName(IntPtr hDevice)
        {
            uint charCount = 0;
            GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref charCount);
            if (charCount == 0 || charCount > 4096) return null;

            IntPtr buffer = Marshal.AllocHGlobal((int)charCount * 2);
            try
            {
                uint written = GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buffer, ref charCount);
                return (written == uint.MaxValue) ? null : Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// Human-readable name for either a full device path or a config `match`
        /// pattern. Patterns are fragments rather than real paths, so they resolve by
        /// finding a connected device they select.
        /// </summary>
        public static string Describe(string deviceNameOrPattern)
        {
            if (string.IsNullOrEmpty(deviceNameOrPattern)) return "(unknown)";
            return deviceNameOrPattern.StartsWith(@"\\?\")
                ? DeviceNames.ForDevicePath(deviceNameOrPattern)
                : DeviceNames.ForPattern(deviceNameOrPattern);
        }
    }
}
