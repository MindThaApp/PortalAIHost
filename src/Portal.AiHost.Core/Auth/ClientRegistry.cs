using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Settings;

namespace Portal.AiHost.Core.Auth;

public sealed class ClientRegistryOptions
{
    /// <summary>Folder holding clients.json and host.key (the host's LocalState, or a test root).</summary>
    public required string DataDirectory { get; init; }

    public TimeSpan RequestLifetime { get; init; } = TimeSpan.FromMinutes(ApiLimits.AuthRequestLifetimeMinutes);
    public int RequestsPerHourPerApp { get; init; } = ApiLimits.AuthRequestsPerHourPerApp;
    public TimeSpan DenyCooldown { get; init; } = TimeSpan.FromHours(ApiLimits.DeniedCooldownHours);

    /// <summary>Longest single long-poll wait.</summary>
    public TimeSpan MaxPollWait { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a finished request still answers polls (denied, claimed, expired) before 404.</summary>
    public TimeSpan FinishedRetention { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Pending requests one calling program may have open at once (all app ids together).</summary>
    public int MaxPendingPerCaller { get; init; } = 8;

    /// <summary>Pending requests the host keeps open at once, so prompts cannot be flooded.</summary>
    public int MaxPendingPerHost { get; init; } = 16;

    /// <summary>Last-used times are written to disk at most this often per token.</summary>
    public TimeSpan LastUsedWriteInterval { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// App registry and token authority (architecture section 3, api-v1 section 3).
/// <list type="bullet">
/// <item>Tokens are created only when an approved request is first polled, handed out exactly once, and never
/// stored: clients.json keeps a keyed HMAC-SHA256 digest. The key is DPAPI-protected (host.key).</item>
/// <item>Validation compares the digest against every record in constant time, with no early exit.</item>
/// <item>A new token for the same app and device revokes the earlier one (rotation on re-approval).</item>
/// <item>A denied app is refused for 24 h unless re-allowed; one pending request per app; 10 requests per hour.</item>
/// </list>
/// All state changes run under one lock; persistence is atomic and happens before a token leaves the host.
/// </summary>
public sealed partial class ClientRegistry : IClientRegistry, ICallerBoundRequests, IDisposable
{
    public const string FileName = "clients.json";
    public const int SchemaVersion = 1;
    public const int MaxAppIdLength = 128;
    public const int MaxAppNameLength = 64;
    public const int MaxAppVersionLength = 32;
    public const int MaxReasonLength = 200;

    private readonly ClientRegistryOptions _options;
    private readonly TimeProvider _time;
    private readonly IAuthAuditLog? _audit;
    private readonly TokenHasher _hasher;
    private readonly SettingsStore<ClientRegistryData> _store;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PendingRequest> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DateTimeOffset>> _attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastUsedWritten = new(StringComparer.Ordinal);
    private readonly ClientRegistryData _data;

    // Decoded digests, parallel to _data.Clients (same order).
    private readonly List<byte[]> _digests = new();

    public ClientRegistry(ClientRegistryOptions options, IHostKeyProtector protector, TimeProvider? time = null,
        IAuthAuditLog? audit = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(protector);
        _options = options;
        _time = time ?? TimeProvider.System;
        _audit = audit;
        Directory.CreateDirectory(options.DataDirectory);

        var key = HostKeyFile.LoadOrCreate(options.DataDirectory, protector, out var keyCreated);
        try
        {
            _hasher = new TokenHasher(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        _store = new SettingsStore<ClientRegistryData>(Path.Combine(options.DataDirectory, FileName),
            AuthJsonContext.Default.ClientRegistryData, SchemaVersion, static () => new ClientRegistryData());
        _data = _store.Load();

        foreach (var client in _data.Clients)
        {
            byte[] digest;
            try
            {
                digest = Convert.FromBase64String(client.TokenHash);
            }
            catch (FormatException)
            {
                digest = [];
            }

            // A record from an older key can never match again; it stays listed so the user can remove it.
            if (keyCreated)
            {
                digest = [];
            }

            _digests.Add(digest);
        }
    }

    public event EventHandler<AccessRequestInfo>? RequestPending;

    public IReadOnlyList<AccessRequestInfo> PendingRequests
    {
        get
        {
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                PruneLocked(now);
                return _requests.Values.Where(r => r.Status == AuthStatuses.Pending).Select(r => r.Info).ToList();
            }
        }
    }

    public Task<AccessRequestInfo> CreateRequestAsync(AuthRequestCreate request, CallerIdentity caller,
        CancellationToken ct) =>
        CreateRequestAsync(request, caller, ClientPrincipal.LocalDevice, ct);

    /// <summary>Creates a pending request for a given device (phase 1: always "local").</summary>
    public Task<AccessRequestInfo> CreateRequestAsync(AuthRequestCreate request, CallerIdentity caller,
        string deviceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        ct.ThrowIfCancellationRequested();
        ValidateCreate(request);

        AccessRequestInfo info;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            PruneLocked(now);

            if (!caller.SameUser)
            {
                Refuse(request.AppId, "other_user");
                throw new ApiException(ErrorCodes.AccessDenied,
                    "Requests from a process running as another Windows user are refused.");
            }

            // An app id belongs to the first program the user approved for it (package, else exe path).
            var key = CallerKey(caller);
            var binding = _data.Bindings.FirstOrDefault(b => b.AppId == request.AppId && b.DeviceId == deviceId);
            if (binding is not null && binding.CallerKey != key)
            {
                Refuse(request.AppId, "app_id_bound_to_other_caller");
                throw new ApiException(ErrorCodes.AccessDenied,
                    "This app id is registered to a different program. The user can remove the app in Portal AI Host.");
            }

            // Pending slot, quota and denial are per (app id, device, calling program): a squatter using the same
            // app id cannot lock out, block or use up the quota of the real app.
            var denial = _data.Denials.FirstOrDefault(d =>
                d.AppId == request.AppId && d.DeviceId == deviceId && d.CallerKey == key && d.DeniedUntil > now);
            if (denial is not null)
            {
                Refuse(request.AppId, "denied_cooldown");
                throw new ApiException(ErrorCodes.AccessDenied,
                    "The user denied access for this app. Ask the user to allow it in Portal AI Host.");
            }

            var open = _requests.Values.Where(r => r.Status == AuthStatuses.Pending).ToList();
            var pending = open.FirstOrDefault(r =>
                r.Info.AppId == request.AppId && r.DeviceId == deviceId && r.CallerKey == key);
            if (pending is not null)
            {
                Refuse(request.AppId, "already_pending");
                throw new ApiException(ErrorCodes.RateLimited,
                    "An access request for this app is already waiting for the user.",
                    retryAfterMs: RetryMs(pending.Info.ExpiresAt - now));
            }

            if (open.Count(r => r.CallerKey == key) >= _options.MaxPendingPerCaller ||
                open.Count >= _options.MaxPendingPerHost)
            {
                Refuse(request.AppId, "too_many_pending");
                throw new ApiException(ErrorCodes.RateLimited,
                    "Too many access requests are waiting for the user.",
                    retryAfterMs: RetryMs(open.Min(r => r.Info.ExpiresAt) - now));
            }

            var attemptKey = request.AppId + "|" + deviceId + "|" + key;
            var attempts = _attempts.TryGetValue(attemptKey, out var list) ? list : _attempts[attemptKey] = new();
            if (attempts.Count >= _options.RequestsPerHourPerApp)
            {
                Refuse(request.AppId, "rate_limited");
                throw new ApiException(ErrorCodes.RateLimited, "Too many access requests from this app.",
                    retryAfterMs: RetryMs(attempts[0] + TimeSpan.FromHours(1) - now));
            }

            attempts.Add(now);
            info = new AccessRequestInfo(
                TokenFormat.NewId("ar_", 16),
                request.AppId,
                request.AppName.Trim(),
                request.AppVersion,
                request.Reason,
                caller,
                now,
                now + _options.RequestLifetime);
            _requests[info.RequestId] = new PendingRequest(info, deviceId, key);
            Audit(AuthAuditKind.RequestCreated, now, info.AppId, info.RequestId, null, null);
        }

        RequestPending?.Invoke(this, info);
        return Task.FromResult(info);
    }

    public async Task<AccessRequestResult?> WaitForRequestAsync(string requestId, TimeSpan wait, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        if (wait < TimeSpan.Zero)
        {
            wait = TimeSpan.Zero;
        }

        if (wait > _options.MaxPollWait)
        {
            wait = _options.MaxPollWait;
        }

        var waitUntil = _time.GetUtcNow() + wait;
        while (true)
        {
            Task signal;
            TimeSpan remaining;
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                PruneLocked(now);
                if (!_requests.TryGetValue(requestId, out var entry))
                {
                    return null;
                }

                if (entry.Status != AuthStatuses.Pending || now >= waitUntil)
                {
                    return ResultLocked(entry, now);
                }

                signal = entry.Changed.Task;
                var untilExpiry = entry.Info.ExpiresAt - now;
                remaining = waitUntil - now < untilExpiry ? waitUntil - now : untilExpiry;
            }

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(remaining, _time, delayCts.Token);
            var finished = await Task.WhenAny(signal, delay).ConfigureAwait(false);
            await delayCts.CancelAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _ = finished;
        }
    }

    public bool IsSameCaller(string requestId, CallerIdentity poller)
    {
        ArgumentNullException.ThrowIfNull(poller);
        lock (_gate)
        {
            if (!_requests.TryGetValue(requestId, out var entry))
            {
                return true;
            }

            return poller.SameUser && CallerKey(entry.Info.Caller) == CallerKey(poller);
        }
    }

    public Task ApproveAsync(string requestId, CancellationToken ct) => DecideAsync(requestId, approve: true);

    public Task DenyAsync(string requestId, CancellationToken ct) => DecideAsync(requestId, approve: false);

    public Task ReAllowAsync(string appId, CancellationToken ct)
    {
        lock (_gate)
        {
            var removed = _data.Denials.RemoveAll(d => d.AppId == appId);
            if (removed > 0)
            {
                SaveLocked();
            }

            foreach (var k in _attempts.Keys.Where(k => k.StartsWith(appId + "|", StringComparison.Ordinal)).ToList())
            {
                _attempts.Remove(k);
            }

            Audit(AuthAuditKind.ReAllowed, _time.GetUtcNow(), appId, null, null, null);
        }

        return Task.CompletedTask;
    }

    public Task<TokenValidation> ValidateAsync(string token, CancellationToken ct)
    {
        const string bearer = "Bearer ";
        var text = token ?? "";
        if (text.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            text = text[bearer.Length..].Trim();
        }

        if (!TokenFormat.IsWellFormed(text))
        {
            return Task.FromResult(Rejected(null, "malformed", ErrorCodes.TokenInvalid));
        }

        var digest = _hasher.Hash(text);
        lock (_gate)
        {
            // Compare against every record; no early exit, so timing does not depend on which one matches.
            var match = -1;
            for (var i = 0; i < _digests.Count; i++)
            {
                var stored = _digests[i];
                var equal = stored.Length == digest.Length && TokenHasher.DigestEquals(stored, digest);
                match = equal ? i : match;
            }

            if (match < 0)
            {
                return Task.FromResult(Rejected(null, "unknown", ErrorCodes.TokenInvalid));
            }

            var client = _data.Clients[match];
            if (client.Revoked)
            {
                return Task.FromResult(Rejected(client.TokenId, "revoked", ErrorCodes.TokenRevoked));
            }

            var now = _time.GetUtcNow();
            client.LastUsedAt = now;
            if (!_lastUsedWritten.TryGetValue(client.TokenId, out var written) ||
                now - written >= _options.LastUsedWriteInterval)
            {
                _lastUsedWritten[client.TokenId] = now;
                SaveLocked();
            }

            return Task.FromResult(new TokenValidation(
                new ClientPrincipal(client.AppId, client.DeviceId, Transport.Loopback, client.TokenId), null));
        }
    }

    public Task<IReadOnlyList<RegisteredClient>> ListAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<RegisteredClient> list = _data.Clients.Select(c => new RegisteredClient(
                c.TokenId, c.AppId, c.AppName, c.PackageFamilyName, c.DeviceId, c.CreatedAt, c.LastUsedAt,
                c.Revoked)).ToList();
            return Task.FromResult(list);
        }
    }

    public Task RevokeAsync(string tokenId, CancellationToken ct)
    {
        lock (_gate)
        {
            var client = _data.Clients.FirstOrDefault(c => c.TokenId == tokenId) ??
                throw new ApiException(ErrorCodes.NotFound, "Unknown token id.");
            RevokeLocked([client], "token");
        }

        return Task.CompletedTask;
    }

    /// <summary>Revokes every token of an app (all devices). Returns how many were active.</summary>
    public int RevokeApp(string appId)
    {
        lock (_gate)
        {
            // Removing an app also frees its app id for whichever program the user approves next.
            _data.Bindings.RemoveAll(b => b.AppId == appId);
            return RevokeLocked(_data.Clients.Where(c => c.AppId == appId).ToList(), "app", forceSave: true);
        }
    }

    /// <summary>Revokes every token of a device (phase 2-3 paired devices). Returns how many were active.</summary>
    public int RevokeDevice(string deviceId)
    {
        lock (_gate)
        {
            _data.Bindings.RemoveAll(b => b.DeviceId == deviceId);
            return RevokeLocked(_data.Clients.Where(c => c.DeviceId == deviceId).ToList(), "device", forceSave: true);
        }
    }

    /// <summary>
    /// Replaces a valid token with a new one: the old token is revoked and the new one returned once.
    /// For a future rotate endpoint or device re-pairing; the caller must pass the token itself as proof.
    /// </summary>
    public async Task<(string Token, string TokenId)> RotateAsync(string currentToken, CancellationToken ct)
    {
        var validation = await ValidateAsync(currentToken, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            throw new ApiException(validation.ErrorCode ?? ErrorCodes.TokenInvalid, "The token is not valid.");
        }

        lock (_gate)
        {
            var old = _data.Clients.First(c => c.TokenId == validation.Principal!.TokenId);
            if (old.Revoked)
            {
                throw new ApiException(ErrorCodes.TokenRevoked, "The token was revoked.");
            }

            var issued = IssueLocked(old.AppId, old.AppName, old.AppVersion, old.DeviceId,
                new CallerIdentity(0, old.PackageFamilyName, old.ExePath, old.CallerVerified, true),
                CallerKeyOf(old), _time.GetUtcNow());
            return issued;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var r in _requests.Values)
            {
                r.Changed.TrySetResult();
            }

            _hasher.Dispose();
        }
    }

    // ---- internals -----------------------------------------------------------------------------

    private Task DecideAsync(string requestId, bool approve)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            PruneLocked(now);
            if (!_requests.TryGetValue(requestId, out var entry))
            {
                throw new ApiException(ErrorCodes.NotFound, "Unknown or expired access request.");
            }

            if (entry.Status != AuthStatuses.Pending)
            {
                // Replayed or late decisions never change a finished request.
                throw new ApiException(ErrorCodes.InvalidRequest, "The access request is no longer pending.");
            }

            if (approve)
            {
                var binding = _data.Bindings.FirstOrDefault(b =>
                    b.AppId == entry.Info.AppId && b.DeviceId == entry.DeviceId);
                if (binding is not null && binding.CallerKey != entry.CallerKey)
                {
                    throw new ApiException(ErrorCodes.AccessDenied,
                        "This app id is registered to a different program; the request cannot be approved.");
                }

                if (binding is null)
                {
                    _data.Bindings.Add(new StoredBinding
                    {
                        AppId = entry.Info.AppId,
                        DeviceId = entry.DeviceId,
                        CallerKey = entry.CallerKey,
                        BoundAt = now,
                    });
                    try
                    {
                        SaveLocked();
                    }
                    catch
                    {
                        _data.Bindings.RemoveAt(_data.Bindings.Count - 1);
                        throw;
                    }
                }

                entry.Status = AuthStatuses.Approved;
                Audit(AuthAuditKind.Approved, now, entry.Info.AppId, requestId, null, null);
            }
            else
            {
                entry.Status = AuthStatuses.Denied;
                entry.FinishedAt = now;
                _data.Denials.RemoveAll(d => d.AppId == entry.Info.AppId && d.DeviceId == entry.DeviceId &&
                    d.CallerKey == entry.CallerKey);
                _data.Denials.Add(new StoredDenial
                {
                    AppId = entry.Info.AppId,
                    DeviceId = entry.DeviceId,
                    CallerKey = entry.CallerKey,
                    DeniedUntil = now + _options.DenyCooldown,
                });
                SaveLocked();
                Audit(AuthAuditKind.Denied, now, entry.Info.AppId, requestId, null, null);
            }

            entry.Signal();
        }

        return Task.CompletedTask;
    }

    private AccessRequestResult ResultLocked(PendingRequest entry, DateTimeOffset now)
    {
        var appId = entry.Info.AppId;
        switch (entry.Status)
        {
            case AuthStatuses.Approved:
                // First collection: create, persist (digest only), then hand out the token once.
                var (token, tokenId) = IssueLocked(appId, entry.Info.AppName, entry.Info.AppVersion, entry.DeviceId,
                    entry.Info.Caller, entry.CallerKey, now);
                entry.Status = AuthStatuses.Claimed;
                entry.TokenId = tokenId;
                entry.FinishedAt = now;
                entry.Signal();
                return new AccessRequestResult(AuthStatuses.Approved, token, tokenId, appId);
            case AuthStatuses.Claimed:
                return new AccessRequestResult(AuthStatuses.Claimed, null, entry.TokenId, appId);
            default:
                return new AccessRequestResult(entry.Status, null, null, appId);
        }
    }

    private (string Token, string TokenId) IssueLocked(string appId, string appName, string? appVersion,
        string deviceId, CallerIdentity caller, string callerKey, DateTimeOffset now)
    {
        var token = TokenFormat.NewToken();
        var digest = _hasher.Hash(token);
        var client = new StoredClient
        {
            TokenId = NewTokenIdLocked(),
            TokenHash = Convert.ToBase64String(digest),
            AppId = appId,
            AppName = appName,
            AppVersion = appVersion,
            PackageFamilyName = caller.PackageFamilyName,
            ExePath = caller.ExePath,
            CallerVerified = caller.Verified,
            DeviceId = deviceId,
            CallerKey = callerKey,
            CreatedAt = now,
        };

        // Rotation: the same program's earlier token for this app and device stops working.
        var previous = _data.Clients.Where(c =>
            c.AppId == appId && c.DeviceId == deviceId && !c.Revoked && CallerKeyOf(c) == callerKey).ToList();
        foreach (var p in previous)
        {
            p.Revoked = true;
            p.RevokedAt = now;
        }

        _data.Clients.Add(client);
        _digests.Add(digest);
        try
        {
            SaveLocked();
        }
        catch
        {
            // Not persisted: undo, so a token that the host would forget is never handed out.
            _data.Clients.RemoveAt(_data.Clients.Count - 1);
            _digests.RemoveAt(_digests.Count - 1);
            foreach (var p in previous)
            {
                p.Revoked = false;
                p.RevokedAt = null;
            }

            throw;
        }

        foreach (var p in previous)
        {
            Audit(AuthAuditKind.TokenRevoked, now, appId, null, p.TokenId, "rotated");
        }

        Audit(AuthAuditKind.TokenIssued, now, appId, null, client.TokenId, null);
        return (token, client.TokenId);
    }

    private string NewTokenIdLocked()
    {
        while (true)
        {
            var id = TokenFormat.NewId("tk_", 8);
            if (!_data.Clients.Any(c => c.TokenId == id))
            {
                return id;
            }
        }
    }

    private int RevokeLocked(List<StoredClient> clients, string scope, bool forceSave = false)
    {
        var now = _time.GetUtcNow();
        var changed = 0;
        foreach (var c in clients.Where(c => !c.Revoked))
        {
            c.Revoked = true;
            c.RevokedAt = now;
            changed++;
            Audit(AuthAuditKind.TokenRevoked, now, c.AppId, null, c.TokenId, scope);
        }

        if (changed > 0 || forceSave)
        {
            SaveLocked();
        }

        return changed;
    }

    private void PruneLocked(DateTimeOffset now)
    {
        List<string>? drop = null;
        foreach (var (id, r) in _requests)
        {
            if (r.Status is AuthStatuses.Pending or AuthStatuses.Approved && now >= r.Info.ExpiresAt)
            {
                // An approval nobody collected in time issues no token.
                r.Status = AuthStatuses.Expired;
                r.FinishedAt = now;
                r.Signal();
                Audit(AuthAuditKind.Expired, now, r.Info.AppId, id, null, null);
            }

            if (r.FinishedAt is { } done && now - done >= _options.FinishedRetention)
            {
                (drop ??= new()).Add(id);
            }
        }

        if (drop is not null)
        {
            foreach (var id in drop)
            {
                _requests.Remove(id);
            }
        }

        _data.Denials.RemoveAll(d => d.DeniedUntil <= now);

        List<string>? stale = null;
        foreach (var (k, times) in _attempts)
        {
            times.RemoveAll(t => now - t >= TimeSpan.FromHours(1));
            if (times.Count == 0)
            {
                (stale ??= new()).Add(k);
            }
        }

        stale?.ForEach(k => _attempts.Remove(k));
    }

    /// <summary>Number of rate-limit buckets kept (stale ones are pruned).</summary>
    internal int TrackedAttemptKeys
    {
        get
        {
            lock (_gate)
            {
                return _attempts.Count;
            }
        }
    }

    /// <summary>Identity of a calling program: package family name, else exe path, else process id.</summary>
    internal static string CallerKey(CallerIdentity caller) =>
        caller.PackageFamilyName is { Length: > 0 } pfn ? "pfn:" + pfn.ToUpperInvariant()
        : caller.ExePath is { Length: > 0 } exe ? "exe:" + exe.ToUpperInvariant()
        : "pid:" + caller.ProcessId.ToString(CultureInfo.InvariantCulture);

    private static string CallerKeyOf(StoredClient client) =>
        client.CallerKey is { Length: > 0 } k
            ? k
            : CallerKey(new CallerIdentity(0, client.PackageFamilyName, client.ExePath, client.CallerVerified, true));

    private void SaveLocked() => _store.Save(_data);

    private TokenValidation Rejected(string? tokenId, string reason, string code)
    {
        lock (_gate)
        {
            Audit(AuthAuditKind.TokenRejected, _time.GetUtcNow(), null, null, tokenId, reason);
        }

        return new TokenValidation(null, code);
    }

    private void Refuse(string appId, string reason) =>
        Audit(AuthAuditKind.RequestRefused, _time.GetUtcNow(), appId, null, null, reason);

    private void Audit(AuthAuditKind kind, DateTimeOffset at, string? appId, string? requestId, string? tokenId,
        string? detail) =>
        _audit?.Record(new AuthAuditEvent(kind, at, appId, requestId, tokenId, detail));

    private static int RetryMs(TimeSpan span) =>
        (int)Math.Clamp(Math.Ceiling(span.TotalMilliseconds), 1000, int.MaxValue);

    private static void ValidateCreate(AuthRequestCreate request)
    {
        if (string.IsNullOrEmpty(request.AppId) || request.AppId.Length > MaxAppIdLength ||
            !AppIdPattern().IsMatch(request.AppId))
        {
            throw new ApiException(ErrorCodes.InvalidRequest,
                "app_id must be a stable lowercase reverse-DNS name, for example com.example.app.", "app_id");
        }

        if (string.IsNullOrWhiteSpace(request.AppName) || request.AppName.Length > MaxAppNameLength ||
            !TextRules.IsDisplaySafe(request.AppName))
        {
            throw new ApiException(ErrorCodes.InvalidRequest,
                $"app_name is required, at most {MaxAppNameLength} characters, with no control characters.",
                "app_name");
        }

        if (request.AppVersion is { } v && (v.Length > MaxAppVersionLength || !TextRules.IsDisplaySafe(v)))
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "app_version is too long or has control characters.",
                "app_version");
        }

        if (request.Reason is { } r && (r.Length > MaxReasonLength || !TextRules.IsDisplaySafe(r)))
        {
            throw new ApiException(ErrorCodes.InvalidRequest,
                $"reason is at most {MaxReasonLength} characters, with no control characters.", "reason");
        }
    }

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$", RegexOptions.CultureInvariant)]
    private static partial Regex AppIdPattern();

    private sealed class PendingRequest(AccessRequestInfo info, string deviceId, string callerKey)
    {
        public AccessRequestInfo Info { get; } = info;
        public string DeviceId { get; } = deviceId;
        public string CallerKey { get; } = callerKey;
        public string Status { get; set; } = AuthStatuses.Pending;
        public string? TokenId { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }

        public TaskCompletionSource Changed { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Signal()
        {
            var old = Changed;
            Changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            old.TrySetResult();
        }
    }
}
