using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Api;

namespace Portal.AiHost.Core.Runtime;

/// <summary>Where the health checks read the facts only the hosting process knows. Every member is read-only.</summary>
public sealed class HealthSources
{
    /// <summary>The engine builds that should be next to the host and whether each program file is there.</summary>
    public Func<IReadOnlyList<EngineFileStatus>>? EngineFiles { get; init; }

    /// <summary>Free bytes on the drive of the models folder, or null when unknown.</summary>
    public Func<long?>? StoreFreeBytes { get; init; }

    /// <summary>The reason a file check marked this model damaged, or null.</summary>
    public Func<string, string?>? DamagedReason { get; init; }
}

public sealed partial class AiHostRuntime
{
    /// <summary>
    /// Runs the health checks. Reads the same sources as the performance view and the model list; nothing is
    /// changed and no engine is started.
    /// </summary>
    public async Task<HealthReport> BuildHealthAsync(bool apiListening, CancellationToken ct)
    {
        var view = await BuildPerformanceViewAsync(null, withStops: false, ct).ConfigureAwait(false);
        var src = _o.Health;

        HealthModelInfo? model = null;
        if (GetActive(ModelKinds.Chat) is { } active)
        {
            var sup = Supervisor(ModelKinds.Chat);
            var state = sup is not null && sup.Current?.ModelId == active.ModelId ? sup.State : EngineState.Stopped;
            model = new HealthModelInfo(active.ModelId, active.DisplayName,
                active.Files.Count(f => !File.Exists(f)), src?.DamagedReason?.Invoke(active.ModelId), state);
        }

        IReadOnlyList<EngineFileStatus>? engineFiles = null;
        long? free = null;
        try
        {
            engineFiles = src?.EngineFiles?.Invoke();
            free = src?.StoreFreeBytes?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An unreadable drive or folder is reported as "could not be read", not as a failure of the host.
        }

        return HealthChecker.Evaluate(new HealthInputs
        {
            ApiListening = apiListening,
            EngineFiles = engineFiles,
            GpuAvailable = view.Info.Ranges.GpuAvailable,
            GpuName = view.GpuName,
            Compute = view.Info.Compute,
            ComputeEffective = view.Info.ComputeEffective,
            Model = model,
            StoreFreeBytes = free,
            Fit = view.Info.Fit,
            FitModel = view.FitModel,
            SmallerModel = view.SmallerModel,
            EasedOffNow = view.Info.EasedOffNow,
            MemoryCapActive = view.Info.MemoryCapActive,
            MemoryCapReason = view.Info.MemoryCapReason,
            SettingsLink = view.Info.SettingsLink,
        });
    }

    private async Task HealthEndpointAsync(ApiContext ctx)
    {
        var report = await BuildHealthAsync(apiListening: true, ctx.Aborted).ConfigureAwait(false);
        await ctx.WriteJsonAsync(200, report, AiHostJsonContext.Default.HealthReport).ConfigureAwait(false);
    }
}
