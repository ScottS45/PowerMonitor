// Purpose: Check hardware counter units, battery-only calibration and calibrated PID energy accounting.
// Author: Scott Sheen (with ChatGpt). Created/Updated: 2026-10-03.
namespace PowerMonitor;
internal static class HardwareEnergyTests
{
    public static void Run(Action<bool, string> check)
    {
        check(Math.Abs(HardwareEnergy.CounterWatts(1_000_000_000_000, 0, 3600)!.Value - 1) < 1e-9, "EMI picowatt-hours convert to watts");
        check(HardwareEnergy.CounterWatts(1, 2, 1) == null && HardwareEnergy.CounterWatts(1, 0, 0) == null, "Counter resets and invalid intervals stay unknown");
        check(HardwareEnergy.ValidPower(double.NaN) == null && HardwareEnergy.ValidPower(-1) == null, "Invalid hardware power is rejected");
        var points = Enumerable.Range(0, 120).Select(i => { var c = (i % 12) / 10d; var g = (i % 7) / 10d; return new CalibrationPoint(c, g, 8 + c * 30 + g * 45, 5); }).ToArray();
        var model = EnergyCalibration.Fit(points, "test");
        check(model != null && model.ValidationMae < 0.1 && Math.Abs(model.Baseline - 8) < 0.2 && Math.Abs(model.Cpu - 30) < 0.2 && Math.Abs(model.Gpu - 45) < 0.2, "Calibration recovers independent CPU/GPU loads with held-out validation");
        check(EnergyCalibration.Fit(points.Take(30).ToArray(), "test") == null, "Short calibration cannot claim accuracy");
        check(EnergyCalibration.Fit(Enumerable.Repeat(new CalibrationPoint(0.1, 0, 12, 5), 120).ToArray(), "test") == null, "Unvaried CPU activity cannot calibrate");
        check(EnergyCalibration.Fit(points.Select(p => p with { Gpu = p.Cpu }).ToArray(), "test") == null, "Correlated CPU/GPU activity cannot claim separate component accuracy");
        var time = DateTimeOffset.UtcNow;
        Sample SampleAt(int n, bool ac) => new(time.AddSeconds(n * 5), ac, 80, ac ? null : 20, null, 80, 10, 20, null, "unknown", null, 60, 0, "", "", false, "", 0, 0, n, 0, "") { Discharging = !ac };
        var p = new ProcessActivity(1, "app", "app.exe", time, 20, 0, 1, []);
        ProcessSnapshot SnapshotAt(int n) => new(time.AddSeconds(n * 5), [new AppActivity("app", "app.exe", [1], 20, null, 0, 1, "unknown", "", "", [p])], [], "", 0, 0, []);
        var calibration = new EnergyCalibration();
        for (int i = 0; i < 80; i++) calibration.Observe(SampleAt(i, true), SnapshotAt(i), 5, 5);
        check(calibration.Profile == null, "AC charging never trains the battery model");
        var alignedCalibration = new EnergyCalibration();
        alignedCalibration.Observe(SampleAt(0, false), SnapshotAt(0) with { Time = time.AddMilliseconds(20) }, 5, 5);
        alignedCalibration.Observe(SampleAt(1, false), SnapshotAt(1) with { Time = time.AddSeconds(5).AddMilliseconds(20) }, 5, 5);
        check(alignedCalibration.Status.Contains("1/60+ samples"), "Fresh CPU collection after battery sampling still calibrates");
        var idle = SampleAt(0, false) with { CpuPercent = 1, CpuWatts = 10 };
        var busy = SampleAt(1, false) with { CpuWatts = 30, Voltage = 10 };
        var allocation = new ComponentAttributor();
        allocation.Apply(idle, SnapshotAt(0), calibration, 5, 5);
        var a = allocation.Apply(busy, SnapshotAt(1), calibration, 5, 5);
        check(Math.Abs(a.Apps[0].EstimatedWatts!.Value - 20) < 1e-9, "CPU idle package power stays outside app allocation");
        var b = allocation.Apply(busy with { Time = time.AddSeconds(10), AmdWatts = 50 }, SnapshotAt(2), calibration, 5, 5);
        check(Math.Abs(b.Apps[0].EstimatedWatts!.Value - 20) < 1e-9 && Math.Abs(b.Apps[0].EstimatedMilliAmpHours!.Value - 4.1666666667) < 1e-6, "APU GPU power is not double counted and mAh uses measured voltage");
        check(b.Accounting!.SystemWatts == 0 && b.Accounting.AppWatts == 20, "Same-time battery residual reconciles allocated power");
        var noIdentity = allocation.Apply(busy with { Time = time.AddSeconds(15) }, SnapshotAt(3) with { Apps = [SnapshotAt(3).Apps[0] with { Processes = [p with { Started = null }] }] }, calibration, 5, 5);
        check(noIdentity.Apps[0].Processes[0].EstimatedMilliAmpHours == null, "Unknown PID creation time cannot inherit charge");
        var gpuOnly = new ComponentAttributor();
        var upstream = SnapshotAt(0) with { GpuPercent = 0.1 };
        var gpuSample = SampleAt(0, true) with { NvidiaWatts = 10 };
        var unknownCpu = gpuOnly.Apply(gpuSample, upstream, calibration, 5, 5);
        check(unknownCpu.Apps[0].EstimatedWatts == null && unknownCpu.Apps[0].EstimatedMilliAmpHours == null,
            "GPU-only hardware cannot report zero total energy for a CPU-active app");
        var e3 = upstream with { Apps = [upstream.Apps[0] with { EstimatedWatts = 2, EstimatedWattHours = 0.2, EstimatedMilliAmpHours = 20, EnergySource = "Windows E3 component model", Processes = [p with { EstimatedWatts = 2, EstimatedMilliAmpHours = 20 }] }] };
        var retained = gpuOnly.Apply(gpuSample, e3, calibration, 5, 5);
        check(retained.Apps[0].EstimatedWatts == 2 && retained.Apps[0].EstimatedMilliAmpHours == 20 && retained.Apps[0].EstimatedWattHours == 0.2 && retained.Apps[0].Processes[0].EstimatedMilliAmpHours == 20,
            "GPU-only hardware preserves E3 app and PID energy without double counting");
        var legacy = e3 with { Apps = [e3.Apps[0] with { EnergySource = "Legacy activity model (uncalibrated)" }] };
        var preservedLegacy = gpuOnly.Apply(gpuSample with { Online = false }, legacy, calibration, 5, 5);
        check(preservedLegacy.Apps[0].EstimatedMilliAmpHours == 20 && preservedLegacy.Apps[0].EstimatedWatts == 2,
            "GPU-only hardware preserves the battery activity estimate");
        allocation.BreakInterval();
        var afterGap = allocation.Apply(busy with { Time = time.AddSeconds(100) }, SnapshotAt(20), calibration, 5, 5);
        check(afterGap.Apps[0].EstimatedMilliAmpHours == noIdentity.Apps[0].EstimatedMilliAmpHours, "Pause and long gaps do not fabricate app charge");
    }
}