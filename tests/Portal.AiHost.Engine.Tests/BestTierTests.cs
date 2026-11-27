using System.Text.Json;
using Portal.AiHost.Engine.Upscale;

namespace Portal.AiHost.Engine.Tests;

public sealed class BestTierGateTests
{
    private sealed class Hw(long ramGb, params GpuAdapterInfo[] gpus) : IGpuInfoSource
    {
        public IReadOnlyList<GpuAdapterInfo> GetGpus() => gpus;

        public long TotalRamBytes => ramGb * 1024 * 1024 * 1024;
    }

    private const long Gb = 1_000_000_000;

    private static GpuAdapterInfo Nv(long vram, bool cuda = true) => new("RTX", "NVIDIA", vram, cuda);

    [Theory]
    [InlineData(12L * 1024 * 1024 * 1024, 32, true, "seedvr2-3b")]
    [InlineData(16L * 1024 * 1024 * 1024, 32, true, "seedvr2-3b")]
    [InlineData(24L * 1024 * 1024 * 1024, 64, true, "seedvr2-7b")]
    [InlineData(8L * 1024 * 1024 * 1024, 32, false, null)]
    [InlineData(11 * Gb, 32, false, null)]
    [InlineData(12L * 1024 * 1024 * 1024, 16, false, null)]
    public void GateTable(long vram, long ramGb, bool ready, string? model)
    {
        var a = BestTierGate.Evaluate(new Hw(ramGb, Nv(vram)));
        Assert.Equal(ready, a.IsReady);
        Assert.Equal(model, a.RecommendedModel);
        if (!ready)
        {
            Assert.Equal("needs_hardware", a.Status);
            Assert.False(string.IsNullOrEmpty(a.Reason));
        }
    }

    [Fact]
    public void AmdOnlyAndNoCudaAreRefused()
    {
        Assert.False(BestTierGate.Evaluate(new Hw(64, new GpuAdapterInfo("RX 7900", "AMD", 24 * Gb, false))).IsReady);
        Assert.False(BestTierGate.Evaluate(new Hw(64, Nv(24 * Gb, cuda: false))).IsReady);
        Assert.False(BestTierGate.Evaluate(new Hw(64)).IsReady);
    }

    [Fact]
    public void BiggestNvidiaCardDecidesAndFlashVsrNeedsVerifiedLicence()
    {
        var hw = new Hw(64, new GpuAdapterInfo("iGPU", "Intel", 1 * Gb, false), Nv(12L * 1024 * 1024 * 1024), Nv(24L * 1024 * 1024 * 1024));
        var a = BestTierGate.Evaluate(hw);
        Assert.Equal("seedvr2-7b", a.RecommendedModel);
        Assert.DoesNotContain("flashvsr", a.Models);
        Assert.Contains("flashvsr", BestTierGate.Evaluate(hw, flashVsrLicenceVerified: true).Models);
    }

    [Fact]
    public void InstallPlanTextStatesSizeAndConsent()
    {
        var plan = BestTierInstallPlan.Create("seedvr2-3b");
        Assert.InRange(plan.TotalBytes, 3_000_000_000, 20_000_000_000);
        Assert.Contains("Nothing is downloaded until you agree", plan.Text);
    }
}

public sealed class BestTierEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt_" + Guid.NewGuid().ToString("N"));

    public BestTierEngineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true); // folder this test class created itself
        }
        catch (IOException)
        {
        }
    }

    private sealed class Hw(bool ok = true) : IGpuInfoSource
    {
        public IReadOnlyList<GpuAdapterInfo> GetGpus() => ok ? [new("RTX 4070", "NVIDIA", 12L * 1024 * 1024 * 1024, true)] : [];

        public long TotalRamBytes => 32L * 1024 * 1024 * 1024;
    }

    private static string FakeExe => Path.Combine(AppContext.BaseDirectory, "FakeBestWorker.exe");

    private BestTierInstall Install(string mode)
    {
        var addon = Path.Combine(_root, "addon_" + mode);
        Directory.CreateDirectory(Path.Combine(addon, "worker"));
        var marker = JsonSerializer.Serialize(new Dictionary<string, object> { ["python"] = FakeExe, ["args"] = mode.Length == 0 ? Array.Empty<string>() : new[] { mode } });
        File.WriteAllText(Path.Combine(addon, BestTierInstall.MarkerFileName), marker);
        return new BestTierInstall(addon);
    }

    private (string In, string Out) Frames(int count)
    {
        var dir = Path.Combine(_root, "seg_" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(dir, "in");
        Directory.CreateDirectory(input);
        for (var i = 1; i <= count; i++)
        {
            File.WriteAllText(Path.Combine(input, $"{i:D4}.png"), "frame" + i);
        }

        return (input, Path.Combine(dir, "out"));
    }

    [Fact]
    public async Task MissingInstallReportsNotInstalled()
    {
        await using var e = new BestTierUpscaleEngine(new BestTierInstall(Path.Combine(_root, "none")), new Hw());
        var s = await e.CheckAsync(CancellationToken.None);
        Assert.Equal(UpscaleEngineStatus.NotInstalled, s.Status);
    }

    [Fact]
    public async Task MarkerWithoutPythonReportsBroken()
    {
        var addon = Path.Combine(_root, "broken");
        Directory.CreateDirectory(addon);
        File.WriteAllText(Path.Combine(addon, BestTierInstall.MarkerFileName), "{\"python\":\"gone\\\\python.exe\"}");
        await using var e = new BestTierUpscaleEngine(new BestTierInstall(addon), new Hw());
        Assert.Equal(UpscaleEngineStatus.Broken, (await e.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task WeakHardwareReportsNeedsHardwareBeforeLookingAtInstall()
    {
        await using var e = new BestTierUpscaleEngine(Install(""), new Hw(ok: false));
        Assert.Equal(UpscaleEngineStatus.NeedsHardware, (await e.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task CheckReadyThenUpscaleReportsProgressAndWritesNewFrames()
    {
        await using var e = new BestTierUpscaleEngine(Install(""), new Hw());
        Assert.True((await e.CheckAsync(CancellationToken.None)).IsReady);
        var (inp, outp) = Frames(3);
        var seen = new List<UpscaleProgress>();
        await e.UpscaleSegmentAsync(new UpscaleSegmentRequest(2, inp, outp, "seedvr2-3b", 2), new Progress<UpscaleProgress>(seen.Add), CancellationToken.None);
        await Task.Delay(100);
        Assert.Equal(3, Directory.GetFiles(outp, "*.png").Length);
        Assert.Contains(seen, p => p.Segment == 2 && p.FramesTotal == 3);
    }

    [Fact]
    public async Task ExistingOutputFramesAreKeptNotOverwritten()
    {
        await using var e = new BestTierUpscaleEngine(Install(""), new Hw());
        var (inp, outp) = Frames(2);
        Directory.CreateDirectory(outp);
        File.WriteAllText(Path.Combine(outp, "0001.png"), "finished earlier");
        await e.UpscaleSegmentAsync(new UpscaleSegmentRequest(0, inp, outp, "m", 2), null, CancellationToken.None);
        Assert.Equal("finished earlier", File.ReadAllText(Path.Combine(outp, "0001.png")));
        Assert.True(File.Exists(Path.Combine(outp, "0002.png")));
    }

    [Fact]
    public async Task WorkerErrorSurfacesWithItsCode()
    {
        await using var e = new BestTierUpscaleEngine(Install("oom"), new Hw());
        var (inp, outp) = Frames(1);
        var ex = await Assert.ThrowsAsync<UpscaleEngineException>(() =>
            e.UpscaleSegmentAsync(new UpscaleSegmentRequest(0, inp, outp, "m", 2), null, CancellationToken.None));
        Assert.Equal("insufficient_memory", ex.Code);
    }

    [Fact]
    public async Task NoCudaProbeReportsBroken()
    {
        await using var e = new BestTierUpscaleEngine(Install("nocuda"), new Hw());
        var s = await e.CheckAsync(CancellationToken.None);
        Assert.Equal(UpscaleEngineStatus.Broken, s.Status);
        Assert.Contains("no_cuda", s.Reason);
    }

    [Fact]
    public async Task CancelStopsTheSegmentCleanly()
    {
        await using var e = new BestTierUpscaleEngine(Install("slow"), new Hw());
        var (inp, outp) = Frames(30);
        using var cts = new CancellationTokenSource();
        var progress = new Progress<UpscaleProgress>(p =>
        {
            if (p.FramesDone >= 2)
            {
                cts.Cancel();
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            e.UpscaleSegmentAsync(new UpscaleSegmentRequest(0, inp, outp, "m", 2), progress, cts.Token));
        Assert.True(Directory.GetFiles(outp, "*.png").Length < 30);
    }

    [Fact]
    public async Task HungWorkerIsKilledOnCancelAndNothingIsLeftRunning()
    {
        await using var e = new BestTierUpscaleEngine(Install("hang"), new Hw());
        var (inp, outp) = Frames(1);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            e.UpscaleSegmentAsync(new UpscaleSegmentRequest(0, inp, outp, "m", 2), null, cts.Token));
    }

    [Fact]
    public void UpscaleCommandIsOneLineOfJson()
    {
        var line = BestTierUpscaleEngine.BuildUpscaleCommand(new UpscaleSegmentRequest(1, @"C:\a b\in", @"C:\a b\out", "seedvr2-3b", 2), @"C:\m");
        Assert.DoesNotContain('\n', line);
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("upscale", doc.RootElement.GetProperty("op").GetString());
        Assert.Equal(@"C:\a b\in", doc.RootElement.GetProperty("input_dir").GetString());
    }
}
