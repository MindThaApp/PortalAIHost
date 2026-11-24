using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;

namespace Portal.AiHost.Core.Remote.Client;

/// <summary>A server reply: status code and the (small) body text.</summary>
public readonly record struct RemoteReply(int Status, string Body);

/// <summary>One logical connection to the PC. The certificate check runs during the TLS handshake, before any request bytes.</summary>
public interface IRemoteChannel : IDisposable
{
    Task<RemoteReply> PostJsonAsync(string path, string json, CancellationToken ct);
}

/// <summary>Opens channels to the PC. Only the LAN connector exists in this step; the relay connector comes later.</summary>
public interface IRemoteConnector
{
    /// <summary>
    /// <paramref name="acceptCertificate"/> gets the DER bytes of the certificate the server presented. When it returns
    /// false the handshake is aborted and nothing is sent.
    /// </summary>
    IRemoteChannel Open(string address, int port, Func<byte[], bool> acceptCertificate);
}

/// <summary>HTTPS over TCP with certificate pinning instead of the system trust store (docs/remote-v1.md section 5).</summary>
public sealed class LanConnector : IRemoteConnector
{
    public IRemoteChannel Open(string address, int port, Func<byte[], bool> acceptCertificate)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            SslOptions = new SslClientAuthenticationOptions
            {
                // Accept-any is replaced by the caller's pin / code check; nothing is sent unless it returns true.
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is not null && acceptCertificate(cert.GetRawCertData()),
            },
        };
        var host = address.Contains(':') ? $"[{address}]" : address;
        return new Channel(new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri($"https://{host}:{port}"),
            Timeout = TimeSpan.FromSeconds(20),
        });
    }

    private sealed class Channel(HttpClient http) : IRemoteChannel
    {
        public async Task<RemoteReply> PostJsonAsync(string path, string json, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Version = new Version(1, 1),
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new StringContent(json, Encoding.UTF8, MediaTypeHeaderValue.Parse("application/json")),
            };
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new RemoteReply((int)response.StatusCode, body);
        }

        public void Dispose() => http.Dispose();
    }
}
