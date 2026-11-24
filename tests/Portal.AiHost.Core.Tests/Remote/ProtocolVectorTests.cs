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
        var key = PairingCrypto.DeriveKey("M2GT-58X4-7K3Q");
        Assert.Equal("0181418c89c8b739fdf913fad5453c77b7a79765fe3ad773bd97dc29d1d4a674", Convert.ToHexStringLower(key));

        var clientNonce = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var hostNonce = Enumerable.Range(0x20, 32).Select(i => (byte)i).ToArray();
        var fingerprint = Enumerable.Range(0xA0, 32).Select(i => (byte)i).ToArray();
        var name = Encoding.UTF8.GetBytes("Laptop");
        var spki = Decode(Spki);

        Assert.Equal("AAECAwQFBgcICQoLDA0ODw", RemoteCodec.Encode(clientNonce));
        Assert.Equal("ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8", RemoteCodec.Encode(hostNonce));
        Assert.Equal("oKGio6SlpqeoqaqrrK2ur7CxsrO0tba3uLm6u7y9vr8", RemoteCodec.Encode(fingerprint));
        Assert.Equal("NVg6_dPR4WnirC8PK0LPSxFFQR4gsJGNCvwgMOVGcCk",
            RemoteCodec.Encode(PairingCrypto.Mac(key, "start", clientNonce, name, spki)));
        Assert.Equal("991fMO6W38DQnzcUWWOFFbUG8w2v-QLrc57y6aV4ju8",
            RemoteCodec.Encode(PairingCrypto.Mac(key, "host", clientNonce, name, spki, hostNonce, fingerprint)));
        Assert.Equal("TCO2jZIvRw75PKbJWmCQfcb46W7AjtTDRRdz2OO7WNc",
            RemoteCodec.Encode(PairingCrypto.Mac(key, "finish", clientNonce, name, spki, hostNonce, fingerprint)));
        Assert.Equal("pN69WGM6OotASkGecpTZ61KG80O80xYHQmrlC4OD3-I",
            RemoteCodec.Encode(PairingCrypto.Mac(key, "done", clientNonce, name, spki, hostNonce, fingerprint,
                Encoding.ASCII.GetBytes("dev_AAAAAAAAAAAAAAAAAAAAAA"))));
    }

    [Fact]
    public void TheDocumentedCodeCarriesTheStartOfTheCertificateHash()
    {
        var fingerprint = Enumerable.Range(0xA0, 32).Select(i => (byte)i).ToArray();
        Assert.True(PairingCrypto.CodeMatchesCertificate("M2GT-58X4-7K3Q", fingerprint));
        Assert.True(PairingCrypto.CodeMatchesCertificate("m2gt58x47k3q", fingerprint));
        Assert.False(PairingCrypto.CodeMatchesCertificate("M2GT-58X5-7K3Q", fingerprint));
        Assert.Equal("M2GT58X47K3Q", PairingCrypto.Normalize("m2gt-58x4-7k3q"));
        Assert.Equal("001100000000", PairingCrypto.Normalize("OoIl00000000"));
        Assert.Null(PairingCrypto.Normalize("M2GT-58X4-7K3"));
        Assert.Null(PairingCrypto.Normalize("M2GT-58X4-7K3U"));
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
