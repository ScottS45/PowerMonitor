// Purpose: Fit a nonnegative component model to battery draw and allocate dynamic energy by PID activity.
// Author: Scott Sheen (with ChatGpt). Created/Updated: 2026-10-03.
using System.Text.Json;

namespace PowerMonitor;

public record CalibrationPoint(double Cpu, double Gpu, double Battery, double Seconds);
public record CalibrationProfile(string Signature, double Baseline, double Cpu, double Gpu, double ValidationMae, int Count, DateTimeOffset Updated);

public sealed class EnergyCalibration
{
    private static readonly string DriverIdentity = string.Join("/", new[] { "nvml.dll", "amdadlx64.dll" }.Select(name => { try { return System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(Environment.SystemDirectory, name)).FileVersion ?? "unknown"; } catch { return "absent"; } }));
    private List<CalibrationPoint> _points = [];
    private readonly Dictionary<string, List<CalibrationPoint>> _datasets = [];
    private readonly string? _path;
    private DateTimeOffset? _previous;
    private string _signature = "";
    public CalibrationProfile? Profile { get; private set; }
    public string Status { get; private set; } = "Calibration needs battery discharge and fresh app activity";
    public EnergyCalibration(string? path = null)
    {
        _path = path;
        try { if (path != null && File.Exists(path)) Profile = JsonSerializer.Deserialize<CalibrationProfile>(File.ReadAllText(path)); }
        catch (Exception e) { AppFiles.Log("Calibration load: " + e.Message); }
    }

    public static (double Cpu, double Gpu, string Signature)? Features(Sample sample, ProcessSnapshot snapshot)
    {
        if (!sample.CpuPercent.HasValue) return null;
        var cpu = sample.CpuWatts ?? sample.CpuPercent.Value;
        var gpu = sample.NvidiaWatts ?? snapshot.GpuPercent;
        // Missing GPU counters are a distinct CPU-only model, never a measured idle GPU.
        var signature = (sample.CpuWatts.HasValue ? "package-W" : "cpu-percent") + "/" + (sample.NvidiaWatts.HasValue ? "nvidia-W" : gpu.HasValue ? "gpu-percent" : "gpu-unknown") + "/" + Environment.MachineName + "/" + DriverIdentity;
        return (cpu / 100, (gpu ?? 0) / 100, signature);
    }

    public void BreakInterval() => _previous = null;
    public void Observe(Sample sample, ProcessSnapshot snapshot, int interval, int processInterval)
    {
        var features = Features(sample, snapshot);
        var seconds = _previous.HasValue ? (sample.Time - _previous.Value).TotalSeconds : 0;
        _previous = sample.Time;
        var age = (sample.Time - snapshot.Time).TotalSeconds;
        if (sample.Online != false || sample.Discharging != true || sample.Watts is not > 0 || features == null || age < -5 || age > processInterval * 1.5 || seconds <= 0 || seconds > interval * 2.5)
        {
            if (sample.Online != false) { BreakInterval(); Status = "Calibration waiting for battery discharge"; }
            return;
        }
        if (_signature != features.Value.Signature)
        {
            _signature = features.Value.Signature;
            if (!_datasets.TryGetValue(_signature, out var data)) _datasets[_signature] = data = [];
            _points = data;
        }
        _points.Add(new(features.Value.Cpu, features.Value.Gpu, sample.Watts.Value, seconds));
        if (_points.Count > 600) _points.RemoveAt(0);
        Status = $"Calibration: {_points.Count}/60+ samples, {_points.Sum(p => p.Seconds):F0}/300+ battery seconds";
        if (_points.Count % 12 != 0) return;
        var fitted = Fit(_points, _signature);
        if (fitted == null) { Status += "; needs more varied activity or lower prediction error"; return; }
        Profile = fitted;
        Status = $"Calibrated: held-out error {fitted.ValidationMae:F1} W ({fitted.Count} samples)";
        if (_path != null)
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(fitted, AppFiles.Json));
            File.Move(temp, _path, true);
        }
    }

    public static CalibrationProfile? Fit(IReadOnlyList<CalibrationPoint> points, string signature)
    {
        if (points.Count < 60 || points.Sum(p => p.Seconds) < 300 || points.Any(p => !double.IsFinite(p.Battery + p.Cpu + p.Gpu) || p.Battery <= 0 || p.Cpu < 0 || p.Gpu < 0)) return null;
        var train = points.Take(points.Count * 4 / 5).ToArray();
        var test = points.Skip(points.Count * 4 / 5).ToArray();
        if (train.Max(p => p.Cpu) - train.Min(p => p.Cpu) < 0.05) return null;
        var gpuVaries = train.Max(p => p.Gpu) - train.Min(p => p.Gpu) >= 0.05;
        if (gpuVaries)
        {
            var meanCpu = train.Average(p => p.Cpu);
            var meanGpu = train.Average(p => p.Gpu);
            var cpuVariance = train.Sum(p => Math.Pow(p.Cpu - meanCpu, 2));
            var gpuVariance = train.Sum(p => Math.Pow(p.Gpu - meanGpu, 2));
            var covariance = train.Sum(p => (p.Cpu - meanCpu) * (p.Gpu - meanGpu));
            // Nearly identical CPU/GPU load changes cannot identify separate coefficients.
            if (covariance * covariance > 0.98 * cpuVariance * gpuVariance) return null;
        }
        // Fit W = baseline + a*CPU + b*GPU with nonnegative projected gradient descent.
        // Features are divided by 100 to keep the steps stable for percent and watt inputs.
        double b = 5, c = 100, g = gpuVaries ? 100 : 0;
        var step = 0.4 / Math.Max(1, train.Max(p => 1 + p.Cpu * p.Cpu + p.Gpu * p.Gpu));
        for (int n = 0; n < 12000; n++)
        {
            double db = 0, dc = 0, dg = 0;
            foreach (var p in train)
            {
                var error = b + c * p.Cpu + g * p.Gpu - p.Battery;
                db += error; dc += error * p.Cpu; dg += error * p.Gpu;
            }
            b = Math.Max(0, b - step * db / train.Length);
            c = Math.Max(0, c - step * dc / train.Length);
            g = gpuVaries ? Math.Max(0, g - step * dg / train.Length) : 0;
        }
        var mae = test.Average(p => Math.Abs(b + c * p.Cpu + g * p.Gpu - p.Battery));
        if (mae > Math.Max(3, test.Average(p => p.Battery) * 0.2)) return null;
        return new(signature, b, c, g, mae, points.Count, DateTimeOffset.UtcNow);
    }
}

public sealed class ComponentAttributor
{
    private readonly Dictionary<string, (double Wh, double Mah)> _totals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (double Watts, double? Current)> _previous = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _time;
    private bool? _online;
    private string? _source;
    private double? _cpuIdle, _gpuIdle;
    public void BreakInterval() { _time = null; _previous.Clear(); }
    public ProcessSnapshot Apply(Sample sample, ProcessSnapshot snapshot, EnergyCalibration calibration, int interval, int processInterval)
    {
        var features = EnergyCalibration.Features(sample, snapshot);
        var profile = calibration.Profile;
        bool calibrated = features.HasValue && profile?.Signature == features.Value.Signature && (sample.Time - profile.Updated).TotalDays < 7;
        bool hardware = sample.CpuWatts.HasValue || sample.NvidiaWatts.HasValue;
        if ((!calibrated && !hardware) || (sample.Time - snapshot.Time).TotalSeconds < -5 || (sample.Time - snapshot.Time).TotalSeconds > processInterval * 1.5) { BreakInterval(); return snapshot; }
        if (sample.CpuPercent is < 2 && sample.CpuWatts.HasValue) _cpuIdle = Math.Min(_cpuIdle ?? double.MaxValue, sample.CpuWatts.Value);
        if (snapshot.GpuPercent is < 1 && sample.NvidiaWatts.HasValue) _gpuIdle = Math.Min(_gpuIdle ?? double.MaxValue, sample.NvidiaWatts.Value);
        var cpuBudget = calibrated ? profile!.Cpu * features!.Value.Cpu : sample.CpuWatts ?? 0;
        var gpuBudget = calibrated ? profile!.Gpu * features!.Value.Gpu : sample.NvidiaWatts ?? 0;
        // Preserve observed idle hardware energy as system power instead of charging it to apps.
        if (sample.CpuWatts.HasValue) cpuBudget *= Math.Clamp((sample.CpuWatts.Value - (_cpuIdle ?? sample.CpuWatts.Value)) / Math.Max(0.001, sample.CpuWatts.Value), 0, 1);
        if (sample.NvidiaWatts.HasValue) gpuBudget *= Math.Clamp((sample.NvidiaWatts.Value - (_gpuIdle ?? sample.NvidiaWatts.Value)) / Math.Max(0.001, sample.NvidiaWatts.Value), 0, 1);
        var processes = snapshot.Apps.SelectMany(a => a.Processes).ToArray();
        var cpuTotal = Math.Max(sample.CpuPercent ?? 0, processes.Sum(p => p.CpuPercent ?? 0));
        double GpuWeight(ProcessActivity p) => p.Engines.Where(e => e.Mapped && (!sample.NvidiaWatts.HasValue || e.Adapter.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))).Sum(e => e.Usage);
        var gpuTotal = processes.Sum(GpuWeight);
        var source = calibrated ? $"Battery-calibrated component model; held-out error {profile!.ValidationMae:F1} W" : "Hardware component allocation (uncalibrated; unknown idle baseline remains unattributed)";
        var seconds = _time.HasValue ? (sample.Time - _time.Value).TotalSeconds : 0;
        var contiguous = _online == sample.Online && _source == source && seconds > 0 && seconds <= interval * 2.5;
        var identifiable = calibrated || (sample.CpuWatts.HasValue && _cpuIdle.HasValue) || (sample.NvidiaWatts.HasValue && _gpuIdle.HasValue);
        if (!identifiable) { BreakInterval(); return snapshot; }
        // A GPU-only model cannot describe total app energy. Retain the upstream
        // battery/E3 estimate: adding GPU energy to those totals could count it twice.
        // Missing CPU energy must remain unknown, never become a zero CPU budget.
        var cpuIdentified = calibrated || (sample.CpuWatts.HasValue && _cpuIdle.HasValue);
        if (!cpuIdentified)
        {
            BreakInterval();
            const string partial = "GPU-only hardware reading; total app estimate retained. CPU energy requires battery calibration or Windows energy (admin).";
            return snapshot with
            {
                Apps = snapshot.Apps.Select(a => a with { EnergySource = a.EnergySource + "; " + partial }).ToArray(),
                Status = snapshot.Status + "; " + partial
            };
        }
        var liveKeys = processes.Select(p => $"PID:{p.Pid}:{p.Started?.UtcTicks}").Concat(snapshot.Apps.Select(a => "APP:" + (a.Path ?? a.Name))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var retired in _previous.Keys.Where(k => !liveKeys.Contains(k)).ToArray()) _previous.Remove(retired);
        foreach (var retired in _totals.Keys.Where(k => k.StartsWith("PID:") && !liveKeys.Contains(k)).ToArray()) _totals.Remove(retired);
        double totalApps = 0;
        var apps = snapshot.Apps.Select(a =>
        {
            var rows = a.Processes.Select(p =>
            {
                var watts = (cpuTotal > 0 ? cpuBudget * (p.CpuPercent ?? 0) / cpuTotal : 0) + (gpuTotal > 0 ? gpuBudget * GpuWeight(p) / gpuTotal : 0);
                totalApps += watts;
                var key = $"PID:{p.Pid}:{p.Started?.UtcTicks}";
                var charge = p.Started.HasValue ? Accumulate(key, watts) : ((double? Wh, double? Mah))(null, null);
                return p with { EstimatedWatts = p.CpuPercent.HasValue || p.Engines.Length > 0 ? watts : null, EstimatedMilliAmpHours = charge.Mah };
            }).ToArray();
            var power = rows.Sum(p => p.EstimatedWatts ?? 0);
            var total = Accumulate("APP:" + (a.Path ?? a.Name), power);
            return a with { Processes = rows, EstimatedWatts = rows.Any(p => p.EstimatedWatts.HasValue) ? power : null, EstimatedWattHours = total.Wh, EstimatedMilliAmpHours = total.Mah, EnergySource = source + "; CPU package includes APU energy; AMD GPU reading is not added twice. AC mAh is battery-equivalent. " + calibration.Status };
        }).ToArray();
        _time = sample.Time; _online = sample.Online; _source = source;
        // Only the same-time battery reading supports residual whole-system power; AC wall power stays unknown.
        var measured = sample.Online == false ? sample.Watts : null;
        return snapshot with { Apps = apps, Accounting = new(measured, totalApps, measured.HasValue && measured.Value >= totalApps ? measured.Value - totalApps : null, sample.UsedMilliAmpHours, null, null, 0, source + "; " + calibration.Status) };

        (double? Wh, double? Mah) Accumulate(string key, double watts)
        {
            var current = BatteryMetrics.ToMilliAmps(watts, sample.Voltage);
            if (contiguous && _previous.TryGetValue(key, out var prev))
            {
                var old = _totals.GetValueOrDefault(key);
                var wh = old.Wh + (prev.Watts + watts) / 2 * seconds / 3600;
                var mah = old.Mah + (prev.Current.HasValue && current.HasValue ? (prev.Current.Value + current.Value) / 2 * seconds / 3600 : 0);
                _totals[key] = (wh, mah);
            }
            _previous[key] = (watts, current);
            return _totals.TryGetValue(key, out var totals) ? (totals.Wh, current.HasValue ? totals.Mah : null) : (null, null);
        }
    }
}