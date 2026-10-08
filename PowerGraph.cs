// ============================================================================
// File: PowerGraph.cs
// Author: Scott Sheen (with ChatGpt)
// Created: 2026-10-02
// Updated: 2026-10-02
// Purpose: Render activity and measured power without filling unknown readings.
// ============================================================================

using System.Drawing.Drawing2D;

namespace PowerMonitor;

public enum GraphMode
{
    Automatic,
    Activity,
    Battery,
    CpuPower
}

public sealed class PowerGraph : Control
{

    public IReadOnlyList<Sample> Samples { get; set; } = [];

    public TimeSpan Range { get; set; } = TimeSpan.FromMinutes(5);

    public GraphMode Mode { get; set; } = GraphMode.Automatic;

    public bool ShowCpu { get; set; }

    public bool ShowNvidia { get; set; }

    public event Action<Sample>? PointSelected;

    private List<Sample> _visible = [];

    public PowerGraph()
    {
        DoubleBuffered = true;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Dock = DockStyle.Fill;
        MinimumSize = new(200, 150);
        Font = new Font("Segoe UI", 9);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Drawing uses cached samples only; painting must not query hardware or processes.
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var end = Samples.LastOrDefault()?.Time ?? DateTimeOffset.UtcNow;
        _visible = Samples.Where(s => s.Time >= end - Range).ToList();
        var mode = Mode == GraphMode.Automatic ? (Samples.LastOrDefault()?.Online == false ? GraphMode.Battery : GraphMode.Activity) : Mode;
        var plot = new Rectangle(54, 46, Math.Max(1, Width - 78), Math.Max(1, Height - 83));
        var activity = mode == GraphMode.Activity;
        var primary = mode switch
        {
            GraphMode.Activity => (Func<Sample, double?>)(s => s.CpuPercent),
            GraphMode.CpuPower => s => s.CpuWatts,
            _ => s => s.Watts
        };
        var max = activity ? 100 : Math.Max(25, _visible.Select(s => primary(s) ?? 0).DefaultIfEmpty().Max() * 1.15);
        using var grid = new Pen(Theme.Border);
        using var muted = new SolidBrush(Theme.Muted);
        var title = mode switch
        {
            GraphMode.Activity => "CPU activity   /   GPU busiest engine",
            GraphMode.CpuPower => "CPU package power (sensor)",
            _ => "Measured battery discharge"
        };
        if (activity)
        {
            using var cpuBrush = new SolidBrush(Theme.Accent);
            using var gpuBrush = new SolidBrush(Theme.Purple);
            e.Graphics.DrawString("CPU activity", Font, cpuBrush, plot.Left, 12);
            e.Graphics.DrawString("GPU busiest engine", Font, gpuBrush, plot.Left + 100, 12);
        }
        else
            e.Graphics.DrawString(title, Font, muted, plot.Left, 12);
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Bottom - i * plot.Height / 4;
            e.Graphics.DrawLine(grid, plot.Left, y, plot.Right, y);
            e.Graphics.DrawString($"{max * i / 4:F0}{(activity ? "%" : " W")}", Font, muted, 4, y - 8);
        }

        DrawSeries(e.Graphics, plot, end, max, Theme.Accent, primary, false);
        if (activity)
            DrawSeries(e.Graphics, plot, end, max, Theme.Purple, s => s.GpuPercent, true);
        if (mode == GraphMode.Battery && ShowCpu)
            DrawSeries(e.Graphics, plot, end, max, Theme.Orange, s => s.CpuWatts, false);
        e.Graphics.DrawString($"{(end - Range).ToLocalTime():HH:mm}", Font, muted, plot.Left, plot.Bottom + 14);
        var endText = end.ToLocalTime().ToString("HH:mm");
        var size = e.Graphics.MeasureString(endText, Font);
        e.Graphics.DrawString(endText, Font, muted, plot.Right - size.Width, plot.Bottom + 14);
        if (!_visible.Any(s => primary(s).HasValue))
            e.Graphics.DrawString(activity ? "Activity starts after the first sampling interval." : mode == GraphMode.Battery ? "Battery discharge is not whole-system AC power. Switch to Activity while plugged in." : "CPU package sensor unavailable.", Font, muted, new RectangleF(plot.Left, plot.Top + 15, plot.Width, 70));
    }

    private void DrawSeries(Graphics graphics, Rectangle plot, DateTimeOffset end, double max, Color color, Func<Sample, double?> value, bool activityTime)
    {
        using var pen = new Pen(color, 2);
        PointF? prior = null;
        DateTimeOffset? previous = null;
        foreach (var sample in _visible)
        {
            var number = value(sample);
            if (activityTime && (sample.ActivityTime == null || (sample.Time - sample.ActivityTime.Value).TotalSeconds > 30))
                number = null;
            if (!number.HasValue)
            {
                prior = null;
                previous = null;
                continue;
            }

            var point = new PointF(plot.Right - (float)((end - sample.Time).TotalSeconds / Math.Max(1, Range.TotalSeconds) * plot.Width), plot.Bottom - (float)(Math.Clamp(number.Value / max, 0, 1) * plot.Height));
            if (prior.HasValue && previous.HasValue && (sample.Time - previous.Value).TotalSeconds <= 65)
                graphics.DrawLine(pen, prior.Value, point);
            prior = point;
            previous = sample.Time;
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_visible.Count == 0)
            return;
        var end = _visible[^1].Time;
        var fraction = Math.Clamp((double)(e.X - 54) / Math.Max(1, Width - 78), 0, 1);
        var target = end - Range + TimeSpan.FromSeconds(Range.TotalSeconds * fraction);
        PointSelected?.Invoke(_visible.MinBy(s => Math.Abs((s.Time - target).TotalSeconds))!);
    }
}
