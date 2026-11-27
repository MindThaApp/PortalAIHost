using System.Net;
using System.Net.Sockets;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Auth;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

public sealed class SwitchableProtector : IHostKeyProtector
{
    private readonly TestKeyProtector _inner = new();

    /// <summary>While set, opening a 33-byte blob (the admission key file) fails, as a protector would when it is temporarily unavailable.</summary>
    public volatile bool FailAdmission;

    public byte[] Protect(byte[] plaintext) => _inner.Protect(plaintext);

    public byte[] Unprotect(byte[] protectedData) =>
        FailAdmission && protectedData.Length == 33
            ? throw new System.Security.Cryptography.CryptographicException("temporarily unavailable")
            : _inner.Unprotect(protectedData);
}

/// <summary>What happens to the relay access key file when it cannot be used, and rotation.</summary>
public sealed class RelayAdmissionKeyFileTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private FakeRelay _relay = null!;
    private readonly SwitchableProtector _protector = new();
    private RemoteAccess? _remote;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.StartAsync(new FakeEngineOptions());
        _relay = await FakeRelay.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_remote is not null) await _remote.DisposeAsync();
        await _relay.DisposeAsync();
        await _host.DisposeAsync();
    }

    private RemoteAccess NewRemote() => _remote = new RemoteAccess(_host.Runtime, new RemoteAccessOptions
    {
        DataDirectory = _host.RootPath,
        KeyProtector = _protector,
        RevokeDevice = id => _host.Registry.RevokeDevice(id),
        BindAddress = IPAddress.Loopback,
        AllowSource = _ => true,
        PortOverride = 0,
        HostName = "Desk PC",
        Addresses = () => ["192.168.1.20"],
        RelayInitialDelay = TimeSpan.FromMilliseconds(50),
        RelayMaxDelay = TimeSpan.FromMilliseconds(300),
    });

    private RemoteSettings Settings() => new() { Enabled = true, RelayUrl = _relay.Url.ToString(), RelayAdmission = true };

    private string KeyFile => Path.Combine(_host.RootPath, "remote-admission.bin");

    [Fact]
    public async Task ADamagedKeyIsKeptAsBadReplacedAndSaidOutLoud()
    {
        File.WriteAllBytes(KeyFile, [0x50, 1, 2, 3]); // opens to the wrong size
        var damaged = File.ReadAllBytes(KeyFile);

        await NewRemote().ApplyAsync(Settings());
        await _relay.WaitHostAsync();

        Assert.Equal(damaged, File.ReadAllBytes(KeyFile + ".bad")); // the old bytes are kept, never overwritten
        Assert.Equal(33, File.ReadAllBytes(KeyFile).Length);
        Assert.NotNull(_relay.PublishedAdmission);
        Assert.Contains("pair again", _remote!.Problem);
        Assert.Contains("pair again", _remote.Snapshot().Problem);
    }

    [Fact]
    public async Task ATemporaryFailureNeverRotatesAndKeepsTheRelayOffButTheHomeNetworkOn()
    {
        await NewRemote().ApplyAsync(Settings()); // makes the key
        await _relay.WaitHostAsync();
        var before = File.ReadAllBytes(KeyFile);
        await _remote!.DisposeAsync();

        _protector.FailAdmission = true;
        var again = NewRemote();
        await again.ApplyAsync(Settings());

        Assert.True(again.Listening); // the home network works
        Assert.Equal(RelayStates.Off, again.Snapshot().Relay);
        Assert.Contains("relay", again.Problem);
        Assert.Equal(before, File.ReadAllBytes(KeyFile)); // untouched
        Assert.False(File.Exists(KeyFile + ".bad"));
        Assert.True(PairingLink.TryParse(again.StartPairing().QrText, out var link));
        Assert.Null(link!.RelayUrl); // a pairing code must not offer a relay that is off

        _protector.FailAdmission = false;
        await again.ApplyAsync(Settings());
        await _relay.WaitHostAsync();
        Assert.NotNull(_relay.PublishedAdmission);
    }

    [Fact]
    public async Task RotatingMakesANewKeyKeepsTheOldOneAndRepublishes()
    {
        await NewRemote().ApplyAsync(Settings());
        await _relay.WaitHostAsync();
        var oldHash = _relay.PublishedAdmission;
        var oldFile = File.ReadAllBytes(KeyFile);

        Assert.True(await _remote!.RotateRelayKeyAsync());
        await Wait.UntilAsync(() => _relay.HasHost && _relay.PublishedAdmission is { } h && !h.AsSpan().SequenceEqual(oldHash), "the new hash");

        Assert.Equal(oldFile, File.ReadAllBytes(KeyFile + ".bad"));
        Assert.NotEqual(oldFile, File.ReadAllBytes(KeyFile));
    }

    [Fact]
    public async Task ARelayThatAnswers4401ToTheKeyIsExplained()
    {
        await NewRemote().ApplyAsync(new RemoteSettings { Enabled = true, RelayUrl = _relay.Url.ToString() });
        await _relay.WaitHostAsync();
        _relay.RefuseHostWith = 4401;

        await _remote!.ApplyAsync(Settings());
        await Wait.UntilAsync(() => _remote.Problem is { } p && p.Contains("may need updating"), "the explanation");
    }
}

public sealed class QrFitTests
{
    private const string Fp = "p9IEbdYnU2WxrDjoY0IOpU7VtsDTSjCCDcfuYy1Z-0w";

    [Fact]
    public void TheNameIsShortenedBeforeAnythingElseSoTheRealDefaultRelayAndKeyFit()
    {
        var name = "A very long computer name!"[..24];
        var qr = RemoteNetwork.QrText("192.168.100.200", 48123, Fp, "ABCDEFGHJKLM", name, RemoteApi.RelayDefaultUrl, Fp);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(qr) <= RemoteNetwork.QrMaxBytes);
        Assert.Contains("&a=" + Fp, qr);
        Assert.Contains("&r=" + RemoteApi.RelayDefaultUrl, qr);
        Assert.NotNull(QrCode.Encode(qr)); // does not throw
        var full = RemoteNetwork.LinkText("192.168.100.200", 48123, Fp, "ABCDEFGHJKLM", name, RemoteApi.RelayDefaultUrl, Fp);
        Assert.Contains(Uri.EscapeDataString(name), full); // "Copy link" keeps everything
    }

    [Fact]
    public void AShortNameIsKeptWhole()
    {
        var qr = RemoteNetwork.QrText("192.168.1.2", 48123, Fp, "ABCDEFGHJKLM", "Desk PC");
        Assert.Contains("&n=Desk%20PC", qr);
    }
}

public sealed class BridgeWaitCapTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private RemoteAccess _remote = null!;
    private FakeRelay _relay = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.StartAsync(new FakeEngineOptions());
        _relay = await FakeRelay.StartAsync();
        _remote = new RemoteAccess(_host.Runtime, new RemoteAccessOptions
        {
            DataDirectory = _host.RootPath,
            KeyProtector = new TestKeyProtector(),
            RevokeDevice = id => _host.Registry.RevokeDevice(id),
            BindAddress = IPAddress.Loopback,
            PortOverride = 0,
            HostName = "Desk PC",
            Addresses = () => ["192.168.1.20"],
        });
        await _remote.ApplyAsync(new RemoteSettings { Enabled = true, RelayUrl = _relay.Url.ToString() });
    }

    public async ValueTask DisposeAsync()
    {
        await _remote.DisposeAsync();
        await _relay.DisposeAsync();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task ConnectionsThatSendNoTokenAreCappedAt16WaitingAtOnce()
    {
        var (ep, _) = _remote.BridgeForTests!.Value;
        var clients = new List<TcpClient>();
        try
        {
            for (var i = 0; i < 24; i++)
            {
                var c = new TcpClient();
                await c.ConnectAsync(ep);
                clients.Add(c);
            }

            await Task.Delay(1000); // well inside the 3 s wait: only the extra connections can be closed by now
            var closed = 0;
            foreach (var c in clients)
            {
                if (c.Client.Poll(0, SelectMode.SelectRead) && c.Client.Available == 0) closed++;
            }

            Assert.InRange(closed, 6, 8);
        }
        finally
        {
            foreach (var c in clients) c.Dispose();
        }
    }
}
