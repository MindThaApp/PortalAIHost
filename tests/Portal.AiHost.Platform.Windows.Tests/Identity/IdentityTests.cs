using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Portal.AiHost.Platform.Windows.Identity;

namespace Portal.AiHost.Platform.Windows.Tests.Identity;

/// <summary>Real Win32 calls against this test process only: a loopback socket pair and DPAPI in memory.</summary>
[SupportedOSPlatform("windows")]
public class IdentityTests
{
    [Fact]
    public void DpapiRoundTripsAndRejectsTampering()
    {
        var protector = new DpapiHostKeyProtector();
        var key = RandomNumberGenerator.GetBytes(32);
        var blob = protector.Protect(key);

        Assert.NotEqual(key, blob);
        Assert.True(blob.AsSpan().IndexOf(key) < 0);
        Assert.Equal(key, protector.Unprotect(blob));

        blob[^1] ^= 0xFF;
        Assert.Throws<Portal.AiHost.Core.Auth.HostKeyDamagedException>(() => protector.Unprotect(blob));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task ResolvesOwnLoopbackConnectionToThisProcess(string address)
    {
        var ip = IPAddress.Parse(address);
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
        {
            return;
        }

        using var listener = new TcpListener(ip, 0);
        listener.Start();
        using var client = new TcpClient(ip.AddressFamily);
        await client.ConnectAsync(ip, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await listener.AcceptTcpClientAsync();

        var serverLocal = (IPEndPoint)server.Client.LocalEndPoint!;
        var serverRemote = (IPEndPoint)server.Client.RemoteEndPoint!;
        var caller = new WindowsCallerIdentityResolver().Resolve(serverRemote, serverLocal);

        Assert.NotNull(caller);
        Assert.Equal(Environment.ProcessId, caller.ProcessId);
        Assert.True(caller.SameUser);
        Assert.Equal(Environment.ProcessPath, caller.ExePath, ignoreCase: true);
        Assert.Null(caller.PackageFamilyName);
        Assert.False(caller.Verified);
    }

    [Fact]
    public void UnknownOrNonLoopbackConnectionsResolveToNull()
    {
        var resolver = new WindowsCallerIdentityResolver();
        Assert.Null(resolver.Resolve(new IPEndPoint(IPAddress.Loopback, 1), new IPEndPoint(IPAddress.Loopback, 2)));
        Assert.Null(resolver.Resolve(new IPEndPoint(IPAddress.Parse("192.168.1.5"), 5000),
            new IPEndPoint(IPAddress.Loopback, 41870)));
    }

    [Fact]
    public void DescribesMissingProcessAsUnverifiedOtherUser()
    {
        var caller = new WindowsCallerIdentityResolver().Describe(int.MaxValue - 3);
        Assert.False(caller.SameUser);
        Assert.False(caller.Verified);
    }
}
