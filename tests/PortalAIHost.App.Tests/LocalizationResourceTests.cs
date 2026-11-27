using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace PortalAIHost.App.Tests;

public class LocalizationResourceTests
{
    private static readonly string StringsDir = FindStringsDir();

    private static string FindStringsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PortalAIHost.App", "Strings");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Strings folder not found above the test output.");
    }

    private static Dictionary<string, string> Load(string lang)
    {
        var path = Path.Combine(StringsDir, lang, "Resources.resw");
        var doc = XDocument.Load(path);
        return doc.Root!.Elements("data").ToDictionary(
            d => (string)d.Attribute("name")!,
            d => (string?)d.Element("value") ?? "");
    }

    private static string Placeholders(string value) =>
        string.Join(",", Regex.Matches(value, @"\{\d+\}").Select(m => m.Value).Distinct().OrderBy(x => x, StringComparer.Ordinal));

    public static IEnumerable<object[]> Languages() =>
        Directory.GetDirectories(StringsDir)
            .Select(Path.GetFileName)
            .Where(n => n is not null && !n.Equals("en-US", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("qps", StringComparison.OrdinalIgnoreCase)
                        && File.Exists(Path.Combine(StringsDir, n, "Resources.resw")))
            .Select(n => new object[] { n! });

    [Fact]
    public void RequiredLanguagesExist()
    {
        foreach (var lang in new[] { "de-DE", "fr-FR", "ar-SA" })
            Assert.True(File.Exists(Path.Combine(StringsDir, lang, "Resources.resw")), lang + " is missing");
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void TranslationMatchesEnglish(string lang)
    {
        var en = Load("en-US");
        var tr = Load(lang);
        Assert.Equal(en.Keys.OrderBy(k => k, StringComparer.Ordinal), tr.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (key, value) in tr)
        {
            Assert.False(string.IsNullOrWhiteSpace(value), lang + ": empty value for " + key);
            Assert.True(Placeholders(en[key]) == Placeholders(value), lang + ": placeholders differ for " + key);
        }
    }
}
