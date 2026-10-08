// ============================================================================
// File: MonitorService.cs
// Author: Scott Sheen (with ChatGpt)
// Created: 2026-10-02
// Updated: 2026-10-03
// Purpose: Coordinate sampling, recording, alerts, and shutdown on one worker.
// ============================================================================

using System.Diagnostics;

namespace PowerMonitor;

public sealed class MonitorService : IDisposable
{

    private readonly CancellationTokenSource _stop = new();

    private readonly SemaphoreSlim _settingsGate = new(1);

    private Task? _worker;

    private readonly Telemetry _telemetry = new();

    private readonly ProcessMonitor _processes = new();

    private readonly AlertEngine _alerts = new();

    private readonly BatteryUsageTracker _batteryUsage = new();

    private readonly PowerAttributor _attributor = new();
    private readonly WindowsEnergyReader _windowsEnergy = new();
    private HardwareEnergy? _hardware;
    private readonly EnergyCalibration _calibration = new(Path.Combine(AppFiles.Root, "energy-calibration.json"));
    private readonly ComponentAttributor _components = new();
    public string EnergyStatus => _calibration.Status;
    public void ToggleHardwareEnergy()
    {
        var updated = System.Text.Json.JsonSerializer.Deserialize<MonitorSettings>(System.Text.Json.JsonSerializer.Serialize(Settings))!;
        updated.EnableHardwareEnergy = !updated.EnableHardwareEnergy;
        AppFiles.Save(updated);
        UpdateSettings(updated);
    }
    public void StartWindowsEnergy() => _windowsEnergy.Start();
    public string WindowsEnergyStatus => _windowsEnergy.Status;

    public MonitorSettings Settings
    {
        get; private set;
    }

    public Detection? Detection
    {
        get; private set;
    }

    public bool Monitoring
    {
        get; set;
    }

    public event Action<Sample>? Sampled;

    public event Action<Detection>? Detected;

    public event Action<AlertEvent>? Alerted;

    public event Action<ProcessSnapshot>? ActivitySampled;

    public long Writes
    {
        get; private set;
    }

    public long Transactions
    {
        get; private set;
    }

    public MonitorService(MonitorSettings settings)
    {
        Settings = settings;
        Monitoring = settings.StartMonitoring;
    }

    public void PauseAlerts(int minutes) => _alerts.SuppressedUntil = DateTimeOffset.UtcNow.AddMinutes(minutes);

    public void UpdateSettings(MonitorSettings settings)
    {
        // Validate before swapping preferences; release the gate even if the update fails.
        settings.Validate();
        _settingsGate.Wait();
        try
        {
            Settings = settings;
            _alerts.Reset();
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    public void Start() => _worker = Task.Run(Run);

    private async Task Run()
    {
        // One worker owns sampling and persistence. The settings gate keeps each sample internally consistent.
        try
        {
            Detection = _telemetry.Detect(Settings.EnableCpuSensors && !Settings.LowPowerMode);
            Detected?.Invoke(Detection);
            foreach (var issue in Detection.Issues)
                AppFiles.Log(issue);

            // The worker owns the store; disposal flushes its final partial batch.
            using var history = new HistoryStore(Path.Combine(AppFiles.Root, "history.sqlite"), Settings.RetentionDays);
            var lastHistory = DateTimeOffset.MinValue;
            var lastActivity = DateTimeOffset.MinValue;
            string previousStatus = "";
            ProcessSnapshot? activity = null;
            bool? lastGpuMode = null;
            bool wasMonitoring = false;
            while (!_stop.IsCancellationRequested)
            {
                var interval = Settings.EffectiveSampleSeconds;
                await _settingsGate.WaitAsync(_stop.Token);
                try
                {
                    if (Monitoring)
                    {
                        if (!wasMonitoring)
                        {
                            _processes.ResetBaseline();
                            _batteryUsage.BreakInterval();
                            _attributor.BreakInterval();
                            _components.BreakInterval();
                            _calibration.BreakInterval();
                            lastActivity = DateTimeOffset.MinValue;
                        }

                        wasMonitoring = true;
                        var watch = Stopwatch.StartNew();
                        var sample = _batteryUsage.Observe(_telemetry.Collect(!Settings.LowPowerMode || Settings.EnableHardwareEnergy), interval);
                        if (Settings.EnableHardwareEnergy)
                        {
                            if (_hardware == null)
                            {
                                _hardware = new HardwareEnergy(true);
                                Detection = _telemetry.Detect(true);
                                Detected?.Invoke(Detection);
                            }
                            var reading = _hardware.Read();
                            sample = sample with { CpuWatts = reading.CpuWatts ?? sample.CpuWatts, NvidiaWatts = reading.NvidiaWatts, AmdWatts = reading.AmdWatts, NvidiaState = reading.NvidiaWatts.HasValue ? "Measured NVML board power" : "Unavailable", HardwareStatus = reading.Status };
                        }
                        else if (_hardware != null)
                        {
                            _hardware.Dispose();
                            _hardware = null;
                            // Close optional CPU probes too when ending the hardware diagnostic.
                            Detection = _telemetry.Detect(Settings.EnableCpuSensors && !Settings.LowPowerMode);
                            _components.BreakInterval();
                            _calibration.BreakInterval();
                        }

                        // CPU totals and every process row now use one shared live-sample interval.
                        if (Settings.EnableProcessMonitoring)
                        {
                            if ((sample.Time - lastActivity).TotalSeconds >= Settings.EffectiveProcessSeconds || lastGpuMode != (Settings.EffectiveGpuActivity || Settings.EnableHardwareEnergy))
                            {
                                activity = _processes.Collect(Settings.EffectiveGpuActivity || Settings.EnableHardwareEnergy, Settings.EnableRestrictedProcessCounters && !Settings.LowPowerMode);
                                lastActivity = sample.Time;
                                lastGpuMode = Settings.EffectiveGpuActivity || Settings.EnableHardwareEnergy;

                            }
                        }
                        else if (activity != null)
                        {
                            activity = null;
                            lastActivity = DateTimeOffset.MinValue;
                            lastGpuMode = null;
                            _processes.Dispose();
                            _processes.ResetBaseline();
                            ActivitySampled?.Invoke(new(sample.Time, [], [], "App activity monitoring disabled", 0, 0, []));
                        }

                        // Reuse cached activity: attribution adds no hardware queries or wakeups.
                        if (activity != null)
                        {
                            sample = sample with { CpuPercent = activity.TotalCpuPercent };
                            activity = _attributor.Allocate(sample, activity, interval, Settings.EffectiveProcessSeconds);
                            activity = _windowsEnergy.Apply(sample, activity);
                            _calibration.Observe(sample, activity, interval, Settings.EffectiveProcessSeconds);
                            activity = _components.Apply(sample, activity, _calibration, interval, Settings.EffectiveProcessSeconds);
                            // Preserve independently reported E3 interrupt energy on the residual CPU row.
                            activity = activity with { Apps = activity.Apps.Select(a => a.Name == CpuCoverage.UnattributedName && !a.EnergySource.StartsWith("Windows E3", StringComparison.Ordinal) ? a with { EstimatedWatts = null, EstimatedMilliAmpHours = null, EstimatedWattHours = null, EnergySource = "System CPU aggregate; individual energy unavailable" } : a).ToArray() };
                            ActivitySampled?.Invoke(activity);
                        }
                        else
                        {
                            _attributor.BreakInterval();
                            _components.BreakInterval();
                            _calibration.BreakInterval();
                        }

                        sample = sample with
                        {
                            PowerAccounting = activity?.Accounting,
                            GpuPercent = activity?.GpuPercent,
                            ActivityTime = activity?.Time,
                            ActivityStatus = activity?.Status,
                            TopProcesses = activity?.Apps.Take(6).Select(a => new Contributor(a.Name, a.CpuPercent, a.GpuPercent, a.IoMiBPerSecond, a.Impact, a.Gpu, a.Pids.Length) { EstimatedWatts = a.EstimatedWatts, EstimatedMilliAmpHours = a.EstimatedMilliAmpHours }).ToArray(),
                            CollectionMs = watch.Elapsed.TotalMilliseconds
                        };
                        // Battery totals were integrated before app accounting.
                        Sampled?.Invoke(sample);

                        // Log status transitions rather than writing the same diagnostic every poll.
                        if (sample.Status != previousStatus)
                        {
                            previousStatus = sample.Status;
                            if (sample.Status.Length > 0)
                                AppFiles.Log(sample.Status);
                        }

                        // History has its own recording interval to control disk traffic and database growth.
                        if ((sample.Time - lastHistory).TotalSeconds >= Settings.EffectiveHistorySeconds)
                        {
                            history.Add(sample);
                            lastHistory = sample.Time;
                            Writes = history.SamplesWritten;
                            Transactions = history.Transactions;
                        }

                        var alert = _alerts.Evaluate(sample, AlertSettings(), IsExcluded(Settings.ExcludedApplications));
                        if (alert != null)
                        {
                            history.AddAlert(alert);
                            Alerted?.Invoke(alert);
                        }
                    }
                    else
                    {
                        _alerts.Reset();
                        if (wasMonitoring)
                        {
                            _processes.ResetBaseline();
                            _batteryUsage.BreakInterval();
                            _attributor.BreakInterval();
                            _components.BreakInterval();
                            _calibration.BreakInterval();
                        }
                        wasMonitoring = false;
                    }
                }
                catch (Exception e)
                {
                    AppFiles.Log("Collection: " + e);
                }
                finally
                {
                    _settingsGate.Release();
                }

                await Task.Delay(TimeSpan.FromSeconds(interval), _stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            AppFiles.Log("Monitor stopped: " + e);
        }
        finally
        {
            _processes.Dispose();
            _hardware?.Dispose();
            _telemetry.Dispose();
            _windowsEnergy.Dispose();
        }
    }

    private MonitorSettings AlertSettings()
    {
        return new MonitorSettings
        {
            SampleSeconds = Settings.EffectiveSampleSeconds,
            Rules = Settings.Rules,
            BatteryOnlyAlerts = Settings.BatteryOnlyAlerts,
            UnplugGraceSeconds = Settings.UnplugGraceSeconds,
            ClearSeconds = Settings.ClearSeconds,
            RepeatSeconds = Settings.RepeatSeconds,
            NotificationsEnabled = Settings.NotificationsEnabled
        };
    }

    private static bool IsExcluded(string[] names)
    {
        foreach (var name in names)
        {
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name));
            try
            {
                if (processes.Length > 0)
                    return true;
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }

        return false;
    }

    public void Dispose()
    {
        // Release owned native handles and flush pending work when the component shuts down.
        _stop.Cancel();
        _worker?.GetAwaiter().GetResult();
        _stop.Dispose();
        _settingsGate.Dispose();
    }
}
