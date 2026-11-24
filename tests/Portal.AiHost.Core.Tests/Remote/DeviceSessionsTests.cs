using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Tests.Support;

namespace Portal.AiHost.Core.Tests.Remote;

public sealed class DeviceSessionsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly DeviceStore _devices;
    private readonly DeviceSessions _sessions;

    public DeviceSessionsTests()
    {
        _devices = new DeviceStore(_dir.Path);
        _sessions = new DeviceSessions(_devices);
    }

    public void Dispose() => _dir.Dispose();

    private string Add(string name)
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        return _devices.Add(name, key.ExportSubjectPublicKeyInfo()).Id;
    }

    [Fact]
    public void UnknownDevicesAreNotAdmitted() => Assert.False(_sessions.TryEnter("dev_nobody", out _));

    [Fact]
    public void RemovalCancelsEveryRequestAlreadyAdmittedAndAdmitsNoMore()
    {
        var id = Add("Laptop");
        var other = Add("Phone");
        Assert.True(_sessions.TryEnter(id, out var first));
        Assert.True(_sessions.TryEnter(id, out var second));
        Assert.True(_sessions.TryEnter(other, out var unrelated));

        _devices.Remove(id);
        _sessions.Revoke(id);

        Assert.True(first.IsCancellationRequested);
        Assert.True(second.IsCancellationRequested);
        Assert.False(unrelated.IsCancellationRequested);
        Assert.False(_sessions.TryEnter(id, out _));
    }

    [Fact]
    public void ARequestArrivingBetweenTheStoreRemovalAndTheCancelIsStillRefused()
    {
        var id = Add("Laptop");
        _devices.Remove(id);

        Assert.False(_sessions.TryEnter(id, out _));
        _sessions.Revoke(id);
    }
}
