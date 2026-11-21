using System.Collections.Concurrent;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Catalogue;
using Portal.AiHost.Core.Download;
using Portal.AiHost.Core.Planning;
using Portal.AiHost.Platform.Windows.Pipes;

namespace PortalAIHost.Service;

/// <summary>
/// The models page behind the control pipe: catalogue plus installed models, downloads (start, pause, resume,
/// cancel) and remove. Files are only ever changed through the model store and the downloader, which keep to the
/// store rules (marker folder, recorded files only, external files only forgotten).
/// </summary>
internal sealed class ModelCenter : IAsyncDisposable
{
    private readonly IModelStore? _store;
    private readonly IReadOnlyList<CatalogueEntry>? _catalogue;
    private readonly ModelDownloader? _downloader;
    private readonly IHardwareProbe? _hardware;
    private readonly Func<HostSettings> _settings;
    private readonly Func<string, string?> _activeId;
    private readonly Func<DownloadProgressEvent, Task> _push;
    private readonly ConcurrentDictionary<string, Running> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _lastState = new(StringComparer.Ordinal);
    private Task<HardwareProfile>? _profile;

    public ModelCenter(IModelStore? store, IReadOnlyList<CatalogueEntry>? catalogue, ModelDownloader? downloader,
        IHardwareProbe? hardware, Func<HostSettings> settings, Func<string, string?> activeId, Func<DownloadProgressEvent, Task> push)
    {
        _store = store;
        _catalogue = catalogue;
        _downloader = downloader;
        _hardware = hardware;
        _settings = settings;
        _activeId = activeId;
        _push = push;
    }

    /// <summary>False when no trusted model list was loaded (fail closed): only installed models are listed.</summary>
    public bool CatalogueAvailable => _catalogue is not null;

    /// <summary>Recommended upgrade over the active chat model, from the last listing or status check.</summary>
    public string? UpgradeModelId { get; private set; }

    public CatalogueEntry? Find(string id) => _catalogue?.FirstOrDefault(e => e.Id == id);

    public async Task<ModelListing> ListAsync(CancellationToken ct)
    {
        var stored = _store is null ? [] : await _store.ListAsync(ct).ConfigureAwait(false);
        var byId = stored.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var models = new List<CatalogueModelSummary>();
        var plans = await RecommendAsync(stored, ct).ConfigureAwait(false);

        foreach (var e in _catalogue ?? [])
        {
            byId.TryGetValue(e.Id, out var s);
            plans.TryGetValue(e.Id, out var c);
            var installed = s is { IsComplete: true };
            models.Add(new CatalogueModelSummary
            {
                Id = e.Id, DisplayName = e.DisplayName, Kind = e.Kind, Engine = e.Engine, SizeBytes = e.TotalBytes,
                Quality = e.Quality, Installed = installed, Active = _activeId(e.Kind) == e.Id,
                Recommended = c.Best, FitsThisPc = c.Candidate?.Plan.Fits ?? true, Reason = c.Candidate?.Reason,
                ExistingCopy = s is { Origin: ModelOrigin.External },
                DownloadState = installed ? null : DownloadStateFor(e.Id, s),
                BytesDone = installed || s is null ? 0 : s.Files.Sum(f => f.SizeBytes),
            });
        }

        // Installed models that are not (or no longer) in the catalogue, for example files the user added in place.
        foreach (var s in stored.Where(s => _catalogue?.Any(e => e.Id == s.Id) != true))
        {
            models.Add(new CatalogueModelSummary
            {
                Id = s.Id, DisplayName = s.DisplayName, Kind = s.Kind, Engine = s.Engine, SizeBytes = s.TotalBytes,
                Installed = s.IsComplete, Active = _activeId(s.Kind) == s.Id, FitsThisPc = true,
                ExistingCopy = s.Origin == ModelOrigin.External,
                DownloadState = s.IsComplete ? null : DownloadStateFor(s.Id, s),
                BytesDone = s.IsComplete ? 0 : s.Files.Sum(f => f.SizeBytes),
            });
        }

        return new ModelListing
        {
            Models = models,
            ModelsFolder = _store?.Location.Path,
            FreeBytes = FreeBytes(),
            UpgradeModelId = UpgradeModelId,
            CatalogueAvailable = CatalogueAvailable,
        };
    }

    private string? DownloadStateFor(string id, StoredModel? s) =>
        _running.ContainsKey(id) ? "downloading"
        : s is null ? null
        : _lastState.TryGetValue(id, out var last) && last is "failed" or "paused" ? last
        : "paused"; // a partial download left from an earlier run resumes from its .part files

    private long FreeBytes()
    {
        if (_store is null)
        {
            return 0;
        }

        try
        {
            return new DriveInfo(Path.GetPathRoot(_store.Location.Path)!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return _store.Location.FreeBytes;
        }
    }

    private async Task<Dictionary<string, (bool Best, Candidate? Candidate)>> RecommendAsync(IReadOnlyList<StoredModel> stored, CancellationToken ct)
    {
        var result = new Dictionary<string, (bool, Candidate?)>(StringComparer.Ordinal);
        if (_catalogue is null || _hardware is null)
        {
            return result;
        }

        HardwareProfile hw;
        try
        {
            hw = await (_profile ??= _hardware.ProbeAsync(CancellationToken.None)).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _profile = null;
            return result;
        }

        var s = _settings();
        var ctx = new RecommendContext(
            stored.Where(m => m.IsComplete).Select(m => m.Id).ToHashSet(StringComparer.Ordinal),
            FreeBytes(),
            Settings: new PlannerSettings { MaxMemoryPercent = s.MaxMemoryPercent, Backend = s.Backend });
        var mem = _hardware.GetMemory();
        foreach (var kind in new[] { ModelKinds.Chat, ModelKinds.Embedding })
        {
            var rec = Recommender.Recommend(_catalogue, kind, hw, mem, ctx);
            foreach (var c in rec.Candidates)
            {
                result[c.Entry.Id] = (rec.Best?.Entry.Id == c.Entry.Id, c);
            }

            if (kind == ModelKinds.Chat)
            {
                UpgradeModelId = HardwareRecheck.Run(null, hw, mem, _catalogue, kind, _activeId(kind), ctx).Upgrade?.ToModelId;
            }
        }

        return result;
    }

    // ---- downloads ------------------------------------------------------------------------------

    /// <summary>Starts a download, or resumes a paused or failed one from its partial file.</summary>
    public Task StartAsync(string id)
    {
        var entry = Find(id) ?? throw new ControlPipeException(ErrorCodes.NotFound,
            CatalogueAvailable ? "That model is not in the model list." : "The model list is unavailable.");
        var downloader = _downloader ?? throw new ControlPipeException(ErrorCodes.FeatureDisabled, "The models folder is not available.");
        var run = new Running();
        if (!_running.TryAdd(id, run))
        {
            run.Cts.Dispose();
            return Task.CompletedTask; // already downloading
        }

        _lastState.TryRemove(id, out _);
        var progress = new Progress(this);
        run.Task = Task.Run(async () =>
        {
            try
            {
                await downloader.DownloadAsync(entry, progress, run.Cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The downloader already reported paused or failed; the partial files stay for a resume.
            }
            finally
            {
                _running.TryRemove(new KeyValuePair<string, Running>(id, run));
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>Stops a download and keeps its partial file, so a resume continues from there.</summary>
    public async Task PauseAsync(string id)
    {
        if (!_running.TryGetValue(id, out var run))
        {
            return;
        }

        await run.Cts.CancelAsync().ConfigureAwait(false);
        await run.Task.ConfigureAwait(false);
        _lastState[id] = "paused";
    }

    /// <summary>Stops a download and has the downloader discard its unfinished files (finished models are refused).</summary>
    public async Task CancelAsync(string id, CancellationToken ct)
    {
        await PauseAsync(id).ConfigureAwait(false);
        if (_downloader is null)
        {
            return;
        }

        try
        {
            await _downloader.CancelAsync(id, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, ex.Message);
        }

        _lastState.TryRemove(id, out _);
        await _push(new DownloadProgressEvent { CatalogueId = id, State = "cancelled" }).ConfigureAwait(false);
    }

    /// <summary>Removes a model through the store: host-created files only; an external model is only forgotten.</summary>
    public async Task RemoveAsync(string id, CancellationToken ct)
    {
        if (_store is null || await _store.GetAsync(id, ct).ConfigureAwait(false) is not { } model)
        {
            throw new ControlPipeException(ErrorCodes.NotFound, "That model is not on this PC.");
        }

        if (_running.ContainsKey(id))
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, "Pause or cancel the download first.");
        }

        if (_activeId(model.Kind) == id)
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, "This model is in use. Pick another model first.");
        }

        try
        {
            await _store.RemoveAsync(id, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, ex.Message);
        }

        _lastState.TryRemove(id, out _);
        await _push(new DownloadProgressEvent { CatalogueId = id, State = "removed" }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _running.Keys.ToList())
        {
            await PauseAsync(id).ConfigureAwait(false);
        }
    }

    private sealed class Running
    {
        public CancellationTokenSource Cts { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private sealed class Progress(ModelCenter owner) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress p)
        {
            if (p.State is "failed" or "paused")
            {
                owner._lastState[p.ModelId] = p.State;
            }

            _ = owner._push(new DownloadProgressEvent
            {
                CatalogueId = p.ModelId, State = p.State, BytesDone = p.BytesDone, BytesTotal = p.BytesTotal,
                EtaSeconds = p.EtaSeconds, Message = p.Message,
            });
        }
    }
}
