using System.Net;
using System.Security.Cryptography;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Remote.Client;
using Portal.AiHost.Core.Tests.Remote;
using Portal.AiHost.FakeEngine;
using Portal.AiHost.Server;

namespace Portal.AiHost.Server.Tests;

/// <summary>Pairing and forgetting a PC through the controller (client.pair, client.unpair).</summary>
public sealed class ClientPairingControlTests : IAsyncLifetime
{
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "pah-pairctl-" + Guid.NewGuid().ToString("N"));
    private TestHost _host = null!;
    private RemoteAccess _remote = null!;
    private ClientStateStore _store = null!;

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
            HostName = "Laptop",
            Addresses = () => ["192.168.1.30"],
        });
        await _remote.ApplyAsync(new RemoteSettings { Enabled = false });
        _store = new ClientStateStore(_stateRoot, new XorSecretProtector());
    }

    public async ValueTask DisposeAsync()
    {
        await _remote.DisposeAsync();
        await _host.DisposeAsync();
        if (Directory.Exists(_stateRoot)) // Test-owned temp folder.
        {
            Directory.Delete(_stateRoot, recursive: true);
        }
    }

    private ClientModeController Controller(bool withState = true) =>
        new(_host.Runtime, _remote, new ClientModeOptions
        {
            CreateLink = () => null,
            State = withState ? _store : null,
            DrainGrace = TimeSpan.FromSeconds(1),
        });

    private void SavePairing()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _store.SavePairing(new ClientRemote("Desk PC", "192.168.1.20", 48123, null,
            RemoteCodec.Encode(SHA256.HashData("pc"u8)), "dev_1", DateTimeOffset.UtcNow), key.ExportPkcs8PrivateKey());
    }

    [Fact]
    public async Task Pairing_without_a_state_folder_is_not_available()
    {
        await using var controller = Controller(withState: false);
        var result = await controller.PairAsync(new PairCommand { Link = "portalai://pair?h=a", DeviceName = "Laptop" });
        Assert.False(result.Ok);
        Assert.Equal(PairErrors.NotAvailable, result.Error);
    }

    [Fact]
    public async Task A_bad_link_is_refused_and_leaves_no_files()
    {
        await using var controller = Controller();
        var result = await controller.PairAsync(new PairCommand { Link = "not a link", DeviceName = "Laptop" });
        Assert.False(result.Ok);
        Assert.Equal(PairErrors.InvalidInput, result.Error);
        Assert.False(_store.IsPaired);
    }

    [Fact]
    public async Task A_saved_pairing_is_never_replaced_by_a_new_one()
    {
        SavePairing();
        var before = File.ReadAllBytes(Path.Combine(_store.Directory, ClientStateStore.RemoteFile));
        await using var controller = Controller();
        var result = await controller.PairAsync(new PairCommand { Address = "192.168.1.9", Code = "ABCD-EFGH-JKLM", DeviceName = "Laptop" });
        Assert.Equal(PairErrors.AlreadyPaired, result.Error);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_store.Directory, ClientStateStore.RemoteFile)));
    }

    [Fact]
    public async Task The_saved_pc_shows_in_server_mode_and_unpair_forgets_only_the_pairing_files()
    {
        SavePairing();
        var other = Path.Combine(_store.Directory, "apps.json");
        File.WriteAllText(other, "{}");
        await using var controller = Controller();
        var before = controller.Snapshot();
        Assert.True(before.Paired);
        Assert.Equal("Desk PC", before.PcName);
        Assert.Equal(HostModes.Server, before.Mode);

        var after = await controller.UnpairAsync();
        Assert.False(after.Paired);
        Assert.Null(after.PcName);
        Assert.False(_store.IsPaired);
        Assert.False(File.Exists(Path.Combine(_store.Directory, ClientStateStore.KeyFile)));
        Assert.True(File.Exists(other));
    }
}
