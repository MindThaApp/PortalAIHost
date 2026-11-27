namespace PortalAIHost.App.Services;

/// <summary>One row of the language picker. Tag is empty for "Windows default".</summary>
public sealed record LanguageOption(string Tag, string NativeName, string EnglishName, string? FlagFile, string? Badge)
{
    public bool IsDefault => Tag.Length == 0;
    public bool HasFlag => FlagFile is not null;
    public bool HasBadge => FlagFile is null && Badge is not null;
    public bool HasGlyph => FlagFile is null && Badge is null;
    public string FlagUri => FlagFile is null ? "" : "ms-appx:///Assets/Flags/" + FlagFile;
}

/// <summary>The languages the app ships, with the flag (or fallback) shown next to each.</summary>
public static class LanguageCatalog
{
    public const string ChineseTraditionalBadge = "繁";

    public static readonly IReadOnlyList<string> Tags =
    [
        "en-US", "de-DE", "fr-FR", "es-ES", "it-IT", "pt-BR", "ja-JP", "zh-Hans", "zh-Hant", "ko-KR",
        "ru-RU", "pl-PL", "nl-NL", "tr-TR", "ar-SA", "hi-IN", "id-ID", "vi-VN", "th-TH", "sv-SE",
    ];

    private static readonly Dictionary<string, (string Native, string English)> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en-US"] = ("English (United States)", "English"),
        ["de-DE"] = ("Deutsch", "German"),
        ["fr-FR"] = ("Français", "French"),
        ["es-ES"] = ("Español", "Spanish"),
        ["it-IT"] = ("Italiano", "Italian"),
        ["pt-BR"] = ("Português (Brasil)", "Portuguese (Brazil)"),
        ["ja-JP"] = ("日本語", "Japanese"),
        ["zh-Hans"] = ("简体中文", "Chinese (Simplified)"),
        ["zh-Hant"] = ("繁體中文", "Chinese (Traditional)"),
        ["ko-KR"] = ("한국어", "Korean"),
        ["ru-RU"] = ("Русский", "Russian"),
        ["pl-PL"] = ("Polski", "Polish"),
        ["nl-NL"] = ("Nederlands", "Dutch"),
        ["tr-TR"] = ("Türkçe", "Turkish"),
        ["ar-SA"] = ("العربية", "Arabic"),
        ["hi-IN"] = ("हिन्दी", "Hindi"),
        ["id-ID"] = ("Bahasa Indonesia", "Indonesian"),
        ["vi-VN"] = ("Tiếng Việt", "Vietnamese"),
        ["th-TH"] = ("ไทย", "Thai"),
        ["sv-SE"] = ("Svenska", "Swedish"),
    };

    private static readonly Dictionary<string, string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en-US"] = "us", ["de-DE"] = "de", ["fr-FR"] = "fr", ["es-ES"] = "es", ["it-IT"] = "it",
        ["pt-BR"] = "br", ["ja-JP"] = "jp", ["zh-Hans"] = "cn", ["ko-KR"] = "kr", ["ru-RU"] = "ru",
        ["pl-PL"] = "pl", ["nl-NL"] = "nl", ["tr-TR"] = "tr", ["ar-SA"] = "sa", ["hi-IN"] = "in",
        ["id-ID"] = "id", ["vi-VN"] = "vn", ["th-TH"] = "th", ["sv-SE"] = "se",
    };

    /// <summary>Flag file name for a tag, or null when the row shows a badge or a globe instead
    /// (zh-Hant, a bare "ar", es-419, Windows default, anything unknown).</summary>
    public static string? FlagFileFor(string tag) => Flags.TryGetValue(tag, out var code) ? code + ".svg" : null;

    public static string? BadgeFor(string tag) =>
        string.Equals(tag, "zh-Hant", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("zh-Hant-", StringComparison.OrdinalIgnoreCase)
            ? ChineseTraditionalBadge
            : null;

    public static LanguageOption For(string tag)
    {
        var (native, english) = Names.TryGetValue(tag, out var n) ? n : (tag, tag);
        return new LanguageOption(tag, native, english, FlagFileFor(tag), BadgeFor(tag));
    }

    /// <summary>"Windows default" first, then the shipped languages.</summary>
    public static IReadOnlyList<LanguageOption> Options(string windowsDefaultLabel, string windowsDefaultSecondary) =>
        [new LanguageOption("", windowsDefaultLabel, windowsDefaultSecondary, null, null), .. Tags.Select(For)];

    /// <summary>Maps a stored override to a shipped tag; empty means follow Windows.</summary>
    public static string Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "";
        var hit = Tags.FirstOrDefault(t => string.Equals(t, tag.Trim(), StringComparison.OrdinalIgnoreCase));
        return hit ?? "";
    }
}
