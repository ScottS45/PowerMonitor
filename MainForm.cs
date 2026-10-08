// ============================================================================
// File: MainForm.cs
// Author: Scott Sheen (with ChatGpt)
// Created: 2026-10-02
// Updated: 2026-10-03
// Purpose: Desktop pages, tray lifecycle, and user-initiated commands.
// ============================================================================

using System.Text.Json;
using Microsoft.Toolkit.Uwp.Notifications;

namespace PowerMonitor;

public sealed class MainForm : Form
{

    // Own separate icon instances so closing the tray cannot invalidate the window icon.
    private readonly Icon _windowIcon = AppIcons.Load(32);

    private readonly Icon _trayIcon = AppIcons.Load(16);

    private readonly MonitorService _monitor;

    private readonly NotifyIcon _tray;

    private readonly TabControl _tabs = new PageHost()
    {
        Dock = DockStyle.Fill,
        Appearance = TabAppearance.FlatButtons,
        ItemSize = new Size(0, 1),
        SizeMode = TabSizeMode.Fixed
    };

    public TabControl Pages => _tabs;

    private readonly NavigationList _navigation = new()
    {
        Dock = DockStyle.Fill
    };

    private readonly Label _title = Theme.Label("Overview", 23, true), _subtitle = Theme.Label("Your system, at a glance", 10, false, Theme.Muted), _connection = Theme.Label("DETECTING", 10, true, Theme.Accent), _footer = Theme.Label("Monitoring starts shortly", 9, false, Theme.Muted);

    private readonly MetricCard _power = new("System power", Theme.Accent), _cpu = new("CPU activity", Theme.Blue), _gpu = new("GPU activity", Theme.Purple), _batteryCard = new("Battery", Theme.Text);

    private readonly Label _environment = Theme.Label("Detecting displays and power profile", 10, false, Theme.Muted), _activityStatus = Theme.Label("Warming up activity counters...", 9, false, Theme.Muted);

    private readonly TextBox _battery = Theme.TextBox(), _detection = Theme.TextBox(), _historyDetails = Theme.TextBox(), _xmg = Theme.TextBox(), _options = Theme.TextBox();

    private readonly PowerGraph _graph = new(), _historyGraph = new();

    private readonly ProcessesView _processes = new();

    private readonly DataGridView _contributors = Theme.Grid(), _alertGrid = Theme.Grid();
    private readonly ToolTip _buttonTips = new() { InitialDelay = 400, ReshowDelay = 100, AutoPopDelay = 20000, ShowAlways = true };
    private readonly ActivityListOrder _liveOrder = new();
    private static readonly ActivityColumn[] LiveColumns = [ActivityColumn.App, ActivityColumn.Cpu, ActivityColumn.Gpu, ActivityColumn.Impact, ActivityColumn.Mah];

    private readonly List<Sample> _samples = [];

    private readonly ToolStripMenuItem _drawItem = new("Current activity: detecting"), _batteryItem = new("Battery: detecting");

    private Detection? _detected;

    private ProcessSnapshot? _activity;

    private Sample? _latest;

    private bool _exiting, _rebuilding;

    private Button? _pauseButton;

    private TextBox? _excludesEditor;

    private TimeSpan _range = TimeSpan.FromMinutes(5);

    private bool _sessionRange;

    private DateTimeOffset? _sessionStart;

    public MainForm(MonitorSettings settings)
    {
        var appVersion = typeof(MainForm).Assembly.GetName().Version!;
        Text = $"Power Monitor v{appVersion.ToString(appVersion.Build == 0 ? 2 : 3)}";
        Width = 1380;
        Height = 900;
        MinimumSize = new(1050, 720);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = new("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = _windowIcon;
        _monitor = new(settings);
        _tray = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "Power Monitor",
            Visible = true
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Power Monitor", null, (_, _) => SelectPage("Live"));
        menu.Items.Add(_drawItem);
        menu.Items.Add(_batteryItem);
        menu.Items.Add("Processes", null, (_, _) => SelectPage("Processes"));
        menu.Items.Add("Start / pause monitoring", null, (_, _) => ToggleMonitoring());
        menu.Items.Add("Pause alerts for 30 minutes", null, (_, _) => _monitor.PauseAlerts(30));
        menu.Items.Add("Power Options", null, (_, _) => SelectPage("Power Options"));
        menu.Items.Add("Settings", null, (_, _) => SelectPage("Settings"));
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _exiting = true;
            Close();
        });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => SelectPage("Live");
        BuildShell();
        BuildLive();
        var processes = Page("Processes");
        processes.Controls.Add(_processes);
        BuildHistory();
        Page("Battery").Controls.Add(Wrap("Battery health & capacity", _battery));
        BuildAlerts();
        BuildOptions();
        BuildSettings(settings);
        _processes.ExcludeRequested += ExcludeApp;

        // Marshal worker notifications onto the UI thread before touching controls.
        _monitor.Sampled += sample => Post(() => UpdateSample(sample));
        _monitor.ActivitySampled += snapshot => Post(() => UpdateActivity(snapshot));
        _monitor.Detected += detection => Post(() => UpdateDetection(detection));
        _monitor.Alerted += alert => Post(() => ShowAlert(alert));
        ToastNotificationManagerCompat.OnActivated += args => Post(() => SelectPage(args.Argument.Contains("options") ? "Power Options" : "Live"));
        RebuildNavigation();
        Theme.Apply(this);
        Shown += (_, _) =>
        {
            _monitor.Start();
            if (!_monitor.Monitoring)
            {
                _connection.Text = "MONITORING PAUSED";
                _footer.Text = "Monitoring paused\r\nSelect Start to begin";
            }

            if (settings.StartMinimized)
                Hide();
        };

        // Normal close can hide to the tray; explicit Exit proceeds to resource cleanup.
        FormClosing += (_, args) =>
        {
            if (!_exiting && _monitor.Settings.CloseToTray)
            {
                args.Cancel = true;
                Hide();
            }
        };
        FormClosed += (_, _) =>
        {
            _exiting = true;
            _monitor.Dispose();
            _tray.Visible = false;
            _buttonTips.Dispose();
            _tray.Dispose();
            _trayIcon.Dispose();
            _windowIcon.Dispose();
        };
    }

    private void BuildShell()
    {
        var sidebar = new Panel
        {
            Dock = DockStyle.Left,
            Width = 205,
            BackColor = Theme.Sidebar,
            Padding = new Padding(0, 18, 0, 14)
        };
        var brand = Theme.Label("  POWER\r\n  MONITOR", 20, true, Theme.Accent);
        brand.Dock = DockStyle.Top;
        brand.Height = 106;
        _footer.Dock = DockStyle.Bottom;
        _footer.Height = 88;
        _footer.Padding = new Padding(18, 10, 12, 0);
        sidebar.Controls.Add(_navigation);
        sidebar.Controls.Add(_footer);
        sidebar.Controls.Add(brand);
        var content = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(22, 16, 22, 20),
            BackColor = Theme.Background
        };
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 85
        };
        _title.Dock = DockStyle.Top;
        _title.Height = 40;
        _subtitle.Dock = DockStyle.Fill;
        _connection.Dock = DockStyle.Right;
        _connection.Width = 190;
        _connection.TextAlign = ContentAlignment.TopRight;
        _connection.Padding = new Padding(0, 12, 2, 0);
        var headings = new Panel
        {
            Dock = DockStyle.Fill
        };
        headings.Controls.Add(_subtitle);
        headings.Controls.Add(_title);
        header.Controls.Add(headings);
        header.Controls.Add(_connection);
        content.Controls.Add(_tabs);
        content.Controls.Add(header);
        Controls.Add(content);
        Controls.Add(sidebar);
        _navigation.SelectedIndexChanged += (_, _) =>
        {
            if (!_rebuilding && _navigation.SelectedItem is string name)
                SelectPage(name, false);
        };
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            var name = _tabs.SelectedTab?.Text;
            if (name != null)
            {
                _title.Text = name == "Live" ? "Overview" : name;
                _navigation.SelectedItem = name;
                RefreshVisible();
            }
        };
    }

    private TabPage Page(string name)
    {
        var page = new TabPage(name)
        {
            BackColor = Theme.Background,
            ForeColor = Theme.Text,
            Padding = new Padding(0)
        };
        _tabs.TabPages.Add(page);
        return page;
    }

    private SurfacePanel Wrap(string heading, Control control)
    {
        var panel = new SurfacePanel
        {
            Dock = DockStyle.Fill
        };
        var title = Theme.Label(heading, 14, true);
        title.Dock = DockStyle.Top;
        title.Height = 44;
        panel.Controls.Add(control);
        panel.Controls.Add(title);
        return panel;
    }

    private void Post(Action action)
    {
        // Worker events must reach controls on the UI thread; shutdown may already have destroyed the handle.
        if (!_exiting && IsHandleCreated && !IsDisposed)
            try
            {
                BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
            }
    }

    private void RebuildNavigation()
    {
        var selected = _tabs.SelectedTab?.Text ?? "Live";
        _rebuilding = true;
        _navigation.Items.Clear();
        foreach (TabPage page in _tabs.TabPages)
            _navigation.Items.Add(page.Text);
        _navigation.SelectedItem = selected;
        _rebuilding = false;
    }

    public void SelectPage(string name, bool reveal = true)
    {
        if (reveal)
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        foreach (TabPage page in _tabs.TabPages)
            if (page.Text == name)
            {
                _tabs.SelectedTab = page;
                break;
            }

        _title.Text = name == "Live" ? "Overview" : name;
        _subtitle.Text = name switch
        {
            "Processes" => "Find apps keeping your CPU and GPUs busy",
            "History" => "Review measured power and activity over time",
            "Battery" => "Capacity, wear and charge telemetry",
            "Settings" => "Tune monitoring without changing your power settings",
            "XMG" => "Read-only Control Center state",
            _ => _latest?.Online == true ? "AC diagnostics - activity is monitored while plugged in" : "Measured power and likely contributors"
        };
        RefreshVisible();
    }

    private void BuildLive()
    {
        var page = Page("Live");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 146));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 14)
        };
        foreach (var card in new[]
        {
            _power,
            _cpu,
            _gpu,
            _batteryCard
        }

        )
        {
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            cards.Controls.Add(card);
        }

        _batteryCard.Margin = new Padding(0);
        var center = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterWidth = 14,
            BackColor = Theme.Background,
            Margin = new Padding(0)
        };
        center.Resize += (_, _) =>
        {
            if (center.Width > 400)
                center.SplitterDistance = center.Width * (center.Width < 950 ? 55 : 59) / 100;
        };
        var graphPanel = new SurfacePanel
        {
            Dock = DockStyle.Fill
        };
        var graphTitle = Theme.Label("Live activity", 14, true);
        graphTitle.Dock = DockStyle.Top;
        graphTitle.Height = 36;
        var tools = GraphToolbar(_graph, false);
        graphPanel.Controls.Add(_graph);
        graphPanel.Controls.Add(tools);
        graphPanel.Controls.Add(graphTitle);
        center.Panel1.Controls.Add(graphPanel);
        var contributionPanel = new SurfacePanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0)
        };
        var topTitle = Theme.Label("Likely contributors", 14, true);
        topTitle.Dock = DockStyle.Top;
        topTitle.Height = 42;
        topTitle.Padding = new Padding(16, 10, 0, 0);
        _activityStatus.Dock = DockStyle.Bottom;
        _activityStatus.Height = 66;
        _activityStatus.Padding = new Padding(16, 8, 12, 0);
        var inspect = Theme.Button("Inspect all apps", () => SelectPage("Processes"), true);
        inspect.Dock = DockStyle.Bottom;
        inspect.AutoSize = false;
        inspect.Padding = new Padding(12, 0, 12, 0);
        inspect.Height = 32;
        _contributors.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "App", FillWeight = 48 });
        _contributors.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "CPU", FillWeight = 18 });
        _contributors.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "GPU", FillWeight = 18 });
        _contributors.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Impact", FillWeight = 27 });
        _contributors.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Est. mAh", FillWeight = 25, MinimumWidth = 92 });
        _contributors.Columns[4].DisplayIndex = 1;
        _contributors.Resize += (_, _) => _contributors.Columns[3].Visible = _contributors.ClientSize.Width >= 420;
        _contributors.Columns[0].MinimumWidth = 100;
        _contributors.Columns[1].MinimumWidth = 64;
        _contributors.Columns[2].MinimumWidth = 64;
        _contributors.Columns[3].MinimumWidth = 80;
        _liveOrder.ApplyGlyphs(_contributors, LiveColumns);
        _contributors.ColumnHeaderMouseClick += (_, e) => { if (e.ColumnIndex >= 0) { _liveOrder.Click(LiveColumns[e.ColumnIndex]); RefreshActivity(); } };
        _contributors.CellClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _contributors.Rows[e.RowIndex].Tag is AppActivity app && app.Name == CpuCoverage.UnattributedName)
            { _liveOrder.SystemExpanded = !_liveOrder.SystemExpanded; RefreshActivity(); }
        };
        _contributors.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _contributors.Rows[e.RowIndex].Tag is AppActivity app && app.Processes.Length > 0) SelectPage("Processes");
        };
        contributionPanel.Controls.Add(_contributors);
        contributionPanel.Controls.Add(_activityStatus);
        _buttonTips.SetToolTip(inspect, "Open the full process list to inspect app energy estimates, individual processes, and the source of each estimate.");
        contributionPanel.Controls.Add(inspect);
        var energyButton = Theme.Button("Windows energy (admin)", () =>
        {
            try { _monitor.StartWindowsEnergy(); }
            catch (Exception e) { MessageBox.Show(e.Message, "Windows energy capture"); }
        });
        energyButton.Dock = DockStyle.Bottom;
        energyButton.AutoSize = false;
        energyButton.Padding = new Padding(12, 0, 12, 0);
        energyButton.Height = 32;
        _buttonTips.SetToolTip(energyButton, "Start or restart Windows energy capture (administrator approval required). Estimates app CPU, disk, and network energy on both AC and battery. Readings usually arrive in about a minute. AC mAh is battery-equivalent energy use. Capture stops when the monitor exits; no driver is installed.");
        contributionPanel.Controls.Add(energyButton);
        var hardwareButton = Theme.Button("Hardware readings / calibration: toggle", () =>
        {
            if (!_monitor.Settings.EnableHardwareEnergy)
                MessageBox.Show(this, "Hardware diagnostic reads CPU and GPU power. GPU polling can keep a sleeping GPU awake. Calibration collects battery discharge automatically and keeps idle/system power separate. Toggle again to stop probing.", "Hardware energy");
            _monitor.ToggleHardwareEnergy();
        });
        hardwareButton.Dock = DockStyle.Bottom;
        hardwareButton.AutoSize = false;
        hardwareButton.Padding = new Padding(12, 0, 12, 0);
        hardwareButton.Height = 32;
        _buttonTips.SetToolTip(hardwareButton, "Toggle CPU/GPU hardware power polling and automatic battery calibration. Calibration needs at least five minutes of varied battery activity and acceptable validation error. Supported sensors vary by laptop; GPU polling can keep the GPU awake and increase power use. Click again to disable.");
        contributionPanel.Controls.Add(hardwareButton);
        contributionPanel.Controls.Add(topTitle);
        center.Panel2.Controls.Add(contributionPanel);
        var environment = new SurfacePanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 14, 0, 0)
        };
        _environment.Dock = DockStyle.Fill;
        environment.Controls.Add(_environment);
        layout.Controls.Add(cards, 0, 0);
        layout.Controls.Add(center, 0, 1);
        layout.Controls.Add(environment, 0, 2);
        page.Controls.Add(layout);
        _graph.PointSelected += sample => MessageBox.Show(this, Details(sample), "Recorded measurements");
    }

    private Control GraphToolbar(PowerGraph graph, bool history)
    {
        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            WrapContents = false
        };
        var modes = new ChoicePicker
        {
            Width = 145
        };
        modes.Items.AddRange(["Automatic", "CPU / GPU activity", "Battery watts", "CPU package watts"]);
        modes.SelectedIndex = 0;
        modes.SelectedIndexChanged += (_, _) =>
        {
            graph.Mode = (GraphMode)Math.Max(0, modes.SelectedIndex);
            graph.Invalidate();
        };
        tools.Controls.Add(modes);
        if (!history)
        {
            var ranges = new ChoicePicker
            {
                Width = 125
            };
            ranges.Items.AddRange(["1 minute", "5 minutes", "15 minutes", "1 hour", "6 hours", "Battery session"]);
            ranges.SelectedIndex = 1;
            ranges.SelectedIndexChanged += (_, _) =>
            {
                _sessionRange = ranges.SelectedIndex == 5;
                _range = ranges.SelectedIndex switch
                {
                    0 => TimeSpan.FromMinutes(1),
                    1 => TimeSpan.FromMinutes(5),
                    2 => TimeSpan.FromMinutes(15),
                    3 => TimeSpan.FromHours(1),
                    4 => TimeSpan.FromHours(6),
                    _ => TimeSpan.FromMinutes(5)
                };
                RefreshGraph();
            };
            tools.Controls.Add(ranges);
            _pauseButton = Theme.Button(_monitor.Monitoring ? "Pause" : "Start", ToggleMonitoring);
            tools.Controls.Add(_pauseButton);
        }

        return tools;
    }

    private void ToggleMonitoring()
    {
        _monitor.Monitoring = !_monitor.Monitoring;
        if (_pauseButton != null)
            _pauseButton.Text = _monitor.Monitoring ? "Pause" : "Start";
        _footer.Text = _monitor.Monitoring ? "Resuming monitoring..." : "Monitoring paused\r\nRecording paused";
        if (!_monitor.Monitoring)
            _connection.Text = "MONITORING PAUSED";
    }

    private void BuildHistory()
    {
        var page = Page("History");
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 14,
            BackColor = Theme.Background
        };
        split.Resize += (_, _) =>
        {
            if (split.Height > 400)
                split.SplitterDistance = split.Height * 56 / 100;
        };
        var graph = Wrap("Recorded power & activity", _historyGraph);
        graph.Controls.Add(GraphToolbar(_historyGraph, true));
        split.Panel1.Controls.Add(graph);
        split.Panel2.Controls.Add(Wrap("Sample context", _historyDetails));
        page.Controls.Add(split);
        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46
        };
        var hours = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 720,
            Value = 6,
            Width = 72
        };
        var hoursLabel = Theme.Label("Hours", 10);
        hoursLabel.Size = new(55, 30);
        tools.Controls.Add(hoursLabel);
        tools.Controls.Add(hours);
        tools.Controls.Add(Theme.Button("Load history", () =>
        {
            try
            {
                using var store = new HistoryStore(Path.Combine(AppFiles.Root, "history.sqlite"), _monitor.Settings.RetentionDays);
                var samples = store.Read(DateTimeOffset.UtcNow.AddHours(-(double)hours.Value), 100000);
                _historyGraph.Samples = samples;
                _historyGraph.Range = TimeSpan.FromHours((double)hours.Value);
                _historyGraph.Invalidate();
                var watts = samples.Where(s => s.Watts.HasValue).ToArray();
                var activity = samples.Where(s => s.CpuPercent.HasValue).ToArray();
                _historyDetails.Text = $"Recorded samples: {samples.Count}\r\nAverage CPU activity: {(activity.Length > 0 ? activity.Average(s => s.CpuPercent!.Value).ToString("F1") + "%" : "Unavailable")}\r\nAverage measured battery discharge: {(watts.Length > 0 ? watts.Average(s => s.Watts!.Value).ToString("F1") + " W" : "Unavailable on AC")}\r\n\r\nSelect a graph point to inspect CPU, GPU activity and the top apps recorded at that time.";
            }
            catch (Exception e)
            {
                MessageBox.Show(this, e.Message, "History unavailable");
            }
        }, true));
        page.Controls.Add(tools);
        _historyGraph.PointSelected += sample => _historyDetails.Text = Details(sample);
    }

    private void BuildAlerts()
    {
        var page = Page("Alerts");
        _alertGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Time", DataPropertyName = "Time", FillWeight = 24 });
        _alertGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Severity", DataPropertyName = "Severity", FillWeight = 15 });
        _alertGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Battery W", DataPropertyName = "Watts", FillWeight = 14 });
        _alertGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Details", DataPropertyName = "Details", FillWeight = 90 });
        page.Controls.Add(Wrap("Sustained battery-drain notifications", _alertGrid));
        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46
        };
        tools.Controls.Add(Theme.Button("Reload alerts", ReloadAlerts));
        tools.Controls.Add(Theme.Button("Pause 30 minutes", () => _monitor.PauseAlerts(30)));
        page.Controls.Add(tools);
    }

    private void BuildOptions()
    {
        var page = Page("Power Options");
        page.Controls.Add(Wrap("Review before changing", _options));
        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46
        };
        tools.Controls.Add(Theme.Button("Windows power settings", () => OpenSettings("ms-settings:powersleep")));
        tools.Controls.Add(Theme.Button("Display settings", () => OpenSettings("ms-settings:display")));
        tools.Controls.Add(Theme.Button("Per-app graphics settings", () => OpenSettings("ms-settings:display-advancedgraphics")));
        page.Controls.Add(tools);
    }

    private static void OpenSettings(string uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "Windows settings");
        }
    }

    private void BuildSettings(MonitorSettings settings)
    {
        var page = Page("Settings");
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterWidth = 14,
            BackColor = Theme.Background
        };
        split.Resize += (_, _) =>
        {
            if (split.Width > 500)
                split.SplitterDistance = split.Width * 63 / 100;
        };
        var form = new SurfacePanel
        {
            Dock = DockStyle.Fill
        };
        var rows = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 2
        };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        int index = 0;
        void Row(string label, Control control, int height = 42)
        {
            rows.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            var text = Theme.Label(label);
            text.Dock = DockStyle.Fill;
            text.TextAlign = ContentAlignment.MiddleLeft;
            control.Anchor = AnchorStyles.Left;
            rows.Controls.Add(text, 0, index);
            rows.Controls.Add(control, 1, index++);
        }

        NumericUpDown Number(double value, int minimum, int maximum)
        {
            return new()
            {
                Minimum = minimum,
                Maximum = maximum,
                Value = (decimal)Math.Clamp(value, minimum, maximum),
                Width = 105
            };
        }

        CheckBox Toggle(bool value) => new()
        {
            Checked = value,
            Text = "Enabled",
            AutoSize = true
        };
        var live = Number(settings.SampleSeconds, 2, 60);
        var processes = Number(settings.ProcessSeconds, 5, 300);
        var history = Number(settings.HistorySeconds, 2, 300);
        var retention = Number(settings.RetentionDays, 1, 3650);
        var notifications = Toggle(settings.NotificationsEnabled);
        var close = Toggle(settings.CloseToTray);
        var minimized = Toggle(settings.StartMinimized);
        var auto = Toggle(settings.StartMonitoring);
        var cpuSensors = Toggle(settings.EnableCpuSensors);
        var hardwareEnergy = Toggle(settings.EnableHardwareEnergy);
        var processMonitoring = Toggle(settings.EnableProcessMonitoring);
        var gpu = Toggle(settings.EnableGpuActivity);
        var lowPower = Toggle(settings.LowPowerMode);
        var restricted = Toggle(settings.EnableRestrictedProcessCounters);
        Row("Low-power mode (recommended)", lowPower);
        Row("Protected-process CPU counters", Theme.Label("Always included", 9));
        Row("Low-power: live/apps 10s+, history 60s+", Theme.Label("GPU probes off", 9));
        Row("Live samples (seconds)", live);
        Row("App CPU sampling", Theme.Label("Same interval as live CPU", 9));
        Row("History samples (seconds)", history);
        Row("Keep history (days)", retention);
        Row("Monitor apps on AC and battery", processMonitoring);
        Row("Read Windows GPU activity counters", gpu);
        Row("CPU hardware sensors (restart required)", cpuSensors);
        Row("Hardware energy / battery calibration (may wake GPU)", hardwareEnergy);
        Row("Battery-drain notifications", notifications);
        Row("Close window to tray", close);
        Row("Start minimized", minimized);
        Row("Start monitoring when opened", auto);
        var watts = new List<NumericUpDown>();
        var durations = new List<NumericUpDown>();
        var clear = new List<NumericUpDown>();
        foreach (var rule in settings.Rules)
        {
            var w = Number(rule.Watts, 1, 500);
            var d = Number(rule.Seconds, 1, 3600);
            var c = Number(rule.ClearWatts, 0, 499);
            watts.Add(w);
            durations.Add(d);
            clear.Add(c);
            Row($"{rule.Name}: enter above watts", w);
            Row($"{rule.Name}: sustained seconds", d);
            Row($"{rule.Name}: clear below watts", c);
        }

        var repeat = Number(settings.RepeatSeconds, 60, 86400);
        var grace = Number(settings.UnplugGraceSeconds, 0, 3600);
        Row("Minimum notification repeat (seconds)", repeat);
        Row("Ignore first seconds after unplug", grace);
        var excludes = new TextBox
        {
            Multiline = true,
            Width = 190,
            Height = 90,
            Text = string.Join(Environment.NewLine, settings.ExcludedApplications),
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.Text
        };
        _excludesEditor = excludes;
        Row("Ignore alerts while these apps run\r\nOne executable name per line", excludes, 104);
        rows.RowCount = index;
        form.Controls.Add(rows);
        split.Panel1.Controls.Add(form);
        split.Panel2.Controls.Add(Wrap("Hardware & capabilities", _detection));
        page.Controls.Add(split);
        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46
        };
        tools.Controls.Add(Theme.Button("Save preferences", () =>
        {
            try
            {
                var updated = JsonSerializer.Deserialize<MonitorSettings>(JsonSerializer.Serialize(_monitor.Settings))!;
                updated.SampleSeconds = (int)live.Value;
                updated.ProcessSeconds = Math.Max(5, updated.SampleSeconds);
                updated.HistorySeconds = (int)history.Value;
                updated.RetentionDays = (int)retention.Value;
                updated.NotificationsEnabled = notifications.Checked;
                updated.CloseToTray = close.Checked;
                updated.StartMinimized = minimized.Checked;
                updated.StartMonitoring = auto.Checked;
                updated.EnableCpuSensors = cpuSensors.Checked;
                updated.EnableHardwareEnergy = hardwareEnergy.Checked;
                updated.EnableProcessMonitoring = processMonitoring.Checked;
                updated.EnableGpuActivity = gpu.Checked;
                updated.LowPowerMode = lowPower.Checked;
                updated.EnableRestrictedProcessCounters = restricted.Checked;
                updated.RepeatSeconds = (int)repeat.Value;
                updated.UnplugGraceSeconds = (int)grace.Value;
                updated.ExcludedApplications = excludes.Lines.Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                updated.Rules = settings.Rules.Select((r, i) => new AlertRule(r.Name, (double)watts[i].Value, (int)durations[i].Value, (double)clear[i].Value)).ToArray();
                AppFiles.Save(updated);
                _monitor.UpdateSettings(updated);
                MessageBox.Show(this, "Preferences saved. CPU sensor changes require restarting the app.", "Settings");
            }
            catch (Exception e)
            {
                MessageBox.Show(this, e.Message, "Invalid preferences");
            }
        }, true));
        tools.Controls.Add(Theme.Button("Test notification", () => ShowAlert(new(DateTimeOffset.UtcNow, "Test", 0, "Test notification. No power settings are changed."))));
        tools.Controls.Add(Theme.Button("Open data folder", () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", AppFiles.Root) { UseShellExecute = true })));
        page.Controls.Add(tools);
    }

    private void UpdateDetection(Detection detection)
    {
        _detected = detection;
        _detection.Text = $"{detection.Windows}\r\n\r\n{detection.Model}\r\n\r\nGraphics adapters\r\n{string.Join("\r\n", detection.Adapters)}\r\n\r\nHardware sensor capabilities\r\n{(detection.Sensors.Length > 0 ? string.Join("\r\n", detection.Sensors) : "CPU hardware probe disabled or unavailable")}\r\n\r\nDiagnostics\r\n{string.Join("\r\n", detection.Issues)}";
        if (detection.XmgPresent)
        {
            var page = Page("XMG");
            page.Controls.Add(Wrap("XMG GPU power-saving mode", _xmg));
            _xmg.Text = detection.XmgDetails;
            _tabs.TabPages.Remove(page);
            _tabs.TabPages.Insert(6, page);
            Theme.Apply(page);
        }

        RebuildNavigation();
        ReloadAlerts();
    }

    private static string Number(double? value, string unit = "") => value.HasValue ? $"{value:F1}{unit}" : "--";

    private static string Runtime(Sample sample) => sample.Watts > 0 && sample.RemainingWh.HasValue ? TimeSpan.FromHours(Math.Min(10000, sample.RemainingWh.Value / sample.Watts.Value)).ToString(@"h\h\ mm\m") : "Unavailable";

    private void UpdateSample(Sample sample)
    {
        _latest = sample;
        if (sample.Online == false)
            _sessionStart ??= sample.Time;
        else
            _sessionStart = null;
        _samples.Add(sample);
        if (_samples.Count > 10800)
            _samples.RemoveRange(0, _samples.Count - 10800);
        _drawItem.Text = sample.Online == true ? $"CPU {Number(sample.CpuPercent, "%")} / GPU {Number(sample.GpuPercent, "%")}" : "Discharge " + Number(sample.Watts, " W");
        _batteryItem.Text = "Battery: " + Number(sample.Percent, "%") + " / " + BatteryMetrics.ChargingStatus(sample);
        var tip = sample.Online == true ? $"AC | CPU {Number(sample.CpuPercent, "%")} | GPU {Number(sample.GpuPercent, "%")}" : $"Battery {Number(sample.Percent, "%")} | {Number(sample.Watts, " W")}";
        _tray.Text = tip[..Math.Min(63, tip.Length)];
        if (Visible && WindowState != FormWindowState.Minimized)
            RenderSample(sample);
    }

    private void RenderSample(Sample sample)
    {
        // A hidden tray window does not need layout, control text updates, or chart paint.
        _connection.Text = !_monitor.Monitoring ? "MONITORING PAUSED" : sample.Online == true ? "PLUGGED IN  /  AC" : sample.Online == false ? "ON BATTERY" : "SOURCE UNKNOWN";
        if (_tabs.SelectedTab?.Text == "Live")
            _subtitle.Text = sample.Online == true ? "AC diagnostics - find apps keeping your hardware busy" : "Measured battery power and likely contributors";
        _power.Set(sample.Online == true ? "AC power" : Number(sample.Watts, " W"), sample.Online == true ? $"Battery charging: {Number(sample.ChargeWatts, " W")}\r\nTotal AC watts not measured" : $"Battery discharge\r\nRuntime: {Runtime(sample)}");
        _cpu.Set(Number(sample.CpuPercent, "%"), $"Package: {Number(sample.CpuWatts, " W")}");
        _gpu.Set(Number(sample.GpuPercent, "%"), _monitor.Settings.LowPowerMode && !_monitor.Settings.EnableHardwareEnergy ? "Disabled in low-power mode\r\nEnable diagnostics in Settings" : "Busiest Windows GPU engine\r\nActivity, not GPU watts");
        _batteryCard.Set(Number(sample.Percent, "%"), $"{Number(sample.RemainingWh, " Wh")} / ~{Number(sample.RemainingMilliAmpHours, " mAh")}\r\nFull {Number(_detected?.Battery.FullWh, " Wh")} / ~{Number(BatteryMetrics.ToMilliAmps(_detected?.Battery.FullWh, sample.Voltage), " mAh")}");
        _environment.Text = $"DISPLAY   {sample.DisplaySummary}\r\nGPU W   NVIDIA {Number(sample.NvidiaWatts, " W")} / AMD {Number(sample.AmdWatts, " W")}\r\nPROFILE   {sample.PowerPlan}   |   Battery Saver: {(sample.BatterySaver == true ? "On" : "Off / unknown")}   |   {sample.XmgMode}";
        _footer.Text = $"Monitor: {sample.SelfCpuPercent:F2}% CPU\r\n{sample.MemoryMb:F0} MB working set\r\n{(_monitor.Settings.LowPowerMode ? "Low-power" : "Diagnostic")} {_monitor.Settings.EffectiveSampleSeconds}s / apps {_monitor.Settings.EffectiveProcessSeconds}s";
        var info = _detected?.Battery;
        _battery.Text = $"CHARGING STATUS\r\n{BatteryMetrics.ChargingStatus(sample)}\r\nCurrent charge     {Number(sample.Percent, "%")}\r\n\r\nCURRENT & OBSERVED USAGE\r\nDischarge current (estimated)     {Number(sample.DischargeMilliAmps, " mA")}\r\nCharging current (estimated)     {Number(sample.ChargeMilliAmps, " mA")}\r\nUsed since monitoring started     {Number(sample.UsedWattHours, " Wh")} / {Number(sample.UsedMilliAmpHours, " mAh")}\r\nAdded since monitoring started     {Number(sample.AddedWattHours, " Wh")} / {Number(sample.AddedMilliAmpHours, " mAh")}\r\nCovered measurement time     {sample.BatteryObservedSeconds / 60:F1} minutes\r\nRemaining charge equivalent (estimated)     {Number(sample.RemainingMilliAmpHours, " mAh")}\r\n\r\nBATTERY CAPACITY HEALTH\r\nHealth / retained design capacity     {Number(info?.HealthPercent, "%")}\r\nWear     {Number(info?.WearPercent, "%")}\r\nDesign capacity     {Number(info?.DesignWh, " Wh")}\r\nFull-charge capacity     {Number(info?.FullWh, " Wh")} / ~{Number(BatteryMetrics.ToMilliAmps(info?.FullWh, sample.Voltage), " mAh")}\r\nFirmware cycle count     {Number(info?.Cycles)}\r\n\r\nRemaining energy     {Number(sample.RemainingWh, " Wh")}\r\nVoltage     {Number(sample.Voltage, " V")}\r\nDischarge power     {Number(sample.Watts, " W")}\r\nCharging power     {Number(sample.ChargeWatts, " W")}\r\nEstimated battery runtime     {Runtime(sample)}\r\n\r\nCurrent = watts / measured volts; mAh totals integrate valid observed intervals only.\r\nTotals reset when the app restarts. Pause, sleep, source changes, and missing readings are skipped.\r\nRemaining mAh uses current voltage; it is not the battery's nominal capacity rating.\r\nHealth describes capacity retention, not charging-circuit or safety condition.\r\nOn AC / not charging can reflect a charge limit; no fault is inferred.\r\nCharging power is battery input, not total laptop consumption.\r\nZero cycle counts may mean the firmware counter is unsupported.";
        _options.Text = $"Current power plan: {sample.PowerPlan}\r\nInternal panel: {(sample.RefreshHz.HasValue ? sample.RefreshHz + " Hz" : "unavailable")}\r\nExternal displays: {sample.ExternalDisplays}\r\n\r\nSuggestions to review\r\n" + (sample.RefreshHz > 60 ? "A lower internal display refresh rate may reduce power use.\r\n" : "") + "Inspect high-activity apps in Processes. A browser may have many subprocesses.\r\nReview per-app GPU preferences when an app uses the discrete GPU unnecessarily.\r\n" + (sample.ExternalDisplays > 0 ? "External displays can require the discrete GPU. Activity while docked does not imply a fault.\r\n" : "") + "\r\nThese links open Windows settings for you to review. This app applies no changes.";
        if (_tabs.SelectedTab?.Text == "Live")
            RefreshGraph();
    }
    private void UpdateActivity(ProcessSnapshot snapshot)
    {
        _activity = snapshot;
        if (Visible)
            RefreshActivity();
        _activityStatus.Text = $"CPU {Number(snapshot.TotalCpuPercent, "%")} | listed {Number(snapshot.AccountedCpuPercent, "%")} | system {Number(snapshot.UnattributedCpuPercent, "%")}\r\nSame {snapshot.CpuIntervalSeconds:F1}s interval | {snapshot.Time.ToLocalTime():HH:mm:ss}\r\n{_monitor.WindowsEnergyStatus}";
    }

    private void RefreshActivity()
    {
        if (_activity == null)
            return;
        if (_tabs.SelectedTab?.Text == "Processes")
            _processes.UpdateSnapshot(_activity);
        if (_tabs.SelectedTab?.Text == "Live")
        {
            _contributors.Rows.Clear();
            foreach (var item in _liveOrder.Rows(_activity))
            {
                var app = item.App;
                var index = _contributors.Rows.Add(item.DisplayName, Number(app.CpuPercent, "%"), Number(app.GpuPercent, "%"), app.Impact, app.EstimatedMilliAmpHours.HasValue ? app.EstimatedMilliAmpHours.Value.ToString("F3") : "--");
                _contributors.Rows[index].Tag = app;
                _contributors.Rows[index].Cells[0].ToolTipText = app.Reason;
                _contributors.Rows[index].Cells[4].ToolTipText = "Estimated captured mAh; on AC this is battery-equivalent energy use. " + app.EnergySource;
                if (item.Child) _contributors.Rows[index].DefaultCellStyle.ForeColor = Theme.Muted;
            }
            _liveOrder.ApplyGlyphs(_contributors, LiveColumns);
            _contributors.ClearSelection();
        }
    }

    internal void VerifyActivityInteractions(Action<string> capture)
    {
        SelectPage("Live", false);
        RefreshActivity();
        ActivityUiSmoke.Header(_contributors, 1);
        if (_liveOrder.Descending || !_contributors.Columns[1].HeaderText.EndsWith("\u2191")) throw new Exception("Live ascending header click failed");
        capture("Live-CPU-ascending");
        ActivityUiSmoke.Header(_contributors, 1);
        RefreshActivity();
        if (!_liveOrder.Descending || !_contributors.Columns[1].HeaderText.EndsWith("\u2193")) throw new Exception("Live descending sort did not persist");
        capture("Live-CPU-descending");
        ActivityUiSmoke.Header(_contributors, 4);
        capture("Live-mAh-descending");
        ActivityUiSmoke.Header(_contributors, 1);
        var system = ActivityUiSmoke.SystemRow(_contributors);
        if (system >= 0)
        {
            ActivityUiSmoke.Cell(_contributors, system);
            RefreshActivity();
            if (!_liveOrder.SystemExpanded || _contributors.Rows.Count <= _activity!.Apps.Length) throw new Exception("Live system expansion failed");
            capture("Live-system-expanded");
            ActivityUiSmoke.Cell(_contributors, ActivityUiSmoke.SystemRow(_contributors));
            if (_liveOrder.SystemExpanded) throw new Exception("Live system collapse failed");
        }
        SelectPage("Processes", false);
        RefreshActivity();
        _processes.VerifyInteractions(capture);
    }

    private void RefreshVisible()
    {
        if (!Visible)
            return;
        if (_latest != null)
            RenderSample(_latest);
        RefreshActivity();
    }

    private void RefreshGraph()
    {
        _graph.Samples = _samples;
        _graph.Range = _sessionRange && _sessionStart.HasValue ? TimeSpan.FromSeconds(Math.Max(60, (_samples.LastOrDefault()?.Time - _sessionStart)?.TotalSeconds ?? 60)) : _range;
        _graph.Invalidate();
    }

    private void ExcludeApp(AppActivity app)
    {
        // Exclusions suppress alerts for a chosen app while retaining diagnostic measurements.
        try
        {
            var settings = JsonSerializer.Deserialize<MonitorSettings>(JsonSerializer.Serialize(_monitor.Settings))!;
            var name = app.Path != null ? Path.GetFileName(app.Path) : app.Name + ".exe";
            settings.ExcludedApplications = settings.ExcludedApplications.Append(name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            AppFiles.Save(settings);
            _monitor.UpdateSettings(settings);
            if (_excludesEditor != null)
                _excludesEditor.Text = string.Join(Environment.NewLine, settings.ExcludedApplications);
            MessageBox.Show(this, $"Battery-drain notifications are suppressed while {name} runs. Measurements still continue.", "Alert exclusion");
        }
        catch (Exception e)
        {
            MessageBox.Show(this, e.Message, "Exclusion");
        }
    }

    private static string Details(Sample sample)
    {
        var text = $"{sample.Time.ToLocalTime():F}\r\n\r\nSource: {(sample.Online == true ? "AC" : "Battery / unknown")}\r\nBattery discharge: {Number(sample.Watts, " W")}\r\nBattery charging: {Number(sample.ChargeWatts, " W")}\r\nCharging status: {BatteryMetrics.ChargingStatus(sample)}\r\nDischarge current: {Number(sample.DischargeMilliAmps, " mA")}\r\nCharging current: {Number(sample.ChargeMilliAmps, " mA")}\r\nObserved used / added: {Number(sample.UsedMilliAmpHours, " mAh")} / {Number(sample.AddedMilliAmpHours, " mAh")}\r\nCPU: {Number(sample.CpuPercent, "%")} / {Number(sample.CpuWatts, " W")}\r\nNVIDIA: {Number(sample.NvidiaWatts, " W")} / AMD: {Number(sample.AmdWatts, " W")}\r\n{sample.HardwareStatus}\r\nGPU engine activity: {Number(sample.GpuPercent, "%")}\r\nActivity sampled: {sample.ActivityTime?.ToLocalTime():T}\r\n\r\n{sample.DisplaySummary}\r\nPower plan: {sample.PowerPlan}\r\n\r\nLikely contributors at this point\r\n";
        if (sample.PowerAccounting is { } accounting)
            text += $"Power accounting: measured {Number(accounting.MeasuredWatts, " W")}, estimated apps {Number(accounting.AppWatts, " W")}, system/unattributed {Number(accounting.SystemWatts, " W")}\r\nObserved charge: {Number(accounting.ObservedMilliAmpHours, " mAh")} = estimated apps {Number(accounting.AppMilliAmpHours, " mAh")} + system {Number(accounting.SystemMilliAmpHours, " mAh")}\r\n{accounting.Status}\r\n\r\n";
        foreach (var app in sample.TopProcesses ?? [])
            text += $"{app.Name} ({app.ProcessCount} processes): estimated {Number(app.EstimatedWatts, " W")} / {Number(app.EstimatedMilliAmpHours, " mAh")}; CPU {Number(app.CpuPercent, "%")}, GPU {Number(app.GpuPercent, "%")}, I/O {Number(app.IoMiBPerSecond)} MiB/s - {app.Impact}\r\n{app.Adapter}\r\n";
        return text + "\r\nActivity is attribution, not measured per-app wattage.\r\n" + sample.Status;
    }

    private void ReloadAlerts()
    {
        try
        {
            using var store = new HistoryStore(Path.Combine(AppFiles.Root, "history.sqlite"), _monitor.Settings.RetentionDays);
            _alertGrid.DataSource = store.ReadAlerts();
        }
        catch (Exception e)
        {
            AppFiles.Log("Read alerts: " + e.Message);
        }
    }

    private void ShowAlert(AlertEvent alert)
    {
        // A toast failure can fall back to the tray; notification delivery must not stop monitoring.
        ReloadAlerts();
        try
        {
            new ToastContentBuilder().AddArgument("page", "live").AddText($"{alert.Severity} battery usage - {alert.Watts:F1} W").AddText(alert.Details).AddButton(new ToastButton().SetContent("Open Details").AddArgument("page", "live")).AddButton(new ToastButton().SetContent("Power Options").AddArgument("page", "options")).AddButton(new ToastButtonDismiss("Dismiss")).Show();
        }
        catch (Exception e)
        {
            AppFiles.Log("Toast fallback: " + e.Message);
            _tray.ShowBalloonTip(10000, $"{alert.Severity}: {alert.Watts:F1} W", alert.Details, ToolTipIcon.Warning);
        }
    }
}
