using System.Text.Json;
using System.Text.Json.Serialization;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Auth;
using Portal.AiHost.Core.Catalogue;
using Portal.AiHost.Core.Download;
using Portal.AiHost.Core.Notifications;
using Portal.AiHost.Core.Store;
using Portal.AiHost.Core.Planning;
using Portal.AiHost.Core.Runtime;
using Portal.AiHost.Core.Scheduling;
using Portal.AiHost.Core.Settings;
using Portal.AiHost.Platform.Windows.Pipes;
using Portal.AiHost.Server;

namespace PortalAIHost.Service;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(ModelNotesData))]
internal sealed partial class ServiceJsonContext : JsonSerializerContext;

public sealed class HostShellOptions
{
    public required HostPaths Paths { get; init; }

    /// <summary>Engines per kind; the composition root passes the real supervisors, tests pass fakes.</summary>
    public required IReadOnlyDictionary<string, IEngineSupervisor> Supervisors { get; init; }

    /// <summary>Reports which engine program files are present, for the health checks. Null: not checked.</summary>
    public Func<IReadOnlyList<EngineFileStatus>>? EngineFiles { get; init; }

    public required IHostKeyProtector KeyProtector { get; init; }

    /// <summary>Protects the client-mode device key and token (docs/client-mode-v1.md section 3). Null: client mode is not available.</summary>
    public Portal.AiHost.Core.Remote.Client.ISecretProtector? SecretProtector { get; init; }

    /// <summary>How the link to the paired PC connects. Null: the LAN connector (tests pass a scripted one).</summary>
    public Portal.AiHost.Core.Remote.Client.IRemoteConnector? ClientConnector { get; init; }

    public ICallerIdentityResolver? CallerResolver { get; init; }

    public IHardwareProbe? Hardware { get; init; }

    /// <summary>Reads engine memory and processor time for status usage; null leaves usage out.</summary>
    public IProcessUsageProbe? UsageProbe { get; init; }

    /// <summary>Screen and power checks for the ease-off rules; null uses only the hardware probe's battery state.</summary>
    public IEaseOffSensors? EaseOffSensors { get; init; }

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

    /// <summary>Tests: how the LAN listener binds and which sources it serves (never the real network).</summary>
    public RemoteAccessTestOptions? RemoteTest { get; init; }
}

/// <summary>Test hooks for the LAN listener.</summary>
public sealed class RemoteAccessTestOptions
{
    public System.Net.IPAddress? BindAddress { get; init; }
    public Func<System.Net.IPAddress?, bool>? AllowSource { get; init; }
    public int? Port { get; init; }
    public Func<List<string>>? Addresses { get; init; }
    public TimeSpan? RelayInitialDelay { get; init; }
    public TimeSpan? RelayMaxDelay { get; init; }
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
    private bool _ownsStore;
    private readonly Lock _inUseGate = new();
    private ModelNotes? _notes;
    private Task _inUseChain = Task.CompletedTask;
    private readonly Dictionary<string, string> _inUse = new(StringComparer.Ordinal); // kind -> model marked in use
    private readonly List<(IEngineSupervisor Supervisor, EventHandler<EngineStateChange> Handler)> _inUseHandlers = [];
    private int _quitting;
    private volatile bool _sessionEnding;

    private readonly NotificationCenter _notifyCenter;
    private readonly NotificationEvents _notify;
    private readonly CancellationTokenSource _notifyStop = new();

    private HostShell(HostShellOptions options)
    {
        _o = options;
        _notifyCenter = new NotificationCenter(
            new DelegateNotifier(r => ToastSink?.Invoke(r) ?? false),
            () => _settings?.Current ?? new HostSettings(),
            () => _o.EaseOffSensors?.IsFullscreen() == true,
            windowOpen: () => _pipe is { ConnectionCount: > 0 });
        _notify = new NotificationEvents(_notifyCenter);
    }

    /// <summary>
    /// Shows a Windows notification (the tray icon). Null in test mode and when there is no icon, so nothing is shown;
    /// the approval window then opens as before.
    /// </summary>
    public Func<ToastRequest, bool>? ToastSink { get; set; }

    public AiHostRuntime Runtime { get; private set; } = null!;

    public AiHostServer Server { get; private set; } = null!;

    /// <summary>"Other devices": paired devices, the pairing code and the LAN listener.</summary>
    public RemoteAccess Remote { get; private set; } = null!;

    /// <summary>Server or client mode (use the paired PC's AI).</summary>
    public ClientModeController ClientMode { get; private set; } = null!;

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
        var secondSlot = ModelSlots.IsSecond(kind); // a device's second model follows the kind's idle time, but is never kept loaded
        var k = s is not null && s.Kinds.TryGetValue(ModelSlots.KindOf(kind), out var v) ? v : null;
        if (k?.KeepLoaded == true && !secondSlot)
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
        settings = await MapOlderMemoryShareAsync(settings, ct).ConfigureAwait(false);
        if (_o.Store is null && _o.OpenStoreReporting is { } open)
        {
            var opened = open(settings);
            _store = opened.Store;
            _ownsStore = true;
            StoreProblem = opened.Problem;
        }
        else
        {
            _store = _o.Store ?? _o.OpenStoreFor?.Invoke(settings) ?? _o.OpenStore?.Invoke();
            _ownsStore = _o.Store is null;
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
            isLoaded: IsLoaded, previousStores: previous)
        {
            Hooks = new ModelCenterHooks
            {
                Notes = _notes = new ModelNotes(Path.Combine(root, "model-notes.json")),
                ClearActive = ClearActiveAsync,
                Activate = ActivateAsync,
                StopEngines = StopEnginesAsync,
                RefreshActive = RefreshActiveAsync,
                ApplyMovedFolder = ApplyMovedFolder,
                PushStore = PushStoreAsync,
                SetMaintenance = msg => { if (Runtime is { } r) r.MaintenanceMessage = msg; },
                RememberFolder = RememberFolder,
                ForgetFolder = ForgetFolder,
            },
        };
        _registry = new ClientRegistry(new ClientRegistryOptions { DataDirectory = root }, _o.KeyProtector);
        _scheduler = new FairScheduler(new FairSchedulerOptions { AppControls = _registry });
        Runtime = await AiHostRuntime.StartAsync(new AiHostRuntimeOptions
        {
            Registry = _registry,
            Scheduler = _scheduler,
            AppControls = _registry,
            Supervisors = _o.Supervisors,
            Hardware = _o.Hardware,
            Planner = new() { MaxMemoryPercent = settings.MaxMemoryPercent, Backend = settings.Backend },
            Settings = () => _settings.Current,
            Catalogue = _o.Catalogue,
            Log = line => System.Diagnostics.Trace.WriteLine("Portal AI Host: " + line),
            HostVersion = _o.HostVersion,
            SafeMode = _marker.SafeMode,
            IdlePolicy = IdlePolicyFor,
            UsageProbe = _o.UsageProbe,
            EaseOffSensors = _o.EaseOffSensors,
            Health = new HealthSources
            {
                EngineFiles = _o.EngineFiles,
                StoreFreeBytes = () => _store?.Location.FreeBytes,
                DamagedReason = id => _notes?.DamagedReason(id),
            },
        }, ct).ConfigureAwait(false);

        await Runtime.ApplyPerformanceAsync(ct).ConfigureAwait(false);

        // A move that did not finish removing its old copies (crash, locked file) is finished, or named, at every start.
        _ = Task.Run(async () =>
        {
            try
            {
                await Models.ReconcilePreviousMoveAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Retried at the next start; the folder stays listed under earlier folders.
            }
        });
        Runtime.EngineStateChanged += (_, change) => { _ = PushEngineStateAsync(change); _notify.OnEngineState(change); };
        _registry.RequestPending += (_, info) => _ = PushApprovalAsync(info);

        await LoadActiveModelsAsync(ct).ConfigureAwait(false);
        Server = await AiHostServer.StartAsync(Runtime, new AiHostServerOptions
        {
            TestMode = _o.Paths.TestMode,
            Ports = _o.Ports,
            DiscoveryPath = _o.Paths.DiscoveryPath,
            CallerResolver = _o.CallerResolver,
        }, ct).ConfigureAwait(false);

        Remote = new RemoteAccess(Runtime, new RemoteAccessOptions
        {
            DataDirectory = root,
            KeyProtector = _o.KeyProtector,
            RevokeDevice = id => _registry.RevokeDevice(id),
            ModelHost = new ShellModelHost(this),
            // Under a test data root the listener never leaves this PC (a listener on the network raises a firewall prompt).
            BindAddress = _o.RemoteTest?.BindAddress ?? (_o.Paths.TestMode ? System.Net.IPAddress.Loopback : null),
            AllowSource = _o.RemoteTest?.AllowSource,
            PortOverride = _o.RemoteTest?.Port ?? (_o.Paths.TestMode ? 0 : null),
            Addresses = _o.RemoteTest?.Addresses,
            UseDefaultRelay = !_o.Paths.TestMode,
            RelayInitialDelay = _o.RemoteTest?.RelayInitialDelay,
            RelayMaxDelay = _o.RemoteTest?.RelayMaxDelay,
            BusyDetector = _o.Paths.TestMode ? null : new Portal.AiHost.Core.Remote.CompositeBusyDetector(new Portal.AiHost.Platform.Windows.Usage.FullScreenGameDetector(), new Portal.AiHost.Platform.Windows.Usage.GpuLoadDetector(log: line => System.Diagnostics.Trace.WriteLine("Portal AI Host: " + line))),
        });
        Remote.Changed += () => _ = PushRemoteChangedAsync();
        Remote.Alert += a => _ = PushDeviceAlertAsync(a);
        // Client mode never listens: the suspension is in place before the first Apply, so nothing opens even briefly.
        if (settings.Mode == HostModes.Client)
        {
            await Remote.SuspendAsync(true, ct).ConfigureAwait(false);
        }

        await Remote.ApplyAsync(settings.Remote, ct).ConfigureAwait(false);
        ClientMode = new ClientModeController(Runtime, Remote, new ClientModeOptions
        {
            CreateLink = () => _o.SecretProtector is null
                ? null
                : new Portal.AiHost.Core.Remote.Client.RemoteHostLink(new Portal.AiHost.Core.Remote.Client.ClientStateStore(root, _o.SecretProtector),
                    _o.ClientConnector ?? new Portal.AiHost.Core.Remote.Client.LanConnector(Portal.AiHost.Core.Remote.Client.LanConnector.LinkConnectTimeout),
                    relayFactory: (url, fingerprint) => new Portal.AiHost.Core.Remote.Client.RelayConnector(url, fingerprint)),
            PersistMode = SaveMode,
            InitialFallback = settings.ClientFallback,
            InitialRelayMode = settings.RelayMode,
            InitialPromptShown = settings.FallbackPromptShown,
            PersistPrefs = SaveClientPrefs,
            State = _o.SecretProtector is null ? null : new Portal.AiHost.Core.Remote.Client.ClientStateStore(root, _o.SecretProtector),
            PairingConnector = _o.ClientConnector,
            AppModels = _o.SecretProtector is null ? null : new Portal.AiHost.Core.Remote.Client.AppModelStore(new Portal.AiHost.Core.Remote.Client.ClientStateStore(root, _o.SecretProtector).Directory),
        });
        Runtime.ClientStatus = () => ClientMode.StatusRemote();
        ClientMode.Changed += () => _ = PushRemoteChangedAsync();
        await ClientMode.RestoreAsync(settings.Mode, ct).ConfigureAwait(false);

        var keepLoaded = settings.Kinds.Where(k => k.Value.KeepLoaded).Select(k => k.Key).ToList();
        _ = Runtime.AutoLoadAsync(keepLoaded, CancellationToken.None);
        _ = Task.Run(() => NotificationLoopAsync(_notifyStop.Token));
    }

    /// <summary>
    /// First read after an update: an older memory share (percent of RAM) becomes the byte limit, saved through the
    /// settings store like any other change. Nothing is saved for a new install or when the file cannot be written.
    /// </summary>
    private async Task<HostSettings> MapOlderMemoryShareAsync(HostSettings settings, CancellationToken ct)
    {
        if (settings.MemoryLimitBytes is not null || _o.Hardware is not { } probe
            || _settings.LoadState is not (SettingsLoadState.Loaded or SettingsLoadState.Migrated))
        {
            return settings;
        }

        try
        {
            var hw = await probe.ProbeAsync(ct).ConfigureAwait(false);
            var limits = PerformanceModel.Resolve(settings, hw);
            settings.MemoryLimitBytes = limits.MemoryLimitBytes;
            if (settings.MaxMemoryPercent != 80 && settings.Preset == PerformancePresets.Balanced)
            {
                settings.Preset = PerformancePresets.Custom; // the user chose their own share
            }

            _settings.Save(settings);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Not saved: the older share still applies through the same resolver.
        }

        return _settings.Current;
    }

    public ControlStatus BuildControlStatus() => new()
    {
        Status = Runtime.BuildStatus(null, Models?.ProfileIfReady()),
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

    /// <summary>
    /// The tray icon was clicked. When a window is connected it is told to show the pop-up and true is returned;
    /// otherwise false, and the caller starts the window with the pop-up switch.
    /// </summary>
    public bool RequestTrayPopup(int x, int y)
    {
        if (_pipe is not { ConnectionCount: > 0 })
        {
            return false;
        }

        _ = _pipe.BroadcastAsync(ControlTypes.EventTrayPopup,
            JsonSerializer.SerializeToElement(new TrayPopupEvent { X = x, Y = y }, AiHostJsonContext.Default.TrayPopupEvent));
        return true;
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
        Guard(() => _notifyStop.Cancel());
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
    /// <summary>Saves the mode (atomically, through the settings store).</summary>
    private void SaveMode(string mode)
    {
        var current = _settings.Current;
        if (current.Mode == mode)
        {
            return;
        }

        current.Mode = mode;
        _settings.Save(current);
    }

    /// <summary>Saves the client-mode choices (fallback, relay mode, prompt flag) atomically, through the settings store.</summary>
    private void SaveClientPrefs(string? fallback, string relayMode, bool promptShown)
    {
        var current = _settings.Current;
        if (current.ClientFallback == fallback && current.RelayMode == relayMode && current.FallbackPromptShown == promptShown)
        {
            return;
        }

        current.ClientFallback = fallback;
        current.RelayMode = relayMode;
        current.FallbackPromptShown = promptShown;
        _settings.Save(current);
    }

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
        await GuardAsync(async () =>
        {
            if (ClientMode is { } clientMode)
            {
                await clientMode.DisposeAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        await GuardAsync(async () =>
        {
            if (Remote is { } remote)
            {
                await remote.DisposeAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        await GuardAsync(() => Server?.StopAsync() ?? Task.CompletedTask).ConfigureAwait(false);
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

        // The store lock goes first and the control pipe last: a new host can only start once the pipe is free, so
        // it never meets a store that the old host still holds.
        if (_ownsStore)
        {
            Guard(() => (_store as IDisposable)?.Dispose());
        }

        await GuardAsync(async () => await _pipe.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
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
            _notes?.MarkUsed(modelId, DateTimeOffset.UtcNow);
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
        using var activation = Models.EnterActivation(); // refuses while a move, check or clean runs; a clean waits for this
        var stored = _store is null ? null : await _store.GetAsync(modelId, ct).ConfigureAwait(false);
        if (stored is not { IsComplete: true })
        {
            throw new ControlPipeException(ErrorCodes.NotFound, "That model is not installed.");
        }

        // The owner's choice wins over a device's: the device's claim on this kind ends first.
        if (Remote is { } remote)
        {
            await remote.ModelControl.OwnerChangedAsync(stored.Kind, ct, stored.Id).ConfigureAwait(false);
        }

        await Runtime.SetActiveAsync(stored.Kind, ToActive(stored), ct).ConfigureAwait(false);
        await PersistActiveAsync().ConfigureAwait(false);
    }

    private readonly object _activeWriteLock = new();

    private void WriteActiveModels()
    {
        lock (_activeWriteLock)
        {
            WriteActiveModelsLocked();
        }
    }

    private void WriteActiveModelsLocked()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kind in new[] { ModelKinds.Chat, ModelKinds.Embedding })
        {
            // While a device's model is active the PC's own choice is what is saved, never the device's.
            var (held, pcModelId) = Remote?.ModelControl.PcChoice(kind, Runtime.GetActive(kind)?.ModelId) ?? (false, null);
            if (held)
            {
                if (pcModelId is not null)
                {
                    map[kind] = pcModelId;
                }
            }
            else if (Runtime.GetActive(kind) is { } m)
            {
                map[kind] = m.ModelId;
            }
        }

        AtomicFile.WriteAllBytes(ActiveModelsPath, JsonSerializer.SerializeToUtf8Bytes(map, ServiceJsonContext.Default.DictionaryStringString));
    }

    private async Task PersistActiveAsync()
    {
        // The PC's own choice is saved only while that model is still installed.
        if (Remote is { } remote && _store is not null)
        {
            foreach (var kind in new[] { ModelKinds.Chat, ModelKinds.Embedding })
            {
                var (held, pcId) = remote.ModelControl.PcChoice(kind, Runtime.GetActive(kind)?.ModelId);
                if (held && pcId is not null && await _store.GetAsync(pcId, CancellationToken.None).ConfigureAwait(false) is not { IsComplete: true })
                {
                    remote.ModelControl.ForgetPcChoice(kind);
                }
            }
        }

        WriteActiveModels();
        await PushStatusAsync().ConfigureAwait(false);
    }

    /// <summary>Stops the engine of a kind and leaves it with no model. Files are never touched.</summary>
    private async Task ClearActiveAsync(string kind, CancellationToken ct)
    {
        if (Remote is { } remote)
        {
            await remote.ModelControl.OwnerChangedAsync(kind, ct).ConfigureAwait(false); // the owner's action wins
        }

        await Runtime.SetActiveAsync(kind, null, ct).ConfigureAwait(false);
        await PersistActiveAsync().ConfigureAwait(false);
    }

    /// <summary>Stops every engine and waits for the in-use marks in the store to clear.</summary>
    private async Task StopEnginesAsync(CancellationToken ct)
    {
        foreach (var sup in _o.Supervisors.Values)
        {
            if (sup.State is not EngineState.Stopped)
            {
                await Runtime.UnloadAsync(sup.Kind, ct).ConfigureAwait(false);
            }
        }

        Task chain;
        lock (_inUseGate)
        {
            chain = _inUseChain;
        }

        await chain.WaitAsync(ct).ConfigureAwait(false);
        await PushStatusAsync().ConfigureAwait(false);
    }

    /// <summary>After a move the active models live in the new store: point them there (engines are stopped).</summary>
    private async Task RefreshActiveAsync(CancellationToken ct)
    {
        if (_store is null)
        {
            return;
        }

        foreach (var kind in new[] { ModelKinds.Chat, ModelKinds.Embedding })
        {
            if (Remote is { } remote)
            {
                await remote.ModelControl.OwnerChangedAsync(kind, ct).ConfigureAwait(false); // the owner's action wins
            }

            if (Runtime.GetActive(kind) is { } m && await _store.GetAsync(m.ModelId, ct).ConfigureAwait(false) is { IsComplete: true } stored)
            {
                await Runtime.SetActiveAsync(kind, ToActive(stored), ct).ConfigureAwait(false);
            }
        }

        await PushStatusAsync().ConfigureAwait(false);
    }

    /// <summary>After a move: the new folder is the models folder from now on, and the old store is not listed as an earlier one.</summary>
    private void ApplyMovedFolder(string parent, string? oldRoot)
    {
        var next = _settings.Current;
        next.ModelsFolder = parent;
        // The old folder stays listed until its copies are gone (ForgetFolder); the new current folder is never "earlier".
        var previous = (next.PreviousStoreFolders ?? []).Where(p =>
            !string.Equals(p, _store?.Location.Path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (oldRoot is not null && !previous.Contains(oldRoot, StringComparer.OrdinalIgnoreCase))
        {
            previous.Insert(0, oldRoot);
        }

        next.PreviousStoreFolders = previous.Count == 0 ? null : previous;
        _settings.Save(next);
    }

    private readonly object _foldersLock = new();

    /// <summary>Puts a folder into the earlier-folders list in settings (host-owned) before it receives anything.</summary>
    private void RememberFolder(string folder)
    {
        lock (_foldersLock)
        {
            var next = _settings.Current;
            var list = new List<string>(next.PreviousStoreFolders ?? []);
            if (list.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            list.Insert(0, folder);
            next.PreviousStoreFolders = list;
            _settings.Save(next);
        }
    }

    private void ForgetFolder(string folder)
    {
        lock (_foldersLock)
        {
            var next = _settings.Current;
            var list = (next.PreviousStoreFolders ?? []).Where(p => !string.Equals(p, folder, StringComparison.OrdinalIgnoreCase)).ToList();
            if (list.Count == (next.PreviousStoreFolders?.Count ?? 0))
            {
                return;
            }

            next.PreviousStoreFolders = list.Count == 0 ? null : list;
            _settings.Save(next);
        }
    }

    private async Task PushStoreAsync(StoreProgressEvent e)
    {
        try
        {
            await _pipe.BroadcastAsync(ControlTypes.EventStoreProgress,
                JsonSerializer.SerializeToElement(e, AiHostJsonContext.Default.StoreProgressEvent)).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static ActiveModel ToActive(StoredModel m) =>
        new(m.Kind, m.Id, m.DisplayName, m.Files.Select(f => f.Path).ToList());

    internal ActiveModel ToActiveModel(StoredModel m) => ToActive(m);

    internal CatalogueEntry? CatalogueEntry(string id) => _o.Catalogue?.FirstOrDefault(e => e.Id == id);

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
        _notify.OnDownload(e, Models?.Find(e.CatalogueId)?.DisplayName);
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

    /// <summary>
    /// Every 15 seconds: shows approval notifications that waited for a full-screen program to end, and every 10
    /// minutes (the first after one minute) reads the health checks so a change to fail can be announced. Only
    /// reads; a failed check is ignored.
    /// </summary>
    private async Task NotificationLoopAsync(CancellationToken ct)
    {
        var tick = 0;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                _notifyCenter.Pump();
                tick++;
                if (tick % 40 != 4 || !NotificationCenter.IsEnabled(_settings.Current, NotificationKinds.Problems))
                {
                    continue;
                }

                try
                {
                    _notify.OnHealth(await Runtime.BuildHealthAsync(apiListening: Server is not null, ct).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
                {
                    // The next round tries again.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PushDeviceAlertAsync(DeviceAlert alert)
    {
        var (title, body) = alert.Kind == "paired"
            ? ("New device paired", $"{alert.DeviceName} can now use this PC's AI.")
            : ("Device connected from away", $"{alert.DeviceName} connected through the relay.");
        _notifyCenter.Raise(new ToastRequest(NotificationKinds.Devices, title, body, "settings"));
        try
        {
            await _pipe.BroadcastAsync(ControlTypes.EventDeviceAlert,
                JsonSerializer.SerializeToElement(alert, AiHostJsonContext.Default.DeviceAlert)).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private int _remoteBurst;

    private async Task PushRemoteChangedAsync()
    {
        // Bursts (a call starting, finishing, stats) become one push per 250 ms.
        if (Interlocked.Exchange(ref _remoteBurst, 1) == 1)
        {
            return;
        }

        await Task.Delay(250).ConfigureAwait(false);
        Volatile.Write(ref _remoteBurst, 0);
        try
        {
            await _pipe.BroadcastAsync(ControlTypes.EventRemoteChanged, null).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }

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

        // A notification replaces the window pop-up (a click opens the approvals page). When it cannot be shown,
        // is turned off, or the window is already open, the window opens as before.
        var pending = ControlHandler.ToPending(info);
        var outcome = _notify.OnApproval(pending.DeviceName is null ? pending.AppName : $"{pending.AppName} on {pending.DeviceName}");
        if (outcome is not (NotifyOutcome.Shown or NotifyOutcome.Deferred))
        {
            RequestShow("approvals");
        }
    }

    public async ValueTask DisposeAsync() => await QuitAsync().ConfigureAwait(false);
}
