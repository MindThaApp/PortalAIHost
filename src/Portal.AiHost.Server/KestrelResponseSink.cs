using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Server;

/// <summary>Writes <see cref="ApiPipeline"/> output to a Kestrel response. SSE writes are flushed at once.</summary>
internal sealed class KestrelResponseSink(HttpContext http, CancellationToken? clientAborted = null) : IResponseSink
{
    private static readonly byte[] DataPrefix = "data: "u8.ToArray();
    private static readonly byte[] EventEnd = "\n\n"u8.ToArray();
    private static readonly byte[] LineEnd = "\n"u8.ToArray();

    public CancellationToken ClientAborted => clientAborted ?? http.RequestAborted;

    public async ValueTask StartAsync(int statusCode, string contentType, IReadOnlyDictionary<string, string>? headers,
        CancellationToken ct)
    {
        var response = http.Response;
        response.StatusCode = statusCode;
        if (statusCode != StatusCodes.Status204NoContent)
        {
            response.ContentType = contentType;
        }

        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                response.Headers[name] = value;
            }
        }

        if (contentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            response.Headers.CacheControl = "no-cache";
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        }

        await response.StartAsync(ct).ConfigureAwait(false);
        if (contentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await response.Body.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) =>
        http.Response.Body.WriteAsync(data, ct);

    public async ValueTask WriteSseDataAsync(ReadOnlyMemory<byte> json, CancellationToken ct)
    {
        var body = http.Response.Body;
        await body.WriteAsync(DataPrefix, ct).ConfigureAwait(false);
        await body.WriteAsync(json, ct).ConfigureAwait(false);
        await body.WriteAsync(EventEnd, ct).ConfigureAwait(false);
        await body.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask WriteSseCommentAsync(string comment, CancellationToken ct)
    {
        var line = comment.StartsWith(':') ? comment : ": " + comment;
        var body = http.Response.Body;
        await body.WriteAsync(Encoding.UTF8.GetBytes(line.ReplaceLineEndings(" ")), ct).ConfigureAwait(false);
        await body.WriteAsync(LineEnd, ct).ConfigureAwait(false);
        await body.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask CompleteAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await http.Response.CompleteAsync().ConfigureAwait(false);
    }
}
