using System.Text;

namespace PortalAIHost.App.Services;

/// <summary>Makes English text longer and accented so clipped or missing strings show up. Keeps {0} placeholders intact.</summary>
public static class PseudoLoc
{
    private const string From = "AEIOUaeiouCcNnSsYyZz";
    private const string To = "ÀÉÎÕÛàéîõûÇçÑñŠšÝýŽž";

    public static string Convert(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder("[");
        var inBrace = false;
        foreach (var ch in text)
        {
            if (ch == '{') inBrace = true;
            var i = inBrace ? -1 : From.IndexOf(ch);
            sb.Append(i >= 0 ? To[i] : ch);
            if (ch == '}') inBrace = false;
        }
        sb.Append('~', Math.Max(1, text.Length / 3));
        sb.Append(']');
        return sb.ToString();
    }
}

/// <summary>Number and size text used by several pages.</summary>
public static class Fmt
{
    public static string Bytes(long bytes)
    {
        const double gb = 1024.0 * 1024 * 1024;
        const double mb = 1024.0 * 1024;
        return bytes >= gb ? $"{bytes / gb:0.#} GB" : $"{Math.Max(0, bytes) / mb:0} MB";
    }

    /// <summary>The model list scores quality from 0 to 100; people see 0 to 5 stars.</summary>
    public static double StarScale(double score100) => Math.Clamp(score100 / 20.0, 0, 5);

    /// <summary>
    /// Shows a path without the Windows user name: the start that belongs to the user profile becomes
    /// %LOCALAPPDATA%, %APPDATA% or %USERPROFILE%. For display only; actions keep the real path.
    /// </summary>
    public static string PrivatePath(string? path, Func<string, string?>? env = null)
    {
        if (string.IsNullOrEmpty(path))
            return path ?? "";
        env ??= Environment.GetEnvironmentVariable;
        foreach (var name in new[] { "LOCALAPPDATA", "APPDATA", "USERPROFILE" })
        {
            var root = env(name)?.TrimEnd('\\', '/');
            if (string.IsNullOrEmpty(root))
                continue;
            if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
                return "%" + name + "%";
            if (path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                return "%" + name + "%" + path[root.Length..];
        }

        return path;
    }

    public static string Stars(double quality)
    {
        var n = (int)Math.Round(Math.Clamp(quality, 0, 5));
        return new string('★', n) + new string('☆', 5 - n);
    }
}
