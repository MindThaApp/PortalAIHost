using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;

namespace Portal.AiHost.Core.Settings;

/// <summary>Upgrades the settings object of an older schema version to the next one.</summary>
public interface ISettingsMigrator
{
    /// <summary>Returns the settings of version <paramref name="fromVersion"/> + 1.</summary>
    JsonNode Migrate(int fromVersion, JsonNode settings);
}

/// <summary>How the last load went, for the UI and logs.</summary>
public enum SettingsLoadState
{
    /// <summary>No file yet; defaults in use.</summary>
    Defaults,
    Loaded,

    /// <summary>An older schema was migrated; the old file was backed up first.</summary>
    Migrated,

    /// <summary>The file could not be read; it was backed up and defaults are in use.</summary>
    Corrupt,

    /// <summary>The file is from a newer host. It is left untouched and saving is refused.</summary>
    NewerSchema,

    /// <summary>The file exists but could not be opened (locked). Defaults in use, saving is refused.</summary>
    Unavailable,
}

/// <summary>
/// Typed settings file: <c>{"schema_version":N,"settings":{...}}</c>. Writes are atomic (temp, flush,
/// replace); the existing file is never truncated first. Before a migration or when the file is unreadable,
/// a copy is kept under a unique name (never overwriting an earlier backup).
/// </summary>
public sealed class SettingsStore<T>
    where T : class
{
    private readonly string _path;
    private readonly JsonTypeInfo<T> _typeInfo;
    private readonly Func<T> _defaults;
    private readonly ISettingsMigrator? _migrator;
    private readonly Lock _gate = new();
    private T _current;

    public SettingsStore(string path, JsonTypeInfo<T> typeInfo, int schemaVersion, Func<T> defaults,
        ISettingsMigrator? migrator = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        _path = Path.GetFullPath(path);
        _typeInfo = typeInfo;
        SchemaVersion = schemaVersion;
        _defaults = defaults;
        _migrator = migrator;
        _current = defaults();
    }

    public int SchemaVersion { get; }

    public string FilePath => _path;

    public SettingsLoadState LoadState { get; private set; } = SettingsLoadState.Defaults;

    /// <summary>Path of the backup made by the last load, if any.</summary>
    public string? LastBackupPath { get; private set; }

    public T Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<T>? Changed;

    /// <summary>Reads the file (or defaults). Never deletes or rewrites the user's file on its own.</summary>
    public T Load()
    {
        lock (_gate)
        {
            LastBackupPath = null;
            if (!File.Exists(_path))
            {
                LoadState = SettingsLoadState.Defaults;
                _current = _defaults();
                return _current;
            }

            byte[] raw;
            try
            {
                raw = File.ReadAllBytes(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked or unreadable: use defaults in memory, refuse to save over the file.
                LoadState = SettingsLoadState.Unavailable;
                _current = _defaults();
                return _current;
            }

            try
            {
                var root = JsonNode.Parse(raw) as JsonObject ?? throw new JsonException("Not an object.");
                var version = root["schema_version"]?.GetValue<int>() ?? throw new JsonException("No schema_version.");
                var settings = root["settings"] ?? throw new JsonException("No settings.");

                if (version > SchemaVersion)
                {
                    LoadState = SettingsLoadState.NewerSchema;
                    _current = _defaults();
                    return _current;
                }

                if (version < SchemaVersion)
                {
                    if (_migrator is null || version < 1)
                    {
                        throw new JsonException("No migration path.");
                    }

                    LastBackupPath = AtomicFile.CopyToUniqueBackup(_path,
                        Path.GetFileNameWithoutExtension(_path) + $".v{version}.bak");
                    for (var v = version; v < SchemaVersion; v++)
                    {
                        settings = _migrator.Migrate(v, settings);
                    }

                    _current = settings.Deserialize(_typeInfo) ?? _defaults();
                    WriteLocked(_current);
                    LoadState = SettingsLoadState.Migrated;
                    return _current;
                }

                _current = settings.Deserialize(_typeInfo) ?? _defaults();
                LoadState = SettingsLoadState.Loaded;
                return _current;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                // Keep the unreadable file as a backup before anything can later replace it.
                LastBackupPath = AtomicFile.CopyToUniqueBackup(_path,
                    Path.GetFileNameWithoutExtension(_path) + ".corrupt.bak");
                LoadState = SettingsLoadState.Corrupt;
                _current = _defaults();
                return _current;
            }
        }
    }

    /// <summary>Saves atomically. Throws InvalidOperationException when the file belongs to a newer schema.</summary>
    public void Save(T settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            if (LoadState is SettingsLoadState.NewerSchema or SettingsLoadState.Unavailable)
            {
                throw new InvalidOperationException(
                    "The settings file was written by a newer version or could not be read; it is not replaced.");
            }

            WriteLocked(settings);
            _current = settings;
            if (LoadState == SettingsLoadState.Defaults || LoadState == SettingsLoadState.Corrupt)
            {
                LoadState = SettingsLoadState.Loaded;
            }
        }

        Changed?.Invoke(this, settings);
    }

    private void WriteLocked(T settings)
    {
        AtomicFile.Write(_path, stream =>
        {
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", SchemaVersion);
            writer.WritePropertyName("settings");
            JsonSerializer.Serialize(writer, settings, _typeInfo);
            writer.WriteEndObject();
            writer.Flush();
        });
    }
}

/// <summary>The host's settings.json (architecture section 10).</summary>
public static class HostSettingsFile
{
    public const string FileName = "settings.json";
    public const int CurrentSchemaVersion = 1;

    public static SettingsStore<HostSettings> Create(string dataDirectory, ISettingsMigrator? migrator = null) =>
        new(Path.Combine(dataDirectory, FileName), AiHostJsonContext.Default.HostSettings, CurrentSchemaVersion,
            static () => new HostSettings(), migrator);
}
