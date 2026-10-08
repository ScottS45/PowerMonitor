// Purpose: Exercise real grid header/row click handlers in isolated UI smoke mode.
// Author: Scott Sheen (with ChatGpt). Created/Updated: 2026-10-03.
using System.Reflection;
namespace PowerMonitor;
internal static class ActivityUiSmoke
{
    public static void Header(DataGridView grid, int column) => typeof(DataGridView).GetMethod("OnColumnHeaderMouseClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(grid, [new DataGridViewCellMouseEventArgs(column, -1, 0, 0, new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0))]);
    public static void Cell(DataGridView grid, int row) => typeof(DataGridView).GetMethod("OnCellClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(grid, [new DataGridViewCellEventArgs(0, row)]);
    public static int SystemRow(DataGridView grid) => grid.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is AppActivity a && a.Name == CpuCoverage.UnattributedName).Select(r => r.Index).DefaultIfEmpty(-1).First();
}