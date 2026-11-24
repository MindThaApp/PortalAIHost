using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;

namespace Portal.AiHost.Core.Remote.Client;

/// <summary>A server reply: status code and the (small) body text.</summary>
public readonly record struct RemoteReply(int Status, string Body, IReadOnlyList<KeyValuePair<string, string>>? Headers = null);

/// <summary>
/// A reply whose body is still being read: status and headers are known, the body arrives as a stream (server-sent
/// events are relayed chunk by chunk). Disposing it closes the connection to the PC, which stops the PC's work.
/// </summary>
public sealed class RemoteStreamReply(int status, IReadOnlyList<KeyValuePair<string, string>> headers, Stream body, IDisposable? owner = null)
    : IDisposable
{
    public int Status { get; } = status;

    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; } = headers;

    public Stream Body { get; } = body;

    public string? Header(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    public void Dispose()
    {
        Body.Dispose();
        owner?.Dispose();
    }
}

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

    /// <summary>
    /// Sends a request and returns as soon as the response headers arrive; the body is read by the caller. No overall
    /// timeout applies once the headers are in. Cancelling the token (or disposing the reply) closes the request.
    /// </summary>
    Task<RemoteStreamReply> OpenAsync(RemoteRequest request, CancellationToken ct);
}

/// <summary>Opens channels to the PC: <see cref="LanConnector"/> on the home network, <see cref="RelayConnector"/> through the relay.</summary>
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
            Timeout = System.Threading.Timeout.InfiniteTimeSpan, // short calls are bounded inside the channel; streams by the caller's deadline
        });
    }

    internal sealed class Channel(HttpClient http) : IRemoteChannel
    {
        private static readonly TimeSpan ShortCallTimeout = TimeSpan.FromSeconds(20);

        public async Task<RemoteReply> PostJsonAsync(string path, string json, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Version = new Version(1, 1),
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new StringContent(json, Encoding.UTF8, MediaTypeHeaderValue.Parse("application/json")),
            };
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(ShortCallTimeout);
            using var response = await http.SendAsync(request, limit.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
            return new RemoteReply((int)response.StatusCode, body);
        }

        private static HttpRequestMessage Build(RemoteRequest request)
        {
            var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Target)
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

            return message;
        }

        private static List<KeyValuePair<string, string>> HeadersOf(HttpResponseMessage response)
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (var h in response.Headers.Concat(response.Content.Headers))
            {
                list.Add(new(h.Key, string.Join(", ", h.Value)));
            }

            return list;
        }

        public async Task<RemoteReply> SendAsync(RemoteRequest request, CancellationToken ct)
        {
            using var message = Build(request);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(ShortCallTimeout);
            using var response = await http.SendAsync(message, limit.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
            return new RemoteReply((int)response.StatusCode, text, HeadersOf(response));
        }

        public async Task<RemoteStreamReply> OpenAsync(RemoteRequest request, CancellationToken ct)
        {
            var message = Build(request);
            HttpResponseMessage? response = null;
            try
            {
                response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return new RemoteStreamReply((int)response.StatusCode, HeadersOf(response), stream,
                    new Disposables(response, message));
            }
            catch
            {
                response?.Dispose();
                message.Dispose();
                throw;
            }
        }

        public void Dispose() => http.Dispose();

        private sealed class Disposables(params IDisposable[] items) : IDisposable
        {
            public void Dispose()
            {
                foreach (var item in items)
                {
                    item.Dispose();
                }
            }
        }
    }
}
