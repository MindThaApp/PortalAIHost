using System.Security.Cryptography;
using System.Text;

namespace Portal.AiHost.Core.Remote;

/// <summary>The relay's identity rules (docs/remote-v1.md, section Relay). Pure functions; no network.</summary>
public static class RelayProtocol
{
    public const string RoomPrefix = "portalai-relay-v1";
    public const string HostProofPrefix = "portalai-relay-v1-host";
    public const string AdmissionPrefix = "portalai-relay-v1-admit";

    /// <summary>Request header a device presents its admission token in (never a URL query string).</summary>
    public const string AdmissionHeader = "X-Portal-Admission";

    /// <summary>Bytes of an admission token.</summary>
    public const int AdmissionTokenBytes = 32;

    /// <summary>A new random admission token. Whoever holds it may open streams to this PC through the relay.</summary>
    public static byte[] NewAdmissionToken() => RandomNumberGenerator.GetBytes(AdmissionTokenBytes);

    /// <summary>SHA-256("portalai-relay-v1-admit\n" || token): the only form the relay ever sees in the host's hello.</summary>
    public static byte[] AdmissionHash(ReadOnlySpan<byte> token)
    {
        var prefix = Encoding.UTF8.GetBytes(AdmissionPrefix + "\n");
        var data = new byte[prefix.Length + token.Length];
        prefix.CopyTo(data, 0);
        token.CopyTo(data.AsSpan(prefix.Length));
        return SHA256.HashData(data);
    }

    /// <summary>Room id = base64url(SHA-256("portalai-relay-v1" || SHA-256(certificate DER))). A device derives it from the pinned fingerprint.</summary>
    public static string RoomId(ReadOnlySpan<byte> fingerprint)
    {
        var prefix = Encoding.UTF8.GetBytes(RoomPrefix);
        var data = new byte[prefix.Length + fingerprint.Length];
        prefix.CopyTo(data, 0);
        fingerprint.CopyTo(data.AsSpan(prefix.Length));
        return RemoteCodec.Encode(SHA256.HashData(data));
    }

    /// <summary>The bytes the host signs: "portalai-relay-v1-host\n" + room id + "\n", then the nonce bytes.</summary>
    public static byte[] HostProofMessage(string roomId, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> admissionHash = default)
    {
        var head = Encoding.UTF8.GetBytes($"{HostProofPrefix}\n{roomId}\n");
        var data = new byte[head.Length + nonce.Length + admissionHash.Length];
        head.CopyTo(data, 0);
        nonce.CopyTo(data.AsSpan(head.Length));
        admissionHash.CopyTo(data.AsSpan(head.Length + nonce.Length)); // signed too, when published
        return data;
    }

    /// <summary>ECDSA P-256 over SHA-256, 64 bytes (r then s).</summary>
    public static byte[] SignHostProof(ECDsa key, string roomId, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> admissionHash = default) =>
        key.SignData(HostProofMessage(roomId, nonce, admissionHash), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
}
