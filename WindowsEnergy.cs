// ============================================================================
// File: WindowsEnergy.cs
// Author: Scott Sheen (with ChatGpt)
// Created: 2026-10-03
// Updated: 2026-10-03
// Purpose: Read Windows E3 component energy without installing a sensor driver.
// ============================================================================
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace PowerMonitor;

public record WindowsEnergyRecord(string AppId, DateTimeOffset Time, double Seconds, double MilliJoules,
    double CpuMilliJoules, double GpuSocMilliJoules, double DiskMilliJoules, double NetworkMilliJoules,
    uint Flags, uint Measured, long Sequence = 0);
public record WindowsEnergyFeed(DateTimeOffset Started, DateTimeOffset Updated, string Status, WindowsEnergyRecord[] Records, int ParentPid = 0);

public static class WindowsEnergyMath
{
    public static double? WattHours(double milliJoules) => double.IsFinite(milliJoules) && milliJoules >= 0 ? milliJoules / 3600000 : null;
    public static double? Watts(double milliJoules, double seconds) => seconds > 0 ? WattHours(milliJoules) * 3600 / seconds : null;
    public static string Identity(string id)
    {
        // Device paths and service suffixes are not distinct executables.
        var suffix = id.IndexOf(" [", StringComparison.Ordinal);
        if (suffix >= 0) id = id[..suffix];
        return id.Replace('/', '\\').Trim();
    }
}

public sealed class WindowsEnergyReader : IDisposable
{
    private readonly string _feed;
    private readonly string _stop;
    private readonly bool _fixture;
    public WindowsEnergyReader(string? fixtureFeed = null)
    {
        _fixture = fixtureFeed != null;
        _feed = fixtureFeed ?? Path.Combine(AppFiles.Root, "windows-energy.json");
        _stop = fixtureFeed != null ? fixtureFeed + ".stop" : Path.Combine(AppFiles.Root, "windows-energy.stop");
    }
    private WindowsEnergyFeed? _cached;
    private DateTime _fileTime;
    private readonly Dictionary<string, double> _charge = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _wh = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = new();
    private readonly HashSet<string> _chargeSeen = new();
    private readonly Dictionary<string, bool> _matches = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _epoch;
    public string Status { get; private set; } = "Windows energy capture is off. Click Windows energy (admin).";

    public void Start()
    {
        if (_cached is { } active && active.ParentPid == Environment.ProcessId && DateTimeOffset.UtcNow - active.Updated < TimeSpan.FromSeconds(25)) return;
        Directory.CreateDirectory(AppFiles.Root);
        if (File.Exists(_stop)) File.Delete(_stop);
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable");
        Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            ArgumentList = { "--energy-worker", "--output", _feed, "--stop-file", _stop, "--parent", Environment.ProcessId.ToString(CultureInfo.InvariantCulture) }
        });
        Status = "Starting Windows energy capture; waiting for its next reporting interval.";
    }

    public ProcessSnapshot Apply(Sample sample, ProcessSnapshot snapshot)
    {
        try
        {
            if (File.Exists(_feed) && File.GetLastWriteTimeUtc(_feed) != _fileTime)
            {
                var feed = JsonSerializer.Deserialize<WindowsEnergyFeed>(File.ReadAllText(_feed));
                if (feed != null)
                {
                    _cached = feed;
                    _fileTime = File.GetLastWriteTimeUtc(_feed);
                }
            }
        }
        catch (IOException) { } // An atomic replacement can race a reader.
        catch (JsonException) { Status = "Windows energy feed is invalid; existing estimates retained."; }
        var data = _cached;
        if (data == null) return WithStatus(snapshot, Status);
        if ((!_fixture && data.ParentPid != Environment.ProcessId) || sample.Time - data.Updated > TimeSpan.FromSeconds(25))
            return WithStatus(snapshot, "Windows energy capture is stopped or stale. Click Windows energy (admin) to restart.");
        if (_epoch != data.Started)
        {
            _epoch = data.Started;
            _charge.Clear(); _wh.Clear(); _seen.Clear(); _chargeSeen.Clear(); _matches.Clear();
        }
        Status = data.Status;
        var groups = data.Records.GroupBy(r => r.AppId).ToArray();
        var apps = snapshot.Apps.Select(app =>
        {
            var matching = groups.Where(g =>
            {
                var matchKey = g.Key + "|" + (app.Path ?? string.Join(",", app.Pids));
                if (!_matches.TryGetValue(matchKey, out var match))
                {
                    match = Matches(g.Key, app);
                    if (match) _matches[matchKey] = true;
                }
                return match || FallbackMatches(g.Key, app, snapshot.Apps, groups.Select(group => group.Key));
            }).SelectMany(g => g).ToArray();
            var key = app.Path ?? app.Name;
            foreach (var r in matching)
            {
                // Only accumulate each Windows interval once, including across cached UI refreshes.
                var identity = $"{r.Sequence}|{r.AppId}|{r.Time.UtcTicks}";
                var energy = WindowsEnergyMath.WattHours(r.MilliJoules);
                var current = BatteryMetrics.ToMilliAmps(energy, sample.Voltage);
                if (energy.HasValue && _seen.Add(identity))
                {
                    _wh[key] = _wh.GetValueOrDefault(key) + energy.Value;
                }
                // Voltage may arrive after the energy interval. Charge conversion has its
                // own deduplication so that delayed voltage does not permanently lose mAh.
                if (energy.HasValue && current.HasValue && _chargeSeen.Add(identity))
                    _charge[key] = _charge.GetValueOrDefault(key) + current.Value;
            }
            var recent = matching.Where(r => sample.Time - r.Time < TimeSpan.FromMinutes(2)).ToArray();
            if (recent.Length == 0)
                return RetainCapturedTotals(app, "Windows E3 interval not yet available; captured totals retained on AC and battery.");
            var latest = recent.Max(r => r.Time);
            var batch = recent.Where(r => (latest - r.Time).TotalSeconds <= 3).ToArray();
            // Foreground/background records in the same batch cover overlapping time.
            double seconds = batch.Max(r => r.Seconds);
            double? watts = WindowsEnergyMath.Watts(batch.Sum(r => r.MilliJoules), seconds);
            var coverage = batch.Sum(r => r.GpuSocMilliJoules) > 0 ? "GPU/SoC energy present" : "GPU/SoC energy absent in this interval";
            var source = $"Windows E3 component model, {seconds:F0}s average; {Math.Max(0, (sample.Time-latest).TotalSeconds):F0}s old. mAh is battery-equivalent on AC. {coverage}; discrete GPU/idle overhead may be missing.";
            
            return app with
            {
                EstimatedWatts = watts, EstimatedMilliAmpHours = _charge.TryGetValue(key, out var charge) ? charge : null,
                EstimatedWattHours = _wh.GetValueOrDefault(key), EnergySource = source + IdentityCoverage(app),
                Processes = app.Processes.Select(p => p with
                {
                    // E3 reports an application, not its PID split. Do not invent per-PID energy.
                    EstimatedWatts = app.Processes.Length == 1 ? watts : null,
                    EstimatedMilliAmpHours = app.Processes.Length == 1 && _charge.TryGetValue(key, out var pidCharge) ? pidCharge : null
                }).ToArray()
            };
        }).ToArray();
        var modelApps = apps.Count(a => a.EnergySource.StartsWith("Windows E3", StringComparison.Ordinal));
        // E3 can cover different time windows from the battery meter; do not subtract
        // its averaged watts from instantaneous battery watts or fabricate a balanced sum.
        return snapshot with
        {
            Apps = apps,
            Accounting = new(sample.Online == false ? sample.Watts : null, null, null, sample.UsedMilliAmpHours,
                null, null, sample.BatteryObservedSeconds,
                $"{Status} | {modelApps} running app groups matched. E3 averages and battery readings cover different intervals; no artificial reconciliation.")
        };
    }

    private ProcessSnapshot WithStatus(ProcessSnapshot snapshot, string status)
    {
        Status = status;
        return snapshot with { Apps = snapshot.Apps.Select(app => RetainCapturedTotals(app, status)).ToArray(), Accounting = snapshot.Accounting is { } a ? a with { Status = a.Status + " " + status } : null };
    }

    private AppActivity RetainCapturedTotals(AppActivity app, string status)
    {
        var key = app.Path ?? app.Name;
        if (!_wh.TryGetValue(key, out var wh))
            return app with { EnergySource = app.EnergySource + "; " + status };

        double? mah = _charge.TryGetValue(key, out var charge) ? charge : null;
        return app with
        {
            EstimatedWatts = null,
            EstimatedWattHours = wh,
            EstimatedMilliAmpHours = mah,
            EnergySource = "Windows E3 captured session totals; " + status + " On AC, mAh is battery-equivalent. No energy is invented for reporting gaps." + IdentityCoverage(app),
            Processes = app.Processes.Select(p => p with
            {
                EstimatedWatts = null,
                EstimatedMilliAmpHours = app.Processes.Length == 1 ? mah : null
            }).ToArray()
        };
    }

    private static string IdentityCoverage(AppActivity app)
    {
        if (app.Name == CpuCoverage.UnattributedName)
            return " Partial coverage: Windows interrupt energy only; other unresolved CPU energy is unavailable. Separate from PID 4 System energy.";
        if (app.Path != null) return "";
        if (app.Name == "System" && app.Pids.Contains(4)) return " Windows System record matched to kernel PID 4.";
        return " Unique exact executable-name match; path inaccessible, identity unverified. App-group estimate, not verified per-PID energy.";
    }

    public static bool FallbackMatches(string id, AppActivity app, IEnumerable<AppActivity> apps, IEnumerable<string> recordIds)
    {
        id = WindowsEnergyMath.Identity(id);
        if (app.Name == CpuCoverage.UnattributedName && app.Pids.Length == 0)
            return id.Equals("System Interrupts", StringComparison.OrdinalIgnoreCase);

        if (app.Name == "System" && app.Pids.Contains(4))
            return id.Equals("System", StringComparison.OrdinalIgnoreCase);

        if (app.Path != null || app.Pids.Length == 0 || apps.Count(a => a.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase)) != 1)
            return false;

        // This is an explicitly labelled identity heuristic. Exact executable names
        // must resolve to one distinct E3 path and one running app group; ambiguous
        // names are withheld rather than assigning one record to multiple programs.
        var fileName = app.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app.Name : app.Name + ".exe";
        var candidates = recordIds.Select(WindowsEnergyMath.Identity)
            .Where(path => path.Contains('\\') && Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return candidates.Length == 1 && id.Equals(candidates[0], StringComparison.OrdinalIgnoreCase);
    }

    public static bool Matches(string id, AppActivity app)
    {
        id = WindowsEnergyMath.Identity(id);
        if (app.Path != null)
        {
            var path = WindowsEnergyMath.Identity(app.Path);
            if (id.Equals(path, StringComparison.OrdinalIgnoreCase)) return true;
            if (path.Length > 2 && path[1] == ':')
            {
                var target = new StringBuilder(1024);
                if (QueryDosDevice(path[..2], target, target.Capacity) > 0 &&
                    id.Equals(target + path[2..], StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        // Package identity is checked against the actual process, never matched by a vague name.
        foreach (var p in app.Processes)
        {
            var h = OpenProcess(0x1000, false, p.Pid);
            if (h == IntPtr.Zero) continue;
            try
            {
                uint length = 0;
                if (GetPackageFullName(h, ref length, null) == 122 && length < 2048)
                {
                    var name = new StringBuilder((int)length);
                    if (GetPackageFullName(h, ref length, name) == 0 && id.Equals(name.ToString(), StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            finally { CloseHandle(h); }
        }
        return false;
    }

    public void Dispose()
    {
        try { if (_cached != null) File.WriteAllText(_stop, "Stop"); } catch (IOException) { }
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern uint QueryDosDevice(string device, StringBuilder target, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern int GetPackageFullName(IntPtr process, ref uint length, StringBuilder? name);
}

public static class WindowsEnergyWorker
{
    public static void Run(string[] args)
    {
        var output = Program.Option(args, "--output")!;
        var stop = Program.Option(args, "--stop-file")!;
        var parent = int.Parse(Program.Option(args, "--parent")!, CultureInfo.InvariantCulture);
        var started = DateTimeOffset.UtcNow;
        var records = new List<WindowsEnergyRecord>();
        long sequence = 0;
        var gate = new object();
        string status = "Waiting for Windows E3's next reporting interval (normally about one minute).";
        var sessionName = "PowerMonitor-E3-" + parent;
        using var session = new TraceEventSession(sessionName);
        session.StopOnDispose = true;
        try
        {
            session.Source.Dynamic.All += e =>
            {
                // QueryStats v2 is an interval estimate. MergeStats represents the same
                // energy and must not be counted again. Other event versions are ignored.
                if ((int)e.ID != 37 || e.ProviderGuid != new Guid("DDCC3826-A68A-4E0D-BCFD-9C06C27C6948")) return;
                try
                {
                    double Value(string name) => Convert.ToDouble(e.PayloadByName(name), CultureInfo.InvariantCulture);
                    var name = e.PayloadByName("AppName")?.ToString() ?? "";
                    var seconds = Value("TimeInMSec") / 1000;
                    if (name.Length == 0 || seconds <= 0) return;
                    var time = new DateTimeOffset(e.TimeStamp.ToUniversalTime());
                    // Clip the first interval to the capture lifetime, excluding pre-session work.
                    var fraction = Math.Clamp((time - started).TotalSeconds / seconds, 0, 1);
                    var cpu = Value("CpuEnergy"); var soc = Value("SocEnergy");
                    var disk = Value("DiskEnergy"); var network = Value("NetworkEnergy") + Value("MbbEnergy");
                    var total = cpu + soc + disk + network + Value("DisplayEnergy") + Value("OtherEnergy") + Value("LossEnergy") + Value("EmiEnergy");
                    if (!double.IsFinite(total) || total < 0) return;
                    lock (gate)
                    {
                        records.Add(new(name, time, seconds * fraction, total * fraction, cpu * fraction,
                            soc * fraction, disk * fraction, network * fraction,
                            (uint)Value("RecordFlags"), (uint)Value("RecordMeasured"), ++sequence));
                        status = $"Windows E3 capture active; {records.Count} component intervals received.";
                        if (records.Count > 20000) records.RemoveRange(0, records.Count - 20000);
                    }
                }
                catch (Exception) { /* An unsupported payload stays unavailable. */ }
            };
            session.EnableProvider(new Guid("DDCC3826-A68A-4E0D-BCFD-9C06C27C6948"), TraceEventLevel.Informational, 4);
            var consume = Task.Run(() => session.Source.Process());
            while (!File.Exists(stop) && ParentAlive(parent) && !consume.IsCompleted)
            {
                WindowsEnergyFeed feed;
                lock (gate) feed = new(started, DateTimeOffset.UtcNow, status, records.ToArray(), parent);
                var temp = output + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(feed));
                File.Move(temp, output, true);
                Thread.Sleep(3000);
            }
            session.Source.StopProcessing();
            consume.Wait(5000);
        }
        catch (Exception e)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new WindowsEnergyFeed(started, DateTimeOffset.UtcNow, "Windows energy: " + e.Message, [], parent)));
        }
    }
    private static bool ParentAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited && p.ProcessName == "PowerMonitor"; }
        catch (ArgumentException) { return false; }
    }
}