using System.Text.Json;
using Portal.AiHost.Core.Settings;

namespace Portal.AiHost.Core.Remote.Client;

/// <summary>
/// Which PC model each local app asks for (docs/client-mode-v1.md 4.7): <c>client\apps.json</c>, a flat map of app id to
/// model id. Missing means "the PC's choice". Every save is one atomic write of this file; nothing is ever deleted
/// (clearing a choice rewrites the file without it). A damaged file reads as empty.
/// </summary>
public sealed class AppModelStore
{
    public const string FileName = "apps.json";
    public const int MaxIdLength = 200;

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, string> _map;

    public AppModelStore(string clientDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientDirectory);
        _path = Path.Combine(Path.GetFullPath(clientDirectory), FileName);
        _map = Load();
    }

    /// <summary>Raised after a choice changed.</summary>
    public event Action? Changed;

    public string? Get(string appId)
    {
        lock (_gate)
        {
            return _map.TryGetValue(appId, out var model) ? model : null;
        }
    }

    public IReadOnlyDictionary<string, string> All()
    {
        lock (_gate)
        {
            return new Dictionary<string, string>(_map, StringComparer.Ordinal);
        }
    }

    /// <summary>Chooses a PC model for an app, or clears the choice when <paramref name="modelId"/> is null or empty.</summary>
    public bool Set(string appId, string? modelId)
    {
        if (!IsValid(appId) || (!string.IsNullOrEmpty(modelId) && !IsValid(modelId)))
        {
            return false;
        }

        lock (_gate)
        {
            var next = new Dictionary<string, string>(_map, StringComparer.Ordinal);
            if (string.IsNullOrEmpty(modelId))
            {
                next.Remove(appId);
            }
            else
            {
                next[appId] = modelId;
            }

            Save(next);
            _map = next;
        }

        Changed?.Invoke();
        return true;
    }

    private static bool IsValid(string value) =>
        value.Length is > 0 and <= MaxIdLength && !value.Any(char.IsControl);

    private Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(_path))
            {
                return map;
            }

            using var doc = JsonDocument.Parse(File.ReadAllBytes(_path));
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } v && IsValid(p.Name) && IsValid(v))
                    {
                        map[p.Name] = v;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            map.Clear();
        }

        return map;
    }

    private void Save(Dictionary<string, string> map)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (var (app, model) in map.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                w.WriteString(app, model);
            }

            w.WriteEndObject();
        }

        // New temp file, flushed, then swapped in; the old file is never truncated.
        AtomicFile.WriteAllBytes(_path, stream.ToArray());
    }
}
