// ============================================================================
// File: SelfTests.cs
// Author: Scott Sheen (with ChatGpt)
// Created: 2026-10-02
// Updated: 2026-10-03
// Purpose: Regression checks for measurements, alert rules, and persistence.
// ============================================================================

using System.Text.Json;

namespace PowerMonitor;

internal static class SelfTests
{

    public static void Run(string[] args)
    {
        // Run deterministic measurement, alert, and persistence checks without starting the tray application.
        var output = Program.Option(args, "--output") ?? Path.Combine(AppContext.BaseDirectory, "self-test.txt");

        // Collect named regression outcomes; any failed invariant stops the test run.
        var results = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition)
                throw new Exception("FAIL: " + name);
            results.Add("PASS: " + name);
        }

        try
        {
            HardwareEnergyTests.Run(Check);
            var sorting = new ActivityListOrder();
            AppActivity SortApp(string name, double? cpu) => new(name, null, [], cpu, null, null, null, "", "", "", []);
            var sortApps = new[] { SortApp("nine", 9), SortApp("hundred", 100), SortApp("unknown", null) };
            Check(sorting.Sort(sortApps).Select(a => a.Name).SequenceEqual(["hundred", "nine", "unknown"]), "CPU sort is numeric, highest-first, and unknown-last");
            var changedApps = new[] { SortApp("nine", 101), SortApp("hundred", 1), SortApp("unknown", null) };
            Check(sorting.Sort(changedApps).First().Name == "nine" && sorting.Descending, "CPU leaders move to the top without resetting the chosen sort");
            sorting.Click(ActivityColumn.Cpu);
            Check(!sorting.Descending && sorting.Sort(sortApps).Select(a => a.Name).SequenceEqual(["nine", "hundred", "unknown"]), "Header click reverses numeric sort and keeps unknown values last");
            sorting.Select(ActivityColumn.App);
            Check(sorting.Sort(sortApps).First().Name == "hundred", "App sort uses names rather than activity");
            sorting.Select(ActivityColumn.Cpu);
            using (var glyphGrid = new DataGridView())
            {
                glyphGrid.Columns.Add(new DataGridViewTextBoxColumn());
                glyphGrid.Columns.Add(new DataGridViewTextBoxColumn());
                sorting.ApplyGlyphs(glyphGrid, [ActivityColumn.App, ActivityColumn.Cpu]);
                Check(glyphGrid.Columns[1].HeaderCell.Value.ToString()!.EndsWith("\u2193") && !glyphGrid.Columns[0].HeaderCell.Value.ToString()!.Contains("\u2193"), "Only the active column has the correct sort arrow");
            }
            var split = CpuCoverage.Breakdown(4, 1, 2);
            Check(split.Sum(p => p.CpuPercent ?? 0) == 4 && split[2].CpuPercent == 1, "System CPU children reconcile to their parent subtotal");
            Check(CpuCoverage.Breakdown(4, null, null)[0].CpuPercent == null, "Unavailable interrupt/DPC values are not replaced with zero");
            Check(CpuCoverage.Breakdown(4, 3, 3).Length == 1, "Overlapping counters do not fabricate a system CPU split");
            var systemSort = new ProcessSnapshot(DateTimeOffset.UtcNow, [SortApp(CpuCoverage.UnattributedName, 4)], [], "", 0, 0, []) { SystemCpuParts = split };
            Check(sorting.Rows(systemSort).Count() == 1, "System CPU starts rolled up");
            sorting.SystemExpanded = true;
            Check(sorting.Rows(systemSort).Count() == 4 && sorting.Rows(systemSort).Skip(1).All(r => r.Child && r.App.Pids.Length == 0), "System CPU expands without inventing process IDs");
            Check(sorting.Rows(systemSort with { Time = DateTimeOffset.UtcNow.AddSeconds(10) }).Count() == 4, "Expanded state survives refreshed readings");
            Check(ProcessCounterFallback.Rate(200, 100, 1, 1, 1000) == 10, "Raw process CPU uses same total-system interval");
            Check(ProcessCounterFallback.Rate(200, 100, 2, 1, 1000) == null, "Protected process PID reuse does not inherit CPU");
            Check(ProcessCounterFallback.Rate(50, 100, 1, 1, 1000) == null, "Process CPU counter reset stays unknown");
            var cpuProcess = new ProcessActivity(100, "protected", null, null, 2, null, null, []);
            var cpuApp = ActivityRules.Group([cpuProcess], false)[0];
            var cpuSnapshot = new ProcessSnapshot(DateTimeOffset.UtcNow, [cpuApp], [], "", 0, 1, []);
            var coverage = CpuCoverage.Apply(cpuSnapshot, 6, 10);
            Check(coverage.Apps.Sum(a => a.CpuPercent ?? 0) == 6 && coverage.UnattributedCpuPercent == 4, "All system CPU is visible as processes plus explicit residual");
            Check(coverage.Apps.Single(a => a.Name == CpuCoverage.UnattributedName).Pids.Length == 0, "Unattributed CPU does not invent a process");
            Check(CpuCoverage.Apply(coverage, 6, 10).Apps.Count(a => a.Name == CpuCoverage.UnattributedName) == 1, "Repeated coverage updates do not double count residual CPU");
            Check(CpuCoverage.Apply(cpuSnapshot, null, 0).UnattributedCpuPercent == null, "Missing CPU total is not reported as idle");
            var config = new MonitorSettings
            {
                UnplugGraceSeconds = 0
            };
            var time = DateTimeOffset.UtcNow;
            Sample SampleAt(int seconds, double? watts, bool? ac = false) => new(time.AddSeconds(seconds), ac, 70, watts, null, 60, 15, 2, null, "Unknown", null, 60, 0, "Panel", "Balanced", false, "Unavailable", 0, 70, 1, 5, "");
            var engine = new AlertEngine();
            Check(engine.Evaluate(SampleAt(0, 35), config, false) == null, "No immediate spike alert");
            for (var i = 5; i < 30; i += 5)
                Check(engine.Evaluate(SampleAt(i, 35), config, false) == null, "Sustained duration " + i);
            Check(engine.Evaluate(SampleAt(30, 35), config, false)?.Severity == "High", "High threshold qualified");
            Check(engine.Evaluate(SampleAt(35, 35), config, false) == null, "Repeat cooldown");
            for (var i = 40; i <= 70; i += 5)
                engine.Evaluate(SampleAt(i, 24), config, false);
            Check(engine.State == "0", "High clears to sustained Warning");
            for (var i = 75; i <= 105; i += 5)
                engine.Evaluate(SampleAt(i, 16), config, false);
            Check(engine.State == "Normal", "Warning clear hysteresis");
            engine = new AlertEngine();
            for (var i = 0; i <= 15; i += 5)
            {
                var alert = engine.Evaluate(SampleAt(i, 50), config, false);
                if (i == 15)
                    Check(alert?.Severity == "Critical", "Critical duration");
            }

            Check(engine.Evaluate(SampleAt(20, null), config, false) == null && engine.State == "Normal", "Missing telemetry resets sustained state");
            Check(engine.Evaluate(SampleAt(25, 80, true), config, false) == null, "No AC alerts");
            engine = new AlertEngine();
            for (var i = 0; i <= 30; i += 5)
                Check(engine.Evaluate(SampleAt(i, 35), config, true) == null, "Exclusion suppresses notification " + i);
            engine = new AlertEngine();
            engine.Evaluate(SampleAt(0, 35), config, false);
            Check(engine.Evaluate(SampleAt(40, 35), config, false) == null, "Sampling gap breaks sustained condition");
            Check(Telemetry.ValidNumber(uint.MaxValue) == null && Telemetry.ValidNumber(2147483648u) == null, "Battery unknown sentinel rejected");
            Check(Telemetry.ValidNumber(41068) == 41068, "Valid battery milliwatts preserved");
            var temp = Path.Combine(Path.GetDirectoryName(output)!, "test-history-" + Guid.NewGuid() + ".sqlite");
            using (var history = new HistoryStore(temp, 90))
            {
                history.Add(SampleAt(0, 41.068));
                history.Add(SampleAt(20, null, true));
                history.Add(SampleAt(40, 15));
                Check(history.Transactions == 1 && history.SamplesWritten == 3, "SQLite samples batched");
                var rows = history.Read(time.AddSeconds(-1));
                Check(rows.Count == 3 && rows[0].Watts == 41.068 && rows[1].Watts == null, "SQLite null and measured rate round trip");
                history.AddAlert(new(time, "High", 35, "Measured"));
                Check(history.ReadAlerts().Count == 1, "Alert persistence");
            }

            File.Delete(temp);
            var invalid = new MonitorSettings
            {
                SampleSeconds = 1
            };
            var rejected = false;
            try
            {
                invalid.Validate();
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }

            Check(rejected, "Aggressive polling rejected");
            var adapters = new Dictionary<string, string>
            {
                ["00000000:00000011"] = "AMD Radeon 610M",
                ["00000000:00000022"] = "NVIDIA RTX 5070 Ti"
            };
            var amd = GpuActivityReader.Parse("pid_77_luid_0x00000000_0x00000011_phys_0_eng_0_engtype_3D", 12.5, adapters)!;
            var nvidia = GpuActivityReader.Parse("pid_88_luid_0x00000000_0x00000022_phys_0_eng_0_engtype_3D", 8, adapters)!;
            Check(amd.Adapter.StartsWith("AMD") && nvidia.Adapter.StartsWith("NVIDIA"), "Both phys_0 counters map to different adapters by LUID");
            Check(!GpuActivityReader.Parse("pid_88_luid_0x00000000_0x00000022_phys_1_eng_0_engtype_3D", 8, adapters)!.Mapped, "Unverified linked physical GPU is not guessed");
            Check(GpuActivityReader.Parse("bad_instance", 12, adapters) == null && GpuActivityReader.Parse("pid_77_luid_0x00000000_0x00000011_phys_0_eng_0_engtype_3D", -1, adapters) == null, "Malformed and negative GPU data rejected");
            Check(ActivityRules.CpuRate(100_000_000, 0, 1, 1, 10, 8) == 12.5, "Per-process CPU normalized to total machine capacity");
            Check(ActivityRules.CpuRate(100_000_000, 0, 2, 1, 10, 8) == null, "PID reuse does not reuse CPU baseline");
            var second = amd with
            {
                Pid = 78,
                Usage = 7.5
            };
            var copy = amd with
            {
                Engine = "Copy (engine 1)",
                Usage = 50
            };
            var grouped = ActivityRules.Group([new ProcessActivity(77, "opera", "C:/Opera/opera.exe", time, 1, 0.1, 50, [amd, copy]), new ProcessActivity(78, "opera", "C:/Opera/opera.exe", time, 2, 0.2, 50, [second])], true);
            Check(grouped.Length == 1 && grouped[0].Pids.Length == 2 && grouped[0].CpuPercent == 3, "Browser subprocess CPU grouped");
            Check(grouped[0].GpuPercent == 50, "GPU uses busiest physical engine, not sum across engines");
            Check(ActivityRules.BusiestEngine([amd with { Usage = 80 }, second with { Usage = 60 }]) == 100, "GPU engine aggregate capped at 100 percent");
            Check(ActivityRules.Impact(null, null, null) == "Unknown", "Missing activity is not rated idle");
            using (var store = new HistoryStore(temp, 90))
            {
                var context = SampleAt(120, null, true) with
                {
                    GpuPercent = 12.5,
                    ActivityTime = time,
                    TopProcesses = [new Contributor("opera", 3, 12.5, 0.3, "Medium", "AMD Radeon 610M", 2)]
                };
                store.Add(context);
                store.Flush();
                var restored = store.Read(time.AddSeconds(119)).Single();
                Check(restored.Watts == null && restored.GpuPercent == 12.5 && restored.TopProcesses![0].Name == "opera", "AC activity and contributor history round trip without fabricated watts");
            }

            Check(BatteryMetrics.ToMilliAmps(20, 10) == 2000, "Watts and voltage convert to milliamps");
            Check(BatteryMetrics.ToMilliAmps(60, 15) == 4000, "Remaining Wh converts to estimated mAh");
            Check(BatteryMetrics.ToMilliAmps(20, 0) == null && BatteryMetrics.ToMilliAmps(null, 10) == null, "Invalid current conversion stays unknown");
            Check(new BatteryInfo(100, 80, 10).HealthPercent == 80, "Health is retained design capacity");
            Check(new BatteryInfo(null, 80, null).HealthPercent == null, "Missing design capacity means unknown health");
            var tracker = new BatteryUsageTracker();
            var start = SampleAt(0, 15) with
            {
                Voltage = 15,
                Discharging = true,
                Charging = false
            };
            tracker.Observe(start, 10);
            var usage = tracker.Observe(start with
            {
                Time = start.Time.AddSeconds(10)
            }, 10);
            Check(Math.Abs(usage.UsedMilliAmpHours!.Value - 1000d / 360) < 0.0001, "mAh integrates measured current over time");
            usage = tracker.Observe(start with
            {
                Time = start.Time.AddSeconds(1000)
            }, 10);
            Check(Math.Abs(usage.UsedMilliAmpHours!.Value - 1000d / 360) < 0.0001, "Sleep gaps do not inflate mAh usage");
            tracker.BreakInterval();
            usage = tracker.Observe(start with
            {
                Time = start.Time.AddSeconds(1010)
            }, 10);
            Check(usage.BatteryObservedSeconds == 10, "Pause skips unobserved battery intervals");
            var charger = new BatteryUsageTracker();
            var onAc = start with
            {
                Online = true,
                Watts = null,
                ChargeWatts = 30,
                Charging = true,
                Discharging = false
            };
            charger.Observe(onAc, 10);
            var charged = charger.Observe(onAc with
            {
                Time = onAc.Time.AddSeconds(10)
            }, 10);
            Check(charged.AddedMilliAmpHours > 5 && charged.UsedMilliAmpHours == null, "Charge integration remains separate from usage");
            Check(BatteryMetrics.ChargingStatus(onAc) == "Charging", "Firmware charging state displayed");
            Check(BatteryMetrics.ChargingStatus(onAc with
            {
                Charging = false,
                ChargeWatts = null,
                Percent = 80
            }) == "On AC / not charging", "Charge limit is not inferred as a fault");
            var economical = new MonitorSettings { SampleSeconds = 5, ProcessSeconds = 10, HistorySeconds = 20 };
            Check(economical.EffectiveSampleSeconds == 10 && economical.EffectiveProcessSeconds == 10 && economical.EffectiveHistorySeconds == 60 && !economical.EffectiveGpuActivity, "Low-power defaults reduce polling and disable GPU counters");
            economical.LowPowerMode = false;
            Check(economical.EffectiveSampleSeconds == 5 && economical.EffectiveGpuActivity, "Diagnostic mode restores requested activity settings");

            Check(WindowsEnergyMath.WattHours(3600000) == 1, "E3 millijoules convert to Wh");
            Check(WindowsEnergyMath.Watts(120000, 60) == 2, "E3 interval energy converts to averaged watts");
            Check(WindowsEnergyMath.Watts(1, 0) == null && WindowsEnergyMath.WattHours(-1) == null, "Invalid E3 energy stays unavailable");
            Check(WindowsEnergyMath.Identity(@"C:\app.exe [LocalService] [service]") == @"C:\app.exe", "Service suffix does not break energy identity");
            Check(charged.AddedWattHours > 0 && charged.UsedWattHours == null, "Battery Wh charging totals remain separate from discharge totals");
            // Deterministic battery fixtures verify accounting without waking hardware.
            var allocator = new PowerAttributor();
            var fixtureProcess = new ProcessActivity(123, "fixture", "C:/fixture.exe", time, 20, 0, 1, []);
            var fixtureApp = ActivityRules.Group([fixtureProcess], false).Single();
            ProcessSnapshot ActivityAt(int offset) => new(time.AddSeconds(offset), [fixtureApp], [], "test", 0, 0, []);
            var battery = SampleAt(0, 20) with
            {
                Voltage = 10
            };
            var allocated = allocator.Allocate(battery, ActivityAt(0), 10, 30);
            var feedPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "e3-fixture.json");
            File.WriteAllText(feedPath, JsonSerializer.Serialize(new WindowsEnergyFeed(time, time, "fixture", [
                new("C:/fixture.exe", time, 3600, 3600000, 3600000, 0, 0, 0, 0, 0, 1)])));
            using (var windowsModel = new WindowsEnergyReader(feedPath))
            {
                var e3 = windowsModel.Apply(battery with { Online = true }, ActivityAt(0));
                Check(e3.Apps[0].EstimatedWatts == 1 && e3.Apps[0].EstimatedMilliAmpHours == 100, "Windows component energy works on AC as battery-equivalent mAh");
                Check(e3.Apps[0].EstimatedWattHours == 1 && e3.Accounting!.MeasuredWatts == null, "E3 Wh does not masquerade as measured AC laptop watts");
                e3 = windowsModel.Apply(battery with { Online = true, Time = time.AddSeconds(10) }, ActivityAt(10));
                Check(e3.Apps[0].EstimatedMilliAmpHours == 100, "Repeated E3 snapshots do not double-count energy");
                e3 = windowsModel.Apply(battery with { Online = false, Time = time.AddSeconds(15) }, ActivityAt(15));
                Check(e3.Apps[0].EstimatedMilliAmpHours == 100, "Switching AC to battery preserves captured mAh without recounting");
                e3 = windowsModel.Apply(battery with { Online = true, Time = time.AddSeconds(130) }, ActivityAt(130));
                Check(e3.Apps[0].EstimatedMilliAmpHours == 100 && e3.Apps[0].EstimatedWattHours == 1 && e3.Apps[0].EstimatedWatts == null,
                    "Reporting gaps retain AC captured mAh and Wh without stale watts");
                e3 = windowsModel.Apply(battery with { Online = false, Time = time.AddSeconds(140) }, ActivityAt(140));
                Check(e3.Apps[0].EstimatedMilliAmpHours == 100, "Reporting gaps also retain battery captured mAh");
                var many = ActivityAt(0) with { Apps = [fixtureApp with { Processes = [fixtureProcess, fixtureProcess with { Pid = 124 }] }] };
                e3 = windowsModel.Apply(battery, many);
                Check(e3.Apps[0].Processes.All(p => p.EstimatedWatts == null && p.EstimatedMilliAmpHours == null), "Application-only E3 energy is not invented for individual PIDs");
            }
            using (var missingVoltage = new WindowsEnergyReader(feedPath))
            {
                var e3 = missingVoltage.Apply(battery with { Voltage = null }, ActivityAt(0));
                Check(e3.Apps[0].EstimatedWattHours == 1 && e3.Apps[0].EstimatedMilliAmpHours == null, "Missing voltage preserves Wh but leaves equivalent mAh unknown");
                e3 = missingVoltage.Apply(battery with { Online = true }, ActivityAt(0));
                Check(e3.Apps[0].EstimatedMilliAmpHours == 100 && e3.Apps[0].EstimatedWattHours == 1,
                    "Delayed voltage fills equivalent mAh once without recounting Wh");
                e3 = missingVoltage.Apply(battery with { Online = false }, ActivityAt(0));
                Check(e3.Apps[0].EstimatedMilliAmpHours == 100, "Delayed charge conversion never double-counts across power modes");
            }
            var protectedNames = new[] { "MsMpEng", "csrss", "dwm", "System", CpuCoverage.UnattributedName };
            var protectedIds = new[] { @"C:\Defender\MsMpEng.exe", @"C:\Windows\System32\csrss.exe", @"C:\Windows\System32\dwm.exe", "System", "System Interrupts" };
            var protectedApps = protectedNames.Select((name, i) => fixtureApp with
            {
                Name = name, Path = null, Pids = i == 4 ? [] : [i == 3 ? 4 : 10000 + i],
                Processes = i == 4 ? [] : [fixtureProcess with { Pid = i == 3 ? 4 : 10000 + i, Name = name, Path = null }]
            }).ToArray();
            Check(WindowsEnergyReader.FallbackMatches(protectedIds[0], protectedApps[0], protectedApps, protectedIds), "Protected executable exact unique name can match E3");
            Check(!WindowsEnergyReader.FallbackMatches(protectedIds[0], protectedApps[0], protectedApps, protectedIds.Concat([@"D:\other\MsMpEng.exe"])), "Ambiguous E3 executable paths cannot match by name");
            Check(!WindowsEnergyReader.FallbackMatches(protectedIds[0], protectedApps[0], protectedApps.Concat([protectedApps[0] with { Path = @"D:\other\MsMpEng.exe" }]), protectedIds), "Ambiguous running app names cannot share energy records");
            Check(!WindowsEnergyReader.FallbackMatches("System", protectedApps[3] with { Pids = [123] }, protectedApps, protectedIds), "Kernel System matching requires PID 4");
            File.WriteAllText(feedPath, JsonSerializer.Serialize(new WindowsEnergyFeed(time, time, "fixture", protectedIds.Select((id, i) => new WindowsEnergyRecord(id, time, 3600, 3600000, 3600000, 0, 0, 0, 0, 0, i + 1)).ToArray())));
            using (var protectedReader = new WindowsEnergyReader(feedPath))
            {
                var result = protectedReader.Apply(battery with { Online = true }, ActivityAt(0) with { Apps = protectedApps });
                Check(result.Apps.All(a => a.EstimatedMilliAmpHours == 100), "Defender csrss dwm kernel and interrupt group receive AC mAh");
                Check(result.Apps[4].EnergySource.Contains("Partial coverage") && result.Apps[0].EnergySource.Contains("identity unverified"), "Partial interrupt coverage and name-based estimates are labelled");
                result = protectedReader.Apply(battery with { Online = false }, ActivityAt(0) with { Apps = protectedApps });
                Check(result.Apps.All(a => a.EstimatedMilliAmpHours == 100), "Protected process mAh works on battery without duplicate energy");
            }
            File.Delete(feedPath);
            File.Delete(feedPath + ".stop");
            Check(Math.Abs(allocated.Accounting!.AppWatts!.Value - 2.8) < 0.000001, "Transparent app allocation follows documented activity budget");
            Check(Math.Abs(allocated.Accounting.AppWatts.Value + allocated.Accounting.SystemWatts!.Value - 20) < 0.000001, "App and unattributed watts balance measured battery watts");
            allocated = allocator.Allocate(battery with
            {
                Time = time.AddSeconds(10)
            }, ActivityAt(10), 10, 30);
            var account = allocated.Accounting!;
            Check(Math.Abs(account.AppMilliAmpHours!.Value + account.SystemMilliAmpHours!.Value - account.ObservedMilliAmpHours!.Value) < 0.000001, "Integrated app and system charge balance observed mAh");
            Check(Math.Abs(allocated.Apps[0].EstimatedMilliAmpHours!.Value - 280d / 360) < 0.000001, "Per-app mAh integrates power divided by voltage");
            Check(allocated.Apps[0].Processes[0].EstimatedMilliAmpHours == allocated.Apps[0].EstimatedMilliAmpHours, "Single PID charge matches its app allocation");
            var covered = account.CoveredSeconds;
            allocated = allocator.Allocate(battery with
            {
                Time = time.AddSeconds(1000)
            }, ActivityAt(1000), 10, 30);
            Check(allocated.Accounting!.CoveredSeconds == covered, "Attribution skips sleep gaps");
            allocator.BreakInterval();
            allocated = allocator.Allocate(battery with
            {
                Time = time.AddSeconds(1010)
            }, ActivityAt(1010), 10, 30);
            Check(allocated.Accounting!.CoveredSeconds == covered, "Attribution skips paused intervals");
            allocated = allocator.Allocate(battery with
            {
                Online = true,
                ChargeWatts = 60
            }, ActivityAt(0), 10, 30);
            Check(allocated.Accounting!.MeasuredWatts == null && allocated.Apps[0].EstimatedWatts == null, "AC charging watts are never allocated to apps");
            allocated = allocator.Allocate(battery with
            {
                Watts = null
            }, ActivityAt(0), 10, 30);
            Check(allocated.Accounting!.SystemWatts == null, "Missing battery power stays unknown");
            allocated = allocator.Allocate(battery with
            {
                Time = time.AddSeconds(100)
            }, ActivityAt(0), 10, 30);
            Check(allocated.Apps[0].EstimatedWatts == null, "Stale process activity receives no current power estimate");
            var zero = fixtureApp with
            {
                CpuPercent = 0,
                GpuPercent = null,
                IoMiBPerSecond = 0
            };
            allocated = new PowerAttributor().Allocate(battery, ActivityAt(0) with
            {
                Apps = [zero]
            }, 10, 30);
            Check(allocated.Accounting!.AppWatts == 0 && allocated.Accounting.SystemWatts == 20, "Idle activity leaves all measured power unattributed");
            allocated = new PowerAttributor().Allocate(battery, ActivityAt(0) with
            {
                Apps = [fixtureApp with { CpuPercent = 100, GpuPercent = 100 }]
            }, 10, 30);
            Check(allocated.Accounting!.SystemWatts >= 6, "System reserve never falls below 30 percent");
            allocated = new PowerAttributor().Allocate(battery, ActivityAt(0) with
            {
                Apps = [fixtureApp with { CpuPercent = null, GpuPercent = null, IoMiBPerSecond = null }]
            }, 10, 30);
            Check(allocated.Apps[0].EstimatedWatts == null, "Unknown app counters are not represented as measured idle power");
            var roundTrip = JsonSerializer.Deserialize<Sample>(JsonSerializer.Serialize(battery with
            {
                PowerAccounting = account,
                TopProcesses = [new Contributor("fixture", 20, null, 0, "High", "", 1) { EstimatedWatts = 2.8, EstimatedMilliAmpHours = 0.7 }]
            }))!;
            Check(roundTrip.PowerAccounting?.ObservedMilliAmpHours == account.ObservedMilliAmpHours && roundTrip.TopProcesses![0].EstimatedWatts == 2.8, "Power accounting and contributor estimates survive history serialization");
            File.Delete(temp);
            results.Add("ALL TESTS PASSED");
            File.WriteAllLines(output, results);
        }
        catch (Exception e)
        {
            results.Add(e.ToString());
            File.WriteAllLines(output, results);
            Environment.ExitCode = 1;
        }
    }
}
