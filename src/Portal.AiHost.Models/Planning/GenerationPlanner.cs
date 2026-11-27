using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Core.Planning;

/// <summary>Memory one generation run needs, by part. Bytes. Estimates (docs/media-and-agents-plan.md section 6).</summary>
public sealed record GenerationNeeds(long DiffusionBytes, long EncoderBytes, long VaeBytes, long ComputeBytes, bool Video)
{
    public long WeightsBytes => DiffusionBytes + EncoderBytes + VaeBytes;
}

/// <summary>The planner's answer: where the parts go, or why the run cannot start now.</summary>
public sealed record GenerationPlan(bool Fits, GenerationPlacement? Placement, string Reason, long GpuBytes, long CpuBytes)
{
    /// <summary>The parts moved off the graphics card, as wire words (text_encoder, vae, weights).</summary>
    public IReadOnlyList<string> OnCpu => Placement is not { } p
        ? []
        : p.Backend == "cpu"
            ? ["text_encoder", "vae", "weights"]
            : [.. new[] { p.ClipOnCpu ? "text_encoder" : null, p.VaeOnCpu ? "vae" : null, p.OffloadToCpu ? "weights" : null }
                .Where(x => x is not null).Select(x => x!)];
}

/// <summary>
/// Pure placement planner for the generation engine. Tries, in order: everything on the graphics card; text encoders
/// on the processor; encoders and VAE on the processor; weights kept in RAM and streamed to the card (offload);
/// processor only. The first that fits both the graphics memory and the RAM budget wins. Video never falls back to
/// processor only unless the user chose the processor, because a clip would take hours (plan section 6.3).
/// </summary>
public static class GenerationPlanner
{
    /// <summary>Host-side buffers of the engine process, always in RAM.</summary>
    public const long ProcessOverheadBytes = 512L << 20;

    /// <summary>
    /// With offload, the share of the diffusion weights assumed on the card at once. An estimate: the real figure
    /// depends on the runtime's offload granularity and is to be calibrated with scripts/measure-gen.ps1.
    /// </summary>
    public const double OffloadResidentFraction = 0.25;

    /// <summary>Pixels above which decoding uses tiles.</summary>
    public const long TilingAbovePixels = 1024L * 1024;

    /// <summary>
    /// Estimates the needs of one run from the model's files and the requested size. A complete checkpoint (role
    /// <c>model</c>) is split into diffusion, encoder and VAE shares by family. <paramref name="catalogueCompute"/>, when
    /// set, is the compute figure at <paramref name="defaultPixels"/> and is scaled by the requested size.
    /// </summary>
    public static GenerationNeeds Estimate(IReadOnlyList<GenerationComponent> components, string family, bool video,
        int width, int height, int frames, long? catalogueCompute = null, long defaultPixels = 0)
    {
        long diffusion = 0, encoders = 0, vae = 0;
        foreach (var c in components)
        {
            var bytes = Math.Max(0, c.Bytes);
            switch (c.Role)
            {
                case GenerationFileRoles.Model:
                    var (encShare, vaeShare) = family switch
                    {
                        GenerationFamilies.Sdxl => (0.23, 0.03),
                        GenerationFamilies.Sd3 => (0.35, 0.03),
                        _ => (0.12, 0.08),
                    };
                    encoders += (long)(bytes * encShare);
                    vae += (long)(bytes * vaeShare);
                    diffusion += bytes - (long)(bytes * encShare) - (long)(bytes * vaeShare);
                    break;
                case GenerationFileRoles.Vae:
                    vae += bytes;
                    break;
                default:
                    if (GenerationFileRoles.IsEncoder(c.Role))
                    {
                        encoders += bytes;
                    }
                    else
                    {
                        diffusion += bytes;
                    }

                    break;
            }
        }

        var pixels = (long)Math.Max(1, width) * Math.Max(1, height);
        long compute;
        if (video)
        {
            // Wan-style VAEs compress time by 4: 33 frames are 9 latent frames.
            var latentFrames = (Math.Max(1, frames) - 1) / 4 + 1;
            compute = ProcessOverheadBytes + pixels * latentFrames * 450;
        }
        else
        {
            compute = ProcessOverheadBytes + pixels * 1600;
        }

        if (catalogueCompute is > 0)
        {
            var scaled = defaultPixels > 0 ? (long)(catalogueCompute.Value * ((double)pixels / defaultPixels)) : catalogueCompute.Value;
            compute = Math.Max(compute, scaled);
        }

        return new GenerationNeeds(diffusion, encoders, vae, compute, video);
    }

    /// <summary>
    /// Plans one run. <paramref name="backend"/> is the user's choice (auto, gpu or cpu); <paramref name="gpuBuild"/> says
    /// whether a graphics-card build of the engine is installed. <paramref name="minVramBytes"/> is the catalogue's
    /// min_vram_gb: a card with less dedicated memory is not used for this model.
    /// </summary>
    public static GenerationPlan Plan(GenerationNeeds needs, MemoryBudget budget, string backend, bool gpuBuild, long pixels = 0,
        long minVramBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(needs);
        ArgumentNullException.ThrowIfNull(budget);
        var ram = budget.RamBudgetBytes;
        var vram = budget.VramBudgetBytes;
        var d = needs.DiffusionBytes;
        var t = needs.EncoderBytes;
        var v = needs.VaeBytes;
        var c = needs.ComputeBytes;
        var tiling = needs.Video || pixels > TilingAbovePixels;
        var wantCpu = string.Equals(backend, "cpu", StringComparison.OrdinalIgnoreCase);
        var cardTooSmall = budget.Gpu is { } card && minVramBytes > 0 && card.VramBytes < minVramBytes;
        var canGpu = !wantCpu && gpuBuild && budget.Gpu is not null && vram > 0 && !cardTooSmall;

        if (canGpu)
        {
            // (GPU bytes, RAM bytes, placement) in order of preference.
            var options = new (long Gpu, long Cpu, GenerationPlacement Placement)[]
            {
                (d + t + v + c, ProcessOverheadBytes, new GenerationPlacement { Backend = "vulkan", VaeTiling = tiling }),
                (d + v + c, t + ProcessOverheadBytes, new GenerationPlacement { Backend = "vulkan", ClipOnCpu = true, VaeTiling = tiling }),
                (d + c, t + v + ProcessOverheadBytes,
                    new GenerationPlacement { Backend = "vulkan", ClipOnCpu = true, VaeOnCpu = true, VaeTiling = true }),
                ((long)(d * OffloadResidentFraction) + c, d + t + v + ProcessOverheadBytes,
                    new GenerationPlacement { Backend = "vulkan", ClipOnCpu = true, VaeOnCpu = true, OffloadToCpu = true, VaeTiling = true }),
            };
            foreach (var (gpu, cpu, placement) in options)
            {
                if (gpu <= vram && cpu <= ram)
                {
                    // Flash attention always on the card: the runtime's "auto" probe is noisy (it picked "off" on a cold first run, 2x slower).
                    var p = placement with { FlashAttention = true, MaxVramBytes = vram };
                    return new GenerationPlan(true, p, "", gpu, cpu);
                }
            }
        }

        // Processor only: always for "cpu"; for pictures also when the card cannot hold the run.
        var cpuOnly = d + t + v + c + ProcessOverheadBytes;
        if ((wantCpu || !needs.Video) && cpuOnly <= ram)
        {
            return new GenerationPlan(true, new GenerationPlacement
            {
                Backend = "cpu", VaeTiling = tiling, FlashAttention = needs.Video,
            }, "", 0, cpuOnly);
        }

        var reason = cardTooSmall && !wantCpu
            ? $"This model needs a graphics card with at least {minVramBytes / (double)(1L << 30):0.#} GB."
            : needs.Video && !wantCpu && !canGpu
                ? "Video needs a graphics card with enough free memory."
                : "The model cannot run in the memory available now.";
        return new GenerationPlan(false, null, reason, 0, 0);
    }
}
