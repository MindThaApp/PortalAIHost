using System.Security.Cryptography;
using System.Text;

namespace Portal.AiHost.Core.Remote;

/// <summary>The relay's identity rules (docs/remote-v1.md, section Relay). Pure functions; no network.</summary>
public static class RelayProtocol
{
    public const string RoomPrefix = "portalai-relay-v1";
    public const string HostProofPrefix = "portalai-relay-v1-host";

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
    public static byte[] HostProofMessage(string roomId, ReadOnlySpan<byte> nonce)
    {
        var head = Encoding.UTF8.GetBytes($"{HostProofPrefix}\n{roomId}\n");
        var data = new byte[head.Length + nonce.Length];
        head.CopyTo(data, 0);
        nonce.CopyTo(data.AsSpan(head.Length));
        return data;
    }

    /// <summary>ECDSA P-256 over SHA-256, 64 bytes (r then s).</summary>
    public static byte[] SignHostProof(ECDsa key, string roomId, ReadOnlySpan<byte> nonce) =>
        key.SignData(HostProofMessage(roomId, nonce), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
}
