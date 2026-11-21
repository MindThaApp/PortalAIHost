using System.Text.Json;
using System.Text.Json.Serialization;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Auth;
using Portal.AiHost.Core.Catalogue;
using Portal.AiHost.Core.Download;
using Portal.AiHost.Core.Store;
using Portal.AiHost.Core.Runtime;
using Portal.AiHost.Core.Scheduling;
using Portal.AiHost.Core.Settings;
using Portal.AiHost.Platform.Windows.Pipes;
using Portal.AiHost.Server;

namespace PortalAIHost.Service;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ServiceJsonContext : JsonSerializerContext;

public sealed class HostShellOptions
{
    public required HostPaths Paths { get; init; }

    /// <summary>Engines per kind; the composition root passes the real supervisors, tests pass fakes.</summary>
    public required IReadOnlyDictionary<string, IEngineSupervisor> Supervisors { get; init; }

    public required IHostKeyProtector KeyProtector { get; init; }

    public ICallerIdentityResolver? CallerResolver { get; init; }

    public IHardwareProbe? Hardware { get; init; }

    public IModelStore? Store { get; init; }

    /// <summary>Opens the model store once this host holds the single-instance lock (used when Store is null).</summary>
    public Func<IModelStore?>? OpenStore { get; init; }

    /// <summary>Opens the store for the saved settings (models folder). Used before <see cref="OpenStore"/> when set.</summary>
    public Func<HostSettings, IModelStore?>? OpenStoreFor { get; init; }

    /// <summary>Like <see cref="OpenStoreFor"/>, and also says why the store could not be opened. Used first when set.</summary>
    public Func<HostSettings, StoreOpenResult>? OpenStoreReporting { get; init; }

    /// <summary>Opens earlier models folders (settings) for listing and remove only; never creates anything.</summary>
    public Func<HostSettings, IModelStore?, IReadOnlyList<IModelStore>>? OpenPreviousStores { get; init; }

    /// <summary>Verified catalogue entries, or null when no trusted model list could be loaded (fail closed).</summary>
    public IReadOnlyList<CatalogueEntry>? Catalogue { get; init; }

    /// <summary>Creates the downloader for the opened store; null disables downloads.</summary>
    public Func<IModelStoreWriter, ModelDownloader?>? CreateDownloader { get; init; }

    public string HostVersion { get; init; } = "1.0.0";

    public TimeSpan QuitGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Grace for in-flight requests when Windows ends the session; the whole stop must fit in a few seconds.</summary>
    public TimeSpan SessionEndGrace { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Overrides the port list (tests).</summary>
    public IReadOnlyList<int>? Ports { get; init; }
}

/// <summary>An opened store, or null with a plain-language reason.</summary>
public sealed record StoreOpenResult(IModelStore? Store, string? Problem);

/// <summary>
/// The running host: runtime, HTTP server, admin pipe and safe-mode marker. <see cref="StartAsync"/> returns null
/// when another host already owns this user's pipe (the caller then sends <c>show</c> and exits).
/// </summary>
public sealed class HostShell : IAsyncDisposable
{
    private readonly HostShellOptions _o;
    private readonly TaskCompletionSource _quit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ControlPipeServer _pipe = null!;
    private ClientRegistry? _registry;
    private FairScheduler? _scheduler;
    private SettingsStore<HostSettings> _settings = null!;
    private SafeModeMarker? _marker;
    private readonly Lock _showGate = new();
    private EventHandler<string?>? _showRequested;
    private List<string?>? _pendingShows = [];
    private IModelStore? _store;
    private readonly Lock _inUseGate = new();
    private Task _inUseChain = Task.CompletedTask;
    private readonly Dictionary<string, string> _inUse = new(StringComparer.Ordinal); // kind -> model marked in use
    private readonly List<(IEngineSupervisor Supervisor, EventHandler<EngineStateChange> Handler)> _inUseHandlers = [];
    private int _quitting;
    private volatile bool _sessionEnding;

    private HostShell(HostShellOptions options) => _o = options;

    public AiHostRuntime Runtime { get; private set; } = null!;

    public AiHostServer Server { get; private set; } = null!;

    /// <summary>True from the start of a quit (pipe, tray or session end).</summary>
    public bool IsQuitting => Volatile.Read(ref _quitting) != 0;

    public SafeModeMarker Marker => _marker!;

    public ControlPipeServer Pipe => _pipe;

    public SettingsStore<HostSettings> Settings => _settings;

    public IModelStore? Store => _store;

    internal HostPaths PathsForTests => _o.Paths;

    /// <summary>Catalogue, downloads and remove for the models page.</summary>
    internal ModelCenter Models { get; private set; } = null!;

    /// <summary>Idle unload after this long for a kind, or null to keep it loaded: settings, and 5 minutes on battery.</summary>
    public TimeSpan? IdleUnloadFor(string kind)
    {
        var (keep, seconds) = IdlePolicyFor(kind);
        return keep ? null : TimeSpan.FromSeconds(seconds);
    }

    internal (bool KeepLoaded, int IdleUnloadSeconds) IdlePolicyFor(string kind)
    {
        var s = _settings?.Current;
        var k = s is not null && s.Kinds.TryGetValue(kind, out var v) ? v : null;
        if (k?.KeepLoaded == true)
        {
            return (true, 0);
        }

        var seconds = k?.IdleUnloadSeconds is > 0 and var n ? n : 600;
        bool battery;
        try
        {
            battery = _o.Hardware?.IsOnBattery == true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            battery = false;
        }

        return (false, battery ? Math.Min(seconds, 300) : seconds);
    }

    /// <summary>
    /// The window asked to be shown (pipe <c>show</c>, tray Open, or an approval request). Requests made before
    /// the first handler is added are kept and delivered to it, so an early <c>show</c> is never lost.
    /// </summary>
    public event EventHandler<string?>? ShowRequested
    {
        add
        {
            List<string?>? replay;
            lock (_showGate)
            {
                _showRequested += value;
                replay = _pendingShows;
                _pendingShows = null;
            }

            foreach (var page in replay ?? [])
            {
                value?.Invoke(this, page);
            }
        }
        remove
        {
            lock (_showGate)
            {
                _showRequested -= value;
            }
        }
    }

    /// <summary>Completes after a clean quit.</summary>
    public Task Completion => _done.Task;

    public static async Task<HostShell?> StartAsync(HostShellOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var shell = new HostShell(options);
        var handler = new ControlHandler(shell);
        // Hold the name now (single instance) but answer only once everything is up: a window that connects
        // during startup waits, and its show or quit is handled after startup instead of hitting a half-built host.
        var pipe = ControlPipeServer.TryStart(options.Paths.PipeName, handler.HandleAsync, acceptNow: false);
        if (pipe is null)
        {
            return null;
        }

        shell._pipe = pipe;
        try
        {
            await shell.StartCoreAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await shell.AbortStartAsync().ConfigureAwait(false);
            throw;
        }

        pipe.Begin();
        return shell;
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        var root = _o.Paths.DataRoot;
        _marker = SafeModeMarker.Begin(root);
        _settings = HostSettingsFile.Create(root);
        var settings = _settings.Load();
        if (_o.Store is null && _o.OpenStoreReporting is { } open)
        {
            var opened = open(settings);
            _store = opened.Store;
            StoreProblem = opened.Problem;
        }
        else
        {
            _store = _o.Store ?? _o.OpenStoreFor?.Invoke(settings) ?? _o.OpenStore?.Invoke();
        }

        var previous = _o.OpenPreviousStores?.Invoke(settings, _store) ?? [];
        await ReleaseStaleInUseAsync(ct).ConfigureAwait(false);
        foreach (var sup in _o.Supervisors.Values)
        {
            EventHandler<EngineStateChange> handler = (_, change) => QueueInUse(sup, change);
            sup.StateChanged += handler;
            _inUseHandlers.Add((sup, handler));
        }

        Models = new ModelCenter(_store, _o.Catalogue,
            _store is IModelStoreWriter writer ? _o.CreateDownloader?.Invoke(writer) : null,
            _o.Hardware, () => _settings.Current, kind => Runtime?.GetActive(kind)?.ModelId, PushDownloadAsync,
            isLoaded: IsLoaded, previousStores: previous);
        _registry = new ClientRegistry(new ClientRegistryOptions { DataDirectory = root }, _o.KeyProtector);
        _scheduler = new FairScheduler();
        Runtime = await AiHostRuntime.StartAsync(new AiHostRuntimeOptions
        {
            Registry = _registry,
            Scheduler = _scheduler,
            Supervisors = _o.Supervisors,
            Hardware = _o.Hardware,
            Planner = new() { MaxMemoryPercent = settings.MaxMemoryPercent, Backend = settings.Backend },
            HostVersion = _o.HostVersion,
            SafeMode = _marker.SafeMode,
            IdlePolicy = IdlePolicyFor,
        }, ct).ConfigureAwait(false);

        Runtime.EngineStateChanged += (_, change) => _ = PushEngineStateAsync(change);
        _registry.RequestPending += (_, info) => _ = PushApprovalAsync(info);

        await LoadActiveModelsAsync(ct).ConfigureAwait(false);
        Server = await AiHostServer.StartAsync(Runtime, new AiHostServerOptions
        {
            TestMode = _o.Paths.TestMode,
            Ports = _o.Ports,
            DiscoveryPath = _o.Paths.DiscoveryPath,
            CallerResolver = _o.CallerResolver,
        }, ct).ConfigureAwait(false);

        var keepLoaded = settings.Kinds.Where(k => k.Value.KeepLoaded).Select(k => k.Key).ToList();
        _ = Runtime.AutoLoadAsync(keepLoaded, CancellationToken.None);
    }

    public ControlStatus BuildControlStatus() => new()
    {
        Status = Runtime.BuildStatus(null),
        SafeMode = Runtime.SafeMode,
        UncleanExits = _marker?.UncleanExits ?? 0,
        BaseUrl = Server?.BaseUrl,
        UpgradeModelId = Models?.UpgradeModelId,
        OnBattery = SafeBattery(),
        StoreProblem = StoreProblem,
    };

    /// <summary>Why the models folder could not be opened, or null.</summary>
    public string? StoreProblem { get; private set; }

    private bool SafeBattery()
    {
        try
        {
            return _o.Hardware?.IsOnBattery == true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    public void RequestShow(string? page)
    {
        EventHandler<string?>? handler;
        lock (_showGate)
        {
            if (_pendingShows is { } pending)
            {
                pending.Add(page);
                return;
            }

            handler = _showRequested;
        }

        handler?.Invoke(this, page);
    }

    /// <summary>Starts a clean quit (tray Quit or pipe <c>quit</c>); safe to call more than once.</summary>
    public void RequestQuit() => _ = QuitAsync();

    public async Task QuitAsync()
    {
        if (Interlocked.Exchange(ref _quitting, 1) != 0)
        {
            await _done.Task.ConfigureAwait(false);
            return;
        }

        _quit.TrySetResult();
        try
        {
            await StopPartsAsync(_sessionEnding ? _o.SessionEndGrace : _o.QuitGrace).ConfigureAwait(false);
            Guard(() => _marker?.EndClean());
        }
        finally
        {
            _done.TrySetResult();
        }
    }

    /// <summary>
    /// Every step on its own guard, so one failure never skips the discovery delete or the clean-exit marker.
    /// Works on a half-started host too (null parts are skipped).
    /// </summary>
    private async Task StopPartsAsync(TimeSpan grace)
    {
        // Downloads stop first and keep their partial files, so the next start can resume them.
        await GuardAsync(async () =>
        {
            if (Models is { } m)
            {
                await m.DisposeAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        await GuardAsync(() => Runtime?.StopAsync(grace) ?? Task.CompletedTask).ConfigureAwait(false);
        await GuardAsync(() => Server?.StopAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        await GuardAsync(async () => await _pipe.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        await GuardAsync(async () =>
        {
            if (Runtime is { } r)
            {
                await r.DisposeAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        await GuardAsync(DrainInUseAsync).ConfigureAwait(false);
        Guard(() => _registry?.Dispose());
        Guard(() => _scheduler?.Dispose());
    }

    // ---- in-use marks ---------------------------------------------------------------------------

    /// <summary>True while any engine that is not stopped still runs with this model (its file may be open).</summary>
    private bool IsLoaded(string modelId) =>
        _o.Supervisors.Values.Any(s => s.State is not EngineState.Stopped && s.Current?.ModelId == modelId);

    /// <summary>
    /// Keeps the store's in-use marks in step with the engines, so the store itself refuses to remove a model an
    /// engine has open. Updates run one after another in event order.
    /// </summary>
    private void QueueInUse(IEngineSupervisor sup, EngineStateChange change)
    {
        if (_store is null)
        {
            return;
        }

        var modelId = change.State is EngineState.Stopped ? null : sup.Current?.ModelId;
        lock (_inUseGate)
        {
            _inUseChain = _inUseChain.ContinueWith(_ => SyncInUseAsync(sup.Kind, modelId), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task SyncInUseAsync(string kind, string? modelId)
    {
        var store = _store!;
        _inUse.TryGetValue(kind, out var marked);
        if (marked == modelId)
        {
            return;
        }

        if (marked is not null)
        {
            _inUse.Remove(kind);
            await TrySetInUseAsync(store, marked, false).ConfigureAwait(false);
        }

        if (modelId is not null && await TrySetInUseAsync(store, modelId, true).ConfigureAwait(false))
        {
            _inUse[kind] = modelId;
        }
    }

    private static async Task<bool> TrySetInUseAsync(IModelStore store, string modelId, bool inUse)
    {
        try
        {
            await store.SetInUseAsync(modelId, inUse, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false; // not a stored model (for example a test spec) or the inventory could not be written
        }
    }

    /// <summary>Marks left by a host that did not exit cleanly: no engine runs yet, so none of them is in use.</summary>
    private async Task ReleaseStaleInUseAsync(CancellationToken ct)
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            foreach (var m in await _store.ListAsync(ct).ConfigureAwait(false))
            {
                if (m.Files.Any(f => f.State == StoredFileState.InUse))
                {
                    await TrySetInUseAsync(_store, m.Id, false).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task DrainInUseAsync()
    {
        foreach (var (sup, handler) in _inUseHandlers)
        {
            sup.StateChanged -= handler;
        }

        Task chain;
        lock (_inUseGate)
        {
            chain = _inUseChain;
        }

        await chain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    /// <summary>
    /// Startup failed: release everything that started. The safe-mode marker stays, so a host that keeps failing
    /// to start ends up in safe mode.
    /// </summary>
    private async Task AbortStartAsync()
    {
        Interlocked.Exchange(ref _quitting, 1);
        try
        {
            await StopPartsAsync(TimeSpan.Zero).ConfigureAwait(false);
        }
        finally
        {
            _done.TrySetResult();
        }
    }

    /// <summary>
    /// Windows is ending the session or closing the app for an update. Called on the tray thread, which must not
    /// return before the stop is done: the clean-exit marker and the discovery delete come first (they are what
    /// the next start checks), then the normal quit with a short grace, bounded by the budget.
    /// True when everything stopped in time.
    /// </summary>
    public bool StopForSessionEnd(TimeSpan budget)
    {
        _sessionEnding = true;
        Guard(() =>
        {
            if (_o.Paths.DiscoveryPath is { } path && Runtime is { } r)
            {
                DiscoveryFileWriter.DeleteIfOwn(path, r.InstanceId);
            }
        });
        Guard(() => _marker?.EndClean());
        var quit = QuitAsync();
        try
        {
            return quit.Wait(budget);
        }
        catch (AggregateException)
        {
            return true;
        }
    }

    private static void Guard(Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    private static async Task GuardAsync(Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    public Task OnResumeAsync() => Runtime.CheckEnginesAfterResumeAsync(CancellationToken.None);

    // ---- active models ------------------------------------------------------------------------

    private string ActiveModelsPath => Path.Combine(_o.Paths.DataRoot, "active-models.json");

    private async Task LoadActiveModelsAsync(CancellationToken ct)
    {
        if (_store is null || !File.Exists(ActiveModelsPath))
        {
            return;
        }

        Dictionary<string, string>? map;
        try
        {
            map = JsonSerializer.Deserialize(File.ReadAllBytes(ActiveModelsPath), ServiceJsonContext.Default.DictionaryStringString);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return;
        }

        foreach (var (kind, id) in map ?? [])
        {
            if (await _store.GetAsync(id, ct).ConfigureAwait(false) is { IsComplete: true } stored && stored.Kind == kind)
            {
                await Runtime.SetActiveAsync(kind, ToActive(stored), ct).ConfigureAwait(false);
            }
        }
    }

    internal async Task ActivateAsync(string modelId, CancellationToken ct)
    {
        var stored = _store is null ? null : await _store.GetAsync(modelId, ct).ConfigureAwait(false);
        if (stored is not { IsComplete: true })
        {
            throw new ControlPipeException(ErrorCodes.NotFound, "That model is not installed.");
        }

        await Runtime.SetActiveAsync(stored.Kind, ToActive(stored), ct).ConfigureAwait(false);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kind in new[] { ModelKinds.Chat, ModelKinds.Embedding })
        {
            if (Runtime.GetActive(kind) is { } m)
            {
                map[kind] = m.ModelId;
            }
        }

        AtomicFile.WriteAllBytes(ActiveModelsPath, JsonSerializer.SerializeToUtf8Bytes(map, ServiceJsonContext.Default.DictionaryStringString));
        await PushStatusAsync().ConfigureAwait(false);
    }

    private static ActiveModel ToActive(StoredModel m) =>
        new(m.Kind, m.Id, m.DisplayName, m.Files.Select(f => f.Path).ToList());

    // ---- pushes -------------------------------------------------------------------------------

    private async Task PushEngineStateAsync(EngineStateChange change)
    {
        try
        {
            await _pipe.BroadcastAsync(ControlTypes.EventEngineState, JsonSerializer.SerializeToElement(new EngineStateEvent
            {
                Kind = change.Kind,
                State = change.State.ToString().ToLowerInvariant(),
                Progress = change.Progress,
            }, AiHostJsonContext.Default.EngineStateEvent)).ConfigureAwait(false);
            await PushStatusAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task PushDownloadAsync(DownloadProgressEvent e)
    {
        try
        {
            await _pipe.BroadcastAsync(ControlTypes.EventDownloadProgress,
                JsonSerializer.SerializeToElement(e, AiHostJsonContext.Default.DownloadProgressEvent)).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal Task PushStatusAsync() =>
        _pipe.BroadcastAsync(ControlTypes.EventStatusChanged,
            JsonSerializer.SerializeToElement(BuildControlStatus(), AiHostJsonContext.Default.ControlStatus));

    private async Task PushApprovalAsync(AccessRequestInfo info)
    {
        try
        {
            await _pipe.BroadcastAsync(ControlTypes.EventApprovalPending,
                JsonSerializer.SerializeToElement(ControlHandler.ToPending(info), AiHostJsonContext.Default.PendingApproval))
                .ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        RequestShow("approvals");
    }

    public async ValueTask DisposeAsync() => await QuitAsync().ConfigureAwait(false);
}
