using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Tests.Remote;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

public sealed class BridgeTokenTests
{
    private sealed class TickTime : TimeProvider
    {
        private long _now = 1_000;
        public override long TimestampFrequency => 1_000;
        public override long GetTimestamp() => _now;
        public void Advance(TimeSpan by) => _now += (long)by.TotalMilliseconds;
    }

    [Fact]
    public void AValidTokenIsAcceptedOnceAndThenRemoved()
    {
        var tokens = new BridgeTokens();
        var t = tokens.Issue();
        Assert.Equal(BridgeTokens.TokenBytes, t.Length);
        Assert.True(tokens.TryConsume(t));
        Assert.False(tokens.TryConsume(t)); // single use
    }

    [Fact]
    public void WipingTheReturnedTokenDoesNotChangeTheIssuedOne()
    {
        var tokens = new BridgeTokens();
        var t = tokens.Issue();
        var copy = (byte[])t.Clone();
        Array.Clear(t);
        Assert.True(tokens.TryConsume(copy));
    }

    [Fact]
    public void WrongShortAndEmptyTokensAreRefused()
    {
        var tokens = new BridgeTokens();
        var t = tokens.Issue();
        var wrong = (byte[])t.Clone();
        wrong[7] ^= 1;
        Assert.False(tokens.TryConsume(wrong));
        Assert.False(tokens.TryConsume(t.AsSpan(0, 31)));
        Assert.False(tokens.TryConsume([]));
        Assert.True(tokens.TryConsume(t)); // a wrong guess does not burn the real token
    }

    [Fact]
    public void AnExpiredTokenIsRefused()
    {
        var time = new TickTime();
        var tokens = new BridgeTokens(time);
        var t = tokens.Issue();
        time.Advance(BridgeTokens.Lifetime + TimeSpan.FromMilliseconds(1));
        Assert.False(tokens.TryConsume(t));
    }

    [Fact]
    public void OutstandingTokensAreCapped()
    {
        var tokens = new BridgeTokens();
        var first = tokens.Issue();
        for (var i = 0; i < 64; i++) tokens.Issue();
        Assert.False(tokens.TryConsume(first)); // the oldest was dropped
    }
}

/// <summary>The internal listener against raw TCP clients. Nothing here leaves loopback.</summary>
public sealed class BridgeListenerTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private RemoteAccess _remote = null!;
    private FakeRelay _relay = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.StartAsync(new FakeEngineOptions { Reply = "hi" });
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

    private static async Task<bool> ClosedWithoutReplyAsync(TcpClient c)
    {
        var s = c.GetStream();
        var buf = new byte[16];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            return await s.ReadAsync(buf, cts.Token) == 0;
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return true; // reset: also closed, nothing was sent
        }
    }

    private async Task<TcpClient> ConnectAsync()
    {
        var (ep, _) = _remote.BridgeForTests!.Value;
        var c = new TcpClient();
        await c.ConnectAsync(ep);
        return c;
    }

    [Fact]
    public async Task ATokenThenTlsIsServed()
    {
        var (_, tokens) = _remote.BridgeForTests!.Value;
        using var c = await ConnectAsync();
        var s = c.GetStream();
        await s.WriteAsync(tokens.Issue());
        using var tls = new SslStream(s, false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync("host.invalid"); // the handshake only works when the token was taken
        Assert.True(tls.IsAuthenticated);
    }

    [Fact]
    public async Task NoTokenIsClosedAtOnce()
    {
        using var c = await ConnectAsync();
        // Sends nothing: the listener gives up after its short wait.
        Assert.True(await ClosedWithoutReplyAsync(c));
    }

    [Fact]
    public async Task ATlsHelloWithoutATokenIsClosedWithoutReply()
    {
        using var c = await ConnectAsync();
        var s = c.GetStream();
        byte[] junk = new byte[64];
        junk[0] = 0x16; // looks like a TLS record, but is not a token
        await s.WriteAsync(junk);
        Assert.True(await ClosedWithoutReplyAsync(c));
    }

    [Fact]
    public async Task AWrongTokenIsClosedWithoutReply()
    {
        var (_, tokens) = _remote.BridgeForTests!.Value;
        _ = tokens.Issue();
        using var c = await ConnectAsync();
        await c.GetStream().WriteAsync(new byte[32]);
        Assert.True(await ClosedWithoutReplyAsync(c));
    }

    [Fact]
    public async Task AReusedTokenIsClosedWithoutReply()
    {
        var (_, tokens) = _remote.BridgeForTests!.Value;
        var t = tokens.Issue();
        using (var first = await ConnectAsync())
        {
            await first.GetStream().WriteAsync(t);
            using var tls = new SslStream(first.GetStream(), false, (_, _, _, _) => true);
            await tls.AuthenticateAsClientAsync("host.invalid");
        }

        using var again = await ConnectAsync();
        await again.GetStream().WriteAsync(t);
        Assert.True(await ClosedWithoutReplyAsync(again));
    }

    [Fact]
    public async Task AnExpiredTokenIsClosedWithoutReply()
    {
        var (_, tokens) = _remote.BridgeForTests!.Value;
        var t = tokens.Issue();
        await Task.Delay(BridgeTokens.Lifetime + TimeSpan.FromMilliseconds(300));
        using var c = await ConnectAsync();
        await c.GetStream().WriteAsync(t);
        Assert.True(await ClosedWithoutReplyAsync(c));
    }
}
