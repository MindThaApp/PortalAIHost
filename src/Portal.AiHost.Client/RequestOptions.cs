namespace Portal.AiHost.Client;

/// <summary>Per-call controls. Every property is optional.</summary>
public sealed class RequestOptions
{
    /// <summary>Overrides <see cref="AiHostClientOptions.DefaultPriority"/>.</summary>
    public AiPriority? Priority { get; init; }

    /// <summary>
    /// Total time budget (queue, model load and generation). Sent as <c>X-Portal-Deadline-Ms</c>, clamped to
    /// 1 s - 1 h. When null, the host's default for the priority applies.
    /// </summary>
    public TimeSpan? Deadline { get; init; }

    /// <summary>Whether to wait for a model load. Default is the host's default (wait).</summary>
    public AiLoadBehavior? LoadBehavior { get; init; }

    /// <summary>Set to false to get an exception instead of a fallback answer. Default true.</summary>
    public bool AllowFallback { get; init; } = true;

    /// <summary>Echoed back by the host for your logs (up to 64 characters of <c>A-Za-z0-9._-</c>).</summary>
    public string? ClientRequestId { get; init; }
}
