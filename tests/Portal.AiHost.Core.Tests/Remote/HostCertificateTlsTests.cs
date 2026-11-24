using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Tests.Support;

namespace Portal.AiHost.Core.Tests.Remote;

public sealed class HostCertificateTlsTests
{
    [Fact]
    public async Task TheStoredCertificateWorksForARealTlsServerHandshake()
    {
        using var dir = new TempDir();
        HostCertificate.LoadOrCreate(dir.Path, new FakeKeyProtector()).Dispose();
        using var cert = HostCertificate.LoadOrCreate(dir.Path, new FakeKeyProtector());
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync();
                await using var tls = new SslStream(accepted.GetStream());
                await tls.AuthenticateAsServerAsync(cert);
                return tls.NegotiatedCipherSuite;
            });
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            byte[]? seen = null;
            await using var clientTls = new SslStream(client.GetStream(), false, (_, c, _, _) =>
            {
                seen = c?.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                return true;
            });
            await clientTls.AuthenticateAsClientAsync("any-name");

            await server;
            Assert.Equal(HostCertificate.Fingerprint(cert), seen);
        }
        finally
        {
            listener.Stop();
        }
    }
}
