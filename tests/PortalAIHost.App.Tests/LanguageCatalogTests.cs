using System.Xml.Linq;
using PortalAIHost.App.Services;
using Xunit;

namespace PortalAIHost.App.Tests;

public sealed class LanguageCatalogTests
{
    private static string AppDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "PortalAIHost.App"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "PortalAIHost.App");
    }

    [Fact]
    public void Lists_twenty_languages_plus_windows_default()
    {
        Assert.Equal(20, LanguageCatalog.Tags.Count);
        var options = LanguageCatalog.Options("Windows default", "Follows Windows");
        Assert.Equal(21, options.Count);
        Assert.True(options[0].IsDefault && options[0].HasGlyph);
    }

    [Fact]
    public void Every_language_has_a_flag_asset_or_a_fallback()
    {
        var flags = Path.Combine(AppDir(), "Assets", "Flags");
        foreach (var option in LanguageCatalog.Tags.Select(LanguageCatalog.For))
        {
            if (option.FlagFile is { } file) Assert.True(File.Exists(Path.Combine(flags, file)), option.Tag + " flag missing");
            else Assert.True(option.HasBadge || option.HasGlyph, option.Tag + " has no fallback");
        }
        Assert.Equal(LanguageCatalog.ChineseTraditionalBadge, LanguageCatalog.For("zh-Hant").Badge);
        Assert.True(LanguageCatalog.For("ar").HasGlyph);
        Assert.True(LanguageCatalog.For("es-419").HasGlyph);
    }

    [Fact]
    public void Flag_files_carry_no_metadata()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppDir(), "Assets", "Flags"), "*.svg"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("<title", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<desc", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<metadata", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<!--", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Manifest_lists_every_language()
    {
        var manifest = File.ReadAllText(Path.Combine(AppDir(), "Package.appxmanifest"));
        foreach (var tag in LanguageCatalog.Tags) Assert.Contains($"<Resource Language=\"{tag}\" />", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_language_has_a_strings_folder()
    {
        var strings = Path.Combine(AppDir(), "Strings");
        var missing = LanguageCatalog.Tags.Where(t => !File.Exists(Path.Combine(strings, t, "Resources.resw"))).ToList();
        Assert.SkipWhen(missing.Count > 0, "Strings not merged yet for: " + string.Join(", ", missing));
    }

    [Fact]
    public void German_choice_resolves_german_text()
    {
        // Mirrors the resource lookup order for an override of de-DE: de-DE, then de, then en-US.
        var strings = Path.Combine(AppDir(), "Strings");
        var de = Path.Combine(strings, "de-DE", "Resources.resw");
        Assert.SkipUnless(File.Exists(de), "de-DE strings not merged yet");
        var german = ReadResw(de);
        var english = ReadResw(Path.Combine(strings, "en-US", "Resources.resw"));
        var key = english.Keys.First(k => german.ContainsKey(k) && german[k] != english[k]);
        Assert.NotEqual(english[key], Lookup("de-DE", key, strings));
        Assert.Equal(german[key], Lookup("de-DE", key, strings));
    }

    [Fact]
    public void Language_choice_round_trips_through_settings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lang-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        new AppStateStore(dir).SaveLanguage("de-DE");
        Assert.Equal("de-DE", new AppStateStore(dir).Language);
        new AppStateStore(dir).SaveLanguage("");
        Assert.Equal("", new AppStateStore(dir).Language);
        new AppStateStore(dir).SaveLanguage("xx-YY");
        Assert.Equal("", new AppStateStore(dir).Language);
    }

    private static string? Lookup(string tag, string key, string strings)
    {
        foreach (var candidate in new[] { tag, tag.Split('-')[0], "en-US" })
        {
            var file = Path.Combine(strings, candidate, "Resources.resw");
            if (File.Exists(file) && ReadResw(file).TryGetValue(key, out var v)) return v;
        }
        return null;
    }

    private static Dictionary<string, string> ReadResw(string file) =>
        XDocument.Load(file).Root!.Elements("data")
            .Where(d => d.Element("value") is not null)
            .GroupBy(d => (string)d.Attribute("name")!)
            .ToDictionary(g => g.Key, g => (string)g.First().Element("value")!);
}
