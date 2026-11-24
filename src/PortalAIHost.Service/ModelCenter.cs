using System.Collections.Concurrent;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Catalogue;
using Portal.AiHost.Core.Download;
using Portal.AiHost.Core.Planning;
using Portal.AiHost.Core.Store;
using Portal.AiHost.Platform.Windows.Pipes;

namespace PortalAIHost.Service;

/// <summary>What the model center needs from the rest of the host for removing the active model and for moves. All optional.</summary>
internal sealed class ModelCenterHooks
{
    /// <summary>Stops the engine of a kind and clears its active model ("no model"). Never touches files.</summary>
    public Func<string, CancellationToken, Task>? ClearActive { get; init; }

    /// <summary>Makes another installed model the active one (the old engine stops).</summary>
    public Func<string, CancellationToken, Task>? Activate { get; init; }

    /// <summary>Stops every engine and waits until the store no longer marks a model as in use.</summary>
    public Func<CancellationToken, Task>? StopEngines { get; init; }

    /// <summary>Points the active models at the current store after a move.</summary>
    public Func<CancellationToken, Task>? RefreshActive { get; init; }

    /// <summary>After a move: saves the new models folder and forgets the old store folder in settings.</summary>
    public Action<string, string?>? ApplyMovedFolder { get; init; }

    /// <summary>Free bytes on the drive that holds a folder; defaults to the real drive.</summary>
    public Func<string, long>? FreeBytesOf { get; init; }

    public Func<StoreProgressEvent, Task>? PushStore { get; init; }

    /// <summary>While set, requests that need a model fail with this message instead of loading files that are being copied. Null clears it.</summary>
    public Action<string?>? SetMaintenance { get; init; }

    /// <summary>Records a folder in settings (the earlier-folders list) before anything is written there, so it is never an orphan.</summary>
    public Action<string>? RememberFolder { get; init; }

    /// <summary>Drops a folder from the earlier-folders list once nothing is left in it.</summary>
    public Action<string>? ForgetFolder { get; init; }

    public ModelNotes? Notes { get; init; }

    /// <summary>How long removing the active model waits for the engine to let go of the files.</summary>
    public TimeSpan UnloadWait { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The models page behind the control pipe: catalogue plus installed models, downloads (start, pause, resume,
/// cancel) and remove. Files are only ever changed through the model store and the downloader, which keep to the
/// store rules (marker folder, recorded files only, external files only forgotten).
/// </summary>
internal sealed partial class ModelCenter : IAsyncDisposable
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
    private readonly object _opSync = new();
    private CancellationTokenSource? _op;
    private string? _opName;
    private IReadOnlyList<StoredModel>? _lastStored;
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

    /// <summary>Throws "models are being moved/checked" while a move or a check runs, so nothing changes the store meanwhile.</summary>
    public void AssertIdle()
    {
        lock (_opSync)
        {
            if (_op is not null)
            {
                throw new ControlPipeException(ErrorCodes.InvalidRequest,
                    _opName == StoreOperations.Move ? "Your models are being moved. Wait until that finishes." : _opName == StoreOperations.Cleanup ? "Space is being freed up. Wait until that finishes." : "A model is being checked. Wait until that finishes.");
            }
        }
    }

    /// <summary>Optional links to the rest of the host. Set once, right after construction.</summary>
    public ModelCenterHooks Hooks { get; init; } = new();

    /// <summary>False when no trusted model list was loaded (fail closed): only installed models are listed.</summary>
    public bool CatalogueAvailable => _catalogue is not null;

    /// <summary>Recommended upgrade over the active chat model, from the last listing or status check.</summary>
    public string? UpgradeModelId { get; private set; }

    public CatalogueEntry? Find(string id) => _catalogue?.FirstOrDefault(e => e.Id == id);

    public async Task<ModelListing> ListAsync(CancellationToken ct)
    {
        // A move holds the store's inventory for its whole run; the page keeps showing the last list meanwhile.
        var stored = _store is null ? []
            : _opName == StoreOperations.Move && _lastStored is { } cached ? cached
            : _lastStored = await _store.ListAsync(ct).ConfigureAwait(false);
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
                LastUsedAt = Hooks.Notes?.LastUsed(e.Id),
                Damaged = installed && Hooks.Notes?.DamagedReason(e.Id) is not null,
                DamagedReason = installed ? Hooks.Notes?.DamagedReason(e.Id) : null,
                AdviceSummary = e.Advice?.Summary,
                GoodFor = e.Advice is { GoodFor.Count: > 0 } a1 ? a1.GoodFor : null,
                Caveats = e.Advice is { Caveats.Count: > 0 } a2 ? a2.Caveats : null,
                MemoryNeededBytes = c.Candidate is { } cand ? cand.Plan.GpuBytes + cand.Plan.CpuBytes : 0,
                LicenceName = string.IsNullOrEmpty(e.Licence?.Spdx) ? null : e.Licence.Spdx,
                LicenceUrl = string.IsNullOrEmpty(e.Licence?.Url) ? null : e.Licence.Url,
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
                LastUsedAt = Hooks.Notes?.LastUsed(s.Id),
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
            ModelsBytes = stored.Where(m => m.Origin == ModelOrigin.Catalogue).Sum(m => m.Files.Sum(f => f.SizeBytes)),
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
            Settings: PerformanceModel.PlannerFor(s, hw));
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
        AssertIdle();
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
        AssertIdle();
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

    /// <summary>
    /// Removes a model through the store: host-created files only; an external model is only forgotten. The model in
    /// use may be removed too: the host first switches to <paramref name="switchToId"/> (another installed model of
    /// the same kind) or, with none, goes to "no model"; the engine is stopped and the removal waits until it let go
    /// of the files. Only <see cref="IModelStore.RemoveAsync"/> deletes anything.
    /// </summary>
    public async Task RemoveAsync(string id, CancellationToken ct, string? switchToId = null, bool removeDamaged = false)
    {
        AssertIdle();
        if (_store is null || await _store.GetAsync(id, ct).ConfigureAwait(false) is not { } model)
        {
            throw new ControlPipeException(ErrorCodes.NotFound, "That model is not on this PC.");
        }

        if (_running.ContainsKey(id))
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, "Pause or cancel the download first.");
        }

        var steppedAway = false;
        if (_activeId(model.Kind) == id)
        {
            steppedAway = true;
            await StepAwayFromActiveAsync(model, switchToId, ct).ConfigureAwait(false);
        }
        else if (_isLoaded(id))
        {
            // After a switch the old engine can still be stopping with this model's file open.
            throw new ControlPipeException(ErrorCodes.InvalidRequest, "This model is still loaded. Try again in a few seconds.");
        }

        await _push(new DownloadProgressEvent { CatalogueId = id, State = "removing" }).ConfigureAwait(false);
        try
        {
            if (removeDamaged && Hooks.Notes?.DamagedReason(id) is not null && _store is IModelStoreRepair repair)
            {
                // A check found this copy damaged and the user confirmed: size and file-ID checks are skipped, all others stay.
                await repair.RemoveDamagedAsync(id, ct).ConfigureAwait(false);
            }
            else
            {
                await _store.RemoveAsync(id, ct).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException ex)
        {
            await RestoreActiveAsync(id, steppedAway).ConfigureAwait(false);
            await PushRolledBackAsync(id, ex.Message).ConfigureAwait(false);
            throw new ControlPipeException(ErrorCodes.InvalidRequest, ex.Message);
        }
        catch (KeyNotFoundException)
        {
            await RestoreActiveAsync(id, steppedAway).ConfigureAwait(false);
            throw new ControlPipeException(ErrorCodes.NotFound, "That model is not on this PC.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Includes the store's safety refusals: nothing that failed a check was deleted.
            await RestoreActiveAsync(id, steppedAway).ConfigureAwait(false);
            await PushRolledBackAsync(id, ex.Message).ConfigureAwait(false);
            throw new ControlPipeException(ErrorCodes.InvalidRequest, "The model could not be removed: " + ex.Message);
        }

        _lastState.TryRemove(id, out _);
        Hooks.Notes?.ClearDamaged(id, forgetUsage: true);
        await _push(new DownloadProgressEvent { CatalogueId = id, State = "removed" }).ConfigureAwait(false);
    }

    /// <summary>A removal that failed after the model in use was set aside puts it back as the active model.</summary>
    private async Task RestoreActiveAsync(string id, bool steppedAway)
    {
        if (!steppedAway || Hooks.Activate is not { } activate)
        {
            return;
        }

        try
        {
            await activate(id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ControlPipeException or IOException or InvalidOperationException or KeyNotFoundException)
        {
            // The removal error is the one the user needs to see.
        }
    }

    private Task PushRolledBackAsync(string id, string message) =>
        _push(new DownloadProgressEvent { CatalogueId = id, State = "remove_failed", Message = message });

    /// <summary>Moves the active slot off a model that is about to be removed, then waits until no engine holds its files.</summary>
    private async Task StepAwayFromActiveAsync(StoredModel model, string? switchToId, CancellationToken ct)
    {
        if (switchToId is { Length: > 0 })
        {
            var next = await _store!.GetAsync(switchToId, ct).ConfigureAwait(false);
            if (next is not { IsComplete: true } || next.Kind != model.Kind || next.Id == model.Id)
            {
                throw new ControlPipeException(ErrorCodes.InvalidRequest, "That model cannot take over: it is not installed.");
            }

            if (Hooks.Activate is null)
            {
                throw new ControlPipeException(ErrorCodes.InvalidRequest, "This model is in use. Pick another model first.");
            }

            await Hooks.Activate(switchToId, ct).ConfigureAwait(false);
        }
        else
        {
            if (Hooks.ClearActive is null)
            {
                throw new ControlPipeException(ErrorCodes.InvalidRequest, "This model is in use. Pick another model first.");
            }

            await Hooks.ClearActive(model.Kind, ct).ConfigureAwait(false);
        }

        var deadline = DateTime.UtcNow + Hooks.UnloadWait;
        while (true)
        {
            var now = await _store!.GetAsync(model.Id, ct).ConfigureAwait(false);
            if (now is null || (!_isLoaded(model.Id) && now.Files.All(f => f.State != StoredFileState.InUse)))
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new ControlPipeException(ErrorCodes.InvalidRequest, "The model is still in use. Try again in a few seconds.");
            }

            await Task.Delay(25, ct).ConfigureAwait(false);
        }
    }

    // ---- storage: open folder, move, check ------------------------------------------------------

    /// <summary>The models folder, for the window to open in Explorer. Reads only.</summary>
    public string StoreFolder()
    {
        if (_store is null)
        {
            throw new ControlPipeException(ErrorCodes.FeatureDisabled, "The models folder is not available.");
        }

        var path = _store.Location.Path;
        if (!Directory.Exists(path))
        {
            throw new ControlPipeException(ErrorCodes.NotFound, "The models folder does not exist (yet).");
        }

        return path;
    }

    /// <summary>Cancels the running move or check, if any. A cancelled move leaves the old models folder as it was.</summary>
    public void CancelOperation()
    {
        lock (_opSync)
        {
            try
            {
                _op?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private CancellationTokenSource BeginOperation(string name)
    {
        lock (_opSync)
        {
            if (_op is not null)
            {
                throw new ControlPipeException(ErrorCodes.InvalidRequest,
                    _opName == StoreOperations.Move ? "Your models are being moved. Wait until that finishes." : _opName == StoreOperations.Cleanup ? "Space is being freed up. Wait until that finishes." : "A model is being checked. Wait until that finishes.");
            }

            _opName = name;
            return _op = new CancellationTokenSource();
        }
    }

    private void EndOperation(CancellationTokenSource cts)
    {
        lock (_opSync)
        {
            if (ReferenceEquals(_op, cts))
            {
                _op = null;
                _opName = null;
            }
        }

        cts.Dispose();
    }

    private Task PushStoreAsync(StoreProgressEvent e) => Hooks.PushStore?.Invoke(e) ?? Task.CompletedTask;

    /// <summary>
    /// Starts moving the models to another folder or drive and returns the running work (the pipe handler ignores it;
    /// progress arrives as events). Problems known up front are pipe errors, including "not enough space" with the
    /// amount needed. Copy, verify, switch, and only then the old copies are removed (see <see cref="ModelStore"/>).
    /// </summary>
    public async Task<Task> StartMoveAsync(string parentFolder, CancellationToken ct)
    {
        if (_store is not IModelStoreMover mover)
        {
            throw new ControlPipeException(ErrorCodes.FeatureDisabled, "The models folder is not available.");
        }

        if (_disposed)
        {
            throw new ControlPipeException(ErrorCodes.HostStopping, "The host is shutting down.");
        }

        if (!Path.IsPathFullyQualified(parentFolder))
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, "Pick a full folder path, for example D:\\Models.");
        }

        if (!_running.IsEmpty)
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, "Pause or finish the downloads before moving your models.");
        }

        var why = ControlHandler.CheckModelsFolder(parentFolder, mover.Location.Path) ?? mover.CheckMoveTarget(parentFolder);
        if (why is not null)
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, why);
        }

        var needed = mover.MoveBytesNeeded;
        var free = FreeBytesOfFolder(parentFolder);
        if (free < needed)
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest,
                $"Not enough space on that drive: moving needs {needed / 1e9:0.0} GB and it has {free / 1e9:0.0} GB free.");
        }

        await FinishPreviousMoveAsync(mover, ct).ConfigureAwait(false);
        if (mover.PreviousLocation is { } pendingOld)
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest,
                $"Your last move left old copies in '{pendingOld}'. Open Models and remove them under the earlier folder, or delete that folder yourself, then try again.");
        }

        var cts = BeginOperation(StoreOperations.Move);
        try
        {
            _lastStored = await _store.ListAsync(ct).ConfigureAwait(false);
            await PushStoreAsync(new StoreProgressEvent { Operation = StoreOperations.Move, State = StoreStates.Running }).ConfigureAwait(false);
        }
        catch
        {
            EndOperation(cts);
            throw;
        }

        return Task.Run(() => MoveCoreAsync(mover, parentFolder, cts), CancellationToken.None);
    }

    private long FreeBytesOfFolder(string folder)
    {
        if (Hooks.FreeBytesOf is { } f)
        {
            return f(folder);
        }

        try
        {
            return new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// If an earlier move left old copies behind (a crash, a locked file, an unplugged drive), tries to finish removing
    /// them, or forgets the pointer when that folder is gone. Runs at start and before a new move. Never blocks a move
    /// forever: what cannot be finished is named for the user.
    /// </summary>
    public async Task ReconcilePreviousMoveAsync(CancellationToken ct)
    {
        if (_store is not IModelStoreMover mover || mover.PreviousLocation is null)
        {
            return;
        }

        CancellationTokenSource cts;
        try
        {
            cts = BeginOperation(StoreOperations.Move);
        }
        catch (ControlPipeException)
        {
            return;
        }

        try
        {
            await FinishPreviousMoveAsync(mover, ct).ConfigureAwait(false);
            _lastStored = null;
        }
        finally
        {
            EndOperation(cts);
        }
    }

    private async Task FinishPreviousMoveAsync(IModelStoreMover mover, CancellationToken ct)
    {
        if (mover.PreviousLocation is not { } previous)
        {
            return;
        }

        Hooks.RememberFolder?.Invoke(previous); // stays listed (and removable) until nothing is left in it
        try
        {
            var left = await mover.RemoveOldCopiesAsync(ct).ConfigureAwait(false);
            if (left.Count == 0)
            {
                Hooks.ForgetFolder?.Invoke(previous);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Marker or store.json gone: nothing left to remove, so the pointer is dropped. A drive that is only
            // unplugged, or a file in use, keeps the pointer and the folder stays listed.
            if (await mover.ClearStalePreviousStoreAsync(ct).ConfigureAwait(false))
            {
                Hooks.ForgetFolder?.Invoke(previous);
            }
        }
    }

    private bool TryApplyMovedFolder(string parent, string? oldRoot)
    {
        if (Hooks.ApplyMovedFolder is not { } apply)
        {
            return true;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                apply(parent, oldRoot);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // retried once
            }
        }

        return false;
    }

    private async Task MoveCoreAsync(IModelStoreMover mover, string parent, CancellationTokenSource cts)
    {
        var switched = false;
        var target = ModelStore.StoreFolderIn(parent);
        Hooks.SetMaintenance?.Invoke("Your models are being moved. Try again in a minute.");
        try
        {
            if (Hooks.StopEngines is { } stop)
            {
                await stop(cts.Token).ConfigureAwait(false);
            }

            // Persisted before anything is written there: a crash or a lost drive leaves a listed folder, never an orphan.
            Hooks.RememberFolder?.Invoke(target);
            var progress = new SyncProgress(f => _ = PushStoreAsync(new StoreProgressEvent
            {
                Operation = StoreOperations.Move, State = StoreStates.Running, Fraction = f,
            }));
            await mover.MoveStoreAsync(parent, progress, cts.Token).ConfigureAwait(false);
            switched = true;
            var oldRoot = mover.PreviousLocation;
            var applied = TryApplyMovedFolder(parent, oldRoot);
            if (Hooks.RefreshActive is { } refresh)
            {
                await refresh(CancellationToken.None).ConfigureAwait(false);
            }

            _lastStored = await mover.ListAsync(CancellationToken.None).ConfigureAwait(false);
            if (!applied)
            {
                // The old copies are kept: settings still name the old folder, and the next start opens it.
                await PushStoreAsync(new StoreProgressEvent
                {
                    Operation = StoreOperations.Move, State = StoreStates.Done, Fraction = 1,
                    Message = "Your models were copied to the new folder, but the settings could not be saved and still point at the old folder. "
                        + "The old copies were kept. Pick the new folder in Settings again after fixing the problem.",
                }).ConfigureAwait(false);
                return;
            }

            await PushStoreAsync(new StoreProgressEvent
            {
                Operation = StoreOperations.Move, State = StoreStates.RemovingOld, Fraction = 1,
                Message = "Your models are in the new folder. Removing the old copies.",
            }).ConfigureAwait(false);
            IReadOnlyList<string> left = [];
            string? note = null;
            try
            {
                left = await mover.RemoveOldCopiesAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                note = "Your models were moved, but the old copies could not be removed and stay where they were: " + ex.Message;
            }

            if (note is null && left.Count == 0 && oldRoot is not null)
            {
                Hooks.ForgetFolder?.Invoke(oldRoot); // nothing left there; otherwise it stays listed
            }

            await PushStoreAsync(new StoreProgressEvent
            {
                Operation = StoreOperations.Move, State = StoreStates.Done, Fraction = 1,
                Message = note ?? (left.Count == 0 ? "Your models were moved." : "Your models were moved. Some old files were left in place."),
                LeftBehind = left.Count == 0 ? null : [.. left],
            }).ConfigureAwait(false);
        }
        catch (StoreCleanupIncompleteException ex) when (!switched)
        {
            // The folder stays remembered: its leftovers are listed under earlier folders and can be removed there.
            await PushStoreAsync(new StoreProgressEvent
            {
                Operation = StoreOperations.Move, State = StoreStates.Failed, Message = ex.Message,
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!switched)
        {
            ForgetIfGone(target);
            await PushStoreAsync(new StoreProgressEvent
            {
                Operation = StoreOperations.Move, State = StoreStates.Cancelled,
                Message = "The move was cancelled. Your models are where they were.",
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!switched)
            {
                ForgetIfGone(target);
            }

            await PushStoreAsync(new StoreProgressEvent
            {
                Operation = StoreOperations.Move, State = StoreStates.Failed,
                Message = switched ? "Your models were moved, but a step after that failed: " + ex.Message
                    : "The move did not finish and was undone. Your models are where they were. " + ex.Message,
            }).ConfigureAwait(false);
        }
        finally
        {
            Hooks.SetMaintenance?.Invoke(null);
            EndOperation(cts);
        }
    }

    private void ForgetIfGone(string folder)
    {
        if (!Directory.Exists(folder))
        {
            Hooks.ForgetFolder?.Invoke(folder);
        }
    }

    /// <summary>
    /// Re-hashes the files of an installed model against the model list's SHA-256 values and returns the running work.
    /// A mismatch marks the model damaged and offers nothing automatic: no file is deleted or changed.
    /// </summary>
    public async Task<Task> StartVerifyAsync(string id, CancellationToken ct)
    {
        if (_store is null || await _store.GetAsync(id, ct).ConfigureAwait(false) is not { } model)
        {
            throw new ControlPipeException(ErrorCodes.NotFound, "That model is not on this PC.");
        }

        if (!model.IsComplete)
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, "That model is not completely downloaded.");
        }

        if (model.Origin == ModelOrigin.External)
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, "A file you added yourself has no checksum in the model list, so it cannot be checked.");
        }

        var entry = Find(id) ?? throw new ControlPipeException(ErrorCodes.InvalidRequest, "That model is not in the model list, so there is nothing to check it against.");
        if (!entry.IsDownloadable)
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, "The model list has no verified checksums for this model.");
        }

        var cts = BeginOperation(StoreOperations.Verify);
        try
        {
            await PushStoreAsync(new StoreProgressEvent { Operation = StoreOperations.Verify, State = StoreStates.Running, CatalogueId = id, BytesTotal = model.TotalBytes }).ConfigureAwait(false);
        }
        catch
        {
            EndOperation(cts);
            throw;
        }

        return Task.Run(() => VerifyCoreAsync(model, entry, cts), CancellationToken.None);
    }

    private async Task VerifyCoreAsync(StoredModel model, CatalogueEntry entry, CancellationTokenSource cts)
    {
        var id = model.Id;
        var total = Math.Max(1, model.TotalBytes);
        long done = 0;
        var problems = new List<string>();
        try
        {
            foreach (var expected in entry.Files)
            {
                var stored = model.Files.FirstOrDefault(f => f.Name == expected.Name);
                if (stored is null || !File.Exists(stored.Path))
                {
                    problems.Add($"{expected.Name} is missing.");
                    continue;
                }

                var baseDone = done;
                var hash = await HashAsync(stored.Path, read =>
                {
                    _ = PushStoreAsync(new StoreProgressEvent
                    {
                        Operation = StoreOperations.Verify, State = StoreStates.Running, CatalogueId = id,
                        Fraction = Math.Min(1.0, (double)(baseDone + read) / total), BytesDone = baseDone + read, BytesTotal = model.TotalBytes,
                    });
                }, cts.Token).ConfigureAwait(false);
                done = baseDone + stored.SizeBytes;
                if (!string.Equals(hash, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{expected.Name} does not match the model list.");
                }
            }

            if (problems.Count == 0)
            {
                Hooks.Notes?.ClearDamaged(id);
                await PushStoreAsync(new StoreProgressEvent
                {
                    Operation = StoreOperations.Verify, State = StoreStates.Done, CatalogueId = id, Fraction = 1,
                    BytesDone = model.TotalBytes, BytesTotal = model.TotalBytes, Message = "All files are intact.",
                }).ConfigureAwait(false);
            }
            else
            {
                var reason = string.Join(" ", problems);
                if (_store is not null && await _store.GetAsync(id, CancellationToken.None).ConfigureAwait(false) is null)
                {
                    // Removed while it was being checked: nothing to mark.
                    await PushStoreAsync(new StoreProgressEvent
                    {
                        Operation = StoreOperations.Verify, State = StoreStates.Cancelled, CatalogueId = id, Message = "The model was removed.",
                    }).ConfigureAwait(false);
                    return;
                }

                Hooks.Notes?.MarkDamaged(id, reason);
                await PushStoreAsync(new StoreProgressEvent
                {
                    Operation = StoreOperations.Verify, State = StoreStates.Damaged, CatalogueId = id, Fraction = 1,
                    BytesDone = model.TotalBytes, BytesTotal = model.TotalBytes, Message = reason,
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await PushStoreAsync(new StoreProgressEvent
            {
                Operation = StoreOperations.Verify, State = StoreStates.Cancelled, CatalogueId = id, Message = "The check was cancelled.",
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await PushStoreAsync(new StoreProgressEvent
            {
                Operation = StoreOperations.Verify, State = StoreStates.Failed, CatalogueId = id,
                Message = "The check could not read the file: " + ex.Message,
            }).ConfigureAwait(false);
        }
        finally
        {
            EndOperation(cts);
        }
    }

    /// <summary>Read-only SHA-256 of a file with a byte counter. Shares the file with an engine that has it open.</summary>
    private static async Task<string> HashAsync(string path, Action<long> onRead, CancellationToken ct)
    {
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, useAsync: true);
        var buffer = new byte[1 << 20];
        long read = 0;
        var lastReport = Environment.TickCount64;
        int n;
        while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer, 0, n);
            read += n;
            if (Environment.TickCount64 - lastReport > 150)
            {
                lastReport = Environment.TickCount64;
                onRead(read);
            }
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
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

            if (p.State == "done")
            {
                owner.Hooks.Notes?.ClearDamaged(p.ModelId); // a fresh download replaces a damaged copy
            }

            owner.Push(new DownloadProgressEvent
            {
                CatalogueId = p.ModelId, State = p.State, BytesDone = p.BytesDone, BytesTotal = p.BytesTotal,
                EtaSeconds = p.EtaSeconds, Message = p.Message,
            });
        }
    }
}
