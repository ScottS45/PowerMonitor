// ============================================================================
// File: Theme.cs
// Author: Scott Sheen (with ChatGpt)
// Created: 2026-10-02
// Updated: 2026-10-02
// Purpose: Reusable colors, controls, and custom desktop rendering.
// ============================================================================

using System.Drawing.Drawing2D;

namespace PowerMonitor;

internal static class Theme
{

    public static readonly Color Background = Color.FromArgb(14, 21, 34), Sidebar = Color.FromArgb(10, 16, 27), Surface = Color.FromArgb(23, 34, 52), SurfaceAlt = Color.FromArgb(28, 41, 60), Border = Color.FromArgb(42, 57, 78), Text = Color.FromArgb(235, 241, 248), Muted = Color.FromArgb(150, 168, 190), Accent = Color.FromArgb(64, 217, 177), Blue = Color.FromArgb(105, 165, 255), Purple = Color.FromArgb(168, 143, 255), Orange = Color.FromArgb(248, 189, 97);

    public static Label Label(string text, int size = 10, bool bold = false, Color? color = null) => new()
    {
        Text = text,
        ForeColor = color ?? Text,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        AutoSize = false,
        UseCompatibleTextRendering = false
    };

    public static Button Button(string text, Action action, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Height = 34,
            Padding = new Padding(12, 4, 12, 4),
            FlatStyle = FlatStyle.Flat,
            ForeColor = primary ? Sidebar : Text,
            BackColor = primary ? Accent : SurfaceAlt,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 10, 0)
        };
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.Click += (_, _) => action();
        return button;
    }

    public static DataGridView Grid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Surface,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Border,
            EnableHeadersVisualStyles = false,
            AutoGenerateColumns = false,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            ColumnHeadersHeight = 38,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Surface,
            ForeColor = Text,
            SelectionBackColor = Color.FromArgb(37, 68, 80),
            SelectionForeColor = Text,
            Padding = new Padding(8, 0, 8, 0),
            Font = new Font("Segoe UI", 10)
        };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = SurfaceAlt,
            ForeColor = Muted,
            SelectionBackColor = SurfaceAlt,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Padding = new Padding(8, 0, 8, 0)
        };
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(20, 31, 48);
        grid.RowTemplate.Height = 36;
        grid.DoubleBuffer();
        return grid;
    }

    private static void DoubleBuffer(this DataGridView grid) => typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(grid, true);

    public static TextBox TextBox(bool editable = false, bool mono = false) => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = !editable,
        ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.None,
        BackColor = Surface,
        ForeColor = Text,
        Font = new Font(mono ? "Consolas" : "Segoe UI", 10),
        Margin = new Padding(0)
    };

    public static void Apply(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is TabPage)
            {
                control.BackColor = Background;
                control.ForeColor = Text;
            }

            if (control is CheckBox)
            {
                control.ForeColor = Text;
            }

            if (control is ComboBox combo)
            {
                combo.BackColor = SurfaceAlt;
                combo.ForeColor = Text;
                combo.FlatStyle = FlatStyle.Flat;
            }

            if (control is NumericUpDown number)
            {
                number.BackColor = SurfaceAlt;
                number.ForeColor = Text;
                number.BorderStyle = BorderStyle.FixedSingle;
            }

            Apply(control);
        }
    }
}

public class SurfacePanel : Panel
{

    public SurfacePanel()
    {
        DoubleBuffered = true;
        BackColor = Theme.Surface;
        Padding = new Padding(16);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Drawing uses cached samples only; painting must not query hardware or processes.
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

public sealed class MetricCard : SurfacePanel
{

    private readonly Label _title, _value, _caption;

    public MetricCard(string title, Color accent)
    {
        _title = Theme.Label(title.ToUpperInvariant(), 9, true, Theme.Muted);
        _value = Theme.Label("--", 22, true, accent);
        _caption = Theme.Label("Waiting for telemetry", 9, false, Theme.Muted);
        _title.Dock = DockStyle.Top;
        _title.Height = 22;
        _value.Dock = DockStyle.Top;
        _value.Height = 43;
        _caption.Dock = DockStyle.Fill;
        Controls.Add(_caption);
        Controls.Add(_value);
        Controls.Add(_title);
        Dock = DockStyle.Fill;
        Margin = new Padding(0, 0, 12, 0);
        Padding = new Padding(16, 12, 12, 8);
    }

    public void Set(string value, string caption)
    {
        _value.Text = value;
        _caption.Text = caption;
    }
}

internal sealed class NavigationList : ListBox
{

    public NavigationList()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = 44;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Sidebar;
        ForeColor = Theme.Muted;
        Font = new Font("Segoe UI", 11);
        IntegralHeight = false;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0)
            return;
        var active = (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(active ? Theme.SurfaceAlt : Theme.Sidebar);
        e.Graphics.FillRectangle(background, e.Bounds);
        if (active)
        {
            using var accent = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(accent, e.Bounds.Left, e.Bounds.Top + 9, 3, e.Bounds.Height - 18);
        }

        TextRenderer.DrawText(e.Graphics, Items[e.Index].ToString(), Font, new Rectangle(e.Bounds.Left + 22, e.Bounds.Top, e.Bounds.Width - 28, e.Bounds.Height), active ? Theme.Text : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }
}

internal sealed class PageHost : TabControl
{

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x1328 && !DesignMode)
        {
            message.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref message);
    }
}

internal sealed class ChoicePicker : Button
{

    private int _selected = -1;

    private readonly ContextMenuStrip _menu = new()
    {
        BackColor = Theme.SurfaceAlt,
        ForeColor = Theme.Text,
        ShowImageMargin = false
    };

    public List<string> Items { get; } = [];

    public event EventHandler? SelectedIndexChanged;

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            var next = value >= 0 && value < Items.Count ? value : -1;
            if (next == _selected)
                return;
            _selected = next;
            Text = SelectedItem ?? "Select";
            Text += "  \u25be";
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? SelectedItem => _selected >= 0 ? Items[_selected] : null;

    public ChoicePicker()
    {
        Height = 30;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderColor = Theme.Border;
        BackColor = Theme.SurfaceAlt;
        ForeColor = Theme.Text;
        TextAlign = ContentAlignment.MiddleLeft;
        Padding = new Padding(8, 0, 4, 0);
        Cursor = Cursors.Hand;
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        _menu.Items.Clear();
        for (var i = 0; i < Items.Count; i++)
        {
            var index = i;
            var item = new ToolStripMenuItem(Items[i])
            {
                ForeColor = Theme.Text,
                BackColor = Theme.SurfaceAlt,
                Checked = i == _selected
            };
            item.Click += (_, _) => SelectedIndex = index;
            _menu.Items.Add(item);
        }

        _menu.Show(this, new Point(0, Height));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
        {
            SelectedIndex = Math.Clamp(_selected + (e.KeyCode == Keys.Down ? 1 : -1), 0, Items.Count - 1);
            e.Handled = true;
        }
        else
            base.OnKeyDown(e);
    }

    protected override void Dispose(bool disposing)
    {
        // Release owned native handles and flush pending work when the component shuts down.
        if (disposing)
            _menu.Dispose();
        base.Dispose(disposing);
    }
}
