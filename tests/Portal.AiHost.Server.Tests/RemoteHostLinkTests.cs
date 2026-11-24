using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Remote.Client;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

/// <summary>A clock tests move by hand. Timers fire inside <see cref="Advance"/>; nothing sleeps.</summary>
internal sealed class StepTime(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<StepTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _now.UtcTicks;

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new StepTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Delays of the timers that are waiting now (relative to the current time), soonest first.</summary>
    public IReadOnlyList<TimeSpan> Pending()
    {
        lock (_gate)
        {
            return _timers.Where(t => t.Due is not null).Select(t => t.Due!.Value - _now).Order().ToList();
        }
    }

    public void Advance(TimeSpan by)
    {
        var target = _now + by;
        while (true)
        {
            StepTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is not null && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.Due!.Value > _now ? next.Due!.Value : _now;
                next.Fire();
            }

            next.Callback(next.State);
        }
    }

    private sealed class StepTimer(StepTime owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? Due { get; private set; }

        public TimerCallback Callback => callback;

        public object? State => state;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                if (!owner._timers.Contains(this))
                {
                    owner._timers.Add(this);
                }
            }

            return true;
        }

        public void Fire() => Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : owner._now + _period;

        public void Dispose()
        {
            lock (owner._gate)
            {
                Due = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class XorSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) => plaintext.Select(b => (byte)(b ^ 0x21)).ToArray();

    public byte[] Unprotect(byte[] protectedData) => protectedData.Select(b => (byte)(b ^ 0x21)).ToArray();
}

/// <summary>Counts the requests that really go out (not the TLS handshakes).</summary>
internal sealed class CountingConnector(IRemoteConnector inner) : IRemoteConnector
{
    public int Requests;
    public int Opens;

    public IRemoteChannel Open(string address, int port, Func<byte[], bool> acceptCertificate)
    {
        Interlocked.Increment(ref Opens);
        return new Channel(inner.Open(address, port, acceptCertificate), this);
    }

    private sealed class Channel(IRemoteChannel inner, CountingConnector owner) : IRemoteChannel
    {
        public Task<RemoteReply> PostJsonAsync(string path, string json, CancellationToken ct)
        {
            Interlocked.Increment(ref owner.Requests);
            return inner.PostJsonAsync(path, json, ct);
        }

        public Task<RemoteReply> SendAsync(RemoteRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref owner.Requests);
            return inner.SendAsync(request, ct);
        }

        public Task<RemoteStreamReply> OpenAsync(RemoteRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref owner.Requests);
            return inner.OpenAsync(request, ct);
        }

        public void Dispose() => inner.Dispose();
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("Gave up waiting for " + what);
            }

            await Task.Delay(5);
        }
    }
}

/// <summary>The link against a scripted connector: backoff, health and state events without a network.</summary>
public sealed class RemoteHostLinkScriptedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pah-link-script-" + Guid.NewGuid().ToString("N"));
    private readonly StepTime _time = new(DateTimeOffset.UtcNow);
    private readonly List<RemoteLinkStatus> _events = [];
    private readonly ScriptedConnector _connector = new();
    private RemoteHostLink? _link;

    private sealed class ScriptedConnector : IRemoteConnector
    {
        public volatile bool Up;
        public int Requests;

        public IRemoteChannel Open(string address, int port, Func<byte[], bool> acceptCertificate) => new Channel(this);

        private sealed class Channel(ScriptedConnector owner) : IRemoteChannel
        {
            public Task<RemoteReply> PostJsonAsync(string path, string json, CancellationToken ct) =>
                throw new NotSupportedException();

            public Task<RemoteReply> SendAsync(RemoteRequest request, CancellationToken ct)
            {
                Interlocked.Increment(ref owner.Requests);
                return owner.Up
                    ? Task.FromResult(new RemoteReply(200, "{}"))
                    : Task.FromException<RemoteReply>(new HttpRequestException("down"));
            }

            public Task<RemoteStreamReply> OpenAsync(RemoteRequest request, CancellationToken ct) =>
                throw new NotSupportedException();

            public void Dispose()
            {
            }
        }
    }

    private RemoteHostLink NewLink()
    {
        var store = new ClientStateStore(_root, new XorSecretProtector());
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        store.SavePairing(new ClientRemote("Desk PC", "192.168.1.20", 48123, null,
            RemoteCodec.Encode(SHA256.HashData("pc"u8)), "dev_1", DateTimeOffset.UtcNow), key.ExportPkcs8PrivateKey());
        _link = new RemoteHostLink(store, _connector, _time,
            new RemoteHostLinkOptions { Jitter = () => 1.0, WatchNetwork = false });
        _link.StateChanged += (_, s) => _events.Add(s);
        return _link;
    }

    public void Dispose()
    {
        _link?.Dispose();
        if (Directory.Exists(_root)) // Test-owned temp folder.
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Waits until the loop sleeps, returns how long, then moves the clock by exactly that.</summary>
    private async Task<TimeSpan> StepAsync()
    {
        await Wait.UntilAsync(() => _time.Pending().Count > 0, "the link to sleep");
        var delay = _time.Pending()[0];
        _time.Advance(delay);
        return delay;
    }

    [Fact]
    public void Backoff_schedule_doubles_from_2_to_60_seconds_and_jitter_only_shortens()
    {
        var o = new RemoteHostLinkOptions();
        var seconds = Enumerable.Range(1, 8).Select(n => RemoteHostLink.BackoffDelay(o, n, 1.0).TotalSeconds).ToArray();
        Assert.Equal([2, 4, 8, 16, 32, 60, 60, 60], seconds);
        Assert.Equal(1.5, RemoteHostLink.BackoffDelay(o, 1, 0).TotalSeconds);
        Assert.InRange(RemoteHostLink.BackoffDelay(o, 6, 0.5).TotalSeconds, 45, 60);
    }

    [Fact]
    public async Task Loop_backs_off_while_down_then_recovers_and_resets_the_schedule()
    {
        var link = NewLink();
        link.Start();

        var delays = new List<double>();
        for (var i = 0; i < 7; i++)
        {
            delays.Add((await StepAsync()).TotalSeconds);
        }

        Assert.Equal([2, 4, 8, 16, 32, 60, 60], delays);
        Assert.Equal(RemoteLinkState.Unreachable, link.Status.State);
        Assert.Equal(RemoteLinkReasons.Offline, link.Status.Reason);

        _connector.Up = true;
        Assert.Equal(60, (await StepAsync()).TotalSeconds); // The probe after this wait succeeds.
        await Wait.UntilAsync(() => link.Status.State == RemoteLinkState.Connected, "the link to recover");
        Assert.Equal(RemoteLinkState.Connected, link.Status.State);

        Assert.Equal(60, (await StepAsync()).TotalSeconds); // Idle: one health check a minute.

        _connector.Up = false;
        Assert.Equal(60, (await StepAsync()).TotalSeconds); // The next check fails.
        await Wait.UntilAsync(() => link.Status.State == RemoteLinkState.Unreachable, "the link to notice");
        Assert.Equal(2, (await StepAsync()).TotalSeconds); // Back to the start of the schedule.

        Assert.Contains(_events, e => e.State == RemoteLinkState.Connecting);
        Assert.Contains(_events, e => e.State == RemoteLinkState.Connected);
        Assert.Equal(RemoteLinkState.Unreachable, _events[^1].State);
    }

    [Fact]
    public async Task Reconnect_probes_at_once_without_waiting_for_the_backoff()
    {
        var link = NewLink();
        Assert.False(await link.ReconnectAsync());
        Assert.Equal(RemoteLinkState.Unreachable, link.Status.State);

        _connector.Up = true;
        Assert.True(await link.ReconnectAsync());
        Assert.Equal(RemoteLinkState.Connected, link.Status.State);
        Assert.NotNull(link.Status.RttMs);
    }

    [Fact]
    public async Task A_request_makes_one_fresh_attempt_and_fails_at_once_while_down()
    {
        var link = NewLink();

        var ex = await Assert.ThrowsAsync<RemoteUnreachableException>(() =>
            link.SendAsync(new RemoteRequest("GET", Routes.Ping, [])));

        Assert.Equal(RemoteLinkReasons.Offline, ex.Reason);
        Assert.Equal(1, _connector.Requests);
    }

    [Fact]
    public async Task Busy_link_checks_every_20_seconds()
    {
        var link = NewLink();
        _connector.Up = true;
        await link.SendAsync(new RemoteRequest("GET", Routes.Ping, []));
        link.Start();

        Assert.Equal(20, (await StepAsync()).TotalSeconds);
    }
}

/// <summary>The link against the real LAN listener on loopback (TLS pin, signing, token, revocation).</summary>
public sealed class RemoteHostLinkLanTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private RemoteAccess _remote = null!;
    private readonly StepTime _time = new(DateTimeOffset.UtcNow);
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "pah-link-lan-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _links = [];

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.StartAsync(new FakeEngineOptions());
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
        });
        await _remote.ApplyAsync(new RemoteSettings { Enabled = true });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var link in _links)
        {
            link.Dispose();
        }

        await _remote.DisposeAsync();
        await _host.DisposeAsync();
        if (Directory.Exists(_stateRoot)) // Test-owned temp folder.
        {
            Directory.Delete(_stateRoot, recursive: true);
        }
    }

    private ClientStateStore Store() => new(_stateRoot, new XorSecretProtector());

    private async Task<ClientStateStore> PairedStoreAsync()
    {
        var store = Store();
        var offer = _remote.StartPairing();
        var text = RemoteNetwork.QrText("127.0.0.1", _remote.Port, offer.Fingerprint, PairingCrypto.Normalize(offer.Code)!, "Desk PC");
        Assert.True(PairingLink.TryParse(text, out var link));
        var outcome = await new DevicePairingClient(new LanConnector(), store) { AllowLoopback = true }
            .PairAsync(PairingTarget.FromLink(link!, "Laptop"));
        Assert.True(outcome.Succeeded, outcome.Message);
        return store;
    }

    private RemoteHostLink NewLink(ClientStateStore store, IRemoteConnector? connector = null, TimeProvider? time = null)
    {
        var link = new RemoteHostLink(store, connector ?? new LanConnector(), time ?? _time,
            new RemoteHostLinkOptions { WatchNetwork = false });
        _links.Add(link);
        return link;
    }

    private async Task<string> ApproveNextAsync()
    {
        await Wait.UntilAsync(() => _host.Registry.PendingRequests.Count > 0, "a token request on the PC");
        var pending = _host.Registry.PendingRequests[0];
        Assert.Equal("Portal AI Host (client mode)", pending.AppName);
        await _host.Registry.ApproveAsync(pending.RequestId, default);
        return pending.RequestId;
    }

    [Fact]
    public async Task Signed_health_check_is_accepted_over_the_pinned_connection()
    {
        var link = NewLink(await PairedStoreAsync());

        Assert.True(await link.ReconnectAsync());

        Assert.Equal(RemoteLinkState.Connected, link.Status.State);
        Assert.NotNull(Assert.Single(_remote.Snapshot().Devices).LastSeenAt);
    }

    [Fact]
    public async Task Token_is_requested_once_stored_and_reused_across_requests_and_restarts()
    {
        var store = await PairedStoreAsync();
        var connector = new CountingConnector(new LanConnector());
        var link = NewLink(store, connector);

        var asking = link.RequestTokenAsync();
        await ApproveNextAsync();
        Assert.Equal(TokenOutcome.Approved, await asking);
        Assert.NotNull(store.LoadToken());
        Assert.True(link.Status.HasToken);

        for (var i = 0; i < 3; i++)
        {
            var reply = await link.SendAsync(new RemoteRequest("GET", Routes.Models, []));
            Assert.Equal(200, reply.Status);
        }

        Assert.Single(await _host.Registry.ListAsync(default)); // One app on the PC: the laptop host.
        Assert.Equal(1, connector.Opens); // One connection, reused.

        var restarted = NewLink(store);
        Assert.True(restarted.Status.HasToken);
        Assert.Equal(200, (await restarted.SendAsync(new RemoteRequest("GET", Routes.Models, []))).Status);
        Assert.Empty(_host.Registry.PendingRequests);
    }

    [Fact]
    public async Task A_rejected_token_triggers_exactly_one_new_request()
    {
        var store = await PairedStoreAsync();
        store.SaveToken("pah_not_a_real_token_0000000000000000000000000");
        var link = NewLink(store);
        var models = new RemoteRequest("GET", Routes.Models, []);

        var first = await Assert.ThrowsAsync<RemoteUnreachableException>(() => link.SendAsync(models));
        Assert.Equal(RemoteLinkReasons.Refused, first.Reason);
        await Wait.UntilAsync(() => _host.Registry.PendingRequests.Count == 1, "the new token request");

        await Assert.ThrowsAsync<RemoteUnreachableException>(() => link.SendAsync(models));
        await Task.Delay(100); // Real time: give a wrongly started second request the chance to show up.
        Assert.Single(_host.Registry.PendingRequests); // Still the same one: no loop.

        var joined = link.RequestTokenAsync(); // Joins the running request.
        await ApproveNextAsync();
        Assert.Equal(TokenOutcome.Approved, await joined);
        Assert.Equal(200, (await link.SendAsync(models)).Status);
    }

    [Fact]
    public async Task Token_request_that_nobody_approves_times_out_and_writes_nothing()
    {
        var store = await PairedStoreAsync();
        var link = NewLink(store);

        var asking = link.RequestTokenAsync();
        await Wait.UntilAsync(() => _host.Registry.PendingRequests.Count == 1, "the token request");
        await Wait.UntilAsync(() => _time.Pending().Count > 0, "the approval deadline");
        _time.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));

        Assert.Equal(TokenOutcome.TimedOut, await asking);
        Assert.Null(store.LoadToken());
        Assert.False(link.Status.HasToken);
    }

    [Fact]
    public async Task Cancelling_the_token_request_writes_nothing()
    {
        var store = await PairedStoreAsync();
        var link = NewLink(store);
        using var cts = new CancellationTokenSource();

        var asking = link.RequestTokenAsync(cts.Token);
        await Wait.UntilAsync(() => _host.Registry.PendingRequests.Count == 1, "the token request");
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking);
        Assert.Null(store.LoadToken());
    }

    [Fact]
    public async Task Another_certificate_stops_the_link_and_no_request_bytes_are_sent()
    {
        using var other = SilentTlsServer.Start();
        var store = Store();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        store.SavePairing(new ClientRemote("Desk PC", "127.0.0.1", other.Port, null,
            RemoteCodec.Encode(SHA256.HashData("the real certificate"u8)), "dev_1", DateTimeOffset.UtcNow),
            key.ExportPkcs8PrivateKey());
        var link = NewLink(store);
        var states = new List<RemoteLinkState>();
        link.StateChanged += (_, s) => states.Add(s.State);

        Assert.False(await link.ReconnectAsync());

        Assert.Equal(RemoteLinkState.FingerprintChanged, link.Status.State);
        Assert.Equal(RemoteLinkReasons.CertChanged, link.Status.Reason);
        Assert.Equal(0, await other.BytesReceivedAsync());

        var ex = await Assert.ThrowsAsync<RemoteUnreachableException>(() => link.SendAsync(new RemoteRequest("GET", Routes.Ping, [])));
        Assert.Equal(RemoteLinkReasons.CertChanged, ex.Reason);
        Assert.False(await link.ReconnectAsync()); // Never re-pins by itself.
        Assert.Equal(RemoteLinkState.FingerprintChanged, link.Status.State);
        Assert.Contains(RemoteLinkState.FingerprintChanged, states);
        Assert.Equal(0, await other.BytesReceivedAsync());
    }

    [Fact]
    public async Task Removed_device_is_revoked_and_the_link_stops_retrying_without_forgetting()
    {
        var store = await PairedStoreAsync();
        store.SaveToken("pah_token");
        var connector = new CountingConnector(new LanConnector());
        var link = NewLink(store, connector);
        Assert.True(await link.ReconnectAsync());

        Assert.True(_remote.RemoveDevice(store.LoadRemote()!.DeviceId));
        Assert.False(await link.ReconnectAsync());

        Assert.Equal(RemoteLinkState.Revoked, link.Status.State);
        var requests = connector.Requests;
        link.Start();
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.False(await link.ReconnectAsync());
        await Assert.ThrowsAsync<RemoteUnreachableException>(() => link.SendAsync(new RemoteRequest("GET", Routes.Models, [])));
        Assert.Equal(requests, connector.Requests); // Nothing more went out.
        Assert.True(store.IsPaired); // Kept: the user decides to forget.
        Assert.Equal("pah_token", store.LoadToken());
    }

    [Fact]
    public async Task A_wrong_clock_is_reported_and_retried_not_treated_as_revoked()
    {
        var store = await PairedStoreAsync();
        var link = NewLink(store, time: new StepTime(DateTimeOffset.UtcNow.AddMinutes(10)));

        Assert.False(await link.ReconnectAsync());

        Assert.Equal(RemoteLinkState.Unreachable, link.Status.State);
        Assert.Equal(RemoteLinkReasons.Clock, link.Status.Reason);
    }

    /// <summary>A TLS server that completes the handshake if the client lets it and counts the bytes that follow.</summary>
    private sealed class SilentTlsServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _cert;
        private int _bytes;
        private Task _loop = Task.CompletedTask;

        private SilentTlsServer()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest("CN=Other", key, HashAlgorithmName.SHA256);
            using var raw = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            _cert = X509CertificateLoader.LoadPkcs12(raw.Export(X509ContentType.Pfx), null);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static SilentTlsServer Start()
        {
            var s = new SilentTlsServer();
            s._listener.Start();
            s._loop = Task.Run(s.RunAsync);
            return s;
        }

        private async Task RunAsync()
        {
            try
            {
                using var tcp = await _listener.AcceptTcpClientAsync();
                using var ssl = new SslStream(tcp.GetStream());
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _cert,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                });
                var buffer = new byte[1024];
                int n;
                while ((n = await ssl.ReadAsync(buffer)) > 0)
                {
                    Interlocked.Add(ref _bytes, n);
                }
            }
            catch (Exception ex) when (ex is IOException or AuthenticationException or ObjectDisposedException or SocketException)
            {
            }
        }

        public async Task<int> BytesReceivedAsync()
        {
            await Task.WhenAny(_loop, Task.Delay(2000));
            return Volatile.Read(ref _bytes);
        }

        public void Dispose()
        {
            _listener.Stop();
            _cert.Dispose();
        }
    }
}
