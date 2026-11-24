using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Catalogue;
using Portal.AiHost.Core.Runtime;
using Portal.AiHost.Core.Scheduling;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

/// <summary>Performance limits on the fake engine: live apply, priority, the endpoint and the context header.</summary>
public sealed class PerformanceTests
{
    private const long GiB = 1L << 30;

    private sealed class Probe(int cores = 12, long ramGb = 64, long vramGb = 8) : IHardwareProbe
    {
        public Task<HardwareProfile> ProbeAsync(CancellationToken ct) => Task.FromResult(new HardwareProfile("windows", "x64", cores,
            ramGb * GiB, vramGb == 0 ? [] : [new GpuProfile("Test GPU", "amd", "0x1", "1", vramGb * GiB, (long)(vramGb * 0.95 * GiB))], [], "fp"));

        public MemorySnapshot GetMemory() => new(ramGb * GiB, ramGb * GiB * 3 / 4, vramGb == 0 ? [] : [(long)(vramGb * 0.95 * GiB)]);

        public bool IsOnBattery => false;
    }

    private static List<CatalogueEntry> Catalogue()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "catalogue", "catalogue.json");
            if (File.Exists(Path.Combine(dir.FullName, "PortalAIHost.slnx")) && File.Exists(path))
            {
                return CatalogueValidator.Parse(File.ReadAllBytes(path)).Catalogue!.Models;
            }
        }

        throw new InvalidOperationException("catalogue not found");
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly TestRoot _root = new();
        public HostSettings Settings { get; } = new();
        public FakeEngineSupervisor Chat { get; } = new(ModelKinds.Chat);
        public FakeEngineSupervisor Embed { get; } = new(ModelKinds.Embedding);
        public AiHostRuntime Runtime { get; private set; } = null!;
        public CatalogueEntry Model { get; private set; } = null!;

        public static async Task<Rig> StartAsync(string modelId, IHardwareProbe? probe = null, Action<string>? log = null)
        {
            var rig = new Rig();
            var catalogue = Catalogue();
            rig.Model = catalogue.Single(m => m.Id == modelId);
            rig.Runtime = await AiHostRuntime.StartAsync(new AiHostRuntimeOptions
            {
                Registry = new Core.Auth.ClientRegistry(new() { DataDirectory = rig._root.Path }, new TestKeyProtector()),
                Scheduler = new FairScheduler(),
                Supervisors = new Dictionary<string, IEngineSupervisor> { [ModelKinds.Chat] = rig.Chat, [ModelKinds.Embedding] = rig.Embed },
                Hardware = probe ?? new Probe(),
                Settings = () => rig.Settings,
                Catalogue = catalogue,
                Log = log,
            });
            await rig.Runtime.SetActiveAsync(ModelKinds.Chat,
                new ActiveModel(ModelKinds.Chat, rig.Model.Id, rig.Model.DisplayName, ["a.gguf"], rig.Model), default);
            await rig.Runtime.ApplyPerformanceAsync(default);
            return rig;
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            _root.Dispose();
        }
    }

    [Fact]
    public async Task Load_passes_threads_and_the_memory_cap_to_the_engine()
    {
        await using var rig = await Rig.StartAsync("qwen3-8b-q4km");
        rig.Settings.CpuThreads = 7;
        rig.Settings.MemoryLimitBytes = 30 * GiB;
        await rig.Runtime.ApplyPerformanceAsync(default);
        await rig.Runtime.EnsureLoadedAsync(ModelKinds.Chat, default);
        Assert.Equal(7, rig.Chat.Current!.Threads);
        Assert.Equal(33 * GiB, rig.Chat.Current.MemoryLimitBytes); // 30 GB plus the 10 % margin
        Assert.True((await rig.Runtime.BuildPerformanceViewAsync(null, false, default)).Info.MemoryCapActive);
    }

    [Fact]
    public async Task Cap_margin_is_one_gb_or_ten_percent()
    {
        Assert.Equal(6 * GiB, Core.Planning.PerformanceModel.CapFor(5 * GiB));
        Assert.Equal(22 * GiB, Core.Planning.PerformanceModel.CapFor(20 * GiB));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_start_that_fails_under_the_cap_is_retried_once_without_it_and_reported()
    {
        var logs = new List<string>();
        await using var rig = await Rig.StartAsync("qwen3-8b-q4km", log: logs.Add);
        rig.Chat.Options.FailLoadUnderMemoryCap = true;
        await rig.Runtime.EnsureLoadedAsync(ModelKinds.Chat, default);
        Assert.Equal(EngineState.Ready, rig.Chat.State);
        Assert.Null(rig.Chat.Current!.MemoryLimitBytes);
        Assert.Contains(logs, l => l.Contains("without the cap"));
        Assert.DoesNotContain(logs, l => l.Contains("Echo") || l.Contains("hello"));
        var info = (await rig.Runtime.BuildPerformanceViewAsync(null, false, default)).Info;
        Assert.False(info.MemoryCapActive);
        Assert.False(string.IsNullOrEmpty(info.MemoryCapReason));
    }

    [Fact]
    public async Task Priority_low_means_below_normal_and_normal_restores_it()
    {
        await using var rig = await Rig.StartAsync("qwen3-8b-q4km");
        Assert.Equal(ProcessPriorityClass.BelowNormal, rig.Chat.RequestedPriority);
        rig.Settings.Priority = PerformancePriorities.Normal;
        await rig.Runtime.ApplyPerformanceAsync(default);
        Assert.Equal(ProcessPriorityClass.Normal, rig.Chat.RequestedPriority);
        Assert.Equal(ProcessPriorityClass.Normal, rig.Embed.RequestedPriority);
        rig.Settings.Priority = PerformancePriorities.Low;
        await rig.Runtime.ApplyPerformanceAsync(default);
        Assert.Equal(ProcessPriorityClass.BelowNormal, rig.Chat.RequestedPriority);
    }

    [Fact]
    public async Task Processor_choice_forces_zero_gpu_layers_on_a_live_reload()
    {
        await using var rig = await Rig.StartAsync("qwen3-8b-q4km");
        await rig.Runtime.EnsureLoadedAsync(ModelKinds.Chat, default);
        Assert.True(rig.Chat.Current!.Placement.GpuLayers > 0);
        var loads = rig.Chat.LoadCount;

        rig.Settings.Compute = "cpu";
        var applied = await rig.Runtime.ApplyPerformanceAsync(default);
        Assert.Contains(ModelKinds.Chat, applied.Reloaded);
        await Wait(() => rig.Chat.LoadCount > loads && rig.Chat.State == EngineState.Ready);
        Assert.Equal("cpu", rig.Chat.Current!.Placement.Backend);
        Assert.Equal(0, rig.Chat.Current.Placement.GpuLayers);
    }

    [Fact]
    public async Task Graphics_card_choice_falls_back_to_processor_when_there_is_no_card()
    {
        await using var rig = await Rig.StartAsync("qwen3-4b-q4km", new Probe(vramGb: 0));
        rig.Settings.Compute = "gpu";
        await rig.Runtime.ApplyPerformanceAsync(default);
        await rig.Runtime.EnsureLoadedAsync(ModelKinds.Chat, default);
        Assert.Equal("cpu", rig.Chat.Current!.Placement.Backend);
        Assert.Equal("cpu", rig.Runtime.CurrentLimits!.Compute);
    }

    [Fact]
    public async Task A_model_that_no_longer_fits_is_unloaded_and_nothing_is_deleted()
    {
        await using var rig = await Rig.StartAsync("qwen3-30b-a3b-instruct-2507-q4km");
        await rig.Runtime.EnsureLoadedAsync(ModelKinds.Chat, default);
        Assert.Equal(EngineState.Ready, rig.Chat.State);

        rig.Settings.MemoryLimitBytes = 4 * GiB;
        var applied = await rig.Runtime.ApplyPerformanceAsync(default);
        Assert.Contains(ModelKinds.Chat, applied.Unloaded);
        Assert.Equal(EngineState.Stopped, rig.Chat.State);
        // The model is still the active one; the next request reports that it cannot load.
        Assert.NotNull(rig.Runtime.GetActive(ModelKinds.Chat));
        var ex = await Assert.ThrowsAsync<ApiException>(() => rig.Runtime.EnsureLoadedAsync(ModelKinds.Chat, default));
        Assert.Equal(ErrorCodes.InsufficientMemory, ex.Code);
    }

    [Fact]
    public async Task Changing_only_the_priority_does_not_reload()
    {
        await using var rig = await Rig.StartAsync("qwen3-8b-q4km");
        await rig.Runtime.EnsureLoadedAsync(ModelKinds.Chat, default);
        var loads = rig.Chat.LoadCount;
        rig.Settings.Priority = PerformancePriorities.Normal;
        var applied = await rig.Runtime.ApplyPerformanceAsync(default);
        Assert.Empty(applied.Reloaded);
        Assert.Empty(applied.Unloaded);
        Assert.Equal(loads, rig.Chat.LoadCount);
    }

    [Fact]
    public async Task Performance_view_reports_fit_stops_and_ranges()
    {
        await using var rig = await Rig.StartAsync("qwen3-8b-q4km");
        var view = await rig.Runtime.BuildPerformanceViewAsync(null, withStops: true, default);
        Assert.Equal("balanced", view.Info.Preset);
        Assert.Equal(PerformanceFits.FitsFully, view.Info.Fit);
        Assert.Equal("gpu", view.Info.ComputeEffective);
        Assert.True(view.Info.Ranges.GpuAvailable);
        Assert.Equal(11, view.Info.Ranges.CpuThreads.Max);
        Assert.Equal(2 * GiB, view.MemoryStops[0].Bytes);
        Assert.Equal(56, view.MemoryStops.Count - 1 + 2);   // 2 GB up to RAM minus 8 GB
        Assert.Equal(rig.Model.DisplayName, view.FitModel);

        // A candidate previews without saving.
        var preview = await rig.Runtime.BuildPerformanceViewAsync(
            new HostSettings { MemoryLimitBytes = 5 * GiB, Compute = "cpu" }, withStops: false, default);
        Assert.Equal(PerformanceFits.NeedsSmallerModel, preview.Info.Fit);
        Assert.NotNull(preview.SmallerModel);
        Assert.Equal("auto", rig.Settings.Compute);
    }

    [Fact]
    public async Task Http_endpoint_needs_a_token_and_is_listed_in_capabilities()
    {
        await using var host = await TestHost.StartAsync();
        using var anonymous = await host.Http.GetAsync(Routes.Performance);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var token = await host.GetTokenAsync();
        using var caps = await host.Http.SendAsync(host.Authed(HttpMethod.Get, Routes.Capabilities, token));
        var endpoints = JsonDocument.Parse(await caps.Content.ReadAsStringAsync()).RootElement.GetProperty("endpoints")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("performance", endpoints);

        using var perf = await host.Http.SendAsync(host.Authed(HttpMethod.Get, Routes.Performance, token));
        Assert.Equal(HttpStatusCode.OK, perf.StatusCode);
        var root = JsonDocument.Parse(await perf.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("balanced", root.GetProperty("preset").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("eased_off_now").ValueKind);
        Assert.Equal(100, root.GetProperty("cpu_ceiling_percent").GetInt32());
        Assert.Equal("portal-ai-host:settings/performance", root.GetProperty("settings_link").GetString());
        Assert.True(root.GetProperty("ranges").GetProperty("memory_limit_bytes").GetProperty("max").GetInt64()
            > root.GetProperty("ranges").GetProperty("memory_limit_bytes").GetProperty("min").GetInt64());
        Assert.True(root.GetProperty("cpu_threads").GetInt32() >= 1);
    }

    [Fact]
    public async Task Context_header_caps_the_reply_and_never_errors()
    {
        await using var host = await TestHost.StartAsync();
        var token = await host.GetTokenAsync();

        async Task<(HttpResponseMessage Response, ChatCompletionRequest? Seen)> Chat(string? header, string body)
        {
            var request = host.Authed(HttpMethod.Post, Routes.ChatCompletions, token, body);
            if (header is not null)
            {
                request.Headers.TryAddWithoutValidation(HeaderNames.ContextTokens, header);
            }

            var response = await host.Http.SendAsync(request);
            return (response, host.Chat.LastChatRequest);
        }

        const string small = """{ "messages": [ { "role": "user", "content": "hello" } ] }""";

        // Without the header nothing is changed.
        var (plain, seenPlain) = await Chat(null, small);
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Null(seenPlain!.MaxTokens);

        // A small context caps the reply.
        var (capped, seenCapped) = await Chat("1000", small);
        Assert.Equal(HttpStatusCode.OK, capped.StatusCode);
        Assert.Equal("1000", capped.Headers.GetValues(HeaderNames.ContextTokens).Single());
        Assert.InRange(seenCapped!.MaxTokens!.Value, 900, 1000);

        // The caller's own smaller max_tokens stays; a bigger one is lowered.
        var (own, seenOwn) = await Chat("1000", """{ "max_tokens": 50, "messages": [ { "role": "user", "content": "hello" } ] }""");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(50, seenOwn!.MaxTokens);
        var (big, seenBig) = await Chat("1000", """{ "max_tokens": 5000, "messages": [ { "role": "user", "content": "hello" } ] }""");
        Assert.Equal(HttpStatusCode.OK, big.StatusCode);
        Assert.InRange(seenBig!.MaxTokens!.Value, 900, 1000);

        // Values out of range are clamped, junk is ignored; neither is an error.
        var (low, seenLow) = await Chat("1", small);
        Assert.Equal(HttpStatusCode.OK, low.StatusCode);
        Assert.Equal("512", low.Headers.GetValues(HeaderNames.ContextTokens).Single());
        Assert.InRange(seenLow!.MaxTokens!.Value, 400, 512);
        var (high, _) = await Chat("999999999", small);
        Assert.Equal(HttpStatusCode.OK, high.StatusCode);
        Assert.Equal("32768", high.Headers.GetValues(HeaderNames.ContextTokens).Single());
        var (junk, seenJunk) = await Chat("lots", small);
        Assert.Equal(HttpStatusCode.OK, junk.StatusCode);
        Assert.Null(seenJunk!.MaxTokens);

        // A long prompt leaves at least a short reply.
        var longPrompt = new string('x', 6000);
        var (tight, seenTight) = await Chat("512", $$"""{ "messages": [ { "role": "user", "content": "{{longPrompt}}" } ] }""");
        Assert.Equal(HttpStatusCode.OK, tight.StatusCode);
        Assert.Equal(16, seenTight!.MaxTokens);
    }

    private static async Task Wait(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(done(), "timed out");
    }
}
