// Purpose: Read existing EMI, NVIDIA NVML and AMD ADLX interfaces without installing drivers.
// Author: Scott Sheen (with ChatGpt). Created/Updated: 2026-10-03.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PowerMonitor;

public record HardwareReading(double? CpuWatts, double? NvidiaWatts, double? AmdWatts, string Status);

public sealed class HardwareEnergy : IDisposable
{
    private readonly EmiCounter _emi = new();
    private IntPtr _nv, _adlx, _system, _performance, _list;
    private bool _nvReady, _amdReady;
    private readonly List<IntPtr> _amdGpus = [];
    private string _nvStatus = "", _amdStatus = "";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArgs();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Count(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Handle(uint index, out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvPower(IntPtr handle, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PowerLimits(IntPtr handle, out uint minimum, out uint maximum);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Version(out ulong version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Initialize(ulong version, out IntPtr system);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetObject(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ListSize(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ListAt(IntPtr self, uint index, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Metrics(IntPtr self, IntPtr gpu, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Metric(IntPtr self, out double value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReleaseObject(IntPtr self);
    private static T Export<T>(IntPtr library, string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    private static T Method<T>(IntPtr self, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(self), slot * IntPtr.Size));
    private static void Release(IntPtr value) { if (value != IntPtr.Zero) Method<ReleaseObject>(value, 1)(value); }
    public static double? ValidPower(double value) => double.IsFinite(value) && value >= 0 && value <= 1500 ? value : null;
    public static double? CounterWatts(ulong current, ulong previous, double seconds) => current >= previous && seconds > 0 ? ValidPower((current - previous) * 3.6e-9 / seconds) : null;

    public HardwareEnergy(bool gpu)
    {
        if (!gpu) { _nvStatus = "NVIDIA probe disabled"; _amdStatus = "AMD probe disabled"; return; }
        // Use only installed System32 driver DLLs; do not search application or current directories.
        try
        {
            _nv = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "nvml.dll"));
            _nvReady = Export<NoArgs>(_nv, "nvmlInit_v2")() == 0;
            _nvStatus = _nvReady ? "NVML" : "NVML initialization unavailable";
        }
        catch (Exception e) { _nvStatus = "NVML: " + e.Message; }
        try
        {
            _adlx = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "amdadlx64.dll"));
            if (Export<Version>(_adlx, "ADLXQueryFullVersion")(out var version) != 0 || Export<Initialize>(_adlx, "ADLXInitialize")(version, out _system) != 0)
                throw new InvalidOperationException("initialization unsupported");
            _amdReady = true;
            if (Method<GetObject>(_system, 1)(_system, out _list) != 0 || Method<GetObject>(_system, 9)(_system, out _performance) != 0)
                throw new InvalidOperationException("performance interface unavailable");
            var count = Method<ListSize>(_list, 3)(_list);
            for (uint i = 0; i < Math.Min(count, 16); i++)
                if (Method<ListAt>(_list, 11)(_list, i, out var card) == 0) _amdGpus.Add(card);
            _amdStatus = "ADLX";
        }
        catch (Exception e) { _amdStatus = "ADLX: " + e.Message; }
    }

    public HardwareReading Read()
    {
        double? nv = null, amd = null;
        if (_nvReady)
        {
            if (Export<Count>(_nv, "nvmlDeviceGetCount_v2")(out var count) == 0 && count == 1 && Export<Handle>(_nv, "nvmlDeviceGetHandleByIndex_v2")(0, out var card) == 0)
            {
                var result = Export<NvPower>(_nv, "nvmlDeviceGetPowerUsage")(card, out var mw);
                nv = result == 0 ? ValidPower(mw / 1000d) : null;
                if (NativeLibrary.TryGetExport(_nv, "nvmlDeviceGetPowerManagementLimitConstraints", out var limitsAddress) && Marshal.GetDelegateForFunctionPointer<PowerLimits>(limitsAddress)(card, out _, out var maximum) == 0 && maximum > 0 && mw > maximum * 1.05)
                {
                    nv = null;
                    result = -100; // Driver returned a sample above its own maximum board power.
                }
                _nvStatus = nv.HasValue ? "NVIDIA board power (NVML)" : result == -100 ? "NVML rejected implausible power above driver maximum" : $"NVML power unsupported/error {result}";
            }
            else _nvStatus = "NVML adapter identity ambiguous/unavailable";
        }
        if (_performance != IntPtr.Zero && _amdGpus.Count == 1)
        {
            IntPtr metrics = IntPtr.Zero;
            try
            {
                var result = Method<Metrics>(_performance, 18)(_performance, _amdGpus[0], out metrics);
                if (result == 0 && metrics != IntPtr.Zero)
                {
                    // Chip power is a diagnostic only: an APU's GPU can overlap CPU package power.
                    result = Method<Metric>(metrics, 9)(metrics, out var watts);
                    amd = result == 0 ? ValidPower(watts) : null;
                }
                _amdStatus = amd.HasValue ? "AMD GPU chip power (ADLX; may overlap CPU package)" : $"ADLX power unsupported/error {result}";
            }
            finally { Release(metrics); }
        }
        var cpu = _emi.Read();
        return new(cpu, nv, amd, $"{_emi.Status}; {_nvStatus}; {_amdStatus}");
    }

    public void Dispose()
    {
        _emi.Dispose();
        foreach (var gpu in _amdGpus) Release(gpu);
        Release(_performance);
        Release(_list);
        if (_amdReady) Export<NoArgs>(_adlx, "ADLXTerminate")();
        if (_nvReady) Export<NoArgs>(_nv, "nvmlShutdown")();
        if (_adlx != IntPtr.Zero) NativeLibrary.Free(_adlx);
        if (_nv != IntPtr.Zero) NativeLibrary.Free(_nv);
    }
}

internal sealed class EmiCounter : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct InterfaceData { public uint Size; public Guid Class; public uint Flags; public IntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr window, uint flags);
    [DllImport("setupapi.dll")] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, uint inputSize, byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
    private SafeFileHandle? _device;
    private int _channels, _index;
    private ulong? _energy, _time;
    public string Status { get; private set; } = "CPU EMI energy counter unavailable";
    private static byte[]? Control(SafeFileHandle device, uint code, int size)
    {
        var bytes = new byte[size];
        return DeviceIoControl(device, code, IntPtr.Zero, 0, bytes, (uint)size, out var returned, IntPtr.Zero) && returned == size ? bytes : null;
    }

    public EmiCounter()
    {
        var guid = new Guid("45BD8344-7ED6-49CF-A440-C276C933B053");
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 18);
        if (set == new IntPtr(-1)) return;
        try
        {
            for (uint i = 0; i < 64; i++)
            {
                var data = new InterfaceData { Size = (uint)Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var size, IntPtr.Zero);
                if (size < 8 || size > 65536) continue;
                var detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4)!;
                    var handle = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid) { handle.Dispose(); continue; }
                    bool keep = false;
                    try
                    {
                        var version = Control(handle, 0x224000, 2);
                        var length = Control(handle, 0x224004, 4);
                        if (version == null || BitConverter.ToUInt16(version) != 2 || length == null) continue;
                        var n = BitConverter.ToUInt32(length);
                        if (n < 68 || n > 65536) continue;
                        var metadata = Control(handle, 0x224008, (int)n);
                        if (metadata == null) continue;
                        var channels = BitConverter.ToUInt16(metadata, 66);
                        if (channels is < 1 or > 64) continue;
                        int offset = 68;
                        for (int c = 0; c < channels && offset + 6 <= metadata.Length; c++)
                        {
                            var unit = BitConverter.ToUInt32(metadata, offset);
                            var nameSize = BitConverter.ToUInt16(metadata, offset + 4);
                            if (nameSize < 2 || nameSize % 2 != 0 || offset + 6 + nameSize > metadata.Length) break;
                            var name = Encoding.Unicode.GetString(metadata, offset + 6, nameSize).TrimEnd('\0');
                            if (unit == 0 && name.EndsWith("_PKG", StringComparison.OrdinalIgnoreCase))
                            {
                                _device = handle; _channels = channels; _index = c; keep = true;
                                Status = "CPU EMI package counter: " + name;
                                return;
                            }
                            offset += 6 + nameSize;
                        }
                    }
                    finally { if (!keep) handle.Dispose(); }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    public double? Read()
    {
        if (_device == null) return null;
        var data = Control(_device, 0x22400c, _channels * 16);
        if (data == null) { _energy = _time = null; Status = "CPU EMI read failed"; return null; }
        var energy = BitConverter.ToUInt64(data, _index * 16);
        var time = BitConverter.ToUInt64(data, _index * 16 + 8);
        // EMI AbsoluteTime is 100 ns. Resets/wraps break the interval rather than fabricating energy.
        double? watts = _energy.HasValue && _time.HasValue && time > _time.Value ? HardwareEnergy.CounterWatts(energy, _energy.Value, (time - _time.Value) / 1e7) : null;
        _energy = energy; _time = time;
        return watts;
    }
    public void Dispose() => _device?.Dispose();
}