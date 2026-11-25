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
/// One model per kind is active at a time (a second concurrent slot is its own step).
/// </summary>
public sealed class ModelControl
{
    private sealed class Entry
    {
        public required string Kind { get; init; }
        public required string ModelId { get; init; }
        public required string DisplayName { get; init; }
        public required string DeviceId { get; init; }
        public required string Placement { get; init; }
        public required PlacementOverride? Override { get; init; }

        /// <summary>The PC's own choice for this kind before the device loaded, put back when the device's model goes.</summary>
        public required ActiveModel? Previous { get; init; }

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
                Kind = e.Kind, ModelId = e.ModelId, DisplayName = e.DisplayName, DeviceId = e.DeviceId,
                DeviceName = _devices.Find(e.DeviceId)?.Name ?? "a removed device", Placement = e.Placement,
            })];
        }
    }

    /// <summary>The model the PC itself chose for a kind while a device's model is active; null when no device holds the kind.</summary>
    public (bool Held, string? PcModelId) PcChoice(string kind)
    {
        lock (_lock)
        {
            return _loaded.TryGetValue(kind, out var e) ? (true, e.Previous?.ModelId) : (false, null);
        }
    }

    // ---- the PC owner's side ----------------------------------------------------------------------

    /// <summary>
    /// The PC owner is about to change this kind (load, unload or pick another model). The device's claim ends first and the PC's
    /// own choice comes back, then the owner's action runs. The device sees <c>owner_changed</c> on its next call.
    /// </summary>
    public async Task OwnerChangedAsync(string kind, CancellationToken ct)
    {
        var entry = Remove(kind, displaced: true);
        if (entry is not null)
        {
            await RestoreAsync(entry, stopEngine: false, ct).ConfigureAwait(false);
            Changed?.Invoke();
        }
    }

    /// <summary>"Take back": unloads the model a device loaded for this kind (memory only) and gives the PC's choice back.</summary>
    public async Task<bool> TakeBackAsync(string kind, CancellationToken ct)
    {
        var entry = Remove(kind, displaced: true);
        if (entry is null)
        {
            return false;
        }

        await RestoreAsync(entry, stopEngine: true, ct).ConfigureAwait(false);
        Changed?.Invoke();
        await _host.ChangedAsync().ConfigureAwait(false);
        return true;
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

    public async Task<DeviceModelList> ListAsync(CancellationToken ct)
    {
        await ReconcileAsync(null, ct).ConfigureAwait(false);
        var installed = await _host.ListInstalledAsync(ct).ConfigureAwait(false);
        var list = new DeviceModelList();
        foreach (var m in installed)
        {
            list.Models.Add(StateOf(m));
        }

        return list;
    }

    private DeviceModelState StateOf(InstalledModel m)
    {
        var active = _runtime.GetActive(m.Kind);
        var engine = _runtime.EngineStateOf(m.Kind);
        var isActive = active?.ModelId == m.Id;
        var state = !isActive ? "unloaded"
            : engine == EngineState.Ready ? "loaded"
            : engine is EngineState.Starting or EngineState.Loading ? "loading"
            : "unloaded";
        Entry? entry;
        lock (_lock)
        {
            _loaded.TryGetValue(m.Kind, out entry);
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
            request.GpuMemoryLimitBytes is <= 0 || request.MemoryLimitBytes is <= 0 || request.CpuThreads is <= 0 or > 1024)
        {
            throw new ApiException(ErrorCodes.InvalidRequest,
                "model_id is required; compute must be auto, gpu or cpu; limits must be positive.");
        }

        var over = new PlacementOverride(request.Compute, request.GpuMemoryLimitBytes, request.MemoryLimitBytes, request.CpuThreads);
        await ReplaceAsync(deviceId, request.ModelId, request.Compute, over, requireOwn: true, ct).ConfigureAwait(false);
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

    private async Task<DeviceModelState> ReplaceAsync(string deviceId, string modelId, string placementWord,
        PlacementOverride? over, bool requireOwn, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ReconcileAsync(null, ct).ConfigureAwait(false);
            var installed = (await _host.ListInstalledAsync(ct).ConfigureAwait(false)).FirstOrDefault(m => m.Id == modelId);
            var resolved = installed is null ? null : await _host.ResolveAsync(modelId, ct).ConfigureAwait(false);
            if (installed is null || resolved is null)
            {
                throw new ApiException(ErrorCodes.NotFound, "That model is not installed.");
            }

            var kind = resolved.Kind;
            var active = _runtime.GetActive(kind);
            var engine = _runtime.EngineStateOf(kind);
            var running = engine is EngineState.Starting or EngineState.Loading or EngineState.Ready;
            Entry? held;
            lock (_lock)
            {
                _loaded.TryGetValue(kind, out held);
            }

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

            if (running && !requireOwn)
            {
                if (held is not null && held.DeviceId != deviceId)
                {
                    throw Conflict(ModelConflictReasons.InUseByDevice, "Another device loaded a model on this PC.");
                }

                if (held is null && active is not null && !_allowReplace())
                {
                    throw Conflict(ModelConflictReasons.InUseByHost, "This PC's own apps are using a model.");
                }
            }

            var next = resolved with { Override = over };

            // Nothing is evicted before the new model is known to fit (the planner checks with this kind's memory free).
            await _runtime.DecidePlacementAsync(next, ct).ConfigureAwait(false);

            var entry = new Entry
            {
                Kind = kind, ModelId = modelId, DisplayName = resolved.DisplayName, DeviceId = deviceId,
                Placement = placementWord, Override = over,
                Previous = held is not null ? held.Previous : active?.Override is null ? active : active with { Override = null },
            };
            lock (_lock)
            {
                _loaded[kind] = entry;
                _displaced.Remove((deviceId, modelId));
            }

            try
            {
                // The model in memory goes only when it differs or a placement changed (an explicit stop first).
                if (running && active is not null && (active.ModelId != modelId || requireOwn))
                {
                    await _runtime.UnloadAsync(kind, ct).ConfigureAwait(false); // engine unload: memory only
                }

                await _runtime.SetActiveAsync(kind, next, ct).ConfigureAwait(false);
                await _runtime.EnsureLoadedAsync(kind, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ApiException or EngineException or EngineCrashedException or OperationCanceledException)
            {
                var mine = false;
                lock (_lock)
                {
                    if (_loaded.TryGetValue(kind, out var now) && ReferenceEquals(now, entry))
                    {
                        _loaded.Remove(kind);
                        mine = true;
                    }
                }

                if (mine)
                {
                    await RestoreAsync(entry, stopEngine: true, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    throw Conflict(ModelConflictReasons.OwnerChanged, "The owner of this PC changed the model.");
                }

                throw;
            }

            lock (_lock)
            {
                if (!_loaded.TryGetValue(kind, out var now) || !ReferenceEquals(now, entry))
                {
                    throw Conflict(ModelConflictReasons.OwnerChanged, "The owner of this PC changed the model.");
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

            Remove(entry.Kind, displaced: false);
            await RestoreAsync(entry, stopEngine: true, ct).ConfigureAwait(false); // engine unload: memory only
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

    private Entry? Remove(string kind, bool displaced)
    {
        lock (_lock)
        {
            if (!_loaded.Remove(kind, out var entry))
            {
                return null;
            }

            if (displaced)
            {
                _displaced.Add((entry.DeviceId, entry.ModelId));
            }

            return entry;
        }
    }

    /// <summary>Gives the PC's own choice for the kind back. The device's model leaves memory first when asked (memory only).</summary>
    private async Task RestoreAsync(Entry entry, bool stopEngine, CancellationToken ct)
    {
        if (stopEngine && _runtime.EngineStateOf(entry.Kind) is not (EngineState.Stopped or null) &&
            _runtime.GetActive(entry.Kind)?.ModelId == entry.ModelId)
        {
            await _runtime.UnloadAsync(entry.Kind, ct).ConfigureAwait(false); // engine unload: memory only
        }

        // Only while the active model is still the device's own: never undo a choice the owner made since.
        if (_runtime.GetActive(entry.Kind)?.ModelId == entry.ModelId)
        {
            await _runtime.SetActiveAsync(entry.Kind, entry.Previous, ct).ConfigureAwait(false);
        }
    }

    /// <summary>A device's model whose engine has stopped (idle unload, crash) is no longer the device's: the PC's choice returns.</summary>
    private async Task ReconcileAsync(string? kind, CancellationToken ct)
    {
        List<Entry> gone;
        lock (_lock)
        {
            gone = [.. _loaded.Values.Where(e => (kind is null || e.Kind == kind) && e.Confirmed &&
                _runtime.EngineStateOf(e.Kind) is EngineState.Stopped or EngineState.Failed or null)];
            foreach (var e in gone)
            {
                _loaded.Remove(e.Kind);
            }
        }

        foreach (var e in gone)
        {
            await RestoreAsync(e, stopEngine: false, ct).ConfigureAwait(false);
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
        pipeline.Map("GET", Routes.DeviceModels, async c =>
        {
            Grant(c);
            await c.WriteJsonAsync(200, await ListAsync(c.Aborted).ConfigureAwait(false), json.DeviceModelList).ConfigureAwait(false);
        }, requiresAuth: false);
        pipeline.Map("POST", Routes.DeviceModelLoad, async c =>
        {
            var device = Grant(c);
            var body = c.ReadJson(json.DeviceLoadRequest);
            await c.WriteJsonAsync(200, await LoadAsync(device, body, c.Aborted).ConfigureAwait(false), json.DeviceModelState)
                .ConfigureAwait(false);
        }, requiresAuth: false);
        pipeline.Map("POST", Routes.DeviceModelUnload, async c =>
        {
            var device = Grant(c);
            var body = c.ReadJson(json.DeviceUnloadRequest);
            await c.WriteJsonAsync(200, await UnloadAsync(device, body, c.Aborted).ConfigureAwait(false), json.DeviceModelState)
                .ConfigureAwait(false);
        }, requiresAuth: false);
        pipeline.Map("GET", Routes.DevicePlacement, async c =>
        {
            var device = Grant(c);
            await c.WriteJsonAsync(200, PlacementsOf(device), json.DevicePlacementList).ConfigureAwait(false);
        }, requiresAuth: false);
        pipeline.Map("POST", Routes.DevicePlacement, async c =>
        {
            var device = Grant(c);
            var body = c.ReadJson(json.DevicePlacementRequest);
            await c.WriteJsonAsync(200, await SetPlacementAsync(device, body, c.Aborted).ConfigureAwait(false), json.DevicePlacementRequest)
                .ConfigureAwait(false);
        }, requiresAuth: false);
    }

    /// <summary>The calling device's id, when it is a signed device that holds Model control right now.</summary>
    private string Grant(ApiContext context)
    {
        var id = context.Request.Caller?.Remote?.Id ?? throw new ApiException(ErrorCodes.NotFound, "Unknown route.");
        if (_devices.Find(id)?.ModelControl != true)
        {
            throw new ApiException(ErrorCodes.ModelControlRequired,
                "This device does not have Model control. Ask the owner of this PC to turn it on.");
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
