namespace PortalAIHost.App.Services;

/// <summary>A rectangle in screen pixels.</summary>
public readonly record struct Box(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// Where the tray pop-up opens: toward the screen interior from the icon, on the icon's monitor, a margin away from
/// the taskbar and the work-area edges. The taskbar side is read from the gap between the monitor and its work area.
/// </summary>
public static class TrayPlacement
{
    /// <param name="icon">Where the icon was clicked, in screen pixels.</param>
    /// <param name="monitor">Full bounds of the icon's monitor.</param>
    /// <param name="work">Work area of that monitor (without the taskbar).</param>
    /// <param name="width">Window width in pixels.</param>
    /// <param name="height">Window height in pixels.</param>
    /// <param name="margin">Gap to the taskbar and work-area edges in pixels (8 DIP).</param>
    public static (int X, int Y) Place((int X, int Y) icon, Box monitor, Box work, int width, int height, int margin)
    {
        var w = Math.Min(width, Math.Max(1, work.Width - 2 * margin));
        var h = Math.Min(height, Math.Max(1, work.Height - 2 * margin));
        int x, y;
        if (work.Bottom < monitor.Bottom || work.Y > monitor.Y || (work.X == monitor.X && work.Width == monitor.Width))
        {
            // Taskbar on the bottom or the top: open above or below it, centred on the icon.
            x = icon.X - w / 2;
            y = work.Y > monitor.Y ? work.Y + margin : work.Bottom - h - margin;
        }
        else if (work.X > monitor.X)
        {
            // Taskbar on the left: open to its right.
            x = work.X + margin;
            y = icon.Y - h / 2;
        }
        else
        {
            // Taskbar on the right: open to its left.
            x = work.Right - w - margin;
            y = icon.Y - h / 2;
        }

        x = Math.Clamp(x, work.X + margin, Math.Max(work.X + margin, work.Right - w - margin));
        y = Math.Clamp(y, work.Y + margin, Math.Max(work.Y + margin, work.Bottom - h - margin));
        return (x, y);
    }
}
