using System.Globalization;

namespace PortalAIHost.App.Services;

/// <summary>Reads the <c>--tray-popup=x,y</c> switch the host passes when a tray click starts the window.</summary>
public static class TrayPopupArgs
{
    public const string Switch = "--tray-popup=";

    /// <summary>The icon position in screen pixels, or null when the text has no valid switch.</summary>
    public static (int X, int Y)? Find(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) return null;
        var start = commandLine.IndexOf(Switch, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var rest = commandLine[(start + Switch.Length)..];
        var end = rest.IndexOfAny([' ', '"', '\t']);
        var value = end < 0 ? rest : rest[..end];
        var parts = value.Split(',');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
            ? (x, y)
            : null;
    }
}
