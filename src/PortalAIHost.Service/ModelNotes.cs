using System.Text.Json;
using Portal.AiHost.Core.Settings;

namespace PortalAIHost.Service;

/// <summary>What is kept in model-notes.json: only the host's own notes about models, never model files.</summary>
internal sealed class ModelNotesData
{
    /// <summary>Model id to the last time an engine loaded it (UTC).</summary>
    public Dictionary<string, DateTimeOffset> LastUsed { get; set; } = new();

    /// <summary>Model id to why a check marked it damaged.</summary>
    public Dictionary<string, string> Damaged { get; set; } = new();
}

/// <summary>
/// The host's small notebook about installed models: when each was last loaded and which ones a check found
/// damaged. A single JSON file in the host's data folder; losing it loses only those notes.
/// </summary>
internal sealed class ModelNotes
{
    private readonly object _sync = new();
    private readonly string? _path;
    private ModelNotesData _data = new();

    public ModelNotes(string? path)
    {
        _path = path;
        if (path is not null && File.Exists(path))
        {
            try
            {
                _data = JsonSerializer.Deserialize(File.ReadAllBytes(path), ServiceJsonContext.Default.ModelNotesData) ?? new();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _data = new();
            }
        }
    }

    public DateTimeOffset? LastUsed(string id)
    {
        lock (_sync)
        {
            return _data.LastUsed.TryGetValue(id, out var t) ? t : null;
        }
    }

    public string? DamagedReason(string id)
    {
        lock (_sync)
        {
            return _data.Damaged.GetValueOrDefault(id);
        }
    }

    public void MarkUsed(string id, DateTimeOffset when)
    {
        lock (_sync)
        {
            _data.LastUsed[id] = when;
            Save();
        }
    }

    public void MarkDamaged(string id, string reason)
    {
        lock (_sync)
        {
            _data.Damaged[id] = reason;
            Save();
        }
    }

    /// <summary>Drops the damaged mark (after a clean check) and, when asked, the whole entry of a removed model.</summary>
    public void ClearDamaged(string id, bool forgetUsage = false)
    {
        lock (_sync)
        {
            var changed = _data.Damaged.Remove(id);
            if (forgetUsage)
            {
                changed |= _data.LastUsed.Remove(id);
            }

            if (changed)
            {
                Save();
            }
        }
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            AtomicFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(_data, ServiceJsonContext.Default.ModelNotesData));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Notes are a convenience; a full disk must not break model handling.
        }
    }
}
