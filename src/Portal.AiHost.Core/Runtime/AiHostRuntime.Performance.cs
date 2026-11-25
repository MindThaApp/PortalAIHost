using System.Globalization;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Api;
using Portal.AiHost.Core.Catalogue;
using Portal.AiHost.Core.Planning;

namespace Portal.AiHost.Core.Runtime;

/// <summary>What changed when new performance limits were applied.</summary>
/// <param name="Reloaded">Kinds whose engine was restarted with the new placement.</param>
/// <param name="Unloaded">Kinds that no longer fit and were stopped. Model files are never touched.</param>
public sealed record PerformanceApplied(IReadOnlyList<string> Reloaded, IReadOnlyList<string> Unloaded)
{
    public static PerformanceApplied None { get; } = new([], []);
}

public sealed partial class AiHostRuntime
{
    /// <summary>Smallest context a request may ask for (X-Portal-Context-Tokens).</summary>
    public const int MinRequestContextTokens = 512;

    /// <summary>Output kept for a reply even when the asked context is nearly used by the prompt.</summary>
    private const int MinOutputTokens = 16;

    private volatile PerformanceLimits? _limits;
    private volatile PlannerSettings? _planner;

    /// <summary>The planner inputs in force: the options, with the performance limits from the settings applied.</summary>
    public PlannerSettings CurrentPlanner => _planner ?? _o.Planner;

    /// <summary>The performance limits in force, or null until <see cref="ApplyPerformanceAsync"/> ran (or without settings).</summary>
    public PerformanceLimits? CurrentLimits => _limits;

    private HardwareProfile? _hardwareProfile;
    private volatile string? _capDropReason;

    /// <summary>
    /// Starts an engine under the memory cap. If that start fails while a cap was set, the same start is tried once
    /// more without the cap, in case the cap is what stopped the engine from allocating. The retry is logged (no
    /// content) and shown by GET /host/performance as memory_cap_active false with a reason, until the limits change.
    /// </summary>
    private async Task StartWithCapFallbackAsync(IEngineSupervisor sup, EngineLaunchSpec spec, string kind)
    {
        try
        {
            await sup.StartAsync(spec, _lifetime.Token).ConfigureAwait(false);
            if (spec.MemoryLimitBytes is not null)
            {
                _capDropReason = null;
            }
        }
        catch (EngineException ex) when (spec.MemoryLimitBytes is not null && ex.ErrorCode != ErrorCodes.InsufficientMemory)
        {
            _capDropReason = "The engine did not start under the memory cap, so it was started without the cap.";
            _o.Log?.Invoke($"engine '{kind}' failed to start under the memory cap ({ex.ErrorCode}); retrying once without the cap");
            await sup.StartAsync(spec with { MemoryLimitBytes = null }, _lifetime.Token).ConfigureAwait(false);
        }
    }

    private async Task<HardwareProfile> ProbeHardwareAsync(CancellationToken ct)
    {
        if (_o.Hardware is { } probe)
        {
            return _hardwareProfile = await probe.ProbeAsync(ct).ConfigureAwait(false);
        }

        // No probe (tests, tools): the facts the process itself knows, with no graphics card.
        return _hardwareProfile ??= new HardwareProfile("windows", "x64", Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, [], [], "none");
    }

    /// <summary>
    /// Reads the saved settings and applies them: the planner uses the new limits, the engines' priority changes at
    /// once, and a loaded model is reloaded with a new placement (or unloaded when it no longer fits) when a limit
    /// that decides placement changed. Nothing is deleted; a stopped model loads again on the next request.
    /// </summary>
    public async Task<PerformanceApplied> ApplyPerformanceAsync(CancellationToken ct)
    {
        if (_o.Settings is not { } settingsProvider)
        {
            return PerformanceApplied.None;
        }

        var hw = await ProbeHardwareAsync(ct).ConfigureAwait(false);
        var old = _limits;
        var limits = PerformanceModel.Resolve(settingsProvider(), hw);
        _limits = limits;
        _capDropReason = null; // new limits: the cap is tried again
        _planner = PerformanceModel.ToPlanner(limits, _o.Planner);

        // Priority and processor ceiling follow the new limits at once (and the ease-off rules, which may now differ).
        lock (_easeLock)
        {
            _tuningApplied = false;
        }

        ReevaluateEaseOff();

        var placementChanged = old is null
            || old.MemoryLimitBytes != limits.MemoryLimitBytes
            || old.KeepFreeBytes != limits.KeepFreeBytes
            || old.Compute != limits.Compute
            || old.GpuMemoryLimitBytes != limits.GpuMemoryLimitBytes
            || old.CpuThreads != limits.CpuThreads;
        if (old is null || !placementChanged)
        {
            return PerformanceApplied.None;
        }

        var reloaded = new List<string>();
        var unloaded = new List<string>();
        foreach (var (kind, sup) in _o.Supervisors)
        {
            if (sup.State is not (EngineState.Ready or EngineState.Loading) || sup.Current is not { } current ||
                GetActive(kind) is not { } model)
            {
                continue;
            }

            try
            {
                var (placement, _) = await DecidePlacementAsync(model, ct).ConfigureAwait(false);
                if (SamePlacement(current.Placement, placement) && current.Threads == ThreadsFor(model)
                    && current.MemoryLimitBytes == limits.MemoryLimitBytes)
                {
                    continue;
                }

                await sup.StopAsync(ct).ConfigureAwait(false);
                _ = ReloadQuietlyAsync(kind);
                reloaded.Add(kind);
            }
            catch (ApiException ex) when (ex.Code == ErrorCodes.InsufficientMemory)
            {
                // No longer fits: stop it (frees its memory); the next request says so in plain words.
                await sup.StopAsync(ct).ConfigureAwait(false);
                unloaded.Add(kind);
            }
        }

        if (reloaded.Count + unloaded.Count > 0)
        {
            Interlocked.Increment(ref _revision);
        }

        return new PerformanceApplied(reloaded, unloaded);
    }

    /// <summary>The planner inputs for one model: the PC's own limits, lowered by a device's placement when it has one.</summary>
    private PlannerSettings PlannerFor(ActiveModel model)
    {
        var planner = CurrentPlanner;
        if (model.Override is not { } o)
        {
            return planner;
        }

        static long? Lower(long? pc, long? asked) => asked is null ? pc : pc is null ? asked : Math.Min(pc.Value, asked.Value);
        return planner with
        {
            Backend = planner.Backend == "auto" && o.Compute is "gpu" or "cpu" ? o.Compute : planner.Backend,
            GpuMemoryLimitBytes = Lower(planner.GpuMemoryLimitBytes, o.GpuMemoryLimitBytes),
            MemoryLimitBytes = Lower(planner.MemoryLimitBytes, o.MemoryLimitBytes),
        };
    }

    private int? ThreadsFor(ActiveModel model)
    {
        var pc = _limits?.CpuThreads;
        return model.Override?.CpuThreads is { } asked && asked > 0 ? (pc is null ? asked : Math.Min(pc.Value, asked)) : pc;
    }

    private static bool SamePlacement(Placement a, Placement b) =>
        a.Backend == b.Backend && a.GpuLayers == b.GpuLayers && a.CpuMoeLayers == b.CpuMoeLayers
        && a.ContextTokens == b.ContextTokens && a.ParallelSlots == b.ParallelSlots && a.KvType == b.KvType;

    private async Task ReloadQuietlyAsync(string kind)
    {
        try
        {
            await EnsureLoadedAsync(kind, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ApiException or EngineException or EngineCrashedException or OperationCanceledException)
        {
            // Reported through engine state; the next request retries the load.
        }
    }

    // ---- GET /host/performance ---------------------------------------------------------------

    /// <summary>
    /// The limits in force and a fit preview for the active chat model. With <paramref name="candidate"/> the same
    /// answer is computed for those settings without saving or applying them.
    /// </summary>
    public async Task<PerformanceView> BuildPerformanceViewAsync(HostSettings? candidate, bool withStops, CancellationToken ct)
    {
        var hw = await ProbeHardwareAsync(ct).ConfigureAwait(false);
        var settings = candidate ?? _o.Settings?.Invoke() ?? new HostSettings();
        var limits = PerformanceModel.Resolve(settings, hw);
        var planner = PerformanceModel.ToPlanner(limits, _o.Planner);
        var mem = _o.Hardware?.GetMemory() ?? new MemorySnapshot(hw.RamBytes, 0, []);

        PlanResult? plan = null;
        string? fit = null;
        string? fitModel = null;
        string? smaller = null;
        if (GetActive(ModelKinds.Chat) is { Entry: { } entry })
        {
            fitModel = entry.DisplayName;
            plan = PlacementPlanner.Plan(entry, hw, mem, planner, CommittedExcept(ModelKinds.Chat));
            var smallerFit = plan.Fits || _o.Catalogue is null ? null
                : PerformanceModel.BestFit(_o.Catalogue, hw, mem, planner);
            smaller = smallerFit is { } sf && sf.Entry.Id != entry.Id ? sf.Entry.DisplayName : null;
            fit = PerformanceModel.Classify(plan, smaller is not null);
        }

        var (gpuName, vram) = PerformanceModel.BestGpu(hw);
        var presets = new Dictionary<string, PerformancePresetValues>();
        foreach (var name in new[] { PerformancePresets.Quiet, PerformancePresets.Balanced, PerformancePresets.Fast })
        {
            presets[name] = PerformanceModel.PresetValues(name, limits.RamBytes, limits.LogicalCores);
        }

        return new PerformanceView
        {
            Info = new PerformanceInfo
            {
                Preset = limits.Preset,
                MemoryLimitBytes = limits.MemoryLimitBytes,
                KeepFreeBytes = limits.KeepFreeBytes,
                Compute = limits.Compute,
                ComputeEffective = PerformanceModel.EffectiveCompute(limits, plan),
                GpuMemoryLimitBytes = limits.GpuMemoryLimitBytes,
                CpuThreads = limits.CpuThreads,
                Priority = limits.Priority,
                CpuCeilingPercent = limits.CpuCeilingPercent,
                EaseOff = [.. limits.EaseOffFlags],
                EasedOffNow = candidate is null ? ReevaluateEaseOff() : _easedReason,
                Fit = fit,
                MemoryCapActive = _capDropReason is null,
                MemoryCapReason = _capDropReason,
                Ranges = new PerformanceRanges
                {
                    MemoryLimitBytes = PerformanceModel.MemoryRange(limits.RamBytes, limits.KeepFreeBytes),
                    KeepFreeBytes = PerformanceModel.KeepFreeRange(limits.RamBytes),
                    GpuMemoryLimitBytes = PerformanceModel.GpuRange(vram),
                    CpuThreads = PerformanceModel.ThreadRange(limits.LogicalCores),
                    GpuAvailable = limits.GpuAvailable,
                },
            },
            RamBytes = limits.RamBytes,
            LogicalCores = limits.LogicalCores,
            GpuName = gpuName,
            GpuVramBytes = vram,
            GpuUnavailableReason = limits.GpuAvailable ? null : "Processor only: no supported graphics card found",
            FitModel = fitModel,
            SmallerModel = smaller,
            Presets = presets,
            MemoryStops = withStops && _o.Catalogue is { } catalogue
                ? PerformanceModel.MemoryStops(catalogue, hw, mem, limits)
                : [],
        };
    }

    private async Task PerformanceEndpointAsync(ApiContext ctx)
    {
        var view = await BuildPerformanceViewAsync(null, withStops: false, ctx.Aborted).ConfigureAwait(false);
        await ctx.WriteJsonAsync(200, view.Info, AiHostJsonContext.Default.PerformanceInfo).ConfigureAwait(false);
    }

    // ---- X-Portal-Context-Tokens -------------------------------------------------------------

    /// <summary>
    /// X-Portal-Context-Tokens. The engine's context size is fixed when the model loads (llama-server cannot change
    /// it per request), so the host cannot make the real context smaller for one request. What it does: clamps the
    /// asked value to 512 up to the context the model has per slot, then caps the reply length (max_tokens, or
    /// max_completion_tokens when the caller used that) so that an estimate of the prompt plus the reply stays
    /// inside the asked context. The estimate is characters divided by three, plus a few tokens per message, so it
    /// errs on the short side. It never rejects a request and never raises the caller's own max_tokens. The
    /// effective value is echoed in the same response header.
    /// </summary>
    internal void ApplyContextLimit(ApiContext ctx, ChatCompletionRequest body, ActiveModel model)
    {
        if (ctx.Options.ContextTokens is not { } asked)
        {
            return;
        }

        var placement = Supervisor(ModelKinds.Chat)?.Current?.Placement;
        var slots = Math.Max(1, placement?.ParallelSlots ?? model.ParallelSlots);
        var perSlot = Math.Max(1, (placement?.ContextTokens ?? model.ContextTokens) / slots);
        var effective = Math.Clamp(asked, Math.Min(MinRequestContextTokens, perSlot), perSlot);
        ctx.ResponseHeaders[HeaderNames.ContextTokens] = effective.ToString(CultureInfo.InvariantCulture);

        var room = Math.Max(MinOutputTokens, effective - EstimatePromptTokens(body));
        if (body.MaxCompletionTokens is { } mct)
        {
            body.MaxCompletionTokens = Math.Min(mct, room);
        }
        else
        {
            body.MaxTokens = body.MaxTokens is { } mt ? Math.Min(mt, room) : room;
        }
    }

    /// <summary>Rough prompt size: characters / 3 plus 4 per message. Deliberately on the high side.</summary>
    internal static int EstimatePromptTokens(ChatCompletionRequest body)
    {
        long chars = 0;
        foreach (var m in body.Messages)
        {
            chars += m.Content?.AsText().Length ?? 0;
        }

        return (int)Math.Min(int.MaxValue / 2, chars / 3 + 4L * body.Messages.Count);
    }
}
