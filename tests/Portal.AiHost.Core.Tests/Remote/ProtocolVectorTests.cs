using System.Text;
using Portal.AiHost.Core.Remote;

namespace Portal.AiHost.Core.Tests.Remote;

/// <summary>The worked example in docs/remote-v1.md (section 8). A client implementation must reproduce these values.</summary>
public sealed class ProtocolVectorTests
{
    private const string Spki = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEB2Vj2tcXSTCV2wJ4C9XMN1QgGOXlr3m1X-JXp1YURY7eDf-WRBEzemJZumgn1_ssKU4F5bozdw09LrpkCbFALg";

    private static byte[] Decode(string text)
    {
        Assert.True(RemoteCodec.TryDecode(text, 256, out var bytes));
        return bytes;
    }

    [Fact]
    public void PairingProofsMatchTheDocumentedValues()
    {
        var key = PairingCrypto.DeriveKey("042917");
        Assert.Equal("5f3abb16fdf627e6606db39cb11c1770dae1e12118f241b72289f6095c40d017", Convert.ToHexStringLower(key));

        var clientNonce = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var hostNonce = Enumerable.Range(0x20, 32).Select(i => (byte)i).ToArray();
        var fingerprint = Enumerable.Range(0xA0, 32).Select(i => (byte)i).ToArray();
        var name = Encoding.UTF8.GetBytes("Laptop");
        var spki = Decode(Spki);

        Assert.Equal("AAECAwQFBgcICQoLDA0ODw", RemoteCodec.Encode(clientNonce));
        Assert.Equal("ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8", RemoteCodec.Encode(hostNonce));
        Assert.Equal("oKGio6SlpqeoqaqrrK2ur7CxsrO0tba3uLm6u7y9vr8", RemoteCodec.Encode(fingerprint));
        Assert.Equal("_mVgkyCbQBBPwtuB1KEJHycZ_DBsr2nphSyweWk4GpM",
            RemoteCodec.Encode(PairingCrypto.Mac(key, "start", clientNonce, name, spki)));
        Assert.Equal("9kf1LBbviBmOCTM1dfN_nICMUONW2izxZEI45QNgXIE",
            RemoteCodec.Encode(PairingCrypto.Mac(key, "host", clientNonce, name, spki, hostNonce, fingerprint)));
        Assert.Equal("V89eqyNSMfWmq7-5WEbN_c7isxKZLuGwU74AniA0xCY",
            RemoteCodec.Encode(PairingCrypto.Mac(key, "finish", clientNonce, name, spki, hostNonce, fingerprint)));
        Assert.Equal("zf7Hj8380FourQgCI-PG8zz7oXEMl9m-aj8f-sgIcOE",
            RemoteCodec.Encode(PairingCrypto.Mac(key, "done", clientNonce, name, spki, hostNonce, fingerprint,
                Encoding.ASCII.GetBytes("dev_AAAAAAAAAAAAAAAAAAAAAA"))));
    }

    [Fact]
    public void SigningStringMatchesTheDocumentedValue()
    {
        var text = Encoding.UTF8.GetString(RemoteSigning.SigningString("POST", "/v1/chat/completions", "1790000000",
            "AAECAwQFBgcICQoLDA0ODw", Encoding.UTF8.GetBytes("""{"model":"x"}""")));
        Assert.Equal(
            "PORTALAI1\nPOST\n/v1/chat/completions\n1790000000\nAAECAwQFBgcICQoLDA0ODw\n" +
            "78b0e11c6754fc9671c2034f4a068d3a0e53c1c22a5a33900dd46bc8cd9136f3", text);
    }
}
