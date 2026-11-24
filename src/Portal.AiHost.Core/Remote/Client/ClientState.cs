using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Portal.AiHost.Core.Settings;

[assembly: InternalsVisibleTo("Portal.AiHost.Server.Tests")]

namespace Portal.AiHost.Core.Remote.Client;

/// <summary>Protects small secrets at rest for the current user (DPAPI on Windows).</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);

    /// <summary>Throws <see cref="CryptographicException"/> when the blob cannot be opened.</summary>
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>What the laptop remembers about the PC it is paired with (no secrets; docs/client-mode-v1.md section 1.5).</summary>
public sealed record ClientRemote(
    string PcName,
    string Address,
    int Port,
    string? RelayUrl,
    string Fingerprint,
    string DeviceId,
    DateTimeOffset PairedAt);

/// <summary>
/// The client's own state folder <c>client\</c> under the host state directory: <c>remote.json</c> (plain, no secrets),
/// <c>device.key</c> and <c>token.dat</c> (both protected by <see cref="ISecretProtector"/>). Every write is atomic
/// (<see cref="AtomicFile"/>); the only deletes are of these three files, and of a half-finished pairing.
/// </summary>
public sealed class ClientStateStore
{
    public const string DirectoryName = "client";
    public const string RemoteFile = "remote.json";
    public const string KeyFile = "device.key";
    public const string TokenFile = "token.dat";

    private readonly ISecretProtector _protector;
    private readonly object _gate = new();

    /// <param name="stateRoot">The host's state directory (honours the <c>--state-dir</c> override).</param>
    public ClientStateStore(string stateRoot, ISecretProtector protector)
    {
        ArgumentException.ThrowIfNullOrEmpty(stateRoot);
        Directory = Path.Combine(Path.GetFullPath(stateRoot), DirectoryName);
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public string Directory { get; }

    private string RemotePath => Path.Combine(Directory, RemoteFile);

    private string KeyPath => Path.Combine(Directory, KeyFile);

    private string TokenPath => Path.Combine(Directory, TokenFile);

    public bool IsPaired
    {
        get
        {
            lock (_gate)
            {
                return File.Exists(RemotePath) && File.Exists(KeyPath);
            }
        }
    }

    /// <summary>
    /// Stores a finished pairing: the protected key first, then <c>remote.json</c> (which marks the pairing as done). If
    /// the second write fails, the first is undone, so a failed save leaves the previous state (or nothing).
    /// </summary>
    public void SavePairing(ClientRemote remote, ReadOnlySpan<byte> devicePrivateKeyPkcs8)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var protectedKey = _protector.Protect(devicePrivateKeyPkcs8.ToArray());
        lock (_gate)
        {
            var previousKey = File.Exists(KeyPath) ? File.ReadAllBytes(KeyPath) : null;
            AtomicFile.WriteAllBytes(KeyPath, protectedKey);
            try
            {
                AtomicFile.WriteAllBytes(RemotePath, Serialize(remote));
            }
            catch
            {
                if (previousKey is null)
                {
                    File.Delete(KeyPath); // The key file this call just created.
                }
                else
                {
                    AtomicFile.WriteAllBytes(KeyPath, previousKey);
                }

                throw;
            }
        }
    }

    /// <summary>The saved pairing, or null when there is none or the file is unreadable.</summary>
    public ClientRemote? LoadRemote()
    {
        lock (_gate)
        {
            if (!File.Exists(RemotePath))
            {
                return null;
            }

            try
            {
                return Deserialize(File.ReadAllBytes(RemotePath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                or InvalidOperationException or FormatException)
            {
                return null;
            }
        }
    }

    /// <summary>The device's signing key, or null when missing or not openable by this user. Caller disposes.</summary>
    public ECDsa? LoadDeviceKey()
    {
        byte[]? plain = null;
        try
        {
            lock (_gate)
            {
                if (!File.Exists(KeyPath))
                {
                    return null;
                }

                plain = _protector.Unprotect(File.ReadAllBytes(KeyPath));
            }

            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(plain, out _);
            return key;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
        finally
        {
            if (plain is not null)
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
    }

    /// <summary>Stores the host's own access token for the PC (protected).</summary>
    public void SaveToken(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        var blob = _protector.Protect(Encoding.UTF8.GetBytes(token));
        lock (_gate)
        {
            AtomicFile.WriteAllBytes(TokenPath, blob);
        }
    }

    public string? LoadToken()
    {
        try
        {
            lock (_gate)
            {
                if (!File.Exists(TokenPath))
                {
                    return null;
                }

                var plain = _protector.Unprotect(File.ReadAllBytes(TokenPath));
                try
                {
                    return Encoding.UTF8.GetString(plain);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// "Forget this PC": deletes exactly <c>remote.json</c>, <c>device.key</c> and <c>token.dat</c> in the client folder,
    /// nothing else (no recursive delete, no other file or folder).
    /// </summary>
    public void Forget()
    {
        lock (_gate)
        {
            foreach (var path in new[] { RemotePath, KeyPath, TokenPath })
            {
                File.Delete(path); // No-op when the file does not exist.
            }
        }
    }

    private static byte[] Serialize(ClientRemote r)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("mode_hint", "client");
            w.WriteString("pc_name", r.PcName);
            w.WriteString("address", r.Address);
            w.WriteNumber("port", r.Port);
            if (r.RelayUrl is not null)
            {
                w.WriteString("relay_url", r.RelayUrl);
            }

            w.WriteString("fingerprint", r.Fingerprint);
            w.WriteString("device_id", r.DeviceId);
            w.WriteString("paired_at", r.PairedAt);
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static ClientRemote Deserialize(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        string Str(string name) => root.GetProperty(name).GetString() ?? throw new FormatException(name);
        return new ClientRemote(
            Str("pc_name"), Str("address"), root.GetProperty("port").GetInt32(),
            root.TryGetProperty("relay_url", out var relay) ? relay.GetString() : null,
            Str("fingerprint"), Str("device_id"), root.GetProperty("paired_at").GetDateTimeOffset());
    }
}
