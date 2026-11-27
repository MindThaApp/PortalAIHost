using System.Runtime.InteropServices;

namespace Portal.AiHost.Engine.Hardware;

/// <summary>The per-engine Settings choice. Auto lets the Host pick; the others put that flavour first.</summary>
public enum BackendOverride
{
    Auto,
    Cuda,
    Vulkan,
    Cpu,
}

/// <summary>One flavour the engine may try, with the device to pass to <c>--device</c> when known.</summary>
public sealed record BackendCandidate(string Flavour, string? DeviceId = null);

/// <summary>Everything the selector looks at. A null probe means "no probe ran".</summary>
public sealed class BackendRequest
{
    public BackendOverride Preference { get; init; } = BackendOverride.Auto;

    public Architecture Architecture { get; init; } = RuntimeInformation.ProcessArchitecture;

    /// <summary>True when the flavour folder is installed. CUDA flavours that are not installed are skipped.</summary>
    public required Func<string, bool> IsInstalled { get; init; }

    public NvmlResult? Nvml { get; init; }

    /// <summary>Minimum NVIDIA driver (major part) the CUDA flavour needs, from the flavour manifest. 0 means no limit.</summary>
    public int MinCudaDriverMajor { get; init; }

    public VulkanProbeResult? VulkanProbe { get; init; }

    /// <summary>CUDA devices from <c>--list-devices</c> on the CUDA build; null when that probe has not run.</summary>
    public IReadOnlyList<ListedDevice>? CudaDevices { get; init; }

    /// <summary>Graphics driver string of the Vulkan adapter (DXGI), the key for Vulkan bad marks.</summary>
    public string? VulkanDriver { get; init; }

    public FlavourMarks? Marks { get; init; }

    public string? EngineTag { get; init; }
}

/// <summary>The ordered candidates (first one that loads wins) and why the others were skipped.</summary>
public sealed record BackendChoice(IReadOnlyList<BackendCandidate> Candidates, IReadOnlyDictionary<string, string> Skipped)
{
    public BackendCandidate First => Candidates[0];
}

/// <summary>Candidate-list selector: CUDA, then Vulkan, then CPU. The CPU build is always the last candidate.</summary>
public static class BackendSelector
{
    /// <summary>CUDA needs compute capability 6.1 or newer (Pascal and later).</summary>
    public const int MinComputeMajor = 6;
    public const int MinComputeMinor = 1;

    public static BackendChoice Choose(BackendRequest r)
    {
        var skipped = new Dictionary<string, string>();
        var cpu = new BackendCandidate(RuntimeProbe.Cpu);
        if (r.Preference == BackendOverride.Cpu || r.Architecture != Architecture.X64)
        {
            if (r.Preference != BackendOverride.Cpu)
            {
                skipped[RuntimeProbe.Vulkan] = "Needs a 64-bit Intel or AMD PC.";
                skipped[RuntimeProbe.Cuda] = "Needs a 64-bit Intel or AMD PC.";
            }

            return new BackendChoice([cpu], skipped);
        }

        var cuda = TryCuda(r, out var cudaWhy);
        if (cuda == null)
        {
            skipped[RuntimeProbe.Cuda] = cudaWhy!;
        }

        var vulkan = TryVulkan(r, out var vulkanWhy);
        if (vulkan == null)
        {
            skipped[RuntimeProbe.Vulkan] = vulkanWhy!;
        }

        var list = new List<BackendCandidate>();
        var order = r.Preference == BackendOverride.Vulkan
            ? new[] { vulkan, cuda }
            : new[] { cuda, vulkan };
        foreach (var c in order)
        {
            if (c != null)
            {
                list.Add(c);
            }
        }

        list.Add(cpu);
        return new BackendChoice(list, skipped);
    }

    private static BackendCandidate? TryCuda(BackendRequest r, out string? why)
    {
        why = null;
        if (r.Nvml is not { Present: true } nvml || nvml.Best is not { } card)
        {
            why = "No NVIDIA card found.";
            return null;
        }

        if (!card.AtLeast(MinComputeMajor, MinComputeMinor))
        {
            why = "This NVIDIA card is too old for CUDA (needs compute capability 6.1).";
            return null;
        }

        if (r.MinCudaDriverMajor > 0 && nvml.DriverMajor < r.MinCudaDriverMajor)
        {
            why = $"Update the NVIDIA driver to {r.MinCudaDriverMajor} or later.";
            return null;
        }

        if (!r.IsInstalled(RuntimeProbe.Cuda))
        {
            why = "The CUDA engine is not installed.";
            return null;
        }

        if (r.Marks?.IsBad(RuntimeProbe.Cuda, r.EngineTag, nvml.DriverVersion) == true)
        {
            why = "The CUDA engine failed to load on this driver.";
            return null;
        }

        string? deviceId = null;
        if (r.CudaDevices != null)
        {
            if (r.CudaDevices.Count == 0)
            {
                why = "The CUDA engine reports no device.";
                return null;
            }

            deviceId = ListDevicesParser.Match(r.CudaDevices, card)?.DeviceId ?? r.CudaDevices[0].DeviceId;
        }

        return new BackendCandidate(RuntimeProbe.Cuda, deviceId);
    }

    private static BackendCandidate? TryVulkan(BackendRequest r, out string? why)
    {
        why = null;
        if (!r.IsInstalled(RuntimeProbe.Vulkan))
        {
            why = "The Vulkan engine is not installed.";
            return null;
        }

        if (r.Marks?.IsBad(RuntimeProbe.Vulkan, r.EngineTag, r.VulkanDriver) == true)
        {
            why = "The Vulkan engine failed to load earlier.";
            return null;
        }

        if (r.VulkanProbe is not { Ok: true, TotalMiB: > 0 })
        {
            why = r.VulkanProbe?.Error ?? "No Vulkan device found.";
            return null;
        }

        return new BackendCandidate(RuntimeProbe.Vulkan);
    }
}
