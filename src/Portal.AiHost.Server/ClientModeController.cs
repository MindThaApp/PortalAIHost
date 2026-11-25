using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Remote.Client;
using Portal.AiHost.Core.Runtime;

namespace Portal.AiHost.Server;

public sealed class ClientModeOptions
{
    /// <summary>Creates the link to the saved PC, or returns null when none is paired. Called once per entry into client mode.</summary>
    public required Func<RemoteHostLink?> CreateLink { get; init; }

    /// <summary>Saves the mode (<c>server</c> or <c>client</c>) in the settings store. Called after a switch worked.</summary>
    public Action<string>? PersistMode { get; init; }

    /// <summary>How long requests the local engine is still answering get to finish before the engine is stopped.</summary>
    public TimeSpan DrainGrace { get; init; } = TimeSpan.FromSeconds(30);

    public RemoteBackendOptions? Backend { get; init; }

    /// <summary>The saved <c>client_fallback</c> (null = never chosen, reads as none).</summary>
    public string? InitialFallback { get; init; }

    /// <summary>The saved <c>relay_mode</c> (<c>auto</c> or <c>manual</c>).</summary>
    public string InitialRelayMode { get; init; } = RelayModes.Auto;

    /// <summary>The one-time prompt was already shown (or a fallback was chosen) in an earlier run.</summary>
    public bool InitialPromptShown { get; init; }

    /// <summary>Saves the fallback, the relay mode and the prompt flag in the settings store (atomically). Called after a change.</summary>
    public Action<string?, string, bool>? PersistPrefs { get; init; }

    /// <summary>Which PC model each local app asks for (<c>clientpps.json</c>). Null: no mapping (Model control is not offered).</summary>
    public AppModelStore? AppModels { get; init; }

    /// <summary>Clock for the forwarding backend (fake in tests).</summary>
    public TimeProvider? Time { get; init; }

    /// <summary>The engine kinds stopped when client mode starts.</summary>
    public IReadOnlyList<string> Kinds { get; init; } = [ModelKinds.Chat, ModelKinds.Embedding];
}

/// <summary>
/// Switches the host between <c>server</c> and <c>client</c> mode without a restart (docs/client-mode-v1.md section 1).
/// Entering client mode turns the LAN listener and the relay bridge off, points the model routes at the paired PC,
/// lets requests the local engine is still answering finish, stops the local engines, and bumps the capabilities
/// revision so apps read them again. Leaving does the reverse; the engine loads again on the first request. No model
/// file, download or pairing is touched by either switch.
/// </summary>
public sealed class ClientModeController : IAsyncDisposable
{
    private readonly AiHostRuntime _runtime;
    private readonly RemoteAccess _remote;
    private readonly ClientModeOptions _o;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RemoteHostLink? _link;
    private string _mode = HostModes.Server;
    private FallbackRouter? _router;
    private RemoteModelClient? _models;
    private volatile string? _fallback;
    private volatile string _relayMode = RelayModes.Auto;
    private volatile bool _promptShown;
    private volatile bool _promptPending;
    private volatile bool _useRelay;

    public ClientModeController(AiHostRuntime runtime, RemoteAccess remote, ClientModeOptions options)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _o = options ?? throw new ArgumentNullException(nameof(options));
        _fallback = _o.InitialFallback is ClientFallbackModes.None or ClientFallbackModes.Local ? _o.InitialFallback : null;
        _relayMode = _o.InitialRelayMode == RelayModes.Manual ? RelayModes.Manual : RelayModes.Auto;
        _promptShown = _o.InitialPromptShown || _fallback is not null;
    }

    /// <summary>The router answering the model routes in client mode (null in server mode).</summary>
    public FallbackRouter? Router => _router;

    /// <summary>Model control calls to the PC (null in server mode).</summary>
    public RemoteModelClient? Models => _models;

    /// <summary>The per-app PC model choices (null when not configured).</summary>
    public AppModelStore? AppModels => _o.AppModels;

    /// <summary>The effective <c>client_fallback</c>: <c>none</c> until the user picks.</summary>
    public string Fallback => _fallback ?? ClientFallbackModes.None;

    public string RelayMode => _relayMode;

    /// <summary>The one-time prompt is waiting for the UI.</summary>
    public bool FallbackPromptPending => _promptPending;

    /// <summary>Chooses what apps get while the home PC is unreachable. Choosing also ends the one-time prompt.</summary>
    public void SetFallback(string fallback)
    {
        if (fallback is not (ClientFallbackModes.None or ClientFallbackModes.Local))
        {
            throw new ArgumentException("fallback must be none or local.", nameof(fallback));
        }

        _fallback = fallback;
        _promptShown = true;
        _promptPending = false;
        Persist();
        _runtime.BumpRevision();
        Changed?.Invoke();
    }

    /// <summary>Auto uses the relay whenever the home network fails; Manual only while <see cref="SetUseRelay"/> is on.</summary>
    public void SetRelayMode(string mode)
    {
        if (mode is not (RelayModes.Auto or RelayModes.Manual))
        {
            throw new ArgumentException("relay mode must be auto or manual.", nameof(mode));
        }

        _relayMode = mode;
        Persist();
        Changed?.Invoke();
    }

    /// <summary>Turns the relay on or off for this session. Not saved: it resets when the host quits.</summary>
    public void SetUseRelay(bool enabled)
    {
        _useRelay = enabled;
        Changed?.Invoke();
        if (enabled && _link is { } link)
        {
            _ = link.ReconnectAsync(); // Try again now that the relay is allowed.
        }
    }

    /// <summary>The UI showed the one-time prompt: it is not raised again.</summary>
    public void MarkPromptShown()
    {
        _promptShown = true;
        _promptPending = false;
        Persist();
        Changed?.Invoke();
    }

    private void Persist() => _o.PersistPrefs?.Invoke(_fallback, _relayMode, _promptShown);

    private bool RelayAllowed() => _relayMode == RelayModes.Auto || _useRelay;

    public string Mode => _mode;

    /// <summary>Raised after the mode or the link state changed.</summary>
    public event Action? Changed;

    /// <summary>The mode and, in client mode, the link state. Holds nothing secret.</summary>
    public ClientModeStatus Snapshot()
    {
        var link = _link;
        var status = new ClientModeStatus
        {
            Mode = _mode,
            Paired = link?.Remote is not null,
            ClientFallback = Fallback,
            FallbackUnset = _fallback is null,
            FallbackPromptPending = _promptPending,
            UsingLaptopModel = _mode == HostModes.Client && _router?.UsingLocal == true,
            RelayMode = _relayMode,
            UseRelay = _useRelay,
        };
        if (_mode == HostModes.Client && link is not null)
        {
            var s = link.Status;
            status.PcName = link.Remote?.PcName;
            status.State = StateName(s.State);
            status.Route = s.State == RemoteLinkState.Connected ? s.Route ?? RemoteRoutes.Lan : RemoteRoutes.Down;
            status.Reason = s.Reason;
            status.RttMs = s.RttMs;
            if (_models is { } models)
            {
                status.ModelControl = models.Granted;
                status.LoadedOnPc = models.LoadedByThisLaptop.ToList();
            }
        }

        return status;
    }

    /// <summary>For <c>/host/status</c>: null in server mode; in client mode the PC's name, the route and the last round trip.</summary>
    public StatusRemote? StatusRemote()
    {
        var link = _link;
        if (_mode != HostModes.Client || link is null)
        {
            return null;
        }

        var s = link.Status;
        return new StatusRemote
        {
            Name = link.Remote?.PcName ?? "",
            Route = s.State == RemoteLinkState.Connected ? s.Route ?? RemoteRoutes.Lan : RemoteRoutes.Down,
            RttMs = s.RttMs,
        };
    }

    private static string StateName(RemoteLinkState state) => state switch
    {
        RemoteLinkState.FingerprintChanged => "fingerprint_changed",
        RemoteLinkState.NotPaired => "not_paired",
        _ => state.ToString().ToLowerInvariant(),
    };

    /// <summary>Applies the mode saved in the settings at start. A client mode that cannot start falls back to server mode.</summary>
    public async Task RestoreAsync(string? savedMode, CancellationToken ct = default)
    {
        if (savedMode == HostModes.Client)
        {
            var result = await SetModeAsync(HostModes.Client, ct).ConfigureAwait(false);
            if (result.Refused is not null)
            {
                // No usable pairing: stay a server and forget the saved client mode.
                _o.PersistMode?.Invoke(HostModes.Server);
            }
        }
    }

    public async Task<ClientModeStatus> SetModeAsync(string mode, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (mode == HostModes.Client && _mode != HostModes.Client)
            {
                if (!await EnterAsync(ct).ConfigureAwait(false))
                {
                    return Snapshot().WithRefused("not_paired");
                }
            }
            else if (mode == HostModes.Server && _mode != HostModes.Server)
            {
                await LeaveAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
        return Snapshot();
    }

    private async Task<bool> EnterAsync(CancellationToken ct)
    {
        var link = _o.CreateLink();
        if (link is null || link.Remote is null || link.Status.State == RemoteLinkState.NotPaired)
        {
            link?.Dispose();
            _link = null;
            return false;
        }

        try
        {
            // 1. Nothing listens on the network or connects to a relay while this host is a client.
            await _remote.SuspendAsync(true, ct).ConfigureAwait(false);

            // 2. New requests go to the PC from here on (this also bumps the revision apps watch).
            link.StateChanged += OnLinkChanged;
            link.Start();
            _link = link;
            link.RelayPolicy = RelayAllowed;
            var models = new RemoteModelClient(link);
            models.Changed += OnModelsChanged;
            _models = models;
            var router = new FallbackRouter(link, _runtime.LocalInferenceBackend, () => _fallback, BackendOptions(models), _o.Time);
            router.UsingLocalChanged += OnUsingLocalChanged;
            _router = router;
            _runtime.SetBackend(router);
            _mode = HostModes.Client;

            // 3. Requests the local engine is answering finish first; then the engines stop. Model files stay.
            var until = DateTime.UtcNow + _o.DrainGrace;
            while (_runtime.LocalInFlightRequests > 0 && DateTime.UtcNow < until && !ct.IsCancellationRequested)
            {
                await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
            }

            foreach (var kind in _o.Kinds)
            {
                try
                {
                    await _runtime.UnloadAsync(kind, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ApiException or EngineException or InvalidOperationException)
                {
                    // No model of that kind, or nothing running: nothing to stop.
                }
            }

            _runtime.BumpRevision();
            _o.PersistMode?.Invoke(HostModes.Client);
            return true;
        }
        catch
        {
            await RollBackAsync(link).ConfigureAwait(false);
            throw;
        }
    }

    private async Task RollBackAsync(RemoteHostLink link)
    {
        link.StateChanged -= OnLinkChanged;
        DropRouter();
        _runtime.SetBackend(null);
        _mode = HostModes.Server;
        _link = null;
        link.Dispose();
        await _remote.SuspendAsync(false).ConfigureAwait(false);
    }

    private async Task LeaveAsync(CancellationToken ct)
    {
        var link = _link;
        DropRouter();
        _runtime.SetBackend(null);
        _mode = HostModes.Server;
        _link = null;
        if (link is not null)
        {
            link.StateChanged -= OnLinkChanged;
            link.Dispose();
        }

        // The listener and the bridge come back as the saved settings say; the engine loads on the first request.
        await _remote.SuspendAsync(false, ct).ConfigureAwait(false);
        _o.PersistMode?.Invoke(HostModes.Server);
    }

    private RemoteBackendOptions BackendOptions(RemoteModelClient models)
    {
        var o = _o.Backend ?? new RemoteBackendOptions();
        var maps = _o.AppModels;
        return new RemoteBackendOptions
        {
            CapabilitiesCache = o.CapabilitiesCache,
            QueuedCommentInterval = o.QueuedCommentInterval,
            DeadlineMargin = o.DeadlineMargin,
            MaxErrorBytes = o.MaxErrorBytes,
            AppName = o.AppName,
            OnUnreachable = o.OnUnreachable,
            // The choice only applies while this laptop has Model control; without it `model` stays as the app sent it.
            ModelFor = maps is null ? null : principal => models.Granted == true ? maps.Get(principal.AppId) : null,
            OnModelControl = models.NoteGrant,
        };
    }

    private void OnModelsChanged() => Changed?.Invoke();

    private void DropRouter()
    {
        if (_models is { } models)
        {
            models.Changed -= OnModelsChanged;
        }

        _models = null;
        if (_router is { } router)
        {
            router.UsingLocalChanged -= OnUsingLocalChanged;
        }

        _router = null;
    }

    private void OnUsingLocalChanged()
    {
        _runtime.BumpRevision();
        Changed?.Invoke();
    }

    private void OnLinkChanged(object? sender, RemoteLinkStatus status)
    {
        _router?.LinkChanged();
        // The first time the PC cannot be reached while no fallback was ever chosen: one calm prompt, never repeated.
        if (status.State is RemoteLinkState.Unreachable or RemoteLinkState.Revoked or RemoteLinkState.FingerprintChanged
            && _fallback is null && !_promptShown && !_promptPending)
        {
            _promptPending = true;
        }

        _runtime.BumpRevision();
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _link?.Dispose();
        _link = null;
        _gate.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

internal static class ClientModeStatusExtensions
{
    public static ClientModeStatus WithRefused(this ClientModeStatus status, string reason)
    {
        status.Refused = reason;
        return status;
    }
}
