using Portal.AiHost.Contracts;

namespace Portal.AiHost.Core.Abstractions;

/// <summary>How a request reached the host. Phases 2-3 add LAN and relay.</summary>
public enum Transport
{
    Loopback,
    Lan,
    Relay,
}

/// <summary>The authenticated caller: app, device and transport, passed through the pipeline.</summary>
public sealed record ClientPrincipal(string AppId, string DeviceId, Transport Transport, string TokenId)
{
    public const string LocalDevice = "local";
}

/// <summary>
/// Identity of the calling process, shown to the user in the approval prompt. Verified means Windows reported a
/// package identity for the process; it says nothing about who made the app. PortalApp is set only for a
/// Store-installed package of the Portal publisher (see <see cref="Auth.PortalPublisher"/>). PackagePublisher is the
/// publisher display name from the package itself, set only for Store or Inbox packages.
/// </summary>
public sealed record CallerIdentity(
    int ProcessId, string? PackageFamilyName, string? ExePath, bool Verified, bool SameUser, bool PortalApp = false,
    string? PackagePublisher = null);

public sealed record AccessRequestInfo(
    string RequestId,
    string AppId,
    string AppName,
    string? AppVersion,
    string? Reason,
    CallerIdentity Caller,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>Outcome of an access request poll. Token is non-null once, when first approved and collected.</summary>
public sealed record AccessRequestResult(string Status, string? Token, string? TokenId, string? AppId)
{
    /// <summary>Never prints the token, so logging a result cannot leak it.</summary>
    public override string ToString() =>
        $"AccessRequestResult {{ Status = {Status}, Token = {(Token is null ? "null" : "[redacted]")}, " +
        $"TokenId = {TokenId}, AppId = {AppId} }}";
}

public sealed record RegisteredClient(
    string TokenId,
    string AppId,
    string AppName,
    string? PackageFamilyName,
    string DeviceId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    bool Revoked);

/// <summary>Outcome of checking a bearer token. ErrorCode is one of the 401 codes when not valid.</summary>
public sealed record TokenValidation(ClientPrincipal? Principal, string? ErrorCode)
{
    public bool IsValid => Principal is not null;
}

/// <summary>
/// App registry and token authority. Tokens are random, only an HMAC of each is kept, and a denied
/// app is refused for 24 hours unless the user re-allows it.
/// </summary>
public interface IClientRegistry
{
    /// <summary>Creates a pending request. Throws ApiException for limit errors (rate_limited, access_denied).</summary>
    Task<AccessRequestInfo> CreateRequestAsync(AuthRequestCreate request, CallerIdentity caller, CancellationToken ct);

    /// <summary>Long-polls until the status changes or the wait ends.</summary>
    Task<AccessRequestResult?> WaitForRequestAsync(string requestId, TimeSpan wait, CancellationToken ct);

    IReadOnlyList<AccessRequestInfo> PendingRequests { get; }

    /// <summary>Raised when a request needs the user's decision.</summary>
    event EventHandler<AccessRequestInfo>? RequestPending;

    Task ApproveAsync(string requestId, CancellationToken ct);

    Task DenyAsync(string requestId, CancellationToken ct);

    /// <summary>Clears the 24 hour cool-down so the app may ask again.</summary>
    Task ReAllowAsync(string appId, CancellationToken ct);

    /// <summary>Validates "Bearer ..." token text and records last use.</summary>
    Task<TokenValidation> ValidateAsync(string token, CancellationToken ct);

    Task<IReadOnlyList<RegisteredClient>> ListAsync(CancellationToken ct);

    /// <summary>Revokes by token id (user in the host, or the client itself via DELETE /host/auth/token).</summary>
    Task RevokeAsync(string tokenId, CancellationToken ct);
}

/// <summary>Throw to produce a specific API error. The pipeline turns it into the error envelope.</summary>
public sealed class ApiException : Exception
{
    public ApiException(string code, string message, string? param = null, int? retryAfterMs = null)
        : base(message)
    {
        Code = code;
        Param = param;
        RetryAfterMs = retryAfterMs;
    }

    public string Code { get; }
    public string? Param { get; }
    public int? RetryAfterMs { get; }
    public Dictionary<string, System.Text.Json.JsonElement>? Details { get; init; }
}

/// <summary>Request class: interactive goes before batch.</summary>
public enum RequestPriority
{
    Interactive,
    Batch,
}

/// <summary>What a request wants from the scheduler.</summary>
public sealed record SchedulingRequest
{
    public required string AppId { get; init; }

    /// <summary>Model kind the job needs a slot of.</summary>
    public string Kind { get; init; } = ModelKinds.Chat;

    public RequestPriority Priority { get; init; } = RequestPriority.Interactive;

    /// <summary>Absolute deadline covering queue, model load and generation.</summary>
    public DateTimeOffset Deadline { get; init; }

    /// <summary>Rough cost hint (for example prompt tokens), used for fairness estimates.</summary>
    public long EstimatedCost { get; init; }
}

/// <summary>Held while a request runs on an engine slot. Disposing frees the slot.</summary>
public interface ISchedulerLease : IAsyncDisposable
{
    TimeSpan QueueWait { get; }
}

public sealed record QueueSnapshot(
    int Running, int WaitingInteractive, int WaitingBatch, IReadOnlyDictionary<string, int> WaitingByApp)
{
    public int Waiting => WaitingInteractive + WaitingBatch;
}

/// <summary>Progress notifications while a request waits (turned into ": portal" comments).</summary>
public enum QueueEventKind
{
    Queued,
    Started,
}

public sealed record QueueEvent(QueueEventKind Kind, int Position);

/// <summary>
/// Fair queue: interactive before batch, round-robin across apps, one slot kept for interactive
/// when there is more than one, per-app and per-host queue caps (queue_full).
/// </summary>
public interface IScheduler
{
    /// <summary>
    /// Waits for a slot. Throws ApiException queue_full or deadline_exceeded (phase queued),
    /// and OperationCanceledException when the caller disconnects (the queue entry is removed).
    /// </summary>
    Task<ISchedulerLease> AcquireAsync(SchedulingRequest request, IProgress<QueueEvent>? progress, CancellationToken ct);

    QueueSnapshot Snapshot();

    /// <summary>Changes the number of engine slots (set from the planner's parallel count).</summary>
    void SetSlots(string kind, int slots);
}

/// <summary>A parsed API request handed to the pipeline by a transport adapter.</summary>
public sealed record ApiRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public IReadOnlyDictionary<string, string> Query { get; init; } = new Dictionary<string, string>();

    /// <summary>Header names compared case-insensitively.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ReadOnlyMemory<byte> Body { get; init; }
    public ClientPrincipal? Principal { get; init; }
    public CallerIdentity? Caller { get; init; }
}

/// <summary>
/// Where the pipeline writes its answer. Kestrel, a LAN listener and the relay each implement it,
/// so the pipeline never touches transport types. Cancelled means the client went away.
/// </summary>
public interface IResponseSink
{
    /// <summary>Raised when the client disconnects.</summary>
    CancellationToken ClientAborted { get; }

    /// <summary>Starts the response. For streams, pass contentType text/event-stream.</summary>
    ValueTask StartAsync(int statusCode, string contentType, IReadOnlyDictionary<string, string>? headers, CancellationToken ct);

    /// <summary>Writes body bytes (a complete JSON document, or part of a stream).</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>Writes one SSE data event and flushes. The payload is JSON, or [DONE].</summary>
    ValueTask WriteSseDataAsync(ReadOnlyMemory<byte> json, CancellationToken ct);

    /// <summary>Writes an SSE comment line (": portal {...}") and flushes.</summary>
    ValueTask WriteSseCommentAsync(string comment, CancellationToken ct);

    /// <summary>Ends the response.</summary>
    ValueTask CompleteAsync(CancellationToken ct);
}
