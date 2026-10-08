// ============================================================================
// File: Telemetry.cs
// Author: Scott Sheen (with ChatGpt)
// Created: 2026-10-02
// Updated: 2026-10-02
// Purpose: Read-only system and battery measurements with explicit missing values.
// ============================================================================

using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace PowerMonitor;

public sealed class Telemetry : IDisposable
{

    public const string XmgPath = @"C:\Program Files\OEM\Control Center\AiStoneService\MyControlCenter\GPUPowerSavingSettings\GPUPowerSavingSettings.json";

    private Computer? _computer;

    private bool _absoluteBatteryUnits;

    private ulong _idle, _kernel, _user;

    private TimeSpan _selfCpu;

    private DateTimeOffset _previous = DateTimeOffset.UtcNow, _metadataAt = DateTimeOffset.MinValue;

    private readonly Process _self = Process.GetCurrentProcess();

    private long _polls;

    private string _plan = "Unknown", _display = "Unknown", _xmg = "Unavailable";

    private int? _hz;

    private int _external;

    private string[] _sensorNames = [];

    public Detection Detect(bool cpuSensors)
    {
        var issues = new List<string>();
        string model = "Unknown";
        string[] adapters = [];
        try
        {
            using var q = Query(@"root\cimv2", "SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            using var rows = q.Get();
            foreach (ManagementObject row in rows)
                using (row)
                {
                    model = $"{row["Manufacturer"]} {row["Model"]}";
                    break;
                }
        }
        catch (Exception e)
        {
            issues.Add("Model: " + e.Message);
        }

        try
        {
            using var q = Query(@"root\cimv2", "SELECT Name FROM Win32_VideoController");
            using var rows = q.Get();
            var names = new List<string>();
            foreach (ManagementObject row in rows)
                using (row)
                    names.Add(row["Name"]?.ToString() ?? "Unknown");
            adapters = names.ToArray();
        }
        catch (Exception e)
        {
            issues.Add("Adapters: " + e.Message);
        }

        _absoluteBatteryUnits = HasAbsoluteBatteryUnits(issues);
        var battery = new BatteryInfo(ReadCapacity("BatteryStaticData", "DesignedCapacity", issues), ReadCapacity("BatteryFullChargedCapacity", "FullChargedCapacity", issues), ReadCapacity("BatteryCycleCount", "CycleCount", issues, false));
        if (!_absoluteBatteryUnits)
            battery = battery with
            {
                DesignWh = null,
                FullWh = null
            };
        try
        {
            using var q = Query(@"root\wmi", "SELECT * FROM BatteryStatus");
            using var rows = q.Get();
            issues.Add($"BatteryStatus instances: {rows.Count}");
        }
        catch (Exception e)
        {
            issues.Add("BatteryStatus: " + e.Message);
        }

        _computer?.Close();
        _computer = null;
        _sensorNames = [];
        if (cpuSensors)
        {
            try
            {
                _computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = false,
                    IsBatteryEnabled = false
                };
                _computer.Open();
                var names = new List<string>();
                foreach (var hardware in _computer.Hardware)
                {
                    hardware.Update();
                    names.AddRange(hardware.Sensors.Select(s => $"{hardware.Name}: {s.Name} ({s.SensorType})"));
                }

                _sensorNames = names.ToArray();
                if (_sensorNames.Length == 0)
                    issues.Add("CPU sensors unavailable without driver access; monitoring continues without elevation.");
            }
            catch (Exception e)
            {
                issues.Add("CPU sensors: " + e.Message);
                _computer?.Close();
                _computer = null;
            }
        }
        else
            issues.Add("LibreHardwareMonitor CPU probe disabled by default; enable in Settings. GPU probes are always disabled.");
        RefreshMetadata(issues);
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var windows = $"{key?.GetValue("ProductName")} {key?.GetValue("DisplayVersion")} (build {key?.GetValue("CurrentBuildNumber")}.{key?.GetValue("UBR")})";
        return new(windows, model, adapters, battery, File.Exists(XmgPath), ReadXmgDetails(), _sensorNames, issues.ToArray());
    }

    private static bool HasAbsoluteBatteryUnits(List<string> issues)
    {
        try
        {
            using var query = Query(@"root\wmi", "SELECT Capabilities FROM BatteryStaticData");
            using var rows = query.Get();
            var count = 0;
            foreach (ManagementObject row in rows)
            {
                using (row)
                {
                    // BATTERY_CAPACITY_RELATIVE (0x40000000) means arbitrary units.
                    if (row["Capabilities"] == null || (Convert.ToUInt32(row["Capabilities"]) & 0x40000000) != 0)
                    {
                        issues.Add("Battery firmware does not report absolute energy units.");
                        return false;
                    }
                    count++;
                }
            }
            return count > 0;
        }
        catch (Exception error)
        {
            issues.Add("Battery units: " + error.Message);
            return false;
        }
    }

    private static ManagementObjectSearcher Query(string scope, string query) => new(new ManagementScope(scope), new ObjectQuery(query), new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(3), ReturnImmediately = true });

    public static double? ValidNumber(object? value)
    {
        // Battery drivers use sentinel values for unavailable data; reject them before converting units.
        if (value == null)
            return null;
        try
        {
            var number = Convert.ToDouble(value);
            return number >= 0 && number < 2147483648 ? number : null;
        }
        catch
        {
            return null;
        }
    }

    private static double? ReadCapacity(string type, string property, List<string> issues, bool scale = true)
    {
        // Windows battery capacities are milliwatt-hours; the UI reports watt-hours.
        try
        {
            using var q = Query(@"root\wmi", $"SELECT {property} FROM {type}");
            using var rows = q.Get();
            double total = 0;
            var count = 0;
            foreach (ManagementObject row in rows)
                using (row)
                {
                    var n = ValidNumber(row[property]);
                    if (n.HasValue)
                    {
                        total += n.Value;
                        count++;
                    }
                }

            return count > 0 ? total / (scale ? 1000 : 1) : null;
        }
        catch (Exception e)
        {
            issues.Add(type + ": " + e.Message);
            return null;
        }
    }

    public static string ReadXmgDetails()
    {
        if (!File.Exists(XmgPath))
            return "XMG Control Center settings not found.";
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(XmgPath))?["GPUPowerSaving"];
            var mode = node?["currentSaveingMode"]?.GetValue<int>();
            var label = mode switch
            {
                0 => "Off",
                1 => "On",
                2 => "Auto",
                _ => "Unknown"
            };
            return $"Power-saving mode: {label}\r\ncurrentSaveingMode = {mode}\r\nAuto = {node?["Auto"]}\r\nAutoEnable = {node?["AutoEnable"]}\r\n\r\nRead only. MUX/display routing is a separate control.";
        }
        catch (Exception e)
        {
            return "Cannot read XMG settings: " + e.Message;
        }
    }

    private void RefreshMetadata(List<string> issues)
    {
        // Slow-changing display and profile metadata is cached to limit polling overhead.
        _metadataAt = DateTimeOffset.UtcNow;
        try
        {
            if (Native.PowerGetActiveScheme(IntPtr.Zero, out var pointer) == 0)
            {
                try
                {
                    var id = Marshal.PtrToStructure<Guid>(pointer);
                    _plan = id.ToString() switch
                    {
                        "381b4222-f694-41f0-9685-ff5bb260df2e" => "Balanced",
                        "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => "High performance",
                        "a1841308-3541-4fab-bc81-f71556f20b4a" => "Power saver",
                        _ => id.ToString()
                    };
                }
                finally
                {
                    Native.LocalFree(pointer);
                }
            }
        }
        catch (Exception e)
        {
            issues.Add("Power plan: " + e.Message);
        }

        try
        {
            var displays = Native.ReadDisplays();
            _display = string.Join("; ", displays.Select(d => $"{d.Name}: {d.Width}×{d.Height} @ {d.Hz} Hz{(d.Internal ? " (internal)" : "")}"));
            _hz = displays.FirstOrDefault(d => d.Internal)?.Hz;
            _external = displays.Count(d => !d.Internal);
            if (displays.Count == 0)
            {
                _display = "Display topology unavailable";
                _hz = null;
            }
        }
        catch (Exception e)
        {
            issues.Add("Displays: " + e.Message);
            _display = "Unavailable";
            _hz = null;
        }

        _xmg = ReadXmgDetails().Split('\r', '\n')[0];
    }

    public Sample Collect(bool allowCpuSensors = true)
    {
        // Preserve unknown sensor values; battery charging is separate from total system power.
        var watch = Stopwatch.StartNew();
        var errors = new List<string>();
        var now = DateTimeOffset.UtcNow;
        bool? online = null, saver = null, charging = null, discharging = null;
        double? percent = null, watts = null, charge = null, remaining = null, voltage = null, cpu = null, cpuW = null;
        if (Native.GetSystemPowerStatus(out var power))
        {
            online = power.ACLineStatus == 255 ? null : power.ACLineStatus == 1;
            percent = power.BatteryLifePercent == 255 ? null : power.BatteryLifePercent;
            saver = power.SystemStatusFlag == 1;
        }
        else
            errors.Add("GetSystemPowerStatus failed.");
        try
        {
            using var q = Query(@"root\wmi", "SELECT PowerOnline, Discharging, Charging, DischargeRate, ChargeRate, RemainingCapacity, Voltage FROM BatteryStatus");
            using var rows = q.Get();
            var batteryRows = new List<(bool Online, bool Discharging, bool Charging, double? Draw, double? Charge, double? Remaining, double? Voltage)>();
            foreach (ManagementObject row in rows)
                using (row)
                {
                    batteryRows.Add((Convert.ToBoolean(row["PowerOnline"]), Convert.ToBoolean(row["Discharging"]), Convert.ToBoolean(row["Charging"]), ValidNumber(row["DischargeRate"]), ValidNumber(row["ChargeRate"]), ValidNumber(row["RemainingCapacity"]), ValidNumber(row["Voltage"])));
                }

            if (batteryRows.Count > 0)
            {
                // Never publish a partial multi-battery total as the authoritative system draw.
                online = batteryRows.Any(b => b.Online);
                charging = batteryRows.Any(b => b.Charging);
                discharging = batteryRows.Any(b => b.Discharging);
                if (online == false && batteryRows.Any(b => b.Discharging) && batteryRows.All(b => !b.Discharging || b.Draw.HasValue))
                    watts = batteryRows.Where(b => b.Discharging).Sum(b => b.Draw!.Value) / 1000;
                if (batteryRows.Any(b => b.Charging) && batteryRows.All(b => !b.Charging || b.Charge.HasValue))
                    charge = batteryRows.Where(b => b.Charging).Sum(b => b.Charge!.Value) / 1000;
                if (batteryRows.All(b => b.Remaining.HasValue))
                    remaining = batteryRows.Sum(b => b.Remaining!.Value) / 1000;
                if (batteryRows.Count == 1)
                    voltage = batteryRows[0].Voltage / 1000;
            }
            else
                errors.Add("No BatteryStatus provider. Total watts unavailable.");
        }
        catch (Exception e)
        {
            errors.Add("Battery WMI: " + e.Message);
        }

        // Relative battery units cannot be converted to W, Wh, mA, or mAh.
        if (!_absoluteBatteryUnits)
        {
            watts = charge = remaining = null;
            errors.Add("Absolute battery units unavailable; power and mAh estimates withheld.");
        }

        if (Native.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var total = (kernel - _kernel) + (user - _user);
            if (_kernel != 0 && total > 0)
                cpu = Math.Clamp(100d * (total - (idle - _idle)) / total, 0, 100);
            _idle = idle;
            _kernel = kernel;
            _user = user;
        }

        if (_computer != null && allowCpuSensors)
            try
            {
                foreach (var hardware in _computer.Hardware)
                {
                    hardware.Update();
                    var sensor = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase));
                    if (sensor?.Value is float value && value > 0)
                        cpuW = value;
                    else if (sensor?.Value is 0f)
                        errors.Add("CPU package sensor reports zero; treated as unavailable until verified.");
                }
            }
            catch (Exception e)
            {
                errors.Add("CPU sensors: " + e.Message);
            }

        if ((now - _metadataAt).TotalSeconds >= 60)
            RefreshMetadata(errors);
        _self.Refresh();
        var ownCpu = _self.TotalProcessorTime;
        var selfUsage = Math.Max(0, (ownCpu - _selfCpu).TotalSeconds / Math.Max(0.001, (now - _previous).TotalSeconds) / Environment.ProcessorCount * 100);
        if (_polls == 0)
            selfUsage = 0;
        _selfCpu = ownCpu;
        _previous = now;
        _polls++;
        return new(now, online, percent, watts, charge, remaining, voltage, cpu, cpuW, "Unknown — not queried (sleep-safe)", null, _hz, _external, _display, _plan, saver, _xmg, selfUsage, _self.WorkingSet64 / 1048576d, _polls, watch.Elapsed.TotalMilliseconds, string.Join("\r\n", errors))
        {
            Charging = charging,
            Discharging = discharging
        };
    }

    public void Dispose()
    {
        // Release owned native handles and flush pending work when the component shuts down.
        _computer?.Close();
        _self.Dispose();
    }
}
