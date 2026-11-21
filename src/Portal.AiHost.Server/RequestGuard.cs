using System.Buffers;
using Microsoft.AspNetCore.Http;

namespace Portal.AiHost.Server;

/// <summary>Browser and DNS-rebinding defence, and the body limit enforced while the body is read.</summary>
public static class RequestGuard
{
    /// <summary>
    /// True only for <c>127.0.0.1:port</c>, <c>[::1]:port</c> or <c>localhost:port</c> (case-insensitive), with the
    /// port the listener really bound. Anything else (another name, no port, another port) is refused.
    /// </summary>
    public static bool IsAllowedHost(string? host, int port)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        var suffix = ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return string.Equals(host, "127.0.0.1" + suffix, StringComparison.Ordinal) ||
            string.Equals(host, "[::1]" + suffix, StringComparison.Ordinal) ||
            string.Equals(host, "localhost" + suffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Both ends of the TCP connection must be loopback; anything else is dropped unanswered.</summary>
    public static bool IsLoopbackConnection(System.Net.IPAddress? remote, System.Net.IPAddress? local) =>
        remote is not null && local is not null && System.Net.IPAddress.IsLoopback(remote) &&
        System.Net.IPAddress.IsLoopback(local);

    /// <summary>Any Origin header means a browser; no legitimate client sends one.</summary>
    public static bool HasOrigin(HttpRequest request) => request.Headers.ContainsKey("Origin");

    /// <summary>
    /// Origin or any Fetch Metadata header means a browser. A no-cors GET (an img or script tag) carries no Origin,
    /// but every current browser adds Sec-Fetch-*; native clients send none of them.
    /// </summary>
    public static bool IsBrowserRequest(HttpRequest request) =>
        HasOrigin(request) || request.Headers.ContainsKey("Sec-Fetch-Site") ||
        request.Headers.ContainsKey("Sec-Fetch-Mode") || request.Headers.ContainsKey("Sec-Fetch-Dest");

    public enum BodyResult
    {
        Ok,
        TooLarge,
    }

    /// <summary>
    /// Reads the body, refusing it as soon as more than <paramref name="maxBytes"/> arrive. A declared
    /// Content-Length over the limit is refused before any byte is read; chunked bodies are counted as they come.
    /// </summary>
    public static async Task<(BodyResult Result, byte[] Body)> ReadBodyAsync(HttpRequest request, int maxBytes,
        CancellationToken ct)
    {
        if (request.ContentLength is { } declared && declared > maxBytes)
        {
            return (BodyResult.TooLarge, []);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var body = new MemoryStream();
            while (true)
            {
                int n;
                try
                {
                    n = await request.Body.ReadAsync(buffer, ct).ConfigureAwait(false);
                }
                catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    return (BodyResult.TooLarge, []);
                }

                if (n == 0)
                {
                    return (BodyResult.Ok, body.ToArray());
                }

                if (body.Length + n > maxBytes)
                {
                    return (BodyResult.TooLarge, []);
                }

                body.Write(buffer, 0, n);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
