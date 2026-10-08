// ============================================================================
// File: AlertEngine.cs
// Author: Scott Sheen (with ChatGpt)
// Created: 2026-10-02
// Updated: 2026-10-02
// Purpose: Sustained discharge thresholds, hysteresis, and notification cooldowns.
// ============================================================================

namespace PowerMonitor;

public sealed class AlertEngine
{

    private readonly Dictionary<int, DateTimeOffset> _since = [];

    private int _active = -1;

    private DateTimeOffset? _clearing, _batterySince, _previous;

    private DateTimeOffset _lastNotification = DateTimeOffset.MinValue;

    public DateTimeOffset SuppressedUntil { get; set; }

    public string State => _active < 0 ? "Normal" : _active.ToString();

    public AlertEvent? Evaluate(Sample sample, MonitorSettings settings, bool excludedApplication)
    {
        // Require sustained thresholds and a lower clear boundary to avoid noisy alert oscillation.
        var now = sample.Time;
        if (_previous.HasValue && (now - _previous.Value).TotalSeconds > settings.SampleSeconds * 2.5)
            Reset();
        _previous = now;
        if (sample.Online == false)
            _batterySince ??= now;
        else
            _batterySince = null;
        if (!sample.Watts.HasValue || (settings.BatteryOnlyAlerts && sample.Online != false))
        {
            Reset();
            return null;
        }

        if (_batterySince.HasValue && (now - _batterySince.Value).TotalSeconds < settings.UnplugGraceSeconds)
        {
            Reset();
            return null;
        }

        // Track how long each threshold stays exceeded; isolated spikes do not qualify.
        var watts = sample.Watts.Value;
        for (var i = 0; i < settings.Rules.Length; i++)
        {
            if (watts > settings.Rules[i].Watts)
                _since.TryAdd(i, now);
            else
                _since.Remove(i);
        }

        // Choose the highest threshold whose duration has completed.
        var qualified = -1;
        foreach (var pair in _since)
            if ((now - pair.Value).TotalSeconds >= settings.Rules[pair.Key].Seconds)
                qualified = Math.Max(qualified, pair.Key);
        var escalated = qualified > _active;
        if (escalated)
        {
            _active = qualified;
            _clearing = null;
        }

        // A lower clear threshold and sustained recovery prevent repeated on/off alerts.
        if (_active >= 0 && watts < settings.Rules[_active].ClearWatts)
        {
            _clearing ??= now;
            if ((now - _clearing.Value).TotalSeconds >= settings.ClearSeconds)
            {
                _active = qualified;
                _clearing = null;
            }
        }
        else
            _clearing = null;

        // Apply notification policy after updating state; pausing alerts does not pause sampling.
        if (_active < 0 || !settings.NotificationsEnabled || excludedApplication || now < SuppressedUntil)
            return null;
        if (!escalated && (now - _lastNotification).TotalSeconds < settings.RepeatSeconds)
            return null;
        _lastNotification = now;
        var details = $"Measured battery discharge: {watts:F1} W. CPU: {sample.CpuPercent:F1}% / {sample.CpuWatts?.ToString("F1") ?? "unavailable"} W. NVIDIA: {sample.NvidiaState}. Display: {sample.DisplaySummary}.";
        if (sample.CpuWatts > 8 && sample.CpuPercent < 10)
            details += " High CPU package power with low utilization: investigate idle states, drivers and power profile.";
        if (sample.RefreshHz > 60)
            details += " High display refresh may contribute.";
        if (sample.ExternalDisplays > 0)
            details += " External displays may require the dGPU; awake does not imply a fault.";
        return new(now, settings.Rules[_active].Name, watts, details);
    }

    public void Reset()
    {
        _since.Clear();
        _active = -1;
        _clearing = null;
    }
}
