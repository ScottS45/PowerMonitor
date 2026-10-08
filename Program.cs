// ============================================================================
// File: Program.cs
// Author: Scott Sheen (with ChatGpt)
// Created: 2026-10-02
// Updated: 2026-10-03
// Purpose: Application startup and isolated diagnostic entry points.
// ============================================================================

using System.Text.Json;

namespace PowerMonitor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--self-test"))
        {
            SelfTests.Run(args);
            return;
        }

        if (args.Contains("--diagnose"))
        {
            Diagnose(args);
            return;
        }

        if (args.Contains("--cpu-counter-probe"))
        {
            using var counters = new ProcessCounterFallback();
            var counterFirst = counters.Read();

            Thread.Sleep(5000);
            var second = counters.Read();
            File.WriteAllText(Option(args, "--output")!, JsonSerializer.Serialize(new { counterFirst, second, counters.TotalCpu, counters.IntervalSeconds, counters.Status }, AppFiles.Json));
            return;
        }
        if (args.Contains("--hardware-probe"))
        {
            using var hardware = new HardwareEnergy(true);
            using var telemetry = new Telemetry();
            var detection = telemetry.Detect(true);
            var readings = new List<object>();
            for (int i = 0; i < 4; i++)
            {
                readings.Add(new { Hardware = hardware.Read(), Sample = telemetry.Collect(true) });
                Thread.Sleep(3000);
            }
            File.WriteAllText(Option(args, "--output")!, JsonSerializer.Serialize(new { detection, readings }, AppFiles.Json));
            return;
        }
        if (args.Contains("--energy-worker")) { WindowsEnergyWorker.Run(args); return; }
        ApplicationConfiguration.Initialize();
        if (args.Contains("--ui-smoke"))
        {
            UiSmoke(args);
            return;
        }

        using var mutex = new Mutex(true, @"Local\LaptopPowerMonitor", out var first);
        if (!first)
        {
            MessageBox.Show("Power Monitor is already running. Exit the old app from its tray menu before starting this version.");
            return;
        }

        Application.ThreadException += (_, e) =>
        {
            AppFiles.Log(e.Exception.ToString());
            MessageBox.Show(e.Exception.Message, "Power Monitor");
        };
        try
        {
            Application.Run(new MainForm(AppFiles.Load()));
        }
        catch (Exception e)
        {
            AppFiles.Log(e.ToString());
            MessageBox.Show(e.Message, "Power Monitor could not start");
        }
    }

    private static void UiSmoke(string[] args)
    {
        // Use an isolated data directory so preview captures cannot alter the user's history or settings.
        var output = Option(args, "--output") ?? Path.Combine(AppContext.BaseDirectory, "ui-smoke");
        Directory.CreateDirectory(output);
        AppFiles.UseRoot(Path.Combine(output, "data"));
        var settings = new MonitorSettings
        {
            CloseToTray = false,
            EnableHardwareEnergy = args.Contains("--hardware"),
            NotificationsEnabled = false
        };
        using var form = new MainForm(settings);
        using var timer = new System.Windows.Forms.Timer
        {
            Interval = 45000
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var tabs = form.Pages;
            foreach (TabPage page in tabs.TabPages)
            {
                form.SelectPage(page.Text, false);
                form.Refresh();
                using var image = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(image, new Rectangle(0, 0, form.Width, form.Height));
                image.Save(Path.Combine(output, page.Text.Replace(" ", "-") + ".png"));
            }

            form.Size = new Size(1050, 720);
            foreach (var name in new[]
            {
                "Live",
                "Processes",
                "Settings"
            }

            )
            {
                form.SelectPage(name, false);
                form.Refresh();
                using var image = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(image, new Rectangle(0, 0, form.Width, form.Height));
                image.Save(Path.Combine(output, name + "-compact.png"));
            }

            try
            {
                form.Size = new Size(1380, 900);
                form.VerifyActivityInteractions(name =>
                {
                    form.Refresh();
                    using var capture = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(capture, new Rectangle(0, 0, form.Width, form.Height));
                    capture.Save(Path.Combine(output, name + ".png"));
                });
                File.WriteAllText(Path.Combine(output, "interaction-checks.txt"), "PASS: live and processes header directions, refresh persistence, system expand/collapse");
            }
            catch (Exception e)
            {
                File.WriteAllText(Path.Combine(output, "interaction-checks.txt"), "FAIL: " + e);
                Environment.ExitCode = 1;
            }
            form.Close();
        };
        form.Shown += (_, _) => timer.Start();
        Application.Run(form);
    }

    private static void Diagnose(string[] args)
    {
        var output = Option(args, "--output") ?? Path.Combine(AppContext.BaseDirectory, "diagnostics.json");
        using var telemetry = new Telemetry();
        using var processes = new ProcessMonitor();
        using var energy = new WindowsEnergyReader(Option(args, "--energy-feed"));
        var detection = telemetry.Detect(args.Contains("--cpu-sensors"));
        var samples = new List<Sample>();
        var snapshots = new List<ProcessSnapshot>();
        var seconds = int.TryParse(Option(args, "--seconds"), out var n) ? Math.Clamp(n, 5, 300) : 60;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var lastActivity = DateTimeOffset.MinValue;
        ProcessSnapshot? activity = null;
        do
        {
            var sample = telemetry.Collect();
            var collection = System.Diagnostics.Stopwatch.StartNew();
            if (args.Contains("--activity") && (sample.Time - lastActivity).TotalSeconds >= 10)
            {
                activity = processes.Collect(!args.Contains("--no-gpu"), args.Contains("--restricted-counters"));
                activity = energy.Apply(sample, activity);
                snapshots.Add(activity);
                lastActivity = sample.Time;
            }

            samples.Add(sample with
            {
                CpuPercent = activity?.TotalCpuPercent ?? sample.CpuPercent,
                GpuPercent = activity?.GpuPercent,
                ActivityTime = activity?.Time,
                ActivityStatus = activity?.Status,
                TopProcesses = activity?.Apps.Take(6).Select(a => new Contributor(a.Name, a.CpuPercent, a.GpuPercent, a.IoMiBPerSecond, a.Impact, a.Gpu, a.Pids.Length)).ToArray(),
                CollectionMs = sample.CollectionMs + collection.Elapsed.TotalMilliseconds
            });
            Thread.Sleep(5000);
        }
        while (watch.Elapsed.TotalSeconds < seconds);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Detection = detection,
            Samples = samples,
            Activity = snapshots
        }, AppFiles.Json));
    }

    internal static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
