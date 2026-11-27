using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Core.Api;

/// <summary>Handles one matched route. Throw <see cref="ApiException"/> for API errors.</summary>
public delegate Task ApiHandler(ApiContext context);

/// <summary>Parsed X-Portal-* request headers (api-v1 section 4).</summary>
public sealed record PortalRequestOptions(
    RequestPriority Priority,
    int DeadlineMs,
    DateTimeOffset Deadline,
    bool FailIfLoading,
    string? ClientRequestId,
    int? ContextTokens = null);

/// <summary>Everything a route handler needs: the request, the caller, parsed options and response helpers.</summary>
public sealed class ApiContext
{
    private readonly TrackingSink _sink;

    internal ApiContext(ApiRequest request, TrackingSink sink, string requestId, TimeProvider time)
    {
        Request = request;
        _sink = sink;
        RequestId = requestId;
        Time = time;
    }

    public ApiRequest Request { get; }

    public string RequestId { get; }

    public TimeProvider Time { get; }

    /// <summary>Set for routes that need a token; null on the open auth routes.</summary>
    public ClientPrincipal? Principal { get; internal set; }

    public PortalRequestOptions Options { get; internal set; } = null!;

    /// <summary>Values of {placeholders} in the route template.</summary>
    public IReadOnlyDictionary<string, string> RouteValues { get; internal set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Headers added to the response when it starts (X-Portal-Model, X-Portal-Queue-Ms...).</summary>
    public Dictionary<string, string> ResponseHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cancelled when the client goes away.</summary>
    public CancellationToken Aborted => _sink.ClientAborted;

    public bool ResponseStarted => _sink.Started;

    /// <summary>Deserialises the body. Malformed JSON becomes 400 invalid_request with the field in param.</summary>
    public T ReadJson<T>(JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (Request.Body.IsEmpty)
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "A JSON request body is required.");
        }

        try
        {
            return JsonSerializer.Deserialize(Request.Body.Span, typeInfo) ??
                throw new ApiException(ErrorCodes.InvalidRequest, "The request body must be a JSON object.");
        }
        catch (JsonException ex)
        {
            var param = ex.Path is { Length: > 2 } p && p.StartsWith("$.", StringComparison.Ordinal) ? p[2..] : null;
            throw new ApiException(ErrorCodes.InvalidRequest, "The request body is not valid JSON for this endpoint.",
                param);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or FormatException)
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "The request body is not valid JSON for this endpoint.");
        }
    }

    /// <summary>Builds the scheduler request for this call (app, priority, deadline).</summary>
    public SchedulingRequest CreateSchedulingRequest(string kind, long estimatedCost = 0) => new()
    {
        AppId = Principal?.AppId ?? throw new InvalidOperationException("No authenticated caller."),
        Kind = kind,
        Priority = Options.Priority,
        Deadline = Options.Deadline,
        EstimatedCost = estimatedCost,
    };

    /// <summary>Records the queue wait in X-Portal-Queue-Ms (call before the response starts).</summary>
    public void SetQueueWait(TimeSpan wait) =>
        ResponseHeaders[HeaderNames.QueueMs] =
            ((long)Math.Max(0, wait.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture);

    public async Task WriteJsonAsync<T>(int statusCode, T value, JsonTypeInfo<T> typeInfo)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        await _sink.StartAsync(statusCode, "application/json; charset=utf-8", ResponseHeaders, Aborted)
            .ConfigureAwait(false);
        await _sink.WriteAsync(bytes, Aborted).ConfigureAwait(false);
        await _sink.CompleteAsync(Aborted).ConfigureAwait(false);
    }

    /// <summary>Writes a complete binary body (for example a WAV file) with its content type.</summary>
    public async Task WriteBytesAsync(int statusCode, string contentType, ReadOnlyMemory<byte> bytes)
    {
        await _sink.StartAsync(statusCode, contentType, ResponseHeaders, Aborted).ConfigureAwait(false);
        await _sink.WriteAsync(bytes, Aborted).ConfigureAwait(false);
        await _sink.CompleteAsync(Aborted).ConfigureAwait(false);
    }

    public async Task WriteEmptyAsync(int statusCode)
    {
        await _sink.StartAsync(statusCode, "application/json; charset=utf-8", ResponseHeaders, Aborted)
            .ConfigureAwait(false);
        await _sink.CompleteAsync(Aborted).ConfigureAwait(false);
    }

    /// <summary>Starts a 200 text/event-stream response. Later errors are sent in-stream by the pipeline.</summary>
    public ValueTask StartStreamAsync() =>
        _sink.StartAsync(200, "text/event-stream", ResponseHeaders, Aborted);

    public ValueTask WriteSseDataAsync<T>(T value, JsonTypeInfo<T> typeInfo) =>
        _sink.WriteSseDataAsync(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo), Aborted);

    public ValueTask WriteSseDoneAsync() => _sink.WriteSseDataAsync("[DONE]"u8.ToArray(), Aborted);

    public ValueTask WriteProgressAsync(PortalProgressEvent progress) =>
        _sink.WriteSseCommentAsync(AiHostJson.ProgressComment(progress), Aborted);

    public ValueTask CompleteAsync() => _sink.CompleteAsync(Aborted);

    // ---- raw pass-through for the client-mode forwarder (the body is another host's answer, copied as it is) ----

    internal ValueTask StartRawAsync(int statusCode, string contentType, IReadOnlyDictionary<string, string>? headers) =>
        _sink.StartAsync(statusCode, contentType, headers, Aborted);

    internal ValueTask WriteRawAsync(ReadOnlyMemory<byte> data) => _sink.WriteAsync(data, Aborted);

    internal ValueTask WriteSseRawDataAsync(ReadOnlyMemory<byte> payload) => _sink.WriteSseDataAsync(payload, Aborted);

    internal ValueTask WriteSseRawCommentAsync(string comment) => _sink.WriteSseCommentAsync(comment, Aborted);
}

/// <summary>Remembers whether and how the response started, so errors go out in the right form.</summary>
internal sealed class TrackingSink(IResponseSink inner) : IResponseSink
{
    public bool Started { get; private set; }
    public bool Streaming { get; private set; }
    public bool Completed { get; private set; }

    public CancellationToken ClientAborted => inner.ClientAborted;

    public ValueTask StartAsync(int statusCode, string contentType, IReadOnlyDictionary<string, string>? headers,
        CancellationToken ct)
    {
        if (Started)
        {
            throw new InvalidOperationException("The response has already started.");
        }

        Started = true;
        Streaming = contentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase);
        return inner.StartAsync(statusCode, contentType, headers, ct);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => inner.WriteAsync(data, ct);

    public ValueTask WriteSseDataAsync(ReadOnlyMemory<byte> json, CancellationToken ct) =>
        inner.WriteSseDataAsync(json, ct);

    public ValueTask WriteSseCommentAsync(string comment, CancellationToken ct) =>
        inner.WriteSseCommentAsync(comment, ct);

    public ValueTask CompleteAsync(CancellationToken ct)
    {
        if (Completed)
        {
            return ValueTask.CompletedTask;
        }

        Completed = true;
        return inner.CompleteAsync(ct);
    }
}
