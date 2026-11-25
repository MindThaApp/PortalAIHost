namespace PortalAIHost.App.Services;

/// <summary>
/// The page a launch asks for: <c>--page=models</c>, or <c>--page=settings/performance</c> for a section of a page.
/// Only known names are accepted; anything else opens the home page.
/// </summary>
public static class PageArgs
{
    public const string Switch = "--page=";

    private static readonly string[] Known = ["home", "models", "apps", "devices", "tryit", "settings", "about", "whatsnew", "approvals"];

    /// <summary>The page text from a command line (or redirected activation text), or null.</summary>
    public static string? Find(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) return null;
        var start = commandLine.IndexOf(Switch, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var rest = commandLine[(start + Switch.Length)..];
        var end = rest.IndexOfAny([' ', '"', '\t']);
        var value = end < 0 ? rest : rest[..end];
        return value.Length == 0 ? null : value;
    }

    /// <summary>Splits "settings/performance" into the page name and the section; the page is "home" when unknown.</summary>
    public static (string Page, string? Section) Split(string tag)
    {
        var slash = tag.IndexOf('/');
        var page = (slash < 0 ? tag : tag[..slash]).ToLowerInvariant();
        var section = slash < 0 || slash == tag.Length - 1 ? null : tag[(slash + 1)..].ToLowerInvariant();
        return Array.IndexOf(Known, page) >= 0 ? (page, section) : ("home", null);
    }
}
