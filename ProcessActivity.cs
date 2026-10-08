// ============================================================================
// File: ProcessActivity.cs
// Author: Scott Sheen (with ChatGpt)
// Created: 2026-10-02
// Updated: 2026-10-03
// Purpose: Process activity rates, GPU engine mapping, and app ranking.
// ============================================================================

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PowerMonitor;

public record GpuEngineActivity(int Pid, string Adapter, string Luid, int PhysicalIndex, string Engine, double Usage, bool Mapped);
public record ProcessActivity(int Pid, string Name, string? Path, DateTimeOffset? Started, double? CpuPercent, double? IoMiBPerSecond, double? MemoryMb, GpuEngineActivity[] Engines)
{
    public double? EstimatedWatts
    {
        get; init;
    }
    public double? EstimatedMilliAmpHours
    {
        get; init;
    }
}
public record AppActivity(string Name, string? Path, int[] Pids, double? CpuPercent, double? GpuPercent, double? IoMiBPerSecond, double? MemoryMb, string Gpu, string Impact, string Reason, ProcessActivity[] Processes)
{

    public double? EstimatedWatts
    {
        get; init;
    }

    public double? EstimatedMilliAmpHours
    {
        get; init;
    }

    public string EnergySource { get; init; } = "Legacy activity model (uncalibrated)";
    public double? EstimatedWattHours { get; init; }

    public double Rank => ActivityRules.Score(CpuPercent, GpuPercent, IoMiBPerSecond);
}

public record CpuResidualPart(string Name, double? CpuPercent, string Explanation);
public record ProcessSnapshot(DateTimeOffset Time, AppActivity[] Apps, GpuEngineActivity[] Engines, string Status, double CollectionMs, int AccessDenied, Dictionary<string, string> Adapters)
{
    public double? TotalCpuPercent { get; init; }
    public double? AccountedCpuPercent { get; init; }
    public double? UnattributedCpuPercent { get; init; }
    public double CpuIntervalSeconds { get; init; }
    public CpuResidualPart[] SystemCpuParts { get; init; } = [];
    public string CpuCoverageStatus { get; init; } = "";
    public PowerAccounting? Accounting
    {
        get; init;
    }

    public double? GpuPercent
    {
        get; init;
    }
}

public static class ActivityRules
{

    public static double? CpuRate(ulong cpu, ulong previous, long identity, long previousIdentity, double seconds, int processors) => identity != previousIdentity || seconds <= 0 || cpu < previous ? null : Math.Clamp((cpu - previous) / 10_000_000d / seconds / Math.Max(1, processors) * 100, 0, 100);

    public static double Score(double? cpu, double? gpu, double? io) => Math.Max((cpu ?? 0) * 3, Math.Max(gpu ?? 0, Math.Min(100, (io ?? 0) * 4)));

    public static string Impact(double? cpu, double? gpu, double? io)
    {
        if (!cpu.HasValue && !io.HasValue && (!gpu.HasValue || gpu < 1))
            return "Unknown";
        if (cpu >= 15 || gpu >= 40 || io >= 25)
            return "Very high";
        if (cpu >= 5 || gpu >= 15 || io >= 10)
            return "High";
        if (cpu >= 1 || gpu >= 5 || io >= 2)
            return "Medium";
        if (cpu >= 0.2 || gpu >= 1 || io >= 0.2)
            return "Low";
        return "Very low";
    }

    public static double BusiestEngine(IEnumerable<GpuEngineActivity> engines) => engines.GroupBy(e => (e.Luid, e.PhysicalIndex, e.Engine)).Select(g => Math.Clamp(g.Sum(e => e.Usage), 0, 100)).DefaultIfEmpty(0).Max();

    public static AppActivity[] Group(IEnumerable<ProcessActivity> processes, bool gpuAvailable)
    {
        // Group related process instances for app-level diagnostics while retaining individual PID details.
        return processes.GroupBy(p => p.Path ?? p.Name, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var rows = group.ToArray();
            var first = rows[0];
            var cpu = SumKnown(rows.Select(p => p.CpuPercent));
            var io = SumKnown(rows.Select(p => p.IoMiBPerSecond));
            var memory = SumKnown(rows.Select(p => p.MemoryMb));
            var engines = rows.SelectMany(p => p.Engines).ToArray();
            double? gpu = gpuAvailable ? BusiestEngine(engines) : null;
            var adapters = string.Join(", ", engines.Where(e => e.Usage > 0.05).Select(e => e.Adapter).Distinct());
            if (adapters.Length == 0)
                adapters = gpuAvailable ? "No active GPU engine" : "Unavailable";
            var cpuCount = rows.Count(p => p.CpuPercent.HasValue);
            var reason = $"CPU: {(cpu.HasValue ? cpu.Value.ToString("F1") + "% of total system capacity" : "warming up / inaccessible")} ({cpuCount}/{rows.Length} processes measured).\r\n" + $"GPU: {(gpu.HasValue ? gpu.Value.ToString("F1") + "% on the busiest engine" : "unavailable")}. {adapters}.\r\n" + $"I/O: {(io.HasValue ? io.Value.ToString("F2") + " MiB/s" : "unavailable")}; includes file, network and device I/O, not disk alone.\r\n" + "Activity suggests a possible contributor; it is not measured app wattage. High activity may be expected for your workload.";
            return new AppActivity(first.Name, first.Path, rows.Select(p => p.Pid).ToArray(), cpu, gpu, io, memory, adapters, Impact(cpu, gpu, io), reason, rows);
        }).OrderByDescending(a => a.Rank).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static double? SumKnown(IEnumerable<double?> values)
    {
        // An absent measurement stays unknown; a measured idle value is zero.
        var known = values.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        return known.Length > 0 ? known.Sum() : null;
    }
}

public sealed class ProcessMonitor : IDisposable
{

    private record Previous(ulong Cpu, long Created, ulong? Io, long Tick, string? Path);

    private readonly Dictionary<int, Previous> _previous = [];

    private GpuActivityReader? _gpu;

    private readonly ProcessCounterFallback _fallback = new();

    public ProcessSnapshot Collect(bool gpuEnabled, bool restrictedCounters = false)
    {
        // Keep unavailable counters nullable and tolerate processes disappearing during collection.
        var watch = Stopwatch.StartNew();
        if (!gpuEnabled)
        {
            _gpu?.Dispose();
            _gpu = null;
        }
        else
            _gpu ??= new GpuActivityReader();
        var gpu = _gpu?.Collect() ?? new GpuReading([], false, "GPU activity monitoring disabled");
        var perPid = gpu.Engines.GroupBy(e => e.Pid).ToDictionary(g => g.Key, g => g.ToArray());

        // One native counter query covers all processes without a WMI provider or elevation.
        var fallback = _fallback.Read();
        var rows = new List<ProcessActivity>();
        var current = new HashSet<int>();
        var denied = 0;
        foreach (var process in Process.GetProcesses())
            using (process)
            {
                try
                {
                    var pid = process.Id;
                    if (pid == 0)
                        continue;

                    // Request limited query access only; inaccessible processes do not trigger elevation.
                    var handle = ProcessNative.OpenProcess(0x1000, false, pid);
                    if (handle == IntPtr.Zero)
                    {
                        denied++;
                        rows.Add(new(pid, process.ProcessName, null, fallback.GetValueOrDefault(pid)?.Started, fallback.GetValueOrDefault(pid)?.Cpu, fallback.GetValueOrDefault(pid)?.Io, fallback.GetValueOrDefault(pid)?.Memory, perPid.GetValueOrDefault(pid) ?? []));
                        continue;
                    }

                    try
                    {
                        if (!ProcessNative.GetProcessTimes(handle, out var created, out _, out var kernel, out var user))
                        {
                            denied++;
                            rows.Add(new(pid, process.ProcessName, null, fallback.GetValueOrDefault(pid)?.Started, fallback.GetValueOrDefault(pid)?.Cpu, null, null, perPid.GetValueOrDefault(pid) ?? []));
                            continue;
                        }

                        var tick = Stopwatch.GetTimestamp();
                        var cpu = kernel + user;
                        var io = ProcessNative.GetProcessIoCounters(handle, out var counters) ? (ulong?)(counters.ReadTransferCount + counters.WriteTransferCount + counters.OtherTransferCount) : null;
                        var memory = ProcessNative.Memory(handle);
                        _previous.TryGetValue(pid, out var prior);

                        // Use a monotonic clock for rates; wall-clock corrections must not distort deltas.
                        var elapsed = prior != null ? (tick - prior.Tick) / (double)Stopwatch.Frequency : 0;
                        var nativePercent = prior != null ? ActivityRules.CpuRate(cpu, prior.Cpu, (long)created, prior.Created, elapsed, Environment.ProcessorCount) : null;
                        // Prefer the shared raw-counter interval for every PID, including accessible ones.
                        var percent = fallback.TryGetValue(pid, out var counter) ? counter.Cpu : _fallback.TotalCpu.HasValue ? null : nativePercent;
                        double? ioRate = prior?.Created == (long)created && prior.Io.HasValue && io.HasValue && io >= prior.Io && elapsed > 0 ? (io.Value - prior.Io.Value) / elapsed / 1048576d : null;
                        var path = prior?.Created == (long)created ? prior.Path : ProcessNative.Path(handle);
                        _previous[pid] = new(cpu, (long)created, io, tick, path);
                        current.Add(pid);
                        rows.Add(new(pid, process.ProcessName, path, DateTimeOffset.FromFileTime((long)created), percent, ioRate, memory, perPid.GetValueOrDefault(pid) ?? []));
                    }
                    finally
                    {
                        ProcessNative.CloseHandle(handle);
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    denied++;
                }
            }

        // Discard exited processes so baseline storage cannot grow indefinitely.
        foreach (var pid in _previous.Keys.Where(p => !current.Contains(p)).ToArray())
            _previous.Remove(pid);
        var listed = rows.Select(r => r.Pid).ToHashSet();
        foreach (var counter in fallback.Where(p => !listed.Contains(p.Key)))
            rows.Add(new(counter.Key, counter.Value.Name, null, counter.Value.Started, counter.Value.Cpu, null, null, perPid.GetValueOrDefault(counter.Key) ?? []));
        var apps = ActivityRules.Group(rows, gpu.Available);
        var snapshot = new ProcessSnapshot(DateTimeOffset.UtcNow, apps, gpu.Engines, gpu.Status + "; " + _fallback.Status, watch.Elapsed.TotalMilliseconds, denied, _gpu?.Adapters ?? [])
        {
            GpuPercent = gpu.Available ? ActivityRules.BusiestEngine(gpu.Engines) : null
        };
        return CpuCoverage.Apply(snapshot, _fallback.TotalCpu, _fallback.IntervalSeconds, _fallback.DpcCpu, _fallback.InterruptCpu);
    }

    public void ResetBaseline()
    {
        // Discard paused or stale intervals so resume does not average across unmonitored time.
        _previous.Clear();
        _fallback.Dispose();
        _gpu?.Dispose();
        _gpu = null;
    }

    public void Dispose()
    {
        // Release owned native handles and flush pending work when the component shuts down.
        _fallback.Dispose();
        _gpu?.Dispose();
        _gpu = null;
    }
}

internal static class ProcessNative
{
    [StructLayout(LayoutKind.Sequential)]

    internal struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]

    private struct MemoryCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSet, WorkingSet, QuotaPeakPagedPool, QuotaPagedPool, QuotaPeakNonPagedPool, QuotaNonPagedPool, Pagefile, PeakPagefile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]

    internal static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")]

    internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]

    internal static extern bool GetProcessTimes(IntPtr handle, out ulong creation, out ulong exit, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")]

    internal static extern bool GetProcessIoCounters(IntPtr handle, out IoCounters counters);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]

    private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll")]

    private static extern bool K32GetProcessMemoryInfo(IntPtr handle, ref MemoryCounters counters, uint size);

    internal static string? Path(IntPtr handle)
    {
        var path = new StringBuilder(2048);
        uint count = 2048;
        return QueryFullProcessImageName(handle, 0, path, ref count) ? path.ToString() : null;
    }

    internal static double? Memory(IntPtr handle)
    {
        var counters = new MemoryCounters
        {
            Size = (uint)Marshal.SizeOf<MemoryCounters>()
        };
        return K32GetProcessMemoryInfo(handle, ref counters, counters.Size) ? (double)counters.WorkingSet / 1048576 : null;
    }
}

public record GpuReading(GpuEngineActivity[] Engines, bool Available, string Status);
public sealed class GpuActivityReader : IDisposable
{

    private IntPtr _query, _counter;

    private bool _primed;

    private string? _failure;

    public Dictionary<string, string> Adapters { get; } = [];

    private static readonly Regex InstancePattern = new(@"pid_(?<pid>\d+)_luid_0x(?<high>[0-9a-f]+)_0x(?<low>[0-9a-f]+)_phys_(?<physical>\d+)_eng_(?<index>\d+)_engtype_(?<engine>.+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public GpuActivityReader()
    {
        try
        {
            foreach (var pair in AdapterInventory.Read())
                Adapters[pair.Key] = pair.Value;
            var status = PdhOpenQuery(null, UIntPtr.Zero, out _query);
            if (status != 0)
                throw new InvalidOperationException($"PDH open: 0x{status:X8}");
            status = PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", UIntPtr.Zero, out _counter);
            if (status != 0)
                throw new InvalidOperationException($"GPU engine counter: 0x{status:X8}");
        }
        catch (Exception e)
        {
            _failure = e.Message;
        }
    }

    public static GpuEngineActivity? Parse(string instance, double value, IReadOnlyDictionary<string, string> adapters)
    {
        // Match GPU adapter LUIDs, not enumeration order. Unresolved physical adapters remain explicitly unknown.
        if (!double.IsFinite(value) || value < 0)
            return null;
        var match = InstancePattern.Match(instance);
        if (!match.Success)
            return null;
        if (!int.TryParse(match.Groups["pid"].Value, out var pid) || !int.TryParse(match.Groups["physical"].Value, out var physical))
            return null;
        var high = uint.Parse(match.Groups["high"].Value, NumberStyles.HexNumber);
        var low = uint.Parse(match.Groups["low"].Value, NumberStyles.HexNumber);
        var luid = $"{high:X8}:{low:X8}";
        var mapped = physical == 0 && adapters.TryGetValue(luid, out _);
        var adapter = mapped ? adapters[luid] : $"Unresolved adapter {luid} / physical {physical}";
        // Physical indices belong to an adapter LUID, not a global GPU numbering scheme.
        return new(pid, adapter, luid, physical, $"{match.Groups["engine"].Value} (engine {match.Groups["index"].Value})", Math.Clamp(value, 0, 100), mapped);
    }

    public GpuReading Collect()
    {
        // Keep unavailable counters nullable and tolerate processes disappearing during collection.
        if (_failure != null)
            return new([], false, _failure);
        var status = PdhCollectQueryData(_query);
        if (status != 0)
        {
            _primed = false;
            return new([], false, $"GPU counters unavailable (0x{status:X8})");
        }

        // PDH rate counters need two observations; the first sample is unavailable, not idle.
        if (!_primed)
        {
            _primed = true;
            return new([], false, "GPU activity warming up; CPU/I/O need a second sample too");
        }

        for (var retry = 0; retry < 3; retry++)
        {
            uint size = 0, count = 0;
            status = PdhGetFormattedCounterArray(_counter, 0x200, ref size, ref count, IntPtr.Zero);
            if (status == 0 && size == 0)
                return new([], true, "Windows GPU counters: no active instances");
            if (status != 0x800007D2 || size == 0 || size > 64 * 1024 * 1024)
                return new([], false, $"GPU counters unavailable (0x{status:X8})");

            // The native variable-size counter array is always freed in the following finally block.
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                status = PdhGetFormattedCounterArray(_counter, 0x200, ref size, ref count, buffer);
                if (status == 0x800007D2)
                    continue;
                if (status != 0)
                    return new([], false, $"GPU counter read failed (0x{status:X8})");
                var list = new List<GpuEngineActivity>();
                var itemSize = Marshal.SizeOf<CounterItem>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(buffer, i * itemSize));
                    if (item.Value.Status > 1)
                        continue;
                    var name = Marshal.PtrToStringUni(item.Name) ?? "";
                    var parsed = Parse(name, item.Value.Value, Adapters);
                    if (parsed != null)
                        list.Add(parsed);
                }

                var unresolved = list.Where(e => e.Usage > 0.05 && !e.Mapped).Select(e => e.Luid).Distinct().Count();
                return new(list.ToArray(), true, unresolved > 0 ? $"Windows GPU counters; {unresolved} adapter mapping(s) unresolved" : "Windows GPU counters; adapters matched by LUID");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return new([], false, "GPU counters changed during collection; retry on next interval");
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]

    private struct CounterValue
    {
        [FieldOffset(0)]
        public uint Status;
        [FieldOffset(8)]
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]

    private struct CounterItem
    {
        public IntPtr Name;
        public CounterValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]

    private static extern uint PdhOpenQuery(string? source, UIntPtr data, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]

    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, UIntPtr data, out IntPtr counter);
    [DllImport("pdh.dll")]

    private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]

    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, ref uint count, IntPtr buffer);
    [DllImport("pdh.dll")]

    private static extern uint PdhCloseQuery(IntPtr query);

    public void Dispose()
    {
        // Release owned native handles and flush pending work when the component shuts down.
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }
}

internal static class AdapterInventory
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]

    private struct Description
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Name;
        public uint Vendor, Device, Subsystem, Revision;
        public nuint DedicatedVideo, DedicatedSystem, SharedSystem;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]

    private delegate int EnumAdapters(IntPtr self, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]

    private delegate int GetDescription(IntPtr self, out Description description);
    [DllImport("dxgi.dll")]

    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);

    private static T Method<T>(IntPtr com, int slot)
        where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(com), slot * IntPtr.Size));

    internal static Dictionary<string, string> Read()
    {
        var result = new Dictionary<string, string>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out var factory));
        try
        {
            var enumerate = Method<EnumAdapters>(factory, 12);
            for (uint i = 0; i < 32; i++)
            {
                var status = enumerate(factory, i, out var adapter);
                if (status == unchecked((int)0x887A0002))
                    break;
                Marshal.ThrowExceptionForHR(status);
                try
                {
                    Marshal.ThrowExceptionForHR(Method<GetDescription>(adapter, 10)(adapter, out var d));
                    if ((d.Flags & 2) == 0)
                        result[$"{unchecked((uint)d.LuidHigh):X8}:{d.LuidLow:X8}"] = d.Name;
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.Release(factory);
        }

        return result;
    }
}
