using System.Text.Json;
using Portal.AiHost.Core.Settings;

namespace PortalAIHost.App.Services;

/// <summary>
/// Remembers the dismissed update version in update-nudge.json inside the app's own state folder. It only ever writes that one
/// fixed file name, atomically (temp file, then replace); it never deletes or moves anything.
/// </summary>
public sealed class UpdateNudgeStore : IUpdateNudgeState
{
    public const string FileName = "update-nudge.json";

    private readonly string _path;
    private readonly object _gate = new();

    public UpdateNudgeStore(string folder)
    {
        _path = Path.Combine(folder, FileName);
        DismissedVersion = Read();
    }

    public string? DismissedVersion { get; private set; }

    public void SaveDismissed(string version)
    {
        lock (_gate)
        {
            DismissedVersion = version;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(new Data { DismissedVersion = version }));
        }
    }

    private sealed class Data
    {
        public string? DismissedVersion { get; set; }
    }

    private string? Read()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize<Data>(File.ReadAllBytes(_path))?.DismissedVersion;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
