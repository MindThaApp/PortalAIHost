using Portal.AiHost.Contracts;

namespace Portal.AiHost.Contracts.Tests;

public sealed class PairingLinkTests
{
    private const string Lan = "portalai://pair?h=192.168.1.20&p=48123&f=abc_-&c=042917&n=Desk%20PC";

    [Fact]
    public void ReadsTheLanFieldsAndHasNoRelayWithoutR()
    {
        Assert.True(PairingLink.TryParse(Lan, out var link));
        Assert.Equal(new PairingLink("192.168.1.20", 48123, "abc_-", "042917", "Desk PC", null), link);
    }

    private const string Token = "p9IEbdYnU2WxrDjoY0IOpU7VtsDTSjCCDcfuYy1Z-0w";

    [Fact]
    public void ReadsTheAdmissionTokenOnlyTogetherWithARelay()
    {
        Assert.True(PairingLink.TryParse(Lan + "&r=https%3A%2F%2Frelay.example.com&a=" + Token, out var link));
        Assert.Equal(Token, link!.RelayAdmission);
        Assert.True(PairingLink.TryParse(Lan + "&a=" + Token, out var noRelay));
        Assert.Null(noRelay!.RelayAdmission);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("p9IEbdYnU2WxrDjoY0IOpU7VtsDTSjCCDcfuYy1Z-0wx")]
    [InlineData("p9IEbdYnU2WxrDjoY0IOpU7VtsDTSjCCDcfuYy1Z+0w")]
    [InlineData("")]
    public void AMalformedAdmissionTokenCountsAsNone(string a)
    {
        Assert.True(PairingLink.TryParse(Lan + "&r=https%3A%2F%2Frelay.example.com&a=" + a, out var link));
        Assert.Null(link!.RelayAdmission);
    }

    [Fact]
    public void ReadsTheEscapedRelayAddressAndIgnoresUnknownFields()
    {
        Assert.True(PairingLink.TryParse(Lan + "&x=1&r=https%3A%2F%2Fportal-relay.portalai.workers.dev", out var link));
        Assert.Equal("https://portal-relay.portalai.workers.dev", link!.RelayUrl);
    }

    [Theory]
    [InlineData("http%3A%2F%2Frelay.example.com")]
    [InlineData("ftp%3A%2F%2Frelay.example.com")]
    [InlineData("https%3A%2F%2Fuser%3Apw%40relay.example.com")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public void AnUnsafeRelayAddressCountsAsNoRelay(string r)
    {
        Assert.True(PairingLink.TryParse(Lan + "&r=" + r, out var link));
        Assert.Null(link!.RelayUrl);
    }

    [Fact]
    public void PlainHttpIsAcceptedForLoopbackOnly()
    {
        Assert.True(PairingLink.TryParse(Lan + "&r=http%3A%2F%2F127.0.0.1%3A8787", out var link));
        Assert.Equal("http://127.0.0.1:8787", link!.RelayUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://pair?h=1")]
    [InlineData("portalai://pair?h=1.2.3.4&p=0&f=x&c=y")]
    [InlineData("portalai://pair?h=1.2.3.4&p=1&c=y")]
    public void ABrokenLinkIsRefused(string? text) => Assert.False(PairingLink.TryParse(text, out _));
}
