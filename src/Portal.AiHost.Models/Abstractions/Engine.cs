using Portal.AiHost.Contracts;

namespace Portal.AiHost.Core.Abstractions;

/// <summary>Lifecycle of one supervised engine (architecture section 1).</summary>
public enum EngineState
{
    Stopped,
    Starting,
    Loading,
    Ready,
    Stopping,
    Failed,
}

/// <summary>
/// Where a model runs. Produced by the placement planner, consumed by an engine adapter.
/// Phase 1 fills one device; the list leaves room for multi-GPU later.
/// </summary>
public sealed record Placement
{
    /// <summary>vulkan or cpu (open string).</summary>
    public string Backend { get; init; } = "cpu";

    public IReadOnlyList<PlacementDevice> Devices { get; init; } = Array.Empty<PlacementDevice>();
    public int GpuLayers { get; init; }
    public int TotalLayers { get; init; }

    /// <summary>MoE expert layers kept on the CPU (--n-cpu-moe), null for dense models.</summary>
    public int? CpuMoeLayers { get; init; }

    public int ContextTokens { get; init; }
    public int ParallelSlots { get; init; } = 1;

    /// <summary>KV cache type, for example f16 or q8_0.</summary>
    public string KvType { get; init; } = "f16";
}

public sealed record PlacementDevice(string Name, string Vendor, int Layers);

/// <summary>Everything an engine needs to start one model.</summary>
public sealed record EngineLaunchSpec
{
    /// <summary>Engine kind, see <see cref="EngineKinds"/>.</summary>
    public string Engine { get; init; } = EngineKinds.LlamaServer;

    /// <summary>Model kind, see <see cref="ModelKinds"/>.</summary>
    public string Kind { get; init; } = ModelKinds.Chat;

    public string ModelId { get; init; } = "";
    public string DisplayName { get; init; } = "";

    /// <summary>Model files in load order; for split GGUFs the first part comes first.</summary>
    public IReadOnlyList<string> ModelFiles { get; init; } = Array.Empty<string>();

    public Placement Placement { get; init; } = new();
    public bool SupportsTools { get; init; }
    public bool SupportsReasoning { get; init; }

    /// <summary>Processor threads for generation (llama-server --threads), or null for the engine's own choice.</summary>
    public int? Threads { get; init; }

    /// <summary>Cap on the memory the engine process may commit (Windows job object), or null for none.</summary>
    public long? MemoryLimitBytes { get; init; }
}

public sealed record EngineStateChange(
    string Kind, EngineState State, double Progress = 0, string? Message = null);

/// <summary>Raised when the engine dies while serving. In-flight requests see engine_restarting.</summary>
public sealed class EngineCrashedException : Exception
{
    public EngineCrashedException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>An engine-side failure that maps to a stable API error code.</summary>
public sealed class EngineException : Exception
{
    public EngineException(string errorCode, string message, Exception? inner = null) : base(message, inner)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

/// <summary>
/// One supervised engine process for one model role (chat, embedding, later transcription).
/// The generic inference calls take and return contract types, so the host front-end is the same
/// whether the real child process (HTTP proxy) or a fake sits behind it.
/// Methods are safe to call concurrently; state changes are serialised internally.
/// </summary>
public interface IEngineSupervisor : IAsyncDisposable
{
    /// <summary>Model kind this supervisor serves.</summary>
    string Kind { get; }

    EngineState State { get; }

    /// <summary>Spec of the loaded or loading model, null when stopped.</summary>
    EngineLaunchSpec? Current { get; }

    /// <summary>Raised on every state change (admin pipe, /host/status).</summary>
    event EventHandler<EngineStateChange>? StateChanged;

    /// <summary>Starts the engine and completes when it is Ready. Fails with EngineException on error.</summary>
    Task StartAsync(EngineLaunchSpec spec, CancellationToken ct);

    /// <summary>Stops the engine and returns its memory. Idempotent.</summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>Non-streaming chat. Throws EngineCrashedException if the engine dies mid-request.</summary>
    Task<ChatCompletionResponse> ChatAsync(ChatCompletionRequest request, CancellationToken ct);

    /// <summary>Streaming chat. Cancelling ct frees the engine slot promptly.</summary>
    IAsyncEnumerable<ChatCompletionChunk> StreamChatAsync(ChatCompletionRequest request, CancellationToken ct);

    Task<EmbeddingsResponse> EmbedAsync(EmbeddingsRequest request, CancellationToken ct);

    /// <summary>Counts tokens exactly as the loaded model would.</summary>
    Task<int> CountTokensAsync(TokenizeRequest request, CancellationToken ct);

    /// <summary>
    /// Sets the operating-system priority of the engine process, now and for every later restart. Lets a host lower
    /// the engine (for example below normal while the user is working) or restore it. Returns false when there is no
    /// process to change or the system refused; the wanted value is still remembered for the next start.
    /// </summary>
    bool SetPriority(System.Diagnostics.ProcessPriorityClass priority) => false;

    /// <summary>The priority last requested, or null when none was.</summary>
    System.Diagnostics.ProcessPriorityClass? RequestedPriority => null;
}

/// <summary>An engine supervisor that can say which process it runs.</summary>
public interface IEngineProcess
{
    int? ProcessId { get; }
}
