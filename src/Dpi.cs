using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DisplayScale
{
    /// <summary>
    /// Per-monitor DPI scaling via the CCD (Connecting and Configuring Displays) APIs.
    ///
    /// Windows exposes no documented way to set the display scale percentage. The
    /// Settings app drives it through two undocumented DISPLAYCONFIG_DEVICE_INFO_TYPE
    /// values, -3 (get) and -4 (set), which read/write a *relative* offset into a fixed
    /// ladder of scale percentages. Everything else here is documented API.
    ///
    /// The offset is relative to the display's "recommended" scale, so the absolute
    /// percentage for a given offset differs per monitor. Conversion happens in
    /// MonitorScale below.
    /// </summary>
    internal static class Dpi
    {
        /// The fixed ladder of scale percentages Windows supports, in order.
        public static readonly int[] Ladder = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

        const uint QDC_ONLY_ACTIVE_PATHS = 2;

        const int GET_SOURCE_NAME = 1;
        const int GET_TARGET_NAME = 2;
        const int GET_DPI_SCALE = -3; // undocumented
        const int SET_DPI_SCALE = -4; // undocumented

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PathSourceInfo
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Rational
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PathTargetInfo
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public Rational refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PathInfo
        {
            public PathSourceInfo sourceInfo;
            public PathTargetInfo targetInfo;
            public uint flags;
        }

        // We never inspect mode info; a correctly sized opaque blob keeps the
        // QueryDisplayConfig array stride right.
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        struct ModeInfoBlob
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DeviceInfoHeader
        {
            public int type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DpiScaleGet
        {
            public DeviceInfoHeader header;
            public int minScaleRel;
            public int curScaleRel;
            public int maxScaleRel;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DpiScaleSet
        {
            public DeviceInfoHeader header;
            public int scaleRel;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SourceDeviceName
        {
            public DeviceInfoHeader header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TargetDeviceName
        {
            public DeviceInfoHeader header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string monitorDevicePath;
        }

        [DllImport("user32.dll")]
        static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPath, out uint numMode);

        [DllImport("user32.dll")]
        static extern int QueryDisplayConfig(uint flags, ref uint numPath, [Out] PathInfo[] paths,
            ref uint numMode, [Out] ModeInfoBlob[] modes, IntPtr currentTopologyId);

        // Distinct managed names per overload: letting the marshaller pick between
        // same-named overloads is how the first prototype ended up passing garbage.
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        static extern int GetDpiScaleInfo(ref DpiScaleGet request);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")]
        static extern int SetDpiScaleInfo(ref DpiScaleSet request);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        static extern int GetSourceNameInfo(ref SourceDeviceName request);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        static extern int GetTargetNameInfo(ref TargetDeviceName request);

        /// <summary>A single active display source and its scaling state.</summary>
        public class MonitorScale
        {
            public string GdiName;       // \\.\DISPLAY6
            public string FriendlyName;  // Odyssey G70NC
            public string DevicePath;

            internal LUID AdapterId;
            internal uint SourceId;

            public int MinRel, CurRel, MaxRel;

            /// Ladder index of this display's "recommended" (100%-equivalent) scale.
            public int RecommendedIndex { get { return -MinRel; } }
            public int CurrentIndex { get { return RecommendedIndex + CurRel; } }
            public int MaxIndex { get { return RecommendedIndex + MaxRel; } }

            public int CurrentPercent { get { return PercentAt(CurrentIndex); } }
            public int MaxPercent { get { return PercentAt(MaxIndex); } }
            public int RecommendedPercent { get { return PercentAt(RecommendedIndex); } }

            static int PercentAt(int index)
            {
                if (index < 0 || index >= Ladder.Length) return -1;
                return Ladder[index];
            }

            public bool Supports(int percent)
            {
                int idx = Array.IndexOf(Ladder, percent);
                return idx >= 0 && idx <= MaxIndex;
            }

            public List<int> AvailablePercents()
            {
                var list = new List<int>();
                for (int i = 0; i <= MaxIndex && i < Ladder.Length; i++) list.Add(Ladder[i]);
                return list;
            }

            /// <summary>True when the profile key names this monitor (friendly name or GDI name).</summary>
            public bool Matches(string key)
            {
                if (string.IsNullOrEmpty(key)) return false;
                if (string.Equals(key, GdiName, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.IsNullOrEmpty(FriendlyName)) return false;
                return FriendlyName.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            public override string ToString()
            {
                return string.Format("{0} ({1})", FriendlyName, GdiName);
            }
        }

        /// <summary>Enumerate every active display source with its current scale state.</summary>
        public static List<MonitorScale> Enumerate()
        {
            var result = new List<MonitorScale>();

            uint numPath, numMode;
            int rc = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out numPath, out numMode);
            if (rc != 0) throw new InvalidOperationException("GetDisplayConfigBufferSizes failed: " + rc);

            var paths = new PathInfo[numPath];
            var modes = new ModeInfoBlob[numMode];
            rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPath, paths, ref numMode, modes, IntPtr.Zero);
            if (rc != 0) throw new InvalidOperationException("QueryDisplayConfig failed: " + rc);

            for (int i = 0; i < numPath; i++)
            {
                var m = new MonitorScale();
                m.AdapterId = paths[i].sourceInfo.adapterId;
                m.SourceId = paths[i].sourceInfo.id;

                var sn = new SourceDeviceName();
                sn.header.type = GET_SOURCE_NAME;
                sn.header.size = (uint)Marshal.SizeOf(typeof(SourceDeviceName));
                sn.header.adapterId = m.AdapterId;
                sn.header.id = m.SourceId;
                if (GetSourceNameInfo(ref sn) == 0) m.GdiName = sn.viewGdiDeviceName;

                var tn = new TargetDeviceName();
                tn.header.type = GET_TARGET_NAME;
                tn.header.size = (uint)Marshal.SizeOf(typeof(TargetDeviceName));
                tn.header.adapterId = paths[i].targetInfo.adapterId;
                tn.header.id = paths[i].targetInfo.id;
                if (GetTargetNameInfo(ref tn) == 0)
                {
                    m.FriendlyName = tn.monitorFriendlyDeviceName;
                    m.DevicePath = tn.monitorDevicePath;
                }

                var g = new DpiScaleGet();
                g.header.type = GET_DPI_SCALE;
                g.header.size = (uint)Marshal.SizeOf(typeof(DpiScaleGet));
                g.header.adapterId = m.AdapterId;
                g.header.id = m.SourceId;
                if (GetDpiScaleInfo(ref g) != 0) continue; // source can't be scaled; skip it

                m.MinRel = g.minScaleRel;
                m.CurRel = g.curScaleRel;
                m.MaxRel = g.maxScaleRel;

                result.Add(m);
            }

            return result;
        }

        /// <summary>
        /// Set a monitor to an absolute scale percentage. Returns false (with a reason)
        /// when the percentage isn't on the ladder or exceeds what the panel allows.
        /// </summary>
        public static bool SetScale(MonitorScale monitor, int percent, out string error)
        {
            error = null;

            int targetIdx = Array.IndexOf(Ladder, percent);
            if (targetIdx < 0)
            {
                error = percent + "% is not a Windows scale step (" + string.Join(", ", Array.ConvertAll(Ladder, x => x + "%")) + ")";
                return false;
            }

            int rel = targetIdx - monitor.RecommendedIndex;
            if (rel < monitor.MinRel || rel > monitor.MaxRel)
            {
                error = string.Format("{0} does not support {1}% (max {2}%)",
                    monitor.FriendlyName, percent, monitor.MaxPercent);
                return false;
            }

            if (rel == monitor.CurRel) return true; // already there; don't churn the desktop

            var s = new DpiScaleSet();
            s.header.type = SET_DPI_SCALE;
            s.header.size = (uint)Marshal.SizeOf(typeof(DpiScaleSet));
            s.header.adapterId = monitor.AdapterId;
            s.header.id = monitor.SourceId;
            s.scaleRel = rel;

            int rc = SetDpiScaleInfo(ref s);
            if (rc != 0)
            {
                error = "DisplayConfigSetDeviceInfo failed: " + rc;
                return false;
            }

            monitor.CurRel = rel;
            return true;
        }
    }
}
