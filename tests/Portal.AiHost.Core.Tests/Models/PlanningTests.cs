using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Catalogue;
using Portal.AiHost.Core.Planning;
using Portal.AiHost.Core.Store;

namespace Portal.AiHost.Core.Tests.Models;

public class PlanningTests
{
    private const long GiB = 1L << 30;

    private static readonly IReadOnlyList<CatalogueEntry> Seed = CatalogueValidator.Parse(TestData.SeedCatalogueBytes()).Catalogue!.Models;

    public sealed record Machine(string Name, HardwareProfile Hw, MemorySnapshot Mem)
    {
        public override string ToString() => Name;
    }

    private static Machine Pc(string name, long ramGb, long availGb, GpuProfile? gpu, string os = "windows")
    {
        var gpus = gpu is null ? Array.Empty<GpuProfile>() : [gpu];
        var hw = new HardwareProfile(os, os == "macos" ? "arm64" : "x64", 12, ramGb * GiB, gpus,
            [new DriveProfile("C:\\", "NTFS", 2000 * GiB)], name);
        var free = gpu is null || gpu.Vendor == "apple" ? Array.Empty<long>() : [gpu.VramBudgetBytes];
        return new Machine(name, hw, new MemorySnapshot(ramGb * GiB, availGb * GiB, free));
    }

    private static GpuProfile Rx6600 => new("AMD Radeon RX 6600", "amd", "0x1", "31.0.24", 8 * GiB, (long)(7.6 * GiB));
    private static GpuProfile Rtx5090 => new("NVIDIA GeForce RTX 5090", "nvidia", "0x2", "32.0.15", 32 * GiB, 31 * GiB);

    public static readonly Machine Rx6600With64 = Pc("64 GB + RX 6600 8 GB", 64, 50, Rx6600);
    public static readonly Machine MacBook24 = Pc("MacBook-like 24 GB", 24, 18, new GpuProfile("Apple M4 Pro", "apple", "0", "", 0, 0), "macos");
    public static readonly Machine Rtx5090With64 = Pc("RTX 5090 32 GB + 64 GB", 64, 52, Rtx5090);
    public static readonly Machine Rtx5090With128 = Pc("RTX 5090 32 GB + 128 GB", 128, 112, Rtx5090);
    public static readonly Machine Rx6600With128 = Pc("128 GB + RX 6600 8 GB", 128, 112, Rx6600);
    public static readonly Machine Laptop8 = Pc("8 GB laptop", 8, 6, new GpuProfile("Intel UHD Graphics", "intel", "0x3", "31.0", 128L << 20, 128L << 20));

    public static TheoryData<Machine, string> Expected => new()
    {
        { Rx6600With64, "qwen3-30b-a3b-instruct-2507-q4km" },
        { MacBook24, "qwen3-14b-q4km" },
        { Rtx5090With64, "qwen3-next-80b-a3b-instruct-q4km" },
        { Rtx5090With128, "qwen3.5-122b-a10b-q4km" },
        { Rx6600With128, "qwen3.5-122b-a10b-q4km" },
        { Laptop8, "qwen3-4b-q4km" },
    };

    private static RecommendContext Ctx(params string[] installed) => new(installed.ToHashSet(), 4000 * GiB);

    [Theory]
    [MemberData(nameof(Expected))]
    public void RecommendsBestModelThatFits(Machine machine, string expected)
    {
        var rec = Recommender.Recommend(Seed, "chat", machine.Hw, machine.Mem, Ctx());
        Assert.NotNull(rec.Best);
        Assert.Equal(expected, rec.Best.Entry.Id);

        var p = rec.Best.Plan;
        var settings = new PlannerSettings();
        var budget = MemoryBudget.From(machine.Hw, Recommender.Typical(machine.Hw, machine.Mem, settings), settings);
        Assert.True(p.CpuBytes <= budget.RamBudgetBytes, "RAM headroom kept");
        Assert.True(budget.Unified || p.GpuBytes <= budget.VramBudgetBytes, "VRAM reserve kept");
        Assert.True(p.TokensPerSecond >= 8);
        Assert.True(p.PerSlotContext >= 4096);
    }

    [Fact]
    public void RecommendationDoesNotChangeWithLiveFreeMemory()
    {
        var hw = Rx6600With64.Hw;
        var roomy = new MemorySnapshot(hw.RamBytes, hw.RamBytes * 9 / 10, [7 * GiB]);
        var busy = new MemorySnapshot(hw.RamBytes, 6 * GiB, [2 * GiB]);
        var a = Recommender.Recommend(Seed, "chat", hw, roomy, Ctx());
        var b = Recommender.Recommend(Seed, "chat", hw, busy, Ctx());
        Assert.Equal(a.Best?.Entry.Id, b.Best?.Entry.Id);
    }

    [Fact]
    public void TooBigTextAgreesWithTheVerdict()
    {
        var rec = Recommender.Recommend(Seed, "chat", Laptop8.Hw, Laptop8.Mem, Ctx());
        foreach (var c in rec.Candidates.Where(c => !c.Plan.Fits))
        {
            Assert.Matches(@"needs about [\d.]+ GB; this PC can give it about [\d.]+ GB|too big to split", c.Reason);
            var m = System.Text.RegularExpressions.Regex.Match(c.Reason, @"needs about ([\d.]+) GB; this PC can give it about ([\d.]+) GB");
            if (m.Success)
            {
                Assert.True(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                    >= double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    [Fact]
    public void MoeOnSmallGpuKeepsAttentionOnGpuAndExpertsInRam()
    {
        var model = Seed.Single(m => m.Id == "qwen3-30b-a3b-instruct-2507-q4km");
        var plan = PlacementPlanner.Plan(model, Rx6600With64.Hw, Rx6600With64.Mem);
        Assert.True(plan.Fits);
        Assert.Equal("vulkan", plan.Placement!.Backend);
        Assert.Equal(model.Memory.NLayers, plan.Placement.GpuLayers);
        Assert.InRange(plan.Placement.CpuMoeLayers!.Value, 1, model.Memory.NLayers - 1);
        Assert.Equal(8192, plan.Placement.ContextTokens);
    }

    [Fact]
    public void ReasonIsOnePlainSentenceWithoutLayerOrExpertTalk()
    {
        var model = Seed.Single(m => m.Id == "qwen3-30b-a3b-instruct-2507-q4km");
        var split = PlacementPlanner.Plan(model, Rx6600With64.Hw, Rx6600With64.Mem);
        Assert.Matches(@"^Runs well on your graphics card and memory, about \d+ words a second$", split.Reason);
        var cpu = PlacementPlanner.Plan(model, Rtx5090With64.Hw, Rtx5090With64.Mem, new PlannerSettings { Backend = "cpu" });
        Assert.Matches(@"^Runs on your processor, about \d+ words a second$", cpu.Reason);
        Assert.DoesNotContain("expert", split.Reason);
        Assert.DoesNotContain("attention", split.Reason);
    }

    [Fact]
    public void DenseModelOnBigGpuRunsFullyOnGpuAndGrowsContextAndSlots()
    {
        var model = Seed.Single(m => m.Id == "qwen3-14b-q4km");
        var plan = PlacementPlanner.Plan(model, Rtx5090With64.Hw, Rtx5090With64.Mem);
        Assert.Equal(model.Memory.NLayers, plan.Placement!.GpuLayers);
        Assert.Null(plan.Placement.CpuMoeLayers);
        Assert.Equal(model.ContextMax, plan.PerSlotContext);
        Assert.True(plan.Placement.ParallelSlots > 1);
    }

    [Fact]
    public void ContextDoesNotGrowAtTheCostOfGpuLayers()
    {
        var model = Seed.Single(m => m.Id == "qwen3-14b-q4km");
        var plan = PlacementPlanner.Plan(model, Rx6600With64.Hw, Rx6600With64.Mem);
        Assert.InRange(plan.Placement!.GpuLayers, 1, model.Memory.NLayers - 1);
        Assert.Equal(model.ContextDefault, plan.Placement.ContextTokens);
    }

    [Fact]
    public void LongContextUsesQ8Kv()
    {
        var model = Seed.Single(m => m.Id == "qwen3-30b-a3b-instruct-2507-q4km");
        var plan = PlacementPlanner.Plan(model, Rtx5090With128.Hw, Rtx5090With128.Mem);
        Assert.True(plan.PerSlotContext > 32768);
        Assert.Equal("q8_0", plan.Placement!.KvType);
    }

    [Fact]
    public void CpuSettingForcesCpuBackend()
    {
        var model = Seed.Single(m => m.Id == "qwen3-8b-q4km");
        var plan = PlacementPlanner.Plan(model, Rtx5090With64.Hw, Rtx5090With64.Mem, new PlannerSettings { Backend = "cpu" });
        Assert.Equal("cpu", plan.Placement!.Backend);
        Assert.Equal(0, plan.Placement.GpuLayers);
        Assert.Empty(plan.Placement.Devices);
    }

    [Fact]
    public void SlowDenseOffloadIsNotRecommended()
    {
        var rec = Recommender.Recommend(Seed, "chat", Rx6600With64.Hw, Rx6600With64.Mem, Ctx());
        var mistral = rec.Candidates.Single(c => c.Entry.Id == "mistral-small-3.2-24b-q4km");
        Assert.True(mistral.Plan.Fits);
        Assert.False(mistral.Recommendable);
        Assert.Contains("too slow", mistral.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelsThatDoNotFitOnDiskAreExplained()
    {
        var rec = Recommender.Recommend(Seed, "chat", Rtx5090With128.Hw, Rtx5090With128.Mem,
            new RecommendContext(new HashSet<string>(), 60 * GiB, "C:"));
        var big = rec.Candidates.Single(c => c.Entry.Id == "qwen3.5-122b-a10b-q4km");
        Assert.False(big.FitsOnDisk);
        Assert.Contains("C: has 64 GB free", big.Reason, StringComparison.Ordinal);
        Assert.True(rec.Best!.Entry.TotalBytes < 60 * GiB);
    }

    [Fact]
    public void MaxMemoryCapLimitsTheChoice()
    {
        var capped = Recommender.Recommend(Seed, "chat", Rx6600With128.Hw, Rx6600With128.Mem,
            Ctx() with { Settings = new PlannerSettings { MaxMemoryPercent = 25 } });
        Assert.True(capped.Best!.Plan.CpuBytes <= 32 * GiB);
        Assert.NotEqual("qwen3.5-122b-a10b-q4km", capped.Best.Entry.Id);
    }

    [Fact]
    public void EmbeddingRecommendationFitsEverywhere()
    {
        foreach (var m in new[] { Laptop8, MacBook24, Rx6600With64 })
        {
            var rec = Recommender.Recommend(Seed, "embedding", m.Hw, m.Mem, Ctx());
            Assert.Equal("embedding", rec.Best!.Entry.Kind);
        }
    }

    [Fact]
    public async Task RecheckOffersUpgradeAndKeepsOldModelUntilNewIsVerified()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        const string oldId = "qwen3-30b-a3b-instruct-2507-q4km";
        var part = await store.BeginFileAsync(oldId, "old.gguf", CancellationToken.None);
        await File.WriteAllBytesAsync(part, new byte[16]);
        var oldFile = await store.CommitFileAsync(oldId, part, TestData.Sha(new byte[16]), CancellationToken.None);

        var same = HardwareRecheck.Run(Rx6600With64.Hw.Fingerprint, Rx6600With64.Hw, Rx6600With64.Mem, Seed, "chat", oldId, Ctx(oldId));
        Assert.False(same.FingerprintChanged);
        Assert.Null(same.Upgrade);
        Assert.True(same.ActivePlan!.Fits);

        // New GPU and RAM: the active model is re-planned and a better model is offered.
        var bigger = HardwareRecheck.Run(Rx6600With64.Hw.Fingerprint, Rtx5090With128.Hw, Rtx5090With128.Mem, Seed, "chat", oldId, Ctx(oldId));
        Assert.True(bigger.FingerprintChanged);
        Assert.Equal(oldId, bigger.Upgrade!.FromModelId);
        Assert.True(bigger.Upgrade.DownloadBytes > 0);
        Assert.Equal(bigger.ActivePlan!.Placement!.GpuLayers, Seed.Single(m => m.Id == oldId).Memory.NLayers);
        Assert.Equal(0, bigger.ActivePlan.Placement.CpuMoeLayers);

        var flow = new UpgradeFlow(store);
        Assert.False(await flow.CanSwitchAsync(bigger.Upgrade, probePassed: true, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.SwitchAsync(bigger.Upgrade, true, CancellationToken.None));

        // New model downloaded (one part still partial): still no switch.
        var newId = bigger.Upgrade.ToModelId;
        var p1 = await store.BeginFileAsync(newId, "new-00001-of-00002.gguf", CancellationToken.None);
        var p2 = await store.BeginFileAsync(newId, "new-00002-of-00002.gguf", CancellationToken.None);
        await store.CommitFileAsync(newId, p1, TestData.Sha([]), CancellationToken.None);
        Assert.False(await flow.CanSwitchAsync(bigger.Upgrade, true, CancellationToken.None));
        await store.CommitFileAsync(newId, p2, TestData.Sha([]), CancellationToken.None);
        Assert.False(await flow.CanSwitchAsync(bigger.Upgrade, probePassed: false, CancellationToken.None));

        var switched = await flow.SwitchAsync(bigger.Upgrade, probePassed: true, CancellationToken.None);
        Assert.Equal(newId, switched.ActiveModelId);
        Assert.Equal(oldId, switched.KeptModelId);
        Assert.True(File.Exists(oldFile.Path), "the old model is kept until the user removes it");
        Assert.NotNull(await store.GetAsync(oldId, CancellationToken.None));
    }

    [Fact]
    public void CandidatesCarryReasonCodesAndByteFigures()
    {
        var rec = Recommender.Recommend(Seed, "chat", Rtx5090With128.Hw, Rtx5090With128.Mem,
            new RecommendContext(new HashSet<string>(), 60 * GiB, "C:"));
        var big = rec.Candidates.Single(c => c.Entry.Id == "qwen3.5-122b-a10b-q4km");
        Assert.Equal(ReasonCode.NotEnoughDisk, big.ReasonCode);
        Assert.Equal(60 * GiB, big.DriveFreeBytes);
        Assert.True(big.NeededBytes > 0 && big.AvailableBytes > 0);
        Assert.Equal(ReasonCode.Recommendable, rec.Best!.ReasonCode);

        var small = Recommender.Recommend(Seed, "chat", Laptop8.Hw, Laptop8.Mem, Ctx());
        Assert.Contains(small.Candidates, c => c.ReasonCode == ReasonCode.NotEnoughMemory && c.NeededBytes > c.AvailableBytes);
    }

    [Fact]
    public void CommittedMemoryReducesWhatIsRecommended()
    {
        var free = Recommender.Recommend(Seed, "chat", Rtx5090With128.Hw, Rtx5090With128.Mem, Ctx());
        var committed = new CommittedMemory(Rtx5090With128.Hw.RamBytes - 6 * GiB, Rtx5090With128.Hw.Gpus.Select(_ => 0L).ToList());
        var held = Recommender.Recommend(Seed, "chat", Rtx5090With128.Hw, Rtx5090With128.Mem,
            new RecommendContext(new HashSet<string>(), 4000 * GiB, Committed: committed));
        Assert.True(held.Candidates.Count(c => c.Recommendable) < free.Candidates.Count(c => c.Recommendable));
    }
}
