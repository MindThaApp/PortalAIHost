using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using Portal.AiHost.Contracts;

namespace Portal.AiHost.Core.Remote.Client;

/// <summary>Where the link to the paired PC stands (docs/client-mode-v1.md section 3.5).</summary>
public enum RemoteLinkState
{
    /// <summary>Created but not started, or disposed.</summary>
    Stopped,

    /// <summary>No saved pairing, or the saved pairing cannot be used (no key, bad fingerprint).</summary>
    NotPaired,

    /// <summary>A health check is running and the link was not up before.</summary>
    Connecting,

    /// <summary>The last health check or request got an answer.</summary>
    Connected,

    /// <summary>Nothing answered, or the PC answered with a problem that may pass (see <see cref="RemoteLinkStatus.Reason"/>). Retrying with backoff.</summary>
    Unreachable,

    /// <summary>The PC no longer knows this device. Terminal: no retries, the pairing is kept for the user to forget.</summary>
    Revoked,

    /// <summary>The PC presented a certificate other than the pinned one. Terminal: never re-pinned silently.</summary>
    FingerprintChanged,
}

/// <summary>Reasons that go into <c>remote_unreachable</c> details (docs/client-mode-v1.md section 4.5).</summary>
public static class RemoteLinkReasons
{
    public const string Offline = "offline";
    public const string Clock = "clock";
    public const string Refused = "refused";
    public const string CertChanged = "cert_changed";
}

/// <summary>A snapshot of the link: state, why, the last round trip and whether the host has its own token.</summary>
public sealed record RemoteLinkStatus(RemoteLinkState State, string? Reason, int? RttMs, bool HasToken);

/// <summary>No answer could be obtained from the PC. Callers turn this into <c>503 remote_unreachable</c>.</summary>
public sealed class RemoteUnreachableException(string reason, int? retryAfterMs = null)
    : Exception($"The PC cannot be reached ({reason}).")
{
    public string Reason { get; } = reason;

    public int? RetryAfterMs { get; } = retryAfterMs;
}

public enum TokenOutcome
{
    Approved,
    Denied,
    TimedOut,
    Unreachable,
    Revoked,
    FingerprintChanged,
    NotPaired,
}

public sealed class RemoteHostLinkOptions
{
    public string AppId { get; init; } = "com.mindthaapp.portalaihost.client";

    public string AppName { get; init; } = "Portal AI Host (client mode)";

    /// <summary>Health check interval while a local app has made a request recently.</summary>
    public TimeSpan BusyInterval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Health check interval when idle.</summary>
    public TimeSpan IdleInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long after the last request the link counts as busy.</summary>
    public TimeSpan RecentWindow { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan BackoffStart { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long the PC's owner has to approve the host's token request.</summary>
    public TimeSpan TokenWait { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Random fraction in [0, 1) used for the backoff jitter. Tests fix it.</summary>
    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;

    /// <summary>Drop connections and probe at once when the network changes.</summary>
    public bool WatchNetwork { get; init; } = true;
}

/// <summary>
/// The laptop host's link to the paired PC over the home network (docs/client-mode-v1.md section 3, LAN only): pins the
/// full saved fingerprint on every TLS connection, signs every request, owns the host's own token, keeps one connection
/// alive, and checks health with a backoff. Nothing here deletes or rewrites saved state except the token file through
/// <see cref="ClientStateStore.SaveToken"/>.
/// </summary>
public sealed class RemoteHostLink : IDisposable
{
    private static readonly byte[] NoBody = [];

    private readonly ClientStateStore _store;
    private readonly IRemoteConnector _connector;
    private readonly TimeProvider _time;
    private readonly RemoteHostLinkOptions _options;
    private readonly ClientRemote? _remote;
    private readonly byte[]? _pin;
    private readonly ECDsa? _key;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();

    private IRemoteChannel? _channel;
    private volatile bool _pinRejected;
    private RemoteLinkStatus _status;
    private string? _token;
    private bool _tokenLoaded;
    private Task<TokenOutcome>? _tokenTask;
    private bool _reasked;
    private Task? _loop;
    private CancellationTokenSource? _sleep;
    private bool _kick;
    private int _failures;
    private int _retryAfterMs = 2000;
    private long _lastRequest;
    private bool _hasRequest;
    private bool _disposed;

    public RemoteHostLink(ClientStateStore store, IRemoteConnector connector, TimeProvider? time = null,
        RemoteHostLinkOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _time = time ?? TimeProvider.System;
        _options = options ?? new RemoteHostLinkOptions();
        _remote = store.LoadRemote();
        _key = store.LoadDeviceKey();
        if (_remote is not null && RemoteCodec.TryDecode(_remote.Fingerprint, 32, out var pin) && pin.Length == 32)
        {
            _pin = pin;
        }

        var usable = _remote is not null && _pin is not null && _key is not null;
        _status = new RemoteLinkStatus(usable ? RemoteLinkState.Stopped : RemoteLinkState.NotPaired, null, null,
            usable && Token() is not null);
        if (usable && _options.WatchNetwork)
        {
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        }
    }

    public RemoteLinkStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>The saved pairing this link uses (null when not paired).</summary>
    public ClientRemote? Remote => _remote;

    /// <summary>Raised after the state, the reason or the token presence changes.</summary>
    public event EventHandler<RemoteLinkStatus>? StateChanged;

    /// <summary>The delay before the next retry after <paramref name="failures"/> failures in a row (2 s doubling to 60 s).</summary>
    public static TimeSpan BackoffDelay(RemoteHostLinkOptions options, int failures, double jitter)
    {
        var steps = Math.Clamp(failures - 1, 0, 16);
        var seconds = Math.Min(options.BackoffMax.TotalSeconds, options.BackoffStart.TotalSeconds * Math.Pow(2, steps));
        return TimeSpan.FromSeconds(seconds * (0.75 + 0.25 * Math.Clamp(jitter, 0, 1)));
    }

    /// <summary>Starts the health loop (does nothing when there is no usable pairing or the link is stopped for good).</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _loop is not null || IsTerminal(_status.State))
            {
                return;
            }

            _loop = Task.Run(() => LoopAsync(_lifetime.Token));
        }
    }

    /// <summary>"Reconnect" now: drops the connection and checks health once. False when the PC did not answer or the link is stopped for good.</summary>
    public async Task<bool> ReconnectAsync(CancellationToken ct = default)
    {
        if (IsTerminal(Status.State))
        {
            return false;
        }

        DropChannel();
        var ok = await ProbeAsync(ct).ConfigureAwait(false);
        Wake();
        return ok;
    }

    /// <summary>
    /// Sends a signed request to the PC. Paths other than the ping and the access-request calls also carry the host's own
    /// token. Returns whatever the PC answered (the caller decides what a 4xx or 5xx means); throws
    /// <see cref="RemoteUnreachableException"/> when no answer could be had. A rejected token starts one new token
    /// request in the background and fails this call with reason <c>refused</c>.
    /// </summary>
    public async Task<RemoteReply> SendAsync(RemoteRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            _lastRequest = _time.GetTimestamp();
            _hasRequest = true;
        }

        var needsToken = !IsTokenFree(request.Target);
        string? token = null;
        if (needsToken)
        {
            token = Token();
            if (token is null)
            {
                ThrowIfTerminal();
                throw new RemoteUnreachableException(RemoteLinkReasons.Refused, _retryAfterMs);
            }
        }

        var reply = await SendSignedAsync(request, token, ct).ConfigureAwait(false);
        if (needsToken && reply.Status == 401 && ErrorCodeOf(reply) is ErrorCodes.TokenInvalid or ErrorCodes.TokenRevoked)
        {
            lock (_gate)
            {
                var again = !_reasked;
                _reasked = true;
                if (again)
                {
                    _ = StartTokenRequest(CancellationToken.None);
                }
            }

            throw new RemoteUnreachableException(RemoteLinkReasons.Refused, _retryAfterMs);
        }

        return reply;
    }

    /// <summary>
    /// Asks the PC's owner for the host's own token (<c>POST /host/auth/requests</c>, then polling) and stores it once
    /// approved. One request at a time; a second caller joins the one running. Cancelling or timing out writes nothing.
    /// </summary>
    public Task<TokenOutcome> RequestTokenAsync(CancellationToken ct = default)
    {
        Task<TokenOutcome> task;
        lock (_gate)
        {
            task = StartTokenRequest(ct);
        }

        return task.WaitAsync(ct);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _lifetime.Cancel();
        DropChannel();
        _key?.Dispose();
        lock (_gate)
        {
            _status = _status with { State = RemoteLinkState.Stopped };
        }
    }

    // ---- Signed transport ---------------------------------------------------------------------------------------

    private async Task<RemoteReply> SendSignedAsync(RemoteRequest request, string? token, CancellationToken ct)
    {
        ThrowIfTerminal();
        var body = request.Body ?? NoBody;
        var (timestamp, nonce, signature) = RemoteSigning.Sign(_key!, request.Method, request.Target, _time.GetUtcNow(), body);
        var headers = new List<KeyValuePair<string, string>>(request.Headers)
        {
            new(RemoteHeaders.Device, _remote!.DeviceId),
            new(RemoteHeaders.Timestamp, timestamp),
            new(RemoteHeaders.Nonce, nonce),
            new(RemoteHeaders.Signature, signature),
        };
        if (token is not null)
        {
            headers.Add(new("Authorization", "Bearer " + token));
        }

        var signed = request with { Headers = headers };
        var started = _time.GetTimestamp();
        RemoteReply reply;
        try
        {
            reply = await GetChannel().SendAsync(signed, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or TaskCanceledException
            or AuthenticationException or ObjectDisposedException)
        {
            DropChannel();
            if (_pinRejected)
            {
                SetStatus(RemoteLinkState.FingerprintChanged, RemoteLinkReasons.CertChanged);
                throw new RemoteUnreachableException(RemoteLinkReasons.CertChanged);
            }

            SetStatus(RemoteLinkState.Unreachable, RemoteLinkReasons.Offline);
            throw new RemoteUnreachableException(RemoteLinkReasons.Offline, _retryAfterMs);
        }

        if (reply.Status == 401 && ErrorCodeOf(reply) == ErrorCodes.DeviceUnauthorized)
        {
            switch (ErrorReasonOf(reply))
            {
                case DeviceAuthReasons.UnknownDevice:
                    SetStatus(RemoteLinkState.Revoked, RemoteLinkReasons.Refused);
                    DropChannel();
                    throw new RemoteUnreachableException(RemoteLinkReasons.Refused);
                case DeviceAuthReasons.StaleTimestamp:
                    SetStatus(RemoteLinkState.Unreachable, RemoteLinkReasons.Clock);
                    throw new RemoteUnreachableException(RemoteLinkReasons.Clock, _retryAfterMs);
                default:
                    SetStatus(RemoteLinkState.Unreachable, RemoteLinkReasons.Refused);
                    throw new RemoteUnreachableException(RemoteLinkReasons.Refused, _retryAfterMs);
            }
        }

        // Any other answer proves the PC is there.
        var rtt = (int)_time.GetElapsedTime(started).TotalMilliseconds;
        Interlocked.Exchange(ref _failures, 0);
        SetStatus(RemoteLinkState.Connected, null, rtt);
        return reply;
    }

    private IRemoteChannel GetChannel()
    {
        lock (_gate)
        {
            return _channel ??= OpenChannel();
        }
    }

    private IRemoteChannel OpenChannel()
    {
        _pinRejected = false;
        var pin = _pin!;
        return _connector.Open(_remote!.Address, _remote.Port, der =>
        {
            // The whole saved fingerprint, on every connection: not just the code prefix.
            var ok = CryptographicOperations.FixedTimeEquals(SHA256.HashData(der), pin);
            if (!ok)
            {
                _pinRejected = true;
            }

            return ok;
        });
    }

    private void DropChannel()
    {
        IRemoteChannel? old;
        lock (_gate)
        {
            old = _channel;
            _channel = null;
        }

        old?.Dispose();
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        DropChannel();
        Wake();
    }

    private void ThrowIfTerminal()
    {
        switch (Status.State)
        {
            case RemoteLinkState.FingerprintChanged:
                throw new RemoteUnreachableException(RemoteLinkReasons.CertChanged);
            case RemoteLinkState.Revoked or RemoteLinkState.NotPaired:
                throw new RemoteUnreachableException(RemoteLinkReasons.Refused);
        }
    }

    // ---- Token -------------------------------------------------------------------------------------------------

    private static bool IsTokenFree(string target) =>
        target.StartsWith(Routes.Ping, StringComparison.Ordinal) ||
        target.StartsWith(Routes.AuthRequests, StringComparison.Ordinal);

    private string? Token()
    {
        lock (_gate)
        {
            if (!_tokenLoaded)
            {
                _token = _store.LoadToken();
                _tokenLoaded = true;
            }

            return _token;
        }
    }

    /// <summary>Must be called with <c>_gate</c> held.</summary>
    private Task<TokenOutcome> StartTokenRequest(CancellationToken ct)
    {
        if (_tokenTask is { IsCompleted: false } running)
        {
            return running;
        }

        _tokenTask = Task.Run(() => RunTokenRequestAsync(ct));
        return _tokenTask;
    }

    private async Task<TokenOutcome> RunTokenRequestAsync(CancellationToken callerToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _lifetime.Token);
        using var deadline = new CancellationTokenSource(_options.TokenWait, _time);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, deadline.Token);
        try
        {
            var create = new AuthRequestCreate
            {
                AppId = _options.AppId,
                AppName = _options.AppName,
                Reason = "Use the AI on this PC from the laptop",
            };
            var created = await SendSignedAsync(new RemoteRequest("POST", Routes.AuthRequests, [],
                JsonSerializer.SerializeToUtf8Bytes(create, AiHostJsonContext.Default.AuthRequestCreate)), null, both.Token)
                .ConfigureAwait(false);
            if (created.Status == 403)
            {
                return TokenOutcome.Denied;
            }

            var info = created.Status == 202
                ? JsonSerializer.Deserialize(created.Body, AiHostJsonContext.Default.AuthRequestCreated)
                : null;
            if (info is null || string.IsNullOrEmpty(info.RequestId) || info.RequestId.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
            {
                return TokenOutcome.Unreachable;
            }

            while (true)
            {
                var poll = await SendSignedAsync(new RemoteRequest("GET",
                    $"{Routes.AuthRequests}/{info.RequestId}?wait_ms=10000", []), null, both.Token).ConfigureAwait(false);
                if (poll.Status != 200)
                {
                    return poll.Status == 404 ? TokenOutcome.TimedOut : TokenOutcome.Unreachable;
                }

                var state = JsonSerializer.Deserialize(poll.Body, AiHostJsonContext.Default.AuthRequestStatus);
                switch (state?.Status)
                {
                    case AuthStatuses.Approved when !string.IsNullOrEmpty(state.Token):
                        _store.SaveToken(state.Token);
                        lock (_gate)
                        {
                            _token = state.Token;
                            _tokenLoaded = true;
                            _reasked = false;
                        }

                        SetStatus(Status.State, Status.Reason, hasToken: true);
                        return TokenOutcome.Approved;
                    case AuthStatuses.Denied or AuthStatuses.Claimed or AuthStatuses.Approved:
                        return TokenOutcome.Denied;
                    case AuthStatuses.Expired:
                        return TokenOutcome.TimedOut;
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !linked.IsCancellationRequested)
        {
            return TokenOutcome.TimedOut;
        }
        catch (RemoteUnreachableException ex)
        {
            return ex.Reason switch
            {
                RemoteLinkReasons.CertChanged => TokenOutcome.FingerprintChanged,
                RemoteLinkReasons.Refused when Status.State == RemoteLinkState.Revoked => TokenOutcome.Revoked,
                RemoteLinkReasons.Refused when Status.State == RemoteLinkState.NotPaired => TokenOutcome.NotPaired,
                _ => TokenOutcome.Unreachable,
            };
        }
        catch (JsonException)
        {
            return TokenOutcome.Unreachable;
        }
    }

    // ---- Health ------------------------------------------------------------------------------------------------

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !IsTerminal(Status.State))
        {
            bool ok;
            try
            {
                ok = await ProbeAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            TimeSpan delay;
            if (ok)
            {
                bool busy;
                lock (_gate)
                {
                    busy = _hasRequest && _time.GetElapsedTime(_lastRequest) < _options.RecentWindow;
                }

                delay = busy ? _options.BusyInterval : _options.IdleInterval;
            }
            else
            {
                delay = BackoffDelay(_options, Interlocked.Increment(ref _failures), _options.Jitter());
                _retryAfterMs = (int)delay.TotalMilliseconds;
            }

            if (IsTerminal(Status.State))
            {
                return;
            }

            await SleepAsync(delay, ct).ConfigureAwait(false);
        }
    }

    private async Task SleepAsync(TimeSpan delay, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate)
        {
            if (_kick)
            {
                _kick = false;
                return;
            }

            _sleep = cts;
        }

        try
        {
            await Task.Delay(delay, _time, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Woken early (reconnect or network change).
        }
        finally
        {
            lock (_gate)
            {
                _sleep = null;
            }
        }
    }

    private void Wake()
    {
        CancellationTokenSource? sleeping;
        lock (_gate)
        {
            sleeping = _sleep;
            _kick = sleeping is null;
        }

        try
        {
            sleeping?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task<bool> ProbeAsync(CancellationToken ct)
    {
        if (IsTerminal(Status.State))
        {
            return false;
        }

        if (Status.State != RemoteLinkState.Connected)
        {
            SetStatus(RemoteLinkState.Connecting, Status.Reason);
        }

        try
        {
            var reply = await SendSignedAsync(new RemoteRequest("GET", Routes.Ping, []), null, ct).ConfigureAwait(false);
            if (reply.Status == 200)
            {
                return true;
            }

            SetStatus(RemoteLinkState.Unreachable, RemoteLinkReasons.Offline);
            return false;
        }
        catch (RemoteUnreachableException)
        {
            return false; // The state was set where the failure was seen.
        }
    }

    // ---- State -------------------------------------------------------------------------------------------------

    private static bool IsTerminal(RemoteLinkState state) =>
        state is RemoteLinkState.Revoked or RemoteLinkState.FingerprintChanged or RemoteLinkState.NotPaired;

    private void SetStatus(RemoteLinkState state, string? reason, int? rtt = null, bool? hasToken = null)
    {
        RemoteLinkStatus next;
        bool changed;
        lock (_gate)
        {
            if (_disposed || IsTerminal(_status.State) && _status.State != state)
            {
                return; // A terminal state is left only by a new link.
            }

            next = new RemoteLinkStatus(state, reason, rtt ?? _status.RttMs, hasToken ?? _status.HasToken);
            changed = next.State != _status.State || next.Reason != _status.Reason || next.HasToken != _status.HasToken;
            _status = next;
        }

        if (changed)
        {
            StateChanged?.Invoke(this, next);
        }
    }

    private static string? ErrorCodeOf(RemoteReply reply)
    {
        try
        {
            using var doc = JsonDocument.Parse(reply.Body);
            return doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var c) &&
                c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ErrorReasonOf(RemoteReply reply)
    {
        try
        {
            using var doc = JsonDocument.Parse(reply.Body);
            return doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("details", out var d) &&
                d.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
