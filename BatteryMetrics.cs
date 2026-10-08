// ============================================================================
// File: BatteryMetrics.cs
// Author: Scott Sheen (with ChatGpt)
// Created: 2026-10-02
// Updated: 2026-10-03
// Purpose: Battery current estimates, observed charge totals, and charging state.
// ============================================================================

namespace PowerMonitor;

public static class BatteryMetrics
{
    public static double? ToMilliAmps(double? value, double? voltage)
    {
        // W / V gives A; Wh / V gives Ah. Both conversions require a valid voltage.
        if (!value.HasValue || !voltage.HasValue || !double.IsFinite(value.Value) ||
            !double.IsFinite(voltage.Value) || value < 0 || voltage <= 0)
            return null;

        return value.Value / voltage.Value * 1000;
    }

    public static string ChargingStatus(Sample sample)
    {
        // Firmware flags take precedence over assuming every AC connection is charging.
        if (sample.Charging == true && sample.Discharging == true)
            return "Mixed / conflicting battery states";
        if (sample.Charging == true || sample.ChargeWatts > 0)
            return "Charging";
        if (sample.Discharging == true || sample.Watts > 0)
            return "Discharging";
        if (sample.Online == true && sample.Charging == false)
            return sample.Percent >= 99 ? "Fully charged / on AC" : "On AC / not charging";

        return "Charging state unavailable";
    }
}

public sealed class BatteryUsageTracker
{
    private Sample? _previous;
    private double _used, _added, _usedWh, _addedWh, _observedSeconds;
    private bool _hasDischarge, _hasCharge;

    public void BreakInterval() => _previous = null;

    public Sample Observe(Sample sample, int intervalSeconds)
    {
        if (_previous != null)
        {
            var seconds = (sample.Time - _previous.Time).TotalSeconds;

            // Integrate only contiguous observations in the same power/charging state.
            // Gaps, missing voltage, sleep, pause, and source changes are never filled in.
            if (seconds > 0 && seconds <= intervalSeconds * 2.5 &&
                sample.Online == _previous.Online && sample.Charging == _previous.Charging &&
                sample.Discharging == _previous.Discharging)
            {
                var used = Integrate(_previous.DischargeMilliAmps, sample.DischargeMilliAmps, seconds);
                var added = Integrate(_previous.ChargeMilliAmps, sample.ChargeMilliAmps, seconds);
                if (used.HasValue)
                {
                    _used += used.Value;
                    _usedWh += Integrate(_previous.Watts, sample.Watts, seconds) ?? 0;
                    _hasDischarge = true;
                }
                if (added.HasValue)
                {
                    _added += added.Value;
                    _addedWh += Integrate(_previous.ChargeWatts, sample.ChargeWatts, seconds) ?? 0;
                    _hasCharge = true;
                }
                if (used.HasValue || added.HasValue)
                    _observedSeconds += seconds;
            }
        }

        _previous = sample;
        return sample with
        {
            UsedMilliAmpHours = _hasDischarge ? _used : null,
            AddedMilliAmpHours = _hasCharge ? _added : null,
            UsedWattHours = _hasDischarge ? _usedWh : null,
            AddedWattHours = _hasCharge ? _addedWh : null,
            BatteryObservedSeconds = _observedSeconds
        };
    }

    private static double? Integrate(double? before, double? after, double seconds) =>
        before.HasValue && after.HasValue ? (before.Value + after.Value) / 2 * seconds / 3600 : null;
}
