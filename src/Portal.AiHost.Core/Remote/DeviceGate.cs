using System.Globalization;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;

namespace Portal.AiHost.Core.Remote;

/// <summary>Detects that a full-screen game or heavy GPU use keeps this PC busy. Injectable so tests can fake it.</summary>
public interface IBusyDetector
{
    bool IsBusy();
}

/// <summary>The detector used when none is given: the PC is never busy.</summary>
public sealed class NeverBusyDetector : IBusyDetector
{
    public bool IsBusy() => false;
}

/// <summary>Why a device call is refused: an error code of the catalogue, a plain message and when to try again.</summary>
public sealed record DeviceRefusal(string Code, string Message, int RetryAfterMs);

/// <summary>Quiet hours: a local time window that may cross midnight.</summary>
public static class QuietHours
{
    /// <summary>"HH:mm" (24 hour) to minutes after midnight; null for anything else.</summary>
    public static int? Parse(string? text)
    {
        if (text is null || !TimeOnly.TryParseExact(text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        {
            return null;
        }

        return t.Hour * 60 + t.Minute;
    }

    /// <summary>
    /// Minutes until the window ends when <paramref name="now"/> (minutes after midnight) is inside it, else null.
    /// Start inclusive, end exclusive. Equal start and end mean no window.
    /// </summary>
    public static int? MinutesLeft(int start, int end, int now)
    {
        if (start == end)
        {
            return null;
        }

        var inside = start < end ? now >= start && now < end : now >= start || now < end;
        if (!inside)
        {
            return null;
        }

        var left = end - now;
        return left > 0 ? left : left + 24 * 60;
    }
}

/// <summary>
/// Decides whether a device call is let in (docs/client-mode-v1.md 5.7) and keeps what is running now. The label of
/// the calling app only picks the per-app pause and the stats row; it never grants or widens access.
/// Order of the checks: pause (device, then app), quiet hours, gaming, then the daily limits.
/// </summary>
public sealed class DeviceGate
{
    /// <summary>A device that came in through the relay counts as "from away" again after this long without a relay call.</summary>
    private static readonly TimeSpan RelayAlertGap = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(10);

    private readonly DeviceStore _devices;
    private readonly DeviceStatsStore _stats;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private readonly Func<bool> _gamingMode;
    private readonly IBusyDetector _busy;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<ActiveCall>> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastRelay = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Route, DateTimeOffset At)> _lastRoute = new(StringComparer.Ordinal);

    public DeviceGate(DeviceStore devices, DeviceStatsStore stats, TimeProvider? time = null, TimeZoneInfo? zone = null,
        Func<bool>? gamingMode = null, IBusyDetector? busy = null)
    {
        _devices = devices;
        _stats = stats;
        _time = time ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
        _gamingMode = gamingMode ?? (() => false);
        _busy = busy ?? new NeverBusyDetector();
        devices.Added += d => Alert?.Invoke(new DeviceAlert { Kind = "paired", DeviceId = d.Id, DeviceName = d.Name });
    }

    /// <summary>Raised for a new pairing and for a device that connects through the relay from away (the tray notifies).</summary>
    public event Action<DeviceAlert>? Alert;

    /// <summary>Raised when what runs now changed (the Devices view refreshes).</summary>
    public event Action? ActivityChanged;

    internal sealed class ActiveCall(string app, long sequence)
    {
        public string App { get; } = app;
        public long Sequence { get; } = sequence;
    }

    private long _sequence;

    /// <summary>True while gaming mode is on and the detector sees a game or heavy GPU use.</summary>
    public bool GamingBusyNow => _gamingMode() && _busy.IsBusy();

    /// <summary>Notes a call arriving over the relay or the home network. Raises the "from away" alert on a new relay session.</summary>
    public void NoteArrival(PairedDevice device, bool viaRelay)
    {
        var now = _time.GetUtcNow();
        var raise = false;
        lock (_gate)
        {
            _lastRoute[device.Id] = (viaRelay ? "relay" : "lan", now);
            if (viaRelay)
            {
                raise = !_lastRelay.TryGetValue(device.Id, out var last) || now - last >= RelayAlertGap;
                _lastRelay[device.Id] = now;
            }
        }

        if (raise)
        {
            Alert?.Invoke(new DeviceAlert { Kind = "relay_connect", DeviceId = device.Id, DeviceName = device.Name });
        }
    }

    /// <summary>Why the call is refused, or null when it may go on.</summary>
    public DeviceRefusal? Check(PairedDevice device, string app)
    {
        if (device.Paused || device.PausedApps.Contains(app, StringComparer.Ordinal))
        {
            return new DeviceRefusal(ErrorCodes.DevicePaused,
                device.Paused ? "This device is paused on the PC." : "This app is paused on the PC.", 60_000);
        }

        var local = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _zone);
        if (QuietHours.Parse(device.QuietStart) is { } start && QuietHours.Parse(device.QuietEnd) is { } end &&
            QuietHours.MinutesLeft(start, end, local.Hour * 60 + local.Minute) is { } left)
        {
            return new DeviceRefusal(ErrorCodes.DeviceQuietHours, "This device is in its quiet hours on the PC.",
                (int)Math.Max(1000, left * 60_000L - local.Second * 1000L));
        }

        if (GamingBusyNow)
        {
            return new DeviceRefusal(ErrorCodes.HostBusyGaming, "The PC is busy with a game right now.", 30_000);
        }

        var (requests, tokens) = _stats.TodayTotal(device.Id);
        if (device.MaxRequestsPerDay is { } maxRequests && requests >= maxRequests ||
            device.MaxTokensPerDay is { } maxTokens && tokens >= maxTokens)
        {
            var untilMidnight = local.Date.AddDays(1) - local.DateTime;
            return new DeviceRefusal(ErrorCodes.DeviceLimitReached, "This device used up today's limit on the PC.",
                (int)Math.Clamp(untilMidnight.TotalMilliseconds, 1000, int.MaxValue));
        }

        return null;
    }

    /// <summary>Marks a call as running. Dispose the result when it ends; call <see cref="DeviceCall.Complete"/> first to count tokens.</summary>
    public DeviceCall Begin(PairedDevice device, string app, bool viaRelay, bool counts)
    {
        ActiveCall call;
        lock (_gate)
        {
            if (!_active.TryGetValue(device.Id, out var list))
            {
                _active[device.Id] = list = [];
            }

            list.Add(call = new ActiveCall(app, ++_sequence));
        }

        if (counts)
        {
            _stats.BeginRequest(device.Id, app);
        }

        ActivityChanged?.Invoke();
        return new DeviceCall(this, device.Id, app, viaRelay ? "relay" : "lan", call, counts, _time.GetTimestamp());
    }

    /// <summary>The apps running now with their queue position (the oldest call of a device runs, later ones wait).</summary>
    public IReadOnlyList<(string App, int Active, int Queued)> ActiveApps(string deviceId)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(deviceId, out var list) || list.Count == 0)
            {
                return [];
            }

            var oldest = list.Min(c => c.Sequence);
            return [.. list.GroupBy(c => c.App, StringComparer.Ordinal).Select(g =>
                (g.Key, g.Count(), g.Count(c => c.Sequence != oldest)))];
        }
    }

    public string StatusOf(PairedDevice device)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(device.Id, out var list) && list.Count > 0)
            {
                return DeviceStatuses.Online;
            }
        }

        var seen = device.LastSeenAt;
        return seen is { } at && _time.GetUtcNow() - at < IdleFor ? DeviceStatuses.Idle : DeviceStatuses.Offline;
    }

    public string? RouteOf(string deviceId)
    {
        lock (_gate)
        {
            return _lastRoute.TryGetValue(deviceId, out var r) ? r.Route : null;
        }
    }

    /// <summary>The summaries the Devices view shows, one per paired device.</summary>
    public List<DeviceAppSummary> AppsOf(PairedDevice device)
    {
        var view = _stats.View(device.Id);
        var running = ActiveApps(device.Id).ToDictionary(a => a.App, StringComparer.Ordinal);
        var names = new SortedSet<string>(view.Last7Days.Keys, StringComparer.Ordinal);
        names.UnionWith(running.Keys);
        names.UnionWith(device.PausedApps);
        return [.. names.Select(name =>
        {
            view.Today.TryGetValue(name, out var today);
            view.Last7Days.TryGetValue(name, out var bars);
            running.TryGetValue(name, out var run);
            return new DeviceAppSummary
            {
                Name = name,
                Paused = device.Paused || device.PausedApps.Contains(name, StringComparer.Ordinal),
                Active = run.Active,
                Queued = run.Queued,
                RequestsToday = today.Requests,
                TokensToday = today.Tokens,
                Last7Days = [.. bars ?? new int[DeviceStatsStore.DaysKept]],
            };
        })];
    }

    public DeviceStatsStore Stats => _stats;

    /// <summary>The device as it is saved right now, or null when it is no longer paired.</summary>
    public PairedDevice? Find(string id) => _devices.Find(id);

    internal void Finish(string deviceId, ActiveCall call)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(deviceId, out var list))
            {
                list.Remove(call);
                if (list.Count == 0)
                {
                    _active.Remove(deviceId);
                }
            }
        }

        ActivityChanged?.Invoke();
    }

    /// <summary>One admitted call. <see cref="Complete"/> saves its counts; disposing frees its place in the queue view.</summary>
    public sealed class DeviceCall : IDisposable
    {
        private readonly DeviceGate _gate;
        private readonly string _deviceId;
        private readonly string _app;
        private readonly string _route;
        private readonly ActiveCall _call;
        private readonly bool _counts;
        private readonly long _started;
        private int _done;

        internal DeviceCall(DeviceGate gate, string deviceId, string app, string route, ActiveCall call, bool counts, long started)
        {
            _gate = gate;
            _deviceId = deviceId;
            _app = app;
            _route = route;
            _call = call;
            _counts = counts;
            _started = started;
        }

        /// <summary>Saves tokens, route, model and speed. <paramref name="firstByte"/> is the timestamp of the first byte sent, if any.</summary>
        public void Complete(long tokens, long? firstByte, string? model, string? modelName)
        {
            if (!_counts)
            {
                return;
            }

            var time = _gate._time;
            var end = time.GetTimestamp();
            int? latency = firstByte is { } f ? (int)time.GetElapsedTime(_started, f).TotalMilliseconds : null;
            double? tps = null;
            if (tokens > 0 && firstByte is { } start)
            {
                var seconds = time.GetElapsedTime(start, end).TotalSeconds;
                if (seconds > 0.05)
                {
                    tps = Math.Round(tokens / seconds, 1);
                }
            }

            _gate._stats.CompleteRequest(_deviceId, _app, tokens, _route, model, modelName, tps, latency);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                _gate.Finish(_deviceId, _call);
            }
        }
    }
}
