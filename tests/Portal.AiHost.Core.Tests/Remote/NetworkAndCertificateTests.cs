using Portal.AiHost.Contracts;
using System.Text;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Portal.AiHost.Core.Auth;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Tests.Support;

namespace Portal.AiHost.Core.Tests.Remote;

public sealed class NetworkAndCertificateTests
{
    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.1")]
    [InlineData("192.168.1.20")]
    [InlineData("169.254.10.10")]
    [InlineData("fe80::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("::ffff:192.168.1.20")]
    public void HomeAndLinkLocalAddressesAreServed(string address) =>
        Assert.True(RemoteNetwork.IsPrivateAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("192.169.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("::ffff:8.8.8.8")]
    public void PublicLoopbackAndUnspecifiedAddressesAreRefused(string address) =>
        Assert.False(RemoteNetwork.IsPrivateAddress(IPAddress.Parse(address)));

    [Fact]
    public void NoAddressIsRefused() => Assert.False(RemoteNetwork.IsPrivateAddress(null));

    [Fact]
    public void QrTextCarriesEverythingTheClientNeeds()
    {
        var text = RemoteNetwork.QrText("192.168.1.20", 48123, "abc_-", "042917", "Desk PC");
        Assert.Equal("portalai://pair?h=192.168.1.20&p=48123&f=abc_-&c=042917&n=Desk%20PC", text);
    }

    [Fact]
    public void QrTextCarriesTheRelayAddressOnlyWhenGiven()
    {
        var on = RemoteNetwork.QrText("192.168.1.20", 48123, "abc_-", "042917", "Desk PC", RemoteApi.RelayDefaultUrl);
        Assert.Equal("portalai://pair?h=192.168.1.20&p=48123&f=abc_-&c=042917&n=Desk%20PC" +
            "&r=https://portal-relay.portalai.workers.dev", on);
        Assert.DoesNotContain("&r=", RemoteNetwork.QrText("192.168.1.20", 48123, "abc_-", "042917", "Desk PC", null));
        Assert.DoesNotContain("&r=", RemoteNetwork.QrText("192.168.1.20", 48123, "abc_-", "042917", "Desk PC", ""));
    }

    [Fact]
    public void LongestRealisticQrTextStillFitsTheQrCode()
    {
        var fingerprint = new string('A', 43);
        var relay = "https://" + new string('r', 60) + ".example.com"; // about 80 characters
        var text = RemoteNetwork.QrText("192.168.100.100", 48133, fingerprint, "ABCDEFGHJKMN", new string('N', 24), relay);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= 213);
        Assert.True(QrCode.Encode(text).Version <= 10);
    }

    [Fact]
    public void CertificateIsCreatedOnceAndReloadedWithTheSameFingerprint()
    {
        using var dir = new TempDir();
        var protector = new FakeKeyProtector();
        using var first = HostCertificate.LoadOrCreate(dir.Path, protector);
        using var second = HostCertificate.LoadOrCreate(dir.Path, protector);

        Assert.Equal(HostCertificate.Fingerprint(first), HostCertificate.Fingerprint(second));
        Assert.True(second.HasPrivateKey);
        Assert.Equal(1, protector.ProtectCalls);
        using var key = first.GetECDsaPrivateKey()!;
        Assert.Equal(256, key.KeySize);

        // The private key file only ever holds what the protector wrapped.
        Assert.StartsWith("FAKEDPAPI1", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(dir.File(HostCertificate.KeyFileName)), 0, 10));
    }

    [Fact]
    public void KeyThatCanNeverBeOpenedIsReplacedButATemporaryFailureIsNot()
    {
        using var dir = new TempDir();
        var protector = new FakeKeyProtector();
        byte[] original;
        using (var first = HostCertificate.LoadOrCreate(dir.Path, protector))
        {
            original = HostCertificate.Fingerprint(first);
        }

        protector.FailTransient = true;
        Assert.Throws<CryptographicException>(() => HostCertificate.LoadOrCreate(dir.Path, protector));
        protector.FailTransient = false;
        using (var same = HostCertificate.LoadOrCreate(dir.Path, protector))
        {
            Assert.Equal(original, HostCertificate.Fingerprint(same));
        }

        protector.FailUnprotect = true;
        using var replaced = HostCertificate.LoadOrCreate(dir.Path, protector);
        Assert.NotEqual(original, HostCertificate.Fingerprint(replaced));
    }

    [Fact]
    public void CertificateIsLongLivedAndForServerAuthOnly()
    {
        using var dir = new TempDir();
        using var cert = HostCertificate.LoadOrCreate(dir.Path, new FakeKeyProtector());
        Assert.True(cert.NotAfter > DateTime.UtcNow.AddYears(10));
        Assert.Equal("CN=Portal AI Host", cert.Subject);
        var usage = Assert.Single(cert.Extensions.OfType<X509EnhancedKeyUsageExtension>());
        Assert.Equal("1.3.6.1.5.5.7.3.1", Assert.Single(usage.EnhancedKeyUsages.Cast<Oid>()).Value);
    }
}
