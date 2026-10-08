// Purpose: Persistent numeric sorting, sort glyphs and expandable system CPU rows.
// Author: Scott Sheen (with ChatGpt). Created/Updated: 2026-10-03.
namespace PowerMonitor;

public enum ActivityColumn { App, Cpu, Gpu, Impact, Watts, Mah, Count, Io, Adapter }
public record ActivityListRow(AppActivity App, string DisplayName, bool Child);
public sealed class ActivityListOrder
{
    public ActivityColumn Column { get; private set; } = ActivityColumn.Cpu;
    public bool Descending { get; private set; } = true;
    public bool SystemExpanded { get; set; }
    public void Select(ActivityColumn column)
    {
        Column = column;
        Descending = column != ActivityColumn.App && column != ActivityColumn.Adapter;
    }
    public void Click(ActivityColumn column)
    {
        if (Column == column) Descending = !Descending;
        else Select(column);
    }
    private double? Value(AppActivity app) => Column switch
    {
        ActivityColumn.Cpu => app.CpuPercent,
        ActivityColumn.Gpu => app.GpuPercent,
        ActivityColumn.Watts => app.EstimatedWatts,
        ActivityColumn.Mah => app.EstimatedMilliAmpHours,
        ActivityColumn.Io => app.IoMiBPerSecond,
        ActivityColumn.Count => app.Pids.Length,
        _ => app.Rank
    };
    public IEnumerable<AppActivity> Sort(IEnumerable<AppActivity> apps)
    {
        if (Column is ActivityColumn.App or ActivityColumn.Adapter)
        {
            Func<AppActivity, string> text = a => Column == ActivityColumn.App ? a.Name : a.Gpu;
            return Descending ? apps.OrderByDescending(text, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase) : apps.OrderBy(text, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
        }
        // Unknown values always remain last. Numeric sorting never compares formatted strings.
        var knownFirst = apps.OrderBy(a => Value(a).HasValue ? 0 : 1);
        return Descending ? knownFirst.ThenByDescending(a => Value(a)).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase) : knownFirst.ThenBy(a => Value(a)).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
    }
    public IEnumerable<ActivityListRow> Rows(ProcessSnapshot snapshot, IEnumerable<AppActivity>? apps = null)
    {
        foreach (var app in Sort(apps ?? snapshot.Apps))
        {
            var system = app.Name == CpuCoverage.UnattributedName;
            yield return new(app, system ? (SystemExpanded ? "\u25BC " : "\u25B6 ") + app.Name : app.Name, false);
            if (!system || !SystemExpanded) continue;
            var parts = snapshot.SystemCpuParts.Length > 0 ? snapshot.SystemCpuParts : [new CpuResidualPart("Unresolved system CPU", app.CpuPercent, "No independent breakdown is available. This CPU cannot safely be assigned to a particular driver or process.")];
            IEnumerable<CpuResidualPart> orderedParts = parts;
            if (Column == ActivityColumn.Cpu)
                orderedParts = Descending ? parts.OrderBy(p => p.CpuPercent.HasValue ? 0 : 1).ThenByDescending(p => p.CpuPercent) : parts.OrderBy(p => p.CpuPercent.HasValue ? 0 : 1).ThenBy(p => p.CpuPercent);
            foreach (var part in orderedParts)
            {
                var child = new AppActivity(part.Name, null, [], part.CpuPercent, null, null, null, "System CPU component", ActivityRules.Impact(part.CpuPercent, null, null), part.Explanation, [])
                { EnergySource = "Breakdown of the parent CPU subtotal; not additional CPU or independently measured process energy" };
                yield return new(child, "    \u21B3 " + part.Name, true);
            }
        }
    }
    private bool _headerPaintInstalled;

    public void ApplyGlyphs(DataGridView grid, IReadOnlyList<ActivityColumn> columns)
    {
        if (!_headerPaintInstalled)
        {
            _headerPaintInstalled = true;
            grid.CellPainting += (_, e) =>
            {
                if (e.RowIndex != -1 || e.ColumnIndex < 0 || e.ColumnIndex >= columns.Count || e.CellStyle == null || e.Graphics == null) return;
                var header = grid.Columns[e.ColumnIndex].HeaderCell;
                var caption = header.Tag as string ?? "";
                var font = e.CellStyle.Font ?? grid.Font;
                var active = columns[e.ColumnIndex] == Column;
                e.Paint(e.ClipBounds, e.PaintParts & ~DataGridViewPaintParts.ContentForeground);
                var padding = e.CellStyle.Padding;
                var bounds = new Rectangle(e.CellBounds.X + padding.Left, e.CellBounds.Y,
                    Math.Max(0, e.CellBounds.Width - padding.Horizontal), e.CellBounds.Height);
                var arrow = Descending ? "\u2193" : "\u2191";
                var arrowWidth = Math.Max(12, TextRenderer.MeasureText(arrow, font, Size.Empty, TextFormatFlags.NoPadding).Width);
                var textBounds = bounds;
                if (active) textBounds.Width = Math.Max(0, bounds.Width - arrowWidth - 3);
                TextRenderer.DrawText(e.Graphics, caption, font, textBounds, e.CellStyle.ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                if (active)
                {
                    var arrowBounds = new Rectangle(bounds.Right - arrowWidth, bounds.Y, arrowWidth, bounds.Height);
                    TextRenderer.DrawText(e.Graphics, arrow, font, arrowBounds, Color.FromArgb(255, 225, 80),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                e.Handled = true;
            };
        }
        for (int i = 0; i < columns.Count; i++)
        {
            grid.Columns[i].SortMode = DataGridViewColumnSortMode.Programmatic;
            var header = grid.Columns[i].HeaderCell;
            header.Tag ??= header.Value?.ToString() ?? "";
            var headerFont = grid.ColumnHeadersDefaultCellStyle.Font ?? grid.Font;
            var captionWidth = TextRenderer.MeasureText((string)header.Tag, headerFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            var arrowSpace = Math.Max(12, TextRenderer.MeasureText("\u2193", headerFont, Size.Empty, TextFormatFlags.NoPadding).Width);
            var required = captionWidth + arrowSpace + 3 + grid.ColumnHeadersDefaultCellStyle.Padding.Horizontal + 4;
            // Protect every caption, including CPU, rather than enlarging only mAh.
            grid.Columns[i].MinimumWidth = Math.Max(columns[i] == ActivityColumn.Mah ? 92 : grid.Columns[i].MinimumWidth, required);
            // Text arrows stay visible with dark headers and narrow live columns.
            header.Value = (string)header.Tag + (columns[i] == Column ? Descending ? " \u2193" : " \u2191" : "");
            header.SortGlyphDirection = SortOrder.None;
            grid.Columns[i].HeaderCell.ToolTipText = "Click to sort; click again to reverse. The selected sort is reapplied to each update.";
        }
    }
}