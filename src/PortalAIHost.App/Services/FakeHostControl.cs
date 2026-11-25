using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Remote.Client;
using Portal.AiHost.Contracts.Control;

namespace PortalAIHost.App.Services;

/// <summary>Scripted situations the fake host can start in. Lets every page run without a real host.</summary>
public enum FakeScenario
{
    NoHost,
    Fresh,
    Downloading,
    Loaded,
    SafeMode,
    ApprovalPending,
    NoCatalogue,
    LowDisk,
}

/// <summary>A host stand-in with scripted state. Nothing here touches disk or the network.</summary>
public sealed class FakeHostControl : IHostControl, IDisposable
{
    private readonly object _gate = new();
    private readonly List<CatalogueModelSummary> _models;
    private readonly List<ClientSummary> _clients = new();
    private readonly List<PendingApproval> _pending = new();
    private readonly Dictionary<string, DownloadProgressEvent> _downloads = new();
    private readonly Timer? _timer;
    private HostSettings _settings = new()
    {
        Kinds = new() { [ModelKinds.Chat] = new KindSettings { KeepLoaded = false, IdleUnloadSeconds = 600 } },
        ModelsFolder = @"C:\Users\You\AppData\Local\PortalAIHost\models",
    };
    private bool _connected;
    private bool _safeMode;
    private string? _activeId;
    private bool? _loadedNow;

    public FakeHostControl(FakeScenario scenario, bool autoTick = true)
    {
        _models = new()
        {
            Model("qwen3-30b", "Qwen3 30B (large)", 18_000_000_000, 90, fits: false, "Needs more memory than this PC has free."),
            Model("chat-14b", "14B model (medium)", 9_000_000_000, 80, fits: true, "Runs well on your graphics card and memory, about 14 words a second.", recommended: true),
            Model("chat-8b", "8B model (small)", 5_000_000_000, 60, fits: true, "Quick and light. A good choice if you also play games."),
            Model("chat-3b", "3B model (tiny)", 2_500_000_000, 40, fits: true, "Works on any PC, but answers are simpler."),
        };
        Scenario = scenario;
        _connected = scenario != FakeScenario.NoHost;
        _safeMode = scenario == FakeScenario.SafeMode;
        CatalogueAvailable = scenario != FakeScenario.NoCatalogue;
        if (scenario == FakeScenario.LowDisk) FreeBytes = 3_400_000_000;
        switch (scenario)
        {
            case FakeScenario.Downloading:
                SetDownload("chat-14b", "downloading", 3_150_000_000, 9_000_000_000);
                break;
            case FakeScenario.Loaded:
            case FakeScenario.SafeMode:
            case FakeScenario.ApprovalPending:
                Install("chat-14b");
                _activeId = "chat-14b";
                _clients.Add(new ClientSummary { TokenId = "t1", AppId = "voice-portal", AppName = "Voice Portal", DeviceId = "local", CreatedAt = DateTimeOffset.Now.AddDays(-3), LastUsedAt = DateTimeOffset.Now.AddMinutes(-12), Priority = AppPriorities.High });
                _clients.Add(new ClientSummary { TokenId = "t2", AppId = "pdf-portal", AppName = "PDF Portal Editor", DeviceId = "local", CreatedAt = DateTimeOffset.Now.AddDays(-1), Paused = true });
                break;
        }
        if (scenario == FakeScenario.ApprovalPending)
            _pending.Add(NewApproval());
        if (autoTick)
            _timer = new Timer(_ => Tick(), null, 700, 700);
    }

    public FakeScenario Scenario { get; }

    /// <summary>When false the model list has only installed models, like a host with no trusted catalogue.</summary>
    public bool CatalogueAvailable { get; set; } = true;

    /// <summary>Free space reported for the models folder.</summary>
    public long FreeBytes { get; set; } = 212_000_000_000;

    public event Action<HostSnapshot>? StatusChanged;
    public event Action<PendingApproval>? ApprovalPending;
    public event Action<DownloadProgressEvent>? DownloadProgress;
#pragma warning disable CS0067 // the fake never reports engine state changes
    public event Action<EngineStateEvent>? EngineStateChanged;
    public event Action<TrayPopupEvent>? TrayPopup;
#pragma warning restore CS0067
    public event Action<StoreProgressEvent>? StoreProgress;

    /// <summary>When set, the next remove is refused with this reason (after "removing" was announced).</summary>
    public string? RefuseNextRemove { get; set; }

    /// <summary>What a check of an installed model finds: "ok" or "damaged".</summary>
    public string VerifyOutcome { get; set; } = "ok";

    /// <summary>When set, a move is refused up front with this reason.</summary>
    public string? RefuseMove { get; set; }

    /// <summary>True: a move stays "running" until cancelled (so tests can cancel it).</summary>
    public bool HoldMove { get; set; }

    /// <summary>Marks a model as installed without a download (tests).</summary>
    public void MarkInstalled(string id)
    {
        lock (_gate) Install(id);
    }

    public List<string> Moves { get; } = new();
    public List<(string Id, string? SwitchTo)> Removals { get; } = new();
    public List<string> Verifies { get; } = new();
    public int CancelStoreCalls { get; private set; }

    /// <summary>Ids the fake reports as damaged after a failed check.</summary>
    public HashSet<string> DamagedIds { get; } = new();

    /// <summary>Called by the fake launcher: the host "starts" and shows a fresh install.</summary>
    public void HostStarted()
    {
        lock (_gate) _connected = true;
        StatusChanged?.Invoke(Snapshot());
    }

    /// <summary>Advances every running download by one step. Public so tests can drive time.</summary>
    public void Tick()
    {
        List<DownloadProgressEvent> events = new();
        bool finished = false;
        lock (_gate)
        {
            foreach (var key in _downloads.Keys.ToList())
            {
                var d = _downloads[key];
                if (d.State != "downloading") continue;
                var done = Math.Min(d.BytesTotal, d.BytesDone + d.BytesTotal / 20);
                var next = done >= d.BytesTotal ? Done(key, d.BytesTotal) : Make(key, "downloading", done, d.BytesTotal);
                if (next.State == "done") finished = true;
                _downloads[key] = next;
                events.Add(next);
            }
        }
        foreach (var e in events) DownloadProgress?.Invoke(e);
        if (finished) StatusChanged?.Invoke(Snapshot());
    }

    /// <summary>Raises a new approval request, as if an app had just asked.</summary>
    public PendingApproval RaiseApproval()
    {
        var a = NewApproval();
        lock (_gate) _pending.Add(a);
        ApprovalPending?.Invoke(a);
        return a;
    }

    public Task<HostSnapshot> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(Snapshot());

    public Task<IReadOnlyList<PendingApproval>> ListApprovalsAsync(CancellationToken ct = default)
    {
        Require();
        lock (_gate) return Task.FromResult<IReadOnlyList<PendingApproval>>(_pending.ToList());
    }

    public Task ApproveAsync(string requestId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var p = _pending.FirstOrDefault(x => x.RequestId == requestId);
            if (p is not null)
            {
                _pending.Remove(p);
                _clients.Add(new ClientSummary { TokenId = "t" + (_clients.Count + 1), AppId = p.AppId, AppName = p.AppName, CreatedAt = DateTimeOffset.Now });
            }
        }
        return Task.CompletedTask;
    }

    public Task DenyAsync(string requestId, CancellationToken ct = default)
    {
        lock (_gate) _pending.RemoveAll(x => x.RequestId == requestId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ClientSummary>> ListClientsAsync(CancellationToken ct = default)
    {
        Require();
        lock (_gate) return Task.FromResult<IReadOnlyList<ClientSummary>>(_clients.Where(c => !c.Revoked).ToList());
    }

    /// <summary>Every pause/priority call received, for tests.</summary>
    public List<(string AppId, bool? Paused, string? Priority)> ControlCalls { get; } = new();

    public Task SetAppControlAsync(string appId, bool? paused, string? priority, CancellationToken ct = default)
    {
        Require();
        lock (_gate)
        {
            ControlCalls.Add((appId, paused, priority));
            for (var i = 0; i < _clients.Count; i++)
            {
                var c = _clients[i];
                if (c.AppId != appId) continue;
                _clients[i] = new ClientSummary
                {
                    TokenId = c.TokenId, AppId = c.AppId, AppName = c.AppName, DeviceId = c.DeviceId,
                    CreatedAt = c.CreatedAt, LastUsedAt = c.LastUsedAt, Revoked = c.Revoked,
                    Paused = paused ?? c.Paused, Priority = priority ?? c.Priority,
                };
            }
        }
        return Task.CompletedTask;
    }

    public Task RevokeClientAsync(string tokenId, CancellationToken ct = default)
    {
        lock (_gate) _clients.RemoveAll(c => c.TokenId == tokenId);
        return Task.CompletedTask;
    }

    public Task<ModelCatalogView> ListModelsAsync(CancellationToken ct = default)
    {
        Require();
        lock (_gate)
        {
            IReadOnlySet<string> existing = Scenario == FakeScenario.Fresh ? new HashSet<string> { "chat-8b" } : new HashSet<string>();
            var upgrade = Scenario is FakeScenario.Loaded or FakeScenario.SafeMode ? "qwen3-30b" : null;
            if (!CatalogueAvailable)
                return Task.FromResult(new ModelCatalogView(_models.Where(m => m.Installed).Select(Copy).ToList(), existing, _settings.ModelsFolder ?? "", FreeBytes, upgrade) { CatalogueAvailable = false });
            return Task.FromResult(new ModelCatalogView(_models.Select(Copy).ToList(), existing, _settings.ModelsFolder ?? "", FreeBytes, upgrade)
            {
                ModelsBytes = _models.Where(m => m.Installed).Sum(m => m.SizeBytes),
            });
        }
    }

    public Task StartDownloadAsync(string catalogueId, CancellationToken ct = default)
    {
        DownloadProgressEvent ev;
        lock (_gate)
        {
            var total = _models.First(m => m.Id == catalogueId).SizeBytes;
            var done = _downloads.TryGetValue(catalogueId, out var old) ? old.BytesDone : 0;
            _downloads[catalogueId] = Make(catalogueId, "downloading", done, total);
            ev = _downloads[catalogueId];
        }
        DownloadProgress?.Invoke(ev);
        return Task.CompletedTask;
    }

    public Task PauseDownloadAsync(string catalogueId, CancellationToken ct = default)
    {
        DownloadProgressEvent? ev = null;
        lock (_gate)
            if (_downloads.TryGetValue(catalogueId, out var d))
                _downloads[catalogueId] = ev = Make(catalogueId, "paused", d.BytesDone, d.BytesTotal);
        if (ev is not null) DownloadProgress?.Invoke(ev);
        return Task.CompletedTask;
    }

    public Task ResumeDownloadAsync(string catalogueId, CancellationToken ct = default) => StartDownloadAsync(catalogueId, ct);

    public int RemoveCalls { get; private set; }

    /// <summary>The fake has no earlier models folders; counts the call like a remove.</summary>
    public Task RemoveOldModelAsync(string catalogueId, string storeFolder, CancellationToken ct = default)
    {
        lock (_gate)
        {
            RemoveCalls++;
        }

        return Task.CompletedTask;
    }

    public Task RemoveModelAsync(string catalogueId, string? switchToId, CancellationToken ct = default)
    {
        DownloadProgress?.Invoke(new DownloadProgressEvent { CatalogueId = catalogueId, State = "removing" });
        if (RefuseNextRemove is { } why)
        {
            RefuseNextRemove = null;
            DownloadProgress?.Invoke(new DownloadProgressEvent { CatalogueId = catalogueId, State = "remove_failed", Message = why });
            throw new HostRequestException(ErrorCodes.InvalidRequest, why);
        }

        lock (_gate)
        {
            Removals.Add((catalogueId, switchToId));
            var wasActive = _activeId == catalogueId;
            RemoveCalls++;
            _downloads.Remove(catalogueId);
            DamagedIds.Remove(catalogueId);
            var i = _models.FindIndex(m => m.Id == catalogueId);
            if (i >= 0) _models[i] = With(_models[i], installed: false, active: false);
            if (wasActive) _activeId = switchToId;
        }

        StatusChanged?.Invoke(Snapshot());
        DownloadProgress?.Invoke(new DownloadProgressEvent { CatalogueId = catalogueId, State = "removed" });
        return Task.CompletedTask;
    }

    public List<string> DamagedRemovals { get; } = new();

    public Task RemoveDamagedModelAsync(string catalogueId, CancellationToken ct = default)
    {
        DamagedRemovals.Add(catalogueId);
        return RemoveModelAsync(catalogueId, null, ct);
    }

    /// <summary>Health checks the fake answers with. Null: a sample built from the scenario (one warning when loaded).</summary>
    public HealthReport? HealthOverride { get; set; }

    public int HealthCalls { get; private set; }

    /// <summary>
    /// A sample report. Running with a model: everything fine except a graphics card that cannot be used, so the
    /// card shows one warning. Without a model: the model check asks for one.
    /// </summary>
    public Task<HealthReport> GetHealthAsync(CancellationToken ct = default)
    {
        Require();
        HealthCalls++;
        if (HealthOverride is { } fixedReport) return Task.FromResult(fixedReport);
        var hasModel = _activeId is not null;
        var checks = new List<HealthCheck>
        {
            new() { Id = HealthIds.Service, Status = HealthStatuses.Ok, Title = "Host service", Detail = "Portal AI Host is running and apps can connect." },
            new() { Id = HealthIds.Engine, Status = HealthStatuses.Ok, Title = "AI engine", Detail = "The AI engine files are in place." },
            new() { Id = HealthIds.Graphics, Status = HealthStatuses.Warn, Title = "Graphics card", Detail = "No supported graphics card was found, so models run on the processor. That works, but replies are slower." },
            hasModel
                ? new HealthCheck { Id = HealthIds.Model, Status = HealthStatuses.Ok, Title = "Chat model", Detail = "The model is ready and loads when an app asks for it.", FixAction = HealthFixes.LoadModel }
                : new HealthCheck { Id = HealthIds.Model, Status = HealthStatuses.Warn, Title = "Chat model", Detail = "No chat model is chosen yet, so apps cannot ask questions.", FixAction = HealthFixes.OpenModels },
            new() { Id = HealthIds.Disk, Status = Scenario == FakeScenario.LowDisk ? HealthStatuses.Warn : HealthStatuses.Ok, Title = "Disk space", Detail = Scenario == FakeScenario.LowDisk ? "The drive with your models has 1.1 GB free, which is too little to download another model." : "212.4 GB free on the models drive." },
            new() { Id = HealthIds.Memory, Status = HealthStatuses.Ok, Title = "Memory", Detail = "The chat model fits in the memory you allow." },
            new() { Id = HealthIds.EaseOff, Status = HealthStatuses.Ok, Title = "Easing off", Detail = "The AI is running at full speed for your settings." },
            new() { Id = HealthIds.MemoryCap, Status = HealthStatuses.Ok, Title = "Memory cap", Detail = "The memory limit is being kept." },
        };
        var notOk = checks.Count(c => c.Status != HealthStatuses.Ok);
        return Task.FromResult(new HealthReport
        {
            Status = notOk == 0 ? HealthStatuses.Ok : HealthStatuses.Warn,
            NeedsAttention = notOk,
            Checks = checks,
        });
    }

    public Task VerifyModelAsync(string catalogueId, CancellationToken ct = default)
    {
        Verifies.Add(catalogueId);
        StoreProgress?.Invoke(new StoreProgressEvent { Operation = StoreOperations.Verify, State = StoreStates.Running, CatalogueId = catalogueId, Fraction = 0.5 });
        if (VerifyOutcome == "damaged")
        {
            DamagedIds.Add(catalogueId);
            StoreProgress?.Invoke(new StoreProgressEvent { Operation = StoreOperations.Verify, State = StoreStates.Damaged, CatalogueId = catalogueId, Fraction = 1, Message = "x.gguf does not match the model list." });
        }
        else
        {
            DamagedIds.Remove(catalogueId);
            StoreProgress?.Invoke(new StoreProgressEvent { Operation = StoreOperations.Verify, State = StoreStates.Done, CatalogueId = catalogueId, Fraction = 1 });
        }

        return Task.CompletedTask;
    }

    /// <summary>Paths passed to "Add your own model".</summary>
    public List<string> Imports { get; } = new();

    /// <summary>When set, the next import is refused up front with this message.</summary>
    public string? RefuseImport { get; set; }

    /// <summary>Keeps an import running (no done event) so a test can cancel it.</summary>
    public bool HoldImport { get; set; }

    public Task ImportModelAsync(string path, CancellationToken ct = default)
    {
        if (RefuseImport is { } why) throw new HostRequestException(ErrorCodes.InvalidRequest, why);
        Imports.Add(path);
        StoreProgress?.Invoke(new StoreProgressEvent { Operation = StoreOperations.Import, State = StoreStates.Running, Fraction = 0.5, BytesDone = 50, BytesTotal = 100 });
        if (!HoldImport)
        {
            var id = "user-" + Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            _models.Add(new CatalogueModelSummary { Id = id, DisplayName = Path.GetFileNameWithoutExtension(path), SizeBytes = 100, Installed = true, UserAdded = true, FitsThisPc = true });
            StoreProgress?.Invoke(new StoreProgressEvent { Operation = StoreOperations.Import, State = StoreStates.Done, CatalogueId = id, Fraction = 1, Message = "Added " + Path.GetFileNameWithoutExtension(path) + "." });
        }
        return Task.CompletedTask;
    }

    public Task<string> GetStoreFolderAsync(CancellationToken ct = default) => Task.FromResult(_settings.ModelsFolder ?? "");

    public Task MoveStoreAsync(string parentFolder, CancellationToken ct = default)
    {
        if (RefuseMove is { } why) throw new HostRequestException(ErrorCodes.InvalidRequest, why);
        Moves.Add(parentFolder);
        StoreProgress?.Invoke(new StoreProgressEvent { Operation = StoreOperations.Move, State = StoreStates.Running, Fraction = 0.4 });
        if (!HoldMove)
            StoreProgress?.Invoke(new StoreProgressEvent { Operation = StoreOperations.Move, State = StoreStates.Done, Fraction = 1, Message = "Your models were moved." });
        return Task.CompletedTask;
    }

    /// <summary>What the fake host offers to free up; a clean removes the ticked items from it.</summary>
    public List<CleanupItemInfo> CleanupSample { get; } =
    [
        new() { Id = "part|sample-a", Kind = CleanupKinds.Partial, Label = "sample-model-q4.gguf", Detail = "Unfinished download, untouched for 12 days", Bytes = 1_800_000_000, FileCount = 1 },
        new() { Id = "old|sample-b", Kind = CleanupKinds.OldCopies, Label = "Old copies of your models", Detail = "Left in the earlier folder after a move; the new copies are recorded as checked", Bytes = 4_200_000_000, FileCount = 2 },
        new() { Id = "damaged|sample-c|900", Kind = CleanupKinds.Damaged, Label = "Sample damaged model", Detail = "x.gguf does not match the model list.", Bytes = 900_000_000, FileCount = 1 },
    ];

    /// <summary>When set, the next scan reports the host as busy with this reason.</summary>
    public string? CleanupBusy { get; set; }

    public int CleanupCalls { get; private set; }

    public Task<CleanupScan> ScanCleanupAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(CleanupBusy is { } busy
                ? new CleanupScan { BusyReason = busy }
                : new CleanupScan { Items = [.. CleanupSample], TotalBytes = CleanupSample.Sum(i => i.Bytes) });
        }
    }

    public Task<CleanupResult> CleanupAsync(IReadOnlyCollection<string> itemIds, CancellationToken ct = default)
    {
        lock (_gate)
        {
            CleanupCalls++;
            var taken = CleanupSample.Where(i => itemIds.Contains(i.Id)).ToList();
            CleanupSample.RemoveAll(i => itemIds.Contains(i.Id));
            return Task.FromResult(new CleanupResult { FreedBytes = taken.Sum(i => i.Bytes), Removed = [.. taken.Select(i => i.Label)] });
        }
    }

    public Task CancelStoreOperationAsync(CancellationToken ct = default)
    {
        CancelStoreCalls++;
        if (HoldImport)
        {
            HoldImport = false;
            StoreProgress?.Invoke(new StoreProgressEvent { Operation = StoreOperations.Import, State = StoreStates.Cancelled });
            return Task.CompletedTask;
        }
        if (HoldMove)
        {
            HoldMove = false;
            StoreProgress?.Invoke(new StoreProgressEvent { Operation = StoreOperations.Move, State = StoreStates.Cancelled, Message = "The move was cancelled. Your models are where they were." });
        }

        return Task.CompletedTask;
    }

    public Task RemoveModelAsync(string catalogueId, CancellationToken ct = default) => RemoveModelAsync(catalogueId, null, ct);

    private static CatalogueModelSummary With(CatalogueModelSummary m, bool installed, bool active) => new()
    {
        Id = m.Id, DisplayName = m.DisplayName, Kind = m.Kind, Engine = m.Engine, SizeBytes = m.SizeBytes, Quality = m.Quality,
        Recommended = m.Recommended, Installed = installed, Active = active, FitsThisPc = m.FitsThisPc, Reason = m.Reason,
    };

    public Task CancelDownloadAsync(string catalogueId, CancellationToken ct = default)
    {
        lock (_gate) _downloads.Remove(catalogueId);
        return Task.CompletedTask;
    }

    public List<string> Activations { get; } = new();

    public Task ActivateModelAsync(string catalogueId, CancellationToken ct = default)
    {
        Activations.Add(catalogueId);
        lock (_gate) _activeId = catalogueId;
        StatusChanged?.Invoke(Snapshot());
        return Task.CompletedTask;
    }

    public Task<HostSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        Require();
        lock (_gate) return Task.FromResult(_settings);
    }

    /// <summary>Settings sent to <see cref="SetSettingsAsync"/>, oldest first (checks read them).</summary>
    public List<HostSettings> SavedSettings { get; } = new();

    /// <summary>A 64 GB, 12 core PC with an 8 GB card, so the page shows realistic ranges.</summary>
    public Task<PerformanceView> GetPerformanceAsync(HostSettings? candidate = null, CancellationToken ct = default)
    {
        Require();
        const long gb = 1L << 30;
        HostSettings s;
        lock (_gate) s = candidate ?? _settings;
        var preset = s.Preset is "quiet" or "fast" or "custom" ? s.Preset : "balanced";
        long memory = s.MemoryLimitBytes ?? (preset == "quiet" ? 32 * gb : preset == "fast" ? 56 * gb : 51 * gb);
        var compute = s.Compute is "gpu" or "cpu" ? s.Compute : "auto";
        var fit = memory < 12 * gb ? "needs_smaller_model" : compute == "cpu" ? "cpu_only" : memory < 20 * gb ? "fits_partial" : "fits_fully";
        var view = new PerformanceView
        {
            Info = new PerformanceInfo
            {
                Preset = preset,
                MemoryLimitBytes = memory,
                KeepFreeBytes = s.KeepFreeBytes ?? 8 * gb,
                Compute = compute,
                ComputeEffective = compute == "cpu" ? "cpu" : "gpu",
                GpuMemoryLimitBytes = s.GpuMemoryLimitBytes ?? 7 * gb + gb / 2,
                CpuThreads = s.CpuThreads ?? (preset == "quiet" ? 6 : preset == "fast" ? 11 : 10),
                Priority = s.Priority == "normal" ? "normal" : "low",
                CpuCeilingPercent = Math.Clamp(s.CpuCeilingPercent, 20, 100),
                EaseOff = EaseOffReasons.Normalize(s.EaseOff),
                Fit = fit,
                Ranges = new PerformanceRanges
                {
                    MemoryLimitBytes = new NumberRange { Min = 2 * gb, Max = 56 * gb, Step = gb },
                    KeepFreeBytes = new NumberRange { Min = 2 * gb, Max = 32 * gb, Step = gb },
                    GpuMemoryLimitBytes = new NumberRange { Min = gb, Max = 7 * gb + gb / 2, Step = gb / 2 },
                    CpuThreads = new NumberRange { Min = 1, Max = 11 },
                    GpuAvailable = true,
                },
            },
            RamBytes = 64 * gb,
            LogicalCores = 12,
            GpuName = "AMD Radeon RX 6600",
            GpuVramBytes = 8 * gb,
            FitModel = "Qwen3 14B",
            SmallerModel = fit == "needs_smaller_model" ? "Qwen3 4B" : null,
            Presets = new()
            {
                ["quiet"] = new PerformancePresetValues { MemoryLimitBytes = 32 * gb, CpuThreads = 6, Priority = "low" },
                ["balanced"] = new PerformancePresetValues { MemoryLimitBytes = 51 * gb, CpuThreads = 10, Priority = "low" },
                ["fast"] = new PerformancePresetValues { MemoryLimitBytes = 56 * gb, CpuThreads = 11, Priority = "normal" },
            },
        };
        for (var b = 2; b <= 56; b++)
        {
            view.MemoryStops.Add(new MemoryStop
            {
                Bytes = b * gb,
                FitsModel = b < 5 ? null : b < 10 ? "Qwen3 8B" : b < 20 ? "Qwen3 14B" : "Qwen3 30B A3B",
                Fit = b < 5 ? null : b < 12 ? "fits_partial" : "fits_fully",
            });
        }

        return Task.FromResult(view);
    }

    public Task<SettingsSaved> SetSettingsAsync(HostSettings settings, CancellationToken ct = default)
    {
        lock (_gate) SavedSettings.Add(settings);
        bool moved;
        lock (_gate)
        {
            moved = !string.Equals(_settings.ModelsFolder, settings.ModelsFolder, StringComparison.OrdinalIgnoreCase);
            _settings = settings;
        }
        return Task.FromResult(new SettingsSaved { ModelsFolderChanged = moved, AppliesAtRestart = moved });
    }

    /// <summary>The turns of the latest "Try it" request (for tests).</summary>
    public IReadOnlyList<TryItTurn> LastTryItTurns { get; private set; } = [];

    public async IAsyncEnumerable<TryItPiece> TryItAsync(IReadOnlyList<TryItTurn> turns, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Require();
        LastTryItTurns = turns.ToList();
        var prompt = turns.LastOrDefault(t => t.IsUser)?.Text ?? "";
        var first = Environment.TickCount64;
        long firstAt = 0, count = 0;
        if (prompt.Contains("[think]", StringComparison.Ordinal))
        {
            await Task.Delay(40, ct).ConfigureAwait(false);
            firstAt = Environment.TickCount64;
            count++;
            yield return new TryItPiece("Working out what to say. ", true);
        }

        foreach (var word in ("This is a sample reply from the practice host. You asked: " + prompt).Split(' '))
        {
            await Task.Delay(40, ct).ConfigureAwait(false);
            if (firstAt == 0) firstAt = Environment.TickCount64;
            count++;
            yield return new TryItPiece(word + " ");
        }

        var end = Environment.TickCount64;
        yield return new TryItPiece("", false, new TryItStats(firstAt - first, end - first, count, 25.0, false));
    }

    public Task CancelTryItAsync() => Task.CompletedTask;

    public event Action? RemoteChanged;

    /// <summary>The paired devices the fake reports (tests add some).</summary>
    public List<PairedDeviceSummary> Devices { get; } = new();

    /// <summary>The code the fake shows while a pairing is open.</summary>
    public PairingOfferInfo? Pairing { get; private set; }

    public int PairingStarts { get; private set; }

    /// <summary>The models the fake says devices loaded (shown as "Loaded by").</summary>
    public List<LoadedModelInfo> LoadedModels { get; } = new();

    public int TakeBacks { get; private set; }

    public Task<RemoteStatus> GetRemoteAsync(CancellationToken ct = default)
    {
        Require();
        lock (_gate)
        {
            return Task.FromResult(new RemoteStatus
            {
                Enabled = _settings.Remote.Enabled,
                Listening = _settings.Remote.Enabled,
                Port = _settings.Remote.Port,
                HostName = "Desk PC",
                Addresses = ["192.168.1.20"],
                Fingerprint = "fake-fingerprint",
                Devices = [.. Devices],
                AllowReplaceModels = _settings.Remote.AllowReplaceModels,
                LoadedModels = [.. LoadedModels],
                Pairing = _settings.Remote.Enabled ? Pairing : null,
                Relay = _settings.Remote.Enabled && _settings.Remote.Relay ? RelayStates.Connected : RelayStates.Off,
            });
        }
    }

    public Task<PairingOfferInfo> StartPairingAsync(CancellationToken ct = default)
    {
        Require();
        lock (_gate)
        {
            if (!_settings.Remote.Enabled) throw new HostRequestException("feature_disabled", "Turn on other devices first.");
            PairingStarts++;
            Pairing = new PairingOfferInfo
            {
                Code = "ABCD-EFGH-JKMN",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                HostName = "Desk PC",
                Address = "192.168.1.20",
                Port = _settings.Remote.Port,
                Fingerprint = "fake-fingerprint",
                QrText = "portalai://pair?h=192.168.1.20&p=48123&f=fake-fingerprint&c=ABCDEFGHJKMN&n=Desk%20PC&r=https://portal-relay.portalai.workers.dev",
            };
            return Task.FromResult(Pairing);
        }
    }

    public Task CancelPairingAsync(CancellationToken ct = default)
    {
        Require();
        lock (_gate) Pairing = null;
        RemoteChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task RemoveDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        Require();
        lock (_gate) Devices.RemoveAll(d => d.DeviceId == deviceId);
        RemoteChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task SetModelControlAsync(string deviceId, bool enabled, CancellationToken ct = default)
    {
        Require();
        lock (_gate)
        {
            var device = Devices.FirstOrDefault(d => d.DeviceId == deviceId)
                ?? throw new HostRequestException("not_found", "Unknown device.");
            device.ModelControl = enabled;
        }

        RemoteChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task TakeBackAsync(string kind, CancellationToken ct = default)
    {
        Require();
        lock (_gate)
        {
            TakeBacks++;
            LoadedModels.RemoveAll(m => m.Kind == kind);
        }

        RemoteChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task PauseDeviceAsync(string deviceId, string? app, bool paused, CancellationToken ct = default)
    {
        Require();
        lock (_gate)
        {
            var d = Devices.FirstOrDefault(x => x.DeviceId == deviceId) ?? throw new HostRequestException("not_found", "Unknown device.");
            if (app is null)
            {
                d.Paused = paused;
            }
            else
            {
                d.PausedApps.RemoveAll(a => a == app);
                if (paused) d.PausedApps.Add(app);
                foreach (var row in d.Apps.Where(a => a.Name == app)) row.Paused = paused;
            }
        }

        RemoteChanged?.Invoke();
        return Task.CompletedTask;
    }

    public SetDeviceSettingsCommand? LastDeviceSettings { get; private set; }

    public Task SetDeviceSettingsAsync(SetDeviceSettingsCommand settings, CancellationToken ct = default)
    {
        Require();
        lock (_gate)
        {
            var d = Devices.FirstOrDefault(x => x.DeviceId == settings.DeviceId) ?? throw new HostRequestException("not_found", "Unknown device.");
            LastDeviceSettings = settings;
            d.PriorityEqual = settings.PriorityEqual;
            d.MaxRequestsPerDay = settings.MaxRequestsPerDay;
            d.MaxTokensPerDay = settings.MaxTokensPerDay;
            d.QuietStart = settings.QuietStart;
            d.QuietEnd = settings.QuietEnd;
            d.AllowedModels = [.. settings.AllowedModels];
        }

        RemoteChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task RenameDeviceAsync(string deviceId, string name, CancellationToken ct = default)
    {
        Require();
        lock (_gate)
        {
            var d = Devices.FirstOrDefault(x => x.DeviceId == deviceId) ?? throw new HostRequestException("not_found", "Unknown device.");
            if (string.IsNullOrWhiteSpace(name)) throw new HostRequestException("invalid_request", "The name is empty.");
            d.Name = name.Trim();
        }

        RemoteChanged?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>Pushes a remote-changed event with nothing new (a burst test).</summary>
    public void CompletePairingNoop() => RemoteChanged?.Invoke();

    // ---- Model control, laptop side (client mode) ----

    /// <summary>The fake is a laptop using another PC.</summary>
    public bool ClientMode { get; set; }

    public string ClientPcName { get; set; } = "Desk PC";

    /// <summary>This laptop has Model control on the PC.</summary>
    public bool ModelControlGranted { get; set; }

    /// <summary>The PC's installed models as the fake PC reports them.</summary>
    public List<DeviceModelState> RemoteModels { get; } =
    [
        new() { Id = "alpha", DisplayName = "Alpha", Kind = ModelKinds.Chat, SizeBytes = 2_000_000_000 },
        new() { Id = "beta", DisplayName = "Beta", Kind = ModelKinds.Chat, SizeBytes = 4_000_000_000 },
    ];

    /// <summary>Makes the next load or unload fail like the PC would (code, reason).</summary>
    public (string Code, string? Reason)? NextRemoteError { get; set; }

    public Dictionary<string, string> AppModelChoices { get; } = new(StringComparer.Ordinal);

    public List<string> RemoteCalls { get; } = [];


    // ---- client mode state, link and settings ----

    /// <summary>A PC is saved while this fake is in server mode (the status still names it).</summary>
    public bool SavedPairing { get; set; }

    /// <summary>Link state while in client mode: connecting, connected, unreachable, revoked, fingerprint_changed, stopped.</summary>
    public string ClientLinkState { get; set; } = "connected";

    public string ClientRoute { get; set; } = RemoteRoutes.Lan;

    public int? ClientRtt { get; set; } = 12;

    public string ClientFallbackValue { get; set; } = ClientFallbackModes.None;

    public bool FallbackUnset { get; set; } = true;

    public bool FallbackPromptPending { get; set; }

    public bool UsingLaptopModel { get; set; }

    public string RelayModeValue { get; set; } = RelayModes.Auto;

    public bool UseRelayValue { get; set; }

    /// <summary>Every client.* call the window made, in order (for example <c>setfallback:local</c>, <c>promptshown</c>).</summary>
    public List<string> ClientCalls { get; } = [];

    public List<PairCommand> PairCalls { get; } = [];

    /// <summary>What the next client.pair answers; null: a success naming <see cref="ClientPcName"/>.</summary>
    public PairResult? NextPairResult { get; set; }

    /// <summary>Runs inside a pairing call (a test holds it open to see the progress state or cancel).</summary>
    public Func<CancellationToken, Task>? PairHook { get; set; }

    /// <summary>Makes the next client.* call throw like a host that went away.</summary>
    public bool ClientStatusFails { get; set; }

    public ClientModeStatus ClientStatusNow()
    {
        lock (_gate)
        {
            var link = ClientMode;
            return new ClientModeStatus
            {
                Mode = link ? HostModes.Client : HostModes.Server,
                Paired = link || SavedPairing,
                PcName = link || SavedPairing ? ClientPcName : null,
                State = link ? ClientLinkState : null,
                Route = link ? (ClientLinkState == "connected" ? ClientRoute : RemoteRoutes.Down) : null,
                RttMs = link && ClientLinkState == "connected" ? ClientRtt : null,
                ClientFallback = ClientFallbackValue,
                FallbackUnset = FallbackUnset,
                FallbackPromptPending = FallbackPromptPending,
                UsingLaptopModel = link && UsingLaptopModel,
                RelayMode = RelayModeValue,
                UseRelay = UseRelayValue,
                ModelControl = link ? ModelControlGranted : null,
                LoadedOnPc = RemoteModels.Where(m => m.LoadedBy == "dev_me").Select(m => m.Id).ToList(),
            };
        }
    }

    public Task<ClientModeStatus> GetClientStatusAsync(CancellationToken ct = default)
    {
        Require();
        if (ClientStatusFails) throw new HostUnavailableException();
        return Task.FromResult(ClientStatusNow());
    }

    public Task<ClientModeStatus> SetClientModeAsync(string mode, CancellationToken ct = default)
    {
        Require();
        lock (_gate)
        {
            ClientCalls.Add("setmode:" + mode);
            if (mode == HostModes.Client && !SavedPairing && !ClientMode)
            {
                var refused = ClientStatusNow();
                refused.Refused = "not_paired";
                return Task.FromResult(refused);
            }

            ClientMode = mode == HostModes.Client;
        }

        RemoteChanged?.Invoke();
        return Task.FromResult(ClientStatusNow());
    }

    public Task<ClientModeStatus> SetClientFallbackAsync(string fallback, CancellationToken ct = default)
    {
        Require();
        lock (_gate)
        {
            ClientCalls.Add("setfallback:" + fallback);
            ClientFallbackValue = fallback;
            FallbackUnset = false;
        }

        return Task.FromResult(ClientStatusNow());
    }

    public Task<ClientModeStatus> SetRelayModeAsync(string relayMode, CancellationToken ct = default)
    {
        Require();
        lock (_gate)
        {
            ClientCalls.Add("setrelaymode:" + relayMode);
            RelayModeValue = relayMode;
        }

        return Task.FromResult(ClientStatusNow());
    }

    public Task<ClientModeStatus> SetUseRelayAsync(bool enabled, CancellationToken ct = default)
    {
        Require();
        lock (_gate)
        {
            ClientCalls.Add("userelay:" + enabled);
            UseRelayValue = enabled;
        }

        return Task.FromResult(ClientStatusNow());
    }

    public Task<ClientModeStatus> MarkFallbackPromptShownAsync(CancellationToken ct = default)
    {
        Require();
        lock (_gate)
        {
            ClientCalls.Add("promptshown");
            FallbackPromptPending = false;
        }

        return Task.FromResult(ClientStatusNow());
    }

    public async Task<PairResult> PairClientAsync(PairCommand command, CancellationToken ct = default)
    {
        Require();
        lock (_gate) PairCalls.Add(command);
        if (PairHook is { } hook) await hook(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ClientCalls.Add("pair");
            if (NextPairResult is { } fixedResult)
            {
                NextPairResult = null;
                fixedResult.Status = ClientStatusNow();
                return fixedResult;
            }

            SavedPairing = true;
            ClientMode = true;
            ClientLinkState = "connecting";
        }

        RemoteChanged?.Invoke();
        return new PairResult { Ok = true, PcName = ClientPcName, Status = ClientStatusNow() };
    }

    public Task<ClientModeStatus> UnpairClientAsync(CancellationToken ct = default)
    {
        Require();
        lock (_gate)
        {
            ClientCalls.Add("unpair");
            ClientMode = false;
            SavedPairing = false;
            UsingLaptopModel = false;
        }

        RemoteChanged?.Invoke();
        return Task.FromResult(ClientStatusNow());
    }

    public event Action<DeviceAlert>? DeviceAlerted;

    /// <summary>The host announces a new pairing or a device connecting from away.</summary>
    public void RaiseDeviceAlert(string kind, string deviceName) =>
        DeviceAlerted?.Invoke(new DeviceAlert { Kind = kind, DeviceId = "dev_x", DeviceName = deviceName });

    private RemoteModelsAnswer RemoteAnswer(string? code = null, string? reason = null, string? message = null)
    {
        lock (_gate)
        {
            var answer = new RemoteModelsAnswer
            {
                Ok = code is null,
                ModelControl = ModelControlGranted,
                PcName = ClientPcName,
                DeviceId = "dev_me",
                Models = RemoteModels.Select(m => new DeviceModelState
                {
                    Id = m.Id, DisplayName = m.DisplayName, Kind = m.Kind, SizeBytes = m.SizeBytes, FitsThisPc = m.FitsThisPc,
                    State = m.State, Placement = m.Placement, LoadedBy = m.LoadedBy,
                }).ToList(),
                ErrorCode = code,
                Reason = reason,
                Message = message,
            };
            return answer;
        }
    }

    public Task<RemoteModelsAnswer> ListRemoteModelsAsync(CancellationToken ct = default)
    {
        Require();
        lock (_gate)
        {
            RemoteCalls.Add("list");
            return Task.FromResult(ModelControlGranted
                ? RemoteAnswer()
                : RemoteAnswer(ErrorCodes.ModelControlRequired, null, "This laptop does not have Model control. Ask the PC owner."));
        }
    }

    public Task<RemoteModelsAnswer> LoadRemoteModelAsync(string modelId, string? placement, CancellationToken ct = default)
    {
        Require();
        lock (_gate)
        {
            RemoteCalls.Add($"load:{modelId}:{placement ?? "auto"}");
            if (!ModelControlGranted)
            {
                return Task.FromResult(RemoteAnswer(ErrorCodes.ModelControlRequired, null, "This laptop does not have Model control. Ask the PC owner."));
            }

            if (NextRemoteError is { } error)
            {
                NextRemoteError = null;
                return Task.FromResult(RemoteAnswer(error.Code, error.Reason, "The PC said no."));
            }

            var model = RemoteModels.FirstOrDefault(m => m.Id == modelId);
            if (model is null)
            {
                return Task.FromResult(RemoteAnswer(ErrorCodes.NotFound, null, "The PC does not have that model."));
            }

            foreach (var other in RemoteModels.Where(m => m.LoadedBy == "dev_me" && m.Kind == model.Kind))
            {
                other.State = "unloaded";
                other.LoadedBy = null;
                other.Placement = null;
            }

            model.State = "loaded";
            model.LoadedBy = "dev_me";
            model.Placement = placement ?? DevicePlacements.Auto;
            return Task.FromResult(RemoteAnswer());
        }
    }

    public Task<RemoteModelsAnswer> UnloadRemoteModelAsync(string modelId, CancellationToken ct = default)
    {
        Require();
        lock (_gate)
        {
            RemoteCalls.Add($"unload:{modelId}");
            if (!ModelControlGranted)
            {
                return Task.FromResult(RemoteAnswer(ErrorCodes.ModelControlRequired, null, "This laptop does not have Model control. Ask the PC owner."));
            }

            if (NextRemoteError is { } error)
            {
                NextRemoteError = null;
                return Task.FromResult(RemoteAnswer(error.Code, error.Reason, "The PC said no."));
            }

            var model = RemoteModels.FirstOrDefault(m => m.Id == modelId && m.LoadedBy == "dev_me");
            if (model is null)
            {
                return Task.FromResult(RemoteAnswer(ErrorCodes.ModelConflict, ModelConflictReasons.NotLoadedByDevice,
                    "This laptop did not load that model, so it cannot unload it."));
            }

            model.State = "unloaded";
            model.LoadedBy = null;
            model.Placement = null;
            return Task.FromResult(RemoteAnswer());
        }
    }

    public Task<AppModelList> GetAppModelsAsync(CancellationToken ct = default)
    {
        Require();
        lock (_gate)
        {
            return Task.FromResult(new AppModelList
            {
                Apps = AppModelChoices.Select(p => new AppModelEntry { AppId = p.Key, ModelId = p.Value }).ToList(),
            });
        }
    }

    public Task<AppModelList> SetAppModelAsync(string appId, string? modelId, CancellationToken ct = default)
    {
        Require();
        lock (_gate)
        {
            if (string.IsNullOrEmpty(modelId)) AppModelChoices.Remove(appId);
            else AppModelChoices[appId] = modelId;
            RemoteCalls.Add($"appmodel:{appId}:{modelId ?? "-"}");
        }

        return GetAppModelsAsync(ct);
    }


    /// <summary>Pretends a device finished pairing: it appears in the list and the code is used up.</summary>
    public void CompletePairing(string name)
    {
        lock (_gate)
        {
            Devices.Add(new PairedDeviceSummary { DeviceId = "dev_" + (Devices.Count + 1), Name = name, CreatedAt = DateTimeOffset.UtcNow });
            Pairing = null;
        }

        RemoteChanged?.Invoke();
    }

    /// <summary>What the fake reports as live use while the model is loaded.</summary>
    public HostUsage Usage { get; set; } = new()
    {
        RamUsedBytes = 18L * 1024 * 1024 * 1024, VramUsedBytes = 7L * 1024 * 1024 * 1024, CpuPercent = 12,
        GenerationTokensPerSecond = 28, UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>What the fake reports as today's activity.</summary>
    public List<AppActivity> Activity { get; } = new();

    /// <summary>When set, load and unload wait for it first (tests use it to see the pending state).</summary>
    public Task? EngineGate { get; set; }

    /// <summary>When set, load and unload fail with this reason.</summary>
    public string? EngineFailure { get; set; }

    public int LoadCalls { get; private set; }
    public int UnloadCalls { get; private set; }

    public Task<IReadOnlyList<AppActivity>> GetActivityAsync(CancellationToken ct = default)
    {
        Require();
        return Task.FromResult<IReadOnlyList<AppActivity>>(Activity.ToList());
    }

    public Task LoadEngineAsync(CancellationToken ct = default) => EngineAsync(true);

    public Task UnloadEngineAsync(CancellationToken ct = default) => EngineAsync(false);

    /// <summary>Raises the tray pop-up signal, like a click on the tray icon.</summary>
    public void RaiseTrayPopup(int x, int y) => TrayPopup?.Invoke(new TrayPopupEvent { X = x, Y = y });

    private async Task EngineAsync(bool load)
    {
        Require();
        if (load) LoadCalls++; else UnloadCalls++;
        if (EngineGate is { } gate) await gate.ConfigureAwait(false);
        if (EngineFailure is { } why) throw new HostRequestException("internal_error", why);
        lock (_gate) _loadedNow = load;
        StatusChanged?.Invoke(Snapshot());
    }

    public void Dispose() => _timer?.Dispose();

    private void Require()
    {
        lock (_gate)
            if (!_connected) throw new HostUnavailableException();
    }

    private HostSnapshot Snapshot()
    {
        lock (_gate)
        {
            if (!_connected) return HostSnapshot.NoHost;
            var active = _models.FirstOrDefault(m => m.Id == _activeId);
            var downloading = _downloads.Values.Any(d => d.State == "downloading");
            var loaded = _loadedNow ?? Scenario is FakeScenario.Loaded or FakeScenario.ApprovalPending;
            var chat = new ModelStatus
            {
                Id = active?.Id,
                DisplayName = active?.DisplayName,
                State = active is null ? ModelStates.NotInstalled : (loaded ? ModelStates.Loaded : ModelStates.Unloaded),
                LoadedSince = active is not null && loaded ? DateTimeOffset.Now.AddMinutes(-20) : null,
                IdleUnloadSeconds = 600,
            };
            var status = new HostStatus
            {
                HostVersion = "1.0.0",
                State = active is not null ? (loaded ? HostStates.Ready : HostStates.Idle) : downloading ? HostStates.Downloading : HostStates.NoModel,
                Models = new() { [ModelKinds.Chat] = chat },
                Queue = new QueueStatus { Running = loaded ? 1 : 0, Waiting = loaded ? 2 : 0 },
                Hardware = new HardwareInfo
                {
                    CpuCores = 12,
                    RamBytes = 64L * 1024 * 1024 * 1024,
                    Gpus = new() { new GpuInfo { Name = "AMD Radeon RX 6600", Vendor = "AMD", VramBytes = 8L * 1024 * 1024 * 1024, Backend = "vulkan" } },
                },
                UpgradeAvailable = Scenario is FakeScenario.Loaded or FakeScenario.SafeMode,
                Usage = loaded ? Usage : null,
            };
            return new HostSnapshot(true, status, _safeMode);
        }
    }

    private void SetDownload(string id, string state, long done, long total) => _downloads[id] = Make(id, state, done, total);

    private DownloadProgressEvent Done(string id, long total)
    {
        Install(id);
        _activeId ??= id;
        return Make(id, "done", total, total);
    }

    private void Install(string id)
    {
        var i = _models.FindIndex(m => m.Id == id);
        var m = _models[i];
        _models[i] = new CatalogueModelSummary
        {
            Id = m.Id, DisplayName = m.DisplayName, Kind = m.Kind, Engine = m.Engine, SizeBytes = m.SizeBytes,
            Quality = m.Quality, Recommended = m.Recommended, Installed = true, FitsThisPc = m.FitsThisPc, Reason = m.Reason,
        };
    }

    private CatalogueModelSummary Copy(CatalogueModelSummary m) => new()
    {
        Id = m.Id, DisplayName = m.DisplayName, Kind = m.Kind, Engine = m.Engine, SizeBytes = m.SizeBytes,
        Quality = m.Quality, Recommended = m.Recommended, Installed = m.Installed, Active = m.Id == _activeId,
        FitsThisPc = m.FitsThisPc, Reason = m.Reason, UserAdded = m.UserAdded,
        Damaged = m.Installed && DamagedIds.Contains(m.Id),
        LastUsedAt = m.Installed ? DateTimeOffset.UtcNow.AddDays(-2) : null,
        MemoryNeededBytes = m.SizeBytes + 1_000_000_000,
        LicenceName = "apache-2.0", LicenceUrl = "https://www.apache.org/licenses/LICENSE-2.0",
        AdviceSummary = m.Id == "chat-14b" ? "A good all-rounder for writing and questions." : null,
        GoodFor = m.Id == "chat-14b" ? ["everyday chat", "writing"] : null,
        DownloadState = !m.Installed && _downloads.TryGetValue(m.Id, out var d) && d.State != "done" ? d.State : null,
        BytesDone = !m.Installed && _downloads.TryGetValue(m.Id, out var d2) ? d2.BytesDone : 0,
    };

    private static DownloadProgressEvent Make(string id, string state, long done, long total) => new()
    {
        CatalogueId = id, State = state, BytesDone = done, BytesTotal = total,
        EtaSeconds = state == "downloading" ? Math.Max(1, (total - done) / 50_000_000.0) : null,
    };

    private static CatalogueModelSummary Model(string id, string name, long size, double quality, bool fits, string reason, bool recommended = false) => new()
    {
        Id = id, DisplayName = name, SizeBytes = size, Quality = quality, FitsThisPc = fits, Reason = reason, Recommended = recommended,
    };

    private static PendingApproval NewApproval() => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        AppId = "files-portal",
        AppName = "Files Portal",
        Reason = "Summarise documents you select.",
        CallerIdentity = "MindtheApp.FilesPortal_24e29cj9741rt",
        CallerVerified = true,
        CallerPortalApp = true,
        ExpiresAt = DateTimeOffset.Now.AddMinutes(2),
    };
}
