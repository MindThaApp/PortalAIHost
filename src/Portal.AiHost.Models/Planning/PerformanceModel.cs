using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Catalogue;

namespace Portal.AiHost.Core.Planning;

/// <summary>The performance limits in force: the saved settings resolved against this PC's hardware.</summary>
public sealed record PerformanceLimits(
    string Preset,
    long MemoryLimitBytes,
    long KeepFreeBytes,
    string Compute,
    long? GpuMemoryLimitBytes,
    int CpuThreads,
    string Priority,
    bool GpuAvailable,
    long RamBytes,
    int LogicalCores,
    long GpuVramBytes)
{
    /// <summary>True when the engine should run at BELOW_NORMAL priority.</summary>
    public bool LowPriority => Priority == PerformancePriorities.Low;
}

/// <summary>
/// The shared performance settings model (performance-and-tuning sections 2 and 3): ranges, defaults, presets,
/// and how saved settings turn into planner inputs. Pure functions, no I/O.
/// </summary>
public static class PerformanceModel
{
    public const long Gb = 1L << 30;
    public const long MinMemoryLimitBytes = 2 * Gb;
    public const long MinKeepFreeBytes = 2 * Gb;
    public const long MinGpuLimitBytes = 1 * Gb;

    /// <summary>Smallest card the "Graphics card" choice is offered for.</summary>
    public const long MinGpuForChoiceBytes = 4 * Gb;

    public const long GpuReserveBytes = Gb / 2;

    /// <summary>
    /// The job object cap for a memory limit: the limit plus a margin of 1 GB or 10 %, whichever is larger, because
    /// the engine needs some memory besides what the planner counts.
    /// </summary>
    public static long CapFor(long memoryLimitBytes) => memoryLimitBytes + Math.Max(Gb, memoryLimitBytes / 10);

    public static long DefaultKeepFree(long ram) => ram >= 32 * Gb ? 8 * Gb : 4 * Gb;

    public static NumberRange KeepFreeRange(long ram) =>
        new() { Min = MinKeepFreeBytes, Max = Math.Max(MinKeepFreeBytes, ram / 2), Step = Gb };

    public static NumberRange MemoryRange(long ram, long keepFree)
    {
        var max = Math.Max(MinMemoryLimitBytes, ram - keepFree);
        return new NumberRange { Min = MinMemoryLimitBytes, Max = max, Step = Gb };
    }

    public static NumberRange ThreadRange(int logical) => new() { Min = 1, Max = Math.Max(1, logical - 1) };

    public static NumberRange? GpuRange(long vram) => vram >= MinGpuForChoiceBytes
        ? new NumberRange { Min = MinGpuLimitBytes, Max = Math.Max(MinGpuLimitBytes, vram - GpuReserveBytes), Step = Gb / 2 }
        : null;

    /// <summary>The largest card and its memory.</summary>
    public static (string? Name, long Vram) BestGpu(HardwareProfile hw)
    {
        var best = hw.Gpus.OrderByDescending(g => g.VramBytes).FirstOrDefault();
        return best is null ? (null, 0) : (best.Name, best.VramBytes);
    }

    /// <summary>Values of a preset on this PC.</summary>
    public static PerformancePresetValues PresetValues(string preset, long ram, int logical)
    {
        var keepFree = DefaultKeepFree(ram);
        var memRange = MemoryRange(ram, keepFree);
        var threads = ThreadRange(logical);
        (long mem, int cores, string priority) = preset switch
        {
            PerformancePresets.Quiet => (ram / 2, logical / 2, PerformancePriorities.Low),
            PerformancePresets.Fast => (ram - keepFree, logical - 1, PerformancePriorities.Normal),
            _ => (ram * 80 / 100, logical - 2, PerformancePriorities.Low),
        };
        return new PerformancePresetValues
        {
            MemoryLimitBytes = Math.Clamp(mem, memRange.Min, memRange.Max),
            CpuThreads = (int)Math.Clamp(cores, threads.Min, threads.Max),
            Priority = priority,
        };
    }

    public static string NormalizePreset(string? preset) => preset switch
    {
        PerformancePresets.Quiet => PerformancePresets.Quiet,
        PerformancePresets.Fast => PerformancePresets.Fast,
        PerformancePresets.Custom => PerformancePresets.Custom,
        _ => PerformancePresets.Balanced,
    };

    /// <summary>
    /// Resolves saved settings against the hardware, clamping every value to its range. Order of precedence for
    /// memory: the saved byte limit, then an older percentage that is not the default, then the preset.
    /// </summary>
    public static PerformanceLimits Resolve(HostSettings s, HardwareProfile hw)
    {
        var ram = Math.Max(hw.RamBytes, MinMemoryLimitBytes);
        var logical = Math.Max(1, hw.CpuCores);
        var preset = NormalizePreset(s.Preset);
        var presetValues = PresetValues(preset == PerformancePresets.Custom ? PerformancePresets.Balanced : preset, ram, logical);

        var keepRange = KeepFreeRange(ram);
        var keepFree = Math.Clamp(s.KeepFreeBytes ?? DefaultKeepFree(ram), keepRange.Min, keepRange.Max);

        var memRange = MemoryRange(ram, keepFree);
        long memory;
        if (s.MemoryLimitBytes is { } explicitLimit)
        {
            memory = explicitLimit;
        }
        else if (s.MaxMemoryPercent is > 0 and < 100 && s.MaxMemoryPercent != 80)
        {
            memory = ram * s.MaxMemoryPercent / 100;
        }
        else
        {
            memory = presetValues.MemoryLimitBytes;
        }

        memory = Math.Clamp(memory, memRange.Min, memRange.Max);

        var (_, vram) = BestGpu(hw);
        var gpuAvailable = vram >= MinGpuForChoiceBytes
            || hw.Gpus.Any(g => g.Vendor.Equals("apple", StringComparison.OrdinalIgnoreCase));
        var compute = s.Compute is "gpu" or "cpu" ? s.Compute : s.Backend is "gpu" or "cpu" ? s.Backend : "auto";
        if (compute == "gpu" && !gpuAvailable)
        {
            compute = "cpu"; // asked for a graphics card this PC does not have
        }

        long? gpuLimit = null;
        if (GpuRange(vram) is { } gr)
        {
            gpuLimit = Math.Clamp(s.GpuMemoryLimitBytes ?? gr.Max, gr.Min, gr.Max);
        }

        var tr = ThreadRange(logical);
        var threads = (int)Math.Clamp(s.CpuThreads ?? presetValues.CpuThreads, tr.Min, tr.Max);
        var priority = s.Priority == PerformancePriorities.Normal ? PerformancePriorities.Normal
            : s.Priority == PerformancePriorities.Low ? PerformancePriorities.Low
            : presetValues.Priority;
        return new PerformanceLimits(preset, memory, keepFree, compute, gpuLimit, threads, priority, gpuAvailable, ram,
            logical, vram);
    }

    /// <summary>Planner inputs for these limits. The planner stays the one place that decides placement.</summary>
    public static PlannerSettings ToPlanner(PerformanceLimits l, PlannerSettings? basis = null) =>
        (basis ?? new PlannerSettings()) with
        {
            MemoryLimitBytes = l.MemoryLimitBytes,
            KeepFreeBytes = l.KeepFreeBytes,
            GpuMemoryLimitBytes = l.GpuMemoryLimitBytes,
            Backend = l.Compute,
        };

    /// <summary>Convenience for callers that only have settings: planner inputs for this hardware.</summary>
    public static PlannerSettings PlannerFor(HostSettings s, HardwareProfile hw) => ToPlanner(Resolve(s, hw));

    /// <summary>gpu when the model would use the graphics card under these limits, otherwise cpu.</summary>
    public static string EffectiveCompute(PerformanceLimits l, PlanResult? plan)
    {
        if (l.Compute == "cpu")
        {
            return "cpu";
        }

        if (plan is { Fits: true, Placement: { } p })
        {
            return p.Backend == "cpu" ? "cpu" : "gpu";
        }

        return l.GpuAvailable ? "gpu" : "cpu";
    }

    /// <summary>The fit word for a plan, or needs_smaller_model / too_big when it does not fit.</summary>
    public static string Classify(PlanResult plan, bool smallerModelFits)
    {
        if (!plan.Fits || plan.Placement is null)
        {
            return smallerModelFits ? PerformanceFits.NeedsSmallerModel : PerformanceFits.TooBig;
        }

        var p = plan.Placement;
        if (p.Backend == "cpu")
        {
            return PerformanceFits.CpuOnly;
        }

        return p.GpuLayers >= p.TotalLayers && (p.CpuMoeLayers ?? 0) == 0
            ? PerformanceFits.FitsFully
            : PerformanceFits.FitsPartial;
    }

    /// <summary>
    /// The best chat model (highest quality, then faster) that plans on this PC under the given planner settings,
    /// or null. Uses typical free memory, like recommendations, so the answer does not flip with open programs.
    /// </summary>
    public static (CatalogueEntry Entry, PlanResult Plan)? BestFit(
        IEnumerable<CatalogueEntry> catalogue, HardwareProfile hw, MemorySnapshot mem, PlannerSettings settings)
    {
        var rec = Recommender.Recommend(catalogue, ModelKinds.Chat, hw, mem,
            new RecommendContext(new HashSet<string>(), long.MaxValue, Settings: settings));
        return rec.Best is null ? null : (rec.Best.Entry, rec.Best.Plan);
    }

    /// <summary>Slider stops from 2 GB up to the range maximum, in 1 GB steps, each with what fits there.</summary>
    public static List<MemoryStop> MemoryStops(
        IReadOnlyList<CatalogueEntry> catalogue, HardwareProfile hw, MemorySnapshot mem, PerformanceLimits l)
    {
        var range = MemoryRange(l.RamBytes, l.KeepFreeBytes);
        var stops = new List<MemoryStop>();
        for (var bytes = range.Min; bytes <= range.Max; bytes += Gb)
        {
            var settings = ToPlanner(l with { MemoryLimitBytes = bytes });
            var best = BestFit(catalogue, hw, mem, settings);
            stops.Add(new MemoryStop
            {
                Bytes = bytes,
                FitsModel = best?.Entry.DisplayName,
                Fit = best is null ? null : Classify(best.Value.Plan, true),
            });
        }

        return stops;
    }
}
