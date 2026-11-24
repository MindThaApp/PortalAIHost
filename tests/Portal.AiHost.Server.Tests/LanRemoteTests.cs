using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Auth;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Tests.Remote;

namespace Portal.AiHost.Server.Tests;

/// <summary>The LAN listener over real TLS on loopback (the source check is switched off unless a test is about it).</summary>
public sealed class LanRemoteTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private RemoteAccess _remote = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.StartAsync();
        _remote = NewRemote(allow: _ => true);
        await _remote.ApplyAsync(new RemoteSettings { Enabled = true });
        _http = Client(_remote);
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _remote.DisposeAsync();
        await _host.DisposeAsync();
    }

    private RemoteAccess NewRemote(Func<IPAddress?, bool>? allow) => new(_host.Runtime, new RemoteAccessOptions
    {
        DataDirectory = _host.RootPath,
        KeyProtector = new TestKeyProtector(),
        RevokeDevice = id => _host.Registry.RevokeDevice(id),
        BindAddress = IPAddress.Loopback,
        AllowSource = allow,
        PortOverride = 0,
        HostName = "Desk PC",
        Addresses = () => ["192.168.1.20"],
    });

    /// <summary>Trusts only the certificate whose fingerprint the host showed (the client pins it when pairing).</summary>
    private static HttpClient Client(RemoteAccess remote)
    {
        var pinned = remote.Snapshot().Fingerprint;
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is not null && RemoteCodec.Encode(SHA256.HashData(cert.GetRawCertData())) == pinned,
            },
        };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{remote.Port}"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<(PairingClient Client, string Code, PairStartResponse Start)> StartPairingAsync(string name = "Laptop")
    {
        var offer = _remote.StartPairing();
        var client = new PairingClient(name);
        using var response = await _http.PostAsync(RemoteApi.PairStartPath,
            Json(JsonSerializer.Serialize(client.Start(offer.Code), AiHostJsonContext.Default.PairStartRequest)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var start = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), AiHostJsonContext.Default.PairStartResponse)!;
        Assert.True(client.VerifyHost(start, offer.Code, Convert.FromBase64String(Pad(offer.Fingerprint))));
        return (client, offer.Code, start);
    }

    private async Task<PairingClient> PairAsync(string name = "Laptop")
    {
        var (client, code, start) = await StartPairingAsync(name);
        using var response = await _http.PostAsync(RemoteApi.PairFinishPath,
            Json(JsonSerializer.Serialize(client.Finish(code, start), AiHostJsonContext.Default.PairFinishRequest)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var done = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), AiHostJsonContext.Default.PairFinishResponse)!;
        Assert.True(client.VerifyDone(done, code, start));
        return client;
    }

    private static string Pad(string b64Url)
    {
        var s = b64Url.Replace('-', '+').Replace('_', '/');
        return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
    }

    private static HttpRequestMessage Signed(PairingClient client, HttpMethod method, string target, string? json = null,
        string? token = null)
    {
        var body = json is null ? [] : Encoding.UTF8.GetBytes(json);
        var message = new HttpRequestMessage(method, target);
        foreach (var (name, value) in client.SignedHeaders(method.Method, target, DateTimeOffset.UtcNow, body))
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        if (json is not null)
        {
            message.Content = Json(json);
        }

        if (token is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return message;
    }

    /// <summary>The access flow from the device: request, the user approves on this PC, collect the token.</summary>
    private async Task<string> GetTokenAsync(PairingClient client, string appId = "com.mindthaapp.aiportal")
    {
        using var create = await _http.SendAsync(Signed(client, HttpMethod.Post, Routes.AuthRequests,
            $$"""{ "app_id": "{{appId}}", "app_name": "AI Portal" }"""));
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var id = (await BodyAsync(create)).GetProperty("request_id").GetString()!;

        var info = Assert.Single(_host.Registry.PendingRequests);
        Assert.Equal("AI Portal on " + client.Name, ApprovalPrompts.For(info).Title);
        Assert.StartsWith("unverified: ", ApprovalPrompts.CallerLine(info.Caller));
        await _host.Registry.ApproveAsync(id, default);

        using var poll = await _http.SendAsync(Signed(client, HttpMethod.Get, $"{Routes.AuthRequests}/{id}?wait_ms=1000"));
        Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        return (await BodyAsync(poll)).GetProperty("token").GetString()!;
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code, string? reason = null)
    {
        Assert.Equal(status, response.StatusCode);
        var error = (await BodyAsync(response)).GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        if (reason is not null)
        {
            Assert.Equal(reason, error.GetProperty("details").GetProperty("reason").GetString());
        }
    }

    [Fact]
    public async Task PairingThenUsingTheApiWorksEndToEndOverTls()
    {
        using var client = await PairAsync();

        var token = await GetTokenAsync(client);
        using var models = await _http.SendAsync(Signed(client, HttpMethod.Get, "/v1/models", token: token));
        Assert.Equal(HttpStatusCode.OK, models.StatusCode);

        var summary = Assert.Single(_remote.Snapshot().Devices);
        Assert.Equal("Laptop", summary.Name);
        Assert.NotNull(summary.LastSeenAt);
        var registered = Assert.Single(await _host.Registry.ListAsync(default));
        Assert.Equal(client.DeviceId, registered.DeviceId);
    }

    [Fact]
    public async Task TheOfferCarriesTheQrTextTheClientScans()
    {
        var offer = _remote.StartPairing();
        Assert.Equal($"portalai://pair?h=192.168.1.20&p={_remote.Port}&f={offer.Fingerprint}&c={PairingCrypto.Normalize(offer.Code)}&n=Desk%20PC", offer.QrText);
        Assert.Equal(offer.Code, _remote.Snapshot().Pairing!.Code);
    }

    [Fact]
    public async Task AnImpersonatingCertificateIsRejectedBeforeAnyProofIsSent()
    {
        var offer = _remote.StartPairing();

        // The device typed a code whose first 40 bits are not the start of this certificate's hash (as with a code
        // for the real host seen on an impersonator's certificate). It refuses the certificate during the handshake,
        // so the proof is never sent.
        var canonical = PairingCrypto.Normalize(offer.Code)!;
        var foreign = PairingCrypto.Display((canonical[0] == '0' ? "1" : "0") + canonical[1..]);
        using var device = new PairingClient("Laptop");
        var attempts = RemoteApi.MaxWrongAttempts + 2;
        for (var i = 0; i < attempts; i++)
        {
            using var refusing = new HttpClient(new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, cert, _, _) =>
                        cert is not null && PairingCrypto.CodeMatchesCertificate(foreign, SHA256.HashData(cert.GetRawCertData())),
                },
            })
            { BaseAddress = new Uri($"https://127.0.0.1:{_remote.Port}") };
            await Assert.ThrowsAsync<HttpRequestException>(() => refusing.PostAsync(RemoteApi.PairStartPath,
                Json(JsonSerializer.Serialize(device.Start(foreign), AiHostJsonContext.Default.PairStartRequest))));
        }

        // Nothing reached the host, so the real code was not used up: the right device still pairs.
        using var good = new PairingClient("Laptop 2");
        using var accepting = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is not null && PairingCrypto.CodeMatchesCertificate(offer.Code, SHA256.HashData(cert.GetRawCertData())),
            },
        })
        { BaseAddress = new Uri($"https://127.0.0.1:{_remote.Port}") };
        using var response = await accepting.PostAsync(RemoteApi.PairStartPath,
            Json(JsonSerializer.Serialize(good.Start(offer.Code), AiHostJsonContext.Default.PairStartRequest)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
    [Fact]
    public async Task WrongCodeIsRefusedAndTheFifthMistakeVoidsTheCode()
    {
        var offer = _remote.StartPairing();
        var canonical = PairingCrypto.Normalize(offer.Code)!;
        var wrong = PairingCrypto.Display(canonical[..^1] + (canonical[^1] == '0' ? '1' : '0'));
        using var attacker = new PairingClient("Attacker");
        for (var i = 1; i < RemoteApi.MaxWrongAttempts; i++)
        {
            using var r = await _http.PostAsync(RemoteApi.PairStartPath,
                Json(JsonSerializer.Serialize(attacker.Start(wrong), AiHostJsonContext.Default.PairStartRequest)));
            await AssertErrorAsync(r, HttpStatusCode.Forbidden, ErrorCodes.PairingFailed, PairingReasons.WrongCode);
        }

        using var fifth = await _http.PostAsync(RemoteApi.PairStartPath,
            Json(JsonSerializer.Serialize(attacker.Start(wrong), AiHostJsonContext.Default.PairStartRequest)));
        await AssertErrorAsync(fifth, HttpStatusCode.Forbidden, ErrorCodes.PairingFailed, PairingReasons.CodeVoided);

        using var owner = new PairingClient("Owner");
        using var late = await _http.PostAsync(RemoteApi.PairStartPath,
            Json(JsonSerializer.Serialize(owner.Start(offer.Code), AiHostJsonContext.Default.PairStartRequest)));
        await AssertErrorAsync(late, HttpStatusCode.Forbidden, ErrorCodes.PairingFailed, PairingReasons.CodeVoided);
        Assert.Empty(_remote.Devices.List());
    }

    [Fact]
    public async Task RequestsWithoutAValidDeviceSignatureGet401()
    {
        using var client = await PairAsync();
        var token = await GetTokenAsync(client);

        using var unsigned = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        unsigned.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var noSignature = await _http.SendAsync(unsigned);
        await AssertErrorAsync(noSignature, HttpStatusCode.Unauthorized, ErrorCodes.DeviceUnauthorized, DeviceAuthReasons.Malformed);

        using var stranger = new PairingClient("Stranger");
        var forged = Signed(stranger, HttpMethod.Get, "/v1/models", token: token);
        forged.Headers.Remove(RemoteHeaders.Device);
        forged.Headers.TryAddWithoutValidation(RemoteHeaders.Device, client.DeviceId);
        using var badSignature = await _http.SendAsync(forged);
        await AssertErrorAsync(badSignature, HttpStatusCode.Unauthorized, ErrorCodes.DeviceUnauthorized, DeviceAuthReasons.BadSignature);

        var unknown = Signed(client, HttpMethod.Get, "/v1/models", token: token);
        unknown.Headers.Remove(RemoteHeaders.Device);
        unknown.Headers.TryAddWithoutValidation(RemoteHeaders.Device, "dev_nobody");
        using var unknownDevice = await _http.SendAsync(unknown);
        await AssertErrorAsync(unknownDevice, HttpStatusCode.Unauthorized, ErrorCodes.DeviceUnauthorized, DeviceAuthReasons.UnknownDevice);
    }

    [Fact]
    public async Task AReplayedSignedRequestIsRefused()
    {
        using var client = await PairAsync();
        var first = Signed(client, HttpMethod.Get, "/host/ping");
        using var ok = await _http.SendAsync(first);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var replay = new HttpRequestMessage(HttpMethod.Get, "/host/ping");
        foreach (var name in new[] { RemoteHeaders.Device, RemoteHeaders.Timestamp, RemoteHeaders.Nonce, RemoteHeaders.Signature })
        {
            replay.Headers.TryAddWithoutValidation(name, first.Headers.GetValues(name));
        }

        using var again = await _http.SendAsync(replay);
        await AssertErrorAsync(again, HttpStatusCode.Unauthorized, ErrorCodes.DeviceUnauthorized, DeviceAuthReasons.Replayed);
    }

    [Fact]
    public async Task ATokenBelongsToOneDevice()
    {
        using var laptop = await PairAsync("Laptop");
        using var phone = await PairAsync("Phone");
        var laptopToken = await GetTokenAsync(laptop);

        using var otherDevice = await _http.SendAsync(Signed(phone, HttpMethod.Get, "/v1/models", token: laptopToken));
        await AssertErrorAsync(otherDevice, HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);

        // A token issued on this PC (loopback) does not work from a device either.
        var localToken = await _host.GetTokenAsync("com.mindthaapp.voiceportal");
        using var fromDevice = await _http.SendAsync(Signed(laptop, HttpMethod.Get, "/v1/models", token: localToken));
        await AssertErrorAsync(fromDevice, HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);

        // And a device token does not work on the loopback listener.
        using var onLoopback = await _host.Http.SendAsync(_host.Authed(HttpMethod.Get, "/v1/models", laptopToken));
        Assert.Equal(HttpStatusCode.Unauthorized, onLoopback.StatusCode);
    }

    [Fact]
    public async Task RemovingADeviceRevokesItAtOnce()
    {
        using var client = await PairAsync();
        var token = await GetTokenAsync(client);
        using (var before = await _http.SendAsync(Signed(client, HttpMethod.Get, "/v1/models", token: token)))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        Assert.True(_remote.RemoveDevice(client.DeviceId!));

        using var after = await _http.SendAsync(Signed(client, HttpMethod.Get, "/v1/models", token: token));
        await AssertErrorAsync(after, HttpStatusCode.Unauthorized, ErrorCodes.DeviceUnauthorized, DeviceAuthReasons.UnknownDevice);
        Assert.True(Assert.Single(await _host.Registry.ListAsync(default)).Revoked);
        Assert.Empty(_remote.Devices.List());
        Assert.False(_remote.RemoveDevice(client.DeviceId!));
    }

    [Fact]
    public async Task BrowserRequestsAreRefused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/host/ping");
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        using var response = await _http.SendAsync(request);
        await AssertErrorAsync(response, HttpStatusCode.Forbidden, ErrorCodes.ForbiddenOrigin);
    }

    [Fact]
    public async Task OnlyPrivateNetworkSourcesAreServedByDefault()
    {
        await using var strict = NewRemote(allow: null);
        await strict.ApplyAsync(new RemoteSettings { Enabled = true });
        Assert.True(strict.Listening);
        using var http = Client(strict);

        // The test connects from 127.0.0.1, which is not a home network address: dropped before the TLS handshake.
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("/host/ping"));
    }

    [Fact]
    public async Task TurnedOffMeansNothingListensAndNoCodeCanBeOpened()
    {
        var port = _remote.Port;
        await _remote.ApplyAsync(new RemoteSettings { Enabled = false });

        Assert.False(_remote.Listening);
        Assert.Throws<InvalidOperationException>(() => _remote.StartPairing());
        using var closed = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        });
        await Assert.ThrowsAsync<HttpRequestException>(() => closed.GetAsync($"https://127.0.0.1:{port}/host/ping"));

        await _remote.ApplyAsync(new RemoteSettings { Enabled = true });
        Assert.True(_remote.Listening);
    }

    [Fact]
    public async Task TheCertificateAndPairedDevicesSurviveARestart()
    {
        using var client = await PairAsync();
        var fingerprint = _remote.Snapshot().Fingerprint;
        await _remote.DisposeAsync();

        _remote = NewRemote(allow: _ => true);
        await _remote.ApplyAsync(new RemoteSettings { Enabled = true });
        _http.Dispose();
        _http = Client(_remote);

        Assert.Equal(fingerprint, _remote.Snapshot().Fingerprint);
        using var ok = await _http.SendAsync(Signed(client, HttpMethod.Get, "/host/ping"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }
}
