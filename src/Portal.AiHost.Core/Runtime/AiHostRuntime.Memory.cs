using System.Globalization;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Api;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Catalogue;
using Portal.AiHost.Core.Planning;

namespace Portal.AiHost.Core.Runtime;

/// <summary>Answer of <see cref="AiHostRuntime.CanReserve"/>.</summary>
/// <param name="Fits">True when the asked memory fits next to everything already planned.</param>
/// <param name="Reason">Plain sentence for the user; empty when it fits.</param>
/// <param name="RamFreeBytes">RAM left under the user's limit before the ask.</param>
/// <param name="VramFreeBytes">Video memory left before the ask.</param>
public sealed record ReservationCheck(bool Fits, string Reason, long RamFreeBytes, long VramFreeBytes);

public sealed partial class AiHostRuntime
{
    private volatile bool _directorYielded;

    /// <summary>
    /// True between <see cref="YieldDirectorAsync"/> and <see cref="ResumeDirectorAsync"/>. Requests for the second chat slot
    /// answer 503 engine_restarting meanwhile instead of starting it again.
    /// </summary>
    public bool DirectorYielded => _directorYielded;

    /// <summary>Default thread cap of the CPU-only preset: half the logical processors (the physical cores on a hyper-threaded PC).</summary>
    public static int CpuOnlyThreads(int logicalProcessors) => Math.Max(1, logicalProcessors / 2);

    /// <summary>The user's RAM limit for engines: the saved limit, or the share of RAM the planner may use.</summary>
    private long RamLimitBytes(long totalRam)
    {
        var planner = CurrentPlanner;
        return planner.MemoryLimitBytes ?? totalRam * planner.MaxMemoryPercent / 100;
    }

    /// <summary>
    /// Planned memory of every engine slot, including a load in progress (its reservation counts from the moment the
    /// placement was decided) and memory apps reserved for their own models. Null until the hardware is known.
    /// </summary>
    public MemoryBudgetStatus? BuildMemoryBudget(HardwareProfile? hw)
    {
        if (hw is null)
        {
            return null;
        }

        var engines = new List<EngineMemoryStatus>();
        long ram = 0;
        long vram = 0;
        foreach (var (kind, ks) in _kinds)
        {
            var sup = Supervisor(kind);
            var loading = ks.Reserved is not null;
            var plan = ks.Reserved ?? (sup?.State is EngineState.Stopped or null ? null : ks.LastPlan);
            if (plan is null)
            {
                continue;
            }

            ram += plan.CpuBytes;
            vram += plan.GpuBytes;
            engines.Add(new EngineMemoryStatus
            {
                Slot = kind,
                ModelId = GetActive(kind)?.ModelId,
                RamBytes = plan.CpuBytes,
                VramBytes = plan.GpuBytes,
                Loading = loading,
                CpuOnly = plan.Placement is { } p && p.GpuLayers <= 0,
            });
        }

        var apps = _leases.ReservedBytes;
        if (apps > 0)
        {
            ram += apps;
            engines.Add(new EngineMemoryStatus { Slot = "apps", RamBytes = apps });
        }

        var limit = RamLimitBytes(hw.RamBytes);
        var (_, vramTotal) = PerformanceModel.BestGpu(hw);
        return new MemoryBudgetStatus
        {
            RamLimitBytes = limit,
            RamPlannedBytes = ram,
            RamFreeBytes = Math.Max(0, limit - ram),
            VramTotalBytes = vramTotal,
            VramPlannedBytes = vram,
            VramFreeBytes = Math.Max(0, vramTotal - vram),
            Engines = engines,
        };
    }

    /// <summary>
    /// Hook for a generation engine (image or video): can this much RAM and video memory be reserved next to the engines
    /// that are planned now? Changes nothing; the caller starts its own load when the answer is yes. With
    /// <paramref name="ignoreSecondSlot"/> the second chat slot's memory counts as free (the director yields, see
    /// <see cref="YieldDirectorAsync"/>).
    /// </summary>
    public ReservationCheck CanReserve(long ramBytes, long vramBytes, bool ignoreSecondSlot = false)
    {
        var budget = BuildMemoryBudget(_hardwareProfile);
        if (budget is null)
        {
            return new ReservationCheck(false, "The memory of this PC is not known yet.", 0, 0);
        }

        var ram = budget.RamFreeBytes;
        var vram = budget.VramFreeBytes;
        if (ignoreSecondSlot && budget.Engines.FirstOrDefault(e => ModelSlots.IsSecond(e.Slot)) is { } second)
        {
            ram += second.RamBytes;
            vram += second.VramBytes;
        }

        if (ramBytes > ram)
        {
            return new ReservationCheck(false,
                $"This needs about {Gb(ramBytes)} GB of RAM, but only {Gb(ram)} GB is free under the limit of {Gb(budget.RamLimitBytes)} GB.",
                ram, vram);
        }

        if (vramBytes > vram)
        {
            return new ReservationCheck(false,
                $"This needs about {Gb(vramBytes)} GB of video memory, but only {Gb(vram)} GB is free.", ram, vram);
        }

        return new ReservationCheck(true, "", ram, vram);
    }

    /// <summary>
    /// The director yields while a clip renders: stops the second chat slot when it runs on the processor only, so a
    /// generation engine can use its RAM. Engine stop only; the model files and the device's claim stay, and
    /// <see cref="ResumeDirectorAsync"/> loads it again. Returns false when there is nothing to yield.
    /// </summary>
    public async Task<bool> YieldDirectorAsync(CancellationToken ct)
    {
        var slot = ModelSlots.Second(ModelKinds.Chat);
        var sup = Supervisor(slot);
        if (sup is null || sup.State is EngineState.Stopped or EngineState.Failed ||
            sup.Current?.Placement is not { } placement || placement.GpuLayers > 0)
        {
            return false;
        }

        _directorYielded = true;
        await sup.StopAsync(ct).ConfigureAwait(false); // engine stop: slot chat.2 only (memory; files untouched)
        Interlocked.Increment(ref _revision);
        return true;
    }

    /// <summary>Ends a yield and loads the second slot's model again (a few seconds from disk).</summary>
    public async Task ResumeDirectorAsync(CancellationToken ct)
    {
        if (!_directorYielded)
        {
            return;
        }

        _directorYielded = false;
        var slot = ModelSlots.Second(ModelKinds.Chat);
        if (GetActive(slot) is not null)
        {
            await EnsureLoadedAsync(slot, ct).ConfigureAwait(false);
        }
    }

    private ApiException InsufficientMemory(ActiveModel model, PlanResult plan)
    {
        var budget = BuildMemoryBudget(_hardwareProfile);
        if (budget is null)
        {
            return new ApiException(ErrorCodes.InsufficientMemory, "The model cannot load in the memory available now.");
        }

        var need = plan.CpuBytes > 0 ? plan.CpuBytes : model.Entry?.TotalBytes ?? 0;
        var reason = $"{model.DisplayName} does not fit: other models already use about {Gb(budget.RamPlannedBytes)} GB " +
            $"of the {Gb(budget.RamLimitBytes)} GB RAM limit" + (need > 0 ? $" and this one needs about {Gb(need)} GB more." : ".") +
            " Unload a model or raise the memory limit.";
        return new ApiException(ErrorCodes.InsufficientMemory, reason)
        {
            Details = new()
            {
                ["ram_limit_bytes"] = Num(budget.RamLimitBytes),
                ["ram_planned_bytes"] = Num(budget.RamPlannedBytes),
            },
        };
    }

    private static JsonElement Num(long v) => JsonDocument.Parse(v.ToString(CultureInfo.InvariantCulture)).RootElement.Clone();

    private static string Gb(long bytes) => (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture);
}
