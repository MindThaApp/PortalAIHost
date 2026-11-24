using System.Buffers.Binary;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Portal.AiHost.Contracts;

namespace Portal.AiHost.Core.Remote;

/// <summary>Base64url (no padding) for the binary fields of the LAN protocol.</summary>
public static class RemoteCodec
{
    public static string Encode(ReadOnlySpan<byte> data) => Base64Url.EncodeToString(data);

    /// <summary>Decodes strict base64url; false for null, empty, too long or malformed text.</summary>
    public static bool TryDecode(string? text, int maxBytes, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(text) || text.Length > maxBytes / 3 * 4 + 4)
        {
            return false;
        }

        try
        {
            bytes = Base64Url.DecodeFromChars(text);
            return bytes.Length <= maxBytes;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}

/// <summary>The code-based proofs of the pairing handshake (docs/remote-v1.md section 3).</summary>
public static class PairingCrypto
{
    public const string StartLabel = "start";
    public const string HostLabel = "host";
    public const string FinishLabel = "finish";
    public const string DoneLabel = "done";

    private static readonly byte[] Salt = "portalai-pair-v2"u8.ToArray();
    private static readonly byte[] KeyInfo = "pair-mac-key"u8.ToArray();

    /// <summary>HKDF-SHA256 of the ASCII canonical code (see <see cref="Normalize"/>). 32 bytes.</summary>
    public static byte[] DeriveKey(string code) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256,
            Encoding.ASCII.GetBytes(Normalize(code) ?? throw new FormatException("Not a pairing code.")), 32, Salt, KeyInfo);

    /// <summary>
    /// HMAC-SHA256 over the label and the length-prefixed fields (4-byte big-endian length, then the bytes), so no
    /// two different field lists can give the same input.
    /// </summary>
    public static byte[] Mac(byte[] key, string label, params byte[][] fields)
    {
        using var data = new MemoryStream();
        Frame(data, Encoding.ASCII.GetBytes(label));
        foreach (var field in fields)
        {
            Frame(data, field);
        }

        return HMACSHA256.HashData(key, data.ToArray());
    }

    private static void Frame(MemoryStream stream, byte[] field)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)field.Length);
        stream.Write(length);
        stream.Write(field);
    }

    /// <summary>Crockford base32: digits and letters without I, L, O and U.</summary>
    public const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private const int PrefixChars = 8;

    /// <summary>
    /// A new code of 12 characters shown as XXXX-XXXX-XXXX: the first 8 (40 bits) are the start of the host
    /// certificate's SHA-256, the last 4 (20 bits) are random. A device that types the code checks the certificate
    /// against the first 8 before it sends anything, so an impersonator cannot collect a proof to guess from.
    /// </summary>
    public static string NewCode(byte[] fingerprint)
    {
        var chars = new char[RemoteApi.CodeLength];
        for (var i = 0; i < PrefixChars; i++)
        {
            chars[i] = CodeAlphabet[PrefixValue(fingerprint, i)];
        }

        var secret = RandomNumberGenerator.GetInt32(0, 1 << 20);
        for (var i = 0; i < 4; i++)
        {
            chars[PrefixChars + i] = CodeAlphabet[(secret >> (15 - 5 * i)) & 31];
        }

        return Display(new string(chars));
    }

    private static int PrefixValue(byte[] fingerprint, int index)
    {
        var bit = index * 5;
        var window = (fingerprint[bit / 8] << 8) | fingerprint[bit / 8 + 1];
        return (window >> (11 - bit % 8)) & 31;
    }

    /// <summary>"ABCD-EFGH-JKMN" from the 12 characters.</summary>
    public static string Display(string canonical) => $"{canonical[..4]}-{canonical[4..8]}-{canonical[8..]}";

    /// <summary>
    /// The 12 upper-case characters of a typed code: hyphens and spaces dropped, O read as 0, I and L as 1.
    /// Null when it is not a valid code.
    /// </summary>
    public static string? Normalize(string? code)
    {
        if (code is null)
        {
            return null;
        }

        var sb = new StringBuilder(RemoteApi.CodeLength);
        foreach (var raw in code)
        {
            if (raw is '-' or ' ')
            {
                continue;
            }

            var c = char.ToUpperInvariant(raw);
            c = c switch { 'O' => '0', 'I' or 'L' => '1', _ => c };
            if (CodeAlphabet.IndexOf(c) < 0)
            {
                return null;
            }

            sb.Append(c);
        }

        return sb.Length == RemoteApi.CodeLength ? sb.ToString() : null;
    }

    /// <summary>
    /// True when the first 40 bits of the code are the start of this certificate's SHA-256. A device must check this
    /// on the certificate it sees, before it sends any proof, and stop when it is false.
    /// </summary>
    public static bool CodeMatchesCertificate(string code, byte[] fingerprint)
    {
        if (Normalize(code) is not { } canonical || fingerprint.Length < 6)
        {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < PrefixChars; i++)
        {
            diff |= CodeAlphabet.IndexOf(canonical[i]) ^ PrefixValue(fingerprint, i);
        }

        return diff == 0;
    }}

/// <summary>Request signing of a paired device (docs/remote-v1.md section 4).</summary>
public static class RemoteSigning
{
    /// <summary>
    /// The text that is signed: "PORTALAI1", method, request target exactly as on the request line, Unix seconds,
    /// nonce and the lower-case hex SHA-256 of the body, one per line.
    /// </summary>
    public static byte[] SigningString(string method, string target, string timestamp, string nonce, ReadOnlySpan<byte> body)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(body));
        return Encoding.UTF8.GetBytes(
            $"{RemoteApi.SigningVersion}\n{method}\n{target}\n{timestamp}\n{nonce}\n{hash}");
    }

    /// <summary>Signs a request the way a client must. Returns the timestamp, nonce and signature header values.</summary>
    public static (string Timestamp, string Nonce, string Signature) Sign(ECDsa key, string method, string target,
        DateTimeOffset now, ReadOnlySpan<byte> body, string? nonce = null)
    {
        var timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        nonce ??= RemoteCodec.Encode(RandomNumberGenerator.GetBytes(16));
        var signature = key.SignData(SigningString(method, target, timestamp, nonce, body), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return (timestamp, nonce, RemoteCodec.Encode(signature));
    }

    /// <summary>Imports a P-256 public key (SubjectPublicKeyInfo); null when the bytes are not one.</summary>
    public static ECDsa? ImportPublicKey(byte[] spki)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(spki, out var read);
            if (read != spki.Length || key.KeySize != 256 || !IsP256(key))
            {
                key.Dispose();
                return null;
            }

            return key;
        }
        catch (CryptographicException)
        {
            key.Dispose();
            return null;
        }
        catch (ArgumentException)
        {
            key.Dispose();
            return null;
        }
    }

    private static bool IsP256(ECDsa key)
    {
        var oid = key.ExportParameters(false).Curve.Oid;
        return oid.Value == "1.2.840.10045.3.1.7" || oid.FriendlyName is "nistP256" or "ECDSA_P256" or "secp256r1";
    }
}
