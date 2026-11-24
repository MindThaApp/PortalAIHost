using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Catalogue;
using Portal.AiHost.Core.Planning;

namespace Portal.AiHost.Core.Tests.Models;

public class PerformanceModelTests
{
    private const long GiB = 1L << 30;

    private static readonly IReadOnlyList<CatalogueEntry> Seed = CatalogueValidator.Parse(TestData.SeedCatalogueBytes()).Catalogue!.Models;

    private static HardwareProfile Pc(long ramGb, int cores, long vramGb)
    {
        GpuProfile[] gpus = vramGb == 0 ? [] : [new GpuProfile("Test GPU", "amd", "0x1", "1", vramGb * GiB, vramGb * GiB)];
        return new HardwareProfile("windows", "x64", cores, ramGb * GiB, gpus, [], "fp");
    }

    private static MemorySnapshot Mem(HardwareProfile hw) =>
        new(hw.RamBytes, hw.RamBytes * 3 / 4, hw.Gpus.Select(g => g.VramBudgetBytes).ToArray());

    [Fact]
    public void NewInstallUsesBalancedDefaults()
    {
        var l = PerformanceModel.Resolve(new HostSettings(), Pc(64, 12, 8));
        Assert.Equal("balanced", l.Preset);
        Assert.Equal(64 * GiB * 80 / 100, l.MemoryLimitBytes);
        Assert.Equal(8 * GiB, l.KeepFreeBytes);        // 32 GB+ PCs
        Assert.Equal("auto", l.Compute);
        Assert.Equal(8 * GiB - GiB / 2, l.GpuMemoryLimitBytes);
        Assert.Equal(10, l.CpuThreads);                 // logical minus 2
        Assert.True(l.LowPriority);
    }

    [Fact]
    public void SmallPcKeepsFourGbFree()
    {
        var l = PerformanceModel.Resolve(new HostSettings(), Pc(16, 4, 0));
        Assert.Equal(4 * GiB, l.KeepFreeBytes);
        Assert.Equal(2, l.CpuThreads);
        Assert.Null(l.GpuMemoryLimitBytes);
        Assert.False(l.GpuAvailable);
    }

    [Theory]
    [InlineData("quiet", 50, 6, "low")]
    [InlineData("balanced", 80, 10, "low")]
    [InlineData("fast", 100, 11, "normal")]
    public void PresetsMatchTheSpec(string preset, int pct, int cores, string priority)
    {
        var v = PerformanceModel.PresetValues(preset, 64 * GiB, 12);
        Assert.Equal(cores, v.CpuThreads);
        Assert.Equal(priority, v.Priority);
        var expected = preset == "fast" ? 56 * GiB : 64 * GiB * pct / 100;
        Assert.Equal(expected, v.MemoryLimitBytes);
    }

    [Fact]
    public void OlderMemoryPercentMapsToBytes()
    {
        var l = PerformanceModel.Resolve(new HostSettings { MaxMemoryPercent = 60 }, Pc(64, 12, 0));
        Assert.Equal(64 * GiB * 60 / 100, l.MemoryLimitBytes);
        // An explicit byte limit wins over the old percentage.
        var e = PerformanceModel.Resolve(new HostSettings { MaxMemoryPercent = 60, MemoryLimitBytes = 20 * GiB }, Pc(64, 12, 0));
        Assert.Equal(20 * GiB, e.MemoryLimitBytes);
    }

    [Fact]
    public void ValuesAreClampedToTheirRanges()
    {
        var l = PerformanceModel.Resolve(new HostSettings
        {
            MemoryLimitBytes = 500 * GiB, KeepFreeBytes = GiB, CpuThreads = 99, GpuMemoryLimitBytes = 99 * GiB,
        }, Pc(32, 8, 8));
        Assert.Equal(2 * GiB, l.KeepFreeBytes);
        Assert.Equal(30 * GiB, l.MemoryLimitBytes);     // RAM minus keep free
        Assert.Equal(7, l.CpuThreads);                  // logical minus 1
        Assert.Equal(7 * GiB + GiB / 2, l.GpuMemoryLimitBytes);
        var low = PerformanceModel.Resolve(new HostSettings { MemoryLimitBytes = 1, CpuThreads = 0 }, Pc(32, 8, 0));
        Assert.Equal(2 * GiB, low.MemoryLimitBytes);
        Assert.Equal(1, low.CpuThreads);
    }

    [Fact]
    public void GraphicsCardChoiceFallsBackToProcessorWithoutASupportedCard()
    {
        Assert.Equal("cpu", PerformanceModel.Resolve(new HostSettings { Compute = "gpu" }, Pc(32, 8, 0)).Compute);
        Assert.Equal("cpu", PerformanceModel.Resolve(new HostSettings { Compute = "gpu" }, Pc(32, 8, 2)).Compute); // 2 GB is too small
        Assert.Equal("gpu", PerformanceModel.Resolve(new HostSettings { Compute = "gpu" }, Pc(32, 8, 8)).Compute);
        // The older backend setting still counts when the new one is automatic.
        Assert.Equal("cpu", PerformanceModel.Resolve(new HostSettings { Backend = "cpu" }, Pc(32, 8, 8)).Compute);
    }

    [Fact]
    public void RangesFollowTheHardware()
    {
        var mem = PerformanceModel.MemoryRange(64 * GiB, 8 * GiB);
        Assert.Equal((2 * GiB, 56 * GiB, GiB), (mem.Min, mem.Max, mem.Step!.Value));
        Assert.Equal(11, PerformanceModel.ThreadRange(12).Max);
        Assert.Null(PerformanceModel.GpuRange(2 * GiB));
        Assert.Equal(7 * GiB + GiB / 2, PerformanceModel.GpuRange(8 * GiB)!.Max);
    }

    [Fact]
    public void PlannerPlansAgainstTheMemoryLimitAndKeepFree()
    {
        var hw = Pc(64, 12, 0);
        var big = Seed.Where(m => m.Kind == "chat" && m.TotalBytes < 40 * GiB).OrderByDescending(m => m.TotalBytes).First();
        var roomy = PerformanceModel.PlannerFor(new HostSettings { MemoryLimitBytes = 56 * GiB, KeepFreeBytes = 4 * GiB }, hw);
        var tight = PerformanceModel.PlannerFor(new HostSettings { MemoryLimitBytes = 8 * GiB }, hw);
        Assert.True(PlacementPlanner.Plan(big, hw, Mem(hw), roomy).Fits);
        Assert.False(PlacementPlanner.Plan(big, hw, Mem(hw), tight).Fits);

        var budget = MemoryBudget.From(hw, Mem(hw), tight);
        Assert.True(budget.RamBudgetBytes <= 8 * GiB);
        var kept = MemoryBudget.From(hw, new MemorySnapshot(64 * GiB, 20 * GiB, []), new PlannerSettings { MemoryLimitBytes = 60 * GiB, KeepFreeBytes = 12 * GiB });
        Assert.Equal(8 * GiB, kept.RamBudgetBytes); // 20 GB free minus 12 GB kept
    }

    [Fact]
    public void GraphicsMemoryLimitCapsTheVramBudget()
    {
        var hw = Pc(32, 12, 8);
        var open = MemoryBudget.From(hw, Mem(hw), new PlannerSettings());
        var capped = MemoryBudget.From(hw, Mem(hw), new PlannerSettings { GpuMemoryLimitBytes = 3 * GiB });
        Assert.True(open.VramBudgetBytes > 3 * GiB);
        Assert.Equal(3 * GiB, capped.VramBudgetBytes);
    }

    [Fact]
    public void ProcessorChoicePlansWithoutTheGraphicsCard()
    {
        var hw = Pc(64, 12, 8);
        var model = Seed.Where(m => m.Kind == "chat" && m.TotalBytes < 8 * GiB).OrderByDescending(m => m.Quality).First();
        var cpu = PerformanceModel.PlannerFor(new HostSettings { Compute = "cpu" }, hw);
        var plan = PlacementPlanner.Plan(model, hw, Mem(hw), cpu);
        Assert.True(plan.Fits);
        Assert.Equal("cpu", plan.Placement!.Backend);
        Assert.Equal(0, plan.Placement.GpuLayers);
        Assert.Equal(PerformanceFits.CpuOnly, PerformanceModel.Classify(plan, false));
        var l = PerformanceModel.Resolve(new HostSettings { Compute = "cpu" }, hw);
        Assert.Equal("cpu", PerformanceModel.EffectiveCompute(l, plan));
    }

    [Fact]
    public void FitWordsFollowThePlan()
    {
        var hw = Pc(64, 12, 24);
        var small = Seed.Where(m => m.Kind == "chat" && m.TotalBytes < 6 * GiB).OrderByDescending(m => m.Quality).First();
        var p = PerformanceModel.PlannerFor(new HostSettings(), hw);
        Assert.Equal(PerformanceFits.FitsFully, PerformanceModel.Classify(PlacementPlanner.Plan(small, hw, Mem(hw), p), true));
        var hugeEntry = Seed.Where(m => m.Kind == "chat").OrderByDescending(m => m.TotalBytes).First();
        var tiny = PerformanceModel.PlannerFor(new HostSettings { MemoryLimitBytes = 2 * GiB, Compute = "cpu" }, hw);
        var noFit = PlacementPlanner.Plan(hugeEntry, hw, Mem(hw), tiny);
        Assert.False(noFit.Fits);
        Assert.Equal(PerformanceFits.NeedsSmallerModel, PerformanceModel.Classify(noFit, true));
        Assert.Equal(PerformanceFits.TooBig, PerformanceModel.Classify(noFit, false));
    }

    [Fact]
    public void MemoryStopsGrowWithTheLimit()
    {
        var hw = Pc(64, 12, 8);
        var l = PerformanceModel.Resolve(new HostSettings(), hw);
        var stops = PerformanceModel.MemoryStops(Seed, hw, Mem(hw), l);
        Assert.Equal(2 * GiB, stops[0].Bytes);
        Assert.Equal(l.RamBytes - l.KeepFreeBytes, stops[^1].Bytes);
        Assert.NotNull(stops[^1].FitsModel);
        Assert.NotEqual(stops[2].FitsModel, stops[^1].FitsModel);
    }
}
