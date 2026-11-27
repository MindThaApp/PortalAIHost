using System.Collections.Concurrent;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.FakeEngine;

/// <summary>
/// In-process stand-in for the stable-diffusion.cpp engine. It writes the partial output file at the start of a run
/// (so cancel and failure have a partial to remove), reports step progress, and finishes the file at the end.
/// </summary>
public sealed class FakeGenerationEngine : IGenerationEngine
{
    private int _running;

    public bool Present { get; set; } = true;
    public bool GpuBuild { get; set; } = true;
    public int Steps { get; set; } = 4;
    public TimeSpan StepDelay { get; set; } = TimeSpan.FromMilliseconds(10);

    /// <summary>When set, every run waits for it after its first step (tests release it).</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>When set, a run fails with this code after its first step.</summary>
    public string? FailWith { get; set; }

    public ConcurrentQueue<GenerationLaunch> Launches { get; } = new();

    /// <summary>Most runs seen at the same time.</summary>
    public int MaxConcurrent { get; private set; }

    public int Running => Volatile.Read(ref _running);

    /// <summary>Raised once a run is past its first step (its partial file exists).</summary>
    public event Action<GenerationLaunch>? Started;

    public GenerationEngineCheck Check() =>
        Present ? new GenerationEngineCheck(true, GpuBuild, null) : new GenerationEngineCheck(false, false, "Not installed (fake).");

    public bool SupportsFamily(string family, string kind) =>
        kind == ModelKinds.Video ? family == GenerationFamilies.Wan : family != GenerationFamilies.Wan;

    public async Task RunAsync(GenerationLaunch launch, IProgress<GenerationProgress>? progress, CancellationToken ct)
    {
        var now = Interlocked.Increment(ref _running);
        lock (this)
        {
            MaxConcurrent = Math.Max(MaxConcurrent, now);
        }

        try
        {
            Launches.Enqueue(launch);
            if (File.Exists(launch.OutputPath))
            {
                throw new InvalidOperationException("The output path already exists.");
            }

            await using (var partial = new FileStream(launch.OutputPath, FileMode.CreateNew, FileAccess.Write))
            {
                await partial.WriteAsync("partial"u8.ToArray(), ct);
            }

            progress?.Report(new GenerationProgress(GenerationStages.Sampling, 1, Steps, 0.1));
            Started?.Invoke(launch);
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(ct);
            }

            if (FailWith is { } code)
            {
                throw new EngineException(code, "Fake failure.");
            }

            for (var step = 2; step <= Steps; step++)
            {
                await Task.Delay(StepDelay, ct);
                progress?.Report(new GenerationProgress(GenerationStages.Sampling, step, Steps, 0.1 + 0.8 * step / Steps));
            }

            await File.WriteAllBytesAsync(launch.OutputPath, [0x89, 0x50, 0x4E, 0x47, .. "fake result"u8.ToArray()], CancellationToken.None);
            progress?.Report(new GenerationProgress(GenerationStages.Saving, 0, 0, 1));
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}
