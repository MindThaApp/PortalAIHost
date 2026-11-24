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

    public ClientModeController(AiHostRuntime runtime, RemoteAccess remote, ClientModeOptions options)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _o = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Mode => _mode;

    /// <summary>Raised after the mode or the link state changed.</summary>
    public event Action? Changed;

    /// <summary>The mode and, in client mode, the link state. Holds nothing secret.</summary>
    public ClientModeStatus Snapshot()
    {
        var link = _link;
        var status = new ClientModeStatus { Mode = _mode, Paired = link?.Remote is not null };
        if (_mode == HostModes.Client && link is not null)
        {
            var s = link.Status;
            status.PcName = link.Remote?.PcName;
            status.State = StateName(s.State);
            status.Route = s.State == RemoteLinkState.Connected ? "lan" : "down";
            status.Reason = s.Reason;
            status.RttMs = s.RttMs;
        }

        return status;
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
            _runtime.SetBackend(new RemoteBackend(link, _o.Backend));
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
        _runtime.SetBackend(null);
        _mode = HostModes.Server;
        _link = null;
        link.Dispose();
        await _remote.SuspendAsync(false).ConfigureAwait(false);
    }

    private async Task LeaveAsync(CancellationToken ct)
    {
        var link = _link;
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

    private void OnLinkChanged(object? sender, RemoteLinkStatus status)
    {
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
