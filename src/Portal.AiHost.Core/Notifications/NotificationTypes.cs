namespace Portal.AiHost.Core.Notifications;

/// <summary>One notification to show: what it says and the window page a click opens.</summary>
/// <param name="Kind">One of <c>NotificationKinds</c>.</param>
/// <param name="Title">Short headline.</param>
/// <param name="Body">One plain sentence.</param>
/// <param name="Page">Window page tag such as "models", "approvals" or "home".</param>
public sealed record ToastRequest(string Kind, string Title, string Body, string Page);

/// <summary>Shows a Windows notification. The host's implementation uses the tray icon; tests use a fake.</summary>
public interface IToastNotifier
{
    /// <summary>Shows the notification. Returns false when it could not be shown (for example no tray icon).</summary>
    bool Show(ToastRequest request);
}

/// <summary>What the centre did with a request.</summary>
public enum NotifyOutcome
{
    Shown,

    /// <summary>The user turned this kind off.</summary>
    Muted,

    /// <summary>The same kind was shown a moment ago.</summary>
    RateLimited,

    /// <summary>A full-screen program is in front; the notification was dropped.</summary>
    DroppedFullscreen,

    /// <summary>Held back (approvals only) and shown later by <see cref="NotificationCenter.Pump"/>.</summary>
    Deferred,

    /// <summary>The notifier could not show it.</summary>
    Failed,

    /// <summary>The Portal AI Host window is open, so a notification would only repeat what it shows.</summary>
    WindowOpen,

    /// <summary>Nothing was waiting.</summary>
    Idle,
}

/// <summary>A notifier that hands each request to a function (the host wires the tray icon in later).</summary>
public sealed class DelegateNotifier(Func<ToastRequest, bool> show) : IToastNotifier
{
    public bool Show(ToastRequest request) => show(request);
}
