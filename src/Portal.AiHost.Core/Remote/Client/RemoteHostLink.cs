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

    /// <summary>The relay says the PC is not connected to it (HTTP 503 host_offline, close 4404 or 4410).</summary>
    public const string RelayHostOffline = "relay_host_offline";

    /// <summary>The relay refused for budget reasons (HTTP 429, close 4429): wait about a minute.</summary>
    public const string RelayBusy = "relay_busy";
}

/// <summary>A snapshot of the link: state, why, the last round trip and whether the host has its own token.</summary>
public sealed record RemoteLinkStatus(RemoteLinkState State, string? Reason, int? RttMs, bool HasToken, string? Route = null);

/// <summary>The routes to the PC (docs/client-mode-v1.md section 3.1).</summary>
public static class RemoteRoutes
{
    public const string Lan = "lan";
    public const string Relay = "relay";
    public const string Down = "down";
}

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

    private readonly IRemoteConnector? _relayConnector;
    private IRemoteChannel? _lanChannel;
    private IRemoteChannel? _relayChannel;
    private string _route = RemoteRoutes.Lan;
    private long _lanProbedAt;
    private long _relayBlockedUntil; // TimeProvider timestamp; 0 = not blocked
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
        RemoteHostLinkOptions? options = null, Func<Uri, byte[], IRemoteConnector>? relayFactory = null)
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
        // The relay is used only when the saved pairing carries a valid relay address (re-checked here; null = none).
        if (usable && relayFactory is not null && PairingLink.ValidateRelayUrl(_remote!.RelayUrl) is { } relayUrl)
        {
            _relayConnector = relayFactory(new Uri(relayUrl), _pin!);
        }

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
        var (needsToken, token) = BeginRequest(request);
        var reply = await SendSignedAsync(request, token, ct).ConfigureAwait(false);
        if (needsToken && reply.Status == 401 && ErrorCodeOf(reply) is ErrorCodes.TokenInvalid or ErrorCodes.TokenRevoked)
        {
            throw TokenRejected();
        }

        return reply;
    }

    /// <summary>
    /// Like <see cref="SendAsync"/> but returns when the response headers arrive and hands over the body as a stream
    /// (server-sent events from the PC are read chunk by chunk). The same signing, token and error rules apply to the
    /// headers; a 401 is read in full (it is small) so its reason can be told apart. A failure while the body is read
    /// is the caller's to report with <see cref="ReportStreamDropped"/>.
    /// </summary>
    public async Task<RemoteStreamReply> OpenStreamAsync(RemoteRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (needsToken, token) = BeginRequest(request);
        ThrowIfTerminal();
        var started = _time.GetTimestamp();
        RemoteStreamReply reply;
        try
        {
            reply = await OnRoutesAsync(channel => channel.OpenAsync(Sign(request, token), ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (RoutesFailedException failed)
        {
            throw TransportFailed(failed);
        }

        if (reply.Status != 401)
        {
            MarkConnected(started);
            return reply;
        }

        string text;
        var headers = reply.Headers;
        try
        {
            using var copy = new MemoryStream();
            await reply.Body.CopyToAsync(copy, ct).ConfigureAwait(false);
            text = System.Text.Encoding.UTF8.GetString(copy.ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw TransportFailed(new RoutesFailedException(ex, null));
        }
        finally
        {
            reply.Dispose();
        }

        var asReply = new RemoteReply(401, text);
        CheckDeviceAuth(asReply);
        if (needsToken && ErrorCodeOf(asReply) is ErrorCodes.TokenInvalid or ErrorCodes.TokenRevoked)
        {
            throw TokenRejected();
        }

        MarkConnected(started);
        return new RemoteStreamReply(401, headers, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>A body stream failed after the headers were in: the PC is treated as not reachable until the next check.</summary>
    public void ReportStreamDropped()
    {
        DropChannel();
        SetStatus(RemoteLinkState.Unreachable, RemoteLinkReasons.Offline);
    }

    /// <summary>The retry delay callers pass on in <c>retry_after_ms</c>.</summary>
    public int RetryAfterMs => _retryAfterMs;

    private (bool NeedsToken, string? Token) BeginRequest(RemoteRequest request)
    {
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

        return (needsToken, token);
    }

    private RemoteUnreachableException TokenRejected()
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

        return new RemoteUnreachableException(RemoteLinkReasons.Refused, _retryAfterMs);
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

    private RemoteRequest Sign(RemoteRequest request, string? token)
    {
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

        return request with { Headers = headers };
    }

    private static bool IsTransportFailure(Exception ex) =>
        ex is HttpRequestException or IOException or SocketException or TaskCanceledException
            or AuthenticationException or ObjectDisposedException;

    /// <summary>Every route that was tried failed. <see cref="Relay"/> is what the relay said, if it was asked.</summary>
    private sealed class RoutesFailedException(Exception inner, RelayException? relay) : Exception(inner.Message, inner)
    {
        public RelayException? Relay { get; } = relay;
    }

    private RemoteUnreachableException TransportFailed(RoutesFailedException failed)
    {
        DropChannel();
        if (_pinRejected)
        {
            // Only the PC's own certificate can say this, on either route; a relay's words never do.
            SetStatus(RemoteLinkState.FingerprintChanged, RemoteLinkReasons.CertChanged);
            return new RemoteUnreachableException(RemoteLinkReasons.CertChanged);
        }

        if (failed.Relay is { } relay)
        {
            var wait = relay.RetryAfterMs ?? _retryAfterMs;
            SetStatus(RemoteLinkState.Unreachable, relay.Reason);
            return new RemoteUnreachableException(relay.Reason, wait);
        }

        SetStatus(RemoteLinkState.Unreachable, RemoteLinkReasons.Offline);
        return new RemoteUnreachableException(RemoteLinkReasons.Offline, _retryAfterMs);
    }

    private static RelayException? FindRelay(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is RelayException relay)
            {
                return relay;
            }
        }

        return null;
    }

    /// <summary>True when nothing of the request can have reached the PC, so the other route may carry it.</summary>
    private static bool NothingSent(Exception ex) =>
        ex is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError }
        || FindRelay(ex) is not null || ex.InnerException is SocketException or TimeoutException;

    /// <summary>The routes in the order to try: the last one that worked first; the relay only when the pairing has one.</summary>
    private List<string> RouteOrder()
    {
        var order = new List<string>(2);
        var relay = _relayConnector is not null;
        if (_route == RemoteRoutes.Relay && relay)
        {
            order.Add(RemoteRoutes.Relay);
            order.Add(RemoteRoutes.Lan);
        }
        else
        {
            order.Add(RemoteRoutes.Lan);
            if (relay)
            {
                order.Add(RemoteRoutes.Relay);
            }
        }

        return order;
    }

    /// <summary>
    /// Runs <paramref name="op"/> on the channel of the preferred route and, when that route could not be connected
    /// (nothing sent), once on the other. A relay that is over its budget is left alone until its wait is over. A
    /// pin mismatch on either route stops here: it is never papered over by the other one.
    /// </summary>
    private async Task<T> OnRoutesAsync<T>(Func<IRemoteChannel, Task<T>> op, CancellationToken ct)
    {
        RelayException? relayError = null;
        Exception? last = null;
        var order = RouteOrder();
        foreach (var route in order)
        {
            if (route == RemoteRoutes.Relay && RelayWaitMs() is { } blocked)
            {
                relayError = new RelayException(RemoteLinkReasons.RelayBusy, blocked, "The relay budget is used up; waiting.");
                last ??= relayError;
                continue;
            }

            try
            {
                var result = await op(GetChannel(route)).ConfigureAwait(false);
                _route = route;
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                last = ex;
                DropChannel(route);
                if (route == RemoteRoutes.Relay)
                {
                    relayError = FindRelay(ex) ?? relayError;
                    if (relayError is { Reason: RemoteLinkReasons.RelayBusy })
                    {
                        var wait = (relayError.RetryAfterMs ?? RelayException.BudgetRetryMs) / 1000.0;
                        Interlocked.Exchange(ref _relayBlockedUntil, _time.GetTimestamp() + (long)(wait * _time.TimestampFrequency));
                    }
                }

                if (_pinRejected || !NothingSent(ex))
                {
                    break;
                }
            }
        }

        throw new RoutesFailedException(last ?? new IOException("No route to the PC."), relayError);
    }

    /// <summary>Milliseconds the relay budget still asks for, or null when the relay may be used.</summary>
    private int? RelayWaitMs()
    {
        var until = Interlocked.Read(ref _relayBlockedUntil);
        if (until == 0)
        {
            return null;
        }

        var left = (until - _time.GetTimestamp()) * 1000.0 / _time.TimestampFrequency;
        if (left <= 0)
        {
            Interlocked.Exchange(ref _relayBlockedUntil, 0);
            return null;
        }

        return (int)Math.Ceiling(left);
    }

    private async Task<RemoteReply> SendSignedAsync(RemoteRequest request, string? token, CancellationToken ct)
    {
        ThrowIfTerminal();
        var started = _time.GetTimestamp();
        RemoteReply reply;
        try
        {
            reply = await OnRoutesAsync(channel => channel.SendAsync(Sign(request, token), ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (RoutesFailedException failed)
        {
            throw TransportFailed(failed);
        }

        CheckDeviceAuth(reply);
        MarkConnected(started);
        return reply;
    }

    private void CheckDeviceAuth(RemoteReply reply)
    {
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
    }

    private void MarkConnected(long started)
    {
        // Any other answer proves the PC is there.
        var rtt = (int)_time.GetElapsedTime(started).TotalMilliseconds;
        Interlocked.Exchange(ref _failures, 0);
        SetStatus(RemoteLinkState.Connected, null, rtt);
    }

    private IRemoteChannel GetChannel(string route)
    {
        lock (_gate)
        {
            if (route == RemoteRoutes.Relay)
            {
                return _relayChannel ??= OpenChannel(_relayConnector!);
            }

            return _lanChannel ??= OpenChannel(_connector);
        }
    }

    private IRemoteChannel OpenChannel(IRemoteConnector connector)
    {
        _pinRejected = false;
        var pin = _pin!;
        return connector.Open(_remote!.Address, _remote.Port, der =>
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

    /// <summary>Drops the connections of both routes and goes back to trying the home network first.</summary>
    private void DropChannel()
    {
        IRemoteChannel? lan, relay;
        lock (_gate)
        {
            lan = _lanChannel;
            relay = _relayChannel;
            _lanChannel = null;
            _relayChannel = null;
            _route = RemoteRoutes.Lan;
        }

        lan?.Dispose();
        relay?.Dispose();
    }

    /// <summary>Drops one route's connection after a failure; the route order is kept.</summary>
    private void DropChannel(string route)
    {
        IRemoteChannel? old;
        lock (_gate)
        {
            if (route == RemoteRoutes.Relay)
            {
                old = _relayChannel;
                _relayChannel = null;
            }
            else
            {
                old = _lanChannel;
                _lanChannel = null;
            }
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
                if (_route == RemoteRoutes.Relay)
                {
                    await TryBackToLanAsync(ct).ConfigureAwait(false);
                }
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

    /// <summary>
    /// While the relay carries the link, the home network is tried again about once a minute with a signed ping on a
    /// separate connection. When it answers, new requests use it; requests already running on the relay are left alone
    /// (they are never moved), and the relay connection is let go once idle.
    /// </summary>
    private async Task TryBackToLanAsync(CancellationToken ct)
    {
        if (_lanProbedAt != 0 && _time.GetElapsedTime(_lanProbedAt) < _options.IdleInterval)
        {
            return;
        }

        _lanProbedAt = _time.GetTimestamp();
        var matched = true;
        var pin = _pin!;
        IRemoteChannel? probe = null;
        try
        {
            probe = _connector.Open(_remote!.Address, _remote.Port, der =>
                matched = CryptographicOperations.FixedTimeEquals(SHA256.HashData(der), pin));
            var started = _time.GetTimestamp();
            var reply = await probe.SendAsync(Sign(new RemoteRequest("GET", Routes.Ping, []), null), ct).ConfigureAwait(false);
            if (reply.Status != 200 || !matched)
            {
                return;
            }

            IRemoteChannel? old;
            lock (_gate)
            {
                old = _lanChannel;
                _lanChannel = probe;
                probe = null;
                _route = RemoteRoutes.Lan;
            }

            old?.Dispose();
            MarkConnected(started);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            // Still away from home.
        }
        finally
        {
            probe?.Dispose();
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

            var route = state == RemoteLinkState.Connected ? _route : RemoteRoutes.Down;
            next = new RemoteLinkStatus(state, reason, rtt ?? _status.RttMs, hasToken ?? _status.HasToken, route);
            changed = next.State != _status.State || next.Reason != _status.Reason || next.HasToken != _status.HasToken
                || next.Route != _status.Route;
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
