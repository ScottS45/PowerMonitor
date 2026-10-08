// ============================================================================
// File: ProcessCounterFallback.cs
// Author: Scott Sheen (with ChatGpt)
// Created: 2026-10-02
// Updated: 2026-10-03
// Purpose: Native Windows raw process CPU counters, including protected processes.
// ============================================================================
using System.Runtime.InteropServices;

namespace PowerMonitor;

internal record CounterProcess(double? Cpu, double? Io, double? Memory)
{
    public string Name { get; init; } = "";
    public DateTimeOffset? Started { get; init; }
}

internal sealed class ProcessCounterFallback : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct RawCounter
    {
        public uint Status;
        public FileTime Time;
        public long First, Second;
        public uint Count;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RawItem { public IntPtr Name; public RawCounter Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, IntPtr data, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr data, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetRawCounterArray(IntPtr counter, ref uint bytes, out uint count, IntPtr items);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
    private IntPtr _query, _cpu, _pid, _birth, _dpc, _interrupt;
    private long? _previousDpc, _previousInterrupt;
    public double? DpcCpu { get; private set; }
    public double? InterruptCpu { get; private set; }
    private readonly Dictionary<int, (long Cpu, long Birth)> _previous = [];
    private ulong? _systemPrevious, _idlePrevious;
    private DateTimeOffset? _previousTime;
    
    public double? TotalCpu { get; private set; }
    public double IntervalSeconds { get; private set; }
    public string Status { get; private set; } = "CPU counters warming up";

    private static void Require(uint result, string operation)
    {
        if (result != 0) throw new InvalidOperationException($"{operation}: 0x{result:X8}");
    }

    private void Open()
    {
        if (_query != IntPtr.Zero) return;
        try
        {
            Require(PdhOpenQuery(null, IntPtr.Zero, out _query), "Open process counter query");
            Require(PdhAddEnglishCounter(_query, @"\Process(*)\% Processor Time", IntPtr.Zero, out _cpu), "Add CPU counter");
            Require(PdhAddEnglishCounter(_query, @"\Process(*)\ID Process", IntPtr.Zero, out _pid), "Add PID counter");
            Require(PdhAddEnglishCounter(_query, @"\Process(*)\Elapsed Time", IntPtr.Zero, out _birth), "Add process identity counter");
            // Optional public processor counters expose deferred and interrupt work without driver tracing.
            if (PdhAddEnglishCounter(_query, @"\Processor(*)\% DPC Time", IntPtr.Zero, out _dpc) != 0) _dpc = IntPtr.Zero;
            if (PdhAddEnglishCounter(_query, @"\Processor(*)\% Interrupt Time", IntPtr.Zero, out _interrupt) != 0) _interrupt = IntPtr.Zero;
        }
        catch { Close(); throw; }
    }

    private static Dictionary<string, RawCounter> ReadArray(IntPtr counter)
    {
        uint bytes = 0;
        var result = PdhGetRawCounterArray(counter, ref bytes, out _, IntPtr.Zero);
        if (result != 0 && result != 0x800007D2) Require(result, "Size process counter array");
        if (bytes == 0 || bytes > 16 * 1024 * 1024) throw new InvalidOperationException("Invalid process counter buffer size");
        // Copy counter values before freeing the native buffer, including instance names.
        var buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            Require(PdhGetRawCounterArray(counter, ref bytes, out var count, buffer), "Read process counter array");
            var size = Marshal.SizeOf<RawItem>();
            if (count > bytes / size) throw new InvalidOperationException("Malformed process counter array");
            var values = new Dictionary<string, RawCounter>(StringComparer.OrdinalIgnoreCase);
            var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<RawItem>(buffer + i * size);
                var name = Marshal.PtrToStringUni(item.Name);
                if (name != null)
                {
                    // Legacy Process arrays repeat names. Preserve every instance in this
                    // single query; joining by name alone silently drops browser/service PIDs.
                    var occurrence = occurrences.GetValueOrDefault(name);
                    occurrences[name] = occurrence + 1;
                    if (item.Value.Status <= 1) values[name + "\u001f" + occurrence] = item.Value;
                }
            }
            return values;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static double? ReadProcessorTime(IntPtr counter, ref long? previous, ulong denominator)
    {
        if (counter == IntPtr.Zero) return null;
        try
        {
            var rows = ReadArray(counter).Where(p => p.Key.Split('\u001f')[0] != "_Total").Select(p => p.Value.First).ToArray();
            if (rows.Length != Environment.ProcessorCount || rows.Any(v => v < 0)) { previous = null; return null; }
            var current = rows.Sum();
            // Per-processor raw timer values are 100 ns; sum cores and use the same system denominator.
            var rate = previous.HasValue && current >= previous.Value && denominator > 0 ? 100d * (current - previous.Value) / denominator : (double?)null;
            previous = current;
            return rate is >= 0 and <= 100 ? rate : null;
        }
        catch { previous = null; return null; }
    }

    internal static double? Rate(long cpu, long previous, long birth, long previousBirth, ulong denominator) =>
        birth > 0 && birth == previousBirth && cpu >= previous && denominator > 0 ? Math.Clamp(100d * (cpu - previous) / denominator, 0, 100) : null;

    public Dictionary<int, CounterProcess> Read()
    {
        var result = new Dictionary<int, CounterProcess>();
        TotalCpu = null;
        IntervalSeconds = 0;
        try
        {
            Open();
            Require(PdhCollectQueryData(_query), "Collect CPU counters");
            // Pair total system times with this same native collection, not the faster UI poll.
            if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) throw new InvalidOperationException("System CPU counter unavailable");
            var now = DateTimeOffset.UtcNow;
            var system = kernel + user;
            var denominator = _systemPrevious.HasValue && system > _systemPrevious.Value ? system - _systemPrevious.Value : 0;
            if (denominator > 0 && _idlePrevious.HasValue && idle >= _idlePrevious.Value && idle - _idlePrevious.Value <= denominator)
                TotalCpu = 100d * (denominator - (idle - _idlePrevious.Value)) / denominator;
            IntervalSeconds = _previousTime.HasValue ? (now - _previousTime.Value).TotalSeconds : 0;
            DpcCpu = ReadProcessorTime(_dpc, ref _previousDpc, denominator);
            InterruptCpu = ReadProcessorTime(_interrupt, ref _previousInterrupt, denominator);
            var cpu = ReadArray(_cpu);
            var pids = ReadArray(_pid);
            var births = ReadArray(_birth);

            var current = new Dictionary<int, (long Cpu, long Birth)>();
            foreach (var pair in cpu)
            {
                if (!pids.TryGetValue(pair.Key, out var id) || id.First <= 0 || id.First > int.MaxValue || !births.TryGetValue(pair.Key, out var birth)) continue;
                var pid = (int)id.First;
                var prior = _previous.GetValueOrDefault(pid);
                var rate = Rate(pair.Value.First, prior.Cpu, birth.First, prior.Birth, denominator);
                current[pid] = (pair.Value.First, birth.First);
                // Counter suffixes identify duplicate executable names, not separate apps.
                var name = System.Text.RegularExpressions.Regex.Replace(pair.Key.Split('\u001f')[0], @"#\d+$", "");
                DateTimeOffset? started = birth.First >= new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).ToFileTime() && birth.First <= now.ToFileTime() ? DateTimeOffset.FromFileTime(birth.First) : null;
                result[pid] = new(rate, null, null) { Name = name, Started = started };
            }
            _previous.Clear();
            foreach (var row in current) _previous[row.Key] = row.Value;
            _systemPrevious = system;
            _idlePrevious = idle;
            _previousTime = now;
            Status = TotalCpu.HasValue ? $"All-process CPU counters; same {IntervalSeconds:F1}s interval, including protected processes" : "All-process CPU counters warming up";
        }
        catch (Exception e)
        {
            Reset();
            Status = "Native process CPU counters unavailable: " + e.Message;
        }
        return result;
    }

    public void Reset()
    {
        _previous.Clear();
        _systemPrevious = _idlePrevious = null;
        _previousTime = null;
        _previousDpc = _previousInterrupt = null;
        DpcCpu = InterruptCpu = null;
        TotalCpu = null;
        IntervalSeconds = 0;
    }

    private void Close()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    public void Dispose() { Reset(); Close(); }
}

public static class CpuCoverage
{
    public const string UnattributedName = "System / unattributed CPU";
    public static CpuResidualPart[] Breakdown(double? remaining, double? dpc, double? interrupts)
    {
        if (!remaining.HasValue) return [];
        if ((dpc ?? 0) + (interrupts ?? 0) > remaining.Value + 0.001)
            return [new("Unresolved system CPU", remaining, $"Interrupt/DPC counters overlap or exceed this residual; an independent split is unavailable. DPC: {dpc:F2}%, interrupts: {interrupts:F2}%. No driver/process is guessed.")];
        return
        [
            new("Deferred procedure calls (DPC)", dpc, "CPU measured by Windows' deferred-work counters. Driver names require a separate trace; they cannot be inferred from this number."),
            new("Hardware interrupts", interrupts, "CPU measured by Windows' interrupt counters. The responsible device/driver is not identified by these counters."),
            new("Other / unresolved system CPU", Math.Max(0, remaining.Value - (dpc ?? 0) - (interrupts ?? 0)), "Remaining CPU without an independent process assignment: kernel work, exited/missing processes, or unavailable interrupt/DPC data. This is a measured residual, not an invented process.")
        ];
    }
    public static ProcessSnapshot Apply(ProcessSnapshot snapshot, double? total, double seconds, double? dpc = null, double? interrupts = null)
    {
        var apps = snapshot.Apps.Where(a => a.Name != UnattributedName).ToList();
        var measured = apps.Where(a => a.CpuPercent.HasValue).Sum(a => a.CpuPercent!.Value);
        double? remaining = total.HasValue ? Math.Max(0, total.Value - measured) : null;
        if (remaining is > 0.001)
        {
            apps.Add(new(UnattributedName, null, [], remaining, null, null, null, "Not a GPU measurement", ActivityRules.Impact(remaining, null, null),
                "CPU included in the system total but not assigned to a surviving process counter. Includes interrupts/DPCs, exited processes and unavailable counters. This is a system aggregate, not an executable.", [])
                { EnergySource = "System CPU residual; no individual-process energy measurement" });
        }
        return snapshot with
        {
            Apps = apps.OrderByDescending(a => a.Rank).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            TotalCpuPercent = total,
            AccountedCpuPercent = snapshot.Apps.Any(a => a.Name != UnattributedName && a.CpuPercent.HasValue) ? measured : null,
            UnattributedCpuPercent = remaining,
            CpuIntervalSeconds = seconds,
            SystemCpuParts = Breakdown(remaining, dpc, interrupts),
            CpuCoverageStatus = total.HasValue ? $"CPU {total:F1}% = processes {measured:F1}% + system/unattributed {remaining:F1}% ({seconds:F1}s)" + (measured > total + 0.1 ? "; counter timing discrepancy: process sum exceeds system total" : "") : "CPU counters warming up / unavailable"
        };
    }
}