using System.Globalization;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Server;

/// <summary>
/// Passes the response through and counts what the Devices view needs: output tokens (the engine's own
/// <c>completion_tokens</c> when it reports them, else one per streamed data event) and when the first byte left.
/// It never keeps or looks at the text beyond that number.
/// </summary>
internal sealed class UsageCountingSink(IResponseSink inner, TimeProvider time) : IResponseSink
{
    private static readonly byte[] Marker = "\"completion_tokens\":"u8.ToArray();

    private long _events;
    private long _reported;

    public long? FirstByte { get; private set; }

    public long Tokens => _reported > 0 ? _reported : _events;

    public CancellationToken ClientAborted => inner.ClientAborted;

    public ValueTask StartAsync(int statusCode, string contentType, IReadOnlyDictionary<string, string>? headers, CancellationToken ct) =>
        inner.StartAsync(statusCode, contentType, headers, ct);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        Note(data.Span, isEvent: false);
        return inner.WriteAsync(data, ct);
    }

    public ValueTask WriteSseDataAsync(ReadOnlyMemory<byte> json, CancellationToken ct)
    {
        Note(json.Span, isEvent: true);
        return inner.WriteSseDataAsync(json, ct);
    }

    public ValueTask WriteSseCommentAsync(string comment, CancellationToken ct) => inner.WriteSseCommentAsync(comment, ct);

    public ValueTask CompleteAsync(CancellationToken ct) => inner.CompleteAsync(ct);

    private void Note(ReadOnlySpan<byte> data, bool isEvent)
    {
        FirstByte ??= time.GetTimestamp();
        if (isEvent && !data.SequenceEqual("[DONE]"u8) && !data.StartsWith("{\"error\""u8))
        {
            _events++;
        }

        var at = data.IndexOf(Marker);
        if (at < 0)
        {
            return;
        }

        var digits = data[(at + Marker.Length)..];
        var n = 0;
        while (n < digits.Length && n < 12 && digits[n] is >= (byte)'0' and <= (byte)'9')
        {
            n++;
        }

        if (n > 0 && long.TryParse(System.Text.Encoding.ASCII.GetString(digits[..n]), NumberStyles.None,
            CultureInfo.InvariantCulture, out var count))
        {
            _reported = Math.Max(_reported, count);
        }
    }
}
