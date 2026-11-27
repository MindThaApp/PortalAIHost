using System.Text.Json;
using System.Text.Json.Serialization;

namespace PortalAIHost.App.Services;

/// <summary>
/// Remembers small window facts in one file, app-state.json, inside a folder the app owns.
/// It only ever writes that one fixed file name; it never deletes or moves anything.
/// </summary>
public sealed class AppStateStore : IAppState
{
    public const string FileName = "app-state.json";
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public AppStateStore(string folder)
    {
        _path = Path.Combine(folder, FileName);
        var d = Read();
        FirstRunDone = d.FirstRunDone;
        Appearance = d.Appearance ?? AppearancePrefs.Default;
        LastSeenVersion = d.LastSeenVersion;
        SendCrashReports = d.SendCrashReports ?? true;
        Language = LanguageCatalog.Normalize(d.Language);
    }

    public bool FirstRunDone { get; private set; }
    public AppearancePrefs Appearance { get; private set; }
    public string? LastSeenVersion { get; private set; }
    public bool SendCrashReports { get; private set; } = true;

    /// <summary>Chosen app language tag; empty follows Windows.</summary>
    public string Language { get; private set; } = "";

    public void SaveLanguage(string tag)
    {
        Language = LanguageCatalog.Normalize(tag);
        Write();
    }

    public void SaveSendCrashReports(bool on)
    {
        SendCrashReports = on;
        Write();
    }

    public void MarkFirstRunDone()
    {
        FirstRunDone = true;
        Write();
    }

    public void SaveAppearance(AppearancePrefs prefs)
    {
        Appearance = prefs;
        Write();
    }

    public void MarkVersionSeen(string version)
    {
        LastSeenVersion = version;
        Write();
    }

    private sealed class Data
    {
        public bool FirstRunDone { get; set; }
        public AppearancePrefs? Appearance { get; set; }
        public string? LastSeenVersion { get; set; }
        public bool? SendCrashReports { get; set; }
        public string? Language { get; set; }
    }

    private void Write()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var data = new Data { FirstRunDone = FirstRunDone, Appearance = Appearance, LastSeenVersion = LastSeenVersion, SendCrashReports = SendCrashReports, Language = Language };
            File.WriteAllText(_path, JsonSerializer.Serialize(data, Json));
        }
        catch (IOException)
        {
            // Not being able to remember this only means the welcome screen or a choice shows once more.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private Data Read()
    {
        try
        {
            if (!File.Exists(_path)) return new Data();
            var text = File.ReadAllText(_path);
            // Older files only held {"firstRunDone":true}; missing fields keep their defaults.
            return JsonSerializer.Deserialize<Data>(text, new JsonSerializerOptions(Json) { PropertyNameCaseInsensitive = true }) ?? new Data();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new Data();
        }
    }
}
