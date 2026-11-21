using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Portal.AiHost.Client.Internal;
using Portal.AiHost.Contracts;

namespace Portal.AiHost.Client;

/// <summary>
/// Talks to Portal AI Host on this PC: discovery, access approval, chat, embeddings and host helpers, with
/// retries and an optional local-model fallback. Create one per app and share it; members are thread-safe.
/// Calls other than the model calls may throw <see cref="AiHostException"/>; <see cref="CheckAsync"/> never does.
/// Cancelling a token throws <see cref="OperationCanceledException"/> as usual.
/// </summary>
public sealed partial class AiHostClient : IDisposable
{
    private readonly AiHostClientOptions _options;
    private readonly HttpClient _http;
    private readonly HostDiscovery _discovery;
    private readonly ITokenStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private HostEndpoint? _endpoint;
    private string? _token;
    private bool _tokenLoaded;
    private string? _pendingRequestId;
    private DateTimeOffset _pendingExpires;
    private int _pendingPollMs = ApiLimits.AuthWaitMsDefault;
    private AiHostState _state = AiHostState.Unknown;

    private AiHostClient(AiHostClientOptions options)
    {
        _options = options;
        _http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            AllowAutoRedirect = false,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _discovery = new HostDiscovery(_http, options, Log);
        _store = options.TokenStore ?? CreateDefaultStore(options.AppId);
    }

    /// <summary>Raised after <see cref="State"/> changes.</summary>
    public event EventHandler<AiHostStateChangedEventArgs>? StateChanged;

    /// <summary>Raised when a call is answered by the fallback model. Show <see cref="FallbackActivatedEventArgs.Message"/>.</summary>
    public event EventHandler<FallbackActivatedEventArgs>? FallbackActivated;

    /// <summary>The last known state. Call <see cref="CheckAsync"/> to refresh it.</summary>
    public AiHostState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    /// <summary>The host's root address (for example <c>http://127.0.0.1:41870/</c>) once discovered, else null.</summary>
    public Uri? BaseAddress => _endpoint?.BaseAddress;

    /// <summary>The address to give an OpenAI SDK as its base URL (the host root plus <c>v1/</c>), or null.</summary>
    public Uri? OpenAiBaseAddress => _endpoint is { } e ? new Uri(e.BaseAddress, "v1/") : null;

    /// <summary>
    /// The stored token to use as an OpenAI SDK's API key, or null if there is none. Available after
    /// <see cref="CheckAsync"/>, <see cref="RequestAccessAsync"/> or the first call. Never log it.
    /// </summary>
    public string? GetBearerToken() => _token;

    /// <summary>Creates a client. Nothing is contacted until the first call.</summary>
    public static AiHostClient Create(AiHostClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AppDisplayName);
        return new AiHostClient(options);
    }

    /// <summary>
    /// Finds the host and checks access and model availability. Never throws for host problems; it returns the
    /// resulting <see cref="State"/>.
    /// </summary>
    public async Task<AiHostState> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (await EnsureEndpointAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                return State;
            }

            if (await GetTokenAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                SetState(State is AiHostState.Denied or AiHostState.Revoked ? State : AiHostState.NeedsAccess);
                return State;
            }

            var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            SetState(status.State is HostStates.NoModel or HostStates.Downloading ? AiHostState.NoModel : AiHostState.Ready);
        }
        catch (AiHostException)
        {
            // the failure already updated the state
        }

        return State;
    }

    /// <summary>
    /// Asks the host for access. Call it from a user action, never silently at start-up. With
    /// <paramref name="wait"/> it polls until the user decides (up to 10 minutes). Without it, it returns once the
    /// request is created; call again with <c>wait: true</c> to continue the same request.
    /// </summary>
    public async Task<AccessResult> RequestAccessAsync(bool wait = true, CancellationToken cancellationToken = default)
    {
        if (await EnsureEndpointAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return new AccessResult(State, "unavailable");
        }

        try
        {
            if (_pendingRequestId is null || DateTimeOffset.UtcNow >= _pendingExpires)
            {
                var create = new AuthRequestCreate
                {
                    AppId = _options.AppId,
                    AppName = _options.AppDisplayName,
                    AppVersion = _options.AppVersion,
                    Reason = _options.AccessReason,
                };
                var body = AiHostJson.SerializeToUtf8(create, AiHostJsonContext.Default.AuthRequestCreate);
                using var resp = await SendCoreAsync(HttpMethod.Post, "host/auth/requests", body, null, false, false,
                    cancellationToken).ConfigureAwait(false);
                var created = await ReadJsonAsync(resp, AiHostJsonContext.Default.AuthRequestCreated, cancellationToken)
                    .ConfigureAwait(false);
                _pendingRequestId = created.RequestId;
                _pendingExpires = created.ExpiresAt;
                _pendingPollMs = Math.Clamp(created.PollWaitMs, 100, ApiLimits.AuthWaitMsDefault);
            }

            if (State is AiHostState.Denied or AiHostState.Revoked or AiHostState.Unknown)
            {
                SetState(AiHostState.NeedsAccess);
            }

            if (!wait)
            {
                return new AccessResult(State, AuthStatuses.Pending);
            }

            return await PollAccessAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AiHostException ex) when (ex.Code == ErrorCodes.AccessDenied)
        {
            _pendingRequestId = null;
            SetState(AiHostState.Denied);
            return new AccessResult(AiHostState.Denied, AuthStatuses.Denied);
        }
        catch (AiHostUnavailableException)
        {
            return new AccessResult(State, "unavailable");
        }
    }

    /// <summary>Signs out: asks the host to revoke this app's token (best effort), then deletes the stored token.</summary>
    public async Task ForgetAccessAsync(CancellationToken cancellationToken = default)
    {
        if (await GetTokenAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            try
            {
                using var resp = await SendCoreAsync(HttpMethod.Delete, "host/auth/token", null, null, true, false,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AiHostException)
            {
                // the host may be down or the token already gone; the local token is removed either way
            }
        }

        await _store.DeleteAsync(cancellationToken).ConfigureAwait(false);
        _token = null;
        _tokenLoaded = true;
        _pendingRequestId = null;
        SetState(AiHostState.NeedsAccess);
    }

    /// <summary>
    /// Starts the host (opens the <c>portal-ai-host:start</c> link), then waits for it to answer.
    /// Returns true if the host is now reachable.
    /// </summary>
    public async Task<bool> TryStartHostAsync(CancellationToken cancellationToken = default)
    {
        var launcher = _options.HostLauncher ?? LaunchByProtocolAsync;
        bool started;
        try
        {
            started = await launcher(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log("host launch failed");
            return false;
        }

        if (!started)
        {
            return false;
        }

        var sw = Stopwatch.StartNew();
        while (true)
        {
            InvalidateEndpoint();
            if (await EnsureEndpointAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                await CheckAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (sw.Elapsed >= _options.HostStartWait)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Gets <c>/host/status</c>. Throws <see cref="AiHostException"/> on failure.</summary>
    public async Task<HostStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var resp = await SendCoreAsync(HttpMethod.Get, "host/status", null, null, true, false, cancellationToken)
            .ConfigureAwait(false);
        return await ReadJsonAsync(resp, AiHostJsonContext.Default.HostStatus, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets <c>/host/capabilities</c>. Re-fetch when the revision changes. Throws <see cref="AiHostException"/>.</summary>
    public async Task<HostCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        using var resp = await SendCoreAsync(HttpMethod.Get, "host/capabilities", null, null, true, false, cancellationToken)
            .ConfigureAwait(false);
        return await ReadJsonAsync(resp, AiHostJsonContext.Default.HostCapabilities, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets <c>/v1/models</c>. Throws <see cref="AiHostException"/> on failure.</summary>
    public async Task<ModelList> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        using var resp = await SendCoreAsync(HttpMethod.Get, "v1/models", null, null, true, false, cancellationToken)
            .ConfigureAwait(false);
        return await ReadJsonAsync(resp, AiHostJsonContext.Default.ModelList, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts loading a model in the background (<c>chat</c> or <c>embedding</c>).</summary>
    public async Task<WarmupResponse> WarmUpAsync(string kind = ModelKinds.Chat, CancellationToken cancellationToken = default)
    {
        var body = AiHostJson.SerializeToUtf8(new WarmupRequest { Kind = kind }, AiHostJsonContext.Default.WarmupRequest);
        using var resp = await SendCoreAsync(HttpMethod.Post, "host/warmup", body, null, true, false, cancellationToken)
            .ConfigureAwait(false);
        return await ReadJsonAsync(resp, AiHostJsonContext.Default.WarmupResponse, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Counts tokens of a message list, exactly as the active chat model will (includes the chat template).</summary>
    public async Task<TokenizeResponse> CountTokensAsync(IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var request = new TokenizeRequest { Model = "default", Messages = messages.ToList() };
        return await TokenizeAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Counts tokens of plain text.</summary>
    public async Task<TokenizeResponse> CountTokensAsync(string text, CancellationToken cancellationToken = default) =>
        await TokenizeAsync(new TokenizeRequest { Model = "default", Text = text }, cancellationToken).ConfigureAwait(false);

    /// <summary>Releases the HTTP connection pool.</summary>
    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    private async Task<TokenizeResponse> TokenizeAsync(TokenizeRequest request, CancellationToken ct)
    {
        var body = AiHostJson.SerializeToUtf8(request, AiHostJsonContext.Default.TokenizeRequest);
        using var resp = await SendCoreAsync(HttpMethod.Post, "host/tokenize", body, null, true, false, ct).ConfigureAwait(false);
        return await ReadJsonAsync(resp, AiHostJsonContext.Default.TokenizeResponse, ct).ConfigureAwait(false);
    }

    private async Task<AccessResult> PollAccessAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_pendingRequestId is null)
            {
                return new AccessResult(State, AuthStatuses.Expired);
            }

            if (DateTimeOffset.UtcNow > _pendingExpires.AddSeconds(5))
            {
                _pendingRequestId = null;
                return new AccessResult(State, AuthStatuses.Expired);
            }

            AuthRequestStatus status;
            try
            {
                using var resp = await SendCoreAsync(HttpMethod.Get,
                    $"host/auth/requests/{Uri.EscapeDataString(_pendingRequestId)}?wait_ms={_pendingPollMs}",
                    null, null, false, false, ct).ConfigureAwait(false);
                status = await ReadJsonAsync(resp, AiHostJsonContext.Default.AuthRequestStatus, ct).ConfigureAwait(false);
            }
            catch (AiHostException ex) when (ex.Code == ErrorCodes.NotFound)
            {
                _pendingRequestId = null; // the host restarted and forgot the request
                return new AccessResult(State, AuthStatuses.Expired);
            }

            switch (status.Status)
            {
                case AuthStatuses.Approved when !string.IsNullOrEmpty(status.Token):
                    await _store.SaveAsync(status.Token, ct).ConfigureAwait(false);
                    _token = status.Token;
                    _tokenLoaded = true;
                    _pendingRequestId = null;
                    SetState(AiHostState.Ready);
                    await CheckAsync(ct).ConfigureAwait(false);
                    return new AccessResult(State, AuthStatuses.Approved);
                case AuthStatuses.Denied:
                    _pendingRequestId = null;
                    SetState(AiHostState.Denied);
                    return new AccessResult(AiHostState.Denied, AuthStatuses.Denied);
                case AuthStatuses.Pending:
                    continue;
                default: // expired, claimed, or an unknown status
                    _pendingRequestId = null;
                    return new AccessResult(State, status.Status);
            }
        }
    }

    private static async Task<bool> LaunchByProtocolAsync(CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.Start(new ProcessStartInfo($"{ApiInfo.ProtocolScheme}:start") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false; // the link is not registered: the host is not installed
        }
    }

    private static ITokenStore CreateDefaultStore(string appId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Provide a TokenStore on this platform.");
        }

        return CreateDpapiStore(appId);
    }

    [SupportedOSPlatform("windows")]
    private static DpapiTokenStore CreateDpapiStore(string appId) =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appId, "portal-ai-host"), appId);

    private async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        if (_tokenLoaded)
        {
            return _token;
        }

        _token = await _store.LoadAsync(ct).ConfigureAwait(false);
        _tokenLoaded = true;
        return _token;
    }

    private async Task<HostEndpoint?> EnsureEndpointAsync(CancellationToken ct)
    {
        if (_endpoint is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_endpoint is { } again)
            {
                return again;
            }

            var found = await _discovery.DiscoverAsync(ct).ConfigureAwait(false);
            _endpoint = found;
            if (found is null)
            {
                SetState(_discovery.LooksInstalled() ? AiHostState.NotRunning : AiHostState.NotInstalled);
            }
            else
            {
                Log("host found via " + found.Source);
                if (State is AiHostState.Unknown or AiHostState.NotInstalled or AiHostState.NotRunning)
                {
                    SetState(AiHostState.NeedsAccess);
                }
            }

            return found;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void InvalidateEndpoint() => _endpoint = null;

    private void SetState(AiHostState next)
    {
        AiHostState old;
        lock (_stateLock)
        {
            old = _state;
            if (old == next)
            {
                return;
            }

            _state = next;
        }

        StateChanged?.Invoke(this, new AiHostStateChangedEventArgs(old, next));
    }

    private void Log(string message) => _options.Logger?.Invoke(message);
}
