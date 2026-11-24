using System.Text;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Tests.Support;

namespace Portal.AiHost.Core.Tests.Remote;

public sealed class RequestSigningTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTimeProvider _time = new();
    private readonly DeviceStore _devices;
    private readonly DeviceRequestVerifier _verifier;
    private readonly PairingClient _client = new("Laptop");

    public RequestSigningTests()
    {
        _devices = new DeviceStore(_dir.Path, _time);
        _verifier = new DeviceRequestVerifier(_devices, _time);
        var pairing = new PairingService(_devices, "Desk PC", new byte[32], _time);
        _client.Pair(pairing, pairing.Begin().Code);
    }

    public void Dispose()
    {
        _client.Dispose();
        _dir.Dispose();
    }

    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"model":"x"}""");

    private Dictionary<string, string> Headers(string method = "POST", string target = "/v1/chat/completions",
        string? nonce = null, TimeSpan? offset = null) =>
        _client.SignedHeaders(method, target, _time.GetUtcNow() + (offset ?? TimeSpan.Zero), Body, nonce: nonce);

    [Fact]
    public void ASignedRequestFromAPairedDeviceIsAccepted()
    {
        var result = _verifier.Verify("POST", "/v1/chat/completions", Headers(), Body);

        Assert.True(result.IsValid);
        Assert.Equal(_client.DeviceId, result.Device!.Id);
        Assert.Equal("Laptop", result.Device.Name);
    }

    [Fact]
    public void LastSeenIsRecorded()
    {
        _verifier.Verify("GET", "/v1/models", _client.SignedHeaders("GET", "/v1/models", _time.GetUtcNow(), []), []);
        Assert.NotNull(_devices.Find(_client.DeviceId)!.LastSeenAt);
    }

    [Fact]
    public void ChangedBodyMethodOrTargetBreaksTheSignature()
    {
        var headers = Headers();
        Assert.Equal(DeviceAuthReasons.BadSignature,
            _verifier.Verify("POST", "/v1/chat/completions", headers, Encoding.UTF8.GetBytes("""{"model":"y"}""")).FailureReason);
        Assert.Equal(DeviceAuthReasons.BadSignature,
            _verifier.Verify("PUT", "/v1/chat/completions", headers, Body).FailureReason);
        Assert.Equal(DeviceAuthReasons.BadSignature,
            _verifier.Verify("POST", "/v1/embeddings", headers, Body).FailureReason);
        Assert.Equal(DeviceAuthReasons.BadSignature,
            _verifier.Verify("POST", "/v1/chat/completions?x=1", headers, Body).FailureReason);
    }

    [Fact]
    public void ASignatureFromAnotherKeyIsRefused()
    {
        using var stranger = new PairingClient("Stranger");
        var forged = stranger.SignedHeaders("POST", "/v1/chat/completions", _time.GetUtcNow(), Body, deviceId: _client.DeviceId);

        Assert.Equal(DeviceAuthReasons.BadSignature, _verifier.Verify("POST", "/v1/chat/completions", forged, Body).FailureReason);
    }

    [Fact]
    public void ReplayedRequestIsRefused()
    {
        var headers = Headers();
        Assert.True(_verifier.Verify("POST", "/v1/chat/completions", headers, Body).IsValid);

        Assert.Equal(DeviceAuthReasons.Replayed, _verifier.Verify("POST", "/v1/chat/completions", headers, Body).FailureReason);
    }

    [Fact]
    public void ANonceMayBeReusedOnlyAfterItsRequestCouldNoLongerBeAccepted()
    {
        var first = Headers(nonce: "AAAAAAAAAAAAAAAAAAAAAA");
        Assert.True(_verifier.Verify("POST", "/v1/chat/completions", first, Body).IsValid);

        _time.Advance(TimeSpan.FromMinutes(10));
        var again = Headers(nonce: "AAAAAAAAAAAAAAAAAAAAAA");
        Assert.True(_verifier.Verify("POST", "/v1/chat/completions", again, Body).IsValid);
    }

    [Fact]
    public void TimeSkewOverTwoMinutesIsRefusedInBothDirections()
    {
        Assert.True(_verifier.Verify("POST", "/v1/chat/completions", Headers(offset: TimeSpan.FromSeconds(RemoteApi.MaxClockSkewSeconds - 1)), Body).IsValid);
        Assert.True(_verifier.Verify("POST", "/v1/chat/completions", Headers(offset: -TimeSpan.FromSeconds(RemoteApi.MaxClockSkewSeconds - 1)), Body).IsValid);
        Assert.Equal(DeviceAuthReasons.StaleTimestamp,
            _verifier.Verify("POST", "/v1/chat/completions", Headers(offset: TimeSpan.FromSeconds(RemoteApi.MaxClockSkewSeconds + 5)), Body).FailureReason);
        Assert.Equal(DeviceAuthReasons.StaleTimestamp,
            _verifier.Verify("POST", "/v1/chat/completions", Headers(offset: -TimeSpan.FromSeconds(RemoteApi.MaxClockSkewSeconds + 5)), Body).FailureReason);
    }

    [Fact]
    public void RemovedDeviceIsRefusedAtOnce()
    {
        var before = Headers();
        Assert.True(_verifier.Verify("POST", "/v1/chat/completions", before, Body).IsValid);

        Assert.True(_devices.Remove(_client.DeviceId!));

        Assert.Equal(DeviceAuthReasons.UnknownDevice,
            _verifier.Verify("POST", "/v1/chat/completions", Headers(), Body).FailureReason);
    }

    [Fact]
    public void UnknownDeviceAndMissingOrBrokenHeadersAreRefused()
    {
        var unknown = Headers();
        unknown[RemoteHeaders.Device] = "dev_nobody";
        Assert.Equal(DeviceAuthReasons.UnknownDevice, _verifier.Verify("POST", "/v1/chat/completions", unknown, Body).FailureReason);

        foreach (var header in new[] { RemoteHeaders.Device, RemoteHeaders.Timestamp, RemoteHeaders.Nonce, RemoteHeaders.Signature })
        {
            var missing = Headers();
            missing.Remove(header);
            Assert.Equal(DeviceAuthReasons.Malformed, _verifier.Verify("POST", "/v1/chat/completions", missing, Body).FailureReason);
        }

        var badNonce = Headers();
        badNonce[RemoteHeaders.Nonce] = "short";
        Assert.Equal(DeviceAuthReasons.Malformed, _verifier.Verify("POST", "/v1/chat/completions", badNonce, Body).FailureReason);

        var badTime = Headers();
        badTime[RemoteHeaders.Timestamp] = "-5";
        Assert.Equal(DeviceAuthReasons.Malformed, _verifier.Verify("POST", "/v1/chat/completions", badTime, Body).FailureReason);

        var badSignature = Headers();
        badSignature[RemoteHeaders.Signature] = "AAAA";
        Assert.Equal(DeviceAuthReasons.Malformed, _verifier.Verify("POST", "/v1/chat/completions", badSignature, Body).FailureReason);
    }

    [Fact]
    public void ABrokenRequestDoesNotUseUpTheNonce()
    {
        var headers = Headers();
        var tampered = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        Assert.False(_verifier.Verify("POST", "/v1/models", tampered, Body).IsValid);

        Assert.True(_verifier.Verify("POST", "/v1/chat/completions", headers, Body).IsValid);
    }
}
