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

    public static string Stars(double quality)
    {
        var n = (int)Math.Round(Math.Clamp(quality, 0, 5));
        return new string('★', n) + new string('☆', 5 - n);
    }
}
