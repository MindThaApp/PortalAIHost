namespace Portal.AiHost.Engine.Upscale;

public enum UpscaleEngineStatus
{
    Ready,
    NotInstalled,
    Broken,
    NeedsHardware,
}

/// <summary>Whether an upscale engine can run now, and why not when it cannot.</summary>
public sealed record UpscaleEngineState(UpscaleEngineStatus Status, string? Reason = null)
{
    public bool IsReady => Status == UpscaleEngineStatus.Ready;
}

/// <summary>One video segment: a folder of numbered PNG frames in, a new folder of upscaled frames out.</summary>
public sealed record UpscaleSegmentRequest(int Segment, string InputDir, string OutputDir, string Model, int Scale);

public sealed record UpscaleProgress(int Segment, int FramesDone, int FramesTotal);

/// <summary>An upscale engine failed; <see cref="Code"/> is a short stable word such as insufficient_memory.</summary>
public sealed class UpscaleEngineException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Common shape of the upscale back ends (the ONNX helper for Fast and Balanced, the Python add-on for Best).
/// Frames are written to <see cref="UpscaleSegmentRequest.OutputDir"/> under new names only; existing frames are
/// kept, which is how a resumed segment skips finished work.
/// </summary>
public interface IUpscaleEngine : IAsyncDisposable
{
    /// <summary>Tier this engine serves: fast, balanced or best.</summary>
    string Tier { get; }

    Task<UpscaleEngineState> CheckAsync(CancellationToken ct);

    /// <summary>Upscales one segment. Throws <see cref="UpscaleEngineException"/> or <see cref="OperationCanceledException"/>.</summary>
    Task UpscaleSegmentAsync(UpscaleSegmentRequest request, IProgress<UpscaleProgress>? progress, CancellationToken ct);
}
