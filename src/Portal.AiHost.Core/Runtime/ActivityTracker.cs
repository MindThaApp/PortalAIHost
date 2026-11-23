using Portal.AiHost.Contracts.Control;

namespace Portal.AiHost.Core.Runtime;

/// <summary>
/// Per-app counters for today, in memory only: requests, last seen and tokens, plus which apps run now.
/// Counts and timing only, never content. Reset at local midnight; never saved.
/// </summary>
public sealed class ActivityTracker
{
    private sealed class Counts
    {
        public int Requests;
        public long Tokens;
        public DateTimeOffset LastSeen;
        public int Running;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Counts> _apps = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private DateOnly _day;
    private double? _lastTokensPerSecond;

    public ActivityTracker(TimeProvider? time = null, TimeZoneInfo? zone = null)
    {
        _time = time ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
        _day = Today();
    }

    /// <summary>Tokens per second of the last finished generation, or null when none yet.</summary>
    public double? LastTokensPerSecond
    {
        get
        {
            lock (_gate)
            {
                return _lastTokensPerSecond;
            }
        }
    }

    /// <summary>Counts a request. The app counts as running only between StartGeneration and dispose.</summary>
    public RequestScope Begin(string appId)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            RollDay();
            var e = Get(appId);
            e.Requests++;
            e.LastSeen = now;
        }

        return new RequestScope(this, appId);
    }

    /// <summary>Today's apps, most recently seen first. <paramref name="names"/> maps app ids to display names.</summary>
    public List<AppActivity> Snapshot(Func<string, string?>? names = null)
    {
        lock (_gate)
        {
            RollDay();
            return _apps.OrderByDescending(p => p.Value.LastSeen).Select(p => new AppActivity
            {
                AppId = p.Key,
                AppName = names?.Invoke(p.Key) ?? p.Key,
                Requests = p.Value.Requests,
                LastSeen = p.Value.LastSeen,
                Tokens = p.Value.Tokens,
                Running = p.Value.Running,
            }).ToList();
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _zone).DateTime);

    private void RollDay()
    {
        var today = Today();
        if (today == _day)
        {
            return;
        }

        _day = today;
        foreach (var key in _apps.Where(p => p.Value.Running == 0).Select(p => p.Key).ToList())
        {
            _apps.Remove(key);
        }

        foreach (var e in _apps.Values)
        {
            e.Requests = 0;
            e.Tokens = 0;
        }
    }

    private Counts Get(string appId)
    {
        if (!_apps.TryGetValue(appId, out var e))
        {
            _apps[appId] = e = new Counts();
        }

        return e;
    }

    private void Generating(string appId, int delta)
    {
        lock (_gate)
        {
            var e = Get(appId);
            e.Running = Math.Max(0, e.Running + delta);
        }
    }

    private void End(string appId, long tokens, double? tokensPerSecond, bool wasRunning)
    {
        lock (_gate)
        {
            RollDay();
            var e = Get(appId);
            if (wasRunning) e.Running = Math.Max(0, e.Running - 1);
            e.Tokens += tokens;
            e.LastSeen = _time.GetUtcNow();
            if (tokensPerSecond is { } v)
            {
                _lastTokensPerSecond = v;
            }
        }
    }

    /// <summary>One request in flight. Mark the start of generation, report tokens, dispose when done.</summary>
    public sealed class RequestScope : IDisposable
    {
        private readonly ActivityTracker _owner;
        private readonly string _appId;
        private DateTimeOffset? _generationStart;
        private long _tokens;
        private int _ended;
        private int _started;

        internal RequestScope(ActivityTracker owner, string appId)
        {
            _owner = owner;
            _appId = appId;
        }

        /// <summary>Generation begins now (after the queue wait and any model load).</summary>
        public void StartGeneration()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                _generationStart = _owner._time.GetUtcNow();
                _owner.Generating(_appId, +1);
            }
        }

        /// <summary>Adds output tokens.</summary>
        public void AddTokens(long count) => Interlocked.Add(ref _tokens, Math.Max(0, count));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0)
            {
                return;
            }

            var tokens = Interlocked.Read(ref _tokens);
            double? rate = null;
            if (_generationStart is { } started && tokens > 0)
            {
                var seconds = (_owner._time.GetUtcNow() - started).TotalSeconds;
                if (seconds > 0.05)
                {
                    rate = Math.Round(tokens / seconds, 1);
                }
            }

            _owner.End(_appId, tokens, rate, Volatile.Read(ref _started) != 0);
        }
    }
}
