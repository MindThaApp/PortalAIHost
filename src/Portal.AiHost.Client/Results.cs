using Portal.AiHost.Contracts;

namespace Portal.AiHost.Client;

/// <summary>The outcome of <see cref="AiHostClient.ChatAsync"/>.</summary>
public sealed class ChatResult
{
    /// <summary>The reply text of the first choice (empty if none).</summary>
    public string Text { get; init; } = "";

    /// <summary>The full host response; null when <see cref="Source"/> is <see cref="AiSource.Fallback"/>.</summary>
    public ChatCompletionResponse? Response { get; init; }

    /// <summary>The model id that served the call.</summary>
    public string? Model { get; init; }

    /// <summary>Who answered.</summary>
    public AiSource Source { get; init; }

    /// <summary>Why the fallback was used, for display; null when the host answered.</summary>
    public string? FallbackReason { get; init; }
}

/// <summary>One piece of a streamed chat reply.</summary>
public sealed class ChatDelta
{
    /// <summary>New reply text, if any.</summary>
    public string? Text { get; init; }

    /// <summary>New reasoning text, if any.</summary>
    public string? ReasoningText { get; init; }

    /// <summary>Queue or model-load progress from a <c>: portal</c> comment, if this piece is one.</summary>
    public PortalProgressEvent? Progress { get; init; }

    /// <summary>Tool-call fragments, if any.</summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>Set on the last content piece.</summary>
    public string? FinishReason { get; init; }

    /// <summary>Token usage, when requested with <c>stream_options.include_usage</c>.</summary>
    public Usage? Usage { get; init; }

    /// <summary>The model id that serves the stream, when known.</summary>
    public string? Model { get; init; }

    /// <summary>Who is answering.</summary>
    public AiSource Source { get; init; }

    /// <summary>Why the fallback was used, for display; null when the host answers.</summary>
    public string? FallbackReason { get; init; }
}

/// <summary>The outcome of <see cref="AiHostClient.EmbedAsync"/>.</summary>
public sealed class EmbedResult
{
    /// <summary>One vector per input, in order.</summary>
    public IReadOnlyList<float[]> Vectors { get; init; } = Array.Empty<float[]>();

    /// <summary>The model id. Store it with the vectors; never compare vectors from different models.</summary>
    public string ModelId { get; init; } = "";

    /// <summary>Who answered.</summary>
    public AiSource Source { get; init; }

    /// <summary>Why the fallback was used; null when the host answered.</summary>
    public string? FallbackReason { get; init; }
}

/// <summary>The outcome of <see cref="AiHostClient.RequestAccessAsync"/>.</summary>
/// <param name="State">The client state after the call.</param>
/// <param name="Status">The access-request status: pending, approved, denied, expired, claimed or unavailable.</param>
public sealed record AccessResult(AiHostState State, string Status);

/// <summary>Arguments of <see cref="AiHostClient.StateChanged"/>.</summary>
public sealed class AiHostStateChangedEventArgs(AiHostState oldState, AiHostState newState) : EventArgs
{
    /// <summary>The previous state.</summary>
    public AiHostState OldState { get; } = oldState;

    /// <summary>The new state.</summary>
    public AiHostState NewState { get; } = newState;
}

/// <summary>Arguments of <see cref="AiHostClient.FallbackActivated"/>.</summary>
public sealed class FallbackActivatedEventArgs(string code, string message) : EventArgs
{
    /// <summary>The error code behind the fallback (an API code or a <see cref="ClientErrorCodes"/> value).</summary>
    public string Code { get; } = code;

    /// <summary>Text to show the user, for example "Home host offline — using this PC's model".</summary>
    public string Message { get; } = message;
}
