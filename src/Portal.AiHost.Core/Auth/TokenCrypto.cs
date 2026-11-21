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

    public static byte[] LoadOrCreate(string dataDirectory, IHostKeyProtector protector, out bool created)
    {
        ArgumentNullException.ThrowIfNull(protector);
        var path = Path.Combine(dataDirectory, FileName);
        if (File.Exists(path))
        {
            var blob = File.ReadAllBytes(path);
            try
            {
                var key = protector.Unprotect(blob);
                if (key.Length == TokenHasher.KeyBytes)
                {
                    created = false;
                    return key;
                }

                CryptographicOperations.ZeroMemory(key);
            }
            catch (CryptographicException)
            {
            }

            // Keep the unreadable blob; the atomic write below then replaces host.key.
            AtomicFile.CopyToUniqueBackup(path, FileName + ".unreadable.bak");
        }

        var fresh = RandomNumberGenerator.GetBytes(TokenHasher.KeyBytes);
        AtomicFile.WriteAllBytes(path, protector.Protect(fresh));
        created = true;
        return fresh;
    }
}
