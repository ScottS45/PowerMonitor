// ============================================================================
// File: AppIcons.cs
// Author: Scott Sheen (with ChatGpt)
// Created: 2026-10-02
// Updated: 2026-10-02
// Purpose: Load independently owned window and tray icons from embedded resources.
// ============================================================================

namespace PowerMonitor;

public static class AppIcons
{

    public static Icon Load(int size)
    {
        // Clone the embedded icon so the caller owns its lifetime independently of the resource stream.
        using var stream = typeof(AppIcons).Assembly.GetManifestResourceStream("PowerMonitor.Assets.PowerMonitor.ico") ?? throw new InvalidOperationException("The embedded application icon is missing.");
        using var source = new Icon(stream, size, size);
        return (Icon)source.Clone();
    }
}
