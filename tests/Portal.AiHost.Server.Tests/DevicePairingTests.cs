using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Remote.Client;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

/// <summary>The device side of pairing against the real LAN listener on loopback.</summary>
public sealed class DevicePairingTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private RemoteAccess _remote = null!;
    private readonly ManualTime _time = new();
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "pah-devpair-" + Guid.NewGuid().ToString("N"));

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Select(b => (byte)(b ^ 0x21)).ToArray();

        public byte[] Unprotect(byte[] protectedData) => protectedData.Select(b => (byte)(b ^ 0x21)).ToArray();
    }

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
            Time = _time,
        });
        await _remote.ApplyAsync(new RemoteSettings { Enabled = true });
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

    private ClientStateStore Store() => new(_stateRoot, new XorProtector());

    private static DevicePairingClient Client(ClientStateStore store) =>
        new(new LanConnector(), store) { AllowLoopback = true };

    private string Link(PairingOfferInfo offer, string? relay = null, string? fingerprint = null) =>
        RemoteNetwork.QrText("127.0.0.1", _remote.Port, fingerprint ?? offer.Fingerprint,
            PairingCrypto.Normalize(offer.Code)!, "Desk PC", relay);

    private static PairingTarget FromLink(string text, string name = "Laptop")
    {
        Assert.True(PairingLink.TryParse(text, out var link));
        return PairingTarget.FromLink(link!, name);
    }

    [Fact]
    public async Task Link_pairing_succeeds_pins_fingerprint_and_keeps_relay()
    {
        var offer = _remote.StartPairing();
        var store = Store();

        var outcome = await Client(store).PairAsync(FromLink(Link(offer, relay: "https://relay.example.com")));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Single(_remote.Snapshot().Devices);
        var saved = store.LoadRemote()!;
        Assert.Equal(offer.Fingerprint, saved.Fingerprint);
        Assert.Equal("https://relay.example.com", saved.RelayUrl);
        Assert.Equal(_remote.Port, saved.Port);
        Assert.Equal("Desk PC", saved.PcName);
        Assert.NotNull(store.LoadDeviceKey());
    }

    [Fact]
    public async Task Typed_code_pairs_on_lan_only_without_relay()
    {
        var offer = _remote.StartPairing();
        var store = Store();

        var outcome = await Client(store).PairAsync(
            PairingTarget.FromTypedCode("127.0.0.1", _remote.Port, offer.Code.ToLowerInvariant(), "Laptop"));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Null(store.LoadRemote()!.RelayUrl);
        Assert.Equal(offer.Fingerprint, store.LoadRemote()!.Fingerprint);
    }

    [Fact]
    public async Task Wrong_code_is_refused_and_leaves_no_files()
    {
        var offer = _remote.StartPairing();
        var canonical = PairingCrypto.Normalize(offer.Code)!;
        var wrongTail = canonical[..8] + (canonical[8] == '0' ? "1111" : "0000");
        var store = Store();

        var outcome = await Client(store).PairAsync(PairingTarget.FromTypedCode("127.0.0.1", _remote.Port, wrongTail, "Laptop"));

        Assert.Equal(PairingError.Refused, outcome.Error);
        Assert.Equal(PairingReasons.WrongCode, outcome.Reason);
        Assert.False(Directory.Exists(store.Directory));
    }

    [Fact]
    public async Task Expired_code_is_reported()
    {
        var offer = _remote.StartPairing();
        _time.Advance(TimeSpan.FromMinutes(6));
        var store = Store();

        var outcome = await Client(store).PairAsync(FromLink(Link(offer)));

        Assert.Equal(PairingError.Refused, outcome.Error);
        Assert.Equal(PairingReasons.Expired, outcome.Reason);
        Assert.False(store.IsPaired);
    }

    [Fact]
    public async Task Link_with_other_fingerprint_aborts_before_any_proof_and_code_stays_usable()
    {
        var offer = _remote.StartPairing();
        var other = RemoteCodec.Encode(SHA256.HashData("another host"u8));
        var store = Store();

        var outcome = await Client(store).PairAsync(FromLink(Link(offer, fingerprint: other)));

        Assert.Equal(PairingError.CertificateMismatch, outcome.Error);
        Assert.False(Directory.Exists(store.Directory));

        // No proof reached the host, so the code was neither used nor counted as a wrong attempt.
        var again = await Client(store).PairAsync(FromLink(Link(offer)));
        Assert.True(again.Succeeded, again.Message);
    }

    [Fact]
    public async Task Typed_code_for_another_certificate_sends_nothing()
    {
        using var server = SilentTlsServer.Start();
        var code = PairingCrypto.NewCode(SHA256.HashData("a different certificate"u8));
        var store = Store();

        var outcome = await Client(store).PairAsync(PairingTarget.FromTypedCode("127.0.0.1", server.Port, code, "Laptop"));

        Assert.Equal(PairingError.CertificateMismatch, outcome.Error);
        Assert.Equal(0, await server.BytesReceivedAsync());
        Assert.False(Directory.Exists(store.Directory));
    }

    [Fact]
    public async Task Public_address_is_refused_without_connecting()
    {
        var client = new DevicePairingClient(new LanConnector(), Store()); // loopback not allowed here
        var outcome = await client.PairAsync(PairingTarget.FromTypedCode("8.8.8.8", 41870, "ABCD-EFGH-JKMN", "Laptop"));
        Assert.Equal(PairingError.AddressNotAllowed, outcome.Error);
    }

    [Fact]
    public async Task Bad_input_is_rejected_before_any_network()
    {
        var outcome = await Client(Store()).PairAsync(PairingTarget.FromTypedCode("127.0.0.1", 1, "nope", "Laptop"));
        Assert.Equal(PairingError.InvalidInput, outcome.Error);
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
