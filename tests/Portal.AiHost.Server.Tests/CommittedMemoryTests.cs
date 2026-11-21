using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Catalogue;
using Portal.AiHost.Core.Planning;
using Portal.AiHost.Core.Runtime;
using Portal.AiHost.Core.Scheduling;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

/// <summary>Load decisions plan against free memory minus what the other running engine holds.</summary>
public sealed class CommittedMemoryTests
{
    private const long GiB = 1L << 30;

    private sealed class Rx6600Probe : IHardwareProbe
    {
        public Task<HardwareProfile> ProbeAsync(CancellationToken ct) => Task.FromResult(new HardwareProfile("windows", "x64", 12,
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

    private static async Task<AiHostRuntime> StartAsync(FakeEngineSupervisor chat, FakeEngineSupervisor other)
    {
        using var root = new TestRoot();
        var runtime = await AiHostRuntime.StartAsync(new AiHostRuntimeOptions
        {
            Registry = new Core.Auth.ClientRegistry(new() { DataDirectory = root.Path }, new TestKeyProtector()),
            Scheduler = new FairScheduler(),
            Supervisors = new Dictionary<string, IEngineSupervisor> { [ModelKinds.Chat] = chat, [ModelKinds.Embedding] = other },
            Hardware = new Rx6600Probe(),
        });
        var big = Model("qwen3-30b-a3b-instruct-2507-q4km");
        var second = Model("qwen3-8b-q4km");
        await runtime.SetActiveAsync(ModelKinds.Chat, new ActiveModel(ModelKinds.Chat, big.Id, big.DisplayName, ["a.gguf"], big), default);
        await runtime.SetActiveAsync(ModelKinds.Embedding, new ActiveModel(ModelKinds.Embedding, second.Id, second.DisplayName, ["b.gguf"], second), default);
        return runtime;
    }

    [Fact]
    public async Task Second_engine_is_planned_with_the_first_engines_memory_subtracted()
    {
        var aloneChat = new FakeEngineSupervisor(ModelKinds.Chat);
        await using var alone = await StartAsync(aloneChat, new FakeEngineSupervisor(ModelKinds.Embedding));
        Assert.Equal(CommittedMemory.None, alone.CommittedExcept(ModelKinds.Chat));
        await alone.EnsureLoadedAsync(ModelKinds.Chat, default);
        var alonePlacement = aloneChat.Current!.Placement;

        var sharedChat = new FakeEngineSupervisor(ModelKinds.Chat);
        await using var shared = await StartAsync(sharedChat, new FakeEngineSupervisor(ModelKinds.Embedding));
        await shared.EnsureLoadedAsync(ModelKinds.Embedding, default);
        var committed = shared.CommittedExcept(ModelKinds.Chat);
        Assert.True(committed.RamBytes + committed.VramFor(0) > 0, "the loaded engine's memory counts as committed");
        await shared.EnsureLoadedAsync(ModelKinds.Chat, default);
        var sharedPlacement = sharedChat.Current!.Placement;

        var expected = PlacementPlanner.Plan(Model("qwen3-30b-a3b-instruct-2507-q4km"), await new Rx6600Probe().ProbeAsync(default),
            new Rx6600Probe().GetMemory(), new PlannerSettings(), committed);
        Assert.Equal(Key(expected.Placement!), Key(sharedPlacement));
        Assert.True(sharedPlacement.GpuLayers < alonePlacement.GpuLayers, $"alone {Key(alonePlacement)} shared {Key(sharedPlacement)} committed {committed.RamBytes} {committed.VramFor(0)}");
        Assert.NotEqual(Key(alonePlacement), Key(sharedPlacement));
    }

    private static (int, int?, int, int, string) Key(Placement p) =>
        (p.GpuLayers, p.CpuMoeLayers, p.ContextTokens, p.ParallelSlots, p.KvType);
}
