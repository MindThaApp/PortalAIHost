using System.Security.Cryptography;
using System.Text;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Remote;

namespace Portal.AiHost.Core.Tests.Remote;

/// <summary>A client as docs/remote-v1.md describes it: keeps a P-256 key, proves the code, signs requests.</summary>
public sealed class PairingClient : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _clientNonce = RandomNumberGenerator.GetBytes(16);

    public PairingClient(string name = "Laptop") => Name = name;

    public string Name { get; }

    public string? DeviceId { get; private set; }

    public byte[] PublicKey => _key.ExportSubjectPublicKeyInfo();

    public ECDsa Key => _key;

    public PairStartRequest Start(string code)
    {
        var mac = PairingCrypto.Mac(PairingCrypto.DeriveKey(code), PairingCrypto.StartLabel,
            _clientNonce, Encoding.UTF8.GetBytes(Name), PublicKey);
        return new PairStartRequest
        {
            ClientNonce = RemoteCodec.Encode(_clientNonce),
            DeviceName = Name,
            DevicePublicKey = RemoteCodec.Encode(PublicKey),
            Mac = RemoteCodec.Encode(mac),
        };
    }

    /// <summary>True when the host proved it knows the code and names this fingerprint.</summary>
    public bool VerifyHost(PairStartResponse response, string code, byte[]? pinnedFingerprint = null)
    {
        var fingerprint = Convert.FromBase64String(Pad(response.Fingerprint));
        if (pinnedFingerprint is not null && !fingerprint.AsSpan().SequenceEqual(pinnedFingerprint))
        {
            return false;
        }

        var expected = PairingCrypto.Mac(PairingCrypto.DeriveKey(code), PairingCrypto.HostLabel,
            _clientNonce, Encoding.UTF8.GetBytes(Name), PublicKey, Decode(response.HostNonce), fingerprint);
        return CryptographicOperations.FixedTimeEquals(expected, Decode(response.Mac));
    }

    public PairFinishRequest Finish(string code, PairStartResponse response)
    {
        var mac = PairingCrypto.Mac(PairingCrypto.DeriveKey(code), PairingCrypto.FinishLabel,
            _clientNonce, Encoding.UTF8.GetBytes(Name), PublicKey, Decode(response.HostNonce), Decode(response.Fingerprint));
        return new PairFinishRequest { HostNonce = response.HostNonce, Mac = RemoteCodec.Encode(mac) };
    }

    public bool VerifyDone(PairFinishResponse done, string code, PairStartResponse start)
    {
        var expected = PairingCrypto.Mac(PairingCrypto.DeriveKey(code), PairingCrypto.DoneLabel,
            _clientNonce, Encoding.UTF8.GetBytes(Name), PublicKey, Decode(start.HostNonce), Decode(start.Fingerprint),
            Encoding.ASCII.GetBytes(done.DeviceId));
        var ok = CryptographicOperations.FixedTimeEquals(expected, Decode(done.Mac));
        if (ok)
        {
            DeviceId = done.DeviceId;
        }

        return ok;
    }

    /// <summary>Runs both steps against a service, as a client that knows the code. Returns the device id.</summary>
    public string Pair(PairingService service, string code)
    {
        var start = service.Start(Start(code));
        Assert.True(VerifyHost(start, code));
        var done = service.Finish(Finish(code, start));
        Assert.True(VerifyDone(done, code, start));
        return done.DeviceId;
    }

    /// <summary>The four signed-request headers for a request.</summary>
    public Dictionary<string, string> SignedHeaders(string method, string target, DateTimeOffset now,
        ReadOnlySpan<byte> body, string? deviceId = null, string? nonce = null)
    {
        var (timestamp, n, signature) = RemoteSigning.Sign(_key, method, target, now, body, nonce);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [RemoteHeaders.Device] = deviceId ?? DeviceId!,
            [RemoteHeaders.Timestamp] = timestamp,
            [RemoteHeaders.Nonce] = n,
            [RemoteHeaders.Signature] = signature,
        };
    }

    private static byte[] Decode(string text) => Convert.FromBase64String(Pad(text));

    private static string Pad(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
    }

    public void Dispose() => _key.Dispose();
}
