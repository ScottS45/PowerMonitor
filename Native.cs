// ============================================================================
// File: Native.cs
// Author: Scott Sheen (with ChatGpt)
// Created: 2026-10-02
// Updated: 2026-10-02
// Purpose: Windows interop layouts and active display discovery.
// ============================================================================

using System.Runtime.InteropServices;

namespace PowerMonitor;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]

    internal struct PowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]

    internal static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("kernel32.dll")]

    internal static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("powrprof.dll")]

    internal static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("kernel32.dll")]

    internal static extern IntPtr LocalFree(IntPtr memory);
    [StructLayout(LayoutKind.Sequential)]
    struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rational
    {
        public uint Numerator, Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Source
    {
        public Luid Adapter;
        public uint Id, ModeIndex, Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Target
    {
        public Luid Adapter;
        public uint Id, ModeIndex, Technology, Rotation, Scaling;
        public Rational Refresh;
        public uint Scanline;
        [MarshalAs(UnmanagedType.Bool)]
        public bool Available;
        public uint Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DisplayPath
    {
        public Source Source;
        public Target Target;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    struct Mode
    {
        [FieldOffset(0)]
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Header
    {
        public uint Type, Size;
        public Luid Adapter;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SourceName
    {
        public Header Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
    }

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] DisplayPath[] pathArray, ref uint modes, [Out] Mode[] modeArray, IntPtr topology);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int DisplayConfigGetDeviceInfo(ref SourceName request);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency, ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string device, int mode, ref DevMode settings);

    internal record Display(string Name, int Width, int Height, int Hz, bool Internal);

    internal static List<Display> ReadDisplays()
    {
        // Display topology can change between size discovery and retrieval; retry bounded buffer races.
        for (var retry = 0; retry < 3; retry++)
        {
            if (GetDisplayConfigBufferSizes(2, out var paths, out var modes) != 0)
                return[];
            var pathArray = new DisplayPath[paths];
            var modeArray = new Mode[modes];
            var result = QueryDisplayConfig(2, ref paths, pathArray, ref modes, modeArray, IntPtr.Zero);
            if (result == 122)
                continue;
            if (result != 0)
                return[];
            var list = new List<Display>();
            foreach (var path in pathArray.Take((int)paths))
            {
                var request = new SourceName
                {
                    Header = new Header
                    {
                        Type = 1,
                        Size = (uint)Marshal.SizeOf<SourceName>(),
                        Adapter = path.Source.Adapter,
                        Id = path.Source.Id
                    },
                    Name = ""
                };
                if (DisplayConfigGetDeviceInfo(ref request) != 0)
                    continue;
                var dm = new DevMode
                {
                    Size = (ushort)Marshal.SizeOf<DevMode>(),
                    DeviceName = "",
                    FormName = ""
                };
                EnumDisplaySettings(request.Name, -1, ref dm);
                // Embedded DisplayPort, embedded UDI, and internal panel output technology.
                var embedded = path.Target.Technology is 11 or 13 or 0x80000000;
                var hz = path.Target.Refresh.Denominator > 0 ? (int)Math.Round((double)path.Target.Refresh.Numerator / path.Target.Refresh.Denominator) : (int)dm.DisplayFrequency;
                list.Add(new(request.Name, (int)dm.PelsWidth, (int)dm.PelsHeight, hz, embedded));
            }

            return list;
        }

        return[];
    }
}
