using System.Text.Json;
using System.Text.Json.Serialization;
using Portal.AiHost.Core.Settings;

namespace Portal.AiHost.Core.Remote;

/// <summary>A device the user paired. PublicKey is its P-256 key (SubjectPublicKeyInfo, base64url).</summary>
public sealed class PairedDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }

    internal PairedDevice Copy() => (PairedDevice)MemberwiseClone();
}

internal sealed class DeviceFile
{
    public int SchemaVersion { get; set; } = 1;
    public List<PairedDevice> Devices { get; set; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(DeviceFile))]
internal sealed partial class RemoteJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The paired devices, kept in devices.json next to the host's other state. Writes are atomic. Only public keys are
/// stored, so the file holds nothing that lets anyone act as a device. A file that cannot be read is kept as a
/// backup and the list starts empty (devices must pair again).
/// </summary>
public sealed class DeviceStore
{
    public const string FileName = "devices.json";
    public const int MaxDevices = 16;

    private static readonly TimeSpan LastSeenWriteInterval = TimeSpan.FromMinutes(1);

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly List<PairedDevice> _devices = [];
    private DateTimeOffset _lastWrite;

    public DeviceStore(string directory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _path = Path.Combine(Path.GetFullPath(directory), FileName);
        _time = time ?? TimeProvider.System;
        Load();
    }

    /// <summary>Raised after a device was added or removed.</summary>
    public event Action? Changed;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _devices.Count;
            }
        }
    }

    public IReadOnlyList<PairedDevice> List()
    {
        lock (_gate)
        {
            return [.. _devices.Select(d => d.Copy())];
        }
    }

    public PairedDevice? Find(string? id)
    {
        lock (_gate)
        {
            return _devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal))?.Copy();
        }
    }

    /// <summary>Adds a device with a new random id and saves. Nothing is added when the save fails.</summary>
    public PairedDevice Add(string name, byte[] publicKeySpki)
    {
        PairedDevice device;
        lock (_gate)
        {
            if (_devices.Count >= MaxDevices)
            {
                throw new InvalidOperationException("Too many paired devices.");
            }

            var now = _time.GetUtcNow();
            device = new PairedDevice
            {
                Id = "dev_" + RemoteCodec.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
                Name = name,
                PublicKey = RemoteCodec.Encode(publicKeySpki),
                CreatedAt = now,
            };
            _devices.Add(device);
            try
            {
                Save(now);
            }
            catch
            {
                _devices.Remove(device);
                throw;
            }

            device = device.Copy();
        }

        Changed?.Invoke();
        return device;
    }

    /// <summary>Forgets a device. Its requests are refused from this moment. Returns false for an unknown id.</summary>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            var index = _devices.FindIndex(d => string.Equals(d.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }

            var removed = _devices[index];
            _devices.RemoveAt(index);
            try
            {
                Save(_time.GetUtcNow());
            }
            catch
            {
                // The device stays refused only if the file is rewritten; undo so what is shown matches what is saved.
                _devices.Insert(index, removed);
                throw;
            }
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Records that the device was seen. Kept in memory; written to disk at most once a minute.</summary>
    public void Touch(string id)
    {
        lock (_gate)
        {
            var device = _devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));
            if (device is null)
            {
                return;
            }

            var now = _time.GetUtcNow();
            device.LastSeenAt = now;
            if (now - _lastWrite >= LastSeenWriteInterval)
            {
                try
                {
                    Save(now);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Written with the next change.
                }
            }
        }
    }

    private void Save(DateTimeOffset now)
    {
        var file = new DeviceFile { Devices = _devices };
        AtomicFile.Write(_path, stream => JsonSerializer.Serialize(stream, file, RemoteJsonContext.Default.DeviceFile));
        _lastWrite = now;
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllBytes(_path), RemoteJsonContext.Default.DeviceFile);
            if (file is not null)
            {
                _devices.AddRange(file.Devices.Where(d => d.Id.Length > 0 && d.PublicKey.Length > 0).Take(MaxDevices));
            }
        }
        catch (JsonException)
        {
            // Unreadable: keep a copy under a new name (never over an earlier one) and start empty.
            try
            {
                AtomicFile.CopyToUniqueBackup(_path, "devices.corrupt.bak");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
