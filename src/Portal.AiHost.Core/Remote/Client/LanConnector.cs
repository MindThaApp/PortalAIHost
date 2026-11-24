using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;

namespace Portal.AiHost.Core.Remote.Client;

/// <summary>A server reply: status code and the (small) body text.</summary>
public readonly record struct RemoteReply(int Status, string Body);

/// <summary>A request to send: method, target exactly as signed (path and query), extra headers and an optional body.</summary>
public sealed record RemoteRequest(
    string Method, string Target, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[]? Body = null,
    string ContentType = "application/json");

/// <summary>One logical connection to the PC. The certificate check runs during the TLS handshake, before any request bytes.</summary>
public interface IRemoteChannel : IDisposable
{
    Task<RemoteReply> PostJsonAsync(string path, string json, CancellationToken ct);

    /// <summary>Sends any request on this channel's (kept-alive) connection and reads the whole small reply.</summary>
    Task<RemoteReply> SendAsync(RemoteRequest request, CancellationToken ct);
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
public sealed class LanConnector(TimeSpan? connectTimeout = null) : IRemoteConnector
{
    /// <summary>Connect timeout of the link on the home network (docs/client-mode-v1.md section 3.1).</summary>
    public static readonly TimeSpan LinkConnectTimeout = TimeSpan.FromSeconds(1.5);

    public IRemoteChannel Open(string address, int port, Func<byte[], bool> acceptCertificate)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(8),
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

        public async Task<RemoteReply> SendAsync(RemoteRequest request, CancellationToken ct)
        {
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Target)
            {
                Version = new Version(1, 1),
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            foreach (var (name, value) in request.Headers)
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }

            if (request.Body is { } body)
            {
                message.Content = new ByteArrayContent(body);
                message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
            }

            using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new RemoteReply((int)response.StatusCode, text);
        }

        public void Dispose() => http.Dispose();
    }
}
