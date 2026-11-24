using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Core.Notifications;

/// <summary>The English words of the notifications. Plain, short, no technical terms.</summary>
public static class NotificationTexts
{
    public const string AppName = "Portal AI Host";

    public static string DownloadDoneTitle => "Model ready";
    public static string DownloadDoneBody(string model) => $"{model} finished downloading and is ready to use.";
    public static string DownloadFailedTitle => "Download did not finish";
    public static string DownloadFailedBody(string model, string reason) => $"{model} could not be downloaded. {reason}";

    public static string ApprovalTitle => "An app is asking for access";
    public static string ApprovalBody(string app) => $"{app} wants to use your AI models. Open Portal AI Host to allow or refuse.";
    public static string ApprovalsManyTitle => "Apps are asking for access";
    public static string ApprovalsManyBody(int count) => $"{count} apps want to use your AI models. Open Portal AI Host to decide.";

    public static string EngineTitle => "The AI engine stopped";
    public static string EngineBody => "It stopped unexpectedly and could not start again. Open Portal AI Host to see what to do.";
    public static string HealthTitle => "Portal AI Host needs attention";

    /// <summary>Turns a technical download error into one plain sentence. The raw text is never shown.</summary>
    public static string PlainDownloadReason(string? raw)
    {
        var t = raw ?? "";
        if (Has(t, "space", "disk full", "no room"))
        {
            return "There was not enough free space on the drive.";
        }

        if (Has(t, "network", "connect", "timed out", "timeout", "resolve", "host", "socket", "internet"))
        {
            return "The connection was lost. Check your internet and try again.";
        }

        if (Has(t, "hash", "checksum", "verify", "mismatch", "match", "size"))
        {
            return "The downloaded file was not complete or not correct. Try again.";
        }

        return "Something went wrong. Open Portal AI Host to try again.";
    }

    private static bool Has(string text, params string[] words) =>
        words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Turns host events into notifications for the <see cref="NotificationCenter"/>. Only events a person would want
/// to hear about while the window is closed become notifications; everything routine is ignored.
/// </summary>
public sealed class NotificationEvents(NotificationCenter center)
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _wasReady = new(StringComparer.Ordinal);
    private string? _lastHealth;

    /// <summary>A model download ended. Finished and failed notify; progress, pause and cancel do not.</summary>
    public NotifyOutcome OnDownload(DownloadProgressEvent e, string? displayName)
    {
        var model = string.IsNullOrWhiteSpace(displayName) ? "The model" : displayName;
        return e.State switch
        {
            "done" => center.Raise(new ToastRequest(NotificationKinds.Downloads,
                NotificationTexts.DownloadDoneTitle, NotificationTexts.DownloadDoneBody(model), "models")),
            "failed" => center.Raise(new ToastRequest(NotificationKinds.Downloads,
                NotificationTexts.DownloadFailedTitle,
                NotificationTexts.DownloadFailedBody(model, NotificationTexts.PlainDownloadReason(e.Message)), "models")),
            _ => NotifyOutcome.Idle,
        };
    }

    /// <summary>A new app asked for approval.</summary>
    public NotifyOutcome OnApproval(string appName)
    {
        var app = string.IsNullOrWhiteSpace(appName) ? "An app" : appName;
        return center.Raise(new ToastRequest(NotificationKinds.Approvals,
            NotificationTexts.ApprovalTitle, NotificationTexts.ApprovalBody(app), "approvals"));
    }

    /// <summary>
    /// An engine changed state. Only "failed after it had been running" notifies (the engine crashed and gave up
    /// restarting); a failed start the user asked for, a stop and an idle unload do not.
    /// </summary>
    public NotifyOutcome OnEngineState(EngineStateChange change)
    {
        lock (_gate)
        {
            switch (change.State)
            {
                case EngineState.Ready:
                    _wasReady.Add(change.Kind);
                    return NotifyOutcome.Idle;
                case EngineState.Stopping or EngineState.Stopped:
                    _wasReady.Remove(change.Kind);
                    return NotifyOutcome.Idle;
                case EngineState.Failed:
                    if (!_wasReady.Remove(change.Kind))
                    {
                        return NotifyOutcome.Idle;
                    }

                    break;
                default:
                    return NotifyOutcome.Idle;
            }
        }

        return center.Raise(new ToastRequest(NotificationKinds.Problems,
            NotificationTexts.EngineTitle, NotificationTexts.EngineBody, "home"));
    }

    /// <summary>
    /// A health check ran. Notifies only when the overall status turns to fail from something else. The first
    /// report only sets the starting point, so a problem that was already there at start is left to the window.
    /// </summary>
    public NotifyOutcome OnHealth(HealthReport report)
    {
        string? previous;
        lock (_gate)
        {
            previous = _lastHealth;
            _lastHealth = report.Status;
        }

        if (previous is null || previous == HealthStatuses.Fail || report.Status != HealthStatuses.Fail)
        {
            return NotifyOutcome.Idle;
        }

        var first = report.Checks.FirstOrDefault(c => c.Status == HealthStatuses.Fail);
        var body = first is null ? "Open Portal AI Host to see what to fix."
            : string.IsNullOrWhiteSpace(first.Detail) ? first.Title : $"{first.Title}. {first.Detail}";
        return center.Raise(new ToastRequest(NotificationKinds.Problems, NotificationTexts.HealthTitle, body, "home"));
    }
}
