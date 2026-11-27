using System.Runtime.InteropServices;
using Portal.AiHost.Engine.Hardware;

namespace Portal.AiHost.Engine.Tests;

public class HardwareProbeTests
{
    private const long Gib = 1024L * 1024 * 1024;

    private sealed class StubNvml(NvmlResult? result, Exception? boom = null) : INvmlApi
    {
        public NvmlResult Query() => boom != null ? throw boom : result!;
    }

    private static NvmlResult Rtx(string driver = "576.52", int major = 8, int minor = 9) =>
        new(true, driver, 12080, [new NvmlDevice("NVIDIA GeForce RTX 4070", 12 * Gib, major, minor)], null);

    private static readonly VulkanProbeResult GoodVulkan = new(true, "GPU", 8000, 7000, null);

    private static BackendRequest Req(
        BackendOverride pref = BackendOverride.Auto,
        NvmlResult? nvml = null,
        string[]? installed = null,
        VulkanProbeResult? vulkan = null,
        int minDriver = 528,
        FlavourMarks? marks = null,
        IReadOnlyList<ListedDevice>? cudaDevices = null,
        Architecture arch = Architecture.X64)
    {
        var set = (installed ?? ["cpu", "vulkan", "cuda"]).ToHashSet();
        return new BackendRequest
        {
            Preference = pref, Nvml = nvml, IsInstalled = set.Contains, VulkanProbe = vulkan ?? GoodVulkan,
            MinCudaDriverMajor = minDriver, Marks = marks, EngineTag = "b1", VulkanDriver = "31.0", CudaDevices = cudaDevices,
            Architecture = arch,
        };
    }

    private static string[] Flavours(BackendChoice c) => [.. c.Candidates.Select(x => x.Flavour)];

    [Fact]
    public void NvmlProbeNeverThrowsAndMissingMeansNoNvidia()
    {
        var r = NvmlProbe.Probe(new StubNvml(null, new DllNotFoundException("nvml")));
        Assert.False(r.Present);
        Assert.Empty(r.Devices);
        Assert.False(NvmlProbe.Probe(new StubNvml(NvmlResult.None())).Present);
    }

    [Fact]
    public void RealProbeIsSafeOnAnyPc()
    {
        var r = NvmlProbe.Probe();
        Assert.Equal(r.Present, r.Devices.Count > 0);
    }

    [Fact]
    public void NvmlResultParsesDriverAndCudaVersion()
    {
        var r = Rtx();
        Assert.Equal(576, r.DriverMajor);
        Assert.Equal("12.8", r.CudaVersionText);
        Assert.Equal(0, NvmlResult.ParseMajor("weird"));
        Assert.Null(NvmlResult.None().CudaVersionText);
    }

    [Theory]
    [InlineData(6, 1, true)]
    [InlineData(6, 0, false)]
    [InlineData(5, 2, false)]
    [InlineData(12, 0, true)]
    public void ComputeCapabilityCompare(int major, int minor, bool ok) =>
        Assert.Equal(ok, new NvmlDevice("x", Gib, major, minor).AtLeast(6, 1));

    [Fact]
    public void AmdPcStaysVulkanThenCpu()
    {
        var c = BackendSelector.Choose(Req(nvml: NvmlResult.None(), installed: ["cpu", "vulkan"]));
        Assert.Equal(["vulkan", "cpu"], Flavours(c));
        Assert.Contains("cuda", c.Skipped.Keys);
    }

    [Fact]
    public void NvidiaWithCudaInstalledPutsCudaFirst()
    {
        var c = BackendSelector.Choose(Req(nvml: Rtx()));
        Assert.Equal(["cuda", "vulkan", "cpu"], Flavours(c));
    }

    [Fact]
    public void CudaNotInstalledIsSkipped()
    {
        var c = BackendSelector.Choose(Req(nvml: Rtx(), installed: ["cpu", "vulkan"]));
        Assert.Equal(["vulkan", "cpu"], Flavours(c));
    }

    [Theory]
    [InlineData("500.10", 8, 9)]   // driver older than the flavour needs
    [InlineData("576.52", 5, 2)]   // card older than 6.1
    public void OldDriverOrCardSkipsCuda(string driver, int major, int minor)
    {
        var c = BackendSelector.Choose(Req(nvml: Rtx(driver, major, minor)));
        Assert.DoesNotContain("cuda", Flavours(c));
    }

    [Fact]
    public void BadMarkAppliesOnlyToSameDriverAndTag()
    {
        var marks = new FlavourMarks();
        marks.Mark("cuda", "b1", "576.52");
        Assert.DoesNotContain("cuda", Flavours(BackendSelector.Choose(Req(nvml: Rtx(), marks: marks))));
        Assert.Contains("cuda", Flavours(BackendSelector.Choose(Req(nvml: Rtx("580.1"), marks: marks))));
        marks.Clear("cuda");
        Assert.Contains("cuda", Flavours(BackendSelector.Choose(Req(nvml: Rtx(), marks: marks))));
    }

    [Fact]
    public void OverridesReorderButKeepFallbacks()
    {
        var n = Rtx();
        Assert.Equal(["vulkan", "cuda", "cpu"], Flavours(BackendSelector.Choose(Req(BackendOverride.Vulkan, n))));
        Assert.Equal(["cpu"], Flavours(BackendSelector.Choose(Req(BackendOverride.Cpu, n))));
        // Forced CUDA that is not usable falls back instead of failing.
        Assert.Equal(["vulkan", "cpu"], Flavours(BackendSelector.Choose(Req(BackendOverride.Cuda, NvmlResult.None()))));
    }

    [Fact]
    public void Arm64AndFailedVulkanMeanCpu()
    {
        Assert.Equal(["cpu"], Flavours(BackendSelector.Choose(Req(nvml: Rtx(), arch: Architecture.Arm64))));
        var bad = new VulkanProbeResult(false, null, 0, 0, "x");
        Assert.Equal(["cpu"], Flavours(BackendSelector.Choose(Req(vulkan: bad, nvml: NvmlResult.None()))));
    }

    [Fact]
    public void CudaDeviceIsMatchedByNameNotIndex()
    {
        const string output = "Available devices:\n  CUDA0: NVIDIA GeForce GTX 1050 (4096 MiB, 3900 MiB free)\n  CUDA1: NVIDIA GeForce RTX 4070 (12281 MiB, 11000 MiB free)\n";
        var devices = ListDevicesParser.Parse(output, 0, "CUDA");
        Assert.Equal(2, devices.Count);
        var c = BackendSelector.Choose(Req(nvml: Rtx(), cudaDevices: devices));
        Assert.Equal("CUDA1", c.First.DeviceId);
    }

    [Fact]
    public void EmptyCudaDeviceListSkipsCuda()
    {
        var c = BackendSelector.Choose(Req(nvml: Rtx(), cudaDevices: []));
        Assert.DoesNotContain("cuda", Flavours(c));
    }

    [Fact]
    public void ParserHandlesRecordedOutputsAndFailures()
    {
        var cuda = "ggml_cuda_init: found 1 CUDA devices:\n  Device 0: NVIDIA GeForce RTX 3060, compute capability 8.6, VMM: yes\nAvailable devices:\n  CUDA0: NVIDIA GeForce RTX 3060 (12287 MiB, 11200 MiB free)\n";
        var d = Assert.Single(ListDevicesParser.Parse(cuda, 0, "CUDA"));
        Assert.Equal((0, 12287L, 11200L), (d.Index, d.TotalMiB, d.FreeMiB));
        Assert.Empty(ListDevicesParser.Parse(cuda, 0, "Vulkan"));
        Assert.Empty(ListDevicesParser.Parse(cuda, 1, "CUDA"));
        Assert.Empty(ListDevicesParser.Parse("", 0, "CUDA"));
        var noFree = Assert.Single(ListDevicesParser.Parse("  CUDA0: X (8000 MiB)", 0, "CUDA"));
        Assert.Equal(8000, noFree.FreeMiB);
    }

    [Fact]
    public void LegacyVulkanFlagBecomesAMark()
    {
        var m = FlavourMarks.FromLegacyVulkanBad(true, "b1", "31.0");
        Assert.True(m.IsBad("vulkan", "b1", "31.0"));
        Assert.False(m.IsBad("vulkan", "b1", "32.0"));
        Assert.Empty(FlavourMarks.FromLegacyVulkanBad(false, "b1", "31.0").Snapshot());
        Assert.True(new FlavourMarks(m.Snapshot()).IsBad("vulkan", "b1", "31.0"));
    }
}
