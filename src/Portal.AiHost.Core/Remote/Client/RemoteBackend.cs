using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Api;
using Portal.AiHost.Core.Runtime;

namespace Portal.AiHost.Core.Remote.Client;

public sealed class RemoteBackendOptions
{
    /// <summary>How long a capabilities answer from the PC is reused (docs/client-mode-v1.md section 4.2).</summary>
    public TimeSpan CapabilitiesCache { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the laptop sends its own "queued" comment while the PC has not answered yet (section 4.4).</summary>
    public TimeSpan QueuedCommentInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The PC ends first: it gets the remaining budget minus this much (section 4.3).</summary>
    public TimeSpan DeadlineMargin { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Largest error body read in full to tell the PC's answers apart.</summary>
    public int MaxErrorBytes { get; init; } = 1024 * 1024;

    /// <summary>Display name of the calling app for <c>X-Portal-Client-App</c>; the app id is used when null.</summary>
    public Func<ClientPrincipal, string?>? AppName { get; init; }

    /// <summary>
    /// Seam for the laptop-model fallback (step 5): called with the error that would be sent when the PC cannot be
    /// reached, before anything was written. Return true when the request was answered some other way.
    /// </summary>
    public Func<InferenceRoute, ApiContext, ApiException, Task<bool>>? OnUnreachable { get; init; }
}

/// <summary>
/// Client mode: forwards the allow-listed model endpoints to the paired PC through <see cref="RemoteHostLink"/>
/// (docs/client-mode-v1.md section 4). Nothing the local app sent for its own sake leaves this host: the app's bearer
/// token is dropped (the link adds the host's own token and the device signature), and only the headers named in 4.3
/// are copied. Event streams are relayed event by event as they arrive; closing the app's connection closes the
/// request to the PC.
/// </summary>
public sealed class RemoteBackend(RemoteHostLink link, RemoteBackendOptions? options = null, TimeProvider? time = null)
    : IInferenceBackend
{
    private readonly RemoteBackendOptions _o = options ?? new RemoteBackendOptions();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _cacheGate = new();
    private (byte[] Body, DateTimeOffset Expires)? _capabilities;

    /// <summary>The endpoints forwarded in client mode: nothing else is ever sent to the PC.</summary>
    public static IReadOnlyDictionary<InferenceRoute, (string Method, string Target)> Allowed { get; } =
        new Dictionary<InferenceRoute, (string, string)>
        {
            [InferenceRoute.Capabilities] = ("GET", Routes.Capabilities),
            [InferenceRoute.Models] = ("GET", Routes.Models),
            [InferenceRoute.Performance] = ("GET", Routes.Performance),
            [InferenceRoute.Health] = ("GET", Routes.Health),
            [InferenceRoute.EaseOffSet] = ("POST", Routes.EaseOff),
            [InferenceRoute.EaseOffClear] = ("DELETE", Routes.EaseOff),
            [InferenceRoute.Warmup] = ("POST", Routes.Warmup),
            [InferenceRoute.Tokenize] = ("POST", Routes.Tokenize),
            [InferenceRoute.ChatCompletions] = ("POST", Routes.ChatCompletions),
            [InferenceRoute.Embeddings] = ("POST", Routes.Embeddings),
        };

    /// <summary>Label only (docs/client-mode-v1.md section 5.7): never used for access decisions.</summary>
    public const string ClientAppHeader = "X-Portal-Client-App";

    private static readonly string[] ForwardedHeaders =
    [
        HeaderNames.Priority, HeaderNames.Load, HeaderNames.ContextTokens, HeaderNames.ClientRequestId,
    ];

    private static readonly string[] ReturnedHeaders =
    [
        HeaderNames.RequestId, HeaderNames.QueueMs, HeaderNames.Model, HeaderNames.RetryAfter,
    ];

    /// <summary>The PC's own refusals of this device (docs/client-mode-v1.md section 4.5) and the reason apps see.</summary>
    private static readonly Dictionary<string, string> PausedReasons = new(StringComparer.Ordinal)
    {
        ["device_paused"] = "paused",
        ["device_quiet_hours"] = "quiet_hours",
        ["host_busy_gaming"] = "gaming_busy",
        ["device_limit_reached"] = "limit_reached",
    };

    public async Task HandleAsync(InferenceRoute route, ApiContext context)
    {
        if (!Allowed.TryGetValue(route, out var endpoint))
        {
            throw new ApiException(ErrorCodes.NotFound, "Unknown route.");
        }

        if (route == InferenceRoute.Capabilities && TryCachedCapabilities() is { } cached)
        {
            await WriteCapabilitiesAsync(context, cached).ConfigureAwait(false);
            return;
        }

        var budget = context.Options.Deadline - _time.GetUtcNow();
        using var deadline = new CancellationTokenSource();
        deadline.CancelAfter(budget > TimeSpan.Zero ? budget : TimeSpan.FromMilliseconds(1));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.Aborted, deadline.Token);
        try
        {
            await ForwardAsync(route, endpoint, context, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !context.Aborted.IsCancellationRequested)
        {
            throw new ApiException(ErrorCodes.DeadlineExceeded, "The request deadline passed.");
        }
    }

    private async Task ForwardAsync(InferenceRoute route, (string Method, string Target) endpoint, ApiContext context,
        CancellationToken ct)
    {
        var request = BuildRequest(endpoint, context);
        var early = route == InferenceRoute.ChatCompletions && WantsStream(context.Request.Body.Span);
        RemoteStreamReply reply;
        try
        {
            var opening = OpenWithBatchRetryAsync(request, endpoint, context, ct);
            if (early)
            {
                // If the PC is slow to answer, the app's stream opens anyway and gets a "queued" comment every few
                // seconds, so its idle timer behaves as with a local host.
                while (!opening.IsCompleted)
                {
                    var tick = Task.Delay(_o.QueuedCommentInterval, _time, ct);
                    if (await Task.WhenAny(opening, tick).ConfigureAwait(false) == opening)
                    {
                        break;
                    }

                    if (!context.ResponseStarted)
                    {
                        await context.StartStreamAsync().ConfigureAwait(false);
                    }

                    await context.WriteSseRawCommentAsync(": portal {\"event\":\"queued\"}").ConfigureAwait(false);
                }
            }

            reply = await opening.ConfigureAwait(false);
        }
        catch (RemoteUnreachableException ex)
        {
            var error = Unreachable(ex.Reason, ex.RetryAfterMs);
            if (!context.ResponseStarted && _o.OnUnreachable is { } fallback &&
                await fallback(route, context, error).ConfigureAwait(false))
            {
                return;
            }

            throw error;
        }

        using (reply)
        {
            var isStream = (reply.Header("Content-Type") ?? "").StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase);
            if (reply.Status >= 400 || (early && !isStream))
            {
                await RelayErrorOrBodyAsync(context, reply, ct).ConfigureAwait(false);
                return;
            }

            CopyHeaders(reply, context);
            if (isStream)
            {
                if (!context.ResponseStarted)
                {
                    await context.StartStreamAsync().ConfigureAwait(false);
                }

                await RelayEventsAsync(context, reply, ct).ConfigureAwait(false);
                await context.CompleteAsync().ConfigureAwait(false);
                return;
            }

            if (route == InferenceRoute.Capabilities && reply.Status == 200)
            {
                var body = await ReadAllAsync(reply.Body, _o.MaxErrorBytes * 4, ct).ConfigureAwait(false);
                lock (_cacheGate)
                {
                    _capabilities = (body, _time.GetUtcNow() + _o.CapabilitiesCache);
                }

                await WriteCapabilitiesAsync(context, body).ConfigureAwait(false);
                return;
            }

            await context.StartRawAsync(reply.Status, ContentTypeOf(reply), context.ResponseHeaders).ConfigureAwait(false);
            await CopyBodyAsync(context, reply, ct).ConfigureAwait(false);
            await context.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens the request on the PC. A batch request that the relay refused for its budget (nothing was sent) waits the
    /// 60 seconds the relay asks for and tries once more, when its deadline leaves room; interactive requests fail at
    /// once (docs/client-mode-v1.md section 4.6). The request is built again so the PC gets the deadline that is left.
    /// </summary>
    private async Task<RemoteStreamReply> OpenWithBatchRetryAsync(RemoteRequest request,
        (string Method, string Target) endpoint, ApiContext context, CancellationToken ct)
    {
        try
        {
            return await link.OpenStreamAsync(request, ct).ConfigureAwait(false);
        }
        catch (RemoteUnreachableException ex) when (ex.Reason == RemoteLinkReasons.RelayBusy
            && context.Options.Priority == RequestPriority.Batch)
        {
            var wait = TimeSpan.FromMilliseconds(ex.RetryAfterMs ?? RelayException.BudgetRetryMs);
            if (context.Options.Deadline - _time.GetUtcNow() <= wait + _o.DeadlineMargin)
            {
                throw;
            }

            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
            return await link.OpenStreamAsync(BuildRequest(endpoint, context), ct).ConfigureAwait(false);
        }
    }

    // ---- request ------------------------------------------------------------------------------------------------

    private RemoteRequest BuildRequest((string Method, string Target) endpoint, ApiContext context)
    {
        // Only these headers leave the host. The app's Authorization (and anything else) stays here.
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var name in ForwardedHeaders)
        {
            if (context.Request.Headers.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
            {
                headers.Add(new(name, value));
            }
        }

        // The PC ends first: it gets what is left of the deadline minus a margin (never less than the API minimum).
        var left = (long)(context.Options.Deadline - _time.GetUtcNow() - _o.DeadlineMargin).TotalMilliseconds;
        headers.Add(new(HeaderNames.DeadlineMs,
            Math.Clamp(left, ApiLimits.MinDeadlineMs, ApiLimits.MaxDeadlineMs).ToString(CultureInfo.InvariantCulture)));
        headers.Add(new(ClientAppHeader, ClientAppLabel(context.Principal)));
        var body = endpoint.Method == "POST" ? context.Request.Body.ToArray() : null;
        return new RemoteRequest(endpoint.Method, endpoint.Target, headers, body);
    }

    private string ClientAppLabel(ClientPrincipal? principal)
    {
        var name = principal is null ? null : _o.AppName?.Invoke(principal) ?? principal.AppId;
        return SanitizeAppName(name);
    }

    /// <summary>At most 64 characters, control characters removed, empty becomes "Unknown app".</summary>
    public static string SanitizeAppName(string? name)
    {
        var clean = new StringBuilder();
        foreach (var c in name ?? "")
        {
            if (!char.IsControl(c))
            {
                clean.Append(c);
            }
        }

        var text = clean.ToString().Trim();
        if (text.Length > 64)
        {
            text = text[..64];
        }

        return text.Length == 0 ? "Unknown app" : text;
    }

    private static bool WantsStream(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            using var doc = JsonDocument.ParseValue(ref reader);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ---- answers ------------------------------------------------------------------------------------------------

    private static string ContentTypeOf(RemoteStreamReply reply) =>
        reply.Header("Content-Type") ?? "application/json; charset=utf-8";

    private static void CopyHeaders(RemoteStreamReply reply, ApiContext context)
    {
        foreach (var name in ReturnedHeaders)
        {
            if (reply.Header(name) is { Length: > 0 } value)
            {
                context.ResponseHeaders[name] = value;
            }
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream body, int max, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > max)
            {
                throw new ApiException(ErrorCodes.InternalError, "The answer from the PC was too large.");
            }
        }

        return ms.ToArray();
    }

    private async Task CopyBodyAsync(ApiContext context, RemoteStreamReply reply, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        int n;
        try
        {
            while ((n = await reply.Body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await context.WriteRawAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            link.ReportStreamDropped();
            throw Unreachable(RemoteLinkReasons.Offline, link.RetryAfterMs);
        }
    }

    private async Task RelayErrorOrBodyAsync(ApiContext context, RemoteStreamReply reply, CancellationToken ct)
    {
        byte[] body;
        try
        {
            body = await ReadAllAsync(reply.Body, _o.MaxErrorBytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            link.ReportStreamDropped();
            throw Unreachable(RemoteLinkReasons.Offline, link.RetryAfterMs);
        }

        var (code, message, details, retryMs) = ParseError(body);
        if (code is not null && PausedReasons.TryGetValue(code, out var reason))
        {
            throw Unreachable(reason, retryMs ?? RetryHeaderMs(reply));
        }

        if (context.ResponseStarted)
        {
            // The app's stream is already open: the PC's answer becomes the in-stream error event.
            throw new ApiException(code is not null && ErrorCatalog.TryGet(code, out _) ? code : ErrorCodes.EngineError,
                message ?? "The PC answered with an error.", retryAfterMs: retryMs) { Details = details };
        }

        CopyHeaders(reply, context);
        await context.StartRawAsync(reply.Status, ContentTypeOf(reply), context.ResponseHeaders).ConfigureAwait(false);
        await context.WriteRawAsync(body).ConfigureAwait(false);
        await context.CompleteAsync().ConfigureAwait(false);
    }

    private static int? RetryHeaderMs(RemoteStreamReply reply) =>
        int.TryParse(reply.Header(HeaderNames.RetryAfter), NumberStyles.None, CultureInfo.InvariantCulture, out var s)
            ? s * 1000
            : null;

    private static (string? Code, string? Message, Dictionary<string, JsonElement>? Details, int? RetryMs) ParseError(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var e) || e.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            var code = e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var message = e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            Dictionary<string, JsonElement>? details = null;
            if (e.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                details = d.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            }

            int? retry = e.TryGetProperty("retry_after_ms", out var r) && r.ValueKind == JsonValueKind.Number &&
                r.TryGetInt32(out var ms) ? ms : null;
            return (code, message, details, retry);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary><c>503 remote_unreachable</c> with the reason in <c>details.reason</c> (section 4.5).</summary>
    public static ApiException Unreachable(string reason, int? retryAfterMs) =>
        new(ErrorCodes.RemoteUnreachable, "The PC that runs the AI cannot be reached now.", retryAfterMs: retryAfterMs)
        {
            Details = new() { ["reason"] = JsonSerializer.SerializeToElement(reason, AiHostJsonContext.Default.String) },
        };

    // ---- events -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Copies the PC's event stream to the app one event at a time, flushed as each arrives. A stream that ends
    /// without the closing marker or an error event was cut: the app gets an in-stream <c>remote_unreachable</c> and
    /// nothing is retried (partial text must not be repeated).
    /// </summary>
    private async Task RelayEventsAsync(ApiContext context, RemoteStreamReply reply, CancellationToken ct)
    {
        using var reader = new StreamReader(reply.Body, Encoding.UTF8, false, 4096, leaveOpen: true);
        var data = new StringBuilder();
        var finished = false;
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    if (data.Length > 0)
                    {
                        var payload = data.ToString();
                        data.Clear();
                        finished |= payload == "[DONE]" || payload.StartsWith("{\"error\"", StringComparison.Ordinal);
                        await context.WriteSseRawDataAsync(Encoding.UTF8.GetBytes(payload)).ConfigureAwait(false);
                    }

                    continue;
                }

                if (line[0] == ':')
                {
                    await context.WriteSseRawCommentAsync(line).ConfigureAwait(false);
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    var value = line.AsSpan(5);
                    if (value.Length > 0 && value[0] == ' ')
                    {
                        value = value[1..];
                    }

                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException)
        {
            finished = false;
        }

        if (!finished)
        {
            link.ReportStreamDropped();
            throw Unreachable(RemoteLinkReasons.Offline, link.RetryAfterMs);
        }
    }

    // ---- capabilities -------------------------------------------------------------------------------------------

    private byte[]? TryCachedCapabilities()
    {
        lock (_cacheGate)
        {
            return _capabilities is { } c && c.Expires > _time.GetUtcNow() ? c.Body : null;
        }
    }

    /// <summary>
    /// The PC's limits and models, with a revision that changes when the PC's revision, the mode or the link state
    /// changes: a hash of the three (section 4.2).
    /// </summary>
    private async Task WriteCapabilitiesAsync(ApiContext context, byte[] remoteBody)
    {
        var node = JsonNode.Parse(remoteBody) as JsonObject ?? throw new ApiException(ErrorCodes.InternalError,
            "The PC sent an answer this host cannot read.");
        var remoteRevision = node["capabilities_revision"]?.ToString() ?? "";
        var state = link.Status.State.ToString();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{remoteRevision}|client|{state}"));
        var revision = BitConverter.ToInt64(hash, 0) & long.MaxValue;
        node["capabilities_revision"] = revision;
        context.ResponseHeaders[HeaderNames.CapabilitiesRevision] = revision.ToString(CultureInfo.InvariantCulture);
        await context.StartRawAsync(200, "application/json; charset=utf-8", context.ResponseHeaders).ConfigureAwait(false);
        await context.WriteRawAsync(Encoding.UTF8.GetBytes(node.ToJsonString())).ConfigureAwait(false);
        await context.CompleteAsync().ConfigureAwait(false);
    }
}
