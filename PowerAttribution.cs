// ============================================================================
// File: PowerAttribution.cs
// Author: Scott Sheen (with ChatGpt)
// Created: 2026-10-02
// Updated: 2026-10-03
// Purpose: Explicit heuristic allocation of measured battery power and charge.
// ============================================================================

namespace PowerMonitor;

public record PowerAccounting(double? MeasuredWatts, double? AppWatts, double? SystemWatts,
    double? ObservedMilliAmpHours, double? AppMilliAmpHours, double? SystemMilliAmpHours,
    double CoveredSeconds, string Status);

public sealed class PowerAttributor
{
    private readonly Dictionary<string, double> _appCharge = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _processCharge = new();
    private Dictionary<string, double> _previousApps = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, double> _previousProcesses = new();
    private DateTimeOffset? _previousTime;
    private double _previousTotalCurrent, _previousAppCurrent;
    private double _totalCharge, _allocatedCharge, _seconds;

    public void BreakInterval() => _previousTime = null;

    private static string AppKey(AppActivity app) => app.Path ?? app.Name;

    // A creation timestamp prevents a reused PID inheriting another process's charge.
    private static string ProcessKey(ProcessActivity process) => $"{process.Pid}:{process.Started?.UtcTicks}";

    private static double Weight(double? cpu, double? gpu, double? io) =>
        Math.Max(0, cpu ?? 0) + Math.Max(0, gpu ?? 0) * 0.5 + Math.Min(10, Math.Max(0, io ?? 0));

    public ProcessSnapshot Allocate(Sample sample, ProcessSnapshot snapshot, int sampleSeconds, int processSeconds)
    {
        var age = (sample.Time - snapshot.Time).TotalSeconds;
        bool valid = sample.Online == false && sample.Watts.HasValue && double.IsFinite(sample.Watts.Value) &&
            sample.Watts >= 0 && age >= -5 && age <= processSeconds * 2.5;
        double? total = valid ? sample.Watts : null;
        double sum = snapshot.Apps.Where(a => a.Processes.Length > 0).Sum(a => Weight(a.CpuPercent, a.GpuPercent, a.IoMiBPerSecond));

        // Deliberately conservative and uncalibrated: CPU + 0.5*GPU + capped I/O
        // determine activity weights. At most 70% of total is attributed to apps;
        // at least 30% stays system/unattributed. This is not sensor-measured app power.
        double? budget = total * (0.7 * Math.Clamp(sum / 100, 0, 1));
        var appWatts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var pidWatts = new Dictionary<string, double>();
        if (budget.HasValue)
        {
            foreach (var app in snapshot.Apps)
            {
                if (app.Processes.Length == 0) continue;
                double watts = sum > 0 ? budget.Value * Weight(app.CpuPercent, app.GpuPercent, app.IoMiBPerSecond) / sum : 0;
                appWatts[AppKey(app)] = watts;
                double processSum = app.Processes.Sum(p => Weight(p.CpuPercent, ActivityRules.BusiestEngine(p.Engines), p.IoMiBPerSecond));
                foreach (var process in app.Processes)
                    pidWatts[ProcessKey(process)] = processSum > 0 ? watts * Weight(process.CpuPercent, ActivityRules.BusiestEngine(process.Engines), process.IoMiBPerSecond) / processSum : 0;
            }
        }

        var totalCurrent = BatteryMetrics.ToMilliAmps(total, sample.Voltage);
        var allocatedCurrent = BatteryMetrics.ToMilliAmps(budget, sample.Voltage);
        var currentApps = appWatts.ToDictionary(p => p.Key, p => BatteryMetrics.ToMilliAmps(p.Value, sample.Voltage) ?? 0, StringComparer.OrdinalIgnoreCase);
        var knownIdentities = snapshot.Apps.SelectMany(a => a.Processes).Where(p => p.Started.HasValue).Select(ProcessKey).ToHashSet();
        var currentProcesses = pidWatts.Where(p => knownIdentities.Contains(p.Key)).ToDictionary(p => p.Key, p => BatteryMetrics.ToMilliAmps(p.Value, sample.Voltage) ?? 0);
        if (totalCurrent.HasValue && allocatedCurrent.HasValue)
        {
            double elapsed = _previousTime.HasValue ? (sample.Time - _previousTime.Value).TotalSeconds : 0;
            if (elapsed > 0 && elapsed <= sampleSeconds * 2.5)
            {
                // Trapezoidal integration uses each endpoint's voltage. Missing samples,
                // pauses, AC, and stale activity break continuity instead of filling gaps.
                _totalCharge += (_previousTotalCurrent + totalCurrent.Value) / 2 * elapsed / 3600;
                _allocatedCharge += (_previousAppCurrent + allocatedCurrent.Value) / 2 * elapsed / 3600;
                Integrate(_appCharge, _previousApps, currentApps, elapsed);
                Integrate(_processCharge, _previousProcesses, currentProcesses, elapsed);
                _seconds += elapsed;
            }
            _previousTime = sample.Time;
            _previousTotalCurrent = totalCurrent.Value;
            _previousAppCurrent = allocatedCurrent.Value;
            _previousApps = currentApps;
            _previousProcesses = currentProcesses;
        }
        else
            BreakInterval();

        // Keep app session totals, but discard retired PID details to bound memory.
        foreach (var key in _processCharge.Keys.Where(key => !knownIdentities.Contains(key)).ToArray())
            _processCharge.Remove(key);

        var apps = snapshot.Apps.Select(app => app with
        {
            EnergySource = "Legacy activity model (uncalibrated); Windows E3 unavailable for this interval",
            EstimatedWattHours = null,
            EstimatedWatts = app.Processes.Length > 0 && total.HasValue && (app.CpuPercent.HasValue || app.GpuPercent.HasValue || app.IoMiBPerSecond.HasValue) ? appWatts.GetValueOrDefault(AppKey(app)) : null,
            EstimatedMilliAmpHours = _appCharge.TryGetValue(AppKey(app), out var used) ? used : null,
            Processes = app.Processes.Select(p => p with
            {
                EstimatedWatts = total.HasValue && (p.CpuPercent.HasValue || p.IoMiBPerSecond.HasValue || p.Engines.Length > 0) ? pidWatts.GetValueOrDefault(ProcessKey(p)) : null,
                EstimatedMilliAmpHours = _processCharge.TryGetValue(ProcessKey(p), out var charge) ? charge : null
            }).ToArray()
        }).ToArray();

        return snapshot with
        {
            Apps = apps,
            Accounting = new(total, budget, total - budget, _seconds > 0 ? _totalCharge : null,
                _seconds > 0 ? _allocatedCharge : null, _seconds > 0 ? _totalCharge - _allocatedCharge : null, _seconds,
                valid ? "Uncalibrated activity estimate; at least 30% reserved as system/unattributed. Unknown counters are not proof of zero usage."
                      : sample.Online == true ? "On AC: measured laptop watts unavailable; charging power cannot be allocated to apps."
                      : "Battery watts or fresh app activity unavailable; estimates withheld.")
        };
    }

    private static void Integrate(Dictionary<string, double> totals, Dictionary<string, double> before,
        Dictionary<string, double> after, double seconds)
    {
        // Exited apps retain their session total; newly observed apps start at zero.
        foreach (var key in before.Keys.Union(after.Keys, totals.Comparer))
            totals[key] = totals.GetValueOrDefault(key) + (before.GetValueOrDefault(key) + after.GetValueOrDefault(key)) / 2 * seconds / 3600;
    }
}
