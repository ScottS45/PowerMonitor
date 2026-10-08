// ============================================================================
// File: ProcessesView.cs
// Author: Scott Sheen (with ChatGpt)
// Created: 2026-10-02
// Updated: 2026-10-03
// Purpose: Searchable app activity and explanatory process details.
// ============================================================================

using System.Diagnostics;

namespace PowerMonitor;

public sealed class ProcessesView : UserControl
{

    private readonly DataGridView _grid = Theme.Grid();

    private readonly TextBox _details = Theme.TextBox();

    private readonly TextBox _search = new()
    {
        Width = 240,
        BackColor = Theme.SurfaceAlt,
        ForeColor = Theme.Text,
        BorderStyle = BorderStyle.FixedSingle
    };

    private readonly ChoicePicker _sort = new()
    {
        Width = 140
    };

    private readonly Label _powerSummary = Theme.Label("Power accounting: waiting for battery discharge...", 10, false, Theme.Accent);

    private readonly Label _status = Theme.Label("Preparing process activity...", 9, false, Theme.Muted);

    private readonly ActivityListOrder _order = new();
    private bool _syncSort;
    private static readonly ActivityColumn[] GridColumns = [ActivityColumn.App, ActivityColumn.Watts, ActivityColumn.Mah, ActivityColumn.Count, ActivityColumn.Cpu, ActivityColumn.Gpu, ActivityColumn.Io, ActivityColumn.Impact, ActivityColumn.Adapter];
    private static readonly ActivityColumn[] PickerColumns = [ActivityColumn.Cpu, ActivityColumn.Gpu, ActivityColumn.Io, ActivityColumn.Watts, ActivityColumn.Mah, ActivityColumn.Impact, ActivityColumn.App, ActivityColumn.Count, ActivityColumn.Adapter];
    private ProcessSnapshot? _snapshot;

    private AppActivity? _selected;

    public event Action<AppActivity>? ExcludeRequested;

    public ProcessesView()
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Background;
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(0, 4, 0, 0)
        };
        var searchLabel = Theme.Label("Find an app", 10);
        searchLabel.Size = new(95, 30);
        _sort.Items.AddRange(["CPU", "GPU", "I/O", "Est. watts", "Est. mAh", "Impact", "App", "Count", "GPU adapter"]);
        _sort.SelectedIndex = 0;
        toolbar.Controls.AddRange([searchLabel, _search, _sort]);
        _search.TextChanged += (_, _) => RefreshRows();
        _sort.SelectedIndexChanged += (_, _) => { if (!_syncSort) { _order.Select(PickerColumns[_sort.SelectedIndex]); RefreshRows(true); } };
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Background,
            SplitterWidth = 14
        };
        split.Resize += (_, _) =>
        {
            if (split.Width > 400)
                split.SplitterDistance = split.Width * 64 / 100;
        };
        var left = new SurfacePanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0)
        };
        left.Controls.Add(_grid);
        split.Panel1.Controls.Add(left);
        var right = new SurfacePanel
        {
            Dock = DockStyle.Fill
        };
        var title = Theme.Label("Why this app?", 14, true);
        title.Dock = DockStyle.Top;
        title.Height = 40;
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 90
        };
        actions.Controls.Add(Theme.Button("Open file location", OpenLocation));
        actions.Controls.Add(Theme.Button("Exclude from drain alerts", () =>
        {
            if (_selected?.Processes.Length > 0)
                ExcludeRequested?.Invoke(_selected);
        }));
        right.Controls.Add(_details);
        right.Controls.Add(actions);
        right.Controls.Add(title);
        split.Panel2.Controls.Add(right);
        _status.Dock = DockStyle.Bottom;
        _status.Height = 90;
        Controls.Add(split);
        Controls.Add(_status);
        _powerSummary.Dock = DockStyle.Top;
        _powerSummary.Height = 66;
        Controls.Add(_powerSummary);
        Controls.Add(toolbar);
        AddColumn("App", 26);
        AddColumn("Est. W", 13);
        AddColumn("Est. mAh", 15);
        AddColumn("Count", 10);
        AddColumn("CPU", 11);
        AddColumn("GPU", 11);
        AddColumn("I/O MiB/s", 13);
        AddColumn("Impact", 13);
        AddColumn("GPU adapter", 25);
        var minimums = new[]
        {
            140,
            82,
            92,
            56,
            72,
            72,
            90,
            90,
            150
        };
        for (var i = 0; i < minimums.Length; i++)
            _grid.Columns[i].MinimumWidth = minimums[i];
        _order.ApplyGlyphs(_grid, GridColumns);
        _grid.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.ColumnIndex < 0) return;
            _order.Click(GridColumns[e.ColumnIndex]);
            _syncSort = true;
            _sort.SelectedIndex = Array.IndexOf(PickerColumns, _order.Column);
            _syncSort = false;
            RefreshRows(true);
        };
        _grid.CellClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].Tag is AppActivity app && app.Name == CpuCoverage.UnattributedName)
            { _order.SystemExpanded = !_order.SystemExpanded; RefreshRows(); }
        };
        _grid.SelectionChanged += (_, _) => ShowSelection();
        _grid.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex == 7 && e.Value is string value)
                e.CellStyle!.ForeColor = value.Contains("high", StringComparison.OrdinalIgnoreCase) ? Theme.Orange : value == "Medium" ? Theme.Blue : Theme.Muted;
        };
        Theme.Apply(this);
    }

    private void AddColumn(string name, int weight) => _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, FillWeight = weight, SortMode = DataGridViewColumnSortMode.Programmatic });

    public void UpdateSnapshot(ProcessSnapshot snapshot)
    {
        var power = snapshot.Accounting;
        _powerSummary.Text = power == null ? "Power accounting unavailable" :
            $"Measured battery: {Rate(power.MeasuredWatts, " W")}  |  Estimated apps: {Rate(power.AppWatts, " W")}  |  System / unattributed: {Rate(power.SystemWatts, " W")}\r\nObserved mAh: {Rate(power.ObservedMilliAmpHours)} = apps {Rate(power.AppMilliAmpHours)} + system {Rate(power.SystemMilliAmpHours)}  |  {power.CoveredSeconds / 60:F1} min covered\r\n{power.Status}";
        _snapshot = snapshot;
        RefreshRows();
    }

    private static string Rate(double? value, string unit = "") => value.HasValue ? $"{value:F1}{unit}" : "--";

    private void RefreshRows(bool resetScroll = false)
    {
        if (_snapshot == null)
            return;
        var identity = _selected?.Path ?? _selected?.Name;
        var filter = _search.Text.Trim();
        var apps = _snapshot.Apps.Where(a => a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || (a.Path?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        _order.ApplyGlyphs(_grid, GridColumns);
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        DataGridViewRow? selected = null;
        foreach (var item in _order.Rows(_snapshot, apps))
        {
            var app = item.App;
            var i = _grid.Rows.Add(item.DisplayName, Rate(app.EstimatedWatts), app.EstimatedMilliAmpHours.HasValue ? app.EstimatedMilliAmpHours.Value.ToString("F3") : "--", app.Pids.Length, Rate(app.CpuPercent, "%"), Rate(app.GpuPercent, "%"), Rate(app.IoMiBPerSecond), app.Impact, app.Gpu);
            var row = _grid.Rows[i];
            row.Tag = app;
            row.Cells[2].ToolTipText = "Estimated captured mAh on battery or AC (battery-equivalent on AC). " + app.EnergySource;
            if (item.Child) row.DefaultCellStyle.ForeColor = Theme.Muted;
            if ((app.Path ?? app.Name) == identity)
                selected = row;
        }

        _grid.ClearSelection();
        if (selected == null && _grid.Rows.Count > 0)
            selected = _grid.Rows[0];
        if (selected != null)
        {
            selected.Selected = true;
            _grid.CurrentCell = selected.Cells[0];
        }

        if ((resetScroll || _order.Column == ActivityColumn.Cpu && _order.Descending) && _grid.Rows.Count > 0)
            _grid.FirstDisplayedScrollingRowIndex = 0;
        _grid.ResumeLayout();
        ShowSelection();
        _status.Text = $"Updated {_snapshot.Time.ToLocalTime():HH:mm:ss}  |  {_snapshot.Apps.Length} app groups  |  {_snapshot.AccessDenied} restricted paths/handles  |  {_snapshot.CollectionMs:F0} ms\r\n{_snapshot.CpuCoverageStatus}\r\n{_snapshot.Status}. I/O includes disk, network and device traffic; activity is not app watts.";
    }

    internal void VerifyInteractions(Action<string> capture)
    {
        ActivityUiSmoke.Header(_grid, 4);
        if (_order.Descending || !_grid.Columns[4].HeaderText.EndsWith("\u2191")) throw new Exception("Processes ascending header click failed");
        capture("Processes-CPU-ascending");
        ActivityUiSmoke.Header(_grid, 4);
        RefreshRows();
        if (!_order.Descending || !_grid.Columns[4].HeaderText.EndsWith("\u2193")) throw new Exception("Processes descending sort did not persist");
        capture("Processes-CPU-descending");
        ActivityUiSmoke.Header(_grid, 2);
        capture("Processes-mAh-descending");
        ActivityUiSmoke.Header(_grid, 4);
        var system = ActivityUiSmoke.SystemRow(_grid);
        if (system >= 0)
        {
            ActivityUiSmoke.Cell(_grid, system);
            RefreshRows();
            if (!_order.SystemExpanded || _grid.Rows.Count <= _snapshot!.Apps.Length) throw new Exception("Processes system expansion failed");
            capture("Processes-system-expanded");
            ActivityUiSmoke.Cell(_grid, ActivityUiSmoke.SystemRow(_grid));
            if (_order.SystemExpanded) throw new Exception("Processes system collapse failed");
        }
    }

    private void ShowSelection()
    {
        _selected = _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Tag as AppActivity : null;
        if (_selected == null)
        {
            _details.Text = "Select an app to see its activity, GPU mapping and process details.";
            return;
        }

        var app = _selected;
        if (app.Processes.Length == 0)
        {
            _details.Text = $"{app.Name}\r\nCPU: {Rate(app.CpuPercent, "%")}\r\n\r\n{app.Reason}\r\n\r\n{app.EnergySource}\r\n\r\nClick the parent system row to expand/collapse. Child rows are part of its subtotal; do not add them to the parent again.";
            return;
        }
        var text = $"{app.Name}\r\n{app.Impact} activity\r\nEstimated power: {Rate(app.EstimatedWatts, " W")}\r\nEstimated captured energy: {Rate(app.EstimatedWattHours, " Wh")} / {Rate(app.EstimatedMilliAmpHours, " mAh")}\r\n\r\n{app.EnergySource}\r\nWindows energy totals cover the current capture session. On AC, mAh is battery-equivalent, not battery discharge.\r\nApp energy cannot always be split reliably between PIDs. Legacy values cover observed battery intervals only.\r\n\r\n{app.Reason}\r\n\r\nExecutable\r\n{app.Path ?? "Unavailable / access restricted"}\r\n\r\nSum of working sets: {Rate(app.MemoryMb)} MB (shared pages may count twice)\r\n\r\nProcesses\r\n";
        foreach (var p in app.Processes)
            text += $"PID {p.Pid}  Est. {Rate(p.EstimatedWatts, " W")} / {Rate(p.EstimatedMilliAmpHours, " mAh")}  CPU {Rate(p.CpuPercent, "%")}  I/O {Rate(p.IoMiBPerSecond)} MiB/s\r\n";

        text += "\r\nGPU engines (Windows activity)\r\n";
        var engines = app.Processes.SelectMany(p => p.Engines).Where(e => e.Usage > 0.05).OrderByDescending(e => e.Usage).Take(15).ToArray();
        if (engines.Length == 0)
            text += "No active engines reported, or GPU counters unavailable. This does not prove the GPU is asleep.\r\n";
        foreach (var e in engines)
            text += $"PID {e.Pid} / {e.Adapter}\r\n{e.Engine}: {e.Usage:F1}%\r\nLUID {e.Luid}, physical node {e.PhysicalIndex}{(e.Mapped ? "" : " - mapping unresolved")}\r\n\r\n";
        _details.Text = text;
    }

    private void OpenLocation()
    {
        if (_selected?.Path is not string path || !File.Exists(path))
        {
            MessageBox.Show(this, "This process path is unavailable.", "File location");
            return;
        }

        var info = new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = true
        };
        info.ArgumentList.Add($"/select,{path}");
        Process.Start(info);
    }
}
