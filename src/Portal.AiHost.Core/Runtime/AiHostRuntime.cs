using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Api;
using Portal.AiHost.Core.Catalogue;
using Portal.AiHost.Core.Planning;

namespace Portal.AiHost.Core.Runtime;

/// <summary>The model that serves one kind (chat, embedding). Set by the composition root or the admin pipe.</summary>
public sealed record ActiveModel(
    string Kind,
    string ModelId,
    string DisplayName,
    IReadOnlyList<string> Files,
    CatalogueEntry? Entry = null,
    int ContextTokens = 4096,
    int ParallelSlots = 1,
    bool SupportsTools = false,
    bool SupportsReasoning = false,
    int EmbeddingDimensions = 0);

public sealed class AiHostRuntimeOptions
{
    public required IClientRegistry Registry { get; init; }

    public required IScheduler Scheduler { get; init; }

    /// <summary>One supervisor per model kind.</summary>
    public required IReadOnlyDictionary<string, IEngineSupervisor> Supervisors { get; init; }

    /// <summary>Optional: when set, loads are planned against free memory minus what other engines hold.</summary>
    public IHardwareProbe? Hardware { get; init; }

    public PlannerSettings Planner { get; init; } = new();

    /// <summary>
    /// The saved settings. When set, the performance limits (memory, graphics memory, threads, priority) come from
    /// them and <see cref="AiHostRuntime.ApplyPerformanceAsync"/> re-reads them.
    /// </summary>
    public Func<HostSettings>? Settings { get; init; }

    /// <summary>Catalogue entries, for the fit preview and the memory slider labels.</summary>
    public IReadOnlyList<CatalogueEntry>? Catalogue { get; init; }

    /// <summary>Host log line sink (never any prompt or reply text).</summary>
    public Action<string>? Log { get; init; }

    public string HostVersion { get; init; } = "1.0.0";

    public string InstanceId { get; init; } = Guid.NewGuid().ToString("N");

    public ApiPipelineOptions Pipeline { get; init; } = new();

    /// <summary>How often a waiting stream gets a ": portal" comment (api-v1 says at least every 5 s).</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(ApiLimits.ProgressCommentIntervalSeconds);

    public int MaxBatchInputs { get; init; } = 64;

    public int MaxInputTokens { get; init; } = 512;

    public int MaxOutputTokens { get; init; } = 8192;

    public bool SafeMode { get; init; }

    /// <summary>Per kind: keep loaded, and the idle unload timeout in seconds now in force (status reports it).</summary>
    public Func<string, (bool KeepLoaded, int IdleUnloadSeconds)>? IdlePolicy { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Reads engine process memory and processor time for status usage. Null: no usage in status.</summary>
    public IProcessUsageProbe? UsageProbe { get; init; }
}

/// <summary>
/// Composition entry for the host: owns the API pipeline, the engines and the load decisions. Transports
/// (Kestrel, later LAN and relay) feed requests into <see cref="Pipeline"/>.
/// </summary>
public sealed partial class AiHostRuntime : IAsyncDisposable
{
    private readonly AiHostRuntimeOptions _o;
    private readonly ConcurrentDictionary<string, ActiveModel> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, KindState> _kinds = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _resumeGate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _keepLoaded = new(StringComparer.Ordinal);
    private readonly UsageSampler? _sampler;
    private long _revision = 1;
    private int _inFlight;
    private volatile bool _stopping;

    private AiHostRuntime(AiHostRuntimeOptions options)
    {
        _o = options;
        SafeMode = options.SafeMode;
        Pipeline = new ApiPipeline(options.Registry, options.Pipeline, options.Time);
        Pipeline.HostStopping = _lifetime.Token;
        Activity = new ActivityTracker(options.Time);
        if (options.UsageProbe is { } probe)
        {
            _sampler = new UsageSampler(() => options.Supervisors.Values.OfType<IEngineProcess>()
                .Select(s => s.ProcessId).Where(p => p.HasValue).Select(p => p!.Value).ToList(), probe, options.Time);
        }
        foreach (var (kind, sup) in options.Supervisors)
        {
            _kinds[kind] = new KindState();
            sup.StateChanged += (_, change) =>
            {
                if (change.State is EngineState.Ready or EngineState.Failed or EngineState.Stopped)
                {
                    Interlocked.Increment(ref _revision);
                }

                EngineStateChanged?.Invoke(this, change);
            };
        }

        MapEndpoints();
    }

    public ApiPipeline Pipeline { get; }

    /// <summary>Per-app counts for today (memory only).</summary>
    public ActivityTracker Activity { get; }

    /// <summary>
    /// Stops the engine of a kind now. Only the engine process ends: model files are never touched. A later
    /// request loads it again.
    /// </summary>
    public async Task UnloadAsync(string kind, CancellationToken ct)
    {
        var (_, sup) = Require(kind, duringMaintenance: true);
        var ks = _kinds[kind];
        Task? pending;
        lock (ks)
        {
            pending = ks.Load is { IsCompleted: false } l ? l : null;
        }

        if (pending is not null)
        {
            try
            {
                await pending.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed load leaves nothing to wait for.
            }
        }

        await sup.StopAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _revision);
    }

    public string InstanceId => _o.InstanceId;

    public string HostVersion => _o.HostVersion;

    public bool SafeMode { get; }

    public bool Stopping => _stopping;

    public long CapabilitiesRevision => Interlocked.Read(ref _revision);

    public IClientRegistry Registry => _o.Registry;

    public int InFlightRequests => Volatile.Read(ref _inFlight);

    public event EventHandler<EngineStateChange>? EngineStateChanged;

    public static Task<AiHostRuntime> StartAsync(AiHostRuntimeOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new AiHostRuntime(options));
    }

    public ActiveModel? GetActive(string kind) => _active.TryGetValue(kind, out var m) ? m : null;

    /// <summary>Sets (or clears) the model for a kind. A loaded engine with another model is stopped.</summary>
    public async Task SetActiveAsync(string kind, ActiveModel? model, CancellationToken ct)
    {
        var old = GetActive(kind);
        if (model is null)
        {
            _active.TryRemove(kind, out _);
        }
        else
        {
            _active[kind] = model with { Kind = kind };
        }

        Interlocked.Increment(ref _revision);
        if (old is not null && old.ModelId != model?.ModelId && _o.Supervisors.TryGetValue(kind, out var sup) &&
            sup.State is not EngineState.Stopped)
        {
            await sup.StopAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Loads every kind marked keep-loaded, unless the host is in safe mode.</summary>
    public async Task AutoLoadAsync(IEnumerable<string> keepLoadedKinds, CancellationToken ct)
    {
        keepLoadedKinds = keepLoadedKinds.ToList();
        foreach (var kind in keepLoadedKinds)
        {
            _keepLoaded[kind] = 0;
        }

        if (SafeMode)
        {
            return;
        }

        foreach (var kind in keepLoadedKinds)
        {
            if (GetActive(kind) is not null)
            {
                try
                {
                    await EnsureLoadedAsync(kind, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ApiException or EngineException or EngineCrashedException)
                {
                    // Reported through engine state; requests retry the load.
                }
            }
        }
    }

    /// <summary>After sleep or resume: restart engines that are no longer healthy.</summary>
    public async Task CheckEnginesAfterResumeAsync(CancellationToken ct)
    {
        // Windows sends more than one resume message per wake; one check at a time, overlapping ones are skipped.
        if (!await _resumeGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            foreach (var (kind, sup) in _o.Supervisors)
            {
                if (GetActive(kind) is null)
                {
                    continue;
                }

                var reload = false;
                if (sup.State is EngineState.Failed)
                {
                    await sup.StopAsync(ct).ConfigureAwait(false);
                    reload = _keepLoaded.ContainsKey(kind);
                }
                else if (sup.State is EngineState.Ready)
                {
                    try
                    {
                        using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        probe.CancelAfter(TimeSpan.FromSeconds(10));
                        await sup.CountTokensAsync(new TokenizeRequest { Text = "ping" }, probe.Token).ConfigureAwait(false);
                        continue;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        await sup.StopAsync(ct).ConfigureAwait(false);
                    }

                    reload = true;
                }

                if (reload)
                {
                    try
                    {
                        await EnsureLoadedAsync(kind, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is ApiException or EngineException or EngineCrashedException)
                    {
                        // The next request retries.
                    }
                }
            }
        }
        finally
        {
            _resumeGate.Release();
        }
    }

    /// <summary>Quit: refuse new work, give running requests a grace period, then stop every engine.</summary>
    public async Task StopAsync(TimeSpan grace, CancellationToken ct = default)
    {
        _stopping = true;
        var until = _o.Time.GetUtcNow() + grace;
        while (InFlightRequests > 0 && _o.Time.GetUtcNow() < until && !ct.IsCancellationRequested)
        {
            await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        foreach (var sup in _o.Supervisors.Values)
        {
            try
            {
                await sup.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is EngineException or InvalidOperationException or IOException)
            {
                // Stopping is best effort; the Job Object ends the child with the host.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_lifetime.IsCancellationRequested)
        {
            await StopAsync(TimeSpan.Zero).ConfigureAwait(false);
        }

        foreach (var sup in _o.Supervisors.Values)
        {
            await sup.DisposeAsync().ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }

    // ---- status --------------------------------------------------------------------------------

    public HostStatus BuildStatus(ClientPrincipal? principal, HardwareProfile? hardware = null)
    {
        var snap = _o.Scheduler.Snapshot();
        var models = new Dictionary<string, ModelStatus>(StringComparer.Ordinal);
        var anyActive = false;
        var anyLoading = false;
        var anyReady = false;
        var anyFailed = false;
        foreach (var (kind, sup) in _o.Supervisors)
        {
            var active = GetActive(kind);
            var ks = _kinds[kind];
            var idle = _o.IdlePolicy?.Invoke(kind) ?? (false, 600);
            anyActive |= active is not null;
            var state = active is null ? ModelStates.NotInstalled : sup.State switch
            {
                EngineState.Ready => ModelStates.Loaded,
                EngineState.Starting or EngineState.Loading => ModelStates.Loading,
                EngineState.Failed => ModelStates.Failed,
                _ => ModelStates.Unloaded,
            };
            anyLoading |= state == ModelStates.Loading;
            anyReady |= state == ModelStates.Loaded;
            anyFailed |= state == ModelStates.Failed;
            models[kind] = new ModelStatus
            {
                Id = active?.ModelId,
                DisplayName = active?.DisplayName,
                State = state,
                LoadedSince = state == ModelStates.Loaded ? ks.LoadedSince : null,
                LoadProgress = state == ModelStates.Loaded ? 1.0 : state == ModelStates.Loading ? ks.Progress : 0.0,
                KeepLoaded = idle.KeepLoaded,
                IdleUnloadSeconds = idle.KeepLoaded ? 0 : idle.IdleUnloadSeconds,
            };
        }

        var hostState = !anyActive ? HostStates.NoModel
            : snap.Running > 0 ? HostStates.Busy
            : anyLoading ? HostStates.Loading
            : anyReady ? HostStates.Ready
            : anyFailed ? HostStates.Error
            : HostStates.Idle;
        var yours = principal is not null && snap.WaitingByApp.TryGetValue(principal.AppId, out var n) ? n : 0;
        return new HostStatus
        {
            HostVersion = _o.HostVersion,
            InstanceId = _o.InstanceId,
            State = hostState,
            Client = principal is null ? null : new StatusClient { AppId = principal.AppId, TokenId = principal.TokenId },
            Models = models,
            Usage = _sampler?.Get(Activity.LastTokensPerSecond),
            Queue = new QueueStatus
            {
                Running = snap.Running,
                Waiting = snap.Waiting,
                WaitingInteractive = snap.WaitingInteractive,
                WaitingBatch = snap.WaitingBatch,
                YoursWaiting = yours,
            },
            Hardware = hardware is null ? new HardwareInfo { CpuCores = Environment.ProcessorCount } : new HardwareInfo
            {
                Os = hardware.Os,
                Arch = hardware.Arch,
                CpuCores = hardware.CpuCores,
                RamBytes = hardware.RamBytes,
                Gpus = hardware.Gpus.Select(g => new GpuInfo
                {
                    Name = g.Name, Vendor = g.Vendor, VramBytes = g.VramBytes, Backend = "vulkan",
                }).ToList(),
            },
        };
    }

    public HostCapabilities BuildCapabilities()
    {
        var chat = GetActive(ModelKinds.Chat);
        var emb = GetActive(ModelKinds.Embedding);
        var chatSup = Supervisor(ModelKinds.Chat);
        var placement = chatSup?.Current?.Placement;
        return new HostCapabilities
        {
            CapabilitiesRevision = CapabilitiesRevision,
            Chat = chat is null || chatSup is null
                ? new ChatCapabilities { Available = false, Reason = UnavailableReasons.NoModel }
                : new ChatCapabilities
                {
                    Available = true,
                    Model = new ChatModelInfo
                    {
                        Id = chat.ModelId,
                        DisplayName = chat.DisplayName,
                        Family = chat.Entry?.Family,
                        ParamsTotalB = chat.Entry?.ParamsTotalB,
                        ParamsActiveB = chat.Entry?.ParamsActiveB,
                        Quant = chat.Entry?.Quant,
                        SizeBytes = chat.Entry?.TotalBytes,
                    },
                    ContextTokens = (placement?.ContextTokens ?? chat.ContextTokens) /
                        Math.Max(1, placement?.ParallelSlots ?? chat.ParallelSlots),
                    ContextTokensMax = chat.Entry?.ContextMax is > 0 ? chat.Entry.ContextMax : chat.ContextTokens,
                    MaxOutputTokens = _o.MaxOutputTokens,
                    ParallelSlots = placement?.ParallelSlots ?? chat.ParallelSlots,
                    Features = new ChatFeatures
                    {
                        Streaming = true, SystemPrompt = true, JsonObject = true, JsonSchema = true,
                        Tools = chat.SupportsTools, Reasoning = chat.SupportsReasoning,
                    },
                    Placement = placement is null ? null : new PlacementInfo
                    {
                        Backend = placement.Backend,
                        GpuOffload = placement.GpuLayers <= 0 ? "none"
                            : placement.GpuLayers >= placement.TotalLayers ? "full" : "partial",
                        GpuLayers = placement.GpuLayers,
                        TotalLayers = placement.TotalLayers,
                        MoeExpertLayersOnCpu = placement.CpuMoeLayers,
                    },
                },
            Embeddings = emb is null || Supervisor(ModelKinds.Embedding) is null
                ? new EmbeddingCapabilities { Available = false, Reason = UnavailableReasons.NoModel }
                : new EmbeddingCapabilities
                {
                    Available = true,
                    Model = new EmbeddingModelInfo { Id = emb.ModelId, DisplayName = emb.DisplayName },
                    Dimensions = emb.EmbeddingDimensions,
                    MaxInputTokens = _o.MaxInputTokens,
                    MaxBatchInputs = _o.MaxBatchInputs,
                    Normalized = true,
                },
            Limits = new CapabilityLimits { MaxRequestBytes = _o.Pipeline.MaxRequestBytes },
            Endpoints =
            [
                EndpointNames.ChatCompletions, EndpointNames.Embeddings, EndpointNames.Models,
                EndpointNames.Tokenize, EndpointNames.Warmup, EndpointNames.Performance,
            ],
        };
    }

    // ---- loading -------------------------------------------------------------------------------

    private IEngineSupervisor? Supervisor(string kind) => _o.Supervisors.TryGetValue(kind, out var s) ? s : null;

    private volatile string? _maintenance;

    /// <summary>
    /// While set (for example "Your models are being moved"), requests that need a model fail with that message
    /// instead of loading an engine from files that are being copied. Cleared when the work ends.
    /// </summary>
    public string? MaintenanceMessage
    {
        get => _maintenance;
        set => _maintenance = value;
    }

    private (ActiveModel Model, IEngineSupervisor Sup) Require(string kind, bool duringMaintenance = false)
    {
        if (_stopping)
        {
            throw new ApiException(ErrorCodes.HostStopping, "Portal AI Host is quitting.");
        }

        if (_maintenance is { } busy && !duringMaintenance)
        {
            throw new ApiException(ErrorCodes.EngineRestarting, busy, retryAfterMs: 5000);
        }

        var model = GetActive(kind);
        var sup = Supervisor(kind);
        if (model is null || sup is null)
        {
            throw new ApiException(ErrorCodes.NoModel, "No model of the needed kind is installed.")
            {
                Details = new() { ["kind"] = JsonSerializer.SerializeToElement(kind, AiHostJsonContext.Default.String) },
            };
        }

        return (model, sup);
    }

    private static bool IsLoaded(IEngineSupervisor sup, ActiveModel model) =>
        sup.State == EngineState.Ready && sup.Current?.ModelId == model.ModelId;

    /// <summary>
    /// Memory held by engines of other kinds, so a second model is planned against what is really left
    /// (WP3 planner, <see cref="CommittedMemory"/>).
    /// </summary>
    public CommittedMemory CommittedExcept(string kind)
    {
        long ram = 0;
        var vram = new List<long>();
        foreach (var (k, ks) in _kinds)
        {
            if (k == kind || ks.LastPlan is not { } plan || Supervisor(k)?.State is EngineState.Stopped or null)
            {
                continue;
            }

            ram += plan.CpuBytes;
            if (vram.Count == 0)
            {
                vram.Add(0);
            }

            vram[0] += plan.GpuBytes;
        }

        return ram == 0 && vram.Count == 0 ? CommittedMemory.None : new CommittedMemory(ram, vram);
    }

    /// <summary>Decides the placement for a load: planned when the hardware and catalogue entry are known.</summary>
    public async Task<(Placement Placement, PlanResult? Plan)> DecidePlacementAsync(ActiveModel model, CancellationToken ct)
    {
        if (_o.Hardware is null || model.Entry is null)
        {
            return (new Placement { ContextTokens = model.ContextTokens, ParallelSlots = model.ParallelSlots }, null);
        }

        var hw = await _o.Hardware.ProbeAsync(ct).ConfigureAwait(false);
        var plan = PlacementPlanner.Plan(model.Entry, hw, _o.Hardware.GetMemory(), CurrentPlanner,
            CommittedExcept(model.Kind));
        if (!plan.Fits || plan.Placement is null)
        {
            throw new ApiException(ErrorCodes.InsufficientMemory,
                "The model cannot load in the memory available now.");
        }

        return (plan.Placement, plan);
    }

    public Task EnsureLoadedAsync(string kind, CancellationToken ct)
    {
        var (model, sup) = Require(kind);
        if (IsLoaded(sup, model))
        {
            return Task.CompletedTask;
        }

        var ks = _kinds[kind];
        Task load;
        lock (ks)
        {
            if (ks.Load is null || ks.Load.IsCompleted || ks.LoadModelId != model.ModelId)
            {
                ks.LoadModelId = model.ModelId;
                ks.Load = LoadCoreAsync(kind, model, sup, ks);
            }

            load = ks.Load;
        }

        return load.WaitAsync(ct);
    }

    private async Task LoadCoreAsync(string kind, ActiveModel model, IEngineSupervisor sup, KindState ks)
    {
        await Task.Yield();
        var (placement, plan) = await DecidePlacementAsync(model, _lifetime.Token).ConfigureAwait(false);
        void OnState(object? s, EngineStateChange c)
        {
            if (c.State == EngineState.Loading)
            {
                ks.Progress = c.Progress;
            }
        }

        sup.StateChanged += OnState;
        try
        {
            ks.Progress = 0;
            await StartWithCapFallbackAsync(sup, new EngineLaunchSpec
            {
                Kind = kind,
                ModelId = model.ModelId,
                DisplayName = model.DisplayName,
                ModelFiles = model.Files,
                Placement = placement,
                SupportsTools = model.SupportsTools,
                SupportsReasoning = model.SupportsReasoning,
                Threads = _limits?.CpuThreads,
                MemoryLimitBytes = _limits is { } lim ? PerformanceModel.CapFor(lim.MemoryLimitBytes) : null,
            }, kind).ConfigureAwait(false);
            ks.LastPlan = plan;
            ks.LoadedSince = _o.Time.GetUtcNow();
            if (kind == ModelKinds.Chat)
            {
                _o.Scheduler.SetSlots(kind, Math.Max(1, placement.ParallelSlots));
            }

            Interlocked.Increment(ref _revision);
        }
        finally
        {
            sup.StateChanged -= OnState;
        }
    }

    /// <summary>App id the window's "Try it" runs under in the fair queue.</summary>
    public const string TryItAppId = "host.tryit";

    /// <summary>"Try it" from the window (admin pipe): one non-streaming chat through the same queue and engine.</summary>
    public async Task<ChatCompletionResponse> TryItChatAsync(ChatCompletionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequestValidator.ValidateChat(request);
        var (active, sup) = Require(ModelKinds.Chat);
        request = ForTryIt(request, active.SupportsReasoning);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        linked.CancelAfter(TimeSpan.FromMilliseconds(ApiLimits.DefaultInteractiveDeadlineMs));
        Interlocked.Increment(ref _inFlight);
        try
        {
            await using var lease = await _o.Scheduler.AcquireAsync(new SchedulingRequest
            {
                AppId = TryItAppId,
                Kind = ModelKinds.Chat,
                Deadline = _o.Time.GetUtcNow().AddMilliseconds(ApiLimits.DefaultInteractiveDeadlineMs),
            }, null, linked.Token).ConfigureAwait(false);
            await EnsureLoadedAsync(ModelKinds.Chat, linked.Token).ConfigureAwait(false);
            return await sup.ChatAsync(request, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>Longest reply "Try it" asks for, so a model that rambles or thinks aloud cannot run for minutes.</summary>
    public const int TryItMaxTokens = 768;

    private static readonly JsonElement DisableThinking = JsonDocument.Parse("{\"enable_thinking\":false}").RootElement.Clone();

    private static ChatCompletionRequest ForTryIt(ChatCompletionRequest request, bool supportsReasoning) => new()
    {
        Model = request.Model,
        Messages = request.Messages,
        Stream = false,
        MaxTokens = request.MaxTokens ?? request.MaxCompletionTokens ?? TryItMaxTokens,
        Temperature = request.Temperature,
        TopP = request.TopP,
        Seed = request.Seed,
        // A quick check: a thinking model answers straight away instead of reasoning first.
        ChatTemplateKwargs = request.ChatTemplateKwargs ?? (supportsReasoning
            ? DisableThinking
            : null),
    };

    // ---- endpoints -----------------------------------------------------------------------------

    private void MapEndpoints()
    {
        var p = Pipeline;
        p.Map("GET", Routes.Ping, ctx => ctx.WriteJsonAsync(200, new PingResponse
        {
            HostVersion = _o.HostVersion, InstanceId = _o.InstanceId,
        }, AiHostJsonContext.Default.PingResponse), requiresAuth: false);
        p.Map("GET", Routes.Status, ctx => Tracked(ctx, StatusAsync));
        p.Map("GET", Routes.Capabilities, ctx => Tracked(ctx, c =>
            c.WriteJsonAsync(200, BuildCapabilities(), AiHostJsonContext.Default.HostCapabilities)));
        p.Map("GET", Routes.Models, ctx => Tracked(ctx, ListModelsAsync));
        p.Map("GET", Routes.Performance, ctx => Tracked(ctx, c =>
            PerformanceEndpointAsync(c)));
        p.Map("POST", Routes.Warmup, ctx => Tracked(ctx, WarmupAsync));
        p.Map("POST", Routes.Tokenize, ctx => Tracked(ctx, TokenizeAsync));
        p.Map("POST", Routes.ChatCompletions, ctx => Tracked(ctx, ChatAsync));
        p.Map("POST", Routes.Embeddings, ctx => Tracked(ctx, EmbeddingsAsync));
    }

    private async Task Tracked(ApiContext ctx, Func<ApiContext, Task> handler)
    {
        Interlocked.Increment(ref _inFlight);
        try
        {
            ctx.ResponseHeaders[HeaderNames.CapabilitiesRevision] =
                CapabilitiesRevision.ToString(CultureInfo.InvariantCulture);
            await handler(ctx).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private async Task StatusAsync(ApiContext ctx)
    {
        var status = BuildStatus(ctx.Principal);
        if (ctx.Principal is { } principal && status.Client is { } client)
        {
            var clients = await _o.Registry.ListAsync(ctx.Aborted).ConfigureAwait(false);
            if (clients.FirstOrDefault(c => c.TokenId == principal.TokenId) is { } registered)
            {
                status = CopyWithClient(status, new StatusClient
                {
                    AppId = client.AppId, TokenId = client.TokenId, ApprovedAt = registered.CreatedAt,
                });
            }
        }

        await ctx.WriteJsonAsync(200, status, AiHostJsonContext.Default.HostStatus).ConfigureAwait(false);
    }

    private static HostStatus CopyWithClient(HostStatus s, StatusClient client) => new()
    {
        ApiVersion = s.ApiVersion, HostVersion = s.HostVersion, InstanceId = s.InstanceId, State = s.State,
        Client = client, Models = s.Models, Queue = s.Queue, Hardware = s.Hardware,
        UpgradeAvailable = s.UpgradeAvailable, Sharing = s.Sharing, Usage = s.Usage,
    };

    private Task ListModelsAsync(ApiContext ctx)
    {
        var list = new ModelList();
        foreach (var (kind, sup) in _o.Supervisors)
        {
            if (GetActive(kind) is { } m)
            {
                list.Data.Add(new ModelEntry
                {
                    Id = m.ModelId,
                    Kind = kind,
                    Active = true,
                    Loaded = IsLoaded(sup, m),
                    ContextTokens = kind == ModelKinds.Chat ? BuildCapabilities().Chat.ContextTokens : null,
                });
            }
        }

        return ctx.WriteJsonAsync(200, list, AiHostJsonContext.Default.ModelList);
    }

    private async Task WarmupAsync(ApiContext ctx)
    {
        var body = ctx.ReadJson(AiHostJsonContext.Default.WarmupRequest);
        if (body.Kind is not (ModelKinds.Chat or ModelKinds.Embedding))
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "kind must be chat or embedding.", "kind");
        }

        var (model, sup) = Require(body.Kind);
        if (IsLoaded(sup, model))
        {
            await ctx.WriteJsonAsync(200, new WarmupResponse { State = ModelStates.Loaded },
                AiHostJsonContext.Default.WarmupResponse).ConfigureAwait(false);
            return;
        }

        _ = EnsureLoadedAsync(body.Kind, _lifetime.Token).ContinueWith(static t => _ = t.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        await ctx.WriteJsonAsync(202, new WarmupResponse { State = ModelStates.Loading, LoadProgress = _kinds[body.Kind].Progress },
            AiHostJsonContext.Default.WarmupResponse).ConfigureAwait(false);
    }

    private async Task TokenizeAsync(ApiContext ctx)
    {
        var body = ctx.ReadJson(AiHostJsonContext.Default.TokenizeRequest);
        if (body.Messages is null && body.Text is null)
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "Send either messages or text.", "text");
        }

        var (model, sup) = Require(ModelKinds.Chat);
        using var deadline = DeadlineToken(ctx);
        await LoadForRequestAsync(ctx, ModelKinds.Chat, deadline.Token, null).ConfigureAwait(false);
        var tokens = await sup.CountTokensAsync(body, deadline.Token).ConfigureAwait(false);
        ctx.ResponseHeaders[HeaderNames.Model] = model.ModelId;
        await ctx.WriteJsonAsync(200, new TokenizeResponse
        {
            Tokens = tokens, ContextTokens = BuildCapabilities().Chat.ContextTokens ?? model.ContextTokens,
        }, AiHostJsonContext.Default.TokenizeResponse).ConfigureAwait(false);
    }

    private async Task EmbeddingsAsync(ApiContext ctx)
    {
        var body = ctx.ReadJson(AiHostJsonContext.Default.EmbeddingsRequest);
        RequestValidator.ValidateEmbeddings(body, _o.MaxBatchInputs);
        var (model, sup) = Require(ModelKinds.Embedding);
        ctx.ResponseHeaders[HeaderNames.Model] = model.ModelId;
        using var deadline = DeadlineToken(ctx);
        using var activity = BeginActivity(ctx);
        await using var lease = await AcquireAsync(ctx, ModelKinds.Embedding, deadline.Token, null).ConfigureAwait(false);
        ctx.SetQueueWait(lease.QueueWait);
        await LoadForRequestAsync(ctx, ModelKinds.Embedding, deadline.Token, null).ConfigureAwait(false);
        activity?.StartGeneration();
        var result = await Generate(ctx, deadline, () => sup.EmbedAsync(body, deadline.Token)).ConfigureAwait(false);
        await ctx.WriteJsonAsync(200, result, AiHostJsonContext.Default.EmbeddingsResponse).ConfigureAwait(false);
    }

    private async Task ChatAsync(ApiContext ctx)
    {
        var body = ctx.ReadJson(AiHostJsonContext.Default.ChatCompletionRequest);
        RequestValidator.ValidateChat(body);
        var (model, sup) = Require(ModelKinds.Chat);
        ApplyContextLimit(ctx, body, model);
        ctx.ResponseHeaders[HeaderNames.Model] = model.ModelId;
        using var deadline = DeadlineToken(ctx);
        using var activity = BeginActivity(ctx);

        if (ctx.Options.FailIfLoading && !IsLoaded(sup, model))
        {
            _ = EnsureLoadedAsync(ModelKinds.Chat, _lifetime.Token).ContinueWith(static t => _ = t.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw new ApiException(ErrorCodes.ModelLoading, "The model is loading. Try again shortly.", retryAfterMs: 3000)
            {
                Details = new()
                {
                    ["progress"] = JsonSerializer.SerializeToElement(_kinds[ModelKinds.Chat].Progress,
                        AiHostJsonContext.Default.Double),
                },
            };
        }

        if (body.Stream != true)
        {
            await using var lease = await AcquireAsync(ctx, ModelKinds.Chat, deadline.Token, null).ConfigureAwait(false);
            ctx.SetQueueWait(lease.QueueWait);
            await LoadForRequestAsync(ctx, ModelKinds.Chat, deadline.Token, null).ConfigureAwait(false);
            activity?.StartGeneration();
            var result = await Generate(ctx, deadline, () => sup.ChatAsync(body, deadline.Token)).ConfigureAwait(false);
            activity?.AddTokens(result.Usage?.CompletionTokens ?? 0);
            await ctx.WriteJsonAsync(200, result, AiHostJsonContext.Default.ChatCompletionResponse).ConfigureAwait(false);
            return;
        }

        // Streaming: headers go out as soon as the request is accepted; later failures are in-stream errors.
        await ctx.StartStreamAsync().ConfigureAwait(false);
        await using var pump = new ProgressPump(ctx, _o.ProgressInterval);
        await using (var lease = await AcquireAsync(ctx, ModelKinds.Chat, deadline.Token, pump).ConfigureAwait(false))
        {
            await LoadForRequestAsync(ctx, ModelKinds.Chat, deadline.Token, pump).ConfigureAwait(false);
            await pump.StopAsync(new PortalProgressEvent { Event = ProgressEvents.Started }).ConfigureAwait(false);
            activity?.StartGeneration();
            long usageTokens = 0, streamed = 0;
            try
            {
                await foreach (var chunk in sup.StreamChatAsync(body, deadline.Token).ConfigureAwait(false))
                {
                    await ctx.WriteSseDataAsync(chunk, AiHostJsonContext.Default.ChatCompletionChunk).ConfigureAwait(false);
                    if (chunk.Usage is { CompletionTokens: > 0 } u)
                    {
                        usageTokens = u.CompletionTokens;
                    }
                    else if (chunk.Choices.Any(c => !string.IsNullOrEmpty(c.Delta.Content) || !string.IsNullOrEmpty(c.Delta.ReasoningContent)))
                    {
                        streamed++;
                    }
                }
            }
            catch (OperationCanceledException) when (deadline.IsDeadline)
            {
                throw Deadline(DeadlinePhases.Generating);
            }
            finally
            {
                activity?.AddTokens(usageTokens > 0 ? usageTokens : streamed);
            }
        }

        await ctx.WriteSseDoneAsync().ConfigureAwait(false);
        await ctx.CompleteAsync().ConfigureAwait(false);
    }

    private ActivityTracker.RequestScope? BeginActivity(ApiContext ctx) =>
        ctx.Principal is { } p ? Activity.Begin(p.AppId) : null;

    private static async Task<T> Generate<T>(ApiContext ctx, DeadlineScope deadline, Func<Task<T>> call)
    {
        _ = ctx;
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsDeadline)
        {
            throw Deadline(DeadlinePhases.Generating);
        }
    }

    private async Task<ISchedulerLease> AcquireAsync(ApiContext ctx, string kind, CancellationToken ct, ProgressPump? pump)
    {
        try
        {
            var progress = pump is null ? null : new SyncProgress<QueueEvent>(e =>
            {
                if (e.Kind == QueueEventKind.Queued)
                {
                    pump.Report(new PortalProgressEvent { Event = ProgressEvents.Queued, Position = e.Position });
                }
            });
            return await _o.Scheduler.AcquireAsync(ctx.CreateSchedulingRequest(kind), progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ctx.Aborted.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw Deadline(DeadlinePhases.Queued);
        }
    }

    private async Task LoadForRequestAsync(ApiContext ctx, string kind, CancellationToken ct, ProgressPump? pump)
    {
        var (model, sup) = Require(kind);
        if (IsLoaded(sup, model))
        {
            return;
        }

        var ks = _kinds[kind];
        pump?.Report(new PortalProgressEvent { Event = ProgressEvents.Loading, Progress = ks.Progress });
        void OnState(object? s, EngineStateChange c)
        {
            if (c.State == EngineState.Loading)
            {
                pump?.Report(new PortalProgressEvent { Event = ProgressEvents.Loading, Progress = c.Progress });
            }
        }

        sup.StateChanged += OnState;
        try
        {
            await EnsureLoadedAsync(kind, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ctx.Aborted.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw Deadline(DeadlinePhases.Loading);
        }
        finally
        {
            sup.StateChanged -= OnState;
        }
    }

    private DeadlineScope DeadlineToken(ApiContext ctx)
    {
        var remaining = ctx.Options.Deadline - _o.Time.GetUtcNow();
        return new DeadlineScope(ctx.Aborted, _lifetime.Token, remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining, _o.Time);
    }

    private static ApiException Deadline(string phase) =>
        new(ErrorCodes.DeadlineExceeded, "The request deadline passed.")
        {
            Details = new() { ["phase"] = JsonSerializer.SerializeToElement(phase, AiHostJsonContext.Default.String) },
        };

    private sealed class KindState
    {
        public Task? Load;
        public string? LoadModelId;
        public volatile PlanResult? LastPlan;
        public double Progress;
        public DateTimeOffset? LoadedSince;
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class DeadlineScope : IDisposable
    {
        private readonly CancellationTokenSource _timer;
        private readonly CancellationTokenSource _linked;

        public DeadlineScope(CancellationToken aborted, CancellationToken lifetime, TimeSpan remaining, TimeProvider time)
        {
            _timer = new CancellationTokenSource(remaining, time);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(aborted, lifetime, _timer.Token);
        }

        public CancellationToken Token => _linked.Token;

        public bool IsDeadline => _timer.IsCancellationRequested;

        public void Dispose()
        {
            _linked.Dispose();
            _timer.Dispose();
        }
    }
}
