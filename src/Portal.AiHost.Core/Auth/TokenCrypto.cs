using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Settings;

namespace Portal.AiHost.Core.Auth;

/// <summary>
/// Protects the host's HMAC key at rest. Windows uses DPAPI (CurrentUser); tests use a fake.
/// Unprotect throws <see cref="CryptographicException"/> when the blob cannot be opened.
/// </summary>
public interface IHostKeyProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] protectedData);
}

/// <summary>
/// Thrown by a protector when the blob can definitely never be opened (another user's key, or damaged data).
/// Only this leads to a new host key; any other failure is treated as temporary.
/// </summary>
public sealed class HostKeyDamagedException : CryptographicException
{
    public HostKeyDamagedException(string message, Exception? inner = null) : base(message, inner) { }

    public HostKeyDamagedException() : base("The host key cannot be opened.") { }

    public HostKeyDamagedException(string message) : base(message) { }
}

/// <summary>Token text format: "pah_" + 43 base64url characters (32 random bytes).</summary>
public static class TokenFormat
{
    public const int RandomBytes = 32;
    public const int EncodedLength = 43;
    public static readonly int TotalLength = ApiInfo.TokenPrefix.Length + EncodedLength;

    /// <summary>Creates a new random token. The caller must hand it out once and never store it.</summary>
    public static string NewToken()
    {
        Span<byte> raw = stackalloc byte[RandomBytes];
        RandomNumberGenerator.Fill(raw);
        var token = ApiInfo.TokenPrefix + Base64Url.EncodeToString(raw);
        CryptographicOperations.ZeroMemory(raw);
        return token;
    }

    /// <summary>Shape check only (prefix, length, alphabet); says nothing about validity.</summary>
    public static bool IsWellFormed(string? token)
    {
        if (token is null || token.Length != TotalLength ||
            !token.StartsWith(ApiInfo.TokenPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = ApiInfo.TokenPrefix.Length; i < token.Length; i++)
        {
            var c = token[i];
            var ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Random identifier with a prefix, for example "tk_" or "ar_".</summary>
    public static string NewId(string prefix, int bytes)
    {
        Span<byte> raw = stackalloc byte[bytes];
        RandomNumberGenerator.Fill(raw);
        return prefix + Convert.ToHexStringLower(raw);
    }
}

/// <summary>
/// Keyed HMAC-SHA256 of tokens. Only these digests are stored; the key itself is kept
/// DPAPI-protected in <see cref="HostKeyFile"/>.
/// </summary>
public sealed class TokenHasher : IDisposable
{
    public const int KeyBytes = 32;
    private readonly byte[] _key;
    private bool _disposed;

    public TokenHasher(ReadOnlySpan<byte> key)
    {
        if (key.Length < KeyBytes)
        {
            throw new ArgumentException("The host key is too short.", nameof(key));
        }

        _key = key.ToArray();
    }

    public byte[] Hash(string token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var bytes = Encoding.UTF8.GetBytes(token);
        try
        {
            return HMACSHA256.HashData(_key, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>Constant-time comparison of two digests.</summary>
    public static bool DigestEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        CryptographicOperations.FixedTimeEquals(a, b);

    public void Dispose()
    {
        if (!_disposed)
        {
            CryptographicOperations.ZeroMemory(_key);
            _disposed = true;
        }
    }
}

/// <summary>
/// The host key file (<c>host.key</c>): 32 random bytes, stored only in protected form.
/// If the file cannot be opened (another user, a reinstall, damage), a copy is kept under a unique backup
/// name and a new key is made. Existing tokens then stop matching and clients get token_invalid.
/// </summary>
public static class HostKeyFile
{
    public const string FileName = "host.key";

    /// <summary>
    /// Loads the key, or makes one when there is none. A blob that is definitely unusable (wrong user, damaged,
    /// wrong length) is backed up under a unique name and replaced. A locked file or a temporary DPAPI failure
    /// is retried and then thrown, so the caller can report "unavailable" and keep every token.
    /// </summary>
    public static byte[] LoadOrCreate(string dataDirectory, IHostKeyProtector protector, out bool created,
        int attempts = 3, TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(protector);
        var delay = retryDelay ?? TimeSpan.FromMilliseconds(50);
        var path = Path.Combine(dataDirectory, FileName);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    break;
                }

                var blob = File.ReadAllBytes(path);
                byte[] key;
                try
                {
                    key = protector.Unprotect(blob);
                }
                catch (HostKeyDamagedException)
                {
                    key = [];
                }

                if (key.Length == TokenHasher.KeyBytes)
                {
                    created = false;
                    return key;
                }

                // Definitely unusable: keep a copy, then the atomic write below replaces host.key.
                CryptographicOperations.ZeroMemory(key);
                AtomicFile.CopyToUniqueBackup(path, FileName + ".unreadable.bak");
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException
                                       && ex is not HostKeyDamagedException && attempt < attempts)
            {
                if (delay > TimeSpan.Zero)
                {
                    Thread.Sleep(delay);
                }
            }
        }

        var fresh = RandomNumberGenerator.GetBytes(TokenHasher.KeyBytes);
        AtomicFile.WriteAllBytes(path, protector.Protect(fresh));
        created = true;
        return fresh;
    }
}
