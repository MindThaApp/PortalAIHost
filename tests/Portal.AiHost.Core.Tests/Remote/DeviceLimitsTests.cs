using System.Text;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Tests.Support;

namespace Portal.AiHost.Core.Tests.Remote;

public sealed class DeviceLimitsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTimeProvider _time = new();
    private readonly DeviceStore _devices;
    private readonly PairingService _pairing;
    private readonly List<PairingClient> _clients = [];

    public DeviceLimitsTests()
    {
        _devices = new DeviceStore(_dir.Path, _time);
        _pairing = new PairingService(_devices, "Desk PC", new byte[32], _time);
    }

    public void Dispose()
    {
        foreach (var c in _clients) c.Dispose();
        _dir.Dispose();
    }

    private PairingClient NewDevice(string name)
    {
        var client = new PairingClient(name);
        _clients.Add(client);
        client.Pair(_pairing, _pairing.Begin().Code);
        return client;
    }

    private DeviceAuthResult Send(DeviceRequestVerifier verifier, PairingClient client, string? nonce = null) =>
        verifier.Verify("GET", "/v1/models", client.SignedHeaders("GET", "/v1/models", _time.GetUtcNow(), [], nonce: nonce), []);

    private static string Nonce(int n) => Convert.ToBase64String(Encoding.ASCII.GetBytes($"nonce-{n:D10}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void ADeviceOverItsRateIsAnswered429OthersAreNotAffectedAndItRecoversAfterTheWindow()
    {
        var verifier = new DeviceRequestVerifier(_devices, _time, new DeviceRequestLimits(MaxRequestsPerMinute: 3, MaxNoncesPerDevice: 1000));
        var busy = NewDevice("Busy");
        var calm = NewDevice("Calm");

        for (var i = 0; i < 3; i++) Assert.True(Send(verifier, busy).IsValid);
        var refused = Send(verifier, busy);
        Assert.Equal(DeviceAuthReasons.RateLimited, refused.FailureReason);
        Assert.True(refused.RetryAfterMs > 0);

        Assert.True(Send(verifier, calm).IsValid);

        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.True(Send(verifier, busy).IsValid);
    }

    [Fact]
    public void ANonceBudgetRefusesInsteadOfDroppingAndFreesUpOnlyByExpiry()
    {
        var verifier = new DeviceRequestVerifier(_devices, _time, new DeviceRequestLimits(MaxRequestsPerMinute: 10_000, MaxNoncesPerDevice: 3));
        var device = NewDevice("Flooder");
        var other = NewDevice("Other");

        for (var i = 0; i < 3; i++) Assert.True(Send(verifier, device, Nonce(i)).IsValid);

        // Full: new requests are refused, and the oldest records are still there to catch a replay.
        Assert.Equal(DeviceAuthReasons.RateLimited, Send(verifier, device, Nonce(99)).FailureReason);
        Assert.Equal(DeviceAuthReasons.Replayed, Send(verifier, device, Nonce(0)).FailureReason);

        // One device filling its budget does not lock out the others.
        Assert.True(Send(verifier, other).IsValid);

        // Records leave only when they expire (2 x skew + 60 s).
        _time.Advance(TimeSpan.FromSeconds(2 * RemoteApi.MaxClockSkewSeconds + 30));
        Assert.Equal(DeviceAuthReasons.RateLimited, Send(verifier, device, Nonce(100)).FailureReason);
        _time.Advance(TimeSpan.FromSeconds(40));
        Assert.True(Send(verifier, device, Nonce(101)).IsValid);
        Assert.True(Send(verifier, device, Nonce(0)).IsValid);
    }

    [Fact]
    public void RequestsThatFailTheSignatureNeverUseUpADevicesAllowance()
    {
        var verifier = new DeviceRequestVerifier(_devices, _time, new DeviceRequestLimits(MaxRequestsPerMinute: 2, MaxNoncesPerDevice: 2));
        var device = NewDevice("Laptop");
        using var stranger = new PairingClient("Stranger");

        for (var i = 0; i < 20; i++)
        {
            var forged = stranger.SignedHeaders("GET", "/v1/models", _time.GetUtcNow(), [], deviceId: device.DeviceId);
            Assert.Equal(DeviceAuthReasons.BadSignature, verifier.Verify("GET", "/v1/models", forged, []).FailureReason);
        }

        Assert.True(Send(verifier, device).IsValid);
        Assert.True(Send(verifier, device).IsValid);
    }

    [Fact]
    public void ManyRequestsAreHandledWithoutScanningTheWholeCache()
    {
        var verifier = new DeviceRequestVerifier(_devices, _time, new DeviceRequestLimits(MaxRequestsPerMinute: 1_000_000, MaxNoncesPerDevice: 20_000));
        var device = NewDevice("Laptop");
        var started = DateTime.UtcNow;
        for (var i = 0; i < 2000; i++)
        {
            Assert.True(Send(verifier, device, Nonce(i)).IsValid);
            _time.Advance(TimeSpan.FromMilliseconds(500));
        }

        // Only the last few minutes are remembered: old nonces were forgotten from the front of the queue.
        Assert.True(Send(verifier, device, Nonce(0)).IsValid);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
    }
}
