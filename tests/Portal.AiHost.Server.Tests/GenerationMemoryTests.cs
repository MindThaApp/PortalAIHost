using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Catalogue;
using Portal.AiHost.Core.Generation;
using Portal.AiHost.Core.Runtime;
using Portal.AiHost.Core.Scheduling;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

/// <summary>A video job next to a processor-only director (plan section 6.4): yield, memory budget slot, resume.</summary>
public sealed class GenerationMemoryTests
{
    private const long GiB = 1L << 30;
    private static readonly string Second = ModelSlots.Second(ModelKinds.Chat);

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

    [Fact]
    public async Task AClipThatNeedsTheDirectorsRamMakesItYieldAndResume()
    {
        using var root = new TestRoot();
        var chat = new FakeEngineSupervisor(ModelKinds.Chat);
        var chat2 = new FakeEngineSupervisor(Second);
        using var scheduler = new FairScheduler();
        await using var runtime = await AiHostRuntime.StartAsync(new AiHostRuntimeOptions
        {
            Registry = new Core.Auth.ClientRegistry(new() { DataDirectory = root.Path }, new TestKeyProtector()),
            Scheduler = scheduler,
            Supervisors = new Dictionary<string, IEngineSupervisor> { [ModelKinds.Chat] = chat, [Second] = chat2 },
            Hardware = new GenerationProbe(),
        });
        await runtime.BuildPerformanceViewAsync(null, false, default);
        var e = Model("qwen3-30b-a3b-instruct-2507-q4km");
        await runtime.SetActiveAsync(Second, new ActiveModel(Second, e.Id, e.DisplayName, ["a.gguf"], e,
            Override: new PlacementOverride(DevicePlacements.CpuOnly, null, null, null)), default);
        await runtime.EnsureLoadedAsync(Second, default);
        Assert.True(runtime.DirectorCanYield);

        var engine = new FakeGenerationEngine { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Started += _ => started.TrySetResult();
        // 16 GB of diffusion weights and an 18 GB text encoder: streamed to the card with offload they need about 35 GB
        // of RAM, which fits the 40 GB budget only without the director's share.
        var models = new FakeGenerationModels(Path.Combine(root.Path, "models")) { VideoDiffusionBytes = 16 * GiB, VideoEncoderBytes = 18 * GiB };
        await using var service = new GenerationService(new GenerationServiceOptions
        {
            Runtime = runtime,
            Scheduler = scheduler,
            Engine = engine,
            Models = models,
            Output = new GenerationOutputFolder(Path.Combine(root.Path, "generated")),
        });

        var job = await service.CreateAsync(ModelKinds.Video, ("com.mindthaapp.aiportal", ClientPrincipal.LocalDevice),
            new GenerationJobRequest { Prompt = "waves", Frames = 33 }, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(runtime.DirectorYielded);
        Assert.Equal(EngineState.Stopped, chat2.State);
        var budget = runtime.BuildMemoryBudget(await new GenerationProbe().ProbeAsync(default))!;
        var slot = Assert.Single(budget.Engines, x => x.Slot == AiHostRuntime.GenerationSlot);
        Assert.Equal("wan-1.3b", slot.ModelId);
        Assert.True(slot.RamBytes > 30 * GiB);
        Assert.True(engine.Launches.Single().Placement.OffloadToCpu);

        engine.Gate.TrySetResult();
        for (var i = 0; i < 200 && (service.Get(ModelKinds.Video, ("com.mindthaapp.aiportal", ClientPrincipal.LocalDevice), job.Id).State
            != GenerationJobStates.Succeeded || chat2.State != EngineState.Ready); i++)
        {
            await Task.Delay(25);
        }

        Assert.Equal(EngineState.Ready, chat2.State);
        Assert.False(runtime.DirectorYielded);
        Assert.DoesNotContain(runtime.BuildMemoryBudget(await new GenerationProbe().ProbeAsync(default))!.Engines,
            x => x.Slot == AiHostRuntime.GenerationSlot);
    }
}
