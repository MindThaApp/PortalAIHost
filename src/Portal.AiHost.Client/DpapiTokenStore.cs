using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Portal.AiHost.Client;

/// <summary>
/// Stores the token encrypted with Windows DPAPI (current user) in one file inside a folder the app owns.
/// It only ever writes or deletes its own <c>*.token</c> file for this app id (and a temporary file beside it).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class DpapiTokenStore : ITokenStore
{
    private const int UiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("portal-ai-host-token-v1");
    private readonly string _path;

    /// <summary>Creates the store.</summary>
    /// <param name="folder">The app's own data folder; created if missing.</param>
    /// <param name="appId">The app id; one token file per app id.</param>
    public DpapiTokenStore(string folder, string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        _path = Path.Combine(Path.GetFullPath(folder), "portal-ai-host-" + Sanitize(appId) + ".token");
    }

    /// <summary>The full path of the token file (it may not exist).</summary>
    public string FilePath => _path;

    /// <inheritdoc />
    public async Task<string?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var protectedBytes = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            var plain = Transform(protectedBytes, protect: false);
            return plain is null ? null : Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var data = Transform(Encoding.UTF8.GetBytes(token), protect: true)
            ?? throw new IOException("DPAPI could not protect the token.");
        var temp = _path + ".tmp";
        await File.WriteAllBytesAsync(temp, data, cancellationToken).ConfigureAwait(false);
        File.Move(temp, _path, overwrite: true); // replaces only this app's own token file
    }

    /// <inheritdoc />
    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        try
        {
            File.Delete(_path); // exactly this app's token file; no wildcards, no directories
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort; a stuck file is replaced by the next token
        }

        return Task.CompletedTask;
    }

    private static string Sanitize(string appId)
    {
        var sb = new StringBuilder(appId.Length);
        foreach (var c in appId)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        }

        return sb.ToString();
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DataBlob dataIn, nint description, ref DataBlob entropy,
        nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DataBlob dataIn, nint description, ref DataBlob entropy,
        nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint handle);

    private static unsafe byte[]? Transform(byte[] input, bool protect)
    {
        var source = input.Length == 0 ? new byte[1] : input;
        fixed (byte* pin = source)
        fixed (byte* pent = Entropy)
        {
            var blobIn = new DataBlob { Size = input.Length, Data = (nint)pin };
            var blobEntropy = new DataBlob { Size = Entropy.Length, Data = (nint)pent };
            DataBlob outBlob;
            var ok = protect
                ? CryptProtectData(ref blobIn, 0, ref blobEntropy, 0, 0, UiForbidden, out outBlob)
                : CryptUnprotectData(ref blobIn, 0, ref blobEntropy, 0, 0, UiForbidden, out outBlob);
            if (!ok)
            {
                return null;
            }

            try
            {
                var result = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
                return result;
            }
            finally
            {
                LocalFree(outBlob.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }
}
