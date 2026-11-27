using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Catalogue;
using Portal.AiHost.Core.Planning;
using Portal.AiHost.Core.Runtime;
using Portal.AiHost.Core.Scheduling;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

/// <summary>CPU-only placement preset, the RAM budget across engines, refusal over the limit and the director yield hook.</summary>
public sealed class PlacementPresetTests
{
    private const long GiB = 1L << 30;
    private static readonly string Second = ModelSlots.Second(ModelKinds.Chat);

    private sealed class Probe : IHardwareProbe
    {
        public Task<HardwareProfile> ProbeAsync(CancellationToken ct) => Task.FromResult(new HardwareProfile("windows", "x64", 24,
            64 * GiB, [new GpuProfile("AMD Radeon RX 6600", "amd", "0x1", "31.0", 8 * GiB, 0)], [], "fp"));

        public MemorySnapshot GetMemory() => new(64 * GiB, 50 * GiB, [(long)(7.5 * GiB)]);

        public bool IsOnBattery => false;
    }

    private static CatalogueEntry Model(string id)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "catalogue", "catalogue.json");
            if (File.Exists(Path.Combine(dir.FullName, "PortalAIHost.slnx")) && File.Exists(path))
            {
                return CatalogueValidator.Parse(File.ReadAllBytes(path)).Catalogue!.Models.Single(m => m.Id == id);
            }
        }

        throw new InvalidOperationException("catalogue not found");
    }

    private sealed record Rig(AiHostRuntime Runtime, FakeEngineSupervisor Chat, FakeEngineSupervisor Chat2);

    private static async Task<Rig> StartAsync(PlannerSettings? planner = null, FakeEngineOptions? chat2Options = null)
    {
        using var root = new TestRoot();
        var chat = new FakeEngineSupervisor(ModelKinds.Chat);
        var chat2 = new FakeEngineSupervisor(Second, chat2Options);
        var runtime = await AiHostRuntime.StartAsync(new AiHostRuntimeOptions
        {
            Registry = new Core.Auth.ClientRegistry(new() { DataDirectory = root.Path }, new TestKeyProtector()),
            Scheduler = new FairScheduler(),
            Supervisors = new Dictionary<string, IEngineSupervisor> { [ModelKinds.Chat] = chat, [Second] = chat2 },
            Hardware = new Probe(),
            Planner = planner ?? new PlannerSettings(),
        });
        return new Rig(runtime, chat, chat2);
    }

    private static ActiveModel Active(string kind, string id, PlacementOverride? over = null)
    {
        var e = Model(id);
        return new ActiveModel(kind, e.Id, e.DisplayName, ["a.gguf"], e, Override: over);
    }

    private static PlacementOverride CpuOnly(int? threads = null) => new(DevicePlacements.CpuOnly, null, null, threads);

    [Fact]
    public async Task Cpu_only_preset_gives_zero_gpu_layers_and_half_the_cores_as_thread_cap()
    {
        var rig = await StartAsync();
        await using var _ = rig.Runtime;
        await rig.Runtime.SetActiveAsync(Second, Active(Second, "qwen3-8b-q4km", CpuOnly()), default);
        await rig.Runtime.EnsureLoadedAsync(Second, default);

        Assert.Equal(0, rig.Chat2.Current!.Placement.GpuLayers);
        Assert.Equal("cpu", rig.Chat2.Current.Placement.Backend);
        Assert.Equal(AiHostRuntime.CpuOnlyThreads(24), rig.Chat2.Current.Threads);
        Assert.Equal(12, rig.Chat2.Current.Threads);
    }

    [Fact]
    public async Task Cpu_only_thread_cap_can_be_adjusted_and_works_for_slot_one()
    {
        var rig = await StartAsync();
        await using var _ = rig.Runtime;
        await rig.Runtime.SetActiveAsync(ModelKinds.Chat, Active(ModelKinds.Chat, "qwen3-8b-q4km", CpuOnly(6)), default);
        await rig.Runtime.EnsureLoadedAsync(ModelKinds.Chat, default);

        Assert.Equal(6, rig.Chat.Current!.Threads);
        Assert.Equal(0, rig.Chat.Current.Placement.GpuLayers);
        Assert.True(DevicePlacements.IsValid(DevicePlacements.CpuOnly));
    }

    [Fact]
    public async Task Budget_sums_every_engine_including_a_load_in_progress()
    {
        var rig = await StartAsync(chat2Options: new FakeEngineOptions { LoadDelay = TimeSpan.FromSeconds(5) });
        await using var _ = rig.Runtime;
        var hw = await new Probe().ProbeAsync(default);
        await rig.Runtime.SetActiveAsync(ModelKinds.Chat, Active(ModelKinds.Chat, "qwen3-8b-q4km"), default);
        await rig.Runtime.EnsureLoadedAsync(ModelKinds.Chat, default);
        var one = rig.Runtime.BuildMemoryBudget(hw)!;
        var chatEngine = Assert.Single(one.Engines);
        Assert.Equal(chatEngine.RamBytes, one.RamPlannedBytes);
        Assert.False(chatEngine.Loading);

        await rig.Runtime.SetActiveAsync(Second, Active(Second, "qwen3-8b-q4km", CpuOnly()), default);
        var load = rig.Runtime.EnsureLoadedAsync(Second, default);
        WaitForReservation(rig.Runtime, hw);
        var two = rig.Runtime.BuildMemoryBudget(hw)!;
        Assert.Equal(2, two.Engines.Count);
        Assert.Equal(two.Engines.Sum(e => e.RamBytes), two.RamPlannedBytes);
        Assert.True(two.RamPlannedBytes > one.RamPlannedBytes, "the loading slot's reservation counts");
        Assert.Equal(two.RamLimitBytes - two.RamPlannedBytes, two.RamFreeBytes);
        Assert.Equal(64L * GiB * 80 / 100, two.RamLimitBytes);
        await load;
    }

    private static void WaitForReservation(AiHostRuntime rt, HardwareProfile hw)
    {
        var until = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < until)
        {
            if (rt.BuildMemoryBudget(hw)!.Engines.Any(e => e.Slot == Second && e.Loading))
            {
                return;
            }

            Thread.Sleep(20);
        }

        Assert.Fail("the reservation never showed in the budget");
    }

    [Fact]
    public async Task Load_over_the_ram_limit_is_refused_with_a_plain_reason()
    {
        var rig = await StartAsync(new PlannerSettings { MemoryLimitBytes = 4 * GiB });
        await using var _ = rig.Runtime;
        await rig.Runtime.SetActiveAsync(ModelKinds.Chat, Active(ModelKinds.Chat, "qwen3-30b-a3b-instruct-2507-q4km"), default);
        var ex = await Assert.ThrowsAsync<ApiException>(() => rig.Runtime.EnsureLoadedAsync(ModelKinds.Chat, default));
        Assert.Equal(ErrorCodes.InsufficientMemory, ex.Code);
        Assert.Contains("RAM limit", ex.Message);
        Assert.Contains("4 GB", ex.Message);
    }

    [Fact]
    public async Task Status_carries_the_memory_fields()
    {
        var rig = await StartAsync();
        await using var _ = rig.Runtime;
        var hw = await new Probe().ProbeAsync(default);
        await rig.Runtime.SetActiveAsync(ModelKinds.Chat, Active(ModelKinds.Chat, "qwen3-8b-q4km"), default);
        await rig.Runtime.EnsureLoadedAsync(ModelKinds.Chat, default);

        var status = rig.Runtime.BuildStatus(null, hw);
        var m = status.Memory!;
        Assert.True(m.RamLimitBytes > 0 && m.RamPlannedBytes + m.VramPlannedBytes > 0 && m.RamFreeBytes > 0);
        Assert.Equal(8 * GiB, m.VramTotalBytes);
        Assert.Equal("chat", Assert.Single(m.Engines).Slot);
        var json = System.Text.Json.JsonSerializer.Serialize(status, AiHostJsonContext.Default.HostStatus);
        Assert.Contains("\"ram_limit_bytes\"", json);
        Assert.Contains("\"engines\"", json);
    }

    [Fact]
    public async Task Can_reserve_and_the_director_yield_free_the_cpu_only_slot()
    {
        var rig = await StartAsync();
        await using var _ = rig.Runtime;
        await rig.Runtime.BuildPerformanceViewAsync(null, false, default); // caches the hardware profile the hook reads
        await rig.Runtime.SetActiveAsync(Second, Active(Second, "qwen3-30b-a3b-instruct-2507-q4km", CpuOnly()), default);
        await rig.Runtime.EnsureLoadedAsync(Second, default);
        var hw = await new Probe().ProbeAsync(default);
        Assert.True(rig.Runtime.BuildMemoryBudget(hw)!.Engines.Single(e => e.Slot == Second).CpuOnly);

        var free = rig.Runtime.CanReserve(0, 0).RamFreeBytes;
        var ask = free + 1 * GiB;
        var refused = rig.Runtime.CanReserve(ask, 0);
        Assert.False(refused.Fits);
        Assert.Contains("free under the limit", refused.Reason);
        Assert.True(rig.Runtime.CanReserve(ask, 0, ignoreSecondSlot: true).Fits, "fits when the director yields");
        Assert.False(rig.Runtime.CanReserve(0, 9 * GiB).Fits);

        Assert.True(await rig.Runtime.YieldDirectorAsync(default));
        Assert.Equal(EngineState.Stopped, rig.Chat2.State);
        Assert.True(rig.Runtime.DirectorYielded);
        Assert.True(rig.Runtime.CanReserve(ask, 0).Fits, "the stopped slot no longer holds RAM");

        await rig.Runtime.ResumeDirectorAsync(default);
        Assert.Equal(EngineState.Ready, rig.Chat2.State);
        Assert.False(rig.Runtime.DirectorYielded);
    }

    [Fact]
    public async Task Yield_does_nothing_for_a_slot_that_uses_the_graphics_card()
    {
        var rig = await StartAsync();
        await using var _ = rig.Runtime;
        await rig.Runtime.SetActiveAsync(Second, Active(Second, "qwen3-8b-q4km"), default);
        await rig.Runtime.EnsureLoadedAsync(Second, default);
        Assert.True(rig.Chat2.Current!.Placement.GpuLayers > 0);
        Assert.False(await rig.Runtime.YieldDirectorAsync(default));
        Assert.Equal(EngineState.Ready, rig.Chat2.State);
    }
}
