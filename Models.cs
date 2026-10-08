// ============================================================================
// File: Models.cs
// Author: Scott Sheen (with ChatGpt)
// Created: 2026-10-02
// Updated: 2026-10-03
// Purpose: Shared measurement contracts, validated preferences, and application files.
// ============================================================================

using System.Text.Json;

namespace PowerMonitor;

public record AlertRule(string Name, double Watts, int Seconds, double ClearWatts);
public sealed class MonitorSettings
{

    // Lower wakeup frequency and avoid collectors that can keep providers or GPUs busy.
    public bool LowPowerMode { get; set; } = true;

    public bool EnableRestrictedProcessCounters
    {
        get; set;
    }

    public int EffectiveSampleSeconds => LowPowerMode ? Math.Max(10, SampleSeconds) : SampleSeconds;

    public int EffectiveProcessSeconds => EffectiveSampleSeconds;

    public int EffectiveHistorySeconds => LowPowerMode ? Math.Max(60, HistorySeconds) : HistorySeconds;

    public bool EffectiveGpuActivity => EnableGpuActivity && !LowPowerMode;

    public int SampleSeconds { get; set; } = 5;

    public int ProcessSeconds { get; set; } = 10;

    public bool EnableProcessMonitoring { get; set; } = true;

    public bool EnableGpuActivity { get; set; } = true;

    public int HistorySeconds { get; set; } = 20;

    public int RetentionDays { get; set; } = 90;

    public bool NotificationsEnabled { get; set; } = true;

    public bool BatteryOnlyAlerts { get; set; } = true;

    public int UnplugGraceSeconds { get; set; } = 30;

    public int RepeatSeconds { get; set; } = 600;

    public int ClearSeconds { get; set; } = 30;

    public bool CloseToTray { get; set; } = true;

    public bool StartMinimized
    {
        get; set;
    }

    public bool StartMonitoring { get; set; } = true;

    public bool EnableHardwareEnergy { get; set; }

    public bool EnableCpuSensors
    {
        get; set;
    }

    public string[] ExcludedApplications { get; set; } = [];

    public AlertRule[] Rules { get; set; } = [new("Warning", 20, 30, 17), new("High", 30, 30, 25), new("Critical", 45, 15, 40)];

    public void Validate()
    {
        if (ProcessSeconds < Math.Max(5, SampleSeconds) || ProcessSeconds > 300)
            throw new InvalidDataException("App activity interval must be at least 5 seconds and the live sample interval, and at most 300 seconds.");
        if (SampleSeconds is < 2 or > 60 || HistorySeconds < SampleSeconds || HistorySeconds > 300)
            throw new InvalidDataException("Live interval must be 2–60 seconds; history must be at least the live interval and at most 300 seconds.");
        if (RetentionDays is < 1 or > 3650 || RepeatSeconds < 60 || UnplugGraceSeconds < 0 || ClearSeconds < 1)
            throw new InvalidDataException("Invalid retention or alert timing.");
        if (Rules.Length != 3 || Rules.Any(r => r.Watts <= 0 || r.Seconds < 1 || r.ClearWatts < 0 || r.ClearWatts >= r.Watts) || Rules.Zip(Rules.Skip(1)).Any(p => p.First.Watts >= p.Second.Watts))
            throw new InvalidDataException("Use three increasing positive thresholds, each with a lower clear threshold and positive duration.");
    }
}

public static class AppFiles
{

    public static string Root { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaptopPowerMonitor");

    public static void UseRoot(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public static string Config => Path.Combine(Root, "settings.json");

    public static JsonSerializerOptions Json
    {
        get;
    } = new()
    {
        WriteIndented = true
    };

    public static MonitorSettings Load()
    {
        // Load stored preferences; new options retain their safe defaults for older settings files.
        Directory.CreateDirectory(Root);
        if (!File.Exists(Config))
        {
            var d = new MonitorSettings();
            Save(d);
            return d;
        }

        var settings = JsonSerializer.Deserialize<MonitorSettings>(File.ReadAllText(Config), Json) ?? throw new InvalidDataException("Empty configuration.");
        settings.Validate();
        return settings;
    }

    public static void Save(MonitorSettings settings)
    {
        settings.Validate();
        var temp = Config + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, Config, true);
    }

    private static readonly object Gate = new();

    public static void Log(string message)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, "monitor.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                File.Move(path, path + ".previous", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
    }
}

public record Sample(DateTimeOffset Time, bool? Online, double? Percent, double? Watts, double? ChargeWatts, double? RemainingWh, double? Voltage, double? CpuPercent, double? CpuWatts, string NvidiaState, double? NvidiaWatts, int? RefreshHz, int ExternalDisplays, string DisplaySummary, string PowerPlan, bool? BatterySaver, string XmgMode, double SelfCpuPercent, double MemoryMb, long PollCount, double CollectionMs, string Status)
{

    public PowerAccounting? PowerAccounting
    {
        get; init;
    }

    public bool? Charging
    {
        get; init;
    }

    public bool? Discharging
    {
        get; init;
    }

    public double? DischargeMilliAmps => BatteryMetrics.ToMilliAmps(Watts, Voltage);

    public double? ChargeMilliAmps => BatteryMetrics.ToMilliAmps(ChargeWatts, Voltage);

    public double? RemainingMilliAmpHours => BatteryMetrics.ToMilliAmps(RemainingWh, Voltage);

    public double? UsedMilliAmpHours
    {
        get; init;
    }

    public double? AddedMilliAmpHours
    {
        get; init;
    }

    public double? UsedWattHours { get; init; }
    public double? AddedWattHours { get; init; }
    public double? AmdWatts { get; init; }
    public string HardwareStatus { get; init; } = "Hardware probes disabled";

    public double BatteryObservedSeconds
    {
        get; init;
    }

    public double? GpuPercent
    {
        get; init;
    }

    public DateTimeOffset? ActivityTime
    {
        get; init;
    }

    public string? ActivityStatus
    {
        get; init;
    }

    public Contributor[]? TopProcesses
    {
        get; init;
    }
}

public record Contributor(string Name, double? CpuPercent, double? GpuPercent, double? IoMiBPerSecond, string Impact, string Adapter, int ProcessCount)
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
public record BatteryInfo(double? DesignWh, double? FullWh, double? Cycles)
{

    public double? HealthPercent => DesignWh > 0 && FullWh.HasValue ? Math.Clamp(100 * FullWh.Value / DesignWh.Value, 0, 100) : null;

    public double? WearPercent => DesignWh > 0 && FullWh.HasValue ? Math.Max(0, 100 * (1 - FullWh.Value / DesignWh.Value)) : null;
}

public record Detection(string Windows, string Model, string[] Adapters, BatteryInfo Battery, bool XmgPresent, string XmgDetails, string[] Sensors, string[] Issues);
public record AlertEvent(DateTimeOffset Time, string Severity, double Watts, string Details);
public interface IPowerAction
{
    string Description
    {
        get;
    }

    string CurrentState
    {
        get;
    }

    string Consequence
    {
        get;
    }

    bool RequiresAdministrator
    {
        get;
    }

    bool RequiresConfirmation
    {
        get;
    }

    Task<ActionResult> ExecuteAsync(CancellationToken cancellationToken);
}

public record ActionResult(bool Success, string Message);
public record ActionMeasurement(DateTimeOffset Time, string Action, double? BeforeWatts, double? AfterWatts, string Result);
