using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;

namespace Portal.AiHost.Core.Notifications;

/// <summary>
/// Decides whether a notification may be shown. Quiet by default: a kind the user turned off is never shown, the
/// same kind is shown at most once per <see cref="MinGap"/>, and while a full-screen program is in front nothing
/// is shown. Only approval requests are not dropped then: they wait (several become one) and show once the
/// full screen ends and the rate limit allows. Nothing is shown while the window is open.
/// </summary>
public sealed class NotificationCenter
{
    public static readonly TimeSpan DefaultMinGap = TimeSpan.FromMinutes(3);

    private readonly IToastNotifier _notifier;
    private readonly Func<HostSettings> _settings;
    private readonly Func<bool> _isFullscreen;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<bool> _windowOpen;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastShown = new(StringComparer.Ordinal);
    private readonly List<ToastRequest> _waiting = [];

    public NotificationCenter(IToastNotifier notifier, Func<HostSettings> settings, Func<bool> isFullscreen,
        Func<DateTimeOffset>? now = null, TimeSpan? minGap = null, Func<bool>? windowOpen = null)
    {
        _notifier = notifier;
        _settings = settings;
        _isFullscreen = isFullscreen;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _windowOpen = windowOpen ?? (() => false);
        MinGap = minGap ?? DefaultMinGap;
    }

    public TimeSpan MinGap { get; }

    /// <summary>Approval requests held back at the moment.</summary>
    public int WaitingCount
    {
        get
        {
            lock (_gate)
            {
                return _waiting.Count;
            }
        }
    }

    public static bool IsEnabled(HostSettings settings, string kind) =>
        settings.MutedNotifications?.Contains(kind, StringComparer.Ordinal) != true;

    public NotifyOutcome Raise(ToastRequest request)
    {
        lock (_gate)
        {
            if (!IsEnabled(_settings(), request.Kind))
            {
                return NotifyOutcome.Muted;
            }

            if (_windowOpen())
            {
                return NotifyOutcome.WindowOpen; // The person is looking at the window; it shows everything live.
            }

            var holdable = request.Kind == NotificationKinds.Approvals;
            if (Fullscreen())
            {
                if (!holdable)
                {
                    return NotifyOutcome.DroppedFullscreen;
                }

                Hold(request);
                return NotifyOutcome.Deferred;
            }

            if (LimitedLocked(request.Kind))
            {
                if (!holdable)
                {
                    return NotifyOutcome.RateLimited;
                }

                Hold(request);
                return NotifyOutcome.Deferred;
            }

            return ShowLocked(request);
        }
    }

    /// <summary>Shows held-back approval requests when nothing blocks them. Call every few seconds.</summary>
    public NotifyOutcome Pump()
    {
        lock (_gate)
        {
            if (_waiting.Count == 0)
            {
                return NotifyOutcome.Idle;
            }

            if (_windowOpen())
            {
                _waiting.Clear(); // The open window already shows the requests.
                return NotifyOutcome.WindowOpen;
            }

            if (!IsEnabled(_settings(), NotificationKinds.Approvals))
            {
                _waiting.Clear();
                return NotifyOutcome.Muted;
            }

            if (Fullscreen())
            {
                return NotifyOutcome.Deferred;
            }

            if (LimitedLocked(NotificationKinds.Approvals))
            {
                return NotifyOutcome.RateLimited;
            }

            var request = _waiting.Count == 1
                ? _waiting[0]
                : new ToastRequest(NotificationKinds.Approvals, NotificationTexts.ApprovalsManyTitle,
                    NotificationTexts.ApprovalsManyBody(_waiting.Count), "approvals");
            var outcome = ShowLocked(request);
            if (outcome == NotifyOutcome.Shown)
            {
                _waiting.Clear();
            }

            return outcome;
        }
    }

    private bool Fullscreen()
    {
        try
        {
            return _isFullscreen();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false; // A failed check must not hide a notification for good.
        }
    }

    private void Hold(ToastRequest request)
    {
        if (_waiting.Count < 16)
        {
            _waiting.Add(request);
        }
    }

    private bool LimitedLocked(string kind) =>
        _lastShown.TryGetValue(kind, out var at) && _now() - at < MinGap;

    private NotifyOutcome ShowLocked(ToastRequest request)
    {
        bool shown;
        try
        {
            shown = _notifier.Show(request);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            shown = false;
        }

        if (!shown)
        {
            return NotifyOutcome.Failed;
        }

        _lastShown[request.Kind] = _now();
        return NotifyOutcome.Shown;
    }
}
