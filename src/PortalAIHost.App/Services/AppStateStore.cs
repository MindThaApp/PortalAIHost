using System.Text.Json;

namespace PortalAIHost.App.Services;

/// <summary>
/// Remembers small window facts in one file, app-state.json, inside a folder the app owns.
/// It only ever writes that one fixed file name; it never deletes or moves anything.
/// </summary>
public sealed class AppStateStore : IAppState
{
    public const string FileName = "app-state.json";
    private readonly string _path;

    public AppStateStore(string folder)
    {
        _path = Path.Combine(folder, FileName);
        FirstRunDone = Read();
    }

    public bool FirstRunDone { get; private set; }

    public void MarkFirstRunDone()
    {
        FirstRunDone = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, "{\"firstRunDone\":true}");
        }
        catch (IOException)
        {
            // Not being able to remember this only means the welcome screen shows once more.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private bool Read()
    {
        try
        {
            if (!File.Exists(_path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            return doc.RootElement.TryGetProperty("firstRunDone", out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
