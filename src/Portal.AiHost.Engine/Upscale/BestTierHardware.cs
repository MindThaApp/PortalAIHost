namespace Portal.AiHost.Engine.Upscale;

public sealed record GpuAdapterInfo(string Name, string Vendor, long VramBytes, bool CudaAvailable);

/// <summary>Where the gate reads GPUs and RAM from; the NVML probe plugs in here later, tests stub it.</summary>
public interface IGpuInfoSource
{
    IReadOnlyList<GpuAdapterInfo> GetGpus();

    long TotalRamBytes { get; }
}

public sealed record BestTierAvailability(
    string Status,
    string? Reason,
    string? RecommendedModel,
    IReadOnlyList<string> Models)
{
    public const string Ready = "ready";
    public const string NeedsHardware = "needs_hardware";

    public bool IsReady => Status == Ready;
}

/// <summary>Hardware gate for the Best tier: NVIDIA with CUDA, 12 GB VRAM or more, 32 GB RAM or more.</summary>
public static class BestTierGate
{
    // Cards sold as 12/24 GB report slightly different byte counts, so the limits sit just under the nominal size.
    public const long MinVramBytes = 12_000_000_000;
    public const long LargeModelVramBytes = 23_000_000_000;
    public const long MinRamBytes = 30L * 1024 * 1024 * 1024;

    public const string Seedvr2Small = "seedvr2-3b";
    public const string Seedvr2Large = "seedvr2-7b";
    public const string FlashVsr = "flashvsr";

    public static BestTierAvailability Evaluate(IGpuInfoSource hardware, bool flashVsrLicenceVerified = false)
    {
        var nvidia = hardware.GetGpus()
            .Where(g => g.Vendor.Contains("nvidia", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(g => g.VramBytes)
            .ToList();
        if (nvidia.Count == 0)
        {
            return No("Best quality needs an NVIDIA graphics card.");
        }

        var gpu = nvidia[0];
        if (!gpu.CudaAvailable)
        {
            return No("The NVIDIA driver does not offer CUDA. Update the graphics driver.");
        }

        if (gpu.VramBytes < MinVramBytes)
        {
            return No("Best quality needs a graphics card with 12 GB of memory or more.");
        }

        if (hardware.TotalRamBytes < MinRamBytes)
        {
            return No("Best quality needs 32 GB of system memory or more.");
        }

        var models = new List<string> { Seedvr2Small };
        if (gpu.VramBytes >= LargeModelVramBytes)
        {
            models.Add(Seedvr2Large);
        }

        if (flashVsrLicenceVerified)
        {
            models.Add(FlashVsr);
        }

        return new BestTierAvailability(BestTierAvailability.Ready, null,
            gpu.VramBytes >= LargeModelVramBytes ? Seedvr2Large : Seedvr2Small, models);
    }

    private static BestTierAvailability No(string reason) =>
        new(BestTierAvailability.NeedsHardware, reason, null, []);
}
