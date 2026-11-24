using System.Security.Cryptography;
using Portal.AiHost.Core.Remote.Client;

namespace Portal.AiHost.Core.Tests.Remote;

public sealed class ClientStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pah-clientstate-" + Guid.NewGuid().ToString("N"));

    private sealed class FakeProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => [0x7B, .. plaintext.Select(b => (byte)(b ^ 0x5A))];

        public byte[] Unprotect(byte[] protectedData) =>
            protectedData.Length > 0 && protectedData[0] == 0x7B
                ? protectedData[1..].Select(b => (byte)(b ^ 0x5A)).ToArray()
                : throw new CryptographicException("bad blob");
    }

    private static ClientRemote Remote() => new("Desk PC", "192.168.1.20", 41870, "https://relay.example.com",
        "fp", "dev_1", new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Round_trip_keeps_remote_key_and_token_and_protects_secrets()
    {
        var store = new ClientStateStore(_root, new FakeProtector());
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = key.ExportPkcs8PrivateKey();

        store.SavePairing(Remote(), pkcs8);
        store.SaveToken("secret-token");

        Assert.True(store.IsPaired);
        Assert.Equal(Remote(), store.LoadRemote());
        Assert.Equal("secret-token", store.LoadToken());
        using var loaded = store.LoadDeviceKey()!;
        Assert.Equal(pkcs8, loaded.ExportPkcs8PrivateKey());

        var dir = Path.Combine(_root, "client");
        Assert.False(File.ReadAllBytes(Path.Combine(dir, "device.key")).AsSpan().IndexOf(pkcs8) >= 0);
        Assert.False(File.ReadAllBytes(Path.Combine(dir, "token.dat")).AsSpan().IndexOf("secret-token"u8) >= 0);
        Assert.DoesNotContain("secret", File.ReadAllText(Path.Combine(dir, "remote.json")));
    }

    [Fact]
    public void Forget_removes_only_the_own_files()
    {
        var store = new ClientStateStore(_root, new FakeProtector());
        store.SavePairing(Remote(), ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKey());
        store.SaveToken("t");
        var other = Path.Combine(_root, "client", "apps.json");
        File.WriteAllText(other, "{}");
        var sibling = Path.Combine(_root, "settings.json");
        File.WriteAllText(sibling, "keep");

        store.Forget();

        Assert.False(store.IsPaired);
        Assert.Null(store.LoadRemote());
        Assert.Null(store.LoadToken());
        Assert.Null(store.LoadDeviceKey());
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(sibling));
    }

    [Fact]
    public void Unopenable_key_gives_null_not_a_throw()
    {
        var store = new ClientStateStore(_root, new FakeProtector());
        store.SavePairing(Remote(), ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKey());
        File.WriteAllBytes(Path.Combine(_root, "client", "device.key"), [1, 2, 3]);
        Assert.Null(store.LoadDeviceKey());
    }

    public void Dispose()
    {
        // Test-owned temp folder only.
        if (Directory.Exists(_root) && Path.GetFileName(_root).StartsWith("pah-clientstate-", StringComparison.Ordinal))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
