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
    private readonly Func<string, bool> _isLoaded;
    private readonly List<IModelStore> _previous;
    private readonly Func<string, IModelStore> _openPrevious;
    private readonly Func<DownloadProgressEvent, Task> _push;
    private readonly ConcurrentDictionary<string, Running> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _lastState = new(StringComparer.Ordinal);
    private readonly object _profileSync = new();
    private Task<HardwareProfile>? _profile;
    private volatile bool _disposed;

    /// <param name="isLoaded">True while an engine still runs with that model (for example while it stops after a switch).</param>
    public ModelCenter(IModelStore? store, IReadOnlyList<CatalogueEntry>? catalogue, ModelDownloader? downloader,
        IHardwareProbe? hardware, Func<HostSettings> settings, Func<string, string?> activeId, Func<DownloadProgressEvent, Task> push,
        Func<string, bool>? isLoaded = null, IReadOnlyList<IModelStore>? previousStores = null,
        Func<string, IModelStore>? openPrevious = null)
    {
        _previous = previousStores?.ToList() ?? [];
        _openPrevious = openPrevious ?? (path => Portal.AiHost.Core.Store.ModelStore.Open(path));
        _store = store;
        _catalogue = catalogue;
        _downloader = downloader;
        _hardware = hardware;
        _settings = settings;
        _activeId = activeId;
        _isLoaded = isLoaded ?? (_ => false);
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

        // Models left in earlier models folders: listed so their space can be freed; remove only.
        IModelStore[] previousNow;
        lock (_previous)
        {
            previousNow = [.. _previous];
        }

        foreach (var old in previousNow)
        {
            IReadOnlyList<StoredModel> oldModels;
            try
            {
                oldModels = await old.ListAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var s in oldModels)
            {
                models.Add(new CatalogueModelSummary
                {
                    Id = s.Id, DisplayName = s.DisplayName, Kind = s.Kind, Engine = s.Engine, SizeBytes = s.TotalBytes,
                    Installed = s.IsComplete, FitsThisPc = true, ExistingCopy = s.Origin == ModelOrigin.External,
                    StoreFolder = old.Location.Path,
                });
            }
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

    /// <summary>Starts the hardware check if it has not run yet and returns its result once it is ready, else null.</summary>
    public HardwareProfile? ProfileIfReady()
    {
        if (_hardware is null)
        {
            return null;
        }

        lock (_profileSync)
        {
            var hardware = _hardware;
            var probe = _profile ??= Task.Run(() => hardware.ProbeAsync(CancellationToken.None));
            return probe.IsCompletedSuccessfully ? probe.Result : null;
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
        Task<HardwareProfile> probe;
        lock (_profileSync)
        {
            var hardware = _hardware;
            probe = _profile ??= Task.Run(() => hardware.ProbeAsync(CancellationToken.None));
        }

        try
        {
            hw = await probe.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            lock (_profileSync)
            {
                if (ReferenceEquals(_profile, probe))
                {
                    _profile = null; // a later listing probes again
                }
            }

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
                // An upgrade only makes sense when a model is in use now; with none, the plain recommendation applies.
                var current = _activeId(kind);
                UpgradeModelId = current is null
                    ? null
                    : HardwareRecheck.Run(null, hw, mem, _catalogue, kind, current, ctx).Upgrade?.ToModelId;
            }
        }

        return result;
    }

    // ---- downloads ------------------------------------------------------------------------------

    /// <summary>
    /// Starts a download, or resumes a paused or failed one from its partial file. Problems known up front (no
    /// model list, not downloadable, a file the user added, not enough space) are answered as pipe errors.
    /// </summary>
    public async Task StartAsync(string id, CancellationToken ct = default)
    {
        if (_disposed)
        {
            throw new ControlPipeException(ErrorCodes.HostStopping, "The host is shutting down.");
        }

        if (!CatalogueAvailable)
        {
            throw new ControlPipeException(ErrorCodes.FeatureDisabled, "The model list is unavailable, so nothing can be downloaded.");
        }

        var entry = Find(id) ?? throw new ControlPipeException(ErrorCodes.NotFound, "That model is not in the model list.");
        var downloader = _downloader ?? throw new ControlPipeException(ErrorCodes.FeatureDisabled, "The models folder is not available.");
        if (_running.ContainsKey(id))
        {
            return; // already downloading
        }

        if (!entry.IsDownloadable)
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, "This model cannot be downloaded: the model list has no verified files for it.");
        }

        if (_store is not null && await _store.GetAsync(id, ct).ConfigureAwait(false) is { Origin: ModelOrigin.External })
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, "This model is a file you added on this PC; it is not downloaded.");
        }

        try
        {
            await downloader.CheckSpaceAsync(entry, ct).ConfigureAwait(false);
        }
        catch (InsufficientDiskSpaceException ex)
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, ex.Message);
        }

        var run = new Running();
        if (_disposed || !_running.TryAdd(id, run))
        {
            run.Dispose();
            return; // already downloading, or the host started shutting down
        }

        _lastState.TryRemove(id, out _);
        var progress = new Progress(this);
        var token = run.Token;
        run.Task = Task.Run(async () =>
        {
            try
            {
                await downloader.DownloadAsync(entry, progress, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The downloader normally reports paused or failed itself (the partial files stay for a resume).
                // Some refusals happen before it reports anything; say so here, so no failure is silent.
                if (!progress.TerminalSent)
                {
                    var state = ex is OperationCanceledException or InsufficientDiskSpaceException or DiskFullException ? "paused" : "failed";
                    progress.Report(new DownloadProgress(id, state, 0, entry.TotalBytes, null,
                        ex is OperationCanceledException ? null : ex.Message));
                }
            }
            finally
            {
                _running.TryRemove(new KeyValuePair<string, Running>(id, run));
                run.Dispose();
            }
        }, CancellationToken.None);
    }

    /// <summary>Stops a download and keeps its partial file, so a resume continues from there.</summary>
    public async Task PauseAsync(string id)
    {
        if (!_running.TryGetValue(id, out var run))
        {
            return;
        }

        run.Cancel();
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

    /// <summary>
    /// Removes a model from an earlier models folder, through that folder's own store rules (marker, recorded
    /// files only, external files only forgotten). The folder itself is never deleted.
    /// </summary>
    public async Task RemoveFromPreviousAsync(string id, string storeFolder, CancellationToken ct)
    {
        IModelStore? known;
        lock (_previous)
        {
            known = _previous.FirstOrDefault(s => string.Equals(s.Location.Path, storeFolder, StringComparison.OrdinalIgnoreCase));
        }

        if (known is null)
        {
            throw new ControlPipeException(ErrorCodes.NotFound, "That folder is not an earlier models folder of this host.");
        }

        // Opened only for this removal and closed again straight away, so the drive can be ejected afterwards.
        IModelStore old;
        try
        {
            old = _openPrevious(known.Location.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, "That models folder could not be opened: " + ex.Message);
        }

        using (old as IDisposable)
        {
            if (await old.GetAsync(id, ct).ConfigureAwait(false) is null)
            {
                throw new ControlPipeException(ErrorCodes.NotFound, "That model is not in that folder.");
            }

            try
            {
                await old.RemoveAsync(id, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                throw new ControlPipeException(ErrorCodes.InvalidRequest, ex.Message);
            }
            catch (KeyNotFoundException)
            {
                throw new ControlPipeException(ErrorCodes.NotFound, "That model is not in that folder.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ControlPipeException(ErrorCodes.InvalidRequest, "The model could not be removed: " + ex.Message);
            }

            var fresh = PreviousStoreSnapshot.Capture(old);
            lock (_previous)
            {
                var i = _previous.FindIndex(x => ReferenceEquals(x, known));
                if (i >= 0)
                {
                    _previous[i] = fresh;
                }
            }
        }
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

        // After a switch the old engine can still be stopping with this model's file open.
        if (_isLoaded(id))
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, "This model is still loaded. Try again in a few seconds.");
        }

        try
        {
            await _store.RemoveAsync(id, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, ex.Message);
        }
        catch (KeyNotFoundException)
        {
            throw new ControlPipeException(ErrorCodes.NotFound, "That model is not on this PC.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Includes the store's safety refusals: nothing that failed a check was deleted.
            throw new ControlPipeException(ErrorCodes.InvalidRequest, "The model could not be removed: " + ex.Message);
        }

        _lastState.TryRemove(id, out _);
        await _push(new DownloadProgressEvent { CatalogueId = id, State = "removed" }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var id in _running.Keys.ToList())
        {
            await PauseAsync(id).ConfigureAwait(false);
        }
    }

    /// <summary>Fire-and-forget push whose failure (for example a closed pipe) is observed, never left unobserved.</summary>
    private void Push(DownloadProgressEvent e)
    {
        Task push;
        try
        {
            push = _push(e);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return;
        }

        _ = push.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private sealed class Running : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly object _sync = new();
        private bool _disposed;

        public Running() => Token = _cts.Token;

        public CancellationToken Token { get; }

        public Task Task { get; set; } = Task.CompletedTask;

        public void Cancel()
        {
            lock (_sync)
            {
                if (!_disposed)
                {
                    _cts.Cancel();
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            _cts.Dispose();
        }
    }

    private sealed class Progress(ModelCenter owner) : IProgress<DownloadProgress>
    {
        private volatile bool _terminal;

        /// <summary>True once done, paused or failed was reported for this run.</summary>
        public bool TerminalSent => _terminal;

        public void Report(DownloadProgress p)
        {
            if (p.State is "done" or "failed" or "paused")
            {
                _terminal = true;
            }

            if (p.State is "failed" or "paused")
            {
                owner._lastState[p.ModelId] = p.State;
            }

            owner.Push(new DownloadProgressEvent
            {
                CatalogueId = p.ModelId, State = p.State, BytesDone = p.BytesDone, BytesTotal = p.BytesTotal,
                EtaSeconds = p.EtaSeconds, Message = p.Message,
            });
        }
    }
}
