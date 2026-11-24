using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Tests.Remote;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

/// <summary>The relay bridge against a relay on a loopback port. Nothing here listens on a real network address.</summary>
public sealed class RelayBridgeTests : IAsyncLifetime
{
    private TestRoot _root = null!;
    private X509Certificate2 _cert = null!;
    private TcpListener _echo = null!;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<IAsyncDisposable> _disposables = [];

    public ValueTask InitializeAsync()
    {
        _root = new TestRoot();
        _cert = HostCertificate.LoadOrCreate(_root.Path, new TestKeyProtector());
        _echo = new TcpListener(IPAddress.Loopback, 0);
        _echo.Start();
        _ = Task.Run(EchoLoopAsync);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _echo.Stop();
        foreach (var d in Enumerable.Reverse(_disposables)) await d.DisposeAsync();
        _cert.Dispose();
        _root.Dispose();
    }

    private async Task EchoLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var c = await _echo.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(async () =>
                {
                    using var client = c;
                    var s = client.GetStream();
                    var buf = new byte[4096];
                    int n;
                    while ((n = await s.ReadAsync(buf)) > 0)
                    {
                        // A line "bye" ends the connection from this side.
                        if (Encoding.ASCII.GetString(buf, 0, n).StartsWith("bye", StringComparison.Ordinal)) return;
                        await s.WriteAsync(buf.AsMemory(0, n));
                    }
                });
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task<FakeRelay> NewRelayAsync(int port = 0)
    {
        var relay = await FakeRelay.StartAsync(port);
        _disposables.Add(relay);
        return relay;
    }

    private RelayBridge NewBridge(Uri url, TimeSpan? initial = null, TimeSpan? max = null, TimeSpan? sendTimeout = null, IPEndPoint? local = null)
    {
        var bridge = RelayBridge.Start(new RelayBridgeOptions
        {
            Url = url,
            Certificate = _cert,
            LocalTarget = local ?? (IPEndPoint)_echo.LocalEndpoint,
            InitialDelay = initial ?? TimeSpan.FromMilliseconds(50),
            MaxDelay = max ?? TimeSpan.FromMilliseconds(400),
            SendTimeout = sendTimeout ?? TimeSpan.FromSeconds(30),
        });
        _disposables.Add(bridge);
        return bridge;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for: " + what);
            await Task.Delay(20);
        }
    }

    private static async Task<WebSocketStream> DeviceAsync(FakeRelay relay, string room) =>
        await WebSocketStream.ConnectAsync(relay.Url, room, CancellationToken.None);

    [Fact]
    public void RoomIdIsTheHashOfThePinnedFingerprint()
    {
        // A device holds only the fingerprint (SHA-256 of the certificate); the room id is derived from that.
        var fp = HostCertificate.Fingerprint(_cert);
        var expected = RemoteCodec.Encode(SHA256.HashData([.. Encoding.UTF8.GetBytes("portalai-relay-v1"), .. fp]));
        Assert.Equal(expected, RelayProtocol.RoomId(fp));
        Assert.Equal(43, expected.Length);
    }

    [Fact]
    public void ProofMatchesTheVectorTheRelayTestsUse()
    {
        // Made once with the host's certificate code and checked by the relay's own tests (relay/test/fixtures).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "relay", "test", "fixtures", "dotnet-host.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "relay", "test", "fixtures", "dotnet-host.json")));
        var root = doc.RootElement;
        Assert.True(RemoteCodec.TryDecode(root.GetProperty("cert").GetString(), 2048, out var der));
        Assert.True(RemoteCodec.TryDecode(root.GetProperty("nonce").GetString(), 32, out var nonce));
        Assert.True(RemoteCodec.TryDecode(root.GetProperty("sig").GetString(), 64, out var sig));
        var room = root.GetProperty("room").GetString()!;
        Assert.Equal(room, RelayProtocol.RoomId(SHA256.HashData(der)));
        using var cert = X509CertificateLoader.LoadCertificate(der);
        using var key = cert.GetECDsaPublicKey()!;
        Assert.True(key.VerifyData(RelayProtocol.HostProofMessage(room, nonce), sig, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }
    [Fact]
    public async Task ConnectsProvesTheHostAndPipesBytesBothWays()
    {
        var relay = await NewRelayAsync();
        var bridge = NewBridge(relay.Url);
        await relay.WaitHostAsync();
        await WaitUntilAsync(() => bridge.State == RelayStates.Connected, "connected");
        Assert.Equal(bridge.RoomId, relay.Room);

        await using var device = await DeviceAsync(relay, bridge.RoomId);
        await device.WriteAsync("hello relay"u8.ToArray());
        var buf = new byte[64];
        var n = await device.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hello relay", Encoding.ASCII.GetString(buf, 0, n));
        Assert.Equal(1, bridge.StreamCount);
    }

    [Fact]
    public async Task LargeTransfersArriveInOrder()
    {
        var relay = await NewRelayAsync();
        var bridge = NewBridge(relay.Url);
        await relay.WaitHostAsync();
        await using var device = await DeviceAsync(relay, bridge.RoomId);

        var payload = RandomNumberGenerator.GetBytes(300_000);
        var send = Task.Run(async () => await device.WriteAsync(payload));
        var got = new byte[payload.Length];
        var o = 0;
        while (o < got.Length)
        {
            var n = await device.ReadAsync(got.AsMemory(o)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(n > 0);
            o += n;
        }

        await send;
        Assert.True(payload.AsSpan().SequenceEqual(got));
    }

    [Fact]
    public async Task SeveralStreamsAreIndependent()
    {
        var relay = await NewRelayAsync();
        var bridge = NewBridge(relay.Url);
        await relay.WaitHostAsync();
        await using var a = await DeviceAsync(relay, bridge.RoomId);
        await using var b = await DeviceAsync(relay, bridge.RoomId);
        await a.WriteAsync("aaa"u8.ToArray());
        await b.WriteAsync("bbbb"u8.ToArray());
        var buf = new byte[16];
        Assert.Equal("bbbb", Encoding.ASCII.GetString(buf, 0, await b.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.Equal("aaa", Encoding.ASCII.GetString(buf, 0, await a.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task WhenTheLocalSideEndsTheDeviceStreamIsClosed()
    {
        var relay = await NewRelayAsync();
        var bridge = NewBridge(relay.Url);
        await relay.WaitHostAsync();
        await using var device = await DeviceAsync(relay, bridge.RoomId);
        await device.WriteAsync("bye"u8.ToArray()); // the echo server hangs up
        var buf = new byte[16];
        Assert.Equal(0, await device.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        await WaitUntilAsync(() => bridge.StreamCount == 0, "stream removed");
    }

    [Fact]
    public async Task WhenTheDeviceLeavesTheLocalConnectionIsClosed()
    {
        var relay = await NewRelayAsync();
        var bridge = NewBridge(relay.Url);
        await relay.WaitHostAsync();
        var device = await DeviceAsync(relay, bridge.RoomId);
        await device.WriteAsync("x"u8.ToArray());
        _ = await device.ReadAsync(new byte[4]);
        Assert.Equal(1, bridge.StreamCount);
        await device.DisposeAsync();
        await WaitUntilAsync(() => bridge.StreamCount == 0, "stream removed");
    }

    [Fact]
    public async Task KeepsTryingWhileTheRelayIsDownAndConnectsWhenItReturns()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var bridge = NewBridge(new Uri($"http://127.0.0.1:{port}"));
        await WaitUntilAsync(() => bridge.State == RelayStates.Unreachable, "unreachable");

        var relay = await NewRelayAsync(port);
        await WaitUntilAsync(() => bridge.State == RelayStates.Connected, "connected after the relay came back");
        await relay.WaitHostAsync();
    }

    [Fact]
    public async Task ReconnectsAfterTheLinkDrops()
    {
        var relay = await FakeRelay.StartAsync();
        var port = relay.Port;
        var bridge = NewBridge(relay.Url);
        await relay.WaitHostAsync();
        await WaitUntilAsync(() => bridge.State == RelayStates.Connected, "connected");

        await relay.DisposeAsync();
        await WaitUntilAsync(() => bridge.State != RelayStates.Connected, "drop noticed");

        var again = await NewRelayAsync(port);
        await WaitUntilAsync(() => bridge.State == RelayStates.Connected, "connected again");
        await again.WaitHostAsync();
    }

    [Fact]
    public async Task ARefusedHostBacksOffInsteadOfHammeringTheRelay()
    {
        var relay = await NewRelayAsync();
        relay.RefuseHostWith = 4401;
        var bridge = NewBridge(relay.Url, initial: TimeSpan.FromMilliseconds(50), max: TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => bridge.State == RelayStates.Unreachable, "unreachable");
        await Task.Delay(1500);
        Assert.InRange(relay.HostConnections, 1, 3); // a refusal waits for the longest delay
        Assert.False(relay.HasHost);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"t\":5,\"room\":[],\"nonce\":{}}")]
    public async Task AMalformedChallengeEndsOnlyThatLinkAndTheBridgeKeepsTrying(string bad)
    {
        var relay = await NewRelayAsync();
        relay.SendInsteadOfChallenge(bad);
        var bridge = NewBridge(relay.Url);
        await relay.WaitHostAsync(); // the next dial gets a normal challenge
        await WaitUntilAsync(() => bridge.State == RelayStates.Connected, "connected after the bad challenge");
        await bridge.DisposeAsync();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("5")]
    public async Task AControlMessageThatIsNotAnObjectReconnectsAndDisposesCleanly(string bad)
    {
        var relay = await NewRelayAsync();
        relay.SendAfterReady(bad);
        var bridge = NewBridge(relay.Url);
        await WaitUntilAsync(() => relay.HostConnections >= 2, "second connection after the bad message");
        await WaitUntilAsync(() => bridge.State == RelayStates.Connected, "connected again");
        await bridge.DisposeAsync(); // must not throw
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"t\":[],\"s\":\"x\"}")]
    [InlineData("{\"t\":\"open\",\"s\":-1}")]
    public async Task AnObjectWithoutUsableFieldsIsIgnoredAndTheLinkStaysUp(string odd)
    {
        var relay = await NewRelayAsync();
        relay.SendAfterReady(odd);
        var bridge = NewBridge(relay.Url);
        await relay.WaitHostAsync();
        await Task.Delay(500);
        Assert.Equal(1, relay.HostConnections);
        Assert.Equal(RelayStates.Connected, bridge.State);
        await bridge.DisposeAsync();
    }

    [Fact]
    public async Task ARelayThatStopsReadingDropsTheLinkAtTheSendDeadline()
    {
        // The local side streams data endlessly and the relay never reads, so the bridge's sends must back up: no
        // device traffic is involved, so nothing else can end the stream first.
        var spew = new TcpListener(IPAddress.Loopback, 0);
        spew.Start();
        using var stop = new CancellationTokenSource();
        var accept = Task.Run(async () =>
        {
            try
            {
                using var c = await spew.AcceptTcpClientAsync(stop.Token);
                var chunk = new byte[16 * 1024];
                await using var net = c.GetStream();
                while (true) await net.WriteAsync(chunk, stop.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        });
        try
        {
            var relay = await NewRelayAsync();
            relay.StopReadingNextHost();
            var bridge = NewBridge(relay.Url, sendTimeout: TimeSpan.FromMilliseconds(500), local: (IPEndPoint)spew.LocalEndpoint);
            await relay.WaitHostAsync();
            await using var device = await DeviceAsync(relay, relay.Room!); // makes the relay open a stream
            await WaitUntilAsync(() => relay.HostConnections >= 2, "the bridge re-dialled after its send stalled", seconds: 60);
            await bridge.DisposeAsync();
        }
        finally
        {
            await stop.CancelAsync();
            spew.Stop();
            await Task.WhenAny(accept, Task.Delay(5000));
        }
    }

    [Fact]
    public async Task DisposingTheBridgeLeavesTheRelay()
    {
        var relay = await NewRelayAsync();
        var bridge = NewBridge(relay.Url);
        await relay.WaitHostAsync();
        await bridge.DisposeAsync();
        await WaitUntilAsync(() => !relay.HasHost, "host gone");
    }

    [Fact]
    public void ATestListenerCanOnlyBindLoopback()
    {
        using var host = new TestRoot();
        Assert.Throws<ArgumentException>(() => new RemoteAccess(null!, new RemoteAccessOptions
        {
            DataDirectory = host.Path,
            KeyProtector = new TestKeyProtector(),
            RevokeDevice = _ => 0,
            BindAddress = IPAddress.Any,
        }));
    }

    [Fact]
    public void RelayAddressesAreCheckedAndTheDefaultIsOnlyUsedWhenAsked()
    {
        string? Url(string? setting, bool def) => RemoteAccess.RelayUrl(new RemoteSettings { RelayUrl = setting }, def);
        Assert.Equal(RemoteApi.RelayDefaultUrl, Url(null, true));
        Assert.Null(Url(null, false));
        Assert.Equal("https://relay.example", Url("https://relay.example/ignored/path", false));
        Assert.Equal("http://127.0.0.1:8787", Url("http://127.0.0.1:8787", false));
        Assert.Null(Url("http://relay.example", false)); // plain http only for this PC
        Assert.Null(Url("https://user:pw@relay.example", false));
        Assert.Null(Url("not a url", false));
    }
}

/// <summary>The whole path: a device's TLS session through the relay to the LAN listener, with signed requests.</summary>
public sealed class RelayEndToEndTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private FakeRelay _relay = null!;
    private RemoteAccess _remote = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.StartAsync(new FakeEngineOptions { Reply = "hi there" });
        _relay = await FakeRelay.StartAsync();
        _remote = new RemoteAccess(_host.Runtime, new RemoteAccessOptions
        {
            DataDirectory = _host.RootPath,
            KeyProtector = new TestKeyProtector(),
            RevokeDevice = id => _host.Registry.RevokeDevice(id),
            BindAddress = IPAddress.Loopback,
            // The default source rule stays on: only private networks are served, so loopback is refused on the LAN
            // endpoint. The bridge's internal endpoint is the only way in for these tests.
            PortOverride = 0,
            HostName = "Desk PC",
            Addresses = () => ["192.168.1.20"],
            RelayInitialDelay = TimeSpan.FromMilliseconds(50),
            RelayMaxDelay = TimeSpan.FromMilliseconds(300),
        });
        await _remote.ApplyAsync(new RemoteSettings { Enabled = true, RelayUrl = _relay.Url.ToString() });
    }

    public async ValueTask DisposeAsync()
    {
        await _remote.DisposeAsync();
        await _relay.DisposeAsync();
        await _host.DisposeAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for: " + what);
            await Task.Delay(20);
        }
    }

    /// <summary>An HTTP client that reaches the host only through the relay and trusts only the pinned certificate.</summary>
    private HttpClient DeviceClient(string pinnedFingerprint, string room)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) => await WebSocketStream.ConnectAsync(_relay.Url, room, ct),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is not null && RemoteCodec.Encode(SHA256.HashData(cert.GetRawCertData())) == pinnedFingerprint,
            },
        };
        return new HttpClient(handler) { BaseAddress = new Uri("https://desk-pc.invalid"), Timeout = TimeSpan.FromSeconds(30) };
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static string Pad(string b64Url)
    {
        var s = b64Url.Replace('-', '+').Replace('_', '/');
        return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
    }

    private static HttpRequestMessage Signed(PairingClient client, HttpMethod method, string target, string? json = null, string? token = null)
    {
        var body = json is null ? [] : Encoding.UTF8.GetBytes(json);
        var message = new HttpRequestMessage(method, target);
        foreach (var (name, value) in client.SignedHeaders(method.Method, target, DateTimeOffset.UtcNow, body))
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        if (json is not null) message.Content = Json(json);
        if (token is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return message;
    }

    [Fact]
    public async Task APairedDeviceChatsThroughTheRelayWithTheNormalSignedRequests()
    {
        await _relay.WaitHostAsync();
        await WaitUntilAsync(() => _remote.Snapshot().Relay == RelayStates.Connected, "relay connected");

        var offer = _remote.StartPairing();
        var fingerprint = _remote.Snapshot().Fingerprint!;
        var room = RelayProtocol.RoomId(Convert.FromBase64String(Pad(fingerprint)));
        Assert.Equal(room, _relay.Room);

        using var http = DeviceClient(fingerprint, room);
        using var client = new PairingClient("Phone");
        using (var response = await http.PostAsync(RemoteApi.PairStartPath,
            Json(JsonSerializer.Serialize(client.Start(offer.Code), AiHostJsonContext.Default.PairStartRequest))))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var start = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), AiHostJsonContext.Default.PairStartResponse)!;
            Assert.True(client.VerifyHost(start, offer.Code, Convert.FromBase64String(Pad(fingerprint))));
            using var finish = await http.PostAsync(RemoteApi.PairFinishPath,
                Json(JsonSerializer.Serialize(client.Finish(offer.Code, start), AiHostJsonContext.Default.PairFinishRequest)));
            Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
            var done = JsonSerializer.Deserialize(await finish.Content.ReadAsStringAsync(), AiHostJsonContext.Default.PairFinishResponse)!;
            Assert.True(client.VerifyDone(done, offer.Code, start));
        }

        // Access request, approval on the PC, then a signed API call with the token: all inside the tunnel.
        using var create = await http.SendAsync(Signed(client, HttpMethod.Post, Routes.AuthRequests,
            """{ "app_id": "com.mindthaapp.aiportal", "app_name": "AI Portal" }"""));
        Assert.True(create.StatusCode == HttpStatusCode.Accepted, await create.Content.ReadAsStringAsync());
        var id = JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement.GetProperty("request_id").GetString()!;
        await _host.Registry.ApproveAsync(id, default);
        using var poll = await http.SendAsync(Signed(client, HttpMethod.Get, $"{Routes.AuthRequests}/{id}?wait_ms=1000"));
        Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        var token = JsonDocument.Parse(await poll.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;

        using var models = await http.SendAsync(Signed(client, HttpMethod.Get, "/v1/models", token: token));
        Assert.Equal(HttpStatusCode.OK, models.StatusCode);
        Assert.Equal("Phone", Assert.Single(_remote.Snapshot().Devices).Name);

        // An unsigned request through the tunnel is refused just like on the LAN.
        using var unsigned = await http.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
    }

    [Fact]
    public async Task ADeviceThatPinnedAnotherCertificateGetsNoSession()
    {
        await _relay.WaitHostAsync();
        var fingerprint = _remote.Snapshot().Fingerprint!;
        var room = RelayProtocol.RoomId(Convert.FromBase64String(Pad(fingerprint)));
        using var http = DeviceClient(RemoteCodec.Encode(new byte[32]), room);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("/v1/health"));
    }

    [Fact]
    public async Task TheLanEndpointStillRefusesLoopbackSources()
    {
        var snapshot = _remote.Snapshot();
        using var direct = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        });
        // The refusal happens before the TLS handshake, so the connection fails.
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => direct.GetAsync($"https://127.0.0.1:{snapshot.Port}/v1/health"));
    }

    [Fact]
    public async Task StatusFollowsTheSettings()
    {
        await WaitUntilAsync(() => _remote.Snapshot().Relay == RelayStates.Connected, "connected");

        await _remote.ApplyAsync(new RemoteSettings { Enabled = true, Relay = false, RelayUrl = _relay.Url.ToString() });
        Assert.Equal(RelayStates.Off, _remote.Snapshot().Relay);
        Assert.True(_remote.Snapshot().Listening);
        await WaitUntilAsync(() => !_relay.HasHost, "host left the relay");

        await _remote.ApplyAsync(new RemoteSettings { Enabled = true, Relay = true, RelayUrl = _relay.Url.ToString() });
        await WaitUntilAsync(() => _remote.Snapshot().Relay == RelayStates.Connected, "connected again");

        await _remote.ApplyAsync(new RemoteSettings { Enabled = false, RelayUrl = _relay.Url.ToString() });
        Assert.Equal(RelayStates.Off, _remote.Snapshot().Relay);
        await WaitUntilAsync(() => !_relay.HasHost, "host left the relay");
    }

    [Fact]
    public async Task AnInvalidRelayAddressIsReportedAndNothingConnects()
    {
        await _remote.ApplyAsync(new RemoteSettings { Enabled = true, RelayUrl = "http://relay.example" });
        var status = _remote.Snapshot();
        Assert.Equal(RelayStates.Off, status.Relay);
        Assert.Contains("relay address", status.Problem);
    }

    [Fact]
    public async Task WithoutAnAddressAndNoDefaultNothingConnects()
    {
        await _remote.ApplyAsync(new RemoteSettings { Enabled = true }); // tests never use the built-in address
        var status = _remote.Snapshot();
        Assert.Equal(RelayStates.Off, status.Relay);
        Assert.Null(status.Problem);
    }
}
