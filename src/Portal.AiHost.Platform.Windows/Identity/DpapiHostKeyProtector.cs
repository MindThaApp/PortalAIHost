using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Portal.AiHost.Core.Auth;

namespace Portal.AiHost.Platform.Windows.Identity;

/// <summary>
/// DPAPI (CurrentUser) protection for the host's HMAC key. Only the same Windows user on this PC can open
/// the blob; the fixed entropy keeps other DPAPI blobs of the user from being swapped in.
/// </summary>
public sealed class DpapiHostKeyProtector : IHostKeyProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PortalAIHost.HostKey.v1");

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return Transform(plaintext, protect: true);
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        ArgumentNullException.ThrowIfNull(protectedData);
        return Transform(protectedData, protect: false);
    }

    /// <summary>Errors that mean the blob will never open: damaged data or another user's key.</summary>
    private static bool IsDefinitive(int error) => error is
        13 // ERROR_INVALID_DATA
        or unchecked((int)0x80090005) // NTE_BAD_DATA
        or unchecked((int)0x80090003) // NTE_BAD_KEY
        or unchecked((int)0x8009000B); // NTE_BAD_KEY_STATE (another user's or a reset profile key)

    private static unsafe byte[] Transform(byte[] input, bool protect)
    {
        fixed (byte* inPtr = input)
        fixed (byte* entPtr = Entropy)
        {
            var inBlob = new IdentityNative.DataBlob { CbData = input.Length, PbData = (nint)inPtr };
            var entBlob = new IdentityNative.DataBlob { CbData = Entropy.Length, PbData = (nint)entPtr };
            var outBlob = default(IdentityNative.DataBlob);
            var ok = protect
                ? IdentityNative.CryptProtectData(&inBlob, null, &entBlob, 0, 0,
                    IdentityNative.CryptProtectUiForbidden, &outBlob)
                : IdentityNative.CryptUnprotectData(&inBlob, 0, &entBlob, 0, 0,
                    IdentityNative.CryptProtectUiForbidden, &outBlob);
            if (!ok)
            {
                var error = Marshal.GetLastPInvokeError();
                if (!protect && IsDefinitive(error))
                {
                    throw new HostKeyDamagedException("The host key blob cannot be opened by this user.",
                        new CryptographicException(error));
                }

                throw new CryptographicException(error);
            }

            try
            {
                var result = new byte[outBlob.CbData];
                new ReadOnlySpan<byte>((void*)outBlob.PbData, outBlob.CbData).CopyTo(result);
                return result;
            }
            finally
            {
                // Clear the plaintext copy DPAPI allocated before freeing it.
                new Span<byte>((void*)outBlob.PbData, outBlob.CbData).Clear();
                IdentityNative.LocalFree(outBlob.PbData);
            }
        }
    }
}
