using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Remote.Client;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

/// <summary>Sends LAN connections to a closed port while <see cref="Down"/> is set (a laptop away from home).</summary>
internal sealed class SwitchableLan(int deadPort) : IRemoteConnector
{
    private readonly LanConnector _inner = new(TimeSpan.FromSeconds(2));

    public volatile bool Down;

    public IRemoteChannel Open(string address, int port, Func<byte[], bool> acceptCertificate) =>
        _inner.Open(address, Down ? deadPort : port, acceptCertificate);
}

/// <summary>The laptop's link over the relay route: a real host behind a loopback relay, a closed port as the "far away" LAN.</summary>
public sealed class RelayRouteTests : IAsyncLifetime
{
    private readonly StepTime _time = new(DateTimeOffset.UtcNow);
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "pah-link-relay-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _links = [];
    private TestHost _host = null!;
    private FakeRelay _relay = null!;
    private RemoteAccess _remote = null!;
    private SwitchableLan _lan = null!;
    private int _relayFactoryCalls;

    public async ValueTask InitializeAsync()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var deadPort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        _lan = new SwitchableLan(deadPort);
        _host = await TestHost.StartAsync(new FakeEngineOptions());
        _relay = await FakeRelay.StartAsync();
        _remote = new RemoteAccess(_host.Runtime, new RemoteAccessOptions
        {
            DataDirectory = _host.RootPath,
            KeyProtector = new TestKeyProtector(),
            RevokeDevice = id => _host.Registry.RevokeDevice(id),
            BindAddress = IPAddress.Loopback,
            AllowSource = _ => true,
            PortOverride = 0,
            HostName = "Desk PC",
            Addresses = () => ["192.168.1.20"],
            RelayInitialDelay = TimeSpan.FromMilliseconds(50),
            RelayMaxDelay = TimeSpan.FromMilliseconds(300),
        });
        await _remote.ApplyAsync(new RemoteSettings { Enabled = true, RelayUrl = _relay.Url.ToString() });
        await _relay.WaitHostAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var link in _links)
        {
            link.Dispose();
        }

        await _remote.DisposeAsync();
        await _relay.DisposeAsync();
        await _host.DisposeAsync();
        if (Directory.Exists(_stateRoot)) // Test-owned temp folder.
        {
            Directory.Delete(_stateRoot, recursive: true);
        }
    }

    private async Task<ClientStateStore> PairAsync(bool withRelay = true)
    {
        var store = new ClientStateStore(_stateRoot, new XorSecretProtector());
        var offer = _remote.StartPairing();
        var text = RemoteNetwork.QrText("127.0.0.1", _remote.Port, offer.Fingerprint, PairingCrypto.Normalize(offer.Code)!, "Desk PC",
            withRelay ? _relay.Url.ToString() : null);
        Assert.True(PairingLink.TryParse(text, out var link));
        var outcome = await new DevicePairingClient(new LanConnector(), store) { AllowLoopback = true }
            .PairAsync(PairingTarget.FromLink(link!, "Laptop"));
        Assert.True(outcome.Succeeded, outcome.Message);
        return store;
    }

    private RemoteHostLink NewLink(ClientStateStore store)
    {
        var link = new RemoteHostLink(store, _lan, _time, new RemoteHostLinkOptions { WatchNetwork = false, Jitter = () => 1.0 },
            (url, fingerprint) =>
            {
                Interlocked.Increment(ref _relayFactoryCalls);
                return new RelayConnector(url, fingerprint);
            });
        _links.Add(link);
        return link;
    }

    private static RemoteRequest Ping() => new("GET", Routes.Ping, []);

    private async Task ApproveNextAsync()
    {
        await Wait.UntilAsync(() => _host.Registry.PendingRequests.Count > 0, "a token request on the PC");
        await _host.Registry.ApproveAsync(_host.Registry.PendingRequests[0].RequestId, default);
    }

    [Fact]
    public async Task The_home_network_is_preferred_when_both_routes_work()
    {
        var link = NewLink(await PairAsync());

        Assert.True(await link.ReconnectAsync());

        Assert.Equal(RemoteRoutes.Lan, link.Status.Route);
        Assert.Equal(0, _relay.DeviceAttempts);
    }

    [Fact]
    public async Task Away_from_home_signed_requests_and_the_token_work_through_the_relay()
    {
        var store = await PairAsync();
        _lan.Down = true;
        var link = NewLink(store);

        var asking = link.RequestTokenAsync();
        await ApproveNextAsync();
        Assert.Equal(TokenOutcome.Approved, await asking);
        var models = await link.SendAsync(new RemoteRequest("GET", Routes.Models, []));

        Assert.Equal(200, models.Status);
        Assert.Equal(RemoteLinkState.Connected, link.Status.State);
        Assert.Equal(RemoteRoutes.Relay, link.Status.Route);
        Assert.True(_relay.DeviceAttempts >= 1);
        Assert.NotNull(Assert.Single(_remote.Snapshot().Devices).LastSeenAt);
    }

    [Fact]
    public async Task A_stream_through_the_relay_delivers_its_body_chunk_by_chunk()
    {
        var store = await PairAsync();
        _lan.Down = true;
        var link = NewLink(store);
        var asking = link.RequestTokenAsync();
        await ApproveNextAsync();
        await asking;

        using var reply = await link.OpenStreamAsync(new RemoteRequest("GET", Routes.Models, []));

        Assert.Equal(200, reply.Status);
        using var reader = new StreamReader(reply.Body);
        Assert.Contains("data", await reader.ReadToEndAsync());
        Assert.Equal(RemoteRoutes.Relay, link.Status.Route);
    }

    [Fact]
    public async Task The_link_falls_back_to_the_relay_then_moves_back_to_the_home_network_when_it_answers()
    {
        var link = NewLink(await PairAsync());
        _lan.Down = true;
        link.Start();

        await Wait.UntilAsync(() => link.Status is { State: RemoteLinkState.Connected, Route: RemoteRoutes.Relay }, "the relay route");
        await Wait.UntilAsync(() => _time.Pending().Count > 0, "the link to sleep");
        _lan.Down = false;
        _time.Advance(TimeSpan.FromSeconds(60));

        await Wait.UntilAsync(() => link.Status is { State: RemoteLinkState.Connected, Route: RemoteRoutes.Lan }, "the way back to the LAN");
    }

    [Fact]
    public async Task A_pairing_without_a_relay_never_touches_the_relay()
    {
        var link = NewLink(await PairAsync(withRelay: false));
        _lan.Down = true;

        var ex = await Assert.ThrowsAsync<RemoteUnreachableException>(() => link.SendAsync(Ping()));

        Assert.Equal(RemoteLinkReasons.Offline, ex.Reason);
        Assert.Equal(0, _relay.DeviceAttempts);
        Assert.Equal(0, _relayFactoryCalls);
        Assert.Equal(RemoteRoutes.Down, link.Status.Route);
    }

    [Fact]
    public async Task An_unsafe_saved_relay_address_counts_as_no_relay()
    {
        var store = await PairAsync();
        var remote = store.LoadRemote()!;
        using var key = store.LoadDeviceKey()!;
        store.SavePairing(remote with { RelayUrl = "http://relay.example.net" }, key.ExportPkcs8PrivateKey());
        _lan.Down = true;
        var link = NewLink(store);

        var ex = await Assert.ThrowsAsync<RemoteUnreachableException>(() => link.SendAsync(Ping()));

        Assert.Equal(RemoteLinkReasons.Offline, ex.Reason);
        Assert.Equal(0, _relayFactoryCalls);
        Assert.Equal(0, _relay.DeviceAttempts);
    }

    [Fact]
    public async Task A_wrong_fingerprint_over_the_relay_is_a_changed_certificate_and_sends_no_request()
    {
        var store = await PairAsync();
        var remote = store.LoadRemote()!;
        using var key = store.LoadDeviceKey()!;
        store.SavePairing(remote with { Fingerprint = RemoteCodec.Encode(SHA256.HashData("someone else"u8)) }, key.ExportPkcs8PrivateKey());
        _lan.Down = true;
        var link = NewLink(store);

        var ex = await Assert.ThrowsAsync<RemoteUnreachableException>(() => link.SendAsync(Ping()));

        Assert.Equal(RemoteLinkReasons.CertChanged, ex.Reason);
        Assert.Equal(RemoteLinkState.FingerprintChanged, link.Status.State);
        Assert.Null(Assert.Single(_remote.Snapshot().Devices).LastSeenAt); // Nothing signed ever reached the PC.
        Assert.Equal(1, _relay.DeviceAttempts);
        var again = await Assert.ThrowsAsync<RemoteUnreachableException>(() => link.SendAsync(Ping()));
        Assert.Equal(RemoteLinkReasons.CertChanged, again.Reason);
        Assert.Equal(1, _relay.DeviceAttempts); // Terminal: the relay is not asked again.
    }

    [Fact]
    public async Task A_relay_that_says_the_PC_is_offline_makes_the_link_unreachable_not_revoked()
    {
        var link = NewLink(await PairAsync());
        _lan.Down = true;
        _relay.RefuseDevicesWith = 503;

        var ex = await Assert.ThrowsAsync<RemoteUnreachableException>(() => link.SendAsync(Ping()));

        Assert.Equal(RemoteLinkReasons.RelayHostOffline, ex.Reason);
        Assert.Equal(RemoteLinkState.Unreachable, link.Status.State);
        Assert.Equal(RemoteRoutes.Down, link.Status.Route);

        _relay.RefuseDevicesWith = null; // The PC is back: the link recovers on its own.
        _lan.Down = true;
        Assert.Equal(200, (await link.SendAsync(Ping())).Status);
        Assert.Equal(RemoteRoutes.Relay, link.Status.Route);
    }

    [Theory]
    [InlineData(429, null)]
    [InlineData(null, 4429)]
    public async Task A_relay_over_budget_fails_with_a_one_minute_wait_and_is_left_alone_until_then(int? httpStatus, int? closeCode)
    {
        var link = NewLink(await PairAsync());
        _lan.Down = true;
        _relay.RefuseDevicesWith = httpStatus;
        _relay.CloseDevicesWith = closeCode;

        var first = await Assert.ThrowsAsync<RemoteUnreachableException>(() => link.SendAsync(Ping()));

        Assert.Equal(RemoteLinkReasons.RelayBusy, first.Reason);
        Assert.Equal(60_000, first.RetryAfterMs);
        Assert.Equal(RemoteLinkState.Unreachable, link.Status.State);
        var attempts = _relay.DeviceAttempts;

        var second = await Assert.ThrowsAsync<RemoteUnreachableException>(() => link.SendAsync(Ping()));
        Assert.Equal(RemoteLinkReasons.RelayBusy, second.Reason);
        Assert.InRange(second.RetryAfterMs!.Value, 1, 60_000);
        Assert.Equal(attempts, _relay.DeviceAttempts); // No retry while the wait lasts.

        _relay.RefuseDevicesWith = null;
        _relay.CloseDevicesWith = null;
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(200, (await link.SendAsync(Ping())).Status);
        Assert.Equal(RemoteRoutes.Relay, link.Status.Route);
    }

    [Fact]
    public async Task Writes_become_binary_frames_of_at_most_65536_bytes()
    {
        var fingerprint = _remote.Snapshot().Fingerprint!;
        Assert.True(RemoteCodec.TryDecode(fingerprint, 32, out var pin));
        var uri = new Uri($"ws://127.0.0.1:{_relay.Port}/v1/device/{RelayProtocol.RoomId(pin)}");
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(uri, CancellationToken.None);
        var open = new byte[64];
        Assert.Equal(WebSocketMessageType.Text, (await ws.ReceiveAsync(open, CancellationToken.None)).MessageType);
        await using var stream = new RelayWebSocketStream(ws);

        await stream.WriteAsync(new byte[200_000]);

        await Wait.UntilAsync(() => _relay.MaxDeviceFrame > 0, "the relay to see a frame");
        Assert.Equal(RelayWebSocketStream.MaxFrameBytes, _relay.MaxDeviceFrame);
    }
}
