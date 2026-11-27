using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Api;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Runtime;

namespace Portal.AiHost.Server;

/// <summary>An installed, complete model as a device may see it.</summary>
public sealed record InstalledModel(string Id, string DisplayName, string Kind, long SizeBytes, bool FitsThisPc);

/// <summary>What Model control needs from the host's model store. Read-only: nothing here writes or deletes a file.</summary>
public interface IModelControlHost
{
    /// <summary>Installed models of the current models folder (no downloads, no earlier folders, no damaged models).</summary>
    Task<IReadOnlyList<InstalledModel>> ListInstalledAsync(CancellationToken ct);

    /// <summary>The model ready to be made active, or null when it is not installed.</summary>
    Task<ActiveModel?> ResolveAsync(string modelId, CancellationToken ct);

    /// <summary>Called after the loaded models changed, so the windows refresh.</summary>
    Task ChangedAsync();
}

/// <summary>
/// Model control (docs/client-mode-v1.md 4.7 and 5.1a): the models a paired device with the grant loaded on this PC.
/// The PC decides by default; a device may load and unload models only while it holds the grant, the PC owner's own
/// action always wins, and nothing here ever touches a model file (unload means "out of memory").
/// Each kind has two engine slots when the host has an engine for the second one (docs/client-mode-v1.md 4.7): the PC's own
/// model lives in the first slot, a device's second model goes to the second slot without displacing the PC's. A slot is
/// named by its key (<see cref="ModelSlots"/>), and the "loaded by" table, the owner-change epoch and the conflict rules
/// are all kept per slot.
/// </summary>
public sealed class ModelControl
{
    private sealed class Entry
    {
        /// <summary>The slot key this entry holds: the kind (first slot) or <c>kind.2</c> (second slot).</summary>
        public required string Kind { get; init; }
        public required string ModelId { get; init; }
        public required string DisplayName { get; init; }
        public required string DeviceId { get; init; }
        public required string Placement { get; init; }
        public required PlacementOverride? Override { get; init; }

        /// <summary>
        /// The id of the PC's own choice for this kind before the device loaded. It is put back when the device's model
        /// goes, looked up again then (it may have been removed meanwhile), so no stale model is ever kept here.
        /// </summary>
        public string? PreviousId { get; set; }

        /// <summary>True once the engine finished loading. Only then does a stopped engine end the entry.</summary>
        public bool Confirmed { get; set; }
    }

    private readonly AiHostRuntime _runtime;
    private readonly DeviceStore _devices;
    private readonly IModelControlHost _host;
    private readonly Func<bool> _allowReplace;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _loaded = new(StringComparer.Ordinal);
    private readonly HashSet<(string Device, string Model)> _displaced = [];
    private readonly Dictionary<string, long> _epoch = new(StringComparer.Ordinal);
    private const int MaxDisplaced = 256;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ModelControl(AiHostRuntime runtime, DeviceStore devices, IModelControlHost host, Func<bool> allowReplace)
    {
        _runtime = runtime;
        _devices = devices;
        _host = host;
        _allowReplace = allowReplace;
        runtime.EngineStateChanged += (_, change) =>
        {
            if (change.State is EngineState.Stopped or EngineState.Failed)
            {
                _ = ReconcileQuietlyAsync(change.Kind);
            }
        };
    }

    /// <summary>Raised when who loaded what changed.</summary>
    public event Action? Changed;

    // ---- reading ---------------------------------------------------------------------------------

    /// <summary>The models a device loaded that are still loaded, with the device's name.</summary>
    public IReadOnlyList<LoadedModelInfo> Loaded()
    {
        lock (_lock)
        {
            return [.. _loaded.Values.Select(e => new LoadedModelInfo
            {
                Kind = ModelSlots.KindOf(e.Kind), ModelId = e.ModelId, DisplayName = e.DisplayName, DeviceId = e.DeviceId,
                DeviceName = _devices.Find(e.DeviceId)?.Name ?? "a removed device", Placement = e.Placement,
            })];
        }
    }

    /// <summary>
    /// The model the PC itself chose for a kind while a device's model is active. Held only while the entry's model is
    /// still the active one; otherwise the active model is the PC's own and nothing is held.
    /// </summary>
    public (bool Held, string? PcModelId) PcChoice(string kind, string? activeModelId)
    {
        lock (_lock)
        {
            return _loaded.TryGetValue(kind, out var e) && e.ModelId == activeModelId ? (true, e.PreviousId) : (false, null);
        }
    }

    /// <summary>The PC's saved choice is no longer installed: forget it, so the kind falls back to "no model" on restore.</summary>
    public void ForgetPcChoice(string kind)
    {
        lock (_lock)
        {
            if (_loaded.TryGetValue(kind, out var e))
            {
                e.PreviousId = null;
            }
        }
    }

    /// <summary>A device was removed or lost its grant: what it was told about earlier owner changes is of no use any more.</summary>
    public void ForgetDevice(string deviceId)
    {
        lock (_lock)
        {
            _displaced.RemoveWhere(d => d.Device == deviceId);
        }
    }

    // ---- the PC owner's side ----------------------------------------------------------------------

    private long EpochOf(string kind)
    {
        lock (_lock)
        {
            return _epoch.GetValueOrDefault(kind);
        }
    }

    /// <summary>Counts an owner-side change of a kind. A device operation that started earlier sees the change and stops. Call under <c>_lock</c>.</summary>
    private long BumpLocked(string kind) => _epoch[kind] = _epoch.GetValueOrDefault(kind) + 1;

    /// <summary>
    /// The PC owner is about to change this kind (load, unload or pick another model). The device's claim ends first and the PC's
    /// own choice comes back, then the owner's action runs. A device operation in flight for this kind is cancelled
    /// (it never touches the engine again), and the device sees <c>owner_changed</c>.
    /// </summary>
    public async Task OwnerChangedAsync(string kind, CancellationToken ct, string? newModelId = null)
    {
        var (entry, epoch) = Remove(kind, displaced: true);
        if (entry is not null)
        {
            await RestoreAsync(entry, epoch, stopEngine: false, ct).ConfigureAwait(false);
            Changed?.Invoke();
        }

        // The owner picked the model a device holds in the second slot: one model is never in both slots, so the
        // device's copy goes (its engine stops, memory only).
        var second = ModelSlots.Second(kind);
        if (newModelId is not null && !ModelSlots.IsSecond(kind))
        {
            bool same;
            lock (_lock)
            {
                same = _loaded.TryGetValue(second, out var held) && held.ModelId == newModelId;
            }

            if (same && Remove(second, displaced: true) is ({ } e2, var ep2))
            {
                await RestoreAsync(e2, ep2, stopEngine: true, ct).ConfigureAwait(false);
                Changed?.Invoke();
            }
        }
    }

    /// <summary>"Take back": unloads the model a device loaded for this kind (memory only) and gives the PC's choice back.</summary>
    public async Task<bool> TakeBackAsync(string kind, CancellationToken ct)
    {
        // Both slots of the kind: whatever a device loaded there goes (the PC's own model in the first slot is never stopped).
        var any = false;
        foreach (var key in ModelSlots.IsSecond(kind) ? [kind] : new[] { kind, ModelSlots.Second(kind) })
        {
            var (entry, epoch) = Remove(key, displaced: true);
            if (entry is null)
            {
                continue;
            }

            any = true;
            await RestoreAsync(entry, epoch, stopEngine: true, ct).ConfigureAwait(false);
        }

        if (any)
        {
            Changed?.Invoke();
            await _host.ChangedAsync().ConfigureAwait(false);
        }

        return any;
    }

    // ---- the device's side ------------------------------------------------------------------------

    private static ApiException Conflict(string reason, string message) =>
        new(ErrorCodes.ModelConflict, message) { Details = Reason(reason) };

    private static Dictionary<string, JsonElement> Reason(string reason) =>
        new() { ["reason"] = ReasonElement(reason) };

    private static JsonElement ReasonElement(string reason)
    {
        // The reasons are fixed identifiers (no quotes or escapes), never request text.
        using var doc = JsonDocument.Parse($"\"{reason}\"");
        return doc.RootElement.Clone();
    }

    /// <summary>The installed models. With a device id, only the models that device may use (its allowed list; empty means all).</summary>
    public async Task<DeviceModelList> ListAsync(CancellationToken ct, string? deviceId = null)
    {
        await ReconcileAsync(null, ct).ConfigureAwait(false);
        var installed = await _host.ListInstalledAsync(ct).ConfigureAwait(false);
        var list = new DeviceModelList();
        foreach (var m in installed.Where(m => deviceId is null || MayUse(deviceId, m.Id)))
        {
            list.Models.Add(StateOf(m));
        }

        return list;
    }

    private DeviceModelState StateOf(InstalledModel m)
    {
        var slot = SlotHolding(m.Kind, m.Id);
        var active = _runtime.GetActive(slot);
        var engine = _runtime.EngineStateOf(slot);
        var isActive = active?.ModelId == m.Id;
        var state = !isActive ? "unloaded"
            : engine == EngineState.Ready ? "loaded"
            : engine is EngineState.Starting or EngineState.Loading ? "loading"
            : "unloaded";
        Entry? entry;
        lock (_lock)
        {
            _loaded.TryGetValue(slot, out entry);
        }

        var byDevice = entry is not null && entry.ModelId == m.Id;
        return new DeviceModelState
        {
            Id = m.Id, DisplayName = m.DisplayName, Kind = m.Kind, SizeBytes = m.SizeBytes, FitsThisPc = m.FitsThisPc,
            State = state,
            Placement = state == "unloaded" ? null : byDevice ? entry!.Placement : DevicePlacements.Auto,
            LoadedBy = state == "unloaded" ? null : byDevice ? entry!.DeviceId : "pc",
        };
    }

    /// <summary>The slot key where this model is the active one (second slot first, else the first slot).</summary>
    private string SlotHolding(string kind, string modelId)
    {
        var second = ModelSlots.Second(kind);
        return _runtime.GetActive(second)?.ModelId == modelId ? second : kind;
    }

    /// <summary>
    /// A request from a granted device named an installed model that is not loaded: load it under the same rules as
    /// <see cref="LoadAsync"/> (grant, the device's allowed list, conflict rules, memory). Does nothing without the grant
    /// or for a model that is not installed with this kind; a refusal throws the same <see cref="ApiException"/> a load
    /// would, which the caller treats as "serve the request as it was".
    /// </summary>
    public async Task AutoLoadAsync(string deviceId, string modelId, string kind, CancellationToken ct)
    {
        if (!Granted(deviceId) || !MayUse(deviceId, modelId))
        {
            return;
        }

        var installed = (await _host.ListInstalledAsync(ct).ConfigureAwait(false)).FirstOrDefault(m => m.Id == modelId);
        if (installed is null || installed.Kind != kind)
        {
            return;
        }

        await ReplaceAsync(deviceId, modelId, DevicePlacements.Auto, null, requireOwn: false, ct).ConfigureAwait(false);
    }

    /// <summary>Loads an installed model for a device. Returns when the model is ready.</summary>
    public async Task<DeviceModelState> LoadAsync(string deviceId, DeviceLoadRequest request, CancellationToken ct)
    {
        var placement = request.Placement ?? DevicePlacements.Auto;
        if (string.IsNullOrEmpty(request.ModelId) || !DevicePlacements.IsValid(placement))
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "model_id is required and placement must be auto, gpu or cpu.");
        }

        var over = placement == DevicePlacements.Auto ? null : new PlacementOverride(placement, null, null, null);
        return await ReplaceAsync(deviceId, request.ModelId, placement, over, requireOwn: false, ct).ConfigureAwait(false);
    }

    /// <summary>Sets the placement of a model this device loaded (the model is loaded again with it).</summary>
    public async Task<DevicePlacementRequest> SetPlacementAsync(string deviceId, DevicePlacementRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.ModelId) || !DevicePlacements.IsValid(request.Compute) ||
            request.GpuMemoryLimitBytes is <= 0 || request.MemoryLimitBytes is <= 0 || request.CpuThreads is <= 0)
        {
            throw new ApiException(ErrorCodes.InvalidRequest,
                "model_id is required; compute must be auto, gpu or cpu; limits must be positive.");
        }

        // Never more threads than this PC has (the PC's own limit is applied on top when it sets one).
        var threads = request.CpuThreads is { } t ? Math.Min(t, Environment.ProcessorCount) : (int?)null;
        var over = new PlacementOverride(request.Compute, request.GpuMemoryLimitBytes, request.MemoryLimitBytes, threads);
        await ReplaceAsync(deviceId, request.ModelId, request.Compute, over, requireOwn: true, ct).ConfigureAwait(false);
        request.CpuThreads = threads;
        return request;
    }

    public DevicePlacementList PlacementsOf(string deviceId)
    {
        lock (_lock)
        {
            return new DevicePlacementList
            {
                Placements = [.. _loaded.Values.Where(e => e.DeviceId == deviceId).Select(e => new DevicePlacementRequest
                {
                    ModelId = e.ModelId, Compute = e.Placement, GpuMemoryLimitBytes = e.Override?.GpuMemoryLimitBytes,
                    MemoryLimitBytes = e.Override?.MemoryLimitBytes, CpuThreads = e.Override?.CpuThreads,
                })],
            };
        }
    }

    /// <summary>The device's allowed-models list lets it use the model (an empty list allows every model).</summary>
    private bool MayUse(string deviceId, string modelId) =>
        _devices.Find(deviceId) is not { } d || d.AllowedModels.Count == 0 || d.AllowedModels.Contains(modelId, StringComparer.Ordinal);

    private bool Granted(string deviceId) => _devices.Find(deviceId)?.ModelControl == true;

    private static ApiException NoGrant() =>
        new(ErrorCodes.ModelControlRequired, "This device does not have Model control. Ask the owner of this PC to turn it on.");

    private static ApiException OwnerChanged() =>
        Conflict(ModelConflictReasons.OwnerChanged, "The owner of this PC changed the model.");

    private async Task<DeviceModelState> ReplaceAsync(string deviceId, string modelId, string placementWord,
        PlacementOverride? over, bool requireOwn, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The grant may have been taken back while this call waited.
            if (!Granted(deviceId))
            {
                throw NoGrant();
            }

            await ReconcileAsync(null, ct).ConfigureAwait(false);
            var installed = (await _host.ListInstalledAsync(ct).ConfigureAwait(false)).FirstOrDefault(m => m.Id == modelId);
            var resolved = installed is null ? null : await _host.ResolveAsync(modelId, ct).ConfigureAwait(false);
            if (installed is null || resolved is null)
            {
                throw new ApiException(ErrorCodes.NotFound, "That model is not installed.");
            }

            if (!MayUse(deviceId, modelId))
            {
                throw new ApiException(ErrorCodes.ModelNotFound, "This device may not use that model.");
            }

            var modelKind = resolved.Kind;
            var first = modelKind;
            var second = ModelSlots.Second(modelKind);
            var hasSecond = _runtime.HasSecondSlot(modelKind);

            // Both slots' epochs are read before anything else: any owner-side change of the chosen slot after this point cancels the call.
            var epoch1 = EpochOf(first);
            var epoch2 = EpochOf(second);
            Entry? held1, held2;
            lock (_lock)
            {
                _loaded.TryGetValue(first, out held1);
                _loaded.TryGetValue(second, out held2);
            }

            var active1 = _runtime.GetActive(first);
            var active2 = hasSecond ? _runtime.GetActive(second) : null;
            var kind = requireOwn
                ? (held2 is not null && held2.DeviceId == deviceId && held2.ModelId == modelId ? second : first)
                : ChooseSlot(deviceId, modelId, hasSecond, first, second, held1, held2, active1, active2);
            var isSecond = kind == second;
            var epoch = isSecond ? epoch2 : epoch1;
            var active = isSecond ? active2 : active1;
            var engine = _runtime.EngineStateOf(kind);
            var running = engine is EngineState.Starting or EngineState.Loading or EngineState.Ready;
            var held = isSecond ? held2 : held1;

            if (requireOwn)
            {
                if (held is null || held.DeviceId != deviceId || held.ModelId != modelId)
                {
                    throw NotYours(deviceId, modelId);
                }
            }
            else if (running && active?.ModelId == modelId)
            {
                return StateOf(installed); // already loaded (by the PC or a device): nothing changes hands
            }

            if (!requireOwn)
            {
                if (held is not null && held.DeviceId != deviceId)
                {
                    throw Conflict(ModelConflictReasons.InUseByDevice, "Another device loaded a model on this PC.");
                }

                // The PC's own chosen model is protected whether or not its engine is running right now.
                if (!isSecond && held is null && active is not null && active.ModelId != modelId && !_allowReplace())
                {
                    throw Conflict(ModelConflictReasons.InUseByHost, "This PC's own apps are using a model.");
                }
            }

            var next = resolved with { Kind = kind, Override = over };

            // Nothing is evicted before the new model is known to fit (the planner checks with this kind's memory free).
            await _runtime.DecidePlacementAsync(next, ct).ConfigureAwait(false);

            var entry = new Entry
            {
                Kind = kind, ModelId = modelId, DisplayName = resolved.DisplayName, DeviceId = deviceId,
                Placement = placementWord, Override = over,
                PreviousId = isSecond ? null : held is not null ? held.PreviousId : active?.ModelId, // the second slot never holds a PC choice
            };
            lock (_lock)
            {
                if (_epoch.GetValueOrDefault(kind) != epoch || !Granted(deviceId))
                {
                    throw _epoch.GetValueOrDefault(kind) != epoch ? OwnerChanged() : NoGrant();
                }

                _loaded[kind] = entry;
                _displaced.Remove((deviceId, modelId));
            }

            var aborted = false;
            try
            {
                // The model in memory goes only when it differs or a placement changed (an explicit stop first).
                if (running && active is not null && (active.ModelId != modelId || requireOwn))
                {
                    if (EpochOf(kind) != epoch)
                    {
                        aborted = true;
                        throw OwnerChanged();
                    }

                    await _runtime.UnloadAsync(kind, ct).ConfigureAwait(false); // engine unload: memory only
                }

                // The check and the change of the active model happen together under the lock, so an owner action
                // (which counts the epoch under the same lock) either comes first and stops this, or comes after and wins.
                Task setActive;
                lock (_lock)
                {
                    if (_epoch.GetValueOrDefault(kind) != epoch)
                    {
                        aborted = true;
                        throw OwnerChanged();
                    }

                    if (!Granted(deviceId))
                    {
                        throw NoGrant();
                    }

                    setActive = _runtime.SetActiveAsync(kind, next, ct);
                }

                await setActive.ConfigureAwait(false);
                if (EpochOf(kind) != epoch)
                {
                    aborted = true;
                    throw OwnerChanged();
                }

                await _runtime.EnsureLoadedAsync(kind, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Whatever went wrong (engine, memory, cancellation, anything else): this device's claim ends here.
                // After an owner change the owner's own steps already put things right, so the runtime is left alone.
                var (mine, ep) = RemoveExact(entry, displaced: false);
                if (mine && !aborted)
                {
                    await RestoreAsync(entry, ep, stopEngine: true, CancellationToken.None).ConfigureAwait(false);
                }

                if (!mine && !aborted)
                {
                    throw OwnerChanged(); // the owner took the kind over while this call was loading
                }

                throw;
            }

            lock (_lock)
            {
                if (_epoch.GetValueOrDefault(kind) != epoch || !_loaded.TryGetValue(kind, out var now) || !ReferenceEquals(now, entry))
                {
                    throw OwnerChanged();
                }

                entry.Confirmed = true;
            }

            Changed?.Invoke();
            await _host.ChangedAsync().ConfigureAwait(false);
            return StateOf(installed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Picks the engine slot for a device's load. A model already in a slot stays there. The PC's own model keeps the first
    /// slot: a device goes to the second slot while the first holds a PC model, takes the first only when nothing is
    /// loaded there (or when the owner allows replacing and the second slot is another device's), and replaces its own
    /// model before it ever touches someone else's.
    /// </summary>
    private string ChooseSlot(string deviceId, string modelId, bool hasSecond, string first, string second,
        Entry? held1, Entry? held2, ActiveModel? active1, ActiveModel? active2)
    {
        if (!hasSecond)
        {
            return first;
        }

        if (active2?.ModelId == modelId)
        {
            return second;
        }

        if (active1?.ModelId == modelId)
        {
            return first;
        }

        if (held1 is null && active1 is null)
        {
            return first; // nothing of the PC's is loaded there
        }

        if (held2 is null || held2.DeviceId == deviceId)
        {
            return second;
        }

        if (held1 is not null && held1.DeviceId == deviceId)
        {
            return first; // the device's own model there is replaced
        }

        if (held1 is null && _allowReplace())
        {
            return first; // the owner allows devices to replace the PC's model
        }

        throw Conflict(ModelConflictReasons.InUseByDevice, "Another device loaded a model on this PC."); // the first loader keeps its slot
    }

    /// <summary>Unloads a model this device loaded (memory only) and gives the PC's own choice back.</summary>
    public async Task<DeviceModelState> UnloadAsync(string deviceId, DeviceUnloadRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.ModelId))
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "model_id is required.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Granted(deviceId))
            {
                throw NoGrant();
            }

            await ReconcileAsync(null, ct).ConfigureAwait(false);
            Entry? entry;
            lock (_lock)
            {
                entry = _loaded.Values.FirstOrDefault(e => e.ModelId == request.ModelId && e.DeviceId == deviceId);
            }

            if (entry is null)
            {
                throw NotYours(deviceId, request.ModelId);
            }

            // Only the very entry looked up: if the owner or another load changed the kind meanwhile, this call stops.
            var (removed, epoch) = RemoveExact(entry, displaced: false);
            if (!removed)
            {
                throw NotYours(deviceId, request.ModelId);
            }

            await RestoreAsync(entry, epoch, stopEngine: true, ct).ConfigureAwait(false); // engine unload: memory only
            Changed?.Invoke();
            await _host.ChangedAsync().ConfigureAwait(false);
            var installed = (await _host.ListInstalledAsync(ct).ConfigureAwait(false)).FirstOrDefault(m => m.Id == request.ModelId);
            return installed is null
                ? new DeviceModelState { Id = request.ModelId, State = "unloaded" }
                : StateOf(installed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>owner_changed when the PC owner took this device's model away since, else not_loaded_by_device.</summary>
    private ApiException NotYours(string deviceId, string modelId)
    {
        lock (_lock)
        {
            return _displaced.Remove((deviceId, modelId))
                ? Conflict(ModelConflictReasons.OwnerChanged, "The owner of this PC changed the model; refresh the list.")
                : Conflict(ModelConflictReasons.NotLoadedByDevice, "This device did not load that model.");
        }
    }

    // ---- shared steps -----------------------------------------------------------------------------

    /// <summary>Ends the claim on a kind (if any) and counts the change, so an operation in flight for the kind stops.</summary>
    private (Entry? Entry, long Epoch) Remove(string kind, bool displaced)
    {
        lock (_lock)
        {
            var epoch = BumpLocked(kind); // counted even with no entry: a device call may be between its checks and its entry
            if (!_loaded.Remove(kind, out var entry))
            {
                return (null, epoch);
            }

            if (displaced)
            {
                AddDisplacedLocked(entry);
            }

            return (entry, epoch);
        }
    }

    /// <summary>Removes this very entry (never a newer one for the same kind).</summary>
    private (bool Removed, long Epoch) RemoveExact(Entry entry, bool displaced)
    {
        lock (_lock)
        {
            if (!_loaded.TryGetValue(entry.Kind, out var now) || !ReferenceEquals(now, entry))
            {
                return (false, _epoch.GetValueOrDefault(entry.Kind));
            }

            _loaded.Remove(entry.Kind);
            if (displaced)
            {
                AddDisplacedLocked(entry);
            }

            return (true, BumpLocked(entry.Kind));
        }
    }

    private void AddDisplacedLocked(Entry entry)
    {
        if (_displaced.Count >= MaxDisplaced)
        {
            _displaced.Clear(); // a device that missed the news only learns not_loaded_by_device instead
        }

        _displaced.Add((entry.DeviceId, entry.ModelId));
    }

    /// <summary>
    /// Gives the PC's own choice for the kind back. The device's model leaves memory first when asked (memory only). The
    /// PC's model is looked up again (it may be gone), and nothing happens if the kind changed since <paramref name="epoch"/>.
    /// </summary>
    private async Task RestoreAsync(Entry entry, long epoch, bool stopEngine, CancellationToken ct)
    {
        if (stopEngine && _runtime.EngineStateOf(entry.Kind) is not (EngineState.Stopped or null) &&
            _runtime.GetActive(entry.Kind)?.ModelId == entry.ModelId && EpochOf(entry.Kind) == epoch)
        {
            await _runtime.UnloadAsync(entry.Kind, ct).ConfigureAwait(false); // engine unload: memory only
        }

        var previous = entry.PreviousId is null ? null : await _host.ResolveAsync(entry.PreviousId, ct).ConfigureAwait(false);
        Task? set = null;
        lock (_lock)
        {
            // Only while nothing else changed this kind and the active model is still the device's own.
            if (_epoch.GetValueOrDefault(entry.Kind) == epoch && _runtime.GetActive(entry.Kind)?.ModelId == entry.ModelId)
            {
                set = _runtime.SetActiveAsync(entry.Kind, previous, ct); // null when the PC's model is no longer installed
            }
        }

        if (set is not null)
        {
            await set.ConfigureAwait(false);
        }
    }

    /// <summary>A device's model whose engine has stopped (idle unload, crash) is no longer the device's: the PC's choice returns.</summary>
    private async Task ReconcileAsync(string? kind, CancellationToken ct)
    {
        List<(Entry Entry, long Epoch)> gone = [];
        lock (_lock)
        {
            foreach (var e in _loaded.Values.Where(e => (kind is null || e.Kind == kind) && e.Confirmed &&
                _runtime.EngineStateOf(e.Kind) is EngineState.Stopped or EngineState.Failed or null).ToList())
            {
                _loaded.Remove(e.Kind);
                gone.Add((e, BumpLocked(e.Kind)));
            }
        }

        foreach (var (e, epoch) in gone)
        {
            await RestoreAsync(e, epoch, stopEngine: false, ct).ConfigureAwait(false);
        }

        if (gone.Count > 0)
        {
            Changed?.Invoke();
        }
    }

    // ---- routes -----------------------------------------------------------------------------------

    /// <summary>
    /// Adds the four device routes (4.7) to the pipeline. They are the only model routes a device can reach beyond the
    /// ones every app has; each call checks the grant in the device store (never anything the device sent), and a
    /// request that did not come from a signed device finds no route.
    /// </summary>
    public void MapRoutes(ApiPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var json = AiHostJsonContext.Default;
        pipeline.Map("GET", Routes.DeviceModels, c => Guarded(async () =>
        {
            var device = Grant(c);
            await c.WriteJsonAsync(200, await ListAsync(c.Aborted, device).ConfigureAwait(false), json.DeviceModelList).ConfigureAwait(false);
        }), requiresAuth: false);
        pipeline.Map("POST", Routes.DeviceModelLoad, c => Guarded(async () =>
        {
            var device = Grant(c);
            var body = c.ReadJson(json.DeviceLoadRequest);
            await c.WriteJsonAsync(200, await LoadAsync(device, body, c.Aborted).ConfigureAwait(false), json.DeviceModelState)
                .ConfigureAwait(false);
        }), requiresAuth: false);
        pipeline.Map("POST", Routes.DeviceModelUnload, c => Guarded(async () =>
        {
            var device = Grant(c);
            var body = c.ReadJson(json.DeviceUnloadRequest);
            await c.WriteJsonAsync(200, await UnloadAsync(device, body, c.Aborted).ConfigureAwait(false), json.DeviceModelState)
                .ConfigureAwait(false);
        }), requiresAuth: false);
        pipeline.Map("GET", Routes.DevicePlacement, c => Guarded(async () =>
        {
            var device = Grant(c);
            await c.WriteJsonAsync(200, PlacementsOf(device), json.DevicePlacementList).ConfigureAwait(false);
        }), requiresAuth: false);
        pipeline.Map("POST", Routes.DevicePlacement, c => Guarded(async () =>
        {
            var device = Grant(c);
            var body = c.ReadJson(json.DevicePlacementRequest);
            await c.WriteJsonAsync(200, await SetPlacementAsync(device, body, c.Aborted).ConfigureAwait(false), json.DevicePlacementRequest)
                .ConfigureAwait(false);
        }), requiresAuth: false);
    }

    /// <summary>Anything unexpected becomes the standard JSON error answer (no exception text), never an unhandled throw.</summary>
    private static async Task Guarded(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (ApiException or OperationCanceledException or EngineException or EngineCrashedException))
        {
            throw new ApiException(ErrorCodes.InternalError, "Unexpected host error.");
        }
    }

    /// <summary>The calling device's id, when it is a signed device that holds Model control right now.</summary>
    private string Grant(ApiContext context)
    {
        var id = context.Request.Caller?.Remote?.Id ?? throw new ApiException(ErrorCodes.NotFound, "Unknown route.");
        if (!Granted(id))
        {
            throw NoGrant();
        }

        return id;
    }

    private async Task ReconcileQuietlyAsync(string kind)
    {
        try
        {
            await ReconcileAsync(kind, CancellationToken.None).ConfigureAwait(false);
            await _host.ChangedAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ApiException or EngineException or ObjectDisposedException or OperationCanceledException)
        {
            // The next call reconciles again.
        }
    }
}
