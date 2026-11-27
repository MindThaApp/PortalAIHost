using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Remote.Client;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

/// <summary>The optional relay admission token, end to end against the fake relay (the real relay is tested in relay/test).</summary>
public sealed class RelayAdmissionTests : IAsyncLifetime
{
    private readonly StepTime _time = new(DateTimeOffset.UtcNow);
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "pah-admit-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _links = [];
    private TestHost _host = null!;
    private FakeRelay _relay = null!;
    private RemoteAccess _remote = null!;
    private SwitchableLan _lan = null!;

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
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var link in _links) link.Dispose();
        await _remote.DisposeAsync();
        await _relay.DisposeAsync();
        await _host.DisposeAsync();
        if (Directory.Exists(_stateRoot)) Directory.Delete(_stateRoot, recursive: true); // Test-owned temp folder.
    }

    private Task ApplyAsync(bool admission) =>
        _remote.ApplyAsync(new RemoteSettings { Enabled = true, RelayUrl = _relay.Url.ToString(), RelayAdmission = admission });

    private PairingLink NewLink()
    {
        var offer = _remote.StartPairing();
        Assert.True(PairingLink.TryParse(offer.QrText, out var link));
        return link!;
    }

    private async Task<ClientStateStore> PairAsync(PairingLink link, string folder)
    {
        var store = new ClientStateStore(Path.Combine(_stateRoot, folder), new XorSecretProtector());
        var outcome = await new DevicePairingClient(new LanConnector(), store) { AllowLoopback = true }
            .PairAsync(PairingTarget.FromLink(link with { Address = "127.0.0.1" }, "Laptop"));
        Assert.True(outcome.Succeeded, outcome.Message);
        return store;
    }

    private RemoteHostLink NewHostLink(ClientStateStore store, bool sendAdmission = true)
    {
        var link = new RemoteHostLink(store, _lan, _time, new RemoteHostLinkOptions { WatchNetwork = false, Jitter = () => 1.0 },
            (url, fingerprint) => new RelayConnector(url, fingerprint,
                admission: sendAdmission ? store.LoadRelayAdmission() : null));
        _links.Add(link);
        return link;
    }

    [Fact]
    public async Task TheHostPublishesOnlyAHashAndPutsTheTokenInThePairingLink()
    {
        await ApplyAsync(admission: true);
        await _relay.WaitHostAsync();
        var link = NewLink();

        Assert.NotNull(link.RelayAdmission);
        Assert.True(RemoteCodec.TryDecode(link.RelayAdmission, 32, out var token));
        Assert.Equal(32, token.Length);
        Assert.NotNull(_relay.PublishedAdmission);
        Assert.Equal(RelayProtocol.AdmissionHash(token), _relay.PublishedAdmission);
        Assert.NotEqual(token, _relay.PublishedAdmission); // the relay never sees the token itself in the hello
    }

    [Fact]
    public async Task ADeviceThatPairedWithTheTokenReachesTheHostThroughTheRelay()
    {
        await ApplyAsync(admission: true);
        await _relay.WaitHostAsync();
        var store = await PairAsync(NewLink(), "with");
        Assert.NotNull(store.LoadRelayAdmission());
        _lan.Down = true;

        var reply = await NewHostLink(store).SendAsync(new RemoteRequest("GET", Routes.Ping, []));

        Assert.Equal(200, reply.Status);
        Assert.Equal(0, _relay.AdmissionRefusals);
    }

    [Fact]
    public async Task ADeviceWithoutTheTokenIsTurnedAwayByTheRelayWithItsOwnReason()
    {
        await ApplyAsync(admission: true);
        await _relay.WaitHostAsync();
        var store = await PairAsync(NewLink(), "without");
        _lan.Down = true;

        var ex = await Assert.ThrowsAsync<RemoteUnreachableException>(() =>
            NewHostLink(store, sendAdmission: false).SendAsync(new RemoteRequest("GET", Routes.Ping, [])));

        Assert.Equal(RemoteLinkReasons.RelayAdmission, ex.Reason);
        Assert.True(_relay.AdmissionRefusals > 0);
    }

    [Fact]
    public async Task WithoutTheSettingNothingIsPublishedAndTheLinkCarriesNoToken()
    {
        await ApplyAsync(admission: false);
        await _relay.WaitHostAsync();

        var link = NewLink();

        Assert.Null(link.RelayAdmission);
        Assert.Null(_relay.PublishedAdmission);
    }

    [Fact]
    public async Task TurningTheSettingOnAndOffReconnectsAndKeepsTheSameTokenForNextTime()
    {
        await ApplyAsync(admission: true);
        await _relay.WaitHostAsync();
        var first = NewLink().RelayAdmission;
        Assert.NotNull(first);

        await ApplyAsync(admission: false);
        await Wait.UntilAsync(() => _relay.HasHost && _relay.PublishedAdmission is null, "an open room again");
        Assert.Null(NewLink().RelayAdmission);

        await ApplyAsync(admission: true);
        await Wait.UntilAsync(() => _relay.HasHost && _relay.PublishedAdmission is not null, "a closed room again");
        Assert.Equal(first, NewLink().RelayAdmission); // stored, so devices paired earlier keep working
    }

    [Fact]
    public async Task TheHostKeepsTheTokenProtectedOnDiskNotInPlainText()
    {
        await ApplyAsync(admission: true);
        var link = NewLink();
        RemoteCodec.TryDecode(link.RelayAdmission, 32, out var token);

        var file = Path.Combine(_host.RootPath, "remote-admission.bin");

        Assert.True(File.Exists(file));
        Assert.False(File.ReadAllBytes(file).AsSpan().IndexOf(token) >= 0);
        Assert.Equal(token, new TestKeyProtector().Unprotect(File.ReadAllBytes(file)));
    }
}

public sealed class RelayAdmissionStateTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pah-admit-state-" + Guid.NewGuid().ToString("N"));

    private static ClientRemote Remote() =>
        new("PC", "192.168.1.2", 48123, "https://relay.example.com", RemoteCodec.Encode(new byte[32]), "dev1", DateTimeOffset.UtcNow);

    [Fact]
    public void ThePairingKeepsTheTokenProtectedAndAForgetRemovesIt()
    {
        var store = new ClientStateStore(_root, new XorSecretProtector());
        var token = RandomNumberGenerator.GetBytes(32);
        store.SavePairing(Remote(), new byte[] { 1, 2, 3 }, token);

        Assert.Equal(token, store.LoadRelayAdmission());
        Assert.NotEqual(token, File.ReadAllBytes(Path.Combine(store.Directory, ClientStateStore.RelayAdmissionFile)));
        store.Forget();
        Assert.Null(store.LoadRelayAdmission());
        Assert.False(File.Exists(Path.Combine(store.Directory, ClientStateStore.RelayAdmissionFile)));
    }

    [Fact]
    public void ANewPairingWithoutATokenDropsTheOldOne()
    {
        var store = new ClientStateStore(_root, new XorSecretProtector());
        store.SavePairing(Remote(), new byte[] { 1 }, RandomNumberGenerator.GetBytes(32));
        store.SavePairing(Remote(), new byte[] { 1 });

        Assert.Null(store.LoadRelayAdmission());
    }

    [Fact]
    public void TheTokenHasTheSameHashAsTheRelaysVector()
    {
        // The relay's own test (relay/test/relay.test.ts) checks the same value.
        var token = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        Assert.Equal("p9IEbdYnU2WxrDjoY0IOpU7VtsDTSjCCDcfuYy1Z-0w", RemoteCodec.Encode(RelayProtocol.AdmissionHash(token)));
    }
}
