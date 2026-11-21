using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Catalogue;

namespace Portal.AiHost.Core.Planning;

/// <summary>Tunables for the planner and recommender. Defaults follow architecture section 5.</summary>
public sealed record PlannerSettings
{
    /// <summary>The host's models may use at most this share of total RAM (Settings "maximum memory use").</summary>
    public int MaxMemoryPercent { get; init; } = 80;

    /// <summary>auto, gpu or cpu.</summary>
    public string Backend { get; init; } = "auto";

    /// <summary>RAM left free after loading: max(min(4 GB, 25 % of RAM), 15 % of RAM).</summary>
    public long MinRamHeadroomBytes { get; init; } = 4L << 30;
    public double RamHeadroomFraction { get; init; } = 0.15;

    /// <summary>Share of VRAM kept free.</summary>
    public double VramReserveFraction { get; init; } = 0.10;

    /// <summary>GPUs with less dedicated memory than this are ignored (integrated graphics).</summary>
    public long MinUsableVramBytes { get; init; } = 2L << 30;

    /// <summary>Share of RAM the GPU may use on unified-memory machines (Apple silicon default working set).</summary>
    public double UnifiedGpuFraction { get; init; } = 0.75;

    /// <summary>Host-side buffers when part of the model runs on the CPU.</summary>
    public long CpuBufferBytes { get; init; } = 512L << 20;

    public int MaxContextTokens { get; init; } = 262_144;
    public int MinContextTokens { get; init; } = 2048;

    /// <summary>Above this, the KV cache uses q8_0.</summary>
    public int Q8KvAboveTokens { get; init; } = 32_768;

    public int MaxParallelSlots { get; init; } = 4;

    /// <summary>Effective memory bandwidth of system RAM for generation.</summary>
    public double CpuBandwidthBytesPerSecond { get; init; } = 60e9;

    /// <summary>Share of a GPU's nominal bandwidth reached during generation.</summary>
    public double GpuEfficiency { get; init; } = 0.7;

    /// <summary>
    /// Recommendations (what to download) assume at least this share of RAM is taken by Windows and other apps
    /// (minimum 2 GB), so they do not flip with whatever happens to be open. Load-time planning uses live memory.
    /// </summary>
    public double TypicalOtherUseFraction { get; init; } = 0.25;

    /// <summary>Chat models slower than this are not recommended.</summary>
    public double MinTokensPerSecond { get; init; } = 8;
}

public sealed record PlanResult(
    bool Fits,
    Placement? Placement,
    string Reason,
    long GpuBytes,
    long CpuBytes,
    double TokensPerSecond)
{
    /// <summary>Context each parallel slot gets (advertised to clients).</summary>
    public int PerSlotContext => Placement is null ? 0 : Placement.ContextTokens / Math.Max(1, Placement.ParallelSlots);
}

/// <summary>Memory budgets for one machine at one moment.</summary>
public sealed record MemoryBudget(long RamBudgetBytes, GpuProfile? Gpu, long VramBudgetBytes, bool Unified, double GpuBandwidth)
{
    public static MemoryBudget From(HardwareProfile hw, MemorySnapshot mem, PlannerSettings s)
    {
        var total = hw.RamBytes;
        var headroom = Math.Max(Math.Min(s.MinRamHeadroomBytes, total / 4), (long)(total * s.RamHeadroomFraction));
        var available = mem.AvailableRamBytes > 0 ? mem.AvailableRamBytes : total;
        var ram = Math.Max(0, Math.Min(available - headroom, total * s.MaxMemoryPercent / 100));

        if (s.Backend == "cpu")
        {
            return new MemoryBudget(ram, null, 0, false, 0);
        }

        var unifiedGpu = hw.Gpus.FirstOrDefault(g => g.Vendor.Equals("apple", StringComparison.OrdinalIgnoreCase));
        if (unifiedGpu is not null)
        {
            var gpuShare = (long)(total * s.UnifiedGpuFraction) - (total - available);
            var bw = total >= 64L << 30 ? 400e9 : 250e9;
            return new MemoryBudget(ram, unifiedGpu, Math.Max(0, Math.Min(ram, gpuShare)), true, bw);
        }

        // Phase 1 uses the GPU with the most dedicated memory.
        var best = -1;
        for (var i = 0; i < hw.Gpus.Count; i++)
        {
            if (hw.Gpus[i].VramBytes >= s.MinUsableVramBytes && (best < 0 || hw.Gpus[i].VramBytes > hw.Gpus[best].VramBytes))
            {
                best = i;
            }
        }

        if (best < 0)
        {
            return new MemoryBudget(ram, null, 0, false, 0);
        }

        var gpu = hw.Gpus[best];
        var free = best < mem.FreeVramBytesPerGpu.Length ? mem.FreeVramBytesPerGpu[best]
            : gpu.VramBudgetBytes > 0 ? gpu.VramBudgetBytes : gpu.VramBytes;
        var vram = Math.Max(0, Math.Min(free, gpu.VramBytes) - (long)(gpu.VramBytes * s.VramReserveFraction));
        return new MemoryBudget(ram, gpu, vram, false, GuessGpuBandwidth(gpu.VramBytes));
    }

    /// <summary>Rough nominal bandwidth by VRAM class (DXGI does not report bandwidth).</summary>
    private static double GuessGpuBandwidth(long vram) => (vram >> 30) switch
    {
        < 6 => 150e9,
        < 12 => 250e9,
        < 20 => 450e9,
        < 28 => 900e9,
        _ => 1500e9,
    };
}

/// <summary>
/// Pure placement planner: where a model's layers, experts and KV cache go, and how much context fits.
/// Dense: as many layers on the GPU as fit. MoE: non-expert tensors and KV on the GPU, experts of the first K
/// layers on the CPU (--n-cpu-moe K), smallest K that fits. Context then doubles up to context_max while the
/// GPU share does not shrink; parallel slots grow last.
/// </summary>
public static class PlacementPlanner
{
    public static PlanResult Plan(CatalogueEntry model, HardwareProfile hw, MemorySnapshot mem, PlannerSettings? settings = null)
    {
        settings ??= new PlannerSettings();
        return Plan(model, MemoryBudget.From(hw, mem, settings), settings);
    }

    public static PlanResult Plan(CatalogueEntry model, MemoryBudget budget, PlannerSettings settings)
    {
        var ctxMax = Math.Min(model.ContextMax, settings.MaxContextTokens);
        var ctx = Math.Min(model.ContextDefault, ctxMax);
        var first = Place(model, budget, settings, ctx, 1);
        while (first is null && ctx / 2 >= Math.Min(settings.MinContextTokens, model.ContextDefault))
        {
            ctx /= 2;
            first = Place(model, budget, settings, ctx, 1);
        }

        if (first is null)
        {
            return new PlanResult(false, null, NoFitReason(model, budget), 0, 0, 0);
        }

        var best = first;
        while (ctx * 2 <= ctxMax)
        {
            var next = Place(model, budget, settings, ctx * 2, 1);
            if (next is null || !KeepsGpuShare(next, first))
            {
                break;
            }

            ctx *= 2;
            best = next;
        }

        for (var slots = 2; slots <= settings.MaxParallelSlots; slots *= 2)
        {
            var next = Place(model, budget, settings, ctx * slots, slots);
            if (next is null || !KeepsGpuShare(next, first))
            {
                break;
            }

            best = next;
        }

        return best;
    }

    private static bool KeepsGpuShare(PlanResult next, PlanResult first) =>
        next.Placement!.GpuLayers >= first.Placement!.GpuLayers
        && (next.Placement.CpuMoeLayers ?? 0) <= (first.Placement.CpuMoeLayers ?? 0)
        && next.Placement.Backend == first.Placement.Backend;

    private static PlanResult? Place(CatalogueEntry model, MemoryBudget b, PlannerSettings s, int ctxTotal, int slots)
    {
        var m = model.Memory;
        var n = m.NLayers;
        var q8 = ctxTotal / slots > s.Q8KvAboveTokens;
        var kv = (long)(m.KvBytesPer1kCtx * (ctxTotal / 1024.0) * (q8 ? 8.5 / 16 : 1));
        var kvType = q8 ? "q8_0" : "f16";
        var perToken = ActiveBytesPerToken(model);

        if (b.Gpu is not null && b.Unified)
        {
            var need = m.WeightsBytes + kv + m.ComputeOverheadBytes;
            if (need > b.VramBudgetBytes || need > b.RamBudgetBytes)
            {
                return CpuOnly(model, b, s, ctxTotal, slots, kv, kvType, perToken);
            }

            return Result(model, "metal", b.Gpu, n, n, null, ctxTotal, slots, kvType, need, 0,
                perToken.Total / (b.GpuBandwidth * s.GpuEfficiency));
        }

        if (b.Gpu is not null && b.VramBudgetBytes > 0)
        {
            if (model.IsMoe)
            {
                var gpuBase = m.NonExpertBytes + kv + m.ComputeOverheadBytes;
                if (gpuBase <= b.VramBudgetBytes)
                {
                    var perLayer = (double)m.ExpertBytes / n;
                    var onGpu = (int)Math.Clamp(Math.Floor((b.VramBudgetBytes - gpuBase) / perLayer), 0, n);
                    var k = n - onGpu;
                    var cpu = (long)(k * perLayer) + (k > 0 ? s.CpuBufferBytes : 0);
                    if (cpu > b.RamBudgetBytes)
                    {
                        return null;
                    }

                    var gpuBytes = gpuBase + (long)(onGpu * perLayer);
                    var seconds = (perToken.NonExpert + perToken.Expert * onGpu / n) / (b.GpuBandwidth * s.GpuEfficiency)
                        + perToken.Expert * k / n / s.CpuBandwidthBytesPerSecond;
                    return Result(model, "vulkan", b.Gpu, n, n, k, ctxTotal, slots, kvType, gpuBytes, cpu, seconds);
                }
            }

            var embedOut = m.EmbedOutputBytes ?? m.WeightsBytes / 20;
            var layerBytes = (double)(m.WeightsBytes - embedOut) / n;
            var kvPerLayer = (double)kv / n;
            var room = b.VramBudgetBytes - embedOut - m.ComputeOverheadBytes;
            var g = room <= 0 ? 0 : (int)Math.Clamp(Math.Floor(room / (layerBytes + kvPerLayer)), 0, n);
            if (g > 0)
            {
                var cpu = g == n ? 0 : (long)((n - g) * (layerBytes + kvPerLayer)) + s.CpuBufferBytes;
                if (cpu > b.RamBudgetBytes)
                {
                    return null;
                }

                var gpuBytes = embedOut + m.ComputeOverheadBytes + (long)(g * (layerBytes + kvPerLayer));
                var seconds = perToken.Total * ((double)g / n) / (b.GpuBandwidth * s.GpuEfficiency)
                    + perToken.Total * ((double)(n - g) / n) / s.CpuBandwidthBytesPerSecond;
                return Result(model, "vulkan", b.Gpu, g, n, null, ctxTotal, slots, kvType, gpuBytes, cpu, seconds);
            }
        }

        return CpuOnly(model, b, s, ctxTotal, slots, kv, kvType, perToken);
    }

    private static PlanResult? CpuOnly(
        CatalogueEntry model, MemoryBudget b, PlannerSettings s, int ctxTotal, int slots, long kv, string kvType, PerToken perToken)
    {
        var m = model.Memory;
        var need = m.WeightsBytes + kv + m.ComputeOverheadBytes + s.CpuBufferBytes;
        if (need > b.RamBudgetBytes)
        {
            return null;
        }

        return Result(model, "cpu", null, 0, m.NLayers, null, ctxTotal, slots, kvType, 0, need,
            perToken.Total / s.CpuBandwidthBytesPerSecond);
    }

    private static PlanResult Result(
        CatalogueEntry model, string backend, GpuProfile? gpu, int gpuLayers, int n, int? cpuMoe, int ctx, int slots,
        string kvType, long gpuBytes, long cpuBytes, double secondsPerToken)
    {
        var placement = new Placement
        {
            Backend = backend,
            Devices = gpu is null ? [] : [new PlacementDevice(gpu.Name, gpu.Vendor, gpuLayers)],
            GpuLayers = gpuLayers,
            TotalLayers = n,
            CpuMoeLayers = cpuMoe,
            ContextTokens = ctx,
            ParallelSlots = slots,
            KvType = kvType,
        };
        var tps = secondsPerToken > 0 ? 1 / secondsPerToken : 0;
        var where = backend switch
        {
            "cpu" => "runs on the processor",
            "metal" => "runs fully on the GPU",
            _ when cpuMoe is > 0 => $"attention on the {gpu!.Name}, {cpuMoe} of {n} expert layers in RAM",
            _ when gpuLayers == n => $"runs fully on the {gpu!.Name}",
            _ => $"{gpuLayers} of {n} layers on the {gpu!.Name}, the rest in RAM",
        };
        var reason = $"{model.DisplayName} {where}, {ctx / slots / 1024}K context, about {tps:0} words/s";
        return new PlanResult(true, placement, reason, gpuBytes, cpuBytes, tps);
    }

    private static string NoFitReason(CatalogueEntry model, MemoryBudget b) =>
        $"{model.DisplayName} needs about {model.Memory.WeightsBytes / 1e9:0.#} GB; this PC can give it about "
        + $"{(b.RamBudgetBytes + (b.Unified ? 0 : b.VramBudgetBytes)) / 1e9:0.#} GB";

    private readonly record struct PerToken(double NonExpert, double Expert)
    {
        public double Total => NonExpert + Expert;
    }

    /// <summary>Bytes read per generated token: all non-expert weights plus the active share of the experts.</summary>
    private static PerToken ActiveBytesPerToken(CatalogueEntry model)
    {
        var m = model.Memory;
        if (!model.IsMoe)
        {
            return new PerToken(m.WeightsBytes, 0);
        }

        var nonExpertParams = model.ParamsTotalB * m.NonExpertBytes / m.WeightsBytes;
        var expertShare = Math.Clamp((model.ParamsActiveB - nonExpertParams) / (model.ParamsTotalB - nonExpertParams), 0.01, 1);
        return new PerToken(m.NonExpertBytes, m.ExpertBytes * expertShare);
    }
}
