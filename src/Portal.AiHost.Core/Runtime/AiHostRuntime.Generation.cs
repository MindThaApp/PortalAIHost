using System.Collections.Concurrent;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Planning;

namespace Portal.AiHost.Core.Runtime;

/// <summary>
/// Memory planning for the generation engine (docs/media-and-agents-plan.md 6.4). A generation job is planned against
/// what the chat and embedding engines hold, and while it runs its plan is reserved, so a chat model that loads
/// meanwhile is planned against what is really left.
/// </summary>
public sealed partial class AiHostRuntime
{
    private readonly ConcurrentDictionary<string, (GenerationPlan Plan, string? ModelId)> _generationReserved = new(StringComparer.Ordinal);

    /// <summary>Slot name the generation engine plans under (never a supervisor kind).</summary>
    public const string GenerationSlot = "generation";

    /// <summary>True once the host started quitting.</summary>
    public bool IsStopping => _stopping;

    /// <summary>Processor threads the PC owner allows the engines, or null for the engine's own choice.</summary>
    public int? EngineThreads => _limits?.CpuThreads;

    /// <summary>Memory cap for one engine process (Windows job object), from the owner's memory limit, or null.</summary>
    public long? EngineMemoryCap => _limits is { } lim ? PerformanceModel.CapFor(lim.MemoryLimitBytes) : null;

    /// <summary>
    /// Plans a generation run against the memory available now. Without a hardware probe (tests, unknown PC) the run
    /// is placed on the graphics card when a card build exists, else on the processor, and is not checked.
    /// <paramref name="ignoreGenerationJobs"/> plans as if no other image or video job held memory (the check when a job
    /// is created, since it waits for the one running now anyway).
    /// </summary>
    public async Task<GenerationPlan> PlanGenerationAsync(GenerationNeeds needs, bool gpuBuild, long pixels, CancellationToken ct,
        bool ignoreGenerationJobs = false, long minVramBytes = 0, string? ignoreSlot = null)
    {
        ArgumentNullException.ThrowIfNull(needs);
        var settings = CurrentPlanner;
        if (_o.Hardware is null)
        {
            var cpu = !gpuBuild || settings.Backend == "cpu";
            return new GenerationPlan(true, new GenerationPlacement
            {
                Backend = cpu ? "cpu" : "vulkan", VaeTiling = needs.Video, FlashAttention = needs.Video,
            }, "", cpu ? 0 : needs.WeightsBytes + needs.ComputeBytes, cpu ? needs.WeightsBytes + needs.ComputeBytes : GenerationPlanner.ProcessOverheadBytes);
        }

        var hw = await _o.Hardware.ProbeAsync(ct).ConfigureAwait(false);
        var committed = CommittedExcept(GenerationSlot, ignoreSlot);
        if (ignoreGenerationJobs)
        {
            var (genRam, genVram) = GenerationReserved();
            committed = new CommittedMemory(Math.Max(0, committed.RamBytes - genRam),
                [.. committed.VramBytesPerGpu.Select((v, i) => i == 0 ? Math.Max(0, v - genVram) : v)]);
        }

        var budget = MemoryBudget.From(hw, _o.Hardware.GetMemory(), settings, committed);
        return GenerationPlanner.Plan(needs, budget, settings.Backend, gpuBuild, pixels, minVramBytes);
    }

    /// <summary>
    /// True when the second chat slot (the director) runs on the processor only and could yield its RAM to a clip
    /// (<see cref="YieldDirectorAsync"/>).
    /// </summary>
    public bool DirectorCanYield =>
        Supervisor(Contracts.ModelSlots.Second(Contracts.ModelKinds.Chat)) is { } sup &&
        sup.State is not (EngineState.Stopped or EngineState.Failed) &&
        sup.Current?.Placement is { } placement && placement.GpuLayers <= 0;

    /// <summary>
    /// Holds a running job's planned memory until the returned handle is disposed. It is counted for every other
    /// engine's plan and shows in the status memory budget as slot "generation".
    /// </summary>
    public IDisposable ReserveGeneration(string jobId, GenerationPlan plan, string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _generationReserved[jobId] = (plan, modelId);
        Interlocked.Increment(ref _revision);
        return new Release(this, jobId);
    }

    /// <summary>RAM and graphics memory reserved by running generation jobs.</summary>
    public (long RamBytes, long VramBytes) GenerationReserved()
    {
        long ram = 0, vram = 0;
        foreach (var (plan, _) in _generationReserved.Values)
        {
            ram += plan.CpuBytes;
            vram += plan.GpuBytes;
        }

        return (ram, vram);
    }

    /// <summary>The model of a running generation job, for the status memory budget (null when none runs).</summary>
    private string? GenerationModelId() => _generationReserved.Values.Select(r => r.ModelId).FirstOrDefault(m => m is not null);

    private sealed class Release(AiHostRuntime owner, string jobId) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0 && owner._generationReserved.TryRemove(jobId, out _))
            {
                Interlocked.Increment(ref owner._revision);
            }
        }
    }
}
