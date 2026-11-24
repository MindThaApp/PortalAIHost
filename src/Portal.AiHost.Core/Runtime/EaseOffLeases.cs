namespace Portal.AiHost.Core.Runtime;

/// <summary>
/// The ease-off requests apps have made (POST /host/ease-off). One lease per app: a new request from the same app
/// replaces its earlier one (that is how it is renewed), a DELETE ends only the caller's own, and a lease that is
/// not renewed ends on its own when its time to live passes. Memory only; nothing is saved.
/// </summary>
public sealed class EaseOffLeases(TimeProvider time)
{
    public const int MinTtlMs = 1000;
    public const int MaxTtlMs = 120_000;
    public const int MaxReasonLength = 40;
    public const int MaxLeases = 64;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);

    private sealed record Lease(string Reason, DateTimeOffset Expires, long ReservedBytes);

    /// <summary>Clamps a time to live into the allowed range.</summary>
    public static int ClampTtl(int ttlMs) => Math.Clamp(ttlMs, MinTtlMs, MaxTtlMs);

    /// <summary>A reason made of letters, digits, underscore and dash only, so it is safe to log and report.</summary>
    public static string CleanReason(string? reason)
    {
        var chars = (reason ?? "").Trim().Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            .Take(MaxReasonLength).ToArray();
        return chars.Length == 0 ? "busy" : new string(chars);
    }

    /// <summary>Starts or renews the app's lease. Returns false when too many apps hold one already.</summary>
    public bool Set(string appId, string? reason, int ttlMs, long? reservedBytes)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            Prune(now);
            if (!_leases.ContainsKey(appId) && _leases.Count >= MaxLeases)
            {
                return false;
            }

            _leases[appId] = new Lease(CleanReason(reason), now.AddMilliseconds(ClampTtl(ttlMs)), Math.Max(0, reservedBytes ?? 0));
            return true;
        }
    }

    /// <summary>Ends the app's own lease. Returns false when it had none.</summary>
    public bool Clear(string appId)
    {
        lock (_lock)
        {
            return _leases.Remove(appId);
        }
    }

    /// <summary>True while any lease has not expired.</summary>
    public bool Any
    {
        get
        {
            lock (_lock)
            {
                Prune(time.GetUtcNow());
                return _leases.Count > 0;
            }
        }
    }

    /// <summary>Memory the apps with a live lease say their own models hold.</summary>
    public long ReservedBytes
    {
        get
        {
            lock (_lock)
            {
                Prune(time.GetUtcNow());
                return _leases.Values.Sum(l => l.ReservedBytes);
            }
        }
    }

    /// <summary>The soonest moment a lease ends, or null when there are none (so a caller knows when to look again).</summary>
    public DateTimeOffset? NextExpiry
    {
        get
        {
            lock (_lock)
            {
                Prune(time.GetUtcNow());
                return _leases.Count == 0 ? null : _leases.Values.Min(l => l.Expires);
            }
        }
    }

    private void Prune(DateTimeOffset now)
    {
        List<string>? gone = null;
        foreach (var (app, lease) in _leases)
        {
            if (lease.Expires <= now)
            {
                (gone ??= []).Add(app);
            }
        }

        if (gone is not null)
        {
            foreach (var app in gone)
            {
                _leases.Remove(app);
            }
        }
    }
}
