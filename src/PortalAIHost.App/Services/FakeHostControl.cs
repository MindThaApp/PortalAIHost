using Portal.AiHost.Contracts;
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

    public FakeHostControl(FakeScenario scenario, bool autoTick = true)
    {
        _models = new()
        {
            Model("qwen3-30b", "Qwen3 30B (large)", 18_000_000_000, 4.5, fits: false, "Needs more memory than this PC has free."),
            Model("chat-14b", "14B model (medium)", 9_000_000_000, 4.0, fits: true, "Best for your PC: good answers, and minutes in about 3 minutes.", recommended: true),
            Model("chat-8b", "8B model (small)", 5_000_000_000, 3.0, fits: true, "Quick and light. A good choice if you also play games."),
            Model("chat-3b", "3B model (tiny)", 2_500_000_000, 2.0, fits: true, "Works on any PC, but answers are simpler."),
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
                _clients.Add(new ClientSummary { TokenId = "t1", AppId = "voice-portal", AppName = "Voice Portal", DeviceId = "local", CreatedAt = DateTimeOffset.Now.AddDays(-3), LastUsedAt = DateTimeOffset.Now.AddMinutes(-12) });
                _clients.Add(new ClientSummary { TokenId = "t2", AppId = "pdf-portal", AppName = "PDF Portal Editor", DeviceId = "local", CreatedAt = DateTimeOffset.Now.AddDays(-1) });
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
#pragma warning restore CS0067

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
            return Task.FromResult(new ModelCatalogView(_models.Select(Copy).ToList(), existing, _settings.ModelsFolder ?? "", FreeBytes, upgrade));
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

    public Task RemoveModelAsync(string catalogueId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            RemoveCalls++;
            _downloads.Remove(catalogueId);
            var i = _models.FindIndex(m => m.Id == catalogueId);
            if (i >= 0) _models[i] = With(_models[i], installed: false, active: false);
            if (_activeId == catalogueId) _activeId = null;
        }
        return Task.CompletedTask;
    }

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

    public Task ActivateModelAsync(string catalogueId, CancellationToken ct = default)
    {
        lock (_gate) _activeId = catalogueId;
        StatusChanged?.Invoke(Snapshot());
        return Task.CompletedTask;
    }

    public Task<HostSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        Require();
        lock (_gate) return Task.FromResult(_settings);
    }

    public Task<SettingsSaved> SetSettingsAsync(HostSettings settings, CancellationToken ct = default)
    {
        bool moved;
        lock (_gate)
        {
            moved = !string.Equals(_settings.ModelsFolder, settings.ModelsFolder, StringComparison.OrdinalIgnoreCase);
            _settings = settings;
        }
        return Task.FromResult(new SettingsSaved { ModelsFolderChanged = moved, AppliesAtRestart = moved });
    }

    public async IAsyncEnumerable<TryItPiece> TryItAsync(string prompt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Require();
        if (prompt.Contains("[think]", StringComparison.Ordinal))
        {
            await Task.Delay(40, ct).ConfigureAwait(false);
            yield return new TryItPiece("Working out what to say. ", true);
        }

        foreach (var word in ("This is a sample reply from the practice host. You asked: " + prompt).Split(' '))
        {
            await Task.Delay(40, ct).ConfigureAwait(false);
            yield return new TryItPiece(word + " ");
        }
    }

    public Task CancelTryItAsync() => Task.CompletedTask;

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
            var loaded = Scenario is FakeScenario.Loaded or FakeScenario.ApprovalPending;
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
        FitsThisPc = m.FitsThisPc, Reason = m.Reason,
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
        CallerIdentity = "PortalApps.FilesPortal_8wekyb3d8bbwe",
        CallerVerified = true,
        ExpiresAt = DateTimeOffset.Now.AddMinutes(2),
    };
}
